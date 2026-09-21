using System.Collections.ObjectModel;
using Avalonia.Threading;
using Tec.App.Services;
using Tec.Core.Benches;
using Tec.Driver.Abi;
using Tec.Hmi.Ui.ViewModels;

namespace Tec.App.ViewModels;

/// <summary>
/// 「温控器参数」窗：一台设备（IDeviceSettings）的参数面板。
/// 分组由驱动声明（schema），表单照 schema 渲染——跟属性栏的连接 / 配置表单同一套；
/// 这里多的只是「读取 / 写入 / 动作」三件事和「实时状态」那一组的定时刷新。
///
/// 会话没开（串口没插、还没点「连接」）就没有 IDeviceSettings 可用：面板如实写「未连接：原因」，
/// 分组一个都不摆——摆出来的每个数都得是设备此刻的值，没连上就没有值。
/// </summary>
public sealed class DeviceSettingsViewModel : ViewModelBase, IDisposable
{
    private readonly Workspace _ws;
    private readonly string _deviceId;
    private readonly DispatcherTimer _timer;
    private IDeviceSettings? _dev;
    private SettingsGroupViewModel? _selected;
    private string _status = "";

    public DeviceSettingsViewModel(Workspace ws, string deviceId, string label)
    {
        _ws = ws;
        _deviceId = deviceId;
        DeviceLabel = label;
        _ws.BenchChanged += OnBenchChanged;
        Resolve();
        // 实时状态两秒一刷：温度、电流、告警都是慢变量，串口上还排着会话自己的轮询
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => _ = TickAsync());
        _timer.Start();
    }

    public string DeviceLabel { get; }

    /// <summary>链路状态那一句（跟属性栏、HMI 念的是同一句）。</summary>
    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public bool Available => _dev is not null;
    public bool Unavailable => _dev is null;

    public ObservableCollection<SettingsGroupViewModel> Groups { get; } = new();

    public SettingsGroupViewModel? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            Raise(nameof(HasSelected));
            if (value is { Loaded: false }) _ = value.ReadAsync();      // 第一次点开这一组就读一遍
        }
    }

    public bool HasSelected => _selected is not null;

    private void OnBenchChanged(object? sender, EventArgs e) => Resolve();

    /// <summary>台面一动会话就重开：重新找这台设备的 IDeviceSettings，分组重建。</summary>
    private void Resolve()
    {
        var session = _ws.Session(_deviceId);
        var (text, _) = DeviceLink.Describe(session, _ws.OpenFailure(_deviceId));
        var dev = session as IDeviceSettings;
        if (session is not null && dev is null) text += "——这台设备的驱动没有参数面板";
        if (_ws.Bench.Devices.All(d => d.InstanceId != _deviceId)) text = "这台设备已不在台面上";
        Status = text;

        if (ReferenceEquals(dev, _dev)) return;
        _dev = dev;
        var keep = _selected?.Id;
        Groups.Clear();
        Selected = null;
        if (dev is not null)
            foreach (var g in dev.Groups)
                Groups.Add(new SettingsGroupViewModel(dev, g, dev.Actions.Where(a => a.Group == g.Id).ToList()));
        Selected = Groups.FirstOrDefault(g => g.Id == keep) ?? Groups.FirstOrDefault();
        RaiseAll(nameof(Available), nameof(Unavailable));
    }

    private async Task TickAsync()
    {
        if (_selected is { Live: true, Busy: false } g) await g.ReadAsync(quiet: true);
    }

    public void Dispose()
    {
        _timer.Stop();
        _ws.BenchChanged -= OnBenchChanged;
    }
}

/// <summary>面板上的一组：一张 schema 表单 + 读取 / 写入 + 这一组下面的动作钮。</summary>
public sealed class SettingsGroupViewModel : ViewModelBase
{
    private readonly IDeviceSettings _dev;
    private readonly SettingsGroup _group;
    private ParameterSet _last = new();
    private string _notes = "";
    private bool _busy;

    public SettingsGroupViewModel(IDeviceSettings dev, SettingsGroup group, IReadOnlyList<SettingsAction> actions)
    {
        _dev = dev;
        _group = group;
        Target = new ParameterSet();
        Form = new SchemaFormViewModel(group.Schema, Target);
        Actions = actions.Select(a => new SettingsActionViewModel(this, a)).ToList();
        Read = new RelayCommand(() => _ = ReadAsync(), () => !Busy);
        Write = new RelayCommand(() => _ = WriteAsync(), () => !Busy && !Live);
    }

    public string Id => _group.Id;
    public string Title => _group.Title;
    public bool Live => _group.Live;
    public bool Writable => !_group.Live;
    public string? Tip => _group.Tip;
    public bool HasTip => !string.IsNullOrWhiteSpace(_group.Tip);
    public ParameterSet Target { get; }
    public SchemaFormViewModel Form { get; }
    public IReadOnlyList<SettingsActionViewModel> Actions { get; }
    public bool HasActions => Actions.Count > 0;
    public RelayCommand Read { get; }
    public RelayCommand Write { get; }

    /// <summary>读过一次没有。没读过的表单里是 schema 缺省值，不是设备的值——所以点开先读。</summary>
    public bool Loaded { get; private set; }

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (!Set(ref _busy, value)) return;
            Read.Refresh();
            Write.Refresh();
        }
    }

    /// <summary>回执：读了什么时候、写了什么、设备拒了什么。</summary>
    public string Notes
    {
        get => _notes;
        private set => Set(ref _notes, value);
    }

    public async Task ReadAsync(bool quiet = false)
    {
        if (Busy) return;
        Busy = true;
        try
        {
            var p = await _dev.ReadAsync(Id, CancellationToken.None);
            foreach (var kv in p) Target[kv.Key] = kv.Value;
            _last = p.Clone();
            Loaded = true;
            Form.RefreshValues();
            if (!quiet) Notes = $"已从温控器读取 {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            Notes = $"读取失败：{ex.Message}";
        }
        finally { Busy = false; }
    }

    /// <summary>只写改过的项：跟上次读回来的对一遍，一样的不发——每写一项都是一次串口往返。</summary>
    public async Task WriteAsync()
    {
        if (Busy || Live) return;
        var changed = new ParameterSet();
        foreach (var f in _group.Schema.Fields)
        {
            if (f.ReadOnly) continue;
            var now = Target[f.Key];
            var was = _last[f.Key];
            if (!Same(now, was)) changed[f.Key] = now;
        }
        if (changed.Count == 0) { Notes = "没有改动，不用写"; return; }

        Busy = true;
        try
        {
            var notes = await _dev.WriteAsync(Id, changed, CancellationToken.None);
            Notes = string.Join("\n", notes);
        }
        catch (Exception ex)
        {
            Notes = $"写入失败：{ex.Message}";
        }
        finally { Busy = false; }
        // 写完读回来：表单上摆的必须是设备此刻的值，不是刚才填的
        await ReadAsync(quiet: true);
    }

    public async Task RunAsync(SettingsAction action)
    {
        if (Busy) return;
        Busy = true;
        try { Notes = await _dev.RunAsync(action.Id, CancellationToken.None); }
        catch (Exception ex) { Notes = $"{action.Label} 失败：{ex.Message}"; }
        finally { Busy = false; }
        if (Live) await ReadAsync(quiet: true);
    }

    private static bool Same(object? a, object? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a is string sa && b is string sb) return string.Equals(sa, sb, StringComparison.Ordinal);
        if (double.TryParse(Convert.ToString(a, System.Globalization.CultureInfo.InvariantCulture), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var da)
            && double.TryParse(Convert.ToString(b, System.Globalization.CultureInfo.InvariantCulture), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var db))
            return Math.Abs(da - db) < 1e-9;
        return Equals(a, b);
    }
}

/// <summary>一颗动作钮。要确认的（自整定）由窗口弹框问过再调 RunAsync。</summary>
public sealed class SettingsActionViewModel
{
    public SettingsActionViewModel(SettingsGroupViewModel group, SettingsAction action)
    {
        Group = group;
        Action = action;
    }

    public SettingsGroupViewModel Group { get; }
    public SettingsAction Action { get; }
    public string Label => Action.Label;
    public string? Tip => Action.Tip;
    public bool Confirm => Action.Confirm;
    public Task RunAsync() => Group.RunAsync(Action);
}
