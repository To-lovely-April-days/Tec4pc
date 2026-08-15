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
