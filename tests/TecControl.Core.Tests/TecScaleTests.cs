using TecControl.Core.Protocol;

namespace TecControl.Core.Tests;

public class TecScaleTests
{
    [Theory]
    [InlineData(2500000, 25.0)]
    [InlineData(2518788, 25.18788)]
    [InlineData(-40000000, -400.0)]
    public void TempFromRaw_ScalesBy1e5(long raw, double expected) =>
        Assert.Equal(expected, TecScale.TempFromRaw(raw), 5);

    [Fact]
    public void TempFromRaw_SensorNotConnected_ReturnsNaN() =>
        Assert.True(double.IsNaN(TecScale.TempFromRaw(999_999_999)));

    [Theory]
    [InlineData(25.0, 2500000)]
    [InlineData(25.18788, 2518788)]
    [InlineData(-0.5, -50000)]
    public void TempToRaw_RoundTrips(double celsius, long expected) =>
        Assert.Equal(expected, TecScale.TempToRaw(celsius));

    [Fact]
    public void OhmsFromRaw_DocExample()
    {
        // 协议 3.6.2：RESISTOR=9916909257 → 9916.909257Ω
        Assert.Equal(9916.909257, TecScale.OhmsFromRaw(9916909257), 6);
    }

    [Fact]
    public void VoltsFromRaw_DocExample()
    {
        // 协议 3.6.2：OUTV=1000000000 → 10V
        Assert.Equal(10.0, TecScale.VoltsFromRaw(1000000000), 6);
    }

    [Fact]
    public void DutyPercentFromRaw_DocExample()
    {
        // 协议 3.3.6：200000 → 10%
        Assert.Equal(10.0, TecScale.DutyPercentFromRaw(200000), 6);
    }

    [Fact]
    public void SpeedRoundTrip()
    {
        Assert.Equal(1.0, TecScale.SpeedFromRaw(1000), 6);
        Assert.Equal(1000, TecScale.SpeedToRaw(1.0));
    }

    [Fact]
    public void CurrentScales()
    {
        // 协议 3.2.21：3256 → 3.256A；3.2.22：10 → 1.0A
        Assert.Equal(3.256, TecScale.CurrentFromRaw(3256), 6);
        Assert.Equal(1.0, TecScale.MaxCurrentFromRaw(10), 6);
        Assert.Equal(15, TecScale.MaxCurrentToRaw(1.5));
    }
}
