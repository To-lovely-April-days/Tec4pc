using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Avalonia.Threading;
using Tec.Driver.Abi;
using Tec.LimitTest.Records;
using Tec.LimitTest.Runs;

namespace Tec.LimitTest;

public abstract class Bindable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>台面上一台设备的连接参数（照驱动的 ConnectionSchema 摆，值当文本编辑）。</summary>
public sealed class FieldRow : Bindable
{
    private string _value = "";
    public FieldRow(string key, string label, string value) { Key = key; Label = label; _value = value; }
    public string Key { get; }
    public string Label { get; }
    public string Value { get => _value; set => Set(ref _value, value); }
}

public sealed class DeviceRow : Bindable
{
    private string _status = "";
    public DeviceRow(string id, string label) { Id = id; Label = label; }
    public string Id { get; }
    public string Label { get; }
    public string Status { get => _status; set => Set(ref _status, value); }
    public ObservableCollection<FieldRow> Fields { get; } = new();
}

/// <summary>矩阵里的一格。</summary>
public sealed class CellRow : Bindable
{
    private bool _selected = true;
    private string _stateText = "待测";
    private string _note = "";
    private bool _canEdit = true;
    private IBrush _fill = Brushes.White;

    public CellRow(TestKind kind, double water)
    {
        Kind = kind;
        Water = water;
    }

    public TestKind Kind { get; }
    /// <summary>水温键；NaN = 不用冷却水的那格（最高温只测一次）。</summary>
    public double Water { get; }
    public string Title => double.IsNaN(Water)
        ? $"{TestKinds.Name(Kind)} · 无冷却水（只测一次）"
        : $"{TestKinds.Name(Kind)} · 冷却水 {Water:0.#} ℃";

    /// <summary>同一格（项 + 水温键；NaN 跟 NaN 算同一个）。</summary>
    public bool Is(TestKind kind, double water) => Kind == kind && (double.IsNaN(Water) ? double.IsNaN(water) : Water == water);
    public bool Selected { get => _selected; set => Set(ref _selected, value); }
    public string StateText { get => _stateText; set => Set(ref _stateText, value); }
    public string Note { get => _note; set => Set(ref _note, value); }
    public bool CanEdit { get => _canEdit; set => Set(ref _canEdit, value); }
    public IBrush Fill { get => _fill; set => Set(ref _fill, value); }

    public void From(PlanCell c)
    {
        StateText = c.StateText;
        Note = c.Note;
        Fill = c.State switch
        {
            CellState.Running => new SolidColorBrush(Color.Parse("#E3F2FD")),
            CellState.Done or CellState.EndedEarly => new SolidColorBrush(Color.Parse("#E8F5E9")),
            CellState.Aborted => new SolidColorBrush(Color.Parse("#FFEBEE")),
            CellState.Skipped or CellState.Unselected => new SolidColorBrush(Color.Parse("#F5F5F5")),
            _ => Brushes.White
        };
    }
}

/// <summary>
/// 主窗的状态。所有能改的东西都是字符串（开始时再解析、解析不了当场说），
/// 跑起来之后矩阵和参数锁住，Runner 的状态每秒刷一遍。
/// </summary>
public sealed class MainViewModel : Bindable
{
    private readonly ToolHost _host;
    private readonly DispatcherTimer _timer;
    private LimitTestRunner? _runner;
    private CancellationTokenSource? _stop;
    private Task? _run;
    private bool _busy;

    /// <summary>程序版本 = 出包时填的补丁号（csproj 的 InformationalVersion），标题栏和「补丁版本」缺省值都用它。</summary>
    public static string ToolVersion { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var asm = typeof(MainViewModel).Assembly;
        var info = asm.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                      .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                      .FirstOrDefault()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            var plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }
        return asm.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "?";
    }

    public string Heading => $"控温极限测试 · 版本 {ToolVersion}";

    public MainViewModel(ToolHost host)
    {
        _host = host;
        _outDir = host.OutDir;
        _patch = ToolVersion;
        RebuildCells();
        LoadDevices();
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Tick());
        _timer.Start();
        Tick();
    }

    // ── 连接 ──────────────────────────────────────────────────────

    public ObservableCollection<DeviceRow> Devices { get; } = new();
    public string BenchPathText => $"台面文件：{_host.BenchPath}";

    private string _portsHint = "";
    public string PortsHint { get => _portsHint; set => Set(ref _portsHint, value); }

    private string _connStatus = "未连接";
    public string ConnStatus { get => _connStatus; set => Set(ref _connStatus, value); }

    public bool CanConnect => !_busy && !IsRunning;

    private void LoadDevices()
    {
        Devices.Clear();
        var rt = _host.Runtime;
        if (rt is null) return;
        foreach (var dev in rt.Bench.Devices)
        {
            var row = new DeviceRow(dev.InstanceId, $"{dev.Display}（{dev.InstanceId}）");
            var drv = rt.Drivers.Driver(dev.DriverId);
            if (drv is null) row.Status = $"驱动 {dev.DriverId} 不存在";
            else
                foreach (var f in drv.ConnectionSchema.Fields)
                {
                    var cur = dev.Connection.Has(f.Key) ? dev.Connection.Str(f.Key)
                            : f.Default is { } d ? Convert.ToString(d, CultureInfo.InvariantCulture) ?? "" : "";
                    row.Fields.Add(new FieldRow(f.Key, f.Label, cur));
                }
            Devices.Add(row);
        }
        try
        {
            var ports = System.IO.Ports.SerialPort.GetPortNames().OrderBy(SerialPortChoices.NumberIn).ToArray();
            PortsHint = ports.Length == 0 ? "本机没检测到串口" : "本机串口：" + string.Join("、", ports);
        }
        catch (Exception ex) { PortsHint = "串口枚举失败：" + ex.Message; }
        RefreshDeviceStatus();
    }

    private void RefreshDeviceStatus()
    {
        var rt = _host.Runtime;
        foreach (var row in Devices)
        {
            if (rt is null) { row.Status = ""; continue; }
            if (!_host.Started) { row.Status = "未连接"; continue; }
            var s = rt.Session(row.Id);
            row.Status = s is null
                ? "未连接：" + (rt.OpenFailure(row.Id) ?? "没打开")
                : $"已连接 · {s.State}";
        }
    }

    /// <summary>把界面上的连接参数写回台面、存盘、连接（连过就是重连）。</summary>
    public async Task ConnectAsync()
    {
        if (_busy || IsRunning) return;
        _busy = true;
        Raise(nameof(CanConnect));
        ConnStatus = "正在连接…";
        try
        {
            var rt = _host.Runtime;
            if (rt is not null)
                foreach (var row in Devices)
                {
                    var dev = rt.Bench.Device(row.Id);
                    if (dev is null) continue;
                    foreach (var f in row.Fields) dev.Connection[f.Key] = f.Value;
                }
            _host.SaveBench();
            await _host.ConnectAsync();
            var rig = _host.Rig;
            ConnStatus = rig is null ? "连接失败：主机没开起来（看日志）" : $"已连接：{rig.Describe}";
        }
        catch (Exception ex)
        {
            ConnStatus = "连接失败：" + ex.Message;
            _host.Log("error", "连接失败：" + ex.Message);
        }
        finally
        {
            _busy = false;
            LoadDevices();
            Raise(nameof(CanConnect));
            Raise(nameof(CanStart));
        }
    }

    public async Task ImportBenchAsync(string path)
    {
        if (_busy || IsRunning) return;
        _busy = true;
        try
        {
            await _host.ImportBenchAsync(path);
            ConnStatus = "台面已导入，未连接";
        }
        catch (Exception ex) { ConnStatus = "导入失败：" + ex.Message; }
        finally
        {
            _busy = false;
            LoadDevices();
            Raise(nameof(BenchPathText));
            Raise(nameof(CanConnect));
            Raise(nameof(CanStart));
        }
    }

    // ── 条件与参数（都是文本，开始时解析）──────────────────────────

    private string _operator = "";
    public string OperatorName { get => _operator; set => Set(ref _operator, value); }
    private string _ambient = "";
    public string AmbientText { get => _ambient; set => Set(ref _ambient, value); }
    private string _patch;
    public string PatchText { get => _patch; set => Set(ref _patch, value); }
    private string _outDir;
    public string OutDir { get => _outDir; set => Set(ref _outDir, value); }

    private string _minTarget = "-40";
    public string MinTargetText { get => _minTarget; set => Set(ref _minTarget, value); }
    private string _maxTarget = "150";
    public string MaxTargetText { get => _maxTarget; set => Set(ref _maxTarget, value); }
    private string _holdTarget = "25";
    public string HoldTargetText { get => _holdTarget; set => Set(ref _holdTarget, value); }
    private string _minMinutes = "60";
    public string MinMinutesText { get => _minMinutes; set => Set(ref _minMinutes, value); }
    private string _maxMinutes = "60";
    public string MaxMinutesText { get => _maxMinutes; set => Set(ref _maxMinutes, value); }
    private string _holdMinutes = "120";
    public string HoldMinutesText { get => _holdMinutes; set => Set(ref _holdMinutes, value); }
    private string _settle = "0.2";
    public string SettleText { get => _settle; set => Set(ref _settle, value); }
    private string _returnTemp = "25";
    public string ReturnTempText { get => _returnTemp; set => Set(ref _returnTemp, value); }
    private string _stability = "30";
    /// <summary>恒温稳定度按最后这么多分钟的每秒数据算。</summary>
    public string StabilityText { get => _stability; set => Set(ref _stability, value); }

    /// <summary>控温对象：0 = 夹套 Tj（单环），1 = 釜内 Tr（上位机串级）。</summary>
    private int _objectIndex;
    public int ObjectIndex { get => _objectIndex; set => Set(ref _objectIndex, value); }
    private TempChannelKind ObjectKind => ObjectIndex == 1 ? TempChannelKind.Reactor : TempChannelKind.Jacket;

    /// <summary>冷却水温度列表（℃，逗号分开，顺序就是跑的顺序）。改了矩阵跟着重排；跑着的时候锁住。</summary>
    private string _waters = "20, 12, 7";
    public string WatersText
    {
        get => _waters;
        set { if (Set(ref _waters, value) && !IsRunning) RebuildCells(); }
    }

    public bool CanEditPlan => !IsRunning;

    public ObservableCollection<CellRow> Cells { get; } = new();

    /// <summary>解析水温列表：逗号 / 顿号 / 空格分开；不低于 7 ℃（冷水机的下限，用户定的）；不重复。</summary>
    private static double[]? ParseWaters(string text, out string? error)
    {
        error = null;
        var parts = text.Split(new[] { ',', '，', '、', ';', '；', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var list = new List<double>();
        foreach (var p in parts)
        {
            if (!double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            { error = $"冷却水温度填的不是数：{p}"; return null; }
            if (v < 7) { error = $"冷却水温度 {v:0.#} ℃ 低于 7 ℃（冷水机的下限，你定的）"; return null; }
            if (list.Contains(v)) { error = $"冷却水温度 {v:0.#} ℃ 填重了"; return null; }
            list.Add(v);
        }
        if (list.Count == 0) { error = "冷却水温度一个都没填"; return null; }
        return list.ToArray();
    }

    /// <summary>按水温列表重排矩阵：每个水温 最低温 + 恒温，最后一格是不用水的最高温（只测一次）。已勾的状态尽量保留。</summary>
    private void RebuildCells()
    {
        if (ParseWaters(WatersText, out _) is not { } waters) return;     // 填到一半：先不动，开始时再说
        var prev = Cells.ToList();
        bool Sel(TestKind k, double w) => prev.FirstOrDefault(c => c.Is(k, w))?.Selected ?? true;
        Cells.Clear();
        foreach (var w in waters)
        {
            Cells.Add(new CellRow(TestKind.MinTemp, w) { Selected = Sel(TestKind.MinTemp, w) });
            Cells.Add(new CellRow(TestKind.Hold, w) { Selected = Sel(TestKind.Hold, w) });
        }
        Cells.Add(new CellRow(TestKind.MaxTemp, BookSpec.NoWater) { Selected = Sel(TestKind.MaxTemp, BookSpec.NoWater) });
    }

    // ── 运行 ──────────────────────────────────────────────────────

    public bool IsRunning => _run is { IsCompleted: false };
    public bool CanStart => !_busy && !IsRunning && _host.Rig is not null;
    public bool CanStop => IsRunning;
    public bool CanSkip => IsRunning && _runner?.State is RunnerState.Running or RunnerState.Returning or RunnerState.WaitingWater;
    public bool IsWaitingWater => IsRunning && _runner?.State == RunnerState.WaitingWater;

    private string _status = "未开始";
    public string StatusText { get => _status; set => Set(ref _status, value); }
    private string _phase = "";
    public string PhaseText { get => _phase; set => Set(ref _phase, value); }
    private string _prompt = "";
    public string PromptText { get => _prompt; set => Set(ref _prompt, value); }
    /// <summary>等人的那一组要不要填实际水温（最高温那组不用水，只要点「继续」）。</summary>
    private bool _promptNeedsWater = true;
    public bool PromptNeedsWater { get => _promptNeedsWater; set => Set(ref _promptNeedsWater, value); }
    private string _water = "";
    public string WaterText { get => _water; set => Set(ref _water, value); }
    private string _bookPath = "";
    public string BookPathText { get => _bookPath; set => Set(ref _bookPath, value); }
    private string _aText = "A：—";
    public string AText { get => _aText; set => Set(ref _aText, value); }
    private string _bText = "B：—";
    public string BText { get => _bText; set => Set(ref _bText, value); }
    public ObservableCollection<string> LogLines { get; } = new();

    /// <summary>解析界面上的数，解析不了就说哪一项。</summary>
    private TestSettings? Parse(out string? error)
    {
        error = null;
        double D(string text, string what, ref string? err)
        {
            if (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return v;
            err ??= $"「{what}」填的不是数：{text}";
            return 0;
        }
        var s = new TestSettings
        {
            Operator = OperatorName.Trim(),
            Patch = PatchText.Trim(),
            MinTarget = D(MinTargetText, "最低温目标", ref error),
            MaxTarget = D(MaxTargetText, "最高温目标", ref error),
            HoldTarget = D(HoldTargetText, "恒温目标", ref error),
            MinMinutes = (int)D(MinMinutesText, "最低温时长", ref error),
            MaxMinutes = (int)D(MaxMinutesText, "最高温时长", ref error),
            HoldMinutes = (int)D(HoldMinutesText, "恒温时长", ref error),
            SettleBand = D(SettleText, "提前结束阈值", ref error),
            ReturnTemp = D(ReturnTempText, "回温目标", ref error),
            StabilityWindowMinutes = (int)D(StabilityText, "稳定度窗口", ref error),
            Object = ObjectKind,
            MaxOnce = true
        };
        if (!string.IsNullOrWhiteSpace(AmbientText)) s.Ambient = D(AmbientText, "环境温度", ref error);
        if (error is not null) return null;
        if (ParseWaters(WatersText, out var werr) is not { } waters) { error = werr; return null; }
        s.Waters = waters;
        if (s.MinMinutes < 1 || s.MaxMinutes < 1 || s.HoldMinutes < 1) { error = "时长至少 1 min"; return null; }
        if (s.StabilityWindowMinutes < 1) { error = "稳定度窗口至少 1 min"; return null; }
        if (string.IsNullOrWhiteSpace(s.Operator)) { error = "操作人没填"; return null; }
        if (Cells.All(c => !c.Selected)) { error = "矩阵里一格都没勾"; return null; }
        if (_host.Rig is { } rig)
        {
            for (var w = 0; w < 2; w++)
                if (rig.Limits(w) is { } lim)
                {
                    if (s.MinTarget < lim.Min || s.MaxTarget > lim.Max)
                    {
                        error = $"目标超出设备范围 {lim.Min:0.#}~{lim.Max:0.#} ℃（温控器的超温上下限）";
                        return null;
                    }
                }
            // 控釜内：被控量是宇电的 Tr，此刻就得有读数——没有的话驱动也会拒绝，这里先说清楚
            if (s.Object == TempChannelKind.Reactor)
            {
                var missing = Enumerable.Range(0, 2).Where(w => rig.Read(w).Tr is null).Select(w => w == 0 ? "A" : "B").ToArray();
                if (missing.Length > 0)
                {
                    error = $"控釜内要有釜内 Tr 读数，工位 {string.Join("、", missing)} 现在没有（宇电探头没接 / 探头会话没连上）；先接好探头，或改控夹套";
                    return null;
                }
            }
        }
        return s;
    }

    public void Start()
    {
        if (!CanStart) return;
        var s = Parse(out var error);
        if (s is null)
        {
            StatusText = "没开始：" + error;
            return;
        }
        var rig = _host.Rig!;
        _runner = new LimitTestRunner(rig, s, OutDir.Trim(), log: _host.Log);
        foreach (var c in _runner.Plan)
            c.Selected = Cells.FirstOrDefault(x => x.Is(c.Kind, c.Water))?.Selected ?? false;
        foreach (var c in Cells) c.CanEdit = false;
        _runner.Changed += () => Dispatcher.UIThread.Post(Refresh);
        _stop = new CancellationTokenSource();
        var token = _stop.Token;
        var runner = _runner;
        _run = Task.Run(async () =>
        {
            try { await runner.RunAsync(token); }
            catch (Exception ex)
            {
                _host.Log("error", "极限测试没跑起来：" + ex.Message);
                Dispatcher.UIThread.Post(() => StatusText = "没开始：" + ex.Message);
            }
        });
        _run.ContinueWith(_ => Dispatcher.UIThread.Post(() =>
        {
            foreach (var c in Cells) c.CanEdit = true;
            Refresh();
        }), TaskScheduler.Default);
        Refresh();
    }

    public void Stop() => _stop?.Cancel();

    public void Skip() => _runner?.SkipCurrent();

    public void EmergencyStop()
    {
        if (_runner is { } r && IsRunning) _ = r.EmergencyStopAsync();
        else if (_host.Rig is { } rig)
            _ = Task.Run(async () =>
            {
                try
                {
                    var did = await rig.SafeStopAsync(CancellationToken.None);
                    _host.Log("warn", did.Count == 0 ? "急停：设备报没有可停的输出" : "急停：" + string.Join("；", did));
                }
                catch (Exception ex) { _host.Log("error", "急停时设备报错：" + ex.Message); }
            });
    }

    public void Continue()
    {
        if (_runner is not { } r || !IsWaitingWater) return;
        if (!r.PromptNeedsWater)
        {
            // 最高温那组不用水：点「继续」就是「水已停」
            r.ConfirmWater(double.NaN);
            return;
        }
        if (!double.TryParse(WaterText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
        {
            StatusText = "实际水温填的不是数：" + WaterText;
            return;
        }
        r.ConfirmWater(v);
        WaterText = "";
    }

    /// <summary>把 Runner 的状态搬到界面（界面线程上调）。</summary>
    private void Refresh()
    {
        if (_runner is { } r)
        {
            foreach (var pc in r.Plan)
                Cells.FirstOrDefault(c => c.Is(pc.Kind, pc.Water))?.From(pc);
            StatusText = r.Status;
            PromptText = r.Prompt ?? "";
            PromptNeedsWater = r.PromptNeedsWater;
            var el = r.Elapsed;
            PhaseText = r.Current is { } cur
                ? $"{cur} · {r.Phase} · 已 {(int)el.TotalMinutes} min {el.Seconds:00} s"
                : r.State == RunnerState.Returning ? $"回温 · 已 {(int)el.TotalMinutes} min {el.Seconds:00} s" : "";
            BookPathText = r.BookPath is { } p ? $"记录表：{p}" : "";
        }
        Raise(nameof(IsRunning));
        Raise(nameof(CanStart));
        Raise(nameof(CanStop));
        Raise(nameof(CanSkip));
        Raise(nameof(CanConnect));
        Raise(nameof(IsWaitingWater));
        Raise(nameof(CanEditPlan));
    }

    private void Tick()
    {
        if (_host.Rig is { } rig)
        {
            AText = "A：" + Fmt(rig.Read(0));
            BText = "B：" + Fmt(rig.Read(1));
        }
        else
        {
            AText = "A：—（未连接）";
            BText = "B：—（未连接）";
        }
        if (IsRunning) Refresh();
        RefreshDeviceStatus();
        var tail = _host.Tail(200);
        if (tail.Count != LogLines.Count || (tail.Count > 0 && LogLines.Count > 0 && !LogLines[^1].EndsWith(tail[^1].Text, StringComparison.Ordinal)))
        {
            LogLines.Clear();
            foreach (var e in tail) LogLines.Add($"{e.At:HH:mm:ss} {e.LevelWord} {e.Text}");
        }
    }

    private static string Fmt(WellReading r)
    {
        string N(double? v, string fmt, string unit) => v is { } x ? x.ToString(fmt, CultureInfo.InvariantCulture) + unit : "—";
        return $"夹套 {N(r.Tj, "0.0", " ℃")} · 釜内 {N(r.Tr, "0.00", " ℃")} · 输出 {N(r.Out, "0.0", " %")} · 电流 {N(r.Cur, "0.00", " A")}"
             + $" · 热源 {r.Source ?? "—"} · 功率线 {(r.TecPower is { } p ? (p ? "通" : "断") : "—")}";
    }

    public void Shutdown()
    {
        _timer.Stop();
        _stop?.Cancel();
        try { _run?.Wait(TimeSpan.FromSeconds(5)); } catch { }
    }
}
