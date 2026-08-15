using System.Collections.ObjectModel;
using Avalonia;
using Tec.App.Services;
using Tec.Core.Benches;
using Tec.Driver.Abi;
using Tec.DriverHost;

using Point = Avalonia.Point;
using BPoint = Tec.Core.Benches.Point;

namespace Tec.App.ViewModels;

/// <summary>设备库的一项。不可用的驱动也要列出来并说明原因，不能悄悄消失（§3.5）。</summary>
public sealed class LibraryItemViewModel
{
    public LibraryItemViewModel(DriverPackage pkg) => Package = pkg;
    public DriverPackage Package { get; }
    public string Name => Package.Display;
    public string Vendor => Package.Manifest.Vendor;
    public string Sub => Package.Driver?.Info.Description ?? Package.Manifest.Description ?? "";
    public string ArtKey => Package.Driver?.Info.IconKey ?? Package.Manifest.Icon ?? "reactor2";
    public bool Usable => Package.Usable;
    public string Problem => Package.Problem ?? "";
    public bool HasProblem => !string.IsNullOrEmpty(Package.Problem);
}

public sealed class DeviceNodeViewModel : ViewModelBase
{
    public DeviceNodeViewModel(DeviceInstance dev, IDeviceDriver? driver, IReadOnlyList<int> channels)
    {
        Device = dev;
        Driver = driver;
        Channels = channels;
    }

    public DeviceInstance Device { get; }
    public IDeviceDriver? Driver { get; }
    public IReadOnlyList<int> Channels { get; }

    public string Id => Device.InstanceId;
    public string Title => Device.Display;
    public string ArtKey => Driver?.Info.IconKey ?? "reactor2";
    public double X => Device.Position.X;
    public double Y => Device.Position.Y;
    public double Width => ArtKey == "reactor2" ? 176 : 120;
    /// <summary>按设备图的宽高比算出来的显示高度，停靠计算要用。</summary>
    public double Height
    {
        get
        {
            var art = Controls.DeviceArtCache.Get(ArtKey);
            return art is null ? Width * 0.8 : Width * art.ViewHeight / art.ViewWidth;
        }
    }

    public void MoveTo(Point p)
    {
        Device.Position = new BPoint(p.X, p.Y);
        RaiseAll(nameof(X), nameof(Y));
    }
    public string ChannelText => Channels.Count == 0 ? "未绑定" : string.Join(" · ", Channels.Select(c => "CH" + c));

    /// <summary>改名后台面上的标签要跟着变。</summary>
    public void NameChanged() => Raise(nameof(Title));

    private bool _sel;
    /// <summary>选中的设备在画布上描一圈框。</summary>
    public bool IsSelected { get => _sel; set => Set(ref _sel, value); }
}

/// <summary>
/// 台面。左边设备库、中间画布、右边属性面板（通信配置 + 设备配置，全部按 schema 渲染）。
/// </summary>
public sealed class BenchViewModel : ViewModelBase
{
    private readonly Workspace _ws;
    private DeviceNodeViewModel? _selected;
    private SchemaFormViewModel? _connectionForm;
    private SchemaFormViewModel? _configForm;
    private string _probeResult = "";
    private bool _renaming;
    private bool _advOpen;
    private bool _wellsOpen = true;
    private bool _chOpen = true;
    private LibraryItemViewModel? _picked;

    public BenchViewModel(Workspace ws)
    {
        _ws = ws;
        Probe = new RelayCommand(async () => await ProbeAsync());
        Rebuild = new RelayCommand(async () => await _ws.RebuildChannelsAsync());
        ToggleRename = new RelayCommand(() => Renaming = !Renaming);
        ToggleAdvanced = new RelayCommand(() => AdvancedOpen = !AdvancedOpen);
        ToggleWells = new RelayCommand(() => WellsOpen = !WellsOpen);
        ToggleChTable = new RelayCommand(() => ChTableOpen = !ChTableOpen);
        DeleteSelected = new RelayCommand(Delete);
        ZoomIn = new RelayCommand(() => Scale(1.25));
        ZoomOut = new RelayCommand(() => Scale(1 / 1.25));
        FitAll = new RelayCommand(Fit);
        ToggleGrid = new RelayCommand(() => ShowGrid = !ShowGrid);
        ws.BenchChanged += (_, _) => Reload();
        Reload();
    }

    public ObservableCollection<LibraryItemViewModel> Library { get; } = new();
    public ObservableCollection<DeviceNodeViewModel> Devices { get; } = new();
    public ObservableCollection<ChannelRowViewModel> ChannelRows { get; } = new();

    public RelayCommand Probe { get; }
    public RelayCommand Rebuild { get; }
    public RelayCommand ToggleRename { get; }
    public RelayCommand ToggleAdvanced { get; }
    public RelayCommand ToggleWells { get; }
    public RelayCommand ToggleChTable { get; }
    public RelayCommand DeleteSelected { get; }
    public RelayCommand ZoomIn { get; }
    public RelayCommand ZoomOut { get; }
    public RelayCommand FitAll { get; }
    public RelayCommand ToggleGrid { get; }

    // ── 视图变换（左下角四个工具）──────────────────────────────────
    private double _zoom = 1, _panX, _panY;
    private bool _grid = true;
    private Size _stage = new(900, 700);

    public double Zoom
    {
        get => _zoom;
        private set { if (Set(ref _zoom, value)) Raise(nameof(ZoomText)); }
    }

    public double PanX { get => _panX; private set => Set(ref _panX, value); }
    public double PanY { get => _panY; private set => Set(ref _panY, value); }
    public bool ShowGrid { get => _grid; private set => Set(ref _grid, value); }
    public string ZoomText => $"{Zoom * 100:F0}%";

    /// <summary>画布可视区尺寸，由视图在尺寸变化时告诉它——「适应」要用。</summary>
    public void StageSize(Size s) => _stage = s;

    private void Scale(double factor)
    {
        // 以可视区中心为锚点缩放，不然放大后看的是左上角
        var cx = _stage.Width / 2;
        var cy = _stage.Height / 2;
        var next = Math.Clamp(Zoom * factor, 0.4, 2.5);
        var k = next / Zoom;
        PanX = cx - (cx - PanX) * k;
        PanY = cy - (cy - PanY) * k;
        Zoom = next;
    }

    /// <summary>适应：把所有设备的包围盒缩放平移到可视区里，留一圈边距。</summary>
    private void Fit()
    {
        if (Devices.Count == 0) { Zoom = 1; PanX = PanY = 0; return; }
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (var d in Devices)
        {
            x0 = Math.Min(x0, d.X);
            y0 = Math.Min(y0, d.Y);
            x1 = Math.Max(x1, d.X + d.Width + BenchDock.NodePad * 2);
            y1 = Math.Max(y1, d.Y + d.Height + BenchDock.NodePad * 2 + 26);   // 26 = 名称两行
        }
        const double pad = 40;
        var z = Math.Clamp(Math.Min((_stage.Width - pad * 2) / Math.Max(x1 - x0, 1),
                                    (_stage.Height - pad * 2) / Math.Max(y1 - y0, 1)), 0.4, 2.5);
        Zoom = z;
        PanX = (_stage.Width - (x1 - x0) * z) / 2 - x0 * z;
        PanY = (_stage.Height - (y1 - y0) * z) / 2 - y0 * z;
    }

    /// <summary>删除选中的设备。插在它身上的设备一并松开，绑定也清掉。</summary>
    private void Delete()
    {
        if (_selected is null) return;
        var id = _selected.Id;
        foreach (var child in _ws.Bench.Devices.Where(d => d.DockHostId == id))
        {
            child.DockHostId = null;
            child.DockAnchor = null;
            child.Dock = DockSide.None;
            _ws.Bench.Bindings.RemoveAll(b => b.DeviceId == child.InstanceId);
        }
        _ws.Bench.Bindings.RemoveAll(b => b.DeviceId == id);
        _ws.Bench.Devices.RemoveAll(d => d.InstanceId == id);
        Selected = null;
        _ = _ws.RebuildChannelsAsync();
    }

    /// <summary>三个 CARET 折叠区的开合。圆圈箭头点了要真收起来，不是装饰。</summary>
    public bool AdvancedOpen { get => _advOpen; set => Set(ref _advOpen, value); }
    public bool WellsOpen { get => _wellsOpen; set => Set(ref _wellsOpen, value); }
    public bool ChTableOpen { get => _chOpen; set => Set(ref _chOpen, value); }

    /// <summary>台面名称与设备数（原型未选中设备时的两行）。</summary>
    public string BenchName
    {
        get => _ws.Bench.Name;
        set { if (value is { Length: > 0 }) { _ws.Bench.Name = value; Raise(); } }
    }

    public string DeviceCountText
        => $"{_ws.Bench.Devices.Count} 台（{_ws.Channels.Count} 通道，{_ws.Channels.Count(c => c.Enabled)} 启用）";

    /// <summary>设备库里点中的那一项。真正的拖拽落位下一轮做。</summary>
    public LibraryItemViewModel? PickedFromLibrary
    {
        get => _picked;
        set => Set(ref _picked, value);
    }

    public DeviceNodeViewModel? Selected
    {
        get => _selected;
        set
        {
            var prev = _selected;
            if (!Set(ref _selected, value)) return;
            if (prev is not null) prev.IsSelected = false;
            if (value is not null) value.IsSelected = true;
            else { Wells.Clear(); ConnectionForm = null; ConfigForm = null; }
            Renaming = false;
            BuildForms();
            BuildBindTargets();
            BuildWells();
            RaiseAll(nameof(HasSelection), nameof(SelectedTitle), nameof(SelectedDriver), nameof(SelectedSub),
                     nameof(IsReactor), nameof(IsProbe), nameof(DeviceName), nameof(DeviceSimulated),
                     nameof(BindTarget));
        }
    }

    public SchemaFormViewModel? ConnectionForm
    {
        get => _connectionForm;
        private set => Set(ref _connectionForm, value);
    }

    public SchemaFormViewModel? ConfigForm
    {
        get => _configForm;
        private set => Set(ref _configForm, value);
    }

    public string ProbeResult
    {
        get => _probeResult;
        private set { if (Set(ref _probeResult, value)) Raise(nameof(HasProbeResult)); }
    }

    public bool HasProbeResult => _probeResult.Length > 0;

    public bool HasSelection => _selected is not null;
    /// <summary>空台面时画布上给一句提示，而不是一片空白。</summary>
    public bool IsEmpty => Devices.Count == 0;
    public string SelectedTitle => _selected?.Title ?? "未选中设备";

    /// <summary>反应器（自带通道）和探头（要绑定通道）两种面板不一样，照原型 renderProps 分支。</summary>
    public bool IsReactor => _selected?.Driver is { Info.ChannelsPerDevice: > 0 };
    public bool IsProbe => _selected is not null && !IsReactor;

    /// <summary>设备名可改（GBG 的铅笔按钮）。改完台面上的标签跟着变。</summary>
    public string DeviceName
    {
        get => _selected?.Device.Display ?? "";
        set
        {
            if (_selected is null || string.IsNullOrWhiteSpace(value)) return;
            _selected.Device.Label = value.Trim();
            _selected.NameChanged();
            RaiseAll(nameof(DeviceName), nameof(SelectedTitle));
        }
    }

    public bool Renaming
    {
        get => _renaming;
        set => Set(ref _renaming, value);
    }

    /// <summary>仿真开关是设备的真实状态，不是摆设。</summary>
    public bool DeviceSimulated
    {
        get => _selected?.Device.Simulated ?? true;
        set
        {
            if (_selected is null || _selected.Device.Simulated == value) return;
            _selected.Device.Simulated = value;
            Raise();
        }
    }

    /// <summary>探头绑到哪个通道（原型的「绑定通道」下拉，含「未绑定」）。</summary>
    public ObservableCollection<string> BindTargets { get; } = new();

    public string BindTarget
    {
        get
        {
            if (_selected is null) return "未绑定";
            var b = _ws.Bench.Bindings.FirstOrDefault(x => x.DeviceId == _selected.Id);
            return b is null ? "未绑定" : $"CH{b.ChannelNumber}";
        }
        set
        {
            if (_selected is null) return;
            _ws.Bench.Bindings.RemoveAll(x => x.DeviceId == _selected.Id);
            if (value is not null && value.StartsWith("CH") && int.TryParse(value[2..], out var ch))
                _ws.Bench.Bindings.Add(new Binding(_selected.Id, ch, BindingMode.Exclusive));
            Raise();
            _ = _ws.RebuildChannelsAsync();
        }
    }

    /// <summary>反应器的孔位 → 通道，可独立启停（原型 d.wells）。</summary>
    public ObservableCollection<ChannelRowViewModel> Wells { get; } = new();

    // ── 拖拽落位 ────────────────────────────────────────────────────
    private LibraryItemViewModel? _dragNew;      // 从设备库拖出来的新设备
    private DeviceNodeViewModel? _dragNode;      // 台面上被拖动的既有设备
    private Point _grab;                         // 抓取点相对设备左上角的偏移
    private Anchor? _hover;
    private DeviceNodeViewModel? _host;

    /// <summary>拖拽期间显示的可插接口。空 = 没在拖。</summary>
    public ObservableCollection<PortDot> Ports { get; } = new();

    /// <summary>台面上已接好的管路，交给 BenchLinks 画。</summary>
    public ObservableCollection<BenchLink> Links { get; } = new();

    public bool Dragging => _dragNew is not null || _dragNode is not null;
    public string DragArtKey { get; private set; } = "";
    public double DragWidth { get; private set; }
    public double DragX { get; private set; }
    public double DragY { get; private set; }

    /// <summary>当前会插上的那个接口。</summary>
    public Anchor? Hover
    {
        get => _hover;
        private set { if (!ReferenceEquals(_hover, value)) { _hover = value; RefreshPorts(); } }
    }

    private double DragHeight
    {
        get
        {
            var art = Controls.DeviceArtCache.Get(DragArtKey);
            return art is null ? DragWidth * 0.8 : DragWidth * art.ViewHeight / art.ViewWidth;
        }
    }

    /// <summary>台面上的宿主（反应器）。原型一台反应器为中心，这里取第一台。</summary>
    private DeviceNodeViewModel? Host => Devices.FirstOrDefault(d => BenchDock.IsHost(d.ArtKey));

    private ISet<string> TakenAnchors(string? exceptDevice = null)
        => _ws.Bench.Devices
              .Where(d => d.DockAnchor is not null && d.InstanceId != exceptDevice)
              .Select(d => d.DockAnchor!)
              .ToHashSet(StringComparer.Ordinal);

    public void BeginDragFromLibrary(LibraryItemViewModel item, Point at)
    {
        _dragNew = item;
        _dragNode = null;
        DragArtKey = item.ArtKey;
        DragWidth = DragArtKey == "reactor2" ? 176 : 120;
        _grab = new Point(DragWidth / 2, DragHeight / 2);
        StartDrag(at);
    }

    public void BeginDragDevice(DeviceNodeViewModel node, Point at)
    {
        _dragNode = node;
        _dragNew = null;
        DragArtKey = node.ArtKey;
        DragWidth = node.Width;
        _grab = new Point(at.X - node.X, at.Y - node.Y);
        Selected = node;
        StartDrag(at);
    }

    private void StartDrag(Point at)
    {
        _host = Host;
        DragTo(at);
        RaiseAll(nameof(Dragging), nameof(DragArtKey), nameof(DragWidth));
    }

    public void DragTo(Point at)
    {
        if (!Dragging) return;
        DragX = at.X - _grab.X;
        DragY = at.Y - _grab.Y;

        Anchor? pick = null;
        if (_host is { } h && !BenchDock.IsHost(DragArtKey))
            pick = BenchDock.Pick(DragArtKey, new Point(h.X, h.Y), h.Width, ConnectPoint,
                                  TakenAnchors(_dragNode?.Id), _dragNode?.Device.DockAnchor);
        Hover = pick;
        RaiseAll(nameof(DragX), nameof(DragY));
    }

    /// <summary>判断插哪个接口用的参考点：设备插头的当前位置。</summary>
    private Point ConnectPoint
    {
        get
        {
            var side = _host is { } h && DragX + DragWidth / 2 < h.X + h.Width / 2 ? "L" : "R";
            return BenchDock.PlugWorld(new Point(DragX, DragY), DragWidth, DragArtKey, side);
        }
    }

    public void CancelDrag()
    {
        _dragNew = null;
        _dragNode = null;
        _hover = null;
        Ports.Clear();
        Raise(nameof(Dragging));
    }

    /// <summary>松手：插上就吸附并连线，没插上就自由摆放。</summary>
    public void EndDrag(Point at)
    {
        if (!Dragging) return;
        DragTo(at);
        var anchor = Hover;
        var host = _host;
        var dev = _dragNode?.Device ?? CreateDevice();
        if (dev is null) { CancelDrag(); return; }

        // 落点就是用户放的位置，设备不被吸走；变的是连线（用户明确要求）
        dev.Position = new BPoint(DragX, DragY);

        if (anchor is not null && host is not null)
        {
            var side = anchor.Side ?? (DragX + DragWidth / 2 < host.X + host.Width / 2 ? "L" : "R");
            dev.DockHostId = host.Id;
            dev.DockAnchor = anchor.Id;
            dev.DockSideTag = side;
            dev.Dock = anchor.Kind == PortKind.Top ? DockSide.Top
                     : side == "L" ? DockSide.Left : DockSide.Right;
            dev.DockSlot = anchor.Slot;

            var ch = host.Channels.ElementAtOrDefault(anchor.Slot);
            Rebind(dev.InstanceId, ch > 0 ? new[] { ch } : Array.Empty<int>(),
                   anchor.Kind == PortKind.Top);
        }
        else
        {
            dev.DockHostId = null;
            dev.DockAnchor = null;
            dev.Dock = DockSide.None;
            _ws.Bench.Bindings.RemoveAll(b => b.DeviceId == dev.InstanceId);
        }

        CancelDrag();
        _ = _ws.RebuildChannelsAsync();
    }

    /// <summary>拖拽时把接口画出来：能插的亮、占用的暗、当前会插上的最大。</summary>
    private void RefreshPorts()
    {
        Ports.Clear();
        if (!Dragging || _host is not { } h || BenchDock.IsHost(DragArtKey)) return;
        var taken = TakenAnchors(_dragNode?.Id);
        foreach (var a in BenchDock.Anchors)
        {
            var w = BenchDock.AnchorWorld(new Point(h.X, h.Y), h.Width, a);
            var legal = BenchDock.Accepts(DragArtKey, a);
            var busy = taken.Contains(a.Id);
            Ports.Add(new PortDot
            {
                X = w.X, Y = w.Y, Label = a.Label,
                Hot = ReferenceEquals(a, Hover),
                Opacity = legal && !busy ? 1 : 0.14,
                Size = ReferenceEquals(a, Hover) ? 17 : legal && !busy ? 12 : 8.4,
                ColorHex = busy && !ReferenceEquals(a, Hover) ? "#c2c7cb"
                         : a.Kind == PortKind.Top ? "#2f6fb5"
                         : a.Kind == PortKind.Side ? "#c53a9d" : "#9aa0a5"
            });
        }
    }

    /// <summary>按停靠关系重算全部管路。</summary>
    private void RebuildLinks()
    {
        Links.Clear();
        foreach (var node in Devices)
        {
            var dev = node.Device;
            if (dev.DockAnchor is null || dev.DockHostId is null) continue;
            var host = Devices.FirstOrDefault(d => d.Id == dev.DockHostId);
            if (host is null) continue;
            var a = BenchDock.Anchors.FirstOrDefault(x => x.Id == dev.DockAnchor);
            if (a is null) continue;

            var to = BenchDock.AnchorWorld(new Point(host.X, host.Y), host.Width, a);
            var from = BenchDock.PlugWorld(new Point(node.X, node.Y), node.Width,
                                           node.ArtKey, dev.DockSideTag);
            var plug = BenchDock.PlugOf(node.ArtKey, dev.DockSideTag);
            Links.Add(new BenchLink(dev.InstanceId, host.Id, a.Id, BenchDock.LinkOf(node.ArtKey))
            {
                From = from, FromDir = plug.Dir,
                To = to, ToDir = a.Dir,
                Channel = host.Channels.ElementAtOrDefault(a.Slot),
                Label = a.Label
            });
        }
    }

    private void Rebind(string deviceId, IReadOnlyList<int> channels, bool exclusive)
    {
        _ws.Bench.Bindings.RemoveAll(b => b.DeviceId == deviceId);
        foreach (var ch in channels)
            _ws.Bench.Bindings.Add(new Binding(deviceId, ch,
                exclusive ? BindingMode.Exclusive : BindingMode.Shared));
    }

    /// <summary>台面上的编号前缀，沿用预置台面的写法（R1/P1/PH1/TU1…）。</summary>
    private static string PrefixOf(string artKey) => artKey switch
    {
        "reactor2" => "R",
        "pump" => "P",
        "ph" => "PH",
        "turb" => "TU",
        "raman" => "RA",
        "ir" => "IR",
        "psd" => "PS",
        "sampler" => "SA",
        "hplc" => "HP",
        _ => "D"
    };

    /// <summary>新设备的编号按类型顺延，撞号就往后排。</summary>
    private DeviceInstance? CreateDevice()
    {
        if (_dragNew is null) return null;
        var prefix = PrefixOf(_dragNew.ArtKey);
        var n = 1;
        while (_ws.Bench.Device($"{prefix}{n}") is not null) n++;
        var dev = new DeviceInstance
        {
            DriverId = _dragNew.Package.Id,
            InstanceId = $"{prefix}{n}",
            Position = new BPoint(DragX, DragY)
        };
        _ws.Bench.Devices.Add(dev);
        return dev;
    }
    public string SelectedDriver => _selected?.Driver?.Info.Name ?? "—";
    public string SelectedSub => _selected is null ? "" : $"{_selected.Id} · {_selected.ChannelText}";

    public string BenchSummary
        => $"{_ws.Bench.Devices.Count} 台设备 · {_ws.Channels.Count} 个通道 · 共享件 {string.Join("、", _ws.Bench.SharedDeviceIds())}";

    private void BuildBindTargets()
    {
        BindTargets.Clear();
        BindTargets.Add("未绑定");
        foreach (var c in _ws.Channels.Where(c => c.Enabled).OrderBy(c => c.Number))
            BindTargets.Add($"CH{c.Number}");
    }

    /// <summary>反应器的 A/B 孔各对一个通道，勾选即启停（原型 d.wells 那几行）。</summary>
    private void BuildWells()
    {
        Wells.Clear();
        if (_selected is null || !IsReactor) return;
        var i = 0;
        foreach (var n in _selected.Channels.OrderBy(x => x))
        {
            if (_ws.ChannelOf(n) is not { } ch) continue;
            Wells.Add(new ChannelRowViewModel(ch, $"{(i == 0 ? "A" : "B")} 孔 → 通道 CH{n}",
                                              Array.Empty<string>()));
            i++;
        }
    }

    public void Reload()
    {
        Library.Clear();
        foreach (var p in _ws.Drivers.ForLibrary()) Library.Add(new LibraryItemViewModel(p));

        Devices.Clear();
        foreach (var dev in _ws.Bench.Devices)
        {
            var driver = _ws.Drivers.Driver(dev.DriverId);
            var chs = driver is { Info.ChannelsPerDevice: > 0 }
                ? _ws.Channels.Where(c => c.HostInstanceId == dev.InstanceId).Select(c => c.Number).ToList()
                : _ws.Bench.Bindings.Where(b => b.DeviceId == dev.InstanceId).Select(b => b.ChannelNumber).ToList();
            Devices.Add(new DeviceNodeViewModel(dev, driver, chs));
        }
        RebuildLinks();
        Raise(nameof(IsEmpty));

        ChannelRows.Clear();
        var reactors = _ws.Bench.Devices
            .Where(d => _ws.Drivers.Driver(d.DriverId) is { Info.ChannelsPerDevice: > 0 }).ToList();
        foreach (var ch in _ws.Channels)
        {
            // 机A · A 孔（原型 devLabel）
            var idx = reactors.FindIndex(d => d.InstanceId == ch.HostInstanceId);
            var machine = idx >= 0 && idx < 8 ? "机" + "ABCDEFGH"[idx] : ch.HostInstanceId;
            var host = $"{machine} · {(ch.Well == 0 ? "A" : "B")} 孔";

            // 绑到该通道的探头，短名去掉"在线检测/在线"（原型 ptag 的写法）
            var probes = _ws.Bench.Bindings
                .Where(b => b.ChannelNumber == ch.Number)
                .Select(b => _ws.Bench.Device(b.DeviceId))
                .Where(d => d is not null)
                .Select(d => _ws.Drivers.Driver(d!.DriverId))
                .Where(dr => dr is { Info.ChannelsPerDevice: 0 })
                .Select(dr => dr!.Info.Name.Replace(" 在线检测", "").Replace("在线", ""))
                .Distinct().ToList();

            ChannelRows.Add(new ChannelRowViewModel(ch, host, probes));
        }

        Raise(nameof(BenchSummary));
    }

    private void BuildForms()
    {
        if (_selected?.Driver is not { } d) { ConnectionForm = null; ConfigForm = null; return; }
        ConnectionForm = new SchemaFormViewModel(d.ConnectionSchema, _selected.Device.Connection);
        ConfigForm = new SchemaFormViewModel(d.ConfigSchema, _selected.Device.Config);
        ProbeResult = "";
    }

    private async Task ProbeAsync()
    {
        if (_selected?.Driver is not { } d) return;
        ProbeResult = "正在测试…";
        try
        {
            var r = await d.ProbeAsync(_selected.Device.Connection, CancellationToken.None);
            ProbeResult = r.Success
                ? $"连接成功：{r.Message}；固件 {r.Firmware}；序列号 {r.Serial}；探测到 {r.DetectedChannels} 路"
                : $"连接失败：{r.Message}";
        }
        catch (Exception ex)
        {
            ProbeResult = "连接失败：" + ex.Message;
        }
    }
}

/// <summary>通道总表的一行（原型 .chtable .row）：色块 · CHn · 来源 · 探头标签 · 启停。</summary>
public sealed class ChannelRowViewModel : ViewModelBase
{
    public ChannelRowViewModel(Channel ch, string host, IReadOnlyList<string> probes)
    {
        Channel = ch;
        Host = host;
        Probes = probes;
        Capabilities = string.Join("、", ch.Capabilities.All.Select(Friendly).Distinct());
    }

    public Channel Channel { get; }
    public string Name => Channel.Name;
    /// <summary>机A · A 孔（原型 devLabel + 孔位字母）。</summary>
    public string Host { get; }
    /// <summary>挂在这个通道上的探头短名（原型 .ptag）。</summary>
    public IReadOnlyList<string> Probes { get; }
    public string Capabilities { get; }
    public string ColorHex => Channel.Number switch
    {
        1 => "#2f7ed8", 2 => "#2aa87a", 3 => "#c9772b", _ => "#8a63d2"
    };

    public bool Enabled
    {
        get => Channel.Enabled;
        set { Channel.Enabled = value; Raise(); }
    }

    private static string Friendly(ICapability c) => c switch
    {
        ITemperatureControl => "温控",
        IStirrer => "搅拌",
        IDosing => "加料",
        IScalarSensor s => s.Tags.Count > 0 ? s.Tags[0].DisplayName : "标量检测",
        ISpectrumSource => "谱图",
        IDistributionSource => "分布",
        IIllumination => "背景灯",
        IImageSource => "图像",
        _ => c.GetType().Name
    };
}

/// <summary>拖拽时画在画布上的一个接口点。</summary>
public sealed class PortDot
{
    public double X { get; init; }
    public double Y { get; init; }
    public double Size { get; init; } = 12;
    public double Opacity { get; init; } = 1;
    public bool Hot { get; init; }
    public string ColorHex { get; init; } = "#2f6fb5";
    public string Label { get; init; } = "";
    public double Left => X - Size / 2;
    public double Top => Y - Size / 2;
}
