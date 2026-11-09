using System.IO.Ports;
using Tec.Core.Benches;
using Tec.Driver.Abi;
using Tec.Drivers.Aqmd;
using Tec.Drivers.DualStation;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 搅拌电机驱动器（爱控 AQMD2408BLS-M）真机驱动的回归。全程走假 Modbus 从站——不插硬件也要能验证协议这一层。
/// 假从站的寄存器按 2026-11-09 现场串口工具读回的那两台填（型号名 AQMD2408BLS-M2、v1.3、极数 0、额定电流 7 A、
/// 偶校验、通讯中断停机 0），要测「配好了」的场景再把极数等改掉。
/// </summary>
public sealed class AqmdStirrerDriverTests
{
    /// <summary>装成现场那台驱动器：身份 / 电机参数 / 系统参数照实测回帧填；poles = 0 就是现场的「没配」。</summary>
    private static FakeModbusSlave MakeDrive(int poles = 4, bool learned = true, byte station = 1)
    {
        var d = new FakeModbusSlave { Station = station };
        d.Regs[0x0000] = 0x0203; d.Regs[0x0001] = 0x0103;                 // 设备标识、v1.3
        var name = "AQMD2408BLS-M2";
        for (var i = 0; i < 8; i++)
        {
            var hi = 2 * i < name.Length ? name[2 * i] : '\0';
            var lo = 2 * i + 1 < name.Length ? name[2 * i + 1] : '\0';
            d.Regs[0x0002 + i] = (ushort)(hi << 8 | lo);
        }
        d.Regs[0x000A] = 1000; d.Regs[0x000B] = 18000; d.Regs[0x000C] = 1000; d.Regs[0x000D] = 14;
        // 电机参数（现场回帧）
        d.Regs[0x0062] = 50000; d.Regs[0x0063] = 8000; d.Regs[0x0064] = 50000; d.Regs[0x0065] = 8000;
        d.Regs[0x0066] = 20000; d.Regs[0x0067] = 8000;
        d.Regs[0x006A] = 700; d.Regs[0x006B] = 700; d.Regs[0x006C] = 300;
        d.Regs[0x0073] = (ushort)poles; d.Regs[0x0074] = 10; d.Regs[0x0075] = (ushort)(learned ? 1 : 0); d.Regs[0x0077] = 0;
        // 系统参数（现场回帧）
        d.Regs[0x0091] = 9600; d.Regs[0x0092] = 2; d.Regs[0x0093] = 1; d.Regs[0x009C] = 1; d.Regs[0x0095] = 0; d.Regs[0x008E] = 0;
        // 实时：停着；极数没配时 0x0034 读回 0xFFFF（现场见过）
        d.Regs[0x0034] = poles == 0 ? (ushort)0xFFFF : (ushort)0;
        return d;
    }

    private static AqmdStirrerDriver Wire(FakeModbusSlave slave, SemaphoreSlim? busLock = null)
    {
        var drv = new AqmdStirrerDriver();
        // 归还回执照串口池的样子：引用落零就关口子——「探测完 / 开不了就让出串口」靠它才看得见
        drv.SerialFactory = cn => new SharedSerial(slave, busLock ?? new SemaphoreSlim(1, 1), release: slave.Close,
                                                   portName: cn.Str(AqmdStirrerDriver.FieldPort, "COM11"));
        return drv;
    }

    private static ParameterSet Conn(int periodMs = 5000)
        => ParameterSet.Of((AqmdStirrerDriver.FieldPort, "COM11"), (AqmdStirrerDriver.FieldPeriod, (double)periodMs));

    private static DriverContext Ctx(int channel, ParameterSet? config = null, List<(string, string)>? logs = null)
        => new()
        {
            InstanceId = $"ST{channel}",
            ChannelNumbers = new[] { channel },
            Config = config ?? new ParameterSet(),
            Simulated = false,
            TimeScale = 1,
            Clock = () => DateTimeOffset.Now,
            Log = logs is null ? (_, _) => { } : (l, t) => { lock (logs) logs.Add((l, t)); }
        };

    private static IStirrer StirOf(IDeviceSession s) => s.CapabilitiesOf(0).OfType<IStirrer>().Single();

    // ── 缺省值 / 探测 ─────────────────────────────────────────────

    [Fact]
    public void 缺省连接参数照现场_COM11_9600_偶校验_2停止位_站号1()
    {
        var drv = new AqmdStirrerDriver();
        var cn = new ParameterSet().FillDefaults(drv.ConnectionSchema);
        Assert.Equal("COM11", cn.Str(AqmdStirrerDriver.FieldPort));
        Assert.Equal(9600, cn.Num(AqmdStirrerDriver.FieldBaud));
        Assert.Equal(AqmdStirrerDriver.ParityEven, cn.Str(AqmdStirrerDriver.FieldParity));
        Assert.Equal("2", cn.Str(AqmdStirrerDriver.FieldStop));
        Assert.Equal(1, cn.Int(AqmdStirrerDriver.FieldAddr));
        // 帧格式真的传到串口层：偶校验 + 2 停止位（现场实测通的那档，不是手册写的 8E1）
        Assert.Equal(Parity.Even, AqmdStirrerDriver.ParityOf(cn.Str(AqmdStirrerDriver.FieldParity)));
        Assert.Equal(StopBits.Two, AqmdStirrerDriver.StopBitsOf(cn.Str(AqmdStirrerDriver.FieldStop)));
        Assert.Contains("8E2", AqmdStirrerDriver.LinkName(cn));
        // 真机驱动认领的是 ABI 那条「搅拌」，跟仿真机同一条——仿真调好的配方插上真机能跑
        Assert.Contains(drv.Commands, c => c.Id == CommandSpecs.Stir);
        Assert.Equal(0, drv.Info.ChannelsPerDevice);
    }

    [Fact]
    public async Task 测试连接_读得到型号固件极数学习状态_并把该补的点出来()
    {
        var drive = MakeDrive(poles: 0);
        var drv = Wire(drive);

        var probe = await drv.ProbeAsync(Conn(), CancellationToken.None);

        Assert.True(probe.Success);
        Assert.Contains("AQMD2408BLS-M2 v1.3", probe.Message);
        Assert.Contains("已学习", probe.Message);
        Assert.Contains("极数没配", probe.Message);                 // 现场那两台就是这样，连接前要填
        Assert.Contains("额定电流还是出厂 7.00 A", probe.Message);
        Assert.Contains("通讯中断停机 关", probe.Message);
        Assert.False(drive.IsOpen);                                  // 探测完释放口子，紧接着真正打开才不会「被占用」
    }

    [Fact]
    public async Task 测试连接_配置的帧格式不通_换着试并说改成哪档()
    {
        // 驱动器实际是 8E1，属性里填的 8E2：配置那档静默，换着试到偶校验/1 停止位才有应答
        var live = MakeDrive();
        var mute = MakeDrive(); mute.Mute = true;
        var drv = new AqmdStirrerDriver
        {
            SerialFactory = cn => new SharedSerial(
                cn.Str(AqmdStirrerDriver.FieldStop) == "1" && cn.Str(AqmdStirrerDriver.FieldParity) == AqmdStirrerDriver.ParityEven ? live : mute,
                new SemaphoreSlim(1, 1))
        };
        var cn = Conn(); cn[AqmdStirrerDriver.FieldParity] = AqmdStirrerDriver.ParityEven; cn[AqmdStirrerDriver.FieldStop] = "2";

        var probe = await drv.ProbeAsync(cn, CancellationToken.None);

        Assert.False(probe.Success);
        Assert.Contains("无应答", probe.Message);
        Assert.Contains("偶校验 / 1 停止位有应答", probe.Message);
        Assert.Contains("改成「1」", probe.Message);
    }

    // ── 开机核对 ─────────────────────────────────────────────────

    [Fact]
    public async Task 极数没配又没填_开不了并说怎么数圈()
    {
        var drive = MakeDrive(poles: 0);
        var drv = Wire(drive);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => drv.OpenAsync(Conn(), Ctx(1), CancellationToken.None));

        Assert.Contains("极数是 0", ex.Message);
        Assert.Contains("400 ÷ 圈数", ex.Message);
        Assert.False(drive.IsOpen);                                  // 开不了就把口子让出来
    }

    [Fact]
    public async Task 配置极数4_开机写进驱动器并存储_读回核对一致()
    {
        var drive = MakeDrive(poles: 0);
        var logs = new List<(string, string)>();
        var cfg = ParameterSet.Of((AqmdStirrerDriver.FieldPoles, 4d));

        await using var s = await Wire(drive).OpenAsync(Conn(), Ctx(1, cfg, logs), CancellationToken.None);

        Assert.Equal(4, drive.Regs[0x0073]);
        Assert.Contains("写寄存器 115=4", drive.Requests);           // 0x0073 极数
        Assert.Contains("写寄存器 352=1", drive.Requests);           // 0x0160 存储电机参数——06 写的不存就掉电丢
        Assert.Equal(4, ((AqmdStirrerSession)s).Poles);
        lock (logs) Assert.Contains(logs, l => l.Item2.Contains("极数 0 → 4") && l.Item2.Contains("读回核对一致"));
    }

    [Fact]
    public async Task 配置额定电流_开机把额定与最大负载电流都写成它()
    {
        var drive = MakeDrive();
        var cfg = ParameterSet.Of((AqmdStirrerDriver.FieldRatedCurrent, 2.5));

        await using var s = await Wire(drive).OpenAsync(Conn(), Ctx(1, cfg), CancellationToken.None);

        Assert.Equal(250, drive.Regs[0x006A]);
        Assert.Equal(250, drive.Regs[0x006B]);
        Assert.Contains("写多寄存器 106+2", drive.Requests);
    }

    [Fact]
    public async Task 未学习_开机拒绝并说按键学习()
    {
        var drive = MakeDrive(learned: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Wire(drive).OpenAsync(Conn(), Ctx(1), CancellationToken.None));

        Assert.Contains("未学习", ex.Message);
        Assert.Contains("按键", ex.Message);
    }

    [Fact]
    public async Task 极数填成奇数_不开串口就拒()
    {
        var drive = MakeDrive();
        var cfg = ParameterSet.Of((AqmdStirrerDriver.FieldPoles, 3d));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Wire(drive).OpenAsync(Conn(), Ctx(1, cfg), CancellationToken.None));

        Assert.Contains("偶数", ex.Message);
        Assert.Equal(0, drive.Opens);
    }

    [Fact]
    public async Task 通讯中断停机选5秒_开机写0x0095并存储系统参数()
    {
        var drive = MakeDrive();
        var cfg = ParameterSet.Of((AqmdStirrerDriver.FieldWatchdog, "5 s"), (AqmdStirrerDriver.FieldStallStop, "3 s"));

        await using var s = await Wire(drive).OpenAsync(Conn(), Ctx(1, cfg), CancellationToken.None);

        Assert.Equal(50, drive.Regs[0x0095]);
        Assert.Equal(30, drive.Regs[0x008E]);
        Assert.Contains("写寄存器 384=1", drive.Requests);           // 0x0180 存储系统参数
    }

    [Fact]
    public async Task 通讯中断停机不改_一个字不写()
    {
        var drive = MakeDrive();

        await using var s = await Wire(drive).OpenAsync(Conn(), Ctx(1), CancellationToken.None);

        Assert.DoesNotContain(drive.Requests, r => r.StartsWith("写", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 转速范围_下限自动按测速下限10Hz折算_上限受配置()
    {
        await using var s4 = await Wire(MakeDrive(poles: 4)).OpenAsync(Conn(), Ctx(1), CancellationToken.None);
        Assert.Equal(50, StirOf(s4).Limits.Min);                     // 10 Hz ÷ 4 × 20
        Assert.Equal(1000, StirOf(s4).Limits.Max);

        await using var s8 = await Wire(MakeDrive(poles: 8)).OpenAsync(Conn(), Ctx(1, ParameterSet.Of((AqmdStirrerDriver.FieldMaxRpm, 600d))), CancellationToken.None);
        Assert.Equal(25, StirOf(s8).Limits.Min);
        Assert.Equal(600, StirOf(s8).Limits.Max);
    }

    // ── 设速 / 停机 ───────────────────────────────────────────────

    [Fact]
    public async Task 设300rpm_4极写0x0043等于600_加减速按满量程5秒折算()
    {
        var drive = MakeDrive(poles: 4);
        var logs = new List<(string, string)>();
        await using var s = await Wire(drive).OpenAsync(Conn(), Ctx(1, logs: logs), CancellationToken.None);

        await StirOf(s).SetSpeedAsync(300, CancellationToken.None);

        // 300 rpm × 4 极 ÷ 20 = 60.0 Hz → ×10 = 600；满量程 1000 rpm = 200 Hz，缺省 5 s → 40 Hz/s → ×10 = 400
        Assert.Equal(600, drive.Regs[0x0043]);
        Assert.Equal(400, drive.Regs[0x0052]);
        Assert.Equal(400, drive.Regs[0x0053]);
        Assert.Contains("写多寄存器 82+2", drive.Requests);          // 加减速一帧写两个，且写在目标之前
        Assert.True(drive.Requests.IndexOf("写多寄存器 82+2") < drive.Requests.IndexOf("写寄存器 67=600"));
        lock (logs) Assert.Contains(logs, l => l.Item2.Contains("搅拌设 300 rpm") && l.Item2.Contains("60.0 Hz"));
    }

    [Fact]
    public async Task 反转配置_目标写成负的换向频率()
    {
        var drive = MakeDrive(poles: 4);
        var cfg = ParameterSet.Of((AqmdStirrerDriver.FieldDirection, AqmdStirrerDriver.DirReverse));
        await using var s = await Wire(drive).OpenAsync(Conn(), Ctx(1, cfg), CancellationToken.None);

        await StirOf(s).SetSpeedAsync(300, CancellationToken.None);

        Assert.Equal(unchecked((ushort)(short)-600), drive.Regs[0x0043]);
    }

    [Fact]
    public async Task 转速超出上限_按边界下发并说明()
    {
        var drive = MakeDrive(poles: 4);
        var logs = new List<(string, string)>();
        await using var s = await Wire(drive).OpenAsync(Conn(), Ctx(1, logs: logs), CancellationToken.None);

        await StirOf(s).SetSpeedAsync(5000, CancellationToken.None);

        Assert.Equal(2000, drive.Regs[0x0043]);                      // 1000 rpm = 200.0 Hz
        lock (logs) Assert.Contains(logs, l => l.Item2.Contains("超出范围"));
    }

    [Fact]
    public async Task 斜坡_IStirrerRamp给的满量程用时下一次设速用掉_之后回缺省()
    {
        var drive = MakeDrive(poles: 4);
        await using var s = await Wire(drive).OpenAsync(Conn(), Ctx(1), CancellationToken.None);
        var stir = StirOf(s);

        ((IStirrerRamp)stir).SetRampSeconds(20);                      // 200 Hz / 20 s = 10 Hz/s → 100
        await stir.SetSpeedAsync(300, CancellationToken.None);
        Assert.Equal(100, drive.Regs[0x0052]);

        await stir.SetSpeedAsync(400, CancellationToken.None);        // 没再给斜坡：回缺省 5 s → 400
        Assert.Equal(400, drive.Regs[0x0052]);
    }

    [Fact]
    public async Task 停机写0x0040正常停止_转速0也走停机()
    {
        var drive = MakeDrive(poles: 4);
        await using var s = await Wire(drive).OpenAsync(Conn(), Ctx(1), CancellationToken.None);
        var stir = StirOf(s);
        await stir.SetSpeedAsync(300, CancellationToken.None);

        await stir.StopAsync(CancellationToken.None);
        Assert.Contains("写寄存器 64=0", drive.Requests);

        drive.Requests.Clear();
        await stir.SetSpeedAsync(0, CancellationToken.None);
        Assert.Contains("写寄存器 64=0", drive.Requests);
        Assert.DoesNotContain(drive.Requests, r => r.StartsWith("写寄存器 67=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 写被驱动器拒绝0x60_报人话说去学习()
    {
        var drive = MakeDrive(poles: 4);
        await using var s = await Wire(drive).OpenAsync(Conn(), Ctx(1), CancellationToken.None);
        drive.RejectWith = 0x60;                                      // 现场第一次写加减速吃到的就是 01 86 60

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => StirOf(s).SetSpeedAsync(300, CancellationToken.None));

        Assert.Contains("0x60", ex.Message);
        Assert.Contains("学习", ex.Message);
    }

    // ── 轮询 ─────────────────────────────────────────────────────

    [Fact]
    public async Task 轮询_按换向频率和极数算转速_电流故障码出样到所绑通道_不信0x0034()
    {
        var drive = MakeDrive(poles: 4);
        await using var s = await Wire(drive).OpenAsync(Conn(), Ctx(2), CancellationToken.None);
        var got = new List<Sample>();
        using var sub = s.Samples.Subscribe(new Collect(x => { lock (got) got.Add(x); }));

        drive.Regs[0x0022] = 600;                                     // 60.0 Hz → 300 rpm
        drive.Regs[0x0021] = 123;                                     // 1.23 A
        drive.Regs[0x0034] = 0xFFFF;                                  // 驱动器自己算的 rpm 是废的（极数没配时现场读回的就是它）
        await ((AqmdStirrerSession)s).PollOnceAsync(CancellationToken.None);

        lock (got)
        {
            Assert.Equal(300, got.Single(x => x is { Tag: "rpm", Channel: 2 }).Value);
            Assert.Equal(1.23, got.Single(x => x is { Tag: "stirI", Channel: 2 }).Value, 2);
            Assert.Equal(0, got.Single(x => x is { Tag: "stirFault", Channel: 2 }).Value);
        }
        Assert.Equal(300, StirOf(s).CurrentRpm);
    }

    [Fact]
    public async Task 反转时换向频率是负的_转速照样报正数()
    {
        var drive = MakeDrive(poles: 4);
        await using var s = await Wire(drive).OpenAsync(Conn(), Ctx(1), CancellationToken.None);

        drive.Regs[0x0022] = unchecked((ushort)(short)-600);
        await ((AqmdStirrerSession)s).PollOnceAsync(CancellationToken.None);

        Assert.Equal(300, StirOf(s).CurrentRpm);
    }

    [Fact]
    public async Task 故障码堵转_会话标故障_日志带处置办法_归零恢复()
    {
        var drive = MakeDrive(poles: 4);
        var logs = new List<(string, string)>();
        await using var s = await Wire(drive).OpenAsync(Conn(), Ctx(1, logs: logs), CancellationToken.None);
        await s.StartAsync(CancellationToken.None);                   // 轮询周期 5 s，测试里自己推拍
        var ps = (AqmdStirrerSession)s;
        Assert.Equal(DeviceState.Ready, s.State);

        drive.Regs[0x0033] = 2;                                       // 堵转停止
        await ps.PollOnceAsync(CancellationToken.None);
        Assert.Equal(DeviceState.Faulted, s.State);
        lock (logs) Assert.Contains(logs, l => l.Item1 == "error" && l.Item2.Contains("堵转停止") && l.Item2.Contains("桨叶"));

        drive.Regs[0x0033] = 0;
        await ps.PollOnceAsync(CancellationToken.None);
        Assert.Equal(DeviceState.Ready, s.State);
        lock (logs) Assert.Contains(logs, l => l.Item2.Contains("故障已清"));
    }

    [Fact]
    public async Task 串口中途死了_读失败立刻关掉重开_口子回来就接着出数()
    {
        var drive = MakeDrive(poles: 4);
        var logs = new List<(string, string)>();
        await using var s = await Wire(drive).OpenAsync(Conn(), Ctx(1, logs: logs), CancellationToken.None);
        var ps = (AqmdStirrerSession)s;
        var got = new List<Sample>();
        using var sub = s.Samples.Subscribe(new Collect(x => { lock (got) got.Add(x); }));
        await ps.PollOnceAsync(CancellationToken.None);
        lock (got) Assert.Equal(3, got.Count);

        drive.Dead = true;                                            // 句柄死了：一写就抛 IO 错
        var opens = drive.Opens;
        await ps.PollOnceAsync(CancellationToken.None);
        Assert.Equal(1, ps.LinkReopens);
        Assert.Equal(opens + 1, drive.Opens);
        Assert.False(drive.Dead);
        lock (logs) Assert.Contains(logs, l => l.Item2.Contains("串口已关掉重开"));

        await ps.PollOnceAsync(CancellationToken.None);
        lock (got) Assert.Equal(6, got.Count);
        lock (logs) Assert.Contains(logs, l => l.Item2.Contains("搅拌驱动器恢复"));
    }

    // ── 配方指令 / 安全停机 ───────────────────────────────────────

    [Fact]
    public async Task 配方搅拌指令_认领并执行_到达用时按Δ转速折算_转速0走停机()
    {
        var drive = MakeDrive(poles: 4);
        await using var s = await Wire(drive).OpenAsync(Conn(), Ctx(1), CancellationToken.None);
        var handler = s.Resolve(CommandSpecs.Stir);
        Assert.NotNull(handler);
        Assert.Null(s.Resolve(CommandSpecs.Control));                 // 不认领温度指令

        var caps = new CapabilitySet();
        caps.Add(s.CapabilitiesOf(0));
        var ctx = new CommandContext { Channel = 1, Capabilities = caps, Now = () => DateTimeOffset.Now, TimeScale = 1000 };

        // 从 0 到 300 rpm 用 10 s：满量程 1000 rpm 折成 33.3 s → 200 Hz / 33.3 = 6 Hz/s → 60
        var outcome = await handler!.ExecuteAsync(ctx, ParameterSet.Of(("rpm", 300d), ("task", "按时长"), ("ramp", 10d)), CancellationToken.None);
        Assert.Equal(EndReason.Reached, outcome.Reason);
        Assert.Equal(600, drive.Regs[0x0043]);
        Assert.Equal(60, drive.Regs[0x0052]);

        drive.Requests.Clear();
        await handler.ExecuteAsync(ctx, ParameterSet.Of(("rpm", 0d), ("task", "立即")), CancellationToken.None);
        Assert.Contains("写寄存器 64=0", drive.Requests);
    }

    [Fact]
    public async Task 安全停机不停搅拌_照实说保持多少转()
    {
        var drive = MakeDrive(poles: 4);
        await using var s = await Wire(drive).OpenAsync(Conn(), Ctx(1), CancellationToken.None);
        await StirOf(s).SetSpeedAsync(300, CancellationToken.None);
        drive.Regs[0x0022] = 600;
        await ((AqmdStirrerSession)s).PollOnceAsync(CancellationToken.None);
        drive.Requests.Clear();

        var did = await s.SafeStopAsync(0, CancellationToken.None);

        Assert.NotNull(did);
        Assert.Contains(did!, l => l.Contains("搅拌保持 300 rpm"));
        Assert.DoesNotContain(drive.Requests, r => r.StartsWith("写", StringComparison.Ordinal));   // 一个寄存器都没动
    }

    [Fact]
    public async Task 两台并在一条485上_站号1和2各答各的()
    {
        var a = MakeDrive(poles: 4, station: 1);
        var b = MakeDrive(poles: 8, station: 2);
        var bus = new FakeModbusBus(a, b);
        var busLock = new SemaphoreSlim(1, 1);
        var drvA = new AqmdStirrerDriver { SerialFactory = _ => new SharedSerial(bus, busLock) };
        var drvB = new AqmdStirrerDriver { SerialFactory = _ => new SharedSerial(bus, busLock) };
        var cnB = Conn(); cnB[AqmdStirrerDriver.FieldAddr] = 2d;

        await using var sa = await drvA.OpenAsync(Conn(), Ctx(1), CancellationToken.None);
        await using var sb = await drvB.OpenAsync(cnB, Ctx(2), CancellationToken.None);

        await StirOf(sa).SetSpeedAsync(300, CancellationToken.None);
        await StirOf(sb).SetSpeedAsync(300, CancellationToken.None);
        Assert.Equal(600, a.Regs[0x0043]);                           // 4 极：60 Hz
        Assert.Equal(1200, b.Regs[0x0043]);                          // 8 极：120 Hz
        Assert.Equal(50, StirOf(sa).Limits.Min);
        Assert.Equal(25, StirOf(sb).Limits.Min);
    }

    private sealed class Collect(Action<Sample> onNext) : IObserver<Sample>
    {
        public void OnNext(Sample value) => onNext(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
