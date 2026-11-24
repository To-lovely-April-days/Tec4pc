#ifdef _WIN32
#include "win_serial.hpp"
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <stdexcept>

namespace tec {

namespace {

std::string last_error_text() {
    const DWORD code = GetLastError();
    char buf[256] = {0};
    FormatMessageA(FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS, nullptr, code, 0, buf, sizeof buf - 1, nullptr);
    return "（系统错误 " + std::to_string(code) + "：" + buf + "）";
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
        if (h_ == INVALID_HANDLE_VALUE) throw std::runtime_error("打不开串口 " + cfg_.port + last_error_text());
        DCB dcb{};
        dcb.DCBlength = sizeof dcb;
        if (!GetCommState(h_, &dcb)) { const auto e = last_error_text(); close(); throw std::runtime_error("读不到串口 " + cfg_.port + " 的参数" + e); }
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
        if (!SetCommState(h_, &dcb)) { const auto e = last_error_text(); close(); throw std::runtime_error("设不了串口 " + cfg_.port + " 的参数" + e); }
        SetupComm(h_, 4096, 4096);
        PurgeComm(h_, PURGE_RXCLEAR | PURGE_TXCLEAR);
    }

    void close() override {
        if (h_ != INVALID_HANDLE_VALUE) { CloseHandle(h_); h_ = INVALID_HANDLE_VALUE; }
    }

    void discard_input() override { if (is_open()) PurgeComm(h_, PURGE_RXCLEAR); }

    void write(const uint8_t* data, size_t n) override {
        DWORD done = 0;
        if (!WriteFile(h_, data, static_cast<DWORD>(n), &done, nullptr) || done != n) throw std::runtime_error("串口 " + cfg_.port + " 写不出去" + last_error_text());
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
        if (!ReadFile(h_, buf, static_cast<DWORD>(n), &got, nullptr)) throw std::runtime_error("串口 " + cfg_.port + " 读出错" + last_error_text());
        return static_cast<int>(got);
    }

private:
    SerialConfig cfg_;
    HANDLE h_ = INVALID_HANDLE_VALUE;
};

}  // namespace

std::vector<std::string> list_serial_ports() {
    std::vector<std::string> out;
    char target[512];
    for (int i = 1; i <= 256; i++) {
        const std::string name = "COM" + std::to_string(i);
        if (QueryDosDeviceA(name.c_str(), target, sizeof target) != 0) out.push_back(name);
    }
    return out;
}

std::unique_ptr<SerialTransport> make_win_serial(const SerialConfig& cfg) { return std::make_unique<WinSerial>(cfg); }

}  // namespace tec
#endif
