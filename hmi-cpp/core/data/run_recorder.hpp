// 数据记录和导出。一炉（一个通道的一次运行）一个文件夹，里面是纯文本的 csv 和一份 json：
//   <数据目录>/runs/<开始时间>-通道n-<名字>/meta.json  samples.csv  events.csv  steps.csv
// 为什么不用数据库：这些文件断电只会丢最后几行，不会整个坏掉；拿 U 盘拷走在哪台电脑上都能直接打开。
// 采样固定每秒记一行；导出时再按 1 / 10 / 60 s 抽稀。
#pragma once
#include <cstdio>
#include <optional>
#include <string>
#include <vector>

namespace tec {

struct Sample {
    double t_s = 0;                // 这一炉开始以来的秒数
    double sp = 0, sp_now = 0;
    std::optional<double> tr;
    double tj = 0, tj_sp = 0, duty = 0;
    char side = 'H';               // H 加热棒 / C TEC
    double stir_set = 0, stir_rpm = 0;
    std::optional<double> ph;
    double dosed_ml = 0;
    int step = 0;                  // 第几步（从 1 数）；不在序列里是 0
};

struct RunInfo {
    std::string id;                // 文件夹名
    std::string name;
    int channel = 1;
    bool running = false;
    double duration_s = 0;
    std::string started;           // 「2026-11-24 10:15:00」
};

class RunRecorder {
public:
    explicit RunRecorder(const std::string& data_dir) : dir_(data_dir) {}
    ~RunRecorder() { end(); }
    bool begin(const std::string& name, int channel);     // 建文件夹、写表头。返回 false = 建不了（盘满、没权限）
    void sample(const Sample& s);
    void event(double t_s, const std::string& kind, const std::string& text);   // kind：操作 / 报警 / 步骤 / 设备
    void step(int index, const std::string& what, double start_s, double end_s, const std::string& status);
    void end();
    bool active() const { return f_samples_ != nullptr; }
    std::string id() const { return id_; }

    // 下面几个不依赖正在记的那一炉
    static std::vector<RunInfo> list(const std::string& data_dir);                 // 最新的在前
    // 把几炉导出到 out_dir：每炉一个文件夹，三个 csv（执行记录、事件日志、采样宽表），带 BOM，Excel 直接打开不乱码
    // from_s / to_s：只导这一段（从这一炉开始算的秒数）；to_s < 0 = 到结尾。给了范围时文件夹名后面带上这一段，不会盖掉整炉导出的那份
    static int export_runs(const std::string& data_dir, const std::vector<std::string>& ids, int interval_s, const std::string& out_dir, double from_s = 0, double to_s = -1);

private:
    void write_meta(bool running);
    std::string dir_, id_, name_, started_;
    int channel_ = 1;
    double last_t_ = 0;
    std::FILE *f_samples_ = nullptr, *f_events_ = nullptr, *f_steps_ = nullptr;
};

}  // namespace tec
