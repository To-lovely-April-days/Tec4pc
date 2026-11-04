using Tec.Driver.Abi;
using Tec.Drivers.DualStation;
using Tec.Drivers.Rd105;
using Tec.Drivers.Rd105.Modbus;
using Xunit;
using Xunit.Abstractions;

namespace Tec.Core.Tests;

/// <summary>
/// 双向挡釜内串级（上位机回路）：功率符号进的挂起段经答复接通 TEC 以后，不再补压夹套目标（2026-11-04 现场 R1-tc1-20261104-104405 那种：
/// 釜内还在目标下面、夹套冲高后要回落）。原来段里每拍「补一次压低」不分进法，接通以后把夹套目标压到釜内 − 2、冻外环积分——往反方向推。
/// 真时钟、假机带热模型，跑十几秒。
/// </summary>
public sealed class DuoSignHoldCascadeTests(ITestOutputHelper output)
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

    /// <summary>每 0.5 s 喂一次釜内 Tr（新鲜窗 10 s）。</summary>
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

    [Theory]
    [InlineData(19.0)]
    [InlineData(19.95)]
    public async Task 符号进的挂起_釜内在目标下面_答打开接TEC后_不压夹套目标(double tr)
    {
        BothTableSeed.Seed("DUO1");
        var rd = new FakeRd105Device { Thermal = true, HeatSign = -1, GainCPerSec = 1.0, AmbientC = 18 };
        rd.Set(1, "LIMITED", 90);
        rd.Set(2, "LIMITED", 90);
        rd.Set(1, "TCADJTEMP", 1_800_000);
        rd.Set(2, "TCADJTEMP", 1_800_000);
        var io = new FakeModbusSlave();
        var logs = new List<string>();
        string[] L() { lock (logs) return logs.ToArray(); }
        var drv = new DualStationDriver
        {
            LinksFactory = _ => new DuoLinks
            {
                Rd105 = new Rd105Link(rd),
                IoPort = io,
                Io = new Io8rClient(new ModbusRtuClient(io, 1, 200))
            }
        };
        var cfg = ParameterSet.Of((DualStationDriver.Fields.Chiller, "已开"));
        cfg[DualStationDriver.Fields.WaterLead] = 0d;
        var ctx = new DriverContext
        {
            InstanceId = "DUO1", ChannelNumbers = new[] { 1, 2 }, Config = cfg, Simulated = false, TimeScale = 1,
            Clock = () => DateTimeOffset.Now,
            Log = (l, text) => { lock (logs) logs.Add($"{DateTime.Now:HH:mm:ss.f} {l} {text}"); }
        };
        await using var s = await drv.OpenAsync(
            ParameterSet.Of((Rd105TecDriver.FieldPeriod, 200d), (DualStationDriver.Fields.Tick, 200d)), ctx, CancellationToken.None);
        await s.StartAsync(CancellationToken.None);
        var t = s.CapabilitiesOf(0).OfType<ITemperatureControl>().Single();
        var st = (ITemperatureStatus)t;
        await WaitUntil(() => !double.IsNaN(t.CurrentJacket), 3000, () => "夹套读数");
        using var feed = new TrFeeder(s, 1, tr);
        await Task.Delay(300);

        ((IHeatSource)t).AskWaterNext();
        await t.SetTargetAsync(new TempTarget(20, TempChannelKind.Reactor), CancellationToken.None);
        await Task.Delay(1500);
        rd.Set(1, "TCADJTEMP", 3_500_000);                 // 夹套冲高到 35：外环要往回收，回路要冷
        var w = s.CapabilitiesOf(0).OfType<ICoolingWaterValve>().Single();
        await WaitUntil(() => w.Pending is not null, 20000, () => string.Join("\n", L().TakeLast(10)));
        var q = w.Pending!;
        await Task.Delay(3000);
        Assert.Equal(q.Id, w.Pending?.Id);                 // 釜内在目标下面：挂起不撤、不重问
        Assert.True(w.Answer(q.Id, true));
        await WaitUntil(() => io.Coils[7] && !io.Coils[0], 15000, () => "接 TEC\n" + string.Join("\n", L().TakeLast(10)));
        await Task.Delay(2500);
        foreach (var l in L()) output.WriteLine(l);
        Assert.Null(st.CascadePushC);
        Assert.DoesNotContain(L(), l => l.Contains("夹套目标压到"));
        Assert.Equal("双向挡·制冷段（功率符号切 TEC）", ((IHeatSource)t).Regime);
        Assert.Single(L(), l => l.Contains("问操作人开不开冷却水"));
    }
}
