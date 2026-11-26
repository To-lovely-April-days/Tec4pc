// 排版自查（只在 Mac / 开发机上用，现场跑不到）：把当前这一页上所有的字量一遍，报三种毛病——
//   · 被裁：字伸到包着它的那块之外，画出来是半截的
//   · 压字：两段字叠在一起
//   · 压控件：字叠到了别人的按钮 / 开关 / 圆点上（不是它自己的外壳）
// 为什么要这个：界面有八种语言，同一个位置的字长短差很多，一页页看图看不过来也看不准。
// 用法：./build/tec_hmi --port 7788 --page Ctl --after 2 --lint
//
// 提示句和报警句另算：它们平时不显示（要命令被拒、要设备真报警才看得见），出图量不到。
//   ./build/tec_hmi --fit ui/tools/fit.tsv
// 这个模式不连核心、不开窗，把每一页搭出来，再把表里每一句摆进它该去的那个位置量一遍。
// 表是 ui/tools/fit.py 从界面代码和控制核心里抽出来的：一行「位置的名字｜中文原句｜占位符填什么」
#pragma once
#include "src/core/lv_obj_private.h"      // 只为读 ext_draw_size（外壳允许往外画多远），自查用

namespace lint {

struct Node { lv_obj_t* o; lv_area_t a; bool label; bool solid; };

static void gather(lv_obj_t* o, std::vector<Node>& out) {
    // 藏起来的那一片整块跳过：结晶曲线的查看 / 编辑卡这种，平时挂在页面上但不显示，量进来全是假的重叠
    if (lv_obj_has_flag(o, LV_OBJ_FLAG_HIDDEN) || lv_obj_get_style_opa(o, 0) == 0) return;
    lv_area_t a; lv_obj_get_coords(o, &a);
    const bool label = lv_obj_check_type(o, &lv_label_class) && lv_obj_get_style_text_opa(o, 0) > 0;
    // 图标也算「别人的地盘」：设计稿里开关、页签上的小图是图片，压上去一样难看
    // 半个屏幕那么大的底色块不算「别人的地盘」：弹窗底下那张暗色罩子、页面的大底板，
    // 字本来就画在它们上面
    const int32_t area = (a.x2 - a.x1 + 1) * (a.y2 - a.y1 + 1);
    const bool solid = !label && area < W * H / 3 &&
                       (lv_obj_get_style_bg_opa(o, 0) > 0 || lv_obj_get_style_border_width(o, 0) > 0 || lv_obj_check_type(o, &lv_image_class));
    out.push_back({o, a, label, solid});
    for (uint32_t i = 0; i < lv_obj_get_child_count(o); i++) gather(lv_obj_get_child(o, i), out);
}

static bool is_ancestor(lv_obj_t* maybe, lv_obj_t* of) {
    for (lv_obj_t* p = of; p; p = lv_obj_get_parent(p)) if (p == maybe) return true;
    return false;
}

static int overlap(const lv_area_t& x, const lv_area_t& y) {
    const int w = std::min(x.x2, y.x2) - std::max(x.x1, y.x1) + 1;
    const int h = std::min(x.y2, y.y2) - std::max(x.y1, y.y1) + 1;
    return w > 0 && h > 0 ? std::min(w, h) : 0;
}

static std::string brief(lv_obj_t* l) {
    std::string s = lv_label_get_text(l) ? lv_label_get_text(l) : "";
    for (char& c : s) if (c == '\n') c = ' ';
    if (s.size() > 42) { size_t cut = 42; while (cut > 0 && (static_cast<unsigned char>(s[cut]) & 0xC0) == 0x80) cut--; s = s.substr(0, cut) + "…"; }
    return s;
}

// slack：差这么几个像素不算（圆角、字形两边的空白量不准，报出来全是噪声）
// only：只量这一段字（--fit 一句一句试的时候用），nullptr 就是整页都量
static int one(const char* page, lv_obj_t* root, int slack, lv_obj_t* only = nullptr, const char* tag = "") {
    std::vector<Node> v;
    gather(root, v);
    int bad = 0;
    // UI_LINT_DUMP=1：把这一页上量到的东西全列出来，查「明明看着压上了却没报」用
    if (std::getenv("UI_LINT_DUMP")) for (const Node& n : v)
        std::printf("  %-4s %4d,%4d %4dx%-4d rel %4d %s\n", n.label ? "字" : n.solid ? "块" : "·", n.a.x1, n.a.y1,
                    n.a.x2 - n.a.x1 + 1, n.a.y2 - n.a.y1 + 1, static_cast<int>(lv_obj_get_x(n.o)), n.label ? brief(n.o).c_str() : "");
    auto say = [&](const char* kind, const std::string& what) {
        std::printf("%-10s %-18s %-6s %s\n", page, tag, kind, what.c_str()); bad++;
    };
    for (const Node& n : v) {
        if (!n.label || n.a.x2 < n.a.x1) continue;
        if (only && n.o != only) continue;
        // 往上找第一个「管得住它」的祖先：会裁剪的（伸出去就被切掉），或者带底色 / 边框的（按钮、小牌子，伸出去就糊在外面）
        for (lv_obj_t* p = lv_obj_get_parent(n.o); p; p = lv_obj_get_parent(p)) {
            const bool clips = !lv_obj_has_flag(p, LV_OBJ_FLAG_OVERFLOW_VISIBLE);
            const bool box = lv_obj_get_style_bg_opa(p, 0) > 0 || lv_obj_get_style_border_width(p, 0) > 0;
            if (!clips && !box) continue;
            lv_area_t pa; lv_obj_get_coords(p, &pa);
            const int l = pa.x1 - n.a.x1, r = n.a.x2 - pa.x2, t = pa.y1 - n.a.y1, b = n.a.y2 - pa.y2;
            const int worst = std::max(std::max(l, r), std::max(t, b));
            if (worst > slack) say(clips ? "被裁" : "出框", "「" + brief(n.o) + "」 超出 " + std::to_string(worst) + " px");
            break;
        }
        // 不裁剪的外壳也不是无限大：LVGL 只多画 ext_draw 那一圈（见 ui_kit.cpp 的 ext_cb，120 px），
        // 再往外就画不出来了。靠右对齐的长句子往左伸得太远会被切掉开头，屏上看着像少了半句
        if (lv_obj_t* par = lv_obj_get_parent(n.o)) {
            lv_area_t pa; lv_obj_get_coords(par, &pa);
            const int out = std::max(pa.x1 - n.a.x1, n.a.x2 - pa.x2);
            const int room = par->spec_attr ? static_cast<int>(par->spec_attr->ext_draw_size) : 0;   // 外壳实际允许往外画多远
            if (out > room) say("画不全", "「" + brief(n.o) + "」 伸出外壳 " + std::to_string(out) + " px，只画得出 " + std::to_string(room));
        }
    }
    for (size_t i = 0; i < v.size(); i++) {
        if (!v[i].label) continue;
        if (only && v[i].o != only) continue;
        // j 要从头走一遍，不能只看 i 后面的：开关、圆点这些常常排在字的前面（先画底、后画字）
        for (size_t j = 0; j < v.size(); j++) {
            if (i == j) continue;
            if (!v[j].label && !v[j].solid) continue;
            if (v[j].label && j < i && !only) continue;   // 两段字互相压，只报一次（只量一段时不省：对方排前面也要报）
            if (is_ancestor(v[i].o, v[j].o) || is_ancestor(v[j].o, v[i].o)) continue;
            const int ov = overlap(v[i].a, v[j].a);
            if (ov <= slack) continue;
            if (v[j].label) say("压字", "「" + brief(v[i].o) + "」 × 「" + brief(v[j].o) + "」 " + std::to_string(ov) + " px");
            else if (!ui::is_on_art(v[i].o)) say("压控件", "「" + brief(v[i].o) + "」 压住一块控件 " + std::to_string(ov) + " px");
        }
    }
    return bad;
}

// 当前这一页，加上盖在它上面的弹窗（键盘、确认框、换语言那张挂在最上面那一层）。
// 两层分开量：弹窗底下那张暗色罩子盖住整屏，混在一起量的话满篇都是「压住」
static int run(const char* page, int slack = 2) {
    int bad = one(page, lv_screen_active(), slack);
    if (lv_obj_t* top = lv_layer_top()) if (lv_obj_get_child_count(top) > 0) bad += one(page, top, slack);
    return bad;
}

// ── 提示句和报警句：一句一句摆进去量 ──
// 为什么非这么做不可：这些话平时屏上没有（命令被拒、设备报警、U 盘没插……才冒一句），
// 出图和 --lint 都量不到；而它们恰恰是最长的句子，换成英文、德文最容易放不下。
// 一句一句来，每句都把那一页重新搭一遍：上一句要是把外壳撑宽了，留着量下一句就量不准了
static int fit(const std::string& path, const ui::PageDef* pages, size_t np, int slack = 2) {
    struct Job { std::string id, zh, fill; int row; };
    std::vector<Job> jobs;
    std::FILE* f = std::fopen(path.c_str(), "rb");
    if (!f) { std::printf("打不开 %s\n", path.c_str()); return -1; }
    std::string all;
    char buf[4096]; size_t n;
    while ((n = std::fread(buf, 1, sizeof buf, f)) > 0) all.append(buf, n);
    std::fclose(f);
    for (size_t i = 0, j; i < all.size(); i = j + 1) {
        j = all.find('\n', i); if (j == std::string::npos) j = all.size();
        std::string line = all.substr(i, j - i);
        if (!line.empty() && line.back() == '\r') line.pop_back();
        if (line.empty() || line[0] == '#') continue;
        const size_t a = line.find('\t'); if (a == std::string::npos) continue;
        const size_t b = line.find('\t', a + 1);
        jobs.push_back({line.substr(0, a),
                        line.substr(a + 1, b == std::string::npos ? std::string::npos : b - a - 1),
                        b == std::string::npos ? std::string() : line.substr(b + 1),
                        static_cast<int>(jobs.size()) + 1});
    }

    // 先搭一遍每一页，记下「哪个位置在哪一页上」。一个位置只在一页上有
    std::map<std::string, size_t> where;
    size_t nfit = 0;
    const char* const* fids = ui::fit_ids(nfit);
    std::vector<std::string> lost(fids, fids + nfit);        // 「拦长句」的名单里，哪些名字已经对不上页面了
    for (size_t k = 0; k < np; k++) {
        lv_obj_t* scr = lv_obj_create(nullptr);
        lv_obj_remove_style_all(scr); lv_obj_set_size(scr, W, H);
        ui::Ui u; pages[k].build(scr, u);
        for (const Job& jb : jobs) if (!where.count(jb.id) && u.has_text(jb.id)) where[jb.id] = k;
        for (size_t q = 0; q < lost.size();) {
            if (u.has_text(lost[q])) lost.erase(lost.begin() + static_cast<long>(q)); else q++;
        }
        lv_obj_delete(scr);
    }
    // 名单里的名字页面上没有 = 那个位置改了名，长句不再被拦着，会重新画到别人身上去
    for (const std::string& t : lost) std::printf("── 拦长句的名单里「%s」在哪一页上都找不到（ui_kit.cpp 的 kFitIds）\n", t.c_str());

    int bad = 0, done = 0;
    std::vector<std::string> nowhere;
    for (const Job& jb : jobs) {
        auto it = where.find(jb.id);
        if (it == where.end()) { nowhere.push_back(jb.id + "｜" + jb.zh); continue; }
        lv_obj_t* scr = lv_obj_create(nullptr);
        lv_obj_remove_style_all(scr); lv_obj_set_size(scr, W, H);
        ui::Ui u; pages[it->second].build(scr, u);
        lv_obj_update_layout(scr);
        // 占位符换成真会填进去的东西：量「{0} ℃」没意义，要量「180.0 ℃」
        std::string s = ui::tr(jb.zh);
        // 第三列可以用「｜」分开给每个占位符各填一个（{0} 是温度、{2} 是设备名这种）；
        // 只给一个就都填它，不给就填 180.0（温度里最长的那种）
        std::vector<std::string> fills;
        for (size_t a = 0, b; a <= jb.fill.size(); a = b + 3) {
            b = jb.fill.find("｜", a);
            fills.push_back(jb.fill.substr(a, b == std::string::npos ? std::string::npos : b - a));
            if (b == std::string::npos) break;
        }
        if (fills.empty() || fills[0].empty()) fills = {"180.0"};
        for (int d = 0; d < 9; d++) {
            const std::string mark = "{" + std::to_string(d) + "}";
            const std::string fill = ui::tr(fills[static_cast<size_t>(d) < fills.size() ? d : fills.size() - 1]);
            for (size_t q = s.find(mark); q != std::string::npos; q = s.find(mark, q + fill.size()))
                s.replace(q, mark.size(), fill);
        }
        u.set_text_raw(jb.id, s);
        lv_obj_update_layout(scr);
        lv_obj_t* lb = u.label(jb.id);
        // 报的时候带上「表里第几行」：换成中文再跑一遍，两边一对比就知道哪几处是英文才有的
        const std::string tag = jb.id + ":" + std::to_string(jb.row);
        bad += one(pages[it->second].name, scr, slack, lb, tag.c_str());
        done++;
        lv_obj_delete(scr);
    }
    if (!nowhere.empty()) {
        // 抽出来的位置名在哪一页上都找不到：多半是抽错了（拼出来的 id、或者那段代码已经删了）
        std::printf("── 没找到这些位置，没量（%d 条）\n", static_cast<int>(nowhere.size()));
        for (const std::string& t : nowhere) std::printf("     %s\n", t.c_str());
    }
    std::printf("── 量了 %d 句，报了 %d 处\n", done, bad);
    return bad;
}

}  // namespace lint
