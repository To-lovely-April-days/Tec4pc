#include "feed.hpp"
#include <cstring>
#include <stdexcept>
#include <vector>

namespace tec {

namespace {
// 泵号只认 1 / 2。C# 原来是「不是 2 就当 1」，这里改成报错：
// 开泵是往釜里送料的动作，传错泵号不该悄悄落到泵 1 上。
int pump_index(int pump) {
    if (pump != 1 && pump != 2) throw std::invalid_argument("进料系统只有泵 1 / 泵 2，没有泵 " + std::to_string(pump));
    return pump;
}
}  // namespace

namespace feed_reg {

float float_of(uint16_t hi, uint16_t lo) {
    // 高字在前（ABCD）：拼成 32 位整数再按位搬成 float，不依赖本机字节序
    const uint32_t bits = (uint32_t(hi) << 16) | lo;
    float f;
    std::memcpy(&f, &bits, sizeof f);
    return f;
}

std::pair<uint16_t, uint16_t> regs_of(float v) {
    uint32_t bits;
    std::memcpy(&bits, &v, sizeof bits);
    return {uint16_t(bits >> 16), uint16_t(bits)};
}

std::string alarm_name(uint16_t reg) {
    switch (reg) {
        case Pump1PressureAlarm: return "泵 1 压力上限报警";
        case Pump2PressureAlarm: return "泵 2 压力上限报警";
        case MfcCommAlarm: return "MFC 通信故障（进料系统内部 PLC 读不到 MFC）";
        case Pump1CommAlarm: return "泵 1 通信故障（进料系统内部 PLC 读不到泵 1）";
        case Pump2CommAlarm: return "泵 2 通信故障（进料系统内部 PLC 读不到泵 2）";
        default: return "寄存器 " + std::to_string(reg);
    }
}

}  // namespace feed_reg

bool FeedSnapshot::any_alarm() const {
    return alarm_active || pump1_pressure_alarm || pump2_pressure_alarm || mfc_comm_alarm || pump1_comm_alarm || pump2_comm_alarm;
}

std::string FeedSnapshot::alarm_text() const {
    std::string s;
    auto add = [&s](const std::string& part) { if (!s.empty()) s += "、"; s += part; };
    if (pump1_pressure_alarm) add(feed_reg::alarm_name(feed_reg::Pump1PressureAlarm));
    if (pump2_pressure_alarm) add(feed_reg::alarm_name(feed_reg::Pump2PressureAlarm));
    if (mfc_comm_alarm) add(feed_reg::alarm_name(feed_reg::MfcCommAlarm));
    if (pump1_comm_alarm) add(feed_reg::alarm_name(feed_reg::Pump1CommAlarm));
    if (pump2_comm_alarm) add(feed_reg::alarm_name(feed_reg::Pump2CommAlarm));
    // 总报警位亮了、分项却一个没亮：照实说没点名，不编一个原因
    if (s.empty() && alarm_active) add("报警状态位为 1（具体哪一路没点名）");
    return s;
}

double FeedSnapshot::pump_setpoint(int pump) const { return pump_index(pump) == 2 ? pump2_setpoint : pump1_setpoint; }
double FeedSnapshot::pump_flow(int pump) const { return pump_index(pump) == 2 ? pump2_flow : pump1_flow; }
double FeedSnapshot::pump_total(int pump) const { return pump_index(pump) == 2 ? pump2_total : pump1_total; }
double FeedSnapshot::pump_pressure(int pump) const { return pump_index(pump) == 2 ? pump2_pressure : pump1_pressure; }
bool FeedSnapshot::pump_on(int pump) const { return pump_index(pump) == 2 ? pump2_on : pump1_on; }

FeedSnapshot FeedSystem::read_all() {
    const std::vector<uint16_t> r = mb_.read_holding_registers(0, feed_reg::AllCount);
    // 底层按字节数核过一遍；这里再拦一道，免得哪天底层放宽了在下面越界读
    if (r.size() < size_t(feed_reg::AllCount)) throw ModbusError("进料系统应答的寄存器数不够 49 个");
    auto F = [&r](uint16_t reg) { return double(feed_reg::float_of(r[reg], r[reg + 1])); };
    // 只有恰好等于 1 才算「是」：寄存器里出现别的值（没见过）一律按「否」，跟 C# 一样
    auto B = [&r](uint16_t reg) { return r[reg] == 1; };
    FeedSnapshot s;
    s.mfc_setpoint = F(feed_reg::MfcSetpoint); s.mfc_flow = F(feed_reg::MfcFlow); s.mfc_total = F(feed_reg::MfcTotal);
    s.pump1_setpoint = F(feed_reg::Pump1Setpoint); s.pump1_flow = F(feed_reg::Pump1Flow);
    s.pump1_total = F(feed_reg::Pump1Total); s.pump1_pressure = F(feed_reg::Pump1Pressure);
    s.pump2_setpoint = F(feed_reg::Pump2Setpoint); s.pump2_flow = F(feed_reg::Pump2Flow);
    s.pump2_total = F(feed_reg::Pump2Total); s.pump2_pressure = F(feed_reg::Pump2Pressure);
    s.mfc_range_high = F(feed_reg::MfcRangeHigh); s.mfc_range_low = F(feed_reg::MfcRangeLow);
    s.mfc_correction = F(feed_reg::MfcCorrection); s.mfc_dpt = F(feed_reg::MfcDpt);
    s.pump1_pressure_high = F(feed_reg::Pump1PressureHigh); s.pump1_pressure_low = F(feed_reg::Pump1PressureLow);
    s.pump2_pressure_high = F(feed_reg::Pump2PressureHigh); s.pump2_pressure_low = F(feed_reg::Pump2PressureLow);
    s.mfc_on = r[feed_reg::MfcOnOff] == 0;      // MFC 0 = 运行（跟泵反着）
    s.pump1_on = B(feed_reg::Pump1OnOff); s.pump2_on = B(feed_reg::Pump2OnOff);
    s.pump1_pressure_alarm = B(feed_reg::Pump1PressureAlarm); s.pump2_pressure_alarm = B(feed_reg::Pump2PressureAlarm);
    s.mfc_comm_alarm = B(feed_reg::MfcCommAlarm);
    s.pump1_comm_alarm = B(feed_reg::Pump1CommAlarm); s.pump2_comm_alarm = B(feed_reg::Pump2CommAlarm);
    s.alarm_active = B(feed_reg::AlarmStatus);
    return s;
}

double FeedSystem::read_float(uint16_t reg) {
    const auto r = mb_.read_holding_registers(reg, 2);
    if (r.size() < 2) throw ModbusError("进料系统应答的寄存器数不够 2 个");
    return feed_reg::float_of(r[0], r[1]);
}

uint16_t FeedSystem::read_int16(uint16_t reg) {
    const auto r = mb_.read_holding_registers(reg, 1);
    if (r.empty()) throw ModbusError("进料系统应答里没有寄存器");
    return r[0];
}

void FeedSystem::write_float(uint16_t reg, double value) {
    // 一个 Float 的两个寄存器必须一帧 0x10 写进去：拆成两帧 0x06 的话，中间那一刻设备里是半新半旧的一个数
    const auto [hi, lo] = feed_reg::regs_of(float(value));
    mb_.write_registers(reg, {hi, lo});
}

void FeedSystem::write_int16(uint16_t reg, uint16_t value) { mb_.write_register(reg, value); }

void FeedSystem::set_pump(int pump, bool on) {
    write_int16(pump_index(pump) == 2 ? feed_reg::Pump2OnOff : feed_reg::Pump1OnOff, on ? 1 : 0);
}

void FeedSystem::set_pump_setpoint(int pump, double ml_per_min) {
    write_float(pump_index(pump) == 2 ? feed_reg::Pump2Setpoint : feed_reg::Pump1Setpoint, ml_per_min);
}

void FeedSystem::set_mfc(bool on) { write_int16(feed_reg::MfcOnOff, on ? 0 : 1); }   // 0 运行、1 停止

void FeedSystem::set_mfc_setpoint(double l_per_min) { write_float(feed_reg::MfcSetpoint, l_per_min); }

void FeedSystem::reset_alarms() { write_int16(feed_reg::AlarmReset, 1); }

void FeedSystem::silence_alarms() { write_int16(feed_reg::AlarmSilence, 1); }

}  // namespace tec
