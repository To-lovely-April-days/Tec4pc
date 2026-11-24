# 触摸屏程序（C++）

2026-11-24 起用 C++ 重写的触摸屏程序。跑在 Windows 上，直接连硬件，对外开 TCP 给 PC 工作站连。

## 结构

| 目录 | 是什么 |
|---|---|
| `core/control/` | 控温算法。`outer_loop` 外环（按实测实时算夹套目标，含快速升温）、`inner_loop` 内环 PID + 双向挡输出、`gain_table` 增益表 |
| `core/station/` | `channel_controller`：一个工位的控温主循环，把外环、内环、执行机构串成每 0.5 s 一拍；不碰硬件，读数进、命令出 |
| `core/sequence/` | 任务序列引擎（最多 6 步，八种步骤） |
| `core/data/` | 数据记录和导出（每炉一个文件夹、csv）、配方库、设置存盘（json） |
| `core/app/` | `core_app`：控制核心，一行一条 json 的命令入口；`real_devices`：按设置把真设备建起来 |
| `core/sim/` | 仿真硬件：不接设备也能把整个控制核心跑起来（`--sim`） |
| `net/` | TCP 服务（一行一条 json）和一个小客户端 |
| `apps/tec_core/` | 控制核心主程序（没有界面） |
| `io/` | 串口抽象、假串口（测试用）、Modbus RTU 主站、Windows 真串口（`win_serial`，只在 Windows 下编，没测过） |
| `drivers/` | 各设备的协议层：`rd105` 温控器、`io8r` 继电器模块、`chiller` 冷水机、`yudian` Tr 探头、`bht_ph` pH 电极、`aqmd` 搅拌、`feed` 进料。只有协议，没有上层的轮询和状态机 |
| `tests/` | 对拍测试：`golden_outer`（外环基础部分）、`golden_fast`（快速升温）、`golden_inner`（内环），都是拿 Python 参考实现的逐拍输出核对 |
| `testdata/` | 对拍数据和一张增益表（`gain-table-run1110.csv`，照 2026-11-10 那一炉重建的，不是现场那张） |
| `research/` | 快速升温在试算台上的对比曲线 |
| `ui/` | 触摸屏界面程序（LVGL）。`design/` 是设计稿，`tools/gen.py` 把设计稿翻成界面代码放进 `gen/`，`src/` 是连控制核心、切页面、刷数据的代码 |
| `ui/gen_all.sh` | 把 `ui/design` 里的设计稿全部重新翻成界面代码；加了新页面在脚本里那一行加名字 |
| `ui/tools/gen_pinyin.swift` | 汉字 → 拼音的小工具（用 Mac 系统自带的转换），生成拼音字库和词库时用，产品里不带 |
| `ui/tools/phrases.txt`、`gen_phrases.py` | 文字键盘的词库：清单里一行行写词，脚本配上拼音生成 `ui/src/phrase_dict.inc`。要加词就改清单再跑脚本 |
| `ui/tools/add_polyphones.py` | 给拼音字库 `ui/src/pinyin_dict.inc` 补读音（ü 打 v 的音节、常用多音字）；可以反复跑 |
| `ui/tools/raw2png.py` | Mac 上自查出图：`tec_hmi_2 --shot` 存的是原始画面数据，用它转成 PNG；`--do 动作名` 可以代替手指点一下再出图 |
| `trial_lvgl/` | LVGL 界面试做：把设计稿的一页自动翻成 LVGL 代码，不开窗口直接出图 |
| `third_party/` | 第三方源码和工具，不进仓库 |

## 第三方（都是可闭源商用的宽松许可）

| 名字 | 版本 | 许可 | 放哪 |
|---|---|---|---|
| LVGL | 9.2.2 | MIT | `third_party/lvgl-9.2.2`（官方 tag 的源码包，删了 docs / demos / examples / tests） |
| nlohmann/json | 3.11.3 | MIT | `third_party/nlohmann/json.hpp`（官方发行的单个头文件） |
| CMake | 3.30.5 | BSD | 只是构建工具 |

## 编译和测试

Windows 上从装工具、下载第三方库到编译运行的完整步骤见 [Windows编译说明.txt](Windows编译说明.txt)。

```
cmake -S . -B build
cmake --build build
ctest --test-dir build --output-on-failure
```

## 不接设备试一下

```
./build/tec_core --sim --data ./data --gains testdata/gain-table-run1110.csv
```

然后用任何能连 TCP 的工具连 5020 端口，发一行 `{"cmd":"state"}`。命令清单在 `core/app/core_app.cpp` 里 `handle()` 上面。

## 还没做、没验证过的

- **没在 Windows 上编过、跑过**：窗口和触摸那一段（`ui/src/main.cpp` 里 `_WIN32` 的分支）、Windows 串口（`drivers/win_serial`）都只在这台 Mac 上看过代码，没编译过。
- **没接过真设备**：控温、联锁、冷水机水温（寄存器 0x0007）、U 盘识别都只对着仿真和假设备测过。
- 快速升温在试算台上还没过线（升温 140 场里 32 场冲过 > 0.10 K），用户 2026-11-24 定的是先把整个产品跑起来、上真机取数据再调；加码上限缺省保持 60 不动。
- 用户页（登录、权限）用户说先不做，顶栏那个键留着没接。
- 照明只有状态（`lamp.set`，核心记着开没开），没有接通信；后续接硬件时只动核心这一处。
- 自整定用户说先不做。
- 开机谁来启动 / 守着控制核心（Windows 上的自启和崩了重启）还没定。

## 设备没连上时

温控器或继电器模块没连上（串口配错、线没插）时，`tec_core` 不会退出：它照常运行、照常答命令，只是不收「开始控温 / 开始序列」。触摸屏顶栏会显示红点「设备未连接」，到「设置 → 通信设置」把串口改对，再重启 `tec_core` 就好。

## 界面程序怎么编、怎么跑

界面程序 `tec_hmi` 和控制核心一起由根目录的 CMake 编（前提是 `third_party/lvgl-9.2.2` 在）。页面代码（`ui/gen`）、图标图片（`ui/assets/img`）、字体（`ui/assets/fonts`）都在仓库里，拿到代码就能编，不用装生成它们的那套工具。

- **Windows（现场用）**：编出来的 `tec_hmi.exe` 开一个客户区正好 1280×720 的窗口，鼠标和触摸都能点。窗口是自己开的（`ui/src/win_window.inc`），画面一个像素对一个像素贴上去、只贴变了的那一块——不用 LVGL 自带的 Windows 驱动（它整幅拉伸着贴，用户 2026-11-24 上机看是「发虚、很卡」）。
  - `tec_hmi --fullscreen`：去掉标题栏铺满屏幕（现场这么开）；屏幕比 1280×720 大时自动放大到放得下的最大。
  - `tec_hmi --zoom 200`：指定放大多少。整数倍边缘清楚，非整数倍会软。
  - `tec_hmi_2x.exe`：同一套界面按 2 倍现画（2560×1440）。不用手动挑：`tec_hmi.exe` 启动时发现屏幕放得下它、它又在旁边，就自动改用它（`--1x` 可以不让它换）。
  - 缺省不带系统的标题栏和边框，只显示界面本身（用户 2026-11-24 要的）；`--frame` 才带。
  - `tec_hmi --host 地址 --port 端口`：连哪里的控制核心，缺省是本机 5020。
  - 自己开窗口这一版还没有在 Windows 上编过、跑过（开发机是 Mac）。
- **Mac（开发用）**：编出来的是不开窗口的版本，只能出图和模拟点击：`tec_hmi --page 页面名 --shot 文件 [--after 秒] [--do 动作名…] [--tap x,y…]`。`--tap` 用的是设计稿上的坐标，走的是和真触摸一样的路。存出来的是原始画面数据，用 `ui/tools/raw2png.py` 转成 PNG（那个脚本按 2 倍图写的，配 `ui/build.sh 2` 编出来的 `ui/build/tec_hmi_2` 用）。

字体：Inter 整套 + 裁小了的 Noto Sans SC（只留 GB2312 的 6763 个汉字和常用符号，`ui/tools/subset_fonts.py` 裁的），都是 OFL 许可，许可文本在字体旁边。
