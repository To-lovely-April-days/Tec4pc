// 整机节拍 + 硬件绑定层的测试：用两台假从站（温控器、继电器模块）跑几十拍，核对写到设备上的动作。
// 重点核三条规矩：继电器只一路一路写、DO2 / DO3 从不被碰、加热棒和 TEC 两只从不同时合着。
#include "../core/station/station.hpp"
#include "../drivers/io8r.hpp"
#include "../drivers/rd105.hpp"
#include "../io/fake_serial.hpp"
#include "../io/modbus_rtu.hpp"
#include <cstdio>
#include <map>
using namespace tec;
static int fails = 0;
#define CHECK(c) do { if (!(c)) { std::printf("失败 %s:%d  %s\n", __FILE__, __LINE__, #c); fails++; } } while (0)

// 一台什么都答应的假从站：保持寄存器和线圈各一张表，写了就记住，读就原样给
struct Slave {
    std::map<int, uint16_t> regs;
    bool coils[16] = {false};
    std::vector<int> coil_writes;      // 被 FC05 写过的线圈号（按次序）
    int fc0f = 0;                      // 收到过几次「多路一起写」
    bool violated = false;             // 出现过加热棒和 TEC 同时合着
    std::vector<uint8_t> answer(const std::vector<uint8_t>& q) {
        std::vector<uint8_t> r = {q[0], q[1]};
        const int addr = q[2] << 8 | q[3], n = q[4] << 8 | q[5];
        if (q[1] == 0x03 || q[1] == 0x04) { r.push_back(uint8_t(n * 2)); for (int i = 0; i < n; i++) { const uint16_t v = regs.count(addr + i) ? regs[addr + i] : 0; r.push_back(uint8_t(v >> 8)); r.push_back(uint8_t(v)); } }
        else if (q[1] == 0x10) { for (int i = 0; i < n; i++) regs[addr + i] = uint16_t(q[7 + i * 2] << 8 | q[8 + i * 2]); r.insert(r.end(), q.begin() + 2, q.begin() + 6); }
        else if (q[1] == 0x06) { regs[addr] = uint16_t(n); r.insert(r.end(), q.begin() + 2, q.begin() + 6); }
        else if (q[1] == 0x05) { coils[addr] = q[4] == 0xFF; coil_writes.push_back(addr); r.insert(r.end(), q.begin() + 2, q.begin() + 6);
                                 if ((coils[0] && coils[7]) || (coils[1] && coils[6])) violated = true; }
        else if (q[1] == 0x0F) { fc0f++; r.insert(r.end(), q.begin() + 2, q.begin() + 6); }
        const uint16_t c = modbus_crc(r.data(), r.size()); r.push_back(uint8_t(c)); r.push_back(uint8_t(c >> 8));
        return r;
    }
};

int main() {
    Slave tc, io;
    FakeSerial s_tc([&](const std::vector<uint8_t>& q) { return tc.answer(q); });
    FakeSerial s_io([&](const std::vector<uint8_t>& q) { return io.answer(q); });
    ModbusRtu m_tc(s_tc, 1), m_io(s_io, 1);
    m_tc.inter_frame_gap_ms = m_io.inter_frame_gap_ms = 0;
    Rd105 rd(m_tc);
    Io8r relay(m_io);
    RealReactorHardware hw(rd, relay, nullptr, nullptr);
    GainTable table({{0, 10, 0.03, 0, {}, 3.5}, {80, 10, 0.03, 0, {}, 3.5}});
    ChannelConfig cfg;
    Station st(hw, table, cfg);

    // 工位 A 夹套控温到 50 ℃（假设备读回的夹套是 0 ℃ → 要加热）
    st.start(0, ControlMode::Jacket, 50.0);
    for (int i = 0; i < 6; i++) st.step();
    auto sn = st.snapshot();
    CHECK(sn.ch[0].running && sn.ch[0].output_on && sn.ch[0].side == HeatSide::Heater);
    CHECK(sn.ch[0].duty < -1.0);                     // 负 = 加热棒在出力
    CHECK(io.coils[0] && !io.coils[7]);              // 工位 A：加热棒 DO0 合，TEC DO7 断
    CHECK(!sn.valve_open && !io.coils[4]);
    CHECK(!sn.ch[1].running && !io.coils[1] && !io.coils[6]);   // 工位 B 没动

    // 改成 −30 ℃：功率翻负，3 s 后开阀通水，再 5 s 后扳到 TEC
    st.set_setpoint(0, -30.0);
    bool saw_valve_before_tec = false;
    for (int i = 0; i < 40; i++) { st.step(); if (io.coils[4] && !io.coils[7]) saw_valve_before_tec = true; }
    sn = st.snapshot();
    CHECK(saw_valve_before_tec);                     // 先通水、后接 TEC
    CHECK(sn.ch[0].side == HeatSide::Tec && io.coils[7] && !io.coils[0]);
    CHECK(sn.valve_open && io.coils[4]);
    CHECK(sn.ch[0].duty > 1.0);                      // 正 = TEC 在制冷

    // 停控：输出关，这一路两只继电器都断
    st.stop(0);
    st.step();
    sn = st.snapshot();
    CHECK(!sn.ch[0].running && !sn.ch[0].output_on && !io.coils[0] && !io.coils[7]);

    // 三条规矩
    CHECK(io.fc0f == 0);                             // 从没用过「多路一起写」
    for (int c : io.coil_writes) CHECK(c != 2 && c != 3);   // DO2 / DO3 从没被碰
    CHECK(!io.violated);                             // 加热棒和 TEC 从没同时合着

    // 温控器不答话：连续读不到就停控
    st.start(1, ControlMode::Jacket, 40.0);
    for (int i = 0; i < 4; i++) st.step();
    CHECK(st.snapshot().ch[1].running);
    FakeSerial dead([](const std::vector<uint8_t>&) { return std::vector<uint8_t>{}; });
    ModbusRtu m_dead(dead, 1, 50); m_dead.timeout_retries = 0; m_dead.inter_frame_gap_ms = 0;
    Rd105 rd_dead(m_dead);
    RealReactorHardware hw_dead(rd_dead, relay, nullptr, nullptr);
    Station st2(hw_dead, table, cfg);
    st2.start(0, ControlMode::Jacket, 40.0);
    for (int i = 0; i < 8; i++) st2.step();
    CHECK(!st2.snapshot().ch[0].running && !st2.snapshot().comm_ok);

    // ── 安全联锁：用一个能随意给读数的假硬件 ──
    struct FakeHw : ReactorHardware {
        ReactorReadings r; int ended = 0;
        ReactorReadings read() override { return r; }
        void begin_control(int) override {}
        void apply(int, const ChannelCommand&) override {}
        void end_control(int) override { ended++; }
        void set_water(bool, bool) override {}
    } fh;
    fh.r.tj[0] = fh.r.tj[1] = 25; fh.r.tr[0] = 25.0; fh.r.tr[1] = 25.0;
    Station s3(fh, table, cfg);
    SafetyLimits lim; lim.tr_max = 80; s3.set_limits(0, lim);
    s3.start(0, ControlMode::Reactor, 60.0);
    for (int i = 0; i < 4; i++) s3.step();
    CHECK(s3.snapshot().ch[0].running && s3.alarms().empty());
    fh.r.tr[0] = 80.6;                               // 釜内超上限：这一拍就停，出一条报警
    s3.step(); s3.step();
    CHECK(!s3.snapshot().ch[0].running && fh.ended == 1);
    { auto al = s3.alarms(); CHECK(al.size() == 1 && !al[0].acked && al[0].ch == 0 && al[0].title.find("Tr 超过上限 80.0") != std::string::npos); s3.ack_alarm(al[0].id); }
    CHECK(s3.alarms()[0].acked);
    // 冷媒过热：只在这一路正用 TEC 制冷时才跳；没在制冷（加热中）水温再高也不管
    {
        FakeHw fc; fc.r.tj[0] = fc.r.tj[1] = 25; fc.r.tr[0] = 25.0; fc.r.tr[1] = 25.0; fc.r.coolant_c = 60.0;
        Station sc(fc, table, cfg);
        sc.start(0, ControlMode::Jacket, 60.0);                    // 往上升：加热棒，不用冷却水
        for (int i = 0; i < 40; i++) sc.step();
        CHECK(sc.snapshot().ch[0].running && sc.alarms().empty() && sc.snapshot().coolant_c.value_or(0) == 60.0);
        sc.stop(0);
        for (int i = 0; i < 4; i++) sc.step();
        fc.r.coolant_c = 30.0;
        sc.start(0, ControlMode::Jacket, 5.0);                     // 往下降：TEC 制冷
        bool cooling = false;
        for (int i = 0; i < 200 && !cooling; i++) { sc.step(); const auto c0 = sc.snapshot().ch[0]; cooling = c0.side == HeatSide::Tec && c0.output_on && c0.duty > 0; }
        CHECK(cooling && sc.alarms().empty());
        fc.r.coolant_c = 45.6;                                     // 水温过了 45
        sc.step();
        CHECK(!sc.snapshot().ch[0].running && sc.alarms().size() == 1 && sc.alarms()[0].title.find("冷媒温度过高 45.6") != std::string::npos);
    }
    // 釜内读不到：夹套控温不受影响；釜内控温过了宽限就停
    fh.r.tr[0].reset(); fh.r.tr[1].reset();
    s3.start(0, ControlMode::Jacket, 40.0); s3.start(1, ControlMode::Reactor, 40.0);
    for (int i = 0; i < 70; i++) s3.step();          // 35 s > 30 s 宽限
    CHECK(s3.snapshot().ch[0].running && !s3.snapshot().ch[1].running);
    // 夹套超过 200：停
    fh.r.tj[0] = 200.5; s3.step();
    CHECK(!s3.snapshot().ch[0].running);
    // 变温速率上限：1 K/min，实际按 3 K/min 升
    fh.r.tj[0] = 30; fh.r.tr[0] = 30.0; lim = SafetyLimits(); lim.rate_max_k_per_min = 1.0; s3.set_limits(0, lim);
    s3.start(0, ControlMode::Reactor, 60.0);
    for (int i = 0; i < 80 && s3.snapshot().ch[0].running; i++) { fh.r.tr[0] = 30.0 + i * 0.025; s3.step(); }
    CHECK(!s3.snapshot().ch[0].running);
    // 结晶和蒸回流能经整机这一层开起来
    fh.r.tr[1] = 60.0; fh.r.tj[1] = 62;
    s3.start_profile(1, {{45, 30}, {20, 50}});
    for (int i = 0; i < 4; i++) s3.step();
    CHECK(s3.snapshot().ch[1].mode == ControlMode::Crystal && s3.snapshot().ch[1].segment == 0 && s3.snapshot().ch[1].segment_remaining_s > 1790);

    std::printf(fails ? "\n%d 项失败\n" : "整机节拍与硬件绑定：全部通过\n", fails);
    return fails ? 1 : 0;
}
