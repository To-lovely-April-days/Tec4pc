// 按设置里的通信参数把真设备一台一台建起来（开串口 → Modbus 主站 → 驱动）。
// 哪台打不开不影响别的：这一台记下原因、当作没接，其余照常。只有温控器和继电器模块是缺了就不能控温的。
#pragma once
#include "../util/text_error.hpp"
#include "../data/store.hpp"
#include "../station/aux_hardware.hpp"
#include "../station/reactor_hardware.hpp"
#include <memory>
#include <string>
#include <vector>

namespace tec {

// problem 存「原句 + 值」两件，不先拼成一句：触摸屏要按原句查词表才翻得了（见 core/util/text_error.hpp）
struct DeviceStatus { std::string key, label, port; bool enabled = false, ok = false; Text problem; };

class RealDevices {
public:
    explicit RealDevices(const AppSettings& s);
    ~RealDevices();
    bool can_control() const;                     // 温控器和继电器模块都连上了
    ReactorHardware& reactor();                   // can_control() 为真时才能用
    AuxHardware& aux();
    const std::vector<DeviceStatus>& status() const { return status_; }

private:
    struct Impl;
    std::unique_ptr<Impl> d_;
    std::vector<DeviceStatus> status_;
};

}  // namespace tec
