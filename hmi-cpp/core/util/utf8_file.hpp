// 按路径开文件，中文路径也开得了。
//
// Windows 上 std::fopen / std::ifstream 收窄字符路径时，是按**系统本地编码**（简体中文机器上是 GBK）去解释的，
// 而我们程序里的路径一律是 UTF-8。路径里只要有一个汉字就对不上，表现是「文件明明在，程序说找不到」——
// 用户 2026-11-25 踩到两次：仓库放在 D:\Project\四工位…\ 底下，界面一个字都画不出来（字体和图片都开不了）。
// 每一炉的记录文件夹名带「通道」、增益表的备份叫「.原始备份」，在 Windows 上同样写不进去。
//
// 这里统一走 std::filesystem::path：它在 Windows 上内部存的就是宽字符，交给 _wfopen 就不经过本地编码那一道。
// std::ifstream / std::ofstream 直接收 fs::path 也是对的，所以那些地方只要把字符串包成 fs::u8path 即可。
#pragma once

#include <cstdio>
#include <cstring>
#include <filesystem>
#include <string>

namespace tec {

inline std::FILE* open_file(const std::filesystem::path& p, const char* mode) {
#ifdef _WIN32
    const std::wstring wmode(mode, mode + std::strlen(mode));
    return _wfopen(p.c_str(), wmode.c_str());
#else
    return std::fopen(p.c_str(), mode);
#endif
}

/// 路径是 UTF-8 字符串时用这个
inline std::FILE* open_file_u8(const std::string& utf8_path, const char* mode) {
    return open_file(std::filesystem::u8path(utf8_path), mode);
}

}  // namespace tec
