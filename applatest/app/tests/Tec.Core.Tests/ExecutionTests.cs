using Tec.Core.Catalog;
using Tec.Core.Records;
using Tec.Core.Scheduling;
using Tec.Drivers.Simulator;
using Tec.Driver.Abi;
using Xunit;

namespace Tec.Core.Tests;

public class ExecutionTests
{
    [Fact]
    public async Task 记录在步骤开始时就建行而不是跑完才写()
    {
        await using var h = new Harness(200);
        await h.ReactorChannelAsync(1);
        var recipe = Harness.RecipeOf("等待", Harness.Mk(BuiltinCommands.Wait, ("dur", 10d)));

        var run = h.Engine.StartChannel(1, recipe, "测试员");
        await Task.Delay(300);

        Assert.NotEmpty(run.Steps);
        var current = run.Current;
        Assert.NotNull(current);
        Assert.Equal(StepStatus.Running, current!.Status);
        Assert.NotNull(current.ActualStart);

        h.Engine.Runner(1)!.Abort("测试员");
        await h.Engine.Runner(1)!.Completion;
    }

    [Fact]
    public async Task 基线在启动时冻结之后改配方不动它()
    {
        await using var h = new Harness(600);
        await h.ReactorChannelAsync(1);
        var recipe = Harness.RecipeOf("冻结", Harness.Mk(BuiltinCommands.Wait, ("dur", 1d)));

        var run = h.Engine.StartChannel(1, recipe, "测试员");
        var frozen = run.Baseline.Schedule.Total;

        recipe.Steps.Add(Harness.Mk(BuiltinCommands.Wait, ("dur", 60d)));

        Assert.Equal(frozen, run.Baseline.Schedule.Total);
        Assert.Single(run.Baseline.Recipe.Steps);

        await h.Engine.Runner(1)!.Completion;
    }

    [Fact]
    public async Task 两种偏差分开算()
    {
        await using var h = new Harness(200);
        await h.ReactorChannelAsync(1);
        var recipe = Harness.RecipeOf("两步",
            Harness.Mk(BuiltinCommands.Wait, ("dur", 1d)),
            Harness.Mk(BuiltinCommands.Wait, ("dur", 1d)));

        var run = h.Engine.StartChannel(1, recipe, "测试员");
        await h.Engine.Runner(1)!.Completion;

        Assert.Equal(2, run.Steps.Count);
        var second = run.Steps[1];

        Assert.NotNull(second.StartDeviation);
        Assert.NotNull(second.DurationDeviation);
        // 开始偏差来自前面累计，时长偏差只看这一步自己
        Assert.Equal(TimeSpan.FromSeconds(60), second.PlanStart);
        Assert.Equal(TimeSpan.FromSeconds(60), second.PlanDuration);
        Assert.True(second.DurationDeviation!.Value.Duration() < TimeSpan.FromSeconds(20),
            $"时长偏差 {second.DurationDeviation} 不该这么大");
    }

    [Fact]
    public async Task 计时到的步骤不会跑短()
    {
        await using var h = new Harness(200);
        await h.ReactorChannelAsync(1);
        var recipe = Harness.RecipeOf("计时",
            Harness.Mk(BuiltinCommands.Wait, ("dur", 2d)));

        var run = h.Engine.StartChannel(1, recipe);
        await h.Engine.Runner(1)!.Completion;

        var step = run.Steps[0];
        Assert.Equal(TerminationKind.Timer, step.Termination);
        Assert.Equal(EndReason.TimerElapsed, step.Reason);
        // 原型上出过"计时到却跑短 99 秒"的自相矛盾记录；实际时长只能 ≥ 计划
        Assert.True(step.ActualDuration >= step.PlanDuration - TimeSpan.FromSeconds(2),
            $"实际 {step.ActualDuration} < 计划 {step.PlanDuration}");
    }

    [Fact]
    public async Task 通道各自启动各记各的零点()
    {
        await using var h = new Harness(600);
        await h.ReactorChannelAsync(1);
        await h.ReactorChannelAsync(2);
        var recipe = Harness.RecipeOf("并行", Harness.Mk(BuiltinCommands.Wait, ("dur", 5d)));

        var a = h.Engine.StartChannel(1, recipe);
        await Task.Delay(120);
        var b = h.Engine.StartChannel(2, recipe);

        Assert.True(b.StartedAt > a.StartedAt);
        Assert.Equal(TimeSpan.Zero, a.Steps[0].PlanStart);
        Assert.Equal(TimeSpan.Zero, b.Steps[0].PlanStart);   // 各自的零点，不是批次零点

        h.Engine.AbortAll();
        await h.Engine.Runner(1)!.Completion;
        await h.Engine.Runner(2)!.Completion;
    }

    [Fact]
    public async Task 升温到不了目标就按超时结束而不是永远挂住()
    {
        await using var h = new Harness(2000);
        await h.ReactorChannelAsync(1);
        var recipe = Harness.RecipeOf("够不着",
            Harness.Mk(CommandSpecs.PassiveCool, ("target", -35d), ("timeout", 2d)));

        var run = h.Engine.StartChannel(1, recipe);
        await h.Engine.Runner(1)!.Completion;

        var step = run.Steps[0];
        Assert.Equal(EndReason.Timeout, step.Reason);
        Assert.Equal(StepStatus.Done, step.Status);
        Assert.False(string.IsNullOrEmpty(step.Note));
    }

    [Fact]
    public async Task 循环体每一轮都写一条记录且计划时间跟着推移()
    {
        await using var h = new Harness(2000);
        await h.ReactorChannelAsync(1);
        var recipe = Harness.RecipeOf("三轮",
            Harness.Mk(BuiltinCommands.LoopBegin, ("by", "按次数"), ("n", 3d)),
            Harness.Mk(BuiltinCommands.Wait, ("dur", 1d)),
            Harness.Mk(BuiltinCommands.LoopEnd));

        var run = h.Engine.StartChannel(1, recipe);
        await h.Engine.Runner(1)!.Completion;

        Assert.Equal(3, run.Steps.Count);
        Assert.Equal(new[] { 1, 2, 3 }, run.Steps.Select(s => s.Iteration).ToArray());
        Assert.Equal(TimeSpan.Zero, run.Steps[0].PlanStart);
        Assert.Equal(TimeSpan.FromSeconds(60), run.Steps[1].PlanStart);
        Assert.Equal(TimeSpan.FromSeconds(120), run.Steps[2].PlanStart);
    }

    [Fact]
    public async Task 暂停继续写进事件而不是悄悄发生()
    {
        await using var h = new Harness(200);
        await h.ReactorChannelAsync(1);
        var recipe = Harness.RecipeOf("暂停",
            Harness.Mk(BuiltinCommands.Wait, ("dur", 1d)),
            Harness.Mk(BuiltinCommands.Wait, ("dur", 1d)));

        var run = h.Engine.StartChannel(1, recipe);
        var runner = h.Engine.Runner(1)!;
        runner.Pause("测试员");
        Assert.True(runner.IsPaused);
        runner.Resume("测试员");
        await runner.Completion;

        Assert.Contains(run.Events, e => e.Kind == EventKind.Paused);
        Assert.Contains(run.Events, e => e.Kind == EventKind.Resumed);
        Assert.Contains(run.Events, e => e.Kind == EventKind.ChannelStarted);
    }

    [Fact]
    public async Task 运行中改参数留下改前改后与操作人()
    {
        await using var h = new Harness(60);
        await h.ReactorChannelAsync(1);
        var recipe = Harness.RecipeOf("热改",
            Harness.Mk(BuiltinCommands.Wait, ("dur", 10d)),
            Harness.Mk(BuiltinCommands.Wait, ("dur", 10d)));

        var run = h.Engine.StartChannel(1, recipe);
        var runner = h.Engine.Runner(1)!;
        var target = recipe.Steps[1];

        var result = runner.ProposeEdit(target.StepId, ParameterSet.Of(("dur", 1d)), "管理员", "缩短保温");
        Assert.True(result.Applied, result.Message);

        var audit = run.Events.FirstOrDefault(e => e.Kind == EventKind.ParameterChanged);
        Assert.NotNull(audit);
        Assert.Equal("管理员", audit!.User);
        Assert.NotEqual(audit.Before, audit.After);

        // 基线不变
        Assert.Equal(TimeSpan.FromMinutes(20), run.Baseline.Schedule.Total);

        runner.Abort("测试员");
        await runner.Completion;
    }

    [Fact]
    public async Task 缺能力的指令在装载时就被拦下而不是跑到一半报错()
    {
        await using var h = new Harness(600);
        var ch = await h.ReactorChannelAsync(1);          // 没有泵
        var recipe = Harness.RecipeOf("要加料",
            Harness.Mk(CommandSpecs.DoseRate, ("vol", 2d), ("rate", 1d)));

        var issues = Recipes.RecipeValidator.Validate(recipe, h.Catalog, ch);
        Assert.Contains(issues, i => i.Level == Recipes.IssueLevel.Error && i.Code == "capability");
    }

    [Fact]
    public async Task 共享泵在两个通道之间排队并把等待写进记录()
    {
        await using var h = new Harness(400);
        await h.ReactorChannelAsync(1, withPump: true);
        await h.ReactorChannelAsync(2, withPump: true);
        h.Arbiter.Declare("P1", 1);
        h.Engine.ResourceOf = (_, id) => id.StartsWith("tec.dose.")
            ? new Execution.ResourceNeed("P1", Execution.ResourcePolicy.Queue)
            : null;

        var recipe = Harness.RecipeOf("加料",
            Harness.Mk(CommandSpecs.DoseRate, ("vol", 4d), ("rate", 1d)));

        var a = h.Engine.StartChannel(1, recipe);
        var b = h.Engine.StartChannel(2, recipe);
        await h.Engine.Runner(1)!.Completion;
        await h.Engine.Runner(2)!.Completion;

        // 同一台泵不可能被两个通道同时占用：两段实际执行区间必须不重叠
        var sa = a.Steps[0];
        var sb = b.Steps[0];
        Assert.NotNull(sa.ActualStart);
        Assert.NotNull(sb.ActualStart);
        var aFrom = sa.ActualStart!.Value;
        var aTo = sa.ActualEnd ?? aFrom;
        var bFrom = sb.ActualStart!.Value;
        var bTo = sb.ActualEnd ?? bFrom;
        Assert.True(aTo <= bFrom || bTo <= aFrom,
            $"CH1 [{aFrom:HH:mm:ss}–{aTo:HH:mm:ss}] 与 CH2 [{bFrom:HH:mm:ss}–{bTo:HH:mm:ss}] 重叠了");

        // 排队的那一条要在记录里说明为什么晚
        var waited = a.Events.Concat(b.Events).Any(e => e.Kind == EventKind.ResourceWait);
        Assert.True(waited, "等待共享资源必须写进事件，否则事后查不出这步为什么晚");
    }
}
