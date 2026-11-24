#include "drives.hpp"
#include <filesystem>
#ifdef _WIN32
#define NOMINMAX
#include <windows.h>
#endif

namespace fs = std::filesystem;

namespace tec {

std::vector<std::string> removable_drives() {
    std::vector<std::string> out;
#ifdef _WIN32
    char buf[512];
    const DWORD n = GetLogicalDriveStringsA(sizeof buf, buf);
    for (const char* p = buf; n > 0 && *p; p += std::strlen(p) + 1)
        if (GetDriveTypeA(p) == DRIVE_REMOVABLE) out.emplace_back(p);
#else
    // 开发机（Mac / Linux）上：挂在 /Volumes 或 /media、/run/media 下面的外接盘。系统盘自己在 /Volumes 里是个指向 / 的链接，跳过
    std::error_code ec;
    for (const char* root : {"/Volumes", "/media", "/run/media"}) {
        if (!fs::is_directory(root, ec)) continue;
        for (auto& e : fs::directory_iterator(root, ec)) {
            if (e.is_symlink(ec) || !e.is_directory(ec)) continue;
            out.push_back(e.path().u8string());
        }
    }
#endif
    return out;
}

int copy_tree(const std::string& src, const std::string& dst, std::string* why) {
    std::error_code ec;
    const fs::path from = fs::u8path(src), to = fs::u8path(dst);
    if (!fs::is_directory(from, ec)) { if (why) *why = "没有可拷的东西：还没导出过"; return -1; }
    fs::create_directories(to, ec);
    if (ec) { if (why) *why = "在目标盘上建不了文件夹：" + ec.message(); return -1; }
    int n = 0;
    for (auto it = fs::recursive_directory_iterator(from, ec); !ec && it != fs::recursive_directory_iterator(); it.increment(ec)) {
        const fs::path rel = fs::relative(it->path(), from, ec);
        if (it->is_directory(ec)) { fs::create_directories(to / rel, ec); continue; }
        fs::create_directories((to / rel).parent_path(), ec);
        std::error_code ce;
        fs::copy_file(it->path(), to / rel, fs::copy_options::overwrite_existing, ce);
        if (ce) { if (why) *why = "拷到一半出错（盘满了，或者被拔掉了）：" + ce.message(); return -1; }
        n++;
    }
    return n;
}

}  // namespace tec
