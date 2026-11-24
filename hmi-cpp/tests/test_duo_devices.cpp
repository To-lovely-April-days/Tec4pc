// 双工位主机那几样 Modbus 设备（IO8R、冷水机、宇电模块、BHT pH 电极）协议层的测试：
// 假串口扮从站，核对发出去的帧、量纲换算，和断线 / 超量程 / 标志锁住这些特殊值的判别。
#include "../drivers/bht_ph.hpp"
#include "../drivers/chiller.hpp"
#include "../drivers/io8r.hpp"
#include "../drivers/yudian.hpp"
#include "../io/fake_serial.hpp"
#include <cmath>
#include <cstdio>
#include <map>
#include <set>
#include <stdexcept>
using namespace tec;
static int fails = 0;
#define CHECK(c) do { if (!(c)) { std::printf("失败 %s:%d  %s\n", __FILE__, __LINE__, #c); fails++; } } while (0)
using Frame = std::vector<uint8_t>;
static Frame with_crc(Frame f) { const uint16_t c = modbus_crc(f.data(), f.size()); f.push_back(uint8_t(c)); f.push_back(uint8_t(c >> 8)); return f; }
static bool near(double a, double b) { return std::fabs(a - b) < 1e-9; }
static uint16_t u16(int v) { return static_cast<uint16_t>(static_cast<int16_t>(v)); }

// 一台够用的假从站：寄存器 / 位各一张表，照功能码答；可以指定某个功能码「回异常码」或「装死不答」
struct Slave {
    uint8_t station = 1;
    std::map<uint16_t, uint16_t> regs;      // FC03 / FC04 / FC06 共用
    uint8_t coils = 0, inputs = 0;          // 8 路，bit0 = 第 0 路
    std::map<uint8_t, uint8_t> reject;      // 功能码 → 异常码
    std::set<uint8_t> silent;               // 这些功能码不答
    Frame operator()(const Frame& q) {
        if (q[0] != station || silent.count(q[1])) return {};
        if (reject.count(q[1])) return with_crc({q[0], uint8_t(q[1] | 0x80), reject[q[1]]});
        const uint16_t addr = uint16_t(q[2] << 8 | q[3]), n = uint16_t(q[4] << 8 | q[5]);
        switch (q[1]) {
            case 0x01: return with_crc({q[0], q[1], 1, coils});
            case 0x02: return with_crc({q[0], q[1], 1, inputs});
            case 0x03: case 0x04: {
                Frame r = {q[0], q[1], uint8_t(n * 2)};
                for (uint16_t i = 0; i < n; i++) { const uint16_t v = regs.count(addr + i) ? regs[addr + i] : 0; r.push_back(uint8_t(v >> 8)); r.push_back(uint8_t(v)); }
                return with_crc(r);
            }
            case 0x05: if (addr < 8) { if (n == 0xFF00) coils |= uint8_t(1 << addr); else coils &= uint8_t(~(1 << addr)); } break;
            case 0x06: regs[addr] = n; break;
            case 0x0F: coils = q[7]; break;
            default: break;
        }
        return with_crc({q[0], q[1], q[2], q[3], q[4], q[5]});   // 写类：原样回声
    }
};

static void test_io8r() {
    Slave sl; sl.coils = 0x91; sl.inputs = 0x02;
    FakeSerial s(std::ref(sl)); ModbusRtu m(s, 1); Io8r io(m);
    // 读继电器 FC01、读 DI FC02：8 路，低位在前
    auto r = io.read_relays();
    CHECK(s.sent[0] == with_crc({0x01, 0x01, 0x00, 0x00, 0x00, 0x08}));
    CHECK(r[0] && !r[1] && !r[2] && !r[3] && r[4] && !r[5] && !r[6] && r[7]);
    auto d = io.read_inputs();
    CHECK(s.sent[1] == with_crc({0x01, 0x02, 0x00, 0x00, 0x00, 0x08}));
    CHECK(!d[0] && d[1] && !d[2] && !d[7]);
    // 切一路：DO3 吸合、DO4 断开
    io.set_relay(3, true); io.set_relay(4, false);
    CHECK(s.sent[2] == with_crc({0x01, 0x05, 0x00, 0x03, 0xFF, 0x00}));
    CHECK(s.sent[3] == with_crc({0x01, 0x05, 0x00, 0x04, 0x00, 0x00}));
    CHECK(sl.coils == 0x89);
    // 八路一帧写完：只留 DO5（冷却水阀留着、别的全断那种用法）
    Io8r::Points p{}; p[5] = true;
    io.write_relays(p);
    CHECK(s.sent[4] == with_crc({0x01, 0x0F, 0x00, 0x00, 0x00, 0x08, 0x01, 0x20}));
    // 全断：一帧 FC0F，不是八帧 FC05
    io.all_off();
    CHECK(s.sent[5] == with_crc({0x01, 0x0F, 0x00, 0x00, 0x00, 0x08, 0x01, 0x00}));
    CHECK(s.sent.size() == 6 && sl.coils == 0);
    // 路号越界：不发帧，直接抛
    bool threw = false;
    try { io.set_relay(8, true); } catch (const std::out_of_range&) { threw = true; }
    CHECK(threw && s.sent.size() == 6);
    threw = false;
    try { io.set_relay(-1, true); } catch (const std::out_of_range&) { threw = true; }
    CHECK(threw && s.sent.size() == 6);
}

static void test_chiller() {
    // 认 04 / 05 的固件：按表走
    { Slave sl; sl.regs[0x0000] = 2; sl.regs[0x0403] = 1;
      FakeSerial s(std::ref(sl)); ModbusRtu m(s, 1); Chiller c(m);
      CHECK(c.read_state() == 2);
      CHECK(s.sent[0] == with_crc({0x01, 0x04, 0x00, 0x00, 0x00, 0x01}));
      CHECK(c.read_start_stop_mode() == 1);
      CHECK(s.sent[1] == with_crc({0x01, 0x03, 0x04, 0x03, 0x00, 0x01}));
      c.start(); c.stop();
      CHECK(s.sent[2] == with_crc({0x01, 0x05, 0x02, 0x00, 0xFF, 0x00}));
      CHECK(s.sent[3] == with_crc({0x01, 0x05, 0x02, 0x01, 0xFF, 0x00}));
      CHECK(s.sent.size() == 4); }
    // 冷却水温度：0x0007，0.1 ℃，零下是补码；04 不认时和读状态一样换 03
    { Slave sl; sl.regs[0x0007] = 205;
      FakeSerial s(std::ref(sl)); ModbusRtu m(s, 1); Chiller c(m);
      CHECK(std::fabs(c.read_liquid_temp() - 20.5) < 1e-9);
      CHECK(s.sent[0] == with_crc({0x01, 0x04, 0x00, 0x07, 0x00, 0x01}));
      sl.regs[0x0007] = static_cast<uint16_t>(-35);
      CHECK(std::fabs(c.read_liquid_temp() + 3.5) < 1e-9); }
    { Slave sl; sl.regs[0x0007] = 300; sl.reject[0x04] = 0x01;
      FakeSerial s(std::ref(sl)); ModbusRtu m(s, 1); Chiller c(m);
      CHECK(std::fabs(c.read_liquid_temp() - 30.0) < 1e-9 && s.sent.size() == 2 && s.sent[1][1] == 0x03); }
    // 04 回「非法功能码」、05 回「非法地址」：换 03 / 06，换成了就一直用，不再回头撞
    { Slave sl; sl.regs[0x0000] = 3; sl.reject[0x04] = 0x01; sl.reject[0x05] = 0x02;
      FakeSerial s(std::ref(sl)); ModbusRtu m(s, 1); Chiller c(m);
      CHECK(c.read_state() == 3);
      CHECK(s.sent.size() == 2 && s.sent[1] == with_crc({0x01, 0x03, 0x00, 0x00, 0x00, 0x01}));
      CHECK(c.read_state() == 3);
      CHECK(s.sent.size() == 3 && s.sent[2][1] == 0x03);
      c.start();
      CHECK(s.sent.size() == 5 && s.sent[3][1] == 0x05 && s.sent[4] == with_crc({0x01, 0x06, 0x02, 0x00, 0x00, 0x72}));
      c.stop();
      CHECK(s.sent.size() == 6 && s.sent[5] == with_crc({0x01, 0x06, 0x02, 0x00, 0x00, 0x70})); }
    // 静默丢帧的固件（连异常码都不回）：超时也换
    { Slave sl; sl.regs[0x0000] = 1; sl.silent = {0x04, 0x05};
      FakeSerial s(std::ref(sl)); ModbusRtu m(s, 1, 50); m.timeout_retries = 0; Chiller c(m);
      CHECK(c.read_state() == 1);
      CHECK(s.sent.size() == 2 && s.sent[0][1] == 0x04 && s.sent[1][1] == 0x03);
      c.start();
      CHECK(s.sent.size() == 4 && s.sent[2][1] == 0x05 && s.sent[3] == with_crc({0x01, 0x06, 0x02, 0x00, 0x00, 0x72})); }
    // 别的异常码（04 = 从站设备故障）不是「不认这个功能码」：原样抛，不换路
    { Slave sl; sl.reject[0x04] = 0x04;
      FakeSerial s(std::ref(sl)); ModbusRtu m(s, 1); Chiller c(m);
      bool got = false;
      try { c.read_state(); } catch (const ModbusError& e) { got = e.code && *e.code == 4; }
      CHECK(got && s.sent.size() == 1);
      got = false;
      try { c.read_state(); } catch (const ModbusError&) { got = true; }
      CHECK(got && s.sent.size() == 2 && s.sent[1][1] == 0x04); }
    CHECK(Chiller::running(1) && Chiller::running(2) && !Chiller::running(0) && !Chiller::running(3) && !Chiller::running(4) && !Chiller::running(std::nullopt));
    CHECK(Chiller::state_name(std::nullopt) == "读不到" && Chiller::state_name(4) == "故障" && Chiller::state_name(9) == "未知状态 9");
}

// 宇电假从站的底子：四路各用各的组（In=1/2/3/4），dPt=2，型号字 8848
static Slave yudian_slave(std::array<int, 4> inp) {
    Slave sl;
    for (int i = 0; i < 4; i++) {
        sl.regs[uint16_t(384 + i)] = uint16_t(i + 1);
        sl.regs[uint16_t(2048 + i)] = uint16_t(inp[i]);
        sl.regs[uint16_t(2052 + i)] = 0;        // ScL
        sl.regs[uint16_t(2056 + i)] = 1400;     // ScH
    }
    sl.regs[2128] = 2; sl.regs[2130] = 0; sl.regs[2131] = 8848;
    return sl;
}

static void test_yudian() {
    // 测温：CH1 Pt100 两位小数、CH2 Pt100 一位小数、CH3 4~20mA（配错了）、CH4 关
    { Slave sl = yudian_slave({22, 21, 51, 22}); sl.regs[387] = 0; sl.regs[2073] = 1;   // CH4 In=0；AAF2 bit0 = 不自动复位
      FakeSerial s(std::ref(sl)); ModbusRtu m(s, 1); YudianProbe y(m);
      bool threw = false;
      try { y.read(); } catch (const std::logic_error&) { threw = true; }   // 没自检过不许读
      CHECK(threw && s.sent.empty() && !y.identity());
      const auto& id = y.init(YudianKind::Thermal);
      CHECK(s.sent.size() == 4);
      CHECK(s.sent[0] == with_crc({0x01, 0x03, 0x01, 0x80, 0x00, 0x04}));   // In 384
      CHECK(s.sent[1] == with_crc({0x01, 0x03, 0x08, 0x00, 0x00, 0x0C}));   // InP/ScL/ScH 2048 ×12
      CHECK(s.sent[2] == with_crc({0x01, 0x03, 0x08, 0x50, 0x00, 0x04}));   // dPt.. 2128
      CHECK(s.sent[3] == with_crc({0x01, 0x03, 0x08, 0x18, 0x00, 0x04}));   // AAF 2072
      CHECK(id.feature_word == 8848 && id.dpt == 2 && id.loc == 0 && !id.write_locked() && id.any_fault_latched());
      CHECK(id.channels[0].usable() && id.channels[0].group == 1 && id.channels[0].inp == 22 && near(*id.channels[0].divisor, 100));
      CHECK(id.channels[1].usable() && near(*id.channels[1].divisor, 10) && !id.channels[1].range_lo);
      CHECK(id.channels[2].enabled && !id.channels[2].usable() && id.channels[2].problem && !id.channels[2].divisor);
      CHECK(!id.channels[3].enabled && !id.channels[3].usable() && id.channels[3].type_name() == "关");
      CHECK(id.usable_channels() == std::vector<int>({1, 2}) && id.usable_list() == "CH1、CH2");
      CHECK(id.channels[0].type_name() == "Pt100 两位小数（InP=22）" && id.channels[2].type_name() == "4~20mA（InP=51，J4）");
      CHECK(id.dump() == "In=1/2/3/0，InP=22/21/51/22，ScL=0/0/0/0，ScH=1400/1400/1400/1400，AAF=0/1/0/0，dPt=2，Loc=0，型号字 8848");
      CHECK(y.identity().has_value());

      // 正常一拍：CH1 24.60 ℃、CH2 零下 -12.3 ℃（补码）、CH3 配错只给原始值、CH4 关
      sl.regs[1536] = 2460; sl.regs[1537] = u16(-123); sl.regs[1538] = u16(-1999); sl.regs[1539] = 777;
      sl.regs[1664] = 0x0000; sl.regs[1665] = 0x0101;                        // CH3、CH4 的 oral 位挂着
      auto r = y.read();
      CHECK(s.sent.size() == 6);
      CHECK(s.sent[4] == with_crc({0x01, 0x03, 0x06, 0x00, 0x00, 0x04}));   // PV 1536
      CHECK(s.sent[5] == with_crc({0x01, 0x03, 0x06, 0x80, 0x00, 0x02}));   // 报警 1664
      CHECK(r[0].value && near(*r[0].value, 24.60) && r[0].raw == 2460 && !r[0].sensor_fault && !r[0].alarm_latched);
      CHECK(r[1].value && near(*r[1].value, -12.3) && r[1].raw == -123 && !r[1].sensor_fault);
      CHECK(!r[2].value && r[2].raw == -1999 && r[2].sensor_fault && r[2].problem);      // 规格不对：没值，oral 位照传
      CHECK(!r[3].value && r[3].raw == 777 && !r[3].sensor_fault && !r[3].problem);      // 关着：报警位也不看

      // 断线 / 短路：量程外的码。CH1 311.11（开路，高于 300 上限）、CH2 -202.1（低于 -200 下限）；报警字全 0 也照判
      sl.regs[1536] = 31111; sl.regs[1537] = u16(-2021); sl.regs[1664] = 0x0000;
      r = y.read();
      CHECK(r[0].value && near(*r[0].value, 311.11) && r[0].sensor_fault && !r[0].alarm_latched);
      CHECK(r[1].value && near(*r[1].value, -202.1) && r[1].sensor_fault && !r[1].alarm_latched);
      // InP=22 压在下限之下：-20215 → -202.15（现场见过的那个数）
      sl.regs[1536] = u16(-20215);
      r = y.read();
      CHECK(near(*r[0].value, -202.15) && r[0].raw == -20215 && r[0].sensor_fault);
      // 量程边界上算在量程内
      sl.regs[1536] = 30000; sl.regs[1537] = u16(-2000);
      r = y.read();
      CHECK(!r[0].sensor_fault && !r[1].sensor_fault);

      // 标志锁住：oral 位挂着、值却在量程内——值照常用（不算故障），alarm_latched 单独报。
      // 高字节 = CH1，低字节 = CH2；顺带核对上限 / 下限报警位
      sl.regs[1536] = 2460; sl.regs[1537] = 250; sl.regs[1664] = 0x0306;    // CH1：oral + 上限；CH2：上限 + 下限
      r = y.read();
      CHECK(near(*r[0].value, 24.60) && !r[0].sensor_fault && r[0].alarm_latched && r[0].alarm_high && !r[0].alarm_low);
      CHECK(near(*r[1].value, 25.0) && !r[1].sensor_fault && !r[1].alarm_latched && r[1].alarm_high && r[1].alarm_low);
      // 量程外又挂着 oral：算断线，不算「标志锁住」
      sl.regs[1536] = 31111; sl.regs[1664] = 0x0100;
      r = y.read();
      CHECK(r[0].sensor_fault && !r[0].alarm_latched);

      // 写 InP：第 3 组写成 22 → 寄存器 2050；重新 init 读回核对
      y.write_inp(3, YudianProbe::kInpPt100x100);
      CHECK(s.sent.back() == with_crc({0x01, 0x06, 0x08, 0x02, 0x00, 0x16}));
      CHECK(y.init(YudianKind::Thermal).channels[2].usable() && y.identity()->channels[2].inp == 22);
      const size_t n = s.sent.size();
      threw = false;
      try { y.write_inp(0, 21); } catch (const std::out_of_range&) { threw = true; }
      CHECK(threw);
      threw = false;
      try { y.write_inp(5, 21); } catch (const std::out_of_range&) { threw = true; }
      CHECK(threw && s.sent.size() == n); }

    // 现场那台的常态：四路共用第 1 组（In=1/1/1/1），写一次 InP 四路都变；地址 2 的模块、Loc 锁着
    { Slave sl = yudian_slave({21, 51, 51, 51}); sl.station = 2;
      for (int i = 0; i < 4; i++) sl.regs[uint16_t(384 + i)] = 1;
      sl.regs[2130] = 0x20;
      FakeSerial s(std::ref(sl)); ModbusRtu m(s, 2); YudianProbe y(m);
      const auto& id = y.init(YudianKind::Thermal);
      CHECK(s.sent[0] == with_crc({0x02, 0x03, 0x01, 0x80, 0x00, 0x04}));
      CHECK(id.usable_channels() == std::vector<int>({1, 2, 3, 4}) && id.write_locked() && !id.any_fault_latched());
      CHECK(id.channels[3].group == 1 && id.channels[3].inp == 21); }

    // In 的个位 5~9 不是组号：不拿它去索引；十位以上不看（In=12 → 第 2 组）
    { Slave sl = yudian_slave({21, 22, 21, 21}); sl.regs[384] = 7; sl.regs[385] = 12;
      FakeSerial s(std::ref(sl)); ModbusRtu m(s, 1); YudianProbe y(m);
      const auto& id = y.init(YudianKind::Thermal);
      CHECK(id.channels[0].enabled && !id.channels[0].usable() && id.channels[0].problem && id.channels[0].inp == -1);
      CHECK(id.channels[1].usable() && id.channels[1].group == 2 && id.channels[1].inp == 22); }

    // 测温口上的别的规格：mV 口读不了、表里没有的规格不猜
    { Slave sl = yudian_slave({25, 99, 0, 23});
      FakeSerial s(std::ref(sl)); ModbusRtu m(s, 1); YudianProbe y(m);
      const auto& id = y.init(YudianKind::Thermal);
      CHECK(!id.channels[0].usable() && !id.channels[1].usable() && id.channels[1].type_name() == "InP=99");
      CHECK(id.channels[2].usable() && near(*id.channels[2].divisor, 10) && id.channels[3].usable() && near(*id.channels[3].divisor, 100)); }

    // 线性电流（pH 变送器 4~20mA）：dPt=2、ScL=0、ScH=1400 → 0.00~14.00；测温口在这种探头眼里读不了
    { Slave sl = yudian_slave({51, 50, 21, 51}); sl.regs[2055] = u16(-200);   // 第 4 组 ScL = -2.00
      FakeSerial s(std::ref(sl)); ModbusRtu m(s, 1); YudianProbe y(m);
      const auto& id = y.init(YudianKind::Linear);
      CHECK(id.channels[0].usable() && near(*id.channels[0].divisor, 100) && near(*id.channels[0].range_lo, 0) && near(*id.channels[0].range_hi, 14));
      CHECK(id.channels[1].usable() && !id.channels[2].usable() && id.channels[2].problem);
      CHECK(near(*id.channels[3].range_lo, -2.0));
      CHECK(id.usable_list() == "CH1、CH2、CH4");
      sl.regs[1536] = 700; sl.regs[1537] = 1500; sl.regs[1538] = 246; sl.regs[1539] = u16(-100);
      sl.regs[1664] = 0x0100; sl.regs[1665] = 0x0000;
      auto r = y.read();
      CHECK(near(*r[0].value, 7.00) && !r[0].sensor_fault && r[0].alarm_latched);
      CHECK(near(*r[1].value, 15.00) && r[1].sensor_fault);                  // 超出定标量程 = 变送器断线 / 超量程
      CHECK(!r[2].value && r[2].raw == 246);
      CHECK(near(*r[3].value, -1.0) && !r[3].sensor_fault); }

    CHECK(YudianProbe::inp_name(21) == "Pt100（InP=21）" && YudianProbe::inp_name(0) == "K 偶（InP=0）" && YudianProbe::inp_name(23) == "Pt1000（InP=23，J0/J2）");
    CHECK(YudianProbe::range_of(22) && near(YudianProbe::range_of(22)->first, -200) && near(YudianProbe::range_of(22)->second, 300));
    CHECK(near(YudianProbe::range_of(21)->second, 800) && !YudianProbe::range_of(51) && !YudianProbe::range_of(25));
    CHECK(YudianProbe::kind_name(YudianKind::Thermal) == "测温" && YudianProbe::kind_name(YudianKind::Linear) == "线性电流");
}

static void test_bht_ph() {
    // 现场 COM12 那支实测的一帧：温度 15.8 ℃、pH 4.62、电位 136.6 mV、温度状态 0；出厂地址 2
    Slave sl; sl.station = 2;
    sl.regs[0] = 158; sl.regs[1] = 462; sl.regs[2] = 250; sl.regs[3] = 1366; sl.regs[5] = 0;
    sl.regs[8] = 2; sl.regs[9] = 9600; sl.regs[25] = 1;
    sl.regs[14] = u16(-312); sl.regs[15] = 98; sl.regs[20] = 1;
    FakeSerial s(std::ref(sl)); ModbusRtu m(s, BhtPh::kDefaultAddress); BhtPh ph(m);

    auto v = ph.read_live();
    CHECK(s.sent.size() == 1 && s.sent[0] == with_crc({0x02, 0x03, 0x00, 0x00, 0x00, 0x06}));
    CHECK(near(v.temp_c, 15.8) && near(v.ph, 4.62) && near(v.manual_temp_c, 25.0) && near(v.mv, 136.6) && v.temp_status == 0);
    // 电位有符号（pH 7 以上是负的）；温度状态 2 = 未接传感器
    sl.regs[1] = 918; sl.regs[3] = u16(-1284); sl.regs[5] = 2;
    v = ph.read_live();
    CHECK(near(v.ph, 9.18) && near(v.mv, -128.4) && v.temp_status == 2);

    auto cal = ph.read_calibration();
    CHECK(s.sent[2] == with_crc({0x02, 0x03, 0x00, 0x0E, 0x00, 0x02}) && s.sent[3] == with_crc({0x02, 0x03, 0x00, 0x14, 0x00, 0x01}));
    CHECK(near(cal.zero_mv, -3.12) && near(cal.slope_percent, 98) && cal.buffer_set == 1);
    auto comm = ph.read_comm();
    CHECK(s.sent[4] == with_crc({0x02, 0x03, 0x00, 0x08, 0x00, 0x02}) && s.sent[5] == with_crc({0x02, 0x03, 0x00, 0x19, 0x00, 0x01}));
    CHECK(comm.address == 2 && comm.baud == 9600 && comm.frame == 1);

    // 标定：寄存器 13 写命令字，结果码从同一个寄存器读回
    ph.start_calibration(BhtCalPoint::Zero);
    CHECK(s.sent.back() == with_crc({0x02, 0x06, 0x00, 0x0D, 0x00, 0x01}));
    ph.start_calibration(BhtCalPoint::SlopeLow);
    CHECK(s.sent.back() == with_crc({0x02, 0x06, 0x00, 0x0D, 0x00, 0x02}));
    ph.start_calibration(BhtCalPoint::SlopeHigh);
    CHECK(s.sent.back() == with_crc({0x02, 0x06, 0x00, 0x0D, 0x00, 0x03}));
    sl.regs[13] = 0x20;
    CHECK(ph.read_calibration_result() == 0x20);
    CHECK(s.sent.back() == with_crc({0x02, 0x03, 0x00, 0x0D, 0x00, 0x01}));

    // 参数写入的编码
    ph.set_buffer_set(1);   CHECK(s.sent.back() == with_crc({0x02, 0x06, 0x00, 0x14, 0x00, 0x01}));
    ph.set_buffer_set(0);   CHECK(s.sent.back() == with_crc({0x02, 0x06, 0x00, 0x14, 0x00, 0x00}));
    ph.set_zero_mv(-3.12);  CHECK(s.sent.back() == with_crc({0x02, 0x06, 0x00, 0x0E, 0xFE, 0xC8}));   // -312 的补码
    ph.set_zero_mv(100);    CHECK(s.sent.back() == with_crc({0x02, 0x06, 0x00, 0x0E, 0x17, 0x1C}));   // 按 +59.16 写 = 5916
    ph.set_zero_mv(-100);   CHECK(sl.regs[14] == u16(-5916));
    ph.set_baud(19200);     CHECK(s.sent.back() == with_crc({0x02, 0x06, 0x00, 0x09, 0x4B, 0x00}));
    ph.set_frame(1);        CHECK(s.sent.back() == with_crc({0x02, 0x06, 0x00, 0x19, 0x00, 0x01}));
    ph.set_address(5);      CHECK(s.sent.back() == with_crc({0x02, 0x06, 0x00, 0x08, 0x00, 0x05}));
    ph.set_address(999);    CHECK(sl.regs[8] == 247);
    ph.restore_factory();   CHECK(s.sent.back() == with_crc({0x02, 0x06, 0x00, 0x0A, 0x07, 0xCC}));   // 1996
    ph.reboot();            CHECK(s.sent.back() == with_crc({0x02, 0x06, 0x00, 0x0B, 0x05, 0xF4}));   // 1524
    ph.write(BhtPh::kRegBuffer, 0); CHECK(s.sent.back() == with_crc({0x02, 0x06, 0x00, 0x14, 0x00, 0x00}));
    // 不认的波特率 / 数据格式：不发帧，直接抛
    const size_t n = s.sent.size();
    bool threw = false;
    try { ph.set_baud(38400); } catch (const std::invalid_argument&) { threw = true; }
    CHECK(threw);
    threw = false;
    try { ph.set_frame(4); } catch (const std::invalid_argument&) { threw = true; }
    CHECK(threw && s.sent.size() == n);

    CHECK(BhtPh::temp_status_text(0) == "正常" && BhtPh::temp_status_text(2) == "未接温度传感器" && BhtPh::temp_status_text(7) == "状态码 7");
    CHECK(BhtPh::buffer_text(1) == "4.00 / 6.86 / 9.18" && BhtPh::buffer_text(0) == "4.00 / 7.00 / 10.00");
    CHECK(BhtPh::frame_text(1) == "8N1" && BhtPh::frame_text(0) == "8N2" && BhtPh::frame_text(9) == "格式码 9");
    CHECK(BhtPh::calibration_result_text(0) == "标定成功" && BhtPh::calibration_result_text(0x80).find("温度超出范围") != std::string::npos);
}

// 两台宇电模块共一条串口（地址 1 和 2）、共一把锁：各问各的站号，答话不串
static void test_shared_port() {
    Slave a = yudian_slave({22, 22, 22, 22}), b = yudian_slave({22, 22, 22, 22});
    b.station = 2; a.regs[1536] = 2500; b.regs[1536] = 4000;
    FakeSerial s([&](const Frame& q) { return q[0] == 1 ? a(q) : b(q); });
    auto lock = std::make_shared<std::mutex>();
    ModbusRtu m1(s, 1, 500, lock), m2(s, 2, 500, lock);
    YudianProbe y1(m1), y2(m2);
    y1.init(YudianKind::Thermal); y2.init(YudianKind::Thermal);
    CHECK(near(*y1.read()[0].value, 25.0) && near(*y2.read()[0].value, 40.0));
    CHECK(s.sent[0][0] == 1 && s.sent[4][0] == 2);
}

int main() {
    test_io8r();
    test_chiller();
    test_yudian();
    test_bht_ph();
    test_shared_port();
    std::printf(fails ? "\n%d 项失败\n" : "双工位设备协议层：全部通过\n", fails);
    return fails ? 1 : 0;
}
