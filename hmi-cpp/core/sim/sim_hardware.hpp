// 仿真硬件：不接任何设备也能把整个控制核心跑起来。开发界面、演示、在没有设备的电脑上试通信协议时用（--sim）。
// 里面的釜是一个很粗的模型（夹套一阶 + 釜壁 + 釜内），只为了让数字像那么回事地动起来，不能拿它判断控得好不好
// ——判断控温用的是试算台那套标定过的对象。
#pragma once
#include "../station/aux_runner.hpp"
#include "../station/reactor_hardware.hpp"

namespace tec {

class SimHardware : public ReactorHardware, public AuxHardware {
public:
    SimHardware();
    // 控温那一侧
    ReactorReadings read() override;
    void begin_control(int) override {}
    void apply(int ch, const ChannelCommand& cmd) override;
    void end_control(int ch) override { duty_[ch] = 0; }
    void set_water(bool valve_open, bool) override { valve_ = valve_open; }
    // 搅拌 / pH / 进料那一侧
    void set_stir(int ch, double rpm) override { stir_set_[ch] = rpm; }
    StirReading read_stir(int ch) override;
    PhReading read_ph(int ch) override;
    FeedReading read_feed() override;
    void set_pump_rate(int pump, double ml_min) override { rate_[pump - 1] = ml_min; }
    void set_pump(int pump, bool on) override { on_[pump - 1] = on; }

    void advance(double dt);        // 让仿真的釜往前走 dt 秒；每个控温节拍调一次
    double speed = 1.0;             // 仿真里时间走多快（演示时可以调快）

private:
    double tj_[2], wall_[2], tr_[2], duty_[2] = {0, 0};
    bool valve_ = false;
    double stir_set_[2] = {0, 0}, stir_[2] = {0, 0}, ph_[2] = {4.62, 6.98};
    double total_[2] = {0, 0}, rate_[2] = {0, 0};
    bool on_[2] = {false, false};
};

}  // namespace tec
