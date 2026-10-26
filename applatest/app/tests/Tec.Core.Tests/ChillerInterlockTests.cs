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

        Assert.True(b.Io.Coils[6]);
        var i = b.Seq.IndexOf("阀开"); var j = b.Seq.IndexOf("开机"); var k = b.Seq.IndexOf("DO6闭");
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
        Assert.True(b.Io.Coils[6]);
        Assert.Equal(new[] { "阀开", "开机", "DO6闭" },
            b.Seq.Where(x => x is "阀开" or "阀关" or "开机" or "关机" or "DO6闭"));
        Assert.False(Water(s).Opening);                   // 冷水机转起来、通水延时过了：菊花收
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

        Assert.False(b.Io.Coils[6]);
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
    public void 冷水机串口缺省COM8_清空才不联动()
    {
        // 现场冷水机在 COM8（用户定的）：装好程序就联动；把串口清空才是不联动
        var cn = new ParameterSet().FillDefaults(new DualStationDriver().ConnectionSchema);
        Assert.Equal("COM8", cn.Str(DualStationDriver.Fields.PortChiller));
        using (var linked = DuoLinks.Serial(cn)) Assert.NotNull(linked.Chiller);

        cn[DualStationDriver.Fields.PortChiller] = "";
        using var links = DuoLinks.Serial(cn);
        Assert.Null(links.Chiller);
    }

    /// <summary>
    /// 包一层：IO8R 写 DO4 / DO6 和冷水机开关机按先后记进 Bench.Seq，好验顺序。
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
