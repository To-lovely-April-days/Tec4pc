using Tec.Driver.Abi;
using Tec.Drivers.DualStation;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 真机探头设备（宇电 J7 = Tr、J4 = pH）。串口在探头自己的属性里（用户定的），
/// 会话轮询模块上自己那一路、把读数发到所绑通道。盯：出数与映射、接反拒开、
/// 断线挂 Bad、pH 能力与指令认领、两支探头共一台模块/两台模块共一条总线。
/// </summary>
public sealed class YudianProbeDriverTests
{
    /// <summary>装成一台 J7：CH1/CH2 都开、都是 Pt100（一位小数）。</summary>
    private static FakeModbusSlave MakeJ7(byte station = 1)
    {
        var dev = new FakeModbusSlave { Station = station };
        dev.Regs[384] = 1; dev.Regs[385] = 2;
        dev.Regs[2048] = 21; dev.Regs[2049] = 21;
        dev.Regs[2128] = 1;
        return dev;
    }

    private static FakeModbusSlave MakeJ4(byte station = 1)
    {
        var dev = new FakeModbusSlave { Station = station };
        dev.Regs[384] = 1; dev.Regs[385] = 2;
        dev.Regs[2048] = 51; dev.Regs[2049] = 51;
        dev.Regs[2052] = 0; dev.Regs[2056] = 1400;      // 0.00~14.00
        dev.Regs[2053] = 0; dev.Regs[2057] = 1400;
        dev.Regs[2128] = 2;
        return dev;
    }

    private static DriverContext Ctx(int channel, double moduleCh = 1)
        => Ctx(new[] { channel }, ParameterSet.Of((YudianProbeDriverBase.FieldModuleCh, moduleCh)));

    /// <summary>配置一项不填——「宇电通道」走缺省（跟工位走）。</summary>
    private static DriverContext CtxFollow(params int[] channels) => Ctx(channels, new ParameterSet());

    private static DriverContext Ctx(int[] channels, ParameterSet config) => new()
    {
        InstanceId = $"PRB{channels[0]}",
        ChannelNumbers = channels,
        Config = config,
        Simulated = false,
        TimeScale = 1,
        Clock = () => DateTimeOffset.Now,
        Log = (_, _) => { }
    };

    /// <summary>把驱动的串口池换成一台假从站（或假总线）。</summary>
    private static T Wire<T>(T drv, TecControl.Core.Comm.ISerialTransport transport,
                             SemaphoreSlim? sharedLock = null) where T : YudianProbeDriverBase
    {
        var busLock = sharedLock ?? new SemaphoreSlim(1, 1);
        drv.SerialFactory = _ => new SharedSerial(transport, busLock);
        return drv;
    }

    [Fact]
    public async Task Tr探头出数到所绑通道_并端出IScalarSensor()
    {
        var j7 = MakeJ7();
        j7.Regs[1536] = 250;                             // CH1 = 25.0 ℃
        var drv = Wire(new YudianTrProbeDriver(), j7);

        await using var s = await drv.OpenAsync(new ParameterSet(), Ctx(channel: 3), CancellationToken.None);
        var got = new List<Sample>();
        using var sub = s.Samples.Subscribe(new Collect(x => { lock (got) got.Add(x); }));
        await ((YudianProbeSession)s).PollOnceAsync(CancellationToken.None);

        lock (got) Assert.Equal(25.0, got.Single(x => x is { Tag: "Tr", Channel: 3 }).Value, 2);
        var sensor = s.CapabilitiesOf(0).OfType<IScalarSensor>().Single();
        Assert.True(sensor.TryReadLatest("Tr", out var smp));
        Assert.Equal(25.0, smp.Value, 2);
    }

    [Fact]
    public async Task 宇电通道配置项_接第2路就读第2路()
    {
        var j7 = MakeJ7();
        j7.Regs[1537] = 300;                             // CH2 = 30.0 ℃
        var drv = Wire(new YudianTrProbeDriver(), j7);

        await using var s = await drv.OpenAsync(new ParameterSet(), Ctx(channel: 1, moduleCh: 2), CancellationToken.None);
        var got = new List<Sample>();
        using var sub = s.Samples.Subscribe(new Collect(x => { lock (got) got.Add(x); }));
        await ((YudianProbeSession)s).PollOnceAsync(CancellationToken.None);

        lock (got) Assert.Equal(30.0, got.Single(x => x.Tag == "Tr").Value, 2);
    }

    // ── 「宇电通道」缺省跟工位走（用户定的缺省要照现场那台机器：A→CH1、B→CH2） ──

    [Fact]
    public void 宇电通道下拉_缺省是跟工位走_老台面存的数字照样认()
    {
        var f = new YudianTrProbeDriver().ConfigSchema.Find(YudianProbeDriverBase.FieldModuleCh)!;
        Assert.Equal(YudianProbeDriverBase.ChFollowWell, f.Default);
        Assert.Equal(new[] { "跟工位走", "1", "2", "3", "4" }, f.Choices);

        var wellB = new[] { 2 };
        Assert.Equal(2, YudianProbeDriverBase.ResolveModuleChannel(new ParameterSet(), wellB));          // 没填 = 跟工位走
        Assert.Equal(2, YudianProbeDriverBase.ResolveModuleChannel(
            ParameterSet.Of((YudianProbeDriverBase.FieldModuleCh, "跟工位走")), wellB));
        Assert.Equal(4, YudianProbeDriverBase.ResolveModuleChannel(
            ParameterSet.Of((YudianProbeDriverBase.FieldModuleCh, "4")), wellB));                        // 手选压过工位
        Assert.Equal(3, YudianProbeDriverBase.ResolveModuleChannel(
            ParameterSet.Of((YudianProbeDriverBase.FieldModuleCh, 3d)), wellB));                         // 老台面里存的是数值
        Assert.Equal(1, YudianProbeDriverBase.ResolveModuleChannel(
            ParameterSet.Of((YudianProbeDriverBase.FieldModuleCh, "CH1")), wellB));
    }

    [Fact]
    public async Task 宇电通道没填_插在工位B就读CH2()
    {
        var j7 = MakeJ7();
        j7.Regs[1536] = 250;                             // CH1 = 25.0（工位 A 那支）
        j7.Regs[1537] = 300;                             // CH2 = 30.0（工位 B 那支）
        var drv = Wire(new YudianTrProbeDriver(), j7);

        await using var s = await drv.OpenAsync(new ParameterSet(), CtxFollow(2), CancellationToken.None);
        var got = new List<Sample>();
        using var sub = s.Samples.Subscribe(new Collect(x => { lock (got) got.Add(x); }));
        await ((YudianProbeSession)s).PollOnceAsync(CancellationToken.None);

        lock (got) Assert.Equal(30.0, got.Single(x => x is { Tag: "Tr", Channel: 2 }).Value, 2);
    }

    [Fact]
    public async Task 跟工位走_绑在第5路或绑了两路_如实拒绝不猜()
    {
        var drv = Wire(new YudianTrProbeDriver(), MakeJ7());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => drv.OpenAsync(new ParameterSet(), CtxFollow(5), CancellationToken.None));
        Assert.Contains("通道 5", ex.Message);
        Assert.Contains("手选 1~4", ex.Message);

        var ex2 = await Assert.ThrowsAsync<InvalidOperationException>(
            () => drv.OpenAsync(new ParameterSet(), CtxFollow(1, 2), CancellationToken.None));
        Assert.Contains("绑了 2 个通道", ex2.Message);

        var ex3 = Assert.Throws<InvalidOperationException>(() => YudianProbeDriverBase.ResolveModuleChannel(
            ParameterSet.Of((YudianProbeDriverBase.FieldModuleCh, "7")), new[] { 1 }));
        Assert.Contains("只认 1~4", ex3.Message);
    }

    [Fact]
    public async Task 选的那一路在模块上关着_报错点名开着的是哪几路()
    {
        var drv = Wire(new YudianTrProbeDriver(), MakeJ7());       // CH1/CH2 开，CH3/CH4 关

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => drv.OpenAsync(new ParameterSet(), Ctx(channel: 1, moduleCh: 3), CancellationToken.None));
        Assert.Contains("CH3 是关闭的", ex.Message);
        Assert.Contains("开着的是 CH1、CH2", ex.Message);
    }

    [Fact]
    public async Task 测试连接_把四路的读数都摆出来()
    {
        var j7 = MakeJ7();
        j7.Regs[1536] = 253;                             // CH1 = 25.3
        j7.Regs[1537] = 8100;                            // CH2 断线残值
        j7.Regs[1664] = 0x0001;                          // CH2 oral（偶数通道在低字节）
        var drv = Wire(new YudianTrProbeDriver(), j7);

        var r = await drv.ProbeAsync(new ParameterSet(), CancellationToken.None);

        Assert.True(r.Success);
        Assert.Contains("CH1 25.3 ℃、CH2 断线/超量程、CH3 关、CH4 关", r.Message);

        // pH 表按 dPt 的小数位显示，没有单位
        var j4 = MakeJ4();
        j4.Regs[1536] = 700; j4.Regs[1537] = 412;
        var ph = await Wire(new YudianPhProbeDriver(), j4).ProbeAsync(new ParameterSet(), CancellationToken.None);
        Assert.Contains("CH1 7.00、CH2 4.12、CH3 关、CH4 关", ph.Message);
    }

    [Fact]
    public async Task 温度探头的口子插了pH表_开机直接拒绝()
    {
        var j4 = MakeJ4();                               // 线性电流——对 Tr 探头就是接反
        var drv = Wire(new YudianTrProbeDriver(), j4);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => drv.OpenAsync(new ParameterSet(), Ctx(1), CancellationToken.None));
        Assert.Contains("J4", ex.Message);
    }

    [Fact]
    public async Task 断线那一拍挂Bad质量位()
    {
        var j7 = MakeJ7();
        j7.Regs[1536] = 8100;                            // 残值
        j7.Regs[1664] = 0x0100;                          // CH1 oral
        var drv = Wire(new YudianTrProbeDriver(), j7);

        await using var s = await drv.OpenAsync(new ParameterSet(), Ctx(1), CancellationToken.None);
        var got = new List<Sample>();
        using var sub = s.Samples.Subscribe(new Collect(x => { lock (got) got.Add(x); }));
        await ((YudianProbeSession)s).PollOnceAsync(CancellationToken.None);

        lock (got) Assert.Equal(Quality.Bad, got.Single(x => x.Tag == "Tr").Quality);
    }

    [Fact]
    public async Task pH电极出数_认领pH采集指令()
    {
        var j4 = MakeJ4();
        j4.Regs[1536] = 700;                             // pH 7.00
        var drv = Wire(new YudianPhProbeDriver(), j4);

        await using var s = await drv.OpenAsync(new ParameterSet(), Ctx(channel: 2), CancellationToken.None);
        var got = new List<Sample>();
        using var sub = s.Samples.Subscribe(new Collect(x => { lock (got) got.Add(x); }));
        await ((YudianProbeSession)s).PollOnceAsync(CancellationToken.None);

        lock (got) Assert.Equal(7.0, got.Single(x => x is { Tag: "pH", Channel: 2 }).Value, 2);
        Assert.NotNull(s.Resolve(CommandSpecs.PhSample));
        var sensor = s.CapabilitiesOf(0).OfType<IScalarSensor>().Single();
        Assert.True(sensor.TryReadLatest("pH", out var smp));
        Assert.Equal(7.0, smp.Value, 2);
    }

    [Fact]
    public async Task 两支Tr探头共一台模块_同口同地址各读各路()
    {
        // 一台 J7 伺候两个工位：两支探头填同一个口 + 同一个地址，「宇电通道」各选各的
        var j7 = MakeJ7();
        j7.Regs[1536] = 250;                             // CH1 = 25.0（工位 A）
        j7.Regs[1537] = 300;                             // CH2 = 30.0（工位 B）
        var shared = new SemaphoreSlim(1, 1);
        var da = Wire(new YudianTrProbeDriver(), j7, shared);
        var db = Wire(new YudianTrProbeDriver(), j7, shared);

        await using var sa = await da.OpenAsync(new ParameterSet(), Ctx(channel: 1, moduleCh: 1), CancellationToken.None);
        await using var sb = await db.OpenAsync(new ParameterSet(), Ctx(channel: 2, moduleCh: 2), CancellationToken.None);

        var got = new List<Sample>();
        using var s1 = sa.Samples.Subscribe(new Collect(x => { lock (got) got.Add(x); }));
        using var s2 = sb.Samples.Subscribe(new Collect(x => { lock (got) got.Add(x); }));
        await ((YudianProbeSession)sa).PollOnceAsync(CancellationToken.None);
        await ((YudianProbeSession)sb).PollOnceAsync(CancellationToken.None);

        lock (got)
        {
            Assert.Equal(25.0, got.Single(x => x is { Tag: "Tr", Channel: 1 }).Value, 2);
            Assert.Equal(30.0, got.Single(x => x is { Tag: "Tr", Channel: 2 }).Value, 2);
        }
    }

    [Fact]
    public async Task 两台模块共一条总线_按地址各答各的()
    {
        // 导轨拼接（手册 §3.3）：J7 站号 1、J4 站号 2 挂在同一条 485 上
        var j7 = MakeJ7(station: 1); j7.Regs[1536] = 250;
        var j4 = MakeJ4(station: 2); j4.Regs[1536] = 700;
        var bus = new FakeModbusBus(j7, j4);
        var shared = new SemaphoreSlim(1, 1);
        var dTr = Wire(new YudianTrProbeDriver(), bus, shared);
        var dPh = Wire(new YudianPhProbeDriver(), bus, shared);

        var cnTr = ParameterSet.Of((YudianProbeDriverBase.FieldAddr, 1d));
        var cnPh = ParameterSet.Of((YudianProbeDriverBase.FieldAddr, 2d));
        await using var sTr = await dTr.OpenAsync(cnTr, Ctx(1), CancellationToken.None);
        await using var sPh = await dPh.OpenAsync(cnPh, Ctx(1), CancellationToken.None);

        var got = new List<Sample>();
        using var s1 = sTr.Samples.Subscribe(new Collect(x => { lock (got) got.Add(x); }));
        using var s2 = sPh.Samples.Subscribe(new Collect(x => { lock (got) got.Add(x); }));
        await ((YudianProbeSession)sTr).PollOnceAsync(CancellationToken.None);
        await ((YudianProbeSession)sPh).PollOnceAsync(CancellationToken.None);

        lock (got)
        {
            Assert.Equal(25.0, got.Single(x => x.Tag == "Tr").Value, 2);
            Assert.Equal(7.0, got.Single(x => x.Tag == "pH").Value, 2);
        }
    }

    [Fact]
    public async Task 模块不应答_探测失败说人话()
    {
        var j7 = MakeJ7();
        j7.Mute = true;
        var drv = Wire(new YudianTrProbeDriver(), j7);
        var probe = await drv.ProbeAsync(new ParameterSet(), CancellationToken.None);
        Assert.False(probe.Success);
        Assert.False(string.IsNullOrWhiteSpace(probe.Message));
    }

    [Fact]
    public void 串口池_同口合并_异波特率拒绝_归还后关闭()
    {
        var a = SerialPortPool.Rent("COMPOOLTEST", 19200);
        var b = SerialPortPool.Rent("COMPOOLTEST", 19200);
        Assert.Same(a.Transport, b.Transport);           // 同口合并成一条链路
        Assert.Same(a.BusLock, b.BusLock);

        var ex = Assert.Throws<InvalidOperationException>(() => SerialPortPool.Rent("COMPOOLTEST", 9600));
        Assert.Contains("波特率", ex.Message);

        a.Dispose();
        b.Dispose();                                     // 引用归零，口子真正关闭
        var c = SerialPortPool.Rent("COMPOOLTEST", 9600);   // 关完再租，换波特率就行了
        Assert.NotSame(a.Transport, c.Transport);
        c.Dispose();
    }

    private sealed class Collect(Action<Sample> onNext) : IObserver<Sample>
    {
        public void OnNext(Sample value) => onNext(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
