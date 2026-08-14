using TecControl.Core.Control;

namespace TecControl.Core.Tests;

public class SlidingAverageTests
{
    private static readonly DateTime T0 = new(2026, 1, 1);

    [Fact]
    public void Averages_WithinWindow()
    {
        var avg = new SlidingAverage(10);
        avg.Add(T0, -30.010);
        avg.Add(T0.AddSeconds(1), -29.990);
        Assert.Equal(-30.000, avg.Value, 6);
    }

    [Fact]
    public void EvictsSamples_OlderThanWindow()
    {
        var avg = new SlidingAverage(10);
        avg.Add(T0, -40.0);                        // 将被逐出
        for (var s = 5; s <= 14; s++) avg.Add(T0.AddSeconds(s), -30.0);
        Assert.Equal(-30.0, avg.Value, 6);         // 旧样本不再影响均值
    }

    [Fact]
    public void IgnoresNaN_AndReportsWindowFull()
    {
        var avg = new SlidingAverage(10);
        Assert.True(double.IsNaN(avg.Value));
        avg.Add(T0, double.NaN);
        Assert.True(double.IsNaN(avg.Value));      // NaN 不入窗

        avg.Add(T0, -30.0);
        Assert.False(avg.IsWindowFull);            // 只有 1 个样本
        avg.Add(T0.AddSeconds(9), -30.0);
        Assert.True(avg.IsWindowFull);             // 跨度 9s ≥ 10×0.8

        avg.Reset();
        Assert.True(double.IsNaN(avg.Value));
    }

    [Fact]
    public void NoiseSuppression_MatchesSqrtN()
    {
        // 平均 N 个独立噪声样本，σ 应按 1/√N 缩小——这是"呈现真实温度"的机理
        var rnd = new Random(7);
        var avg = new SlidingAverage(10);
        var means = new List<double>();
        var t = T0;
        for (var i = 0; i < 2000; i++)
        {
            t = t.AddSeconds(0.5);
            var m = avg.Add(t, rnd.NextGaussianApprox() * 0.008);   // σ=8mK 的读数噪声
            if (i > 40) means.Add(m);
        }
        var sigma = Math.Sqrt(means.Sum(x => x * x) / means.Count);
        // 10s 窗 @0.5s ≈ 20 样本 → 理论 σ≈8/√20≈1.8mK；判宽放到 3mK
        Assert.True(sigma < 0.003, $"均值噪声应显著缩小，实际 σ={sigma * 1000:F2} mK");
    }
}

file static class RandomExt
{
    /// <summary>Box-Muller 高斯近似。</summary>
    public static double NextGaussianApprox(this Random r) =>
        Math.Sqrt(-2.0 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble());
}
