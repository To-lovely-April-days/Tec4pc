// RD105 协议层的测试：假串口扮设备，核对发出去的帧（Modbus 的寄存器地址 / 字序 / 功能码，ASCII 的指令文本）和解析结果。
// 期望值的来处：C# 的 Rd105Registers（地址表）、TecScale（量纲）、Rd105ModbusBridgeTests（协议 §2.2 示例帧
// 01 03 10 00 00 02 C0 CB、DATADEMAND 示例里的 2259187 / 11139104486）、FakeRd105Device（ASCII 设备怎么答）。
#include "../drivers/rd105.hpp"
#include "../io/fake_serial.hpp"
#include <cmath>
#include <cstdio>
#include <map>
#include <string>
#include <vector>
using namespace tec;
static int fails = 0;
#define CHECK(c) do { if (!(c)) { std::printf("失败 %s:%d  %s\n", __FILE__, __LINE__, #c); fails++; } } while (0)
static bool near(double a, double b, double eps = 1e-9) { return std::fabs(a - b) < eps; }
using Bytes = std::vector<uint8_t>;
static Bytes with_crc(Bytes f) { const uint16_t c = modbus_crc(f.data(), f.size()); f.push_back(uint8_t(c)); f.push_back(uint8_t(c >> 8)); return f; }

// 一台说 Modbus-RTU 的假 RD105（站号 1）：一张寄存器表，认 03 / 10
struct Slave {
    std::map<int, uint16_t> regs;
    std::vector<std::string> log;        // "R 0x1000+2" / "W 0x1000+2"
    bool mute = false;
    int exception_at = -1;               // 读这个地址回异常码 02
    int clamp_addr = -1; uint16_t clamp_max = 0;     // 写这个单字寄存器超过上限就存成上限（设备钳位）
    long long duty_floor_raw = 0;        // 非零的小占空比按这个绝对值存（现场：写 −4.41 % 回显 −6.43 %）

    void set32(int a, int32_t v) { regs[a] = uint16_t(uint32_t(v) >> 16); regs[a + 1] = uint16_t(v); }
    int32_t get32(int a) { return int32_t(uint32_t(regs[a]) << 16 | regs[a + 1]); }
    void set64(int a, int64_t v) { for (int i = 0; i < 4; i++) regs[a + i] = uint16_t(uint64_t(v) >> 16 * (3 - i)); }
    int64_t get64(int a) { uint64_t x = 0; for (int i = 0; i < 4; i++) x = x << 16 | regs[a + i]; return int64_t(x); }

    Bytes handle(const Bytes& q) {
        if (mute || q.size() < 8 || q[0] != 1) return {};
        const int addr = q[2] << 8 | q[3], n = q[4] << 8 | q[5];
        char b[32];
        std::snprintf(b, sizeof b, "%c 0x%04X+%d", q[1] == 3 ? 'R' : 'W', addr, n);
        log.push_back(b);
        if (q[1] == 0x03) {
            if (addr == exception_at) return with_crc({1, 0x83, 2});
            Bytes r = {1, 3, uint8_t(n * 2)};
            for (int i = 0; i < n; i++) { r.push_back(uint8_t(regs[addr + i] >> 8)); r.push_back(uint8_t(regs[addr + i])); }
            return with_crc(r);
        }
        if (q[1] == 0x10) {
            for (int i = 0; i < n; i++) regs[addr + i] = uint16_t(q[7 + i * 2] << 8 | q[8 + i * 2]);
            if (addr == clamp_addr && regs[addr] > clamp_max) regs[addr] = clamp_max;
            if (duty_floor_raw && (addr == 0x1103 || addr == 0x2103)) {
                const int64_t v = get64(addr);
                if (v != 0 && std::llabs(v) < duty_floor_raw) set64(addr, v < 0 ? -duty_floor_raw : duty_floor_raw);
            }
            return with_crc({1, 0x10, q[2], q[3], q[4], q[5]});
        }
        return {};
    }
};

// 像 215L 的一台（数照 C# Rd105ModbusBridgeTests.Device）
static Slave device() {
    Slave s;
    s.regs[0x0001] = 4;                 // 型号代码 4 = 215L
    s.regs[0x000C] = 130;               // FPV 1.3.0
    s.regs[0x0004] = 0;                 // CONTMODE
    s.set32(0x1000, 2500000);           // TC1:TG 25 ℃
    s.set32(0x2000, 3000000);           // TC2:TG 30 ℃
    s.set32(0x1002, 2259187);           // TC1:TCADJTEMP
    s.set64(0x1004, 11139104486LL);     // TC1:RESISTOR
    s.set32(0x2002, 999999999);         // TC2 没接传感器
    s.regs[0x0003] = 23;                // SINTERIORTEMP
    return s;
}

// 一台说 ASCII 的假 RD105：存指令文本，按 FakeRd105Device 的样子答
struct AsciiDev {
    std::map<std::string, std::string> regs;
    std::vector<std::string> cmds;
    int mute_next = 0, garble_next = 0;
    bool mute = false;
    std::string datademand = "TC1:TCADJTEMP=2259187@TC1:RESISTOR=11139104486@TC1:OUTV=1000000000@"
                             "TC2:TCADJTEMP=999999999@TC2:RESISTOR=0@TC2:OUTV=0@SINTERIORTEMP=23@\r\n";
    Bytes handle(const Bytes& q) {
        std::string c(q.begin(), q.end());
        cmds.push_back(c);
        if (mute) return {};
        if (mute_next > 0) { mute_next--; return {}; }
        const size_t eq = c.find('='), at = c.find('@');
        if (eq == std::string::npos || at == std::string::npos) return {};
        const std::string key = c.substr(0, eq), val = c.substr(eq + 1, at - eq - 1);
        std::string r;
        if (key == "DATADEMAND") r = datademand;
        else {
            if (val != "?") regs[key] = val;
            r = "OK" + key + "=" + (regs.count(key) ? regs[key] : "0") + "@\r\n";
        }
        if (garble_next > 0) { garble_next--; for (char& ch : r) if (ch == '@') ch = 'P'; }   // 现场：'@' 被打成 'P'
        return Bytes(r.begin(), r.end());
    }
};
static std::string text(const Bytes& b) { return std::string(b.begin(), b.end()); }

int main() {
    // ── 量纲（C# TecScale）──
    CHECK(near(rd105::temp_from_raw(2500000), 25.0));
    CHECK(std::isnan(rd105::temp_from_raw(999999999)));
    CHECK(rd105::temp_to_raw(-15) == -1500000);
    CHECK(near(rd105::ohms_from_raw(10000000000LL), 10000.0));
    CHECK(near(rd105::volts_from_raw(1000000000), 10.0));
    CHECK(near(rd105::duty_percent_from_raw(200000), 10.0) && near(rd105::duty_percent_from_raw(-2000000), -100.0));
    CHECK(rd105::duty_percent_to_raw(-4.41) == -88200);
    CHECK(near(rd105::speed_from_raw(1000), 1.0) && rd105::speed_to_raw(0.5) == 500);
    CHECK(near(rd105::current_from_raw(3256), 3.256));
    CHECK(near(rd105::max_current_from_raw(10), 1.0) && rd105::max_current_to_raw(5) == 50);
    CHECK(rd105::model_name(4) == "215L" && rd105::model_name(31) == "203" && rd105::model_name(99) == "99");
    CHECK(rd105::firmware_text(130) == "v1.3.0");
    // 寄存器编解码：高字在前、负数补码（协议示例 2500000 = 00 26 25 A0）
    CHECK((rd105::encode_regs(rd105::RegType::I32, 2500000) == std::vector<uint16_t>{0x0026, 0x25A0}));
    CHECK((rd105::encode_regs(rd105::RegType::I64, -88200) == std::vector<uint16_t>{0xFFFF, 0xFFFF, 0xFFFE, 0xA778}));
    CHECK(rd105::decode_regs(rd105::RegType::I16, {0xFFFF}) == -1 && rd105::decode_regs(rd105::RegType::U16, {0xFFFF}) == 65535);
    CHECK(rd105::decode_regs(rd105::RegType::I32, {0xFFE9, 0x1CA0}) == -1500000);
    CHECK(rd105::find_reg("pwmduty") && rd105::find_reg("pwmduty")->address == 0x1103 && !rd105::find_reg("NOSUCH"));
    // 告警字
    { auto d = rd105::describe_faults(rd105::kUnderVoltage | rd105::kCh2CurrentLimiting);
      CHECK(d.size() == 2 && d[1] == "通道2 输出限流中");
      CHECK(rd105::describe_faults(0).empty());
      CHECK(rd105::current_limit_bit(1) == 0x0040 && rd105::current_limit_bit(2) == 0x0400 && rd105::sensor_fault_bit(2) == 0x0200); }

    // ══ Modbus-RTU ══
    // 查询走 03；协议 §2.2 那一帧原样；通道二 = 地址 + 0x1000
    { Slave d = device(); FakeSerial s([&d](const Bytes& q) { return d.handle(q); }); ModbusRtu m(s, 1); Rd105 r(m);
      CHECK(r.is_modbus());
      CHECK(r.query(1, "TG") == 2500000 && r.query(2, "TG") == 3000000);
      CHECK((s.sent[0] == Bytes{0x01, 0x03, 0x10, 0x00, 0x00, 0x02, 0xC0, 0xCB}));
      CHECK(s.sent[1] == with_crc({0x01, 0x03, 0x20, 0x00, 0x00, 0x02})); }
    // 设置走 10，负数补码，写完紧跟一次回读
    { Slave d = device(); FakeSerial s([&d](const Bytes& q) { return d.handle(q); }); ModbusRtu m(s, 1); Rd105 r(m);
      r.set_target(1, -15);
      CHECK(d.get32(0x1000) == -1500000);
      CHECK(s.sent[0] == with_crc({0x01, 0x10, 0x10, 0x00, 0x00, 0x02, 0x04, 0xFF, 0xE9, 0x1C, 0xA0}));
      CHECK((d.log == std::vector<std::string>{"W 0x1000+2", "R 0x1000+2"})); }
    // 快照：一个参数一帧，只读两路温度和自身温度；没接传感器是 NaN；Modbus 下没有电阻 / 输出电压
    { Slave d = device(); FakeSerial s([&d](const Bytes& q) { return d.handle(q); }); ModbusRtu m(s, 1); Rd105 r(m);
      auto snap = r.read_snapshot();
      CHECK(near(snap.temp_c[0], 22.59187) && std::isnan(snap.temp_c[1]) && snap.internal_c == 23);
      CHECK(std::isnan(snap.resistor_ohms[0]) && std::isnan(snap.out_volts[0]));
      CHECK((d.log == std::vector<std::string>{"R 0x1002+2", "R 0x2002+2", "R 0x0003+1"}));
      CHECK(near(r.read_temperature(1), 22.59187));
      CHECK(near(rd105::ohms_from_raw(r.query(1, "RESISTOR")), 11139.104486, 1e-6) && d.log.back() == "R 0x1004+4"); }
    // 自身温度是带符号的整数 ℃
    { Slave d = device(); d.regs[0x0003] = 0xFFFB; FakeSerial s([&d](const Bytes& q) { return d.handle(q); }); ModbusRtu m(s, 1); Rd105 r(m);
      CHECK(r.read_snapshot().internal_c == -5); }
    // 告警字、电流、占空比回读
    { Slave d = device(); d.regs[0x0007] = 0x0044; d.regs[0x1111] = 3256; d.regs[0x2111] = 120; d.set64(0x2103, -1800000);
      FakeSerial s([&d](const Bytes& q) { return d.handle(q); }); ModbusRtu m(s, 1); Rd105 r(m);
      CHECK(r.read_error_code() == (rd105::kUnderVoltage | rd105::kCh1CurrentLimiting));
      CHECK(near(r.read_current(1), 3.256) && near(r.read_current(2), 0.12));
      CHECK(near(r.read_duty_percent(2), -90.0));
      CHECK((d.log == std::vector<std::string>{"R 0x0007+1", "R 0x1111+1", "R 0x2111+1", "R 0x2103+4"})); }
    // 写占空比：int64 四个寄存器；超了钳到 ±100；回显跟写的不一样不抛、照回显给
    { Slave d = device(); d.duty_floor_raw = 128600; FakeSerial s([&d](const Bytes& q) { return d.handle(q); }); ModbusRtu m(s, 1); Rd105 r(m);
      CHECK(near(r.set_duty_percent(1, -4.41), -6.43));
      CHECK(s.sent[0] == with_crc({0x01, 0x10, 0x11, 0x03, 0x00, 0x04, 0x08, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFE, 0xA7, 0x78}));
      CHECK(s.sent[1] == with_crc({0x01, 0x03, 0x11, 0x03, 0x00, 0x04}));
      CHECK(near(r.set_duty_percent(2, 150), 100.0) && d.get64(0x2103) == 2000000);
      CHECK(near(r.set_duty_percent(2, -150), -100.0) && d.get64(0x2103) == -2000000);
      CHECK(near(r.set_duty_percent(1, 0), 0.0) && d.get64(0x1103) == 0);
      bool bad = false; try { r.set_duty_percent(1, std::nan("")); } catch (const std::invalid_argument&) { bad = true; }
      CHECK(bad); }
    // 上位机开控那几笔：MODE=3、ENABLE、POWERMODE=2、FPWM，单字的量也走 10
    { Slave d = device(); FakeSerial s([&d](const Bytes& q) { return d.handle(q); }); ModbusRtu m(s, 1); Rd105 r(m);
      r.set_mode(2, 3); r.set_enable(1, true); r.set_power_mode(1, 2); r.set_pwm_frequency(0); r.set_controller_mode(0);
      CHECK(d.regs[0x2101] == 3 && d.regs[0x1100] == 1 && d.regs[0x1110] == 2 && d.regs[0x000D] == 0);
      CHECK(s.sent[0] == with_crc({0x01, 0x10, 0x21, 0x01, 0x00, 0x01, 0x02, 0x00, 0x03}));
      CHECK(s.sent[2] == with_crc({0x01, 0x10, 0x11, 0x00, 0x00, 0x01, 0x02, 0x00, 0x01}));
      CHECK((d.log == std::vector<std::string>{"W 0x2101+1", "R 0x2101+1", "W 0x1100+1", "R 0x1100+1", "W 0x1110+1", "R 0x1110+1",
                                                 "W 0x000D+1", "R 0x000D+1", "W 0x0004+1", "R 0x0004+1"}));
      r.set_enable(1, false); CHECK(d.regs[0x1100] == 0);
      CHECK(r.read_pwm_frequency() == 0);
      bool bad = false; try { r.set_pwm_frequency(4); } catch (const std::invalid_argument&) { bad = true; } CHECK(bad);
      bad = false; try { r.set_power_mode(1, 3); } catch (const std::invalid_argument&) { bad = true; } CHECK(bad);
      bad = false; try { r.set_enable(3, true); } catch (const std::invalid_argument&) { bad = true; } CHECK(bad); }
    // 保护值、PID、限流、速率：地址和标度（C# 测试：超温上限 150 ℃ → 0x133D = 15000000，通道二 0x233D）
    { Slave d = device(); FakeSerial s([&d](const Bytes& q) { return d.handle(q); }); ModbusRtu m(s, 1); Rd105 r(m);
      r.set_over_temp(1, 150, -30); r.set_over_temp(2, 150, -30);
      CHECK(d.get32(0x133D) == 15000000 && d.get32(0x133F) == -3000000 && d.get32(0x233D) == 15000000 && d.get32(0x233F) == -3000000);
      r.set_pid(1, 3000, 150, 0); r.set_max_current(1, 5); r.set_speed(1, 0.5); r.set_max_duty(1, 90); r.start_auto_tune(2);
      CHECK(d.get32(0x1200) == 3000 && d.get32(0x1202) == 150 && d.get32(0x1204) == 0);
      CHECK(d.regs[0x1112] == 50 && d.regs[0x1108] == 500 && d.regs[0x110E] == 90 && d.regs[0x2107] == 1);
      CHECK(r.read_auto_pid(2) == 1);
      d.regs[0x1101] = 3; d.regs[0x1100] = 1; d.regs[0x110A] = 404; d.regs[0x110B] = 0;
      auto c = r.read_channel_config(1);
      CHECK(near(c.target_c, 25) && c.enabled && c.mode == 3 && c.kp == 3000 && c.ki == 150 && c.kd == 0 && c.max_duty_percent == 90);
      CHECK(near(c.speed_c_per_sec, 0.5) && near(c.over_temp_up_c, 150) && near(c.over_temp_lower_c, -30) && near(c.max_current_a, 5) && c.auto_pid == 0);
      auto sv = r.read_start_voltage_percent(1);
      CHECK(near(sv.first, 2.02) && near(sv.second, 0)); }
    // 设备钳位（LIMITED 写 95 存成 90）：回读不符再发一次，还不符抛，不是超时
    { Slave d = device(); d.clamp_addr = 0x110E; d.clamp_max = 90; FakeSerial s([&d](const Bytes& q) { return d.handle(q); }); ModbusRtu m(s, 1); Rd105 r(m);
      bool got = false;
      try { r.set_max_duty(1, 95); } catch (const Rd105Error& e) { got = !e.timeout && std::string(e.what()).find("设备回读 90") != std::string::npos; }
      CHECK(got);
      CHECK((d.log == std::vector<std::string>{"W 0x110E+1", "R 0x110E+1", "W 0x110E+1", "R 0x110E+1"}));
      CHECK(r.set_echo(1, "LIMITED", 95) == 90); }
    // 型号代码换成名字、固件号；CONTMODE 固件回异常码 → 那一项空着，别的照常
    { Slave d = device(); FakeSerial s([&d](const Bytes& q) { return d.handle(q); }); ModbusRtu m(s, 1); Rd105 r(m);
      auto i = r.read_device_info();
      CHECK(i.model == "215L" && i.firmware == "v1.3.0" && i.controller_mode && *i.controller_mode == 0);
      d.exception_at = 0x0004;
      i = r.read_device_info();
      CHECK(i.model == "215L" && i.firmware == "v1.3.0" && !i.controller_mode);
      d.exception_at = 0x0001;
      CHECK(r.read_device_info().model == "未知"); }
    // 表里没有的量如实拒绝，一帧都不发；通道量 / 通用量用错也拒绝
    { Slave d = device(); FakeSerial s([&d](const Bytes& q) { return d.handle(q); }); ModbusRtu m(s, 1); Rd105 r(m);
      int n = 0;
      try { r.query(1, "NOSUCH"); } catch (const Rd105Error& e) { n += std::string(e.what()).find("寄存器表里没有") != std::string::npos; }
      try { r.query(1, "OUTV"); } catch (const Rd105Error&) { n++; }
      try { r.query(0, "TG"); } catch (const Rd105Error&) { n++; }
      try { r.query(1, "FPWM"); } catch (const Rd105Error&) { n++; }
      CHECK(n == 4 && s.sent.empty()); }
    // 从站不答：报超时，话里带站号、寄存器地址和发出去的原始帧
    { Slave d = device(); d.mute = true; FakeSerial s([&d](const Bytes& q) { return d.handle(q); }); ModbusRtu m(s, 1, 50); m.retry_backoff_ms = 1; Rd105 r(m);
      bool got = false; std::string what;
      try { r.query(1, "TG"); } catch (const Rd105Error& e) { got = e.timeout; what = e.what(); }
      CHECK(got && what.find("Modbus-RTU 站号 1 读 0x1000") != std::string::npos && what.find("发 01 03 10 00 00 02 C0 CB｜收 无") != std::string::npos);
      got = false;
      try { r.read_device_info(); } catch (const Rd105Error& e) { got = e.timeout; }    // 设备信息：超时不吞
      CHECK(got); }
    // 应答前带 485 切换的脏字节（现场：F8 01 03 04 …）照样读得对
    { Slave d = device(); FakeSerial s([&d](const Bytes& q) { auto r = d.handle(q); r.insert(r.begin(), 0xF8); return r; }); ModbusRtu m(s, 1); Rd105 r(m);
      CHECK(r.query(1, "TG") == 2500000 && r.read_device_info().model == "215L"); }

    // ══ ASCII ══
    // 查询 / 设置的指令文本；应答带 OK 前缀和 \r\n
    { AsciiDev d; d.regs["TC1:TG"] = "2500000"; d.regs["ERRORCODE"] = "68"; d.regs["TC2:CURRENT"] = "3256";
      FakeSerial s([&d](const Bytes& q) { return d.handle(q); }); Rd105 r(s);
      CHECK(!r.is_modbus());
      CHECK(r.query(1, "TG") == 2500000 && text(s.sent[0]) == "TC1:TG=?@\n");
      CHECK(r.read_error_code() == 0x44 && text(s.sent[1]) == "ERRORCODE=?@\n");
      CHECK(near(r.read_current(2), 3.256) && text(s.sent[2]) == "TC2:CURRENT=?@\n");
      r.set_target(1, -15); CHECK(text(s.sent[3]) == "TC1:TG=-1500000@\n" && d.regs["TC1:TG"] == "-1500000");
      CHECK(near(r.set_duty_percent(1, -4.41), -4.41) && text(s.sent[4]) == "TC1:PWMDUTY=-88200@\n");
      r.set_mode(2, 3); r.set_enable(2, true); r.set_power_mode(2, 2); r.set_pwm_frequency(2);
      CHECK(text(s.sent[5]) == "TC2:MODE=3@\n" && text(s.sent[6]) == "TC2:ENABLE=1@\n" && text(s.sent[7]) == "TC2:POWERMODE=2@\n" && text(s.sent[8]) == "FPWM=2@\n");
      CHECK(near(r.read_duty_percent(1), -4.41)); }
    // 快照：一条 DATADEMAND=2，带电阻和输出电压；缺温度字段是坏帧
    { AsciiDev d; FakeSerial s([&d](const Bytes& q) { return d.handle(q); }); Rd105 r(s);
      auto snap = r.read_snapshot();
      CHECK(text(s.sent[0]) == "DATADEMAND=2@\n" && s.sent.size() == 1);
      CHECK(near(snap.temp_c[0], 22.59187) && std::isnan(snap.temp_c[1]) && snap.internal_c == 23);
      CHECK(near(snap.resistor_ohms[0], 11139.104486, 1e-6) && near(snap.out_volts[0], 10.0) && near(snap.out_volts[1], 0.0));
      d.datademand = "TC1:TCADJTEMP=2259187@SINTERIORTEMP=23@\r\n";
      bool got = false; try { r.read_snapshot(); } catch (const Rd105Error& e) { got = !e.timeout; }
      CHECK(got && s.sent.size() == 3);        // 坏帧重发过一次
      d.datademand = "TC1:TCADJTEMP=2500000@TC2:TCADJTEMP=2600000@SINTERIORTEMP=24@\n";     // 没有 OUTV / RESISTOR、不以 \r\n 结尾
      snap = r.read_snapshot();
      CHECK(near(snap.temp_c[1], 26.0) && std::isnan(snap.out_volts[0]) && std::isnan(snap.resistor_ohms[1])); }
    // 坏帧（'@' 打成 'P'）重发一次救回来；丢一帧重发一次救回来
    { AsciiDev d; d.regs["TC1:TG"] = "2500000"; FakeSerial s([&d](const Bytes& q) { return d.handle(q); }); Rd105 r(s);
      r.reply_timeout_ms = 30;
      d.garble_next = 1; CHECK(r.query(1, "TG") == 2500000 && s.sent.size() == 2);
      d.mute_next = 1; CHECK(r.query(1, "TG") == 2500000 && s.sent.size() == 4);
      d.garble_next = 2; bool got = false; try { r.query(1, "TG"); } catch (const Rd105Error& e) { got = !e.timeout; } CHECK(got);
      d.mute = true; got = false; std::string what;
      try { r.query(1, "TG"); } catch (const Rd105Error& e) { got = e.timeout; what = e.what(); }
      CHECK(got && what.find("TC1:TG=?@\\n") != std::string::npos && s.sent.size() == 8); }
    // 设置回显不符：再发一次，还不符抛
    { int n = 0; FakeSerial s([&n](const Bytes&) { n++; std::string r = "OKTC1:LIMITED=90@\r\n"; return Bytes(r.begin(), r.end()); }); Rd105 r(s);
      bool got = false; try { r.set_max_duty(1, 95); } catch (const Rd105Error& e) { got = !e.timeout; }
      CHECK(got && n == 2); }
    // 型号回的是文本；固件号是数字；CONTMODE 答得不像话就空着。全角冒号也认
    { AsciiDev d; d.regs["TEC"] = "215L"; d.regs["FPV"] = "130"; d.regs["CONTMODE"] = "abc";
      FakeSerial s([&d](const Bytes& q) { return d.handle(q); }); Rd105 r(s);
      auto i = r.read_device_info();
      CHECK(i.model == "215L" && i.firmware == "v1.3.0" && !i.controller_mode);
      CHECK(text(s.sent[0]) == "TEC=?@\n" && text(s.sent[1]) == "FPV=?@\n" && text(s.sent[2]) == "CONTMODE=?@\n"); }
    { FakeSerial s([](const Bytes&) { std::string r = "OKTC1\xEF\xBC\x9ATG=2500000@\r\n"; return Bytes(r.begin(), r.end()); }); Rd105 r(s);
      CHECK(r.query(1, "tg") == 2500000); }

    std::printf(fails ? "\n%d 项失败\n" : "RD105 协议层：全部通过\n", fails);
    return fails ? 1 : 0;
}
