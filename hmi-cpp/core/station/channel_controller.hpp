// 一个工位的控温主循环：把外环、内环、执行机构串成每 0.5 s 一拍的完整流程。
// 它不碰硬件：每拍把读数给它，它给出这一拍该怎么出力、继电器该在哪一侧、冷却水阀开不开。
// 所以同一份代码既能接真设备，也能在测试里对着试算台的逐拍记录核对（tests/golden_channel.cpp）。
//
// 一拍的次序（照原来程序 HostControlLoop 的次序，经试算台核对过）：
//   推执行机构（冷水机 / 阀 / 进行中的切换）→ 每 2 s 算一次外环（釜内控温时）→ 输出开着就算内环 → 把这一拍的功率告诉执行机构 → 出命令
#pragma once
#include "../control/gain_table.hpp"
#include "../control/heat_source_switch.hpp"
#include "../control/inner_loop.hpp"
#include "../control/outer_loop.hpp"
#include <memory>
#include <optional>
#include <string>
#include <vector>

namespace tec {

enum class ControlMode {
    Off,
    Reactor,   // 釜内温度 Tr：串级
    Jacket,    // 夹套温度 Tj：直接控夹套
    Reflux,    // 蒸回流：夹套始终比釜内高 ΔT，沸点不知道的时候用
    Crystal,   // 结晶：釜内温度按分段曲线走（每段一个目标温度 + 用时）
};

// 到达方式：尽快 / 按速率 / 按时长（缺省「尽快」，用户 2026-11-23 定的）
enum class Arrive { Fast, Rate, Duration };

struct Target {
    double value = 0;
    Arrive arrive = Arrive::Fast;
    double rate_k_per_min = 0;     // 按速率时用
    double duration_min = 0;       // 按时长时用
};

struct ProfileSegment { double target = 0; double minutes = 0; };

struct ChannelConfig {
    OuterParams outer;            // 外环（含快速升温 outer.fast）
    SwitchParams sw;
    double inner_period_s = 0.5;
    double outer_period_s = 2.0;
    double max_duty = 90.0;       // 占空比上限 ±90 %
    double jacket_sp_min = -50.0, jacket_sp_max = 200.0;   // 夹套目标的硬范围（上限 200 ℃ 是用户定的）
};

struct ChannelCommand {
    double device_duty = 0;       // 写给温控器的带符号占空比（%）：正 = TEC 制冷，负 = 加热棒（设备上就是这个方向）
    bool output_on = false;       // 温控器输出开不开；扳继电器那约 1 s 是关的
    HeatSide side = HeatSide::Heater;   // 继电器该在哪一侧
    bool valve_open = false;      // 冷却水阀
    ChillerState chiller = ChillerState::Idle;
    double jacket_sp = 0;         // 这一拍的夹套目标（显示、记录用）
    std::vector<std::string> notes;     // 这一拍新出的事件 / 报警文字
};

class ChannelController {
public:
    ChannelController(const GainTable& table, const ChannelConfig& cfg);
    // 开始控温。t 用同一条单调递增的时间线（秒）
    void start(ControlMode mode, double setpoint);                // 尽快到达
    void start(ControlMode mode, const Target& target);           // 釜内 / 夹套，带到达方式
    void start_reflux(double delta_k);                            // 蒸回流：夹套 = 釜内 + delta_k
    void start_profile(const std::vector<ProfileSegment>& segs);  // 结晶：分段曲线，从开控那一拍的釜内温度起
    void set_setpoint(double setpoint);                           // 在控时改目标：尽快到达
    void stop();
    bool running() const { return mode_ != ControlMode::Off; }
    ControlMode mode() const { return mode_; }
    double setpoint() const { return sp_; }                       // 最终目标（结晶时 = 当前这一段的目标）
    double setpoint_now() const { return sp_now_; }               // 这一拍的目标：斜坡上的当前值
    int segment_index() const { return seg_; }                    // 结晶：现在走到第几段（从 0 数）；走完了 = 段数
    double segment_remaining_s() const { return seg_left_s_; }
    // 正在走的那条结晶曲线和它的起点温度（开控那一拍的读数）。界面和 PC 端要照着画曲线、标出走到哪了
    const std::vector<ProfileSegment>& profile() const { return segs_; }
    double profile_start() const { return prof_from_; }
    bool profile_done() const { return mode_ == ControlMode::Crystal && seg_ >= static_cast<int>(segs_.size()); }
    bool ramp_done() const { return ramp_done_; }                 // 按速率 / 按时长：斜坡走完了没有

    // 每 inner_period_s 调一次。tr 没读到就给空（釜内控温时外环按「没读数」走）
    ChannelCommand tick(double tj, std::optional<double> tr);

    // 显示 / 记录用的内部量
    const OuterLoop* outer() const { return outer_.get(); }
    double pid_out() const { return pid_out_; }
    double demand() const { return demand_; }
    const HeatSourceSwitch& heat_switch() const { return *sw_; }

private:
    const GainTable& table_;
    ChannelConfig cfg_;
    ControlMode mode_ = ControlMode::Off;
    double sp_ = 0;
    std::unique_ptr<OuterLoop> outer_;
    std::unique_ptr<InnerLoop> inner_;
    std::unique_ptr<HeatSourceSwitch> sw_;
    long k_ = 0;                  // 开控以来第几拍
    int outer_every_ = 4;
    double tj_sp_ = 0, pid_out_ = 0, demand_ = 0;
    std::optional<double> tr_last_;
    bool inner_sat_low_ = false;
    void begin(ControlMode mode);
    void advance_trajectory(double t, double pv);
    Target target_;
    double sp_now_ = 0, sp_rate_ = 0, ramp_from_ = 0, ramp_rate_ = 0, ramp_t0_ = 0, reflux_delta_ = 0;
    bool ramp_armed_ = false, ramp_done_ = true;
    std::vector<ProfileSegment> segs_;
    int seg_ = 0;
    double seg_left_s_ = 0, seg_t0_ = 0, seg_from_ = 0, prof_from_ = 0;
};

}  // namespace tec
