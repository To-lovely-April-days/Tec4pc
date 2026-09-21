using Tec.Drivers.Rd105.Modbus;
using Tec.Drivers.DualStation.Yudian;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 宇电 AI-8848G D91 访问层（双工位反应主机实施第 2 步，需求 §4）。
/// 盯手册里的三个坑：标度自己算（InP 查表 / 线性看 dPt）、报警状态高低字节
/// 奇偶通道分家、Loc 锁着要报出来；再加两条「接反了」的自检——
/// 温度口插 pH 表、pH 口插温度表，都得在 Init 就说清，不能等曲线画歪。
/// </summary>
public class YudianClientTests
{
    private static (FakeModbusSlave dev, YudianClient yd) Rig()
    {
        var dev = new FakeModbusSlave();
        dev.Open();
        return (dev, new YudianClient(new ModbusRtuClient(dev, 1, 200)));
    }

    /// <summary>装成一台典型 J7：4 路都开、都用同编号组，CH1/CH2 是 Pt100。</summary>
    private static void MakeJ7(FakeModbusSlave dev)
    {
        for (var i = 0; i < 4; i++) dev.Regs[384 + i] = (ushort)(i + 1);  // In：个位 = 组号
        dev.Regs[2048] = 21;   // 组1：Pt100 -200~+800，一位小数
        dev.Regs[2049] = 22;   // 组2：Pt100 -200.00~+300.00，两位小数
        dev.Regs[2050] = 0;    // 组3：K
        dev.Regs[2051] = 0;    // 组4：K
        dev.Regs[2128] = 1;    // dPt（只影响面板，测温型标度不该看它）
        dev.Regs[2130] = 0;    // Loc 没锁
        dev.Regs[2131] = 0x1234;
    }

    [Fact]
    public async Task J7_标度按InP查表_与dPt无关()
    {
        var (dev, yd) = Rig();
        MakeJ7(dev);
        dev.Regs[1536] = 250;    // CH1 Pt100 ×10 → 25.0
        dev.Regs[1537] = 2500;   // CH2 Pt100 ×100 → 25.00
        await yd.InitAsync(YudianKind.Thermal);
        var r = await yd.ReadAsync();
        Assert.Equal(25.0, r[0].Value);
        Assert.Equal(25.0, r[1].Value);
    }

    [Fact]
    public async Task J7_零下温度是补码_按有符号解()
    {
        var (dev, yd) = Rig();
        MakeJ7(dev);
        dev.Regs[1536] = unchecked((ushort)-2000);   // -200.0 ℃
        await yd.InitAsync(YudianKind.Thermal);
        var r = await yd.ReadAsync();
        Assert.Equal(-200.0, r[0].Value);
    }

    [Fact]
    public async Task 报警状态_高字节奇数通道_低字节偶数通道()
    {
        var (dev, yd) = Rig();
        MakeJ7(dev);
        dev.Regs[1664] = 0x0102;   // CH1 oral（断线），CH2 HA 上限
        dev.Regs[1665] = 0x0400;   // CH3 LA 下限
        await yd.InitAsync(YudianKind.Thermal);
        var r = await yd.ReadAsync();
        Assert.True(r[0].SensorFault);
        Assert.True(r[1].AlarmHigh);
        Assert.True(r[2].AlarmLow);
        Assert.False(r[3].SensorFault || r[3].AlarmHigh || r[3].AlarmLow);
    }

    [Fact]
    public async Task 断线那一路_值照给但挂fault_由上层发Bad()
    {
        var (dev, yd) = Rig();
        MakeJ7(dev);
        dev.Regs[1536] = 8100;     // 断线时寄存器里的残值
        dev.Regs[1664] = 0x0100;
        await yd.InitAsync(YudianKind.Thermal);
        var r = await yd.ReadAsync();
        Assert.True(r[0].SensorFault);
        Assert.Equal(810.0, r[0].Value);
    }

    [Fact]
    public async Task 关闭的通道_不给值也不算故障()
    {
        var (dev, yd) = Rig();
        MakeJ7(dev);
        dev.Regs[386] = 0;         // In03 个位 0 = 关闭（手册要求没用的路要关）
        await yd.InitAsync(YudianKind.Thermal);
        Assert.False(yd.Identity!.Channels[2].Enabled);
        var r = await yd.ReadAsync();
        Assert.Null(r[2].Value);
        Assert.Null(r[2].Problem);
    }

    [Fact]
    public async Task 温度口插了pH表_Init就报接反()
    {
        var (dev, yd) = Rig();
        MakeJ7(dev);
        dev.Regs[2048] = 51;       // 组1 竟是 4~20mA
        var id = await yd.InitAsync(YudianKind.Thermal);
        Assert.Contains("4~20mA（InP=51，J4）", id.Channels[0].Problem);
        Assert.Contains("写成 21", id.Channels[0].Problem);
        Assert.False(id.Channels[0].Usable);
        Assert.Equal("4~20mA（InP=51，J4）", id.Channels[0].TypeName);
        var r = await yd.ReadAsync();
        Assert.Null(r[0].Value);   // 报了问题就不给数
        Assert.Equal(0, r[0].Raw);
    }

    [Fact]
    public async Task 不认识的InP_拒绝换算不猜系数_mV口说清不是热阻()
    {
        var (dev, yd) = Rig();
        MakeJ7(dev);
        dev.Regs[2048] = 99;       // 说明书 §5.2 表里没有
        dev.Regs[2049] = 25;       // 0~75mV：表里有，但不是热偶/热阻
        var id = await yd.InitAsync(YudianKind.Thermal);
        Assert.Contains("不猜", id.Channels[0].Problem);
        Assert.Contains("0~75mV（InP=25）", id.Channels[1].Problem);
        Assert.Contains("不是热偶/热阻", id.Channels[1].Problem);
        var r = await yd.ReadAsync();
        Assert.Null(r[0].Value);
        Assert.Null(r[1].Value);
    }

    [Fact]
    public async Task 标度表照说明书1_3_两位小数的是13_17_18_19_22_23()
    {
        var (dev, yd) = Rig();
        for (var i = 0; i < 4; i++) dev.Regs[384 + i] = (ushort)(i + 1);
        dev.Regs[2048] = 23;   // Pt1000 -200.00~+300.00
        dev.Regs[2049] = 19;   // Ni120 -50~+270.00
        dev.Regs[2050] = 20;   // Cu50 -50~+150
        dev.Regs[2051] = 12;   // F2 450~2000
        dev.Regs[1536] = 2500; dev.Regs[1537] = 2500; dev.Regs[1538] = 250; dev.Regs[1539] = 4500;
        await yd.InitAsync(YudianKind.Thermal);
        var r = await yd.ReadAsync();
        Assert.Equal(25.0, r[0].Value);
        Assert.Equal(25.0, r[1].Value);
        Assert.Equal(25.0, r[2].Value);
        Assert.Equal(450.0, r[3].Value);
    }

    [Fact]
    public async Task J4_线性定标看dPt_量程从ScLScH换算()
    {
        var (dev, yd) = Rig();
        dev.Regs[384] = 1; dev.Regs[385] = 2;          // 两路 pH，其余关
        dev.Regs[2048] = 51; dev.Regs[2049] = 51;
        dev.Regs[2052] = 0; dev.Regs[2056] = 1400;     // 组1 定标 0.00~14.00
        dev.Regs[2053] = 0; dev.Regs[2057] = 1400;
        dev.Regs[2128] = 2;                            // dPt = 2 位小数
        dev.Regs[1536] = 700;                          // 7.00 pH
        var id = await yd.InitAsync(YudianKind.Linear);
        Assert.Equal(0.0, id.Channels[0].RangeLo);
        Assert.Equal(14.0, id.Channels[0].RangeHi);
        var r = await yd.ReadAsync();
        Assert.Equal(7.0, r[0].Value);
    }

    [Fact]
    public async Task pH口插了温度表_Init就报接反()
    {
        var (dev, yd) = Rig();
        dev.Regs[384] = 1;
        dev.Regs[2048] = 21;       // 竟是 Pt100
        var id = await yd.InitAsync(YudianKind.Linear);
        Assert.Contains("Pt100（InP=21）", id.Channels[0].Problem);
        Assert.Contains("不是电流口", id.Channels[0].Problem);
    }

    [Fact]
    public async Task Loc锁着_身份里报出来()
    {
        var (dev, yd) = Rig();
        MakeJ7(dev);
        dev.Regs[2130] = 0b0010_0000;   // Loc.5：0800H 段写不进（应答正常但不生效）
        var id = await yd.InitAsync(YudianKind.Thermal);
        Assert.True(id.WriteLocked);
    }

    [Fact]
    public async Task In的个位是组号_通道可以借别组的配置()
    {
        var (dev, yd) = Rig();
        MakeJ7(dev);
        dev.Regs[385] = 1;             // CH2 借组 1（Pt100 ×10）
        dev.Regs[1537] = 300;
        await yd.InitAsync(YudianKind.Thermal);
        var r = await yd.ReadAsync();
        Assert.Equal(30.0, r[1].Value);
    }

    [Fact]
    public async Task 没Init就读_直接拒绝()
    {
        var (_, yd) = Rig();
        await Assert.ThrowsAsync<InvalidOperationException>(() => yd.ReadAsync());
    }
}
