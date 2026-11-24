#include "io8r.hpp"
#include <stdexcept>
#include <string>

namespace tec {

namespace {
Io8r::Points to_points(const std::vector<bool>& v) {
    Io8r::Points p{};
    for (int i = 0; i < Io8r::kPoints; i++) p[i] = v[i];
    return p;
}
}  // namespace

Io8r::Points Io8r::read_relays() { return to_points(bus_.read_coils(0, kPoints)); }

Io8r::Points Io8r::read_inputs() { return to_points(bus_.read_discrete_inputs(0, kPoints)); }

void Io8r::set_relay(int index, bool closed) {
    if (index < 0 || index >= kPoints)
        throw std::out_of_range("IO8R 只有 DO0~DO7，要切的是 DO" + std::to_string(index));
    bus_.write_coil(static_cast<uint16_t>(index), closed);
}

void Io8r::write_relays(const Points& closed) {
    bus_.write_coils(0, std::vector<bool>(closed.begin(), closed.end()));
}

void Io8r::all_off() { bus_.write_coils(0, std::vector<bool>(kPoints, false)); }

}  // namespace tec
