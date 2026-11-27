// 「PID 整定」页与「一键两段式硬件自整定」弹窗。这个文件直接嵌在 main.cpp 的 App 里面。
//
// 现在的架构（用户 2026-11-27 拍板）：
//   · 两个工位各自独立保存一张 8 行增益表（gain-table-ch1.csv / ch2.csv）和一套外环硬件特征参数（tw / tau / rc_nom / 电热余热 / 加码上限）。
//   · 点顶栏「通道 1 / 通道 2」就切到对应工位的那套表和参数，互不干扰。
//   · 内建「一键自整定」：自动执行「① 干态电热阶跃测 Kp/Ki/Kd 与电热余热 → ② 通水 TEC 阶跃测真实加热比与刹车速率」，
//     若釜内装液并开着搅拌，同步拟合出釜壁滞后 tw、釜时间常数 τ 与稳态偏置列，跑完自动存盘并立即热生效。

static constexpr int kPidRows = 8;
struct PidView {
    json rows = json::array();       // 当前通道屏上这份（可能是改了还没应用的草稿）
    bool dirty = false, clear_armed = false, was_tuning = false;
    double look_t = 45, boost = 60.0, tw = 35.0, tau = 880.0, rc_nom = 0.185, hmom = 3.2;
    std::string tuned_at;
    json tune_st = json::object();   // tune.status 最近一次回答
    std::string msg, msg2;
    std::chrono::steady_clock::time_point asked{}, asked_tune{};
};
PidView pv[2];

Page tunedlg;
struct TuneDlgState {
    bool open = false;
    double t_high = 65.0;
    double t_low = 10.0;
    double heat_duty = 60.0;
    std::string msg;
    bool msg_ok = false;      // 这句话是「成功」还是「出错」：决定它什么颜色。
                              // 不能看话是不是「已」字开头——换成英文就全成红的了
} tds;

// 页面搭好后把底部 5 个圆键匀成 6 个（720 = 6 × 120），在第 6 格挂上「一键自整定」圆键
void pid_attach_ui(Ui& u) {
    const int S = UI_SCALE;
    lv_obj_t* add_t = u.box("pg.add.t");
    lv_obj_t* slot0 = add_t ? lv_obj_get_parent(add_t) : nullptr;
    lv_obj_t* bar = slot0 ? lv_obj_get_parent(slot0) : nullptr;
    if (!bar || lv_obj_get_child_count(bar) != 5) return;

    for (uint32_t i = 0; i < 5; i++) {
        lv_obj_t* sl = lv_obj_get_child(bar, i);
        lv_obj_set_x(sl, static_cast<int>(i) * 120 * S);
        lv_obj_set_width(sl, 120 * S);
        if (lv_obj_get_child_count(sl) >= 2) {
            lv_obj_t* cir = lv_obj_get_child(sl, 0);
            lv_obj_t* tb = lv_obj_get_child(sl, 1);
            lv_obj_set_x(cir, 36 * S);
            lv_obj_set_x(tb, lv_obj_get_x(tb) - 12 * S);
        }
    }

    lv_obj_t* s5 = mk(bar, 600 * S, 0, 120 * S, 71 * S, 0);
    u.reg_act(s5, "pg:tune");
    lv_obj_t* cir = mk(s5, 36 * S, 0, 48 * S, 48 * S, 0);
    bg(cir, 0x007A5A, 255);
    lv_obj_set_style_radius(cir, LV_RADIUS_CIRCLE, 0);
    sh(cir, 0x007A5A, 77, 8 * S, 0, 3 * S, 0);
    u.reg_box("pg.tune.cir", cir);
    // 图标单独出在 ui/design/FxIcon.dc.html 上（名字固定，不跟着别的设计稿里的元素顺序走）。
    // 原来借的是「重新从盘上读」那个转圈箭头，两个键长得一模一样（用户 2026-11-27 要求换掉）
    lv_obj_t* ic = im(cir, 9 * S, 9 * S, S == 2 ? "A:assets/img/fx_autotune_2.png" : "A:assets/img/fx_autotune_1.png");
    if (ic) {
        lv_obj_set_style_image_recolor(ic, lv_color_hex(0xFFFFFF), 0);
        lv_obj_set_style_image_recolor_opa(ic, 255, 0);
    }
    lv_obj_t* tb = mk(s5, 18 * S, 53 * S, 84 * S, 18 * S, 0);
    u.reg_box("pg.tune.t", tb);
    lv_obj_t* tl = txta(tb, 42 * S, 9 * S, 'c', "一键自整定", 600, 13, 0x007A5A, 255, 0, 0);
    u.reg_text("pg.tune.t", tl, 'c', 42 * S, 9 * S);
}

void build_tune_dlg(lv_obj_t* scr, Ui& u) {
    const int S = UI_SCALE;
    bg(scr, 0x0A192F, 128);

    lv_obj_t* card = mk(scr, 260 * S, 92 * S, 760 * S, 536 * S, 1);
    bg(card, 0xFFFFFF, 255);
    lv_obj_set_style_radius(card, 14 * S, 0);
    lv_obj_set_style_clip_corner(card, true, 0);
    sh(card, 0x0A192F, 70, 24 * S, 0, 8 * S, 0);

    // 顶栏
    lv_obj_t* hdr = mk(card, 0, 0, 760 * S, 58 * S, 0);
    bg(hdr, 0xF5F8FC, 255);
    bd(hdr, 0xE8ECF1, 255, 1 * S, LV_BORDER_SIDE_BOTTOM);
    lv_obj_t* th_box = mk(hdr, 24 * S, 16 * S, 480 * S, 26 * S, 0);
    lv_obj_t* th_lbl = txta(th_box, 0, 13 * S, 'l', "通道 1 · 一键两段式自整定", 700, 19, 0x1F2A35, 255, 0, 0);
    u.reg_text("td.title", th_lbl, 'l', 0, 13 * S);

    lv_obj_t* bdg = mk(hdr, 596 * S, 14 * S, 140 * S, 30 * S, 0);
    bg(bdg, 0xE8EEF5, 255);
    lv_obj_set_style_radius(bdg, 15 * S, 0);
    u.reg_box("td.badge", bdg);
    lv_obj_t* bdg_t = txta(bdg, 70 * S, 15 * S, 'c', "待启动", 600, 13, 0x4F5B67, 255, 0, 0);
    u.reg_text("td.badge.t", bdg_t, 'c', 70 * S, 15 * S);

    // 流程说明条
    lv_obj_t* tip = mk(card, 24 * S, 70 * S, 712 * S, 54 * S, 0);
    bg(tip, 0xF0F6FC, 255);
    lv_obj_set_style_radius(tip, 8 * S, 0);
    lv_obj_t* tip1_b = mk(tip, 14 * S, 7 * S, 684 * S, 18 * S, 0);
    lv_obj_t* tip1 = txta(tip1_b, 0, 9 * S, 'l', "① 干态加热阶跃测 Kp/Ki/Kd 与电热余热  →  ② 通水 TEC 阶跃测真实加热比与刹车速率", 500, 13, 0x00559C, 255, 0, 0);
    u.reg_text("td.tip1", tip1, 'l', 0, 9 * S);
    lv_obj_t* tip2_b = mk(tip, 14 * S, 29 * S, 684 * S, 18 * S, 0);
    lv_obj_t* tip2 = txta(tip2_b, 0, 9 * S, 'l', "建议釜内装水或常用溶剂并开启搅拌（约 300 rpm），可同步标定釜壁滞后 tw 与稳态偏置", 400, 13, 0x4F5B67, 255, 0, 0);
    u.reg_text("td.tip2", tip2, 'l', 0, 9 * S);

    // 三个整定参数输入格
    struct Fld { const char* label; const char* act; const char* id; const char* btn_id; const char* def_v; };
    static const Fld F[3] = {
        {"最高加热温度 ℃", "pt:thigh", "td.thigh", "td.thigh.btn", "65"},
        {"最低制冷温度 ℃", "pt:tlow",  "td.tlow",  "td.tlow.btn",  "10"},
        {"加热阶跃出力 %", "pt:hduty", "td.hduty", "td.hduty.btn", "60"},
    };
    for (int i = 0; i < 3; i++) {
        lv_obj_t* col = mk(card, (24 + i * 244) * S, 138 * S, 224 * S, 72 * S, 0);
        lv_obj_t* lb = mk(col, 0, 0, 224 * S, 18 * S, 0);
        txta(lb, 0, 9 * S, 'l', F[i].label, 400, 13, 0x8792A0, 255, 0, 0);
        lv_obj_t* btn = mk(col, 0, 24 * S, 224 * S, 44 * S, 0);
        bg(btn, 0xF3F7FC, 255);
        lv_obj_set_style_radius(btn, 8 * S, 0);
        bd(btn, 0xC9D6E5, 255, 2 * S, LV_BORDER_SIDE_FULL);
        u.reg_act(btn, F[i].act);
        u.reg_box(F[i].btn_id, btn);
        lv_obj_t* vb = mk(btn, 52 * S, 9 * S, 120 * S, 26 * S, 0);
        lv_obj_t* vl = txta(vb, 60 * S, 13 * S, 'c', F[i].def_v, 600, 18, 0x1F2A35, 255, 0, 0);
        u.reg_text(F[i].id, vl, 'c', 60 * S, 13 * S);
    }

    // 实时进度与辨识结果面板
    lv_obj_t* pan = mk(card, 24 * S, 224 * S, 712 * S, 224 * S, 0);
    bg(pan, 0xF8FAFC, 255);
    lv_obj_set_style_radius(pan, 10 * S, 0);
    bd(pan, 0xE2E8F0, 255, 1 * S, LV_BORDER_SIDE_FULL);

    lv_obj_t* ph_b = mk(pan, 16 * S, 14 * S, 680 * S, 22 * S, 0);
    lv_obj_t* ph_l = txta(ph_b, 0, 11 * S, 'l', "尚未启动，核对参数后点击右下角「开始自整定」", 600, 15, 0x1F2A35, 255, 0, 0);
    u.reg_text("td.phase", ph_l, 'l', 0, 11 * S);

    lv_obj_t* trk = mk(pan, 16 * S, 44 * S, 680 * S, 12 * S, 1);
    bg(trk, 0xDCE4EE, 255);
    lv_obj_set_style_radius(trk, 6 * S, 0);
    u.reg_box("td.bar", trk);
    lv_obj_t* fl = mk(trk, 0, 0, 0, 12 * S, 0);
    bg(fl, 0x00559C, 255);
    lv_obj_set_style_radius(fl, 6 * S, 0);
    u.reg_box("td.fill", fl);

    lv_obj_t* l1_b = mk(pan, 16 * S, 70 * S, 680 * S, 20 * S, 0);
    lv_obj_t* l1 = txta(l1_b, 0, 10 * S, 'l', "当前读数：—", 400, 14, 0x4F5B67, 255, 0, 0);
    u.reg_text("td.live1", l1, 'l', 0, 10 * S);

    lv_obj_t* l2_b = mk(pan, 16 * S, 98 * S, 680 * S, 20 * S, 0);
    lv_obj_t* l2 = txta(l2_b, 0, 10 * S, 'l', "执行状态：—", 400, 14, 0x4F5B67, 255, 0, 0);
    u.reg_text("td.live2", l2, 'l', 0, 10 * S);

    lv_obj_t* div = mk(pan, 16 * S, 130 * S, 680 * S, 1 * S, 0);
    bg(div, 0xE2E8F0, 255);

    lv_obj_t* r1_b = mk(pan, 16 * S, 144 * S, 680 * S, 20 * S, 0);
    lv_obj_t* r1 = txta(r1_b, 0, 10 * S, 'l', "本通道当前硬件参数：—", 500, 14, 0x00559C, 255, 0, 0);
    u.reg_text("td.res1", r1, 'l', 0, 10 * S);

    lv_obj_t* r2_b = mk(pan, 16 * S, 172 * S, 680 * S, 20 * S, 0);
    lv_obj_t* r2 = txta(r2_b, 0, 10 * S, 'l', "跑完自动更新本通道 8 行增益表与外环参数并存盘", 400, 13, 0x8792A0, 255, 0, 0);
    u.reg_text("td.res2", r2, 'l', 0, 10 * S);

    // 底栏按钮
    lv_obj_t* msg_b = mk(card, 24 * S, 476 * S, 390 * S, 22 * S, 0);
    lv_obj_t* msg_l = txta(msg_b, 0, 11 * S, 'l', "", 500, 14, 0xE5484D, 255, 0, 0);
    u.reg_text("td.msg", msg_l, 'l', 0, 11 * S);

    lv_obj_t* b_cls = mk(card, 434 * S, 464 * S, 132 * S, 48 * S, 0);
    bg(b_cls, 0xF1F4F8, 255);
    lv_obj_set_style_radius(b_cls, 10 * S, 0);
    bd(b_cls, 0xC9D6E5, 255, 1 * S, LV_BORDER_SIDE_FULL);
    u.reg_act(b_cls, "pt:close");
    lv_obj_t* cls_t = txta(b_cls, 66 * S, 24 * S, 'c', "关闭窗口", 600, 15, 0x4F5B67, 255, 0, 0);
    u.reg_text("td.close.t", cls_t, 'c', 66 * S, 24 * S);

    lv_obj_t* b_run = mk(card, 578 * S, 464 * S, 158 * S, 48 * S, 0);
    bg(b_run, 0x007A5A, 255);
    lv_obj_set_style_radius(b_run, 10 * S, 0);
    u.reg_box("td.run.btn", b_run);
    u.reg_act(b_run, "pt:run");
    lv_obj_t* run_t = txta(b_run, 79 * S, 24 * S, 'c', "开始自整定", 600, 16, 0xFFFFFF, 255, 0, 0);
    u.reg_text("td.run.t", run_t, 'c', 79 * S, 24 * S);
}

void pid_fetch(int ci, bool force) {
    if (ci < 0 || ci > 1) return;
    PidView& v = pv[ci];
    const auto now = std::chrono::steady_clock::now();
    if (!v.dirty && (force || now - v.asked > std::chrono::seconds(3))) {
        v.asked = now;
        const json r = ask({{"cmd", "gains.get"}, {"ch", ci + 1}});
        if (r.value("ok", false)) {
            if (r["rows"].is_array()) v.rows = r["rows"];
            if (r.contains("tune") && r["tune"].is_object()) {
                const json& tn = r["tune"];
                v.boost = tn.value("fast_boost_max", v.boost);
                v.tw = tn.value("tw", v.tw);
                v.tau = tn.value("tau", v.tau);
                v.rc_nom = tn.value("rc_nom", v.rc_nom);
                v.hmom = tn.value("heater_mom_gain", v.hmom);
                v.tuned_at = tn.value("tuned_at", std::string());
            }
        }
    }
    const bool fast_poll = tds.open || v.was_tuning;
    if (force || now - v.asked_tune > std::chrono::milliseconds(fast_poll ? 800 : 2500)) {
        v.asked_tune = now;
        const json ts = ask({{"cmd", "tune.status"}, {"ch", ci + 1}});
        if (ts.value("ok", false)) {
            const bool active_now = ts.value("active", false);
            if (v.was_tuning && !active_now) {
                v.dirty = false;
                v.asked = {};
                const json g = ask({{"cmd", "gains.get"}, {"ch", ci + 1}});
                if (g.value("ok", false)) {
                    if (g["rows"].is_array()) v.rows = g["rows"];
                    if (g.contains("tune") && g["tune"].is_object()) {
                        const json& tn = g["tune"];
                        v.boost = tn.value("fast_boost_max", v.boost);
                        v.tw = tn.value("tw", v.tw);
                        v.tau = tn.value("tau", v.tau);
                        v.rc_nom = tn.value("rc_nom", v.rc_nom);
                        v.hmom = tn.value("heater_mom_gain", v.hmom);
                        v.tuned_at = tn.value("tuned_at", std::string());
                    }
                }
            }
            v.was_tuning = active_now;
            v.tune_st = ts;
        }
    }
}

void tune_dlg_refresh() {
    if (!tunedlg.scr || !tds.open || !tunedlg.ui) return;
    const int ci = std::max(0, std::min(1, channel - 1));
    pid_fetch(ci, false);
    PidView& v = pv[ci];
    Ui& u = *tunedlg.ui;

    u.set_text_raw("td.title", ui::fmt("通道 {0} · 一键两段式自整定", {std::to_string(ci + 1)}));
    u.set_text_raw("td.thigh", f0(tds.t_high));
    u.set_text_raw("td.tlow", f0(tds.t_low));
    u.set_text_raw("td.hduty", f0(tds.heat_duty));

    const json& ts = v.tune_st;
    const bool active = ts.value("active", false);
    const std::string ph = ts.value("phase", "idle");
    const double prog = active ? ts.value("progress", 0.0) : (ph == "done" ? 1.0 : 0.0);

    const uint32_t bdg_bg = active ? 0xE3F2FD : ph == "done" ? 0xE6F7EF : ph == "failed" ? 0xFDECEC : 0xE8EEF5;
    const uint32_t bdg_fg = active ? 0x00559C : ph == "done" ? 0x007A5A : ph == "failed" ? 0xD64545 : 0x4F5B67;
    if (lv_obj_t* b = u.box("td.badge")) bg(b, bdg_bg, 255);
    u.set_text("td.badge.t", active ? "整定运行中" : ph == "done" ? "整定已完成" : ph == "failed" ? "整定未成功" : ph == "aborted" ? "已中止" : "待启动");
    u.set_text_color("td.badge.t", bdg_fg);

    const std::string note = ts.value("note", std::string());
    u.set_text_raw("td.phase", !note.empty() ? note : ui::tr("尚未启动，核对参数后点击右下角「开始自整定」"));

    if (lv_obj_t* bar = u.box("td.bar")) if (lv_obj_t* fill = u.box("td.fill")) {
        const int w = static_cast<int>(std::round(std::min(1.0, std::max(0.0, prog)) * lv_obj_get_width(bar)));
        lv_obj_set_width(fill, w);
        bg(fill, ph == "done" ? 0x00B368 : ph == "failed" ? 0xE5484D : 0x00559C, 255);
    }

    double tj = ts.value("tj", 0.0), tr_v = 0.0, rpm = 0.0;
    bool has_tr = ts.contains("tr") && ts["tr"].is_number();
    bool show_tr = true;
    if (st.is_object() && st.contains("ch") && st["ch"].size() > static_cast<size_t>(ci)) {
        const json& chj = st["ch"][ci];
        if (!active) {
            tj = chj.value("tj", tj);
            has_tr = chj["tr"].is_number();
            if (has_tr) tr_v = chj["tr"].get<double>();
        } else if (has_tr) {
            tr_v = ts["tr"].get<double>();
        }
        rpm = chj["stir"].value("rpm", 0.0);
        show_tr = chj.value("show_tr", true);
    }
    u.set_text_raw("td.live1", ui::fmt("当前读数：夹套 Tj {0} ℃　·　釜内 Tr {1}　·　搅拌 {2} rpm",
                                       {f1(tj), has_tr ? (f2(tr_v) + " ℃") : "—", f0(rpm)}));

    const double duty = ts.value("duty", 0.0);
    const std::string act_s = !active ? ui::tr("待机")
                              : std::fabs(duty) < 0.5 ? ui::tr("零出力观察 / 切换等待")
                              : duty < 0 ? ui::fmt("加热棒 {0} %", {f0(-duty)})
                                         : ui::fmt("TEC 制冷 {0} %", {f0(duty)});
    u.set_text_raw("td.live2", ui::fmt("执行状态：{0}　·　冷却水阀 {1}　·　已用时 {2}（{3} %）",
                                       {act_s, ui::tr(ts.value("valve_open", false) ? "开" : "关"),
                                        hms(ts.value("elapsed_s", 0.0)), f0(prog * 100.0)}));

    u.set_text_raw("td.res1", ui::fmt("本通道标定参数：釜壁滞后 tw {0} s · 釜时间常数 τ {1} s · 刹车速率 {2} K/s · 电热余热 {3} K",
                                      {f0(v.tw), f0(v.tau), f2(v.rc_nom), f1(v.hmom)}));

    if (!active && show_tr && has_tr && rpm < 30.0) {
        u.set_text("td.res2", "提示：检测到当前搅拌未开启（0 rpm）；若釜内装有液体，建议先开 300 rpm 搅拌再启动自整定");
        u.set_text_color("td.res2", 0xE07B00);
    } else if (ph == "done" && ts.contains("result")) {
        u.set_text_raw("td.res2", ui::fmt("已自动生成 8 行平滑增益表并写入通道 {0} 独立文件，无需重启已直接生效", {std::to_string(ci + 1)}));
        u.set_text_color("td.res2", 0x007A5A);
    } else {
        u.set_text_raw("td.res2", !v.tuned_at.empty() ? ui::fmt("上次整定时间：{0}（两工位增益表与硬件参数各自独立保存）", {v.tuned_at})
                                                      : ui::tr("跑完自动更新本通道 8 行增益表与外环参数并存盘，无需重启直接生效"));
        u.set_text_color("td.res2", 0x8792A0);
    }

    u.set_text_raw("td.msg", tds.msg);
    u.set_text_color("td.msg", tds.msg_ok ? 0x007A5A : 0xE5484D);
    u.set_text("td.close.t", active ? "后台运行" : "关闭窗口");
    if (lv_obj_t* rb = u.box("td.run.btn")) bg(rb, active ? 0xE5484D : 0x007A5A, 255);
    u.set_text("td.run.t", active ? "中止自整定" : "开始自整定");
}

void open_tune_dlg() {
    if (!tunedlg.scr) {
        tunedlg.scr = lv_obj_create(lv_layer_top());
        lv_obj_remove_style_all(tunedlg.scr);
        lv_obj_set_size(tunedlg.scr, W, H);
        lv_obj_add_flag(tunedlg.scr, LV_OBJ_FLAG_CLICKABLE);
        tunedlg.ui = std::make_unique<Ui>();
        tunedlg.ui->on_action = [this](const std::string& a) { action(a); };
        build_tune_dlg(tunedlg.scr, *tunedlg.ui);
        lv_obj_update_layout(tunedlg.scr);
    }
    tds.open = true;
    tds.msg.clear();
    const int ci = std::max(0, std::min(1, channel - 1));
    pid_fetch(ci, true);
    lv_obj_remove_flag(tunedlg.scr, LV_OBJ_FLAG_HIDDEN);
    lv_obj_move_foreground(tunedlg.scr);
    tune_dlg_refresh();
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
    const int ci = std::max(0, std::min(1, channel - 1));
    PidView& v = pv[ci];

    if (a.rfind("pt:", 0) == 0) {
        if (!tunedlg.scr || !tds.open) return true;
        const bool active = v.tune_st.value("active", false);
        if (a == "pt:close") {
            tds.open = false;
            lv_obj_add_flag(tunedlg.scr, LV_OBJ_FLAG_HIDDEN);
            refresh();
            return true;
        }
        if (a == "pt:thigh" || a == "pt:tlow" || a == "pt:hduty") {
            if (active) { tds.msg_ok = false; tds.msg = ui::tr("正在自整定中，如需修改参数请先中止"); tune_dlg_refresh(); return true; }
            if (a == "pt:thigh") {
                open_keypad("自整定 · 最高加热温度", "℃", tds.t_high, 35, 135, f0(tds.t_high) + " ℃", "—",
                            [this](double x) { tds.t_high = x; tds.msg.clear(); tune_dlg_refresh(); }, 0);
            } else if (a == "pt:tlow") {
                open_keypad("自整定 · 最低制冷温度", "℃", tds.t_low, -20, 25, f0(tds.t_low) + " ℃", "—",
                            [this](double x) { tds.t_low = x; tds.msg.clear(); tune_dlg_refresh(); }, 0);
            } else {
                open_keypad("自整定 · 加热阶跃出力", "%", tds.heat_duty, 30, 85, f0(tds.heat_duty) + " %", "—",
                            [this](double x) { tds.heat_duty = x; tds.msg.clear(); tune_dlg_refresh(); }, 0);
            }
            if (kp.scr) lv_obj_move_foreground(kp.scr);
            return true;
        }
        if (a == "pt:run") {
            if (active) {
                const json r = ask({{"cmd", "tune.stop"}, {"ch", ci + 1}});
                tds.msg_ok = r.value("ok", false);
                tds.msg = tds.msg_ok ? ui::tr("已发送中止指令，正在安全关断输出") : core_text(r, "error", "中止失败");
            } else {
                const json r = ask({{"cmd", "tune.start"}, {"ch", ci + 1},
                                    {"t_high", tds.t_high}, {"t_low", tds.t_low}, {"heat_duty", tds.heat_duty}, {"cool_duty", 80.0}});
                tds.msg_ok = r.value("ok", false);
                tds.msg = tds.msg_ok ? ui::tr("已启动两段式自整定") : core_text(r, "error", "启动失败");
                if (r.value("ok", false)) v.was_tuning = true;
            }
            pid_fetch(ci, true);
            poll();
            tune_dlg_refresh();
            return true;
        }
        return true;
    }

    if (a.rfind("pg:", 0) != 0) return false;
    if (a == "pg:tune") {
        open_tune_dlg();
        return true;
    }
    const int n = static_cast<int>(v.rows.size());
    if (a != "pg:clear") v.clear_armed = false;
    v.msg.clear();
    const bool too_many = n > kPidRows;
    const bool edit = a.rfind("pg:cell:", 0) == 0 || a.rfind("pg:del:", 0) == 0 || a == "pg:add" || a == "pg:clear" || a == "pg:apply";
    if (edit && too_many) { v.msg = ui::fmt("表里有 {0} 行，屏上只放得下 8 行，这张表请在 PC 端改", {std::to_string(n)}); refresh(); return true; }
    if (a.rfind("pg:cell:", 0) == 0 && a.size() >= 11) {
        const int k = a[8] - '0', i = a[10] - '0';
        if (k < 0 || k >= n || i < 0 || i > 5) return true;
        struct Col { const char* key; const char* title; const char* unit; double lo, hi; int dec; };
        static const Col C[6] = {{"temp", "这一行管的温度", "℃", -40, 200, 0}, {"kp", "Kp", "", 0.01, 100, 2}, {"ki", "Ki", "", 0, 1, 4}, {"kd", "Kd", "", 0, 2000, 1},
                                 {"steady_bias", "稳态偏置（维持温差的起点）", "K", -30, 60, 1}, {"heat_ratio", "加热比", "", 0.05, 20, 2}};
        const Col& c = C[i];
        const double cur_v = v.rows[k].contains(c.key) && v.rows[k][c.key].is_number() ? v.rows[k][c.key].get<double>() : 0.0;
        char last[48]; std::snprintf(last, sizeof last, "%.*f %s", c.dec, cur_v, c.unit);
        const std::string key = c.key;
        open_keypad(ui::fmt("通道 {0} · {1} ℃ 这一行 · {2}", {std::to_string(ci + 1), tnum(v.rows[k].value("temp", 0.0)), ui::tr(c.title)}), c.unit, cur_v, c.lo, c.hi, last, "—",
                    [this, ci, k, key](double x) { if (k < static_cast<int>(pv[ci].rows.size())) { pv[ci].rows[k][key] = x; pv[ci].dirty = true; } refresh(); }, c.dec);
        return true;
    }
    if (a.rfind("pg:del:", 0) == 0) { const int k = a[7] - '0'; if (k >= 0 && k < n) { v.rows.erase(k); v.dirty = true; } }
    else if (a == "pg:add") {
        if (n >= kPidRows) v.msg = "最多 8 行";
        else {
            json r = n > 0 ? v.rows[n - 1] : json{{"temp", 20.0}, {"kp", 9.4}, {"ki", 0.06}, {"kd", 130.0}, {"steady_bias", nullptr}, {"heat_ratio", 1.6}};
            if (n > 0) r["temp"] = r.value("temp", 0.0) + 10.0;
            v.rows.push_back(r); v.dirty = true;
        }
    } else if (a == "pg:apply") {
        if (!v.dirty) v.msg = "没有改动";
        else {
            const json r = ask({{"cmd", "gains.set"}, {"ch", ci + 1}, {"rows", v.rows}});
            if (r.value("ok", false)) { v.dirty = false; v.msg = "已应用并存入本通道独立增益表"; pid_fetch(ci, true); }
            else v.msg = core_text(r, "error", "没应用成");
        }
    } else if (a == "pg:undo") { v.dirty = false; pid_fetch(ci, true); v.msg = "已放弃修改"; }
    else if (a == "pg:reload") {
        const json r = ask({{"cmd", "gains.reload"}, {"ch", ci + 1}});
        if (r.value("ok", false)) { v.dirty = false; pid_fetch(ci, true); v.msg = "已从盘上重读本通道增益表"; } else v.msg = core_text(r, "error", "没读成");
    } else if (a == "pg:clear") {
        if (!v.clear_armed) v.clear_armed = true;
        else { v.rows = json::array(); v.dirty = true; v.clear_armed = false; v.msg = "屏上这份已清空；加行后点「应用表格」，或点「放弃修改」找回来"; }
    } else if (a == "pg:look") {
        open_keypad("看某个温度会用什么增益", "℃", v.look_t, -40, 200, tnum(v.look_t) + " ℃", "—", [this, ci](double x) { pv[ci].look_t = x; refresh(); }, 0);
        return true;
    } else if (a == "pg:boost") {
        open_keypad(ui::fmt("通道 {0} · 快速升温加码上限（0 = 不加码）", {std::to_string(ci + 1)}), "K", v.boost, 0, 60, f1(v.boost) + " K", "—", [this, ci](double x) {
            const json r = ask({{"cmd", "settings.set"}, {"ch", ci + 1}, {"fast_boost_max", x}});
            pv[ci].msg2 = r.value("ok", false) ? "已保存并立即生效（当前通道独立参数）" : core_text(r, "error", "没存进去");
            pid_fetch(ci, true);
            refresh();
        }, 1);
        return true;
    }
    refresh();
    return true;
}

void refresh_pid(Ui& u, const json& c, int ci) {
    pid_fetch(ci, false);
    trend_fetch(ci);
    PidView& v = pv[ci];
    const int n = static_cast<int>(v.rows.size());
    const bool run = c.value("running", false);
    const bool tuning = c.value("tuning", false);
    const double tj_sp = c.value("tj_sp", 0.0), duty = c.value("duty", 0.0);

    int nearest = -1;
    if (run && !v.dirty) {
        double best = 1e9;
        for (int k = 0; k < n; k++) {
            const double d = std::fabs(v.rows[k].value("temp", 0.0) - tj_sp);
            if (d < best) { best = d; nearest = k; }
        }
    }
    for (int k = 0; k < kPidRows; k++) {
        const std::string id = "pg.r." + std::to_string(k);
        u.show(id, k < n);
        if (k >= n) continue;
        const json& r = v.rows[k];
        auto opt = [&](const char* key, int dec) { if (!r.contains(key) || !r[key].is_number()) return std::string("—"); char b[32]; std::snprintf(b, sizeof b, "%.*f", dec, r[key].get<double>()); std::string s = b; if (s[0] == '-') s.replace(0, 1, "−"); return s; };
        const std::string cells[6] = {tnum(r.value("temp", 0.0)), f2(r.value("kp", 0.0)), f4(r.value("ki", 0.0)), f1(r.value("kd", 0.0)), opt("steady_bias", 1), opt("heat_ratio", 2)};
        for (int i = 0; i < 6; i++) {
            const std::string cid = id + ".c" + std::to_string(i);
            u.set_text(cid, cells[i]);
            if (lv_obj_t* box = u.box(cid)) if (lv_obj_t* lb = u.label(cid)) { lv_obj_update_layout(lb); lv_obj_set_width(box, lv_obj_get_width(lb) + 18 * UI_SCALE); }
        }
        u.show(id + ".bar", k == nearest);
        if (lv_obj_t* o = u.box(id)) if (u.changed(id + "bg", k == nearest)) bg(o, 0xE3EEFA, k == nearest ? 255 : 0);
    }

    u.set_text_raw("pg.sub", ui::fmt("通道 {0} 独立增益表 · 加热制冷共用", {std::to_string(ci + 1)}));
    const double tune_pct = v.tune_st.value("progress", 0.0) * 100.0;
    const std::string info = tuning ? ui::fmt("自整定运行中 · {0} %（点「自整定中」看进度）", {f0(tune_pct)})
                           : !v.msg.empty() ? ui::tr(v.msg)
                           : v.dirty ? ui::tr("有改动还没应用（点「应用表格」才生效）")
                           : nearest >= 0 ? ui::fmt("{0} 行 · 在用 {1} ℃ 附近", {std::to_string(n), tnum(v.rows[nearest].value("temp", 0.0))})
                           : !v.tuned_at.empty() ? ui::fmt("{0} 行 · 整定于 {1}", {std::to_string(n), v.tuned_at})
                                                 : ui::fmt("{0} 行", {std::to_string(n)});
    u.set_text_raw("pg.info", info);
    u.show("pg.sub", !tuning && v.msg.empty() && !v.dirty);
    u.set_text_color("pg.info", tuning ? 0x007A5A : !v.msg.empty() ? 0x00559C : v.dirty ? 0xE07B00 : 0x8792A0);
    u.set_text("pg.clear.t", v.clear_armed ? "再点一次确认" : "清空这张表");
    u.set_text("pg.tune.t", tuning ? "自整定中…" : "一键自整定");

    // 此刻在用
    if (tuning) {
        u.set_text_raw("pg.use.1", v.tune_st.value("note", ui::tr("正在执行两段式硬件自整定…")));
        u.set_text_raw("pg.use.2", ui::fmt("当前 Tj {0} ℃ · 出力 {1} %（{2}）",
                                           {f1(c.value("tj", 0.0)), f0(std::fabs(duty)), ui::tr(duty > 0 ? "TEC 制冷" : duty < 0 ? "加热棒" : "零出力观察")}));
    } else if (run) {
        u.set_text_raw("pg.use.1", pid_gains_text(v.rows, tj_sp));
        u.set_text_raw("pg.use.2", ui::fmt("夹套目标 {0} ℃ · 出力 {1} %（{2}）", {f1(tj_sp), f0(std::fabs(duty)), ui::tr(duty > 0 ? "TEC 制冷" : "加热棒")}));
    } else {
        u.set_text("pg.use.1", "没在控温");
        u.set_text("pg.use.2", "开始控温以后，这里显示当时按夹套目标插值出来的那组增益");
    }
    u.set_text("pg.look.t", tnum(v.look_t));
    u.set_text_raw("pg.look.r", ui::fmt("℃ 会用：{0}", {pid_gains_text(v.rows, v.look_t, false)}));

    // 稳定度：拿趋势数据里最近 5 分钟的釜温算
    const TrendData& d = tdat[ci];
    std::string s1 = ui::tr("数据还不够（要攒够半分钟）"), s2 = (run || tuning) ? "" : ui::tr("没在控温");
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
    if (run || tuning) s2 = ui::fmt("出力 {0} %，{1}", {f0(std::fabs(duty)), ui::tr(std::fabs(duty) >= 89.5 ? "已经顶到头了（再大也加不上去）" : "没有顶到头")});
    u.set_text_raw("pg.st.1", s1); u.set_text_raw("pg.st.2", s2);

    // 外环与本通道硬件特征参数
    if (run) {
        u.set_text_raw("pg.out.1", ui::fmt("维持温差（估）{0} K　·　釜的时间常数（估）{1} s",
                                            {std::string(c.value("b_hat", 0.0) >= 0 ? "+" : "") + f1(c.value("b_hat", 0.0)), f0(c.value("tau_hat", 0.0))}));
        u.set_text_raw("pg.out.2", ui::fmt("快速升温此刻加码 {0} K　·　夹套目标 {1} ℃",
                                            {std::string(c.value("boost", 0.0) >= 0 ? "+" : "") + f1(c.value("boost", 0.0)), f1(tj_sp)}));
    } else {
        u.set_text("pg.out.1", tuning ? "正在自整定硬件特性…" : "没在控温");
        u.set_text("pg.out.2", "开始控温以后，这里显示外环此刻估出来的维持温差和时间常数");
    }
    u.set_text_raw("pg.out.3", ui::fmt("本通道标定：釜壁滞后 tw {0} s · 刹车速率 {1} K/s · 电热余热 {2} K",
                                       {f0(v.tw), f2(v.rc_nom), f1(v.hmom)}));
    u.set_text("pg.boost", tnum(v.boost));
    u.set_text("pg.boost.n", v.boost > 0 ? "升温时夹套最多比稳妥算法多拉高这么多；0 = 不加码" : "0 = 不加码（稳妥，升温慢一些）");
    u.set_text_raw("pg.msg", !v.msg2.empty() ? ui::tr(v.msg2)
                             : !v.tuned_at.empty() ? ui::fmt("两通道独立存盘 · 本通道整定于 {0}", {v.tuned_at})
                                                   : ui::tr("两通道参数各自独立；换釜或换导热介质建议点「一键自整定」"));
    u.set_text_color("pg.msg", !v.msg2.empty() ? 0x00559C : 0x8792A0);
    if (tds.open) tune_dlg_refresh();
}
