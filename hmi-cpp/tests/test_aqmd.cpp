// AQMD 搅拌驱动器协议层的测试：假串口扮一台驱动器（一张寄存器表 + 03 / 06 / 10 三个功能码），
// 核对每个操作发出去的帧和解析出来的工程量。寄存器初值照 2026-11-09 现场串口工具读回的那两台填
// （型号名 AQMD2408BLS-M2、v1.3、额定电流 7 A、偶校验、通讯中断停机 0），跟 C# 那边的 AqmdStirrerDriverTests 同一套。
#include "../drivers/aqmd.hpp"
#include "../io/fake_serial.hpp"
#include <cmath>
#include <cstdio>
#include <map>
#include <string>
#include <vector>
using namespace tec;
static int fails = 0;
#define CHECK(c) do { if (!(c)) { std::printf("失败 %s:%d  %s\n", __FILE__, __LINE__, #c); fails++; } } while (0)
static std::vector<uint8_t> with_crc(std::vector<uint8_t> f) { const uint16_t c = modbus_crc(f.data(), f.size()); f.push_back(uint8_t(c)); f.push_back(uint8_t(c >> 8)); return f; }
static bool near(double a, double b) { return std::fabs(a - b) < 1e-9; }
static bool has(const std::string& s, const char* part) { return s.find(part) != std::string::npos; }

// 假驱动器：站号 1。reject_with 不为 0 时所有写都回这个异常码（现场没学习时吃到的 01 86 60 就是这么来的）。
struct FakeDrive {
    std::map<uint16_t, uint16_t> regs;
    int reject_with = 0;
    FakeSerial serial{[this](const std::vector<uint8_t>& q) { return answer(q); }};

    explicit FakeDrive(int poles = 4, bool learned = true) {
        regs[0x0000] = 0x0203; regs[0x0001] = 0x0103;
        const std::string name = "AQMD2408BLS-M2";
        for (size_t i = 0; i < 8; i++) {
            const uint8_t hi = 2 * i < name.size() ? name[2 * i] : 0, lo = 2 * i + 1 < name.size() ? name[2 * i + 1] : 0;
            regs[uint16_t(0x0002 + i)] = uint16_t(hi << 8 | lo);
        }
        regs[0x000A] = 1000; regs[0x000B] = 18000; regs[0x000C] = 1000; regs[0x000D] = 14;
        regs[0x0062] = 50000; regs[0x0063] = 8000; regs[0x0064] = 50000; regs[0x0065] = 8000;
        regs[0x0066] = 20000; regs[0x0067] = 8000;
        regs[0x006A] = 700; regs[0x006B] = 700; regs[0x006C] = 300;
        regs[0x0073] = uint16_t(poles); regs[0x0074] = 10; regs[0x0075] = learned ? 1 : 0; regs[0x0077] = 0;
        regs[0x0091] = 9600; regs[0x0092] = 2; regs[0x0093] = 1; regs[0x009C] = 1;
        regs[0x0034] = poles == 0 ? 0xFFFF : 0;   // 极数没配时驱动器自己算的 rpm 读回 0xFFFF（现场见过）
    }

    std::vector<uint8_t> answer(const std::vector<uint8_t>& q) {
        const uint8_t fc = q[1];
        const uint16_t addr = uint16_t(q[2] << 8 | q[3]), n = uint16_t(q[4] << 8 | q[5]);
        if (fc == 0x03) {
            std::vector<uint8_t> r = {q[0], fc, uint8_t(n * 2)};
            for (uint16_t i = 0; i < n; i++) { const uint16_t v = regs[uint16_t(addr + i)]; r.push_back(uint8_t(v >> 8)); r.push_back(uint8_t(v)); }
            return with_crc(r);
        }
        if (reject_with) return with_crc({q[0], uint8_t(fc | 0x80), uint8_t(reject_with)});
        if (fc == 0x06) { regs[addr] = n; return with_crc({q[0], fc, q[2], q[3], q[4], q[5]}); }
        if (fc == 0x10) {
            for (uint16_t i = 0; i < n; i++) regs[uint16_t(addr + i)] = uint16_t(q[7 + i * 2] << 8 | q[8 + i * 2]);
            return with_crc({q[0], fc, q[2], q[3], q[4], q[5]});
        }
        return {};
    }
    const std::vector<uint8_t>& last() const { return serial.sent.back(); }
};

int main() {
    // ── 换算 ──
    {
        CHECK(near(aqmd_rpm_to_hz(300, 4, 1.0), 60.0));              // 300 rpm × 4 极 ÷ 20
        CHECK(near(aqmd_hz_to_rpm(60.0, 4, 1.0), 300.0));
        CHECK(near(aqmd_hz_to_rpm(10, 4, 1.0), 50.0));               // 测速下限 10 Hz：4 极最低 50 rpm、8 极 25 rpm
        CHECK(near(aqmd_hz_to_rpm(10, 8, 1.0), 25.0));
        CHECK(near(aqmd_hz_to_rpm(60.0, 4, 2.0), 150.0));            // 带减速箱再 ÷ 减速比
        CHECK(near(aqmd_rpm_to_hz(300, 4, 0), 60.0));                // 减速比读成 0 当 1
        CHECK(std::isnan(aqmd_hz_to_rpm(60.0, 0, 1.0)));             // 极数没配：算不出，不编数
        CHECK(aqmd_speed_reg(60.0, false) == 600);                   // ×0.1 Hz 一格
        CHECK(aqmd_speed_reg(60.0, true) == 60);                     // 0x0077 = 1 时 ×1 Hz 一格
        CHECK(aqmd_speed_reg(-60.0, false) == -600);
        CHECK(aqmd_speed_reg(99999, false) == 32767 && aqmd_speed_reg(-99999, false) == -32768);
        CHECK(aqmd_speed_reg(0.25, false) == 2 && aqmd_speed_reg(0.35, false) == 4);   // 五成双，跟 C# Math.Round 一致
        CHECK(near(aqmd_frequency_of(600, false), 60.0) && near(aqmd_frequency_of(600, true), 600.0));
        CHECK(near(aqmd_frequency_of(uint16_t(int16_t(-600)), false), -60.0));
        CHECK(aqmd_accel_reg(40, 50000) == 400);                     // 满量程 200 Hz、5 s → 40 Hz/s
        CHECK(aqmd_accel_reg(0, 50000) == 1);                        // 0 写进去电机永远加不上去
        CHECK(aqmd_accel_reg(9000, 50000) == 50000);                 // 不超过驱动器的最大加速度
        CHECK(aqmd_accel_reg(40, 0) == 1);
    }
    // ── 设转速：300 rpm、4 极 → 0x0043 = 600；先写加减速再写目标 ──
    {
        FakeDrive d; ModbusRtu m(d.serial, 1); Aqmd a(m);
        a.write_accel(aqmd_accel_reg(40, 50000), aqmd_accel_reg(40, 50000));
        CHECK(d.last() == with_crc({0x01, 0x10, 0x00, 0x52, 0x00, 0x02, 0x04, 0x01, 0x90, 0x01, 0x90}));
        a.write_speed(aqmd_speed_reg(aqmd_rpm_to_hz(300, 4, 1.0), false));     // 写 0x0043 就是启动
        CHECK(d.last() == with_crc({0x01, 0x06, 0x00, 0x43, 0x02, 0x58}));
        CHECK(d.regs[0x0043] == 600 && d.regs[0x0052] == 400 && d.regs[0x0053] == 400);
        // 反转：目标写成负的换向频率（补码 0xFDA8）
        a.write_speed(aqmd_speed_reg(-aqmd_rpm_to_hz(300, 4, 1.0), false));
        CHECK(d.last() == with_crc({0x01, 0x06, 0x00, 0x43, 0xFD, 0xA8}));
    }
    // ── 停止：缺省正常停止 0x0040 = 0；另两种停法 ──
    {
        FakeDrive d; ModbusRtu m(d.serial, 1); Aqmd a(m);
        a.stop();
        CHECK(d.last() == with_crc({0x01, 0x06, 0x00, 0x40, 0x00, 0x00}));
        a.stop(AqmdStopMode::EmergencyBrake);
        CHECK(d.last() == with_crc({0x01, 0x06, 0x00, 0x40, 0x00, 0x01}));
        a.stop(AqmdStopMode::Coast);
        CHECK(d.last() == with_crc({0x01, 0x06, 0x00, 0x40, 0x00, 0x02}));
    }
    // ── 读实时状态：转速按换向频率和极数算，不信 0x0034；电流 ×0.01 A；故障、堵转 ──
    {
        FakeDrive d; ModbusRtu m(d.serial, 1); Aqmd a(m);
        d.regs[0x0020] = 355;        // PWM 35.5 %
        d.regs[0x0021] = 123;        // 1.23 A
        d.regs[0x0022] = 600;        // 60.0 Hz → 4 极 300 rpm
        d.regs[0x0034] = 0xFFFF;
        auto s = a.read_status();
        CHECK(d.last() == with_crc({0x01, 0x03, 0x00, 0x20, 0x00, 0x16}));     // 0x0020 起 22 个，一帧读全
        CHECK(near(s.pwm_percent, 35.5) && near(s.current_a, 1.23) && near(s.frequency_hz, 60.0));
        CHECK(near(s.rpm(4, 1.0), 300.0));
        CHECK(std::isnan(s.rpm(0, 1.0)));
        CHECK(s.rpm_raw == 0xFFFF && !s.rpm_x10);
        CHECK(s.fault == 0 && s.stall == 0);
        CHECK(aqmd_fault_text(s.fault) == "无故障" && aqmd_stall_text(s.stall) == "未堵转");

        // 反转时换向频率是负的，转速照样报正数；PWM 也是有符号的
        d.regs[0x0022] = uint16_t(int16_t(-600)); d.regs[0x0020] = uint16_t(int16_t(-355));
        s = a.read_status();
        CHECK(near(s.frequency_hz, -60.0) && near(s.rpm(4, 1.0), 300.0) && near(s.pwm_percent, -35.5));

        // 0x0035 = 1：换向频率的数值即 Hz
        d.regs[0x0022] = 600; d.regs[0x0035] = 1;
        s = a.read_status();
        CHECK(s.rpm_x10 && near(s.frequency_hz, 600.0));
        d.regs[0x0035] = 0;

        // 堵转停止：故障码 2 + 堵转标志 1，文字和处置办法都带得出来
        d.regs[0x0033] = 2; d.regs[0x0032] = 1;
        s = a.read_status();
        CHECK(s.fault == 2 && s.stall == 1);
        CHECK(aqmd_fault_text(2) == "堵转停止" && has(aqmd_fault_advice(2), "桨叶"));
        CHECK(aqmd_stall_text(1) == "正转堵转停止" && aqmd_stall_text(2) == "反转堵转停止");
        CHECK(aqmd_fault_text(6) == "过流关断" && aqmd_fault_text(42) == "未知故障码 42" && aqmd_fault_advice(0).empty());
    }
    // ── 身份 / 电机参数 / 系统参数 / 温度电压 ──
    {
        FakeDrive d(0); ModbusRtu m(d.serial, 1); Aqmd a(m);
        const auto id = a.read_identity();
        CHECK(d.last() == with_crc({0x01, 0x03, 0x00, 0x00, 0x00, 0x10}));
        CHECK(id.device_id == 0x0203 && id.version == "1.3" && id.name == "AQMD2408BLS-M2");
        CHECK(id.pwm_hz == 18000 && near(id.max_output_current_a, 10.0));
        CHECK(id.display() == "AQMD2408BLS-M2 v1.3");

        const auto mp = a.read_motor();
        CHECK(d.last() == with_crc({0x01, 0x03, 0x00, 0x60, 0x00, 0x1C}));
        CHECK(near(mp.max_frequency_hz, 2000.0) && mp.max_accel_reg == 50000 && mp.max_decel_reg == 50000);
        CHECK(near(mp.rated_current_a, 7.0) && near(mp.max_load_current_a, 7.0));
        CHECK(mp.poles == 0 && near(mp.gear_ratio, 1.0) && mp.learned && !mp.setpoint_x10 && mp.loop_algorithm == 0);

        const auto sys = a.read_system();
        CHECK(d.last() == with_crc({0x01, 0x03, 0x00, 0x80, 0x00, 0x20}));
        CHECK(sys.baud == 9600 && sys.parity_mode == 2 && sys.slave_address == 1 && sys.default_speed_mode == 1);
        CHECK(near(sys.watchdog_seconds, 0) && near(sys.stall_stop_seconds, 0) && !sys.alarms_disabled);
        CHECK(aqmd_frame_name(sys.parity_mode) == "偶校验 + 1 停止位");

        d.regs[0x0037] = 412; d.regs[0x0038] = 241;
        const auto tv = a.read_temp_volt();
        CHECK(d.last() == with_crc({0x01, 0x03, 0x00, 0x37, 0x00, 0x02}));
        CHECK(near(tv.temp_c, 41.2) && near(tv.volt, 24.1));
    }
    // ── 配置：极数、额定电流（连最大负载电流一起）、通讯中断停机、堵转停机，各自要存储才落盘 ──
    {
        FakeDrive d(0); ModbusRtu m(d.serial, 1); Aqmd a(m);
        a.write_poles(4);
        CHECK(d.last() == with_crc({0x01, 0x06, 0x00, 0x73, 0x00, 0x04}));
        a.write_rated_current(250);                                            // 2.50 A
        CHECK(d.last() == with_crc({0x01, 0x10, 0x00, 0x6A, 0x00, 0x02, 0x04, 0x00, 0xFA, 0x00, 0xFA}));
        a.store_motor_params();
        CHECK(d.last() == with_crc({0x01, 0x06, 0x01, 0x60, 0x00, 0x01}));
        const auto mp = a.read_motor();
        CHECK(mp.poles == 4 && near(mp.rated_current_a, 2.5) && near(mp.max_load_current_a, 2.5));

        a.write_watchdog(50);                                                  // 5 s
        CHECK(d.last() == with_crc({0x01, 0x06, 0x00, 0x95, 0x00, 0x32}));
        a.write_stall_stop(30);                                                // 3 s
        CHECK(d.last() == with_crc({0x01, 0x06, 0x00, 0x8E, 0x00, 0x1E}));
        a.store_system_params();
        CHECK(d.last() == with_crc({0x01, 0x06, 0x01, 0x80, 0x00, 0x01}));
        const auto sys = a.read_system();
        CHECK(near(sys.watchdog_seconds, 5.0) && near(sys.stall_stop_seconds, 3.0));
    }
    // ── 学习相序 ──
    {
        FakeDrive d(4, false); ModbusRtu m(d.serial, 1); Aqmd a(m);
        CHECK(!a.read_motor().learned);
        a.start_learn();
        CHECK(d.last() == with_crc({0x01, 0x06, 0x00, 0xE1, 0x00, 0x01}));
        d.regs[0x00E2] = 3;
        CHECK(a.read_learn_status() == 3);
        CHECK(d.last() == with_crc({0x01, 0x03, 0x00, 0xE2, 0x00, 0x01}));
        CHECK(aqmd_learn_status_text(3) == "学习完毕" && aqmd_learn_status_text(4) == "学习失败");
    }
    // ── 写被拒：没学习时驱动器回 01 86 60，报错要说人话、且不重发 ──
    {
        FakeDrive d; ModbusRtu m(d.serial, 1); Aqmd a(m);
        d.reject_with = 0x60;
        std::string said; int code = 0;
        try { a.write_speed(600); } catch (const ModbusError& e) { said = aqmd_explain_write_rejected(e); code = e.code.value_or(0); }
        CHECK(code == 0x60 && has(said, "0x60") && has(said, "学习"));
        CHECK(d.serial.sent.size() == 1);
        d.reject_with = 0x40; said.clear();
        try { a.write_accel(400, 400); } catch (const ModbusError& e) { said = aqmd_explain_write_rejected(e); }
        CHECK(has(said, "0x40") && has(said, "485 通讯方式"));
        // 链路层的错（没有异常码）原样带出
        CHECK(aqmd_explain_write_rejected(ModbusError("超时", std::nullopt, true)) == "超时");
    }
    std::printf(fails ? "\n%d 项失败\n" : "AQMD 搅拌驱动器：全部通过\n", fails);
    return fails ? 1 : 0;
}
