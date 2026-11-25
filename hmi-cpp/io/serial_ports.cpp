#include "serial_ports.hpp"
#include <algorithm>
#include <cstdlib>

#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <setupapi.h>
#else
#include <dirent.h>
#endif

namespace tec {
namespace {

std::function<std::vector<SerialPortInfo>()> g_lister;

// COM 号里的数字，拿来排序（COM9 要排在 COM10 前面，照字符串排会反）
int com_number(const std::string& p) {
    size_t i = 0;
    while (i < p.size() && (p[i] < '0' || p[i] > '9')) i++;
    return i < p.size() ? std::atoi(p.c_str() + i) : 0;
}

#ifdef _WIN32
std::string to_utf8(const wchar_t* w) {
    if (!w || !*w) return {};
    const int n = WideCharToMultiByte(CP_UTF8, 0, w, -1, nullptr, 0, nullptr, nullptr);
    if (n <= 1) return {};
    std::string s(static_cast<size_t>(n - 1), '\0');
    WideCharToMultiByte(CP_UTF8, 0, w, -1, s.data(), n, nullptr, nullptr);
    return s;
}

// 「USB-Enhanced-SERIAL-A CH344 (COM16)」→ 名字 "USB-Enhanced-SERIAL-A CH344"、口 "COM16"。
// 括号里不是 COMn 的（并口之类）不要
bool split_friendly(const std::string& text, std::string* name, std::string* port) {
    const size_t open = text.rfind('(');
    if (open == std::string::npos || text.back() != ')') return false;
    const std::string inner = text.substr(open + 1, text.size() - open - 2);
    if (inner.rfind("COM", 0) != 0 || inner.size() < 4) return false;
    for (size_t i = 3; i < inner.size(); i++) if (inner[i] < '0' || inner[i] > '9') return false;
    *port = inner;
    size_t end = open;
    while (end > 0 && (text[end - 1] == ' ' || text[end - 1] == '\t')) end--;
    *name = text.substr(0, end);
    return true;
}

// 走 SetupAPI，不走 WMI / 注册表：那两条要另外引包，SetupAPI 是系统 DLL，链一个 setupapi 就够。
// 整段包在失败就放弃里：取不到名字不是错，退回只列端口号。
std::vector<SerialPortInfo> ask_system() {
    std::vector<SerialPortInfo> out;
    GUID ports_class = {0x4D36E978, 0xE325, 0x11CE, {0xBF, 0xC1, 0x08, 0x00, 0x2B, 0xE1, 0x03, 0x18}};  // GUID_DEVCLASS_PORTS
    HDEVINFO dev = SetupDiGetClassDevsW(&ports_class, nullptr, nullptr, DIGCF_PRESENT);
    if (dev == INVALID_HANDLE_VALUE) return out;
    SP_DEVINFO_DATA info{};
    info.cbSize = sizeof(info);
    for (DWORD i = 0; SetupDiEnumDeviceInfo(dev, i, &info); i++) {
        wchar_t buf[512] = {};
        DWORD used = 0;
        if (!SetupDiGetDeviceRegistryPropertyW(dev, &info, SPDRP_FRIENDLYNAME, nullptr,
                                               reinterpret_cast<PBYTE>(buf), sizeof(buf), &used))
            continue;
        SerialPortInfo p;
        if (!split_friendly(to_utf8(buf), &p.name, &p.port)) continue;
        wchar_t id[512] = {};
        if (SetupDiGetDeviceInstanceIdW(dev, &info, id, static_cast<DWORD>(std::size(id)), nullptr))
            p.usb_id = to_utf8(id);
        // USB 以外的（板载 COM、蓝牙虚拟口）不给 usb_id：它们的实例 ID 不跟着物理插口走
        if (p.usb_id.rfind("USB\\", 0) != 0 && p.usb_id.rfind("FTDIBUS\\", 0) != 0) p.usb_id.clear();
        out.push_back(std::move(p));
    }
    SetupDiDestroyDeviceInfoList(dev);
    // SetupAPI 一个口都没给（驱动没装友好名之类）：退回挨个问 COM1..COM256 在不在
    if (out.empty()) {
        char target[512];
        for (int i = 1; i <= 256; i++) {
            const std::string name = "COM" + std::to_string(i);
            if (QueryDosDeviceA(name.c_str(), target, sizeof target) != 0) out.push_back(SerialPortInfo{name, "", "", 0});
        }
    }
    return out;
}
#else
// 开发机（macOS / Linux）上：只为了让界面和扫描这条路能在不接硬件时走通。
// 这里拿不到 USB 身份，usb_id 一律是空的——不编。
std::vector<SerialPortInfo> ask_system() {
    std::vector<SerialPortInfo> out;
    DIR* d = opendir("/dev");
    if (!d) return out;
    while (dirent* e = readdir(d)) {
        const std::string n = e->d_name;
        const bool want = n.rfind("cu.usbserial", 0) == 0 || n.rfind("cu.usbmodem", 0) == 0 ||
                          n.rfind("cu.wchusbserial", 0) == 0 || n.rfind("ttyUSB", 0) == 0 || n.rfind("ttyACM", 0) == 0;
        if (want) out.push_back(SerialPortInfo{"/dev/" + n, "", "", 0});
    }
    closedir(d);
    return out;
}
#endif

}  // namespace

void set_port_lister(std::function<std::vector<SerialPortInfo>()> f) { g_lister = std::move(f); }

std::vector<SerialPortInfo> list_serial_ports() {
    std::vector<SerialPortInfo> out = g_lister ? g_lister() : ask_system();
    std::sort(out.begin(), out.end(), [](const SerialPortInfo& a, const SerialPortInfo& b) {
        const int na = com_number(a.port), nb = com_number(b.port);
        return na != nb ? na < nb : a.port < b.port;
    });
    // 给人看的口号：只给有 USB 身份的口编，按实例 ID 排出来的先后编 1、2、3…
    //
    // 怎么排：实例 ID 长这样 USB\VID_1A86&PID_55DA&MI_02\6&1f2a3b4c&0&0000——
    // 最后一段是这颗芯片挂在哪个 USB 位置上，前面的 MI_xx 是它第几路。
    // 所以先按最后一段排（同一颗芯片的四路排在一起），再按整串排（同一颗芯片里按 MI_00/02/04/06）。
    // 一开始只按整串排是错的：那样四口芯片的第一路全排在前面，八个口的号会交错成 1 3 5 7 2 4 6 8
    //（2026-11-25 在仿真的八口上看出来的）。
    // ★ 这个编法在现场那台八口 CH344 上还没验过。真正拿来认设备的是 usb_id 本身，口号只是显示用的；
    //   要是现场发现口号和集线器上的丝印对不上，改这里不影响绑定，设备照样连得上。
    auto tail = [](const std::string& id) {
        const size_t i = id.rfind('\\');
        return i == std::string::npos ? id : id.substr(i + 1);
    };
    std::vector<SerialPortInfo*> usb;
    for (auto& p : out) if (!p.usb_id.empty()) usb.push_back(&p);
    std::sort(usb.begin(), usb.end(), [&tail](const SerialPortInfo* a, const SerialPortInfo* b) {
        const std::string ta = tail(a->usb_id), tb = tail(b->usb_id);
        return ta != tb ? ta < tb : a->usb_id < b->usb_id;
    });
    for (size_t i = 0; i < usb.size(); i++) usb[i]->slot = static_cast<int>(i) + 1;
    return out;
}

}  // namespace tec
