namespace TecControl.Core.Control;

/// <summary>
/// 设定值生成层：把"目标温度从何而来"独立成模块。
/// 手动设定 → 无 profile；时间驱动（结晶降温曲线）→ SegmentProfile / TableProfile。
/// </summary>
public interface ITemperatureProfile
{
    /// <summary>曲线名称（界面显示用）。</summary>
    string Name { get; }

    /// <summary>从起始温度算起的总时长（分钟）。</summary>
    double TotalMinutes(double startC);

    /// <summary>
    /// 求 elapsedMinutes 时刻的设定值；返回 null 表示曲线已执行完毕。
    /// </summary>
    double? SetpointAt(double elapsedMinutes, double startC);
}

/// <summary>
/// 一个曲线段：以 RateCPerMin 的速率变温到 TargetC，随后恒温 HoldMinutes。
/// 单段即"线性降温"，多段即"阶梯降温"；RateCPerMin=0 表示立即阶跃到目标值。
/// </summary>
public sealed record ProfileSegment(double TargetC, double RateCPerMin, double HoldMinutes = 0)
{
    /// <summary>协议与工艺要求的速率范围：0.05~5 ℃/min（0 表示阶跃）。</summary>
    public const double MinRate = 0.05;
    public const double MaxRate = 5.0;

    public void Validate()
    {
        if (RateCPerMin < 0)
            throw new ArgumentOutOfRangeException(nameof(RateCPerMin), "变温速率不能为负");
        if (RateCPerMin > 0 && (RateCPerMin < MinRate || RateCPerMin > MaxRate))
            throw new ArgumentOutOfRangeException(nameof(RateCPerMin),
                $"变温速率应在 {MinRate}~{MaxRate} ℃/min 之间（0 表示阶跃）");
        if (HoldMinutes < 0)
            throw new ArgumentOutOfRangeException(nameof(HoldMinutes), "恒温时长不能为负");
    }

    public double RampMinutesFrom(double fromC) =>
        RateCPerMin > 0 ? Math.Abs(TargetC - fromC) / RateCPerMin : 0;
}

/// <summary>分段式温度曲线：覆盖线性降温与阶梯降温。</summary>
public sealed class SegmentProfile : ITemperatureProfile
{
    private readonly List<ProfileSegment> _segments;

    public SegmentProfile(IEnumerable<ProfileSegment> segments, string name = "分段曲线")
    {
        _segments = segments.ToList();
        if (_segments.Count == 0)
            throw new ArgumentException("曲线至少需要一段", nameof(segments));
        foreach (var s in _segments) s.Validate();
        Name = name;
    }

    public string Name { get; }
    public IReadOnlyList<ProfileSegment> Segments => _segments;

    /// <summary>线性降温：单段，从当前温度按固定速率到终止温度，可选保温。</summary>
    public static SegmentProfile Linear(double targetC, double rateCPerMin, double holdMinutes = 0) =>
        new([new ProfileSegment(targetC, rateCPerMin, holdMinutes)], "线性降温");

    public double TotalMinutes(double startC)
    {
        double total = 0, cur = startC;
        foreach (var s in _segments)
        {
            total += s.RampMinutesFrom(cur) + s.HoldMinutes;
            cur = s.TargetC;
        }
        return total;
    }

    public double? SetpointAt(double elapsedMinutes, double startC)
    {
        if (elapsedMinutes < 0) return startC;

        double t = 0, cur = startC;
        foreach (var s in _segments)
        {
            var ramp = s.RampMinutesFrom(cur);
            if (elapsedMinutes < t + ramp)
            {
                var frac = ramp > 0 ? (elapsedMinutes - t) / ramp : 1.0;
                return cur + (s.TargetC - cur) * frac;
            }
            t += ramp;

            if (elapsedMinutes < t + s.HoldMinutes) return s.TargetC;
            t += s.HoldMinutes;
            cur = s.TargetC;
        }
        // 恰好到达终点时返回终值，越过后才判定执行完毕
        return elapsedMinutes <= t + 1e-9 ? cur : null;
    }
}

/// <summary>自定义曲线：用户给出 (时间, 温度) 数据点，中间线性插值。</summary>
public sealed class TableProfile : ITemperatureProfile
{
    private readonly List<(double Minutes, double TempC)> _points;

    public TableProfile(IEnumerable<(double Minutes, double TempC)> points, string name = "自定义曲线")
    {
        _points = points.OrderBy(p => p.Minutes).ToList();
        if (_points.Count < 2)
            throw new ArgumentException("自定义曲线至少需要 2 个数据点", nameof(points));
        Name = name;
    }

    public string Name { get; }
    public IReadOnlyList<(double Minutes, double TempC)> Points => _points;

    public double TotalMinutes(double startC) => _points[^1].Minutes;

    public double? SetpointAt(double elapsedMinutes, double startC)
    {
        if (elapsedMinutes <= _points[0].Minutes) return _points[0].TempC;
        if (elapsedMinutes > _points[^1].Minutes) return null;

        for (var i = 1; i < _points.Count; i++)
        {
            var (t1, v1) = _points[i - 1];
            var (t2, v2) = _points[i];
            if (elapsedMinutes <= t2)
            {
                var span = t2 - t1;
                var frac = span > 0 ? (elapsedMinutes - t1) / span : 1.0;
                return v1 + (v2 - v1) * frac;
            }
        }
        return _points[^1].TempC;
    }

    /// <summary>从 CSV 文本解析（每行 "分钟,温度"，允许表头与空行）。</summary>
    public static TableProfile FromCsv(string text, string name = "自定义曲线")
    {
        var pts = new List<(double, double)>();
        foreach (var line in text.Split('\n'))
        {
            var parts = line.Trim().Split(',');
            if (parts.Length < 2) continue;
            if (double.TryParse(parts[0], out var m) && double.TryParse(parts[1], out var v))
                pts.Add((m, v));
        }
        return new TableProfile(pts, name);
    }
}
