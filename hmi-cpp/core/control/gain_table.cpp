#include "gain_table.hpp"
#include <filesystem>
#include <algorithm>
#include <cmath>
#include <cstdio>
#include <fstream>
#include <sstream>

namespace tec {

GainTable::GainTable(std::vector<GainRow> rows) : rows_(std::move(rows)) {
    std::sort(rows_.begin(), rows_.end(), [](const GainRow& a, const GainRow& b) { return a.temp < b.temp; });
}

GainTable GainTable::load(const std::string& path) {
    std::ifstream in(std::filesystem::u8path(path));
    std::vector<GainRow> rows;
    std::string line;
    while (std::getline(in, line)) {
        while (!line.empty() && (line.back() == '\r' || line.back() == ' ')) line.pop_back();
        if (line.empty() || line[0] == '#') continue;
        if (!(std::isdigit(static_cast<unsigned char>(line[0])) || line[0] == '-' || line[0] == '+' || line[0] == '.')) continue;   // 表头
        std::vector<std::string> c;
        std::stringstream ss(line);
        std::string cell;
        while (std::getline(ss, cell, ',')) c.push_back(cell);
        if (!line.empty() && line.back() == ',') c.emplace_back();
        if (c.size() < 4) continue;
        GainRow r;
        r.temp = std::stod(c[0]); r.kp = std::stod(c[1]); r.ki = std::stod(c[2]); r.kd = std::stod(c[3]);
        if (c.size() > 10 && !c[10].empty()) r.steady_bias = std::stod(c[10]);
        if (c.size() > 11 && !c[11].empty()) r.heat_ratio = std::stod(c[11]);
        rows.push_back(r);
    }
    return GainTable(std::move(rows));
}

bool GainTable::save(const std::string& path) const {
    {
        namespace fs = std::filesystem;
        std::ifstream old(fs::u8path(path), std::ios::binary);
        std::ifstream bak(fs::u8path(path + ".原始备份"), std::ios::binary);
        if (old && !bak) { std::ofstream o(fs::u8path(path + ".原始备份"), std::ios::binary); o << old.rdbuf(); }
    }
    // 先写临时文件再改名：写到一半断电，原来那份还在
    const std::string tmp = path + ".tmp";
    {
        std::ofstream o(std::filesystem::u8path(tmp), std::ios::binary);
        if (!o) return false;
        o << "温度(℃),Kp,Ki,Kd,Ku,Tu(s),外环Kp,外环Ki,外环Kd,偏置上限(℃),稳态偏置(℃),加热比\n";
        o << "# 这张表是触摸屏 / PC 端改过以后由控制核心存的；原来的文件在同目录的「.原始备份」里\n";
        auto num = [](double v) { std::ostringstream ss; ss.precision(10); ss << v; return ss.str(); };
        for (auto& r : rows_)
            o << num(r.temp) << ',' << num(r.kp) << ',' << num(r.ki) << ',' << num(r.kd) << ",,,,,,," << (r.steady_bias ? num(*r.steady_bias) : "") << ',' << (r.heat_ratio ? num(*r.heat_ratio) : "") << "\n";
        if (!o) return false;
    }
    // 同样要走 filesystem：窄字符那套在 Windows 上按本地编码解释路径，中文路径会对不上
    namespace fs = std::filesystem;
    std::error_code ec;
    fs::remove(fs::u8path(path), ec);
    fs::rename(fs::u8path(tmp), fs::u8path(path), ec);
    return !ec;
}

namespace {
// 在给定的行里按温度线性插值；两头之外取最近一行的值
template <class Pick>
double interp(const std::vector<const GainRow*>& rows, double t, Pick pick) {
    if (rows.size() == 1 || t <= rows.front()->temp) return pick(*rows.front());
    if (t >= rows.back()->temp) return pick(*rows.back());
    for (size_t i = 1; i < rows.size(); i++) {
        const GainRow& lo = *rows[i - 1];
        const GainRow& hi = *rows[i];
        if (t > hi.temp) continue;
        const double span = hi.temp - lo.temp;
        const double f = span > 1e-9 ? (t - lo.temp) / span : 0.0;
        return pick(lo) + (pick(hi) - pick(lo)) * f;
    }
    return pick(*rows.back());
}
}  // namespace

InnerGains GainTable::gains_at(double temp) const {
    if (rows_.empty()) return {};
    std::vector<const GainRow*> all;
    for (auto& r : rows_) all.push_back(&r);
    return {interp(all, temp, [](const GainRow& r) { return r.kp; }), interp(all, temp, [](const GainRow& r) { return r.ki; }),
            interp(all, temp, [](const GainRow& r) { return r.kd; })};
}

std::optional<double> GainTable::heat_ratio_at(double temp) const {
    std::vector<const GainRow*> rows;
    for (auto& r : rows_) if (r.heat_ratio && *r.heat_ratio > 0) rows.push_back(&r);
    if (rows.empty()) return std::nullopt;
    return interp(rows, temp, [](const GainRow& r) { return *r.heat_ratio; });
}

std::optional<double> GainTable::steady_bias_at(double temp) const {
    std::vector<const GainRow*> rows;
    for (auto& r : rows_) if (r.steady_bias) rows.push_back(&r);
    if (rows.empty()) return std::nullopt;
    if (temp >= rows.front()->temp && temp <= rows.back()->temp) return interp(rows, temp, [](const GainRow& r) { return *r.steady_bias; });
    const GainRow* near = rows.front();
    for (auto* r : rows) if (std::fabs(r->temp - temp) < std::fabs(near->temp - temp)) near = r;
    if (std::fabs(near->temp - temp) > 80.0) return std::nullopt;
    if (rows.size() == 1) return *near->steady_bias + 0.10 * (temp - near->temp);
    double mt = 0, mb = 0;
    for (auto* r : rows) { mt += r->temp; mb += *r->steady_bias; }
    mt /= rows.size(); mb /= rows.size();
    double num = 0, den = 0;
    for (auto* r : rows) { num += (r->temp - mt) * (*r->steady_bias - mb); den += (r->temp - mt) * (r->temp - mt); }
    const double slope = den > 1e-9 ? std::min(0.30, std::max(0.02, num / den)) : 0.10;
    return *near->steady_bias + slope * (temp - near->temp);
}

}  // namespace tec
