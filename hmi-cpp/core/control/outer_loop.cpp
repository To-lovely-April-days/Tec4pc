#include "outer_loop.hpp"
#include <algorithm>
#include <cmath>

namespace tec {

OuterLoop::OuterLoop(const OuterParams& p) : p_(p), tau0_(p.tau), tau_(p.tau), tw0_(p.tw), tw_(p.tw) {
    k_ = calc_k();
    to_eff_ = p_.fast.enabled ? p_.fast.to_boost : p_.to;
}

double OuterLoop::calc_k() const {
    if (p_.fast.enabled) {
        const double k = p_.fast.k_margin_fast * (tau_ + tw_) * (tau_ + tw_) / (4.0 * tau_ * tw_);
        return std::min(3.5, std::max(1.15, k));
    }
    const double k = p_.k_margin * (tau_ + tw_) * (tau_ + tw_) / (4 * tau_ * tw_);
    return std::max(1.0, k);
}

double OuterLoop::bias_prior(double tr) const {
    if (p_.fast.bias_start) {
        const double v = p_.fast.bias_start(tr);
        if (v > 0.0) return v;
    }
    return std::max(0.5, 1.5 + 0.08 * (tr - 15.0));
}

void OuterLoop::reset(const OuterObs& o) {
    const double tr = o.tr.value_or(o.tj);
    tau_ = tau0_;
    tw_ = tw0_;
    wall_ = wall_prev_ = o.tj;
    w1_ = w2_ = w2_prev_ = o.tj;
    xr_ = tr;
    ref_ = tr;
    med_.assign(1, tr);
    xr_hist_.assign(1, {o.t, tr});
    full_s_ = 0;
    alarm_on_ = false;
    wall_rate_ = 0;
    eps_lp_ = 0;
    tj_prev_.reset();
    rj_ = 0;
    boost_ = 0;
    peak_ = tr;
    heater_mom_ = 0;

    if (p_.fast.enabled) {
        const double prior = bias_prior(tr);
        const double raw_diff = o.tj - tr;
        b_ = std::fabs(raw_diff - prior) <= 1.5 ? raw_diff : prior;
        b_ctrl_ = b_;
        tr_anchor_ = tr;
        drive_int_ = 0.0;
        tr_prev_ = tr;
        tr_rate_lp_ = 0.0;
        stall_s_ = 0.0;
        k_ = calc_k();
        const double sp_final = o.sp_final.value_or(o.sp);
        const bool is_ramp = (o.sp_rate != 0.0) || (sp_final != o.sp);
        sp_step_size_ = std::fabs(sp_final - tr);
        spf_prev_ = sp_final;
        if (!is_ramp && p_.fast.boost_max > 0.0 && (sp_final - tr) > 3.5) {
            stage_ = FastStage::Boost;
        } else {
            stage_ = FastStage::Hold;
        }
        return;
    }

    b_ = o.tj - tr;
    b_ctrl_ = b_;
    eT_ = tr;
    eb_ = o.tj - tr;
    th_ = 1.0 / tau_;
    tau_e_ = tau_;
    P_ = {};
    P_[0][0] = p_.r;
    P_[1][1] = p_.p0_b;
    P_[2][2] = (p_.p0_th_rel * th_) * (p_.p0_th_rel * th_);
    f1_ = f2_ = tr;
    tr0_ = tr;
    g1_ = g2_ = o.tj;
    S_ = {};
    k_ = calc_k();
}

// 釜内读数先过 3 点中值：只坏一个点的读数进不来
std::optional<double> OuterLoop::reading(const OuterObs& o) {
    if (!o.tr || std::isnan(*o.tr)) return std::nullopt;
    med_.push_back(*o.tr);
    if (med_.size() > 3) med_.pop_front();
    std::array<double, 3> s{};
    std::copy(med_.begin(), med_.end(), s.begin());
    std::sort(s.begin(), s.begin() + med_.size());
    return s[med_.size() / 2];
}

void OuterLoop::observe(const OuterObs& o, const std::optional<double>& tr) {
    const double dt = o.dt;
    wall_ += (o.tj - wall_) * (1.0 - std::exp(-dt / tw_));
    wall_rate_ = (wall_ - wall_prev_) / dt;
    wall_prev_ = wall_;
    tau_ekf(dt, tr);
    if (tr) tau_reg(dt, *tr);
    const double tau = tau_;
    const double eps = tr ? *tr - xr_ : 0.0;
    double to = p_.to;
    if (p_.eps0 > 0) {
        double es = eps;
        if (p_.eps_lp_s > 0) {
            eps_lp_ += (eps - eps_lp_) * (1.0 - std::exp(-dt / p_.eps_lp_s));
            es = eps_lp_;
        }
        to = std::max(p_.to_min, to / (1.0 + (es / p_.eps0) * (es / p_.eps0)));
    }
    to_eff_ = to;
    const double l1 = 2.0 / to - 1.0 / tau;
    const double c = tau / (to * to);
    xr_ += dt * ((wall_ - xr_ - b_) / tau + l1 * eps);
    b_ += dt * (-c * eps);
}

void OuterLoop::set_tau(double tau_new) {
    const double dtau = tau_new - tau_;
    if (dtau != 0.0) {
        const double slope = (wall_ - xr_ - b_) / tau_;
        b_ -= dtau * slope;
        tau_ = tau_new;
        k_ = calc_k();
    }
}

void OuterLoop::tau_ekf(double dt, const std::optional<double>& tr) {
    const double g = g_sp_;
    const double th = th_;
    const double x = wall_ - eT_ - eb_;
    eT_ += dt * th * x;
    const double f00 = 1.0 - dt * th, f01 = -dt * th, f02 = dt * x * g;
    auto P = P_;
    double r0[3];
    for (int j = 0; j < 3; j++) r0[j] = f00 * P[0][j] + f01 * P[1][j] + f02 * P[2][j];
    const double p00 = r0[0] * f00 + r0[1] * f01 + r0[2] * f02;
    const double p01 = r0[1], p02 = r0[2];
    const double qm_base = p_.sig_delta * wall_rate_ * th * dt;
    const double qm = qm_base * qm_base * (p_.tcorr / dt);
    const double th0 = 1.0 / tau0_;
    const double v_max = (p_.p0_th_rel * th0) * (p_.p0_th_rel * th0);
    double p22 = P[2][2];
    if (p22 < v_max) p22 = std::min(v_max, p22 + g * p_.q_th_rel * th0 * th0 * dt);
    const double p11 = P[1][1] + p_.q_b * dt, p12 = P[1][2];
    P[0] = {p00 + 1e-8 * dt + qm, p01, p02};
    P[1] = {p01, p11, p12};
    P[2] = {p02, p12, p22};
    if (tr) {
        const double sden = P[0][0] + p_.r;
        const double kk[3] = {P[0][0] / sden, P[0][1] / sden, P[0][2] / sden};
        const double inn = *tr - eT_;
        eT_ += kk[0] * inn;
        eb_ += kk[1] * inn;
        th_ += kk[2] * inn;
        const std::array<double, 3> row = P[0];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++) P[i][j] -= kk[i] * row[j];
        P[1][0] = P[0][1] = 0.5 * (P[0][1] + P[1][0]);
        P[2][0] = P[0][2] = 0.5 * (P[0][2] + P[2][0]);
        P[2][1] = P[1][2] = 0.5 * (P[1][2] + P[2][1]);
    }
    const double lo = 1.0 / (p_.tau_hi * tau0_), hi = 1.0 / (p_.tau_lo * tau0_);
    if (th_ <= lo || th_ >= hi) {
        th_ = std::min(hi, std::max(lo, th_));
        P[0][2] = P[2][0] = 0.0;
        P[1][2] = P[2][1] = 0.0;
    }
    for (int i = 0; i < 3; i++) P[i][i] = std::max(P[i][i], i < 2 ? 1e-12 : 1e-18);
    const int pairs[3][2] = {{0, 1}, {0, 2}, {1, 2}};
    for (auto& ij : pairs) {
        const int i = ij[0], j = ij[1];
        const double lim = 0.999 * std::sqrt(P[i][i] * P[j][j]);
        if (std::fabs(P[i][j]) > lim) P[i][j] = P[j][i] = std::copysign(lim, P[i][j]);
    }
    P_ = P;
    tau_e_ = 1.0 / th_;
}

void OuterLoop::tau_reg(double dt, double tr) {
    const double a = 1.0 - std::exp(-dt / p_.tf);
    f1_ += (tr - f1_) * a;
    f2_ += (f1_ - f2_) * a;
    g1_ += (wall_ - g1_) * a;
    g2_ += (g1_ - g2_) * a;
    const double z = (f1_ - f2_) / p_.tf;
    const double y = g2_ - f2_ - p_.beta * (f2_ - tr0_);
    const double wr = std::max(std::fabs(g1_ - g2_) / p_.tf, std::fabs(wall_rate_));
    const double xx = p_.sig_d * wr / p_.reg_r;
    const double w = 1.0 / (1.0 + xx * xx);
    const double d = std::exp(-dt / p_.tm);
    auto& S = S_;
    S[0] = S[0] * d + w;
    S[1] = S[1] * d + w * z;
    S[2] = S[2] * d + w * y;
    S[3] = S[3] * d + w * z * z;
    S[4] = S[4] * d + w * z * y;
    S[5] = S[5] * d + w * y * y;
    if (S[0] <= 1e-9) return;
    const double czz = S[3] - S[1] * S[1] / S[0];
    const double czy = S[4] - S[1] * S[2] / S[0];
    const double cyy = S[5] - S[2] * S[2] / S[0];
    const double lam = p_.tm / dt * p_.z0 * p_.z0;
    if (czz <= 0.0 || cyy <= 0.0) return;
    const double rho = czy / std::sqrt(czz * cyy);
    const double rp = std::max(0.0, rho);
    const double gain = czz / (czz + lam) * rp * rp * g_sp_;
    const double lo = std::max(p_.tau_lo * tau0_, std::min(tau0_, tau_e_));
    const double tau_ls = std::min(p_.tau_hi * tau0_, std::max(lo, czy / czz));
    double rate = dt / p_.ta * gain;
    if (tau_ls < tau_) rate /= p_.ta_down_mult;
    set_tau(tau_ + rate * (tau_ls - tau_));
}

std::pair<double, double> OuterLoop::g(double e) const {
    const double km = k_ - 1.0;
    if (e <= 0.0) return {km * e, km};
    const double pu = p_up();
    if (pu <= 1e-9) return {0.0, 0.0};
    const double t = std::tanh(km * e / pu);
    return {pu * t, km * (1.0 - t * t)};
}

double OuterLoop::u_base_easymax(double sp, double xr_val, double b_val) const {
    const double u_ss = sp + b_val;
    const double e_raw = sp - xr_val;
    const double tw1 = 0.26 * tw_;
    const double tw2 = 0.74 * tw_;
    const double dx_wall = (tw1 * (w1_ - u_ss) + tw2 * (w2_ - u_ss)) / tau_;
    double e = e_raw;
    if (e_raw > 0.0 && dx_wall > 0.0) {
        e = std::max(0.0, e_raw - dx_wall);
    } else if (e_raw < 0.0 && dx_wall < 0.0) {
        e = std::min(0.0, e_raw - dx_wall);
    }
    const double km = k_ - 1.0;
    const double km_cool = std::min(1.25, km);
    const double pu = p_.fast.rho_fast * tau_ / k_;
    const double g_val = e <= 0.0 ? (km_cool * e) : (pu > 1e-9 ? pu * std::tanh(km * e / pu) : 0.0);
    return u_ss + g_val;
}

double OuterLoop::predict_ballistic_peak(const OuterObs& o, double sp, double b_est, double slope_scale) const {
    const FastParams& f = p_.fast;
    const double dt = f.dt_pred;
    double Tj = o.tj;
    double W1 = w1_;
    double W2 = w2_;
    double T = xr_;
    const double tab0 = bias_prior(T);
    const double b0 = b_est;
    const double tw1 = 0.26 * tw_;
    const double tw2 = 0.74 * tw_;
    const double aw1 = 1.0 - std::exp(-dt / tw1);
    const double aw2 = 1.0 - std::exp(-dt / tw2);

    const double now_s = o.t;
    const double t_sw = o.cooling_side ? 0.0 : (o.in_lead ? std::max(0.0, o.lead_until - now_s) + 1.0 : f.t_switch_nom);
    const double t_ch = (o.chiller_state == 2) ? 0.0 : ((o.chiller_state == 1) ? std::max(0.0, 26.0 - (now_s - o.chiller_since)) : f.t_chiller_nom);
    double mom_left = o.cooling_side ? 0.0 : heater_mom_;

    double peak = T;
    const double hz = t_sw + t_ch + std::max(4.2 * tw_, 0.50 * tau_);
    for (double t = 0.0; t < hz; t += dt) {
        const double b_cur = b0 + slope_scale * (bias_prior(T) - tab0);
        if (t < t_sw) {
            const double step_m = mom_left * (1.0 - std::exp(-dt / 14.0));
            Tj += step_m - 0.0010 * std::max(0.0, Tj - 24.0) * dt;
            mom_left -= step_m;
        } else {
            const double rc_inst = f.rc_nom * std::max(0.52, std::min(1.28, (Tj + 10.0) / 62.0));
            const double cool_frac = (t - t_sw) >= t_ch ? 1.0 : (0.18 + 0.82 * ((t - t_sw) / std::max(1.0, t_ch)));
            const double passive = 0.0012 * std::max(0.0, Tj - 24.0);
            const double tj_aim = sp + b_cur;
            const double d_track = (tj_aim - Tj) / 14.0 * dt;
            Tj += std::max(-(rc_inst * cool_frac + passive) * dt, d_track);
        }
        W1 += (Tj - W1) * aw1;
        W2 += (W1 - W2) * aw2;
        T += dt * (W2 - T - b_cur) / tau_;
        if (T > peak) {
            peak = T;
        } else if (T < peak - 0.012 && t > (t_sw + t_ch + 0.7 * tw_)) {
            break;
        }
    }
    return peak;
}

OuterOut OuterLoop::step_easymax(const OuterObs& o) {
    const FastParams& f = p_.fast;
    const auto tr = reading(o);
    const double dt = o.dt;
    const double sp = o.sp;
    const double sp_final = o.sp_final.value_or(sp);
    const bool is_ramp = (o.sp_rate != 0.0) || (sp_final != sp);

    if (tj_prev_ && dt > 0.0) {
        rj_ += ((o.tj - *tj_prev_) / dt - rj_) * (1.0 - std::exp(-dt / 10.0));
    }
    tj_prev_ = o.tj;

    heater_mom_ += (f.heater_mom_gain * o.heater_duty_frac - heater_mom_) * (1.0 - std::exp(-dt / std::max(2.0, f.heater_mom_tau)));

    // 检测运行中改目标
    if (!o.first && spf_prev_ && std::fabs(sp_final - *spf_prev_) > 1e-6) {
        const double delta = sp_final - xr_;
        sp_step_size_ = std::fabs(delta);
        tr_anchor_ = tr.value_or(xr_);
        drive_int_ = 0.0;
        if (is_ramp) {
            stage_ = FastStage::Hold;
            boost_ = 0.0;
        } else if (f.enabled && f.boost_max > 0.0 && delta > 3.5) {
            stage_ = FastStage::Boost;
        } else if (delta >= -0.2) {
            const double ub_new = u_base_easymax(sp_final, xr_, b_ctrl_);
            if (o.tj > ub_new + 1.5 || w1_ > ub_new + 1.5) {
                stage_ = FastStage::Brake;
            } else {
                stage_ = FastStage::Hold;
                boost_ = 0.0;
            }
        } else {
            stage_ = FastStage::Hold;
            boost_ = 0.0;
        }
    }
    spf_prev_ = sp_final;

    double w_b = 1.0;
    if (!o.first) {
        const double xr_prev = xr_;
        const double tw1 = 0.26 * tw_;
        const double tw2 = 0.74 * tw_;
        w1_ += (o.tj - w1_) * (1.0 - std::exp(-dt / tw1));
        w2_ += (w1_ - w2_) * (1.0 - std::exp(-dt / tw2));
        wall_rate_ = (w2_ - w2_prev_) / dt;
        w2_prev_ = w2_;
        wall_ = w2_;

        if (tr) {
            const double raw_tr_rate = (tr_prev_ && dt > 0.0) ? ((*tr - *tr_prev_) / dt) : 0.0;
            tr_prev_ = *tr;
            tr_rate_lp_ += (raw_tr_rate - tr_rate_lp_) * (1.0 - std::exp(-dt / 20.0));
        }

        // 在线积分能量平衡 τ̂ / tw 辨识（带未搅拌热停滞保护与降温初期釜壁滞后过渡保护）
        if (tr && !is_ramp) {
            const bool unstirred_stall = (std::fabs(o.tj - *tr) > 8.0) && (std::fabs(tr_rate_lp_) < 0.0020);
            stall_s_ = unstirred_stall ? (stall_s_ + dt) : std::max(0.0, stall_s_ - dt);
            if (stall_s_ >= 20.0) {
                tr_anchor_ = *tr;
                drive_int_ = 0.0;
            } else if (std::fabs(sp - *tr) > 0.6) {
                const bool cool_wall_transient = (sp < tr_anchor_) && (std::fabs(o.tj - w2_) > 5.0);
                if (cool_wall_transient) {
                    tr_anchor_ = *tr;
                    drive_int_ = 0.0;
                } else {
                    const double drive_now = w2_ - *tr - b_;
                    drive_int_ += drive_now * dt;
                    const double d_tr_tot = *tr - tr_anchor_;
                    if (std::fabs(d_tr_tot) >= 1.8 && std::fabs(drive_int_) >= 550.0 && (drive_int_ * d_tr_tot > 0.0)) {
                        const double lo_mult = std::max(f.tau_lo_fast, 0.45);
                        const double tau_int = std::min(f.tau_hi_fast * tau0_, std::max(lo_mult * tau0_, drive_int_ / d_tr_tot));
                        const double snr = std::min(1.0, (std::fabs(d_tr_tot) - 1.4) / 3.5);
                        const double tau_tc = tau_int < tau_ ? 22.0 : 35.0;
                        tau_ += (1.0 - std::exp(-dt / tau_tc)) * snr * (tau_int - tau_);
                        const double tw_scale = std::max(0.70, std::min(1.35, std::sqrt(tau_ / tau0_)));
                        tw_ = tw0_ * tw_scale;
                        k_ = calc_k();
                    }
                }
            }
        }

        const double eps = tr ? (*tr - xr_) : 0.0;
        eps_lp_ += (eps - eps_lp_) * (1.0 - std::exp(-dt / f.eps_lp_s_fast));

        const double calm_err = std::max(std::fabs(o.tj - w2_) / 5.0, std::fabs(w1_ - w2_) / 4.0);
        const double calm_blend = 1.0 / (1.0 + calm_err * calm_err);
        const double calm_eff = calm_blend;

        const double to_base = f.to_calm * calm_eff + f.to_boost * (1.0 - calm_eff);
        const double to = std::max(f.to_min_fast, to_base / (1.0 + (eps_lp_ / f.eps0_fast) * (eps_lp_ / f.eps0_fast)));
        to_eff_ = to;

        const double l1 = 2.0 / to - 1.0 / tau_;
        const double c = tau_ / (to * to);

        const double net_drive = std::fabs(w2_ - xr_ - b_) + 0.8 * tw_ * std::fabs(wall_rate_);
        w_b = 1.0 / (1.0 + (net_drive / 8.5) * (net_drive / 8.5));
        if (stage_ == FastStage::Hold && calm_eff > 0.45) {
            w_b = std::max(w_b, calm_eff);
        }

        xr_ += dt * ((w2_ - xr_ - b_) / tau_ + l1 * eps);

        double db_ff = bias_prior(xr_) - bias_prior(xr_prev);
        if (db_ff > 0.0 && eps_lp_ > 0.025) {
            const double ff_gate = std::max(0.0, 1.0 - (eps_lp_ - 0.025) / 0.035);
            db_ff *= ff_gate;
        }
        b_ += db_ff + dt * (-c * w_b * eps);
        if (tr && (*tr > sp + 1.0) && (tr_rate_lp_ <= 0.0005)) {
            const double b_floor = bias_prior(xr_) - 2.0;
            if (b_ < b_floor) b_ = b_floor;
        }
        b_ = std::max(-15.0, std::min(35.0, b_));
    }

    const double b_exo_drop = (1.0 - w_b) * std::min(6.0, 12.0 * std::max(0.0, eps_lp_ - 0.025));
    b_ctrl_ = b_ - b_exo_drop;

    ref_ += (sp - ref_) * (1.0 - std::exp(-dt / (tau_ / k_)));
    const double d_ref = sp - ref_;
    g_sp_ = d_ref * d_ref / (d_ref * d_ref + p_.e0 * p_.e0);

    auto al = alarm(o);

    double u_base = u_base_easymax(sp, xr_, b_ctrl_);
    if (is_ramp) {
        u_base += p_.ramp_ff * (tau_ + tw_) * o.sp_rate;
        const double uf = u_base_easymax(sp_final, xr_, b_ctrl_);
        u_base = sp_final >= sp ? std::min(u_base, uf) : std::max(u_base, uf);
        stage_ = FastStage::Hold;
        boost_ = 0.0;
        peak_ = xr_;
        return {std::max(-50.0, std::min(200.0, u_base)), al};
    }

    if (f.boost_max <= 0.0) {
        stage_ = FastStage::Hold;
        boost_ = 0.0;
        peak_ = xr_;
        return {std::max(-50.0, std::min(200.0, u_base)), al};
    }

    const double b_cons = std::min(b_ctrl_, bias_prior(xr_) + 0.15);
    const double u_ss_cons = sp + b_cons;
    const double slope_scale = eps_lp_ <= 0.025 ? 1.0 : std::max(0.0, 1.0 - (eps_lp_ - 0.025) / 0.04);
    const double aim_margin = 0.18 + 0.75 * std::fabs(eps_lp_);
    const double sp_aim = sp - aim_margin;

    peak_ = predict_ballistic_peak(o, sp, b_cons, slope_scale);

    double want = 0.0;
    if (stage_ == FastStage::Boost) {
        const double dyn_cap = std::min(f.boost_max, std::max(10.0, 2.5 * sp_step_size_));
        const double room = sp_aim - peak_;
        if (room > 0.04 && (sp - xr_) > 0.8) {
            want = std::min(dyn_cap, 18.0 * room);
        } else {
            if (o.tj > u_base + 1.2 || w1_ > u_base + 1.2) {
                stage_ = FastStage::Brake;
            } else {
                stage_ = FastStage::Hold;
            }
        }
    }

    if (stage_ == FastStage::Brake) {
        const double over_safety = peak_ - (sp - 0.06);
        const double neg_pull = std::min(22.0, 12.0 * std::max(0.0, over_safety));
        const double u_glide_cap = std::min(u_base, u_ss_cons + 2.2);
        const double u_brake_tgt = over_safety > 0.0
            ? std::max(xr_ - 2.0, u_ss_cons - neg_pull)
            : std::min(u_glide_cap, u_ss_cons + std::max(0.0, (sp - 0.06 - peak_) * 5.0));
        want = std::min(0.0, u_brake_tgt - u_base);

        const double flare_s = std::min(6.0, std::max(1.2, 0.14 * std::max(0.0, o.tj - 23.0)));
        const double tj_flare = o.tj + flare_s * std::min(0.0, o.tj_d_filt);
        if (tj_flare <= std::max(u_brake_tgt, u_ss_cons) + 0.65 && o.tj <= u_base + 2.5 && peak_ <= sp - 0.04) {
            stage_ = FastStage::Hold;
            want = 0.0;
        }
    }

    if (stage_ == FastStage::Hold) {
        want = 0.0;
    }

    if (stage_ == FastStage::Brake) {
        boost_ += (want - boost_) * (1.0 - std::exp(-dt / 4.0));
    } else if (stage_ == FastStage::Hold) {
        boost_ += (want - boost_) * (1.0 - std::exp(-dt / 6.0));
        if (std::fabs(boost_) < 0.05) boost_ = 0.0;
    } else {
        boost_ += (want - boost_) * (1.0 - std::exp(-dt / 12.0));
    }

    const double u = std::min(u_base + boost_, std::max(u_base, f.tj_cap));
    return {std::max(-50.0, std::min(200.0, u)), al};
}

OuterOut OuterLoop::step(const OuterObs& o) {
    if (p_.fast.enabled) {
        return step_easymax(o);
    }
    const auto tr = reading(o);
    if (!o.first) observe(o, tr);
    const double sp = o.sp;
    const double rate = o.sp_rate;
    const double sp_final = o.sp_final.value_or(sp);
    if (spf_prev_ && p_.reopen_e1 > 0) {
        const double dl = sp_final - *spf_prev_;
        if (dl != 0.0) {
            const double w = dl * dl / (dl * dl + p_.reopen_e1 * p_.reopen_e1);
            const double v_max = (p_.p0_th_rel / tau0_) * (p_.p0_th_rel / tau0_);
            if (P_[2][2] < v_max) P_[2][2] += (v_max - P_[2][2]) * w;
        }
    }
    spf_prev_ = sp_final;
    ref_ += (sp - ref_) * (1.0 - std::exp(-o.dt / (tau_ / k_)));
    const double d = sp - ref_;
    g_sp_ = d * d / (d * d + p_.e0 * p_.e0);
    double u = sp + b_ + g(sp - xr_).first;
    if (rate != 0.0 || sp_final != sp) {
        u += p_.ramp_ff * (tau_ + tw_) * rate;
        const double uf = sp_final + b_ + g(sp_final - xr_).first;
        u = sp_final >= sp ? std::min(u, uf) : std::max(u, uf);
    }
    auto al = alarm(o);
    return {u, al};
}

// 用户定的放热报警：制冷顶满 60 s，釜内估计还在升，而且釜壁已经比釜内凉——说明热是料自己放出来的
std::optional<std::string> OuterLoop::alarm(const OuterObs& o) {
    xr_hist_.push_back({o.t, xr_});
    while (xr_hist_.front().first < o.t - 60.0) xr_hist_.pop_front();
    const bool full = o.cooling_side && o.inner_sat_low && !o.tec_withheld && o.output_on;
    full_s_ = full ? full_s_ + o.dt : 0.0;
    const bool rising = xr_ - xr_hist_.front().second > p_.alarm_rise_k;
    const bool cond = full_s_ >= p_.alarm_full_s && rising && wall_ < xr_;
    std::optional<std::string> out;
    if (cond && !alarm_on_) out = "制冷顶满 60 s 釜内还在升";
    alarm_on_ = cond;
    return out;
}

}  // namespace tec
