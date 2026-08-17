namespace Tec.Core.Safety;

/// <summary>
/// 一条报警的一生。
///
/// **触发和确认是两件事。** 机器判断条件成立就触发，那是安全层的事；
/// 有没有人看见、看见之后怎么处理，只有人能落。GLP 追的正是后半段——
/// 一份报告里写着「运行中报警 3 次」而答不出谁看过、当时做了什么，
/// 等于没记。所以报警不会自己消失：条件恢复了也要人确认过才翻篇。
/// </summary>
public sealed class Alarm
{
    public required string Key { get; init; }
    public required int Channel { get; init; }
    public required string Tag { get; init; }
    /// <summary>这条限值声明的动作。安全层已经照它做过了，做了什么在 <see cref="Did"/> 里。</summary>
    public required SafetyAction Action { get; init; }
    public required DateTimeOffset RaisedAt { get; init; }

    public string Message { get; internal set; } = "";
    public double? Value { get; internal set; }

    /// <summary>
    /// 触发那一刻安全层**实际做了什么**（切加热 / 停泵 / 中止通道），逐条。
    /// 空表 = 只报警，没动设备——那也是要写清楚的一种：现场得知道机器还在跑。
    /// </summary>
    public IReadOnlyList<string> Did { get; internal set; } = Array.Empty<string>();

    /// <summary>条件此刻还成立着。</summary>
    public bool Standing { get; internal set; } = true;
    public DateTimeOffset LastAt { get; internal set; }
    public DateTimeOffset? ResolvedAt { get; internal set; }
    /// <summary>同一条限值报了第几回。中间恢复过又回来了才加一。</summary>
    public int Episodes { get; internal set; } = 1;

    public DateTimeOffset? AckAt { get; internal set; }
    public string? AckBy { get; internal set; }
    public string? AckNote { get; internal set; }
    public bool Acknowledged => AckAt is not null;

    /// <summary>还得有人管：条件还成立，或者虽然恢复了但没人确认过。</summary>
    public bool Live => Standing || !Acknowledged;

    /// <summary>
    /// 一个词说清此刻的处境。「已恢复，待确认」这一档不能省——
    /// 一条自己好了的报警要是悄悄消失，操作人根本不会知道刚才出过事。
    /// </summary>
    public string StateText => Standing
        ? (Acknowledged ? "报警中（已确认）" : "报警中")
        : (Acknowledged ? "已恢复" : "已恢复，待确认");

    /// <summary>动作的中文说法。记录、界面、报告共用一处。</summary>
    public string ActionText => Action switch
    {
        SafetyAction.Alarm => "仅报警",
        SafetyAction.StopDosing => "停加料",
        SafetyAction.StopHeating => "停加热",
        SafetyAction.AbortChannel => "中止本通道",
        SafetyAction.StopAll => "中止全部通道",
        _ => Action.ToString()
    };

    public TimeSpan Duration(DateTimeOffset now) => (ResolvedAt ?? now) - RaisedAt;
}

/// <summary>
/// 报警本。安全层负责判断，这里负责「还有几条没人管」。
///
/// 按限值去重：同一条限值反复越限只占一行，中间恢复过再回来算下一回
/// （<see cref="Alarm.Episodes"/>）。**回来那一次要重新确认**——
/// 上一次确认确认的是上一次那回事。
/// </summary>
public sealed class AlarmBook
{
    private const int MaxHistory = 500;

    private readonly object _gate = new();
    private readonly Dictionary<string, Alarm> _live = new(StringComparer.Ordinal);
    private readonly List<Alarm> _all = new();

    public event EventHandler? Changed;

    /// <summary>还要人管的那几条，最近的排在前面。</summary>
    public IReadOnlyList<Alarm> Live
    {
        get { lock (_gate) return _live.Values.OrderByDescending(a => a.LastAt).ToList(); }
    }

    /// <summary>这一趟出现过的全部报警，含已经翻篇的。</summary>
    public IReadOnlyList<Alarm> All
    {
        get { lock (_gate) return _all.ToArray(); }
    }

    public int UnackedCount
    {
        get { lock (_gate) return _live.Values.Count(a => !a.Acknowledged); }
    }

    public int StandingCount
    {
        get { lock (_gate) return _live.Values.Count(a => a.Standing); }
    }

    public Alarm? Find(string key)
    {
        lock (_gate) return _live.TryGetValue(key, out var a) ? a : null;
    }

    /// <summary>安全层报了一条。did = 触发时实际做过的动作。</summary>
    public Alarm Raise(SafetyEvent e, IReadOnlyList<string> did, DateTimeOffset at)
    {
        Alarm alarm;
        lock (_gate)
        {
            var key = SafetyMonitor.KeyOf(e.Limit);
            if (_live.TryGetValue(key, out var old))
            {
                // 恢复过又回来了，是同一件事的第二回——时好时坏的那种不该在
                // 清单上摊成两行。**确认作废**：上一次确认的是上一次那回事，
                // 沿用会让一条正在响的报警看上去"已经有人管了"
                // （安全层那条路上"确认过 + 已恢复"当场翻篇，走不到这里；
                //   限值被单独重设过再报才会）
                old.Standing = true;
                old.ResolvedAt = null;
                old.LastAt = at;
                old.Message = e.Message;
                old.Value = e.Value;
                old.Did = did;
                old.Episodes++;
                old.AckAt = null;
                old.AckBy = null;
                old.AckNote = null;
                alarm = old;
            }
            else
            {
                alarm = new Alarm
                {
                    Key = key,
                    Channel = e.Channel,
                    Tag = e.Limit.Tag,
                    Action = e.Limit.Action,
                    RaisedAt = at,
                    LastAt = at,
                    Message = e.Message,
                    Value = e.Value,
                    Did = did
                };
                _live[key] = alarm;
                _all.Add(alarm);
                if (_all.Count > MaxHistory) _all.RemoveRange(0, _all.Count - MaxHistory);
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return alarm;
    }

    /// <summary>条件不再成立。返回 null 表示本来就没在报这一条。</summary>
    public Alarm? Resolve(string key, DateTimeOffset at)
    {
        Alarm? a;
        lock (_gate)
        {
            if (!_live.TryGetValue(key, out a) || !a.Standing) return null;
            a.Standing = false;
            a.ResolvedAt = at;
            a.LastAt = at;
            if (a.Acknowledged) _live.Remove(key);      // 恢复了又确认过 → 翻篇
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return a;
    }

    /// <summary>
    /// 全部收尾（台面重建、限值重设）。条件还成不成立此刻已经无从判断——
    /// 直说"限值已重设"，不假装它自己好了。还没确认的仍然留在本上等人确认。
    /// </summary>
    public IReadOnlyList<Alarm> ResolveAll(DateTimeOffset at, string reason)
    {
        var touched = new List<Alarm>();
        lock (_gate)
        {
            foreach (var a in _live.Values.Where(x => x.Standing).ToList())
            {
                a.Standing = false;
                a.ResolvedAt = at;
                a.LastAt = at;
                a.Message = $"{a.Message} —— {reason}";
                if (a.Acknowledged) _live.Remove(a.Key);
                touched.Add(a);
            }
        }
        if (touched.Count > 0) Changed?.Invoke(this, EventArgs.Empty);
        return touched;
    }

    /// <summary>有人确认了。返回 null 表示这条不在本上，或者已经确认过。</summary>
    public Alarm? Ack(string key, string? user, string? note, DateTimeOffset at)
    {
        Alarm? a;
        lock (_gate)
        {
            if (!_live.TryGetValue(key, out a) || a.Acknowledged) return null;
            a.AckAt = at;
            a.AckBy = user;
            a.AckNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
            if (!a.Standing) _live.Remove(key);         // 已经恢复了 → 翻篇
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return a;
    }

    /// <summary>全部清空。换一个批次才用得上；日常不要用它当"消音键"。</summary>
    public void Reset()
    {
        lock (_gate) { _live.Clear(); _all.Clear(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
