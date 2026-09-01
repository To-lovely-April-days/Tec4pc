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
            // 数据目录 / 时标可用环境变量改：TEC_HMI_DATA 指到别的盘，
            // TEC_HMI_TIMESCALE 只给仿真台面演示加速用（真机恒 1，README 有说明）
            var dataDir = Environment.GetEnvironmentVariable("TEC_HMI_DATA");
            var scale = double.TryParse(Environment.GetEnvironmentVariable("TEC_HMI_TIMESCALE"),
                out var s) && s > 0 ? s : 1;

            _runtime = new HmiRuntime(string.IsNullOrWhiteSpace(dataDir) ? null : dataDir);
            _runtime.Boot(scale);
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
                Title = $"{label} · HMI",
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
