// 宇电 AI-8848G D91 四路采集模块的访问层。从 C# 的 YudianClient 搬过来，只有协议层
// （「读第几路」「要不要把 InP 写成 Pt100」「断线只说一遍」这些是上层会话的事，不在这里）。
// J7（热偶 / 热阻，釜内 Tr 的 Pt100 接它）和 J4（4~20mA，pH 变送器接它）除输入规格外寄存器完全一致，
// 所以一套代码、两种 Kind。出厂地址 1、波特率 19200；几台模块拼在同一段导轨上时 485 已并联，
// 共一条串口——那几个 ModbusRtu 要共用同一把锁。
//
// 手册里三条反直觉的坑，全都在这层消化掉：
//   1. 寄存器按参数分块、不按通道分块（读 4 路 PV 是 1536 起连读 4 个）；
//   2. dPt 只影响面板显示，上位机读到的永远是整数，小数位自己算——
//      测温型按 InP 查表（量程带 .00 的是两位小数），线性型按 dPt；
//   3. 报警状态一个寄存器装两路：高字节 = 奇数通道，低字节 = 偶数通道。
//
// 现场经验（原样带过来）：
//   · 现场那台 J7 出厂四路 InP 全是 51（4~20mA，J4 专用），Pt100 读不了——参数出厂配错了。模块没有面板，
//     改 InP 只能走总线（06 功能码，只受 Loc 约束）。写完要读回核对，不信应答只信读回；
//   · 每改一次 InP 都要把模块断电重上电（说明书原话「PT100 输入需要先接好线再重新上电」）；
//   · 值可不可信按量程判、不按报警字里的 oral 位判：标志一锁，Tr 就永远 Bad（现场踩到），见 YudianReading。
#pragma once
#include "../io/modbus_rtu.hpp"
#include <array>
#include <optional>
#include <string>
#include <utility>
#include <vector>

namespace tec {

// 这台模块该是哪种输入
enum class YudianKind {
    Thermal,   // AI-8848GD91J7：热电偶 / 热电阻，InP 查表定标度
    Linear     // AI-8848GD91J4：4~20mA（或 0~20mA），ScL/ScH 定标 + dPt 定小数位
};

// 一路输入在初始化时查明的身份。problem 非空表示这一路的数不能信
struct YudianChannelSetup {
    bool enabled = false;                // In 个位 = 0 就是这一路关着
    int group = 0;                       // 用的哪组输入配置（In 个位，1~4）
    int inp = -1;                        // 那组的输入规格；定不出来是 -1
    std::optional<double> divisor;       // 寄存器值 ÷ 它 = 工程值；空 = 定不出标度
    std::optional<double> range_lo;      // 线性输入的定标下限（已换算）；测温型是空
    std::optional<double> range_hi;
    std::optional<std::string> problem;  // 接反了 / 规格不认识……人话原因

    // 这一路开着、规格跟探头对得上、标度定得出——探头能读它
    bool usable() const { return enabled && !problem && divisor; }
    // 「Pt100（InP=21）」「4~20mA（InP=51，J4）」「关」——报错和探测应答里指着说的那一路是什么口
    std::string type_name() const;
};

// 初始化自检读回来的全套身份。开每条串口都要过这一关（需求 §4）
struct YudianIdentity {
    uint16_t feature_word = 0;   // 2131 型号特征字。手册没给对照表，只存原始值进日志
    uint16_t loc = 0;            // 2130 参数封锁
    uint16_t dpt = 0;            // 2128 小数点位置（只影响面板显示与线性定标的隐含小数位）
    std::array<YudianChannelSetup, 4> channels;
    // 读回来的原始寄存器。没有说明书核对时，把它们原样摆出来比任何解读都可靠
    std::array<uint16_t, 4> raw_in{}, raw_inp{}, raw_scl{}, raw_sch{};
    std::array<uint16_t, 4> raw_aaf{};   // AAF1~4（0818H）：报警自动复位选择

    // Loc.5/6/7 任一位挂着，写入就会「应答正常但不生效」——手册点名的最难查的坑
    bool write_locked() const { return (loc & 0xE0) != 0; }
    // 探头能读的那几路（1 起的 CH 号，按序）
    std::vector<int> usable_channels() const;
    // 「CH2、CH4」；一路都没有是「没有一路」
    std::string usable_list() const;
    // 有哪一组 AAF 把「输入故障不自动复位」打开了（bit0）——那样断线标志要手动清（说明书 §5.3）
    bool any_fault_latched() const;
    // 「In=1/2/3/4，InP=51/51/51/51，ScL=…，ScH=…，AAF=…，dPt=2，Loc=0，型号字 8848」——模块参数一览，进日志用
    std::string dump() const;
};

// 一路的一次读数。
// raw 是 PV 寄存器的原样（有符号）——这一路的规格认不出来时 value 是空，原始数照样给，让人自己看它像不像温度。
// sensor_fault = 值不可信：换算出来的数在这种输入的量程之外（断线 / 短路时寄存器里是 311.11、−202.15
//   这类量程外的码），或者量程定不出来而报警字里的 oral 位挂着。这时 value 照样给，上层该按 Bad 发。
// alarm_latched = 报警字里 oral 位挂着、值却在量程内：标志锁着没清（AAF 不自动复位，说明书 §5.3）或
//   干扰瞬间断线又回来了——值照常用，标志单独报出来。从前只看 oral 位，标志一锁，Tr 就永远 Bad（现场踩到）。
struct YudianReading {
    std::optional<double> value;
    int16_t raw = 0;
    bool sensor_fault = false;
    bool alarm_high = false;
    bool alarm_low = false;
    std::optional<std::string> problem;
    bool alarm_latched = false;
};

class YudianProbe {
public:
    // 寄存器地址（十进制，见速查手册 §7）
    static constexpr uint16_t kRegIn = 384;      // In01~04：个位 0 = 该路关闭，1~4 = 用哪组输入配置
    static constexpr uint16_t kRegPv = 1536;     // PV1~4 测量值，只读，有符号
    static constexpr uint16_t kRegAlarm = 1664;  // 报警状态，一寄存器两通道
    static constexpr uint16_t kRegInp = 2048;    // InP1~4 输入规格（后面紧跟 ScL1~4、ScH1~4，12 个一把读）
    static constexpr uint16_t kRegScl = 2052;    // ScL1~4 线性定标下限
    static constexpr uint16_t kRegAaf = 2072;    // AAF1~4 报警自动复位选择（0818H）
    static constexpr uint16_t kRegDpt = 2128;    // dPt 小数点位置（2128..2131 一把读上来：dPt、2129、Loc、特征字）

    // 常用的 InP 代码：Tr 探头缺省写成 Pt100 两位小数（用户定的）
    static constexpr uint16_t kInpK = 0, kInpPt100 = 21, kInpPt100x100 = 22, kInpMa0_20 = 50, kInpMa4_20 = 51;

    explicit YudianProbe(ModbusRtu& bus) : bus_(bus) {}
    ModbusRtu& bus() { return bus_; }

    // 初始化自检（四问）：读 In / InP+ScL+ScH / dPt+Loc+特征字 / AAF，核对每一路的输入规格跟期望的
    // 模块种类对不对得上。「温度口插了 pH 表」这种接反在这里就报出来（那一路的 problem），不等到曲线画歪了才发现
    const YudianIdentity& init(YudianKind kind);

    // 初始化自检后的身份；没 init 过就是空
    const std::optional<YudianIdentity>& identity() const { return id_; }

    // 读一拍（两问）：4 路 PV + 报警状态。没 init 过抛 std::logic_error——没有标度没法换算。
    // PV 是有符号数——零下的温度是负的补码，别当无符号解
    std::array<YudianReading, 4> read();

    // 把第 group 组（1~4）的输入规格写成 inp（FC 06）。写完由调用方重新 init() 读回核对，不信应答信读回；
    // 写之前调用方该先看 write_locked()。group 不在 1~4 抛 std::out_of_range
    void write_inp(int group, uint16_t inp);

    // InP 代码 → 人话（说明书 §5.2 的表）；表里没有的照代码原样印（「InP=99」），不猜它是什么口
    static std::string inp_name(int inp);
    // 测温规格的量程（℃，说明书 §1.3）；不是测温规格返回空
    static std::optional<std::pair<double, double>> range_of(int inp);
    // 「测温」/「线性电流」
    static std::string kind_name(YudianKind kind);

private:
    ModbusRtu& bus_;
    std::optional<YudianIdentity> id_;
    YudianKind kind_ = YudianKind::Thermal;
};

}  // namespace tec
