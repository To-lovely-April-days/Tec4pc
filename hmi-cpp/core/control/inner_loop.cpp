#include "inner_loop.hpp"
#include <algorithm>
#include <cmath>

namespace tec {

void Pid::set_output_limits(double lo, double hi) {
    out_min_ = lo; out_max_ = hi;
    i_ = std::min(hi, std::max(lo, i_));
}
void Pid::reset() { i_ = 0; d_ = 0; first_ = true; last_p = last_i = last_d = 0; }
void Pid::reset_derivative() { first_ = true; d_ = 0; last_d = 0; }
void Pid::preset_integral(double v) { i_ = std::min(out_max_, std::max(out_min_, v)); last_i = i_; }

void Pid::track_pv(double measurement, double dt) {
    if (dt <= 0 || std::isnan(dt)) dt = 1e-3;
    if (first_) { last_pv_ = measurement; d_ = 0; first_ = false; }
    const double raw_d = (measurement - last_pv_) / dt;
    const double alpha = d_filter_s_ <= 0 ? 1.0 : dt / (d_filter_s_ + dt);
    d_ += alpha * (raw_d - d_);
    last_pv_ = measurement;
}

double Pid::update(double setpoint, double measurement, double dt, bool freeze_integral) {
    if (dt <= 0 || std::isnan(dt)) dt = 1e-3;
    const double error = setpoint - measurement;
    if (first_) { last_pv_ = measurement; d_ = 0; first_ = false; }
    // 微分看的是测量值不是偏差：改目标那一下不会踢一脚
    const double raw_d = (measurement - last_pv_) / dt;
    const double alpha = d_filter_s_ <= 0 ? 1.0 : dt / (d_filter_s_ + dt);
    d_ += alpha * (raw_d - d_);
    last_pv_ = measurement;
    const double p = kp_ * error;
    const double d = -kd_ * d_;
    const double unsat = p + i_ + d;
    // 条件积分：输出已经顶在限幅上、偏差还在把它往外推的时候不再积（不然回头要很久才退得出来）
    const bool pushing_high = unsat >= out_max_ && error > 0;
    const bool pushing_low = unsat <= out_min_ && error < 0;
    if (!freeze_integral && !pushing_high && !pushing_low) i_ = std::min(out_max_, std::max(out_min_, i_ + ki_ * error * dt));
    const double out = std::min(out_max_, std::max(out_min_, p + i_ + d));
    last_p = p; last_i = i_; last_d = d;
    return out;
}

double Pid::update_coordinated(double setpoint, double measurement, double dt, double lo, double hi, double i_floor,
                               bool in_brake, bool side_is_tec, double& p_plus_i_out) {
    track_pv(measurement, dt);
    out_min_ = lo;
    out_max_ = hi;
    i_ = std::min(out_max_, std::max(i_floor, i_));

    const double err = setpoint - measurement;
    const double p = kp_ * err;
    const double d_scale = (in_brake && err < -3.5) ? 0.30 : 1.0;
    const double d = -kd_ * d_scale * d_;
    const double unsat = p + i_ + d;
    const bool pushing_hi = unsat >= out_max_ && err > 0;
    const bool pushing_lo = unsat <= out_min_ && err < 0;
    const bool coasting_on_tec = (side_is_tec && unsat > 0 && err < 0);
    const bool coasting_on_heater = (!side_is_tec && unsat < 0 && err > -2.5 && !in_brake);
    if (!pushing_hi && !pushing_lo && !coasting_on_tec && !coasting_on_heater) {
        i_ = std::min(out_max_, std::max(i_floor, i_ + ki_ * err * dt));
    }
    const double out = std::min(out_max_, std::max(out_min_, p + i_ + d));
    last_p = p; last_i = i_; last_d = d;
    p_plus_i_out = p + i_;
    return out;
}

InnerOut InnerLoop::step(double jacket_sp, double tj, double dt, bool heat_only) {
    const InnerGains g = table_.gains_at(jacket_sp);
    pid_.set_gains(g.kp, g.ki, g.kd);
    double r = table_.heat_ratio_at(jacket_sp).value_or(1.0);
    if (!(r > 0)) r = 1.0;
    ratio_ = r;
    // TEC 那半轴的输出要乘加热比，所以 PID 的下限按比例收小，免得乘完超过 100 %
    const double floor = heat_only ? 0.0 : -max_duty_ / std::max(1.0, r);
    if (std::fabs(pid_.out_min() - floor) > 1e-9 || pid_.out_max() != max_duty_) pid_.set_output_limits(floor, max_duty_);
    const double out = pid_.update(jacket_sp, tj, dt);
    InnerOut o;
    o.pid_out = out;
    o.sat_high = out >= pid_.out_max() - 1e-6;
    o.sat_low = out <= pid_.out_min() + 1e-6;
    if (out > 0) {
        o.heater_pct = out; o.signed_duty = out;
    } else if (out < 0 && !heat_only) {
        const double tec = std::min(max_duty_, -out * r);
        o.tec_pct = tec; o.signed_duty = -tec;
    }
    return o;
}

void InnerLoop::on_side_switched(bool to_tec, double jacket_sp, bool valve_open) {
    if (to_tec) {
        if (pid_.integral() > 0.0) pid_.set_integral_raw(0.0);
    } else {
        const double water_tail_comp = valve_open ? std::min(9.5, 0.28 * std::max(0.0, jacket_sp - 23.0)) : 0.0;
        const double est_hold = std::max(0.0, std::min(42.0, 0.38 * (jacket_sp - 21.0) + water_tail_comp));
        if (pid_.integral() < est_hold) pid_.set_integral_raw(est_hold);
    }
}

InnerOut InnerLoop::step_coordinated(double jacket_sp, double tj, double dt, bool output_enabled,
                                     bool heat_only, bool in_boost, bool in_brake, bool side_is_tec) {
    if (!output_enabled) {
        pid_.track_pv(tj, dt);
        return {};
    }
    const InnerGains g = table_.gains_at(jacket_sp);
    pid_.set_gains(g.kp, g.ki, g.kd);
    double r = table_.heat_ratio_at(jacket_sp).value_or(1.0);
    if (!(r > 0)) r = 1.0;
    ratio_ = r;

    const double r_floor = std::max(0.15, r);
    const double floor = (heat_only || in_boost) ? 0.0 : -max_duty_ / r_floor;
    const double i_floor = (heat_only || in_boost) ? 0.0 : -max_duty_ / std::max(1.0, r);
    const double out = pid_.update_coordinated(jacket_sp, tj, dt, floor, max_duty_, i_floor, in_brake, side_is_tec, last_p_plus_i_);

    InnerOut o;
    o.pid_out = out;
    o.sat_high = out >= pid_.out_max() - 1e-6;
    o.sat_low = out <= pid_.out_min() + 1e-6;
    if (out > 0) {
        o.heater_pct = out;
        o.signed_duty = out;
    } else if (out < 0 && !heat_only && !in_boost) {
        const double tec = std::min(max_duty_, -out * r);
        o.tec_pct = tec;
        o.signed_duty = -tec;
    }
    return o;
}

}  // namespace tec
