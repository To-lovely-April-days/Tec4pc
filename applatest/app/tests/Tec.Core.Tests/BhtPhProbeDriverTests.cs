using Tec.Driver.Abi;
using Tec.Drivers.DualStation;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 数字 pH 电极（BHT-DM100 一体式变送器）真机驱动的回归。全程走假 Modbus 从站——假从站的寄存器按
/// 2026-11-09 现场 COM12 那支读回的帧填（地址 2：温度 15.8 ℃、pH 4.62、手动温度 25.0、电位 136.6 mV、温度状态 0）。
/// </summary>
public sealed class BhtPhProbeDriverTests
{
    private static FakeModbusSlave MakeProbe(byte station = 2)
    {
        var d = new FakeModbusSlave { Station = station };
        d.Regs[0] = 158; d.Regs[1] = 462; d.Regs[2] = 250; d.Regs[3] = 1366; d.Regs[4] = 0; d.Regs[5] = 0;   // 现场回帧
        d.Regs[8] = station; d.Regs[9] = 9600; d.Regs[13] = 0; d.Regs[14] = 0; d.Regs[15] = 100; d.Regs[20] = 0; d.Regs[25] = 1;
        return d;
    }

    private static BhtPhProbeDriver Wire(FakeModbusSlave slave)
        => new() { SerialFactory = cn => new SharedSerial(slave, new SemaphoreSlim(1, 1), release: slave.Close,
                                                           portName: cn.Str(BhtPhProbeDriver.FieldPort, "COM12")) };

    private static ParameterSet Conn(int addr = 2)
        => ParameterSet.Of((BhtPhProbeDriver.FieldPort, "COM12"), (BhtPhProbeDriver.FieldAddr, (double)addr));

    private static DriverContext Ctx(int channel, List<(string, string)>? logs = null)
        => new()
        {
            InstanceId = $"PH{channel}",
            ChannelNumbers = new[] { channel },
            Config = new ParameterSet(),
            Simulated = false,
            TimeScale = 1,
            Clock = () => DateTimeOffset.Now,
            Log = logs is null ? (_, _) => { } : (l, t) => { lock (logs) logs.Add((l, t)); }
        };

    [Fact]
    public void 缺省连接参数照现场_COM12_地址2_9600_B工位是COM13()
    {
        var drv = new BhtPhProbeDriver();
        var cn = new ParameterSet().FillDefaults(drv.ConnectionSchema);
        Assert.Equal("COM12", cn.Str(BhtPhProbeDriver.FieldPort));
        Assert.Equal("COM13", BhtPhProbeDriver.DefaultPortB);
        Assert.Equal(2, cn.Int(BhtPhProbeDriver.FieldAddr));               // 说明书缺省值一栏写的 2，不是例子里的 1
        Assert.Equal(9600, cn.Num(BhtPhProbeDriver.FieldBaud));
        Assert.Equal(0, drv.Info.ChannelsPerDevice);                        // 绑到工位上，不自带通道
        Assert.True(drv.Info.HasSettings);                                  // 标定在设备参数面板里做
        Assert.Equal("phel", drv.Info.IconKey);                             // 台面上跟宇电那支电极插同一个 pH 口
    }

    [Fact]
    public async Task 测试连接_摆出pH电位温度零点斜率标准液和通讯参数()
    {
        var probe = MakeProbe();
        var probeResult = await Wire(probe).ProbeAsync(Conn(), CancellationToken.None);

        Assert.True(probeResult.Success);
        Assert.Contains("pH 4.62", probeResult.Message);
        Assert.Contains("136.6 mV", probeResult.Message);
        Assert.Contains("15.8 ℃", probeResult.Message);
        Assert.Contains("斜率 100 %", probeResult.Message);
        Assert.Contains("4.00 / 7.00 / 10.00", probeResult.Message);
        Assert.Contains("地址 2、9600、8N1", probeResult.Message);
        Assert.False(probe.IsOpen);                                          // 探测完让出串口
    }

    [Fact]
    public async Task 测试连接_地址配成1没应答_换地址2通了就说改成2()
    {
        var probe = MakeProbe(station: 2);
        var result = await Wire(probe).ProbeAsync(Conn(addr: 1), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("地址 1 无应答", result.Message);
        Assert.Contains("换地址 2 有应答", result.Message);
        Assert.Contains("改成 2", result.Message);
    }

    [Fact]
    public async Task 轮询_pH出样到所绑通道_并端出IScalarSensor_认领pH采集()
    {
        var probe = MakeProbe();
        await using var s = await Wire(probe).OpenAsync(Conn(), Ctx(2), CancellationToken.None);
        var got = new List<Sample>();
        using var sub = s.Samples.Subscribe(new Collect(x => { lock (got) got.Add(x); }));

        await ((BhtPhProbeSession)s).PollOnceAsync(CancellationToken.None);

        lock (got) Assert.Equal(4.62, got.Single(x => x is { Tag: "pH", Channel: 2 }).Value, 2);
        var sensor = s.CapabilitiesOf(0).OfType<IScalarSensor>().Single();
        Assert.True(sensor.TryReadLatest("pH", out var smp));
        Assert.Equal(4.62, smp.Value, 2);
        Assert.Equal(Quality.Good, smp.Quality);
        Assert.NotNull(s.Resolve(CommandSpecs.PhSample));
        Assert.Null(s.Resolve(CommandSpecs.Stir));
    }

    [Fact]
    public async Task 温度状态未接传感器_pH按Bad发_只说一次_恢复再记一笔()
    {
        var probe = MakeProbe();
        var logs = new List<(string, string)>();
        await using var s = await Wire(probe).OpenAsync(Conn(), Ctx(1, logs), CancellationToken.None);
        var ps = (BhtPhProbeSession)s;
        var got = new List<Sample>();
        using var sub = s.Samples.Subscribe(new Collect(x => { lock (got) got.Add(x); }));

        probe.Regs[5] = 2;
        await ps.PollOnceAsync(CancellationToken.None);
        await ps.PollOnceAsync(CancellationToken.None);
        lock (got) Assert.All(got, x => Assert.Equal(Quality.Bad, x.Quality));
        lock (logs) Assert.Single(logs.Where(l => l.Item2.Contains("未接温度传感器")));

        probe.Regs[5] = 0;
        await ps.PollOnceAsync(CancellationToken.None);
        lock (got) Assert.Equal(Quality.Good, got[^1].Quality);
        lock (logs) Assert.Contains(logs, l => l.Item2.Contains("温度状态恢复正常"));
    }

    [Fact]
    public async Task 参数面板_实时组读得到_标准液写了读回核对_实时组不可写()
    {
        var probe = MakeProbe();
        await using var s = await Wire(probe).OpenAsync(Conn(), Ctx(1), CancellationToken.None);
        var dev = (IDeviceSettings)s;

        var live = await dev.ReadAsync(BhtPhProbeSession.GroupLive, CancellationToken.None);
        Assert.Equal(4.62, live.Num(BhtPhProbeSession.KPh), 2);
        Assert.Equal(136.6, live.Num(BhtPhProbeSession.KMv), 1);
        Assert.Equal("正常", live.Str(BhtPhProbeSession.KTempStatus));

        var notes = await dev.WriteAsync(BhtPhProbeSession.GroupCal,
            ParameterSet.Of((BhtPhProbeSession.KBuffer, BhtPhProbeSession.BufferOptions[1])), CancellationToken.None);
        Assert.Equal(1, probe.Regs[20]);
        Assert.Contains(notes, n => n.Contains("4.00 / 6.86 / 9.18"));

        var ro = await dev.WriteAsync(BhtPhProbeSession.GroupLive, ParameterSet.Of((BhtPhProbeSession.KPh, 7d)), CancellationToken.None);
        Assert.Contains(ro, n => n.Contains("只读"));
    }

    [Fact]
    public async Task 标定_写13读回结果码_成功与斜率超范围都说人话()
    {
        var probe = MakeProbe();
        var logs = new List<(string, string)>();
        await using var s = await Wire(probe).OpenAsync(Conn(), Ctx(1, logs), CancellationToken.None);
        var dev = (IDeviceSettings)s;
        // 变送器收到标定命令后把结果码放回寄存器 13（说明书「校准反馈错误代码列表」）；这里先让它答「成功」并把零电位更新掉
        probe.OnWrite = (addr, _) => { if (addr == 13) { probe.Regs[13] = 0; probe.Regs[14] = unchecked((ushort)(short)-120); } };

        var ok = await dev.RunAsync(BhtPhProbeSession.ActCalZero, CancellationToken.None);
        Assert.Contains("写寄存器 13=1", probe.Requests);
        Assert.Contains("零电位标定：标定成功", ok);
        Assert.Contains("0.00 → -1.20 mV", ok);

        probe.OnWrite = (addr, _) => { if (addr == 13) probe.Regs[13] = 0x20; };   // 这次答「斜率超出范围」
        var bad = await dev.RunAsync(BhtPhProbeSession.ActCalLow, CancellationToken.None);
        Assert.Contains("写寄存器 13=2", probe.Requests);
        Assert.Contains("斜率超出范围", bad);
        lock (logs) Assert.Contains(logs, l => l.Item1 == "error" && l.Item2.Contains("斜率超出范围"));
    }

    [Fact]
    public async Task 标定_电极温度不在5到50度_不写直接说没标()
    {
        var probe = MakeProbe();
        probe.Regs[0] = 620;                                                  // 62.0 ℃
        await using var s = await Wire(probe).OpenAsync(Conn(), Ctx(1), CancellationToken.None);

        var msg = await ((IDeviceSettings)s).RunAsync(BhtPhProbeSession.ActCalHigh, CancellationToken.None);

        Assert.Contains("没标", msg);
        Assert.DoesNotContain(probe.Requests, r => r.StartsWith("写寄存器 13=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 串口中途死了_读失败立刻关掉重开_口子回来就接着出数()
    {
        var probe = MakeProbe();
        var logs = new List<(string, string)>();
        await using var s = await Wire(probe).OpenAsync(Conn(), Ctx(1, logs), CancellationToken.None);
        var ps = (BhtPhProbeSession)s;
        var got = new List<Sample>();
        using var sub = s.Samples.Subscribe(new Collect(x => { lock (got) got.Add(x); }));
        await ps.PollOnceAsync(CancellationToken.None);

        probe.Dead = true;
        await ps.PollOnceAsync(CancellationToken.None);
        Assert.Equal(1, ps.LinkReopens);
        Assert.False(probe.Dead);

        await ps.PollOnceAsync(CancellationToken.None);
        lock (got) Assert.Equal(2, got.Count);
        lock (logs) Assert.Contains(logs, l => l.Item2.Contains("pH 变送器恢复"));
    }

    [Fact]
    public async Task 安全停机_电极没有可停的_空表()
    {
        var probe = MakeProbe();
        await using var s = await Wire(probe).OpenAsync(Conn(), Ctx(1), CancellationToken.None);
        var did = await s.SafeStopAsync(0, CancellationToken.None);
        Assert.NotNull(did);
        Assert.Empty(did!);
    }

    private sealed class Collect(Action<Sample> onNext) : IObserver<Sample>
    {
        public void OnNext(Sample value) => onNext(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
