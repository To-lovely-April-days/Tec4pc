using TecControl.Core.Control;

namespace TecControl.Core.Tests;

public class TemperatureProfileTests
{
    [Fact]
    public void Linear_RampsAtRequestedRate()
    {
        // 25℃ 起，以 0.5℃/min 降到 5℃ → 需 40 分钟
        var p = SegmentProfile.Linear(targetC: 5, rateCPerMin: 0.5);
        Assert.Equal(40, p.TotalMinutes(25), 3);
        Assert.Equal(25, p.SetpointAt(0, 25)!.Value, 3);
        Assert.Equal(20, p.SetpointAt(10, 25)!.Value, 3);
        Assert.Equal(15, p.SetpointAt(20, 25)!.Value, 3);
        Assert.Equal(5, p.SetpointAt(40, 25)!.Value, 3);
        Assert.Null(p.SetpointAt(41, 25));      // 结束
    }

    [Fact]
    public void Linear_WithHold_StaysAtTargetThenEnds()
    {
        var p = SegmentProfile.Linear(5, 1.0, holdMinutes: 30); // 20min 降温 + 30min 保温
        Assert.Equal(50, p.TotalMinutes(25), 3);
        Assert.Equal(5, p.SetpointAt(25, 25)!.Value, 3);
        Assert.Equal(5, p.SetpointAt(49, 25)!.Value, 3);
        Assert.Null(p.SetpointAt(51, 25));
    }

    [Fact]
    public void Stepwise_MultipleSegmentsWithDifferentRates()
    {
        // 阶梯降温：40→20 快速(2℃/min，10min)，保温 15min，再 20→0 慢速(0.2℃/min，100min)
        var p = new SegmentProfile([
            new ProfileSegment(20, 2.0, 15),
            new ProfileSegment(0, 0.2),
        ]);
        Assert.Equal(10 + 15 + 100, p.TotalMinutes(40), 3);
        Assert.Equal(30, p.SetpointAt(5, 40)!.Value, 3);      // 第一段中点
        Assert.Equal(20, p.SetpointAt(20, 40)!.Value, 3);     // 保温段
        Assert.Equal(10, p.SetpointAt(75, 40)!.Value, 3);     // 第二段中点
        Assert.Equal(0, p.SetpointAt(125, 40)!.Value, 3);
        Assert.Null(p.SetpointAt(126, 40));
    }

    [Fact]
    public void ZeroRate_MeansImmediateStep()
    {
        var p = new SegmentProfile([new ProfileSegment(-10, 0, 5)]);
        Assert.Equal(5, p.TotalMinutes(25), 3);
        Assert.Equal(-10, p.SetpointAt(0.001, 25)!.Value, 3);
    }

    [Theory]
    [InlineData(0.04)]   // 低于 0.05
    [InlineData(5.5)]    // 高于 5
    [InlineData(-1)]
    public void RateOutOfRange_Rejected(double rate) =>
        Assert.ThrowsAny<ArgumentException>(() => SegmentProfile.Linear(0, rate));

    [Fact]
    public void Table_InterpolatesBetweenPoints()
    {
        var p = new TableProfile([(0, 25.0), (10, 15.0), (30, 15.0), (60, -5.0)]);
        Assert.Equal(25, p.SetpointAt(0, 25)!.Value, 3);
        Assert.Equal(20, p.SetpointAt(5, 25)!.Value, 3);   // 线性插值
        Assert.Equal(15, p.SetpointAt(20, 25)!.Value, 3);  // 平台
        Assert.Equal(5, p.SetpointAt(45, 25)!.Value, 3);
        Assert.Null(p.SetpointAt(61, 25));
    }

    [Fact]
    public void Table_FromCsv_SkipsHeaderAndBlanks()
    {
        var csv = "分钟,温度\n0,25\n\n30,-10\n";
        var p = TableProfile.FromCsv(csv);
        Assert.Equal(2, p.Points.Count);
        Assert.Equal(7.5, p.SetpointAt(15, 25)!.Value, 3);
    }

    [Fact]
    public void CrystallizationRange_SupportsSlowAndFastRates()
    {
        // 工艺要求 0.05~5℃/min 全范围可用
        var slow = SegmentProfile.Linear(-10, 0.05);   // 极慢结晶
        var fast = SegmentProfile.Linear(-10, 5.0);    // 快速冷却
        Assert.Equal(700, slow.TotalMinutes(25), 1);
        Assert.Equal(7, fast.TotalMinutes(25), 1);
    }
}
