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
public sealed class AqmdStirrerSession : IDeviceSession, IDeviceSettings
{
    // ── 设备参数面板的键 ──
    public const string GroupLive = "live", GroupMotor = "motor", GroupProtect = "protect";
    public const string KFreq = "freq", KRpm = "rpm", KCurrent = "current", KPwm = "pwm", KFault = "fault", KStall = "stall",
                        KDrvTemp = "drvtemp", KVolt = "volt",
                        KPoles = "poles", KRated = "rated", KMaxLoad = "maxload", KRatio = "ratio", KLearned = "learned", KMaxHz = "maxhz",
                        KWatchdog = "watchdog", KStallStop = "stallstop";
    public const string ActCountTest = "counttest", ActStop = "stop", ActLearn = "learn";
    /// <summary>数圈测试的换向频率：20.0 Hz——极数 = 20 × 20 ÷ 每分钟圈数 = 400 ÷ 圈数。</summary>
    public const double CountTestHz = 20.0;

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
    /// <summary>极数已知且相序已学习——这时才把 IStirrer 挂出去、认领搅拌指令。否则会话照样开着（面板能用），这一路没有搅拌能力。</summary>
    private bool _ready;
    private string _notReadyWhy = "";
    private CancellationTokenSource? _countCts;
    private AqmdStatus? _lastStatus;

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
    /// <summary>IStirrer 挂出去了没有（极数已知且已学习）。</summary>
    public bool StirrerReady => _ready;
    /// <summary>数圈测试转多久（秒）。测试用，缺省 60。</summary>
    internal double CountTestSeconds { get; set; } = 60;
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

    /// <summary>极数没配 / 没学习时不挂 IStirrer：面板上显示「未接搅拌驱动器」，比挂一个算不出转速的能力诚实。</summary>
    public IReadOnlyList<ICapability> CapabilitiesOf(int well)
        => _ready && well >= 0 && well < _stirrers.Length ? new ICapability[] { _stirrers[well] } : Array.Empty<ICapability>();

    /// <summary>认领配方里的「搅拌」（仿真机那份在测试项目里，真机这份只按 IStirrer 说话）。没就绪不认领——校验器会如实拦下。</summary>
    public ICommandHandler? Resolve(string commandId)
        => _ready && commandId == CommandSpecs.Stir ? new AqmdStirHandler() : null;

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

        // 极数没配 / 没学习：**连得上，但这一路没有搅拌能力**——从前这里直接拒开，可数圈得先让它转、它不填极数又不让转，
        // 现场就卡死在「未连接」上（用户截图）。现在会话开着、参数面板能用：数圈测试、写极数、学习相序都在面板里做，做完重新「连接」
        if (_poles <= 0)
            _notReadyWhy = $"驱动器里电机极数是 0（寄存器 0x0073），「{AqmdStirrerDriver.FieldPoles}」也没填——转速换算不出来，这一路暂时没有搅拌能力。" +
                           "到「设备参数」面板点「数圈测试」让它按 20 Hz 转一分钟，数圈数：极数 = 400 ÷ 圈数（100 圈 = 4 极、50 圈 = 8 极），" +
                           "在「电机参数」里写进去，再点「连接」";
        else if (!motor.Learned)
            _notReadyWhy = $"驱动器报电机相序未学习（0x0075 = 0）——这一路暂时没有搅拌能力。电机空载时到「设备参数」面板点「学习相序」" +
                           $"（或{AqmdRegisters.FaultAdvice(1)}），学成再点「连接」";
        _ready = _notReadyWhy.Length == 0;

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

        // 限值与方向（极数未知算不出转速范围，就绪了才算）
        _dirSign = cfg.Str(AqmdStirrerDriver.FieldDirection, AqmdStirrerDriver.DirForward) == AqmdStirrerDriver.DirReverse ? -1 : 1;
        _defaultRampSeconds = Math.Max(0.5, cfg.Num(AqmdStirrerDriver.FieldRamp, 5));
        double min = 0, max = 0, maxDev = 0;
        if (_ready)
        {
            var maxCfg = Math.Max(10, cfg.Num(AqmdStirrerDriver.FieldMaxRpm, 1000));
            maxDev = AqmdRegisters.HzToRpm(motor.MaxFrequencyHz, _poles, _ratio);
            max = Math.Min(maxCfg, maxDev > 0 ? maxDev : maxCfg);
            var minCfg = cfg.Num(AqmdStirrerDriver.FieldMinRpm, 0);
            var minAuto = Math.Ceiling(AqmdRegisters.HzToRpm(AqmdRegisters.MinMeasurableHz, _poles, _ratio));
            min = minCfg > 0 ? minCfg : minAuto;
            if (min >= max)
                throw new InvalidOperationException($"{InstanceId}：转速下限 {min:0} rpm 不低于上限 {max:0} rpm（上限受驱动器最大换向频率 {motor.MaxFrequencyHz:0} Hz 钳到 {maxDev:0}）——改「{AqmdStirrerDriver.FieldMaxRpm}」或「{AqmdStirrerDriver.FieldMinRpm}」");
            _limits = new SpeedLimits(min, max);
        }

        // 当前状态：在转就接手（CurrentRpm 对上），故障码照实记
        var st = await _client.ReadStatusAsync(ct).ConfigureAwait(false);
        Apply(st, _ctx.Clock(), publish: false);

        _ctx.Log?.Invoke("info",
            $"{InstanceId} {_identity.Display}（{AqmdStirrerDriver.LinkName(_connection)}）：极数 {_poles}、减速比 {_ratio:0.0}、额定电流 {motor.RatedCurrentA:0.00} A、" +
            $"最大换向频率 {motor.MaxFrequencyHz:0} Hz、{(motor.Learned ? "已学习" : "未学习")}；" +
            (_ready ? $"软件转速范围 {min:0}–{max:0} rpm、" : "**这一路暂无搅拌能力**、") +
            $"{(_dirSign > 0 ? "正转" : "反转")}、缺省加减速 {_defaultRampSeconds:0.#} s；" +
            $"驱动器通讯中断停机 {(sys.WatchdogSeconds > 0 ? $"{sys.WatchdogSeconds:0.#} s" : "关（断线搅拌保持）")}、堵转停机 {(sys.StallStopSeconds > 0 ? $"{sys.StallStopSeconds:0.#} s" : "关")}；" +
            $"此刻 {(Math.Abs(st.FrequencyHz) > 0.05 ? $"在转（{st.FrequencyHz:0.0} Hz{(_poles > 0 ? $" ≈ {_stirrers[0].CurrentRpm:0} rpm" : "")}）" : "停着")}、电流 {st.CurrentA:0.00} A、故障码 {st.Fault}" +
            (said.Count > 0 ? $"；本次写入：{string.Join("、", said)}" : ""));
        if (!_ready)
            _ctx.Log?.Invoke("error", $"{InstanceId} {_notReadyWhy}");
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
        // 转速从实测换向频率按我们的极数算——0x0034 那个寄存器在极数没配时读回 0xFFFF，不信它；极数未知就不发 rpm（不编数）
        var rpm = _poles > 0 ? Math.Round(Math.Abs(AqmdRegisters.HzToRpm(st.FrequencyHz, _poles, _ratio)), 0) : 0;

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

        _lastStatus = st;
        foreach (var s in _stirrers)
        {
            s.CurrentRpm = rpm;
            var b = new Sample(s.Channel, AqmdStirrerDriver.TagCurrent, at.UtcTicks, at, Math.Round(st.CurrentA, 2), Quality.Good);
            var c = new Sample(s.Channel, AqmdStirrerDriver.TagFault, at.UtcTicks, at, st.Fault, Quality.Good);
            if (!publish) continue;
            _out.Push(b); _out.Push(c);
            if (_poles <= 0) continue;
            var a = new Sample(s.Channel, AqmdStirrerDriver.TagRpm, at.UtcTicks, at, rpm, Quality.Good);
            _out.Push(a);
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
        _countCts?.Cancel();
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        var s = _stirrers[0];
        if (s.CurrentRpm > 0 || s.Target > 0)
            _ctx.Log?.Invoke("warn", $"{InstanceId} 会话关闭，搅拌保持 {Math.Max(s.CurrentRpm, s.Target):0} rpm 不动——要停请在面板或配方里停；" +
                                     "驱动器「通讯中断停机」开着的话它会按那个时间自己刹车");
        _out.Complete();
        _link.Dispose();                            // 归还串口池；共口的另一台还在用就不真关
        State = DeviceState.Disposed;
    }

    // ── IDeviceSettings：驱动器自己的寄存器 ─────────────────────────
    //
    // 实时量、电机参数（极数 / 额定电流直接写驱动器并存储）、保护（通讯中断停机 / 堵转停机）、
    // 数圈测试（极数不知道时让它按 20 Hz 转一分钟数圈）、学习相序（等于按键短按）。
    // 极数 / 学习状态改了之后 IStirrer 要重新「连接」才挂出去——通道开机时就把能力取走了。

    public IReadOnlyList<SettingsGroup> Groups { get; } = new[]
    {
        new SettingsGroup(GroupLive, "实时", new ParameterSchema(new[]
        {
            Field.Num(KFreq, "换向频率", 0, "Hz", -4000, 4000, 0.1) with { ReadOnly = true, Tip = "寄存器 0x0022，负 = 反转。转速就从它按极数算" },
            Field.Text(KRpm, "转速", "—") with { ReadOnly = true },
            Field.Num(KCurrent, "电机电流", 0, "A", 0, 16, 0.01) with { ReadOnly = true },
            Field.Num(KPwm, "输出占空比", 0, "%", -100, 100, 0.1) with { ReadOnly = true },
            Field.Text(KFault, "故障", "无故障") with { ReadOnly = true },
            Field.Text(KStall, "堵转", "未堵转") with { ReadOnly = true },
            Field.Num(KDrvTemp, "驱动器温度", 25, "℃", -40, 125, 0.1) with { ReadOnly = true },
            Field.Num(KVolt, "电源电压", 24, "V", 0, 30, 0.1) with { ReadOnly = true }
        })) { Live = true, Tip = "驱动器此刻的读数，两秒一刷。「数圈测试」转起来以后在这里看换向频率是不是 20 Hz 上下" },
        new SettingsGroup(GroupMotor, "电机参数", new ParameterSchema(new[]
        {
            Field.Num(KPoles, "极数", 0, "", 0, 64, 2) with { Tip = "寄存器 0x0073。铭牌上的极数；不知道就点下面「数圈测试」，极数 = 400 ÷ 每分钟圈数。写完存储、读回核对，然后重新「连接」" },
            Field.Num(KRated, "额定电流", 7, "A", 0.5, 10, 0.1) with { Tip = "寄存器 0x006A，按电机铭牌填；最大负载电流（0x006B）一起写成同一个值（手册 §3.2 第 5 步）。出厂 7 A 偏大" },
            Field.Num(KMaxLoad, "最大负载电流", 7, "A", 0, 10, 0.1) with { ReadOnly = true },
            Field.Num(KRatio, "减速比", 1, "", 0, 100, 0.1) with { ReadOnly = true },
            Field.Text(KLearned, "相序学习", "—") with { ReadOnly = true },
            Field.Num(KMaxHz, "最大换向频率", 2000, "Hz", 0, 3276.7, 0.1) with { ReadOnly = true }
        })) { Tip = "写进驱动器并存储（0x0160），读回核对一致才算。极数 / 学习状态变了要重新点「连接」，这一路的搅拌能力才会出来" },
        new SettingsGroup(GroupProtect, "保护", new ParameterSchema(new[]
        {
            Field.Num(KWatchdog, "通讯中断停机", 0, "s", 0, 25.5, 0.5) with { Tip = "寄存器 0x0095：上位机多久不问就自己刹车，0 = 不停（断线搅拌保持）" },
            Field.Num(KStallStop, "堵转停机时间", 0, "s", 0, 25.5, 0.1) with { Tip = "寄存器 0x008E：堵转多久自己停，0 = 不保护（卡住就按最大负载电流一直顶着）" }
        })) { Tip = "写进驱动器并存储（0x0180），读回核对。台面属性栏里「不改」的项以这里为准" }
    };

    public IReadOnlyList<SettingsAction> Actions { get; } = new[]
    {
        new SettingsAction(ActCountTest, "数圈测试（20 Hz 转 60 s 自动停）") { Group = GroupMotor, Confirm = true,
            Tip = "按 20 Hz 换向频率转一分钟然后自己停。数一分钟桨叶转了多少圈：极数 = 400 ÷ 圈数（100 圈 = 4 极、50 圈 = 8 极、200 圈 = 2 极）。桨叶要能自由转" },
        new SettingsAction(ActStop, "立即停机") { Group = GroupMotor, Tip = "寄存器 0x0040 写 0 正常停止——数圈测试中途想停就按它" },
        new SettingsAction(ActLearn, "学习相序（电机空载）") { Group = GroupMotor, Confirm = true,
            Tip = "寄存器 0x00E1 写 1，等于驱动器按键短按一下。电机要空载（桨叶不在液里），学习时电机会转几下；学成要重新「连接」" }
    };

    public async Task<ParameterSet> ReadAsync(string groupId, CancellationToken ct)
    {
        var p = new ParameterSet();
        switch (groupId)
        {
            case GroupLive:
            {
                var st = await _client.ReadStatusAsync(ct).ConfigureAwait(false);
                var (t, v) = await _client.ReadTempVoltAsync(ct).ConfigureAwait(false);
                p[KFreq] = Math.Round(st.FrequencyHz, 1);
                p[KRpm] = _poles > 0 ? $"{Math.Abs(AqmdRegisters.HzToRpm(st.FrequencyHz, _poles, _ratio)):0} rpm" : "—（极数未配，算不出）";
                p[KCurrent] = Math.Round(st.CurrentA, 2); p[KPwm] = Math.Round(st.PwmPercent, 1);
                p[KFault] = st.Fault == 0 ? "无故障" : $"{st.Fault}：{AqmdRegisters.FaultText(st.Fault)}";
                p[KStall] = st.Stall switch { 1 => "正转堵转停止", 2 => "反转堵转停止", _ => "未堵转" };
                p[KDrvTemp] = Math.Round(t, 1); p[KVolt] = Math.Round(v, 1);
                break;
            }
            case GroupMotor:
            {
                var m = await _client.ReadMotorAsync(ct).ConfigureAwait(false);
                p[KPoles] = m.Poles; p[KRated] = Math.Round(m.RatedCurrentA, 2); p[KMaxLoad] = Math.Round(m.MaxLoadCurrentA, 2);
                p[KRatio] = Math.Round(m.GearRatio, 1); p[KLearned] = m.Learned ? "已学习" : "未学习——电机转不了，先学习";
                p[KMaxHz] = Math.Round(m.MaxFrequencyHz, 1);
                break;
            }
            case GroupProtect:
            {
                var sys = await _client.ReadSystemAsync(ct).ConfigureAwait(false);
                p[KWatchdog] = Math.Round(sys.WatchdogSeconds, 1); p[KStallStop] = Math.Round(sys.StallStopSeconds, 1);
                break;
            }
            default: throw new ArgumentException($"没有这一组：{groupId}", nameof(groupId));
        }
        return p;
    }

    public async Task<IReadOnlyList<string>> WriteAsync(string groupId, ParameterSet values, CancellationToken ct)
    {
        var notes = new List<string>();
        var group = Groups.FirstOrDefault(g => g.Id == groupId) ?? throw new ArgumentException($"没有这一组：{groupId}", nameof(groupId));
        if (group.Live) { notes.Add("「实时」都是只读的，没有可写的项"); return notes; }
        foreach (var key in values.Keys.ToList())
        {
            var spec = group.Schema.Find(key);
            if (spec is null) { notes.Add($"{key}：这一组里没有这一项，没写"); continue; }
            if (spec.ReadOnly) { notes.Add($"{spec.Label}：只读，没写"); continue; }
            try
            {
                notes.Add(await WriteOneAsync(key, spec, values, ct).ConfigureAwait(false));
            }
            catch (InvalidOperationException ex) { notes.Add(ex.Message); }
            catch (ModbusException ex)
            {
                notes.Add($"{spec.Label}：{AqmdRegisters.ExplainWriteRejected(ex)}");
                _ctx.Log?.Invoke("warn", $"{InstanceId} 「{spec.Label}」没写成——{AqmdRegisters.ExplainWriteRejected(ex)}");
            }
        }
        return notes;
    }

    private async Task<string> WriteOneAsync(string key, FieldSpec spec, ParameterSet values, CancellationToken ct)
    {
        switch (key)
        {
            case KPoles:
            {
                var want = values.Int(key);
                if (want <= 0 || want > 64 || want % 2 != 0) return $"极数 {want} 不对——极数是偶数（2、4、6、8……）";
                var m = await _client.ReadMotorAsync(ct).ConfigureAwait(false);
                if (m.Poles == want) return $"极数已经是 {want}，不用写";
                await WriteVerifiedAsync("极数", $"极数 {m.Poles} → {want}",
                    () => _client.WritePolesAsync((ushort)want, ct), () => _client.StoreMotorParamsAsync(ct),
                    async () => (await _client.ReadMotorAsync(ct).ConfigureAwait(false)).Poles == want, ct).ConfigureAwait(false);
                return $"极数 {m.Poles} → {want}，已写进驱动器并存储、读回核对一致——重新点「连接」，这一路的搅拌能力才会出来";
            }
            case KRated:
            {
                var a = Math.Clamp(values.Num(key), 0.5, 10);
                var raw = (ushort)Math.Round(a * 100);
                var m = await _client.ReadMotorAsync(ct).ConfigureAwait(false);
                if (Math.Abs(m.RatedCurrentA - a) < 0.005 && Math.Abs(m.MaxLoadCurrentA - a) < 0.005) return $"额定电流已经是 {a:0.00} A，不用写";
                await WriteVerifiedAsync("额定电流", $"额定电流 {m.RatedCurrentA:0.00} → {a:0.00} A（最大负载电流同值）",
                    () => _client.WriteRatedCurrentAsync(raw, ct), () => _client.StoreMotorParamsAsync(ct),
                    async () => { var x = await _client.ReadMotorAsync(ct).ConfigureAwait(false); return Math.Abs(x.RatedCurrentA - a) < 0.005 && Math.Abs(x.MaxLoadCurrentA - a) < 0.005; }, ct).ConfigureAwait(false);
                return $"额定电流 {m.RatedCurrentA:0.00} → {a:0.00} A，最大负载电流同值，已存储、读回核对一致";
            }
            case KWatchdog or KStallStop:
            {
                var sec = Math.Clamp(values.Num(key), 0, 25.5);
                var tenths = (ushort)Math.Round(sec * 10);
                var label = key == KWatchdog ? "通讯中断停机" : "堵转停机时间";
                var sys = await _client.ReadSystemAsync(ct).ConfigureAwait(false);
                var cur = key == KWatchdog ? sys.WatchdogSeconds : sys.StallStopSeconds;
                if (Math.Abs(cur - tenths * 0.1) < 0.01) return $"{label}已经是 {(tenths == 0 ? "关" : $"{sec:0.#} s")}，不用写";
                await WriteVerifiedAsync(label, $"{label} {cur:0.#} → {(tenths == 0 ? "关" : $"{sec:0.#} s")}",
                    () => key == KWatchdog ? _client.WriteWatchdogAsync(tenths, ct) : _client.WriteStallStopAsync(tenths, ct),
                    () => _client.StoreSystemParamsAsync(ct),
                    async () => { var x = await _client.ReadSystemAsync(ct).ConfigureAwait(false); return Math.Abs((key == KWatchdog ? x.WatchdogSeconds : x.StallStopSeconds) - tenths * 0.1) < 0.01; }, ct).ConfigureAwait(false);
                return $"{label} {cur:0.#} → {(tenths == 0 ? "关" : $"{sec:0.#} s")}，已存储、读回核对一致";
            }
            default: return $"{spec.Label}：不知道怎么写";
        }
    }

    public async Task<string> RunAsync(string actionId, CancellationToken ct)
    {
        switch (actionId)
        {
            case ActCountTest:
            {
                var m = _motor ?? await _client.ReadMotorAsync(ct).ConfigureAwait(false);
                if (!m.Learned) return "没转：驱动器报相序未学习，先点「学习相序」（电机空载）";
                _countCts?.Cancel();
                _countCts = new CancellationTokenSource();
                var token = _countCts.Token;
                try
                {
                    // 加减速放缓到 20 Hz/s（一秒到 20 Hz），不管极数是多少都温和
                    await _client.WriteAccelAsync(200, 200, ct).ConfigureAwait(false);
                    _accelWritten = (200, 200);
                    await _client.WriteSpeedAsync(AqmdRegisters.SpeedReg(CountTestHz * _dirSign, _setpointX10), ct).ConfigureAwait(false);
                }
                catch (ModbusException ex) { return $"没转起来：{AqmdRegisters.ExplainWriteRejected(ex)}"; }
                var secs = Math.Max(0.1, CountTestSeconds);
                _ = Task.Run(async () =>
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(secs), token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                    try
                    {
                        await _client.StopAsync(AqmdStopMode.Normal, CancellationToken.None).ConfigureAwait(false);
                        _ctx.Log?.Invoke("info", $"{InstanceId} 数圈测试到时，已停机");
                    }
                    catch (Exception ex) { _ctx.Log?.Invoke("error", $"{InstanceId} 数圈测试到时停机失败：{ex.Message}——到面板点「立即停机」"); }
                }, CancellationToken.None);
                _ctx.Log?.Invoke("warn", $"{InstanceId} 数圈测试：按 {CountTestHz:0} Hz 换向频率{(_dirSign > 0 ? "正转" : "反转")} {secs:0} s 后自动停（由操作人在参数面板发起）");
                return $"已按 {CountTestHz:0} Hz 转起来，{secs:0} s 后自动停（中途想停点「立即停机」）。数这一分钟桨叶转了多少圈：" +
                       "极数 = 400 ÷ 圈数——100 圈 = 4 极、50 圈 = 8 极、200 圈 = 2 极。数出来填到上面「极数」点「写入」，再回台面点「连接」。「实时」那一组能看换向频率是不是 20 上下";
            }
            case ActStop:
                _countCts?.Cancel();
                try { await _client.StopAsync(AqmdStopMode.Normal, ct).ConfigureAwait(false); }
                catch (ModbusException ex) { return $"停机没写进去：{AqmdRegisters.ExplainWriteRejected(ex)}"; }
                foreach (var s in _stirrers) s.Target = 0;
                _ctx.Log?.Invoke("info", $"{InstanceId} 面板「立即停机」：0x0040 写 0");
                return "已写正常停止（0x0040 = 0），按减速度减下来";
            case ActLearn:
            {
                var st = _lastStatus ?? await _client.ReadStatusAsync(ct).ConfigureAwait(false);
                if (Math.Abs(st.FrequencyHz) > 0.5) return "没学：电机还在转，先点「立即停机」等它停稳";
                try { await _client.StartLearnAsync(ct).ConfigureAwait(false); }
                catch (ModbusException ex) { return $"学习命令被拒：{AqmdRegisters.ExplainWriteRejected(ex)}"; }
                _ctx.Log?.Invoke("warn", $"{InstanceId} 相序学习已启动（0x00E1 写 1，由操作人在参数面板发起）");
                // 手册：学习时电机转几下、几秒钟；状态 3 完毕 / 4 失败。最多等 60 s
                var last = -1;
                for (var i = 0; i < 60; i++)
                {
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                    last = await _client.ReadLearnStatusAsync(ct).ConfigureAwait(false);
                    if (last is 3 or 4) break;
                }
                var m = await _client.ReadMotorAsync(ct).ConfigureAwait(false);
                _motor = m;
                var verdict = last switch
                {
                    3 => $"学习完毕，驱动器学习状态 = {(m.Learned ? "已学习" : "仍报未学习——查接线再学一次")}。重新点「连接」，这一路的搅拌能力才会出来",
                    4 => "学习失败（0x00E2 = 4）——查电机 U/V/W 三相线与霍尔线接没接对、电机是不是空载（手册 §3.2 第 6 步）",
                    _ => $"等了 60 s 还没完（状态 {AqmdRegisters.LearnStatusText(last)}）——看驱动器指示灯：黄绿交替闪 6 次后同时长亮 1 s 是成功、黄灯再连闪 3 次是失败"
                };
                _ctx.Log?.Invoke(last == 3 ? "warn" : "error", $"{InstanceId} 相序学习：{verdict}");
                return verdict;
            }
            default: throw new ArgumentException($"没有这个动作：{actionId}", nameof(actionId));
        }
    }

    /// <summary>这一路的搅拌能力。斜坡经 IStirrerRamp 给（满量程用时），下一次 SetSpeedAsync 用掉。</summary>
    private sealed class Stirrer : IStirrer, IStirrerRamp, IStirrerStatus
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
        /// <summary>IStirrerStatus：面板开关对账用。</summary>
        public double TargetRpm => Target;
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
