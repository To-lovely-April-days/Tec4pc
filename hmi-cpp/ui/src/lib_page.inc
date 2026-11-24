// 配方库页（顶栏「文件」）。这个文件直接嵌在 main.cpp 的 App 里面（和 seq_page.inc 一样）。
//
// 配方存在控制核心里（recipe.list / get / save / delete），这一页只是看和点：
// 左边列出来，点一个右边显示它的各步；可以装进通道 1 或通道 2（替换那个通道正在编的序列），可以删，
// 也可以把某个通道现在编着的序列存成一个新配方。PC 端存的配方这里一样看得到——列表每 3 秒重新要一次。

static constexpr int kLibRows = 7, kLibSteps = 6;
struct LibView {
    json list = json::array();       // [{name, steps, est_minutes, saved}…]，最近存的在前
    int page = 0;
    std::string sel;                 // 选中的配方名（按名字记，不按第几行：列表会变）
    json steps = json::array();      // 选中那个配方的各步
    std::string steps_of;            // steps 是哪个配方的
    bool del_armed = false;          // 「删除」点过一次了，再点一次才真删
    std::string msg;
    std::chrono::steady_clock::time_point asked{};
};
LibView lib;

void lib_fetch(bool force) {
    const auto now = std::chrono::steady_clock::now();
    if (!force && now - lib.asked < std::chrono::seconds(3)) return;
    lib.asked = now;
    const json r = ask({{"cmd", "recipe.list"}});
    if (!r.value("ok", false) || !r["recipes"].is_array()) return;
    lib.list = r["recipes"];
    bool found = false;
    for (auto& x : lib.list) if (x.value("name", "") == lib.sel) found = true;
    if (!found) { lib.sel = lib.list.empty() ? "" : lib.list[0].value("name", ""); lib.del_armed = false; }
    const int pages = std::max(1, (static_cast<int>(lib.list.size()) + kLibRows - 1) / kLibRows);
    lib.page = std::max(0, std::min(lib.page, pages - 1));
    if (lib.steps_of != lib.sel) {
        lib.steps = json::array();
        if (!lib.sel.empty()) { const json g = ask({{"cmd", "recipe.get"}, {"name", lib.sel}}); if (g.value("ok", false) && g["steps"].is_array()) lib.steps = g["steps"]; }
        lib.steps_of = lib.sel;
    }
}

bool lib_action(const std::string& a) {
    if (a == "nav:files" || a == "seq:lib") { lib.msg.clear(); lib.del_armed = false; show("Files"); return true; }
    if (a.rfind("lib:", 0) != 0) return false;
    const int n = static_cast<int>(lib.list.size());
    const int pages = std::max(1, (n + kLibRows - 1) / kLibRows);
    if (a != "lib:del") lib.del_armed = false;
    lib.msg.clear();
    if (a == "lib:prev") lib.page = std::max(0, lib.page - 1);
    else if (a == "lib:next") lib.page = std::min(pages - 1, lib.page + 1);
    else if (a.rfind("lib:sel:", 0) == 0) { const int i = lib.page * kLibRows + (a[8] - '0'); if (i < n) lib.sel = lib.list[i].value("name", ""); lib_fetch(true); }
    else if (a.rfind("lib:load:", 0) == 0) {
        const int ch = a[9] - '0', ci = ch - 1;
        if (lib.sel.empty()) lib.msg = "还没有配方";
        else if (seq_locked(ci)) lib.msg = "通道 " + std::to_string(ch) + " 的序列正在跑，结束了才能装";
        else {
            const json r = ask({{"cmd", "seq.load"}, {"ch", ch}, {"steps", lib.steps}});
            if (!r.value("ok", false)) lib.msg = r.value("error", std::string("没装进去"));
            else {
                // 装好了直接带到那个通道的任务序列页，让人马上看到装进去的是什么
                sv[ci].key.clear(); sv[ci].sel = 0; sv[ci].msg = "已装入配方「" + lib.sel + "」"; sv[ci].name = lib.sel;
                channel = ch; ch_tab = 2;
                poll();
                show(channel_page());
                return true;
            }
        }
    } else if (a == "lib:del") {
        if (lib.sel.empty()) lib.msg = "还没有配方";
        else if (!lib.del_armed) lib.del_armed = true;           // 删了找不回来：要点两次
        else {
            const json r = ask({{"cmd", "recipe.delete"}, {"name", lib.sel}});
            lib.msg = r.value("ok", false) ? "已删除「" + lib.sel + "」" : r.value("error", std::string("没删掉"));
            lib.del_armed = false; lib.sel.clear(); lib.steps_of = "\x01";
            lib_fetch(true);
        }
    } else if (a.rfind("lib:save:", 0) == 0) {
        const int ch = a[9] - '0';
        const json g = ask({{"cmd", "seq.get"}, {"ch", ch}});
        if (!g.value("ok", false) || !g["steps"].is_array() || g["steps"].empty()) lib.msg = "通道 " + std::to_string(ch) + " 现在没有编好的序列";
        else {
            // 起名字：默认给这条序列自己的名字，没有就留空让人填
            const json steps = g["steps"];
            open_text_kb("给这个配方起个名字", sv[ch - 1].name, 30, [this, steps](const std::string& name) {
                if (name.empty()) { lib.msg = "名字是空的，没存"; refresh(); return; }
                bool had = false;
                for (auto& x : lib.list) if (x.value("name", "") == name) had = true;
                const json r = ask({{"cmd", "recipe.save"}, {"name", name}, {"steps", steps}});
                if (r.value("ok", false)) { lib.msg = "已存为「" + name + "」" + (had ? "（覆盖了同名的旧配方）" : ""); lib.sel = name; lib.steps_of = "\x01"; lib.page = 0; }
                else lib.msg = r.value("error", std::string("没存进去"));
                lib_fetch(true);
                refresh();
            });
            return true;
        }
    }
    refresh();
    return true;
}

void refresh_lib(Ui& u) {
    lib_fetch(false);
    const int n = static_cast<int>(lib.list.size());
    const int pages = std::max(1, (n + kLibRows - 1) / kLibRows);
    u.set_text("lib.count", "配方库 · " + std::to_string(n) + " 个");
    u.show("lib.pager", pages > 1);
    u.set_text("lib.page", std::to_string(lib.page + 1) + " / " + std::to_string(pages));
    json cur_item;
    for (int i = 0; i < kLibRows; i++) {
        const std::string id = "lib.r." + std::to_string(i);
        const int k = lib.page * kLibRows + i;
        u.show(id, k < n);
        if (k >= n) continue;
        const json& x = lib.list[k];
        const bool on = x.value("name", "") == lib.sel;
        u.set_text(id + ".n", x.value("name", ""));
        // 「尽快到温」这类步骤事先算不出要多久；整个配方都算不出时就不写时长，免得写个「0 min」
        const double est = x.value("est_minutes", 0.0);
        u.set_text(id + ".k", std::to_string(x.value("steps", 0)) + " 步" + (est > 0 ? " · 约 " + dur_text(est) : ""));
        u.set_text(id + ".t", x.value("saved", ""));
        u.show(id + ".bar", on);
        if (lv_obj_t* o = u.box(id)) if (u.changed(id + "bg", on)) bg(o, on ? 0xE3EEFA : 0xFFFFFF, 255);
    }
    for (auto& x : lib.list) if (x.value("name", "") == lib.sel) cur_item = x;
    if (cur_item.is_object()) {
        u.set_text("lib.name", lib.sel);
        const double est = cur_item.value("est_minutes", 0.0);
        u.set_text("lib.sub", std::to_string(cur_item.value("steps", 0)) + " 步" + (est > 0 ? " · 预计约 " + dur_text(est) : "") + " · 保存于 " + cur_item.value("saved", ""));
    } else { u.set_text("lib.name", "配方库是空的"); u.set_text("lib.sub", "在任务序列页编好序列，再回来点下面的「存通道的序列」"); }
    const int ns = static_cast<int>(lib.steps.size());
    for (int i = 0; i < kLibSteps; i++) {
        const std::string id = "lib.s." + std::to_string(i);
        u.show(id, i < ns);
        if (i >= ns) continue;
        const json& s = lib.steps[i];
        std::string k, a, d;
        step_lines(s, k, a, d);
        const double stir = s.value("stir_rpm", -1.0), m = step_minutes(s);
        const std::string kind = s.value("kind", "");
        u.set_text(id + ".a", k);
        const bool timed = kind == "wait" || kind == "reflux" || kind == "crystal";       // 这几种第三行字就是时长，右边已经有了，不重复写
        u.set_text(id + ".b", d.empty() || timed ? a : a + " · " + d);
        u.set_text(id + ".c", kind != "stir" && stir >= 0 ? "搅拌 " + f0(stir) + " rpm" : "");
        u.set_text(id + ".d", m > 0 ? dur_text(m) : "");
    }
    // 两个「装入」键：那个通道的序列正在跑就变淡并写明
    for (int ch = 1; ch <= 2; ch++) {
        const std::string id = "lib.load" + std::to_string(ch);
        const bool busy = seq_locked(ch - 1);
        u.set_text(id + ".t", busy ? "通道 " + std::to_string(ch) + " 运行中" : "装入通道 " + std::to_string(ch));
        if (lv_obj_t* o = u.box(id)) if (u.changed(id, busy || !cur_item.is_object())) lv_obj_set_style_opa(o, busy || !cur_item.is_object() ? 115 : 255, 0);
    }
    u.set_text("lib.del.t", lib.del_armed ? "再点一次确认" : "删除");
    u.set_text("lib.info", !lib.msg.empty() ? lib.msg : lib.del_armed ? "删了找不回来。确定要删就再点一次「删除」" : "装入会替换该通道现在编着的全部步骤");
    u.set_text_color("lib.info", !lib.msg.empty() ? 0x00559C : lib.del_armed ? 0xD64545 : 0x8792A0);
}
