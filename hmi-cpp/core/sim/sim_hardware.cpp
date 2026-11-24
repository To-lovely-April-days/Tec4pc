#include "sim_hardware.hpp"
#include <cmath>

namespace tec {

SimHardware::SimHardware() {
    for (int c = 0; c < 2; c++) { tj_[c] = 25.0 + c * 0.1; wall_[c] = tj_[c]; tr_[c] = 24.5 + c * 0.8; }
}

ReactorReadings SimHardware::read() {
    ReactorReadings r;
    for (int c = 0; c < 2; c++) { r.tj[c] = std::round(tj_[c] * 1000) / 1000; r.tr[c] = std::round(tr_[c] * 100) / 100; }
    r.board_c = 31;
    r.coolant_c = 20.5;
    return r;
}

void SimHardware::apply(int ch, const ChannelCommand& cmd) { duty_[ch] = cmd.output_on ? cmd.device_duty : 0.0; }

void SimHardware::advance(double dt) {
    dt *= speed;
    for (int c = 0; c < 2; c++) {
        // 占空比：负 = 加热棒（约 0.16 K/s 满功率），正 = TEC 制冷（约 0.25 K/s 满功率）；夹套往室温漏热
        const double heat = duty_[c] < 0 ? -duty_[c] / 90.0 * 0.16 : 0.0;
        const double cool = duty_[c] > 0 ? duty_[c] / 90.0 * 0.25 : 0.0;
        tj_[c] += dt * (heat - cool - (tj_[c] - 22.0) * 0.0006 - (tj_[c] - tr_[c]) * 0.0008);
        wall_[c] += (tj_[c] - wall_[c]) * (1.0 - std::exp(-dt / 150.0));
        const double bias = 0.1 * (tr_[c] - 5.0);                 // 维持温差随温度涨
        tr_[c] += dt * (wall_[c] - tr_[c] - bias) / 950.0;
        stir_[c] += (stir_set_[c] - stir_[c]) * (1.0 - std::exp(-dt / 2.0));
    }
    for (int p = 0; p < 2; p++) if (on_[p]) { total_[p] += rate_[p] * dt / 60.0; ph_[p] += (7.0 - ph_[p]) * rate_[p] * dt / 600.0; }
}

StirReading SimHardware::read_stir(int ch) { StirReading r; r.ok = true; r.rpm = std::round(stir_[ch]); r.current_a = stir_[ch] > 1 ? 0.3 + stir_[ch] / 600.0 : 0.0; return r; }
PhReading SimHardware::read_ph(int ch) { PhReading r; r.ph = std::round(ph_[ch] * 100) / 100; r.temp_c = tr_[ch]; return r; }
FeedReading SimHardware::read_feed() { FeedReading r; r.ok = true; for (int p = 0; p < 2; p++) { r.total_ml[p] = total_[p]; r.on[p] = on_[p]; r.flow_ml_min[p] = on_[p] ? rate_[p] : 0; } return r; }

}  // namespace tec
