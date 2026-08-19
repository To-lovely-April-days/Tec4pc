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

    private void OnChangePassword(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        _ = new ChangePasswordWindow(vm.Workspace).ShowDialog(this);
    }

    private void OnManageUsers(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || !vm.IsAdmin) return;
        _ = new UserAdminWindow(vm.Workspace).ShowDialog(this);
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
        var store = vm.Workspace.Store;
        var name = vm.Workspace.ExperimentName;
        var detail = store.CurrentPath is { } p
            ? $"上次保存的位置：{p}"
            : "这份实验还没有保存过。选「保存」会让你挑一个位置。";

        var choice = await ConfirmDialog.Ask(this,
            "未保存的改动",
            $"「{name}」有改动还没有保存。关掉程序这些改动就没了。",
            detail, "保存并退出", "不保存，直接退出");

        switch (choice)
        {
            case DialogChoice.Primary:
                // 存不成（挑位置时取消了、或者写盘失败）就留在程序里，不能闷头关掉
                if (!await vm.Start.SaveForExit()) return;
                break;
            case DialogChoice.Secondary:
                break;
            default:
                return;
        }

        _confirmed = true;
        Close();
    }
}
