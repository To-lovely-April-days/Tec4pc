// 内环：夹套温度的 PID，加上双向挡的输出映射（正 = 加热棒，负 = TEC 制冷）。
// 独立调用 step() 时保持原双向映射不变（tests/golden_inner.cpp 逐拍对拍）；
// 串级三阶段协同调用 step_coordinated() 时，修正高温区 r < 1 的 TEC 出力下限，并支持 C→H 稳态维持功率无扰预置。
#pragma once
#include "gain_table.hpp"

namespace tec {

class Pid {
public:
    Pid(double out_min, double out_max, double d_filter_s) : out_min_(out_min), out_max_(out_max), d_filter_s_(d_filter_s < 0 ? 0 : d_filter_s) {}
    void set_gains(double kp, double ki, double kd) { kp_ = kp; ki_ = ki; kd_ = kd; }
    void set_output_limits(double lo, double hi);
    void reset();
    void reset_derivative();              // 扳继电器、输出关了一会儿之后恢复时用：别把这段时间的温度变化算成一个大微分
    void preset_integral(double v);
    void set_integral_raw(double v) { i_ = v; last_i = v; }
    double integral() const { return i_; }
    double d_filt() const { return d_; }
    double out_min() const { return out_min_; }
    double out_max() const { return out_max_; }
    double update(double setpoint, double measurement, double dt, bool freeze_integral = false);
    void track_pv(double measurement, double dt);
    double update_coordinated(double setpoint, double measurement, double dt, double lo, double hi, double i_floor,
                              bool in_brake, bool side_is_tec, double& p_plus_i_out);
    double last_p = 0, last_i = 0, last_d = 0;

private:
    double kp_ = 0, ki_ = 0, kd_ = 0, out_min_, out_max_, d_filter_s_;
    double i_ = 0, last_pv_ = 0, d_ = 0;
    bool first_ = true;
};

struct InnerOut {
    double pid_out = 0;       // PID 那一侧的量（没乘加热比）
    double heater_pct = 0;    // 继电器在加热棒侧时该给多少（%）
    double tec_pct = 0;       // 继电器在 TEC 侧时该给多少（%）
    double signed_duty = 0;   // 带符号的占空比：正 = 加热棒，负 = TEC（已乘加热比）
    bool sat_high = false, sat_low = false;
};

class InnerLoop {
public:
    explicit InnerLoop(const GainTable& table, double max_duty = 90.0, double d_filter_s = 8.0)
        : table_(table), max_duty_(max_duty), pid_(-max_duty, max_duty, d_filter_s) {}
    // heat_only：要 TEC 但冷却水还没通上 / 冷水机还在最短间隔里——这段时间回路按只加热算，PID 下限钳在 0
    InnerOut step(double jacket_sp, double tj, double dt, bool heat_only);

    // EasyMax 串级阶段协同内环步进
    InnerOut step_coordinated(double jacket_sp, double tj, double dt, bool output_enabled,
                              bool heat_only, bool in_boost, bool in_brake, bool side_is_tec);
    void on_side_switched(bool to_tec, double jacket_sp, bool valve_open);

    Pid& pid() { return pid_; }
    double ratio() const { return ratio_; }
    double d_filt() const { return pid_.d_filt(); }
    double last_p_plus_i() const { return last_p_plus_i_; }

private:
    const GainTable& table_;
    double max_duty_;
    Pid pid_;
    double ratio_ = 1.0;
    double last_p_plus_i_ = 0.0;
};

}  // namespace tec
