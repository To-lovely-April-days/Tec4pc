// 任务序列：一个通道最多 6 步，一步一步往下走。
// 八种步骤（设计稿「步骤类型」弹窗定的）：釜内温度、夹套温度、蒸回流、结晶、搅拌转速、等待、加料、暂停确认。
//
// 每种步骤怎么算「做完了」：
//   釜内 / 夹套温度  斜坡走完（尽快到达没有斜坡），而且实测温度连续 30 s 待在目标 ± 到达容差以内
//   蒸回流           维持够设定的时长
//   结晶             分段曲线全部走完
//   搅拌转速         下发了就算完
//   等待             等够时长（期间温度和搅拌保持上一步的状态）
//   加料             加够体积；pH 反馈加料是维持够时长
//   暂停确认         操作人点了确认
// 最后一步做完以后序列停在「已完成」，温度和搅拌保持着不停——料还在釜里，自己停掉控温反而危险；要停由操作人点「结束序列」。
//
// 它不直接碰设备：要做的动作都经 ChannelOps 这个接口，测试里换成假的。
#pragma once
#include "../station/channel_controller.hpp"
#include <string>
#include <vector>

namespace tec {

enum class StepKind { Reactor, Jacket, Reflux, Crystal, Stir, Wait, Dose, Pause };
enum class DoseMode { AtOnce, Rate, PhHold };     // 一次加入 / 按速率 / pH 反馈（用户 2026-11-24：pH 反馈是加料的一种加入方式，不单独成步骤）

struct DoseParams {
    int pump = 1;                  // 1 或 2
    DoseMode mode = DoseMode::Rate;
    std::string liquid;
    double volume_ml = 10;         // 一次加入 / 按速率：加多少
    double rate_ml_min = 0.5;      // 按速率：流量
    bool sync_with_temp = true;
    // pH 反馈
    double ph_target = 7, ph_band = 0.2, max_rate_ml_min = 1, max_volume_ml = 50, hold_min = 60;
};

struct Step {
    StepKind kind = StepKind::Wait;
    Target target;                         // 釜内 / 夹套温度
    double stir_rpm = -1;                  // 温度步顺带设的搅拌；< 0 = 不动。搅拌步用它当目标转速
    double minutes = 0;                    // 等待 / 蒸回流的时长
    double reflux_delta_k = 8;
    std::vector<ProfileSegment> profile;   // 结晶
    DoseParams dose;
    std::string message;                   // 暂停确认：给操作人看的提示
};

class ChannelOps {
public:
    virtual ~ChannelOps() = default;
    virtual void start_temp(ControlMode mode, const Target& t) = 0;
    virtual void start_reflux(double delta_k) = 0;
    virtual void start_profile(const std::vector<ProfileSegment>& segs) = 0;
    virtual void stop_temp() = 0;
    virtual bool temp_running() const = 0;       // 控温还在跑吗（被安全联锁停掉就是 false）
    virtual double temp_pv(ControlMode mode) const = 0;   // 被控量这一拍的读数；读不到给 NaN
    virtual bool ramp_done() const = 0;
    virtual bool profile_done() const = 0;
    virtual void set_stir(double rpm) = 0;
    virtual void start_dose(const DoseParams& d) = 0;
    virtual bool dose_done() const = 0;
    virtual double dose_progress() const = 0;    // 0…1
    virtual void stop_dose() = 0;
};

enum class SeqState { Idle, Running, WaitingConfirm, Done, Aborted, Faulted };

struct SeqStatus {
    SeqState state = SeqState::Idle;
    int index = 0;                 // 正在做第几步（从 0 数）
    int count = 0;
    double step_elapsed_s = 0;
    double step_progress = 0;      // 这一步做了多少（0…1），界面上当前步圆环用
    double total_elapsed_s = 0;
    std::string note;              // 出错 / 等确认时给人看的话
};

class SequenceRunner {
public:
    static constexpr int kMaxSteps = 6;
    explicit SequenceRunner(ChannelOps& ops) : ops_(ops) {}
    bool load(const std::vector<Step>& steps);     // 序列在跑的时候不许换，返回 false；超过 6 步也返回 false
    const std::vector<Step>& steps() const { return steps_; }
    bool start(double t);
    void end(double t);                            // 操作人点「结束序列」：停控温、停加料
    void confirm();                                // 暂停确认那一步，操作人点了确认
    void tick(double t);                           // 每个节拍调一次
    SeqStatus status() const { return st_; }

    double arrive_band_k = 0.5;                    // 到达容差
    double arrive_hold_s = 30.0;                   // 要连续待在容差内这么久才算到

private:
    void enter(double t);
    ChannelOps& ops_;
    std::vector<Step> steps_;
    SeqStatus st_;
    double t_start_ = 0, step_t0_ = 0, in_band_since_ = -1, start_pv_ = 0;
    bool confirmed_ = false, have_start_pv_ = false;
};

}  // namespace tec
