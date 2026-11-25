// 仿真模式下的假串口：让「自动检测」那一屏在不接硬件的机器上也能走一遍。
//
// 为什么要这个：真串口只有 Windows 有，开发机上一个口都没有，自动检测这条路就没法自查；
// 现场装机前也想先在仿真里看一眼这一屏长什么样、点起来顺不顺。
// 假设备答的是各自协议里真正的那几个寄存器（和 tests/test_port_scan.cpp 里一样的料），
// 所以走的是和真机完全同一套识别逻辑，不是另画一张假界面。
//
// 认出来的型号后面一律带「（仿真）」——仿真模式下整页本来就写着「没连真设备」，这里再标一次，
// 免得有人把仿真的结果当成真机的结果。
#pragma once
#include "../../io/fake_serial.hpp"
#include "../../io/serial_ports.hpp"
#include "../../io/modbus_rtu.hpp"
#include <map>
#include <memory>
#include <string>
#include <vector>

namespace tec {

// 假的八口 CH344：口号和 USB 身份都编得和现场那台一个样子，COM 号故意和出厂配置里的不一样，
// 好让「检测到的和现在配的不一样」这件事在仿真里也看得见
inline std::vector<SerialPortInfo> sim_serial_ports() {
    std::vector<SerialPortInfo> v;
    static const char* kLetters[8] = {"A", "B", "C", "D", "A", "B", "C", "D"};
    for (int i = 0; i < 8; i++) {
        SerialPortInfo p;
        p.port = "COM" + std::to_string(20 + i);
        p.name = std::string("USB-Enhanced-SERIAL-") + kLetters[i] + " CH344";
        p.usb_id = "USB\\VID_1A86&PID_55DA&MI_0" + std::to_string((i % 4) * 2) + "\\SIM" + std::to_string(i / 4);
        v.push_back(p);
    }
    return v;
}

// 一个只答 FC03 / FC01 / FC02 的假从站
struct SimSlave {
    uint8_t station = 1;
    std::map<uint16_t, uint16_t> regs;
    int bits = 0;                      // >0 = 这台认 FC01 / FC02，答这么多位

    std::vector<uint8_t> reply(const std::vector<uint8_t>& q) const {
        if (q.size() < 8 || q[0] != station) return {};
        const uint8_t fc = q[1];
        const uint16_t start = static_cast<uint16_t>(q[2] << 8 | q[3]);
        const int count = q[4] << 8 | q[5];
        auto finish = [](std::vector<uint8_t> r) {
            const uint16_t c = modbus_crc(r.data(), r.size());
            r.push_back(c & 0xFF); r.push_back(c >> 8);
            return r;
        };
        auto err = [&](uint8_t code) { return finish({q[0], static_cast<uint8_t>(fc | 0x80), code}); };
        if (fc == 0x01 || fc == 0x02) {
            if (bits <= 0 || start != 0 || count != bits) return err(2);
            return finish({q[0], fc, 1, 0});
        }
        if (fc != 0x03 && fc != 0x04) return err(1);
        for (int i = 0; i < count; i++) if (!regs.count(static_cast<uint16_t>(start + i))) return err(2);
        std::vector<uint8_t> r{q[0], fc, static_cast<uint8_t>(count * 2)};
        for (int i = 0; i < count; i++) {
            const uint16_t x = regs.at(static_cast<uint16_t>(start + i));
            r.push_back(x >> 8); r.push_back(x & 0xFF);
        }
        return finish(std::move(r));
    }
};

// 八个口上各挂一台，顺序照现场的接线：温控器、继电器、Tr、pH ×2、搅拌 ×2、进料
inline std::map<std::string, SimSlave> sim_slaves() {
    std::map<std::string, SimSlave> m;
    {   // COM20：TEC 温控器 RD105。0x0001 是型号代码（30 = 203），0x000C 是固件
        SimSlave s; s.station = 1; s.regs[0x0001] = 30; s.regs[0x000C] = 130; m["COM20"] = s;
    }
    {   // COM21：切换继电器 IO8R，八路继电器 + 八路光耦
        SimSlave s; s.station = 1; s.bits = 8; m["COM21"] = s;
    }
    {   // COM22：Tr 探头（宇电四路）。1536 起四路测量值，2048 起四路输入规格（21 = Pt100）
        SimSlave s; s.station = 1;
        for (uint16_t a = 1536; a < 1540; a++) s.regs[a] = 250;
        for (uint16_t a = 2048; a < 2052; a++) s.regs[a] = 21;
        m["COM22"] = s;
    }
    for (const char* p : {"COM23", "COM24"}) {   // pH 电极两台：0~5 一组读数，25 是数据格式
        SimSlave s; s.station = 1;
        for (uint16_t a = 0; a <= 5; a++) s.regs[a] = 700;
        s.regs[25] = 1;
        m[p] = s;
    }
    for (const char* p : {"COM25", "COM26"}) {   // 搅拌两台：设备标识 + 版本 + ASCII 名字
        SimSlave s; s.station = 2;
        s.regs[0x0000] = 0x0203; s.regs[0x0001] = 0x0103;
        const std::string name = "AQMD2408BLS-M2";
        for (int i = 0; i < 8; i++) {
            const size_t a = static_cast<size_t>(i) * 2, b = a + 1;
            const uint8_t hi = a < name.size() ? static_cast<uint8_t>(name[a]) : 0;
            const uint8_t lo = b < name.size() ? static_cast<uint8_t>(name[b]) : 0;
            s.regs[static_cast<uint16_t>(0x0002 + i)] = static_cast<uint16_t>(hi << 8 | lo);
        }
        for (uint16_t a = 0x000A; a <= 0x000F; a++) s.regs[a] = 0;
        m[p] = s;
    }
    {   // COM27：进料系统，0~48 一帧连读
        SimSlave s; s.station = 1;
        for (uint16_t a = 0; a <= 48; a++) s.regs[a] = 0;
        m["COM27"] = s;
    }
    return m;
}

// 把假串口装上：list_serial_ports() 以后报这八个口，开口开出来的是会按协议作答的假从站
inline std::function<std::unique_ptr<SerialTransport>(const SerialConfig&)> install_sim_ports() {
    set_port_lister(sim_serial_ports);
    return [](const SerialConfig& c) -> std::unique_ptr<SerialTransport> {
        static const std::map<std::string, SimSlave> all = sim_slaves();
        auto it = all.find(c.port);
        const SimSlave s = it == all.end() ? SimSlave{} : it->second;
        return std::make_unique<FakeSerial>([s](const std::vector<uint8_t>& q) { return s.reply(q); });
    };
}

}  // namespace tec
