// 「反应釜与安全」页和安全联锁的报警弹窗。这个文件直接嵌在 main.cpp 的 App 里面。
//
// 安全限值在控制核心里（limits.get / limits.set，存盘）。这一页显示并让操作人改其中三项：
// 釜内下限、釜内上限、变温速率上限。核心只收「在设备底线之内收紧」的值，序列运行中不收改动——
// 这两条规矩都在核心里把着，界面只是把核心不答应时说的话原样显示出来。
//
// 报警弹窗：核心里只要还有没确认的报警，不管现在在哪一页，弹窗都会出来并且关不掉，
// 一次显示一条（最早的那条），点「复位（确认）」确认这一条，还有就接着出下一条。

struct SafetyView { json lim; std::string msg; std::chrono::steady_clock::time_point asked{}; };
SafetyView sfv[2];

void safety_fetch(int ci, bool force) {
    const auto now = std::chrono::steady_clock::now();
    if (!force && sfv[ci].lim.is_object() && now - sfv[ci].asked < std::chrono::seconds(3)) return;
    sfv[ci].asked = now;
    const json r = ask({{"cmd", "limits.get"}, {"ch", ci + 1}});
    if (r.value("ok", false) && r["limits"].is_object()) sfv[ci].lim = r["limits"];
}

bool safety_action(const std::string& a) {
    if (a.rfind("sf:edit:", 0) != 0) return false;
    const int ci = channel - 1, k = a[8] - '0';
    SafetyView& v = sfv[ci];
    if (!v.lim.is_object()) return true;
    if (seq_locked(ci)) { v.msg = "序列运行中不能改安全限值"; refresh(); return true; }
    const double lo = v.lim.value("tr_min", -40.0), hi = v.lim.value("tr_max", 180.0), rate = v.lim.value("rate_max_k_per_min", 0.0);
    static const char* field[3] = {"tr_min", "tr_max", "rate_max_k_per_min"};
    static const char* title[3] = {"釜内温度下限 Tr min", "釜内温度上限 Tr max", "变温速率上限（填 0 = 不检测）"};
    if (k < 0 || k > 2) return true;
    const double cur_v = k == 0 ? lo : k == 1 ? hi : rate;
    // 键盘的范围就按规矩给：下限只能在 −40 和现在的上限之间，上限只能在现在的下限和 180 之间
    const double kmin = k == 0 ? -40 : k == 1 ? std::ceil(lo + 1) : 0, kmax = k == 0 ? std::floor(hi - 1) : k == 1 ? 180 : 20;
    const std::string unit = k == 2 ? "℃/min" : "℃";
    open_keypad("通道 " + std::to_string(channel) + " · " + title[k], unit, cur_v, kmin, kmax, (k == 2 ? f2(cur_v) : f1(cur_v)) + " " + unit, "—",
                [this, ci, k](double x) {
                    const json r = ask({{"cmd", "limits.set"}, {"ch", ci + 1}, {field[k], x}});
                    sfv[ci].msg = r.value("ok", false) ? "已保存" : r.value("error", std::string("没改成"));
                    safety_fetch(ci, true);
                    refresh();
                }, k == 2 ? 2 : 1);
    return true;
}

void refresh_safety(Ui& u, const json& c, int ci) {
    safety_fetch(ci, false);
    SafetyView& v = sfv[ci];
    const json vs = c.value("vessel", json(""));
    u.set_text("sf.vessel", vs.is_string() && !vs.get<std::string>().empty() ? vs.get<std::string>() : "—");
    // 还没拿到限值：把几个数字清成「—」，别把设计稿里的示意值（−40.0 / 180.0）当成真的限值摆在那儿
    if (!v.lim.is_object()) {
        for (int i = 0; i < 5; i++) u.set_text("sf.v." + std::to_string(i), "—");
        u.set_text("sf.chip", "还没拿到控制核心的安全限值");
        return;
    }
    const double lo = v.lim.value("tr_min", -40.0), hi = v.lim.value("tr_max", 180.0), rate = v.lim.value("rate_max_k_per_min", 0.0);
    const std::string vals[5] = {f1(lo) + " ℃", f1(hi) + " ℃", rate > 0 ? f2(rate) + " ℃/min" : "关（不检测）",
                                 f0(v.lim.value("tj_min", -40.0)) + " … " + f0(v.lim.value("tj_max", 200.0)) + " ℃", "≤ " + f0(v.lim.value("tc_max", 45.0)) + " ℃"};
    for (int i = 0; i < 5; i++) {
        const std::string id = "sf.v." + std::to_string(i);
        u.set_text(id, vals[i]);
        // 前三行的数值外面有个框：字变长变短，框跟着走
        if (i < 3) if (lv_obj_t* box = u.box(id)) if (lv_obj_t* lb = u.label(id)) { lv_obj_update_layout(lb); lv_obj_set_width(box, lv_obj_get_width(lb) + 24 * UI_SCALE); }
    }
    u.set_text("sf.n.0", std::string("低于此值 → 停控并报警　·　设备底线 −40 ℃") + (lo > -40 ? "　·　已收紧" : ""));
    u.set_text("sf.n.1", std::string("高于此值 → 停控并报警　·　设备底线 180 ℃") + (hi < 180 ? "　·　已收紧" : ""));
    u.set_text("sf.n.2", rate > 0 ? "釜内最近一分钟变温比这个快 → 停控并报警；填 0 = 关" : "点左边的框填一个值就开始盯实测斜率，填 0 = 关");
    // 冷媒温度 = 冷水机的冷却水温度（用户 2026-11-24）。只在这个通道正用 TEC 制冷时才会因为它停控：水太热，TEC 的热排不出去
    u.set_text("sf.n.4", st["coolant_c"].is_number() ? "冷水机水温现在 " + f1(st["coolant_c"].get<double>()) + " ℃　·　TEC 制冷时高于此值 → 停控并报警"
                                                      : "没读到冷水机水温（没接或没通上），读到以后才检测这一项");
    const std::string tip = !v.msg.empty() ? v.msg : seq_locked(ci) ? "序列运行中不可修改" : "限值只能在设备底线之内收紧";
    u.set_text("sf.chip", tip);
    lv_obj_t* chip = u.box("sf.chip"); lv_obj_t* chip2 = u.box("sf.chip2");
    if (chip && chip2) if (lv_obj_t* lb = u.label("sf.chip")) {
        lv_obj_update_layout(lb);
        lv_obj_set_width(chip, lv_obj_get_width(lb) + 28 * UI_SCALE); lv_obj_set_x(lb, 14 * UI_SCALE);
        lv_obj_set_x(chip2, lv_obj_get_x(chip) + lv_obj_get_width(chip) + 8 * UI_SCALE);
    }
}

// ── 报警弹窗 ──
Page aldlg;
struct AlarmView { json list = json::array(); int shown_id = -1; int count = -1; std::chrono::steady_clock::time_point asked{}; };
AlarmView alv;

void alarm_refresh() {
    const int na = st.is_object() ? st.value("alarms_unacked", 0) : 0;
    if (na <= 0) { if (aldlg.scr) lv_obj_add_flag(aldlg.scr, LV_OBJ_FLAG_HIDDEN); alv.count = 0; return; }
    if (!aldlg.scr) {
        aldlg.scr = lv_obj_create(lv_layer_top());
        lv_obj_remove_style_all(aldlg.scr);
        lv_obj_set_size(aldlg.scr, W, H);
        lv_obj_add_flag(aldlg.scr, LV_OBJ_FLAG_CLICKABLE);         // 点外面关不掉，也点不到后面
        aldlg.ui = std::make_unique<Ui>();
        aldlg.ui->on_action = [this](const std::string& a) { action(a); };
        for (const PageDef& d : kPages) if (std::string(d.name) == "AlarmDlg") { d.build(aldlg.scr, *aldlg.ui); lv_obj_update_layout(aldlg.scr); }
    }
    const auto now = std::chrono::steady_clock::now();
    if (na != alv.count || now - alv.asked > std::chrono::seconds(2)) {
        alv.asked = now; alv.count = na;
        const json r = ask({{"cmd", "alarm.list"}});
        if (r.value("ok", false) && r["alarms"].is_array()) alv.list = r["alarms"];
    }
    const json* shown = nullptr;
    for (auto& a : alv.list) if (!a.value("acked", false)) { shown = &a; break; }
    if (!shown) return;
    if (lv_obj_has_flag(aldlg.scr, LV_OBJ_FLAG_HIDDEN)) { lv_obj_remove_flag(aldlg.scr, LV_OBJ_FLAG_HIDDEN); lv_obj_move_foreground(aldlg.scr); }     // 盖在别的弹窗（键盘之类）上面
    alv.shown_id = shown->value("id", -1);
    Ui& u = *aldlg.ui;
    const int ch = shown->value("ch", 1);
    // 报警记的是核心的时间，换成墙上的钟点：现在几点，减去「核心现在的时间 − 报警那一刻」
    const long long wall = static_cast<long long>(std::time(nullptr)) - static_cast<long long>(std::llround(st.value("t", 0.0) - shown->value("t", 0.0)));
    u.set_text("al.when", "通道 " + std::to_string(ch) + " · " + clock_text(wall) + (na > 1 ? " · 还有 " + std::to_string(na - 1) + " 条" : ""));
    u.set_text("al.title", shown->value("title", ""));
    u.set_text("al.action", "安全层已执行：" + shown->value("action", ""));
    if (ch >= 1 && ch <= 2 && st.contains("ch")) {
        const json& c = st["ch"][ch - 1];
        u.set_text("al.now", std::string("现在实测：") + (c["tr"].is_number() ? "Tr " + f1(c["tr"].get<double>()) + " ℃　" : "") + "Tj " + f1(c.value("tj", 0.0)) + " ℃");
    }
    u.show("al.all", na > 1);
}

// ── 报警记录（点顶栏的铃、没有待确认的报警时出来）──
// 确认过的报警原来就看不到了，这里把核心这次开机以来的全列出来，最近的在上，一页 8 条。
Page allist;
int al2_page = 0;
static constexpr int kAl2Rows = 8;

void alarmlist_fill() {
    if (!allist.scr || lv_obj_has_flag(allist.scr, LV_OBJ_FLAG_HIDDEN)) return;
    Ui& u = *allist.ui;
    const int n = static_cast<int>(alv.list.size());
    const int pages = std::max(1, (n + kAl2Rows - 1) / kAl2Rows);
    al2_page = std::max(0, std::min(al2_page, pages - 1));
    u.set_text("al2.sub", n == 0 ? "控制核心这次开机以来没有报过警" : "共 " + std::to_string(n) + " 条 · 控制核心这次开机以来的");
    u.set_text("al2.page", std::to_string(al2_page + 1) + " / " + std::to_string(pages));
    for (int i = 0; i < kAl2Rows; i++) {
        const std::string id = "al2.r." + std::to_string(i);
        const int k = n - 1 - (al2_page * kAl2Rows + i);        // 最近的在上
        u.show(id, k >= 0);
        if (k < 0) continue;
        const json& a = alv.list[k];
        const std::time_t w = std::time(nullptr) - static_cast<std::time_t>(std::llround(st.value("t", 0.0) - a.value("t", 0.0)));
        std::tm tm{};
#ifdef _WIN32
        localtime_s(&tm, &w);
#else
        localtime_r(&w, &tm);
#endif
        char b[32]; std::strftime(b, sizeof b, "%m-%d %H:%M:%S", &tm);
        const bool acked = a.value("acked", false);
        u.set_text(id + ".t", b);
        u.set_text(id + ".c", "通道 " + std::to_string(a.value("ch", 1)));
        u.set_text(id + ".n", a.value("title", ""));
        u.set_text(id + ".s", acked ? "已确认" : "未确认");
        u.set_text_color(id + ".s", acked ? 0x8792A0 : 0xD64545);
    }
}

void open_alarmlist() {
    if (!allist.scr) {
        allist.scr = lv_obj_create(lv_layer_top());
        lv_obj_remove_style_all(allist.scr);
        lv_obj_set_size(allist.scr, W, H);
        lv_obj_add_flag(allist.scr, LV_OBJ_FLAG_CLICKABLE);
        allist.ui = std::make_unique<Ui>();
        allist.ui->on_action = [this](const std::string& a) { action(a); };
        for (const PageDef& d : kPages) if (std::string(d.name) == "AlarmListDlg") { d.build(allist.scr, *allist.ui); lv_obj_update_layout(allist.scr); }
    }
    const json r = ask({{"cmd", "alarm.list"}});
    if (r.value("ok", false) && r["alarms"].is_array()) alv.list = r["alarms"];
    al2_page = 0;
    lv_obj_remove_flag(allist.scr, LV_OBJ_FLAG_HIDDEN);
    lv_obj_move_foreground(allist.scr);
    alarmlist_fill();
}

bool alarm_action(const std::string& a) {
    if (a.rfind("al2:", 0) == 0) {
        if (!allist.scr) return true;
        if (a == "al2:close") lv_obj_add_flag(allist.scr, LV_OBJ_FLAG_HIDDEN);
        else { al2_page += a == "al2:next" ? 1 : -1; alarmlist_fill(); }
        return true;
    }
    if (a == "alarms") {
        // 点顶栏的铃：有没确认的就把报警弹窗叫出来；没有就看报警记录
        if (st.is_object() && st.value("alarms_unacked", 0) > 0) { alv.count = -1; alarm_refresh(); }
        else open_alarmlist();
        return true;
    }
    if (a != "al:ack" && a != "al:all") return false;
    if (a == "al:ack") ask({{"cmd", "alarm.ack"}, {"id", alv.shown_id}}); else ask({{"cmd", "alarm.ack"}});
    alv.count = -1;
    poll();
    return true;
}
