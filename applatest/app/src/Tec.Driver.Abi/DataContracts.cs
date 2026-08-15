namespace Tec.Driver.Abi;

/// <summary>核心架构 §8.1。仿真数据必须自带 Simulated，绝不能和真实数据混进一份报告。</summary>
public enum Quality
{
    Good,
    /// <summary>通道还活着但这个值已经过期——绝不能拿旧值一直冒充新值。</summary>
    Stale,
    Bad,
    Simulated
}

/// <summary>界面按形状选渲染器，不按设备类型写死（§8.2）。</summary>
public enum DataShape
{
    Scalar,
    Spectrum,
    Distribution,
    Image,
    State,
    Vector
}

public sealed record ValueRange(double Min, double Max);

public sealed record AxisSpec(string Name, string? Unit, double Start, double End, int Points);

/// <summary>
/// 一路输出的描述符。第三方仪器怎么可视化，答案全在这里（§8.2 / §9.3）。
/// </summary>
public sealed record TagDescriptor(string Tag, string DisplayName, string? Unit, DataShape Shape)
{
    public AxisSpec? Axis { get; init; }
    public ValueRange? Nominal { get; init; }
    /// <summary>派生量，如 Tr−Tj。</summary>
    public string? DerivedFrom { get; init; }
    /// <summary>典型采样周期。拉曼 30 s、温度 1 s，导出时要能看出哪些点是真采到的。</summary>
    public TimeSpan? Period { get; init; }
}

/// <summary>
/// 采样点。打单调时钟戳，同时记墙钟对照，防止对时/夏令时把曲线撕开（§8.1）。
/// </summary>
public readonly record struct Sample(
    int Channel,
    string Tag,
    long MonotonicTicks,
    DateTimeOffset WallClock,
    double Value,
    Quality Quality)
{
    public TimeSpan Monotonic => TimeSpan.FromTicks(MonotonicTicks);
}

/// <summary>L2：谱图。第一版只留接口，不做预处理与建模（§9.1）。</summary>
public sealed record Spectrum(int Channel, DateTimeOffset WallClock, AxisSpec Axis, double[] Intensities, Quality Quality);

/// <summary>L2：分布（粒度、弦长）。</summary>
public sealed record Distribution(int Channel, DateTimeOffset WallClock, AxisSpec Axis, double[] Values, Quality Quality);

/// <summary>L3：仅预留。图像本身不进内存管线，只传引用。</summary>
public sealed record FrameRef(int Channel, DateTimeOffset WallClock, string Uri, int Width, int Height);

/// <summary>朴素特征值窗口——只提取，不建模（§9.3）。</summary>
public sealed record PeakDefinition(string Name, double From, double To, PeakMetric Metric);

public enum PeakMetric { Height, Area, Sum, Mean }
