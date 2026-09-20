namespace Tec.Drivers.Rd105;

/// <summary>寄存器里的数长什么样（协议 §4 指令预览表的「数据类型」列）。</summary>
public enum RegType { U16, I16, U32, I32, I64, U64 }

/// <summary>一条 ASCII 指令对应的 Modbus 寄存器：地址、类型、是不是按通道分的（通道二 = 地址 + 0x1000）。</summary>
public sealed record RegSpec(ushort Address, RegType Type, bool PerChannel)
{
    public int Count => Type switch
    {
        RegType.U16 or RegType.I16 => 1,
        RegType.U32 or RegType.I32 => 2,
        _ => 4
    };
}

/// <summary>
/// RD105 的 ASCII 指令名 ⇄ Modbus 寄存器对照（协议 v1.3.0 §3 / §4）。
/// 只抄了驱动真会用到的那些，加上几条看得见的基础项；没抄的指令走 Modbus 桥时
/// 会被如实拒绝（「寄存器表里没有」），不会猜一个地址发出去。
///
/// 字序：协议示例 2500000 发成 00 26 25 A0——高字在前，多字寄存器都按这个来。
/// </summary>
public static class Rd105Registers
{
    /// <summary>通道二的寄存器 = 通道一地址 + 这个偏移（协议 §2.2）。</summary>
    public const ushort ChannelStride = 0x1000;

    public static readonly IReadOnlyDictionary<string, RegSpec> Map =
        new Dictionary<string, RegSpec>(StringComparer.OrdinalIgnoreCase)
        {
            // ── 通道控制指令（TCn:）──
            ["TG"] = new(0x1000, RegType.I32, true),
            ["TCADJTEMP"] = new(0x1002, RegType.I32, true),
            ["RESISTOR"] = new(0x1004, RegType.U64, true),
            ["ENABLE"] = new(0x1100, RegType.U16, true),
            ["MODE"] = new(0x1101, RegType.U16, true),
            ["PIDPOL"] = new(0x1102, RegType.U16, true),
            ["PWMDUTY"] = new(0x1103, RegType.I64, true),
            ["AUTOPID"] = new(0x1107, RegType.U16, true),
            ["SPEED"] = new(0x1108, RegType.U16, true),
            ["FDEADV"] = new(0x110A, RegType.U16, true),
            ["BDEADV"] = new(0x110B, RegType.U16, true),
            ["ONSENSOR"] = new(0x110C, RegType.I16, true),
            ["LIMITED"] = new(0x110E, RegType.I16, true),
            ["STARTUPDELAY"] = new(0x110F, RegType.U16, true),
            ["POWERMODE"] = new(0x1110, RegType.U16, true),
            ["CURRENT"] = new(0x1111, RegType.U16, true),
            ["SETCURRENT"] = new(0x1112, RegType.U16, true),
            ["KP"] = new(0x1200, RegType.U32, true),
            ["KI"] = new(0x1202, RegType.U32, true),
            ["KD"] = new(0x1204, RegType.U32, true),
            ["POLYOMIAL"] = new(0x1300, RegType.U16, true),
            ["OVERTEMPUP"] = new(0x133D, RegType.I32, true),
            ["OVERTEMPLOWER"] = new(0x133F, RegType.I32, true),
            // ── 通用参数指令 ──
            ["RESET"] = new(0x0000, RegType.U16, false),
            ["TEC"] = new(0x0001, RegType.U16, false),
            ["ADDRESS"] = new(0x0002, RegType.U16, false),
            ["SINTERIORTEMP"] = new(0x0003, RegType.I16, false),
            ["CONTMODE"] = new(0x0004, RegType.I16, false),
            ["ERRORCODE"] = new(0x0007, RegType.U16, false),
            ["BOUNDTABLEONE"] = new(0x0008, RegType.U16, false),
            ["BOUNDTABLETWO"] = new(0x0009, RegType.U16, false),
            ["OVERTVPT"] = new(0x000A, RegType.U16, false),
            ["OVERTTEMP"] = new(0x000B, RegType.U16, false),
            ["FPV"] = new(0x000C, RegType.U16, false),
            ["FPWM"] = new(0x000D, RegType.U16, false)
        };

    /// <summary>只写不读的（回读会被拒），设完照下发的值回显。</summary>
    public static bool WriteOnly(string name) => name.Equals("RESET", StringComparison.OrdinalIgnoreCase);

    public static long Decode(RegType type, ReadOnlySpan<ushort> regs)
    {
        ulong acc = 0;
        foreach (var r in regs) acc = acc << 16 | r;
        return type switch
        {
            RegType.U16 => (ushort)acc,
            RegType.I16 => (short)(ushort)acc,
            RegType.U32 => (uint)acc,
            RegType.I32 => (int)(uint)acc,
            RegType.I64 => (long)acc,
            // uint64 的范围（电阻最大 5e11）long 装得下；真溢出说明表读错了，如实抛
            RegType.U64 => acc <= long.MaxValue ? (long)acc
                : throw new OverflowException($"寄存器里的 uint64 值 {acc} 超出可表示范围"),
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };
    }

    public static ushort[] Encode(RegType type, long value)
    {
        var n = type switch { RegType.U16 or RegType.I16 => 1, RegType.U32 or RegType.I32 => 2, _ => 4 };
        var raw = (ulong)value;
        var regs = new ushort[n];
        for (var i = n - 1; i >= 0; i--)
        {
            regs[i] = (ushort)(raw & 0xFFFF);
            raw >>= 16;
        }
        return regs;
    }

    /// <summary>
    /// TEC 寄存器是型号代码（协议 §3.5.1），ASCII 口回的却是型号名（实测 "215L"）。
    /// 桥上照 §3.5.1 那张表换成名字，上层看到的跟走 ASCII 一样；表里没有的照数字给。
    /// </summary>
    public static string ModelName(long code) => code switch
    {
        1 => "103", 2 => "207L", 3 => "207", 4 => "215L", 5 => "215", 6 => "215Pro",
        7 => "107L", 8 => "107", 9 => "115L", 10 => "115", 11 => "115Pro",
        12 => "100L", 13 => "100", 14 => "100Pro", 15 => "403L", 16 => "403", 17 => "403Pro",
        18 => "415L", 19 => "415", 20 => "603L", 21 => "603", 22 => "615L", 23 => "615", 24 => "615Pro",
        25 => "803L", 26 => "803", 27 => "815L", 28 => "815", 29 => "815Pro", 30 => "203L", 31 => "203",
        _ => code.ToString()
    };
}
