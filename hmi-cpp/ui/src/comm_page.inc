// 「设置 → 通信设置」页和点一行弹出来的设备通信参数窗。这个文件直接嵌在 main.cpp 的 App 里面。
//
// 左边是本机直接连的九台设备各自的串口参数和连接状态，右边是对外给 PC 连的 TCP。
// 参数存在控制核心的设置里（settings.set，只有本机的界面能改，PC 改不了——不然 PC 把端口改掉自己就连不上了）。
// 串口参数和监听端口改了要重启控制核心才生效：核心是开机时把各设备连上的，现在还不会运行中重连。
// 「允许远程控制」是马上生效的。

static constexpr int kCommRows = 9, kCommClients = 3;
struct CommView {
    json info;                       // comm.status 的回答
    json tcp;                        // settings.get 里的 tcp 那一块
    std::string msg, tip;
    std::chrono::steady_clock::time_point asked{};
};
CommView cmv;
Page commdlg;
struct CommEdit { json dev; bool open = false; } ced_dev;          // 弹窗里正在改的那台（草稿）

void comm_fetch(bool force) {
    const auto now = std::chrono::steady_clock::now();
    if (!force && cmv.info.is_object() && now - cmv.asked < std::chrono::seconds(2)) return;
    cmv.asked = now;
    const json r = ask({{"cmd", "comm.status"}});
    if (r.value("ok", false)) cmv.info = r;
    const json s = ask({{"cmd", "settings.get"}});
    if (s.value("ok", false) && s["tcp"].is_object()) cmv.tcp = s["tcp"];
}

void set_switch(Ui& u, const std::string& sid, bool on) {
    if (!u.changed("sw:" + sid, on)) return;
    if (lv_obj_t* t = u.box("sw." + sid)) bg(t, on ? 0x00559C : 0xC3CCD6, 255);
    if (lv_obj_t* k = u.box("sw." + sid + ".k")) lv_obj_set_x(k, (on ? 25 : 3) * UI_SCALE);
}

void comm_dlg_show() {
    Ui& u = *commdlg.ui;
    const json& d = ced_dev.dev;
    u.set_text("cd.title", d.value("label", ""));
    // 标题有长有短，后面那句小字跟在标题后面走
    if (lv_obj_t* tl = u.label("cd.title")) if (lv_obj_t* sub = u.box("cd.sub")) if (lv_obj_t* tb = u.box("cd.title")) { lv_obj_update_layout(tl); lv_obj_set_x(sub, lv_obj_get_x(tb) + lv_obj_get_width(tl) + 12 * UI_SCALE); }
    u.set_text("cd.port", d.value("port", ""));
    static const int bauds[5] = {9600, 19200, 38400, 57600, 115200};
    int bi = -1; for (int i = 0; i < 5; i++) if (bauds[i] == d.value("baud", 9600)) bi = i;
    set_radio(u, "cdbaud", 5, bi);
    const std::string par = d.value("parity", "N");
    set_radio(u, "cdpar", 3, par == "E" ? 1 : par == "O" ? 2 : 0);
    set_radio(u, "cdstop", 2, d.value("stop_bits", 1) == 2 ? 1 : 0);
    u.set_text("cd.station", std::to_string(d.value("station", 1)));
    u.set_text("cd.poll", std::to_string(d.value("poll_ms", 1000)));
    set_switch(u, "cd:enabled", d.value("enabled", true));
}

bool comm_action(const std::string& a) {
    if (a == "nav:settings") { cmv.msg.clear(); show("Comm"); return true; }
    const bool dlg = a.rfind("cd:", 0) == 0 || a.rfind("cdbaud:", 0) == 0 || a.rfind("cdpar:", 0) == 0 || a.rfind("cdstop:", 0) == 0;
    if (a.rfind("cm:", 0) != 0 && !dlg) return false;
    if (dlg) {
        if (!commdlg.scr || !ced_dev.open) return true;
        json& d = ced_dev.dev;
        if (a == "cd:close") { ced_dev.open = false; lv_obj_add_flag(commdlg.scr, LV_OBJ_FLAG_HIDDEN); return true; }
        if (a == "cd:save") {
            const json r = ask({{"cmd", "settings.set"}, {"device", d}});
            if (!r.value("ok", false)) { commdlg.ui->set_text("cd.msg", r.value("error", std::string("没存进去"))); return true; }
            cmv.msg = "「" + d.value("label", "") + "」的参数已保存，重启控制核心后生效";
            ced_dev.open = false; lv_obj_add_flag(commdlg.scr, LV_OBJ_FLAG_HIDDEN);
            comm_fetch(true); refresh();
            return true;
        }
        if (a == "cd:enabled") d["enabled"] = !d.value("enabled", true);
        else if (a.rfind("cdbaud:", 0) == 0) { static const int bauds[5] = {9600, 19200, 38400, 57600, 115200}; d["baud"] = bauds[std::max(0, std::min(4, a[7] - '0'))]; }
        else if (a.rfind("cdpar:", 0) == 0) { static const char* pars[3] = {"N", "E", "O"}; d["parity"] = pars[std::max(0, std::min(2, a[6] - '0'))]; }
        else if (a.rfind("cdstop:", 0) == 0) d["stop_bits"] = a[7] == '1' ? 2 : 1;
        else if (a == "cd:port") {
            // 串口号只填 COM 后面的数字。原来的名字不是 COMn 这种（开发机上）就从 1 起
            const std::string com = d.value("port", "");
            const double cur_n = com.rfind("COM", 0) == 0 ? std::atof(com.c_str() + 3) : 1;
            open_keypad(d.value("label", "") + " · 串口号", "COM", cur_n, 1, 64, com, "—", [this](double x) { ced_dev.dev["port"] = "COM" + std::to_string(static_cast<int>(x)); comm_dlg_show(); }, 0);
            lv_obj_move_foreground(kp.scr);          // 键盘要盖在这个弹窗上面
            return true;
        }
        else if (a == "cd:station") {
            open_keypad(d.value("label", "") + " · 站号", "", d.value("station", 1), 1, 247, std::to_string(d.value("station", 1)), "—", [this](double x) { ced_dev.dev["station"] = static_cast<int>(x); comm_dlg_show(); }, 0);
            lv_obj_move_foreground(kp.scr);
            return true;
        }
        else if (a == "cd:poll") {
            open_keypad(d.value("label", "") + " · 轮询周期", "ms", d.value("poll_ms", 1000), 100, 5000, std::to_string(d.value("poll_ms", 1000)) + " ms", "—", [this](double x) { ced_dev.dev["poll_ms"] = static_cast<int>(x); comm_dlg_show(); }, 0);
            lv_obj_move_foreground(kp.scr);
            return true;
        }
        comm_dlg_show();
        return true;
    }
    cmv.msg.clear();
    if (a.rfind("cm:dev:", 0) == 0) {
        const int k = a[7] - '0';
        if (!cmv.info.is_object() || k < 0 || k >= static_cast<int>(cmv.info["devices"].size())) return true;
        if (!commdlg.scr) {
            commdlg.scr = lv_obj_create(lv_layer_top());
            lv_obj_remove_style_all(commdlg.scr);
            lv_obj_set_size(commdlg.scr, W, H);
            lv_obj_add_flag(commdlg.scr, LV_OBJ_FLAG_CLICKABLE);
            commdlg.ui = std::make_unique<Ui>();
            commdlg.ui->on_action = [this](const std::string& x) { action(x); };
            for (const PageDef& d : kPages) if (std::string(d.name) == "CommDlg") { d.build(commdlg.scr, *commdlg.ui); lv_obj_update_layout(commdlg.scr); }
        }
        ced_dev.dev = cmv.info["devices"][k]; ced_dev.dev.erase("state"); ced_dev.dev.erase("problem");
        ced_dev.open = true;
        const std::string prob = cmv.info["devices"][k].value("problem", "");
        // 原因太长就截一段：这一行右边是按钮，放不下整句
        std::string shown = prob;
        if (utf8_count(shown) > 22) { while (utf8_count(shown) > 21) utf8_pop(shown); shown += "…"; }
        commdlg.ui->set_text("cd.msg", prob.empty() ? "保存后要重启控制核心才生效" : "没连上：" + shown);
        commdlg.ui->set_text_color("cd.msg", prob.empty() ? 0x8792A0 : 0xD64545);
        lv_obj_remove_flag(commdlg.scr, LV_OBJ_FLAG_HIDDEN);
        comm_dlg_show();
        return true;
    }
    if (!cmv.tcp.is_object()) return true;
    auto save_tcp = [this](const json& part, const std::string& ok_text) {
        const json r = ask({{"cmd", "settings.set"}, {"tcp", part}});
        cmv.tip = r.value("ok", false) ? ok_text : r.value("error", std::string("没存进去"));
        comm_fetch(true); refresh();
    };
    if (a == "cm:tcp") save_tcp({{"enabled", !cmv.tcp.value("enabled", true)}}, "已保存，重启控制核心后生效");
    else if (a == "cm:remote") save_tcp({{"allow_remote_control", !cmv.tcp.value("allow_remote_control", true)}}, "已保存，马上生效");
    else if (a == "cm:port") open_keypad("对外通信 · 监听端口", "", cmv.tcp.value("port", 5020), 1024, 65535, std::to_string(cmv.tcp.value("port", 5020)), "—",
                                         [save_tcp](double x) { save_tcp({{"port", static_cast<int>(x)}}, "端口已保存，重启控制核心后生效；PC 端要跟着改"); }, 0);
    else if (a == "cm:timeout") open_keypad("对外通信 · 断线超时", "s", cmv.tcp.value("timeout_s", 10), 2, 120, std::to_string(cmv.tcp.value("timeout_s", 10)) + " s", "—",
                                            [save_tcp](double x) { save_tcp({{"timeout_s", static_cast<int>(x)}}, "已保存，重启控制核心后生效"); }, 0);
    return true;
}

void refresh_comm(Ui& u) {
    comm_fetch(false);
    // 还没从控制核心拿到设备清单：**必须把这几行藏掉**。原样不动的话，屏上留着的是设计稿里的示意值
    // （一排「正常」、一行「无应答」），看着像真的读数，点又点不动——用户 2026-11-25 就是被这一屏骗了
    if (!cmv.info.is_object()) {
        for (int k = 0; k < kCommRows; k++) u.show("cm.r." + std::to_string(k), false);
        u.set_text("cm.msg", "还没拿到控制核心的设备清单");
        u.set_text_color("cm.msg", 0xD64545);
        return;
    }
    const json& devs = cmv.info["devices"];
    const int n = static_cast<int>(devs.size());
    for (int k = 0; k < kCommRows; k++) {
        const std::string id = "cm.r." + std::to_string(k);
        u.show(id, k < n);
        if (k >= n) continue;
        const json& d = devs[k];
        u.set_text(id + ".n", d.value("label", ""));
        u.set_text(id + ".p", d.value("port", ""));
        u.set_text(id + ".b", std::to_string(d.value("baud", 9600)));
        u.set_text(id + ".a", std::to_string(d.value("station", 1)));
        const std::string stt = d.value("state", "unknown"), prob = d.value("problem", "");
        // 状态是控制核心开机时连设备的结果（它不会运行中重连）；仿真时没有真设备可连
        const int code = stt == "ok" ? 1 : stt == "fail" ? 2 : stt == "off" ? 0 : 3;
        u.set_text(id + ".s", code == 1 ? "正常" : code == 2 ? (prob.empty() || utf8_count(prob) > 12 ? "没连上" : prob) : code == 0 ? "未启用" : stt == "sim" ? "仿真（没连真设备）" : "未知");
        u.set_text_color(id + ".s", code == 1 ? 0x4F5B67 : code == 2 ? 0xD64545 : 0x8792A0);
        if (lv_obj_t* o = u.box(id + ".dot")) if (u.changed(id + ".dot", code)) bg(o, code == 1 ? 0x00B368 : code == 2 ? 0xE5484D : code == 3 ? 0xF2A33A : 0xB8C4D0, 255);
    }
    // 控温设备没连上时，这一页就是来修它的地方：把话说在最显眼的这一行
    const bool offline = st.is_object() && !st.value("offline", std::string()).empty();
    u.set_text("cm.msg", !cmv.msg.empty() ? cmv.msg : offline ? "温控器或继电器模块没连上：点红色的那一行改串口，改好后重启控制核心" : "点一行改它的串口、波特率、校验、站号；改了要重启控制核心才生效");
    u.set_text_color("cm.msg", !cmv.msg.empty() ? 0x00559C : offline ? 0xD64545 : 0x8792A0);
    // 对外通信
    const std::string addr = cmv.info.value("address", "");
    u.set_text("cm.addr", addr.empty() ? "没连网" : addr);
    if (cmv.tcp.is_object()) {
        set_switch(u, "cm:tcp", cmv.tcp.value("enabled", true));
        set_switch(u, "cm:remote", cmv.tcp.value("allow_remote_control", true));
        u.set_text("cm.port", std::to_string(cmv.tcp.value("port", 5020)));
        u.set_text("cm.timeout", std::to_string(cmv.tcp.value("timeout_s", 10)));
    }
    u.set_text("cm.tip", cmv.tip.empty() ? "PC 断开不影响本机控温和序列" : cmv.tip);
    u.set_text_color("cm.tip", cmv.tip.empty() ? 0x8792A0 : 0x00559C);
    // 已连上的 PC：本机自己的界面程序（127.0.0.1）不算
    std::vector<json> pcs;
    for (auto& c : cmv.info["clients"]) if (c.value("address", std::string()).rfind("127.", 0) != 0) pcs.push_back(c);
    u.set_text("cm.count", pcs.empty() ? "现在没有 PC 连着" : std::to_string(pcs.size()) + " 台");
    for (int k = 0; k < kCommClients; k++) {
        const std::string id = "cm.c." + std::to_string(k);
        u.show(id, k < static_cast<int>(pcs.size()));
        if (k >= static_cast<int>(pcs.size())) continue;
        u.set_text(id + ".ip", pcs[k].value("address", ""));
        u.set_text(id + ".io", "收 " + std::to_string(pcs[k].value("rx", 0L)) + " 条 · 发 " + std::to_string(pcs[k].value("tx", 0L)) + " 条");
        u.set_text(id + ".t", "已连 " + hms(pcs[k].value("connected_s", 0.0)));
    }
}
