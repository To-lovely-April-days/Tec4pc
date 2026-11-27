#include "heat_source_switch.hpp"
#include <algorithm>
#include <cmath>

namespace tec {

void HeatSourceSwitch::start(double t, double sp, double tr) {
    if (sp < tr - p_.band_k) {             // 起步就是降温：先通水，等完直接合 TEC
        ensure_water(t);
        phase_ = Phase::Lead; seq_to_ = HeatSide::Tec; seq_until_ = lead_until_; seq_from_hold_ = false; seq_fresh_ = true;
    }
}

void HeatSourceSwitch::start_coordinated(double t, double sp, double tr, bool is_ramp) {
    if (!is_ramp && sp < tr - 1.5) {
        ensure_water(t);
        phase_ = Phase::Lead; seq_to_ = HeatSide::Tec; seq_until_ = lead_until_; seq_from_hold_ = false; seq_fresh_ = true;
    }
}

void HeatSourceSwitch::tick_water(double t) {
    if (chiller_ == ChillerState::Starting && t - chiller_since_ >= p_.chiller_start_delay_s) chiller_ = ChillerState::Running;
    else if (chiller_ == ChillerState::Stopping && t - chiller_since_ >= p_.chiller_stop_delay_s) chiller_ = ChillerState::Idle;
    if (water_closing_ && chiller_ == ChillerState::Idle) {
        water_closing_ = false;
        valve_open_ = false;
        events_.push_back({t, "冷却水阀关闭"});
    }
}

bool HeatSourceSwitch::cold_start_needed() const {
    return !(valve_open_ && !water_closing_) && !(water_closing_ && chiller_ != ChillerState::Idle);
}

void HeatSourceSwitch::open_water(double t) {
    if (!valve_open_) valve_opens_++;
    water_closing_ = false;
    valve_open_ = true;
    if (chiller_ == ChillerState::Idle) {
        chiller_ = p_.chiller_start_delay_s > 0 ? ChillerState::Starting : ChillerState::Running;
        chiller_since_ = t;
        last_cold_start_ = t; have_cold_start_ = true;
    } else if (chiller_ == ChillerState::Stopping) {
        chiller_ = ChillerState::Running;   // 撤销关水：冷水机还在转，直接接着用，不算冷启动
    }
}

void HeatSourceSwitch::ensure_water(double t) {
    if (valve_open_ && !water_closing_) {
        const double left = lead_until_ - t;
        if (!(left > 0 && left <= p_.water_lead_s)) lead_until_ = t;   // 水早就通着：不用等
        return;
    }
    lead_until_ = t + p_.water_lead_s;
    open_water(t);
    events_.push_back({t, "冷却水阀打开（接 TEC 前通水）"});
}

void HeatSourceSwitch::release_water(double t) {
    if (!valve_open_ || water_closing_) return;
    water_closing_ = true;
    if (chiller_ == ChillerState::Starting || chiller_ == ChillerState::Running) {
        chiller_ = p_.chiller_stop_delay_s > 0 ? ChillerState::Stopping : ChillerState::Idle;
        chiller_since_ = t;
    }
}

bool HeatSourceSwitch::pre(double t) {
    tick_water(t);
    if (phase_ == Phase::Switch && t >= seq_until_) {
        side_ = seq_to_;
        enabled_ = true;
        flips_++;
        events_.push_back({t, side_ == HeatSide::Tec ? "切到 TEC 制冷" : "切回加热棒"});
        if (side_ == HeatSide::Tec) { heat_only_ = false; have_ep_ = seq_from_hold_; ep_since_ = t; }
        else have_ep_ = false;
        sign_side_ = -1;
        phase_ = Phase::None;
        return true;
    }
    return false;
}

int HeatSourceSwitch::pre_coordinated(double t) {
    tick_water(t);
    if (phase_ == Phase::Switch && t >= seq_until_) {
        side_ = seq_to_;
        enabled_ = true;
        flips_++;
        events_.push_back({t, side_ == HeatSide::Tec ? "切到 TEC 制冷" : "切回加热棒"});
        if (side_ == HeatSide::Tec) {
            heat_only_ = false;
            have_ep_ = false;
        } else {
            have_ep_ = false;
            have_to_heater_ = true;
            last_to_heater_t_ = t;
        }
        sign_side_ = -1;
        phase_ = Phase::None;
        return side_ == HeatSide::Tec ? 1 : 0;
    }
    return -1;
}

int HeatSourceSwitch::signed_side(double t, double d) {
    const int s = std::fabs(d) <= p_.sign_deadband_pct ? -1 : (d > 0 ? 0 : 1);
    const int cur = side_ == HeatSide::Heater ? 0 : 1;
    if (s == -1 || s == cur) { sign_side_ = -1; return s; }
    if (sign_side_ != s) { sign_side_ = s; sign_since_ = t; }
    return t - sign_since_ >= p_.sign_dwell_s - 1e-9 ? s : -1;
}

void HeatSourceSwitch::begin_to_tec(double t, bool from_hold) {
    ensure_water(t);
    phase_ = Phase::Lead; seq_to_ = HeatSide::Tec; seq_until_ = lead_until_; seq_from_hold_ = from_hold; seq_fresh_ = false;
}

void HeatSourceSwitch::begin_switch(double t, HeatSide to) {
    enabled_ = false;
    const bool fh = phase_ != Phase::None ? seq_from_hold_ : false;
    phase_ = Phase::Switch; seq_to_ = to; seq_until_ = t + p_.switch_s; seq_from_hold_ = fh; seq_fresh_ = false;
    if (to == HeatSide::Heater) release_water(t);
}

bool HeatSourceSwitch::gate_tec(double t) {
    if (cold_start_needed() && have_cold_start_ && t - last_cold_start_ < p_.chiller_min_gap_s) { hold(t); return false; }
    return true;
}

void HeatSourceSwitch::hold(double t) {
    holding_ = true;
    heat_only_ = true;
    have_heat_since_ = false;
    events_.push_back({t, "冷水机刚启动过，等够最短间隔再接 TEC"});
}

void HeatSourceSwitch::post(double t, double demand) {
    if (phase_ != Phase::None) {
        if (phase_ == Phase::Lead) {
            const bool forced = seq_from_hold_ || seq_fresh_;
            const int by = forced ? 1 : signed_side(t, demand);
            if (t >= seq_until_) {
                if (forced || by == 1) begin_switch(t, HeatSide::Tec);
                else {
                    phase_ = Phase::None;
                    events_.push_back({t, "通水等完功率已不指向 TEC：不切，水收回"});
                    release_water(t);
                }
            }
        }
        return;
    }
    if (side_ == HeatSide::Heater) {
        if (holding_) {
            if (demand > p_.sign_deadband_pct) {
                if (!have_heat_since_) { have_heat_since_ = true; heat_since_ = t; }
                if (t - heat_since_ >= p_.sign_dwell_s - 1e-9) {
                    holding_ = false; heat_only_ = false; sign_side_ = -1;
                    return;
                }
            } else {
                have_heat_since_ = false;
            }
            if (cold_start_needed() && have_cold_start_ && t - last_cold_start_ < p_.chiller_min_gap_s) return;
            holding_ = false;
            begin_to_tec(t, true);
            return;
        }
        if (signed_side(t, demand) == 1 && gate_tec(t)) begin_to_tec(t, false);
    } else {
        if (have_ep_ && p_.min_cool_after_hold_s > 0 && t - ep_since_ < std::max(1.0, p_.min_cool_after_hold_s)) { sign_side_ = -1; return; }
        if (signed_side(t, demand) == 0) begin_switch(t, HeatSide::Heater);
    }
}

void HeatSourceSwitch::post_coordinated(double t, double sp_cur, double tr_val, double tj, double tj_sp,
                                        double xr, double w2, double b_ctrl, double tw, double tau,
                                        double d_filt, double last_p_plus_i, double demand,
                                        bool in_boost, bool in_brake) {
    const double err_j = tj_sp - tj;
    const double pred_110s_tr = xr + 110.0 * std::max(0.0, (w2 - xr - b_ctrl) / std::max(1.0, tau));
    const bool proactive_exo_cool = (!in_boost) && (tr_val > sp_cur - 0.30) && (pred_110s_tr > sp_cur + 0.04) && (err_j < -1.2);
    const bool active_cool_step = (tr_val > sp_cur + 0.45) && (tj_sp < sp_cur + b_ctrl - 0.70);

    if (phase_ != Phase::None) {
        if (phase_ == Phase::Lead) {
            const bool want_cool = in_brake || proactive_exo_cool || active_cool_step || seq_fresh_ || seq_from_hold_ ||
                                   (!in_boost && err_j < -1.5 && demand < -2.0);
            if (t >= seq_until_) {
                if (want_cool) {
                    begin_switch(t, HeatSide::Tec);
                } else {
                    phase_ = Phase::None;
                    events_.push_back({t, "通水等完功率已不指向 TEC：不切，水收回"});
                    release_water(t);
                }
            }
        }
        return;
    }

    if (side_ == HeatSide::Heater) {
        bool want_tec = false;
        double dwell_req = 3.0;
        if (in_boost) {
            want_tec = false;
            dwell_req = 999.0;
        } else if (in_brake) {
            want_tec = true;
            dwell_req = 0.5;
        } else if (tj_sp < 23.0) {
            want_tec = (err_j < -0.6 && last_p_plus_i < -2.0 && demand < -2.0);
            dwell_req = 4.0;
        } else {
            const bool just_switched_h = have_to_heater_ && (t - last_to_heater_t_ < 75.0);
            const bool still_heating_tr = (w2 - xr - b_ctrl) > -0.05;
            const double over_j_thr = just_switched_h ? -2.20 : -1.35;
            const bool exo_or_over = proactive_exo_cool || (!just_switched_h && active_cool_step) ||
                                     (still_heating_tr && tr_val > sp_cur + 0.12 && pred_110s_tr > sp_cur + 0.12 && err_j < over_j_thr) ||
                                     (err_j < -3.5 && tr_val > sp_cur - 0.3);
            want_tec = exo_or_over && (last_p_plus_i < -3.0) && (demand < -3.0);
            dwell_req = active_cool_step ? 2.0 : 3.0;
        }

        if (want_tec) {
            if (sign_side_ != 1) { sign_side_ = 1; sign_since_ = t; }
            if (t - sign_since_ >= dwell_req - 1e-9) {
                if (cold_start_needed() && have_cold_start_ && (t - last_cold_start_ < p_.chiller_min_gap_s)) {
                    if (!holding_) hold(t);
                } else {
                    holding_ = false;
                    ensure_water(t);
                    phase_ = Phase::Lead; seq_to_ = HeatSide::Tec; seq_until_ = lead_until_;
                    seq_from_hold_ = false; seq_fresh_ = (in_brake || proactive_exo_cool || active_cool_step);
                }
            }
        } else {
            sign_side_ = -1;
            if (holding_ && demand > 2.0) {
                holding_ = false;
                heat_only_ = false;
            }
        }
    } else {
        bool want_heater = false;
        double dwell_h = 2.0;
        if (in_brake) {
            want_heater = false;
            dwell_h = 999.0;
        } else if ((sp_cur < 32.0) && (tr_val > sp_cur + 0.18) &&
                   (xr + (std::max(15.0, tw) + 32.0) * std::min(0.0, (w2 - xr - b_ctrl) / std::max(1.0, tau)) > sp_cur + 0.06)) {
            // 常温/低温目标（<32 ℃）主动降温途中：按釜壁滞后 tw + 电热死区预留滑行提前量，未到滑行窗口前由 TEC 零出力滑行，严禁中途切回电热反烧
            want_heater = false;
            dwell_h = 999.0;
        } else if (tj_sp >= 28.0 && !proactive_exo_cool && !active_cool_step) {
            const double flare_s = std::min(6.0, std::max(1.2, 0.14 * std::max(0.0, tj - 23.0)));
            const double err_flare = tj_sp - (tj + flare_s * std::min(0.0, d_filt));
            want_heater = (err_flare >= -0.65);
            dwell_h = 0.5;
        } else {
            want_heater = (err_j > 0.35) && (last_p_plus_i > 1.5) && (demand > 1.5);
            dwell_h = 2.0;
        }

        if (want_heater) {
            if (sign_side_ != 0) { sign_side_ = 0; sign_since_ = t; }
            if (t - sign_since_ >= dwell_h - 1e-9) {
                begin_switch(t, HeatSide::Heater);
            }
        } else {
            sign_side_ = -1;
        }
    }
}

}  // namespace tec
