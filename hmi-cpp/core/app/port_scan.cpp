#include "port_scan.hpp"
#include "../../drivers/aqmd.hpp"
#include "../../drivers/bht_ph.hpp"
#include "../../drivers/chiller.hpp"
#include "../../drivers/io8r.hpp"
#include "../../drivers/rd105.hpp"
#include "../../drivers/yudian.hpp"
#include "../../io/modbus_rtu.hpp"
#include <algorithm>
#include <optional>

namespace tec {
namespace {

struct Found { std::string type, model; };

// ── 各类设备的「报姓名」。全部只读，而且每一条都要「答得上来」+「答出来的数说得通」两条都成立 ──
// 只有一条成立不算：别的设备在同一个地址上也可能答出点什么来，CRC 对、长度对，但数是别的意思。

// 搅拌驱动 AQMD：最干净的一个，它自己就存着设备标识、版本号和 ASCII 名字（0x0000~0x000F 一帧读全）
std::optional<Found> id_stir(ModbusRtu& m) {
    const std::vector<uint16_t> r = m.read_holding_registers(aqmd_reg::kDeviceId, aqmd_reg::kIdentityCount);
    if (r.size() < 10) return std::nullopt;
    std::string name;
    for (int i = 0; i < 8; i++) {                       // 0x0002~0x0009：两个字符一个寄存器，'\0' 结尾
        const char hi = static_cast<char>(r[2 + i] >> 8), lo = static_cast<char>(r[2 + i] & 0xFF);
        if (hi == '\0') break; name += hi;
        if (lo == '\0') break; name += lo;
    }
    if (name.rfind("AQMD", 0) != 0) return std::nullopt;
    const int major = r[1] >> 8, minor = r[1] & 0xFF;
    return Found{"stir", name + " v" + std::to_string(major) + "." + std::to_string(minor)};
}

// pH 电极 BHT：0~5 是温度 / pH / mV 这一组，25 是数据格式（说明书写死 = 1）。
// 光读 0~5 不够——别的板子低地址上也有数；要 25 == 1 一起成立
std::optional<Found> id_ph(ModbusRtu& m) {
    const std::vector<uint16_t> live = m.read_holding_registers(BhtPh::kRegTemp, 6);
    if (live.size() < 6) return std::nullopt;
    const std::vector<uint16_t> fmt = m.read_holding_registers(BhtPh::kRegFrame, 1);
    if (fmt.size() != 1 || fmt[0] != 1) return std::nullopt;
    return Found{"ph", "BHT pH 电极"};
}

// 切换继电器 IO8R：8 路继电器（FC01）+ 8 路光耦（FC02）都答得上来。
// 答位的设备本来就少，两个功能码都认的更少
std::optional<Found> id_io8r(ModbusRtu& m) {
    if (m.read_coils(0, Io8r::kPoints).size() != static_cast<size_t>(Io8r::kPoints)) return std::nullopt;
    if (m.read_discrete_inputs(0, Io8r::kPoints).size() != static_cast<size_t>(Io8r::kPoints)) return std::nullopt;
    return Found{"io8r", "JY-MODBUS-IO8R · 8 路"};
}

// TEC 温控器 RD105：0x0001 是型号代码（协议 §3.5.1 的表里 1~31），0x000C 是固件版本。
// 只认表里有的代码——读回一个表外的数字说明这不是它，是别的设备在这个地址上的别的量
std::optional<Found> id_rd105(ModbusRtu& m) {
    const std::vector<uint16_t> t = m.read_holding_registers(0x0001, 1);
    if (t.size() != 1 || t[0] < 1 || t[0] > 31) return std::nullopt;
    std::string model = "RD" + rd105::model_name(t[0]);
    const std::vector<uint16_t> f = m.read_holding_registers(0x000C, 1);
    if (f.size() == 1) model += " · 固件 " + rd105::firmware_text(f[0]);
    return Found{"rd105", model};
}

// 冷水机：0x0000 机组状态只可能是 0~4，0x0403 起停方式只可能是 0~2。
// 协议里读状态的功能码表写 04、实例给 03，两条都试（和驱动里一样）
std::optional<Found> id_chiller(ModbusRtu& m) {
    for (int pass = 0; pass < 2; pass++) {
        std::vector<uint16_t> st, mode;
        try {
            st = pass == 0 ? m.read_input_registers(Chiller::kStateAddr, 1) : m.read_holding_registers(Chiller::kStateAddr, 1);
            mode = pass == 0 ? m.read_input_registers(Chiller::kStartStopModeAddr, 1) : m.read_holding_registers(Chiller::kStartStopModeAddr, 1);
        } catch (const ModbusError&) { continue; }
        if (st.size() == 1 && mode.size() == 1 && st[0] <= 4 && mode[0] <= 2) return Found{"chiller", "冷水机主板"};
    }
    return std::nullopt;
}

// Tr 探头（宇电 AI-8848G 四路）：地址排得很有特征——1536 起 4 个是四路测量值，
// 2048 起 4 个是四路输入规格（InP，表里的取值有限）。两处都对上才算
std::optional<Found> id_yudian(ModbusRtu& m) {
    if (m.read_holding_registers(YudianProbe::kRegPv, 4).size() != 4) return std::nullopt;
    const std::vector<uint16_t> inp = m.read_holding_registers(YudianProbe::kRegInp, 4);
    if (inp.size() != 4) return std::nullopt;
    for (uint16_t v : inp) if (v > 60) return std::nullopt;        // InP 是一张小表里的号，不会是个大数
    return Found{"tr", "宇电 AI-8848G · 四路"};
}

// 进料系统：一帧连读 0~48（49 个）。这一条最不挑，放在最后，而且因为「认不准就不猜」，
// 它和别人同时对上的时候谁也不采用
std::optional<Found> id_feed(ModbusRtu& m) {
    if (m.read_holding_registers(0, 49).size() != 49) return std::nullopt;
    return Found{"feed", "进料系统主板"};
}

using IdFn = std::optional<Found> (*)(ModbusRtu&);
const IdFn kIdentifiers[] = {id_stir, id_ph, id_io8r, id_rd105, id_chiller, id_yudian, id_feed};

}  // namespace

std::string scan_type_label(const std::string& type) {
    if (type == "rd105") return "TEC 温控器";
    if (type == "io8r") return "切换继电器";
    if (type == "chiller") return "冷水机";
    if (type == "tr") return "Tr 探头";
    if (type == "ph") return "pH 电极";
    if (type == "stir") return "搅拌驱动";
    if (type == "feed") return "进料系统";
    return type;
}

std::vector<ScanHit> scan_serial_ports(const ScanOptions& opt, const PortOpenFn& open, const ScanProgressFn& on_progress) {
    std::vector<SerialPortInfo> ports = list_serial_ports();
    if (!opt.only_ports.empty()) {
        std::vector<SerialPortInfo> keep;
        for (auto& p : ports)
            if (std::find(opt.only_ports.begin(), opt.only_ports.end(), p.port) != opt.only_ports.end()) keep.push_back(p);
        ports.swap(keep);
    }

    std::vector<ScanHit> out;
    const int total = static_cast<int>(ports.size());
    for (int i = 0; i < total; i++) {
        const SerialPortInfo& p = ports[i];
        ScanHit hit;
        hit.port = p.port; hit.usb_id = p.usb_id; hit.usb_name = p.name; hit.slot = p.slot;

        for (int baud : opt.bauds) {
            if (!hit.type.empty() || !hit.problem.empty()) break;
            SerialConfig sc; sc.port = p.port; sc.baud = baud; sc.parity = 'N'; sc.stop_bits = 1;
            std::unique_ptr<SerialTransport> tr;
            try { tr = open(sc); if (tr) tr->open(); }
            catch (const std::exception& e) { hit.problem = e.what(); break; }     // 口打不开（被别的程序占着）：这个口就别试了
            if (!tr) continue;

            for (int station : opt.stations) {
                ModbusRtu m(*tr, static_cast<uint8_t>(station), opt.timeout_ms);
                // 扫描时不重试：每个不通的口都要白等几倍超时，八个口就是几十秒
                m.timeout_retries = 0;
                m.corrupt_retries = 0;

                std::vector<Found> matched;
                for (IdFn f : kIdentifiers) {
                    try { if (std::optional<Found> r = f(m)) matched.push_back(*r); }
                    catch (const ModbusError&) {}          // 这一类不是它，换下一类
                    catch (const std::exception&) {}
                }
                if (matched.empty()) continue;
                if (matched.size() > 1) {
                    // 两类以上都对上了：不挑。把都对上的列出来，让人自己定
                    std::string who;
                    for (auto& f : matched) { if (!who.empty()) who += "、"; who += scan_type_label(f.type); }
                    hit.problem = "认不准：" + who + " 都对得上，请人工指定";
                    hit.baud = baud; hit.station = station;
                    break;
                }
                hit.type = matched[0].type; hit.model = matched[0].model;
                hit.baud = baud; hit.station = station;
                break;
            }
            try { tr->close(); } catch (const std::exception&) {}
        }
        out.push_back(hit);
        if (on_progress) on_progress(i + 1, total, hit);
    }
    return out;
}

std::vector<PortFix> resolve_device_ports(AppSettings& s) {
    std::vector<PortFix> fixed;
    const std::vector<SerialPortInfo> ports = list_serial_ports();
    for (DeviceComm& d : s.devices) {
        if (d.usb_id.empty()) continue;              // 还没标定过这台：只能认死配置里的 COM 号
        const SerialPortInfo* now = nullptr;
        for (const SerialPortInfo& p : ports) if (p.usb_id == d.usb_id) { now = &p; break; }
        if (!now) continue;                          // 这个 USB 口今天不在（线拔了 / 集线器没上电）：别乱改，照旧报连不上
        if (now->port == d.port) { d.usb_name = now->name; continue; }
        fixed.push_back(PortFix{d.key, d.label, d.port, now->port, now->name});
        d.port = now->port;
        d.usb_name = now->name;
    }
    return fixed;
}

}  // namespace tec
