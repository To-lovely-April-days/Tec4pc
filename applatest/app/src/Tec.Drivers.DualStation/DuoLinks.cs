using TecControl.Core.Comm;
using Tec.Driver.Abi;
using Tec.Drivers.DualStation.Modbus;
using Tec.Drivers.DualStation.Yudian;
using Tec.Drivers.Rd105;
using F = Tec.Drivers.DualStation.DualStationDriver.Fields;

namespace Tec.Drivers.DualStation;

/// <summary>
/// 双工位反应主机的四条链路捆成一束：RD105（两路夹套控温）、宇电 J7（釜内 Tr）、
/// 宇电 J4（pH，可没有）、IO8R（热源切换，可没有）。每个模块独立一条串口（需求 §1），
/// 谁堵谁的，采集节奏互不拖累。测试时四条全换成假设备，整机逻辑不插硬件就能回归。
/// </summary>
public sealed class DuoLinks : IDisposable
{
    public required Rd105Link Rd105 { get; init; }
    public required ISerialTransport TempPort { get; init; }
    public required YudianClient TempMod { get; init; }
    public ISerialTransport? PhPort { get; init; }
    public YudianClient? PhMod { get; init; }
    public ISerialTransport? IoPort { get; init; }
    public Io8rClient? Io { get; init; }

    public void OpenAll()
    {
        Rd105.Open();
        if (!TempPort.IsOpen) TempPort.Open();
        if (PhPort is { IsOpen: false }) PhPort.Open();
        if (IoPort is { IsOpen: false }) IoPort.Open();
    }

    public void Dispose()
    {
        Rd105.Dispose();
        TempPort.Dispose();
        PhPort?.Dispose();
        IoPort?.Dispose();
    }

    /// <summary>按连接参数开四条真串口。字段名与 DualStationDriver.ConnectionSchema 一一对应。</summary>
    public static DuoLinks Serial(ParameterSet cn)
    {
        var tempPort = new SerialPortTransport(cn.Str(F.PortTemp, "COM4"), (int)cn.Num(F.BaudTemp, 19200));
        var temp = new YudianClient(new ModbusRtuClient(tempPort, (byte)cn.Num(F.AddrTemp, 1)));

        ISerialTransport? phPort = null; YudianClient? ph = null;
        if (cn.Str(F.HasPh, "有") == "有")
        {
            phPort = new SerialPortTransport(cn.Str(F.PortPh, "COM5"), (int)cn.Num(F.BaudPh, 19200));
            ph = new YudianClient(new ModbusRtuClient(phPort, (byte)cn.Num(F.AddrPh, 1)));
        }

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
            TempPort = tempPort, TempMod = temp,
            PhPort = phPort, PhMod = ph,
            IoPort = ioPort, Io = io
        };
    }
}
