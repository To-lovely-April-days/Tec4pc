using TecControl.Core.Control;

namespace TecControl.Core.Tests;

public class PidGainScheduleTests
{
    private static GainPoint P(double t, double kp, double ki = 0.01, double kd = 100) =>
        new(t, new PidGains(kp, ki, kd));

    [Fact]
    public void Empty_ReturnsNull_SoManualGainsRemain()
    {
        var s = new PidGainSchedule();
        Assert.Null(s.GainsAt(-10));
    }

    [Fact]
    public void SinglePoint_UsedEverywhere()
    {
        var s = new PidGainSchedule();
        s.Learn(P(-10, 12.9));
        Assert.Equal(12.9, s.GainsAt(-40)!.Kp, 6);
        Assert.Equal(12.9, s.GainsAt(25)!.Kp, 6);
    }

    [Fact]
    public void InterpolatesBetweenPoints()
    {
        // 0℃ 时 Kp=10，-40℃ 时 Kp=30（低温增益需更大：过程增益下降）
        var s = new PidGainSchedule();
        s.Learn(P(0, 10));
        s.Learn(P(-40, 30));

        Assert.Equal(20, s.GainsAt(-20)!.Kp, 6);   // 中点
        Assert.Equal(15, s.GainsAt(-10)!.Kp, 6);
        Assert.Equal(25, s.GainsAt(-30)!.Kp, 6);
    }

    [Fact]
    public void OutsideRange_ClampsToNearestPoint_NoExtrapolation()
    {
        var s = new PidGainSchedule();
        s.Learn(P(0, 10));
        s.Learn(P(-40, 30));

        Assert.Equal(30, s.GainsAt(-60)!.Kp, 6);   // 比最低点更冷 → 取最低点
        Assert.Equal(10, s.GainsAt(30)!.Kp, 6);    // 比最高点更热 → 取最高点
    }

    [Fact]
    public void ThreePoints_PiecewiseLinear()
    {
        var s = new PidGainSchedule();
        s.Learn(P(0, 10));
        s.Learn(P(-20, 16));
        s.Learn(P(-40, 30));

        Assert.Equal(13, s.GainsAt(-10)!.Kp, 6);   // 第一段中点
        Assert.Equal(23, s.GainsAt(-30)!.Kp, 6);   // 第二段中点
    }

    [Fact]
    public void NearbyTemperature_OverwritesInsteadOfDuplicating()
    {
        var s = new PidGainSchedule { MergeBandC = 2.0 };
        s.Learn(P(-10, 12));
        s.Learn(P(-11, 15));       // 相差 1℃ < 合并带 → 覆盖
        Assert.Equal(1, s.Count);
        Assert.Equal(15, s.GainsAt(-10)!.Kp, 6);

        s.Learn(P(-20, 20));       // 相差较远 → 新增
        Assert.Equal(2, s.Count);
    }

    [Fact]
    public void AllGainsInterpolated_NotJustKp()
    {
        var s = new PidGainSchedule();
        s.Learn(new GainPoint(0, new PidGains(10, 0.02, 100)));
        s.Learn(new GainPoint(-40, new PidGains(30, 0.06, 500)));

        var g = s.GainsAt(-20)!;
        Assert.Equal(20, g.Kp, 6);
        Assert.Equal(0.04, g.Ki, 6);
        Assert.Equal(300, g.Kd, 6);
    }

    [Fact]
    public void SaveLoad_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gains-{Guid.NewGuid():N}.csv");
        try
        {
            var a = new PidGainSchedule();
            a.Learn(new GainPoint(-10, new PidGains(12.893, 0.0314, 382.444), 28.651, 186.9));
            a.Learn(new GainPoint(-30, new PidGains(20.5, 0.05, 500)));
            GainScheduleStore.Save(a, path);

            var b = new PidGainSchedule();
            GainScheduleStore.Load(b, path);

            Assert.Equal(2, b.Count);
            Assert.Equal(12.893, b.GainsAt(-10)!.Kp, 3);
            Assert.Equal(0.0314, b.GainsAt(-10)!.Ki, 5);
            Assert.Equal(16.6965, b.GainsAt(-20)!.Kp, 3);   // 插值
        }
        finally { File.Delete(path); }
    }

    // ---------- 外环参数调度 ----------

    private static GainPoint PO(double t, double outerKp, double outerKi, double bias, double? steadyBias = null) =>
        new(t, new PidGains(10, 0.01, 100), 0, 0, new PidGains(outerKp, outerKi, 0), bias, steadyBias);

    [Fact]
    public void OuterAt_NullWhenNoPointRegistersOuterGains()
    {
        var s = new PidGainSchedule();
        s.Learn(P(-10, 12));
        Assert.Null(s.OuterAt(-10));       // 只有内环参数 → 调用方沿用手动外环值
    }

    [Fact]
    public void OuterAt_InterpolatesGainsAndBiasLimit()
    {
        var s = new PidGainSchedule();
        s.Learn(PO(0, 2.0, 0.010, 12));
        s.Learn(PO(-40, 1.0, 0.004, 8));

        var o = s.OuterAt(-20)!;
        Assert.Equal(1.5, o.Gains.Kp, 6);
        Assert.Equal(0.007, o.Gains.Ki, 6);
        Assert.Equal(10, o.MaxBiasC, 6);
    }

    [Fact]
    public void OuterAt_IgnoresPointsWithoutOuterGains()
    {
        // 中间那个点只整定了内环：外环插值应跳过它，在两个有外环参数的点之间直接连线
        var s = new PidGainSchedule();
        s.Learn(PO(0, 2.0, 0.010, 12));
        s.Learn(P(-20, 16));
        s.Learn(PO(-40, 1.0, 0.004, 8));

        Assert.Equal(1.5, s.OuterAt(-20)!.Gains.Kp, 6);
        Assert.Equal(16, s.GainsAt(-20)!.Kp, 6);   // 内环仍取自己那一点
    }

    [Fact]
    public void SteadyBias_LearnedOnlyOnExistingPoint()
    {
        var s = new PidGainSchedule();
        s.Learn(P(-10, 12));

        Assert.True(s.LearnSteadyBias(-10.5, -5.2));   // 落在合并带内
        Assert.Equal(-5.2, s.SteadyBiasAt(-10)!.Value, 6);

        Assert.False(s.LearnSteadyBias(80, -3));       // 表里没有该温度 → 不凭空建点
        Assert.Equal(1, s.Count);
    }

    [Fact]
    public void SteadyBias_Interpolates()
    {
        var s = new PidGainSchedule();
        s.Learn(PO(0, 1.5, 0.008, 10, steadyBias: -2.0));
        s.Learn(PO(-40, 1.5, 0.004, 10, steadyBias: -8.0));

        Assert.Equal(-5.0, s.SteadyBiasAt(-20)!.Value, 6);
    }

    [Fact]
    public void Relearn_KeepsOuterGainsAndSteadyBias()
    {
        // 在同一温度重新自整定内环，不应抹掉现场调好的外环参数与已学到的稳态偏置
        var s = new PidGainSchedule();
        s.Learn(PO(-10, 1.8, 0.009, 12, steadyBias: -5.2));
        s.Learn(new GainPoint(-10, new PidGains(14, 0.04, 400), 30, 190));

        Assert.Equal(14, s.GainsAt(-10)!.Kp, 6);
        Assert.Equal(1.8, s.OuterAt(-10)!.Gains.Kp, 6);
        Assert.Equal(12, s.OuterAt(-10)!.MaxBiasC, 6);
        Assert.Equal(-5.2, s.SteadyBiasAt(-10)!.Value, 6);
    }

    [Fact]
    public void SuggestOuter_ScalesIntegralWithSystemSpeed()
    {
        // Tu 越长（低温段系统越慢）→ 积分时间越长 → Ki 越小
        var fast = PidGainSchedule.SuggestOuter(187);
        var slow = PidGainSchedule.SuggestOuter(374);

        Assert.Equal(2.5 / (1.7 * 187), fast.Gains.Ki, 6);
        Assert.Equal(2.5 / (1.7 * 374), slow.Gains.Ki, 6);
        Assert.True(slow.Gains.Ki < fast.Gains.Ki);
        Assert.Equal(0, fast.Gains.Kd, 6);
        Assert.Equal(8, fast.MaxBiasC, 6);

        // Tu 异常（手工点/整定失败）时退回保守的 600s 积分时间
        Assert.Equal(2.5 / 600, PidGainSchedule.SuggestOuter(0).Gains.Ki, 6);
    }

    [Fact]
    public void SaveLoad_RoundTripsOuterColumns()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gains-outer-{Guid.NewGuid():N}.csv");
        try
        {
            var a = new PidGainSchedule();
            a.Learn(PO(-10, 1.5, 0.008, 10, steadyBias: -5.23));
            a.Learn(P(-30, 20.5));            // 无外环参数的点
            GainScheduleStore.Save(a, path);

            var b = new PidGainSchedule();
            GainScheduleStore.Load(b, path);

            Assert.Equal(2, b.Count);
            Assert.Equal(0.008, b.OuterAt(-10)!.Gains.Ki, 5);
            Assert.Equal(10, b.OuterAt(-10)!.MaxBiasC, 3);
            Assert.Equal(-5.23, b.SteadyBiasAt(-10)!.Value, 3);
            Assert.Null(b.Points.Single(p => p.TemperatureC == -30).OuterGains);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void EnsureOuterGains_FillsOnlyMissingRows_FromEachPointsOwnTu()
    {
        var s = new PidGainSchedule();
        s.Learn(new GainPoint(-10, new PidGains(12.893, 0.0314, 382.444), 28.651, 186.9));  // 缺外环
        s.Learn(new GainPoint(-30, new PidGains(20, 0.05, 500), 30, 400));                  // 缺外环，系统更慢
        s.Learn(PO(0, 1.8, 0.009, 12));                                                     // 已有外环，应原样保留

        Assert.Equal(2, s.EnsureOuterGains());
        Assert.Equal(0, s.EnsureOuterGains());   // 幂等：再调用没有可补的

        // 补出来的 Ki 按各点自己的 Tu 算，慢的那点更小
        Assert.Equal(2.5 / (1.7 * 186.9), s.OuterAt(-10)!.Gains.Ki, 6);
        Assert.Equal(2.5 / (1.7 * 400), s.OuterAt(-30)!.Gains.Ki, 6);
        Assert.Equal(8, s.OuterAt(-10)!.MaxBiasC, 6);

        Assert.Equal(1.8, s.OuterAt(0)!.Gains.Kp, 6);   // 原有的没被覆盖
        Assert.Equal(12, s.OuterAt(0)!.MaxBiasC, 6);
    }

    [Fact]
    public void EnsureOuterGains_WidensLimitWhenSteadyBiasAlreadyKnown()
    {
        // 已经学到该温度需要 −9.4℃ 偏置：补外环参数时限幅不能还给默认的 8℃
        var s = new PidGainSchedule();
        s.Learn(new GainPoint(-40, new PidGains(20, 0.05, 500), 30, 400, SteadyBiasC: -9.4));

        Assert.Equal(1, s.EnsureOuterGains());
        Assert.Equal(13.4, s.OuterAt(-40)!.MaxBiasC, 6);
    }

    [Fact]
    public void LearnSteadyBias_ReportsChangeOnlyWhenItMatters()
    {
        var s = new PidGainSchedule();
        s.Learn(PO(-10, 1.5, 0.008, 20));

        Assert.True(s.LearnSteadyBias(-10, -5.20));    // 首次
        Assert.False(s.LearnSteadyBias(-10, -5.22));   // 抖动，不算变更（不触发落盘）
        Assert.True(s.LearnSteadyBias(-10, -5.40));    // 超过阈值
    }

    [Fact]
    public void LoadOrSeed_PrefersUserFile_FallsBackToSeed()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"seed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var userPath = Path.Combine(dir, "user.csv");
        try
        {
            // 用户表不存在 → 返回 null（本测试环境没有出厂表）
            var s1 = new PidGainSchedule();
            Assert.Null(GainScheduleStore.LoadOrSeed(s1, userPath));
            Assert.Equal(0, s1.Count);

            // 用户表存在 → 用用户表
            var a = new PidGainSchedule();
            a.Learn(PO(-10, 2.5, 0.008, 8));
            GainScheduleStore.Save(a, userPath);

            var s2 = new PidGainSchedule();
            Assert.Equal(userPath, GainScheduleStore.LoadOrSeed(s2, userPath));
            Assert.Equal(1, s2.Count);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void LoadOrSeed_EmptyUserFile_FallsBackToSeed()
    {
        // 现场回归：点"清空"会立即把空表写盘。旧逻辑此后永远优先这张空用户表，
        // 出厂工作点再也够不着（界面表现为"表格加载不出配置的 CSV"）。
        // 用户表存在但没有数据行时必须退回出厂表。
        var dir = Path.Combine(Path.GetTempPath(), $"seed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var userPath = Path.Combine(dir, "user.csv");
        var seedPath = Path.Combine(dir, "seed.csv");
        try
        {
            GainScheduleStore.Save(new PidGainSchedule(), userPath);   // 清空后落盘：只有表头
            var seed = new PidGainSchedule();
            seed.Learn(PO(-10, 2.5, 0.008, 8));
            GainScheduleStore.Save(seed, seedPath);

            var s = new PidGainSchedule();
            Assert.Equal(seedPath, GainScheduleStore.LoadOrSeed(s, userPath, seedPath));
            Assert.Equal(1, s.Count);

            // 出厂表也没有时返回 null，表保持为空
            var s2 = new PidGainSchedule();
            Assert.Null(GainScheduleStore.LoadOrSeed(s2, userPath, Path.Combine(dir, "none.csv")));
            Assert.Equal(0, s2.Count);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ShippedSeedFile_IsReadableAndCarriesOuterGains()
    {
        // 仓库里的出厂表必须能被读出来，否则首次运行等于没有初值
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var seed = Path.Combine(repoRoot, "config", "gain-schedule.csv");
        Assert.True(File.Exists(seed), $"出厂增益表缺失：{seed}");

        var s = new PidGainSchedule();
        GainScheduleStore.Load(s, seed);

        Assert.Equal(1, s.Count);
        Assert.Equal(12.893, s.GainsAt(-10)!.Kp, 3);
        Assert.Equal(2.5, s.OuterAt(-10)!.Gains.Kp, 3);
        Assert.Equal(8, s.OuterAt(-10)!.MaxBiasC, 3);
        Assert.Equal(0, s.EnsureOuterGains());   // 出厂表本身已完整，不该被再补
    }

    // ---------- 执行器分段（≥100℃ 加热换 PWM 电加热器，制冷仍是 TEC） ----------

    [Fact]
    public void NoInterpolationAcrossActuatorSegmentBoundary()
    {
        // 90℃（TEC 加热）与 110℃（电加热）各登记一点：99℃ 必须取 90℃ 点的整套参数，
        // 决不能和 110℃ 点混着插值——那是另一套执行器的参数
        var s = new PidGainSchedule();
        s.Learn(P(90, 10));
        s.Learn(P(110, 40));

        Assert.Equal(10, s.GainsAt(99)!.Kp, 6);
        Assert.Equal(40, s.GainsAt(100)!.Kp, 6);   // 边界属于高温段
        Assert.Equal(40, s.GainsAt(105)!.Kp, 6);

        // 关闭分段（NaN）后恢复全域插值
        s.SegmentBoundaryC = double.NaN;
        Assert.Equal(25, s.GainsAt(100)!.Kp, 6);
    }

    [Fact]
    public void SegmentWithoutPoints_FallsBackToManual_NotNearestOtherSegment()
    {
        // 只在低温段整定过：120℃ 不许拿 −10℃ 的参数就近顶替（执行器都换了），
        // 返回 null 让调用方回退手动参数，并提示先在高温段自整定
        var s = new PidGainSchedule();
        s.Learn(PO(-10, 2.5, 0.008, 8, steadyBias: -0.35));

        Assert.Null(s.GainsAt(120));
        Assert.Null(s.OuterAt(120));
        Assert.Null(s.SteadyBiasAt(120));

        Assert.Equal(10, s.GainsAt(-40)!.Kp, 6);   // 低温段行为不变（就近取点仍有效）
    }

    [Fact]
    public void MergeBand_DoesNotSpanBoundary()
    {
        // 99.5℃ 与 100.5℃ 温差 1℃ < 合并带 2℃，但分属两套执行器 → 必须是两个点
        var s = new PidGainSchedule { MergeBandC = 2.0 };
        s.Learn(P(99.5, 10));
        s.Learn(P(100.5, 40));
        Assert.Equal(2, s.Count);

        s.Remove(99.8);                            // 删除也不跨段：只删掉低温段那个
        Assert.Equal(1, s.Count);
        Assert.Equal(40, s.GainsAt(100.5)!.Kp, 6);
    }

    // ---------- 加热比调度（执行器不对称归一化随温度变化） ----------

    [Fact]
    public void HeatRatioAt_InterpolatesWithinSegment_NullOtherwise()
    {
        var s = new PidGainSchedule();
        Assert.Null(s.HeatRatioAt(-10));           // 空表 → 沿用全局值

        s.Learn(new GainPoint(0, new PidGains(10, 0.01, 100), HeatRatio: 1.0));
        s.Learn(new GainPoint(-20, new PidGains(16, 0.02, 200), HeatRatio: 2.0));

        Assert.Equal(1.5, s.HeatRatioAt(-10)!.Value, 6);
        Assert.Equal(2.0, s.HeatRatioAt(-40)!.Value, 6);   // 段内就近取点
        Assert.Null(s.HeatRatioAt(120));                    // 高温段没登记 → 不跨段顶替
    }

    [Fact]
    public void Relearn_KeepsHeatRatio()
    {
        // 重新自整定不应抹掉手工填好的加热比（与外环参数、稳态偏置同理）
        var s = new PidGainSchedule();
        s.Learn(new GainPoint(-10, new PidGains(10, 0.01, 100), HeatRatio: 1.8));
        s.Learn(new GainPoint(-10, new PidGains(14, 0.04, 400), 30, 190));

        Assert.Equal(14, s.GainsAt(-10)!.Kp, 6);
        Assert.Equal(1.8, s.HeatRatioAt(-10)!.Value, 6);
    }

    [Fact]
    public void SaveLoad_RoundTripsHeatRatio()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gains-hr-{Guid.NewGuid():N}.csv");
        try
        {
            var a = new PidGainSchedule();
            a.Learn(new GainPoint(110, new PidGains(5, 0.005, 50), HeatRatio: 0.6));
            a.Learn(P(-10, 12.9));                 // 没填加热比的行
            GainScheduleStore.Save(a, path);

            var b = new PidGainSchedule();
            GainScheduleStore.Load(b, path);

            Assert.Equal(2, b.Count);
            Assert.Equal(0.6, b.HeatRatioAt(110)!.Value, 3);
            Assert.Null(b.HeatRatioAt(-10));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_AcceptsOldSixColumnFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gains-old-{Guid.NewGuid():N}.csv");
        try
        {
            File.WriteAllLines(path, [
                "温度(℃),Kp,Ki,Kd,Ku,Tu(s)",
                "-10.000,12.8930,0.031400,382.4440,28.6510,186.9",
            ]);

            var s = new PidGainSchedule();
            GainScheduleStore.Load(s, path);

            Assert.Equal(1, s.Count);
            Assert.Equal(12.893, s.GainsAt(-10)!.Kp, 3);
            Assert.Null(s.OuterAt(-10));
            Assert.Null(s.SteadyBiasAt(-10));
        }
        finally { File.Delete(path); }
    }
}
