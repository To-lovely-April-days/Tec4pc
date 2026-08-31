using TecControl.Core.Comm;
using Tec.Driver.Abi;
using Tec.Drivers.DualStation.Modbus;
using Tec.Drivers.Rd105;
using F = Tec.Drivers.DualStation.DualStationDriver.Fields;

namespace Tec.Drivers.DualStation;

/// <summary>
/// 双工位反应主机自己的两条链路：RD105（两路夹套控温）与 IO8R（热源切换，
/// 可没有）。这两样长在机器里，串口写在主机属性里；**釜内 Tr 与 pH 不在这里**——
/// 它们是独立的探头设备（宇电模块），串口在各自探头的属性里（用户定的：
/// 每台设备的串口跟着设备自己走），读数由探头会话发、主机经 IExternalReactorTemp
/// 接住釜温。测试时两条链路都换成假设备。
/// </summary>
public sealed class DuoLinks : IDisposable
{
    public required Rd105Link Rd105 { get; init; }
    public ISerialTransport? IoPort { get; init; }
    public Io8rClient? Io { get; init; }

    public void OpenAll()
    {
        Rd105.Open();
        if (IoPort is { IsOpen: false }) IoPort.Open();
    }

    public void Dispose()
    {
        Rd105.Dispose();
        IoPort?.Dispose();
    }

    /// <summary>按连接参数开真串口。字段名与 DualStationDriver.ConnectionSchema 一一对应。</summary>
    public static DuoLinks Serial(ParameterSet cn)
    {
        ISerialTransport? ioPort = null; Io8rClient? io = null;
        if (cn.Str(F.HasIo, "有") == "有")
        {
            ioPort = new SerialPortTransport(cn.Str(F.PortIo, "COM6"), (int)cn.Num(F.BaudIo, 9600));
            io = new Io8rClient(new ModbusRtuClient(ioPort, (byte)cn.Num(F.AddrIo, 1)));
        }

        return new DuoLinks
        {
            Rd105 = new Rd105Link(new SerialPortTransport(
                cn.Str(F.PortRd105, "COM3"), (int)cn.Num(F.BaudRd105, 38400))),
            IoPort = ioPort, Io = io
        };
    }
}
