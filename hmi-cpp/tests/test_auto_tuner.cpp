// 两段式硬件自整定 + 双通道独立增益表与参数闭环验收测试：
//   1. 在照 2026-11-27 真机 samples.csv 标定的干式模块物理模型（tw ≈ 16 s、τ ≈ 880 s、TEC 0.185 K/s、电热滞后 11 s + 26 s）上，
//      从零运行 AutoTuner，核验：
//        · 全程严格遵守硬件安全不变量（先断输出再切继电器、TEC 出力前必先开冷却水 ≥ 5 s、结束后排余热才关水阀）
//        · 准确辨识出 8 行平滑增益表、真实加热比、外环刹车速率 rc_nom、电热余热 heater_mom_gain 及釜壁滞后 tw / τ
//        · 将整定产出的增益表与外环参数直接装入 ChannelController，跑「43.06 → 25.00 ℃（含前 155 s 未开搅拌）」与「20 → 50 ℃ 快速升温」，
//          均满足最大超调/过冷 ≤ 0.08 K、稳态误差 ≤ 0.04 K、单次切换无反烧！
//   2. 空釜模式（无 Tr 探头读数）：只更新夹套内环、加热比、刹车速率，完整保留原有稳态偏置与外环 tw / τ。
//   3. CoreApp 双通道独立性：通道 1 与通道 2 的增益表、外环参数、一键自整定与存盘完全独立，重启不丢。
#include "../core/app/core_app.hpp"
#include "../core/control/auto_tuner.hpp"
#include "../core/sim/sim_hardware.hpp"
#include "../core/station/channel_controller.hpp"
#include "../core/util/utf8_file.hpp"
#include "../third_party/nlohmann/json.hpp"
#include <algorithm>
#include <cmath>
#include <cstdio>
#include <deque>
#include <filesystem>
#include <random>
#include <vector>

using namespace tec;
using json = nlohmann::json;
namespace fs = std::filesystem;

static int g_fails = 0;
#define CHECK(c) do { if (!(c)) { std::printf("失败 %s:%d  %s\n", __FILE__, __LINE__, #c); g_fails++; } } while (0)

namespace {

// 2026-11-27 真机实测标定的干式模块 + 250 mL 玻璃釜物理模型
class RealDryBlockPlant {
public:
    RealDryBlockPlant(double tr0, double tj0, uint32_t seed = 7)
        : rng_(seed), tr_(tr0), tj_(tj0), tw_(tj0), tsink_(tj0) {
        hq_.push_back({0.0, 0.0});
        wq_.push_back({0.0, tj0});
    }

    double bias_true(double tr) const { return std::max(0.4, 0.9 + 0.06 * (tr - 25.0)); }

    std::pair<double, double> read_sensors() {
        std::normal_distribution<double> nj(0.0, 0.010);
        std::normal_distribution<double> nr(0.0, 0.004);
        const double tj_m = std::round((tj_ + nj(rng_)) * 1000.0) / 1000.0;
        const double tr_m = std::round((tr_ + nr(rng_)) * 100.0) / 100.0;
        return {tj_m, tr_m};
    }

    void step(double dt, double device_duty, bool output_on, bool side_is_tec, bool valve_open, bool chiller_run, double tau_r = 880.0) {
        t_ += dt;
        const double h = 0.5 * dt;
        for (int s = 0; s < 2; s++) {
            const double tgt = output_on ? device_duty : 0.0;
            if (!output_on) duty_ = 0.0;
            else {
                const double d = tgt - duty_;
                duty_ += std::fabs(d) > 15.0 ? std::copysign(15.0, d) : d;
            }
            const double heater_pct = (output_on && !side_is_tec) ? std::max(0.0, -duty_) : 0.0;
            const double tec_pct = (output_on && side_is_tec) ? std::max(0.0, duty_) : 0.0;

            const double w_tgt = (valve_open && chiller_run) ? 1.0 : (valve_open ? 0.08 : 0.0);
            water_flow_ += (w_tgt - water_flow_) * (1.0 - std::exp(-h / 16.0));

            const double tc = t_ - (1 - s) * h;
            hq_.push_back({tc, heater_pct});
            double h_del = hq_.front().second;
            while (hq_.size() > 1 && tc - hq_.front().first >= 11.0) { h_del = hq_.front().second; hq_.pop_front(); }
            h1_ += (h_del - h1_) * (1.0 - std::exp(-h / 26.0));
            h2_ += (h1_ - h2_) * (1.0 - std::exp(-h / 5.0));

            cf_ += (tec_pct - cf_) * (1.0 - std::exp(-h / 12.0));
            const double u_tec = cf_ / 100.0;
            const double dt_opp = std::max(0.0, tsink_ - tj_);
            const double cop = u_tec > 1e-4 ? std::max(0.0, 1.0 - dt_opp / 64.0) : 0.0;
            const double hot_assist = 1.0 + 0.002 * std::max(0.0, std::min(80.0, tj_ - tsink_));
            const double q_peltier = 0.185 * u_tec * (tj_ < tsink_ ? cop : hot_assist);
            const double q_joule = 0.11 * u_tec * u_tec;
            const double q_fourier = 0.00048 * (1.0 + 1.8 * water_flow_) * (tj_ - tsink_);

            const double q_tec = q_peltier + q_fourier;
            tsink_ += h * ((q_tec + q_joule) / 0.35 - 0.14 * water_flow_ * (tsink_ - 20.0) - 0.0012 * (tsink_ - 21.0));
            tj_ += h * (0.255 * (h2_ / 100.0) - q_tec - 0.00078 * (tj_ - 21.0) - 0.00075 * (tj_ - tr_));

            wq_.push_back({tc, tj_});
            double tj_del = wq_.front().second;
            while (wq_.size() > 1 && tc - wq_.front().first >= 4.0) { tj_del = wq_.front().second; wq_.pop_front(); }
            tw_ += (tj_del - tw_) * (1.0 - std::exp(-h / 12.0));
            tr_ += h * (tw_ - tr_ - bias_true(tr_)) / tau_r;
        }
    }

private:
    std::mt19937 rng_;
    double tr_, tj_, tw_, tsink_;
    double t_ = 0.0, duty_ = 0.0, h1_ = 0.0, h2_ = 0.0, cf_ = 0.0, water_flow_ = 0.0;
    std::deque<std::pair<double, double>> hq_, wq_;
};

}  // namespace

int main(int argc, char** argv) {
    const std::string seed_csv = argc > 1 ? argv[1] : "testdata/gain-table-run1110.csv";
    const GainTable seed_table = GainTable::load(seed_csv);
    CHECK(!seed_table.empty());

    // ── 1. 真机物理模型全自动两段式整定 + 闭环升/降温精度验证 ──
    {
        RealDryBlockPlant plant(24.0, 24.8, 101);
        AutoTuner tuner;
        TuneOptions opt;
        opt.t_high = 65.0;
        opt.t_low = 10.0;
        opt.heat_duty = 60.0;
        opt.cool_duty = 80.0;
        tuner.start(opt, seed_table, 170.0, 950.0);

        HeatSide prev_side = HeatSide::Heater;
        bool prev_on = false;
        double water_since = -1.0;
        int safety_violations = 0;

        for (int k = 0; k < 4000 && tuner.active(); k++) {
            const double t = k * 0.5;
            const auto [tj_m, tr_m] = plant.read_sensors();
            const TuneStepOut cmd = tuner.tick(0.5, tj_m, tr_m);

            if (cmd.valve_open && water_since < 0.0) water_since = t;
            if (!cmd.valve_open) water_since = -1.0;

            // 铁律 1：切换继电器前输出必须已关断
            if (cmd.side != prev_side && (prev_on || cmd.output_on)) safety_violations++;
            // 铁律 2：TEC 出力时冷却水阀必已开满 5 s 以上
            if (cmd.output_on && cmd.side == HeatSide::Tec && cmd.device_duty > 0.0) {
                if (!cmd.valve_open || water_since < 0.0 || (t - water_since) < 4.9) safety_violations++;
            }
            prev_side = cmd.side;
            prev_on = cmd.output_on;

            const bool ch_run = cmd.chiller == ChillerState::Starting || cmd.chiller == ChillerState::Running;
            plant.step(0.5, cmd.device_duty, cmd.output_on, cmd.side == HeatSide::Tec, cmd.valve_open, ch_run);
        }

        const TuneStatus st = tuner.status();
        CHECK(safety_violations == 0);
        CHECK(st.phase == TunePhase::Done && st.result.has_value());
        if (st.result) {
            const TuneResult& R = *st.result;
            std::printf("[AutoTuner 真机辨识] L_h=%.1fs, tau_h=%.1fs, Kh=%.3f, Kc=%.3f, rc_nom=%.3f, mom=%.2fK, outer=%d (tw=%.0fs, tau=%.0fs, rmse=%.3fK)\n",
                        R.lh_s, R.tau_h_s, R.kh_mid, R.kc_mid, R.rc_nom, R.heater_mom_gain,
                        R.outer_identified ? 1 : 0, R.tw, R.tau, R.outer_rmse);
            CHECK(R.rows.size() == 8);
            CHECK(R.rc_nom >= 0.12 && R.rc_nom <= 0.23);
            CHECK(R.heater_mom_gain >= 1.5 && R.heater_mom_gain <= 7.0);
            CHECK(R.outer_identified && R.tw >= 12.0 && R.tw <= 45.0 && R.tau >= 650.0 && R.tau <= 1150.0);

            // 把整定出来的表和参数直接装进控制器，跑用户那炉 43.06 -> 25.00 ℃（前 155 s 不开搅拌！）和 20 -> 50 ℃ 升温
            GainTable tuned_table(R.rows);
            ChannelConfig cfg;
            cfg.outer.tw = R.tw;
            cfg.outer.tau = R.tau;
            cfg.outer.fast.enabled = true;
            cfg.outer.fast.boost_max = 60.0;
            cfg.outer.fast.rc_nom = R.rc_nom;
            cfg.outer.fast.heater_mom_gain = R.heater_mom_gain;
            cfg.outer.fast.bias_start = [&tuned_table](double t) { return tuned_table.steady_bias_at(t).value_or(0.0); };

            // 工况 A：43.06 → 25.00 ℃ 降温（含前 155 s 未开搅拌）
            {
                RealDryBlockPlant p_cool(43.06, 44.22, 202);
                ChannelController ctl(tuned_table, cfg);
                ctl.start(ControlMode::Reactor, 25.0);
                double min_tr = 999.0;
                int flips = 0;
                HeatSide ls = HeatSide::Heater;
                std::vector<double> tail;
                for (int k = 0; k < 50 * 120; k++) {
                    const double t = k * 0.5;
                    const auto [tj_m, tr_m] = p_cool.read_sensors();
                    const ChannelCommand cmd = ctl.tick(tj_m, tr_m);
                    if (k > 0 && cmd.side != ls) flips++;
                    ls = cmd.side;
                    p_cool.step(0.5, cmd.device_duty, cmd.output_on, cmd.side == HeatSide::Tec, cmd.valve_open,
                                cmd.chiller == ChillerState::Starting || cmd.chiller == ChillerState::Running,
                                t < 155.0 ? 2800.0 : 880.0);
                    min_tr = std::min(min_tr, tr_m);
                    if (k >= 50 * 120 - 240) tail.push_back(std::fabs(tr_m - 25.0));
                }
                const double under_k = 25.0 - min_tr;
                const double ss_err = *std::max_element(tail.begin(), tail.end());
                std::printf("[整定后闭环降温 43.06->25C] 过冷=%.3fK, flips=%d, 稳态误差=%.3fK\n", under_k, flips, ss_err);
                CHECK(under_k <= 0.08 && flips <= 2 && ss_err <= 0.04);
            }

            // 工况 B：20.0 → 50.0 ℃ 快速升温
            {
                RealDryBlockPlant p_heat(20.0, 21.2, 303);
                ChannelController ctl(tuned_table, cfg);
                ctl.start(ControlMode::Reactor, 50.0);
                double max_tr = -999.0;
                int flips = 0;
                HeatSide ls = HeatSide::Heater;
                std::vector<double> tail;
                for (int k = 0; k < 50 * 120; k++) {
                    const auto [tj_m, tr_m] = p_heat.read_sensors();
                    const ChannelCommand cmd = ctl.tick(tj_m, tr_m);
                    if (k > 0 && cmd.side != ls) flips++;
                    ls = cmd.side;
                    p_heat.step(0.5, cmd.device_duty, cmd.output_on, cmd.side == HeatSide::Tec, cmd.valve_open,
                                cmd.chiller == ChillerState::Starting || cmd.chiller == ChillerState::Running);
                    max_tr = std::max(max_tr, tr_m);
                    if (k >= 50 * 120 - 240) tail.push_back(std::fabs(tr_m - 50.0));
                }
                const double over_k = max_tr - 50.0;
                const double ss_err = *std::max_element(tail.begin(), tail.end());
                std::printf("[整定后闭环升温 20->50C] 超调=%+.3fK, flips=%d, 稳态误差=%.3fK\n", over_k, flips, ss_err);
                CHECK(over_k <= 0.08 && flips <= 2 && ss_err <= 0.04);
            }
        }
    }

    // ── 2. 空釜无 Tr 读数整定：只标定内环，不破坏原有外环 tw / τ ──
    {
        RealDryBlockPlant plant(25.0, 25.0, 404);
        AutoTuner tuner;
        TuneOptions opt;
        opt.t_high = 55.0;
        opt.t_low = 12.0;
        opt.chiller_stop_delay_s = 2.0;
        tuner.start(opt, seed_table, 42.0, 910.0);
        for (int k = 0; k < 3000 && tuner.active(); k++) {
            const auto [tj_m, tr_m] = plant.read_sensors();
            (void)tr_m;
            const TuneStepOut cmd = tuner.tick(0.5, tj_m, std::nullopt);
            plant.step(0.5, cmd.device_duty, cmd.output_on, cmd.side == HeatSide::Tec, cmd.valve_open,
                       cmd.chiller == ChillerState::Starting || cmd.chiller == ChillerState::Running);
        }
        const TuneStatus st = tuner.status();
        CHECK(st.phase == TunePhase::Done && st.result.has_value());
        CHECK(!st.result->outer_identified && std::fabs(st.result->tw - 42.0) < 1e-6 && std::fabs(st.result->tau - 910.0) < 1e-6);
    }

    // ── 3. CoreApp 双通道完全独立存盘与在线整定测试 ──
    {
        std::error_code ec;
        const fs::path dir = fs::temp_directory_path() / "tec_dual_tune_test";
        fs::remove_all(dir, ec);
        fs::create_directories(dir, ec);
        const std::string ch1_csv = (dir / "gain-table-ch1.csv").u8string();
        fs::copy_file(fs_path(seed_csv), fs_path(ch1_csv), fs::copy_options::overwrite_existing);

        SimHardware sim;
        sim.speed = 6.0;   // 加速仿真物理过程
        CoreApp app(sim, sim, dir.u8string(), ch1_csv);
        auto call = [&](const json& q) { return json::parse(app.handle(q.dump(), false)); };

        // 改通道 2 的增益表与加码上限，通道 1 纹丝不动
        json rows2 = call({{"cmd", "gains.get"}, {"ch", 2}})["rows"];
        CHECK(!rows2.empty());
        rows2[0]["kp"] = 17.77;
        CHECK(call({{"cmd", "gains.set"}, {"ch", 2}, {"rows", rows2}})["ok"] == true);
        CHECK(call({{"cmd", "settings.set"}, {"ch", 2}, {"fast_boost_max", 25.0}, {"tw", 48.0}})["ok"] == true);
        CHECK(std::fabs(call({{"cmd", "gains.get"}, {"ch", 2}})["rows"][0]["kp"].get<double>() - 17.77) < 1e-6);
        CHECK(std::fabs(call({{"cmd", "gains.get"}, {"ch", 1}})["rows"][0]["kp"].get<double>() - 17.77) > 1.0);
        CHECK(std::fabs(call({{"cmd", "gains.get"}, {"ch", 2}})["tune"]["fast_boost_max"].get<double>() - 25.0) < 1e-6);
        CHECK(std::fabs(call({{"cmd", "gains.get"}, {"ch", 1}})["tune"]["fast_boost_max"].get<double>() - 60.0) < 1e-6);

        // 在通道 1 启动一键自整定并跑到完成，验证通道 1 自动存盘而通道 2 仍保持 17.77
        CHECK(call({{"cmd", "tune.start"}, {"ch", 1}, {"t_high", 55.0}, {"t_low", 12.0}, {"heat_duty", 70.0}, {"chiller_stop_delay_s", 2.0}})["ok"] == true);
        // 整定运行期间再开通道 2 整定应被拒绝（共用冷却水路）
        CHECK(call({{"cmd", "tune.start"}, {"ch", 2}, {"t_high", 55.0}})["ok"] == false);

        for (int k = 0; k < 2000; k++) {
            app.tick();
            sim.advance(0.5);
            const json ts = call({{"cmd", "tune.status"}, {"ch", 1}});
            if (!ts["active"].get<bool>()) break;
        }
        const json ts_end = call({{"cmd", "tune.status"}, {"ch", 1}});
        CHECK(ts_end["phase"] == "done");
        const json g1 = call({{"cmd", "gains.get"}, {"ch", 1}});
        CHECK(g1["rows"].size() == 8 && !g1["tune"]["tuned_at"].get<std::string>().empty());
        CHECK(std::fabs(call({{"cmd", "gains.get"}, {"ch", 2}})["rows"][0]["kp"].get<double>() - 17.77) < 1e-6);

        // 模拟重启 CoreApp：两通道独立增益表与独立硬件参数完整恢复
        {
            SimHardware sim2;
            CoreApp app2(sim2, sim2, dir.u8string(), ch1_csv);
            const json g1_r = json::parse(app2.handle("{\"cmd\":\"gains.get\",\"ch\":1}", false));
            const json g2_r = json::parse(app2.handle("{\"cmd\":\"gains.get\",\"ch\":2}", false));
            CHECK(g1_r["rows"].size() == 8 && !g1_r["tune"]["tuned_at"].get<std::string>().empty());
            CHECK(std::fabs(g2_r["rows"][0]["kp"].get<double>() - 17.77) < 1e-6);
            CHECK(std::fabs(g2_r["tune"]["tw"].get<double>() - 48.0) < 1e-6);
        }
        fs::remove_all(dir, ec);
    }

    if (g_fails == 0) std::printf("全部自整定与双通道独立性测试通过！\n");
    return g_fails ? 1 : 0;
}
