using System.Collections.ObjectModel;
using Tec.App.Services;
using Tec.Core.Benches;
using Tec.Driver.Abi;
using Tec.DriverHost;

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
    public string ChannelText => Channels.Count == 0 ? "未绑定" : string.Join(" · ", Channels.Select(c => "CH" + c));
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
    private LibraryItemViewModel? _picked;

    public BenchViewModel(Workspace ws)
    {
        _ws = ws;
        Probe = new RelayCommand(async () => await ProbeAsync());
        Rebuild = new RelayCommand(async () => await _ws.RebuildChannelsAsync());
        ws.BenchChanged += (_, _) => Reload();
        Reload();
    }

    public ObservableCollection<LibraryItemViewModel> Library { get; } = new();
    public ObservableCollection<DeviceNodeViewModel> Devices { get; } = new();
    public ObservableCollection<ChannelRowViewModel> ChannelRows { get; } = new();

    public RelayCommand Probe { get; }
    public RelayCommand Rebuild { get; }

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
            if (!Set(ref _selected, value)) return;
            BuildForms();
            RaiseAll(nameof(HasSelection), nameof(SelectedTitle), nameof(SelectedDriver), nameof(SelectedSub));
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
        private set => Set(ref _probeResult, value);
    }

    public bool HasSelection => _selected is not null;
    public string SelectedTitle => _selected?.Title ?? "未选中设备";
    public string SelectedDriver => _selected?.Driver?.Info.Name ?? "—";
    public string SelectedSub => _selected is null ? "" : $"{_selected.Id} · {_selected.ChannelText}";

    public string BenchSummary
        => $"{_ws.Bench.Devices.Count} 台设备 · {_ws.Channels.Count} 个通道 · 共享件 {string.Join("、", _ws.Bench.SharedDeviceIds())}";

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
