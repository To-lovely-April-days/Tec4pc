namespace TecControl.Core.Control;

/// <summary>一个已学习到的稳态工作点。</summary>
public sealed record SteadyPoint(double SetpointC, double OutputPercent);

/// <summary>
/// 稳态前馈：自动学习"维持某温度需要多少输出"，供任意设定值启动时预置积分。
///
/// 物理依据：稳态时 TEC 泵热量与漏热平衡，所需输出近似与温差成正比：
///     u_ss ≈ (T_env − T_set) / K
/// 因此 u_ss 对 T_set 近似线性。本类在每个稳态点记录 (设定值, 输出)，
/// 用最小二乘拟合直线 u = a + b·T_set，之后对任意设定值（含从未跑过的）给出预测。
///
/// 覆盖宽温域（如 0 ~ −40℃）时无需人工建表：跑过的点越多，拟合越准；
/// 仅有 1 个点时退化为"同一设定值直接复用"，无点时返回 null（从零累积）。
/// </summary>
public sealed class SteadyStateFeedforward
{
    private readonly object _lock = new();
    private readonly List<SteadyPoint> _points = [];

    /// <summary>两个学习点的设定值相差小于此值时视为同一工作点（新值覆盖旧值）。</summary>
    public double MergeBandC { get; init; } = 1.0;

    /// <summary>
    /// 执行器分段边界（℃）：≥100℃ 加热执行器从 TEC 换成电加热棒，输出百分比是
    /// 另一套执行器的量纲。学习与预测一律不跨段——把加热棒的稳态点混进 TEC 的
    /// 拟合直线会把两段的预测都拉歪。设为 NaN 关闭分段。与 PidGainSchedule 同规则。
    /// </summary>
    public double SegmentBoundaryC { get; set; } = 100.0;

    private bool SameSegment(double a, double b) =>
        double.IsNaN(SegmentBoundaryC) || (a >= SegmentBoundaryC) == (b >= SegmentBoundaryC);

    /// <summary>最多保留的工作点数（超出后淘汰最早的）。</summary>
    public int Capacity { get; init; } = 32;

    /// <summary>外推时允许超出已学习设定值范围的最大距离（℃），超出则按边界截断。</summary>
    public double ExtrapolationLimitC { get; init; } = 25;

    public IReadOnlyList<SteadyPoint> Points
    {
        get { lock (_lock) return _points.ToArray(); }
    }

    /// <summary>记录（或更新）一个稳态工作点。合并覆盖不跨执行器段（99.5℃ 不吞并 100.5℃）。</summary>
    public void Learn(double setpointC, double outputPercent)
    {
        lock (_lock)
        {
            var idx = _points.FindIndex(p => Math.Abs(p.SetpointC - setpointC) <= MergeBandC
                                             && SameSegment(p.SetpointC, setpointC));
            if (idx >= 0)
                _points[idx] = new SteadyPoint(setpointC, outputPercent);
            else
            {
                _points.Add(new SteadyPoint(setpointC, outputPercent));
                if (_points.Count > Capacity) _points.RemoveAt(0);
            }
        }
    }

    /// <summary>
    /// 预测维持该设定值所需的稳态输出；该设定值所在执行器段内无可用数据时返回 null
    /// （决不拿另一段——另一套执行器——的点来拟合）。
    /// </summary>
    public double? Predict(double setpointC)
    {
        lock (_lock)
        {
            var usable = _points.Where(p => SameSegment(p.SetpointC, setpointC)).ToList();
            if (usable.Count == 0) return null;
            if (usable.Count == 1)
            {
                // 只有一个点：仅在附近沿用，远离时不敢外推
                var only = usable[0];
                return Math.Abs(only.SetpointC - setpointC) <= ExtrapolationLimitC
                    ? only.OutputPercent
                    : null;
            }

            // 最小二乘拟合 u = a + b·T
            var n = usable.Count;
            double sx = 0, sy = 0, sxx = 0, sxy = 0;
            foreach (var p in usable)
            {
                sx += p.SetpointC; sy += p.OutputPercent;
                sxx += p.SetpointC * p.SetpointC; sxy += p.SetpointC * p.OutputPercent;
            }
            var den = n * sxx - sx * sx;
            if (Math.Abs(den) < 1e-9)
                return usable[^1].OutputPercent; // 所有点设定值相同

            var b = (n * sxy - sx * sy) / den;
            var a = (sy - b * sx) / n;

            // 限制外推距离，避免远离已知区间时给出离谱的预置
            var min = usable.Min(p => p.SetpointC);
            var max = usable.Max(p => p.SetpointC);
            var clamped = Math.Clamp(setpointC, min - ExtrapolationLimitC, max + ExtrapolationLimitC);
            return a + b * clamped;
        }
    }

    public void Clear()
    {
        lock (_lock) _points.Clear();
    }

    /// <summary>供界面显示的摘要，如 "3 个工作点：-10.0℃→34.2%, ..."。</summary>
    public string Describe()
    {
        lock (_lock)
        {
            if (_points.Count == 0) return "尚无稳态记录";
            var items = _points.OrderByDescending(p => p.SetpointC)
                .Select(p => $"{p.SetpointC:F1}℃→{p.OutputPercent:F1}%");
            return $"{_points.Count} 个工作点：{string.Join("，", items)}";
        }
    }
}
