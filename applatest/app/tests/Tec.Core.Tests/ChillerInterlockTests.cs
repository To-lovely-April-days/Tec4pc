using TecControl.Core.Comm;
using Tec.Driver.Abi;
using Tec.Drivers.DualStation;
using Tec.Drivers.Rd105;
using Tec.Drivers.Rd105.Modbus;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 冷却水阀跟冷水机联动（用户定的标准开关顺序）：开水先开阀（IO8R DO4）、再开冷水机；
/// 关水先停冷水机，等它真停了（运行状态 = 空闲）再关阀——面板开关、自动联动、安全停机、开机、断开设备都按这个走。
/// </summary>
public sealed class ChillerInterlockTests
{
    private sealed class Bench
    {
        public FakeRd105Device Rd = new();
        public FakeModbusSlave Io = new();
        public FakeChiller Chiller = new();
        public DualStationDriver Drv = null!;
        public List<(string Level, string Text)> Logs = new();
        public DateTimeOffset Now = DateTimeOffset.Now;
        /// <summary>阀和冷水机的动作按先后串起来：「阀开」「开机」「关机」「阀关」。</summary>
        public List<string> Seq = new();

        public DriverContext Ctx(ParameterSet? config = null, bool reconnecting = false)
        {
            config ??= new ParameterSet();
            if (!config.Has(Rd105TecDriver.FieldControl)) config[Rd105TecDriver.FieldControl] = Rd105TecDriver.ControlDevice;
            if (!config.Has(DualStationDriver.Fields.WaterLead)) config[DualStationDriver.Fields.WaterLead] = 0d;
            return new DriverContext
            {
                InstanceId = "DUO1",
                ChannelNumbers = new[] { 1, 2 },
                Config = config,
                Simulated = false,
                TimeScale = 1,
                Reconnecting = reconnecting,
                Clock = () => Now,
                Log = (lvl, text) => { lock (Logs) Logs.Add((lvl, text)); }
            };
        }
    }

    private static Bench Rig(bool withChiller = true)
    {
        var b = new Bench();
        var io = new SeqTransport(b.Io, b, isChiller: false);
        var ch = new SeqTransport(b.Chiller, b, isChiller: true);
        b.Drv = new DualStationDriver
        {
            LinksFactory = _ => new DuoLinks
            {
                Rd105 = new Rd105Link(b.Rd),
                IoPort = io,
                Io = new Io8rClient(new ModbusRtuClient(io, 1, 200)),
                ChillerPort = withChiller ? ch : null,
                Chiller = withChiller ? new ChillerClient(new ModbusRtuClient(ch, 1, 200)) : null
            }
        };
        return b;
    }

    private static ParameterSet Conn() => ParameterSet.Of(
        (Rd105TecDriver.FieldPeriod, 200d),
        (DualStationDriver.Fields.Tick, 5000d));

    private static ParameterSet TecOn() => ParameterSet.Of((DualStationDriver.Fields.TecHeat, "启用"));

    private static ICoolingWaterValve Water(IDeviceSession s) => s.CapabilitiesOf(0).OfType<ICoolingWaterValve>().Single();
    private static ITemperatureControl Temp(IDeviceSession s, int well) => s.CapabilitiesOf(well).OfType<ITemperatureControl>().Single();
    private static Task Tick(IDeviceSession s) => ((DuoSession)s).PollOnceAsync(CancellationToken.None);

    /// <summary>等内层 RD105 轮询把夹套温度端上来（双向挡起步按方向，要吃实测 Tj）。NaN 也算没到。</summary>
    private static async Task WaitJacket(ITemperatureControl t, double value)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && !(Math.Abs(t.CurrentJacket - value) <= 0.01))
            await Task.Delay(30);
        Assert.Equal(value, t.CurrentJacket, 1);
    }

    private static async Task<IDeviceSession> Open(Bench b, ParameterSet? cfg = null, bool reconnecting = false)
    {
        var s = await b.Drv.OpenAsync(Conn(), b.Ctx(cfg, reconnecting), CancellationToken.None);
        ((DuoSession)s).ChillerStopWaitOnDispose = TimeSpan.FromMilliseconds(600);
        b.Seq.Clear();
        return s;
    }

    // ── 面板开关 ─────────────────────────────────────────────────────

    [Fact]
    public async Task 开冷却水_先开阀_再开冷水机()
    {
        var b = Rig();
        await using var s = await Open(b);

        await Water(s).SetAsync(true, CancellationToken.None);

        Assert.True(b.Io.Coils[4]);
        Assert.Equal(2, b.Chiller.State);
        Assert.Equal(new[] { "阀开", "开机" }, b.Seq);
    }

    [Fact]
    public async Task 关冷却水_先停冷水机_延时停机期间阀不关_停稳了才关()
    {
        var b = Rig();
        await using var s = await Open(b);
        await Water(s).SetAsync(true, CancellationToken.None);
        b.Seq.Clear();

        await Water(s).SetAsync(false, CancellationToken.None);

        Assert.Equal(new[] { "关机" }, b.Seq);            // 先停冷水机
        Assert.Equal(3, b.Chiller.State);                 // 延时停机
        Assert.True(b.Io.Coils[4]);                       // 冷水机还没停稳：阀不关
        Assert.True(Water(s).IsOpen);
        Assert.Contains("停稳后关阀", Water(s).Status);

        await Tick(s);
        Assert.True(b.Io.Coils[4]);                       // 还在延时停机：照样不关

        b.Chiller.Settle();
        await Tick(s);
        Assert.False(b.Io.Coils[4]);                      // 停稳了才关阀
        Assert.False(Water(s).IsOpen);
        Assert.Equal(new[] { "关机", "阀关" }, b.Seq);
        Assert.DoesNotContain("停稳后关阀", Water(s).Status);
    }

    [Fact]
    public async Task 冷水机本来就停着_关冷却水当场关阀()
    {
        var b = Rig();
        await using var s = await Open(b);
        await Water(s).SetAsync(true, CancellationToken.None);
        b.Chiller.StopsAtOnce = true;

        await Water(s).SetAsync(false, CancellationToken.None);

        Assert.False(b.Io.Coils[4]);
        Assert.Equal(0, b.Chiller.State);
    }

    [Fact]
    public async Task 冷水机读不到_关水时阀一直开着_读到停稳了才关()
    {
        var b = Rig();
        await using var s = await Open(b);
        await Water(s).SetAsync(true, CancellationToken.None);
        b.Chiller.Mute = true;

        await Water(s).SetAsync(false, CancellationToken.None);
        b.Now += TimeSpan.FromSeconds(30);
        await Tick(s);
        Assert.True(b.Io.Coils[4]);                       // 确认不了冷水机停没停：阀不关
        lock (b.Logs) Assert.Contains(b.Logs, l => l.Text.Contains("冷水机读不到"));

        b.Chiller.Mute = false;
        b.Chiller.State = 0;
        b.Now += TimeSpan.FromSeconds(30);
        await Tick(s);
        Assert.False(b.Io.Coils[4]);
    }

    [Fact]
    public async Task 关水挂着又开水_撤销关水_冷水机重新开机_阀一直没关()
    {
        var b = Rig();
        await using var s = await Open(b);
        await Water(s).SetAsync(true, CancellationToken.None);
        await Water(s).SetAsync(false, CancellationToken.None);
        Assert.Equal(3, b.Chiller.State);

        await Water(s).SetAsync(true, CancellationToken.None);

        Assert.Equal(2, b.Chiller.State);
        Assert.True(b.Io.Coils[4]);
        Assert.DoesNotContain("阀关", b.Seq);
        b.Chiller.Settle();
        await Tick(s);
        Assert.True(b.Io.Coils[4]);                       // 关水已经撤销了：不会再关
    }

    // ── 跟 TEC 自动联动 ──────────────────────────────────────────────

    [Fact]
    public async Task 接TEC_先开阀_再开冷水机_再合TEC功率线()
    {
        var b = Rig();
        await using var s = await Open(b, TecOn());

        await Temp(s, 0).SetTargetAsync(new TempTarget(60), CancellationToken.None);   // 不过阈值：走 TEC

        Assert.True(b.Io.Coils[7]);
        var i = b.Seq.IndexOf("阀开"); var j = b.Seq.IndexOf("开机"); var k = b.Seq.IndexOf("DO7闭");
        Assert.True(i >= 0 && i < j && j < k, string.Join(" / ", b.Seq));
    }

    [Fact]
    public async Task 降温打开温控_通水延时里采集拍挤进来_阀和冷水机只开一次()
    {
        // 现场踩到：设定比当前低、打开温控（出厂默认 = 双向挡，起步合 TEC），冷却水阀和冷水机开了又关、关了再开。
        // 通水延时那几秒 TEC 功率线还没合，这一路看着不要水，采集拍的对账就把阀当成没人认领关掉了，
        // 延时过了切换序列在锁里再开一遍
        var b = Rig();
        var cfg = new ParameterSet { [DualStationDriver.Fields.WaterLead] = 0.8d };
        await using var s = await Open(b, cfg);
        await s.StartAsync(CancellationToken.None);
        var t = Temp(s, 0);
        await WaitJacket(t, 25.0);
        b.Seq.Clear();

        var on = t.SetTargetAsync(new TempTarget(10), CancellationToken.None);   // 比夹套低超过死区：起步合 TEC
        await Task.Delay(250);                            // 阀开了、冷水机开机发了，正在等通水
        Assert.True(Water(s).Opening);                    // 面板转菊花
        await Tick(s);                                    // 采集拍挤进通水延时
        await Tick(s);
        await on;

        Assert.True(b.Io.Coils[4]);
        Assert.True(b.Io.Coils[7]);
        Assert.Equal(new[] { "阀开", "开机", "DO7闭" },
            b.Seq.Where(x => x is "阀开" or "阀关" or "开机" or "关机" or "DO7闭"));
        Assert.False(Water(s).Opening);                   // 冷水机转起来、通水延时过了：菊花收
    }

    // ── HMI 降温弹窗（用户定的：判是降温、水没备好时问开不开冷水机和冷却水阀；选「不开」照样接 TEC 制冷）──

    private static async Task<(IDeviceSession S, ITemperatureControl T)> Cooling(Bench b, ParameterSet? cfg = null)
    {
        var s = await Open(b, cfg);
        await s.StartAsync(CancellationToken.None);
        var t = Temp(s, 0);
        await WaitJacket(t, 25.0);
        b.Seq.Clear();
        return (s, t);
    }

    [Fact]
    public async Task 降温判断_比夹套低过死区且冷水机标已开才算()
    {
        var b = Rig();
        var (s, t) = await Cooling(b);
        await using var _ = s;
        var h = (IHeatSource)t;
        Assert.True(h.WouldCool(10, TempChannelKind.Jacket));
        Assert.False(h.WouldCool(24, TempChannelKind.Jacket));    // 死区 2 K 以内：起步合加热棒，不接 TEC
        Assert.False(h.WouldCool(60, TempChannelKind.Jacket));

        var b2 = Rig();
        var (s2, t2) = await Cooling(b2, ParameterSet.Of((DualStationDriver.Fields.Chiller, "已关")));
        await using var __ = s2;
        Assert.False(((IHeatSource)t2).WouldCool(10, TempChannelKind.Jacket));   // 冷水机标已关：降温挡只自然凉
    }

    [Fact]
    public async Task 选不开冷却水_直接接TEC_阀和冷水机不动_对账也不补开()
    {
        var b = Rig();
        var (s, t) = await Cooling(b);
        await using var _ = s;
        Assert.False(Water(s).Ready);

        ((IHeatSource)t).SkipWaterNext();
        await t.SetTargetAsync(new TempTarget(10), CancellationToken.None);
        await Tick(s);
        await Tick(s);

        Assert.True(b.Io.Coils[7]);                       // TEC 接上了
        Assert.False(b.Io.Coils[4]);                      // 阀没开
        Assert.Equal(0, b.Chiller.State);                 // 冷水机没开
        Assert.DoesNotContain(b.Seq, x => x is "阀开" or "开机");
        lock (b.Logs) Assert.Single(b.Logs, l => l.Text.Contains("人选了不开冷却水"));

        // 不开水的这一路在制冷：人在面板上关水 / 开水都由人
        await Water(s).SetAsync(true, CancellationToken.None);
        await Water(s).SetAsync(false, CancellationToken.None);
    }

    // ── 中途切 TEC：弹窗问开不开冷却水、冷水机最短间隔（用户定的，2026-10-28）──────────
    //
    // 手动开的温控、开那一下没弹过（升温目标）：中途被控量冲过目标要切 TEC 而冷却水没开好 → 驱动挂询问、等人（到时按打开）；
    // 等人期间加热棒继电器留着、不接 TEC。两次冷启动冷水机之间至少隔 M 秒。配方下发不问、照自动开水。

    /// <summary>把夹套拨到 tj、钟拨过 dt 秒、推一拍。</summary>
    private static async Task Drive(Bench b, IDeviceSession s, ITemperatureControl t, double tj, double dt)
    {
        b.Rd.Set(1, "TCADJTEMP", (long)Math.Round(tj * 1e5));
        await WaitJacket(t, tj);
        b.Now = b.Now.AddSeconds(dt);
        // 【本地改动】这组用例按「10 s 窗就进段」写的（2026-10-28）；进段至少 30 s 窗、压低再进间隔见 DuoPushReentryTests
        ((DuoSession)s).EnterSlopeMinSpan = ((DuoSession)s).SlopeMinSpan;
        await Tick(s);
    }

    /// <summary>夹套目标 30 ℃ 升温（加热棒合着），再把夹套顶过 30.1 且在升：提前判据要切 TEC。manual = 手动开的、开那一下没弹过。</summary>
    private static async Task Overshoot(Bench b, IDeviceSession s, ITemperatureControl t, bool manual)
    {
        if (manual) ((IHeatSource)t).AskWaterNext();
        await t.SetTargetAsync(new TempTarget(30), CancellationToken.None);
        Assert.True(b.Io.Coils[0]);
        await Drive(b, s, t, 30.0, 31);
        b.Seq.Clear();
        foreach (var (tj, dt) in new[] { (30.06, 4.0), (30.12, 4.0), (30.18, 4.0) }) await Drive(b, s, t, tj, dt);
    }

    private static int AskId(IDeviceSession s) => Water(s).Pending?.Id ?? throw new Xunit.Sdk.XunitException("没挂询问");

    [Fact]
    public async Task 手动下发_开那一下没弹窗_中途切TEC挂询问_等人期间加热棒留着不接TEC()
    {
        var b = Rig();
        var (s, t) = await Cooling(b);
        await using var _ = s;
        await Overshoot(b, s, t, manual: true);

        var q = Water(s).Pending;
        Assert.NotNull(q);
        Assert.Equal(1, q!.Channel);
        Assert.Contains("工位 A 要切 TEC 制冷", q.Why);
        Assert.Equal(b.Now + TimeSpan.FromSeconds(60), q.Deadline);
        Assert.True(b.Io.Coils[0]);                       // 加热棒继电器留着（回路钳 0）
        Assert.False(b.Io.Coils[7]);
        Assert.False(b.Io.Coils[4]);
        Assert.Equal(0, b.Chiller.State);
        Assert.Contains("等人答开不开冷却水", ((IHeatSource)t).Regime);
        lock (b.Logs) Assert.Single(b.Logs, l => l.Text.Contains("问操作人开不开冷却水"));

        await Tick(s);
        await Tick(s);
        Assert.Equal(q.Id, Water(s).Pending?.Id);        // 不重复挂
        Assert.False(b.Io.Coils[7]);
    }

    [Fact]
    public async Task 挂询问_答打开_先开阀再开冷水机再接TEC_本次控温不再问()
    {
        var b = Rig();
        var (s, t) = await Cooling(b);
        await using var _ = s;
        var d = (DuoSession)s;
        d.ChillerMinGap = TimeSpan.Zero;
        d.CoolMinDuration = TimeSpan.Zero;
        d.ExitLead = TimeSpan.Zero;
        d.ReentryDwell = TimeSpan.Zero;                   // 【本地改动】这条紧跟着再进一段；再进间隔（2026-11-06）另见 DuoPushReentryTests
        await Overshoot(b, s, t, manual: true);
        var id = AskId(s);

        Assert.True(Water(s).Answer(id, true));
        Assert.Null(Water(s).Pending);                    // 答过了就不再给（落地在下一拍）：面板别把刚答完的窗又弹一次
        Assert.False(Water(s).Answer(id, true));          // 答过了
        await Tick(s);
        Assert.Null(Water(s).Pending);
        Assert.True(b.Io.Coils[7]);
        Assert.False(b.Io.Coils[0]);
        Assert.Equal(new[] { "阀开", "开机", "DO0断", "DO7闭" }, b.Seq.Where(x => x is "阀开" or "开机" or "DO0断" or "DO7闭"));
        Assert.Contains("制冷段", ((IHeatSource)t).Regime);
        lock (b.Logs) Assert.Contains(b.Logs, l => l.Text.Contains("中途切 TEC：操作人选了打开冷却水"));        // 由挂起落地
        lock (b.Logs) Assert.DoesNotContain(b.Logs, l => l.Text.Contains("询问有了结果") || l.Text.Contains("询问已收回"));

        // 退段（夹套回落且在降）→ 回加热棒、停冷水机；再冲过去 → 这次控温不再问，直接自动开水
        await Drive(b, s, t, 29.95, 5);
        await Drive(b, s, t, 29.90, 5);
        await Drive(b, s, t, 29.80, 5);
        Assert.True(b.Io.Coils[0]);
        Assert.Contains("关机", b.Seq);
        b.Seq.Clear();
        await Drive(b, s, t, 29.8, 31);
        foreach (var (tj, dt) in new[] { (30.06, 4.0), (30.12, 4.0), (30.18, 4.0) }) await Drive(b, s, t, tj, dt);
        Assert.Null(Water(s).Pending);
        Assert.True(b.Io.Coils[7]);
        Assert.Contains("开机", b.Seq);
    }

    [Fact]
    public async Task 挂询问_答不开_直接接TEC_阀和冷水机不动()
    {
        var b = Rig();
        var (s, t) = await Cooling(b);
        await using var _ = s;
        await Overshoot(b, s, t, manual: true);
        var id = AskId(s);

        Assert.True(Water(s).Answer(id, false));
        await Tick(s);
        await Tick(s);
        Assert.Null(Water(s).Pending);
        Assert.True(b.Io.Coils[7]);
        Assert.False(b.Io.Coils[4]);
        Assert.Equal(0, b.Chiller.State);
        Assert.DoesNotContain(b.Seq, x => x is "阀开" or "开机");
        // 由挂起落地（ResolveHold）：日志说「直接接」，不说开水；不走没人等的那条（SettleIdleAsk）
        lock (b.Logs) Assert.Single(b.Logs, l => l.Text.Contains("中途切 TEC：操作人选了不开冷却水"));
        lock (b.Logs) Assert.DoesNotContain(b.Logs, l => l.Text.Contains("询问有了结果") || l.Text.Contains("询问已收回") || l.Text.Contains("通水后接 TEC"));
    }

    [Fact]
    public async Task 挂询问_没人答超时按打开()
    {
        var b = Rig();
        var (s, t) = await Cooling(b);
        await using var _ = s;
        await Overshoot(b, s, t, manual: true);
        var id = AskId(s);

        b.Now = b.Now.AddSeconds(30);
        await Tick(s);
        Assert.Equal(id, Water(s).Pending?.Id);
        Assert.False(b.Io.Coils[7]);
        b.Now = b.Now.AddSeconds(31);
        await Tick(s);
        Assert.Null(Water(s).Pending);
        Assert.True(b.Io.Coils[7]);
        Assert.True(b.Io.Coils[4]);
        Assert.Equal(2, b.Chiller.State);
        Assert.False(Water(s).Answer(id, false));         // 过期
        lock (b.Logs) Assert.Single(b.Logs, l => l.Text.Contains("中途切 TEC：60 s 没人答，按「打开冷却水」办"));
        lock (b.Logs) Assert.DoesNotContain(b.Logs, l => l.Text.Contains("询问有了结果") || l.Text.Contains("询问已收回"));
    }

    [Fact]
    public async Task 挂询问_等人期间被控量自己凉回去_留在加热棒_询问不收回_再冲过去等同一个_到时按打开()
    {
        // 2026-11-04 起：本次控温只问一次。等人期间不要制冷了只撤挂起段（留在加热棒），问题留着；再要 TEC 接着等同一个，不重问
        // （原来撤段就收回、再要再问——要冷 ⇄ 不要冷来回一次就问一次，现场升温 80 s 问了 15 次）
        var b = Rig();
        var (s, t) = await Cooling(b);
        await using var _ = s;
        ((DuoSession)s).ReentryDwell = TimeSpan.Zero;     // 【本地改动】这条紧跟着再进一段；再进间隔（2026-11-06）另见 DuoPushReentryTests
        await Overshoot(b, s, t, manual: true);
        var id = AskId(s);

        await Drive(b, s, t, 30.0, 5);
        await Drive(b, s, t, 29.9, 5);
        await Drive(b, s, t, 29.8, 5);
        Assert.Equal(id, Water(s).Pending?.Id);           // 问题留着
        Assert.True(b.Io.Coils[0]);
        Assert.False(b.Io.Coils[7]);
        Assert.Contains("询问挂着", ((IHeatSource)t).Regime);
        lock (b.Logs) Assert.Contains(b.Logs, l => l.Text.Contains("制冷段取消") && l.Text.Contains("等人答复") && l.Text.Contains("询问留着"));
        lock (b.Logs) Assert.DoesNotContain(b.Logs, l => l.Text.Contains("询问收回"));

        await Drive(b, s, t, 29.8, 31);
        foreach (var (tj, dt) in new[] { (30.06, 4.0), (30.12, 4.0), (30.18, 4.0) }) await Drive(b, s, t, tj, dt);
        Assert.Equal(id, Water(s).Pending?.Id);           // 同一个问题，不重问；截止时刻不变
        Assert.False(b.Io.Coils[7]);
        Assert.Contains("等人答开不开冷却水", ((IHeatSource)t).Regime);
        lock (b.Logs) Assert.Single(b.Logs, l => l.Text.Contains("问操作人开不开冷却水"));
        lock (b.Logs) Assert.Contains(b.Logs, l => l.Text.Contains("询问还挂着（同一个，不重问）"));

        b.Now = b.Now.AddSeconds(3);                      // 第一次挂出来满 60 s：按打开办
        await Tick(s);
        Assert.Null(Water(s).Pending);
        Assert.True(b.Io.Coils[7]);
        Assert.True(b.Io.Coils[4]);
        lock (b.Logs) Assert.Contains(b.Logs, l => l.Text.Contains("60 s 没人答，按「打开冷却水」办"));
    }

    // ── 功率符号进的挂起（2026-11-04 现场 R1-tc1-20261104-104405）────────────────────
    //
    // 釜内 −2 → 20 升温，夹套冲到 28 后外环开始往回收：釜内 17.6 还在目标下面、回路要把夹套往下拉（功率翻负）——功率符号要 TEC。
    // 原来挂起段一律「被控量低过目标就撤」，符号进法当拍就撤、收回询问、3 s 后又问：80 s 问了 15 次，TEC 一次没接上。

    /// <summary>温控器 PID 方式下「回路要的功率」是从设备读回的 PWMDUTY（这台加热棒吃负、TEC 制冷是正）：写进假机、等内层读到。</summary>
    private static async Task DeviceDuty(Bench b, IDeviceSession s, double percent)
    {
        b.Rd.Set(1, "PWMDUTY", (long)Math.Round(percent * 20_000));
        var inner = ((DuoSession)s).InnerTemp(0);
        var deadline = DateTime.UtcNow.AddSeconds(4);
        while (DateTime.UtcNow < deadline && !(Math.Abs(inner.LastDuty - percent) <= 0.01)) await Task.Delay(50);
        Assert.Equal(percent, inner.LastDuty, 2);
    }

    /// <summary>手动开的 30 ℃ 升温（加热棒合着），夹套 28（目标下面）；回路转成要冷（TEC 那一极 +15 %）连续 3 s：符号要 TEC → 挂询问。</summary>
    private static async Task<int> SignWantsTecBelowTarget(Bench b, IDeviceSession s, ITemperatureControl t)
    {
        ((IHeatSource)t).AskWaterNext();
        await t.SetTargetAsync(new TempTarget(30), CancellationToken.None);
        Assert.True(b.Io.Coils[0]);
        await Drive(b, s, t, 28.0, 31);
        b.Seq.Clear();
        await DeviceDuty(b, s, 15);
        await Drive(b, s, t, 28.0, 1);
        await Drive(b, s, t, 28.0, 3.5);
        var q = Water(s).Pending;
        Assert.NotNull(q);
        // 被控量在目标下面，别说成冲过了；夹套控温不重复写两遍夹套
        Assert.Equal("工位 A 要切 TEC 制冷：回路要把夹套往下拉（功率连续 3 s 指向 TEC；夹套 28.00 ℃，目标 30.00 ℃）", q!.Why);
        Assert.True(q.Waiting);
        Assert.True(b.Io.Coils[0]);
        Assert.False(b.Io.Coils[7]);
        return q.Id;
    }

    [Fact]
    public async Task 功率符号进的挂起_被控量在目标下面不撤_一直等到答复或到时_不来回问()
    {
        var b = Rig();
        var (s, t) = await Cooling(b);
        await using var _ = s;
        var id = await SignWantsTecBelowTarget(b, s, t);

        await DeviceDuty(b, s, 0);                         // 挂起期间回路钳 0（只加热）
        foreach (var tj in new[] { 28.02, 28.05, 28.08, 28.1 }) await Drive(b, s, t, tj, 10);   // 夹套在目标下面慢慢升：+40 s
        Assert.Equal(id, Water(s).Pending?.Id);
        Assert.True(b.Io.Coils[0]);
        Assert.False(b.Io.Coils[7]);
        Assert.Contains("等人答开不开冷却水", ((IHeatSource)t).Regime);
        lock (b.Logs) Assert.Single(b.Logs, l => l.Text.Contains("问操作人开不开冷却水"));
        lock (b.Logs) Assert.DoesNotContain(b.Logs, l => l.Text.Contains("询问收回") || l.Text.Contains("制冷段取消"));

        b.Now = b.Now.AddSeconds(21);                      // 挂出来满 60 s：按打开办——先开阀、开冷水机，再断加热棒、合 TEC
        await Tick(s);
        Assert.Null(Water(s).Pending);
        Assert.True(b.Io.Coils[7]);
        Assert.False(b.Io.Coils[0]);
        Assert.Equal(new[] { "阀开", "开机", "DO0断", "DO7闭" }, b.Seq.Where(x => x is "阀开" or "开机" or "DO0断" or "DO7闭"));
        Assert.Contains("制冷段", ((IHeatSource)t).Regime);
    }

    [Fact]
    public async Task 功率符号进的挂起_回路又要加热满3s才撤_问题留着_再要TEC等同一个_答打开就接()
    {
        var b = Rig();
        var (s, t) = await Cooling(b);
        await using var _ = s;
        var d = (DuoSession)s;
        d.ChillerMinGap = TimeSpan.Zero;
        var id = await SignWantsTecBelowTarget(b, s, t);

        // 回路要加热（加热棒那一极 −5 % = 回路 +5 %）：不到 3 s 不撤
        await DeviceDuty(b, s, -5);
        await Drive(b, s, t, 28.0, 1);
        await Drive(b, s, t, 28.0, 1);
        Assert.Contains("等人答开不开冷却水", ((IHeatSource)t).Regime);
        await Drive(b, s, t, 28.0, 2.5);
        Assert.Equal("双向挡（加热棒 ↔ TEC 跟功率符号走）·开不开冷却水的询问挂着", ((IHeatSource)t).Regime);
        Assert.Equal(id, Water(s).Pending?.Id);
        Assert.False(Water(s).Pending!.Waiting);           // 面板副标题据此说「此刻又不要制冷了」
        Assert.True(b.Io.Coils[0]);
        Assert.False(b.Io.Coils[7]);
        lock (b.Logs) Assert.Contains(b.Logs, l => l.Text.Contains("制冷段取消") && l.Text.Contains("回路又要加热了") && l.Text.Contains("询问留着"));
        lock (b.Logs) Assert.DoesNotContain(b.Logs, l => l.Text.Contains("询问收回"));

        // 又要冷：接着等同一个问题，不重问
        await DeviceDuty(b, s, 15);
        await Drive(b, s, t, 28.0, 1);
        await Drive(b, s, t, 28.0, 3.5);
        Assert.Equal(id, Water(s).Pending?.Id);
        Assert.True(Water(s).Pending!.Waiting);
        Assert.Contains("等人答开不开冷却水", ((IHeatSource)t).Regime);
        Assert.False(b.Io.Coils[7]);
        lock (b.Logs) Assert.Single(b.Logs, l => l.Text.Contains("问操作人开不开冷却水"));

        Assert.True(Water(s).Answer(id, true));
        await Tick(s);
        Assert.Null(Water(s).Pending);
        Assert.True(b.Io.Coils[7]);
        Assert.Equal(new[] { "阀开", "开机", "DO0断", "DO7闭" }, b.Seq.Where(x => x is "阀开" or "开机" or "DO0断" or "DO7闭"));
        Assert.Equal("双向挡·制冷段（功率符号切 TEC）", ((IHeatSource)t).Regime);
        lock (b.Logs) Assert.Single(b.Logs, l => l.Text.Contains("中途切 TEC：操作人选了打开冷却水"));   // 由挂起落地，不走没人等的那条
        lock (b.Logs) Assert.DoesNotContain(b.Logs, l => l.Text.Contains("询问有了结果") || l.Text.Contains("询问已收回"));
    }

    [Fact]
    public async Task 问题挂着没人在等_答了或到时就记下_之后再要TEC照答复办不再问()
    {
        // 答「不开」：记下，之后要 TEC 直接接、阀和冷水机不动
        var b = Rig();
        var (s, t) = await Cooling(b);
        await using (s)
        {
            var id = await SignWantsTecBelowTarget(b, s, t);
            await DeviceDuty(b, s, -5);
            await Drive(b, s, t, 28.0, 1);
            await Drive(b, s, t, 28.0, 3.5);                   // 撤段，问题留着
            Assert.Equal(id, Water(s).Pending?.Id);
            Assert.True(Water(s).Answer(id, false));
            await Tick(s);
            Assert.Null(Water(s).Pending);
            Assert.Equal("双向挡（加热棒 ↔ TEC 跟功率符号走）", ((IHeatSource)t).Regime);
            Assert.True(b.Io.Coils[0]);                         // 此刻不要制冷：不扳
            lock (b.Logs) Assert.Contains(b.Logs, l => l.Text.Contains("询问有了结果：操作人选了不开冷却水"));

            await DeviceDuty(b, s, 15);
            await Drive(b, s, t, 28.0, 1);
            await Drive(b, s, t, 28.0, 3.5);
            Assert.Null(Water(s).Pending);
            Assert.True(b.Io.Coils[7]);
            Assert.False(b.Io.Coils[4]);
            Assert.Equal(0, b.Chiller.State);
            lock (b.Logs) Assert.Single(b.Logs, l => l.Text.Contains("问操作人开不开冷却水"));
        }

        // 没人答、到时：按打开记下，之后要 TEC 先通水再接
        var b2 = Rig();
        var (s2, t2) = await Cooling(b2);
        await using (s2)
        {
            var d2 = (DuoSession)s2;
            d2.ChillerMinGap = TimeSpan.Zero;
            var id2 = await SignWantsTecBelowTarget(b2, s2, t2);
            await DeviceDuty(b2, s2, -5);
            await Drive(b2, s2, t2, 28.0, 1);
            await Drive(b2, s2, t2, 28.0, 3.5);
            Assert.Equal(id2, Water(s2).Pending?.Id);
            b2.Now = b2.Now.AddSeconds(60);
            await Tick(s2);
            Assert.Null(Water(s2).Pending);
            Assert.True(b2.Io.Coils[0]);
            Assert.False(Water(s2).Answer(id2, false));         // 过期
            lock (b2.Logs) Assert.Contains(b2.Logs, l => l.Text.Contains("询问有了结果：60 s 没人答，按「打开冷却水」办"));

            b2.Seq.Clear();
            await DeviceDuty(b2, s2, 15);
            await Drive(b2, s2, t2, 28.0, 1);
            await Drive(b2, s2, t2, 28.0, 3.5);
            Assert.Null(Water(s2).Pending);
            Assert.True(b2.Io.Coils[7]);
            Assert.Equal(new[] { "阀开", "开机", "DO0断", "DO7闭" }, b2.Seq.Where(x => x is "阀开" or "开机" or "DO0断" or "DO7闭"));
            lock (b2.Logs) Assert.Single(b2.Logs, l => l.Text.Contains("问操作人开不开冷却水"));
        }
    }

    [Fact]
    public async Task 功率符号进的挂起_回路钳0_被控量低过目标且在降_撤段_到时也不接TEC()
    {
        // 挂起期间回路钳 0（PWMDUTY = 0：想冷也出不来），夹套自己往下掉、低过目标且在降 = 真不要冷了
        var b = Rig();
        var (s, t) = await Cooling(b);
        await using var _ = s;
        ((DuoSession)s).ChillerMinGap = TimeSpan.Zero;
        await SignWantsTecBelowTarget(b, s, t);
        var askAt = b.Now;
        await DeviceDuty(b, s, 0);
        foreach (var tj in new[] { 27.9, 27.8, 27.7, 27.6, 27.5, 27.4, 27.3, 27.2, 27.1, 27.0, 26.9, 26.8 }) await Drive(b, s, t, tj, 5);
        b.Now = askAt.AddSeconds(61);                      // 问出来满 60 s
        await Tick(s);
        lock (b.Logs) Assert.Single(b.Logs, l => l.Text.Contains("制冷段取消") && l.Text.Contains("等水期间被控量已回落") && l.Text.Contains("询问留着"));
        Assert.True(b.Io.Coils[0]);                        // 到时只记下「打开」，此刻不接 TEC
        Assert.False(b.Io.Coils[7]);
        Assert.False(b.Io.Coils[4]);
        Assert.Equal(0, b.Chiller.State);
        Assert.Null(Water(s).Pending);
        lock (b.Logs) Assert.Contains(b.Logs, l => l.Text.Contains("询问有了结果：60 s 没人答，按「打开冷却水」办"));
        lock (b.Logs) Assert.Single(b.Logs, l => l.Text.Contains("问操作人开不开冷却水"));
    }

    [Fact]
    public async Task 功率符号进的挂起_要加热断断续续不累计_得连续3s才撤()
    {
        var b = Rig();
        var (s, t) = await Cooling(b);
        await using var _ = s;
        var id = await SignWantsTecBelowTarget(b, s, t);
        // 在死区边上抖（要加热 1 s ⇄ 不表态 1 s）：每回到死区就重新计，一直不撤
        for (var i = 0; i < 5; i++)
        {
            await DeviceDuty(b, s, -5);
            await Drive(b, s, t, 28.0, 1);
            await DeviceDuty(b, s, 0);
            await Drive(b, s, t, 28.0, 1);
        }
        Assert.Contains("等人答开不开冷却水", ((IHeatSource)t).Regime);
        lock (b.Logs) Assert.DoesNotContain(b.Logs, l => l.Text.Contains("制冷段取消"));
        Assert.Equal(id, Water(s).Pending?.Id);
        // 断一次之后再连续满 3 s（从头一拍看到要加热算起）：照常撤
        await DeviceDuty(b, s, -5);
        await Drive(b, s, t, 28.0, 1);
        await Drive(b, s, t, 28.0, 3.5);
        lock (b.Logs) Assert.Contains(b.Logs, l => l.Text.Contains("回路又要加热了"));
        Assert.Equal(id, Water(s).Pending?.Id);
    }

    /// <summary>面板开过一次又关了：冷水机刚冷启动过，最短间隔（缺省 60 s）内自动要 TEC 得挂起等。</summary>
    private static async Task ChillerJustStarted(Bench b, IDeviceSession s)
    {
        Assert.Equal(60, ((DuoSession)s).ChillerMinGap.TotalSeconds);
        await Water(s).SetAsync(true, CancellationToken.None);
        await Water(s).SetAsync(false, CancellationToken.None);
        b.Chiller.Settle();
        await Tick(s);
        Assert.False(b.Io.Coils[4]);
    }

    [Fact]
    public async Task 功率符号进的间隔挂起_在目标下面不撤不刷日志_到点按顺序接TEC()
    {
        // 原来同一个「低过目标就撤」也管着间隔挂起：60 s 里撤了立、立了撤，「刚开过（最短间隔…）」刷 13 次
        var b = Rig();
        var (s, t) = await Cooling(b);
        await using var _ = s;
        await ChillerJustStarted(b, s);
        await t.SetTargetAsync(new TempTarget(30), CancellationToken.None);   // 正常下发：不问人
        Assert.True(b.Io.Coils[0]);
        await Drive(b, s, t, 28.0, 5);
        b.Seq.Clear();
        await DeviceDuty(b, s, 15);
        await Drive(b, s, t, 28.0, 1);
        await Drive(b, s, t, 28.0, 3.5);
        Assert.Null(Water(s).Pending);
        Assert.Contains("等冷水机最短间隔", ((IHeatSource)t).Regime);
        for (var i = 0; i < 15; i++)
        {
            await Drive(b, s, t, 28.0, 2);
            Assert.Contains("等冷水机最短间隔", ((IHeatSource)t).Regime);
        }
        lock (b.Logs) Assert.DoesNotContain(b.Logs, l => l.Text.Contains("制冷段取消"));
        lock (b.Logs) Assert.Single(b.Logs, l => l.Text.Contains("刚开过（最短间隔"));
        Assert.False(b.Io.Coils[7]);
        Assert.DoesNotContain("开机", b.Seq);

        b.Now = b.Now.AddSeconds(30);                      // 间隔到点
        await Drive(b, s, t, 28.0, 1);
        Assert.True(b.Io.Coils[7]);
        Assert.False(b.Io.Coils[0]);
        Assert.Equal(new[] { "阀开", "开机", "DO0断", "DO7闭" }, b.Seq.Where(x => x is "阀开" or "开机" or "DO0断" or "DO7闭"));
        Assert.Equal("双向挡·制冷段（功率符号切 TEC）", ((IHeatSource)t).Regime);
    }

    [Fact]
    public async Task 功率符号进的间隔挂起_回路又要加热满3s才撤_不碰询问()
    {
        var b = Rig();
        var (s, t) = await Cooling(b);
        await using var _ = s;
        await ChillerJustStarted(b, s);
        await t.SetTargetAsync(new TempTarget(30), CancellationToken.None);
        await Drive(b, s, t, 28.0, 5);
        await DeviceDuty(b, s, 15);
        await Drive(b, s, t, 28.0, 1);
        await Drive(b, s, t, 28.0, 3.5);
        Assert.Contains("等冷水机最短间隔", ((IHeatSource)t).Regime);

        await DeviceDuty(b, s, -5);
        await Drive(b, s, t, 28.0, 1);
        await Drive(b, s, t, 28.0, 1);
        Assert.Contains("等冷水机最短间隔", ((IHeatSource)t).Regime);   // 不到 3 s 不撤
        await Drive(b, s, t, 28.0, 2.5);
        Assert.Equal("双向挡（加热棒 ↔ TEC 跟功率符号走）", ((IHeatSource)t).Regime);
        Assert.Null(Water(s).Pending);
        List<string> texts;
        lock (b.Logs) texts = b.Logs.Where(l => l.Text.Contains("制冷段取消")).Select(l => l.Text).ToList();
        Assert.Single(texts);
        Assert.Contains("等冷水机间隔期间回路又要加热了", texts[0]);
        Assert.DoesNotContain("询问留着", texts[0]);
        Assert.True(b.Io.Coils[0]);
        Assert.False(b.Io.Coils[7]);
    }

    [Fact]
    public async Task 问题挂着没人等_面板把水开了_落地为问过_之后要TEC不再问()
    {
        var b = Rig();
        var (s, t) = await Cooling(b);
        await using var _ = s;
        ((DuoSession)s).ChillerMinGap = TimeSpan.Zero;
        var id = await SignWantsTecBelowTarget(b, s, t);
        await DeviceDuty(b, s, -5);
        await Drive(b, s, t, 28.0, 1);
        await Drive(b, s, t, 28.0, 3.5);                   // 撤段，问题留着
        Assert.Equal(id, Water(s).Pending?.Id);

        await Water(s).SetAsync(true, CancellationToken.None);     // 人在面板上把水开了
        await Tick(s);
        Assert.Null(Water(s).Pending);
        Assert.False(Water(s).Answer(id, false));
        Assert.True(b.Io.Coils[0]);
        lock (b.Logs) Assert.Contains(b.Logs, l => l.Text.Contains("询问有了结果：冷却水已经开了"));

        await Water(s).SetAsync(false, CancellationToken.None);    // 又关了：再要 TEC 不问，先通水再接
        b.Chiller.Settle();
        await Tick(s);
        Assert.False(b.Io.Coils[4]);
        b.Seq.Clear();
        await DeviceDuty(b, s, 15);
        await Drive(b, s, t, 28.0, 1);
        await Drive(b, s, t, 28.0, 3.5);
        Assert.Null(Water(s).Pending);
        Assert.True(b.Io.Coils[7]);
        Assert.Equal(new[] { "阀开", "开机", "DO0断", "DO7闭" }, b.Seq.Where(x => x is "阀开" or "开机" or "DO0断" or "DO7闭"));
    }

    [Fact]
    public async Task 问题挂着没人等_另一路正常下发降温把水开了_落地为问过()
    {
        var b = Rig();
        var (s, t) = await Cooling(b);
        await using var _ = s;
        ((DuoSession)s).ChillerMinGap = TimeSpan.Zero;
        var tB = Temp(s, 1);
        b.Rd.Set(2, "TCADJTEMP", 28_00000);
        await WaitJacket(tB, 28.0);
        var id = await SignWantsTecBelowTarget(b, s, t);
        await DeviceDuty(b, s, -5);
        await Drive(b, s, t, 28.0, 1);
        await Drive(b, s, t, 28.0, 3.5);
        Assert.Equal(id, Water(s).Pending?.Id);

        await tB.SetTargetAsync(new TempTarget(10), CancellationToken.None);   // B 正常下发降温：不问，自动开水接 TEC
        Assert.True(b.Io.Coils[4]);
        await Tick(s);
        Assert.Null(Water(s).Pending);
        Assert.True(b.Io.Coils[0]);
        lock (b.Logs) Assert.Contains(b.Logs, l => l.Text.Contains("工位 A 开不开冷却水的询问有了结果：冷却水已经开了"));
    }

    [Fact]
    public async Task 问题挂着没人等_对着过时的窗点了不开_同一拍水已开_按水已开落地_不记不开()
    {
        var b = Rig();
        var (s, t) = await Cooling(b);
        await using var _ = s;
        ((DuoSession)s).ChillerMinGap = TimeSpan.Zero;
        var id = await SignWantsTecBelowTarget(b, s, t);
        await DeviceDuty(b, s, -5);
        await Drive(b, s, t, 28.0, 1);
        await Drive(b, s, t, 28.0, 3.5);

        await Water(s).SetAsync(true, CancellationToken.None);     // 水先被开了，窗还没来得及收
        Assert.True(Water(s).Answer(id, false));
        await Tick(s);
        lock (b.Logs) Assert.Contains(b.Logs, l => l.Text.Contains("询问有了结果：冷却水已经开了"));
        lock (b.Logs) Assert.DoesNotContain(b.Logs, l => l.Text.Contains("操作人选了不开冷却水"));

        await Water(s).SetAsync(false, CancellationToken.None);
        b.Chiller.Settle();
        await Tick(s);
        b.Seq.Clear();
        await DeviceDuty(b, s, 15);
        await Drive(b, s, t, 28.0, 1);
        await Drive(b, s, t, 28.0, 3.5);
        Assert.True(b.Io.Coils[7]);
        Assert.True(b.Io.Coils[4]);                         // 没被记成「不开」：接 TEC 前照样通水
        Assert.Equal(new[] { "阀开", "开机", "DO0断", "DO7闭" }, b.Seq.Where(x => x is "阀开" or "开机" or "DO0断" or "DO7闭"));
    }

    [Fact]
    public async Task 问题挂着没人等_冷水机改成已关_问题收回_改回已开再要TEC重问()
    {
        var b = Rig();
        var cfg = new ParameterSet();
        var (s, t) = await Cooling(b, cfg);
        await using var _ = s;
        ((DuoSession)s).ChillerMinGap = TimeSpan.Zero;
        var id = await SignWantsTecBelowTarget(b, s, t);
        await DeviceDuty(b, s, -5);
        await Drive(b, s, t, 28.0, 1);
        await Drive(b, s, t, 28.0, 3.5);
        Assert.Equal(id, Water(s).Pending?.Id);
        cfg[DualStationDriver.Fields.Chiller] = "已关";
        await Drive(b, s, t, 28.0, 1);
        Assert.Null(Water(s).Pending);
        Assert.False(Water(s).Answer(id, false));
        lock (b.Logs) Assert.Contains(b.Logs, l => l.Text.Contains("询问收回（规矩变了"));
        cfg[DualStationDriver.Fields.Chiller] = "已开";
        await Drive(b, s, t, 28.0, 1);
        await DeviceDuty(b, s, 15);
        await Drive(b, s, t, 28.0, 1);
        await Drive(b, s, t, 28.0, 3.5);
        Assert.NotNull(Water(s).Pending);
        Assert.True(Water(s).Pending!.Id > id);
        Assert.False(b.Io.Coils[7]);
    }

    // ── 两路共用一个询问槽位（一只阀一台冷水机）────────────────────────────

    private static async Task DutyOf(Bench b, IDeviceSession s, int well, double percent)
    {
        b.Rd.Set(well + 1, "PWMDUTY", (long)Math.Round(percent * 20_000));
        var inner = ((DuoSession)s).InnerTemp(well);
        var deadline = DateTime.UtcNow.AddSeconds(4);
        while (DateTime.UtcNow < deadline && !(Math.Abs(inner.LastDuty - percent) <= 0.01)) await Task.Delay(50);
        Assert.Equal(percent, inner.LastDuty, 2);
    }

    private static async Task Step(Bench b, IDeviceSession s, double dt) { b.Now = b.Now.AddSeconds(dt); await Tick(s); }

    /// <summary>两路都是手动开的 30 ℃ 升温、夹套 28：B 先要 TEC 挂了问题，又要加热撤段（问题留着没人等）；接着 A 要 TEC。</summary>
    private static async Task<(Bench B, IDeviceSession S, ITemperatureControl TA, ITemperatureControl TB, WaterAsk QB, DateTimeOffset AWants)> TwoWells(bool bLeavesIdle = true)
    {
        var b = Rig();
        var (s, tA) = await Cooling(b);
        ((DuoSession)s).ChillerMinGap = TimeSpan.Zero;
        var tB = Temp(s, 1);
        b.Rd.Set(1, "TCADJTEMP", 28_00000);
        b.Rd.Set(2, "TCADJTEMP", 28_00000);
        await WaitJacket(tA, 28);
        await WaitJacket(tB, 28);
        ((IHeatSource)tA).AskWaterNext();
        await tA.SetTargetAsync(new TempTarget(30), CancellationToken.None);
        ((IHeatSource)tB).AskWaterNext();
        await tB.SetTargetAsync(new TempTarget(30), CancellationToken.None);
        await Step(b, s, 31);
        await DutyOf(b, s, 1, 15);                         // B 要冷
        await Step(b, s, 1);
        await Step(b, s, 3.5);
        var qB = Water(s).Pending!;
        Assert.Contains("工位 B", qB.Why);
        if (bLeavesIdle)
        {
            await DutyOf(b, s, 1, -5);                     // B 又要加热满 3 s：撤段，问题留着没人等
            await Step(b, s, 1);
            await Step(b, s, 3.5);
            Assert.False(Water(s).Pending!.Waiting);
        }
        await DutyOf(b, s, 0, 15);                         // A 要冷：槽位被 B 的问题占着，排在后面等
        await Step(b, s, 1);
        var aWants = b.Now;
        await Step(b, s, 3.5);
        Assert.Equal(qB.Id, Water(s).Pending?.Id);
        Assert.True(Water(s).Pending!.Waiting);            // A 在等它
        Assert.Contains("等人答开不开冷却水", ((IHeatSource)tA).Regime);
        Assert.False(b.Io.Coils[7]);
        return (b, s, tA, tB, qB, aWants);
    }

    [Fact]
    public async Task 两路_B的问题挂着没人等_A排在后面_答打开就开水接A_只问一次()
    {
        var (b, s, tA, tB, qB, _) = await TwoWells();
        await using var _s = s;
        Assert.True(Water(s).Answer(qB.Id, true));
        await Step(b, s, 1);
        await Step(b, s, 1);
        Assert.Null(Water(s).Pending);
        Assert.True(b.Io.Coils[4]);
        Assert.True(b.Io.Coils[7]);
        for (var k = 0; k < 5; k++) { await Step(b, s, 1); Assert.Null(Water(s).Pending); }   // 没有第二个问题
        Assert.Equal("双向挡（加热棒 ↔ TEC 跟功率符号走）", ((IHeatSource)tB).Regime);         // B 此刻不要冷：不扳
        lock (b.Logs) Assert.Contains(b.Logs, l => l.Text.Contains("一只阀，本路照办、不再另问"));
    }

    [Fact]
    public async Task 两路_B的问题挂着没人等_没人答_A在B的截止时刻接上_只问一次()
    {
        var (b, s, _, _, qB, aWants) = await TwoWells();
        await using var _s = s;
        b.Now = qB.Deadline;
        await Tick(s);
        await Step(b, s, 1);
        Assert.True((b.Now - aWants).TotalSeconds <= 60);  // 不超过 B 那个问题剩下的倒计时
        Assert.True(b.Io.Coils[7]);
        Assert.True(b.Io.Coils[4]);
        Assert.Null(Water(s).Pending);
        for (var k = 0; k < 5; k++) { await Step(b, s, 1); Assert.Null(Water(s).Pending); }
    }

    [Fact]
    public async Task 两路_B的问题挂着没人等_答不开只算B_A再挂自己的()
    {
        var (b, s, _, _, qB, _) = await TwoWells();
        await using var _s = s;
        Assert.True(Water(s).Answer(qB.Id, false));        // 「不开」不带过去：没水 TEC 会干烧，各路自己认
        await Step(b, s, 1);
        await Step(b, s, 1);
        var qA = Water(s).Pending;
        Assert.NotNull(qA);
        Assert.Contains("工位 A", qA!.Why);
        Assert.False(b.Io.Coils[7]);
        Assert.False(b.Io.Coils[4]);
    }

    [Fact]
    public async Task 两路都在等_B答打开_A不再问_一起开水接上()
    {
        var (b, s, _, _, qB, _) = await TwoWells(bLeavesIdle: false);
        await using var _s = s;
        Assert.True(Water(s).Answer(qB.Id, true));
        await Step(b, s, 1);
        await Step(b, s, 1);
        await Step(b, s, 1);
        Assert.Null(Water(s).Pending);
        Assert.True(b.Io.Coils[4]);
        Assert.True(b.Io.Coils[6]);                        // B 的 TEC（DO6）
        Assert.True(b.Io.Coils[7]);                        // A 的 TEC（DO7）
        lock (b.Logs) Assert.Single(b.Logs, l => l.Text.Contains("要切 TEC 制冷，这次控温是手动开的") && l.Text.Contains("工位 B"));
    }

    [Fact]
    public async Task 功率符号进_釜内目标_询问文字写釜内和夹套()
    {
        // 现场那种：釜内串级、釜内 17.6 还在目标 20 下面、夹套 28 要往下拉
        var b = Rig();
        var (s, t) = await Cooling(b);
        await using var _ = s;
        async Task DriveTr(double tj, double tr, double dt)
        {
            b.Rd.Set(1, "TCADJTEMP", (long)Math.Round(tj * 1e5));
            await WaitJacket(t, tj);
            b.Now = b.Now.AddSeconds(dt);
            ((IExternalReactorTemp)s).FeedReactor(1, tr, Quality.Good);
            await Tick(s);
        }
        ((IHeatSource)t).AskWaterNext();
        await DriveTr(-2.0, -2.0, 1);
        await t.SetTargetAsync(new TempTarget(20, TempChannelKind.Reactor), CancellationToken.None);
        for (var i = 0; i < 4; i++) await DriveTr(10.0 + 4 * i, 5.0 + 3 * i, 8);
        for (var i = 0; i < 4; i++) await DriveTr(28.0, 17.6, 8);
        await DeviceDuty(b, s, 15);
        await DriveTr(28.0, 17.6, 1);
        await DriveTr(28.0, 17.6, 3.5);
        var q = Water(s).Pending;
        Assert.NotNull(q);
        Assert.Equal("工位 A 要切 TEC 制冷：回路要把夹套往下拉（功率连续 3 s 指向 TEC；夹套 28.00 ℃，釜内 17.60 ℃，目标 20.00 ℃）", q!.Why);
        for (var i = 0; i < 6; i++) await DriveTr(28.0, 17.6 + 0.02 * i, 5);   // 釜内一直在目标下面慢慢升：不撤、不重问
        Assert.Equal(q.Id, Water(s).Pending?.Id);
        Assert.Contains("等人答开不开冷却水", ((IHeatSource)t).Regime);
        lock (b.Logs) Assert.DoesNotContain(b.Logs, l => l.Text.Contains("制冷段取消") || l.Text.Contains("询问收回"));
    }

    [Fact]
    public async Task 配方下发不问_照常自动开水接TEC()
    {
        var b = Rig();
        var (s, t) = await Cooling(b);
        await using var _ = s;
        await Overshoot(b, s, t, manual: false);

        Assert.Null(Water(s).Pending);
        Assert.True(b.Io.Coils[7]);
        Assert.Equal(new[] { "阀开", "开机", "DO0断", "DO7闭" }, b.Seq.Where(x => x is "阀开" or "开机" or "DO0断" or "DO7闭"));
    }

    [Fact]
    public async Task 挂询问_停控收回()
    {
        var b = Rig();
        var (s, t) = await Cooling(b);
        await using var _ = s;
        await Overshoot(b, s, t, manual: true);
        Assert.NotNull(Water(s).Pending);
        await t.StopAsync(CancellationToken.None);
        Assert.Null(Water(s).Pending);
        await Tick(s);
        Assert.False(b.Io.Coils[7]);
        Assert.False(b.Io.Coils[0]);
    }

    [Fact]
    public async Task 冷水机延时停机中又要制冷_撤销关水不算冷启动_不等间隔()
    {
        var b = Rig();
        var (s, t) = await Cooling(b);
        await using var _ = s;
        var d = (DuoSession)s;
        d.CoolMinDuration = TimeSpan.Zero;
        d.ExitLead = TimeSpan.Zero;
        d.ReentryDwell = TimeSpan.Zero;                   // 【本地改动】这条紧跟着再进一段；再进间隔（2026-11-06）另见 DuoPushReentryTests
        await Overshoot(b, s, t, manual: false);
        Assert.True(b.Io.Coils[7]);

        // 退段：冷水机停机已发、还在延时停机（没 Settle），阀开着
        await Drive(b, s, t, 29.95, 5);
        await Drive(b, s, t, 29.90, 5);
        await Drive(b, s, t, 29.80, 5);
        Assert.True(b.Io.Coils[0]);
        Assert.Equal(3, b.Chiller.State);
        b.Seq.Clear();

        // 不到 120 s 又冲过去：冷水机还在转（延时停机）→ 撤销关水、重新开机，不算冷启动、不挂起
        await Drive(b, s, t, 29.8, 31);
        foreach (var (tj, dt) in new[] { (30.06, 4.0), (30.12, 4.0), (30.18, 4.0) }) await Drive(b, s, t, tj, dt);
        Assert.True(b.Io.Coils[7]);
        Assert.Contains("开机", b.Seq);
        Assert.DoesNotContain("等冷水机最短间隔", ((IHeatSource)t).Regime ?? "");
        lock (b.Logs) Assert.DoesNotContain(b.Logs, l => l.Text.Contains("刚开过（最短间隔"));
    }

    [Fact]
    public async Task 面板手动开水也算一次冷启动_间隔内自动进段先不开()
    {
        var b = Rig();
        var (s, t) = await Cooling(b);
        await using var _ = s;
        await Water(s).SetAsync(true, CancellationToken.None);     // 人在面板上开了一次（冷启动）
        await Water(s).SetAsync(false, CancellationToken.None);
        b.Chiller.Settle();
        await Tick(s);
        Assert.False(b.Io.Coils[4]);
        b.Seq.Clear();

        await Overshoot(b, s, t, manual: false);
        Assert.True(b.Io.Coils[0]);
        Assert.False(b.Io.Coils[7]);
        Assert.DoesNotContain("开机", b.Seq);
        Assert.Contains("等冷水机最短间隔", ((IHeatSource)t).Regime);
    }

    [Theory]
    [InlineData(null, 60)]     // 没存：缺省 60 s
    [InlineData(120d, 60)]     // 存的是旧缺省 120：按 60 用（用户定的：不用现场去改文件）
    [InlineData(90d, 90)]      // 别的填法照填的走
    [InlineData(0d, 0)]
    public async Task 冷水机最短间隔_缺省60_存着旧缺省120按60用_别的照填(double? stored, double expected)
    {
        var b = Rig();
        var cfg = new ParameterSet();
        if (stored is { } v) cfg[DualStationDriver.Fields.ChillerGap] = v;
        await using var s = await b.Drv.OpenAsync(Conn(), b.Ctx(cfg), CancellationToken.None);
        Assert.Equal(expected, ((DuoSession)s).ChillerMinGap.TotalSeconds);
        lock (b.Logs) Assert.Equal(stored == 120d, b.Logs.Any(l => l.Text.Contains("旧缺省 120 s")));
    }

    [Fact]
    public async Task 冷水机最短间隔_间隔内自动进段先不开_到点再开()
    {
        var b = Rig();
        var (s, t) = await Cooling(b);
        await using var _ = s;
        var d = (DuoSession)s;
        d.CoolMinDuration = TimeSpan.Zero;
        d.ExitLead = TimeSpan.Zero;
        d.ChillerMinGap = TimeSpan.FromSeconds(120);      // 这条的时间线按 120 s 排（缺省 60 s）
        d.ReentryDwell = TimeSpan.Zero;                   // 【本地改动】这条紧跟着再进一段；再进间隔（2026-11-06）另见 DuoPushReentryTests
        await Overshoot(b, s, t, manual: false);          // 自动开水：冷启动一次
        Assert.True(b.Io.Coils[7]);
        Assert.Equal(2, b.Chiller.State);

        // 退段：停冷水机、停稳关阀
        await Drive(b, s, t, 29.95, 5);
        await Drive(b, s, t, 29.90, 5);
        await Drive(b, s, t, 29.80, 5);
        Assert.True(b.Io.Coils[0]);
        Assert.Contains("关机", b.Seq);
        b.Chiller.Settle();
        await Tick(s);
        Assert.False(b.Io.Coils[4]);
        b.Seq.Clear();

        // 不到 120 s 又冲过去：先不开，加热棒留着；到点再开
        await Drive(b, s, t, 29.8, 31);
        foreach (var (tj, dt) in new[] { (30.06, 4.0), (30.12, 4.0), (30.18, 4.0) }) await Drive(b, s, t, tj, dt);
        Assert.True(b.Io.Coils[0]);
        Assert.False(b.Io.Coils[7]);
        Assert.DoesNotContain("开机", b.Seq);
        Assert.Contains("等冷水机最短间隔", ((IHeatSource)t).Regime);
        lock (b.Logs) Assert.Single(b.Logs, l => l.Level == "warn" && l.Text.Contains("刚开过（最短间隔 120 s）"));
        await Drive(b, s, t, 30.20, 4);
        Assert.False(b.Io.Coils[7]);

        b.Now = b.Now.AddSeconds(120);
        await Drive(b, s, t, 30.22, 1);
        Assert.True(b.Io.Coils[7]);
        Assert.Equal(new[] { "阀开", "开机", "DO0断", "DO7闭" }, b.Seq.Where(x => x is "阀开" or "开机" or "DO0断" or "DO7闭"));
        Assert.Contains("制冷段", ((IHeatSource)t).Regime);
    }

    [Fact]
    public async Task 选不开冷却水只管这一次_下次正常下发照常先通水_停控也清掉()
    {
        var b = Rig();
        var (s, t) = await Cooling(b);
        await using var _ = s;
        ((IHeatSource)t).SkipWaterNext();
        await t.SetTargetAsync(new TempTarget(10), CancellationToken.None);
        Assert.False(b.Io.Coils[4]);

        await t.SetTargetAsync(new TempTarget(8), CancellationToken.None);     // 配方 / 正常下发：没说不开
        await Tick(s);
        Assert.True(b.Io.Coils[4]);
        Assert.Contains("开机", b.Seq);

        // 停控把「下一次不开水」也清掉：说了不开、没下发就停了，下一次照常开水
        var b2 = Rig();
        var (s2, t2) = await Cooling(b2);
        await using var __ = s2;
        ((IHeatSource)t2).SkipWaterNext();
        await t2.StopAsync(CancellationToken.None);
        await t2.SetTargetAsync(new TempTarget(10), CancellationToken.None);
        Assert.True(b2.Io.Coils[4]);
    }

    [Fact]
    public async Task 选不开冷却水_阀本来开着就不碰_不替它关()
    {
        // 「不开」是不碰水，不是关水：先正常降温（自动开了水），再改目标选了不开——开着的阀和冷水机原样留着
        var b = Rig();
        var (s, t) = await Cooling(b);
        await using var _ = s;
        await t.SetTargetAsync(new TempTarget(10), CancellationToken.None);
        await Tick(s);                                    // 读到冷水机在转才算备好
        Assert.True(b.Io.Coils[4]);
        Assert.True(Water(s).Ready);
        b.Seq.Clear();

        ((IHeatSource)t).SkipWaterNext();
        await t.SetTargetAsync(new TempTarget(8), CancellationToken.None);
        await Tick(s);
        await Tick(s);

        Assert.True(b.Io.Coils[4]);
        Assert.Equal(2, b.Chiller.State);
        Assert.DoesNotContain(b.Seq, x => x is "阀关" or "关机");
    }

    [Fact]
    public async Task 冷却水Ready_阀开着且冷水机在转才算()
    {
        var b = Rig();
        await using var s = await Open(b);
        Assert.False(Water(s).Ready);
        b.Chiller.StartsSlowly = true;

        await Water(s).SetAsync(true, CancellationToken.None);
        Assert.False(Water(s).Ready);                     // 开机发了，还没读到它在转
        await Tick(s);
        Assert.True(Water(s).Ready);                      // 准备启动也算在转

        await Water(s).SetAsync(false, CancellationToken.None);
        Assert.False(Water(s).Ready);                     // 关水挂着
    }

    [Fact]
    public async Task 开冷却水_冷水机还没转起来时报Opening_转起来了收()
    {
        var b = Rig();
        await using var s = await Open(b);
        b.Chiller.StartsSlowly = true;                    // 收到开机先「准备启动」，下一拍才「正常运行」

        await Water(s).SetAsync(true, CancellationToken.None);
        Assert.True(Water(s).IsOpen);
        Assert.True(Water(s).Opening);
        Assert.Contains("开启中", Water(s).Status);

        await Tick(s);                                    // 读到「准备启动」：还没转稳，接着转菊花
        Assert.True(Water(s).Opening);
        Assert.Contains("准备启动", Water(s).Status);

        b.Chiller.Settle();
        await Tick(s);
        Assert.False(Water(s).Opening);
        Assert.Contains("正常运行", Water(s).Status);
    }

    [Fact]
    public async Task 开冷却水_冷水机一直没转起来_等满开机告警时限菊花也收()
    {
        var b = Rig();
        b.Chiller.StartStopMode = 0;                      // 远程指令不理：一直空闲
        await using var s = await Open(b);
        await Water(s).SetAsync(true, CancellationToken.None);
        Assert.True(Water(s).Opening);

        b.Now += TimeSpan.FromSeconds(61);
        await Tick(s);
        Assert.False(Water(s).Opening);                   // 不让开关一直转：告警已经说了冷水机没转
    }

    [Fact]
    public async Task 两路都不制冷了_先停冷水机_停稳再关阀()
    {
        var b = Rig();
        await using var s = await Open(b, TecOn());
        await Temp(s, 0).SetTargetAsync(new TempTarget(60), CancellationToken.None);
        b.Seq.Clear();

        await Temp(s, 0).StopAsync(CancellationToken.None);
        await Tick(s);                                    // 这一拍 TEC 断开、发停机

        Assert.False(b.Io.Coils[7]);
        Assert.Contains("关机", b.Seq);
        Assert.True(b.Io.Coils[4]);                       // 冷水机在延时停机：阀不关

        b.Chiller.Settle();
        await Tick(s);
        Assert.False(b.Io.Coils[4]);
        Assert.True(b.Seq.IndexOf("关机") < b.Seq.IndexOf("阀关"), string.Join(" / ", b.Seq));
    }

    [Fact]
    public async Task 安全停机_先停冷水机_阀等冷水机停稳()
    {
        var b = Rig();
        await using var s = await Open(b, TecOn());
        await Temp(s, 0).SetTargetAsync(new TempTarget(60), CancellationToken.None);

        var notes = await s.SafeStopAsync(0, CancellationToken.None);

        Assert.Contains(notes!, n => n.Contains("冷水机停机已发"));
        Assert.True(b.Io.Coils[4]);
        b.Chiller.Settle();
        await Tick(s);
        Assert.False(b.Io.Coils[4]);
    }

    [Fact]
    public async Task 有路在制冷_关冷却水被拒绝_冷水机不停()
    {
        var b = Rig();
        await using var s = await Open(b, TecOn());
        await Temp(s, 0).SetTargetAsync(new TempTarget(60), CancellationToken.None);
        b.Seq.Clear();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Water(s).SetAsync(false, CancellationToken.None));

        Assert.Empty(b.Seq);
        Assert.Equal(2, b.Chiller.State);
    }

    // ── 冷水机自己在转 ───────────────────────────────────────────────

    [Fact]
    public async Task 开机时冷水机在转_阀留着开_别的继电器照常全断()
    {
        var b = Rig();
        b.Chiller.State = 2;                              // 上一炉留着没停
        b.Io.Coils[4] = true;
        b.Io.Coils[0] = true;
        await using var s = await Open(b);

        Assert.True(b.Io.Coils[4]);                       // 转着关阀会憋泵：留着
        Assert.False(b.Io.Coils[0]);
        Assert.True(Water(s).IsOpen);
        lock (b.Logs) Assert.Contains(b.Logs, l => l.Text.Contains("开机时冷水机在转"));

        await Tick(s);
        Assert.True(b.Io.Coils[4]);                       // 按手动开的算：自动联动不去关它
    }

    [Fact]
    public async Task 开机时冷水机停着_八路全断()
    {
        var b = Rig();
        b.Io.Coils[4] = true;
        await using var s = await Open(b);
        Assert.False(b.Io.Coils[4]);
    }

    [Fact]
    public async Task 手动开水_冷水机答话慢_自动联动不抢跑关阀()
    {
        // 手动开水的旗子在序列起跑时就立：冷水机开机要等它答话（链路慢要数秒），期间采集拍
        // 会看到「阀开了、还没人认领」——从前这一拍就把阀自动关了（现场踩到：开水自己弹回关）
        var b = Rig();
        await using var s = await Open(b);
        b.Chiller.Mute = true;                        // 冷水机不应答：开机指令要等满超时

        var openTask = Water(s).SetAsync(true, CancellationToken.None);
        await Task.Delay(50);                         // 让序列先跑起来（阀已开、开机还在等应答）
        await Tick(s);                                // 采集拍挤进来——不该把阀关了
        await openTask;

        Assert.True(Water(s).IsOpen);
        Assert.False(Water(s).Closing);               // 没被挂上关水（开关看着弹回关就是它）
        Assert.True(b.Io.Coils[4]);
        Assert.DoesNotContain(b.Seq, x => x == "关机");   // 没人要关水，停机就不该发
    }

    [Fact]
    public async Task 台面重建重开_冷水机不停_阀不关()
    {
        // 重建重开 ≠ 真断开：拖一台设备 / 打开项目都会重建。旧会话打 Reconnecting 标记再收——
        // 不停冷水机、不关阀（物理状态原样留着）；新会话 ctx.Reconnecting，开机复位按物理现状接手
        var b = Rig();
        b.Chiller.State = 2;                              // 冷水机在转
        b.Io.Coils[4] = true;                             // 阀开着（上一炉留着）
        var s1 = await Open(b);
        Assert.True(Water(s1).IsOpen);

        ((DuoSession)s1).Reconnecting = true;             // 宿主在 CloseSessionsAsync 里打的那一下
        await s1.DisposeAsync();
        Assert.Equal(2, b.Chiller.State);                 // 没发停机
        Assert.True(b.Io.Coils[4]);                       // 阀没关

        await using var s2 = await Open(b, reconnecting: true);
        Assert.True(Water(s2).IsOpen);                    // 新会话接手：阀还开着
        Assert.Equal(2, b.Chiller.State);                 // 冷水机一直没被动过
        lock (b.Logs) Assert.Contains(b.Logs, l => l.Text.Contains("台面重建"));

        await s2.DisposeAsync();                          // 真断开（不打标记）：停机照发
        Assert.Equal(3, b.Chiller.State);                 // 延时停机
        Assert.True(b.Io.Coils[4]);                       // 还在转（延时停机），阀不关——憋泵那套逻辑不变
    }

    [Fact]
    public async Task 冷水机在转而阀被关了_下一拍先开阀()
    {
        var b = Rig();
        await using var s = await Open(b);
        b.Chiller.State = 2;                              // 有人在冷水机面板上开了机

        await Tick(s);

        Assert.True(b.Io.Coils[4]);
        lock (b.Logs) Assert.Contains(b.Logs, l => l.Text.Contains("憋泵"));
    }

    // ── 断开设备 ─────────────────────────────────────────────────────

    [Fact]
    public async Task 断开设备_冷水机停稳了才关阀()
    {
        var b = Rig();
        var s = await Open(b);
        await Water(s).SetAsync(true, CancellationToken.None);
        b.Chiller.StopsAtOnce = true;

        await s.DisposeAsync();

        Assert.False(b.Io.Coils[4]);
        Assert.True(b.Seq.IndexOf("关机") < b.Seq.IndexOf("阀关"), string.Join(" / ", b.Seq));
    }

    [Fact]
    public async Task 断开设备_冷水机等不到停稳_阀留着开_别的全断()
    {
        var b = Rig();
        var s = await Open(b);
        await Water(s).SetAsync(true, CancellationToken.None);
        b.Io.Coils[0] = true;

        await s.DisposeAsync();                           // 延时停机一直不走完

        Assert.True(b.Io.Coils[4]);
        Assert.False(b.Io.Coils[0]);
        lock (b.Logs) Assert.Contains(b.Logs, l => l.Text.Contains("断开设备") && l.Text.Contains("留着开"));
    }

    // ── 协议细节 ─────────────────────────────────────────────────────

    [Fact]
    public async Task 冷水机不认FC04和FC05_换FC03和FC06照样联动()
    {
        var b = Rig();
        b.Chiller.OnlyFc03 = true;
        b.Chiller.OnlyFc06 = true;
        await using var s = await Open(b);

        await Water(s).SetAsync(true, CancellationToken.None);
        Assert.Equal(2, b.Chiller.State);
        await Water(s).SetAsync(false, CancellationToken.None);
        b.Chiller.Settle();
        await Tick(s);
        Assert.False(b.Io.Coils[4]);
    }

    [Fact]
    public async Task 冷水机FC04被静默丢弃_等不到异常码_超时也换FC03()
    {
        // 有的固件对不认识的功能码连异常码都不回：主站只能等到超时——超时同样换 03
        var dev = new FakeChiller { DropFc04 = true, State = 2 };
        dev.Open();
        var ch = new ChillerClient(new ModbusRtuClient(dev, 1, 100) { TimeoutRetries = 1, RetryBackoffMs = 0 });
        Assert.Equal(2, await ch.ReadStateAsync());
        Assert.Equal(2, await ch.ReadStateAsync());                 // 换过去就一直用 03，不再白等
        Assert.Equal(2, dev.Requests.Count(r => r == "FC04静默丢弃"));   // 总线层重试 1 次：FC04 发了 2 次才认输
        Assert.Equal(2, dev.Requests.Count(r => r == "读状态"));
    }

    [Fact]
    public async Task 冷水机FC05被静默丢弃_超时也换FC06照样开关机()
    {
        var dev = new FakeChiller { DropFc05 = true };
        dev.Open();
        var ch = new ChillerClient(new ModbusRtuClient(dev, 1, 100) { TimeoutRetries = 1, RetryBackoffMs = 0 });
        await ch.StartAsync();
        Assert.Equal(2, (int)dev.State);                            // FC06 写 0x0072 起效
        await ch.StopAsync();
        Assert.Equal(3, (int)dev.State);                            // 默认走延时停机
    }

    [Fact]
    public async Task 起停方式是本地_开机时提醒改成本地加远程()
    {
        var b = Rig();
        b.Chiller.StartStopMode = 0;
        await using var s = await Open(b);
        lock (b.Logs) Assert.Contains(b.Logs, l => l.Text.Contains("起停方式") && l.Text.Contains("本地+远程"));
    }

    [Fact]
    public async Task 冷水机开机发了一直没转起来_报一次()
    {
        var b = Rig();
        b.Chiller.StartStopMode = 0;                      // 远程指令不理
        await using var s = await Open(b);
        await Water(s).SetAsync(true, CancellationToken.None);

        b.Now += TimeSpan.FromSeconds(61);
        await Tick(s);
        await Tick(s);

        lock (b.Logs) Assert.Single(b.Logs, l => l.Text.Contains("冷水机还是「空闲」"));
    }

    [Fact]
    public async Task 开机停机帧跟协议例子逐字节一致()
    {
        var t = new FrameLog();
        var ch = new ChillerClient(new ModbusRtuClient(t, 1, 50) { TimeoutRetries = 0 });
        await Assert.ThrowsAsync<TimeoutException>(() => ch.StartAsync());
        Assert.Equal("01 05 02 00 FF 00 8D 82", t.Frames[0]);       // 协议 §2.2.1 B 2）
        // FC05 超时（从站不应答）后换 FC06 写命令字——协议 §2.2.1 C/D 的示例帧同样逐字节对上
        Assert.Equal("01 06 02 00 00 72 08 57", t.Frames[1]);
        await Assert.ThrowsAsync<TimeoutException>(() => ch.StopAsync());
        Assert.Equal("01 06 02 00 00 70 89 96", t.Frames[2]);       // 换过通道就一直用 FC06

        // FC05 关机的 golden 帧也要钉：换一条新链路（这条已经把命令通道换成 FC06 了）
        var t2 = new FrameLog();
        var ch2 = new ChillerClient(new ModbusRtuClient(t2, 1, 50) { TimeoutRetries = 0 });
        await Assert.ThrowsAsync<TimeoutException>(() => ch2.StopAsync());
        Assert.Equal("01 05 02 01 FF 00 DC 42", t2.Frames[0]);
    }

    [Fact]
    public async Task 没配冷水机_阀照旧单独开关()
    {
        var b = Rig(withChiller: false);
        await using var s = await Open(b);
        await Water(s).SetAsync(true, CancellationToken.None);
        await Water(s).SetAsync(false, CancellationToken.None);
        Assert.False(b.Io.Coils[4]);
        Assert.Null(Water(s).Status);
        Assert.Null(Water(s).Alert);
    }

    [Fact]
    public async Task 冷水机报故障_读不到_能力报Alert短词_好了就清()
    {
        // 面板状态签上标红的那个短词：整句状态进了悬停提示，出毛病得在签上看得见
        var b = Rig();
        await using var s = await Open(b);
        Assert.Null(Water(s).Alert);

        b.Chiller.State = ChillerClient.Fault;
        await Tick(s);
        Assert.Equal("冷水机故障", Water(s).Alert);

        b.Chiller.State = ChillerClient.Idle;
        b.Chiller.Mute = true;
        await Tick(s);
        Assert.Equal("冷水机读不到", Water(s).Alert);

        b.Chiller.Mute = false;
        b.Now += TimeSpan.FromSeconds(11);                // 读不到之后隔 10 s 再试
        await Tick(s);
        Assert.Null(Water(s).Alert);
    }

    [Fact]
    public async Task 关水挂着_能力报Closing_再开一次撤销()
    {
        var b = Rig();
        await using var s = await Open(b);
        await Water(s).SetAsync(true, CancellationToken.None);
        Assert.False(Water(s).Closing);

        await Water(s).SetAsync(false, CancellationToken.None);
        Assert.True(Water(s).Closing);                    // 面板按「要的」显示成关
        Assert.True(Water(s).IsOpen);                     // 阀实际还开着

        await Water(s).SetAsync(true, CancellationToken.None);
        Assert.False(Water(s).Closing);
        Assert.Equal(2, b.Chiller.State);
    }

    [Fact]
    public async Task 冷水机串口跟IO8R撞口_开机就拒绝()
    {
        var b = new Bench();
        var drv = new DualStationDriver
        {
            LinksFactory = _ => new DuoLinks
            {
                Rd105 = new Rd105Link(b.Rd), RdPortName = "COM7",
                IoPort = b.Io, Io = new Io8rClient(new ModbusRtuClient(b.Io, 1, 200)), IoPortName = "COM10",
                ChillerPort = b.Chiller, Chiller = new ChillerClient(new ModbusRtuClient(b.Chiller, 1, 200)), ChillerPortName = "com10"
            }
        };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => drv.OpenAsync(Conn(), b.Ctx(), CancellationToken.None));
        Assert.Contains("冷水机串口", ex.Message);
        Assert.Contains("IO8R 串口", ex.Message);
    }

    [Fact]
    public void 冷水机串口缺省COM10_清空才不联动()
    {
        // 现场冷水机在 COM10（2026-11-09 用户定的，原先 COM8）：装好程序就联动；把串口清空才是不联动
        var cn = new ParameterSet().FillDefaults(new DualStationDriver().ConnectionSchema);
        Assert.Equal("COM10", cn.Str(DualStationDriver.Fields.PortChiller));
        using (var linked = DuoLinks.Serial(cn)) Assert.NotNull(linked.Chiller);

        cn[DualStationDriver.Fields.PortChiller] = "";
        using var links = DuoLinks.Serial(cn);
        Assert.Null(links.Chiller);
    }

    /// <summary>
    /// 包一层：IO8R 写 DO4 / DO7 和冷水机开关机按先后记进 Bench.Seq，好验顺序。
    /// </summary>
    private sealed class SeqTransport(ISerialTransport inner, Bench b, bool isChiller) : TecControl.Core.Comm.ISerialTransport
    {
        public bool IsOpen => inner.IsOpen;
        public void Open() => inner.Open();
        public void Close() => inner.Close();
        public void DiscardInput() => inner.DiscardInput();
        public void Dispose() => inner.Dispose();
        public int Read(byte[] buffer, int offset, int count, int timeoutMs) => inner.Read(buffer, offset, count, timeoutMs);

        public void Write(byte[] buffer, int offset, int count)
        {
            var f = buffer.AsSpan(offset, count);
            if (count >= 6 && isChiller && f[1] == 0x05) b.Seq.Add(f[3] == 0x00 ? "开机" : "关机");
            if (count >= 6 && isChiller && f[1] == 0x06) b.Seq.Add(f[5] == 0x72 ? "开机" : "关机");
            if (count >= 6 && !isChiller && f[1] == 0x05)
            {
                var on = f[4] == 0xFF;
                if (f[3] == 4) b.Seq.Add(on ? "阀开" : "阀关");
                else b.Seq.Add($"DO{f[3]}{(on ? "闭" : "断")}");
            }
            if (count >= 7 && !isChiller && f[1] == 0x0F)
            {
                var water = (f[7] >> 4 & 1) != 0;
                if (b.Io.Coils[4] != water) b.Seq.Add(water ? "阀开" : "阀关");
            }
            inner.Write(buffer, offset, count);
        }
    }

    /// <summary>只记下发的帧、不答（验帧格式）。</summary>
    private sealed class FrameLog : TecControl.Core.Comm.ISerialTransport
    {
        public List<string> Frames { get; } = new();
        public bool IsOpen => true;
        public void Open() { }
        public void Close() { }
        public void DiscardInput() { }
        public void Dispose() { }
        public int Read(byte[] buffer, int offset, int count, int timeoutMs) => 0;
        public void Write(byte[] buffer, int offset, int count)
            => Frames.Add(string.Join(" ", buffer.Skip(offset).Take(count).Select(x => x.ToString("X2"))));
    }
}
