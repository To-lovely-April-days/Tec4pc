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
}

/// <summary>四片 TEC（两路输出）的同步方式。</summary>
public enum ChannelSyncMode
{
    /// <summary>上位机镜像（推荐）：设备 CONTMODE=0，程序每次把同一占空比分别写给两个通道。
    /// 显式、可验证、可配平，不依赖读不回来的固件联动状态。</summary>
    HostMirror,

    /// <summary>硬件跟随：依赖设备 CONTMODE=2（通道 2 输出由固件复制通道 1）。</summary>
    HardwareFollow,
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
        /// 仅在误差足够小且未饱和时以低通方式更新。</summary>
        public double? LastSteadyOutput;

        /// <summary>跨设定值的稳态前馈模型（自动学习"任意温度需要多少输出"）。</summary>
        public readonly SteadyStateFeedforward Feedforward = new();

        /// <summary>增益调度表（自整定结果按温度登记，运行时插值）。</summary>
        public readonly PidGainSchedule GainSchedule = new();

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


        /// <summary>外环算出的内环设定值（℃）。</summary>
        public double InnerSetpointC;

        public DateTime LastOuterUpdate;

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

        /// <summary>反馈传感器：0 = 用本通道传感器（默认）；1/2 = 指定 TC1/TC2。
        /// 执行器与探头位置解耦（如 CONTMODE=2 单环控制时主环用 TC2 腔体探头做反馈）。</summary>
        public int FeedbackSensor;

        /// <summary>手动输出的物理原始占空比（已含反向）。实测 215L 固件：MODE=3 输出若不定期
        /// 刷新会被固件看门狗自动撤销（约 1 分钟），因此手动模式也需每周期重写。</summary>
        public double? ManualDutyPercent;

        public bool Busy => Active || Tuner is not null;
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
        await controller.SetDutyPercentAsync(ch, duty, ct).ConfigureAwait(false);
        if (ch == 1 && SyncMode == ChannelSyncMode.HostMirror)
            await controller.SetDutyPercentAsync(2, duty, ct).ConfigureAwait(false);
    }

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
        var magnitude = Math.Clamp(Math.Abs(maxDutyPercent), 0, 100);
        var s = State(ch);
        s.MaxDutyMagnitude = magnitude;
        s.Pid.Configure(kp, ki, kd, OutputFloor(s), magnitude, derivativeFilterSeconds);
        s.InvertOutput = invertOutput;
        s.ManualGains = new PidGains(kp, ki, kd);
    }

    /// <summary>该通道 PID 的输出下限：只加热执行器为 0（负半轴不存在），双向为 −上限。</summary>
    private static double OutputFloor(ChannelState s) =>
        s.Actuator == ActuatorMode.HeatOnly ? 0 : -s.MaxDutyMagnitude;

    /// <summary>
    /// 设置执行器形态（手动换线过渡方案）。运行/整定中禁止切换——形态必须与当时的
    /// 实际接线一致，只能停下来、改完线再切。切到只加热时内环输出下限抬到 0：
    /// 负输出不存在（固件会把负占空比路由到制冷 PWM 引脚，而 TEC 线已断开），
    /// PID 若被允许算出负值只会白积负积分。
    /// </summary>
    public void SetActuatorMode(int ch, ActuatorMode mode)
    {
        var s = State(ch);
        if (s.Busy && s.Actuator != mode)
            throw new InvalidOperationException($"通道{ch}正在运行，请先停止再切换执行器形态（并确认接线已相应改好）");
        s.Actuator = mode;
        s.Pid.SetOutputLimits(OutputFloor(s), s.MaxDutyMagnitude);
    }

    public ActuatorMode GetActuatorMode(int ch) => State(ch).Actuator;

    /// <summary>该通道的增益调度表（自整定成功后自动登记工作点）。</summary>
    public PidGainSchedule GetGainSchedule(int ch) => State(ch).GainSchedule;

    /// <summary>启用增益调度：表内有点时按设定值插值覆盖手动参数。</summary>
    public bool GainSchedulingEnabled { get; set; } = true;

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
        if (Math.Abs(celsius - s.SetpointC) > 1e-9)
        {
            // 新目标意味着新一段趋近过程：重新武装两段积分放电
            s.OuterApproachDischargeArmed = true;
            s.OuterCrossingArmed = true;
            s.OuterClampLatched = false;
            s.OuterClampBinding = false;
            s.LastOuterErrorC = double.NaN;
        }
        s.SetpointC = celsius;
    }

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
    private void ApplyOuterTuning(ChannelState s, double setpointC)
    {
        var tuning = GainSchedulingEnabled ? s.GainSchedule.OuterAt(setpointC) : null;
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

    public void SetStrategy(int ch, ControlStrategy strategy) => State(ch).Strategy = strategy;

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
    }

    public void StopProfile(int ch) => State(ch).Profile = null;

    public ITemperatureProfile? GetProfile(int ch) => State(ch).Profile;

    /// <summary>预测维持指定设定值所需的稳态输出（PID 内部符号）；无数据返回 null。</summary>
    public double? PredictSteadyOutput(int ch, double setpointC) =>
        State(ch).Feedforward.Predict(setpointC);

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
        if ((integralPreset ?? s.Feedforward.Predict(setpointC) ?? s.LastSteadyOutput) is { } preset)
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
            s.InnerSetpointC = setpointC;
            ApplyOuterTuning(s, setpointC);   // 先装限幅，预置才会按正确范围截断
            if (s.GainSchedule.SteadyBiasAt(setpointC) is { } bias)
            {
                s.OuterPid.PresetIntegral(bias);
                s.InnerSetpointC = Math.Clamp(setpointC + bias, InnerSetpointMinC, InnerSetpointMaxC);
            }
        }

        s.SteadyCycles = 0;
        s.Runaway.Start(DateTime.Now);
        s.SaturationWarned = false;
        s.LastUpdateTime = default;
        s.LastDutyPercent = 0;
        s.Active = true;
    }

    /// <summary>启动继电器法自整定（会激起 ±回差以上的小幅温度振荡）。</summary>
    public async Task StartAutoTuneAsync(int ch, double setpointC,
        double relayAmplitudePercent, double hysteresisC, CancellationToken ct = default)
    {
        var s = State(ch);
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
        var ceiling = Math.Max(1, s.Pid.OutputMax);
        if (h >= ceiling) bias = 0;   // 幅值本身已覆盖全量程时无偏置可言

        // 只加热执行器：输出下限 0，继电摆动整体落在正半轴（弱加热半周靠自然散热降温）。
        // 此时幅值不能超过上限的一半，否则可行偏置域为空——整定器会把偏置收缩到中点，
        // 等效幅值被迫减小，建议界面幅值 ≤ 上限/2。
        s.Tuner = new RelayAutoTuner(setpointC, relayAmplitudePercent, hysteresisC, bias,
            outputCeilingPercent: ceiling,
            outputFloorPercent: s.Actuator == ActuatorMode.HeatOnly ? 0 : double.NaN);
        s.TuneStartTime = DateTime.Now;
    }

    /// <summary>停止某通道的一切输出活动（闭环/自整定/手动）：输出归零并关闭使能。</summary>
    public async Task StopChannelAsync(int ch, CancellationToken ct = default)
    {
        var s = State(ch);
        s.Active = false;
        s.Tuner = null;
        s.ManualDutyPercent = null;
        s.LastDutyPercent = 0;
        await controller.SetDutyPercentAsync(ch, 0, ct).ConfigureAwait(false);
        await controller.SetEnableAsync(ch, false, ct).ConfigureAwait(false);
        if (ch == 1)
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

        var raw = s.Actuator == ActuatorMode.HeatOnly
            ? Math.Clamp(dutyPercent, 0, 100)          // 加热棒引脚路由固定，反向无意义
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
        await controller.SetPwmFrequencyAsync(
            State(ch).Actuator == ActuatorMode.HeatOnly ? HeaterPwmFrequencyLevel : TecPwmFrequencyLevel,
            ct).ConfigureAwait(false);

        if (ch == 1)
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

            if (s.Tuner is not null)
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

        // ① 设定值生成层：时间驱动曲线（结晶降温等）
        double? profileProgress = null;
        var profileName = s.Profile?.Name;
        if (s.Profile is { } profile)
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
                profileProgress = 1;
                ProfileCompleted?.Invoke(ch, profile.Name);
            }
        }

        // ② 闭环控制层：串级时先跑外环，算出内环设定值
        var cascade = s.Strategy == ControlStrategy.Cascade;
        var outerPv = double.NaN;
        if (cascade)
        {
            outerPv = s.OuterFeedbackSensor == 1 ? snapshot.Temp1C : snapshot.Temp2C;
            if (double.IsNaN(outerPv))
            {
                await TripChannelAsync(ch,
                    $"通道{ch}串级外环传感器（TC{s.OuterFeedbackSensor}）读数丢失，已停控").ConfigureAwait(false);
                return new ChannelCycleInfo(false, false, 0, s.SetpointC, pv, 0, 0, 0, 0);
            }

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
                var innerSaturated = s.LastDutyPercent >= s.Pid.OutputMax - 1e-6
                                     || s.LastDutyPercent <= s.Pid.OutputMin + 1e-6;
                var bias = s.OuterPid.Update(s.SetpointC, outerPv, outerDt,
                    freezeIntegral: innerSaturated || s.OuterClampBinding);

                // 腔体跟踪缺口前馈：腔体离它的目标还差多少，就按比例多推一把，
                // 等效把腔体跟随加速 (1+k) 倍——趋近段提前归位消掉剩余下冲，
                // 稳态缺口→0 自然退场（pv 即内环被控量=腔体实测）
                var boost = OuterTrackingBoost <= 0
                    ? 0
                    : Math.Clamp(OuterTrackingBoost * (s.SetpointC + bias - pv),
                        -OuterBoostClampC, OuterBoostClampC);

                // 落地偏置钳位（第三枪）：交接带内、首次过零前，偏置不许比
                // 稳态偏置深出超过余量——腔体提前归位，压掉到温欠账。
                // 事件武装的静态界，无动态无回路；过零（武装标志落下）即退场。
                var effBias = bias;
                s.OuterClampBinding = false;
                if (OuterBiasClampBandC > 0 && s.OuterCrossingArmed
                    && s.GainSchedule.SteadyBiasAt(s.SetpointC) is { } clampRef)
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
                if (s.Profile is null
                    && s.GainSchedule.SteadyBiasAt(s.SetpointC) is { } steadyI)
                {
                    if (s.OuterApproachDischargeArmed
                        && Math.Abs(outerErr) <= OuterDischargeApproachBandC)
                    {
                        s.OuterApproachDischargeArmed = false;
                        if (Math.Abs(s.OuterPid.LastI - steadyI) > OuterDischargeMinGapC)
                            s.OuterPid.PresetIntegral(steadyI);
                    }
                    // 过零判据用 <=0 且前值非零：严格 <0 会被"恰好等于零"的采样吞掉
                    // （TC1 量化 1mK，实测撞上 e=+0.000 → 乘积为零 → 第二枪未开，下冲 0.115）
                    if (s.OuterCrossingArmed
                        && !double.IsNaN(s.LastOuterErrorC) && Math.Abs(s.LastOuterErrorC) > 1e-12
                        && s.LastOuterErrorC * outerErr <= 0)
                    {
                        s.OuterCrossingArmed = false;
                        if (Math.Abs(s.OuterPid.LastI - steadyI) > OuterDischargeMinGapC)
                            s.OuterPid.PresetIntegral(steadyI);
                    }
                }
                s.LastOuterErrorC = outerErr;

                // 偏置限幅自动扩程：偏置顶在限幅上、内环未饱和、且误差已停止缩小，
                // 说明瓶颈是这条人为限幅（维持低温需要的偏置随温差增大，低温段容易不够用）。
                // 长距离降温时偏置顶限幅但误差持续缩小——那是限幅在正常限速，不会触发。
                var oldLimit = s.OuterPid.OutputMax;
                if (s.BiasGuard.Update(outerDt, bias, oldLimit, innerSaturated,
                        outerErr) is { } widened)
                {
                    s.OuterPid.SetOutputLimits(-widened, widened);
                    if (s.GainSchedule.WidenOuterBiasLimit(s.SetpointC, widened))
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
                if (!innerSaturated && s.Profile is null && Math.Abs(outerErr) <= OuterLearnBandC)
                {
                    s.LastSteadyBias = s.LastSteadyBias is { } prev
                        ? prev + 0.05 * (bias - prev)   // 低通，约 20 次外环更新的时间常数
                        : bias;
                    if (++s.OuterSteadyCycles >= OuterSteadyCyclesToLearn
                        && s.GainSchedule.LearnSteadyBias(s.SetpointC, s.LastSteadyBias.Value))
                        GainScheduleChanged?.Invoke(ch);
                }
                else
                {
                    s.OuterSteadyCycles = 0;
                }
            }
        }
        else
        {
            s.InnerSetpointC = s.SetpointC;
        }

        var innerSetpoint = s.InnerSetpointC;

        // 增益调度：按当前设定值插值出该温区的参数（表为空时沿用手动参数）
        if (GainSchedulingEnabled && s.GainSchedule.GainsAt(innerSetpoint) is { } scheduled)
            s.Pid.SetGains(scheduled.Kp, scheduled.Ki, scheduled.Kd);
        else if (s.ManualGains is { } manual)
            s.Pid.SetGains(manual.Kp, manual.Ki, manual.Kd);

        var pidOut = s.Pid.Update(innerSetpoint, pv, dt);

        // 执行器不对称归一化：加热侧更有效时按比例缩小，使 PID 面对的执行器左右对称。
        // 比值优先取增益表"加热比"列的温度调度值，表里没登记时沿用全局值。
        // 只加热执行器不归一（比值恒 1）：单向执行器没有对侧可配平，该段自整定测得的
        // 就是加热棒本身的增益——再除一个（多半来自 TEC 时代的）比值只会让闭环与整定失配。
        var heatRatio = s.Actuator == ActuatorMode.HeatOnly
            ? 1.0
            : (GainSchedulingEnabled ? s.GainSchedule.HeatRatioAt(innerSetpoint) : null)
              ?? HeatingEffectivenessRatio;
        var duty = pidOut > 0 && heatRatio > 0
            ? pidOut / heatRatio
            : pidOut;
        if (s.Actuator == ActuatorMode.HeatOnly)
        {
            // 只加热执行器：占空比恒 ≥0（负值会被固件路由到制冷 PWM 引脚，线已断开）。
            // 反向输出对加热棒无意义（引脚路由由固件固定），忽略以免正输出被反成负、加热棒永不动作。
            duty = Math.Clamp(duty, 0, 100);
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
        var saturated = s.Actuator == ActuatorMode.HeatOnly ? satHigh : satHigh || satLow;
        if (s.Runaway.Check(snapshot.Timestamp, innerSetpoint, pv, saturated))
        {
            await TripChannelAsync(ch, s.Actuator == ActuatorMode.HeatOnly
                ? $"通道{ch}加热满幅期间温度持续不升反降，已自动停控。可能原因：" +
                  "①加热棒市电未通/固态继电器故障；②PWM 引脚接线脱落；③使能线未接通"
                : $"通道{ch}输出饱和期间温度持续背离设定值，已自动停控。可能原因：" +
                  "①输出方向接反（检查“反向输出”勾选与接线极性）；" +
                  "②制冷废热未被带走；③散热失效").ConfigureAwait(false);
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
            SaturationWarning?.Invoke(cascade
                // 串级启动阶段外环通常会把内环设定推到偏置限幅以索取最大能力，属正常现象
                ? $"通道{ch}输出已连续满幅 {mins:F0} 分钟：外环正要求最大能力" +
                  $"（内部 {outerPv:F2}℃ → 主设定 {s.SetpointC:F2}℃；腔体 {pv:F2}℃ → 外环要求 {innerSetpoint:F2}℃）。" +
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
        s.LastDutyPercent = duty;

        return new ChannelCycleInfo(true, false, 0, s.SetpointC, pv, duty,
            s.Pid.LastP, s.Pid.LastI, s.Pid.LastD)
        {
            Cascade = cascade,
            InnerSetpointC = innerSetpoint,
            OuterMeasuredC = outerPv,
            ProfileProgress = profileProgress,
            ProfileName = profileName,
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

        var duty = tuner.Process(snapshot.Timestamp, pv);
        // 只加热执行器：引脚路由固件固定，反向无意义（反向会把正摆动送成负、加热棒不动作）
        if (s.Actuator != ActuatorMode.HeatOnly && s.InvertOutput) duty = -duty;

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
            s.GainSchedule.Learn(new GainPoint(
                s.SetpointC, result.Conservative, result.UltimateGainKu, result.UltimatePeriodTuSeconds,
                outer.Gains, outer.MaxBiasC));
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
