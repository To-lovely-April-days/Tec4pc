// 冷水机主板（《主机触模屏通讯协议2023》）：Modbus-RTU，9600 8N1，站号 1。从 C# 的 ChillerClient 搬过来，只有协议层。
// 冷却水联动只用到三样：
//   · 开机 / 关机：FC05 写 0x0200 / 0x0201 = FF00（协议 §2.2.1 A）
//   · 机组运行状态：0x0000（§2.2.3）：0 空闲 / 1 准备启动 / 2 正常运行 / 3 延时停机 / 4 故障
//   · 开机时看一眼「起停方式」：0x0403（§2.3.1）。出厂 0 = 只能本地启停，远程开关机指令它不理
//
// 协议里读状态的功能码前后不一：表里写 04，应用实例给的是 03 的帧（CRC 对得上）。先按表发 04，
// 从站回「非法功能码 / 地址」（异常码 1 / 2）再换 03，换成了就一直用它。开关机同理：FC05 不认就换 FC06
// （0x0200 写 0x0072 开机 / 0x0070 关机，§2.2.1 C）。有的固件连异常码都不回、静默丢帧——
// 超时也换：03 / 06 两条路协议里都认，换错没有代价，不换就是永远读不到。
#pragma once
#include "../io/modbus_rtu.hpp"
#include <atomic>
#include <optional>
#include <string>

namespace tec {

class Chiller {
public:
    static constexpr uint16_t kStateAddr = 0x0000;
    static constexpr uint16_t kLiquidTempAddr = 0x0007;
    static constexpr uint16_t kStartCoil = 0x0200;
    static constexpr uint16_t kStopCoil = 0x0201;
    static constexpr uint16_t kCommandReg = 0x0200;
    static constexpr uint16_t kStartValue = 0x0072;
    static constexpr uint16_t kStopValue = 0x0070;
    static constexpr uint16_t kStartStopModeAddr = 0x0403;

    static constexpr int kIdle = 0;
    static constexpr int kFault = 4;

    explicit Chiller(ModbusRtu& bus) : bus_(bus) {}

    // 底下的 Modbus 主站——重试这些链路参数调它（探测时把重试关掉）
    ModbusRtu& bus() { return bus_; }

    // 机组运行状态（0x0000）：0 空闲 / 1 准备启动 / 2 正常运行 / 3 延时停机 / 4 故障
    int read_state();

    // 起停方式（0x0403，FC03）：0 本地 / 1 本地+远程 / 2 远程。0 时远程开关机不生效
    int read_start_stop_mode();
    // 液体温度（0x0007，单位 0.1 ℃，协议 §2.2.3）：就是冷水机送出来的冷却水的温度，界面上叫「Tc 冷媒」
    double read_liquid_temp();

    void start() { command(true); }
    void stop() { command(false); }

    // 冷水机在转（泵在走水）：准备启动 / 正常运行。注意「延时停机」(3) 不算——
    // 上层判「开机时还在转、阀要留着」时是自己另按 1 / 2 / 3 判的
    static bool running(std::optional<int> state) { return state && (*state == 1 || *state == 2); }

    // 状态码 → 人话；空 = 「读不到」
    static std::string state_name(std::optional<int> state);

private:
    void command(bool start);

    ModbusRtu& bus_;
    // 试出来这台固件认哪条路之后就记住，不每拍再撞一次墙。原子量：读状态的线程和发开关机的线程可能不是同一个
    std::atomic<bool> read_by_holding_{false};
    std::atomic<bool> cmd_by_register_{false};
};

}  // namespace tec
