// 自动检测：一个口一个口地问「你是谁」，谁答得上来就是谁。
//
// 这是「按 USB 口记住设备」的兜底和首次标定（主力在 io/serial_ports.hpp：COM 号变了按 usb_id 反查，
// 不发一个字节）。线被挪到别的插口、或者第一次装机还没标定过，才需要真的去问一遍。
//
// 三条写死的规矩：
//   · 只发读功能码 FC01 / FC02 / FC03 / FC04。整个文件里没有一处写——扫描期间一个继电器都不动、
//     一个参数都不改。这不是约定，是这里真的只调 read_*；改这个文件的人注意别破了它。
//   · 认不出来就说认不出来，不猜。一个口要是有两类设备都对得上（寄存器布局撞了），报「认不准」
//     并把都对上的列出来，绝不挑一个。
//   · 有通道在控制中不许扫——扫描要占住串口、会打断轮询。这一条由调用方（core_app）把关。
//
// 同型号的两路（pH ×2、搅拌 ×2）站号型号全一样，协议上分不出通道 1 和通道 2：这里只报「这个口上
// 有一台 BHT pH 电极」，派给哪个通道由人指派一次，之后交给 usb_id 记住。
#pragma once
#include "../../io/serial_ports.hpp"
#include "../data/store.hpp"
#include "../../io/serial_transport.hpp"
#include <functional>
#include <memory>
#include <string>
#include <vector>

namespace tec {

struct ScanHit {
    std::string port;        // "COM20"
    std::string usb_id;      // 这个口的 USB 身份（可能是空的）
    std::string usb_name;    // 设备管理器里的名字（可能是空的）
    int slot = 0;            // 给人看的口号；0 = 认不出位置
    std::string type;        // 认出来的是哪一类：rd105 / io8r / chiller / tr / ph / stir / feed；空 = 没认出来
    std::string model;       // 「RD105 · 固件 v1.3.0」「AQMD2408BLS-M2 v1.3」…
    int baud = 0;            // 问通了的波特率
    int station = 0;         // 问通了的站号
    std::string problem;     // 非空 = 口打不开、或者认不准（文字里说清是哪几类都对上了）
};

struct ScanOptions {
    int timeout_ms = 300;                                    // 一问一答等多久（扫描要快，正常轮询用 500）
    std::vector<int> bauds{38400, 9600, 19200};              // 现场九台只用到这三种；先试 38400 是因为温控器在这一档
    std::vector<int> stations{1, 2};                         // 搅拌是 2，其余是 1；pH 手册写 2 例子写 1
    std::vector<std::string> only_ports;                     // 空 = 系统上有几个口就扫几个
};

using PortOpenFn = std::function<std::unique_ptr<SerialTransport>(const SerialConfig&)>;
using ScanProgressFn = std::function<void(int done, int total, const ScanHit& latest)>;

// 挨个口扫。返回每个口一条（认出来的和没认出来的都在里面，顺序同 list_serial_ports）。
std::vector<ScanHit> scan_serial_ports(const ScanOptions& opt, const PortOpenFn& open, const ScanProgressFn& on_progress = {});

// 这一类设备在界面上怎么称呼（「TEC 温控器」…）；认不出来的类型原样返回
std::string scan_type_label(const std::string& type);

// 开机时按记住的 usb_id 反查今天的 COM 号——这是整件事的主力，平时就靠它，不发一个字节。
// 记过 usb_id 的设备，只要那根线还插在同一个 USB 口上，COM 号怎么变都跟得上。
// 返回改动了哪几台（空 = 没有一台要改）。调用方负责存盘。
struct PortFix {
    std::string key, label, from, to, usb_name;
};
std::vector<PortFix> resolve_device_ports(AppSettings& s);

}  // namespace tec
