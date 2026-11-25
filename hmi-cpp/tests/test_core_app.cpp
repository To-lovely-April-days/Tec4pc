// 端到端测试：仿真硬件 + 控制核心 + TCP 服务。像 PC 工作站那样连上来下命令、看状态，核对整条链路。
#include "../core/app/core_app.hpp"
#include "../core/station/reactor_hardware.hpp"
#include "../core/control/gain_table.hpp"
#include "../core/sim/sim_hardware.hpp"
#include "../core/util/utf8_file.hpp"
#include "../net/tcp_server.hpp"
#include "../third_party/nlohmann/json.hpp"
#include <cmath>
#include <cstdio>
#include <filesystem>
#include <chrono>
#include <thread>
#include <fstream>
using namespace tec;
using json = nlohmann::json;
namespace fs = std::filesystem;
static int fails = 0;
#define CHECK(c) do { if (!(c)) { std::printf("失败 %s:%d  %s\n", __FILE__, __LINE__, #c); fails++; } } while (0)

int main(int argc, char** argv) {
    const std::string table = argc > 1 ? argv[1] : "testdata/gain-table-run1110.csv";
    std::error_code ec;
    const fs::path base = fs::temp_directory_path() / "tec_core_app_test_run";
    fs::remove_all(base, ec);
    fs::create_directories(base, ec);
    const std::string gains_copy = (base / "gains.csv").u8string();
    fs::copy_file(fs_path(table), fs_path(gains_copy), fs::copy_options::overwrite_existing);
    SimHardware sim;
    CoreApp app(sim, sim, base.u8string(), gains_copy);
    auto run = [&](double seconds) { for (int i = 0; i < seconds * 2; i++) { app.tick(); sim.advance(0.5); } };
    auto local = [&](const json& q) { return json::parse(app.handle(q.dump(), false)); };

    TcpServer srv(0, [&](const std::string& line, bool) { return app.handle(line, true); });      // 测试里把 TCP 来的都当远程（PC）
    std::string err;
    CHECK(srv.start(&err));
    // 连不上的地址要按时放弃，不能傻等：界面嵌在 PC 工作站里跑的时候，这一等就是整个工作站卡住。
    // 192.0.2.1 是标准里留着「专门用来测试、永远不会真分给谁」的地址，连它通常石沉大海
    {
        TcpClient dead;
        const auto t0 = std::chrono::steady_clock::now();
        CHECK(!dead.connect("192.0.2.1", 5020, 300));
        const double spent = std::chrono::duration<double>(std::chrono::steady_clock::now() - t0).count();
        CHECK(spent < 2.0);
        if (spent >= 2.0) std::printf("  连不上的地址等了 %.1f 秒才放弃\n", spent);
    }
    // 不等的连接：要能分好几次调着连上（界面就是这么用的，每帧往前走一步）。
    // 踩过：调用方在没连上时顺手 close 了一把，正在进行的连接被掐掉，结果永远连不上
    {
        TcpClient step;
        bool up = false;
        for (int i = 0; i < 200 && !up; i++) { up = step.connect_async("127.0.0.1", srv.port()); if (!up) std::this_thread::sleep_for(std::chrono::milliseconds(10)); }
        CHECK(up);
        std::string r;
        CHECK(step.request(json({{"cmd", "state"}}).dump(), r));      // 连上以后真的能用
    }
    TcpClient pc;
    CHECK(pc.connect("127.0.0.1", srv.port()));
    auto remote = [&](const json& q) { std::string r; CHECK(pc.request(q.dump(), r)); return r.empty() ? json::object() : json::parse(r); };

    // 看状态
    run(2);
    json s = remote({{"cmd", "state"}, {"id", 7}});
    CHECK(s["ok"] == true && s["id"] == 7 && s["ch"].size() == 2 && s["ch"][0]["running"] == false && s["comm_ok"] == true);
    CHECK(s["ch"][0]["tj"].is_number() && s["ch"][0]["tr"].is_number() && s["ch"][0]["ph"].is_number());
    // PC 下命令：通道 1 釜内控温到 40 ℃
    CHECK(remote({{"cmd", "temp.start"}, {"ch", 1}, {"mode", "reactor"}, {"target", 40}, {"name", "端到端"}})["ok"] == true);
    CHECK(remote({{"cmd", "stir.set"}, {"ch", 1}, {"rpm", 300}})["ok"] == true);
    run(120);
    s = remote({{"cmd", "state"}});
    CHECK(s["ch"][0]["running"] == true && s["ch"][0]["mode"] == "reactor" && s["ch"][0]["sp"] == 40 && s["ch"][0]["duty"].get<double>() < 0);   // 在加热
    { const json rl = remote({{"cmd", "runs.list"}}); CHECK(rl["runs"].size() == 1 && rl["runs"][0]["running"] == true && rl["runs"][0]["duration_s"].get<double>() > 100); }   // 正在跑的一炉也报得出已经跑了多久
    CHECK(s["ch"][0]["tj"].get<double>() > 30 && s["ch"][0]["stir"]["rpm"].get<double>() > 290 && s["ch"][0]["run"] == "端到端");
    CHECK(s["ch"][1]["running"] == false);
    // 不认识的命令、坏 json、通道号不对
    CHECK(remote({{"cmd", "nope"}})["ok"] == false);
    { std::string r; CHECK(pc.request("这不是 json", r)); CHECK(json::parse(r)["ok"] == false); }
    CHECK(remote({{"cmd", "temp.stop"}, {"ch", 5}})["ok"] == false);
    // 安全限值：只能在底线之内收紧
    CHECK(remote({{"cmd", "limits.set"}, {"ch", 1}, {"tr_max", 250}})["ok"] == false);
    CHECK(remote({{"cmd", "limits.set"}, {"ch", 1}, {"tr_max", 120}})["ok"] == true);
    CHECK(remote({{"cmd", "limits.get"}, {"ch", 1}})["limits"]["tr_max"] == 120);
    // 通信设置只能在触摸屏上改
    CHECK(remote({{"cmd", "settings.set"}, {"tcp", {{"port", 9999}}}})["ok"] == false);
    // 触摸屏关掉「允许远程控制」：PC 还能看，不能控
    CHECK(local({{"cmd", "settings.set"}, {"tcp", {{"allow_remote_control", false}}}})["ok"] == true);
    CHECK(remote({{"cmd", "state"}})["ok"] == true && remote({{"cmd", "runs.list"}})["ok"] == true);
    CHECK(remote({{"cmd", "temp.stop"}, {"ch", 1}})["ok"] == false);
    CHECK(local({{"cmd", "settings.set"}, {"tcp", {{"allow_remote_control", true}}}})["ok"] == true);
    // 停控 → 这一炉收尾，能列出来、能导出
    CHECK(remote({{"cmd", "temp.stop"}, {"ch", 1}})["ok"] == true);
    run(3);
    s = remote({{"cmd", "state"}});
    CHECK(s["ch"][0]["running"] == false && s["ch"][0]["run"].is_null());
    json runs = remote({{"cmd", "runs.list"}});
    CHECK(runs["runs"].size() == 1 && runs["runs"][0]["name"] == "端到端" && runs["runs"][0]["running"] == false && runs["runs"][0]["duration_s"].get<double>() > 100);
    json ex = remote({{"cmd", "runs.export"}, {"ids", {runs["runs"][0]["id"]}}, {"interval_s", 10}});
    CHECK(ex["exported"] == 1 && fs::exists(fs::u8path(ex["out_dir"].get<std::string>()) / fs::u8path(runs["runs"][0]["id"].get<std::string>()) / fs::u8path("采样宽表.csv")));
    // 序列：通道 2 装一个三步的序列（夹套到 30 → 搅拌 200 → 暂停确认），存成配方
    json steps = json::array({{{"kind", "jacket"}, {"target", 30}, {"stir_rpm", 150}}, {{"kind", "stir"}, {"stir_rpm", 200}}, {{"kind", "pause"}, {"message", "请取样"}}});
    CHECK(remote({{"cmd", "seq.load"}, {"ch", 2}, {"steps", steps}})["ok"] == true);
    CHECK(remote({{"cmd", "seq.get"}, {"ch", 2}})["steps"].size() == 3);
    CHECK(remote({{"cmd", "recipe.save"}, {"name", "三步示例"}, {"steps", steps}})["ok"] == true);
    CHECK(remote({{"cmd", "recipe.list"}})["recipes"].size() == 1);
    CHECK(remote({{"cmd", "seq.start"}, {"ch", 2}, {"name", "三步示例"}})["ok"] == true);
    CHECK(remote({{"cmd", "seq.load"}, {"ch", 2}, {"steps", steps}})["ok"] == false);          // 已开始的序列不可增改
    CHECK(remote({{"cmd", "temp.setpoint"}, {"ch", 2}, {"target", 99}})["ok"] == false);       // 序列在跑，手动命令不收
    run(5);
    s = remote({{"cmd", "state"}});
    CHECK(s["ch"][1]["seq"]["state"] == "running" && s["ch"][1]["seq"]["index"] == 0 && s["ch"][1]["mode"] == "jacket" && s["ch"][1]["stir"]["set"] == 150);
    for (int i = 0; i < 60 && remote({{"cmd", "state"}})["ch"][1]["seq"]["state"] != "waiting_confirm"; i++) run(10);
    s = remote({{"cmd", "state"}});
    CHECK(s["ch"][1]["seq"]["state"] == "waiting_confirm" && s["ch"][1]["seq"]["index"] == 2 && s["ch"][1]["seq"]["note"] == "请取样" && s["ch"][1]["stir"]["set"] == 200);
    CHECK(remote({{"cmd", "seq.confirm"}, {"ch", 2}})["ok"] == true);
    run(2);
    s = remote({{"cmd", "state"}});
    CHECK(s["ch"][1]["seq"]["state"] == "done" && s["ch"][1]["running"] == true);              // 做完了温度保持着
    CHECK(remote({{"cmd", "seq.end"}, {"ch", 2}})["ok"] == true);
    run(3);
    CHECK(remote({{"cmd", "state"}})["ch"][1]["running"] == false && remote({{"cmd", "runs.list"}})["runs"].size() == 2);
    // 重启：通道 2 的序列还在
    { SimHardware sim2; CoreApp app2(sim2, sim2, base.u8string(), gains_copy); CHECK(json::parse(app2.handle("{\"cmd\":\"seq.get\",\"ch\":2}", false))["steps"].size() == 3); }
    // 报警：把限值收到比现在还低 → 开控就跳，报警能列、能确认
    CHECK(remote({{"cmd", "limits.set"}, {"ch", 1}, {"tr_max", 10}})["ok"] == true);
    CHECK(remote({{"cmd", "temp.start"}, {"ch", 1}, {"mode", "reactor"}, {"target", 30}})["ok"] == true);
    run(3);
    s = remote({{"cmd", "state"}});
    CHECK(s["ch"][0]["running"] == false && s["alarms_unacked"] == 1);
    json al = remote({{"cmd", "alarm.list"}});
    CHECK(al["alarms"].size() == 1 && al["alarms"][0]["ch"] == 1 && al["alarms"][0]["acked"] == false);
    CHECK(remote({{"cmd", "alarm.ack"}, {"id", al["alarms"][0]["id"]}})["ok"] == true && remote({{"cmd", "state"}})["alarms_unacked"] == 0);
    // 手动结晶曲线：存得进、读得回；开始时不带曲线就用存着的；跑起来以后状态里报的是正在走的那条和起点温度
    CHECK(remote({{"cmd", "limits.set"}, {"ch", 2}, {"tr_max", 150}})["ok"] == true);
    const json prof = json::array({{{"target", 30.0}, {"minutes", 1.0}}, {{"target", 30.0}, {"minutes", 5.0}}, {{"target", 20.0}, {"minutes", 10.0}}});
    CHECK(remote({{"cmd", "profile.set"}, {"ch", 2}, {"segments", prof}})["ok"] == true);
    CHECK(remote({{"cmd", "profile.get"}, {"ch", 2}})["segments"] == prof);
    s = remote({{"cmd", "state"}});
    CHECK(s["ch"][1]["profile"]["active"] == false && s["ch"][1]["profile"]["segments"] == prof);      // 没在跑：报的是存着的手动曲线
    const double tr0 = s["ch"][1]["tr"].get<double>();
    CHECK(remote({{"cmd", "temp.profile"}, {"ch", 2}})["ok"] == true);
    run(90);
    s = remote({{"cmd", "state"}});
    CHECK(s["ch"][1]["mode"] == "crystal" && s["ch"][1]["profile"]["active"] == true && s["ch"][1]["profile"]["segments"] == prof && s["ch"][1]["segment"] == 1);
    CHECK(std::fabs(s["ch"][1]["profile"]["start"].get<double>() - tr0) < 1.0);
    // 带着曲线开始：按带来的走，并且记成新的手动曲线
    const json prof2 = json::array({{{"target", 25.0}, {"minutes", 3.0}}});
    CHECK(remote({{"cmd", "temp.profile"}, {"ch", 2}, {"segments", prof2}})["ok"] == true);
    run(2);
    CHECK(remote({{"cmd", "state"}})["ch"][1]["profile"]["segments"] == prof2 && remote({{"cmd", "profile.get"}, {"ch", 2}})["segments"] == prof2);
    CHECK(remote({{"cmd", "temp.stop"}, {"ch", 2}})["ok"] == true);
    run(2);
    CHECK(remote({{"cmd", "state"}})["ch"][1]["profile"]["active"] == false);
    // 重启：手动曲线还在
    { SimHardware sim3; CoreApp app3(sim3, sim3, base.u8string(), gains_copy); CHECK(json::parse(app3.handle("{\"cmd\":\"profile.get\",\"ch\":2}", false))["segments"] == prof2); }
    CHECK(remote({{"cmd", "profile.set"}, {"ch", 2}, {"segments", json::array()}})["ok"] == true && remote({{"cmd", "temp.profile"}, {"ch", 2}})["ok"] == false);   // 清空以后没曲线可跑
    // 增益表：控着温不让换；停下来以后能改、能存盘、能从盘上重读
    {
        const json rows0 = remote({{"cmd", "gains.get"}})["rows"];
        CHECK(rows0.size() >= 2);
        json rows1 = rows0; rows1[0]["kp"] = 12.5;
        CHECK(remote({{"cmd", "temp.start"}, {"ch", 2}, {"mode", "jacket"}, {"target", 26}})["ok"] == true);
        run(2);
        CHECK(remote({{"cmd", "gains.set"}, {"rows", rows1}})["ok"] == false);
        CHECK(remote({{"cmd", "temp.stop"}, {"ch", 2}})["ok"] == true);
        run(2);
        CHECK(remote({{"cmd", "gains.set"}, {"rows", rows1}})["ok"] == true && remote({{"cmd", "gains.get"}})["rows"][0]["kp"] == 12.5);
        CHECK(GainTable::load(gains_copy).rows()[0].kp == 12.5 && GainTable::load(gains_copy).rows().size() == rows0.size());      // 存到盘上了，行数没丢
        CHECK(GainTable::load(gains_copy + ".原始备份").rows()[0].kp == rows0[0]["kp"].get<double>());                             // 原来那份留了备份
        json bad = rows1; bad[1]["temp"] = bad[0]["temp"];
        CHECK(remote({{"cmd", "gains.set"}, {"rows", bad}})["ok"] == false && remote({{"cmd", "gains.set"}, {"rows", json::array()}})["ok"] == false);
        CHECK(remote({{"cmd", "gains.reload"}})["ok"] == true && remote({{"cmd", "gains.get"}})["rows"][0]["kp"] == 12.5);
    }
    // 通信状态：九台设备的参数都在；没接主程序那一头时状态是「不知道」，停用的那台是 off
    { const json cs = remote({{"cmd", "comm.status"}}); CHECK(cs["ok"] == true && cs["devices"].size() == 9 && cs["devices"][0]["key"] == "rd105" && cs["devices"][0]["state"] == "unknown" && cs["devices"][2]["state"] == "off"); }
    { const json si = remote({{"cmd", "sys.info"}}); CHECK(si["ok"] == true && si["version"].is_string() && si["disk_free_gb"].is_number() && si["runs"].get<int>() >= 2 && si["gain_rows"].get<int>() >= 2); }
    // 照明只记状态；时间标记进趋势数据；快照存成文件；导出可以只导一段；导出目录能整个拷到别处（U 盘）
    {
        CHECK(remote({{"cmd", "state"}})["ch"][0]["lamp"] == false && remote({{"cmd", "lamp.set"}, {"ch", 1}, {"on", true}})["ok"] == true && remote({{"cmd", "state"}})["ch"][0]["lamp"] == true);
        CHECK(remote({{"cmd", "mark.add"}, {"ch", 1}, {"text", "取样"}})["count"] == 1);
        const json tm = remote({{"cmd", "trend.get"}, {"ch", 1}, {"max", 1}});
        CHECK(tm["marks"].size() == 1 && tm["marks"][0]["text"] == "取样");
        const json sn = remote({{"cmd", "trend.snapshot"}, {"ch", 1}});
        CHECK(sn["ok"] == true && sn["rows"].get<int>() > 10 && fs::exists(base / "Exports" / fs::u8path("快照") / fs::u8path(sn["file"].get<std::string>())));
        const json rl = remote({{"cmd", "runs.list"}});
        const std::string rid = rl["runs"].back()["id"];                    // 最早那一炉：通道 1 控到 40 ℃，跑了两分多钟
        CHECK(remote({{"cmd", "runs.export"}, {"ids", json::array({rid})}, {"interval_s", 1}, {"from_min", 0.5}, {"to_min", 1.0}})["exported"] == 1);
        size_t lines = 0;
        for (auto& e : fs::directory_iterator(base / "Exports")) if (e.path().filename().u8string().find("0-1 min") != std::string::npos || e.path().filename().u8string().find("（") != std::string::npos) {
            std::ifstream in(e.path() / fs::u8path("采样宽表.csv")); std::string l; while (std::getline(in, l)) lines++;
        }
        CHECK(lines >= 29 && lines <= 33);                                  // 表头 + 30 秒到 60 秒之间每秒一行
        CHECK(remote({{"cmd", "runs.export"}, {"ids", json::array({rid})}, {"from_min", 2}, {"to_min", 1}})["ok"] == false);
        const fs::path usb = base / "fake_usb";
        const json cp = remote({{"cmd", "export.copy"}, {"dest", usb.u8string()}});
        CHECK(cp["ok"] == true && cp["files"].get<int>() >= 4 && fs::exists(usb / fs::u8path("TEC导出") / fs::u8path("快照")));
        CHECK(remote({{"cmd", "drives.list"}})["drives"].is_array());
    }
    // 趋势数据：2 秒一点，不控温时也在记；带 after 只给新增的
    json tr = remote({{"cmd", "trend.get"}, {"ch", 1}});
    CHECK(tr["ok"] == true && tr["t"].size() > 50 && tr["t"].size() == tr["tr"].size() && tr["t"].size() == tr["tj_sp"].size());
    CHECK(std::fabs(tr["t"][1].get<double>() - tr["t"][0].get<double>() - 2.0) < 1e-6);
    const double last_t = tr["t"].back().get<double>();
    run(10);
    tr = remote({{"cmd", "trend.get"}, {"ch", 1}, {"after", last_t}});
    CHECK(tr["t"].size() == 5 && tr["t"][0].get<double>() > last_t && tr["sp"].back().is_null());       // 没在控温：给定是空的
    CHECK(remote({{"cmd", "trend.get"}, {"ch", 1}, {"max", 3}})["t"].size() == 3);
    // 控温设备没连上（串口配错）时：核心照样起来、照样答命令、能改通信设置，只是不收开始控温和开始序列
    {
        const fs::path b4 = fs::temp_directory_path() / "tec_core_app_test_offline";
        fs::remove_all(b4, ec);
        OfflineReactorHardware off("温控器没连上");
        SimHardware aux4;
        CoreApp app4(off, aux4, b4.u8string(), gains_copy);
        app4.offline_reason = "温控器没连上";
        for (int i = 0; i < 20; i++) app4.tick();
        auto l4 = [&](const json& q) { return json::parse(app4.handle(q.dump(), false)); };
        json s4 = l4({{"cmd", "state"}});
        CHECK(s4["ok"] == true && s4["comm_ok"] == false && s4["offline"] == "温控器没连上" && s4["ch"][0]["running"] == false);
        CHECK(l4({{"cmd", "temp.start"}, {"ch", 1}, {"mode", "jacket"}, {"target", 30}})["ok"] == false);
        CHECK(l4({{"cmd", "seq.load"}, {"ch", 1}, {"steps", json::array({{{"kind", "wait"}, {"minutes", 1}}})}})["ok"] == true && l4({{"cmd", "seq.start"}, {"ch", 1}})["ok"] == false);
        CHECK(l4({{"cmd", "settings.set"}, {"device", {{"key", "rd105"}, {"port", "COM21"}}}})["ok"] == true);
        CHECK(l4({{"cmd", "comm.status"}})["devices"][0]["port"] == "COM21");
        for (int i = 0; i < 20; i++) app4.tick();
        CHECK(l4({{"cmd", "state"}})["ch"][0]["running"] == false && l4({{"cmd", "state"}})["alarms_unacked"] == 0);     // 没在控温，读不到数也不该报警
        fs::remove_all(b4, ec);
    }
    // 服务端看得到这台 PC
    CHECK(srv.clients().size() == 1 && srv.clients()[0].address == "127.0.0.1" && srv.clients()[0].rx > 20);
    pc.close();
    srv.stop();
    fs::remove_all(base, ec);
    std::printf(fails ? "\n%d 项失败\n" : "控制核心端到端：全部通过\n", fails);
    return fails ? 1 : 0;
}
