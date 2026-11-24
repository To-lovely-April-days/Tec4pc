// AQMD2408BLS-M 无刷电机驱动器（成都爱控，搅拌用）的协议层：寄存器表、量纲换算、一问一答的读写。
// 从原来 C# 程序的 AqmdRegisters + AqmdClient 搬过来的。只管「怎么跟驱动器说话」，
// 「什么时候读、读到了干什么」（开机核对、轮询、断线重开、数圈测试、学习流程）是上层会话的事，不在这里。
//
// 寄存器地址全部照《AQMD2408BLS-M 用户手册 V0.90》第 6 章（p113–128），是手册里的 Modbus 保持寄存器地址。
// 现场那两台（2026-11-09 串口工具实测）型号名读回「AQMD2408BLS-M2」、固件 v1.3，寄存器排布与手册一致；
// 一次读 32 个寄存器答得全，0x0060 起读 28 个也通。
//
// 现场踩过的坑（原样带过来，上层接这个类的时候要知道）：
//   · 串口帧格式：两台都是 9600 bps、无校验 + 2 停止位（8N2）实测通。驱动器自己的 0x0092 报的是偶校验 + 1 停止位——
//     8E1 和 8N2 都是 11 位一个字符，所以通；8N1（10 位）不通。站号都是 1。
//   · 速度闭环的目标是「换向频率」（Hz），不是 rpm：rpm = Hz ÷ 极个数 × 20 ÷ 减速比（手册 p10）。
//     极数不知道就换算不了。现场两台出厂极数 0x0073 = 0（没配），得先数圈测出来写进去。
//   · 驱动器自己算的转速 0x0034 在极数没配时读回 0xFFFF（现场见过）——不信它，
//     实际转速一律用实测换向频率 0x0022 按我们自己的极数算。
//   · 06 / 10 写进去的配置参数是临时的，掉电就丢；要再往 0x0160（电机参数）/ 0x0180（系统参数）写 1 才落到存储器。
//     写了读回却不一样：查驱动器的 0x0094「485 控制时禁止参数配置」。
//   · 没学习电机相序时，写速度 / 加减速会被拒：异常码 0x60（现场第一次写加减速就吃了一串 01 86 60）。
//     通用的 Modbus 翻译只会说「未定义的异常码」，用 explain_write_rejected 把话说对。
//   · 加减速要写在目标速度之前（先定坡度再给目标）；加减速写 0 电机永远加不上去，accel_reg 钳在 1 以上。
//   · 写 0x0043 本身就把驱动器切到速度闭环方式（手册 §4.5.1），没有单独的「启动」命令：
//     启动 = 写一个非 0 的目标；转向 = 目标的正负号；停 = 写 0x0040。
#pragma once
#include "../io/modbus_rtu.hpp"
#include <cstdint>
#include <string>

namespace tec {

// ── 寄存器表 ────────────────────────────────────────────────────────
namespace aqmd_reg {
// 6.3.1 设备描述信息（只读）
constexpr uint16_t kDeviceId = 0x0000;          // 设备标识（现场读回 0x0203）
constexpr uint16_t kVersion = 0x0001;           // 高字节主版本、低字节副版本（现场 0x0103 = v1.3）
constexpr uint16_t kNameStart = 0x0002;         // 0x0002~0x0009：'\0' 结尾的 ASCII 名字（现场 "AQMD2408BLS-M2"）
constexpr uint16_t kPwmHz = 0x000B;             // PWM 频率 Hz（现场 18000）
constexpr uint16_t kMaxOutputCurrent = 0x000C;  // ×0.01 A（现场 10.00 A）
constexpr int kIdentityCount = 16;              // 0x0000~0x000F 一帧读全

// 6.3.2 实时状态（只读）
constexpr uint16_t kStatusStart = 0x0020;
constexpr int kStatusCount = 22;                // 0x0020~0x0035 一帧读全（0x0036 手册没定义，不读）
constexpr uint16_t kPwm = 0x0020;               // ×0.1 %，有符号
constexpr uint16_t kCurrent = 0x0021;           // ×0.01 A
constexpr uint16_t kFrequency = 0x0022;         // 实时换向频率：0x0035=1 时数值即 Hz，=0 时 ×0.1 Hz；有符号，负 = 反转
constexpr uint16_t kStall = 0x0032;             // 0 未堵转 / 1 正转堵转停止 / 2 反转堵转停止
constexpr uint16_t kFault = 0x0033;             // 故障码 0~9，见 aqmd_fault_text
constexpr uint16_t kRpm = 0x0034;               // 电机转速 rpm（要先配对极数 0x0073；没配读回 0xFFFF，现场见过）
constexpr uint16_t kRpmX10 = 0x0035;            // 1 = 0x0034 / 0x0022 的数值要 ×10
constexpr uint16_t kDriverTemp = 0x0037;        // 驱动电路温度 ×0.1 ℃，有符号
constexpr uint16_t kSupplyVoltage = 0x0038;     // 电源电压 ×0.1 V

// 6.3.3 速度控制（写）
constexpr uint16_t kStopCmd = 0x0040;           // 0 正常停止 / 1 紧急制动 / 2 自由停止
constexpr uint16_t kSpeedSetpoint = 0x0043;     // 速度闭环目标换向频率 ×0.1 Hz（0x0077=1 时 ×1），有符号，负 = 反转
constexpr uint16_t kAccel = 0x0052;             // 加速加速度 ×0.1 Hz/s（临时，不存）
constexpr uint16_t kDecel = 0x0053;             // 减速加速度 ×0.1 Hz/s（临时，不存）

// 6.3.4 电机控制参数
constexpr uint16_t kMotorStart = 0x0060;
constexpr int kMotorCount = 28;                 // 0x0060~0x007B（现场读 28 个通；0x007C 之后手册没定义）
constexpr uint16_t kMaxAccel = 0x0062;          // 最大加速加速度 ×0.1 Hz/s（现场 5000 Hz/s）
constexpr uint16_t kMaxDecel = 0x0064;          // 最大减速加速度 ×0.1 Hz/s
constexpr uint16_t kMaxFrequency = 0x0066;      // 最大换向频率 ×0.1 Hz（现场 2000 Hz）
constexpr uint16_t kRatedCurrent = 0x006A;      // 电机额定电流 ×0.01 A（出厂 7.00，现场还是出厂值）
constexpr uint16_t kMaxLoadCurrent = 0x006B;    // 最大负载电流 ×0.01 A
constexpr uint16_t kLoopAlgorithm = 0x0070;     // 0 速度闭环 / 1 时间-位置闭环 / 2 时间-位置速率
constexpr uint16_t kPoles = 0x0073;             // 电机极个数（现场 0 = 没配）
constexpr uint16_t kGearRatio = 0x0074;         // 减速比 ×0.1（现场 10 = 1.0）
constexpr uint16_t kLearned = 0x0075;           // 0 未学习 / 1 已学习（只能靠学习动作改）
constexpr uint16_t kSetpointX10 = 0x0077;       // 1 = 0x0043 的数值 ×10

// 6.3.5 系统参数
constexpr uint16_t kSystemStart = 0x0080;
constexpr int kSystemCount = 32;                // 0x0080~0x009F（现场读 32 个通）
constexpr uint16_t kStallStopTime = 0x008E;     // 堵转停止时间 ×0.1 s（0 = 不保护；现场 0）
constexpr uint16_t kBaudHi = 0x0090;
constexpr uint16_t kBaudLo = 0x0091;
constexpr uint16_t kParityMode = 0x0092;        // 0 无校验+2 停止位 / 1 奇校验+1 停止位 / 2 偶校验+1 停止位（现场 2）
constexpr uint16_t kDefaultSpeedMode = 0x0093;  // 485 默认调速方式 0 占空比 / 1 速度闭环 / 2 位置闭环（现场 1）
constexpr uint16_t kCommWatchdog = 0x0095;      // 通讯中断停止时间 ×0.1 s（0 = 不停；现场 0）
constexpr uint16_t kAlarmsDisabled = 0x0099;    // 1 = 禁用报警（现场 0）
constexpr uint16_t kSlaveAddress = 0x009C;      // 485 从站地址（现场 1）

// 6.3.9 电机学习
constexpr uint16_t kLearnCommand = 0x00E1;      // 写 1 开始学习（= 按键短按一下）
constexpr uint16_t kLearnStatus = 0x00E2;       // 0 就绪 / 1 正在学习 / 2 正在停止 / 3 学习完毕 / 4 学习失败

// 6.3.11 配置参数存储（06 写进去的参数是临时的，写 1 到这里才落到存储器）
constexpr uint16_t kStoreMotorParams = 0x0160;
constexpr uint16_t kStoreSystemParams = 0x0180;

// 实时速度最佳测量范围的下限（手册 §1.2：10 Hz ~ 4000 Hz，低于它「无测量值」）。闭环的最低可用转速由它折算。
constexpr double kMinMeasurableHz = 10;
}  // namespace aqmd_reg

// ── 换算（手册 p10 / p117）──────────────────────────────────────────
// 取整一律「四舍六入五成双」，跟 C# 的 Math.Round 一个结果——两边算出来的寄存器值要逐位相同。

// 换向频率 → 转速：rpm = Hz ÷ 极个数 × 20，有减速箱再 ÷ 减速比。极数 ≤ 0（没配）返回 NaN：算不出就别编数。
double aqmd_hz_to_rpm(double hz, int poles, double ratio);
// 转速 → 换向频率：Hz = rpm × 极个数 × 减速比 ÷ 20。减速比 ≤ 0 当 1。
double aqmd_rpm_to_hz(double rpm, int poles, double ratio);
// 0x0043 的寄存器值：缺省 ×0.1 Hz 一格；0x0077 启用（setpoint_x10）时 ×1 Hz 一格。有符号，负 = 反转；超出 int16 钳到边界。
int16_t aqmd_speed_reg(double hz, bool setpoint_x10);
// 0x0022 读回 → Hz（有符号）：0x0035=1（x10）时数值即 Hz，=0 时 ×0.1。
double aqmd_frequency_of(uint16_t raw, bool x10);
// 0x0052 / 0x0053 的寄存器值（×0.1 Hz/s），钳在 1 ~ max_reg 之间——0 写进去电机永远加不上去。
// max_reg 传驱动器的 0x0062 / 0x0064（读不到就传 0xFFFF）。
uint16_t aqmd_accel_reg(double hz_per_second, uint16_t max_reg);

// ── 码 → 人话 ───────────────────────────────────────────────────────
std::string aqmd_fault_text(int code);          // 0x0033 故障码的名字
// 手册 §7.2 的处置办法，日志里跟着故障码一起说——现场的人不用翻手册。没有对应办法返回空串。
std::string aqmd_fault_advice(int code);
std::string aqmd_stall_text(int stall);         // 0x0032：未堵转 / 正转堵转停止 / 反转堵转停止
std::string aqmd_learn_status_text(int status); // 0x00E2
std::string aqmd_frame_name(int parity_mode);   // 0x0092：「偶校验 + 1 停止位」这样的叫法
// 驱动器自己的扩展异常码（手册 6.2.4 表 6.2）：0x40 禁止操作、0x60 尚未学习电机相序。
// 写被拒时拿这个出报错文字；不是这几个码（或者根本是链路错）就原样返回 e.what()。
std::string aqmd_explain_write_rejected(const ModbusError& e);

// ── 读回来的几块数据 ────────────────────────────────────────────────

// 设备描述信息（0x0000~0x000F）
struct AqmdIdentity {
    uint16_t device_id = 0;
    std::string version;                // "1.3"
    std::string name;                   // "AQMD2408BLS-M2"
    int pwm_hz = 0;
    double max_output_current_a = 0;
    std::string display() const;        // 「AQMD2408BLS-M2 v1.3」——探测回显、开机日志都这么叫它
};

// 电机控制参数（0x0060~0x007B）里用得上的几项
struct AqmdMotorParams {
    double max_frequency_hz = 0;
    uint16_t max_accel_reg = 0;         // 原始寄存器值（×0.1 Hz/s），直接喂给 aqmd_accel_reg 当上限
    uint16_t max_decel_reg = 0;
    double rated_current_a = 0;
    double max_load_current_a = 0;
    int loop_algorithm = 0;
    int poles = 0;                      // 0 = 没配
    double gear_ratio = 0;
    bool learned = false;
    bool setpoint_x10 = false;
};

// 一拍实时状态（0x0020~0x0035）。frequency_hz 有符号：负 = 反转。
struct AqmdStatus {
    double pwm_percent = 0;
    double current_a = 0;
    double frequency_hz = 0;
    int stall = 0;                      // 0 未堵转 / 1 正转堵转停止 / 2 反转堵转停止
    int fault = 0;                      // 0 = 无故障
    uint16_t rpm_raw = 0;               // 驱动器自己算的 rpm，极数没配时是 0xFFFF——只留着给人看，不拿来用
    bool rpm_x10 = false;
    // 实际转速（rpm，不带符号）：按实测换向频率和我们自己的极数算。极数 ≤ 0 返回 NaN。
    double rpm(int poles, double ratio) const;
};

// 系统参数（0x0080~0x009F）里要摆给人看的几项
struct AqmdSystemParams {
    int baud = 0;
    int parity_mode = 0;
    int slave_address = 0;
    double watchdog_seconds = 0;        // 通讯中断停机时间，0 = 不停
    double stall_stop_seconds = 0;      // 堵转停机时间，0 = 不保护
    bool alarms_disabled = false;
    int default_speed_mode = 0;
};

// 0x0040 的三种停法。搅拌停机一律「正常停止」（按 0x0053 的减速度减下来）。
enum class AqmdStopMode : uint16_t { Normal = 0, EmergencyBrake = 1, Coast = 2 };

// ── 访问层：Modbus 一问一答 → 工程量 ────────────────────────────────
// 同步接口，出错原样抛 ModbusError（链路超时 / 坏帧 / 从站异常码都在里面，异常码用 aqmd_explain_write_rejected 翻译）。
// 不持有串口，也不做重试以外的任何恢复——那是 ModbusRtu 和上层会话的事。
class Aqmd {
public:
    explicit Aqmd(ModbusRtu& modbus) : mb_(modbus) {}
    ModbusRtu& modbus() { return mb_; }

    AqmdIdentity read_identity();
    AqmdMotorParams read_motor();
    AqmdStatus read_status();
    AqmdSystemParams read_system();
    struct TempVolt { double temp_c; double volt; };
    TempVolt read_temp_volt();                          // 驱动器温度与电源电压：0x0037、0x0038 一帧读两个

    void start_learn();                                 // 0x00E1 = 1，等于按键短按一下。电机要空载
    int read_learn_status();                            // 0x00E2，见 aqmd_learn_status_text

    // 写速度闭环目标（0x0043，有符号，负 = 反转）。写它本身就把驱动器切到速度闭环方式，电机就转起来了。
    void write_speed(int16_t reg);
    void stop(AqmdStopMode mode = AqmdStopMode::Normal);
    // 加减速加速度一帧写两个（0x0052 / 0x0053 相邻，手册写明支持 0x10）。临时值，不存。要在 write_speed 之前写。
    void write_accel(uint16_t accel_reg, uint16_t decel_reg);

    void write_poles(uint16_t poles);                   // 0x0073。临时，要 store_motor_params 才落盘
    // 额定电流与最大负载电流一起写成同一个值（手册 §3.2 第 5 步：如无要求两者相同）。单位 ×0.01 A。临时，要存。
    void write_rated_current(uint16_t centi_amps);
    void write_watchdog(uint16_t tenths);               // 0x0095 通讯中断停机时间 ×0.1 s，0 = 不停。临时，要 store_system_params
    void write_stall_stop(uint16_t tenths);             // 0x008E 堵转停机时间 ×0.1 s，0 = 不保护。临时，要 store_system_params
    void store_motor_params();                          // 0x0160 = 1
    void store_system_params();                         // 0x0180 = 1

private:
    ModbusRtu& mb_;
};

}  // namespace tec
