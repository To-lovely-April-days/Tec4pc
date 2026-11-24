#include "aqmd.hpp"
#include <cmath>
#include <cstdio>
#include <limits>

namespace tec {

namespace {
// C# 的 Math.Round 是「五成双」（0.5 → 0、1.5 → 2），std::round 是「五远离零」。
// 两边算出来的寄存器值要一样，所以自己写一个，不依赖浮点环境的舍入模式。
double round_half_even(double v) {
    const double f = std::floor(v);
    const double d = v - f;
    if (d > 0.5) return f + 1;
    if (d < 0.5) return f;
    return std::fmod(f, 2.0) == 0 ? f : f + 1;
}
double clamp(double v, double lo, double hi) { return v < lo ? lo : (v > hi ? hi : v); }
}  // namespace

// ── 换算 ────────────────────────────────────────────────────────────

double aqmd_hz_to_rpm(double hz, int poles, double ratio) {
    if (poles <= 0) return std::numeric_limits<double>::quiet_NaN();
    return hz * 20 / poles / (ratio <= 0 ? 1 : ratio);
}

double aqmd_rpm_to_hz(double rpm, int poles, double ratio) {
    return rpm * poles * (ratio <= 0 ? 1 : ratio) / 20;
}

int16_t aqmd_speed_reg(double hz, bool setpoint_x10) {
    // NaN 转整数在 C++ 里是未定义行为（C# 那边没管这个）；算不出来的目标当 0，宁可不转也不乱转
    if (std::isnan(hz)) return 0;
    return static_cast<int16_t>(clamp(round_half_even(setpoint_x10 ? hz : hz * 10), -32768, 32767));
}

double aqmd_frequency_of(uint16_t raw, bool x10) {
    return static_cast<int16_t>(raw) * (x10 ? 1.0 : 0.1);
}

uint16_t aqmd_accel_reg(double hz_per_second, uint16_t max_reg) {
    if (std::isnan(hz_per_second)) return 1;   // 同上：算不出来按最缓的坡度走
    const double hi = max_reg < 1 ? 1 : max_reg;
    return static_cast<uint16_t>(clamp(round_half_even(hz_per_second * 10), 1, hi));
}

// ── 码 → 人话 ───────────────────────────────────────────────────────

std::string aqmd_fault_text(int code) {
    switch (code) {
        case 0: return "无故障";
        case 1: return "尚未学习电机相序";
        case 2: return "堵转停止";
        case 3: return "霍尔信号错误";
        case 4: return "无法达到目标速度";
        case 5: return "线圈错误（本款不支持）";
        case 6: return "过流关断";
        case 7: return "过热关断";
        case 8: return "过压关断";
        case 9: return "欠压关断";
        default: return "未知故障码 " + std::to_string(code);
    }
}

std::string aqmd_fault_advice(int code) {
    switch (code) {
        case 1: return "电机空载时短按驱动器按键学习相序（黄绿灯交替闪 6 次后同时长亮 1 s 为成功）";
        case 2: return "查桨叶是不是卡住、负载是不是过大；制动或反转一次可清除堵转标志";
        case 3: return "查霍尔信号线接没接牢、与电机电源线分开走线；偶发可把霍尔错误屏蔽时间（0x0105）调大";
        case 4: return "查目标转速是否超过电机满 PWM 能达到的转速、负载是否过大、最大负载电流是否配小了、加速度是否配大了";
        case 6: return "查额定电流是否配得比电机实际小、电机线是否短路或接触不良、是否高速时被突然卡住";
        case 7: return "查驱动器通风、是否长时间过载、过热关断温度是否配得太低";
        case 8: return "查电源空载电压是否超过过压关断值、制动时电压是否冲高（非电池供电建议禁用过压关断）";
        case 9: return "查电源带载时是否跌落、电源功率是否太小、电源线是否过长过细";
        default: return "";
    }
}

std::string aqmd_stall_text(int stall) {
    switch (stall) {
        case 1: return "正转堵转停止";
        case 2: return "反转堵转停止";
        default: return "未堵转";
    }
}

std::string aqmd_learn_status_text(int status) {
    switch (status) {
        case 0: return "准备就绪";
        case 1: return "正在学习";
        case 2: return "正在停止";
        case 3: return "学习完毕";
        case 4: return "学习失败";
        default: return "状态码 " + std::to_string(status);
    }
}

std::string aqmd_frame_name(int parity_mode) {
    switch (parity_mode) {
        case 0: return "无校验 + 2 停止位";
        case 1: return "奇校验 + 1 停止位";
        case 2: return "偶校验 + 1 停止位";
        default: return "校验码 " + std::to_string(parity_mode);
    }
}

std::string aqmd_explain_write_rejected(const ModbusError& e) {
    if (!e.code) return e.what();
    switch (*e.code) {
        case 0x60:
            return "驱动器拒绝了（异常码 0x60：尚未学习电机相序）——确认黄灯是灭的（485 通讯方式），"
                   "然后电机空载时短按驱动器按键学习一次，或用厂家软件学习；学成再连";
        case 0x40:
            return "驱动器拒绝了（异常码 0x40：禁止操作）——多半还在数字/模拟信号控制方式（黄灯常亮）："
                   "按住驱动器按键 1 s 松开切到 485 通讯方式（黄灯灭、绿灯闪）再试";
        case 0x02: return "驱动器拒绝了（异常码 0x02：非法数据地址）——这个固件没有这个寄存器，查手册版本";
        case 0x03: return "驱动器拒绝了（异常码 0x03：非法数据值）——写的值超出寄存器允许范围";
        default: return e.what();
    }
}

// ── 数据块 ──────────────────────────────────────────────────────────

std::string AqmdIdentity::display() const {
    char id[8];
    std::snprintf(id, sizeof id, "0x%04X", device_id);
    return (name.empty() ? std::string("设备 ") + id : name) + " v" + version;
}

double AqmdStatus::rpm(int poles, double ratio) const {
    return std::fabs(aqmd_hz_to_rpm(frequency_hz, poles, ratio));   // 反转时换向频率是负的，转速照样报正数
}

// ── 读 ──────────────────────────────────────────────────────────────

AqmdIdentity Aqmd::read_identity() {
    const auto r = mb_.read_holding_registers(aqmd_reg::kDeviceId, aqmd_reg::kIdentityCount);
    AqmdIdentity id;
    id.device_id = r[0];
    id.version = std::to_string(r[1] >> 8) + "." + std::to_string(r[1] & 0xFF);
    // 名字是 8 个寄存器、每个两个 ASCII 字符（高字节在前），'\0' 结尾
    for (int i = aqmd_reg::kNameStart; i < aqmd_reg::kNameStart + 8; i++) {
        const char hi = static_cast<char>(r[i] >> 8), lo = static_cast<char>(r[i] & 0xFF);
        if (hi == '\0') break;
        id.name += hi;
        if (lo == '\0') break;
        id.name += lo;
    }
    const auto first = id.name.find_first_not_of(" \t\r\n"), last = id.name.find_last_not_of(" \t\r\n");
    id.name = first == std::string::npos ? "" : id.name.substr(first, last - first + 1);
    id.pwm_hz = r[aqmd_reg::kPwmHz];
    id.max_output_current_a = r[aqmd_reg::kMaxOutputCurrent] * 0.01;
    return id;
}

AqmdMotorParams Aqmd::read_motor() {
    const auto r = mb_.read_holding_registers(aqmd_reg::kMotorStart, aqmd_reg::kMotorCount);
    auto at = [&r](uint16_t addr) { return r[addr - aqmd_reg::kMotorStart]; };
    AqmdMotorParams m;
    m.max_frequency_hz = at(aqmd_reg::kMaxFrequency) * 0.1;
    m.max_accel_reg = at(aqmd_reg::kMaxAccel);
    m.max_decel_reg = at(aqmd_reg::kMaxDecel);
    m.rated_current_a = at(aqmd_reg::kRatedCurrent) * 0.01;
    m.max_load_current_a = at(aqmd_reg::kMaxLoadCurrent) * 0.01;
    m.loop_algorithm = at(aqmd_reg::kLoopAlgorithm);
    m.poles = at(aqmd_reg::kPoles);
    m.gear_ratio = at(aqmd_reg::kGearRatio) * 0.1;
    m.learned = at(aqmd_reg::kLearned) == 1;
    m.setpoint_x10 = at(aqmd_reg::kSetpointX10) == 1;
    return m;
}

AqmdStatus Aqmd::read_status() {
    const auto r = mb_.read_holding_registers(aqmd_reg::kStatusStart, aqmd_reg::kStatusCount);
    auto at = [&r](uint16_t addr) { return r[addr - aqmd_reg::kStatusStart]; };
    AqmdStatus s;
    s.rpm_x10 = at(aqmd_reg::kRpmX10) == 1;
    s.pwm_percent = static_cast<int16_t>(at(aqmd_reg::kPwm)) * 0.1;
    s.current_a = at(aqmd_reg::kCurrent) * 0.01;
    s.frequency_hz = aqmd_frequency_of(at(aqmd_reg::kFrequency), s.rpm_x10);
    s.stall = at(aqmd_reg::kStall);
    s.fault = at(aqmd_reg::kFault);
    s.rpm_raw = at(aqmd_reg::kRpm);
    return s;
}

AqmdSystemParams Aqmd::read_system() {
    const auto r = mb_.read_holding_registers(aqmd_reg::kSystemStart, aqmd_reg::kSystemCount);
    auto at = [&r](uint16_t addr) { return r[addr - aqmd_reg::kSystemStart]; };
    AqmdSystemParams s;
    s.baud = at(aqmd_reg::kBaudHi) << 16 | at(aqmd_reg::kBaudLo);
    s.parity_mode = at(aqmd_reg::kParityMode);
    s.slave_address = at(aqmd_reg::kSlaveAddress);
    s.watchdog_seconds = at(aqmd_reg::kCommWatchdog) * 0.1;
    s.stall_stop_seconds = at(aqmd_reg::kStallStopTime) * 0.1;
    s.alarms_disabled = at(aqmd_reg::kAlarmsDisabled) == 1;
    s.default_speed_mode = at(aqmd_reg::kDefaultSpeedMode);
    return s;
}

Aqmd::TempVolt Aqmd::read_temp_volt() {
    const auto r = mb_.read_holding_registers(aqmd_reg::kDriverTemp, 2);
    return {static_cast<int16_t>(r[0]) * 0.1, r[1] * 0.1};
}

int Aqmd::read_learn_status() { return mb_.read_holding_registers(aqmd_reg::kLearnStatus, 1)[0]; }

// ── 写 ──────────────────────────────────────────────────────────────

void Aqmd::start_learn() { mb_.write_register(aqmd_reg::kLearnCommand, 1); }
void Aqmd::write_speed(int16_t reg) { mb_.write_register(aqmd_reg::kSpeedSetpoint, static_cast<uint16_t>(reg)); }
void Aqmd::stop(AqmdStopMode mode) { mb_.write_register(aqmd_reg::kStopCmd, static_cast<uint16_t>(mode)); }
void Aqmd::write_accel(uint16_t accel_reg, uint16_t decel_reg) { mb_.write_registers(aqmd_reg::kAccel, {accel_reg, decel_reg}); }
void Aqmd::write_poles(uint16_t poles) { mb_.write_register(aqmd_reg::kPoles, poles); }
void Aqmd::write_rated_current(uint16_t centi_amps) { mb_.write_registers(aqmd_reg::kRatedCurrent, {centi_amps, centi_amps}); }
void Aqmd::write_watchdog(uint16_t tenths) { mb_.write_register(aqmd_reg::kCommWatchdog, tenths); }
void Aqmd::write_stall_stop(uint16_t tenths) { mb_.write_register(aqmd_reg::kStallStopTime, tenths); }
void Aqmd::store_motor_params() { mb_.write_register(aqmd_reg::kStoreMotorParams, 1); }
void Aqmd::store_system_params() { mb_.write_register(aqmd_reg::kStoreSystemParams, 1); }

}  // namespace tec
