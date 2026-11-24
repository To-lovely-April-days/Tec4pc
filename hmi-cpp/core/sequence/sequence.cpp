#include "sequence.hpp"
#include <algorithm>
#include <cmath>

namespace tec {

bool SequenceRunner::load(const std::vector<Step>& steps) {
    if (st_.state == SeqState::Running || st_.state == SeqState::WaitingConfirm) return false;   // 已开始的序列不可增改
    if (static_cast<int>(steps.size()) > kMaxSteps) return false;
    steps_ = steps;
    st_ = SeqStatus();
    st_.count = static_cast<int>(steps_.size());
    return true;
}

bool SequenceRunner::start(double t) {
    if (steps_.empty() || st_.state == SeqState::Running || st_.state == SeqState::WaitingConfirm) return false;
    st_ = SeqStatus();
    st_.count = static_cast<int>(steps_.size());
    st_.state = SeqState::Running;
    t_start_ = t;
    enter(t);
    return true;
}

void SequenceRunner::end(double) {
    if (st_.state == SeqState::Idle) return;
    ops_.stop_dose();
    ops_.stop_temp();
    st_.state = SeqState::Aborted;
    st_.note = "操作人结束了序列";
}

void SequenceRunner::confirm() { confirmed_ = true; }

// 进入第 index 步：把这一步要做的动作发出去
void SequenceRunner::enter(double t) {
    step_t0_ = t; in_band_since_ = -1; confirmed_ = false; have_start_pv_ = false;
    st_.step_elapsed_s = 0; st_.step_progress = 0; st_.note.clear();
    const Step& s = steps_[st_.index];
    switch (s.kind) {
        case StepKind::Reactor: case StepKind::Jacket:
            if (s.stir_rpm >= 0) ops_.set_stir(s.stir_rpm);
            ops_.start_temp(s.kind == StepKind::Reactor ? ControlMode::Reactor : ControlMode::Jacket, s.target);
            break;
        case StepKind::Reflux:
            if (s.stir_rpm >= 0) ops_.set_stir(s.stir_rpm);
            ops_.start_reflux(s.reflux_delta_k);
            break;
        case StepKind::Crystal:
            if (s.stir_rpm >= 0) ops_.set_stir(s.stir_rpm);
            ops_.start_profile(s.profile);
            break;
        case StepKind::Stir: ops_.set_stir(std::max(0.0, s.stir_rpm)); break;
        case StepKind::Dose: ops_.start_dose(s.dose); break;
        case StepKind::Pause: st_.state = SeqState::WaitingConfirm; st_.note = s.message.empty() ? "等待操作人确认" : s.message; break;
        case StepKind::Wait: break;
    }
}

void SequenceRunner::tick(double t) {
    if (st_.state != SeqState::Running && st_.state != SeqState::WaitingConfirm) return;
    st_.total_elapsed_s = t - t_start_;
    st_.step_elapsed_s = t - step_t0_;
    const Step& s = steps_[st_.index];
    bool done = false;
    const bool temp_step = s.kind == StepKind::Reactor || s.kind == StepKind::Jacket || s.kind == StepKind::Reflux || s.kind == StepKind::Crystal;
    // 控温步做到一半控温被停了（安全联锁、通信断）：序列不能假装没事接着往下走
    if (temp_step && st_.step_elapsed_s > 1.0 && !ops_.temp_running()) {
        ops_.stop_dose();
        st_.state = SeqState::Faulted;
        st_.note = "控温被停止，序列中断在第 " + std::to_string(st_.index + 1) + " 步";
        return;
    }
    switch (s.kind) {
        case StepKind::Reactor: case StepKind::Jacket: {
            const double pv = ops_.temp_pv(s.kind == StepKind::Reactor ? ControlMode::Reactor : ControlMode::Jacket);
            if (!std::isnan(pv)) {
                if (!have_start_pv_) { have_start_pv_ = true; start_pv_ = pv; }
                const double span = std::fabs(s.target.value - start_pv_);
                st_.step_progress = span > 1e-6 ? std::min(1.0, std::max(0.0, 1.0 - std::fabs(s.target.value - pv) / span)) : 1.0;
                const bool in = ops_.ramp_done() && std::fabs(pv - s.target.value) <= arrive_band_k;
                if (!in) in_band_since_ = -1;
                else if (in_band_since_ < 0) in_band_since_ = t;
                done = in && t - in_band_since_ >= arrive_hold_s;
            }
            break;
        }
        case StepKind::Reflux: case StepKind::Wait: {
            const double dur = s.minutes * 60.0;
            st_.step_progress = dur > 0 ? std::min(1.0, st_.step_elapsed_s / dur) : 1.0;
            done = st_.step_elapsed_s >= dur;
            break;
        }
        case StepKind::Crystal: {
            double total = 0;
            for (auto& g : s.profile) total += g.minutes * 60.0;
            st_.step_progress = total > 0 ? std::min(1.0, st_.step_elapsed_s / total) : 1.0;
            done = ops_.profile_done();
            break;
        }
        case StepKind::Stir: st_.step_progress = 1.0; done = true; break;
        case StepKind::Dose: st_.step_progress = ops_.dose_progress(); done = ops_.dose_done(); break;
        case StepKind::Pause: done = confirmed_; break;
    }
    if (!done) return;
    st_.state = SeqState::Running;
    if (st_.index + 1 >= static_cast<int>(steps_.size())) {
        st_.step_progress = 1.0;
        st_.state = SeqState::Done;          // 温度和搅拌保持着，见文件头
        st_.note = "序列已完成，温度保持中";
        return;
    }
    st_.index++;
    enter(t);
}

}  // namespace tec
