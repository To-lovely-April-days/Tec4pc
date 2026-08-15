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
        }
        base.OnFrameworkInitializationCompleted();
    }
}
