// 对拍：内环 PID + 双向输出，对试算台 inner.py 的逐拍输出。用法：golden_inner <增益表.csv> <对拍数据.csv>...
#include "../core/control/inner_loop.hpp"
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
        tec::InnerLoop il(table);
        long n = 0, bad = 0;
        while (std::getline(in, line)) {
            std::vector<double> v;
            std::stringstream ss(line);
            std::string cell;
            while (std::getline(ss, cell, ',')) v.push_back(std::stod(cell));
            if (v.size() < 10) continue;
            const tec::InnerOut o = il.step(v[0], v[1], v[2], v[3] != 0);
            const double got[6] = {o.pid_out, o.heater_pct, o.tec_pct, o.signed_duty, o.sat_high ? 1.0 : 0.0, o.sat_low ? 1.0 : 0.0};
            for (int i = 0; i < 6; i++)
                if (std::fabs(got[i] - v[4 + i]) > 5e-9) { if (bad < 3) std::printf("  %s 第 %ld 拍 第 %d 列：算出 %.9f，应为 %.9f\n", argv[a], n, i, got[i], v[4 + i]); bad++; }
            n++;
        }
        if (bad) bad_files++;
        std::printf("%s %s  %ld 拍\n", bad ? "不一致" : "一致  ", argv[a], n);
    }
    return bad_files ? 1 : 0;
}
