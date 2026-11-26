#include "ui_kit.hpp"
#include <cstring>
#include "../../core/util/utf8_file.hpp"
#include <cstdio>
#include <map>
#include <string>
#include <vector>

namespace ui {

// 字体：数字和英文用 Inter，中文落到思源黑体（Noto Sans SC）；同一字重同一字号只建一次。两套都是 OFL 开源许可，可以随产品交付
// 资源目录，末尾带斜杠；空的就是当前目录
static std::string g_assets;

// 图片走 LVGL 的文件系统，盘符 A。自带的那个 stdio 驱动把目录写死在编译期，
// 这里自己挂一个：盘符 A 下的相对路径都从 g_assets 底下开
static void* fs_open(lv_fs_drv_t*, const char* path, lv_fs_mode_t mode) {
    if (mode != LV_FS_MODE_RD) return nullptr;      // 界面只读图，不写
    return tec::open_file_u8(g_assets + path, "rb");
}
static lv_fs_res_t fs_close(lv_fs_drv_t*, void* f) { std::fclose(static_cast<std::FILE*>(f)); return LV_FS_RES_OK; }
static lv_fs_res_t fs_read(lv_fs_drv_t*, void* f, void* buf, uint32_t want, uint32_t* got) {
    *got = static_cast<uint32_t>(std::fread(buf, 1, want, static_cast<std::FILE*>(f)));
    return LV_FS_RES_OK;
}
static lv_fs_res_t fs_seek(lv_fs_drv_t*, void* f, uint32_t pos, lv_fs_whence_t whence) {
    const int w = whence == LV_FS_SEEK_SET ? SEEK_SET : whence == LV_FS_SEEK_CUR ? SEEK_CUR : SEEK_END;
    return std::fseek(static_cast<std::FILE*>(f), static_cast<long>(pos), w) == 0 ? LV_FS_RES_OK : LV_FS_RES_UNKNOWN;
}
static lv_fs_res_t fs_tell(lv_fs_drv_t*, void* f, uint32_t* pos) {
    *pos = static_cast<uint32_t>(std::ftell(static_cast<std::FILE*>(f)));
    return LV_FS_RES_OK;
}

void set_assets_dir(const std::string& dir) {
    g_assets = dir;
    if (!g_assets.empty() && g_assets.back() != '/' && g_assets.back() != '\\') g_assets += '/';
}

bool assets_ok(void) {
    if (std::FILE* f = tec::open_file_u8(g_assets + "assets/fonts/inter_400.ttf", "rb")) { std::fclose(f); return true; }
    return false;
}

void register_fs(void) {
    static lv_fs_drv_t drv;                      // LVGL 只记地址，这块不能是临时的
    static bool done = false;
    if (done) return;
    done = true;
    lv_fs_drv_init(&drv);
    drv.letter = 'A';
    drv.cache_size = 64 * 1024;                  // 一次多读一点：解一张 PNG 要来回读很多小段
    drv.open_cb = fs_open; drv.close_cb = fs_close; drv.read_cb = fs_read; drv.seek_cb = fs_seek; drv.tell_cb = fs_tell;
    lv_fs_drv_register(&drv);
}

static const std::vector<unsigned char>& font_file(const std::string& path) {
    static std::map<std::string, std::vector<unsigned char>> files;      // 读进来就一直留着：字体引擎用的是这块内存本身
    auto it = files.find(path);
    if (it != files.end()) return it->second;
    std::vector<unsigned char>& v = files[path];
    if (std::FILE* f = tec::open_file_u8(g_assets + path, "rb")) {
        std::fseek(f, 0, SEEK_END); const long n = std::ftell(f); std::fseek(f, 0, SEEK_SET);
        if (n > 0) { v.resize(static_cast<size_t>(n)); if (std::fread(v.data(), 1, v.size(), f) != v.size()) v.clear(); }
        std::fclose(f);
    }
    return v;
}

// ── 界面语言 ──
static std::string g_lang = "zh";
static std::map<std::string, std::string> g_tr;      // 中文 → 当前语言；中文本身是空表

// 表里一行一条，用制表符分开：中文、en、ja、ko、de、fr、es、ru。
// 为什么用制表符不用逗号：界面上的句子里逗号多得很，用逗号分列一改就错；制表符一个都没有。
// 开头的 BOM 是给 Excel 认 UTF-8 用的，读的时候跳过
static void load_table(const std::string& code) {
    g_tr.clear();
    if (code == "zh") return;
    std::FILE* f = tec::open_file_u8(g_assets + "assets/lang/strings.tsv", "rb");
    if (!f) return;
    std::string all;
    char buf[8192];
    size_t n;
    while ((n = std::fread(buf, 1, sizeof buf, f)) > 0) all.append(buf, n);
    std::fclose(f);
    size_t p = 0;
    if (all.size() >= 3 && static_cast<unsigned char>(all[0]) == 0xEF) p = 3;
    int col = -1, en_col = -1, row = 0;
    while (p <= all.size()) {
        size_t e = all.find('\n', p);
        if (e == std::string::npos) e = all.size();
        std::string line = all.substr(p, e - p);
        p = e + 1;
        if (!line.empty() && line.back() == '\r') line.pop_back();
        if (line.empty()) { if (e >= all.size()) break; continue; }
        std::vector<std::string> cell;
        size_t q = 0;
        for (;;) { const size_t t = line.find('\t', q); cell.push_back(line.substr(q, t == std::string::npos ? t : t - q)); if (t == std::string::npos) break; q = t + 1; }
        if (row++ == 0) {                        // 表头：找出这种语言是第几列，顺带记下英文那一列（回落用）
            for (size_t i = 1; i < cell.size(); i++) { if (cell[i] == code) col = static_cast<int>(i); if (cell[i] == "en") en_col = static_cast<int>(i); }
            if (col < 0) return;                 // 表里没有这种语言：整张表不要，全显示中文
            continue;
        }
        if (cell.empty() || cell[0].empty()) continue;
        std::string v = col < static_cast<int>(cell.size()) ? cell[col] : std::string();
        if (v.empty() && en_col > 0 && en_col < static_cast<int>(cell.size())) v = cell[en_col];      // 这一句还没译：先用英文
        if (!v.empty() && v != cell[0]) g_tr[cell[0]] = v;
        if (e >= all.size()) break;
    }
}

// 整张表读进来，每种语言各数一遍：一共多少条、填了多少条。只数一次
static const std::map<std::string, int>& percents() {
    static std::map<std::string, int> m;
    static bool done = false;
    if (done) return m;
    done = true;
    std::FILE* f = tec::open_file_u8(g_assets + "assets/lang/strings.tsv", "rb");
    if (!f) return m;
    std::string all;
    char buf[8192];
    size_t n;
    while ((n = std::fread(buf, 1, sizeof buf, f)) > 0) all.append(buf, n);
    std::fclose(f);
    size_t p = all.size() >= 3 && static_cast<unsigned char>(all[0]) == 0xEF ? 3 : 0;
    std::vector<std::string> head;
    std::vector<int> done_n;
    int rows = 0;
    while (p <= all.size()) {
        size_t e = all.find('\n', p);
        if (e == std::string::npos) e = all.size();
        std::string line = all.substr(p, e - p);
        const bool last = e >= all.size();
        p = e + 1;
        if (!line.empty() && line.back() == '\r') line.pop_back();
        if (!line.empty() && line[0] != '#') {
            std::vector<std::string> cell;
            size_t q = 0;
            for (;;) { const size_t t = line.find('\t', q); cell.push_back(line.substr(q, t == std::string::npos ? t : t - q)); if (t == std::string::npos) break; q = t + 1; }
            if (head.empty()) { head.assign(cell.begin() + (cell.empty() ? 0 : 1), cell.end()); done_n.assign(head.size(), 0); }
            else if (!cell.empty() && !cell[0].empty()) {
                rows++;
                for (size_t i = 0; i < head.size(); i++) if (i + 1 < cell.size() && !cell[i + 1].empty()) done_n[i]++;
            }
        }
        if (last) break;
    }
    m["zh"] = 100;
    for (size_t i = 0; i < head.size(); i++) m[head[i]] = rows > 0 ? static_cast<int>(done_n[i] * 100.0 / rows + 0.5) : 0;
    return m;
}

int language_percent(const std::string& code) {
    const std::map<std::string, int>& m = percents();
    auto it = m.find(code);
    return it == m.end() ? 0 : it->second;
}

void set_language(const std::string& code) { g_lang = code.empty() ? "zh" : code; load_table(g_lang); }
const std::string& language() { return g_lang; }

const std::string& tr(const std::string& zh) {
    auto it = g_tr.find(zh);
    return it == g_tr.end() ? zh : it->second;
}

std::string fmt(const std::string& zh, std::initializer_list<std::string> args) {
    std::string out = tr(zh);
    int n = 0;
    for (const std::string& a : args) {
        const std::string mark = "{" + std::to_string(n++) + "}";
        for (size_t p = out.find(mark); p != std::string::npos; p = out.find(mark, p + a.size()))
            out.replace(p, mark.size(), a);
    }
    return out;
}

bool language_has_font(const std::string& code) {
    // 拉丁字母和西里尔字母 Inter 本来就带（德法西葡意俄都够），汉字在思源黑体里；
    // 假名和谚文这两套都没有，要另外放字体文件进来
    const char* need = code == "ja" ? "assets/fonts/noto_jp_400.ttf" : code == "ko" ? "assets/fonts/noto_kr_400.ttf" : nullptr;
    return !need || !font_file(need).empty();
}

lv_font_t* font(int weight, float css_px) {
    static struct { int w, px; lv_font_t* f; } cache[96];
    static int n = 0;
    const int px = static_cast<int>(css_px * UI_SCALE + 0.5f), w = weight >= 650 ? 700 : weight >= 550 ? 600 : weight >= 450 ? 500 : 400;
    for (int i = 0; i < n; i++) if (cache[i].w == w && cache[i].px == px) return cache[i].f;
    // 字体文件整个读进内存再交给字体引擎。让它直接读文件的话，每取一个字都要去硬盘上翻一遍 10 MB 的字体，
    // 第一屏要画半分钟；读进内存后同一字重只读一次（四个字重合计约 44 MB 内存）
    // 字距微调（kerning）关掉：这个字体引擎每量一个字、每画一个字都要去字体的字距表里查一遍相邻两个字，查得极慢——
    // 开着的时候搭一页要一秒上下（用户的 Windows 电脑上更久，2026-11-24 说「每个菜单第一次进去超级慢、很卡」），关掉以后是几十毫秒。
    // 代价只是个别英文字母对（比如 AV、To）之间不再收紧一点点，界面上大多是汉字和数字，看不出来
    char p[96];
    std::snprintf(p, sizeof p, "assets/fonts/inter_%d.ttf", w);
    const std::vector<unsigned char>& a = font_file(p);
    lv_font_t* f = a.empty() ? nullptr : lv_tiny_ttf_create_data_ex(a.data(), a.size(), px, LV_FONT_KERNING_NONE, LV_TINY_TTF_CACHE_GLYPH_CNT);
    std::snprintf(p, sizeof p, "assets/fonts/noto_sc_%d.ttf", w);
    const std::vector<unsigned char>& b = font_file(p);
    lv_font_t* cjk = b.empty() ? nullptr : lv_tiny_ttf_create_data_ex(b.data(), b.size(), px, LV_FONT_KERNING_NONE, LV_TINY_TTF_CACHE_GLYPH_CNT);
    if (f) f->fallback = cjk; else f = cjk;
    // 日文假名、韩文谚文：字体文件放进来了就接在链子后面，没放就当没有这两种语言（选择框里会标「缺字体」）
    lv_font_t* tail = cjk ? cjk : f;
    for (const char* name : {"noto_jp_%d.ttf", "noto_kr_%d.ttf"}) {
        std::snprintf(p, sizeof p, (std::string("assets/fonts/") + name).c_str(), w);
        const std::vector<unsigned char>& x = font_file(p);
        if (x.empty()) continue;
        lv_font_t* g2 = lv_tiny_ttf_create_data_ex(x.data(), x.size(), px, LV_FONT_KERNING_NONE, LV_TINY_TTF_CACHE_GLYPH_CNT);
        if (!g2) continue;
        if (tail) tail->fallback = g2; else f = g2;
        tail = g2;
    }
    if (n < 96) { cache[n].w = w; cache[n].px = px; cache[n].f = f; n++; }
    return f;
}

// 设计稿里文字和小圆点常常比包着它的元素大一点；LVGL 默认按父元素边界裁，这里给不裁剪的元素留出余量
static void ext_cb(lv_event_t* e) { lv_event_set_ext_draw_size(e, 120 * UI_SCALE); }

// 一行里前面那个按钮长了以后，这一行里排在它后面的按钮也要往右让同样多。
// 为什么要留着记号而不是当场推：生成的页面代码是从左往右一个个搭出来的，
// 前面那个长的时候，后面那些还没搭出来，推不动。所以记在这一行上，后面搭到了自己让。
// 只让按钮（在 reg_act 那一下让，那时它才算按钮）：同一行里的说明小字不跟着让——
// 那种字本来就长，往右挪一截就顶出卡片了（2026-11-27 英文「反应釜与安全」那页上试出来的）
struct RowPush { int from_x = 0, dx = 0; };
static std::map<lv_obj_t*, RowPush> g_row_push;

lv_obj_t* mk(lv_obj_t* p, int x, int y, int w, int h, int clip) {
    lv_obj_t* o = lv_obj_create(p);
    lv_obj_remove_style_all(o);
    lv_obj_set_pos(o, x, y); lv_obj_set_size(o, w, h);
    lv_obj_remove_flag(o, static_cast<lv_obj_flag_t>(LV_OBJ_FLAG_SCROLLABLE | LV_OBJ_FLAG_CLICKABLE));   // 缺省不接点击：点到它身上的要落到后面真正能点的元素上
    if (!clip) { lv_obj_add_flag(o, LV_OBJ_FLAG_OVERFLOW_VISIBLE); lv_obj_add_event_cb(o, ext_cb, LV_EVENT_REFR_EXT_DRAW_SIZE, nullptr); lv_obj_refresh_ext_draw_size(o); }
    return o;
}
void bg(lv_obj_t* o, uint32_t c, int a) { lv_obj_set_style_bg_color(o, lv_color_hex(c), 0); lv_obj_set_style_bg_opa(o, static_cast<lv_opa_t>(a), 0); }
void bd(lv_obj_t* o, uint32_t c, int a, int w, lv_border_side_t s) {
    lv_obj_set_style_border_color(o, lv_color_hex(c), 0); lv_obj_set_style_border_opa(o, static_cast<lv_opa_t>(a), 0); lv_obj_set_style_border_width(o, w, 0); lv_obj_set_style_border_side(o, s, 0);
}
void ol(lv_obj_t* o, uint32_t c, int a, int w) {
    lv_obj_set_style_outline_color(o, lv_color_hex(c), 0); lv_obj_set_style_outline_opa(o, static_cast<lv_opa_t>(a), 0); lv_obj_set_style_outline_width(o, w, 0); lv_obj_set_style_outline_pad(o, 0, 0);
}
void sh(lv_obj_t* o, uint32_t c, int a, int blur, int x, int y, int spread) {
    lv_obj_set_style_shadow_color(o, lv_color_hex(c), 0); lv_obj_set_style_shadow_opa(o, static_cast<lv_opa_t>(a), 0); lv_obj_set_style_shadow_width(o, blur * 2, 0);
    lv_obj_set_style_shadow_offset_x(o, x, 0); lv_obj_set_style_shadow_offset_y(o, y, 0); lv_obj_set_style_shadow_spread(o, spread, 0);
}
// 一段文字画出来多宽多高。直接按字体量，不去调 lv_obj_update_layout——那个函数每调一次都把整页所有元素重新排一遍，
// 一页几百段文字就是几百遍，搭一页要一秒上下（慢机器上好几秒）。用户 2026-11-24 上机说的「每个菜单第一次进去超级慢」就慢在这里
static void text_size(lv_obj_t* l, const char* s, int wrap_w, int& w, int& h) {
    lv_point_t sz;
    lv_text_get_size(&sz, s, lv_obj_get_style_text_font(l, 0), lv_obj_get_style_text_letter_space(l, 0), lv_obj_get_style_text_line_space(l, 0),
                     wrap_w ? wrap_w : LV_COORD_MAX, LV_TEXT_FLAG_NONE);
    w = wrap_w ? wrap_w : sz.x; h = sz.y;
}

// 长度不可控的那种话占几行。就一行：这些位置在设计稿里都是一行，底下紧挨着别的东西
//（结晶卡那句下面就是分段进度条），折成两行等于把下面那行盖了——躲开右边又压住下边，没意义。
// 所以装不下的尾巴点成「…」，句子本身要短到放得下——最长的那句「温控器或继电器没连上…」
// 就是照最窄的那个位置（结晶卡的 536 px）剪过的
static const int kFitLines = 1;

// 「这段字本来就写在图上」：拿对象自带的那个闲置指针当记号（这套界面别处没用它）
static int on_art_tag;
void mark_on_art(lv_obj_t* label) { if (label) lv_obj_set_user_data(label, &on_art_tag); }
bool is_on_art(lv_obj_t* label) { return label && lv_obj_get_user_data(label) == &on_art_tag; }

std::vector<lv_point_precise_t> stair_line(const std::vector<lv_point_precise_t>& in) {
    if (in.size() < 2) return in;
    std::vector<lv_point_precise_t> out;
    out.reserve(in.size() * 3);
    auto emit = [&](int32_t x, int32_t y) {
        if (!out.empty() && static_cast<int32_t>(out.back().x) == x && static_cast<int32_t>(out.back().y) == y) return;
        out.push_back({static_cast<lv_value_precise_t>(x), static_cast<lv_value_precise_t>(y)});
    };
    emit(static_cast<int32_t>(in[0].x), static_cast<int32_t>(in[0].y));
    for (size_t i = 1; i < in.size(); i++) {
        const int32_t x0 = static_cast<int32_t>(in[i - 1].x), y0 = static_cast<int32_t>(in[i - 1].y);
        const int32_t x1 = static_cast<int32_t>(in[i].x), y1 = static_cast<int32_t>(in[i].y);
        const int32_t dx = x1 - x0, dy = y1 - y0;
        // 本来就是横的或竖的：原样留着（LVGL 画这两种没问题）
        if (dx == 0 || dy == 0 || out.size() > 60000) { emit(x1, y1); continue; }
        if (dx > 0 ? dx >= (dy > 0 ? dy : -dy) : -dx >= (dy > 0 ? dy : -dy)) {
            const int32_t step = dx > 0 ? 1 : -1;
            int32_t prev = y0;
            for (int32_t x = x0 + step; x != x1 + step; x += step) {
                const int32_t y = y0 + static_cast<int32_t>(static_cast<double>(x - x0) * dy / dx + (dy > 0 ? 0.5 : -0.5));
                if (y != prev) { emit(x, prev); emit(x, y); prev = y; }      // 竖着跳一格，再接着横着走
            }
        } else {
            const int32_t step = dy > 0 ? 1 : -1;
            int32_t prev = x0;
            for (int32_t y = y0 + step; y != y1 + step; y += step) {
                const int32_t x = x0 + static_cast<int32_t>(static_cast<double>(y - y0) * dx / dy + (dx > 0 ? 0.5 : -0.5));
                if (x != prev) { emit(prev, y); emit(x, y); prev = x; }
            }
        }
        emit(x1, y1);
    }
    return out;
}

int label_w(lv_obj_t* label) { int w = 0, h = 0; text_size(label, lv_label_get_text(label), 0, w, h); return w; }

lv_obj_t* txt(lv_obj_t* p, int cx, int cy, const char* s, int weight, float px, uint32_t c, int a, int ls, int wrap_w) {
    return txta(p, cx, cy, 'c', s, weight, px, c, a, ls, wrap_w);
}

// 按钮跟着字长：外文比中文长，可按钮的宽度是从设计稿里量死的，字就直接伸出按钮去
// （2026-11-27 英文界面上结晶编辑器的「新增一段」= Add a segment 伸到了屏幕外；
//  序列页的「换类型」「删除这一步」、语言选择里的「完整」也都一样）。
//
// 只给按钮做（reg_act 把 LV_OBJ_FLAG_CLICKABLE 加上了，生成的页面代码里它排在画字之前），
// 而且只给「按钮刚好包住这句话」的那种：字的另一头离按钮边还空着一大片的，多半是表头、
// 卡片这类有底色的框，不该跟着字长。
// 往哪边长：字往哪边长，按钮就往哪边长，另一头留的空当和设计稿里一样。
// 靠左写的字（anchor l）往右长；按钮本来贴着那一行右端的，就改成整个往左挪，右边那条边不动。
// 靠右写的字（anchor r）往左长，按钮也往左长——盒子左移了，字在盒子里的位置要跟着挪回设计稿里那个右边缘。
// 这个元素现在多宽（还没排过版，读的是设好的那个数；字是跟着内容走的，直接量）
static int obj_w(lv_obj_t* o) {
    const int32_t w = lv_obj_get_style_width(o, 0);
    if (w > 0 && w < 4000) return w;
    return lv_obj_check_type(o, &lv_label_class) ? label_w(o) : 0;
}

// 按钮长出来以后，紧挨着它的那几个也要跟着让——设计稿里它们本来是一行挨着排的（flex），
// 翻成绝对坐标以后就不会自己让了。只推「贴着它的」那一串（中间空当不超过 24 px），
// 隔着老远的（比如同一行最左边那句提示）不动
static void push_row(lv_obj_t* box, int extra, bool leftward) {
    lv_obj_t* row = lv_obj_get_parent(box);
    if (!row) return;
    const int gap = 24 * UI_SCALE;
    for (int again = 1; again;) {
        again = 0;
        const int bx = lv_obj_get_style_x(box, 0), bw = obj_w(box);
        for (uint32_t i = 0; i < lv_obj_get_child_count(row); i++) {
            lv_obj_t* c = lv_obj_get_child(row, i);
            if (c == box) continue;
            const int cx = lv_obj_get_style_x(c, 0), cw = obj_w(c);
            if (leftward ? (cx + cw <= bx && bx - (cx + cw) <= gap) : (cx >= bx + bw && cx - (bx + bw) <= gap)) {
                lv_obj_set_x(c, cx + (leftward ? -extra : extra));
                box = c; again = 1; break;        // 再往外推一个（三个按钮挨着排的那种）
            }
        }
    }
}

static void hug_button(lv_obj_t* p, lv_obj_t* l, char anchor, int ax, int design_w, int now_w) {
    if (!p || now_w <= design_w) return;
    if (!lv_obj_has_flag(p, LV_OBJ_FLAG_CLICKABLE)) return;
    const int32_t pw = lv_obj_get_style_width(p, 0), pxx = lv_obj_get_style_x(p, 0);
    if (pw <= 0 || pw > 4000) return;                       // 宽度不是量好的数（LV_SIZE_CONTENT 之类）：不碰
    const int pad = 24 * UI_SCALE;                          // 设计稿里字离按钮边最多这么远，再远就不是「包住字」了
    const int extra = now_w - design_w;
    if (anchor == 'l') {
        if (pw - (ax + design_w) > pad || ax > pad * 3) return;
        // 这个按钮在它那一行里是不是贴着右端的
        lv_obj_t* row = lv_obj_get_parent(p);
        const int32_t rw = row ? lv_obj_get_style_width(row, 0) : 0;
        const bool right_end = rw > 0 && rw < 4000 && rw - (pxx + pw) <= pad && rw - (pxx + pw) < pxx;
        push_row(p, extra, right_end);                  // 先推旁边的（按原来的位置算谁贴着谁），再动自己
        if (right_end) lv_obj_set_x(p, pxx - extra);
        lv_obj_set_width(p, pw + extra);
        if (!right_end && row) {                        // 往右长的：后面还没搭出来的那些，搭到的时候自己让
            RowPush& rp = g_row_push[row];
            if (rp.dx == 0) {
                rp.from_x = pxx + pw;
                lv_obj_add_event_cb(row, [](lv_event_t* e) { g_row_push.erase(static_cast<lv_obj_t*>(lv_event_get_target(e))); }, LV_EVENT_DELETE, nullptr);
            }
            rp.dx += extra;
        }
    } else if (anchor == 'r') {
        if (ax - design_w > pad || pw - ax > pad) return;
        push_row(p, extra, true);
        lv_obj_set_pos(p, pxx - extra, lv_obj_get_style_y(p, 0));
        lv_obj_set_width(p, pw + extra);
        lv_obj_set_x(l, ax - design_w);
    }
}

lv_obj_t* txta(lv_obj_t* p, int ax, int cy, char anchor, const char* s0, int weight, float px, uint32_t c, int a, int ls, int wrap_w) {
    // 设计稿翻出来的页面代码里，每一句都是从这儿画的——语言就在这儿换，
    // 所以 ui/gen/pages_*.inc 那两万行一个字都不用动。
    // key 这个局部变量不能省：tr() 查不到时返回的是「传进去的那个串」的引用，
    // 直接写 tr(s0) 的话那是个用完就没的临时对象，拿到的引用是悬的（第一次自查时屏上冒出一串乱码就是这么来的）
    const std::string key = s0;
    const std::string& S = tr(key);
    const char* s = S.c_str();
    lv_obj_t* l = lv_label_create(p);
    lv_obj_remove_style_all(l);
    lv_obj_set_style_text_font(l, font(weight, px), 0); lv_obj_set_style_text_color(l, lv_color_hex(c), 0); lv_obj_set_style_text_opa(l, static_cast<lv_opa_t>(a), 0);
    lv_obj_set_style_text_letter_space(l, ls, 0);
    if (wrap_w) { lv_obj_set_width(l, wrap_w); lv_label_set_long_mode(l, LV_LABEL_LONG_WRAP); }
    lv_label_set_text(l, s);
    int w = 0, h = 0;
    text_size(l, s, wrap_w, w, h);
    lv_obj_set_pos(l, anchor == 'l' ? ax : anchor == 'r' ? ax - w : ax - w / 2, cy - h / 2);
    if (&S != &key) {                       // 换过语言的才量第二遍（中文时 tr() 返回的就是传进来的那个串）
        int dw = 0, dh = 0;
        text_size(l, s0, wrap_w, dw, dh);
        hug_button(p, l, anchor, ax, dw, w);
    }
    return l;
}
// 搭页面那一下，这一段已经往右让了多少。三段连着排（A→B→C）时 C 要把 A 让出来的那份也算上
static std::map<lv_obj_t*, int> g_init_shift;

lv_obj_t* txta_shift(lv_obj_t* p, int ax, int cy, char anchor, lv_obj_t* prev, int prev_w, const char* s, int weight, float px, uint32_t c, int a, int ls, int wrap_w) {
    lv_obj_t* l = txta(p, ax, cy, anchor, s, weight, px, c, a, ls, wrap_w);
    const int grew = prev ? label_w(prev) - prev_w : 0;       // 前一段比设计稿里宽出来多少
    const int extra = (grew > 0 ? grew : 0) + (prev ? g_init_shift[prev] : 0);
    g_init_shift[l] = extra;
    // 这一段被删掉时把这条也擦掉。换语言会把所有页面拆了重搭，不擦的话表里留着一堆
    // 指向已删对象的键；新对象很容易拿到同一个地址，于是莫名其妙继承了上一页某一段的让位距离
    lv_obj_add_event_cb(l, [](lv_event_t* e) { g_init_shift.erase(static_cast<lv_obj_t*>(lv_event_get_target(e))); }, LV_EVENT_DELETE, nullptr);
    // 往右让的时候要从设计稿里那个 ax 算起，不能读 lv_obj_get_x()：这会儿这一页还没排过版，
    // 读回来是 0，字就全挤到外壳左边去了（2026-11-26：英文界面上「Clock」压在自己的小圆点上）
    if (extra > 0) {
        int w = 0, h = 0;
        text_size(l, lv_label_get_text(l), wrap_w, w, h);
        lv_obj_set_x(l, (anchor == 'l' ? ax : anchor == 'r' ? ax - w : ax - w / 2) + extra);
    }
    return l;
}

lv_obj_t* im(lv_obj_t* p, int x, int y, const char* path) { lv_obj_t* i = lv_image_create(p); lv_image_set_src(i, path); lv_obj_set_pos(i, x, y); return i; }

// 「这个位置摆的是控制核心说什么就是什么的话」——命令被拒的理由、报警句、串口那边抛上来的错。
// 界面既不知道它会有多长，也管不着（核心在改，译文更长）。所以这些位置的字一律拦一道：
// 放得下照旧，放不下就在自己那一栏的边上收住，尾巴点成「…」。
// 不拦的话长句就直愣愣地画过去，把右边的东西盖了——2026-11-27 现场：结晶卡上那句
// 「温控器或继电器没连上，不能控温……」横着压过了右边的曲线图。
//
// 名单从哪来：python3 ui/tools/fit.py 把「哪一句话会摆到哪个位置上」抽一遍，
// 核心的话能流到的位置就是这些。改了界面代码要再抽一遍对一对；
// 名字写错、页面改了名的，--fit 会报「没找到这些位置」。
static const char* kFitIds[] = {
    "cv.seg",            // 结晶卡：第几段 / 出错时的那句
    "ctl.temp.status",   // 设备控制页状态行
    "cm.tip", "cm.msg",  // 通信设置
    "cs.tip",            // 自动检测结果
    "cd.msg",            // 改某一台设备的弹窗
    "pg.msg", "pg.info", // PID 整定
    "lib.info",          // 方案库
    "seq.chip", "ex.chip",  // 序列页、导出页下面那个胶囊（它的宽度是按字长现算的，字不拦住胶囊就撑出卡片）
    "xq.run",            // 退出控制核心的弹窗
    "tr.win",            // 通道趋势：平时写「窗口 … min」，临时拿来说一句话
};

const char* const* fit_ids(size_t& n) { n = sizeof kFitIds / sizeof kFitIds[0]; return kFitIds; }

void Ui::reg_text(const char* id, lv_obj_t* label, char anchor, int ax, int cy) {
    texts_[id] = T{label, anchor, ax, cy, lv_label_get_text(label)};
    for (const char* f : kFitIds) if (std::strcmp(f, id) == 0) { texts_[id].fit = true; break; }
    if (label) id_of_[label] = id;       // 反查：算「跟着前面让多少」时要顺着链子往前找
}

void Ui::reg_act(lv_obj_t* obj, const char* action) {
    // 这一行里前面那个按钮长出来了：这一个跟着往右让（见 g_row_push）
    auto push = g_row_push.find(lv_obj_get_parent(obj));
    if (push != g_row_push.end()) {
        const int x = lv_obj_get_style_x(obj, 0);
        if (x >= push->second.from_x) lv_obj_set_x(obj, x + push->second.dx);
    }
    lv_obj_add_flag(obj, LV_OBJ_FLAG_CLICKABLE);
    lv_obj_set_ext_click_area(obj, 4 * UI_SCALE);            // 手指比鼠标粗：可点的范围比画出来的大一圈
    auto* a = new std::string(action);
    acts_.push_back(a);
    struct Ctx { Ui* ui; std::string* act; };
    auto* ctx = new Ctx{this, a};
    lv_obj_add_event_cb(obj, [](lv_event_t* e) {
        auto* c = static_cast<Ctx*>(lv_event_get_user_data(e));
        if (c->ui->on_action) c->ui->on_action(*c->act);
    }, LV_EVENT_CLICKED, ctx);
    // 按下去有反应：整体暗一点。没有这个，触摸屏上分不清点没点上
    lv_obj_set_style_opa(obj, LV_OPA_70, LV_STATE_PRESSED);
}

// 运行时写上去的字也过一道词表。拼出来的句子（「通道 」+ 号 + 「 正在控温」）表里对不上，
// 原样显示中文——这些要一条条改成带占位符的整句才能翻
void Ui::set_text(const std::string& id, const std::string& s0) { set_text_raw(id, tr(s0)); }

void Ui::reg_follow(const std::string& id, lv_obj_t* prev, int prev_w) {
    auto it = texts_.find(id);
    if (it == texts_.end() || !prev) return;
    it->second.prev = prev;
    it->second.prev_w = prev_w;
    follower_of_[prev] = id;
}

// 一句长度不可控的话，最宽能摆到哪儿：到它所在那一栏的边上。
// 哪一栏？包着这段字的那层壳是跟着字长走的空壳（设计稿里它就只有这句话那么宽），不算数；
// 外面那一层才是设计稿里量好的一栏。左边留了多少，右边也给它留多少——
// 不然塞进「卡片里的一行」这种壳的时候，右边的内边距就被吃掉了
int Ui::fit_room(T& t) const {
    if (t.room) return t.room;
    lv_obj_t* par = lv_obj_get_parent(t.label);
    lv_obj_t* col = par ? lv_obj_get_parent(par) : nullptr;
    if (!col) return 0;
    const int px = lv_obj_get_x(par), cw = lv_obj_get_width(col);
    const int room = t.anchor == 'r' ? px + t.ax : cw - px - 2 * t.ax;
    // 还没排过版的时候量出来是 0：这一拍先不管它，下一拍（每半秒一次）再量
    if (room < 60 * UI_SCALE) return 0;
    t.room = room;
    return room;
}

void Ui::set_text_raw(const std::string& id, const std::string& s) {
    auto it = texts_.find(id);
    if (it == texts_.end() || it->second.cur == s) return;      // 没变就不动：不变的字不重画
    T& t = it->second;
    t.cur = s;
    int w = 0, h = 0, line_h = 0;
    if (t.fit) {
        // 长度不可控的那种位置（见上面的 kFitIds）：先按不折行量，放得下就当什么都没发生
        text_size(t.label, s.c_str(), 0, w, h);
        line_h = h;
        const int room = fit_room(t);
        if (room && w > room) {
            lv_obj_set_size(t.label, room, h * kFitLines);
            lv_label_set_long_mode(t.label, LV_LABEL_LONG_DOT);   // 装不下的尾巴由 LVGL 点成「…」
            w = room;
        } else {
            lv_obj_set_size(t.label, LV_SIZE_CONTENT, LV_SIZE_CONTENT);
            lv_label_set_long_mode(t.label, LV_LABEL_LONG_WRAP);
        }
        lv_label_set_text(t.label, s.c_str());
    } else {
        lv_label_set_text(t.label, s.c_str());
        // 设计稿里要折行的那几段是定了宽度的；其余的宽度是「跟着字走」，按不折行量
        const int32_t sw = lv_obj_get_style_width(t.label, 0);
        text_size(t.label, s.c_str(), sw == LV_SIZE_CONTENT ? 0 : sw, w, h);
        line_h = h;
    }
    const int x = (t.anchor == 'l' ? t.ax : t.anchor == 'r' ? t.ax - w : t.ax - w / 2) + follow_shift(t);
    // 第一行永远留在设计稿里那一行上（kFitLines 要是改成多行，是往下长，不往上跑）
    lv_obj_set_pos(t.label, x, t.cy - line_h / 2);
    // 新的字比设计稿里那句长很多时，会伸出包着它的那个元素太远而被裁掉；包着它的元素只是个不带底色的壳，就跟着放宽
    lv_obj_t* par = lv_obj_get_parent(t.label);
    if (t.anchor == 'l' && par && lv_obj_get_style_bg_opa(par, 0) == 0 && x + w > lv_obj_get_width(par)) lv_obj_set_width(par, x + w);
    // 靠右对齐的字变长是往左伸的。不去挪那个壳（它里面可能还有别的东西，比如前面的小圆点），
    // 只把它「允许画出去多远」放大到够用：缺省只留了 120 像素的余量，再长就被裁掉开头
    if (t.anchor == 'r' && par && x < -100 * UI_SCALE) {
        lv_obj_add_event_cb(par, [](lv_event_t* e) { lv_event_set_ext_draw_size(e, 700 * UI_SCALE); }, LV_EVENT_REFR_EXT_DRAW_SIZE, nullptr);
        lv_obj_refresh_ext_draw_size(par);
    }
    // 自己摆好了，再通知后面跟着的那一段跟着挪（顺序反过来的话它按旧宽度算，差一拍）
    auto f = follower_of_.find(t.label);
    if (f != follower_of_.end()) place(f->second);
}

// 跟在别人后面的那一段，该往右让多少
int Ui::follow_shift(const T& t) const {
    if (!t.prev) return 0;
    const int grew = label_w(t.prev) - t.prev_w;
    int shift = grew > 0 ? grew : 0;
    // 前面那一段自己也可能是跟着别人让过的（A→B→C）：把它让的那份也加上
    auto pit = id_of_.find(t.prev);
    if (pit != id_of_.end()) {
        auto tit = texts_.find(pit->second);
        if (tit != texts_.end()) shift += follow_shift(tit->second);
    } else {
        auto sit = g_init_shift.find(t.prev);
        if (sit != g_init_shift.end()) shift += sit->second;
    }
    return shift;
}

// 位置重算一遍（前面那一段的字换了，这一段要跟着挪）
void Ui::place(const std::string& id) {
    auto it = texts_.find(id);
    if (it == texts_.end()) return;
    T& t = it->second;
    int w = 0, h = 0;
    const int32_t sw = lv_obj_get_style_width(t.label, 0);
    text_size(t.label, lv_label_get_text(t.label), sw == LV_SIZE_CONTENT ? 0 : sw, w, h);
    const int x = (t.anchor == 'l' ? t.ax : t.anchor == 'r' ? t.ax - w : t.ax - w / 2) + follow_shift(t);
    lv_obj_set_pos(t.label, x, t.cy - h / 2);
    auto f = follower_of_.find(t.label);
    if (f != follower_of_.end()) place(f->second);
}

void Ui::set_text_color(const std::string& id, uint32_t c) {
    auto it = texts_.find(id);
    if (it != texts_.end() && changed("tc:" + id, c)) lv_obj_set_style_text_color(it->second.label, lv_color_hex(c), 0);
}

void Ui::show(const std::string& id, bool on) {
    lv_obj_t* o = box(id);
    if (!o || !changed("vis:" + id, on)) return;
    if (on) lv_obj_remove_flag(o, LV_OBJ_FLAG_HIDDEN); else lv_obj_add_flag(o, LV_OBJ_FLAG_HIDDEN);
}

}  // namespace ui
