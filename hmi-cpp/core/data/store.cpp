#include "store.hpp"
#include "../../third_party/nlohmann/json.hpp"
#include <algorithm>
#include <chrono>
#include <ctime>
#include <filesystem>
#include <fstream>

namespace fs = std::filesystem;
using json = nlohmann::json;

namespace tec {

namespace {
const char* kKinds[] = {"reactor", "jacket", "reflux", "crystal", "stir", "wait", "dose", "pause"};
const char* kArrive[] = {"fast", "rate", "duration"};
const char* kDose[] = {"at_once", "rate", "ph_hold"};

template <size_t N> int index_of(const char* const (&names)[N], const std::string& s, int fallback) {
    for (size_t i = 0; i < N; i++) if (s == names[i]) return static_cast<int>(i);
    return fallback;
}

json to_json(const Step& s) {
    json j = {{"kind", kKinds[static_cast<int>(s.kind)]}};
    switch (s.kind) {
        case StepKind::Reactor: case StepKind::Jacket:
            j["target"] = s.target.value; j["arrive"] = kArrive[static_cast<int>(s.target.arrive)];
            j["rate_k_per_min"] = s.target.rate_k_per_min; j["duration_min"] = s.target.duration_min; j["stir_rpm"] = s.stir_rpm;
            break;
        case StepKind::Reflux: j["delta_k"] = s.reflux_delta_k; j["minutes"] = s.minutes; j["stir_rpm"] = s.stir_rpm; break;
        case StepKind::Crystal: {
            json a = json::array();
            for (auto& g : s.profile) a.push_back({{"target", g.target}, {"minutes", g.minutes}});
            j["profile"] = a; j["stir_rpm"] = s.stir_rpm;
            break;
        }
        case StepKind::Stir: j["stir_rpm"] = s.stir_rpm; break;
        case StepKind::Wait: j["minutes"] = s.minutes; break;
        case StepKind::Dose:
            j["dose"] = {{"pump", s.dose.pump}, {"mode", kDose[static_cast<int>(s.dose.mode)]}, {"liquid", s.dose.liquid}, {"volume_ml", s.dose.volume_ml},
                         {"rate_ml_min", s.dose.rate_ml_min}, {"sync_with_temp", s.dose.sync_with_temp}, {"ph_target", s.dose.ph_target}, {"ph_band", s.dose.ph_band},
                         {"max_rate_ml_min", s.dose.max_rate_ml_min}, {"max_volume_ml", s.dose.max_volume_ml}, {"hold_min", s.dose.hold_min}};
            break;
        case StepKind::Pause: j["message"] = s.message; break;
    }
    return j;
}

Step step_from(const json& j) {
    Step s;
    s.kind = static_cast<StepKind>(index_of(kKinds, j.value("kind", "wait"), 5));
    s.target.value = j.value("target", 0.0);
    s.target.arrive = static_cast<Arrive>(index_of(kArrive, j.value("arrive", "fast"), 0));
    s.target.rate_k_per_min = j.value("rate_k_per_min", 0.0);
    s.target.duration_min = j.value("duration_min", 0.0);
    s.stir_rpm = j.value("stir_rpm", -1.0);
    s.minutes = j.value("minutes", 0.0);
    s.reflux_delta_k = j.value("delta_k", 8.0);
    s.message = j.value("message", "");
    if (j.contains("profile") && j["profile"].is_array())
        for (auto& g : j["profile"]) s.profile.push_back({g.value("target", 0.0), g.value("minutes", 0.0)});
    if (j.contains("dose") && j["dose"].is_object()) {
        const json& d = j["dose"];
        DoseParams def;
        s.dose.pump = d.value("pump", 1);
        s.dose.mode = static_cast<DoseMode>(index_of(kDose, d.value("mode", "rate"), 1));
        s.dose.liquid = d.value("liquid", "");
        s.dose.volume_ml = d.value("volume_ml", def.volume_ml); s.dose.rate_ml_min = d.value("rate_ml_min", def.rate_ml_min);
        s.dose.sync_with_temp = d.value("sync_with_temp", true);
        s.dose.ph_target = d.value("ph_target", def.ph_target); s.dose.ph_band = d.value("ph_band", def.ph_band);
        s.dose.max_rate_ml_min = d.value("max_rate_ml_min", def.max_rate_ml_min); s.dose.max_volume_ml = d.value("max_volume_ml", def.max_volume_ml);
        s.dose.hold_min = d.value("hold_min", def.hold_min);
    }
    return s;
}

json steps_json(const std::vector<Step>& steps) {
    json a = json::array();
    for (auto& s : steps) a.push_back(to_json(s));
    return a;
}

bool steps_parse(const json& a, std::vector<Step>& out) {
    if (!a.is_array() || a.size() > static_cast<size_t>(SequenceRunner::kMaxSteps)) return false;
    out.clear();
    for (auto& j : a) { if (!j.is_object()) return false; out.push_back(step_from(j)); }
    return true;
}

// 先写到临时文件再改名：写到一半断电，原来那份还在
bool write_text(const fs::path& p, const std::string& text) {
    std::error_code ec;
    fs::create_directories(p.parent_path(), ec);
    fs::path tmp = p; tmp += ".tmp";
    { std::ofstream o(tmp, std::ios::binary); if (!o) return false; o << text; if (!o) return false; }
    fs::rename(tmp, p, ec);
    return !ec;
}

std::string safe(std::string s) {
    for (char& c : s) if (c == '/' || c == '\\' || c == ':' || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|') c = '_';
    return s;
}

std::string today_text() {
    const std::time_t t = std::chrono::system_clock::to_time_t(std::chrono::system_clock::now());
    std::tm tm{};
#ifdef _WIN32
    localtime_s(&tm, &t);
#else
    localtime_r(&t, &tm);
#endif
    char b[32];
    std::strftime(b, sizeof b, "%m-%d %H:%M", &tm);
    return b;
}
}  // namespace

std::string steps_to_json(const std::vector<Step>& steps) { return steps_json(steps).dump(); }

bool steps_from_json(const std::string& text, std::vector<Step>& out) {
    const json a = json::parse(text, nullptr, false);
    return !a.is_discarded() && steps_parse(a, out);
}

double estimate_minutes(const std::vector<Step>& steps) {
    double m = 0;
    for (auto& s : steps) {
        switch (s.kind) {
            case StepKind::Reactor: case StepKind::Jacket: if (s.target.arrive == Arrive::Duration) m += s.target.duration_min; break;
            case StepKind::Reflux: case StepKind::Wait: m += s.minutes; break;
            case StepKind::Crystal: for (auto& g : s.profile) m += g.minutes; break;
            case StepKind::Dose:
                if (s.dose.mode == DoseMode::PhHold) m += s.dose.hold_min;
                else if (s.dose.mode == DoseMode::Rate && s.dose.rate_ml_min > 0) m += s.dose.volume_ml / s.dose.rate_ml_min;
                break;
            default: break;
        }
    }
    return m;
}

std::vector<RecipeInfo> RecipeStore::list() const {
    std::vector<std::pair<fs::file_time_type, RecipeInfo>> v;
    std::error_code ec;
    for (auto& e : fs::directory_iterator(fs::u8path(dir_) / "recipes", ec)) {
        if (e.path().extension() != ".json") continue;
        std::ifstream in(e.path());
        const json j = json::parse(in, nullptr, false);
        std::vector<Step> steps;
        if (j.is_discarded() || !steps_parse(j.value("steps", json::array()), steps)) continue;
        RecipeInfo r;
        r.name = e.path().stem().u8string(); r.steps = static_cast<int>(steps.size()); r.est_minutes = estimate_minutes(steps); r.saved = j.value("saved", "");
        v.push_back({e.last_write_time(ec), r});
    }
    std::sort(v.begin(), v.end(), [](auto& a, auto& b) { return a.first > b.first; });
    std::vector<RecipeInfo> out;
    for (auto& p : v) out.push_back(p.second);
    return out;
}

bool RecipeStore::load(const std::string& name, std::vector<Step>& out) const {
    std::ifstream in(fs::u8path(dir_) / "recipes" / fs::u8path(safe(name) + ".json"));
    if (!in) return false;
    const json j = json::parse(in, nullptr, false);
    return !j.is_discarded() && steps_parse(j.value("steps", json::array()), out);
}

bool RecipeStore::save(const std::string& name, const std::vector<Step>& steps) const {
    if (name.empty() || static_cast<int>(steps.size()) > SequenceRunner::kMaxSteps) return false;
    const json j = {{"saved", today_text()}, {"steps", steps_json(steps)}};
    return write_text(fs::u8path(dir_) / "recipes" / fs::u8path(safe(name) + ".json"), j.dump(2));
}

bool RecipeStore::remove(const std::string& name) const {
    std::error_code ec;
    return fs::remove(fs::u8path(dir_) / "recipes" / fs::u8path(safe(name) + ".json"), ec);
}

bool RecipeStore::load_channel(int ch, std::vector<Step>& out) const {
    std::ifstream in(fs::u8path(dir_) / ("sequence-ch" + std::to_string(ch + 1) + ".json"));
    if (!in) return false;
    const json j = json::parse(in, nullptr, false);
    return !j.is_discarded() && steps_parse(j, out);
}

bool RecipeStore::save_channel(int ch, const std::vector<Step>& steps) const {
    return write_text(fs::u8path(dir_) / ("sequence-ch" + std::to_string(ch + 1) + ".json"), steps_json(steps).dump(2));
}

bool RecipeStore::load_profile(int ch, std::vector<ProfileSegment>& out) const {
    std::ifstream in(fs::u8path(dir_) / ("profile-ch" + std::to_string(ch + 1) + ".json"));
    if (!in) return false;
    const json j = json::parse(in, nullptr, false);
    if (j.is_discarded() || !j.is_array()) return false;
    out.clear();
    for (auto& g : j) if (g.is_object()) out.push_back({g.value("target", 0.0), g.value("minutes", 0.0)});
    return true;
}

bool RecipeStore::save_profile(int ch, const std::vector<ProfileSegment>& segs) const {
    json a = json::array();
    for (auto& g : segs) a.push_back({{"target", g.target}, {"minutes", g.minutes}});
    return write_text(fs::u8path(dir_) / ("profile-ch" + std::to_string(ch + 1) + ".json"), a.dump(2));
}

AppSettings AppSettings::defaults() {
    AppSettings s;
    // 现场那台的接线（原来 PC 台面上配的那一套，搬到这里）
    // 后面两个空串是 usb_id / usb_name：出厂还没标定过 USB 口，开机只能认死这里写的 COM 号；
    // 现场在「通信设置 → 自动检测」里标定一次之后，COM 号再变就能自己找回来
    s.devices = {
        {"rd105", "TEC 温控器 RD105", true, "COM7", 38400, 'N', 1, 1, 500, "", ""},
        {"io8r", "切换继电器 IO8R", true, "COM8", 9600, 'N', 1, 1, 1000, "", ""},
        {"chiller", "冷水机", false, "COM9", 9600, 'N', 1, 1, 1000, "", ""},
        {"tr", "Tr 探头（宇电四路模块）", true, "COM3", 9600, 'N', 1, 1, 1000, "", ""},
        {"ph1", "pH 电极 · 通道 1", true, "COM12", 9600, 'N', 1, 1, 1000, "", ""},
        {"ph2", "pH 电极 · 通道 2", true, "COM13", 9600, 'N', 1, 1, 1000, "", ""},
        {"stir1", "搅拌 · 通道 1", true, "COM14", 9600, 'N', 2, 1, 500, "", ""},
        {"stir2", "搅拌 · 通道 2", true, "COM11", 9600, 'N', 2, 1, 500, "", ""},
        {"feed", "进料系统", true, "COM15", 9600, 'N', 1, 1, 1000, "", ""},
    };
    return s;
}

const DeviceComm* AppSettings::device(const std::string& key) const {
    for (auto& d : devices) if (d.key == key) return &d;
    return nullptr;
}

bool AppSettings::save(const std::string& data_dir) const {
    json j;
    j["devices"] = json::array();
    for (auto& d : devices)
        j["devices"].push_back({{"key", d.key}, {"label", d.label}, {"enabled", d.enabled}, {"port", d.port}, {"usb_id", d.usb_id}, {"usb_name", d.usb_name}, {"baud", d.baud}, {"parity", std::string(1, d.parity)},
                                {"stop_bits", d.stop_bits}, {"station", d.station}, {"poll_ms", d.poll_ms}});
    j["tcp"] = {{"enabled", tcp.enabled}, {"port", tcp.port}, {"timeout_s", tcp.timeout_s}, {"allow_remote_control", tcp.allow_remote_control}};
    for (int c = 0; c < 2; c++) {
        j["channels"].push_back({{"vessel", vessel[c]},
                                 {"limits", {{"tr_min", limits[c].tr_min}, {"tr_max", limits[c].tr_max}, {"tj_min", limits[c].tj_min}, {"tj_max", limits[c].tj_max},
                                             {"rate_max_k_per_min", limits[c].rate_max_k_per_min}, {"tr_lost_grace_s", limits[c].tr_lost_grace_s}}},
                                 {"stir", {{"poles", stir[c].poles}, {"gear_ratio", stir[c].gear_ratio}, {"direction", stir[c].direction}, {"ramp_s", stir[c].ramp_s}}}});
    }
    j["aux"] = {{"pump_max_rate_ml_min", aux.pump_max_rate_ml_min}, {"single_max_volume_ml", aux.single_max_volume_ml}, {"stir_min_rpm", aux.stir_min_rpm}, {"stir_max_rpm", aux.stir_max_rpm}};
    j["do_map"] = {{"heater", {do_map.heater[0], do_map.heater[1]}}, {"tec", {do_map.tec[0], do_map.tec[1]}}, {"water", do_map.water}};
    j["fast_boost_max"] = fast_boost_max;
    j["ui_lang"] = ui_lang;
    return write_text(fs::u8path(data_dir) / "settings.json", j.dump(2));
}

AppSettings AppSettings::load(const std::string& data_dir) {
    AppSettings s = defaults();
    std::ifstream in(fs::u8path(data_dir) / "settings.json");
    if (!in) return s;
    const json j = json::parse(in, nullptr, false);
    if (j.is_discarded() || !j.is_object()) return s;
    // 逐项读、读不到的留缺省：以后加了新设置，老的设置文件照样能用
    if (j.contains("devices") && j["devices"].is_array())
        for (auto& jd : j["devices"])
            for (auto& d : s.devices)
                if (d.key == jd.value("key", "")) {
                    d.enabled = jd.value("enabled", d.enabled); d.port = jd.value("port", d.port); d.baud = jd.value("baud", d.baud);
                    d.usb_id = jd.value("usb_id", d.usb_id); d.usb_name = jd.value("usb_name", d.usb_name);
                    const std::string par = jd.value("parity", std::string(1, d.parity)); if (!par.empty()) d.parity = par[0];
                    d.stop_bits = jd.value("stop_bits", d.stop_bits); d.station = jd.value("station", d.station); d.poll_ms = jd.value("poll_ms", d.poll_ms);
                }
    if (j.contains("tcp")) { const json& t = j["tcp"]; s.tcp.enabled = t.value("enabled", s.tcp.enabled); s.tcp.port = t.value("port", s.tcp.port); s.tcp.timeout_s = t.value("timeout_s", s.tcp.timeout_s); s.tcp.allow_remote_control = t.value("allow_remote_control", s.tcp.allow_remote_control); }
    if (j.contains("channels") && j["channels"].is_array())
        for (size_t c = 0; c < 2 && c < j["channels"].size(); c++) {
            const json& jc = j["channels"][c];
            s.vessel[c] = jc.value("vessel", s.vessel[c]);
            if (jc.contains("limits")) { const json& l = jc["limits"]; SafetyLimits& L = s.limits[c];
                L.tr_min = l.value("tr_min", L.tr_min); L.tr_max = l.value("tr_max", L.tr_max); L.rate_max_k_per_min = l.value("rate_max_k_per_min", L.rate_max_k_per_min); L.tr_lost_grace_s = l.value("tr_lost_grace_s", L.tr_lost_grace_s);
                // 夹套的两条是设备底线：文件里写得再宽也不许超过出厂的范围
                L.tj_min = std::max(SafetyLimits().tj_min, l.value("tj_min", L.tj_min)); L.tj_max = std::min(SafetyLimits().tj_max, l.value("tj_max", L.tj_max)); }
            if (jc.contains("stir")) { const json& m = jc["stir"]; StirMotor& M = s.stir[c]; M.poles = m.value("poles", M.poles); M.gear_ratio = m.value("gear_ratio", M.gear_ratio); M.direction = m.value("direction", M.direction); M.ramp_s = m.value("ramp_s", M.ramp_s); }
        }
    if (j.contains("aux")) { const json& a = j["aux"]; s.aux.pump_max_rate_ml_min = a.value("pump_max_rate_ml_min", s.aux.pump_max_rate_ml_min); s.aux.single_max_volume_ml = a.value("single_max_volume_ml", s.aux.single_max_volume_ml); s.aux.stir_min_rpm = a.value("stir_min_rpm", s.aux.stir_min_rpm); s.aux.stir_max_rpm = a.value("stir_max_rpm", s.aux.stir_max_rpm); }
    if (j.contains("do_map")) { const json& d = j["do_map"];
        if (d.contains("heater") && d["heater"].size() == 2) { s.do_map.heater[0] = d["heater"][0]; s.do_map.heater[1] = d["heater"][1]; }
        if (d.contains("tec") && d["tec"].size() == 2) { s.do_map.tec[0] = d["tec"][0]; s.do_map.tec[1] = d["tec"][1]; }
        s.do_map.water = d.value("water", s.do_map.water);
        // DO2 / DO3 是使能继电器，程序绝不许碰：设置文件里要是把哪一路配到了 2 或 3，整张表退回缺省
        for (int v : {s.do_map.heater[0], s.do_map.heater[1], s.do_map.tec[0], s.do_map.tec[1], s.do_map.water}) if (v == 2 || v == 3 || v < 0 || v > 7) { s.do_map = DoMap(); break; } }
    s.fast_boost_max = std::max(0.0, j.value("fast_boost_max", s.fast_boost_max));
    s.ui_lang = j.value("ui_lang", s.ui_lang);
    return s;
}

}  // namespace tec
