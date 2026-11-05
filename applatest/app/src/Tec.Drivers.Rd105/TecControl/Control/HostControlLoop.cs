using TecControl.Core.Models;
using TecControl.Core.Protocol;

namespace TecControl.Core.Control;

/// <summary>控温策略：直接单环（Tj 模式）或串级（Tr 模式）。</summary>
public enum ControlStrategy
{
    /// <summary>单环：PID 直接以所选反馈传感器为被控量驱动 TEC（对应 Tj 夹套/腔体模式）。</summary>
    Direct,

    /// <summary>串级：外环以内部温度为被控量输出内环设定值，内环驱动 TEC（对应 Tr 釜内模式）。</summary>
    Cascade,
}

/// <summary>单个通道在一个控制周期内的状态快照。</summary>
public sealed record ChannelCycleInfo(
    bool Active,
    bool Tuning,
    int TuneCycles,
    double SetpointC,
    double MeasuredC,
    double DutyPercent,
    double PTerm,
    double ITerm,
    double DTerm)
{
    /// <summary>本周期是否运行在串级模式。</summary>
    public bool Cascade { get; init; }

    /// <summary>串级时外环算出的内环设定值（℃）；单环时等于 SetpointC。</summary>
    public double InnerSetpointC { get; init; } = double.NaN;

    /// <summary>串级时外环被控量（内部温度 ℃）。</summary>
    public double OuterMeasuredC { get; init; } = double.NaN;

    /// <summary>正在执行温度曲线时的进度（0~1）；未执行为 null。</summary>
    public double? ProfileProgress { get; init; }

    /// <summary>正在执行的曲线名称；未执行为 null。</summary>
    public string? ProfileName { get; init; }

    /// <summary>【本地改动】串级外环的釜内 Tr 读数丢了、正在保持（外环冻结、内环设定钳到主设定）：已保持多少秒；没在保持为 null。</summary>
    public double? OuterHoldSeconds { get; init; }

    /// <summary>【本地改动】温控器回显（实际存下）的占空比；跟 DutyPercent 不一样时说明设备按自己的规矩改了。没回显过为 null。</summary>
    public double? AppliedDutyPercent { get; init; }

    /// <summary>【本地改动】串级外环这一拍的 P / I / D（℃）；单环为 NaN。逐拍记录排查外环积分攒了多少用。</summary>
    public double OuterP { get; init; } = double.NaN;
    public double OuterI { get; init; } = double.NaN;
    public double OuterD { get; init; } = double.NaN;

    /// <summary>【本地改动】串级制冷段的夹套压低量（K，<see cref="HostControlLoop.SetOuterPushDown"/>）；没压 / 单环为 NaN。</summary>
    public double OuterPushC { get; init; } = double.NaN;

    /// <summary>【本地改动】大降温收尾这一拍在外环 P / I / D 之外多给的偏置（K，<see cref="HostControlLoop.OuterFinishGain"/>）；
    /// 内环设定 = 主设定 + P + I + D + 它（再夹限幅）。单环 / Tr 丢失保持中为 NaN。</summary>
    public double OuterFinishC { get; init; } = double.NaN;

    /// <summary>【本地改动】升温加速：这一拍夹套目标归速度环管。</summary>
    public bool HeatBoosting { get; init; }

    /// <summary>【本地改动】升温加速：釜内实测升温速度（K/min，最近 40 s 最小二乘）、刹车曲线允许的速度（K/min）、刹车偏置分数（0–1）；不在速度环 NaN。</summary>
    public double HeatBoostVmCPerMin { get; init; } = double.NaN;
    public double HeatBoostVstarCPerMin { get; init; } = double.NaN;
    public double HeatBoostBrake { get; init; } = double.NaN;
}

/// <summary>一个完整控制周期的结果（后台线程发布）。电流每 4 个周期采样一次，其余周期为 null。</summary>
public sealed record ControlCycleResult(
    TecSnapshot Snapshot,
    ChannelCycleInfo Ch1,
    ChannelCycleInfo Ch2,
    TecErrorCode? ErrorCode,
    double? Current1A = null,
    double? Current2A = null)
{
    /// <summary>本周期实际通信+计算耗时（ms）。用于判断串口带宽是否成为瓶颈。</summary>
    public double CycleMs { get; init; }

    /// <summary>耗时占控制周期的比例（0~1+）。接近或超过 1 说明周期设得过短。</summary>
    public double CycleLoad { get; init; }
}

/// <summary>自整定结束通知。</summary>
public sealed record AutoTuneOutcome(int Channel, bool Success, AutoTuneResult? Result, string? Reason);

/// <summary>
/// 执行器形态。手动换线过渡方案（DAM 路由模块到货前）：一次运行内执行器固定，
/// 由人工改接线决定，软件按所选形态约束输出方向。
/// </summary>
public enum ActuatorMode
{
    /// <summary>TEC 双向（默认）：正加热负制冷，占空比 ±上限。</summary>
    Bidirectional,

    /// <summary>
    /// 电加热棒（只加热）：>100℃ 段手动换线后使用。占空比恒 ≥0——固件把正占空比
    /// 路由到该通道的加热 PWM 引脚（通道 1 = PWM2）驱动过零型 SSR 通断市电；
    /// TEC 功率线此时应已人工断开，"降温"只能靠自然散热。
    /// 厂家已确认（2026-08-03）：PWM 引脚与 TEC 口同步，使能必须保持打开；
    /// 固件无通信看门狗（断线时保持输出），设备端过温保护是唯一无通信兜底，
    /// 使用前必须先按目标温度改写双通道过温阈值。
    /// </summary>
    HeatOnly,

    /// <summary>
    /// 【本地改动】TEC 只制冷：占空比恒 ≤0。双工位主机「TEC 加热」不启用时 TEC 侧用它——反向那一极物理上接的是
    /// 加热棒的 SSR、而加热棒继电器在 TEC 侧是断开的，正输出什么都不发生；按双向算的话 PID 往正半轴白攒积分，
    /// 要制冷时得先把它退掉（几分钟到几十分钟没有冷）。
    /// </summary>
    CoolOnly,

    /// <summary>
    /// 【本地改动】（0336）加热棒 + TEC 双向：**一个 PID，正半轴给加热棒、负半轴给 TEC 制冷**，占空比 ±上限。
    /// 双工位主机「冷水机」标已开时用它（双向挡）——反应会放热也会吸热，控得住就得两边随时能出力；
    /// 哪只继电器合着由组合会话按 PID 输出的符号扳（连续 3 s 同号才扳），回路本身不管继电器。
    /// 参数按**双向挡那张表**（GainTable.Both：整定时就是加热棒 ↔ TEC 来回推），负半轴乘该行「加热比」再给 TEC——
    /// 整定时那个比值就是 TEC % ÷ 加热棒 %，整定和控温看到的是同一个对象。
    /// 双向挡里 TEC 暂时接不上（制冷段挂起）时形态换成只加热、表不换——同一组参数，只改下限、不复位。
    /// </summary>
    HeaterTec,
}

/// <summary>
/// 【本地改动】增益表：TEC、加热棒、双向挡各一张。同一温度三者的对象不一样——TEC 与加热棒的过程增益差一个量级，
/// 双向挡是两只一起（整定时冲过头切 TEC、掉下来切加热棒）。表跟着热源策略走，不跟着形态走：
/// 双向挡里 TEC 暂时接不上按只加热算，照样查双向挡那张。换表才是换对象（PID 复位、按前馈预置）。
/// </summary>
public enum GainTable
{
    Tec,
    Heater,
    Both,
}

/// <summary>四片 TEC（两路输出）的同步方式。</summary>
public enum ChannelSyncMode
{
    /// <summary>上位机镜像（推荐）：设备 CONTMODE=0，程序每次把同一占空比分别写给两个通道。
    /// 显式、可验证、可配平，不依赖读不回来的固件联动状态。</summary>
    HostMirror,

    /// <summary>硬件跟随：依赖设备 CONTMODE=2（通道 2 输出由固件复制通道 1）。</summary>
    HardwareFollow,

    /// <summary>【本地改动】两路各自独立：TC1 = 工位 A、TC2 = 工位 B 各有各的回路和设定值，
    /// 谁也不镜像谁、停一路不动另一路（双工位反应主机的拓扑，设备 CONTMODE=0）。</summary>
    Independent,
}

/// <summary>
/// 上位机控温主循环：设备仅作温度采集与功率驱动（MODE=3 通信设定电压百分比），
/// 闭环计算完全在本类中完成 —— 每周期：读温度 → PID 计算 → 写输出占空比。
/// 支持继电器法 PID 自整定（RelayAutoTuner）。
///
/// 安全机制：
///   1. 停止控温 / 断开连接 / 程序退出时输出归零并关闭使能；
///   2. 通道传感器读数丢失（未接/断线）时立即归零该通道并停控；
///   3. 连续通信失败达到阈值时尝试全部归零并停控（设备端 LIMITED/SETCURRENT/过温保护作硬件兜底）；
///   4. 自整定超时自动终止并归零。
/// </summary>
/// <summary>【本地改动】升温加速档位（见 <see cref="HostControlLoop.HeatBoost"/>）。</summary>
public enum HeatBoostLevel
{
    /// <summary>不加速：升温照现在的外环。</summary>
    Off,
    /// <summary>保守：刹车曲线 Tb 250 s、夹套最多高出目标 15 K——第一次上现场用。</summary>
    Conservative,
    /// <summary>正常：Tb 200 s、夹套最多高出目标 19 K。</summary>
    Normal,
}

public sealed class HostControlLoop(TecController controller) : IDisposable
{
    private sealed class ChannelState
    {
        public readonly PidController Pid = new();
        public readonly RunawayDetector Runaway = new();

        /// <summary>已就长期饱和发出过警告，避免刷屏。</summary>
        public bool SaturationWarned;

        /// <summary>最近一次稳态下的 PID 内部输出（未取反），用于下次启动的积分预置。
        /// 仅在误差足够小且未饱和时以低通方式更新。【本地改动】按执行器各存一份：加热棒的 +15 % 和 TEC 的 −20 %
        /// 不是一个量纲，切换时拿另一个执行器的数预置只会错。</summary>
        public double? LastSteadyOutput
        {
            get => Table switch { GainTable.Heater => HeaterSteadyOutput, GainTable.Both => BothSteadyOutput, _ => TecSteadyOutput };
            set
            {
                switch (Table)
                {
                    case GainTable.Heater: HeaterSteadyOutput = value; break;
                    case GainTable.Both: BothSteadyOutput = value; break;
                    default: TecSteadyOutput = value; break;
                }
            }
        }
        public double? TecSteadyOutput;
        public double? HeaterSteadyOutput;
        public double? BothSteadyOutput;

        /// <summary>【本地改动】此刻查哪张增益表（表 / 前馈 / 稳态输出都按它分），由 SetActuatorMode 跟形态一起定。</summary>
        public GainTable Table = GainTable.Tec;

        /// <summary>跨设定值的稳态前馈模型（自动学习"任意温度需要多少输出"）。【本地改动】按表各一份、不按 100 ℃ 分段
        /// （段按执行器分，不按温度）——切换时拿另一个对象学到的稳态输出去预置只会错。</summary>
        public readonly SteadyStateFeedforward TecFeedforward = new() { SegmentBoundaryC = double.NaN };
        public readonly SteadyStateFeedforward HeaterFeedforward = new() { SegmentBoundaryC = double.NaN };
        public readonly SteadyStateFeedforward BothFeedforward = new() { SegmentBoundaryC = double.NaN };
        public SteadyStateFeedforward Feedforward => Table switch
        {
            GainTable.Heater => HeaterFeedforward,
            GainTable.Both => BothFeedforward,
            _ => TecFeedforward
        };

        /// <summary>增益调度表（自整定结果按温度登记，运行时插值）。</summary>
        public readonly PidGainSchedule GainSchedule = new();

        /// <summary>【本地改动】加热棒（只加热执行器）那张增益表。双工位主机上加热棒不只在高温段用——
        /// 「TEC 加热」不启用时阈值以下的升温也走它，同一温度可能是 TEC 也可能是加热棒在出力，
        /// 按温度分段（SegmentBoundaryC）分不开，只能按执行器分两张表。</summary>
        public readonly PidGainSchedule HeaterSchedule = new();

        /// <summary>【本地改动】双向挡那张表（加热棒 ↔ TEC 来回推整出来的）。</summary>
        public readonly PidGainSchedule BothSchedule = new();

        /// <summary>【本地改动】此刻那张表：查参数、学稳态偏置、登记自整定结果都走它。</summary>
        public PidGainSchedule ActiveSchedule => ScheduleOf(Table);

        public PidGainSchedule ScheduleOf(GainTable t) => t switch
        {
            GainTable.Heater => HeaterSchedule,
            GainTable.Both => BothSchedule,
            _ => GainSchedule
        };

        /// <summary>【本地改动】这一路单独关掉增益调度（固定用手动参数）。全局开关 GainSchedulingEnabled 照旧。</summary>
        public bool SchedulingOff;

        /// <summary>手动参数（增益表为空时使用），由 ConfigurePid 记录。</summary>
        public PidGains? ManualGains;

        // ---- 串级（Tr 模式） ----
        public ControlStrategy Strategy = ControlStrategy.Direct;

        /// <summary>外环 PID：输入内部温度误差，输出内环设定值的偏置（℃）。</summary>
        public readonly PidController OuterPid = new();

        /// <summary>外环反馈传感器（默认 TC1 内部）。</summary>
        public int OuterFeedbackSensor = 1;

        /// <summary>手动外环参数（增益表未登记外环时使用），由 ConfigureCascade 记录。</summary>
        public PidGains? ManualOuterGains;
        public double ManualOuterMaxBiasC = 15;

        /// <summary>最近一次记录到的稳态偏置（腔体设定值 − 内部设定值，℃）。</summary>
        public double? LastSteadyBias;

        /// <summary>外环连续处于稳态的更新次数，达到阈值才写回增益表。</summary>
        public int OuterSteadyCycles;

        /// <summary>外环偏置限幅自动扩程保护。</summary>
        public readonly OuterBiasRangeGuard BiasGuard = new();

        /// <summary>
        /// 外环"过零积分放电"的一次性武装标志（启动或改设定值时武装，首次过零后解除）。
        /// 趋近段外环积分沿途累积的量远超稳态所需（−10℃ 实测过零时 −3.0 vs 稳态 −0.35），
        /// 多出部分只能靠温度越过设定值反向积分放掉——这正是串级下冲的主要来源
        /// （实测下冲面积 334 ℃·s 与 ΔI/Ki=340 吻合）。误差首次过零时把积分直接预置到
        /// 该温度已学到的稳态偏置，多余电荷瞬间清零，下冲随之消失。
        /// </summary>
        public bool OuterCrossingArmed;

        /// <summary>
        /// 外环"趋近放电"的一次性武装标志（第一枪；过零放电是第二枪）。
        /// 只在过零时放电仍嫌太晚：僵尸积分在趋近最后阶段一直把腔体设定值压得过低，
        /// 腔体因此在核心到站时还欠着约 3℃ 才到它的稳态位置，核心只能继续下沉等它——
        /// 实测第三轮剩余的 0.18℃ 下冲全部来自这段腔体欠账（腔体 t=14.4 回到稳态位置，
        /// 核心谷底恰在 t=13.9）。误差缩进趋近带内、釜内真在往目标走时提前放电（到站稳住学到了还没打的作废），腔体提前两分钟归位。
        /// </summary>
        public bool OuterApproachDischargeArmed;

        /// <summary>上一拍外环误差（过零检测用）；NaN 表示尚无历史。</summary>
        public double LastOuterErrorC = double.NaN;

        /// <summary>【本地改动】外环误差历史：最近 <see cref="OuterApproachWindow"/> 里每次外环更新的 (时刻, 误差)，
        /// 「趋近不积分」量误差缩小得多快用。新一段趋近（改设定 / 起步 / 解除压低）和 Tr 丢失恢复时清。只在回路线程读写
        /// （会话线程那边只立 <see cref="OuterRearmedFlag"/>）。</summary>
        public readonly Queue<(DateTime At, double ErrC)> OuterErrHistory = new();

        // 【本地改动】会话线程（SetSetpoint / 解除压低 / 起步）立的标志，回路线程下一次外环更新开头 Interlocked.Exchange 收走再落实——
        // 会话线程碰回路线程正在用的队列会撞；先读后清会把两次之间又立的那一下吞掉。
        /// <summary>新一段趋近：清误差历史。</summary>
        public int OuterRearmedFlag;
        /// <summary>SetSetpoint 直接给了新目标：坡上攒的放掉、积分还在起步预置上时按新方向重摆刹车。</summary>
        public int OuterTargetChangedFlag;
        /// <summary>重新起步：丢掉记着的坡起点。</summary>
        public int RampCancelledFlag;

        /// <summary>【本地改动】外环积分里的偏置算不算摸清了：起步用了学到的 / 估出来的稳态偏置、放电预置到学到的，或者这一场已经稳住过一分钟
        /// （<see cref="OuterSteadyCyclesToLearn"/>）。</summary>
        public bool OuterBiasKnown;

        /// <summary>【本地改动】外环积分还停在起步预置（或重摆刹车后的值）上、一下都没积过。只有这时候积分里那份刹车是完整的、
        /// 改目标可以原样拿掉换方向；积过一下刹车就算被吸收了（<see cref="OuterBrakeC"/> 清 0），不再单算。</summary>
        public bool OuterIntegralPristine;

        /// <summary>【本地改动】积分还没动过时里面那份刹车（K，降温趋近 +、升温趋近 −、0 = 没加）。</summary>
        public double OuterBrakeC;

        // 【本地改动】大降温收尾（OuterFinishGain）：这一段趋近的状态。新一段趋近（收 OuterRearmedFlag）、不适用（曲线 / 压低 / 别的表）时清。只在回路线程读写
        /// <summary>这一段趋近起步时的釜内（走过多远从它算）。</summary>
        public double FinStartPv = double.NaN;
        /// <summary>内环连续顶在满制冷多少秒；够了 <see cref="FinSatOk"/> 锁存。</summary>
        public double FinSatSeconds;
        public bool FinSatOk;
        /// <summary>武装了：满制冷顶够了、走过够远、目标够低。这一段趋近里一直武装着。</summary>
        public bool FinArmed;
        /// <summary>到站（进 ±0.1 或过了零）：g 不再往上加。</summary>
        public bool FinArrived;
        public DateTime FinArrivedAt;
        /// <summary>稳住了（或到站后顶够了时长）：g 往下退、这一段不再加。</summary>
        public bool FinEnded;
        /// <summary>爬行程度 g ∈ [0, 1]：多给的比例 = OuterFinishGain × g × 外环 P。</summary>
        public double FinGain;
        public double FinSettledSeconds;
        public double FinLastErr = double.NaN;
        /// <summary>这一拍收尾规则多给的偏置（K）。</summary>
        public double FinExtraC;
        /// <summary>改目标时带过来、正在线性退的那份（K）和起点时刻；0 = 没有。新一段趋近不清它（改目标时就是它接手），
        /// 起步 / 解除压低 / 不适用时清。</summary>
        public double FinCarryC;
        public DateTime FinCarryAt;
        /// <summary>这一拍在外环 P / I / D 之外一共加了多少（收尾 + 带着退的），逐拍记录用。</summary>
        public double FinAppliedC;
        /// <summary>武装后起过一次「爬」（提示只发一次）。</summary>
        public bool FinCrawlNoted;
        /// <summary>最近 <see cref="OuterFinishShotWindow"/> 的 (时刻, 外环误差)：过零那一枪按过零速度封顶用。</summary>
        public readonly Queue<(DateTime At, double ErrC)> FinErrHistory = new();

        // ---- 【本地改动】升温加速（速度环 + 刹车曲线，见 HostControlLoop.HeatBoost） ----
        /// <summary>这一段趋近还能进速度环：起步 / 改目标时立；进了、交还 / 中止、釜内进了 <see cref="HostControlLoop.HeatBoostEnterC"/> 以内时落。</summary>
        public bool VelEligible;
        /// <summary>此刻夹套目标归速度环管（回路线程写，会话线程读状态）。</summary>
        public volatile bool VelMode;
        public DateTime VelSince;
        /// <summary>进速度环时这个目标学到的稳态偏置（夹套 − 釜内，K）。</summary>
        public double VelB;
        public DateTime? VelOkSince;
        public double VelVmCPerMin = double.NaN;
        public double VelVstarCPerMin = double.NaN;
        public double VelBrake = double.NaN;
        /// <summary>外环每拍记一笔釜内（不在速度环也记：中途接入时测速要有历史），留 <see cref="HostControlLoop.HeatBoostStallWindow"/> 多一点。</summary>
        public readonly Queue<(DateTime At, double TrC)> VelTrHistory = new();
        /// <summary>速度环里每拍的 (夹套目标 − 主设定 − 外环 Kp·误差, 夹套目标 − 釜内)，留 <see cref="HostControlLoop.HeatBoostHandAverage"/>：交还时积分从它们的平均接。</summary>
        public readonly Queue<(DateTime At, double PiBias, double OverTr)> VelBiasHistory = new();
        /// <summary>会话说 TEC 此刻接得上（冷却水已通 / 人选了不开 / 不用水）：速度环要靠 TEC 刹车，接不上不进、进了就退。</summary>
        public volatile bool HeatBoostAllowed;
        /// <summary>上一拍没进速度环的原因（同一个原因只说一次）。</summary>
        public string? VelBlockedSaid;
        /// <summary>别的条件都齐了、只差会话放行（TEC 接得上）：会话可以借它提前问冷却水。</summary>
        public volatile bool VelWantsTec;
        /// <summary>加速中 TEC 接不上 / 被扣着从什么时候起（宽限 HeatBoostDenyGrace）。</summary>
        public DateTime? VelDeniedSince;
        /// <summary>速度环上一拍的夹套目标（温差限之前）：中止时判「在不在刹车」用——被温差限压着的是在往上推、不是在刹。</summary>
        public double VelIspUncapped = double.NaN;

        // ---- 【本地改动】外环偏置自校正（见 HostControlLoop.OuterBiasCheckWindow） ----
        /// <summary>最近一个窗口的 (时刻, 釜内, 夹套)；改目标 / Tr 丢失恢复 / 曲线 / 加速 / 大降温收尾时清。压低（制冷段）期间照记。</summary>
        public readonly Queue<(DateTime At, double TrC, double TjC)> BiasWin = new();
        public DateTime LastBiasFix;
        /// <summary>窗口清过以后外环误差变过号没有（<see cref="OuterBiasCheckAfterCross"/>）：没过零之前不记——趋近尾巴上那段不算。</summary>
        public bool BiasCrossed;
        public double BiasLastErr = double.NaN;
        /// <summary>夹套经 <see cref="OuterBiasCheckWallHat"/> 一阶滞后的估计（釜壁温度）：开控起一直跟、不随窗口清。</summary>
        public double BiasWallHat = double.NaN;
        public DateTime BiasWallAt;
        /// <summary>自校正改完积分以后到这个时刻之前外环积分不动（<see cref="OuterBiasCheckHold"/>）。</summary>
        public DateTime BiasHoldUntil;
        /// <summary>这一场按实测定的稳态偏置（在 <see cref="BiasOverrideSp"/> 这个目标上；±<see cref="OuterBiasCheckCarryC"/> 内的别的目标按「实测 − 表」的差平移，
        /// 开控才清）：两枪放电、落地钳位、坡尾回基、加速都按它，不按表里那个。在它管着的目标上真稳住学到以后跟着学到的走（管不着的目标学到的只进表）。</summary>
        public double? BiasOverrideC;
        public double BiasOverrideSp = double.NaN;
        /// <summary>【本地改动】这一回两枪是解除压低重新武装的（不是改目标 / 开控）：自校正管着的目标上拿住期过了这种就不打（回路线程用）。</summary>
        public bool ShotsFromPushRelease;
        /// <summary>【本地改动】解除压低立的（会话线程），回路线程收走：这一回重新武装是解除压低。SetSetpoint 不碰它——那边前后两次立
        /// <see cref="OuterRearmedFlag"/>、回路线程几个标志分几下收，后一下可能落到下一拍，单看「重新武装了、没改目标」会把改目标认成解除压低。</summary>
        public int OuterRearmInPlaceFlag;
        /// <summary>【本地改动】解除压低以后外环积分不许往上攒，到这个时刻为止（<see cref="OuterBiasCheckHold"/> 封顶；每回解除压低都立——
        /// 自校正管着的目标上拿住期里松开的从拿住期满起算；自校正改一次不动它）：压低把釜内拽到目标下面那一截不是偏置不够，攒进去就是下一回冲过头的电荷。
        /// 釜内到过目标下面、又回到目标就放开；往下退照退。回路线程用。</summary>
        public DateTime ReleaseGuardUntil;
        public bool ReleaseGuardBelow;
        /// <summary>【本地改动】这个目标上（上次改以后）这一串冲过头被压低里第一次解除压低的时刻：整个窗口都在它后面的，
        /// 估出来比积分低过 <see cref="OuterBiasCheckRefixGapC"/> 就再改一次（往下）。上一次解除隔了两个窗口以上（不是一串里的了）挪到这一次——
        /// 不然早先那一次一直立着，后来一回扰动压低、一松开整个窗口就带着扰动和压低那一截，按小门槛错改。
        /// 改目标 / 开控 / 走曲线清；再改一次以后重新等。回路线程用。</summary>
        public DateTime RefixFrom;
        /// <summary>【本地改动】上一次解除压低的时刻（配 <see cref="RefixFrom"/> 判是不是一串里的）。回路线程用。</summary>
        public DateTime LastReleaseAt;
        /// <summary>【本地改动】自校正这一场第一次改的那一刻三张表的副本：附近目标的平移按它算（改完在原目标稳住学到的会把表里那行改成实测，
        /// 拿现在的表算差就没了）。开控清。</summary>
        public BiasTables? BiasCarryTables;
        /// <summary>【本地改动】这一场在哪些目标上实测过偏置（自校正改的那个、管着的目标上真稳住学到的）：回到这些目标直接按实测，
        /// 附近的目标从离得最近的那个实测点平移。自校正每改一次只剩改的那一个（之前的可能是釜变了以前的）；开控清。
        /// 回路线程整个换掉（不改元素），别的线程读到的总是完整的一份。</summary>
        public (double Sp, double Bias)[] BiasMeasured = [];

        /// <summary>【本地改动】外环积分的参照值——信得过的那个偏置：起步预置、重摆刹车、放电预置、坡尾回基、这一场稳住时的积分。
        /// 改目标 / 解除压低不动它。「趋近不积分」「夹套跟不上不积分」只拦积分往参照值外面、往外环正推的方向走；
        /// 往参照值那边回（退债）两条都不拦。</summary>
        public double OuterIntegralRef;

        /// <summary>【本地改动】曲线斜坡起步那一拍的外环积分；不在坡上为 null。坡停下来（走完 / 进保温段）或中途直接改目标时
        /// 积分回到它（或这个温度学到的稳态偏置），只往「停在目标前面」那一侧回。坡中途重下一条坡（重规划）不换起点。</summary>
        public double? RampBaseI;

        /// <summary>【本地改动】这段斜坡起步时的主设定值（坡停下来时定方向）。</summary>
        public double RampStartSpC;

        /// <summary>【本地改动】曲线是回路自己走完的（不是被 StopProfile 停的）：下一次外环更新按坡走完回基。只在回路线程读写。
        /// 被外面停掉的坡不在这儿回基——SetTargetAsync 是「停曲线 → 给新目标」两步，中间回路可能插一拍，按坡的方向放会放反；
        /// 留着坡起点等新目标那一拍按釜内在新目标哪一边放。</summary>
        public bool ProfileFinished;

        /// <summary>【本地改动】上一次外环更新时的主设定值（判断曲线还在不在走）；NaN = 还没更新过。</summary>
        public double LastOuterSetpointC = double.NaN;

        /// <summary>【本地改动】每拍（控没控都记）的 (时刻, 釜内 / 外环被控量, 夹套 / 内环被控量)，保留
        /// <see cref="OuterStartQuietWindow"/> 多一点。起步时没学到稳态偏置就看这段稳不稳、稳就拿「夹套 − 釜内」当外环积分起点。
        /// 回路线程写、StartChannelAsync（会话线程）读：锁它自己。</summary>
        public readonly Queue<(DateTime At, double OuterC, double InnerC)> History = new();

        /// <summary>落地偏置钳位已锁存（进交接带一次即锁，至首次过零由武装标志一并解除）。
        /// 必须锁存：按拍重判会在带边被 ±10mK 噪声来回触发，实测出现 4 次咬合-松开抖动。</summary>
        public bool OuterClampLatched;

        /// <summary>钳位方向：true=自上而下趋近（限深），锁存时刻记录。</summary>
        public bool ClampFromAbove;

        /// <summary>钳位地板的斜坡当前值（℃）。咬合瞬间从当时偏置起步、按限速抬向目标，
        /// 避免一步 5℃ 的设定值台阶让内环冲过头（实测腔体过冲 0.39℃、核心回勾 0.04℃）。</summary>
        public double ClampFloorSlewC;

        /// <summary>上一拍钳位是否实际约束了偏置（用于冻结外环积分）。</summary>
        public bool OuterClampBinding;

        /// <summary>
        /// 【本地改动】串级「夹套压低」（双工位主机制冷段，<see cref="SetOuterPushDown"/>）：压着的时候内环设定不按
        /// 外环 PID 的输出走，按影子偏置 = clamp(P + D − BelowC, ±限幅)（FromEquilibrium 再加上冻结的 I）；
        /// 外环积分冻结、两枪放电 / 落地钳位 / 扩程 / 稳态学习整段跳过。引用整体换（会话线程写、回路线程读）。
        /// </summary>
        public OuterPush? Push;


        /// <summary>外环算出的内环设定值（℃）。</summary>
        public double InnerSetpointC;

        public DateTime LastOuterUpdate;

        /// <summary>【本地改动】串级外环的外部被控量（釜内 Tr）丢失的起点；null = 没丢。</summary>
        public DateTime? OuterLostSince;

        /// <summary>【本地改动】曲线在 Tr 丢失保持期暂停的时刻；恢复时把 ProfileStart 往后挪这么久。</summary>
        public DateTime? ProfileHoldAt;

        // ---- 设定值生成层（时间驱动曲线） ----
        public ITemperatureProfile? Profile;
        public DateTime ProfileStart;
        public double ProfileStartC;

        /// <summary>当前稳态已持续的周期数，达到阈值才写入前馈模型，避免把暂态误记为稳态。</summary>
        public int SteadyCycles;
        public volatile bool Active;
        public double SetpointC;
        public bool InvertOutput;
        public double LastDutyPercent;
        public DateTime LastUpdateTime;

        /// <summary>执行器形态（手动换线过渡方案：一次运行内固定）。</summary>
        public ActuatorMode Actuator = ActuatorMode.Bidirectional;

        /// <summary>ConfigurePid 记录的输出上限幅度，切换执行器形态时按此重装限幅。</summary>
        public double MaxDutyMagnitude = 100;

        public RelayAutoTuner? Tuner;
        public DateTime TuneStartTime;

        /// <summary>【本地改动】整定器最近一拍算出的输出（PID 那一侧：正 = 加热、负 = 制冷），双向挡整定时会话按它的符号扳继电器。</summary>
        public double? LastTuneOutput;

        /// <summary>【本地改动】双向挡整定时 TEC 那半周乘的比值（TEC % ÷ 加热棒 %），整完登记成那一行的「加热比」。</summary>
        public double TuneCoolRatio = 1;

        /// <summary>反馈传感器：0 = 用本通道传感器（默认）；1/2 = 指定 TC1/TC2。
        /// 执行器与探头位置解耦（如 CONTMODE=2 单环控制时主环用 TC2 腔体探头做反馈）。</summary>
        public int FeedbackSensor;

        /// <summary>手动输出的物理原始占空比（已含反向）。实测 215L 固件：MODE=3 输出若不定期
        /// 刷新会被固件看门狗自动撤销（约 1 分钟），因此手动模式也需每周期重写。</summary>
        public double? ManualDutyPercent;

        /// <summary>【本地改动】串级外环的外部被控量来源（双工位主机：釜内 Tr 由宇电模块另采，
        /// 不在这台温控器的两路 TC 上）。null = 沿用 OuterFeedbackSensor 指的那路 TC。</summary>
        public Func<double>? OuterSource;

        /// <summary>【本地改动】输出挂起：热源切换序列「关输出 → 扳继电器 → 开输出」那两秒里，
        /// 回路不算也不写（算了也执行不了，积分只会攒债）；恢复时 dt 从头计。</summary>
        public volatile bool Suspended;

        /// <summary>【本地改动】从什么时候起挂着（整定中挂太久就判失败，别让整定器空等到超时）。</summary>
        public DateTime? SuspendedSince;

        /// <summary>【本地改动】只加热执行器（电加热棒）的占空比符号。固件把哪个符号路由到
        /// 加热棒那只 SSR 的引脚，由现场接线决定——这台机器实测是负的（温控器自己的 PID
        /// 升温时读回 −90 %）。</summary>
        public int HeaterSign = 1;

        public bool Busy => Active || Tuner is not null;

        /// <summary>【本地改动】最近一次写占空比时温控器回显的值（设备实际存下的）。</summary>
        public double AppliedDutyPercent = double.NaN;
        /// <summary>【本地改动】回显不符已经说过了（说过就不每拍刷；差值变了 / 隔够久再说一次）。</summary>
        public bool EchoMismatchSaid;
        public double EchoOffsetSaid;
        public long EchoSaidAtTick;
        /// <summary>【本地改动】上一拍写的占空比（温控器回显慢一拍：回显等于上一拍写的也算对上）；不符 / 对上从什么时候起持续。</summary>
        public double EchoPrevWritten = double.NaN;
        public long EchoOffSinceTick;
        public long EchoOkSinceTick;
    }

    private readonly ChannelState[] _channels = [new(), new()];
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private int _consecutiveFailures;
    private int _cycleCount;

    /// <summary>控制周期。串口一个周期约需 3~4 条指令，不建议低于 200ms。</summary>
    public TimeSpan Period { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>连续通信失败达到该次数后触发安全停机。</summary>
    public int MaxConsecutiveFailures { get; set; } = 5;

    /// <summary>自整定最长允许时间，超时自动终止并归零输出。</summary>
    public TimeSpan AutoTuneTimeout { get; set; } = TimeSpan.FromMinutes(60);

    /// <summary>四片 TEC 的同步方式，默认上位机镜像（要求设备 CONTMODE=0）。</summary>
    public ChannelSyncMode SyncMode { get; set; } = ChannelSyncMode.HostMirror;

    /// <summary>TEC 双向执行器的 PWM 频率档（FPWM）：默认 2（10Hz，设备出厂值）。
    /// TEC 制冷量正比 I、焦耳热正比 I²，脉动越小效率越高，深冷可试 3（100Hz）。</summary>
    public int TecPwmFrequencyLevel { get; set; } = 2;

    /// <summary>只加热执行器（过零 SSR + 加热棒）的 PWM 频率档：默认 0（0.5Hz 时间比例调功）。</summary>
    public int HeaterPwmFrequencyLevel { get; set; } = 0;

    /// <summary>主通道(1)的占空比写入按 SyncMode 决定是否镜像到通道 2。</summary>
    private async Task WriteDutyAsync(int ch, double duty, CancellationToken ct)
    {
        var echo = await controller.SetDutyPercentAsync(ch, duty, ct).ConfigureAwait(false);
        NoteDutyEcho(ch, duty, echo);
        if (ch == 1 && SyncMode == ChannelSyncMode.HostMirror)
            await controller.SetDutyPercentAsync(2, duty, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 【本地改动】温控器回显的占空比跟写进去的不一样：记下来、说一声，**不当通信失败**。
    /// 现场：夹套到温后加热棒只要 4 %，写 −4.41 % 温控器回显 −6.43 %（差 2.02 %），从前这算坏帧 → 重发 → 仍不符 → 抛，
    /// 连着 20 拍就「连续通信失败」停控——温控器明明在正常工作。差值是设备的规矩（启动电压 / 最小输出），
    /// PID 的积分会把稳定的偏移吃掉；说清楚差多少，让人去参数窗核。
    /// </summary>
    /// <summary>
    /// 【本地改动】（0335）回显不符要持续这么久才说（对上了也要持续这么久才说「又对上了」）。现场逐拍记录：温控器回显的多半是
    /// **上一拍**写的值（applied(t) = written(t−1) 占 89 %），起步还有每拍 15 % 的斜率限制（0 → −15 → −30 … 3 s 到满）；
    /// 保温段写入值每拍在 ±2 % 里抖，回显永远慢一拍，按拍比就「对不上 / 又对上了」来回报——40 min 刷了 962 条。
    /// </summary>
    public int EchoDwellMs { get; set; } = 5000;

    private void NoteDutyEcho(int ch, double written, double echo)
    {
        var s = State(ch);
        s.AppliedDutyPercent = echo;
        var prev = s.EchoPrevWritten;
        s.EchoPrevWritten = written;
        var off = echo - written;
        var now = Environment.TickCount64;
        // 对上：等于这一拍写的，或等于上一拍写的（回显慢一拍）。PWMDUTY 分辨率 0.00005 %：0.02 以内算相等
        if (Math.Abs(off) <= 0.02 || (!double.IsNaN(prev) && Math.Abs(echo - prev) <= 0.02))
        {
            s.EchoOffSinceTick = 0;
            if (!s.EchoMismatchSaid) return;
            if (s.EchoOkSinceTick == 0) s.EchoOkSinceTick = now;
            if (now - s.EchoOkSinceTick < EchoDwellMs) return;
            s.EchoMismatchSaid = false;
            s.EchoOkSinceTick = 0;
            ChannelNotice?.Invoke(ch, "info", $"通道{ch}占空比回显又对上了（写 {written:F2} % 回显 {echo:F2} %）");
            return;
        }
        s.EchoOkSinceTick = 0;
        // 不符要持续够久才算「温控器按自己的规矩改了写入值」：起步的斜率限制、换挡瞬间那几拍都不算
        if (s.EchoOffSinceTick == 0) s.EchoOffSinceTick = now;
        if (now - s.EchoOffSinceTick < EchoDwellMs) return;
        // 说过就不再说，除非差值变了 0.5 % 以上**而且**离上次说过去了一分钟（有最小输出的温控器差值每拍都在变）
        if (s.EchoMismatchSaid && (Math.Abs(off - s.EchoOffsetSaid) < 0.5 || now - s.EchoSaidAtTick < 60_000)) return;
        s.EchoMismatchSaid = true;
        s.EchoOffsetSaid = off;
        s.EchoSaidAtTick = now;
        ChannelNotice?.Invoke(ch, "warn",
            $"通道{ch}写占空比 {written:F2} %，温控器回显 {echo:F2} %（差 {off:+0.00;-0.00} %）：按回显的算，不当通信失败。" +
            "差值一直在 2 % 左右多半是参数窗「正向 / 反向启动电压」（不需要就设 0）；只在小占空比时出现是温控器有最小输出，这一段控不细");
    }

    /// <summary>【本地改动】按路触发的安全停机（哪一路、为什么）。SafetyTripped 照旧整体发一份。</summary>
    public event Action<int, string>? ChannelTripped;

    /// <summary>【本地改动】按路的提示（路号、级别 "warn" / "info"、正文）：串级外环丢了釜内 Tr 进入保持、Tr 回来接着算。不是停控。</summary>
    public event Action<int, string, string>? ChannelNotice;

    /// <summary>【本地改动】要不要由回路改写设备全局的 FPWM。单釜机器按执行器形态改（加热棒 0.5 Hz、
    /// TEC 10 Hz）；双工位机器两路可能一路在加热棒、一路在 TEC，FPWM 只有一个，改了必顾此失彼——关掉，
    /// FPWM 留给参数窗的人定。</summary>
    public bool ManagePwmFrequency { get; set; } = true;

    /// <summary>【本地改动】给某路的串级外环换一个外部被控量来源（釜内 Tr）；null 回到本机 TC。</summary>
    public void SetOuterSource(int ch, Func<double>? source) => State(ch).OuterSource = source;

    /// <summary>【本地改动】挂起 / 恢复某路的输出（热源切换那两秒）。挂起期间回路不算不写。</summary>
    public void SuspendOutput(int ch, bool suspend)
    {
        var s = State(ch);
        if (s.Suspended == suspend) return;
        s.Suspended = suspend;
        s.SuspendedSince = suspend ? DateTime.Now : null;
        if (!suspend)
        {
            s.LastUpdateTime = default;      // dt 从恢复那一拍重新计，别把挂起的几秒算进积分
            s.Pid.ResetDerivative();         // 挂起那两秒温度走了一截，别拿它算斜率踢输出
        }
    }

    public bool IsSuspended(int ch) => State(ch).Suspended;

    /// <summary>【本地改动】只加热执行器的占空比符号（+1 / −1），见 ChannelState.HeaterSign。</summary>
    public void SetHeaterSign(int ch, int sign) => State(ch).HeaterSign = sign < 0 ? -1 : 1;

    /// <summary>
    /// 【本地改动】（0336）加热棒 + TEC 双向：PID 侧的量翻成写到设备上的数。正半轴是加热棒（符号按接线 HeaterSign），
    /// 负半轴是 TEC 制冷（按「TEC 输出反向」）。现场那台两边都翻成相反的符号（升温 −、降温 +），正好是固件按符号路由的两个引脚。
    /// </summary>
    private static double ToDevice(ChannelState s, double pidSide)
        => pidSide >= 0 ? s.HeaterSign * pidSide : s.InvertOutput ? -pidSide : pidSide;

    /// <summary>每个控制周期结束后发布（后台线程回调）。</summary>
    public event Action<ControlCycleResult>? CycleCompleted;

    /// <summary>单个周期内发生通信异常时回调（不会终止循环）。</summary>
    public event Action<Exception>? CycleFaulted;

    /// <summary>触发安全停机（传感器丢失或连续通信失败）时回调，参数为原因说明。</summary>
    public event Action<string>? SafetyTripped;

    /// <summary>输出长期饱和但温度未跑飞（多为设定值超出制冷/加热能力）时的一次性警告。</summary>
    public event Action<string>? SaturationWarning;

    /// <summary>输出持续饱和多久后发出能力不足警告（不停机）。</summary>
    public TimeSpan SaturationWarnAfter { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>自整定结束（成功或失败）时回调（后台线程）。</summary>
    public event Action<AutoTuneOutcome>? AutoTuneFinished;

    /// <summary>
    /// 增益表内容发生实质变化时回调（后台线程），参数为通道号。
    /// 触发时机：自整定登记新工作点、运行中学到稳态偏置、偏置限幅被自动放宽。
    /// 供界面立即落盘，避免程序异常退出时丢掉学习成果。
    /// </summary>
    public event Action<int>? GainScheduleChanged;

    public bool IsRunning => _loopTask is { IsCompleted: false };

    private ChannelState State(int ch) => _channels[ch - 1];

    // ---------- 参数配置（可在运行中调用） ----------

    public void ConfigurePid(int ch, double kp, double ki, double kd,
        double maxDutyPercent, bool invertOutput, double derivativeFilterSeconds = 2.0)
    {
        var magnitude = Math.Clamp(Math.Abs(maxDutyPercent), 1, 100);
        var s = State(ch);
        s.MaxDutyMagnitude = magnitude;
        s.Pid.Configure(kp, ki, kd, OutputFloor(s), OutputCeiling(s), derivativeFilterSeconds);
        s.InvertOutput = invertOutput;
        s.ManualGains = new PidGains(kp, ki, kd);
    }

    /// <summary>【本地改动】只换手动增益，不动输出上限与方向（参数面板改 PID 时用）。</summary>
    public void SetManualGains(int ch, double kp, double ki, double kd)
    {
        var s = State(ch);
        s.Pid.SetGains(kp, ki, kd);
        s.ManualGains = new PidGains(kp, ki, kd);
    }

    /// <summary>该通道 PID 的输出下限：只加热执行器为 0（负半轴不存在），双向为 −上限。</summary>
    private static double OutputFloor(ChannelState s) =>
        s.Actuator == ActuatorMode.HeatOnly ? 0 : -s.MaxDutyMagnitude;

    /// <summary>【本地改动】输出上限：只制冷执行器为 0（正半轴不存在），其余为 +上限。</summary>
    private static double OutputCeiling(ChannelState s) =>
        s.Actuator == ActuatorMode.CoolOnly ? 0 : s.MaxDutyMagnitude;

    /// <summary>【本地改动】只换输出上限（参数窗改了温控器的 LIMITED 时跟上），增益、方向不动。</summary>
    public void SetMaxDuty(int ch, double maxDutyPercent)
    {
        var s = State(ch);
        s.MaxDutyMagnitude = Math.Clamp(Math.Abs(maxDutyPercent), 1, 100);
        s.Pid.SetOutputLimits(OutputFloor(s), OutputCeiling(s));
    }

    /// <summary>
    /// 【本地改动】形态默认查哪张表：只加热 = 加热棒表，加热棒 + TEC 双向 = 双向挡表，TEC 双向 / 只制冷 = TEC 表。
    /// 双向挡里 TEC 暂时接不上时会话明说「只加热、双向挡表」（SetActuatorMode 第三个参数）。
    /// 同一张表之内换形态只是改限幅（不换对象、不复位）；跨表才是换对象（积分清零、按前馈预置）。
    /// </summary>
    public static GainTable DefaultTable(ActuatorMode m) => m switch
    {
        ActuatorMode.HeatOnly => GainTable.Heater,
        ActuatorMode.HeaterTec => GainTable.Both,
        _ => GainTable.Tec
    };

    /// <summary>
    /// 设置执行器形态（手动换线过渡方案）。运行/整定中禁止切换——形态必须与当时的
    /// 实际接线一致，只能停下来、改完线再切。切到只加热时内环输出下限抬到 0：
    /// 负输出不存在（固件会把负占空比路由到制冷 PWM 引脚，而 TEC 线已断开），
    /// PID 若被允许算出负值只会白积负积分。
    /// </summary>
    public void SetActuatorMode(int ch, ActuatorMode mode, GainTable? table = null)
    {
        var s = State(ch);
        var t = table ?? DefaultTable(mode);
        if (t == GainTable.Both && mode is not (ActuatorMode.HeaterTec or ActuatorMode.HeatOnly))
            throw new InvalidOperationException($"通道{ch}：双向挡那张表只配「加热棒 + TEC 双向」或（TEC 暂时接不上时的）只加热，不配{mode}");
        // 【本地改动】双工位主机的继电器由程序自己扳：挂起输出 → 扳继电器 → 换形态 → 恢复，
        // 这时候通道是「运行中但挂起」，允许切；没挂起的运行中照旧不许（形态必须跟接线一致）
        // 【本地改动】同一张表之内换形态（TEC 双向 ⇄ 只制冷：「TEC 加热」开关翻了；双向挡里只加热 ⇄ 加热棒 + TEC：制冷段挂起 / 放开）
        // 只是改限幅，不换对象，运行中也许改
        if (s.Busy && !s.Suspended && s.Table != t)
            throw new InvalidOperationException($"通道{ch}正在运行，请先停止再切换执行器形态（并确认接线已相应改好）");
        if (s.Tuner is not null && s.Table != t)
            throw new InvalidOperationException($"通道{ch}正在自整定（{s.Table} 那张表）——整定中不能换表，结果会登记错地方");
        var swapped = s.Table != t;      // 换了表（换了对象）才复位
        s.Actuator = mode;
        s.Table = t;
        s.Pid.SetOutputLimits(OutputFloor(s), OutputCeiling(s));
        if (swapped && s.Active)
        {
            // 换了执行器就是换了对象（加热棒和 TEC 的增益差一个量级）：积分清零、按前馈预置，
            // 跑飞检测重新给宽限期——不然刚换上的那一拍还背着上一个执行器攒的积分
            s.Pid.Reset();
            if (PresetFor(s, s.InnerSetpointC) is { } preset) s.Pid.PresetIntegral(preset);
            s.Runaway.Start(DateTime.Now);
            s.SaturationWarned = false;
            s.SteadyCycles = 0;
        }
    }

    public ActuatorMode GetActuatorMode(int ch) => State(ch).Actuator;

    /// <summary>【本地改动】此刻查的是哪张增益表。</summary>
    public GainTable GetTable(int ch) => State(ch).Table;

    /// <summary>该通道的增益调度表（自整定成功后自动登记工作点）。</summary>
    public PidGainSchedule GetGainSchedule(int ch) => State(ch).GainSchedule;

    /// <summary>启用增益调度：表内有点时按设定值插值覆盖手动参数。</summary>
    public bool GainSchedulingEnabled { get; set; } = true;

    /// <summary>【本地改动】某路某形态默认查的那张增益表（见 DefaultTable）。</summary>
    public PidGainSchedule GetGainSchedule(int ch, ActuatorMode mode) => State(ch).ScheduleOf(DefaultTable(mode));

    /// <summary>【本地改动】某路的某张增益表。</summary>
    public PidGainSchedule GetGainSchedule(int ch, GainTable table) => State(ch).ScheduleOf(table);

    /// <summary>【本地改动】按路开关增益调度（两个工位各有各的表，开关也各管各的）。</summary>
    public void SetGainScheduling(int ch, bool on) => State(ch).SchedulingOff = !on;

    /// <summary>【本地改动】这一路此刻按不按增益表走（全局开关与按路开关都开着才算）。</summary>
    public bool IsGainScheduling(int ch) => Scheduling(State(ch));

    private bool Scheduling(ChannelState s) => GainSchedulingEnabled && !s.SchedulingOff;

    /// <summary>【本地改动】手动内环参数（ConfigurePid / SetManualGains 记下的那组）；没配过是 null。</summary>
    public PidGains? GetManualGains(int ch) => State(ch).ManualGains;

    /// <summary>【本地改动】手动外环参数与偏置限幅（ConfigureCascade 记下的那组）。</summary>
    public (PidGains? Gains, double MaxBiasC) GetManualOuter(int ch)
        => (State(ch).ManualOuterGains, State(ch).ManualOuterMaxBiasC);

    /// <summary>【本地改动】这一路正在自整定。</summary>
    public bool IsTuning(int ch) => State(ch).Tuner is not null;

    /// <summary>
    /// 加热相对制冷的有效度比值。实测本机加热膜约为 TEC 制冷的 2 倍，
    /// 用输出归一化（正输出除以该比值）代替"两套增益硬切换"，零点连续、无抖动。
    /// 这是全局兜底值：增益表某行填了"加热比"列时，按温度调度的值优先。
    /// 仅作用于 TEC 双向执行器；只加热模式（电加热棒）恒按 1 处理——
    /// 单向执行器无对侧可配平，整定结果已直接反映加热棒增益。
    /// </summary>
    public double HeatingEffectivenessRatio { get; set; } = 1.0;

    public void SetSetpoint(int ch, double celsius)
    {
        var s = State(ch);
        var changed = Math.Abs(celsius - s.SetpointC) > 1e-9;
        if (changed)
        {
            // 新目标意味着新一段趋近过程：重新武装两段积分放电；压着的夹套目标也随旧目标作废（【本地改动】）
            s.Push = null;
            RearmOuter(s);
        }
        s.SetpointC = celsius;
        if (changed)
        {
            // 【本地改动】标志在新设定值写进去之后立：回路线程收标志时看到的一定是新目标
            Volatile.Write(ref s.OuterTargetChangedFlag, 1);
            Volatile.Write(ref s.OuterRearmedFlag, 1);
        }
    }

    /// <summary>新一段趋近：重新武装两枪积分放电、解开落地钳位、清过零历史（SetSetpoint 与解除压低共用）。
    /// 【本地改动】也让回路线程下一次外环更新清趋近判据的误差历史（跨新旧目标算误差变化没意义）、积分参照落到此刻的积分（立标志）。</summary>
    private static void RearmOuter(ChannelState s)
    {
        s.OuterApproachDischargeArmed = true;
        s.OuterCrossingArmed = true;
        s.OuterClampLatched = false;
        s.OuterClampBinding = false;
        s.LastOuterErrorC = double.NaN;
        Volatile.Write(ref s.OuterRearmedFlag, 1);
    }

    /// <summary>【本地改动】串级「夹套压低」的参数：压到基准以下多少 K；基准是釜内（false）还是稳态偏置（true）。</summary>
    public sealed record OuterPush(double BelowC, bool FromEquilibrium);

    /// <summary>
    /// 【本地改动】串级外环「夹套压低」（双工位主机双向挡的制冷段：被控量冲过目标、提前切 TEC 时把夹套目标压到基准以下，
    /// 不等外环慢慢把 +5 K 的保温偏置退掉）。压着的时候：内环设定 = 主设定 + clamp(外环 P + D − belowC, ±偏置限幅)
    /// （fromEquilibrium = true 再加上冻结的 I，即压到稳态偏置之下 belowC）；外环积分冻结在压之前的值（不写假值），
    /// 趋近 / 过零放电、落地钳位、限幅扩程、稳态偏置学习整段跳过（不然制冷段里 Tr 在 ±0.05 K 带内停上一分钟就把 −2 当稳态写进表）。
    /// belowC = null 解除：从冻结的积分接着算，重新武装两枪放电（表里有稳态偏置的自动校回去）。
    /// 只对串级、激活、没在自整定的通道生效；返回接没接受。偏置限幅不放宽（外环增益调度每次更新都重夹），超了只能压到限幅、warn 一次。
    /// </summary>
    public bool SetOuterPushDown(int ch, double? belowC, bool fromEquilibrium = false)
    {
        var s = State(ch);
        if (belowC is null)
        {
            if (s.Push is null) return false;
            // 【本地改动】解除标志先立、再清 Push（都带屏障）：回路线程一拍开头看到没压着，紧跟着收标志就一定收得到这次解除；
            // 也先于重新武装那个标志立：回路线程收到重新武装时一定也看得到它
            Volatile.Write(ref s.OuterRearmInPlaceFlag, 1);
            Volatile.Write(ref s.Push, null);
            RearmOuter(s);
            ChannelNotice?.Invoke(ch, "info", $"通道{ch}夹套压低解除：外环从积分 {s.OuterPid.LastI:F2} ℃ 接着算，趋近 / 过零放电重新武装");
            return true;
        }
        if (!s.Active || s.Strategy != ControlStrategy.Cascade || s.Tuner is not null) return false;
        var below = Math.Abs(belowC.Value);
        var limit = s.OuterPid.OutputMax;
        if (!fromEquilibrium && below > limit)
            ChannelNotice?.Invoke(ch, "warn", $"通道{ch}夹套压低 {below:F1} K 超过外环偏置限幅 ±{limit:F1} ℃，只能压到限幅");
        s.Push = new OuterPush(below, fromEquilibrium);
        s.OuterSteadyCycles = 0;
        s.BiasGuard.Reset();
        ChannelNotice?.Invoke(ch, "info", $"通道{ch}夹套压低 {below:F1} K（基准 {(fromEquilibrium ? "稳态偏置" : "釜内")}）：" +
            $"外环积分冻结在 {s.OuterPid.LastI:F2} ℃，夹套设定 {s.InnerSetpointC:F2} ℃ → 下一拍按压低算");
        return true;
    }

    /// <summary>【本地改动】此刻压着多少 K（null = 没压）。</summary>
    public double? GetOuterPushDown(int ch) => State(ch).Push?.BelowC;

    /// <summary>
    /// 趋近放电带宽（℃）：外环误差在该值以内、且釜内真在往目标走（ApproachingSetpoint，同「趋近不积分」的判据）时执行第一段积分放电；
    /// 到站稳住学到稳态偏置时还没打的作废。
    /// 从这里起比例项单独已足够把温度推到设定值，积分再累积只会变成过冲电荷。
    /// </summary>
    public double OuterDischargeApproachBandC { get; set; } = 1.0;

    /// <summary>
    /// 放电触发的最小积分差距（℃）：积分与表中稳态偏置相差超过该值才执行预置。
    /// 第四轮实测第一枪后 4 分钟收尾又累积了 0.6，原门槛 0.5 边缘情况会漏放——
    /// 门槛调低，多放一次只是把积分挪到表值，无副作用。
    /// </summary>
    public double OuterDischargeMinGapC { get; set; } = 0.25;

    /// <summary>
    /// 稳态偏置学习的误差带（℃）。必须显著小于 SteadyBandC：第四轮实测系统在
    /// ±0.2℃ 内缓沉时（还背着 −0.6 暂态积分）学习器就开录，把暂态偏置 −1.05 当
    /// 稳态写进表，导致第二枪比对失准而跳过。收到 ±0.05℃ 后，学习自然推迟到
    /// 真稳之后，学到的才是真稳态值。
    /// </summary>
    public double OuterLearnBandC { get; set; } = 0.05;

    /// <summary>
    /// 【本地改动】学稳态偏置还要釜内真不动：<see cref="OuterApproachWindow"/> 里外环误差最高最低差不超过它（K），这一拍才算稳。≤0 或 NaN = 不查。
    /// 现场（2026-11-03）：釜内每分钟 0.05 K 从 15.05 穿到 14.95，在 ±0.05 带里待满了一分钟，就把当时还在漂的 +0.7 当成 15 ℃ 的稳态偏置记进了表
    /// （那一场维持 15 ℃ 实际要 +1.3 到 +1.9）——下次起步预置、两枪放电都拿它当准。
    /// </summary>
    public double OuterLearnStillC { get; set; } = 0.02;

    /// <summary>
    /// 【本地改动】外环偏置自校正：表里学到的稳态偏置错得多时，外环自己在目标附近按实测改过来。0 = 关。
    /// 现场（2026-11-04 R1-tc1-20261104-165419，双向挡釜内 32.8 → 40 ℃）：表里 40 ℃ 的偏置 5.53 K（多半是从 50 ℃ 那行沿用来的），
    /// 这台釜在 40 ℃ 只要 3.3 K（拟合两种釜模型 3.2–3.3，现场 10 分钟平均「夹套 − 釜内」3.0–3.4）。夹套在目标附近被多给 2 K，
    /// 釜内一直往上爬，过 40.1 就提前切 TEC、夹套压到釜内 − 2；松开以后两枪放电又把积分打回 5.53——外环积分整场 5.45–5.54，
    /// 每 5 分多钟冲一次 40.2–40.3，一直没稳。光改提前切换的参数（δ、压低基准）孪生里都还摆，关掉提前切冲 0.62 K。
    /// 自校正：釜内过一次目标（外环误差变号，<see cref="OuterBiasCheckAfterCross"/>）以后，每个外环拍记一笔 (釜内, 釜壁估计)——
    /// 釜壁估计是夹套经 <see cref="OuterBiasCheckWallHat"/> 的一阶滞后；攒满 <see cref="OuterBiasCheckWindow"/>（压低期间照记，那时夹套真在那儿）。
    /// 这段里釜内一直在目标 ±<see cref="OuterBiasCheckBandC"/> 里、平均离目标不到 <see cref="OuterBiasCheckMeanC"/>，
    /// 就用热平衡估维持目标要的偏置：b̂ = 平均(釜壁 − 釜内) − <see cref="OuterBiasCheckTauHat"/> × 釜内这段的平均升温速度；
    /// 跟外环积分差过 <see cref="OuterBiasCheckGapC"/> 就把积分放到 b̂、拿住 <see cref="OuterBiasCheckHold"/>，这一场这个目标的「学到的偏置」
    /// 也换成它（两枪放电、落地钳位、坡尾回基、升温加速都按它；这一场每个实测点 ±<see cref="OuterBiasCheckCarryC"/> 内都管：回到实测过的目标按实测、
    /// 夹在两个实测点中间线性插、只在一边的从最近那个按「实测 − 表」平移（两头同一张表，表按第一次改那一刻的副本，
    /// 平移完不出「实测 ~ 现在表里」那一段、只往物理上对的那边留余量），拿住也接着拿；
    /// 在管着的目标上真稳住学到以后跟着学到的走、记成实测点、学进表；管不着的目标学到的只进表）。解除压低重新武装的两枪
    /// 在管着的目标上拿住期过了不再往它上面打（估值差零点几 K 时两枪会把积分一直拽回去）、改成釜内被压到目标下面那段积分不往上攒
    /// （不然一攒一压越摆越大；拿住期里松开的从拿住期满起算）；改目标武装的照打（松压低和改目标同一拍到的算改目标）。
    /// 冲过目标被压低过一回（改没改过都算）：解除以后整个窗口估出来比积分低过 <see cref="OuterBiasCheckRefixGapC"/> 就再改一次（只往下）——
    /// 估高 0.3–0.7 K、表偏高 0.5 K 这种不到 1.0 K 的，原来一压一冲摆一个多小时、或者一直摆。两次之间至少隔一个窗口。
    /// 曲线 / 升温加速 / 大降温收尾期间不记；改目标 / Tr 丢失恢复也从头等过零。
    /// 门槛为什么这么定（Python 孪生：40 ℃ 那场 850 种釜 / 表 / 提前切换组合 + 20 ℃ 升温、降温收尾、扰动、噪声 ~230 种）：
    /// 不等过零、直接用夹套平均时，趋近尾巴和釜壁存热让 b̂ 偏低 0.5–1.9 K，表本来就对的也会改错、拖慢到站；
    /// 现在这样表对的组合里 b̂ 跟积分最多差 0.35 K，差 1.0 才动，留 0.65 K 余量；表错 2 K（这一场）差 2.3 K 照样改。
    /// C# 孪生（那场拟合的釜 + 真回路）：表 5.53 时 31 分钟改一次（b̂ 3.24、真值 3.32），之后不再压低，41 分钟起釜内 ±0.05；表对时一次都不触发。
    /// 管不了的：第一次冲过头（过零之前）照样冲；表偏低、釜内从下面慢慢爬上来不过零的，不改（跟原来一样慢）；
    /// 窗口里釜正好变了（扰动 10 分钟上下）估出来是两段的平均，按小门槛改了的话扰动撤了以后往下塌一点、慢慢回来（孪生 12 分钟扰动：多 5 分钟到稳）。
    /// </summary>
    public TimeSpan OuterBiasCheckWindow { get; set; } = TimeSpan.FromSeconds(600);

    public double OuterBiasCheckBandC { get; set; } = 0.5;
    public double OuterBiasCheckMeanC { get; set; } = 0.15;
    public double OuterBiasCheckGapC { get; set; } = 1.0;

    /// <summary>【本地改动】偏置自校正改过以后，管着的目标上又冲过目标被压低过一回（估高了）：解除压低以后整个窗口里估出来的
    /// 比积分低过这么多（K）就再改一次，只往下。不然估高 0.3–0.7 K 时差得不到 <see cref="OuterBiasCheckGapC"/>，积分只在每回冲过目标那几分钟往下退一点，
    /// 一压一冲摆一个多小时（孪生：估高 0.6 K 压低 6 回、90 分钟才稳；这样压一回、改一次就稳）。窗口整个在解除以后，压低以前的扰动不算进去。0 = 不这样改。</summary>
    public double OuterBiasCheckRefixGapC { get; set; } = 0.2;

    /// <summary>【本地改动】偏置自校正的热平衡：釜内每升 1 K/s 夹套要比釜内多高（s，约等于釜的时间常数，现场拟合 930–1030 s，取小一点）。</summary>
    public double OuterBiasCheckTauHat { get; set; } = 800;

    /// <summary>【本地改动】偏置自校正只在釜内过一次目标以后才开始攒（窗口清过以后重新等）：趋近尾巴上釜内还在减速，估出来偏低
    /// （孪生里表对的时候也会改错、拖慢到站）。只管第一次——会话提前切换的压低 / 松开不算清。</summary>
    public bool OuterBiasCheckAfterCross { get; set; } = true;

    /// <summary>【本地改动】偏置自校正的热平衡里用「釜壁」不用夹套：夹套经这个一阶滞后（s）当釜壁温度（现场两种釜模型拟合壁滞后 105–134 s）。
    /// 夹套比釜壁先走，窗口两头夹套差得多时直接用夹套平均会偏（那场升温窗口里偏低 0.6–1.2 K）。0 = 直接用夹套。</summary>
    public double OuterBiasCheckWallHat { get; set; } = 120;

    /// <summary>【本地改动】偏置自校正改完以后外环积分拿住多久（釜内跑出 ±<see cref="OuterBiasCheckBandC"/> 就放开）：改的那一下釜壁还热着 / 冷着
    /// （刚压低过），釜内还要往下 / 往上走一段，积分在这段过渡里照常攒就又攒偏了，回头冲过目标 + 0.1 又被压低、一直摆。
    /// 拿住期间靠比例收，偏置差 0.25 K 才差 0.1 K。0 = 不拿。</summary>
    public TimeSpan OuterBiasCheckHold { get; set; } = TimeSpan.FromSeconds(600);

    /// <summary>【本地改动】偏置自校正改过以后，这一场改到离那个目标多近（K）以内的新目标，也按实测跟表的差平移表里的值（两枪放电 / 坡尾回基 / 加速都按它）。
    /// 不平移的话改完马上改个 0.3 K，表里那个错的偏置又回来——孪生里 40 改完改 40.3 又冲过 0.43、又压低、又等十几分钟再改一次。0 = 只管原目标。</summary>
    public double OuterBiasCheckCarryC { get; set; } = 2.0;

    /// <summary>
    /// 腔体跟踪缺口前馈增益（0 关闭）。剩余到温下冲的物理来源：外环偏置收拢时腔体
    /// 滞后跟随，核心到站时腔体还欠位 ~0.7℃，存量冷量把核心多压 0.05~0.08℃。
    /// 补偿项 = 增益 × (腔体目标 − 腔体实测)，等效把腔体跟随加速 (1+k) 倍，
    /// 腔体与核心同时到站。
    ///
    /// 为什么用跟踪缺口而不用温度斜率：斜率方案实测翻车——稳态 ±15mK 慢漂经
    /// 300 倍增益放大成 ±0.3℃ 的腔体设定值抖动（内环跟踪峰峰 0.30→2.02℃）；
    /// 而缺口信号趋近段高达 0.7℃、稳态只有跟踪噪声 ±0.06，信噪比高一个量级，
    /// 且稳态时缺口→0 自然退场、不引入稳态偏移（(1+k)(r−Tw)=0 ⇒ Tw=r）。
    /// 三点实测回放校准：k=1.5 预测下冲 −10/−20/−30 = 0.045/0.026/0.028（含
    /// 模型 1.2 乐观折扣），带噪仿真稳态峰峰不增反减。
    ///
    /// 【实机否决，默认关闭（2026-07-31）】k=1.5 实测在 −10℃ 触发周期 ~300s 的
    /// 等幅振荡（稳态峰峰 96→317mK，内环设定值摆 1.47℃）。根因：本前馈在腔体
    /// 回路外再包一圈正反馈，而真实腔体响应含 ~50s 纯滞后（热量穿壁到探头），
    /// 纯滞后频率处相位达 180°、k=1.5 环增益 >1 → 在对应周期（~4-6×滞后≈300s）
    /// 自激。仿真用纯一阶腔体（相位上限 90°）故未暴露。留作实验参数，
    /// 若要试探仅建议 k≤0.5（|kH|<1 有结构保证）。到温下冲的既定答案：
    /// 接受 0.05~0.08 一次性暂态，过冲敏感批次用温度曲线末段缓坡。
    /// </summary>
    public double OuterTrackingBoost { get; set; } = 0;

    /// <summary>缺口前馈限幅（℃），防深度饱和段把腔体设定值推飞。</summary>
    public double OuterBoostClampC { get; set; } = 3;

    /// <summary>
    /// 落地偏置钳位（"第三枪"）的交接带宽（℃），0 关闭。误差首次缩到带内起、
    /// 至首次过零止的窗口内，把外环偏置钳在"稳态偏置 ± 余量"的近侧——
    /// 不许比例项把腔体压得比稳态位深太多，腔体提前归位，到温欠账大减。
    ///
    /// 与被实机否决的两个连续补偿（斜率超前/缺口前馈）的本质区别：本机制是
    /// 事件武装的静态界（max/min 与常数比较），无增益、无动态、无新回路，
    /// 结构上不存在振荡模式；最坏情况只是趋近末段慢一到两分钟。
    /// 生效窗口过零即闭（复用过零放电的武装标志），稳态抗扰权限不受任何影响。
    /// </summary>
    /// 【默认关闭（2026-07-31 实测裁决）】精度档实测：下冲降到 0.018℃，但滑行段
    /// 13.5min 使到温总时长 +10min，且 3℃/min 斜坡仍快于内环跟踪（腔体过冲 0.32、
    /// 核心回勾 0.024）。下冲↔速度的交换率由 τc≈700s 定死，作为默认不划算——
    /// 默认回到快速模式（下冲 0.05~0.08、到温最快、曲线平滑）。客户规格要求
    /// 过冲 <0.03℃ 时设 0.8 启用，并可把 OuterClampSlewCPerMin 降到 1.5 消回勾。
    public double OuterBiasClampBandC { get; set; } = 0;

    /// <summary>落地偏置钳位的余量（℃）：允许偏置比稳态偏置深出的最大幅度。</summary>
    public double OuterBiasClampMarginC { get; set; } = 0.3;

    /// <summary>钳位地板的抬升限速（℃/min）：咬合时从当时偏置匀速过渡到目标地板，
    /// 免去大台阶冲击（内环追 5℃ 台阶会过冲）。两个常数间的限速过渡，静态类、无回路。</summary>
    public double OuterClampSlewCPerMin { get; set; } = 3;

    /// <summary>设置某通道闭环/自整定使用的反馈传感器（1=TC1，2=TC2）。</summary>
    public void SetFeedbackSensor(int ch, int sensor)
    {
        if (sensor is not (1 or 2))
            throw new ArgumentOutOfRangeException(nameof(sensor), "反馈传感器只能是 1 或 2");
        State(ch).FeedbackSensor = sensor;
    }

    private static double Pv(ChannelState s, int ch, TecSnapshot snapshot)
    {
        var sensor = s.FeedbackSensor is 1 or 2 ? s.FeedbackSensor : ch;
        return sensor == 1 ? snapshot.Temp1C : snapshot.Temp2C;
    }

    public (double SetpointC, bool Active, double LastDutyPercent) GetChannelStatus(int ch)
    {
        var s = State(ch);
        return (s.SetpointC, s.Active, s.LastDutyPercent);
    }

    // ---------- 启停 ----------

    /// <summary>启动整个循环（连接设备后调用一次；未激活通道只监视不输出）。</summary>
    public void Start()
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        _consecutiveFailures = 0;
        _loopTask = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>误差在此范围内且输出未饱和时，认为处于稳态，记录输出用于下次积分预置。</summary>
    public double SteadyBandC { get; set; } = 0.2;

    /// <summary>连续处于稳态多少个周期后才写入前馈模型（默认约 1 分钟 @500ms）。</summary>
    public int SteadyCyclesToLearn { get; set; } = 120;

    /// <summary>最近一次记录到的稳态输出（PID 内部符号，未取反）；无记录为 null。</summary>
    public double? GetLastSteadyOutput(int ch) => State(ch).LastSteadyOutput;

    /// <summary>该通道的稳态前馈模型（自动学习的工作点集合）。</summary>
    public SteadyStateFeedforward GetFeedforward(int ch) => State(ch).Feedforward;

    // ---------- 串级（Tr 模式） ----------

    /// <summary>外环更新周期。内外环周期比建议 ≥4:1，默认 2s（内环 500ms）。</summary>
    public TimeSpan OuterPeriod { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 【本地改动】串级外环的外部被控量（釜内 Tr）读不到时先保持多久再停控。保持期：外环冻结（不算、不放电、
    /// 不学偏置、不扩程）、曲线暂停、内环设定钳到主设定值（不高于、不低于釜内设定——夹套停在釜内设定上，
    /// 传热推不过设定，盲区里不会把釜推过头）、内环照跑；Tr 在这段里回来就无扰接着串级。0 = 读不到当拍停控。
    /// 只对外部来源（SetOuterSource）生效：本机 TC 读 NaN 是探头没接，不是暂态。
    /// </summary>
    public TimeSpan OuterLossGrace { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 【本地改动】外环积分分离：釜内离设定值超过这么多（℃）时外环只用 P，不积分。NaN = 关。
    /// 现场：釜内 90 ℃ 一步到位，釜的时间常数二十分钟，趋近那十几分钟里外环积分攒了 20 多 K，
    /// Tr 到 90 时夹套设定还在 108、夹套 110 只能自然凉——过冲 1 ℃ 多还在往上。远处的偏差交给 P 和偏置上限，
    /// 积分只在带内攒维持温度要的那几度。学到的稳态偏置预置照旧（带外冻在预置值上）。
    /// </summary>
    public double OuterIntegralBandC { get; set; } = 3;

    /// <summary>
    /// 【本地改动】趋近不积分：外环误差在 <see cref="OuterApproachWindow"/> 里缩小得比这个快（K/min）——釜内正往目标走——
    /// 外环积分就不动；停住了 / 往外漂了才积。积分在参照值（信得过的那个偏置）另一边、往回退的不拦。≤0 或 NaN = 关。
    /// 现场（2026-11-02，双向挡釜内 32 → 20 ℃）：带内三分多钟的趋近里积分攒到 −4.06 K，Tr 到 20 时夹套目标还压在 16 ℃，
    /// 积分要等 Tr 掉到 20 以下才往回退、退了六分多钟——Tr 一路掉到 18.94 ℃、十二分钟才回来。用这一场拟合的釜
    /// （时间常数 ≈ 675 s、稳态偏置 +1.3 K、夹套跟随 ≈ 35 s）回放：趋近时冻住积分，这一段攒出来的过冲就没了。
    /// 用误差的变化（不是 Tr 的）：曲线斜坡上设定值和 Tr 一起走、误差不缩，积分照常跟坡；坡停了 Tr 追上来才算趋近。
    /// </summary>
    public double OuterApproachFreezeCPerMin { get; set; } = 0.05;

    /// <summary>【本地改动】趋近判据量误差变化的时间窗（宇电 Tr 0.01 K 分辨率下 60 s 窗的量化噪声约 0.01 K/min，比门槛小一截）。</summary>
    public TimeSpan OuterApproachWindow { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 【本地改动】趋近的第二条判据：误差缩得没 <see cref="OuterApproachFreezeCPerMin"/> 快，但照眼下的速度这么长时间内走得完剩下的误差，
    /// 也算在趋近，积分照样不往外攒。≤0 = 只看速度门槛。
    /// 现场（2026-11-03，双向挡釜内 22 → 15 ℃）：釜内 15.26 → 15.07 那几分钟每分钟只靠近 0.02–0.05 K（照那个速度剩下 5–8 分钟就到），
    /// 速度门槛当它停住了，外环积分从 +1.5 一路退到 +0.69；到站时夹套偏冷，釜内掉到 14.86，停控时（32 分钟）还在 14.97。
    /// 用这一场拟合的釜（一阶、带壁热容两种都试了）回放：积分不漂了，冲过 0.07–0.11 → 0.01–0.07 K；代价是积分起点偏在「停在前面」那一侧时
    /// 要等爬得更慢积分才开始补，到站平均晚约 2 分钟。
    /// </summary>
    public TimeSpan OuterApproachHorizon { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>【本地改动】按到站时间算趋近时，误差至少得缩得这么快（K/min）、窗里至少缩过这个速度 × 窗长（60 s 窗 = 0.015 K）才算在动——
    /// 60 s 窗里 Tr 跳一个字（0.01 K）就是 0.01 K/min，读数来回跳一下不算往目标走。</summary>
    public double OuterApproachMinCPerMin { get; set; } = 0.015;

    /// <summary>
    /// 【本地改动】内环跟不上不积分（K）：夹套离它的目标（外环上一拍算的内环设定）还差这么多、且差在外环正要推的那一边时，
    /// 外环积分不往那个方向攒（往回退不拦）。≤0 或 NaN = 关。
    /// 小台阶（20 → 22 ℃）时比例项不饱和，改设定后头一两分钟夹套还在追、Tr 还没动——「趋近」判据看不出来，
    /// 积分照攒，回放过冲 0.2 K；加上这条 0.02 K。双向挡 TEC 等冷水机 / 等驻留没出力时也是同一回事：执行不了的需求别记账。
    /// </summary>
    public double OuterInnerLagFreezeC { get; set; } = 0.5;

    /// <summary>
    /// 【本地改动】大降温收尾：釜内满幅降温到低温目标、最后一段爬的时候，外环临时多给比例——多给 外环 P × 这个倍数 × g，
    /// g 是爬行程度（0 → 1）。≤0 = 关（下面几条一起关）。只在双向挡那张表上、不在曲线 / 压低里。
    /// 现场（2026-11-03，双向挡釜内 8 → −15 ℃）：满幅降温 20 分钟，然后 −14.3 → −15.0 爬了 25 分钟，47 分钟才进 ±0.1，
    /// 到站后又往下偏到 −15.06。拿逐拍数据拟合（<c>CascadeFieldTwinTests</c>）：降温后釜壁 / 盖那份慢热容还落在釜内后面 2–3 K、
    /// 往釜里漏热，维持目标要的夹套比长期稳态再冷一截、二三十分钟才退；「趋近不积分」让外环积分在这段不攒，只剩比例推，
    /// 越近推得越轻——就是爬。放开积分去补又会冲过（攒的到站要还）。多给比例不留电荷：误差到 0 它自己就没了。
    /// g 只在「爬」时升：|误差| ≤ 0.8 K、照 60 s 里的速度 <see cref="OuterFinishCrawlHorizon"/> 内到不了；
    /// <see cref="OuterFinishRamp"/> 升满；到站后锁住不再加；60 s 平均误差在 ±<see cref="OuterLearnBandC"/> 里待够
    /// <see cref="OuterFinishSettleTime"/>（或到站后过了 <see cref="OuterFinishMaxHold"/>）算稳住，<see cref="OuterFinishRelease"/> 退完。
    /// 武装另要：目标 ≤ <see cref="OuterFinishMaxSetpointC"/>、内环连续顶在满制冷 ≥ <see cref="OuterFinishSaturation"/>、釜内走过 ≥ 2 K——
    /// 中温段（0–10 ℃）、换一台釜、夹套再慢一点时孪生里这一下会把釜推得来回摆，先只放在现场验证过的低温段。
    /// 武装期间两件事跟着改：过零那一枪按过零速度封顶（<see cref="OuterFinishShotTau"/>）、到 g 退完之前不学稳态偏置——
    /// 这段里积分加多给的那份是「漏热还没退」的偏置，不是这个温度的稳态，记进表下次起步就按它偏。
    /// 倍数取 0.5（外环 Kp 2.5 时最多 3.75）、总的不超过 <see cref="OuterFinishMaxKp"/>：1.0 时现场那场还能再快 3–4 分钟，
    /// 但孪生里夹套再慢一截（滞后 150 s）、釜壁更慢、改个冷点的夹套时都会到站后再出 ±0.1 一两回——以前「腔体跟踪缺口前馈」就是仿真说行、
    /// 现场纯滞后一大就自激（<see cref="OuterTrackingBoost"/>），这回先取一半，看过现场数据再说。
    /// 孪生回放（现场那场）：进 ±0.1 47 → 43 min、±0.05 50 → 48 min、冲过 0.025 → 0.010，到站后不再往下偏；
    /// 升温、小台阶、中温段不武装，逐点同现状。
    /// </summary>
    public double OuterFinishGain { get; set; } = 0.5;

    /// <summary>【本地改动】大降温收尾加上多给的那份以后外环总比例不超过它：表里外环 Kp 本来就大的（≥ 它）不再多给。
    /// 孪生里外环 Kp 6 的 −25 → −28 多给以后合计到 9，冲过 0.03 → 0.09。</summary>
    public double OuterFinishMaxKp { get; set; } = 5;

    /// <summary>【本地改动】大降温收尾多给着的时候改目标、新目标离釜内不到 <see cref="OuterFinishCarryNearC"/>：多给的那份不一下撤掉，
    /// 在这么长时间里线性退完。一下撤掉夹套目标跳几 K（漏热的补偿全在那份里），孪生里爬着改到釜内附近的上下冲 0.2 → 0.3 K；
    /// 带着退：35 种里只剩 3 种比不收尾时多（≤ 0.03 K）。离得远的改目标照旧一下撤掉（新的一段趋近，带着反而拖慢）。≤0 = 不带。</summary>
    public TimeSpan OuterFinishCarry { get; set; } = TimeSpan.FromMinutes(20);

    public double OuterFinishCarryNearC { get; set; } = 1.0;

    /// <summary>【本地改动】大降温收尾只在目标不高于它（℃）时武装。</summary>
    public double OuterFinishMaxSetpointC { get; set; } = -10;

    /// <summary>【本地改动】大降温收尾武装要内环连续顶在满制冷这么久（双向挡 TEC 接着时才算；TEC 被扣着时不算、也不清）。</summary>
    public TimeSpan OuterFinishSaturation { get; set; } = TimeSpan.FromSeconds(300);

    /// <summary>【本地改动】算「爬」：照 60 s 里的趋近速度这么长时间内走不完剩下的误差（或不动 / 往外走）。</summary>
    public TimeSpan OuterFinishCrawlHorizon { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>【本地改动】g 从 0 升到 1（爬）、或误差出了 0.8 K 时从 1 退到 0 用的时间。</summary>
    public TimeSpan OuterFinishRamp { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>【本地改动】60 s 平均误差连续在 ±<see cref="OuterLearnBandC"/> 里这么久算稳住。</summary>
    public TimeSpan OuterFinishSettleTime { get; set; } = TimeSpan.FromSeconds(300);

    /// <summary>【本地改动】到站后最多锁这么久，没稳住也当稳住（小幅慢摆、Tr 噪声大时 g 别一直挂着、偏置一直不学）。</summary>
    public TimeSpan OuterFinishMaxHold { get; set; } = TimeSpan.FromMinutes(20);

    /// <summary>【本地改动】稳住后 g 从 1 退到 0 用的时间（慢退，让积分接得住）。</summary>
    public TimeSpan OuterFinishRelease { get; set; } = TimeSpan.FromMinutes(20);

    /// <summary>【本地改动】防摆：g &gt; 0 时釜内以快于这个速度（K/min）离开目标——多给的比例带出来的摆——当到站，g 按 <see cref="OuterFinishAwayRelease"/> 退。</summary>
    public double OuterFinishAwayCPerMin { get; set; } = 0.05;

    public TimeSpan OuterFinishAwayRelease { get; set; } = TimeSpan.FromSeconds(600);

    /// <summary>【本地改动】武装时过零那一枪的封顶：积分最多往学到的偏置挪 过零速度（K/min）× 这么久 ÷ 60，只往挡住去势的方向挪。
    /// 爬着穿过去的（速度慢）几乎不挪——这时积分里是漏热那份偏置，一枪打回表里的长期值就把釜内推回去（现场到站后往下偏的那一下）。</summary>
    public TimeSpan OuterFinishShotTau { get; set; } = TimeSpan.FromSeconds(400);

    /// <summary>【本地改动】过零速度按最近这么长的外环误差做最小二乘。</summary>
    public TimeSpan OuterFinishShotWindow { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>【本地改动】过零速度的那段误差历史至少得跨这么久，不够（刚改过目标 / Tr 刚恢复）就不挪——几个点算斜率，跳一个字就算成快穿。</summary>
    public TimeSpan OuterFinishShotMinSpan { get; set; } = TimeSpan.FromSeconds(60);

    private const double FinishMinTravelC = 2.0;
    private const double FinishBandC = 0.8;
    private const double FinishArrivedC = 0.1;
    private const double FinishShotMinGapC = 0.05;

    /// <summary>
    /// 【本地改动】起步时外环积分从哪儿起：表里这个温度学到过稳态偏置就用它（原来就这样）；没学到时看起步前这么长一段
    /// 釜内和夹套稳不稳，稳就拿最后 <see cref="OuterStartAverage"/> 里「夹套 − 釜内」的平均当起点——那就是起步那个温度
    /// 维持住要的偏置。散热型的偏置随温度单调变（温度越高越大），拿起点的估终点：降温估大了（夹套给得偏高）、升温估小了
    /// （偏低），两边都是「停在目标前面再靠过去」那一侧，不会冲过去。都不成（起步前温度还在变）就从 0 起、往来的方向加
    /// <see cref="OuterBrakeBiasC"/> 刹车。≤0 = 不估。
    /// 现场那一场起步前夹套 33.34、釜内 32.03：估出 +1.31，拟合出的稳态偏置正是 +1.3。回放过冲 1.04 → 0.03 K。
    /// </summary>
    public TimeSpan OuterStartQuietWindow { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>【本地改动】起步估偏置取「夹套 − 釜内」平均的那最后一段。</summary>
    public TimeSpan OuterStartAverage { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>【本地改动】起步前算「稳」：时间窗里釜内最高最低差不超过它（K）。</summary>
    public double OuterStartQuietOuterC { get; set; } = 0.2;

    /// <summary>【本地改动】起步前算「稳」：时间窗里夹套最高最低差不超过它（K）。</summary>
    public double OuterStartQuietInnerC { get; set; } = 0.6;

    /// <summary>
    /// 【本地改动】没学到稳态偏置、起步前又不稳时外环积分的刹车起点（K）：釜内在目标上方（降温趋近）给 +，下方给 −——
    /// 比例项单独把釜内停在目标前面一点，积分再慢慢靠过去。釜内离目标不到 <see cref="OuterBrakeMinDistanceC"/> 不加；
    /// 离得近时按 (1 + 外环 Kp) × 距离 ÷ 2 封顶（比例项先走一半路）。偏置还没摸清时直接改目标，按新方向重摆。
    /// 回放（稳态偏置 −1.3 ~ +1.3 K）过冲 ≤ 0.03 K；偏置大到 +2.5 K 时还会冲 0.2 K——那种温度跑过一次学到偏置就好了。
    /// </summary>
    public double OuterBrakeBiasC { get; set; } = 1.5;

    /// <summary>【本地改动】起步时釜内离目标多远（K）才加刹车。</summary>
    public double OuterBrakeMinDistanceC { get; set; } = 0.5;

    /// <summary>内环设定值安全限幅（℃），防止外环把腔体温度推到极端值。</summary>
    public double InnerSetpointMinC { get; set; } = -50;
    public double InnerSetpointMaxC { get; set; } = 200;

    /// <summary>
    /// 【本地改动】升温加速（速度环 + 刹车曲线）。现在的外环只看「还差多远」、不看「升得多快」：夹套最多给到 主设定 + 偏置限幅（8 K），
    /// 光把上限放高，收手一定晚、一定冲过。速度环看釜内升得多快：离得远时夹套顶高把釜往上推，釜内升得比「这个距离允许的速度」快就提前
    /// 把夹套往下拉（必要时 TEC 刹车），到站时速度正好收到 0，再交回现在的外环收尾。
    /// 夹套目标 = 釜内 + 稳态偏置 B + (τ̂ + T̂w)·v* − T̂w·τ̂·a·f + Kv·(v* − v_m)，Kv 项只在 v_m &gt; v*（升得太快）时有；
    /// 夹在 [主设定 − 偏置限幅, 主设定 + 余量（保守 15 K / 正常 19 K）] 里，再不超过 釜内 + <see cref="HeatBoostMaxDeltaC"/>。
    /// v_m = 最近 <see cref="HeatBoostWindow"/> 釜内的最小二乘斜率；v*(e) 是刹车曲线：离目标 e ≤ <see cref="HeatBoostLinearC"/> 时 e / Tb（保守 250 s / 正常 200 s），
    /// 再远按恒减速 <see cref="HeatBoostDecel"/>：√((e1/Tb)² + 2a(e − e1))；f = 刹车偏置分数（e 过 e1 后 <see cref="HeatBoostFadeC"/> 内渐入，再乘 v_m / v*）。
    /// 只在：双向挡那张表、串级控釜内、起步 / 改目标时釜内在目标下面 ≥ <see cref="HeatBoostEnterC"/>、没在曲线 / 压低里、这个目标学到过稳态偏置、
    /// 会话说 TEC 接得上（<see cref="SetHeatBoostAllowed"/>）。条件不齐先照现在的外环升，趋近途中齐了再接（中途接入孪生里验过）。
    /// 交还：|e| ≤ <see cref="HeatBoostHandBandC"/> 且速度 ≤ <see cref="HeatBoostHandStillCPerMin"/> 连续 <see cref="HeatBoostHandHold"/>
    /// （积分 = 最近 <see cref="HeatBoostHandAverage"/>「夹套目标 − 主设定 − Kp·e」的平均，钳在 B ± <see cref="HeatBoostHandBiasC"/>）；
    /// 或卡住（离目标 ≤ <see cref="HeatBoostStallBandC"/>、<see cref="HeatBoostStallWindow"/> 里釜内动了不到 <see cref="HeatBoostStallC"/>：
    /// 积分 = 平均「夹套目标 − 釜内」− Kp·e，不钳——学到的偏置偏低得多时速度环只有比例、停在目标前面）。交还 / 中止时两枪放电都撤防（积分已经接好了）。
    /// 中止回现在的外环：改目标（按新目标重判）、曲线、压低、Tr 丢失、换表、关了加速、TEC 接不上 / 被扣着连续 <see cref="HeatBoostDenyGrace"/>、
    /// 釜内冲过目标 <see cref="HeatBoostAbortOverC"/>（最后一道网）。中止时积分 = B，但正在刹车（现在的外环拿 B 会把夹套目标往上抬）时
    /// 接成「跟速度环此刻的夹套目标一样」，最多比 B 低 <see cref="HeatBoostAbortBiasFloorC"/>——不然壁里存的热把釜推过头。
    /// 速度环期间外环积分不动、不学稳态偏置、不扩程、不打两枪、不算大降温收尾。
    /// 孪生（2026-11-04 现场 −2 → 20 ℃ 拟合的釜，正常档）：首进 ±0.1 30.1 → 26.6 min、稳进 ±0.05 46.5 → 29.1 min、冲过 0.071 → 0.008 K；
    /// 15 → 25 稳进 37.9 → 23.1、20 → 21 稳进 28.6 → 9.4、20 → 50 首进 35.5 → 31.0。12 种失配（釜快 30 %、壁慢一倍、多 60 s 纯滞后、
    /// 偏置偏 0.5、TEC 一半、内环慢……）每格都不比现在冲得多。代价：TEC 早接（那场 9.7 min 就要），继电器每场切 9–15 次（原来 1 次）；
    /// TEC 根本接不上时比现在更糟（11.5 对 7.6 K）——所以要会话放行。
    /// </summary>
    public HeatBoostLevel HeatBoost { get; set; } = HeatBoostLevel.Off;

    /// <summary>【本地改动】升温加速时夹套目标最多比釜内高多少（K）：玻璃釜怕温差太大。≤0 = 不限。
    /// 孪生：−2 → 20 不限时温差到 37 K；限 30 K 首进 26.6 → 27.7 min、限 25 K → 29.5 min，冲过都不变。</summary>
    public double HeatBoostMaxDeltaC { get; set; } = 30;

    /// <summary>【本地改动】升温加速：起步 / 改目标时釜内至少在目标下面这么多（K）才进；趋近途中进了这个距离就不再进。</summary>
    public double HeatBoostEnterC { get; set; } = 0.5;

    /// <summary>【本地改动】升温加速前馈：釜内每升 1 K/s 夹套要比釜内多高（s，约等于釜的时间常数；现场拟合 1030 s，取小一点偏保守）。</summary>
    public double HeatBoostTauHat { get; set; } = 800;

    /// <summary>【本地改动】升温加速前馈：夹套 → 釜壁那一段的滞后（s，现场拟合 140 s）。</summary>
    public double HeatBoostWallHat { get; set; } = 250;

    /// <summary>【本地改动】升温加速刹车曲线的恒减速（K/s²）。真正限速的是它（壁里存的热放得多快）：再大孪生里纯滞后 +60 s、壁慢一倍就冲过 0.1。</summary>
    public double HeatBoostDecel { get; set; } = 6e-5;

    /// <summary>【本地改动】升温加速刹车曲线：离目标这么近（K）以内按 e / Tb 线性收。</summary>
    public double HeatBoostLinearC { get; set; } = 0.6;

    /// <summary>【本地改动】升温加速刹车偏置过了线性段以后在这么多 K 里渐入。</summary>
    public double HeatBoostFadeC { get; set; } = 0.5;

    /// <summary>【本地改动】升温加速速度反馈（s）：升得比刹车曲线快时每快 1 K/s 夹套多降这么多 K；慢了不加（只管刹车）。</summary>
    public double HeatBoostKv { get; set; } = 1500;

    /// <summary>【本地改动】升温加速测速窗：釜内最小二乘斜率（宇电 0.01 K 分辨率下 40 s 窗相邻两拍抖 0.003 K/min）。不到一半窗长记 0。</summary>
    public TimeSpan HeatBoostWindow { get; set; } = TimeSpan.FromSeconds(40);

    public double HeatBoostHandBandC { get; set; } = 0.1;
    public double HeatBoostHandStillCPerMin { get; set; } = 0.03;
    public TimeSpan HeatBoostHandHold { get; set; } = TimeSpan.FromSeconds(240);
    public TimeSpan HeatBoostHandAverage { get; set; } = TimeSpan.FromSeconds(120);
    public double HeatBoostHandBiasC { get; set; } = 0.3;
    /// <summary>【本地改动】卡住兜底的带（K）：学到的偏置偏低 2.5 K 以上时速度环（只有比例）会停在离目标 0.5–0.7 K 处一直不动——带要盖住它。
    /// 孪生：偏低 2.8 / 4 / 6 K 都在 40 分钟上下交还、冲过 ≤ 0.03 K；带 0.5 时停在 19.4 ℃ 四个钟头。</summary>
    public double HeatBoostStallBandC { get; set; } = 2.0;
    public TimeSpan HeatBoostStallWindow { get; set; } = TimeSpan.FromSeconds(300);
    public double HeatBoostStallC { get; set; } = 0.02;

    /// <summary>
    /// 【本地改动】升温加速最后一道网：速度环里釜内冲过目标这么多（K）交回现在的外环。只管「冲得离谱、两条交还都够不着」那种。
    /// 别设小：过了目标速度环刹得比现在的外环狠（夹套目标 ≈ 釜内 + B + 5.25·e，外环是 3.5·e），评审时 0.05 在纯滞后 +60 s 的 20 → 21
    /// 把冲过从 0.076 推到 0.127 K，0.10 也有几格变坏；0.5 在孪生全部失配里一次都不触发。
    /// </summary>
    public double HeatBoostAbortOverC { get; set; } = 0.5;

    /// <summary>【本地改动】升温加速：会话说 TEC 接不上 / TEC 被扣着要连续这么久才中止（读一次冷水机失败、IO 闪一下别把整段加速丢了；孪生里 TEC 晚 60 s 照样不冲）。</summary>
    public TimeSpan HeatBoostDenyGrace { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>【本地改动】升温加速刹车中途中止：交给现在的外环的积分最多比学到的偏置低这么多（K）。</summary>
    public double HeatBoostAbortBiasFloorC { get; set; } = 2;

    /// <summary>【本地改动】升温加速刹车曲线线性段的时间常数 Tb（正常档；保守档 × 1.25 = 250 s）。</summary>
    public TimeSpan HeatBoostBrakeTime { get; set; } = TimeSpan.FromSeconds(200);

    /// <summary>【本地改动】升温加速夹套最多高出主设定多少（K，正常档；保守档 15 K）。不低于外环偏置限幅。</summary>
    public double HeatBoostHeadroomC { get; set; } = 19;

    public double HeatBoostConservativeHeadroomC { get; set; } = 15;

    /// <summary>【本地改动】升温加速此刻的刹车曲线时间常数（s）和夹套最多高出主设定的余量（K）。</summary>
    private (double Tb, double HeadroomC) HeatBoostCurve()
        => HeatBoost == HeatBoostLevel.Conservative
            ? (HeatBoostBrakeTime.TotalSeconds * 1.25, HeatBoostConservativeHeadroomC)
            : (HeatBoostBrakeTime.TotalSeconds, HeatBoostHeadroomC);

    /// <summary>【本地改动】会话每拍告诉回路这一路此刻 TEC 接不接得上（冷却水已通 / 人选了不开 / 不用水）：升温加速靠 TEC 刹车，接不上不进、进了就退。</summary>
    public void SetHeatBoostAllowed(int ch, bool allowed) => State(ch).HeatBoostAllowed = allowed;

    /// <summary>【本地改动】这一路此刻夹套目标归升温加速管。</summary>
    public bool IsHeatBoosting(int ch)
    {
        var s = State(ch);
        return s.VelMode && s.Active && s.Strategy == ControlStrategy.Cascade;
    }

    /// <summary>【本地改动】这一路在等 TEC 接得上才能升温加速（别的条件都齐了，只差会话放行）：会话可以借这个提前问冷却水。</summary>
    public bool HeatBoostWaitingForTec(int ch)
    {
        var s = State(ch);
        return s.VelWantsTec && !s.VelMode && s.Active && s.Strategy == ControlStrategy.Cascade;
    }

    /// <summary>
    /// 配置串级：外环以 outerSensor（默认 TC1 内部）为被控量，输出内环设定值偏置。
    /// 外环增益单位：Kp 为 ℃/℃（无量纲），Ki 为 1/s，Kd 为 s。
    /// </summary>
    public void ConfigureCascade(int ch, double kp, double ki, double kd,
        double maxBiasC, int outerSensor = 1, double derivativeFilterSeconds = 10)
    {
        var s = State(ch);
        var bias = Math.Abs(maxBiasC);
        s.OuterPid.Configure(kp, ki, kd, -bias, bias, derivativeFilterSeconds);
        s.OuterFeedbackSensor = outerSensor is 1 or 2 ? outerSensor : 1;
        s.ManualOuterGains = new PidGains(kp, ki, kd);
        s.ManualOuterMaxBiasC = bias;
    }

    /// <summary>
    /// 把该设定值对应的外环参数装到外环 PID 上：优先用增益表插值结果，其次用手动参数。
    /// 外环快慢取决于"腔体→内部"的传热速度，低温段整体变慢，外环也必须跟着变慢，
    /// 因此外环参数与内环一样需要随温度调度。
    /// </summary>
    /// <summary>
    /// 【本地改动】外环（釜内 → 夹套）的参数与稳态偏置该查哪张表：外环的对象是釜，跟此刻是加热棒还是 TEC 在出力无关，
    /// 不能随继电器换来换去（换一次外环增益和限幅就跳一次）。取 TEC / 加热棒两张表里离设定值最近的那个外环工作点所在的表；
    /// 都没登记外环的就用此刻执行器那张（让手动参数兜底）。
    /// 双向挡只查双向挡那张：继电器怎么扳都是那一张，外环本来就不跳；也不借另外两张（没整过的温度要先整，不拿别的对象凑）。
    /// </summary>
    public PidGainSchedule GetOuterSchedule(int ch, double setpointC) => OuterSchedule(State(ch), setpointC);

    private static PidGainSchedule OuterSchedule(ChannelState s, double setpointC)
        => s.Table == GainTable.Both ? s.BothSchedule : NearestOuterSchedule(s, setpointC);

    /// <summary>
    /// 【本地改动】稳态偏置（夹套 − 釜内）是釜的热平衡，跟哪只执行器在出力无关：双向挡那张表还没学到这个温度的，
    /// 借 TEC / 加热棒两张表里最近的那个（只读）——不然第一次跑双向挡外环积分从 0 起、两枪放电也打不了。学到的照样记进双向挡那张。
    /// </summary>
    private double? SteadyBiasFor(ChannelState s, double setpointC)
    {
        if (s.BiasOverrideC is not { } o) return TableBiasFor(s, setpointC);
        if (Math.Abs(setpointC - s.BiasOverrideSp) < 1e-9) return o;     // 【本地改动】自校正过的这一场按实测
        var live = TableBiasFor(s, setpointC);
        if (!OverrideCovers(s, setpointC)) return live;
        // 这一场实测过的点：回到实测过的目标直接按实测；夹在两个实测点中间按两头线性插（偏置随温度近乎线性）；
        // 只在一边的从那边离得最近的那个平移。只从最新那个平移不行：表副本只在改之前那一刻对，
        // 这一场别处学对了的行它还错着，从挪过去的目标平移回来会把错的那段斜率带回来
        // （孪生：43 那行错、40 对，42 改完去 40 学完回 42，副本平移 4.82 夹到 3.67、真值 3.32，每回升过来都冲 0.15、压低三次）
        (double Sp, double B) lo = (double.NaN, 0), hi = (double.NaN, 0);
        foreach (var (msp, mb) in s.BiasMeasured.Append((s.BiasOverrideSp, o)))
        {
            if (Math.Abs(msp - setpointC) < 1e-9) return mb;
            if (msp < setpointC && !(msp <= lo.Sp)) lo = (msp, mb);
            if (msp > setpointC && !(msp >= hi.Sp)) hi = (msp, mb);
        }
        if (!double.IsNaN(lo.Sp) && !double.IsNaN(hi.Sp))
            return lo.B + (hi.B - lo.B) * (setpointC - lo.Sp) / (hi.Sp - lo.Sp);
        var (refSp, refB) = double.IsNaN(lo.Sp) ? hi : lo;
        // 附近的目标按「实测 − 表」平移，两头从同一张表上取：TEC / 加热棒两张表按离得近的选、双向挡出了外推范围借另两张——
        // 两头各查各的，表和表之间那个台阶就加进来了（例：加热棒挡 37 改完 3.0、TEC 表那头 1.0、加热棒表这头 5.5，平移成 7.5）。
        // 表按自校正第一次改那一刻的副本查：改完在原目标稳住学到的会把表里那行改成实测，拿现在的表算差就没了，旁边还错着的行又回来
        // （孪生：40、43 两行都错成 5.53，40 学完再改 41.5，平移归零、按 4.31 打两枪冲 0.23、压低两次）。那张表在原目标上没数：直接用实测
        var (t1, from) = s.BiasCarryTables is { } pre
            ? TableBiasSource(s.Table, pre.Tec, pre.Heater, pre.Both, setpointC)
            : TableBiasSource(s, setpointC);
        if (t1 is not { } v || from is null) return live;
        var carried = v + (refB - (from.SteadyBiasAt(refSp) ?? v));
        if (live is not { } l) return carried;
        // 平移完不许跑出「实测 ~ 现在表里这个目标」那一段（按离实测点多远给偏置随温度的斜率留一点）：表只是原目标那行错、别的温度这一场学对了，
        // 整段平移就把差算了两遍（孪生：40 改完去 43 学对、回 42 平移成 1.77，真值 3.3，下冲 0.45）。
        // 只往物理上对的那边留：维持的偏置随釜内温度单调往上（散热 ∝ 釜内 − 室温，室温以下也一样），比实测点高的目标不会比实测还小、低的不会比实测还大
        // （两边都留的话 40 改完 3.24、回 42 夹到 2.94，掉 0.16）
        var slack = OuterBiasCarrySlackPerK * Math.Abs(setpointC - refSp);
        return setpointC > refSp
            ? Math.Clamp(carried, refB, Math.Max(refB, l) + slack)
            : Math.Clamp(carried, Math.Min(refB, l) - slack, refB);
    }

    /// <summary>【本地改动】平移的结果最多跑出「实测 ~ 现在表里」那一段多远：每离实测点 1 K 给这么多（K），稳态偏置随温度的斜率（散热 ∝ 釜内 − 室温，40 ℃ 上约 0.17 K/K）。</summary>
    private const double OuterBiasCarrySlackPerK = 0.15;

    /// <summary>【本地改动】记下这一场在这个目标上实测到的偏置（<see cref="ChannelState.BiasMeasured"/>）：整个换一份，不改原来那份的元素。</summary>
    private static void NoteMeasured(ChannelState s, double setpointC, double bias)
    {
        var m = s.BiasMeasured;
        var i = Array.FindIndex(m, x => Math.Abs(x.Sp - setpointC) < 1e-9);
        (double Sp, double Bias)[] next = i >= 0 ? [.. m] : [.. m, (setpointC, bias)];
        if (i >= 0) next[i] = (setpointC, bias);
        s.BiasMeasured = next;
    }

    /// <summary>【本地改动】自校正第一次改那一刻三张表的副本（<see cref="ChannelState.BiasCarryTables"/>）。</summary>
    private sealed record BiasTables(PidGainSchedule Tec, PidGainSchedule Heater, PidGainSchedule Both);

    /// <summary>【本地改动】这个目标归不归这一场的实测管（自校正改的那个、之后管着的目标上学到的，任一个 ±<see cref="OuterBiasCheckCarryC"/> 内平移）。</summary>
    private bool OverrideCovers(ChannelState s, double setpointC)
    {
        if (s.BiasOverrideC is null) return false;
        if (Math.Abs(setpointC - s.BiasOverrideSp) <= OuterBiasCheckCarryC + 1e-9) return true;
        foreach (var (msp, _) in s.BiasMeasured)
            if (Math.Abs(setpointC - msp) <= OuterBiasCheckCarryC + 1e-9) return true;
        return false;
    }

    /// <summary>【本地改动】增益表里这个目标的稳态偏置（不看自校正）。</summary>
    private static double? TableBiasFor(ChannelState s, double setpointC) => TableBiasSource(s, setpointC).Bias;

    /// <summary>【本地改动】同上，连带是哪张表给的数。</summary>
    private static (double? Bias, PidGainSchedule? From) TableBiasSource(ChannelState s, double setpointC)
        => TableBiasSource(s.Table, s.GainSchedule, s.HeaterSchedule, s.BothSchedule, setpointC);

    /// <summary>【本地改动】同上，三张表显式给（自校正平移拿表的副本查）。</summary>
    private static (double? Bias, PidGainSchedule? From) TableBiasSource(GainTable table, PidGainSchedule tec, PidGainSchedule heater,
        PidGainSchedule both, double setpointC)
    {
        var nearest = NearestOuterSchedule(table, tec, heater, both, setpointC);
        var sched = table == GainTable.Both ? both : nearest;
        if (sched.SteadyBiasAt(setpointC) is { } b) return (b, sched);
        if (table == GainTable.Both && nearest.SteadyBiasAt(setpointC) is { } nb) return (nb, nearest);
        return (null, null);
    }

    private static PidGainSchedule NearestOuterSchedule(ChannelState s, double setpointC)
        => NearestOuterSchedule(s.Table, s.GainSchedule, s.HeaterSchedule, s.BothSchedule, setpointC);

    private static PidGainSchedule NearestOuterSchedule(GainTable table, PidGainSchedule tecTable, PidGainSchedule heaterTable,
        PidGainSchedule bothTable, double setpointC)
    {
        var tec = tecTable.NearestOuterDistance(setpointC);
        var heater = heaterTable.NearestOuterDistance(setpointC);
        if (tec is null && heater is null) return table switch { GainTable.Heater => heaterTable, GainTable.Both => bothTable, _ => tecTable };
        if (heater is null) return tecTable;
        if (tec is null) return heaterTable;
        return tec <= heater ? tecTable : heaterTable;
    }

    private void ApplyOuterTuning(ChannelState s, double setpointC)
    {
        var tuning = Scheduling(s) ? OuterSchedule(s, setpointC).OuterAt(setpointC) : null;     // 【本地改动】外环按釜选表、按路开关
        tuning ??= s.ManualOuterGains is { } manual
            ? new OuterTuning(manual, s.ManualOuterMaxBiasC)
            : null;
        if (tuning is null) return;

        s.OuterPid.SetGains(tuning.Gains.Kp, tuning.Gains.Ki, tuning.Gains.Kd);
        var bias = Math.Abs(tuning.MaxBiasC);
        if (bias > 1e-6) s.OuterPid.SetOutputLimits(-bias, bias);
    }

    /// <summary>外环连续处于稳态多少次更新后才把稳态偏置写回增益表（默认约 1 分钟 @2s）。</summary>
    public int OuterSteadyCyclesToLearn { get; set; } = 30;

    public void SetStrategy(int ch, ControlStrategy strategy)
    {
        var s = State(ch);
        if (s.Strategy != strategy) s.Push = null;      // 换策略：压低随串级一起作废（【本地改动】）
        s.Strategy = strategy;
    }

    public ControlStrategy GetStrategy(int ch) => State(ch).Strategy;

    // ---------- 设定值生成层（结晶曲线等） ----------

    /// <summary>曲线执行结束时回调（后台线程）。参数为通道号与曲线名。</summary>
    public event Action<int, string>? ProfileCompleted;

    /// <summary>
    /// 启动温度曲线。曲线以启动瞬间的当前设定值为起点，按时间连续输出设定值；
    /// 串级模式下驱动外环（内部温度）设定值，单环模式下驱动内环设定值。
    /// </summary>
    public void StartProfile(int ch, ITemperatureProfile profile)
    {
        var s = State(ch);
        s.Profile = profile;
        s.ProfileStart = DateTime.Now;
        s.ProfileStartC = s.SetpointC;
        s.ProfileHoldAt = s.OuterLostSince is null ? null : s.ProfileStart;   // 【本地改动】保持期里起的曲线从头就暂停
        // 【本地改动】坡起点不在这儿清：坡中途重下一条坡（重规划）还用最早那个起点——那时的积分才是没带跟坡电荷的值
    }

    public void StopProfile(int ch) => State(ch).Profile = null;

    public ITemperatureProfile? GetProfile(int ch) => State(ch).Profile;

    /// <summary>预测维持指定设定值所需的稳态输出（PID 内部符号）；无数据返回 null。</summary>
    public double? PredictSteadyOutput(int ch, double setpointC) => PresetFor(State(ch), setpointC);

    /// <summary>
    /// 【本地改动】积分预置用的稳态输出：这张表那份前馈；双向挡那份还没学到时借加热棒那份正的（PID 正半轴原样给加热棒，两份单位一样）——
    /// 第一次在这个温度跑双向挡也不从 0 起（从 0 起就是下冲 0.8 ℃、五十分钟才收回来的那个坑）。
    /// </summary>
    private static double? PresetFor(ChannelState s, double setpointC)
        => s.Feedforward.Predict(setpointC)
           ?? (s.Table == GainTable.Both && s.HeaterFeedforward.Predict(setpointC) is { } h && h > 0 ? h : null);

    /// <summary>
    /// 激活某通道的闭环控温：切输出模式为通信设定（MODE=3）、打开使能、复位 PID。
    /// </summary>
    /// <param name="integralPreset">
    /// 积分预置（PID 内部符号，未取反）。null 表示自动：优先用稳态前馈模型对该设定值的预测，
    /// 其次用最近一次稳态输出，都没有则从 0 开始。
    /// </param>
    public async Task StartChannelAsync(int ch, double setpointC, double? integralPreset = null,
        CancellationToken ct = default)
    {
        var s = State(ch);
        if (s.Tuner is not null)
            throw new InvalidOperationException($"通道{ch}正在自整定，请先取消再启动控温");
        s.SetpointC = setpointC;
        s.ManualDutyPercent = null;
        await PrepareOutputAsync(ch, ct).ConfigureAwait(false);
        s.Pid.Reset();
        if ((integralPreset ?? PresetFor(s, setpointC) ?? s.LastSteadyOutput) is { } preset)
            s.Pid.PresetIntegral(preset);

        // 串级：外环也从干净状态起步，但把积分预置到该温度已学到的稳态偏置上。
        // 否则腔体设定值要从"等于主设定值"一路积分到实际需要的偏置（−10℃ 实测约需 −5℃），
        // 这段累积正是上一版下冲 0.8℃、五十多分钟才收回来的根源。
        if (s.Strategy == ControlStrategy.Cascade)
        {
            s.OuterPid.Reset();
            s.LastOuterUpdate = default;
            s.OuterSteadyCycles = 0;
            s.LastSteadyBias = null;
            s.BiasGuard.Reset();
            s.OuterApproachDischargeArmed = true;
            s.OuterCrossingArmed = true;
            s.OuterClampLatched = false;
            s.OuterClampBinding = false;
            s.LastOuterErrorC = double.NaN;
            s.OuterLostSince = null;                 // 【本地改动】
            s.ProfileHoldAt = null;
            s.Push = null;                           // 【本地改动】新一轮串级：上一轮压着的夹套目标作废
            s.VelMode = false;                       // 【本地改动】升温加速从头判（重新武装标志让回路线程立 VelEligible）
            s.VelEligible = false;
            s.VelWantsTec = false;
            s.VelOkSince = null;
            s.VelBlockedSaid = null;
            ClearVelReadout(s);
            // 测速历史、偏置自校正窗口归回路线程（RampCancelledFlag 那一拍清），这儿不碰——回路线程可能正在用
            s.BiasOverrideC = null;                  // 【本地改动】偏置自校正从头来（下面起步预置就要读它，在这边清）
            s.BiasCarryTables = null;
            s.BiasMeasured = [];
            s.LastBiasFix = default;
            s.BiasWallHat = double.NaN;
            s.BiasWallAt = default;
            s.BiasHoldUntil = default;
            s.InnerSetpointC = setpointC;
            Volatile.Write(ref s.RampCancelledFlag, 1);        // 【本地改动】坡尾回基 / 趋近判据 / 测速历史 / 偏置自校正窗口从头来（回路线程清）
            Volatile.Write(ref s.OuterTargetChangedFlag, 0);
            Volatile.Write(ref s.OuterRearmInPlaceFlag, 0);     // 【本地改动】上一轮停控前刚解除的压低不算这一轮的
            s.LastOuterSetpointC = double.NaN;
            ApplyOuterTuning(s, setpointC);   // 先装限幅，预置才会按正确范围截断
            // 【本地改动】积分起点：学到的稳态偏置 → 起步前稳着时的「夹套 − 釜内」→ 往来的方向刹车（OuterStartBias）
            var (start, brake, known, why) = OuterStartBias(s, setpointC);
            s.OuterPid.PresetIntegral(start);
            s.OuterBrakeC = brake;
            s.OuterBiasKnown = known;
            s.OuterIntegralPristine = true;
            s.OuterIntegralRef = s.OuterPid.LastI;
            Volatile.Write(ref s.OuterRearmedFlag, 1);
            if (Math.Abs(s.OuterPid.LastI) > 1e-12)
                s.InnerSetpointC = Math.Clamp(setpointC + s.OuterPid.LastI, InnerSetpointMinC, InnerSetpointMaxC);
            ChannelNotice?.Invoke(ch, "info", $"通道{ch}串级外环积分起点 {s.OuterPid.LastI:+0.00;-0.00;0} ℃：{why}");
        }

        s.SteadyCycles = 0;
        s.Runaway.Start(DateTime.Now);
        s.SaturationWarned = false;
        s.LastUpdateTime = default;
        s.LastDutyPercent = 0;
        s.Active = true;
    }

    /// <summary>启动继电器法自整定（会激起 ±回差以上的小幅温度振荡）。</summary>
    public Task StartAutoTuneAsync(int ch, double setpointC,
        double relayAmplitudePercent, double hysteresisC, CancellationToken ct = default)
        => StartAutoTuneAsync(ch, setpointC, relayAmplitudePercent, hysteresisC, coolOnly: false, ct);

    /// <summary>
    /// 【本地改动】coolOnly = TEC 只许制冷：继电在 [−上限, 0] 里摆（强制冷 / 弱制冷两个半周，
    /// 弱制冷半周靠环境漏热回升）。双工位主机「TEC 加热」不启用时 TEC 侧整定用它——
    /// TEC 反向加热在那台机器上没接 / 没验过，整定也不能让它反向出力。
    /// </summary>
    public async Task StartAutoTuneAsync(int ch, double setpointC,
        double relayAmplitudePercent, double hysteresisC, bool coolOnly, CancellationToken ct = default)
    {
        var s = State(ch);
        if (s.Table == GainTable.Both)
            throw new InvalidOperationException($"通道{ch}查的是双向挡那张表——那张表只能按加热棒 ↔ TEC 来回推整（StartAlternatingAutoTuneAsync）");
        if (coolOnly && s.Actuator is ActuatorMode.HeatOnly or ActuatorMode.HeaterTec)
            throw new InvalidOperationException($"通道{ch}执行器是加热棒（{(s.Actuator == ActuatorMode.HeatOnly ? "只加热" : "加热棒 + TEC 双向")}），不能按只制冷整定");
        coolOnly |= s.Actuator == ActuatorMode.CoolOnly;     // 【本地改动】形态本身就是只制冷
        if (s.Active)
            throw new InvalidOperationException($"通道{ch}正在闭环控温，请先停止再自整定");
        s.SetpointC = setpointC;
        s.ManualDutyPercent = null;
        await PrepareOutputAsync(ch, ct).ConfigureAwait(false);
        s.LastDutyPercent = 0;

        // 继电偏置：围绕"维持该温度所需的稳态输出"摆动。来源优先级：
        //   本次会话最近一次稳态输出（几分钟前刚测的，反映当日工况）
        //   → 前馈模型存档（跨天历史，工况可能已变——现场实测换水温后偏差达 17 个点）
        //   → 0。
        // 初值不准也不致命：整定器 Seeking 段带自寻中，会把中心逐步挪到位。
        var h = Math.Clamp(Math.Abs(relayAmplitudePercent), 1, 100);
        var bias = s.LastSteadyOutput ?? s.Feedforward.Predict(setpointC) ?? 0;
        var ceiling = Math.Max(1, s.MaxDutyMagnitude);       // 【本地改动】只制冷时 Pid.OutputMax 是 0，上限要拿幅度
        if (h >= ceiling && !coolOnly) bias = 0;   // 幅值本身已覆盖全量程时无偏置可言

        // 只加热执行器：输出下限 0，继电摆动整体落在正半轴（弱加热半周靠自然散热降温）。
        // 此时幅值不能超过上限的一半，否则可行偏置域为空——整定器会把偏置收缩到中点，
        // 等效幅值被迫减小，建议界面幅值 ≤ 上限/2。
        // 【本地改动】只制冷：上限 0、下限 −上限，继电整体落在负半轴（同样的道理反过来）
        var floor = s.Actuator == ActuatorMode.HeatOnly ? 0 : double.NaN;
        if (coolOnly) { floor = -ceiling; ceiling = 0; }
        s.Tuner = new RelayAutoTuner(setpointC, relayAmplitudePercent, hysteresisC, bias,
            outputCeilingPercent: ceiling,
            outputFloorPercent: floor);
        s.TuneStartTime = DateTime.Now;
    }

    /// <summary>
    /// 【本地改动】双向挡整定：掉下来那半周加热棒 heaterPercent、冲过设定值那半周 TEC tecPercent（用户定的：全程加热棒 ↔ TEC 来回推，
    /// 跟双向挡控温同一个对象）。整定器在 PID 那一侧按 ±heaterPercent 摆（中心从稳态输出起、可以自寻），TEC 那半周再乘
    /// 比值 r = tecPercent ÷ heaterPercent 写给 TEC——跟控温时「负半轴乘加热比」同一个映射，整完 r 就登记成那一行的加热比。
    /// 中心再怎么挪两半周也永远一正一负：哪一半都不许变 0（变成靠自然凉）或换成另一只执行器。
    /// 此刻要查双向挡那张表（会话已经把形态扳到加热棒 + TEC 双向，或冷水机最短间隔没到时的只加热）；
    /// 继电器跟着整定器输出的符号扳由组合会话做（GetTuningOutput），回路不管继电器。
    /// </summary>
    public async Task StartAlternatingAutoTuneAsync(int ch, double setpointC,
        double heaterPercent, double tecPercent, double hysteresisC, CancellationToken ct = default)
    {
        var s = State(ch);
        if (s.Table != GainTable.Both || s.Actuator is not (ActuatorMode.HeaterTec or ActuatorMode.HeatOnly))
            throw new InvalidOperationException($"通道{ch}没在双向挡那张表上（{s.Actuator} / {s.Table}）——先把热源扳到双向挡再整");
        if (s.Active)
            throw new InvalidOperationException($"通道{ch}正在闭环控温，请先停止再自整定");
        s.SetpointC = setpointC;
        s.ManualDutyPercent = null;
        await PrepareOutputAsync(ch, ct).ConfigureAwait(false);
        s.LastDutyPercent = 0;
        s.LastTuneOutput = null;

        var ceiling = Math.Max(1, s.MaxDutyMagnitude);
        var heat = Math.Clamp(Math.Abs(heaterPercent), 1, ceiling);
        var cool = Math.Clamp(Math.Abs(tecPercent), 1, ceiling);
        s.TuneCoolRatio = cool / heat;
        // 中心从「维持这个温度要的输出」起（双向挡那份稳态输出 / 前馈，PID 那一侧），没有就 0；整定器自寻中会挪到位
        var bias = s.LastSteadyOutput ?? PresetFor(s, setpointC) ?? 0;
        s.Tuner = new RelayAutoTuner(setpointC, heat, hysteresisC, bias,
            outputCeilingPercent: ceiling,
            outputFloorPercent: -ceiling / Math.Max(1, s.TuneCoolRatio),      // 乘了比值也不超过上限
            alternateMinPercent: Math.Min(AlternateMinPercent, heat))
        { CoolScale = s.TuneCoolRatio };
        s.TuneStartTime = DateTime.Now;
    }

    /// <summary>【本地改动】整定中输出最多挂多久（切热源：关输出 → 扳继电器 → 核反馈 → 开水 / 关水，正常十来秒）。</summary>
    public TimeSpan TuningSuspendLimit { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>【本地改动】双向挡整定里每半周至少留多少出力（%）：自寻中挪得再多，加热半周也还是加热棒、制冷半周也还是 TEC。</summary>
    public const double AlternateMinPercent = 3;

    /// <summary>【本地改动】双向挡那张表在这个温度的稳态偏置（夹套 − 釜内）：自己没学到借另两张最近的——跟控温时预置外环用的同一个数。</summary>
    public double? BothSteadyBias(int ch, double setpointC)
    {
        var s = State(ch);
        return s.BothSchedule.SteadyBiasAt(setpointC) ?? NearestOuterSchedule(s, setpointC).SteadyBiasAt(setpointC);
    }

    /// <summary>【本地改动】整定器此刻要的输出（PID 那一侧：正 = 加热、负 = 制冷）；没在整定或还没算过一拍是 null。</summary>
    public double? GetTuningOutput(int ch)
    {
        var s = State(ch);
        return s.Tuner is null ? null : s.LastTuneOutput;
    }

    /// <summary>停止某通道的一切输出活动（闭环/自整定/手动）：输出归零并关闭使能。</summary>
    public async Task StopChannelAsync(int ch, CancellationToken ct = default)
    {
        var s = State(ch);
        s.Active = false;
        s.Tuner = null;
        s.LastTuneOutput = null;                     // 【本地改动】
        s.ManualDutyPercent = null;
        s.LastDutyPercent = 0;
        s.OuterLostSince = null;                     // 【本地改动】
        s.ProfileHoldAt = null;
        s.Push = null;                               // 【本地改动】
        s.VelMode = false;                           // 【本地改动】
        s.VelEligible = false;
        s.VelWantsTec = false;
        await controller.SetDutyPercentAsync(ch, 0, ct).ConfigureAwait(false);
        await controller.SetEnableAsync(ch, false, ct).ConfigureAwait(false);
        if (ch == 1 && SyncMode != ChannelSyncMode.Independent)     // 【本地改动】独立两路：停 A 不动 B
        {
            // 无论哪种同步方式，主通道停止时通道 2 一并归零关闭
            await controller.SetDutyPercentAsync(2, 0, ct).ConfigureAwait(false);
            await controller.SetEnableAsync(2, false, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 手动写输出占空比（阶跃测试用；要求该通道未在闭环控温或自整定）。
    /// 勾选"反向输出"后此处输入同样自动反向；写入后由主循环每周期重写以防固件看门狗撤销输出。
    /// </summary>
    public async Task ManualDutyAsync(int ch, double dutyPercent, CancellationToken ct = default)
    {
        var s = State(ch);
        if (s.Busy)
            throw new InvalidOperationException($"通道{ch}正在闭环控温或自整定，请先停止再手动输出");
        if (s.Actuator == ActuatorMode.HeatOnly && dutyPercent < 0)
            throw new InvalidOperationException(
                $"通道{ch}当前为只加热执行器（电加热棒）：手动输出不能为负。" +
                "负占空比会被固件路由到制冷 PWM 引脚，而 TEC 功率线已断开");
        if (s.Actuator == ActuatorMode.CoolOnly && dutyPercent > 0)
            throw new InvalidOperationException(
                $"通道{ch}当前为只制冷执行器（TEC，「TEC 加热」不启用）：手动输出不能为正——正半轴那一极接的是加热棒的 SSR，而加热棒继电器断着");

        var raw = s.Actuator == ActuatorMode.HeatOnly
            ? s.HeaterSign * Math.Clamp(dutyPercent, 0, 100)   // 加热棒引脚路由固定，反向无意义；符号看接线【本地改动】
            : s.Actuator == ActuatorMode.HeaterTec
                ? ToDevice(s, Math.Clamp(dutyPercent, -100, 100))   // 【本地改动】正半轴加热棒（按接线符号）、负半轴 TEC（按反向）
                : Math.Clamp(s.InvertOutput ? -dutyPercent : dutyPercent, -100, 100);
        await PrepareOutputAsync(ch, ct).ConfigureAwait(false);
        await WriteDutyAsync(ch, raw, ct).ConfigureAwait(false);
        s.ManualDutyPercent = raw == 0 ? null : raw;
        s.LastDutyPercent = raw;
    }

    private async Task PrepareOutputAsync(int ch, CancellationToken ct)
    {
        await controller.SetModeAsync(ch, 3, ct).ConfigureAwait(false);
        await controller.SetDutyPercentAsync(ch, 0, ct).ConfigureAwait(false);
        await controller.SetEnableAsync(ch, true, ct).ConfigureAwait(false);

        // 上电安全：断电重启后设备默认不输出。固件无通信看门狗（厂家确认 2026-08-03），
        // 出厂 POWERMODE=0 会在重启后带旧占空比复活且无人监管——每次启动都写成 2（幂等）。
        await controller.SetPowerModeAsync(ch, 2, ct).ConfigureAwait(false);

        // PWM 引脚频率随执行器形态（FPWM 为设备全局参数，由本次启动的通道决定）：
        // 加热棒经过零型 SSR 切市电，须 0.5Hz 时间比例调功（50Hz 下 0.5% 分辨率，
        // 默认 10Hz 只有 10%）；TEC 用高频档减小电流脉动。每次启动都写，
        // 防止上一场加热棒会话把 0.5Hz 残留给 TEC 运行（0.5Hz 斩波对 TEC 是热循环折磨）。
        if (ManagePwmFrequency)      // 【本地改动】双工位机器不动设备全局的 FPWM
            await controller.SetPwmFrequencyAsync(
                State(ch).Actuator == ActuatorMode.HeatOnly ? HeaterPwmFrequencyLevel : TecPwmFrequencyLevel,
                ct).ConfigureAwait(false);

        if (ch == 1 && SyncMode != ChannelSyncMode.Independent)     // 【本地改动】独立两路各管各的
        {
            if (SyncMode == ChannelSyncMode.HostMirror)
            {
                // 镜像模式：通道 2 也要接受通信占空比，需同样切到 MODE=3 并清零
                await controller.SetModeAsync(2, 3, ct).ConfigureAwait(false);
                await controller.SetDutyPercentAsync(2, 0, ct).ConfigureAwait(false);
            }
            // 两种模式下通道 2 的输出级都需要使能；上电安全同样双通道生效
            await controller.SetEnableAsync(2, true, ct).ConfigureAwait(false);
            await controller.SetPowerModeAsync(2, 2, ct).ConfigureAwait(false);
        }
    }

    /// <summary>停止循环并尽力把两路输出归零（断开连接/退出程序时调用）。</summary>
    public async Task ShutdownAsync()
    {
        _cts?.Cancel();
        try { if (_loopTask is { } t) await Task.WhenAny(t, Task.Delay(2000)).ConfigureAwait(false); }
        catch { /* 忽略循环收尾异常 */ }

        foreach (var ch in new[] { 1, 2 })
        {
            try { await StopChannelAsync(ch).ConfigureAwait(false); }
            catch { /* 断线时尽力而为，设备端硬件保护兜底 */ }
        }

        _cts?.Dispose();
        _cts = null;
        _loopTask = null;
    }

    /// <summary>
    /// 【本地改动】串级起步时外环积分从哪儿起（见 <see cref="OuterStartQuietWindow"/>）：
    /// ① 这个温度学到的稳态偏置；② 起步前一段釜内、夹套都稳 → 最后一小段「夹套 − 釜内」的平均；
    /// ③ 都没有：釜内离目标够远就往来的方向加刹车（降温趋近 +、升温趋近 −），不然 0。返回值还没按偏置限幅夹（PresetIntegral 夹）。
    /// </summary>
    private (double Bias, double Brake, bool Known, string Why) OuterStartBias(ChannelState s, double setpointC)
    {
        if (SteadyBiasFor(s, setpointC) is { } learned)
            return (learned, 0, true, "这个温度学到的稳态偏置");
        var q = QuietStartBias(s);
        if (q.Bias is { } est)
            return (est, 0, true, $"这个温度没学到稳态偏置；起步前 {OuterStartQuietWindow.TotalSeconds:0} s 釜内 / 夹套都稳" +
                                  $"（波动 {q.OuterSpanC:F2} / {q.InnerSpanC:F2} K），按「夹套 − 釜内」估");
        var tr = q.LastOuterC;
        var brake = BrakeToward(s, tr, setpointC);
        if (brake != 0)
            return (brake, brake, false, $"这个温度没学到稳态偏置、{q.Reason}：釜内 {tr:F2} ℃ 在目标{(tr > setpointC ? "上" : "下")}方，" +
                                         $"往来的方向加 {brake:+0.0;-0.0} K 刹车——先停在目标前面，积分再慢慢靠过去");
        return (0, 0, false, $"这个温度没学到稳态偏置、{q.Reason}，釜内离目标不到 {OuterBrakeMinDistanceC:0.#} K：从 0 起");
    }

    /// <summary>
    /// 【本地改动】往来的方向的刹车：釜内在目标上方（降温趋近）+、下方（升温趋近）−；离得不够远 / 读不到 / 关了给 0。
    /// 不超过 (1 + 外环 Kp) × 距离 ÷ 2：比例项单独停住的地方离目标 = 刹车 ÷ (1 + Kp)，这样至少先走一半路——
    /// 离目标 0.5 K 的小台阶不至于一上来就被 1.5 K 的刹车按住、全靠积分慢慢爬。
    /// </summary>
    private double BrakeToward(ChannelState s, double outerC, double setpointC)
    {
        if (!(OuterBrakeBiasC > 0) || !double.IsFinite(outerC)) return 0;
        var d = Math.Abs(outerC - setpointC);
        if (d < OuterBrakeMinDistanceC) return 0;
        var brake = Math.Min(OuterBrakeBiasC, (1 + Math.Max(0, s.OuterPid.Kp)) * d / 2);
        return outerC > setpointC ? brake : -brake;
    }

    /// <summary>【本地改动】看起步前那段历史稳不稳；稳就给「夹套 − 釜内」的平均，不稳给原因。LastOuterC = 最近一个釜内读数（刹车定方向用）。</summary>
    private (double? Bias, double OuterSpanC, double InnerSpanC, double LastOuterC, string Reason) QuietStartBias(ChannelState s)
    {
        (DateTime At, double OuterC, double InnerC)[] h;
        lock (s.History) h = s.History.ToArray();
        var lastOuter = double.NaN;
        for (var i = h.Length - 1; i >= 0; i--)
            if (double.IsFinite(h[i].OuterC)) { lastOuter = h[i].OuterC; break; }
        if (double.IsNaN(lastOuter) && s.OuterSource is { } src)
        {
            try { lastOuter = src(); } catch { /* 读不到就不定方向 */ }
        }

        if (OuterStartQuietWindow <= TimeSpan.Zero) return (null, 0, 0, lastOuter, "起步估偏置关着");
        if (h.Length == 0 || DateTime.Now - h[^1].At > TimeSpan.FromSeconds(10))
            return (null, 0, 0, lastOuter, "起步前没有最近的温度记录");
        var end = h[^1].At;
        var from = end - OuterStartQuietWindow;
        if (h[0].At > from + TimeSpan.FromSeconds(Math.Min(10, OuterStartQuietWindow.TotalSeconds / 10)))
            return (null, 0, 0, lastOuter, $"起步前的温度记录不满 {OuterStartQuietWindow.TotalSeconds:0} s");
        double oMin = double.MaxValue, oMax = double.MinValue, iMin = double.MaxValue, iMax = double.MinValue, sum = 0;
        var n = 0;
        foreach (var (at, o, inner) in h)
        {
            if (at < from) continue;
            if (!double.IsFinite(o) || !double.IsFinite(inner))
                return (null, 0, 0, lastOuter, "起步前釜内 / 夹套有读不到的时候");
            oMin = Math.Min(oMin, o); oMax = Math.Max(oMax, o);
            iMin = Math.Min(iMin, inner); iMax = Math.Max(iMax, inner);
            if (at >= end - OuterStartAverage) { sum += inner - o; n++; }
        }
        if (n == 0) return (null, 0, 0, lastOuter, "起步前的温度记录不够");
        var oSpan = oMax - oMin;
        var iSpan = iMax - iMin;
        if (oSpan > OuterStartQuietOuterC || iSpan > OuterStartQuietInnerC)
            return (null, oSpan, iSpan, lastOuter,
                $"起步前 {OuterStartQuietWindow.TotalSeconds:0} s 温度还在变（釜内波动 {oSpan:F2} K、夹套 {iSpan:F2} K，" +
                $"稳要 ≤ {OuterStartQuietOuterC:0.##} / {OuterStartQuietInnerC:0.##} K）");
        return (sum / n, oSpan, iSpan, lastOuter, "");
    }

    /// <summary>【本地改动】两枪放电把积分预置到学到的稳态偏置：偏置摸清了，参照落到它、刹车不再单算。</summary>
    private static void OnOuterPresetToLearned(ChannelState s)
    {
        s.OuterBiasKnown = true;
        s.OuterIntegralPristine = false;
        s.OuterBrakeC = 0;
        s.OuterIntegralRef = s.OuterPid.LastI;
    }

    /// <summary>
    /// 【本地改动】坡上攒的那份放掉：积分回到起坡时的值（这个温度学到过稳态偏置就回到它），但只往「停在目标前面」那一侧回——
    /// side &gt; 0 = 从上面往下趋近（降温坡 / 釜内在目标上方），积分只许往上回（夹套给高点）；&lt; 0 反过来；0 不限。
    /// 起坡点不一定是稳着的（趋近半路上起的坡、带着刹车、重规划前那条坡的电荷……），限了方向它再不准也只会让釜停在前面、慢点到，不会推过头。
    /// </summary>
    private void DischargeRamp(int ch, ChannelState s, int side, string what)
    {
        var rampBase = s.RampBaseI!.Value;
        s.RampBaseI = null;
        var carried = s.OuterPid.LastI;
        var learned = SteadyBiasFor(s, s.SetpointC);
        var back = learned ?? rampBase;
        if (side > 0) back = Math.Max(back, carried);
        else if (side < 0) back = Math.Min(back, carried);
        if (Math.Abs(carried - back) > OuterDischargeMinGapC)
        {
            s.OuterPid.PresetIntegral(back);
            ChannelNotice?.Invoke(ch, "info",
                $"通道{ch}{what}：外环积分 {carried:F2} → {s.OuterPid.LastI:F2} ℃" +
                $"（{(learned is null ? "回到起坡时的值" : "这个温度学到的稳态偏置")}——跟坡多攒的那部分放掉，别带着它冲过头）");
        }
        if (learned is not null) s.OuterBiasKnown = true;
        s.OuterIntegralPristine = false;
        s.OuterBrakeC = 0;
        s.OuterIntegralRef = s.OuterPid.LastI;
    }

    /// <summary>【本地改动】记一笔外环误差并判断「正在趋近」：时间窗里误差在缩小，缩得比 OuterApproachFreezeCPerMin 快，
    /// 或者照这个速度 OuterApproachHorizon 之内走得完（速度至少 OuterApproachMinCPerMin）。
    /// 窗里不到 20 s（或窗长的 1/3）的历史不下判断（改设定 / 起步刚清过）。</summary>
    private bool ApproachingSetpoint(ChannelState s, DateTime now, double err)
    {
        var q = s.OuterErrHistory;
        q.Enqueue((now, err));
        while (q.Count > 0 && now - q.Peek().At > OuterApproachWindow) q.Dequeue();
        if (!(OuterApproachFreezeCPerMin > 0) || q.Count < 2) return false;
        var (at0, e0) = q.Peek();
        var span = (now - at0).TotalSeconds;
        if (span < Math.Min(20, OuterApproachWindow.TotalSeconds / 3)) return false;
        var rate = (err - e0) / span;
        if (err * rate >= 0) return false;
        var perMin = Math.Abs(rate) * 60;
        if (perMin > OuterApproachFreezeCPerMin) return true;
        // 爬得慢，但照这个速度一会儿就到——也是在趋近。窗里还得真走过「最低速度 × 窗长」那么多：清过历史、窗还没满时
        // 跨度只有 20 多秒，Tr 跳一个字折成的速度就过了最低速度
        return OuterApproachHorizon > TimeSpan.Zero && perMin > OuterApproachMinCPerMin
               && Math.Abs(err - e0) > OuterApproachMinCPerMin * OuterApproachWindow.TotalMinutes
               && Math.Abs(err) / perMin <= OuterApproachHorizon.TotalMinutes;
    }

    /// <summary>【本地改动】釜内这一阵动没动：趋近判据那个时间窗里外环误差最高最低差不超过 <see cref="OuterLearnStillC"/>。
    /// 本拍的误差已经由 ApproachingSetpoint 记进去了。</summary>
    private bool OuterStill(ChannelState s)
    {
        if (!(OuterLearnStillC > 0)) return true;
        double lo = double.MaxValue, hi = double.MinValue;
        foreach (var (_, e) in s.OuterErrHistory)
        {
            lo = Math.Min(lo, e);
            hi = Math.Max(hi, e);
        }
        return hi - lo <= OuterLearnStillC + 1e-9;
    }

    /// <summary>【本地改动】大降温收尾从头来：新一段趋近、或此刻不适用。</summary>
    private static void ResetFinish(ChannelState s, double pv)
    {
        s.FinStartPv = pv;
        s.FinSatSeconds = 0;
        s.FinSatOk = s.FinArmed = s.FinArrived = s.FinEnded = s.FinCrawlNoted = false;
        s.FinGain = s.FinSettledSeconds = s.FinExtraC = 0;
        s.FinLastErr = double.NaN;
        s.FinErrHistory.Clear();
    }

    /// <summary>
    /// 【本地改动】大降温收尾（<see cref="OuterFinishGain"/>）这一拍在外环偏置上多给多少（K）；不适用 / 没武装 = 0。
    /// 只在回路线程、外环那一拍调；本拍误差已经由 ApproachingSetpoint 记进 OuterErrHistory。dt 已封顶（挂起恢复那一拍别一口气跳）。
    /// </summary>
    private double OuterFinish(int ch, ChannelState s, DateTime now, double pv, double err, double dt, bool innerLow, OuterPush? push)
    {
        if (!(OuterFinishGain > 0) || push is not null || s.Profile is not null || s.Table != GainTable.Both
            || OuterBiasClampBandC > 0)      // 落地钳位会把往下多给的那份整个钳掉，两个不一起开
        {
            ResetFinish(s, pv);
            s.FinCarryC = 0;
            return 0;
        }
        var q = s.FinErrHistory;
        q.Enqueue((now, err));
        while (q.Count > 0 && now - q.Peek().At > OuterFinishShotWindow) q.Dequeue();

        // 双向挡 TEC 被扣着（等冷水机最短间隔、问冷却水）时形态是只加热、下限是 0：那不是满制冷，不算、也不清，g 也不往上加
        var tecOn = s.Actuator == ActuatorMode.HeaterTec;
        if (tecOn) s.FinSatSeconds = innerLow ? s.FinSatSeconds + dt : 0;
        if (s.FinSatSeconds >= OuterFinishSaturation.TotalSeconds) s.FinSatOk = true;
        if (!s.FinArmed && s.FinSatOk && Math.Abs(pv - s.FinStartPv) >= FinishMinTravelC && s.SetpointC <= OuterFinishMaxSetpointC)
        {
            s.FinArmed = true;
            ChannelNotice?.Invoke(ch, "info",
                $"通道{ch}大降温收尾已武装（满制冷顶了 {s.FinSatSeconds / 60:F0} 分钟、釜内 {s.FinStartPv:F2} → {pv:F2} ℃）：" +
                $"最后一段爬的时候外环临时多给比例，到站后锁住、稳住后 {OuterFinishRelease.TotalMinutes:0} 分钟退完；这期间不学稳态偏置");
        }
        if (!s.FinArmed)
        {
            s.FinExtraC = 0;
            return 0;
        }

        // 60 s 窗（趋近判据那个）：趋近速度（K/min，往目标走为正；窗不够 2/3 不算）、平均误差
        double? v = null;
        var mean = err;
        var h = s.OuterErrHistory;
        if (h.Count >= 2)
        {
            var (at0, e0) = h.Peek();
            var span = (now - at0).TotalSeconds;
            if (span >= OuterApproachWindow.TotalSeconds * 0.66) v = (Math.Abs(e0) - Math.Abs(err)) / span * 60;
            mean = 0;
            foreach (var (_, e) in h) mean += e;
            mean /= h.Count;
        }

        // 到站：进 ±0.1 或过了零，锁住
        if (!s.FinArrived && (Math.Abs(err) <= FinishArrivedC
                              || (!double.IsNaN(s.FinLastErr) && Math.Abs(s.FinLastErr) > 1e-12 && s.FinLastErr * err <= 0)))
        {
            s.FinArrived = true;
            s.FinArrivedAt = now;
        }
        s.FinLastErr = err;

        var crawl = tecOn && !s.FinEnded && !s.FinArrived && Math.Abs(err) <= FinishBandC && v is { } vv
                    && (vv <= 1e-9 || Math.Abs(err) / vv > OuterFinishCrawlHorizon.TotalMinutes);
        // 防摆：多给着的时候釜内往外走得快——是它带出来的摆，当到站、慢慢退
        var away = s.FinGain > 0 && v < -OuterFinishAwayCPerMin;
        if (away && !s.FinArrived)
        {
            s.FinArrived = true;
            s.FinArrivedAt = now;
        }

        var g = s.FinGain;
        if (away) g -= dt / OuterFinishAwayRelease.TotalSeconds;
        else if (s.FinEnded) g -= dt / OuterFinishRelease.TotalSeconds;
        else if (crawl) g += dt / OuterFinishRamp.TotalSeconds;
        else if (Math.Abs(err) > FinishBandC) g -= dt / OuterFinishRamp.TotalSeconds;
        s.FinGain = Math.Clamp(g, 0, 1);
        if (crawl && !s.FinCrawlNoted)
        {
            s.FinCrawlNoted = true;
            ChannelNotice?.Invoke(ch, "info",
                $"通道{ch}釜内 {pv:F2} ℃ 离目标 {err:+0.00;-0.00} K 在爬：外环比例临时加大（最多 {1 + FinishGainFor(s):0.##} 倍）");
        }

        // 稳住：60 s 平均误差在学习带里待够；到站后锁太久也当稳住
        if (!s.FinEnded)
        {
            s.FinSettledSeconds = Math.Abs(mean) <= OuterLearnBandC ? s.FinSettledSeconds + dt : 0;
            var held = s.FinArrived && now - s.FinArrivedAt >= OuterFinishMaxHold;
            if (s.FinSettledSeconds >= OuterFinishSettleTime.TotalSeconds || held)
            {
                s.FinEnded = true;
                if (s.FinGain > 0)
                    ChannelNotice?.Invoke(ch, "info",
                        $"通道{ch}大降温收尾{(held ? $"到站已 {OuterFinishMaxHold.TotalMinutes:0} 分钟" : "稳住了")}：" +
                        $"多给的比例 {OuterFinishRelease.TotalMinutes:0} 分钟退完，退完后接着学稳态偏置");
            }
        }

        s.FinExtraC = FinishGainFor(s) * s.FinGain * s.OuterPid.LastP;
        return s.FinExtraC;
    }

    /// <summary>【本地改动】大降温收尾此刻的倍数：<see cref="OuterFinishGain"/>，加上以后外环总比例不超过 <see cref="OuterFinishMaxKp"/>。</summary>
    private double FinishGainFor(ChannelState s)
    {
        var kp = s.OuterPid.Kp;
        return kp > 0 ? Math.Min(OuterFinishGain, Math.Max(0, OuterFinishMaxKp / kp - 1)) : 0;
    }

    /// <summary>【本地改动】改目标时带过来的那份这一拍还剩多少（线性退，退完清掉）。</summary>
    private double FinishCarry(ChannelState s, DateTime now)
    {
        if (s.FinCarryC == 0) return 0;
        var left = OuterFinishCarry > TimeSpan.Zero ? 1 - (now - s.FinCarryAt) / OuterFinishCarry : 0;
        if (left > 0) return s.FinCarryC * Math.Min(left, 1);
        s.FinCarryC = 0;
        return 0;
    }

    /// <summary>
    /// 【本地改动】大降温收尾武装时过零那一枪：积分往学到的偏置挪，最多挪 过零速度 × <see cref="OuterFinishShotTau"/>，只往挡住去势的方向挪。
    /// 爬着穿过去的（速度慢）几乎不挪；过零速度的误差历史不够 <see cref="OuterFinishShotMinSpan"/> 就不挪。积分参照照样落到挪完的值上。
    /// </summary>
    private void FinishCrossingShot(int ch, ChannelState s, double steadyI, DateTime now)
    {
        var before = s.OuterPid.LastI;
        var gap = steadyI - before;
        if (Math.Abs(gap) <= FinishShotMinGapC) return;
        double? rate = null;                 // 外环误差（主设定 − 釜内）对时间的斜率，K/min
        var q = s.FinErrHistory;
        if (q.Count >= 2 && (now - q.Peek().At) >= OuterFinishShotMinSpan)
        {
            var t0 = q.Peek().At;
            double n = q.Count, mt = 0, me = 0;
            foreach (var (at, e) in q) { mt += (at - t0).TotalSeconds; me += e; }
            mt /= n;
            me /= n;
            double num = 0, den = 0;
            foreach (var (at, e) in q)
            {
                var dt = (at - t0).TotalSeconds - mt;
                num += dt * (e - me);
                den += dt * dt;
            }
            if (den > 0) rate = num / den * 60;
        }
        var step = 0.0;
        if (rate is { } r)
        {
            var cap = OuterFinishShotTau.TotalSeconds * Math.Abs(r) / 60;
            step = Math.Clamp(gap, -cap, cap);
            if (step * r < 0) step = 0;
        }
        if (step != 0) s.OuterPid.PresetIntegral(before + step);
        OnOuterPresetToLearned(s);
        ChannelNotice?.Invoke(ch, "info",
            $"通道{ch}过零（大降温收尾）：过零速度 {(rate is { } rr ? $"{rr:F3} K/min" : "不明")}，外环积分 {before:F2} → {s.OuterPid.LastI:F2} ℃" +
            $"（学到的 {steadyI:F2}；爬着穿过去的不一枪打回表值）");
    }

    /// <summary>
    /// 【本地改动】升温加速这一拍（<see cref="HeatBoost"/>）：归速度环管就返回夹套目标（还没按内环设定安全限幅夹），不归返回 null（照现在的外环算）。
    /// 进、交还、中止都在这儿判。只在回路线程、外环那一拍、Tr 没丢的时候调；err = 主设定 − 釜内。
    /// </summary>
    private double? HeatBoostStep(int ch, ChannelState s, DateTime now, double tr, double err, OuterPush? push)
    {
        var hist = s.VelTrHistory;
        hist.Enqueue((now, tr));
        var keep = (HeatBoostStallWindow > HeatBoostWindow ? HeatBoostStallWindow : HeatBoostWindow) + OuterPeriod + OuterPeriod;
        while (hist.Count > 0 && now - hist.Peek().At > keep) hist.Dequeue();

        var learned = SteadyBiasFor(s, s.SetpointC);
        var blocked = HeatBoost == HeatBoostLevel.Off ? "升温加速关着"
            : push is not null ? "夹套压着（制冷段）"
            : s.Profile is not null ? "在走温度曲线"
            : s.Table != GainTable.Both ? "不是双向挡"
            : s.Actuator != ActuatorMode.HeaterTec ? "TEC 被扣着（等人答冷却水 / 等冷水机，只加热）"
            : learned is null ? $"{s.SetpointC:F1} ℃ 还没学到稳态偏置（先跑一次稳住了才知道）"
            : !s.HeatBoostAllowed ? "TEC 此刻接不上（冷却水没通 / 等冷水机 / 只加热）"
            : null;

        // TEC 接不上 / 被扣着：加速中先等 HeatBoostDenyGrace 再中止（别的原因当拍中止）
        var denied = blocked is not null && (!s.HeatBoostAllowed || s.Actuator != ActuatorMode.HeaterTec)
                     && HeatBoost != HeatBoostLevel.Off && push is null && s.Profile is null && s.Table == GainTable.Both && learned is not null;
        if (!denied || !s.VelMode) s.VelDeniedSince = null;
        else
        {
            s.VelDeniedSince ??= now;
            if (now - s.VelDeniedSince.Value < HeatBoostDenyGrace) blocked = null;
        }

        if (!s.VelMode)
        {
            ClearVelReadout(s);
            s.VelWantsTec = false;
            if (!s.VelEligible) return null;
            if (err < HeatBoostEnterC)
            {
                s.VelEligible = false;            // 降温、离得太近、或照现在的外环已经升到跟前了：这一段不再进
                return null;
            }
            if (blocked is not null)
            {
                s.VelWantsTec = learned is not null && HeatBoost != HeatBoostLevel.Off && push is null && s.Profile is null
                                && s.Table == GainTable.Both;
                if (HeatBoost != HeatBoostLevel.Off && s.VelBlockedSaid != blocked)
                {
                    s.VelBlockedSaid = blocked;
                    ChannelNotice?.Invoke(ch, "info",
                        $"通道{ch}升温到 {s.SetpointC:F2} ℃（釜内 {tr:F2} ℃）先不加速：{blocked}——照现在的外环升，条件齐了再接");
                }
                return null;
            }
            s.VelMode = true;
            s.VelEligible = false;
            s.VelSince = now;
            s.VelB = learned!.Value;
            s.VelOkSince = null;
            s.VelBiasHistory.Clear();
            s.VelBlockedSaid = null;
            var (tb0, head0) = HeatBoostCurve();
            ChannelNotice?.Invoke(ch, "info",
                $"通道{ch}升温加速：釜内 {tr:F2} → {s.SetpointC:F2} ℃。夹套先顶高（最多 {s.SetpointC + Math.Max(head0, s.OuterPid.OutputMax):F1} ℃" +
                $"{(HeatBoostMaxDeltaC > 0 ? $"、比釜内高不过 {HeatBoostMaxDeltaC:0.#} K" : "")}），按釜内升温速度提前收、TEC 刹车，到站稳住后交回外环；" +
                $"稳态偏置 {s.VelB:+0.00;-0.00} K、刹车曲线 Tb {tb0:0} s（{(HeatBoost == HeatBoostLevel.Conservative ? "保守" : "正常")}档）");
        }
        else if (blocked is not null)
        {
            EndHeatBoost(ch, s, denied ? $"{blocked}已 {HeatBoostDenyGrace.TotalSeconds:0} s" : blocked, rearming: false, err);
            return null;
        }
        else if (tr - s.SetpointC > HeatBoostAbortOverC)
        {
            EndHeatBoost(ch, s, $"釜内 {tr:F2} ℃ 冲过目标 {tr - s.SetpointC:F2} K", rearming: false, err);
            return null;
        }

        var (tb, headroom) = HeatBoostCurve();
        var sp = s.SetpointC;

        // 测速：窗里釜内的最小二乘斜率（K/s）；不到半个窗记 0
        var vm = 0.0;
        {
            var win = HeatBoostWindow + TimeSpan.FromTicks(OuterPeriod.Ticks / 4);
            DateTime? t0 = null;
            double n = 0, mt = 0, my = 0;
            foreach (var (at, v) in hist)
            {
                if (now - at > win) continue;
                t0 ??= at;
                n++;
                mt += (at - t0.Value).TotalSeconds;
                my += v;
            }
            if (t0 is { } first && n >= 2 && (now - first).TotalSeconds >= HeatBoostWindow.TotalSeconds / 2)
            {
                mt /= n;
                my /= n;
                double num = 0, den = 0;
                foreach (var (at, v) in hist)
                {
                    if (now - at > win) continue;
                    var dx = (at - first).TotalSeconds - mt;
                    num += dx * (v - my);
                    den += dx * dx;
                }
                if (den > 0) vm = num / den;
            }
        }

        var vs = HeatBoostVStar(err, tb);
        var kv = vs <= vm ? HeatBoostKv : 0;                 // 只管刹车：升得慢了不加
        var fr = err <= HeatBoostLinearC ? 0
            : HeatBoostFadeC > 0 ? Math.Clamp((err - HeatBoostLinearC) / HeatBoostFadeC, 0, 1) : 1;
        if (vs > 0) fr *= Math.Clamp(vm / vs, 0, 1);
        var raw = tr + s.VelB + (HeatBoostTauHat + HeatBoostWallHat) * vs
                  - HeatBoostWallHat * HeatBoostTauHat * HeatBoostDecel * fr
                  + kv * (vs - vm);
        var hi = sp + Math.Max(headroom, s.OuterPid.OutputMax);
        var lo = sp + s.OuterPid.OutputMin;
        var isp = Math.Max(lo, Math.Min(hi, raw));
        s.VelIspUncapped = isp;
        if (HeatBoostMaxDeltaC > 0) isp = Math.Min(isp, tr + HeatBoostMaxDeltaC);     // 玻璃釜温差最后夹：安全优先
        s.VelVmCPerMin = vm * 60;
        s.VelVstarCPerMin = vs * 60;
        s.VelBrake = fr;

        var kp = s.OuterPid.Kp;
        var bh = s.VelBiasHistory;
        bh.Enqueue((now, isp - sp - kp * err, isp - tr));
        while (bh.Count > 0 && now - bh.Peek().At > HeatBoostHandAverage) bh.Dequeue();

        // 交还：到站稳住 240 s；或卡在带外不动（学到的偏置偏低得多，速度环没积分补不上）
        if (Math.Abs(err) <= HeatBoostHandBandC && Math.Abs(vm) * 60 <= HeatBoostHandStillCPerMin) s.VelOkSince ??= now;
        else s.VelOkSince = null;
        var settled = s.VelOkSince is { } okAt && now - okAt >= HeatBoostHandHold;
        var stalled = false;
        if (Math.Abs(err) <= HeatBoostStallBandC && now - s.VelSince >= HeatBoostStallWindow)
        {
            double tlo = double.MaxValue, thi = double.MinValue;
            var far = false;
            foreach (var (at, v) in hist)
            {
                if (now - at > HeatBoostStallWindow) continue;
                if (Math.Abs(sp - v) > HeatBoostStallBandC) far = true;
                tlo = Math.Min(tlo, v);
                thi = Math.Max(thi, v);
            }
            stalled = !far && thi - tlo <= HeatBoostStallC + 1e-9;
        }
        if (!settled && !stalled) return isp;

        double handI;
        string how;
        if (stalled)
        {
            var over = 0.0;
            foreach (var (_, _, o) in bh) over += o;
            handI = Math.Clamp(over / bh.Count - kp * err, s.OuterPid.OutputMin, s.OuterPid.OutputMax);
            how = $"离目标 {err:+0.00;-0.00} K 卡了 {HeatBoostStallWindow.TotalMinutes:0} 分钟不动（学到的偏置 {s.VelB:+0.00;-0.00} 多半偏了）：" +
                  $"积分按这几分钟实际的「夹套 − 釜内」接";
        }
        else
        {
            var pi = 0.0;
            foreach (var (_, p, _) in bh) pi += p;
            handI = Math.Clamp(pi / bh.Count, s.OuterPid.OutputMin, s.OuterPid.OutputMax);
            handI = Math.Clamp(handI, s.VelB - HeatBoostHandBiasC, s.VelB + HeatBoostHandBiasC);
            how = $"釜内 {tr:F2} ℃ 稳在目标 ±{HeatBoostHandBandC:0.##} K 里 {HeatBoostHandHold.TotalMinutes:0} 分钟：积分无扰接上";
        }
        var before = s.OuterPid.LastI;
        HeatBoostHandOver(s, handI);
        ChannelNotice?.Invoke(ch, "info",
            $"通道{ch}升温加速交回外环（加速了 {(now - s.VelSince).TotalMinutes:F1} 分钟）：{how}，外环积分 {before:F2} → {s.OuterPid.LastI:F2} ℃；两枪放电撤防");
        return null;
    }

    /// <summary>
    /// 【本地改动】外环偏置自校正（<see cref="OuterBiasCheckWindow"/>）：记一笔、窗口攒满且釜内一直在目标附近转就按热平衡估偏置，
    /// 跟积分差得多就改积分、这一场这个目标的偏置改成实测。只在现在的外环那一拍（不在升温加速里）、Tr 没丢时调。
    /// </summary>
    private void BiasSelfCheck(int ch, ChannelState s, DateTime now, double tr, double tj, double err, OuterPush? push)
    {
        var w = OuterBiasCheckWindow;
        var q = s.BiasWin;
        var wall = OuterBiasCheckWallHat > 0 ? s.BiasWallHat : tj;
        if (!(w > TimeSpan.Zero) || s.Profile is not null || s.FinArmed && (!s.FinEnded || s.FinGain > 0)
            || !double.IsFinite(tr) || !double.IsFinite(wall) || !double.IsFinite(err))
        {
            BiasWinReset(s);
            return;
        }
        if (OuterBiasCheckAfterCross && !s.BiasCrossed)
        {
            // 过零以前不记：趋近尾巴上釜内还在减速，热平衡里那一项（τ·τw·釜内的加速度）没算，估出来偏低
            if (double.IsFinite(s.BiasLastErr) && Math.Abs(s.BiasLastErr) > 1e-12 && s.BiasLastErr * err <= 0) s.BiasCrossed = true;
            s.BiasLastErr = err;
            if (!s.BiasCrossed) return;
        }
        q.Enqueue((now, tr, wall));
        while (q.Count > 0 && now - q.Peek().At > w) q.Dequeue();
        if (push is not null || q.Count < 2) return;                       // 压低期间照记、不判
        var first = q.Peek();
        if (now - first.At < w - OuterPeriod - OuterPeriod) return;        // 窗口还没攒满
        if (s.LastBiasFix != default && now - s.LastBiasFix < w) return;   // 两次之间至少隔一个窗口
        var sp = s.SetpointC;
        double sumTr = 0, sumD = 0, maxDev = 0;
        var lastTr = first.TrC;
        foreach (var (_, r, j) in q)
        {
            sumTr += r;
            sumD += j - r;
            maxDev = Math.Max(maxDev, Math.Abs(r - sp));
            lastTr = r;
        }
        var n = q.Count;
        var meanTr = sumTr / n;
        if (maxDev > OuterBiasCheckBandC || Math.Abs(meanTr - sp) > OuterBiasCheckMeanC) return;
        var span = (now - first.At).TotalSeconds;
        var bhat = sumD / n - OuterBiasCheckTauHat * (lastTr - first.TrC) / span;
        var before = s.OuterPid.LastI;
        // 这个目标上冲过目标被压低过、整个窗口都在解除以后：估出来比积分低就按小门槛改（只往下——冲过头说明给多了）
        var refix = OuterBiasCheckRefixGapC > 0 && s.RefixFrom > s.LastBiasFix && first.At >= s.RefixFrom
                    && now >= s.BiasHoldUntil && before - bhat > OuterBiasCheckRefixGapC;
        if (!refix && Math.Abs(bhat - before) <= OuterBiasCheckGapC) return;
        var (table, from) = TableBiasSource(s, sp);
        // 附近目标的平移按改之前的表算（这一场第一次改的那一刻；后面再改不重拍——表那时候可能已经学过了）
        s.BiasCarryTables ??= new BiasTables(s.GainSchedule.Clone(), s.HeaterSchedule.Clone(), s.BothSchedule.Clone());
        s.OuterPid.PresetIntegral(bhat);
        s.OuterIntegralRef = s.OuterPid.LastI;
        s.OuterIntegralPristine = false;
        s.OuterBrakeC = 0;
        s.OuterBiasKnown = true;
        s.BiasOverrideC = s.OuterPid.LastI;
        s.BiasOverrideSp = sp;
        s.BiasMeasured = [(sp, s.OuterPid.LastI)];     // 之前别处实测的可能是釜变了以前的：只留这一个
        s.LastSteadyBias = s.OuterPid.LastI;     // 学稳态偏置从实测这个值起低通，别从改之前那个错的起
        s.OuterSteadyCycles = 0;
        s.LastBiasFix = now;
        s.BiasHoldUntil = now + OuterBiasCheckHold;
        // 说的是数真从哪张表来的：TEC / 加热棒按离得近的那张取、双向挡这个温度没有的借另两张（不看此刻是哪个挡）
        var tableName = ReferenceEquals(from, s.BothSchedule) ? "双向挡表" : ReferenceEquals(from, s.HeaterSchedule) ? "加热棒表" : "TEC 表";
        var borrowed = s.Table == GainTable.Both && from is not null && !ReferenceEquals(from, s.BothSchedule);
        // 表偏没偏按 b̂ 的误差说（表对时 b̂ 最多差 0.35 K），不按改不改的门槛：积分攒过了头才改的那次，表差 0.8 K 也不能说「对得上、不用改」
        var tableDiff = table is { } t0 ? Math.Abs(t0 - bhat) : 0;
        var tableOff = tableDiff > OuterBiasTableOffC;
        ChannelNotice?.Invoke(ch, "warn",
            $"通道{ch}外环偏置自校正：" + (refix ? "冲过目标被压低过（偏置给多了），" : "") +
            $"釜内 {span / 60:0} 分钟一直在目标 {sp:F2} ℃ ±{OuterBiasCheckBandC:0.0#} K 里转（平均 {meanTr:F2} ℃）没稳住，" +
            $"实测维持目标要的「夹套 − 釜内」≈ {bhat:+0.00;-0.00} K，外环积分 {before:F2} → {s.OuterPid.LastI:F2} ℃，先拿住 {OuterBiasCheckHold.TotalMinutes:0} 分钟" +
            (table is not { } t ? "；表里这个温度还没有稳态偏置，稳住以后学进表"
                : tableOff ? (tableDiff > OuterBiasCheckGapC ? $"；表里这个温度的稳态偏置 {t:+0.00;-0.00} K 偏了"
                                 : $"；表里这个温度的稳态偏置 {t:+0.00;-0.00} K 跟实测差 {tableDiff:0.00} K") +
                             "，这一场按实测用（两枪放电也按它），稳住以后照常学进表" +
                             (borrowed
                                 ? $"——双向挡表这个温度还没有，这个数是借「{tableName}」的；要是经常偏，PID 页「双向挡表」这个温度加一行"
                                 : $"——表里这个值要是经常偏，PID 页「{tableName}」改一下")
                : $"；表里这个温度的稳态偏置 {t:+0.00;-0.00} K 跟实测对得上，是积分自己跑偏了（多半中途有扰动），表不用改"));
    }

    /// <summary>【本地改动】自校正提示里「表跟实测差了」的门槛（K）：表对时 b̂ 跟真值最多差 0.35 K（Python 孪生）。差过它、不到再改一次的门槛的
    /// 只说「跟实测差多少」，过了才说「偏了」；差不过它才说「对得上、积分自己跑偏」（孪生：表偏高 0.5、b̂ 3.41，按 0.5 算会说对得上）。</summary>
    private const double OuterBiasTableOffC = 0.35;

    /// <summary>【本地改动】偏置自校正窗口清空、过零重新等（改目标 / Tr 丢失恢复 / 曲线 / 加速 / 大降温收尾 / 开控）。压低和解除压低不清。</summary>
    private static void BiasWinReset(ChannelState s)
    {
        s.BiasWin.Clear();
        s.BiasCrossed = false;
        s.BiasLastErr = double.NaN;
    }

    /// <summary>【本地改动】偏置自校正的釜壁估计：夹套一阶滞后 <see cref="OuterBiasCheckWallHat"/>，每拍跟（Tr 丢失保持期也跟），开控时从夹套起。</summary>
    private void BiasWallTrack(ChannelState s, DateTime now, double tj)
    {
        if (!double.IsFinite(tj)) return;
        var tau = OuterBiasCheckWallHat;
        if (!(tau > 0) || !double.IsFinite(s.BiasWallHat) || s.BiasWallAt == default)
        {
            s.BiasWallHat = tj;
            s.BiasWallAt = now;
            return;
        }
        var dt = (now - s.BiasWallAt).TotalSeconds;
        if (dt <= 0) return;
        s.BiasWallHat += (tj - s.BiasWallHat) * Math.Min(1, dt / tau);
        s.BiasWallAt = now;
    }

    /// <summary>【本地改动】升温加速刹车曲线：离目标 e（K）时允许的升温速度（K/s）。e ≤ e1 线性 e / Tb；再远恒减速 a。</summary>
    private double HeatBoostVStar(double e, double tb)
    {
        if (e <= HeatBoostLinearC || !(HeatBoostDecel > 0)) return e / tb;
        var v1 = HeatBoostLinearC / tb;
        return Math.Sqrt(v1 * v1 + 2 * HeatBoostDecel * (e - HeatBoostLinearC));
    }

    /// <summary>【本地改动】升温加速中途退出（不是到站交还）：积分回学到的偏置（rearming = 改目标 / 重新武装时取新目标学到的，两枪照新目标武装着），
    /// 不是重新武装的两枪撤防（这一段的趋近已经被速度环走过了）。
    /// 刹车中途退（err 给了）：现在的外环拿 B 算出的夹套目标比速度环此刻的（温差限之前的）高——那就是把刹车撤了，壁里存的热会把釜推过头
    /// （评审孪生：−2 → 20 在 11–17 分钟退，冲过 0.07 → 0.11 K）。这时积分接成「跟速度环此刻的夹套目标一样」，最多比 B 低
    /// <see cref="HeatBoostAbortBiasFloorC"/>，1 K 那一枪重新武装（进 1 K 放回 B，不然停在目标前面爬）；还在往上推照旧 B、两枪撤防。</summary>
    private void EndHeatBoost(int ch, ChannelState s, string why, bool rearming, double err = double.NaN)
    {
        var before = s.OuterPid.LastI;
        var learned = rearming ? SteadyBiasFor(s, s.SetpointC) : null;
        var i = learned ?? s.VelB;
        var braking = false;
        if (!rearming && double.IsFinite(err) && double.IsFinite(s.InnerSetpointC) && double.IsFinite(s.VelIspUncapped))
        {
            var kp = s.OuterPid.Kp;
            var withB = s.SetpointC + Math.Clamp(kp * err + i, s.OuterPid.OutputMin, s.OuterPid.OutputMax);
            if (withB > s.VelIspUncapped + 1e-9)
            {
                braking = true;
                i = Math.Max(i - HeatBoostAbortBiasFloorC, Math.Min(i, s.InnerSetpointC - s.SetpointC - kp * err));
            }
        }
        var armA = s.OuterApproachDischargeArmed;
        var armX = s.OuterCrossingArmed;
        HeatBoostHandOver(s, i);
        if (rearming)
        {
            s.OuterApproachDischargeArmed = armA;
            s.OuterCrossingArmed = armX;
            s.OuterBiasKnown = learned is not null;
        }
        else if (braking)
            s.OuterApproachDischargeArmed = true;     // 压低的积分只管眼下这一刹：进 1 K 那一枪把它放回学到的偏置，不然停在目标前面爬几十分钟
        ChannelNotice?.Invoke(ch, rearming ? "info" : "warn",
            $"通道{ch}升温加速退出：{why}——交回现在的外环，积分 {before:F2} → {s.OuterPid.LastI:F2} ℃" +
            $"{(braking ? $"（正在刹车：接住速度环此刻的夹套目标 {s.InnerSetpointC:F2} ℃，不撤刹车）" : "")}" +
            $"{(rearming ? "" : "、两枪放电撤防")}");
    }

    /// <summary>【本地改动】速度环 → 现在的外环：积分接到 i，参照落到它、刹车不再单算，两枪撤防，速度环状态清掉。</summary>
    private static void HeatBoostHandOver(ChannelState s, double i)
    {
        s.VelMode = false;
        s.VelEligible = false;
        s.VelOkSince = null;
        s.VelWantsTec = false;
        s.VelDeniedSince = null;
        s.VelIspUncapped = double.NaN;
        ClearVelReadout(s);
        s.OuterPid.PresetIntegral(i);
        s.OuterPid.ResetDerivative();
        s.OuterIntegralRef = s.OuterPid.LastI;
        s.OuterIntegralPristine = false;
        s.OuterBrakeC = 0;
        s.OuterBiasKnown = true;
        s.OuterApproachDischargeArmed = false;
        s.OuterCrossingArmed = false;
    }

    private static void ClearVelReadout(ChannelState s)
    {
        s.VelVmCPerMin = s.VelVstarCPerMin = s.VelBrake = double.NaN;
    }

    /// <summary>【本地改动】每拍记一笔 (釜内 / 外环被控量, 夹套 / 内环被控量)，控没控都记——起步估偏置要看起步「前」稳不稳。</summary>
    private void RecordHistory(ChannelState s, TecSnapshot snapshot, double pv)
    {
        if (OuterStartQuietWindow <= TimeSpan.Zero) return;
        double outer;
        try { outer = s.OuterSource is { } src ? src() : s.OuterFeedbackSensor == 1 ? snapshot.Temp1C : snapshot.Temp2C; }
        catch { outer = double.NaN; }
        var keep = OuterStartQuietWindow + TimeSpan.FromSeconds(10);
        lock (s.History)
        {
            s.History.Enqueue((snapshot.Timestamp, outer, pv));
            while (s.History.Count > 0 && snapshot.Timestamp - s.History.Peek().At > keep) s.History.Dequeue();
        }
    }

    // ---------- 主循环 ----------

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var cycleStart = Environment.TickCount64;
            try
            {
                await RunOneCycleAsync(ct).ConfigureAwait(false);
                _consecutiveFailures = 0;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                CycleFaulted?.Invoke(ex);
                if (++_consecutiveFailures >= MaxConsecutiveFailures)
                {
                    _consecutiveFailures = 0;
                    await TripSafetyAsync($"连续 {MaxConsecutiveFailures} 个周期通信失败：{ex.Message}").ConfigureAwait(false);
                }
            }

            var elapsed = Environment.TickCount64 - cycleStart;
            var remaining = (int)(Period.TotalMilliseconds - elapsed);
            if (remaining > 0)
            {
                try { await Task.Delay(remaining, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task RunOneCycleAsync(CancellationToken ct)
    {
        var workStart = Environment.TickCount64;
        var snapshot = await controller.ReadSnapshotAsync(ct).ConfigureAwait(false);

        var infos = new ChannelCycleInfo[2];
        for (var ch = 1; ch <= 2; ch++)
        {
            var s = State(ch);
            var pv = Pv(s, ch, snapshot);
            RecordHistory(s, snapshot, pv);          // 【本地改动】

            if (s.Suspended && s.Tuner is { } held
                && DateTime.Now - (s.SuspendedSince ?? DateTime.Now) > TuningSuspendLimit)
            {
                // 【本地改动】整定中输出挂着太久（双向挡整定切热源卡住了 / 切换失败没人放开）：判失败收尾——
                // 挂着的回路不算整定器那一拍，不收尾的话整定器空等到 4 h 超时，界面一直写着「整定中」
                await FinishTuningAsync(ch, s, success: false,
                    reason: $"整定中切热源后输出 {TuningSuspendLimit.TotalSeconds:0} s 都没重新打开（切换卡住了 / 失败了），整定停掉").ConfigureAwait(false);
                infos[ch - 1] = new ChannelCycleInfo(false, false, held.CompletedCycles, s.SetpointC, pv, 0, 0, 0, 0);
            }
            else if (s.Suspended)
            {
                // 【本地改动】热源切换那两秒：不算不写，也不重写手动输出（输出本来就关着）。
                // 在整定的话温度照样记给整定器（极值别漏，刚换向那一下往往就是峰）
                s.Tuner?.Observe(snapshot.Timestamp, pv);
                infos[ch - 1] = new ChannelCycleInfo(s.Active, s.Tuner is not null, s.Tuner?.CompletedCycles ?? 0,
                    s.SetpointC, pv, 0, 0, 0, 0)
                {
                    Cascade = s.Strategy == ControlStrategy.Cascade, InnerSetpointC = s.InnerSetpointC,
                    OuterHoldSeconds = s.OuterLostSince is { } lost ? (snapshot.Timestamp - lost).TotalSeconds : null
                };
            }
            else if (s.Tuner is not null)
            {
                infos[ch - 1] = await RunTuningStepAsync(ch, s, snapshot, pv, ct).ConfigureAwait(false);
            }
            else if (s.Active)
            {
                infos[ch - 1] = await RunControlStepAsync(ch, s, snapshot, pv, ct).ConfigureAwait(false);
            }
            else
            {
                if (s.ManualDutyPercent is double manual)
                {
                    // 手动模式周期重写，防止固件看门狗撤销 MODE=3 输出（实测约 1 分钟失效）
                    await WriteDutyAsync(ch, manual, ct).ConfigureAwait(false);
                    s.LastDutyPercent = manual;
                }
                infos[ch - 1] = new ChannelCycleInfo(false, false, 0, s.SetpointC, pv, s.LastDutyPercent, 0, 0, 0);
            }
        }

        TecErrorCode? error = null;
        double? current1 = null, current2 = null;
        if (_cycleCount++ % 4 == 0)
        {
            error = await controller.ReadErrorCodeAsync(ct).ConfigureAwait(false);
            current1 = await controller.ReadCurrentAsync(1, ct).ConfigureAwait(false);
            current2 = await controller.ReadCurrentAsync(2, ct).ConfigureAwait(false);
        }

        var elapsedMs = Environment.TickCount64 - workStart;
        CycleCompleted?.Invoke(new ControlCycleResult(snapshot, infos[0], infos[1], error, current1, current2)
        {
            CycleMs = elapsedMs,
            CycleLoad = Period.TotalMilliseconds > 0 ? elapsedMs / Period.TotalMilliseconds : 0,
        });
    }

    private async Task<ChannelCycleInfo> RunControlStepAsync(
        int ch, ChannelState s, TecSnapshot snapshot, double pv, CancellationToken ct)
    {
        if (double.IsNaN(pv))
        {
            await TripChannelAsync(ch, $"通道{ch}温度读数丢失（传感器未接或断线）").ConfigureAwait(false);
            return new ChannelCycleInfo(false, false, 0, s.SetpointC, pv, 0, 0, 0, 0);
        }

        var dt = s.LastUpdateTime == default
            ? Period.TotalSeconds
            : (snapshot.Timestamp - s.LastUpdateTime).TotalSeconds;
        s.LastUpdateTime = snapshot.Timestamp;

        // 【本地改动】串级外环的外部被控量（釜内 Tr）读不到：不再当拍停控，先「保持」再说（OuterLossGrace）。
        // 保持 = 外环冻结（不算、不放电、不学偏置、不扩程）、曲线暂停、内环设定钳到主设定值——夹套停在釜内设定上，
        // 传热推不过设定，盲区里不会把釜推过头；内环照跑。Tr 在宽限内回来就无扰接着串级：积分沿用（它是维持
        // 这个温度的偏置，没过时）、微分历史重置（别跨整个盲区算斜率）、外环 dt 从下一拍重计、曲线从暂停处续。
        // 宽限过了还没回来才停控。本机 TC 当外环（OuterSource 为 null）读 NaN 是探头没接，不是暂态，照旧当拍停控。
        // 现场：釜内 50 ℃ 串级升温到 3.2 min 探头样本断了 10 s，回路当拍停控、加热棒关了，面板上只剩开关自己灭了
        var cascade = s.Strategy == ControlStrategy.Cascade;
        var outerPv = double.NaN;
        var holding = false;
        if (cascade)
        {
            // 外环被控量可以来自外部（双工位主机：宇电采的釜内 Tr）
            outerPv = s.OuterSource is { } src ? src() : s.OuterFeedbackSensor == 1 ? snapshot.Temp1C : snapshot.Temp2C;
            if (double.IsNaN(outerPv))
            {
                if (s.OuterSource is null || OuterLossGrace <= TimeSpan.Zero)
                {
                    await TripChannelAsync(ch, s.OuterSource is not null
                        ? $"通道{ch}串级外环的釜内 Tr 读数丢失（宇电探头断线 / 探头会话没出数），已停控"
                        : $"通道{ch}串级外环传感器（TC{s.OuterFeedbackSensor}）读数丢失，已停控").ConfigureAwait(false);
                    return new ChannelCycleInfo(false, false, 0, s.SetpointC, pv, 0, 0, 0, 0);
                }
                var held = Math.Clamp(s.SetpointC, InnerSetpointMinC, InnerSetpointMaxC);
                if (s.OuterLostSince is null)
                {
                    s.OuterLostSince = snapshot.Timestamp;
                    s.ProfileHoldAt ??= snapshot.Timestamp;
                    s.OuterSteadyCycles = 0;
                    if (s.VelMode) EndHeatBoost(ch, s, "釜内 Tr 读数丢失", rearming: false, s.LastOuterErrorC);     // 【本地改动】测不了速度就不按速度刹（刹车中的接住刹车）
                    s.VelEligible = false;
                    s.VelWantsTec = false;                // 这一段不会再加速了：别让会话接着备水
                    ChannelNotice?.Invoke(ch, "warn",
                        $"通道{ch}串级外环的釜内 Tr 读数丢失：外环冻结，夹套设定从 {s.InnerSetpointC:F1} ℃ 收到 {held:F1} ℃" +
                        $"（不高于、不低于釜内设定）；{OuterLossGrace.TotalSeconds:0} s 内 Tr 回来就接着串级，回不来就停控");
                }
                if (snapshot.Timestamp - s.OuterLostSince.Value >= OuterLossGrace)
                {
                    await TripChannelAsync(ch,
                        $"通道{ch}串级外环的釜内 Tr 读数丢失超过 {OuterLossGrace.TotalSeconds:0} s（宇电探头断线 / 探头会话没出数）——" +
                        $"这段里夹套已按釜内设定 {s.SetpointC:F1} ℃ 保持，仍无读数，已停控。修好探头后重新下发目标，或改控夹套").ConfigureAwait(false);
                    return new ChannelCycleInfo(false, false, 0, s.SetpointC, pv, 0, 0, 0, 0);
                }
                s.InnerSetpointC = held;
                holding = true;
            }
            else if (s.OuterLostSince is { } since)
            {
                var lostFor = (snapshot.Timestamp - since).TotalSeconds;
                s.OuterLostSince = null;
                s.OuterPid.ResetDerivative();
                s.OuterErrHistory.Clear();                // 【本地改动】趋近判据别跨盲区算误差变化
                s.FinErrHistory.Clear();
                s.VelTrHistory.Clear();                   // 【本地改动】升温加速测速也别跨盲区
                BiasWinReset(s);                            // 【本地改动】偏置自校正也别跨盲区
                // 【本地改动】大降温收尾还没到站：多给的那份从 0 重新爬，夹套设定别从「钳在主设定」一步跳回去。到站以后不清——
                // 那时 |误差| ≤ 0.1、多给的不到 0.25 K，没什么可跳的；g 还管着「退完之前不学偏置」，清了就提前学进漏热那一截
                if (!s.FinArrived) s.FinGain = 0;
                s.LastOuterUpdate = default;              // 下一拍外环 dt = OuterPeriod，别把断掉的几十秒一口气积进去
                s.BiasGuard.Reset();
                var resumed = "";
                if (s.ProfileHoldAt is { } at)
                {
                    s.ProfileStart += snapshot.Timestamp - at;
                    s.ProfileHoldAt = null;
                    if (s.Profile is not null) resumed = "、曲线从暂停处续";
                }
                ChannelNotice?.Invoke(ch, "info",
                    $"通道{ch}釜内 Tr 已恢复（丢失 {lostFor:F0} s，Tr {outerPv:F2} ℃）：串级外环接着算，积分沿用 {s.OuterPid.LastI:F2} ℃、微分重置{resumed}");
            }
        }

        // ① 设定值生成层：时间驱动曲线（结晶降温等）。【本地改动】Tr 丢失保持期曲线暂停
        double? profileProgress = null;
        var profileName = s.Profile?.Name;
        if (s.Profile is { } profile && s.ProfileHoldAt is null)
        {
            var elapsed = (snapshot.Timestamp - s.ProfileStart).TotalMinutes;
            var total = profile.TotalMinutes(s.ProfileStartC);
            if (profile.SetpointAt(elapsed, s.ProfileStartC) is { } sp)
            {
                s.SetpointC = sp;
                profileProgress = total > 0 ? Math.Clamp(elapsed / total, 0, 1) : 1;
            }
            else
            {
                s.Profile = null;
                s.ProfileFinished = true;            // 【本地改动】坡尾回基认这个，不认外面 StopProfile
                profileProgress = 1;
                ProfileCompleted?.Invoke(ch, profile.Name);
            }
        }

        if (cascade) BiasWallTrack(s, snapshot.Timestamp, pv);     // 【本地改动】偏置自校正的釜壁估计：Tr 丢失保持期也跟着夹套走

        // ② 闭环控制层：串级时先跑外环，算出内环设定值（【本地改动】Tr 丢失保持期整段跳过，内环设定已钳在主设定）
        if (cascade && !holding)
        {
            var outerDt = s.LastOuterUpdate == default
                ? OuterPeriod.TotalSeconds
                : (snapshot.Timestamp - s.LastOuterUpdate).TotalSeconds;
            if (outerDt >= OuterPeriod.TotalSeconds)
            {
                s.LastOuterUpdate = snapshot.Timestamp;

                // 外环增益调度：按主设定值插值出该温区的外环参数与偏置限幅
                ApplyOuterTuning(s, s.SetpointC);

                // 内环饱和或钳位正在约束偏置时冻结外环积分——两者同理：
                // 输出执行不了/被界住时继续积分只会累积过零后要还的债。
                // 判据对上下限各查一侧：只加热模式贴着 0 下限（要降温却无制冷）同样是饱和。
                // 【本地改动】只冻会把内环推得更深的方向：内环顶在上限还要往上、贴在下限还要往下才冻；积分往回走（退债）
                // 不冻——只加热执行器贴着 0 时 Tr 已过设定，外环正向欠账要能退掉，不然夹套设定一直比该有的高、Tr 停在设定
                // 之上（原来两个方向都冻）。LastDutyPercent 存的是 PID 那一侧的输出，跟 PID 限幅比才对得上
                var innerHigh = s.LastDutyPercent >= s.Pid.OutputMax - 1e-6;
                var innerLow = s.LastDutyPercent <= s.Pid.OutputMin + 1e-6;
                var innerSaturated = innerHigh || innerLow;
                // 【本地改动】压没压着这一拍先拍下来、整拍按它（下面趋近 / 过零放电、冻积分、自校正都用这一份），再收标志：
                // 会话线程解除压低是先立解除标志、再清 Push，这儿看到没压着、下面收标志就一定收得到——不会这一拍按没压着打两枪、
                // 解除那个标志却留到下一拍才认（那一拍「解除压低不打两枪」就漏了）
                var push = Volatile.Read(ref s.Push);
                // 【本地改动】会话线程立的标志先收走、在这儿（回路线程）落实，再按（可能刚换的）目标算误差
                if (Interlocked.Exchange(ref s.RampCancelledFlag, 0) != 0)
                {
                    s.RampBaseI = null;
                    s.ProfileFinished = false;
                    s.VelTrHistory.Clear();
                    s.VelBiasHistory.Clear();
                    BiasWinReset(s);
                    s.ShotsFromPushRelease = false;          // 开控：新的一段趋近
                    s.ReleaseGuardUntil = default;
                    s.RefixFrom = default;
                }
                var rearmed = Interlocked.Exchange(ref s.OuterRearmedFlag, 0) != 0;
                var targetChanged = Interlocked.Exchange(ref s.OuterTargetChangedFlag, 0) != 0;
                // 解除压低那个标志每拍都收：跟改目标同一拍到的（会话下新目标时先松压低、紧跟着改目标）算改目标，留到下一拍就被认成解除压低了
                var releasedInPlace = Interlocked.Exchange(ref s.OuterRearmInPlaceFlag, 0) != 0;
                // 【本地改动】这一回重新武装是哪一种：改目标是新的一段趋近，两枪照打；只有解除压低那种在自校正管着的目标上不打。
                // 各看各的标志（解除压低那个 SetSetpoint 不碰），光有重新武装不改判断——SetSetpoint 后一下立的重新武装可能落到下一拍
                if (targetChanged)
                {
                    s.ShotsFromPushRelease = false;
                    s.ReleaseGuardUntil = default;
                    s.RefixFrom = default;
                }
                else if (releasedInPlace)
                {
                    s.ShotsFromPushRelease = true;
                    // 管着的目标上：拿住期过了两枪就不打，压低拽下去那一截别攒进积分（ReleaseGuardUntil）。拿住期里松开的从拿住期满起算——
                    // 不然拿住期满正赶上釜内被压在目标下面，两枪不打、积分又没人拦，一攒又冲（孪生：改完 7 分钟压一次，冲 0.13、又压两回）
                    // 管不着的目标上（两枪照打）也立：两枪在松开那会儿多半空打掉（釜内在目标上面、跟表差不到门槛），压低拽下去那一截
                    // 照样攒进积分、回头冲过又压，每 12 分钟一回一直摆（孪生：表对的、42 → 43 之后每 12 分钟压一回；42 稳住后扰动 8 分钟，100 分钟里压 5 回）
                    s.ReleaseGuardUntil = OverrideCovers(s, s.SetpointC)
                        ? (snapshot.Timestamp >= s.BiasHoldUntil ? snapshot.Timestamp : s.BiasHoldUntil) + OuterBiasCheckHold
                        : snapshot.Timestamp + OuterBiasCheckHold;
                    s.ReleaseGuardBelow = false;
                    // 这个目标上（上次改以后）第一次冲过头被压低：从这儿起整个窗口估出来的比积分低就按小门槛改（BiasSelfCheck）。
                    // 上一次解除隔了两个窗口以上：不是一串来回压里的，从这一次重新起算——窗口整个在这次解除以后，压低以前的扰动不算进去
                    // （孪生：早先压过一次没改，50 分钟后 6 分钟扰动压一次，松开那一拍就按小门槛改成 2.88、真值 3.32，多沉 13 分钟）
                    if (s.RefixFrom <= s.LastBiasFix || snapshot.Timestamp - s.LastReleaseAt > OuterBiasCheckWindow + OuterBiasCheckWindow)
                        s.RefixFrom = snapshot.Timestamp;
                    s.LastReleaseAt = snapshot.Timestamp;
                }
                if (rearmed)
                {
                    s.OuterErrHistory.Clear();
                    // 【本地改动】升温加速：新一段趋近可以再进速度环；正在速度环里改了目标 / 重新武装——先交回现在的控法，下面按新目标重判
                    if (s.VelMode)
                        EndHeatBoost(ch, s, targetChanged ? $"改了目标 {s.SetpointC:F2} ℃" : "外环重新武装", rearming: true);
                    s.VelEligible = true;
                    s.VelBlockedSaid = null;
                    // 【本地改动】大降温收尾多给着的时候直接改目标、新目标就在釜内附近：多给的那份带过去慢慢退（OuterFinishCarry）。
                    // 起步 / 解除压低（只立了新一段趋近）、或改得离釜内远（新的一段趋近，带着反而拖慢）：清掉，连上一回近处改目标还在退的那份一起；
                    // 近处改目标且这一拍还多给着：从它起退；近处改目标但没在多给（上一回带过来的还在退）：接着退
                    var lastExtra = s.FinExtraC;
                    var nearTarget = Math.Abs(s.SetpointC - outerPv) <= OuterFinishCarryNearC;
                    if (!targetChanged || !nearTarget || OuterFinishCarry <= TimeSpan.Zero) s.FinCarryC = 0;
                    else if (lastExtra != 0)
                    {
                        s.FinCarryC = lastExtra;
                        s.FinCarryAt = snapshot.Timestamp;
                        ChannelNotice?.Invoke(ch, "info",
                            $"通道{ch}大降温收尾中改目标 {s.SetpointC:F2} ℃（釜内 {outerPv:F2} ℃）：多给的 {lastExtra:+0.00;-0.00} K 不一下撤掉，" +
                            $"{OuterFinishCarry.TotalMinutes:0} 分钟线性退完");
                    }
                    ResetFinish(s, outerPv);
                }
                if (targetChanged)
                {
                    BiasWinReset(s);                         // 【本地改动】偏置自校正按新目标从头攒（实测的偏置留着：附近的目标按差值平移）
                    // 改到附近（实测值还管着）：拿住接着拿——改完那一下的过渡还没走完，积分照常攒又攒偏（孪生：40 改完改 40.3，放开拿住又冲 0.17、压低两次）
                    if (!OverrideCovers(s, s.SetpointC)) s.BiasHoldUntil = default;
                    s.LastSteadyBias = null;                 // 【本地改动】学稳态偏置的低通也从头来：旧目标的偏置别掺进新目标那行
                    // 坡中途直接给了新目标（StopProfile + SetSetpoint）：坡上攒的那份先放掉，按釜内在新目标哪一边定方向
                    if (s.RampBaseI is not null && s.Profile is null)
                    {
                        s.ProfileFinished = false;
                        var side = outerPv - s.SetpointC;
                        DischargeRamp(ch, s, Math.Abs(side) >= 0.05 ? Math.Sign(side) : Math.Sign(s.RampStartSpC - s.SetpointC),
                            $"斜坡中途改目标 {s.SetpointC:F2} ℃");
                    }
                    // 偏置没摸清、积分还停在起步预置上（刹车完整在里面）：按新方向重摆刹车
                    if (s.OuterIntegralPristine && !s.OuterBiasKnown && s.Push is null && SteadyBiasFor(s, s.SetpointC) is null)
                    {
                        var brake = BrakeToward(s, outerPv, s.SetpointC);
                        if (Math.Abs(brake - s.OuterBrakeC) > 1e-9)
                        {
                            var carried = s.OuterPid.LastI;
                            s.OuterPid.PresetIntegral(carried - s.OuterBrakeC + brake);
                            ChannelNotice?.Invoke(ch, "info",
                                $"通道{ch}新目标 {s.SetpointC:F2} ℃（釜内 {outerPv:F2} ℃）、偏置还没摸清：外环刹车 {s.OuterBrakeC:+0.0;-0.0;0} → {brake:+0.0;-0.0;0} K，" +
                                $"积分 {carried:F2} → {s.OuterPid.LastI:F2} ℃");
                            s.OuterBrakeC = brake;
                            s.OuterIntegralRef = s.OuterPid.LastI;
                        }
                    }
                }
                var outerErr0 = s.SetpointC - outerPv;

                // 【本地改动】坡尾回基：曲线斜坡上主设定值每次外环更新都在动——动起来那一拍记下积分（坡起点），
                // 停下来（坡走完进保温段 / 曲线走完）那一拍把积分放回坡起点（这个温度学到过稳态偏置就放到它），只往停在目标前面那一侧放
                // （DischargeRamp）。跟坡时积分里多出来的那部分是夹套为带着釜走多拉开的 τ·速率，坡一停就是过冲的电荷（同趋近积分的道理）。
                // 坡起点的积分是起坡那个温度的稳态偏置：散热型偏置随温度单调，拿它当终点的，降温偏高、升温偏低——停在目标前面那一侧。
                // 回放（釜 τ ≈ 675 s）：32 → 20 ℃ 0.5 K/min 坡尾过冲 1.2 → 0.3 K 以内、20 → 60 ℃ 1 K/min 0.8 → 0.03 K。
                // 坡中途直接给新目标在上面收标志那儿放；坡中途重下一条坡不换起点。
                var spMoving = !double.IsNaN(s.LastOuterSetpointC) && Math.Abs(s.SetpointC - s.LastOuterSetpointC) > 1e-9;
                if (spMoving) s.LastSteadyBias = null;     // 【本地改动】曲线 / 斜坡在走（主设定值直接写、不走改目标那条）：学稳态偏置的低通同样从头来
                // 【本地改动】走曲线 / 斜坡（不立改目标那个标志）：到的是别的温度了，原来那个目标上冲过头的记录作废（BiasSelfCheck 按小门槛再改认它）
                if (spMoving || s.Profile is not null) s.RefixFrom = default;
                if (spMoving && s.Profile is not null)
                {
                    if (s.RampBaseI is null)
                    {
                        s.RampBaseI = s.OuterPid.LastI;
                        s.RampStartSpC = s.LastOuterSetpointC;
                    }
                }
                else if (!spMoving && s.RampBaseI is not null && (s.Profile is not null || s.ProfileFinished))
                {
                    // 进了保温段（曲线还在）或曲线是回路自己走完的；被外面停掉的等新目标那一拍（上面）按新目标放
                    s.ProfileFinished = false;
                    DischargeRamp(ch, s, Math.Sign(s.RampStartSpC - s.SetpointC), $"斜坡走到 {s.SetpointC:F2} ℃ 停了");
                }
                s.LastOuterSetpointC = s.SetpointC;

                // 【本地改动】升温加速（速度环 + 刹车曲线，HeatBoost）：这一拍归它管就整段按它来——夹套目标由「釜内升得多快、
                // 还差多远」直接算，外环 PID 不算不积分；趋近判据的误差历史照记（交还以后接着用），两枪放电、限幅扩程、稳态偏置学习、
                // 大降温收尾都跳过（交还 / 中止时两枪撤防、积分无扰接上）
                if (HeatBoostStep(ch, s, snapshot.Timestamp, outerPv, outerErr0, s.Push) is { } boostIsp)
                {
                    ApproachingSetpoint(s, snapshot.Timestamp, outerErr0);
                    BiasWinReset(s);                         // 偏置自校正只管现在的外环
                    s.LastOuterErrorC = outerErr0;
                    s.OuterSteadyCycles = 0;
                    s.BiasGuard.Reset();
                    s.FinAppliedC = 0;
                    s.OuterClampBinding = false;
                    s.InnerSetpointC = Math.Clamp(boostIsp, InnerSetpointMinC, InnerSetpointMaxC);
                }
                else
                {
                    // 【本地改动】趋近不积分（OuterApproachFreezeCPerMin）与内环跟不上不积分（OuterInnerLagFreezeC）：
                    // 前者 Tr 正往目标走时不攒（攒了到站就是过冲）；后者夹套还没追上外环上一拍要的需求时不攒——执行不了 / 还没执行到的
                    // 需求别记账。两条都只拦积分往参照值（信得过的偏置）外面、往外环正推的方向走；积分在参照值另一边、往回退（退债）不拦——
                    // 冲过头以后 Tr 往回走那一段要靠它把攒反了的积分退回来
                    var outward = (s.OuterPid.LastI - s.OuterIntegralRef) * outerErr0 >= 0;
                    var approachRaw = ApproachingSetpoint(s, snapshot.Timestamp, outerErr0);     // Tr 真在往目标走（不管积分在哪边）：趋近那一枪也认它
                    var approaching = approachRaw && outward;
                    var innerLag = s.InnerSetpointC - pv;
                    var innerBehind = OuterInnerLagFreezeC > 0 && Math.Abs(innerLag) > OuterInnerLagFreezeC
                                      && innerLag * outerErr0 > 0 && outward;

                    var freeze = (innerHigh && outerErr0 > 0 && s.OuterPid.LastI >= 0)
                                 || (innerLow && outerErr0 < 0 && s.OuterPid.LastI <= 0)
                                 || (!double.IsNaN(OuterIntegralBandC) && Math.Abs(outerErr0) > OuterIntegralBandC)   // 积分分离
                                 || approaching || innerBehind;
                    // 【本地改动】夹套压低（制冷段）：这一拍整段按它来——积分冻结（现有饱和冻结判据不覆盖
                    // 「I = +5.5、内环贴下限、err < 0」这种，必须显式冻，不然 I 以 Ki·err 慢慢往下积）。push 是这一拍开头拍下的那份
                    BiasSelfCheck(ch, s, snapshot.Timestamp, outerPv, pv, outerErr0, push);     // 【本地改动】表里的偏置错得多：按实测改（改了这一拍就按新积分算）
                    var integralBefore = s.OuterPid.LastI;
                    // 【本地改动】自校正刚改完：积分先按实测那个值拿住（OuterBiasCheckHold），别在改完那一下的过渡里又攒回去
                    // 釜内跑出实测那个目标 ±带子（改到附近也按实测那个目标量，改目标那一步不算跑出）、或走上曲线（设定在动）就放开、不再拿
                    if (snapshot.Timestamp < s.BiasHoldUntil
                        && (Math.Abs(outerPv - (double.IsFinite(s.BiasOverrideSp) ? s.BiasOverrideSp : s.SetpointC)) > OuterBiasCheckBandC || s.Profile is not null))
                        s.BiasHoldUntil = default;
                    var biasHold = snapshot.Timestamp < s.BiasHoldUntil;
                    // 【本地改动】解除压低以后（不管自校正管不管得着这个目标，两枪多半空打）：釜内在目标下面时积分不往上攒，压低拽下去那一截不是偏置不够——
                    // 攒进去就是下一回冲过头的电荷，再压低、再攒，越摆越大（孪生：40 稳住后压一次，积分 3.25 → 3.84 → 4.18、又压两次、又改一次）。
                    // 釜内在目标上面照常往下退（估高了靠它收）；到过下面又回到目标、走上曲线、压上了就放开
                    var releaseGuard = false;
                    if (snapshot.Timestamp < s.ReleaseGuardUntil)
                    {
                        if (s.Profile is not null) s.ReleaseGuardUntil = default;
                        else if (outerErr0 > 0) { s.ReleaseGuardBelow = true; releaseGuard = true; }
                        else if (s.ReleaseGuardBelow) s.ReleaseGuardUntil = default;
                    }
                    var bias = s.OuterPid.Update(s.SetpointC, outerPv, outerDt,
                        freezeIntegral: freeze || s.OuterClampBinding || push is not null || biasHold || releaseGuard);
                    if (Math.Abs(s.OuterPid.LastI - integralBefore) > 1e-12)
                    {
                        // 【本地改动】积过一下：起步那份刹车被吸收进去了，不再单算（改目标不再按它重摆）
                        s.OuterIntegralPristine = false;
                        s.OuterBrakeC = 0;
                    }
                    // 【本地改动】大降温收尾：爬的时候多给比例（加在 PID 之后，PID 自己的条件积分看不到它；近处偏置离限幅远，没影响）
                    var finish = OuterFinish(ch, s, snapshot.Timestamp, outerPv, outerErr0,
                        Math.Min(outerDt, OuterPeriod.TotalSeconds * 2.5), innerLow, push)
                                 + FinishCarry(s, snapshot.Timestamp);
                    s.FinAppliedC = finish;
                    if (finish != 0)
                        bias = Math.Clamp(bias + finish, s.OuterPid.OutputMin, s.OuterPid.OutputMax);

                    // 腔体跟踪缺口前馈：腔体离它的目标还差多少，就按比例多推一把，
                    // 等效把腔体跟随加速 (1+k) 倍——趋近段提前归位消掉剩余下冲，
                    // 稳态缺口→0 自然退场（pv 即内环被控量=腔体实测）。压低期间不叠加
                    var boost = OuterTrackingBoost <= 0 || push is not null
                        ? 0
                        : Math.Clamp(OuterTrackingBoost * (s.SetpointC + bias - pv),
                            -OuterBoostClampC, OuterBoostClampC);

                    // 落地偏置钳位（第三枪）：交接带内、首次过零前，偏置不许比
                    // 稳态偏置深出超过余量——腔体提前归位，压掉到温欠账。
                    // 事件武装的静态界，无动态无回路；过零（武装标志落下）即退场。
                    var effBias = bias;
                    s.OuterClampBinding = false;
                    if (push is null && OuterBiasClampBandC > 0 && s.OuterCrossingArmed
                        && SteadyBiasFor(s, s.SetpointC) is { } clampRef)     // 【本地改动】外环按釜选表
                    {
                        // 进带一次即锁存（带边按拍重判会被噪声来回触发，实测抖动 4 次）
                        if (!s.OuterClampLatched
                            && Math.Abs(s.SetpointC - outerPv) <= OuterBiasClampBandC)
                        {
                            s.OuterClampLatched = true;
                            s.ClampFromAbove = s.SetpointC - outerPv < 0;
                            s.ClampFloorSlewC = bias;   // 地板从当时偏置起步，斜坡抬向目标
                        }
                        if (s.OuterClampLatched)
                        {
                            var target = s.ClampFromAbove
                                ? clampRef - OuterBiasClampMarginC
                                : clampRef + OuterBiasClampMarginC;
                            var step = OuterClampSlewCPerMin / 60.0 * outerDt;
                            s.ClampFloorSlewC = s.ClampFromAbove
                                ? Math.Min(target, s.ClampFloorSlewC + step)
                                : Math.Max(target, s.ClampFloorSlewC - step);
                            effBias = s.ClampFromAbove
                                ? Math.Max(bias, s.ClampFloorSlewC)   // 自上而下趋近：限深
                                : Math.Min(bias, s.ClampFloorSlewC);  // 自下而上趋近：限高
                            s.OuterClampBinding = Math.Abs(effBias - bias) > 1e-9;
                        }
                    }

                    if (push is not null)
                    {
                        // 影子偏置：主设定 + P + D − 压低量（基准稳态偏置时再加冻结的 I），夹在偏置限幅内。
                        // 用 P 而不是 Tr − Δ：Tr 刚过目标 0.1 K 时两者只差 0.15 K，釜内冲得越高 P 越负压得越深
                        var baseBias = s.OuterPid.LastP + s.OuterPid.LastD + (push.FromEquilibrium ? s.OuterPid.LastI : 0);
                        effBias = Math.Clamp(baseBias - push.BelowC, s.OuterPid.OutputMin, s.OuterPid.OutputMax);
                    }
                    s.InnerSetpointC = Math.Clamp(s.SetpointC + effBias + boost,
                        InnerSetpointMinC, InnerSetpointMaxC);

                    // 两段式积分放电：趋近段沿途多攒的积分（远超稳态所需）若不处理，
                    // 只能靠温度越过设定值反向积分慢慢放掉——即串级下冲的主因。
                    // 第一枪（趋近放电）：误差在趋近带内、且 Tr 真在往目标走时放（1 K 小台阶一改完就在带里，Tr 没动之前不打）。从这里起比例项已足够
                    //   推进，僵尸积分只会把腔体设定值多压两分钟，让腔体在核心到站时
                    //   还欠着几℃ 才到稳态位置——实测剩余下冲全部来自这段腔体欠账。
                    // 第二枪（过零放电）：第一枪之后最后一段趋近重新累积的小额积分，
                    //   过零瞬间再清一次。
                    // 均为一次性触发；曲线运行中不适用；改设定值 / 解除压低后重新武装；第一枪到站稳住学到稳态偏置时还没打就作废；
                    // 积分从不冻结，稳态偏置过时最多偏一次、由正常积分接管收敛。
                    var outerErr = s.SetpointC - outerPv;
                    // 【本地改动】偏置自校正改过这个目标、拿住期也过了：解除压低重新武装的两枪不再往那个估值上打——估值差个零点几 K 时，
                    // 每次松开压低两枪都把积分拽回去，积分自己收不过去、差得又不到再改一次的门槛，就一直摆（孪生：估高 0.3–0.5 K 时每 11 分钟压低一次）；
                    // 积分放开，差得多了自校正再改一次。改目标武装的（新的一段趋近，包括改回原目标）照打：开头一两分钟釜内还没动、
                    // 积分照攒，不放掉就冲（孪生：40 改完改 41.5 不打冲 0.18、压低两次、又改一次；打 0.04）
                    var overrideSettled = s.ShotsFromPushRelease && OverrideCovers(s, s.SetpointC) && snapshot.Timestamp >= s.BiasHoldUntil;
                    if (push is null && s.Profile is null && !overrideSettled
                        && SteadyBiasFor(s, s.SetpointC) is { } steadyI)     // 【本地改动】外环按釜选表；压低期间两枪都不打
                    {
                        // 【本地改动】进了趋近带还得 Tr 真在往目标走才打：1 K 的小台阶一改完误差就在带里，原来第一拍就打（积分跟学到的差不到门槛、
                        // 空打），之后 Tr 还没动、积分照攒往外跑 0.5 K，没枪收拾（孪生：43 → 42 掉 0.135、42 → 43 冲 0.156 以后每 12 分钟压一回）
                        if (s.OuterApproachDischargeArmed
                            && Math.Abs(outerErr) <= OuterDischargeApproachBandC && approachRaw)
                        {
                            s.OuterApproachDischargeArmed = false;
                            if (Math.Abs(s.OuterPid.LastI - steadyI) > OuterDischargeMinGapC)
                            {
                                s.OuterPid.PresetIntegral(steadyI);
                                OnOuterPresetToLearned(s);     // 【本地改动】
                            }
                        }
                        // 过零判据用 <=0 且前值非零：严格 <0 会被"恰好等于零"的采样吞掉
                        // （TC1 量化 1mK，实测撞上 e=+0.000 → 乘积为零 → 第二枪未开，下冲 0.115）
                        if (s.OuterCrossingArmed
                            && !double.IsNaN(s.LastOuterErrorC) && Math.Abs(s.LastOuterErrorC) > 1e-12
                            && s.LastOuterErrorC * outerErr <= 0)
                        {
                            s.OuterCrossingArmed = false;
                            if (s.FinArmed)
                                FinishCrossingShot(ch, s, steadyI, snapshot.Timestamp);     // 【本地改动】大降温收尾：按过零速度封顶
                            else if (Math.Abs(s.OuterPid.LastI - steadyI) > OuterDischargeMinGapC)
                            {
                                s.OuterPid.PresetIntegral(steadyI);
                                OnOuterPresetToLearned(s);     // 【本地改动】
                            }
                        }
                    }
                    s.LastOuterErrorC = outerErr;

                    // 偏置限幅自动扩程：偏置顶在限幅上、内环未饱和、且误差已停止缩小，
                    // 说明瓶颈是这条人为限幅（维持低温需要的偏置随温差增大，低温段容易不够用）。
                    // 长距离降温时偏置顶限幅但误差持续缩小——那是限幅在正常限速，不会触发。
                    var oldLimit = s.OuterPid.OutputMax;
                    if (push is not null)
                    {
                        s.BiasGuard.Reset();           // 压低期间不扩程：扩程会写进增益表
                    }
                    else if (s.BiasGuard.Update(outerDt, bias, oldLimit, innerSaturated,
                            outerErr) is { } widened)
                    {
                        s.OuterPid.SetOutputLimits(-widened, widened);
                        if (OuterSchedule(s, s.SetpointC).WidenOuterBiasLimit(s.SetpointC, widened))     // 【本地改动】外环按釜选表
                            GainScheduleChanged?.Invoke(ch);
                        else
                            s.ManualOuterMaxBiasC = widened;   // 表里没有该温度点时至少本次运行生效
                        SaturationWarning?.Invoke(
                            $"通道{ch}外环偏置已顶在 ±{oldLimit:F0}℃ 限幅上 " +
                            $"{s.BiasGuard.PinnedSecondsToWiden / 60:F0} 分钟、误差停止缩小而内环尚有余力：" +
                            $"维持 {s.SetpointC:F1}℃ 需要的腔体-内部温差超出限幅，已自动放宽到 ±{widened:F0}℃");
                    }

                    // 稳态偏置学习：内部温度已稳在主设定值上时，当前偏置就是该温度维持平衡
                    // 所需的腔体-内部温差，登记后供下次同温启动预置外环积分。
                    // 学习带必须比 SteadyBandC 窄：缓沉暂态（误差 0.05~0.2）时若开录，
                    // 会把暂态偏置当稳态写进表，反过来让放电比对失准（第四轮实测教训）。
                    // 【本地改动】还要釜内这一阵真不动（OuterLearnStillC）：慢慢穿过目标的那一分钟也在带里，那时的偏置是漂着的。
                    // 大降温收尾武装着、多给的那份还没退完时也不学：那时的偏置里有漏热还没退的那一截
                    if (push is null && !innerSaturated && s.Profile is null && Math.Abs(outerErr) <= OuterLearnBandC && OuterStill(s)
                        && !(s.FinArmed && (!s.FinEnded || s.FinGain > 0)))
                    {
                        s.LastSteadyBias = s.LastSteadyBias is { } prev
                            ? prev + 0.05 * (bias - prev)   // 低通，约 20 次外环更新的时间常数
                            : bias;
                        // 【本地改动】学到的稳态偏置记进外环那张表；表里没有这个温度的行就按插值参数新建一行带着它——
                        // 自整定只在几个点整过，中间的温度第一次跑到稳态也该记下来，下次启动才有预置、两枪放电才开得了
                        if (++s.OuterSteadyCycles >= OuterSteadyCyclesToLearn
                            && OuterSchedule(s, s.SetpointC).LearnSteadyBias(s.SetpointC, s.LastSteadyBias.Value, createIfMissing: true))
                            GainScheduleChanged?.Invoke(ch);
                        // 【本地改动】这一场已经稳住过：积分里就是维持这个温度要的偏置，刹车那份不再单算（改方向 / 坡走完不重摆）
                        if (s.OuterSteadyCycles >= OuterSteadyCyclesToLearn)
                        {
                            s.OuterApproachDischargeArmed = false;     // 【本地改动】没趋近就到站稳住了（台阶小、Tr 没怎么动）：趋近那一枪作废，别留到以后乱打
                            // 【本地改动】真稳住学到了：这一场按学到的用（表里那行可能记在邻近的行、插值出来不一样，或者表没收），不退回表里那个。
                            // 只在自校正管着的目标上挪：管不着的目标学到的已经进表、照表查就是它；挪过去反倒把原目标那个实测丢了
                            // （改回原目标又按表里错的打两枪、又摆），附近没校正过的目标还平白不打两枪
                            if (OverrideCovers(s, s.SetpointC))
                            {
                                s.BiasOverrideC = s.LastSteadyBias;
                                s.BiasOverrideSp = s.SetpointC;
                                NoteMeasured(s, s.SetpointC, s.LastSteadyBias.Value);
                            }
                            s.OuterBiasKnown = true;
                            s.OuterIntegralPristine = false;
                            s.OuterBrakeC = 0;
                            s.OuterIntegralRef = s.OuterPid.LastI;
                        }
                    }
                    else
                    {
                        s.OuterSteadyCycles = 0;
                    }
                }
            }
        }
        else if (!cascade)
        {
            s.InnerSetpointC = s.SetpointC;
        }

        var innerSetpoint = s.InnerSetpointC;

        // 增益调度：按当前设定值插值出该温区的参数（表为空时沿用手动参数）
        if (Scheduling(s) && s.ActiveSchedule.GainsAt(innerSetpoint) is { } scheduled)     // 【本地改动】按执行器选表、按路开关
            s.Pid.SetGains(scheduled.Kp, scheduled.Ki, scheduled.Kd);
        else if (s.ManualGains is { } manual)
            s.Pid.SetGains(manual.Kp, manual.Ki, manual.Kd);

        // 【本地改动】加热棒 + TEC 双向：负半轴乘加热比再给 TEC，比值大于 1 时 PID 输出到 −上限 ÷ 比值 TEC 就满了——
        // 下限跟着缩（跟整定时一样），不然 TEC 早顶住了积分还往下攒，放热一停 TEC 半天退不下来，饱和判据也看不见 TEC 顶住
        if (s.Actuator == ActuatorMode.HeaterTec)
        {
            var r = (Scheduling(s) ? s.ActiveSchedule.HeatRatioAt(innerSetpoint) : null) ?? HeatingEffectivenessRatio;
            var floor = -s.MaxDutyMagnitude / Math.Max(1, r > 0 ? r : 1);
            if (Math.Abs(s.Pid.OutputMin - floor) > 1e-9) s.Pid.SetOutputLimits(floor, s.MaxDutyMagnitude);
        }

        var pidOut = s.Pid.Update(innerSetpoint, pv, dt);

        // 执行器不对称归一化：加热侧更有效时按比例缩小，使 PID 面对的执行器左右对称。
        // 比值优先取增益表"加热比"列的温度调度值，表里没登记时沿用全局值。
        // 只加热执行器不归一（比值恒 1）：单向执行器没有对侧可配平，该段自整定测得的
        // 就是加热棒本身的增益——再除一个（多半来自 TEC 时代的）比值只会让闭环与整定失配。
        var heatRatio = s.Actuator == ActuatorMode.HeatOnly
            ? 1.0
            : (Scheduling(s) ? s.ActiveSchedule.HeatRatioAt(innerSetpoint) : null)     // 【本地改动】
              ?? HeatingEffectivenessRatio;
        var duty = pidOut > 0 && heatRatio > 0
            ? pidOut / heatRatio
            : pidOut;
        if (s.Actuator == ActuatorMode.HeaterTec)
        {
            // 【本地改动】（0336）加热棒 + TEC 双向：正半轴原样给加热棒；负半轴给 TEC 乘加热比（双向挡那张表里这个比值是整定时的
            // TEC % ÷ 加热棒 %——整定和控温同一个映射）。写到设备上：正半轴按加热棒的接线符号，负半轴按「TEC 输出反向」
            duty = pidOut < 0 && heatRatio > 0 ? pidOut * heatRatio : pidOut;
            duty = ToDevice(s, Math.Clamp(duty, -s.MaxDutyMagnitude, s.MaxDutyMagnitude));
        }
        else if (s.Actuator == ActuatorMode.HeatOnly)
        {
            // 只加热执行器：占空比幅度恒 ≥0（另一侧的引脚上没有负载）。
            // 反向输出对加热棒无意义（引脚路由由固件固定）；写到设备上的符号按接线定（HeaterSign）【本地改动】
            duty = s.HeaterSign * Math.Clamp(duty, 0, 100);
        }
        else if (s.Actuator == ActuatorMode.CoolOnly)
        {
            // 【本地改动】只制冷：正半轴那一极接的是加热棒的 SSR、继电器断着，一笔正的都不写
            duty = Math.Clamp(duty, -100, 0);
            if (s.InvertOutput) duty = -duty;
        }
        else
        {
            duty = Math.Clamp(duty, -100, 100);
            if (s.InvertOutput) duty = -duty;
        }

        // 方向跑飞保护：输出饱和期间温度持续背离设定值 → 大概率极性接反，自动停控。
        // 只加热模式仅看加热侧饱和：贴着 0 下限时温度上漂是"没有制冷能力"，不是接反。
        var satHigh = pidOut >= s.Pid.OutputMax - 1e-6;
        var satLow = pidOut <= s.Pid.OutputMin + 1e-6;
        // 【本地改动】单方向执行器只看它能出力的那一侧：贴着 0 是「没有另一侧」，不是饱和
        var saturated = s.Actuator switch
        {
            ActuatorMode.HeatOnly => satHigh,
            ActuatorMode.CoolOnly => satLow,
            _ => satHigh || satLow
        };
        if (s.Runaway.Check(snapshot.Timestamp, innerSetpoint, pv, saturated))
        {
            await TripChannelAsync(ch, s.Actuator switch
            {
                ActuatorMode.HeatOnly =>
                    $"通道{ch}加热满幅期间温度持续不升反降，已自动停控。可能原因：" +
                    "①加热棒市电未通/固态继电器故障；②PWM 引脚接线脱落；③使能线未接通",
                ActuatorMode.CoolOnly =>
                    $"通道{ch}制冷满幅期间温度持续不降反升，已自动停控。可能原因：" +
                    "①TEC 功率线没接通 / 冷却水没开；②散热失效；③输出方向接反（检查「TEC 输出反向」）",
                ActuatorMode.HeaterTec =>       // 【本地改动】
                    $"通道{ch}输出饱和期间温度持续背离设定值，已自动停控。加热满幅不升：①加热棒市电未通 / 固态继电器故障；" +
                    "②加热棒继电器没合上、PWM 引脚接线脱落；制冷满幅不降：③TEC 功率线没接通 / 冷却水没开；④散热失效；" +
                    "⑤输出方向接反（检查「TEC 输出反向」「加热棒占空比」）",
                _ =>
                    $"通道{ch}输出饱和期间温度持续背离设定值，已自动停控。可能原因：" +
                    "①输出方向接反（检查“反向输出”勾选与接线极性）；" +
                    "②制冷废热未被带走；③散热失效"
            }).ConfigureAwait(false);
            return new ChannelCycleInfo(false, false, 0, s.SetpointC, pv, 0, 0, 0, 0);
        }

        // 稳态时记录 PID 输出：既供下次同点启动预置，也喂给前馈模型学习温度-输出关系。
        // 执行曲线期间设定值持续变化，不属于稳态，不予登记。
        if (!saturated && s.Profile is null && Math.Abs(innerSetpoint - pv) <= SteadyBandC)
        {
            s.LastSteadyOutput = s.LastSteadyOutput is { } prev
                ? prev + 0.02 * (pidOut - prev)   // 低通，约 25 个周期的时间常数
                : pidOut;

            // 连续稳定足够久才登记工作点，避免把暂态穿越误当稳态
            if (++s.SteadyCycles >= SteadyCyclesToLearn)
                s.Feedforward.Learn(innerSetpoint, s.LastSteadyOutput.Value);
        }
        else
        {
            s.SteadyCycles = 0;
        }

        // 长期饱和但温度并未跑飞：多为设定值超出能力，仅提示不停机
        if (!s.SaturationWarned && s.Runaway.SaturatedSeconds >= SaturationWarnAfter.TotalSeconds)
        {
            s.SaturationWarned = true;
            var mins = s.Runaway.SaturatedSeconds / 60;
            SaturationWarning?.Invoke(cascade && s.Push is not null
                // 【本地改动】制冷段压着夹套目标：满幅是设计内的（夹套设定在釜内之下），TEC 接上后会退出
                ? $"通道{ch}输出已连续满幅 {mins:F0} 分钟：制冷段夹套压低中（夹套设定 {innerSetpoint:F2} ℃ 低于釜内 {outerPv:F2} ℃），TEC 接上后会退出饱和"
                : cascade
                // 串级启动阶段外环通常会把内环设定推到偏置限幅以索取最大能力，属正常现象
                ? $"通道{ch}输出已连续满幅 {mins:F0} 分钟：外环正要求最大能力" +
                  $"（内部 {(holding ? "Tr 丢失保持中" : $"{outerPv:F2}℃")} → 主设定 {s.SetpointC:F2}℃；腔体 {pv:F2}℃ → 外环要求 {innerSetpoint:F2}℃）。" +
                  "串级启动阶段属正常，待内部接近主设定值后会自动退出饱和；" +
                  "若内部已接近主设定值仍持续满幅，再检查制冷能力或输出上限"
                : $"通道{ch}输出已连续满幅 {mins:F0} 分钟（当前 {pv:F2}℃，设定 {innerSetpoint:F2}℃）：" +
                  "系统正朝设定值靠近但未达到，可能是设定值超出当前制冷/加热能力，或输出上限设得偏低");
        }
        else if (!saturated)
        {
            s.SaturationWarned = false;
        }

        await WriteDutyAsync(ch, duty, ct).ConfigureAwait(false);
        // 饱和判据拿 LastDutyPercent 跟 PID 的限幅比，所以这里记的是 PID 那一侧的输出（反向、加热比、加热棒符号都还没动过的）
        // ——写到设备上的数在 ChannelCycleInfo.DutyPercent 里【本地改动】
        s.LastDutyPercent = pidOut;

        return new ChannelCycleInfo(true, false, 0, s.SetpointC, pv, duty,
            s.Pid.LastP, s.Pid.LastI, s.Pid.LastD)
        {
            Cascade = cascade,
            InnerSetpointC = innerSetpoint,
            OuterMeasuredC = outerPv,
            ProfileProgress = profileProgress,
            ProfileName = profileName,
            OuterHoldSeconds = holding ? (snapshot.Timestamp - s.OuterLostSince!.Value).TotalSeconds : null,   // 【本地改动】
            AppliedDutyPercent = double.IsNaN(s.AppliedDutyPercent) ? null : s.AppliedDutyPercent,
            OuterP = cascade ? s.OuterPid.LastP : double.NaN,
            OuterI = cascade ? s.OuterPid.LastI : double.NaN,
            OuterD = cascade ? s.OuterPid.LastD : double.NaN,
            OuterPushC = cascade && s.Push is { } pushed ? pushed.BelowC : double.NaN,
            OuterFinishC = cascade && !holding ? s.FinAppliedC : double.NaN,
            HeatBoosting = cascade && !holding && s.VelMode,
            HeatBoostVmCPerMin = cascade && !holding && s.VelMode ? s.VelVmCPerMin : double.NaN,
            HeatBoostVstarCPerMin = cascade && !holding && s.VelMode ? s.VelVstarCPerMin : double.NaN,
            HeatBoostBrake = cascade && !holding && s.VelMode ? s.VelBrake : double.NaN,
        };
    }

    private async Task<ChannelCycleInfo> RunTuningStepAsync(
        int ch, ChannelState s, TecSnapshot snapshot, double pv, CancellationToken ct)
    {
        var tuner = s.Tuner!;

        if (double.IsNaN(pv))
        {
            await FinishTuningAsync(ch, s, success: false,
                reason: $"通道{ch}温度读数丢失（传感器未接或断线）").ConfigureAwait(false);
            return new ChannelCycleInfo(false, false, 0, s.SetpointC, pv, 0, 0, 0, 0);
        }

        if (DateTime.Now - s.TuneStartTime > AutoTuneTimeout)
        {
            await FinishTuningAsync(ch, s, success: false,
                reason: $"自整定超时（{AutoTuneTimeout.TotalMinutes:F0} 分钟）：对象响应过慢或继电幅值不足").ConfigureAwait(false);
            return new ChannelCycleInfo(false, false, 0, s.SetpointC, pv, 0, 0, 0, 0);
        }

        // 【本地改动】双向挡整定里要制冷、TEC 却被扣着（等冷水机最短间隔，形态只加热）：告诉整定器这一周不算数
        if (s.Table == GainTable.Both)
            tuner.SetWithheld(snapshot.Timestamp, s.Actuator == ActuatorMode.HeatOnly && tuner.CoolingHalf);
        var duty = tuner.Process(snapshot.Timestamp, pv);
        s.LastTuneOutput = duty;        // 【本地改动】PID 那一侧的量：双向挡整定时会话按它的符号扳继电器
        // 只加热执行器：引脚路由固件固定，反向无意义（反向会把正摆动送成负、加热棒不动作）；符号按接线【本地改动】
        // 【本地改动】双向挡整定里 TEC 暂时接不上（等冷水机最短间隔）也是只加热：负的那半周钳到 0（加热棒关着自然凉），不许送到 TEC 引脚
        if (s.Actuator == ActuatorMode.HeatOnly) duty = s.HeaterSign * Math.Max(0, duty);
        else if (s.Actuator == ActuatorMode.HeaterTec)
        {
            // 【本地改动】双向挡整定：TEC 那半周乘比值（TEC % ÷ 加热棒 %），跟控温时负半轴乘加热比同一个映射
            if (s.Table == GainTable.Both && duty < 0) duty = Math.Max(duty * s.TuneCoolRatio, -s.MaxDutyMagnitude);
            duty = ToDevice(s, duty);
        }
        else if (s.InvertOutput) duty = -duty;

        await WriteDutyAsync(ch, duty, ct).ConfigureAwait(false);
        s.LastDutyPercent = duty;

        if (tuner.State == AutoTuneState.Succeeded)
        {
            var result = tuner.Result;
            await FinishTuningAsync(ch, s, success: true, result: result).ConfigureAwait(false);
            return new ChannelCycleInfo(false, false, tuner.CompletedCycles, s.SetpointC, pv, 0, 0, 0, 0);
        }

        if (tuner.State == AutoTuneState.Failed)
        {
            await FinishTuningAsync(ch, s, success: false, reason: tuner.FailReason).ConfigureAwait(false);
            return new ChannelCycleInfo(false, false, tuner.CompletedCycles, s.SetpointC, pv, 0, 0, 0, 0);
        }

        return new ChannelCycleInfo(false, true, tuner.CompletedCycles, s.SetpointC, pv, duty, 0, 0, 0);
    }

    private async Task FinishTuningAsync(int ch, ChannelState s, bool success,
        AutoTuneResult? result = null, string? reason = null)
    {
        // 自整定成功即按整定温度登记增益工作点，跨温区自动建表（默认采用平稳型参数）。
        // 同时按该温度的 Tu 推荐一组外环起始参数：外环无法用继电器法整定（内环得先闭环），
        // 但外环该多慢与内环 Tu 直接相关，据此给出的起点比全温域一组固定值可靠得多。
        if (success && result is not null)
        {
            var outer = PidGainSchedule.SuggestOuter(result.UltimatePeriodTuSeconds);
            s.ActiveSchedule.Learn(new GainPoint(     // 【本地改动】登记进整定时那张表（双向挡整定里形态会在只加热 ⇄ 双向间换，表不换）
                s.SetpointC, result.Conservative, result.UltimateGainKu, result.UltimatePeriodTuSeconds,
                outer.Gains, outer.MaxBiasC,
                HeatRatio: s.Table == GainTable.Both ? s.TuneCoolRatio : null));   // 双向挡：整定时 TEC 那半周乘的比值，控温照样乘它
            GainScheduleChanged?.Invoke(ch);
        }

        s.Tuner = null;
        try { await StopChannelAsync(ch).ConfigureAwait(false); }
        catch { /* 尽力而为 */ }
        AutoTuneFinished?.Invoke(new AutoTuneOutcome(ch, success, result, reason));
    }

    private async Task TripChannelAsync(int ch, string reason)
    {
        try { await StopChannelAsync(ch).ConfigureAwait(false); }
        catch { /* 尽力而为 */ }
        ChannelTripped?.Invoke(ch, reason);      // 【本地改动】
        SafetyTripped?.Invoke(reason);
    }

    private async Task TripSafetyAsync(string reason)
    {
        foreach (var ch in new[] { 1, 2 })
        {
            var s = State(ch);
            if (s.Busy)
            {
                try { await StopChannelAsync(ch).ConfigureAwait(false); }
                catch { /* 通信中断时无法归零，由设备端硬件保护兜底 */ }
                ChannelTripped?.Invoke(ch, reason);      // 【本地改动】
            }
        }
        SafetyTripped?.Invoke(reason);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
