namespace Tec.Driver.Abi;

/// <summary>
/// 整套架构的地基：上层永远不问"这是什么设备"，只问"它能不能做这件事"（§3.2）。
/// </summary>
public interface ICapability
{
    /// <summary>这份能力实例属于哪个通道。台面装配时由宿主填好。</summary>
    int Channel { get; }
}

public interface ICapabilityLookup
{
    T? Get<T>() where T : class, ICapability;
    bool Has<T>() where T : class, ICapability;
    IReadOnlyList<ICapability> All { get; }
}

// ── 限值：由设备自己给出，不在界面里写死 ──────────────────────────────

public sealed record TempLimits(double Min, double Max, double MaxRatePerMin);
public sealed record SpeedLimits(double Min, double Max);
public sealed record FlowLimits(double Min, double Max, double MaxVolume);

public sealed record CalibrationRecord(
    string Kind,
    DateTimeOffset At,
    string User,
    string Standard,
    double Result,
    DateTimeOffset? ExpiresAt)
{
    public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } e && now > e;
}

// ── 能力契约 ─────────────────────────────────────────────────────────

public enum TempChannelKind { Reactor, Jacket }

public sealed record TempTarget(double Value, TempChannelKind Kind = TempChannelKind.Reactor);

public interface ITemperatureControl : ICapability
{
    TempLimits Limits { get; }
    /// <summary>Tr 当前值。用于估算与安全层，不是控制回路的一部分（§7.7）。</summary>
    double CurrentReactor { get; }
    double CurrentJacket { get; }
    Task SetTargetAsync(TempTarget target, CancellationToken ct);
    Task RampAsync(double target, double ratePerMin, TempChannelKind kind, CancellationToken ct);
    /// <summary>等待到达，由下位机判稳；上位机只等结果。</summary>
    Task<bool> WaitReachedAsync(double target, double tolerance, TimeSpan timeout, CancellationToken ct);
    Task StopAsync(CancellationToken ct);
    IObservable<Sample> Temperature { get; }
}

/// <summary>
/// 控温回路此刻的状态（可选能力，设备会话知道就实现）：设定值是多少、回路开着没有。
/// 手动面板重开 / 程序重启之后拿它对账——面板上那颗「温控」开关和目标框跟着回路的真实状态走，
/// 不跟着面板自己上一次的记忆走。Setpoint 没下发过就是 null，不假装有一个。
/// </summary>
public interface ITemperatureStatus : ICapability
{
    double? Setpoint { get; }
    /// <summary>回路是不是在控温（意图）：下发过目标且没停。热源切换序列中途临时关掉输出的那两秒不算停。</summary>
    bool Active { get; }

    /// <summary>
    /// 控温回路闭在上位机（true）还是设备自己（false）。界面据此把话说对：
    /// 「尽快」是温控器按自己的最大能力走，还是上位机回路按最大输出（限幅）走。缺省 false。
    /// </summary>
    bool HostLoop => false;

    /// <summary>
    /// 串级（釜内控温）时外环算出来的夹套设定值——内环此刻真正在追的数。
    /// 不是串级 / 没在控 / 设备不报就是 null（缺省 null），不拿目标冒充。
    /// </summary>
    double? CascadeInnerSetpoint => null;
}

/// <summary>自整定当前的状态。</summary>
public enum TuningState { Idle, Running, Succeeded, Failed, Cancelled }

/// <summary>一组 PID 参数。整定出来的、手填的，都是这个形状。</summary>
public sealed record PidTuning(double Kp, double Ki, double Kd)
{
    public override string ToString() => $"Kp={Kp:F3} Ki={Ki:F4} Kd={Kd:F3}";
}

/// <summary>自整定的结果。失败时 Gains 为 null，Reason 说明为什么。</summary>
public sealed record TuningOutcome(bool Success, PidTuning? Gains, string? Reason)
{
    /// <summary>整定时的工作点（℃）。同一台设备不同温度段的参数不一样，得记住是在哪儿整的。</summary>
    public double? SetpointC { get; init; }
    /// <summary>整的是釜内环（串级外环）还是夹套环。</summary>
    public TempChannelKind Kind { get; init; } = TempChannelKind.Jacket;
}

/// <summary>
/// PID 整定与控制策略。**它不是配方指令**——自整定要激起温度振荡，
/// 是设备调试 / 维护动作，属于手动控制面板，不该出现在配方的步骤库里：
/// 谁也不希望一条配方跑到一半自己去整定一遍。
///
/// 设备支持就实现，不支持就不提供这个能力，界面据此决定要不要显示整定入口（§3.2）。
/// </summary>
public interface ITemperatureTuning : ICapability
{
    /// <summary>釜内 Tr（串级）还是夹套 Tj（单环）。配方里的「釜内控温 / 夹套控温」也走它。</summary>
    TempChannelKind Strategy { get; }
    Task SetStrategyAsync(TempChannelKind kind, CancellationToken ct);

    /// <summary>当前生效的参数。串级时 Kind=Reactor 取外环、Jacket 取内环。</summary>
    PidTuning GetGains(TempChannelKind kind);
    Task SetGainsAsync(TempChannelKind kind, PidTuning gains, CancellationToken ct);

    TuningState TuningState { get; }
    /// <summary>整定进度的粗略描述，给界面显示用（「正在寻找振荡」「第 3 个周期」…）。</summary>
    string TuningNote { get; }
    event EventHandler<TuningOutcome>? TuningFinished;

    /// <summary>
    /// 启动继电器法自整定。会在设定值附近激起小幅振荡——**必须由人在场发起**，
    /// 所以只从控制面板调用，绝不由配方触发。
    /// </summary>
    Task StartTuningAsync(double setpointC, TempChannelKind kind, CancellationToken ct);

    Task CancelTuningAsync(CancellationToken ct);
}

/// <summary>
/// 蒸回流（夹套跟随）：夹套目标 = 釜内实测 + ΔT，持续跟着走。
/// 回流的物理形态是 Tr 停在沸点平台、Tj 恒高 ΔT——固定目标的
/// ITemperatureControl 表达不了这种随动，所以单立一个可选能力：
/// 设备支持才提供（§3.2），HMI 的 TrTj 格子与配方校验都按 Has&lt;T&gt;() 判。
/// 实现方自己负责限速与钳制：Tj 目标不得越过 maxTj，Tr 无效（探头断线）
/// 必须停跟随，安全动作（SafeStop / E 级程序）之后不得把目标写回去。
/// </summary>
public interface IRefluxControl : ICapability
{
    /// <summary>开始跟随。deltaT = Tj 高出 Tr 的常差（K），maxTj = 夹套目标上限（℃）。</summary>
    Task StartAsync(double deltaT, double maxTj, CancellationToken ct);

    /// <summary>停止跟随。控温目标停在最后一次下发的值上——跟「控温」步结束后的
    /// 语义一致，收尾往哪走由下一步（或安全停机）决定，不在这里替工艺做主。</summary>
    Task StopAsync(CancellationToken ct);

    bool Active { get; }
}

/// <summary>
/// 热源通路（双工位主机的 TEC ⇄ 电加热切换）。界面靠它说清楚这一路**现在能往哪个方向出力**：
/// 「TEC 加热」没启用时 TEC 侧只能制冷、电加热侧只能升温，光看一个「热源 TEC」不够——
/// 操作人得知道这一刻要降温还是升温办不办得到。
/// 设备支持才提供（§3.2），界面按 Has&lt;T&gt;() 判，没有就什么都不显示。
/// </summary>
public interface IHeatSource : ICapability
{
    /// <summary>TEC 反向输出加热启用了没有。false = 所有加热都走电加热棒，TEC 只用来制冷。</summary>
    bool TecHeating { get; }

    /// <summary>电加热通路可不可用（配了切换模块而且开着）。</summary>
    bool ElectricAvailable { get; }

    /// <summary>当前在哪一侧：false = TEC，true = 电加热。</summary>
    bool OnElectric { get; }
}

public interface IStirrer : ICapability
{
    SpeedLimits Limits { get; }
    double CurrentRpm { get; }
    Task SetSpeedAsync(double rpm, CancellationToken ct);
    Task StopAsync(CancellationToken ct);
    IObservable<Sample> Speed { get; }
}

/// <summary>
/// 可选：加减速斜坡时间可设的搅拌器（IStirrer 的实现顺带实现它）。
/// 真机各有各的加减速寄存器，不是每台都给设——所以不进 IStirrer 本体，
/// 面板与执行器按「是不是它」问，不认识任何具体设备类。
/// </summary>
public interface IStirrerRamp
{
    /// <summary>满量程加减速用时（秒）。SetSpeedAsync 之前设好。</summary>
    void SetRampSeconds(double seconds);
}

public sealed record DoseRequest(double Volume, double RatePerMin)
{
    /// <summary>加料的物料名，只用于记录与报告。</summary>
    public string? Material { get; init; }
}

public interface IDosing : ICapability
{
    FlowLimits Limits { get; }
    /// <summary>未标定 = null。编排时要拦下来（§10.3）。</summary>
    CalibrationRecord? Calibration { get; }
    double TotalVolume { get; }
    Task DoseAsync(DoseRequest request, CancellationToken ct);
    Task SetRateAsync(double ratePerMin, CancellationToken ct);
    Task StopAsync(CancellationToken ct);
    IObservable<Sample> Flow { get; }
    IObservable<Sample> Total { get; }
}

/// <summary>pH、浊度、压力、任何单值。第三方接入的第一优先级（L1，§9.3）。</summary>
public interface IScalarSensor : ICapability
{
    IReadOnlyList<TagDescriptor> Tags { get; }
    bool TryReadLatest(string tag, out Sample sample);
    IObservable<Sample> Values { get; }
}

public interface ISpectrumSource : ICapability
{
    AxisSpec Axis { get; }
    IReadOnlyList<PeakDefinition> Peaks { get; }
    IObservable<Spectrum> Spectra { get; }
}

public interface IDistributionSource : ICapability
{
    AxisSpec Axis { get; }
    IObservable<Distribution> Distributions { get; }
}

/// <summary>FR-2.3 LED 背景灯。</summary>
public interface IIllumination : ICapability
{
    Task SetAsync(bool on, double brightness, CancellationToken ct);
}

/// <summary>L3：仅预留接口（§9.3）。</summary>
public interface IImageSource : ICapability
{
    IObservable<FrameRef> Frames { get; }
}
