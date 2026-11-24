// 对拍：执行机构层（加热棒 / TEC 切换、冷却水、冷水机），对试算台 session.py（冷却水 = 要冷就开）的逐拍状态。
#include "../core/control/heat_source_switch.hpp"
#include <cstdio>
#include <fstream>
#include <sstream>
#include <string>
#include <vector>

int main(int argc, char** argv) {
    int bad_files = 0;
    for (int a = 1; a < argc; a++) {
        std::ifstream in(argv[a]);
        std::string line;
        std::getline(in, line);
        double sp = 0, tr0 = 0;
        std::sscanf(line.c_str(), "#sp=%lf,tr0=%lf", &sp, &tr0);
        std::getline(in, line);
        tec::HeatSourceSwitch s;
        long n = 0, bad = 0;
        while (std::getline(in, line)) {
            std::vector<std::string> c;
            std::stringstream ss(line);
            std::string cell;
            while (std::getline(ss, cell, ',')) c.push_back(cell);
            if (c.size() < 9) continue;
            const double t = std::stod(c[0]), d = std::stod(c[1]);
            if (n == 0) s.start(t, sp, tr0);
            const bool rd = s.pre(t);
            s.post(t, d);
            const bool ok = rd == (c[2] == "1") && (s.side() == tec::HeatSide::Tec) == (c[3] == "C") && s.output_enabled() == (c[4] == "1") &&
                            s.heat_only() == (c[5] == "1") && s.valve_open() == (c[6] == "1") && static_cast<int>(s.chiller()) == std::stoi(c[7]) && s.flips() == std::stoi(c[8]);
            if (!ok) { if (bad < 3) std::printf("  %s 第 %ld 拍（t = %.1f）状态对不上\n", argv[a], n, t); bad++; }
            n++;
        }
        if (bad) bad_files++;
        std::printf("%s %s  %ld 拍，切换 %d 次\n", bad ? "不一致" : "一致  ", argv[a], n, s.flips());
    }
    return bad_files ? 1 : 0;
}
