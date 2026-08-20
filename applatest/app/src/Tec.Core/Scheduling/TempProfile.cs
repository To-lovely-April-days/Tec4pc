using Tec.Core.Catalog;
using Tec.Core.Recipes;
using Tec.Driver.Abi;

namespace Tec.Core.Scheduling;

/// <summary>
/// 剖面上的一段：这一步占的时间区间，以及区间两端的估算温度。
/// 两端不等 = 这一步在改温度（斜线）；相等 = 温度不动（平线）。
/// </summary>
public sealed record TempSegment(
    int Index, string StepId, TimeSpan Start, TimeSpan End,
    double FromC, double ToC, string Title);

/// <summary>
/// 一条持续状态：从某一步设上，直到被下一条同类指令改掉（或配方走完）。
/// 甘特条画的就是它。
/// </summary>
public sealed record StateSpan(
    string Name, string Value, TimeSpan Start, TimeSpan End, string Module);

/// <summary>
/// 温度剖面。**全部由排期推出来，一个数都不是编的**：
/// 每一步的起止时刻来自 <see cref="Schedule"/>，两端温度来自估算器一路推进的
/// <see cref="EstimationContext.Temperature"/>（控温指令自己知道会把温度带到哪）。
///
/// 用途是「应用前先看一眼这条配方长什么样」，不是运行曲线——运行页画的是
/// 真实采样。两者不要混：这里画的是**计划**，画在同一张图上会让人以为设备真这么走过。
/// </summary>
public sealed class TempProfile
{
    /// <summary>
    /// 会留下持续状态的指令，以及甘特条上怎么称呼它。
    ///
    /// 判据只有一条：**这一步结束之后，它设的东西还在起作用**。搅拌设了转速，
    /// 步骤结束搅拌照转；安全联锁挂上之后一直盯着。控温不算——控温到达即结束，
    /// 之后温度归温度曲线管，不是一条「一直在生效的设定」。
    /// </summary>
    private static readonly string[] Persistent = { CommandSpecs.Stir, BuiltinCommands.Interlock };

    private TempProfile(IReadOnlyList<TempSegment> segments, IReadOnlyList<StateSpan> states,
                        TimeSpan total, double minC, double maxC, bool hasCurve, bool hasLoop)
    {
        Segments = segments;
        States = states;
        Total = total;
        MinC = minC;
        MaxC = maxC;
        HasCurve = hasCurve;
        HasLoop = hasLoop;
    }

    public static readonly TempProfile Empty =
        new(Array.Empty<TempSegment>(), Array.Empty<StateSpan>(), TimeSpan.Zero, 25, 25, false, false);

    public IReadOnlyList<TempSegment> Segments { get; }
    public IReadOnlyList<StateSpan> States { get; }
    public TimeSpan Total { get; }
    public double MinC { get; }
    public double MaxC { get; }

    /// <summary>全程有没有一步真的动过温度。一条水平直线不值得占一块地方。</summary>
    public bool HasCurve { get; }

    /// <summary>
    /// 配方里有会重复的循环。**曲线只画第一轮**：排期把重复的时长记在循环开始那一行的
    /// 跨度上，body 的每一步只有第一轮的起止时刻，后面几轮没有各自的时间点可画。
    /// 界面据此在标题上注一句，不让人以为整条曲线就这么长。
    /// </summary>
    public bool HasLoop { get; }

    public bool IsEmpty => Segments.Count == 0 || Total <= TimeSpan.Zero;

    public static TempProfile Build(Recipe recipe, ICommandCatalog catalog, Schedule plan)
    {
        var segs = new List<TempSegment>();
        var states = new List<StateSpan>();
        var open = new Dictionary<string, (int At, string Name, string Value, string Module)>(StringComparer.Ordinal);
        var total = TimeSpan.Zero;
        var hasLoop = false;

        for (var i = 0; i < plan.Entries.Count && i < recipe.Steps.Count; i++)
        {
            var e = plan.Entries[i];
            var s = recipe.Steps[i];

            if (BuiltinCommands.IsLoopBegin(e.CommandId))
            {
                if (e.Repeats > 1) hasLoop = true;
                continue;                       // 循环行本身不占时间，跨度画出来会跟 body 重叠
            }
            if (BuiltinCommands.IsLoopEnd(e.CommandId)) continue;

            // 用 Duration 不用 Extent：Extent 在循环开始行上是整段跨度，
            // 跟 body 各步的区间是重叠的，一起画就成了两层
            var end = e.Start + e.Duration;
            if (end > total) total = end;

            if (s.Enabled)
                segs.Add(new TempSegment(i, e.StepId, e.Start, end, e.StartTemp, e.EndTemp, e.Title));

            if (!s.Enabled || !Persistent.Contains(e.CommandId)) continue;
            if (!catalog.TryGet(e.CommandId, out var d)) continue;

            // 同一条持续指令再出现一次 = 前一条到此为止（搅拌改转速、改成 0 就是停）
            if (open.Remove(e.CommandId, out var prev))
                states.Add(new StateSpan(prev.Name, prev.Value, plan.Entries[prev.At].Start, e.Start, prev.Module));
            open[e.CommandId] = (i, d.DisplayName, ValueOf(d, s), d.Module);
        }

        // 没被改掉的那些一直生效到配方走完
        foreach (var kv in open)
            states.Add(new StateSpan(kv.Value.Name, kv.Value.Value,
                                     plan.Entries[kv.Value.At].Start, total, kv.Value.Module));

        if (segs.Count == 0) return Empty;

        var lo = double.MaxValue;
        var hi = double.MinValue;
        var moved = false;
        foreach (var g in segs)
        {
            lo = Math.Min(lo, Math.Min(g.FromC, g.ToC));
            hi = Math.Max(hi, Math.Max(g.FromC, g.ToC));
            if (Math.Abs(g.ToC - g.FromC) > 0.05) moved = true;
        }

        states.Sort((a, b) => a.Start.CompareTo(b.Start));
        return new TempProfile(segs, states, total, lo, hi, moved, hasLoop);
    }

    /// <summary>
    /// 甘特条上写的那句值。搅拌报转速（0 就说停机），别的报指令自己的整句描述——
    /// 描述模板是指令自带的，这里不另写一份说法。
    /// </summary>
    private static string ValueOf(CommandDescriptor d, Step s)
    {
        var input = new CommandInput(s.Parameters, s.Rows);
        if (d.Id != CommandSpecs.Stir) return d.SummaryOf(input);
        var rpm = s.Parameters.Num("rpm");
        return rpm <= 0 ? "停机" : Txt.Fx(rpm) + " rpm";
    }

    /// <summary>
    /// y 轴刻度间隔。跨度大就疏一点——20 ℃ 的跨度画 50 一档只剩一根线，
    /// 200 ℃ 的跨度画 10 一档挤成一片灰。
    /// </summary>
    public static double TickStep(double span)
        => span > 120 ? 50 : span > 60 ? 20 : span > 24 ? 10 : 5;

    /// <summary>x 轴刻度间隔（分钟）。同上，按总时长挑一档。</summary>
    public static int TimeStepMinutes(TimeSpan total)
    {
        var m = total.TotalMinutes;
        return m > 720 ? 180 : m > 360 ? 120 : m > 120 ? 60 : m > 40 ? 30 : m > 10 ? 10 : 5;
    }
}
