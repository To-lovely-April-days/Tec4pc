using Tec.Core.Catalog;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 批次的边界（§7.1）。批次 = 同一时段的那几路：先起 CH1、一分钟后再起 CH2
/// 是同一炉；全都停下来之后再按启动，那是下一炉，导出页里该单独占一行。
/// </summary>
public class BatchTests
{
    private static Recipes.Recipe Short() => Harness.RecipeOf("一步",
        Harness.Mk(BuiltinCommands.Wait, ("dur", 1d)));

    [Fact]
    public async Task 跑完一炉再起一炉是两个批次()
    {
        // 从前判据是「记录里一条通道都没有」，于是这一开机永远只有一个批次：
        // 两炉的子记录混在同一条记录里，导出时分不开
        await using var h = new Harness(600);
        await h.ReactorChannelAsync(1);

        h.Engine.EnsureBatch("实验", "张三", "台面");
        h.Engine.StartChannel(1, Short(), "张三");
        await h.Engine.Runner(1)!.Completion;

        Assert.True(h.Engine.EnsureBatch("实验", "张三", "台面"));
        h.Engine.StartChannel(1, Short(), "张三");
        await h.Engine.Runner(1)!.Completion;

        Assert.Equal(2, h.Engine.Batches.Count);
        Assert.All(h.Engine.Batches, b => Assert.Single(b.Channels));
    }

    [Fact]
    public async Task 一路还在跑的时候起另一路_算同一炉()
    {
        await using var h = new Harness(200);
        await h.ReactorChannelAsync(1);
        await h.ReactorChannelAsync(2);

        h.Engine.EnsureBatch("实验", "张三", "台面");
        h.Engine.StartChannel(1, Harness.RecipeOf("等着", Harness.Mk(BuiltinCommands.Wait, ("dur", 600d))), "张三");
        await Task.Delay(120);

        Assert.False(h.Engine.EnsureBatch("实验", "张三", "台面"));
        h.Engine.StartChannel(2, Short(), "张三");

        Assert.Single(h.Engine.Batches);
        Assert.Equal(2, h.Engine.Batches[0].Channels.Count);

        h.Engine.AbortAll("张三");
        foreach (var r in h.Engine.Runners) await r.Completion;
    }

    [Fact]
    public async Task 记录编号当天顺号()
    {
        await using var h = new Harness(600);
        await h.ReactorChannelAsync(1);

        h.Engine.EnsureBatch("实验", "张三", "台面");
        h.Engine.StartChannel(1, Short(), "张三");
        await h.Engine.Runner(1)!.Completion;
        h.Engine.EnsureBatch("实验", "张三", "台面");
        h.Engine.StartChannel(1, Short(), "张三");
        await h.Engine.Runner(1)!.Completion;

        Assert.Equal("EXP-20260101-001", h.Engine.Batches[0].RunId);
        Assert.Equal("EXP-20260101-002", h.Engine.Batches[1].RunId);
    }

    [Fact]
    public async Task 按了运行又没跑起来的空壳批次不留在册上()
    {
        // 「按了运行但一条通道都没编排」留下的空批次，摆进导出页只会占一行，
        // 点进去什么都没有
        await using var h = new Harness(600);
        await h.ReactorChannelAsync(1);

        h.Engine.EnsureBatch("实验", "张三", "台面");     // 什么都没启动
        h.Engine.EnsureBatch("实验", "张三", "台面");
        h.Engine.StartChannel(1, Short(), "张三");
        await h.Engine.Runner(1)!.Completion;

        Assert.Single(h.Engine.Batches);
    }
}
