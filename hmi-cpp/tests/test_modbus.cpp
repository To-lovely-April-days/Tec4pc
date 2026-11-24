// Modbus RTU 主站的测试：用假串口扮从站，核对发出去的帧、解析、脏字节、坏帧重发、异常码。
#include "../io/fake_serial.hpp"
#include "../io/modbus_rtu.hpp"
#include <cstdio>
#include <vector>
using namespace tec;
static int fails = 0;
#define CHECK(c) do { if (!(c)) { std::printf("失败 %s:%d  %s\n", __FILE__, __LINE__, #c); fails++; } } while (0)
static std::vector<uint8_t> with_crc(std::vector<uint8_t> f) { const uint16_t c = modbus_crc(f.data(), f.size()); f.push_back(uint8_t(c)); f.push_back(uint8_t(c >> 8)); return f; }

int main() {
    // 标准例子：01 03 00 00 00 0A 的 CRC 是 C5 CD
    { const uint8_t f[] = {0x01, 0x03, 0x00, 0x00, 0x00, 0x0A}; CHECK(modbus_crc(f, 6) == 0xCDC5); }
    // 读两个保持寄存器；应答前面带一个 485 毛刺字节
    { FakeSerial s([](const std::vector<uint8_t>&) { auto r = with_crc({0x01, 0x03, 0x04, 0x12, 0x34, 0xAB, 0xCD}); r.insert(r.begin(), 0xF8); return r; });
      ModbusRtu m(s, 1); auto v = m.read_holding_registers(0x10, 2);
      CHECK(v.size() == 2 && v[0] == 0x1234 && v[1] == 0xABCD);
      CHECK(s.sent[0] == with_crc({0x01, 0x03, 0x00, 0x10, 0x00, 0x02})); }
    // 读 8 路线圈，低位在前
    { FakeSerial s([](const std::vector<uint8_t>&) { return with_crc({0x02, 0x01, 0x01, 0x91}); });
      ModbusRtu m(s, 2); auto b = m.read_coils(0, 8);
      CHECK(b[0] && !b[1] && !b[2] && !b[3] && b[4] && !b[5] && !b[6] && b[7]); }
    // 写线圈、写寄存器、写多线圈、写多寄存器：从站原样回声
    { FakeSerial s([](const std::vector<uint8_t>& q) { return with_crc({q[0], q[1], q[2], q[3], q[4], q[5]}); });
      ModbusRtu m(s, 1); m.write_coil(4, true); m.write_register(0x20, 300); m.write_coils(0, std::vector<bool>(8, false)); m.write_registers(5, {1, 2, 3});
      CHECK(s.sent[0] == with_crc({0x01, 0x05, 0x00, 0x04, 0xFF, 0x00}));
      CHECK(s.sent[1] == with_crc({0x01, 0x06, 0x00, 0x20, 0x01, 0x2C}));
      CHECK(s.sent[2] == with_crc({0x01, 0x0F, 0x00, 0x00, 0x00, 0x08, 0x01, 0x00}));
      CHECK(s.sent[3] == with_crc({0x01, 0x10, 0x00, 0x05, 0x00, 0x03, 0x06, 0x00, 0x01, 0x00, 0x02, 0x00, 0x03})); }
    // 第一次回一帧 CRC 坏的，第二次回好的：应当自动重发一次
    { int n = 0; FakeSerial s([&n](const std::vector<uint8_t>&) { auto r = with_crc({0x01, 0x03, 0x02, 0x00, 0x07}); if (n++ == 0) r.back() ^= 0xFF; return r; });
      ModbusRtu m(s, 1); m.retry_backoff_ms = 1; auto v = m.read_holding_registers(0, 1);
      CHECK(v[0] == 7 && s.sent.size() == 2); }
    // 从站回异常码 02：不重试，带出异常码
    { FakeSerial s([](const std::vector<uint8_t>&) { return with_crc({0x01, 0x83, 0x02}); });
      ModbusRtu m(s, 1); bool got = false;
      try { m.read_holding_registers(0, 1); } catch (const ModbusError& e) { got = e.code && *e.code == 2; }
      CHECK(got && s.sent.size() == 1); }
    // 从站不答话：超时，重发够次数后认输
    { FakeSerial s([](const std::vector<uint8_t>&) { return std::vector<uint8_t>{}; });
      ModbusRtu m(s, 1, 50); m.retry_backoff_ms = 1; bool got = false;
      try { m.read_holding_registers(0, 1); } catch (const ModbusError& e) { got = e.timeout; }
      CHECK(got && s.sent.size() == 3); }
    std::printf(fails ? "\n%d 项失败\n" : "Modbus 主站：全部通过\n", fails);
    return fails ? 1 : 0;
}
