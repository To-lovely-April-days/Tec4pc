// 搅拌 / pH / 加料运行层的测试：用一台假的进料系统，核对定量加料加够就停、pH 反馈加料的开停、各种拒绝和出错的处理。
#include "../core/station/aux_runner.hpp"
#include <cmath>
#include <cstdio>
using namespace tec;
static int fails = 0;
#define CHECK(c) do { if (!(c)) { std::printf("失败 %s:%d  %s\n", __FILE__, __LINE__, #c); fails++; } } while (0)

struct FakeAux : AuxHardware {
    double stir_set[2] = {0, 0}; double ph[2] = {7, 7}; bool ph_ok = true;
    double total[2] = {100, 200}, rate[2] = {0, 0}; bool on[2] = {false, false}; bool feed_ok = true; std::string alarm;
    int pump_writes = 0;
    void set_stir(int ch, double rpm) override { stir_set[ch] = rpm; }
    StirReading read_stir(int ch) override { StirReading r; r.ok = true; r.rpm = stir_set[ch]; r.current_a = stir_set[ch] > 0 ? 0.8 : 0; return r; }
    PhReading read_ph(int ch) override { PhReading r; if (ph_ok) r.ph = ph[ch]; return r; }
    FeedReading read_feed() override { FeedReading r; r.ok = feed_ok; for (int i = 0; i < 2; i++) { r.total_ml[i] = total[i]; r.on[i] = on[i]; r.flow_ml_min[i] = on[i] ? rate[i] : 0; } r.alarm_text = alarm; return r; }
    void set_pump_rate(int p, double v) override { rate[p - 1] = v; pump_writes++; }
    void set_pump(int p, bool o) override { on[p - 1] = o; pump_writes++; }
    void advance(double dt) { for (int i = 0; i < 2; i++) if (on[i]) total[i] += rate[i] * dt / 60.0; }   // 泵开着累计量就涨
};

int main() {
    FakeAux hw; AuxRunner aux(hw);
    double t = 0;
    auto run = [&](int secs) { for (int i = 0; i < secs; i++) { t += 1; hw.advance(1); aux.step(t); } };
    // 搅拌：设了下一拍写下去，读回来；超过上限钳住
    aux.set_stir(0, 300); run(1);
    CHECK(hw.stir_set[0] == 300 && aux.snapshot().stir[0].rpm == 300 && aux.snapshot().stir_set_rpm[0] == 300);
    aux.set_stir(1, 5000); run(1); CHECK(hw.stir_set[1] == 1000);
    // 按速率加 3 mL、6 mL/min：30 s 加完，加够就停泵
    DoseParams d; d.pump = 1; d.mode = DoseMode::Rate; d.volume_ml = 3; d.rate_ml_min = 6;
    CHECK(aux.start_dose(0, d)); CHECK(!aux.dose_done(0));
    run(15); CHECK(hw.on[0] && hw.rate[0] == 6 && std::fabs(aux.dose_progress(0) - 0.47) < 0.06);
    run(20); CHECK(aux.dose_done(0) && !hw.on[0]);
    CHECK(aux.snapshot().dose[0].dosed_ml >= 3.0 && aux.snapshot().dose[0].dosed_ml < 3.25);
    // 每秒不重复写同一个状态
    { const int w = hw.pump_writes; run(5); CHECK(hw.pump_writes == w); }
    // 一次加入：按泵最大流量
    d.mode = DoseMode::AtOnce; d.volume_ml = 5; CHECK(aux.start_dose(0, d)); run(2); CHECK(hw.rate[0] == 50); run(10); CHECK(aux.dose_done(0));
    // 拒绝：泵号不对、体积超上限、同一台泵两个通道抢
    d.pump = 3; CHECK(!aux.start_dose(0, d) && !aux.snapshot().dose[0].note.empty());
    d.pump = 1; d.volume_ml = 5000; CHECK(!aux.start_dose(0, d));
    d.volume_ml = 10; d.mode = DoseMode::Rate; d.rate_ml_min = 1; CHECK(aux.start_dose(0, d)); CHECK(!aux.start_dose(1, d));
    // 中途停：泵关掉
    run(3); CHECK(hw.on[0]); aux.stop_dose(0); run(1); CHECK(!hw.on[0] && aux.dose_done(0));
    // 进料系统报警：停
    CHECK(aux.start_dose(0, d)); run(2); hw.alarm = "泵 1 压力高"; run(1); CHECK(aux.dose_done(0) && !hw.on[0]); hw.alarm.clear();
    // 读不到进料系统：停
    CHECK(aux.start_dose(0, d)); run(2); hw.feed_ok = false; run(1); CHECK(aux.dose_done(0)); hw.feed_ok = true;
    // pH 反馈：目标 7.0、死区 0.2，开始时 pH 8.0（偏高）→ 认定料液是压低 pH 的
    DoseParams ph; ph.pump = 2; ph.mode = DoseMode::PhHold; ph.ph_target = 7.0; ph.ph_band = 0.2; ph.max_rate_ml_min = 2; ph.max_volume_ml = 1.0; ph.hold_min = 2;
    hw.ph[1] = 8.0; CHECK(aux.start_dose(1, ph)); run(2);
    CHECK(hw.on[1] && hw.rate[1] == 2);                 // 偏得远：按最大流量
    hw.ph[1] = 7.3; run(2); CHECK(hw.on[1] && hw.rate[1] < 2 && hw.rate[1] > 0);   // 近了：慢下来
    hw.ph[1] = 7.1; run(2); CHECK(hw.on[1]);            // 在死区里但还没回到目标：接着加
    hw.ph[1] = 6.98; run(2); CHECK(!hw.on[1]);          // 回到目标：停
    hw.ph[1] = 7.15; run(2); CHECK(!hw.on[1]);          // 在死区里：不动
    hw.ph[1] = 7.25; run(2); CHECK(hw.on[1]);           // 偏出死区：再开
    hw.ph[1] = 6.5; run(2); CHECK(!hw.on[1]);           // 偏到另一边去了：这种料液纠不回来，只能停着
    hw.ph_ok = false; hw.ph[1] = 8; run(2); CHECK(!hw.on[1] && !aux.snapshot().dose[1].note.empty());   // 读不到 pH：不加
    hw.ph_ok = true; CHECK(!aux.dose_done(1));
    run(120); CHECK(aux.dose_done(1) && !hw.on[1]);     // 维持够时长：这一步结束
    // pH 反馈加到最大总量就不再加，但要等够时长才结束
    hw.ph[1] = 9.0; ph.max_volume_ml = 0.05; ph.hold_min = 1; CHECK(aux.start_dose(1, ph)); run(10);
    CHECK(!hw.on[1] && !aux.dose_done(1) && aux.snapshot().dose[1].dosed_ml >= 0.05);
    run(60); CHECK(aux.dose_done(1));
    std::printf(fails ? "\n%d 项失败\n" : "搅拌 / pH / 加料运行层：全部通过\n", fails);
    return fails ? 1 : 0;
}
