using System.Diagnostics;

namespace Tec.Core;

/// <summary>
/// 单调时钟 + 墙钟对照（§8.1）。仿真时按 Rate 倍速推进——
/// 记录里的时间全部走这一份，"实际时长"与"计划时长"才可比。
/// </summary>
public sealed class VirtualClock
{
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private readonly DateTimeOffset _origin;
    private TimeSpan _carried = TimeSpan.Zero;   // 改速率之前已经走过的虚拟时间
    private double _rate = 1;

    public VirtualClock(DateTimeOffset? origin = null) => _origin = origin ?? DateTimeOffset.Now;

    /// <summary>1 = 实时。改速率时保住已经走过的时间，曲线不会跳。</summary>
    public double Rate
    {
        get => _rate;
        set
        {
            if (value <= 0) value = 1;
            _carried = Elapsed;
            _sw.Restart();
            _rate = value;
        }
    }

    public TimeSpan Elapsed => _carried + TimeSpan.FromTicks((long)(_sw.Elapsed.Ticks * _rate));

    public DateTimeOffset Now => _origin + Elapsed;

    /// <summary>单调戳，用于采样。与 Now 同源，不受对时与夏令时影响。</summary>
    public long MonotonicTicks => Elapsed.Ticks;

    public Func<DateTimeOffset> Func => () => Now;

    /// <summary>把一段仿真时间换算成真实等待时间。</summary>
    public TimeSpan RealDelay(TimeSpan simulated)
        => _rate <= 0 ? simulated : TimeSpan.FromTicks((long)(simulated.Ticks / _rate));
}
