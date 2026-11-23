using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Tec.Hmi.Runtime;
using Tec.Hmi.Ui.ViewModels;
using Tec.Hmi.Ui.Views;

namespace Tec.Hmi;

public partial class App : Application
{
    private HmiRuntime? _runtime;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 数据目录可用环境变量改：TEC_HMI_DATA 指到别的盘。
            // 时标恒为 1——从前的 TEC_HMI_TIMESCALE 是给仿真台面演示加速的，程序里没有仿真了
            var dataDir = Environment.GetEnvironmentVariable("TEC_HMI_DATA");
            // 【本地改动】演示模式（--demo）：一次性的演示数据目录、仿真时标；不带参数时跟从前一模一样
            var demo = Tec.Core.DemoMode.Enabled;
            if (demo) dataDir = Tec.Core.DemoMode.ResetDir(Tec.Core.DemoMode.HmiDataDir);

            _runtime = new HmiRuntime(string.IsNullOrWhiteSpace(dataDir) ? null : dataDir);
            _runtime.Boot(demo ? Tec.Core.DemoMode.TimeScale : 1);
            // 开机建通道。HmiRuntime 全程 ConfigureAwait(false)、不碰界面线程，
            // 在这儿等它跑完是安全的；串口打不开会照实记日志继续，不会挂在这里
            _runtime.StartAsync().GetAwaiter().GetResult();

            var host = _runtime.Bench.Devices.FirstOrDefault(d =>
                _runtime.Drivers.Driver(d.DriverId)?.Info.ChannelsPerDevice > 0);
            var label = host?.Display ?? _runtime.Bench.Name;
            var channels = _runtime.Channels.Select(c => c.Number).ToArray();

            var vm = new HmiViewModel(_runtime, label, channels);

            // 全屏无边框直接承载 HmiView。设计尺寸 1280×720 钉死在 Viewbox 里：
            // 1280×720 物理屏 1:1 逐像素；别的屏等比放大缩小，不裁不变形。
            // 不用工作站那扇 1282×752 的桌面弹窗壳——720 高的屏放不下它。
            // --windowed 参数开成普通窗（调试/截图用），现场不带参数就是全屏
            var windowed = desktop.Args?.Contains("--windowed") == true;
            desktop.MainWindow = new Window
            {
                Title = $"{label} · HMI" + (demo ? " · " + Tec.Core.DemoMode.Badge : ""),
                Background = Brushes.White,
                SystemDecorations = windowed ? SystemDecorations.Full : SystemDecorations.None,
                WindowState = windowed ? WindowState.Normal : WindowState.FullScreen,
                Width = 1280, Height = 720,
                Content = new Viewbox
                {
                    Stretch = Stretch.Uniform,
                    Child = new HmiView { Width = 1280, Height = 720, DataContext = vm }
                },
                DataContext = vm
            };

            // 【本地改动】演示模式：两路的面板序列自己跑起来（工位 A 立刻、工位 B 一分钟后）——
            // 装置总览 / 趋势 / 序列页一开就有曲线和进度。走的是面板自己的「启动序列」按钮那条路
            if (demo)
            {
                var kick = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                var fired = 0;
                kick.Tick += (_, _) =>
                {
                    fired++;
                    if (fired == 1) vm.Zones.ElementAtOrDefault(0)?.StartSeq();
                    if (fired == 1) kick.Interval = TimeSpan.FromSeconds(60);
                    if (fired == 2) { vm.Zones.ElementAtOrDefault(1)?.StartSeq(); kick.Stop(); }
                };
                kick.Start();
            }

            desktop.ShutdownRequested += (_, _) =>
            {
                try { _runtime?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                catch { /* 退出路上出岔就随它去，归档在 30s 快照里 */ }
                _runtime = null;
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
