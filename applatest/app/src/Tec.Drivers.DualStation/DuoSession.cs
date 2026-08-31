using Tec.Driver.Abi;
using Tec.Drivers.DualStation.Yudian;
using Tec.Drivers.Rd105;
using F = Tec.Drivers.DualStation.DualStationDriver.Fields;

namespace Tec.Drivers.DualStation;

/// <summary>
/// 双工位反应主机的组合会话：四个模块拼成**一台设备、两个通道**（需求 §1）。
///
/// 里面套着一个 Rd105Session 干夹套控温和 Tj/Tset/duty/fault 的采集（原样转发），
/// 自己再开一个采集循环轮宇电（Tr / pH）和 IO8R（热源切换状态与回切），
/// 并把 Tr 喂给每个工位的控温能力——WaitReached 按釜内判就是靠这口饭。
///
/// 热源切换（需求 §2）：
///  · 切入电加热按目标判（目标 &gt; 阈值就切，不等夹套爬到阈值再换挡）；
///  · 切回 TEC 要目标 ≤ 阈值 **且** 实测夹套 ≤ 阈值 − 滞回——夹套还烫着就把
///    TEC 接回去，等于让它贴着超出耐温的热源；
///  · 每次切换都走全套序列：关输出 → 切继电器 → （核反馈）→ 重开输出。
/// </summary>
public sealed class DuoSession : IDeviceSession
{
    private readonly DuoLinks _links;
    private readonly DriverContext _ctx;
    private readonly Broadcast<Sample> _out = new();
    private readonly Rd105Session _rd;
    private readonly IDisposable _fwd;
    private readonly DuoTempControl[] _temps;
    private readonly SemaphoreSlim _switchLock = new(1, 1);

    private readonly double _threshold;     // 电加热切换阈值（≤ 90，用户定的死上限）
    private readonly double _hyst;          // 回切滞回
    private readonly bool _feedback;        // 有没有接切换反馈 DI
    private readonly int[] _mapJ7 = new int[2];
    private readonly int[] _mapJ4 = new int[2];
    private readonly int[] _doIdx = new int[2];
    private readonly int[] _diIdx = new int[2];
    private readonly TimeSpan _tick;

    private readonly bool[] _electric = new bool[2];
    private bool _phOk;
    private bool _ioOk;
    private int _j7Fails, _j4Fails, _ioFails;

    private DeviceState _state = DeviceState.Connected;
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;

    public DuoSession(DuoLinks links, DriverContext ctx, ParameterSet connection)
    {
        _links = links;
        _ctx = ctx;
        var cfg = ctx.Config;

        // 阈值上限 90 是死的（用户定的：只能比 90 小）——表单已经限了，这里再拴一道
        _threshold = Math.Min(90, cfg.Num(F.Threshold, 90));
        _hyst = Math.Clamp(cfg.Num(F.Hysteresis, 5), 2, 20);
        _feedback = cfg.Str(F.Feedback, "无") == "有";
        for (var w = 0; w < 2; w++)
        {
            _mapJ7[w] = Math.Clamp((int)cfg.Num(w == 0 ? F.J7ChA : F.J7ChB, w + 1), 1, 4);
            _mapJ4[w] = Math.Clamp((int)cfg.Num(w == 0 ? F.J4ChA : F.J4ChB, w + 1), 1, 4);
            _doIdx[w] = Math.Clamp((int)cfg.Num(w == 0 ? F.DoA : F.DoB, w), 0, 7);
            _diIdx[w] = Math.Clamp((int)cfg.Num(w == 0 ? F.DiA : F.DiB, w), 0, 7);
        }
        _tick = TimeSpan.FromMilliseconds(Math.Clamp(connection.Num(F.Tick, 1000), 100, 5000));

        // 内层 RD105 会话：夹套控温 + Tj/Tset/duty/fault 采集，原样转发到本会话的流上
        _rd = new Rd105Session(links.Rd105, new DriverContext
        {
            InstanceId = ctx.InstanceId,
            ChannelNumbers = ctx.ChannelNumbers,
            Config = cfg,
            Simulated = false,
            TimeScale = 1,
            Clock = ctx.Clock,
            Log = ctx.Log
        }, connection);
        _fwd = _rd.Samples.Subscribe(new Fwd(_out));
        _rd.StateChanged += (_, st) => { if (st != DeviceState.Disposed) State = st; };

        _temps = new[] { new DuoTempControl(this, 0), new DuoTempControl(this, 1) };
    }

    public string InstanceId => _ctx.InstanceId;

    public DeviceState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            StateChanged?.Invoke(this, value);
        }
    }

    public event EventHandler<DeviceState>? StateChanged;

    public IObservable<Sample> Samples => _out;

    public int WellCount => 2;

    public IReadOnlyList<TagDescriptor> Tags { get; } = new[]
    {
        new TagDescriptor("Tr", "釜内温度", "℃", DataShape.Scalar)
            { Nominal = new ValueRange(-40, 180) },
        new TagDescriptor("Tj", "夹套温度", "℃", DataShape.Scalar)
            { Nominal = new ValueRange(-40, 180) },
        new TagDescriptor("dT", "Tr−Tj 温差", "℃", DataShape.Scalar)
            { Nominal = new ValueRange(-60, 60) },
        new TagDescriptor("Tset", "设定温度", "℃", DataShape.Scalar)
            { Nominal = new ValueRange(-40, 180) },
        new TagDescriptor("duty", "控温输出", "%", DataShape.Scalar)
            { Nominal = new ValueRange(-100, 100) },
        new TagDescriptor("pH", "pH", "", DataShape.Scalar)
            { Nominal = new ValueRange(0, 14) },
        // 热源：0 = TEC，1 = 电加热。发成一路状态量，记录里看得出什么时候换的挡
        new TagDescriptor("heat", "热源", "", DataShape.State)
            { Nominal = new ValueRange(0, 1) },
        new TagDescriptor("fault", "设备告警字", "", DataShape.State)
            { Nominal = new ValueRange(0, 0) }
    };

    public IReadOnlyList<ICapability> CapabilitiesOf(int well)
        => well is 0 or 1
            ? new ICapability[] { _temps[well] }
                .Concat(_rd.CapabilitiesOf(well).OfType<ITemperatureTuning>()).ToArray()
            : Array.Empty<ICapability>();

    public ICommandHandler? Resolve(string commandId) => null;

    internal Rd105TemperatureControl InnerTemp(int well) => _rd.TempOf(well);

    // ── 开机 ─────────────────────────────────────────────────────────

    /// <summary>
    /// 开机自检（需求 §2.6 的降级表就落在这里）：
    /// RD105 写保护寄存器；宇电 J7 过自检——它是 Tr 的根，接反/规格不认识直接开不了机；
    /// J4 和 IO8R 坏了照常开，但各自如实降级（pH 不发数 / 电加热不可用）。
    /// </summary>
    public async Task InitAsync(CancellationToken ct)
    {
        await _rd.ApplyProtectionAsync(ct).ConfigureAwait(false);

        var id = await _links.TempMod.InitAsync(YudianKind.Thermal, ct).ConfigureAwait(false);
        for (var w = 0; w < 2; w++)
        {
            var chSetup = id.Channels[_mapJ7[w] - 1];
            if (!chSetup.Enabled)
                throw new InvalidOperationException(
                    $"宇电温度模块 CH{_mapJ7[w]} 是关闭的（In=0），工位 {AB(w)} 的釜内 Tr 没有来源——" +
                    "查通道映射配置或模块参数");
            if (chSetup.Problem is { } p)
                throw new InvalidOperationException($"宇电温度模块 CH{_mapJ7[w]}（工位 {AB(w)} 的 Tr）：{p}");
        }
        if (id.WriteLocked)
            _ctx.Log?.Invoke("warn", $"{InstanceId} 宇电温度模块 Loc 锁着写入（我们只读不受影响，" +
                                     "但部署改参数时会「写了不报错却不生效」）");

        _phOk = false;
        if (_links.PhMod is { } ph)
        {
            try
            {
                var pid = await ph.InitAsync(YudianKind.Linear, ct).ConfigureAwait(false);
                var bad = new List<string>();
                for (var w = 0; w < 2; w++)
                {
                    var s = pid.Channels[_mapJ4[w] - 1];
                    if (!s.Enabled) bad.Add($"CH{_mapJ4[w]} 关闭");
                    else if (s.Problem is { } p) bad.Add($"CH{_mapJ4[w]}：{p}");
                }
                if (bad.Count == 0) _phOk = true;
                else _ctx.Log?.Invoke("warn", $"{InstanceId} pH 模块自检不过（{string.Join("；", bad)}）——pH 这一路不发数");
            }
            catch (Exception ex)
            {
                _ctx.Log?.Invoke("warn", $"{InstanceId} pH 模块打不开：{ex.Message}——照常开机，pH 这一路不发数");
            }
        }

        _ioOk = false;
        if (_links.Io is { } io)
        {
            try
            {
                // 开机先把两路切换继电器复位到断开位（TEC 侧）——继电器落回的必须是安全侧
                await io.AllOffAsync(ct).ConfigureAwait(false);
                _electric[0] = _electric[1] = false;
                _ioOk = true;
                _ctx.Log?.Invoke("info", $"{InstanceId} IO8R 已把热源切换继电器复位到 TEC 侧");
            }
            catch (Exception ex)
            {
                _ctx.Log?.Invoke("warn", $"{InstanceId} IO8R 打不开：{ex.Message}——照常开机，" +
                                         $"但电加热不可用，目标超过 {_threshold:F0} ℃ 的下发会被拒绝");
            }
        }
    }

    public async Task StartAsync(CancellationToken ct)
    {
        await _rd.StartAsync(ct).ConfigureAwait(false);
        _pollCts = new CancellationTokenSource();
        _pollTask = Task.Run(() => PollLoopAsync(_pollCts.Token), CancellationToken.None);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _pollCts?.Cancel();
        if (_pollTask is { } t) { try { await t.ConfigureAwait(false); } catch { } }
        _pollTask = null;
        await _rd.StopAsync(ct).ConfigureAwait(false);
    }

    // ── 采集循环（宇电 + IO8R；RD105 的轮询在内层会话里自己转） ─────

    private async Task PollLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_tick);
        while (!ct.IsCancellationRequested)
        {
            try { if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) break; }
            catch (OperationCanceledException) { break; }
            try { await PollOnceAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>跑一拍采集。单拎出来是为了回归测试能一拍一拍地推，不靠真时钟。</summary>
    internal async Task PollOnceAsync(CancellationToken ct)
    {
        var at = _ctx.Clock();

        // 釜内 Tr：读回来喂给控温能力（到达判据吃它），再发到采样流
        try
        {
            var r = await _links.TempMod.ReadAsync(ct).ConfigureAwait(false);
            if (_j7Fails > 0) { _ctx.Log?.Invoke("info", $"{InstanceId} 宇电温度模块恢复"); _j7Fails = 0; }
            for (var w = 0; w < 2; w++)
            {
                var x = r[_mapJ7[w] - 1];
                var innerT = _rd.TempOf(w);
                if (x.Value is { } v)
                {
                    var q = x.SensorFault ? Quality.Bad : Quality.Good;
                    Push(innerT.Channel, "Tr", v, at, q);
                    // 断线的残值不能进控制判据——喂 NaN，让 WaitReached 退回夹套
                    innerT.FeedReactor(x.SensorFault ? double.NaN : v);
                    var tj = innerT.CurrentJacket;
                    if (!double.IsNaN(tj)) Push(innerT.Channel, "dT", v - tj, at, q);
                }
                else
                {
                    innerT.FeedReactor(double.NaN);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            if (_j7Fails++ == 0)
                _ctx.Log?.Invoke("warn", $"{InstanceId} 宇电温度模块读失败：{ex.Message}（连续失败只报第一次）");
            for (var w = 0; w < 2; w++) _rd.TempOf(w).FeedReactor(double.NaN);
        }

        // pH
        if (_links.PhMod is { } ph && _phOk)
        {
            try
            {
                var r = await ph.ReadAsync(ct).ConfigureAwait(false);
                if (_j4Fails > 0) { _ctx.Log?.Invoke("info", $"{InstanceId} pH 模块恢复"); _j4Fails = 0; }
                for (var w = 0; w < 2; w++)
                {
                    var x = r[_mapJ4[w] - 1];
                    if (x.Value is { } v)
                        Push(_rd.TempOf(w).Channel, "pH", v, at, x.SensorFault ? Quality.Bad : Quality.Good);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                if (_j4Fails++ == 0)
                    _ctx.Log?.Invoke("warn", $"{InstanceId} pH 模块读失败：{ex.Message}（连续失败只报第一次）");
            }
        }

        // IO8R：读回继电器实际位置跟命令核对，发热源状态，条件满足时回切 TEC
        if (_links.Io is { } io && _ioOk)
        {
            try
            {
                var dos = await io.ReadRelaysAsync(ct).ConfigureAwait(false);
                if (_ioFails > 0) { _ctx.Log?.Invoke("info", $"{InstanceId} IO8R 恢复"); _ioFails = 0; }
                for (var w = 0; w < 2; w++)
                {
                    var actual = dos[_doIdx[w]];
                    if (actual != _electric[w])
                    {
                        // 模块上有按键，人手也能扳——以实际为准，不跟自己的想象过日子
                        _ctx.Log?.Invoke("warn", $"{InstanceId} 工位 {AB(w)} 切换继电器实际在" +
                            $"{(actual ? "电加热" : "TEC")}侧，与上位机命令不符——已按实际状态记录");
                        _electric[w] = actual;
                    }
                    Push(_rd.TempOf(w).Channel, "heat", actual ? 1 : 0, at, Quality.Good);
                }

                for (var w = 0; w < 2; w++)
                {
                    var innerT = _rd.TempOf(w);
                    if (_electric[w]
                        && (innerT.Setpoint ?? double.MinValue) <= _threshold
                        && !double.IsNaN(innerT.CurrentJacket)
                        && innerT.CurrentJacket <= _threshold - _hyst)
                    {
                        try { await SwitchAsync(w, toElectric: false, ct).ConfigureAwait(false); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            _ctx.Log?.Invoke("warn", $"{InstanceId} 工位 {AB(w)} 回切 TEC 失败：{ex.Message}（下拍再试）");
                        }
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                if (_ioFails++ == 0)
                    _ctx.Log?.Invoke("warn", $"{InstanceId} IO8R 读失败：{ex.Message}（连续失败只报第一次）");
            }
        }
    }

    // ── 热源切换 ─────────────────────────────────────────────────────

    /// <summary>
    /// 下发目标前确保热源在对的一侧。目标 &gt; 阈值必须先切电加热；
    /// 目标 ≤ 阈值时**不在这里回切**——夹套没凉到（阈值 − 滞回）之前把 TEC
    /// 接回去会烧它，回切由采集循环在条件满足的那一拍执行。
    /// </summary>
    internal async Task EnsureSourceAsync(int well, double target, CancellationToken ct)
    {
        if (target <= _threshold) return;
        if (_links.Io is null || !_ioOk)
            throw new InvalidOperationException(
                $"目标 {target:F1} ℃ 高于电加热切换阈值 {_threshold:F0} ℃，" +
                (_links.Io is null ? "但这台没配 IO8R 切换模块" : "但 IO8R 没打开") +
                "——电加热用不了，这个目标上不去");
        if (!_electric[well]) await SwitchAsync(well, toElectric: true, ct).ConfigureAwait(false);
    }

    /// <summary>切换动作全套序列（需求 §2.4）：关输出 → 切继电器 → 核反馈 → 重开输出。</summary>
    private async Task SwitchAsync(int well, bool toElectric, CancellationToken ct)
    {
        var io = _links.Io!;
        await _switchLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_electric[well] == toElectric) return;      // 抢锁期间别人已经切完了
            var innerT = _rd.TempOf(well);
            _ctx.Log?.Invoke("info", $"{InstanceId} 工位 {AB(well)} 热源切换：" +
                $"{(toElectric ? "TEC → 电加热" : "电加热 → TEC")}（先关输出）");

            await innerT.StopAsync(ct).ConfigureAwait(false);                       // ① 带载切继电器 = 触点拉弧
            await io.SetRelayAsync(_doIdx[well], toElectric, ct).ConfigureAwait(false);   // ②
            _electric[well] = toElectric;

            if (_feedback)                                                          // ③ 接了反馈就必须核实
            {
                var deadline = Environment.TickCount64 + 2000;
                var ok = false;
                while (!ok && Environment.TickCount64 < deadline)
                {
                    var di = await io.ReadInputsAsync(ct).ConfigureAwait(false);
                    ok = di[_diIdx[well]] == toElectric;
                    if (!ok) await Task.Delay(100, ct).ConfigureAwait(false);
                }
                if (!ok)
                {
                    _ctx.Log?.Invoke("error", $"{InstanceId} 工位 {AB(well)} 切换命令已发，" +
                        $"但反馈 DI{_diIdx[well]} 2 秒内没对上——控温输出保持关闭");
                    throw new InvalidOperationException(
                        $"工位 {AB(well)} 热源切换未核实：继电器命令已发，但反馈 DI{_diIdx[well]} 没有跟上——" +
                        "接触器可能没动作。控温输出保持关闭，请到现场检查");
                }
            }

            if (innerT.Setpoint is not null)                                        // ④ 有目标才重开输出
                await innerT.EnableAsync(true, ct).ConfigureAwait(false);
            _ctx.Log?.Invoke("info", $"{InstanceId} 工位 {AB(well)} 已切至{(toElectric ? "电加热" : "TEC")}" +
                (_feedback ? "（反馈已核实）" : "（无反馈回路，未核实）"));
        }
        finally
        {
            _switchLock.Release();
        }
    }

    // ── 安全停 ───────────────────────────────────────────────────────

    /// <summary>关本工位控温输出 + 把本工位切换继电器断开（落回 TEC 侧），采集不停。</summary>
    public async ValueTask<IReadOnlyList<string>?> SafeStopAsync(int well, CancellationToken ct)
    {
        if (well is not (0 or 1)) return Array.Empty<string>();
        var notes = new List<string>();
        var inner = await _rd.SafeStopAsync(well, ct).ConfigureAwait(false);
        if (inner is not null) notes.AddRange(inner);

        if (_links.Io is { } io && _ioOk)
        {
            try
            {
                await io.SetRelayAsync(_doIdx[well], false, ct).ConfigureAwait(false);
                _electric[well] = false;
                notes.Add($"工位 {AB(well)} 热源切换继电器已断开（落回 TEC 侧）");
            }
            catch (Exception ex)
            {
                notes.Add($"断开工位 {AB(well)} 热源切换继电器失败：{ex.Message}——" +
                          "兜底靠 IO8R 的总线错误复位（部署清单第 1 条，必须已改成「复位」）");
            }
        }
        return notes;
    }

    private void Push(int channel, string tag, double value, DateTimeOffset at, Quality quality)
    {
        if (double.IsNaN(value)) return;
        _out.Push(new Sample(channel, tag, at.UtcTicks, at, value, quality));
    }

    private static string AB(int well) => well == 0 ? "A" : "B";

    public async ValueTask DisposeAsync()
    {
        _pollCts?.Cancel();
        if (_pollTask is { } t) { try { await t.ConfigureAwait(false); } catch { } }

        // 走人前把两路继电器落回 TEC 侧。断线后的兜底是 IO8R 的总线错误复位，
        // 但能自己收拾就不劳驾兜底
        if (_links.Io is { } io && _ioOk)
        {
            try { await io.AllOffAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
        }

        _fwd.Dispose();
        await _rd.DisposeAsync().ConfigureAwait(false);     // 里面会关掉 RD105 的链路
        _links.TempPort.Dispose();
        _links.PhPort?.Dispose();
        _links.IoPort?.Dispose();
        _out.Complete();
        State = DeviceState.Disposed;
    }

    private sealed class Fwd(Broadcast<Sample> target) : IObserver<Sample>
    {
        public void OnNext(Sample value) => target.Push(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
