// 执行机构这一层：加热棒和 TEC 之间怎么切、冷却水什么时候开关。一个工位一个。
//
// 硬件安全铁律（所有模式共同遵守）：
//   · 接 TEC 之前先通水：开冷却水阀（有冷水机就一起开）→ 等 5 s → 关输出、断加热棒、合 TEC、开输出
//   · 扳继电器前必须关断温控器输出 1 s（不带载扳继电器），加热棒与 TEC 绝不同时吸合
//   · 回加热棒：断 TEC → 停冷水机 → 等冷水机停稳（30 s）才关阀，绝不憋泵
//   · 冷水机两次冷启动之间至少隔 60 s
//   · 使能继电器 DO2 / DO3 程序绝不去碰；继电器只一路一路写（FC05）
#pragma once
#include <string>
#include <vector>

namespace tec {

struct SwitchParams {
    double chiller_start_delay_s = 30.0;   // 冷水机从下令到真转起来；接了冷水机串口读得到状态时用读到的
    double chiller_stop_delay_s = 30.0;
    double sign_dwell_s = 3.0;
    double sign_deadband_pct = 1.0;
    double water_lead_s = 5.0;
    double chiller_min_gap_s = 60.0;
    double min_cool_after_hold_s = 60.0;
    double switch_s = 1.0;
    double band_k = 2.0;                   // 下发目标时目标比釜内低超过这么多，起步就先合 TEC
};

enum class HeatSide { Heater, Tec };
enum class ChillerState { Idle = 0, Starting = 1, Running = 2, Stopping = 3 };

struct SwitchEvent { double t; std::string text; };

class HeatSourceSwitch {
public:
    explicit HeatSourceSwitch(const SwitchParams& p = {}) : p_(p) {}
    // 独立模式（夹套直接控温 / 对拍基准 golden_switch）
    void start(double t, double sp, double tr);
    bool pre(double t);
    void post(double t, double demand);

    // EasyMax 串级三阶段水路与热源协同模式
    void start_coordinated(double t, double sp, double tr, bool is_ramp);
    int pre_coordinated(double t);   // −1 未完成切换，0 刚切到加热棒，1 刚切到 TEC
    void post_coordinated(double t, double sp_cur, double tr_val, double tj, double tj_sp,
                          double xr, double w2, double b_ctrl, double tw, double tau,
                          double d_filt, double last_p_plus_i, double demand,
                          bool in_boost, bool in_brake);

    HeatSide side() const { return side_; }
    bool output_enabled() const { return enabled_; }   // false = 正在扳继电器，这一拍别算 PID、输出给 0
    bool heat_only() const { return heat_only_; }      // true = 要 TEC 但还在等，回路按只加热算
    bool valve_open() const { return valve_open_; }
    ChillerState chiller() const { return chiller_; }
    int flips() const { return flips_; }
    int valve_opens() const { return valve_opens_; }
    bool in_lead() const { return phase_ == Phase::Lead; }
    double lead_until() const { return lead_until_; }
    double chiller_since() const { return chiller_since_; }
    std::vector<SwitchEvent> take_events() { auto e = std::move(events_); events_.clear(); return e; }

private:
    enum class Phase { None, Lead, Switch };
    void tick_water(double t);
    bool cold_start_needed() const;
    void open_water(double t);
    void ensure_water(double t);
    void release_water(double t);
    int signed_side(double t, double d);               // −1 不表态，0 加热棒，1 TEC
    void begin_to_tec(double t, bool from_hold);
    void begin_switch(double t, HeatSide to);
    bool gate_tec(double t);
    void hold(double t);

    SwitchParams p_;
    HeatSide side_ = HeatSide::Heater;
    bool enabled_ = true, heat_only_ = false, holding_ = false;
    int sign_side_ = -1;
    double sign_since_ = 0;
    bool have_heat_since_ = false; double heat_since_ = 0;
    bool have_ep_ = false; double ep_since_ = 0;
    Phase phase_ = Phase::None;
    HeatSide seq_to_ = HeatSide::Tec; double seq_until_ = 0; bool seq_from_hold_ = false, seq_fresh_ = false;
    bool valve_open_ = false, water_closing_ = false;
    ChillerState chiller_ = ChillerState::Idle;
    double chiller_since_ = 0;
    bool have_cold_start_ = false; double last_cold_start_ = 0;
    bool have_to_heater_ = false; double last_to_heater_t_ = -1e9;
    double lead_until_ = -1e9;
    int flips_ = 0;
    int valve_opens_ = 0;
    std::vector<SwitchEvent> events_;
};

}  // namespace tec
