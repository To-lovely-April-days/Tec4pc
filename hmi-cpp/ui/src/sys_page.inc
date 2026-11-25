// 「设置 → 系统设置」页：本机的实际情况，只看不改。这个文件直接嵌在 main.cpp 的 App 里面。
// 各行的内容来自控制核心（sys.info、comm.status、state）和界面程序自己；每 2 秒问一次。

static constexpr const char* kUiVersion = "0.1.0";
struct SysView { json info, comm; std::chrono::steady_clock::time_point asked{}; };
SysView syv;

// ── 退出程序（用户 2026-11-25 从三版里选的「甲」：这一页底下一条）──
// 为什么要：现场是满屏的触摸界面，没有标题栏，人想回 Windows 桌面（插 U 盘、装驱动、关机）只能拔电。
// 为什么点了不直接退、非要一个框：界面退出 ≠ 炉子停。tec_hmi 和 tec_core 是两个程序，
// 退界面只是这块屏没了，控温、搅拌、加料照常，PC 工作站也照样连得上。这句话不摆在人眼前，
// 两种误会都危险——以为自己在停机，或者以为退了就安全了。
Page exitdlg;
bool quit = false;            // 置上以后主循环就退出（见 main() 里那个 for）
bool exit_all_ok = false;     // 「连控制核心一起退」现在能不能点：两个通道都停着、而且连得上核心
std::string exit_err;         // 核心不让退时它给的原因，写在当中那条上（那一行放得下整句，右下角那行小字放不下）

void exit_dlg_show() {
    if (!exitdlg.scr) {
        exitdlg.scr = lv_obj_create(lv_layer_top());
        lv_obj_remove_style_all(exitdlg.scr);
        lv_obj_set_size(exitdlg.scr, W, H);
        lv_obj_add_flag(exitdlg.scr, LV_OBJ_FLAG_CLICKABLE);     // 点框外面不关：这不是随手点开的东西，要么退要么取消
        exitdlg.ui = std::make_unique<Ui>();
        exitdlg.ui->on_action = [this](const std::string& x) { action(x); };
        for (const PageDef& d : kPages) if (std::string(d.name) == "ExitDlg") { d.build(exitdlg.scr, *exitdlg.ui); lv_obj_update_layout(exitdlg.scr); }
    }
    if (!exitdlg.ui) return;
    Ui& u = *exitdlg.ui;
    // 哪几个通道在跑。框开着的时候每半秒重算一遍：人一边看着框，别的地方（PC、序列走完）可能刚好把炉子停了
    int nrun = 0, one = 0; double temp = 0; bool has_t = false;
    if (st.is_object() && st["ch"].is_array()) for (int i = 0; i < 2 && i < static_cast<int>(st["ch"].size()); i++) {
        const json& c = st["ch"][i];
        if (!c.is_object() || !c.value("running", false)) continue;
        nrun++; one = i + 1;
        const json& t = c["tr"].is_number() ? c["tr"] : c["tj"];
        if (t.is_number()) { temp = t.get<double>(); has_t = true; }
    }
    const bool run = nrun > 0;
    std::string line = nrun == 0   ? "两个通道都待机，没有在跑的炉子"
                     : nrun == 2   ? "两个通道都在控温 —— 退出后这块屏上看不到了，炉子照常控"
                     : "通道 " + std::to_string(one) + " 正在控温" + (has_t ? " " + f1(temp) + " ℃" : "") + " —— 退出后这块屏上看不到了，炉子照常控";
    if (!connected) line = "连不上控制核心：这块屏现在看到的不是实时的";
    if (!exit_err.empty()) line = exit_err;
    u.set_text("xq.run", line);
    // 几种样子不是几套元素，是同一个元素换颜色（设计稿里 display:none 的东西运行时根本不存在，见 ui/tools/dump.js）
    const int look = !exit_err.empty() ? 1 : !connected ? 2 : run ? 1 : 0;
    if (u.changed("xq.look", look)) {
        if (lv_obj_t* b = u.box("xq.band")) bg(b, look == 1 ? 0xFDECEC : 0xF3F7FC, 255);
        if (lv_obj_t* d = u.box("xq.dot")) bg(d, look == 1 ? 0xE5484D : look == 2 ? 0xC77700 : 0x9AA5B1, 255);
        u.set_text_color("xq.run", look == 1 ? 0xA32B2F : look == 2 ? 0x8A5A00 : 0x4F5B67);
    }
    // 「连控制核心一起退」：这一下会把炉子停掉，所以只有两个通道都停着才亮
    exit_all_ok = connected && !run;
    // 这里要连 connected 一起记：那行小字说的话分三种，只按 exit_all_ok 记的话，
    // 没连上时先写的「连不上控制核心」会一直留着（第一次自查就是这样：核心明明连着，小字还写着连不上）
    if (u.changed("xq.all", (exit_all_ok ? 1 : 0) | (connected ? 2 : 0))) {
        // 设计稿里那圈线是 inset 的 box-shadow，gen.py 翻成的是 border 不是 outline（看 ui/gen/pages_1.inc 里的 bd()）——
        // 一开始按 outline 改，颜色怎么设都不动
        if (lv_obj_t* b = u.box("xq.all")) lv_obj_set_style_border_color(b, lv_color_hex(exit_all_ok ? 0xE5484D : 0xD5DEE8), 0);
        u.set_text_color("xq.all.t", exit_all_ok ? 0xE5484D : 0x9AA5B1);
        u.set_text("xq.note", exit_all_ok ? "炉子会停，设备不再受控" : !connected ? "连不上控制核心" : "两个通道都待机时才能点");
        u.set_text_color("xq.note", 0x8792A0);
    }
}

bool sys_action(const std::string& a) {
    if (a == "nav:settings") { cmv.msg.clear(); show("Sys"); return true; }
    if (a.rfind("tab:", 0) == 0 && (cur == "Sys" || cur == "Comm")) { show(a[4] == '1' ? "Comm" : "Sys"); return true; }
    if (a == "sy:exit") { exit_err.clear(); exit_dlg_show(); lv_obj_remove_flag(exitdlg.scr, LV_OBJ_FLAG_HIDDEN); return true; }
    if (a == "xq:no") { if (exitdlg.scr) lv_obj_add_flag(exitdlg.scr, LV_OBJ_FLAG_HIDDEN); return true; }
    if (a == "xq:yes") { quit = true; return true; }
    if (a == "xq:all") {
        if (!exit_all_ok || !exitdlg.ui) return true;            // 灰着的时候点了不算
        exit_err.clear();
        const json r = ask({{"cmd", "sys.exit"}});
        if (!r.value("ok", false)) {                             // 核心不让退（这半秒里刚好有通道跑起来了）：把它给的原因照原样摆出来
            exit_err = r.value("error", std::string("控制核心没退"));
            exit_dlg_show();
            return true;
        }
        quit = true;
        return true;
    }
    return false;
}

void refresh_sys(Ui& u) {
    if (exitdlg.scr && !lv_obj_has_flag(exitdlg.scr, LV_OBJ_FLAG_HIDDEN)) exit_dlg_show();
    const auto now = std::chrono::steady_clock::now();
    if (!syv.info.is_object() || now - syv.asked > std::chrono::seconds(2)) {
        syv.asked = now;
        const json r = ask({{"cmd", "sys.info"}});
        if (r.value("ok", false)) syv.info = r;
        const json c = ask({{"cmd", "comm.status"}});
        if (c.value("ok", false)) syv.comm = c;
    }
    const std::time_t t = std::time(nullptr); std::tm tm{};
#ifdef _WIN32
    localtime_s(&tm, &t);
#else
    localtime_r(&t, &tm);
#endif
    char b[48]; std::strftime(b, sizeof b, "%Y-%m-%d %H:%M:%S", &tm);
    u.set_text("sy.v.0", b);
    if (!syv.info.is_object()) { u.set_text("sy.v.1", connected ? "读取中…" : "没连上"); u.set_text("sy.n.1", "界面程序连不上控制核心：看看它有没有在运行"); return; }
    const json& i = syv.info;
    const bool offline = st.is_object() && !st.value("offline", std::string()).empty();
    u.set_text_raw("sy.v.1", ui::fmt("{0} · 已运行 {1}", {i.value("version", ""), dur_text(i.value("uptime_s", 0.0) / 60.0)}));
    u.set_text("sy.n.1", i.value("sim", false) ? "仿真模式：没有连任何真设备" : offline ? "真机 · 控温设备没连上，去「通信设置」核对串口" : "真机 · 控温设备连着");
    u.set_text_color("sy.n.1", offline ? 0xD64545 : 0x8792A0);
    u.set_text("sy.v.2", std::string(kUiVersion) + " · LVGL " + std::to_string(LVGL_VERSION_MAJOR) + "." + std::to_string(LVGL_VERSION_MINOR) + "." + std::to_string(LVGL_VERSION_PATCH));
    u.set_text_raw("sy.n.2", ui::fmt("连着 {0} 的控制核心 · 端口 {1}", {host, std::to_string(port)}));
    u.set_text_raw("sy.v.3", i["disk_free_gb"].is_number() ? ui::fmt("剩余 {0} GB / 共 {1} GB", {f1(i["disk_free_gb"].get<double>()), f0(i.value("disk_total_gb", 0.0))}) : ui::tr("读不到"));
    // 数据目录的路径可能很长：只写最后两级
    std::string dir = i.value("data_dir", "");
    for (char& ch : dir) if (ch == '\\') ch = '/';
    size_t cut = dir.rfind('/'); if (cut != std::string::npos && cut > 0) cut = dir.rfind('/', cut - 1);
    u.set_text_raw("sy.n.3", ui::fmt("数据放在 {0}", {cut != std::string::npos && cut > 0 ? "…" + dir.substr(cut) : dir}));
    u.set_text_raw("sy.v.4", ui::fmt("{0} 炉 · 配方 {1} 个", {std::to_string(i.value("runs", 0)), std::to_string(i.value("recipes", 0))}));
    u.set_text_raw("sy.v.5", ui::fmt("{0} 行 · 双向挡", {std::to_string(i.value("gain_rows", 0))}));
    const bool valve = st.is_object() && st.value("valve_open", false), chiller = st.is_object() && st.value("chiller_run", false);
    // 四种说法各写成一整句：拼出来的半截话翻不了，而且别的语言语序不一样
    u.set_text("sy.v.6", valve ? (chiller ? "已开 · 冷水机运行中" : "已开 · 冷水机没在运行")
                               : (chiller ? "已关 · 冷水机运行中" : "已关 · 冷水机没在运行"));
    // 语言那一行：写现在用的这一种，名字按它本来的样子写
    for (int i = 0; i < kLangN; i++) if (lang_code == kLangs[i].code) u.set_text_raw("sy.v.8", kLangs[i].flag_native);
    u.set_text("sy.n.8", "点一下换；顶栏那颗地球也能换");
    if (syv.comm.is_object()) {
        int pcs = 0;
        for (auto& c : syv.comm["clients"]) if (c.value("address", std::string()).rfind("127.", 0) != 0) pcs++;
        const std::string addr = syv.comm.value("address", "");
        const std::string ap = std::to_string(cmv.tcp.is_object() ? cmv.tcp.value("port", port) : port);
        u.set_text_raw("sy.v.7", addr.empty() ? ui::tr("没连网")
                                 : pcs > 0 ? ui::fmt("{0} : {1} · {2} 台 PC 连着", {addr, ap, std::to_string(pcs)})
                                           : ui::fmt("{0} : {1} · 没有 PC 连着", {addr, ap}));
    }
}
