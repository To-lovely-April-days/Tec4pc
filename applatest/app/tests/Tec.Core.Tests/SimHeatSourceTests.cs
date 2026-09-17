using Tec.Driver.Abi;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 仿真反应器的热源切换（TEC ⇄ 电加热）：判据与真机 DuoSession 一字不差（需求 §2），
/// 不插硬件也要看得到整套行为。
///
/// 上半段是「TEC 加热」**启用**那套（按阈值判），配置里都带 TecOn；
/// 下半段是**出厂默认**（不启用）：TEC 只当冷源，升温一律走电加热棒，按冷热判。
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

    /// <summary>「TEC 加热」启用 + 可选的其他配置——按阈值判那套测试都用它。</summary>
    private static ParameterSet TecOn(params (string Key, object? Value)[] more)
        => ParameterSet.Of(new (string, object?)[] { ("TEC加热", "启用") }.Concat(more).ToArray());

    private static double? Tset(Harness h)
        => h.Pipeline.TryLatest(1, "Tset", h.Clock.Now, out var s) ? s.Value : null;

    [Fact]
    public async Task 目标过阈值_切电加热_状态量报已核实_夹套真能上到90以上()
    {
        await using var h = new Harness(600);
        var ch = await h.ReactorChannelAsync(1,
            reactorConfig: TecOn(("切换反馈", "有"), ("沸点", 150d)));
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
        var ch = await h.ReactorChannelAsync(1, reactorConfig: TecOn());
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
            reactorConfig: TecOn(("电加热切换", "无")));
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
            reactorConfig: TecOn(("电加热切换阈值", 120d)));
        var temp = ch.Capabilities.Get<ITemperatureControl>()!;

        await temp.SetTargetAsync(new TempTarget(100), default);   // 100 > 90（不是 > 120）→ 切电加热
        await Until(() => Heat(h) >= 1, 5000, "阈值上限 90 是死的，100 ℃ 该切电加热");
    }

    [Fact]
    public async Task 回切要等夹套凉到阈值减滞回_电加热侧只能自然凉()
    {
        await using var h = new Harness(600);
        var ch = await h.ReactorChannelAsync(1, reactorConfig: TecOn(("沸点", 150d)));
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
            reactorConfig: TecOn(("电加热切换", "无"), ("沸点", 150d)));
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
        var ch = await h.ReactorChannelAsync(1, reactorConfig: TecOn(("沸点", 150d)));
        var temp = ch.Capabilities.Get<ITemperatureControl>()!;
        var reflux = ch.Capabilities.Get<IRefluxControl>()!;

        await reflux.StartAsync(10, 150, default);

        await Until(() => Heat(h) >= 1, 60000, "跟随目标越过 90 该走热源切换");
        Assert.True(reflux.Active, "切过去之后跟随不该停");
        await Until(() => temp.CurrentJacket > 100, 60000, "电加热侧跟随该继续往上走");
    }

    // ── 出厂默认：「TEC 加热」不启用——所有加热都走电加热棒 ─────────────

    [Fact]
    public async Task 默认不启用TEC加热_远低于阈值的升温也切电加热()
    {
        await using var h = new Harness(600);
        var ch = await h.ReactorChannelAsync(1);
        var temp = ch.Capabilities.Get<ITemperatureControl>()!;

        await Until(() => Heat(h) == 0, 5000, "开机该在 TEC 侧");
        await temp.SetTargetAsync(new TempTarget(60), default);

        // 60 ℃ 离 90 的阈值还远，但 TEC 加热没启用——升温只有电加热棒这一条路
        await Until(() => Heat(h) == 1, 5000, "不启用 TEC 加热时，升温一律切电加热");
        await Until(() => temp.CurrentReactor > 50, 30000, "切过去之后该照常升温");
    }

    [Fact]
    public async Task 默认不启用TEC加热_降温回TEC_升降一趟继电器各切一次()
    {
        await using var h = new Harness(600);
        var ch = await h.ReactorChannelAsync(1);
        var temp = ch.Capabilities.Get<ITemperatureControl>()!;

        await temp.SetTargetAsync(new TempTarget(60), default);
        await Until(() => Heat(h) == 1, 5000, "升温 → 电加热");
        await Until(() => temp.CurrentJacket > 55, 30000, "先到温");

        // 掉头降温：夹套早就在 85 ℃ 以下了，这一刻就能接回 TEC
        await temp.SetTargetAsync(new TempTarget(20), default);
        await Until(() => Heat(h) == 0, 10000, "降温 → 回 TEC");
        await Until(() => temp.CurrentJacket < 40, 30000, "回到 TEC 侧才有主动制冷");
    }

    [Fact]
    public async Task 默认不启用TEC加热_TEC侧升不上去_换挡之前只会自然凉()
    {
        await using var h = new Harness(600);
        var ch = await h.ReactorChannelAsync(1,
            reactorConfig: ParameterSet.Of(("电加热切换", "无")));   // 没有电加热通路
        var temp = ch.Capabilities.Get<ITemperatureControl>()!;

        // 先降到 10 ℃（TEC 干得了），再要求升回 40 ℃——升温没有执行器
        await temp.SetTargetAsync(new TempTarget(10), default);
        await Until(() => temp.CurrentReactor < 12, 30000, "TEC 制冷照常");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => temp.SetTargetAsync(new TempTarget(40), default));
        Assert.Contains("TEC 加热", ex.Message);
        Assert.Contains("没配 IO8R", ex.Message);

        // 拒绝了就没改目标：还守着 10 ℃，不会闷头往上爬
        await Task.Delay(400);
        Assert.InRange(Tset(h) ?? double.NaN, 9.5, 10.5);
        Assert.True(temp.CurrentReactor < 15, $"Tr = {temp.CurrentReactor:F1}，不该升上去");
    }

    [Fact]
    public async Task 默认不启用TEC加热_死区之内不换挡()
    {
        await using var h = new Harness(600);
        var ch = await h.ReactorChannelAsync(1, reactorConfig: ParameterSet.Of(("热源死区", 5d)));
        var temp = ch.Capabilities.Get<ITemperatureControl>()!;

        await Until(() => Heat(h) == 0, 5000, "开机在 TEC 侧");
        await temp.SetTargetAsync(new TempTarget(28), default);   // 差 3 K < 死区 5 K

        await Task.Delay(500);
        Assert.Equal(0, Heat(h));                                  // 没去扳继电器
        Assert.InRange(Tset(h) ?? double.NaN, 27.5, 28.5);         // 目标照常下发
    }

    [Fact]
    public async Task 默认不启用TEC加热_蒸回流全程走电加热()
    {
        await using var h = new Harness(600);
        var ch = await h.ReactorChannelAsync(1, reactorConfig: ParameterSet.Of(("沸点", 60d)));
        var temp = ch.Capabilities.Get<ITemperatureControl>()!;
        var reflux = ch.Capabilities.Get<IRefluxControl>()!;

        await reflux.StartAsync(8, 120, default);

        // 目标 Tr+8 从一开始就在夹套之上——不启用 TEC 加热时，第一拍就得切电加热
        await Until(() => Heat(h) >= 1, 10000, "跟随升温该切电加热");
        Assert.True(reflux.Active);
        await Until(() => temp.CurrentReactor > 58, 60000, "照常爬到沸点平台");
    }

    [Fact]
    public async Task 默认不启用TEC加热_没有电加热_蒸回流当场拒绝()
    {
        await using var h = new Harness(600);
        var ch = await h.ReactorChannelAsync(1,
            reactorConfig: ParameterSet.Of(("电加热切换", "无"), ("沸点", 150d)));
        var reflux = ch.Capabilities.Get<IRefluxControl>()!;
        var temp = ch.Capabilities.Get<ITemperatureControl>()!;

        // 跟随目标永远是 Tr+ΔT，整段都在升温——开不了就当场说，
        // 不要先答应下来、跑起来再静默停掉（与真机 StartRefluxAsync 同一句话）
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => reflux.StartAsync(8, 120, default));
        Assert.Contains("TEC 加热", ex.Message);
        Assert.False(reflux.Active);

        await Task.Delay(400);
        Assert.Equal(0, Heat(h));
        Assert.True(temp.CurrentReactor < 30, $"Tr = {temp.CurrentReactor:F1}，没答应就不该升温");
    }

    [Fact]
    public async Task 仿真里把TEC加热打开也是当场生效()
    {
        await using var h = new Harness(600);
        var cfg = new ParameterSet();                       // 出厂默认：不启用
        var ch = await h.ReactorChannelAsync(1, reactorConfig: cfg);
        var temp = ch.Capabilities.Get<ITemperatureControl>()!;

        await temp.SetTargetAsync(new TempTarget(60), default);
        await Until(() => Heat(h) == 1, 5000, "不启用 → 升温走电加热");

        cfg["TEC加热"] = "启用";                             // 属性栏上扳开关
        await Until(() => Heat(h) == 0, 10000, "60 ℃ 没过阈值、夹套也不烫，该当场把 TEC 接回来");
        Assert.True(ch.Capabilities.Get<IHeatSource>()!.TecHeating);
    }

    [Fact]
    public async Task 热源能力上报_界面据此说清这一路能往哪个方向出力()
    {
        await using var h = new Harness(600);
        var off = await h.ReactorChannelAsync(1);
        var on = await h.ReactorChannelAsync(2, reactorConfig: TecOn());

        var a = off.Capabilities.Get<IHeatSource>();
        var b = on.Capabilities.Get<IHeatSource>();

        Assert.NotNull(a);
        Assert.False(a!.TecHeating);       // 默认不启用 → 界面写「TEC（只制冷）」
        Assert.True(a.ElectricAvailable);
        Assert.False(a.OnElectric);
        Assert.NotNull(b);
        Assert.True(b!.TecHeating);        // 启用 → 界面写「TEC」
    }
}
