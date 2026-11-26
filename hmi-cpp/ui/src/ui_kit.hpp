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

// ── 界面语言 ──
// 一张表 assets/lang/strings.tsv：第一列是中文（界面里写的就是它），后面每种语言一列。
// 换语言 = 换一列，不换代码、不重编；补一种语言 = 在表里加一列。
// 查不到的条目先回落英文、再回落中文——所以译文没齐也能用，只是那几句是英文。
void set_language(const std::string& code);          // "zh" / "en" / "ja" / "ko" / "de" / "fr" / "es" / "ru"
const std::string& language();
const std::string& tr(const std::string& zh);        // 表里没有就原样返回
// 带占位符的整句。先按中文原句去词表里取这种语言的说法，再把 {0} {1} … 换成给进来的值。
//   fmt("{0} · 已运行 {1}", {ver, dur})
// 为什么非这么写不可：「通道 」+ 号 + 「 正在控温」这种拼法，半截话没法翻，
// 而且德语俄语的语序和中文不一样，得让译者拿到整句、自己决定占位符摆哪儿
std::string fmt(const std::string& zh, std::initializer_list<std::string> args);
// 这台机器上有没有这种文字要的字体。日文假名、韩文谚文现在这两套字体里都没有，
// 缺字体的语言选了也只能看见方框，所以选择框里要先问一声
bool language_has_font(const std::string& code);
// 这种语言译到几成（0–100）。选择框上标「完整 / 未齐 / 未译」靠它，说的是实话不是写死的
int language_percent(const std::string& code);

lv_font_t* font(int weight, float css_px);
lv_obj_t* mk(lv_obj_t* p, int x, int y, int w, int h, int clip);
void bg(lv_obj_t* o, uint32_t c, int a);
void bd(lv_obj_t* o, uint32_t c, int a, int w, lv_border_side_t s);
void ol(lv_obj_t* o, uint32_t c, int a, int w);
void sh(lv_obj_t* o, uint32_t c, int a, int blur, int x, int y, int spread);
lv_obj_t* txt(lv_obj_t* p, int cx, int cy, const char* s, int weight, float px, uint32_t c, int a, int ls, int wrap_w);
// 和 txt() 一样，只是位置按「这段字变长的时候哪一边不动」来摆（设计稿里量出来的：靠左 l / 靠右 r / 居中 c）。
// 为什么要分：换成英文、德文以后好些句子比中文长，一律按居中摆的话两头都往外顶，会叠到旁边的字上
lv_obj_t* txta(lv_obj_t* p, int ax, int cy, char anchor, const char* s, int weight, float px, uint32_t c, int a, int ls, int wrap_w);
// 紧跟在前一段字后面的那一段（卡片标题后面跟一句小字、标签后面跟一句提示，设计稿里到处是这种）。
// 换成英文、德文以后前面那段会变长，这一段的位置写死的话就被压在底下了：
// 量一下前一段比设计稿里宽了多少，这一段就往右让多少。前面那段没变长（中文）就一动不动，和原来一样。
// prev_w 是前一段在设计稿里的宽度
lv_obj_t* txta_shift(lv_obj_t* p, int ax, int cy, char anchor, lv_obj_t* prev, int prev_w, const char* s, int weight, float px, uint32_t c, int a, int ls, int wrap_w);
lv_obj_t* im(lv_obj_t* p, int x, int y, const char* path);
// 一段文字现在多宽（按字体直接量，不用先让整页重排一遍）
int label_w(lv_obj_t* label);

// 「这段字本来就写在图上」。排版自查（--lint）把图片也当成「别人的地盘」，压上去要报；
// 可釜盖右上角的通道号就是画在釜体照片上的，不标一下每次自查都是一堆假警报
void mark_on_art(lv_obj_t* label);
bool is_on_art(lv_obj_t* label);

// 一页上所有「有名字的东西」
class Ui {
public:
    std::function<void(const std::string& action)> on_action;
    void reg_text(const char* id, lv_obj_t* label, char anchor, int ax, int cy);
    void reg_box(const char* id, lv_obj_t* obj) { boxes_[id] = obj; }
    void reg_act(lv_obj_t* obj, const char* action);
    // 改一段文字。对齐方式照设计稿里它原来的（靠左 / 靠右 / 居中），字变长变短位置都对
    void set_text(const std::string& id, const std::string& s0);
    // 不过词表，原样写上去。语言名（English、日本語…）、路径、编号这种不该翻的走这个
    void set_text_raw(const std::string& id, const std::string& s);
    // 「这一段紧跟在 prev 后面」。prev 的字一换（比如「通道 1」换成 Channel 1），这一段自己往右让，
    // 让的距离 = prev 现在的宽度 − 它在设计稿里的宽度 prev_w。中文时两者一样，一动不动
    void reg_follow(const std::string& id, lv_obj_t* prev, int prev_w);
    void set_text_color(const std::string& id, uint32_t c);
    // 「这个样子上次是不是已经设过了」：每半秒刷一次状态，样式没变就不要再设——
    // 设一次样式 LVGL 就把那一块重画一遍，弹窗开着时底下每重画一块都要连阴影一起算，很慢
    bool changed(const std::string& key, long long v) { auto it = seen_.find(key); if (it != seen_.end() && it->second == v) return false; seen_[key] = v; return true; }
    void show(const std::string& id, bool on);
    lv_obj_t* label(const std::string& id) const { auto it = texts_.find(id); return it == texts_.end() ? nullptr : it->second.label; }
    lv_obj_t* box(const std::string& id) const { auto it = boxes_.find(id); return it == boxes_.end() ? nullptr : it->second; }
    bool has_text(const std::string& id) const { return texts_.count(id) != 0; }

private:
    struct T { lv_obj_t* label; char anchor; int ax, cy; std::string cur; lv_obj_t* prev = nullptr; int prev_w = 0; };
    int follow_shift(const T& t) const;
    void place(const std::string& id);
    std::map<std::string, T> texts_;
    std::map<lv_obj_t*, std::string> id_of_;            // 反查：这个 label 在 texts_ 里叫什么名字
    std::map<lv_obj_t*, std::string> follower_of_;      // 这一段字后面跟着谁

    std::map<std::string, long long> seen_;
    std::map<std::string, lv_obj_t*> boxes_;
    std::vector<std::string*> acts_;
};

struct PageDef { const char* name; void (*build)(lv_obj_t* scr, Ui& ui); };

}  // namespace ui
