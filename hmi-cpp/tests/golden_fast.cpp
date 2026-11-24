// 对拍：快速升温外环（参数 = 试算台上的 MF_u）。输入同 golden_outer，逐拍核对内部量、预测最高点、加码、夹套目标。
// 用法：golden_fast <增益表.csv> <对拍数据.csv>...
#include "../core/control/gain_table.hpp"
#include "../core/control/outer_loop.hpp"
#include <cmath>
#include <cstdio>
#include <fstream>
#include <sstream>
#include <string>
#include <vector>

int main(int argc, char** argv) {
    if (argc < 3) return 2;
    const tec::GainTable table = tec::GainTable::load(argv[1]);
    if (table.empty()) { std::printf("读不到增益表 %s\n", argv[1]); return 2; }
    int files = 0, bad_files = 0;
    long rows = 0;
    for (int a = 2; a < argc; a++) {
        std::ifstream in(argv[a]);
        std::string line;
        std::getline(in, line);
        tec::OuterParams p;
        p.fast.enabled = true;
        p.fast.bias_start = [&table](double t) { return table.steady_bias_at(t).value_or(0.0); };
        tec::OuterLoop est(p);
        long n = 0, bad = 0;
        double fworst = 0;
        while (std::getline(in, line)) {
            std::vector<double> v;
            std::stringstream ss(line);
            std::string cell;
            while (std::getline(ss, cell, ',')) v.push_back(std::stod(cell));
            if (v.size() < 13) continue;
            tec::OuterObs o;
            o.t = v[0]; o.dt = v[1]; o.sp = v[2]; o.tr = v[3]; o.tj = v[4]; o.first = n == 0;
            if (n == 0) est.reset(o);
            const double u = est.step(o).tj_sp;
            // 列：wall,xr,b,tau,rj,peak,boost,u
            const double got[8] = {est.wall(), est.xr(), est.b(), est.tau(), est.rj(), est.peak(), est.boost(), u};
            const double tol[8] = {2e-9, 2e-9, 2e-9, 1e-6, 2e-9, 5e-9, 5e-9, 5e-9};
            for (int i = 0; i < 8; i++) {
                const double e = std::fabs(got[i] - v[5 + i]);
                if (e / tol[i] > fworst) fworst = e / tol[i];
                if (e > tol[i] && bad < 3) std::printf("  %s 第 %ld 拍 第 %d 列：算出 %.9f，应为 %.9f\n", argv[a], n, i, got[i], v[5 + i]);
                if (e > tol[i]) bad++;
            }
            n++;
        }
        files++; rows += n;
        if (bad) bad_files++;
        std::printf("%s %s  %ld 拍，最大偏差 = 容差的 %.2f 倍\n", bad ? "不一致" : "一致  ", argv[a], n, fworst);
    }
    std::printf("\n%d 场 %ld 拍，%d 场不一致\n", files, rows, bad_files);
    return bad_files ? 1 : 0;
}
