using Tec.Drivers.Rd105.Modbus;

namespace Tec.Drivers.Aqmd;

/// <summary>
/// AQMD2408BLS-M 无刷电机驱动器（成都爱控）的寄存器表与标度换算。
/// 全部照《AQMD2408BLS-M 用户手册 V0.90》第 6 章（p113–128）抄，地址是手册里的 Modbus 保持寄存器地址。
/// 现场那两台（2026-11-09 串口工具实测）型号名读回「AQMD2408BLS-M2」、固件 v1.3，
/// 一次读 32 个寄存器答得全，0x0060 起读 28 个也通；寄存器排布与手册一致。
/// </summary>
public static class AqmdRegisters
{
    // ── 6.3.1 设备描述信息（只读）──────────────────────────────────
    public const ushort DeviceId = 0x0000;          // 设备标识（现场读回 0x0203）
    public const ushort Version = 0x0001;           // 高字节主版本、低字节副版本（现场 0x0103 = v1.3）
    public const ushort NameStart = 0x0002;         // 0x0002~0x0009：'\0' 结尾的 ASCII 名字（现场 "AQMD2408BLS-M2"）
    public const ushort PwmHz = 0x000B;             // PWM 频率 Hz（现场 18000）
    public const ushort MaxOutputCurrent = 0x000C;  // ×0.01 A（现场 10.00 A）
    public const int IdentityCount = 16;            // 0x0000~0x000F 一帧读全

    // ── 6.3.2 实时状态（只读）───────────────────────────────────────
    public const ushort StatusStart = 0x0020;
    public const int StatusCount = 22;              // 0x0020~0x0035 一帧读全（0x0036 手册没定义，不读）
    public const ushort Pwm = 0x0020;               // ×0.1 %
    public const ushort Current = 0x0021;           // ×0.01 A
    public const ushort Frequency = 0x0022;         // 实时换向频率：0x0035=1 时数值即 Hz，=0 时 ×0.1 Hz；有符号，负 = 反转
    public const ushort Stall = 0x0032;             // 0 未堵转 / 1 正转堵转停止 / 2 反转堵转停止
    public const ushort Fault = 0x0033;             // 故障码 0~9，见 FaultText
    public const ushort Rpm = 0x0034;               // 电机转速 rpm（要先配对极数 0x0073；没配读回 0xFFFF，现场见过）
    public const ushort RpmX10 = 0x0035;            // 1 = 0x0034 / 0x0022 的数值要 ×10
    public const ushort DriverTemp = 0x0037;        // 驱动电路温度 ×0.1 ℃
    public const ushort SupplyVoltage = 0x0038;     // 电源电压 ×0.1 V

    // ── 6.3.9 电机学习 ──────────────────────────────────────────────
    public const ushort LearnCommand = 0x00E1;      // 写 1 开始学习（= 按键短按一下）
    public const ushort LearnStatus = 0x00E2;       // 0 就绪 / 1 正在学习 / 2 正在停止 / 3 学习完毕 / 4 学习失败

    public static string LearnStatusText(int s) => s switch
    {
        0 => "准备就绪", 1 => "正在学习", 2 => "正在停止", 3 => "学习完毕", 4 => "学习失败", _ => $"状态码 {s}"
    };

    // ── 6.3.3 速度控制（写）──────────────────────────────────────────
    public const ushort StopCmd = 0x0040;           // 0 正常停止 / 1 紧急制动 / 2 自由停止
    public const ushort SpeedSetpoint = 0x0043;     // 速度闭环目标换向频率 ×0.1 Hz（0x0077=1 时 ×1），有符号，负 = 反转
    public const ushort Accel = 0x0052;             // 加速加速度 ×0.1 Hz/s（临时，不存）
    public const ushort Decel = 0x0053;             // 减速加速度 ×0.1 Hz/s（临时，不存）

    // ── 6.3.4 电机控制参数 ───────────────────────────────────────────
    public const ushort MotorStart = 0x0060;
    public const int MotorCount = 28;               // 0x0060~0x007B（现场读 28 个通；0x007C 之后手册没定义）
    public const ushort MaxAccel = 0x0062;          // 最大加速加速度 ×0.1 Hz/s（现场 5000 Hz/s）
    public const ushort MaxDecel = 0x0064;          // 最大减速加速度 ×0.1 Hz/s
    public const ushort MaxFrequency = 0x0066;      // 最大换向频率 ×0.1 Hz（现场 2000 Hz）
    public const ushort RatedCurrent = 0x006A;      // 电机额定电流 ×0.01 A（出厂 7.00，现场还是出厂值）
    public const ushort MaxLoadCurrent = 0x006B;    // 最大负载电流 ×0.01 A
    public const ushort LoopAlgorithm = 0x0070;     // 0 速度闭环 / 1 时间-位置闭环 / 2 时间-位置速率
    public const ushort Poles = 0x0073;             // 电机极个数（现场 0 = 没配）
    public const ushort GearRatio = 0x0074;         // 减速比 ×0.1（现场 10 = 1.0）
    public const ushort Learned = 0x0075;           // 0 未学习 / 1 已学习（只能靠学习动作改）
    public const ushort SetpointX10 = 0x0077;       // 1 = 0x0043 的数值 ×10

    // ── 6.3.5 系统参数 ──────────────────────────────────────────────
    public const ushort SystemStart = 0x0080;
    public const int SystemCount = 32;              // 0x0080~0x009F（现场读 32 个通）
    public const ushort StallStopTime = 0x008E;     // 堵转停止时间 ×0.1 s（0 = 不保护；现场 0）
    public const ushort BaudHi = 0x0090;
    public const ushort BaudLo = 0x0091;
    public const ushort ParityMode = 0x0092;        // 0 无校验+2 停止位 / 1 奇校验+1 停止位 / 2 偶校验+1 停止位（现场 2）
    public const ushort DefaultSpeedMode = 0x0093;  // 485 默认调速方式 0 占空比 / 1 速度闭环 / 2 位置闭环（现场 1）
    public const ushort CommWatchdog = 0x0095;      // 通讯中断停止时间 ×0.1 s（0 = 不停；现场 0）
    public const ushort AlarmsDisabled = 0x0099;    // 1 = 禁用报警（现场 0）
    public const ushort SlaveAddress = 0x009C;      // 485 从站地址（现场 1）

    // ── 6.3.11 配置参数存储（06 写进去的参数是临时的，写 1 到这里才落到存储器）──
    public const ushort StoreMotorParams = 0x0160;
    public const ushort StoreSystemParams = 0x0180;

    /// <summary>实时速度最佳测量范围的下限（手册 §1.2：10 Hz ~ 4000 Hz，低于它「无测量值」）。闭环的最低可用转速由它折算。</summary>
    public const double MinMeasurableHz = 10;

    // ── 换算（手册 p10 / p117）──────────────────────────────────────

    /// <summary>换向频率 → 转速：rpm = Hz ÷ 极个数 × 20，有减速箱再 ÷ 减速比。</summary>
    public static double HzToRpm(double hz, int poles, double ratio)
        => poles <= 0 ? double.NaN : hz * 20 / poles / (ratio <= 0 ? 1 : ratio);

    /// <summary>转速 → 换向频率：Hz = rpm × 极个数 × 减速比 ÷ 20。</summary>
    public static double RpmToHz(double rpm, int poles, double ratio)
        => rpm * poles * (ratio <= 0 ? 1 : ratio) / 20;

    /// <summary>0x0043 的寄存器值：缺省 ×0.1 Hz 一格；0x0077 启用时 ×1 Hz 一格。有符号，负 = 反转。</summary>
    public static short SpeedReg(double hz, bool setpointX10)
        => (short)Math.Clamp(Math.Round(setpointX10 ? hz : hz * 10), short.MinValue, short.MaxValue);

    /// <summary>0x0022 读回 → Hz（有符号）：0x0035=1 时数值即 Hz，=0 时 ×0.1。</summary>
    public static double FrequencyOf(ushort raw, bool x10)
        => (short)raw * (x10 ? 1.0 : 0.1);

    /// <summary>0x0052 / 0x0053 的寄存器值（×0.1 Hz/s），钳在 1 ~ 上限之间——0 写进去电机永远加不上去。</summary>
    public static ushort AccelReg(double hzPerSecond, ushort maxReg)
        => (ushort)Math.Clamp(Math.Round(hzPerSecond * 10), 1, Math.Max(1, (int)maxReg));

    // ── 故障码（6.3.2 的 0x0033 + §7.2 故障报警处理）──────────────────

    public static string FaultText(int code) => code switch
    {
        0 => "无故障",
        1 => "尚未学习电机相序",
        2 => "堵转停止",
        3 => "霍尔信号错误",
        4 => "无法达到目标速度",
        5 => "线圈错误（本款不支持）",
        6 => "过流关断",
        7 => "过热关断",
        8 => "过压关断",
        9 => "欠压关断",
        _ => $"未知故障码 {code}"
    };

    /// <summary>手册 §7.2 的处置办法，日志里跟着故障码一起说——现场的人不用翻手册。</summary>
    public static string FaultAdvice(int code) => code switch
    {
        1 => "电机空载时短按驱动器按键学习相序（黄绿灯交替闪 6 次后同时长亮 1 s 为成功）",
        2 => "查桨叶是不是卡住、负载是不是过大；制动或反转一次可清除堵转标志",
        3 => "查霍尔信号线接没接牢、与电机电源线分开走线；偶发可把霍尔错误屏蔽时间（0x0105）调大",
        4 => "查目标转速是否超过电机满 PWM 能达到的转速、负载是否过大、最大负载电流是否配小了、加速度是否配大了",
        6 => "查额定电流是否配得比电机实际小、电机线是否短路或接触不良、是否高速时被突然卡住",
        7 => "查驱动器通风、是否长时间过载、过热关断温度是否配得太低",
        8 => "查电源空载电压是否超过过压关断值、制动时电压是否冲高（非电池供电建议禁用过压关断）",
        9 => "查电源带载时是否跌落、电源功率是否太小、电源线是否过长过细",
        _ => ""
    };

    /// <summary>
    /// 驱动器自己的扩展异常码（手册 6.2.4 表 6.2）：0x40 禁止操作、0x60 尚未学习电机相序。
    /// 现场第一次写加减速就吃了一串 `01 86 60`——通用的 Modbus 翻译只会说「未定义的异常码」，这里把话说对。
    /// </summary>
    public static string ExplainWriteRejected(ModbusException ex) => ex.Code switch
    {
        0x60 => "驱动器拒绝了（异常码 0x60：尚未学习电机相序）——确认黄灯是灭的（485 通讯方式），" +
                "然后电机空载时短按驱动器按键学习一次，或用厂家软件学习；学成再连",
        0x40 => "驱动器拒绝了（异常码 0x40：禁止操作）——多半还在数字/模拟信号控制方式（黄灯常亮）：" +
                "按住驱动器按键 1 s 松开切到 485 通讯方式（黄灯灭、绿灯闪）再试",
        0x02 => "驱动器拒绝了（异常码 0x02：非法数据地址）——这个固件没有这个寄存器，查手册版本",
        0x03 => "驱动器拒绝了（异常码 0x03：非法数据值）——写的值超出寄存器允许范围",
        _ => ex.Message
    };
}
