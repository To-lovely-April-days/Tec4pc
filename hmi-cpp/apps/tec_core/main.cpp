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
#include <vector>
#ifndef _WIN32
#include <unistd.h>
#endif
#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#endif

// 这个程序自己在哪个目录（增益表缺省就放在它旁边）
static std::string exe_dir() {
#ifdef _WIN32
    wchar_t buf[MAX_PATH] = {0};
    if (GetModuleFileNameW(nullptr, buf, MAX_PATH)) {
        std::filesystem::path p(buf);
        return p.parent_path().u8string() + "/";
    }
    return "";
#else
    return "./";
#endif
}

static std::atomic<bool> g_quit{false};
static std::atomic<bool> g_restart{false};      // 停机以后把自己原样再启动一遍（sys.restart）
static void on_signal(int) { g_quit = true; }

// 把自己原样启动一遍，然后让这一个退出。
// 必须等串口和 TCP 端口都还掉了再叫（串口在 Windows 上是独占打开的，端口也一样），
// 所以调用点在停机动作做完、srv.stop() 之后、real 释放之后
static void relaunch(int argc, char** argv) {
#ifdef _WIN32
    wchar_t exe[MAX_PATH] = {0};
    if (!GetModuleFileNameW(nullptr, exe, MAX_PATH)) { std::printf("重启没成功：找不到自己的程序路径，请手动再启动一次\n"); return; }
    std::wstring cmd = GetCommandLineW();            // 连参数一起照搬（--data、--gains、--port…）
    std::vector<wchar_t> buf(cmd.begin(), cmd.end());
    buf.push_back(L'\0');
    STARTUPINFOW si{}; si.cb = sizeof si;
    PROCESS_INFORMATION pi{};
    if (CreateProcessW(exe, buf.data(), nullptr, nullptr, FALSE, 0, nullptr, nullptr, &si, &pi)) {
        CloseHandle(pi.hProcess); CloseHandle(pi.hThread);
        std::printf("控制核心重启中…\n");
    } else {
        std::printf("重启没成功（系统错误 %lu）：请手动再启动一次控制核心\n", static_cast<unsigned long>(GetLastError()));
    }
#else
    // 开发机（Mac / Linux）上一样能用：分一个出来接着跑，这一个照常退出
    (void)argc;
    const pid_t pid = fork();
    if (pid == 0) { execv(argv[0], argv); _exit(127); }
    if (pid < 0) std::printf("重启没成功：请手动再启动一次控制核心\n");
    else std::printf("控制核心重启中…\n");
#endif
}

int main(int argc, char** argv) {
    // 这个程序打印的中文是 UTF-8；Windows 控制台缺省按本地编码（GBK）显示，不说一声就是满屏乱码
    // （用户 2026-11-25 截到的那一屏）
#ifdef _WIN32
    SetConsoleOutputCP(CP_UTF8);
#endif
    std::string data = "data", gains;
    bool sim = false;
    int port = -1;
    for (int i = 1; i < argc; i++) {
        const std::string a = argv[i];
        if (a == "--sim") sim = true;
        else if (a == "--data" && i + 1 < argc) data = tec::fs_path(argv[++i]).u8string();
        else if (a == "--gains" && i + 1 < argc) gains = tec::fs_path(argv[++i]).u8string();
        else if (a == "--port" && i + 1 < argc) port = std::atoi(argv[++i]);
    }
    // 没写 --gains 就用放在程序旁边的那张表：编出来和装出来都会往那儿拷一份（见 CMakeLists）。
    // 以前缺省是 "data/gain-table.csv"，数据目录换个地方就读不到了——读不到的后果不是报错，
    // 是内环 Kp/Ki/Kd 全 0、出力一直 0 %，看着像在控温其实一点力都不出（用户 2026-11-27 现场撞上）
    if (gains.empty()) {
        const std::string beside = exe_dir() + "gain-table.csv";
        std::error_code ec;
        gains = std::filesystem::exists(tec::fs_path(beside.c_str()), ec) ? beside : std::string("data/gain-table.csv");
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
        for (auto& s : real->status()) std::printf("  %-28s %-6s %s\n", s.label.c_str(), s.port.c_str(), s.ok ? "正常" : s.problem.filled().c_str());
        if (real->can_control()) rh = &real->reactor();
        else {
            // 不退出：退出了触摸屏就连不上核心，也就没法进「通信设置」把串口改对
            // 这句话会摆到界面上几个很窄的位置里去（结晶卡上那一行最窄，536 px）：
            // 长了就只能被点成「…」，正好把「去哪儿改」这半句吃掉。所以剪到放得下为止。
            //「改好后要重启控制核心」不在这儿说了——真到了「通信设置」那一页，那页自己会说
            offline = "温控器或继电器没连上，不能控温。去「设置 → 通信设置」核对串口";
            std::printf("%s\n", offline.c_str());
            offline_hw = std::make_unique<tec::OfflineReactorHardware>(offline);
            rh = offline_hw.get();
        }
        ah = &real->aux();
    }
    tec::CoreApp app(*rh, *ah, data, gains);
    // 增益表是内环的命根子：一行都没读到的话 PID 参数全是 0，出力永远 0 %，看着在控温其实不出力。
    // 所以开机就喊一声（而且这种状态下核心不让开始控温，见 core_app 的 temp.start）
    if (app.gain_rows() == 0) std::printf("增益表一行都没读到（%s）：这样控不了温，开始控温会被拒。把 gain-table.csv 放到程序旁边，或者用 --gains 指对路径\n", gains.c_str());
    else std::printf("增益表：%s（%d 行）\n", gains.c_str(), static_cast<int>(app.gain_rows()));
    std::fflush(stdout);     // 这一行是现场查「怎么不出力」的第一手材料，输出重定向到文件时别卡在缓冲里
    // 本机来的连接（触摸屏自己的界面程序）不算远程
    tec::TcpServer srv(port > 0 ? port : settings.tcp.port, [&](const std::string& line, bool local) { return app.handle(line, !local); }, settings.tcp.timeout_s);
    app.offline_reason = offline;
    // 自动检测要一个口一个口地开，核心自己不碰真串口（只有 Windows 有），上面建好了接进去
    app.open_serial = open_serial;
    // 扫之前把手上的串口全让开、扫完再开回来。不让的话扫描一个接着设备的口都开不了：
    // Windows 上串口是独占打开的，同一个进程再开一次照样被拒（仿真模式没有 real，不用让）
    if (real) app.lend_ports = [&real](bool release) {
        if (release) real->release_ports();
        else if (!real->reopen_ports()) std::printf("自动检测结束，有串口没能重新打开——重启控制核心\n");
    };
    // 触摸屏上「退出程序 → 连控制核心一起退」走的就是这里：不另写一套停机动作，
    // 只把主循环叫停，让下面那段现成的退出（停序列、停控温、再跑两拍写到设备上）照常走一遍
    app.on_exit = []() { g_quit = true; };
    // 「自动检测 → 应用」改完串口要重启才生效：核心自己来，不用人去关窗口再敲一遍命令（用户 2026-11-27）
    app.on_restart = []() { g_restart = true; g_quit = true; };
    app.comm_info = [&]() {
        nlohmann::json j = {{"sim", sim}, {"address", tec::local_ip()}, {"clients", nlohmann::json::array()}, {"status", nlohmann::json::array()}};
        for (auto& c : srv.clients()) j["clients"].push_back({{"address", c.address}, {"connected_s", c.connected_s}, {"rx", c.rx}, {"tx", c.tx}});
        if (real) for (auto& s : real->status()) j["status"].push_back({{"key", s.key}, {"ok", s.ok}, {"problem", s.problem.filled()},
                                                              {"problem_t", s.problem.t}, {"problem_args", s.problem.args}});
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
    if (g_restart) {
        // 串口要先真的关掉，新起来的那个才开得了（Windows 上串口是独占的）。
        // 这之后不能再碰 app 了——它手里攥着 real 里的那些设备
        real.reset();
        offline_hw.reset();
        relaunch(argc, argv);
    }
    return 0;
}
