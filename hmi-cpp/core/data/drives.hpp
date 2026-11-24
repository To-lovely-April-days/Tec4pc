// 找插在这台电脑上的 U 盘（可移动盘），和把一个文件夹整个拷过去。触摸屏上「拷到 U 盘」用。
#pragma once
#include <string>
#include <vector>

namespace tec {

// 现在插着的可移动盘的根目录（Windows 上形如 "E:\\"）。一个都没有就是空表
std::vector<std::string> removable_drives();

// 把 src 文件夹里的东西整个拷到 dst 下面（dst 不存在会建）。返回拷了多少个文件；出错返回 −1，why 里是原因
int copy_tree(const std::string& src, const std::string& dst, std::string* why = nullptr);

}  // namespace tec
