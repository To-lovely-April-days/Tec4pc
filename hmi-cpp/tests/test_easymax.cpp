// 闭环验收测试（EasyMax / RC1 级三阶段串级与水路协调算法）：
// 在真机标定的双节点物理反应器模型（含加热棒死区/双阶滞后、TEC 帕尔贴温差衰减与焦耳热、冷水机流量建立滞后、
// 釜壁纯滞后 + 一阶热阻、传感器高斯噪声与 0.01 ℃ 量化）上，直接驱动 C++ ChannelController 跑完 18 组全温域与参数失配工况
// 及连续变温、中途降目标测试，核验：
//   1. 全部工况釜内最大超调 ≤ +0.10 K，稳态精度 ≤ 0.05 K
//   2. 升温制动全程单次开水、单次切 TEC 再切回加热棒（flips ≤ 2, valve_opens ≤ 1）
//   3. 硬件安全不变量：切继电器前必须先关断输出；TEC 出力时冷却水阀必处于打开状态
#include "../core/station/channel_controller.hpp"
#include <algorithm>
#include <cmath>
#include <cstdio>
#include <deque>
#include <random>
#include <string>
#include <utility>
#include <vector>

using namespace tec;

namespace {

struct PlantParams {
    const char* name = "nom";
    double tau_r = 1005.0;
    double wall_delay = 42.0;
    double wall_lag = 129.0;
    double heater_delay = 6.5;
    double heater_lag1 = 23.7;
    double heater_lag2 = 4.0;
    double tec_lag = 14.0;
    double heater_rate_100 = 0.205;
    double tec_pump_100 = 0.275;
    double k_tec_fourier = 0.00048;
    double c_sink_ratio = 0.35;
    double k_sink_water = 0.14;
    double k_sink_air = 0.0012;
    double k_amb = 0.00078;
    double k_jr_couple = 0.00075;
    double t_amb = 21.0;
    double t_water = 20.0;
    double b_at_50 = 5.05;
    double b_slope = 0.087;
    double duty_slew_per_s = 30.0;
    double tj_noise_std = 0.012;
    double tr_noise_std = 0.004;
    double tr_quant = 0.01;
    double exo_rate = 0.0;
    double exo_start_s = 999999.0;
};

class CalibratedPlant {
public:
    CalibratedPlant(const PlantParams& p, double tr0, double tj0, uint32_t seed = 42)
        : p_(p), rng_(seed), tr_(tr0), tj_(tj0), tw_(tj0), tsink_(tj0) {
        hq_.push_back({0.0, 0.0});
        wq_.push_back({0.0, tj0});
    }

    double bias_true(double tr) const {
        return std::max(0.5, p_.b_at_50 + p_.b_slope * (tr - 50.0));
    }

    std::pair<double, double> read_sensors() {
        std::normal_distribution<double> nj(0.0, p_.tj_noise_std);
        std::normal_distribution<double> nr(0.0, p_.tr_noise_std);
        const double tj_m = std::round((tj_ + nj(rng_)) * 1000.0) / 1000.0;
        const double tr_q = std::round((tr_ + nr(rng_)) / p_.tr_quant) * p_.tr_quant;
        const double tr_m = std::round(tr_q * 100.0) / 100.0;
        return {tj_m, tr_m};
    }

    void step(double dt, const ChannelCommand& cmd) {
        t_ += dt;
        const int n_sub = 2;
        const double h = dt / n_sub;
        const bool side_is_tec = cmd.side == HeatSide::Tec;
        const bool chiller_run = cmd.chiller == ChillerState::Starting || cmd.chiller == ChillerState::Running;
        for (int s = 0; s < n_sub; s++) {
            const double target_duty = cmd.output_on ? cmd.device_duty : 0.0;
            if (!cmd.output_on) {
                duty_applied_ = 0.0;
            } else {
                const double diff = target_duty - duty_applied_;
                const double ms = p_.duty_slew_per_s * h;
                duty_applied_ += std::fabs(diff) > ms ? std::copysign(ms, diff) : diff;
            }
            const double heater_pct = (cmd.output_on && !side_is_tec) ? std::max(0.0, -duty_applied_) : 0.0;
            const double tec_pct = (cmd.output_on && side_is_tec) ? std::max(0.0, duty_applied_) : 0.0;

            const double w_tgt = (cmd.valve_open && chiller_run) ? 1.0 : (cmd.valve_open ? 0.05 : 0.0);
            const double tau_w = w_tgt > water_flow_ ? 18.0 : 15.0;
            water_flow_ += (w_tgt - water_flow_) * (1.0 - std::exp(-h / tau_w));

            hq_.push_back({t_, heater_pct});
            double h_del = hq_.front().second;
            while (hq_.size() > 1 && t_ - hq_.front().first >= p_.heater_delay) {
                h_del = hq_.front().second;
                hq_.pop_front();
            }
            h1_ += (h_del - h1_) * (1.0 - std::exp(-h / p_.heater_lag1));
            h2_ += (h1_ - h2_) * (1.0 - std::exp(-h / p_.heater_lag2));

            cf_ += (tec_pct - cf_) * (1.0 - std::exp(-h / p_.tec_lag));
            const double u_tec = cf_ / 100.0;
            const double dt_oppose = std::max(0.0, tsink_ - tj_);
            const double cop_factor = u_tec > 1e-4 ? std::max(0.0, 1.0 - dt_oppose / 64.0) : 0.0;
            const double hot_assist = 1.0 + 0.002 * std::max(0.0, std::min(80.0, tj_ - tsink_));
            const double q_peltier = p_.tec_pump_100 * u_tec * (tj_ < tsink_ ? cop_factor : hot_assist);
            const double q_joule = 0.12 * u_tec * u_tec;
            const double k_cond = p_.k_tec_fourier * (1.0 + 1.8 * water_flow_);
            const double q_fourier = k_cond * (tj_ - tsink_);

            const double q_tec_from_tj = q_peltier + q_fourier;
            const double q_into_sink = (q_tec_from_tj + q_joule) / p_.c_sink_ratio;
            const double q_sink_cool = p_.k_sink_water * water_flow_ * (tsink_ - p_.t_water) + p_.k_sink_air * (tsink_ - p_.t_amb);
            tsink_ += h * (q_into_sink - q_sink_cool);

            const double q_heat = p_.heater_rate_100 * (h2_ / 100.0);
            const double q_amb = -p_.k_amb * (tj_ - p_.t_amb);
            const double q_couple = -p_.k_jr_couple * (tj_ - tr_);
            tj_ += h * (q_heat - q_tec_from_tj + q_amb + q_couple);

            wq_.push_back({t_, tj_});
            double tj_del = wq_.front().second;
            while (wq_.size() > 1 && t_ - wq_.front().first >= p_.wall_delay) {
                tj_del = wq_.front().second;
                wq_.pop_front();
            }
            tw_ += (tj_del - tw_) * (1.0 - std::exp(-h / p_.wall_lag));

            const double exo = t_ >= p_.exo_start_s ? p_.exo_rate : 0.0;
            tr_ += h * ((tw_ - tr_ - bias_true(tr_)) / p_.tau_r + exo);
        }
    }

private:
    PlantParams p_;
    std::mt19937 rng_;
    double tr_, tj_, tw_, tsink_;
    double t_ = 0.0, duty_applied_ = 0.0, h1_ = 0.0, h2_ = 0.0, cf_ = 0.0, water_flow_ = 0.0;
    std::deque<std::pair<double, double>> hq_, wq_;
};

struct CaseSpec {
    const char* label;
    PlantParams pp;
    double tr0;
    double tj0;
    double sp;
    double dur_min;
};

}  // namespace

int main(int argc, char** argv) {
    const std::string gains_path = argc > 1 ? argv[1] : "testdata/gain-table-run1110.csv";
    const GainTable table = GainTable::load(gains_path);
    if (table.empty()) {
        std::printf("无法加载增益表：%s\n", gains_path.c_str());
        return 2;
    }

    PlantParams p_nom;
    PlantParams p_fast = p_nom; p_fast.tau_r = 480.0; p_fast.wall_delay = 28.0; p_fast.wall_lag = 92.0;
    PlantParams p_vfast = p_nom; p_vfast.tau_r = 400.0; p_vfast.wall_delay = 25.0; p_vfast.wall_lag = 80.0;
    PlantParams p_slow = p_nom; p_slow.tau_r = 1650.0; p_slow.wall_delay = 52.0; p_slow.wall_lag = 160.0;
    PlantParams p_lowb = p_nom; p_lowb.b_at_50 = 1.8; p_lowb.b_slope = 0.03;
    PlantParams p_hib = p_nom; p_hib.b_at_50 = 8.5; p_hib.b_slope = 0.12;
    PlantParams p_warmw = p_nom; p_warmw.t_water = 28.0;
    PlantParams p_exo = p_nom; p_exo.exo_rate = 0.003; p_exo.exo_start_s = 600.0;

    const std::vector<CaseSpec> suite = {
        {"1. Nominal 19.3->50C",          p_nom,   19.34, 21.27,  50.0,  90.0},
        {"2. Nominal 20->60C",            p_nom,   20.00, 21.50,  60.0,  95.0},
        {"3. HighStep 60->85C",           p_nom,   60.00, 65.40,  85.0,  95.0},
        {"4. HighTemp 25->115C",          p_nom,   25.00, 26.00, 115.0, 110.0},
        {"5. CrossZero -1.9->20C(Tj=5)",  p_nom,   -1.91,  5.04,  20.0,  90.0},
        {"6. SmallStep 40->45C",          p_nom,   40.00, 43.50,  45.0,  80.0},
        {"7. MicroStep 48.5->50C",        p_nom,   48.50, 53.40,  50.0,  70.0},
        {"8. FastFlask(tau=480s)20->55",  p_fast,  20.00, 21.50,  55.0,  90.0},
        {"9. VeryFast(tau=400s)20->60",   p_vfast, 20.00, 21.50,  60.0,  90.0},
        {"10.SlowFlask(tau=1650s)20->55", p_slow,  20.00, 21.50,  55.0, 120.0},
        {"11.LowBias(b=1.8K) 20->50C",    p_lowb,  20.00, 21.00,  50.0,  90.0},
        {"12.HighBias(b=8.5K) 20->50C",   p_hib,   20.00, 23.00,  50.0,  95.0},
        {"13.WarmWater(28C) 20->55C",     p_warmw, 20.00, 21.50,  55.0,  90.0},
        {"14.Exotherm(+0.003K/s@600s)",   p_exo,   20.00, 21.50,  50.0,  95.0},
        {"15.CoolDown 18->8C",            p_nom,   17.80, 19.50,   8.0,  90.0},
        {"16.DeepCool 8->-15C",           p_nom,    7.92,  9.50, -15.0, 110.0},
        {"17.HotToCold 60->20C",          p_nom,   60.00, 65.50,  20.0,  95.0},
        {"18.HighCool 85->60C",           p_nom,   85.00, 92.20,  60.0,  90.0},
    };

    ChannelConfig cfg;
    cfg.outer.fast.enabled = true;
    cfg.outer.fast.boost_max = 60.0;

    int fails = 0;
    for (const auto& c : suite) {
        CalibratedPlant plant(c.pp, c.tr0, c.tj0);
        ChannelController ctl(table, cfg);
        ctl.start(ControlMode::Reactor, c.sp);

        const int steps = static_cast<int>(c.dur_min * 60.0 / 0.5);
        std::vector<double> trs(steps);
        HeatSide prev_side = HeatSide::Heater;
        bool prev_out_on = true;
        bool safety_ok = true;

        for (int k = 0; k < steps; k++) {
            const auto [tj_m, tr_m] = plant.read_sensors();
            const ChannelCommand cmd = ctl.tick(tj_m, tr_m);
            // 硬件安全检查：切换继电器前上一拍必须先关断输出；TEC 制冷出力时水阀必开
            if (k > 0 && cmd.side != prev_side && prev_out_on) safety_ok = false;
            if (cmd.output_on && cmd.side == HeatSide::Tec && cmd.device_duty > 0.0 && !cmd.valve_open) safety_ok = false;
            prev_side = cmd.side;
            prev_out_on = cmd.output_on;
            plant.step(0.5, cmd);
            trs[k] = tr_m;
        }

        const bool heating = c.sp >= c.tr0;
        double peak_tr = trs[0];
        for (double v : trs) peak_tr = heating ? std::max(peak_tr, v) : std::min(peak_tr, v);
        const double over = heating ? (peak_tr - c.sp) : (c.sp - peak_tr);

        double ss_err = 0.0;
        for (int k = steps - 1200; k < steps; k++) ss_err = std::max(ss_err, std::fabs(trs[k] - c.sp));

        const int flips = ctl.heat_switch().flips();
        const int valves = ctl.heat_switch().valve_opens();
        const bool ok = safety_ok && (over <= 0.10 + 1e-6) && (ss_err <= 0.05 + 1e-6) && (flips <= 2) && (valves <= 1);
        if (!ok) fails++;
        std::printf("[%s] %-30s | over=%+.3fK | ss=%.3fK | flips=%2d | valve=%2d\n",
                    ok ? "PASS" : "FAIL", c.label, over, ss_err, flips, valves);
    }

    // 19. 升温中途紧急下调目标测试：20 -> 70C，8 min 夹套正处于高温拉升时改目标为 42C，不得超调
    {
        CalibratedPlant plant(p_nom, 20.0, 21.5);
        ChannelController ctl(table, cfg);
        ctl.start(ControlMode::Reactor, 70.0);
        const int steps = static_cast<int>(75.0 * 60.0 / 0.5);
        const int change_k = static_cast<int>(8.0 * 60.0 / 0.5);
        double max_after = -1e9, ss_err = 0.0;
        for (int k = 0; k < steps; k++) {
            if (k == change_k) ctl.set_setpoint(42.0);
            const auto [tj_m, tr_m] = plant.read_sensors();
            const ChannelCommand cmd = ctl.tick(tj_m, tr_m);
            plant.step(0.5, cmd);
            if (k >= change_k) max_after = std::max(max_after, tr_m);
            if (k >= steps - 1200) ss_err = std::max(ss_err, std::fabs(tr_m - 42.0));
        }
        const double over = max_after - 42.0;
        const bool ok = (over <= 0.10 + 1e-6) && (ss_err <= 0.05 + 1e-6) &&
                        (ctl.heat_switch().flips() <= 2) && (ctl.heat_switch().valve_opens() <= 1);
        if (!ok) fails++;
        std::printf("[%s] %-30s | over=%+.3fK | ss=%.3fK | flips=%2d | valve=%2d\n",
                    ok ? "PASS" : "FAIL", "19.MidBoostDrop 20->70->42C", over, ss_err,
                    ctl.heat_switch().flips(), ctl.heat_switch().valve_opens());
    }

    std::printf(fails ? "\nEasyMax 闭环验收：%d 项未通过\n" : "\nEasyMax 闭环验收：全部 19 项通过！\n", fails);
    return fails ? 1 : 0;
}
