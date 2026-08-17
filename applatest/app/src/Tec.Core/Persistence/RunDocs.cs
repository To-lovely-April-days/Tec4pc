using Tec.Core.Records;
using Tec.Core.Recipes;
using Tec.Core.Scheduling;
using Tec.Driver.Abi;

namespace Tec.Core.Persistence;

// 归档文件的格式。和 Documents.cs 同一个道理：文件格式是对外承诺，
// 内存模型天天改——混在一起的话，某天给 StepRecord 加个界面用的字段，
// 上个月归档的那几炉就读不回来了。

public sealed class SchedEntryDoc
{
    public int Index { get; set; }
    public string StepId { get; set; } = "";
    public string CommandId { get; set; } = "";
    public TimeSpan Start { get; set; }
    public TimeSpan Duration { get; set; }
    public TimeSpan Span { get; set; }
    public int Repeats { get; set; } = 1;
    public string Title { get; set; } = "";
    public TerminationKind Termination { get; set; }
    public bool Known { get; set; } = true;
    public double StartTemp { get; set; } = 25;
}

public sealed class BaselineDoc
{
    public RecipeDoc Recipe { get; set; } = new();
    public DateTimeOffset FrozenAt { get; set; }
    public string? ApprovedBy { get; set; }
    /// <summary>冻结那一刻的排期。**不在读回时重算**——重算用的是今天的指令目录，
    /// 算出来的「计划」就不再是当时冻结的那一份，GLP 上等于把基线改了。</summary>
    public List<SchedEntryDoc> Schedule { get; set; } = new();
    public List<string> Missing { get; set; } = new();
}

public sealed class StepRecordDoc
{
    public int Index { get; set; }
    public string StepId { get; set; } = "";
    public string CommandId { get; set; } = "";
    public string Title { get; set; } = "";
    public TerminationKind Termination { get; set; }
    public int Iteration { get; set; } = 1;
    public TimeSpan PlanStart { get; set; }
    public TimeSpan PlanDuration { get; set; }
    public DateTimeOffset? ActualStart { get; set; }
    public DateTimeOffset? ActualEnd { get; set; }
    public TimeSpan? ActualDuration { get; set; }
    public EndReason? Reason { get; set; }
    public StepStatus Status { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset ChannelStart { get; set; }
    public string? ControlMode { get; set; }
    public string? Phase { get; set; }
}

public sealed class EventRecordDoc
{
    public DateTimeOffset At { get; set; }
    public int Channel { get; set; }
    public EventKind Kind { get; set; }
    public string Text { get; set; } = "";
    public string? User { get; set; }
    public int? StepIndex { get; set; }
    public string? Before { get; set; }
    public string? After { get; set; }
}

public sealed class ChannelRunDoc
{
    public int Channel { get; set; }
    public BaselineDoc Baseline { get; set; } = new();
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public ChannelRunState State { get; set; }
    public string? Operator { get; set; }
    public bool Simulated { get; set; }
    public List<StepRecordDoc> Steps { get; set; } = new();
    public List<EventRecordDoc> Events { get; set; } = new();
}

/// <summary>一炉。归档目录里的 run.json 就是它。</summary>
public sealed class RunDoc
{
    public const int CurrentSchema = 1;

    public int Schema { get; set; } = CurrentSchema;
    public string RunId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Operator { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public string BenchName { get; set; } = "";
    /// <summary>归档写下来的时刻。和 ClosedAt 不是一回事——中途归档的批次还没结束。</summary>
    public DateTimeOffset ArchivedAt { get; set; }
    /// <summary>写归档时程序版本，读回来对不上格式时能说清是哪一版写的。</summary>
    public string AppVersion { get; set; } = "";
    public List<ChannelRunDoc> Channels { get; set; } = new();
}

/// <summary>归档的模型 ↔ 文档互转。</summary>
public static class RunFiles
{
    public static RunDoc ToDoc(this RunRecord rec, DateTimeOffset archivedAt, string version)
    {
        var doc = new RunDoc
        {
            RunId = rec.RunId,
            Name = rec.Name,
            Operator = rec.Operator,
            CreatedAt = rec.CreatedAt,
            ClosedAt = rec.ClosedAt,
            BenchName = rec.BenchName,
            ArchivedAt = archivedAt,
            AppVersion = version
        };
        foreach (var ch in rec.Channels) doc.Channels.Add(ch.ToDoc());
        return doc;
    }

    public static ChannelRunDoc ToDoc(this ChannelRun ch)
    {
        var doc = new ChannelRunDoc
        {
            Channel = ch.Channel,
            StartedAt = ch.StartedAt,
            FinishedAt = ch.FinishedAt,
            State = ch.State,
            Operator = ch.Operator,
            Simulated = ch.Simulated,
            Baseline = new BaselineDoc
            {
                Recipe = ch.Baseline.Recipe.ToDoc(),
                FrozenAt = ch.Baseline.FrozenAt,
                ApprovedBy = ch.Baseline.ApprovedBy,
                Missing = ch.Baseline.Schedule.MissingCommands.ToList()
            }
        };
        foreach (var e in ch.Baseline.Schedule.Entries)
            doc.Baseline.Schedule.Add(new SchedEntryDoc
            {
                Index = e.Index, StepId = e.StepId, CommandId = e.CommandId,
                Start = e.Start, Duration = e.Duration, Span = e.Span, Repeats = e.Repeats,
                Title = e.Title, Termination = e.Termination, Known = e.Known, StartTemp = e.StartTemp
            });

        foreach (var s in ch.Steps)
            doc.Steps.Add(new StepRecordDoc
            {
                Index = s.Index, StepId = s.StepId, CommandId = s.CommandId, Title = s.Title,
                Termination = s.Termination, Iteration = s.Iteration,
                PlanStart = s.PlanStart, PlanDuration = s.PlanDuration,
                ActualStart = s.ActualStart, ActualEnd = s.ActualEnd, ActualDuration = s.ActualDuration,
                Reason = s.Reason, Status = s.Status, Note = s.Note, ChannelStart = s.ChannelStart,
                ControlMode = s.ControlMode, Phase = s.Phase
            });

        foreach (var e in ch.Events)
            doc.Events.Add(new EventRecordDoc
            {
                At = e.At, Channel = e.Channel, Kind = e.Kind, Text = e.Text,
                User = e.User, StepIndex = e.StepIndex, Before = e.Before, After = e.After
            });
        return doc;
    }

    public static RunRecord ToModel(this RunDoc doc)
    {
        var rec = new RunRecord
        {
            RunId = doc.RunId,
            CreatedAt = doc.CreatedAt,
            Name = doc.Name,
            Operator = doc.Operator,
            ClosedAt = doc.ClosedAt,
            BenchName = doc.BenchName
        };
        foreach (var c in doc.Channels) rec.Append(c.ToModel());
        return rec;
    }

    public static ChannelRun ToModel(this ChannelRunDoc doc)
    {
        var entries = doc.Baseline.Schedule
            .Select(e => new ScheduleEntry(e.Index, e.StepId, e.CommandId, e.Start, e.Duration,
                                           e.Span, e.Repeats, e.Title, e.Termination, e.Known)
            { StartTemp = e.StartTemp })
            .ToList();

        var run = new ChannelRun
        {
            Channel = doc.Channel,
            StartedAt = doc.StartedAt,
            FinishedAt = doc.FinishedAt,
            State = doc.State,
            Operator = doc.Operator,
            Simulated = doc.Simulated,
            Baseline = new RunBaseline
            {
                Recipe = doc.Baseline.Recipe.ToModel(),
                Schedule = Schedule.FromEntries(entries, doc.Baseline.Missing),
                FrozenAt = doc.Baseline.FrozenAt,
                ApprovedBy = doc.Baseline.ApprovedBy
            }
        };

        foreach (var s in doc.Steps)
            run.Append(new StepRecord
            {
                Index = s.Index, StepId = s.StepId, CommandId = s.CommandId, Title = s.Title,
                Termination = s.Termination, Iteration = s.Iteration,
                PlanStart = s.PlanStart, PlanDuration = s.PlanDuration, ChannelStart = s.ChannelStart,
                ActualStart = s.ActualStart, ActualEnd = s.ActualEnd, ActualDuration = s.ActualDuration,
                Reason = s.Reason, Status = s.Status, Note = s.Note,
                ControlMode = s.ControlMode, Phase = s.Phase
            });

        foreach (var e in doc.Events)
            run.Append(new EventRecord
            {
                At = e.At, Channel = e.Channel, Kind = e.Kind, Text = e.Text,
                User = e.User, StepIndex = e.StepIndex, Before = e.Before, After = e.After
            });
        return run;
    }
}
