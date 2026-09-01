using Tec.Driver.Abi;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 仿真双态热模型 + 蒸回流（夹套跟随）。
/// 盯四件事：**夹套控温驱动的真是 Tj**（从前 kind 被无视，Tr 直接照目标走）、
/// 蒸回流的物理形态（Tr 爬到沸点停在平台、Tj 恒高 ΔT）、夹套上限真钳住、
/// 停跟随/停控温两种收尾各是各的语义。
/// </summary>
public class SimRefluxTests
{
    private static async Task Until(Func<bool> cond, int ms, string why)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond())
        {
            Assert.True(sw.ElapsedMilliseconds < ms, $"等待超时：{why}");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task 夹套控温驱动的是Tj_釜内只通过换热跟着走()
    {
        await using var h = new Harness(600);
        var ch = await h.ReactorChannelAsync(1);
        var temp = ch.Capabilities.Get<ITemperatureControl>()!;

        await temp.RampAsync(60, 16, TempChannelKind.Jacket, default);
        var ok = await temp.WaitReachedAsync(60, 0.5, TimeSpan.FromMinutes(30), default);

        Assert.True(ok, "夹套控温的到达判据在 Tj 侧");
        Assert.InRange(temp.CurrentJacket, 58.5, 61.5);
        // 换热有惯性：夹套刚到 60 时釜内绝不可能也到了——到了就说明模型还在把 Tr 当被控量
        Assert.True(temp.CurrentReactor < 55,
            $"Tr = {temp.CurrentReactor:F1} ℃，釜内不该跟夹套同步到达");
    }

    [Fact]
    public async Task 蒸回流_Tr爬到沸点停在平台_Tj恒高ΔT()
    {
        await using var h = new Harness(600);
        var ch = await h.ReactorChannelAsync(1,
            reactorConfig: ParameterSet.Of(("沸点", 90d)));
        var temp = ch.Capabilities.Get<ITemperatureControl>()!;
        var reflux = ch.Capabilities.Get<IRefluxControl>();
        Assert.NotNull(reflux);

        await reflux!.StartAsync(8, 150, default);
        Assert.True(reflux.Active);

        // 升温段：ΔT 8 K × 30 %/min 换热 ≈ 2.4 ℃/min，从 25 ℃ 爬上去
        await Until(() => temp.CurrentReactor > 89, 30000, "Tr 没爬到沸点");

        // 平台段：再跑一阵，Tr 停在沸点不越过，Tj 恒高 ΔT
        await Task.Delay(600);
        Assert.InRange(temp.CurrentReactor, 88.0, 90.3);
        Assert.InRange(temp.CurrentJacket, 95.5, 99.5);     // ≈ 90 + 8

        // 停跟随：目标停在最后一次下发的值上——夹套还守着 Tr+ΔT，不许掉下去
        await reflux.StopAsync(default);
        Assert.False(reflux.Active);
        await Task.Delay(400);
        Assert.True(temp.CurrentJacket > 95,
            $"停跟随后 Tj = {temp.CurrentJacket:F1} ℃，控温目标该驻留在最后的跟随值上");
    }

    [Fact]
    public async Task 蒸回流的夹套上限真钳住()
    {
        await using var h = new Harness(600);
        var ch = await h.ReactorChannelAsync(1);        // 沸点缺省 100，上限设 60 先撞上限
        var temp = ch.Capabilities.Get<ITemperatureControl>()!;
        var reflux = ch.Capabilities.Get<IRefluxControl>()!;

        await reflux.StartAsync(10, 60, default);
        await Until(() => temp.CurrentJacket > 58, 15000, "Tj 没到上限附近");

        // 上限之下釜内慢慢靠向 60，全程 Tj 不许越过 60
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(50);
            Assert.True(temp.CurrentJacket <= 60.6,
                $"Tj = {temp.CurrentJacket:F1} ℃ 越过了 maxTj = 60");
        }
        Assert.True(temp.CurrentReactor < 60.6, "Tr 不可能高过被钳住的夹套");
    }

    [Fact]
    public async Task 停控温把跟随一起清掉_没有谁再把目标写回去()
    {
        await using var h = new Harness(600);
        var ch = await h.ReactorChannelAsync(1);
        var temp = ch.Capabilities.Get<ITemperatureControl>()!;
        var reflux = ch.Capabilities.Get<IRefluxControl>()!;

        await reflux.StartAsync(10, 150, default);
        await Until(() => temp.CurrentReactor > 40, 15000, "回流升温没起来");

        // 安全停机走 ITemperatureControl.StopAsync（SafeStop 的路径）：
        // 跟随环必须一起清，不清的话下一拍它又把夹套目标抬回去
        await temp.StopAsync(default);
        Assert.False(reflux.Active, "停控之后跟随环还挂着");

        var tj = temp.CurrentJacket;
        await Task.Delay(400);
        Assert.True(temp.CurrentJacket <= tj + 0.5,
            $"停控后 Tj 从 {tj:F1} 涨到 {temp.CurrentJacket:F1} ℃——有东西还在写目标");
    }

    [Fact]
    public async Task 配方里的蒸回流步骤按时长跑完并收掉跟随()
    {
        await using var h = new Harness(600);
        var ch = await h.ReactorChannelAsync(1,
            reactorConfig: ParameterSet.Of(("沸点", 60d)));   // 沸点放低，让平台在步内建立
        var reflux = ch.Capabilities.Get<IRefluxControl>()!;

        h.Engine.StartChannel(1, Harness.RecipeOf("回流",
            Harness.Mk(CommandSpecs.Reflux, ("dt", 6d), ("tjmax", 120d), ("dur", 30d))), "王工");
        var runner = h.Engine.Runner(1)!;

        // 步进行中跟随环真开着
        await Until(() => reflux.Active, 10000, "回流步没把跟随环开起来");

        await runner.Completion.WaitAsync(TimeSpan.FromSeconds(20));
        // 步结束（计时到）之后跟随必须收掉——步都没了夹套还在追是事故
        Assert.False(reflux.Active, "回流步结束后跟随环还开着");
        var step = h.Engine.Record.Channels[0].Steps[0];
        Assert.Equal(EndReason.TimerElapsed, step.Reason);
    }
}
