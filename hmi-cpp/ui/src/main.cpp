// 触摸屏界面程序。它自己不碰设备，只通过本机 TCP 连控制核心（tec_core）：每半秒问一次状态刷到屏上，点了什么就发一条命令过去。
//   tec_hmi [--host 127.0.0.1] [--port 5020] [--page 页面名] [--shot 文件 --after 秒]
//   --shot：不开窗口，跑够时间把屏幕存成一张图就退出（开发时自查用）
//   --lint：同一时刻把这一页的排版量一遍（字有没有被裁、有没有压到别的字或控件），见 ui/src/lint.inc
//   --fit 表：提示句和报警句平时屏上没有，出图量不到——把表里每一句摆进它该去的位置量一遍
#include "../../net/tcp_server.hpp"
#include "../../third_party/nlohmann/json.hpp"
#include "ui_kit.hpp"

// ── 编的时候先卡两道，免得到现场才发现 ──
// 曲线（趋势、结晶曲线、方案总览）是用 LVGL 的线画的，斜线归「复杂渲染」管。关掉的话圆角、
// 阴影照样有（那是另一条路），斜线却只剩两头的圆点——现场 2026-11-27 看到的「曲线都是点」就是这个样子
static_assert(LV_DRAW_SW_COMPLEX == 1, "LVGL 的复杂渲染被关了：曲线会只剩点。打开 ui/lv_conf.h 里的 LV_DRAW_SW_COMPLEX");
// LVGL 不在仓库里（各自按 README 下 9.2.2 的源码包）。版本不一样，画线这些地方的行为就可能不一样，
// 与其在现场猜，不如编的时候就说出来
static_assert(LVGL_VERSION_MAJOR == 9 && LVGL_VERSION_MINOR == 2, "third_party 里的 LVGL 不是 9.2.x：按 README 下 9.2.2，别的版本没验过");
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
// 手指（或鼠标）现在在哪、按没按着。Windows 上是窗口消息在写，不开窗口时是 --tap 在写；LVGL 每一帧来读
static struct { int x = 0, y = 0; bool down = false; } g_finger;
#ifdef _WIN32
#if !defined(TEC_HMI_LIB)              // 编成库给 PC 工作站用时不开窗口，这一套用不上
#include "win_window.inc"
#define TEC_HAS_WINDOW 1
#endif
#endif
#ifdef _WIN32
// 弹个框说句中文。Windows 上要先把 UTF-8 转成宽字符：按窄字符那套走会被当成系统本地编码，显示成乱码
#if !defined(TEC_HMI_LIB)
static void win_message_box(const std::string& text, const std::string& title) {
    auto wide = [](const std::string& u8) {
        const int n = MultiByteToWideChar(CP_UTF8, 0, u8.c_str(), -1, nullptr, 0);
        std::wstring w(n > 0 ? n - 1 : 0, L'\0');
        if (n > 0) MultiByteToWideChar(CP_UTF8, 0, u8.c_str(), -1, w.data(), n);
        return w;
    };
    MessageBoxW(nullptr, wide(text).c_str(), wide(title).c_str(), MB_ICONERROR | MB_OK);
}
#endif
#endif

static bool g_dirty = false;         // 上次宿主取走画面之后，有没有重新画过
static void flush(lv_display_t* d, const lv_area_t* a, uint8_t* px) {
    g_dirty = true;
    const int w = a->x2 - a->x1 + 1;
    uint32_t* out = g_fb;
#ifdef TEC_HAS_WINDOW
    if (win::bits) out = win::bits;        // 开着窗口：直接写到窗口的那张画布上
#endif
    for (int y = a->y1; y <= a->y2; y++) std::memcpy(&out[y * W + a->x1], px + static_cast<size_t>(y - a->y1) * w * 4, static_cast<size_t>(w) * 4);
#ifdef TEC_HAS_WINDOW
    win::present(a->x1, a->y1, a->x2, a->y2);      // 只把变了的这一块贴上去
#endif
    lv_display_flush_ready(d);
}

static std::string f1(double v) { char b[32]; std::snprintf(b, sizeof b, "%.1f", v); std::string s = b; if (s == "-0.0") s = "0.0";      /* 四舍五入到 0 的小负数不带负号 */ if (s[0] == '-') s.replace(0, 1, "−"); return s; }
static std::string f2(double v) { char b[32]; std::snprintf(b, sizeof b, "%.2f", v); return b; }
static std::string f0(double v) { char b[32]; std::snprintf(b, sizeof b, "%.0f", v); std::string s = b; if (s == "-0") s = "0"; if (s[0] == '-') s.replace(0, 1, "−"); return s; }      // 负号用正经的减号，和一位小数的那个一致
static std::string hms(double s) { const long t = static_cast<long>(s < 0 ? 0 : s); char b[32]; std::snprintf(b, sizeof b, "%02ld:%02ld:%02ld", t / 3600, t / 60 % 60, t % 60); return b; }

// 控制核心给的话（拒绝命令的原因、设备连不上的原因、报警那一句）：核心交的是两件——
// 填好的中文（记录和 PC 端照用），和「原句 + 值」（error_t / error_args 这一对）。
// 界面按原句去查词表，再把值填回去，这样八种语言都对；老核心只给填好的中文，就原样显示。
// 值也过一道词表：有的值本身是话（设备名、「应答」这种），数查不到就原样回来。
static std::string core_text(const json& o, const char* base, const std::string& fallback = "", const char* args_key = nullptr) {
    const std::string tkey = std::string(base) + "_t";
    const std::string t = o.value(tkey, std::string());
    if (!t.empty()) {
        std::string out = ui::tr(t);
        const std::string akey = args_key ? args_key : std::string(base) + "_args";
        if (o.contains(akey) && o[akey].is_array()) {
            int i = 0;
            for (const auto& a : o[akey]) {
                const std::string v = ui::tr(a.is_string() ? a.get<std::string>() : std::string());
                const std::string mark = "{" + std::to_string(i++) + "}";
                for (size_t q = out.find(mark); q != std::string::npos; q = out.find(mark, q + v.size()))
                    out.replace(q, mark.size(), v);
            }
        }
        return out;
    }
    const std::string plain = o.value(base, std::string());
    return ui::tr(plain.empty() ? fallback : plain);
}

#include "pinyin_dict.inc"
#include "phrase_dict.inc"

#include "lint.inc"

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
            for (const PageDef& d : kPages) if (std::string(d.name) == "KeypadDlg") { d.build(kp.scr, *kp.ui); lv_obj_update_layout(kp.scr); }
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
        // 单位紧跟着数字走。「数字变宽就把后面那段往右让」这件事生成的代码里已经串上了（见 gen.py 的
        // txta_shift / reg_follow），但那套只往右让、不往回收：键盘上这个数从「0」到「−188.88」宽度差一倍多，
        // 短的时候单位就孤零零停在远处。所以这儿每次都按当前这个数的宽度重摆一遍——
        // 先把那一段自己让出去的距离清掉（它刚被上面的 set_text 挪过），再整体挪装它的那个小框
        if (lv_obj_t* ub = u.box("kp.unit"))
            if (lv_obj_t* vb = u.box("kp.value"))
                if (lv_obj_t* vl = u.label("kp.value")) {
                    if (lv_obj_t* ul = u.label("kp.unit")) lv_obj_set_x(ul, 0);
                    lv_obj_set_x(ub, lv_obj_get_x(vb) + label_w(vl) + 8 * UI_SCALE);
                }
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
#include "lang_page.inc"
#include "vessel_fx.inc"

    // 弹窗和满屏那几页不在 pages 那张表里，各自是一个 Page。换语言时要连它们一起拆掉重搭
    std::vector<Page*> aux_pages() { return {&kp, &tkb, &nolink, &commdlg, &exitdlg, &stepdlg, &aldlg, &allist, &langdlg}; }

    json ask(const json& q) {
        // 不等的连接：机器关着 / 地址填错时，这里立刻回来，界面照常刷新——
        // 界面嵌在 PC 工作站里跑，在这儿等就是整个工作站一卡一卡（用户 2026-11-24 踩到的那种卡）
        if (!connected) connected = link.connect_async(host, port);
        // 还在连：直接回「连不上」就走，**不能在这儿 close**——那会把正在进行的连接掐掉，于是永远连不上（踩过）
        if (!connected) return json{{"ok", false}, {"error", "连不上控制核心"}};
        std::string r;
        if (link.request(q.dump(), r, host == "127.0.0.1" ? 800 : 3000)) { const json j = json::parse(r, nullptr, false); if (!j.is_discarded()) return j; }
        connected = false; link.close();
        return json{{"ok", false}, {"error", "连不上控制核心"}};
    }

    // 把一页搭出来（只搭、不显示）。已经搭过就什么都不做。返回 false = 没有这一页
    bool ensure(const std::string& name) {
        Page& p = pages[name];
        if (p.scr) return true;
        for (const PageDef& d : kPages) if (name == d.name) {
            p.scr = lv_obj_create(nullptr);
            lv_obj_remove_style_all(p.scr);
            lv_obj_remove_flag(p.scr, LV_OBJ_FLAG_SCROLLABLE);
            p.ui = std::make_unique<Ui>();
            p.ui->on_action = [this](const std::string& a) { action(a); };
            { d.build(p.scr, *p.ui); lv_obj_update_layout(p.scr); }
            // 釜上的外圈和搅拌桨是两张贴在釜体照片上的图，页面搭好就贴上（见 vessel_fx.inc）
            attach_fx(*p.ui, "main.1.vessel", "m", "main.1.badge");
            attach_fx(*p.ui, "main.2.vessel", "m", "main.2.badge");
            attach_fx(*p.ui, "ctl.vessel", "c", "ctl.badge");
        }
        if (!p.scr) { pages.erase(name); return false; }      // 这一页还没做
        return true;
    }

    // ── 提前准备 ──
    // 用户 2026-11-24 上机：「每个菜单第一次进去的时候超级慢」。大头是字体引擎查字距表太慢（已经在 ui_kit.cpp 的 font() 里关掉，搭一页从一秒降到几十毫秒）；
    // 剩下的是第一次画一页时要现算这一页上的字形、现解这一页上的图标图片，算过解过的留在缓存里。这里把这点余量也拿掉：第一页出来以后，趁没人碰屏幕的空当，
    // 一页一页地在内存里先画一遍（不上屏），把字形和图片都备好。一次只备一页，手指按着的时候不备，免得挡住操作。
    size_t warm_next = 0;
    bool warm_step() {
        static const size_t n = sizeof kPages / sizeof kPages[0];
        if (warm_next >= n) return false;
        const PageDef& d = kPages[warm_next++];
        const std::string name = d.name;
        const bool piece = name.size() > 4 && name.compare(name.size() - 4, 4, "Part") == 0, dlg = name.size() > 3 && name.compare(name.size() - 3, 3, "Dlg") == 0;
        lv_obj_t* scr = nullptr;
        std::unique_ptr<Ui> tmp_ui;
        if (piece || dlg) {
            // 弹窗和「一页里的一块」平时是用到才搭在别的东西上面的：这里搭一份临时的画一遍就扔，要的只是它带出来的字形和图片
            scr = lv_obj_create(nullptr);
            lv_obj_remove_style_all(scr);
            tmp_ui = std::make_unique<Ui>();
            { d.build(scr, *tmp_ui); lv_obj_update_layout(scr); }
        } else {
            if (name == cur || !ensure(name)) return true;
            scr = pages[name].scr;
        }
        lv_obj_set_size(scr, W, H);
        if (lv_draw_buf_t* shot = lv_snapshot_take(scr, LV_COLOR_FORMAT_XRGB8888)) lv_draw_buf_destroy(shot);
        if (piece || dlg) lv_obj_delete(scr);
        return true;
    }

    // ── 没连上控制核心时的那一屏 ──
    // 整屏换成设计稿里的 NoLink 那一页（用户 2026-11-25 从三个方案里选的「丙」：深色满屏，现场隔几米也看得见）。
    // 不在页面上蒙白纱——底下是设计稿的样例数字（65.0、结晶工艺-0824），看着像真的，容易误读。
    Page nolink;
    bool ever_linked = false;      // 连上过没有：决定说「还没连上」还是「断开了」
    int nolink_tick = 0;

    void nolink_show() {
        if (!nolink.scr) {
            for (const PageDef& d : kPages) if (std::string(d.name) == "NoLink") {
                nolink.scr = lv_obj_create(nullptr);
                lv_obj_remove_style_all(nolink.scr);
                lv_obj_remove_flag(nolink.scr, LV_OBJ_FLAG_SCROLLABLE);
                nolink.ui = std::make_unique<Ui>();
                d.build(nolink.scr, *nolink.ui);
            }
            if (!nolink.scr) return;
        }
        if (lv_screen_active() != nolink.scr) lv_screen_load(nolink.scr);
        Ui& u = *nolink.ui;
        // 一直没连上，和跑着跑着掉线，是两回事，话要说得不一样
        u.set_text("nl.title", ever_linked ? "和控制核心断开了" : "未连上控制核心");
        u.set_text("nl.where", host + " : " + std::to_string(port));
        // 地址长短不一：装它的小条跟着宽窄，并保持居中
        if (lv_obj_t* chip = u.box("nl.chip")) if (lv_obj_t* lb = u.label("nl.where")) {
            const int w = label_w(lb) + 40 * UI_SCALE;
            if (lv_obj_get_width(chip) != w) { lv_obj_set_width(chip, w); lv_obj_set_x(chip, (W - w) / 2); }
        }
        if (++nolink_tick % 4 == 0) {      // 每两秒多一个点，转一圈：看得出程序还活着
            static const char* d[] = {"正在自动重试", "正在自动重试 ·", "正在自动重试 · ·", "正在自动重试 · · ·"};
            u.set_text("nl.retry", d[(nolink_tick / 4) % 4]);
        }
    }

    /// 连上了：回到原来那一页
    void nolink_hide() {
        ever_linked = true;
        if (!nolink.scr || lv_screen_active() != nolink.scr) return;
        auto it = pages.find(cur);
        if (it != pages.end() && it->second.scr) lv_screen_load(it->second.scr);
    }

    void show(const std::string& name) {
        if (!ensure(name)) return;
        Page& p = pages[name];
        cur = name;
        lv_screen_load(p.scr);
        refresh();
    }

    std::string channel_page() const {
        static const char* tabs[] = {"Ctl", "ChTrend", "Seq", "Safety", "Export", "Pid"};
        return std::string(tabs[ch_tab]) + (channel == 2 ? "_2" : "");
    }

    // 控温启停圆键右边的两行字（控温中 / 点按停止）：换成英文变宽时整颗键往左让，右边贴齐卡片边、左边不撞圆圈
    void fit_run_btn(Ui& u) {
        lv_obj_t *cir = u.box("ctl.run.circle"), *l1 = u.label("ctl.run.t1"), *l2 = u.label("ctl.run.t2");
        if (!cir || !l1 || !l2) return;
        lv_obj_t* btn = lv_obj_get_parent(cir);
        lv_obj_t* wrap = btn ? lv_obj_get_parent(btn) : nullptr;
        if (!btn || !wrap) return;
        const int need_w = std::max(131 * UI_SCALE, 76 * UI_SCALE + std::max(label_w(l1), label_w(l2)));
        const int right = lv_obj_get_width(wrap), bx = right - need_w;
        lv_obj_set_x(btn, bx);
        lv_obj_set_width(btn, need_w);
        if (lv_obj_t* ed = u.box("cv.edit")) lv_obj_set_x(ed, bx - 16 * UI_SCALE - lv_obj_get_width(ed));
    }

    static std::string step_label(const json& s) {
        const std::string k = s.value("kind", "");
        if (k == "reactor") return "Tr " + f1(s.value("target", 0.0));
        if (k == "jacket") return "Tj " + f1(s.value("target", 0.0));
        if (k == "reflux") return ui::tr("回流");
        if (k == "crystal") return ui::tr("结晶");
        if (k == "stir") return "R " + f0(s.value("stir_rpm", 0.0));
        if (k == "wait") return "Wait";
        if (k == "dose") return ui::tr("加料");
        return ui::tr("暂停");
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
        if (lang_action(a)) return;         // 顶栏那颗地球、设置页「语言」那一行
        if (sys_action(a)) return;          // 排在通信设置前面：顶栏「设置」先进系统设置这一页
        if (comm_action(a)) return;
        if (overview_action(a)) return;
        if (alarm_action(a)) return;
        if (a == "edit:target" && st.is_object()) {
            const json& c = st["ch"][ci];
            const bool run = c.value("running", false);
            const double now_sp = run ? c.value("sp", 25.0) : pending_sp[ci];
            if (mode[ci] == 2) {
                // 蒸回流填的不是目标温度，是「夹套比釜内高多少」。已经在回流时核心不收改动，先停再改
                if (run) { ctl_msg[ci] = "蒸回流进行中改不了温差：先停下，改好再开"; refresh(); return; }
                open_keypad(ui::fmt("通道 {0} · 蒸回流：夹套比釜内高", {std::to_string(channel)}), "K", reflux_dk[ci], 1, 30, f1(reflux_dk[ci]) + " K",
                            c["tr"].is_number() ? f1(c.value("tj", 0.0) - c["tr"].get<double>()) + " K" : "—", [this, ci](double v) { reflux_dk[ci] = v; refresh(); }, 1);
                return;
            }
            const bool jk = mode[ci] == 1;       // 夹套控温：目标是夹套温度，上限按夹套的 200 ℃
            const json& nowv = jk ? c["tj"] : c["tr"];
            open_keypad(ui::fmt(jk ? "通道 {0} · 目标 Tj" : "通道 {0} · 目标 Tr", {std::to_string(channel)}), "℃", now_sp, -40, jk ? 200 : 180, f1(now_sp) + " ℃", nowv.is_number() ? f1(nowv.get<double>()) + " ℃" : "—",
                        [this, ci, run](double v) { pending_sp[ci] = v; if (run) ask({{"cmd", "temp.setpoint"}, {"ch", ci + 1}, {"target", v}}); poll(); });
            return;
        }
        if (a == "edit:stir" && st.is_object()) {
            const json& c = st["ch"][ci];
            open_keypad(ui::fmt("通道 {0} · 搅拌转速", {std::to_string(channel)}), "rpm", c["stir"].value("set", 0.0), 0, 1000, f0(c["stir"].value("set", 0.0)) + " rpm", f0(c["stir"].value("rpm", 0.0)) + " rpm",
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
        // 下面这几个动作都要读最近一次的状态。还没连上核心时屏上盖着「未连上控制核心」那一层，手指点不到，
        // 但 --do 这种直接喊动作名的绕过了那一层：没状态就什么也不做，别拿空的去取值（会当场崩）
        if ((a == "temp.toggle" || a == "stir.toggle" || a == "lamp.toggle" || a == "seq.end") && (!st.is_object() || !st.contains("ch"))) return;
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
                cry_msg[ci] = mode[ci] == 3 && !r.value("ok", false) ? core_text(r, "error", "开始不了") : "";
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

    int poll_fail = 0;      // 连着几次没要到状态
    bool lang_loaded = false;      // 上次用的那种语言捡回来了没有（要先连上核心才问得到）

    void poll() {
        const json j = ask({{"cmd", "state"}});
        if (j.value("ok", false)) { st = j; poll_fail = 0; }
        // 控制核心掉了（程序退了、网线拔了）：屏上不能一直挂着最后那组读数装作还在跑。
        // 连丢三次（一秒半）就把状态清掉，自动切到「未连上控制核心」那一屏（用户 2026-11-25：核心掉了不会提醒）
        else if (++poll_fail >= 3) st = json();
        // 连上以后问一次：上次用的是哪种语言。只问一次，换语言时自己会存
        if (!lang_loaded && connected) {
            const json cfg = ask({{"cmd", "settings.get"}});
            if (cfg.value("ok", false)) { lang_loaded = true; lang_pick_up(cfg); }
        }
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
        // 控温设备没连上时核心给的温度是没读过的 0，不是读数：底下那六格里跟温度有关的一律空着
        const bool offline = !st.value("offline", std::string()).empty();
        for (int n = 1; n <= 2; n++) {
            const json& c = st["ch"][n - 1];
            const std::string k = "main." + std::to_string(n);
            const bool run = c.value("running", false);
            const json& q = c["seq"];
            const std::string qs = q.value("state", "idle"), cmode = c.value("mode", "off");
            const bool seq_on = qs == "running" || qs == "waiting_confirm";
            const bool live = run || seq_on;
            const double duty = c.value("duty", 0.0);
            if (lv_obj_t* d = u.box(k + ".dot")) if (u.changed(k + ".dot", live)) bg(d, live ? 0x00B368 : 0xBFC7CF, 255);
            vessel_state(u, k + ".vessel", k + ".badge", c, n, offline);
            const std::string name = c.contains("run") && c["run"].is_string() ? c["run"].get<std::string>() : "";      // 没在跑序列时核心给的是空值，不是空字符串
            u.set_text(k + ".name", !name.empty() ? name : live ? "手动运行" : "未在运行");
            u.set_text(k + ".r1", seq_on ? hms(q.value("total_elapsed_s", 0.0)) : run ? "控温中" : "待机");
            const char* mname = cmode == "reactor" ? "釜内控温 Tr" : cmode == "jacket" ? "夹套控温 Tj" : cmode == "reflux" ? "蒸回流" : cmode == "crystal" ? "结晶" : "未控温";
            u.set_text_raw(k + ".mode", ui::fmt("{0} · {1}", {ui::tr(seq_on ? "程序模式" : "手动模式"), ui::tr(mname)}));
            u.set_text_raw(k + ".r2", qs == "waiting_confirm" ? ui::tr("等待确认后继续")
                                       : seq_on ? ui::fmt("步骤 {0} / {1}", {std::to_string(q.value("index", 0) + 1), std::to_string(q.value("count", 0))})
                                       : !run ? "" : ui::tr(duty < -1 ? "加热中" : duty > 1 ? "制冷中" : "保温中"));
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
            // 搅拌读不到（没接、串口不通）就直说。写「关」会让人以为点一下就能开，核心那边其实是拒绝的
            const bool stir_ok = c["stir"].value("ok", false);
            u.set_text(k + ".stir", !stir_ok ? "未连接" : set > 0 ? f0(rpm) + " rpm" : "关");
            set_ring(u, k + ".stir", k + ".stir", stir_ok && set > 0);
            const bool lamp = c.value("lamp", false);
            u.set_text(k + ".lamp", lamp ? "开" : "关");
            set_ring(u, k + ".lamp", k + ".lamp", lamp);
            u.set_text(k + ".t0", offline ? "—" : f1(tj));
            const double dt = tr - tj;
            u.set_text(k + ".t1", has_tr && !offline ? (dt >= 0 ? "+" : "") + f1(dt) : "—");
            u.set_text(k + ".t2", stir_ok ? f0(rpm) : "—");
            u.set_text(k + ".t3", f1(c["dose"].value("dosed_ml", 0.0)));
            u.set_text(k + ".t4", st["coolant_c"].is_number() ? f1(st["coolant_c"].get<double>()) : "—");      // 冷媒温度 = 冷水机的冷却水温度（两个通道共用一台冷水机）；没读到就空着
            const double rt = rate_of(n - 1, st.value("t", 0.0), tr);
            u.set_text(k + ".t5", offline ? "—" : (rt >= 0 ? "+" : "") + f1(rt));
        }
    }

    // 搅拌卡上的三处读数。设备控制页和结晶查看那一块各有一张搅拌卡，两处都用它
    void refresh_stir(Ui& u, const json& c) {
        const double stir_set = c["stir"].value("set", 0.0), rpm = c["stir"].value("rpm", 0.0);
        const bool stir_ok = c["stir"].value("ok", false);      // 搅拌读得到读数没有；读不到时这张卡上的数都不算数
        u.set_text("ctl.stir_set", f0(stir_set));
        u.set_text("ctl.stir.val", !stir_ok ? "未连接" : stir_set > 0 ? f0(rpm) + " rpm" : "关");
        set_ring(u, "ctl.stir", "ctl.stir.val", stir_ok && stir_set > 0);
        const bool lamp = c.value("lamp", false);
        u.set_text("ctl.lamp.val", lamp ? "开" : "关");
        set_ring(u, "ctl.lamp", "ctl.lamp.val", lamp);
        u.set_text_raw("ctl.stir.status", stir_ok ? ui::fmt("实测 {0} rpm　·　电流 {1} A", {f0(rpm), f2(c["stir"].value("current_a", 0.0))}) : ui::tr("搅拌没有连上：在设置 → 通信里配好搅拌电机的串口"));
        u.set_text_raw("ctl.stir.status2", stir_ok ? ui::fmt("桨式 · 50–1000 rpm　·　实测 {0} rpm · 电流 {1} A", {f0(rpm), f2(c["stir"].value("current_a", 0.0))}) : ui::tr("桨式 · 50–1000 rpm　·　搅拌没有连上"));      // 结晶那一块里的搅拌卡矮一些，读数并在标题那一行
        u.set_text_color("ctl.stir.status", stir_ok ? 0x8792A0 : 0xD64545);
        u.set_text_color("ctl.stir.status2", stir_ok ? 0x8792A0 : 0xD64545);
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
        // 还没拿到过状态：页面上那些数字是设计稿里的样例值（65.0、结晶工艺-0824 之类），不是真的。
        // 盖一层半透明的白，一眼看出「这不是实时数据」——顶栏那句红字容易被忽略，而面板在 PC 上是看远处机器的，
        // 把样例值当读数会出事
        if (!st.is_object() || !st.contains("ch")) { nolink_show(); return; }
        nolink_hide();
        // 控温设备没连上（串口配错之类）：核心还活着但不能控温。顶栏这个位置平时写冷却水，这时换成红点和这句话，哪一页都看得见
        const bool offline = !st.value("offline", std::string()).empty();
        u.set_text("top.water", offline ? "设备未连接" : st.value("valve_open", false) ? "冷却水已开" : "冷却水已关");
        u.show("top.water.dot", true);
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
            u.set_text_raw(k + ".state", qs == "running" ? ui::fmt("运行中 · {0} / {1}", {std::to_string(q.value("index", 0) + 1), std::to_string(q.value("count", 0))})
                                          : ui::tr(qs == "waiting_confirm" ? "等待确认" : run ? "控温中" : "待机"));
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
        const std::string qs = q.value("state", "idle"), cmode = c.value("mode", "off");
        const bool seq_on = qs == "running" || qs == "waiting_confirm";
        // 通道标题栏
        u.set_text_raw("head.ch", ui::fmt("通道 {0}", {std::to_string(channel)}));
        u.set_text("head.name", c["run"].is_string() ? c["run"].get<std::string>() : "未在运行");
        const double duty = c.value("duty", 0.0);
        const char* mname = cmode == "reactor" ? "釜内控温 Tr" : cmode == "jacket" ? "夹套控温 Tj" : cmode == "reflux" ? "蒸回流" : cmode == "crystal" ? "结晶" : "未控温";
        u.set_text_raw("head.mode", ui::fmt("{0} · {1} · {2}", {ui::tr(seq_on ? "程序控制" : "手动控制"),
                                               ui::tr(!run ? "待机" : duty < -1 ? "加热中" : duty > 1 ? "制冷中" : "保温中"), ui::tr(mname)}));
        u.set_text_raw("head.step", seq_on ? ui::fmt("步骤 {0} / {1}", {std::to_string(q.value("index", 0) + 1), std::to_string(q.value("count", 0))}) : ui::tr("无序列"));
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
        if (run) { this->mode[ci] = cmode == std::string("jacket") ? 1 : cmode == std::string("reflux") ? 2 : cmode == std::string("crystal") ? 3 : 0; pending_sp[ci] = c.value("sp", pending_sp[ci]); }
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
        vessel_state(u, "ctl.vessel", "ctl.badge", c, channel, offline);
        if (c["tj"].is_number()) {
            std::string s = "Tj " + f1(c["tj"].get<double>()) + " ℃";
            if (c["tr"].is_number()) s += "　·　Tr − Tj " + f1(c["tr"].get<double>() - c["tj"].get<double>()) + " K";
            if (st["coolant_c"].is_number()) s += "　·　Tc " + f1(st["coolant_c"].get<double>()) + " ℃";
            if (run) s += ui::fmt("　·　夹套目标 {0} ℃　·　出力 {1} %（{2}）", {f1(c.value("tj_sp", 0.0)), f0(std::fabs(duty)), ui::tr(duty > 0 ? "TEC 制冷" : "加热棒")});
            u.set_text_raw("ctl.temp.status", offline ? ui::tr(st.value("offline", std::string())) : !ctl_msg[ci].empty() ? ui::tr(ctl_msg[ci]) : s);
        } else if (offline) u.set_text("ctl.temp.status", st.value("offline", std::string()));
        u.set_text_color("ctl.temp.status", offline || !ctl_msg[ci].empty() ? 0xD64545 : 0x8792A0);
        u.set_text("ctl.run.t1", run ? "控温中" : "未控温");
        u.set_text("ctl.run.t2", run ? "点按停止" : "点按启动");
        u.set_text_color("ctl.run.t1", run ? 0x0A8A4F : 0x5A6675);
        fit_run_btn(u);
        // 圆键里的图标是一张白色的图：控温中圆是绿的，白图标正好；没控温时圆是白的，要把图标染成灰的才看得见
        if (lv_obj_t* ic = u.box("ctl.run.icon")) if (u.changed("ctl.run.icon", run)) { lv_obj_set_style_image_recolor(ic, lv_color_hex(0x8792A0), 0); lv_obj_set_style_image_recolor_opa(ic, run ? 0 : 255, 0); }
        if (lv_obj_t* o = u.box("ctl.run.circle")) if (u.changed("ctl.run.circle", run)) { bg(o, run ? 0x00B368 : 0xFFFFFF, 255); bd(o, run ? 0x00B368 : 0xC3CCD6, 255, 3 * UI_SCALE, LV_BORDER_SIDE_FULL); ol(o, 0x00B368, run ? 46 : 0, 5 * UI_SCALE); }
        refresh_cry(c, ci);
    }
};

#ifdef TEC_HMI_LIB
// ── 给别的程序嵌着用的接口 ──
// PC 工作站（C#）里点「手动控制面板」，弹出来的就是这套界面本身，不是另写一遍、也不是另开一个程序：
// 工作站把这个库装进自己的窗口，每帧问它要一张画面贴上去，把鼠标动作当手指喂回来。
// 这样屏上和 PC 上是同一份代码渲染的，不会走样（用户 2026-11-24 定的「甲」方案）。
//
// 规矩：所有函数都要在同一个线程上调（工作站的界面线程）。LVGL 不是线程安全的。
#ifdef _WIN32
#define TEC_API extern "C" __declspec(dllexport)
#else
#define TEC_API extern "C" __attribute__((visibility("default")))
#endif

namespace {
struct Embed {
    std::unique_ptr<App> app;
    bool ready = false;
    long loops = 0;
    std::chrono::steady_clock::time_point last_poll{};
};
Embed& em() { static Embed e; return e; }
}  // namespace

/// 起界面。assets_dir 是放着 assets 文件夹的那一层；host / port 是控制核心在哪。成功回 0
// 出了岔子记在这儿，调用方可以取走显示；不让任何异常跑到调用方那边去——
// 跨语言边界上漏出去的异常，在 C# 里只会变成一句「External component has thrown an exception」，什么也看不出来
// （用户 2026-11-25 在工作站里点面板就撞上这个）
static std::string g_err;
TEC_API const char* tec_hmi_last_error(void) { return g_err.c_str(); }

TEC_API int tec_hmi_init(const char* assets_dir, const char* host, int port, int channel) try {
    Embed& e = em();
    if (e.ready) return 0;                                  // 已经起过了
    ui::set_assets_dir(assets_dir ? assets_dir : "");
    if (!ui::assets_ok()) return 3;      // 字体图片都在 assets 里，没有它画出来是一片灰，不如直接说找不到
    lv_init();
    ui::register_fs();                  // 要在 lv_init 之后：lv_init 会把驱动表重置
    lv_tick_set_cb([]() -> uint32_t { return static_cast<uint32_t>(std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now().time_since_epoch()).count()); });
    lv_display_t* d = lv_display_create(W, H);
    if (!d) return 1;
    static uint32_t buf[W * H];
    lv_display_set_buffers(d, buf, nullptr, sizeof buf, LV_DISPLAY_RENDER_MODE_PARTIAL);
    lv_display_set_flush_cb(d, flush);
    lv_indev_t* touch = lv_indev_create();
    lv_indev_set_type(touch, LV_INDEV_TYPE_POINTER);
    lv_indev_set_read_cb(touch, [](lv_indev_t*, lv_indev_data_t* data) { data->point.x = g_finger.x; data->point.y = g_finger.y; data->state = g_finger.down ? LV_INDEV_STATE_PRESSED : LV_INDEV_STATE_RELEASED; });
    e.app = std::make_unique<App>();
    if (host && *host) e.app->host = host;
    if (port > 0) e.app->port = port;
    if (channel == 1 || channel == 2) e.app->channel = channel;
    e.app->show("Ctl");
    e.ready = true;
    return 0;
} catch (const std::exception& ex) {
    g_err = std::string("起界面时出错：") + ex.what();
    return 10;
} catch (...) {
    g_err = "起界面时出错（不知道是什么错）";
    return 11;
}

/// 走一帧：该问状态就问一次，该重画就重画。回「下次最晚隔多少毫秒再叫我」
TEC_API int tec_hmi_tick(void) try {
    Embed& e = em();
    if (!e.ready) return 100;
    const auto now = std::chrono::steady_clock::now();
    if (now - e.last_poll >= std::chrono::milliseconds(500)) { e.last_poll = now; e.app->poll(); }
    const uint32_t wait = lv_timer_handler();
    // 趁没人按着屏幕，把还没看过的页先在内存里画一遍（第一次点进去就不用现算了）
    if (++e.loops > 5 && !g_finger.down) e.app->warm_step();
    return static_cast<int>(wait < 1 ? 1 : wait > 30 ? 30 : wait);
} catch (const std::exception& ex) {
    g_err = std::string("刷新界面时出错：") + ex.what();
    return 100;
} catch (...) {
    g_err = "刷新界面时出错（不知道是什么错）";
    return 100;
}

/// 画面那块内存。一个像素四个字节，顺序是蓝绿红透明（和 Windows / Avalonia 的 Bgra8888 一致）
TEC_API const void* tec_hmi_pixels(void) { return g_fb; }
TEC_API void tec_hmi_size(int* w, int* h) { if (w) *w = W; if (h) *h = H; }

/// 自上次问过以后画面变没变。没变就不用再拷一遍
TEC_API int tec_hmi_take_dirty(void) { const bool d = g_dirty; g_dirty = false; return d ? 1 : 0; }

/// 手指（鼠标）在哪、按没按着。坐标就是画面自己的坐标，宿主负责从控件尺寸换算过来
TEC_API void tec_hmi_pointer(int x, int y, int down) {
    g_finger.x = x < 0 ? 0 : x >= W ? W - 1 : x;
    g_finger.y = y < 0 ? 0 : y >= H ? H - 1 : y;
    g_finger.down = down != 0;
}

/// 让界面跳到某一页 / 某个通道（工作站点的是哪台机器的哪个工位）
TEC_API void tec_hmi_show(const char* page, int channel) {
    Embed& e = em();
    if (!e.ready) return;
    if (channel == 1 || channel == 2) e.app->channel = channel;
    e.app->show(page && *page ? page : "Ctl");
}

/// 换一台机器看。整个程序里只有一套界面（LVGL 是全局的），所以同时只能看一台；
/// 工作站点了另一台机器的面板，就调这个把连接换过去，不是再开一个
TEC_API void tec_hmi_connect(const char* host, int port, int channel) {
    Embed& e = em();
    if (!e.ready) return;
    if (host && *host) e.app->host = host;
    if (port > 0) e.app->port = port;
    e.app->link.close();
    e.app->connected = false;
    e.app->st = json();                      // 上一台机器的读数别留在屏上
    if (channel == 1 || channel == 2) e.app->channel = channel;
    e.app->show("Ctl");
    e.last_poll = {};                        // 马上问一次新机器的状态
}

TEC_API void tec_hmi_shutdown(void) {
    Embed& e = em();
    if (!e.ready) return;
    e.ready = false;
    e.app.reset();
    // LVGL 不拆：这个库在宿主程序里只起一次，拆了再起反而容易出事
}
#endif  // TEC_HMI_LIB

#ifndef TEC_HMI_LIB
int main(int argc, char** argv) {
    // 字体和图片在哪：从程序自己旁边往上找带 assets 的那一层。从别的目录启动（开机自启、双击快捷方式）
    // 当前目录是不对的，所以不靠当前目录，直接把找到的路径告诉界面
    {
        namespace fs = std::filesystem;
        std::error_code ec;
        fs::path d = fs::absolute(argv[0], ec).parent_path().lexically_normal();
        std::string found, tried;
        for (int i = 0; i < 4 && found.empty(); i++, d = d.parent_path()) {
            tried += "\n    " + (d / "assets").u8string();
            if (fs::exists(d / "assets" / "fonts", ec)) found = d.u8string();
        }
        ui::set_assets_dir(found);      // 找不到也要调：盘符 A 的文件驱动是在它里面挂上的
        // 没有 assets 就一个字、一张图都画不出来，只剩一片白底和几个框——现场看见这一屏根本猜不到是少拷了文件夹。
        // 所以这里直接说出来就退出，不要闷头画（用户 2026-11-25 两次遇到这一屏）
        if (!ui::assets_ok()) {
            const std::string msg = "没找到界面用的字体和图片，界面会是一片白，所以先停下。\n\n"
                                    "把 assets 文件夹（里面是 fonts 和 img）放到下面任意一处：" + tried +
                                    "\n\n它在编译输出目录里，和这个程序在一起；源码里的那份在 hmi-cpp/ui/assets。";
#ifdef _WIN32
            // 必须走宽字符那套：源码里的中文是 UTF-8，MessageBoxA 会按系统的本地编码去解释它，出来是一片乱码
            // （用户 2026-11-25 看到的就是乱码框）
            win_message_box(msg, "HTLAB 多工位反应平台");
#else
            std::fprintf(stderr, "%s\n", msg.c_str());
#endif
            return 2;
        }
    }
    App app;
    // 开机先到总览：两个通道一屏看全。要从别的页面起来就 --page（出图自查常用）
    std::string page = "Main", shot;
    std::vector<std::string> todo;             // --do 动作名：开发时自查用，代替手指点一下
    std::vector<std::pair<int, int>> taps;     // --tap x,y：模拟手指在那个位置点一下（不开窗口时用）
    int zoom = 0;                              // --zoom：Windows 上整个界面放大多少（百分比）；不写 = 窗口时 100，全屏时放大到屏幕放得下的最大
    bool fullscreen = false;                   // --fullscreen：Windows 上铺满屏幕
    bool frame = false;                        // --frame：Windows 上带系统的标题栏和边框（缺省不带）
    bool only_1x = false;                      // --1x：屏幕再大也用 1 倍这一份，不自动换成 2 倍的
    bool lint_it = false;                      // --lint：出图的那一刻把这一页的排版量一遍（见 ui/src/lint.inc）
    bool line_test = false;      // --linetest：三条线的小自检（见下面那一段）
    std::string fit_file, lang;                // --fit 表：一句一句摆进去量（提示句、报警句屏上平时没有）；--lang：不连核心时指定界面语言
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
        else if (a == "--frame") frame = true;
        else if (a == "--1x") only_1x = true;
        else if (a == "--lint") lint_it = true;      // 排版自查：把这一页上所有的字量一遍（见 ui/src/lint.inc）
        else if (a == "--fit" && i + 1 < argc) fit_file = argv[++i];
        else if (a == "--linetest") line_test = true;      // 画三条线看哪种画得出来（现场「曲线只剩点」时用）
        else if (a == "--lang" && i + 1 < argc) lang = argv[++i];
    }
    (void)only_1x;
    lv_init();
    ui::register_fs();                  // 图片走的那个文件驱动：要在 lv_init 之后挂
    // LVGL 的钟用系统的单调钟：动画、长按判定都按真实时间走，不管一圈循环实际花了多久
    lv_tick_set_cb([]() -> uint32_t { return static_cast<uint32_t>(std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now().time_since_epoch()).count()); });
    using clock = std::chrono::steady_clock;
    const auto t0 = clock::now();
    auto last_poll = t0 - std::chrono::seconds(1);

    bool windowed = false;
#ifdef _WIN32
    // Windows 上真正开窗口（见 win_window.inc）。带 --shot 时不开，走不开窗口、只出图的那一套（自查用）
    if (shot.empty() && fit_file.empty()) {
#if UI_SCALE == 1
        if (!only_1x && zoom == 0 && win::hand_over_to_2x(fullscreen)) return 0;       // 屏幕够大：交给按 2 倍画的那一份
#endif
        if (!win::open(zoom, fullscreen, frame)) { std::fprintf(stderr, "窗口开不出来\n"); return 1; }
        windowed = true;
    }
#else
    (void)zoom; (void)fullscreen; (void)frame;
#endif

    // LVGL 画到内存里，画完一块交给 flush：开着窗口就贴到窗口上；不开窗口（Mac 上开发自查）就只留在内存里，可以存成图
    lv_display_t* d = lv_display_create(W, H);
    static uint32_t buf[W * H];
    lv_display_set_buffers(d, buf, nullptr, sizeof buf, LV_DISPLAY_RENDER_MODE_PARTIAL);
    lv_display_set_flush_cb(d, flush);
    // 假的手指：--tap x,y（设计稿上的坐标，1280×720 那一套）走的是和真触摸一样的路——LVGL 自己找点中了谁、判是不是一次点击
    auto& finger = g_finger;
    lv_indev_t* touch = lv_indev_create();
    lv_indev_set_type(touch, LV_INDEV_TYPE_POINTER);
    lv_indev_set_read_cb(touch, [](lv_indev_t*, lv_indev_data_t* data) { data->point.x = g_finger.x; data->point.y = g_finger.y; data->state = g_finger.down ? LV_INDEV_STATE_PRESSED : LV_INDEV_STATE_RELEASED; });
    // ── --linetest：三条线的小自检 ──
    // 现场 2026-11-27：趋势和结晶曲线都「只剩点，连不成线」。横线竖线和斜线在 LVGL 里走的不是一条路
    //（斜线要靠「复杂渲染」那一套遮罩），所以画三条看哪种出得来：
    //   三条都在 = 画线没问题，去查画曲线的那段代码
    //   横竖在、斜的没有 = 这台机器上的 LVGL 画不了斜线（复杂渲染被关了，或者 third_party 里不是 9.2.2）
    // 窗口开着就一直显示，Ctrl+C 或关窗退出；带 --shot 时存一张图就退
    if (line_test) {
        lv_obj_t* scr = lv_obj_create(nullptr);
        lv_obj_remove_style_all(scr);
        bg(scr, 0xFFFFFF, 255);
        static lv_point_precise_t hor[2], ver[2], skew[2];
        const int S = UI_SCALE;
        hor[0] = {lv_value_precise_t(80 * S), lv_value_precise_t(120 * S)};  hor[1] = {lv_value_precise_t(480 * S), lv_value_precise_t(120 * S)};
        ver[0] = {lv_value_precise_t(80 * S), lv_value_precise_t(180 * S)};  ver[1] = {lv_value_precise_t(80 * S),  lv_value_precise_t(400 * S)};
        skew[0] = {lv_value_precise_t(160 * S), lv_value_precise_t(400 * S)}; skew[1] = {lv_value_precise_t(560 * S), lv_value_precise_t(180 * S)};
        for (lv_point_precise_t* p2 : {hor, ver, skew}) {
            lv_obj_t* l = lv_line_create(scr);
            lv_line_set_points(l, p2, 2);
            lv_obj_set_style_line_width(l, 4 * S, 0);
            lv_obj_set_style_line_color(l, lv_color_hex(0x00559C), 0);
            lv_obj_set_style_line_rounded(l, true, 0);
        }
        txt(scr, 320 * S, 60 * S, "横线 · 竖线 · 斜线", 600, 20, 0x1F2A35, 255, 0, 0);
        txt(scr, 320 * S, 440 * S, "斜的那条没出来 = 这台机器上的 LVGL 画不了斜线", 400, 15, 0x8792A0, 255, 0, 0);
        lv_screen_load(scr);
        for (int k = 0;; k++) {
#ifdef _WIN32
            if (windowed && !win::pump()) return 0;
#endif
            lv_timer_handler();
            std::this_thread::sleep_for(std::chrono::milliseconds(20));
            if (!shot.empty() && k >= 30) {
                if (std::FILE* f = std::fopen(shot.c_str(), "wb")) { std::fwrite(g_fb, 4, static_cast<size_t>(W) * H, f); std::fclose(f); }
                return 0;
            }
            if (!windowed && k > 600) return 0;
        }
    }

    if (!lang.empty()) ui::set_language(lang);      // 平时语言是从核心的设置里读的；--fit 不连核心，得明说
    // --fit：不连核心、不进主循环，把表里每一句摆进它该去的位置量一遍就退出
    if (!fit_file.empty()) return lint::fit(fit_file, kPages, sizeof kPages / sizeof kPages[0]) > 0 ? 2 : 0;
    app.show(page);
    int frames = 0, tap_phase = 0;
    long loops = 0;
    auto last_warm = t0;
    for (;;) {
        const auto now = clock::now();
        if (app.quit) return 0;                        // 设置页底下点了「退出」（见 sys_page.inc）
#ifdef _WIN32
        if (windowed && !win::pump()) return 0;        // 窗口被关掉了
#endif
        if (now - last_poll >= std::chrono::milliseconds(500)) { last_poll = now; app.poll(); }
        const uint32_t wait = lv_timer_handler();
        // 查「哪一下慢」用：设了环境变量 UI_TIME，一圈超过 50 毫秒就往 tec_hmi_time.log 里记一行（Windows 上程序没有命令行窗口，所以写文件）
        static const bool timing = std::getenv("UI_TIME") != nullptr;
        auto note_slow = [&](const char* what, clock::time_point from) {
            const double ms = std::chrono::duration<double, std::milli>(clock::now() - from).count();
            if (timing && ms > 50) if (std::FILE* f = std::fopen("tec_hmi_time.log", "a")) { std::fprintf(f, "%s %.0f ms（当前页 %s）\n", what, ms, app.cur.c_str()); std::fclose(f); }
        };
        note_slow("画一圈", now);
        // 提前准备别的页：第一页已经画出来（过了头几圈）、没有鼠标/触摸活动的时候，隔一小段空当备一页
        bool can_warm = ++loops > 5 && !g_finger.down && (shot.empty() || std::getenv("UI_WARM"));
#ifdef _WIN32
        if (windowed && (win::input_recent(400) || now - last_warm < std::chrono::milliseconds(80))) can_warm = false;
#endif
        if (can_warm) { last_warm = now; const auto w0 = clock::now(); if (app.warm_step()) note_slow("提前准备一页", w0); }
#ifdef _WIN32
        if (windowed) { win::idle(wait < 1 ? 1 : wait > 10 ? 10 : wait); continue; }      // 开着窗口：下面那些是不开窗口时自查用的，用不着
#endif
        (void)wait; (void)windowed;
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
        if ((!shot.empty() || lint_it) && todo.empty() && taps.empty() && frames >= 60 && std::chrono::duration<double>(now - t0).count() >= after) {
            if (!shot.empty()) if (std::FILE* f = std::fopen(shot.c_str(), "wb")) { std::fwrite(g_fb, 4, static_cast<size_t>(W) * H, f); std::fclose(f); }
            if (lint_it) return lint::run(app.cur.c_str()) > 0 ? 2 : 0;
            return 0;
        }
    }
}
#endif  // TEC_HMI_LIB
