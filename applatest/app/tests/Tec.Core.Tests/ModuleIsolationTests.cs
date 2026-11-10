using System.Diagnostics;
using Tec.Core.Benches;
using Tec.Core.Catalog;
using Tec.Core.Records;
using Tec.Core.Safety;
using Tec.Driver.Abi;
using Tec.Drivers.Aqmd;
using Tec.Drivers.DualStation;
using Tec.Drivers.Rd105.Modbus;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 各模块互不干涉（用户 2026-11-10：「有的设备模块通信异常的情况下，我关闭搅拌都关不了」）。
/// 盯四件事：中止收尾各设备各有预算、紧急程序各动作各有预算、通道挂载顺序与谁先连上无关、
/// 串口事务不占公共线程池、不同串口的设备分在不同组里同时连。
/// </summary>
public sealed class ModuleIsolationTests
{
    private static Recipes.Recipe Waiting(double seconds = 30)
        => Harness.RecipeOf("等着", Harness.Mk(BuiltinCommands.Wait, ("dur", seconds)));

    private static IEnumerable<string> SafeStopLines(ChannelRun r)
        => r.Events.Where(e => e.Kind == EventKind.SafeStop).Select(e => e.Text);

    /// <summary>一台能配能力、能配停机行为的假设备。</summary>
    private sealed class Stub : IDeviceSession
    {
        private readonly Func<CancellationToken, ValueTask<IReadOnlyList<string>?>> _stop;
        private readonly ICapability[] _caps;

        public Stub(string id, Func<CancellationToken, ValueTask<IReadOnlyList<string>?>>? stop = null, params ICapability[] caps)
        {
            InstanceId = id;
            _stop = stop ?? (_ => ValueTask.FromResult<IReadOnlyList<string>?>(Array.Empty<string>()));
            _caps = caps;
        }

        public string InstanceId { get; }
        public DeviceState State => DeviceState.Ready;
        public event EventHandler<DeviceState>? StateChanged { add { } remove { } }
        public IReadOnlyList<TagDescriptor> Tags => Array.Empty<TagDescriptor>();
        public IObservable<Sample> Samples { get; } = new Broadcast<Sample>();
        public int WellCount => 1;
        public IReadOnlyList<ICapability> CapabilitiesOf(int well) => _caps;
        public ICommandHandler? Resolve(string commandId) => null;
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public long StopCalledAtMs { get; private set; } = -1;
        public int Calls { get; private set; }

        public ValueTask<IReadOnlyList<string>?> SafeStopAsync(int well, CancellationToken ct)
        {
            Calls++;
            StopCalledAtMs = Environment.TickCount64;
            return _stop(ct);
        }
    }

    /// <summary>温控器不通：下发目标一直等到令牌到期。</summary>
    private sealed class HangingTemp : ITemperatureControl
    {
        public int Channel => 1;
        public TempLimits Limits { get; } = new(-40, 180, 5);
        public double CurrentReactor => 25;
        public double CurrentJacket => 25;
        public async Task SetTargetAsync(TempTarget target, CancellationToken ct) => await Task.Delay(Timeout.Infinite, ct);
        public Task RampAsync(double target, double ratePerMin, TempChannelKind kind, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> WaitReachedAsync(double target, double tolerance, TimeSpan timeout, CancellationToken ct) => Task.FromResult(true);
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
        public IObservable<Sample> Temperature { get; } = new Broadcast<Sample>();
    }

    private sealed class FakeStir : IStirrer
    {
        public int Channel => 1;
        public SpeedLimits Limits { get; } = new(0, 1000);
        public double CurrentRpm { get; private set; }
        public double? SetTo { get; private set; }
        public Task SetSpeedAsync(double rpm, CancellationToken ct) { ct.ThrowIfCancellationRequested(); SetTo = rpm; CurrentRpm = rpm; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct) { CurrentRpm = 0; return Task.CompletedTask; }
        public IObservable<Sample> Speed { get; } = new Broadcast<Sample>();
    }

    private sealed class FakeSensor(string name) : IScalarSensor
    {
        public string Name => name;
        public int Channel => 1;
        public IReadOnlyList<TagDescriptor> Tags => Array.Empty<TagDescriptor>();
        public bool TryReadLatest(string tag, out Sample sample) { sample = default; return false; }
        public IObservable<Sample> Values { get; } = new Broadcast<Sample>();
    }

    // ── 中止收尾 ─────────────────────────────────────────────────

    [Fact]
    public async Task 主机不应答把自己的预算耗光_排在后面的泵照样立刻被停()
    {
        // 从前整条通道共用一个 10 s、设备挨个停：主机排第一，它不通就把预算耗光，后面的泵拿到的是过期令牌——泵停不下来
        await using var h = new Harness(200);
        h.Engine.SafeStopBudget = TimeSpan.FromMilliseconds(600);
        var ch = await h.ReactorChannelAsync(1);
        var dead = new Stub("R-DEAD", async ct => { await Task.Delay(Timeout.Infinite, ct); return Array.Empty<string>(); });
        var pump = new Stub("F1", ct =>
        {
            ct.ThrowIfCancellationRequested();              // 拿到过期令牌就停不了——这正是从前的毛病
            return ValueTask.FromResult<IReadOnlyList<string>?>(new[] { "泵 1 已停，本趟累计加入 12.40 mL" });
        });
        ch.Attach(dead, 0, false);
        ch.Attach(pump, 0, false);

        var run = h.Engine.StartChannel(1, Waiting(), "张三");
        await Task.Delay(200);
        var t0 = Environment.TickCount64;
        h.Engine.Runner(1)!.Abort("张三");
        await h.Engine.Runner(1)!.Completion;

        Assert.Equal(1, pump.Calls);
        Assert.Contains("F1 泵 1 已停，本趟累计加入 12.40 mL", SafeStopLines(run));
        Assert.Contains(SafeStopLines(run), t => t.Contains("R-DEAD") && t.Contains("停机失败") && t.Contains("没停完"));
        Assert.True(pump.StopCalledAtMs - t0 < 400, $"泵的停机不该等主机的预算，实际等了 {pump.StopCalledAtMs - t0} ms");
    }

    [Fact]
    public async Task 驱动连令牌都不理_到预算就不等了_照实记没有响应_别的设备不受影响()
    {
        await using var h = new Harness(200);
        h.Engine.SafeStopBudget = TimeSpan.FromMilliseconds(300);
        var ch = await h.ReactorChannelAsync(1);
        var never = new TaskCompletionSource<IReadOnlyList<string>?>();
        var stuck = new Stub("STUCK", _ => new ValueTask<IReadOnlyList<string>?>(never.Task));   // 卡死在串口里那种
        var ok = new Stub("P9", _ => ValueTask.FromResult<IReadOnlyList<string>?>(new[] { "加料泵已停" }));
        ch.Attach(stuck, 0, false);
        ch.Attach(ok, 0, false);

        var run = h.Engine.StartChannel(1, Waiting(), "张三");
        await Task.Delay(200);
        var sw = Stopwatch.StartNew();
        h.Engine.Runner(1)!.Abort("张三");
        await h.Engine.Runner(1)!.Completion;

        Assert.True(sw.ElapsedMilliseconds < 5000, $"收尾不该被卡死，用了 {sw.ElapsedMilliseconds} ms");
        Assert.Contains(SafeStopLines(run), t => t.Contains("STUCK") && t.Contains("没有响应"));
        Assert.Contains("P9 加料泵已停", SafeStopLines(run));
    }

    [Fact]
    public async Task 紧急程序_温控器不通耗光自己的预算_紧急转速照样发得出去()
    {
        // 从前 Tsafe 与 Rsafe 共用一个预算：温控器不通把它耗光，搅拌拿到过期令牌，紧急转速发不出去
        await using var h = new Harness(200);
        h.Engine.SafeStopBudget = TimeSpan.FromMilliseconds(400);
        var stir = new FakeStir();
        var ch = new Channel(1, "X", 0);
        ch.Attach(new Stub("T-DEAD", null, new HangingTemp()), 0, true);
        ch.Attach(new Stub("ST1", null, stir), 0, false);
        h.Engine.Attach(ch);

        var run = h.Engine.StartChannel(1, Waiting(), "张三");
        h.Engine.Safety.SetEmergencyPlan(1, new EmergencyPlan(20, 100));     // 启动会清上一炉的，启动之后再设
        await Task.Delay(200);
        h.Engine.Runner(1)!.Abort(null, "安全联锁", emergency: true);
        await h.Engine.Runner(1)!.Completion;

        Assert.Equal(100, stir.SetTo);
        Assert.Contains(SafeStopLines(run), t => t.Contains("紧急转速 100.0 rpm") && !t.Contains("失败"));
        Assert.Contains(SafeStopLines(run), t => t.Contains("安全温度 20.0 ℃") && t.Contains("失败") && t.Contains("400 ms 内设备没有响应"));
    }

    // ── 通道挂载：顺序确定、线程安全 ─────────────────────────────

    [Fact]
    public void 挂载按台面清单的序号排_跟谁先连上无关()
    {
        var a = new FakeSensor("Tr 探头");
        var b = new FakeSensor("pH 电极");
        var ch = new Channel(1, "R1", 0);

        ch.Attach(new Stub("PH1", null, b), 0, false, order: 2);     // pH 先连上
        ch.Attach(new Stub("TR1", null, a), 0, false, order: 1);     // Tr 后连上，但清单里排在前

        Assert.Equal(new[] { "TR1", "PH1" }, ch.Attachments.Select(x => x.Session.InstanceId));
        Assert.Same(a, ch.Capabilities.Get<IScalarSensor>());        // 「第一个 IScalarSensor」每次都是清单里靠前的那台
    }

    [Fact]
    public void 不给序号照旧按挂上来的先后_同一台重挂不重复()
    {
        var ch = new Channel(1, "R1", 0);
        var s1 = new Stub("A", null, new FakeSensor("a"));
        var s2 = new Stub("B", null, new FakeSensor("b"));
        ch.Attach(s1, 0, true);
        ch.Attach(s2, 0, false);
        ch.Attach(s1, 0, true);                                        // 重挂同一台

        Assert.Equal(2, ch.Attachments.Count);
        Assert.Equal(2, ch.Capabilities.All.Count);
        ch.DetachAll();
        Assert.Empty(ch.Attachments);
        Assert.Empty(ch.Capabilities.All);
    }

    [Fact]
    public async Task 一边挂一边读_读到的永远是完整的一份_不抛集合被修改()
    {
        var ch = new Channel(1, "R1", 0);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        var reader = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                foreach (var a in ch.Attachments) _ = a.Session.InstanceId;       // 从前这里是遍历一只正在被改的 List
                _ = ch.Capabilities.Get<IScalarSensor>();
                _ = ch.ResolveHandler("x");
            }
        });
        var writers = Enumerable.Range(0, 4).Select(i => Task.Run(() =>
        {
            var n = 0;
            while (!cts.IsCancellationRequested)
                ch.Attach(new Stub($"D{i}-{n++ % 5}", null, new FakeSensor("s")), 0, false, order: i);
        })).ToArray();

        await Task.WhenAll(writers.Append(reader));                     // 抛了就是没隔离住
    }

    // ── 串口事务不占公共线程池 ───────────────────────────────────

    [Fact]
    public async Task 串口一问一答在专用线程上做_不占线程池()
    {
        var slave = new FakeModbusSlave { Station = 1 };
        slave.Regs[0] = 42;
        slave.Open();
        var mb = new ModbusRtuClient(slave, 1);

        var r = await mb.ReadHoldingRegistersAsync(0, 1);
        await mb.WriteRegisterAsync(1, 7);

        Assert.Equal(42, r[0]);
        Assert.Equal(7, slave.Regs[1]);
        Assert.False(slave.AnyWriteOnPoolThread);       // 一台不通的设备每拍卡 2 s，占的是它自己的线程，不是大家的池
    }

    [Fact]
    public async Task 一台设备不应答_别的设备的指令不排队()
    {
        // 八台都不应答（每问卡满超时），同时给一台通着的设备发指令——通着的那台不该等它们
        var dead = Enumerable.Range(0, 8).Select(_ => { var d = new FakeModbusSlave { Station = 1, Mute = true }; d.Open(); return d; }).ToList();
        var live = new FakeModbusSlave { Station = 1 }; live.Open();
        var deadTasks = dead.Select(d => new ModbusRtuClient(d, 1, timeoutMs: 400) { TimeoutRetries = 2, RetryBackoffMs = 100 }
                                             .ReadHoldingRegistersAsync(0, 1)).ToList();
        await Task.Delay(50);                                            // 让它们都卡进去

        var sw = Stopwatch.StartNew();
        await new ModbusRtuClient(live, 1).WriteRegisterAsync(0x40, 0);  // 「关搅拌」那一帧
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 300, $"通着的设备的指令被拖了 {sw.ElapsedMilliseconds} ms");
        foreach (var t in deadTasks) await Assert.ThrowsAsync<TimeoutException>(() => t);
    }

    // ── 开会话分组：不同串口同时连 ───────────────────────────────

    [Fact]
    public void 一台设备用到哪些串口_从连接参数的串口字段里取_没填按缺省()
    {
        var host = new DualStationDriver();
        var ports = ConnectGroups.PortsOf(host, ParameterSet.Of((DualStationDriver.Fields.PortRd105, "COM7")));
        Assert.Contains("COM7", ports);
        Assert.Contains(DualStationDriver.Defaults.PortIo, ports);       // 没填的按缺省
        Assert.Contains(DualStationDriver.Defaults.PortChiller, ports);

        Assert.Equal(new[] { "COM14" }, ConnectGroups.PortsOf(new AqmdStirrerDriver(),
            ParameterSet.Of((AqmdStirrerDriver.FieldPort, "COM14"))));
    }

    [Fact]
    public void 不同串口各成一组同时连_共用一个口的归一组按清单先后()
    {
        // 现场台面：主机（COM7 / COM3 / COM10）、宇电 Tr（COM8）、旧 pH 也在 COM8、搅拌 A COM11、搅拌 B COM14、数字 pH COM12
        var items = new (string Id, string[] Ports)[]
        {
            ("R1", new[] { "COM7", "COM3", "COM10" }),
            ("TR1", new[] { "COM8" }),
            ("ST1", new[] { "COM11" }),
            ("PHold", new[] { "com8" }),                                 // 大小写不敏感：跟 TR1 共口
            ("ST2", new[] { "COM14" }),
            ("PH1", new[] { "COM12" }),
            ("X", Array.Empty<string>())                                  // 没有串口的设备各自一组
        };

        var groups = ConnectGroups.Group(items, x => x.Ports).Select(g => string.Join("+", g.Select(x => x.Id))).ToList();

        Assert.Equal(new[] { "R1", "TR1+PHold", "ST1", "ST2", "PH1", "X" }, groups);
    }

    [Fact]
    public void 两台设备隔着第三台共口_三台连成一组()
    {
        var items = new (string Id, string[] Ports)[]
        {
            ("A", new[] { "COM1" }), ("B", new[] { "COM2" }), ("C", new[] { "COM1", "COM2" })
        };
        var groups = ConnectGroups.Group(items, x => x.Ports);
        Assert.Single(groups);
        Assert.Equal(new[] { "A", "B", "C" }, groups[0].Select(x => x.Id));
    }

    // ── 搅拌开关对账用的「最后下发的目标」 ─────────────────────────

    [Fact]
    public async Task 搅拌驱动器报最后下发的目标_停机归零_面板开关据此对账()
    {
        var d = new FakeModbusSlave { Station = 1 };
        d.Regs[0x0000] = 0x0203; d.Regs[0x0001] = 0x0103;
        d.Regs[0x0062] = 50000; d.Regs[0x0064] = 50000; d.Regs[0x0066] = 20000; d.Regs[0x006A] = 700; d.Regs[0x006B] = 700;
        d.Regs[0x0073] = 4; d.Regs[0x0074] = 10; d.Regs[0x0075] = 1;
        var drv = new AqmdStirrerDriver { SerialFactory = _ => new SharedSerial(d, new SemaphoreSlim(1, 1), release: d.Close) };
        await using var s = await drv.OpenAsync(ParameterSet.Of((AqmdStirrerDriver.FieldPort, "COM11")),
            new DriverContext { InstanceId = "ST1", ChannelNumbers = new[] { 1 }, Log = (_, _) => { } }, CancellationToken.None);
        var stir = s.CapabilitiesOf(0).OfType<IStirrer>().Single();
        var status = Assert.IsAssignableFrom<IStirrerStatus>(stir);

        Assert.Equal(0, status.TargetRpm);
        await stir.SetSpeedAsync(300, CancellationToken.None);
        Assert.Equal(300, status.TargetRpm);             // 刚下发、实测还是 0 的那几拍，开关靠它不被打回「关」
        Assert.Equal(0, stir.CurrentRpm);
        await stir.StopAsync(CancellationToken.None);
        Assert.Equal(0, status.TargetRpm);
    }
}
