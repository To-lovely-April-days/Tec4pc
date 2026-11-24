// 两液一气进料系统（霍桐仪器 HTLAB，Modbus-RTU）的协议层：一路 MFC 气体 + 两台计量泵。
// 从原来 C# 程序的 FeedRegisters / FeedClient 搬过来的，只搬寄存器表、量纲、读写操作；
// 上层那套（轮询、定量加料送够就停、安全停机、断线重连）不在这里。
//
// 寄存器表摘自厂家给的 MaxChemical 插件源码 TwoLiquidOneGasFeedSystem_ModbusRTU.cs 的注释（2026-11-10 用户给的）：
//   · 厂家 CSV 里的地址按 MCGS PLC 地址（从 1 数）标，实际下发到 Modbus 帧的是从 0 数的，整体减 1——这里写的全是下发地址
//   · Float 两个寄存器一个数，IEEE-754 单精度大端（ABCD，高字在前）；Int 一个寄存器
//   · 功能码 03 读、06 写单个、10 写多个；一次读 49 个寄存器（0~48）把过程值 / 配置 / 状态 / 报警全拿到
//   · 单位：MFC 是 L/min、L；泵是 mL/min、mL、MPa。寄存器里就是这些单位的浮点数，不带倍率，不用再乘除
//   · 厂家软件缺省 9600 8N1、站号 1、2 s 轮询一次
//
// 这台设备**没有「目标体积」寄存器**。定量加料是上层做的：写设定流量、开泵、记下起点累计量，
// 每拍读累计量，够了就写停。累计量是进料系统 PLC 报的，不是本程序拿流量积分的。
#pragma once
#include "../io/modbus_rtu.hpp"
#include <cstdint>
#include <string>
#include <utility>

namespace tec {

namespace feed_reg {
// ── Float（各占 2 寄存器）──
constexpr uint16_t MfcSetpoint = 0;         // MFC 设定流量 L/min            AI_SV_00H_1
constexpr uint16_t MfcFlow = 2;             // MFC 瞬时流量 L/min            _MFC_Lm
constexpr uint16_t MfcTotal = 4;            // MFC 累计流量 L                _MFC_ADD_L
constexpr uint16_t Pump1Setpoint = 6;       // 泵 1 设定流量 mL/min          Flow_F_SV_3
constexpr uint16_t Pump1Flow = 8;           // 泵 1 瞬时流量 mL/min
constexpr uint16_t Pump1Total = 10;         // 泵 1 累计 mL
constexpr uint16_t Pump1Pressure = 12;      // 泵 1 压力 MPa
constexpr uint16_t Pump2Setpoint = 14;
constexpr uint16_t Pump2Flow = 16;
constexpr uint16_t Pump2Total = 18;
constexpr uint16_t Pump2Pressure = 20;
constexpr uint16_t MfcRangeHigh = 22;       // MFC 量程上限 L/min            AI_SCH_0EH_1
constexpr uint16_t MfcRangeLow = 24;        // MFC 量程下限
constexpr uint16_t MfcCorrection = 26;      // MFC 流量修正系数
constexpr uint16_t MfcDpt = 28;             // MFC 小数点位（固定 1）
constexpr uint16_t Pump1PressureHigh = 30;  // 泵 1 压力上限报警 MPa
constexpr uint16_t Pump1PressureLow = 32;
constexpr uint16_t Pump2PressureHigh = 34;
constexpr uint16_t Pump2PressureLow = 36;

// ── Int16（各占 1 寄存器）──
constexpr uint16_t MfcOnOff = 38;           // **0 运行 / 1 停止**（跟泵反着，厂家就这么定的）  AI_Srun_1BH_1
constexpr uint16_t Pump1OnOff = 39;         // 1 运行 / 0 停止                                   Liq_PP_Run_1
constexpr uint16_t Pump2OnOff = 40;
constexpr uint16_t Pump1PressureAlarm = 41;
constexpr uint16_t Pump2PressureAlarm = 42;
constexpr uint16_t MfcCommAlarm = 43;       // 进料系统内部 PLC 读不到 MFC
constexpr uint16_t Pump1CommAlarm = 44;
constexpr uint16_t Pump2CommAlarm = 45;
constexpr uint16_t AlarmReset = 46;         // 写 1 复位全部报警
constexpr uint16_t AlarmSilence = 47;       // 写 1 消音
constexpr uint16_t AlarmStatus = 48;        // 1 = 有报警

constexpr int AllCount = 49;                // 0~48 一帧读全（Modbus 单帧上限 125）

// 两个寄存器（高字在前）→ IEEE-754 单精度
float float_of(uint16_t hi, uint16_t lo);
// 单精度 → 两个寄存器（first = 高字），写 0x10 用
std::pair<uint16_t, uint16_t> regs_of(float v);
// 报警寄存器的中文名；不认识的回「寄存器 N」
std::string alarm_name(uint16_t reg);
}  // namespace feed_reg

// 一拍全量快照（寄存器 0~48）。Float 一律先按单精度解出来再放宽成 double，跟 C# 一样。
struct FeedSnapshot {
    double mfc_setpoint = 0, mfc_flow = 0, mfc_total = 0;                              // L/min、L/min、L
    double pump1_setpoint = 0, pump1_flow = 0, pump1_total = 0, pump1_pressure = 0;    // mL/min、mL/min、mL、MPa
    double pump2_setpoint = 0, pump2_flow = 0, pump2_total = 0, pump2_pressure = 0;
    double mfc_range_high = 0, mfc_range_low = 0, mfc_correction = 0, mfc_dpt = 0;
    double pump1_pressure_high = 0, pump1_pressure_low = 0, pump2_pressure_high = 0, pump2_pressure_low = 0;
    bool mfc_on = false, pump1_on = false, pump2_on = false;
    bool pump1_pressure_alarm = false, pump2_pressure_alarm = false;
    bool mfc_comm_alarm = false, pump1_comm_alarm = false, pump2_comm_alarm = false;
    bool alarm_active = false;                                                         // 总报警位（寄存器 48）

    bool any_alarm() const;
    // 「泵 1 压力上限报警、泵 2 通信故障…」——报警位挨个点名；没有就是空串
    std::string alarm_text() const;

    // 按泵号取（1 / 2；别的号抛 std::invalid_argument）
    double pump_setpoint(int pump) const;
    double pump_flow(int pump) const;
    double pump_total(int pump) const;      // 已加体积 mL：PLC 报的累计量，开停泵不清零，一趟加了多少要上层减起点
    double pump_pressure(int pump) const;
    bool pump_on(int pump) const;
};

// 进料系统的访问层：一帧读全 + 单点写（Float 走 0x10 两寄存器、Int 走 0x06）。
// 同步接口，出错原样抛 tec::ModbusError（超时 / 坏帧 / 从站异常码），这一层不吞、不重试、不翻译。
// 写完不自己读回核对——C# 是上层在「设备参数」面板里写完再读回比对的，协议层不做。
class FeedSystem {
public:
    explicit FeedSystem(ModbusRtu& mb) : mb_(mb) {}
    ModbusRtu& modbus() { return mb_; }

    FeedSnapshot read_all();                                 // 03 读 0~48，一帧拿全
    double read_float(uint16_t reg);                         // 03 读 2 个寄存器
    uint16_t read_int16(uint16_t reg);                       // 03 读 1 个寄存器
    void write_float(uint16_t reg, double value);            // 0x10 写 2 个寄存器（先收窄成单精度）
    void write_int16(uint16_t reg, uint16_t value);          // 0x06

    void set_pump(int pump, bool on);                        // 泵 1 / 2 开停：1 运行、0 停止
    void set_pump_setpoint(int pump, double ml_per_min);     // 泵设定流量 mL/min
    void set_mfc(bool on);                                   // MFC 开停：**0 运行、1 停止**（跟泵反着）
    void set_mfc_setpoint(double l_per_min);                 // MFC 设定流量 L/min
    void reset_alarms();                                     // 寄存器 46 写 1
    void silence_alarms();                                   // 寄存器 47 写 1

private:
    ModbusRtu& mb_;
};

}  // namespace tec
