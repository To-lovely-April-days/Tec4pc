// 串口的最小抽象。真机上是 Windows 串口（win_serial），测试里换成假串口（fake_serial），整条链路不接硬件也能跑一遍。
#pragma once
#include <cstddef>
#include <cstdint>
#include <string>

namespace tec {

class SerialTransport {
public:
    virtual ~SerialTransport() = default;
    virtual bool is_open() const = 0;
    virtual void open() = 0;                 // 打不开抛 std::runtime_error
    virtual void close() = 0;
    virtual void discard_input() = 0;        // 清空接收缓冲：发新一问之前，把上一问迟到的字节扔掉
    virtual void write(const uint8_t* data, size_t n) = 0;
    // 最多读 n 个字节；timeout_ms 内一个字节都没来就返回 0
    virtual int read(uint8_t* buf, size_t n, int timeout_ms) = 0;
};

struct SerialConfig {
    std::string port;          // 如 "COM7"
    int baud = 9600;
    char parity = 'N';         // 'N' 无 / 'E' 偶 / 'O' 奇
    int stop_bits = 1;         // 1 或 2
};

}  // namespace tec
