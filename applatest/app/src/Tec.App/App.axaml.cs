using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Tec.App.Services;
using Tec.App.ViewModels;
using Tec.App.Views;

namespace Tec.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var workspace = new Workspace();
            workspace.Boot();
            desktop.MainWindow = new MainWindow { DataContext = new MainViewModel(workspace) };
            desktop.ShutdownRequested += (_, _) => workspace.Shutdown();
            // 建通道要 await，不能在界面线程上阻塞等——窗口先出来，
            // 通道建好之后 BenchChanged 会把各视图刷一遍
            _ = workspace.StartAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
