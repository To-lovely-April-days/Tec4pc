namespace Tec.Drivers.DualStation.Modbus;

/// <summary>
/// 从站答了、但答得不对：回了异常码，或者站号/功能码/长度/CRC 对不上。
/// 「根本没答」不走这里——那是 TimeoutException，两种失败现场处置不一样
/// （答错多半是配置问题，不答多半是线和电的问题），报错就分开报。
/// </summary>
public sealed class ModbusException : Exception
{
    /// <summary>从站回的 Modbus 异常码；不是异常应答（本地校验不过）就是 null。</summary>
    public byte? Code { get; }

    public ModbusException(string message) : base(message) { }

    public ModbusException(byte code) : base($"从站回了异常码 {code:X2}：{Describe(code)}")
        => Code = code;

    public static string Describe(byte code) => code switch
    {
        0x01 => "非法功能码（从站不支持这条指令）",
        0x02 => "非法数据地址（点位或寄存器地址越界）",
        0x03 => "非法数据值",
        0x04 => "从站设备故障",
        0x06 => "从站忙，稍后再试",
        _ => "未定义的异常码"
    };
}
