using System.Collections.ObjectModel;
using Tec.App.Services;
using Tec.Core;
using Tec.Core.Safety;

namespace Tec.App.ViewModels;

/// <summary>
/// 报警清单上的一条。
///
/// 一条报警要回答四件事，缺一件都不够处理：**出了什么事**（消息 + 数值）、
/// **机器已经做了什么**（切加热？停泵？还是什么都没做）、**多久了**、
/// **有没有人管过**。中间那件最容易漏——「仅报警」的限值超限时机器一动不动，
/// 现场必须知道这一点，否则会以为已经被兜住了。
/// </summary>
public sealed class AlarmRowViewModel : ViewModelBase
{
    private readonly Workspace _ws;
    private readonly Action _changed;
    private string _note = "";

    public AlarmRowViewModel(Workspace ws, Alarm alarm, Action changed)
    {
        _ws = ws;
        _changed = changed;
        Alarm = alarm;
        Ack = new RelayCommand(DoAck);
    }

    public Alarm Alarm { get; }
    public string Key => Alarm.Key;
    public int Channel => Alarm.Channel;
    public string ChLabel => $"CH{Alarm.Channel}";
    public string ChColorHex => Alarm.Channel switch
    { 1 => "#2f7ed8", 2 => "#2aa87a", 3 => "#c9772b", _ => "#8a63d2" };

    public string Message => Alarm.Message;
    public string StateText => Alarm.StateText;
    public bool Standing => Alarm.Standing;

    /// <summary>报警中是红的，恢复了转灰——一眼分得出还有几条正响着。</summary>
    public string StateColorHex => Alarm.Standing ? "#c0392b" : "#5f6f7a";
    public string StateFillHex => Alarm.Standing ? "#fbe6e3" : "#eef1f3";

    public string TimeText
    {
        get
        {
            var head = $"{Alarm.RaisedAt:HH:mm:ss} 起 · 持续 {Fmt.Hms(Alarm.Duration(_ws.Clock.Now))}";
            return Alarm.Episodes > 1 ? $"{head} · 第 {Alarm.Episodes} 回" : head;
        }
    }

    /// <summary>
    /// 机器已经做了什么。空表的时候必须明说「未对设备做任何动作」——
    /// 留白会被读成"已经处理好了"。
    /// </summary>
    public string DidText => Alarm.Did.Count == 0
        ? $"处置：{Alarm.ActionText} —— 未对设备做任何动作"
        : $"处置：{Alarm.ActionText} —— {string.Join("；", Alarm.Did)}";

    public bool Acknowledged => Alarm.Acknowledged;
    public bool CanAck => !Alarm.Acknowledged;

    public string AckText => Alarm.Acknowledged
        ? $"{Alarm.AckAt:HH:mm:ss} {Alarm.AckBy ?? "—"} 已确认"
          + (Alarm.AckNote is null ? "" : $"：{Alarm.AckNote}")
        : "";

    /// <summary>确认时可以带一句处理说明，落进记录。</summary>
    public string Note
    {
        get => _note;
        set => Set(ref _note, value);
    }

    public RelayCommand Ack { get; }

    private void DoAck()
    {
        if (!CanAck) return;
        _ws.Engine.AckAlarm(Alarm.Key, _ws.Operator, Note);
        Note = "";
        _changed();
    }

    public void Tick() => RaiseAll(nameof(Message), nameof(StateText), nameof(Standing),
                                   nameof(StateColorHex), nameof(StateFillHex), nameof(TimeText),
                                   nameof(DidText), nameof(Acknowledged), nameof(CanAck), nameof(AckText));
}

/// <summary>
/// 运行页顶上那条报警带 + 展开后的清单。
///
/// 平时它不占地方（一条都没有就整条不画）。有报警才出现，**不做成弹框**：
/// 模态窗会把急停那颗钮挡在后面，而报警恰恰是最需要人能马上按停的时候。
/// </summary>
public sealed class AlarmBarViewModel : ViewModelBase
{
    private readonly Workspace _ws;
    private readonly Action<string> _say;
    private bool _open;

    public AlarmBarViewModel(Workspace ws, Action<string> say)
    {
        _ws = ws;
        _say = say;
        Toggle = new RelayCommand(() => Open = !Open);
        AckAll = new RelayCommand(DoAckAll);
    }

    public ObservableCollection<AlarmRowViewModel> Rows { get; } = new();

    public RelayCommand Toggle { get; }
    public RelayCommand AckAll { get; }

    public bool Any => Rows.Count > 0;
    public bool Open
    {
        get => _open && Any;
        set => Set(ref _open, value);
    }

    public int Unacked { get; private set; }
    public int Standing { get; private set; }
    public bool CanAckAll => Unacked > 0;

    /// <summary>还在响的用红，全都恢复了只等确认用灰蓝——颜色本身就是一句话。</summary>
    public string BarFillHex => Standing > 0 ? "#fbe6e3" : "#eef1f3";
    public string BarLineHex => Standing > 0 ? "#e0b4ad" : "#ccd4d9";
    public string BarInkHex => Standing > 0 ? "#c0392b" : "#41525c";

    /// <summary>
    /// 一行字说清「现在有几条、最要紧的是哪条」。数「正在响的」和
    /// 「等确认的」两个数：一条已经恢复但没人确认的报警仍然要留在眼前，
    /// 但它跟一条正在响的不是一回事。
    /// </summary>
    public string Text
    {
        get
        {
            if (Rows.Count == 0) return "";
            var head = Standing > 0
                ? $"{Standing} 条报警进行中" + (Unacked > 0 ? $"，{Unacked} 条待确认" : "，均已确认")
                : $"{Rows.Count} 条报警已恢复，待确认";
            var top = Rows.FirstOrDefault(r => r.Standing) ?? Rows[0];
            return $"{head} · {top.ChLabel} {top.Message}";
        }
    }

    public string ToggleText => Open ? "收起" : "查看并确认";

    public void Tick()
    {
        var live = _ws.Engine.Alarms.Live;

        // 按 Key 对齐，不整表重建：重建会把操作人正在输入的那句处理说明冲掉，
        // 而报警清单每 700 ms 刷一次——那一行字根本打不完
        var want = live.Select(a => a.Key).ToList();
        if (!Rows.Select(r => r.Key).SequenceEqual(want))
        {
            var old = Rows.ToDictionary(r => r.Key, StringComparer.Ordinal);
            Rows.Clear();
            foreach (var a in live)
                Rows.Add(old.TryGetValue(a.Key, out var keep) && ReferenceEquals(keep.Alarm, a)
                    ? keep
                    : new AlarmRowViewModel(_ws, a, () => Tick()));
        }

        foreach (var r in Rows) r.Tick();
        Unacked = _ws.Engine.Alarms.UnackedCount;
        Standing = _ws.Engine.Alarms.StandingCount;

        RaiseAll(nameof(Any), nameof(Open), nameof(Text), nameof(ToggleText),
                 nameof(Unacked), nameof(Standing), nameof(CanAckAll),
                 nameof(BarFillHex), nameof(BarLineHex), nameof(BarInkHex));
    }

    private void DoAckAll()
    {
        var n = _ws.Engine.AckAllAlarms(_ws.Operator);
        if (n > 0) _say($"已确认 {n} 条报警" + (Standing > 0 ? "（条件仍成立的仍在清单上）" : ""));
        Tick();
    }
}
