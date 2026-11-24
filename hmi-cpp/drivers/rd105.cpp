#include "rd105.hpp"
#include <algorithm>
#include <cctype>
#include <cerrno>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <limits>
#include <map>
#include <thread>

namespace tec {

namespace {

long long now_ms() {
    return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
}
void sleep_ms(long long ms) { if (ms > 0) std::this_thread::sleep_for(std::chrono::milliseconds(ms)); }

std::string upper(std::string s) {
    for (char& c : s) c = static_cast<char>(std::toupper(static_cast<unsigned char>(c)));
    return s;
}
std::string trim(const std::string& s) {
    size_t a = 0, b = s.size();
    while (a < b && std::isspace(static_cast<unsigned char>(s[a]))) a++;
    while (b > a && std::isspace(static_cast<unsigned char>(s[b - 1]))) b--;
    return s.substr(a, b - a);
}
std::string hex4(uint16_t v) { char b[8]; std::snprintf(b, sizeof b, "0x%04X", v); return b; }

// C# 的 Math.Round 是「四舍六入五成双」，nearbyint 在缺省舍入方式下也是；不用 llround（它是五入）
long long round_even(double v, const char* what) {
    if (!std::isfinite(v)) throw std::invalid_argument(std::string(what) + " 不是一个数，不往设备里写");
    return static_cast<long long>(std::nearbyint(v));
}

void check_channel(int channel, bool allow_general) {
    if (channel == 1 || channel == 2 || (allow_general && channel == 0)) return;
    throw std::invalid_argument("RD105 通道号只能是 1 或 2");
}

// ── ASCII 的拼帧和解析（照 C# TecAscii）───────────────────────────────────────

std::string field_key(int channel, const std::string& name) {
    return channel == 0 ? name : "TC" + std::to_string(channel) + ":" + name;
}
std::string printable(const std::string& s) {
    std::string o;
    for (char c : s) { if (c == '\r') o += "\\r"; else if (c == '\n') o += "\\n"; else o += c; }
    return o;
}

// 应答是若干「名=值@」拼起来的，片段前面可能带 OK，末尾 \r\n；协议文档的示例里偶尔是全角冒号，一并认。
// 值不一定是数字：实测型号 TEC 回的是 "215L"。键统一成大写。
std::map<std::string, std::string> parse_fields_text(const std::string& reply) {
    std::string norm;
    for (size_t i = 0; i < reply.size(); i++) {
        if (reply.compare(i, 3, "\xEF\xBC\x9A") == 0) { norm += ':'; i += 2; continue; }   // 全角冒号（UTF-8）
        if (reply[i] == '\r' || reply[i] == '\n') continue;
        norm += reply[i];
    }
    std::map<std::string, std::string> out;
    size_t pos = 0;
    while (pos <= norm.size()) {
        size_t at = norm.find('@', pos);
        if (at == std::string::npos) at = norm.size();
        std::string token = trim(norm.substr(pos, at - pos));
        pos = at + 1;
        while (token.size() >= 2 && upper(token.substr(0, 2)) == "OK") token = token.substr(2);
        const size_t eq = token.find('=');
        if (eq == std::string::npos || eq == 0) continue;
        const std::string key = upper(trim(token.substr(0, eq)));
        const std::string value = trim(token.substr(eq + 1));
        if (!value.empty() && value != "?") out[key] = value;
    }
    return out;
}

bool parse_int(const std::string& text, long long* out) {
    if (text.empty()) return false;
    size_t i = (text[0] == '-' || text[0] == '+') ? 1 : 0;
    if (i == text.size()) return false;
    for (size_t k = i; k < text.size(); k++) if (!std::isdigit(static_cast<unsigned char>(text[k]))) return false;
    errno = 0;
    *out = std::strtoll(text.c_str(), nullptr, 10);
    return errno == 0;
}

// 取一个字段的文本；没有就是坏帧（现场：'@' 被打成 'P'，两个字段粘成一个不是数字的东西）
std::string get_text_field(const std::string& reply, int channel, const std::string& name) {
    const auto fields = parse_fields_text(reply);
    const auto it = fields.find(upper(field_key(channel, name)));
    if (it == fields.end()) throw Rd105Error("应答里没有字段 " + field_key(channel, name) + "，原始应答：" + printable(reply));
    return it->second;
}
long long get_field(const std::string& reply, int channel, const std::string& name) {
    long long v = 0;
    const auto fields = parse_fields_text(reply);
    const auto it = fields.find(upper(field_key(channel, name)));
    if (it == fields.end() || !parse_int(it->second, &v))
        throw Rd105Error("应答里没有字段 " + field_key(channel, name) + "，原始应答：" + printable(reply));
    return v;
}

}  // namespace

// ── 量纲 ────────────────────────────────────────────────────────────────────

namespace rd105 {

double temp_from_raw(long long raw) { return raw == kSensorNotConnectedRaw ? std::numeric_limits<double>::quiet_NaN() : raw / 1e5; }
long long temp_to_raw(double celsius) { return round_even(celsius * 1e5, "温度"); }
double ohms_from_raw(long long raw) { return raw / 1e6; }
double volts_from_raw(long long raw) { return raw / 1e8; }
double duty_percent_from_raw(long long raw) { return raw / 20000.0; }
long long duty_percent_to_raw(double percent) { return round_even(percent * 20000.0, "占空比"); }
double speed_from_raw(long long raw) { return raw / 1000.0; }
long long speed_to_raw(double deg_per_sec) { return round_even(deg_per_sec * 1000.0, "变温速率"); }
double current_from_raw(long long raw) { return raw / 1000.0; }
double max_current_from_raw(long long raw) { return raw / 10.0; }
long long max_current_to_raw(double amps) { return round_even(amps * 10.0, "最大电流"); }
double start_voltage_percent_from_raw(long long raw) { return raw / 200.0; }

std::vector<std::string> describe_faults(uint16_t code) {
    static const std::pair<uint16_t, const char*> table[] = {
        {kHighTempLimiting, "温控器自身高温，正在限制输出"},
        {kOverTempShutdown, "温控器过温，已停止输出"},
        {kUnderVoltage, "供电电压过低（<7V）"},
        {kOverVoltage, "供电电压过高（>30V）"},
        {kCh1SensorOutOfRange, "通道1 传感器温度越限"},
        {kCh1CurrentLimiting, "通道1 输出限流中"},
        {kCh2SensorOutOfRange, "通道2 传感器温度越限"},
        {kCh2CurrentLimiting, "通道2 输出限流中"},
    };
    std::vector<std::string> out;
    for (const auto& e : table) if (code & e.first) out.push_back(e.second);
    return out;
}
uint16_t sensor_fault_bit(int channel) { check_channel(channel, false); return channel == 1 ? kCh1SensorOutOfRange : kCh2SensorOutOfRange; }
uint16_t current_limit_bit(int channel) { check_channel(channel, false); return channel == 1 ? kCh1CurrentLimiting : kCh2CurrentLimiting; }

std::string model_name(long long code) {
    static const char* names[] = {"103", "207L", "207", "215L", "215", "215Pro", "107L", "107", "115L", "115", "115Pro",
                                  "100L", "100", "100Pro", "403L", "403", "403Pro", "415L", "415", "603L", "603", "615L",
                                  "615", "615Pro", "803L", "803", "815L", "815", "815Pro", "203L", "203"};
    return code >= 1 && code <= 31 ? names[code - 1] : std::to_string(code);
}
std::string firmware_text(long long f) {
    return "v" + std::to_string(f / 100) + "." + std::to_string(f / 10 % 10) + "." + std::to_string(f % 10);
}

const RegSpec* find_reg(const std::string& name) {
    static const RegSpec table[] = {
        // 通道量（TCn:）
        {"TG", 0x1000, RegType::I32, true},
        {"TCADJTEMP", 0x1002, RegType::I32, true},
        {"RESISTOR", 0x1004, RegType::U64, true},
        {"ENABLE", 0x1100, RegType::U16, true},
        {"MODE", 0x1101, RegType::U16, true},
        {"PIDPOL", 0x1102, RegType::U16, true},
        {"PWMDUTY", 0x1103, RegType::I64, true},
        {"AUTOPID", 0x1107, RegType::U16, true},
        {"SPEED", 0x1108, RegType::U16, true},
        {"FDEADV", 0x110A, RegType::U16, true},
        {"BDEADV", 0x110B, RegType::U16, true},
        {"ONSENSOR", 0x110C, RegType::I16, true},
        {"LIMITED", 0x110E, RegType::I16, true},
        {"STARTUPDELAY", 0x110F, RegType::U16, true},
        {"POWERMODE", 0x1110, RegType::U16, true},
        {"CURRENT", 0x1111, RegType::U16, true},
        {"SETCURRENT", 0x1112, RegType::U16, true},
        {"KP", 0x1200, RegType::U32, true},
        {"KI", 0x1202, RegType::U32, true},
        {"KD", 0x1204, RegType::U32, true},
        {"POLYOMIAL", 0x1300, RegType::U16, true},      // 拼写照协议原文（少个 N），别顺手改对
        {"OVERTEMPUP", 0x133D, RegType::I32, true},
        {"OVERTEMPLOWER", 0x133F, RegType::I32, true},
        // 通用量
        {"RESET", 0x0000, RegType::U16, false},
        {"TEC", 0x0001, RegType::U16, false},
        {"ADDRESS", 0x0002, RegType::U16, false},
        {"SINTERIORTEMP", 0x0003, RegType::I16, false},
        {"CONTMODE", 0x0004, RegType::I16, false},
        {"ERRORCODE", 0x0007, RegType::U16, false},
        {"BOUNDTABLEONE", 0x0008, RegType::U16, false},
        {"BOUNDTABLETWO", 0x0009, RegType::U16, false},
        {"OVERTVPT", 0x000A, RegType::U16, false},
        {"OVERTTEMP", 0x000B, RegType::U16, false},
        {"FPV", 0x000C, RegType::U16, false},
        {"FPWM", 0x000D, RegType::U16, false},
    };
    const std::string u = upper(name);
    for (const auto& r : table) if (u == r.name) return &r;
    return nullptr;
}

int reg_count(RegType type) {
    switch (type) {
        case RegType::U16: case RegType::I16: return 1;
        case RegType::U32: case RegType::I32: return 2;
        default: return 4;
    }
}

long long decode_regs(RegType type, const std::vector<uint16_t>& regs) {
    uint64_t acc = 0;
    for (uint16_t r : regs) acc = acc << 16 | r;
    switch (type) {
        case RegType::U16: return static_cast<uint16_t>(acc);
        case RegType::I16: return static_cast<int16_t>(static_cast<uint16_t>(acc));
        case RegType::U32: return static_cast<uint32_t>(acc);
        case RegType::I32: return static_cast<int32_t>(static_cast<uint32_t>(acc));
        case RegType::I64: return static_cast<int64_t>(acc);
        default:
            // uint64 的范围（电阻最大 5e11）long long 装得下；真溢出说明表读错了，如实报
            if (acc > static_cast<uint64_t>(std::numeric_limits<int64_t>::max())) throw Rd105Error("寄存器里的 uint64 值超出可表示范围");
            return static_cast<int64_t>(acc);
    }
}

std::vector<uint16_t> encode_regs(RegType type, long long value) {
    const int n = reg_count(type);
    uint64_t raw = static_cast<uint64_t>(value);     // 负数按补码
    std::vector<uint16_t> regs(n);
    for (int i = n - 1; i >= 0; i--) { regs[i] = static_cast<uint16_t>(raw & 0xFFFF); raw >>= 16; }
    return regs;
}

}  // namespace rd105

// ── 两种链路 ────────────────────────────────────────────────────────────────

Rd105::Rd105(ModbusRtu& modbus) : mb_(&modbus) {}
Rd105::Rd105(SerialTransport& ascii_port) : ascii_(&ascii_port) {}

// Modbus：查表得地址。表里没有的不猜；通道量 / 通用量用错了也当场说
const rd105::RegSpec& Rd105::mb_spec(int channel, const std::string& name, uint16_t* addr) const {
    const rd105::RegSpec* spec = rd105::find_reg(name);
    if (!spec) throw Rd105Error("RD105 走 Modbus-RTU 不支持 " + name + "：协议 §4 的寄存器表里没有它");
    if (spec->per_channel != (channel != 0))
        throw Rd105Error(spec->per_channel ? name + " 是通道量，要指明通道 1 / 2" : name + " 是通用量，不分通道");
    *addr = static_cast<uint16_t>(spec->address + (channel == 2 ? 0x1000 : 0));
    return *spec;
}

std::vector<uint16_t> Rd105::mb_read(uint16_t addr, int count) {
    try { return mb_->read_holding_registers(addr, count); }
    catch (const ModbusError& e) {
        throw Rd105Error("RD105 Modbus-RTU 站号 " + std::to_string(mb_->station()) + " 读 " + hex4(addr) + "：" + e.what(), e.timeout, e.code);
    }
}

// 一律用 0x10（写多个），单字的量也是：C# 桥上只用 03 / 10 这两个功能码，现场验过的只有这一种
void Rd105::mb_write(uint16_t addr, const std::vector<uint16_t>& regs) {
    try { mb_->write_registers(addr, regs); }
    catch (const ModbusError& e) {
        throw Rd105Error("RD105 Modbus-RTU 站号 " + std::to_string(mb_->station()) + " 写 " + hex4(addr) + "：" + e.what(), e.timeout, e.code);
    }
}

// ASCII：发一条、收一帧。没应答按次数重发（重发前照样留指令间隔、清缓冲，迟到的旧应答串不进来）
std::string Rd105::ascii_exec(const std::string& command) {
    for (int attempt = 0;; attempt++) {
        sleep_ms(min_command_gap_ms - (now_ms() - last_command_end_));
        ascii_->discard_input();
        ascii_->write(reinterpret_cast<const uint8_t*>(command.data()), command.size());
        std::string rx;
        uint8_t buf[512];
        const long long deadline = now_ms() + reply_timeout_ms;
        while (now_ms() < deadline) {
            const int n = ascii_->read(buf, sizeof buf, inter_byte_idle_ms);
            if (n > 0) {
                rx.append(reinterpret_cast<const char*>(buf), static_cast<size_t>(n));
                if (rx.size() >= 2 && rx[rx.size() - 2] == '\r' && rx.back() == '\n') break;   // 正常应答以 \r\n 结尾
            } else if (!rx.empty()) {
                break;              // 已经收到东西、线上又静了这么久：一帧完了
            } else {
                sleep_ms(1);        // 假串口的 read 立刻返回 0；真串口自己会等
            }
        }
        last_command_end_ = now_ms();
        if (!rx.empty()) return rx;
        if (attempt >= timeout_retries) throw Rd105Error("RD105 指令无应答：" + printable(command), true);
    }
}

// 发 + 解析；解析不了（坏帧）重发。所有「发了要看应答内容」的都走这里，坏帧和丢帧一个待遇
void Rd105::ascii_exec_parsed(const std::string& command, const std::function<void(const std::string&)>& parse) {
    for (int attempt = 0;; attempt++) {
        const std::string reply = ascii_exec(command);
        try { parse(reply); return; }
        catch (const Rd105Error&) { if (attempt >= corrupt_retries) throw; }
    }
}

// ── 按量名读写 ──────────────────────────────────────────────────────────────

long long Rd105::query(int channel, const std::string& name) {
    std::lock_guard<std::recursive_mutex> g(mu_);
    check_channel(channel, true);
    if (mb_) {
        uint16_t addr = 0;
        const auto& spec = mb_spec(channel, name, &addr);
        return rd105::decode_regs(spec.type, mb_read(addr, rd105::reg_count(spec.type)));
    }
    long long v = 0;
    ascii_exec_parsed(field_key(channel, name) + "=?@\n", [&](const std::string& r) { v = get_field(r, channel, name); });
    return v;
}

long long Rd105::set_echo(int channel, const std::string& name, long long value) {
    std::lock_guard<std::recursive_mutex> g(mu_);
    check_channel(channel, true);
    if (mb_) {
        uint16_t addr = 0;
        const auto& spec = mb_spec(channel, name, &addr);
        mb_write(addr, rd105::encode_regs(spec.type, value));
        // RESET 只写不读（回读会被拒），照下发的值回；别的回读设备里实际存下的
        if (upper(name) == "RESET") return value;
        return rd105::decode_regs(spec.type, mb_read(addr, rd105::reg_count(spec.type)));
    }
    long long v = 0;
    ascii_exec_parsed(field_key(channel, name) + "=" + std::to_string(value) + "@\n",
                      [&](const std::string& r) { v = get_field(r, channel, name); });
    return v;
}

void Rd105::set(int channel, const std::string& name, long long value) {
    std::lock_guard<std::recursive_mutex> g(mu_);
    long long echoed = set_echo(channel, name, value);
    if (echoed == value) return;
    echoed = set_echo(channel, name, value);
    if (echoed != value)
        throw Rd105Error("设置 " + field_key(channel, name) + " 失败：写入 " + std::to_string(value) + "，设备回读 " + std::to_string(echoed));
}

// 读一个可能是文本的量（ASCII 口的型号回 "215L"）。返回值是数字（解析得了的话），text 里是原文
long long Rd105::query_text_or_number(int channel, const std::string& name, std::string* text) {
    if (mb_) { const long long v = query(channel, name); *text = std::to_string(v); return v; }
    ascii_exec_parsed(field_key(channel, name) + "=?@\n", [&](const std::string& r) { *text = get_text_field(r, channel, name); });
    long long v = 0;
    return parse_int(*text, &v) ? v : -1;
}

// ── 每一拍读的 ──────────────────────────────────────────────────────────────

Rd105Snapshot Rd105::read_snapshot() {
    std::lock_guard<std::recursive_mutex> g(mu_);
    const double nan = std::numeric_limits<double>::quiet_NaN();
    Rd105Snapshot s{{nan, nan}, nan, {nan, nan}, {nan, nan}};
    if (mb_) {
        // Modbus 没有 DATADEMAND 这条聚合指令，一个一个读。只读控温要的三个量：
        // RESISTOR 上层没人用，而且现场那台固件读它永远答坏帧（报 8 字节数据、只来 7 个就停：01 03 08 00 00 00 00 06 9C 83），
        // 每拍白耗几百 ms——这里干脆不问（要看电阻用 query(ch, "RESISTOR")）
        s.temp_c[0] = rd105::temp_from_raw(query(1, "TCADJTEMP"));
        s.temp_c[1] = rd105::temp_from_raw(query(2, "TCADJTEMP"));
        s.internal_c = static_cast<double>(query(0, "SINTERIORTEMP"));
        return s;
    }
    ascii_exec_parsed("DATADEMAND=2@\n", [&](const std::string& reply) {
        const auto f = parse_fields_text(reply);
        auto need = [&](const char* key) {
            long long v = 0;
            const auto it = f.find(key);
            if (it == f.end() || !parse_int(it->second, &v)) throw Rd105Error(std::string("DATADEMAND 应答缺少字段 ") + key + "：" + printable(reply));
            return v;
        };
        auto opt = [&](const char* key, double (*conv)(long long)) {
            long long v = 0;
            const auto it = f.find(key);
            return it != f.end() && parse_int(it->second, &v) ? conv(v) : nan;
        };
        s.temp_c[0] = rd105::temp_from_raw(need("TC1:TCADJTEMP"));
        s.temp_c[1] = rd105::temp_from_raw(need("TC2:TCADJTEMP"));
        s.internal_c = static_cast<double>(need("SINTERIORTEMP"));
        s.resistor_ohms[0] = opt("TC1:RESISTOR", rd105::ohms_from_raw);
        s.resistor_ohms[1] = opt("TC2:RESISTOR", rd105::ohms_from_raw);
        s.out_volts[0] = opt("TC1:OUTV", rd105::volts_from_raw);
        s.out_volts[1] = opt("TC2:OUTV", rd105::volts_from_raw);
    });
    return s;
}

double Rd105::read_temperature(int channel) { check_channel(channel, false); return rd105::temp_from_raw(query(channel, "TCADJTEMP")); }
uint16_t Rd105::read_error_code() { return static_cast<uint16_t>(query(0, "ERRORCODE")); }
double Rd105::read_current(int channel) { check_channel(channel, false); return rd105::current_from_raw(query(channel, "CURRENT")); }
double Rd105::read_duty_percent(int channel) { check_channel(channel, false); return rd105::duty_percent_from_raw(query(channel, "PWMDUTY")); }

// ── 写 ──────────────────────────────────────────────────────────────────────

double Rd105::set_duty_percent(int channel, double percent) {
    check_channel(channel, false);
    if (!std::isfinite(percent)) throw std::invalid_argument("占空比不是一个数，不往设备里写");
    const double clamped = std::max(-100.0, std::min(100.0, percent));
    return rd105::duty_percent_from_raw(set_echo(channel, "PWMDUTY", rd105::duty_percent_to_raw(clamped)));
}
void Rd105::set_enable(int channel, bool on) { check_channel(channel, false); set(channel, "ENABLE", on ? 1 : 0); }
void Rd105::set_mode(int channel, int mode) { check_channel(channel, false); set(channel, "MODE", mode); }
void Rd105::set_power_mode(int channel, int mode) {
    check_channel(channel, false);
    if (mode < 0 || mode > 2) throw std::invalid_argument("POWERMODE 只能是 0~2");
    set(channel, "POWERMODE", mode);
}
int Rd105::read_pwm_frequency() { return static_cast<int>(query(0, "FPWM")); }
void Rd105::set_pwm_frequency(int level) {
    if (level < 0 || level > 3) throw std::invalid_argument("FPWM 档位只能是 0~3");
    set(0, "FPWM", level);
}
void Rd105::set_controller_mode(int mode) { set(0, "CONTMODE", mode); }

void Rd105::set_target(int channel, double celsius) { check_channel(channel, false); set(channel, "TG", rd105::temp_to_raw(celsius)); }
void Rd105::set_speed(int channel, double deg_per_sec) { check_channel(channel, false); set(channel, "SPEED", rd105::speed_to_raw(deg_per_sec)); }
void Rd105::set_max_duty(int channel, int percent) { check_channel(channel, false); set(channel, "LIMITED", percent); }
void Rd105::set_over_temp(int channel, double up_c, double lower_c) {
    std::lock_guard<std::recursive_mutex> g(mu_);
    check_channel(channel, false);
    set(channel, "OVERTEMPUP", rd105::temp_to_raw(up_c));
    set(channel, "OVERTEMPLOWER", rd105::temp_to_raw(lower_c));
}
void Rd105::set_max_current(int channel, double amps) { check_channel(channel, false); set(channel, "SETCURRENT", rd105::max_current_to_raw(amps)); }
void Rd105::set_pid(int channel, long long kp, long long ki, long long kd) {
    std::lock_guard<std::recursive_mutex> g(mu_);
    check_channel(channel, false);
    set(channel, "KP", kp);
    set(channel, "KI", ki);
    set(channel, "KD", kd);
}
void Rd105::start_auto_tune(int channel) { check_channel(channel, false); set(channel, "AUTOPID", 1); }
int Rd105::read_auto_pid(int channel) { check_channel(channel, false); return static_cast<int>(query(channel, "AUTOPID")); }

Rd105ChannelConfig Rd105::read_channel_config(int ch) {
    std::lock_guard<std::recursive_mutex> g(mu_);
    check_channel(ch, false);
    Rd105ChannelConfig c{};
    c.target_c = rd105::temp_from_raw(query(ch, "TG"));
    c.enabled = query(ch, "ENABLE") != 0;
    c.mode = static_cast<int>(query(ch, "MODE"));
    c.kp = query(ch, "KP");
    c.ki = query(ch, "KI");
    c.kd = query(ch, "KD");
    c.max_duty_percent = static_cast<int>(query(ch, "LIMITED"));
    c.speed_c_per_sec = rd105::speed_from_raw(query(ch, "SPEED"));
    c.over_temp_up_c = rd105::temp_from_raw(query(ch, "OVERTEMPUP"));
    c.over_temp_lower_c = rd105::temp_from_raw(query(ch, "OVERTEMPLOWER"));
    c.max_current_a = rd105::max_current_from_raw(query(ch, "SETCURRENT"));
    c.auto_pid = static_cast<int>(query(ch, "AUTOPID"));
    return c;
}

std::pair<double, double> Rd105::read_start_voltage_percent(int channel) {
    std::lock_guard<std::recursive_mutex> g(mu_);
    check_channel(channel, false);
    const double fwd = rd105::start_voltage_percent_from_raw(query(channel, "FDEADV"));
    const double bwd = rd105::start_voltage_percent_from_raw(query(channel, "BDEADV"));
    return {fwd, bwd};
}

// ── 设备标识 ────────────────────────────────────────────────────────────────

// 协议表里 TEC 寄存器是型号代码；ASCII 口实测直接回型号名（"215L"）。Modbus 下照 §3.5.1 的表换成名字，两边看到的一样。
// 逐项容错：个别查询固件不支持 / 格式意外不影响别的项；一声不吭（超时）是链路的事，照抛
Rd105DeviceInfo Rd105::read_device_info() {
    std::lock_guard<std::recursive_mutex> g(mu_);
    Rd105DeviceInfo info{"未知", "未知", std::nullopt};
    std::string text;
    try {
        const long long code = query_text_or_number(0, "TEC", &text);
        info.model = mb_ ? rd105::model_name(code) : text;
    } catch (const Rd105Error& e) { if (e.timeout) throw; }
    try {
        long long f = 0;
        query_text_or_number(0, "FPV", &text);
        info.firmware = parse_int(text, &f) ? rd105::firmware_text(f) : text;
    } catch (const Rd105Error& e) { if (e.timeout) throw; }
    try {
        long long cm = 0;
        query_text_or_number(0, "CONTMODE", &text);
        if (parse_int(text, &cm)) info.controller_mode = static_cast<int>(cm);
    } catch (const Rd105Error& e) { if (e.timeout) throw; }     // 这台固件可能不答 CONTMODE 的查询
    return info;
}

}  // namespace tec
