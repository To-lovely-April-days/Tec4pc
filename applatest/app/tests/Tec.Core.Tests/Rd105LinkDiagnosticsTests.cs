using Tec.Driver.Abi;
using Tec.Drivers.DualStation;
using Tec.Drivers.DualStation.Modbus;
using Tec.Drivers.Rd105;
using TecControl.Core.Comm;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 现场第一次接真机撞上的那两件事：RD105 发了指令没回音时，报错得说清是哪个口、
/// 什么波特率、该查什么（原始异常只有一句「指令无应答：TEC=?@」）；IO8R 那条口
/// 填错了不能把 RD105 一起拖死——需求 §2.6 本来就允许 IO8R 坏着开机。
/// </summary>
public sealed class Rd105LinkDiagnosticsTests
{
    /// <summary>开得了口、永远不回话的串口：接错口 / 波特率不对时看到的就是这样。</summary>
    private sealed class SilentPort : ISerialTransport
    {
        public bool IsOpen { get; private set; }
        public void Open() => IsOpen = true;
        public void Close() => IsOpen = false;
        public void DiscardInput() { }
        public void Write(byte[] buffer, int offset, int count) { }
        public int Read(byte[] buffer, int offset, int count, int timeoutMs) { Thread.Sleep(Math.Min(timeoutMs, 20)); return 0; }
        public void Dispose() { }
    }

    /// <summary>开不了的串口（口名不存在 / 被别的程序占着）。</summary>
    private sealed class DeadPort : ISerialTransport
    {
        public bool IsOpen => false;
        public void Open() => throw new IOException("The port 'COM6' does not exist.");
        public void Close() { }
        public void DiscardInput() { }
        public void Write(byte[] buffer, int offset, int count) => throw new InvalidOperationException("port closed");
        public int Read(byte[] buffer, int offset, int count, int timeoutMs) => throw new InvalidOperationException("port closed");
        public void Dispose() { }
    }

    private static DriverContext Ctx(List<(string, string)> logs) => new()
    {
        InstanceId = "DUO1",
        ChannelNumbers = new[] { 1, 2 },
        Config = new ParameterSet(),
        Simulated = false,
        TimeScale = 1,
        Clock = () => DateTimeOffset.Now,
        Log = (lvl, text) => { lock (logs) logs.Add((lvl, text)); }
    };

    [Fact]
    public async Task RD105不回话_探测失败要说清口_波特率和该查什么()
    {
        var drv = new DualStationDriver
        {
            LinksFactory = _ => new DuoLinks
            {
                Rd105 = new Rd105Link(new SilentPort()),
                RdPortName = "COM7", RdBaud = 38400
            }
        };
        var cn = ParameterSet.Of((DualStationDriver.Fields.HasIo, "无"));

        var r = await drv.ProbeAsync(cn, CancellationToken.None);

        Assert.False(r.Success);
        Assert.Contains("COM7 @ 38400", r.Message);
        Assert.Contains("无应答", r.Message);
        // 三个最常见的原因得在话里：口、波特率（TTL 38400 / 485 9600）、接线
        Assert.Contains("38400", r.Message);
        Assert.Contains("9600", r.Message);
        Assert.Contains("接线", r.Message);
        Assert.DoesNotContain("\n", r.Message);      // 原始异常里那个 "\n" 不能带出来
    }

    [Fact]
    public async Task RD105不回话_开机失败的原因也是这一句_不是裸的指令无应答()
    {
        var drv = new DualStationDriver
        {
            LinksFactory = _ => new DuoLinks
            {
                Rd105 = new Rd105Link(new SilentPort()),
                RdPortName = "COM7", RdBaud = 38400
            }
        };
        var logs = new List<(string, string)>();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => drv.OpenAsync(ParameterSet.Of((DualStationDriver.Fields.HasIo, "无")), Ctx(logs), CancellationToken.None));

        Assert.Contains("COM7 @ 38400", ex.Message);
        Assert.Contains("RS485 口出厂 9600", ex.Message);
        Assert.IsType<TimeoutException>(ex.InnerException);
    }

    [Fact]
    public async Task IO8R串口打不开_RD105照常开机_电加热不可用_原因记在日志里()
    {
        var rd = new FakeRd105Device();
        var drv = new DualStationDriver
        {
            LinksFactory = _ => new DuoLinks
            {
                Rd105 = new Rd105Link(rd),
                IoPort = new DeadPort(),
                Io = new Io8rClient(new ModbusRtuClient(new DeadPort(), 1, 200)),
                IoPortName = "COM6"
            }
        };
        var logs = new List<(string, string)>();

        await using var s = await drv.OpenAsync(new ParameterSet(), Ctx(logs), CancellationToken.None);

        // 主机开起来了（两路控温能力都在），只是电加热这条路不可用
        var heat = s.CapabilitiesOf(0).OfType<IHeatSource>().Single();
        Assert.False(heat.ElectricAvailable);
        Assert.Contains(logs, l => l.Item1 == "warn"
                                   && l.Item2.Contains("IO8R 串口 COM6 打不开")
                                   && l.Item2.Contains("does not exist")
                                   && l.Item2.Contains("电加热不可用"));
    }

    [Fact]
    public async Task IO8R串口打不开_探测也照实分开说_RD105通了_IO8R没通()
    {
        var drv = new DualStationDriver
        {
            LinksFactory = _ => new DuoLinks
            {
                Rd105 = new Rd105Link(new FakeRd105Device()),
                IoPort = new DeadPort(),
                Io = new Io8rClient(new ModbusRtuClient(new DeadPort(), 1, 200)),
                IoPortName = "COM6"
            }
        };

        var r = await drv.ProbeAsync(new ParameterSet(), CancellationToken.None);

        Assert.True(r.Success);                          // RD105 通了就算通
        Assert.Contains("RD105：", r.Message);
        Assert.Contains("IO8R 串口 COM6 打不开", r.Message);
        Assert.Contains("电加热不可用", r.Message);
    }
}
