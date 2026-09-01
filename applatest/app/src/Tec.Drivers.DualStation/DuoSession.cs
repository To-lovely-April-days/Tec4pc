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

    // ── 蒸回流（夹套跟随）状态。__refluxGate 拴三样：跟随参数、安全压制旗、
    //    喂 Tr 的时间戳——采集循环与安全停/新目标可能在不同线程上碰它们
    private sealed class Follow
    {
        public double DeltaT;
        public double MaxTj;
    }

    private readonly object _refluxGate = new();
    private readonly Follow?[] _reflux = new Follow?[2];
    /// <summary>安全压制：SafeStop/停控清跟随时立起，跟随环写到一半撞见它要把输出关回去。</summary>
    private readonly bool[] _refluxKill = new bool[2];
    private readonly DateTimeOffset[] _trFedAt = { DateTimeOffset.MinValue, DateTimeOffset.MinValue };

    /// <summary>Tr 新鲜度窗：跟随环只吃这窗内喂过的釜温——探头会话死了残值还在，不能追着残值走。</summary>
    private static readonly TimeSpan TrFreshWindow = TimeSpan.FromSeconds(10);

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
            {
                t.FeedReactor(quality == Quality.Good ? value : double.NaN);
                lock (_refluxGate) _trFedAt[w] = _ctx.Clock();
            }
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

        // 蒸回流跟随环：每拍把夹套目标追到 Tr+ΔT（钳上限、限速写、必要时切热源）
        for (var w = 0; w < 2; w++)
            await FollowOnceAsync(w, ct).ConfigureAwait(false);

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

    // ── 蒸回流（夹套跟随，需求 §2 + 0277 §B.5）────────────────────────
    //
    // 跟随环长在采集循环里：每拍读一次喂进来的 Tr，把夹套目标写成
    // min(Tr + ΔT, maxTj)，限速走 RampAsync（SPEED 限速写 TG，不是阶跃）。
    // 四道联锁：① Tr 无效/超时 → 停跟随（目标驻留，不追残值）；
    // ② 目标越过电加热阈值 → EnsureSourceAsync 全套切换序列；
    // ③ 安全动作（SafeStop / 停控温）→ 清跟随 + 压制旗，写到一半撞见就把
    //    输出关回去——下一拍绝不再把目标写回去；
    // ④ maxTj 之外还有设备自己的超温保护寄存器兜底（开机 ApplyProtection 写进去的）。

    /// <summary>开始跟随。第一拍不等采集循环，当下就把目标立起来。</summary>
    internal async Task StartRefluxAsync(int well, double deltaT, double maxTj, CancellationToken ct)
    {
        var t = _rd.TempOf(well);
        var dt = Math.Clamp(deltaT, 0.5, 30);
        var cap = Math.Min(maxTj, t.Limits.Max);    // 设备超温保护是死上限，指令说了不算

        // 起步就要有 Tr：跟随的目标就是 Tr+ΔT，没有 Tr 就没有目标——
        // 开一个空转的跟随比拒绝更糟
        if (!TrValid(well))
            throw new InvalidOperationException(
                $"工位 {AB(well)} 蒸回流开不了：釜内 Tr 没有有效读数" +
                "（宇电 Tr 探头没接、没连或断线）——夹套跟随的目标是 Tr+ΔT，没有 Tr 就没有目标");

        if (cap > _threshold && (_links.Io is null || !_ioOk))
            _ctx.Log?.Invoke("warn", $"{InstanceId} 工位 {AB(well)} 蒸回流：夹套上限 {cap:F0} ℃ " +
                $"高于电加热切换阈值 {_threshold:F0} ℃，但电加热不可用——跟到阈值那一刻会停跟随");

        lock (_refluxGate)
        {
            _refluxKill[well] = false;
            _reflux[well] = new Follow { DeltaT = dt, MaxTj = cap };
        }
        _ctx.Log?.Invoke("info", $"{InstanceId} 工位 {AB(well)} 蒸回流开始：Tj 跟随 Tr+{dt:F1} K，上限 {cap:F0} ℃");
        await FollowOnceAsync(well, ct).ConfigureAwait(false);
    }

    /// <summary>停止跟随（步收尾/下一步接管）：目标停在最后一次下发的值上，输出不动。</summary>
    internal void StopReflux(int well)
    {
        bool was;
        lock (_refluxGate)
        {
            was = _reflux[well] is not null;
            _reflux[well] = null;
        }
        if (was) _ctx.Log?.Invoke("info", $"{InstanceId} 工位 {AB(well)} 蒸回流结束：目标停在最后一次下发的值上");
    }

    /// <summary>安全路径清跟随（SafeStop / 停控温）：连压制旗一起立——
    /// 跟随环这拍要是已经越过检查点，写完会自己把输出关回去。</summary>
    internal bool SuppressReflux(int well)
    {
        lock (_refluxGate)
        {
            var was = _reflux[well] is not null;
            _reflux[well] = null;
            _refluxKill[well] = true;
            return was;
        }
    }

    internal bool RefluxActive(int well)
    {
        lock (_refluxGate) return _reflux[well] is not null;
    }

    private bool TrValid(int well)
    {
        if (double.IsNaN(_rd.TempOf(well).CurrentReactor)) return false;
        lock (_refluxGate) return _ctx.Clock() - _trFedAt[well] <= TrFreshWindow;
    }

    /// <summary>跟随环跑一拍。目标没动（&lt; 0.1 ℃）就不写寄存器——不白磨总线。</summary>
    private async Task FollowOnceAsync(int well, CancellationToken ct)
    {
        Follow? r;
        lock (_refluxGate) r = _reflux[well];
        if (r is null) return;

        var t = _rd.TempOf(well);
        if (!TrValid(well))
        {
            // 联锁①：探头断线/超时。目标驻留在最后一次下发的值上——
            // 追着残值走比停跟随危险得多
            lock (_refluxGate) _reflux[well] = null;
            _ctx.Log?.Invoke("error", $"{InstanceId} 工位 {AB(well)} 蒸回流已停：" +
                "釜内 Tr 无效或超过新鲜度窗（探头断线？）——夹套目标停在最后一次下发的值上");
            return;
        }

        var tg = Math.Clamp(t.CurrentReactor + r.DeltaT, t.Limits.Min, r.MaxTj);
        if (t.Setpoint is { } sp && Math.Abs(tg - sp) < 0.1) return;

        try
        {
            // 联锁②：越过阈值先走全套热源切换序列（关输出→切继电器→核反馈→重开）
            await EnsureSourceAsync(well, tg, ct).ConfigureAwait(false);
            // 限速写：SPEED 按设备最大变温能力限，TG 让温控器自己斜坡过去——
            // 不是每拍阶跃，Tj 的变化率有帽子
            await t.RampAsync(tg, t.Limits.MaxRatePerMin, TempChannelKind.Jacket, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            lock (_refluxGate) _reflux[well] = null;
            _ctx.Log?.Invoke("error", $"{InstanceId} 工位 {AB(well)} 蒸回流已停：{ex.Message}");
            return;
        }

        // 联锁③的竞态补偿：写的当口有人安全停机（旗已清 + 压制立着），
        // 刚才那笔 RampAsync 把输出重新打开了——关回去
        bool kill;
        lock (_refluxGate) kill = _reflux[well] is null && _refluxKill[well];
        if (kill)
        {
            try { await t.StopAsync(ct).ConfigureAwait(false); } catch { }
            _ctx.Log?.Invoke("warn", $"{InstanceId} 工位 {AB(well)} 蒸回流写目标与安全停机撞车——输出已重新关闭");
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

        // 先清跟随（联锁③）：不清的话下一拍跟随环又把目标写回去、把输出重新打开
        if (SuppressReflux(well))
            notes.Add($"工位 {AB(well)} 蒸回流跟随已停——安全停机后不会再有目标写回去");

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
