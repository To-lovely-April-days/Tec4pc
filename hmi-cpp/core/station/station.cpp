#include "station.hpp"
#include <chrono>
#include <cmath>
#include <cstdio>
#include <thread>

namespace tec {

Station::Station(ReactorHardware& hw, const GainTable& table, const ChannelConfig& cfg)
    : hw_(hw), cfg_(cfg), ctl_{ChannelController(table, cfg), ChannelController(table, cfg)} {}

void Station::started_locked(int ch, const char* what) {
    begun_[ch] = false;              // 设备那边的开控动作放到下一拍在节拍线程里做：串口只让一个线程碰
    tr_missing_since_[ch] = -1;
    tr_hist_[ch].clear();
    events_.push_back({snap_.t, ch, what});
}

void Station::start(int ch, ControlMode mode, double setpoint) {
    Target t; t.value = setpoint;
    start(ch, mode, t);
}

void Station::start(int ch, ControlMode mode, const Target& target) {
    std::lock_guard<std::mutex> g(m_);
    ctl_[ch].start(mode, target);
    started_locked(ch, mode == ControlMode::Reactor ? "开始釜内控温" : "开始夹套控温");
}

void Station::start_reflux(int ch, double delta_k) {
    std::lock_guard<std::mutex> g(m_);
    ctl_[ch].start_reflux(delta_k);
    started_locked(ch, "开始蒸回流");
}

void Station::start_profile(int ch, const std::vector<ProfileSegment>& segs) {
    std::lock_guard<std::mutex> g(m_);
    ctl_[ch].start_profile(segs);
    started_locked(ch, "开始结晶曲线");
}

void Station::set_limits(int ch, const SafetyLimits& lim) { std::lock_guard<std::mutex> g(m_); lim_[ch] = lim; }
SafetyLimits Station::limits(int ch) const { std::lock_guard<std::mutex> g(m_); return lim_[ch]; }

std::vector<Alarm> Station::alarms() const {
    std::lock_guard<std::mutex> g(m_);
    std::vector<Alarm> out;
    for (auto& a : alarms_) if (!a.acked) out.push_back(a);
    for (auto& a : alarms_) if (a.acked) out.push_back(a);
    return out;
}

void Station::ack_alarm(int id) {
    std::lock_guard<std::mutex> g(m_);
    for (auto& a : alarms_) if (id < 0 || a.id == id) a.acked = true;
}

void Station::trip(int ch, double t, const std::string& title) {
    Alarm a;
    a.id = next_alarm_++; a.t = t; a.ch = ch; a.title = title; a.action = "停止控温，搅拌保持";
    alarms_.push_back(a);
    if (alarms_.size() > 200) alarms_.erase(alarms_.begin());
    stop_locked(ch, "安全联锁：" + title);
}

static std::string f1(double v) { char b[32]; std::snprintf(b, sizeof b, "%.1f", v); return b; }

// 安全层：和控温算法分开，只看读数和限值。算法出了错、目标给错了，这一层照样拦得住。
void Station::check_safety(int ch, double t, const ReactorReadings& r) {
    if (!ctl_[ch].running()) return;
    const SafetyLimits& L = lim_[ch];
    const double tj = r.tj[ch];
    // 冷媒过热只在这一路正用 TEC 制冷时才算数：加热棒升温用不着冷却水，水温高低和它没关系
    if (r.coolant_c && *r.coolant_c > L.tc_max && snap_.ch[ch].side == HeatSide::Tec && snap_.ch[ch].output_on && snap_.ch[ch].duty > 0)
        return trip(ch, t, "冷媒温度过高 " + f1(*r.coolant_c) + " ℃（上限 " + f1(L.tc_max) + " ℃，TEC 的热排不出去）");
    if (tj > L.tj_max) return trip(ch, t, "Tj 超过上限 " + f1(L.tj_max) + " ℃（实测 " + f1(tj) + " ℃）");
    if (tj < L.tj_min) return trip(ch, t, "Tj 低于下限 " + f1(L.tj_min) + " ℃（实测 " + f1(tj) + " ℃）");
    if (r.tr[ch]) {
        const double tr = *r.tr[ch];
        tr_missing_since_[ch] = -1;
        if (tr > L.tr_max) return trip(ch, t, "Tr 超过上限 " + f1(L.tr_max) + " ℃（实测 " + f1(tr) + " ℃）");
        if (tr < L.tr_min) return trip(ch, t, "Tr 低于下限 " + f1(L.tr_min) + " ℃（实测 " + f1(tr) + " ℃）");
        auto& hst = tr_hist_[ch];
        hst.push_back({t, tr});
        while (hst.front().first < t - 60.0) hst.pop_front();
        if (L.rate_max_k_per_min > 0 && t - hst.front().first >= 30.0) {      // 至少攒够 30 s 才算，免得一两个读数的抖动就跳
            const double rate = (tr - hst.front().second) / (t - hst.front().first) * 60.0;
            if (std::fabs(rate) > L.rate_max_k_per_min) return trip(ch, t, "釜内变温速率超过上限 " + f1(L.rate_max_k_per_min) + " K/min（实测 " + f1(rate) + " K/min）");
        }
    } else if (ctl_[ch].mode() != ControlMode::Jacket) {
        // 釜内控温、蒸回流、结晶都要釜内读数；夹套控温不要
        if (tr_missing_since_[ch] < 0) tr_missing_since_[ch] = t;
        else if (t - tr_missing_since_[ch] >= L.tr_lost_grace_s) return trip(ch, t, "釜内温度连续 " + f1(L.tr_lost_grace_s) + " s 读不到");
    }
}

void Station::set_setpoint(int ch, double setpoint) {
    std::lock_guard<std::mutex> g(m_);
    ctl_[ch].set_setpoint(setpoint);
}

void Station::stop(int ch) {
    std::lock_guard<std::mutex> g(m_);
    stop_locked(ch, "停止控温");
}

void Station::stop_locked(int ch, const std::string& why) {
    if (!ctl_[ch].running()) return;
    ctl_[ch].stop();
    events_.push_back({snap_.t, ch, why});
}

StationSnapshot Station::snapshot() const {
    std::lock_guard<std::mutex> g(m_);
    return snap_;
}

std::vector<StationEvent> Station::take_events() {
    std::lock_guard<std::mutex> g(m_);
    std::vector<StationEvent> out(events_.begin(), events_.end());
    events_.clear();
    return out;
}

void Station::step() {
    std::lock_guard<std::mutex> g(m_);
    const double t = k_ * cfg_.inner_period_s;
    snap_.t = t;
    ReactorReadings r;
    bool ok = true;
    try {
        r = hw_.read();
        fails_ = 0;
    } catch (const std::exception& e) {
        ok = false;
        fails_++;
        snap_.problem = std::string("读不到温控器：") + e.what();
        if (fails_ == max_comm_failures)
            for (int ch = 0; ch < 2; ch++) stop_locked(ch, "连续读不到温控器，已停止控温");
    }
    snap_.comm_ok = ok;
    if (ok) { snap_.problem = r.problem; snap_.board_c = r.board_c; snap_.coolant_c = r.coolant_c; }
    bool valve = false, chiller = false;
    for (int ch = 0; ch < 2; ch++) {
        ChannelSnapshot& s = snap_.ch[ch];
        if (ok) { s.tj = r.tj[ch]; s.tr = r.tr[ch]; }
        if (ok && !std::isnan(r.tj[ch])) check_safety(ch, t, r);
        const bool run = ctl_[ch].running();
        try {
            if (run && ok && !std::isnan(r.tj[ch])) {
                if (!begun_[ch]) { hw_.begin_control(ch); begun_[ch] = true; }
                const ChannelCommand cmd = ctl_[ch].tick(r.tj[ch], r.tr[ch]);
                hw_.apply(ch, cmd);
                for (auto& n : cmd.notes) events_.push_back({t, ch, n});
                s.jacket_sp = cmd.jacket_sp; s.duty = cmd.device_duty; s.side = cmd.side; s.output_on = cmd.output_on;
                if (const OuterLoop* o = ctl_[ch].outer()) { s.b_hat = o->b(); s.tau_hat = o->tau(); s.boost = o->boost(); s.peak = o->peak(); }
                valve = valve || cmd.valve_open;
                chiller = chiller || cmd.chiller == ChillerState::Starting || cmd.chiller == ChillerState::Running;
            } else if (!run && begun_[ch]) {
                hw_.end_control(ch);         // 刚停下来的这一拍：关输出、断开这一路的两只继电器
                begun_[ch] = false;
                s.duty = 0; s.output_on = false;
            }
            // 在控但这一拍没读到数：什么都不写，设备保持上一拍的输出；连续读不到由上面的计数停控
        } catch (const std::exception& e) {
            snap_.problem = std::string("写设备出错：") + e.what();
            snap_.comm_ok = false;
        }
        s.running = ctl_[ch].running(); s.mode = ctl_[ch].mode(); s.setpoint = ctl_[ch].setpoint();
        s.setpoint_now = ctl_[ch].setpoint_now(); s.segment = ctl_[ch].segment_index(); s.segment_remaining_s = ctl_[ch].segment_remaining_s();
        s.ramp_done = ctl_[ch].ramp_done(); s.profile_done = ctl_[ch].profile_done();
        if (s.running && s.mode == ControlMode::Crystal) { s.profile = ctl_[ch].profile(); s.profile_start = ctl_[ch].profile_start(); }
        else s.profile.clear();
    }
    try {
        hw_.set_water(valve, chiller);
        snap_.valve_open = valve; snap_.chiller_run = chiller;
    } catch (const std::exception& e) {
        snap_.problem = std::string("写冷却水出错：") + e.what();
        snap_.comm_ok = false;
    }
    k_++;
}

void Station::run(const std::atomic<bool>& quit) {
    using clock = std::chrono::steady_clock;
    const auto period = std::chrono::duration_cast<clock::duration>(std::chrono::duration<double>(cfg_.inner_period_s));
    auto next = clock::now();
    while (!quit.load()) {
        step();
        next += period;                       // 按固定的时刻表走，不是「干完再睡 0.5 s」：通信慢了不会让节拍越拖越长
        const auto now = clock::now();
        if (next < now) next = now;           // 这一拍超时了：不补拍，从现在重新排
        std::this_thread::sleep_until(next);
    }
    stop(0); stop(1);
    step();                                   // 再跑一拍，把「停控」真正写到设备上
}

}  // namespace tec
