using TecControl.Core.Control;

namespace TecControl.Core.Tests;

public class StabilityMonitorTests
{
    private static readonly DateTime T0 = new(2026, 1, 1);

    [Fact]
    public void TooFewSamples_ReturnsNull()
    {
        var m = new StabilityMonitor(300);
        Assert.Null(m.Compute(-10));
        m.Add(T0, -10);
        Assert.Null(m.Compute(-10));
    }

    [Fact]
    public void ComputesPeakToPeakAndStdDev()
    {
        var m = new StabilityMonitor(300);
        double[] temps = [-10.05, -9.95, -10.00, -10.02, -9.98];
        for (var i = 0; i < temps.Length; i++)
            m.Add(T0.AddSeconds(i), temps[i]);

        var s = m.Compute(-10)!;
        Assert.Equal(5, s.SampleCount);
        Assert.Equal(0.10, s.PeakToPeakC, 6);
        Assert.Equal(0.05, s.PlusMinusC, 6);
        Assert.Equal(-10.0, s.MeanC, 6);
        Assert.Equal(0.05, s.MaxDeviationC, 6);
    }

    [Fact]
    public void DropsSamplesOutsideWindow()
    {
        var m = new StabilityMonitor(windowSeconds: 60);
        // 前 60 秒有大波动，之后平稳；窗口滚动后旧数据应被丢弃
        for (var i = 0; i < 60; i++) m.Add(T0.AddSeconds(i), i % 2 == 0 ? -9 : -11);
        for (var i = 60; i < 180; i++) m.Add(T0.AddSeconds(i), -10.00 + (i % 2) * 0.02);

        var s = m.Compute(-10)!;
        Assert.True(s.PeakToPeakC < 0.05, $"旧波动应已滚出窗口，实际峰峰 {s.PeakToPeakC:F4}");
        Assert.True(s.WindowSeconds <= 61);
    }

    [Fact]
    public void IgnoresNaN_SensorDropout()
    {
        var m = new StabilityMonitor(300);
        m.Add(T0, -10.0);
        m.Add(T0.AddSeconds(1), double.NaN);
        m.Add(T0.AddSeconds(2), -10.02);
        Assert.Equal(2, m.Compute(-10)!.SampleCount);
    }

    [Fact]
    public void IsWindowFull_OnlyAfterEnoughSpan()
    {
        var m = new StabilityMonitor(windowSeconds: 100);
        for (var i = 0; i < 50; i++) m.Add(T0.AddSeconds(i), -10);
        Assert.False(m.IsWindowFull);
        for (var i = 50; i < 120; i++) m.Add(T0.AddSeconds(i), -10);
        Assert.True(m.IsWindowFull);
    }

    [Fact]
    public void RealMeasurementReproduction()
    {
        // 复现实测 -10℃ 稳态段：峰峰 0.121℃、标准差 0.029℃
        var m = new StabilityMonitor(600);
        var rnd = new Random(42);
        for (var i = 0; i < 700; i++)
            m.Add(T0.AddSeconds(i * 0.5), -10.0 + (rnd.NextDouble() - 0.5) * 0.12);

        var s = m.Compute(-10)!;
        Assert.InRange(s.PeakToPeakC, 0.09, 0.13);
        Assert.InRange(s.StdDevC, 0.02, 0.04);
    }
}
