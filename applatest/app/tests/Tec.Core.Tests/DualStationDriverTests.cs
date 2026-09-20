using Tec.Driver.Abi;
using Tec.Drivers.DualStation;
using Tec.Drivers.Rd105.Modbus;
using Tec.Drivers.Rd105;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 双工位反应主机组合会话（需求 §1/§2）。主机只带自己的两条链路（RD105 + IO8R），
/// 全是假设备；盯规约里的硬条款：开机继电器复位 TEC 侧、切换序列先关输出、
/// 高目标没 IO8R 诚实拒绝、回切要等夹套凉到阈值−滞回、反馈没跟上输出保持
/// 关闭、SafeStop 断继电器；外部釜温（宇电探头会话发的）经 IExternalReactorTemp
/// 喂进来，进判到达与 dT。
/// </summary>
public sealed class DualStationDriverTests
{
    private sealed class Bench
    {
        public FakeRd105Device Rd = new();
        public FakeModbusSlave Io = new();
        public DualStationDriver Drv = null!;
        public List<(string Level, string Text)> Logs = new();
        /// <summary>会话读的钟。要验「自动切电加热的最短间隔」就得能把它往前拨。</summary>
        public DateTimeOffset Now = DateTimeOffset.Now;

        public DriverContext Ctx(ParameterSet? config = null) => new()
        {
            InstanceId = "DUO1",
            ChannelNumbers = new[] { 1, 2 },
            Config = config ?? new ParameterSet(),
            Simulated = false,
            TimeScale = 1,
            Clock = () => Now,
            Log = (lvl, text) => { lock (Logs) Logs.Add((lvl, text)); }
        };
    }

    private static Bench Rig(bool withIo = true)
    {
        var b = new Bench();
        b.Drv = new DualStationDriver
        {
            LinksFactory = _ => new DuoLinks
            {
                Rd105 = new Rd105Link(b.Rd),
                IoPort = withIo ? b.Io : null,
                Io = withIo ? new Io8rClient(new ModbusRtuClient(b.Io, 1, 200)) : null
            }
        };
        return b;
    }

    /// <summary>连接参数：RD105 周期压到 200ms、模块轮询拉到 5s（测试手动一拍一拍推）。</summary>
    private static ParameterSet Conn() => ParameterSet.Of(
        (Rd105TecDriver.FieldPeriod, 200d),
        (DualStationDriver.Fields.Tick, 5000d));

    /// <summary>
    /// 「TEC 加热」启用的设备配置——按阈值切换那套规矩的测试都用它。
    /// 默认（不启用）是另一套：TEC 只当冷源，升温一律走电加热棒，见本文件下半段。
    /// </summary>
    private static ParameterSet TecOn(params (string Key, object? Value)[] more)
        => ParameterSet.Of(new (string, object?)[] { (DualStationDriver.Fields.TecHeat, "启用") }
                           .Concat(more).ToArray());

    private static ITemperatureControl Temp(IDeviceSession s, int well)
        => s.CapabilitiesOf(well).OfType<ITemperatureControl>().Single();

    /// <summary>等内层 RD105 轮询把夹套温度端上来（回切判据要吃实测 Tj）。</summary>
    private static async Task WaitJacket(ITemperatureControl t, double value)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        // 注意 NaN：夹套还没端上来时 CurrentJacket 是 NaN，任何比较都是 false，
        // 写成 > 0.01 的话循环会立刻退出——要用「不满足 ≤」这种 NaN 也算没到的写法
        while (DateTime.UtcNow < deadline && !(Math.Abs(t.CurrentJacket - value) <= 0.01))
            await Task.Delay(30);
        Assert.Equal(value, t.CurrentJacket, 1);
    }

    // ── 开机 ─────────────────────────────────────────────────────────

    [Fact]
    public void 连接参数缺省照现场那台机器填()
    {
        // 用户定的：RD105 在 CH344 的 C 口（COM7）、IO8R 在 B 口（COM10），
        // 两条都是 RS485 = Modbus-RTU、9600、站号 1。装好程序不改一项点「连接」就该通。
        var cn = new ParameterSet().FillDefaults(new DualStationDriver().ConnectionSchema);

        Assert.Equal("COM7", cn.Str(DualStationDriver.Fields.PortRd105));
        Assert.Equal(9600, cn.Num(DualStationDriver.Fields.BaudRd105));
        Assert.Equal(Rd105Protocol.Modbus, cn.Str(DualStationDriver.Fields.ProtoRd105));
        Assert.Equal(1, cn.Int(DualStationDriver.Fields.AddrRd105));

        Assert.Equal("有", cn.Str(DualStationDriver.Fields.HasIo));
        Assert.Equal("COM10", cn.Str(DualStationDriver.Fields.PortIo));
        Assert.Equal(9600, cn.Num(DualStationDriver.Fields.BaudIo));
        Assert.Equal(1, cn.Int(DualStationDriver.Fields.AddrIo));

        // 单独的 RD105 温控器驱动跟主机里那条 RD105 链路指的是同一台机器，缺省不能各说各的
        Assert.Equal(Rd105TecDriver.DefaultPort, DualStationDriver.Defaults.PortRd105);
        Assert.Equal(Rd105TecDriver.DefaultBaud, DualStationDriver.Defaults.BaudRd105);
        Assert.Equal(Rd105TecDriver.DefaultProtocol, DualStationDriver.Defaults.ProtoRd105);
        Assert.Equal(Rd105TecDriver.DefaultAddress, DualStationDriver.Defaults.AddrRd105);
    }

    [Fact]
    public async Task 开机_继电器复位TEC侧_两路保护都写进设备()
    {
        var b = Rig();
        b.Io.Coils[0] = b.Io.Coils[1] = true;    // 上一炉留下的残局
        var cfg = ParameterSet.Of((Rd105TecDriver.FieldOverUp, 150d), (Rd105TecDriver.FieldOverLow, -30d));

        await using var s = await b.Drv.OpenAsync(Conn(), b.Ctx(cfg), CancellationToken.None);

        Assert.All(b.Io.Coils, c => Assert.False(c));
        Assert.Contains(b.Io.Requests, r => r.StartsWith("写多DO"));
        foreach (var tc in new[] { 1, 2 })
        {
            Assert.Equal(150_00000, b.Rd.Get(tc, "OVERTEMPUP"));
            Assert.Equal(-30_00000, b.Rd.Get(tc, "OVERTEMPLOWER"));
        }
    }

    // ── 外部釜温（宇电探头会话发的，工作台牵线喂进来） ──────────────

    [Fact]
    public async Task 外部Tr喂进来_进判据也进dT()
    {
        var b = Rig();
        b.Rd.Set(1, "TCADJTEMP", 24_00000);      // A 夹套 24.0
        await using var s = await b.Drv.OpenAsync(Conn(), b.Ctx(), CancellationToken.None);
        await s.StartAsync(CancellationToken.None);
        await WaitJacket(Temp(s, 0), 24.0);

        ((IExternalReactorTemp)s).FeedReactor(1, 25.0, Quality.Good);

        var got = new List<Sample>();
        using var sub = s.Samples.Subscribe(new Collect(x => { lock (got) got.Add(x); }));
        await ((DuoSession)s).PollOnceAsync(CancellationToken.None);
        await s.StopAsync(CancellationToken.None);

        Assert.Equal(25.0, Temp(s, 0).CurrentReactor, 1);          // 判到达吃它
        lock (got) Assert.Equal(1.0, got.Single(x => x is { Tag: "dT", Channel: 1 }).Value, 1);
    }

    [Fact]
    public async Task 外部Tr质量坏_按NaN处置_不进判据()
    {
        var b = Rig();
        await using var s = await b.Drv.OpenAsync(Conn(), b.Ctx(), CancellationToken.None);

        ((IExternalReactorTemp)s).FeedReactor(1, 810.0, Quality.Bad);   // 断线残值

        Assert.True(double.IsNaN(Temp(s, 0).CurrentReactor));
    }

    // ── 温度指令：真机认领 ABI 的能力通用执行器 ─────────────────────

    [Fact]
    public async Task 真机认领五条温度指令_并能真执行一步控温()
    {
        var b = Rig();
        await using var s = await b.Drv.OpenAsync(Conn(), b.Ctx(), CancellationToken.None);

        // 与仿真同一份执行器——纯真机台面跑含温度步的配方不再报「没有设备认领」
        foreach (var id in new[] { CommandSpecs.Control, CommandSpecs.Gradient,
                                   CommandSpecs.Hold, CommandSpecs.PassiveCool, CommandSpecs.Reflux })
            Assert.NotNull(s.Resolve(id));
        Assert.Null(s.Resolve(CommandSpecs.Stir));   // 没有搅拌，认不了不硬认

        // 冒烟：控温一步真跑通。外部 Tr 已在允差内 → 立即判到达
        ((IExternalReactorTemp)s).FeedReactor(1, 24.9, Quality.Good);
        var ctx = new CommandContext
        {
            Channel = 1,
            Capabilities = new OneCap(Temp(s, 0)),
            Now = () => DateTimeOffset.Now,
            TimeScale = 10000                        // 「到达后等稳定」那 1 分钟缩成几毫秒
        };
        var outcome = await s.Resolve(CommandSpecs.Control)!.ExecuteAsync(
            ctx, ParameterSet.Of(("target", 25d), ("tol", 0.5d)), CancellationToken.None);

        Assert.Equal(EndReason.Reached, outcome.Reason);
        Assert.Equal(25_00000, b.Rd.Get(1, "TG"));   // 目标真写到了设备上
        Assert.Equal(1, b.Rd.Get(1, "ENABLE"));
    }

    private sealed class OneCap(ICapability cap) : ICapabilityLookup
    {
        public T? Get<T>() where T : class, ICapability => cap as T;
        public bool Has<T>() where T : class, ICapability => cap is T;
        public IReadOnlyList<ICapability> All => new[] { cap };
    }

    // ── 热源切换：「TEC 加热」**启用**时按阈值判（本段都带 TecOn()）───────

    [Fact]
    public async Task 目标不过阈值_不碰继电器()
    {
        var b = Rig();
        await using var s = await b.Drv.OpenAsync(Conn(), b.Ctx(TecOn()), CancellationToken.None);
        var before = b.Io.Requests.Count;

        await Temp(s, 0).SetTargetAsync(new TempTarget(60), CancellationToken.None);

        Assert.Equal(60_00000, b.Rd.Get(1, "TG"));
        Assert.Equal(before, b.Io.Requests.Count);       // 一条 IO8R 指令都没发
        Assert.False(b.Io.Coils[0]);
    }

    [Fact]
    public async Task 目标过阈值_先关输出再切继电器再开回来()
    {
        var b = Rig();
        await using var s = await b.Drv.OpenAsync(Conn(), b.Ctx(TecOn()), CancellationToken.None);
        // **从开机之后开始数**：InitAsync 自己会先发两条 ENABLE=0（重连时设备可能还开着输出），
        // 从头找的话拿到的是开机那条，「先关输出再切继电器」这个断言就成了恒真
        // 先把输出真的开起来（60 ℃ 不过阈值，不会动继电器），这样切换序列里的
        // ①「关输出」才是一条真发出去的命令——否则开机那两条 ENABLE=0 已经让它
        // 无从分辨，断言就退化成恒真
        await Temp(s, 0).SetTargetAsync(new TempTarget(60), CancellationToken.None);
        Assert.False(b.Io.Coils[0]);
        Assert.Equal(1, b.Rd.Get(1, "ENABLE"));
        var mark = b.Rd.Commands.Count;

        await Temp(s, 0).SetTargetAsync(new TempTarget(120), CancellationToken.None);

        Assert.True(b.Io.Coils[0]);                      // A 已在电加热侧
        Assert.False(b.Io.Coils[1]);                     // B 没被牵连
        Assert.Equal(120_00000, b.Rd.Get(1, "TG"));
        Assert.Equal(1, b.Rd.Get(1, "ENABLE"));
        // 切继电器之前必须先关输出（带载切 = 触点拉弧）：
        // 命令流里 ENABLE=0 要出现在这次 TG=120 之前
        var cmds = b.Rd.Commands;
        var off = cmds.FindIndex(mark, c => c.Contains("TC1:ENABLE=0"));
        var tg = cmds.FindLastIndex(c => c.Contains("TC1:TG=12000000"));
        Assert.True(off >= 0 && off < tg, $"先关输出再下高温目标（off={off}, tg={tg}, mark={mark}）");
    }

    [Fact]
    public async Task 没配IO8R_高目标诚实拒绝()
    {
        var b = Rig(withIo: false);
        await using var s = await b.Drv.OpenAsync(Conn(), b.Ctx(TecOn()), CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Temp(s, 0).SetTargetAsync(new TempTarget(120), CancellationToken.None));

        Assert.Contains("阈值", ex.Message);
        Assert.NotEqual(120_00000, b.Rd.Get(1, "TG"));   // 拒绝了就不留下发痕迹
    }

    [Fact]
    public async Task 回切要等夹套凉到阈值减滞回()
    {
        var b = Rig();
        await using var s = await b.Drv.OpenAsync(Conn(), b.Ctx(TecOn()), CancellationToken.None);
        await s.StartAsync(CancellationToken.None);
        var t = Temp(s, 0);

        await t.SetTargetAsync(new TempTarget(120), CancellationToken.None);
        Assert.True(b.Io.Coils[0]);

        // 目标降回 50——但夹套还烫着（95 ℃），这一拍不许回切
        b.Rd.Set(1, "TCADJTEMP", 95_00000);
        await WaitJacket(t, 95.0);
        await t.SetTargetAsync(new TempTarget(50), CancellationToken.None);
        await ((DuoSession)s).PollOnceAsync(CancellationToken.None);
        Assert.True(b.Io.Coils[0]);                      // 仍在电加热侧（自然冷却）

        // 凉到 84 ℃（< 90 − 5）才回切，且回切后输出重新打开
        b.Rd.Set(1, "TCADJTEMP", 84_00000);
        await WaitJacket(t, 84.0);
        await ((DuoSession)s).PollOnceAsync(CancellationToken.None);
        await s.StopAsync(CancellationToken.None);

        Assert.False(b.Io.Coils[0]);
        Assert.Equal(1, b.Rd.Get(1, "ENABLE"));
        Assert.Equal(50_00000, b.Rd.Get(1, "TG"));
    }

    [Fact]
    public async Task 有反馈_没跟上就保持关输出并说清楚()
    {
        var b = Rig();
        await using var s = await b.Drv.OpenAsync(
            Conn(), b.Ctx(TecOn((DualStationDriver.Fields.Feedback, "有"))), CancellationToken.None);
        // b.Io.Inputs[0] 保持 false = 接触器没吸合

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Temp(s, 0).SetTargetAsync(new TempTarget(120), CancellationToken.None));

        Assert.Contains("未核实", ex.Message);
        Assert.Equal(0, b.Rd.Get(1, "ENABLE"));          // 输出保持关闭
    }

    [Fact]
    public async Task 有反馈_跟上了才开输出()
    {
        var b = Rig();
        b.Io.Inputs[0] = true;                           // 接触器辅助触点在位
        await using var s = await b.Drv.OpenAsync(
            Conn(), b.Ctx(TecOn((DualStationDriver.Fields.Feedback, "有"))), CancellationToken.None);

        await Temp(s, 0).SetTargetAsync(new TempTarget(120), CancellationToken.None);

        Assert.True(b.Io.Coils[0]);
        Assert.Equal(1, b.Rd.Get(1, "ENABLE"));
        lock (b.Logs) Assert.Contains(b.Logs, l => l.Text.Contains("反馈已核实"));
    }

    /// <summary>跑一拍并取这一拍发出的热源状态量（通道 1 = 工位 A）。</summary>
    private static async Task<double> HeatOnce(IDeviceSession s)
    {
        var got = new List<Sample>();
        using var sub = s.Samples.Subscribe(new Collect(x => { lock (got) got.Add(x); }));
        await ((DuoSession)s).PollOnceAsync(CancellationToken.None);
        lock (got) return got.Single(x => x is { Tag: "heat", Channel: 1 }).Value;
    }

    [Fact]
    public async Task 热源状态量_无反馈回路只报未核实()
    {
        var b = Rig();
        await using var s = await b.Drv.OpenAsync(Conn(), b.Ctx(TecOn()), CancellationToken.None);

        Assert.Equal(0, await HeatOnce(s));                                   // TEC
        await Temp(s, 0).SetTargetAsync(new TempTarget(120), CancellationToken.None);
        Assert.Equal(1, await HeatOnce(s));                                   // 电加热·未核实——没接反馈就不假装核实过
    }

    [Fact]
    public async Task 热源状态量_有反馈_对上报已核实_中途掉了降级并报出来()
    {
        var b = Rig();
        b.Io.Inputs[0] = true;
        await using var s = await b.Drv.OpenAsync(
            Conn(), b.Ctx(TecOn((DualStationDriver.Fields.Feedback, "有"))), CancellationToken.None);

        await Temp(s, 0).SetTargetAsync(new TempTarget(120), CancellationToken.None);
        Assert.Equal(2, await HeatOnce(s));                                   // 电加热·已核实

        // 接触器中途释放：继电器还吸着，DI 掉了——电加热其实没在加热，照实降级、报一次
        b.Io.Inputs[0] = false;
        Assert.Equal(1, await HeatOnce(s));
        Assert.Equal(1, await HeatOnce(s));
        lock (b.Logs) Assert.Single(b.Logs, l => l.Text.Contains("接触器可能已释放"));

        // 又对上了：回到已核实，报一次「已对上」
        b.Io.Inputs[0] = true;
        Assert.Equal(2, await HeatOnce(s));
        lock (b.Logs) Assert.Contains(b.Logs, l => l.Text.Contains("已与继电器对上"));
    }

    [Fact]
    public async Task 热源状态量_继电器已断触点仍吸着_按粘连报错()
    {
        var b = Rig();
        await using var s = await b.Drv.OpenAsync(
            Conn(), b.Ctx(TecOn((DualStationDriver.Fields.Feedback, "有"))), CancellationToken.None);

        b.Io.Inputs[0] = true;                                                 // 继电器在 TEC 侧，触点却吸着
        Assert.Equal(1, await HeatOnce(s));                                   // 电加热棒可能还带电——不能报 TEC
        lock (b.Logs) Assert.Contains(b.Logs, l => l.Level == "error" && l.Text.Contains("粘连"));
    }

    // ── 热源切换：「TEC 加热」**不启用**（默认）——所有加热都走电加热棒 ───
    //
    // 这一段一条 TecOn() 都不带，走的就是出厂默认。判据变成「该升温还是该降温」：
    // 目标高过被控量一个死区就切电加热，低一个死区才回 TEC，死区之内保持不动。

    [Fact]
    public async Task 默认不启用TEC加热_目标远低于阈值也切电加热()
    {
        var b = Rig();
        await using var s = await b.Drv.OpenAsync(Conn(), b.Ctx(), CancellationToken.None);
        await s.StartAsync(CancellationToken.None);
        var t = Temp(s, 0);
        await WaitJacket(t, 25.0);                       // 假机开机就是 25 ℃

        await t.SetTargetAsync(new TempTarget(60), CancellationToken.None);

        // 60 ℃ 离 90 的阈值远着呢，但 TEC 加热没启用——升温只有电加热棒这一条路
        Assert.True(b.Io.Coils[0]);
        Assert.False(b.Io.Coils[1]);                     // B 没被牵连
        Assert.Equal(60_00000, b.Rd.Get(1, "TG"));
        Assert.Equal(1, b.Rd.Get(1, "ENABLE"));
        await s.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task 默认不启用TEC加热_要降温才回TEC_且夹套得先凉够()
    {
        var b = Rig();
        await using var s = await b.Drv.OpenAsync(Conn(), b.Ctx(), CancellationToken.None);
        await s.StartAsync(CancellationToken.None);
        var t = Temp(s, 0);
        await WaitJacket(t, 25.0);

        await t.SetTargetAsync(new TempTarget(120), CancellationToken.None);
        Assert.True(b.Io.Coils[0]);

        // 目标降到 50，可夹套还烫着（95 ℃）：要降温，但这一刻不许接回 TEC
        b.Rd.Set(1, "TCADJTEMP", 95_00000);
        await WaitJacket(t, 95.0);
        await t.SetTargetAsync(new TempTarget(50), CancellationToken.None);
        await ((DuoSession)s).PollOnceAsync(CancellationToken.None);
        Assert.True(b.Io.Coils[0]);

        // 凉到 84 ℃（< 90 − 5）才回 TEC——这条硬约束两种模式都不让步
        b.Rd.Set(1, "TCADJTEMP", 84_00000);
        await WaitJacket(t, 84.0);
        await ((DuoSession)s).PollOnceAsync(CancellationToken.None);
        await s.StopAsync(CancellationToken.None);

        Assert.False(b.Io.Coils[0]);
        Assert.Equal(1, b.Rd.Get(1, "ENABLE"));          // 回切完输出照旧开着
    }

    [Fact]
    public async Task 默认不启用TEC加热_死区之内不折腾继电器()
    {
        var b = Rig();
        await using var s = await b.Drv.OpenAsync(Conn(), b.Ctx(), CancellationToken.None);
        await s.StartAsync(CancellationToken.None);
        var t = Temp(s, 0);
        await WaitJacket(t, 25.0);
        int Writes() { lock (b.Io.Requests) return b.Io.Requests.Count(r => r.StartsWith("写")); }
        var before = Writes();

        await t.SetTargetAsync(new TempTarget(26.5), CancellationToken.None);   // 差 1.5 K < 死区 2 K
        await ((DuoSession)s).PollOnceAsync(CancellationToken.None);
        await s.StopAsync(CancellationToken.None);

        Assert.False(b.Io.Coils[0]);
        Assert.Equal(before, Writes());                  // 一条写继电器的帧都没发
        Assert.Equal(26_50000, b.Rd.Get(1, "TG"));       // 目标照常下发，只是不换挡
    }

    [Fact]
    public async Task 默认不启用TEC加热_没有IO8R_升温目标直接拒绝_理由提到开关()
    {
        var b = Rig(withIo: false);
        await using var s = await b.Drv.OpenAsync(Conn(), b.Ctx(), CancellationToken.None);
        await s.StartAsync(CancellationToken.None);
        var t = Temp(s, 0);
        await WaitJacket(t, 25.0);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => t.SetTargetAsync(new TempTarget(60), CancellationToken.None));

        Assert.Contains("TEC 加热", ex.Message);         // 说清是哪个开关关着
        Assert.Contains("没配 IO8R", ex.Message);
        Assert.NotEqual(60_00000, b.Rd.Get(1, "TG"));    // 拒绝了就不留下发痕迹

        // 降温照样能下发：冷源本来就是 TEC，跟这个开关无关
        await t.SetTargetAsync(new TempTarget(10), CancellationToken.None);
        Assert.Equal(10_00000, b.Rd.Get(1, "TG"));
        await s.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task 默认不启用TEC加热_温度自己漂出死区_采集循环把它切到电加热()
    {
        var b = Rig();
        await using var s = await b.Drv.OpenAsync(Conn(), b.Ctx(), CancellationToken.None);
        await s.StartAsync(CancellationToken.None);
        var t = Temp(s, 0);
        await WaitJacket(t, 25.0);

        // 先在 TEC 侧做一次降温：目标 10、夹套 25
        await t.SetTargetAsync(new TempTarget(10), CancellationToken.None);
        Assert.False(b.Io.Coils[0]);

        // 夹套一路凉到 5 ℃，比目标低了 5 K——现在缺的是热，采集循环自己换挡
        b.Rd.Set(1, "TCADJTEMP", 5_00000);
        await WaitJacket(t, 5.0);
        await ((DuoSession)s).PollOnceAsync(CancellationToken.None);
        await s.StopAsync(CancellationToken.None);

        Assert.True(b.Io.Coils[0]);
        Assert.Equal(1, b.Rd.Get(1, "ENABLE"));
    }

    [Fact]
    public async Task 釜温已经高过目标_就不许再去接电加热棒()
    {
        var b = Rig();
        await using var s = await b.Drv.OpenAsync(Conn(), b.Ctx(), CancellationToken.None);
        await s.StartAsync(CancellationToken.None);
        var t = Temp(s, 0);
        b.Rd.Set(1, "TCADJTEMP", 80_00000);               // 夹套 80
        await WaitJacket(t, 80.0);
        Feed(s, 1, 95.0);                                  // 釜里 95 ℃——放热已经起来了

        // 「恒温保持」下发的是下发那一刻的釜温；放热让 Tr 跑到目标上面去之后，
        // 光看夹套会判成「要升温」（88 > 80+2），一头把电加热棒接上——
        // 往一个正在放热的釜里补热。这一条硬否决就是拦它的
        await t.SetTargetAsync(new TempTarget(88), CancellationToken.None);
        await ((DuoSession)s).PollOnceAsync(CancellationToken.None);
        await s.StopAsync(CancellationToken.None);

        Assert.False(b.Io.Coils[0]);                       // 留在 TEC 侧
        Assert.Equal(88_00000, b.Rd.Get(1, "TG"));         // 目标照常下发，只是不接加热棒
    }

    [Fact]
    public async Task 夹套读不到就不动继电器_不拿釜温的残值去扳它()
    {
        var b = Rig();
        b.Rd.Set(1, "TCADJTEMP", TecControl.Core.Protocol.TecScale.SensorNotConnectedRaw);   // TC1 没接传感器 → NaN
        await using var s = await b.Drv.OpenAsync(Conn(), b.Ctx(), CancellationToken.None);
        await s.StartAsync(CancellationToken.None);
        Feed(s, 1, 25.0);                                  // 釜温有（可能已经是残值）

        await Temp(s, 0).SetTargetAsync(new TempTarget(60), CancellationToken.None);
        await ((DuoSession)s).PollOnceAsync(CancellationToken.None);
        await s.StopAsync(CancellationToken.None);

        // 判不了方向就别扳继电器，尤其别扳向加热侧——夹套实际多少温度没人知道
        Assert.False(b.Io.Coils[0]);
    }

    [Fact]
    public async Task 切换写继电器失败_下一拍接着切_输出还接得回来()
    {
        var b = Rig();
        await using var s = await b.Drv.OpenAsync(Conn(), b.Ctx(), CancellationToken.None);
        await s.StartAsync(CancellationToken.None);
        var t = Temp(s, 0);
        await WaitJacket(t, 25.0);

        await t.SetTargetAsync(new TempTarget(60), CancellationToken.None);   // 升温 → 电加热
        Assert.True(b.Io.Coils[0]);
        Assert.Equal(1, b.Rd.Get(1, "ENABLE"));

        // 到温之后夹套漂过死区，该回 TEC 了——可这一笔写继电器撞上总线不应答。
        // 切换序列在 ① 关输出之后抛出，输出停在关闭状态
        b.Rd.Set(1, "TCADJTEMP", 63_00000);
        await WaitJacket(t, 63.0);
        b.Io.Mute = true;
        await ((DuoSession)s).PollOnceAsync(CancellationToken.None);
        Assert.Equal(0, b.Rd.Get(1, "ENABLE"));            // 输出确实被关掉了

        // 总线恢复，下一拍重试成功：**输出必须接得回来**。
        // 从前这里看的是设备上的 ENABLE，上一笔失败把它留在 0，于是永远判成
        // 「没人要控温」，一次总线抖动就把通道静悄悄停了
        b.Io.Mute = false;
        await ((DuoSession)s).PollOnceAsync(CancellationToken.None);
        await s.StopAsync(CancellationToken.None);

        Assert.False(b.Io.Coils[0]);                       // 回到 TEC 侧
        Assert.Equal(1, b.Rd.Get(1, "ENABLE"));            // 控温继续
        Assert.Equal(60_00000, b.Rd.Get(1, "TG"));
    }

    [Fact]
    public async Task 恒温保持下发的自指目标_不把继电器拽到加热棒侧()
    {
        var b = Rig();
        await using var s = await b.Drv.OpenAsync(Conn(), b.Ctx(), CancellationToken.None);
        await s.StartAsync(CancellationToken.None);
        var t = Temp(s, 0);
        b.Rd.Set(1, "TCADJTEMP", 80_00000);
        await WaitJacket(t, 80.0);
        Feed(s, 1, 88.0);                                  // 釜里 88，夹套 80：放热已经起来了

        // 「恒温保持」下发的就是下发那一刻的釜温，跟目标**严格相等**——
        // 光看夹套会判成「要升温」（88 > 80+2），一头把电加热棒接上。
        // 这一档必须也挡住，不能只挡「已经超出一个死区」那一档
        await t.SetTargetAsync(new TempTarget(t.CurrentReactor), CancellationToken.None);
        await ((DuoSession)s).PollOnceAsync(CancellationToken.None);
        await s.StopAsync(CancellationToken.None);

        Assert.False(b.Io.Coils[0]);
        Assert.Equal(88_00000, b.Rd.Get(1, "TG"));         // 目标照常下发
    }

    [Fact]
    public async Task 蒸回流跟随也要守最短间隔_不跟着Tr抖()
    {
        var b = Rig();
        await using var s = await b.Drv.OpenAsync(Conn(), b.Ctx(), CancellationToken.None);
        await s.StartAsync(CancellationToken.None);
        var t = Temp(s, 0);
        await WaitJacket(t, 25.0);

        Feed(s, 1, 40.0);                                  // 目标 45 > 25+2 → 切电加热
        await Reflux(s, 0).StartAsync(5, 120, CancellationToken.None);
        Assert.True(b.Io.Coils[0]);

        // 夹套一路被顶到 60：目标 45 比它低一个死区以上 → 回 TEC（制冷方向不等）
        b.Rd.Set(1, "TCADJTEMP", 60_00000);
        await WaitJacket(t, 60.0);
        await ((DuoSession)s).PollOnceAsync(CancellationToken.None);
        Assert.False(b.Io.Coils[0]);

        // Tr 一抖又要升温了。跟随环是唯一每拍自动改目标的环路，它也得守最短间隔——
        // 不守的话这条路会绕开防抖，继电器跟着 Tr 的噪声一拍一切
        var writes = b.Io.Requests.Count(r => r.StartsWith("写DO"));
        Feed(s, 1, 70.0);                                  // 目标 75 > 60+2
        await ((DuoSession)s).PollOnceAsync(CancellationToken.None);
        Assert.False(b.Io.Coils[0]);
        Assert.Equal(writes, b.Io.Requests.Count(r => r.StartsWith("写DO")));   // 一条都没发

        b.Now = b.Now.AddSeconds(31);
        Feed(s, 1, 70.0);                                  // 钟往前拨过了新鲜度窗，Tr 要重新喂
        await ((DuoSession)s).PollOnceAsync(CancellationToken.None);
        await s.StopAsync(CancellationToken.None);
        Assert.True(b.Io.Coils[0]);                        // 等够了才切
        Assert.True(Reflux(s, 0).Active, "跟随不该被这道闸弄停");
    }

    [Fact]
    public async Task 自动切电加热要等最短间隔_但抢冷一刻不等()
    {
        var b = Rig();
        await using var s = await b.Drv.OpenAsync(Conn(), b.Ctx(), CancellationToken.None);
        await s.StartAsync(CancellationToken.None);
        var t = Temp(s, 0);
        await WaitJacket(t, 25.0);

        await t.SetTargetAsync(new TempTarget(60), CancellationToken.None);   // 升温 → 电加热
        Assert.True(b.Io.Coils[0]);

        // 掉头要降温：夹套 25 ℃ 早在滞回线以下，这一拍就该回 TEC——
        // 制冷是安全动作，不受最短间隔约束
        await t.SetTargetAsync(new TempTarget(10), CancellationToken.None);
        await ((DuoSession)s).PollOnceAsync(CancellationToken.None);
        Assert.False(b.Io.Coils[0]);

        // 紧接着夹套掉到 5 ℃，又缺热了。刚切过——自动换挡得等够 30 s
        b.Rd.Set(1, "TCADJTEMP", 5_00000);
        await WaitJacket(t, 5.0);
        await ((DuoSession)s).PollOnceAsync(CancellationToken.None);
        Assert.False(b.Io.Coils[0]);

        b.Now = b.Now.AddSeconds(31);
        await ((DuoSession)s).PollOnceAsync(CancellationToken.None);
        await s.StopAsync(CancellationToken.None);
        Assert.True(b.Io.Coils[0]);
    }

    [Fact]
    public async Task 停控的通道不会被自动切到电加热_安全停机落回的TEC侧留得住()
    {
        var b = Rig();
        await using var s = await b.Drv.OpenAsync(Conn(), b.Ctx(), CancellationToken.None);
        await s.StartAsync(CancellationToken.None);
        var t = Temp(s, 0);
        await WaitJacket(t, 25.0);

        await t.SetTargetAsync(new TempTarget(120), CancellationToken.None);
        Assert.True(b.Io.Coils[0]);

        // 安全停机：关输出、继电器落回 TEC 侧
        await s.SafeStopAsync(0, CancellationToken.None);
        Assert.False(b.Io.Coils[0]);
        Assert.Equal(0, b.Rd.Get(1, "ENABLE"));

        // 再跑几拍：目标还留在 120（TG 没清），但输出是关的——
        // 不许有人替操作人把它又切回电加热、更不许把输出打开。
        // **每一拍都把钟推过最短间隔**：不推的话这条测试是被 MinDwell 挡绿的，
        // 真正要验的那道「没人要控温就不切电加热」的守卫一次都没被走到
        for (var i = 0; i < 3; i++)
        {
            b.Now = b.Now.AddSeconds(31);
            await ((DuoSession)s).PollOnceAsync(CancellationToken.None);
            Assert.False(b.Io.Coils[0]);
            Assert.Equal(0, b.Rd.Get(1, "ENABLE"));
        }
        await s.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task 没有IO8R_温度漂到要升温_把话说出来而不是闷着()
    {
        var b = Rig(withIo: false);
        await using var s = await b.Drv.OpenAsync(Conn(), b.Ctx(), CancellationToken.None);
        await s.StartAsync(CancellationToken.None);
        var t = Temp(s, 0);
        await WaitJacket(t, 25.0);

        // 开机就该把「这台只能制冷」讲清楚，不必等配方撞上去
        lock (b.Logs) Assert.Contains(b.Logs, l => l.Text.Contains("只能制冷"));

        await t.SetTargetAsync(new TempTarget(10), CancellationToken.None);   // 降温：允许
        b.Rd.Set(1, "TCADJTEMP", 5_00000);                                    // 夹套凉过头，现在缺热
        await WaitJacket(t, 5.0);
        await ((DuoSession)s).PollOnceAsync(CancellationToken.None);
        await ((DuoSession)s).PollOnceAsync(CancellationToken.None);
        await s.StopAsync(CancellationToken.None);

        // 这条提醒从前写在 IO8R 守卫块里，没有 IO8R 时根本执行不到——正是最该说的那种机器
        lock (b.Logs)
        {
            Assert.Single(b.Logs, l => l.Text.Contains("需要升温但"));
            Assert.Contains(b.Logs, l => l.Text.Contains("TEC 加热"));
        }
    }

    [Fact]
    public async Task 要降温却因为夹套还烫接不回TEC_按段报一次没有制冷能力()
    {
        var b = Rig();
        await using var s = await b.Drv.OpenAsync(Conn(), b.Ctx(), CancellationToken.None);
        await s.StartAsync(CancellationToken.None);
        var t = Temp(s, 0);
        await WaitJacket(t, 25.0);

        await t.SetTargetAsync(new TempTarget(120), CancellationToken.None);   // 上电加热
        b.Rd.Set(1, "TCADJTEMP", 95_00000);                                    // 夹套 95 ℃，高过回切线 85
        await WaitJacket(t, 95.0);
        await t.SetTargetAsync(new TempTarget(50), CancellationToken.None);     // 抢冷
        await ((DuoSession)s).PollOnceAsync(CancellationToken.None);
        await ((DuoSession)s).PollOnceAsync(CancellationToken.None);

        Assert.True(b.Io.Coils[0]);                       // 还在电加热侧：这条保护不让步
        lock (b.Logs) Assert.Single(b.Logs, l => l.Level == "error" && l.Text.Contains("没有制冷能力"));

        // 凉到 84 就能接回 TEC，报过的那一段也翻篇
        b.Rd.Set(1, "TCADJTEMP", 84_00000);
        await WaitJacket(t, 84.0);
        await ((DuoSession)s).PollOnceAsync(CancellationToken.None);
        await s.StopAsync(CancellationToken.None);
        Assert.False(b.Io.Coils[0]);
    }

    [Fact]
    public async Task 停控之后继电器落回TEC侧_不等夹套凉_与安全停机同一句话()
    {
        var b = Rig();
        await using var s = await b.Drv.OpenAsync(Conn(), b.Ctx(), CancellationToken.None);
        await s.StartAsync(CancellationToken.None);
        var t = Temp(s, 0);
        await WaitJacket(t, 25.0);

        await t.SetTargetAsync(new TempTarget(120), CancellationToken.None);
        b.Rd.Set(1, "TCADJTEMP", 120_00000);              // 夹套 120 ℃，远高于回切线
        await WaitJacket(t, 120.0);
        Assert.True(b.Io.Coils[0]);

        await t.StopAsync(CancellationToken.None);         // 操作人停控：输出关，TG 还留着
        await ((DuoSession)s).PollOnceAsync(CancellationToken.None);
        await s.StopAsync(CancellationToken.None);

        // 输出已经关了，接回 TEC 不带载——SafeStopAsync 在同样的状态下就是直接断的，
        // 这条路不该反过来把接触器一直吸着等夹套自己凉
        Assert.False(b.Io.Coils[0]);
        Assert.Equal(0, b.Rd.Get(1, "ENABLE"));            // 而且绝不顺手把输出打开
    }

    [Fact]
    public async Task 回切TEC不会把停掉的控温又打开()
    {
        var b = Rig();
        await using var s = await b.Drv.OpenAsync(Conn(), b.Ctx(TecOn()), CancellationToken.None);
        await s.StartAsync(CancellationToken.None);
        var t = Temp(s, 0);
        await WaitJacket(t, 25.0);

        await t.SetTargetAsync(new TempTarget(120), CancellationToken.None);   // 切到电加热
        Assert.True(b.Io.Coils[0]);
        await t.StopAsync(CancellationToken.None);                             // 操作人停控（TG 还留着）
        Assert.Equal(0, b.Rd.Get(1, "ENABLE"));

        // 夹套凉下来，采集循环把继电器落回 TEC 侧——这是好事，
        // 但绝不能顺手把 ENABLE 打开：没人要它继续控温
        b.Rd.Set(1, "TCADJTEMP", 80_00000);
        await WaitJacket(t, 80.0);
        await ((DuoSession)s).PollOnceAsync(CancellationToken.None);
        await s.StopAsync(CancellationToken.None);

        Assert.False(b.Io.Coils[0]);
        Assert.Equal(0, b.Rd.Get(1, "ENABLE"));
    }

    [Fact]
    public async Task 属性栏上把TEC加热打开_当场生效_不用断开重连()
    {
        var b = Rig();
        var cfg = new ParameterSet();                    // 出厂默认：不启用
        await using var s = await b.Drv.OpenAsync(Conn(), b.Ctx(cfg), CancellationToken.None);
        await s.StartAsync(CancellationToken.None);
        var t = Temp(s, 0);
        await WaitJacket(t, 25.0);

        await t.SetTargetAsync(new TempTarget(60), CancellationToken.None);
        Assert.True(b.Io.Coils[0]);                      // 不启用 → 升温走电加热

        // 属性栏上把开关扳到「启用」。改的就是这一份 ParameterSet 本体，
        // 会话每拍重读——不用把设备断开重连
        cfg[DualStationDriver.Fields.TecHeat] = "启用";
        await ((DuoSession)s).PollOnceAsync(CancellationToken.None);
        await s.StopAsync(CancellationToken.None);

        // 60 ℃ 没过 90 的阈值，夹套也早凉在 85 以下：这一拍就该把 TEC 接回来
        Assert.False(b.Io.Coils[0]);
        Assert.Equal(1, b.Rd.Get(1, "ENABLE"));
        lock (b.Logs) Assert.Contains(b.Logs, l => l.Text.Contains("「TEC 加热」改为启用"));
    }

    [Fact]
    public async Task 默认不启用TEC加热_没有电加热_蒸回流开不了_理由写明()
    {
        var b = Rig(withIo: false);
        await using var s = await b.Drv.OpenAsync(Conn(), b.Ctx(), CancellationToken.None);
        Feed(s, 1, 60.0);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Reflux(s, 0).StartAsync(5, 120, CancellationToken.None));

        Assert.Contains("TEC 加热", ex.Message);
        Assert.False(Reflux(s, 0).Active);
    }

    [Fact]
    public async Task 默认不启用TEC加热_蒸回流全程走电加热()
    {
        var b = Rig();
        await using var s = await b.Drv.OpenAsync(Conn(), b.Ctx(), CancellationToken.None);
        await s.StartAsync(CancellationToken.None);
        await WaitJacket(Temp(s, 0), 25.0);

        Feed(s, 1, 40.0);                                 // Tr+ΔT = 45，离 90 的阈值还远
        await Reflux(s, 0).StartAsync(5, 120, CancellationToken.None);
        await s.StopAsync(CancellationToken.None);

        Assert.True(b.Io.Coils[0]);                       // 照样切电加热：升温就得走它
        Assert.Equal(45_00000, b.Rd.Get(1, "TG"));
        Assert.True(Reflux(s, 0).Active);
    }

    // ── 安全停 ───────────────────────────────────────────────────────

    [Fact]
    public async Task SafeStop_关输出断继电器_只动本工位()
    {
        var b = Rig();
        await using var s = await b.Drv.OpenAsync(Conn(), b.Ctx(TecOn()), CancellationToken.None);
        await Temp(s, 0).SetTargetAsync(new TempTarget(120), CancellationToken.None);
        await Temp(s, 1).SetTargetAsync(new TempTarget(120), CancellationToken.None);

        var notes = await s.SafeStopAsync(0, CancellationToken.None);

        Assert.Equal(0, b.Rd.Get(1, "ENABLE"));
        Assert.False(b.Io.Coils[0]);
        Assert.Equal(1, b.Rd.Get(2, "ENABLE"));          // B 照跑
        Assert.True(b.Io.Coils[1]);
        Assert.Contains(notes!, n => n.Contains("TEC 侧"));
    }

    // ── 蒸回流（夹套跟随环，长在采集循环里） ─────────────────────────

    private static IRefluxControl Reflux(IDeviceSession s, int well)
        => s.CapabilitiesOf(well).OfType<IRefluxControl>().Single();

    private static void Feed(IDeviceSession s, int channel, double tr)
        => ((IExternalReactorTemp)s).FeedReactor(channel, tr, Quality.Good);

    [Fact]
    public async Task 跟随每拍把夹套目标写成Tr加ΔT_限速走SPEED_目标没动不磨总线()
    {
        var b = Rig();
        await using var s = await b.Drv.OpenAsync(Conn(), b.Ctx(), CancellationToken.None);
        var r = Reflux(s, 0);

        Feed(s, 1, 30.0);
        await r.StartAsync(5, 120, CancellationToken.None);

        Assert.True(r.Active);
        Assert.Equal(35_00000, b.Rd.Get(1, "TG"));           // 第一拍不等采集循环
        Assert.Equal(1, b.Rd.Get(1, "ENABLE"));
        Assert.True(b.Rd.Get(1, "SPEED") > 0, "跟随写 TG 必须带 SPEED 限速，不是阶跃");

        // Tr 升到 40 → 下一拍目标追到 45
        Feed(s, 1, 40.0);
        await ((DuoSession)s).PollOnceAsync(CancellationToken.None);
        Assert.Equal(45_00000, b.Rd.Get(1, "TG"));

        // Tr 只动 0.05 ℃ → 目标变化 < 0.1，不写寄存器
        var writes = b.Rd.Commands.Count(c => c.Contains("TC1:TG="));
        Feed(s, 1, 40.05);
        await ((DuoSession)s).PollOnceAsync(CancellationToken.None);
        Assert.Equal(writes, b.Rd.Commands.Count(c => c.Contains("TC1:TG=")));
    }

    [Fact]
    public async Task 跟随目标钳在夹套上限之下()
    {
        var b = Rig();
        await using var s = await b.Drv.OpenAsync(Conn(), b.Ctx(), CancellationToken.None);

        Feed(s, 1, 45.0);
        await Reflux(s, 0).StartAsync(10, 50, CancellationToken.None);   // Tr+ΔT = 55 > maxTj 50

        Assert.Equal(50_00000, b.Rd.Get(1, "TG"));
    }

    [Fact]
    public async Task 跟随越过阈值_走全套热源切换序列()
    {
        var b = Rig();
        await using var s = await b.Drv.OpenAsync(Conn(), b.Ctx(TecOn()), CancellationToken.None);
        // 先让跟随在阈值以内跑起来（Tr 80 → 目标 85 ≤ 90，不动继电器），
        // 输出这时是真开着的，后面那一笔切换里的「先关输出」才看得见
        Feed(s, 1, 80.0);
        await Reflux(s, 0).StartAsync(5, 120, CancellationToken.None);
        Assert.False(b.Io.Coils[0]);
        Assert.Equal(1, b.Rd.Get(1, "ENABLE"));
        var mark = b.Rd.Commands.Count;

        Feed(s, 1, 87.0);                                     // 87 + 5 = 92 > 90
        await ((DuoSession)s).PollOnceAsync(CancellationToken.None);

        Assert.True(b.Io.Coils[0]);                           // 已切电加热
        Assert.Equal(92_00000, b.Rd.Get(1, "TG"));
        // 带载切继电器 = 触点拉弧：ENABLE=0 必须出现在高温目标之前
        var cmds = b.Rd.Commands;
        var off = cmds.FindIndex(mark, c => c.Contains("TC1:ENABLE=0"));
        var tg = cmds.FindLastIndex(c => c.Contains("TC1:TG=9200000"));
        Assert.True(off >= 0 && off < tg, $"先关输出再下高温目标（off={off}, tg={tg}）");
    }

    [Fact]
    public async Task 没配IO8R_跟随撞上阈值就停_并说清原因()
    {
        var b = Rig(withIo: false);
        await using var s = await b.Drv.OpenAsync(Conn(), b.Ctx(TecOn()), CancellationToken.None);
        var r = Reflux(s, 0);

        Feed(s, 1, 60.0);
        await r.StartAsync(5, 120, CancellationToken.None);   // 65 ≤ 90，先能跟
        Assert.True(r.Active);

        Feed(s, 1, 88.0);                                     // 93 > 90 且没有电加热
        await ((DuoSession)s).PollOnceAsync(CancellationToken.None);

        Assert.False(r.Active);
        lock (b.Logs) Assert.Contains(b.Logs, l => l.Level == "error" && l.Text.Contains("蒸回流已停"));
        Assert.Equal(65_00000, b.Rd.Get(1, "TG"));            // 目标驻留，没把 93 硬写上去
    }

    [Fact]
    public async Task Tr无效跟随停_目标驻留不追残值()
    {
        var b = Rig();
        await using var s = await b.Drv.OpenAsync(Conn(), b.Ctx(), CancellationToken.None);
        var r = Reflux(s, 0);

        Feed(s, 1, 30.0);
        await r.StartAsync(5, 120, CancellationToken.None);
        Assert.Equal(35_00000, b.Rd.Get(1, "TG"));

        ((IExternalReactorTemp)s).FeedReactor(1, 810.0, Quality.Bad);    // 探头断线残值
        await ((DuoSession)s).PollOnceAsync(CancellationToken.None);

        Assert.False(r.Active);
        Assert.Equal(35_00000, b.Rd.Get(1, "TG"));            // 停在最后一次下发的值上
        lock (b.Logs) Assert.Contains(b.Logs, l => l.Level == "error" && l.Text.Contains("Tr 无效"));

        // 探头又活了也不自动续跟——安全恢复要人（或配方）明确再开
        Feed(s, 1, 60.0);
        await ((DuoSession)s).PollOnceAsync(CancellationToken.None);
        Assert.Equal(35_00000, b.Rd.Get(1, "TG"));
    }

    [Fact]
    public async Task 没有Tr读数_跟随根本开不了()
    {
        var b = Rig();
        await using var s = await b.Drv.OpenAsync(Conn(), b.Ctx(), CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Reflux(s, 0).StartAsync(5, 120, CancellationToken.None));

        Assert.Contains("Tr", ex.Message);
        Assert.Equal(0, b.Rd.Get(1, "ENABLE"));               // 拒绝了就不留下发痕迹
    }

    [Fact]
    public async Task SafeStop清跟随_之后没有谁再把目标写回去()
    {
        var b = Rig();
        await using var s = await b.Drv.OpenAsync(Conn(), b.Ctx(), CancellationToken.None);
        var r = Reflux(s, 0);

        Feed(s, 1, 30.0);
        await r.StartAsync(5, 120, CancellationToken.None);

        var notes = await s.SafeStopAsync(0, CancellationToken.None);

        Assert.False(r.Active);
        Assert.Contains(notes!, n => n.Contains("蒸回流"));
        Assert.Equal(0, b.Rd.Get(1, "ENABLE"));

        // 之后哪怕 Tr 还在动，采集循环也不许把目标写回去、把输出重新打开
        Feed(s, 1, 60.0);
        await ((DuoSession)s).PollOnceAsync(CancellationToken.None);
        await ((DuoSession)s).PollOnceAsync(CancellationToken.None);
        Assert.Equal(35_00000, b.Rd.Get(1, "TG"));
        Assert.Equal(0, b.Rd.Get(1, "ENABLE"));
    }

    [Fact]
    public async Task 明确下发新目标_跟随退位不再追()
    {
        var b = Rig();
        await using var s = await b.Drv.OpenAsync(Conn(), b.Ctx(), CancellationToken.None);
        var r = Reflux(s, 0);

        Feed(s, 1, 30.0);
        await r.StartAsync(5, 120, CancellationToken.None);
        Assert.True(r.Active);

        await Temp(s, 0).SetTargetAsync(new TempTarget(40), CancellationToken.None);

        Assert.False(r.Active);
        Feed(s, 1, 50.0);
        await ((DuoSession)s).PollOnceAsync(CancellationToken.None);
        Assert.Equal(40_00000, b.Rd.Get(1, "TG"));            // 守着新目标，不追 Tr+ΔT
    }

    [Fact]
    public async Task 停跟随目标驻留_输出不动()
    {
        var b = Rig();
        await using var s = await b.Drv.OpenAsync(Conn(), b.Ctx(), CancellationToken.None);
        var r = Reflux(s, 0);

        Feed(s, 1, 30.0);
        await r.StartAsync(5, 120, CancellationToken.None);

        await r.StopAsync(CancellationToken.None);            // 步收尾：停跟随 ≠ 停控温

        Assert.False(r.Active);
        Assert.Equal(35_00000, b.Rd.Get(1, "TG"));
        Assert.Equal(1, b.Rd.Get(1, "ENABLE"));               // 输出还开着，收尾由下一步决定
    }

    // ── 探测 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task 探测汇总主机两条链路_并指路探头()
    {
        var b = Rig();
        var probe = await b.Drv.ProbeAsync(Conn(), CancellationToken.None);
        Assert.True(probe.Success);
        Assert.Contains("RD105", probe.Message);
        Assert.Contains("IO8R", probe.Message);
        Assert.Contains("探头", probe.Message);          // Tr/pH 的口子在探头设备上，指个路
        Assert.Equal(2, probe.DetectedChannels);
    }

    private sealed class Collect(Action<Sample> onNext) : IObserver<Sample>
    {
        public void OnNext(Sample value) => onNext(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
