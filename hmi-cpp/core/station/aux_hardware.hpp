// 搅拌、pH、进料三样的真设备接线。哪样没接就给 nullptr，对应的读数是「没有」，不影响别的。
#pragma once
#include "aux_runner.hpp"

namespace tec {

class Aqmd;
class BhtPh;
class FeedSystem;

struct StirMotor {
    int poles = 4;              // 电机极数：转速和换向频率之间靠它换算，填错了转速就差倍数
    double gear_ratio = 1.0;
    int direction = 1;          // +1 正转 / −1 反转
    double ramp_s = 5.0;        // 从 0 加到目标转速用多久
};

class RealAuxHardware : public AuxHardware {
public:
    RealAuxHardware(Aqmd* stir_a, Aqmd* stir_b, BhtPh* ph_a, BhtPh* ph_b, FeedSystem* feed, const StirMotor& ma = {}, const StirMotor& mb = {});
    void set_stir(int ch, double rpm) override;
    StirReading read_stir(int ch) override;
    PhReading read_ph(int ch) override;
    FeedReading read_feed() override;
    void set_pump_rate(int pump, double ml_min) override;
    void set_pump(int pump, bool on) override;

private:
    Aqmd* stir_[2];
    BhtPh* ph_[2];
    FeedSystem* feed_;
    StirMotor motor_[2];
    int max_accel_reg_[2] = {-1, -1};
    bool x10_[2] = {false, false};
};

}  // namespace tec
