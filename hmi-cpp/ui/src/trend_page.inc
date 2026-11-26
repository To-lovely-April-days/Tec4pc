// 通道的趋势曲线页。这个文件直接嵌在 main.cpp 的 App 里面（和 seq_page.inc 一样，只是为了不让 main.cpp 太长）。
//
// 数据在控制核心里：核心一直把最近 3 小时的读数留着（2 秒一点，不控温时也记）。
// 界面进这一页时先要一次全的，以后每 2 秒只要新增的那几点——所以界面重启、切走再回来，曲线都是连着的。
// 画法：每条线先按屏幕上的像素列抽稀（一列一个点），再交给 LVGL 画；读不到数的地方断开，不拿直线硬连。

struct TrendData {
    std::vector<double> t, tr, tj, tjsp, sp, ph, rpm;
    std::vector<long long> wall;
    std::vector<std::pair<double, std::string>> marks;      // 时间标记：哪一刻（核心的时间）、写的什么
    double run_start = std::nan("");        // 这一炉开始的时刻（核心的时间）；没有在跑的一炉时是空的
    std::chrono::steady_clock::time_point asked{};
};
TrendData tdat[2];
bool tr_on[2][6] = {{true, true, true, false, false, false}, {true, true, true, false, false, false}};      // 六条线各自开没开：Tr、Tj、Tj 设定、Tr−Tj、pH、转速
int tr_xax[2] = {0, 0}, tr_yax[2] = {0, 0};      // 横轴：0 时钟 1 实验时间；纵轴：0 全部 1 设定值附近
std::string tr_msg[2];
std::chrono::steady_clock::time_point tr_msg_at[2];      // 那句话是什么时候说的：留 6 秒再换回「窗口 … min」
void tr_say(int ci, const std::string& s) { tr_msg[ci] = s; tr_msg_at[ci] = std::chrono::steady_clock::now(); }

// 导出目录拷到 U 盘：插着就拷，返回要告诉操作人的那句话。数据导出页和趋势页的「导出快照」都用它。
// 触摸屏上没有文件管理器，数据只留在本机等于拿不走，所以「导出」这件事做到 U 盘上才算完
std::string copy_to_usb() {
    const json d = ask({{"cmd", "drives.list"}});
    if (!d.value("ok", false) || !d["drives"].is_array() || d["drives"].empty()) return ui::tr("没插 U 盘：已存在本机，插上再点一次");
    const json r = ask({{"cmd", "export.copy"}});
    if (!r.value("ok", false)) return ui::fmt("拷到 U 盘没成功：{0}", {core_text(r, "error", "不知道为什么")});
    std::string to = r.value("to", std::string());
    for (char& ch : to) if (ch == '\\') ch = '/';
    return ui::fmt("已拷到 U 盘 {0}（{1} 个文件）", {to, std::to_string(r.value("files", 0))});
}
struct TrendPlot { std::string key; std::deque<std::vector<lv_point_precise_t>> lines; };
std::map<lv_obj_t*, TrendPlot> tplots;          // 每个画图的框一份：线的坐标要一直留着

void trend_fetch(int ci) {
    TrendData& d = tdat[ci];
    const auto now = std::chrono::steady_clock::now();
    if (now - d.asked < std::chrono::milliseconds(1900)) return;       // 核心 2 秒才多一点，问勤了也没有新的
    d.asked = now;
    json q = {{"cmd", "trend.get"}, {"ch", ci + 1}};
    if (!d.t.empty()) q["after"] = d.t.back();
    const json r = ask(q);
    if (!r.value("ok", false) || !r.contains("t")) return;
    auto val = [](const json& a, size_t i) { return i < a.size() && a[i].is_number() ? a[i].get<double>() : std::nan(""); };
    // 核心重启过：它的时间从头数了，手上这些旧点接不上，全部丢掉重新要
    if (!d.t.empty() && r.value("now", d.t.back()) < d.t.back() - 1) { d = TrendData(); return; }
    for (size_t i = 0; i < r["t"].size(); i++) {
        d.t.push_back(val(r["t"], i)); d.wall.push_back(i < r["wall"].size() ? r["wall"][i].get<long long>() : 0);
        d.tr.push_back(val(r["tr"], i)); d.tj.push_back(val(r["tj"], i)); d.tjsp.push_back(val(r["tj_sp"], i));
        d.sp.push_back(val(r["sp"], i)); d.ph.push_back(val(r["ph"], i)); d.rpm.push_back(val(r["rpm"], i));
    }
    if (r.contains("marks") && r["marks"].is_array()) {      // 标记每次都是给全的（最多 200 个），直接换掉
        d.marks.clear();
        for (auto& m : r["marks"]) d.marks.emplace_back(m.value("t", 0.0), m.value("text", std::string()));
    }
    d.run_start = r.contains("run_start") && r["run_start"].is_number() ? r["run_start"].get<double>() : std::nan("");
    const size_t cap = 5400;
    if (d.t.size() > cap) {
        const size_t cut = d.t.size() - cap;
        for (auto* v : {&d.t, &d.tr, &d.tj, &d.tjsp, &d.sp, &d.ph, &d.rpm}) v->erase(v->begin(), v->begin() + static_cast<long>(cut));
        d.wall.erase(d.wall.begin(), d.wall.begin() + static_cast<long>(cut));
    }
}

static std::string clock_text(long long wall) {
    const std::time_t w = static_cast<std::time_t>(wall); std::tm tm{};
#ifdef _WIN32
    localtime_s(&tm, &w);
#else
    localtime_r(&w, &tm);
#endif
    char b[16]; std::strftime(b, sizeof b, "%H:%M:%S", &tm); return b;
}

void draw_trend(lv_obj_t* box, int ci, Ui& u) {
    if (!box) return;
    const TrendData& d = tdat[ci];
    TrendPlot& tp = tplots[box];
    char kb[112]; std::snprintf(kb, sizeof kb, "%zu/%zu/%.1f/%d%d/%d%d%d%d%d%d", d.t.size(), d.marks.size(), d.t.empty() ? 0.0 : d.t.back(), tr_xax[ci], tr_yax[ci],
                               tr_on[ci][0], tr_on[ci][1], tr_on[ci][2], tr_on[ci][3], tr_on[ci][4], tr_on[ci][5]);
    if (tp.key == kb) return;                   // 数据和选项都没变：不重画
    tp.key = kb;
    lv_obj_clean(box);
    tp.lines.clear();
    const int S = UI_SCALE, BW = lv_obj_get_width(box), BH = lv_obj_get_height(box);
    const int L = 56 * S, R = 46 * S, T = 26 * S, B = 32 * S, pw = BW - L - R, ph = BH - T - B;
    lv_obj_t* plot = mk(box, L, T, pw, ph, 1);
    bg(plot, 0xFFFFFF, 255);
    auto label = [&](int cx, int cy, const std::string& s, uint32_t col = 0x8792A0) { return txt(box, cx, cy, s.c_str(), 400, 12, col, 255, 0, 0); };
    auto label_r = [&](int xr, int cy, const std::string& s) { lv_obj_t* l = label(xr, cy, s); lv_obj_set_x(l, xr - label_w(l)); };
    auto label_l = [&](int xl, int cy, const std::string& s) { lv_obj_t* l = label(xl, cy, s); lv_obj_set_x(l, xl); };
    if (d.t.size() < 2) { label(L + pw / 2, T + ph / 2, "正在等数据…"); bd(plot, 0xB8C4D0, 255, S, LV_BORDER_SIDE_FULL); u.set_text("tr.win", "窗口 —"); return; }

    // 横轴：最多往回看 120 分钟；窗口按已有数据的长短取 10 / 30 / 60 / 150 分钟里最小够用的，右边留出至少两成空着（还没到的时间）
    // 左边界再往前挪到「整刻度」上：时钟轴挪到整分钟的倍数，实验时间轴挪到从这一炉开始算的整刻度，这样底下的刻度字是整数
    const double now = d.t.back(), start = std::max(d.t.front(), now - 120 * 60.0);
    const bool exp_axis = tr_xax[ci] == 1;
    const double origin = exp_axis ? (std::isfinite(d.run_start) ? d.run_start : d.t.front()) : now - static_cast<double>(d.wall.back());      // 刻度从哪一刻算起（核心的时间）
    double win = 150 * 60.0, x0 = start;
    for (double w : {10.0, 30.0, 60.0, 150.0}) {
        const double tick = w * 60.0 / 5.0;
        const double aligned = origin + std::floor((start - origin) / tick) * tick;
        win = w * 60.0; x0 = aligned;
        if (now - aligned <= 0.85 * win) break;
    }
    u.set_text_raw("tr.win", ui::fmt("窗口 {0} min", {f0(win / 60.0)}));
    size_t i0 = 0;
    while (i0 + 1 < d.t.size() && d.t[i0] < x0) i0++;
    auto X = [&](double t) { return static_cast<int>((t - x0) / win * pw + 0.5); };

    // 纵轴
    const bool zoom = tr_yax[ci] == 1;
    double lo = 1e9, hi = -1e9, step = 20, sp_last = std::nan("");
    for (size_t i = d.t.size(); i-- > i0;) if (std::isfinite(d.sp[i])) { sp_last = d.sp[i]; break; }
    if (zoom) {
        // 设定值附近：以目标为中心上下 0.6 ℃，看的就是 ±0.1 ℃ 那条带；没在控温就以现在的釜温为中心
        const double mid = std::isfinite(sp_last) ? sp_last : std::isfinite(d.tr.back()) ? std::round(d.tr.back() * 10) / 10 : d.tj.back();
        lo = mid - 0.6; hi = mid + 0.6; step = 0.2;
    } else {
        const std::vector<double>* src[3] = {&d.tr, &d.tj, &d.tjsp};
        for (int k = 0; k < 3; k++) if (tr_on[ci][k]) for (size_t i = i0; i < d.t.size(); i++) { const double v = (*src[k])[i]; if (std::isfinite(v)) { lo = std::min(lo, v); hi = std::max(hi, v); } }
        if (lo > hi) { lo = 0; hi = 100; }
        if (hi - lo < 4) { const double m = (hi + lo) / 2; lo = m - 2; hi = m + 2; }       // 太平的线别放大到满屏都是毛刺
        for (double s : {1.0, 2.0, 5.0, 10.0, 20.0, 50.0}) { step = s; if ((hi - lo) / s <= 5) break; }
        lo = std::floor(lo / step) * step; hi = std::ceil(hi / step) * step;
        if (hi <= lo) hi = lo + step;
    }
    auto Y = [&](double v) { return static_cast<int>((hi - v) / (hi - lo) * ph + 0.5); };

    // 还没到的时间：淡一点的底
    const int xn = std::min(pw, X(now));
    if (xn < pw) bg(mk(plot, xn, 0, pw - xn, ph, 0), 0xF4F6F9, 255);
    if (zoom) bg(mk(plot, 0, Y((lo + hi) / 2 + 0.1), pw, Y((lo + hi) / 2 - 0.1) - Y((lo + hi) / 2 + 0.1), 0), 0x00A862, 36);
    const int ny = static_cast<int>(std::round((hi - lo) / step));
    for (int k = 0; k <= ny; k++) {
        const double v = lo + k * step;
        if (k > 0 && k < ny) bg(mk(plot, 0, Y(v), pw, S, 0), 0xDCE3EA, 255);
        label_r(L - 8 * S, T + Y(v), zoom ? f1(v) : tnum(v));
    }
    label_r(L - 8 * S, T - 12 * S, "℃");
    for (int k = 0; k <= 5; k++) {
        const double t = x0 + win * k / 5.0;
        const int x = static_cast<int>(pw * k / 5.0);
        if (k > 0 && k < 5) bg(mk(plot, x, 0, S, ph, 0), 0xDCE3EA, 255);
        std::string s;
        if (tr_xax[ci] == 0) s = clock_text(d.wall.back() - static_cast<long long>(std::llround(now - t)));
        else { const double rel = t - origin; s = rel < -0.5 ? "" : hms(std::round(rel)); }      // 实验时间：从这一炉开始算；没有在跑的一炉就从手上最早的数据算
        if (!s.empty()) label(L + x, T + ph + 14 * S, s);
    }

    // 画一条线：按像素列抽稀；读不到数的地方断开
    auto series = [&](const std::vector<double>& v, uint32_t color, double width, const std::function<int(double)>& yf) {
        std::vector<lv_point_precise_t> cur;
        int last_x = -1000000;
        auto flush = [&]() {
            if (cur.size() >= 2) {
                tp.lines.push_back(stair_line(cur));        // 台阶，见 ui_kit 的 stair_line
                lv_obj_t* ln = lv_line_create(plot);
                lv_line_set_points(ln, tp.lines.back().data(), static_cast<uint32_t>(tp.lines.back().size()));
                lv_obj_set_style_line_width(ln, static_cast<int>(width * S + 0.5), 0);
                lv_obj_set_style_line_color(ln, lv_color_hex(color), 0);
            }
            cur.clear(); last_x = -1000000;
        };
        for (size_t i = i0; i < d.t.size(); i++) {
            if (!std::isfinite(v[i])) { flush(); continue; }
            const int x = X(d.t[i]), y = yf(v[i]);
            if (x == last_x) cur.back().y = static_cast<lv_value_precise_t>(y);       // 同一列里只留最后一个点
            else cur.push_back({static_cast<lv_value_precise_t>(x), static_cast<lv_value_precise_t>(y)});
            last_x = x;
        }
        flush();
    };
    const std::function<int(double)> yl = [&](double v) { return Y(v); };
    // 右边另有刻度的三条线：温差 −25…+25 K、pH 0…14、转速 0…1000 rpm，各按自己的范围铺满图高；右轴的刻度标的是其中开着的第一条
    struct RightAxis { int k; const char* unit; double lo, hi; uint32_t color; };
    const RightAxis RA[3] = {{3, "K", -25, 25, 0x6B5BD6}, {4, "pH", 0, 14, 0x00B368}, {5, "rpm", 0, 1000, 0x8A96A3}};
    std::vector<double> diff;
    bool axis_done = false;
    if (!zoom) for (const RightAxis& a : RA) {
        if (!tr_on[ci][a.k]) continue;
        const std::function<int(double)> yr = [&, a](double v) { return static_cast<int>((a.hi - v) / (a.hi - a.lo) * ph + 0.5); };
        if (a.k == 3) { diff.resize(d.t.size()); for (size_t i = 0; i < d.t.size(); i++) diff[i] = d.tr[i] - d.tj[i]; series(diff, a.color, 1.4, yr); }
        else series(a.k == 4 ? d.ph : d.rpm, a.color, 1.4, yr);
        if (!axis_done) {
            axis_done = true;
            for (int k = 0; k <= 4; k++) { const double v = a.lo + (a.hi - a.lo) * k / 4.0; label_l(L + pw + 6 * S, T + yr(v), tnum(v)); }
            label_l(L + pw + 6 * S, T - 12 * S, a.unit);
        }
    }
    if (zoom) {
        if (std::isfinite(sp_last)) {           // 目标线：水平的，可以画成虚线
            tp.lines.push_back({{0, static_cast<lv_value_precise_t>(Y(sp_last))}, {static_cast<lv_value_precise_t>(xn), static_cast<lv_value_precise_t>(Y(sp_last))}});
            lv_obj_t* ln = lv_line_create(plot);
            lv_line_set_points(ln, tp.lines.back().data(), 2);
            lv_obj_set_style_line_width(ln, S, 0); lv_obj_set_style_line_color(ln, lv_color_hex(0x55616C), 0);
            lv_obj_set_style_line_dash_width(ln, 6 * S, 0); lv_obj_set_style_line_dash_gap(ln, 4 * S, 0);
        }
        if (tr_on[ci][0]) series(d.tr, 0xE5645A, 2.0, yl);
    } else {
        if (tr_on[ci][2]) series(d.tjsp, 0xF2A33A, 1.2, yl);
        if (tr_on[ci][1]) series(d.tj, 0x19B5A5, 1.6, yl);
        if (tr_on[ci][0]) series(d.tr, 0xE5645A, 2.0, yl);
    }
    // 时间标记：一道紫色的虚竖线，顶上一个带序号的小牌子（序号是这次开机以来第几个标记）
    for (size_t k = 0; k < d.marks.size(); k++) {
        if (d.marks[k].first < x0 || d.marks[k].first > now) continue;
        const int x = X(d.marks[k].first);
        tp.lines.push_back({{static_cast<lv_value_precise_t>(x), 0}, {static_cast<lv_value_precise_t>(x), static_cast<lv_value_precise_t>(ph)}});
        lv_obj_t* ln = lv_line_create(plot);
        lv_line_set_points(ln, tp.lines.back().data(), 2);
        lv_obj_set_style_line_width(ln, S, 0); lv_obj_set_style_line_color(ln, lv_color_hex(0x7B5BD6), 0);
        lv_obj_set_style_line_dash_width(ln, 4 * S, 0); lv_obj_set_style_line_dash_gap(ln, 3 * S, 0);
        const std::string no = std::to_string(k + 1);
        const int w = static_cast<int>(no.size() * 8 + 10) * S, h = 16 * S;
        lv_obj_t* b = mk(box, L + x - w / 2, T - h - 2 * S, w, h, 0);
        bg(b, 0x7B5BD6, 255); lv_obj_set_style_radius(b, 4 * S, 0);
        txt(b, w / 2, h / 2, no.c_str(), 600, 11, 0xFFFFFF, 255, 0, 0);
    }
    // 「现在」那条竖线，和线头上的读数标签（两个标签挨得太近就上下错开）
    if (xn < pw) bg(mk(plot, xn, 0, S, ph, 0), 0x55616C, 255);
    bd(mk(box, L, T, pw, ph, 0), 0xB8C4D0, 255, S, LV_BORDER_SIDE_FULL);
    struct Tag { bool on; double v; uint32_t color; std::string text; int y; };
    Tag tags[2] = {{tr_on[ci][1] && !zoom && std::isfinite(d.tj.back()), d.tj.back(), 0x19B5A5, "Tj " + f1(d.tj.back()), 0},
                   {tr_on[ci][0] && std::isfinite(d.tr.back()), d.tr.back(), 0xE5645A, "Tr " + (zoom ? f2(d.tr.back()) : f1(d.tr.back())), 0}};
    for (Tag& g : tags) g.y = std::max(11 * S, std::min(ph - 11 * S, Y(g.v)));
    if (tags[0].on && tags[1].on && std::abs(tags[0].y - tags[1].y) < 24 * S) {
        const int mid = (tags[0].y + tags[1].y) / 2, up = tags[0].y <= tags[1].y ? 0 : 1;
        tags[up].y = mid - 12 * S; tags[1 - up].y = mid + 12 * S;
    }
    for (const Tag& g : tags) {
        if (!g.on) continue;
        const int h = 22 * S;
        lv_obj_t* b = mk(box, L + xn + 8 * S, T + g.y - h / 2, 10 * S, h, 0);
        bg(b, g.color, 255); lv_obj_set_style_radius(b, 5 * S, 0);
        lv_obj_t* lb = txta(b, 8 * S, h / 2, 'l', g.text.c_str(), 600, 12.5f, 0xFFFFFF, 255, 0, 0);
        lv_obj_set_width(b, label_w(lb) + 16 * S);
    }
}

bool trend_action(const std::string& a) {
    const int ci = channel - 1;
    if (a.rfind("tr:tog:", 0) == 0) { const int k = a[7] - '0'; if (k >= 0 && k < 6) tr_on[ci][k] = !tr_on[ci][k]; refresh(); return true; }
    if (a.rfind("xax:", 0) == 0) { tr_xax[ci] = a[4] - '0'; refresh(); return true; }
    if (a.rfind("yax:", 0) == 0) { tr_yax[ci] = a[4] - '0'; refresh(); return true; }
    if (a == "tr:mark") {
        // 时间标记：记下「现在」这一刻。核心在趋势图上竖一道线，这一炉在跑的话同时写进它的事件日志（导出能看到）
        const json r = ask({{"cmd", "mark.add"}, {"ch", ci + 1}});
        tr_say(ci, r.value("ok", false) ? ui::fmt("已加第 {0} 个时间标记", {std::to_string(r.value("count", 0))}) : core_text(r, "error", "标记没加上"));
        tdat[ci].asked = {};          // 马上重新要一次数据，标记线立刻出来
        refresh(); return true;
    }
    if (a == "tr:snap" || a == "tr:export") {
        // 拍快照：把图上这段数据（最近 3 小时）存成一个 CSV，放在本机导出目录的「快照」里；
        // 导出快照：再拍一张，并把导出目录拷到 U 盘
        const json r = ask({{"cmd", "trend.snapshot"}, {"ch", ci + 1}});
        if (!r.value("ok", false)) tr_say(ci, core_text(r, "error", "快照没存成"));
        else if (a == "tr:snap") tr_say(ci, "快照已存在本机（导出目录 / 快照）");
        else tr_say(ci, copy_to_usb());
        refresh(); return true;
    }
    return false;
}

void refresh_trend(Ui& u, const json& c, int ci) {
    trend_fetch(ci);
    const bool run = c.value("running", false);
    const bool has_tr = c["tr"].is_number();
    const double tj = c.value("tj", 0.0), tr = has_tr ? c["tr"].get<double>() : tj;
    u.set_text("tr.v.0", has_tr ? f1(tr) : "—");
    u.set_text("tr.v.1", f1(tj));
    u.set_text("tr.v.2", run ? f1(c.value("tj_sp", 0.0)) : "—");
    u.set_text("tr.v.3", has_tr ? f1(tr - tj) : "—");
    u.set_text("tr.v.4", c["ph"].is_number() ? f2(c["ph"].get<double>()) : "—");
    u.set_text("tr.v.5", f0(c["stir"].value("rpm", 0.0)));
    static const uint32_t colors[6] = {0xE5645A, 0x19B5A5, 0xF2A33A, 0x6B5BD6, 0x00B368, 0x8A96A3};
    for (int k = 0; k < 6; k++) {
        const std::string id = "tr.s." + std::to_string(k);
        const bool on = tr_on[ci][k];
        u.set_text_color("tr.v." + std::to_string(k), on ? 0x1D3557 : 0x8792A0);
        u.set_text_color(id + ".n", on ? 0x4F5B67 : 0x8792A0);
        if (!u.changed(id, on)) continue;
        if (lv_obj_t* o = u.box(id)) bd(o, on ? colors[k] : 0xDCE2E9, 255, 3 * UI_SCALE, LV_BORDER_SIDE_BOTTOM);
        if (lv_obj_t* o = u.box(id + ".dot")) bg(o, on ? colors[k] : 0xC3CCD6, 255);
    }
    set_radio(u, "xax", 2, tr_xax[ci]);
    set_radio(u, "yax", 2, tr_yax[ci]);
    draw_trend(u.box("tr.chart"), ci, u);
    if (!tr_msg[ci].empty()) {
        if (std::chrono::steady_clock::now() - tr_msg_at[ci] < std::chrono::seconds(6)) u.set_text("tr.win", tr_msg[ci]);
        else { tr_msg[ci].clear(); if (lv_obj_t* cb = u.box("tr.chart")) tplots[cb].key.clear(); }      // 话说完了：让图重画一次，把「窗口 … min」写回来
    }
}
