using TecControl.Core.Control;

namespace TecControl.Core.Tests;

public class SteadyStateFeedforwardTests
{
    [Fact]
    public void NoData_ReturnsNull() =>
        Assert.Null(new SteadyStateFeedforward().Predict(-20));

    [Fact]
    public void SinglePoint_ReusedNearby_NotFarAway()
    {
        var ff = new SteadyStateFeedforward();
        ff.Learn(-10, -34.2);
        Assert.Equal(-34.2, ff.Predict(-12)!.Value, 3);   // 附近沿用
        Assert.Null(ff.Predict(-40));                      // 太远不敢外推
    }

    [Fact]
    public void TwoPoints_InterpolateAndExtrapolateLinearly()
    {
        // 物理关系：u_ss ≈ (T_env − T_set)/K，实测 29℃环境、增益 1.14℃/%
        var ff = new SteadyStateFeedforward();
        ff.Learn(-10, -34.2);
        ff.Learn(-20, -43.0);

        // 内插
        Assert.Equal(-38.6, ff.Predict(-15)!.Value, 1);
        // 外推到未跑过的 -30℃：应接近 -51.8%
        Assert.Equal(-51.8, ff.Predict(-30)!.Value, 1);
    }

    [Fact]
    public void ExtrapolationIsBounded()
    {
        var ff = new SteadyStateFeedforward { ExtrapolationLimitC = 10 };
        ff.Learn(-10, -34.0);
        ff.Learn(-20, -43.0);
        // -60℃ 远超已知区间，应按边界（-30℃）截断，而不是线性外推到离谱值
        var far = ff.Predict(-60)!.Value;
        var atBoundary = ff.Predict(-30)!.Value;
        Assert.Equal(atBoundary, far, 3);
    }

    [Fact]
    public void RelearnSamePoint_Overwrites()
    {
        var ff = new SteadyStateFeedforward();
        ff.Learn(-10, -34.0);
        ff.Learn(-10.5, -36.0);           // 落在合并带内 → 覆盖
        Assert.Single(ff.Points);
        Assert.Equal(-36.0, ff.Points[0].OutputPercent, 3);
    }

    [Fact]
    public void FullRangeLearning_PredictsUnvisitedSetpoints()
    {
        // 模拟按实测物理规律（环境 29℃、增益 1.14℃/%）在几个点学习后，
        // 对从未跑过的温度给出合理预测——即用户无需为每个温度手工建表
        var ff = new SteadyStateFeedforward { ExtrapolationLimitC = 25 };
        foreach (var sp in new[] { 0.0, -10.0, -20.0 })
            ff.Learn(sp, -(29.0 - sp) / 1.14);

        foreach (var sp in new[] { -5.0, -15.0, -25.0, -35.0 })
        {
            var expected = -(29.0 - sp) / 1.14;
            Assert.Equal(expected, ff.Predict(sp)!.Value, 1);
        }
    }

    [Fact]
    public void SaveLoad_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ff-{Guid.NewGuid():N}.csv");
        try
        {
            var a = new SteadyStateFeedforward();
            a.Learn(-10, -34.2);
            a.Learn(-25, -47.5);
            FeedforwardStore.Save(a, path);

            var b = new SteadyStateFeedforward();
            FeedforwardStore.Load(b, path);

            Assert.Equal(2, b.Points.Count);
            Assert.Equal(a.Predict(-18)!.Value, b.Predict(-18)!.Value, 3);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ---------- 执行器分段（≥100℃ 加热棒的稳态点不得混进 TEC 段的拟合） ----------

    [Fact]
    public void Prediction_DoesNotCrossActuatorSegment()
    {
        // TEC 段两个点斜率约 -0.9%/℃；加热棒段一个 +40% 的点。
        // 若不分段，110℃ 的点会把拟合线整个拉翻，两段预测都失真。
        var ff = new SteadyStateFeedforward();
        ff.Learn(-10, -34.2);
        ff.Learn(-25, -47.5);
        ff.Learn(110, 40);

        // TEC 段：预测只由 TEC 段两点线性外推（0℃ ≈ -25.3）
        Assert.Equal(-25.3, ff.Predict(0)!.Value, 1);

        // 加热棒段：只有一个点 → 附近沿用；远离（超出外推限）返回 null
        Assert.Equal(40, ff.Predict(115)!.Value, 6);
        Assert.Null(ff.Predict(180));

        // 关闭分段（NaN）后恢复全域拟合（行为兼容开关）：110℃ 的点把线拉向正侧
        ff.SegmentBoundaryC = double.NaN;
        Assert.True(ff.Predict(0)!.Value < -28,
            $"全域拟合应明显偏离分段值，实际 {ff.Predict(0)!.Value:F1}");
    }

    [Fact]
    public void Learn_DoesNotMergeAcrossSegment()
    {
        // 99.5℃（TEC）与 100.3℃（加热棒）温差 0.8℃ < 合并带 1℃，但分属两套执行器
        var ff = new SteadyStateFeedforward();
        ff.Learn(99.5, 60);
        ff.Learn(100.3, 25);
        Assert.Equal(2, ff.Points.Count);
    }
}
