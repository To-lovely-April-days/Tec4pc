using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using System.Runtime.InteropServices;
using Tec.App.Services;
using Tec.App.ViewModels;

namespace Tec.App.Views;

public partial class MainWindow : Window
{
    /// <summary>已经问过「要不要保存」了，这次关窗直接放行，别再问一遍。</summary>
    private bool _confirmed;

    public MainWindow()
    {
        InitializeComponent();
        // 隧道阶段挂：F12 在输入框里按下时也要收得到，冒泡上来之前就可能被吃掉
        AddHandler(KeyDownEvent, OnDiagKey, RoutingStrategies.Tunnel);
        // 句柄要等窗口真的建出来才有
        Opened += (_, _) => RoundCornersOnWindows11();
    }

    /// <summary>
    /// 让 Windows 11 把这扇窗的四角切圆。
    ///
    /// **这是「不透明窗」的配套。**从前圆角是自己裁的：整扇窗开透明通道，
    /// 里面用一个 8px 圆角的 Border 兜住内容，外面那一圈露桌面。代价是
    /// 每一帧 DWM 都要按 alpha 混合整窗——现场实测就是这一条把鼠标拖卡的。
    /// 窗改成不透明之后自己就不能再裁了（裁了就是四个白角），
    /// 圆角这件事交回给系统：DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUND，
    /// 它削的是整扇窗，我们里面照旧画方的。
    ///
    /// Windows 11（22000+）才有这个属性；Windows 10 和别的系统上这一句
    /// 直接失败，那就是方角——跟那些系统上别的程序一样，本来也没有圆角。
    /// 所以整段包在 try 里，失败不管：它只关乎四个角好不好看。
    /// </summary>
    private void RoundCornersOnWindows11()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            if (TryGetPlatformHandle()?.Handle is not { } h || h == IntPtr.Zero) return;
            var round = DwmWindowCornerRound;
            DwmSetWindowAttribute(h, DwmWindowCornerPreference, ref round, sizeof(int));
        }
        catch
        {
            // 老系统没有这个属性，方角就方角
        }
    }

    private const int DwmWindowCornerPreference = 33;   // DWMWA_WINDOW_CORNER_PREFERENCE
    private const int DwmWindowCornerRound = 2;         // DWMWCP_ROUND

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // ── 性能自检（F12 / Shift+F12）─────────────────────────────────────
    //
    // 量表本身搬去了 Services/PerfProbe——**登录窗也要用**。现场报回来的是
    // 「登录界面移动鼠标都是卡卡的」，那扇窗只有一张图和几个输入框，
    // 跟配方页、步骤库、心跳全都无关。量表只挂在主窗口上就够不着它了。
    //
    // 这里只剩两件主窗口自己的事：F12 的按键、以及 Shift+F12 切窗口透明。
    //
    // Shift+F12 留着是当对照用的。**这扇窗从前为了 8px 圆角整个带透明通道**，
    // 现场按下去那一次「顺了很多」，后来才弄清那是碰巧——毛病是断续的，
    // 顺不顺跟按没按这一下没有稳定关系。窗口还是改成了不透明（那一层
    // 每帧混合整窗的开销本来就白花），但它不是病因，别记成结论。
    private bool _opaque = true;    // 窗口默认不透明，见 axaml 顶上那段
    private PerfProbe? _probe;

    private void OnDiagKey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.F12) return;
        e.Handled = true;

        _probe ??= PerfProbe.Attach(this, DiagLine);

        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            _opaque = !_opaque;
            TransparencyLevelHint = new[]
            {
                _opaque ? WindowTransparencyLevel.None : WindowTransparencyLevel.Transparent
            };
            Background = _opaque ? Brushes.White : Brushes.Transparent;
        }
        else
        {
            PerfProbe.Overlays(this, _probe.Toggle());
        }

        DiagLine.IsVisible = _probe.On || !_opaque;   // 透明是非默认档，露出来提醒一句
        if (DiagLine.IsVisible) _probe.Refresh();
    }

    /// <summary>自绘标题栏要自己负责拖动。</summary>
    private void OnTitlebarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    /// <summary>顶栏机器图标：开台面上第一台反应主机的「温控器参数」窗。</summary>
    private void OnOpenDeviceSettings(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) DeviceSettingsWindow.OpenFirstHost(vm.Workspace);
    }

    private void OnMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object? sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    // ── 用户区下拉 ───────────────────────────────────────────────────

    /// <summary>
    /// 选完一项就把账户菜单收起来。Avalonia 的 Flyout 只认「点到别处」这一种关法，
    /// 点里面的按钮它自己不关——不收的话，弹出来的窗前面还浮着一块菜单。
    /// </summary>
    private void CloseUserMenu() => (UserZone.Flyout as Flyout)?.Hide();

    private void OnChangePassword(object? sender, RoutedEventArgs e)
    {
        CloseUserMenu();
        if (DataContext is not MainViewModel vm) return;
        _ = new ChangePasswordWindow(vm.Workspace).ShowDialog(this);
    }

    private void OnManageUsers(object? sender, RoutedEventArgs e)
    {
        CloseUserMenu();
        if (DataContext is not MainViewModel vm || !vm.IsAdmin) return;
        _ = new UserAdminWindow(vm.Workspace).ShowDialog(this);
    }

    private void OnLogout(object? sender, RoutedEventArgs e)
    {
        CloseUserMenu();
        if (DataContext is MainViewModel vm && vm.Logout.CanExecute(null)) vm.Logout.Execute(null);
    }

    /// <summary>锁屏。通道不动——锁的是人，不是台面（见 LockWindow 顶上那段）。</summary>
    private async void OnLockScreen(object? sender, RoutedEventArgs e)
    {
        CloseUserMenu();
        if (DataContext is not MainViewModel vm || vm.Workspace.CurrentUser is null) return;
        vm.Workspace.Log.Write("登录", $"锁定屏幕（{vm.Workspace.CurrentUser.Name}）", vm.Workspace.Operator);

        var win = new LockWindow(vm.Workspace);
        await win.ShowDialog(this);
        if (win.SwitchUser && vm.Logout.CanExecute(null)) vm.Logout.Execute(null);
    }

    /// <summary>
    /// 「下次登录必须修改密码」的账号，进了工作站先把这件事办了。
    /// 初始密码是别人定的，本人没改过之前，记录上那个署名站不住脚——
    /// 所以这扇对话框关不掉（见 ChangePasswordWindow 的 forced）。
    /// </summary>
    public async Task ForceChangePasswordIfNeeded()
    {
        if (DataContext is not MainViewModel vm) return;
        if (vm.Workspace.CurrentUser is not { MustChangePassword: true }) return;
        await new ChangePasswordWindow(vm.Workspace, forced: true).ShowDialog(this);
    }

    /// <summary>
    /// 有未保存的改动就先问一句。Closing 不能等异步结果，所以先把这次关窗拦下来，
    /// 问完再自己调一次 Close()——标准做法。
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_confirmed || DataContext is not MainViewModel vm) return;

        var store = vm.Workspace.Store;
        if (!store.Dirty) return;

        e.Cancel = true;
        _ = AskThenClose(vm);
    }

    private async Task AskThenClose(MainViewModel vm)
    {
        if (!await ConfirmLeaveAsync(this, vm)) return;
        _confirmed = true;
        Close();
    }

    /// <summary>
    /// 有未保存的改动就问一句，返回「可以走了吗」。
    ///
    /// 拎成静态的是因为**退出的口子不止一个**：主窗口的 ✕ 是一个，注销之后
    /// 关掉那扇登录窗又是一个——两处得问同一句话、走同一套判断，
    /// 不然从其中一个口子出去就能把没存的实验悄悄丢掉。
    /// </summary>
    public static async Task<bool> ConfirmLeaveAsync(Window owner, MainViewModel vm)
    {
        var store = vm.Workspace.Store;
        if (!store.Dirty) return true;

        var name = vm.Workspace.ExperimentName;
        var detail = store.CurrentPath is { } p
            ? $"上次保存的位置：{p}"
            : "这份实验还没有保存过。选「保存」会让你挑一个位置。";

        var choice = await ConfirmDialog.Ask(owner,
            "未保存的改动",
            $"「{name}」有改动还没有保存。关掉程序这些改动就没了。",
            detail, "保存并退出", "不保存，直接退出");

        return choice switch
        {
            // 存不成（挑位置时取消了、或者写盘失败）就留在程序里，不能闷头关掉
            DialogChoice.Primary => await vm.Start.SaveForExit(),
            DialogChoice.Secondary => true,
            _ => false
        };
    }
}
