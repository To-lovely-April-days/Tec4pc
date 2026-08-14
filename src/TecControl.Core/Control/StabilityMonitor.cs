namespace TecControl.Core.Control;

/// <summary>滚动窗口内的稳定度统计结果。</summary>
public sealed record StabilityStats(
    int SampleCount,
    double WindowSeconds,
    double MeanC,
    double PeakToPeakC,
    double StdDevC,
    double MaxDeviationC,
    double MinC,
    double MaxC)
{
    /// <summary>习惯表述的 ±值（峰峰值的一半）。</summary>
    public double PlusMinusC => PeakToPeakC / 2;
}

/// <summary>
/// 控温稳定度实时统计：维护最近一段时间的温度滚动窗口，给出峰峰值/标准差/最大偏差。
/// 界面直接显示，无需事后用 Excel 处理 CSV，也便于验收时读数。
/// </summary>
public sealed class StabilityMonitor(double windowSeconds = 300)
{
    private readonly object _lock = new();
    private readonly Queue<(DateTime Time, double TempC)> _samples = new();

    public double WindowSeconds { get; } = windowSeconds;

    public void Add(DateTime time, double tempC)
    {
        if (double.IsNaN(tempC)) return;
        lock (_lock)
        {
            _samples.Enqueue((time, tempC));
            while (_samples.Count > 0 && (time - _samples.Peek().Time).TotalSeconds > WindowSeconds)
                _samples.Dequeue();
        }
    }

    public void Reset()
    {
        lock (_lock) _samples.Clear();
    }

    /// <summary>统计当前窗口；样本不足 2 个时返回 null。</summary>
    public StabilityStats? Compute(double setpointC)
    {
        lock (_lock)
        {
            if (_samples.Count < 2) return null;

            var temps = _samples.Select(s => s.TempC).ToArray();
            var mean = temps.Average();
            var min = temps.Min();
            var max = temps.Max();
            var variance = temps.Sum(t => (t - mean) * (t - mean)) / temps.Length;
            var maxDev = temps.Max(t => Math.Abs(t - setpointC));
            var span = (_samples.Last().Time - _samples.First().Time).TotalSeconds;

            return new StabilityStats(temps.Length, span, mean, max - min, Math.Sqrt(variance), maxDev,
                min, max);
        }
    }

    /// <summary>窗口是否已填满（统计结果是否具有代表性）。</summary>
    public bool IsWindowFull
    {
        get
        {
            lock (_lock)
            {
                if (_samples.Count < 2) return false;
                return (_samples.Last().Time - _samples.First().Time).TotalSeconds >= WindowSeconds * 0.9;
            }
        }
    }
}
