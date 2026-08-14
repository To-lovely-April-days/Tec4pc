using TecControl.Core.Control;

namespace TecControl.Core.Tests;

public class RunawayDetectorTests
{
    private static readonly DateTime T0 = new(2026, 1, 1);

    private static RunawayDetector Started()
    {
        var d = new RunawayDetector(deltaC: 0.5, detectSeconds: 180, graceSeconds: 120);
        d.Start(T0);
        return d;
    }

    [Fact]
    public void NormalApproach_UnderSaturation_DoesNotTrip()
    {
        // 启动爬坡：输出饱和但温度在向设定值靠近 —— 不应触发
        var d = Started();
        var pv = 22.0;
        for (var s = 0; s <= 900; s++)
        {
            pv -= 0.01; // 朝 -10℃ 靠近
            Assert.False(d.Check(T0.AddSeconds(s), -10, pv, outputSaturated: true));
        }
    }

    [Fact]
    public void StartupOvershootWithinGrace_DoesNotTrip()
    {
        // 现场实测误报场景：自整定停止后温度正在回暖，此时启动控温，
        // 热惯性使温度在最初 1~2 分钟继续升高 —— 不应被判为方向接反
        var d = Started();
        var pv = -12.0;
        var tripped = false;
        for (var s = 0; s <= 110; s += 1)
        {
            pv += 0.02; // 回暖中，2 分钟升约 2℃
            tripped |= d.Check(T0.AddSeconds(s), -10, pv, outputSaturated: true);
        }
        Assert.False(tripped, "启动宽限期内的热惯性回暖不应触发跑飞保护");
    }

    [Fact]
    public void SustainedDivergence_TripsAfterGraceAndWindow()
    {
        // 真·方向接反：宽限期过后仍持续背离
        var d = Started();
        var pv = 21.6;
        var tripped = false;
        var t = 0;
        while (t <= 1200 && !tripped)
        {
            pv += 0.01; // 持续远离 -10℃
            tripped = d.Check(T0.AddSeconds(t), -10, pv, outputSaturated: true);
            t++;
        }
        Assert.True(tripped, "持续背离应在宽限期+检测窗口后触发");
        Assert.True(t >= 120, "不应早于宽限期触发");
    }

    [Fact]
    public void NotSaturated_ResetsWindow()
    {
        var d = Started();
        Assert.False(d.Check(T0.AddSeconds(200), -10, 0, true));
        Assert.False(d.Check(T0.AddSeconds(260), -10, 1, false)); // 退出饱和 → 清零
        Assert.Equal(0, d.SaturatedSeconds);
        // 重新饱和后窗口重新起算，短时间内不触发
        Assert.False(d.Check(T0.AddSeconds(261), -10, 1, true));
        Assert.False(d.Check(T0.AddSeconds(300), -10, 2, true));
    }

    [Fact]
    public void HoldingNearSetpoint_WhileSaturated_DoesNotTrip_ButReportsSaturation()
    {
        // 制冷力不足顶满输出但温度稳住不再恶化 —— 不算方向接反，
        // 但饱和时长应被记录，供上层发"能力不足"警告
        var d = Started();
        for (var s = 0; s <= 900; s++)
            Assert.False(d.Check(T0.AddSeconds(s), -10, -6.0, outputSaturated: true));
        Assert.True(d.SaturatedSeconds > 300, "应累计饱和时长以便上层提示能力不足");
    }
}
