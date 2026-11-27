#include "reactor_hardware.hpp"
#include "../../drivers/chiller.hpp"
#include "../../drivers/io8r.hpp"
#include "../../drivers/rd105.hpp"
#include "../../drivers/yudian.hpp"
#include <cmath>

namespace tec {

RealReactorHardware::RealReactorHardware(Rd105& rd, Io8r& io, YudianProbe* yudian, Chiller* chiller, const DoMap& map)
    : rd_(rd), io_(io), yudian_(yudian), chiller_(chiller), map_(map) {}

void RealReactorHardware::relay(int index, bool closed) { io_.set_relay(index, closed); }   // 只许一路一路写，见 DoMap 的说明

ReactorReadings RealReactorHardware::read() {
    ReactorReadings r;
    const Rd105Snapshot s = rd_.read_snapshot();
    r.tj[0] = s.temp_c[0]; r.tj[1] = s.temp_c[1]; r.board_c = s.internal_c;
    if (yudian_ && gate_tr_.resting()) {
        r.problem = std::string("釜内探头读不到：") + gate_tr_.why();
    } else if (yudian_) {
        try {
            const auto v = yudian_->read();
            gate_tr_.ok();
            for (int ch = 0; ch < 2; ch++) {
                if (v[ch].value && !v[ch].sensor_fault) r.tr[ch] = *v[ch].value;
                else if (v[ch].problem) r.problem += (r.problem.empty() ? "" : "；") + std::string("釜内探头 ") + (ch == 0 ? "A" : "B") + "：" + *v[ch].problem;
            }
        } catch (const std::exception& e) {
            // 釜内探头读不到不该让夹套也跟着没读数：夹套那一路是温控器给的，照常返回
            gate_tr_.failed(e.what());
            r.problem = std::string("釜内探头读不到：") + e.what();
        }
    }
    if (chiller_) {
        if (coolant_tick_++ % 4 == 0 && !gate_coolant_.resting()) {
            try { coolant_ = chiller_->read_liquid_temp(); gate_coolant_.ok(); }
            catch (const std::exception& e) { coolant_.reset(); gate_coolant_.failed(e.what()); }   // 读不到就是没有这个数：不拿旧值充数，也不因此影响控温
        } else if (gate_coolant_.resting()) coolant_.reset();
        r.coolant_c = coolant_;
    }
    return r;
}

void RealReactorHardware::begin_control(int ch) {
    const int c = ch + 1;
    // 次序照原来的程序：先把模式切到「上位机给占空比」，占空比清 0，再开输出——反过来的话输出一开就带着上一次留在设备里的占空比
    rd_.set_mode(c, 3);
    rd_.set_duty_percent(c, 0);
    rd_.set_power_mode(c, 2);
    enabled_[ch] = false;
    side_[ch] = -1;
}

void RealReactorHardware::apply(int ch, const ChannelCommand& cmd) {
    const int c = ch + 1;
    // 1) 要关输出的先关：扳继电器之前输出必须是关的，继电器不带载切换
    if (!cmd.output_on && enabled_[ch]) {
        rd_.set_duty_percent(c, 0);
        rd_.set_enable(c, false);
        enabled_[ch] = false;
    }
    // 2) 继电器跟着命令的那一侧走。先断旧的、再合新的：加热棒和 TEC 两只从不同时合着
    const int want = cmd.side == HeatSide::Heater ? 0 : 1;
    if (side_[ch] != want) {
        if (enabled_[ch]) { rd_.set_duty_percent(c, 0); rd_.set_enable(c, false); enabled_[ch] = false; }   // 兜底：正常走不到这里
        if (side_[ch] == 0) relay(map_.heater[ch], false);
        if (side_[ch] == 1) relay(map_.tec[ch], false);
        relay(want == 0 ? map_.heater[ch] : map_.tec[ch], true);
        side_[ch] = want;
    }
    // 3) 该开输出的开，然后写这一拍的占空比
    if (cmd.output_on) {
        if (!enabled_[ch]) { rd_.set_duty_percent(c, 0); rd_.set_enable(c, true); enabled_[ch] = true; }
        rd_.set_duty_percent(c, cmd.device_duty);
    }
}

void RealReactorHardware::end_control(int ch) {
    const int c = ch + 1;
    rd_.set_duty_percent(c, 0);
    rd_.set_enable(c, false);
    enabled_[ch] = false;
    // 不控温就把这一路的两只都断开（用户定的：不常合）。无论记着的状态是什么都各写一遍，防止记的和实际的不一样
    relay(map_.heater[ch], false);
    relay(map_.tec[ch], false);
    side_[ch] = -1;
}

void RealReactorHardware::set_water(bool valve_open, bool chiller_run) {
    // 开：先开阀再开冷水机；关：先停冷水机，阀由上面的执行机构层等冷水机停稳了再让关（冷水机转着关阀会憋泵）
    if (valve_open && valve_ != 1) { relay(map_.water, true); valve_ = 1; }
    // 冷水机这一句写不出去的时候要晾着它，不能每拍重来：写一次不通要等满超时加重试（300×2），
    // 而 chiller_run_ 又只有写成了才记下来——于是每一拍都重写、每一拍都白等六百毫秒，
    // 整个核心就被它拖慢，屏上点什么都像死了（用户 2026-11-27：启用冷水机重启之后非常卡顿）。
    // 和读水温用同一条规矩：连着几次不通就认定它这会儿不在，隔一阵再试
    if (chiller_ && (chiller_run ? 1 : 0) != chiller_run_ && !gate_coolant_.resting()) {
        try {
            if (chiller_run) chiller_->start(); else chiller_->stop();
            chiller_run_ = chiller_run ? 1 : 0;
            gate_coolant_.ok();
        } catch (const std::exception& e) {
            gate_coolant_.failed(e.what());     // 这一拍就算了：冷水机不转，水阀照样是开的，TEC 那边不受影响
        }
    }
    if (!valve_open && valve_ != 0) { relay(map_.water, false); valve_ = 0; }
}

}  // namespace tec
