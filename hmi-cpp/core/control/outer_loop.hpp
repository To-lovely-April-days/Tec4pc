// 釜内控温的外环：按实测实时算出「夹套该到多少度」。
//
// 基础模式（fast.enabled == false）：保持 2026-11-14 定稿实现（tests/golden_outer.cpp 逐拍对拍）。
// 快速无超调串级模式（fast.enabled == true，EasyMax / RC1 级三阶段平滑制动算法）：
//   1. 单调三阶段状态机：BOOST（干态全功率拉高）→ BRAKE（单次通水 + TEC 精确抽走釜壁储热）→ HOLD（无扰切回干态加热稳态保温）
//   2. 双带宽二阶釜壁观测器（tw1=0.26*tw, tw2=0.74*tw）+ 在线积分能量平衡 τ̂/tw 自适应辨识
//   3. 釜壁储热 Smith 等效前馈补偿（消除制动切回保温瞬间的外环比例二次补热与降温过冷）
//   4. 扰动/放热有效偏置前馈修正（b_ctrl），在低散热失配或突发反应放热时提前压低制动目标，严守 ±0.10 ℃ 零超调红线
#pragma once
#include <array>
#include <deque>
#include <functional>
#include <optional>
#include <string>
#include <utility>

namespace tec {

enum class FastStage {
    Hold = 0,
    Boost = 1,
    Brake = 2,
};

struct FastParams {
    bool enabled = false;
    double rc = 0.15;
    double t_dead = 30.0;
    double t_track = 20.0;
    double kb = 8.0;
    double boost_max = 60.0;      // 最多加码多少（K）；0 = 不启用加码
    double neg_max = 30.0;
    double tj_cap = 150.0;        // 加码后夹套目标上限（℃）
    double m0 = 0.05;
    double c_eps = 1.0;
    double m_over = 0.02;
    double boost_lp = 20.0;
    double dt_pred = 4.0;

    // EasyMax 三阶段弹道预测与双带宽观测器常数
    double rho_fast = 0.022;
    double k_margin_fast = 1.05;
    double to_boost = 135.0;
    double to_calm = 38.0;
    double to_min_fast = 22.0;
    double eps0_fast = 1.5;
    double eps_lp_s_fast = 14.0;
    double tau_lo_fast = 0.35;
    double tau_hi_fast = 3.0;
    double rc_nom = 0.225;           // TEC + 冷却水在热夹套制动段的标称下拉速率（K/s）
    double heater_mom_gain = 3.0;    // 满功率加热棒关断后的余热冲高估计（K）
    double heater_mom_tau = 18.0;    // 加热棒余热时间常数（s）
    double t_switch_nom = 7.0;       // 驻留 + 通水先导 + 继电器死区的标称切换时间（s）
    double t_chiller_nom = 24.0;     // 冷水机水路建立标称时间（s）

    // 维持温差的起步表（增益表里「稳态偏置」列）
    std::function<double(double)> bias_start;
};

struct OuterParams {
    // ── 出厂三个数（每台机器、每个工位整定一次）──
    double tau = 950.0;      // 釜的时间常数的起点（s）
    double tw = 170.0;       // 夹套 → 釜壁的滞后（s）
    double rho = 0.017;      // 夹套在常用高温、零出力时自己每秒凉多少（K/s）
    // ── 设计常数（所有机器一样）──
    double k_margin = 0.75;  // K 取「刚好不冲过」那个值的几成
    double to = 150.0;       // 观测器时间常数（s）
    double eps0 = 2.0;       // 读数离模型多远（K）时观测器快一倍
    double to_min = 20.0;
    double eps_lp_s = 20.0;  // 变速看的是「读数 − 模型」这么多秒的平均，一个坏点带不动它
    double tau_lo = 0.4, tau_hi = 3.0;   // τ̂ 只在 τ̂₀ × [lo, hi] 里走
    double e0 = 0.2;
    // 卡尔曼那一路
    double r = 5e-2, q_b = 1e-4, p0_b = 4.0, p0_th_rel = 0.3, q_th_rel = 0.0, sig_delta = 120.0, tcorr = 60.0;
    double reopen_e1 = 5.0;  // 目标一拍里变了 Δ：对 τ̂ 的把握按 Δ² ÷ (Δ² + e1²) 放回去重新认
    // 回归那一路
    double tf = 100.0, tm = 600.0, ta = 120.0, reg_r = 0.3, z0 = 3e-4, sig_d = 100.0, beta = 0.1;
    double ta_down_mult = 1.0;
    // 放热报警（只报，不动控制）
    double alarm_full_s = 60.0, alarm_rise_k = 0.02;
    double ramp_ff = 1.0;
    FastParams fast;
};

// 一拍的输入
struct OuterObs {
    double t = 0;                 // 开控以来的秒数
    double dt = 2.0;              // 距上一拍多少秒
    double sp = 0;                // 这一拍的釜内目标（斜坡时是斜坡上的当前值）
    std::optional<double> tr;     // 釜内读数；没读到就空着
    double tj = 0;                // 夹套读数
    double sp_rate = 0;           // 斜坡的目标变化率（K/s）；台阶 = 0
    std::optional<double> sp_final;   // 斜坡的终点；台阶时空着
    bool first = false;           // 开控第一拍
    // 放热报警与水路协同要看的执行器状态
    bool cooling_side = false, inner_sat_low = false, tec_withheld = false, output_on = true;
    double heater_duty_frac = 0.0;    // 当前加热棒归一化占空比（0..1），用于估计电热棒热惯性余量
    double tj_d_filt = 0.0;           // 内环滤波后的夹套温变率 dTj/dt（K/s），用于速度前瞻平滑退制动
    bool in_lead = false;             // 热源切换器是否正处于通水先导阶段
    double lead_until = -1e9;         // 通水先导结束时刻（s）
    int chiller_state = 0;            // 冷水机状态（0=Idle, 1=Starting, 2=Running, 3=Stopping）
    double chiller_since = 0.0;      // 冷水机进入当前状态的时刻（s）
};

struct OuterOut {
    double tj_sp = 0;             // 夹套目标
    std::optional<std::string> alarm;   // 这一拍新报出来的报警；没有就空着
};

class OuterLoop {
public:
    explicit OuterLoop(const OuterParams& p = {});
    void reset(const OuterObs& o);        // 第一次拿到读数那一拍
    OuterOut step(const OuterObs& o);

    // 内部量：界面上显示、对拍用
    double wall() const { return wall_; }
    double xr() const { return xr_; }
    double b() const { return b_; }
    double b_ctrl() const { return p_.fast.enabled ? b_ctrl_ : b_; }
    double w1() const { return w1_; }
    double w2() const { return w2_; }
    FastStage stage() const { return stage_; }
    double tau() const { return tau_; }
    double tau_ekf() const { return tau_e_; }
    double k() const { return k_; }
    double p_up() const { return (p_.fast.enabled ? p_.fast.rho_fast : p_.rho) * tau_ / k_; }
    double g_sp() const { return g_sp_; }
    double to_eff() const { return to_eff_; }
    double rj() const { return rj_; }
    double peak() const { return peak_; }
    double boost() const { return boost_; }

private:
    std::optional<double> reading(const OuterObs& o);
    void observe(const OuterObs& o, const std::optional<double>& tr);
    void set_tau(double tau_new);
    void tau_ekf(double dt, const std::optional<double>& tr);
    void tau_reg(double dt, double tr);
    std::pair<double, double> g(double e) const;
    double calc_k() const;
    std::optional<std::string> alarm(const OuterObs& o);

    // EasyMax 三阶段串级与弹道预测内部方法
    double bias_prior(double tr) const;
    double u_base_easymax(double sp, double xr_val, double b_val) const;
    double predict_ballistic_peak(const OuterObs& o, double sp, double b_est, double slope_scale) const;
    OuterOut step_easymax(const OuterObs& o);

    OuterParams p_;
    double tau0_, tau_, tw0_, tw_, k_;
    double wall_ = 0, wall_prev_ = 0, wall_rate_ = 0, xr_ = 0, b_ = 0, eps_lp_ = 0, to_eff_ = 0;
    double ref_ = 0, g_sp_ = 0;
    std::optional<double> spf_prev_;
    std::deque<double> med_;
    // 卡尔曼那一路
    double eT_ = 0, eb_ = 0, th_ = 0, tau_e_ = 0;
    std::array<std::array<double, 3>, 3> P_{};
    // 回归那一路
    double f1_ = 0, f2_ = 0, g1_ = 0, g2_ = 0, tr0_ = 0;
    std::array<double, 6> S_{};
    // 报警
    std::deque<std::pair<double, double>> xr_hist_;
    double full_s_ = 0;
    bool alarm_on_ = false;
    // 快速升温（EasyMax 三阶段状态与二阶釜壁状态）
    FastStage stage_ = FastStage::Hold;
    double w1_ = 0, w2_ = 0, w2_prev_ = 0;
    double b_ctrl_ = 0;
    double tr_anchor_ = 0, drive_int_ = 0;
    std::optional<double> tr_prev_;
    double tr_rate_lp_ = 0, stall_s_ = 0;
    double heater_mom_ = 0;
    double sp_step_size_ = 0;
    std::optional<double> tj_prev_;
    double rj_ = 0, peak_ = 0, boost_ = 0;
};

}  // namespace tec
