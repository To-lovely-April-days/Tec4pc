// 读不到的设备先晾一会儿再问。
//
// 一次读不到要等满超时再重试：500 ms × 3 次 + 两次 200 ms 退避 ≈ 2 秒。九台设备里有三台没接
// （现场常事：线没插、串口配错、设备没上电），一拍就白等六秒——控温循环赶不上点，界面上点什么
// 都像死了（用户 2026-11-26 在现场遇到的卡顿）。
//
// 所以：连着读不到几次就认定它这会儿不在，之后一段时间不再去问，直接报「上次为什么读不到」。
// 期间它要是又接上了，晾够时间自然会再试一次，自己就恢复了。
// 注意这不改变「读不到」这件事本身：调用方拿到的还是同一个异常，只是不用每拍都等那两秒。
#pragma once
#include <chrono>
#include <string>

namespace tec {

class Flaky {
public:
    Flaky(int strikes = 3, double rest_s = 10.0) : strikes_(strikes), rest_s_(rest_s) {}

    // 这会儿要不要跳过它（跳过时 why() 是上一次读不到的原因）
    bool resting() const { return fails_ >= strikes_ && now() < quiet_until_; }
    const std::string& why() const { return why_; }

    void ok() { fails_ = 0; why_.clear(); }
    void failed(const std::string& why) {
        why_ = why;
        if (++fails_ >= strikes_) quiet_until_ = now() + rest_s_;
    }

private:
    static double now() {
        return std::chrono::duration<double>(std::chrono::steady_clock::now().time_since_epoch()).count();
    }
    int strikes_;
    double rest_s_;
    int fails_ = 0;
    double quiet_until_ = 0;
    std::string why_;
};

}  // namespace tec
