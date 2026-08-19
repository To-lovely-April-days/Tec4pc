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
            var vm = new MainViewModel(workspace);
            var main = new MainWindow { DataContext = vm };
            var login = new LoginWindow { DataContext = vm.Login };

            // 开机只开登录窗（840 那扇）。登录成功点「进入工作站」才开主窗口。
            // 没登录就把登录窗关了 = 退出程序（那时还没有任何用户改动要保存）。
            // 注销走的是另一条路：主窗口里盖回登录层，不回这扇窗——
            // 主窗口的「关窗前问保存」得留在原地管用。
            vm.Login.EnteredWorkstation += (_, _) =>
            {
                desktop.MainWindow = main;
                main.Show();
                login.Close();
            };
            desktop.MainWindow = login;

            desktop.ShutdownRequested += (_, _) => workspace.Shutdown();
            // 建通道要 await，不能在界面线程上阻塞等——窗口先出来，
            // 通道建好之后 BenchChanged 会把各视图刷一遍
            _ = workspace.StartAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
