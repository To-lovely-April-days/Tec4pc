namespace TecControl.Core.Control;

public enum AutoTuneState
{
    /// <summary>等待温度第一次穿越设定值（建立振荡）。</summary>
    Seeking,
    /// <summary>继电振荡测量中。</summary>
    Oscillating,
    Succeeded,
    Failed,
}

/// <summary>一组 PID 增益（单位与 PidController 一致：%/℃、%/(℃·s)、%·s/℃）。</summary>
public sealed record PidGains(double Kp, double Ki, double Kd)
{
    public override string ToString() => $"Kp={Kp:F3}  Ki={Ki:F4}  Kd={Kd:F3}";
}

/// <summary>自整定结果。</summary>
public sealed record AutoTuneResult(
    double UltimateGainKu,
    double UltimatePeriodTuSeconds,
    double RelayAmplitudePercent,
    double OscillationAmplitudeC,
    PidGains Fast,          // Ziegler–Nichols 经典规则：响应快，可能有超调
    PidGains Conservative); // Tyreus–Luyben 规则：平稳无超调优先（推荐温控使用）

/// <summary>
/// 继电反馈自整定（Åström–Hägglund 法，带输出偏置）。
///
/// 原理：输出在 bias±h（偏置 ± 继电幅值）之间按温度相对设定值的位置切换（带回差
/// 抗噪声），使对象进入小幅等幅振荡；测得振荡周期 Tu 与振幅 a 后：
///     Ku = 4h / (π·a)   （偏置不进公式：Ku 只由摆动半幅 h 决定）
/// 再按 Ziegler–Nichols / Tyreus–Luyben 规则给出 PID 参数。
///
/// 偏置的意义：深冷/深热工作点维持温度本身就需要可观的稳态输出（−30℃ 实测 67%），
/// 围绕 0 摆动的继电根本压不住温度，小幅值直接"够不着"失败、大幅值振荡严重不对称
/// 使 Ku/Tu 失真。把继电中心放在该温度的稳态输出上（由前馈模型自动提供），
/// 摆动重新对称，小幅值即可得到教科书条件的测量。
///
/// 本类为纯算法：外部每个采样周期调用 Process(时间, 温度)，返回应输出的占空比。
/// 不含任何 IO，可用仿真对象做单元测试。
/// </summary>
public sealed class RelayAutoTuner(
    double setpointC,
    double relayAmplitudePercent = 30,
    double hysteresisC = 0.05,
    double biasPercent = 0,
    int requiredCycles = 3,
    int maxCycles = 10,
    double stallSeconds = 120,
    double stallDeltaC = 0.3,
    double adjustStepPercent = 5,
    double outputCeilingPercent = 100,
    double halfCycleTimeoutSeconds = 600,
    double outputFloorPercent = double.NaN,
    double passiveHalfTimeoutSeconds = 3600,
    double passiveStallSeconds = 300,
    double passiveStallDeltaC = 0.05,
    double passiveOutwardSeconds = 900)
{
    // 【本地改动】最后三个参数：单方向执行器（只加热的加热棒 / 只制冷的 TEC）的「被动半周」——
    // 那半周输出已经是 0、偏置也挪不动了，温度只能靠自然散热 / 自然回温漂回设定值。
    // 不能拿主动半周的尺子（2 min 内 0.3 ℃、半周 10 min）量它：只要还在往回走（5 min 内往回 0.05 ℃）
    // 就接着等，过冲还在往外走（5 min 内又冲出去 0.01 ℃ 以上）也接着等，从输出变 0 那一刻起最长等 1 h；
    // 输出变 0 都 15 min 了还在往外走，那不是执行器余热的过冲，是自然平衡点就在这一侧——不等了。
    private readonly List<double> _periods = [];    // 已完成整周期的周期（秒）
    private readonly List<double> _amplitudes = []; // 已完成整周期的振幅（℃）

    /// <summary>
    /// 输出边界与偏置可行域。outputFloorPercent 为 NaN（默认）时下限取 −上限（双向执行器）；
    /// 只加热执行器传 0：继电在 [偏置−幅值, 偏置+幅值] ⊆ [0, 上限] 内摆动——
    /// "弱加热半周"靠自然散热降温，振荡照常建立，Ku=4h/(πa) 公式不受影响。
    /// 幅值大到可行域为空时偏置收缩到量程中点（等效告知幅值应减小）。
    /// </summary>
    private static (double Floor, double Ceiling, double BiasMin, double BiasMax) Bounds(
        double amplitude, double ceilingPercent, double floorPercent)
    {
        var h = Math.Clamp(Math.Abs(amplitude), 1, 100);
        var ceiling = Math.Min(100, Math.Abs(ceilingPercent));
        var floor = double.IsNaN(floorPercent)
            ? -ceiling
            : Math.Clamp(floorPercent, -100, ceiling);
        var biasMin = floor + h;
        var biasMax = ceiling - h;
        if (biasMin > biasMax) biasMin = biasMax = (floor + ceiling) / 2;
        return (floor, ceiling, biasMin, biasMax);
    }

    private readonly (double Floor, double Ceiling, double BiasMin, double BiasMax) _bounds =
        Bounds(relayAmplitudePercent, outputCeilingPercent, outputFloorPercent);

    private int _sign;                    // +1 加热半周期，-1 制冷半周期
    private bool _started;
    private DateTime? _cycleStartTime;    // 当前整周期起点（切换到加热的时刻）
    private double _cycleMax, _cycleMin;  // 当前整周期内温度极值

    // Seeking 阶段的停滞检测（继电幅值不足以把温度推到设定值时提前失败）
    private DateTime _stallWindowStart;
    private double _stallWindowPv;
    private bool _stallWindowValid;

    /// <summary>最近一次继电切换（或启动/偏置调整）的时刻，半周期超时守门用。</summary>
    private DateTime _lastSwitchTime;

    /// <summary>【本地改动】这半周从哪一刻起变成被动的（输出 0、偏置挪不动）；不在被动半周时为 null。</summary>
    private DateTime? _passiveSince;

    /// <summary>【本地改动】被动半周自己的进展窗口（Seeking / Oscillating 都用：起步就在设定值之上时，第一次换向就进了 Oscillating）。</summary>
    private DateTime _passiveWindowStart;
    private double _passiveWindowPv;

    public double SetpointC { get; } = setpointC;
    public double RelayAmplitudePercent { get; } = Math.Clamp(Math.Abs(relayAmplitudePercent), 1, 100);
    public double HysteresisC { get; } = Math.Max(0.005, Math.Abs(hysteresisC));

    /// <summary>
    /// 继电中心（维持设定值所需的稳态输出，%）。输出 = 偏置 ± 幅值。
    /// 初值来自外部（前馈/最近稳态），但工况会变（现场实测：换了冷水机水温后，
    /// 历史存档偏置 −66% 与当日实际 −49% 相差超过继电幅值，弱制冷半周仍在净制冷，
    /// 温度永不回升穿越设定值，继电永不切换）。因此 Seeking 阶段带自寻中：
    /// 一个观察窗内温度在期望方向上无进展，就把中心向期望方向挪一步，
    /// 任何过时的初值都能自行爬回正确位置；中心顶到边仍无进展才判定"够不着"。
    /// 起振后（Oscillating）中心冻结，保证 Ku 测量条件不变。
    /// </summary>
    public double BiasPercent { get; private set; } = Math.Clamp(
        biasPercent,
        Bounds(relayAmplitudePercent, outputCeilingPercent, outputFloorPercent).BiasMin,
        Bounds(relayAmplitudePercent, outputCeilingPercent, outputFloorPercent).BiasMax);

    // 上下都按边界截：正常情况下偏置可行域已保证 偏置±幅值 在 [下限,上限] 内，
    // 只有幅值大到可行域塌缩（偏置被收到量程中点）时这里才真正咬合——
    // 宁可摆动不对称（整定精度打折），也不许输出越过用户上限
    private double Out() => Math.Clamp(BiasPercent + RelayAmplitudePercent * _sign,
        _bounds.Floor, _bounds.Ceiling);

    /// <summary>
    /// 【本地改动】这半周是不是被动的：输出已经是 0（加热棒关着 / 只制冷时 TEC 关着），
    /// 而且偏置朝期望方向也挪不动了——只能等温度自己漂回来。
    /// </summary>
    private bool PassiveHalf()
    {
        if (Math.Abs(Out()) > 1e-9) return false;
        var nudged = Math.Clamp(BiasPercent + _sign * adjustStepPercent, _bounds.BiasMin, _bounds.BiasMax);
        return Math.Abs(nudged - BiasPercent) <= 1e-9;
    }

    /// <summary>【本地改动】此刻在被动半周里（界面上说「等温度自己漂回来」用）。</summary>
    public bool InPassiveHalf => _started && State is AutoTuneState.Seeking or AutoTuneState.Oscillating && PassiveHalf();

    /// <summary>【本地改动】「够不着」的原话按方向说：要加热却升不上去 → 设定值改到极限温度之下，反之之上。</summary>
    private string StuckReason(double pv)
        => $"继电幅值 ±{RelayAmplitudePercent:F0}%（偏置已推到{(_sign > 0 ? "上" : "下")}限 {BiasPercent:F0}%）" +
           $"仍不足以使温度到达设定值 {SetpointC:F2}℃：温度停在 {pv:F2}℃ 附近。" +
           $"请提高继电幅值，或把设定值改到该极限温度之{(_sign > 0 ? "下" : "上")}（建议留 5℃ 余量）";

    /// <summary>
    /// 【本地改动】被动半周回不来的原话：不是幅值的事——这个温度离自然平衡点太近，或者平衡点就在设定值这一侧
    /// （比如只制冷的 TEC 去整比室温还高的温度：不制冷时它只会往下走）。
    /// </summary>
    private string PassiveReason(double pv, DateTime time)
        => $"这半周输出已经是 0（{(_sign < 0 ? "不加热，只能靠自然散热降温" : "不制冷，只能靠自然回温")}），" +
           $"{(time - (_passiveSince ?? _lastSwitchTime)).TotalMinutes:F0} min 里温度没能{(_sign < 0 ? "降" : "升")}回设定值 {SetpointC:F2}℃（此刻 {pv:F2}℃）。" +
           $"不{(_sign < 0 ? "加热" : "制冷")}时温度自己停的地方（自然平衡点）离这个温度太近，或者就在它{(_sign < 0 ? "上面" : "下面")}，" +
           "单方向的执行器在这儿整不出振荡——" +
           (_sign < 0
               ? "换一个比不加热时夹套会停的温度高得多的温度再整，或者改用 TEC 整"
               : "只制冷的 TEC 只能整比不制冷时夹套会停的温度（冷却水 / 室温附近）低得多的温度，高过它的用加热棒整") +
           "；提高继电幅值没有用";

    public AutoTuneState State { get; private set; } = AutoTuneState.Seeking;
    public string? FailReason { get; private set; }
    public AutoTuneResult? Result { get; private set; }

    /// <summary>已完成的测量周期数（不含被丢弃的首个过渡周期）。</summary>
    public int CompletedCycles => Math.Max(0, _periods.Count - 1);

    /// <summary>
    /// 送入一个采样，返回本拍应输出的占空比（%）。结束（成功/失败）后恒返回 0。
    /// </summary>
    public double Process(DateTime time, double pv)
    {
        if (State is AutoTuneState.Succeeded or AutoTuneState.Failed) return 0;

        if (!_started)
        {
            _started = true;
            // 初始方向：低于设定值则加热，反之制冷
            _sign = pv <= SetpointC ? +1 : -1;
            _stallWindowStart = time;
            _stallWindowPv = pv;
            _stallWindowValid = true;
            _lastSwitchTime = time;
            return Out();
        }

        // 半周期超时守门（Oscillating 同样有效）：过时偏置的挂死点有两个——
        // Seeking 段温度漂离永不首穿越，或首穿越轻松完成后某个半周的输出电平
        // 根本推不过切换阈值（其平衡温度落在阈值同侧），在 Oscillating 里永远
        // 等不到下一次切换。后者由此守门：半周期超时即挪偏置、清空已测周期、
        // 退回 Seeking 重新建立振荡——Ku 测量永远只用同一组电平下的完整周期。
        // 【本地改动】被动半周（输出 0、偏置挪不动）从变成被动那一刻起算，给得更长（缺省 1 h）。
        // Seeking 里的主动半周不走这道守门：那里有方向感知的停滞检测，有进展（2 min 往前 0.3 ℃）就一定
        // 走得到设定值，没进展它自己挪偏置 / 判失败。原来首次逼近慢一点（从室温升到 50 ℃ 要二三十分钟）
        // 也每 10 min 把偏置往外推一格，越过设定值时输出偏大、过冲更大，回头那半周更长。
        var passive = PassiveHalf();
        if (!passive) _passiveSince = null;
        else if (_passiveSince is null)
        {
            _passiveSince = time;
            _passiveWindowStart = time;
            _passiveWindowPv = pv;
        }
        var guarded = passive || State != AutoTuneState.Seeking;
        var halfLimit = passive ? Math.Max(halfCycleTimeoutSeconds, passiveHalfTimeoutSeconds) : halfCycleTimeoutSeconds;
        if (guarded && (time - (passive ? _passiveSince!.Value : _lastSwitchTime)).TotalSeconds >= halfLimit)
        {
            var nudged = Math.Clamp(BiasPercent + _sign * adjustStepPercent, _bounds.BiasMin, _bounds.BiasMax);
            if (Math.Abs(nudged - BiasPercent) <= 1e-9)
            {
                Fail(passive ? PassiveReason(pv, time) : StuckReason(pv));     // 【本地改动】原话按情形说
                return 0;
            }
            BiasPercent = nudged;
            _periods.Clear();
            _amplitudes.Clear();
            _cycleStartTime = null;
            State = AutoTuneState.Seeking;
            _stallWindowStart = time;
            _stallWindowPv = pv;
            _stallWindowValid = true;
            _lastSwitchTime = time;
        }

        // 尚未起振时监视"期望方向上的进展"（方向感知：温度不动或反向漂移都算无进展。
        // 现场教训：过时偏置下温度以 0.08℃/min 缓慢漂离，无方向的"停滞"检测会被
        // 不断重置的窗口骗过，Seeking 永远挂起）。
        if (passive)
        {
            // 【本地改动】被动半周（Seeking / Oscillating 都一样）：过冲还在往外走（又冲出去 passiveStallDeltaC/5 以上）
            // 就把窗口起点挪过去，往回走够 passiveStallDeltaC 算有进展；passiveStallSeconds 里两样都没有（停住了）判失败。
            // 输出变 0 都 passiveOutwardSeconds 了还在往外走，也判失败（平衡点在这一侧，执行器余热冲不了这么久）；
            // 一直慢慢往回走的由上面的被动半周期上限（1 h）收住
            var back = _sign * (pv - _passiveWindowPv);
            var outward = back <= -passiveStallDeltaC / 5;
            if (outward && (time - _passiveSince!.Value).TotalSeconds >= passiveOutwardSeconds)
            {
                Fail(PassiveReason(pv, time));
                return 0;
            }
            if (outward || back >= passiveStallDeltaC)
            {
                _passiveWindowStart = time;
                _passiveWindowPv = pv;
            }
            else if ((time - _passiveWindowStart).TotalSeconds >= passiveStallSeconds)
            {
                Fail(PassiveReason(pv, time));
                return 0;
            }
        }
        else if (State == AutoTuneState.Seeking && _stallWindowValid)
        {
            var progress = _sign * (pv - _stallWindowPv);   // 期望方向：加热半周应升温，制冷半周应降温
            if (progress >= stallDeltaC)
            {
                _stallWindowStart = time;
                _stallWindowPv = pv;
            }
            else if ((time - _stallWindowStart).TotalSeconds >= stallSeconds)
            {
                var nudged = Math.Clamp(BiasPercent + _sign * adjustStepPercent, _bounds.BiasMin, _bounds.BiasMax);
                if (Math.Abs(nudged - BiasPercent) > 1e-9)
                {
                    // 自寻中：当前半周推不动温度 → 继电中心向期望方向挪一步再试
                    BiasPercent = nudged;
                    _stallWindowStart = time;
                    _stallWindowPv = pv;
                }
                else
                {
                    Fail(StuckReason(pv));     // 【本地改动】原话按方向说
                    return 0;
                }
            }
        }

        if (_cycleStartTime is not null)
        {
            _cycleMax = Math.Max(_cycleMax, pv);
            _cycleMin = Math.Min(_cycleMin, pv);
        }

        // 继电切换（带回差）
        if (_sign > 0 && pv > SetpointC + HysteresisC)
        {
            _sign = -1; // 加热 → 制冷
            _lastSwitchTime = time;
            RestartStallWindow(time, pv);
        }
        else if (_sign < 0 && pv < SetpointC - HysteresisC)
        {
            _sign = +1; // 制冷 → 加热：一个整周期在此闭合
            _lastSwitchTime = time;
            RestartStallWindow(time, pv);
            OnFullCycleBoundary(time, pv);
        }

        return State is AutoTuneState.Succeeded or AutoTuneState.Failed
            ? 0
            : Out();
    }

    /// <summary>
    /// 【本地改动】继电一换向，「期望方向上的进展」从换向那一刻重新量。原来窗口起点留在换向之前：
    /// 刚越过设定值、过冲还在往外走的那一两分钟，拿换向前的温度当起点，进展是负的——两分钟一到就判
    /// 「推不动」（现场：加热棒 50 ℃ 越过设定值冲到 53.6 ℃，还没开始回落就失败了）。
    /// </summary>
    private void RestartStallWindow(DateTime time, double pv)
    {
        _passiveSince = null;
        if (State != AutoTuneState.Seeking) return;
        _stallWindowStart = time;
        _stallWindowPv = pv;
    }

    private void OnFullCycleBoundary(DateTime time, double pv)
    {
        if (_cycleStartTime is null)
        {
            // 第一次切回加热：正式开始测量，停滞检测退场
            State = AutoTuneState.Oscillating;
            _stallWindowValid = false;
        }
        else
        {
            var period = (time - _cycleStartTime.Value).TotalSeconds;
            var amplitude = (_cycleMax - _cycleMin) / 2.0;
            _periods.Add(period);
            _amplitudes.Add(amplitude);
            TryFinish();
        }

        _cycleStartTime = time;
        _cycleMax = _cycleMin = pv;
    }

    private void TryFinish()
    {
        // 丢弃首个过渡周期后，至少要有 requiredCycles 个测量周期
        if (CompletedCycles >= requiredCycles && IsStable(out var tu, out var amp))
        {
            if (amp <= HysteresisC)
            {
                Fail($"振荡振幅 {amp:F4}℃ 过小（不大于回差 {HysteresisC:F4}℃），" +
                     "测量不可信：请增大继电幅值或减小回差后重试");
                return;
            }

            var ku = 4.0 * RelayAmplitudePercent / (Math.PI * amp);

            // Ziegler–Nichols 经典 PID：Kp=0.6Ku, Ti=Tu/2, Td=Tu/8
            var fast = new PidGains(
                Kp: 0.6 * ku,
                Ki: 0.6 * ku / (0.5 * tu),
                Kd: 0.6 * ku * tu / 8.0);

            // Tyreus–Luyben：Kp=0.45Ku, Ti=2.2Tu, Td=Tu/6.3（温控推荐，平稳）
            var conservative = new PidGains(
                Kp: 0.45 * ku,
                Ki: 0.45 * ku / (2.2 * tu),
                Kd: 0.45 * ku * tu / 6.3);

            Result = new AutoTuneResult(ku, tu, RelayAmplitudePercent, amp, fast, conservative);
            State = AutoTuneState.Succeeded;
            return;
        }

        if (CompletedCycles >= maxCycles)
            Fail($"经过 {maxCycles} 个周期振荡仍不稳定，" +
                 "请增大继电幅值（增强激励）或增大回差（抑制噪声）后重试");
    }

    /// <summary>取最近 requiredCycles 个周期，检查一致性并输出平均值。</summary>
    private bool IsStable(out double meanPeriod, out double meanAmplitude)
    {
        var periods = _periods.TakeLast(requiredCycles).ToArray();
        var amplitudes = _amplitudes.TakeLast(requiredCycles).ToArray();
        meanPeriod = periods.Average();
        meanAmplitude = amplitudes.Average();

        var periodSpread = (periods.Max() - periods.Min()) / meanPeriod;
        var ampSpread = meanAmplitude > 0 ? (amplitudes.Max() - amplitudes.Min()) / meanAmplitude : 1.0;
        return periodSpread < 0.25 && ampSpread < 0.35;
    }

    private void Fail(string reason)
    {
        State = AutoTuneState.Failed;
        FailReason = reason;
    }
}
