using Avalonia.Media;
using Tec.App.Controls;
using Tec.App.Services;
using Tec.Core;
using Tec.Core.Records;

namespace Tec.App.ViewModels;

/// <summary>
/// 由运行记录生成甘特模型。计划条来自**冻结基线**里的 Schedule，
/// 实际条来自 StepRecord——两者共用同一份事实，不再各编一套时长（§13.1）。
/// </summary>
public static class GanttBuilder
{
    private static readonly Color[] Palette =
    {
        Color.Parse("#2f7ed8"), Color.Parse("#2aa87a"), Color.Parse("#c9772b"), Color.Parse("#8a63d2")
    };

    public static Color ColorOf(int channel) => Palette[(channel - 1 + Palette.Length) % Palette.Length];

    public static GanttModel Build(Workspace ws, bool wallClock)
    {
        var rec = ws.Engine.Record;
        var now = ws.Clock.Now;
        var runs = rec.Channels.OrderBy(c => c.Channel).ToList();

        if (runs.Count == 0)
            return new GanttModel { WallClock = wallClock, Span = 600, Now = 0, Origin = now };

        var origin = wallClock ? runs.Min(c => c.StartedAt) : now;
        var span = 600.0;

        foreach (var run in runs)
        {
            var planned = run.Baseline.Schedule.Total.TotalSeconds;
            var elapsed = (run.FinishedAt ?? now).Subtract(run.StartedAt).TotalSeconds;
            var need = wallClock
                ? (run.StartedAt - origin).TotalSeconds + Math.Max(planned, elapsed)
                : Math.Max(planned, elapsed);
            span = Math.Max(span, need * 1.05);
        }

        var model = new GanttModel
        {
            WallClock = wallClock,
            Span = span,
            Origin = wallClock ? origin : now,
            Now = wallClock ? (now - origin).TotalSeconds : span   // 通道基准下"现在"各不相同，画在最右
        };

        foreach (var run in runs)
        {
            // 通道各自启动：墙钟下每条泳道的零点不同，这正是要看的东西
            var shift = wallClock ? (run.StartedAt - origin).TotalSeconds : 0;
            var lane = new GanttLane
            {
                Name = $"CH{run.Channel}",
                Color = ColorOf(run.Channel),
                Note = $"{Fmt.Clock(run.StartedAt)} 起 · 计划 {Fmt.Hms(run.Baseline.Schedule.Total)}"
            };

            foreach (var e in run.Baseline.Schedule.Entries)
            {
                if (e.Extent <= TimeSpan.Zero && e.Repeats == 0) continue;
                lane.Bars.Add(new GanttBar
                {
                    Title = e.Title,
                    PlanStart = shift + e.Start.TotalSeconds,
                    PlanEnd = shift + e.End.TotalSeconds,
                    Loop = e.Repeats > 0
                });
            }

            foreach (var s in run.Steps)
            {
                if (s.ActualStartOffset is not { } off) continue;
                var end = s.ActualDuration is { } d ? off + d : (now - run.StartedAt);
                lane.Bars.Add(new GanttBar
                {
                    Title = s.Title,
                    PlanStart = shift + s.PlanStart.TotalSeconds,
                    PlanEnd = shift + (s.PlanStart + s.PlanDuration).TotalSeconds,
                    ActualStart = shift + off.TotalSeconds,
                    ActualEnd = shift + end.TotalSeconds,
                    Running = s.Status == StepStatus.Running,
                    Bad = s.Status is StepStatus.Failed or StepStatus.Aborted
                });
            }

            model.Lanes.Add(lane);
        }

        return model;
    }
}
