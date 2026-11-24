// 「数据导出」页。这个文件直接嵌在 main.cpp 的 App 里面。
//
// 每一炉的记录存在控制核心那边（runs.list 列出来，runs.export 导出）。这一页列出本机所有的炉次（两个通道的都在），
// 勾上要的、选好采样间隔，点「导出所选」；核心把每一炉导成三个 CSV（执行记录、事件日志、采样宽表）放进它数据目录下的 Exports 文件夹。
// 勾选是按炉次的编号记的，不按第几行：列表每 3 秒刷新一次，正在跑的那一炉时长在变、新的一炉会插进来。

static constexpr int kExpRows = 4;
struct ExportView {
    json runs = json::array();
    std::set<std::string> picked;
    int page = 0, interval = 1;          // interval：0 = 1 s，1 = 10 s，2 = 60 s
    int range = 0;                       // 导出范围：0 整炉全程，1 自定义（每一炉里从第几分钟到第几分钟）
    double from_min = 0, to_min = 60;
    std::string msg;
    std::chrono::steady_clock::time_point asked{};
};
ExportView exv;

void export_fetch(bool force) {
    const auto now = std::chrono::steady_clock::now();
    if (!force && now - exv.asked < std::chrono::seconds(3)) return;
    exv.asked = now;
    const json r = ask({{"cmd", "runs.list"}});
    if (!r.value("ok", false) || !r["runs"].is_array()) return;
    exv.runs = r["runs"];
    // 已经不在列表里的炉次（被 PC 端清掉了之类）从勾选里拿掉
    std::set<std::string> alive;
    for (auto& x : exv.runs) alive.insert(x.value("id", ""));
    for (auto it = exv.picked.begin(); it != exv.picked.end();) it = alive.count(*it) ? std::next(it) : exv.picked.erase(it);
    const int pages = std::max(1, (static_cast<int>(exv.runs.size()) + kExpRows - 1) / kExpRows);
    exv.page = std::max(0, std::min(exv.page, pages - 1));
}

bool export_action(const std::string& a) {
    if (a.rfind("exint:", 0) == 0) { exv.interval = std::max(0, std::min(2, a[6] - '0')); refresh(); return true; }
    if (a.rfind("exrange:", 0) == 0) { exv.range = a[8] == '1' ? 1 : 0; exv.msg.clear(); refresh(); return true; }
    if (a == "ex:from" || a == "ex:to") {
        // 起止都是「从每一炉开始时算起的第几分钟」。起点最多到终点前一分钟，终点最少比起点晚一分钟
        const bool from = a == "ex:from";
        const double lo = from ? 0 : exv.from_min + 1, hi = from ? std::max(0.0, exv.to_min - 1) : 6000;
        open_keypad(from ? "导出范围 · 从第几分钟起" : "导出范围 · 到第几分钟止", "min", from ? exv.from_min : exv.to_min, lo, hi, f0(from ? exv.from_min : exv.to_min) + " min", "—",
                    [this, from](double x) { (from ? exv.from_min : exv.to_min) = std::round(x); exv.msg.clear(); refresh(); }, 0);
        return true;
    }
    if (a.rfind("ex:", 0) != 0) return false;
    const int n = static_cast<int>(exv.runs.size());
    const int pages = std::max(1, (n + kExpRows - 1) / kExpRows);
    exv.msg.clear();
    if (a == "ex:prev") exv.page = std::max(0, exv.page - 1);
    else if (a == "ex:next") exv.page = std::min(pages - 1, exv.page + 1);
    else if (a.rfind("ex:tog:", 0) == 0) {
        const int k = exv.page * kExpRows + (a[7] - '0');
        if (k < n) { const std::string id = exv.runs[k].value("id", ""); if (!exv.picked.erase(id)) exv.picked.insert(id); }
    } else if (a == "ex:all") {
        // 全选管的是所有页，不只是眼前这四行；已经全选了再点就是全不选
        if (static_cast<int>(exv.picked.size()) == n) exv.picked.clear();
        else for (auto& x : exv.runs) exv.picked.insert(x.value("id", ""));
    } else if (a == "ex:go") {
        if (exv.picked.empty()) exv.msg = "先勾上要导出的炉次";
        else {
            static const int secs[3] = {1, 10, 60};
            json ids = json::array();
            for (auto& id : exv.picked) ids.push_back(id);
            json q = {{"cmd", "runs.export"}, {"ids", ids}, {"interval_s", secs[exv.interval]}};
            if (exv.range == 1) { q["from_min"] = exv.from_min; q["to_min"] = exv.to_min; }
            const json r = ask(q);
            if (r.value("ok", false)) {
                // 路径只写最后两级（比如 data/Exports）：全写出来一行放不下，前面那一长串对操作人也没用
                std::string dir = r.value("out_dir", std::string("Exports"));
                for (char& ch : dir) if (ch == '\\') ch = '/';
                size_t cut = dir.rfind('/');
                if (cut != std::string::npos && cut > 0) cut = dir.rfind('/', cut - 1);
                if (cut != std::string::npos) dir = "…" + dir.substr(cut);
                // 导完顺手拷到 U 盘：屏上没有文件管理器，数据留在本机拿不走
                const json dr = ask({{"cmd", "drives.list"}});
                if (dr.value("ok", false) && dr["drives"].is_array() && !dr["drives"].empty()) exv.msg = "已导出 " + std::to_string(r.value("exported", 0)) + " 炉　·　" + copy_to_usb();
                else exv.msg = "已导出 " + std::to_string(r.value("exported", 0)) + " 炉，放在本机 " + dir + "　·　没插 U 盘，插上再点一次";
            }
            else exv.msg = r.value("error", std::string("没导出来"));
        }
    }
    refresh();
    return true;
}

void refresh_export(Ui& u) {
    export_fetch(false);
    const int n = static_cast<int>(exv.runs.size()), np = static_cast<int>(exv.picked.size());
    const int pages = std::max(1, (n + kExpRows - 1) / kExpRows);
    set_radio(u, "exint", 3, exv.interval);
    set_radio(u, "exrange", 2, exv.range);
    u.show("ex.range.box", exv.range == 1);
    u.set_text("ex.from", f0(exv.from_min)); u.set_text("ex.to", f0(exv.to_min));
    u.set_text("ex.count", n == 0 ? "还没有记录" : "已选 " + std::to_string(np) + " / " + std::to_string(n));
    u.show("ex.pager", pages > 1);
    u.set_text("ex.page", std::to_string(exv.page + 1) + " / " + std::to_string(pages));
    u.show("ex.all.on", n > 0 && np == n); u.show("ex.all.part", np > 0 && np < n); u.show("ex.all.off", np == 0 || n == 0);
    // 今天的日期，用来把今天开始的炉次写成「今天」
    const std::time_t now = std::time(nullptr); std::tm tm{};
#ifdef _WIN32
    localtime_s(&tm, &now);
#else
    localtime_r(&now, &tm);
#endif
    char today[16]; std::strftime(today, sizeof today, "%Y-%m-%d", &tm);
    for (int i = 0; i < kExpRows; i++) {
        const std::string id = "ex.r." + std::to_string(i);
        const int k = exv.page * kExpRows + i;
        u.show(id, k < n);
        if (k >= n) continue;
        const json& x = exv.runs[k];
        const bool on = exv.picked.count(x.value("id", "")) > 0, running = x.value("running", false);
        u.set_text(id + ".n", x.value("name", ""));
        u.set_text(id + ".c", "通道 " + std::to_string(x.value("channel", 1)));
        u.set_text(id + ".s", running ? "运行中" : "已完成");
        u.set_text_color(id + ".s", running ? 0xFFFFFF : 0x4F5B67);
        if (lv_obj_t* o = u.box(id + ".s")) if (u.changed(id + ".s", running)) bg(o, running ? 0x00B368 : 0xEDF0F4, 255);
        u.set_text(id + ".d", hms(x.value("duration_s", 0.0)));
        const std::string started = x.value("started", "");
        u.set_text(id + ".t", started.rfind(today, 0) == 0 ? "今天 " + (started.size() >= 16 ? started.substr(11, 5) : std::string()) : started.substr(0, std::min<size_t>(16, started.size())));
        u.show(id + ".on", on); u.show(id + ".off", !on);
        if (lv_obj_t* o = u.box(id)) if (u.changed(id + "bg", on)) bg(o, on ? 0xEEF4FB : 0xFFFFFF, 255);
    }
    u.set_text("ex.go.t", "导出所选（" + std::to_string(np) + "）");
    if (lv_obj_t* o = u.box("ex.go")) if (u.changed("go", np > 0)) lv_obj_set_style_opa(o, np > 0 ? 255 : 115, 0);
    // 左下角：平时写导出的是什么，有话要说（导出结果、出错）时换成那句话
    const std::string tip = !exv.msg.empty() ? exv.msg : "3 个 CSV：执行记录 · 事件日志 · 采样宽表";
    u.set_text("ex.chip", tip);
    lv_obj_t* chip = u.box("ex.chip"); lv_obj_t* chip2 = u.box("ex.chip2");
    if (chip && chip2) if (lv_obj_t* lb = u.label("ex.chip")) {
        lv_obj_update_layout(lb);
        lv_obj_set_width(chip, lv_obj_get_width(lb) + 28 * UI_SCALE); lv_obj_set_x(lb, 14 * UI_SCALE);
        lv_obj_set_x(chip2, lv_obj_get_x(chip) + lv_obj_get_width(chip) + 8 * UI_SCALE);
        // 那句话长了（带着导出目录的路径）就把第二个提示收起来，免得顶到右边的按钮
        if (exv.msg.empty()) lv_obj_remove_flag(chip2, LV_OBJ_FLAG_HIDDEN); else lv_obj_add_flag(chip2, LV_OBJ_FLAG_HIDDEN);
    }
}
