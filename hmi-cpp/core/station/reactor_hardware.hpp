// 把控温主循环接到真设备上的那一层：读数从哪来、命令写到哪去。
// 抽成一个接口，是为了控制核心的其余部分（主循环线程、序列、通信）不接硬件也能跑、能测。
#pragma once
#include "channel_controller.hpp"
#include <optional>
#include <stdexcept>
#include <string>

namespace tec {

class Rd105;
class Io8r;
class YudianProbe;
class Chiller;

struct ReactorReadings {
    double tj[2] = {0, 0};              // 两个工位的夹套温度；没接探头是 NaN
    std::optional<double> tr[2];        // 两个工位的釜内温度；读不到 / 探头故障就空着
    double board_c = 0;                 // 温控器板上温度
    std::optional<double> coolant_c;    // 冷媒温度 = 冷水机的冷却水温度；没接冷水机 / 读不到就空着
    std::string problem;                // 这一拍读数出的问题（空 = 正常）
};

// 继电器模块上每一路接的是什么（从 0 数）。现场接线：加热棒 DO0 / DO1，TEC 功率 DO7 / DO6，冷却水阀 DO4。
// DO2 / DO3 是使能继电器：程序绝不去关它们（用户定的），所以这里根本没有它们的位置。
struct DoMap {
    int heater[2] = {0, 1};
    int tec[2] = {7, 6};
    int water = 4;
};

class ReactorHardware {
public:
    virtual ~ReactorHardware() = default;
    virtual ReactorReadings read() = 0;
    virtual void begin_control(int ch) = 0;                         // ch：0 = 工位 A，1 = 工位 B
    virtual void apply(int ch, const ChannelCommand& cmd) = 0;      // 每拍一次
    virtual void end_control(int ch) = 0;                           // 停控：输出关、这一路的加热棒和 TEC 继电器断开
    virtual void set_water(bool valve_open, bool chiller_run) = 0;  // 冷却水阀整机只有一只，两个工位谁要都开
};

// 温控器或继电器模块没连上时顶在那里的「空设备」：读数一律报读不到，什么都不往外写。
// 有了它，控制核心在串口配错的时候也能起来——不能控温，但触摸屏还连得上、还能进「通信设置」把串口改对。
class OfflineReactorHardware : public ReactorHardware {
public:
    explicit OfflineReactorHardware(std::string why) : why_(std::move(why)) {}
    ReactorReadings read() override { throw std::runtime_error(why_); }
    void begin_control(int) override { throw std::runtime_error(why_); }
    void apply(int, const ChannelCommand&) override {}
    void end_control(int) override {}
    void set_water(bool, bool) override {}
private:
    std::string why_;
};

class RealReactorHardware : public ReactorHardware {
public:
    // yudian / chiller 可以没有（给 nullptr）：没有釜内探头就只能夹套控温；没接冷水机串口就只管阀
    RealReactorHardware(Rd105& rd, Io8r& io, YudianProbe* yudian, Chiller* chiller, const DoMap& map = {});
    ReactorReadings read() override;
    void begin_control(int ch) override;
    void apply(int ch, const ChannelCommand& cmd) override;
    void end_control(int ch) override;
    void set_water(bool valve_open, bool chiller_run) override;

private:
    void relay(int index, bool closed);
    Rd105& rd_;
    Io8r& io_;
    YudianProbe* yudian_;
    Chiller* chiller_;
    DoMap map_;
    bool enabled_[2] = {false, false};
    int side_[2] = {-1, -1};            // −1 两只都断着，0 加热棒合着，1 TEC 合着
    int valve_ = -1, chiller_run_ = -1; // −1 = 还没写过
    int coolant_tick_ = 0;              // 冷水机水温不用每拍都读（它变得慢，串口也慢）：四拍读一次
    std::optional<double> coolant_;
};

}  // namespace tec
