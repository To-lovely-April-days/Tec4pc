namespace Tec.Drivers.DualStation.Modbus;

/// <summary>
/// CRC-16/MODBUS：多项式 0xA001（0x8005 反射），初值 0xFFFF，帧尾低字节在前。
/// 标准校验向量 "123456789" → 0x4B37，测试里就锁的这个值——
/// 别信任何一份手册抄来的示例帧，先过这条再谈别的。
/// </summary>
public static class ModbusCrc
{
    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? crc >> 1 ^ 0xA001 : crc >> 1;
        }
        return (ushort)crc;
    }

    /// <summary>算 frame[..^2] 的 CRC，填进最后两个字节（低前高后）。</summary>
    public static void Append(byte[] frame)
    {
        var crc = Compute(frame.AsSpan(0, frame.Length - 2));
        frame[^2] = (byte)crc;
        frame[^1] = (byte)(crc >> 8);
    }

    /// <summary>frame 最后两个字节是不是前面内容的 CRC。</summary>
    public static bool Check(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 4) return false;
        var crc = Compute(frame[..^2]);
        return frame[^2] == (byte)crc && frame[^1] == (byte)(crc >> 8);
    }
}
