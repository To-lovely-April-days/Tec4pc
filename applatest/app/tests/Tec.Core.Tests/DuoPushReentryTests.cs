using Tec.Driver.Abi;
using Tec.Drivers.DualStation;
using Tec.Drivers.Rd105.Modbus;
using Tec.Drivers.Rd105;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 【本地改动】双向挡·压低再进间隔（2026-11-06，现场 R1-tc1-20261106-130619：65.8 → 70 ℃）：
/// 31.46 min 功率转正松开压低，31.55 min（5 s 后）Tr 70.15 ℃ 相对目标 +0.04 K/min（0.01 K 量化的抖动）又按提前判据进、再压 2 K；
/// 第二段的最短 60 s 把继电器扣在 TEC 上而内环早已要加热（−90 %）——夹套掉到 63.8 ℃（压低目标 67.7，低 4 K）、Tr 掉 1.56 K、15 分钟才爬回来。
/// 改成：提前判据进的段结束（回落 / 符号退 / 挂起期间回落）之后「压低再进间隔」（缺省 60 s）内不再按提前判据进，继电器只按功率符号走；
/// 进段的「在升」至少要 30 s 窗的斜率（10 s 窗一格量化就过门槛）。
/// 温控器 PID 假机、假钟一拍一拍推（同 DualStationDriverTests 那组），只验继电器与段的规矩。
/// </summary>
public sealed class DuoPushReentryTests
{
    private sealed class Bench
    {
        public FakeRd105Device Rd = new();
        public FakeModbusSlave Io = new();
        public DualStationDriver Drv = null!;
        public List<(string Level, string Text)> Logs = new();
        public DateTimeOffset Now = DateTimeOffset.Now;

        public DriverContext Ctx(ParameterSet? config = null)
        {
            config ??= new ParameterSet();
            if (!config.Has(Rd105TecDriver.FieldControl)) config[Rd105TecDriver.FieldControl] = Rd105TecDriver.ControlDevice;
            if (!config.Has(DualStationDriver.Fields.WaterLead)) config[DualStationDriver.Fields.WaterLead] = 0d;
            return new DriverContext
            {
                InstanceId = "DUO1",
                ChannelNumbers = new[] { 1, 2 },
                Config = config,
                Simulated = false,
                TimeScale = 1,
                Clock = () => Now,
                Log = (lvl, text) => { lock (Logs) Logs.Add((lvl, text)); }
            };
        }

        public int Count(string needle) { lock (Logs) return Logs.Count(l => l.Text.Contains(needle)); }
        public string Tail() { lock (Logs) return string.Join("\n", Logs.TakeLast(8).Select(l => l.Text)); }
    }

    private static Bench Rig()
    {
        var b = new Bench();
        b.Drv = new DualStationDriver
        {
            LinksFactory = _ => new DuoLinks
            {
                Rd105 = new Rd105Link(b.Rd),
                IoPort = b.Io,
                Io = new Io8rClient(new ModbusRtuClient(b.Io, 1, 200))
            }
        };
        return b;
    }

    /// <summary>RD105 周期 200 ms、模块轮询 5 s（手动一拍一拍推）。</summary>
    private static ParameterSet Conn() => ParameterSet.Of((Rd105TecDriver.FieldPeriod, 200d), (DualStationDriver.Fields.Tick, 5000d));

    private static ParameterSet ChillerOn(params (string Key, object? Value)[] more)
        => ParameterSet.Of(new (string, object?)[] { (DualStationDriver.Fields.Chiller, "已开") }.Concat(more).ToArray());

    private static ITemperatureControl Temp(IDeviceSession s) => s.CapabilitiesOf(0).OfType<ITemperatureControl>().Single();
    private static ICoolingWaterValve Water(IDeviceSession s) => s.CapabilitiesOf(0).OfType<ICoolingWaterValve>().Single();

    private static async Task WaitJacket(ITemperatureControl t, double value)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && !(Math.Abs(t.CurrentJacket - value) <= 0.01))
            await Task.Delay(30);
        Assert.Equal(value, t.CurrentJacket, 1);
    }

    /// <summary>温控器 PID 方式的功率：写进假机的占空比就是回路看到的（这台加热棒吃负：−5 = 要加热 5 %，+20 = 要制冷 20 %）。</summary>
    private static async Task DeviceDuty(Bench b, IDeviceSession s, double percent)
    {
        b.Rd.Set(1, "PWMDUTY", (long)Math.Round(percent * 20_000));
        var inner = ((DuoSession)s).InnerTemp(0);
        var deadline = DateTime.UtcNow.AddSeconds(4);
        while (DateTime.UtcNow < deadline && !(Math.Abs(inner.LastDuty - percent) <= 0.01)) await Task.Delay(50);
        Assert.Equal(percent, inner.LastDuty, 2);
    }

    /// <summary>把夹套温度拨到 tj、钟拨过 dt 秒、推一拍（被控量进滑窗、判据跑一遍）。</summary>
    private static async Task Drive(Bench b, IDeviceSession s, ITemperatureControl t, double tj, double dt)
    {
        b.Rd.Set(1, "TCADJTEMP", (long)Math.Round(tj * 1e5));
        await WaitJacket(t, tj);
        b.Now = b.Now.AddSeconds(dt);
        await ((DuoSession)s).PollOnceAsync(CancellationToken.None);
    }

    private static async Task<(IDeviceSession S, ITemperatureControl T, DuoSession D)> Open(Bench b, ParameterSet? cfg = null)
    {
        var s = await b.Drv.OpenAsync(Conn(), b.Ctx(cfg ?? ChillerOn()), CancellationToken.None);
        await s.StartAsync(CancellationToken.None);
        var t = Temp(s);
        var d = (DuoSession)s;
        d.CoolMinDuration = TimeSpan.FromSeconds(20);
        d.ExitLead = TimeSpan.Zero;                        // 退段只看被控量本身低过 目标 − δ
        await WaitJacket(t, 25.0);
        return (s, t, d);
    }

    /// <summary>
    /// 升温到 30 ℃（加热棒合着），再把夹套顶过 30.1 且在升：5 s 一拍、每拍 +0.03 K，**凑够 30 s 跨度**那一拍（30.18）才按提前判据切 TEC。
    /// 点：30.00@31、30.03@36、30.06@41、30.09@46、30.12@51、30.15@56、30.18@61（滑窗留 35 s：进段窗 30 s + 一拍 5 s）。
    /// </summary>
    private static async Task RiseIntoTec(Bench b, IDeviceSession s, ITemperatureControl t, double target = 30, bool hold = false)
    {
        await t.SetTargetAsync(new TempTarget(target), CancellationToken.None);
        Assert.True(b.Io.Coils[0]);
        Assert.False(b.Io.Coils[7]);
        await DeviceDuty(b, s, -5);                        // 加热棒还在出力：功率符号说的是加热
        var before = b.Count("提前切 TEC，不等功率翻号");
        await Drive(b, s, t, target, 31);                  // 旧点出窗
        foreach (var tj in new[] { 0.03, 0.06, 0.09, 0.12, 0.15 }) await Drive(b, s, t, target + tj, 5);
        Assert.True(b.Io.Coils[0], "30 s 跨度没凑够不该进段\n" + b.Tail());
        Assert.Equal(before, b.Count("提前切 TEC，不等功率翻号"));
        await Drive(b, s, t, target + 0.18, 5);
        Assert.Equal(before + 1, b.Count("提前切 TEC，不等功率翻号"));
        if (hold)
        {
            // 手动开的、开那一下没问过：段立了、先问「开不开冷却水」挂起——加热棒继电器留着、不接 TEC
            Assert.True(b.Io.Coils[0], "挂起期间加热棒继电器该留着\n" + b.Tail());
            Assert.False(b.Io.Coils[7]);
            Assert.Contains("等人答开不开冷却水", ((IHeatSource)t).Regime);
            return;
        }
        Assert.True(b.Io.Coils[7], "凑够 30 s 跨度、高过 δ 且在升：该切 TEC\n" + b.Tail());
        Assert.False(b.Io.Coils[0]);
        Assert.Contains("制冷段", ((IHeatSource)t).Regime);
    }

    /// <summary>TEC 在制冷、被控量回落到目标 − δ 以下且在降（段龄 ≥ 20 s）：段结束，继电器交给回路；回路转成要加热就回加热棒。</summary>
    private static async Task FallOutOfTec(Bench b, IDeviceSession s, ITemperatureControl t, double target = 30)
    {
        await DeviceDuty(b, s, 20);                        // TEC 在制冷（功率 −20 %）
        foreach (var (dv, dt) in new[] { (-0.05, 5.0), (-0.10, 5.0), (-0.15, 5.0) }) await Drive(b, s, t, target + dv, dt);
        Assert.True(b.Io.Coils[7]);
        await Drive(b, s, t, target - 0.20, 6);            // 段龄 21 s ≥ 20：低过 目标 − 0.1 且在降 → 段结束
        Assert.DoesNotContain("制冷段", ((IHeatSource)t).Regime ?? "");
        Assert.True(b.Io.Coils[7]);                        // 回路还要冷：留在 TEC
        await DeviceDuty(b, s, -5);                        // 回路不要冷了（要加热 5 %）
        await Drive(b, s, t, target - 0.25, 2);            // 低过目标且在降、回路不要冷：立刻回加热棒
        Assert.True(b.Io.Coils[0]);
        Assert.False(b.Io.Coils[7]);
    }

    [Fact]
    public async Task 回落松开后_再进间隔内又高过且在升_不按提前判据进_加热棒照出力_到点才再进()
    {
        var b = Rig();
        var (s, t, d) = await Open(b);
        await using var _ = s;
        Assert.Equal(TimeSpan.FromSeconds(60), d.ReentryDwell);           // 缺省 60 s（配置缺省）

        await RiseIntoTec(b, s, t);
        await FallOutOfTec(b, s, t);
        var exit = b.Now;                                                  // 段结束那一拍（29.80 @ 6 s 那拍之后又推了 2 s）
        Assert.Equal(1, b.Count("60 s 内不再按提前判据进"));
        var mark = b.Io.Requests.Count;

        // 松开后釜壁回弹：被控量再冲过目标且在升（30 s 窗斜率清清楚楚过门槛）——间隔内不进、不报「提前切 TEC」，加热棒继电器留着
        // 点：29.75@+2、29.85@+7、29.95@+12、30.05@+17、30.15@+22、30.25@+27、…、30.85@+57 都在 60 s 之内
        foreach (var dv in new[] { -0.15, -0.05, 0.05, 0.15, 0.25, 0.35, 0.45, 0.55, 0.65, 0.75, 0.85 })
            await Drive(b, s, t, 30 + dv, 5);
        Assert.True((b.Now - exit) < TimeSpan.FromSeconds(60), $"用例时间轴错了：{(b.Now - exit).TotalSeconds} s");
        Assert.True(b.Io.Coils[0], "再进间隔内不该切 TEC\n" + b.Tail());
        Assert.False(b.Io.Coils[7]);
        Assert.Empty(b.Io.Requests.Skip(mark).Where(r => r.StartsWith("写")));
        Assert.Equal(1, b.Count("提前切 TEC，不等功率翻号"));
        Assert.Equal(1, b.Count("再进间隔 60 s 内不按提前判据进，继电器按功率符号走"));   // 只报一次
        Assert.DoesNotContain("制冷段", ((IHeatSource)t).Regime ?? "");

        // 到点（≥ 60 s）：还高过且在升 → 照常按提前判据进
        await Drive(b, s, t, 30.95, 5);
        Assert.True((b.Now - exit) >= TimeSpan.FromSeconds(60));
        Assert.True(b.Io.Coils[7], "间隔到点该再进\n" + b.Tail());
        Assert.False(b.Io.Coils[0]);
        Assert.Equal(2, b.Count("提前切 TEC，不等功率翻号"));
        Assert.Contains("制冷段", ((IHeatSource)t).Regime);
        await s.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task 功率符号退出松开_间隔内回路要冷照符号接TEC_不算提前判据_不压不立段()
    {
        var b = Rig();
        var (s, t, d) = await Open(b);
        await using var _ = s;
        await RiseIntoTec(b, s, t);

        // 被控量平了（不再升）、回路转成要加热（+20 %）够 3 s：段结束（功率符号退）、回加热棒——间隔从这一刻算
        await Drive(b, s, t, 30.18, 31);
        await DeviceDuty(b, s, -20);
        await Drive(b, s, t, 30.18, 1);
        await Drive(b, s, t, 30.18, 3.5);
        Assert.True(b.Io.Coils[0], "符号退该回加热棒\n" + b.Tail());
        Assert.False(b.Io.Coils[7]);
        Assert.Equal(1, b.Count("制冷段结束（功率已连续 3 s 指向电加热"));
        Assert.Equal(1, b.Count("60 s 内不再按提前判据进"));
        var exit = b.Now;

        // 又升起来（高过 δ、30 s 窗斜率过门槛）：间隔内不按提前判据进
        foreach (var tj in new[] { 30.28, 30.38, 30.48, 30.58, 30.68, 30.78 }) await Drive(b, s, t, tj, 5);
        Assert.True(b.Io.Coils[0], "再进间隔内不该切 TEC\n" + b.Tail());
        Assert.Equal(1, b.Count("提前切 TEC，不等功率翻号"));
        Assert.Equal(1, b.Count("再进间隔 60 s 内不按提前判据进"));

        // 间隔内回路真要冷（−20 % 连续 3 s）：照功率符号接 TEC——符号进法，不立提前判据的段、不压
        await DeviceDuty(b, s, 20);
        await Drive(b, s, t, 30.88, 1);
        await Drive(b, s, t, 30.98, 3.5);
        Assert.True((b.Now - exit) < TimeSpan.FromSeconds(60), $"用例时间轴错了：{(b.Now - exit).TotalSeconds} s");
        Assert.True(b.Io.Coils[7], "回路要冷该按符号接 TEC\n" + b.Tail());
        Assert.False(b.Io.Coils[0]);
        Assert.Equal(1, b.Count("已连续 3 s 指向TEC——合 TEC 功率线"));
        Assert.Equal(1, b.Count("提前切 TEC，不等功率翻号"));
        Assert.DoesNotContain("提前切 TEC", ((IHeatSource)t).Regime ?? "");
        await s.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task 挂起期间回落撤段_也算松开_间隔内不再进()
    {
        // 手动开的温控、开那一下没问过：提前判据进段先问「开不开冷却水」挂起（加热棒留着）；等人期间被控量回落撤段——同样记再进间隔
        var b = Rig();
        var (s, t, d) = await Open(b);
        await using var _ = s;
        d.AskTimeout = TimeSpan.FromHours(1);              // 问题一直挂着（假钟要拨过 60 s，别让它到时按打开落地）
        ((IHeatSource)t).AskWaterNext();
        await RiseIntoTec(b, s, t, hold: true);
        var q = Water(s).Pending;
        Assert.NotNull(q);

        foreach (var (tj, dt) in new[] { (30.10, 5.0), (30.00, 5.0), (29.90, 5.0), (29.80, 5.0) }) await Drive(b, s, t, tj, dt);
        Assert.DoesNotContain("制冷段", ((IHeatSource)t).Regime ?? "");
        Assert.Equal(1, b.Count("等水期间被控量已回落"));
        Assert.Equal(1, b.Count("60 s 内不再按提前判据进"));
        Assert.Equal(q!.Id, Water(s).Pending?.Id);          // 问题留着（本次控温只问一次）
        var exit = b.Now;

        foreach (var tj in new[] { 29.90, 30.00, 30.10, 30.20, 30.30, 30.40, 30.50 }) await Drive(b, s, t, tj, 5);
        Assert.True((b.Now - exit) < TimeSpan.FromSeconds(60));
        Assert.True(b.Io.Coils[0]);
        Assert.False(b.Io.Coils[7]);
        Assert.Equal(1, b.Count("提前切 TEC，不等功率翻号"));
        Assert.Equal(1, b.Count("再进间隔 60 s 内不按提前判据进"));
        Assert.DoesNotContain("制冷段", ((IHeatSource)t).Regime ?? "");

        // 到点再进：又挂同一个问题等答复
        foreach (var tj in new[] { 30.60, 30.70, 30.80, 30.90, 31.00, 31.10 }) await Drive(b, s, t, tj, 5);
        Assert.True((b.Now - exit) >= TimeSpan.FromSeconds(60));
        Assert.Equal(2, b.Count("提前切 TEC，不等功率翻号"));
        Assert.Contains("等人答开不开冷却水", ((IHeatSource)t).Regime);
        await s.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task 再进间隔配0_松开后立刻能再进()
    {
        var b = Rig();
        var (s, t, d) = await Open(b, ChillerOn((DualStationDriver.Fields.PushReentry, 0d)));
        await using var _ = s;
        Assert.Equal(TimeSpan.Zero, d.ReentryDwell);
        await RiseIntoTec(b, s, t);
        await FallOutOfTec(b, s, t);
        Assert.Equal(0, b.Count("不再按提前判据进"));

        foreach (var dv in new[] { -0.15, -0.05, 0.05, 0.15 }) await Drive(b, s, t, 30 + dv, 5);
        Assert.True(b.Io.Coils[7], "间隔配 0：高过且在升就该再进\n" + b.Tail());
        Assert.Equal(2, b.Count("提前切 TEC，不等功率翻号"));
        Assert.Equal(0, b.Count("再进间隔"));
        await s.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task 新目标下发_再进间隔作废()
    {
        var b = Rig();
        var (s, t, d) = await Open(b);
        await using var _ = s;
        await RiseIntoTec(b, s, t);
        await FallOutOfTec(b, s, t);
        var exit = b.Now;
        Assert.Equal(1, b.Count("60 s 内不再按提前判据进"));

        // 新目标 31：滑窗清掉、间隔作废；从新目标起凑够 30 s 窗就能进（离松开还不到 60 s）
        await t.SetTargetAsync(new TempTarget(31), CancellationToken.None);
        Assert.True(b.Io.Coils[0]);
        await Drive(b, s, t, 31.0, 1);
        foreach (var dv in new[] { 0.03, 0.06, 0.09, 0.12, 0.15, 0.18 }) await Drive(b, s, t, 31 + dv, 5);
        Assert.True((b.Now - exit) < TimeSpan.FromSeconds(60), $"用例时间轴错了：{(b.Now - exit).TotalSeconds} s");
        Assert.True(b.Io.Coils[7], "新目标之后不该再被旧间隔拦\n" + b.Tail());
        Assert.Equal(2, b.Count("提前切 TEC，不等功率翻号"));
        Assert.Equal(0, b.Count("再进间隔 60 s 内不按提前判据进"));
        await s.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task 进段的斜率至少跨30s窗_12s不进_凑够才进_已在TEC侧的回落照旧10s()
    {
        var b = Rig();
        var (s, t, d) = await Open(b);
        await using var _ = s;
        Assert.Equal(TimeSpan.FromSeconds(30), d.EnterSlopeMinSpan);
        Assert.Equal(TimeSpan.FromSeconds(10), d.SlopeMinSpan);
        Assert.Equal(0.0005, d.SlopeEpsKPerS);

        await t.SetTargetAsync(new TempTarget(30), CancellationToken.None);
        await DeviceDuty(b, s, -5);
        await Drive(b, s, t, 30.0, 31);
        // 原来的写法：30.18 @ 12 s 跨度就切（2026-10-28 那组用例）——现在 12 s 不够 30 s，不进
        foreach (var (tj, dt) in new[] { (30.06, 4.0), (30.12, 4.0), (30.18, 4.0) }) await Drive(b, s, t, tj, dt);
        Assert.True(b.Io.Coils[0], "12 s 跨度不该进段\n" + b.Tail());
        Assert.False(b.Io.Coils[7]);
        Assert.Equal(0, b.Count("提前切 TEC"));
        // 继续升：30.24@49、30.30@55、30.36@61——跨度到 30 s 那一拍进（30.18 @ 43 那拍之后又 18 s）
        await Drive(b, s, t, 30.24, 6);
        await Drive(b, s, t, 30.30, 6);
        Assert.True(b.Io.Coils[0], "24 s 跨度还不够\n" + b.Tail());
        await Drive(b, s, t, 30.36, 6);
        Assert.True(b.Io.Coils[7], "30 s 跨度、高过 δ 且在升：该进\n" + b.Tail());
        Assert.Equal(1, b.Count("提前切 TEC，不等功率翻号"));
        await t.StopAsync(CancellationToken.None);

        // 已在 TEC 侧（降温目标起步合 TEC）、低过目标且在降：回加热棒照旧 10 s 窗就表态
        await WaitJacket(t, 30.36);
        await t.SetTargetAsync(new TempTarget(10), CancellationToken.None);
        Assert.True(b.Io.Coils[7]);
        await DeviceDuty(b, s, 0);                         // 回路没在明确要冷
        await Drive(b, s, t, 10.4, 31);
        await Drive(b, s, t, 10.2, 5);
        await Drive(b, s, t, 10.0, 5);
        Assert.True(b.Io.Coils[7]);
        await Drive(b, s, t, 9.8, 5);                      // 15 s 跨度：低过 9.9 且在降 → 回加热棒
        Assert.True(b.Io.Coils[0], "回落照旧 10 s 窗\n" + b.Tail());
        Assert.False(b.Io.Coils[7]);
        await s.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task 配置字段_压低再进间隔_缺省60_范围0到300_开会话读取并钳位()
    {
        var schema = new DualStationDriver().ConfigSchema;
        var f = schema.Fields.Single(x => x.Key == DualStationDriver.Fields.PushReentry);
        Assert.Equal("压低再进间隔", DualStationDriver.Fields.PushReentry);
        Assert.Equal(60d, f.Default);
        Assert.Equal(0d, f.Min);
        Assert.Equal(300d, f.Max);
        Assert.Equal("s", f.Unit);
        Assert.Contains("2026-11-06", f.Tip);
        // 紧挨着「最短制冷时间」（跟退出超前 / 最短制冷时间一组）
        var keys = schema.Fields.Select(x => x.Key).ToList();
        Assert.Equal(keys.IndexOf(DualStationDriver.Fields.CoolMin) + 1, keys.IndexOf(DualStationDriver.Fields.PushReentry));
        var cfg = new ParameterSet().FillDefaults(schema);
        Assert.Equal(60, cfg.Num(DualStationDriver.Fields.PushReentry));

        foreach (var (given, want) in new[] { (120d, 120d), (999d, 300d), (-5d, 0d) })
        {
            var b = Rig();
            await using var s = await b.Drv.OpenAsync(Conn(), b.Ctx(ChillerOn((DualStationDriver.Fields.PushReentry, given))), CancellationToken.None);
            Assert.Equal(TimeSpan.FromSeconds(want), ((DuoSession)s).ReentryDwell);
        }
        // 不填：缺省 60
        {
            var b = Rig();
            await using var s = await b.Drv.OpenAsync(Conn(), b.Ctx(ChillerOn()), CancellationToken.None);
            Assert.Equal(TimeSpan.FromSeconds(60), ((DuoSession)s).ReentryDwell);
        }
    }
}
