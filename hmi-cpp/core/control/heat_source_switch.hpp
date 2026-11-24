// 执行机构这一层：加热棒和 TEC 之间怎么切、冷却水什么时候开关。一个工位一个。
//
// 规矩（除最后一条外都是从原来的程序搬过来的，经试算台 session.py 核对过真机 11-10 那一炉）：
//   · 继电器跟功率符号走：占空比绝对值不到 1 % 不表态；指向另一侧连续 3 s 才扳（免得在零附近来回扳）
//   · 接 TEC 之前先通水：开冷却水阀（有冷水机就一起开）→ 等 5 s → 关输出、断加热棒、合 TEC、开输出
//     等水那 5 s 加热棒继电器还合着；等完了功率要是已经不指向 TEC 就不切，水收回去
//   · 扳一次继电器输出关约 1 s，这段时间不算 PID；恢复时微分历史重置、积分留着
//   · 回加热棒：断 TEC → 停冷水机 → 冷水机停稳了才关阀（冷水机还在转就关阀会憋泵）→ 合加热棒
//   · 冷水机两次冷启动之间至少隔 60 s：没到点就先挂着（回路按只加热算），到点再接；这样接上的 TEC 至少留 60 s
//   · 冷却水跟着 TEC 制冷自动开关，不问人（用户 2026-11-24 定的，取代了以前「第一次要制冷先弹窗问」那一条）
//
// 这一层只出「该在哪一侧、输出开不开、阀开不开、冷水机转不转」，不直接碰硬件。接到继电器模块上的时候注意：
// 使能继电器 DO2 / DO3 程序绝不去关（用户定的）；所以写继电器只许一路一路写，不许用「八路全断」。
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
    // 下发目标那一刻
    void start(double t, double sp, double tr);
    // 每拍算 PID 之前调：推冷水机 / 阀、推进行中的切换。返回 true = 这一拍恢复输出，内环微分要重置
    bool pre(double t);
    // 每拍算完 PID 之后调：demand = 这一拍的带符号占空比（正 = 加热棒，负 = TEC）
    void post(double t, double demand);

    HeatSide side() const { return side_; }
    bool output_enabled() const { return enabled_; }   // false = 正在扳继电器，这一拍别算 PID、输出给 0
    bool heat_only() const { return heat_only_; }      // true = 要 TEC 但还在等，回路按只加热算
    bool valve_open() const { return valve_open_; }
    ChillerState chiller() const { return chiller_; }
    int flips() const { return flips_; }
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
    double lead_until_ = -1e9;
    int flips_ = 0;
    std::vector<SwitchEvent> events_;
};

}  // namespace tec
