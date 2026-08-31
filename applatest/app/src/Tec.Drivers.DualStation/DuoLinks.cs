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

    /// <summary>
    /// 按连接参数开真串口。字段名与 DualStationDriver.ConnectionSchema 一一对应。
    ///
    /// 宇电两台既可以各占一条串口，也可以共用一条——手册 §3.3：导轨端子拼接后
    /// 通讯自动并联，两台拼在一起时本来就在同一条 485 总线上。「pH 串口」填成
    /// 与「温度模块串口」相同就是共口：共用一条链路、一把总线锁，此时两台的
    /// Modbus 地址必须不同（出厂都是 1，先用面板改一台）、波特率必须一致。
    /// </summary>
    public static DuoLinks Serial(ParameterSet cn)
    {
        var tempPortName = cn.Str(F.PortTemp, "COM4").Trim();
        var tempBaud = (int)cn.Num(F.BaudTemp, 19200);
        var tempAddr = (byte)cn.Num(F.AddrTemp, 1);
        var busLock = new SemaphoreSlim(1, 1);

        var tempPort = new SerialPortTransport(tempPortName, tempBaud);
        var temp = new YudianClient(new ModbusRtuClient(tempPort, tempAddr, busLock: busLock));

        ISerialTransport? phPort = null; YudianClient? ph = null;
        if (cn.Str(F.HasPh, "有") == "有")
        {
            var phPortName = cn.Str(F.PortPh, "COM5").Trim();
            var phAddr = (byte)cn.Num(F.AddrPh, 1);
            if (string.Equals(phPortName, tempPortName, StringComparison.OrdinalIgnoreCase))
            {
                if (phAddr == tempAddr)
                    throw new InvalidOperationException(
                        $"pH 与温度模块共用串口 {tempPortName} 时，两台的 Modbus 地址不能都是 {tempAddr}——" +
                        "宇电出厂地址都是 1，先用面板把其中一台改掉（手册【注】）");
                if ((int)cn.Num(F.BaudPh, 19200) != tempBaud)
                    throw new InvalidOperationException(
                        $"pH 与温度模块共用串口 {tempPortName} 时波特率必须一致——一条串口只有一个波特率");
                // 共口：复用温度模块的链路与总线锁；phPort 留空，口子归温度那条链路管
                ph = new YudianClient(new ModbusRtuClient(tempPort, phAddr, busLock: busLock));
            }
            else
            {
                phPort = new SerialPortTransport(phPortName, (int)cn.Num(F.BaudPh, 19200));
                ph = new YudianClient(new ModbusRtuClient(phPort, phAddr));
            }
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
