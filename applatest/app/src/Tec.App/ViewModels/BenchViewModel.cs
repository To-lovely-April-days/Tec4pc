using Tec.Hmi.Ui.ViewModels;
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

    /// <summary>
    /// 设备库的分类。**从驱动自己报的能力里读出来的**，不是另立一张表：
    /// 装进来一个第三方驱动，它归哪一类由它的 Capabilities 决定，
    /// 不用回来改主程序。清单里没有 category 这个字段，也不该有——
    /// 那样同一件事就有两个说法，早晚对不上。
    ///
    /// 能力可能挂在两个地方：加载成功的读驱动实例（DriverInfo.Capabilities），
    /// 加载失败的（灰掉但仍然列出来的那些）只剩清单，读清单的那份。
    /// </summary>
    public string Category
    {
        get
        {
            var caps = Package.Driver?.Info.Capabilities
                       ?? (IReadOnlyList<string>)Package.Manifest.Capabilities;
            if (caps.Contains("ITemperatureControl")) return "反应与控温";
            if (caps.Contains("IDosing")) return "加料";
            if (caps.Contains("IScalarSensor")) return "在线检测";
            return "其他";
        }
    }
}

/// <summary>
/// 设备库里的一类。跟配方页步骤库的 <c>ModuleGroup</c> 是同一副样子——
/// 6px 色条 + 52px 组头 + 两列格子，收起展开也一样。两页的左栏干的是同一件事
/// （「从库里挑一个放到右边去」），长成两副样子没有道理。
///
/// 色条直接借步骤库那张模块色表：控温类设备与「温控」那组指令是同一个
/// 物理子系统，两页给它同一个颜色，扫一眼就对得上。
/// </summary>
public sealed class DeviceGroup : ViewModelBase
{
    private bool _open = true;

    // 从前还带一支 Color（借配方页的模块色），画在分类块左缘那条 6px 色条上。
    // 色条去掉之后没人用了，一起删——分类名写在组头上，色条是同一件事说第二遍。
    public DeviceGroup(string name) => Name = name;

    public string Name { get; }
    public ObservableCollection<LibraryItemViewModel> Items { get; } = new();
    public bool Open { get => _open; set => Set(ref _open, value); }
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
    public IReadOnlyList<int> Channels { get; private set; }

    /// <summary>绑定变了只改这一处，不用把整个节点换掉。</summary>
    public void SetChannels(IReadOnlyList<int> chs)
    {
        if (Channels.SequenceEqual(chs)) return;
        Channels = chs;
        Raise(nameof(ChannelText));
    }

    public string Id => Device.InstanceId;
    public string Title => Device.Display;

    /// <summary>驱动声明的设备图。插拔不改它，改的是下面的 ArtKey。</summary>
    public string BaseArtKey => Driver?.Info.IconKey ?? "rd105";

    /// <summary>
    /// 画布上实际画哪张图：Tr / pH 插上工位就换成插入形态（斜 9.6° 的那支，
    /// 与交互演示一致），拎起来（Lifted）或拔下来就换回独立形态。
    /// </summary>
    public string ArtKey
        => !Lifted && BenchDock.InsertArtFor(BaseArtKey, Device.DockAnchor) is { } ins ? ins : BaseArtKey;

    /// <summary>正被拖着走。拖动一开始就按独立形态画——插入件是长在工位上的样子，拎在手里不成立。</summary>
    public bool Lifted { get; set; }

    /// <summary>当前画的是插入形态（标签、名字都不画——演示里插入件旁只有读数框）。</summary>
    public bool Inserted => ArtKey != BaseArtKey;

    /// <summary>插拔 / 拎放之后把跟着图走的那几个属性一起刷一遍。</summary>
    public void DockVisualChanged()
        => RaiseAll(nameof(ArtKey), nameof(Width), nameof(Height), nameof(Inserted));

    private bool _run1, _run2;
    /// <summary>主机两颗工位 LED：工位上挂了配件就点亮（演示 led → #2F6B38）。</summary>
    public bool Run1 { get => _run1; set => Set(ref _run1, value); }
    public bool Run2 { get => _run2; set => Set(ref _run2, value); }

    private double _spin;
    /// <summary>转子角度（度）。泵在跑时动画拍子推着走，data-spin 那一组跟着转。</summary>
    public double Spin { get => _spin; set => Set(ref _spin, value); }

    private int _therm1, _therm2;
    /// <summary>两个工位的温控走向：1 升温 / −1 降温 / 0 没在变温。夹套染色与升降温标看它。</summary>
    public int Therm1 { get => _therm1; set => Set(ref _therm1, value); }
    public int Therm2 { get => _therm2; set => Set(ref _therm2, value); }

    private double _rpm1, _rpm2;
    /// <summary>两个工位的实测转速（rpm）。>0 才让桨叶摆，拍子也据此起停。</summary>
    public double Rpm1 { get => _rpm1; set => Set(ref _rpm1, value); }
    public double Rpm2 { get => _rpm2; set => Set(ref _rpm2, value); }
    public bool Stirring => Rpm1 > 0 || Rpm2 > 0;

    private double? _paddle1, _paddle2;
    /// <summary>桨叶相位 0..1（动画拍推）；null = 这一路没在搅拌，桨叶静止。</summary>
    public double? Paddle1 { get => _paddle1; set => Set(ref _paddle1, value); }
    public double? Paddle2 { get => _paddle2; set => Set(ref _paddle2, value); }

    public double X => Device.Position.X;
    public double Y => Device.Position.Y;
    public double Width => BenchDock.DisplayWidth(ArtKey);
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
    private bool _wellsOpen = true;
    private bool _chOpen = true;
    private LibraryItemViewModel? _picked;

    public BenchViewModel(Workspace ws)
    {
        _ws = ws;
        // 没存过盘的走不了「保存」——那得选路径，交给开始页的「另存为」
        SaveExperiment = new RelayCommand(() =>
        {
            if (ws.Store.CurrentPath is null) { SaveHint = "这份实验还没存过，去「开始 → 另存为」选个位置。"; return; }
            try { ws.Store.Save(); SaveHint = $"已保存到 {ws.Store.CurrentPath}"; }
            catch (Exception ex) { SaveHint = "保存失败：" + ex.Message; }
        });

        Probe = new RelayCommand(async () => await ProbeAsync());
        ToggleRename = new RelayCommand(() => Renaming = !Renaming);
        ToggleWells = new RelayCommand(() => WellsOpen = !WellsOpen);
        ToggleChTable = new RelayCommand(() => ChTableOpen = !ChTableOpen);
        DeleteSelected = new RelayCommand(Delete);
        ZoomIn = new RelayCommand(() => Scale(1.25));
        ZoomOut = new RelayCommand(() => Scale(1 / 1.25));
        FitAll = new RelayCommand(Fit);
        ws.BenchChanged += (_, _) => Reload();
        Reload();
    }

    public ObservableCollection<LibraryItemViewModel> Library { get; } = new();
    /// <summary>设备库按能力分的类。视图绑的是这个，Library 仍留着（拖拽与查找走它）。</summary>
    public ObservableCollection<DeviceGroup> Groups { get; } = new();
    public ObservableCollection<DeviceNodeViewModel> Devices { get; } = new();
    public ObservableCollection<ChannelRowViewModel> ChannelRows { get; } = new();

    /// <summary>HMI 手动控制面板要挂在同一份工作台上（视图代码开窗用）。</summary>
    internal Workspace Ws => _ws;

    /// <summary>底部那个保存：存实验。原来只是个图标，点了没反应。</summary>
    public RelayCommand SaveExperiment { get; }

    private string _saveHint = "";
    /// <summary>保存的结果就写在按钮上方，与「测试连接」的回显同一个位置。</summary>
    public string SaveHint
    {
        get => _saveHint;
        private set { if (Set(ref _saveHint, value)) Raise(nameof(HasSaveHint)); }
    }

    public bool HasSaveHint => _saveHint.Length > 0;

    public RelayCommand Probe { get; }
    public RelayCommand ToggleRename { get; }
    public RelayCommand ToggleWells { get; }
    public RelayCommand ToggleChTable { get; }
    public RelayCommand DeleteSelected { get; }
    public RelayCommand ZoomIn { get; }
    public RelayCommand ZoomOut { get; }
    public RelayCommand FitAll { get; }

    // ── 视图变换（左下角三个工具：放大 / 缩小 / 适应）───────────────
    // 原型台面底下铺着一层 28px 网格，连同一个开关一起去掉了：设备按落点
    // 自由摆、不吸附网格，那些线对不齐任何东西，留着只是噪音
    private double _zoom = 1, _panX, _panY;
    private Size _stage = new(900, 700);

    public double Zoom
    {
        get => _zoom;
        private set { if (Set(ref _zoom, value)) Raise(nameof(ZoomText)); }
    }

    public double PanX { get => _panX; private set => Set(ref _panX, value); }
    public double PanY { get => _panY; private set => Set(ref _panY, value); }
    public string ZoomText => $"{Zoom * 100:F0}%";

    /// <summary>画布可视区尺寸，由视图在尺寸变化时告诉它——「适应」要用。</summary>
    public void StageSize(Size s)
    {
        var changed = Math.Abs(_stage.Width - s.Width) > 0.5 || Math.Abs(_stage.Height - s.Height) > 0.5;
        _stage = s;
        if (!changed) return;
        // 可视区刚定下来（页面首次量出尺寸 / 窗口改了大小）：把小窗放回
        // 存档的位置再夹。开档时 Reload 先于页面布局跑，那一刻夹用的还是
        // 缺省的 900×700——存在画布右半边的窗会被错误地拽回左边（实测踩到）。
        // 挪动是随手落盘的（PanelMoved），所以放回存档位置不会丢在场的挪动
        foreach (var p in Panels)
        {
            if (_ws.Bench.Device(p.DeviceId) is { PanelX: { } px, PanelY: { } py })
            {
                p.X = px;
                p.Y = py;
            }
            ClampPanel(p);
        }
    }

    /// <summary>
    /// 画布可视区换算到世界坐标的矩形。画布**只有缩放没有平移**，
    /// 拖出可视区的东西没有任何办法再看见——所以设备和泵小窗都夹在这个框里
    /// （演示的面板拖动也是夹在画布内的，同一件事）。
    /// </summary>
    private Rect VisibleWorld()
        => new(-PanX / Zoom, -PanY / Zoom,
               Math.Max(_stage.Width, 120) / Zoom, Math.Max(_stage.Height, 120) / Zoom);

    /// <summary>目标比可视区还大时保住左/上沿，别把东西夹没了。</summary>
    private static double ClampAxis(double v, double min, double max)
        => max <= min ? min : Math.Clamp(v, min, max);

    /// <summary>把一扇泵小窗整个收进可视区（自动弹出、拖动、量出实际高度后都要夹）。</summary>
    public void ClampPanel(PumpPanelViewModel p)
    {
        var vis = VisibleWorld();
        p.X = ClampAxis(p.X, vis.X + 6, vis.Right - p.W - 6);
        p.Y = ClampAxis(p.Y, vis.Y + 6, vis.Bottom - p.H - 6);
    }

    /// <summary>
    /// 用户拖动小窗：夹进可视区之后把位置**记到设备上**——它随 .tec 一起落盘，
    /// 下次打开窗子还在挪去的地方。位置变了也算实验改动，脏标记点上。
    /// </summary>
    public void PanelMoved(PumpPanelViewModel p)
    {
        ClampPanel(p);
        if (_ws.Bench.Device(p.DeviceId) is not { } dev) return;
        if (dev.PanelX == p.X && dev.PanelY == p.Y) return;
        dev.PanelX = p.X;
        dev.PanelY = p.Y;
        _ws.Store.MarkDirty();
    }

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
        foreach (var p in Panels) ClampPanel(p);   // 可视区变了，小窗跟着收回来
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
        foreach (var p in Panels) ClampPanel(p);
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
    public bool WellsOpen { get => _wellsOpen; set => Set(ref _wellsOpen, value); }
    public bool ChTableOpen { get => _chOpen; set => Set(ref _chOpen, value); }

    // BenchName / DeviceCountText 都撤了，属性栏上那两行不再显示。
    // 设备数那句画布右下角的 BenchSummary 已经在说；
    // 台面名（_ws.Bench.Name）还照旧写进运行记录与报告，只是界面上暂时没有入口改它。

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
            // 关掉的泵控制小窗，点一下台面上那台泵就再弹出来
            if (value is { } vn && _panels.TryGetValue(vn.Id, out var pp)) pp.Closed = false;
            Renaming = false;
            BuildForms();
            BuildBindTargets();
            BuildWells();
            _connDirty = false;
            RaiseAll(nameof(HasSelection), nameof(SelectedTitle), nameof(SelectedDriver), nameof(SelectedSub),
                     nameof(IsReactor), nameof(IsProbe), nameof(DeviceName), nameof(BindTarget),
                     nameof(LinkText), nameof(LinkOk));
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

    // ── 链路状态（属性栏名字底下那一行）────────────────────────────
    //
    // 「已连接」只在会话真开着时才说；打不开把驱动报的原因原话带出来。
    // 连接参数改了但还没重连的那段时间，如实标「未生效」——会话还拿着旧口子，
    // 填了 COM7 面板里却读不到数，就是没这一句闹的。

    /// <summary>连接表单改过、还没点「连接」让它生效。</summary>
    private bool _connDirty;

    public string LinkText
    {
        get
        {
            if (_selected is null) return "";
            var (text, _) = DeviceLink.Describe(_ws.Session(_selected.Id), _ws.OpenFailure(_selected.Id));
            return _connDirty ? $"{text} · 连接参数已改，点「连接」生效" : text;
        }
    }

    public bool LinkOk => _selected is not null && !_connDirty
        && DeviceLink.Describe(_ws.Session(_selected.Id), _ws.OpenFailure(_selected.Id)).Ok;

    private void RaiseLink() => RaiseAll(nameof(LinkText), nameof(LinkOk));

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
    private Point _origin;                       // 拖之前设备在哪儿，Esc 要送回去
    private Anchor? _hover;
    private DeviceNodeViewModel? _host;

    /// <summary>拖拽期间显示的可插接口。空 = 没在拖。</summary>
    public ObservableCollection<PortDot> Ports { get; } = new();

    /// <summary>台面上已接好的管路，交给 BenchLinks 画。</summary>
    public ObservableCollection<BenchLink> Links { get; } = new();

    /// <summary>插上工位的 Tr / pH 各带一张读数标签（演示的 tag()：白底红框，值 + 单位）。</summary>
    public ObservableCollection<ReadTagViewModel> Tags { get; } = new();

    /// <summary>每台接上的泵一扇控制小窗（演示的泵控制面板）。接上自动弹出，可拖、可关。</summary>
    public ObservableCollection<PumpPanelViewModel> Panels { get; } = new();
    private readonly Dictionary<string, PumpPanelViewModel> _panels = new(StringComparer.Ordinal);

    private double _flowClock;
    /// <summary>流动动画的时钟（秒）。泵在跑时 AnimTick 推着走，BenchLinks 按它挪虚线。</summary>
    public double FlowClock { get => _flowClock; private set => Set(ref _flowClock, value); }

    /// <summary>有泵在跑：视图据此起停动画拍子（不跑就一拍都不打，别学常驻心跳的教训）。</summary>
    public bool AnyPumpRunning => Panels.Any(p => p.Running);

    /// <summary>有东西在动（泵转子或哪个工位的桨）：动画拍子的起停条件。</summary>
    public bool AnyMotion => AnyPumpRunning || Devices.Any(d => d.Stirring);

    /// <summary>小窗上的启停按到了：重算管路（流动标志变了）并告诉视图起停动画。</summary>
    internal void PumpRunChanged()
    {
        RebuildLinks();
        Raise(nameof(AnyPumpRunning));
    }

    /// <summary>跑着改速率：流速跟着变（演示 pumpFx 调 animation-duration 同一件事）。</summary>
    internal void PumpRateChanged(PumpPanelViewModel p)
    {
        if (p.Running) RebuildLinks();
    }

    /// <summary>小窗背后那台泵的加料能力——**操作走的就是驱动契约 IDosing**。</summary>
    internal IDosing? DosingFor(PumpPanelViewModel p)
        => p.Channel > 0 ? _ws.ChannelOf(p.Channel)?.Capabilities.Get<IDosing>() : null;

    /// <summary>
    /// 动画一拍：泵转子按速率转（演示 3.2 − 0.5×速率 秒一圈），流动时钟前进。
    /// 返回 false = 没有泵在跑，视图就把拍子停了。
    /// </summary>
    public bool AnimTick(double dt)
    {
        var any = false;
        foreach (var p in Panels)
        {
            if (!p.Running) continue;
            any = true;
            if (Devices.FirstOrDefault(d => d.Id == p.DeviceId) is { } node)
                node.Spin = (node.Spin + 360 * dt / Math.Max(0.7, 3.2 - 0.5 * p.Rate)) % 360;
        }
        if (any) FlowClock += dt;

        // 观察窗里的桨：转速 >0 才摆，一圈 1.2 s——与 HMI 釜图同一拍
        foreach (var node in Devices)
        {
            if (node.Rpm1 > 0) { node.Paddle1 = ((node.Paddle1 ?? 0) + dt / 1.2) % 1.0; any = true; }
            else node.Paddle1 = null;
            if (node.Rpm2 > 0) { node.Paddle2 = ((node.Paddle2 ?? 0) + dt / 1.2) % 1.0; any = true; }
            else node.Paddle2 = null;
        }
        return any;
    }

    public bool Dragging => _dragNew is not null || _dragNode is not null;

    /// <summary>
    /// 幽灵只在「从设备库往外拖」时出现——那会儿画布上还没有这台设备，
    /// 总得有个东西跟着手。拖已有的设备时设备自己在动，再画一个幽灵就是重影。
    /// </summary>
    public bool ShowGhost => _dragNew is not null;

    public string DragArtKey { get; private set; } = "";
    public double DragWidth { get; private set; }
    public double DragX { get; private set; }
    public double DragY { get; private set; }

    /// <summary>设备库那一栏的宽度，与视图的三栏定义一致。</summary>
    // 跟配方页、配方库页的左栏同宽。三页的左栏干的是同一件事，
    // 宽度不一致的话在菜单之间切换整个中列会横跳一下
    private const double LibraryWidth = 240;

    /// <summary>
    /// 手上拎着的那个小样的尺寸。**固定**，既不随设备实际大小变、也不随缩放变：
    /// 从前幽灵画的是「落地之后的原大」，一台反应器拖起来就是一大块半透明的图
    /// 糊在画布上，挡住了底下的接口圆点和管路预览——而那两样才是拖动时真正
    /// 要看的东西。小样只回答「手上是哪一台」，落在哪、接哪个口由接口圆点说。
    /// 尺寸取的是设备库格子里那张图（102×74）再小一档。
    /// </summary>
    private const double GhostW = 88, GhostH = 64;

    public double GhostBoxW => GhostW;
    public double GhostBoxH => GhostH;

    /// <summary>
    /// 幽灵画在跨三栏的顶层，所以要把画布坐标换成视图坐标——不这样它会被
    /// 画布的裁剪切掉，从设备库里拖出来时像是从库底下钻出来的。
    ///
    /// 抓取点 _grab 是设备中心（见 BeginDragFromLibrary），所以把小样也按中心
    /// 摆：它既跟着指针，又正好落在「这台设备将来占的那块地方」的正中。
    /// </summary>
    public double GhostX => (DragX + DragWidth / 2) * Zoom + PanX + LibraryWidth - GhostW / 2;
    public double GhostY => (DragY + DragHeight / 2) * Zoom + PanY - GhostH / 2;

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

    /// <summary>台面上所有能当宿主的设备（反应器）。放几台就是几台。</summary>
    private List<DeviceNodeViewModel> Hosts
        => Devices.Where(d => BenchDock.IsHost(d.ArtKey)).ToList();

    /// <summary>
    /// 某台宿主身上已被占用的接口。必须按宿主分开数——接口号（T1a / R2…）
    /// 是每台反应器各有一套，混在一起的话，一台反应器的 T1a 被占了，
    /// 另一台的 T1a 也跟着连不上。
    /// </summary>
    private ISet<string> TakenAnchors(string hostId, string? exceptDevice = null)
        => _ws.Bench.Devices
              .Where(d => d.DockAnchor is not null && d.DockHostId == hostId
                          && d.InstanceId != exceptDevice)
              .Select(d => d.DockAnchor!)
              .ToHashSet(StringComparer.Ordinal);

    public void BeginDragFromLibrary(LibraryItemViewModel item, Point at)
    {
        _dragNew = item;
        _dragNode = null;
        DragArtKey = item.ArtKey;
        DragWidth = BenchDock.DisplayWidth(DragArtKey);
        _grab = new Point(DragWidth / 2, DragHeight / 2);
        StartDrag(at);
    }

    public void BeginDragDevice(DeviceNodeViewModel node, Point at)
    {
        _dragNode = node;
        _dragNew = null;
        // 插在工位上的探头一拎就换回独立形态——插入件是长在工位上的样子，
        // 拎在手里不成立。抓取点改按小样中心：插入件和独立图不一样大，
        // 沿用按下时的偏移会让探头跳到手的斜下方
        var wasInserted = node.Inserted;
        node.Lifted = true;
        node.DockVisualChanged();
        DragArtKey = node.ArtKey;
        DragWidth = node.Width;
        _grab = wasInserted ? new Point(DragWidth / 2, DragHeight / 2)
                            : new Point(at.X - node.X, at.Y - node.Y);
        _origin = new Point(node.X, node.Y);
        Selected = node;
        StartDrag(at);
    }

    private void StartDrag(Point at)
    {
        _host = null;                       // 拖到哪台反应器附近就接哪台，每次移动重算
        DragTo(at);
        RaiseAll(nameof(Dragging), nameof(ShowGhost), nameof(DragArtKey), nameof(DragWidth));
    }

    public void DragTo(Point at)
    {
        if (!Dragging) return;
        DragX = at.X - _grab.X;
        DragY = at.Y - _grab.Y;

        // 整台设备（连底下两行名字）都夹在可视区里：半台机器悬在画布外
        // 就是半台机器再也点不着（用户截到泵的瓶子被裁在设备库底下）
        var vis = VisibleWorld();
        var bw = DragWidth + BenchDock.NodePad * 2;
        var bh = DragHeight + BenchDock.NodePad * 2 + 26;
        DragX = ClampAxis(DragX, vis.X, vis.Right - bw);
        DragY = ClampAxis(DragY, vis.Y, vis.Bottom - bh);

        // 台面上已有的设备直接跟着手走。原来是把设备留在原地、另画一个幽灵，
        // 看着就像拖不动——设备本来就在画布上，让它自己动才对
        _dragNode?.MoveTo(new Point(DragX, DragY));

        // 拖的是主机的话，插在它工位上的探头得跟着机器走——
        // 它们画在机器身上，机器挪了探头留在原地就是拔了线的样子
        if (_dragNode is { } hn && BenchDock.IsHost(hn.ArtKey)) SnapChildren(hn);

        PickHost();
        RebuildLinks();                    // 拖动时管路跟着手走
        RaiseAll(nameof(DragX), nameof(DragY), nameof(GhostX), nameof(GhostY));
    }

    /// <summary>
    /// 台面上可以有好几台反应器。每次移动都把每台各算一个候选接口，
    /// 取插头离得最近的那台——原来只认第一台，摆第二台反应器时
    /// 别的设备怎么拖都只能连到第一台上。
    /// </summary>
    private void PickHost()
    {
        if (BenchDock.IsHost(DragArtKey)) { _host = null; Hover = null; return; }

        DeviceNodeViewModel? bestHost = null;
        Anchor? bestAnchor = null;
        var bestDist = double.MaxValue;

        foreach (var h in Hosts)
        {
            var plug = ConnectPointFor(h);
            var a = BenchDock.Pick(DragArtKey, new Point(h.X, h.Y), h.Width, plug,
                                   TakenAnchors(h.Id, _dragNode?.Id),
                                   _dragNode?.Device.DockHostId == h.Id ? _dragNode.Device.DockAnchor : null);
            if (a is null) continue;        // 这台的同类接口占满了，看下一台

            var w = BenchDock.AnchorWorld(new Point(h.X, h.Y), h.Width, a);
            var d = (w.X - plug.X) * (w.X - plug.X) + (w.Y - plug.Y) * (w.Y - plug.Y);
            if (d >= bestDist) continue;
            bestDist = d;
            bestHost = h;
            bestAnchor = a;
        }

        _host = bestHost;
        Hover = bestAnchor;
    }

    /// <summary>判断插哪个接口用的参考点：设备插头相对某台宿主的当前位置。</summary>
    private Point ConnectPointFor(DeviceNodeViewModel host)
    {
        var side = DragX + DragWidth / 2 < host.X + host.Width / 2 ? "L" : "R";
        return BenchDock.PlugWorld(new Point(DragX, DragY), DragWidth, DragArtKey, side);
    }

    /// <summary>把某台主机身上插着的探头都吸回各自工位的位置。</summary>
    private void SnapChildren(DeviceNodeViewModel host)
    {
        foreach (var n in Devices)
        {
            if (n.Device.DockHostId != host.Id || n.Lifted) continue;
            if (BenchDock.InsertArtFor(n.BaseArtKey, n.Device.DockAnchor) is not { } ins) continue;
            if (BenchDock.AnchorById(n.Device.DockAnchor) is not { } a) continue;
            n.MoveTo(BenchDock.SnapPosition(ins, a, new Point(host.X, host.Y), host.Width));
        }
    }

    /// <summary>Esc 撤销这次拖拽：设备既然是跟着手走的，就得把它送回原位。</summary>
    public void CancelDrag()
    {
        _dragNode?.MoveTo(_origin);
        ClearDrag();
    }

    /// <summary>只清拖拽状态，不动设备位置。落位成功后走这条。</summary>
    private void ClearDrag()
    {
        if (_dragNode is { } n) { n.Lifted = false; n.DockVisualChanged(); }
        _dragNew = null;
        _dragNode = null;
        _hover = null;
        Ports.Clear();
        RaiseAll(nameof(Dragging), nameof(ShowGhost));
        RebuildLinks();
    }

    /// <summary>
    /// 松手：插上就吸附并连线，没插上就自由摆放。
    /// Tr / pH 插上工位时**吸进机器**——位置换算到演示插入件画的那个地方，
    /// 图换成斜插形态；泵留在放手的位置，靠管子连过去（两条都照交互演示）。
    /// </summary>
    public void EndDrag(Point at)
    {
        if (!Dragging) return;
        DragTo(at);
        var anchor = Hover;
        var host = _host;
        var baseKey = _dragNode?.BaseArtKey ?? _dragNew?.ArtKey ?? "";
        var node = _dragNode;
        var dev = node?.Device ?? CreateDevice();
        if (dev is null) { ClearDrag(); return; }

        // 落点就是用户放的位置，设备不被吸走；变的是连线（用户明确要求）。
        // 唯一的例外是插进工位的探头，见下
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

            if (BenchDock.InsertArtFor(baseKey, anchor.Id) is { } ins)
            {
                var p = BenchDock.SnapPosition(ins, anchor, new Point(host.X, host.Y), host.Width);
                dev.Position = new BPoint(p.X, p.Y);
            }

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

        node?.MoveTo(new Point(dev.Position.X, dev.Position.Y));
        ClearDrag();
        RebuildLinks();
        _ = _ws.RebuildChannelsAsync();
    }

    /// <summary>
    /// 拖拽时把接口画出来：能插的亮、占用的暗、当前会插上的最大。
    /// 台面上每台反应器的接口都画——不然摆了两台，只看得见一台的口，
    /// 根本不知道另一台也能接。
    /// </summary>
    private void RefreshPorts()
    {
        Ports.Clear();
        if (!Dragging || BenchDock.IsHost(DragArtKey)) return;

        foreach (var h in Hosts)
        {
            var taken = TakenAnchors(h.Id, _dragNode?.Id);
            var target = ReferenceEquals(h, _host);
            foreach (var a in BenchDock.Anchors)
            {
                var w = BenchDock.AnchorWorld(new Point(h.X, h.Y), h.Width, a);
                var legal = BenchDock.Accepts(DragArtKey, a);
                var busy = taken.Contains(a.Id);
                var hot = target && ReferenceEquals(a, Hover);
                Ports.Add(new PortDot
                {
                    X = w.X, Y = w.Y, Label = a.Label,
                    Hot = hot,
                    // 不是当前瞄准的那台就淡一档，一眼看得出会接到哪台上
                    Opacity = legal && !busy ? (target ? 1 : 0.45) : 0.14,
                    Size = hot ? 17 : legal && !busy ? 12 : 8.4,
                    ColorHex = busy && !hot ? "#c2c7cb"
                             : a.Kind == PortKind.Top ? "#a41626"
                             : a.Kind == PortKind.Side ? "#c53a9d" : "#9aa0a5"
                });
            }
        }
    }

    /// <summary>
    /// 按停靠关系重算全部管路。拖动中的那台用幽灵的位置算，管路跟着手走；
    /// 从设备库拖出来的新设备，只要选中了接口就先画一条预览。
    /// </summary>
    private void RebuildLinks()
    {
        Links.Clear();
        foreach (var node in Devices)
        {
            var dev = node.Device;
            if (dev.DockAnchor is null || dev.DockHostId is null) continue;
            if (_dragNode is not null && dev.InstanceId == _dragNode.Id) continue;   // 由预览接管
            var host = Devices.FirstOrDefault(d => d.Id == dev.DockHostId);
            if (host is null) continue;
            var a = BenchDock.AnchorById(dev.DockAnchor);
            if (a is null) continue;
            // Tr / pH 是插进工位的，没有管子——插入件本身画在工位上，
            // 它的「连接」由插入形态 + 读数标签表达（见 RebuildDecor）
            if (a.Accept is "tr" or "ph") continue;
            Add(node.ArtKey, new Point(node.X, node.Y), node.Width, dev.DockSideTag,
                dev.InstanceId, host, a);
        }

        // 拖动预览只给泵：探头插上前没有管线可预览，接到哪个口由接口圆点说
        if (Dragging && Hover is { } ha && _host is { } hh && ha.Accept == "feed")
        {
            var side = ha.Side ?? (DragX + DragWidth / 2 < hh.X + hh.Width / 2 ? "L" : "R");
            Add(DragArtKey, new Point(DragX, DragY), DragWidth, side,
                _dragNode?.Id ?? "?", hh, ha);
        }

        RebuildDecor();

        // 单条几何交给 BenchDock.Link——运行页的台面总览用的是同一个，
        // 两边各写一份的话，同一台面在两页会画成两个样子。
        // 泵在跑的那根管挂上流动标志：速度按演示的公式（32 单位走 1.6−0.24×速率 秒）
        void Add(string art, Point pos, double width, string? side,
                 string devId, DeviceNodeViewModel host, Anchor a)
        {
            var l = BenchDock.Link(art, pos, width, side, devId, host.Id,
                                   new Point(host.X, host.Y), host.Width, host.Channels, a);
            if (l.Kind == LinkKind.Feed && _panels.TryGetValue(devId, out var p) && p.Running)
                l = l with { Flow = true, FlowSpeed = 32.0 / Math.Max(0.3, 1.6 - 0.24 * p.Rate) };
            Links.Add(l);
        }
    }

    /// <summary>
    /// 跟着停靠关系走的两样点缀：插上工位的探头各一张读数标签
    /// （白底灰框的提示式读数框，一根引线从探头头部引出去），主机的工位 LED
    /// （工位上挂了任何配件就点亮，演示 render() 里 led 那一句）。
    /// </summary>
    private void RebuildDecor()
    {
        // 读数框不再按演示原尺寸贴在探头旁：64×32 乘上主机比例只剩 34×17、
        // 字 8px，看不清（用户提出）。改成**提示框式的引出**：框固定 84×34、
        // 字 16，从探头头部拉一根引线伸出去——搅拌器左侧的 Tr 往左上角引、
        // 右边的 pH 往右上角引（用户定的方向）。
        //
        // **两排，四条引线两两不交叉**：单排摆的话工位 1 的 pH 框和工位 2 的
        // Tr 框要在中间换位，两根引线必然打叉，看的人会把一通道的数错认成
        // 二通道的（用户指出）。现在 Tr 一排在上（引线落点 head−24，往左上）、
        // pH 一排在下（落点 head+30，往右上，框比落点往左长——框再往右挪
        // 就会挡住邻位 Tr 引线上行的走廊 161..174）。逐条验过：四条引线的
        // 横向区间两两不重叠，也没有一条从别的框底下穿过。
        // 探头头顶在主机坐标里：Tr (140,31)、pH (220,31)（斜 9.6° 转完的位置），
        // 工位 2 整体右移 200。
        const double TW = 84, TH = 34;
        Tags.Clear();
        foreach (var node in Devices)
        {
            var dev = node.Device;
            if (node.Lifted || dev.DockHostId is null) continue;
            if (BenchDock.AnchorById(dev.DockAnchor) is not { Accept: "tr" or "ph" } a) continue;
            if (Devices.FirstOrDefault(d => d.Id == dev.DockHostId) is not { } host) continue;

            var s = host.Width / BenchDock.MachineVw;
            var hx = (a.Accept == "tr" ? 140.0 : 220.0) + (a.Slot == 1 ? 200 : 0);
            var head = new Point(host.X + BenchDock.NodePad + hx * s,
                                 host.Y + BenchDock.NodePad + 31 * s);
            double x, y, ax;
            if (a.Accept == "tr")
            {
                x = head.X - 98;           // 框 [head−98, head−14]，引线落它右下角
                ax = head.X - 24;
                y = head.Y - TH - 65;      // 上排
            }
            else
            {
                x = head.X - 44;           // 框 [head−44, head+40]，引线落它右下角
                ax = head.X + 30;
                y = head.Y - TH - 23;      // 下排
            }
            var ay = y + TH;
            Tags.Add(new ReadTagViewModel(a.Accept, host.Channels.ElementAtOrDefault(a.Slot))
            {
                X = x, Y = y, W = TW, H = TH,
                FontSize = 16,
                BorderW = new Thickness(1.6),
                Radius = new CornerRadius(4),
                AnchorX = head.X, AnchorY = head.Y,
                AttachX = ax, AttachY = ay
            });
        }
        RefreshTagValues();

        foreach (var h in Devices)
        {
            if (!BenchDock.IsHost(h.ArtKey)) continue;
            bool s0 = false, s1 = false;
            foreach (var d in _ws.Bench.Devices)
            {
                if (d.DockHostId != h.Id) continue;
                if (BenchDock.AnchorById(d.DockAnchor) is not { } a) continue;
                if (a.Slot == 0) s0 = true;
                else s1 = true;
            }
            h.Run1 = s0;
            h.Run2 = s1;
        }

        // 泵控制小窗：接上的泵一台一扇，**接上那一刻自动在泵旁边弹出来**；
        // 拔下或删掉就收走。窗随泵：肘形引线的落点跟着泵的当前位置走，
        // 窗本身留在用户摆的地方（拖动窗头可以挪）
        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in Devices)
        {
            var dev = node.Device;
            if (dev.DockHostId is null) continue;
            if (BenchDock.AnchorById(dev.DockAnchor) is not { Accept: "feed" } a) continue;
            if (Devices.FirstOrDefault(d => d.Id == dev.DockHostId) is not { } host) continue;
            live.Add(dev.InstanceId);
            if (!_panels.TryGetValue(dev.InstanceId, out var p))
            {
                // 用户挪过的位置存在设备上（随 .tec 落盘），有就用它——
                // 存盘再开窗子跳回默认位置就等于没挪（用户实测提出）。
                // 没挪过按默认弹在泵右边；右边放不下（泵靠着画布右缘）
                // 就翻到左边，最后整扇夹进可视区——弹出来就得整扇看得见
                var vis0 = VisibleWorld();
                var px = node.X + node.Width + BenchDock.NodePad * 2 + 26;
                if (px + PumpPanelViewModel.PanelW + 6 > vis0.Right)
                    px = node.X - PumpPanelViewModel.PanelW - 26;
                p = new PumpPanelViewModel(this, dev.InstanceId)
                {
                    X = dev.PanelX ?? px,
                    Y = dev.PanelY ?? node.Y - 16
                };
                ClampPanel(p);
                _panels[dev.InstanceId] = p;
                Panels.Add(p);
            }
            p.SetDock(a.Slot, host.Channels.ElementAtOrDefault(a.Slot));
            var t = BenchDock.PumpPanelTarget(new Point(node.X, node.Y), node.Width);
            p.SetTarget(t.X, t.Y);
        }
        foreach (var id in _panels.Keys.Where(k => !live.Contains(k)).ToList())
        {
            var p = _panels[id];
            p.MarkStopped();                // 泵已不在通道上，会话也随台面重建停了
            _panels.Remove(id);
            Panels.Remove(p);
        }
        Raise(nameof(AnyPumpRunning));
    }

    /// <summary>
    /// 把每张读数标签的数值刷一遍。**读的是真数**：
    /// Tr 读所插通道 ITemperatureControl 的当前釜温——运行页曲线画的就是这一路；
    /// pH 读该通道 IScalarSensor 里 pH 那一签的最新采样。
    /// 没有通道、没有能力、还没有采样，就显示「—」，不编一个数出来。
    /// 视图每秒叫一次（页面可见时），插拔后 RebuildDecor 也立即叫。
    /// </summary>
    public void RefreshTagValues()
    {
        foreach (var t in Tags)
        {
            var txt = "—";
            var ch = t.Channel > 0 ? _ws.ChannelOf(t.Channel) : null;
            if (ch is not null)
            {
                if (t.Kind == "tr")
                {
                    // 釜内探头没绑 / 没读到就是 NaN——印「—」，不印「NaN ℃」
                    if (ch.Capabilities.Get<ITemperatureControl>() is { } tc && !double.IsNaN(tc.CurrentReactor))
                        txt = $"{tc.CurrentReactor:F1} ℃";
                }
                else
                {
                    var sensor = ch.Capabilities.All.OfType<IScalarSensor>()
                                   .FirstOrDefault(s => s.Tags.Any(g => g.Tag == "pH"));
                    // 带上量的名目（用户要求数值带单位）：pH 本身无量纲，名目就是 pH
                    if (sensor is not null && sensor.TryReadLatest("pH", out var smp))
                        txt = $"pH {smp.Value:F2}";
                }
            }
            t.Text = txt;
        }

        // 小窗上的「累计加料」同拍刷新：读 IDosing.TotalVolume——
        // 驱动模型自己积分出来的量（带起停加速段），不在界面上另攒一份
        foreach (var p in Panels)
        {
            var cap = DosingFor(p);
            p.SetTotal(cap is null ? "—" : $"{cap.TotalVolume:F2} mL");
        }

        RefreshStationState();
        RaiseLink();      // 会话是后台开的：属性栏那行「已连接 / 未连接」跟着每秒刷
    }

    /// <summary>
    /// 台面上主机两个工位的状态：升温 / 降温、有没有在搅拌。**全是实测值**——
    /// 温控走向按「设定温度比现在的釜温高还是低」判：Tset 这一签只在控温时
    /// 才发（停控没有设定值），所以它在不在就是「这一路在不在控温」，
    /// 差值的方向就是升温还是降温；搅拌读 IStirrer 的实测转速。
    ///
    /// 两条走过弯路的路子记在这儿，别再绕回去：
    /// **跟夹套走势判**（原型 v60 的做法）会读反——这台机器的夹套领先釜温，
    /// 降温时 Tj 先冲到目标下方再回摆，降到一半标就翻成「升温」。
    /// **跟 duty 判**会抖——控温输出稍有偏差就打满，到温后噪声让它在
    /// ±100 % 之间跳，标跟着一闪一闪。差值带 0.5 K 死区，两样都没有。
    /// </summary>
    private void RefreshStationState()
    {
        var moved = false;
        foreach (var node in Devices)
        {
            if (node.Channels.Count == 0) continue;
            var chs = node.Channels.OrderBy(x => x).ToArray();   // 索引即工位号（与读数标签同一约定）
            var (t1, r1) = StationState(chs.ElementAtOrDefault(0));
            var (t2, r2) = StationState(chs.ElementAtOrDefault(1));
            node.Therm1 = t1;
            node.Therm2 = t2;
            if ((node.Rpm1 > 0) != (r1 > 0) || (node.Rpm2 > 0) != (r2 > 0)) moved = true;
            node.Rpm1 = r1;
            node.Rpm2 = r2;
        }
        if (moved) Raise(nameof(AnyMotion));   // 桨转起来了：视图据此点火动画拍
    }

    private (int Therm, double Rpm) StationState(int channel)
    {
        if (channel <= 0 || _ws.ChannelOf(channel) is not { } ch) return (0, 0);
        var rpm = ch.Capabilities.Get<IStirrer>()?.CurrentRpm ?? 0;
        // 没有设定值 = 没在控温（也含采样过期、质量不好），什么都不点
        if (!_ws.Pipeline.TryLatest(channel, "Tset", _ws.Clock.Now, out var s)
            || s.Quality is not (Quality.Good or Quality.Simulated)) return (0, rpm);
        if (ch.Capabilities.Get<ITemperatureControl>() is not { } tc) return (0, rpm);
        var d = s.Value - tc.CurrentReactor;
        return (d > 0.5 ? 1 : d < -0.5 ? -1 : 0, rpm);   // 0.5 K 死区：到温了就不再点
    }

    private void Rebind(string deviceId, IReadOnlyList<int> channels, bool exclusive)
    {
        _ws.Bench.Bindings.RemoveAll(b => b.DeviceId == deviceId);
        foreach (var ch in channels)
            _ws.Bench.Bindings.Add(new Binding(deviceId, ch,
                exclusive ? BindingMode.Exclusive : BindingMode.Shared));
    }

    /// <summary>台面上的编号前缀，沿用预置台面的写法（R1/P1/PH1…）。</summary>
    private static string PrefixOf(string artKey) => artKey switch
    {
        "rd105" => "R",
        "feedpump" => "P",
        "phel" => "PH",
        "trprobe" => "TR",
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
            Wells.Add(new ChannelRowViewModel(ch, $"{(i == 0 ? "A" : "B")} 孔 → 通道 CH{n}"));
            i++;
        }
    }

    /// <summary>
    /// 分类的先后。按「先有台面才有别的」排：反应器给出通道，加料和探头都得
    /// 挂到通道上去。空的那一类不出现——不装加料泵的机器不该看见一个空的「加料」。
    /// </summary>
    private static readonly string[] CategoryOrder = { "反应与控温", "加料", "在线检测", "其他" };

    /// <summary>
    /// 设备库只上真机三件：双工位反应主机 + 两支宇电探头。程序里没有仿真，
    /// 库里也就没有仿真设备；加料泵还没有真机驱动，跟着仿真一起下架——
    /// 库里摆一台拖上去只能虚拟加料的泵，就是拿仿真冒充真机。
    /// 单独的 RD105 温控器驱动照旧注册（老台面上摆过的还能开），库里不单列：
    /// 主机把「电加热切换」选「无」就是它。
    /// </summary>
    private static readonly string[] LibraryIds =
    {
        Tec.Drivers.DualStation.DualStationDriver.DriverId,
        Tec.Drivers.DualStation.YudianTrProbeDriver.DriverId,
        Tec.Drivers.DualStation.YudianPhProbeDriver.DriverId
    };

    public void Reload()
    {
        // 台面一动会话全部按当前参数重开——刚改的连接参数这时已经生效，「未生效」的标记撤掉
        _connDirty = false;
        RaiseLink();

        Library.Clear();
        foreach (var p in _ws.Drivers.ForLibrary())
            if (LibraryIds.Contains(p.Id)) Library.Add(new LibraryItemViewModel(p));

        // 收起 / 展开的状态得带过来。Reload 是台面一有风吹草动就跑一遍的
        // （BenchChanged），而设备库本身跟台面上摆了什么无关——不记着的话，
        // 收起一类再拖一台设备下去，那一类自己又弹开了（实测踩到）
        var wasOpen = Groups.ToDictionary(g => g.Name, g => g.Open);
        Groups.Clear();
        foreach (var name in CategoryOrder)
        {
            var items = Library.Where(x => x.Category == name).ToList();
            if (items.Count == 0) continue;
            var g = new DeviceGroup(name);
            if (wasOpen.TryGetValue(name, out var open)) g.Open = open;
            foreach (var it in items) g.Items.Add(it);
            Groups.Add(g);
        }

        // 台面一动全部设备会话重开（RebuildChannelsAsync 的行为），跑着的泵
        // 这一刻物理上已经停了——小窗如实归位到「启动」，不装作还在跑
        foreach (var p in _panels.Values) p.MarkStopped();

        SyncDevices();
        RebuildLinks();
        Raise(nameof(IsEmpty));

        ChannelRows.Clear();
        foreach (var ch in _ws.Channels)
        {
            // 机A · A 孔（原型 devLabel）。运行页的通道磁贴用的是同一句，
            // 所以这句只写一处——两页各写一遍，同一个孔迟早在两页里叫出两个名字
            var host = WellLabel.Of(_ws, ch.Number);

            ChannelRows.Add(new ChannelRowViewModel(ch, host));
        }

        // 画布右下角那句「N 台设备 · M 个通道」得跟着变
        Raise(nameof(BenchSummary));
    }

    /// <summary>
    /// 把节点列表对齐到台面，能复用的就地更新，不整批重建。
    /// 重建过一次的话，正在拖的那个节点就成了孤儿——它照旧在改模型，
    /// 但已经不在画面上了，于是拖动全程设备"钉"在原地，松手后又一次重建才跳过去。
    /// 台面重建是异步回来的，什么时候落地不一定，所以这毛病时有时无。
    /// </summary>
    private void SyncDevices()
    {
        // 台面上已经没有的（或者整份台面被换掉、模型对象都不是原来那个了）先摘掉；
        // 「模拟」开关换过驱动身份的也摘——节点上缓存的 Driver、表单、通道数
        // 全是按旧身份建的，就地改不如整个换
        for (var i = Devices.Count - 1; i >= 0; i--)
        {
            var live = _ws.Bench.Device(Devices[i].Id);
            if (live is null || !ReferenceEquals(live, Devices[i].Device)
                || (Devices[i].Driver?.Info.Id ?? live.DriverId) != live.DriverId)
                Devices.RemoveAt(i);
        }

        for (var i = 0; i < _ws.Bench.Devices.Count; i++)
        {
            var dev = _ws.Bench.Devices[i];
            var driver = _ws.Drivers.Driver(dev.DriverId);
            var chs = driver is { Info.ChannelsPerDevice: > 0 }
                ? _ws.Channels.Where(c => c.HostInstanceId == dev.InstanceId).Select(c => c.Number).ToList()
                : _ws.Bench.Bindings.Where(b => b.DeviceId == dev.InstanceId).Select(b => b.ChannelNumber).ToList();

            var at = -1;
            for (var k = 0; k < Devices.Count; k++)
                if (ReferenceEquals(Devices[k].Device, dev)) { at = k; break; }

            if (at < 0) Devices.Insert(Math.Min(i, Devices.Count), new DeviceNodeViewModel(dev, driver, chs));
            else
            {
                Devices[at].SetChannels(chs);
                if (at != i && i < Devices.Count) Devices.Move(at, i);
            }
        }

        // 插在工位上的探头把位置对回工位（读盘进来的位置可能是旧比例下存的），
        // 图与尺寸也刷一遍——插拔状态可能在这次重建里变了
        foreach (var n in Devices)
        {
            if (!n.Lifted
                && BenchDock.InsertArtFor(n.BaseArtKey, n.Device.DockAnchor) is { } ins
                && BenchDock.AnchorById(n.Device.DockAnchor) is { } a
                && Devices.FirstOrDefault(h => h.Id == n.Device.DockHostId) is { } host)
                n.MoveTo(BenchDock.SnapPosition(ins, a, new Point(host.X, host.Y), host.Width));
            n.DockVisualChanged();
        }

        // 选中的那台如果被摘掉了，右栏得跟着清空
        if (_selected is { } sel && !Devices.Contains(sel)) Selected = null;
    }

    private void BuildForms()
    {
        if (_selected?.Driver is not { } d) { ConnectionForm = null; ConfigForm = null; return; }
        // 串口下拉去问系统这台机器上现在有哪些口（见 SerialPortScan）——
        // 写死一份 COM1~COM6 的话，插着八口 USB 转串的机器一个都选不着。
        // 每次建表单都重扫一遍：插拔之后点一下别的设备再点回来就是最新的
        // 连接参数一改就标「未生效」：会话还拿着旧口子，直到点「连接」重开
        ConnectionForm = new SchemaFormViewModel(d.ConnectionSchema, _selected.Device.Connection,
                                                 changed: () => { _connDirty = true; RaiseLink(); },
                                                 choicesOf: PortChoices);
        ConfigForm = new SchemaFormViewModel(d.ConfigSchema, _selected.Device.Config,
                                             choicesOf: PortChoices);
        ProbeResult = "";
    }

    private static IReadOnlyList<ChoiceOption>? PortChoices(string key, string? current)
        => key == WellKnownChoices.SerialPorts ? SerialPortScan.Options(current) : null;

    /// <summary>
    /// 「连接」：按当前连接参数先探一遍（固件 / 序列号 / 路数回显在按钮上方），
    /// 再把会话重开——探完之后设备就是连着的，不是「测完了还得再连一次」。
    /// 探不通照实说驱动报的原因；名字底下那行链路状态跟着会话重开一起刷新。
    /// </summary>
    private async Task ProbeAsync()
    {
        if (_selected is not { } sel || sel.Driver is null) return;

        ProbeResult = "正在连接…";
        var id = sel.Id;
        var r = await _ws.ReconnectAsync(id);
        ProbeResult = r.Success
            ? $"连接成功：{r.Message}"
              + (string.IsNullOrEmpty(r.Firmware) ? "" : $"；固件 {r.Firmware}")
              + (string.IsNullOrEmpty(r.Serial) ? "" : $"；序列号 {r.Serial}")
              + (r.DetectedChannels is { } n ? $"；探测到 {n} 路" : "")
            : $"连接失败：{r.Message}";
        // 重建把节点整个换过一遍，选回同一台，链路那一行才念得到新会话
        Selected = Devices.FirstOrDefault(d => d.Id == id) ?? Selected;
        _connDirty = false;
        RaiseLink();
    }
}

/// <summary>通道总表的一行（原型 .chtable .row）：色块 · CHn · 来源 · 探头标签 · 启停。</summary>
public sealed class ChannelRowViewModel : ViewModelBase
{
    public ChannelRowViewModel(Channel ch, string host)
    {
        Channel = ch;
        Host = host;
        Capabilities = string.Join("、", ch.Capabilities.All.Select(Friendly).Distinct());
    }

    public Channel Channel { get; }
    public string Name => Channel.Name;
    /// <summary>机A · A 孔（原型 devLabel + 孔位字母）。</summary>
    public string Host { get; }
    // Probes（挂在这个通道上的探头短名，原型 .ptag）撤了：总表里那一列小标签不再显示
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

/// <summary>
/// 插上工位的 Tr / pH 的读数框：白底灰框（#4A4A4A，同新版演示的 tag()），
/// 提示框式地从探头头部引一根线伸出去，框 84×34、字 16——不再按主机比例缩，
/// 缩完只剩 8px 的字看不清（用户提出改成引出式）。
/// 数值见 RefreshTagValues：真数或「—」。
/// </summary>
public sealed class ReadTagViewModel : ViewModelBase
{
    public ReadTagViewModel(string kind, int channel)
    {
        Kind = kind;
        Channel = channel;
    }

    /// <summary>tr / ph，决定去通道上读哪一路。</summary>
    public string Kind { get; }
    public int Channel { get; }

    public double X { get; init; }
    public double Y { get; init; }
    public double W { get; init; }
    public double H { get; init; }
    public double FontSize { get; init; }
    public Thickness BorderW { get; init; }
    public CornerRadius Radius { get; init; }

    /// <summary>引线的两端：探头头顶 → 框沿上的接点（TagLeaders 画）。</summary>
    public double AnchorX { get; init; }
    public double AnchorY { get; init; }
    public double AttachX { get; init; }
    public double AttachY { get; init; }

    private string _text = "—";
    public string Text { get => _text; set => Set(ref _text, value); }
}

/// <summary>
/// 泵控制小窗，1:1 照交互演示的泵控制面板：216×220 白卡、可拖标题栏、
/// 肘形引线连到泵顶、设定速率（大数字 + ±0.05 + 滑杆，0.05–5.00 mL/min）、
/// 累计加料、启动/停止。跟演示差一处（用户定的）：改速率不弹屏幕键盘，
/// 点一下数字直接物理键盘输入，Enter 确定、Esc 取消。
///
/// **每一步操作最后走的都是驱动契约 IDosing**：启动 = SetRateAsync、
/// 停止 = StopAsync、跑着改速率再 SetRateAsync 一次；累计加料读 TotalVolume。
/// 界面不自己攒任何数。
/// </summary>
public sealed class PumpPanelViewModel : ViewModelBase
{
    public const double PanelW = 216, PanelH = 220, TrackW = 104;
    private const double RateMin = 0.05, RateMax = 5.00;

    private readonly BenchViewModel _owner;
    private double _x, _y, _tx, _ty, _rate = 1.20;
    private bool _running, _editing, _closed;
    private string _editText = "", _total = "—";
    private int _slot, _channel;

    public PumpPanelViewModel(BenchViewModel owner, string deviceId)
    {
        _owner = owner;
        DeviceId = deviceId;
        StepDown = new RelayCommand(() => Nudge(-1));
        StepUp = new RelayCommand(() => Nudge(+1));
        ToggleRun = new RelayCommand(Run);
        // 演示的 ×：编辑态下是取消，平时是关窗（点那台泵可再弹出）。
        // 关窗不停泵——藏起一扇窗不该悄悄动执行器
        Close = new RelayCommand(() => { if (Editing) CancelEdit(); else Closed = true; });
    }

    public string DeviceId { get; }
    public double W => PanelW;

    private double _h = PanelH;
    /// <summary>
    /// 卡片的**实际**高度，由视图量了喂回来（SizeChanged）。
    /// 高度不再写死 220：那是演示在它自家字体下排出来的数，换一台机器
    /// 字体渲染高一点，「停止」按钮就顶出白卡了（用户实测截到）。
    /// 卡片让内容自己撑，这个数只服务肘形引线的出线点。
    /// </summary>
    public double H { get => _h; private set => Set(ref _h, value); }

    public void SetMeasuredHeight(double h)
    {
        if (h <= 40) return;
        H = h;
        _owner.ClampPanel(this);   // 实际高度到了再夹一次，免得量完才发现底边出了可视区
    }

    public RelayCommand StepDown { get; }
    public RelayCommand StepUp { get; }
    public RelayCommand ToggleRun { get; }
    public RelayCommand Close { get; }

    public double X { get => _x; set => Set(ref _x, value); }
    public double Y { get => _y; set => Set(ref _y, value); }

    /// <summary>肘形引线的落点（泵顶）。泵挪窝时台面重算喂进来。</summary>
    public double TargetX => _tx;
    public double TargetY => _ty;

    public void SetTarget(double x, double y)
    {
        if (Math.Abs(_tx - x) < 0.01 && Math.Abs(_ty - y) < 0.01) return;
        _tx = x;
        _ty = y;
        RaiseAll(nameof(TargetX), nameof(TargetY));
    }

    public int Slot => _slot;
    public int Channel => _channel;

    public void SetDock(int slot, int channel)
    {
        if (_slot == slot && _channel == channel) return;
        _slot = slot;
        _channel = channel;
        Raise(nameof(Title));
    }

    public string Title => $"进料泵 · 工位 {_slot + 1}";

    public double Rate => _rate;
    public string RateText => _rate.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
    /// <summary>滑杆填充段的宽度与滑块位置，几何同演示（轨 104）。</summary>
    public double FillWidth => (_rate - RateMin) / (RateMax - RateMin) * TrackW;
    public Thickness ThumbMargin => new(FillWidth - 8, 0, 0, 0);

    public bool Running { get => _running; private set { if (Set(ref _running, value)) Raise(nameof(RunLabel)); } }
    public string RunLabel => _running ? "停 止" : "启 动";

    public bool Editing { get => _editing; private set => Set(ref _editing, value); }
    public string EditText { get => _editText; set => Set(ref _editText, value); }

    public bool Closed { get => _closed; set => Set(ref _closed, value); }

    public string TotalText { get => _total; private set => Set(ref _total, value); }
    public void SetTotal(string text) => TotalText = text;

    /// <summary>台面重建把会话换掉了：泵物理上停了，按钮如实回到「启动」。</summary>
    public void MarkStopped() => Running = false;

    // ── 速率 ────────────────────────────────────────────────────────
    private void SetRate(double v)
    {
        v = Math.Clamp(v, RateMin, RateMax);
        if (Math.Abs(v - _rate) < 0.0001) return;
        _rate = v;
        RaiseAll(nameof(Rate), nameof(RateText), nameof(FillWidth), nameof(ThumbMargin));
        if (Running && _owner.DosingFor(this) is { } cap)
            _ = cap.SetRateAsync(_rate, CancellationToken.None);
        _owner.PumpRateChanged(this);
    }

    /// <summary>± 按钮：一格 0.05，跟演示一样吸到 0.05 的整数倍。</summary>
    private void Nudge(int d) => SetRate(Math.Round((_rate + d * 0.05) / 0.05) * 0.05);

    /// <summary>滑杆：x 为指针在轨上的横向位置（0..TrackW），吸 0.05。</summary>
    public void SlideTo(double x)
    {
        var v = RateMin + Math.Clamp(x, 0, TrackW) / TrackW * (RateMax - RateMin);
        SetRate(Math.Round(v / 0.05) * 0.05);
    }

    // ── 编辑（物理键盘直接输入） ─────────────────────────────────────
    public void BeginEdit()
    {
        EditText = RateText;
        Editing = true;
    }

    public void CommitEdit()
    {
        if (double.TryParse(EditText, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var v))
            SetRate(Math.Round(v * 100) / 100);          // 演示 commit 同款：0.01 精度再夹进量程
        Editing = false;
    }

    public void CancelEdit() => Editing = false;

    // ── 启停 ────────────────────────────────────────────────────────
    private void Run()
    {
        var cap = _owner.DosingFor(this);
        if (cap is null) return;                          // 通道还没建好，按了也没有对象可指挥
        if (Running)
        {
            _ = cap.StopAsync(CancellationToken.None);
            Running = false;
        }
        else
        {
            _ = cap.SetRateAsync(_rate, CancellationToken.None);
            Running = true;
        }
        _owner.PumpRunChanged();
    }
}

/// <summary>拖拽时画在画布上的一个接口点。</summary>
public sealed class PortDot
{
    public double X { get; init; }
    public double Y { get; init; }
    public double Size { get; init; } = 12;
    public double Opacity { get; init; } = 1;
    public bool Hot { get; init; }
    public string ColorHex { get; init; } = "#a41626";
    public string Label { get; init; } = "";
    public double Left => X - Size / 2;
    public double Top => Y - Size / 2;
}
