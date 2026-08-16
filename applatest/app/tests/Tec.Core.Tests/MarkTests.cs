using Tec.Core.Catalog;
using Tec.Core.Records;
using Tec.Driver.Abi;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 手动标记：操作人在运行途中记的一笔（取样、投料、看到析晶）。
///
/// 这是 GLP 记录的一部分，所以规矩和步骤行一样：只追加不修改，落在**那条通道**
/// 的记录链上，署操作人的名。盯住三件事：
///   · 没跑起来的通道不给标——没有记录链，挂哪儿都是编的
///   · 空标记不进记录（一行没内容的事件是噪声，不是数据）
///   · 标在当前步上，导出时对得回是哪一步出的事
/// </summary>
public class MarkTests
{
    private static Recipes.Recipe Waiting(double seconds = 30)
        => Harness.RecipeOf("等着", Harness.Mk(BuiltinCommands.Wait, ("dur", seconds)));

    [Fact]
    public async Task 标记进记录并署操作人的名()
    {
        await using var h = new Harness(200);
        await h.ReactorChannelAsync(1);
        var run = h.Engine.StartChannel(1, Waiting(), "张三");
        await Task.Delay(200);

        Assert.True(h.Engine.Mark(1, "取样 10 mL", "李四"));

        var ev = Assert.Single(run.Events, e => e.Kind == EventKind.OperatorMark);
        Assert.Equal("取样 10 mL", ev.Text);
        Assert.Equal("李四", ev.User);       // 记的是按按钮的人，不是开这条通道的人
        Assert.Equal(1, ev.Channel);

        h.Engine.Runner(1)!.Abort("测试员");
        await h.Engine.Runner(1)!.Completion;
    }

    [Fact]
    public async Task 没启动的通道标不上()
    {
        await using var h = new Harness(200);
        await h.ReactorChannelAsync(1);

        // 台面上有这条通道，但它没跑——没有运行记录就没有链可挂
        Assert.False(h.Engine.Mark(1, "取样", "张三"));
        Assert.Empty(h.Engine.Record.Channels);
    }

    [Fact]
    public async Task 空标记不进记录()
    {
        await using var h = new Harness(200);
        await h.ReactorChannelAsync(1);
        var run = h.Engine.StartChannel(1, Waiting(), "张三");
        await Task.Delay(200);

        Assert.False(h.Engine.Mark(1, "", "张三"));
        Assert.False(h.Engine.Mark(1, "   ", "张三"));
        Assert.DoesNotContain(run.Events, e => e.Kind == EventKind.OperatorMark);

        h.Engine.Runner(1)!.Abort("测试员");
        await h.Engine.Runner(1)!.Completion;
    }

    [Fact]
    public async Task 前后空白去掉再记()
    {
        await using var h = new Harness(200);
        await h.ReactorChannelAsync(1);
        var run = h.Engine.StartChannel(1, Waiting(), "张三");
        await Task.Delay(200);

        h.Engine.Mark(1, "  投料完成  ", "张三");

        Assert.Equal("投料完成", run.Events.Single(e => e.Kind == EventKind.OperatorMark).Text);

        h.Engine.Runner(1)!.Abort("测试员");
        await h.Engine.Runner(1)!.Completion;
    }

    [Fact]
    public async Task 标在当前那一步上()
    {
        await using var h = new Harness(200);
        await h.ReactorChannelAsync(1);
        var run = h.Engine.StartChannel(1, Waiting(), "张三");
        await Task.Delay(250);

        var cur = run.Current;
        Assert.NotNull(cur);
        h.Engine.Mark(1, "看到析晶", "张三");

        // 导出时要答得出「这一笔是哪一步里出的事」
        Assert.Equal(cur!.Index, run.Events.Single(e => e.Kind == EventKind.OperatorMark).StepIndex);

        h.Engine.Runner(1)!.Abort("测试员");
        await h.Engine.Runner(1)!.Completion;
    }

    [Fact]
    public async Task 标记只落在那一条通道上()
    {
        await using var h = new Harness(200);
        await h.ReactorChannelAsync(1);
        await h.ReactorChannelAsync(2);
        var r1 = h.Engine.StartChannel(1, Waiting(), "张三");
        var r2 = h.Engine.StartChannel(2, Waiting(), "张三");
        await Task.Delay(200);

        h.Engine.Mark(2, "CH2 取样", "张三");

        Assert.DoesNotContain(r1.Events, e => e.Kind == EventKind.OperatorMark);
        Assert.Single(r2.Events, e => e.Kind == EventKind.OperatorMark);

        foreach (var ch in new[] { 1, 2 })
        {
            h.Engine.Runner(ch)!.Abort("测试员");
            await h.Engine.Runner(ch)!.Completion;
        }
    }

    [Fact]
    public async Task 跑完了还能补一笔()
    {
        // 「刚才那炉料看着不对」——收尾之后补记也得记得上，
        // 记录是只追加的链，补记就是往链尾再挂一条
        await using var h = new Harness(600);
        await h.ReactorChannelAsync(1);
        var run = h.Engine.StartChannel(1, Waiting(1), "张三");
        await h.Engine.Runner(1)!.Completion;

        Assert.True(h.Engine.Mark(1, "出料偏黄", "张三"));
        Assert.Single(run.Events, e => e.Kind == EventKind.OperatorMark);
    }
}
