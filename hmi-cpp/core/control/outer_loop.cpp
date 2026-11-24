#include "outer_loop.hpp"
#include <algorithm>
#include <cmath>

namespace tec {

OuterLoop::OuterLoop(const OuterParams& p) : p_(p), tau0_(p.tau), tau_(p.tau), tw_(p.tw) {
    k_ = calc_k();
    to_eff_ = p_.to;
}

double OuterLoop::calc_k() const {
    double k = p_.k_margin * (tau_ + tw_) * (tau_ + tw_) / (4 * tau_ * tw_);
    return std::max(1.0, k);
}

void OuterLoop::reset(const OuterObs& o) {
    const double tr = o.tr.value_or(o.tj);
    wall_ = wall_prev_ = o.tj;
    xr_ = tr;
    b_ = o.tj - tr;
    ref_ = tr;
    med_.assign(1, tr);
    xr_hist_.assign(1, {o.t, tr});
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
    wall_rate_ = 0;
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

// τ̂ 变了：观测器里「釜内在以多快的速度走」是事实，不跟着跳——把 b̂ 按同一个斜率换算过去
void OuterLoop::set_tau(double tau_new) {
    const double dtau = tau_new - tau_;
    if (dtau != 0.0) {
        const double slope = (wall_ - xr_ - b_) / tau_;
        b_ -= dtau * slope;
        tau_ = tau_new;
        k_ = calc_k();
    }
}

// 另起一路：T、它自己的 b、θ = 1 ÷ τ 三个量，按「读数 − 模型」一起小修。它只把 τ̂ 交出去，它的 T、b 不进控制律
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

// 回归那一路：y = F[Ŵ − Tr] − β·(F[Tr] − 起点)，z = F[dTr/dt]，带遗忘的加权协方差
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
    // 往小里改要两路都同意：回归想改到多小，不许小过卡尔曼那一路认的 τ（往小改错了会冲过头，往大改错了只是慢）
    const double lo = std::max(p_.tau_lo * tau0_, std::min(tau0_, tau_e_));
    const double tau_ls = std::min(p_.tau_hi * tau0_, std::max(lo, czy / czz));
    double rate = dt / p_.ta * gain;
    if (tau_ls < tau_) rate /= p_.ta_down_mult;
    set_tau(tau_ + rate * (tau_ls - tau_));
}

// 比例那一份。升温方向平滑封顶：离目标远的时候夹套最多比「稳住要的温度」高 P上限，不会无限往上顶
std::pair<double, double> OuterLoop::g(double e) const {
    const double km = k_ - 1.0;
    if (e <= 0.0) return {km * e, km};
    const double pu = p_up();
    if (pu <= 1e-9) return {0.0, 0.0};
    const double t = std::tanh(km * e / pu);
    return {pu * t, km * (1.0 - t * t)};
}

OuterOut OuterLoop::step(const OuterObs& o) {
    const auto tr = reading(o);
    if (!o.first) observe(o, tr);
    const double sp = o.sp;
    const double rate = o.sp_rate;
    const double sp_final = o.sp_final.value_or(sp);
    // 终点变了：对 τ̂ 的把握放开（新的一段升降温 = 新的一次认 τ 的机会；变得小就基本不放）
    if (spf_prev_ && p_.reopen_e1 > 0) {
        const double dl = sp_final - *spf_prev_;
        if (dl != 0.0) {
            const double w = dl * dl / (dl * dl + p_.reopen_e1 * p_.reopen_e1);
            const double v_max = (p_.p0_th_rel / tau0_) * (p_.p0_th_rel / tau0_);
            if (P_[2][2] < v_max) P_[2][2] += (v_max - P_[2][2]) * w;
        }
    }
    spf_prev_ = sp_final;
    // 「目标变动引起的过渡还剩多少」：只在过渡里认 τ，稳住以后不认（稳态下读数里没有 τ 的信息，认了只会被噪声带偏）
    ref_ += (sp - ref_) * (1.0 - std::exp(-o.dt / (tau_ / k_)));
    const double d = sp - ref_;
    g_sp_ = d * d / (d * d + p_.e0 * p_.e0);
    // 控制律
    double u = sp + b_ + g(sp - xr_).first;
    if (rate != 0.0 || sp_final != sp) {
        u += p_.ramp_ff * (tau_ + tw_) * rate;
        const double uf = sp_final + b_ + g(sp_final - xr_).first;
        u = sp_final >= sp ? std::min(u, uf) : std::max(u, uf);
    }
    auto al = alarm(o);
    if (p_.fast.enabled) {
        const double boosted = u + fast_boost(o);
        u = std::min(boosted, std::max(u, p_.fast.tj_cap));
        u = std::max(-50.0, std::min(200.0, u));
    }
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

// 从这一拍的状态起，按「不再加码、夹套回到稳住要的温度」往前推，返回釜内最高会到多少。
// 只推「空档 + 3.5 × 釜壁滞后」这么远：只看靠已经存进去的热能冲到哪，不把以后慢慢爬的那段算进来
// （第一版把慢慢爬也算成「会到」，结果刹得太早）。
double OuterLoop::predict_peak(double sp, double tj, bool use_min) const {
    const FastParams& f = p_.fast;
    const double dt = f.dt_pred;
    double Tj = tj, W = wall_, T = xr_;
    double b0 = b_;
    const bool tab = static_cast<bool>(f.bias_start);
    const double tab0 = tab ? f.bias_start(T) : 0.0;
    if (tab && use_min) b0 = std::min(b0, tab0);
    double b = b0;
    const double aw = 1.0 - std::exp(-dt / tw_);
    double peak = T;
    const double rj = std::max(0.0, rj_);
    const double hz = f.t_dead + 3.5 * tw_;
    for (double t = 0.0; t < hz; t += dt) {
        if (t < f.t_dead) {
            Tj += rj * dt;                                   // 空档里夹套照现在的势头继续走
        } else {
            const double d = (sp + b - Tj) / f.t_track * dt;
            Tj += std::max(-f.rc * dt, d);                   // 往下最快只能按 TEC 拉得动的速度走
        }
        W += (Tj - W) * aw;
        if (tab) b = b0 + f.bias_start(T) - tab0;            // 维持温差随釜温变：走势照起步表，高低照这一炉认出来的
        T += dt * (W - T - b) / tau_;
        if (T > peak) peak = T;
        else if (T < peak - 0.02 && t > f.t_dead) break;
    }
    return peak;
}

double OuterLoop::fast_boost(const OuterObs& o) {
    const FastParams& f = p_.fast;
    if (tj_prev_ && o.dt > 0) rj_ += ((o.tj - *tj_prev_) / o.dt - rj_) * (1.0 - std::exp(-o.dt / 10.0));
    tj_prev_ = o.tj;
    const double sp = o.sp;
    peak_ = predict_peak(sp, o.tj, true);
    const double margin = f.m0 + f.c_eps * std::fabs(eps_lp_);
    const double room = sp - margin - peak_;
    double want = 0.0;
    if (room >= 0.0) {
        want = std::min(f.boost_max, f.kb * room);
    } else {
        const double pk = f.bias_start ? predict_peak(sp, o.tj, false) : peak_;
        const double over = pk - (sp + f.m_over);
        if (over > 0.0) want = -std::min(f.neg_max, f.kb * over);
        // 只管「还没到目标、但余热要把它带过头」；已经在目标上方（降温台阶、放热）不归这里管
        want *= std::min(1.0, std::max(0.0, (sp + 0.3 - xr_) / 0.3));
    }
    if (f.boost_lp > 0) boost_ += (want - boost_) * (1.0 - std::exp(-o.dt / f.boost_lp));
    else boost_ = want;
    return boost_;
}

}  // namespace tec
