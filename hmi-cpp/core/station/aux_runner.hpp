// 搅拌、pH、加料这三样的运行层：每秒轮询一次读数，管定量加料和 pH 反馈加料的过程。
// 和控温那条线分开：这几台设备各在各的串口上，哪台不通都不该拖慢 0.5 s 的控温节拍。
//
// 加料怎么做的（进料系统自己没有「加多少」这个设置，原来的程序也是上层盯着做）：
//   按速率    设流量 → 开泵 → 记下起点累计量 → 累计量涨够了就停泵
//   一次加入  同上，流量取泵的最大流量（泵送液要时间，没有真正的瞬时加入）
//   pH 反馈   开始那一刻看 pH 在目标的哪一边，就认定这种料液是把 pH 往目标那边推的；之后 pH 偏在那一边超过死区就开泵，
//             回到目标就停；加够最大总量就不再加；维持够时长这一步结束。
//             （原来的程序里 pH 反馈只有参数定义、没有实现，「往哪边推」这条是 2026-11-24 新定的，等用户确认）
#pragma once
#include "../sequence/sequence.hpp"
#include <mutex>
#include <optional>
#include <string>

namespace tec {

struct StirReading { bool ok = false; double rpm = 0, current_a = 0; int fault = 0; std::string fault_text; };
struct PhReading { std::optional<double> ph; std::optional<double> temp_c; };
struct FeedReading { bool ok = false; double total_ml[2] = {0, 0}, flow_ml_min[2] = {0, 0}; bool on[2] = {false, false}; std::string alarm_text; };

class AuxHardware {
public:
    virtual ~AuxHardware() = default;
    virtual void set_stir(int ch, double rpm) = 0;          // rpm = 0 是停
    virtual StirReading read_stir(int ch) = 0;
    virtual PhReading read_ph(int ch) = 0;
    virtual FeedReading read_feed() = 0;
    virtual void set_pump_rate(int pump, double ml_min) = 0;   // pump：1 / 2
    virtual void set_pump(int pump, bool on) = 0;
};

struct AuxConfig {
    double pump_max_rate_ml_min = 50.0;      // 泵最大流量
    double single_max_volume_ml = 2000.0;    // 单次最大体积：一步加料不许超过它（填错一个小数点不至于把料全打进去）
    double stir_min_rpm = 0, stir_max_rpm = 1000;
};

struct DoseStatus {
    bool active = false, done = true;
    int pump = 0;
    DoseMode mode = DoseMode::Rate;
    double dosed_ml = 0, target_ml = 0, elapsed_s = 0;
    std::string note;                        // 出了什么事（被拒绝、到上限、泵报警）
};

struct AuxSnapshot {
    StirReading stir[2];
    double stir_set_rpm[2] = {0, 0};
    PhReading ph[2];
    FeedReading feed;
    DoseStatus dose[2];                      // 按通道
};

class AuxRunner {
public:
    AuxRunner(AuxHardware& hw, const AuxConfig& cfg = {}) : hw_(hw), cfg_(cfg) {}
    void set_stir(int ch, double rpm);
    bool start_dose(int ch, const DoseParams& d);     // 被拒绝返回 false，原因在 snapshot().dose[ch].note
    void stop_dose(int ch);
    bool dose_done(int ch) const;
    double dose_progress(int ch) const;
    AuxSnapshot snapshot() const;
    void step(double t);                              // 每秒调一次

private:
    struct Job { bool active = false; DoseParams p; double t0 = 0, total0 = 0; bool have_total0 = false, pump_on = false; int dir = 0; };
    void pump(int pump, bool on, double rate);
    AuxHardware& hw_;
    AuxConfig cfg_;
    mutable std::mutex m_;
    AuxSnapshot snap_;
    Job job_[2];
    bool stir_dirty_[2] = {false, false};
    double pump_rate_[3] = {0, -1, -1};
    int pump_state_[3] = {0, -1, -1};
};

// 序列引擎要的那组动作，接到整机上：控温走 Station，搅拌和加料走 AuxRunner
class Station;
class StationChannelOps : public ChannelOps {
public:
    StationChannelOps(Station& st, AuxRunner& aux, int ch) : st_(st), aux_(aux), ch_(ch) {}
    void start_temp(ControlMode mode, const Target& t) override;
    void start_reflux(double delta_k) override;
    void start_profile(const std::vector<ProfileSegment>& segs) override;
    void stop_temp() override;
    bool temp_running() const override;
    double temp_pv(ControlMode mode) const override;
    bool ramp_done() const override;
    bool profile_done() const override;
    void set_stir(double rpm) override { aux_.set_stir(ch_, rpm); }
    void start_dose(const DoseParams& d) override { aux_.start_dose(ch_, d); }
    bool dose_done() const override { return aux_.dose_done(ch_); }
    double dose_progress() const override { return aux_.dose_progress(ch_); }
    void stop_dose() override { aux_.stop_dose(ch_); }

private:
    Station& st_;
    AuxRunner& aux_;
    int ch_;
};

}  // namespace tec
