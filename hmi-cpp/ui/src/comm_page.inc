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
    if (lv_obj_t* sw = u.box("sw.cd:enabled")) if (lv_obj_t* sw_par = lv_obj_get_parent(sw)) if (lv_obj_get_child_count(sw_par) > 0) {
        const int sx = label_w(lv_obj_get_child(sw_par, 0)) + 10 * UI_SCALE;
        if (sx > lv_obj_get_x(sw)) {
            lv_obj_set_x(sw, sx); lv_obj_set_width(sw_par, sx + 54 * UI_SCALE);
            if (lv_obj_t* mb = u.box("cd.msg")) lv_obj_set_x(mb, sx + 78 * UI_SCALE);
        }
    }
}

bool comm_action(const std::string& a) {
    if (a == "nav:settings") { cmv.msg.clear(); show("Comm"); return true; }
    // 自动检测那两屏（扫描中 / 对照结果）自己一套动作
    if (a == "cm:scan" || a.rfind("cs:", 0) == 0 || a.rfind("cr:", 0) == 0) return scan_action(a);
    const bool dlg = a.rfind("cd:", 0) == 0 || a.rfind("cdbaud:", 0) == 0 || a.rfind("cdpar:", 0) == 0 || a.rfind("cdstop:", 0) == 0;
    if (a.rfind("cm:", 0) != 0 && !dlg) return false;
    if (dlg) {
        if (!commdlg.scr || !ced_dev.open) return true;
        json& d = ced_dev.dev;
        if (a == "cd:close") { ced_dev.open = false; lv_obj_add_flag(commdlg.scr, LV_OBJ_FLAG_HIDDEN); return true; }
        if (a == "cd:save") {
            const json r = ask({{"cmd", "settings.set"}, {"device", d}});
            if (!r.value("ok", false)) { commdlg.ui->set_text("cd.msg", core_text(r, "error", "没存进去")); return true; }
            cmv.msg = ui::fmt("「{0}」的参数已保存，重启控制核心后生效", {ui::tr(d.value("label", ""))});
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
            open_keypad(ui::fmt("{0} · 串口号", {ui::tr(d.value("label", ""))}), "COM", cur_n, 1, 64, com, "—", [this](double x) { ced_dev.dev["port"] = "COM" + std::to_string(static_cast<int>(x)); comm_dlg_show(); }, 0);
            lv_obj_move_foreground(kp.scr);          // 键盘要盖在这个弹窗上面
            return true;
        }
        else if (a == "cd:station") {
            open_keypad(ui::fmt("{0} · 站号", {ui::tr(d.value("label", ""))}), "", d.value("station", 1), 1, 247, std::to_string(d.value("station", 1)), "—", [this](double x) { ced_dev.dev["station"] = static_cast<int>(x); comm_dlg_show(); }, 0);
            lv_obj_move_foreground(kp.scr);
            return true;
        }
        else if (a == "cd:poll") {
            open_keypad(ui::fmt("{0} · 轮询周期", {ui::tr(d.value("label", ""))}), "ms", d.value("poll_ms", 1000), 100, 5000, std::to_string(d.value("poll_ms", 1000)) + " ms", "—", [this](double x) { ced_dev.dev["poll_ms"] = static_cast<int>(x); comm_dlg_show(); }, 0);
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
        const std::string prob = core_text(cmv.info["devices"][k], "problem");
        // 原因太长就截一段：这一行右边是按钮，放不下整句
        std::string shown = prob;
        if (utf8_count(shown) > 22) { while (utf8_count(shown) > 21) utf8_pop(shown); shown += "…"; }
        commdlg.ui->set_text_raw("cd.msg", prob.empty() ? ui::tr("保存后要重启控制核心才生效") : ui::fmt("没连上：{0}", {shown}));
        commdlg.ui->set_text_color("cd.msg", prob.empty() ? 0x8792A0 : 0xD64545);
        lv_obj_remove_flag(commdlg.scr, LV_OBJ_FLAG_HIDDEN);
        comm_dlg_show();
        return true;
    }
    if (!cmv.tcp.is_object()) return true;
    auto save_tcp = [this](const json& part, const std::string& ok_text) {
        const json r = ask({{"cmd", "settings.set"}, {"tcp", part}});
        cmv.tip = r.value("ok", false) ? ok_text : core_text(r, "error", "没存进去");
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
    refresh_scan_running();      // 正在扫的话，每拍问一次进度（扫完它自己换到对照那一屏）
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
        // 带 USB 标记 = 这台记住了自己在哪个 USB 口，COM 号变了也能自己找回来
        u.show(id + ".usb", !d.value("usb_id", std::string()).empty());
        u.set_text(id + ".b", std::to_string(d.value("baud", 9600)));
        u.set_text(id + ".a", std::to_string(d.value("station", 1)));
        const std::string stt = d.value("state", "unknown"), prob = core_text(d, "problem");
        // 状态是控制核心开机时连设备的结果（它不会运行中重连）；仿真时没有真设备可连
        // 「这台连上没有」说的是开机那一刻的事（核心不会运行中重连）。开机以后改过这台的参数，
        // 核心就只报 restart：再拿开机那次的结果写在这儿会骗人——把冷水机启用了，屏上却还写着「未启用」
        // warn = 连上了、可是有话要说（冷水机设成只能本地启停这种）：话说不下就点一下这一行看详情
        const int code = stt == "ok" ? 1 : stt == "fail" ? 2 : stt == "off" ? 0 : stt == "restart" ? 4 : stt == "warn" ? 5 : 3;
        u.set_text(id + ".s", code == 1 ? "正常" : code == 2 ? (prob.empty() || utf8_count(prob) > 12 ? "没连上" : prob)
                                                : code == 5 ? (utf8_count(prob) > 14 ? "连上了，但设置不对" : prob)
                                                : code == 0 ? "未启用" : code == 4 ? "改过了，重启核心后生效" : stt == "sim" ? "仿真（没连真设备）" : "未知");
        u.set_text_color(id + ".s", code == 1 ? 0x4F5B67 : code == 2 ? 0xD64545 : code == 4 || code == 5 ? 0xE07B00 : 0x8792A0);
        if (lv_obj_t* o = u.box(id + ".dot")) if (u.changed(id + ".dot", code)) bg(o, code == 1 ? 0x00B368 : code == 2 ? 0xE5484D : code >= 3 ? 0xF2A33A : 0xB8C4D0, 255);
    }
    // 控温设备没连上时，这一页就是来修它的地方：把话说在最显眼的这一行
    const bool offline = st.is_object() && !st.value("offline", std::string()).empty();
    u.set_text("cm.msg", !cmv.msg.empty() ? cmv.msg : offline ? "温控器或继电器模块没连上：按右上角「自动检测」问一遍，或点红色的那一行改串口" : "点一行改它的串口、波特率、校验、站号；带 USB 标记的记住了插口位置，COM 号变了也能自己找回来");
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
    u.set_text_raw("cm.count", pcs.empty() ? ui::tr("现在没有 PC 连着") : ui::fmt("{0} 台", {std::to_string(pcs.size())}));
    for (int k = 0; k < kCommClients; k++) {
        const std::string id = "cm.c." + std::to_string(k);
        u.show(id, k < static_cast<int>(pcs.size()));
        if (k >= static_cast<int>(pcs.size())) continue;
        u.set_text(id + ".ip", pcs[k].value("address", ""));
        u.set_text_raw(id + ".io", ui::fmt("收 {0} 条 · 发 {1} 条", {std::to_string(pcs[k].value("rx", 0L)), std::to_string(pcs[k].value("tx", 0L))}));
        u.set_text_raw(id + ".t", ui::fmt("已连 {0}", {hms(pcs[k].value("connected_s", 0.0))}));
    }
}

// ───────── 自动检测：这台设备接在哪个串口（用户 2026-11-25 要的）─────────
//
// 平时根本走不到这里：设备配置里记了 usb_id，控制核心开机就按 USB 口反查出今天的 COM 号。
// 线被挪到别的插口、或者第一次装机还没标定过，才来按一下「自动检测」真的问一遍。
// 界面按用户 2026-11-25 从三版里选的「乙：一屏对照」做：扫描一屏（CommScanRun），
// 扫完换成对照一屏（CommScan），同型号的两路就在行里指派通道。
//
// 这一层只管显示和挑选，真正去问设备的是控制核心（ports.scan，只发读功能码）。

static constexpr int kScanRows = 9;
// 设备键 → 扫描认出来的类型。同型号的两路（pH、搅拌）认出来是同一个类型，分不出通道，所以要人指派
static std::string scan_type_of(const std::string& key) {
    if (key == "ph1" || key == "ph2") return "ph";
    if (key == "stir1" || key == "stir2") return "stir";
    return key;
}

struct ScanRow {
    std::string key, label, old_port;
    bool off = false;         // 「通信设置」里关掉的：这一行没去找
    json hit;                 // 配到的那一条扫描结果；空 = 这台没扫到
    bool take = true;
    int pair = -1;            // 0 = pH 这一对，1 = 搅拌这一对；−1 = 不成对
    int pair_slot = 0;        // 在这一对里本来排第几（0 = 通道 1）
};
struct ScanView {
    bool open = false, running = false;
    std::vector<ScanRow> rows;
    bool swap[2] = {false, false};      // 这一对的两台是不是被人对调过
    std::string msg;
} scv;
Page commscan, commscanrun;

void scan_build_page(Page& pg, const char* name) {
    if (pg.scr) return;
    pg.scr = lv_obj_create(lv_layer_top());
    lv_obj_remove_style_all(pg.scr);
    lv_obj_set_size(pg.scr, W, H);
    lv_obj_add_flag(pg.scr, LV_OBJ_FLAG_CLICKABLE);
    pg.ui = std::make_unique<Ui>();
    pg.ui->on_action = [this](const std::string& x) { action(x); };
    for (const PageDef& d : kPages) if (std::string(d.name) == name) { d.build(pg.scr, *pg.ui); lv_obj_update_layout(pg.scr); }
}
void scan_show(Page& pg, bool on) {
    if (!pg.scr) return;
    if (on) { lv_obj_remove_flag(pg.scr, LV_OBJ_FLAG_HIDDEN); lv_obj_move_foreground(pg.scr); }
    else lv_obj_add_flag(pg.scr, LV_OBJ_FLAG_HIDDEN);
}

// 把扫描结果配到九台设备上：一类一类配，同型号的两路按顺序先给通道 1 再给通道 2
void scan_build_rows(const json& hits) {
    scv.rows.clear();
    scv.swap[0] = scv.swap[1] = false;
    if (!cmv.info.is_object()) return;
    std::map<std::string, std::vector<json>> by_type;
    for (const json& h : hits) {
        const std::string t = h.value("type", "");
        if (!t.empty()) by_type[t].push_back(h);
    }
    std::map<std::string, int> used;
    for (const json& d : cmv.info["devices"]) {
        ScanRow r;
        r.key = d.value("key", ""); r.label = d.value("label", ""); r.old_port = d.value("port", "");
        // 关掉（未启用）的那几台：核心压根没去找它们，这一行要说清是「没找」，不是「找了没找到」。
        // 行还是要占着——每一行长什么样（打勾 / 选通道 1、2）是设计稿里按行号定死的，跳过一行后面全挪位
        r.off = !d.value("enabled", true);
        if (r.key == "ph1" || r.key == "stir1") { r.pair = r.key == "ph1" ? 0 : 1; r.pair_slot = 0; }
        if (r.key == "ph2" || r.key == "stir2") { r.pair = r.key == "ph2" ? 0 : 1; r.pair_slot = 1; }
        const std::string ty = scan_type_of(r.key);
        auto it = by_type.find(ty);
        if (it != by_type.end() && used[ty] < static_cast<int>(it->second.size())) r.hit = it->second[used[ty]++];
        r.take = !r.hit.is_null();
        scv.rows.push_back(std::move(r));
    }
}

// 这一行最后要用哪一条结果：成对的两路被人对调过就取对家的
const json* scan_hit_of(int k) {
    if (k < 0 || k >= static_cast<int>(scv.rows.size())) return nullptr;
    const ScanRow& r = scv.rows[k];
    if (r.pair >= 0 && scv.swap[r.pair]) {
        for (size_t j = 0; j < scv.rows.size(); j++)
            if (static_cast<int>(j) != k && scv.rows[j].pair == r.pair) return scv.rows[j].hit.is_null() ? nullptr : &scv.rows[j].hit;
        return nullptr;
    }
    return r.hit.is_null() ? nullptr : &r.hit;
}

void refresh_scan_result() {
    if (!commscan.ui) return;
    Ui& u = *commscan.ui;
    int found = 0, changed = 0, ask = 0;
    for (int k = 0; k < kScanRows; k++) {
        const std::string id = "cs.r." + std::to_string(k);
        const bool on = k < static_cast<int>(scv.rows.size());
        u.show(id, on);
        if (!on) continue;
        const ScanRow& r = scv.rows[k];
        const json* h = scan_hit_of(k);
        u.set_text(id + ".n", r.label);
        u.set_text(id + ".old", r.old_port);
        const std::string np = h ? h->value("port", "") : "";
        const int slot = h ? h->value("slot", 0) : 0;
        const bool diff = h && np != r.old_port;
        if (h) found++;
        if (diff) changed++;
        u.show(id + ".u", h && slot > 0);
        u.set_text_raw(id + ".f", !h ? "—" : slot > 0 ? ui::fmt("{0} 口 · {1}", {std::to_string(slot), np}) : np);
        u.set_text_color(id + ".f", diff ? 0x00559C : 0x4F5B67);
        const std::string prob = h ? core_text(*h, "problem") : "";
        // 型号后面跟固件版本：核心把两件分开给，句子在这儿拼，这样才翻得了
        const std::string fw = h ? h->value("firmware", "") : "";
        const std::string model = !h ? "" : fw.empty() ? h->value("model", "") : ui::fmt("{0} · 固件 {1}", {h->value("model", ""), fw});
        u.set_text(id + ".m", r.off ? "未启用，没检测" : !h ? "没扫到" : !prob.empty() ? prob : model);
        u.set_text_color(id + ".m", !h ? 0x8792A0 : 0x1A2330);
        const bool pair = r.pair >= 0 && h;
        if (pair) ask++;
        if (lv_obj_t* o = u.box(id + ".dot")) if (u.changed(id + ".dot", !h ? 0 : pair ? 2 : 1))
            bg(o, !h ? 0xB8C4D0 : pair ? 0xC77700 : 0x00B368, 255);
        // 采用那一格：普通行给一个勾，同型号的两路给「这台是通道 1 | 2」
        u.show(id + ".take", h && !pair);
        u.show(id + ".ch", pair);
        if (h && !pair) {
            // 勾上 / 不勾：同一个方块改底色、对勾跟着显隐（display:none 的元素运行时不存在，不能拿两个轮流出场）
            if (lv_obj_t* o = u.box(id + ".cb")) if (u.changed(id + ".cb", r.take)) bg(o, r.take ? 0x00559C : 0xFFFFFF, 255);
            u.show(id + ".cb.ck", r.take);
        }
        if (pair) {
            const int sel = scv.swap[r.pair] ? 1 - r.pair_slot : r.pair_slot;
            for (int i = 0; i < 2; i++) {
                if (lv_obj_t* o = u.box(id + ".s" + std::to_string(i))) if (u.changed(id + ".s" + std::to_string(i), i == sel))
                    bg(o, i == sel ? 0x00559C : 0xFFFFFF, 255);
                u.set_text_color(id + ".s" + std::to_string(i) + "t", i == sel ? 0xFFFFFF : 0x4F5B67);
            }
        }
    }
    const int ports = scv.rows.empty() ? 0 : static_cast<int>(scv.rows.size());
    (void)ports;
    u.set_text_raw("cs.sub", ask ? ui::fmt("认出 {0} 台 · {1} 处和现在配的不一样 · {2} 台同型号要你指派通道", {std::to_string(found), std::to_string(changed), std::to_string(ask)})
                                 : ui::fmt("认出 {0} 台 · {1} 处和现在配的不一样", {std::to_string(found), std::to_string(changed)}));
    u.set_text_raw("cs.apply", changed ? ui::fmt("应用这 {0} 处", {std::to_string(changed)}) : ui::tr("应用"));
    if (!scv.msg.empty()) { u.set_text("cs.tip", scv.msg); u.set_text_color("cs.tip", 0xD64545); }
    else { u.set_text("cs.tip", "只读地问过，没动任何输出；应用后要重启控制核心才生效"); u.set_text_color("cs.tip", 0x8792A0); }
}

// 扫描进行中那一屏：每拍问一次控制核心的进度。扫完自动换到对照那一屏
void refresh_scan_running() {
    if (!scv.running || !commscanrun.ui) return;
    Ui& u = *commscanrun.ui;
    const json r = ask({{"cmd", "ports.scan.status"}});
    if (!r.value("ok", false)) { scv.running = false; scan_show(commscanrun, false); cmv.msg = core_text(r, "error", "检测没跑起来"); return; }
    const int done = r.value("done", 0), total = r.value("total", 0);
    u.set_text_raw("cr.n", total ? ui::fmt("第 {0} 个口 / 共 {1} 个", {std::to_string(std::max(1, done)), std::to_string(total)}) : ui::tr("正在找串口…"));
    u.set_text_raw("cr.t", ui::fmt("已用 {0} s", {f1(r.value("elapsed_s", 0.0))}));
    if (lv_obj_t* bar = u.box("cr.bar")) if (lv_obj_t* track = lv_obj_get_parent(bar)) {
        const int full = lv_obj_get_width(track);
        lv_obj_set_width(bar, total > 0 ? std::max(0, full * done / total) : 0);
    }
    const json& hits = r["hits"];
    // 屏上只有八行，口比八个多的时候往下滚：永远显示最近问过的这八个
    //（用户 2026-11-27：现场十五个口，后面的看不见）
    const int base = std::max(0, static_cast<int>(hits.size()) - 8);
    for (int k = 0; k < 8; k++) {
        const int idx = base + k;
        const std::string id = "cr.r." + std::to_string(k);
        const bool on = idx < total;
        u.show(id, on);
        if (!on) continue;
        const bool have = idx < static_cast<int>(hits.size());
        const bool now = have && idx == static_cast<int>(hits.size()) - 1 && r.value("running", false);
        // 一个圆点改颜色：绿 = 认出来了、蓝 = 正在问、灰 = 还没问
        if (lv_obj_t* o = u.box(id + ".dot")) if (u.changed(id + ".dot", !have ? 0 : now ? 1 : 2))
            bg(o, !have ? 0xC3CCD6 : now ? 0x00559C : 0x00B368, 255);
        if (!have) {
            u.set_text_raw(id + ".slot", ui::fmt("{0} 口", {std::to_string(idx + 1)})); u.set_text(id + ".p", "—"); u.set_text(id + ".link", "—");
            // 颜色也要盖掉：设计稿里这几行本来有一行是蓝的（画的是「正在问」那一行），不盖就留着别人的颜色
            u.set_text(id + ".m", "还没问"); u.set_text_color(id + ".m", 0x8792A0);
            continue;
        }
        const json& h = hits[idx];
        const int slot = h.value("slot", 0);
        u.set_text_raw(id + ".slot", slot > 0 ? ui::fmt("{0} 口", {std::to_string(slot)}) : "—");
        u.set_text(id + ".p", h.value("port", ""));
        const int baud = h.value("baud", 0);
        u.set_text_raw(id + ".link", baud ? ui::fmt("{0} · 站号 {1}", {std::to_string(baud), std::to_string(h.value("station", 0))}) : "—");
        const std::string fw = h.value("firmware", "");
        const std::string prob = core_text(h, "problem");
        const std::string model = fw.empty() ? h.value("model", "") : ui::fmt("{0} · 固件 {1}", {h.value("model", ""), fw});
        // 「问了没人答」和「有人答话但认不出」是两回事：前者多半是线没接 / 口不对 / 设备没上电，
        // 后者是我们这边的识别规矩不认识它。分开写，现场才知道该查哪一头
        u.set_text(id + ".m", !prob.empty() ? prob : !model.empty() ? model : "问了没人答");
        u.set_text_color(id + ".m", !prob.empty() ? 0xD64545 : !model.empty() ? 0x1A2330 : 0x8792A0);
    }
    if (!r.value("running", false)) {
        scv.running = false;
        scan_build_rows(r["hits"]);
        scan_show(commscanrun, false);
        scan_build_page(commscan, "CommScan");
        scv.open = true;
        refresh_scan_result();
        scan_show(commscan, true);
    }
}

bool scan_action(const std::string& a) {
    if (a == "cm:scan" || a == "cs:again") {
        const json r = ask({{"cmd", "ports.scan"}});
        if (!r.value("ok", false)) { cmv.msg = core_text(r, "error", "检测没跑起来"); scv.msg = cmv.msg; if (scv.open) refresh_scan_result(); return true; }
        scv.msg.clear(); cmv.msg.clear();
        scan_show(commscan, false); scv.open = false;
        scan_build_page(commscanrun, "CommScanRun");
        scv.running = true;
        scan_show(commscanrun, true);
        refresh_scan_running();
        return true;
    }
    if (a == "cr:close" || a == "cr:stop") { scv.running = false; scan_show(commscanrun, false); cmv.msg = "检测停了"; return true; }
    if (a == "cs:close") { scv.open = false; scan_show(commscan, false); return true; }
    if (a.rfind("cs:take:", 0) == 0) {
        const int k = std::atoi(a.c_str() + 8);
        if (k >= 0 && k < static_cast<int>(scv.rows.size()) && scv.rows[k].pair < 0) scv.rows[k].take = !scv.rows[k].take;
        refresh_scan_result();
        return true;
    }
    if (a.rfind("cs:ch:", 0) == 0) {
        // 「这台是通道 1 / 2」：点的是对家那一边，就把这一对的两台对调
        const size_t c = a.find(':', 6);
        const int k = std::atoi(a.substr(6, c - 6).c_str()), want = std::atoi(a.c_str() + c + 1);
        if (k >= 0 && k < static_cast<int>(scv.rows.size()) && scv.rows[k].pair >= 0) {
            const ScanRow& r = scv.rows[k];
            if (want != (scv.swap[r.pair] ? 1 - r.pair_slot : r.pair_slot)) scv.swap[r.pair] = !scv.swap[r.pair];
        }
        refresh_scan_result();
        return true;
    }
    if (a == "cs:apply") {
        json devs = json::array();
        for (int k = 0; k < static_cast<int>(scv.rows.size()); k++) {
            const ScanRow& r = scv.rows[k];
            const json* h = scan_hit_of(k);
            if (!h || (r.pair < 0 && !r.take)) continue;
            if (!h->value("problem", std::string()).empty()) continue;     // 认不准的不写进去
            // 校验和停止位也一起写：搅拌驱动要 8N2，别的是 8N1，这两项不对照样连不上
            devs.push_back({{"key", r.key}, {"port", h->value("port", "")}, {"usb_id", h->value("usb_id", "")},
                            {"usb_name", h->value("usb_name", "")}, {"baud", h->value("baud", 9600)},
                            {"parity", h->value("parity", std::string("N"))}, {"stop_bits", h->value("stop_bits", 1)},
                            {"station", h->value("station", 1)}});
        }
        if (devs.empty()) { scv.msg = "没有可以应用的行"; refresh_scan_result(); return true; }
        const json r = ask({{"cmd", "ports.apply"}, {"devices", devs}});
        if (!r.value("ok", false)) { scv.msg = core_text(r, "error", "没存进去"); refresh_scan_result(); return true; }
        scv.open = false; scan_show(commscan, false);
        // 改完串口要重启核心才生效，顺手替人做了（能走到检测这一步，两个通道本来就都停着）。
        // 重启那几秒界面会显示「未连上控制核心」，核心起来了它自己接回去，界面不用动
        const json rr = ask({{"cmd", "sys.restart"}});
        cmv.msg = rr.value("ok", false)
                      ? ui::fmt("已按检测结果改了 {0} 台，并记住了它们的 USB 口；控制核心正在重启", {std::to_string(r.value("applied", 0))})
                      : ui::fmt("已按检测结果改了 {0} 台；重启控制核心后生效（自动重启没成功：{1}）",
                                {std::to_string(r.value("applied", 0)), core_text(rr, "error", "这个程序不支持重启")});
        comm_fetch(true); refresh();
        return true;
    }
    return false;
}
