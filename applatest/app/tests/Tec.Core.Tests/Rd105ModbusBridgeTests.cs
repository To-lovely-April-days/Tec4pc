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
    public async Task 快照走DATADEMAND_两路各一帧读6个寄存器_OUTV没有寄存器就如实缺_读成NaN()
    {
        var s = Device();
        using var link = Link(s);

        var snap = await link.Controller.ReadSnapshotAsync();

        Assert.Equal(22.59187, snap.Temp1C, 5);
        Assert.Equal(11139.104486, snap.Resistor1Ohms, 6);
        Assert.True(double.IsNaN(snap.Temp2C));            // 999999999 = 未接
        Assert.True(double.IsNaN(snap.OutVolts1));         // Modbus 没有 OUTV，不编
        Assert.Equal(23, snap.InternalTempC);
        Assert.Contains("读寄存器 4098+6", s.Requests);     // 0x1002 起 TCADJTEMP(2)+RESISTOR(4) 一帧
        Assert.Contains("读寄存器 8194+6", s.Requests);
        Assert.Contains("读寄存器 3+1", s.Requests);
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
                return new DuoLinks { Rd105 = link, RdPortName = "COM7", RdBaud = (int)cn.Num(DualStationDriver.Fields.BaudRd105, 38400),
                                      RdProtocol = cn.Str(DualStationDriver.Fields.ProtoRd105, Rd105Protocol.Ascii) };
            }
        };

        var r = await drv.ProbeAsync(ParameterSet.Of((DualStationDriver.Fields.HasIo, "无")), CancellationToken.None);

        Assert.False(r.Success);
        Assert.Contains("换着试了 Modbus-RTU 站号 1 @ 38400、ASCII @ 9600、Modbus-RTU 站号 1 @ 9600 也都没应答", r.Message);
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
}
