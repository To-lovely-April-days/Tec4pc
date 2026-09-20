using TecControl.Core.Comm;

namespace Tec.Drivers.Rd105;

/// <summary>
/// RD105 两种通讯格式（协议 §2）：ASCII（TTL 口，出厂 38400）和 Modbus-RTU
/// （RS485 口，出厂 9600、站号 1）。连接表单里让人选，选错了「连接」会换着试并说出来。
/// </summary>
public static class Rd105Protocol
{
    public const string Ascii = "ASCII（TTL 口）";
    public const string Modbus = "Modbus-RTU（RS485 口）";
    public static readonly string[] Options = { Ascii, Modbus };

    public static bool IsModbus(string? protocol)
        => protocol is not null && protocol.StartsWith("Modbus", StringComparison.OrdinalIgnoreCase);

    /// <summary>短名，报错和日志里用：「ASCII」/「Modbus-RTU 站号 1」。</summary>
    public static string Short(string? protocol, int station)
        => IsModbus(protocol) ? $"Modbus-RTU 站号 {station}" : "ASCII";

    /// <summary>
    /// 按协议开一条到 RD105 的传输：ASCII 直接是串口；Modbus 在串口上套一层桥，
    /// 上面的 TecClient / TecController / 控制环一行不用改。
    /// </summary>
    public static ISerialTransport Transport(string portName, int baud, string? protocol, int station)
    {
        var serial = new SerialPortTransport(portName, baud);
        return IsModbus(protocol) ? new Rd105ModbusBridge(serial, (byte)Math.Clamp(station, 1, 247)) : serial;
    }

    /// <summary>
    /// 配置的组合没应答时换着试的顺序：先换协议（同一个口两种格式都可能），再换到
    /// 另一档出厂波特率（38400 ⇄ 9600）。只有这两档：协议 §1 出厂只有这两个值，
    /// 别的档是现场自己改过的，那就该现场自己知道。
    /// </summary>
    public static IEnumerable<(string Protocol, int Baud)> Alternatives(string? protocol, int baud)
    {
        var other = IsModbus(protocol) ? Ascii : Modbus;
        var mine = IsModbus(protocol) ? Modbus : Ascii;
        var otherBaud = baud == 38400 ? 9600 : 38400;
        yield return (other, baud);
        yield return (mine, otherBaud);
        yield return (other, otherBaud);
    }
}
