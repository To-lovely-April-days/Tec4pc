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
#include <map>
#include <stdexcept>

namespace tec {

struct RealDevices::Impl {
    // 同一个串口号只开一次，几台设备共用（共口的设备还共用一把总线锁）
    std::map<std::string, std::unique_ptr<SerialTransport>> ports;
    std::map<std::string, std::shared_ptr<std::mutex>> locks;
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

    ModbusRtu& bus(const DeviceComm& c) {
#ifdef _WIN32
        auto& p = ports[c.port];
        if (!p) {
            SerialConfig sc; sc.port = c.port; sc.baud = c.baud; sc.parity = c.parity; sc.stop_bits = c.stop_bits;
            p = make_win_serial(sc);
            p->open();
        }
        auto& lk = locks[c.port];
        if (!lk) lk = std::make_shared<std::mutex>();
        buses.push_back(std::make_unique<ModbusRtu>(*p, static_cast<uint8_t>(c.station), 500, lk));
        return *buses.back();
#else
        (void)c;
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
    bring_up("chiller", [&](const DeviceComm& c) { d_->chiller = std::make_unique<Chiller>(d_->bus(c)); d_->chiller->read_state(); });
    bring_up("tr", [&](const DeviceComm& c) { d_->yudian = std::make_unique<YudianProbe>(d_->bus(c)); d_->yudian->init(YudianKind::Thermal); });
    bring_up("ph1", [&](const DeviceComm& c) { d_->ph[0] = std::make_unique<BhtPh>(d_->bus(c)); d_->ph[0]->read_live(); });
    bring_up("ph2", [&](const DeviceComm& c) { d_->ph[1] = std::make_unique<BhtPh>(d_->bus(c)); d_->ph[1]->read_live(); });
    bring_up("stir1", [&](const DeviceComm& c) { d_->stir[0] = std::make_unique<Aqmd>(d_->bus(c)); d_->stir[0]->read_status(); });
    bring_up("stir2", [&](const DeviceComm& c) { d_->stir[1] = std::make_unique<Aqmd>(d_->bus(c)); d_->stir[1]->read_status(); });
    bring_up("feed", [&](const DeviceComm& c) { d_->feed = std::make_unique<FeedSystem>(d_->bus(c)); d_->feed->read_all(); });
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
bool RealDevices::can_control() const { return d_->reactor != nullptr; }
ReactorHardware& RealDevices::reactor() { if (!d_->reactor) throw std::runtime_error("温控器或继电器模块没连上"); return *d_->reactor; }
AuxHardware& RealDevices::aux() { return *d_->aux; }

}  // namespace tec
