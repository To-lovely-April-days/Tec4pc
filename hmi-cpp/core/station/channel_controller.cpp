#include "channel_controller.hpp"
#include <algorithm>
#include <cmath>

namespace tec {

ChannelController::ChannelController(const GainTable& table, const ChannelConfig& cfg) : table_(table), cfg_(cfg) {
    outer_every_ = std::max(1, static_cast<int>(std::lround(cfg_.outer_period_s / cfg_.inner_period_s)));
}

void ChannelController::begin(ControlMode mode) {
    mode_ = mode;
    k_ = 0;
    OuterParams op = cfg_.outer;
    if (op.fast.enabled && !op.fast.bias_start) {
        op.fast.bias_start = [this](double t) { return table_.steady_bias_at(t).value_or(0.0); };
    }
    // 每次开控都从干净的状态起：上一炉认出来的东西不带到这一炉（料可能换了）
    outer_ = std::make_unique<OuterLoop>(op);
    inner_ = std::make_unique<InnerLoop>(table_, cfg_.max_duty);
    sw_ = std::make_unique<HeatSourceSwitch>(cfg_.sw);
    tj_sp_ = pid_out_ = demand_ = 0;
    inner_sat_low_ = false;
    tr_last_.reset();
    sp_rate_ = 0; ramp_armed_ = false; ramp_done_ = true;
    segs_.clear(); seg_ = 0; seg_left_s_ = 0;
}

void ChannelController::start(ControlMode mode, double setpoint) {
    Target t; t.value = setpoint;
    start(mode, t);
}

void ChannelController::start(ControlMode mode, const Target& target) {
    begin(mode);
    target_ = target;
    sp_ = sp_now_ = target.value;
    // 斜坡要从「开控那一拍的实测温度」起，这时候还没有读数：先记下要走斜坡，第一拍拿到读数再定起点
    ramp_armed_ = target.arrive != Arrive::Fast;
    ramp_done_ = !ramp_armed_;
}

void ChannelController::start_reflux(double delta_k) {
    begin(ControlMode::Reflux);
    reflux_delta_ = delta_k;
    sp_ = sp_now_ = delta_k;
}

void ChannelController::start_profile(const std::vector<ProfileSegment>& segs) {
    begin(ControlMode::Crystal);
    segs_ = segs;
    seg_ = 0;
    sp_ = sp_now_ = segs.empty() ? 0.0 : segs[0].target;
    prof_from_ = sp_;         // 第一拍读数来之前先顶一个数，免得往外报的是上一条曲线的起点
    ramp_armed_ = true;       // 同样等第一拍的读数定曲线起点
}

void ChannelController::set_setpoint(double setpoint) {
    sp_ = sp_now_ = setpoint;
    target_.value = setpoint; target_.arrive = Arrive::Fast;
    sp_rate_ = 0; ramp_armed_ = false; ramp_done_ = true;
}

// 算这一拍的目标（斜坡 / 分段曲线上的当前值）和目标的变化率。pv = 被控量这一拍的读数
void ChannelController::advance_trajectory(double t, double pv) {
    if (mode_ == ControlMode::Crystal) {
        if (ramp_armed_) { ramp_armed_ = false; seg_ = 0; seg_t0_ = t; seg_from_ = pv; prof_from_ = pv; }
        // 一段走完就进下一段；下一段从上一段的目标起，不是从实测起——曲线本身是连续的，实测跟不上是控制的事
        while (seg_ < static_cast<int>(segs_.size()) && t - seg_t0_ >= segs_[seg_].minutes * 60.0) {
            seg_t0_ += segs_[seg_].minutes * 60.0; seg_from_ = segs_[seg_].target; seg_++;
        }
        if (seg_ >= static_cast<int>(segs_.size())) {     // 全部走完：停在最后一段的目标上保温
            sp_ = sp_now_ = segs_.empty() ? pv : segs_.back().target; sp_rate_ = 0; seg_left_s_ = 0;
            return;
        }
        const ProfileSegment& g = segs_[seg_];
        const double dur = g.minutes * 60.0, el = t - seg_t0_;
        sp_ = g.target;
        sp_rate_ = dur > 0 ? (g.target - seg_from_) / dur : 0.0;
        sp_now_ = seg_from_ + sp_rate_ * el;
        seg_left_s_ = dur - el;
        return;
    }
    if (ramp_armed_) {
        ramp_armed_ = false; ramp_from_ = pv; ramp_t0_ = t;
        const double dist = target_.value - pv;
        double rate = 0;       // K/s，带符号
        if (target_.arrive == Arrive::Rate && target_.rate_k_per_min > 0) rate = std::copysign(target_.rate_k_per_min / 60.0, dist);
        else if (target_.arrive == Arrive::Duration && target_.duration_min > 0) rate = dist / (target_.duration_min * 60.0);
        ramp_rate_ = rate;
        ramp_done_ = rate == 0.0;
    }
    if (ramp_done_) { sp_now_ = sp_; sp_rate_ = 0; return; }
    const double v = ramp_from_ + ramp_rate_ * (t - ramp_t0_);
    if ((ramp_rate_ > 0 && v >= target_.value) || (ramp_rate_ < 0 && v <= target_.value)) { sp_now_ = sp_; sp_rate_ = 0; ramp_done_ = true; }
    else { sp_now_ = v; sp_rate_ = ramp_rate_; }
}

void ChannelController::stop() { mode_ = ControlMode::Off; }

ChannelCommand ChannelController::tick(double tj, std::optional<double> tr) {
    ChannelCommand cmd;
    if (mode_ == ControlMode::Off || !inner_) return cmd;       // 不控温：输出关、占空比 0。继电器和阀由上层按「停控」处理
    const double dt = cfg_.inner_period_s;
    const double t = k_ * dt;
    if (tr) tr_last_ = tr;

    const bool cascade = mode_ == ControlMode::Reactor || mode_ == ControlMode::Crystal;
    const bool easymax = cascade && cfg_.outer.fast.enabled && cfg_.outer.fast.boost_max > 0.0;

    if (easymax) {
        const double tr_val = tr_last_.value_or(tj);
        advance_trajectory(t, tr_val);
        const bool is_ramp = (sp_rate_ != 0.0) || (sp_now_ != sp_);
        if (k_ == 0) {
            sw_->start_coordinated(t, sp_, tr_val, is_ramp);
        }
        const int sw_done = sw_->pre_coordinated(t);
        if (sw_done >= 0) {
            inner_->on_side_switched(sw_done == 1, tj_sp_, sw_->valve_open());
        }

        if (k_ % outer_every_ == 0) {
            OuterObs o;
            o.t = t; o.dt = cfg_.outer_period_s; o.sp = sp_now_; o.tr = tr_last_; o.tj = tj; o.first = k_ == 0;
            if (is_ramp) { o.sp_rate = sp_rate_; o.sp_final = sp_; }
            o.cooling_side = sw_->side() == HeatSide::Tec;
            o.inner_sat_low = inner_sat_low_;
            o.tec_withheld = sw_->heat_only();
            o.output_on = sw_->output_enabled();
            o.heater_duty_frac = (sw_->output_enabled() && sw_->side() == HeatSide::Heater && demand_ > 0.0)
                                     ? std::max(0.0, demand_ / cfg_.max_duty)
                                     : 0.0;
            o.tj_d_filt = inner_->d_filt();
            o.in_lead = sw_->in_lead();
            o.lead_until = sw_->lead_until();
            o.chiller_state = static_cast<int>(sw_->chiller());
            o.chiller_since = sw_->chiller_since();
            if (o.first) outer_->reset(o);
            const OuterOut out = outer_->step(o);
            if (out.alarm) cmd.notes.push_back(*out.alarm);
            tj_sp_ = std::min(cfg_.jacket_sp_max, std::max(cfg_.jacket_sp_min, out.tj_sp));
        }

        const FastStage stg = outer_->stage();
        const bool in_boost = stg == FastStage::Boost;
        const bool in_brake = stg == FastStage::Brake;

        double heater = 0, tec = 0;
        const InnerOut io = inner_->step_coordinated(
            tj_sp_, tj, dt, sw_->output_enabled(), sw_->heat_only(), in_boost, in_brake, sw_->side() == HeatSide::Tec);
        if (sw_->output_enabled()) {
            pid_out_ = io.pid_out; demand_ = io.signed_duty; heater = io.heater_pct; tec = io.tec_pct;
            inner_sat_low_ = io.sat_low;
        } else {
            demand_ = 0;
        }

        const double tw_eff = cfg_.outer.tw * std::max(0.70, std::min(1.35, std::sqrt(outer_->tau() / std::max(1.0, cfg_.outer.tau))));
        sw_->post_coordinated(t, sp_now_, tr_val, tj, tj_sp_, outer_->xr(), outer_->w2(), outer_->b_ctrl(), tw_eff, outer_->tau(),
                              inner_->d_filt(), inner_->last_p_plus_i(), demand_, in_boost, in_brake);
        for (auto& e : sw_->take_events()) cmd.notes.push_back(e.text);

        const bool on = sw_->output_enabled();
        const double h = on && sw_->side() == HeatSide::Heater ? heater : 0.0;
        const double c = on && sw_->side() == HeatSide::Tec ? tec : 0.0;
        cmd.device_duty = c > 0 ? c : -h;
        cmd.output_on = on;
        cmd.side = sw_->side();
        cmd.valve_open = sw_->valve_open();
        cmd.chiller = sw_->chiller();
        cmd.jacket_sp = tj_sp_;
        k_++;
        return cmd;
    }

    if (k_ == 0) {
        // 起步合哪一侧看被控量：釜内控温看釜内，夹套控温看夹套
        const double pv = mode_ == ControlMode::Jacket ? tj : tr_last_.value_or(tj);
        if (mode_ == ControlMode::Reflux) sw_->start(t, tj, tj);       // 蒸回流总是加热，起步合加热棒
        else sw_->start(t, sp_, pv);
    }
    const bool reset_d = sw_->pre(t);
    if (mode_ != ControlMode::Reflux) advance_trajectory(t, mode_ == ControlMode::Jacket ? tj : tr_last_.value_or(tj));
    if (cascade) {
        if (k_ % outer_every_ == 0) {
            OuterObs o;
            o.t = t; o.dt = cfg_.outer_period_s; o.sp = sp_now_; o.tr = tr_last_; o.tj = tj; o.first = k_ == 0;
            // 走斜坡时把目标的变化率和终点告诉外环：它按变化率提前给夹套加量，快到终点时按终点收住
            if (sp_rate_ != 0.0 || sp_now_ != sp_) { o.sp_rate = sp_rate_; o.sp_final = sp_; }
            o.cooling_side = sw_->side() == HeatSide::Tec;
            o.inner_sat_low = inner_sat_low_;
            o.tec_withheld = sw_->heat_only();
            o.output_on = sw_->output_enabled();
            if (o.first) outer_->reset(o);
            const OuterOut out = outer_->step(o);
            if (out.alarm) cmd.notes.push_back(*out.alarm);
            tj_sp_ = std::min(cfg_.jacket_sp_max, std::max(cfg_.jacket_sp_min, out.tj_sp));
        }
    } else if (mode_ == ControlMode::Reflux) {
        // 釜内读不到就按夹套不动处理：不知道釜内多少度，不能再往上加
        const double base = tr_last_.value_or(tj - reflux_delta_);
        tj_sp_ = std::min(cfg_.jacket_sp_max, std::max(cfg_.jacket_sp_min, base + reflux_delta_));
    } else {
        tj_sp_ = std::min(cfg_.jacket_sp_max, std::max(cfg_.jacket_sp_min, sp_now_));
    }
    double heater = 0, tec = 0;
    if (sw_->output_enabled()) {
        if (reset_d) inner_->pid().reset_derivative();
        const InnerOut io = inner_->step(tj_sp_, tj, dt, sw_->heat_only());
        pid_out_ = io.pid_out; demand_ = io.signed_duty; heater = io.heater_pct; tec = io.tec_pct;
        inner_sat_low_ = io.sat_low;
    } else {
        demand_ = 0;      // 正在扳继电器：这一拍不算 PID（积分留着），输出给 0
    }
    sw_->post(t, demand_);
    for (auto& e : sw_->take_events()) cmd.notes.push_back(e.text);
    // 继电器在哪一侧就只出哪一侧的力：功率符号已经翻了但继电器还没扳过去的那几秒，另一侧吃 0
    const bool on = sw_->output_enabled();
    const double h = on && sw_->side() == HeatSide::Heater ? heater : 0.0;
    const double c = on && sw_->side() == HeatSide::Tec ? tec : 0.0;
    cmd.device_duty = c > 0 ? c : -h;     // 设备上：正 = TEC 制冷，负 = 加热棒
    cmd.output_on = on;
    cmd.side = sw_->side();
    cmd.valve_open = sw_->valve_open();
    cmd.chiller = sw_->chiller();
    cmd.jacket_sp = tj_sp_;
    k_++;
    return cmd;
}

}  // namespace tec
