using Tec.Core.Execution;
using Tec.Core.Persistence;
using Tec.Core.Records;
using Tec.Core.Recipes;
using Tec.Driver.Abi;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// Error 级校验问题要真的挡住启动（CH-5「阻断下发」），
/// 以及事件挂步骤用稳定标识、不用数组下标（CH-6.5）。
///
/// 从前校验器只管把配方页的提示条染红——「错误」与「警告」的差别只剩颜色，
/// 参数超范围的配方照样一按就下发。这一组测试钉死：Error 挡事，Warning 放行。
/// </summary>
public class StartGateTests
{
    private static Recipes.Recipe Bad(double target) => Harness.RecipeOf("超限",
        Harness.Mk(CommandSpecs.Control, ("target", target), ("rate", 2d), ("obj", "釜内 Tr")));

    [Fact]
    public async Task 参数超范围的配方拦下不启动()
    {
        await using var h = new Harness(600);
        await h.ReactorChannelAsync(1);

        // target 500 ℃ 超出字段上限 180 ℃——校验器早就会报 Error，现在它得挡事
        var ex = Assert.Throws<RecipeRejectedException>(
            () => h.Engine.StartChannel(1, Bad(500), "张三"));

        Assert.Contains("不能启动", ex.Message);
        Assert.Contains("高于上限", ex.Message);
        Assert.Equal(1, ex.Channel);

        // 拦下就是没发生：状态没动，记录里一炉都没有
        Assert.True(h.Engine.Runner(1)!.CanStart);
        Assert.Empty(h.Engine.Record.Channels);
    }

    [Fact]
    public async Task 拦下之后换一份好配方照常启动()
    {
        await using var h = new Harness(600);
        await h.ReactorChannelAsync(1);

        Assert.Throws<RecipeRejectedException>(() => h.Engine.StartChannel(1, Bad(500), "张三"));

        var run = h.Engine.StartChannel(1, Harness.RecipeOf("正常",
            Harness.Mk(CommandSpecs.Hold, ("dur", 1d), ("tol", 0.1), ("obj", "釜内 Tr"))), "张三");
        Assert.Equal(ChannelRunState.Running, run.State);

        h.Engine.Runner(1)!.Abort("测试员");
        await h.Engine.Runner(1)!.Completion;
    }

    [Fact]
    public async Task 通道没有的能力也拦()
    {
        await using var h = new Harness(600);
        await h.ReactorChannelAsync(1);          // 只有反应器，没有泵

        var dose = Harness.RecipeOf("加料", Harness.Mk(CommandSpecs.Dose,
            ("pump", "加料泵 1"), ("liq", "甲苯"), ("vol", 10d), ("rate", 1d), ("sync", true)));

        var ex = Assert.Throws<RecipeRejectedException>(
            () => h.Engine.StartChannel(1, dose, "张三"));
        Assert.Contains("CH1", ex.Message);
    }

    [Fact]
    public async Task 只有警告不拦()
    {
        await using var h = new Harness(600);
        await h.ReactorChannelAsync(1);

        // 「按稳定结束但没设超时」只是 Warning——警告是给人看的，不是给闸用的
        var recipe = Harness.RecipeOf("只有警告",
            Harness.Mk(CommandSpecs.Hold, ("dur", 1d), ("tol", 0.1), ("obj", "釜内 Tr")));
        var issues = RecipeValidator.Validate(recipe, h.Catalog, h.Engine.Runner(1)!.Channel);
        Assert.DoesNotContain(issues, i => i.Level == IssueLevel.Error);

        var run = h.Engine.StartChannel(1, recipe, "张三");
        Assert.Equal(ChannelRunState.Running, run.State);

        h.Engine.Runner(1)!.Abort("测试员");
        await h.Engine.Runner(1)!.Completion;
    }

    [Fact]
    public async Task 几条错误一起时消息报第一条并给个总数()
    {
        await using var h = new Harness(600);
        await h.ReactorChannelAsync(1);

        var two = Harness.RecipeOf("两条错",
            Harness.Mk(CommandSpecs.Control, ("target", 500d), ("rate", 2d), ("obj", "釜内 Tr")),
            Harness.Mk(CommandSpecs.Control, ("target", -100d), ("rate", 2d), ("obj", "釜内 Tr")));

        var ex = Assert.Throws<RecipeRejectedException>(
            () => h.Engine.StartChannel(1, two, "张三"));
        Assert.Equal(2, ex.Errors.Count);
        Assert.Contains("共 2 条错误", ex.Message);
        // 完整清单在异常里，每一条都点得出是哪一步
        Assert.All(ex.Errors, e => Assert.NotNull(e.StepId));
    }

    // ── 事件用 StepId（CH-6.5） ─────────────────────────────────────

    [Fact]
    public async Task 热改事件挂的是步骤标识不是下标()
    {
        await using var h = new Harness(600);
        await h.ReactorChannelAsync(1);
        var recipe = Harness.RecipeOf("两段保温",
            Harness.Mk(CommandSpecs.Hold, ("dur", 20d), ("tol", 0.1), ("obj", "釜内 Tr")),
            Harness.Mk(CommandSpecs.Hold, ("dur", 30d), ("tol", 0.1), ("obj", "釜内 Tr")));
        var run = h.Engine.StartChannel(1, recipe, "张三");
        var runner = h.Engine.Runner(1)!;

        Assert.True(runner.ProposeEdit(recipe.Steps[1].StepId,
            ParameterSet.Of(("dur", 5d)), "张三", "放热偏大").Applied);

        var e = run.Events.Single(x => x.Kind == EventKind.ParameterChanged);
        Assert.Equal(recipe.Steps[1].StepId, e.StepId);

        runner.Abort("测试员");
        await runner.Completion;
    }

    [Fact]
    public async Task 事件的步骤标识过归档往返不丢()
    {
        await using var h = new Harness(200);
        await h.ReactorChannelAsync(1);
        var run = h.Engine.StartChannel(1, Harness.RecipeOf("一段",
            Harness.Mk(CommandSpecs.Hold, ("dur", 5d), ("tol", 0.1), ("obj", "釜内 Tr"))), "张三");
        await Task.Delay(250);
        h.Engine.Mark(1, "看到析晶", "张三");
        h.Engine.Runner(1)!.Abort("测试员");
        await h.Engine.Runner(1)!.Completion;

        var back = h.Engine.Record.ToDoc(DateTimeOffset.Now, "test").ToModel();
        var mark = back.Channels[0].Events.Single(e => e.Kind == EventKind.OperatorMark);
        Assert.Equal(run.Events.Single(e => e.Kind == EventKind.OperatorMark).StepId, mark.StepId);
        Assert.NotNull(mark.StepId);
    }

    [Fact]
    public async Task 老档案里的下标读盘时换算回步骤标识()
    {
        // 快照机制之前的归档只存了 StepIndex——模拟一份老档案：
        // 把新写的 StepId 挪进 StepIndex（老下标 = 步骤记录的行号），再读回来
        await using var h = new Harness(200);
        await h.ReactorChannelAsync(1);
        h.Engine.StartChannel(1, Harness.RecipeOf("一段",
            Harness.Mk(CommandSpecs.Hold, ("dur", 5d), ("tol", 0.1), ("obj", "釜内 Tr"))), "张三");
        await Task.Delay(250);
        h.Engine.Mark(1, "看到析晶", "张三");
        h.Engine.Runner(1)!.Abort("测试员");
        await h.Engine.Runner(1)!.Completion;

        var doc = h.Engine.Record.ToDoc(DateTimeOffset.Now, "test");
        var chDoc = doc.Channels[0];
        foreach (var e in chDoc.Events.Where(e => e.StepId is not null))
        {
            var rec = chDoc.Steps.First(s => s.StepId == e.StepId);
            e.StepIndex = rec.Index;             // 老档案的写法
            e.StepId = null;
        }

        var back = doc.ToModel();
        var mark = back.Channels[0].Events.Single(e => e.Kind == EventKind.OperatorMark);
        Assert.NotNull(mark.StepId);
        Assert.Equal(chDoc.Steps.First(s => s.Index == 0).StepId, mark.StepId);
    }

    [Fact]
    public async Task 老档案里指不着的下标放空不瞎猜()
    {
        await using var h = new Harness(200);
        await h.ReactorChannelAsync(1);
        h.Engine.StartChannel(1, Harness.RecipeOf("一段",
            Harness.Mk(CommandSpecs.Hold, ("dur", 5d), ("tol", 0.1), ("obj", "釜内 Tr"))), "张三");
        await Task.Delay(250);
        h.Engine.Mark(1, "看到析晶", "张三");
        h.Engine.Runner(1)!.Abort("测试员");
        await h.Engine.Runner(1)!.Completion;

        var doc = h.Engine.Record.ToDoc(DateTimeOffset.Now, "test");
        var mark = doc.Channels[0].Events.Single(e => e.Kind == EventKind.OperatorMark);
        mark.StepId = null;
        mark.StepIndex = 99;                     // 坏档案：哪一步都对不上

        var back = doc.ToModel();
        // 审计里指错对象比空着更糟——对不上就照实空着
        Assert.Null(back.Channels[0].Events.Single(e => e.Kind == EventKind.OperatorMark).StepId);
    }
}
