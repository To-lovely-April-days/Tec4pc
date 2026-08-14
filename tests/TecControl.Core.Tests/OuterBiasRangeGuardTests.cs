using TecControl.Core.Control;

namespace TecControl.Core.Tests;

public class OuterBiasRangeGuardTests
{
    private static OuterBiasRangeGuard Guard() =>
        new() { PinnedSecondsToWiden = 300, StepC = 5, CeilingC = 25 };

    /// <summary>
    /// 喂 n 次"偏置顶在限幅上"的更新，返回第一次放宽的结果（没放宽则为 null）。
    /// 误差默认给常数 0.5℃——停滞不动，即"真卡死"场景。
    /// </summary>
    private static double? Feed(OuterBiasRangeGuard g, int updates, double limit,
        bool innerSaturated = false, double bias = double.NaN, double error = 0.5)
    {
        for (var i = 0; i < updates; i++)
            if (g.Update(2.0, double.IsNaN(bias) ? -limit : bias, limit, innerSaturated, error)
                is { } widened)
                return widened;
        return null;
    }

    [Fact]
    public void DoesNotWidenBeforeThreshold()
    {
        var g = Guard();
        Assert.Null(Feed(g, 149, 8));      // 149×2s = 298s < 300s
        Assert.Equal(298, g.PinnedSeconds, 6);
    }

    [Fact]
    public void WidensByOneStepAfterThreshold()
    {
        var g = Guard();
        Assert.Equal(13, Feed(g, 151, 8)!.Value, 6);
        Assert.Equal(0, g.PinnedSeconds, 6);   // 放宽后重新计时
    }

    [Fact]
    public void DoesNotWidenWhenInnerLoopIsSaturated()
    {
        // 内环已满幅 → 瓶颈是制冷能力而非限幅，放宽只会把腔体推得更极端
        var g = Guard();
        Assert.Null(Feed(g, 500, 8, innerSaturated: true));
        Assert.Equal(0, g.PinnedSeconds, 6);
    }

    [Fact]
    public void ResetsWhenBiasComesOffTheLimit()
    {
        var g = Guard();
        Feed(g, 100, 8);
        Assert.Null(g.Update(2.0, -3.0, 8, false, 0.5));   // 偏置退回限幅内
        Assert.Equal(0, g.PinnedSeconds, 6);
    }

    [Fact]
    public void DoesNotWidenWhileErrorIsStillShrinking()
    {
        // 长距离降温：偏置一直顶在限幅上，但误差持续缩小——限幅在正常限速，不许放宽。
        // 每 2s 缩小 0.02℃（0.6℃/min，比现场实测最慢的趋近段还慢一半以上）。
        var g = Guard();
        var error = 26.0;
        for (var t = 0.0; t < 1800; t += 2)   // 顶限幅 30 分钟，远超 300s 阈值
        {
            Assert.Null(g.Update(2.0, -8, 8, false, error));
            error = Math.Max(0.0, error - 0.02);
        }
        Assert.True(g.PinnedSeconds < 300, $"计时不应走满，实际 {g.PinnedSeconds:F0}s");
    }

    [Fact]
    public void WidensOnceProgressStalls()
    {
        // 先正常推进（不计时），误差一旦停滞，从停滞处起 300s 走满才放宽
        var g = Guard();
        var error = 3.0;
        for (var t = 0.0; t < 300; t += 2)   // 推进 210s + 停滞 90s，均不足以触发
        {
            Assert.Null(g.Update(2.0, -8, 8, false, error));
            error = Math.Max(0.9, error - 0.02);   // 210s 后停在 0.9℃
        }
        Assert.Equal(13, Feed(g, 151, 8, error: 0.9)!.Value, 6);
    }

    [Fact]
    public void StopsAtCeiling()
    {
        var g = Guard();
        Assert.Equal(25, Feed(g, 151, 22)!.Value, 6);   // 22+5 截到 25
        Assert.Null(Feed(g, 151, 25));                  // 已在天花板，不再放宽
    }

    [Fact]
    public void PositiveBiasCountsToo()
    {
        // 加热工况偏置为正，同样需要扩程
        var g = Guard();
        Assert.Equal(13, Feed(g, 151, 8, bias: 8)!.Value, 6);
    }
}
