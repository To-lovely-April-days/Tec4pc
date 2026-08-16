using Tec.Driver.Abi;
using Tec.Drivers.Ttd7008;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 真机驱动的回归。全程走假串口——不插硬件也要能验证协议这一层，
/// 否则每改一行都得抱着机器跑一趟。
/// </summary>
public sealed class Ttd7008DriverTests
{
    private static (Ttd7008Driver Driver, FakeTecDevice Device) Rig()
    {
        var device = new FakeTecDevice();
        var driver = new Ttd7008Driver { LinkFactory = _ => new Ttd7008Link(device) };
        return (driver, device);
    }

    private static ParameterSet Conn() =>
        ParameterSet.Of((Ttd7008Driver.FieldPort, "COM9"), (Ttd7008Driver.FieldBaud, 38400d));

    [Fact]
    public async Task 测试连接读得到型号与固件()
    {
        var (driver, device) = Rig();
        device.Model = "215L";

        var probe = await driver.ProbeAsync(Conn(), CancellationToken.None);

        Assert.True(probe.Success);
        Assert.Contains("215L", probe.Message);
        Assert.Equal("v1.3.0", probe.Firmware);      // FPV=130 → v1.3.0
        Assert.Equal(2, probe.DetectedChannels);
    }

    [Fact]
    public async Task 对面不应答时测试连接失败而不是抛异常()
    {
        var (driver, device) = Rig();
        device.Mute = true;

        var probe = await driver.ProbeAsync(Conn(), CancellationToken.None);

        // 界面上「测试连接」按下去必须给一句话，不能把异常抛到 UI 线程上
        Assert.False(probe.Success);
        Assert.False(string.IsNullOrWhiteSpace(probe.Message));
    }

    [Fact]
    public async Task 串口打不开时把原话带出来()
    {
        var driver = new Ttd7008Driver
        {
            LinkFactory = _ => throw new IOException("COM9 已被占用")
        };

        var probe = await driver.ProbeAsync(Conn(), CancellationToken.None);

        Assert.False(probe.Success);
        Assert.Contains("COM9 已被占用", probe.Message);
    }

    [Fact]
    public async Task 测试连接之后把串口关掉不占着口子()
    {
        var (driver, device) = Rig();

        await driver.ProbeAsync(Conn(), CancellationToken.None);

        // 探测完不释放的话，紧接着真正打开设备就会「端口被占用」
        Assert.False(device.IsOpen);
    }

    [Fact]
    public void 真机驱动与仿真机认同一套温度指令()
    {
        var (driver, _) = Rig();
        var real = driver.Commands.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);

        // 拿仿真调好的配方插上真机必须能跑——两边的指令 Id 必须重合，
        // 各写各的「升温至」就等于两套工艺语言
        foreach (var d in CommandSpecs.Temperature)
            Assert.Contains(d.Id, real);
    }

    [Fact]
    public void 连接参数与配置参数都有缺省值()
    {
        var (driver, _) = Rig();

        var cn = new ParameterSet().FillDefaults(driver.ConnectionSchema);
        Assert.Equal("COM3", cn.Str(Ttd7008Driver.FieldPort));
        Assert.Equal(500, cn.Num(Ttd7008Driver.FieldPeriod));

        // 超温与限流是设备侧的硬保护，缺省值必须填齐，不能让操作人从空白开始猜
        var cfg = new ParameterSet().FillDefaults(driver.ConfigSchema);
        Assert.Equal(180, cfg.Num(Ttd7008Driver.FieldOverUp));
        Assert.Equal(-40, cfg.Num(Ttd7008Driver.FieldOverLow));
        Assert.True(cfg.Num(Ttd7008Driver.FieldMaxCurrent) > 0);
    }

    [Fact]
    public async Task 假设备能完整走一遍读写()
    {
        // 这条是给假设备本身把关的：它要是答得不对，上面几条测试就没有意义
        var device = new FakeTecDevice();
        using var link = new Ttd7008Link(device);
        link.Open();

        await link.Controller.SetTargetAsync(1, 55.5);
        await link.Controller.SetEnableAsync(1, true);
        var cfgTarget = await link.Controller.ReadChannelConfigAsync(1);
        Assert.Equal(55.5, cfgTarget.TargetC, 3);
        Assert.True(cfgTarget.Enabled);

        device.Set(1, "TCADJTEMP", 40_00000);
        device.Set(2, "TCADJTEMP", 12_50000);
        var snap = await link.Controller.ReadSnapshotAsync();
        Assert.Equal(40.0, snap.Temp1C, 3);
        Assert.Equal(12.5, snap.Temp2C, 3);
    }
}
