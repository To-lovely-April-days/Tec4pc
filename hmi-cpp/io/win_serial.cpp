#ifdef _WIN32
#include "win_serial.hpp"
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <stdexcept>
#include "../core/util/text_error.hpp"      // 报错要交「原句 + 值」，界面才翻得了

namespace tec {

namespace {

std::string last_error_text() {
    const DWORD code = GetLastError();
    // 要走宽字符那套：窄字符版给回来的是系统本地编码（简体中文机器上是 GBK），
    // 而程序里的字串一律 UTF-8，直接拼进去打到控制台就是一串乱码（用户 2026-11-25 截到的「系统错误 2: єT◆X…」）
    wchar_t wbuf[256] = {0};
    FormatMessageW(FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS, nullptr, code, 0, wbuf, 255, nullptr);
    std::wstring w(wbuf);
    while (!w.empty() && (w.back() == L'\r' || w.back() == L'\n' || w.back() == L' ')) w.pop_back();      // 它末尾会带回车换行
    std::string u8;
    const int n = WideCharToMultiByte(CP_UTF8, 0, w.c_str(), static_cast<int>(w.size()), nullptr, 0, nullptr, nullptr);
    if (n > 0) { u8.resize(static_cast<size_t>(n)); WideCharToMultiByte(CP_UTF8, 0, w.c_str(), static_cast<int>(w.size()), u8.data(), n, nullptr, nullptr); }
    return "（系统错误 " + std::to_string(code) + "：" + u8 + "）";
}

class WinSerial : public SerialTransport {
public:
    explicit WinSerial(const SerialConfig& cfg) : cfg_(cfg) {}
    ~WinSerial() override { close(); }
    bool is_open() const override { return h_ != INVALID_HANDLE_VALUE; }

    void open() override {
        if (is_open()) return;
        // COM10 以上必须写成 \\.\COM10，COM1–9 这么写也行，所以统一加前缀
        const std::string path = "\\\\.\\" + cfg_.port;
        h_ = CreateFileA(path.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr);
        if (h_ == INVALID_HANDLE_VALUE) throw TextError("打不开串口 {0}{1}", {cfg_.port, last_error_text()});
        DCB dcb{};
        dcb.DCBlength = sizeof dcb;
        if (!GetCommState(h_, &dcb)) { const auto e = last_error_text(); close(); throw TextError("读不到串口 {0} 的参数{1}", {cfg_.port, e}); }
        dcb.BaudRate = static_cast<DWORD>(cfg_.baud);
        dcb.ByteSize = 8;
        dcb.Parity = cfg_.parity == 'E' ? EVENPARITY : cfg_.parity == 'O' ? ODDPARITY : NOPARITY;
        dcb.fParity = cfg_.parity != 'N';
        dcb.StopBits = cfg_.stop_bits == 2 ? TWOSTOPBITS : ONESTOPBIT;
        dcb.fBinary = TRUE;
        // 485 转换器不用流控；这几项不关的话有的驱动会等一个永远不来的握手信号
        dcb.fOutxCtsFlow = FALSE; dcb.fOutxDsrFlow = FALSE; dcb.fDsrSensitivity = FALSE;
        dcb.fOutX = FALSE; dcb.fInX = FALSE;
        dcb.fDtrControl = DTR_CONTROL_ENABLE; dcb.fRtsControl = RTS_CONTROL_ENABLE;
        if (!SetCommState(h_, &dcb)) { const auto e = last_error_text(); close(); throw TextError("设不了串口 {0} 的参数{1}", {cfg_.port, e}); }
        SetupComm(h_, 4096, 4096);
        PurgeComm(h_, PURGE_RXCLEAR | PURGE_TXCLEAR);
    }

    void close() override {
        if (h_ != INVALID_HANDLE_VALUE) { CloseHandle(h_); h_ = INVALID_HANDLE_VALUE; }
    }

    void discard_input() override { if (is_open()) PurgeComm(h_, PURGE_RXCLEAR); }

    void write(const uint8_t* data, size_t n) override {
        DWORD done = 0;
        if (!WriteFile(h_, data, static_cast<DWORD>(n), &done, nullptr) || done != n) throw TextError("串口 {0} 写不出去{1}", {cfg_.port, last_error_text()});
    }

    int read(uint8_t* buf, size_t n, int timeout_ms) override {
        // 「有字节就立刻返回，最多等 timeout_ms」：ReadIntervalTimeout = MAXDWORD 且 ReadTotalTimeoutMultiplier = MAXDWORD 时，
        // ReadFile 在收到第一个字节后马上返回，一个都没有就等满 ReadTotalTimeoutConstant
        COMMTIMEOUTS to{};
        to.ReadIntervalTimeout = MAXDWORD;
        to.ReadTotalTimeoutMultiplier = MAXDWORD;
        to.ReadTotalTimeoutConstant = static_cast<DWORD>(timeout_ms < 1 ? 1 : timeout_ms);
        to.WriteTotalTimeoutConstant = 500;
        SetCommTimeouts(h_, &to);
        DWORD got = 0;
        if (!ReadFile(h_, buf, static_cast<DWORD>(n), &got, nullptr)) throw TextError("串口 {0} 读出错{1}", {cfg_.port, last_error_text()});
        return static_cast<int>(got);
    }

private:
    SerialConfig cfg_;
    HANDLE h_ = INVALID_HANDLE_VALUE;
};

}  // namespace

// 列串口的实现搬去 io/serial_ports.cpp 了（那边还要取 USB 身份）。
// QueryDosDevice 挨个问 COM1..COM256 这条退路也搬过去了，当 SetupAPI 一个口都给不出时用。

std::unique_ptr<SerialTransport> make_win_serial(const SerialConfig& cfg) { return std::make_unique<WinSerial>(cfg); }

}  // namespace tec
#endif
