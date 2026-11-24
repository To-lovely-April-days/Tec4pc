// 界面的小工具箱：画东西的几个函数（生成的页面代码用它们），加上「按名字改文字 / 按名字找元素 / 点了通知程序」这三件事。
#pragma once
#include "lvgl.h"
#include <functional>
#include <map>
#include <string>
#include <vector>

#ifndef UI_SCALE
#define UI_SCALE 1          // 产品是 1（1280×720）；2 只用来出放大的对比图
#endif

namespace ui {

// 字体和图片去哪个目录找（里面是 assets/fonts 和 assets/img）。
// 不用「进程的当前目录」：界面嵌在 PC 工作站里跑的时候，改当前目录会连累宿主程序。
// 独立程序和嵌入都在启动时调一次；要在画任何东西之前调
void set_assets_dir(const std::string& dir);

// 把「盘符 A」这个文件驱动挂给 LVGL（图片走它）。**必须在 lv_init() 之后调**：
// lv_init 会把驱动表重新初始化一遍，之前挂的会被抹掉
void register_fs(void);

// 资源目录里到底有没有东西。没有的话界面会一个字、一张图都没有，是一片灰——
// 与其画出那么一屏，不如早点说「找不到」（踩过：装到 Windows 上少拷了 assets，看到的就是一片灰）
bool assets_ok(void);

lv_font_t* font(int weight, float css_px);
lv_obj_t* mk(lv_obj_t* p, int x, int y, int w, int h, int clip);
void bg(lv_obj_t* o, uint32_t c, int a);
void bd(lv_obj_t* o, uint32_t c, int a, int w, lv_border_side_t s);
void ol(lv_obj_t* o, uint32_t c, int a, int w);
void sh(lv_obj_t* o, uint32_t c, int a, int blur, int x, int y, int spread);
lv_obj_t* txt(lv_obj_t* p, int cx, int cy, const char* s, int weight, float px, uint32_t c, int a, int ls, int wrap_w);
lv_obj_t* im(lv_obj_t* p, int x, int y, const char* path);
// 一段文字现在多宽（按字体直接量，不用先让整页重排一遍）
int label_w(lv_obj_t* label);

// 一页上所有「有名字的东西」
class Ui {
public:
    std::function<void(const std::string& action)> on_action;
    void reg_text(const char* id, lv_obj_t* label, char anchor, int ax, int cy);
    void reg_box(const char* id, lv_obj_t* obj) { boxes_[id] = obj; }
    void reg_act(lv_obj_t* obj, const char* action);
    // 改一段文字。对齐方式照设计稿里它原来的（靠左 / 靠右 / 居中），字变长变短位置都对
    void set_text(const std::string& id, const std::string& s);
    void set_text_color(const std::string& id, uint32_t c);
    // 「这个样子上次是不是已经设过了」：每半秒刷一次状态，样式没变就不要再设——
    // 设一次样式 LVGL 就把那一块重画一遍，弹窗开着时底下每重画一块都要连阴影一起算，很慢
    bool changed(const std::string& key, long long v) { auto it = seen_.find(key); if (it != seen_.end() && it->second == v) return false; seen_[key] = v; return true; }
    void show(const std::string& id, bool on);
    lv_obj_t* label(const std::string& id) const { auto it = texts_.find(id); return it == texts_.end() ? nullptr : it->second.label; }
    lv_obj_t* box(const std::string& id) const { auto it = boxes_.find(id); return it == boxes_.end() ? nullptr : it->second; }
    bool has_text(const std::string& id) const { return texts_.count(id) != 0; }

private:
    struct T { lv_obj_t* label; char anchor; int ax, cy; std::string cur; };
    std::map<std::string, T> texts_;
    std::map<std::string, long long> seen_;
    std::map<std::string, lv_obj_t*> boxes_;
    std::vector<std::string*> acts_;
};

struct PageDef { const char* name; void (*build)(lv_obj_t* scr, Ui& ui); };

}  // namespace ui
