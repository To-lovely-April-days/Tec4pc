using TecControl.Core.Control;
using Xunit;

namespace Tec.Core.Tests.Step1Pins;

/// <summary>
/// 【本地改动】C0 跑飞检测旧入口钉子（2026-11-16，外环「按实测实时算」第 1 步动 RunawayDetector 之前，在 22b9ebe 上跑绿）。
/// 第 1 步给新模式加一个分方向计时的新入口（CheckDirectional），旧的 Check(DateTime, double, double, bool) 一字不动、旧模式照调它
/// （HostControlLoop.cs:3648，saturated 在双向形态下 = satHigh || satLow，:3638–3646）。tests/ 下原来没有一条 RunawayDetector 的测试，
/// 这里用写死的时间序列直接调，把旧入口的行为钉住：饱和计时、判据（满 detectSeconds 且误差比饱和起点大过 deltaC，严格大于）、
/// 开控后 graceSeconds 内不判、不饱和一拍就清零、Start / Reset 的作用，以及**方向翻转时计时不清零、背离基准不重取**（现有缺陷，旧入口要保留）。
/// 缺省参数 0.5 K / 180 s / 120 s（构造函数签名另由 F1 接缝哨兵钉）。
/// </summary>
public sealed class RunawayLegacyPinTests
{
    private static readonly DateTime T0 = new(2026, 11, 16, 9, 0, 0, DateTimeKind.Local);

    private static DateTime At(double seconds) => T0.AddTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond));

    [Fact]
    public void 旧入口_不饱和一拍就清零_再饱和从那一拍重新起算_背离基准重取()
    {
        var d = new RunawayDetector();
        Assert.False(d.Check(At(0), 50, 49, true));               // 头一拍饱和：记起点（误差 1 K），不判
        Assert.Equal(0.0, d.SaturatedSeconds);
        Assert.False(d.Check(At(100), 50, 48, true));
        Assert.Equal(100, d.SaturatedSeconds, 9);
        Assert.False(d.Check(At(101), 50, 48, false));            // 不饱和一拍：清零
        Assert.Equal(0.0, d.SaturatedSeconds);
        Assert.False(d.Check(At(102), 50, 48, true));             // 再饱和：从这拍起算，基准换成 48（误差 2 K）
        Assert.Equal(0.0, d.SaturatedSeconds);
        Assert.False(d.Check(At(282), 50, 47.6, true));           // 满 180 s：误差 2.4 ≤ 2 + 0.5 → 不判
        Assert.Equal(180, d.SaturatedSeconds, 9);
        Assert.True(d.Check(At(283), 50, 47.4, true));            // 2.6 > 2.5 → 判跑飞
    }

    [Fact]
    public void 旧入口_缺省参数_饱和满180s且误差比饱和起点大过0点5才判_恰好0点5不判_判了之后每拍照判()
    {
        var d = new RunawayDetector();
        d.Start(At(0));
        Assert.False(d.Check(At(0), 50, 49.0, true));             // 起点误差 1.0
        Assert.False(d.Check(At(60), 50, 48.0, true));            // 背离了但才 60 s
        Assert.Equal(60, d.SaturatedSeconds, 9);
        Assert.False(d.Check(At(179.9), 50, 48.0, true));         // 179.9 s < 180
        Assert.Equal(179.9, d.SaturatedSeconds, 9);
        Assert.False(d.Check(At(180), 50, 48.5, true));           // 满 180 s，误差 1.5 = 1.0 + 0.5（不是严格大于）→ 不判
        Assert.Equal(180, d.SaturatedSeconds, 9);
        Assert.True(d.Check(At(181), 50, 48.49, true));           // 1.51 > 1.5 → 判
        Assert.True(d.Check(At(182), 50, 48.49, true));           // 没有锁存 / 复位：条件还在就接着判真
        Assert.Equal(182, d.SaturatedSeconds, 9);
    }

    [Fact]
    public void 旧入口_开控后120s宽限内不判_计时照走_满120s才判_没Start过就没有宽限()
    {
        // detectSeconds 缩到 30 s，好让「满了检测时长、但还在宽限里」这一段露出来（缺省 180 > 120 时宽限被检测时长盖住）
        var d = new RunawayDetector(detectSeconds: 30);
        d.Start(At(0));
        Assert.False(d.Check(At(0), 50, 49, true));
        Assert.False(d.Check(At(60), 50, 47, true));              // 饱和 60 s ≥ 30、误差 3 > 1.5，但开控才 60 s < 120
        Assert.Equal(60, d.SaturatedSeconds, 9);                  // 宽限里计时照走
        Assert.False(d.Check(At(119.999), 50, 47, true));
        Assert.True(d.Check(At(120), 50, 47, true));              // 开控满 120 s（不是严格大于）→ 判

        var fresh = new RunawayDetector(detectSeconds: 30);       // 从没 Start 过：没有宽限
        Assert.False(fresh.Check(At(0), 50, 49, true));
        Assert.False(fresh.Check(At(29.999), 50, 47, true));
        Assert.True(fresh.Check(At(30), 50, 47, true));
    }

    [Fact]
    public void 旧入口_方向翻转时饱和计时不清零_背离基准不重取_翻过去才80s就按180s判跑飞_现有缺陷旧入口保留()
    {
        // 双向形态（加热棒 + TEC）：回路传进来的 saturated = 顶上限 || 贴下限（HostControlLoop.cs:3638–3646），不带方向。
        // 先加热顶满 100 s（釜内掉 49 → 48.8），然后输出翻到制冷贴满——对旧入口来说只是「还饱和着」：计时接着走、基准还是 49。
        var d = new RunawayDetector();
        d.Start(At(-1000));                                       // 早过了宽限
        Assert.False(d.Check(At(0), 50, 49.0, true));             // 加热顶满起点
        Assert.False(d.Check(At(100), 50, 48.8, true));           // 加热顶满 100 s
        Assert.False(d.Check(At(100.5), 50, 48.8, true));         // 这一拍起输出翻到制冷贴满（调用方看来还是 true）
        Assert.Equal(100.5, d.SaturatedSeconds, 9);               // 计时没清零（分方向的话应当是 0）
        Assert.False(d.Check(At(179), 50, 48.4, true));
        Assert.True(d.Check(At(180), 50, 48.4, true));            // 制冷才贴满 79.5 s，按总饱和 180 s、基准 49 判：|50 − 48.4| = 1.6 > 1.5
        Assert.Equal(180, d.SaturatedSeconds, 9);
    }

    [Fact]
    public void 旧入口_长期饱和但在收敛_不判_饱和时长照实报()
    {
        var d = new RunawayDetector();
        d.Start(At(-1000));
        Assert.False(d.Check(At(0), 50, 40, true));               // 设定超出能力：一直顶满
        for (var s = 60; s <= 1800; s += 60)
            Assert.False(d.Check(At(s), 50, 40 + s / 360.0, true));   // 误差从 10 收到 5
        Assert.Equal(1800, d.SaturatedSeconds, 9);
    }

    [Fact]
    public void 旧入口_误差按绝对值比_被控量穿过设定值跑到另一边也算背离()
    {
        var d = new RunawayDetector();
        Assert.False(d.Check(At(0), 50, 49.0, true));             // 起点误差 1（在下面）
        Assert.False(d.Check(At(180), 50, 51.5, true));           // 在上面 1.5：= 1 + 0.5 → 不判
        Assert.True(d.Check(At(181), 50, 51.6, true));            // 在上面 1.6 > 1.5 → 判
    }

    [Fact]
    public void 旧入口_Start清掉饱和计时并重设宽限_Reset清掉计时也清掉宽限()
    {
        var d = new RunawayDetector();
        Assert.False(d.Check(At(0), 50, 49, true));
        Assert.False(d.Check(At(100), 50, 48, true));
        Assert.Equal(100, d.SaturatedSeconds, 9);
        d.Start(At(100));                                         // 开控（或运行中换形态，HostControlLoop.cs:797 / :1755）
        Assert.Equal(0.0, d.SaturatedSeconds);
        Assert.False(d.Check(At(101), 50, 48, true));             // 重新起算，基准 48
        Assert.Equal(0.0, d.SaturatedSeconds);
        Assert.False(d.Check(At(280), 50, 47, true));             // 179 s
        Assert.True(d.Check(At(281), 50, 47, true));              // 180 s、开控 181 s > 120、误差 3 > 2.5

        d.Reset();
        Assert.Equal(0.0, d.SaturatedSeconds);
        var r = new RunawayDetector(detectSeconds: 30);
        r.Start(At(0));
        r.Reset();                                                // Reset 连宽限一起清：之后马上能判
        Assert.False(r.Check(At(1), 50, 49, true));
        Assert.True(r.Check(At(31), 50, 47, true));
    }

    [Fact]
    public void 旧入口签名不变_Check吃时刻设定被控量饱和四个参数回真假()
    {
        var m = typeof(RunawayDetector).GetMethod(nameof(RunawayDetector.Check), new[] { typeof(DateTime), typeof(double), typeof(double), typeof(bool) });
        Assert.NotNull(m);
        Assert.Equal(typeof(bool), m!.ReturnType);
        Assert.Equal(new[] { "time", "setpoint", "pv", "outputSaturated" }, m.GetParameters().Select(p => p.Name));
        Assert.Equal(new[] { typeof(DateTime) }, typeof(RunawayDetector).GetMethod(nameof(RunawayDetector.Start))!.GetParameters().Select(p => p.ParameterType));
        Assert.Empty(typeof(RunawayDetector).GetMethod(nameof(RunawayDetector.Reset))!.GetParameters());
        var sat = typeof(RunawayDetector).GetProperty(nameof(RunawayDetector.SaturatedSeconds))!;
        Assert.Equal(typeof(double), sat.PropertyType);
        Assert.False(sat.SetMethod!.IsPublic);
    }
}
