using Tec.Driver.Abi;
using Tec.Drivers.DualStation;
using Tec.Drivers.Rd105;
using Tec.Drivers.Rd105.Modbus;

namespace Tec.Drivers.Aqmd;

/// <summary>
/// 一台搅拌电机驱动器的会话：开机核对身份 / 极数 / 学习状态、按配置写电机参数，之后轮询实时状态区
/// （转速、电流、故障码）发到所绑通道，并以 IStirrer 端出去（HMI 搅拌卡、趋势「R 转速」、配方「搅拌」步骤、
/// 紧急转速 Rsafe 都只认这个接口）。
///
/// 两条规矩跟整机的设计对齐：
/// · **安全停机不停搅拌**（热液不搅局部过热），照实记一句「搅拌保持 N rpm」；要停在配方里写「搅拌 0 rpm」。
/// · **程序退出 / 设备删掉也不停搅拌**——跟温控器断线保持最后输出一个道理；驱动器自己的「通讯中断停机」
///   开着的话它会按那个时间自己停，开机日志里把这个值写清楚。
/// </summary>
public sealed class AqmdStirrerSession : IDeviceSession
{
    private readonly SharedSerial _link;
    private readonly AqmdClient _client;
    private readonly DriverContext _ctx;
    private readonly ParameterSet _connection;
    private readonly TimeSpan _period;
    private readonly Broadcast<Sample> _out = new();
    private readonly Stirrer[] _stirrers;
    private readonly LinkRecovery _recover;
    private DeviceState _state = DeviceState.Connected;
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;
    private int _fails;
    private int _lastFault;
    private int _lastStall;

    // 开机从驱动器读出来 / 按配置定下来的换算常数
    private AqmdIdentity? _identity;
    private AqmdMotorParams? _motor;
    private int _poles;
    private double _ratio = 1;
    private bool _setpointX10;
    private int _dirSign = 1;
    private double _defaultRampSeconds = 5;
    private SpeedLimits _limits = new(0, 1000);

    // 最近一次写进 0x0052/0x0053 的加减速，一样就不再写（每次设速少两帧）
    private (ushort Acc, ushort Dec)? _accelWritten;

    internal AqmdStirrerSession(SharedSerial link, AqmdClient client, DriverContext ctx, ParameterSet connection, TimeSpan period)
    {
        _link = link;
        _client = client;
        _ctx = ctx;
        _connection = connection;
        _period = period;
        var chs = ctx.ChannelNumbers.Count > 0 ? ctx.ChannelNumbers : new[] { 0 };
        _stirrers = chs.Select(c => new Stirrer(this, c)).ToArray();
        _recover = new LinkRecovery($"{ctx.InstanceId} 搅拌驱动器", link.Reopen, (l, t) => ctx.Log?.Invoke(l, t));
    }

    /// <summary>「通讯中断停机」要写的 ×0.1 s 值；null = 不改。</summary>
    public ushort? WatchdogTenths { get; init; }
    /// <summary>「堵转停机时间」要写的 ×0.1 s 值；null = 不改。</summary>
    public ushort? StallStopTenths { get; init; }

    /// <summary>重开过几次口子（测试用）。</summary>
    internal int LinkReopens => _recover.Reopens;
    /// <summary>开机定下来的极数（测试与日志用）。</summary>
    public int Poles => _poles;
    public AqmdIdentity? Identity => _identity;

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
    public int WellCount => _stirrers.Length;

    public IReadOnlyList<TagDescriptor> Tags { get; } = new[]
    {
        new TagDescriptor(AqmdStirrerDriver.TagRpm, "搅拌转速", "rpm", DataShape.Scalar) { Nominal = new ValueRange(0, 1000) },
        new TagDescriptor(AqmdStirrerDriver.TagCurrent, "搅拌电流", "A", DataShape.Scalar),
        new TagDescriptor(AqmdStirrerDriver.TagFault, "搅拌故障码", "", DataShape.State)
    };

    public IReadOnlyList<ICapability> CapabilitiesOf(int well)
        => well >= 0 && well < _stirrers.Length ? new ICapability[] { _stirrers[well] } : Array.Empty<ICapability>();

    /// <summary>认领配方里的「搅拌」（仿真机那份在测试项目里，真机这份只按 IStirrer 说话）。</summary>
    public ICommandHandler? Resolve(string commandId)
        => commandId == CommandSpecs.Stir ? new AqmdStirHandler() : null;

    // ── 开机 ───────────────────────────────────────────────────────

    /// <summary>
    /// 开机自检：读身份、电机参数、状态，按配置把极数 / 额定电流 / 看门狗 / 堵转写进驱动器并读回核对。
    /// 极数定不下来、没学习——直接开不了，报错里说去哪填、怎么学。
    /// </summary>
    public async Task InitAsync(CancellationToken ct)
    {
        var cfg = _ctx.Config;
        _identity = await _client.ReadIdentityAsync(ct).ConfigureAwait(false);
        var motor = await _client.ReadMotorAsync(ct).ConfigureAwait(false);
        var said = new List<string>();

        // 极数：配置 > 驱动器；配置了而驱动器里不一样就写过去（0x0034 那边读的 rpm 也才对）
        var cfgPoles = cfg.Int(AqmdStirrerDriver.FieldPoles, 0);
        if (cfgPoles > 0 && motor.Poles != cfgPoles)
        {
            await WriteVerifiedAsync(AqmdStirrerDriver.FieldPoles, $"极数 {motor.Poles} → {cfgPoles}",
                () => _client.WritePolesAsync((ushort)cfgPoles, ct), () => _client.StoreMotorParamsAsync(ct),
                async () => (await _client.ReadMotorAsync(ct).ConfigureAwait(false)).Poles == cfgPoles, ct).ConfigureAwait(false);
            motor = await _client.ReadMotorAsync(ct).ConfigureAwait(false);
            said.Add($"极数已写成 {cfgPoles} 并存储");
        }
        _poles = cfgPoles > 0 ? cfgPoles : motor.Poles;
        if (_poles <= 0)
            throw new InvalidOperationException(
                $"{InstanceId}：驱动器里电机极数是 0（寄存器 0x0073），「{AqmdStirrerDriver.FieldPoles}」也没填——转速换算不出来。" +
                "在台面属性栏填电机铭牌上的极数再连；不知道就让它转 20 Hz 数一分钟圈数，极数 = 400 ÷ 圈数（100 圈 = 4 极、50 圈 = 8 极）");
        _ratio = motor.GearRatio <= 0 ? 1 : motor.GearRatio;
        _setpointX10 = motor.SetpointX10;

        // 额定电流：填了就写（额定 = 最大负载，手册 §3.2 第 5 步）
        var rated = cfg.Num(AqmdStirrerDriver.FieldRatedCurrent, 0);
        if (rated > 0)
        {
            var want = (ushort)Math.Round(rated * 100);
            if (Math.Abs(motor.RatedCurrentA - rated) > 0.005 || Math.Abs(motor.MaxLoadCurrentA - rated) > 0.005)
            {
                await WriteVerifiedAsync(AqmdStirrerDriver.FieldRatedCurrent, $"额定电流 {motor.RatedCurrentA:0.00} A → {rated:0.00} A",
                    () => _client.WriteRatedCurrentAsync(want, ct), () => _client.StoreMotorParamsAsync(ct),
                    async () =>
                    {
                        var m = await _client.ReadMotorAsync(ct).ConfigureAwait(false);
                        return Math.Abs(m.RatedCurrentA - rated) < 0.005 && Math.Abs(m.MaxLoadCurrentA - rated) < 0.005;
                    }, ct).ConfigureAwait(false);
                motor = await _client.ReadMotorAsync(ct).ConfigureAwait(false);
                said.Add($"额定电流已写成 {rated:0.00} A 并存储");
            }
        }
        _motor = motor;

        // 学习状态：没学过电机转不了，写速度也会被 0x60 拒掉——开机就说清
        if (!motor.Learned)
            throw new InvalidOperationException(
                $"{InstanceId}：驱动器报电机相序未学习（0x0075 = 0）——{AqmdRegisters.FaultAdvice(1)}，或用厂家软件学习；学成再连");

        // 看门狗 / 堵转：选了才写
        var sys = await _client.ReadSystemAsync(ct).ConfigureAwait(false);
        var sysDirty = false;
        if (WatchdogTenths is { } wd && Math.Abs(sys.WatchdogSeconds - wd * 0.1) > 0.01)
        {
            await _client.WriteWatchdogAsync(wd, ct).ConfigureAwait(false);
            said.Add($"通讯中断停机 {sys.WatchdogSeconds:0.#} s → {(wd == 0 ? "关" : $"{wd * 0.1:0.#} s")}");
            sysDirty = true;
        }
        if (StallStopTenths is { } ss && Math.Abs(sys.StallStopSeconds - ss * 0.1) > 0.01)
        {
            await _client.WriteStallStopAsync(ss, ct).ConfigureAwait(false);
            said.Add($"堵转停机 {sys.StallStopSeconds:0.#} s → {(ss == 0 ? "关" : $"{ss * 0.1:0.#} s")}");
            sysDirty = true;
        }
        if (sysDirty)
        {
            await _client.StoreSystemParamsAsync(ct).ConfigureAwait(false);
            sys = await _client.ReadSystemAsync(ct).ConfigureAwait(false);
            if ((WatchdogTenths is { } w2 && Math.Abs(sys.WatchdogSeconds - w2 * 0.1) > 0.01)
                || (StallStopTenths is { } s2 && Math.Abs(sys.StallStopSeconds - s2 * 0.1) > 0.01))
                throw new InvalidOperationException($"{InstanceId}：通讯中断停机 / 堵转停机写了读回却不一样——写没生效，查驱动器的 0x0094「485 控制时禁止参数配置」");
        }

        // 限值与方向
        var maxCfg = Math.Max(10, cfg.Num(AqmdStirrerDriver.FieldMaxRpm, 1000));
        var maxDev = AqmdRegisters.HzToRpm(motor.MaxFrequencyHz, _poles, _ratio);
        var max = Math.Min(maxCfg, maxDev > 0 ? maxDev : maxCfg);
        var minCfg = cfg.Num(AqmdStirrerDriver.FieldMinRpm, 0);
        var minAuto = Math.Ceiling(AqmdRegisters.HzToRpm(AqmdRegisters.MinMeasurableHz, _poles, _ratio));
        var min = minCfg > 0 ? minCfg : minAuto;
        if (min >= max)
            throw new InvalidOperationException($"{InstanceId}：转速下限 {min:0} rpm 不低于上限 {max:0} rpm（上限受驱动器最大换向频率 {motor.MaxFrequencyHz:0} Hz 钳到 {maxDev:0}）——改「{AqmdStirrerDriver.FieldMaxRpm}」或「{AqmdStirrerDriver.FieldMinRpm}」");
        _limits = new SpeedLimits(min, max);
        _dirSign = cfg.Str(AqmdStirrerDriver.FieldDirection, AqmdStirrerDriver.DirForward) == AqmdStirrerDriver.DirReverse ? -1 : 1;
        _defaultRampSeconds = Math.Max(0.5, cfg.Num(AqmdStirrerDriver.FieldRamp, 5));

        // 当前状态：在转就接手（CurrentRpm 对上），故障码照实记
        var st = await _client.ReadStatusAsync(ct).ConfigureAwait(false);
        Apply(st, _ctx.Clock(), publish: false);

        _ctx.Log?.Invoke("info",
            $"{InstanceId} {_identity.Display}（{AqmdStirrerDriver.LinkName(_connection)}）：极数 {_poles}、减速比 {_ratio:0.0}、额定电流 {motor.RatedCurrentA:0.00} A、" +
            $"最大换向频率 {motor.MaxFrequencyHz:0} Hz；软件转速范围 {min:0}–{max:0} rpm、{(_dirSign > 0 ? "正转" : "反转")}、缺省加减速 {_defaultRampSeconds:0.#} s；" +
            $"驱动器通讯中断停机 {(sys.WatchdogSeconds > 0 ? $"{sys.WatchdogSeconds:0.#} s" : "关（断线搅拌保持）")}、堵转停机 {(sys.StallStopSeconds > 0 ? $"{sys.StallStopSeconds:0.#} s" : "关")}；" +
            $"此刻 {(Math.Abs(st.FrequencyHz) > 0.05 ? $"在转 {_stirrers[0].CurrentRpm:0} rpm" : "停着")}、电流 {st.CurrentA:0.00} A、故障码 {st.Fault}" +
            (said.Count > 0 ? $"；本次写入：{string.Join("、", said)}" : ""));
        if (st.Fault != 0)
            _ctx.Log?.Invoke("warn", $"{InstanceId} 开机就带着故障码 {st.Fault}（{AqmdRegisters.FaultText(st.Fault)}）——{AqmdRegisters.FaultAdvice(st.Fault)}");
    }

    /// <summary>写 → 存 → 读回核对；不信应答只信读回（宇电 InP 那套规矩）。被驱动器拒了把异常码翻成人话。</summary>
    private async Task WriteVerifiedAsync(string field, string what, Func<Task> write, Func<Task> store, Func<Task<bool>> verify, CancellationToken ct)
    {
        try
        {
            await write().ConfigureAwait(false);
            await store().ConfigureAwait(false);
        }
        catch (ModbusException ex)
        {
            throw new InvalidOperationException($"{InstanceId} 「{field}」写不进去（{what}）：{AqmdRegisters.ExplainWriteRejected(ex)}", ex);
        }
        if (!await verify().ConfigureAwait(false))
            throw new InvalidOperationException($"{InstanceId} 「{field}」写了（{what}）读回却不一样——写没生效，查驱动器的 0x0094「485 控制时禁止参数配置」");
        _ctx.Log?.Invoke("warn", $"{InstanceId} 「{field}」：{what}，已写进驱动器并存储，读回核对一致");
    }

    // ── 轮询 ───────────────────────────────────────────────────────

    public Task StartAsync(CancellationToken ct)
    {
        _link.Open();
        _pollCts = new CancellationTokenSource();
        _pollTask = Task.Run(() => PollLoopAsync(_pollCts.Token), CancellationToken.None);
        State = _lastFault != 0 ? DeviceState.Faulted : DeviceState.Ready;
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _pollCts?.Cancel();
        if (_pollTask is { } t) { try { await t.ConfigureAwait(false); } catch { } }
        _pollTask = null;
        State = DeviceState.Connected;
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_period);
        while (!ct.IsCancellationRequested)
        {
            try { if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) break; }
            catch (OperationCanceledException) { break; }
            try { await PollOnceAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>采一拍。单拎出来是为了回归测试能一拍一拍地推。</summary>
    internal async Task PollOnceAsync(CancellationToken ct)
    {
        try
        {
            var st = await _client.ReadStatusAsync(ct).ConfigureAwait(false);
            if (_fails > 0) { _ctx.Log?.Invoke("info", $"{InstanceId} 搅拌驱动器恢复（读失败 {_fails} 拍后）"); _fails = 0; }
            _recover.Ok();
            Apply(st, _ctx.Clock(), publish: true);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            if (_fails++ == 0)
            {
                var why = ex is IOException or UnauthorizedAccessException ? SerialFault.Explain(ex, _link.PortName) : ex.Message;
                _ctx.Log?.Invoke("warn", $"{InstanceId} 搅拌驱动器读失败：{why}（连续失败只报第一次）");
            }
            _recover.Failed(ex);
        }
    }

    /// <summary>一拍状态 → 实测转速 / 电流 / 故障码；故障码变化记日志、改会话状态；堵转单说。</summary>
    private void Apply(AqmdStatus st, DateTimeOffset at, bool publish)
    {
        // 转速从实测换向频率按我们的极数算——0x0034 那个寄存器在极数没配时读回 0xFFFF，不信它
        var rpm = Math.Abs(AqmdRegisters.HzToRpm(st.FrequencyHz, _poles, _ratio));
        if (double.IsNaN(rpm)) rpm = 0;
        rpm = Math.Round(rpm, 0);

        if (st.Fault != _lastFault)
        {
            if (st.Fault != 0)
                _ctx.Log?.Invoke("error", $"{InstanceId} 搅拌驱动器故障码 {st.Fault}：{AqmdRegisters.FaultText(st.Fault)}——{AqmdRegisters.FaultAdvice(st.Fault)}" +
                                          $"（电流 {st.CurrentA:0.00} A，实测 {rpm:0} rpm）");
            else
                _ctx.Log?.Invoke("info", $"{InstanceId} 搅拌驱动器故障已清（原故障码 {_lastFault}）");
            _lastFault = st.Fault;
            if (_pollTask is not null) State = st.Fault != 0 ? DeviceState.Faulted : DeviceState.Ready;
        }
        if (st.Stall != _lastStall)
        {
            if (st.Stall != 0) _ctx.Log?.Invoke("warn", $"{InstanceId} 搅拌{(st.Stall == 1 ? "正转" : "反转")}堵转停止（0x0032 = {st.Stall}）——查桨叶是不是卡住；制动或反转一次可清除");
            _lastStall = st.Stall;
        }

        foreach (var s in _stirrers)
        {
            s.CurrentRpm = rpm;
            var a = new Sample(s.Channel, AqmdStirrerDriver.TagRpm, at.UtcTicks, at, rpm, Quality.Good);
            var b = new Sample(s.Channel, AqmdStirrerDriver.TagCurrent, at.UtcTicks, at, Math.Round(st.CurrentA, 2), Quality.Good);
            var c = new Sample(s.Channel, AqmdStirrerDriver.TagFault, at.UtcTicks, at, st.Fault, Quality.Good);
            if (!publish) continue;
            _out.Push(a); _out.Push(b); _out.Push(c);
            s.Publish(in a);
        }
    }

    // ── 动作（IStirrer 调）────────────────────────────────────────

    private async Task SetSpeedCoreAsync(Stirrer who, double rpm, double rampSeconds, CancellationToken ct)
    {
        if (rpm <= 0) { await StopCoreAsync(who, ct).ConfigureAwait(false); return; }
        var want = Math.Clamp(rpm, _limits.Min, _limits.Max);
        var hz = AqmdRegisters.RpmToHz(want, _poles, _ratio);
        var fullHz = AqmdRegisters.RpmToHz(_limits.Max, _poles, _ratio);
        var accHz = fullHz / Math.Max(0.5, rampSeconds);
        var acc = AqmdRegisters.AccelReg(accHz, _motor?.MaxAccelReg ?? ushort.MaxValue);
        var dec = AqmdRegisters.AccelReg(accHz, _motor?.MaxDecelReg ?? ushort.MaxValue);
        try
        {
            if (_accelWritten is not { } prev || prev.Acc != acc || prev.Dec != dec)
            {
                await _client.WriteAccelAsync(acc, dec, ct).ConfigureAwait(false);
                _accelWritten = (acc, dec);
            }
            await _client.WriteSpeedAsync(AqmdRegisters.SpeedReg(hz * _dirSign, _setpointX10), ct).ConfigureAwait(false);
        }
        catch (ModbusException ex)
        {
            throw new InvalidOperationException($"{InstanceId} 设转速失败：{AqmdRegisters.ExplainWriteRejected(ex)}", ex);
        }
        who.Target = want;
        _ctx.Log?.Invoke("info", $"{InstanceId} 搅拌设 {want:0} rpm（换向频率 {hz:0.0} Hz，{(_dirSign > 0 ? "正转" : "反转")}，加减速 {accHz:0.#} Hz/s ≈ 满量程 {rampSeconds:0.#} s）" +
                                 (Math.Abs(want - rpm) > 0.5 ? $"——要的 {rpm:0} 超出范围 {_limits.Min:0}–{_limits.Max:0}，按边界下发" : ""));
    }

    private async Task StopCoreAsync(Stirrer who, CancellationToken ct)
    {
        try { await _client.StopAsync(AqmdStopMode.Normal, ct).ConfigureAwait(false); }
        catch (ModbusException ex)
        {
            throw new InvalidOperationException($"{InstanceId} 停搅拌失败：{AqmdRegisters.ExplainWriteRejected(ex)}", ex);
        }
        who.Target = 0;
        _ctx.Log?.Invoke("info", $"{InstanceId} 搅拌停机（正常停止，按减速度减下来）");
    }

    /// <summary>
    /// 安全停机**不停搅拌**：热液不搅局部过热。照实说保持多少转——GLP 要答得出停的那一刻机器被动了什么。
    /// 要停在配方里写「搅拌 0 rpm」，或者紧急程序里给 Rsafe。
    /// </summary>
    public ValueTask<IReadOnlyList<string>?> SafeStopAsync(int well, CancellationToken ct)
    {
        var s = well >= 0 && well < _stirrers.Length ? _stirrers[well] : _stirrers[0];
        var line = s.CurrentRpm > 0 || s.Target > 0
            ? $"搅拌保持 {Math.Max(s.CurrentRpm, s.Target):0} rpm —— 热液不搅有局部过热风险，停搅拌要在配方里写"
            : "搅拌本来就停着，未动";
        return ValueTask.FromResult<IReadOnlyList<string>?>(new[] { line });
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        var s = _stirrers[0];
        if (s.CurrentRpm > 0 || s.Target > 0)
            _ctx.Log?.Invoke("warn", $"{InstanceId} 会话关闭，搅拌保持 {Math.Max(s.CurrentRpm, s.Target):0} rpm 不动——要停请在面板或配方里停；" +
                                     "驱动器「通讯中断停机」开着的话它会按那个时间自己刹车");
        _out.Complete();
        _link.Dispose();                            // 归还串口池；共口的另一台还在用就不真关
        State = DeviceState.Disposed;
    }

    /// <summary>这一路的搅拌能力。斜坡经 IStirrerRamp 给（满量程用时），下一次 SetSpeedAsync 用掉。</summary>
    private sealed class Stirrer : IStirrer, IStirrerRamp
    {
        private readonly AqmdStirrerSession _s;
        private readonly Broadcast<Sample> _speed = new();
        private double? _rampSeconds;

        public Stirrer(AqmdStirrerSession s, int channel) { _s = s; Channel = channel; }

        public int Channel { get; }
        public SpeedLimits Limits => _s._limits;
        public double CurrentRpm { get; internal set; }
        /// <summary>最后下发的目标（0 = 停）；SafeStop / 关会话时说话用。</summary>
        internal double Target { get; set; }
        public IObservable<Sample> Speed => _speed;

        public Task SetSpeedAsync(double rpm, CancellationToken ct)
        {
            var ramp = _rampSeconds ?? _s._defaultRampSeconds;
            _rampSeconds = null;                      // 一次性：下一条没给斜坡就回缺省
            return _s.SetSpeedCoreAsync(this, rpm, ramp, ct);
        }

        public Task StopAsync(CancellationToken ct) => _s.StopCoreAsync(this, ct);

        public void SetRampSeconds(double seconds) => _rampSeconds = Math.Max(0.5, seconds);

        internal void Publish(in Sample s) => _speed.Push(s);
    }
}

/// <summary>
/// 配方「搅拌」在真机上的认领，只按 IStirrer 说话。「到达用时」是从当前转速到目标的时间，
/// IStirrerRamp 要的是满量程用时——按 Δrpm 折算过去，电机真的在那么多秒里到达；
/// 「立即」按驱动的最短加减速（0.5 s）。转速 0 走 StopAsync 不走 SetSpeedAsync(0)：真机上是两个寄存器。
/// </summary>
internal sealed class AqmdStirHandler : ICommandHandler
{
    public async Task<CommandOutcome> ExecuteAsync(CommandContext ctx, CommandInput p, CancellationToken ct)
    {
        var stir = ctx.Capabilities.Get<IStirrer>() ?? throw new InvalidOperationException("该通道没有搅拌能力");
        var began = ctx.Now();
        var rpm = p.Num("rpm");
        var ramp = CommandSpecs.StirImmediate(p) ? 0.5 : Math.Max(0.5, p.Num("ramp", 5));

        if (stir is IStirrerRamp r)
        {
            var delta = Math.Abs(Math.Max(0, rpm) - stir.CurrentRpm);
            r.SetRampSeconds(delta > 1 ? ramp * stir.Limits.Max / delta : ramp);
        }
        if (rpm <= 0) await stir.StopAsync(ct).ConfigureAwait(false);
        else await stir.SetSpeedAsync(rpm, ct).ConfigureAwait(false);

        await DriverTime.DelayAsync(TimeSpan.FromSeconds(ramp), ctx.TimeScale, ct).ConfigureAwait(false);
        return new CommandOutcome(EndReason.Reached, ctx.Now() - began)
        {
            Note = rpm <= 0 ? "搅拌已停机" : $"搅拌已设 {rpm:0} rpm，实测 {stir.CurrentRpm:0} rpm"
        };
    }
}
