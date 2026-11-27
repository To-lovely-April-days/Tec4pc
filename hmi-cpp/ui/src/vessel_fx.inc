// 釜上的状态（照设计稿「釜体状态表现」来，用户 2026-11-26 定）。这个文件直接嵌在 main.cpp 的 App 里面。
//
// 一只釜是三层叠出来的：底下是釜体照片，中间一张外圈（升温的橙带 / 降温的青虚线 / 超限的红边），
// 最上面是搅拌桨。外圈和桨的图都和照片同一个框（设计稿 ui/design/FxMain.dc.html、FxCtl.dc.html 出的
// fx_*.png），贴在照片同一个位置上就天然对齐 —— 代码里不写釜底的弧度，那条轮廓是从釜体照片上一行一行
// 量出来的（量法见生成那两张设计稿的脚本）；照片换了，重量一遍就行，不会再对不上。
//
// 桨为什么要单独一层：原来三片桨是印在照片里的，照片转不动。现在照片里的桨抹掉了，桨另画六帧
// （三片叶子转 120° 就是一整圈），轮着贴就是在转。
//
// 动起来的几样都是换图或者改透明度，没有逐帧重画：
//   升温 橙带忽明忽暗 · 降温 青虚线三帧轮着走 · 超限 红边闪 · 读不到 通道号外面的圈黄白交替闪
struct VesselFx {
    lv_obj_t* ring = nullptr;      // 外圈
    lv_obj_t* dose = nullptr;      // 加料：往下滴的那一滴和液面上的涟漪
    lv_obj_t* prop = nullptr;      // 搅拌桨
    lv_obj_t* badge = nullptr;     // 釜盖右上角的通道号
    std::string tag;               // 用哪一套贴图：m = 总览上那只釜的尺寸，c = 设备控制页那只
    int kind = 0;                  // 外圈画什么：0 不画 1 升温 2 降温 3 超限
    int rpm = 0;                   // 搅拌多快：0 = 不转
    bool bflash = false;           // 通道号要不要闪（读不到的时候）
    bool dosing = false;           // 加料泵在不在走
    int frame = 0, acc = 0, phase = 0, at_frame = -1, at_cool = -1, at_dose = -1;
};
std::map<lv_obj_t*, VesselFx> vfx;
lv_timer_t* fx_timer = nullptr;

static std::string fx_img(const std::string& tag, const std::string& what) {
    return "A:assets/img/fx_" + tag + "_" + what + "_" + std::to_string(UI_SCALE) + ".png";
}

// 页面搭好以后把外圈和桨这两层贴上去。按釜体照片那个元素的位置摆，两张图和照片同框，所以直接同坐标
void attach_fx(Ui& u, const std::string& vid, const std::string& tag, const std::string& bid) {
    lv_obj_t* v = u.box(vid);
    if (!v || vfx.count(v)) return;
    VesselFx& f = vfx[v];
    f.tag = tag;
    f.badge = u.box(bid);
    ui::mark_on_art(u.label(bid));      // 通道号是写在釜体照片上的，排版自查别报它压住了图
    lv_obj_t* par = lv_obj_get_parent(v);
    const int x = lv_obj_get_x(v), y = lv_obj_get_y(v);
    f.ring = im(par, x, y, fx_img(tag, "heat").c_str());
    f.dose = im(par, x, y, fx_img(tag, "dose0").c_str());
    f.prop = im(par, x, y, fx_img(tag, "stir0").c_str());
    lv_obj_add_flag(f.ring, LV_OBJ_FLAG_HIDDEN);
    lv_obj_add_flag(f.dose, LV_OBJ_FLAG_HIDDEN);
    if (!fx_timer) fx_timer = lv_timer_create([](lv_timer_t* t) { static_cast<App*>(lv_timer_get_user_data(t))->fx_tick(); }, 120, this);
}

// 只管动画，不管状态：每 120 毫秒叫一次。状态由 vessel_state() 每半秒刷一次
void fx_tick() {
    for (auto& kv : vfx) {
        VesselFx& f = kv.second;
        if (!f.prop) continue;
        if (lv_obj_get_screen(f.prop) != lv_screen_active()) continue;      // 没显示的那几页不用动，白费解码
        f.phase++;
        // 搅拌：转得越快换得越勤。400 rpm 差不多一拍一帧（转一整圈 0.7 秒），200 rpm 两拍一帧
        if (f.rpm > 0) { f.acc += f.rpm; while (f.acc >= 400) { f.acc -= 400; f.frame = (f.frame + 1) % 6; } }
        else { f.frame = 0; f.acc = 0; }
        if (f.frame != f.at_frame) { lv_image_set_src(f.prop, fx_img(f.tag, "stir" + std::to_string(f.frame)).c_str()); f.at_frame = f.frame; }
        if (f.badge && f.bflash) ol(f.badge, (f.phase / 4) % 2 ? 0xFFFFFF : 0xB8860B, 255, 2 * UI_SCALE);
        if (f.dose && f.dosing) {      // 加料：四帧一个来回，一滴落下去、液面荡开一圈
            const int fr = (f.phase / 2) % 4;
            if (fr != f.at_dose) { lv_image_set_src(f.dose, fx_img(f.tag, "dose" + std::to_string(fr)).c_str()); f.at_dose = fr; }
        }
        if (f.kind == 1) {
            const int k = f.phase % 16, up = k < 8 ? k : 16 - k;      // 三角波：两秒一个来回
            lv_obj_set_style_image_opa(f.ring, static_cast<lv_opa_t>(70 + up * 23), 0);
        } else if (f.kind == 2) {
            const int fr = (f.phase / 2) % 3;                         // 三帧虚线轮着走，看着像在流
            if (fr != f.at_cool) { lv_image_set_src(f.ring, fx_img(f.tag, "cool" + std::to_string(fr)).c_str()); f.at_cool = fr; }
        } else if (f.kind == 3) {
            lv_obj_set_style_image_opa(f.ring, (f.phase / 4) % 2 ? 60 : 255, 0);
        }
    }
}

// 这一路有没有还没确认的报警（超限、联锁这些）。报警里记着是哪个通道
bool ch_alarm(int ch) {
    if (!st.is_object() || st.value("alarms_unacked", 0) <= 0 || !alv.list.is_array()) return false;
    for (auto& a : alv.list) if (!a.value("acked", false) && a.value("ch", 0) == ch) return true;
    return false;
}

// 按这一路现在的样子，决定釜上贴什么、通道号什么颜色
void vessel_state(Ui& u, const std::string& vid, const std::string& bid, const json& c, int ch, bool offline) {
    lv_obj_t* v = u.box(vid);
    if (!v) return;
    const bool run = c.value("running", false);
    const double duty = c.value("duty", 0.0);
    const std::string qs = c["seq"].value("state", "idle");
    const bool alarm = ch_alarm(ch);
    // 釜体照片本身的深浅：读不到的时候整只淡下去，别的时候就是原色。
    //
    // 不能用「染色」（image_recolor）：染色只作用在照片不透明的那些点上，而这张照片里
    // 玻璃壁围出来的那一块内壁是实白的、照片外面是透明的。一染，内壁变成灰的、外面还是卡片的白，
    // 看着就像釜里头糊了一块灰影（用户 2026-11-27 现场看出来的）。
    // 整张一起调透明度就没这毛病：连外框带内壁一起变淡，看着就是「这一路现在没在说话」
    if (u.changed(vid + ".tone", offline)) {
        lv_obj_set_style_image_recolor_opa(v, 0, 0);
        lv_obj_set_style_image_opa(v, static_cast<lv_opa_t>(offline ? 120 : 255), 0);
    }
    // 外圈：超限的红边盖过一切（这时候输出已经切了，橙带青线都不该再出现），然后才看在加热还是在制冷
    const int kind = offline ? 0 : alarm ? 3 : !run ? 0 : duty < -1 ? 1 : duty > 1 ? 2 : 0;
    auto it = vfx.find(v);
    if (it != vfx.end() && it->second.ring) {
        VesselFx& f = it->second;
        if (kind != f.kind) {
            f.kind = kind; f.phase = 0; f.at_cool = -1;
            if (!kind) lv_obj_add_flag(f.ring, LV_OBJ_FLAG_HIDDEN);
            else {
                lv_obj_remove_flag(f.ring, LV_OBJ_FLAG_HIDDEN);
                lv_obj_set_style_image_opa(f.ring, 255, 0);
                if (kind != 2) lv_image_set_src(f.ring, fx_img(f.tag, kind == 1 ? "heat" : "alarm").c_str());
            }
        }
        // 搅拌读不到、或者没开，桨就停着。开起来了但这一秒读回来是 0（刚启动）就按设的转速转，
        // 免得看着像没动
        const json& s = c["stir"];
        const bool on = s.value("ok", false) && s.value("set", 0.0) > 0;
        const double rpm = s.value("rpm", 0.0);
        f.rpm = !on ? 0 : static_cast<int>(rpm > 1 ? rpm : s.value("set", 0.0));
        // 加料泵在走：釜里多一层往下滴的动画。读不到的时候整只釜是灰的，这层也不画
        const bool dosing = !offline && c["dose"].value("active", false);
        if (dosing != f.dosing && f.dose) {
            f.dosing = dosing; f.at_dose = -1;
            if (dosing) lv_obj_remove_flag(f.dose, LV_OBJ_FLAG_HIDDEN); else lv_obj_add_flag(f.dose, LV_OBJ_FLAG_HIDDEN);
        }
        const bool flash = offline;
        if (flash != f.bflash) { f.bflash = flash; if (!flash && f.badge) ol(f.badge, 0xFFFFFF, 255, 2 * UI_SCALE); }
    }
    // 釜盖右上角的通道号：读不到灰、超限红、升温橙、降温青、保温绿、程序跑完灰打勾、停着蓝。
    // 在控温的排在「跑完」前面：序列跑完以后又手动开了控温，该显示在加热还是在制冷，不是那个勾
    const int bst = offline ? 0 : alarm ? 1 : run ? (duty < -1 ? 4 : duty > 1 ? 5 : 6) : qs == "done" ? 2 : 3;
    static const uint32_t bcol[] = {0xC3CAD3, 0xD64545, 0x8792A0, 0x0A66C2, 0xE8590C, 0x1098AD, 0x14995A};
    if (lv_obj_t* b = u.box(bid)) if (u.changed(bid + ".st", bst)) bg(b, bcol[bst], 255);
    u.set_text_raw(bid, bst == 2 ? "✓" : std::to_string(ch));
}
