#include "run_recorder.hpp"
#include "../util/utf8_file.hpp"
#include <cstdio>
#include "../../third_party/nlohmann/json.hpp"
#include <algorithm>
#include <chrono>
#include <ctime>
#include <filesystem>
#include <fstream>
#include <sstream>

namespace fs = std::filesystem;
using json = nlohmann::json;

namespace tec {

namespace {
std::string now_text(const char* fmt) {
    const std::time_t t = std::chrono::system_clock::to_time_t(std::chrono::system_clock::now());
    std::tm tm{};
#ifdef _WIN32
    localtime_s(&tm, &t);
#else
    localtime_r(&t, &tm);
#endif
    char b[64];
    std::strftime(b, sizeof b, fmt, &tm);
    return b;
}
// 名字要当文件夹名用：把系统不许出现在文件名里的字符换掉
std::string safe_name(std::string s) {
    for (char& c : s) if (c == '/' || c == '\\' || c == ':' || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|') c = '_';
    return s.empty() ? "未命名" : s;
}
std::string csv(const std::string& s) {      // 文字里有逗号、引号、换行就整个用引号包起来
    if (s.find_first_of(",\"\n") == std::string::npos) return s;
    std::string o = "\"";
    for (char c : s) { if (c == '"') o += '"'; o += c; }
    return o + "\"";
}
std::string num(const std::optional<double>& v, const char* fmt) {
    if (!v) return "";
    char b[32]; std::snprintf(b, sizeof b, fmt, *v); return b;
}
}  // namespace

bool RunRecorder::begin(const std::string& name, int channel) {
    end();
    name_ = name; channel_ = channel; started_ = now_text("%Y-%m-%d %H:%M:%S"); last_t_ = 0;
    id_ = now_text("%Y%m%d-%H%M%S") + "-通道" + std::to_string(channel) + "-" + safe_name(name);
    std::error_code ec;
    const fs::path p = fs::u8path(dir_) / "runs" / fs::u8path(id_);
    fs::create_directories(p, ec);
    if (ec) return false;
    f_samples_ = open_file(p / "samples.csv", "w");
    f_events_ = open_file(p / "events.csv", "w");
    f_steps_ = open_file(p / "steps.csv", "w");
    if (!f_samples_ || !f_events_ || !f_steps_) { end(); return false; }
    std::fputs("t_s,sp,sp_now,tr,tj,tj_sp,duty,side,stir_set,stir_rpm,ph,dosed_ml,step\n", f_samples_);
    std::fputs("t_s,kind,text\n", f_events_);
    std::fputs("index,what,start_s,end_s,status\n", f_steps_);
    write_meta(true);
    return true;
}

void RunRecorder::write_meta(bool running) {
    json m = {{"name", name_}, {"channel", channel_}, {"started", started_}, {"running", running}, {"duration_s", last_t_}};
    std::ofstream(fs::u8path(dir_) / "runs" / fs::u8path(id_) / "meta.json") << m.dump(2);
}

void RunRecorder::sample(const Sample& s) {
    if (!f_samples_) return;
    std::fprintf(f_samples_, "%.1f,%.2f,%.3f,%s,%.3f,%.3f,%.2f,%c,%.0f,%.0f,%s,%.2f,%d\n", s.t_s, s.sp, s.sp_now, num(s.tr, "%.2f").c_str(), s.tj, s.tj_sp, s.duty, s.side,
                 s.stir_set, s.stir_rpm, num(s.ph, "%.2f").c_str(), s.dosed_ml, s.step);
    last_t_ = s.t_s;
    // 每 10 行落一次盘：断电最多丢 10 秒
    if (static_cast<long>(s.t_s) % 10 == 0) std::fflush(f_samples_);
}

void RunRecorder::event(double t_s, const std::string& kind, const std::string& text) {
    if (!f_events_) return;
    std::fprintf(f_events_, "%.1f,%s,%s\n", t_s, csv(kind).c_str(), csv(text).c_str());
    std::fflush(f_events_);         // 事件少而要紧，每条都落盘
}

void RunRecorder::step(int index, const std::string& what, double start_s, double end_s, const std::string& status) {
    if (!f_steps_) return;
    std::fprintf(f_steps_, "%d,%s,%.1f,%.1f,%s\n", index, csv(what).c_str(), start_s, end_s, csv(status).c_str());
    std::fflush(f_steps_);
}

void RunRecorder::end() {
    const bool was = f_samples_ != nullptr;
    for (std::FILE** f : {&f_samples_, &f_events_, &f_steps_}) if (*f) { std::fclose(*f); *f = nullptr; }
    if (was) write_meta(false);
}

std::vector<RunInfo> RunRecorder::list(const std::string& data_dir) {
    std::vector<RunInfo> out;
    std::error_code ec;
    for (auto& e : fs::directory_iterator(fs::u8path(data_dir) / "runs", ec)) {
        if (!e.is_directory()) continue;
        std::ifstream in(e.path() / "meta.json");
        if (!in) continue;
        const json m = json::parse(in, nullptr, false);
        if (m.is_discarded()) continue;       // 这一炉的 meta 坏了（写到一半断电）：跳过，不让它挡住别的
        RunInfo r;
        r.id = e.path().filename().u8string();
        r.name = m.value("name", ""); r.channel = m.value("channel", 1); r.running = m.value("running", false);
        r.duration_s = m.value("duration_s", 0.0); r.started = m.value("started", "");
        out.push_back(r);
    }
    std::sort(out.begin(), out.end(), [](const RunInfo& a, const RunInfo& b) { return a.id > b.id; });
    return out;
}

int RunRecorder::export_runs(const std::string& data_dir, const std::vector<std::string>& ids, int interval_s, const std::string& out_dir, double from_s, double to_s) {
    const bool ranged = from_s > 0 || to_s >= 0;
    auto inside = [&](const std::string& line) { const double t = std::atof(line.c_str()); return t >= from_s && (to_s < 0 || t <= to_s); };
    int n = 0;
    std::error_code ec;
    for (const std::string& id : ids) {
        char rng[64] = "";
        if (ranged) std::snprintf(rng, sizeof rng, to_s >= 0 ? "（%.0f-%.0f min）" : "（%.0f min 起）", from_s / 60.0, to_s / 60.0);
        const fs::path src = fs::u8path(data_dir) / "runs" / fs::u8path(id), dst = fs::u8path(out_dir) / fs::u8path(id + rng);
        if (!fs::exists(src / "samples.csv")) continue;
        fs::create_directories(dst, ec);
        if (ec) continue;
        const char* bom = "\xEF\xBB\xBF";
        auto copy_with = [&](const char* from, const char* to, const char* header, bool by_time) {
            std::ifstream in(src / from);
            std::ofstream o(dst / fs::u8path(to), std::ios::binary);
            o << bom << header << "\n";
            std::string line;
            std::getline(in, line);
            while (std::getline(in, line)) if (!by_time || inside(line)) o << line << "\n";
        };
        copy_with("steps.csv", "执行记录.csv", "步骤,内容,开始(s),结束(s),状态", false);       // 执行记录不裁：各步的起止本来就写在里面
        copy_with("events.csv", "事件日志.csv", "时间(s),类别,内容", ranged);
        {   // 采样宽表：按间隔抽稀。按「第几个间隔」取每个间隔里的第一行，不是按行号数——中间缺过几秒也不会错位
            std::ifstream in(src / "samples.csv");
            std::ofstream o(dst / fs::u8path("采样宽表.csv"), std::ios::binary);
            o << bom << "时间(s),目标(℃),当前目标(℃),釜内Tr(℃),夹套Tj(℃),夹套目标(℃),占空比(%),热源,搅拌设定(rpm),搅拌实测(rpm),pH,已加料(mL),步骤\n";
            std::string line;
            std::getline(in, line);
            long last_bucket = -1;
            while (std::getline(in, line)) {
                if (ranged && !inside(line)) continue;
                const long bucket = static_cast<long>(std::atof(line.c_str())) / std::max(1, interval_s);
                if (bucket == last_bucket) continue;
                last_bucket = bucket;
                o << line << "\n";
            }
        }
        n++;
    }
    return n;
}

}  // namespace tec
