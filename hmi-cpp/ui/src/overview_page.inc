// 总览里的两页：「方案总览」和「曲线总览」。这个文件直接嵌在 main.cpp 的 App 里面。
//
// 方案总览：每个通道把它的序列画成一条「计划的设定温度」曲线——竖线分步，橙线是现在走到哪，
//   加料步骤不改温度，用一个标记立在曲线上，写明加多少、在几度加。点卡片进那个通道的任务序列页。
//   横轴是计划时间。有些步骤事先算不出要多久（「尽快」到温、搅拌、暂停确认），画的时候给它估一个宽度，
//   所以「现在」那条线是按「第几步 + 这一步走了几成」摆的，不是按钟点摆的。
// 曲线总览：两个通道的釜温、夹套温度画在同一张图上，数据就是通道趋势页用的那份。

double sc_t0[2] = {25, 25};          // 计划曲线的起点温度：序列没在跑时跟着釜温，跑起来以后定住
struct OvPlot { std::string key; std::deque<std::vector<lv_point_precise_t>> lines; };
std::map<lv_obj_t*, OvPlot> ovplots;

// 一步在计划曲线上占多少分钟、走完以后设定温度是多少。from = 这一步开始时的温度
static double plan_minutes(const json& s, double from, bool& guessed) {
    const std::string k = s.value("kind", "");
    guessed = false;
    const double m = step_minutes(s);
    if (m > 0) return m;
    guessed = true;
    if (k == "reactor" || k == "jacket") {
        const std::string ar = s.value("arrive", "fast");
        if (ar == "rate" && s.value("rate_k_per_min", 0.0) > 0) { guessed = false; return std::max(1.0, std::fabs(s.value("target", from) - from) / s.value("rate_k_per_min", 1.0)); }
        return std::max(5.0, std::fabs(s.value("target", from) - from) / 1.0);      // 「尽快」：按每分钟 1 ℃ 估个宽度
    }
    return 0.0;         // 搅拌、暂停、一次加入：不占时间，画的时候另给一点宽度
}

void draw_scheme(lv_obj_t* box, int ci, const json& steps, const json& q) {
    if (!box) return;
    const std::string qs = q.value("state", "idle");
    const bool on = qs == "running" || qs == "waiting_confirm", done = qs == "done";
    const int idx = done ? static_cast<int>(steps.size()) : q.value("index", 0);
    char kb[96]; std::snprintf(kb, sizeof kb, "/%s/%d/%.2f/%.0f", qs.c_str(), idx, std::round(q.value("step_progress", 0.0) * 50) / 50, sc_t0[ci]);
    OvPlot& op = ovplots[box];
    const std::string key = steps.dump() + kb;
    if (key == op.key) return;
    op.key = key;
    lv_obj_clean(box);
    op.lines.clear();
    const int S = UI_SCALE, BW = lv_obj_get_width(box), BH = lv_obj_get_height(box);
    const int L = 44 * S, R = 12 * S, T = 40 * S, B = 26 * S, pw = BW - L - R, ph = BH - T - B;
    lv_obj_t* plot = mk(box, L, T, pw, ph, 1);
    bg(plot, 0xFFFFFF, 255);
    const int n = static_cast<int>(steps.size());
    if (n == 0) { txt(box, L + pw / 2, T + ph / 2, "这个通道还没有编序列。点这里去编", 400, 15, 0x8792A0, 255, 0, 0); bd(plot, 0xC9D3DE, 255, S, LV_BORDER_SIDE_FULL); return; }

    // 先把每一步的起止时间和温度排出来
    struct Seg { double t0, t1, v0, v1; std::vector<std::pair<double, double>> mid; bool guessed; };
    std::vector<Seg> seg;
    double t = 0, v = sc_t0[ci], real_total = 0;
    for (auto& s : steps) { bool g; real_total += plan_minutes(s, v, g); const std::string k = s.value("kind", ""); if (k == "reactor" || k == "jacket") v = s.value("target", v); else if (k == "crystal" && s.contains("profile") && !s["profile"].empty()) v = s["profile"].back().value("target", v); }
    const double filler = std::max(2.0, real_total * 0.04);        // 不占时间的步骤给这么宽，不然在图上看不见
    v = sc_t0[ci];
    double lo = v, hi = v;
    for (auto& s : steps) {
        const std::string k = s.value("kind", "");
        Seg g; g.t0 = t; g.v0 = v;
        double m = plan_minutes(s, v, g.guessed);
        if (m <= 0) m = filler;
        if (k == "reactor" || k == "jacket") v = s.value("target", v);
        else if (k == "crystal" && s.contains("profile")) { double tt = t; for (auto& p : s["profile"]) { tt += p.value("minutes", 0.0); v = p.value("target", v); g.mid.push_back({tt, v}); lo = std::min(lo, v); hi = std::max(hi, v); } }
        t += m; g.t1 = t; g.v1 = v;
        lo = std::min(lo, v); hi = std::max(hi, v);
        seg.push_back(g);
    }
    const double total = std::max(1.0, t);
    lo -= 8; hi += 12;
    auto X = [&](double tt) { return static_cast<int>(tt / total * pw + 0.5); };
    auto Y = [&](double vv) { return static_cast<int>((hi - vv) / (hi - lo) * ph + 0.5); };
    auto label = [&](int cx, int cy, const std::string& s, uint32_t col, int weight = 400, float px = 12) { return txt(box, cx, cy, s.c_str(), weight, px, col, 255, 0, 0); };

    // 走过的那一段：底色淡淡地铺一层
    double now_t = -1;
    if (on && idx < n) now_t = seg[idx].t0 + (seg[idx].t1 - seg[idx].t0) * std::min(1.0, std::max(0.0, q.value("step_progress", 0.0)));
    if (done) now_t = total;
    if (now_t > 0) bg(mk(plot, 0, 0, X(now_t), ph, 0), 0x00559C, 22);
    // 横线和左边的温度刻度
    double ystep = 10;
    for (double st2 : {5.0, 10.0, 20.0, 50.0}) { ystep = st2; if ((hi - lo) / st2 <= 4.5) break; }
    for (double vv = std::ceil(lo / ystep) * ystep; vv < hi; vv += ystep) {
        bg(mk(plot, 0, Y(vv), pw, S, 0), 0xEDF1F5, 255);
        lv_obj_t* l = label(0, T + Y(vv), tnum(vv), 0x8792A0);
        lv_obj_set_x(l, L - 8 * S - label_w(l));
    }
    // 竖线分步 + 上面一行的步骤名（放得下就写全，放不下只写序号）
    for (int i = 0; i < n; i++) {
        const int x0 = X(seg[i].t0), x1 = X(seg[i].t1);
        if (i < n - 1) bg(mk(plot, x1, 0, S, ph, 0), 0xD5DEE8, 255);
        std::string k, a, d;
        step_lines(steps[i], k, a, d);
        if (steps[i].value("kind", "") == "dose") continue;       // 加料另外用标记画
        const int stt = (on || done) ? (i < idx ? 2 : i == idx ? 1 : 0) : 0;
        const int col_w = x1 - x0;
        const std::string full_s = std::to_string(i + 1) + " " + k + (a.empty() ? "" : " " + a);
        const std::string mid_s = std::to_string(i + 1) + " " + k;
        const std::string num_s = std::to_string(i + 1);
        lv_obj_t* sl = label(L + (x0 + x1) / 2, 14 * S, full_s, stt == 1 ? 0x00559C : stt == 2 ? 0x8792A0 : 0x4F5B67, stt == 1 ? 600 : 400, 14);
        if (label_w(sl) > col_w - 10 * S) {
            lv_label_set_text(sl, mid_s.c_str());
            if (label_w(sl) > col_w - 8 * S) lv_label_set_text(sl, num_s.c_str());
            lv_obj_set_x(sl, L + (x0 + x1 - label_w(sl)) / 2);
        }
    }
    // 计划曲线：整条先画浅色，走过的部分再用深色描
    std::vector<lv_point_precise_t> all, past;
    auto pt = [&](double tt, double vv) { return lv_point_precise_t{static_cast<lv_value_precise_t>(X(tt)), static_cast<lv_value_precise_t>(Y(vv))}; };
    all.push_back(pt(0, sc_t0[ci]));
    if (now_t > 0) past.push_back(all.back());
    double pt_t = 0, pt_v = sc_t0[ci];
    auto add = [&](double tt, double vv) {
        all.push_back(pt(tt, vv));
        if (now_t >= tt) past.push_back(all.back());
        else if (now_t > pt_t && tt > pt_t) past.push_back(pt(now_t, pt_v + (vv - pt_v) * (now_t - pt_t) / (tt - pt_t)));
        pt_t = tt; pt_v = vv;
    };
    for (auto& g : seg) { for (auto& m : g.mid) add(m.first, m.second); add(g.t1, g.v1); }
    // 先改写成「台阶」：现场那台机器上 LVGL 画不出斜线（见 ui_kit 的 stair_line）
    auto line = [&](const std::vector<lv_point_precise_t>& pts, uint32_t color, double width) {
        if (pts.size() < 2) return;
        op.lines.push_back(stair_line(pts));
        lv_obj_t* l = lv_line_create(plot);
        lv_line_set_points(l, op.lines.back().data(), static_cast<uint32_t>(op.lines.back().size()));
        lv_obj_set_style_line_width(l, static_cast<int>(width * S + 0.5), 0);
        lv_obj_set_style_line_color(l, lv_color_hex(color), 0);
    };
    line(all, 0x9FB2C8, 2.4);
    line(past, 0x00559C, 3.0);
    if (on && now_t >= 0) bg(mk(box, L + X(now_t) - S, T - 6 * S, static_cast<int>(2.5 * S), ph + 6 * S, 0), 0xF08C00, 255);     // 橙线：现在
    bd(mk(box, L, T, pw, ph, 0), 0xC9D3DE, 255, S, LV_BORDER_SIDE_FULL);
    // 加料的标记：一根小杆立在曲线上，顶上一个圈，旁边写加多少、在几度加
    for (int i = 0; i < n; i++) {
        if (steps[i].value("kind", "") != "dose") continue;
        const int xm = L + (X(seg[i].t0) + X(seg[i].t1)) / 2, ym = T + Y(seg[i].v1);
        const int top = std::max(T + 4 * S, ym - 60 * S);
        bg(mk(box, xm - S, top + 26 * S, static_cast<int>(1.5 * S + 0.5), std::max(0, ym - top - 26 * S), 0), 0x00559C, 255);
        lv_obj_t* dot = mk(box, xm - 4 * S, ym - 4 * S, 8 * S, 8 * S, 0); bg(dot, 0x00559C, 255); lv_obj_set_style_radius(dot, LV_RADIUS_CIRCLE, 0);
        lv_obj_t* ring = mk(box, xm - 13 * S, top, 26 * S, 26 * S, 0); bg(ring, 0xFFFFFF, 255); bd(ring, 0x00559C, 255, 2 * S, LV_BORDER_SIDE_FULL); lv_obj_set_style_radius(ring, LV_RADIUS_CIRCLE, 0);
        lv_obj_t* drop = mk(ring, 6 * S, 6 * S, 10 * S, 10 * S, 0); bg(drop, 0x00559C, 255); lv_obj_set_style_radius(drop, LV_RADIUS_CIRCLE, 0);
        std::string k, a, d;
        step_lines(steps[i], k, a, d);
        // 标记靠右边太近时字写在左边，免得伸出图外
        lv_obj_t* l1 = label(0, top + 8 * S, ui::fmt("{0} 加料 {1}", {std::to_string(i + 1), a}), 0x00559C, 600, 14);
        lv_obj_t* l2 = label(0, top + 25 * S, d.empty() ? ui::fmt("在 {0} ℃ 加", {f1(seg[i].v1)}) : ui::fmt("在 {0} ℃ 加 · {1}", {f1(seg[i].v1), d}), 0x4F5B67, 400, 13);
        const bool left = xm + std::max(label_w(l1), label_w(l2)) + 24 * S > L + pw;
        lv_obj_set_x(l1, left ? xm - 20 * S - label_w(l1) : xm + 20 * S);
        lv_obj_set_x(l2, left ? xm - 20 * S - label_w(l2) : xm + 20 * S);
    }
    // 下面的时间刻度
    static const double nice[] = {5, 10, 15, 30, 60, 120, 240};
    double xstep = 240;
    for (double s2 : nice) if (total / s2 <= 8) { xstep = s2; break; }
    for (double tt = 0; tt <= total + 1e-6; tt += xstep) label(L + X(tt), T + ph + 13 * S, tt == 0 ? "0" : dur_text(tt), 0x8792A0);
}

void refresh_scheme(Ui& u) {
    for (int n = 1; n <= 2; n++) {
        const int ci = n - 1;
        const json& c = st["ch"][ci];
        const json& q = c["seq"];
        const std::string qs = q.value("state", "idle"), id = "sc." + std::to_string(n);
        const bool on = qs == "running" || qs == "waiting_confirm";
        // 步骤和任务序列页用的是同一份：序列状态或步数变了才重新向核心要
        SeqView& v = sv[ci];
        const std::string key = qs + "/" + std::to_string(q.value("count", 0));
        if (key != v.key) { seq_fetch(ci); v.key = key; }
        if (!on && qs != "done") sc_t0[ci] = c["tr"].is_number() ? c["tr"].get<double>() : c.value("tj", 25.0);
        const int ns = static_cast<int>(v.steps.size());
        const std::string run = c.contains("run") && c["run"].is_string() ? c["run"].get<std::string>() : "";
        u.set_text(id + ".name", ns == 0 ? "还没有序列" : on && !run.empty() ? run : !v.name.empty() ? v.name : "未命名序列");
        // 总时长和图上的横轴用同一个算法（算不出时长的步骤按估的算），所以写「约」
        double total = 0, tv = sc_t0[ci];
        for (auto& s : v.steps) { bool g; total += plan_minutes(s, tv, g); const std::string k = s.value("kind", ""); if (k == "reactor" || k == "jacket") tv = s.value("target", tv); else if (k == "crystal" && s.contains("profile") && !s["profile"].empty()) tv = s["profile"].back().value("target", tv); }
        std::string sub = ns == 0 ? "" : ui::fmt("{0} 步", {std::to_string(ns)}) + (total > 0 ? ui::fmt(" · 共约 {0}", {dur_text(total)}) : "");
        if (on || qs == "done") sub += ui::fmt(" · 已走 {0}", {dur_text(q.value("total_elapsed_s", 0.0) / 60.0)});
        u.set_text_raw(id + ".sub", sub);
        // 右上角的状态牌：字有长有短，牌子跟着变宽，右边缘不动
        const int code = qs == "running" ? 1 : qs == "waiting_confirm" ? 2 : qs == "done" ? 3 : 0;
        u.set_text_raw(id + ".badge", code == 1 ? ui::fmt("运行中 · 步骤 {0} / {1}", {std::to_string(q.value("index", 0) + 1), std::to_string(q.value("count", 0))})
                                      : ui::tr(code == 2 ? "等待确认" : code == 3 ? "已完成" : "未启动"));
        u.set_text_color(id + ".badge", code == 0 ? 0x4F5B67 : 0xFFFFFF);
        if (lv_obj_t* b = u.box(id + ".badge")) {
            if (u.changed(id + ".badge", code)) { bg(b, code == 1 ? 0x00559C : code == 2 ? 0xF08C00 : code == 3 ? 0x8792A0 : 0xE9EEF5, 255); }
            if (lv_obj_t* lb = u.label(id + ".badge")) {
                lv_obj_update_layout(lb);
                static int right[2] = {0, 0};
                if (right[ci] == 0) right[ci] = lv_obj_get_x(b) + lv_obj_get_width(b);
                const int w = lv_obj_get_width(lb) + 28 * UI_SCALE;
                lv_obj_set_width(b, w); lv_obj_set_x(b, right[ci] - w); lv_obj_set_x(lb, 14 * UI_SCALE);
            }
        }
        draw_scheme(u.box(id + ".chart"), ci, v.steps, q);
    }
}

// ── 曲线总览：两个通道四条线画在一张图上 ──
void draw_overview_trend(lv_obj_t* box) {
    if (!box) return;
    OvPlot& op = ovplots[box];
    char kb[96]; std::snprintf(kb, sizeof kb, "%zu/%zu/%.1f", tdat[0].t.size(), tdat[1].t.size(), tdat[0].t.empty() ? 0.0 : tdat[0].t.back());
    if (op.key == kb) return;
    op.key = kb;
    lv_obj_clean(box);
    op.lines.clear();
    const int S = UI_SCALE, BW = lv_obj_get_width(box), BH = lv_obj_get_height(box);
    const int L = 56 * S, R = 30 * S, T = 26 * S, B = 32 * S, pw = BW - L - R, ph = BH - T - B;
    lv_obj_t* plot = mk(box, L, T, pw, ph, 1);
    bg(plot, 0xFFFFFF, 255);
    auto label = [&](int cx, int cy, const std::string& s) { return txt(box, cx, cy, s.c_str(), 400, 12, 0x8792A0, 255, 0, 0); };
    const TrendData& a = tdat[0];
    if (a.t.size() < 2) { label(L + pw / 2, T + ph / 2, "正在等数据…"); bd(plot, 0xB8C4D0, 255, S, LV_BORDER_SIDE_FULL); return; }
    // 时间窗和通道趋势页同一个规矩：最多回看 120 分钟，窗口取 10 / 30 / 60 / 150 分钟里最小够用的，左边对齐到整刻度
    const double now = a.t.back(), start = std::max(a.t.front(), now - 120 * 60.0), origin = now - static_cast<double>(a.wall.back());
    double win = 150 * 60.0, x0 = start;
    for (double w : {10.0, 30.0, 60.0, 150.0}) { const double tick = w * 60.0 / 5.0; x0 = origin + std::floor((start - origin) / tick) * tick; win = w * 60.0; if (now - x0 <= 0.85 * win) break; }
    auto X = [&](double t) { return static_cast<int>((t - x0) / win * pw + 0.5); };
    double lo = 1e9, hi = -1e9;
    for (int c = 0; c < 2; c++) for (size_t i = 0; i < tdat[c].t.size(); i++) if (tdat[c].t[i] >= x0) for (double v : {tdat[c].tr[i], tdat[c].tj[i]}) if (std::isfinite(v)) { lo = std::min(lo, v); hi = std::max(hi, v); }
    if (lo > hi) { lo = 0; hi = 100; }
    if (hi - lo < 4) { const double m = (hi + lo) / 2; lo = m - 2; hi = m + 2; }
    double step = 20;
    for (double s2 : {1.0, 2.0, 5.0, 10.0, 20.0, 50.0}) { step = s2; if ((hi - lo) / s2 <= 5) break; }
    lo = std::floor(lo / step) * step; hi = std::ceil(hi / step) * step;
    auto Y = [&](double v) { return static_cast<int>((hi - v) / (hi - lo) * ph + 0.5); };
    const int xn = std::min(pw, X(now));
    if (xn < pw) bg(mk(plot, xn, 0, pw - xn, ph, 0), 0xF4F6F9, 255);
    const int ny = static_cast<int>(std::round((hi - lo) / step));
    for (int k = 0; k <= ny; k++) {
        const double v = lo + k * step;
        if (k > 0 && k < ny) bg(mk(plot, 0, Y(v), pw, S, 0), 0xDCE3EA, 255);
        lv_obj_t* l = label(0, T + Y(v), tnum(v)); lv_obj_set_x(l, L - 8 * S - label_w(l));
    }
    { lv_obj_t* l = label(0, T - 12 * S, "℃"); lv_obj_set_x(l, L - 8 * S - label_w(l)); }
    for (int k = 0; k <= 5; k++) {
        const int x = static_cast<int>(pw * k / 5.0);
        if (k > 0 && k < 5) bg(mk(plot, x, 0, S, ph, 0), 0xDCE3EA, 255);
        label(L + x, T + ph + 14 * S, clock_text(a.wall.back() - static_cast<long long>(std::llround(now - (x0 + win * k / 5.0)))));
    }
    struct Ser { int c; bool tj; uint32_t color; double width; const char* name; };
    const Ser SER[4] = {{1, true, 0x9ADAD2, 1.4, "Tj 通道2"}, {1, false, 0xF0A9A4, 1.4, "Tr 通道2"}, {0, true, 0x19B5A5, 1.6, "Tj 通道1"}, {0, false, 0xE5645A, 2.0, "Tr 通道1"}};
    struct Tag { int y; uint32_t color; std::string text; };
    std::vector<Tag> tags;
    for (const Ser& s : SER) {
        const TrendData& d = tdat[s.c];
        const std::vector<double>& v = s.tj ? d.tj : d.tr;
        std::vector<lv_point_precise_t> pts;
        int last_x = -1000000;
        auto flush = [&]() {
            if (pts.size() >= 2) {
                op.lines.push_back(stair_line(pts));        // 台阶，见 ui_kit 的 stair_line
                lv_obj_t* ln = lv_line_create(plot);
                lv_line_set_points(ln, op.lines.back().data(), static_cast<uint32_t>(op.lines.back().size()));
                lv_obj_set_style_line_width(ln, static_cast<int>(s.width * S + 0.5), 0);
                lv_obj_set_style_line_color(ln, lv_color_hex(s.color), 0);
            }
            pts.clear(); last_x = -1000000;
        };
        for (size_t i = 0; i < d.t.size(); i++) {
            if (d.t[i] < x0) continue;
            if (!std::isfinite(v[i])) { flush(); continue; }
            const int x = X(d.t[i]), y = Y(v[i]);
            if (x == last_x) pts.back().y = static_cast<lv_value_precise_t>(y); else pts.push_back({static_cast<lv_value_precise_t>(x), static_cast<lv_value_precise_t>(y)});
            last_x = x;
        }
        flush();
        if (!d.t.empty() && std::isfinite(v.back())) tags.push_back({Y(v.back()), s.color == 0x9ADAD2 ? 0x5FBFB4u : s.color == 0xF0A9A4 ? 0xD98F89u : s.color, ui::tr(s.name) + " " + f1(v.back())});
    }
    if (xn < pw) bg(mk(plot, xn, 0, S, ph, 0), 0x55616C, 255);
    bd(mk(box, L, T, pw, ph, 0), 0xB8C4D0, 255, S, LV_BORDER_SIDE_FULL);
    // 线头的读数标签：按高低排好，挨得太近的往下顺着排开
    std::sort(tags.begin(), tags.end(), [](const Tag& p, const Tag& q2) { return p.y < q2.y; });
    for (size_t i = 0; i < tags.size(); i++) { tags[i].y = std::max(11 * S, tags[i].y); if (i > 0 && tags[i].y < tags[i - 1].y + 24 * S) tags[i].y = tags[i - 1].y + 24 * S; }
    for (size_t i = tags.size(); i-- > 0;) { if (tags[i].y > ph - 11 * S) tags[i].y = ph - 11 * S; if (i + 1 < tags.size() && tags[i].y > tags[i + 1].y - 24 * S) tags[i].y = tags[i + 1].y - 24 * S; }
    for (const Tag& g : tags) {
        const int h = 22 * S;
        lv_obj_t* b = mk(box, L + xn + 8 * S, T + g.y - h / 2, 10 * S, h, 0);
        bg(b, g.color, 255); lv_obj_set_style_radius(b, 5 * S, 0);
        lv_obj_t* lb = txta(b, 8 * S, h / 2, 'l', g.text.c_str(), 600, 12.5f, 0xFFFFFF, 255, 0, 0);
        lv_obj_set_width(b, label_w(lb) + 16 * S);
    }
}

bool overview_action(const std::string& a) {
    if (a.rfind("sc:go:", 0) == 0) { channel = a[6] == '2' ? 2 : 1; ch_tab = 2; show(channel_page()); return true; }
    if (a.rfind("tab:", 0) == 0 && (cur == "Main" || cur == "Scheme" || cur == "Trends")) {
        static const char* pages3[3] = {"Main", "Scheme", "Trends"};
        const int k = a[4] - '0';
        if (k >= 0 && k < 3) show(pages3[k]);
        return true;
    }
    return false;
}
