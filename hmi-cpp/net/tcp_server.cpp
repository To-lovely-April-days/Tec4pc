#include "tcp_server.hpp"
#include <chrono>
#include <cstring>

#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <winsock2.h>
#include <ws2tcpip.h>
typedef SOCKET sock_t;
static void sock_close(long long s) { closesocket(static_cast<SOCKET>(s)); }
static void net_init() { static bool done = false; if (!done) { WSADATA w; WSAStartup(MAKEWORD(2, 2), &w); done = true; } }
#else
#include <arpa/inet.h>
#include <netinet/in.h>
#include <netinet/tcp.h>
#include <sys/select.h>
#include <sys/socket.h>
#include <unistd.h>
typedef int sock_t;
static void sock_close(long long s) { ::close(static_cast<int>(s)); }
static void net_init() {}
#ifndef MSG_NOSIGNAL
#define MSG_NOSIGNAL 0
#endif
#endif

namespace tec {

namespace {
long long now_ms() { return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now().time_since_epoch()).count(); }
// 等这个套接字可读，最多等 ms 毫秒。返回 1 可读 / 0 超时 / −1 出错
int wait_readable(long long s, int ms) {
    fd_set fds; FD_ZERO(&fds); FD_SET(static_cast<sock_t>(s), &fds);
    timeval tv; tv.tv_sec = ms / 1000; tv.tv_usec = (ms % 1000) * 1000;
    return select(static_cast<int>(s) + 1, &fds, nullptr, nullptr, &tv);
}
bool send_all(long long s, const std::string& data) {
    size_t off = 0;
    while (off < data.size()) {
        const auto n = send(static_cast<sock_t>(s), data.data() + off, static_cast<int>(data.size() - off), MSG_NOSIGNAL);
        if (n <= 0) return false;
        off += static_cast<size_t>(n);
    }
    return true;
}
}  // namespace

TcpServer::TcpServer(int port, Handler h, int idle_timeout_s) : port_(port), idle_timeout_s_(idle_timeout_s), handler_(std::move(h)) {}
TcpServer::~TcpServer() { stop(); }

bool TcpServer::start(std::string* error) {
    net_init();
    const sock_t s = socket(AF_INET, SOCK_STREAM, 0);
    int yes = 1;
    setsockopt(s, SOL_SOCKET, SO_REUSEADDR, reinterpret_cast<const char*>(&yes), sizeof yes);
#ifdef SO_NOSIGPIPE
    setsockopt(s, SOL_SOCKET, SO_NOSIGPIPE, reinterpret_cast<const char*>(&yes), sizeof yes);
#endif
    sockaddr_in a{};
    a.sin_family = AF_INET; a.sin_addr.s_addr = htonl(INADDR_ANY); a.sin_port = htons(static_cast<unsigned short>(port_));
    if (bind(s, reinterpret_cast<sockaddr*>(&a), sizeof a) != 0 || listen(s, 8) != 0) {
        if (error) *error = "端口 " + std::to_string(port_) + " 开不了监听（多半是被别的程序占着）";
        sock_close(s);
        return false;
    }
    if (port_ == 0) { socklen_t len = sizeof a; getsockname(s, reinterpret_cast<sockaddr*>(&a), &len); port_ = ntohs(a.sin_port); }   // 端口给 0 = 让系统挑一个（测试用）
    listen_ = s;
    quit_ = false;
    accept_thread_ = std::thread([this] { accept_loop(); });
    return true;
}

void TcpServer::stop() {
    if (quit_.exchange(true)) return;
    if (listen_ >= 0) { sock_close(listen_); listen_ = -1; }
    if (accept_thread_.joinable()) accept_thread_.join();
    std::vector<std::thread> ths;
    { std::lock_guard<std::mutex> g(m_); for (auto& sl : slots_) { if (sl.used && sl.sock >= 0) sock_close(sl.sock); if (sl.th.joinable()) ths.push_back(std::move(sl.th)); } }
    for (auto& t : ths) t.join();
}

std::vector<ClientInfo> TcpServer::clients() const {
    std::lock_guard<std::mutex> g(m_);
    std::vector<ClientInfo> out;
    for (auto& sl : slots_) if (sl.used) { ClientInfo c = sl.info; c.connected_s = (now_ms() - sl.t0) / 1000.0; out.push_back(c); }
    return out;
}

void TcpServer::accept_loop() {
    while (!quit_) {
        if (wait_readable(listen_, 200) <= 0) continue;
        sockaddr_in a{}; socklen_t len = sizeof a;
        const sock_t c = accept(static_cast<sock_t>(listen_), reinterpret_cast<sockaddr*>(&a), &len);
        if (static_cast<long long>(c) < 0) continue;
        int yes = 1;
        setsockopt(c, IPPROTO_TCP, TCP_NODELAY, reinterpret_cast<const char*>(&yes), sizeof yes);   // 一问一答的小包，不要攒着发
#ifdef SO_NOSIGPIPE
        setsockopt(c, SOL_SOCKET, SO_NOSIGPIPE, reinterpret_cast<const char*>(&yes), sizeof yes);
#endif
        char ip[64] = {0};
        inet_ntop(AF_INET, &a.sin_addr, ip, sizeof ip);
        const bool local = ntohl(a.sin_addr.s_addr) == INADDR_LOOPBACK;
        std::lock_guard<std::mutex> g(m_);
        size_t slot = slots_.size();
        for (size_t i = 0; i < slots_.size(); i++) if (!slots_[i].used) { if (slots_[i].th.joinable()) slots_[i].th.join(); slot = i; break; }
        if (slot == slots_.size()) slots_.emplace_back();
        Slot& sl = slots_[slot];
        sl.used = true; sl.info = ClientInfo(); sl.info.address = ip; sl.t0 = now_ms(); sl.sock = static_cast<long long>(c);
        sl.th = std::thread([this, c, ip, local, slot] { client_loop(static_cast<long long>(c), ip, local, slot); });
    }
}

void TcpServer::client_loop(long long sock, std::string, bool local, size_t slot) {
    std::string buf;
    char tmp[4096];
    long long last_rx = now_ms();
    while (!quit_) {
        const int w = wait_readable(sock, 200);
        if (w < 0) break;
        if (w == 0) {
            // 太久没动静就当它掉线了：网线被拔的时候系统不一定马上告诉我们，不能让一个死连接一直占着
            if (idle_timeout_s_ > 0 && now_ms() - last_rx > idle_timeout_s_ * 1000LL) break;
            continue;
        }
        const auto n = recv(static_cast<sock_t>(sock), tmp, sizeof tmp, 0);
        if (n <= 0) break;
        last_rx = now_ms();
        buf.append(tmp, static_cast<size_t>(n));
        if (buf.size() > (1u << 20)) break;         // 一行超过 1 MB：不是我们的客户端
        size_t pos;
        bool ok = true;
        while (ok && (pos = buf.find('\n')) != std::string::npos) {
            std::string line = buf.substr(0, pos);
            buf.erase(0, pos + 1);
            if (!line.empty() && line.back() == '\r') line.pop_back();
            if (line.empty()) continue;
            const std::string reply = handler_(line, local) + "\n";
            ok = send_all(sock, reply);
            std::lock_guard<std::mutex> g(m_);
            slots_[slot].info.rx++; slots_[slot].info.tx++;
        }
        if (!ok) break;
    }
    sock_close(sock);
    std::lock_guard<std::mutex> g(m_);
    slots_[slot].used = false; slots_[slot].sock = -1;
}

TcpClient::~TcpClient() { close(); }
void TcpClient::close() { if (sock_ >= 0) { sock_close(sock_); sock_ = -1; } buf_.clear(); }

bool TcpClient::connect(const std::string& host, int port, int) {
    net_init();
    close();
    const sock_t s = socket(AF_INET, SOCK_STREAM, 0);
    sockaddr_in a{};
    a.sin_family = AF_INET; a.sin_port = htons(static_cast<unsigned short>(port));
    if (inet_pton(AF_INET, host.c_str(), &a.sin_addr) != 1 || ::connect(s, reinterpret_cast<sockaddr*>(&a), sizeof a) != 0) { sock_close(s); return false; }
    int yes = 1;
    setsockopt(s, IPPROTO_TCP, TCP_NODELAY, reinterpret_cast<const char*>(&yes), sizeof yes);
#ifdef SO_NOSIGPIPE
    setsockopt(s, SOL_SOCKET, SO_NOSIGPIPE, reinterpret_cast<const char*>(&yes), sizeof yes);
#endif
    sock_ = static_cast<long long>(s);
    return true;
}

bool TcpClient::request(const std::string& line, std::string& reply, int timeout_ms) {
    if (sock_ < 0 || !send_all(sock_, line + "\n")) return false;
    const long long deadline = now_ms() + timeout_ms;
    char tmp[4096];
    for (;;) {
        const size_t pos = buf_.find('\n');
        if (pos != std::string::npos) { reply = buf_.substr(0, pos); buf_.erase(0, pos + 1); return true; }
        const long long left = deadline - now_ms();
        if (left <= 0 || wait_readable(sock_, static_cast<int>(left)) <= 0) return false;
        const auto n = recv(static_cast<sock_t>(sock_), tmp, sizeof tmp, 0);
        if (n <= 0) return false;
        buf_.append(tmp, static_cast<size_t>(n));
    }
}

// 取本机对外的地址：拿一个 UDP 套接字「假装」要连外面的一个地址（不会真发任何东西），看系统给它挑了哪个本机地址
std::string local_ip() {
    net_init();
    const long long s = static_cast<long long>(::socket(AF_INET, SOCK_DGRAM, 0));
    if (s < 0) return "";
    sockaddr_in to{};
    to.sin_family = AF_INET; to.sin_port = htons(53);
    inet_pton(AF_INET, "192.168.255.254", &to.sin_addr);
    std::string out;
    if (::connect(s, reinterpret_cast<sockaddr*>(&to), sizeof to) == 0) {
        sockaddr_in me{};
        socklen_t len = sizeof me;
        if (::getsockname(s, reinterpret_cast<sockaddr*>(&me), &len) == 0) { char b[64] = {}; if (inet_ntop(AF_INET, &me.sin_addr, b, sizeof b)) out = b; }
    }
    sock_close(s);
    return out == "0.0.0.0" || out.rfind("127.", 0) == 0 ? "" : out;
}

}  // namespace tec
