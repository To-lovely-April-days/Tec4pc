using Tec.Driver.Abi;
using Tec.Drivers.Rd105;
using F = Tec.Drivers.DualStation.DualStationDriver.Fields;

namespace Tec.Drivers.DualStation;

/// <summary>
/// 双工位反应主机的组合会话：一台设备、两个通道（需求 §1）。
///
/// 里面套着一个 Rd105Session 干夹套控温和 Tj/Tset/duty/fault 的采集（原样转发），
/// 自己再开一个采集循环轮 IO8R（热源切换状态与回切）。**釜内 Tr 不归它采**——
/// Tr 是独立的宇电探头设备发的，工作台在会话都开起来之后把带 Tr 签的采样按
/// 通道喂进来（IExternalReactorTemp），主机拿它做判到达、E 级程序和 dT。
///
/// 热源切换（需求 §2）：
///  · 切入电加热按目标判（目标 &gt; 阈值就切，不等夹套爬到阈值再换挡）；
///  · 切回 TEC 要目标 ≤ 阈值 **且** 实测夹套 ≤ 阈值 − 滞回——夹套还烫着就把
///    TEC 接回去，等于让它贴着超出耐温的热源；
///  · 每次切换都走全套序列：关输出 → 切继电器 → （核反馈）→ 重开输出。
/// </summary>
public sealed class DuoSession : IDeviceSession, IExternalReactorTemp
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
    private readonly int[] _doIdx = new int[2];
    private readonly int[] _diIdx = new int[2];
    private readonly TimeSpan _tick;

    private readonly bool[] _electric = new bool[2];
    private bool _ioOk;
    private int _ioFails;

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
        // Tr 与 pH 不在这里声明——它们是宇电探头设备的签，各归各的会话，
        // 主机再发一份就是同一个量两个来源
        new TagDescriptor("Tj", "夹套温度", "℃", DataShape.Scalar)
            { Nominal = new ValueRange(-40, 180) },
        new TagDescriptor("dT", "Tr−Tj 温差", "℃", DataShape.Scalar)
            { Nominal = new ValueRange(-60, 60) },
        new TagDescriptor("Tset", "设定温度", "℃", DataShape.Scalar)
            { Nominal = new ValueRange(-40, 180) },
        new TagDescriptor("duty", "控温输出", "%", DataShape.Scalar)
            { Nominal = new ValueRange(-100, 100) },
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

    /// <summary>温度指令认领 ABI 的能力通用执行器——与仿真同一份语义，
    /// 仿真调好的配方插上真机能跑。搅拌/加料这台没有，认不了不硬认。</summary>
    public ICommandHandler? Resolve(string commandId) => CapabilityCommands.Resolve(commandId);

    internal Rd105TemperatureControl InnerTemp(int well) => _rd.TempOf(well);

    /// <summary>
    /// 工作台喂进来的外部釜温（宇电 Tr 探头会话发的）。质量不好就按 NaN 处置——
    /// 断线残值不能进控制判据；判到达与 E 级程序吃的就是这口饭。
    /// </summary>
    public void FeedReactor(int channel, double value, Quality quality)
    {
        for (var w = 0; w < 2; w++)
        {
            var t = _rd.TempOf(w);
            if (t.Channel == channel)
                t.FeedReactor(quality == Quality.Good ? value : double.NaN);
        }
    }

    // ── 开机 ─────────────────────────────────────────────────────────

    /// <summary>
    /// 开机自检：RD105 写两路保护寄存器；IO8R 把切换继电器复位到 TEC 侧
    /// （继电器落回的必须是安全侧），坏了照常开但电加热不可用（需求 §2.6）。
    /// </summary>
    public async Task InitAsync(CancellationToken ct)
    {
        await _rd.ApplyProtectionAsync(ct).ConfigureAwait(false);

        _ioOk = false;
        if (_links.Io is { } io)
        {
            try
            {
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

    // ── 采集循环（dT + IO8R；RD105 的轮询在内层会话里自己转） ───────

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

    /// <summary>跑一拍。单拎出来是为了回归测试能一拍一拍地推，不靠真时钟。</summary>
    internal async Task PollOnceAsync(CancellationToken ct)
    {
        var at = _ctx.Clock();

        // dT：外部喂进来的 Tr − 本机的 Tj。两头都有才发，缺哪头都不编
        for (var w = 0; w < 2; w++)
        {
            var t = _rd.TempOf(w);
            if (!double.IsNaN(t.CurrentReactor) && !double.IsNaN(t.CurrentJacket))
                Push(t.Channel, "dT", t.CurrentReactor - t.CurrentJacket, at, Quality.Good);
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
