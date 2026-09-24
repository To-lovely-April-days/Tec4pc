using Tec.Driver.Abi;
using Tec.Drivers.DualStation;
using Tec.Drivers.Rd105;
using Tec.Drivers.Rd105.Modbus;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 上位机 PID 的整定台（面板「PID 整定」页背后那一层，IPidTuningBench / Rd105HostPid）。锁的是：
/// · 只有「控温方式 = 上位机 PID」才有；两个工位各一个；
/// · 两张表按执行器分（TEC / 加热棒）分别落盘，手动参数与调度开关落盘，重开读回；表不合法整张不动；
/// · 回路真按执行器取那张表的参数（看每拍的 P 项 / 误差），调度关了用手动那组；
/// · 自整定：单机 TEC 能起振、整定中拒绝下发、取消停输出；跑完平稳型登记进对应的表并存盘；
/// · 双工位：加热棒整定把继电器扳到加热棒、整定期间不自动换挡、取消 / 安全停机后全断；
///   「TEC 加热」不启用时 TEC 侧只制冷继电（占空比一笔反向加热都没有）；建议 / 拒绝的规矩。
/// 每个测试自己的增益表目录（Rd105HostControl.UseGainsDir），不碰用户目录、不互相串。
/// </summary>
public class PidTuningBenchTests
{
    private static ParameterSet Conn() => ParameterSet.Of((Rd105TecDriver.FieldPeriod, 200d));

    private static ParameterSet HostCfg()
        => ParameterSet.Of((Rd105TecDriver.FieldControl, Rd105TecDriver.ControlHost));

    private static string NewDir() => Path.Combine(Path.GetTempPath(), "tec-pid-" + Guid.NewGuid().ToString("N"));

    private static DriverContext Ctx(ParameterSet cfg, List<string>? logs = null, string id = "R1") => new()
    {
        InstanceId = id,
        ChannelNumbers = new[] { 1, 2 },
        Config = cfg,
        Simulated = false,
        TimeScale = 1,
        Clock = () => DateTimeOffset.Now,
        Log = (l, t) => { if (logs is not null) lock (logs) logs.Add($"{l} {t}"); }
    };

    private static (Rd105TecDriver Drv, FakeRd105Device Dev) Standalone()
    {
        var dev = new FakeRd105Device { Thermal = true, GainCPerSec = 1.0 };
        dev.Set(1, "LIMITED", 90); dev.Set(2, "LIMITED", 90);
        return (new Rd105TecDriver { LinkFactory = _ => new Rd105Link(dev) }, dev);
    }

    private static async Task WaitUntil(Func<bool> cond, int ms, Func<string> what)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond())
        {
            Assert.True(DateTime.UtcNow < deadline, "等太久：" + what());
            await Task.Delay(25);
        }
    }

    private static Task WaitUntil(Func<bool> cond, int ms, string what) => WaitUntil(cond, ms, () => what);

    private static IPidTuningBench Bench(IDeviceSession s, int well)
        => s.CapabilitiesOf(well).OfType<IPidTuningBench>().Single();

    private static ITemperatureControl Temp(IDeviceSession s, int well)
        => s.CapabilitiesOf(well).OfType<ITemperatureControl>().Single();

    private static long DutyOf(string cmd) => long.Parse(cmd.Split('=')[1].TrimEnd('@'));

    [Fact]
    public async Task 温控器PID方式没有整定台_上位机方式两个工位各一个()
    {
        using var _ = Rd105HostControl.UseGainsDir(NewDir());
        var (drv, _) = Standalone();
        await using (var dev = await drv.OpenAsync(Conn(), Ctx(ParameterSet.Of((Rd105TecDriver.FieldControl, Rd105TecDriver.ControlDevice))), CancellationToken.None))
        {
            Assert.Empty(dev.CapabilitiesOf(0).OfType<IPidTuningBench>());
            Assert.Empty(dev.CapabilitiesOf(1).OfType<IPidTuningBench>());
        }
        var (drv2, _) = Standalone();
        await using var host = await drv2.OpenAsync(Conn(), Ctx(HostCfg()), CancellationToken.None);
        Assert.NotSame(Bench(host, 0), Bench(host, 1));
        Assert.Equal(PidActuator.Tec, Bench(host, 0).CurrentActuator);
        Assert.Empty(Bench(host, 0).Rows(PidActuator.Tec));
        // 空表：在用的是手动那组（缺省 8 / 0.02 / 0），不是表里插出来的
        var use = Bench(host, 0).Preview(PidActuator.Tec, 25);
        Assert.False(use.InnerFromTable);
        Assert.Equal(8, use.Inner.Kp);
    }

    [Fact]
    public async Task 两张表按执行器分开落盘_手动参数与调度开关落盘_重开读回()
    {
        var dir = NewDir();
        using var _ = Rd105HostControl.UseGainsDir(dir);
        var (drv, _) = Standalone();
        var logs = new List<string>();
        await using (var s = await drv.OpenAsync(Conn(), Ctx(HostCfg(), logs), CancellationToken.None))
        {
            Assert.Contains(logs, l => l.Contains("TC1 上位机 PID") && l.Contains("表都空着"));
            var b = Bench(s, 0);
            b.ApplyRows(PidActuator.Tec, new[]
            {
                new PidGainRow(25, 12, 0.03, 350),
                new PidGainRow(-20, 10, 0.02, 300) { OuterKp = 2.5, OuterKi = 0.008, OuterMaxBiasC = 8 }
            });
            b.ApplyRows(PidActuator.Heater, new[] { new PidGainRow(60, 3, 0.01, 50) });

            Assert.True(File.Exists(Rd105HostControl.GainsPath("R1", 1)));
            Assert.True(File.Exists(Rd105HostControl.HeaterGainsPath("R1", 1)));
            Assert.Contains("温度(℃),Kp,Ki,Kd", File.ReadLines(Rd105HostControl.HeaterGainsPath("R1", 1)).First());
            Assert.False(File.Exists(Rd105HostControl.GainsPath("R1", 2)));      // B 没动过，不落空文件
            Assert.Equal(b.TablePath(PidActuator.Heater), Rd105HostControl.HeaterGainsPath("R1", 1));

            // 表按温度从低到高；两张表互不串
            Assert.Equal(new[] { -20d, 25 }, b.Rows(PidActuator.Tec).Select(r => r.TemperatureC));
            Assert.Equal(new[] { 60d }, b.Rows(PidActuator.Heater).Select(r => r.TemperatureC));
            Assert.Equal(8, b.Rows(PidActuator.Tec)[0].OuterMaxBiasC);
            Assert.Null(b.Rows(PidActuator.Tec)[1].OuterKp);

            // 插值：TEC 表 0 ℃ 落在 −20 与 25 之间；加热棒只有一行，全温域沿用；外环只有 −20 那行登记了
            var p = b.Preview(PidActuator.Tec, 0);
            Assert.True(p.InnerFromTable);
            Assert.Equal(10 + 2 * 20 / 45.0, p.Inner.Kp, 6);
            Assert.True(p.OuterFromTable);
            Assert.Equal(2.5, p.Outer.Kp, 6);
            Assert.Equal(3, b.Preview(PidActuator.Heater, 0).Inner.Kp, 6);
            Assert.Equal(1, b.Preview(PidActuator.Heater, 0).HeatRatio);

            // 手动参数与调度开关
            b.SetManual(new PidManual(new PidTuning(9, 0.03, 0), new PidTuning(2, 0.006, 0), 6));
            b.SetScheduling(false);
            var off = b.Preview(PidActuator.Tec, 0);
            Assert.False(off.InnerFromTable);
            Assert.Equal(9, off.Inner.Kp);
            Assert.False(off.OuterFromTable);
            Assert.Equal(6, off.OuterMaxBiasC);
            Assert.True(File.Exists(Rd105HostControl.ParamsPath("R1", 1)));
        }

        // 重开：两张表、手动参数、开关都回来
        var (drv2, dev2) = Standalone();
        await using var s2 = await drv2.OpenAsync(Conn(), Ctx(HostCfg()), CancellationToken.None);
        await s2.StartAsync(CancellationToken.None);
        var b2 = Bench(s2, 0);
        Assert.Equal(new[] { -20d, 25 }, b2.Rows(PidActuator.Tec).Select(r => r.TemperatureC));
        Assert.Equal(12, b2.Rows(PidActuator.Tec)[1].Kp);
        Assert.Equal(new[] { 60d }, b2.Rows(PidActuator.Heater).Select(r => r.TemperatureC));
        Assert.False(b2.Scheduling);
        Assert.Equal(9, b2.Manual.Inner.Kp);
        Assert.Equal(6, b2.Manual.OuterMaxBiasC);
        Assert.True(Bench(s2, 1).Scheduling);               // B 路的开关没被 A 带着改
    }

    [Fact]
    public async Task 稳态偏置可以手填_进表_预览与在用都拿它()
    {
        // 0335：稳态偏置从前只能等回路学（釜内 ±0.05 里待满 60 s），两炉都差一点没学到；现场量得出来（夹套比釜内高 5.5）就该能填
        using var _ = Rd105HostControl.UseGainsDir(NewDir());
        var (drv, _) = Standalone();
        await using var s = await drv.OpenAsync(Conn(), Ctx(HostCfg()), CancellationToken.None);
        var b = Bench(s, 0);
        b.ApplyRows(PidActuator.Heater, new[]
        {
            new PidGainRow(50, 11.63, 0.0157, 150) { OuterKp = 3, OuterKi = 0.004364, OuterKd = 0, OuterMaxBiasC = 15, SteadyBiasC = 5.5 }
        });
        var row = Assert.Single(b.Rows(PidActuator.Heater));
        Assert.Equal(5.5, row.SteadyBiasC);
        Assert.Equal(5.5, b.Preview(PidActuator.Heater, 50).SteadyBiasC);
        Assert.Equal(5.5, b.Preview(PidActuator.Heater, 55).SteadyBiasC);   // 15 ℃ 内沿用
        Assert.Null(b.Preview(PidActuator.Heater, 80).SteadyBiasC);

        // 重读盘上的表还在；清掉这一格就是没有
        b.Reload();
        Assert.Equal(5.5, Assert.Single(b.Rows(PidActuator.Heater)).SteadyBiasC);
        b.ApplyRows(PidActuator.Heater, new[] { row with { SteadyBiasC = null } });
        Assert.Null(Assert.Single(b.Rows(PidActuator.Heater)).SteadyBiasC);
    }

    [Fact]
    public async Task 表不合法整张不动_两度以内合并()
    {
        using var _ = Rd105HostControl.UseGainsDir(NewDir());
        var (drv, _) = Standalone();
        await using var s = await drv.OpenAsync(Conn(), Ctx(HostCfg()), CancellationToken.None);
        var b = Bench(s, 0);
        b.ApplyRows(PidActuator.Tec, new[] { new PidGainRow(25, 12, 0.03, 350) });

        var e1 = Assert.Throws<ArgumentException>(() => b.ApplyRows(PidActuator.Tec, new[]
        {
            new PidGainRow(0, 10, 0.02, 0),
            new PidGainRow(40, 10, 0.02, 0) { OuterKp = 2.5 }            // 外环只填一半
        }));
        Assert.Contains("第 2 行", e1.Message);
        Assert.Contains("外环只填了一半", e1.Message);
        Assert.Throws<ArgumentException>(() => b.ApplyRows(PidActuator.Tec, new[] { new PidGainRow(0, -1, 0.02, 0) }));
        Assert.Throws<ArgumentException>(() => b.ApplyRows(PidActuator.Tec, new[] { new PidGainRow(500, 10, 0.02, 0) }));
        Assert.Throws<ArgumentException>(() => b.ApplyRows(PidActuator.Tec, new[] { new PidGainRow(0, 0, 0, 5) }));
        Assert.Equal(new[] { 25d }, b.Rows(PidActuator.Tec).Select(r => r.TemperatureC));   // 原表不动

        b.ApplyRows(PidActuator.Tec, new[] { new PidGainRow(25, 12, 0.03, 350), new PidGainRow(26.5, 13, 0.03, 350) });
        var rows = b.Rows(PidActuator.Tec);
        Assert.Single(rows);                                 // 26.5 与 25 相差不到 2 ℃：同一个工作点，后面的覆盖
        Assert.Equal(13, rows[0].Kp);

        Assert.Throws<ArgumentException>(() => b.SetManual(new PidManual(new PidTuning(0, 0, 0), new PidTuning(2, 0.006, 0), 6)));
        Assert.Throws<ArgumentException>(() => b.SetManual(new PidManual(new PidTuning(8, 0.02, 0), new PidTuning(2, 0.006, 0), 0)));
    }

    [Fact]
    public async Task 回路真按执行器取那张表_调度关了用手动()
    {
        using var _ = Rd105HostControl.UseGainsDir(NewDir());
        var (drv, dev) = Standalone();
        await using var s = await drv.OpenAsync(Conn(), Ctx(HostCfg()), CancellationToken.None);
        await s.StartAsync(CancellationToken.None);
        var b = Bench(s, 0);
        b.ApplyRows(PidActuator.Tec, new[] { new PidGainRow(40, 5, 0, 0) });
        b.ApplyRows(PidActuator.Heater, new[] { new PidGainRow(40, 40, 0, 0) });
        var rd = (Rd105Session)s;
        var inner = rd.TempOf(0);
        await WaitUntil(() => !double.IsNaN(inner.CurrentJacket), 3000, "夹套读数");
        dev.Thermal = false;                                  // 温度钉住，误差不随时间变，看 P 项更干净

        double Kp() => b.Live is { Active: true } l && Math.Abs(l.SetpointC - l.MeasuredC) > 1
            ? l.P / (l.SetpointC - l.MeasuredC) : double.NaN;

        await inner.SetTargetAsync(new TempTarget(40, TempChannelKind.Jacket), CancellationToken.None);
        await WaitUntil(() => Math.Abs(Kp() - 5) < 0.05, 3000, () => $"TEC 表 Kp 5（此刻 {Kp()}）");
        Assert.Equal(PidActuator.Tec, b.InUse.Actuator);
        Assert.True(b.InUse.InnerFromTable);

        // 继电器扳到加热棒（挂起 → 换形态 → 恢复）：同一温度，换成加热棒那张表
        await inner.EnableAsync(false, CancellationToken.None);
        inner.SetActuator(electric: true);
        await inner.EnableAsync(true, CancellationToken.None);
        await WaitUntil(() => Math.Abs(Kp() - 40) < 0.4, 3000, () => $"加热棒表 Kp 40（此刻 {Kp()}）");
        Assert.Equal(PidActuator.Heater, b.InUse.Actuator);

        // 这一路关掉调度：固定用手动那组
        b.SetScheduling(false);
        await WaitUntil(() => Math.Abs(Kp() - 8) < 0.1, 3000, () => $"手动 Kp 8（此刻 {Kp()}）");
        Assert.False(b.InUse.InnerFromTable);
    }

    [Fact]
    public async Task 单机TEC自整定_起振_整定中拒绝下发_取消停输出()
    {
        using var _ = Rd105HostControl.UseGainsDir(NewDir());
        var (drv, dev) = Standalone();
        var logs = new List<string>();
        await using var s = await drv.OpenAsync(Conn(), Ctx(HostCfg(), logs), CancellationToken.None);
        await s.StartAsync(CancellationToken.None);
        var b = Bench(s, 0);
        var t = Temp(s, 0);
        await WaitUntil(() => !double.IsNaN(t.CurrentJacket), 3000, "夹套读数");

        Assert.Contains("没有加热棒切换", b.CheckTune(new PidAutoTuneRequest(30, PidActuator.Heater)));
        Assert.Contains("超出设备保护范围", b.CheckTune(new PidAutoTuneRequest(500, PidActuator.Tec)));
        Assert.Contains("继电幅值", b.CheckTune(new PidAutoTuneRequest(30, PidActuator.Tec, 2)));
        Assert.Equal(PidActuator.Tec, b.SuggestActuator(95));    // 单机没有热源切换：只有 TEC

        // 在控温也能发起：先停控温再整
        await t.SetTargetAsync(new TempTarget(28, TempChannelKind.Jacket), CancellationToken.None);
        PidAutoTuneReport? report = null;
        b.AutoTuneFinished += r => report = r;
        await b.StartAutoTuneAsync(new PidAutoTuneRequest(30, PidActuator.Tec, 30, 0.05), CancellationToken.None);
        Assert.True(b.Tuning);
        Assert.False(((ITemperatureStatus)t).Active);         // 控温停了
        Assert.Contains("自整定中", b.TuneNote);
        await WaitUntil(() => dev.Get(1, "PWMDUTY") != 0, 3000, "整定输出");
        Assert.Equal(3, dev.Get(1, "MODE"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => t.SetTargetAsync(new TempTarget(20, TempChannelKind.Jacket), CancellationToken.None));
        Assert.Contains("正在自整定", ex.Message);
        Assert.Contains("已经在自整定", b.CheckTune(new PidAutoTuneRequest(30, PidActuator.Tec)));

        await b.CancelAutoTuneAsync(CancellationToken.None);
        Assert.False(b.Tuning);
        Assert.NotNull(report);
        Assert.False(report!.Success);
        Assert.Equal("操作人取消", report.Reason);
        Assert.Equal(0, dev.Get(1, "PWMDUTY"));
        Assert.Equal(0, dev.Get(1, "ENABLE"));
        Assert.Contains(logs, l => l.Contains("自整定开始"));
        // 取消之后下发照常
        await t.SetTargetAsync(new TempTarget(28, TempChannelKind.Jacket), CancellationToken.None);
        Assert.True(((ITemperatureStatus)t).Active);
    }

    [Fact]
    public async Task 自整定跑完_平稳型登记进TEC表并存盘()
    {
        var dir = NewDir();
        using var _ = Rd105HostControl.UseGainsDir(dir);
        var (drv, dev) = Standalone();
        await using var s = await drv.OpenAsync(Conn(), Ctx(HostCfg()), CancellationToken.None);
        await s.StartAsync(CancellationToken.None);
        var b = Bench(s, 0);
        var t = Temp(s, 0);
        await WaitUntil(() => !double.IsNaN(t.CurrentJacket), 3000, "夹套读数");

        PidAutoTuneReport? report = null;
        var tablesChanged = 0;
        b.AutoTuneFinished += r => report = r;
        b.TablesChanged += () => Interlocked.Increment(ref tablesChanged);
        await b.StartAutoTuneAsync(new PidAutoTuneRequest(26, PidActuator.Tec, 30, 0.05), CancellationToken.None);
        await WaitUntil(() => report is not null, 60000, () => $"整定结束（{b.TuneNote}）");

        Assert.True(report!.Success, report.Reason);
        Assert.True(report.Registered);
        Assert.True(report.Ku > 0);
        Assert.True(report.TuSeconds > 0);
        Assert.NotNull(report.Conservative);
        Assert.NotNull(report.Fast);
        var row = Assert.Single(b.Rows(PidActuator.Tec));
        Assert.Equal(26, row.TemperatureC, 6);
        Assert.True(row.FromAutoTune);
        Assert.Equal(report.Conservative!.Kp, row.Kp, 6);
        Assert.NotNull(row.OuterKp);                        // 外环按 Tu 推荐一组
        Assert.Empty(b.Rows(PidActuator.Heater));
        Assert.True(File.Exists(Rd105HostControl.GainsPath("R1", 1)));
        Assert.True(tablesChanged > 0);
        Assert.False(b.Tuning);
        Assert.Equal(0, dev.Get(1, "ENABLE"));              // 整完输出关掉
    }

    // ── 双工位：继电器配合 ────────────────────────────────────────────

    private sealed class Duo
    {
        public FakeRd105Device Rd = new() { Thermal = true, HeatSign = -1, GainCPerSec = 1.0 };
        public FakeModbusSlave Io = new();
        public List<string> Logs = new();
        public DualStationDriver Drv;

        public Duo()
        {
            Rd.Set(1, "LIMITED", 90); Rd.Set(2, "LIMITED", 90);
            Drv = new DualStationDriver
            {
                LinksFactory = _ => new DuoLinks
                {
                    Rd105 = new Rd105Link(Rd),
                    IoPort = Io,
                    Io = new Io8rClient(new ModbusRtuClient(Io, 1, 200))
                }
            };
        }

        public DriverContext Ctx(ParameterSet? cfg = null) => new()
        {
            InstanceId = "DUO1",
            ChannelNumbers = new[] { 1, 2 },
            Config = cfg ?? new ParameterSet(),          // 主机缺省 = 上位机 PID、反向、加热棒负、「TEC 加热」不启用
            Simulated = false,
            TimeScale = 1,
            Clock = () => DateTimeOffset.Now,
            Log = (l, t) => { lock (Logs) Logs.Add($"{l} {t}"); }
        };

        public static ParameterSet Conn() => ParameterSet.Of((Rd105TecDriver.FieldPeriod, 200d), (DualStationDriver.Fields.Tick, 200d));

        /// <summary>「冷水机」标为已开（0333：TEC 没有冷却水不能开，TEC 那张表只有标成已开才整得了）。</summary>
        public static ParameterSet ChillerOn() => ParameterSet.Of((DualStationDriver.Fields.Chiller, "已开"));
    }

    [Fact]
    public async Task 双工位加热棒整定_继电器扳到加热棒_整定中不换挡不许下发_取消后全断()
    {
        using var _ = Rd105HostControl.UseGainsDir(NewDir());
        var d = new Duo();
        await using var s = await d.Drv.OpenAsync(Duo.Conn(), d.Ctx(Duo.ChillerOn()), CancellationToken.None);
        await s.StartAsync(CancellationToken.None);
        var b = Bench(s, 0);
        var t = Temp(s, 0);
        await WaitUntil(() => !double.IsNaN(t.CurrentJacket), 3000, "夹套读数");

        // 建议：阈值以上加热棒；「TEC 加热」不启用时比夹套低超过一个死区（要制冷才维持得住）才是 TEC，其余加热棒
        Assert.Equal(PidActuator.Heater, b.SuggestActuator(95));
        Assert.Equal(PidActuator.Heater, b.SuggestActuator(50));
        Assert.Equal(PidActuator.Tec, b.SuggestActuator(5));
        Assert.Contains("TEC 不接", b.CheckTune(new PidAutoTuneRequest(95, PidActuator.Tec)));
        Assert.Null(b.CheckTune(new PidAutoTuneRequest(40, PidActuator.Heater)));

        var n = d.Rd.Commands.Count;
        PidAutoTuneReport? report = null;
        b.AutoTuneFinished += r => report = r;
        await b.StartAutoTuneAsync(new PidAutoTuneRequest(40, PidActuator.Heater, 20, 0.05), CancellationToken.None);
        Assert.True(d.Io.Coils[0]);                          // 加热棒继电器合
        Assert.False(d.Io.Coils[6]);                         // TEC 功率线断
        Assert.Equal(PidActuator.Heater, b.CurrentActuator);
        await WaitUntil(() => d.Rd.Get(1, "PWMDUTY") < 0, 3000, "加热棒负占空比");
        await Task.Delay(1000);                              // 五拍采集循环：没在控温，但整定占着继电器，不许断
        Assert.True(d.Io.Coils[0]);
        Assert.False(d.Io.Coils[6]);
        // 加热棒只加热：一笔正占空比（这台机器上的加热棒符号是负）都没有
        Assert.All(d.Rd.Commands.Skip(n).Where(c => c.StartsWith("TC1:PWMDUTY=")), c => Assert.True(DutyOf(c) <= 0, c));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => t.SetTargetAsync(new TempTarget(20, TempChannelKind.Jacket), CancellationToken.None));
        Assert.Contains("正在自整定", ex.Message);
        Assert.True(d.Io.Coils[0]);                          // 拒绝了就一个继电器都没动

        await b.CancelAutoTuneAsync(CancellationToken.None);
        Assert.Equal("操作人取消", report?.Reason);
        await WaitUntil(() => !d.Io.Coils[0] && !d.Io.Coils[6], 3000, "取消后全断");
        Assert.Equal(0, d.Rd.Get(1, "PWMDUTY"));
        Assert.Contains(d.Logs, l => l.Contains("自整定结束") && l.Contains("全断"));
    }

    [Fact]
    public async Task 单方向整定幅值不超过LIMITED一半_双向TEC不受限()
    {
        using var _ = Rd105HostControl.UseGainsDir(NewDir());
        var d = new Duo();
        d.Rd.Set(1, "LIMITED", 80);
        await using var s = await d.Drv.OpenAsync(Duo.Conn(), d.Ctx(Duo.ChillerOn()), CancellationToken.None);
        await s.StartAsync(CancellationToken.None);
        var b = Bench(s, 0);
        var t = Temp(s, 0);
        await WaitUntil(() => !double.IsNaN(t.CurrentJacket), 3000, "夹套读数");

        // 加热棒只加热：LIMITED 80 → 幅值最多 40（上限按开机读到的 LIMITED 算，不是写死的 90）
        var heat = b.CheckTune(new PidAutoTuneRequest(40, PidActuator.Heater, 50, 0.05));
        Assert.Contains("一半", heat);
        Assert.Contains("LIMITED 80", heat);
        Assert.Contains("最多 40", heat);
        Assert.Null(b.CheckTune(new PidAutoTuneRequest(40, PidActuator.Heater, 40, 0.05)));
        // 「TEC 加热」不启用：TEC 只制冷，同样单方向
        Assert.Contains("一半", b.CheckTune(new PidAutoTuneRequest(20, PidActuator.Tec, 50, 0.05)));
        Assert.Null(b.CheckTune(new PidAutoTuneRequest(20, PidActuator.Tec, 20, 0.05)));
        // 被拒的请求一个继电器都没动
        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => b.StartAutoTuneAsync(new PidAutoTuneRequest(40, PidActuator.Heater, 50, 0.05), CancellationToken.None));
        Assert.Contains("一半", ex.Message);
        Assert.False(b.Tuning);
        Assert.False(d.Io.Coils[0]);

        // 单机 RD105 的 TEC 是双向的：幅值不受这条限
        var (drv, _) = Standalone();
        await using var solo = await drv.OpenAsync(Conn(), Ctx(HostCfg()), CancellationToken.None);
        Assert.DoesNotContain("一半", Bench(solo, 0).CheckTune(new PidAutoTuneRequest(20, PidActuator.Tec, 60, 0.05)) ?? "");
    }

    [Fact]
    public async Task 双工位TEC整定_TEC加热不启用只制冷继电_安全停机收尾()
    {
        using var _ = Rd105HostControl.UseGainsDir(NewDir());
        var d = new Duo();
        await using var s = await d.Drv.OpenAsync(Duo.Conn(), d.Ctx(Duo.ChillerOn()), CancellationToken.None);
        await s.StartAsync(CancellationToken.None);
        var b = Bench(s, 0);
        var t = Temp(s, 0);
        await WaitUntil(() => !double.IsNaN(t.CurrentJacket), 3000, "夹套读数");

        var n = d.Rd.Commands.Count;
        PidAutoTuneReport? report = null;
        b.AutoTuneFinished += r => report = r;
        await b.StartAutoTuneAsync(new PidAutoTuneRequest(20, PidActuator.Tec, 30, 0.05), CancellationToken.None);
        Assert.True(d.Io.Coils[6]);                          // TEC 功率线合
        Assert.False(d.Io.Coils[0]);                         // 加热棒断
        Assert.Contains(d.Logs, l => l.Contains("只制冷继电"));
        await WaitUntil(() => d.Rd.Get(1, "PWMDUTY") > 0, 3000, "TEC 制冷（反向配置下制冷是正的）");
        await WaitUntil(() => t.CurrentJacket < 21, 15000, () => $"夹套往 20 走（{t.CurrentJacket:F2}）");
        await Task.Delay(1500);
        // 只制冷：这台是「反向」——制冷写正、加热写负；整定期间一笔负占空比（TEC 反向加热）都没有
        Assert.All(d.Rd.Commands.Skip(n).Where(c => c.StartsWith("TC1:PWMDUTY=")), c => Assert.True(DutyOf(c) >= 0, c));

        // 安全停机：整定收尾（报「被停下」），继电器全断
        await s.SafeStopAsync(0, CancellationToken.None);
        await WaitUntil(() => report is not null, 3000, "整定被停下的报告");
        Assert.False(report!.Success);
        Assert.Contains("被停下", report.Reason);
        Assert.False(b.Tuning);
        Assert.False(d.Io.Coils[6]);
        Assert.False(d.Io.Coils[0]);
        // 之后下发照常（没被整定旗拦着）
        await t.SetTargetAsync(new TempTarget(15, TempChannelKind.Jacket), CancellationToken.None);
        Assert.True(((ITemperatureStatus)t).Active);
    }
}
