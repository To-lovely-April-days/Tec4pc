// 任务序列页。这个文件直接嵌在 main.cpp 的 App 里面（只是为了不让 main.cpp 太长），不是独立的一份代码。
//
// 做法：序列以控制核心里那一份为准。进这一页、或者序列状态变了，就向核心要一次（seq.get）；
// 每改一处（加一步、删一步、改一个数）马上整份发回核心（seq.load），再要回来显示——
// 这样 PC 端和触摸屏同时在看时，两边看到的永远是同一份，也不会出现「屏上改了但没存进去」。
// 核心在序列跑起来以后不收改动，所以跑着的时候这里所有的改动键都不起作用，只给一句提示。

struct SeqView {
    json steps = json::array();
    int sel = 0;                // 参数区现在显示的是第几步
    std::string key;            // 上次向核心要步骤时的「状态 / 步数」，变了就重新要
    std::string msg;            // 核心不答应时给的那句话，显示在左下角
    std::string name;           // 这条序列的名字（新建时起的，或者装进来的配方名）；空着就在开始时按日期时间自动起
};
SeqView sv[2];

static constexpr int kParts = 8;
struct SeqParts { bool built = false; lv_obj_t* scr[kParts] = {}; std::unique_ptr<Ui> ui[kParts]; lv_obj_t* arc[6] = {}; };
std::map<std::string, SeqParts> seq_parts;      // 通道 1、通道 2 各有一页，各带一套参数区
Page stepdlg;
int step_slot = -1;             // 步骤类型弹窗是给哪一步选的；-1 = 在末尾加一步

static const char* kind_title(const std::string& k) {
    return k == "reactor" ? "Tr 釜内温度" : k == "jacket" ? "Tj 夹套温度" : k == "reflux" ? "蒸回流" : k == "crystal" ? "结晶"
         : k == "stir" ? "搅拌转速" : k == "wait" ? "等待" : k == "dose" ? "加料" : "暂停确认";
}
// 参数区一共 8 块，按步骤类型挑一块显示。加料按「加入方式」分两块：pH 反馈要填的东西和另外两种完全不一样
static int part_of(const json& s) {
    const std::string k = s.value("kind", "wait");
    if (k == "reactor" || k == "jacket") return 0;
    if (k == "reflux") return 1;
    if (k == "wait") return 2;
    if (k == "stir") return 3;
    if (k == "dose") return s.contains("dose") && s["dose"].value("mode", "rate") == "ph_hold" ? 5 : 4;
    if (k == "crystal") return 6;
    return 7;
}
static std::string dur_text(double m) {
    const long t = static_cast<long>(m + 0.5);
    char b[48];
    if (t >= 60) std::snprintf(b, sizeof b, "%ld h %02ld min", t / 60, t % 60); else std::snprintf(b, sizeof b, "%ld min", t);
    return b;
}
// 这一步大概要多久（分钟）。「尽快」到温的步骤事先算不出来，按 0 计，所以总时长前面写的是「约」
static double step_minutes(const json& s) {
    const std::string k = s.value("kind", "");
    if (k == "reactor" || k == "jacket") return s.value("arrive", "fast") == "duration" ? s.value("duration_min", 0.0) : 0.0;
    if (k == "reflux" || k == "wait") return s.value("minutes", 0.0);
    if (k == "crystal") { double m = 0; if (s.contains("profile")) for (auto& g : s["profile"]) m += g.value("minutes", 0.0); return m; }
    if (k == "dose" && s.contains("dose")) {
        const json& d = s["dose"]; const std::string md = d.value("mode", "rate");
        if (md == "ph_hold") return d.value("hold_min", 0.0);
        if (md == "rate" && d.value("rate_ml_min", 0.0) > 0) return d.value("volume_ml", 0.0) / d.value("rate_ml_min", 1.0);
    }
    return 0.0;
}
// 步骤格里的三行字：类型、做什么、怎么做
static void step_lines(const json& s, std::string& k, std::string& a, std::string& d) {
    const std::string kind = s.value("kind", "wait");
    a.clear(); d.clear();
    if (kind == "reactor" || kind == "jacket") {
        k = kind == "reactor" ? "Tr" : "Tj";
        a = "到 " + f1(s.value("target", 0.0)) + " ℃";
        const std::string ar = s.value("arrive", "fast");
        d = ar == "rate" ? f2(s.value("rate_k_per_min", 0.0)) + " ℃/min" : ar == "duration" ? "按时长 " + dur_text(s.value("duration_min", 0.0)) : "尽快";
    } else if (kind == "reflux") { k = "回流"; a = "夹套高 " + f1(s.value("delta_k", 8.0)) + " K"; d = dur_text(s.value("minutes", 0.0)); }
    else if (kind == "wait") { k = "Wait"; a = "保持现状"; d = dur_text(s.value("minutes", 0.0)); }
    else if (kind == "stir") { k = "R"; a = f0(s.value("stir_rpm", 0.0)) + " rpm"; }
    else if (kind == "crystal") {
        k = "结晶";
        const int n = s.contains("profile") ? static_cast<int>(s["profile"].size()) : 0;
        a = std::to_string(n) + " 段"; d = dur_text(step_minutes(s));
    } else if (kind == "dose") {
        k = "加料";
        const json d0 = s.value("dose", json::object()); const std::string md = d0.value("mode", "rate");
        if (md == "ph_hold") { a = "pH " + f2(d0.value("ph_target", 7.0)) + " ± " + f2(d0.value("ph_band", 0.2)); d = "维持 " + dur_text(d0.value("hold_min", 0.0)); }
        else { a = f1(d0.value("volume_ml", 0.0)) + " mL"; d = md == "rate" ? f2(d0.value("rate_ml_min", 0.0)) + " mL/min" : "一次加入"; }
    } else { k = "暂停"; a = "等人确认"; }
}

bool seq_locked(int ci) const {
    if (!st.is_object() || !st.contains("ch")) return false;
    const std::string qs = st["ch"][ci]["seq"].value("state", "idle");
    return qs == "running" || qs == "waiting_confirm";
}
void seq_fetch(int ci) {
    const json g = ask({{"cmd", "seq.get"}, {"ch", ci + 1}});
    if (g.value("ok", false) && g["steps"].is_array()) sv[ci].steps = g["steps"];
    const int n = static_cast<int>(sv[ci].steps.size());
    sv[ci].sel = std::max(0, std::min(sv[ci].sel, n - 1));
}
// 把改好的步骤整份交给核心；核心不收（比如序列已经开始）就把它的话显示出来
void seq_push(int ci) {
    const json r = ask({{"cmd", "seq.load"}, {"ch", ci + 1}, {"steps", sv[ci].steps}});
    sv[ci].msg = r.value("ok", false) ? "" : r.value("error", std::string("没存进去"));
    seq_fetch(ci);
    sv[ci].key.clear();
    poll();
}

void open_stepdlg(int slot, const std::string& title) {
    if (!stepdlg.scr) {
        stepdlg.scr = lv_obj_create(lv_layer_top());
        lv_obj_remove_style_all(stepdlg.scr);
        lv_obj_set_size(stepdlg.scr, W, H);
        lv_obj_add_flag(stepdlg.scr, LV_OBJ_FLAG_CLICKABLE);
        stepdlg.ui = std::make_unique<Ui>();
        stepdlg.ui->on_action = [this](const std::string& a) { action(a); };
        for (const PageDef& d : kPages) if (std::string(d.name) == "StepDlg") d.build(stepdlg.scr, *stepdlg.ui);
    }
    step_slot = slot;
    stepdlg.ui->set_text("st.title", title);
    lv_obj_remove_flag(stepdlg.scr, LV_OBJ_FLAG_HIDDEN);
}

// 新加一步时先给一组能直接跑的数，用户再改；不给全的那些由核心补缺省值
static json default_step(int kind) {
    static const char* names[] = {"reactor", "jacket", "reflux", "crystal", "stir", "wait", "dose", "pause"};      // 和步骤类型弹窗里八个键的顺序一致
    json s = {{"kind", names[kind]}};
    switch (kind) {
        case 0: case 1: s["target"] = 25.0; s["arrive"] = "fast"; s["stir_rpm"] = -1.0; break;
        case 2: s["delta_k"] = 8.0; s["minutes"] = 60.0; s["stir_rpm"] = -1.0; break;
        case 3: s["profile"] = json::array({{{"target", 20.0}, {"minutes", 60.0}}}); s["stir_rpm"] = -1.0; break;
        case 4: s["stir_rpm"] = 300.0; break;
        case 5: s["minutes"] = 30.0; break;
        case 6: s["dose"] = {{"pump", 1}, {"mode", "rate"}, {"volume_ml", 10.0}, {"rate_ml_min", 1.0}}; break;
        default: s["message"] = ""; break;
    }
    return s;
}

// 参数区里能点开数字键盘的那些格子：点的是哪一格 → 改步骤里的哪个数、范围多少、几位小数
struct SeqField { const char* key; const char* where; const char* name; const char* title; const char* unit; double lo, hi; int dec; };
bool seq_edit_field(int ci, const std::string& key) {
    SeqView& v = sv[ci];
    if (v.steps.empty()) return true;
    json& s = v.steps[v.sel];
    const std::string kind = s.value("kind", "");
    const bool by_time = s.value("arrive", "fast") == "duration";
    const SeqField F[] = {
        {"target", "", "target", kind == "jacket" ? "目标 Tj" : "目标 Tr", "℃", -40, kind == "jacket" ? 200.0 : 180.0, 1},
        {"arr", "", by_time ? "duration_min" : "rate_k_per_min", by_time ? "到达用时" : "升降温速率", by_time ? "min" : "℃/min", by_time ? 1.0 : 0.01, by_time ? 600.0 : 20.0, by_time ? 0 : 2},
        {"stir", "", "stir_rpm", "搅拌转速", "rpm", 0, 1000, 0},
        {"delta", "", "delta_k", "夹套比釜内高", "K", 1, 30, 1},
        {"minutes", "", "minutes", "时长", "min", 1, 600, 0},
        {"volume", "dose", "volume_ml", "加料体积", "mL", 0.1, 500, 1},
        {"rate", "dose", "rate_ml_min", "流量", "mL/min", 0.01, 50, 2},
        {"ph", "dose", "ph_target", "目标 pH", "pH", 0, 14, 2},
        {"band", "dose", "ph_band", "死区", "pH", 0.05, 2, 2},
        {"maxrate", "dose", "max_rate_ml_min", "最大流量", "mL/min", 0.01, 50, 2},
        {"maxvol", "dose", "max_volume_ml", "最大总量", "mL", 0.1, 500, 1},
        {"hold", "dose", "hold_min", "维持时长", "min", 1, 600, 0},
    };
    for (const SeqField& f : F) {
        if (key != f.key) continue;
        const std::string where = f.where, name = f.name;
        const json& from = where.empty() ? s : s[where];
        const double curv = std::max(0.0, from.value(name, 0.0)) ;
        const double shown = name == "target" ? from.value(name, 0.0) : curv;
        char last[48]; std::snprintf(last, sizeof last, "%.*f %s", f.dec, shown, f.unit);
        const int idx = v.sel;
        open_keypad("第 " + std::to_string(idx + 1) + " 步 · " + f.title, f.unit, shown, f.lo, f.hi, last, "—",
                    [this, ci, idx, where, name](double x) {
                        SeqView& w = sv[ci];
                        if (idx >= static_cast<int>(w.steps.size())) return;
                        if (where.empty()) w.steps[idx][name] = x; else w.steps[idx][where][name] = x;
                        seq_push(ci);
                    }, f.dec);
        return true;
    }
    return false;
}

bool seq_action(const std::string& a) {
    const int ci = channel - 1;
    SeqView& v = sv[ci];
    const bool edit = a.rfind("sq:add", 0) == 0 || a.rfind("det:", 0) == 0 || a.rfind("st:pick:", 0) == 0 || a.rfind("f:", 0) == 0 || a.rfind("sarr:", 0) == 0
                   || a.rfind("pump:", 0) == 0 || a.rfind("way:", 0) == 0 || a == "sync" || a == "seq:clear" || a == "seq:new";
    if (edit && seq_locked(ci)) { v.msg = "已开始的序列不可增改"; refresh(); return true; }
    const int n = static_cast<int>(v.steps.size());
    if (a.rfind("sq:sel:", 0) == 0) { const int i = a[7] - '0'; if (i < n) v.sel = i; refresh(); return true; }
    if (a == "sq:add") { if (n < 6) open_stepdlg(-1, "第 " + std::to_string(n + 1) + " 步做什么？"); return true; }
    if (a == "det:type") { if (n > 0) open_stepdlg(v.sel, "第 " + std::to_string(v.sel + 1) + " 步换成什么？"); return true; }
    if (a == "det:del") { if (n > 0) { v.steps.erase(v.sel); seq_push(ci); } return true; }
    if (a == "st:close") { if (stepdlg.scr) lv_obj_add_flag(stepdlg.scr, LV_OBJ_FLAG_HIDDEN); return true; }
    if (a.rfind("st:pick:", 0) == 0) {
        const json s = default_step(std::max(0, std::min(7, a[8] - '0')));
        if (step_slot >= 0 && step_slot < n) { v.steps[step_slot] = s; v.sel = step_slot; }
        else if (n < 6) { v.steps.push_back(s); v.sel = n; }
        if (stepdlg.scr) lv_obj_add_flag(stepdlg.scr, LV_OBJ_FLAG_HIDDEN);
        seq_push(ci);
        return true;
    }
    if (a == "f:liquid" && n > 0) {
        const int idx = v.sel;
        open_text_kb("第 " + std::to_string(idx + 1) + " 步 · 加的是什么料液", v.steps[idx].value("dose", json::object()).value("liquid", ""), 12,
                     [this, ci, idx](const std::string& s) { SeqView& w = sv[ci]; if (idx < static_cast<int>(w.steps.size())) { w.steps[idx]["dose"]["liquid"] = s; seq_push(ci); } });
        return true;
    }
    if (a == "f:msg" && n > 0) {
        const int idx = v.sel;
        open_text_kb("第 " + std::to_string(idx + 1) + " 步 · 停下来时给操作人看的提示", v.steps[idx].value("message", ""), 24,
                     [this, ci, idx](const std::string& s) { SeqView& w = sv[ci]; if (idx < static_cast<int>(w.steps.size())) { w.steps[idx]["message"] = s; seq_push(ci); } });
        return true;
    }
    if (a == "f:stir:keep") {
        // 温度类步骤的搅拌改回「不变」（沿用上一步的转速）；单独的「搅拌转速」步骤没有这个键
        if (n > 0 && v.steps[v.sel].value("kind", "") != "stir") { v.steps[v.sel]["stir_rpm"] = -1.0; seq_push(ci); }
        return true;
    }
    if (a.rfind("f:", 0) == 0) return seq_edit_field(ci, a.substr(2));
    if (a.rfind("sarr:", 0) == 0 && n > 0) {
        json& s = v.steps[v.sel];
        const int k = a[5] - '0';
        s["arrive"] = k == 1 ? "rate" : k == 2 ? "duration" : "fast";
        // 第一次切到「按速率 / 按时长」时那个数还是 0，先给一个能用的，用户再点开改
        if (k == 1 && s.value("rate_k_per_min", 0.0) <= 0) s["rate_k_per_min"] = 1.0;
        if (k == 2 && s.value("duration_min", 0.0) <= 0) s["duration_min"] = 30.0;
        seq_push(ci);
        return true;
    }
    if ((a.rfind("pump:", 0) == 0 || a.rfind("way:", 0) == 0 || a == "sync") && n > 0) {
        json& d = v.steps[v.sel]["dose"];
        if (!d.is_object()) d = json::object();
        if (a.rfind("pump:", 0) == 0) d["pump"] = a[5] - '0' + 1;
        else if (a == "sync") d["sync_with_temp"] = !d.value("sync_with_temp", true);
        else { static const char* modes[] = {"at_once", "rate", "ph_hold"}; d["mode"] = modes[std::max(0, std::min(2, a[4] - '0'))]; }
        seq_push(ci);
        return true;
    }
    if (a == "seq:clear") { v.steps = json::array(); v.sel = 0; seq_push(ci); return true; }
    if (a == "seq:new") {
        // 新建：先起名字，确定后清空步骤从头编。点 × 取消就什么都不动
        open_text_kb("给新序列起个名字", "", 30, [this, ci](const std::string& s) { SeqView& w = sv[ci]; w.name = s; w.steps = json::array(); w.sel = 0; seq_push(ci); });
        return true;
    }
    if (a == "seq:start") {
        // 这一炉的名字：有起好的序列名就用它，没有就按日期时间自动起
        const std::time_t now = std::time(nullptr); std::tm tm{};
#ifdef _WIN32
        localtime_s(&tm, &now);
#else
        localtime_r(&now, &tm);
#endif
        char b[48]; std::strftime(b, sizeof b, "序列 %m-%d %H:%M", &tm);
        const json r = ask({{"cmd", "seq.start"}, {"ch", channel}, {"name", v.name.empty() ? std::string(b) : v.name}});
        v.msg = r.value("ok", false) ? "" : r.value("error", std::string("开始不了"));
        poll();
        return true;
    }
    if (a == "seq:confirm") { ask({{"cmd", "seq.confirm"}, {"ch", channel}}); poll(); return true; }
    return false;
}

void seq_build_parts(const std::string& page, Ui& u, lv_obj_t* scr) {
    SeqParts& P = seq_parts[page];
    if (P.built) return;
    P.built = true;
    static const char* names[kParts] = {"SeqTempPart", "SeqRefluxPart", "SeqWaitPart", "SeqStirPart", "SeqDosePart", "SeqPhPart", "SeqCryPart", "SeqPausePart"};
    for (int p = 0; p < kParts; p++) {
        // 每块参数区放进一个和屏幕一样大的透明容器里：生成的代码是按整屏坐标摆的，藏 / 显只要动这个容器
        P.scr[p] = lv_obj_create(scr);
        lv_obj_remove_style_all(P.scr[p]);
        lv_obj_set_size(P.scr[p], W, H);
        lv_obj_remove_flag(P.scr[p], static_cast<lv_obj_flag_t>(LV_OBJ_FLAG_SCROLLABLE | LV_OBJ_FLAG_CLICKABLE));
        lv_obj_add_flag(P.scr[p], LV_OBJ_FLAG_HIDDEN);
        P.ui[p] = std::make_unique<Ui>();
        P.ui[p]->on_action = [this](const std::string& a) { action(a); };
        for (const PageDef& d : kPages) if (std::string(d.name) == names[p]) d.build(P.scr[p], *P.ui[p]);
    }
    // 当前步外圈那道跟着进度走的蓝弧：设计稿里画不出「会动的弧」，这里用 LVGL 的弧形控件叠在那个圆上
    for (int i = 0; i < 6; i++) {
        lv_obj_t* box = u.box("sq." + std::to_string(i) + ".now");
        if (!box) continue;
        const int bw = 5 * UI_SCALE, w = lv_obj_get_width(box);
        lv_obj_t* arc = lv_arc_create(box);
        lv_obj_remove_style_all(arc);
        lv_obj_set_size(arc, w, w);
        lv_obj_set_pos(arc, -bw, -bw);            // 子元素的位置是从边框里面算起的，往回挪一个边框宽才和外圈重合
        lv_obj_remove_flag(arc, LV_OBJ_FLAG_CLICKABLE);
        lv_obj_set_style_arc_opa(arc, 0, LV_PART_MAIN);
        lv_obj_set_style_arc_width(arc, bw, LV_PART_INDICATOR);
        lv_obj_set_style_arc_color(arc, lv_color_hex(0x00559C), LV_PART_INDICATOR);
        lv_obj_set_style_arc_rounded(arc, true, LV_PART_INDICATOR);
        lv_arc_set_rotation(arc, 270);
        lv_arc_set_bg_angles(arc, 0, 360);
        lv_arc_set_range(arc, 0, 100);
        P.arc[i] = arc;
    }
}

void seq_fill_part(Ui& d, const json& s, int i, int state, double elapsed_s) {
    const std::string kind = s.value("kind", "wait");
    d.set_text("det.title", "第 " + std::to_string(i + 1) + " 步 · " + kind_title(kind));
    d.set_text("det.badge", state == 1 ? "进行中" : state == 2 ? "已完成" : "未开始");
    if (lv_obj_t* b = d.box("det.badge")) if (d.changed("badge", state)) bg(b, state == 1 ? 0x00559C : state == 2 ? 0x8792A0 : 0xDCE4ED, 255);
    d.set_text_color("det.badge", state == 0 ? 0x5A6675 : 0xFFFFFF);
    d.set_text("det.right", state == 1 ? "这一步已走 " + hms(elapsed_s) : "");
    const double stir = s.value("stir_rpm", -1.0);
    d.set_text("f.stir", kind == "stir" ? f0(std::max(0.0, stir)) : stir < 0 ? "不变" : f0(stir));
    std::string tip = "点上面任意一步，这里就换成那一步的参数";
    if (kind == "reactor" || kind == "jacket") {
        const std::string ar = s.value("arrive", "fast");
        const int k = ar == "rate" ? 1 : ar == "duration" ? 2 : 0;
        d.set_text("f.target.l", kind == "jacket" ? "目标 Tj" : "目标 Tr");
        d.set_text("f.target", f1(s.value("target", 0.0)));
        set_radio(d, "sarr", 3, k);
        d.show("f.arr.box", k != 0);
        d.set_text("f.arr.l", k == 2 ? "用时" : "速率");
        d.set_text("f.arr.u", k == 2 ? "min" : "℃/min");
        d.set_text("f.arr", k == 2 ? f0(s.value("duration_min", 0.0)) : f2(s.value("rate_k_per_min", 0.0)));
        tip = "温度到了目标附近并且稳住，这一步才算完；搅拌写「不变」就是沿用上一步的转速";
    } else if (kind == "reflux") {
        d.set_text("f.delta", f1(s.value("delta_k", 8.0)));
        d.set_text("f.minutes", f0(s.value("minutes", 0.0)));
        tip = "夹套一直比釜内高这么多度，让它自己找到沸点；走满时长这一步结束";
    } else if (kind == "wait") { d.set_text("f.minutes", f0(s.value("minutes", 0.0))); tip = "控温和搅拌保持上一步的样子，只是等这么久"; }
    else if (kind == "stir") tip = "把搅拌改到这个转速，马上进下一步";
    else if (kind == "dose") {
        const json d0 = s.value("dose", json::object());
        const std::string md = d0.value("mode", "rate");
        const int way = md == "at_once" ? 0 : md == "ph_hold" ? 2 : 1;
        set_radio(d, "pump", 2, d0.value("pump", 1) - 1);
        set_radio(d, "way", 3, way);
        const std::string liq = d0.value("liquid", "");
        d.set_text("f.liquid", liq.empty() ? "未填" : liq);
        d.set_text("f.volume", f1(d0.value("volume_ml", 0.0)));
        d.set_text("f.rate", f2(d0.value("rate_ml_min", 0.0)));
        d.show("f.rate.box", way == 1);
        const bool sync = d0.value("sync_with_temp", true);
        if (d.changed("sync", sync)) {
            if (lv_obj_t* t = d.box("sw.sync")) bg(t, sync ? 0x00559C : 0xC3CCD6, 255);
            if (lv_obj_t* k = d.box("sw.sync.k")) lv_obj_set_x(k, (sync ? 25 : 3) * UI_SCALE);
        }
        d.set_text("f.ph", f2(d0.value("ph_target", 7.0)));
        d.set_text("f.band", f2(d0.value("ph_band", 0.2)));
        d.set_text("f.maxrate", f2(d0.value("max_rate_ml_min", 1.0)));
        d.set_text("f.maxvol", f1(d0.value("max_volume_ml", 50.0)));
        d.set_text("f.hold", f0(d0.value("hold_min", 60.0)));
        tip = way == 2 ? "pH 电极测、调节泵加，把 pH 维持在目标附近；走满维持时长结束，最大总量是加料上限"
            : way == 1 ? "加料时长 = 体积 ÷ 流量；加完这一步结束" : "按泵的最大流量一次送完";
    } else if (kind == "crystal") {
        const json pf = s.value("profile", json::array());
        if (pf.empty()) d.set_text("f.cry", "还没有曲线");
        else d.set_text("f.cry", "共 " + std::to_string(pf.size()) + " 段 · " + dur_text(step_minutes(s)) + " · 最后到 " + f1(pf.back().value("target", 0.0)) + " ℃");
        tip = "结晶就是分段曲线控温：点「编辑曲线」改各段的目标温度和用时，第一段从当时的釜温走起";
    } else {
        const std::string m = s.value("message", "");
        d.set_text("f.msg", m.empty() ? "请确认后继续" : m);
        tip = "走到这一步就停下来等人；在屏上点「确认继续」才往下走";
    }
    d.set_text("det.tip", tip);
}

void refresh_seq(Ui& u, const json& c, int ci) {
    SeqView& v = sv[ci];
    auto pg = pages.find(cur);
    seq_build_parts(cur, u, pg->second.scr);
    SeqParts& P = seq_parts[cur];
    const json& q = c["seq"];
    const std::string qs = q.value("state", "idle");
    const bool locked = qs == "running" || qs == "waiting_confirm";
    const std::string key = qs + "/" + std::to_string(q.value("count", 0));
    if (key != v.key) { seq_fetch(ci); v.key = key; }
    const int n = static_cast<int>(v.steps.size());
    const bool started = locked || qs == "done";
    const int idx = qs == "done" ? n : q.value("index", 0);
    const int pct = static_cast<int>(std::min(1.0, std::max(0.0, q.value("step_progress", 0.0))) * 100 + 0.5);

    double total = 0;
    for (auto& s : v.steps) total += step_minutes(s);
    std::string sum = (v.name.empty() ? "" : "「" + v.name + "」　·　") + std::to_string(n) + " / 6 步";
    if (n > 0) sum += "　·　预计约 " + dur_text(total);
    if (started) sum += "　·　已走 " + dur_text(q.value("total_elapsed_s", 0.0) / 60.0);
    u.set_text("seq.sum", sum);

    for (int i = 0; i < 6; i++) {
        const std::string id = "sq." + std::to_string(i);
        u.show(id, i < n);
        if (i < 5) {
            // 连线：连到下一步；最后一步后面那段连的是「添加」，序列跑起来以后「添加」不在了，这段也收掉
            u.show("sq.line." + std::to_string(i), i < n && (i + 1 < n || !locked));
            if (lv_obj_t* ln = u.box("sq.line." + std::to_string(i))) if (u.changed("line" + std::to_string(i), started && i < idx)) bg(ln, started && i < idx ? 0x00559C : 0xC9D6E5, 255);
        }
        if (i >= n) continue;
        const int stt = !started ? 0 : i < idx ? 2 : i == idx ? 1 : 0;        // 0 没到  1 正在走  2 走完
        u.show(id + ".todo", stt == 0); u.show(id + ".now", stt == 1); u.show(id + ".done", stt == 2);
        if (stt == 1) { u.set_text(id + ".pct", std::to_string(pct) + "%"); if (P.arc[i]) lv_arc_set_value(P.arc[i], pct); }
        std::string k, a, d;
        step_lines(v.steps[i], k, a, d);
        u.set_text(id + ".k", k); u.set_text(id + ".a", a); u.set_text(id + ".d", d);
        u.set_text_color(id + ".k", stt == 1 ? 0x00559C : stt == 2 ? 0x8792A0 : 0x1F2A35);
        u.set_text_color(id + ".a", stt == 2 ? 0x8792A0 : 0x5A6675);
        if (lv_obj_t* o = u.box(id)) if (u.changed("sel" + std::to_string(i), i == v.sel)) {
            bg(o, 0xFFFFFF, i == v.sel ? 255 : 0);
            ol(o, 0x00559C, i == v.sel ? 255 : 0, 2 * UI_SCALE);
            lv_obj_set_style_outline_pad(o, -2 * UI_SCALE, 0);        // 框画在格子里面，不往外占地方
        }
    }
    // 「添加」永远跟在最后一步后面
    const bool can_add = n < 6 && !locked;
    u.show("sq.add", can_add);
    if (can_add) if (lv_obj_t* add = u.box("sq.add")) if (lv_obj_t* slot = u.box("sq." + std::to_string(n))) lv_obj_set_x(add, lv_obj_get_x(slot) - 8 * UI_SCALE);

    // 参数区：只显示选中那一步对应的一块
    const int show_part = n > 0 ? part_of(v.steps[v.sel]) : -1;
    for (int p = 0; p < kParts; p++) if (P.scr[p]) {
        if (p == show_part) lv_obj_remove_flag(P.scr[p], LV_OBJ_FLAG_HIDDEN); else lv_obj_add_flag(P.scr[p], LV_OBJ_FLAG_HIDDEN);
    }
    if (show_part >= 0 && P.ui[show_part])
        seq_fill_part(*P.ui[show_part], v.steps[v.sel], v.sel, !started ? 0 : v.sel < idx ? 2 : v.sel == idx ? 1 : 0, q.value("step_elapsed_s", 0.0));

    // 底栏左边：一句话提示。右边几个键按状态只留该留的，从右往左靠齐
    const std::string note = q.value("note", "");
    const std::string tip = !v.msg.empty() ? v.msg : locked ? "已开始的序列不可增改" : (qs == "faulted" || qs == "aborted") && !note.empty() ? note : "";
    u.show("seq.chip", !tip.empty());
    lv_obj_t* chip = u.box("seq.chip"); lv_obj_t* chip2 = u.box("seq.chip2");
    if (chip && chip2) {
        if (!tip.empty()) {
            u.set_text("seq.chip", tip);
            if (lv_obj_t* lb = u.label("seq.chip")) { lv_obj_update_layout(lb); lv_obj_set_width(chip, lv_obj_get_width(lb) + 28 * UI_SCALE); lv_obj_set_x(lb, 14 * UI_SCALE); }
        }
        lv_obj_set_x(chip2, tip.empty() ? lv_obj_get_x(chip) : lv_obj_get_x(chip) + lv_obj_get_width(chip) + 8 * UI_SCALE);
    }
    struct { const char* id; bool on; } keys[] = {{"seq.stop", locked}, {"seq.start", !locked}, {"seq.confirm", qs == "waiting_confirm"}, {"seq.clear", !locked && n > 0}};
    static int right_edge[2] = {0, 0};
    if (lv_obj_t* stop = u.box("seq.stop")) { int& r = right_edge[cur == "Seq" ? 0 : 1]; if (r == 0) r = lv_obj_get_x(stop) + lv_obj_get_width(stop); int x = r;
        for (auto& k : keys) { u.show(k.id, k.on); if (!k.on) continue; if (lv_obj_t* b = u.box(k.id)) { x -= lv_obj_get_width(b); lv_obj_set_x(b, x); x -= 8 * UI_SCALE; } }
    }
}
