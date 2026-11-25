#include "core_app.hpp"
#include "../../third_party/nlohmann/json.hpp"
#include "../data/drives.hpp"
#include <fstream>
#include <filesystem>
#include <ctime>
#include <limits>
#include <cmath>
#include <set>

using json = nlohmann::json;

namespace tec {

namespace {
const char* mode_name(ControlMode m) {
    switch (m) { case ControlMode::Reactor: return "reactor"; case ControlMode::Jacket: return "jacket"; case ControlMode::Reflux: return "reflux"; case ControlMode::Crystal: return "crystal"; default: return "off"; }
}
const char* seq_state_name(SeqState s) {
    switch (s) { case SeqState::Running: return "running"; case SeqState::WaitingConfirm: return "waiting_confirm"; case SeqState::Done: return "done"; case SeqState::Aborted: return "aborted"; case SeqState::Faulted: return "faulted"; default: return "idle"; }
}
json opt(const std::optional<double>& v) { return v ? json(*v) : json(nullptr); }
json num(double v) { return std::isfinite(v) ? json(v) : json(nullptr); }
json profile_json(const std::vector<ProfileSegment>& segs) {
    json a = json::array();
    for (auto& g : segs) a.push_back({{"target", g.target}, {"minutes", g.minutes}});
    return a;
}
json fail(const std::string& why) { return {{"ok", false}, {"error", why}}; }

Target target_from(const json& j) {
    Target t;
    t.value = j.value("target", 0.0);
    const std::string a = j.value("arrive", "fast");
    t.arrive = a == "rate" ? Arrive::Rate : a == "duration" ? Arrive::Duration : Arrive::Fast;
    t.rate_k_per_min = j.value("rate_k_per_min", 0.0);
    t.duration_min = j.value("duration_min", 0.0);
    return t;
}

// 只看不改的命令。别的命令都会改变设备或存盘的状态，远程控制关着的时候 PC 不许下
const std::set<std::string> kReadOnly = {"state", "seq.get", "recipe.list", "recipe.get", "alarm.list", "limits.get", "settings.get", "runs.list", "gains.get", "events", "profile.get", "trend.get", "comm.status", "sys.info", "drives.list", "ports.list", "ports.scan.status"};
}  // namespace

CoreApp::CoreApp(ReactorHardware& hw, AuxHardware& aux, const std::string& data_dir, const std::string& gain_table_path)
    : dir_(data_dir), gain_path_(gain_table_path), settings_(AppSettings::load(data_dir)), table_(GainTable::load(gain_table_path)), recipes_(data_dir) {
    // 快速升温：加码上限从设置里来。0 = 不加码，就是原来稳妥的那套
    cfg_.outer.fast.enabled = settings_.fast_boost_max > 0;
    cfg_.outer.fast.boost_max = settings_.fast_boost_max;
    cfg_.outer.fast.bias_start = [this](double t) { return table_.steady_bias_at(t).value_or(0.0); };
    station_ = std::make_unique<Station>(hw, table_, cfg_);
    aux_ = std::make_unique<AuxRunner>(aux, settings_.aux);
    for (int c = 0; c < 2; c++) {
        station_->set_limits(c, settings_.limits[c]);
        ch_[c].ops = std::make_unique<StationChannelOps>(*station_, *aux_, c);
        ch_[c].seq = std::make_unique<SequenceRunner>(*ch_[c].ops);
        ch_[c].rec = std::make_unique<RunRecorder>(data_dir);
        std::vector<Step> steps;
        if (recipes_.load_channel(c, steps)) ch_[c].seq->load(steps);      // 上次编到一半的序列，重启后还在
        recipes_.load_profile(c, ch_[c].manual_profile);
    }
}

CoreApp::~CoreApp() { for (auto& c : ch_) if (c.rec) c.rec->end(); }

void CoreApp::begin_run(int ch, const std::string& name, double t) {
    Chan& c = ch_[ch];
    if (c.rec->active()) return;
    c.run_name = name;
    c.rec->begin(name, ch + 1);
    c.rec_t0 = t; c.last_step = -1; c.step_t0 = 0;
}

void CoreApp::tick() {
    station_->step();
    const StationSnapshot s = station_->snapshot();
    std::lock_guard<std::mutex> g(m_);
    if (k_ % 2 == 0) aux_->step(s.t);                 // 搅拌 / pH / 加料每秒一次
    for (int c = 0; c < 2; c++) ch_[c].seq->tick(s.t);
    for (auto& e : station_->take_events())
        if (e.ch >= 0 && ch_[e.ch].rec->active()) ch_[e.ch].rec->event(e.t - ch_[e.ch].rec_t0, e.text.find("安全联锁") != std::string::npos ? "报警" : "设备", e.text);
    if (k_ % 2 == 0) record(s.t);
    k_++;
}

// 每秒记一行；序列换步时记一条执行记录；这一炉结束（控温停了、序列也不在跑）就收尾
void CoreApp::record(double t) {
    const StationSnapshot s = station_->snapshot();
    const AuxSnapshot a = aux_->snapshot();
    for (int c = 0; c < 2; c++) {
        Chan& ch = ch_[c];
        const SeqStatus q = ch.seq->status();
        const bool seq_on = q.state == SeqState::Running || q.state == SeqState::WaitingConfirm;
        const bool active = s.ch[c].running || seq_on;
        if (k_ % 4 == 0) {
            const double nan = std::numeric_limits<double>::quiet_NaN();
            TrendPoint p;
            p.t = t; p.wall = static_cast<long long>(std::time(nullptr));
            p.tr = s.ch[c].tr.value_or(nan); p.tj = s.ch[c].tj; p.tj_sp = s.ch[c].running ? s.ch[c].jacket_sp : nan; p.sp = s.ch[c].running ? s.ch[c].setpoint_now : nan;
            p.ph = a.ph[c].ph.value_or(nan); p.rpm = a.stir[c].rpm; p.on = s.ch[c].running;
            ch.trend.push_back(p);
            if (ch.trend.size() > kTrendMax) ch.trend.pop_front();
        }
        if (!ch.rec->active()) { ch.was_active = active; continue; }
        const double rt = t - ch.rec_t0;
        if (seq_on && q.index != ch.last_step) {
            if (ch.last_step >= 0) ch.rec->step(ch.last_step + 1, "第 " + std::to_string(ch.last_step + 1) + " 步", ch.step_t0, rt, "完成");
            ch.last_step = q.index; ch.step_t0 = rt;
            ch.rec->event(rt, "步骤", "进入第 " + std::to_string(q.index + 1) + " 步");
        }
        Sample m;
        m.t_s = rt; m.sp = s.ch[c].setpoint; m.sp_now = s.ch[c].setpoint_now; m.tr = s.ch[c].tr; m.tj = s.ch[c].tj; m.tj_sp = s.ch[c].jacket_sp; m.duty = s.ch[c].duty;
        m.side = s.ch[c].side == HeatSide::Tec ? 'C' : 'H'; m.stir_set = a.stir_set_rpm[c]; m.stir_rpm = a.stir[c].rpm; m.ph = a.ph[c].ph; m.dosed_ml = a.dose[c].dosed_ml;
        m.step = seq_on ? q.index + 1 : 0;
        ch.rec->sample(m);
        if (!active && ch.was_active) {
            if (ch.last_step >= 0) ch.rec->step(ch.last_step + 1, "第 " + std::to_string(ch.last_step + 1) + " 步", ch.step_t0, rt, q.state == SeqState::Done ? "完成" : q.state == SeqState::Faulted ? "中断" : "结束");
            ch.rec->event(rt, "操作", "这一炉结束");
            ch.rec->end();
        }
        ch.was_active = active;
    }
}

std::string CoreApp::state_json() {
    const StationSnapshot s = station_->snapshot();
    const AuxSnapshot a = aux_->snapshot();
    json j = {{"ok", true}, {"t", s.t}, {"comm_ok", s.comm_ok}, {"problem", s.problem}, {"valve_open", s.valve_open}, {"chiller_run", s.chiller_run}, {"board_c", s.board_c}};
    j["coolant_c"] = opt(s.coolant_c);      // 冷媒温度 = 冷水机的冷却水温度；没接冷水机是空值
    j["offline"] = offline_reason;      // 不空 = 控温设备没连上，界面要醒目地说出来
    int unacked = 0;
    for (auto& al : station_->alarms()) unacked += !al.acked;
    j["alarms_unacked"] = unacked;
    j["ch"] = json::array();
    for (int c = 0; c < 2; c++) {
        const ChannelSnapshot& x = s.ch[c];
        const SeqStatus q = ch_[c].seq->status();
        j["ch"].push_back({
            {"running", x.running}, {"mode", mode_name(x.mode)}, {"sp", x.setpoint}, {"sp_now", x.setpoint_now}, {"tj", num(x.tj)}, {"tr", opt(x.tr)},
            {"tj_sp", x.jacket_sp}, {"duty", x.duty}, {"side", x.side == HeatSide::Tec ? "tec" : "heater"}, {"output_on", x.output_on},
            {"b_hat", x.b_hat}, {"tau_hat", x.tau_hat}, {"boost", x.boost}, {"segment", x.segment}, {"segment_remaining_s", x.segment_remaining_s},
            {"ramp_done", x.ramp_done}, {"profile_done", x.profile_done}, {"vessel", settings_.vessel[c]}, {"lamp", ch_[c].lamp},
            // 结晶曲线：正在按曲线控温时给的是正在走的那条（不管是手动开的还是序列里的结晶步骤），否则是存着的手动曲线
            {"profile", {{"active", !x.profile.empty()}, {"start", x.profile_start}, {"segments", profile_json(x.profile.empty() ? ch_[c].manual_profile : x.profile)}}},
            {"stir", {{"ok", a.stir[c].ok}, {"set", a.stir_set_rpm[c]}, {"rpm", a.stir[c].rpm}, {"current_a", a.stir[c].current_a}, {"fault", a.stir[c].fault_text}}},
            {"ph", opt(a.ph[c].ph)},
            {"dose", {{"active", a.dose[c].active}, {"done", a.dose[c].done}, {"pump", a.dose[c].pump}, {"dosed_ml", a.dose[c].dosed_ml}, {"target_ml", a.dose[c].target_ml}, {"note", a.dose[c].note}}},
            {"seq", {{"state", seq_state_name(q.state)}, {"index", q.index}, {"count", q.count}, {"step_progress", q.step_progress}, {"step_elapsed_s", q.step_elapsed_s},
                     {"total_elapsed_s", q.total_elapsed_s}, {"note", q.note}}},
            {"run", ch_[c].rec->active() ? json(ch_[c].run_name) : json(nullptr)},
        });
    }
    j["feed"] = {{"ok", a.feed.ok}, {"total_ml", {a.feed.total_ml[0], a.feed.total_ml[1]}}, {"flow_ml_min", {a.feed.flow_ml_min[0], a.feed.flow_ml_min[1]}},
                 {"on", {a.feed.on[0], a.feed.on[1]}}, {"alarm", a.feed.alarm_text}};
    return j.dump();
}

// ── 命令清单 ──
//   state                                   整机状态（界面每秒问几次）
//   temp.start  ch mode(reactor|jacket) target [arrive rate_k_per_min duration_min] [name]
//   temp.reflux ch delta_k [name]           temp.profile ch segments[{target,minutes}] [name]
//   temp.setpoint ch target                 temp.stop ch
//   stir.set ch rpm                         dose.start ch dose{…}        dose.stop ch
//   lamp.set ch on（照明：只记状态）   mark.add ch [text]（时间标记）   trend.snapshot ch（把趋势数据存成 CSV 快照）
//   drives.list（插着的 U 盘）   export.copy [dest]（把导出目录拷到 U 盘）   runs.export 可带 from_min / to_min
//   sys.info（版本、已运行多久、数据目录、磁盘剩余、炉次数、配方数）
//   sys.exit（退出控制核心；只收本机的，两个通道都停着才收）
//   comm.status（各设备通信参数和连接状态、连着的 PC、本机地址）
//   trend.get ch [after] [max]（最近 3 小时的读数，2 秒一点，趋势曲线用）
//   profile.get ch   profile.set ch segments[…]（手动结晶曲线，存盘；temp.profile 不带 segments 就用它）
//   seq.get ch    seq.load ch steps[…]      seq.start ch [name]          seq.end ch        seq.confirm ch
//   recipe.list   recipe.get name           recipe.save name steps[…]    recipe.delete name
//   alarm.list    alarm.ack [id]            limits.get ch                limits.set ch tr_min tr_max rate_max_k_per_min
//   settings.get  settings.set {…}（通信参数改了要重启控制核心才生效）
//   runs.list     runs.export ids[…] interval_s out_dir
//   gains.get     gains.set rows[{temp,kp,ki,kd,steady_bias,heat_ratio}…]     gains.reload（从盘上重读）。后两条要两个通道都停着才收
std::string CoreApp::handle(const std::string& line, bool remote) {
    const json q = json::parse(line, nullptr, false);
    if (q.is_discarded() || !q.is_object()) return fail("不是一条合法的 json 命令").dump();
    const std::string cmd = q.value("cmd", "");
    json r;
    try {
        if (cmd == "state") { std::lock_guard<std::mutex> g(m_); r = json::parse(state_json()); }
        else if (remote && !settings_.tcp.allow_remote_control && !kReadOnly.count(cmd)) r = fail("触摸屏上关了「允许远程控制」，PC 只能看");
        else {
            std::lock_guard<std::mutex> g(m_);
            const int c = q.value("ch", 1) - 1;                       // 命令里通道从 1 数
            const bool needs_ch = cmd.rfind("temp.", 0) == 0 || cmd.rfind("stir.", 0) == 0 || cmd.rfind("dose.", 0) == 0 || cmd.rfind("seq.", 0) == 0 || cmd.rfind("limits.", 0) == 0;
            const double t = station_->snapshot().t;
            const std::string who = remote ? "PC" : "触摸屏";
            auto note = [&](const std::string& text) { if (ch_[c].rec->active()) ch_[c].rec->event(t - ch_[c].rec_t0, "操作", who + "：" + text); };
            const SeqState ss = needs_ch && c >= 0 && c < 2 ? ch_[c].seq->status().state : SeqState::Idle;
            const bool seq_busy = ss == SeqState::Running || ss == SeqState::WaitingConfirm;
            r = {{"ok", true}};
            if (needs_ch && (c < 0 || c > 1)) r = fail("通道只能是 1 或 2");
            // 序列在跑的时候，手动的控温 / 搅拌 / 加料命令不收：两边抢着下命令，谁也说不清设备现在听谁的
            else if (!offline_reason.empty() && (cmd == "temp.start" || cmd == "temp.reflux" || cmd == "temp.profile" || cmd == "seq.start"))
                r = fail(offline_reason);
            else if (seq_busy && (cmd == "temp.start" || cmd == "temp.reflux" || cmd == "temp.profile" || cmd == "temp.setpoint" || cmd == "temp.stop" || cmd == "dose.start"))
                r = fail("这个通道正在跑序列，先结束序列再手动操作");
            else if (cmd == "temp.start") {
                const std::string m = q.value("mode", "reactor");
                const Target tg = target_from(q);
                begin_run(c, q.value("name", std::string("手动控温")), t);
                station_->start(c, m == "jacket" ? ControlMode::Jacket : ControlMode::Reactor, tg);
                note("开始控温，目标 " + std::to_string(tg.value));
            } else if (cmd == "temp.reflux") { begin_run(c, q.value("name", std::string("蒸回流")), t); station_->start_reflux(c, q.value("delta_k", 8.0)); note("开始蒸回流"); }
            else if (cmd == "temp.profile") {
                // 带了 segments 就按带来的走，并且记成这个通道的手动曲线；没带就用存着的那一条
                std::vector<ProfileSegment> segs = ch_[c].manual_profile;
                const bool given = q.contains("segments");
                if (given) { segs.clear(); for (auto& s : q["segments"]) segs.push_back({s.value("target", 0.0), s.value("minutes", 0.0)}); }
                if (segs.empty() || segs.size() > 12) r = fail("结晶曲线要 1 到 12 段");
                else {
                    if (given) { ch_[c].manual_profile = segs; recipes_.save_profile(c, segs); }
                    begin_run(c, q.value("name", std::string("结晶")), t); station_->start_profile(c, segs); note("开始结晶曲线");
                }
            }
            else if (cmd == "trend.get") {
                // 趋势曲线的数据：给出 after 之后的点（界面先要一次全的，以后每次只要新增的）。按列给，省字节
                const double after = q.value("after", -1.0);
                const size_t max = static_cast<size_t>(std::max(1, q.value("max", static_cast<int>(kTrendMax))));
                const auto& tr = ch_[c].trend;
                size_t i0 = 0;
                while (i0 < tr.size() && tr[i0].t <= after) i0++;
                if (tr.size() - i0 > max) i0 = tr.size() - max;
                for (const char* k : {"t", "wall", "tr", "tj", "tj_sp", "sp", "ph", "rpm", "on"}) r[k] = json::array();
                for (size_t i = i0; i < tr.size(); i++) {
                    const TrendPoint& p = tr[i];
                    r["t"].push_back(p.t); r["wall"].push_back(p.wall); r["tr"].push_back(num(p.tr)); r["tj"].push_back(num(p.tj)); r["tj_sp"].push_back(num(p.tj_sp));
                    r["sp"].push_back(num(p.sp)); r["ph"].push_back(num(p.ph)); r["rpm"].push_back(num(p.rpm)); r["on"].push_back(p.on ? 1 : 0);
                }
                r["marks"] = json::array();
                for (auto& m : ch_[c].marks) r["marks"].push_back({{"t", m.t}, {"wall", m.wall}, {"text", m.text}});
                r["now"] = t;
                r["run_start"] = ch_[c].rec->active() ? json(ch_[c].rec_t0) : json(nullptr);      // 这一炉是什么时候开始的（「实验时间」从这里算起）
            }
            else if (cmd == "profile.get") r["segments"] = profile_json(ch_[c].manual_profile);
            else if (cmd == "profile.set") {
                std::vector<ProfileSegment> segs;
                if (q.contains("segments") && q["segments"].is_array()) for (auto& s : q["segments"]) segs.push_back({s.value("target", 0.0), s.value("minutes", 0.0)});
                if (segs.size() > 12) r = fail("结晶曲线最多 12 段");
                else if (!recipes_.save_profile(c, segs)) r = fail("曲线没存进去");
                else ch_[c].manual_profile = segs;
            }
            else if (cmd == "temp.setpoint") { station_->set_setpoint(c, q.value("target", 0.0)); note("改目标为 " + std::to_string(q.value("target", 0.0))); }
            else if (cmd == "temp.stop") { note("停止控温"); station_->stop(c); }
            else if (cmd == "stir.set") { aux_->set_stir(c, q.value("rpm", 0.0)); note("搅拌设为 " + std::to_string(static_cast<int>(q.value("rpm", 0.0))) + " rpm"); }
            else if (cmd == "dose.start") {
                std::vector<Step> one;
                json st = {{"kind", "dose"}, {"dose", q.value("dose", json::object())}};
                if (!steps_from_json(json::array({st}).dump(), one)) r = fail("加料参数不对");
                else if (!aux_->start_dose(c, one[0].dose)) r = fail(aux_->snapshot().dose[c].note);
                else note("开始加料");
            }
            else if (cmd == "dose.stop") { aux_->stop_dose(c); note("停止加料"); }
            else if (cmd == "seq.get") r["steps"] = json::parse(steps_to_json(ch_[c].seq->steps()));
            else if (cmd == "seq.load") {
                std::vector<Step> steps;
                if (!q.contains("steps") || !steps_from_json(q["steps"].dump(), steps)) r = fail("步骤不对，或者超过了 6 步");
                else if (!ch_[c].seq->load(steps)) r = fail("序列已经开始，不能增改");
                else recipes_.save_channel(c, steps);
            }
            else if (cmd == "seq.start") {
                if (ch_[c].seq->steps().empty()) r = fail("序列是空的");
                else if (station_->snapshot().ch[c].running) r = fail("这个通道正在手动控温，先停止控温再开始序列");
                else { begin_run(c, q.value("name", std::string("未命名序列")), t); if (!ch_[c].seq->start(t)) r = fail("序列开始不了"); else note("开始序列"); }
            }
            else if (cmd == "seq.end") { note("结束序列"); ch_[c].seq->end(t); }
            else if (cmd == "seq.confirm") { ch_[c].seq->confirm(); note("确认继续"); }
            else if (cmd == "recipe.list") {
                r["recipes"] = json::array();
                for (auto& x : recipes_.list()) r["recipes"].push_back({{"name", x.name}, {"steps", x.steps}, {"est_minutes", x.est_minutes}, {"saved", x.saved}});
            }
            else if (cmd == "recipe.get") { std::vector<Step> steps; if (!recipes_.load(q.value("name", ""), steps)) r = fail("没有这个配方"); else r["steps"] = json::parse(steps_to_json(steps)); }
            else if (cmd == "recipe.save") { std::vector<Step> steps; if (!q.contains("steps") || !steps_from_json(q["steps"].dump(), steps) || !recipes_.save(q.value("name", ""), steps)) r = fail("存不了：名字是空的，或者步骤不对"); }
            else if (cmd == "recipe.delete") { if (!recipes_.remove(q.value("name", ""))) r = fail("没有这个配方"); }
            else if (cmd == "alarm.list") {
                r["alarms"] = json::array();
                for (auto& al : station_->alarms()) r["alarms"].push_back({{"id", al.id}, {"t", al.t}, {"ch", al.ch + 1}, {"title", al.title}, {"action", al.action}, {"acked", al.acked}});
            }
            else if (cmd == "alarm.ack") station_->ack_alarm(q.value("id", -1));
            else if (cmd == "limits.get") { const SafetyLimits L = station_->limits(c); r["limits"] = {{"tr_min", L.tr_min}, {"tr_max", L.tr_max}, {"tj_min", L.tj_min}, {"tj_max", L.tj_max}, {"rate_max_k_per_min", L.rate_max_k_per_min}, {"tc_max", L.tc_max}}; }
            else if (cmd == "limits.set") {
                if (seq_busy) r = fail("序列运行中不能改安全限值");
                else {
                    SafetyLimits L = station_->limits(c);
                    L.tr_min = q.value("tr_min", L.tr_min); L.tr_max = q.value("tr_max", L.tr_max); L.rate_max_k_per_min = std::max(0.0, q.value("rate_max_k_per_min", L.rate_max_k_per_min));
                    // 操作人只能在设备底线之内收紧：釜内范围不许超出 −40…180
                    const SafetyLimits base;
                    if (L.tr_min < base.tr_min || L.tr_max > base.tr_max || L.tr_min >= L.tr_max) r = fail("限值只能在设备底线（−40 … 180 ℃）之内，而且下限要小于上限");
                    else { station_->set_limits(c, L); settings_.limits[c] = L; settings_.save(dir_); }
                }
            }
            else if (cmd == "settings.get") {
                r["tcp"] = {{"enabled", settings_.tcp.enabled}, {"port", settings_.tcp.port}, {"timeout_s", settings_.tcp.timeout_s}, {"allow_remote_control", settings_.tcp.allow_remote_control}};
                r["fast_boost_max"] = settings_.fast_boost_max;
                r["devices"] = json::array();
                for (auto& d : settings_.devices) r["devices"].push_back({{"key", d.key}, {"label", d.label}, {"enabled", d.enabled}, {"port", d.port}, {"usb_id", d.usb_id}, {"usb_name", d.usb_name}, {"baud", d.baud}, {"parity", std::string(1, d.parity)}, {"stop_bits", d.stop_bits}, {"station", d.station}, {"poll_ms", d.poll_ms}});
            }
            else if (cmd == "settings.set") {
                if (remote) r = fail("通信和系统设置只能在触摸屏上改");     // PC 把触摸屏的端口改掉，自己就再也连不上了
                else {
                    if (q.contains("tcp")) { const json& tj = q["tcp"]; TcpSettings& T = settings_.tcp; T.enabled = tj.value("enabled", T.enabled); T.port = tj.value("port", T.port); T.timeout_s = tj.value("timeout_s", T.timeout_s); T.allow_remote_control = tj.value("allow_remote_control", T.allow_remote_control); }
                    if (q.contains("fast_boost_max")) settings_.fast_boost_max = std::max(0.0, q.value("fast_boost_max", settings_.fast_boost_max));
                    if (q.contains("device")) {
                        const json& dj = q["device"]; bool found = false;
                        for (auto& d : settings_.devices) if (d.key == dj.value("key", "")) {
                            found = true;
                            d.enabled = dj.value("enabled", d.enabled); d.port = dj.value("port", d.port); d.baud = dj.value("baud", d.baud);
                            const std::string par = dj.value("parity", std::string(1, d.parity)); if (!par.empty()) d.parity = par[0];
                            d.stop_bits = dj.value("stop_bits", d.stop_bits); d.station = dj.value("station", d.station); d.poll_ms = dj.value("poll_ms", d.poll_ms);
                        }
                        if (!found) r = fail("没有这台设备");
                    }
                    if (r.value("ok", false) && !settings_.save(dir_)) r = fail("设置存不了盘");
                    else if (r.value("ok", false)) r["restart_needed"] = q.contains("device") || (q.contains("tcp") && q["tcp"].contains("port"));
                }
            }
            // ── 自动检测：这台设备接在哪个串口 ──
            // 平时根本用不到它：设备配置里记了 usb_id，开机就按 USB 口反查出今天的 COM 号（resolve_device_ports）。
            // 线被挪到别的插口、或者第一次装机还没标定过，才来这里真的问一遍。
            else if (cmd == "ports.list") {
                r["ports"] = json::array();
                for (const SerialPortInfo& p : list_serial_ports())
                    r["ports"].push_back({{"port", p.port}, {"name", p.name}, {"usb_id", p.usb_id}, {"slot", p.slot}});
            }
            else if (cmd == "ports.scan") {
                std::lock_guard<std::mutex> lk(scan_.mu);
                if (remote) r = fail("自动检测只能在触摸屏上做");
                else if (scan_.running) r = fail("正在检测，等它扫完");
                else if (!open_serial) r = fail("这台机器上没有真串口可扫（仿真模式）");
                else if (station_->snapshot().ch[0].running || station_->snapshot().ch[1].running) r = fail("有通道正在控制中。检测要占住串口、会打断轮询，先把两个通道都停下来再检测");
                else {
                    if (scan_.th.joinable()) scan_.th.join();
                    scan_.running = true; scan_.want_stop = false; scan_.has_result = false;
                    scan_.done = 0; scan_.total = 0; scan_.now_port.clear(); scan_.hits.clear();
                    scan_.started = t;
                    ScanOptions opt;
                    if (q.contains("timeout_ms")) opt.timeout_ms = std::max(80, std::min(2000, q.value("timeout_ms", 300)));
                    scan_.th = std::thread([this, opt]() {
                        std::vector<ScanHit> hits;
                        try {
                            hits = scan_serial_ports(opt, open_serial, [this](int done, int total, const ScanHit& last) {
                                std::lock_guard<std::mutex> g(scan_.mu);
                                scan_.done = done; scan_.total = total; scan_.now_port = last.port;
                                scan_.hits.push_back(last);
                            });
                        } catch (const std::exception&) {}
                        std::lock_guard<std::mutex> g(scan_.mu);
                        if (!hits.empty()) scan_.hits = hits;
                        scan_.running = false; scan_.has_result = true;
                    });
                }
            }
            else if (cmd == "ports.scan.status") {
                std::lock_guard<std::mutex> lk(scan_.mu);
                r["running"] = scan_.running;
                r["done"] = scan_.done; r["total"] = scan_.total; r["port"] = scan_.now_port;
                r["elapsed_s"] = scan_.running || scan_.has_result ? std::max(0.0, t - scan_.started) : 0.0;
                r["hits"] = json::array();
                for (const ScanHit& h : scan_.hits)
                    r["hits"].push_back({{"port", h.port}, {"usb_id", h.usb_id}, {"usb_name", h.usb_name}, {"slot", h.slot},
                                         {"type", h.type}, {"type_label", scan_type_label(h.type)}, {"model", h.model},
                                         {"baud", h.baud}, {"station", h.station}, {"problem", h.problem}});
            }
            else if (cmd == "ports.apply") {
                // 人在检测结果那一屏点了「应用」：把选中的几行写进设备配置。
                // 一起记下 usb_id——这才是重点，记住了以后 COM 号再变就不用再来检测一次了。
                if (remote) r = fail("自动检测只能在触摸屏上做");
                else if (!q.contains("devices") || !q["devices"].is_array()) r = fail("没给要改哪几台");
                else {
                    int n = 0;
                    std::string missing;
                    for (const json& dj : q["devices"]) {
                        const std::string key = dj.value("key", "");
                        DeviceComm* d = nullptr;
                        for (auto& x : settings_.devices) if (x.key == key) d = &x;
                        if (!d) { if (!missing.empty()) missing += "、"; missing += key; continue; }
                        d->port = dj.value("port", d->port);
                        d->usb_id = dj.value("usb_id", d->usb_id);
                        d->usb_name = dj.value("usb_name", d->usb_name);
                        d->baud = dj.value("baud", d->baud);
                        d->station = dj.value("station", d->station);
                        n++;
                    }
                    if (!missing.empty()) r = fail("设置里没有这些设备：" + missing);
                    else if (!settings_.save(dir_)) r = fail("设置存不了盘");
                    else { r["applied"] = n; r["restart_needed"] = n > 0; }
                }
            }
            else if (cmd == "lamp.set") ch_[c].lamp = q.value("on", !ch_[c].lamp);      // 只记状态，不动任何输出
            else if (cmd == "mark.add") {
                // 时间标记：趋势图上竖一道线；这一炉在跑的话同时记进它的事件日志
                Chan::Mark m{t, static_cast<long long>(std::time(nullptr)), q.value("text", std::string("标记"))};
                ch_[c].marks.push_back(m);
                if (ch_[c].marks.size() > 200) ch_[c].marks.pop_front();
                if (ch_[c].rec->active()) ch_[c].rec->event(t - ch_[c].rec_t0, "操作", "时间标记：" + m.text);
                r["count"] = ch_[c].marks.size();
            }
            else if (cmd == "trend.snapshot") {
                // 拍快照：把趋势图现在手上这段数据（最近 3 小时）原样存成一个 CSV，放进导出目录的「快照」文件夹
                const std::time_t w = std::time(nullptr); std::tm tm{};
#ifdef _WIN32
                localtime_s(&tm, &w);
#else
                localtime_r(&w, &tm);
#endif
                char stamp[32]; std::strftime(stamp, sizeof stamp, "%Y%m%d-%H%M%S", &tm);
                const std::filesystem::path dir = std::filesystem::u8path(dir_) / "Exports" / std::filesystem::u8path("快照");
                std::error_code ec;
                std::filesystem::create_directories(dir, ec);
                const std::string name = std::string(stamp) + "-通道" + std::to_string(c + 1) + ".csv";
                std::ofstream o(dir / std::filesystem::u8path(name), std::ios::binary);
                if (!o) r = fail("快照存不了（导出目录写不进去）");
                else {
                    o << "\xEF\xBB\xBF" << "时刻,釜内Tr(℃),夹套Tj(℃),夹套目标(℃),当前目标(℃),pH,搅拌(rpm),在控温\n";
                    for (auto& p : ch_[c].trend) {
                        const std::time_t pw = static_cast<std::time_t>(p.wall); std::tm ptm{};
#ifdef _WIN32
                        localtime_s(&ptm, &pw);
#else
                        localtime_r(&pw, &ptm);
#endif
                        char ts[32]; std::strftime(ts, sizeof ts, "%Y-%m-%d %H:%M:%S", &ptm);
                        auto cell = [&](double v) { if (std::isfinite(v)) o << v; };
                        o << ts << ','; cell(p.tr); o << ','; cell(p.tj); o << ','; cell(p.tj_sp); o << ','; cell(p.sp); o << ','; cell(p.ph); o << ','; cell(p.rpm); o << ',' << (p.on ? 1 : 0) << "\n";
                    }
                    r["file"] = name; r["rows"] = ch_[c].trend.size();
                }
            }
            else if (cmd == "drives.list") { r["drives"] = json::array(); for (auto& d : removable_drives()) r["drives"].push_back(d); }
            else if (cmd == "export.copy") {
                // 把导出目录整个拷到 U 盘（或者命令里给的目录）下的「TEC导出」文件夹里
                std::string dest = q.value("dest", std::string());
                if (dest.empty()) { const auto ds = removable_drives(); if (!ds.empty()) dest = ds.front(); }
                if (dest.empty()) r = fail("没找到 U 盘：插上以后再点一次");
                else {
                    std::string why;
                    const std::string to = (std::filesystem::u8path(dest) / std::filesystem::u8path("TEC导出")).u8string();
                    const int n = copy_tree(dir_ + "/Exports", to, &why);
                    if (n < 0) r = fail(why); else { r["files"] = n; r["to"] = to; }
                }
            }
            else if (cmd == "sys.info") {
                // 系统设置页要看的：版本、开了多久、数据放哪、盘还剩多少、存了多少炉和配方
                json extra = json::object();
                if (comm_info) { const json x = json::parse(comm_info(), nullptr, false); if (x.is_object()) extra = x; }
                r["version"] = kVersion;
                r["uptime_s"] = std::chrono::duration<double>(std::chrono::steady_clock::now() - started_).count();
                r["sim"] = extra.value("sim", false);
                r["data_dir"] = dir_;
                r["gains_path"] = gain_path_;
                std::error_code ec;
                const auto sp = std::filesystem::space(std::filesystem::u8path(dir_), ec);
                r["disk_free_gb"] = ec ? json(nullptr) : json(static_cast<double>(sp.available) / 1e9);
                r["disk_total_gb"] = ec ? json(nullptr) : json(static_cast<double>(sp.capacity) / 1e9);
                r["runs"] = RunRecorder::list(dir_).size();
                r["recipes"] = recipes_.list().size();
                r["gain_rows"] = table_.rows().size();
            }
            else if (cmd == "sys.exit") {
                // 退出控制核心。触摸屏上「退出程序 → 连控制核心一起退」下的就是这条。
                // 只收本机的：PC 要是能把核心关掉，现场没人知道炉子为什么不控了。
                // 两个通道都停着才收——真正的停机动作在主程序的退出那一段（停序列、停控温、再跑两拍让「停」写到设备上），
                // 这里只负责把主循环叫停，不自己去动设备。
                const StationSnapshot snap = station_->snapshot();
                bool busy = snap.ch[0].running || snap.ch[1].running;
                for (int k = 0; k < 2; k++) { const SeqState q2 = ch_[k].seq->status().state; busy = busy || q2 == SeqState::Running || q2 == SeqState::WaitingConfirm; }
                if (remote) r = fail("只有触摸屏上能退出控制核心");
                else if (busy) r = fail("有通道正在控温或跑序列，停下来才能退出");
                else if (!on_exit) r = fail("这个程序不支持退出");
                else on_exit();
            }
            else if (cmd == "comm.status") {
                // 各设备的通信参数 + 开机时连没连上；连着的 PC；本机地址。后面三样由主程序提供，没接（测试里）就只有参数
                json extra = json::object();
                if (comm_info) { const json x = json::parse(comm_info(), nullptr, false); if (x.is_object()) extra = x; }
                r["sim"] = extra.value("sim", false);
                r["address"] = extra.value("address", std::string());
                r["clients"] = extra.contains("clients") ? extra["clients"] : json::array();
                r["devices"] = json::array();
                for (auto& d : settings_.devices) {
                    json one = {{"key", d.key}, {"label", d.label}, {"enabled", d.enabled}, {"port", d.port}, {"usb_id", d.usb_id}, {"usb_name", d.usb_name}, {"baud", d.baud}, {"parity", std::string(1, d.parity)}, {"stop_bits", d.stop_bits}, {"station", d.station}, {"poll_ms", d.poll_ms},
                                {"state", !d.enabled ? "off" : r["sim"].get<bool>() ? "sim" : "unknown"}, {"problem", ""}};
                    if (extra.contains("status")) for (auto& s2 : extra["status"]) if (s2.value("key", "") == d.key && d.enabled) { one["state"] = s2.value("ok", false) ? "ok" : "fail"; one["problem"] = s2.value("problem", ""); }
                    r["devices"].push_back(one);
                }
            }
            else if (cmd == "runs.list") {
                r["runs"] = json::array();
                const double now_t = station_->snapshot().t;
                for (auto& x : RunRecorder::list(dir_)) {
                    // 还在跑的那一炉，记录文件里还没有总时长：按「现在 − 这一炉开始」现算
                    double dur = x.duration_s;
                    const int rc = x.channel - 1;
                    if (x.running && rc >= 0 && rc < 2 && ch_[rc].rec->active()) dur = std::max(0.0, now_t - ch_[rc].rec_t0);
                    r["runs"].push_back({{"id", x.id}, {"name", x.name}, {"channel", x.channel}, {"running", x.running}, {"duration_s", dur}, {"started", x.started}});
                }
            }
            else if (cmd == "runs.export") {
                std::vector<std::string> ids;
                if (q.contains("ids")) for (auto& x : q["ids"]) ids.push_back(x.get<std::string>());
                const std::string out = q.value("out_dir", dir_ + "/Exports");
                // from_min / to_min：只导每一炉里这一段（从那一炉开始算的分钟）；不给就是整炉
                const double from_s = q.value("from_min", 0.0) * 60.0, to_s = q.contains("to_min") ? q.value("to_min", 0.0) * 60.0 : -1.0;
                if (to_s >= 0 && to_s <= from_s) { r = fail("导出范围不对：结束要比开始晚"); }
                else r["exported"] = RunRecorder::export_runs(dir_, ids, q.value("interval_s", 10), out, from_s, to_s);
                r["out_dir"] = out;
            }
            else if (cmd == "gains.get") {
                r["rows"] = json::array();
                for (auto& row : table_.rows()) r["rows"].push_back({{"temp", row.temp}, {"kp", row.kp}, {"ki", row.ki}, {"kd", row.kd}, {"steady_bias", opt(row.steady_bias)}, {"heat_ratio", opt(row.heat_ratio)}});
            }
            else if (cmd == "gains.set" || cmd == "gains.reload") {
                // 换增益表：两个通道都没在控温、也没在跑序列时才收。控着温的时候把 PID 参数整个换掉，输出会跳
                const StationSnapshot snap = station_->snapshot();
                bool busy = snap.ch[0].running || snap.ch[1].running;
                for (int k = 0; k < 2; k++) { const SeqState q2 = ch_[k].seq->status().state; busy = busy || q2 == SeqState::Running || q2 == SeqState::WaitingConfirm; }
                if (busy) r = fail("有通道正在控温或跑序列，停下来才能换增益表");
                else if (cmd == "gains.reload") {
                    GainTable t2 = GainTable::load(gain_path_);
                    if (t2.empty()) r = fail("盘上的增益表读不出来");
                    else station_->locked([&] { table_ = t2; });
                } else {
                    std::vector<GainRow> rows;
                    std::string why;
                    if (!q.contains("rows") || !q["rows"].is_array() || q["rows"].empty() || q["rows"].size() > 16) why = "增益表要 1 到 16 行";
                    else for (auto& x : q["rows"]) {
                        GainRow g2;
                        g2.temp = x.value("temp", 0.0); g2.kp = x.value("kp", 0.0); g2.ki = x.value("ki", 0.0); g2.kd = x.value("kd", 0.0);
                        if (x.contains("steady_bias") && x["steady_bias"].is_number()) g2.steady_bias = x["steady_bias"].get<double>();
                        if (x.contains("heat_ratio") && x["heat_ratio"].is_number()) g2.heat_ratio = x["heat_ratio"].get<double>();
                        if (g2.temp < -60 || g2.temp > 250) why = "温度要在 −60 … 250 ℃ 之间";
                        else if (!(g2.kp > 0) || g2.ki < 0 || g2.kd < 0) why = "Kp 要大于 0，Ki、Kd 不能是负的";
                        else if (g2.heat_ratio && !(*g2.heat_ratio > 0)) why = "加热比要大于 0";
                        for (auto& o : rows) if (std::fabs(o.temp - g2.temp) < 0.05) why = "有两行是同一个温度";
                        rows.push_back(g2);
                    }
                    if (!why.empty()) r = fail(why);
                    else {
                        GainTable t2(rows);
                        if (!t2.save(gain_path_)) r = fail("增益表没存进去（文件写不了）");
                        else station_->locked([&] { table_ = t2; });
                    }
                }
            }
            else r = fail("不认识的命令：" + cmd);
        }
    } catch (const std::exception& e) {
        r = fail(std::string("处理命令时出错：") + e.what());
    }
    if (q.contains("id")) r["id"] = q["id"];
    return r.dump();
}

}  // namespace tec
