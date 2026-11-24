// 对外的 TCP 服务：一行一条 json 命令，回一行 json。PC 工作站和本机的界面程序都连它。
// 自己用系统的套接字写的（Windows 上是 Winsock），不依赖第三方网络库。每个连进来的客户端一个线程——
// 连接数很少（界面 1 个 + PC 1、2 个），这样最简单，也最不容易出怪问题。
#pragma once
#include <atomic>
#include <functional>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

namespace tec {

// 这台机器对外的那个 IPv4 地址（界面上「本机地址」那一行用）。没连网时返回空串
std::string local_ip();

struct ClientInfo { std::string address; double connected_s = 0; long rx = 0, tx = 0; };

class TcpServer {
public:
    // handler(一行命令, 客户端是不是本机) → 一行应答。会在各客户端自己的线程里被调，handler 要自己保证线程安全
    using Handler = std::function<std::string(const std::string& line, bool local)>;
    TcpServer(int port, Handler h, int idle_timeout_s = 10);
    ~TcpServer();
    bool start(std::string* error = nullptr);     // 开始监听。端口被占用等返回 false
    void stop();
    int port() const { return port_; }
    std::vector<ClientInfo> clients() const;

private:
    void accept_loop();
    void client_loop(long long sock, std::string addr, bool local, size_t slot);
    int port_, idle_timeout_s_;
    Handler handler_;
    long long listen_ = -1;
    std::atomic<bool> quit_{false};
    std::thread accept_thread_;
    mutable std::mutex m_;
    struct Slot { bool used = false; ClientInfo info; long long t0 = 0; long long sock = -1; std::thread th; };
    std::vector<Slot> slots_;
};

// 测试和小工具用的客户端：连上、发一行、收一行
class TcpClient {
public:
    ~TcpClient();
    bool connect(const std::string& host, int port, int timeout_ms = 2000);
    bool request(const std::string& line, std::string& reply, int timeout_ms = 3000);
    void close();

private:
    long long sock_ = -1;
    std::string buf_;
};

}  // namespace tec
