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
/// 一条限值此刻的处境。**同一次越限只报一次**——温度贴着上限抖，
/// 一秒一条能在十分钟里刷出六百行，真正要人管的那条反而被埋掉了。
/// 报了之后一直算「在报」，直到条件不再成立才恢复，恢复也是一条事实。
/// </summary>
internal enum LimitPhase { Quiet, Firing }

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
    /// <summary>正在报的那几条。有它才分得出「刚越限」和「一直越着」。</summary>
    private readonly HashSet<string> _firing = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private int _busy;

    public SafetyMonitor(DataPipeline pipeline, Func<DateTimeOffset>? now = null)
    {
        _pipeline = pipeline;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    /// <summary>
    /// 越限了。**一次越限只发一次**，一直越着不会反复发。
    ///
    /// 消息里**不带通道前缀**：报警清单、执行记录、报告都自带「通道」这一列，
    /// 再带一个 CHn 会读成「CH1 CH1 Tr 高于上限」。
    /// </summary>
    public event EventHandler<SafetyEvent>? Triggered;

    /// <summary>
    /// 报过的那条不再成立了。恢复不等于没事：报警仍要人确认过才算翻篇
    /// （<see cref="Alarms.AlarmBook"/> 管这件事），这里只负责说"条件没了"。
    /// </summary>
    public event EventHandler<SafetyEvent>? Cleared;

    /// <summary>信号沉默多久算失效。断线不报警是最危险的失败模式。</summary>
    public TimeSpan SignalTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>一条限值在册上的身份。报警本按它认「还是那一条」。</summary>
    public static string KeyOf(SafetyLimit lim) => $"{lim.Channel}|{lim.Tag}|{lim.Action}";

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
        lock (_gate) { _limits.Clear(); _pending.Clear(); _last.Clear(); _firing.Clear(); }
    }

    /// <summary>联锁余量：温度 ±1 ℃，变化率 +25%。</summary>
    private const double TempMargin = 1.0, RateFactor = 1.25;

    /// <summary>
    /// 缺省值从设备 Limits 推导，并**留一点余量**。
    ///
    /// 实测撞出来的：设备允许 16 ℃/min，配方就照 16 ℃/min 跑，
    /// 传感器噪声让实测斜率在 16 上下抖——限值卡在 16 会把一次
    /// 完全正常的升温判成超速，然后中止整批。设定 180 ℃ 也一样，
    /// 稳态噪声随时能读出 180.03。
    ///
    /// 联锁要比工作范围松一档才叫联锁：它防的是"跑飞了"，
    /// 不是"贴着上限干活"。操作人可以再往里收，收不回来的是余量本身（§7.5）。
    /// </summary>
    public static SafetyLimit FromTemperature(int channel, TempLimits l, SafetyAction action = SafetyAction.AbortChannel)
        => new(channel, "Tr", l.Min - TempMargin, l.Max + TempMargin,
               l.MaxRatePerMin * RateFactor, TimeSpan.FromSeconds(3), action)
        {
            FromDeviceLimits = true,
            Note = $"由设备温度范围推导，留 {Fmt.Num(TempMargin, 0)} ℃ / {Fmt.Num((RateFactor - 1) * 100, 0)}% 联锁余量"
        };

    /// <summary>
    /// 周期性调用（1 Hz 足够）。返回本轮**新**触发的事件。
    ///
    /// 三条不变量：
    /// · 一次越限只发一次 —— 一直越着不再重复发，刷屏会把该看的那条埋掉；
    /// · 传感器失效（无信号 / Bad / Stale / 超时未更新）与越限走同一条去抖路径，
    ///   读不到值绝不当作正常（§7.5）；
    /// · 恢复也发一次（<see cref="Cleared"/>）。报警响了三秒还是三小时，
    ///   读记录的人要分得出。
    ///
    /// 定时器带周期，上一跳还没算完下一跳就会进来。那几个字典没有锁，
    /// 撞上就是脏读——忙着就跳过这一跳，1 Hz 的求值漏一次没有代价。
    /// </summary>
    public IReadOnlyList<SafetyEvent> Evaluate()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return Array.Empty<SafetyEvent>();
        try { return EvaluateCore(); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    private IReadOnlyList<SafetyEvent> EvaluateCore()
    {
        var now = _now();
        var fired = new List<SafetyEvent>();
        List<SafetyLimit> limits;
        lock (_gate) limits = _limits.ToList();

        // 限值被撤掉了（台面重建、通道移除），正在报的那条得跟着收——
        // 否则它永远挂在报警本上，等一个再也不会来的恢复
        var known = limits.Select(KeyOf).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in _firing.Where(k => !known.Contains(k)).ToList())
        {
            _firing.Remove(gone);
            _pending.Remove(gone);
        }

        foreach (var lim in limits)
        {
            var key = KeyOf(lim);
            string? breach;
            double? value = null;

            if (!_pipeline.TryLatest(lim.Channel, lim.Tag, now, out var s))
            {
                breach = "无信号";
            }
            else if (s.Quality is Quality.Bad or Quality.Stale)
            {
                value = s.Value;
                breach = $"信号 {s.Quality}";
            }
            else if (now - s.WallClock > SignalTimeout)
            {
                value = s.Value;
                breach = "超时未更新";
            }
            else
            {
                value = s.Value;
                breach = null;
                if (lim.Min is { } min && s.Value < min) breach = $"低于下限 {Fmt.Num(min)}（实测 {Over(s.Value, min)}）";
                else if (lim.Max is { } max && s.Value > max) breach = $"高于上限 {Fmt.Num(max)}（实测 {Over(s.Value, max)}）";
                else if (lim.MaxRatePerMin is { } rate && _last.TryGetValue(key, out var prev))
                {
                    var dt = (s.WallClock - prev.At).TotalMinutes;
                    if (dt > 0)
                    {
                        var slope = Math.Abs(s.Value - prev.Value) / dt;
                        if (slope > rate)
                            breach = $"变化率超过 {Fmt.Num(rate)}/min（实测 {Over(slope, rate)}/min）";
                    }
                }
                // 变化率要连着两个好点才算得出来，所以只在读到好值时记基准
                _last[key] = (s.WallClock, s.Value);
            }

            if (breach is null)
            {
                _pending.Remove(key);
                if (_firing.Remove(key))
                    Cleared?.Invoke(this, new SafetyEvent(now, lim.Channel, lim,
                        $"{lim.Tag} 已恢复正常", value));
                continue;
            }

            if (_firing.Contains(key)) continue;        // 已经在报了，不重复发

            if (!_pending.TryGetValue(key, out var since))
            {
                _pending[key] = now;
                since = now;
            }
            if (now - since < lim.Debounce) continue;   // 去抖，避免噪声刷屏

            _pending.Remove(key);
            _firing.Add(key);
            fired.Add(Fire(lim, now, value, $"{lim.Tag} {breach}"));
        }

        return fired;
    }

    /// <summary>
    /// 越限那个数按一位小数印出来常常和限值本身一模一样（「高于上限 180.0（180.0）」），
    /// 读的人只会以为程序算错了。看得出差别为止再多给两位。
    /// </summary>
    private static string Over(double value, double bound)
    {
        for (var d = 1; d <= 3; d++)
            if (Fmt.Num(value, d) != Fmt.Num(bound, d)) return Fmt.Num(value, d);
        return Fmt.Num(value, 3);
    }

    private SafetyEvent Fire(SafetyLimit lim, DateTimeOffset at, double? value, string message)
    {
        var e = new SafetyEvent(at, lim.Channel, lim, message, value);
        Triggered?.Invoke(this, e);
        return e;
    }
}
