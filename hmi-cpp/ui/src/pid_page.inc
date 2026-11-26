// 「PID 整定」页。这个文件直接嵌在 main.cpp 的 App 里面。
//
// 这一页和最早的设计稿不一样，是照现在这套 C++ 控制核心改的：
//   · 增益表只有内环（夹套）用的那几项：Kp / Ki / Kd、稳态偏置、加热比。一张表，加热制冷、两个通道共用。
//   · 外环（釜温）不查表，是靠这一炉的实测现算的，所以没有「外环 Kp / Ki」可填；这里只把它此刻估出来的量摆出来看，
//     外加它唯一一个可调的：快速升温加码上限。
//   · 核心里没有自整定，也没有「不走表、手填一组参数」这回事，那两块就不放了。
//
// 改表是先在屏上改一份草稿，点「应用表格」才整张交给核心（gains.set：核心校验、存盘、马上换上）。
// 核心只在两个通道都停着的时候收；控着温的时候换 PID 参数，输出会跳。

static constexpr int kPidRows = 8;
struct PidView {
    json rows = json::array();       // 屏上这份（可能是改了还没应用的草稿）：[{temp, kp, ki, kd, steady_bias, heat_ratio}…]
    bool dirty = false, clear_armed = false;
    double look_t = 45, boost = 0;
    std::string msg, msg2;           // msg：表这边的话；msg2：外环那一块的话
    std::chrono::steady_clock::time_point asked{}, asked2{};
};
PidView pv;

void pid_fetch(bool force) {
    const auto now = std::chrono::steady_clock::now();
    if (!pv.dirty && (force || now - pv.asked > std::chrono::seconds(3))) {          // 有没应用的草稿时不拿核心的去盖它
        pv.asked = now;
        const json r = ask({{"cmd", "gains.get"}});
        if (r.value("ok", false) && r["rows"].is_array()) pv.rows = r["rows"];
    }
    if (force || now - pv.asked2 > std::chrono::seconds(5)) {
        pv.asked2 = now;
        const json r = ask({{"cmd", "settings.get"}});
        if (r.value("ok", false)) pv.boost = r.value("fast_boost_max", 0.0);
    }
}

// 在草稿表里按温度线性插值（和核心里的算法一样：两头之外取最近一行）。key 那一项有的行没填，就只在填了的行里插
static bool pid_interp(const json& rows, double t, const char* key, double& out) {
    std::vector<std::pair<double, double>> v;
    for (auto& r : rows) if (r.contains(key) && r[key].is_number()) v.push_back({r.value("temp", 0.0), r[key].get<double>()});
    if (v.empty()) return false;
    std::sort(v.begin(), v.end());
    if (v.size() == 1 || t <= v.front().first) { out = v.front().second; return true; }
    if (t >= v.back().first) { out = v.back().second; return true; }
    for (size_t i = 1; i < v.size(); i++) if (t <= v[i].first) {
        const double span = v[i].first - v[i - 1].first, f = span > 1e-9 ? (t - v[i - 1].first) / span : 0.0;
        out = v[i - 1].second + (v[i].second - v[i - 1].second) * f;
        return true;
    }
    out = v.back().second;
    return true;
}
static std::string f4(double v) { char b[32]; std::snprintf(b, sizeof b, "%.4f", v); return b; }
static std::string pid_gains_text(const json& rows, double t, bool with_ratio = true) {
    double kp = 0, ki = 0, kd = 0, hr = 0;
    if (!pid_interp(rows, t, "kp", kp)) return ui::tr("表是空的");
    pid_interp(rows, t, "ki", ki); pid_interp(rows, t, "kd", kd);
    std::string s = "Kp " + f2(kp) + " · Ki " + f4(ki) + " · Kd " + f1(kd);
    if (with_ratio && pid_interp(rows, t, "heat_ratio", hr)) s += ui::fmt(" · 加热比 {0}", {f2(hr)});
    return s;
}

bool pid_action(const std::string& a) {
    if (a.rfind("pg:", 0) != 0) return false;
    const int n = static_cast<int>(pv.rows.size());
    if (a != "pg:clear") pv.clear_armed = false;
    pv.msg.clear();
    const bool too_many = n > kPidRows;
    const bool edit = a.rfind("pg:cell:", 0) == 0 || a.rfind("pg:del:", 0) == 0 || a == "pg:add" || a == "pg:clear" || a == "pg:apply";
    if (edit && too_many) { pv.msg = ui::fmt("表里有 {0} 行，屏上只放得下 8 行，这张表请在 PC 端改", {std::to_string(n)}); refresh(); return true; }
    if (a.rfind("pg:cell:", 0) == 0 && a.size() >= 11) {
        const int k = a[8] - '0', i = a[10] - '0';
        if (k < 0 || k >= n || i < 0 || i > 5) return true;
        struct Col { const char* key; const char* title; const char* unit; double lo, hi; int dec; };
        static const Col C[6] = {{"temp", "这一行管的温度", "℃", -40, 200, 0}, {"kp", "Kp", "", 0.01, 100, 2}, {"ki", "Ki", "", 0, 1, 4}, {"kd", "Kd", "", 0, 2000, 1},
                                 {"steady_bias", "稳态偏置（维持温差的起点）", "K", -30, 60, 1}, {"heat_ratio", "加热比", "", 0.05, 20, 2}};
        const Col& c = C[i];
        const double cur_v = pv.rows[k].contains(c.key) && pv.rows[k][c.key].is_number() ? pv.rows[k][c.key].get<double>() : 0.0;
        char last[48]; std::snprintf(last, sizeof last, "%.*f %s", c.dec, cur_v, c.unit);
        const std::string key = c.key;
        open_keypad(ui::fmt("{0} ℃ 这一行 · {1}", {tnum(pv.rows[k].value("temp", 0.0)), ui::tr(c.title)}), c.unit, cur_v, c.lo, c.hi, last, "—",
                    [this, k, key](double x) { if (k < static_cast<int>(pv.rows.size())) { pv.rows[k][key] = x; pv.dirty = true; } refresh(); }, c.dec);
        return true;
    }
    if (a.rfind("pg:del:", 0) == 0) { const int k = a[7] - '0'; if (k >= 0 && k < n) { pv.rows.erase(k); pv.dirty = true; } }
    else if (a == "pg:add") {
        if (n >= kPidRows) pv.msg = "最多 8 行";
        else {
            // 新的一行先照最后一行抄一份、温度往上加 10 ℃，再改成要的数
            json r = n > 0 ? pv.rows[n - 1] : json{{"temp", 20.0}, {"kp", 9.4}, {"ki", 0.03}, {"kd", 180.0}, {"steady_bias", nullptr}, {"heat_ratio", nullptr}};
            if (n > 0) r["temp"] = r.value("temp", 0.0) + 10.0;
            pv.rows.push_back(r); pv.dirty = true;
        }
    } else if (a == "pg:apply") {
        if (!pv.dirty) pv.msg = "没有改动";
        else {
            const json r = ask({{"cmd", "gains.set"}, {"rows", pv.rows}});
            if (r.value("ok", false)) { pv.dirty = false; pv.msg = "已应用并存盘"; pid_fetch(true); }
            else pv.msg = core_text(r, "error", "没应用成");
        }
    } else if (a == "pg:undo") { pv.dirty = false; pid_fetch(true); pv.msg = "已放弃修改"; }
    else if (a == "pg:reload") {
        const json r = ask({{"cmd", "gains.reload"}});
        if (r.value("ok", false)) { pv.dirty = false; pid_fetch(true); pv.msg = "已从盘上重读"; } else pv.msg = core_text(r, "error", "没读成");
    } else if (a == "pg:clear") {
        if (!pv.clear_armed) pv.clear_armed = true;           // 清空要点两次
        else { pv.rows = json::array(); pv.dirty = true; pv.clear_armed = false; pv.msg = "屏上这份已清空；要重新加行再点「应用表格」，或点「放弃修改」找回来"; }
    } else if (a == "pg:look") {
        open_keypad("看某个温度会用什么增益", "℃", pv.look_t, -40, 200, tnum(pv.look_t) + " ℃", "—", [this](double x) { pv.look_t = x; refresh(); }, 0);
        return true;
    } else if (a == "pg:boost") {
        open_keypad("快速升温加码上限（0 = 不加码）", "K", pv.boost, 0, 30, f1(pv.boost) + " K", "—", [this](double x) {
            const json r = ask({{"cmd", "settings.set"}, {"fast_boost_max", x}});
            pv.msg2 = r.value("ok", false) ? "已保存，重启控制核心后生效" : core_text(r, "error", "没存进去");
            pid_fetch(true);
            refresh();
        }, 1);
        return true;
    }
    refresh();
    return true;
}

void refresh_pid(Ui& u, const json& c, int ci) {
    pid_fetch(false);
    trend_fetch(ci);
    const int n = static_cast<int>(pv.rows.size());
    const bool run = c.value("running", false);
    const double tj_sp = c.value("tj_sp", 0.0), duty = c.value("duty", 0.0);
    // 此刻用的是表里哪一行附近：离夹套目标最近的那一行
    int nearest = -1;      // 不能叫 near：Windows 的头文件把 near 定义成了一个空的宏
    if (run && !pv.dirty) { double best = 1e9; for (int k = 0; k < n; k++) { const double d = std::fabs(pv.rows[k].value("temp", 0.0) - tj_sp); if (d < best) { best = d; nearest = k; } } }
    for (int k = 0; k < kPidRows; k++) {
        const std::string id = "pg.r." + std::to_string(k);
        u.show(id, k < n);
        if (k >= n) continue;
        const json& r = pv.rows[k];
        auto opt = [&](const char* key, int dec) { if (!r.contains(key) || !r[key].is_number()) return std::string("—"); char b[32]; std::snprintf(b, sizeof b, "%.*f", dec, r[key].get<double>()); std::string s = b; if (s[0] == '-') s.replace(0, 1, "−"); return s; };
        const std::string cells[6] = {tnum(r.value("temp", 0.0)), f2(r.value("kp", 0.0)), f4(r.value("ki", 0.0)), f1(r.value("kd", 0.0)), opt("steady_bias", 1), opt("heat_ratio", 2)};
        for (int i = 0; i < 6; i++) {
            const std::string cid = id + ".c" + std::to_string(i);
            u.set_text(cid, cells[i]);
            if (lv_obj_t* box = u.box(cid)) if (lv_obj_t* lb = u.label(cid)) { lv_obj_update_layout(lb); lv_obj_set_width(box, lv_obj_get_width(lb) + 18 * UI_SCALE); }      // 小框跟着字的宽窄走
        }
        u.show(id + ".bar", k == nearest);
        if (lv_obj_t* o = u.box(id)) if (u.changed(id + "bg", k == nearest)) bg(o, 0xE3EEFA, k == nearest ? 255 : 0);
    }
    const std::string info = !pv.msg.empty() ? ui::tr(pv.msg) : pv.dirty ? ui::tr("有改动还没应用（点「应用表格」才生效）")
                           : nearest >= 0 ? ui::fmt("{0} 行 · 在用 {1} ℃ 附近", {std::to_string(n), tnum(pv.rows[nearest].value("temp", 0.0))})
                                          : ui::fmt("{0} 行", {std::to_string(n)});
    u.set_text_raw("pg.info", info);
    u.show("pg.sub", pv.msg.empty() && !pv.dirty);        // 右边有话要说时把左边那句小字收起来，不然两句会叠在一起
    u.set_text_color("pg.info", !pv.msg.empty() ? 0x00559C : pv.dirty ? 0xE07B00 : 0x8792A0);
    u.set_text("pg.clear.t", pv.clear_armed ? "再点一次确认" : "清空这张表");

    // 此刻在用
    if (run) {
        u.set_text_raw("pg.use.1", pid_gains_text(pv.rows, tj_sp));
        u.set_text_raw("pg.use.2", ui::fmt("夹套目标 {0} ℃ · 出力 {1} %（{2}）", {f1(tj_sp), f0(std::fabs(duty)), ui::tr(duty > 0 ? "TEC 制冷" : "加热棒")}));
    } else { u.set_text("pg.use.1", "没在控温"); u.set_text("pg.use.2", "开始控温以后，这里显示当时按夹套目标插值出来的那组增益"); }
    u.set_text("pg.look.t", tnum(pv.look_t));
    u.set_text_raw("pg.look.r", ui::fmt("℃ 会用：{0}", {pid_gains_text(pv.rows, pv.look_t, false)}));      // 这一行地方窄，只写三项增益

    // 稳定度：拿趋势数据里最近 5 分钟的釜温算
    const TrendData& d = tdat[ci];
    std::string s1 = ui::tr("数据还不够（要攒够半分钟）"), s2 = run ? "" : ui::tr("没在控温");
    if (d.t.size() >= 2) {
        const double now = d.t.back();
        double lo = 1e9, hi = -1e9, sum = 0; int cnt = 0; double first = now;
        for (size_t i = d.t.size(); i-- > 0 && now - d.t[i] <= 300.0;) if (std::isfinite(d.tr[i])) { lo = std::min(lo, d.tr[i]); hi = std::max(hi, d.tr[i]); sum += d.tr[i]; cnt++; first = d.t[i]; }
        if (cnt >= 2 && now - first >= 30.0) {
            const double span = now - first;
            const std::string win_s = span >= 290 ? ui::tr("近 5 min") : ui::fmt("近 {0} s", {f0(span)});
            s1 = ui::fmt("{0}：Tr 平均 {1} ℃，波动 ±{2} ℃", {win_s, f2(sum / cnt), f2((hi - lo) / 2)});
        }
    }
    if (run) s2 = ui::fmt("出力 {0} %，{1}", {f0(std::fabs(duty)), ui::tr(std::fabs(duty) >= 89.5 ? "已经顶到头了（再大也加不上去）" : "没有顶到头")});
    u.set_text_raw("pg.st.1", s1); u.set_text_raw("pg.st.2", s2);

    // 外环
    if (run) {
        u.set_text_raw("pg.out.1", ui::fmt("维持温差（估）{0} K　·　釜的时间常数（估）{1} s",
                                            {std::string(c.value("b_hat", 0.0) >= 0 ? "+" : "") + f1(c.value("b_hat", 0.0)), f0(c.value("tau_hat", 0.0))}));
        u.set_text_raw("pg.out.2", ui::fmt("快速升温此刻加码 {0} K　·　夹套目标 {1} ℃",
                                            {std::string(c.value("boost", 0.0) >= 0 ? "+" : "") + f1(c.value("boost", 0.0)), f1(tj_sp)}));
    } else { u.set_text("pg.out.1", "没在控温"); u.set_text("pg.out.2", "开始控温以后，这里显示外环此刻估出来的维持温差和时间常数"); }
    u.set_text("pg.boost", tnum(pv.boost));
    u.set_text("pg.boost.n", pv.boost > 0 ? "升温时夹套最多比稳妥算法多拉高这么多；0 = 不加码" : "0 = 不加码（稳妥，升温慢一些）");
    u.set_text("pg.msg", pv.msg2.empty() ? "改了以后要重启控制核心才生效" : pv.msg2);
    u.set_text_color("pg.msg", pv.msg2.empty() ? 0x8792A0 : 0x00559C);
}
