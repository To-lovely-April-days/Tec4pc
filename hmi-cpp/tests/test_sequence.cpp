// 任务序列引擎的测试：用一个假通道走一遍八种步骤里的六种，核对每一步发出的动作和什么时候进下一步。
#include "../core/sequence/sequence.hpp"
#include <cmath>
#include <cstdio>
#include <string>
using namespace tec;
static int fails = 0;
#define CHECK(c) do { if (!(c)) { std::printf("失败 %s:%d  %s\n", __FILE__, __LINE__, #c); fails++; } } while (0)

struct FakeOps : ChannelOps {
    std::string log; double pv = 20; bool running = false, ramp = true, prof = false, dosing = false; double dosed = 0, stir = 0;
    void start_temp(ControlMode m, const Target& t) override { running = true; log += (m == ControlMode::Reactor ? "Tr" : "Tj") + std::to_string(int(t.value)) + ";"; }
    void start_reflux(double d) override { running = true; log += "reflux" + std::to_string(int(d)) + ";"; }
    void start_profile(const std::vector<ProfileSegment>& s) override { running = true; log += "profile" + std::to_string(s.size()) + ";"; }
    void stop_temp() override { running = false; log += "stop;"; }
    bool temp_running() const override { return running; }
    double temp_pv(ControlMode) const override { return pv; }
    bool ramp_done() const override { return ramp; }
    bool profile_done() const override { return prof; }
    void set_stir(double r) override { stir = r; log += "stir" + std::to_string(int(r)) + ";"; }
    void start_dose(const DoseParams& d) override { dosing = true; dosed = 0; log += "dose" + std::to_string(d.pump) + ";"; }
    bool dose_done() const override { return dosed >= 1.0; }
    double dose_progress() const override { return dosed; }
    void stop_dose() override { dosing = false; log += "dosestop;"; }
};

int main() {
    FakeOps o;
    SequenceRunner q(o);
    std::vector<Step> steps(6);
    steps[0].kind = StepKind::Reactor; steps[0].target.value = 65; steps[0].stir_rpm = 300;
    steps[1].kind = StepKind::Wait; steps[1].minutes = 1;
    steps[2].kind = StepKind::Stir; steps[2].stir_rpm = 450;
    steps[3].kind = StepKind::Dose; steps[3].dose.pump = 2;
    steps[4].kind = StepKind::Pause; steps[4].message = "请加入晶种";
    steps[5].kind = StepKind::Crystal; steps[5].profile = {{45, 30}, {20, 50}};
    CHECK(q.load(steps));
    { std::vector<Step> seven(7); SequenceRunner q2(o); CHECK(!q2.load(seven)); }     // 最多 6 步
    double t = 0;
    CHECK(q.start(t));
    CHECK(o.log == "stir300;Tr65;" && q.status().state == SeqState::Running && q.status().index == 0);
    CHECK(!q.load(steps));                                                            // 已开始的序列不可增改
    // 还没到温：不进下一步；进度按离目标多远算
    t += 0.5; q.tick(t);                                                              // 第一拍：记下起点 20 ℃
    o.pv = 42.5; for (int i = 0; i < 20; i++) { t += 0.5; q.tick(t); }
    CHECK(q.status().index == 0 && std::fabs(q.status().step_progress - 0.5) < 0.01);
    // 到了容差内但没待够 30 s：不算
    o.pv = 64.8; for (int i = 0; i < 40; i++) { t += 0.5; q.tick(t); }
    CHECK(q.status().index == 0);
    // 中途又掉出去一下：重新计时
    o.pv = 64.0; t += 0.5; q.tick(t);
    o.pv = 64.9; for (int i = 0; i < 59; i++) { t += 0.5; q.tick(t); }
    CHECK(q.status().index == 0);
    for (int i = 0; i < 3; i++) { t += 0.5; q.tick(t); }
    CHECK(q.status().index == 1);                                                     // 进「等待」
    // 等待 1 min
    for (int i = 0; i < 118; i++) { t += 0.5; q.tick(t); }
    CHECK(q.status().index == 1 && q.status().step_progress > 0.9);
    for (int i = 0; i < 4; i++) { t += 0.5; q.tick(t); }
    // 搅拌步下发了就过，直接到了加料
    CHECK(q.status().index == 3 && o.stir == 450 && o.log.find("dose2;") != std::string::npos);
    o.dosed = 0.4; t += 0.5; q.tick(t); CHECK(q.status().index == 3 && std::fabs(q.status().step_progress - 0.4) < 1e-9);
    o.dosed = 1.0; t += 0.5; q.tick(t);
    // 暂停确认：停在这等人
    CHECK(q.status().index == 4 && q.status().state == SeqState::WaitingConfirm && q.status().note == "请加入晶种");
    for (int i = 0; i < 10; i++) { t += 0.5; q.tick(t); }
    CHECK(q.status().index == 4);
    q.confirm(); t += 0.5; q.tick(t);
    CHECK(q.status().index == 5 && q.status().state == SeqState::Running && o.log.find("profile2;") != std::string::npos);
    // 结晶：曲线走完才算
    for (int i = 0; i < 10; i++) { t += 0.5; q.tick(t); }
    CHECK(q.status().index == 5);
    o.prof = true; t += 0.5; q.tick(t);
    CHECK(q.status().state == SeqState::Done && o.running);                           // 做完了温度保持着，不自己停
    q.end(t);
    CHECK(q.status().state == SeqState::Aborted && !o.running);

    // 控温步中途被安全联锁停掉：序列中断，不往下走
    FakeOps o2; SequenceRunner q3(o2);
    std::vector<Step> two(2); two[0].kind = StepKind::Jacket; two[0].target.value = 50; two[1].kind = StepKind::Wait; two[1].minutes = 5;
    q3.load(two); q3.start(0);
    for (int i = 1; i <= 10; i++) q3.tick(i * 0.5);
    o2.running = false; q3.tick(6.0);
    CHECK(q3.status().state == SeqState::Faulted && q3.status().index == 0);

    // 按速率到达：斜坡没走完，哪怕温度碰巧在容差内也不算到
    FakeOps o3; SequenceRunner q4(o3);
    std::vector<Step> one(2); one[0].kind = StepKind::Reactor; one[0].target.value = 20; one[0].target.arrive = Arrive::Rate; one[1].kind = StepKind::Wait;
    o3.pv = 20.1; o3.ramp = false; q4.load(one); q4.start(0);
    for (int i = 1; i <= 100; i++) q4.tick(i * 0.5);
    CHECK(q4.status().index == 0);
    o3.ramp = true; for (int i = 101; i <= 170; i++) q4.tick(i * 0.5);
    CHECK(q4.status().index == 1);

    std::printf(fails ? "\n%d 项失败\n" : "任务序列：全部通过\n", fails);
    return fails ? 1 : 0;
}
