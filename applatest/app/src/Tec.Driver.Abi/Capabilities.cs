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

public interface IStirrer : ICapability
{
    SpeedLimits Limits { get; }
    double CurrentRpm { get; }
    Task SetSpeedAsync(double rpm, CancellationToken ct);
    Task StopAsync(CancellationToken ct);
    IObservable<Sample> Speed { get; }
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
