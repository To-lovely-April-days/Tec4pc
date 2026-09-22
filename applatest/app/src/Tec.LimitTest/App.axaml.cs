using Avalonia;
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
            // 数据目录可用环境变量改：TEC_LIMIT_DATA 指到别的盘
            var dataDir = Environment.GetEnvironmentVariable("TEC_LIMIT_DATA");
            _host = new ToolHost(string.IsNullOrWhiteSpace(dataDir) ? null : dataDir);
            _host.Prepare();

            var vm = new MainViewModel(_host);
            desktop.MainWindow = new MainWindow { DataContext = vm };
            desktop.ShutdownRequested += (_, _) =>
            {
                vm.Shutdown();
                try { _host?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                catch { /* 退出路上出岔就随它去 */ }
                _host = null;
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
