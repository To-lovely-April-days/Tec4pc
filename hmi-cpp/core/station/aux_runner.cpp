#include "aux_runner.hpp"
#include "station.hpp"
#include <algorithm>
#include <cmath>
#include <limits>

namespace tec {

void AuxRunner::set_stir(int ch, double rpm) {
    std::lock_guard<std::mutex> g(m_);
    snap_.stir_set_rpm[ch] = rpm <= 0 ? 0.0 : std::min(cfg_.stir_max_rpm, std::max(cfg_.stir_min_rpm, rpm));
    stir_dirty_[ch] = true;            // 真正写设备放到 step() 里：串口只让轮询那一个线程碰
}

bool AuxRunner::start_dose(int ch, const DoseParams& d) {
    std::lock_guard<std::mutex> g(m_);
    DoseStatus& s = snap_.dose[ch];
    auto reject = [&](const char* why) { s = DoseStatus(); s.note = why; return false; };
    if (d.pump != 1 && d.pump != 2) return reject("泵号只能是 1 或 2");
    if (job_[1 - ch].active && job_[1 - ch].p.pump == d.pump) return reject("这台泵另一个通道正在用");
    const double vol = d.mode == DoseMode::PhHold ? d.max_volume_ml : d.volume_ml;
    if (!(vol > 0)) return reject("加料体积要大于 0");
    if (vol > cfg_.single_max_volume_ml) return reject("超过了单次最大体积");
    if (d.mode == DoseMode::Rate && !(d.rate_ml_min > 0)) return reject("流量要大于 0");
    job_[ch] = Job(); job_[ch].active = true; job_[ch].p = d;
    s = DoseStatus(); s.active = true; s.done = false; s.pump = d.pump; s.mode = d.mode; s.target_ml = vol;
    return true;
}

void AuxRunner::stop_dose(int ch) {
    std::lock_guard<std::mutex> g(m_);
    if (!job_[ch].active) return;
    job_[ch].active = false;           // 泵在下一次 step() 里关
    snap_.dose[ch].active = false; snap_.dose[ch].done = true; snap_.dose[ch].note = "加料被停止";
}

bool AuxRunner::dose_done(int ch) const { std::lock_guard<std::mutex> g(m_); return snap_.dose[ch].done; }

double AuxRunner::dose_progress(int ch) const {
    std::lock_guard<std::mutex> g(m_);
    const DoseStatus& s = snap_.dose[ch];
    if (s.done) return 1.0;
    if (s.mode == DoseMode::PhHold) { const double dur = job_[ch].p.hold_min * 60.0; return dur > 0 ? std::min(1.0, s.elapsed_s / dur) : 1.0; }
    return s.target_ml > 0 ? std::min(1.0, s.dosed_ml / s.target_ml) : 1.0;
}

AuxSnapshot AuxRunner::snapshot() const { std::lock_guard<std::mutex> g(m_); return snap_; }

// 只在状态真变了的时候写设备：每秒重复写同一个值没有意义，还多占总线
void AuxRunner::pump(int p, bool on, double rate) {
    if (on && std::fabs(pump_rate_[p] - rate) > 1e-9) { hw_.set_pump_rate(p, rate); pump_rate_[p] = rate; }
    if (pump_state_[p] != (on ? 1 : 0)) { hw_.set_pump(p, on); pump_state_[p] = on ? 1 : 0; }
}

void AuxRunner::step(double t) {
    std::lock_guard<std::mutex> g(m_);
    for (int ch = 0; ch < 2; ch++) {
        try {
            if (stir_dirty_[ch]) { hw_.set_stir(ch, snap_.stir_set_rpm[ch]); stir_dirty_[ch] = false; }
            snap_.stir[ch] = hw_.read_stir(ch);
        } catch (const std::exception& e) { snap_.stir[ch] = StirReading(); snap_.stir[ch].fault_text = e.what(); }
        try { snap_.ph[ch] = hw_.read_ph(ch); } catch (const std::exception&) { snap_.ph[ch] = PhReading(); }
    }
    bool feed_ok = true;
    try { snap_.feed = hw_.read_feed(); feed_ok = snap_.feed.ok; } catch (const std::exception& e) { snap_.feed = FeedReading(); snap_.feed.alarm_text = e.what(); feed_ok = false; }
    bool used[3] = {false, false, false};
    for (int ch = 0; ch < 2; ch++) {
        Job& j = job_[ch];
        DoseStatus& s = snap_.dose[ch];
        if (!j.active) continue;
        const int p = j.p.pump;
        used[p] = true;
        try {
            if (!feed_ok) {                      // 读不到进料系统：不知道加了多少，只能停
                pump(p, false, 0); j.active = false; s.active = false; s.done = true; s.note = "读不到进料系统，加料已停止"; continue;
            }
            if (!j.have_total0) { j.have_total0 = true; j.total0 = snap_.feed.total_ml[p - 1]; j.t0 = t; }
            s.dosed_ml = snap_.feed.total_ml[p - 1] - j.total0;
            s.elapsed_s = t - j.t0;
            if (!snap_.feed.alarm_text.empty()) { pump(p, false, 0); j.active = false; s.active = false; s.done = true; s.note = "进料系统报警：" + snap_.feed.alarm_text; continue; }
            if (j.p.mode == DoseMode::PhHold) {
                const bool time_up = s.elapsed_s >= j.p.hold_min * 60.0;
                const bool full = s.dosed_ml >= j.p.max_volume_ml;
                bool want = false; double rate = j.p.max_rate_ml_min;
                const auto ph = snap_.ph[ch].ph;
                if (!time_up && !full && ph) {
                    const double e = *ph - j.p.ph_target;            // > 0：pH 偏高
                    if (j.dir == 0 && std::fabs(e) > j.p.ph_band) j.dir = e > 0 ? -1 : +1;   // 认定这种料液往哪边推（−1 = 把 pH 压低）
                    const double need = j.dir < 0 ? e : -e;          // 在「料液能纠正的那一边」偏了多少
                    // 偏出死区才开泵，回到目标才停：在死区里不来回开关
                    want = j.pump_on ? need > 0 : need > j.p.ph_band;
                    // 离目标越近加得越慢，免得冲过头（冲过头这种料液纠不回来）
                    rate = j.p.max_rate_ml_min * std::min(1.0, std::max(0.1, need / (4.0 * std::max(0.01, j.p.ph_band))));
                }
                if (!ph && !time_up) s.note = "读不到 pH，暂停加料";
                else if (full && !time_up) s.note = "已加到最大总量，不再加";
                else s.note.clear();
                pump(p, want, std::min(rate, cfg_.pump_max_rate_ml_min));
                j.pump_on = want;
                if (time_up) { pump(p, false, 0); j.active = false; s.active = false; s.done = true; }
            } else {
                const double rate = j.p.mode == DoseMode::AtOnce ? cfg_.pump_max_rate_ml_min : std::min(j.p.rate_ml_min, cfg_.pump_max_rate_ml_min);
                if (s.dosed_ml >= j.p.volume_ml) { pump(p, false, 0); j.active = false; s.active = false; s.done = true; }
                else pump(p, true, rate);
            }
        } catch (const std::exception& e) {
            j.active = false; s.active = false; s.done = true; s.note = std::string("写进料系统出错：") + e.what();
        }
    }
    // 没有任务在用的泵一律关着（包括被 stop_dose 停掉的）
    for (int p = 1; p <= 2; p++)
        if (!used[p] && pump_state_[p] != 0) { try { hw_.set_pump(p, false); pump_state_[p] = 0; } catch (const std::exception&) {} }
}

// ── 序列引擎的动作接到整机上 ──
void StationChannelOps::start_temp(ControlMode mode, const Target& t) { st_.start(ch_, mode, t); }
void StationChannelOps::start_reflux(double delta_k) { st_.start_reflux(ch_, delta_k); }
void StationChannelOps::start_profile(const std::vector<ProfileSegment>& segs) { st_.start_profile(ch_, segs); }
void StationChannelOps::stop_temp() { st_.stop(ch_); }
bool StationChannelOps::temp_running() const { return st_.snapshot().ch[ch_].running; }
double StationChannelOps::temp_pv(ControlMode mode) const {
    const ChannelSnapshot s = st_.snapshot().ch[ch_];
    if (mode == ControlMode::Jacket) return s.tj;
    return s.tr ? *s.tr : std::numeric_limits<double>::quiet_NaN();
}
bool StationChannelOps::ramp_done() const { return st_.snapshot().ch[ch_].ramp_done; }
bool StationChannelOps::profile_done() const { return st_.snapshot().ch[ch_].profile_done; }

}  // namespace tec
