// 控制核心主程序（没有界面）。开机自启、一直跑：连设备、控温、跑序列、记数据、对外开 TCP。
//   tec_core [--data 数据目录] [--gains 增益表.csv] [--sim] [--port 端口]
//   --sim   不连真设备，用仿真的釜（开发界面、演示用）
#include "../../third_party/nlohmann/json.hpp"
#include "../../core/app/core_app.hpp"
#include "../../core/app/port_scan.hpp"
#include "../../core/app/real_devices.hpp"
#ifdef _WIN32
#include "../../io/win_serial.hpp"
#endif
#include "../../core/sim/sim_hardware.hpp"
#include "../../core/sim/sim_ports.hpp"
#include "../../core/util/utf8_file.hpp"
#include "../../net/tcp_server.hpp"
#include <atomic>
#include <chrono>
#include <csignal>
#include <cstdio>
#include <memory>
#include <string>
#include <thread>
#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#endif

static std::atomic<bool> g_quit{false};
static void on_signal(int) { g_quit = true; }

int main(int argc, char** argv) {
    // 这个程序打印的中文是 UTF-8；Windows 控制台缺省按本地编码（GBK）显示，不说一声就是满屏乱码
    // （用户 2026-11-25 截到的那一屏）
#ifdef _WIN32
    SetConsoleOutputCP(CP_UTF8);
#endif
    std::string data = "data", gains = "data/gain-table.csv";
    bool sim = false;
    int port = -1;
    for (int i = 1; i < argc; i++) {
        const std::string a = argv[i];
        if (a == "--sim") sim = true;
        else if (a == "--data" && i + 1 < argc) data = tec::fs_path(argv[++i]).u8string();
        else if (a == "--gains" && i + 1 < argc) gains = tec::fs_path(argv[++i]).u8string();
        else if (a == "--port" && i + 1 < argc) port = std::atoi(argv[++i]);
    }
    std::signal(SIGINT, on_signal);
    std::signal(SIGTERM, on_signal);
    // 仿真模式的假串口要在读设置之前装好：下面开机第一件事就是按 USB 口反查 COM 号，
    // 装晚了那一步问到的是「这台机器上一个串口都没有」，仿真里就验不出这条路通不通
    tec::PortOpenFn open_serial;
    if (sim) open_serial = tec::install_sim_ports();
#ifdef _WIN32
    else open_serial = [](const tec::SerialConfig& c) { return tec::make_win_serial(c); };
#endif
    tec::AppSettings settings = tec::AppSettings::load(data);
    // 开机第一件事：按记住的 USB 口反查今天的 COM 号。
    // COM 号是 Windows 按枚举顺序发的，换个 USB 插口 / 重装驱动 / 集线器枚举顺序一变就会整排平移；
    // 标定过的设备认 usb_id，线不动就跟得上，不用人去改配置（用户 2026-11-25 要的）。
    for (const tec::PortFix& f : tec::resolve_device_ports(settings)) {
        std::printf("  %s 的串口变了：%s → %s%s%s\n", f.label.c_str(), f.from.c_str(), f.to.c_str(),
                    f.usb_name.empty() ? "" : "（同一个 USB 口：", f.usb_name.empty() ? "" : (f.usb_name + "）").c_str());
        settings.save(data);
        std::fflush(stdout);     // 这几行是现场查「怎么又连不上」的第一手材料，输出重定向到文件时别卡在缓冲里
    }
    std::unique_ptr<tec::SimHardware> simhw;
    std::unique_ptr<tec::RealDevices> real;
    std::unique_ptr<tec::OfflineReactorHardware> offline_hw;
    std::string offline;
    tec::ReactorHardware* rh = nullptr;
    tec::AuxHardware* ah = nullptr;
    if (sim) {
        simhw = std::make_unique<tec::SimHardware>();
        rh = simhw.get(); ah = simhw.get();
        std::printf("仿真模式：没有连任何设备\n");
    } else {
        real = std::make_unique<tec::RealDevices>(settings);
        for (auto& s : real->status()) std::printf("  %-28s %-6s %s\n", s.label.c_str(), s.port.c_str(), s.ok ? "正常" : s.problem.c_str());
        if (real->can_control()) rh = &real->reactor();
        else {
            // 不退出：退出了触摸屏就连不上核心，也就没法进「通信设置」把串口改对
            offline = "温控器或继电器模块没连上，不能控温。到「设置 → 通信设置」核对串口，改好后重启控制核心";
            std::printf("%s\n", offline.c_str());
            offline_hw = std::make_unique<tec::OfflineReactorHardware>(offline);
            rh = offline_hw.get();
        }
        ah = &real->aux();
    }
    tec::CoreApp app(*rh, *ah, data, gains);
    // 本机来的连接（触摸屏自己的界面程序）不算远程
    tec::TcpServer srv(port > 0 ? port : settings.tcp.port, [&](const std::string& line, bool local) { return app.handle(line, !local); }, settings.tcp.timeout_s);
    app.offline_reason = offline;
    // 自动检测要一个口一个口地开，核心自己不碰真串口（只有 Windows 有），上面建好了接进去
    app.open_serial = open_serial;
    app.comm_info = [&]() {
        nlohmann::json j = {{"sim", sim}, {"address", tec::local_ip()}, {"clients", nlohmann::json::array()}, {"status", nlohmann::json::array()}};
        for (auto& c : srv.clients()) j["clients"].push_back({{"address", c.address}, {"connected_s", c.connected_s}, {"rx", c.rx}, {"tx", c.tx}});
        if (real) for (auto& s : real->status()) j["status"].push_back({{"key", s.key}, {"ok", s.ok}, {"problem", s.problem}});
        return j.dump();
    };
    std::string err;
    if (settings.tcp.enabled && !srv.start(&err)) std::printf("对外通信没开起来：%s\n", err.c_str());
    else if (settings.tcp.enabled) std::printf("对外通信：TCP 端口 %d\n", srv.port());
    using clock = std::chrono::steady_clock;
    const auto period = std::chrono::duration_cast<clock::duration>(std::chrono::duration<double>(app.period_s()));
    auto next = clock::now();
    while (!g_quit) {
        app.tick();
        if (simhw) simhw->advance(app.period_s());
        next += period;
        const auto now = clock::now();
        if (next < now) next = now;
        std::this_thread::sleep_until(next);
    }
    // 退出前把两个通道都停掉，再跑两拍让「停」真正写到设备上
    app.handle("{\"cmd\":\"seq.end\",\"ch\":1}", false); app.handle("{\"cmd\":\"seq.end\",\"ch\":2}", false);
    app.handle("{\"cmd\":\"temp.stop\",\"ch\":1}", false); app.handle("{\"cmd\":\"temp.stop\",\"ch\":2}", false);
    app.tick(); app.tick();
    srv.stop();
    return 0;
}
