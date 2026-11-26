// 要给人看的报错：一句「原句」和几个值分开留着，不要先拼成一句话。
//
// 为什么非这么做不可：触摸屏有八种语言，界面上每一句都要先拿原句去查词表。
// 拼好的句子（"打不开串口 " + "COM7"）在表里查不到，英文界面上就原样冒一句中文。
// 和报警那一套是同一回事（见 station.hpp 的 Alarm::title_t / args）：
// 核心自己仍旧留一份填好的中文（记录、PC 端照用），另外把原句和值分开交出去，
// 界面按原句查词表、再把值填回去。
//
// 用法：
//     throw TextError("打不开串口 {0}{1}", {port, last_error_text()});
//     ...
//     catch (const std::exception& e) { r = fail(compose("处理命令时出错：{0}", {text_of(e)})); }
#pragma once
#include <exception>
#include <stdexcept>
#include <string>
#include <utility>
#include <vector>

namespace tec {

// 把 {0} {1} … 换成 args 里的值
inline std::string fill_text(const std::string& t, const std::vector<std::string>& args) {
    std::string out = t;
    for (size_t i = 0; i < args.size(); i++) {
        const std::string mark = "{" + std::to_string(i) + "}";
        for (size_t q = out.find(mark); q != std::string::npos; q = out.find(mark, q + args[i].size()))
            out.replace(q, mark.size(), args[i]);
    }
    return out;
}

// 一句话的两件：原句 + 值
struct Text {
    std::string t;
    std::vector<std::string> args;
    std::string filled() const { return fill_text(t, args); }
};

// 一个值（设备名、口名、数）。它本身不是话，所以单独占一个坑，不进原句
inline Text val(const std::string& v) { return Text{"{0}", {v}}; }

// 带「原句 + 值」的异常。what() 还是填好的中文，所以不认得它的地方照旧能用
class TextError : public std::runtime_error {
public:
    explicit TextError(std::string t, std::vector<std::string> args = {})
        : std::runtime_error(fill_text(t, args)), text{std::move(t), std::move(args)} {}
    // 后面再缀一段只给记录看的（原始字节这种）：它不进要翻的那一句。
    // 为什么分开：缀上去以后整句就不是词表里的那一条了，屏上这句话就翻不了；
    // 而原始字节本来也没什么好翻的，留给记录和 PC 端就够了
    TextError(Text x, const std::string& tail)
        : std::runtime_error(x.filled() + tail), text(std::move(x)) {}
    Text text;
};

// 任何异常都能问出这两件：不是 TextError 的，原句就是它的 what()（查不到词表就原样显示）
inline Text text_of(const std::exception& e) {
    if (const auto* te = dynamic_cast<const TextError*>(&e)) return te->text;
    return Text{e.what(), {}};
}

// 把几句话套进一句里（「处理命令时出错：{0}」套住里面那一句）。
// 里面那句自己也可能带占位符，所以要重新编号，不然两边的 {0} 会打架
inline Text compose(const std::string& outer_t, const std::vector<Text>& parts) {
    Text out;
    for (size_t i = 0; i < outer_t.size();) {
        if (outer_t[i] == '{') {
            const size_t e = outer_t.find('}', i);
            if (e != std::string::npos) {
                const std::string num = outer_t.substr(i + 1, e - i - 1);
                bool digits = !num.empty();
                for (char c : num) digits = digits && c >= '0' && c <= '9';
                if (digits) {
                    const size_t k = static_cast<size_t>(std::stoul(num));
                    if (k < parts.size()) {
                        const size_t base = out.args.size();
                        // 把里面那句的 {j} 挪到 {base+j}
                        const std::string& it = parts[k].t;
                        for (size_t a = 0; a < it.size();) {
                            if (it[a] == '{') {
                                const size_t b = it.find('}', a);
                                if (b != std::string::npos) {
                                    const std::string n2 = it.substr(a + 1, b - a - 1);
                                    bool d2 = !n2.empty();
                                    for (char c : n2) d2 = d2 && c >= '0' && c <= '9';
                                    if (d2) {
                                        out.t += "{" + std::to_string(base + std::stoul(n2)) + "}";
                                        a = b + 1;
                                        continue;
                                    }
                                }
                            }
                            out.t += it[a++];
                        }
                        for (const std::string& v : parts[k].args) out.args.push_back(v);
                        i = e + 1;
                        continue;
                    }
                }
            }
        }
        out.t += outer_t[i++];
    }
    return out;
}

}  // namespace tec
