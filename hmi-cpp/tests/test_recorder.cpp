// 数据记录和导出的测试：记一炉、列出来、按 10 s 间隔导出，核对文件内容。
#include "../core/data/run_recorder.hpp"
#include <cstdio>
#include <filesystem>
#include <fstream>
#include <sstream>
using namespace tec;
namespace fs = std::filesystem;
static int fails = 0;
#define CHECK(c) do { if (!(c)) { std::printf("失败 %s:%d  %s\n", __FILE__, __LINE__, #c); fails++; } } while (0)
static std::string slurp(const fs::path& p) { std::ifstream in(p, std::ios::binary); std::stringstream ss; ss << in.rdbuf(); return ss.str(); }
static int lines(const std::string& s) { int n = 0; for (char c : s) n += c == '\n'; return n; }

int main() {
    const fs::path base = fs::temp_directory_path() / "tec_recorder_test";
    fs::remove_all(base);
    const std::string dir = base.u8string();
    {
        RunRecorder r(dir);
        CHECK(r.begin("结晶工艺/0824", 1));
        CHECK(r.active() && r.id().find("通道1-结晶工艺_0824") != std::string::npos);     // 斜杠换掉了
        for (int i = 0; i <= 100; i++) { Sample s; s.t_s = i; s.sp = 60; s.sp_now = 60; if (i != 5) s.tr = 20 + i * 0.1; s.tj = 25 + i * 0.2; s.tj_sp = 70; s.duty = -90; s.step = 1; r.sample(s); }
        r.event(12.5, "报警", "Tr 超过上限, 已停止");                                    // 带逗号：要加引号
        r.step(1, "Tr 升到 65.0 ℃", 0, 100, "完成");
        auto ls = RunRecorder::list(dir);
        CHECK(ls.size() == 1 && ls[0].running && ls[0].channel == 1 && ls[0].name == "结晶工艺/0824");
        r.end();
    }
    auto ls = RunRecorder::list(dir);
    CHECK(ls.size() == 1 && !ls[0].running && ls[0].duration_s == 100);
    const fs::path out = base / "Exports";
    CHECK(RunRecorder::export_runs(dir, {ls[0].id, "不存在的"}, 10, out.u8string()) == 1);
    const fs::path d = out / fs::u8path(ls[0].id);
    const std::string wide = slurp(d / fs::u8path("采样宽表.csv")), ev = slurp(d / fs::u8path("事件日志.csv")), st = slurp(d / fs::u8path("执行记录.csv"));
    CHECK(wide.rfind("\xEF\xBB\xBF", 0) == 0);                       // 带 BOM
    CHECK(lines(wide) == 1 + 11);                                    // 表头 + 0、10、…、100 s 共 11 行
    CHECK(wide.find("\n10.0,60.00,60.000,21.00,27.000,") != std::string::npos);
    CHECK(ev.find("\"Tr 超过上限, 已停止\"") != std::string::npos);
    CHECK(st.find("1,Tr 升到 65.0 ℃,0.0,100.0,完成") != std::string::npos);
    // 按 1 s 导出：全部 101 行；釜内没读到的那一行那一格是空的
    CHECK(RunRecorder::export_runs(dir, {ls[0].id}, 1, out.u8string()) == 1);
    const std::string all = slurp(d / fs::u8path("采样宽表.csv"));
    CHECK(lines(all) == 1 + 101 && all.find("\n5.0,60.00,60.000,,26.000,") != std::string::npos);
    fs::remove_all(base);
    std::printf(fails ? "\n%d 项失败\n" : "数据记录与导出：全部通过\n", fails);
    return fails ? 1 : 0;
}
