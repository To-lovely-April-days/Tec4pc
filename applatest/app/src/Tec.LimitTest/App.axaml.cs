using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Tec.LimitTest;

public partial class App : Application
{
    private ToolHost? _host;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 没接住的异常一律先落一份文件再死：装到别的电脑上「双击没反应」的时候至少有地方看
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                WriteCrash("未处理的异常", e.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                WriteCrash("后台任务异常（已吞掉）", e.Exception);
                e.SetObserved();
            };

            try
            {
                // 数据目录可用环境变量改：TEC_LIMIT_DATA 指到别的盘
                var dataDir = Environment.GetEnvironmentVariable("TEC_LIMIT_DATA");
                _host = new ToolHost(string.IsNullOrWhiteSpace(dataDir) ? null : dataDir);
                _host.Prepare();

                var vm = new MainViewModel(_host);
                desktop.MainWindow = new MainWindow { DataContext = vm, Title = vm.Heading };
                desktop.ShutdownRequested += (_, _) =>
                {
                    vm.Shutdown();
                    try { _host?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                    catch { /* 退出路上出岔就随它去 */ }
                    _host = null;
                };
            }
            catch (Exception ex)
            {
                // 开机就倒（数据目录建不了、台面文件写不进……）：开一扇只写原因的窗，不无声退出
                var path = WriteCrash("启动失败", ex);
                desktop.MainWindow = new Window
                {
                    Title = "控温极限测试 · 启动失败",
                    Width = 720, Height = 360,
                    Content = new TextBlock
                    {
                        Margin = new Thickness(16),
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        Text = "程序没能启动：\n\n" + ex.Message +
                               "\n\n数据目录：" + (Environment.GetEnvironmentVariable("TEC_LIMIT_DATA") ?? "%AppData%\\TecLimitTest") +
                               "\n（用环境变量 TEC_LIMIT_DATA 可以指到别的盘）" +
                               (path is null ? "" : "\n\n完整信息已写到：" + path)
                    }
                };
            }
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>崩溃信息写到临时目录（数据目录可能正是建不起来的那个）。返回写成的路径，写不成返回 null。</summary>
    private static string? WriteCrash(string what, Exception? ex)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "TecLimitTest-crash.log");
            File.AppendAllText(path,
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {what} · 版本 {MainViewModel.ToolVersion}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
            return path;
        }
        catch
        {
            return null;
        }
    }
}
