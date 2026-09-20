using TecControl.Core.Comm;
using Tec.Driver.Abi;
using Tec.Drivers.Rd105.Modbus;
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

    /// <summary>只为报错时说得清是哪个口——假链路（测试）不填，报错就不带口名。</summary>
    public string? RdPortName { get; init; }
    public int RdBaud { get; init; }
    /// <summary>ASCII（TTL 口）还是 Modbus-RTU（RS485 口）；null 按 ASCII 说。</summary>
    public string? RdProtocol { get; init; }
    public int RdStation { get; init; } = 1;
    public string? IoPortName { get; init; }

    /// <summary>IO8R 那条串口没打开的原因；打开了 / 没配 IO8R 就是 null。</summary>
    public string? IoOpenError { get; private set; }

    /// <summary>RD105 那条串口在报错里的叫法：「RD105 串口 COM7 @ 38400（ASCII）」。</summary>
    public string RdName => RdPortName is null
        ? $"RD105 串口（{Rd105Protocol.Short(RdProtocol, RdStation)}）"
        : $"RD105 串口 {RdPortName} @ {RdBaud}（{Rd105Protocol.Short(RdProtocol, RdStation)}）";

    /// <summary>
    /// 开口子。RD105 打不开就是开不了机（它是主机的命）；IO8R 打不开**不拦**——
    /// 记下原因，会话照常开、电加热不可用（需求 §2.6 本来就允许 IO8R 坏着开机）。
    /// 从前 IO8R 口名填错会把 RD105 一起拖死，属性栏只说「打不开链路」，看不出是哪条口。
    /// </summary>
    public void OpenAll()
    {
        try { Rd105.Open(); }
        catch (Exception ex) { throw new InvalidOperationException($"{RdName} 打不开：{ex.Message}", ex); }

        IoOpenError = null;
        if (IoPort is { IsOpen: false })
        {
            try { IoPort.Open(); }
            catch (Exception ex)
            {
                IoOpenError = $"IO8R 串口{(IoPortName is null ? "" : " " + IoPortName)} 打不开：{ex.Message}";
            }
        }
    }

    public void Dispose()
    {
        Rd105.Dispose();
        IoPort?.Dispose();
    }

    /// <summary>
    /// RD105 发了指令没回音时该怎么说。原始异常只有一句「指令无应答：TEC=?@」，
    /// 站在机器前的人不知道该查什么——把口、波特率和三个最常见的原因一起摆出来。
    /// 协议 §1：TTL 口出厂 38400，RS485 口出厂 9600。
    /// </summary>
    public string NoReply(Exception ex)
        => $"{RdName} 无应答（{ex.Message.TrimEnd('\n', '\r')}）——查：①是不是接 RD105 的那一路串口；" +
           "②波特率：TTL 口出厂 38400、RS485 口出厂 9600；③接线（TTL 的 TX/RX 要交叉、485 的 A/B、共地）";

    /// <summary>按连接参数开真串口。字段名与 DualStationDriver.ConnectionSchema 一一对应。</summary>
    public static DuoLinks Serial(ParameterSet cn)
    {
        ISerialTransport? ioPort = null; Io8rClient? io = null;
        var ioPortName = cn.Str(F.PortIo, "COM6");
        if (cn.Str(F.HasIo, "有") == "有")
        {
            ioPort = new SerialPortTransport(ioPortName, (int)cn.Num(F.BaudIo, 9600));
            io = new Io8rClient(new ModbusRtuClient(ioPort, (byte)cn.Num(F.AddrIo, 1)));
        }

        var rdPort = cn.Str(F.PortRd105, "COM3");
        var rdBaud = (int)cn.Num(F.BaudRd105, 38400);
        var rdProto = cn.Str(F.ProtoRd105, Rd105Protocol.Ascii);
        var rdStation = cn.Int(F.AddrRd105, 1);
        return new DuoLinks
        {
            // 「RD105 协议」选 Modbus-RTU 时串口上套一层桥（Rd105ModbusBridge），链路其余部分不变
            Rd105 = new Rd105Link(Rd105Protocol.Transport(rdPort, rdBaud, rdProto, rdStation)),
            RdPortName = rdPort, RdBaud = rdBaud, RdProtocol = rdProto, RdStation = rdStation,
            IoPort = ioPort, Io = io, IoPortName = io is null ? null : ioPortName
        };
    }
}
