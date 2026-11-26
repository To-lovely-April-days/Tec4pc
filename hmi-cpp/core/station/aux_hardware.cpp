#include "aux_hardware.hpp"
#include "../../drivers/aqmd.hpp"
#include "../../drivers/bht_ph.hpp"
#include "../../drivers/feed.hpp"
#include "../util/text_error.hpp"
#include <cmath>
#include <stdexcept>

namespace tec {

namespace {
// 读一次：通了就把闸放开，读不到就记一笔（连着几次读不到，下面那几次就直接跳过，不再白等超时）
template <class F>
auto read_guarded(Flaky& gate, F&& f) -> decltype(f()) {
    try {
        auto v = f();
        gate.ok();
        return v;
    } catch (const std::exception& e) {
        gate.failed(e.what());
        throw;
    }
}
}  // namespace

RealAuxHardware::RealAuxHardware(Aqmd* stir_a, Aqmd* stir_b, BhtPh* ph_a, BhtPh* ph_b, FeedSystem* feed, const StirMotor& ma, const StirMotor& mb)
    : stir_{stir_a, stir_b}, ph_{ph_a, ph_b}, feed_(feed), motor_{ma, mb} {}

void RealAuxHardware::set_stir(int ch, double rpm) {
    Aqmd* d = stir_[ch];
    if (!d) throw std::runtime_error("这个通道没有接搅拌驱动器");
    if (rpm <= 0) { d->stop(); return; }
    const StirMotor& m = motor_[ch];
    if (max_accel_reg_[ch] < 0) {          // 第一次用：读一下驱动器允许的最大加减速和设定值的单位，之后不再读
        const AqmdMotorParams mp = d->read_motor();
        max_accel_reg_[ch] = mp.max_accel_reg > 0 ? mp.max_accel_reg : 65535;
        x10_[ch] = mp.setpoint_x10;
    }
    const double hz = aqmd_rpm_to_hz(rpm, m.poles, m.gear_ratio);
    // 加减速要先于目标写：先写目标的话，驱动器会按上一次留下的加减速冲上去（原来程序里记着的经验）
    const uint16_t acc = aqmd_accel_reg(hz / (m.ramp_s > 0.1 ? m.ramp_s : 0.1), static_cast<uint16_t>(max_accel_reg_[ch]));
    d->write_accel(acc, acc);
    d->write_speed(aqmd_speed_reg(hz * (m.direction < 0 ? -1 : 1), x10_[ch]));
}

StirReading RealAuxHardware::read_stir(int ch) {
    StirReading r;
    Aqmd* d = stir_[ch];
    if (!d) return r;
    if (gate_stir_[ch].resting()) throw TextError(gate_stir_[ch].why());
    const AqmdStatus s = read_guarded(gate_stir_[ch], [&] { return d->read_status(); });
    r.ok = true;
    const double rpm = s.rpm(motor_[ch].poles, motor_[ch].gear_ratio);
    r.rpm = std::isnan(rpm) ? 0.0 : rpm;
    r.current_a = s.current_a;
    r.fault = s.fault;
    if (s.fault != 0) r.fault_text = aqmd_fault_text(s.fault);
    else if (s.stall != 0) r.fault_text = aqmd_stall_text(s.stall);
    return r;
}

PhReading RealAuxHardware::read_ph(int ch) {
    PhReading r;
    BhtPh* d = ph_[ch];
    if (!d) return r;
    if (gate_ph_[ch].resting()) throw TextError(gate_ph_[ch].why());
    const BhtLive v = read_guarded(gate_ph_[ch], [&] { return d->read_live(); });
    r.ph = v.ph;
    if (v.temp_status == 0) r.temp_c = v.temp_c;     // 温度状态不正常时这个温度不可信，不给
    return r;
}

FeedReading RealAuxHardware::read_feed() {
    FeedReading r;
    if (!feed_) return r;
    if (gate_feed_.resting()) throw TextError(gate_feed_.why());
    const FeedSnapshot s = read_guarded(gate_feed_, [&] { return feed_->read_all(); });
    r.ok = true;
    for (int p = 1; p <= 2; p++) { r.total_ml[p - 1] = s.pump_total(p); r.flow_ml_min[p - 1] = s.pump_flow(p); r.on[p - 1] = s.pump_on(p); }
    if (s.any_alarm()) r.alarm_text = s.alarm_text();
    return r;
}

void RealAuxHardware::set_pump_rate(int pump, double ml_min) {
    if (!feed_) throw std::runtime_error("没有接进料系统");
    feed_->set_pump_setpoint(pump, ml_min);
}

void RealAuxHardware::set_pump(int pump, bool on) {
    if (!feed_) throw std::runtime_error("没有接进料系统");
    feed_->set_pump(pump, on);
}

}  // namespace tec
