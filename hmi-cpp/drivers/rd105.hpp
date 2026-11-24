// RD105 TEC 温控器（光测未来，协议 v1.3.0，一台两路 TC1 / TC2）的协议层：指令 / 寄存器、量纲换算、读写。
// 从原来 C# 程序的 Tec.Drivers.Rd105（Rd105Registers / Rd105ModbusBridge / TecClient / TecAscii / TecScale /
// TecErrorCode / TecController 里的读写部分）搬过来的。只有协议，没有会话、轮询和控温。
//
// 设备有两种通讯格式（协议 §2）：
//   · Modbus-RTU —— RS485 口。现场那台接的就是它：站号 1、38400（协议写的 485 出厂值是 9600，现场改过）。缺省走这个
//   · ASCII      —— TTL 口，出厂 38400。指令长这样：「TC1:TG=?@\n」，应答「OKTC1:TG=2500000@\r\n」
// C# 那边是让按 ASCII 写的上层经一个「桥」跑在 Modbus 上；这里没有历史包袱，Modbus 直接读写寄存器，
// 两种格式对外是同一套函数，量名用的还是 ASCII 指令名（TG / PWMDUTY …），跟协议文档和现场日志对得上。
//
// 现场踩过的几条，原样留着：
//   · Modbus 一个参数一帧，不跨参数连读：现场固件对「从 0x1002 读 6 个」只答了 TCADJTEMP 自己的 4 个字节
//     （按参数长度答，不按请求个数答）
//   · 寄存器表里没有的量如实拒绝，不猜地址发出去
//   · Modbus 写完回读设备里的值：ASCII 口回显的就是设备实际存下的（可能被钳位），两边行为一致
//   · 写占空比（PWMDUTY）回显跟写进去的不一样不算通信坏：现场 MODE=3 下写 −4.41 % 回显 −6.43 %
//     （启动电压 FDEADV / BDEADV 垫了一层），那是设备的规矩。从前当坏帧 → 连着 20 拍「通信失败」停控，撞上过
//   · ASCII 口现场见过单比特翻转：分隔符 '@'(0x40) 被打成 'P'(0x50)，相邻两个字段粘在一起——解析不了就重发
//   · 所有写都是幂等的绝对值写入，重发安全
#pragma once
#include "../io/modbus_rtu.hpp"
#include "../io/serial_transport.hpp"
#include <cstdint>
#include <functional>
#include <mutex>
#include <optional>
#include <stdexcept>
#include <string>
#include <utility>
#include <vector>

namespace tec {

// 这一层报的错。Modbus 主站抛的 ModbusError 在这里接住、带上站号和寄存器地址再抛成它，上层只认这一种。
class Rd105Error : public std::runtime_error {
public:
    explicit Rd105Error(const std::string& what, bool timeout = false, std::optional<int> modbus_code = std::nullopt)
        : std::runtime_error(what), timeout(timeout), modbus_code(modbus_code) {}
    bool timeout;                    // true = 设备没答 / 应答烂尾（链路的事）；false = 答了但不对（坏帧、拒写、表里没有）
    std::optional<int> modbus_code;  // Modbus 从站回的异常码；别的错是空的
};

// ── 量纲换算（照 C# TecScale）。「raw」是线上传的整数 ─────────────────────────────
namespace rd105 {

constexpr long long kSensorNotConnectedRaw = 999999999;   // 通道没接传感器时 TCADJTEMP 回的哨兵值

double temp_from_raw(long long raw);          // 1e-5 ℃ 一个字：2500000 = 25.00000 ℃；哨兵值 → NaN
long long temp_to_raw(double celsius);
double ohms_from_raw(long long raw);          // RESISTOR：10000000000 = 10.000000 kΩ，即 μΩ
double volts_from_raw(long long raw);         // OUTV（只有 ASCII 的 DATADEMAND=2 有）：1000000000 = 10 V
double duty_percent_from_raw(long long raw);  // PWMDUTY：±2000000 = ±100 %
long long duty_percent_to_raw(double percent);
double speed_from_raw(long long raw);         // SPEED：1000 = 1 ℃/s，0 = 不限
long long speed_to_raw(double deg_per_sec);
double current_from_raw(long long raw);       // CURRENT：3256 = 3.256 A
double max_current_from_raw(long long raw);   // SETCURRENT：10 = 1.0 A
long long max_current_to_raw(double amps);
double start_voltage_percent_from_raw(long long raw);   // FDEADV / BDEADV：200 = 1 %

// ERRORCODE（0x0007）16 位状态字，协议 §3.5.8
enum Fault : uint16_t {
    kHighTempLimiting = 1 << 0,      // 温控器自身高温：超过自身过温阈值，限制输出
    kOverTempShutdown = 1 << 1,      // 温控器过温：超过内部设定最大温度，停止输出
    kUnderVoltage = 1 << 2,          // 供电过低（< 7 V）
    kOverVoltage = 1 << 3,           // 供电过高（> 30 V）
    kCh1SensorOutOfRange = 1 << 5,   // 通道 1 传感器温度越过高 / 低阈值
    kCh1CurrentLimiting = 1 << 6,    // 通道 1 输出电流到了设定最大值（限流中）
    kCh2SensorOutOfRange = 1 << 9,
    kCh2CurrentLimiting = 1 << 10,
};
std::vector<std::string> describe_faults(uint16_t code);   // 翻成中文条目；没有告警返回空
uint16_t sensor_fault_bit(int channel);                    // 这一路的「传感器越限」位
uint16_t current_limit_bit(int channel);                   // 这一路的「限流中」位

std::string model_name(long long code);                    // TEC 寄存器的型号代码 → 型号名（协议 §3.5.1），表里没有照数字给
std::string firmware_text(long long fpv);                  // FPV 130 → "v1.3.0"

// 寄存器表（协议 §3 / §4）。只抄了 C# 驱动抄的那些
enum class RegType { U16, I16, U32, I32, I64, U64 };
struct RegSpec {
    const char* name;      // ASCII 指令名
    uint16_t address;      // 通道一 / 通用的地址；通道二 = 这个 + 0x1000（协议 §2.2）
    RegType type;
    bool per_channel;      // true = 通道量，要带 TC1: / TC2:；false = 通用量
};
const RegSpec* find_reg(const std::string& name);          // 不分大小写；表里没有返回 nullptr
int reg_count(RegType type);
long long decode_regs(RegType type, const std::vector<uint16_t>& regs);   // 多字寄存器高字在前（协议示例 2500000 发成 00 26 25 A0）
std::vector<uint16_t> encode_regs(RegType type, long long value);

}  // namespace rd105

// 一次读回来的实时量。温度 NaN = 这一路没接传感器
struct Rd105Snapshot {
    double temp_c[2];          // TC1 / TC2 的实测温度（TCADJTEMP）。现场每路探头测的都是夹套
    double internal_c;         // 温控器自身（板上）温度 SINTERIORTEMP；整数 ℃，这一项不带 1e-5 标度
    double resistor_ohms[2];   // 只有 ASCII 口带；Modbus 下是 NaN（见 read_snapshot）
    double out_volts[2];       // 实际输出电压 OUTV：只有 ASCII 口带，Modbus 没有这个寄存器，是 NaN，不拿别的量冒充
};

// 一路的整套参数（参数窗 / 开机读配置用）
struct Rd105ChannelConfig {
    double target_c;
    bool enabled;
    int mode;                  // 0 双向；1 制冷；2 加热；3 通信设定电压百分比（上位机控温用这个）
    long long kp, ki, kd;      // 温控器内部单位，协议没给物理量纲（出厂 3000 / 150 / 0）
    int max_duty_percent;      // LIMITED，协议上限 90
    double speed_c_per_sec;    // 0 = 不限
    double over_temp_up_c, over_temp_lower_c;
    double max_current_a;
    int auto_pid;              // 0 常规；1 自整定进行中
};

struct Rd105DeviceInfo {
    std::string model;                    // "215L" 这类；读不出来是 "未知"
    std::string firmware;                 // "v1.3.0"；读不出来是 "未知"
    std::optional<int> controller_mode;   // CONTMODE；有的固件不答这一条，那就是空
};

class Rd105 {
public:
    // Modbus-RTU（RS485 口，现场缺省）。站号、超时、重发次数都在 ModbusRtu 上配
    explicit Rd105(ModbusRtu& modbus);
    // ASCII（TTL 口）。帧格式 8N1
    explicit Rd105(SerialTransport& ascii_port);

    bool is_modbus() const { return mb_ != nullptr; }
    static int default_baud() { return 38400; }        // 现场那台；协议写的出厂值：TTL 38400、RS485 9600
    static uint8_t default_station() { return 1; }

    // ── 每一拍读的 ──
    // 两路温度 + 自身温度。Modbus：三帧（两路 TCADJTEMP、SINTERIORTEMP），一个参数一帧；ASCII：一条 DATADEMAND=2
    Rd105Snapshot read_snapshot();
    double read_temperature(int channel);          // 单读一路 TCADJTEMP（℃，没接传感器是 NaN）
    uint16_t read_error_code();                    // 告警状态字，位定义见 rd105::Fault
    double read_current(int channel);              // 这一路的实测输出电流（A）
    double read_duty_percent(int channel);         // 设备里现在存的占空比（%）

    // ── 上位机控温写的 ──
    // 写输出占空比（PWMDUTY，带符号 ±100 %，超了钳到 ±100）。设备要在 MODE=3。返回设备回显（实际存下）的占空比，
    // 回显不符不抛。哪个符号是加热由现场接线定（现场那台：降温读回 +90、升温 −90），这一层不管方向
    double set_duty_percent(int channel, double percent);
    void set_enable(int channel, bool on);         // 输出开关 ENABLE
    void set_mode(int channel, int mode);          // 0 双向 / 1 制冷 / 2 加热 / 3 通信设定占空比
    // 开机电源输出模式：0 跟随断电前（出厂）/ 1 上电输出 / 2 上电关闭。上位机控温一律写 2：固件没有通信看门狗
    //（厂家确认 2026-08-03），断电重启后带着旧占空比复活而上位机不在，输出就没人管（加热棒尤其危险）
    void set_power_mode(int channel, int mode);
    int read_pwm_frequency();                      // FPWM 档：0 = 0.5 Hz / 1 = 1 Hz / 2 = 10 Hz / 3 = 100 Hz。设备全局，两路共用
    void set_pwm_frequency(int level);
    void set_controller_mode(int mode);            // CONTMODE，0 = 两路各自独立。这台固件对它的查询应答不正常

    // ── 温控器自己 PID 用的 / 保护值 ──
    void set_target(int channel, double celsius);
    void set_speed(int channel, double deg_per_sec);
    void set_max_duty(int channel, int percent);
    void set_over_temp(int channel, double up_c, double lower_c);
    void set_max_current(int channel, double amps);
    void set_pid(int channel, long long kp, long long ki, long long kd);
    void start_auto_tune(int channel);             // AUTOPID=1；整定完设备自己回 0
    int read_auto_pid(int channel);
    Rd105ChannelConfig read_channel_config(int channel);
    // 启动电压（正向 FDEADV，反向 BDEADV），单位 %。不为 0 时设备会在写进去的占空比上垫这么多
    std::pair<double, double> read_start_voltage_percent(int channel);

    // ── 设备标识 ──
    // 型号 / 固件 / CONTMODE。逐项容错：某一项设备答不对（坏帧、异常码）不影响别的；设备不答（超时）照抛
    Rd105DeviceInfo read_device_info();

    // ── 按量名直接读写（参数窗、调试用）。channel：1 / 2 = TC1 / TC2，0 = 通用量 ──
    long long query(int channel, const std::string& name);
    // 写完核对回显：不符再发一次，还不符抛 Rd105Error（帧是好的、值不符，多半是设备拒写 / 钳位，不值得多发）
    void set(int channel, const std::string& name, long long value);
    // 写完把回显带回来，不符不抛
    long long set_echo(int channel, const std::string& name, long long value);

    // ASCII 口的链路参数（Modbus 的在 ModbusRtu 上）
    int min_command_gap_ms = 10;   // 相邻两条指令的间隔，协议要 ≥ 5 ms
    int reply_timeout_ms = 500;
    int inter_byte_idle_ms = 60;   // 应答开始以后这么久没有新字节就算一帧完了（兜底不以 \r\n 结尾的长应答）
    int timeout_retries = 1;       // 没应答重发几次。大电流 PWM 开关下偶尔丢一帧是常态（实测一整天一次）
    int corrupt_retries = 1;       // 应答解析不了重发几次

private:
    long long query_text_or_number(int channel, const std::string& name, std::string* text);
    std::vector<uint16_t> mb_read(uint16_t addr, int count);
    void mb_write(uint16_t addr, const std::vector<uint16_t>& regs);
    const rd105::RegSpec& mb_spec(int channel, const std::string& name, uint16_t* addr) const;
    std::string ascii_exec(const std::string& command);
    void ascii_exec_parsed(const std::string& command, const std::function<void(const std::string&)>& parse);

    ModbusRtu* mb_ = nullptr;
    SerialTransport* ascii_ = nullptr;
    std::recursive_mutex mu_;      // 一次只做一件事：写完回读、一条指令的重发，中间不许别的线程插一问
    long long last_command_end_ = -1000000;
};

}  // namespace tec
