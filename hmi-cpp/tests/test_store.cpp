// 配方库和设置存盘的测试：存了再读回来要一样；坏文件、越界的设置要处理得住。
#include "../core/data/store.hpp"
#include <cstdio>
#include <filesystem>
#include <fstream>
using namespace tec;
namespace fs = std::filesystem;
static int fails = 0;
#define CHECK(c) do { if (!(c)) { std::printf("失败 %s:%d  %s\n", __FILE__, __LINE__, #c); fails++; } } while (0)

int main() {
    const fs::path base = fs::temp_directory_path() / "tec_store_test";
    fs::remove_all(base);
    const std::string dir = base.u8string();
    std::vector<Step> steps(5);
    steps[0].kind = StepKind::Reactor; steps[0].target.value = 65; steps[0].target.arrive = Arrive::Duration; steps[0].target.duration_min = 12; steps[0].stir_rpm = 300;
    steps[1].kind = StepKind::Wait; steps[1].minutes = 45;
    steps[2].kind = StepKind::Crystal; steps[2].profile = {{45, 30}, {45, 20}, {20, 50}, {5, 60}};
    steps[3].kind = StepKind::Dose; steps[3].dose.pump = 2; steps[3].dose.mode = DoseMode::PhHold; steps[3].dose.ph_target = 6.5; steps[3].dose.hold_min = 30; steps[3].dose.liquid = "盐酸 1 mol/L";
    steps[4].kind = StepKind::Pause; steps[4].message = "请加入晶种";
    RecipeStore rs(dir);
    CHECK(rs.save("结晶工艺-0824", steps));
    CHECK(rs.save("低温筛选-B3", {steps[1]}));
    auto ls = rs.list();
    CHECK(ls.size() == 2);
    std::vector<Step> back;
    CHECK(rs.load("结晶工艺-0824", back) && back.size() == 5);
    CHECK(back[0].kind == StepKind::Reactor && back[0].target.value == 65 && back[0].target.arrive == Arrive::Duration && back[0].target.duration_min == 12 && back[0].stir_rpm == 300);
    CHECK(back[2].kind == StepKind::Crystal && back[2].profile.size() == 4 && back[2].profile[3].target == 5 && back[2].profile[3].minutes == 60);
    CHECK(back[3].dose.pump == 2 && back[3].dose.mode == DoseMode::PhHold && back[3].dose.ph_target == 6.5 && back[3].dose.liquid == "盐酸 1 mol/L");
    CHECK(back[4].kind == StepKind::Pause && back[4].message == "请加入晶种");
    CHECK(estimate_minutes(steps) == 12 + 45 + 160 + 30);
    for (auto& r : ls) if (r.name == "结晶工艺-0824") CHECK(r.steps == 5 && r.est_minutes == 247);
    CHECK(rs.remove("低温筛选-B3") && rs.list().size() == 1 && !rs.load("低温筛选-B3", back));
    { std::vector<Step> seven(7); CHECK(!rs.save("太长", seven)); }
    // 通道现在编着的序列
    CHECK(rs.save_channel(1, steps) && rs.load_channel(1, back) && back.size() == 5 && !rs.load_channel(0, back));
    // json 文本往返（TCP 用）
    CHECK(steps_from_json(steps_to_json(steps), back) && back.size() == 5 && back[1].minutes == 45);
    CHECK(!steps_from_json("不是 json", back) && !steps_from_json("{\"a\":1}", back));
    // 设置：缺省、改了存、读回来
    AppSettings s = AppSettings::load(dir);
    CHECK(s.devices.size() == 9 && s.device("rd105")->port == "COM7" && s.device("stir1")->port == "COM14" && s.device("stir2")->port == "COM11");
    s.devices[0].port = "COM21"; s.tcp.port = 6000; s.limits[0].tr_max = 120; s.stir[1].poles = 8; s.fast_boost_max = 10;
    CHECK(s.save(dir));
    AppSettings t = AppSettings::load(dir);
    CHECK(t.device("rd105")->port == "COM21" && t.tcp.port == 6000 && t.limits[0].tr_max == 120 && t.limits[1].tr_max == 180 && t.stir[1].poles == 8 && t.fast_boost_max == 10);
    // 设置文件被人改坏：夹套上限写到 300、把加热棒配到 DO2——都不许生效
    { std::ifstream in(base / "settings.json"); std::string txt((std::istreambuf_iterator<char>(in)), {}); in.close();
      auto p = txt.find("\"tj_max\": 200.0"); CHECK(p != std::string::npos); txt.replace(p, 15, "\"tj_max\": 300.0");
      p = txt.find("\"heater\": ["); CHECK(p != std::string::npos); const auto q = txt.find(']', p); txt.replace(p, q - p + 1, "\"heater\": [2, 1]");
      std::ofstream(base / "settings.json") << txt; }
    t = AppSettings::load(dir);
    CHECK(t.limits[0].tj_max == 200 && t.do_map.heater[0] == 0 && t.do_map.tec[0] == 7 && t.do_map.water == 4);
    // 整个文件是乱码：用缺省
    std::ofstream(base / "settings.json") << "{{{";
    CHECK(AppSettings::load(dir).device("rd105")->port == "COM7");
    fs::remove_all(base);
    std::printf(fails ? "\n%d 项失败\n" : "配方库与设置：全部通过\n", fails);
    return fails ? 1 : 0;
}
