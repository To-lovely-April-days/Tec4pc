// 「设置 → 系统设置」页：本机的实际情况，只看不改。这个文件直接嵌在 main.cpp 的 App 里面。
// 各行的内容来自控制核心（sys.info、comm.status、state）和界面程序自己；每 2 秒问一次。

static constexpr const char* kUiVersion = "0.1.0";
struct SysView { json info, comm; std::chrono::steady_clock::time_point asked{}; };
SysView syv;

bool sys_action(const std::string& a) {
    if (a == "nav:settings") { cmv.msg.clear(); show("Sys"); return true; }
    if (a.rfind("tab:", 0) == 0 && (cur == "Sys" || cur == "Comm")) { show(a[4] == '1' ? "Comm" : "Sys"); return true; }
    return false;
}

void refresh_sys(Ui& u) {
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
    u.set_text("sy.v.1", i.value("version", "") + " · 已运行 " + dur_text(i.value("uptime_s", 0.0) / 60.0));
    u.set_text("sy.n.1", i.value("sim", false) ? "仿真模式：没有连任何真设备" : offline ? "真机 · 控温设备没连上，去「通信设置」核对串口" : "真机 · 控温设备连着");
    u.set_text_color("sy.n.1", offline ? 0xD64545 : 0x8792A0);
    u.set_text("sy.v.2", std::string(kUiVersion) + " · LVGL " + std::to_string(LVGL_VERSION_MAJOR) + "." + std::to_string(LVGL_VERSION_MINOR) + "." + std::to_string(LVGL_VERSION_PATCH));
    u.set_text("sy.n.2", "连着 " + host + " 的控制核心 · 端口 " + std::to_string(port));
    u.set_text("sy.v.3", i["disk_free_gb"].is_number() ? "剩余 " + f1(i["disk_free_gb"].get<double>()) + " GB / 共 " + f0(i.value("disk_total_gb", 0.0)) + " GB" : "读不到");
    // 数据目录的路径可能很长：只写最后两级
    std::string dir = i.value("data_dir", "");
    for (char& ch : dir) if (ch == '\\') ch = '/';
    size_t cut = dir.rfind('/'); if (cut != std::string::npos && cut > 0) cut = dir.rfind('/', cut - 1);
    u.set_text("sy.n.3", "数据放在 " + (cut != std::string::npos && cut > 0 ? "…" + dir.substr(cut) : dir));
    u.set_text("sy.v.4", std::to_string(i.value("runs", 0)) + " 炉 · 配方 " + std::to_string(i.value("recipes", 0)) + " 个");
    u.set_text("sy.v.5", std::to_string(i.value("gain_rows", 0)) + " 行 · 双向挡");
    const bool valve = st.is_object() && st.value("valve_open", false), chiller = st.is_object() && st.value("chiller_run", false);
    u.set_text("sy.v.6", std::string(valve ? "已开" : "已关") + " · 冷水机" + (chiller ? "运行中" : "没在运行"));
    if (syv.comm.is_object()) {
        int pcs = 0;
        for (auto& c : syv.comm["clients"]) if (c.value("address", std::string()).rfind("127.", 0) != 0) pcs++;
        const std::string addr = syv.comm.value("address", "");
        u.set_text("sy.v.7", (addr.empty() ? "没连网" : addr + " : " + std::to_string(cmv.tcp.is_object() ? cmv.tcp.value("port", port) : port)) + (pcs > 0 ? " · " + std::to_string(pcs) + " 台 PC 连着" : " · 没有 PC 连着"));
    }
}
