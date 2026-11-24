// 文字键盘弹窗（起名字、填料液名、写提示语用）。这个文件直接嵌在 main.cpp 的 App 里面。
//
// 中文用拼音：打拼音字母，中间一行出候选，点一个就上去。候选分三种，按这个顺序排：
//   词   ——词库 phrase_dict.inc 里的（实验室里起名字常用的三百多个词）。整词拼音打全、打一半、只打每个字的头一个字母（简拼）都能出，
//           比如「重结晶」打 chongjiejing、chongj、cjj 都行；
//   字   ——字库 pinyin_dict.inc（6800 多个简体常用字，常用的排前面）；
//   连打 ——词库里没有的词可以把拼音一口气打完再一个字一个字选：打 fanying，先出 fan 的字，选了「反」剩下 ying 接着选。
// 英文、数字、常用符号直接打。
// 版面是设计稿生成的（TextDlg），键上的字由这里按「中 / 英、大小写、数字符号」三种状态换。

// （字库表 kPinyin 在 main.cpp 开头引进来：它是一张全局的表，不能放在 App 里面）

Page tkb;
struct TextKb {
    std::string text, py;            // 已经上去的字；正在打的拼音
    struct Cand { std::string text; size_t eat; };      // 候选的字或词；选了它要吃掉拼音开头的几个字母
    std::vector<Cand> cand;          // 当前拼音的全部候选
    std::vector<size_t> starts{0};   // 候选一页放几个不一定（词比字宽）：记着每一页是从第几个候选开始的，最后一个是当前页
    size_t shown = 0;                // 当前页上放了几个
    bool chinese = true, upper = false, symbols = false;
    size_t max_chars = 30;
    std::function<void(const std::string&)> ok;
} tk;
static constexpr int kCand = 8;
static constexpr size_t kPyMax = 24;      // 一次最多打这么多拼音字母（够打一个六个字的词）

static size_t utf8_count(const std::string& s) { size_t n = 0; for (unsigned char c : s) if ((c & 0xC0) != 0x80) n++; return n; }
static void utf8_pop(std::string& s) { while (!s.empty()) { const unsigned char c = static_cast<unsigned char>(s.back()); s.pop_back(); if ((c & 0xC0) != 0x80) break; } }
static std::vector<std::string> utf8_split(const char* s) {
    std::vector<std::string> out;
    for (const char* p = s; *p;) { const unsigned char c = static_cast<unsigned char>(*p); const int n = c >= 0xF0 ? 4 : c >= 0xE0 ? 3 : c >= 0xC0 ? 2 : 1; out.emplace_back(p, static_cast<size_t>(n)); p += n; }
    return out;
}

// 四排键在三种状态下各是什么。第 3 排头尾和第 4 排是功能键，不在这张表里
static const char* tk_row(int r, bool symbols) {
    static const char* letters[3] = {"qwertyuiop", "asdfghjkl", "zxcvbnm"};
    static const char* syms[3] = {"1234567890", "-_/:()%#+", ".,?!~*="};
    return (symbols ? syms : letters)[r];
}

void tk_lookup() {
    tk.cand.clear(); tk.starts.assign(1, 0);
    if (tk.py.empty()) return;
    const std::string& py = tk.py;
    const size_t n = py.size();
    auto have = [&](const std::string& t) { for (auto& c : tk.cand) if (c.text == t) return true; return false; };
    auto add = [&](const std::string& t, size_t eat) { if (!have(t)) tk.cand.push_back({t, eat}); };
    // 一、词。先出完全对上的（整词拼音或简拼），再出只打了开头的；只打了一个字母时不出词——那样出来的太多太杂
    if (n >= 2) {
        for (const auto& e : kPhrases) if (py == e.py || py == e.ab) add(e.text, n);
        size_t more = 0;
        for (const auto& e : kPhrases) {
            if (more >= 16) break;
            const bool full = n >= 3 && std::strncmp(e.py, py.c_str(), n) == 0, ab = std::strncmp(e.ab, py.c_str(), n) == 0;
            if ((full || ab) && !have(e.text)) { tk.cand.push_back({e.text, n}); more++; }
        }
        // 一口气打了好几个词（fanyingguolv）：开头正好是词库里某个词的整词拼音，就先出这个词，选了剩下的接着出
        for (const auto& e : kPhrases) { const size_t L = std::strlen(e.py); if (L < n && std::strncmp(e.py, py.c_str(), L) == 0) add(e.text, L); }
    }
    // 二、字：拼音完全对上的音节
    bool exact = false;
    for (const auto& e : kPinyin) if (py == e.py) { exact = true; for (auto& ch : utf8_split(e.chars)) add(ch, n); break; }
    // 三、连打：整串不是一个音节时，取开头最长的那个音节出字，选了只吃掉这一段，剩下的接着选
    if (!exact) for (size_t len = std::min<size_t>(6, n - 1); len >= 1; len--) {
        const std::string head = py.substr(0, len);
        bool hit = false;
        for (const auto& e : kPinyin) if (head == e.py) { hit = true; for (auto& ch : utf8_split(e.chars)) add(ch, len); break; }
        if (hit) break;
    }
    // 四、音节只打了开头（比如只打了 zh）：把以它开头的各个音节的头几个字凑上，不用打全也能选
    for (const auto& e : kPinyin) {
        if (py == e.py || std::strncmp(e.py, py.c_str(), n) != 0) continue;
        const std::vector<std::string> more = utf8_split(e.chars);
        for (size_t i = 0; i < more.size() && i < 4; i++) add(more[i], n);
    }
}

void tk_show() {
    Ui& u = *tkb.ui;
    u.set_text("tk.text", tk.text + "▏");
    u.set_text("tk.count", std::to_string(utf8_count(tk.text)) + " / " + std::to_string(tk.max_chars));
    u.set_text("tk.py", tk.py.size() > 8 ? "…" + tk.py.substr(tk.py.size() - 7) : tk.py);      // 拼音那一格只放得下八九个字母，长了只留尾巴
    // 候选一个挨一个从左往右排：字占一格，词按字数放宽；排到翻页键跟前放不下了就留给下一页
    const size_t first = tk.starts.back();
    lv_obj_t* b0 = u.box("tk.c.0"); lv_obj_t* prev = u.box("tk.cprev");
    const int gap = 8 * UI_SCALE, unit = 72 * UI_SCALE;
    int x = b0 ? lv_obj_get_x(b0) : 0;
    const int limit = prev ? lv_obj_get_x(prev) - gap : 800 * UI_SCALE;
    tk.shown = 0;
    for (int i = 0; i < kCand; i++) {
        const size_t k = first + static_cast<size_t>(i);
        const std::string id = "tk.c." + std::to_string(i);
        lv_obj_t* box = u.box(id); lv_obj_t* lb = u.label(id);
        bool on = box && lb && k < tk.cand.size() && tk.shown == static_cast<size_t>(i);
        if (on) {
            u.set_text(id, tk.cand[k].text);
            lv_obj_update_layout(lb);
            const int w = std::max(unit, lv_obj_get_width(lb) + 28 * UI_SCALE);
            if (x + w > limit && i > 0) on = false;
            else { lv_obj_set_x(box, x); lv_obj_set_width(box, w); lv_obj_set_x(lb, (w - lv_obj_get_width(lb)) / 2); x += w + gap; tk.shown++; }
        }
        u.show(id, on);
    }
    const bool more = first + tk.shown < tk.cand.size(), back = tk.starts.size() > 1;
    u.show("tk.cprev", more || back); u.show("tk.cnext", more || back);
    for (int r = 0; r < 3; r++) {
        const char* row = tk_row(r, tk.symbols);
        const int n = static_cast<int>(std::strlen(row));
        for (int i = 0; i < n; i++) {
            char c = row[i];
            if (!tk.symbols && tk.upper && !tk.chinese) c = static_cast<char>(std::toupper(static_cast<unsigned char>(c)));
            u.set_text("tk.k." + std::to_string(r) + "." + std::to_string(r == 2 ? i + 1 : i), std::string(1, c));
        }
        // 符号那一套第 2、3 排和字母的个数一样（9 个、7 个），不用藏键
    }
    u.set_text("tk.k.2.0", tk.symbols ? "℃" : tk.chinese ? "—" : tk.upper ? "小写" : "大写");
    u.set_text("tk.k.3.0", tk.symbols ? "字母" : "123");
    u.set_text("tk.k.3.1", tk.chinese ? "中" : "英");
    if (lv_obj_t* o = u.box("tk.k.3.1")) if (u.changed("cn", tk.chinese)) bg(o, tk.chinese ? 0xD6E6F7 : 0xE3E9F1, 255);
}

void open_text_kb(const std::string& title, const std::string& initial, size_t max_chars, std::function<void(const std::string&)> ok) {
    if (!tkb.scr) {
        tkb.scr = lv_obj_create(lv_layer_top());
        lv_obj_remove_style_all(tkb.scr);
        lv_obj_set_size(tkb.scr, W, H);
        lv_obj_add_flag(tkb.scr, LV_OBJ_FLAG_CLICKABLE);
        tkb.ui = std::make_unique<Ui>();
        tkb.ui->on_action = [this](const std::string& a) { action(a); };
        for (const PageDef& d : kPages) if (std::string(d.name) == "TextDlg") { d.build(tkb.scr, *tkb.ui); lv_obj_update_layout(tkb.scr); }
    }
    tk.text = initial; tk.py.clear(); tk.cand.clear(); tk.starts.assign(1, 0); tk.upper = false; tk.symbols = false;
    tk.max_chars = max_chars; tk.ok = std::move(ok);
    tkb.ui->set_text("tk.title", title);
    lv_obj_remove_flag(tkb.scr, LV_OBJ_FLAG_HIDDEN);
    lv_obj_move_foreground(tkb.scr);
    tk_show();
}

void tk_put(const std::string& s) { if (utf8_count(tk.text) < tk.max_chars) tk.text += s; }

bool text_kb_action(const std::string& a) {
    if (a.rfind("tk:", 0) != 0) return false;
    if (!tkb.scr) return true;
    if (a == "tk:close") { lv_obj_add_flag(tkb.scr, LV_OBJ_FLAG_HIDDEN); return true; }
    // 选了第 k 个候选：字上去，拼音吃掉它用了的那一段，剩下的接着出候选
    auto pick = [this](size_t k) { if (k >= tk.cand.size()) return; const TextKb::Cand c = tk.cand[k]; tk_put(c.text); tk.py.erase(0, std::min(c.eat, tk.py.size())); tk_lookup(); };
    if (a == "tk:cprev") { if (tk.starts.size() > 1) tk.starts.pop_back(); }
    else if (a == "tk:cnext") { if (tk.starts.back() + tk.shown < tk.cand.size()) tk.starts.push_back(tk.starts.back() + tk.shown); }
    else if (a.rfind("tk:cand:", 0) == 0) {
        pick(tk.starts.back() + static_cast<size_t>(a[8] - '0'));
    } else if (a.rfind("tk:key:", 0) == 0 && a.size() >= 10) {
        const int r = a[7] - '0', i = std::atoi(a.c_str() + 9);
        if (r == 3) {
            if (i == 0) { tk.symbols = !tk.symbols; tk.py.clear(); tk_lookup(); }
            else if (i == 1) { tk.chinese = !tk.chinese; tk.symbols = false; tk.py.clear(); tk_lookup(); }
            else if (i == 2) {                                           // 空格：有拼音在打就是「选第一个字」
                if (!tk.py.empty() && !tk.cand.empty()) pick(tk.starts.back());
                else if (tk.py.empty()) tk_put(" ");
            }
            else if (i == 3 || i == 4) { if (!tk.py.empty()) { tk_put(tk.py); tk.py.clear(); tk_lookup(); } tk_put(i == 3 ? "-" : "."); }
            else if (i == 5) {
                // 确定：还有没选字的拼音，就先把这几个字母原样上去，再点一次才真的收起（免得把打了一半的东西悄悄丢掉）
                if (!tk.py.empty()) { tk_put(tk.py); tk.py.clear(); tk_lookup(); }
                else {
                    std::string out = tk.text;
                    while (!out.empty() && out.back() == ' ') out.pop_back();
                    lv_obj_add_flag(tkb.scr, LV_OBJ_FLAG_HIDDEN);
                    if (tk.ok) tk.ok(out);
                    return true;
                }
            }
        } else if (r == 2 && i == 0) {
            if (tk.symbols) tk_put("℃"); else if (tk.chinese) tk_put("—"); else tk.upper = !tk.upper;
        } else if (r == 2 && i == 8) {
            if (!tk.py.empty()) { tk.py.pop_back(); tk_lookup(); } else utf8_pop(tk.text);
        } else if (r >= 0 && r <= 2) {
            const char* row = tk_row(r, tk.symbols);
            const int k = r == 2 ? i - 1 : i;
            if (k >= 0 && k < static_cast<int>(std::strlen(row))) {
                char c = row[k];
                if (tk.symbols) tk_put(std::string(1, c));
                else if (tk.chinese) { if (tk.py.size() < kPyMax) { tk.py += c; tk_lookup(); } }
                else tk_put(std::string(1, tk.upper ? static_cast<char>(std::toupper(static_cast<unsigned char>(c))) : c));
            }
        }
    }
    tk_show();
    return true;
}
