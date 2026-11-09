using System.Buffers.Binary;

namespace Tec.Drivers.Htlab;

/// <summary>
/// 两液一气进料系统（霍桐仪器 HTLAB，Modbus-RTU）的寄存器表。摘自厂家给的 MaxChemical 插件源码
/// `TwoLiquidOneGasFeedSystem_ModbusRTU.cs` 的注释（2026-11-10 用户给的）：
/// · CSV 里的地址按 MCGS PLC 地址（从 1 数）标，实际下发到 Modbus 帧的是从 0 数的，整体减 1——这里写的全是下发地址；
/// · Float 两个寄存器一个数，IEEE-754 大端（ABCD，高字在前）；Int 一个寄存器；
/// · 功能码 03 读、06 写单个、10 写多个；一次读 49 个寄存器（0~48）把过程值 / 配置 / 状态 / 报警全拿到。
/// 单位：MFC 是 L/min、L；泵是 mL/min、mL、MPa。
/// </summary>
public static class FeedRegisters
{
    // ── Float（各占 2 寄存器）──
    public const ushort MfcSetpoint = 0;        // MFC 设定流量 L/min            AI_SV_00H_1
    public const ushort MfcFlow = 2;            // MFC 瞬时流量 L/min            _MFC_Lm
    public const ushort MfcTotal = 4;           // MFC 累计流量 L                _MFC_ADD_L
    public const ushort Pump1Setpoint = 6;      // 泵 1 设定流量 mL/min          Flow_F_SV_3
    public const ushort Pump1Flow = 8;          // 泵 1 瞬时流量 mL/min
    public const ushort Pump1Total = 10;        // 泵 1 累计 mL
    public const ushort Pump1Pressure = 12;     // 泵 1 压力 MPa
    public const ushort Pump2Setpoint = 14;
    public const ushort Pump2Flow = 16;
    public const ushort Pump2Total = 18;
    public const ushort Pump2Pressure = 20;
    public const ushort MfcRangeHigh = 22;      // MFC 量程上限 L/min            AI_SCH_0EH_1
    public const ushort MfcRangeLow = 24;       // MFC 量程下限
    public const ushort MfcCorrection = 26;     // MFC 流量修正系数
    public const ushort MfcDpt = 28;            // MFC 小数点位（固定 1）
    public const ushort Pump1PressureHigh = 30; // 泵 1 压力上限报警 MPa
    public const ushort Pump1PressureLow = 32;
    public const ushort Pump2PressureHigh = 34;
    public const ushort Pump2PressureLow = 36;

    // ── Int16（各占 1 寄存器）──
    public const ushort MfcOnOff = 38;          // **0 运行 / 1 停止**（跟泵反着，厂家就这么定的）  AI_Srun_1BH_1
    public const ushort Pump1OnOff = 39;        // 1 运行 / 0 停止                                   Liq_PP_Run_1
    public const ushort Pump2OnOff = 40;
    public const ushort Pump1PressureAlarm = 41;
    public const ushort Pump2PressureAlarm = 42;
    public const ushort MfcCommAlarm = 43;
    public const ushort Pump1CommAlarm = 44;
    public const ushort Pump2CommAlarm = 45;
    public const ushort AlarmReset = 46;        // 写 1 复位全部报警
    public const ushort AlarmSilence = 47;      // 写 1 消音
    public const ushort AlarmStatus = 48;       // 1 = 有报警

    public const int AllCount = 49;             // 0~48 一帧读全（Modbus 单帧上限 125）

    /// <summary>两个寄存器（高字在前）→ IEEE-754 单精度。</summary>
    public static float FloatOf(ushort hi, ushort lo)
    {
        Span<byte> b = stackalloc byte[4];
        b[0] = (byte)(hi >> 8); b[1] = (byte)hi; b[2] = (byte)(lo >> 8); b[3] = (byte)lo;
        return BinaryPrimitives.ReadSingleBigEndian(b);
    }

    /// <summary>单精度 → 两个寄存器（高字在前），写 0x10 用。</summary>
    public static (ushort Hi, ushort Lo) RegsOf(float v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteSingleBigEndian(b, v);
        return ((ushort)(b[0] << 8 | b[1]), (ushort)(b[2] << 8 | b[3]));
    }

    public static string AlarmName(ushort reg) => reg switch
    {
        Pump1PressureAlarm => "泵 1 压力上限报警",
        Pump2PressureAlarm => "泵 2 压力上限报警",
        MfcCommAlarm => "MFC 通信故障（进料系统内部 PLC 读不到 MFC）",
        Pump1CommAlarm => "泵 1 通信故障（进料系统内部 PLC 读不到泵 1）",
        Pump2CommAlarm => "泵 2 通信故障（进料系统内部 PLC 读不到泵 2）",
        _ => $"寄存器 {reg}"
    };
}
