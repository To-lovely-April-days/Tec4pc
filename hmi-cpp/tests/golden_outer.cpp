// 对拍：把 14 场真机记录的输入一拍一拍喂给 C++ 版外环，算出来的内部量和夹套目标要和 Python 参考实现逐拍一样。
// 对拍数据是参考实现按 %.9f / %.6f 写的，所以容差取到打印精度。
#include "../core/control/outer_loop.hpp"
#include <cmath>
#include <cstdio>
#include <fstream>
#include <sstream>
#include <string>
#include <vector>

int main(int argc, char** argv) {
    int files = 0, bad_files = 0;
    long rows = 0;
    double worst = 0;
    for (int a = 1; a < argc; a++) {
        std::ifstream in(argv[a]);
        std::string line;
        std::getline(in, line);   // 表头
        tec::OuterLoop est;
        long n = 0, bad = 0;
        double fworst = 0;
        while (std::getline(in, line)) {
            std::vector<double> v;
            std::stringstream ss(line);
            std::string cell;
            while (std::getline(ss, cell, ',')) v.push_back(std::stod(cell));
            if (v.size() < 15) continue;
            tec::OuterObs o;
            o.t = v[0]; o.dt = v[1]; o.sp = v[2]; o.tr = v[3]; o.tj = v[4]; o.first = n == 0;
            if (n == 0) est.reset(o);
            const double u = est.step(o).tj_sp;
            // 列：wall,xr,b,tau,tau_ekf,k,p_up,g_sp,to_eff,u
            const double got[10] = {est.wall(), est.xr(), est.b(), est.tau(), est.tau_ekf(), est.k(), est.p_up(), est.g_sp(), est.to_eff(), u};
            const double tol[10] = {2e-9, 2e-9, 2e-9, 1e-6, 1e-6, 2e-9, 2e-9, 2e-9, 1e-6, 2e-9};
            for (int i = 0; i < 10; i++) {
                const double e = std::fabs(got[i] - v[5 + i]);
                const double rel = e / tol[i];
                if (rel > fworst) fworst = rel;
                if (e > tol[i] && bad < 3) { std::printf("  %s 第 %ld 拍 第 %d 列：算出 %.9f，应为 %.9f\n", argv[a], n, i, got[i], v[5 + i]); }
                if (e > tol[i]) bad++;
            }
            n++;
        }
        files++; rows += n;
        if (bad) bad_files++;
        if (fworst > worst) worst = fworst;
        std::printf("%s %s  %ld 拍，最大偏差 = 容差的 %.2f 倍\n", bad ? "不一致" : "一致  ", argv[a], n, fworst);
    }
    std::printf("\n%d 场 %ld 拍，%d 场不一致\n", files, rows, bad_files);
    return bad_files ? 1 : 0;
}
