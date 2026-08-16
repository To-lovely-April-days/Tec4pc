using Tec.Core.Catalog;
using Tec.Core.Records;
using Tec.Driver.Abi;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 一路一路各自启停。台面上一台 RD-105 有两个孔，两路共用一台机器，
/// 但工艺是各跑各的：B 孔出问题要单独停，A 孔那一炉不能跟着黄掉。
///
/// 这一组盯两件事：
///   · 动一路不碰另一路（状态、记录、步骤全都不串）
///   · **每一次操作都进记录**——启动 / 暂停 / 继续 / 中止 / 跳步，
///     谁在什么时候做的都要留下。这正是 GLP 要的那条链
/// </summary>
public class ChannelLifecycleTests
{
    private static Recipes.Recipe Waiting(double seconds = 30)
        => Harness.RecipeOf("等着", Harness.Mk(BuiltinCommands.Wait, ("dur", seconds)));

    private static IEnumerable<EventKind> Kinds(ChannelRun r) => r.Events.Select(e => e.Kind);

    [Fact]
    public async Task 两路各自启动互不干扰()
    {
        await using var h = new Harness(200);
        await h.ReactorChannelAsync(1);
        await h.ReactorChannelAsync(2);

        h.Engine.StartChannel(1, Waiting(), "张三");
        await Task.Delay(200);

        // CH2 没启动就该一点动静都没有
        Assert.Equal(ChannelRunState.Running, h.Engine.Runner(1)!.State);
        Assert.Null(h.Engine.Record.Of(2));
        Assert.Single(h.Engine.Record.Channels);

        h.Engine.Runner(1)!.Abort("测试员");
        await h.Engine.Runner(1)!.Completion;
    }

    [Fact]
    public async Task 停一路不影响另一路()
    {
        await using var h = new Harness(200);
        await h.ReactorChannelAsync(1);
        await h.ReactorChannelAsync(2);
        h.Engine.StartChannel(1, Waiting(), "张三");
        h.Engine.StartChannel(2, Waiting(), "张三");
        await Task.Delay(200);

        h.Engine.Runner(2)!.Abort("张三", "操作人停止 CH2");
        await h.Engine.Runner(2)!.Completion;

        Assert.Equal(ChannelRunState.Running, h.Engine.Runner(1)!.State);
        Assert.Equal(ChannelRunState.Aborted, h.Engine.Record.Of(2)!.State);

        h.Engine.Runner(1)!.Abort("测试员");
        await h.Engine.Runner(1)!.Completion;
    }

    [Fact]
    public async Task 暂停只停那一路继续也只继续那一路()
    {
        await using var h = new Harness(200);
        await h.ReactorChannelAsync(1);
        await h.ReactorChannelAsync(2);
        h.Engine.StartChannel(1, Waiting(), "张三");
        h.Engine.StartChannel(2, Waiting(), "张三");
        await Task.Delay(200);

        h.Engine.Runner(1)!.Pause("张三");
        Assert.Equal(ChannelRunState.Paused, h.Engine.Runner(1)!.State);
        Assert.Equal(ChannelRunState.Running, h.Engine.Runner(2)!.State);

        h.Engine.Runner(1)!.Resume("张三");
        Assert.Equal(ChannelRunState.Running, h.Engine.Runner(1)!.State);

        foreach (var ch in new[] { 1, 2 })
        {
            h.Engine.Runner(ch)!.Abort("测试员");
            await h.Engine.Runner(ch)!.Completion;
        }
    }

    [Fact]
    public async Task 收尾之后记录上是已中止不是正在停止()
    {
        // Aborting 是过渡态。记录上留 Aborting 的话，一个早已停下的通道
        // 在界面上永远显示成「正在停止」
        await using var h = new Harness(200);
        await h.ReactorChannelAsync(1);
        var run = h.Engine.StartChannel(1, Waiting(), "张三");
        await Task.Delay(200);

        h.Engine.Runner(1)!.Abort("张三");
        await h.Engine.Runner(1)!.Completion;

        Assert.Equal(ChannelRunState.Aborted, run.State);
        Assert.NotNull(run.FinishedAt);
        // runner 自己回到 Idle，好让操作人再起一趟
        Assert.Equal(ChannelRunState.Idle, h.Engine.Runner(1)!.State);
    }

    [Fact]
    public async Task 跑完了记录上是已完成()
    {
        await using var h = new Harness(600);
        await h.ReactorChannelAsync(1);
        var run = h.Engine.StartChannel(1, Waiting(1), "张三");
        await h.Engine.Runner(1)!.Completion;

        Assert.Equal(ChannelRunState.Completed, run.State);
        Assert.Equal(ChannelRunState.Completed, h.Engine.Runner(1)!.State);
    }

    [Fact]
    public async Task 停掉之后能再起一趟且是新的一条记录()
    {
        await using var h = new Harness(200);
        await h.ReactorChannelAsync(1);
        var first = h.Engine.StartChannel(1, Waiting(), "张三");
        await Task.Delay(200);
        h.Engine.Runner(1)!.Abort("张三");
        await h.Engine.Runner(1)!.Completion;

        var second = h.Engine.StartChannel(1, Waiting(), "张三");
        await Task.Delay(200);

        Assert.NotSame(first, second);
        Assert.Equal(2, h.Engine.Record.Channels.Count);      // 旧的那条不覆盖
        Assert.Equal(ChannelRunState.Aborted, first.State);   // 旧的结局留着
        Assert.Same(second, h.Engine.Record.Of(1));           // 「当前这一趟」是新的

        h.Engine.Runner(1)!.Abort("测试员");
        await h.Engine.Runner(1)!.Completion;
    }

    [Fact]
    public async Task 台面重建后执行器换新的不再指着已经关掉的会话()
    {
        // 台面一动（拖进一台设备）就会重开全部会话、重建全部 Channel 对象。
        // 通道号还是那个号，但对象和它背后的会话都是新的。
        // 老执行器攥着已经 Dispose 的会话，让它接着跑等于对着空气下指令——
        // 界面上「运行中」，釜温一动不动，一声不吭
        await using var h = new Harness(200);
        var first = await h.ReactorChannelAsync(1);
        var runnerBefore = h.Engine.Runner(1);
        Assert.Same(first, runnerBefore!.Channel);

        // 同一个 Channel 对象再接一次，还是同一个执行器
        Assert.Same(runnerBefore, h.Engine.Attach(first));

        // 台面重建：新的 Channel 对象 → 必须换一个执行器
        var rebuilt = await h.ReactorChannelAsync(1);
        Assert.NotSame(first, rebuilt);
        var runnerAfter = h.Engine.Runner(1);
        Assert.NotSame(runnerBefore, runnerAfter);
        Assert.Same(rebuilt, runnerAfter!.Channel);
    }

    [Fact]
    public async Task 台面重建时正在跑的那一路会被中止而不是留在运行中()
    {
        await using var h = new Harness(200);
        await h.ReactorChannelAsync(1);
        var run = h.Engine.StartChannel(1, Waiting(), "张三");
        await Task.Delay(200);

        await h.ReactorChannelAsync(1);          // 重建
        await Task.Delay(200);

        Assert.NotEqual(ChannelRunState.Running, run.State);
        Assert.Contains(run.Events, e => e.Kind == EventKind.Aborted);
    }

    // ── 执行记录 ────────────────────────────────────────────────────

    [Fact]
    public async Task 启动暂停继续中止每一步都进记录()
    {
        await using var h = new Harness(200);
        await h.ReactorChannelAsync(1);
        var run = h.Engine.StartChannel(1, Waiting(), "张三");
        await Task.Delay(200);

        h.Engine.Runner(1)!.Pause("张三");
        h.Engine.Runner(1)!.Resume("李四");
        h.Engine.Runner(1)!.Abort("王五", "操作人停止 CH1");
        await h.Engine.Runner(1)!.Completion;

        Assert.Contains(EventKind.ChannelStarted, Kinds(run));
        Assert.Contains(EventKind.Paused, Kinds(run));
        Assert.Contains(EventKind.Resumed, Kinds(run));
        Assert.Contains(EventKind.Aborted, Kinds(run));
        Assert.Contains(EventKind.ChannelFinished, Kinds(run));

        // 谁按的就署谁的名，不是一律记成开这条通道的人
        Assert.Equal("张三", run.Events.First(e => e.Kind == EventKind.Paused).User);
        Assert.Equal("李四", run.Events.First(e => e.Kind == EventKind.Resumed).User);
        Assert.Equal("王五", run.Events.First(e => e.Kind == EventKind.Aborted).User);
    }

    [Fact]
    public async Task 中止不再混在普通提示里()
    {
        // 从前中止走 EventKind.Note，界面把 Note 过滤掉了，
        // 于是「谁在什么时候停了这一路」在执行记录里根本查不到
        await using var h = new Harness(200);
        await h.ReactorChannelAsync(1);
        var run = h.Engine.StartChannel(1, Waiting(), "张三");
        await Task.Delay(200);

        h.Engine.Runner(1)!.Abort("张三", "操作人停止 CH1");
        await h.Engine.Runner(1)!.Completion;

        var ev = Assert.Single(run.Events, e => e.Kind == EventKind.Aborted);
        Assert.Contains("操作人停止", ev.Text);
    }

    [Fact]
    public async Task 中止时放开暂停闸门不算一次继续()
    {
        // 中止要先放开闸门才收得了尾，但那不是操作人按了「继续」——
        // 记成一条继续的话，记录上「中止」后面紧跟一条「继续」，
        // 读的人会以为有人又把它开起来了
        await using var h = new Harness(200);
        await h.ReactorChannelAsync(1);
        var run = h.Engine.StartChannel(1, Waiting(), "张三");
        await Task.Delay(200);

        h.Engine.Runner(1)!.Pause("张三");
        h.Engine.Runner(1)!.Abort("张三", "操作人停止 CH1");
        await h.Engine.Runner(1)!.Completion;

        Assert.DoesNotContain(run.Events, e => e.Kind == EventKind.Resumed);
        Assert.Single(run.Events, e => e.Kind == EventKind.Paused);
        Assert.Single(run.Events, e => e.Kind == EventKind.Aborted);
    }

    [Fact]
    public async Task 暂停着按跳过要记成一次继续()
    {
        // 这一次是真的从暂停回到了运行，而且是操作人要的——照实记
        await using var h = new Harness(200);
        await h.ReactorChannelAsync(1);
        var run = h.Engine.StartChannel(1, Waiting(), "张三");
        await Task.Delay(200);

        h.Engine.Runner(1)!.Pause("张三");
        h.Engine.Runner(1)!.SkipCurrent("张三", "操作人跳过");

        Assert.Single(run.Events, e => e.Kind == EventKind.StepSkipped);
        Assert.Single(run.Events, e => e.Kind == EventKind.Resumed);

        h.Engine.Runner(1)!.Abort("测试员");
        await h.Engine.Runner(1)!.Completion;
    }

    [Fact]
    public async Task 跳步进记录()
    {
        await using var h = new Harness(200);
        await h.ReactorChannelAsync(1);
        var run = h.Engine.StartChannel(1, Waiting(), "张三");
        await Task.Delay(200);

        h.Engine.Runner(1)!.SkipCurrent("张三", "操作人跳过");

        Assert.Single(run.Events, e => e.Kind == EventKind.StepSkipped);

        h.Engine.Runner(1)!.Abort("测试员");
        await h.Engine.Runner(1)!.Completion;
    }

    [Fact]
    public async Task 事件文本不自带通道前缀()
    {
        // 记录行本身就有「通道」这一列，文本再带一个 CHn，
        // 界面上会读成「CH1 CH1 暂停」
        await using var h = new Harness(200);
        await h.ReactorChannelAsync(1);
        var run = h.Engine.StartChannel(1, Waiting(), "张三");
        await Task.Delay(200);
        h.Engine.Runner(1)!.Pause("张三");
        h.Engine.Runner(1)!.Abort("张三");
        await h.Engine.Runner(1)!.Completion;

        Assert.DoesNotContain(run.Events, e => e.Text.StartsWith("CH", StringComparison.Ordinal));
        // 结束那条要写人话，不能把枚举名漏出来
        var fin = run.Events.Single(e => e.Kind == EventKind.ChannelFinished);
        Assert.Contains("已中止", fin.Text);
        Assert.DoesNotContain("Abort", fin.Text);
    }

    [Fact]
    public async Task 两路的记录各挂各的链()
    {
        await using var h = new Harness(200);
        await h.ReactorChannelAsync(1);
        await h.ReactorChannelAsync(2);
        var r1 = h.Engine.StartChannel(1, Waiting(), "张三");
        var r2 = h.Engine.StartChannel(2, Waiting(), "张三");
        await Task.Delay(200);

        h.Engine.Runner(2)!.Pause("张三");

        Assert.DoesNotContain(r1.Events, e => e.Kind == EventKind.Paused);
        Assert.Single(r2.Events, e => e.Kind == EventKind.Paused);
        Assert.All(r1.Events, e => Assert.Equal(1, e.Channel));
        Assert.All(r2.Events, e => Assert.Equal(2, e.Channel));

        foreach (var ch in new[] { 1, 2 })
        {
            h.Engine.Runner(ch)!.Abort("测试员");
            await h.Engine.Runner(ch)!.Completion;
        }
    }
}
