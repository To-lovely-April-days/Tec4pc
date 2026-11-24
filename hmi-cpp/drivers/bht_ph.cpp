#include "bht_ph.hpp"
#include <algorithm>
#include <cmath>
#include <stdexcept>

namespace tec {

BhtLive BhtPh::read_live() {
    const auto r = bus_.read_holding_registers(kRegTemp, 6);
    BhtLive v;
    v.temp_c = r[0] * 0.1;                          // 温度寄存器按无符号解（C# 原样；说明书量程 0~130 ℃，没有负数）
    v.ph = r[1] * 0.01;
    v.manual_temp_c = r[2] * 0.1;
    v.mv = static_cast<int16_t>(r[3]) * 0.1;        // 电位有符号：pH 7 以上是负的
    v.temp_status = r[5];
    return v;
}

BhtCalibration BhtPh::read_calibration() {
    const auto zs = bus_.read_holding_registers(kRegZero, 2);
    const auto buf = bus_.read_holding_registers(kRegBuffer, 1);
    BhtCalibration c;
    c.zero_mv = static_cast<int16_t>(zs[0]) * 0.01;
    c.slope_percent = zs[1];
    c.buffer_set = buf[0];
    return c;
}

BhtComm BhtPh::read_comm() {
    const auto ab = bus_.read_holding_registers(kRegAddress, 2);
    const auto fr = bus_.read_holding_registers(kRegFrame, 1);
    BhtComm c;
    c.address = ab[0];
    c.baud = ab[1];
    c.frame = fr[0];
    return c;
}

void BhtPh::write(uint16_t reg, uint16_t value) { bus_.write_register(reg, value); }

void BhtPh::start_calibration(BhtCalPoint point) { write(kRegCalibrate, static_cast<uint16_t>(point)); }

int BhtPh::read_calibration_result() { return bus_.read_holding_registers(kRegCalibrate, 1)[0]; }

void BhtPh::set_buffer_set(int buffer_set) { write(kRegBuffer, buffer_set == 1 ? 1 : 0); }

void BhtPh::set_zero_mv(double mv) {
    mv = std::clamp(mv, -kZeroLimitMv, kZeroLimitMv);
    write(kRegZero, static_cast<uint16_t>(static_cast<int16_t>(std::lround(mv * 100))));
}

void BhtPh::set_address(int address) { write(kRegAddress, static_cast<uint16_t>(std::clamp(address, 1, 247))); }

void BhtPh::set_baud(int baud) {
    if (baud != 4800 && baud != 9600 && baud != 19200)
        throw std::invalid_argument("波特率只认 4800 / 9600 / 19200，" + std::to_string(baud) + " 没写");
    write(kRegBaud, static_cast<uint16_t>(baud));
}

void BhtPh::set_frame(int frame) {
    if (frame < 0 || frame > 3) throw std::invalid_argument("数据格式只有 0~3（8N2 / 8N1 / 8E1 / 8O1），" + std::to_string(frame) + " 没写");
    write(kRegFrame, static_cast<uint16_t>(frame));
}

void BhtPh::restore_factory() { write(kRegRestore, kRestoreMagic); }

void BhtPh::reboot() { write(kRegReboot, kRebootMagic); }

std::string BhtPh::temp_status_text(int status) {
    switch (status) {
        case 0: return "正常";
        case 1: return "温度过高或过低";
        case 2: return "未接温度传感器";
        default: return "状态码 " + std::to_string(status);
    }
}

std::string BhtPh::buffer_text(int buffer_set) { return buffer_set == 1 ? "4.00 / 6.86 / 9.18" : "4.00 / 7.00 / 10.00"; }

std::string BhtPh::frame_text(int frame) {
    switch (frame) {
        case 0: return "8N2";
        case 1: return "8N1";
        case 2: return "8E1";
        case 3: return "8O1";
        default: return "格式码 " + std::to_string(frame);
    }
}

std::string BhtPh::calibration_result_text(int code) {
    switch (code) {
        case 0x00: return "标定成功";
        case 0x80: return "标定失败：温度超出范围（允许标定的温度 5~50 ℃）";
        case 0x40: return "标定失败：mV 值超出范围";
        case 0x20: return "标定失败：斜率超出范围（小于 70 % 或大于 120 %）——电极老化或缓冲液不对";
        case 0x10: return "标定失败：零点毫伏值超出范围（小于 −70 mV 或大于 +70 mV）——电极脏了、缓冲液不对或没泡进去";
        default: return "标定结果码 " + std::to_string(code) + "（说明书表里没有）";
    }
}

}  // namespace tec
