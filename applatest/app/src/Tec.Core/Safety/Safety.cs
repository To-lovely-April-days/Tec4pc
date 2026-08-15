using Tec.Core.Data;
using Tec.Driver.Abi;

namespace Tec.Core.Safety;

public enum SafetyAction
{
    Alarm,
    StopDosing,
    StopHeating,
    AbortChannel,
    StopAll
}

/// <summary>
/// 安全层**独立于配方，优先于一切**。不能靠配方里放一条"安全联锁"指令来保证安全——
/// 那条指令只是工艺逻辑，不是安全功能（§7.5）。
/// </summary>
public sealed record SafetyLimit(
    int Channel,
    string Tag,
    double? Min,
    double? Max,
    double? MaxRatePerMin,
    TimeSpan Debounce,
    SafetyAction Action)
{
    public string? Note { get; init; }
    /// <summary>操作人只能收紧不能放宽——界面据此校验。</summary>
    public bool FromDeviceLimits { get; init; }
}

public sealed record SafetyEvent(DateTimeOffset At, int Channel, SafetyLimit Limit, string Message, double? Value);

/// <summary>
/// 独立于执行引擎周期性求值，命中即执行动作并写事件。
/// **传感器失效（断偶、超量程、通信中断）本身就是触发条件**——
/// 不能因为读不到值就当作正常（§7.5）。
/// </summary>
public sealed class SafetyMonitor
{
    private readonly DataPipeline _pipeline;
    private readonly Func<DateTimeOffset> _now;
    private readonly List<SafetyLimit> _limits = new();
    private readonly Dictionary<string, DateTimeOffset> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (DateTimeOffset At, double Value)> _last = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public SafetyMonitor(DataPipeline pipeline, Func<DateTimeOffset>? now = null)
    {
        _pipeline = pipeline;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public event EventHandler<SafetyEvent>? Triggered;

    /// <summary>信号沉默多久算失效。断线不报警是最危险的失败模式。</summary>
    public TimeSpan SignalTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public IReadOnlyList<SafetyLimit> Limits
    {
        get { lock (_gate) return _limits.ToList(); }
    }

    public void Add(SafetyLimit limit)
    {
        lock (_gate) _limits.Add(limit);
    }

    public void Clear()
    {
        lock (_gate) { _limits.Clear(); _pending.Clear(); _last.Clear(); }
    }

    /// <summary>缺省值从设备 Limits 推导。</summary>
    public static SafetyLimit FromTemperature(int channel, TempLimits l, SafetyAction action = SafetyAction.AbortChannel)
        => new(channel, "Tr", l.Min, l.Max, l.MaxRatePerMin, TimeSpan.FromSeconds(3), action)
        { FromDeviceLimits = true, Note = "由设备温度范围推导" };

    /// <summary>周期性调用（1 Hz 足够）。返回本轮触发的事件。</summary>
    public IReadOnlyList<SafetyEvent> Evaluate()
    {
        var now = _now();
        var fired = new List<SafetyEvent>();
        List<SafetyLimit> limits;
        lock (_gate) limits = _limits.ToList();

        foreach (var lim in limits)
        {
            var key = $"{lim.Channel}|{lim.Tag}|{lim.Action}";
            if (!_pipeline.TryLatest(lim.Channel, lim.Tag, now, out var s))
            {
                fired.Add(Fire(lim, now, null, $"CH{lim.Channel} {lim.Tag} 无信号"));
                continue;
            }

            if (s.Quality is Quality.Bad or Quality.Stale)
            {
                fired.Add(Fire(lim, now, s.Value, $"CH{lim.Channel} {lim.Tag} 信号 {s.Quality}"));
                continue;
            }

            if (now - s.WallClock > SignalTimeout)
            {
                fired.Add(Fire(lim, now, s.Value, $"CH{lim.Channel} {lim.Tag} 超时未更新"));
                continue;
            }

            string? breach = null;
            if (lim.Min is { } min && s.Value < min) breach = $"低于下限 {Fmt.Num(min)}";
            else if (lim.Max is { } max && s.Value > max) breach = $"高于上限 {Fmt.Num(max)}";
            else if (lim.MaxRatePerMin is { } rate && _last.TryGetValue(key, out var prev))
            {
                var dt = (s.WallClock - prev.At).TotalMinutes;
                if (dt > 0)
                {
                    var slope = Math.Abs(s.Value - prev.Value) / dt;
                    if (slope > rate) breach = $"变化率 {Fmt.Num(slope)}/min 超过 {Fmt.Num(rate)}/min";
                }
            }

            _last[key] = (s.WallClock, s.Value);

            if (breach is null) { _pending.Remove(key); continue; }

            if (!_pending.TryGetValue(key, out var since))
            {
                _pending[key] = now;
                since = now;
            }
            if (now - since < lim.Debounce) continue;   // 去抖，避免噪声刷屏

            _pending.Remove(key);
            fired.Add(Fire(lim, now, s.Value, $"CH{lim.Channel} {lim.Tag} {breach}（{Fmt.Num(s.Value)}）"));
        }

        return fired;
    }

    private SafetyEvent Fire(SafetyLimit lim, DateTimeOffset at, double? value, string message)
    {
        var e = new SafetyEvent(at, lim.Channel, lim, message, value);
        Triggered?.Invoke(this, e);
        return e;
    }
}
