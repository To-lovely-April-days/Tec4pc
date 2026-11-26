#include "real_devices.hpp"
#include "../../drivers/aqmd.hpp"
#include "../../drivers/bht_ph.hpp"
#include "../../drivers/chiller.hpp"
#include "../../drivers/feed.hpp"
#include "../../drivers/io8r.hpp"
#include "../../drivers/rd105.hpp"
#include "../../drivers/yudian.hpp"
#include "../../io/modbus_rtu.hpp"
#ifdef _WIN32
#include "../../io/win_serial.hpp"
#endif
#include "../util/text_error.hpp"
#include <map>
#include <stdexcept>

namespace tec {

struct RealDevices::Impl {
    // 同一个串口号只开一次，几台设备共用（共口的设备还共用一把总线锁）
    std::map<std::string, std::unique_ptr<SerialTransport>> ports;
    std::map<std::string, std::shared_ptr<std::mutex>> locks;
    std::map<std::string, DeviceComm> opened_by;       // 这个口当初是按哪台设备的参数开的
    std::vector<std::unique_ptr<ModbusRtu>> buses;
    std::unique_ptr<Rd105> rd;
    std::unique_ptr<Io8r> io;
    std::unique_ptr<Chiller> chiller;
    std::unique_ptr<YudianProbe> yudian;
    std::unique_ptr<BhtPh> ph[2];
    std::unique_ptr<Aqmd> stir[2];
    std::unique_ptr<FeedSystem> feed;
    std::unique_ptr<RealReactorHardware> reactor;
    std::unique_ptr<RealAuxHardware> aux;

    // critical = 温控器和继电器模块（缺了就不能控温）：给足超时和重试，宁可慢也要读到。
    // 别的几台（釜内探头、冷水机、pH、搅拌、进料）等得起：超时短一点、只重试一次 ——
    // 一台没接的设备原来每次读要白等 500×3 + 退避 400 ≈ 2 秒，几台加起来就把一拍撑爆了
    ModbusRtu& bus(const DeviceComm& c, bool critical = true) {
#ifdef _WIN32
        auto& p = ports[c.port];
        if (!p) {
            SerialConfig sc; sc.port = c.port; sc.baud = c.baud; sc.parity = c.parity; sc.stop_bits = c.stop_bits;
            p = make_win_serial(sc);
            p->open();
            opened_by[c.port] = c;
        } else {
            // 两台设备配在同一个串口上是允许的（485 是一根总线，按站号分），但串口参数必须一样：
            // 口只能按一套参数打开，先来的那台说了算。参数不一样的话后来的这台根本不会通——
            // 而且不说一声的话现象很怪：扫描能扫到它，一重启就连不上（用户 2026-11-27 遇到的：
            // pH 电极和搅拌都配在 COM12 上，pH 按 8N1 先把口开了，搅拌要的 8N2 就没了）
            const DeviceComm& first = opened_by[c.port];
            if (first.baud != c.baud || first.parity != c.parity || first.stop_bits != c.stop_bits)
                // 屏上这一行的状态格只放得下十来个字（长了会压到右边的箭头上），所以要翻的那句话很短，
                // 具体差在哪缀在后面——缀的那一段只进记录和控制台，不进要翻的句子（见 util/text_error.hpp）
                const auto settings = [](const DeviceComm& d) {
                    return d.port + " " + std::to_string(d.baud) + " " + std::string(1, d.parity) + std::to_string(d.stop_bits);
                };
                // 缀的这一段整句写在一个字符串里（用 {0} 占位，不要用 + 拼）：抽词表的工具收这个文件里
                // 所有的中文字符串，拿加号拼的话「开着，这台要」这种半截话会混进词表（踩过）
                const std::string detail = fill_text("（{0} 按 {1} 开着，这台要 {2}）", {first.label, settings(first), settings(c)});
                throw TextError(Text{"和同口那台设备参数不一样", {}}, detail);
        }
        auto& lk = locks[c.port];
        if (!lk) lk = std::make_shared<std::mutex>();
        buses.push_back(std::make_unique<ModbusRtu>(*p, static_cast<uint8_t>(c.station), critical ? 500 : 300, lk));
        if (!critical) { buses.back()->timeout_retries = 1; buses.back()->corrupt_retries = 1; }
        return *buses.back();
#else
        (void)c; (void)critical;
        throw std::runtime_error("真串口只有 Windows 版；这台电脑上请用 --sim");
#endif
    }
};

RealDevices::RealDevices(const AppSettings& s) : d_(std::make_unique<Impl>()) {
    // 逐台试：建得起来就留着，建不起来记下原因
    auto bring_up = [&](const char* key, const std::function<void(const DeviceComm&)>& make) {
        const DeviceComm* c = s.device(key);
        DeviceStatus st;
        st.key = key;
        if (!c) { st.problem = Text{"设置里没有这台设备", {}}; status_.push_back(st); return; }
        st.label = c->label; st.port = c->port; st.enabled = c->enabled;
        if (!c->enabled) { st.problem = Text{"未启用", {}}; status_.push_back(st); return; }
        try { make(*c); st.ok = true; }
        catch (const std::exception& e) { st.problem = text_of(e); }
        status_.push_back(st);
    };
    bring_up("rd105", [&](const DeviceComm& c) { d_->rd = std::make_unique<Rd105>(d_->bus(c)); d_->rd->read_snapshot(); });        // 读一拍：通不通当场知道
    bring_up("io8r", [&](const DeviceComm& c) { d_->io = std::make_unique<Io8r>(d_->bus(c)); d_->io->read_relays(); });
    bring_up("chiller", [&](const DeviceComm& c) { d_->chiller = std::make_unique<Chiller>(d_->bus(c, false)); d_->chiller->read_state(); });
    bring_up("tr", [&](const DeviceComm& c) { d_->yudian = std::make_unique<YudianProbe>(d_->bus(c, false)); d_->yudian->init(YudianKind::Thermal); });
    bring_up("ph1", [&](const DeviceComm& c) { d_->ph[0] = std::make_unique<BhtPh>(d_->bus(c, false)); d_->ph[0]->read_live(); });
    bring_up("ph2", [&](const DeviceComm& c) { d_->ph[1] = std::make_unique<BhtPh>(d_->bus(c, false)); d_->ph[1]->read_live(); });
    bring_up("stir1", [&](const DeviceComm& c) { d_->stir[0] = std::make_unique<Aqmd>(d_->bus(c, false)); d_->stir[0]->read_status(); });
    bring_up("stir2", [&](const DeviceComm& c) { d_->stir[1] = std::make_unique<Aqmd>(d_->bus(c, false)); d_->stir[1]->read_status(); });
    bring_up("feed", [&](const DeviceComm& c) { d_->feed = std::make_unique<FeedSystem>(d_->bus(c, false)); d_->feed->read_all(); });
    // 试读失败的那几台当作没接（指针清掉），免得后面每拍都去撞一台不通的设备
    for (auto& st : status_) {
        if (st.ok) continue;
        if (st.key == "rd105") d_->rd.reset(); else if (st.key == "io8r") d_->io.reset(); else if (st.key == "chiller") d_->chiller.reset();
        else if (st.key == "tr") d_->yudian.reset(); else if (st.key == "ph1") d_->ph[0].reset(); else if (st.key == "ph2") d_->ph[1].reset();
        else if (st.key == "stir1") d_->stir[0].reset(); else if (st.key == "stir2") d_->stir[1].reset(); else if (st.key == "feed") d_->feed.reset();
    }
    if (d_->rd && d_->io) d_->reactor = std::make_unique<RealReactorHardware>(*d_->rd, *d_->io, d_->yudian.get(), d_->chiller.get(), s.do_map);
    d_->aux = std::make_unique<RealAuxHardware>(d_->stir[0].get(), d_->stir[1].get(), d_->ph[0].get(), d_->ph[1].get(), d_->feed.get(), s.stir[0], s.stir[1]);
}

RealDevices::~RealDevices() = default;

void RealDevices::release_ports() {
    for (auto& kv : d_->ports) if (kv.second) { try { kv.second->close(); } catch (const std::exception&) {} }
}

bool RealDevices::reopen_ports() {
    bool all = true;
    for (auto& kv : d_->ports) {
        if (!kv.second) continue;
        try { kv.second->open(); } catch (const std::exception&) { all = false; }
    }
    return all;
}

bool RealDevices::can_control() const { return d_->reactor != nullptr; }
ReactorHardware& RealDevices::reactor() { if (!d_->reactor) throw std::runtime_error("温控器或继电器模块没连上"); return *d_->reactor; }
AuxHardware& RealDevices::aux() { return *d_->aux; }

}  // namespace tec
