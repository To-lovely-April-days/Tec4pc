// 控制核心：把控温、搅拌和加料、序列、记录、配方库、设置都拢在一起，对外只有两个入口：
//   tick()            每 0.5 s 一拍（节拍线程调）
//   handle(一行 json)  收一条命令，回一行 json（TCP 服务和本机界面都走它）
// 触摸屏界面和 PC 工作站用的是同一套命令，所以两边看到的、能做的完全一样。
//
// 命令和应答都是一行一条的 json。命令：{"cmd":"名字", ...参数, "id":可选}；应答：{"ok":true, ...} 或 {"ok":false,"error":"原因"}，带回 id。
// 命令清单见 core_app.cpp 里 handle() 上面的表。
#pragma once
#include "port_scan.hpp"
#include "../data/run_recorder.hpp"
#include "../data/store.hpp"
#include "../sequence/sequence.hpp"
#include "../station/aux_runner.hpp"
#include "../station/station.hpp"
#include <chrono>
#include <atomic>
#include <functional>
#include <deque>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

namespace tec {

class CoreApp {
public:
    // hw / aux：真设备或仿真；data_dir：放设置、配方、记录的目录
    CoreApp(ReactorHardware& hw, AuxHardware& aux, const std::string& data_dir, const std::string& gain_table_path);
    ~CoreApp();
    void tick();
    // remote = true：命令是从 TCP 来的（PC）。设置里关了「允许远程控制」时，远程只能看、不能下会改变设备状态的命令
    std::string handle(const std::string& line, bool remote);
    double period_s() const { return cfg_.inner_period_s; }
    const AppSettings& settings() const { return settings_; }
    std::string data_dir() const { return dir_; }
    // 通信设置页要看的东西（各设备连没连上、连着哪些 PC、本机地址）核心自己不知道，由主程序接进来：返回一个 json 对象的文本
    std::function<std::string()> comm_info;
    // 真串口只有 Windows 有，核心自己不建，由主程序接进来：自动检测要靠它一个口一个口地开。
    // 不接（仿真、测试）= 这台机器上没有真串口可扫，自动检测会如实说不能扫
    PortOpenFn open_serial;
    // 自动检测要独占串口，可那些口平时都在主程序手上开着（Windows 上串口是独占打开的，同一个进程
    // 再开一次也会被拒）。扫之前调一次 (true) 让主程序把口全关掉，扫完调一次 (false) 开回来。
    // 不接（仿真、测试）= 本来就没有占着谁，扫描照跑
    std::function<void(bool release)> lend_ports;
    // 退出控制核心（sys.exit）时调它：核心自己不知道怎么结束进程，由主程序把自己的主循环停掉。
    // 不接 = 这个程序不支持退出（嵌在别人里跑的时候），sys.exit 会如实拒绝
    std::function<void()> on_exit;
    // 不空 = 控温用的设备没连上（主程序开机时发现的），里面是原因。这时核心照常跑、照常答命令，只是不收开始控温 / 开始序列
    std::string offline_reason;
    static constexpr const char* kVersion = "0.1.0";      // 控制核心的版本号（系统设置页、PC 端都看它）

private:
    // 趋势曲线用的最近几小时读数：不管有没有在控温都记，每 2 秒一点，只放内存里（重启就从头记；一炉的完整数据在记录文件里）
    struct TrendPoint { double t = 0; long long wall = 0; double tr = 0, tj = 0, tj_sp = 0, sp = 0, ph = 0, rpm = 0; bool on = false; };
    static constexpr size_t kTrendMax = 5400;      // 2 秒一点 × 5400 = 3 小时
    struct Chan {
        std::unique_ptr<StationChannelOps> ops;
        std::unique_ptr<SequenceRunner> seq;
        std::unique_ptr<RunRecorder> rec;
        double rec_t0 = 0;
        int last_step = -1;
        double step_t0 = 0;
        bool was_active = false;
        std::string run_name;
        std::vector<ProfileSegment> manual_profile;     // 这个通道手动用的结晶曲线（存盘的那一份）
        std::deque<TrendPoint> trend;
        struct Mark { double t; long long wall; std::string text; };
        std::deque<Mark> marks;                         // 操作人在趋势图上打的时间标记（最近 200 个，只在内存；这一炉在跑时同时记进事件日志）
        bool lamp = false;                              // 照明的开关状态。现在只记状态，还没接到哪一路输出上（用户 2026-11-24：通信以后再接）
    };
    // 自动检测在自己的线程上跑（一个口几百毫秒，八个口几秒，不能卡住 0.5 s 一拍的控制循环）。
    // 界面开扫以后每隔一会儿问一次 ports.scan.status 拿进度和结果。
    struct Scanner {
        std::thread th;
        std::mutex mu;
        bool running = false, want_stop = false, has_result = false;
        int done = 0, total = 0;
        std::string now_port;
        double started = 0;
        std::vector<ScanHit> hits;
        ~Scanner() { if (th.joinable()) th.join(); }
    };
    Scanner scan_;
    // 扫描期间串口全让给扫描了，这段时间一拍也不要去问设备（问了只会一路报读不到，还会白记一堆没用的点）
    std::atomic<bool> scanning_{false};

    void record(double t);
    void begin_run(int ch, const std::string& name, double t);
    std::string state_json();

    std::string dir_, gain_path_;
    AppSettings settings_;
    GainTable table_;
    ChannelConfig cfg_;
    RecipeStore recipes_;
    std::unique_ptr<Station> station_;
    std::unique_ptr<AuxRunner> aux_;
    Chan ch_[2];
    std::chrono::steady_clock::time_point started_ = std::chrono::steady_clock::now();
    std::mutex m_;            // 护着序列、记录、设置；Station 和 AuxRunner 自己有锁
    long k_ = 0;
};

}  // namespace tec
