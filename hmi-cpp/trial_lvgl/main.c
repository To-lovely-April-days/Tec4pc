/* LVGL 试做：用 LVGL 把设计稿的「设备控制」页画出来，不开窗口，直接把画面存成图。
 * 页面内容在 build/<页面>_<缩放>.inc 里，是 tools/gen.py 从设计稿导出的；这里只有外壳和几个画东西的小函数。 */
#include "lvgl.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#ifndef SCALE
#define SCALE 1
#endif
#define W (1280 * SCALE)
#define H (720 * SCALE)
static uint32_t fb[W * H];
static void flush(lv_display_t *d, const lv_area_t *a, uint8_t *px) {
    int w = a->x2 - a->x1 + 1;
    for (int y = a->y1; y <= a->y2; y++) memcpy(&fb[y * W + a->x1], px + (size_t)(y - a->y1) * w * 4, (size_t)w * 4);
    lv_display_flush_ready(d);
}
/* 字体：数字和英文用 SF，中文落到苹方；同一字重同一字号只建一次 */
static struct { int w, px; lv_font_t *f; } fc[64]; static int nfc;
static lv_font_t *font(int weight, float css_px) {
    int px = (int)(css_px * SCALE + 0.5f), w = weight >= 650 ? 700 : weight >= 550 ? 600 : weight >= 450 ? 500 : 400;
    for (int i = 0; i < nfc; i++) if (fc[i].w == w && fc[i].px == px) return fc[i].f;
    char p[96]; snprintf(p, sizeof p, "A:assets/fonts/sf_%d%c.ttf", w, css_px < 20 ? 's' : 'l');
    lv_font_t *f = lv_tiny_ttf_create_file(p, px);
    snprintf(p, sizeof p, "A:assets/fonts/pf_%c.ttf", w >= 600 ? 'B' : w >= 500 ? 'M' : 'R');
    lv_font_t *cjk = lv_tiny_ttf_create_file(p, px);
    if (f) f->fallback = cjk; else f = cjk;
    fc[nfc].w = w; fc[nfc].px = px; fc[nfc].f = f; nfc++;
    return f;
}
/* 设计稿里文字和小圆点常常比包着它的元素大一点；LVGL 默认按父元素边界裁，这里给不裁剪的元素留出余量 */
static void ext_cb(lv_event_t *e) { lv_event_set_ext_draw_size(e, 120 * SCALE); }
static lv_obj_t *mk(lv_obj_t *p, int x, int y, int w, int h, int clip) {
    lv_obj_t *o = lv_obj_create(p); lv_obj_remove_style_all(o);
    lv_obj_set_pos(o, x, y); lv_obj_set_size(o, w, h); lv_obj_remove_flag(o, LV_OBJ_FLAG_SCROLLABLE);
    if (!clip) { lv_obj_add_flag(o, LV_OBJ_FLAG_OVERFLOW_VISIBLE); lv_obj_add_event_cb(o, ext_cb, LV_EVENT_REFR_EXT_DRAW_SIZE, NULL); lv_obj_refresh_ext_draw_size(o); }
    return o;
}
static void bg(lv_obj_t *o, uint32_t c, int a) { lv_obj_set_style_bg_color(o, lv_color_hex(c), 0); lv_obj_set_style_bg_opa(o, a, 0); }
static void bd(lv_obj_t *o, uint32_t c, int a, int w, lv_border_side_t s) {
    lv_obj_set_style_border_color(o, lv_color_hex(c), 0); lv_obj_set_style_border_opa(o, a, 0); lv_obj_set_style_border_width(o, w, 0); lv_obj_set_style_border_side(o, s, 0);
}
static void ol(lv_obj_t *o, uint32_t c, int a, int w) {
    lv_obj_set_style_outline_color(o, lv_color_hex(c), 0); lv_obj_set_style_outline_opa(o, a, 0); lv_obj_set_style_outline_width(o, w, 0); lv_obj_set_style_outline_pad(o, 0, 0);
}
static void sh(lv_obj_t *o, uint32_t c, int a, int blur, int x, int y, int spread) {
    lv_obj_set_style_shadow_color(o, lv_color_hex(c), 0); lv_obj_set_style_shadow_opa(o, a, 0); lv_obj_set_style_shadow_width(o, blur * 2, 0);
    lv_obj_set_style_shadow_offset_x(o, x, 0); lv_obj_set_style_shadow_offset_y(o, y, 0); lv_obj_set_style_shadow_spread(o, spread, 0);
}
static void txt(lv_obj_t *p, int cx, int cy, const char *s, int weight, float px, uint32_t c, int a, int ls, int wrap_w) {
    lv_obj_t *l = lv_label_create(p); lv_obj_remove_style_all(l);
    lv_obj_set_style_text_font(l, font(weight, px), 0); lv_obj_set_style_text_color(l, lv_color_hex(c), 0); lv_obj_set_style_text_opa(l, a, 0);
    lv_obj_set_style_text_letter_space(l, ls, 0);
    if (wrap_w) { lv_obj_set_width(l, wrap_w); lv_label_set_long_mode(l, LV_LABEL_LONG_WRAP); }
    lv_label_set_text(l, s); lv_obj_update_layout(l);
    lv_obj_set_pos(l, cx - lv_obj_get_width(l) / 2, cy - lv_obj_get_height(l) / 2);
    if (getenv("DBG")) printf("%s | label %dx%d parent %dx%d\n", s, (int)lv_obj_get_width(l), (int)lv_obj_get_height(l), (int)lv_obj_get_width(p), (int)lv_obj_get_height(p));
}
static void im(lv_obj_t *p, int x, int y, const char *path) { lv_obj_t *i = lv_image_create(p); lv_image_set_src(i, path); lv_obj_set_pos(i, x, y); }
int main(int argc, char **argv) {
    lv_init();
    lv_display_t *d = lv_display_create(W, H);
    static uint32_t buf[W * H];
    lv_display_set_buffers(d, buf, NULL, sizeof buf, LV_DISPLAY_RENDER_MODE_PARTIAL);
    lv_display_set_flush_cb(d, flush);
    lv_obj_t *scr = lv_screen_active(); lv_obj_remove_style_all(scr); lv_obj_remove_flag(scr, LV_OBJ_FLAG_SCROLLABLE);
    static lv_obj_t *o[2048];
#include PAGE_INC
    clock_t c0 = clock();
    lv_tick_inc(20); lv_timer_handler();                       /* 第一帧：整屏重画，含读字体、解图片 */
    double first_ms = (clock() - c0) * 1000.0 / CLOCKS_PER_SEC;
    c0 = clock(); lv_obj_invalidate(scr); lv_tick_inc(20); lv_timer_handler();   /* 第二帧：再整屏重画一次，字形已在缓存里 */
    double again_ms = (clock() - c0) * 1000.0 / CLOCKS_PER_SEC;
    FILE *f = fopen(argc > 1 ? argv[1] : "build/out.bgra", "wb"); fwrite(fb, 4, (size_t)W * H, f); fclose(f);
    lv_mem_monitor_t m; lv_mem_monitor(&m);
    printf("ok %dx%d 字体 %d 套 · 第一帧 %.0f ms · 整屏重画 %.0f ms\n", W, H, nfc, first_ms, again_ms);
    return 0;
}
