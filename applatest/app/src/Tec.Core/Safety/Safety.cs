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
    /// <summary>
    /// 配方「改限值」步骤加的。跟底线（FromDeviceLimits）分开记：
    /// 底线独立于配方永远在，这种只活到下一次启动运行（那时被清掉重来）。
    /// </summary>
    public bool FromRecipe { get; init; }

    /// <summary>
    /// 操作人在 HMI 面板「反应釜与安全」页收的一层。与配方层同理只能在底线
    /// 之内收紧，但生命期不同：它不跟批次走，改回底线值才撤（设备屏上拧的
    /// 东西不该因为下一炉开跑就悄悄弹回去）。
    /// </summary>
    public bool FromOperator { get; init; }

    /// <summary>
    /// 某一步执行期间的临时限值（iControl 每步的 Advanced 覆盖层）。
    /// 值是那一步的 StepId：步骤开始时挂上，结束（不论怎么结束）立刻撤下。
    /// 三层并存同时求值——底线 / 配方 / 本步——所以这一层**只能收紧不可能放宽**：
    /// 多一条限值只会多一双眼睛，撤下即还原。
    /// </summary>
    public string? StepScope { get; init; }
}

/// <summary>
/// 触发动作的措辞 ↔ 枚举。「改限值」步骤、每步的临时限值、界面下拉共用这一份——
/// 各写一份对照表的话，迟早出现「界面写着停加料、执行的是仅报警」。
/// </summary>
public static class SafetyActionWords
{
    /// <summary>五档的下拉选项，顺序即界面顺序。</summary>
    public static readonly string[] All = { "仅报警", "停止加料", "停止加热", "中止本通道", "中止全部通道" };

    public static string Of(SafetyAction a) => a switch
    {
        SafetyAction.Alarm => "仅报警",
        SafetyAction.StopDosing => "停止加料",
        SafetyAction.StopHeating => "停止加热",
        SafetyAction.StopAll => "中止全部通道",
        _ => "中止本通道"
    };

    /// <summary>老配方的「停止实验 / 暂停实验」也在这儿兜底翻译，别处不用各自记。</summary>
    public static SafetyAction Parse(string word) => word switch
    {
        "仅报警" => SafetyAction.Alarm,
        "停止加料" => SafetyAction.StopDosing,
        "停止加热" => SafetyAction.StopHeating,
        "中止全部通道" => SafetyAction.StopAll,
        // 引擎没有「暂停」这一档，最接近本意（先停下来等人看）的是报警
        "暂停实验" => SafetyAction.Alarm,
        _ => SafetyAction.AbortChannel      // 中止本通道；老值「停止实验」也是这个意思
    };
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

    /// <summary>
    /// 一条限值在册上的身份。报警本按它认「还是那一条」。
    /// **分层去撞**：底线、配方、本步三层可以在同一通道同一监测量上同动作并存
    /// （配方把 Tr 收到 80、底线还守着 181，动作都是中止本通道），
    /// 不带层标的话三条共用一份去抖与「在报」状态——后越限的那条永远发不出来。
    /// </summary>
    public static string KeyOf(SafetyLimit lim)
        => $"{lim.Channel}|{lim.Tag}|{lim.Action}"
           + (lim.FromRecipe ? "|recipe" : "")
           + (lim.FromOperator ? "|op" : "")
           + (lim.StepScope is { } s ? $"|step:{s}" : "");

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

    /// <summary>
    /// 配方「改限值」步骤设定一条限值。同一通道同一监测量的**配方**限值
    /// 只留最新一条（后面的步骤改的是同一条限值，不是叠一条新的）；
    /// 底线限值（FromDeviceLimits）不受影响——两条同时求值，
    /// 配方只能在底线之内再收紧，放宽是放不动的。
    /// </summary>
    public void SetRecipeLimit(SafetyLimit limit)
    {
        var lim = limit with { FromRecipe = true };
        lock (_gate)
        {
            _limits.RemoveAll(l => l.FromRecipe && l.Channel == lim.Channel && l.Tag == lim.Tag);
            _limits.Add(lim);
        }
    }

    /// <summary>
    /// 撤掉某通道全部配方限值（下一炉启动前清场，上一炉收紧的不带进来）。
    /// 撤掉之后正在报的那条由 EvaluateCore 里「限值不在册」那段收尾。
    /// </summary>
    public int RemoveRecipeLimits(int channel)
    {
        lock (_gate) return _limits.RemoveAll(l => l.FromRecipe && l.Channel == channel);
    }

    /// <summary>
    /// 面板上操作人收的一层限值。同一通道同一监测量只留最新一条。
    /// **只能在底线包络之内收紧**（§7.5）：出界的部分按到底线上；
    /// 三个数都收到与底线持平（或更松）就等于撤掉这一层——设回原值 = 回到底线。
    /// 动作沿用底线的动作：操作人收的是「多早动手」，不是「动什么手」。
    /// 返回实际生效的那条；撤掉（回到底线）返回 null。
    /// </summary>
    public SafetyLimit? SetOperatorLimit(int channel, string tag,
        double? min, double? max, double? maxRate)
    {
        lock (_gate)
        {
            var baseLim = _limits.FirstOrDefault(l =>
                l.FromDeviceLimits && l.Channel == channel && l.Tag == tag);
            var action = baseLim?.Action ?? SafetyAction.AbortChannel;
            if (baseLim is not null)
            {
                if (min is { } lo && baseLim.Min is { } bl) min = Math.Max(lo, bl);
                if (max is { } hi && baseLim.Max is { } bh) max = Math.Min(hi, bh);
                if (maxRate is { } r && baseLim.MaxRatePerMin is { } br) maxRate = Math.Min(r, br);
                var slackMin = min is null || (baseLim.Min is { } l2 && min <= l2);
                var slackMax = max is null || (baseLim.Max is { } h2 && max >= h2);
                var slackRate = maxRate is null
                                || (baseLim.MaxRatePerMin is { } r2 && maxRate >= r2);
                if (slackMin && slackMax && slackRate)
                {
                    _limits.RemoveAll(l => l.FromOperator && l.Channel == channel && l.Tag == tag);
                    return null;
                }
            }
            _limits.RemoveAll(l => l.FromOperator && l.Channel == channel && l.Tag == tag);
            var lim = new SafetyLimit(channel, tag, min, max, maxRate,
                                      TimeSpan.FromSeconds(3), action)
            { FromOperator = true, Note = "操作人在面板上收紧" };
            _limits.Add(lim);
            return lim;
        }
    }

    /// <summary>
    /// 某一步执行期间的临时限值（每步的 Advanced 覆盖层）。同一步重进
    /// （循环体里那一步每一轮都会再挂一次）先撤旧的再挂新的，不叠加。
    /// </summary>
    public void SetStepLimits(int channel, string stepId, IEnumerable<SafetyLimit> limits)
    {
        lock (_gate)
        {
            _limits.RemoveAll(l => l.StepScope == stepId && l.Channel == channel);
            foreach (var lim in limits)
                _limits.Add(lim with { Channel = channel, StepScope = stepId });
        }
    }

    /// <summary>
    /// 撤某一步的临时限值；stepId 为 null 撤这条通道全部步骤层限值
    /// （运行收尾时兜底清场——一步都不许把限值留过自己的生命期）。
    /// 正在报的由 EvaluateCore 里「限值不在册」那段收尾。
    /// </summary>
    public int RemoveStepLimits(int channel, string? stepId = null)
    {
        lock (_gate)
            return _limits.RemoveAll(l => l.StepScope is not null && l.Channel == channel
                                          && (stepId is null || l.StepScope == stepId));
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
