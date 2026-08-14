namespace TecControl.Core.Control;

/// <summary>
/// 时间窗滑动平均，用于把测温链路的电子噪声平均掉、呈现真实温度。
///
/// 正当性（实测论证，2026-07-30）：核心热质量时间常数 τ≈700s，稳态时腔体仅在
/// ±0.15℃ 内波动，由 dTc/dt=(Tw−Tc)/τ 可得真实温度 10 秒内最多移动 ~2mK；
/// 而读数上的秒级抖动 σ≈8mK 经双探头互相关（r=0.07，不相关）与频谱分解证明
/// 是各自测温电路的电子噪声。因此窗口内真实温度近似恒定，平均只消噪声，
/// 不会掩盖任何物理上可能发生的真实变化。
/// </summary>
public sealed class SlidingAverage(double windowSeconds = 10)
{
    private readonly object _lock = new();
    private readonly Queue<(DateTime Time, double Value)> _samples = new();

    public double WindowSeconds { get; } = windowSeconds;

    /// <summary>加入一个采样并返回当前窗口均值；NaN 输入被忽略。</summary>
    public double Add(DateTime time, double value)
    {
        if (double.IsNaN(value)) return Value;
        lock (_lock)
        {
            _samples.Enqueue((time, value));
            while (_samples.Count > 0 &&
                   (time - _samples.Peek().Time).TotalSeconds > WindowSeconds)
                _samples.Dequeue();
            return _samples.Average(s => s.Value);
        }
    }

    /// <summary>当前窗口均值；无样本时为 NaN。</summary>
    public double Value
    {
        get
        {
            lock (_lock) return _samples.Count == 0 ? double.NaN : _samples.Average(s => s.Value);
        }
    }

    /// <summary>窗口是否已填满（均值是否具有全窗代表性）。</summary>
    public bool IsWindowFull
    {
        get
        {
            lock (_lock)
            {
                if (_samples.Count < 2) return false;
                return (_samples.Last().Time - _samples.First().Time).TotalSeconds
                       >= WindowSeconds * 0.8;
            }
        }
    }

    public void Reset()
    {
        lock (_lock) _samples.Clear();
    }
}
