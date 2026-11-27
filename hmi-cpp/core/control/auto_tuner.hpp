// 两段式硬件特征与内外环一体化自整定器（替代旧版频繁切水与继电器的 RelayAutoTuner）：
//   1. [阶段 1/3] 基线采样（Baseline）：关断输出，测当前夹套起点温度与噪声底
//   2. [阶段 2/3] 干态电热阶跃 + 关断余热（HeatRamp → HeatCoast，不开冷却水）：
//        · 固定安全功率拉升夹套至 t_high，测电热等效纯滞后 L_h 与各温区 100% 升温率 K_h(T)
//        · 关断加热棒滑行，实测电热棒满功率余热冲高 heater_mom_gain 与热惯性时间常数 tau_h
//   3. [阶段 3/3] 单次开水稳流 + TEC 制冷阶跃（WaterLead → CoolRamp → Cooldown）：
//        · 先开冷却水阀满先导时间，再断输出安全切至 TEC 并等水流稳定
//        · 固定功率下拉夹套至 t_low（或触底平顶自动收尾），实测各温区 TEC 100% 降温率 K_c(T) 与外环刹车速率 rc_nom
//        · 按物理对称性直接计算各温区真实「加热比 = K_h(T) / K_c(T)」，并用无超调 SIMC 规则生成平滑抗噪的 8 行增益表
//        · 若釜内 Tr 探头有效且整定全程温变显著（釜内带液开搅拌），同步最小二乘拟合出真实釜壁滞后 tw、釜时间常数 tau 与稳态偏置列
#pragma once
#include "gain_table.hpp"
#include "heat_source_switch.hpp"
#include <optional>
#include <string>
#include <vector>

namespace tec {

enum class TunePhase {
    Idle = 0,
    Baseline = 1,
    HeatRamp = 2,
    HeatCoast = 3,
    WaterLead = 4,
    CoolRamp = 5,
    Cooldown = 6,
    Done = 7,
    Failed = 8,
    Aborted = 9,
};

struct TuneOptions {
    double t_high = 65.0;               // 加热阶跃目标温度（℃）
    double t_low = 10.0;                // 制冷阶跃目标温度（℃）
    double heat_duty = 60.0;            // 加热阶跃占空比（%，30..85）
    double cool_duty = 80.0;            // TEC 制冷阶跃占空比（%，40..90）
    double max_duty = 90.0;             // 设备允许最大占空比（%）
    double water_lead_s = 6.0;          // 切 TEC 前的开水先导秒数（≥ 5 s）
    double water_settle_s = 8.0;        // 切到 TEC 后零出力等水路稳流的秒数
    double chiller_stop_delay_s = 30.0; // 结束或中止后停冷水机延迟关水阀秒数
};

struct TuneResult {
    std::vector<GainRow> rows;          // 生成的 8 行增益表（正好铺满触摸屏 8 行表格）
    double lh_s = 8.0;                  // 实测电热死区滞后（s）
    double tau_h_s = 16.0;              // 实测电热热惯性时间常数（s）
    double kh_mid = 0.22;               // 中温段 100% 电热升温速率（K/s）
    double kc_mid = 0.16;               // 中温段 100% TEC 降温速率（K/s）
    double rc_nom = 0.185;              // 实测热夹套刹车速率（K/s，供外环弹道预测用）
    double heater_mom_gain = 3.2;       // 实测满功率加热棒余热冲高（K）
    bool outer_identified = false;      // 是否成功辨识出釜内 tw / tau / 稳态偏置
    double tw = 35.0;                   // 实测夹套→釜壁热滞后（s）
    double tau = 880.0;                 // 实测釜内时间常数（s）
    double outer_rmse = 0.0;            // 外环模型全段拟合残差（K）
    std::string summary;                // 整定结果简述
};

struct TuneStepOut {
    double device_duty = 0.0;           // 正 = TEC，负 = 加热棒
    bool output_on = false;
    HeatSide side = HeatSide::Heater;
    bool valve_open = false;
    ChillerState chiller = ChillerState::Idle;
    double target_preview = 0.0;        // 当前阶段面向的目标温度（供状态显示与曲线参考）
    std::vector<std::string> notes;
};

struct TuneStatus {
    TunePhase phase = TunePhase::Idle;
    bool active = false;
    double progress = 0.0;              // 0.0 .. 1.0
    double elapsed_s = 0.0;
    double tj = 0.0;
    std::optional<double> tr;
    double duty = 0.0;
    HeatSide side = HeatSide::Heater;
    bool valve_open = false;
    std::string phase_code;             // idle / baseline / heat_ramp / heat_coast / water_lead / cool_ramp / cooldown / done / failed / aborted
    std::string note;                   // 当前阶段或失败/完成说明
    std::optional<TuneResult> result;
};

class AutoTuner {
public:
    AutoTuner() = default;

    // 启动新一轮两段式自整定；existing_table 用于在空釜未辨识出稳态偏置时保留原有偏置起点
    void start(const TuneOptions& opt, const GainTable& existing_table, double fallback_tw = 35.0, double fallback_tau = 880.0);

    // 人工中止：若正在用 TEC 制冷，自动转入安全排热收尾（先关输出停冷水机，延迟关水阀）
    void abort();
    // 紧急停机（安全联锁跳闸或停机退出时立即切断一切输出与阀门）
    void force_stop(const std::string& why);

    bool active() const;
    TunePhase phase() const { return phase_; }
    TuneStatus status() const;

    // 每 inner_period_s（0.5 s）调一次
    TuneStepOut tick(double dt, double tj, std::optional<double> tr);

private:
    struct TracePt {
        double t = 0.0;
        double tj = 0.0;
        double tr = 0.0;
        bool has_tr = false;
    };
    struct SlopeSample {
        double temp = 0.0;
        double rate_100 = 0.0;
    };

    void enter_cooldown(TunePhase final_after_water, const std::string& reason);
    void synthesize();
    double sliding_slope(double window_s) const;

    TuneOptions opt_{};
    GainTable prev_table_{};
    double fb_tw_ = 35.0, fb_tau_ = 880.0;

    TunePhase phase_ = TunePhase::Idle;
    TunePhase after_cooldown_ = TunePhase::Done;
    double elapsed_ = 0.0;
    double phase_t_ = 0.0;
    double sub_t_ = 0.0;
    bool used_tec_ = false;

    double last_tj_ = 25.0;
    std::optional<double> last_tr_;
    double last_duty_ = 0.0;
    HeatSide side_ = HeatSide::Heater;
    bool valve_ = false;
    ChillerState chiller_ = ChillerState::Idle;

    double tj_base_ = 25.0;
    double tj_amb_ = 22.0;
    double noise_pk_ = 0.02;
    bool lh_found_ = false;
    double lh_s_ = 8.0;
    double max_heat_slope_ = 0.0;
    double tj_cutoff_ = 65.0;
    double tj_peak_ = 65.0;
    double t_peak_ = 0.0;
    double coast_loss_rate_ = 0.001;
    double tj_cool_start_ = 65.0;
    double min_cool_tj_ = 65.0;

    std::vector<TracePt> hist_;         // 近期窗口（用于算斜率）
    std::vector<TracePt> outer_trace_;  // 全程 2 s 抽样（用于外环 tw / tau / bias 拟合）
    double next_outer_sample_ = 0.0;

    std::vector<SlopeSample> heat_samples_;
    std::vector<SlopeSample> cool_samples_;
    double last_heat_bin_t_ = -1e9;
    double last_cool_bin_t_ = 1e9;

    std::string note_;
    std::optional<TuneResult> result_;
};

}  // namespace tec
