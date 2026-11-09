using Tec.Core.Benches;
using Tec.Driver.Abi;
using Tec.Drivers.DualStation;
using Tec.Drivers.Htlab;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 两液一气进料系统（霍桐 HTLAB）真机驱动的回归。全程走假 Modbus 从站，寄存器按厂家插件源码注释里的表填：
/// Float 两寄存器大端、Int 一寄存器；MFC 开停 0 = 运行（跟泵反着）。
/// </summary>
public sealed class FeedSystemDriverTests
{
    private static void SetF(FakeModbusSlave d, ushort reg, double v)
    {
        var (hi, lo) = FeedRegisters.RegsOf((float)v);
        d.Regs[reg] = hi; d.Regs[reg + 1] = lo;
    }

    private static double GetF(FakeModbusSlave d, ushort reg)
        => FeedRegisters.FloatOf(d.Regs.TryGetValue(reg, out var h) ? h : (ushort)0, d.Regs.TryGetValue(reg + 1, out var l) ? l : (ushort)0);

    /// <summary>装成一台停着的进料系统：MFC 量程 0~10，泵压力报警 0~1 MPa，没有报警。</summary>
    private static FakeModbusSlave MakeFeed()
    {
        var d = new FakeModbusSlave { Station = 1 };
        for (ushort r = 0; r < 49; r++) d.Regs[r] = 0;
        SetF(d, FeedRegisters.MfcRangeHigh, 10); SetF(d, FeedRegisters.MfcCorrection, 1); SetF(d, FeedRegisters.MfcDpt, 1);
        SetF(d, FeedRegisters.Pump1PressureHigh, 1); SetF(d, FeedRegisters.Pump2PressureHigh, 1);
        d.Regs[FeedRegisters.MfcOnOff] = 1;                // MFC 停着 = 1
        return d;
    }

    private static FeedSystemDriver Wire(FakeModbusSlave slave)
        => new() { SerialFactory = cn => new SharedSerial(slave, new SemaphoreSlim(1, 1), release: slave.Close,
                                                           portName: cn.Str(FeedSystemDriver.FieldPort, "COM15")) };

    private static ParameterSet Conn() => ParameterSet.Of((FeedSystemDriver.FieldPort, "COM15"));

    private static DriverContext Ctx(int channel, ParameterSet? config = null, List<(string, string)>? logs = null)
        => new()
        {
            InstanceId = "F1", ChannelNumbers = new[] { channel }, Config = config ?? new ParameterSet(),
            Simulated = false, TimeScale = 1, Clock = () => DateTimeOffset.Now,
            Log = logs is null ? (_, _) => { } : (l, t) => { lock (logs) logs.Add((l, t)); }
        };

    [Fact]
    public void 大端浮点来回换算_跟厂家例子一致()
    {
        var (hi, lo) = FeedRegisters.RegsOf(2.3f);
        Assert.Equal(2.3f, FeedRegisters.FloatOf(hi, lo));
        Assert.Equal(0x4013, hi);                              // 2.3f = 0x40133333，高字在前
        Assert.Equal(0x3333, lo);
    }

    [Fact]
    public async Task 测试连接_把MFC两台泵报警量程全摆出来()
    {
        var d = MakeFeed();
        SetF(d, FeedRegisters.Pump1Setpoint, 2.3); SetF(d, FeedRegisters.Pump1Flow, 2.29); SetF(d, FeedRegisters.Pump1Total, 125.4); SetF(d, FeedRegisters.Pump1Pressure, 0.31);
        d.Regs[FeedRegisters.Pump1OnOff] = 1;

        var r = await Wire(d).ProbeAsync(Conn(), CancellationToken.None);

        Assert.True(r.Success);
        Assert.Contains("泵 1 设 2.30 瞬时 2.29 mL/min 累计 125.4 mL 压力 0.31 MPa 运行", r.Message);
        Assert.Contains("MFC 设 0.00", r.Message);
        Assert.Contains("停着", r.Message);
        Assert.Contains("无报警", r.Message);
        Assert.Contains("MFC 量程 0~10 L/min", r.Message);
        Assert.False(d.IsOpen);
    }

    [Fact]
    public async Task 挂到通道上的是配方加料泵_缺省泵1_可配成泵2_两台都能按号取()
    {
        var d = MakeFeed();
        await using var s1 = await Wire(d).OpenAsync(Conn(), Ctx(1), CancellationToken.None);
        var dosing = s1.CapabilitiesOf(0).OfType<IDosing>().Single();
        Assert.Equal(1, ((FeedSystemSession)s1).RecipePump);
        Assert.NotNull(dosing.Calibration);                    // 计量泵自带流量闭环，校验器不该拦
        Assert.Equal(50, dosing.Limits.Max);
        Assert.Equal(2000, dosing.Limits.MaxVolume);

        await using var s2 = await Wire(MakeFeed()).OpenAsync(Conn(), Ctx(1, ParameterSet.Of((FeedSystemDriver.FieldRecipePump, FeedSystemDriver.Pump2))), CancellationToken.None);
        Assert.Equal(2, ((FeedSystemSession)s2).RecipePump);
        Assert.NotSame(((FeedSystemSession)s2).PumpOf(1), ((FeedSystemSession)s2).PumpOf(2));
    }

    [Fact]
    public async Task 定量加料_写设定流量和启动_累计量到了泵自己停()
    {
        var d = MakeFeed();
        var logs = new List<(string, string)>();
        await using var s = await Wire(d).OpenAsync(Conn(), Ctx(1, logs: logs), CancellationToken.None);
        var fs = (FeedSystemSession)s;
        var pump = s.CapabilitiesOf(0).OfType<IDosing>().Single();
        SetF(d, FeedRegisters.Pump1Total, 100);                 // 起点累计 100 mL
        await fs.PollOnceAsync(CancellationToken.None);

        await pump.DoseAsync(new DoseRequest(10, 2.5) { Material = "硝酸" }, CancellationToken.None);

        Assert.Equal(2.5, GetF(d, FeedRegisters.Pump1Setpoint), 3);      // 0x10 写两个寄存器
        Assert.Equal(1, d.Regs[FeedRegisters.Pump1OnOff]);                 // 泵 1 启动 = 1
        Assert.Contains("写多寄存器 6+2", d.Requests);

        SetF(d, FeedRegisters.Pump1Total, 105); SetF(d, FeedRegisters.Pump1Flow, 2.5); d.Regs[FeedRegisters.Pump1OnOff] = 1;
        await fs.PollOnceAsync(CancellationToken.None);
        Assert.Equal(1, d.Regs[FeedRegisters.Pump1OnOff]);                 // 还差 5 mL，不停

        SetF(d, FeedRegisters.Pump1Total, 110.2);
        await fs.PollOnceAsync(CancellationToken.None);
        Assert.Equal(0, d.Regs[FeedRegisters.Pump1OnOff]);                 // 送够 10 mL，泵自己停
        Assert.Equal(110.2, pump.TotalVolume, 1);
        lock (logs) Assert.Contains(logs, l => l.Item2.Contains("送够 10.2 mL"));
    }

    [Fact]
    public async Task 轮询_配方泵镜像到flow和vol_两台泵与MFC各自出样()
    {
        var d = MakeFeed();
        await using var s = await Wire(d).OpenAsync(Conn(), Ctx(2), CancellationToken.None);
        var got = new List<Sample>();
        using var sub = s.Samples.Subscribe(new Collect(x => { lock (got) got.Add(x); }));
        SetF(d, FeedRegisters.Pump1Flow, 1.5); SetF(d, FeedRegisters.Pump1Total, 20);
        SetF(d, FeedRegisters.Pump2Flow, 0.7); SetF(d, FeedRegisters.MfcFlow, 3.2); SetF(d, FeedRegisters.Pump2Pressure, 0.45);

        await ((FeedSystemSession)s).PollOnceAsync(CancellationToken.None);

        lock (got)
        {
            Assert.Equal(1.5, got.Single(x => x is { Tag: "flow", Channel: 2 }).Value, 3);
            Assert.Equal(20, got.Single(x => x is { Tag: "vol", Channel: 2 }).Value, 3);
            Assert.Equal(0.7, got.Single(x => x.Tag == "p2flow").Value, 3);
            Assert.Equal(3.2, got.Single(x => x.Tag == "gasFlow").Value, 3);
            Assert.Equal(0.45, got.Single(x => x.Tag == "p2P").Value, 3);
            Assert.Equal(0, got.Single(x => x.Tag == "feedAlarm").Value);
        }
    }

    [Fact]
    public async Task 报警位亮_会话标故障日志点名_复位后恢复()
    {
        var d = MakeFeed();
        var logs = new List<(string, string)>();
        await using var s = await Wire(d).OpenAsync(Conn(), Ctx(1, logs: logs), CancellationToken.None);
        await s.StartAsync(CancellationToken.None);
        var fs = (FeedSystemSession)s;

        d.Regs[FeedRegisters.Pump1PressureAlarm] = 1; d.Regs[FeedRegisters.AlarmStatus] = 1;
        await fs.PollOnceAsync(CancellationToken.None);
        Assert.Equal(DeviceState.Faulted, s.State);
        lock (logs) Assert.Contains(logs, l => l.Item1 == "error" && l.Item2.Contains("泵 1 压力上限报警"));

        // 复位：假从站收到 46 写 1 就把报警清掉
        d.OnWrite = (addr, v) => { if (addr == FeedRegisters.AlarmReset && v == 1) { d.Regs[FeedRegisters.Pump1PressureAlarm] = 0; d.Regs[FeedRegisters.AlarmStatus] = 0; } };
        var said = await ((IDeviceSettings)s).RunAsync(FeedSystemSession.ActReset, CancellationToken.None);
        Assert.Contains("报警清了", said);
        Assert.Equal(DeviceState.Ready, s.State);
    }

    [Fact]
    public async Task 参数面板_写MFC设定流量读回核对_MFC运行写0停止写1_泵运行写1()
    {
        var d = MakeFeed();
        await using var s = await Wire(d).OpenAsync(Conn(), Ctx(1), CancellationToken.None);
        var dev = (IDeviceSettings)s;

        var notes = await dev.WriteAsync(FeedSystemSession.GroupControl, ParameterSet.Of(
            (FeedSystemSession.KMfcSet, 2.5), (FeedSystemSession.KMfcRun, FeedSystemSession.RunOn), (FeedSystemSession.KP2Run, FeedSystemSession.RunOn)), CancellationToken.None);

        Assert.Equal(2.5, GetF(d, FeedRegisters.MfcSetpoint), 3);
        Assert.Equal(0, d.Regs[FeedRegisters.MfcOnOff]);                  // MFC：0 = 运行
        Assert.Equal(1, d.Regs[FeedRegisters.Pump2OnOff]);                // 泵：1 = 运行
        Assert.Contains(notes, n => n.Contains("MFC 设定流量 写为 2.5") && n.Contains("读回一致"));
        Assert.Contains(notes, n => n.Contains("MFC 已运行"));
        Assert.Contains(notes, n => n.Contains("泵 2 已运行"));

        var live = await dev.ReadAsync(FeedSystemSession.GroupLive, CancellationToken.None);
        Assert.Equal(FeedSystemSession.RunOn, live.Str(FeedSystemSession.KMfcRun));
        Assert.Equal(FeedSystemSession.RunOn, live.Str(FeedSystemSession.KP2Run));
    }

    [Fact]
    public async Task 配方加料步骤_按加料泵2点名_一次加入按流量上限_送够报实加()
    {
        var d = MakeFeed();
        await using var s = await Wire(d).OpenAsync(Conn(), Ctx(1), CancellationToken.None);
        var fs = (FeedSystemSession)s;
        var handler = s.Resolve(CommandSpecs.Dose);
        Assert.NotNull(handler);
        var caps = new CapabilitySet(); caps.Add(s.CapabilitiesOf(0));
        var ctx = new CommandContext { Channel = 1, Capabilities = caps, Now = () => DateTimeOffset.Now, TimeScale = 1000 };

        // 后台按拍推进假泵：每 50 ms 累计量加 3 mL
        using var cts = new CancellationTokenSource();
        var feeder = Task.Run(async () =>
        {
            double total = 0;
            while (!cts.IsCancellationRequested)
            {
                await Task.Delay(50);
                if (d.Regs[FeedRegisters.Pump2OnOff] == 1) { total += 3; SetF(d, FeedRegisters.Pump2Total, total); }
                await fs.PollOnceAsync(CancellationToken.None);
            }
        });

        var outcome = await handler!.ExecuteAsync(ctx, ParameterSet.Of(("pump", "加料泵 2"), ("liq", "碱液"), ("vol", 9d), ("task", "一次加入")), CancellationToken.None);
        cts.Cancel();
        try { await feeder; } catch { }

        Assert.Equal(EndReason.QuantityDelivered, outcome.Reason);
        Assert.Equal(50, GetF(d, FeedRegisters.Pump2Setpoint), 2);        // 一次加入 = 泵流量上限
        Assert.Equal(0, d.Regs[FeedRegisters.Pump2OnOff]);                 // 送够停了
        Assert.Contains("泵 2 实加", outcome.Note);
        Assert.Equal(0, d.Regs[FeedRegisters.Pump1OnOff]);                 // 泵 1 没被动
    }

    [Fact]
    public async Task 安全停机_两台泵都停_气路按配置停_照实说累计()
    {
        var d = MakeFeed();
        await using var s = await Wire(d).OpenAsync(Conn(), Ctx(1), CancellationToken.None);
        var fs = (FeedSystemSession)s;
        d.Regs[FeedRegisters.Pump1OnOff] = 1; d.Regs[FeedRegisters.MfcOnOff] = 0;   // 泵 1 和 MFC 在跑
        SetF(d, FeedRegisters.Pump1Total, 33.3);
        await fs.PollOnceAsync(CancellationToken.None);

        var did = await s.SafeStopAsync(0, CancellationToken.None);

        Assert.Equal(0, d.Regs[FeedRegisters.Pump1OnOff]);
        Assert.Equal(1, d.Regs[FeedRegisters.MfcOnOff]);                  // 缺省勾了「安全停机也停气路」
        Assert.Contains(did!, l => l.Contains("泵 1") && l.Contains("已停"));
        Assert.Contains(did!, l => l.Contains("泵 2 本来就停着"));
        Assert.Contains(did!, l => l.Contains("MFC 气路已停"));
    }

    [Fact]
    public async Task 真断开停泵停气_台面重建重开不动()
    {
        var d = MakeFeed();
        d.Regs[FeedRegisters.Pump2OnOff] = 1;
        var s = await Wire(d).OpenAsync(Conn(), Ctx(1), CancellationToken.None);
        ((IReconnectAwareSession)s).Reconnecting = true;
        await s.DisposeAsync();
        Assert.Equal(1, d.Regs[FeedRegisters.Pump2OnOff]);                 // 重开：泵照旧跑

        var d2 = MakeFeed();
        d2.Regs[FeedRegisters.Pump2OnOff] = 1; d2.Regs[FeedRegisters.MfcOnOff] = 0;
        var s2 = await Wire(d2).OpenAsync(Conn(), Ctx(1), CancellationToken.None);
        await s2.DisposeAsync();
        Assert.Equal(0, d2.Regs[FeedRegisters.Pump2OnOff]);                // 真断开：停泵
        Assert.Equal(1, d2.Regs[FeedRegisters.MfcOnOff]);                  // 停气
    }

    private sealed class Collect(Action<Sample> onNext) : IObserver<Sample>
    {
        public void OnNext(Sample value) => onNext(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
