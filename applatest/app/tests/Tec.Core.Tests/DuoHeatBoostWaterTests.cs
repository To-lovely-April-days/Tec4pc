using TecControl.Core.Control;
using Tec.Driver.Abi;
using Tec.Drivers.DualStation;
using Tec.Drivers.Rd105;
using Tec.Drivers.Rd105.Modbus;
using Xunit;
using Xunit.Abstractions;

namespace Tec.Core.Tests;

/// <summary>
/// 升温加速（HostControlLoop.HeatBoost）的会话一侧：回路靠 TEC 刹车，会话判「TEC 接不接得上」每拍推给回路，回路在等 TEC 就提前备水。
/// · 不用问（配方 / 没武装询问）：一开始就开冷却水阀，通水后放行，加速期间在加热棒侧也不关水；
/// · 手动开的、开那一下没问过（AskWaterNext）：提前问开不开冷却水（不扳继电器），答开 → 开水放行；答不开 → TEC 直接接（不开水）放行；
/// · 关着（缺省）：不问、不开水，跟原来一样。
/// 真时钟、假机（夹套热模型），釜内 Tr 喂常数——只验接线和水，不验控温效果（那个在 <see cref="HeatBoostTwinTests"/>）。
/// </summary>
public sealed class DuoHeatBoostWaterTests(ITestOutputHelper output)
{
    private static async Task WaitUntil(Func<bool> cond, int ms, Func<string> what)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond())
        {
            Assert.True(DateTime.UtcNow < deadline, "等太久：" + what());
            await Task.Delay(25);
        }
    }

    private sealed class TrFeeder : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();

        public TrFeeder(IDeviceSession s, int channel, double value)
        {
            var ext = (IExternalReactorTemp)s;
            _ = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    ext.FeedReactor(channel, value, Quality.Good);
                    try { await Task.Delay(500, _cts.Token); } catch (OperationCanceledException) { break; }
                }
            });
        }

        public void Dispose() => _cts.Cancel();
    }

    /// <summary>双向挡表：−40 ~ 230 都整过（同 BothTableSeed），20 ℃ 那行带外环参数和学到的稳态偏置 +1.6（没有就不加速）。</summary>
    private static void SeedWithBias(string instanceId)
    {
        foreach (var tc in new[] { 1, 2 })
        {
            var sch = new PidGainSchedule { SegmentBoundaryC = double.NaN };
            for (var t = -40; t <= 230; t += 30)
                sch.Learn(t == 20
                    ? new GainPoint(t, Rd105HostControl.FallbackInner, 1, 100, new PidGains(2.5, 0.01, 0), 8, SteadyBiasC: 1.6)
                    : new GainPoint(t, Rd105HostControl.FallbackInner, UltimateGainKu: 1, UltimatePeriodTuSeconds: 100));
            Rd105HostControl.Save(sch, Rd105HostControl.BothGainsPath(instanceId, tc));
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        public readonly FakeRd105Device Rd = new() { Thermal = true, HeatSign = -1, GainCPerSec = 1.0, AmbientC = 18 };
        public readonly FakeModbusSlave Io = new();
        private readonly List<string> _logs = new();
        public IDeviceSession S = null!;
        public ITemperatureControl T = null!;
        public ICoolingWaterValve W = null!;
        private TrFeeder? _feed;

        public string[] Logs() { lock (_logs) return _logs.ToArray(); }
        public bool WaterOn => Io.Coils[4];          // 冷却水阀 DO4（DO 从 0 数）

        /// <summary>增益表 / 逐拍记录目录的替换是 AsyncLocal：在 async 方法里设，回到测试方法就没了（逐拍记录会写进用户目录）——
        /// 先在这个同步方法里设好（留在测试方法的上下文里），再进 async 的那一半。</summary>
        public static Task<Rig> OpenAsync(string id, string boost)
        {
            Rd105HostControl.UseGainsDir(Path.Combine(Path.GetTempPath(), "tec-boost-" + Guid.NewGuid().ToString("N")));   // 测试流程里一直有效，不收回
            SeedWithBias(id);
            return OpenCoreAsync(id, boost);
        }

        private static async Task<Rig> OpenCoreAsync(string id, string boost)
        {
            var r = new Rig();
            r.Rd.Set(1, "LIMITED", 90);
            r.Rd.Set(2, "LIMITED", 90);
            r.Rd.Set(1, "TCADJTEMP", 1_600_000);
            r.Rd.Set(2, "TCADJTEMP", 1_600_000);
            var drv = new DualStationDriver
            {
                LinksFactory = _ => new DuoLinks
                {
                    Rd105 = new Rd105Link(r.Rd),
                    IoPort = r.Io,
                    Io = new Io8rClient(new ModbusRtuClient(r.Io, 1, 200))
                }
            };
            var cfg = ParameterSet.Of((DualStationDriver.Fields.Chiller, "已开"));
            cfg[DualStationDriver.Fields.WaterLead] = 0d;
            cfg[DualStationDriver.Fields.HeatBoost] = boost;
            var ctx = new DriverContext
            {
                InstanceId = id, ChannelNumbers = new[] { 1, 2 }, Config = cfg, Simulated = false, TimeScale = 1,
                Clock = () => DateTimeOffset.Now,
                Log = (l, text) => { lock (r._logs) r._logs.Add($"{DateTime.Now:HH:mm:ss.f} {l} {text}"); }
            };
            r.S = await drv.OpenAsync(
                ParameterSet.Of((Rd105TecDriver.FieldPeriod, 200d), (DualStationDriver.Fields.Tick, 200d)), ctx, CancellationToken.None);
            await r.S.StartAsync(CancellationToken.None);
            r.T = r.S.CapabilitiesOf(0).OfType<ITemperatureControl>().Single();
            r.W = r.S.CapabilitiesOf(0).OfType<ICoolingWaterValve>().Single();
            await WaitUntil(() => !double.IsNaN(r.T.CurrentJacket), 3000, () => "夹套读数");
            r._feed = new TrFeeder(r.S, 1, 15.0);
            await Task.Delay(300);
            return r;
        }

        public string Regime => ((IHeatSource)T).Regime ?? "";

        public async ValueTask DisposeAsync()
        {
            _feed?.Dispose();
            await S.DisposeAsync();
        }
    }

    private void Dump(Rig r)
    {
        foreach (var l in r.Logs()) output.WriteLine(l);
    }

    [Fact]
    public async Task 不用问_一开始就开冷却水_通水后进加速_停控关水()
    {
        await using var r = await Rig.OpenAsync("BOOST-A", "正常");
        await r.T.SetTargetAsync(new TempTarget(20, TempChannelKind.Reactor), CancellationToken.None);
        await WaitUntil(() => r.WaterOn, 5000, () => "开冷却水\n" + string.Join("\n", r.Logs().TakeLast(10)));
        await WaitUntil(() => r.Logs().Any(l => l.Contains("升温加速：釜内")), 5000,
            () => "进加速\n" + string.Join("\n", r.Logs().TakeLast(10)));
        Assert.Contains(r.Logs(), l => l.Contains("升温加速要用 TEC 刹车：冷却水阀（DO4）先打开"));
        Assert.Contains(r.Logs(), l => l.Contains("控温记录 →") && l.Contains("tec-boost-"));   // 逐拍记录进临时目录，不进用户目录
        Assert.EndsWith("·升温加速", r.Regime);
        Assert.Null(r.W.Pending);                                       // 没武装询问：不问
        Assert.True(r.Io.Coils[0] && !r.Io.Coils[7]);                   // 加热棒那只合着、TEC 那只断着（在升温）
        await Task.Delay(1500);
        Assert.True(r.WaterOn);                                         // 在加热棒侧也不关水（认领着）
        await r.T.StopAsync(CancellationToken.None);
        await WaitUntil(() => !r.WaterOn, 5000, () => "停控关水\n" + string.Join("\n", r.Logs().TakeLast(10)));
        Dump(r);
    }

    [Fact]
    public async Task 手动开的没问过_提前问_答开才开水进加速()
    {
        await using var r = await Rig.OpenAsync("BOOST-B", "保守");
        ((IHeatSource)r.T).AskWaterNext();
        await r.T.SetTargetAsync(new TempTarget(20, TempChannelKind.Reactor), CancellationToken.None);
        await WaitUntil(() => r.W.Pending is not null, 5000, () => "提前问\n" + string.Join("\n", r.Logs().TakeLast(10)));
        var q = r.W.Pending!;
        Assert.Contains("升温加速要用 TEC 刹车", q.Why);
        Assert.True(q.Waiting);                                         // 面板按「正等着」说
        await Task.Delay(1000);
        Assert.False(r.WaterOn);                                        // 答之前不开水
        Assert.DoesNotContain(r.Logs(), l => l.Contains("升温加速：釜内"));
        Assert.Contains("开不开冷却水的询问挂着", r.Regime);
        Assert.Equal(q.Id, r.W.Pending?.Id);                            // 不重问
        Assert.True(r.W.Answer(q.Id, true));
        await WaitUntil(() => r.WaterOn, 5000, () => "答开后开水\n" + string.Join("\n", r.Logs().TakeLast(10)));
        await WaitUntil(() => r.Logs().Any(l => l.Contains("升温加速：釜内")), 5000,
            () => "进加速\n" + string.Join("\n", r.Logs().TakeLast(10)));
        Assert.Contains(r.Logs(), l => l.Contains("询问有了结果：操作人选了打开冷却水——升温加速先开阀"));
        Assert.Single(r.Logs(), l => l.Contains("提前问操作人开不开冷却水"));
        Dump(r);
    }

    [Fact]
    public async Task 手动开的答不开_TEC直接接_不开水也进加速()
    {
        await using var r = await Rig.OpenAsync("BOOST-C", "正常");
        ((IHeatSource)r.T).AskWaterNext();
        await r.T.SetTargetAsync(new TempTarget(20, TempChannelKind.Reactor), CancellationToken.None);
        await WaitUntil(() => r.W.Pending is not null, 5000, () => "提前问");
        Assert.True(r.W.Answer(r.W.Pending!.Id, false));
        await WaitUntil(() => r.Logs().Any(l => l.Contains("升温加速：釜内")), 5000,
            () => "进加速\n" + string.Join("\n", r.Logs().TakeLast(10)));
        Assert.Contains(r.Logs(), l => l.Contains("询问有了结果：操作人选了不开冷却水——升温加速：TEC 直接接"));
        await Task.Delay(1000);
        Assert.False(r.WaterOn);
        Dump(r);
    }

    [Fact]
    public async Task 关着_不问不开水_挡位文字不变()
    {
        await using var r = await Rig.OpenAsync("BOOST-D", "关");
        ((IHeatSource)r.T).AskWaterNext();
        await r.T.SetTargetAsync(new TempTarget(20, TempChannelKind.Reactor), CancellationToken.None);
        await Task.Delay(3000);
        Assert.False(r.WaterOn);
        Assert.Null(r.W.Pending);
        Assert.DoesNotContain(r.Logs(), l => l.Contains("升温加速"));
        Assert.Equal("双向挡（加热棒 ↔ TEC 跟功率符号走）", r.Regime);
        Dump(r);
    }
}
