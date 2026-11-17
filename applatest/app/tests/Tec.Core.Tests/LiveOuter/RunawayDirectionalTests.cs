using TecControl.Core.Control;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 【本地改动】（2026-11-16，外环改「按实测实时算」第 1 步，主会话 D12）跑飞检测的分方向入口 <see cref="RunawayDetector.CheckDirectional"/>：
/// 只给「新外环在控」那一路用；旧入口 Check 的行为由 Step1Pins/RunawayLegacyPinTests 钉着（它们在改动前的代码上录的，这里不重复）。
/// 时间序列写死，直接调。缺省构造：背离 0.5 K、饱和 180 s、宽限 120 s。
/// </summary>
public class RunawayDirectionalTests
{
    private static readonly DateTime T0 = new(2026, 11, 16, 9, 0, 0, DateTimeKind.Local);
    private static DateTime At(double s) => T0.AddSeconds(s);

    [Fact]
    public void C1_方向不变时跟旧入口逐拍同结果_同样的计时同样的判()
    {
        var oldOne = new RunawayDetector();
        var newOne = new RunawayDetector();
        oldOne.Start(T0);
        newOne.Start(T0);
        // 制冷顶满：夹套目标 20、夹套从 30 往上漂（背离），中间有一段不饱和；第 340 s 起判跑飞（饱和满 180 s、背离超过 0.5）
        int trips = 0, firstTrip = -1;
        for (var k = 0; k <= 800; k++)
        {
            var t = k * 0.5;
            var sat = !(t >= 150 && t < 160);
            var pv = 30 + 0.004 * t;
            var a = oldOne.Check(At(t), 20, pv, sat);
            var b = newOne.CheckDirectional(At(t), 20, pv, sat ? -1 : 0);
            Assert.Equal(a, b);
            Assert.Equal(oldOne.SaturatedSeconds, newOne.SaturatedSeconds);
            if (b && trips++ == 0) firstTrip = k;
        }
        Assert.Equal(680, firstTrip);                                  // 340 s：两边同一拍开始判
        Assert.Equal(121, trips);
    }

    [Fact]
    public void C1_饱和换方向_计时和背离基准从翻的那一拍重来_旧入口照样接着算()
    {
        // 宽限过了（开控 200 s 以后）：先加热顶满 170 s，再一拍里翻到制冷顶满
        var d = new RunawayDetector();
        var legacy = new RunawayDetector();
        d.Start(T0);
        legacy.Start(T0);
        double t;
        for (t = 200; t < 370; t += 0.5)
        {
            Assert.False(d.CheckDirectional(At(t), 60, 50 - 0.001 * (t - 200), +1));
            Assert.False(legacy.Check(At(t), 60, 50 - 0.001 * (t - 200), true));
        }
        Assert.InRange(d.SaturatedSeconds, 169, 170);
        // 翻到制冷顶满（夹套目标一拍跳到 30）：新入口从这一拍重新计时，起点读数取这一拍的
        Assert.False(d.CheckDirectional(At(t), 30, 49.83, -1));
        Assert.Equal(0, d.SaturatedSeconds);
        Assert.False(legacy.Check(At(t), 30, 49.83, true));
        Assert.True(legacy.SaturatedSeconds > 169);                   // 旧入口：计时还是加热那一段接过来的（原缺陷，原样留着）
        // 再过 15 s：旧入口满 180 s、拿加热段的起点 50 跟现在比（|30 − 50.6| > |30 − 50| + 0.5）→ 判跑飞；新入口才 15 s，不判
        var pv = 50.6;
        Assert.True(legacy.Check(At(t + 15), 30, pv, true));
        Assert.False(d.CheckDirectional(At(t + 15), 30, pv, -1));
        Assert.Equal(15, d.SaturatedSeconds, 6);
        // 新入口：制冷顶满再满 180 s，起点是翻的那一拍（49.83）：背离 0.77 K > 0.5 → 判
        Assert.True(d.CheckDirectional(At(t + 180), 30, pv, -1));
        // 制冷顶满 180 s 但背离没超过 0.5（起点 49.83 → 50.2）：不判
        var d2 = new RunawayDetector();
        d2.Start(T0);
        Assert.False(d2.CheckDirectional(At(300), 30, 49.83, -1));
        Assert.False(d2.CheckDirectional(At(480), 30, 50.2, -1));
    }

    [Fact]
    public void C2_不饱和清计时_Start和Reset之后从头来_方向记忆不误清()
    {
        var d = new RunawayDetector();
        d.Start(T0);
        Assert.False(d.CheckDirectional(At(300), 30, 40, -1));
        Assert.False(d.CheckDirectional(At(400), 30, 40, -1));
        Assert.Equal(100, d.SaturatedSeconds);
        Assert.False(d.CheckDirectional(At(401), 30, 40, 0));          // 不饱和：清
        Assert.Equal(0, d.SaturatedSeconds);
        Assert.False(d.CheckDirectional(At(402), 30, 40, +1));         // 不饱和之后换了方向：本来就是从头算
        Assert.False(d.CheckDirectional(At(500), 30, 40, +1));
        Assert.Equal(98, d.SaturatedSeconds);
        d.Start(At(600));                                              // 开控 / 换表：宽限从头、计时清
        Assert.Equal(0, d.SaturatedSeconds);
        Assert.False(d.CheckDirectional(At(601), 30, 40, +1));          // 方向没变也是「刚开始饱和」
        Assert.Equal(0, d.SaturatedSeconds);
        Assert.False(d.CheckDirectional(At(700), 30, 41, +1));          // 宽限（120 s）内不判
        d.Reset();
        Assert.Equal(0, d.SaturatedSeconds);
        Assert.False(d.CheckDirectional(At(800), 30, 40, -5));          // 方向只看符号
        Assert.False(d.CheckDirectional(At(990), 30, 40.4, -7));
        Assert.Equal(190, d.SaturatedSeconds);
        Assert.True(d.CheckDirectional(At(991), 30, 40.6, -1));         // Reset 后没有宽限：满 180 s、背离 0.6 > 0.5 → 判
    }

    [Fact]
    public void C3_构造函数没动_只有一个_参数名和缺省值照旧()
    {
        var ctor = Assert.Single(typeof(RunawayDetector).GetConstructors());
        Assert.Equal(new[] { "deltaC", "detectSeconds", "graceSeconds" }, ctor.GetParameters().Select(p => p.Name));
        Assert.Equal(new object?[] { 0.5, 180.0, 120.0 }, ctor.GetParameters().Select(p => p.DefaultValue));
        var m = typeof(RunawayDetector).GetMethod(nameof(RunawayDetector.Check))!;
        Assert.Equal(new[] { typeof(DateTime), typeof(double), typeof(double), typeof(bool) }, m.GetParameters().Select(p => p.ParameterType));
    }
}
