// 整台设备（两个工位）的控温节拍：每 0.5 s 读一次数、两个工位各算一拍、把命令写下去。
// 界面和 TCP 不直接碰控制器：下命令走这里的 start / stop / set_setpoint，看数据取 snapshot()，都是线程安全的。
#pragma once
#include "channel_controller.hpp"
#include "reactor_hardware.hpp"
#include <functional>
#include <atomic>
#include <deque>
#include <mutex>
#include <string>
#include <vector>

namespace tec {

struct ChannelSnapshot {
    bool running = false;
    ControlMode mode = ControlMode::Off;
    double setpoint = 0;
    double tj = 0;
    std::optional<double> tr;
    double jacket_sp = 0;
    double duty = 0;                 // 设备占空比：正 = TEC 制冷，负 = 加热棒
    HeatSide side = HeatSide::Heater;
    bool output_on = false;
    // 外环的内部量（PID 整定页、诊断用）
    double b_hat = 0, tau_hat = 0, boost = 0, peak = 0;
    double setpoint_now = 0;         // 斜坡 / 曲线上这一拍的目标
    int segment = 0;                 // 结晶：第几段（从 0 数）
    double segment_remaining_s = 0;
    bool ramp_done = true, profile_done = false;
    std::vector<ProfileSegment> profile;   // 结晶模式在跑时：正在走的曲线；别的时候是空的
    double profile_start = 0;        // 那条曲线的起点温度
};

struct StationSnapshot {
    double t = 0;                    // 开机以来的秒数
    ChannelSnapshot ch[2];
    bool valve_open = false;
    bool chiller_run = false;
    double board_c = 0;
    std::optional<double> coolant_c;       // 冷媒温度；没接冷水机就是空的
    bool comm_ok = true;
    std::string problem;
};

// 安全限值，每个工位一组。超了就停止这一路的控温并报警（搅拌不归这里管，照常转）。
// 夹套的两条是设备底线，操作人改不了；釜内的两条和变温速率操作人可以在底线之内收紧。
struct SafetyLimits {
    double tr_min = -40.0, tr_max = 180.0;   // 釜内温度范围（℃）
    double tj_min = -40.0, tj_max = 200.0;   // 夹套温度范围（℃）。上限 200 是用户定的（玻璃釜 + 导热油）
    double rate_max_k_per_min = 0.0;         // 釜内变温速率上限（K/min，按最近 60 s 算）；0 = 不检测
    double tr_lost_grace_s = 30.0;
    double tc_max = 45.0;                    // 冷媒（冷水机冷却水）温度上限：正在用 TEC 制冷时水温超过它，热排不出去，停控并报警           // 需要釜内读数的控温方式下，釜内连续这么久读不到就停控
};

// 一条报警。触发后一直留着，直到操作人确认（设计稿「安全联锁」弹窗的「复位（确认）」）
struct Alarm {
    int id = 0;
    double t = 0;
    int ch = 0;
    std::string title;      // 如「Tr 超过上限 180.0 ℃（实测 180.6 ℃）」
    std::string action;     // 安全层做了什么，如「停止控温，搅拌保持」
    bool acked = false;
};

struct StationEvent { double t; int ch; std::string text; };   // ch：0 / 1；−1 = 整机

class Station {
public:
    Station(ReactorHardware& hw, const GainTable& table, const ChannelConfig& cfg);
    void start(int ch, ControlMode mode, double setpoint);
    void start(int ch, ControlMode mode, const Target& target);
    void start_reflux(int ch, double delta_k);
    void start_profile(int ch, const std::vector<ProfileSegment>& segs);
    void set_limits(int ch, const SafetyLimits& lim);
    SafetyLimits limits(int ch) const;
    std::vector<Alarm> alarms() const;          // 还没确认的在前
    void ack_alarm(int id);                      // id < 0 = 全部确认
    void set_setpoint(int ch, double setpoint);
    void stop(int ch);
    StationSnapshot snapshot() const;
    std::vector<StationEvent> take_events();

    // 在控制那把锁里面做一件事（换增益表之类要保证这时候没有哪一拍正算到一半）
    void locked(const std::function<void()>& f) { std::lock_guard<std::mutex> g(m_); f(); }
    void step();                     // 跑一拍。测试里直接调；真机上由 run() 按节拍调
    void run(const std::atomic<bool>& quit);   // 按 0.5 s 的节拍一直跑到 quit 为真；退出前把两个工位都停掉

    int max_comm_failures = 6;       // 连续这么多拍读不到温控器（约 3 s）就停控：读不到温度还继续出力是不能接受的

private:
    void stop_locked(int ch, const std::string& why);
    void started_locked(int ch, const char* what);
    void check_safety(int ch, double t, const ReactorReadings& r);
    void trip(int ch, double t, const std::string& title);
    SafetyLimits lim_[2];
    std::vector<Alarm> alarms_;
    int next_alarm_ = 1;
    double tr_missing_since_[2] = {-1, -1};
    std::deque<std::pair<double, double>> tr_hist_[2];
    ReactorHardware& hw_;
    ChannelConfig cfg_;
    ChannelController ctl_[2];
    mutable std::mutex m_;
    StationSnapshot snap_;
    std::deque<StationEvent> events_;
    bool begun_[2] = {false, false};
    int fails_ = 0;
    long k_ = 0;
};

}  // namespace tec
