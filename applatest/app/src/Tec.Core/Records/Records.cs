using Tec.Core.Recipes;
using Tec.Core.Scheduling;
using Tec.Driver.Abi;

namespace Tec.Core.Records;

public enum StepStatus { Pending, Running, Done, Skipped, Aborted, Failed }

public enum EventKind
{
    ChannelStarted,
    ChannelFinished,
    Paused,
    Resumed,
    ParameterChanged,
    StepSkipped,
    /// <summary>中止收尾时驱动把设备收到安全态做的事（切加热、停泵…）。
    /// 「停的那一刻机器被动了什么」必须留下，这是 GLP 追得到的一环。</summary>
    SafeStop,
    /// <summary>通道被中止（操作人停的、安全层停的）。
    /// 从前它走 Note，界面把 Note 过滤掉了，于是「谁在什么时候停了这一路」
    /// 在执行记录里根本查不到——那正是 GLP 最该留下的一条。</summary>
    Aborted,
    Alarm,
    SafetyAction,
    /// <summary>报警被人确认了。GLP 追的不只是「机器什么时候报的」，
    /// 还有「谁在什么时候看见了它、当时怎么处理的」——后者只能由人来落。</summary>
    AlarmAck,
    /// <summary>报警条件不再成立（温度回到范围内、断掉的信号又回来了）。
    /// 恢复本身也是一条事实：一条报警响了三秒还是响了三小时，读记录的人要分得出。</summary>
    AlarmCleared,
    OperatorMark,
    Sampling,
    ResourceWait,
    DeviceFault,
    Note
}

/// <summary>
/// 一条步骤记录。**两种偏差必须分开存**（§8.3）：
/// 开始偏差 = 被前面拖累了多少；时长偏差 = 这步自己跑得对不对。
/// 只报一个数就分不出"这步慢"和"前面拖累"，排查方向完全不同。
/// </summary>
public sealed class StepRecord
{
    public required int Index { get; init; }
    public required string StepId { get; init; }
    public required string CommandId { get; init; }
    public required string Title { get; init; }
    public required TerminationKind Termination { get; init; }
    /// <summary>循环体里的第几轮。第 1 轮以外的计划时间由循环跨度推移得到。</summary>
    public int Iteration { get; init; } = 1;

    // 计划：永远来自冻结基线，之后再改配方也不动它（§7.2）
    public required TimeSpan PlanStart { get; init; }
    public required TimeSpan PlanDuration { get; init; }

    // 实际
    public DateTimeOffset? ActualStart { get; set; }
    public DateTimeOffset? ActualEnd { get; set; }
    public TimeSpan? ActualDuration { get; set; }
    public EndReason? Reason { get; set; }
    public StepStatus Status { get; set; } = StepStatus.Pending;
    public string? Note { get; set; }

    /// <summary>
    /// 这一步按什么控温：釜内 Tr 还是夹套 Tj。非温度步骤为 null。
    ///
    /// **事后分析必须知道某一段是什么控制方式**，否则 Tr−Tj 曲线没法解读：
    /// 按 Tj 控时 Tr 跟着走、温差是热效应；按 Tr 控时夹套在追 Tr，温差是控制器在使劲。
    /// 两种情况下同样一条 Tr−Tj 曲线说的是完全不同的事。
    /// 值从启动那一刻冻结的配方参数里取，之后再改配方也不动它。
    /// </summary>
    public string? ControlMode { get; set; }

    /// <summary>工艺阶段，操作人在配方里标的（升温 / 保温 / 结晶 / 蒸馏…）。没标就是 null。</summary>
    public string? Phase { get; set; }

    /// <summary>该通道自己的 t0。通道各自启动，不能用批次时间。</summary>
    public DateTimeOffset ChannelStart { get; init; }

    public TimeSpan? ActualStartOffset
        => ActualStart is { } a ? a - ChannelStart : null;

    public TimeSpan? StartDeviation
        => ActualStartOffset is { } o ? o - PlanStart : null;

    public TimeSpan? DurationDeviation
        => ActualDuration is { } d ? d - PlanDuration : null;

    /// <summary>
    /// 相对阈值要配绝对下限：只用百分比的话，5 秒的步骤抖 1 秒就报红，表会被噪声淹掉（§13.5）。
    /// </summary>
    public bool DurationOutOfTolerance(double ratio = 0.10, TimeSpan? floor = null)
    {
        if (DurationDeviation is not { } dev) return false;
        if (Termination == TerminationKind.Immediate) return false;
        var abs = dev < TimeSpan.Zero ? dev.Negate() : dev;
        var f = floor ?? TimeSpan.FromSeconds(20);
        if (abs < f) return false;
        return PlanDuration > TimeSpan.Zero && abs.TotalSeconds > PlanDuration.TotalSeconds * ratio;
    }
}

public sealed class EventRecord
{
    public required DateTimeOffset At { get; init; }
    public required int Channel { get; init; }
    public required EventKind Kind { get; init; }
    public required string Text { get; init; }
    public string? User { get; init; }
    public int? StepIndex { get; init; }
    /// <summary>参数修改事件的改前/改后值，审计要能还原（§7.6）。</summary>
    public string? Before { get; init; }
    public string? After { get; init; }
}

/// <summary>启动那一刻冻结下来的配方 + 排期。这是 GLP 的前提（§7.2）。</summary>
public sealed class RunBaseline
{
    public required Recipe Recipe { get; init; }
    public required Schedule Schedule { get; init; }
    public required DateTimeOffset FrozenAt { get; init; }
    public string? ApprovedBy { get; init; }
}

/// <summary>
/// 通道的运行状态。Aborting 是「正在收尾」的过渡态，Aborted 才是这一趟的结局——
/// 两者要分开：从前收尾完了记录上留的还是 Aborting，界面读出来是「正在停止」，
/// 一个早已停下的通道永远显示成正在停。
/// </summary>
public enum ChannelRunState { Idle, Loading, Ready, Running, Paused, Aborting, Aborted, Completed, Faulted }

/// <summary>状态的中文说法。记录里写给人看的地方都走这一处，免得枚举名漏到界面上。</summary>
public static class RunStateWords
{
    public static string Of(ChannelRunState s) => s switch
    {
        ChannelRunState.Idle => "待机",
        ChannelRunState.Loading => "准备中",
        ChannelRunState.Ready => "就绪",
        ChannelRunState.Running => "运行中",
        ChannelRunState.Paused => "已暂停",
        ChannelRunState.Aborting => "正在停止",
        ChannelRunState.Aborted => "已中止",
        ChannelRunState.Completed => "已完成",
        ChannelRunState.Faulted => "故障",
        _ => s.ToString()
    };
}

/// <summary>通道子记录。批次只是把同时段的通道子记录归拢在一起（§7.1）。</summary>
public sealed class ChannelRun
{
    public required int Channel { get; init; }
    public required RunBaseline Baseline { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; set; }
    public ChannelRunState State { get; set; } = ChannelRunState.Ready;
    public string? Operator { get; init; }
    /// <summary>仿真运行必须标出来，绝不能和真实数据混进一份报告（§8.1）。</summary>
    public bool Simulated { get; init; }

    private readonly List<StepRecord> _steps = new();
    private readonly List<EventRecord> _events = new();
    private readonly object _gate = new();

    /// <summary>
    /// 读回的是快照，不是那两条活的表。
    ///
    /// **往这条链上追加的不止一个线程**：执行循环在线程池上跑，安全层在自己的
    /// 定时器线程上求值，手动标记和报警确认来自界面线程；而读的几乎总是界面线程
    /// （每 700 ms 把整张记录表重排一次）。裸 List 在这种组合下迟早在枚举中途
    /// 被 Add 撞上——那是一个当场抛异常、复现全靠运气的崩溃。
    /// 一趟运行几百条，复制的代价远小于把锁摊到调用方。
    /// </summary>
    public IReadOnlyList<StepRecord> Steps { get { lock (_gate) return _steps.ToArray(); } }
    public IReadOnlyList<EventRecord> Events { get { lock (_gate) return _events.ToArray(); } }

    // 只追加，不允许更新或删除；修正走"追加一条更正事件"（§8.3）
    public void Append(StepRecord s) { lock (_gate) _steps.Add(s); }
    public void Append(EventRecord e) { lock (_gate) _events.Add(e); }

    public StepRecord? Current
    {
        get
        {
            lock (_gate)
            {
                for (var i = _steps.Count - 1; i >= 0; i--)
                    if (_steps[i].Status == StepStatus.Running) return _steps[i];
                return null;
            }
        }
    }

    public TimeSpan Elapsed(DateTimeOffset now)
        => (FinishedAt ?? now) - StartedAt;

    /// <summary>滚动预测：基线 + 当前累计偏移外推。和基线分开放，绝不混在一个视图里（§7.2）。</summary>
    public DateTimeOffset ProjectedFinish(DateTimeOffset now)
    {
        var drift = TimeSpan.Zero;
        var steps = Steps;
        for (var i = steps.Count - 1; i >= 0; i--)
        {
            var s = steps[i];
            if (s.Status == StepStatus.Running && s.ActualStartOffset is { } o)
            {
                drift = o - s.PlanStart;
                var ran = now - (s.ActualStart ?? now);
                if (ran > s.PlanDuration) drift += ran - s.PlanDuration;
                break;
            }
            if (s.Status == StepStatus.Done && s.ActualStartOffset is { } o2 && s.ActualDuration is { } d2)
            {
                drift = o2 + d2 - (s.PlanStart + s.PlanDuration);
                break;
            }
        }
        return StartedAt + Baseline.Schedule.Total + drift;
    }
}

/// <summary>批次。</summary>
public sealed class RunRecord
{
    public string RunId { get; init; } = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
    public string Name { get; set; } = "未命名批次";
    public string? Operator { get; set; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ClosedAt { get; set; }
    public string BenchName { get; set; } = "";

    private readonly List<ChannelRun> _channels = new();
    public IReadOnlyList<ChannelRun> Channels => _channels;

    public void Append(ChannelRun c) => _channels.Add(c);

    public ChannelRun? Of(int channel)
    {
        for (var i = _channels.Count - 1; i >= 0; i--)
            if (_channels[i].Channel == channel) return _channels[i];
        return null;
    }

    public IReadOnlyList<int> StartedChannels
        => _channels.Select(c => c.Channel).Distinct().OrderBy(x => x).ToList();

    /// <summary>批次里最早的通道启动时刻。墙钟对齐的时间轴以它为原点。</summary>
    public DateTimeOffset? FirstStart
        => _channels.Count == 0 ? null : _channels.Min(c => c.StartedAt);
}
