// Windows 上的真串口。只在 Windows 下编译；这台开发用的 Mac 上编不了也跑不了，所以这个文件没有被测试过，
// 第一次在现场机器上编译时要留意。
#pragma once
#include "serial_transport.hpp"
#include <memory>
#include <vector>

namespace tec {

// 列出系统里现有的串口名（"COM3"、"COM7"…），给通信设置页的「扫描串口」用
std::vector<std::string> list_serial_ports();

// 打不开（口不存在、被别的程序占着）时 open() 抛 std::runtime_error，文字里带系统给的原因
std::unique_ptr<SerialTransport> make_win_serial(const SerialConfig& cfg);

}  // namespace tec
