using Tec.Core.Catalog;
using Tec.Core.Records;
using Tec.Driver.Abi;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 运行中改参数（FR-5.5 / §7.6）。改的是**这一趟**，不是配方本身：
/// 基线一旦冻结永不变，偏差照常按基线算——两份分开摆，才看得出
/// 「这一趟被改过什么」。
/// </summary>
public class HotEditTests
{
    private static Recipes.Recipe TwoHolds() => Harness.RecipeOf("两段保温",
        Harness.Mk(CommandSpecs.Hold, ("dur", 20d), ("tol", 0.1), ("obj", "釜内 Tr")),
        Harness.Mk(CommandSpecs.Hold, ("dur", 30d), ("tol", 0.1), ("obj", "釜内 Tr")));

    [Fact]
    public async Task 改完之后跑的是新的那一份_基线还是老的()
    {
        await using var h = new Harness(600);
        await h.ReactorChannelAsync(1);
        var recipe = TwoHolds();
        var run = h.Engine.StartChannel(1, recipe, "张三");
        var runner = h.Engine.Runner(1)!;
        var target = recipe.Steps[1];

        Assert.True(runner.ProposeEdit(target.StepId, ParameterSet.Of(("dur", 5d)), "张三", "放热偏大").Applied);

        // 现行那一份改了
        var live = runner.LiveSteps.First(s => s.StepId == target.StepId);
        Assert.Equal(5d, live.Parameters.Num("dur"));

        // 基线一个字没动——甘特上的"计划"柱子不该跟着挪
        var frozen = run.Baseline.Recipe.Steps.First(s => s.StepId == target.StepId);
        Assert.Equal(30d, frozen.Parameters.Num("dur"));

        runner.Abort("张三");
        await runner.Completion;
    }

    [Fact]
    public async Task 没改到的参数原样留着()
    {
        // 提案只带改动的键。合并时把没提的那些抹掉，等于顺手把允差清零了
        await using var h = new Harness(600);
        await h.ReactorChannelAsync(1);
        var recipe = TwoHolds();
        h.Engine.StartChannel(1, recipe, "张三");
        var runner = h.Engine.Runner(1)!;
        var target = recipe.Steps[1];

        runner.ProposeEdit(target.StepId, ParameterSet.Of(("dur", 5d)), "张三", "缩短");

        var live = runner.LiveSteps.First(s => s.StepId == target.StepId);
        Assert.Equal(0.1, live.Parameters.Num("tol"));
        Assert.Equal("釜内 Tr", live.Parameters.Str("obj"));

        runner.Abort("张三");
        await runner.Completion;
    }

    [Fact]
    public async Task 跑完了就不给改()
    {
        // 那趟记录已经收口了。照改会往一条结束了的链上再追一条「参数修改」，
        // 读记录的人只能理解成"结束之后又有人动了参数"
        await using var h = new Harness(600);
        await h.ReactorChannelAsync(1);
        var recipe = Harness.RecipeOf("一步", Harness.Mk(BuiltinCommands.Wait, ("dur", 1d)));
        var run = h.Engine.StartChannel(1, recipe, "张三");
        var runner = h.Engine.Runner(1)!;
        await runner.Completion;

        Assert.False(runner.CanEdit);
        var result = runner.ProposeEdit(recipe.Steps[0].StepId, ParameterSet.Of(("dur", 9d)), "张三", "试试");
        Assert.False(result.Applied);
        Assert.Contains("没在运行", result.Message);
        Assert.DoesNotContain(run.Events, e => e.Kind == EventKind.ParameterChanged);
    }

    [Fact]
    public async Task 暂停着也能改()
    {
        // 「不支持热改的指令先暂停再改」是引擎自己给的出路，暂停期间必须真能改
        await using var h = new Harness(600);
        await h.ReactorChannelAsync(1);
        var recipe = TwoHolds();
        h.Engine.StartChannel(1, recipe, "张三");
        var runner = h.Engine.Runner(1)!;
        runner.Pause("张三");

        Assert.True(runner.CanEdit);
        Assert.True(runner.ProposeEdit(recipe.Steps[1].StepId,
                                       ParameterSet.Of(("dur", 7d)), "张三", "暂停时改").Applied);

        runner.Abort("张三");
        await runner.Completion;
    }

    [Fact]
    public async Task 超出设备限值的改动当场退回()
    {
        await using var h = new Harness(600);
        await h.ReactorChannelAsync(1);
        var recipe = Harness.RecipeOf("控温",
            Harness.Mk(BuiltinCommands.Wait, ("dur", 600d)),
            Harness.Mk(CommandSpecs.Control, ("target", 60d), ("rate", 2d), ("obj", "釜内 Tr")));
        var run = h.Engine.StartChannel(1, recipe, "张三");
        var runner = h.Engine.Runner(1)!;

        var result = runner.ProposeEdit(recipe.Steps[1].StepId,
                                        ParameterSet.Of(("rate", 999d)), "张三", "快一点");
        Assert.False(result.Applied);
        Assert.Contains("上限", result.Message);
        Assert.DoesNotContain(run.Events, e => e.Kind == EventKind.ParameterChanged);

        runner.Abort("张三");
        await runner.Completion;
    }

    [Fact]
    public async Task 改当前步要指令自己说支持热改()
    {
        await using var h = new Harness(600);
        await h.ReactorChannelAsync(1);
        // 自然冷却没有声明 SupportsHotEdit：它是"停输出等环境降"，
        // 中途换目标温度没有对应的动作可发
        var recipe = Harness.RecipeOf("冷却",
            Harness.Mk(CommandSpecs.PassiveCool, ("target", 5d), ("timeout", 120d)));
        var run = h.Engine.StartChannel(1, recipe, "张三");
        var runner = h.Engine.Runner(1)!;
        await Task.Delay(120);
        Assert.True(runner.CanEdit);

        var result = runner.ProposeEdit(recipe.Steps[0].StepId,
                                        ParameterSet.Of(("target", 10d)), "张三", "改冷却终点");
        Assert.False(result.Applied);
        Assert.Contains("不支持热改", result.Message);

        runner.Abort("张三");
        await runner.Completion;
    }
}
