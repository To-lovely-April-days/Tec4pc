// 釜内控温的外环：按实测实时算出「夹套该到多少度」。
//
// 这是 2026-11-14 定稿的做法（Python 参考实现 outer_m.py 的类 M，缺省参数）逐行搬过来的，算出来的数要和参考实现逐拍一样
// （tests/golden_outer.cpp 拿 14 场真机记录核对）。改这里的算式之前先改参考实现并重出对拍数据，不要只改一边。
//
//   夹套目标 = 目标 + b̂ + g(目标 − T̂)
//   b̂：维持温差（夹套要比釜内高多少才能稳住），T̂：釜内温度的估计，都由一个二阶观测器按读数实时算
//   g：比例那一份。升温方向平滑封顶（tanh），降温方向不封
//   τ̂：釜的时间常数，按实测连续修正（两路算法：回归 + 卡尔曼；往小改要两路都同意，因为往小改错了会冲过头）
//
// 为什么没有「到站收回」「卡住补一把」这类规则：用户 2026-11-12 定的方向是不加规则，全部按实测算。
#pragma once
#include <array>
#include <deque>
#include <functional>
#include <optional>
#include <string>
#include <utility>

namespace tec {

// 快速升温（用户 2026-11-24 要的：夹套拉高、快到、算出要超就提前转 TEC 制冷、不超调优先）。
// 只在升温方向起作用；降温、保温、放热完全交给上面那套。
//   夹套目标 = 上面算的夹套目标 + 加码
//   每一拍用同一个模型往前推一遍：「从现在起不再加码，靠已经存进夹套和釜壁里的热，釜内最高会到多少」
//   还差得远就加码（夹套拉高）；快够了加码连续收到 0；算出来要冲过目标就把夹套压到「稳住要的温度」以下，让 TEC 把多的热抽走
// 没有写死的切换温度——刹车点是每拍算出来的。
// 状态：2026-11-24 试算台上升温稳住时间中位 55 → 30 min，但 140 场里 32 场冲过 > 0.10 K，还没过线，等真机数据再调。
struct FastParams {
    bool enabled = false;
    double rc = 0.15;         // TEC 全力制冷时夹套每秒能降多少（K/s）。试算台上量到 0.24–0.28，这里只按六成算
    double t_dead = 30.0;     // 从决定刹车到夹套真开始降的空档（s）：符号驻留 + 开冷却水阀通水 + 扳继电器 + 夹套自己的滞后
    double t_track = 20.0;    // 夹套跟目标的快慢（s）
    double kb = 8.0;          // 「还差多少」→ 加码 的增益（K/K）
    double boost_max = 60.0;  // 最多加码多少（K）
    double neg_max = 30.0;    // 算出来要冲过时最多把夹套压低多少（K）
    double tj_cap = 150.0;    // 加码后夹套目标的顶（℃）
    double m0 = 0.05;         // 基础余量（K）
    double c_eps = 1.0;       // 读数离模型每远 1 K，余量多留这么多 K（模型对不上 = 没把握 = 早刹车）
    double m_over = 0.02;     // 算出来的最高点超过 目标 + 这么多 才往下压
    double boost_lp = 20.0;   // 加码过一阶平滑（s），免得夹套来回摆
    double dt_pred = 4.0;     // 往前推的步长（s）
    // 维持温差的起步表（增益表里「稳态偏置」那一列）。给了的话：决定「还加不加码」用 观测器的 和 表的 里小的那个（偏向早刹车）；
    // 决定「要不要往下压」只用观测器自己的（保温时它是准的，不会因为表偏小就平白去制冷）
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
    // 放热报警要看的执行器状态
    bool cooling_side = false, inner_sat_low = false, tec_withheld = false, output_on = true;
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
    double tau() const { return tau_; }
    double tau_ekf() const { return tau_e_; }
    double k() const { return k_; }
    double p_up() const { return p_.rho * tau_ / k_; }
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
    double predict_peak(double sp, double tj, bool use_min) const;
    double fast_boost(const OuterObs& o);

    OuterParams p_;
    double tau0_, tau_, tw_, k_;
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
    // 快速升温
    std::optional<double> tj_prev_;
    double rj_ = 0, peak_ = 0, boost_ = 0;
};

}  // namespace tec
