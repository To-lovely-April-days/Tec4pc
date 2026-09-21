using Tec.Driver.Abi;
using Tec.Drivers.DualStation;
using Tec.Drivers.Rd105;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 串口开不了 / 半路掉了时的话（SerialFault）。起因：现场宇电模块断电重上电，USB 转串跟着掉了一下，
/// 程序里口子还开着，再点「连接」属性栏只有一句「函数不正确。: 'COM8'」——谁看了都不知道该拔哪根线。
/// </summary>
public sealed class SerialFaultTests
{
    private static IOException InvalidFunction() => new("函数不正确。: 'COM8'") { HResult = SerialFault.HrInvalidFunction };

    [Fact]
    public void 函数不正确_说是拔插过USB转串_该重新拔插()
    {
        var s = SerialFault.Explain(InvalidFunction(), "COM8");
        Assert.StartsWith("串口 COM8 打不开：驱动报「函数不正确」", s);
        Assert.Contains("USB 转串", s);
        Assert.Contains("重新拔插", s);
        Assert.Contains("再点「连接」", s);

        // 只认文案不认 HResult 的那种（别的运行时 / 英文系统）也认
        Assert.True(SerialFault.IsInvalidFunction(new IOException("Incorrect function.")));
        Assert.False(SerialFault.IsInvalidFunction(new IOException("The semaphore timeout period has expired.")));
    }

    [Fact]
    public void 被占着_口没了_别的IO错_各说各的()
    {
        Assert.Contains("被别的程序占着", SerialFault.Explain(new UnauthorizedAccessException("Access to the port 'COM8' is denied."), "COM8"));
        Assert.Contains("现在没有这个口", SerialFault.Explain(new IOException("The port 'COM9' does not exist."), "COM9"));
        var other = SerialFault.Explain(new IOException("The I/O operation has been aborted"), "COM8");
        Assert.StartsWith("串口 COM8 出错：", other);
        Assert.Contains("点「连接」重开", other);
        // 不是串口层的异常：只带口名和原话，不编原因
        Assert.Equal("串口 COM8 打不开：随便什么", SerialFault.Explain(new InvalidOperationException("随便什么"), "COM8"));
    }

    /// <summary>Open 就抛「函数不正确」的假串口——模拟拔插过 USB 转串之后的样子。</summary>
    private sealed class DeadPort : TecControl.Core.Comm.ISerialTransport
    {
        public bool IsOpen => false;
        public void Open() => throw InvalidFunction();
        public void Close() { }
        public void DiscardInput() { }
        public void Dispose() { }
        public void Write(byte[] buffer, int offset, int count) => throw InvalidFunction();
        public int Read(byte[] buffer, int offset, int count, int timeoutMs) => throw InvalidFunction();
    }

    [Fact]
    public async Task 宇电探头_测试连接和开机都把这句翻成人话()
    {
        var drv = new YudianTrProbeDriver
        {
            SerialFactory = _ => new SharedSerial(new DeadPort(), new SemaphoreSlim(1, 1), portName: "COM8")
        };
        var cn = ParameterSet.Of((YudianProbeDriverBase.FieldPort, "COM8"));

        var r = await drv.ProbeAsync(cn, CancellationToken.None);
        Assert.False(r.Success);
        Assert.StartsWith("串口 COM8 打不开：驱动报「函数不正确」", r.Message);

        var ctx = new DriverContext
        {
            InstanceId = "TR1", ChannelNumbers = new[] { 1 }, Config = ParameterSet.Of((YudianProbeDriverBase.FieldModuleCh, "2")),
            Simulated = false, TimeScale = 1, Clock = () => DateTimeOffset.Now, Log = (_, _) => { }
        };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => drv.OpenAsync(cn, ctx, CancellationToken.None));
        Assert.StartsWith("串口 COM8 打不开：驱动报「函数不正确」", ex.Message);
        Assert.IsType<IOException>(ex.InnerException);
    }

    [Fact]
    public void 主机两条链路_RD105打不开带口名和该怎么办_IO8R打不开不拦()
    {
        var links = new DuoLinks
        {
            Rd105 = new Rd105Link(new DeadPort()), RdPortName = "COM7", RdBaud = 38400, RdProtocol = Rd105Protocol.Modbus,
            IoPort = new DeadPort(), IoPortName = "COM10"
        };
        var ex = Assert.Throws<InvalidOperationException>(links.OpenAll);
        Assert.StartsWith("RD105 串口 COM7 @ 38400（Modbus-RTU 站号 1） 打不开：串口 COM7 打不开：驱动报「函数不正确」", ex.Message);

        var okRd = new DuoLinks
        {
            Rd105 = new Rd105Link(new FakeRd105Device()), RdPortName = "COM7",
            IoPort = new DeadPort(), IoPortName = "COM10"
        };
        okRd.OpenAll();
        Assert.StartsWith("IO8R 串口 COM10 打不开：串口 COM10 打不开：驱动报「函数不正确」", okRd.IoOpenError);
    }
}
