# Tec.Hmi 设备端部署

设备自带触摸屏的 HMI 可执行程序：全屏承载手动控制面板（与工作站弹窗里那份
是同一份界面代码），地基是精简工作台（引擎/安全/归档齐全，没有登录、
台面编辑、配方库、化合物库）。

## 出包

```bash
scripts/publish-hmi.sh linux-x64     # 工控机；ARM 面板机用 linux-arm64，Windows 用 win-x64
```

产物在 `dist/tec-hmi-<RID>/`：self-contained 单文件，设备上**不用装 .NET**。
**绝不加 `PublishTrimmed`**——Avalonia 反射绑定和 drivers/ 运行时加载都不兼容裁剪。

## 部署清单（Linux 设备）

1. **串口权限**：运行账号进 `dialout` 组（`sudo usermod -aG dialout <user>`，
   重新登录生效）。串口名形如 `/dev/ttyUSB0`，直接填进台面文件的「串口」。
2. **中文字体**：`sudo apt install fonts-noto-cjk`（或把文泉驿/Noto 字体拷进
   `/usr/share/fonts/` 后 `fc-cache -f`）。程序按
   雅黑 → Noto Sans CJK SC → 文泉驿 的顺序找；一个都没装时中文显示方框——
   那是「设备没装字体」的实话，装上就好。
3. **台面文件**：第一次启动会在数据目录写出缺省的 `bench.json`
   （双工位主机 + 两支宇电探头，串口是驱动缺省值），按现场串口改好重启。
   格式就是工作站的台面文件：也可以在工作站上把台面摆好、填好串口，
   另存台面后拷过来直接用。
4. **IO8R 总线错误模式**：模块拨到「复位」（部署清单第 1 条，
   docs/双工位反应主机驱动需求.md §7）——上位机断线时继电器落回 TEC 侧全靠它。
5. **数据目录**：缺省 `~/.config/TecHmi`（归档 Runs/、日志 Logs/、面板序列
   HmiPanel/ 都在里面）。要放到别的盘：环境变量 `TEC_HMI_DATA=/data/techmi`。
6. **第三方驱动**：驱动包散装进 exe 旁边的 `drivers/`（与工作站同一套规矩）。

## 运行

```bash
./Tec.Hmi                 # 全屏（现场）
./Tec.Hmi --windowed      # 普通窗口（调试/截图）
```

- 屏幕正好 1280×720 时逐像素 1:1；别的分辨率等比缩放，不裁不变形。
- 程序里没有仿真：bench.json 里摆的每一台都按真机打开，串口打不开的那台在面板
  头卡上显示「未连接：原因」（系统页每台一行），其余照常。早期版本写着仿真驱动号
  （`tec.reactor.rd105` 等）的 bench.json 读进来会自动换成真机驱动，换了什么记进日志。
  时钟恒为 1:1（从前那个 `TEC_HMI_TIMESCALE` 已经没有了）。

## 开机自启（systemd 示例）

```ini
# /etc/systemd/system/tec-hmi.service
[Unit]
Description=Tec HMI
After=graphical.target

[Service]
User=hmi
Environment=DISPLAY=:0
ExecStart=/opt/tec-hmi/Tec.Hmi
Restart=on-failure

[Install]
WantedBy=graphical.target
```

断电直接拔：程序每 30 秒把跑着的批次快照进归档，下次开机自动把没善终的
炉收尾并在日志里写明——这套跟工作站是同一份恢复逻辑。
