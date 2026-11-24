// 增益表：按温度分行，行与行之间线性插值。只有一张（双向挡，加热制冷共用，用户 2026-11-24 定的）。
// 文件格式沿用原来 C# 程序的（# 开头的行跳过）：
//   温度,Kp,Ki,Kd,Ku,Tu,外环Kp,外环Ki,外环Kd,偏置上限,稳态偏置,加热比
// 「稳态偏置」那一列只当起点用（用户 2026-11-10 定的：料会换，表不是真值，开控以后以这一炉的实测为准）。
#pragma once
#include <optional>
#include <string>
#include <vector>

namespace tec {

struct GainRow {
    double temp = 0, kp = 0, ki = 0, kd = 0;
    std::optional<double> steady_bias;   // 稳态偏置（维持温差的起点），没填就空着
    std::optional<double> heat_ratio;    // 加热比：TEC 那半轴的输出放大多少倍
};

struct InnerGains { double kp = 0, ki = 0, kd = 0; };

class GainTable {
public:
    GainTable() = default;
    explicit GainTable(std::vector<GainRow> rows);
    static GainTable load(const std::string& path);      // 读不到文件返回空表
    // 存成同一种格式的文件。这里用不到的那几列（Ku、Tu、外环三项、偏置上限）留空；
    // 第一次覆盖前把原来的文件另存一份「.原始备份」，里面的注释和那几列还在
    bool save(const std::string& path) const;
    bool empty() const { return rows_.empty(); }
    const std::vector<GainRow>& rows() const { return rows_; }
    InnerGains gains_at(double temp) const;
    std::optional<double> heat_ratio_at(double temp) const;
    // 维持温差的起点。表内插值；表外按各行的最小二乘斜率（夹在 0.02…0.30 K/K）从最近一行往外推，最远推 80 K，再远就不给
    std::optional<double> steady_bias_at(double temp) const;

private:
    std::vector<GainRow> rows_;
};

}  // namespace tec
