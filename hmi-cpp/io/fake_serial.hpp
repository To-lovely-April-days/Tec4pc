// 假串口：测试用。给它一个「收到这一帧就回什么」的函数，它就扮演从站。
#pragma once
#include "serial_transport.hpp"
#include <deque>
#include <functional>
#include <vector>

namespace tec {

class FakeSerial : public SerialTransport {
public:
    using Responder = std::function<std::vector<uint8_t>(const std::vector<uint8_t>& request)>;
    explicit FakeSerial(Responder r) : responder_(std::move(r)) {}
    bool is_open() const override { return open_; }
    void open() override { open_ = true; }
    void close() override { open_ = false; }
    void discard_input() override { rx_.clear(); }
    void write(const uint8_t* data, size_t n) override {
        std::vector<uint8_t> req(data, data + n);
        sent.push_back(req);
        for (uint8_t b : responder_(req)) rx_.push_back(b);
    }
    int read(uint8_t* buf, size_t n, int) override {
        size_t k = 0;
        while (k < n && !rx_.empty()) { buf[k++] = rx_.front(); rx_.pop_front(); }
        return static_cast<int>(k);
    }
    std::vector<std::vector<uint8_t>> sent;   // 发出去过的每一帧，测试里拿来核对

private:
    Responder responder_;
    std::deque<uint8_t> rx_;
    bool open_ = false;
};

}  // namespace tec
