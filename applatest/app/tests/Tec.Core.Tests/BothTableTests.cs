using TecControl.Core.Control;
using Tec.Core.Benches;
using Tec.Core.Catalog;
using Tec.Core.Recipes;
using Tec.Driver.Abi;
using Tec.Drivers.Rd105;
using Tec.Drivers.Simulator;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 双向挡单独一张增益表（用户定的，2026-10-29）。锁的是：
/// · 整定器两半周永远一正一负（加热棒 ↔ TEC），中心挪到头也不变 0；TEC 那半周被冷水机最短间隔扣过的那一周不算、连着两周判失败；
/// · 回路：加热棒 + TEC 双向默认查双向挡表、双向挡里只加热同表不复位、整定中不许换表、双向挡表上不许按单方向整；
///   积分预置在双向挡那份没学到时借加热棒那份；
/// · 整定台：双向挡表落 -both.csv、带加热比；「整过没有」的规矩（2 ℃ / 30 K / 只算自整定的行 / 釜内带稳态偏置 / 建议点按保护范围）；
/// · 配方开跑前校验：会下发控温目标的步落在没整过的温度报错。
/// </summary>
public class BothTableTests
{
    // ── 整定器 ─────────────────────────────────────────────────────

    private static RelayAutoTuner Alternating(double bias) =>
        new(50, relayAmplitudePercent: 20, hysteresisC: 0.05, biasPercent: bias,
            outputCeilingPercent: 90, alternateMinPercent: 3);

    [Fact]
    public void 双向挡整定器_两半周一正一负_中心挪到头也不变0()
    {
        // 维持 50 ℃ 要 +30 %（比幅值 20 还大）：单方向那套会让往下那半周还是加热棒 +10 %；双向挡把中心钳在 幅值 − 3 = 17
        var tuner = Alternating(bias: 30);
        Assert.True(tuner.Alternating);
        Assert.Equal(17, tuner.BiasPercent);
        var t0 = new DateTime(2026, 1, 1);
        Assert.Equal(37, tuner.Process(t0, 49));                    // 加热半周：加热棒
        Assert.Equal(-3, tuner.Process(t0.AddSeconds(1), 50.2));    // 冲过去了：TEC，至少 3 %
        Assert.True(tuner.CoolingHalf);
    }

    [Fact]
    public void 双向挡整定器_一阶对象两边推_整得出来()
    {
        var tuner = Alternating(bias: 0);
        var (state, _) = RunPlant(tuner, withheldWhenCooling: false);
        Assert.True(state == AutoTuneState.Succeeded, tuner.FailReason);
        Assert.True(tuner.Result!.UltimateGainKu > 0);
    }

    [Fact]
    public void 双向挡整定器_TEC半周被冷水机间隔扣着_这一周不算_连着两周判失败()
    {
        var tuner = Alternating(bias: 0);
        var (state, _) = RunPlant(tuner, withheldWhenCooling: true);
        Assert.Equal(AutoTuneState.Failed, state);
        Assert.Contains("冷水机最短间隔", tuner.FailReason);
    }

    [Fact]
    public void 双向挡整定器_只有一周被扣_不连着就不判失败_照样整得出来()
    {
        // 被扣着的那半周掉过设定值时还扣着：那一周不算，下一周正常接 TEC 就照常算（不能把「扣着」带进下一周）
        var tuner = Alternating(bias: 0);
        var start = new DateTime(2026, 1, 1);
        double t = 45, q = 0;
        var coolingHalves = 0;
        var wasCooling = false;
        for (var s = 0; s < 6 * 3600 && tuner.State is not (AutoTuneState.Succeeded or AutoTuneState.Failed); s++)
        {
            var now = start.AddSeconds(s);
            if (tuner.CoolingHalf && !wasCooling) coolingHalves++;
            wasCooling = tuner.CoolingHalf;
            var held = tuner.CoolingHalf && coolingHalves == 3;            // 只扣第三个制冷半周（已经在量了）
            tuner.SetWithheld(now, held);
            var u = tuner.Process(now, t);
            if (held) u = Math.Max(0, u);
            q += (u - q) / 20;
            t += 2e-3 * q - 2e-4 * (t - 25);
        }
        Assert.True(tuner.State == AutoTuneState.Succeeded, tuner.FailReason);
    }

    [Fact]
    public void 挂着时只记温度不换向()
    {
        var tuner = Alternating(bias: 0);
        var t0 = new DateTime(2026, 1, 1);
        tuner.Process(t0, 49);
        tuner.Observe(t0.AddSeconds(1), 51);                        // 切继电器那几秒：记极值，不换向
        Assert.False(tuner.CoolingHalf);
        Assert.Equal(-20, tuner.Process(t0.AddSeconds(2), 50.2));
    }

    /// <summary>一阶对象：正输出加热、负输出制冷，向 25 ℃ 漏热；执行器 20 s 滞后。被扣着时负输出送不出去（钳 0，自然凉）。</summary>
    private static (AutoTuneState, double) RunPlant(RelayAutoTuner tuner, bool withheldWhenCooling)
    {
        var start = new DateTime(2026, 1, 1);
        double t = 45, q = 0;
        for (var s = 0; s < 6 * 3600; s++)
        {
            var now = start.AddSeconds(s);
            var held = withheldWhenCooling && tuner.CoolingHalf;
            tuner.SetWithheld(now, held);
            var u = tuner.Process(now, t);
            if (tuner.State is AutoTuneState.Succeeded or AutoTuneState.Failed) return (tuner.State, s);
            if (held) u = Math.Max(0, u);
            q += (u - q) / 20;
            t += 2e-3 * q - 2e-4 * (t - 25);
        }
        return (tuner.State, 6 * 3600);
    }

    // ── 回路 ───────────────────────────────────────────────────────

    private static (HostControlLoop Loop, FakeRd105Device Dev) Loop()
    {
        var dev = new FakeRd105Device();
        dev.Open();
        return (new HostControlLoop(new Rd105Link(dev).Controller), dev);
    }

    [Fact]
    public void 加热棒加TEC双向默认查双向挡表_双向挡里只加热同表_表不能乱配()
    {
        var (loop, _) = Loop();
        loop.SetActuatorMode(1, ActuatorMode.HeaterTec);
        Assert.Equal(GainTable.Both, loop.GetTable(1));
        Assert.Same(loop.GetGainSchedule(1, GainTable.Both), loop.GetGainSchedule(1, ActuatorMode.HeaterTec));
        Assert.NotSame(loop.GetGainSchedule(1, GainTable.Both), loop.GetGainSchedule(1, ActuatorMode.HeatOnly));
        loop.SetActuatorMode(1, ActuatorMode.HeatOnly, GainTable.Both);          // TEC 暂时接不上：只加热、表不换
        Assert.Equal(GainTable.Both, loop.GetTable(1));
        Assert.Throws<InvalidOperationException>(() => loop.SetActuatorMode(1, ActuatorMode.CoolOnly, GainTable.Both));
        loop.SetActuatorMode(1, ActuatorMode.HeatOnly);                          // 升温挡：加热棒那张
        Assert.Equal(GainTable.Heater, loop.GetTable(1));
        // 双向挡表外环只查自己那张（TEC 表登记了外环也不借）
        var tec = loop.GetGainSchedule(1, GainTable.Tec);
        tec.SegmentBoundaryC = double.NaN;
        tec.Learn(new GainPoint(50, new PidGains(8, 0.02, 0), 0, 0, new PidGains(2.5, 0.0079, 0), 8));
        loop.SetActuatorMode(1, ActuatorMode.HeaterTec);
        Assert.Same(loop.GetGainSchedule(1, GainTable.Both), loop.GetOuterSchedule(1, 50));
    }

    [Fact]
    public void 双向挡积分预置_自己那份没学到借加热棒那份正的()
    {
        var (loop, _) = Loop();
        loop.SetActuatorMode(1, ActuatorMode.HeatOnly);
        loop.GetFeedforward(1).Learn(50, 15);                    // 升温挡学到：维持 50 ℃ 加热棒 +15 %
        loop.SetActuatorMode(1, ActuatorMode.HeaterTec);
        Assert.Equal(15, loop.PredictSteadyOutput(1, 50));       // 双向挡第一次跑 50 ℃：借它，不从 0 起
        loop.GetFeedforward(1).Learn(50, 12);                    // 双向挡自己学到了就用自己的
        Assert.Equal(12, loop.PredictSteadyOutput(1, 50));
    }

    [Fact]
    public async Task 双向挡表上不许按单方向整_整定中不许换表_TEC半周乘比值()
    {
        var (loop, dev) = Loop();
        loop.ConfigurePid(1, 8, 0.02, 0, 90, invertOutput: false);
        loop.SetActuatorMode(1, ActuatorMode.HeaterTec);
        await Assert.ThrowsAsync<InvalidOperationException>(() => loop.StartAutoTuneAsync(1, 50, 20, 0.05));
        await loop.StartAlternatingAutoTuneAsync(1, 50, heaterPercent: 20, tecPercent: 40, hysteresisC: 0.05);
        Assert.Throws<InvalidOperationException>(() => loop.SetActuatorMode(1, ActuatorMode.HeatOnly));
        loop.SetActuatorMode(1, ActuatorMode.HeatOnly, GainTable.Both);          // 同表换形态照样许
        loop.SetActuatorMode(1, ActuatorMode.HeaterTec, GainTable.Both);
        // 夹套 60 > 50：制冷半周，PID 那一侧 −20，TEC 乘比值 2 = −40 %
        dev.Set(1, "TCADJTEMP", 60_00000);
        loop.Start();
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (dev.Get(1, "PWMDUTY") != -40 * 20_000 && DateTime.UtcNow < deadline) await Task.Delay(50);
            Assert.Equal(-40 * 20_000, dev.Get(1, "PWMDUTY"));
            Assert.Equal(-20, loop.GetTuningOutput(1));
        }
        finally
        {
            await loop.StopChannelAsync(1);
            loop.Dispose();
        }
        Assert.Null(loop.GetTuningOutput(1));
    }

    // ── 整定台：落盘与「整过没有」──────────────────────────────────

    private static string NewDir() => Path.Combine(Path.GetTempPath(), "tec-both-" + Guid.NewGuid().ToString("N"));

    private static async Task<(IDeviceSession S, IPidTuningBench B)> Bench()
    {
        var dev = new FakeRd105Device();
        var drv = new Rd105TecDriver { LinkFactory = _ => new Rd105Link(dev) };
        var s = await drv.OpenAsync(ParameterSet.Of((Rd105TecDriver.FieldPeriod, 200d)), new DriverContext
        {
            InstanceId = "R1",
            ChannelNumbers = new[] { 1, 2 },
            Config = ParameterSet.Of((Rd105TecDriver.FieldControl, Rd105TecDriver.ControlHost)),
            Simulated = false,
            TimeScale = 1,
            Clock = () => DateTimeOffset.Now
        }, CancellationToken.None);
        return (s, s.CapabilitiesOf(0).OfType<IPidTuningBench>().Single());
    }

    private static PidGainRow Tuned(double t) => new(t, 8, 0.02, 0) { Ku = 10, TuSeconds = 120 };

    [Fact]
    public async Task 双向挡表落both文件_带加热比_别的表不动()
    {
        using var _ = Rd105HostControl.UseGainsDir(NewDir());
        var (s, b) = await Bench();
        await using var __ = s;
        b.ApplyRows(PidActuator.Both, new[] { Tuned(50) with { HeatRatio = 2 } });
        Assert.True(File.Exists(Rd105HostControl.BothGainsPath("R1", 1)));
        Assert.Equal(Rd105HostControl.BothGainsPath("R1", 1), b.TablePath(PidActuator.Both));
        Assert.False(File.Exists(Rd105HostControl.GainsPath("R1", 1)));
        Assert.False(File.Exists(Rd105HostControl.HeaterGainsPath("R1", 1)));
        b.Reload();
        Assert.Equal(2, Assert.Single(b.Rows(PidActuator.Both)).HeatRatio);
        Assert.Empty(b.Rows(PidActuator.Tec));
    }

    [Fact]
    public async Task 整过没有_离点2度内或夹在30K内两点之间_手填不算_釜内带稳态偏置_建议点按保护范围()
    {
        using var _ = Rd105HostControl.UseGainsDir(NewDir());
        var (s, b) = await Bench();
        await using var __ = s;
        var hp = (Rd105HostPid)b;
        Assert.Contains("已整：无", hp.BothCoverage(40, 40));
        b.ApplyRows(PidActuator.Both, new[]
        {
            Tuned(25), Tuned(50) with { SteadyBiasC = 6 }, Tuned(110),
            new PidGainRow(140, 8, 0.02, 0)                      // 手填的：不算整过
        });
        Assert.Null(hp.BothCoverage(26, 26));
        Assert.Null(hp.BothCoverage(40, 40));                    // 25 ~ 50 相距 25：中间插值
        Assert.Null(hp.BothCoverage(25, 50));                    // 斜坡整段都盖住
        var gap = hp.BothCoverage(60, 60);                       // 50 ~ 110 相距 60：中间缺点
        Assert.Contains("先在「PID 整定」页按双向挡整 80 ℃", gap);
        Assert.NotNull(hp.BothCoverage(25, 60));
        Assert.Contains("手填的行不算", hp.BothCoverage(140, 140));
        // 釜内 50：夹套设定在 50 + 6 = 56，那一头没盖住
        Assert.Null(hp.BothCoverage(50, 50));
        Assert.NotNull(hp.BothCoverage(50, 50, reactor: true));
        // 超温上限缺省 180：200 那个建议点整不了，不列
        var top = hp.BothCoverage(175, 175)!;
        Assert.Contains("整 170、175 ℃", top);
        Assert.DoesNotContain("200", top);
        // 调度关了（固定手动参数）不查
        b.SetScheduling(false);
        Assert.Null(hp.BothCoverage(60, 60));
    }

    // ── 配方开跑前校验 ─────────────────────────────────────────────

    private sealed class FakeHeat : IHeatSource
    {
        public int Channel => 1;
        public bool TecHeating => false;
        public bool ElectricAvailable => true;
        public bool OnElectric => true;
        public string? UntunedReason(double from, double to, TempChannelKind kind)
            => Math.Max(from, to) > 60 ? $"双向挡表里 {Math.Max(from, to):0} ℃ 还没整定" : null;
    }

    [Fact]
    public void 配方开跑前_控温梯度起始结束落在没整过的温度报错()
    {
        var catalog = new CommandCatalog();
        catalog.Register(new Rd105ReactorDriver().Commands);
        var ch = new Channel(1, "X1", 0);
        ch.Capabilities.Add(new ICapability[] { new FakeHeat() });
        var recipe = Harness.RecipeOf("双向挡",
            Harness.Mk(CommandSpecs.Control, ("target", 40d), ("obj", "釜内 Tr")),
            Harness.Mk(CommandSpecs.Control, ("target", 80d), ("obj", "釜内 Tr")),
            new Step
            {
                CommandId = CommandSpecs.Gradient,
                Parameters = new ParameterSet(),
                Rows = new List<ParameterSet> { ParameterSet.Of(("t", 40d), ("r", 1d), ("h", 1d)), ParameterSet.Of(("t", 70d), ("r", 1d), ("h", 1d)) }
            },
            Harness.Mk(BuiltinCommands.Finish, ("cool", true), ("safe", 30d)));
        var issues = RecipeValidator.Validate(recipe, catalog, ch).Where(i => i.Code == "untuned").ToList();
        Assert.All(issues, i => Assert.Equal(IssueLevel.Error, i.Level));
        Assert.DoesNotContain(issues, i => i.Message.StartsWith("第 1 步"));                        // 40：整过
        Assert.Contains(issues, i => i.Message.StartsWith("第 2 步") && i.Message.Contains("80 ℃"));   // 40 → 80 的斜坡
        Assert.Contains(issues, i => i.Message.StartsWith("第 3 步") && i.Message.Contains("70 ℃"));   // 梯度 40 → 70 那段
        Assert.Contains(issues, i => i.Message.StartsWith("第 4 步"));                              // 结束降温：从 70 降到 30
    }
}
