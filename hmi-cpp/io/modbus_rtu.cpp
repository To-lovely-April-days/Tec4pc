#include "modbus_rtu.hpp"
#include <chrono>
#include <cstdio>
#include <thread>

namespace tec {

namespace {
long long now_ms() {
    return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
}
void sleep_ms(long long ms) { if (ms > 0) std::this_thread::sleep_for(std::chrono::milliseconds(ms)); }
std::string hex(const std::vector<uint8_t>& v) {
    std::string s;
    char b[4];
    for (size_t i = 0; i < v.size(); i++) { std::snprintf(b, sizeof b, i ? " %02X" : "%02X", v[i]); s += b; }
    return s;
}
// 不可能是站号的字节（0 是广播、248–255 协议保留）才当毛刺滑过去；别的站号答话是配错了站号，要原样报出来
bool is_noise(uint8_t b) { return b == 0 || b > 247; }
}  // namespace

uint16_t modbus_crc(const uint8_t* data, size_t n) {
    uint16_t crc = 0xFFFF;
    for (size_t i = 0; i < n; i++) {
        crc ^= data[i];
        for (int k = 0; k < 8; k++) crc = (crc & 1) ? static_cast<uint16_t>((crc >> 1) ^ 0xA001) : static_cast<uint16_t>(crc >> 1);
    }
    return crc;   // 发的时候低字节在前
}

ModbusRtu::ModbusRtu(SerialTransport& transport, uint8_t station, int timeout_ms, std::shared_ptr<std::mutex> bus_lock)
    : transport_(transport), station_(station), timeout_ms_(timeout_ms < 50 ? 50 : timeout_ms),
      lock_(bus_lock ? std::move(bus_lock) : std::make_shared<std::mutex>()) {
    if (station == 0 || station > 247) throw std::invalid_argument("Modbus 从站地址要在 1~247（0 是广播，收不到应答）");
}

std::vector<bool> ModbusRtu::read_coils(uint16_t start, int count) { return read_bits(0x01, start, count); }
std::vector<bool> ModbusRtu::read_discrete_inputs(uint16_t start, int count) { return read_bits(0x02, start, count); }
std::vector<uint16_t> ModbusRtu::read_holding_registers(uint16_t start, int count) { return read_regs(0x03, start, count); }
std::vector<uint16_t> ModbusRtu::read_input_registers(uint16_t start, int count) { return read_regs(0x04, start, count); }

static void verify_echo(uint16_t a, int b, const std::vector<uint8_t>& body) {
    const int ea = body[0] << 8 | body[1], eb = body[2] << 8 | body[3];
    if (ea != a || eb != b) throw ModbusError("写入的回声对不上：发的和从站答回来的不是同一个地址 / 值");
}

void ModbusRtu::write_coil(uint16_t address, bool on) {
    const int v = on ? 0xFF00 : 0x0000;
    verify_echo(address, v, transact(0x05, {uint8_t(address >> 8), uint8_t(address), uint8_t(v >> 8), uint8_t(v)}));
}

void ModbusRtu::write_register(uint16_t address, uint16_t value) {
    verify_echo(address, value, transact(0x06, {uint8_t(address >> 8), uint8_t(address), uint8_t(value >> 8), uint8_t(value)}));
}

void ModbusRtu::write_coils(uint16_t start, const std::vector<bool>& values) {
    const int n = static_cast<int>(values.size());
    if (n < 1 || n > 1968) throw std::invalid_argument("一次写 1~1968 个线圈");
    const int nb = (n + 7) / 8;
    std::vector<uint8_t> p(5 + nb, 0);
    p[0] = uint8_t(start >> 8); p[1] = uint8_t(start); p[2] = uint8_t(n >> 8); p[3] = uint8_t(n); p[4] = uint8_t(nb);
    for (int i = 0; i < n; i++) if (values[i]) p[5 + i / 8] |= uint8_t(1 << (i % 8));
    verify_echo(start, n, transact(0x0F, p));
}

void ModbusRtu::write_registers(uint16_t start, const std::vector<uint16_t>& values) {
    const int n = static_cast<int>(values.size());
    if (n < 1 || n > 123) throw std::invalid_argument("一次写 1~123 个寄存器");
    std::vector<uint8_t> p(5 + n * 2);
    p[0] = uint8_t(start >> 8); p[1] = uint8_t(start); p[2] = uint8_t(n >> 8); p[3] = uint8_t(n); p[4] = uint8_t(n * 2);
    for (int i = 0; i < n; i++) { p[5 + i * 2] = uint8_t(values[i] >> 8); p[6 + i * 2] = uint8_t(values[i]); }
    verify_echo(start, n, transact(0x10, p));
}

std::vector<bool> ModbusRtu::read_bits(uint8_t fc, uint16_t start, int count) {
    if (count < 1 || count > 2000) throw std::invalid_argument("一次读 1~2000 个位");
    const auto body = transact(fc, {uint8_t(start >> 8), uint8_t(start), uint8_t(count >> 8), uint8_t(count)}, (count + 7) / 8);
    std::vector<bool> bits(count);
    for (int i = 0; i < count; i++) bits[i] = (body[1 + i / 8] >> (i % 8) & 1) != 0;   // 低位在前：第 0 点在首字节 bit0
    return bits;
}

std::vector<uint16_t> ModbusRtu::read_regs(uint8_t fc, uint16_t start, int count) {
    if (count < 1 || count > 125) throw std::invalid_argument("一次读 1~125 个寄存器");
    const auto body = transact(fc, {uint8_t(start >> 8), uint8_t(start), uint8_t(count >> 8), uint8_t(count)}, count * 2);
    std::vector<uint16_t> regs(count);
    for (int i = 0; i < count; i++) regs[i] = static_cast<uint16_t>(body[1 + i * 2] << 8 | body[2 + i * 2]);
    return regs;
}

std::vector<uint8_t> ModbusRtu::transact(uint8_t fc, const std::vector<uint8_t>& payload, int expected_data_bytes) {
    std::lock_guard<std::mutex> g(*lock_);      // 半双工总线，一次只许一问
    std::vector<uint8_t> frame;
    frame.push_back(station_); frame.push_back(fc);
    frame.insert(frame.end(), payload.begin(), payload.end());
    const uint16_t crc = modbus_crc(frame.data(), frame.size());
    frame.push_back(uint8_t(crc)); frame.push_back(uint8_t(crc >> 8));
    for (int attempt = 0;; attempt++) {
        sleep_ms(inter_frame_gap_ms - (now_ms() - last_frame_end_));   // RTU 靠 3.5 个字符的静默分帧，相邻两帧之间留空当
        transport_.discard_input();
        transport_.write(frame.data(), frame.size());
        rx_trace_.clear();
        try {
            auto body = read_response(fc, expected_data_bytes);
            last_frame_end_ = now_ms();
            return body;
        } catch (const ModbusError& e) {
            last_frame_end_ = now_ms();
            const bool link = !e.code;
            if (link && attempt < (e.timeout ? timeout_retries : corrupt_retries)) { sleep_ms(retry_backoff_ms); continue; }
            // 报错把这一问一答的原始字节带上：现场对着串口助手一眼能看出是没发对、没收到，还是收到的不是想要的。
            // 字节只缀在 what() 后面（记录、PC 端看得到），不进要翻的那一句——
            // 缀进去整句就不是词表里的那一条了，触摸屏上这句话反而翻不了
            ModbusError out(e.text, "｜发 " + hex(frame) + "｜收 " + (rx_trace_.empty() ? "无" : hex(rx_trace_)), e.code, e.timeout);
            out.heard_station = e.heard_station;      // 「答话的是几号站」要带出去：自动检测靠它找改过站号的设备
            throw out;
        }
    }
}

void ModbusRtu::read_exact(uint8_t* buf, size_t n, long long deadline, const char* what) {
    size_t got = 0;
    long long last_byte = now_ms();
    while (got < n) {
        const long long remain = deadline - now_ms();
        if (remain <= 0)
            throw ModbusError("{0} ms 内没等到完整{1}——从站没上电、站号 / 波特率不对，或者线断了", {std::to_string(timeout_ms_), what}, std::nullopt, true);
        if (got > 0 && now_ms() - last_byte > inter_byte_idle_ms)
            throw ModbusError("应答烂尾：{0}收到一半后没有新字节——多半是 485 上的干扰把这一帧掐断了", {what}, std::nullopt, true);
        const int k = transport_.read(buf + got, n - got, static_cast<int>(remain < 20 ? remain : 20));
        if (k > 0) {
            rx_trace_.insert(rx_trace_.end(), buf + got, buf + got + k);
            got += static_cast<size_t>(k);
            last_byte = now_ms();
        }
    }
}

std::vector<uint8_t> ModbusRtu::read_response(uint8_t fc, int expected_data_bytes) {
    const long long deadline = now_ms() + timeout_ms_;
    uint8_t head[2];
    read_exact(head, 2, deadline, "应答");
    int noise = 0;
    while (head[0] != station_ && is_noise(head[0]) && noise < max_leading_noise) {
        noise++;
        head[0] = head[1];
        read_exact(&head[1], 1, deadline, "应答");
    }
    if (head[0] != station_)
        throw ModbusError("应答站号不对：问的是 {0} 号，答话的是 {1} 号——查一下站号和波特率",
                          {std::to_string(station_), std::to_string(head[0])}).heard(head[0]);
    auto check_crc = [](const std::vector<uint8_t>& whole, uint8_t lo, uint8_t hi) {
        const uint16_t c = modbus_crc(whole.data(), whole.size());
        if (uint8_t(c) != lo || uint8_t(c >> 8) != hi) throw ModbusError("应答校验（CRC）不过：这一帧在线上被打坏了");
    };
    if (head[1] == (fc | 0x80)) {
        uint8_t tail[3];
        read_exact(tail, 3, deadline, "异常应答");
        check_crc({head[0], head[1], tail[0]}, tail[1], tail[2]);
        throw ModbusError("从站回了异常码 {0}", {std::to_string(tail[0])}, tail[0]);
    }
    if (head[1] != fc) throw ModbusError("应答功能码不对");
    if (fc >= 0x01 && fc <= 0x04) {
        uint8_t cnt;
        read_exact(&cnt, 1, deadline, "应答长度");
        if (expected_data_bytes >= 0 && cnt != expected_data_bytes)
            throw ModbusError("应答长度不对：多半是上一问迟到的应答串了帧");
        std::vector<uint8_t> rest(cnt + 2);
        read_exact(rest.data(), rest.size(), deadline, "应答数据");
        std::vector<uint8_t> whole = {head[0], head[1], cnt};
        whole.insert(whole.end(), rest.begin(), rest.begin() + cnt);
        check_crc(whole, rest[cnt], rest[cnt + 1]);
        std::vector<uint8_t> body = {cnt};
        body.insert(body.end(), rest.begin(), rest.begin() + cnt);
        return body;
    }
    uint8_t rest[6];
    read_exact(rest, 6, deadline, "应答数据");
    check_crc({head[0], head[1], rest[0], rest[1], rest[2], rest[3]}, rest[4], rest[5]);
    return {rest[0], rest[1], rest[2], rest[3]};
}

}  // namespace tec
