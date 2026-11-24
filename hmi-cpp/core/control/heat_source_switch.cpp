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
            // 挂着的时候回路又要加热了：连续够久就不等了
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
        // 等过最短间隔才接上的 TEC 至少留一阵：那段时间功率被钳在 0 以上，「该不该制冷」的证据没了，不能刚接上就被扳回去
        if (have_ep_ && p_.min_cool_after_hold_s > 0 && t - ep_since_ < std::max(1.0, p_.min_cool_after_hold_s)) { sign_side_ = -1; return; }
        if (signed_side(t, demand) == 0) begin_switch(t, HeatSide::Heater);
    }
}

}  // namespace tec
