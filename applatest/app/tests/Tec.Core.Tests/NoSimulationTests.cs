using Tec.Core.Benches;
using Tec.Core.Persistence;
using Tec.Driver.Abi;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 程序里不再有仿真之后要守住的几件事：
/// 老台面里的仿真设备读进来怎么办（换真机 / 摘掉，且必须说出来）、
/// 链路状态那一句怎么念（「已连接」只在会话真开着时才说）。
/// </summary>
public class NoSimulationTests
{
    private static BenchDoc OldBench() => new()
    {
        Name = "老台面",
        Devices =
        {
            new DeviceDoc
            {
                DriverId = "tec.reactor.rd105", InstanceId = "R1", Label = "反应器 R1", Simulated = true,
                Connection = ParameterSet.Of(("端口", "COM7"), ("波特率", "115200"))
            },
            new DeviceDoc { DriverId = "tec.probe.tr", InstanceId = "TR1", Simulated = true, DockHostId = "R1" },
            new DeviceDoc { DriverId = "tec.probe.ph", InstanceId = "PH1", Simulated = true },
            new DeviceDoc { DriverId = "tec.dosing.pump", InstanceId = "P1", Label = "加料泵 P1", Simulated = true },
            new DeviceDoc { DriverId = "tec.probe.raman", InstanceId = "RM1", DockHostId = "P1" }
        },
        Bindings =
        {
            new BindingDoc { DeviceId = "TR1", Channel = 1 },
            new BindingDoc { DeviceId = "P1", Channel = 1 },
            new BindingDoc { DeviceId = "P1", Channel = 2 }
        }
    };

    [Fact]
    public void 老台面的仿真设备换成真机孪生_串口沿用()
    {
        var bench = new Bench();
        var notes = OldBench().ApplyTo(bench);

        var r1 = Assert.Single(bench.Devices, d => d.InstanceId == "R1");
        Assert.Equal("tec.reactor.duo", r1.DriverId);
        // 操作人在仿真反应器上选好的口搬到真机的字段里——换驱动不该把他选的口丢了
        Assert.Equal("COM7", r1.Connection.Str("RD105串口"));
        // 波特率不搬：仿真那份默认 115200，RD105 出厂 38400
        Assert.False(r1.Connection.Has("RD105波特率"));

        Assert.Equal("tec.probe.tr.yudian", Assert.Single(bench.Devices, d => d.InstanceId == "TR1").DriverId);
        Assert.Equal("tec.probe.ph.yudian", Assert.Single(bench.Devices, d => d.InstanceId == "PH1").DriverId);

        Assert.Contains(notes, n => n.Contains("R1") && n.Contains("tec.reactor.duo") && n.Contains("COM7"));
    }

    [Fact]
    public void 没有真机形态的仿真设备从台面摘掉_绑定与停靠一并清_并且说出来()
    {
        var bench = new Bench();
        var notes = OldBench().ApplyTo(bench);

        Assert.DoesNotContain(bench.Devices, d => d.InstanceId == "P1");
        Assert.DoesNotContain(bench.Devices, d => d.InstanceId == "RM1");
        Assert.DoesNotContain(bench.Bindings, b => b.DeviceId == "P1");
        // 摘掉的设备身上停着的东西不能留着悬空的宿主引用
        Assert.DoesNotContain(bench.Devices, d => d.DockHostId == "P1");

        Assert.Contains(notes, n => n.Contains("加料泵 P1") && n.Contains("移除"));
        Assert.Contains(notes, n => n.Contains("RM1") && n.Contains("移除"));
    }

    [Fact]
    public void 真机台面读进来什么都不动_也就什么都不说()
    {
        var bench = new Bench();
        var notes = new BenchDoc
        {
            Devices = { new DeviceDoc { DriverId = "tec.reactor.duo", InstanceId = "R1" } }
        }.ApplyTo(bench);

        Assert.Empty(notes);
        Assert.Equal("tec.reactor.duo", bench.Devices[0].DriverId);
    }

    [Fact]
    public void 旧文件里的模拟位读进来忽略_写出去不再带()
    {
        var bench = new Bench();
        OldBench().ApplyTo(bench);
        var json = TecJson.Write(bench.ToDoc());
        Assert.DoesNotContain("Simulated", json, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class StubSession(DeviceState state) : IDeviceSession
    {
        public string InstanceId => "R1";
        public DeviceState State { get; } = state;
#pragma warning disable CS0067
        public event EventHandler<DeviceState>? StateChanged;
#pragma warning restore CS0067
        public IReadOnlyList<TagDescriptor> Tags => Array.Empty<TagDescriptor>();
        public IObservable<Sample> Samples { get; } = new Broadcast<Sample>();
        public int WellCount => 2;
        public IReadOnlyList<ICapability> CapabilitiesOf(int well) => Array.Empty<ICapability>();
        public ICommandHandler? Resolve(string commandId) => null;
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public void 链路那句话_只有会话真开着才叫已连接()
    {
        Assert.Equal(("未连接", false), DeviceLink.Describe(null, null));
        Assert.Equal(("未连接：串口 COM3 打不开", false), DeviceLink.Describe(null, "串口 COM3 打不开"));
        Assert.Equal(("已连接", true), DeviceLink.Describe(new StubSession(DeviceState.Ready), null));
        Assert.Equal(("已连接", true), DeviceLink.Describe(new StubSession(DeviceState.Connected), null));
        // 设备自己报故障：链路通着但数据不可信，跟没连上不是一回事，也不能叫「已连接」了事
        var (faulted, ok) = DeviceLink.Describe(new StubSession(DeviceState.Faulted), null);
        Assert.False(ok);
        Assert.Contains("故障", faulted);
        Assert.Equal(("已断开", false), DeviceLink.Describe(new StubSession(DeviceState.Disposed), null));
    }
}
