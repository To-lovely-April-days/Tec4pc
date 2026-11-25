// 这台机器上现在有哪些串口，以及每个口在 USB 上的身份。
//
// 为什么要 USB 身份，而不是只记 COM 号：
// 现场是八口 CH344 USB 转串，COM 号是 Windows 按枚举顺序发的。换个 USB 插口、重装一次驱动、
// 开机时集线器枚举顺序变了，整排号就平移，九台设备一起连不上
// （用户 2026-11-25：「我的串口名称经常变动 导致我有时候连接会连不上」）。
// USB 的物理位置是不变的，所以设备配置里记的是 usb_id，开机时反查它今天是哪个 COM——
// 不问人、不往总线上发一个字节。线不动，号怎么变都跟得上。
//
// 取不到名字 / 身份不算错（开发机上、非 USB 的板载串口上都取不到），照样把口列出来，
// 只是那几项是空的——不编一个名字出来。
#pragma once
#include <functional>
#include <string>
#include <vector>

namespace tec {

struct SerialPortInfo {
    std::string port;      // "COM7"
    std::string name;      // 设备管理器里那串友好名，如「USB-Enhanced-SERIAL-A CH344」
    std::string usb_id;    // 设备实例 ID，如 USB\VID_1A86&PID_55DA&MI_02\6&1f2a3b4c&0&0000
    int slot = 0;          // 给人看的口号 1..N；0 = 这个口没有 USB 身份，认不出位置
};

// 按 COM 号里的数字排序。slot 只给有 usb_id 的口编，编法见 .cpp 里的说明。
std::vector<SerialPortInfo> list_serial_ports();

// 测试和仿真用：换掉「去问系统」这一步。传空的恢复成真的去问系统。
void set_port_lister(std::function<std::vector<SerialPortInfo>()> f);

}  // namespace tec
