namespace Tec.Driver.Abi;

// ── 上位机 PID 的整定台（面板「PID 整定」页用）──────────────────────────
//
// 跟 ITemperatureTuning 的区别：那个是「策略 + 一组参数 + 自整定」的最小契约，配方与引擎能懂；
// 这个是把回路的整套调参面摆给人看、给人改：按执行器分的两张增益表（各温度段的 PID）、
// 手动参数、此刻在用的参数、每拍的 P/I/D、继电器法自整定。只有「控温方式 = 上位机 PID」
// 的会话才提供（温控器 PID 方式下 PID 在温控器里，面板说一句去参数窗）。
//
// 单位照 TecControl.App 的增益表：内环 Kp %/℃、Ki %/(℃·s)、Kd %·s/℃；
// 外环 Kp ℃/℃、Ki 1/s、Kd s；偏置上限 ℃。

/// <summary>执行器。增益表按它分两张：同一温度，TEC 和加热棒的过程增益差一个量级。</summary>
public enum PidActuator
{
    /// <summary>TEC（双向；「TEC 加热」不启用的机器上只制冷）。</summary>
    Tec,

    /// <summary>电加热棒（PWM 固态继电器，只加热）。</summary>
    Heater
}

/// <summary>增益表里的一行：某温度下的内环 PID、串级外环、加热比，外加自整定与运行中学到的只读量。</summary>
public sealed record PidGainRow(double TemperatureC, double Kp, double Ki, double Kd)
{
    public double? OuterKp { get; init; }
    public double? OuterKi { get; init; }
    public double? OuterKd { get; init; }
    /// <summary>串级外环偏置上限 ±℃（外环最多把夹套设定拉离釜内目标多少）。</summary>
    public double? OuterMaxBiasC { get; init; }
    /// <summary>
    /// 加热比 = 加热 / 制冷有效度之比；空 = 用全局值。TEC 表那行的给 TEC 双向用（正半轴除以它）；
    /// 加热棒表那行的给「加热棒 + TEC 双向」（0336 双向挡）用——PID 按加热棒整，负半轴给 TEC 时乘它。只加热不用。
    /// </summary>
    public double? HeatRatio { get; init; }

    /// <summary>自整定测得的临界增益（只读）；手工行是 0。</summary>
    public double Ku { get; init; }
    /// <summary>自整定测得的临界周期 s（只读）；手工行是 0。</summary>
    public double TuSeconds { get; init; }
    /// <summary>运行中学到的稳态偏置 ℃（夹套设定 − 釜内目标，只读）；没学到是 null。</summary>
    public double? SteadyBiasC { get; init; }

    public bool FromAutoTune => Ku > 0;
}

/// <summary>手动参数：增益表空着 / 这一路关了增益调度时回路用的那组。</summary>
public sealed record PidManual(PidTuning Inner, PidTuning Outer, double OuterMaxBiasC);

/// <summary>某执行器在某温度实际会用的参数，以及每一项从哪来（增益表插值 / 手动）。</summary>
public sealed record PidInUse(
    double AtC,
    PidActuator Actuator,
    PidTuning Inner,
    bool InnerFromTable,
    PidTuning Outer,
    double OuterMaxBiasC,
    bool OuterFromTable,
    double? SteadyBiasC,
    double HeatRatio)
{
    /// <summary>
    /// 此刻是「加热棒 + TEC 双向」（0336 双向挡）：执行器算加热棒（参数按加热棒那张表），但负半轴也出力（TEC 制冷，乘加热比）。
    /// 预览（Preview）永远是 false。
    /// </summary>
    public bool Bidirectional { get; init; }
}

/// <summary>回路最近一拍（约 0.5 s 一拍）。</summary>
public sealed record PidLive(
    bool Active,
    bool Tuning,
    int TuneCycles,
    double SetpointC,
    double MeasuredC,
    double DutyPercent,
    double P,
    double I,
    double D,
    bool Cascade,
    double InnerSetpointC,
    double OuterMeasuredC,
    PidActuator Actuator,
    DateTimeOffset At);

/// <summary>发起一次继电器法自整定。</summary>
/// <param name="SetpointC">整定温度（℃）：在它附近激起振荡，结果登记为这一温度的工作点。</param>
/// <param name="Actuator">用哪个执行器整（结果登记进哪张表）。</param>
/// <param name="RelayAmplitudePercent">继电幅值 %（5~100）。</param>
/// <param name="HysteresisC">回差 ℃（0.01~5）；比读数噪声大一点。</param>
public sealed record PidAutoTuneRequest(
    double SetpointC,
    PidActuator Actuator,
    double RelayAmplitudePercent = 30,
    double HysteresisC = 0.05);

/// <summary>自整定结束（成功 / 失败 / 取消 / 被停下）。</summary>
public sealed record PidAutoTuneReport(bool Success, string? Reason, double SetpointC, PidActuator Actuator)
{
    public double? Ku { get; init; }
    public double? TuSeconds { get; init; }
    /// <summary>测得的振荡幅度 ℃（峰到峰的一半）。</summary>
    public double? OscillationC { get; init; }
    public double? RelayAmplitudePercent { get; init; }
    /// <summary>平稳型（Tyreus–Luyben，温控推荐，自动登记进表的就是它）。</summary>
    public PidTuning? Conservative { get; init; }
    /// <summary>快速型（Ziegler–Nichols，可能超调，只供参考）。</summary>
    public PidTuning? Fast { get; init; }
    /// <summary>平稳型已经登记进 Actuator 那张表。</summary>
    public bool Registered { get; init; }
}

/// <summary>
/// 上位机 PID 的整定台（每个工位一个）。**不是配方指令**：自整定要激起振荡，
/// 改表会直接改变回路行为，都是有人在场的调试动作，只从手动面板调用。
/// </summary>
public interface IPidTuningBench : ICapability
{
    /// <summary>这张表存在哪（给人看）。</summary>
    string TablePath(PidActuator actuator);

    /// <summary>表里的行，按温度从低到高。</summary>
    IReadOnlyList<PidGainRow> Rows(PidActuator actuator);

    /// <summary>
    /// 整张替换并存盘。行不合法（温度 / 参数不是数、负数、外环只填一半……）抛 ArgumentException，
    /// 原表不动；温度相差 2 ℃ 以内的两行算同一个工作点，后面的覆盖前面的。
    /// </summary>
    void ApplyRows(PidActuator actuator, IReadOnlyList<PidGainRow> rows);

    /// <summary>两张表和手动参数都从盘上重读（丢掉没应用的改动、拿回别处改过的）。</summary>
    void Reload();

    /// <summary>这一路按不按增益表走（关了就固定用手动参数）。</summary>
    bool Scheduling { get; }
    void SetScheduling(bool on);

    PidManual Manual { get; }
    /// <summary>改手动参数并存盘。参数不合法抛 ArgumentException。</summary>
    void SetManual(PidManual manual);

    /// <summary>此刻执行器（继电器扳在哪一侧 / 回路的执行器形态）。</summary>
    PidActuator CurrentActuator { get; }

    /// <summary>回路此刻真正在用的参数：内环按内环设定（串级时 = 外环算出的夹套设定）查、外环按主设定查。</summary>
    PidInUse InUse { get; }

    /// <summary>某执行器在某温度会用什么参数（按当前表与开关；表里没有就是手动那组）。</summary>
    PidInUse Preview(PidActuator actuator, double temperatureC);

    /// <summary>最近一拍；回路还没转过是 null。</summary>
    PidLive? Live { get; }

    /// <summary>这个温度建议用哪个执行器整（按设备的热源策略）。</summary>
    PidActuator SuggestActuator(double setpointC);

    /// <summary>现在能不能按这个请求整：null = 可以；否则是一句原因（温度越限 / 夹套太烫接不回 TEC / 没有电加热……）。</summary>
    string? CheckTune(PidAutoTuneRequest request);

    bool Tuning { get; }

    /// <summary>整定进度的一句话（「在 25.0 ℃ 附近振荡，已完成 2 个周期（一般 4 个）」）。</summary>
    string TuneNote { get; }

    /// <summary>
    /// 开始自整定。这一路正在控温的话**先停控温**（调用方应先问过人），再按执行器把热源扳好、
    /// 让回路进入继电器振荡。结束（成功 / 失败 / 取消 / 被安全停机停下）发 AutoTuneFinished，
    /// 输出关掉、继电器断开；成功时平稳型参数自动登记进对应那张表并存盘。
    /// </summary>
    Task StartAutoTuneAsync(PidAutoTuneRequest request, CancellationToken ct);

    Task CancelAutoTuneAsync(CancellationToken ct);

    event Action<PidAutoTuneReport>? AutoTuneFinished;

    /// <summary>表被回路改了（自整定登记、学到稳态偏置、偏置上限放宽）或重读了。可能在任何线程上来。</summary>
    event Action? TablesChanged;
}
