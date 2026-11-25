// 换界面语言（用户 2026-11-25 要的；选择框是从两版里选的「乙」：竖列表）。这个文件直接嵌在 main.cpp 的 App 里面。
//
// 怎么换：界面上所有的字都从 ui_kit 的 txt() 和 Ui::set_text() 过一道词表
//（assets/lang/strings.tsv，第一列中文、后面每种语言一列），换语言就是换一列。
// 所以 ui/gen/pages_*.inc 那两万行生成代码一个字都不用改，补一种语言也只是往表里加一列。
//
// 换完要把已经搭好的页面全部拆掉重搭：页面上的字是搭那一下写死在 LVGL 对象里的，不重搭不会变。
// 拆的动作不能在点击事件里做（正要被删的就是刚被点的那个按钮），所以走 lv_async_call，等这一轮事件派完再动。
//
// 语言存在控制核心的设置里（settings 的 ui_lang），不是存本机文件：换人开机、界面重装都还是上次那一种。
// 连不上核心的时候就还是中文——那会儿屏上是「未连上控制核心」那一屏，也没别的可看。

static constexpr struct { const char* code; const char* flag_native; } kLangs[] = {
    {"zh", "简体中文"}, {"en", "English"}, {"ja", "日本語"}, {"ko", "한국어"},
    {"de", "Deutsch"}, {"fr", "Français"}, {"es", "Español"}, {"ru", "Русский"}};
static constexpr int kLangN = static_cast<int>(sizeof kLangs / sizeof kLangs[0]);

Page langdlg;
std::string lang_code = "zh";
std::string lang_msg;              // 点了没法用的那一行时，底下那句话换成为什么

void lang_dlg_show() {
    if (!langdlg.scr) {
        langdlg.scr = lv_obj_create(lv_layer_top());
        lv_obj_remove_style_all(langdlg.scr);
        lv_obj_set_size(langdlg.scr, W, H);
        lv_obj_add_flag(langdlg.scr, LV_OBJ_FLAG_CLICKABLE);
        langdlg.ui = std::make_unique<Ui>();
        langdlg.ui->on_action = [this](const std::string& x) { action(x); };
        for (const PageDef& d : kPages) if (std::string(d.name) == "LangDlg") { d.build(langdlg.scr, *langdlg.ui); lv_obj_update_layout(langdlg.scr); }
    }
    if (!langdlg.ui) return;
    Ui& u = *langdlg.ui;
    for (int i = 0; i < kLangN; i++) {
        const std::string code = kLangs[i].code, id = "lg." + std::to_string(i);
        const bool on = code == lang_code, has_font = ui::language_has_font(code);
        const int pct = ui::language_percent(code);
        // 选中的那一行：底色浅蓝、名字变蓝、圆点填实。都是同一个元素换颜色——
        // 设计稿里 display:none 的东西运行时根本不存在（见 ui/tools/dump.js），不能画两套
        if (u.changed(id + ".sel", on)) {
            if (lv_obj_t* row = u.box(id)) bg(row, 0xF2F7FD, on ? 255 : 0);
            if (lv_obj_t* r = u.box(id + ".ring")) bd(r, on ? 0x00559C : 0xA9B7C6, 255, 2 * UI_SCALE, LV_BORDER_SIDE_FULL);
            if (lv_obj_t* d = u.box(id + ".in")) bg(d, 0x00559C, on ? 255 : 0);
            // 名字的颜色跟着「选中」走，不能挂在下面那个标签的判断里：选中换了、标签没换，颜色就不会跟着动
            u.set_text_color(id + ".n", !has_font ? 0x9AA5B1 : on ? 0x00559C : 0x1F2A35);
        }
        // 右边那个小标签说实话：字体没放进来的语言点了也是一屏方框，所以先说「缺字体」
        const int look = !has_font ? 3 : pct >= 100 ? 0 : pct > 0 ? 1 : 2;
        // 语言名要保持它本来的样子（English 就是 English、日本語 就是 日本語），不能跟着界面语言翻
        u.set_text_raw(id + ".n", kLangs[i].flag_native);
        if (u.changed(id + ".tag", look)) {
            u.set_text(id + ".tag", look == 0 ? "完整" : look == 1 ? "未齐" : look == 2 ? "未译" : "缺字体");
            u.set_text_color(id + ".tag", look == 0 ? 0x0A8A4F : look == 1 ? 0xC77700 : 0x8792A0);
            if (lv_obj_t* t = u.box(id + ".tag")) bg(t, look == 0 ? 0xE7F6EE : look == 1 ? 0xFDF3E3 : 0xF1F3F6, 255);
        }
    }
    u.set_text("lg.msg", lang_msg.empty() ? "点一下马上就换，不合适再点回来。标「未齐」的，没译到的条目显示英文" : lang_msg);
    u.set_text_color("lg.msg", lang_msg.empty() ? 0x8792A0 : 0xD64545);
}

// 正在显示的那一页，等新的搭好挂上去以后才让 LVGL 自己找空当删——它还会再碰一次那张旧的。
// 连同它的 Ui 一起留到下一次换语言才扔：Ui 里记的全是那页上各个对象的地址，对象没真删掉之前不能先把表扔了
std::vector<Page> retired;

// 换语言以后把搭好的页面全部拆掉重搭：页面上的字是搭那一下写死在 LVGL 对象里的，不重搭不会变
void relayout_for_language() {
    retired.clear();                                // 上一次留下的那一页，这会儿早删干净了
    const std::string keep = cur.empty() ? "Main" : cur;
    const bool lang_open = langdlg.scr && !lv_obj_has_flag(langdlg.scr, LV_OBJ_FLAG_HIDDEN);
    // 正显示着的那一张，不管它是哪一页、还是「未连上控制核心」那一屏，都不能现在删：
    // LVGL 换上新的之前还会再碰它一次。第一次自查时偶尔崩就是这么来的——刚连上核心、
    // 屏上还挂着「未连上」那一屏的时候换语言，把它删了
    lv_obj_t* active = lv_screen_active();
    auto hold = [&](Page& p) { retired.push_back(std::move(p)); p = Page{}; };
    for (Page* p : aux_pages()) if (p->scr) {
        if (p->scr == active) { hold(*p); continue; }
        lv_obj_delete(p->scr); p->scr = nullptr; p->ui.reset();
    }
    for (auto it = pages.begin(); it != pages.end();) {
        if (it->second.scr == active) { hold(it->second); it = pages.erase(it); continue; }
        if (it->second.scr) lv_obj_delete(it->second.scr);
        it = pages.erase(it);
    }
    // 几个页面把自己搭出来的东西记在表里（任务序列的参数区、结晶曲线的查看/编辑卡、画曲线的框）。
    // 那些东西是长在刚才删掉的页面上的，表不清掉的话下一次刷新就会去碰已经没了的对象（踩过：设备控制页换语言必崩）
    seq_parts.clear();
    cry_parts.clear();
    curves.clear();
    ovplots.clear();
    warm_next = 0;                                  // 提前准备那一轮也要重来：字形换了一套
    cur.clear();
    show(keep);                                     // 搭一张新的并挂上去
    for (Page& p : retired) if (p.scr) { lv_obj_delete_async(p.scr); p.scr = nullptr; }
    if (lang_open) { lang_dlg_show(); lv_obj_remove_flag(langdlg.scr, LV_OBJ_FLAG_HIDDEN); }
}

void set_lang(const std::string& code) {
    if (code == lang_code) return;
    lang_code = code;
    ui::set_language(code);
    ask({{"cmd", "settings.set"}, {"ui_lang", code}});       // 存不进去也照样换，只是下次开机回到原来那种
    // 正在派点击事件，这会儿删掉被点的那个按钮会炸；等这一轮派完再拆
    lv_async_call([](void* p) { static_cast<App*>(p)->relayout_for_language(); }, this);
}

bool lang_action(const std::string& a) {
    if (a == "lg:open") { lang_msg.clear(); lang_dlg_show(); lv_obj_remove_flag(langdlg.scr, LV_OBJ_FLAG_HIDDEN); return true; }
    if (a == "lg:done") { if (langdlg.scr) lv_obj_add_flag(langdlg.scr, LV_OBJ_FLAG_HIDDEN); return true; }
    if (a.rfind("lg:", 0) != 0 || a.size() != 4 || a[3] < '0' || a[3] > '9') return false;
    const int i = a[3] - '0';
    if (i < 0 || i >= kLangN) return true;
    const std::string code = kLangs[i].code;
    if (!ui::language_has_font(code)) {
        // 字体没放进来：换过去只能看见一屏方框，不如不换，并且说清楚缺的是什么
        lang_msg = std::string(kLangs[i].flag_native) + "：这台机器上没有它要的字体，放进 assets/fonts 才能用";
        lang_dlg_show();
        return true;
    }
    lang_msg.clear();
    set_lang(code);
    return true;
}

// 开机时把上次用的语言捡回来。settings.get 要等连上核心才答得出，所以连上之前先是中文
void lang_pick_up(const json& settings) {
    const std::string want = settings.value("ui_lang", std::string("zh"));
    if (want == lang_code || !ui::language_has_font(want)) return;
    bool known = false;
    for (int i = 0; i < kLangN; i++) if (want == kLangs[i].code) known = true;
    if (known) set_lang(want);
}
