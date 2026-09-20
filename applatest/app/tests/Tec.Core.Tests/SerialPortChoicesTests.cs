using Tec.Driver.Abi;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 串口下拉是怎么摆出来的。
///
/// 起因：属性栏里的「串口」原来写死 COM1~COM6，而现场那台机器插着八口 USB 转串，
/// 口号是 COM3 / 7 / 8 / 10 / 15~18——一个都选不着。现在照实去扫，
/// 这里盯的是「扫回来之后怎么摆」：排序、设备名、以及存好的口不在线时不能被吞掉。
/// </summary>
public class SerialPortChoicesTests
{
    private static SerialPortInfo P(string name, string? friendly = null) => new(name, friendly);

    [Fact]
    public void 按端口号的数字排_不是按字符串排()
    {
        // 现场那台机器的真实口号。照字符串排的话 COM10 会跑到 COM3 前面
        var opts = SerialPortChoices.Build(
            new[] { P("COM8"), P("COM16"), P("COM3"), P("COM10"), P("COM7") }, null);

        Assert.Equal(new[] { "COM3", "COM7", "COM8", "COM10", "COM16" },
                     opts.Select(o => o.Value));
    }

    [Fact]
    public void 有设备名就连名字一起显示_存的仍然只是端口号()
    {
        var opts = SerialPortChoices.Build(
            new[] { P("COM16", "USB-Enhanced-SERIAL-A CH344"), P("COM3") }, null);

        // 八个 CH344 口光看 COM 号分不出哪一路接的是温控器，名字得摆出来
        Assert.Equal("COM3", opts[0].Value);
        Assert.Equal("COM3", opts[0].Text);                 // 没名字就只显示口号，不编一个
        Assert.Equal("COM16", opts[1].Value);               // 存进 .tecbench 的是这个
        Assert.Equal("COM16 · USB-Enhanced-SERIAL-A CH344", opts[1].Text);
    }

    [Fact]
    public void 存好的口不在线_留在下拉里并标出来()
    {
        // 台面是在办公室配的 / 线拔了 / 换了台机器：存着 COM16，现在只扫到 COM3
        var opts = SerialPortChoices.Build(new[] { P("COM3") }, "COM16");

        Assert.Equal(2, opts.Count);
        Assert.Equal("COM16", opts[0].Value);               // 不能被空下拉吞掉
        Assert.Equal("COM16 · 当前不在线", opts[0].Text);
        Assert.Equal("COM3", opts[1].Value);
    }

    [Fact]
    public void 存好的口就在线_不重复也不标不在线()
    {
        var opts = SerialPortChoices.Build(
            new[] { P("COM3"), P("COM16", "CH344") }, "COM16");

        Assert.Equal(new[] { "COM3", "COM16" }, opts.Select(o => o.Value));
        Assert.DoesNotContain(opts, o => o.Text.Contains("不在线"));
    }

    [Fact]
    public void 一个口都没扫到_就是空表_不摆一份假的()
    {
        Assert.Empty(SerialPortChoices.Build(Array.Empty<SerialPortInfo>(), null));

        // 但存着的那个仍然留着——否则一打开属性栏配好的口就没了
        var opts = SerialPortChoices.Build(Array.Empty<SerialPortInfo>(), "COM16");
        Assert.Single(opts);
        Assert.Equal("COM16", opts[0].Value);
    }

    [Fact]
    public void Linux的口名也排得动_同一族排一起族内按数字()
    {
        var opts = SerialPortChoices.Build(
            new[] { P("/dev/ttyUSB10"), P("/dev/ttyUSB2"), P("/dev/ttyS0"), P("COM3") }, null);

        // ttyUSB2 在 ttyUSB10 前面（按数字，不是按字符串）；COM 与 /dev 各归各的
        Assert.Equal(new[] { "/dev/ttyS0", "/dev/ttyUSB2", "/dev/ttyUSB10", "COM3" },
                     opts.Select(o => o.Value));
    }

    [Fact]
    public void 同一个口报两遍只留一个()
    {
        var opts = SerialPortChoices.Build(new[] { P("COM3"), P("com3", "重复") }, null);
        Assert.Single(opts);
    }

    [Fact]
    public void 串口字段的声明里不带写死的选项()
    {
        // 声明里那份静态 Choices 必须是空的：机器上有哪些口只有运行现场知道。
        // 这一条防的是「又有人图省事塞一份 COM1~COM6 进去」
        foreach (var schema in new[]
                 {
                     new Tec.Drivers.Simulator.Rd105ReactorDriver().ConnectionSchema,
                     new Tec.Drivers.Simulator.DosingPumpDriver().ConnectionSchema,
                     new Tec.Drivers.DualStation.DualStationDriver().ConnectionSchema,
                     new Tec.Drivers.DualStation.YudianTrProbeDriver().ConnectionSchema,
                     new Tec.Drivers.Rd105.Rd105TecDriver().ConnectionSchema
                 })
            foreach (var f in schema.Fields.Where(f => f.Label.Contains("串口")))
            {
                Assert.Equal(WellKnownChoices.SerialPorts, f.ChoicesFrom);
                Assert.Empty(f.Choices ?? Array.Empty<string>());
            }
    }
}
