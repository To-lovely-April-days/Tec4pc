#include "yudian.hpp"
#include <cmath>
#include <stdexcept>

namespace tec {

namespace {

// 说明书 §5.2 的表。suffix 是「哪种模块专用」的标注
struct InpRow { int code; const char* name; const char* suffix; };
const InpRow kInpTable[] = {
    {0, "K 偶", nullptr}, {1, "S 偶", nullptr}, {2, "R 偶", nullptr}, {3, "T 偶", nullptr}, {4, "E 偶", nullptr},
    {5, "J 偶", nullptr}, {6, "B 偶", nullptr}, {7, "N 偶", nullptr}, {8, "WRe3-WRe25", nullptr}, {9, "WRe5-WRe26", nullptr},
    {12, "F2 辐射高温计", nullptr}, {13, "T 偶 0~300.00℃", nullptr}, {17, "K 偶 0~300.00℃", nullptr},
    {18, "J 偶 0~300.00℃", nullptr}, {19, "Ni120", nullptr}, {20, "Cu50", nullptr}, {21, "Pt100", nullptr},
    {22, "Pt100 两位小数", nullptr}, {23, "Pt1000", "J0/J2"}, {24, "0~2000Ω", "J0/J2"}, {25, "0~75mV", nullptr},
    {27, "0~320Ω", nullptr}, {28, "0~20mV", nullptr}, {29, "0~50mV", nullptr}, {33, "1~5V", "J3"}, {34, "0~5V", "J3"},
    {35, "-10~+10mV", nullptr}, {36, "-37.5~+37.5mV", nullptr}, {38, "10~50mV", nullptr}, {39, "15~75mV", nullptr},
    {42, "0~10V", "J3"}, {43, "2~10V", "J3"}, {50, "0~20mA", "J4"}, {51, "4~20mA", "J4"},
};

// 说明书 §5.2 表里有、但既不是热偶 / 热阻也不是电流的规格（mV / V / Ω）
bool is_other_linear(int inp) {
    switch (inp) {
        case 24: case 25: case 27: case 28: case 29: case 33: case 34:
        case 35: case 36: case 38: case 39: case 42: case 43: return true;
        default: return false;
    }
}

// 测温型 InP → 寄存器隐含小数位。按说明书 §1.3「传感器测量范围」表：量程写着 .00 的是两位
// （13 T、17 K、18 J、19 Ni120、22 Pt100、23 Pt1000，×100 后都在 int16 之内），其余一位
// （Pt100 21 是 -200~+800℃，×10）。表里没有的规格返回空，宁可拒绝换算也不上一个猜的系数。
// dPt 只管面板显示，不影响上位机读到的数（§5.4）
std::optional<int> thermal_decimals_of(int inp) {
    switch (inp) {
        case 0: case 1: case 2: case 3: case 4: case 5: case 6: case 7: case 8: case 9: case 12: case 20: case 21: return 1;
        case 13: case 17: case 18: case 19: case 22: case 23: return 2;
        default: return std::nullopt;
    }
}

YudianChannelSetup make_setup(YudianKind kind, int group, int inp, int16_t scl, int16_t sch, int dpt) {
    YudianChannelSetup s;
    s.enabled = true;
    s.group = group;
    s.inp = inp;
    const bool is_current = inp == 50 || inp == 51;
    if (kind == YudianKind::Thermal) {
        const auto decimals = thermal_decimals_of(inp);
        if (is_current)
            s.problem = "这一路的输入规格是 " + YudianProbe::inp_name(inp) + "——说明书 §5.2 标注 50/51 是 J4 电流模块专用，"
                        "热阻探头读不了它；接的是 Pt100 就该把它写成 21";
        else if (is_other_linear(inp))
            s.problem = "这一路的输入规格是 " + YudianProbe::inp_name(inp) + "——不是热偶/热阻，温度探头读不了它";
        else if (!decimals)
            s.problem = "输入规格 InP=" + std::to_string(inp) + " 不在说明书 §5.2 的表里，换算系数定不出来，不猜";
        else
            s.divisor = std::pow(10.0, *decimals);
        return s;
    }
    if (!is_current) {
        s.problem = "这一路的输入规格是 " + YudianProbe::inp_name(inp) + "——不是电流口，pH 变送器（4~20mA）读不了它";
        return s;
    }
    // 线性：寄存器值就是「面板显示值去掉小数点」，隐含小数位 = dPt。
    // ScL/ScH 同一套隐含小数位，换算完就是量程（pH 通常 0.00~14.00）
    const double div = std::pow(10.0, dpt);
    s.divisor = div;
    s.range_lo = scl / div;
    s.range_hi = sch / div;
    return s;
}

std::string join_signed(const std::array<uint16_t, 4>& xs) {
    std::string s;
    for (size_t i = 0; i < xs.size(); i++) { if (i) s += "/"; s += std::to_string(static_cast<int16_t>(xs[i])); }
    return s;
}

}  // namespace

std::string YudianChannelSetup::type_name() const { return enabled ? YudianProbe::inp_name(inp) : "关"; }

std::vector<int> YudianIdentity::usable_channels() const {
    std::vector<int> v;
    for (int i = 0; i < 4; i++) if (channels[i].usable()) v.push_back(i + 1);
    return v;
}

std::string YudianIdentity::usable_list() const {
    const auto v = usable_channels();
    if (v.empty()) return "没有一路";
    std::string s;
    for (size_t i = 0; i < v.size(); i++) { if (i) s += "、"; s += "CH" + std::to_string(v[i]); }
    return s;
}

bool YudianIdentity::any_fault_latched() const {
    for (uint16_t a : raw_aaf) if (a & 0x01) return true;
    return false;
}

std::string YudianIdentity::dump() const {
    return "In=" + join_signed(raw_in) + "，InP=" + join_signed(raw_inp) + "，ScL=" + join_signed(raw_scl) + "，ScH=" + join_signed(raw_sch) +
           "，AAF=" + join_signed(raw_aaf) + "，dPt=" + std::to_string(dpt) + "，Loc=" + std::to_string(loc) + "，型号字 " + std::to_string(feature_word);
}

const YudianIdentity& YudianProbe::init(YudianKind kind) {
    const auto in_regs = bus_.read_holding_registers(kRegIn, 4);
    const auto groups = bus_.read_holding_registers(kRegInp, 12);   // InP×4 + ScL×4 + ScH×4
    const auto glob = bus_.read_holding_registers(kRegDpt, 4);      // dPt、2129、Loc、特征字
    const auto aaf = bus_.read_holding_registers(kRegAaf, 4);       // AAF1~4：断线标志锁不锁

    YudianIdentity id;
    id.dpt = glob[0];
    id.loc = glob[2];
    id.feature_word = glob[3];
    for (int i = 0; i < 4; i++) {
        id.raw_in[i] = in_regs[i];
        id.raw_inp[i] = groups[i];
        id.raw_scl[i] = groups[4 + i];
        id.raw_sch[i] = groups[8 + i];
        id.raw_aaf[i] = aaf[i];

        const int g = in_regs[i] % 10;
        if (g == 0) continue;                 // 这一路关着：缺省构造的 setup 就是「关」
        if (g > 4) {
            // 个位 5~9 不是组号——别拿它去索引，那会把 ScL 当成 InP 读
            YudianChannelSetup s;
            s.enabled = true;
            s.group = g;
            s.problem = "In=" + std::to_string(in_regs[i]) + " 的个位 " + std::to_string(g) + " 不是 1~4 的组号，规格定不出来，不猜";
            id.channels[i] = s;
            continue;
        }
        id.channels[i] = make_setup(kind, g, groups[g - 1], static_cast<int16_t>(groups[4 + (g - 1)]),
                                    static_cast<int16_t>(groups[8 + (g - 1)]), id.dpt);
    }
    kind_ = kind;
    id_ = id;
    return *id_;
}

void YudianProbe::write_inp(int group, uint16_t inp) {
    if (group < 1 || group > 4) throw std::out_of_range("组号只有 1~4");
    bus_.write_register(static_cast<uint16_t>(kRegInp + group - 1), inp);
}

std::array<YudianReading, 4> YudianProbe::read() {
    if (!id_) throw std::logic_error("先 init() 过自检拿到标度，再来读数");
    const auto pv = bus_.read_holding_registers(kRegPv, 4);
    const auto alarm = bus_.read_holding_registers(kRegAlarm, 2);

    std::array<YudianReading, 4> outs;
    for (int i = 0; i < 4; i++) {
        const auto& ch = id_->channels[i];
        // 高字节 = 奇数通道（CH1/CH3），低字节 = 偶数通道（CH2/CH4）
        const int bits = i % 2 == 0 ? alarm[i / 2] >> 8 : alarm[i / 2] & 0xFF;
        const bool fault = (bits & 0x01) != 0;   // oral：断线或超量程
        const bool ha = (bits & 0x02) != 0;
        const bool la = (bits & 0x04) != 0;

        auto& o = outs[i];
        o.raw = static_cast<int16_t>(pv[i]);
        if (!ch.enabled) continue;               // 关着的那一路：只给原始值，报警位也不看
        if (ch.problem || !ch.divisor) {
            o.sensor_fault = fault; o.alarm_high = ha; o.alarm_low = la; o.problem = ch.problem;
            continue;
        }
        // 值可不可信按**量程**判，不按报警字里的 oral 位判：断线 / 短路时寄存器里是量程外的码
        // （311.11、−202.15 那种），照样给出去但把 sensor_fault 挂上，由上层发成 Bad。
        // oral 位只说「出过事」——AAF 不自动复位时它锁住不清，干扰瞬间断线又回来也会留下它；
        // 值在量程内就照常用，把「标志锁着」单独报出来（现场踩到：标志一锁 Tr 永远 Bad）。
        // 量程定不出来的规格退回看 oral 位
        const double value = o.raw / *ch.divisor;
        std::optional<std::pair<double, double>> range;
        if (kind_ == YudianKind::Thermal) range = range_of(ch.inp);
        else if (ch.range_lo && ch.range_hi) range = std::make_pair(*ch.range_lo, *ch.range_hi);
        const bool bad = range ? (value < range->first || value > range->second) : fault;
        o.value = value;
        o.sensor_fault = bad;
        o.alarm_high = ha;
        o.alarm_low = la;
        o.alarm_latched = fault && !bad;
    }
    return outs;
}

std::string YudianProbe::inp_name(int inp) {
    for (const auto& r : kInpTable)
        if (r.code == inp)
            return std::string(r.name) + "（InP=" + std::to_string(inp) + (r.suffix ? std::string("，") + r.suffix : std::string()) + "）";
    return "InP=" + std::to_string(inp);
}

// 断线 / 超量程时拿换算值对着它说「低于下限」还是「高于上限」——
// 现场读到 -20215（InP=22 → -202.15 ℃）就是压在 -200.00 的下限之下，不是「没数」
std::optional<std::pair<double, double>> YudianProbe::range_of(int inp) {
    switch (inp) {
        case 0: return std::make_pair(-200.0, 1300.0);
        case 1: case 2: return std::make_pair(-50.0, 1700.0);
        case 3: return std::make_pair(-200.0, 350.0);
        case 4: return std::make_pair(0.0, 800.0);
        case 5: return std::make_pair(0.0, 1000.0);
        case 6: return std::make_pair(200.0, 1800.0);
        case 7: return std::make_pair(0.0, 1300.0);
        case 8: case 9: return std::make_pair(0.0, 2300.0);
        case 12: return std::make_pair(450.0, 2000.0);
        case 13: case 17: case 18: return std::make_pair(0.0, 300.0);
        case 19: return std::make_pair(-50.0, 270.0);
        case 20: return std::make_pair(-50.0, 150.0);
        case 21: return std::make_pair(-200.0, 800.0);
        case 22: case 23: return std::make_pair(-200.0, 300.0);
        default: return std::nullopt;
    }
}

std::string YudianProbe::kind_name(YudianKind kind) { return kind == YudianKind::Thermal ? "测温" : "线性电流"; }

}  // namespace tec
