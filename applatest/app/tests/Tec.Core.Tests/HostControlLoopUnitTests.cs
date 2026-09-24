using TecControl.Core.Control;
using Tec.Drivers.Rd105;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 上位机回路（HostControlLoop / PidGainSchedule）0331 那几处本地改动的纯单元回归，直接架在假 RD105 上、不开会话：
/// · 执行器三态：TEC 只制冷（CoolOnly）限幅 [−上限, 0]、手动输出不能为正、双向 ⇄ 只制冷不算换对象；
/// · 前馈 / 最近稳态输出按执行器各存一份：加热棒学到的不给 TEC 预置；
/// · 外环该查哪张表：按离设定值最近的外环工作点，跟此刻执行器无关；
/// · 稳态偏置离得远不沿用；表里没行、学到偏置就按插值参数新建一行带着。
/// </summary>
public class HostControlLoopUnitTests
{
    private static (HostControlLoop Loop, FakeRd105Device Dev) Loop()
    {
        var dev = new FakeRd105Device();
        dev.Open();                                        // 不开会话，口子自己开
        return (new HostControlLoop(new Rd105Link(dev).Controller), dev);
    }

    private static readonly PidGains Inner = new(8, 0.02, 0);
    private static readonly PidGains Outer = new(2.5, 0.0079, 0);

    [Fact]
    public void 前馈与稳态输出按执行器各存一份()
    {
        var (loop, _) = Loop();
        loop.SetActuatorMode(1, ActuatorMode.HeatOnly);
        loop.GetFeedforward(1).Learn(50, 15);              // 加热棒维持 50 ℃ 要 +15 %
        loop.SetActuatorMode(1, ActuatorMode.CoolOnly);
        Assert.Null(loop.GetFeedforward(1).Predict(50));   // TEC 那份是空的：不拿 +15 去预置只制冷的积分
        loop.GetFeedforward(1).Learn(50, -20);
        loop.SetActuatorMode(1, ActuatorMode.HeatOnly);
        Assert.Equal(15, loop.GetFeedforward(1).Predict(50));
        loop.SetActuatorMode(1, ActuatorMode.Bidirectional);
        Assert.Equal(-20, loop.GetFeedforward(1).Predict(50));   // 双向和只制冷共一份（都是 TEC）
        // 不按 100 ℃ 分段：同一份前馈 90 和 110 能一起拟
        loop.GetFeedforward(1).Learn(110, -30);
        Assert.NotNull(loop.GetFeedforward(1).Predict(100));
    }

    [Fact]
    public async Task 只制冷形态_限幅上限0_手动输出不能为正_改上限跟着走()
    {
        var (loop, dev) = Loop();
        loop.ConfigurePid(1, 8, 0.02, 0, 90, invertOutput: false);
        loop.SetActuatorMode(1, ActuatorMode.CoolOnly);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => loop.ManualDutyAsync(1, 10));
        Assert.Contains("只制冷", ex.Message);
        await loop.ManualDutyAsync(1, -20);
        Assert.Equal(-20 * 20_000, dev.Get(1, "PWMDUTY"));
        await loop.ManualDutyAsync(1, 0);

        // 参数窗改了 LIMITED：上限跟着改，形态不变
        loop.SetMaxDuty(1, 50);
        await loop.ManualDutyAsync(1, -80);
        Assert.Equal(-80 * 20_000, dev.Get(1, "PWMDUTY"));  // 手动输出不受回路上限管（原样）
        Assert.Equal(ActuatorMode.CoolOnly, loop.GetActuatorMode(1));
        await loop.ManualDutyAsync(1, 0);

        // 只加热照旧不能为负
        loop.SetActuatorMode(1, ActuatorMode.HeatOnly);
        await Assert.ThrowsAsync<InvalidOperationException>(() => loop.ManualDutyAsync(1, -10));
    }

    [Fact]
    public void 外环该查哪张表_按离设定值最近的外环工作点_跟执行器无关()
    {
        var (loop, _) = Loop();
        var tec = loop.GetGainSchedule(1, ActuatorMode.Bidirectional);
        var heater = loop.GetGainSchedule(1, ActuatorMode.HeatOnly);
        tec.SegmentBoundaryC = double.NaN;
        heater.SegmentBoundaryC = double.NaN;
        tec.Learn(new GainPoint(25, Inner, 0, 0, Outer, 8));
        heater.Learn(new GainPoint(80, Inner, 0, 0, new PidGains(2.5, 0.0044, 0), 8));

        Assert.Same(tec, loop.GetOuterSchedule(1, 40));       // 离 25 近
        Assert.Same(heater, loop.GetOuterSchedule(1, 70));    // 离 80 近
        loop.SetActuatorMode(1, ActuatorMode.HeatOnly);
        Assert.Same(tec, loop.GetOuterSchedule(1, 40));       // 加热棒在出力也照样查 TEC 表——外环的对象是釜，不是执行器
        // 两张都没登记外环：此刻执行器那张（让手动参数兜底）
        var (loop2, _) = Loop();
        loop2.SetActuatorMode(1, ActuatorMode.HeatOnly);
        Assert.Same(loop2.GetGainSchedule(1, ActuatorMode.HeatOnly), loop2.GetOuterSchedule(1, 40));
    }

    [Fact]
    public void 稳态偏置离得远不沿用_表里没行学到就新建一行()
    {
        var sch = new PidGainSchedule { SegmentBoundaryC = double.NaN };
        sch.Learn(new GainPoint(50, Inner, 20, 300, Outer, 8, SteadyBiasC: 3.0));
        Assert.Equal(3.0, sch.SteadyBiasAt(60));            // 10 ℃ 内沿用
        Assert.Null(sch.SteadyBiasAt(140));                  // 90 ℃ 外不沿用（50 ℃ 的偏置喂给 140 ℃ 只会错）
        Assert.NotNull(sch.GainsAt(140));                    // 内环参数照旧取最近行

        // 60 ℃ 没有行：缺省不建（原来的规矩），要求建才建，带着插值参数与偏置，Ku/Tu 记 0（不是自整定的）
        Assert.False(sch.LearnSteadyBias(60, 2.0));
        Assert.True(sch.LearnSteadyBias(60, 2.0, createIfMissing: true));
        Assert.Equal(2, sch.Points.Count);
        var row = sch.Points.Single(p => Math.Abs(p.TemperatureC - 60) < 1e-9);
        Assert.Equal(2.0, row.SteadyBiasC);
        Assert.Equal(0, row.UltimateGainKu);
        Assert.Equal(Inner, row.Gains);
        Assert.NotNull(row.OuterGains);
        Assert.Equal(8, row.OuterMaxBiasC);                  // Max(8, |2| + 4)
        Assert.Equal(2.0, sch.SteadyBiasAt(60));
        // 再学一次 60.5：合并进那一行，不再新建
        Assert.True(sch.LearnSteadyBias(60.5, 2.5, createIfMissing: true));
        Assert.Equal(2, sch.Points.Count);
    }
}
