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
        /// 核心谷底恰在 t=13.9）。误差首次缩进趋近带内时提前放电，腔体提前两分钟归位。
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
            s.Push = null;
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
    /// 趋近放电带宽（℃）：外环误差首次缩到该值以内时执行第一段积分放电。
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
    /// 【本地改动】内环跟不上不积分（K）：夹套离它的目标（外环上一拍算的内环设定）还差这么多、且差在外环正要推的那一边时，
    /// 外环积分不往那个方向攒（往回退不拦）。≤0 或 NaN = 关。
    /// 小台阶（20 → 22 ℃）时比例项不饱和，改设定后头一两分钟夹套还在追、Tr 还没动——「趋近」判据看不出来，
    /// 积分照攒，回放过冲 0.2 K；加上这条 0.02 K。双向挡 TEC 等冷水机 / 等驻留没出力时也是同一回事：执行不了的需求别记账。
    /// </summary>
    public double OuterInnerLagFreezeC { get; set; } = 0.5;

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
    private static double? SteadyBiasFor(ChannelState s, double setpointC)
        => OuterSchedule(s, setpointC).SteadyBiasAt(setpointC)
           ?? (s.Table == GainTable.Both ? NearestOuterSchedule(s, setpointC).SteadyBiasAt(setpointC) : null);

    private static PidGainSchedule NearestOuterSchedule(ChannelState s, double setpointC)
    {
        var tec = s.GainSchedule.NearestOuterDistance(setpointC);
        var heater = s.HeaterSchedule.NearestOuterDistance(setpointC);
        if (tec is null && heater is null) return s.ActiveSchedule;
        if (heater is null) return s.GainSchedule;
        if (tec is null) return s.HeaterSchedule;
        return tec <= heater ? s.GainSchedule : s.HeaterSchedule;
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
            s.InnerSetpointC = setpointC;
            Volatile.Write(ref s.RampCancelledFlag, 1);        // 【本地改动】坡尾回基 / 趋近判据从头来（回路线程清）
            Volatile.Write(ref s.OuterTargetChangedFlag, 0);
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

    /// <summary>【本地改动】记一笔外环误差并判断「正在趋近」：时间窗里误差在缩小、缩得比 OuterApproachFreezeCPerMin 快。
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
        return err * rate < 0 && Math.Abs(rate) * 60 > OuterApproachFreezeCPerMin;
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
                // 【本地改动】会话线程立的标志先收走、在这儿（回路线程）落实，再按（可能刚换的）目标算误差
                if (Interlocked.Exchange(ref s.RampCancelledFlag, 0) != 0)
                {
                    s.RampBaseI = null;
                    s.ProfileFinished = false;
                }
                if (Interlocked.Exchange(ref s.OuterRearmedFlag, 0) != 0) s.OuterErrHistory.Clear();
                if (Interlocked.Exchange(ref s.OuterTargetChangedFlag, 0) != 0)
                {
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

                // 【本地改动】趋近不积分（OuterApproachFreezeCPerMin）与内环跟不上不积分（OuterInnerLagFreezeC）：
                // 前者 Tr 正往目标走时不攒（攒了到站就是过冲）；后者夹套还没追上外环上一拍要的需求时不攒——执行不了 / 还没执行到的
                // 需求别记账。两条都只拦积分往参照值（信得过的偏置）外面、往外环正推的方向走；积分在参照值另一边、往回退（退债）不拦——
                // 冲过头以后 Tr 往回走那一段要靠它把攒反了的积分退回来
                var outward = (s.OuterPid.LastI - s.OuterIntegralRef) * outerErr0 >= 0;
                var approaching = ApproachingSetpoint(s, snapshot.Timestamp, outerErr0) && outward;
                var innerLag = s.InnerSetpointC - pv;
                var innerBehind = OuterInnerLagFreezeC > 0 && Math.Abs(innerLag) > OuterInnerLagFreezeC
                                  && innerLag * outerErr0 > 0 && outward;

                var freeze = (innerHigh && outerErr0 > 0 && s.OuterPid.LastI >= 0)
                             || (innerLow && outerErr0 < 0 && s.OuterPid.LastI <= 0)
                             || (!double.IsNaN(OuterIntegralBandC) && Math.Abs(outerErr0) > OuterIntegralBandC)   // 积分分离
                             || approaching || innerBehind;
                // 【本地改动】夹套压低（制冷段）：这一拍整段按它来——积分冻结（现有饱和冻结判据不覆盖
                // 「I = +5.5、内环贴下限、err < 0」这种，必须显式冻，不然 I 以 Ki·err 慢慢往下积）
                var push = s.Push;
                var integralBefore = s.OuterPid.LastI;
                var bias = s.OuterPid.Update(s.SetpointC, outerPv, outerDt,
                    freezeIntegral: freeze || s.OuterClampBinding || push is not null);
                if (Math.Abs(s.OuterPid.LastI - integralBefore) > 1e-12)
                {
                    // 【本地改动】积过一下：起步那份刹车被吸收进去了，不再单算（改目标不再按它重摆）
                    s.OuterIntegralPristine = false;
                    s.OuterBrakeC = 0;
                }

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
                // 第一枪（趋近放电）：误差首次缩进趋近带内即放。从这里起比例项已足够
                //   推进，僵尸积分只会把腔体设定值多压两分钟，让腔体在核心到站时
                //   还欠着几℃ 才到稳态位置——实测剩余下冲全部来自这段腔体欠账。
                // 第二枪（过零放电）：第一枪之后最后一段趋近重新累积的小额积分，
                //   过零瞬间再清一次。
                // 均为一次性触发；曲线运行中不适用；改设定值后重新武装；
                // 积分从不冻结，稳态偏置过时最多偏一次、由正常积分接管收敛。
                var outerErr = s.SetpointC - outerPv;
                if (push is null && s.Profile is null
                    && SteadyBiasFor(s, s.SetpointC) is { } steadyI)     // 【本地改动】外环按釜选表；压低期间两枪都不打
                {
                    if (s.OuterApproachDischargeArmed
                        && Math.Abs(outerErr) <= OuterDischargeApproachBandC)
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
                        if (Math.Abs(s.OuterPid.LastI - steadyI) > OuterDischargeMinGapC)
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
                if (push is null && !innerSaturated && s.Profile is null && Math.Abs(outerErr) <= OuterLearnBandC)
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
