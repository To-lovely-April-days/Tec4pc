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

    /// <summary>单通道 + 把日志（级别，正文）收进列表。</summary>
    private static DriverContext CtxLog(int channel, List<(string, string)> logs)
        => Ctx(new[] { channel }, ParameterSet.Of((YudianProbeDriverBase.FieldModuleCh, 1d)),
               (l, t) => { lock (logs) logs.Add((l, t)); });

    /// <summary>「宇电通道」不填——走缺省（跟工位走）。</summary>
    private static DriverContext CtxFollow(params int[] channels) => Ctx(channels, new ParameterSet());

    /// <summary>
    /// 「输入规格」的缺省是「写成 Pt100 两位小数」（用户定的）——这里的假模块多数配的是 InP=21，
    /// 不想测写的那些用例把它按成「照模块的 InP」，免得每条都先把假模块写成 22。
    /// 要测缺省行为的传 keepInp: false。
    /// </summary>
    private static DriverContext Ctx(int[] channels, ParameterSet config, Action<string, string>? log = null, bool keepInp = true)
    {
        if (keepInp && !config.Has(YudianProbeDriverBase.FieldInp)) config[YudianProbeDriverBase.FieldInp] = YudianProbeDriverBase.InpKeep;
        return new DriverContext
        {
            InstanceId = $"PRB{channels[0]}",
            ChannelNumbers = channels,
            Config = config,
            Simulated = false,
            TimeScale = 1,
            Clock = () => DateTimeOffset.Now,
            Log = log ?? ((_, _) => { })
        };
    }

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
        Assert.Null(YudianProbeDriverBase.ResolveModuleChannel(new ParameterSet(), wellB));             // 没填 = 跟工位走，开口子再定
        Assert.Null(YudianProbeDriverBase.ResolveModuleChannel(
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
    public async Task 选的那一路在模块上关着_报错点名能读的是哪几路()
    {
        var drv = Wire(new YudianTrProbeDriver(), MakeJ7());       // CH1/CH2 开，CH3/CH4 关

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => drv.OpenAsync(new ParameterSet(), Ctx(channel: 1, moduleCh: 3), CancellationToken.None));
        Assert.Contains("CH3 是关闭的", ex.Message);
        Assert.Contains("测温口是 CH1、CH2", ex.Message);
    }

    [Fact]
    public async Task 测试连接_把四路各是什么口和读数都摆出来()
    {
        var j7 = MakeJ7();
        j7.Regs[1536] = 253;                             // CH1 = 25.3
        j7.Regs[1537] = 8100;                            // CH2 断线残值
        j7.Regs[1664] = 0x0001;                          // CH2 oral（偶数通道在低字节）
        var drv = Wire(new YudianTrProbeDriver(), j7);

        var r = await drv.ProbeAsync(new ParameterSet(), CancellationToken.None);

        Assert.True(r.Success);
        Assert.Contains("CH1 Pt100（InP=21） 25.3 ℃、CH2 Pt100（InP=21） 断线/超量程（原始值 8100 = 810.0 ℃，高于量程上限 800.0：模块量到的电阻像开路", r.Message);
        Assert.Contains("J7 不支持））、CH3 关、CH4 关——测温口是 CH1、CH2", r.Message);
        Assert.DoesNotContain("全报断线", r.Message);                    // 还有一路好的，不上那段排查话

        // pH 表按 dPt 的小数位显示，没有单位
        var j4 = MakeJ4();
        j4.Regs[1536] = 700; j4.Regs[1537] = 412;
        var ph = await Wire(new YudianPhProbeDriver(), j4).ProbeAsync(new ParameterSet(), CancellationToken.None);
        Assert.True(ph.Success);
        Assert.Contains("CH1 4~20mA（InP=51，J4） 7.00、CH2 4~20mA（InP=51，J4） 4.12、CH3 关、CH4 关——线性电流口是 CH1、CH2", ph.Message);
    }

    // ── 现场那台：一台模块两种口混用——CH1/CH3 线性电流（pH 变送器）、CH2/CH4 Pt100（Tr） ──

    /// <summary>现场读回来的样子：In 四路全开，InP 组 1/3 = 51、组 2/4 = 21。</summary>
    private static FakeModbusSlave MakeFieldModule()
    {
        var dev = new FakeModbusSlave();
        dev.Regs[384] = 1; dev.Regs[385] = 2; dev.Regs[386] = 3; dev.Regs[387] = 4;
        dev.Regs[2048] = 51; dev.Regs[2049] = 21; dev.Regs[2050] = 51; dev.Regs[2051] = 21;
        dev.Regs[2052] = 0; dev.Regs[2056] = 1400;       // 组1 pH 定标 0.00~14.00
        dev.Regs[2054] = 0; dev.Regs[2058] = 1400;       // 组3
        dev.Regs[2128] = 2;
        dev.Regs[1536] = 700;                            // CH1 pH 7.00
        dev.Regs[1537] = 246;                            // CH2 24.6 ℃（Pt100 一位小数，不看 dPt）
        dev.Regs[1538] = 412;                            // CH3 pH 4.12
        dev.Regs[1539] = 251;                            // CH4 25.1 ℃
        return dev;
    }

    [Fact]
    public async Task 跟工位走就是CH几_通道1读CH1_通道2读CH2_那一路不合就点名能读的()
    {
        // 混用的那台：CH1/CH3 线性、CH2/CH4 Pt100。跟工位走不再去找「第几路同类口」，
        // 通道 1 → CH1（用户定的），CH1 不是测温口就如实说，并点名测温口是哪几路
        var dev = MakeFieldModule();
        var shared = new SemaphoreSlim(1, 1);
        var logs = new List<string>();
        var trA = Wire(new YudianTrProbeDriver(), dev, shared);
        var trB = Wire(new YudianTrProbeDriver(), dev, shared);
        var phA = Wire(new YudianPhProbeDriver(), dev, shared);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => trA.OpenAsync(new ParameterSet(), CtxFollow(1), CancellationToken.None));
        Assert.Contains("CH1：这一路的输入规格是 4~20mA（InP=51，J4）", ex.Message);
        Assert.Contains("测温口是 CH2、CH4", ex.Message);

        await using var sTrB = await trB.OpenAsync(new ParameterSet(), Ctx(new[] { 2 }, new ParameterSet(), (_, t) => { lock (logs) logs.Add(t); }), CancellationToken.None);
        await using var sPhA = await phA.OpenAsync(new ParameterSet(), CtxFollow(1), CancellationToken.None);
        Assert.Equal(2, ((YudianProbeSession)sTrB).ModuleChannel);
        Assert.Equal(1, ((YudianProbeSession)sPhA).ModuleChannel);
        lock (logs) Assert.Contains(logs, l => l.Contains("跟工位走：通道 2 → CH2") && l.Contains("测温口 CH2、CH4"));

        // 手选 4：工位 B 那支接在 CH4 上
        await using var sTr4 = await Wire(new YudianTrProbeDriver(), dev, shared)
            .OpenAsync(new ParameterSet(), Ctx(channel: 2, moduleCh: 4), CancellationToken.None);
        var got = new List<Sample>();
        using var s2 = sTrB.Samples.Subscribe(new Collect(x => { lock (got) got.Add(x); }));
        using var s3 = sPhA.Samples.Subscribe(new Collect(x => { lock (got) got.Add(x); }));
        using var s4 = sTr4.Samples.Subscribe(new Collect(x => { lock (got) got.Add(x); }));
        foreach (var s in new[] { sTrB, sPhA, sTr4 })
            await ((YudianProbeSession)s).PollOnceAsync(CancellationToken.None);
        lock (got)
        {
            Assert.Equal(24.6, got.Single(x => x is { Tag: "Tr", Channel: 2 } && x.Value < 25).Value, 2);
            Assert.Equal(25.1, got.Single(x => x is { Tag: "Tr", Channel: 2 } && x.Value > 25).Value, 2);
            Assert.Equal(7.0, got.Single(x => x is { Tag: "pH", Channel: 1 }).Value, 2);
        }
    }

    [Fact]
    public async Task 现场那台_Tr手选CH1_报错说这一路是线性电流_测温口是CH2CH4()
    {
        // 截图里那条：「宇电通道」填 1，CH1 却是 4~20mA 口
        var drv = Wire(new YudianTrProbeDriver(), MakeFieldModule());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => drv.OpenAsync(new ParameterSet(), Ctx(channel: 1, moduleCh: 1), CancellationToken.None));
        Assert.Contains("CH1：这一路的输入规格是 4~20mA（InP=51，J4）", ex.Message);
        Assert.Contains("测温口是 CH2、CH4", ex.Message);
        Assert.Contains("改成那一路", ex.Message);
    }

    [Fact]
    public async Task 现场那台_测试连接不把pH那两路当错_四路各是什么口都摆出来()
    {
        var r = await Wire(new YudianTrProbeDriver(), MakeFieldModule()).ProbeAsync(new ParameterSet(), CancellationToken.None);

        Assert.True(r.Success);
        // 读不了的那两路把 PV 寄存器原样印出来，让人自己看它像不像温度
        Assert.Contains("CH1 4~20mA（InP=51，J4） 原始值 700、CH2 Pt100（InP=21） 24.6 ℃、CH3 4~20mA（InP=51，J4） 原始值 412、CH4 Pt100（InP=21） 25.1 ℃——测温口是 CH2、CH4", r.Message);
        Assert.Contains("模块参数：In=1/2/3/4，InP=51/21/51/21，ScL=0/0/0/0，ScH=1400/0/1400/0，AAF=0/0/0/0，dPt=2，Loc=0，型号字 0", r.Message);
    }

    [Fact]
    public async Task 现场第三眼_写完InP四路全报断线_测试连接说清重新上电和接法_轮询只警告一次()
    {
        // 截图里那台：In 四路都用组 1，InP1 已写成 21，四路都挂着 oral——可读数 24.0~24.3 都在量程内：
        // 值照常用（不按 Bad），标志挂着单独标出来，排查话（重上电 / 面板 / 接线）照样说
        var dev = new FakeModbusSlave();
        for (var i = 0; i < 4; i++) { dev.Regs[384 + i] = 1; dev.Regs[1536 + i] = (ushort)(240 + i); }
        dev.Regs[2048] = 21; dev.Regs[2128] = 1; dev.Regs[2131] = 8848;
        dev.Regs[1664] = 0x0101; dev.Regs[1665] = 0x0101;
        var drv = Wire(new YudianTrProbeDriver(), dev);

        var r = await drv.ProbeAsync(new ParameterSet(), CancellationToken.None);
        Assert.True(r.Success);                                            // 链路是通的
        Assert.Contains("CH3 Pt100（InP=21） 24.2 ℃（「输入故障」位挂着，读数在量程内——标志可能锁着没清）", r.Message);
        Assert.Contains("能读的几路报警字里「输入故障」位都挂着（读数在量程内，照常用）——①每改一次 InP 都要把模块断电重上电", r.Message);
        Assert.Contains("PT100 输入需要先接好线再重新上电", r.Message);
        Assert.Contains("②看模块自己的面板", r.Message);
        Assert.Contains("颜色相同（阻值小）的两根接 IN 和 COM、剩下一根接 RT", r.Message);
        Assert.DoesNotContain("AAF 里", r.Message);                        // AAF 全 0：不提锁定

        // 现场第四眼：InP=22，四路原始值都是 -20215 = -202.15 ℃——压在 -200.00 的下限之下，不是「没数」
        var low = new FakeModbusSlave();
        for (var i = 0; i < 4; i++) { low.Regs[384 + i] = 1; low.Regs[1536 + i] = unchecked((ushort)-20215); }
        low.Regs[2048] = 22; low.Regs[1664] = 0x0101; low.Regs[1665] = 0x0101;
        var r4 = await Wire(new YudianTrProbeDriver(), low).ProbeAsync(new ParameterSet(), CancellationToken.None);
        Assert.Contains("CH2 Pt100 两位小数（InP=22） 断线/超量程（原始值 -20215 = -202.15 ℃，低于量程下限 -200.00：模块量到的电阻接近 0——短路、RT 与 IN 接错位，或探头是热电偶）", r4.Message);

        // AAF.0 开着：加一句「标志锁住要手动清」
        dev.Regs[2072] = 1;
        var r2 = await drv.ProbeAsync(new ParameterSet(), CancellationToken.None);
        Assert.Contains("AAF=1/0/0/0", r2.Message);
        Assert.Contains("断线标志锁住要手动清", r2.Message);

        // 轮询：读数在量程内就按 Good 发；标志挂着只说一遍，清了记一笔，再挂再说
        var logs = new List<string>();
        await using var s = await drv.OpenAsync(new ParameterSet(),
            Ctx(new[] { 1 }, ParameterSet.Of((YudianProbeDriverBase.FieldModuleCh, "3")), (_, t) => { lock (logs) logs.Add(t); }), CancellationToken.None);
        var ps = (YudianProbeSession)s;
        var got = new List<Sample>();
        using var sub = s.Samples.Subscribe(new Collect(x => { lock (got) got.Add(x); }));
        await ps.PollOnceAsync(CancellationToken.None);
        await ps.PollOnceAsync(CancellationToken.None);
        lock (got) Assert.All(got, x => Assert.Equal(Quality.Good, x.Quality));
        lock (logs) Assert.Single(logs, l => l.Contains("CH3 报警字里「输入故障」位挂着") && l.Contains("读数 24.2 在量程内"));
        lock (logs) Assert.DoesNotContain(logs, l => l.Contains("断线/超量程"));
        dev.Regs[1665] = 0;
        await ps.PollOnceAsync(CancellationToken.None);
        lock (logs) Assert.Contains(logs, l => l.Contains("CH3 报警标志已清"));
        dev.Regs[1665] = 0x0101;
        await ps.PollOnceAsync(CancellationToken.None);
        lock (logs) Assert.Equal(2, logs.Count(l => l.Contains("CH3 报警字里「输入故障」位挂着")));

        // 真断线（读数压在量程之下）才按 Bad 发、才上「断线/超量程」那段话
        dev.Regs[1538] = unchecked((ushort)-2022);
        await ps.PollOnceAsync(CancellationToken.None);
        lock (got) Assert.Equal(Quality.Bad, got[^1].Quality);
        lock (logs) Assert.Single(logs, l => l.Contains("CH3 断线/超量程（读数 -202.2，原始值 -2022）") && l.Contains("断电重上电"));
    }

    // ── 现场第二眼：四路 InP 全是 51——Pt100 接上去读不了，模块没面板，改 InP 只能走总线 ──

    /// <summary>截图里那台：In 四路全开，InP 四路都是 51，型号字 8848（0x2290）。</summary>
    private static FakeModbusSlave MakeAll51()
    {
        var dev = new FakeModbusSlave();
        for (var i = 0; i < 4; i++) { dev.Regs[384 + i] = (ushort)(i + 1); dev.Regs[2048 + i] = 51; }
        dev.Regs[2128] = 1;
        dev.Regs[2131] = 8848;
        dev.Regs[1537] = 246;                            // CH2 上接着 Pt100，寄存器里的原始值
        return dev;
    }

    [Fact]
    public async Task 四路全是51_测试连接把原始值和模块参数摆出来_并说怎么改成Pt100()
    {
        var r = await Wire(new YudianTrProbeDriver(), MakeAll51()).ProbeAsync(new ParameterSet(), CancellationToken.None);

        Assert.False(r.Success);
        Assert.Contains("CH1 4~20mA（InP=51，J4） 原始值 0、CH2 4~20mA（InP=51，J4） 原始值 246、", r.Message);
        Assert.Contains("四路里没有一路是测温口", r.Message);
        Assert.Contains("「输入规格」缺省会把它那一路写成 Pt100（写完模块要断电重上电）", r.Message);
        Assert.Contains("InP=51/51/51/51", r.Message);
        Assert.Contains("型号字 8848", r.Message);
    }

    [Fact]
    public void 输入规格下拉_有K型热电偶那一项_解析成InP0()
    {
        var f = new YudianTrProbeDriver().ConfigSchema.Find(YudianProbeDriverBase.FieldInp)!;
        Assert.Equal(new[] { "照模块的 InP（不写）", "写成 Pt100（InP=21）", "写成 Pt100 两位小数（InP=22）", "写成 K 型热电偶（InP=0）" }, f.Choices);
        Assert.Equal("写成 Pt100 两位小数（InP=22）", f.Default);                                        // 用户定的缺省
        Assert.Equal((ushort)0, YudianProbeDriverBase.ResolveInpWrite(ParameterSet.Of((YudianProbeDriverBase.FieldInp, YudianProbeDriverBase.InpWriteK))));
        Assert.Equal((ushort)22, YudianProbeDriverBase.ResolveInpWrite(ParameterSet.Of((YudianProbeDriverBase.FieldInp, YudianProbeDriverBase.InpWritePt100x100))));
        Assert.Equal((ushort)22, YudianProbeDriverBase.ResolveInpWrite(new ParameterSet()));             // 没填 = 缺省
        Assert.Null(YudianProbeDriverBase.ResolveInpWrite(ParameterSet.Of((YudianProbeDriverBase.FieldInp, YudianProbeDriverBase.InpKeep))));
        Assert.Null(new YudianPhProbeDriver().ConfigSchema.Find(YudianProbeDriverBase.FieldInp));   // pH 电极没有这一项
    }

    [Fact]
    public async Task 输入规格_写成Pt100_写06读回核对_然后按Pt100读数()
    {
        var dev = MakeAll51();
        var logs = new List<string>();
        var drv = Wire(new YudianTrProbeDriver(), dev);
        var cfg = ParameterSet.Of((YudianProbeDriverBase.FieldModuleCh, "2"),
                                  (YudianProbeDriverBase.FieldInp, YudianProbeDriverBase.InpWritePt100));

        await using var s = await drv.OpenAsync(new ParameterSet(), Ctx(new[] { 1 }, cfg, (_, t) => { lock (logs) logs.Add(t); }), CancellationToken.None);

        Assert.Equal(21, dev.Regs[2049]);                                    // 只写了 CH2 那一组
        Assert.Equal(51, dev.Regs[2048]); Assert.Equal(51, dev.Regs[2050]); Assert.Equal(51, dev.Regs[2051]);
        Assert.Contains(dev.Requests, q => q == "写寄存器 2049=21");
        lock (logs) Assert.Contains(logs, l => l.Contains("InP 由 51 写成 21") && l.Contains("读回核对一致"));

        var got = new List<Sample>();
        using var sub = s.Samples.Subscribe(new Collect(x => { lock (got) got.Add(x); }));
        await ((YudianProbeSession)s).PollOnceAsync(CancellationToken.None);
        lock (got) Assert.Equal(24.6, got.Single(x => x.Tag == "Tr").Value, 2);

        // 再开一次：已经是 21 了就不再写
        dev.Requests.Clear();
        await using var s2 = await drv.OpenAsync(new ParameterSet(), Ctx(new[] { 1 }, cfg), CancellationToken.None);
        Assert.DoesNotContain(dev.Requests, q => q.StartsWith("写寄存器"));
    }

    [Fact]
    public async Task 输入规格_写的前提与失败都如实拒绝()
    {
        var drv = Wire(new YudianTrProbeDriver(), MakeAll51());

        // 跟工位走 + 要写：写通道对应的那一路（通道 2 → CH2，In=2 → 组 2）
        var followDev = MakeAll51();
        var follow = ParameterSet.Of((YudianProbeDriverBase.FieldInp, YudianProbeDriverBase.InpWritePt100));
        await using (await Wire(new YudianTrProbeDriver(), followDev).OpenAsync(new ParameterSet(), Ctx(new[] { 2 }, follow), CancellationToken.None))
        {
            Assert.Equal(21, followDev.Regs[2049]);
            Assert.Equal(51, followDev.Regs[2048]);
        }

        // Loc 锁着：不写，说清
        var locked = MakeAll51(); locked.Regs[2130] = 0b0010_0000;
        var cfg = ParameterSet.Of((YudianProbeDriverBase.FieldModuleCh, "2"),
                                  (YudianProbeDriverBase.FieldInp, YudianProbeDriverBase.InpWritePt100));
        var ex2 = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Wire(new YudianTrProbeDriver(), locked).OpenAsync(new ParameterSet(), Ctx(new[] { 1 }, cfg), CancellationToken.None));
        Assert.Contains("Loc=32 锁着写入", ex2.Message);
        Assert.DoesNotContain(locked.Requests, q => q.StartsWith("写寄存器"));

        // 写了读回还是 51（模块应答了但不生效）：不信应答只信读回
        var ex3 = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Wire(new YudianTrProbeDriver(), new WriteIgnoringSlave()).OpenAsync(new ParameterSet(), Ctx(new[] { 1 }, cfg), CancellationToken.None));
        Assert.Contains("读回来却是 51", ex3.Message);

        // 手选的那一路不是 Pt100、又没选写：报错里带上「怎么改」
        var ex4 = await Assert.ThrowsAsync<InvalidOperationException>(
            () => drv.OpenAsync(new ParameterSet(), Ctx(channel: 1, moduleCh: 2), CancellationToken.None));
        Assert.Contains("CH2：这一路的输入规格是 4~20mA（InP=51，J4）", ex4.Message);
        Assert.Contains("「输入规格」选「写成 Pt100 两位小数（InP=22）」再连", ex4.Message);
    }

    /// <summary>应答 06 正常、寄存器却不变的从站——模拟「Loc 没报锁但写就是不生效」那种情况。</summary>
    private sealed class WriteIgnoringSlave : TecControl.Core.Comm.ISerialTransport
    {
        private readonly FakeModbusSlave _inner = MakeAll51();
        public bool IsOpen => _inner.IsOpen;
        public void Open() => _inner.Open();
        public void Close() => _inner.Close();
        public void DiscardInput() => _inner.DiscardInput();
        public void Dispose() => _inner.Dispose();
        public int Read(byte[] buffer, int offset, int count, int timeoutMs) => _inner.Read(buffer, offset, count, timeoutMs);
        public void Write(byte[] buffer, int offset, int count)
        {
            _inner.Write(buffer, offset, count);
            for (var i = 0; i < 4; i++) _inner.Regs[2048 + i] = 51;     // 写什么都回滚
        }
    }

    [Fact]
    public async Task 跟工位走_那一路关着_如实拒绝_四路都不是测温口_测试连接说不通()
    {
        var dev = MakeFieldModule();
        dev.Regs[385] = 0;                               // CH2 关
        var drv = Wire(new YudianTrProbeDriver(), dev);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => drv.OpenAsync(new ParameterSet(), CtxFollow(2), CancellationToken.None));
        Assert.Contains("CH2 是关闭的", ex.Message);
        Assert.Contains("测温口是 CH4", ex.Message);

        // 四路里一路测温口都没有：测试连接如实说不通
        dev.Regs[2051] = 51;
        var r = await drv.ProbeAsync(new ParameterSet(), CancellationToken.None);
        Assert.False(r.Success);
        Assert.Contains("没有一路是测温口", r.Message);
    }

    [Fact]
    public async Task 输入规格缺省_写成Pt100两位小数_新探头空配置第一次连就把那一路写成22()
    {
        // 刚拖上台面、属性栏没打开过：配置是空的。缺省也得是用户定的那套——跟工位走 + 写成 22
        var j7 = MakeJ7();                               // InP 组 1 = 21
        j7.Regs[1536] = 2500;                            // 写成 22 之后两位小数：25.00
        var drv = Wire(new YudianTrProbeDriver(), j7);

        await using var s = await drv.OpenAsync(new ParameterSet(), Ctx(new[] { 1 }, new ParameterSet(), keepInp: false), CancellationToken.None);

        Assert.Equal(22, j7.Regs[2048]);
        Assert.Contains(j7.Requests, q => q == "写寄存器 2048=22");
        Assert.Equal(1, ((YudianProbeSession)s).ModuleChannel);
        var got = new List<Sample>();
        using var sub = s.Samples.Subscribe(new Collect(x => { lock (got) got.Add(x); }));
        await ((YudianProbeSession)s).PollOnceAsync(CancellationToken.None);
        lock (got) Assert.Equal(25.0, got.Single(x => x.Tag == "Tr").Value, 2);

        // 缺省串口：现场那台宇电在 CH344 的 D 口 COM3（用户定的）
        Assert.Equal("COM3", new YudianTrProbeDriver().ConnectionSchema.Find(YudianProbeDriverBase.FieldPort)!.Default);
        Assert.Equal("COM3", new YudianPhProbeDriver().ConnectionSchema.Find(YudianProbeDriverBase.FieldPort)!.Default);
    }

    [Fact]
    public async Task 温度探头的口子插了pH表_开机直接拒绝()
    {
        var j4 = MakeJ4();                               // 线性电流——对 Tr 探头就是接反
        var drv = Wire(new YudianTrProbeDriver(), j4);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => drv.OpenAsync(new ParameterSet(), Ctx(1), CancellationToken.None));
        Assert.Contains("4~20mA（InP=51，J4）", ex.Message);
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
    public async Task 报警位锁着读数正常_按Good发_日志说一遍标志锁着()
    {
        var j7 = MakeJ7();
        j7.Regs[1536] = unchecked((ushort)-185);         // −18.5 ℃，量程内
        j7.Regs[1664] = 0x0100;                          // CH1 oral 锁着
        var logs = new List<(string, string)>();
        var drv = Wire(new YudianTrProbeDriver(), j7);

        await using var s = await drv.OpenAsync(new ParameterSet(), CtxLog(1, logs), CancellationToken.None);
        var got = new List<Sample>();
        using var sub = s.Samples.Subscribe(new Collect(x => { lock (got) got.Add(x); }));
        await ((YudianProbeSession)s).PollOnceAsync(CancellationToken.None);
        await ((YudianProbeSession)s).PollOnceAsync(CancellationToken.None);

        lock (got) Assert.All(got.Where(x => x.Tag == "Tr"), x => Assert.Equal(Quality.Good, x.Quality));
        lock (logs) Assert.Single(logs, l => l.Item2.Contains("「输入故障」位挂着") && l.Item2.Contains("AAF"));
    }

    [Fact]
    public async Task 串口中途死了_读失败立刻关掉重开_口子回来就接着出数()
    {
        // 现场：降温跑到 48 min，宇电那条口子突然读不出来，之后再也没回来——
        // USB 转串被抖了一下旧句柄死了，每一拍都报错，从前没人去重开
        var j7 = MakeJ7();
        j7.Regs[1536] = 250;
        var logs = new List<(string, string)>();
        var drv = Wire(new YudianTrProbeDriver(), j7);
        await using var s = await drv.OpenAsync(new ParameterSet(), CtxLog(1, logs), CancellationToken.None);
        var ps = (YudianProbeSession)s;
        var got = new List<Sample>();
        using var sub = s.Samples.Subscribe(new Collect(x => { lock (got) got.Add(x); }));
        await ps.PollOnceAsync(CancellationToken.None);
        lock (got) Assert.Single(got);

        j7.Dead = true;                                  // 句柄死了：一写就抛 IO 错
        var opens = j7.Opens;
        await ps.PollOnceAsync(CancellationToken.None);  // 这一拍读失败 → 立刻关掉重开（假口子重开即活）
        Assert.Equal(1, ps.LinkReopens);
        Assert.Equal(opens + 1, j7.Opens);
        Assert.False(j7.Dead);
        lock (logs)
        {
            Assert.Contains(logs, l => l.Item2.Contains("读失败") && l.Item2.Contains("函数不正确"));
            Assert.Contains(logs, l => l.Item2.Contains("串口已关掉重开"));
        }

        await ps.PollOnceAsync(CancellationToken.None);  // 下一拍就出数了
        lock (got) Assert.Equal(2, got.Count);
        lock (logs) Assert.Contains(logs, l => l.Item2.Contains("宇电模块恢复"));
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
