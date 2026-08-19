using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
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
            new Shell(desktop, workspace).Start();
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// 登录窗与主窗口的换手。两扇窗、三条路（进工作站 / 注销 / 退出）搅在一起，
    /// 摊在 OnFrameworkInitializationCompleted 里读不清楚，拎成一个小类。
    ///
    /// **主窗口只建一次，注销时只是收起来。** 重建的话，正在跑的通道、
    /// 打开的实验、各页的选中状态全都得重新拼——而 Workspace 一直活着，
    /// 收起来再打开，下一个人接手的就是原样。
    /// </summary>
    private sealed class Shell
    {
        private readonly IClassicDesktopStyleApplicationLifetime _desktop;
        private readonly Workspace _ws;
        private readonly MainViewModel _vm;
        private readonly MainWindow _main;
        private LoginWindow? _login;

        public Shell(IClassicDesktopStyleApplicationLifetime desktop, Workspace ws)
        {
            _desktop = desktop;
            _ws = ws;
            _vm = new MainViewModel(ws);
            _main = new MainWindow { DataContext = _vm };

            // 关窗的时机由我们说了算：注销时主窗口是「收起来」不是关掉，
            // 这期间开着的窗只有登录窗——默认的「最后一扇窗关掉就退出」
            // 会在这中间把程序判死
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.ShutdownRequested += (_, _) => ws.Shutdown();

            _main.Closed += (_, _) => Quit();
            _vm.Login.EnteredWorkstation += (_, _) => EnterWorkstation();
            _vm.SignedOut += (_, _) => ShowLogin();
        }

        public void Start()
        {
            ShowLogin();
            // 建通道要 await，不能在界面线程上阻塞等——窗口先出来，
            // 通道建好之后 BenchChanged 会把各视图刷一遍
            _ = _ws.StartAsync();
        }

        /// <summary>开一扇新的登录窗（开机、注销都走它）。</summary>
        private void ShowLogin()
        {
            _vm.Login.ShowForm();
            LoginWindow win = null!;
            win = new LoginWindow
            {
                DataContext = _vm.Login,
                // 登录窗上的 ✕：主窗口还挂着没存的实验就先问一句，再退出。
                // 对话框的宿主用局部变量，不用 _login 字段——进工作站时字段会被清掉
                ConfirmExit = () => MainWindow.ConfirmLeaveAsync(win, _vm),
                ExitApp = Quit
            };
            _login = win;
            _desktop.MainWindow = win;
            win.Show();
            _main.Hide();
        }

        /// <summary>
        /// 进工作站。**先让主窗口画完第一帧再关登录窗**：反过来的话，中间那一下
        /// 屏幕上一扇窗都没有，看见的是主窗口还没画内容的白底（用户报的「闪白屏」）。
        /// 两次 Post 到 Loaded：第一次等布局跑完，第二次等这一帧真的画出去。
        /// </summary>
        private void EnterWorkstation()
        {
            var closing = _login;
            _login = null;
            _desktop.MainWindow = _main;

            // 登录窗在这段等待里置顶。主窗口 1500×940 比它大，不置顶的话
            // 一 Show 就整个盖上来——「留着登录窗」等于没留，看见的还是那片空白
            if (closing is not null) closing.Topmost = true;

            _main.Show();
            _main.Activate();

            // **等主窗口真的画出内容再关登录窗。**反过来的话，中间那一下屏幕上
            // 只剩一扇还没画完的主窗口，看见的就是它的空白底（用户报的「闪白屏」）。
            //
            // 两个条件都满足才收：
            //   · 渲染回调回来两次——Post 到 Loaded 只等到布局，帧还没上屏（实测过），
            //     RequestAnimationFrame 才是等渲染；第一帧往往还在建合成器的表面。
            //   · 至少停够 Dwell——慢机器上头两帧回来时窗口仍是空的（实测：
            //     0.15 s 还全空，0.5 s 已画好）。这段停留人是察觉不到的，
            //     反正登录窗一直盖着，不是白等。
            // 两条都由同一个 Finish 收口，谁后到谁真的关；再加一条硬上限兜底，
            // 万一某个平台不回渲染回调，也不会把登录窗永远留在那儿。
            var frames = 0;
            var waited = false;
            var closed = false;
            void Finish()
            {
                if (closed || frames < 2 || !waited) return;
                closed = true;
                closing?.Dismiss();
            }

            _main.RequestAnimationFrame(_ =>
            {
                frames++;
                _main.RequestAnimationFrame(_ => { frames++; Finish(); });
            });

            var dwell = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            dwell.Tick += (_, _) => { dwell.Stop(); waited = true; Finish(); };
            dwell.Start();

            var backstop = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            backstop.Tick += (_, _) =>
            {
                backstop.Stop();
                frames = 2; waited = true;
                Finish();
            };
            backstop.Start();
        }

        private void Quit()
        {
            if (_desktop.MainWindow is null) return;   // 已经在退了
            _desktop.MainWindow = null;
            _desktop.Shutdown();
        }
    }
}
