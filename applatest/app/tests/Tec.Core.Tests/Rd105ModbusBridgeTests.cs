using Tec.Driver.Abi;
using Tec.Drivers.DualStation;
using Tec.Drivers.Rd105;
using TecControl.Core.Comm;
using TecControl.Core.Protocol;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// RD105 走 RS485 口 = Modbus-RTU（协议 §1 / §2.2）。Rd105ModbusBridge 让按 ASCII 写的
/// 整条链路（TecClient → TecController）原样跑在 Modbus 上：这里盯寄存器地址、字序、
/// 功能码、回读回显、型号换名、DATADEMAND 的拼法，以及「表里没有就拒绝、不答就报超时」。
/// 从站是 FakeModbusSlave（跟 IO8R / 宇电共用的那台假从站）。
/// </summary>
public sealed class Rd105ModbusBridgeTests
{
    private static void Set32(FakeModbusSlave s, int addr, int v)
    {
        s.Regs[addr] = (ushort)((uint)v >> 16);
        s.Regs[addr + 1] = (ushort)v;
    }

    private static void Set64(FakeModbusSlave s, int addr, ulong v)
    {
        for (var i = 0; i < 4; i++) s.Regs[addr + i] = (ushort)(v >> 16 * (3 - i));
    }

    private static int Get32(FakeModbusSlave s, int addr)
        => (int)((uint)s.Regs.GetValueOrDefault(addr) << 16 | s.Regs.GetValueOrDefault(addr + 1));

    /// <summary>一台像 215L 的假 RD105：两路目标、两路实测、电阻、自身温度。</summary>
    private static FakeModbusSlave Device(byte station = 1)
    {
        var s = new FakeModbusSlave { Station = station };
        s.Regs[0x0001] = 4;              // TEC 型号代码 4 = 215L（协议 §3.5.1）
        s.Regs[0x000C] = 130;            // FPV 1.3.0
        Set32(s, 0x1000, 2500000);       // TC1:TG 25.00000
        Set32(s, 0x2000, 3000000);       // TC2:TG 30.00000
        Set32(s, 0x1002, 2259187);       // TC1:TCADJTEMP（协议 DATADEMAND 示例里的数）
        Set64(s, 0x1004, 11139104486);   // TC1:RESISTOR 11139.104486 Ω
        Set32(s, 0x2002, 999999999);     // TC2 未接传感器
        Set64(s, 0x2004, 0);
        s.Regs[0x0003] = 23;             // SINTERIORTEMP
        return s;
    }

    private static Rd105Link Link(FakeModbusSlave s, byte station = 1)
    {
        var link = new Rd105Link(new Rd105ModbusBridge(s, station, 200));
        link.Client.ReplyTimeoutMs = 100;
        link.Open();
        return link;
    }

    [Fact]
    public async Task 查询走03功能码_通道二加0x1000_多字寄存器高字在前()
    {
        var s = Device();
        using var link = Link(s);

        Assert.Equal(2500000, await link.Client.QueryAsync(1, TecCmd.Target));
        Assert.Equal(3000000, await link.Client.QueryAsync(2, TecCmd.Target));

        Assert.Contains("读寄存器 4096+2", s.Requests);     // 0x1000
        Assert.Contains("读寄存器 8192+2", s.Requests);     // 0x2000
        // 协议 §2.2 那帧：01 03 10 00 00 02 + CRC
        Assert.Contains(s.Raw, f => f.Length == 8 && f[0] == 1 && f[1] == 3 && f[2] == 0x10 && f[3] == 0 && f[5] == 2);
    }

    [Fact]
    public async Task 设置走10功能码_负数按补码_设完回读设备里的值再回显()
    {
        var s = Device();
        using var link = Link(s);

        await link.Client.SetAsync(1, TecCmd.Target, -1500000);     // −15 ℃

        Assert.Equal(-1500000, Get32(s, 0x1000));
        var wr = s.Requests.IndexOf("写多寄存器 4096+2");
        Assert.True(wr >= 0);
        // 写完紧跟一次回读：回显的是设备实际存下的值，不是自己念一遍下发值
        Assert.Equal("读寄存器 4096+2", s.Requests[wr + 1]);
    }

    [Fact]
    public async Task 型号寄存器是代码_桥上换成名字_固件号照数字给()
    {
        var s = Device();
        using var link = Link(s);

        var (model, firmware, _) = await link.Controller.ReadDeviceInfoAsync();

        Assert.Equal("215L", model);
        Assert.Equal("v1.3.0", firmware);
    }

    [Fact]
    public async Task 快照走DATADEMAND_一个参数一帧_OUTV没有寄存器就如实缺_读成NaN()
    {
        var s = Device();
        using var link = Link(s);

        var snap = await link.Controller.ReadSnapshotAsync();

        Assert.Equal(22.59187, snap.Temp1C, 5);
        Assert.Equal(11139.104486, snap.Resistor1Ohms, 6);
        Assert.True(double.IsNaN(snap.Temp2C));            // 999999999 = 未接
        Assert.True(double.IsNaN(snap.OutVolts1));         // Modbus 没有 OUTV，不编
        Assert.Equal(23, snap.InternalTempC);
        // 一个参数一帧，不跨参数连读（现场固件按参数长度答）
        Assert.Contains("读寄存器 4098+2", s.Requests);     // 0x1002 TCADJTEMP
        Assert.Contains("读寄存器 4100+4", s.Requests);     // 0x1004 RESISTOR
        Assert.Contains("读寄存器 8194+2", s.Requests);
        Assert.Contains("读寄存器 8196+4", s.Requests);
        Assert.Contains("读寄存器 3+1", s.Requests);
        Assert.DoesNotContain(s.Requests, r => r.EndsWith("+6"));
    }

    [Fact]
    public async Task 固件按参数长度答不按请求个数答_电阻答不出来就不带_温度照常()
    {
        // 现场：读 RESISTOR（uint64，4 个寄存器）固件只给 2 个——「应答长度不对」。
        // 电阻驱动没用，不带就是了；温度那一路必须照常出来，轮询不能因此断
        var s = Device();
        s.RegsPerReadCap = 2;
        using var link = Link(s);

        var snap = await link.Controller.ReadSnapshotAsync();

        Assert.Equal(22.59187, snap.Temp1C, 5);
        Assert.True(double.IsNaN(snap.Resistor1Ohms));
        Assert.Equal(23, snap.InternalTempC);
        Assert.Contains("读寄存器 4100+4", s.Requests);     // 试过了，是固件答不出来，不是没问
    }

    [Fact]
    public async Task 寄存器表里没有的指令如实拒绝_不猜地址()
    {
        var s = Device();
        using var link = Link(s);

        var ex = await Assert.ThrowsAsync<TecProtocolException>(() => link.Client.QueryAsync(1, "NOSUCH"));
        Assert.Contains("寄存器表里没有", ex.Message);
        Assert.DoesNotContain(s.Requests, r => r.StartsWith("读寄存器"));
    }

    [Fact]
    public async Task 从站不答_报的是Modbus的超时_带站号()
    {
        var s = Device();
        s.Mute = true;
        using var link = Link(s);

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => link.Client.QueryAsync(1, TecCmd.Target));
        Assert.Contains("Modbus-RTU 站号 1", ex.Message);
    }

    [Fact]
    public async Task 双工位主机走Modbus桥_开机把两路保护值写进寄存器()
    {
        var s = Device();
        var drv = new DualStationDriver
        {
            LinksFactory = _ => new DuoLinks
            {
                Rd105 = new Rd105Link(new Rd105ModbusBridge(s, 1, 200)),
                RdProtocol = Rd105Protocol.Modbus
            }
        };
        var cfg = ParameterSet.Of((Rd105TecDriver.FieldOverUp, 150d), (Rd105TecDriver.FieldOverLow, -30d));
        var ctx = new DriverContext
        {
            InstanceId = "DUO1", ChannelNumbers = new[] { 1, 2 }, Config = cfg,
            Clock = () => DateTimeOffset.Now, Log = (_, _) => { }
        };

        await using var session = await drv.OpenAsync(ParameterSet.Of((DualStationDriver.Fields.HasIo, "无")), ctx, CancellationToken.None);

        Assert.Equal(150_00000, Get32(s, 0x133D));
        Assert.Equal(-30_00000, Get32(s, 0x133F));
        Assert.Equal(150_00000, Get32(s, 0x233D));
        Assert.Equal(-30_00000, Get32(s, 0x233F));
    }

    /// <summary>开得了口、永远不回话的串口。</summary>
    private sealed class SilentPort : ISerialTransport
    {
        public bool IsOpen { get; private set; }
        public void Open() => IsOpen = true;
        public void Close() => IsOpen = false;
        public void DiscardInput() { }
        public void Write(byte[] buffer, int offset, int count) { }
        public int Read(byte[] buffer, int offset, int count, int timeoutMs) { Thread.Sleep(Math.Min(timeoutMs, 10)); return 0; }
        public void Dispose() { }
    }

    [Fact]
    public async Task 探测_按ASCII配的没应答_换着试出Modbus有应答_话里说清改成什么()
    {
        // 现场那种情况：接的是 485 口（Modbus），表单里还是默认的 ASCII
        var s = Device();
        var drv = new DualStationDriver
        {
            LinksFactory = cn =>
            {
                var proto = cn.Str(DualStationDriver.Fields.ProtoRd105, Rd105Protocol.Ascii);
                var baud = (int)cn.Num(DualStationDriver.Fields.BaudRd105, 38400);
                var link = Rd105Protocol.IsModbus(proto)
                    ? new Rd105Link(new Rd105ModbusBridge(s, 1, 100))
                    : new Rd105Link(new SilentPort());
                link.Client.ReplyTimeoutMs = 60;
                return new DuoLinks { Rd105 = link, RdPortName = "COM7", RdBaud = baud, RdProtocol = proto };
            }
        };
        var cn = ParameterSet.Of((DualStationDriver.Fields.HasIo, "无"),
                                 (DualStationDriver.Fields.ProtoRd105, Rd105Protocol.Ascii),
                                 (DualStationDriver.Fields.BaudRd105, "38400"));

        var r = await drv.ProbeAsync(cn, CancellationToken.None);

        Assert.False(r.Success);                                   // 按配置的那套没通，就不算通
        Assert.Contains("COM7 @ 38400（ASCII） 无应答", r.Message);
        Assert.Contains("Modbus-RTU 站号 1 @ 38400 有应答（型号 215L）", r.Message);
        Assert.Contains("改成「Modbus-RTU（RS485 口）」", r.Message);
    }

    [Fact]
    public async Task 探测_换着试也全没应答_把试过的都列出来()
    {
        var drv = new DualStationDriver
        {
            LinksFactory = cn =>
            {
                var link = new Rd105Link(new SilentPort());
                link.Client.ReplyTimeoutMs = 60;
                return new DuoLinks { Rd105 = link, RdPortName = "COM7",
                                      RdBaud = (int)cn.Num(DualStationDriver.Fields.BaudRd105, DualStationDriver.Defaults.BaudRd105),
                                      RdProtocol = cn.Str(DualStationDriver.Fields.ProtoRd105, DualStationDriver.Defaults.ProtoRd105) };
            }
        };

        // 协议 / 波特率一项没填：按缺省（Modbus-RTU、38400）试，再换着试另外三档
        var r = await drv.ProbeAsync(ParameterSet.Of((DualStationDriver.Fields.HasIo, "无")), CancellationToken.None);

        Assert.False(r.Success);
        Assert.Contains("COM7 @ 38400（Modbus-RTU 站号 1） 无应答", r.Message);
        Assert.Contains("换着试了 ASCII @ 38400、Modbus-RTU 站号 1 @ 9600、ASCII @ 9600 也都没应答", r.Message);
    }

    [Fact]
    public void 协议与波特率的候选_先换协议再换出厂另一档()
    {
        Assert.Equal(new[] { (Rd105Protocol.Modbus, 38400), (Rd105Protocol.Ascii, 9600), (Rd105Protocol.Modbus, 9600) },
                     Rd105Protocol.Alternatives(Rd105Protocol.Ascii, 38400).ToArray());
        Assert.Equal(new[] { (Rd105Protocol.Ascii, 9600), (Rd105Protocol.Modbus, 38400), (Rd105Protocol.Ascii, 38400) },
                     Rd105Protocol.Alternatives(Rd105Protocol.Modbus, 9600).ToArray());
        // 现场改过的档（115200）也只换到出厂那档去试
        Assert.Equal(new[] { (Rd105Protocol.Modbus, 115200), (Rd105Protocol.Ascii, 38400), (Rd105Protocol.Modbus, 38400) },
                     Rd105Protocol.Alternatives(Rd105Protocol.Ascii, 115200).ToArray());
    }

    [Fact]
    public async Task 应答前带485切换的脏字节_F8打头_往后滑一个就对上_照样读得对()
    {
        // 现场第一次接 485 口读到的就是这样：F8 01 03 04 …——真应答跟在一个脏字节后面
        var s = Device();
        s.LeadingNoise = new byte[] { 0xF8 };
        using var link = Link(s);

        Assert.Equal(2500000, await link.Client.QueryAsync(1, TecCmd.Target));
        var (model, _, _) = await link.Controller.ReadDeviceInfoAsync();
        Assert.Equal("215L", model);
    }

    [Fact]
    public async Task 脏字节太多滑不到站号_报站号不对_把收到的开头列出来()
    {
        var s = Device();
        s.LeadingNoise = new byte[] { 0xF8, 0xFF, 0xFE, 0xF8, 0xFF, 0xFE };
        using var link = Link(s);

        var ex = await Assert.ThrowsAsync<TecProtocolException>(() => link.Client.QueryAsync(1, TecCmd.Target));
        Assert.Contains("站号不对", ex.Message);
        Assert.Contains("F8 FF FE F8", ex.Message);      // 收到的开头原样列出来，现场对得上示波器
    }

    [Fact]
    public async Task 相邻两帧之间留静默_桥上一条指令连发的几帧不粘在一起()
    {
        // RTU 靠 3.5 字符静默分帧。设置一条 TG = 写一帧 + 回读一帧，中间至少要隔 InterFrameGapMs
        var s = Device();
        using var link = Link(s);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        await link.Client.SetAsync(1, TecCmd.Target, 2600000);

        Assert.Equal(new[] { "写多寄存器 4096+2", "读寄存器 4096+2" }, s.Requests);
        Assert.True(sw.ElapsedMilliseconds >= 10, $"两帧之间没留够静默：只用了 {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task 报错带上这一问一答的原始字节_对得上协议示例帧()
    {
        // 协议 §2.2 示例：读 TC1:TG 发 01 03 10 00 00 02 C0 CB。从站不答 → 报错里得有这一帧原样
        var s = Device();
        s.Mute = true;
        using var link = Link(s);
        var ex = await Assert.ThrowsAsync<TimeoutException>(() => link.Client.QueryAsync(1, TecCmd.Target));
        Assert.Contains("发 01 03 10 00 00 02 C0 CB｜收 无", ex.Message);

        // 答话前带脏字节且滑不到站号：收到的字节也原样列出
        var s2 = Device();
        s2.LeadingNoise = new byte[] { 0xF8, 0xFF, 0xFE, 0xF8, 0xFF, 0xFE };
        using var link2 = Link(s2);
        var ex2 = await Assert.ThrowsAsync<TecProtocolException>(() => link2.Client.QueryAsync(1, TecCmd.Target));
        Assert.Contains("｜收 F8 FF FE F8 FF FE", ex2.Message);
    }
}
