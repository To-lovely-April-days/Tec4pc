using Tec.Driver.Abi;
using Tec.Drivers.DualStation;
using Tec.Drivers.DualStation.Modbus;
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
    public async Task 真机认领四条温度指令_并能真执行一步控温()
    {
        var b = Rig();
        await using var s = await b.Drv.OpenAsync(Conn(), b.Ctx(), CancellationToken.None);

        // 与仿真同一份执行器——纯真机台面跑含温度步的配方不再报「没有设备认领」
        foreach (var id in new[] { CommandSpecs.Control, CommandSpecs.Gradient,
                                   CommandSpecs.Hold, CommandSpecs.PassiveCool })
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
