using Tec.Driver.Abi;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 仿真反应器的热源切换（TEC ⇄ 电加热）：判据与真机 DuoSession 一字不差（需求 §2），
/// 不插硬件也要看得到整套行为——切入按目标判、没配就拒绝、回切要等夹套凉到
/// 阈值 − 滞回、电加热侧只能升不能降、蒸回流撞阈值的两种走法、状态量 heat 的三个值。
/// </summary>
public class SimHeatSourceTests
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

    /// <summary>管线里通道 1 最新的热源状态量；还没发过就是 null。</summary>
    private static double? Heat(Harness h)
        => h.Pipeline.TryLatest(1, "heat", h.Clock.Now, out var s) ? s.Value : null;

    private static double? Tset(Harness h)
        => h.Pipeline.TryLatest(1, "Tset", h.Clock.Now, out var s) ? s.Value : null;

    [Fact]
    public async Task 目标过阈值_切电加热_状态量报已核实_夹套真能上到90以上()
    {
        await using var h = new Harness(600);
        var ch = await h.ReactorChannelAsync(1,
            reactorConfig: ParameterSet.Of(("切换反馈", "有"), ("沸点", 150d)));
        var temp = ch.Capabilities.Get<ITemperatureControl>()!;

        await Until(() => Heat(h) == 0, 5000, "开机该在 TEC 侧");

        await temp.SetTargetAsync(new TempTarget(120), default);

        await Until(() => Heat(h) == 2, 5000, "目标 120 > 90 该切到电加热，接了反馈就报已核实");
        await Until(() => temp.CurrentJacket > 95, 30000, "电加热侧夹套该能越过 90 ℃");
    }

    [Fact]
    public async Task 目标不过阈值_留在TEC侧()
    {
        await using var h = new Harness(600);
        var ch = await h.ReactorChannelAsync(1);
        var temp = ch.Capabilities.Get<ITemperatureControl>()!;

        await temp.SetTargetAsync(new TempTarget(80), default);
        await Until(() => temp.CurrentReactor > 60, 30000, "80 ℃ 目标在 TEC 侧照常升温");

        Assert.Equal(0, Heat(h));
    }

    [Fact]
    public async Task 没配电加热_高目标诚实拒绝_不留下发痕迹()
    {
        await using var h = new Harness(600);
        var ch = await h.ReactorChannelAsync(1,
            reactorConfig: ParameterSet.Of(("电加热切换", "无")));
        var temp = ch.Capabilities.Get<ITemperatureControl>()!;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => temp.SetTargetAsync(new TempTarget(120), default));
        Assert.Contains("阈值", ex.Message);
        Assert.Contains("没配", ex.Message);

        // 拒绝了就没有设定值，也没切挡；阈值以内的目标照常
        await Task.Delay(100);
        Assert.Null(Tset(h));
        await temp.SetTargetAsync(new TempTarget(80), default);
        await Until(() => Tset(h) == 80, 5000, "80 ℃ 目标该正常下发");
        Assert.Equal(0, Heat(h));
    }

    [Fact]
    public async Task 阈值只许往下调_配置里写高了按90算()
    {
        await using var h = new Harness(600);
        var ch = await h.ReactorChannelAsync(1,
            reactorConfig: ParameterSet.Of(("电加热切换阈值", 120d)));
        var temp = ch.Capabilities.Get<ITemperatureControl>()!;

        await temp.SetTargetAsync(new TempTarget(100), default);   // 100 > 90（不是 > 120）→ 切电加热
        await Until(() => Heat(h) >= 1, 5000, "阈值上限 90 是死的，100 ℃ 该切电加热");
    }

    [Fact]
    public async Task 回切要等夹套凉到阈值减滞回_电加热侧只能自然凉()
    {
        await using var h = new Harness(600);
        var ch = await h.ReactorChannelAsync(1, reactorConfig: ParameterSet.Of(("沸点", 150d)));
        var temp = ch.Capabilities.Get<ITemperatureControl>()!;

        await temp.SetTargetAsync(new TempTarget(120), default);
        await Until(() => Heat(h) == 1, 5000, "没接反馈 → 电加热·未核实");
        // 釜内也烧到 100 以上：仿真里不出力的夹套会很快贴向釜温，釜温低的话夹套
        // 一松开就凉到滞回线以下，测不出「烫着不许回切」这一条
        await Until(() => temp.CurrentReactor > 100, 30000, "先把釜和夹套都烧上去");

        // 目标降回 50：夹套还烫着，这一刻不许回切；电加热侧没有制冷执行器，只能自然凉
        await temp.SetTargetAsync(new TempTarget(50), default);
        await Task.Delay(300);
        Assert.Equal(1, Heat(h));
        Assert.True(temp.CurrentJacket > 95, $"Tj = {temp.CurrentJacket:F1}，电加热侧不该主动降温");

        // 凉到 85（90 − 5）以下才回切 TEC，回切那一刻夹套一定已经在阈值 − 滞回之下
        await Until(() => Heat(h) == 0, 60000, "夹套凉到阈值 − 滞回该回切 TEC");
        Assert.True(temp.CurrentJacket <= 86, $"回切时 Tj = {temp.CurrentJacket:F1}，该 ≤ 85");

        // 回到 TEC 侧之后才有主动降温：往 50 走得比 0.5 ℃/min 快得多
        var before = temp.CurrentJacket;
        await Task.Delay(400);
        Assert.True(temp.CurrentJacket < before - 3, "TEC 侧该在主动制冷");
    }

    [Fact]
    public async Task 蒸回流撞阈值_没配电加热就停跟随_目标停在阈值()
    {
        await using var h = new Harness(600);
        var ch = await h.ReactorChannelAsync(1,
            reactorConfig: ParameterSet.Of(("电加热切换", "无"), ("沸点", 150d)));
        var temp = ch.Capabilities.Get<ITemperatureControl>()!;
        var reflux = ch.Capabilities.Get<IRefluxControl>()!;

        await reflux.StartAsync(10, 150, default);
        Assert.True(reflux.Active);

        await Until(() => !reflux.Active, 60000, "Tr+ΔT 越过 90 时电加热用不了，跟随该停");
        await Task.Delay(100);
        Assert.Equal(0, Heat(h));
        Assert.InRange(Tset(h) ?? double.NaN, 89.5, 90.5);       // 目标驻留在阈值上
        Assert.True(temp.CurrentJacket <= 91.5, $"Tj = {temp.CurrentJacket:F1}，没电加热夹套上不过阈值");
    }

    [Fact]
    public async Task 蒸回流撞阈值_配了电加热就切过去继续跟()
    {
        await using var h = new Harness(600);
        var ch = await h.ReactorChannelAsync(1, reactorConfig: ParameterSet.Of(("沸点", 150d)));
        var temp = ch.Capabilities.Get<ITemperatureControl>()!;
        var reflux = ch.Capabilities.Get<IRefluxControl>()!;

        await reflux.StartAsync(10, 150, default);

        await Until(() => Heat(h) >= 1, 60000, "跟随目标越过 90 该走热源切换");
        Assert.True(reflux.Active, "切过去之后跟随不该停");
        await Until(() => temp.CurrentJacket > 100, 60000, "电加热侧跟随该继续往上走");
    }
}
