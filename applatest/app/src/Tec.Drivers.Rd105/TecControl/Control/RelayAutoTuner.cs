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
    double outputFloorPercent = double.NaN)
{
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
        if ((time - _lastSwitchTime).TotalSeconds >= halfCycleTimeoutSeconds)
        {
            var nudged = Math.Clamp(BiasPercent + _sign * adjustStepPercent, _bounds.BiasMin, _bounds.BiasMax);
            if (Math.Abs(nudged - BiasPercent) <= 1e-9)
            {
                Fail($"继电幅值 ±{RelayAmplitudePercent:F0}%（偏置已推至 {BiasPercent:F0}% 上限）" +
                     $"仍不足以使温度到达设定值 {SetpointC:F2}℃：温度停在 {pv:F2}℃ 附近。" +
                     "请提高继电幅值，或把设定值改到该极限温度之上（建议留 5℃ 余量）");
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
        if (State == AutoTuneState.Seeking && _stallWindowValid)
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
                    Fail($"继电幅值 ±{RelayAmplitudePercent:F0}%（偏置已推至 {BiasPercent:F0}% 上限）" +
                         $"仍不足以使温度到达设定值 {SetpointC:F2}℃：温度停在 {pv:F2}℃ 附近。" +
                         "请提高继电幅值，或把设定值改到该极限温度之上（建议留 5℃ 余量）");
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
        }
        else if (_sign < 0 && pv < SetpointC - HysteresisC)
        {
            _sign = +1; // 制冷 → 加热：一个整周期在此闭合
            _lastSwitchTime = time;
            OnFullCycleBoundary(time, pv);
        }

        return State is AutoTuneState.Succeeded or AutoTuneState.Failed
            ? 0
            : Out();
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
