#include "auto_tuner.hpp"
#include <algorithm>
#include <cmath>
#include <cstdio>

namespace tec {

namespace {
double clampd(double v, double lo, double hi) {
    return std::min(hi, std::max(lo, v));
}

std::string f1s(double v) {
    char b[32];
    std::snprintf(b, sizeof b, "%.1f", v);
    return b;
}

std::string f2s(double v) {
    char b[32];
    std::snprintf(b, sizeof b, "%.2f", v);
    return b;
}

std::string f0s(double v) {
    char b[32];
    std::snprintf(b, sizeof b, "%.0f", v);
    return b;
}

const char* phase_code_of(TunePhase p) {
    switch (p) {
        case TunePhase::Baseline: return "baseline";
        case TunePhase::HeatRamp: return "heat_ramp";
        case TunePhase::HeatCoast: return "heat_coast";
        case TunePhase::WaterLead: return "water_lead";
        case TunePhase::CoolRamp: return "cool_ramp";
        case TunePhase::Cooldown: return "cooldown";
        case TunePhase::Done: return "done";
        case TunePhase::Failed: return "failed";
        case TunePhase::Aborted: return "aborted";
        default: return "idle";
    }
}

// 用实测散点拟合一条平缓的线性趋势 rate(T) = a + b * (T - 35)，单点或空点时平滑退化
void fit_rate_line(const std::vector<std::pair<double, double>>& pts, double fallback, double min_v, double max_v,
                   double& a_at_35, double& slope_per_k) {
    if (pts.empty()) {
        a_at_35 = fallback;
        slope_per_k = 0.0;
        return;
    }
    if (pts.size() == 1) {
        a_at_35 = clampd(pts[0].second, min_v, max_v);
        slope_per_k = 0.0;
        return;
    }
    double sx = 0, sy = 0, sxx = 0, sxy = 0;
    const double n = static_cast<double>(pts.size());
    for (const auto& p : pts) {
        const double x = p.first - 35.0;
        sx += x;
        sy += p.second;
        sxx += x * x;
        sxy += x * p.second;
    }
    const double den = n * sxx - sx * sx;
    if (std::fabs(den) > 25.0) {
        slope_per_k = clampd((n * sxy - sx * sy) / den, -0.0035, 0.0035);
        a_at_35 = clampd((sy - slope_per_k * sx) / n, min_v, max_v);
    } else {
        slope_per_k = 0.0;
        a_at_35 = clampd(sy / n, min_v, max_v);
    }
}
}  // namespace

void AutoTuner::start(const TuneOptions& opt, const GainTable& existing_table, double fallback_tw, double fallback_tau) {
    opt_ = opt;
    opt_.heat_duty = clampd(opt_.heat_duty, 25.0, opt_.max_duty);
    opt_.cool_duty = clampd(opt_.cool_duty, 35.0, opt_.max_duty);
    opt_.water_lead_s = std::max(5.0, opt_.water_lead_s);
    opt_.water_settle_s = std::max(4.0, opt_.water_settle_s);
    prev_table_ = existing_table;
    fb_tw_ = clampd(fallback_tw, 10.0, 300.0);
    fb_tau_ = clampd(fallback_tau, 250.0, 3000.0);

    phase_ = TunePhase::Baseline;
    after_cooldown_ = TunePhase::Done;
    elapsed_ = 0.0;
    phase_t_ = 0.0;
    sub_t_ = 0.0;
    used_tec_ = false;

    last_duty_ = 0.0;
    side_ = HeatSide::Heater;
    valve_ = false;
    chiller_ = ChillerState::Idle;

    lh_found_ = false;
    lh_s_ = 8.0;
    max_heat_slope_ = 0.0;
    coast_loss_rate_ = 0.0008;

    hist_.clear();
    outer_trace_.clear();
    next_outer_sample_ = 0.0;
    heat_samples_.clear();
    cool_samples_.clear();
    result_.reset();
    note_ = "阶段 1/3：基线采样，正在测量起点温度与传感器底噪";
}

bool AutoTuner::active() const {
    return phase_ == TunePhase::Baseline || phase_ == TunePhase::HeatRamp || phase_ == TunePhase::HeatCoast ||
           phase_ == TunePhase::WaterLead || phase_ == TunePhase::CoolRamp || phase_ == TunePhase::Cooldown;
}

void AutoTuner::enter_cooldown(TunePhase final_after_water, const std::string& reason) {
    after_cooldown_ = final_after_water;
    note_ = reason;
    last_duty_ = 0.0;
    if (used_tec_ && opt_.chiller_stop_delay_s > 0.5) {
        phase_ = TunePhase::Cooldown;
        phase_t_ = 0.0;
        chiller_ = ChillerState::Stopping;
        valve_ = true;
    } else {
        phase_ = final_after_water;
        phase_t_ = 0.0;
        side_ = HeatSide::Heater;
        valve_ = false;
        chiller_ = ChillerState::Idle;
    }
}

void AutoTuner::abort() {
    if (!active()) return;
    enter_cooldown(TunePhase::Aborted, "已中止自整定，正在安全关断输出");
}

void AutoTuner::force_stop(const std::string& why) {
    if (!active()) return;
    phase_ = TunePhase::Aborted;
    phase_t_ = 0.0;
    last_duty_ = 0.0;
    side_ = HeatSide::Heater;
    valve_ = false;
    chiller_ = ChillerState::Idle;
    note_ = why;
}

double AutoTuner::sliding_slope(double window_s) const {
    if (hist_.size() < 4) return 0.0;
    const double t_end = hist_.back().t;
    size_t i0 = hist_.size() - 1;
    while (i0 > 0 && t_end - hist_[i0].t < window_s) i0--;
    const size_t n = hist_.size() - i0;
    if (n < 3 || (t_end - hist_[i0].t) < 2.0) return 0.0;
    double st = 0, sy = 0, stt = 0, sty = 0;
    const double t_ref = hist_[i0].t;
    for (size_t i = i0; i < hist_.size(); i++) {
        const double x = hist_[i].t - t_ref;
        const double y = hist_[i].tj;
        st += x;
        sy += y;
        stt += x * x;
        sty += x * y;
    }
    const double dn = static_cast<double>(n) * stt - st * st;
    return std::fabs(dn) > 1e-6 ? (static_cast<double>(n) * sty - st * sy) / dn : 0.0;
}

TuneStatus AutoTuner::status() const {
    TuneStatus s;
    s.phase = phase_;
    s.active = active();
    s.elapsed_s = elapsed_;
    s.tj = last_tj_;
    s.tr = last_tr_;
    s.duty = last_duty_;
    s.side = side_;
    s.valve_open = valve_;
    s.phase_code = phase_code_of(phase_);
    s.note = note_;
    s.result = result_;

    switch (phase_) {
        case TunePhase::Baseline:
            s.progress = 0.05 * clampd(phase_t_ / 10.0, 0.0, 1.0);
            break;
        case TunePhase::HeatRamp: {
            const double span = std::max(5.0, opt_.t_high - tj_base_);
            s.progress = 0.05 + 0.40 * clampd((last_tj_ - tj_base_) / span, 0.0, 1.0);
            break;
        }
        case TunePhase::HeatCoast:
            s.progress = 0.45 + 0.08 * clampd(phase_t_ / 28.0, 0.0, 1.0);
            break;
        case TunePhase::WaterLead:
            s.progress = 0.53 + 0.07 * clampd(phase_t_ / (opt_.water_lead_s + opt_.water_settle_s + 2.0), 0.0, 1.0);
            break;
        case TunePhase::CoolRamp: {
            const double span = std::max(5.0, tj_cool_start_ - opt_.t_low);
            s.progress = 0.60 + 0.35 * clampd((tj_cool_start_ - last_tj_) / span, 0.0, 1.0);
            break;
        }
        case TunePhase::Cooldown:
            s.progress = 0.95 + 0.04 * clampd(phase_t_ / std::max(1.0, opt_.chiller_stop_delay_s), 0.0, 1.0);
            break;
        case TunePhase::Done:
            s.progress = 1.0;
            break;
        default:
            s.progress = 0.0;
            break;
    }
    return s;
}

TuneStepOut AutoTuner::tick(double dt, double tj, std::optional<double> tr) {
    TuneStepOut cmd;
    if (!active() || dt <= 0.0 || std::isnan(tj)) return cmd;

    elapsed_ += dt;
    phase_t_ += dt;
    last_tj_ = tj;
    last_tr_ = tr;

    hist_.push_back({elapsed_, tj, tr.value_or(0.0), tr.has_value() && std::isfinite(*tr)});
    if (hist_.size() > 160) hist_.erase(hist_.begin());

    if (elapsed_ >= next_outer_sample_ - 1e-6) {
        outer_trace_.push_back({elapsed_, tj, tr.value_or(0.0), tr.has_value() && std::isfinite(*tr)});
        next_outer_sample_ = elapsed_ + 2.0;
    }

    switch (phase_) {
        case TunePhase::Baseline: {
            side_ = HeatSide::Heater;
            valve_ = false;
            chiller_ = ChillerState::Idle;
            last_duty_ = 0.0;
            cmd.output_on = false;
            cmd.device_duty = 0.0;
            cmd.side = side_;
            cmd.target_preview = tj;

            if (phase_t_ >= 10.0) {
                double sum = 0.0, lo = 1e9, hi = -1e9;
                for (const auto& p : hist_) {
                    sum += p.tj;
                    lo = std::min(lo, p.tj);
                    hi = std::max(hi, p.tj);
                }
                tj_base_ = sum / static_cast<double>(hist_.size());
                tj_amb_ = clampd(tj_base_, 15.0, 30.0);
                noise_pk_ = clampd(hi - lo, 0.01, 0.15);
                if (opt_.t_high < tj_base_ + 14.0) opt_.t_high = std::min(130.0, tj_base_ + 22.0);
                last_heat_bin_t_ = tj_base_ + 2.0;

                phase_ = TunePhase::HeatRamp;
                phase_t_ = 0.0;
                hist_.clear();
                hist_.push_back({elapsed_, tj, tr.value_or(0.0), tr.has_value() && std::isfinite(*tr)});
                cmd.notes.push_back("自整定：基线采样完成，进入干态加热阶跃辨识");
                note_ = "阶段 2/3：干态电热阶跃升温至 " + f0s(opt_.t_high) + " ℃";
            }
            break;
        }

        case TunePhase::HeatRamp: {
            side_ = HeatSide::Heater;
            valve_ = false;
            chiller_ = ChillerState::Idle;
            last_duty_ = -opt_.heat_duty;
            cmd.output_on = true;
            cmd.device_duty = last_duty_;
            cmd.side = side_;
            cmd.target_preview = opt_.t_high;

            const double slope = sliding_slope(8.0);
            if (slope > max_heat_slope_) max_heat_slope_ = slope;

            const double rise = tj - tj_base_;
            const double trig = std::max(0.22, 4.0 * noise_pk_);
            if (!lh_found_ && rise >= trig && slope > 0.005) {
                const double t_tangent = phase_t_ - rise / slope;
                lh_s_ = clampd(t_tangent > 0.5 ? t_tangent : phase_t_ * 0.65, 1.5, 45.0);
                lh_found_ = true;
            }

            if (phase_t_ >= 12.0 && tj >= last_heat_bin_t_ + 3.5 && slope > 0.004) {
                const double loss_comp = 0.0008 * std::max(0.0, tj - tj_amb_);
                const double r100 = (slope + loss_comp) / (opt_.heat_duty / 100.0);
                heat_samples_.push_back({tj - 1.5, clampd(r100, 0.03, 0.80)});
                last_heat_bin_t_ = tj;
            }

            if (phase_t_ >= 90.0 && rise < 0.7) {
                enter_cooldown(TunePhase::Failed, "整定失败：电热出力 90 s 夹套升温不足 0.7 ℃，请检查加热棒与继电器");
                cmd.output_on = false;
                cmd.device_duty = 0.0;
                cmd.notes.push_back(note_);
                break;
            }

            const bool plateau = (phase_t_ >= 120.0 && rise >= 12.0 && slope < 0.004);
            if (tj >= opt_.t_high || plateau || phase_t_ >= 1500.0) {
                if (heat_samples_.empty() && max_heat_slope_ > 0.003) {
                    heat_samples_.push_back({0.5 * (tj_base_ + tj), clampd(max_heat_slope_ / (opt_.heat_duty / 100.0), 0.03, 0.80)});
                }
                if (!lh_found_) lh_s_ = 6.0;
                tj_cutoff_ = tj;
                tj_peak_ = tj;
                t_peak_ = 0.0;
                phase_ = TunePhase::HeatCoast;
                phase_t_ = 0.0;
                hist_.clear();
                hist_.push_back({elapsed_, tj, tr.value_or(0.0), tr.has_value() && std::isfinite(*tr)});
                cmd.output_on = false;
                cmd.device_duty = 0.0;
                last_duty_ = 0.0;
                cmd.notes.push_back("自整定：达到加热上限，关断电热测余热冲高");
                note_ = "阶段 2/3：已断电热，正在实测加热棒余热惯性与干态散热";
            }
            break;
        }

        case TunePhase::HeatCoast: {
            side_ = HeatSide::Heater;
            valve_ = false;
            chiller_ = ChillerState::Idle;
            last_duty_ = 0.0;
            cmd.output_on = false;
            cmd.device_duty = 0.0;
            cmd.side = side_;
            cmd.target_preview = tj_cutoff_;

            if (tj > tj_peak_) {
                tj_peak_ = tj;
                t_peak_ = phase_t_;
            }
            const double slope = sliding_slope(6.0);
            if (phase_t_ >= 16.0 && slope < -0.0002) {
                coast_loss_rate_ = std::max(coast_loss_rate_, -slope);
            }

            const bool settled = (phase_t_ >= 18.0 && (phase_t_ - t_peak_) >= 8.0 && slope <= 0.001);
            if (settled || phase_t_ >= 34.0) {
                phase_ = TunePhase::WaterLead;
                phase_t_ = 0.0;
                sub_t_ = 0.0;
                valve_ = true;
                chiller_ = ChillerState::Starting;
                used_tec_ = true;
                cmd.valve_open = true;
                cmd.chiller = chiller_;
                cmd.notes.push_back("自整定：开启冷却水先导，准备切换至 TEC 制冷段");
                note_ = "阶段 3/3：正在打开冷却水并安全切换至 TEC";
            }
            break;
        }

        case TunePhase::WaterLead: {
            valve_ = true;
            if (phase_t_ >= 2.0) chiller_ = ChillerState::Running;
            last_duty_ = 0.0;
            cmd.output_on = false;
            cmd.device_duty = 0.0;
            cmd.valve_open = true;
            cmd.chiller = chiller_;
            cmd.target_preview = opt_.t_low;

            if (phase_t_ < opt_.water_lead_s) {
                side_ = HeatSide::Heater;
            } else {
                side_ = HeatSide::Tec;
            }
            cmd.side = side_;

            if (phase_t_ >= opt_.water_lead_s + 1.5 + opt_.water_settle_s) {
                tj_cool_start_ = tj;
                min_cool_tj_ = tj;
                last_cool_bin_t_ = tj - 2.0;
                phase_ = TunePhase::CoolRamp;
                phase_t_ = 0.0;
                hist_.clear();
                hist_.push_back({elapsed_, tj, tr.value_or(0.0), tr.has_value() && std::isfinite(*tr)});
                cmd.notes.push_back("自整定：水路已稳定，开始 TEC 制冷阶跃辨识");
                note_ = "阶段 3/3：通水 TEC 制冷阶跃下拉至 " + f0s(opt_.t_low) + " ℃";
            }
            break;
        }

        case TunePhase::CoolRamp: {
            side_ = HeatSide::Tec;
            valve_ = true;
            chiller_ = ChillerState::Running;
            last_duty_ = opt_.cool_duty;
            cmd.output_on = true;
            cmd.device_duty = last_duty_;
            cmd.side = side_;
            cmd.valve_open = true;
            cmd.chiller = chiller_;
            cmd.target_preview = opt_.t_low;

            if (tj < min_cool_tj_) min_cool_tj_ = tj;
            const double cool_rate = -sliding_slope(8.0);   // 正数 = 降温速率（K/s）
            const double drop = tj_cool_start_ - tj;

            if (phase_t_ >= 12.0 && tj <= last_cool_bin_t_ - 3.5 && cool_rate > 0.005) {
                const double tr_ref = tr.value_or(tj_amb_);
                const double passive_help = 0.00075 * std::max(0.0, tj - tj_amb_) + 0.00070 * std::max(0.0, tj - tr_ref);
                const double net_tec_rate = std::max(0.02, cool_rate - passive_help);
                const double r100 = net_tec_rate / (opt_.cool_duty / 100.0);
                cool_samples_.push_back({tj + 1.5, clampd(r100, 0.03, 0.90)});
                last_cool_bin_t_ = tj;
            }

            if (phase_t_ >= 90.0 && drop < 0.7) {
                enter_cooldown(TunePhase::Failed, "整定失败：TEC 制冷 90 s 夹套降温不足 0.7 ℃，请检查冷却水与 TEC");
                cmd.output_on = false;
                cmd.device_duty = 0.0;
                cmd.notes.push_back(note_);
                break;
            }

            const double slow_rate = -sliding_slope(16.0);
            const bool bottomed_out = (phase_t_ >= 75.0 && drop >= 14.0 && slow_rate < 0.009);
            if (tj <= opt_.t_low || bottomed_out || phase_t_ >= 1500.0) {
                if (cool_samples_.empty() && drop > 2.0 && phase_t_ > 5.0) {
                    cool_samples_.push_back({0.5 * (tj_cool_start_ + tj), clampd((drop / phase_t_) / (opt_.cool_duty / 100.0), 0.03, 0.90)});
                }
                synthesize();
                enter_cooldown(TunePhase::Done, result_ ? result_->summary : "自整定已完成");
                cmd.output_on = false;
                cmd.device_duty = 0.0;
                cmd.side = side_;
                cmd.valve_open = valve_;
                cmd.chiller = chiller_;
                cmd.notes.push_back("自整定：参数辨识完成，进入冷却水排余热收尾");
            }
            break;
        }

        case TunePhase::Cooldown: {
            last_duty_ = 0.0;
            cmd.output_on = false;
            cmd.device_duty = 0.0;
            cmd.side = side_;
            cmd.valve_open = true;
            cmd.chiller = ChillerState::Stopping;
            cmd.target_preview = tj;

            if (phase_t_ >= opt_.chiller_stop_delay_s) {
                valve_ = false;
                chiller_ = ChillerState::Idle;
                side_ = HeatSide::Heater;
                phase_ = after_cooldown_;
                cmd.valve_open = false;
                cmd.chiller = ChillerState::Idle;
                cmd.side = HeatSide::Heater;
                cmd.notes.push_back("自整定：水路收尾完成，执行器已全部安全归零");
            }
            break;
        }

        default:
            break;
    }
    return cmd;
}

void AutoTuner::synthesize() {
    TuneResult res;
    res.lh_s = clampd(lh_s_, 2.0, 40.0);

    const double delta_coast = std::max(0.0, tj_peak_ - tj_cutoff_);
    res.heater_mom_gain = delta_coast > 0.12
        ? clampd(delta_coast / (opt_.heat_duty / 100.0), 0.8, 8.5)
        : 1.5;
    res.tau_h_s = t_peak_ > 1.5 ? clampd(1.20 * t_peak_, 6.0, 42.0) : 8.0;

    std::vector<std::pair<double, double>> h_pts, c_pts;
    for (const auto& s : heat_samples_) h_pts.push_back({s.temp, s.rate_100});
    for (const auto& s : cool_samples_) c_pts.push_back({s.temp, s.rate_100});

    double kh35 = 0.22, kh_slope = -0.0004;
    double kc35 = 0.18, kc_slope = 0.0010;
    fit_rate_line(h_pts, 0.22, 0.04, 0.70, kh35, kh_slope);
    fit_rate_line(c_pts, 0.18, 0.04, 0.75, kc35, kc_slope);
    res.kh_mid = kh35;
    res.kc_mid = kc35;

    // 热夹套制动速率 rc_nom：取 35~60 ℃ 区间的实测 TEC 速率折算到满量程 max_duty（保守乘 0.92 裕度）
    double rc_sum = 0.0;
    int rc_cnt = 0;
    for (const auto& s : cool_samples_) {
        if (s.temp >= 26.0) {
            rc_sum += s.rate_100 * (opt_.max_duty / 100.0);
            rc_cnt++;
        }
    }
    const double rc_est = rc_cnt > 0 ? (rc_sum / rc_cnt) : (kc35 * (opt_.max_duty / 100.0));
    res.rc_nom = clampd(0.86 * rc_est, 0.08, 0.42);

    // 若整定过程中釜内 Tr 探头有效且温变 ≥ 1.6 K（说明釜内装有液体并开着搅拌），同步拟合二阶釜壁滞后 tw、釜时间常数 tau 与稳态偏置！
    res.tw = fb_tw_;
    res.tau = fb_tau_;
    double b25_fit = 1.6, b_slope_fit = 0.06;
    double tr_lo = 1e9, tr_hi = -1e9;
    size_t tr_valid_n = 0;
    for (const auto& p : outer_trace_) {
        if (p.has_tr) {
            tr_lo = std::min(tr_lo, p.tr);
            tr_hi = std::max(tr_hi, p.tr);
            tr_valid_n++;
        }
    }

    if (tr_valid_n >= 40 && (tr_hi - tr_lo) >= 1.6) {
        double best_score = 1e9, best_rmse = 1e9;
        double best_tw = fb_tw_, best_tau = fb_tau_, best_b25 = 1.2;
        const bool has_b_prior = outer_trace_[0].has_tr && std::fabs(outer_trace_[0].tj - outer_trace_[0].tr) <= 4.5;
        const double b_prior = has_b_prior
            ? clampd((outer_trace_[0].tj - outer_trace_[0].tr) - 0.065 * (outer_trace_[0].tr - 25.0), 0.4, 2.8)
            : 1.2;
        static const double kTwGrid[] = {12, 16, 22, 30, 42, 58, 80, 110, 145, 180};
        static const double kTauGrid[] = {360, 500, 650, 820, 900, 980, 1180, 1450, 1800};
        static const double kBGrid[] = {0.5, 0.9, 1.3, 1.8, 2.4};

        std::vector<double> w2_buf(outer_trace_.size(), 0.0);
        for (double tw_c : kTwGrid) {
            double w1 = outer_trace_[0].tj, w2 = outer_trace_[0].tj;
            w2_buf[0] = w2;
            for (size_t i = 1; i < outer_trace_.size(); i++) {
                const double dt = std::max(0.1, outer_trace_[i].t - outer_trace_[i - 1].t);
                w1 += (outer_trace_[i].tj - w1) * (1.0 - std::exp(-dt / (0.26 * tw_c)));
                w2 += (w1 - w2) * (1.0 - std::exp(-dt / (0.74 * tw_c)));
                w2_buf[i] = w2;
            }
            for (double tau_c : kTauGrid) {
                for (double b_c : kBGrid) {
                    double tr_s = outer_trace_[0].tr;
                    double err2 = 0.0;
                    size_t cnt = 0;
                    for (size_t i = 1; i < outer_trace_.size(); i++) {
                        const double dt = std::max(0.1, outer_trace_[i].t - outer_trace_[i - 1].t);
                        const double b_cur = b_c + 0.065 * (tr_s - 25.0);
                        tr_s += dt * (w2_buf[i] - tr_s - b_cur) / tau_c;
                        if (outer_trace_[i].has_tr) {
                            const double e = outer_trace_[i].tr - tr_s;
                            err2 += e * e;
                            cnt++;
                        }
                    }
                    const double rmse = cnt > 0 ? std::sqrt(err2 / static_cast<double>(cnt)) : 1e9;
                    const double score = rmse + (has_b_prior ? 0.035 * std::fabs(b_c - b_prior) : 0.0);
                    if (score < best_score) {
                        best_score = score;
                        best_rmse = rmse;
                        best_tw = tw_c;
                        best_tau = tau_c;
                        best_b25 = b_c;
                    }
                }
            }
        }
        if (best_rmse < 0.45) {
            res.outer_identified = true;
            res.tw = clampd(best_tw, 12.0, 220.0);
            res.tau = clampd(best_tau, 320.0, 2200.0);
            res.outer_rmse = best_rmse;
            b25_fit = best_b25;
            b_slope_fit = 0.065;
        }
    }

    // 生成 8 行平滑增益表（完全覆盖 -15 .. 90 ℃，触摸屏一屏 8 行完整可看可改）
    // 采用标准 Skogestad SIMC 串联滞后消去律：Td 抵消电热热惯性，闭环常数按纯滞后 L_h 整定
    static const double kTemps[8] = {-15.0, 0.0, 15.0, 25.0, 40.0, 55.0, 70.0, 90.0};
    const double l_eff = clampd(res.lh_s, 3.5, 24.0);
    const double tau_c = std::max(16.0, 1.30 * l_eff + 0.20 * res.tau_h_s);

    for (double t_row : kTemps) {
        // 电热升温速率在高温略受散热衰减；TEC 降温速率在低温受帕尔贴温差 COP 衰减、高温受冷水导热增强
        const double kh_t = clampd(kh35 + kh_slope * (t_row - 35.0) - 0.0003 * std::max(0.0, t_row - 50.0), 0.04, 0.65);
        const double cop_cold = t_row < 20.0 ? clampd(1.0 - (20.0 - t_row) / 68.0, 0.35, 1.0) : (1.0 + 0.003 * (t_row - 20.0));
        const double kc_base = clampd(kc35 + kc_slope * (clampd(t_row, 10.0, 70.0) - 35.0), 0.04, 0.70);
        const double kc_t = clampd(kc_base * (t_row < 15.0 ? cop_cold : 1.0), 0.035, 0.75);

        const double kh_per_pct = kh_t / 100.0;
        const double kp = clampd(1.0 / (kh_per_pct * (tau_c + l_eff)), 5.0, 32.0);
        const double ti = clampd(3.4 * (tau_c + l_eff), 65.0, 195.0);
        const double ki = clampd(kp / ti, 0.025, 0.35);
        const double td = clampd(0.35 * l_eff + 0.32 * res.tau_h_s, 5.0, 16.0);
        const double kd = clampd(kp * td, 25.0, 240.0);
        const double hr = clampd(kh_t / kc_t, 0.35, 4.50);

        GainRow row;
        row.temp = t_row;
        row.kp = std::round(kp * 100.0) / 100.0;
        row.ki = std::round(ki * 10000.0) / 10000.0;
        row.kd = std::round(kd * 10.0) / 10.0;
        row.heat_ratio = std::round(hr * 100.0) / 100.0;

        if (res.outer_identified) {
            row.steady_bias = std::round(std::max(0.3, b25_fit + b_slope_fit * (t_row - 25.0)) * 10.0) / 10.0;
        } else if (auto sb = prev_table_.steady_bias_at(t_row)) {
            row.steady_bias = std::round(*sb * 10.0) / 10.0;
        }
        res.rows.push_back(row);
    }

    res.summary = "整定完成：刹车速率 " + f2s(res.rc_nom) + " K/s · 加热比 " + f2s(kh35 / std::max(0.035, kc35)) +
                  (res.outer_identified ? (" · 釜壁滞后 " + f0s(res.tw) + " s / τ " + f0s(res.tau) + " s")
                                        : "（釜温未大幅变化，外环 tw/τ 保留原值）");
    result_ = res;
}

}  // namespace tec
