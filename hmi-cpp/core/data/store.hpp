// 配方库和设置的存盘。都是 json 文本文件，放在数据目录下：
//   recipes/<名字>.json        配方库：一个配方 = 一个任务序列（不分通道，两个通道都能装）
//   sequence-ch1.json / -ch2   每个通道现在编着的那个序列（断电重启还在）
//   settings.json              设置：通信、安全限值、搅拌电机、加料、快速升温等
// 设置都在界面上改（用户的习惯：不到现场改文件），这里只管读写。
#pragma once
#include "../sequence/sequence.hpp"
#include "../station/aux_hardware.hpp"
#include "../station/station.hpp"
#include <string>
#include <vector>

namespace tec {

struct RecipeInfo { std::string name; int steps = 0; double est_minutes = 0; std::string saved; };

class RecipeStore {
public:
    explicit RecipeStore(const std::string& data_dir) : dir_(data_dir) {}
    std::vector<RecipeInfo> list() const;                          // 最近存的在前
    bool load(const std::string& name, std::vector<Step>& out) const;
    bool save(const std::string& name, const std::vector<Step>& steps) const;
    bool remove(const std::string& name) const;
    bool load_channel(int ch, std::vector<Step>& out) const;       // ch：0 / 1
    bool save_channel(int ch, const std::vector<Step>& steps) const;
    // 每个通道手动用的那条结晶曲线（设备控制页「结晶」模式编的）：存盘，重启后还在
    bool load_profile(int ch, std::vector<ProfileSegment>& out) const;
    bool save_profile(int ch, const std::vector<ProfileSegment>& segs) const;

private:
    std::string dir_;
};

// 一台设备的通信参数（设置 → 通信设置 → 硬件通信里的一行）
struct DeviceComm {
    std::string key;            // rd105 / io8r / chiller / tr / ph1 / ph2 / stir1 / stir2 / feed
    std::string label;          // 界面上显示的名字
    bool enabled = true;
    std::string port;
    int baud = 9600;
    char parity = 'N';
    int stop_bits = 1;
    int station = 1;
    int poll_ms = 1000;
    // 下面两项排在最后，是为了让 AppSettings::defaults() 里那张出厂接线表不用跟着改——
    // 出厂本来就没有标定过 USB 口。
    //
    // usb_id：记住的 USB 口身份（设备实例 ID）。非空时开机先按它反查今天这台是哪个 COM——
    // COM 号是 Windows 按枚举顺序发的，会平移；USB 的物理位置不会（见 io/serial_ports.hpp）。
    // 空 = 还没标定过这台，只能认死上面那个 port
    std::string usb_id;
    std::string usb_name;       // 设备管理器里那串名字，只给人看（「USB-Enhanced-SERIAL-A CH344」）
};

struct TcpSettings {
    bool enabled = true;
    int port = 5020;
    int timeout_s = 10;         // 超过这么久没收到 PC 的任何东西就算它掉线
    bool allow_remote_control = true;
};

struct AppSettings {
    std::vector<DeviceComm> devices;
    TcpSettings tcp;
    SafetyLimits limits[2];
    StirMotor stir[2];
    AuxConfig aux;
    DoMap do_map;
    // 快速升温：最多加码多少（K）。0 = 不加码，就是原来稳妥的算法；上真机先从小的试起
    double fast_boost_max = 60.0;
    std::string vessel[2] = {"250 mL 玻璃釜", "250 mL 玻璃釜"};

    static AppSettings defaults();                                 // 现场那台的接线（COM7 / COM8 / COM3 / COM12 / COM13 / COM14 / COM11 / COM15）
    static AppSettings load(const std::string& data_dir);          // 文件没有或坏了就用缺省值
    bool save(const std::string& data_dir) const;
    const DeviceComm* device(const std::string& key) const;
};

// 给 TCP 用的：序列 ⇄ json 文本
std::string steps_to_json(const std::vector<Step>& steps);
bool steps_from_json(const std::string& text, std::vector<Step>& out);
double estimate_minutes(const std::vector<Step>& steps);          // 估一下整个序列要多久（尽快到达的温度步不知道多久，按 0 算）

}  // namespace tec
