using System.Collections.ObjectModel;
using Avalonia.Threading;
using Tec.App.Services;
using Tec.Core;
using Tec.Core.Execution;
using Tec.Core.Records;
using Tec.Core.Recipes;
using Tec.Core.Scheduling;
using Tec.Driver.Abi;

namespace Tec.App.ViewModels;

/// <summary>
/// 执行记录的一行。步骤行与事件行在同一条链上——
/// 事后回看时"这一步为什么慢"和"当时发生了什么"必须挨在一起。
/// </summary>
public sealed class ExecRowViewModel
{
    public static ExecRowViewModel FromStep(StepRecord s) => new()
    {
        IsEvent = false,
        Order = s.ActualStart ?? DateTimeOffset.MinValue,
        Channel = 0,
        Title = s.Title,
        Termination = TerminationText.Of(s.Termination),
        PlanStart = Fmt.Hms(s.PlanStart),
        ActualStart = s.ActualStartOffset is { } o ? Fmt.Hms(o) : "—",
        StartDeviation = s.StartDeviation is { } d ? Fmt.Signed(d) : "—",
        PlanDuration = Fmt.Hms(s.PlanDuration),
        ActualDuration = s.ActualDuration is { } a ? Fmt.Hms(a) : "—",
        DurationDeviation = s.DurationDeviation is { } dd ? Fmt.Signed(dd) : "—",
        Reason = s.Reason?.ToString() ?? (s.Status == StepStatus.Running ? "执行中" : "—"),
        Status = StatusText(s.Status),
        IsRunning = s.Status == StepStatus.Running,
        IsBad = s.Status is StepStatus.Failed or StepStatus.Aborted,
        IsOutOfTolerance = s.DurationOutOfTolerance(),
        Iteration = s.Iteration,
        Note = s.Note ?? ""
    };

    public static ExecRowViewModel FromEvent(EventRecord e, DateTimeOffset t0) => new()
    {
        IsEvent = true,
        Order = e.At,
        Channel = e.Channel,
        Title = e.Text,
        ActualStart = Fmt.Hms(e.At - t0),
        Reason = e.Kind.ToString(),
        Status = e.User ?? "",
        Note = e.Before is null ? "" : $"{e.Before} → {e.After}",
        IsBad = e.Kind is EventKind.Alarm or EventKind.SafetyAction or EventKind.DeviceFault
    };

    private static string StatusText(StepStatus s) => s switch
    {
        StepStatus.Pending => "待执行",
        StepStatus.Running => "执行中",
        StepStatus.Done => "完成",
        StepStatus.Skipped => "跳过",
        StepStatus.Aborted => "中止",
        _ => "失败"
    };

    public bool IsEvent { get; init; }
    public DateTimeOffset Order { get; init; }
    public int Channel { get; init; }
    public int Iteration { get; init; } = 1;
    public string Title { get; init; } = "";
    public string Termination { get; init; } = "";
    public string PlanStart { get; init; } = "";
    public string ActualStart { get; init; } = "";
    public string StartDeviation { get; init; } = "";
    public string PlanDuration { get; init; } = "";
    public string ActualDuration { get; init; } = "";
    public string DurationDeviation { get; init; } = "";
    public string Reason { get; init; } = "";
    public string Status { get; init; } = "";
    public string Note { get; init; } = "";
    public bool IsRunning { get; init; }
    public bool IsBad { get; init; }
    public bool IsOutOfTolerance { get; init; }
}

/// <summary>一个通道的磁贴。当前步直接读记录表最后一行，不维护第二份状态（§7.3）。</summary>
public sealed class ChannelTileViewModel : ViewModelBase
{
    private readonly Workspace _ws;

    public ChannelTileViewModel(Workspace ws, ChannelRunner runner)
    {
        _ws = ws;
        Runner = runner;
    }

    public ChannelRunner Runner { get; }
    public int Number => Runner.Number;
    public string Name => $"CH{Number}";

    public string StateText => Runner.State switch
    {
        ChannelRunState.Running => "运行中",
        ChannelRunState.Paused => "已暂停",
        ChannelRunState.Completed => "已完成",
        ChannelRunState.Faulted => "故障",
        ChannelRunState.Aborting => "中止中",
        _ => "空闲"
    };

    public bool IsRunning => Runner.State == ChannelRunState.Running;
    public bool IsPaused => Runner.State == ChannelRunState.Paused;
    public bool IsIdle => Runner.State is ChannelRunState.Idle or ChannelRunState.Completed;
    public bool IsWaitingOperator => _ws.OperatorGate.IsWaiting(Number);

    public string RecipeName => Runner.Run?.Baseline.Recipe.Name ?? "未装载";
    public string CurrentStep => Runner.Run?.Current?.Title ?? (Runner.Run is null ? "—" : "（步骤间）");

    public string Elapsed => Runner.Run is { } r ? Fmt.Hms(r.Elapsed(_ws.Clock.Now)) : "0:00:00";
    public string StartedAt => Runner.Run is { } r ? Fmt.Clock(r.StartedAt) : "—";

    /// <summary>滚动预测，和基线分开放（§7.2）。</summary>
    public string Projected => Runner.Run is { } r ? Fmt.Clock(r.ProjectedFinish(_ws.Clock.Now)) : "—";
    public string Planned => Runner.Run is { } r ? Fmt.Hms(r.Baseline.Schedule.Total) : "—";

    public string Drift
    {
        get
        {
            var cur = Runner.Run?.Current;
            return cur?.StartDeviation is { } d ? Fmt.Signed(d) : "0:00";
        }
    }

    public string Tr => Read("Tr", "℃");
    public string Tj => Read("Tj", "℃");
    public string Rpm => Read("rpm", "rpm");
    public string Extra => Read("pH", "") is var ph && ph != "—" ? "pH " + ph : Read("turb", "NTU");

    private string Read(string tag, string unit)
    {
        if (!_ws.Pipeline.TryLatest(Number, tag, _ws.Clock.Now, out var s)) return "—";
        var text = s.Value.ToString(tag == "rpm" ? "F0" : "F2");
        if (s.Quality is Quality.Stale or Quality.Bad) text += "!";
        return unit.Length > 0 ? $"{text} {unit}" : text;
    }

    public void Refresh() => RaiseAll(
        nameof(StateText), nameof(IsRunning), nameof(IsPaused), nameof(IsIdle), nameof(IsWaitingOperator),
        nameof(RecipeName), nameof(CurrentStep), nameof(Elapsed), nameof(StartedAt),
        nameof(Projected), nameof(Planned), nameof(Drift),
        nameof(Tr), nameof(Tj), nameof(Rpm), nameof(Extra));
}

public sealed class RunViewModel : ViewModelBase
{
    private readonly Workspace _ws;
    private readonly DispatcherTimer _timer;
    private ChannelTileViewModel? _selected;
    private Recipe? _recipeToLoad;
    private bool _wallClockAxis = true;

    public RunViewModel(Workspace ws)
    {
        _ws = ws;
        foreach (var r in ws.Engine.Runners.OrderBy(r => r.Number))
            Tiles.Add(new ChannelTileViewModel(ws, r));
        foreach (var r in ws.Library) Recipes.Add(r);
        _selected = Tiles.FirstOrDefault();
        _recipeToLoad = ws.Library.FirstOrDefault();

        StartChannel = new RelayCommand(() => Start());
        PauseChannel = new RelayCommand(() => _selected?.Runner.Pause("操作员"));
        ResumeChannel = new RelayCommand(() => _selected?.Runner.Resume("操作员"));
        AbortChannel = new RelayCommand(() => _selected?.Runner.Abort("操作员"));
        SkipStep = new RelayCommand(() => _selected?.Runner.SkipCurrent("操作员"));
        ConfirmPrompt = new RelayCommand(() => { if (_selected is not null) ws.OperatorGate.Confirm(_selected.Number); });
        ToggleAxis = new RelayCommand(() => WallClockAxis = !WallClockAxis);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    public ObservableCollection<ChannelTileViewModel> Tiles { get; } = new();
    public ObservableCollection<ExecRowViewModel> Rows { get; } = new();
    public ObservableCollection<Recipe> Recipes { get; } = new();

    public RelayCommand StartChannel { get; }
    public RelayCommand PauseChannel { get; }
    public RelayCommand ResumeChannel { get; }
    public RelayCommand AbortChannel { get; }
    public RelayCommand SkipStep { get; }
    public RelayCommand ConfirmPrompt { get; }
    public RelayCommand ToggleAxis { get; }

    public ChannelTileViewModel? Selected
    {
        get => _selected;
        set { if (Set(ref _selected, value)) { RebuildRows(); Raise(nameof(SelectedTitle)); } }
    }

    public Recipe? RecipeToLoad
    {
        get => _recipeToLoad;
        set => Set(ref _recipeToLoad, value);
    }

    /// <summary>
    /// 时间轴对齐方式。通道各自启动，所以两种都要有：
    /// 墙钟看的是"谁跟谁在同时跑"，通道基准看的是"这一条自己走到哪了"。
    /// </summary>
    public bool WallClockAxis
    {
        get => _wallClockAxis;
        set { if (Set(ref _wallClockAxis, value)) { Raise(nameof(AxisText)); GanttChanged?.Invoke(this, EventArgs.Empty); } }
    }

    public string AxisText => _wallClockAxis ? "墙钟对齐" : "各通道基准";
    public string SelectedTitle => _selected is null ? "执行记录" : $"执行记录 · {_selected.Name}";

    public string BatchText
    {
        get
        {
            var rec = _ws.Engine.Record;
            var started = rec.StartedChannels;
            return started.Count == 0
                ? "尚无通道启动"
                : $"批次 {rec.RunId} · 已启动 {string.Join("、", started.Select(c => "CH" + c))}";
        }
    }

    public string SimNote => _ws.TimeScale > 1
        ? $"仿真 {_ws.TimeScale:F0}× · 数据标记为 Simulated，不与实测混用"
        : "实测";

    public event EventHandler? GanttChanged;

    public RunRecord Record => _ws.Engine.Record;
    public Workspace Workspace => _ws;

    private void Start()
    {
        if (_selected is null || _recipeToLoad is null) return;
        try
        {
            _ws.Engine.StartChannel(_selected.Number, _recipeToLoad, "操作员");
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Raise(nameof(Error));
            return;
        }
        Error = null;
        RaiseAll(nameof(Error), nameof(BatchText));
        GanttChanged?.Invoke(this, EventArgs.Empty);
    }

    public string? Error { get; private set; }

    private void Tick()
    {
        foreach (var t in Tiles) t.Refresh();
        RebuildRows();
        Raise(nameof(BatchText));
        GanttChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RebuildRows()
    {
        var run = _selected is null ? null : _ws.Engine.Record.Of(_selected.Number);
        if (run is null)
        {
            if (Rows.Count > 0) Rows.Clear();
            return;
        }

        var rows = new List<ExecRowViewModel>();
        foreach (var s in run.Steps) rows.Add(ExecRowViewModel.FromStep(s));
        foreach (var e in run.Events) rows.Add(ExecRowViewModel.FromEvent(e, run.StartedAt));
        rows.Sort((a, b) => a.Order.CompareTo(b.Order));

        // 行本身是快照，运行中的那一行每秒都在变，所以整表重建；行数不多，够用
        Rows.Clear();
        foreach (var r in rows) Rows.Add(r);
    }

    public void Stop() => _timer.Stop();
}
