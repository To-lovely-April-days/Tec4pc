// 结晶曲线：查看和编辑。这个文件直接嵌在 main.cpp 的 App 里面（和 seq_page.inc 一样，只是为了不让 main.cpp 太长）。
//
// 两块都叠在设备控制页上：
//   查看 —— 控温方式选了「结晶」时盖在原来的温度控制卡上，显示曲线、各段、走到哪一段；
//   编辑 —— 点「编辑曲线」后盖住温度控制卡和搅拌卡。
// 编辑有两个来处：设备控制页自己的手动曲线，和任务序列里某个结晶步骤的曲线。
// 从任务序列进来的，点「完成 / 取消」会回到任务序列页；改的是那一步，不动手动曲线。
//
// 曲线本身以控制核心为准：手动曲线存在核心里（profile.set / profile.get，重启还在），
// 核心的状态里每次都带着「现在该看的那条」——正在按曲线控温时是正在走的那条（不管谁下发的），否则是存着的手动曲线。
// 所以界面重启、或者 PC 端下发的曲线，屏上一样看得到各段。

static constexpr int kCryMax = 12;          // 屏上一共 12 行，曲线最多 12 段
double cry_t0[2] = {25, 25};                // 曲线起点温度：没在跑时跟着釜温；跑起来以后用核心记下的开控那一刻的温度
std::string cry_msg[2];
struct CryEdit { bool on = false; int ci = 0; bool from_seq = false; int step = 0; json prof = json::array(); } ced;

struct CryParts { bool built = false; lv_obj_t* scr[2] = {}; std::unique_ptr<Ui> ui[2]; };       // 0 查看  1 编辑
std::map<std::string, CryParts> cry_parts;
struct Curve { std::string key; std::vector<lv_point_precise_t> all, done; };
std::map<lv_obj_t*, Curve> curves;           // 每个画曲线的框一份：线的坐标要一直留着，LVGL 的线只是指过来用

static std::string tnum(double v) { return std::fabs(v - std::round(v)) < 0.05 ? f0(v) : f1(v); }
static double prof_minutes(const json& p) { double m = 0; for (auto& g : p) m += g.value("minutes", 0.0); return m; }

// 在一个空框里画曲线：横轴时间（分钟）、纵轴温度。done_min >= 0 时，走过的那一段用深色再描一遍
void draw_curve(lv_obj_t* box, const json& prof, double t0, double done_min) {
    if (!box) return;
    char kb[64]; std::snprintf(kb, sizeof kb, "/%.0f/%.1f", t0, done_min < 0 ? -1.0 : std::floor(done_min * 2) / 2);
    Curve& cv = curves[box];
    const std::string key = prof.dump() + kb;
    if (key == cv.key) return;               // 没变就不重画
    cv.key = key;
    lv_obj_clean(box);
    cv.all.clear(); cv.done.clear();
    const int S = UI_SCALE, W_ = lv_obj_get_width(box), H_ = lv_obj_get_height(box);
    const int px = 36 * S, py = 6 * S, pw = W_ - px - 10 * S, ph = H_ - py - 24 * S;
    lv_obj_t* plot = mk(box, px, py, pw, ph, 0);
    bg(plot, 0xFFFFFF, 255);
    if (prof.empty()) { txt(box, px + pw / 2, py + ph / 2, "还没有曲线", 400, 14, 0x8792A0, 255, 0, 0); bd(plot, 0xD5DEE8, 255, S, LV_BORDER_SIDE_FULL); return; }
    const double total = std::max(1.0, prof_minutes(prof));
    double lo = t0, hi = t0;
    for (auto& g : prof) { lo = std::min(lo, g.value("target", 0.0)); hi = std::max(hi, g.value("target", 0.0)); }
    const double ystep = hi - lo > 100 ? 50 : 25;
    lo = std::floor(lo / ystep) * ystep; hi = std::ceil(hi / ystep) * ystep;
    if (hi - lo < ystep) hi = lo + ystep;
    static const double nice[] = {5, 10, 15, 30, 60, 120, 240, 480};
    double xstep = nice[7];
    for (double n : nice) if (total / n <= 4.5) { xstep = n; break; }
    auto X = [&](double t) { return static_cast<int>(t / total * pw + 0.5); };
    auto Y = [&](double v) { return static_cast<int>((hi - v) / (hi - lo) * ph + 0.5); };
    for (double v = lo; v <= hi + 1e-6; v += ystep) {
        if (v > lo && v < hi) bg(mk(plot, 0, Y(v), pw, S, 0), 0xE6ECF2, 255);
        txt(box, px - 16 * S, py + Y(v), tnum(v).c_str(), 400, 12, 0x8792A0, 255, 0, 0);
    }
    for (double t = 0; t <= total + 1e-6; t += xstep) {
        if (t > 0 && X(t) < pw - S) bg(mk(plot, X(t), 0, S, ph, 0), 0xE6ECF2, 255);
        txt(box, px + X(t), py + ph + 12 * S, f0(t).c_str(), 400, 12, 0x8792A0, 255, 0, 0);
    }
    // 坐标轴：左边和下边各一道
    bg(mk(plot, 0, 0, S, ph, 0), 0xC3CCD6, 255);
    bg(mk(plot, 0, ph - S, pw, S, 0), 0xC3CCD6, 255);
    double t = 0, v = t0;
    cv.all.push_back({static_cast<lv_value_precise_t>(X(0)), static_cast<lv_value_precise_t>(Y(v))});
    if (done_min >= 0) cv.done.push_back(cv.all.back());
    for (auto& g : prof) {
        const double t1 = t + g.value("minutes", 0.0), v1 = g.value("target", 0.0);
        cv.all.push_back({static_cast<lv_value_precise_t>(X(t1)), static_cast<lv_value_precise_t>(Y(v1))});
        if (done_min >= t1) cv.done.push_back(cv.all.back());
        else if (done_min > t && t1 > t) { const double f = (done_min - t) / (t1 - t); cv.done.push_back({static_cast<lv_value_precise_t>(X(done_min)), static_cast<lv_value_precise_t>(Y(v + (v1 - v) * f))}); }
        t = t1; v = v1;
    }
    auto line = [&](const std::vector<lv_point_precise_t>& pts, uint32_t color) {
        if (pts.size() < 2) return;
        lv_obj_t* l = lv_line_create(plot);
        lv_line_set_points(l, pts.data(), static_cast<uint32_t>(pts.size()));
        lv_obj_set_style_line_width(l, static_cast<int>(2.4 * S), 0);
        lv_obj_set_style_line_color(l, lv_color_hex(color), 0);
        lv_obj_set_style_line_rounded(l, true, 0);
    };
    // 有「走过的一段」时，整条先用浅色画，走过的再用深色描；没有就整条深色
    line(cv.all, done_min >= 0 ? 0xB5C7DB : 0x00559C);
    if (done_min >= 0) line(cv.done, 0x00559C);
}

void cry_build_parts(const std::string& page, lv_obj_t* scr) {
    CryParts& P = cry_parts[page];
    if (P.built) return;
    P.built = true;
    static const char* names[2] = {"CryViewPart", "CryEditPart"};
    for (int p = 0; p < 2; p++) {
        P.scr[p] = lv_obj_create(scr);
        lv_obj_remove_style_all(P.scr[p]);
        lv_obj_set_size(P.scr[p], W, H);
        lv_obj_remove_flag(P.scr[p], static_cast<lv_obj_flag_t>(LV_OBJ_FLAG_SCROLLABLE | LV_OBJ_FLAG_CLICKABLE));
        lv_obj_add_flag(P.scr[p], LV_OBJ_FLAG_HIDDEN);
        P.ui[p] = std::make_unique<Ui>();
        P.ui[p]->on_action = [this](const std::string& a) { action(a); };
        for (const PageDef& d : kPages) if (std::string(d.name) == names[p]) { d.build(P.scr[p], *P.ui[p]); lv_obj_update_layout(P.scr[p]); }
        // 这张卡是盖在原来的卡片上面的：让它自己接住点击，不然点到空白处会点中底下被盖住的键
        for (uint32_t k = 0; k < lv_obj_get_child_count(P.scr[p]); k++) lv_obj_add_flag(lv_obj_get_child(P.scr[p], k), LV_OBJ_FLAG_CLICKABLE);
        // 这一块的卡片和底下原来的卡片大小、位置不一样，缝里会露出底下的卡片边：先垫一块和页面同色的底把那片区域盖住
        if (lv_obj_get_child_count(P.scr[p]) > 0) {
            lv_obj_t* first = lv_obj_get_child(P.scr[p], 0);
            const int x0 = lv_obj_get_x(first) - 4 * UI_SCALE, y0 = lv_obj_get_y(first) - 4 * UI_SCALE;
            lv_obj_t* pad = mk(P.scr[p], x0, y0, W - x0, 716 * UI_SCALE - y0, 0);
            bg(pad, 0xEEF1F5, 255);
            lv_obj_add_flag(pad, LV_OBJ_FLAG_CLICKABLE);
            lv_obj_move_to_index(pad, 0);
        }
    }
}

// 这个通道现在该显示哪条曲线：核心状态里带着的那条（[{target, minutes}…]）
static const json& cry_shown(const json& c) {
    static const json none = json::array();
    return c.contains("profile") && c["profile"].contains("segments") && c["profile"]["segments"].is_array() ? c["profile"]["segments"] : none;
}

void refresh_cry(const json& c, int ci) {
    auto pg = pages.find(cur);
    cry_build_parts(cur, pg->second.scr);
    CryParts& P = cry_parts[cur];
    const bool editing = ced.on && ced.ci == ci;
    const bool run = c.value("running", false) && c.value("mode", "off") == "crystal";
    const bool view = !editing && mode[ci] == 3;
    auto vis = [](lv_obj_t* o, bool on) { if (on) lv_obj_remove_flag(o, LV_OBJ_FLAG_HIDDEN); else lv_obj_add_flag(o, LV_OBJ_FLAG_HIDDEN); };
    vis(P.scr[0], view); vis(P.scr[1], editing);
    const double tr = c["tr"].is_number() ? c["tr"].get<double>() : c.value("tj", 25.0);
    cry_t0[ci] = run && c.contains("profile") && c["profile"].value("active", false) ? c["profile"].value("start", tr) : tr;

    if (view) {
        Ui& u = *P.ui[0];
        const json& prof = cry_shown(c);
        const int n = std::min(kCryMax, static_cast<int>(prof.size()));
        const double total = prof_minutes(prof);
        u.set_text_raw("cry.sum", n == 0 ? ui::tr("还没有曲线，点「编辑曲线」添加")
                                  : ui::fmt("釜内 Tr · 共 {0} 段 · {1} · {2} → {3} ℃", {std::to_string(n), dur_text(total), tnum(cry_t0[ci]), tnum(prof[n - 1].value("target", 0.0))}));
        const int seg = !run ? -1 : c.value("profile_done", false) ? n : std::min(n - 1, c.value("segment", 0));
        double done_min = -1;
        if (run && n > 0) {
            done_min = 0;
            for (int k = 0; k < n && k < seg; k++) done_min += prof[k].value("minutes", 0.0);
            if (seg < n) done_min += std::max(0.0, prof[seg].value("minutes", 0.0) - c.value("segment_remaining_s", 0.0) / 60.0);
        }
        if (!cry_msg[ci].empty()) { u.set_text("cv.seg", cry_msg[ci]); u.set_text("cv.desc", ""); u.set_text("cv.left", ""); }
        // 正在按曲线控温但状态里没带曲线：只会在连着旧版控制核心时出现，说明一下，别去取不存在的段
        else if (run && n == 0) { u.set_text("cv.seg", "正在按曲线控温"); u.set_text_raw("cv.desc", ui::fmt("给定 {0} ℃", {f1(c.value("sp_now", 0.0))})); u.set_text("cv.left", ""); }
        else if (!run) { u.set_text("cv.seg", n == 0 ? "没有曲线" : "未开始"); u.set_text("cv.desc", n == 0 ? "" : "点右边的圆键开始"); u.set_text("cv.left", ""); }
        else if (seg >= n) { u.set_text("cv.seg", "曲线走完了"); u.set_text_raw("cv.desc", ui::fmt("保持在 {0} ℃", {tnum(c.value("sp", 0.0))})); u.set_text("cv.left", ""); }
        else {
            u.set_text_raw("cv.seg", ui::fmt("第 {0} / {1} 段", {std::to_string(seg + 1), std::to_string(n)}));
            u.set_text_raw("cv.desc", ui::fmt("到 {0} ℃ · 给定 {1} ℃", {tnum(prof[seg].value("target", 0.0)), f1(c.value("sp_now", 0.0))}));
            u.set_text_raw("cv.left", ui::fmt("本段剩 {0}", {dur_text(std::ceil(c.value("segment_remaining_s", 0.0) / 60.0))}));
        }
        // 各段的进度条：每段的宽度按它的用时占总时长的比例
        if (lv_obj_t* bar = u.box("cv.pbar")) {
            const int gap = 3 * UI_SCALE, bw = lv_obj_get_width(bar) - gap * std::max(0, n - 1);
            double acc = 0;
            for (int k = 0; k < kCryMax; k++) {
                const std::string id = "cv.p." + std::to_string(k);
                u.show(id, k < n);
                lv_obj_t* o = u.box(id);
                if (!o || k >= n) continue;
                const double d = prof[k].value("minutes", 0.0);
                const int x0 = static_cast<int>(acc / std::max(1.0, total) * bw) + k * gap, x1 = static_cast<int>((acc + d) / std::max(1.0, total) * bw) + k * gap;
                lv_obj_set_x(o, x0); lv_obj_set_width(o, std::max(2, x1 - x0));
                const int stt = k < seg ? 2 : k == seg ? 1 : 0;
                if (u.changed(id, stt)) bg(o, stt == 2 ? 0x00559C : stt == 1 ? 0x8FB4D9 : 0xC9D6E5, 255);
                acc += d;
            }
        }
        for (int k = 0; k < kCryMax; k++) {
            const std::string id = "cv.r." + std::to_string(k);
            u.show(id, k < n);
            if (k >= n) continue;
            const double T = prof[k].value("target", 0.0), d = prof[k].value("minutes", 0.0), prev = k == 0 ? cry_t0[ci] : prof[k - 1].value("target", 0.0);
            u.set_text(id + ".t", tnum(T) + " ℃");
            u.set_text(id + ".d", f0(d) + " min");
            u.set_text_raw(id + ".c", std::fabs(T - prev) < 0.05 || d <= 0 ? ui::tr("保温")
                                      : ui::fmt(T < prev ? "降 {0}" : "升 {0}", {f2(std::fabs(T - prev) / d)}));
            const int stt = k < seg ? 2 : k == seg ? 1 : 0;
            u.show(id + ".cur", stt == 1);
            if (lv_obj_t* o = u.box(id)) if (u.changed(id + "bg", stt)) bg(o, 0xE3EEFA, stt == 1 ? 255 : 0);
            const uint32_t col = stt == 2 ? 0x8792A0 : 0x1F2A35;
            u.set_text_color(id + ".n", col); u.set_text_color(id + ".t", col); u.set_text_color(id + ".d", col);
        }
        draw_curve(u.box("cv.chart"), prof, cry_t0[ci], done_min);
        refresh_stir(u, c);
        // 跑着的时候不能改曲线：键变淡
        const bool any_run = c.value("running", false);
        if (lv_obj_t* e = u.box("cv.edit")) if (u.changed("edit", any_run)) lv_obj_set_style_opa(e, any_run ? 115 : 255, 0);
        u.set_text("ctl.run.t1", any_run ? "控温中" : "未控温");
        u.set_text("ctl.run.t2", any_run ? "点按停止" : "点按启动");
        u.set_text_color("ctl.run.t1", any_run ? 0x0A8A4F : 0x5A6675);
        fit_run_btn(u);
        if (lv_obj_t* ic = u.box("ctl.run.icon")) if (u.changed("ctl.run.icon", any_run)) { lv_obj_set_style_image_recolor(ic, lv_color_hex(0x8792A0), 0); lv_obj_set_style_image_recolor_opa(ic, any_run ? 0 : 255, 0); }      // 没控温时圆是白的，白图标染成灰的
        if (lv_obj_t* o = u.box("ctl.run.circle")) if (u.changed("ctl.run.circle", any_run)) { bg(o, any_run ? 0x00B368 : 0xFFFFFF, 255); bd(o, any_run ? 0x00B368 : 0xC3CCD6, 255, 3 * UI_SCALE, LV_BORDER_SIDE_FULL); ol(o, 0x00B368, any_run ? 46 : 0, 5 * UI_SCALE); }
    }
    if (editing) {
        Ui& u = *P.ui[1];
        const json& prof = ced.prof;
        const int n = static_cast<int>(prof.size());
        const std::string whose = ced.from_seq ? ui::fmt("任务序列第 {0} 步", {std::to_string(ced.step + 1)}) : ui::tr("釜内 Tr");
        u.set_text_raw("cry.sum", n == 0 ? ui::tr("还没有曲线")
                                  : ui::fmt("{0} · 共 {1} 段 · {2} · {3} → {4} ℃", {whose, std::to_string(n), dur_text(prof_minutes(prof)), tnum(cry_t0[ci]), tnum(prof[n - 1].value("target", 0.0))}));
        u.set_text_raw("ce.count", ui::fmt("点格子改温度和用时，变化看右边曲线　·　已有 {0} 段（最多 {1} 段）", {std::to_string(n), std::to_string(kCryMax)}));
        for (int k = 0; k < kCryMax; k++) {
            const std::string id = "ce.r." + std::to_string(k);
            u.show(id, k < n);
            if (k >= n) continue;
            u.set_text(id + ".t", tnum(prof[k].value("target", 0.0)));
            u.set_text(id + ".d", f0(prof[k].value("minutes", 0.0)));
        }
        draw_curve(u.box("ce.chart"), prof, cry_t0[ci], -1);
    }
}

void cry_open_editor(int ci, const json& prof, bool from_seq, int step) {
    ced.on = true; ced.ci = ci; ced.from_seq = from_seq; ced.step = step;
    ced.prof = prof.is_array() ? prof : json::array();
    while (static_cast<int>(ced.prof.size()) > kCryMax) ced.prof.erase(ced.prof.size() - 1);
    cry_msg[ci].clear();
}
void cry_close_editor() {
    const bool back = ced.from_seq;
    ced.on = false;
    if (back) { ch_tab = 2; show(channel_page()); } else refresh();
}

bool cry_action(const std::string& a) {
    const int ci = channel - 1;
    if (a == "cry:edit") {
        if (!st.is_object() || !st.contains("ch")) return true;
        if (st["ch"][ci].value("running", false)) { cry_msg[ci] = "正在控温，先停止再改曲线"; refresh(); return true; }
        cry_open_editor(ci, cry_shown(st["ch"][ci]), false, 0);
        refresh();
        return true;
    }
    if (a == "det:cry") {
        // 从任务序列的结晶步骤进来：编辑器在设备控制页上，先切过去，改完再回来
        SeqView& v = sv[ci];
        if (v.steps.empty() || v.steps[v.sel].value("kind", "") != "crystal") return true;
        if (seq_locked(ci)) { v.msg = "已开始的序列不可增改"; refresh(); return true; }
        cry_open_editor(ci, v.steps[v.sel].value("profile", json::array()), true, v.sel);
        ch_tab = 0; show(channel_page());
        return true;
    }
    if (a.rfind("ce:", 0) != 0) return false;
    if (!ced.on) return true;
    json& p = ced.prof;
    const int n = static_cast<int>(p.size());
    if (a == "ce:add") {
        // 新的一段先照上一段的温度、10 分钟：等于接一段保温，用户再改
        if (n < kCryMax) p.push_back({{"target", n > 0 ? p[n - 1].value("target", 20.0) : std::round(cry_t0[ced.ci])}, {"minutes", 10.0}});
        refresh();
    } else if (a == "ce:cancel") cry_close_editor();
    else if (a == "ce:ok") {
        if (ced.from_seq) {
            SeqView& v = sv[ced.ci];
            if (ced.step < static_cast<int>(v.steps.size())) { v.steps[ced.step]["profile"] = p; seq_push(ced.ci); }
        } else {
            const json r = ask({{"cmd", "profile.set"}, {"ch", ced.ci + 1}, {"segments", p}});
            cry_msg[ced.ci] = r.value("ok", false) ? "" : r.value("error", std::string("曲线没存进去"));
            poll();
        }
        cry_close_editor();
    } else if (a.size() > 5) {
        const int k = std::atoi(a.c_str() + 5);
        if (k < 0 || k >= n) return true;
        if (a[3] == 'x') { p.erase(k); refresh(); }
        else {
            const bool temp = a[3] == 't';
            const double cur_v = p[k].value(temp ? "target" : "minutes", 0.0);
            open_keypad(ui::fmt(temp ? "第 {0} 段 · 目标温度" : "第 {0} 段 · 用时", {std::to_string(k + 1)}), temp ? "℃" : "min", cur_v, temp ? -40 : 1, temp ? 180 : 600,
                        (temp ? tnum(cur_v) : f0(cur_v)) + (temp ? " ℃" : " min"), "—",
                        [this, k, temp](double x) { if (k < static_cast<int>(ced.prof.size())) ced.prof[k][temp ? "target" : "minutes"] = x; refresh(); }, temp ? 1 : 0);
        }
    }
    return true;
}
