namespace TecControl.Core.Control;

/// <summary>串级外环在某温度下的整定：增益 + 偏置限幅。</summary>
public sealed record OuterTuning(PidGains Gains, double MaxBiasC);

/// <summary>
/// 增益表中的一个工作点：某温度下的全套控制参数。
/// </summary>
/// <param name="TemperatureC">该工作点的温度（℃）。</param>
/// <param name="Gains">内环（腔体）PID 参数。</param>
/// <param name="UltimateGainKu">自整定得到的临界增益；手工点为 0。</param>
/// <param name="UltimatePeriodTuSeconds">自整定得到的临界周期（秒）；手工点为 0。</param>
/// <param name="OuterGains">串级外环 PID 参数；null 表示该点未登记外环参数（沿用手动值）。</param>
/// <param name="OuterMaxBiasC">串级外环偏置限幅（℃）；0 表示未登记。</param>
/// <param name="SteadyBiasC">
/// 该温度下实测的稳态偏置（腔体设定值 − 内部设定值，℃）。运行中自动学习，
/// 供下次在同温度启动串级时预置外环积分，省去偏置从零累积的等待。null 表示尚未学到。
/// </param>
/// <param name="HeatRatio">
/// 该温度下加热相对制冷的有效度比值（正输出除以该值做执行器对称归一化）。null 表示
/// 未登记（沿用全局值）。>100℃ 段加热换成 PWM 电加热器后此比值与低温段完全不同，
/// 按温度调度才能两段各得其所。
/// </param>
public sealed record GainPoint(
    double TemperatureC,
    PidGains Gains,
    double UltimateGainKu = 0,
    double UltimatePeriodTuSeconds = 0,
    PidGains? OuterGains = null,
    double OuterMaxBiasC = 0,
    double? SteadyBiasC = null,
    double? HeatRatio = null);

/// <summary>
/// 增益调度：TEC 的过程增益随温度显著变化（温差越大 COP 越低，同样 1% 输出换来的温降越小），
/// 单组 PID 参数无法在宽温域（如 +200 ~ −40℃）全程最优。
///
/// 做法：每次在某温度自整定成功后自动登记一个工作点；运行时按当前设定值在相邻工作点之间
/// 线性插值出参数。跑过的点越多越准；只有 1 个点时退化为固定参数（即原有行为）；
/// 设定值超出已登记范围时取最近端点的参数（不外推，避免给出危险增益）。
///
/// 每个工作点同时登记三样随温度变化的东西：
///   ① 内环（腔体）PID 参数；
///   ② 串级外环 PID 参数与偏置限幅——外环快慢取决于"腔体→内部"的传热速度，
///      低温段系统整体变慢，外环也必须跟着变慢，否则会像 −10℃ 那次一样下冲后久久收不回来；
///   ③ 稳态偏置——维持该温度时腔体需要比内部低（或高）多少度，用于启动时预置外环积分；
///   ④ 加热比（可选）——执行器不对称归一化的比值，>100℃ 段换 PWM 电加热器后与低温段不同。
///
/// 分段：≥100℃ 加热执行器从 TEC 换成 PWM 电加热器（见 SegmentBoundaryC），
/// 一切插值与就近取点不跨段——高温段没登记过点时回退手动参数，提示先在该段自整定。
/// </summary>
public sealed class PidGainSchedule
{
    private readonly object _lock = new();
    private readonly List<GainPoint> _points = [];

    /// <summary>两个工作点温度相差小于此值时视为同一点（新值覆盖旧值）。</summary>
    public double MergeBandC { get; init; } = 2.0;

    /// <summary>
    /// 执行器分段边界（℃）：本机 ≥100℃ 后加热从 TEC 换成 PWM 电加热器（制冷仍是 TEC），
    /// 过程增益在边界处不连续。插值、就近取点、合并覆盖一律不跨段——99℃ 的参数对
    /// 101℃ 是另一套执行器的参数，混在一起插值只会两边都不对。
    /// 设为 NaN 可关闭分段（恢复全域插值）。
    /// </summary>
    public double SegmentBoundaryC { get; set; } = 100.0;

    /// <summary>t 所在的执行器段：低温段 0（&lt;边界），高温段 1（≥边界）。</summary>
    private int SegmentOf(double t) =>
        !double.IsNaN(SegmentBoundaryC) && t >= SegmentBoundaryC ? 1 : 0;

    private bool SameSegment(double a, double b) => SegmentOf(a) == SegmentOf(b);

    public IReadOnlyList<GainPoint> Points
    {
        get { lock (_lock) return _points.OrderByDescending(p => p.TemperatureC).ToArray(); }
    }

    public int Count
    {
        get { lock (_lock) return _points.Count; }
    }

    /// <summary>
    /// 登记（或更新）一个工作点，通常由自整定成功后自动调用。
    /// 覆盖同温度旧点时，新点未提供的外环参数与稳态偏置沿用旧值——重新自整定内环
    /// 不应抹掉现场手工调好的外环参数，稳态偏置更是与 PID 参数无关的物理量。
    /// </summary>
    public void Learn(GainPoint point)
    {
        lock (_lock)
        {
            var idx = FindNear(point.TemperatureC);
            if (idx < 0) { _points.Add(point); return; }

            var old = _points[idx];
            _points[idx] = point with
            {
                OuterGains = point.OuterGains ?? old.OuterGains,
                OuterMaxBiasC = point.OuterGains is null && point.OuterMaxBiasC <= 0
                    ? old.OuterMaxBiasC
                    : point.OuterMaxBiasC,
                SteadyBiasC = point.SteadyBiasC ?? old.SteadyBiasC,
                HeatRatio = point.HeatRatio ?? old.HeatRatio,
            };
        }
    }

    /// <summary>合并带内且同段的已有点下标；没有返回 −1。99.5℃ 不得吞并 100.5℃ 的点。</summary>
    private int FindNear(double temperatureC) => _points.FindIndex(p =>
        Math.Abs(p.TemperatureC - temperatureC) <= MergeBandC
        && SameSegment(p.TemperatureC, temperatureC));

    /// <summary>
    /// 记录某温度实测到的稳态偏置。只更新已存在的工作点（表里没有该温度就不建点，
    /// 避免运行中凭空塞入没整定过的参数行）。返回是否落到了某个工作点上。
    /// </summary>
    /// <param name="changeThresholdC">
    /// 小于此幅度的变化视为噪声，不算"变了"（返回 false）。学习每 2 秒发生一次，
    /// 若每次都算变更会把持久化触发得过于频繁。
    /// </param>
    public bool LearnSteadyBias(double temperatureC, double biasC, double changeThresholdC = 0.05)
    {
        lock (_lock)
        {
            var idx = FindNear(temperatureC);
            if (idx < 0) return false;

            // 顺带保证限幅够用：既然实测维持该温度就要这么大偏置，限幅至少要在此之上留出
            // 抗扰余量，否则下次到这个温度会卡在限幅上永远差一截（只放宽不收窄）。
            var old = _points[idx];
            var newLimit = old.OuterGains is null
                ? old.OuterMaxBiasC
                : Math.Max(old.OuterMaxBiasC, Math.Abs(biasC) + 4);

            var changed = old.SteadyBiasC is not { } prev
                          || Math.Abs(prev - biasC) > changeThresholdC
                          || newLimit > old.OuterMaxBiasC;

            _points[idx] = old with { SteadyBiasC = biasC, OuterMaxBiasC = newLimit };
            return changed;
        }
    }

    /// <summary>
    /// 给尚未登记外环参数的工作点补上一组推荐值（按各自的 Tu 推算），返回补全的点数。
    /// 用于把旧版只有内环参数的增益表一次性升级到带外环调度，无需手工逐行填写。
    /// 已有外环参数的点原样保留。
    /// </summary>
    public int EnsureOuterGains()
    {
        lock (_lock)
        {
            var filled = 0;
            for (var i = 0; i < _points.Count; i++)
            {
                if (_points[i].OuterGains is not null) continue;
                var o = SuggestOuter(_points[i].UltimatePeriodTuSeconds);
                // 已学到稳态偏置的点，限幅按实测需求放宽
                var limit = _points[i].SteadyBiasC is { } b
                    ? Math.Max(o.MaxBiasC, Math.Abs(b) + 4)
                    : o.MaxBiasC;
                _points[i] = _points[i] with { OuterGains = o.Gains, OuterMaxBiasC = limit };
                filled++;
            }
            return filled;
        }
    }

    /// <summary>放宽某温度点的偏置限幅（外环自动扩程时调用；只放宽不收窄）。</summary>
    public bool WidenOuterBiasLimit(double temperatureC, double newLimitC)
    {
        lock (_lock)
        {
            var idx = FindNear(temperatureC);
            if (idx < 0 || _points[idx].OuterGains is null) return false;
            if (newLimitC <= _points[idx].OuterMaxBiasC) return false;
            _points[idx] = _points[idx] with { OuterMaxBiasC = newLimitC };
            return true;
        }
    }

    public void Remove(double temperatureC)
    {
        lock (_lock) _points.RemoveAll(p =>
            Math.Abs(p.TemperatureC - temperatureC) <= MergeBandC
            && SameSegment(p.TemperatureC, temperatureC));
    }

    public void Clear()
    {
        lock (_lock) _points.Clear();
    }

    /// <summary>
    /// 求某设定值应使用的内环 PID 参数；表为空时返回 null（调用方沿用当前参数）。
    /// </summary>
    public PidGains? GainsAt(double setpointC)
    {
        lock (_lock)
        {
            return Interpolate(_points, setpointC,
                p => true,
                p => p.Gains,
                (lo, hi, f) => new PidGains(
                    Lerp(lo.Gains.Kp, hi.Gains.Kp, f),
                    Lerp(lo.Gains.Ki, hi.Gains.Ki, f),
                    Lerp(lo.Gains.Kd, hi.Gains.Kd, f)));
        }
    }

    /// <summary>
    /// 求某设定值应使用的串级外环整定；没有任何点登记过外环参数时返回 null
    /// （调用方沿用界面上的手动外环参数）。
    /// </summary>
    public OuterTuning? OuterAt(double setpointC)
    {
        lock (_lock)
        {
            return Interpolate(_points, setpointC,
                p => p.OuterGains is not null && p.OuterMaxBiasC > 0,
                p => new OuterTuning(p.OuterGains!, p.OuterMaxBiasC),
                (lo, hi, f) => new OuterTuning(
                    new PidGains(
                        Lerp(lo.OuterGains!.Kp, hi.OuterGains!.Kp, f),
                        Lerp(lo.OuterGains!.Ki, hi.OuterGains!.Ki, f),
                        Lerp(lo.OuterGains!.Kd, hi.OuterGains!.Kd, f)),
                    Lerp(lo.OuterMaxBiasC, hi.OuterMaxBiasC, f)));
        }
    }

    /// <summary>
    /// 求某设定值的稳态偏置预测（用于启动串级时预置外环积分）；无数据返回 null。
    /// </summary>
    public double? SteadyBiasAt(double setpointC)
    {
        lock (_lock)
        {
            return Interpolate<double?>(_points, setpointC,
                p => p.SteadyBiasC is not null,
                p => p.SteadyBiasC,
                (lo, hi, f) => Lerp(lo.SteadyBiasC!.Value, hi.SteadyBiasC!.Value, f));
        }
    }

    /// <summary>
    /// 求某设定值的加热有效度比值（执行器不对称归一化用）；没有任何点登记过时
    /// 返回 null（调用方沿用全局值）。与其它量一样不跨执行器段插值。
    /// </summary>
    public double? HeatRatioAt(double setpointC)
    {
        lock (_lock)
        {
            return Interpolate<double?>(_points, setpointC,
                p => p.HeatRatio is > 0,
                p => p.HeatRatio,
                (lo, hi, f) => Lerp(lo.HeatRatio!.Value, hi.HeatRatio!.Value, f));
        }
    }

    /// <summary>
    /// 由内环自整定结果推荐一组外环起始参数。
    ///
    /// 依据：串级要求外环明显慢于内环，否则两环互相追赶产生振荡。内环临界周期 Tu
    /// 就是该温度下系统快慢的直接度量（低温段 Tu 变长 → 外环自动变慢），因此取
    ///   外环 Kp = 2.5 ℃/℃（腔体每偏离 1℃，腔体设定值反向修正 2.5℃）
    ///   外环 Ti = 1.7·Tu → Ki = Kp/Ti
    ///   外环 Kd = 0（内环已是快速回路，外环再加微分只会放大噪声）
    ///   偏置限幅 = 8℃（不够用时由 OuterBiasRangeGuard 在运行中自动放宽）
    ///
    /// 这组系数是在辨识出的双容对象（壁面 τ≈16min/纯滞后 48s、内部 τ≈8.6min）上扫参得到的：
    /// 相比原来的 1.5/0.0025/±15，进入 ±0.05℃ 的时间由 75.8min 缩短到 37.4min，
    /// 下冲由 0.24℃ 降到 0.04℃；对象参数 ±50% 失配时仍单调收敛不振荡。
    /// 仍是起点不是终点：现场若到温慢可加大 Kp，若下冲明显可缩小限幅。
    /// </summary>
    public static OuterTuning SuggestOuter(double ultimatePeriodTuSeconds,
        double kp = 2.5, double maxBiasC = 8)
    {
        // Tu 异常（自整定失败或手工点）时退回一个保守的 600s 积分时间
        var ti = ultimatePeriodTuSeconds is > 30 and < 3600 ? 1.7 * ultimatePeriodTuSeconds : 600;
        return new OuterTuning(new PidGains(kp, kp / ti, 0), Math.Abs(maxBiasC));
    }

    /// <summary>
    /// 通用插值骨架：在满足 <paramref name="filter"/> 且与设定值同一执行器段的工作点中，
    /// 按温度线性插值；落在区间外时取最近端点（不外推），可用点少于 2 个时分别退化为
    /// 单点/无值。设定值所在段没有任何可用点时返回无值（调用方回退手动参数）——
    /// 决不允许把另一段（另一套执行器）的参数拿来就近顶替。
    /// </summary>
    private T? Interpolate<T>(List<GainPoint> all, double setpointC,
        Func<GainPoint, bool> filter, Func<GainPoint, T?> single,
        Func<GainPoint, GainPoint, double, T> blend)
    {
        var usable = all.Where(p => filter(p) && SameSegment(p.TemperatureC, setpointC))
            .OrderBy(p => p.TemperatureC).ToList();
        if (usable.Count == 0) return default;
        if (usable.Count == 1) return single(usable[0]);

        if (setpointC <= usable[0].TemperatureC) return single(usable[0]);
        if (setpointC >= usable[^1].TemperatureC) return single(usable[^1]);

        for (var i = 1; i < usable.Count; i++)
        {
            var lo = usable[i - 1];
            var hi = usable[i];
            if (setpointC > hi.TemperatureC) continue;

            var span = hi.TemperatureC - lo.TemperatureC;
            var f = span > 1e-9 ? (setpointC - lo.TemperatureC) / span : 0;
            return blend(lo, hi, f);
        }
        return single(usable[^1]);
    }

    private static double Lerp(double a, double b, double f) => a + (b - a) * f;

    /// <summary>供界面显示的摘要。</summary>
    public string Describe()
    {
        lock (_lock)
        {
            if (_points.Count == 0) return "增益表为空（使用当前手动参数）";
            var items = _points.OrderByDescending(p => p.TemperatureC)
                .Select(p => $"{p.TemperatureC:F1}℃(Kp={p.Gains.Kp:F2})");
            return $"{_points.Count} 个温度点：{string.Join("，", items)}" +
                   (_points.Count == 1 ? "（再在其它温度自整定一次即可开始插值）" : "");
        }
    }
}
