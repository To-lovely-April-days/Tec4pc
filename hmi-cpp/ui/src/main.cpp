// 触摸屏界面程序。它自己不碰设备，只通过本机 TCP 连控制核心（tec_core）：每半秒问一次状态刷到屏上，点了什么就发一条命令过去。
//   tec_hmi [--host 127.0.0.1] [--port 5020] [--page 页面名] [--shot 文件 --after 秒]
//   --shot：不开窗口，跑够时间把屏幕存成一张图就退出（开发时自查用）
#include "../../net/tcp_server.hpp"
#include "../../third_party/nlohmann/json.hpp"
#include "ui_kit.hpp"
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <ctime>
#include <map>
#include <memory>
#include <string>
#include <thread>
#include <algorithm>
#include <set>
#include <cctype>
#include <deque>
#include <filesystem>
#include <vector>
#include <functional>

#ifdef _WIN32
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#endif

using json = nlohmann::json;
using namespace ui;

#define W (1280 * UI_SCALE)
#define H (720 * UI_SCALE)

#include PAGES_INC

static uint32_t g_fb[W * H];
static void flush(lv_display_t* d, const lv_area_t* a, uint8_t* px) {
    const int w = a->x2 - a->x1 + 1;
    if (std::getenv("UI_TRACE")) std::fprintf(stderr, "flush %d,%d %dx%d\n", a->x1, a->y1, w, a->y2 - a->y1 + 1);
    for (int y = a->y1; y <= a->y2; y++) std::memcpy(&g_fb[y * W + a->x1], px + static_cast<size_t>(y - a->y1) * w * 4, static_cast<size_t>(w) * 4);
    lv_display_flush_ready(d);
}

static std::string f1(double v) { char b[32]; std::snprintf(b, sizeof b, "%.1f", v); std::string s = b; if (s == "-0.0") s = "0.0";      /* 四舍五入到 0 的小负数不带负号 */ if (s[0] == '-') s.replace(0, 1, "−"); return s; }
static std::string f2(double v) { char b[32]; std::snprintf(b, sizeof b, "%.2f", v); return b; }
static std::string f0(double v) { char b[32]; std::snprintf(b, sizeof b, "%.0f", v); std::string s = b; if (s == "-0") s = "0"; if (s[0] == '-') s.replace(0, 1, "−"); return s; }      // 负号用正经的减号，和一位小数的那个一致
static std::string hms(double s) { const long t = static_cast<long>(s < 0 ? 0 : s); char b[32]; std::snprintf(b, sizeof b, "%02ld:%02ld:%02ld", t / 3600, t / 60 % 60, t % 60); return b; }

#include "pinyin_dict.inc"
#include "phrase_dict.inc"

struct App {
    tec::TcpClient link;
    std::string host = "127.0.0.1";
    int port = 5020;
    bool connected = false;
    json st;                                   // 最近一次的整机状态
    struct Page { lv_obj_t* scr = nullptr; std::unique_ptr<Ui> ui; };
    std::map<std::string, Page> pages;
    std::string cur;                           // 现在显示的页面
    int channel = 1;                           // 现在看的是哪个通道
    int ch_tab = 0;                            // 通道页里的第几个页签
    // 还没下发的本地选择：没在控温时改的目标、到达方式、控温方式，开控那一下一起发
    double pending_sp[2] = {25, 25}, arrive_val[2] = {1, 1};
    int arrive[2] = {0, 0}, mode[2] = {0, 0};
    double reflux_dk[2] = {8, 8};              // 蒸回流：夹套比釜内高多少 K（开控那一下发给核心）
    std::string ctl_msg[2];                    // 设备控制页状态行上临时要说的一句话
    json seq_steps[2];
    std::string seq_key[2];

    // ── 数字键盘 ──
    Page kp;
    struct { std::string text, unit; double min = 0, max = 100; bool fresh = true; std::function<void(double)> ok; } kps;

    void open_keypad(const std::string& title, const std::string& unit, double value, double lo, double hi, const std::string& last, const std::string& now, std::function<void(double)> ok, int dec = -1) {
        if (!kp.scr) {
            kp.scr = lv_obj_create(lv_layer_top());
            lv_obj_remove_style_all(kp.scr);
            lv_obj_set_size(kp.scr, W, H);
            lv_obj_add_flag(kp.scr, LV_OBJ_FLAG_CLICKABLE);          // 弹窗开着的时候，点不到后面的页面
            kp.ui = std::make_unique<Ui>();
            kp.ui->on_action = [this](const std::string& a) { action(a); };
            for (const PageDef& d : kPages) if (std::string(d.name) == "KeypadDlg") d.build(kp.scr, *kp.ui);
            // 滑块可以按住拖：点一下只触发一次，拖动要另外接「按住」这个事件
            if (lv_obj_t* tr = kp.ui->box("kp.track")) lv_obj_add_event_cb(tr, [](lv_event_t* e) { static_cast<App*>(lv_event_get_user_data(e))->slide(); }, LV_EVENT_PRESSING, this);
        }
        kps.unit = unit; kps.min = lo; kps.max = hi; kps.fresh = true; kps.ok = std::move(ok);
        char b[32];
        if (dec >= 0) std::snprintf(b, sizeof b, "%.*f", dec, value);
        else std::snprintf(b, sizeof b, hi - lo > 50 || unit == "rpm" ? (unit == "rpm" ? "%.0f" : "%.1f") : "%.2f", value);
        kps.text = b;
        Ui& u = *kp.ui;
        u.set_text("kp.title", title); u.set_text("kp.unit", unit);
        u.set_text("kp.min", f0(lo)); u.set_text("kp.max", f0(hi));
        u.set_text("kp.last", last); u.set_text("kp.now", now); u.set_text("kp.curtext", "");
        if (lv_obj_t* c = u.box("kp.cur")) lv_obj_add_flag(c, LV_OBJ_FLAG_HIDDEN);
        lv_obj_remove_flag(kp.scr, LV_OBJ_FLAG_HIDDEN);
        keypad_show();
    }
    void keypad_show() {
        Ui& u = *kp.ui;
        std::string shown = kps.text.empty() ? "0" : kps.text;
        if (shown[0] == '-') shown.replace(0, 1, "−");
        u.set_text("kp.value", shown);
        const double v = std::atof(kps.text.c_str());
        lv_obj_t* tr = u.box("kp.track");
        if (!tr) return;
        const int tw = lv_obj_get_width(tr);
        const double fr = kps.max > kps.min ? std::min(1.0, std::max(0.0, (v - kps.min) / (kps.max - kps.min))) : 0.0;
        if (lv_obj_t* f = u.box("kp.fill")) lv_obj_set_width(f, static_cast<int>(fr * tw));
        if (lv_obj_t* k = u.box("kp.knob")) lv_obj_set_x(k, static_cast<int>(fr * tw) - lv_obj_get_width(k) / 2);
    }
    void slide() {
        lv_indev_t* in = lv_indev_active();
        lv_obj_t* tr = kp.ui ? kp.ui->box("kp.track") : nullptr;
        if (!in || !tr) return;
        lv_point_t p; lv_indev_get_point(in, &p);
        lv_area_t a; lv_obj_get_coords(tr, &a);
        const double fr = std::min(1.0, std::max(0.0, static_cast<double>(p.x - a.x1) / std::max(1, a.x2 - a.x1)));
        // 拖动时按整数走：手指拖不出小数点后的精度，要小数就用右边的数字键
        char b[32]; std::snprintf(b, sizeof b, "%.0f", std::round(kps.min + fr * (kps.max - kps.min)));
        kps.text = b; kps.fresh = true;
        keypad_show();
    }
    bool keypad_action(const std::string& a) {
        if (a.rfind("kp:", 0) != 0) return false;
        if (!kp.scr) return true;                  // 键盘还没打开过：没有东西可按
        if (a == "kp:close") lv_obj_add_flag(kp.scr, LV_OBJ_FLAG_HIDDEN);
        else if (a == "kp:back") { if (!kps.text.empty()) kps.text.pop_back(); kps.fresh = false; }
        else if (a == "kp:slide") slide();
        else if (a == "kp:ok") {
            const double v = std::min(kps.max, std::max(kps.min, std::atof(kps.text.c_str())));      // 超出范围的按范围的边算，不让填进去
            lv_obj_add_flag(kp.scr, LV_OBJ_FLAG_HIDDEN);
            if (kps.ok) kps.ok(v);
            return true;
        }
        else if (a.rfind("kp:key:", 0) == 0) {
            const std::string k = a.substr(7);
            if (k == "±") { if (!kps.text.empty() && kps.text[0] == '-') kps.text.erase(0, 1); else kps.text = "-" + kps.text; }
            else {
                if (kps.fresh) kps.text.clear();           // 刚打开时按第一个数字是重新输，不是接在原来的数后面
                if (k == "." ) { if (kps.text.find('.') == std::string::npos) kps.text += kps.text.empty() || kps.text == "-" ? "0." : "."; }
                else if (kps.text.size() < 8) kps.text += k;
            }
            kps.fresh = false;
        }
        if (!lv_obj_has_flag(kp.scr, LV_OBJ_FLAG_HIDDEN)) keypad_show();
        return true;
    }

    // 搅拌 / 照明那种圆键按开关状态改样子：开着是蓝圈、蓝图标、绿灯；关着整个是灰的。
    // p 是这个键的名字前缀（设备控制页 ctl.stir，总览页 main.1.stir），val 是旁边那个读数的名字
    void set_ring(Ui& u, const std::string& p, const std::string& val, bool on) {
        if (!u.changed(p + ".on", on)) return;
        if (lv_obj_t* r = u.box(p + ".ring")) { bg(r, on ? 0xEAF2FB : 0xF3F5F8, 255); bd(r, on ? 0x00559C : 0xD5DCE4, 255, 3 * UI_SCALE, LV_BORDER_SIDE_FULL); }
        if (lv_obj_t* i = u.box(p + ".icon")) { lv_obj_set_style_image_recolor(i, lv_color_hex(0x9AA5B1), 0); lv_obj_set_style_image_recolor_opa(i, on ? 0 : 255, 0); }
        if (lv_obj_t* d = u.box(p + ".led")) bg(d, on ? 0x2FD27A : 0xC3CCD6, 255);
        u.set_text_color(p + ".name", on ? 0x4F5B67 : 0x8792A0);
        u.set_text_color(val, on ? 0x00559C : 0x8792A0);
    }

    // 一组圆点单选按「选了第几个」改样子
    void set_radio(Ui& u, const std::string& group, int n, int sel) {
        for (int i = 0; i < n; i++) {
            const std::string id = "rg." + group + "." + std::to_string(i);
            const bool on = i == sel;
            if (!u.changed(id, on)) continue;
            if (lv_obj_t* r = u.box(id + ".ring")) bd(r, on ? 0x00559C : 0xA9B7C6, 255, 2 * UI_SCALE, LV_BORDER_SIDE_FULL);
            if (lv_obj_t* d = u.box(id + ".in")) bg(d, 0x00559C, on ? 255 : 0);
            u.set_text_color(id, on ? 0x00559C : (group == "mode" ? 0x1F2A35 : 0x5A6675));
        }
    }

#include "text_kb.inc"
#include "seq_page.inc"
#include "cry_page.inc"
#include "trend_page.inc"
#include "lib_page.inc"
#include "safety_page.inc"
#include "export_page.inc"
#include "pid_page.inc"
#include "comm_page.inc"
#include "overview_page.inc"
#include "sys_page.inc"

    json ask(const json& q) {
        if (!connected) connected = link.connect(host, port);
        std::string r;
        if (connected && link.request(q.dump(), r)) { const json j = json::parse(r, nullptr, false); if (!j.is_discarded()) return j; }
        connected = false; link.close();
        return json{{"ok", false}, {"error", "连不上控制核心"}};
    }

    void show(const std::string& name) {
        Page& p = pages[name];
        if (!p.scr) {
            for (const PageDef& d : kPages) if (name == d.name) {
                p.scr = lv_obj_create(nullptr);
                lv_obj_remove_style_all(p.scr);
                lv_obj_remove_flag(p.scr, LV_OBJ_FLAG_SCROLLABLE);
                p.ui = std::make_unique<Ui>();
                p.ui->on_action = [this](const std::string& a) { action(a); };
                d.build(p.scr, *p.ui);
            }
            if (!p.scr) { pages.erase(name); return; }      // 这一页还没做
        }
        cur = name;
        lv_screen_load(p.scr);
        refresh();
    }

    std::string channel_page() const {
        static const char* tabs[] = {"Ctl", "ChTrend", "Seq", "Safety", "Export", "Pid"};
        return std::string(tabs[ch_tab]) + (channel == 2 ? "_2" : "");
    }

    static std::string step_label(const json& s) {
        const std::string k = s.value("kind", "");
        if (k == "reactor") return "Tr " + f1(s.value("target", 0.0));
        if (k == "jacket") return "Tj " + f1(s.value("target", 0.0));
        if (k == "reflux") return "回流";
        if (k == "crystal") return "结晶";
        if (k == "stir") return "R " + f0(s.value("stir_rpm", 0.0));
        if (k == "wait") return "Wait";
        if (k == "dose") return "加料";
        return "暂停";
    }

    void action(const std::string& a) {
        const json ch = channel;
        const int ci = channel - 1;
        if (keypad_action(a)) return;
        if (text_kb_action(a)) return;
        if (lib_action(a)) return;          // 排在任务序列前面：「从配方库」这个键归它管
        if (seq_action(a)) return;
        if (cry_action(a)) return;
        if (trend_action(a)) return;
        if (safety_action(a)) return;
        if (export_action(a)) return;
        if (pid_action(a)) return;
        if (sys_action(a)) return;          // 排在通信设置前面：顶栏「设置」先进系统设置这一页
        if (comm_action(a)) return;
        if (overview_action(a)) return;
        if (alarm_action(a)) return;
        if (a == "edit:target" && st.is_object()) {
            const json& c = st["ch"][ci];
            const bool run = c.value("running", false);
            const double cur = run ? c.value("sp", 25.0) : pending_sp[ci];
            if (mode[ci] == 2) {
                // 蒸回流填的不是目标温度，是「夹套比釜内高多少」。已经在回流时核心不收改动，先停再改
                if (run) { ctl_msg[ci] = "蒸回流进行中改不了温差：先停下，改好再开"; refresh(); return; }
                open_keypad("通道 " + std::to_string(channel) + " · 蒸回流：夹套比釜内高", "K", reflux_dk[ci], 1, 30, f1(reflux_dk[ci]) + " K",
                            c["tr"].is_number() ? f1(c.value("tj", 0.0) - c["tr"].get<double>()) + " K" : "—", [this, ci](double v) { reflux_dk[ci] = v; refresh(); }, 1);
                return;
            }
            const bool jk = mode[ci] == 1;       // 夹套控温：目标是夹套温度，上限按夹套的 200 ℃
            const json& nowv = jk ? c["tj"] : c["tr"];
            open_keypad("通道 " + std::to_string(channel) + (jk ? " · 目标 Tj" : " · 目标 Tr"), "℃", cur, -40, jk ? 200 : 180, f1(cur) + " ℃", nowv.is_number() ? f1(nowv.get<double>()) + " ℃" : "—",
                        [this, ci, run](double v) { pending_sp[ci] = v; if (run) ask({{"cmd", "temp.setpoint"}, {"ch", ci + 1}, {"target", v}}); poll(); });
            return;
        }
        if (a == "edit:stir" && st.is_object()) {
            const json& c = st["ch"][ci];
            open_keypad("通道 " + std::to_string(channel) + " · 搅拌转速", "rpm", c["stir"].value("set", 0.0), 0, 1000, f0(c["stir"].value("set", 0.0)) + " rpm", f0(c["stir"].value("rpm", 0.0)) + " rpm",
                        [this, ci](double v) { ask({{"cmd", "stir.set"}, {"ch", ci + 1}, {"rpm", v}}); poll(); });
            return;
        }
        if (a.rfind("arrive:", 0) == 0) {
            arrive[ci] = a[7] - '0';
            if (arrive[ci] != 0)      // 按速率 / 按时长要一个数：直接弹键盘问
                open_keypad(arrive[ci] == 1 ? "升降温速率" : "到达用时", arrive[ci] == 1 ? "℃/min" : "min", arrive_val[ci], arrive[ci] == 1 ? 0.01 : 1, arrive[ci] == 1 ? 20 : 600, "—", "—", [this, ci](double v) { arrive_val[ci] = v; refresh(); });
            refresh();
            return;
        }
        // 总览页上的三个键：改设定值、搅拌、照明。做的事和通道页里的一样，只是先认一下是哪个通道
        if (a.rfind("main:", 0) == 0 && a.size() > 6) {
            channel = a.back() - '0';
            const std::string k = a.substr(5, a.size() - 7);
            if (k == "sp") action("edit:target"); else if (k == "stir") action("stir.toggle"); else if (k == "lamp") action("lamp.toggle");
            return;
        }
        if (a.rfind("mode:", 0) == 0) {
            mode[ci] = a[5] - '0';
            cry_msg[ci].clear(); ctl_msg[ci].clear();
            if (ced.on && ced.ci == ci && !ced.from_seq) ced.on = false;      // 编辑到一半换了控温方式：当作取消
            refresh();
            return;
        }
        if (a == "nav:ch1" || a == "nav:ch2") { channel = a == "nav:ch1" ? 1 : 2; show(channel_page()); }
        else if (a == "nav:overview") show("Main");
        else if (a.rfind("tab:", 0) == 0 && (cur.rfind("Ctl", 0) == 0 || cur.rfind("Seq", 0) == 0 || cur.rfind("ChTrend", 0) == 0 || cur.rfind("Safety", 0) == 0 || cur.rfind("Export", 0) == 0 || cur.rfind("Pid", 0) == 0)) {
            const int was = ch_tab;
            ch_tab = a[4] - '0';
            const std::string want = channel_page();
            show(want);
            if (cur != want) ch_tab = was;       // 那一页还没做：留在原来的页签上
        }
        else if (a == "temp.toggle") {
            ctl_msg[ci].clear();
            const json& c = st["ch"][channel - 1];
            if (c.value("running", false)) ask({{"cmd", "temp.stop"}, {"ch", ch}});
            else {
                json q = {{"cmd", "temp.start"}, {"ch", ch}, {"mode", mode[ci] == 1 ? "jacket" : "reactor"}, {"target", pending_sp[ci]},
                          {"arrive", arrive[ci] == 1 ? "rate" : arrive[ci] == 2 ? "duration" : "fast"}};
                if (arrive[ci] == 1) q["rate_k_per_min"] = arrive_val[ci];
                if (arrive[ci] == 2) q["duration_min"] = arrive_val[ci];
                if (mode[ci] == 2) q = {{"cmd", "temp.reflux"}, {"ch", ch}, {"delta_k", reflux_dk[ci]}};
                if (mode[ci] == 3) {
                    if (cry_shown(c).empty()) { cry_msg[ci] = "先点「编辑曲线」加几段"; refresh(); return; }
                    q = {{"cmd", "temp.profile"}, {"ch", ch}};       // 不带曲线：核心用它存着的那条手动曲线
                }
                const json r = ask(q);
                cry_msg[ci] = mode[ci] == 3 && !r.value("ok", false) ? r.value("error", std::string("开始不了")) : "";
            }
            poll();
        }
        else if (a == "stir.toggle") {
            const double set = st["ch"][channel - 1]["stir"].value("set", 0.0);
            ask({{"cmd", "stir.set"}, {"ch", ch}, {"rpm", set > 0 ? 0 : 300}});
            poll();
        }
        else if (a == "lamp.toggle") {
            // 照明现在只有状态（还没接通信，用户 2026-11-24：后续会接）：核心记着开没开，两处界面和 PC 端看到的是同一个
            ask({{"cmd", "lamp.set"}, {"ch", ch}, {"on", !st["ch"][channel - 1].value("lamp", false)}});
            poll();
        }
        else if (a == "seq.end") { ask({{"cmd", "seq.end"}, {"ch", ch}}); poll(); }
    }

    void poll() {
        const json j = ask({{"cmd", "state"}});
        if (j.value("ok", false)) st = j;
        refresh();
    }

    // 变温速度（℃/min）核心不给，这里拿最近一分钟的釜温自己算：只是给人看个趋势，不参与控制
    std::deque<std::pair<double, double>> hist[2];
    double rate_of(int ci, double t, double v) {
        auto& h = hist[ci];
        if (h.empty() || t - h.back().first >= 1.0) h.emplace_back(t, v);
        while (h.size() > 2 && t - h.front().first > 60.0) h.pop_front();
        return h.size() < 2 || h.back().first - h.front().first < 5.0 ? 0.0 : (h.back().second - h.front().second) / (h.back().first - h.front().first) * 60.0;
    }

    void refresh_main(Ui& u) {
        for (int n = 1; n <= 2; n++) {
            const json& c = st["ch"][n - 1];
            const std::string k = "main." + std::to_string(n);
            const bool run = c.value("running", false);
            const json& q = c["seq"];
            const std::string qs = q.value("state", "idle"), mode = c.value("mode", "off");
            const bool seq_on = qs == "running" || qs == "waiting_confirm";
            const bool live = run || seq_on;
            const double duty = c.value("duty", 0.0);
            if (lv_obj_t* d = u.box(k + ".dot")) if (u.changed(k + ".dot", live)) bg(d, live ? 0x00B368 : 0xBFC7CF, 255);
            const std::string name = c.contains("run") && c["run"].is_string() ? c["run"].get<std::string>() : "";      // 没在跑序列时核心给的是空值，不是空字符串
            u.set_text(k + ".name", !name.empty() ? name : live ? "手动运行" : "未在运行");
            u.set_text(k + ".r1", seq_on ? hms(q.value("total_elapsed_s", 0.0)) : run ? "控温中" : "待机");
            const char* mname = mode == "reactor" ? "釜内控温 Tr" : mode == "jacket" ? "夹套控温 Tj" : mode == "reflux" ? "蒸回流" : mode == "crystal" ? "结晶" : "未控温";
            u.set_text(k + ".mode", std::string(seq_on ? "程序模式" : "手动模式") + " · " + mname);
            u.set_text(k + ".r2", qs == "waiting_confirm" ? "等待确认后继续" : seq_on ? "步骤 " + std::to_string(q.value("index", 0) + 1) + " / " + std::to_string(q.value("count", 0))
                                  : !run ? "" : duty < -1 ? "加热中" : duty > 1 ? "制冷中" : "保温中");
            // 顶上那条细进度：整个序列走了多少（走完的步数 + 当前这一步走了几成）
            const int cnt = q.value("count", 0);
            const double fr = seq_on && cnt > 0 ? std::min(1.0, (q.value("index", 0) + q.value("step_progress", 0.0)) / cnt) : 0.0;
            if (lv_obj_t* bar = u.box(k + ".bar")) if (lv_obj_t* fill = u.box(k + ".fill")) lv_obj_set_width(fill, static_cast<int>(fr * lv_obj_get_width(bar)));
            const bool has_tr = c["tr"].is_number();
            const double tj = c.value("tj", 0.0), tr = has_tr ? c["tr"].get<double>() : tj;
            u.set_text(k + ".ph", c["ph"].is_number() ? f2(c["ph"].get<double>()) : "—");
            u.set_text_color(k + ".ph", live ? 0x00B368 : 0x5A6675);
            u.set_text(k + ".tr", has_tr ? f1(tr) : "—");
            u.set_text_color(k + ".tr", live ? 0x0A2A6B : 0x5A6675);
            u.set_text(k + ".sp", run ? f1(c.value("sp", 0.0)) : f1(pending_sp[n - 1]));
            const double set = c["stir"].value("set", 0.0), rpm = c["stir"].value("rpm", 0.0);
            u.set_text(k + ".stir", set > 0 ? f0(rpm) + " rpm" : "关");
            set_ring(u, k + ".stir", k + ".stir", set > 0);
            const bool lamp = c.value("lamp", false);
            u.set_text(k + ".lamp", lamp ? "开" : "关");
            set_ring(u, k + ".lamp", k + ".lamp", lamp);
            u.set_text(k + ".t0", f1(tj));
            const double dt = tr - tj;
            u.set_text(k + ".t1", has_tr ? (dt >= 0 ? "+" : "") + f1(dt) : "—");
            u.set_text(k + ".t2", f0(rpm));
            u.set_text(k + ".t3", f1(c["dose"].value("dosed_ml", 0.0)));
            u.set_text(k + ".t4", st["coolant_c"].is_number() ? f1(st["coolant_c"].get<double>()) : "—");      // 冷媒温度 = 冷水机的冷却水温度（两个通道共用一台冷水机）；没读到就空着
            const double rt = rate_of(n - 1, st.value("t", 0.0), tr);
            u.set_text(k + ".t5", (rt >= 0 ? "+" : "") + f1(rt));
        }
    }

    // 搅拌卡上的三处读数。设备控制页和结晶查看那一块各有一张搅拌卡，两处都用它
    void refresh_stir(Ui& u, const json& c) {
        const double stir_set = c["stir"].value("set", 0.0), rpm = c["stir"].value("rpm", 0.0);
        u.set_text("ctl.stir_set", f0(stir_set));
        u.set_text("ctl.stir.val", stir_set > 0 ? f0(rpm) + " rpm" : "关");
        set_ring(u, "ctl.stir", "ctl.stir.val", stir_set > 0);
        const bool lamp = c.value("lamp", false);
        u.set_text("ctl.lamp.val", lamp ? "开" : "关");
        set_ring(u, "ctl.lamp", "ctl.lamp.val", lamp);
        u.set_text("ctl.stir.status", "实测 " + f0(rpm) + " rpm　·　电流 " + f2(c["stir"].value("current_a", 0.0)) + " A");
        u.set_text("ctl.stir.status2", "桨式 · 50–1000 rpm　·　实测 " + f0(rpm) + " rpm · 电流 " + f2(c["stir"].value("current_a", 0.0)) + " A");      // 结晶那一块里的搅拌卡矮一些，读数并在标题那一行
    }

    // 把最近一次的状态刷到当前这一页上。只改有名字的文字；没变的不动
    void refresh() {
        auto it = pages.find(cur);
        if (it == pages.end() || !it->second.ui) return;
        Ui& u = *it->second.ui;
        const std::time_t now = std::time(nullptr);
        std::tm tm{};
#ifdef _WIN32
        localtime_s(&tm, &now);
#else
        localtime_r(&now, &tm);
#endif
        char b[32];
        std::strftime(b, sizeof b, "%H:%M", &tm); u.set_text("top.clock", b);
        std::strftime(b, sizeof b, "%Y.%m.%d", &tm); u.set_text("top.date", b);
        if (!st.is_object() || !st.contains("ch")) { u.set_text("top.water", connected ? "读取中…" : "未连上控制核心"); return; }
        // 控温设备没连上（串口配错之类）：核心还活着但不能控温。顶栏这个位置平时写冷却水，这时换成红点和这句话，哪一页都看得见
        const bool offline = !st.value("offline", std::string()).empty();
        u.set_text("top.water", offline ? "设备未连接" : st.value("valve_open", false) ? "冷却水已开" : "冷却水已关");
        const int wd = offline ? 2 : st.value("valve_open", false) ? 1 : 0;
        if (lv_obj_t* d = u.box("top.water.dot")) if (u.changed("top.water.dot", wd)) bg(d, wd == 2 ? 0xFF5A5F : wd == 1 ? 0x5BF08A : 0x9AAABB, 255);
        alarm_refresh();
        const int na = st.value("alarms_unacked", 0);
        u.set_text("top.alarms", std::to_string(na));
        u.show("top.alarms", na > 0);
        for (int n = 1; n <= 2; n++) {
            const json& c = st["ch"][n - 1];
            const std::string k = "top.ch" + std::to_string(n);
            const bool run = c.value("running", false);
            const json& q = c["seq"];
            const std::string qs = q.value("state", "idle");
            // 顶上通道键显示釜内温度；釜内读不到就显示夹套的
            const json& t = c["tr"].is_number() ? c["tr"] : c["tj"];
            u.set_text(k + ".temp", t.is_number() && !offline ? f1(t.get<double>()) : "—");       // 设备没连上时核心里的温度是没读过的 0，不能当读数显示
            u.set_text(k + ".state", qs == "running" ? "运行中 · " + std::to_string(q.value("index", 0) + 1) + " / " + std::to_string(q.value("count", 0))
                                     : qs == "waiting_confirm" ? "等待确认" : run ? "控温中" : "待机");
            if (lv_obj_t* d = u.box(k + ".dot")) if (u.changed(k + ".dot", run)) bg(d, run ? 0x00B368 : 0xB8C4D0, 255);
        }
        if (cur == "Main") { refresh_main(u); return; }
        if (cur == "Files") { refresh_lib(u); return; }
        if (cur == "Comm") { refresh_comm(u); return; }
        if (cur == "Sys") { refresh_sys(u); return; }
        if (cur == "Scheme") { refresh_scheme(u); return; }
        if (cur == "Trends") { trend_fetch(0); trend_fetch(1); draw_overview_trend(u.box("tv.chart")); return; }
        const json& c = st["ch"][channel - 1];
        const bool run = c.value("running", false);
        const json& q = c["seq"];
        const std::string qs = q.value("state", "idle"), mode = c.value("mode", "off");
        const bool seq_on = qs == "running" || qs == "waiting_confirm";
        // 通道标题栏
        u.set_text("head.ch", "通道 " + std::to_string(channel));
        u.set_text("head.name", c["run"].is_string() ? c["run"].get<std::string>() : "未在运行");
        const double duty = c.value("duty", 0.0);
        const char* mname = mode == "reactor" ? "釜内控温 Tr" : mode == "jacket" ? "夹套控温 Tj" : mode == "reflux" ? "蒸回流" : mode == "crystal" ? "结晶" : "未控温";
        u.set_text("head.mode", std::string(seq_on ? "程序控制" : "手动控制") + " · " + (!run ? "待机" : duty < -1 ? "加热中" : duty > 1 ? "制冷中" : "保温中") + " · " + mname);
        u.set_text("head.step", seq_on ? "步骤 " + std::to_string(q.value("index", 0) + 1) + " / " + std::to_string(q.value("count", 0)) : "无序列");
        // 「本步剩余」：这一步有确定时长的（等待、蒸回流、结晶曲线、按时长到达）才算得出剩多少；
        // 「到温度为止」「到 pH 为止」这类谁也说不准要多久，就老老实实写已经走了多久
        {
            const double el = q.value("step_elapsed_s", 0.0);
            double total = -1;
            const int idx = q.value("index", 0);
            const int cix = channel - 1;
            if (seq_on && seq_steps[cix].is_array() && idx >= 0 && idx < static_cast<int>(seq_steps[cix].size())) {
                const json& sx = seq_steps[cix][idx];
                const std::string kd = sx.value("kind", "");
                if (kd == "wait" || kd == "reflux") total = sx.value("minutes", 0.0) * 60.0;
                else if (kd == "crystal" && sx["profile"].is_array()) { total = 0; for (auto& g : sx["profile"]) total += g.value("minutes", 0.0) * 60.0; }
                else if ((kd == "reactor" || kd == "jacket") && sx.value("arrive", "fast") == "duration") total = sx.value("duration_min", 0.0) * 60.0;
            }
            u.set_text("head.remain.l", !seq_on || total > 0 ? "本步剩余" : "本步已走");
            u.set_text("head.remain", !seq_on ? "—" : total > 0 ? hms(std::max(0.0, total - el)) : hms(el));
        }
        u.show("head.end", seq_on);
        // 步骤进度条：有序列在跑才显示；步骤的名字要另外问一次，序列换了才重问
        const int ci = channel - 1;
        const std::string key = qs + "/" + std::to_string(q.value("count", 0));
        if (seq_on && key != seq_key[ci]) { const json g = ask({{"cmd", "seq.get"}, {"ch", channel}}); if (g.value("ok", false)) { seq_steps[ci] = g["steps"]; seq_key[ci] = key; } }
        if (!seq_on) seq_key[ci].clear();
        for (int i = 0; i < 6; i++) {
            const std::string id = "seg." + std::to_string(i);
            lv_obj_t* box = u.box(id);
            if (!box) continue;
            const bool vis = seq_on && i < q.value("count", 0);
            u.show(id, vis);
            if (!vis) continue;
            const int idx = q.value("index", 0);
            const double fr = i < idx ? 1.0 : i == idx ? q.value("step_progress", 0.0) : 0.0;
            if (lv_obj_t* bar = u.box(id + ".bar")) if (lv_obj_t* fill = u.box(id + ".fill")) lv_obj_set_width(fill, static_cast<int>(fr * lv_obj_get_width(bar)));
            u.set_text(id + ".t", std::to_string(i + 1) + " " + (seq_steps[ci].is_array() && i < static_cast<int>(seq_steps[ci].size()) ? step_label(seq_steps[ci][i]) : ""));
            u.set_text_color(id + ".t", i == idx ? 0x00559C : i < idx ? 0x8792A0 : 0xA9B4C0);
        }
        if (cur.rfind("Seq", 0) == 0) { refresh_seq(u, c, ci); return; }
        if (cur.rfind("ChTrend", 0) == 0) { refresh_trend(u, c, ci); return; }
        if (cur.rfind("Safety", 0) == 0) { refresh_safety(u, c, ci); return; }
        if (cur.rfind("Export", 0) == 0) { refresh_export(u); return; }
        if (cur.rfind("Pid", 0) == 0) { refresh_pid(u, c, ci); return; }
        if (run) { this->mode[ci] = mode == std::string("jacket") ? 1 : mode == std::string("reflux") ? 2 : mode == std::string("crystal") ? 3 : 0; pending_sp[ci] = c.value("sp", pending_sp[ci]); }
        set_radio(u, "mode", 4, this->mode[ci]);
        set_radio(u, "arrive", 3, arrive[ci]);
        // 设备控制页
        u.set_text("ctl.tr", c["tr"].is_number() ? f1(c["tr"].get<double>()) : "—");
        u.set_text("ctl.ph", c["ph"].is_number() ? f2(c["ph"].get<double>()) : "—");
        u.set_text("ctl.sp", run ? f1(c.value("sp", 0.0)) : "—");
        // 目标那一格跟着控温方式变：釜内控温填 Tr，夹套控温填 Tj，蒸回流填的是夹套比釜内高多少 K（这时没有「到达方式」）
        const int md = this->mode[ci];
        u.set_text("ctl.target.l", md == 1 ? "目标 Tj" : md == 2 ? "夹套比釜内高" : "目标 Tr");
        u.set_text("ctl.target.u", md == 2 ? "K" : "℃");
        u.set_text("ctl.target", md == 2 ? f1(reflux_dk[ci]) : f1(run ? c.value("sp", 0.0) : pending_sp[ci]));
        u.set_text("ctl.temp.status.sub", md == 1 ? "夹套控温 Tj" : md == 2 ? "蒸回流" : "釜内控温 Tr");
        u.show("ctl.arrive.box", md != 2);
        refresh_stir(u, c);
        if (c["tj"].is_number()) {
            std::string s = "Tj " + f1(c["tj"].get<double>()) + " ℃";
            if (c["tr"].is_number()) s += "　·　Tr − Tj " + f1(c["tr"].get<double>() - c["tj"].get<double>()) + " K";
            if (st["coolant_c"].is_number()) s += "　·　Tc " + f1(st["coolant_c"].get<double>()) + " ℃";
            if (run) s += "　·　夹套目标 " + f1(c.value("tj_sp", 0.0)) + " ℃　·　出力 " + f0(std::fabs(duty)) + " %（" + (duty > 0 ? "TEC 制冷" : "加热棒") + "）";
            u.set_text("ctl.temp.status", offline ? st.value("offline", std::string()) : !ctl_msg[ci].empty() ? ctl_msg[ci] : s);
        } else if (offline) u.set_text("ctl.temp.status", st.value("offline", std::string()));
        u.set_text_color("ctl.temp.status", offline || !ctl_msg[ci].empty() ? 0xD64545 : 0x8792A0);
        u.set_text("ctl.run.t1", run ? "控温中" : "未控温");
        u.set_text("ctl.run.t2", run ? "点按停止" : "点按启动");
        u.set_text_color("ctl.run.t1", run ? 0x0A8A4F : 0x5A6675);
        if (lv_obj_t* o = u.box("ctl.run.circle")) if (u.changed("ctl.run.circle", run)) { bg(o, run ? 0x00B368 : 0xFFFFFF, 255); bd(o, run ? 0x00B368 : 0xC3CCD6, 255, 3 * UI_SCALE, LV_BORDER_SIDE_FULL); ol(o, 0x00B368, run ? 46 : 0, 5 * UI_SCALE); }
        refresh_cry(c, ci);
    }
};

int main(int argc, char** argv) {
    // 字体和图片按「当前目录下的 assets」找。从别的目录启动（比如开机自启、双击快捷方式）时当前目录不对，
    // 界面会一个字都没有，所以这里先挪到程序自己旁边有 assets 的那一层。
    {
        namespace fs = std::filesystem;
        std::error_code ec;
        fs::path d = fs::absolute(argv[0], ec).parent_path();
        for (int i = 0; i < 3 && !fs::exists("assets/fonts", ec); i++, d = d.parent_path())
            if (fs::exists(d / "assets" / "fonts", ec)) fs::current_path(d, ec);
    }
    App app;
    std::string page = "Ctl", shot;
    std::vector<std::string> todo;             // --do 动作名：开发时自查用，代替手指点一下
    std::vector<std::pair<int, int>> taps;     // --tap x,y：模拟手指在那个位置点一下（不开窗口时用）
    int zoom = 100;                            // --zoom：Windows 上整个窗口放大多少（百分比）
    bool fullscreen = false;                   // --fullscreen：Windows 上去掉边框铺满屏幕
    double after = 2.0;
    for (int i = 1; i < argc; i++) {
        const std::string a = argv[i];
        if (a == "--host" && i + 1 < argc) app.host = argv[++i];
        else if (a == "--port" && i + 1 < argc) app.port = std::atoi(argv[++i]);
        else if (a == "--page" && i + 1 < argc) page = argv[++i];
        else if (a == "--shot" && i + 1 < argc) shot = argv[++i];
        else if (a == "--after" && i + 1 < argc) after = std::atof(argv[++i]);
        else if (a == "--channel" && i + 1 < argc) app.channel = std::atoi(argv[++i]);
        else if (a == "--do" && i + 1 < argc) todo.push_back(argv[++i]);
        else if (a == "--tap" && i + 1 < argc) { int x = 0, y = 0; if (std::sscanf(argv[++i], "%d,%d", &x, &y) == 2) taps.push_back({x, y}); }
        else if (a == "--zoom" && i + 1 < argc) zoom = std::atoi(argv[++i]);
        else if (a == "--fullscreen") fullscreen = true;
    }
    lv_init();
    // LVGL 的钟用系统的单调钟：动画、长按判定都按真实时间走，不管一圈循环实际花了多久
    lv_tick_set_cb([]() -> uint32_t { return static_cast<uint32_t>(std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now().time_since_epoch()).count()); });
    using clock = std::chrono::steady_clock;
    const auto t0 = clock::now();
    auto last_poll = t0 - std::chrono::seconds(1);

#ifdef _WIN32
    // ── Windows 上真正开窗口：LVGL 自带的 Windows 驱动画窗口、收鼠标和触摸。带 --shot 时仍走下面不开窗口的那一套（自查出图用） ──
    if (shot.empty()) {
        lv_display_t* d = lv_windows_create_display(L"HTLAB 多工位反应平台", W, H, zoom, false, false);
        if (!d) { std::fprintf(stderr, "窗口开不出来\n"); return 1; }
        lv_windows_acquire_pointer_indev(d);
        if (fullscreen) {
            // 现场用：去掉标题栏和边框，铺满主屏幕（屏幕要是 1280×720，或者用 --zoom 按比例放大到屏幕那么大）
            HWND hwnd = lv_windows_get_display_window_handle(d);
            SetWindowLongPtrW(hwnd, GWL_STYLE, WS_POPUP | WS_VISIBLE);
            SetWindowPos(hwnd, HWND_TOP, 0, 0, GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN), SWP_FRAMECHANGED | SWP_SHOWWINDOW);
        }
        // 窗口有它自己的一条线程在收消息；我们在主线程里改界面时把 LVGL 的那把锁拿上，免得两边同时动
        lv_lock(); app.show(page); lv_unlock();
        const HWND win = lv_windows_get_display_window_handle(d);      // 先记下窗口：窗口关掉以后 LVGL 会把 d 一起销毁，那时不能再拿 d 去问
        for (;;) {
            const auto now = clock::now();
            if (now - last_poll >= std::chrono::milliseconds(500)) { last_poll = now; lv_lock(); app.poll(); lv_unlock(); }
            const uint32_t wait = lv_timer_handler();           // 点击之类的事件是在这里面分发的（它自己会拿那把锁）
            if (!IsWindow(win)) return 0;      // 窗口被关掉了
            Sleep(wait < 1 ? 1 : wait > 10 ? 10 : wait);
        }
    }
#else
    (void)zoom; (void)fullscreen;
#endif

    // ── 不开窗口：画到内存里，可以模拟手指点、可以把画面存出来。这台 Mac 上开发和自查用的就是这一套 ──
    lv_display_t* d = lv_display_create(W, H);
    static uint32_t buf[W * H];
    lv_display_set_buffers(d, buf, nullptr, sizeof buf, LV_DISPLAY_RENDER_MODE_PARTIAL);
    lv_display_set_flush_cb(d, flush);
    // 假的手指：--tap x,y（设计稿上的坐标，1280×720 那一套）走的是和真触摸一样的路——LVGL 自己找点中了谁、判是不是一次点击
    static struct { int x = 0, y = 0; bool down = false; } finger;
    lv_indev_t* touch = lv_indev_create();
    lv_indev_set_type(touch, LV_INDEV_TYPE_POINTER);
    lv_indev_set_read_cb(touch, [](lv_indev_t*, lv_indev_data_t* data) { data->point.x = finger.x; data->point.y = finger.y; data->state = finger.down ? LV_INDEV_STATE_PRESSED : LV_INDEV_STATE_RELEASED; });
    app.show(page);
    int frames = 0, tap_phase = 0;
    for (;;) {
        const auto now = clock::now();
        if (now - last_poll >= std::chrono::milliseconds(500)) { last_poll = now; app.poll(); }
        lv_timer_handler();
        std::this_thread::sleep_for(std::chrono::milliseconds(10));
        // 第一帧要现算所有字形，可能一画就是两三秒；所以「点完」和「出图」之间按画过的帧数隔开，不按钟点，
        // 不然点完还没来得及重画就把旧画面存出去了
        frames++;
        if (frames >= 20 && tap_phase == 0) {
            if (!todo.empty()) { for (auto& a : todo) app.action(a); todo.clear(); frames = 20; }
            else if (!taps.empty()) { finger.x = taps.front().first * UI_SCALE; finger.y = taps.front().second * UI_SCALE; finger.down = true; tap_phase = 1; }
        } else if (tap_phase > 0) {
            // 按住十来帧（一百多毫秒）再抬起，隔十来帧再点下一个：和人点一下差不多
            tap_phase++;
            if (tap_phase == 14) finger.down = false;
            if (tap_phase >= 30) { taps.erase(taps.begin()); tap_phase = 0; frames = 20; }
        }
        if (!shot.empty() && todo.empty() && taps.empty() && frames >= 60 && std::chrono::duration<double>(now - t0).count() >= after) {
            if (std::FILE* f = std::fopen(shot.c_str(), "wb")) { std::fwrite(g_fb, 4, static_cast<size_t>(W) * H, f); std::fclose(f); }
            return 0;
        }
    }
}
