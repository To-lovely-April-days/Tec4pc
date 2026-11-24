// 控温主循环里几种控温方式的测试：按速率 / 按时长的斜坡、结晶的分段曲线、蒸回流。只核「这一拍的目标给得对不对」。
#include "../core/station/channel_controller.hpp"
#include <cmath>
#include <cstdio>
using namespace tec;
static int fails = 0;
#define CHECK(c) do { if (!(c)) { std::printf("失败 %s:%d  %s\n", __FILE__, __LINE__, #c); fails++; } } while (0)
static bool near(double a, double b, double tol = 1e-6) { return std::fabs(a - b) < tol; }

int main() {
    GainTable table({{0, 10, 0.03, 0, 1.5, 3.5}, {80, 10, 0.03, 0, 5.0, 3.5}});
    ChannelConfig cfg;
    // 夹套控温，按速率 2 K/min 从 20 升到 30：0.5 s 一拍，每拍目标涨 1/60 K
    { ChannelController c(table, cfg); Target t; t.value = 30; t.arrive = Arrive::Rate; t.rate_k_per_min = 2; c.start(ControlMode::Jacket, t);
      c.tick(20.0, {}); CHECK(near(c.setpoint_now(), 20.0)); CHECK(!c.ramp_done());
      for (int i = 0; i < 120; i++) c.tick(20.0, {});            // 过了 60 s
      CHECK(near(c.setpoint_now(), 22.0)); CHECK(near(c.setpoint(), 30.0));
      for (int i = 0; i < 600; i++) c.tick(20.0, {});            // 再过 300 s：早该到了
      CHECK(near(c.setpoint_now(), 30.0)); CHECK(c.ramp_done()); }
    // 按时长 10 min 从 60 降到 20：速率 −4 K/min
    { ChannelController c(table, cfg); Target t; t.value = 20; t.arrive = Arrive::Duration; t.duration_min = 10; c.start(ControlMode::Jacket, t);
      c.tick(60.0, {});
      for (int i = 0; i < 600; i++) c.tick(60.0, {});            // 300 s，走了一半
      CHECK(near(c.setpoint_now(), 40.0)); }
    // 尽快：目标直接就是终点
    { ChannelController c(table, cfg); c.start(ControlMode::Jacket, 45.0); auto cmd = c.tick(20.0, {}); CHECK(near(cmd.jacket_sp, 45.0)); CHECK(c.ramp_done()); }
    // 在控时改目标
    { ChannelController c(table, cfg); c.start(ControlMode::Jacket, 45.0); c.tick(20.0, {}); c.set_setpoint(10.0); auto cmd = c.tick(20.0, {}); CHECK(near(cmd.jacket_sp, 10.0)); }
    // 结晶：60 →（30 min）45 →（20 min）45 保温 →（50 min）20，起点是开控时的釜内 60
    { ChannelController c(table, cfg); c.start_profile({{45, 30}, {45, 20}, {20, 50}});
      c.tick(62.0, 60.0); CHECK(c.segment_index() == 0); CHECK(near(c.setpoint_now(), 60.0)); CHECK(near(c.segment_remaining_s(), 1800.0));
      for (int i = 0; i < 1800; i++) c.tick(62.0, 60.0);         // 15 min
      CHECK(c.segment_index() == 0); CHECK(near(c.setpoint_now(), 52.5)); CHECK(near(c.setpoint(), 45.0));
      for (int i = 0; i < 2400; i++) c.tick(62.0, 60.0);         // 35 min：第 2 段保温
      CHECK(c.segment_index() == 1); CHECK(near(c.setpoint_now(), 45.0));
      for (int i = 0; i < 4800; i++) c.tick(62.0, 60.0);         // 75 min：第 3 段走了 25 min
      CHECK(c.segment_index() == 2); CHECK(near(c.setpoint_now(), 32.5)); CHECK(near(c.segment_remaining_s(), 1500.0));
      for (int i = 0; i < 4000; i++) c.tick(62.0, 60.0);         // 全部走完：停在最后的目标上
      CHECK(c.profile_done()); CHECK(near(c.setpoint_now(), 20.0)); }
    // 蒸回流：夹套目标 = 釜内 + 8
    { ChannelController c(table, cfg); c.start_reflux(8.0); auto cmd = c.tick(70.0, 65.0); CHECK(near(cmd.jacket_sp, 73.0));
      cmd = c.tick(72.0, 66.5); CHECK(near(cmd.jacket_sp, 74.5)); CHECK(cmd.side == HeatSide::Heater); }
    // 夹套目标不会超过 200
    { ChannelController c(table, cfg); c.start(ControlMode::Jacket, 260.0); auto cmd = c.tick(20.0, {}); CHECK(near(cmd.jacket_sp, 200.0)); }
    std::printf(fails ? "\n%d 项失败\n" : "控温方式：全部通过\n", fails);
    return fails ? 1 : 0;
}
