// 自动检测这件事的两半，各测一遍：
//   1. resolve_device_ports —— 平时的主力：COM 号变了，按记住的 USB 口反查回来，不发一个字节
//   2. scan_serial_ports  —— 兜底：一个口一个口地只读问「你是谁」，认不准就说认不准、不猜
#include "../core/app/port_scan.hpp"
#include "../io/fake_serial.hpp"
#include "../io/modbus_rtu.hpp"
#include <cstdio>
#include <map>
#include <string>
#include <vector>

using namespace tec;

static int fails = 0;
static void check(bool ok, const std::string& what) {
    if (!ok) { std::printf("[FAIL] %s\n", what.c_str()); fails++; }
}

// ── 一个只会答 FC03 的假从站：给它一张「地址 → 值」的表，表里没有的地址答异常码 2（非法地址）──
struct Regs {
    uint8_t station = 1;
    std::map<uint16_t, uint16_t> v;
    std::vector<uint8_t> operator()(const std::vector<uint8_t>& q) const {
        if (q.size() < 8 || q[0] != station) return {};
        const uint8_t fc = q[1];
        const uint16_t start = static_cast<uint16_t>(q[2] << 8 | q[3]);
        const int count = q[4] << 8 | q[5];
        auto err = [&](uint8_t code) {
            std::vector<uint8_t> r{q[0], static_cast<uint8_t>(fc | 0x80), code};
            const uint16_t c = modbus_crc(r.data(), r.size());
            r.push_back(c & 0xFF); r.push_back(c >> 8);
            return r;
        };
        if (fc != 0x03) return err(1);                      // 只认 FC03
        for (int i = 0; i < count; i++) if (!v.count(static_cast<uint16_t>(start + i))) return err(2);
        std::vector<uint8_t> r{q[0], fc, static_cast<uint8_t>(count * 2)};
        for (int i = 0; i < count; i++) {
            const uint16_t x = v.at(static_cast<uint16_t>(start + i));
            r.push_back(x >> 8); r.push_back(x & 0xFF);
        }
        const uint16_t c = modbus_crc(r.data(), r.size());
        r.push_back(c & 0xFF); r.push_back(c >> 8);
        return r;
    }
};

// 一台 AQMD 搅拌驱动该有的样子：设备标识 + 版本 + ASCII 名字（现场实测 "AQMD2408BLS-M2" v1.3）
static Regs aqmd_slave(uint8_t station) {
    Regs g; g.station = station;
    g.v[0x0000] = 0x0203;
    g.v[0x0001] = 0x0103;
    const std::string name = "AQMD2408BLS-M2";
    for (int i = 0; i < 8; i++) {
        const size_t a = static_cast<size_t>(i) * 2, b = a + 1;
        const uint8_t hi = a < name.size() ? static_cast<uint8_t>(name[a]) : 0;
        const uint8_t lo = b < name.size() ? static_cast<uint8_t>(name[b]) : 0;
        g.v[static_cast<uint16_t>(0x0002 + i)] = static_cast<uint16_t>(hi << 8 | lo);
    }
    for (uint16_t a = 0x000A; a <= 0x000F; a++) g.v[a] = 0;
    return g;
}

// 一台 BHT pH 电极：0~5 是温度 / pH / mV 那一组，25 是数据格式（说明书写死 = 1）
static Regs bht_slave(uint8_t station) {
    Regs g; g.station = station;
    for (uint16_t a = 0; a <= 5; a++) g.v[a] = 100;
    g.v[25] = 1;
    return g;
}

int main() {
    // ── 1. 按记住的 USB 口反查今天的 COM 号 ──
    {
        set_port_lister([] {
            return std::vector<SerialPortInfo>{
                {"COM20", "USB-Enhanced-SERIAL-A CH344", "USB\\VID_1A86&PID_55DA&MI_00\\A", 0},
                {"COM21", "USB-Enhanced-SERIAL-B CH344", "USB\\VID_1A86&PID_55DA&MI_02\\B", 0},
            };
        });
        AppSettings s = AppSettings::defaults();
        // 温控器标定过（线还插在 A 口），搅拌 1 标定过但那个 USB 口今天不在，继电器从来没标定过
        for (auto& d : s.devices) {
            if (d.key == "rd105") { d.port = "COM7"; d.usb_id = "USB\\VID_1A86&PID_55DA&MI_00\\A"; }
            if (d.key == "stir1") { d.port = "COM14"; d.usb_id = "USB\\VID_1A86&PID_55DA&MI_99\\拔了"; }
        }
        const std::vector<PortFix> fixed = resolve_device_ports(s);
        check(fixed.size() == 1, "只有温控器这一台的口要改");
        if (fixed.size() == 1) {
            check(fixed[0].key == "rd105" && fixed[0].from == "COM7" && fixed[0].to == "COM20", "温控器 COM7 → COM20");
            check(fixed[0].usb_name == "USB-Enhanced-SERIAL-A CH344", "顺带记下设备管理器里那串名字");
        }
        for (const auto& d : s.devices) {
            if (d.key == "rd105") check(d.port == "COM20", "温控器的口已经改过来");
            // USB 口今天不在（线拔了 / 集线器没上电）：绝不乱改成别人的口，照旧报连不上
            if (d.key == "stir1") check(d.port == "COM14", "USB 口不在的那台不动");
            // 从来没标定过的只能认死配置里的 COM 号
            if (d.key == "io8r") check(d.port == "COM8" && d.usb_id.empty(), "没标定过的不动");
        }
        // 口号：有 USB 身份的才编，按实例 ID 的先后 1、2
        const std::vector<SerialPortInfo> ps = list_serial_ports();
        check(ps.size() == 2 && ps[0].slot == 1 && ps[1].slot == 2, "有 USB 身份的口才编号");
    }

    // ── 2. 一个口一个口地问「你是谁」──
    {
        set_port_lister([] {
            return std::vector<SerialPortInfo>{
                {"COM20", "A", "USB\\A", 1},      // 一台搅拌驱动，站号 2
                {"COM21", "B", "USB\\B", 2},      // 一台 pH 电极，站号 1
                {"COM22", "C", "USB\\C", 3},      // 什么都没接
                {"COM23", "D", "USB\\D", 4},      // 口打不开
            };
        });
        std::map<std::string, Regs> slaves;
        slaves["COM20"] = aqmd_slave(2);
        slaves["COM21"] = bht_slave(1);
        int opened = 0;
        auto open = [&](const SerialConfig& c) -> std::unique_ptr<SerialTransport> {
            if (c.port == "COM23") throw std::runtime_error("口被别的程序占着");
            opened++;
            if (!slaves.count(c.port))      // 什么都没接的口：一声不吭（不是「回异常码」，那是有人在）
                return std::make_unique<FakeSerial>([](const std::vector<uint8_t>&) { return std::vector<uint8_t>{}; });
            const Regs g = slaves.at(c.port);
            return std::make_unique<FakeSerial>([g](const std::vector<uint8_t>& q) { return g(q); });
        };
        ScanOptions opt;
        opt.timeout_ms = 50;
        opt.bauds = {9600};                       // 假串口不看波特率，一档就够
        int progress = 0;
        const std::vector<ScanHit> hits = scan_serial_ports(opt, open, [&](int, int, const ScanHit&) { progress++; });

        check(hits.size() == 4, "四个口各回一条");
        check(progress == 4, "进度每个口报一次");
        check(hits[0].type == "stir" && hits[0].model == "AQMD2408BLS-M2 v1.3", "COM20 认出搅拌驱动和版本号");
        check(hits[0].station == 2 && hits[0].slot == 1, "顺带记下站号和 USB 口号");
        check(hits[1].type == "ph" && hits[1].model == "BHT pH 电极", "COM21 认出 pH 电极");
        check(hits[2].type.empty() && hits[2].problem.t.empty(), "没接东西的口：没认出来，也不报错");
        check(hits[3].type.empty() && !hits[3].problem.t.empty(), "打不开的口如实报原因");

        // 只读：整个扫描过程一个写功能码都不许发
        for (const auto& kv : slaves) (void)kv;
        bool wrote = false;
        auto open_watch = [&](const SerialConfig& c) -> std::unique_ptr<SerialTransport> {
            const Regs g = slaves.count(c.port) ? slaves.at(c.port) : Regs{1, {}};
            (void)0;
            return std::make_unique<FakeSerial>([g, &wrote](const std::vector<uint8_t>& q) {
                if (q.size() > 1 && (q[1] == 0x05 || q[1] == 0x06 || q[1] == 0x0F || q[1] == 0x10)) wrote = true;
                return g(q);
            });
        };
        set_port_lister([] { return std::vector<SerialPortInfo>{{"COM20", "A", "USB\\A", 1}, {"COM21", "B", "USB\\B", 2}}; });
        scan_serial_ports(opt, open_watch);
        check(!wrote, "扫描全程只发读功能码，一个写都没有");
    }

    // ── 3. 两类都对得上的时候不许猜 ──
    {
        set_port_lister([] { return std::vector<SerialPortInfo>{{"COM30", "X", "USB\\X", 1}}; });
        // 一台既像 pH 电极（0~5 有数、25 == 1）又像进料系统（0~48 连读得通）的设备
        Regs g; g.station = 1;
        for (uint16_t a = 0; a <= 48; a++) g.v[a] = 100;
        g.v[25] = 1;
        auto open = [g](const SerialConfig&) -> std::unique_ptr<SerialTransport> {
            return std::make_unique<FakeSerial>([g](const std::vector<uint8_t>& q) { return g(q); });
        };
        ScanOptions opt; opt.timeout_ms = 50; opt.bauds = {9600};
        const std::vector<ScanHit> hits = scan_serial_ports(opt, open);
        check(hits.size() == 1, "一个口一条");
        check(hits[0].type.empty(), "认不准就不认——绝不从两类里挑一类");
        check(hits[0].problem.filled().find("认不准") != std::string::npos, "说清是认不准");
        check(hits[0].problem.filled().find("pH 电极") != std::string::npos && hits[0].problem.filled().find("进料系统") != std::string::npos,
              "把都对上的那几类列出来给人看");
    }

    // ── 4. 帧格式也要试：搅拌驱动要 8N2 才通（只按 8N1 问的话永远认不出来）──
    {
        set_port_lister([] { return std::vector<SerialPortInfo>{{"COM40", "S", "USB\\S", 1}}; });
        const Regs g = aqmd_slave(1);
        auto open = [g](const SerialConfig& c) -> std::unique_ptr<SerialTransport> {
            // 参数不对就一声不吭，和真设备一样
            if (!(c.parity == 'N' && c.stop_bits == 2 && c.baud == 9600))
                return std::make_unique<FakeSerial>([](const std::vector<uint8_t>&) { return std::vector<uint8_t>{}; });
            return std::make_unique<FakeSerial>([g](const std::vector<uint8_t>& q) { return g(q); });
        };
        ScanOptions opt; opt.timeout_ms = 20; opt.bauds = {38400, 9600};
        const std::vector<ScanHit> hits = scan_serial_ports(opt, open);
        check(hits.size() == 1 && hits[0].type == "stir", "8N2 的搅拌驱动能认出来");
        check(hits[0].parity == 'N' && hits[0].stop_bits == 2 && hits[0].baud == 9600, "顺带记下它是 9600 / 8N2");
    }

    // ── 5. 设置里已经写好的参数先试一枪 ──
    {
        set_port_lister([] { return std::vector<SerialPortInfo>{{"COM41", "G", "USB\\G", 1}}; });
        std::vector<SerialConfig> tried;
        const Regs g = bht_slave(2);
        auto open = [&](const SerialConfig& c) -> std::unique_ptr<SerialTransport> {
            tried.push_back(c);
            return std::make_unique<FakeSerial>([g](const std::vector<uint8_t>& q) { return g(q); });
        };
        ScanOptions opt; opt.timeout_ms = 20;
        opt.guesses.push_back(ScanGuess{"COM41", 19200, 'E', 1, 2});
        const std::vector<ScanHit> hits = scan_serial_ports(opt, open);
        check(!tried.empty() && tried[0].baud == 19200 && tried[0].parity == 'E' && tried[0].stop_bits == 1,
              "第一枪按设置里写的参数问");
        check(hits.size() == 1 && hits[0].type == "ph" && hits[0].station == 2, "第一枪就中，站号也对");
        check(tried.size() == 1, "中了就不再往下试别的组合");
    }

    // ── 6. 有人答话、可七类都对不上：也要说出来，别让人以为这口是空的 ──
    {
        set_port_lister([] { return std::vector<SerialPortInfo>{{"COM42", "U", "USB\\U", 1}}; });
        Regs g; g.station = 1; g.v[7] = 123;          // 只有 7 号地址有数：七类谁都对不上
        auto open = [g](const SerialConfig&) -> std::unique_ptr<SerialTransport> {
            return std::make_unique<FakeSerial>([g](const std::vector<uint8_t>& q) { return g(q); });
        };
        ScanOptions opt; opt.timeout_ms = 20; opt.bauds = {9600};
        const std::vector<ScanHit> hits = scan_serial_ports(opt, open);
        check(hits.size() == 1 && hits[0].type.empty(), "认不出来就是认不出来");
        check(hits[0].problem.filled().find("答话") != std::string::npos, "但要说清楚「这个口上有东西在答话」");
    }

    set_port_lister(nullptr);
    if (fails == 0) std::printf("自动检测：全部通过\n");
    return fails == 0 ? 0 : 1;
}
