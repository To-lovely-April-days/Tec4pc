#include "ui_kit.hpp"
#include <cstdio>
#include <map>
#include <string>
#include <vector>

namespace ui {

// 字体：数字和英文用 Inter，中文落到思源黑体（Noto Sans SC）；同一字重同一字号只建一次。两套都是 OFL 开源许可，可以随产品交付
static const std::vector<unsigned char>& font_file(const std::string& path) {
    static std::map<std::string, std::vector<unsigned char>> files;      // 读进来就一直留着：字体引擎用的是这块内存本身
    auto it = files.find(path);
    if (it != files.end()) return it->second;
    std::vector<unsigned char>& v = files[path];
    if (std::FILE* f = std::fopen(path.c_str(), "rb")) {
        std::fseek(f, 0, SEEK_END); const long n = std::ftell(f); std::fseek(f, 0, SEEK_SET);
        if (n > 0) { v.resize(static_cast<size_t>(n)); if (std::fread(v.data(), 1, v.size(), f) != v.size()) v.clear(); }
        std::fclose(f);
    }
    return v;
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
    if (n < 96) { cache[n].w = w; cache[n].px = px; cache[n].f = f; n++; }
    return f;
}

// 设计稿里文字和小圆点常常比包着它的元素大一点；LVGL 默认按父元素边界裁，这里给不裁剪的元素留出余量
static void ext_cb(lv_event_t* e) { lv_event_set_ext_draw_size(e, 120 * UI_SCALE); }

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

int label_w(lv_obj_t* label) { int w = 0, h = 0; text_size(label, lv_label_get_text(label), 0, w, h); return w; }

lv_obj_t* txt(lv_obj_t* p, int cx, int cy, const char* s, int weight, float px, uint32_t c, int a, int ls, int wrap_w) {
    lv_obj_t* l = lv_label_create(p);
    lv_obj_remove_style_all(l);
    lv_obj_set_style_text_font(l, font(weight, px), 0); lv_obj_set_style_text_color(l, lv_color_hex(c), 0); lv_obj_set_style_text_opa(l, static_cast<lv_opa_t>(a), 0);
    lv_obj_set_style_text_letter_space(l, ls, 0);
    if (wrap_w) { lv_obj_set_width(l, wrap_w); lv_label_set_long_mode(l, LV_LABEL_LONG_WRAP); }
    lv_label_set_text(l, s);
    int w = 0, h = 0;
    text_size(l, s, wrap_w, w, h);
    lv_obj_set_pos(l, cx - w / 2, cy - h / 2);
    return l;
}
lv_obj_t* im(lv_obj_t* p, int x, int y, const char* path) { lv_obj_t* i = lv_image_create(p); lv_image_set_src(i, path); lv_obj_set_pos(i, x, y); return i; }

void Ui::reg_text(const char* id, lv_obj_t* label, char anchor, int ax, int cy) { texts_[id] = T{label, anchor, ax, cy, lv_label_get_text(label)}; }

void Ui::reg_act(lv_obj_t* obj, const char* action) {
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

void Ui::set_text(const std::string& id, const std::string& s) {
    auto it = texts_.find(id);
    if (it == texts_.end() || it->second.cur == s) return;      // 没变就不动：不变的字不重画
    T& t = it->second;
    t.cur = s;
    lv_label_set_text(t.label, s.c_str());
    int w = 0, h = 0;
    // 设计稿里要折行的那几段是定了宽度的；其余的宽度是「跟着字走」，按不折行量
    const int32_t sw = lv_obj_get_style_width(t.label, 0);
    text_size(t.label, s.c_str(), sw == LV_SIZE_CONTENT ? 0 : sw, w, h);
    const int x = t.anchor == 'l' ? t.ax : t.anchor == 'r' ? t.ax - w : t.ax - w / 2;
    lv_obj_set_pos(t.label, x, t.cy - h / 2);
    // 新的字比设计稿里那句长很多时，会伸出包着它的那个元素太远而被裁掉；包着它的元素只是个不带底色的壳，就跟着放宽
    lv_obj_t* par = lv_obj_get_parent(t.label);
    if (t.anchor == 'l' && par && lv_obj_get_style_bg_opa(par, 0) == 0 && x + w > lv_obj_get_width(par)) lv_obj_set_width(par, x + w);
    // 靠右对齐的字变长是往左伸的。不去挪那个壳（它里面可能还有别的东西，比如前面的小圆点），
    // 只把它「允许画出去多远」放大到够用：缺省只留了 120 像素的余量，再长就被裁掉开头
    if (t.anchor == 'r' && par && x < -100 * UI_SCALE) {
        lv_obj_add_event_cb(par, [](lv_event_t* e) { lv_event_set_ext_draw_size(e, 700 * UI_SCALE); }, LV_EVENT_REFR_EXT_DRAW_SIZE, nullptr);
        lv_obj_refresh_ext_draw_size(par);
    }
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
