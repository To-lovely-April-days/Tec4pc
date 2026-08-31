using Tec.Driver.Abi;
using Tec.Drivers.DualStation;
using Tec.Drivers.DualStation.Modbus;
using Tec.Drivers.DualStation.Yudian;
using Tec.Drivers.Rd105;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 双工位反应主机组合会话（实施第 4 步，需求 §1/§2）。四条链路全是假设备，
/// 盯的都是规约里的硬条款：开机继电器复位 TEC 侧、Tr/pH 按映射发到两个通道、
/// 切换序列先关输出、高目标没 IO8R 诚实拒绝、回切要等夹套凉到阈值−滞回、
/// 反馈没跟上输出保持关闭、SafeStop 断继电器。
/// </summary>
public sealed class DualStationDriverTests
{
    private sealed class Bench
    {
        public FakeRd105Device Rd = new();
        public FakeModbusSlave J7 = new();
        public FakeModbusSlave J4 = new();
        public FakeModbusSlave Io = new();
        public DualStationDriver Drv = null!;
        public List<(string Level, string Text)> Logs = new();

        public DriverContext Ctx(ParameterSet? config = null) => new()
        {
            InstanceId = "DUO1",
            ChannelNumbers = new[] { 1, 2 },
            Config = config ?? new ParameterSet(),
            Simulated = false,
            TimeScale = 1,
            Clock = () => DateTimeOffset.Now,
            Log = (lvl, text) => { lock (Logs) Logs.Add((lvl, text)); }
        };
    }

    /// <summary>接线齐全的一台：J7 两路 Pt100，J4 两路 4~20mA 定标 0.00~14.00，IO8R 在。</summary>
    private static Bench Rig(bool withIo = true, bool withPh = true)
    {
        var b = new Bench();

        b.J7.Regs[384] = 1; b.J7.Regs[385] = 2;              // CH1/CH2 开，其余关
        b.J7.Regs[2048] = 21; b.J7.Regs[2049] = 21;          // Pt100，一位小数
        b.J7.Regs[2128] = 1;

        b.J4.Regs[384] = 1; b.J4.Regs[385] = 2;
        b.J4.Regs[2048] = 51; b.J4.Regs[2049] = 51;          // 4~20mA
        b.J4.Regs[2052] = 0; b.J4.Regs[2056] = 1400;         // 0.00~14.00
        b.J4.Regs[2053] = 0; b.J4.Regs[2057] = 1400;
        b.J4.Regs[2128] = 2;

        b.Drv = new DualStationDriver
        {
            LinksFactory = _ => new DuoLinks
            {
                Rd105 = new Rd105Link(b.Rd),
                TempPort = b.J7,
                TempMod = new YudianClient(new ModbusRtuClient(b.J7, 1, 200)),
                PhPort = withPh ? b.J4 : null,
                PhMod = withPh ? new YudianClient(new ModbusRtuClient(b.J4, 1, 200)) : null,
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

    [Fact]
    public async Task 温度口插了pH表_开机直接拒绝()
    {
        var b = Rig();
        b.J7.Regs[2048] = 51;                    // 组1 竟是 4~20mA——接反了
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => b.Drv.OpenAsync(Conn(), b.Ctx(), CancellationToken.None));
        Assert.Contains("J4", ex.Message);
    }

    [Fact]
    public async Task pH模块坏了_照常开机_pH不发数()
    {
        var b = Rig();
        b.J4.Mute = true;                        // pH 表不应答
        await using var s = await b.Drv.OpenAsync(Conn(), b.Ctx(), CancellationToken.None);

        var got = new List<Sample>();
        using var sub = s.Samples.Subscribe(new Collect(x => { lock (got) got.Add(x); }));
        await ((DuoSession)s).PollOnceAsync(CancellationToken.None);

        lock (got) Assert.DoesNotContain(got, x => x.Tag == "pH");
        lock (b.Logs) Assert.Contains(b.Logs, l => l.Text.Contains("pH 模块打不开"));
    }

    // ── 采集与映射 ───────────────────────────────────────────────────

    [Fact]
    public async Task 一拍采集_Tr与pH按映射发到两个通道_dT用宇电的Tr()
    {
        var b = Rig();
        b.J7.Regs[1536] = 250;                   // CH1 → 工位 A：25.0 ℃
        b.J7.Regs[1537] = 300;                   // CH2 → 工位 B：30.0 ℃
        b.J4.Regs[1536] = 700;                   // pH 7.00
        b.J4.Regs[1537] = 900;                   // pH 9.00
        b.Rd.Set(1, "TCADJTEMP", 24_00000);      // A 夹套 24.0
        b.Rd.Set(2, "TCADJTEMP", 33_00000);      // B 夹套 33.0

        await using var s = await b.Drv.OpenAsync(Conn(), b.Ctx(), CancellationToken.None);
        await s.StartAsync(CancellationToken.None);
        await WaitJacket(Temp(s, 0), 24.0);
        await WaitJacket(Temp(s, 1), 33.0);

        var got = new List<Sample>();
        using var sub = s.Samples.Subscribe(new Collect(x => { lock (got) got.Add(x); }));
        await ((DuoSession)s).PollOnceAsync(CancellationToken.None);
        await s.StopAsync(CancellationToken.None);

        lock (got)
        {
            Assert.Equal(25.0, got.Single(x => x is { Tag: "Tr", Channel: 1 }).Value, 2);
            Assert.Equal(30.0, got.Single(x => x is { Tag: "Tr", Channel: 2 }).Value, 2);
            Assert.Equal(7.0, got.Single(x => x is { Tag: "pH", Channel: 1 }).Value, 2);
            Assert.Equal(9.0, got.Single(x => x is { Tag: "pH", Channel: 2 }).Value, 2);
            Assert.Equal(1.0, got.Single(x => x is { Tag: "dT", Channel: 1 }).Value, 1);   // 25.0 − 24.0
            Assert.Equal(0, got.Single(x => x is { Tag: "heat", Channel: 1 }).Value);      // TEC 侧
        }
        // 判到达吃的是宇电喂进来的釜内温度
        Assert.Equal(25.0, Temp(s, 0).CurrentReactor, 1);
    }

    [Fact]
    public async Task Tr断线_发Bad并且不进控制判据()
    {
        var b = Rig();
        b.J7.Regs[1536] = 8100;                  // 断线残值
        b.J7.Regs[1664] = 0x0100;                // CH1 oral
        await using var s = await b.Drv.OpenAsync(Conn(), b.Ctx(), CancellationToken.None);

        var got = new List<Sample>();
        using var sub = s.Samples.Subscribe(new Collect(x => { lock (got) got.Add(x); }));
        await ((DuoSession)s).PollOnceAsync(CancellationToken.None);

        lock (got) Assert.Equal(Quality.Bad, got.Single(x => x is { Tag: "Tr", Channel: 1 }).Quality);
        Assert.True(double.IsNaN(Temp(s, 0).CurrentReactor));
    }

    // ── 热源切换 ─────────────────────────────────────────────────────

    [Fact]
    public async Task 目标不过阈值_不碰继电器()
    {
        var b = Rig();
        await using var s = await b.Drv.OpenAsync(Conn(), b.Ctx(), CancellationToken.None);
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
        await using var s = await b.Drv.OpenAsync(Conn(), b.Ctx(), CancellationToken.None);

        await Temp(s, 0).SetTargetAsync(new TempTarget(120), CancellationToken.None);

        Assert.True(b.Io.Coils[0]);                      // A 已在电加热侧
        Assert.False(b.Io.Coils[1]);                     // B 没被牵连
        Assert.Equal(120_00000, b.Rd.Get(1, "TG"));
        Assert.Equal(1, b.Rd.Get(1, "ENABLE"));
        // 切继电器之前必须先关输出（带载切 = 触点拉弧）：
        // 命令流里 ENABLE=0 要出现在这次 TG=120 之前
        var cmds = b.Rd.Commands;
        var off = cmds.FindIndex(c => c.Contains("TC1:ENABLE=0"));
        var tg = cmds.FindLastIndex(c => c.Contains("TC1:TG=12000000"));
        Assert.True(off >= 0 && off < tg, $"先关输出再下高温目标（off={off}, tg={tg}）");
    }

    [Fact]
    public async Task 没配IO8R_高目标诚实拒绝()
    {
        var b = Rig(withIo: false);
        await using var s = await b.Drv.OpenAsync(Conn(), b.Ctx(), CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Temp(s, 0).SetTargetAsync(new TempTarget(120), CancellationToken.None));

        Assert.Contains("阈值", ex.Message);
        Assert.NotEqual(120_00000, b.Rd.Get(1, "TG"));   // 拒绝了就不留下发痕迹
    }

    [Fact]
    public async Task 回切要等夹套凉到阈值减滞回()
    {
        var b = Rig();
        await using var s = await b.Drv.OpenAsync(Conn(), b.Ctx(), CancellationToken.None);
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
        var cfg = ParameterSet.Of((DualStationDriver.Fields.Feedback, "有"));
        await using var s = await b.Drv.OpenAsync(Conn(), b.Ctx(cfg), CancellationToken.None);
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
        var cfg = ParameterSet.Of((DualStationDriver.Fields.Feedback, "有"));
        await using var s = await b.Drv.OpenAsync(Conn(), b.Ctx(cfg), CancellationToken.None);

        await Temp(s, 0).SetTargetAsync(new TempTarget(120), CancellationToken.None);

        Assert.True(b.Io.Coils[0]);
        Assert.Equal(1, b.Rd.Get(1, "ENABLE"));
        lock (b.Logs) Assert.Contains(b.Logs, l => l.Text.Contains("反馈已核实"));
    }

    // ── 安全停 ───────────────────────────────────────────────────────

    [Fact]
    public async Task SafeStop_关输出断继电器_只动本工位()
    {
        var b = Rig();
        await using var s = await b.Drv.OpenAsync(Conn(), b.Ctx(), CancellationToken.None);
        await Temp(s, 0).SetTargetAsync(new TempTarget(120), CancellationToken.None);
        await Temp(s, 1).SetTargetAsync(new TempTarget(120), CancellationToken.None);

        var notes = await s.SafeStopAsync(0, CancellationToken.None);

        Assert.Equal(0, b.Rd.Get(1, "ENABLE"));
        Assert.False(b.Io.Coils[0]);
        Assert.Equal(1, b.Rd.Get(2, "ENABLE"));          // B 照跑
        Assert.True(b.Io.Coils[1]);
        Assert.Contains(notes!, n => n.Contains("TEC 侧"));
    }

    // ── 探测 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task 探测把四个模块的状态汇总成一句话()
    {
        var b = Rig();
        var probe = await b.Drv.ProbeAsync(Conn(), CancellationToken.None);
        Assert.True(probe.Success);
        Assert.Contains("RD105", probe.Message);
        Assert.Contains("温度模块", probe.Message);
        Assert.Contains("pH 模块", probe.Message);
        Assert.Contains("IO8R", probe.Message);
        Assert.Equal(2, probe.DetectedChannels);
    }

    [Fact]
    public async Task 温度模块不应答_探测失败但把话说全()
    {
        var b = Rig();
        b.J7.Mute = true;
        var probe = await b.Drv.ProbeAsync(Conn(), CancellationToken.None);
        Assert.False(probe.Success);                     // Tr 是根，没有它整机不算通
        Assert.Contains("RD105", probe.Message);         // 但别的模块的情况也要说
    }

    private sealed class Collect(Action<Sample> onNext) : IObserver<Sample>
    {
        public void OnNext(Sample value) => onNext(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
