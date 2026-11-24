// 对拍：一个工位的控温主循环（外环 + 内环 + 执行机构串起来）。
// 把试算台闭环跑出来的逐拍读数（夹套、釜内）原样喂进来，核对每一拍的夹套目标、PID 输出、功率符号、继电器侧、冷却水阀、冷水机状态。
// 这核的是「同样的读数进来，C++ 整条链路每一拍做的决定和试算台一样」，不是控得好不好。
// 用法：golden_channel <增益表.csv> <迹.csv>...   迹的列：t_s,sp,tj_sp,tr,tj,duty_w,duty_a,side,valve,chiller,pid_out
#include "../core/station/channel_controller.hpp"
#include <cmath>
#include <cstdio>
#include <fstream>
#include <sstream>
#include <string>
#include <vector>

int main(int argc, char** argv) {
    if (argc < 3) return 2;
    const tec::GainTable table = tec::GainTable::load(argv[1]);
    int bad_files = 0;
    for (int a = 2; a < argc; a++) {
        std::ifstream in(argv[a]);
        std::string line;
        std::getline(in, line);
        tec::ChannelConfig cfg;
        cfg.outer.fast.enabled = true;
        cfg.outer.fast.bias_start = [&table](double t) { return table.steady_bias_at(t).value_or(0.0); };
        tec::ChannelController ch(table, cfg);
        long n = 0, bad = 0;
        while (std::getline(in, line)) {
            std::vector<std::string> c;
            std::stringstream ss(line);
            std::string cell;
            while (std::getline(ss, cell, ',')) c.push_back(cell);
            if (c.size() < 11) continue;
            const double sp = std::stod(c[1]), tr = std::stod(c[3]), tj = std::stod(c[4]);
            if (n == 0) ch.start(tec::ControlMode::Reactor, sp);
            const tec::ChannelCommand cmd = ch.tick(tj, tr);
            // 迹里 duty_w = −(这一拍的功率符号量)：正 = TEC；注意它记的是 PID 那一刻想要的，不管继电器在哪一侧
            const bool ok = std::fabs(cmd.jacket_sp - std::stod(c[2])) < 6e-4 && std::fabs(-ch.demand() - std::stod(c[5])) < 6e-3 &&
                            (cmd.side == tec::HeatSide::Tec) == (c[7] == "C") && cmd.valve_open == (c[8] == "1") &&
                            static_cast<int>(cmd.chiller) == std::stoi(c[9]) && std::fabs(ch.pid_out() - std::stod(c[10])) < 6e-3;
            if (!ok) {
                if (bad < 3) std::printf("  %s 第 %ld 拍（t = %s）：夹套目标 %.3f / %s，功率 %.2f / %s，侧 %c / %s\n", argv[a], n, c[0].c_str(), cmd.jacket_sp, c[2].c_str(),
                                         -ch.demand(), c[5].c_str(), cmd.side == tec::HeatSide::Tec ? 'C' : 'H', c[7].c_str());
                bad++;
            }
            n++;
        }
        if (bad) bad_files++;
        std::printf("%s %s  %ld 拍，%ld 拍对不上\n", bad ? "不一致" : "一致  ", argv[a], n, bad);
    }
    return bad_files ? 1 : 0;
}
