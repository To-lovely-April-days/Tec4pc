using Tec.Core.Catalog;
using Tec.Core.Recipes;
using Tec.Driver.Abi;

namespace Tec.Core.Scheduling;

public sealed record ScheduleEntry(
    int Index,
    string StepId,
    string CommandId,
    TimeSpan Start,
    TimeSpan Duration,
    // Span：循环开始行专用，覆盖全部轮次的跨度；非循环行为 0
    TimeSpan Span,
    int Repeats,
    string Title,
    TerminationKind Termination,
    bool Known)
{
    /// <summary>甘特条的实际长度：普通行取 Duration，循环开始行取 Span。</summary>
    public TimeSpan Extent => Span > Duration ? Span : Duration;
    public TimeSpan End => Start + Extent;
}

/// <summary>
/// 排期。**唯一来源**——配方页的预计开始/耗时、甘特条形、趋势时间轴全部读这一份。
/// 原型上吃过亏：甘特自己按步数等分编了一套时长，卡片写 6:50:00、甘特只占 1/7 宽（§4.4 第 3 条）。
/// </summary>
public sealed class Schedule
{
    private Schedule(IReadOnlyList<ScheduleEntry> entries, IReadOnlyList<string> missingCommands)
    {
        Entries = entries;
        MissingCommands = missingCommands;
        var total = TimeSpan.Zero;
        foreach (var e in entries) if (e.End > total) total = e.End;
        Total = total;
    }

    public IReadOnlyList<ScheduleEntry> Entries { get; }
    public TimeSpan Total { get; }
    /// <summary>配方引用了但目录里没有的指令。配方照样能打开，界面据此提示"缺少驱动"。</summary>
    public IReadOnlyList<string> MissingCommands { get; }

    public static readonly Schedule Empty = new(Array.Empty<ScheduleEntry>(), Array.Empty<string>());

    public ScheduleEntry? ByStepId(string stepId)
    {
        foreach (var e in Entries) if (e.StepId == stepId) return e;
        return null;
    }

    /// <summary>
    /// 串行推进：升温耗时取决于当前温度，所以估算必须按顺序走一遍整条配方。
    /// 循环用栈处理，与原型 schedule() 一致。
    /// </summary>
    public static Schedule Build(Recipe recipe, ICommandCatalog catalog, EstimationContext? seed = null)
    {
        var ctx = (seed ?? new EstimationContext()).Clone();
        var entries = new List<ScheduleEntry>(recipe.Steps.Count);
        var missing = new List<string>();
        var stack = new Stack<LoopFrame>();
        var t = TimeSpan.Zero;

        for (var i = 0; i < recipe.Steps.Count; i++)
        {
            var s = recipe.Steps[i];
            var known = catalog.TryGet(s.CommandId, out var d);
            if (!known && !missing.Contains(s.CommandId)) missing.Add(s.CommandId);

            var input = new CommandInput(s.Parameters, s.Rows);
            var title = known ? SafeDescribe(d, input) : $"缺少驱动：{s.CommandId}";
            var kind = known ? d.Termination : TerminationKind.Immediate;

            if (!s.Enabled)
            {
                entries.Add(new ScheduleEntry(i, s.StepId, s.CommandId, t, TimeSpan.Zero, TimeSpan.Zero, 0,
                    title, kind, known));
                continue;
            }

            if (BuiltinCommands.IsLoopBegin(s.CommandId))
            {
                entries.Add(new ScheduleEntry(i, s.StepId, s.CommandId, t, TimeSpan.Zero, TimeSpan.Zero,
                    BuiltinCommands.RepeatsOf(input), title, kind, known));
                stack.Push(new LoopFrame(t, i, BuiltinCommands.RepeatsOf(input)));
                continue;
            }

            if (BuiltinCommands.IsLoopEnd(s.CommandId))
            {
                entries.Add(new ScheduleEntry(i, s.StepId, s.CommandId, t, TimeSpan.Zero, TimeSpan.Zero, 0,
                    title, kind, known));
                if (stack.Count > 0)
                {
                    var frame = stack.Pop();
                    var one = t - frame.At;
                    t = frame.At + TimeSpan.FromTicks(one.Ticks * frame.Repeats);
                    var open = entries[frame.Index];
                    entries[frame.Index] = open with
                    {
                        Span = TimeSpan.FromTicks(one.Ticks * frame.Repeats),
                        Repeats = frame.Repeats
                    };
                }
                continue;
            }

            var dur = TimeSpan.Zero;
            if (known)
            {
                try { dur = d.Estimate(input, ctx); }
                catch { dur = TimeSpan.Zero; }
                if (dur < TimeSpan.Zero) dur = TimeSpan.Zero;
            }

            entries.Add(new ScheduleEntry(i, s.StepId, s.CommandId, t, dur, TimeSpan.Zero, 0, title, kind, known));
            t += dur;
        }

        // 循环开始没配对：把它当普通零长行，排期照样能算出来，校验器负责报错
        return new Schedule(entries, missing);
    }

    private static string SafeDescribe(CommandDescriptor d, CommandInput p)
    {
        try { return d.Describe(p); }
        catch { return d.DisplayName; }
    }

    private readonly record struct LoopFrame(TimeSpan At, int Index, int Repeats);
}
