using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Tec.App.ViewModels;

namespace Tec.App.Views;

public partial class MainWindow : Window
{
    /// <summary>已经问过「要不要保存」了，这次关窗直接放行，别再问一遍。</summary>
    private bool _confirmed;

    public MainWindow() => InitializeComponent();

    /// <summary>自绘标题栏要自己负责拖动。</summary>
    private void OnTitlebarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
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
