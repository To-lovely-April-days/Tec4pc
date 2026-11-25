// Windows 上的真串口。只在 Windows 下编译；这台开发用的 Mac 上编不了也跑不了，所以这个文件没有被测试过，
// 第一次在现场机器上编译时要留意。
#pragma once
#include "serial_transport.hpp"
#include <memory>
#include <vector>

namespace tec {

// 列串口的事搬去 io/serial_ports.hpp 了：那边除了口名还给 USB 身份（设备实例 ID），
// 设备配置认的是它、不是 COM 号。这里原来那个只给口名的版本留着会和新的撞车
//（返回类型不同、名字一样，MSVC 直接报 C2556；Mac 上这个文件不参与编译，看不见）。

// 打不开（口不存在、被别的程序占着）时 open() 抛 std::runtime_error，文字里带系统给的原因
std::unique_ptr<SerialTransport> make_win_serial(const SerialConfig& cfg);

}  // namespace tec
