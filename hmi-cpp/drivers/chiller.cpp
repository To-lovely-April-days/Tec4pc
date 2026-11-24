#include "chiller.hpp"

namespace tec {

namespace {
// 「这条路它不认」：回了非法功能码 / 非法地址，或者干脆不答（静默丢帧）。
// 别的错（CRC 坏、站号不对、别的异常码）不是「不认」，原样往上抛
bool not_supported(const ModbusError& e) { return e.timeout || (e.code && (*e.code == 0x01 || *e.code == 0x02)); }
}  // namespace

int Chiller::read_state() {
    if (!read_by_holding_) {
        try {
            return bus_.read_input_registers(kStateAddr, 1)[0];
        } catch (const ModbusError& e) {
            if (!not_supported(e)) throw;
            read_by_holding_ = true;   // 03 协议应用实例里就认，换过去没有代价
        }
    }
    return bus_.read_holding_registers(kStateAddr, 1)[0];
}

double Chiller::read_liquid_temp() {
    // 和读运行状态同一段寄存器、同一条规矩：先按表用 04，不认就换 03，换成了就一直用
    uint16_t raw;
    if (!read_by_holding_) {
        try {
            raw = bus_.read_input_registers(kLiquidTempAddr, 1)[0];
            return static_cast<int16_t>(raw) * 0.1;
        } catch (const ModbusError& e) {
            if (!not_supported(e)) throw;
            read_by_holding_ = true;
        }
    }
    raw = bus_.read_holding_registers(kLiquidTempAddr, 1)[0];
    return static_cast<int16_t>(raw) * 0.1;      // 有符号：零下的水温是补码
}

int Chiller::read_start_stop_mode() { return bus_.read_holding_registers(kStartStopModeAddr, 1)[0]; }

void Chiller::command(bool start) {
    if (!cmd_by_register_) {
        try {
            bus_.write_coil(start ? kStartCoil : kStopCoil, true);
            return;
        } catch (const ModbusError& e) {
            if (!not_supported(e)) throw;
            cmd_by_register_ = true;   // 同上：换 FC06 写命令字（0x0072 开机 / 0x0070 关机）
        }
    }
    bus_.write_register(kCommandReg, start ? kStartValue : kStopValue);
}

std::string Chiller::state_name(std::optional<int> state) {
    if (!state) return "读不到";
    switch (*state) {
        case 0: return "空闲";
        case 1: return "准备启动";
        case 2: return "正常运行";
        case 3: return "延时停机";
        case 4: return "故障";
        default: return "未知状态 " + std::to_string(*state);
    }
}

}  // namespace tec
