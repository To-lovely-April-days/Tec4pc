using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Tec.Core.Users;

namespace Tec.App.Views;

/// <summary>
/// 管理员重置某个账号的密码。窗口只负责收「新密码 / 要不要强制改 / 要不要顺带解锁」，
/// 真正落库和写审计在用户管理那边做——重置这件事得跟审计一起发生，
/// 拆到两个地方就可能只发生一半。
/// </summary>
public partial class ResetPasswordWindow : Window
{
    public ResetPasswordWindow() : this("", "", "", false) { }   // 设计器用

    public ResetPasswordWindow(string name, string display, string roleName, bool locked)
    {
        InitializeComponent();

        WhoLine.Text = $"为 {name}（{display} · {roleName}）重置密码";

        // 「同时解锁」只在真锁着的时候出现：没锁的账号摆一个解锁勾，
        // 会让人以为它本来是锁着的
        UnlockBox.IsVisible = locked;
        if (!locked) Height -= 22;

        PassBox.Text = PasswordPolicy.Generate();
        Opened += (_, _) => { PassBox.Focus(); PassBox.SelectAll(); };
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>点了「重置」才是 true；✕、取消、Esc 都是 false。</summary>
    public bool Confirmed { get; private set; }

    public string Password => PassBox.Text ?? "";
    public bool MustChange => ForceBox.IsChecked == true;
    public bool AlsoUnlock => UnlockBox.IsVisible && UnlockBox.IsChecked == true;

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
        else if (e.Key == Key.Enter) OnOk(this, e);
    }

    private void OnTitlebarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    private void OnGenerate(object? sender, RoutedEventArgs e)
    {
        PassBox.Text = PasswordPolicy.Generate();
        PassBox.Focus();
        PassBox.SelectAll();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        // 只拦长度和字符集这一档：管理员定的是临时密码，本人下次登录还要改
        if (PasswordPolicy.FirstProblem(Password) is { } bad)
        {
            ErrText.Text = bad;
            ErrText.IsVisible = true;
            return;
        }
        Confirmed = true;
        Close();
    }
}
