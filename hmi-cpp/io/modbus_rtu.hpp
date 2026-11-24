// Modbus RTU 主站，一问一答，同步（调用它的线程会等到应答或认输）。
// 从原来 C# 程序的 ModbusRtuClient 搬过来的，现场踩过的几条经验原样保留：
//   · 每次发问前清接收缓冲：上一问超时后迟到的应答不会混进这一问
//   · 应答前面容忍几个脏字节（485 收发切换的毛刺，现场见过 F8 01 03 …）
//   · 帧收到一半断了（被干扰掐断）不等满总超时，早认输早重发
//   · 重发前等一拍：超时多半是从站答得晚，立刻重发会和迟到的应答在总线上撞车
//   · 从站回了异常码不重试：那是配置或语义错，重发一百遍也一样
// 一条串口上挂几个从站（宇电两台共口）时，几个 ModbusRtu 要共用同一把锁。
#pragma once
#include "serial_transport.hpp"
#include <memory>
#include <mutex>
#include <optional>
#include <stdexcept>
#include <string>
#include <vector>

namespace tec {

class ModbusError : public std::runtime_error {
public:
    explicit ModbusError(const std::string& what, std::optional<int> code = std::nullopt, bool timeout = false)
        : std::runtime_error(what), code(code), timeout(timeout) {}
    std::optional<int> code;   // 从站回的异常码；链路层的错（超时、坏帧）是空的
    bool timeout;              // true = 没等到 / 帧烂尾；false = 收到了但不对
};

uint16_t modbus_crc(const uint8_t* data, size_t n);

class ModbusRtu {
public:
    ModbusRtu(SerialTransport& transport, uint8_t station, int timeout_ms = 500, std::shared_ptr<std::mutex> bus_lock = nullptr);
    uint8_t station() const { return station_; }

    std::vector<bool> read_coils(uint16_t start, int count);                 // FC 01
    std::vector<bool> read_discrete_inputs(uint16_t start, int count);       // FC 02
    std::vector<uint16_t> read_holding_registers(uint16_t start, int count); // FC 03
    std::vector<uint16_t> read_input_registers(uint16_t start, int count);   // FC 04
    void write_coil(uint16_t address, bool on);                              // FC 05
    void write_register(uint16_t address, uint16_t value);                   // FC 06
    void write_coils(uint16_t start, const std::vector<bool>& values);       // FC 0F
    void write_registers(uint16_t start, const std::vector<uint16_t>& values); // FC 10

    int max_leading_noise = 4;
    int timeout_retries = 2;
    int corrupt_retries = 2;
    int inter_byte_idle_ms = 100;
    int retry_backoff_ms = 200;
    int inter_frame_gap_ms = 10;

private:
    std::vector<uint8_t> transact(uint8_t fc, const std::vector<uint8_t>& payload, int expected_data_bytes = -1);
    std::vector<uint8_t> read_response(uint8_t fc, int expected_data_bytes);
    void read_exact(uint8_t* buf, size_t n, long long deadline, const char* what);
    std::vector<bool> read_bits(uint8_t fc, uint16_t start, int count);
    std::vector<uint16_t> read_regs(uint8_t fc, uint16_t start, int count);

    SerialTransport& transport_;
    uint8_t station_;
    int timeout_ms_;
    std::shared_ptr<std::mutex> lock_;
    long long last_frame_end_ = -1000000;
    std::vector<uint8_t> rx_trace_;
};

}  // namespace tec
