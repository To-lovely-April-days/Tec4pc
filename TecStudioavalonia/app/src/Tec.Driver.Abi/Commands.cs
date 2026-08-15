namespace Tec.Driver.Abi;

/// <summary>
/// 一条指令靠什么结束——执行记录里最值钱的一列，必须在指令声明时就定死（§4.3）。
/// 原型验证过：偏差模型只能按结束条件分类，按模块分类会出现
/// "计时到的步骤跑短了 99 秒"这种自相矛盾的记录。
/// </summary>
public enum TerminationKind
{
    /// <summary>到达目标值（到温、到转速）。只会偏慢。</summary>
    Setpoint,
    /// <summary>计时到。几乎不偏。</summary>
    Timer,
    /// <summary>加完设定量。偏差小，受标定精度影响。</summary>
    Quantity,
    /// <summary>条件满足（pH、浊度、外部信号）。两个方向都可能，偏差最大。</summary>
    Condition,
    /// <summary>等操作人。</summary>
    Operator,
    /// <summary>报警中止。</summary>
    Alarm,
    /// <summary>超时保护。</summary>
    Timeout,
    /// <summary>
    /// 下发即完成（设定转速、开背景灯、写标记）。
    /// 架构文档 §4.3 的表里没有单列这一类，但原型的偏差模型里有 inst，
    /// 实装时必须区分——否则"零时长步骤"会被算进偏差统计。
    /// </summary>
    Immediate
}

public enum EndReason
{
    Reached,
    TimerElapsed,
    QuantityDelivered,
    ConditionMet,
    OperatorConfirmed,
    Completed,
    Timeout,
    Alarm,
    Aborted,
    Skipped,
    Failed
}

public sealed record CommandOutcome(EndReason Reason, TimeSpan Actual)
{
    public string? Note { get; init; }
    public static CommandOutcome Instant(EndReason reason = EndReason.Completed)
        => new(reason, TimeSpan.Zero);
}

/// <summary>
/// 时长估算的上下文。必须按顺序走一遍整条配方串行传递——
/// 升温耗时取决于当前温度，不能逐步独立计算（§4.4）。
/// </summary>
public sealed class EstimationContext
{
    public double Temperature { get; set; } = 25;
    public double Jacket { get; set; } = 25;
    public double Rpm { get; set; }
    public double Volume { get; set; }
    public double Ph { get; set; } = 7;
    public Dictionary<string, double> Extra { get; } = new(StringComparer.Ordinal);

    public EstimationContext Clone()
    {
        var c = new EstimationContext
        {
            Temperature = Temperature,
            Jacket = Jacket,
            Rpm = Rpm,
            Volume = Volume,
            Ph = Ph
        };
        foreach (var kv in Extra) c.Extra[kv.Key] = kv.Value;
        return c;
    }
}

/// <summary>
/// 一条指令的全部输入：标量参数 + 多分段表的行。
/// 梯度控温、分段加料的时长只能由行推算，所以估算与描述拿到的必须是这个，
/// 而不是光秃秃一个 ParameterSet。
/// </summary>
public sealed record CommandInput(ParameterSet Parameters, IReadOnlyList<ParameterSet>? Rows = null)
{
    public static implicit operator CommandInput(ParameterSet p) => new(p);

    public IReadOnlyList<ParameterSet> RowsOrEmpty => Rows ?? Array.Empty<ParameterSet>();

    public bool Has(string key) => Parameters.Has(key);
    public double Num(string key, double fallback = 0) => Parameters.Num(key, fallback);
    public int Int(string key, int fallback = 0) => Parameters.Int(key, fallback);
    public string Str(string key, string fallback = "") => Parameters.Str(key, fallback);
    public bool Flag(string key, bool fallback = false) => Parameters.Flag(key, fallback);
    public TimeSpan Duration(string key, TimeSpan fallback = default) => Parameters.Duration(key, fallback);
}

/// <summary>纯函数、确定性。同样输入必须得到同样结果，否则排期每次刷新都在跳（§4.4）。</summary>
public delegate TimeSpan DurationEstimator(CommandInput input, EstimationContext ctx);

/// <summary>生成整句话："升温 釜内 Tr 至 60 ℃，2 ℃/min"。</summary>
public delegate string DescriptionTemplate(CommandInput input);

public sealed record CommandDescriptor(
    string Id,
    string DisplayName,
    string Module,
    Type? RequiredCapability,
    ParameterSchema Parameters,
    TerminationKind Termination,
    DurationEstimator Estimate,
    DescriptionTemplate Describe)
{
    public string? IconKey { get; init; }
    /// <summary>
    /// 除 RequiredCapability 之外还要具备的能力。
    /// 反馈加料就是典型：既要 IDosing 才能加，又要 IScalarSensor 才有判据。
    /// </summary>
    public IReadOnlyList<Type> AlsoRequires { get; init; } = Array.Empty<Type>();
    /// <summary>当前步是否允许热改（§7.6）。</summary>
    public bool SupportsHotEdit { get; init; }
    /// <summary>Setpoint / Condition 指令必须有超时保护，否则到不了的目标会把通道永远挂住。</summary>
    public bool RequiresTimeout => Termination is TerminationKind.Setpoint or TerminationKind.Condition;
    public string? Tip { get; init; }
}

public sealed class CommandContext
{
    public required int Channel { get; init; }
    public required ICapabilityLookup Capabilities { get; init; }
    public required Func<DateTimeOffset> Now { get; init; }
    /// <summary>写一条事件到执行记录（等待资源、外部信号失效…）。</summary>
    public Action<string>? Note { get; init; }
    /// <summary>0..1，界面上的单步进度。没有可靠进度的指令不要瞎报。</summary>
    public Action<double>? Progress { get; init; }
    /// <summary>仿真会话把它设成 >1 以加速演示；真实会话恒为 1。</summary>
    public double TimeScale { get; init; } = 1;
}

public interface ICommandHandler
{
    Task<CommandOutcome> ExecuteAsync(CommandContext ctx, CommandInput input, CancellationToken ct);
}

/// <summary>驱动声明自己能做哪些可编排操作，配方从这里选（§4.1）。</summary>
public interface ICommandProvider
{
    IReadOnlyList<CommandDescriptor> Commands { get; }
    ICommandHandler? Resolve(string commandId);
}
