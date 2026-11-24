// 进料系统协议层的测试：假串口扮一台有 49 个寄存器的从站，核对发出去的帧和解析结果。
// 期望值的出处：C# 的 FeedSystemDriverTests（2.3f = 0x4013 0x3333、泵 1 启动 = 寄存器 39 写 1、MFC 0 = 运行）。
#include "../drivers/feed.hpp"
#include "../io/fake_serial.hpp"
#include <cmath>
#include <cstdio>
#include <map>
#include <stdexcept>
#include <vector>
using namespace tec;
static int fails = 0;
#define CHECK(c) do { if (!(c)) { std::printf("失败 %s:%d  %s\n", __FILE__, __LINE__, #c); fails++; } } while (0)
using Frame = std::vector<uint8_t>;
static Frame with_crc(Frame f) { const uint16_t c = modbus_crc(f.data(), f.size()); f.push_back(uint8_t(c)); f.push_back(uint8_t(c >> 8)); return f; }

// 假从站：认 03 / 06 / 10，寄存器存在 regs 里
struct FakeFeed {
    std::map<uint16_t, uint16_t> regs;
    FakeFeed() { for (uint16_t r = 0; r < 49; r++) regs[r] = 0; regs[feed_reg::MfcOnOff] = 1; }   // MFC 停着 = 1
    void set_f(uint16_t reg, float v) { auto [hi, lo] = feed_reg::regs_of(v); regs[reg] = hi; regs[reg + 1] = lo; }
    Frame answer(const Frame& q) {
        const uint16_t addr = uint16_t(q[2] << 8 | q[3]), n = uint16_t(q[4] << 8 | q[5]);
        if (q[1] == 0x03) {
            Frame r = {q[0], 0x03, uint8_t(n * 2)};
            for (uint16_t i = 0; i < n; i++) { r.push_back(uint8_t(regs[addr + i] >> 8)); r.push_back(uint8_t(regs[addr + i])); }
            return with_crc(r);
        }
        if (q[1] == 0x06) { regs[addr] = n; return with_crc({q[0], q[1], q[2], q[3], q[4], q[5]}); }
        if (q[1] == 0x10) {
            for (uint16_t i = 0; i < n; i++) regs[addr + i] = uint16_t(q[7 + 2 * i] << 8 | q[8 + 2 * i]);
            return with_crc({q[0], q[1], q[2], q[3], q[4], q[5]});
        }
        return with_crc({q[0], uint8_t(q[1] | 0x80), 0x01});
    }
};

int main() {
    // Float 两寄存器大端：2.3f = 0x40133333，高字在前
    { auto [hi, lo] = feed_reg::regs_of(2.3f);
      CHECK(hi == 0x4013 && lo == 0x3333);
      CHECK(feed_reg::float_of(hi, lo) == 2.3f);
      CHECK(feed_reg::float_of(0x42D2, 0x0000) == 105.0f); }

    // 设流量：0x10 往设定流量寄存器写两个寄存器（泵 1 = 6、泵 2 = 14、MFC = 0）
    { FakeFeed d; FakeSerial s([&d](const Frame& q) { return d.answer(q); });
      ModbusRtu m(s, 1); FeedSystem f(m);
      f.set_pump_setpoint(1, 2.5); f.set_pump_setpoint(2, 2.3); f.set_mfc_setpoint(1.0);
      CHECK(s.sent[0] == with_crc({0x01, 0x10, 0x00, 0x06, 0x00, 0x02, 0x04, 0x40, 0x20, 0x00, 0x00}));
      CHECK(s.sent[1] == with_crc({0x01, 0x10, 0x00, 0x0E, 0x00, 0x02, 0x04, 0x40, 0x13, 0x33, 0x33}));
      CHECK(s.sent[2] == with_crc({0x01, 0x10, 0x00, 0x00, 0x00, 0x02, 0x04, 0x3F, 0x80, 0x00, 0x00}));
      CHECK(d.regs[6] == 0x4020 && d.regs[7] == 0x0000 && d.regs[14] == 0x4013 && d.regs[15] == 0x3333); }

    // 启动 / 停止：泵 1 = 寄存器 39、泵 2 = 40，1 运行 0 停止；MFC = 38，反着，0 运行 1 停止
    { FakeFeed d; FakeSerial s([&d](const Frame& q) { return d.answer(q); });
      ModbusRtu m(s, 1); FeedSystem f(m);
      f.set_pump(1, true); f.set_pump(2, true); f.set_pump(1, false); f.set_pump(2, false); f.set_mfc(true); f.set_mfc(false);
      CHECK(s.sent[0] == with_crc({0x01, 0x06, 0x00, 0x27, 0x00, 0x01}));
      CHECK(s.sent[1] == with_crc({0x01, 0x06, 0x00, 0x28, 0x00, 0x01}));
      CHECK(s.sent[2] == with_crc({0x01, 0x06, 0x00, 0x27, 0x00, 0x00}));
      CHECK(s.sent[3] == with_crc({0x01, 0x06, 0x00, 0x28, 0x00, 0x00}));
      CHECK(s.sent[4] == with_crc({0x01, 0x06, 0x00, 0x26, 0x00, 0x00}));
      CHECK(s.sent[5] == with_crc({0x01, 0x06, 0x00, 0x26, 0x00, 0x01})); }

    // 报警复位 / 消音：寄存器 46 / 47 写 1
    { FakeFeed d; FakeSerial s([&d](const Frame& q) { return d.answer(q); });
      ModbusRtu m(s, 1); FeedSystem f(m);
      f.reset_alarms(); f.silence_alarms();
      CHECK(s.sent[0] == with_crc({0x01, 0x06, 0x00, 0x2E, 0x00, 0x01}));
      CHECK(s.sent[1] == with_crc({0x01, 0x06, 0x00, 0x2F, 0x00, 0x01})); }

    // 一帧读全：发的是 03 读 0 起 49 个；已加体积、流量、压力、运行状态、报警都解对
    { FakeFeed d;
      d.set_f(feed_reg::MfcSetpoint, 1.5f); d.set_f(feed_reg::MfcFlow, 1.25f); d.set_f(feed_reg::MfcTotal, 12.5f);
      d.set_f(feed_reg::Pump1Setpoint, 2.5f); d.set_f(feed_reg::Pump1Flow, 2.25f); d.set_f(feed_reg::Pump1Total, 105.0f); d.set_f(feed_reg::Pump1Pressure, 0.5f);
      d.set_f(feed_reg::Pump2Setpoint, 4.0f); d.set_f(feed_reg::Pump2Flow, 3.75f); d.set_f(feed_reg::Pump2Total, 7.5f); d.set_f(feed_reg::Pump2Pressure, 0.25f);
      d.set_f(feed_reg::MfcRangeHigh, 10.0f); d.set_f(feed_reg::MfcCorrection, 1.0f); d.set_f(feed_reg::MfcDpt, 1.0f);
      d.set_f(feed_reg::Pump1PressureHigh, 2.0f); d.set_f(feed_reg::Pump2PressureHigh, 3.0f); d.set_f(feed_reg::Pump2PressureLow, -0.5f);
      d.regs[feed_reg::Pump1OnOff] = 1; d.regs[feed_reg::MfcOnOff] = 0;
      d.regs[feed_reg::Pump2CommAlarm] = 1; d.regs[feed_reg::AlarmStatus] = 1;
      FakeSerial s([&d](const Frame& q) { return d.answer(q); });
      ModbusRtu m(s, 1); FeedSystem f(m);
      const FeedSnapshot v = f.read_all();
      CHECK(s.sent.size() == 1 && s.sent[0] == with_crc({0x01, 0x03, 0x00, 0x00, 0x00, 0x31}));
      CHECK(v.mfc_setpoint == 1.5 && v.mfc_flow == 1.25 && v.mfc_total == 12.5);
      CHECK(v.pump1_setpoint == 2.5 && v.pump1_flow == 2.25 && v.pump1_total == 105.0 && v.pump1_pressure == 0.5);
      CHECK(v.pump2_setpoint == 4.0 && v.pump2_flow == 3.75 && v.pump2_total == 7.5 && v.pump2_pressure == 0.25);
      CHECK(v.mfc_range_high == 10.0 && v.mfc_range_low == 0.0 && v.mfc_correction == 1.0 && v.mfc_dpt == 1.0);
      CHECK(v.pump1_pressure_high == 2.0 && v.pump1_pressure_low == 0.0 && v.pump2_pressure_high == 3.0 && v.pump2_pressure_low == -0.5);
      CHECK(v.mfc_on && v.pump1_on && !v.pump2_on);
      CHECK(v.pump_total(1) == 105.0 && v.pump_total(2) == 7.5 && v.pump_on(1) && !v.pump_on(2));
      CHECK(v.pump_flow(2) == 3.75 && v.pump_setpoint(2) == 4.0 && v.pump_pressure(1) == 0.5);
      CHECK(!v.pump1_pressure_alarm && !v.pump2_pressure_alarm && !v.mfc_comm_alarm && !v.pump1_comm_alarm && v.pump2_comm_alarm && v.alarm_active);
      CHECK(v.any_alarm() && v.alarm_text() == "泵 2 通信故障（进料系统内部 PLC 读不到泵 2）"); }

    // 全停、无报警的一拍：MFC 寄存器是 1 = 停；没有报警时文字是空串
    { FakeFeed d; FakeSerial s([&d](const Frame& q) { return d.answer(q); });
      ModbusRtu m(s, 1); FeedSystem f(m);
      const FeedSnapshot v = f.read_all();
      CHECK(!v.mfc_on && !v.pump1_on && !v.pump2_on && !v.any_alarm() && v.alarm_text().empty()); }

    // 报警文字：几项一起亮用顿号连；只有总报警位亮时照实说没点名
    { FeedSnapshot v; v.pump1_pressure_alarm = true; v.mfc_comm_alarm = true;
      CHECK(v.alarm_text() == "泵 1 压力上限报警、MFC 通信故障（进料系统内部 PLC 读不到 MFC）");
      FeedSnapshot w; w.alarm_active = true;
      CHECK(w.any_alarm() && w.alarm_text() == "报警状态位为 1（具体哪一路没点名）"); }

    // 写了再走设备读回来：设流量 → 启动 → 读状态，整链对得上；非 2.5 这种整二进制的数只在单精度精度内相等
    { FakeFeed d; FakeSerial s([&d](const Frame& q) { return d.answer(q); });
      ModbusRtu m(s, 1); FeedSystem f(m);
      f.set_pump_setpoint(2, 2.3); f.set_pump(2, true);
      const FeedSnapshot v = f.read_all();
      CHECK(v.pump2_on && !v.pump1_on && std::fabs(v.pump2_setpoint - 2.3) < 1e-6 && v.pump2_setpoint == double(2.3f)); }

    // 单点读：Float 读 2 个寄存器、Int 读 1 个
    { FakeFeed d; d.set_f(feed_reg::Pump2Total, 7.5f);
      FakeSerial s([&d](const Frame& q) { return d.answer(q); });
      ModbusRtu m(s, 1); FeedSystem f(m);
      CHECK(f.read_float(feed_reg::Pump2Total) == 7.5);
      CHECK(f.read_int16(feed_reg::MfcOnOff) == 1);
      CHECK(s.sent[0] == with_crc({0x01, 0x03, 0x00, 0x12, 0x00, 0x02}));
      CHECK(s.sent[1] == with_crc({0x01, 0x03, 0x00, 0x26, 0x00, 0x01})); }

    // 站号跟着 ModbusRtu 走，不写死 1
    { FakeFeed d; FakeSerial s([&d](const Frame& q) { return d.answer(q); });
      ModbusRtu m(s, 5); FeedSystem f(m);
      f.set_pump(1, true);
      CHECK(s.sent[0] == with_crc({0x05, 0x06, 0x00, 0x27, 0x00, 0x01})); }

    // 泵号不是 1 / 2：报错，一帧都不发（不悄悄落到泵 1 上）
    { FakeFeed d; FakeSerial s([&d](const Frame& q) { return d.answer(q); });
      ModbusRtu m(s, 1); FeedSystem f(m); int thrown = 0;
      try { f.set_pump(3, true); } catch (const std::invalid_argument&) { thrown++; }
      try { f.set_pump_setpoint(0, 1.0); } catch (const std::invalid_argument&) { thrown++; }
      try { FeedSnapshot().pump_total(3); } catch (const std::invalid_argument&) { thrown++; }
      CHECK(thrown == 3 && s.sent.empty()); }

    // 从站回异常码：原样抛 ModbusError，带着异常码
    { FakeSerial s([](const Frame& q) { return with_crc({q[0], uint8_t(q[1] | 0x80), 0x02}); });
      ModbusRtu m(s, 1); FeedSystem f(m); bool got = false;
      try { f.set_pump(1, true); } catch (const ModbusError& e) { got = e.code && *e.code == 2; }
      CHECK(got); }

    std::printf(fails ? "\n%d 项失败\n" : "进料系统协议层：全部通过\n", fails);
    return fails ? 1 : 0;
}
