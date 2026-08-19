using Avalonia.Controls;
using Avalonia.Interactivity;
using Tec.App.Services;

namespace Tec.App.Views;

/// <summary>改自己的密码：先对旧的，再收新的。改成写审计。</summary>
public partial class ChangePasswordWindow : Window
{
    private readonly Workspace _ws;

    public ChangePasswordWindow() : this(null!) { }   // 设计器用

    public ChangePasswordWindow(Workspace ws)
    {
        _ws = ws;
        InitializeComponent();
        if (ws?.CurrentUser is { } u) WhoLine.Text = $"账号 {u.Name}（{u.Display}）";
    }

    private void Fail(string text)
    {
        ErrText.Text = text;
        ErrBox.IsVisible = true;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        if (_ws?.CurrentUser is not { } u) { Close(); return; }
        var oldPw = OldBox.Text ?? "";
        var newPw = NewBox.Text ?? "";
        if (!_ws.Users.Check(u.Name, oldPw)) { Fail("旧密码不对。"); return; }
        if (newPw.Length < 4) { Fail("新密码至少 4 位。"); return; }
        if (newPw != (New2Box.Text ?? "")) { Fail("两次输入的新密码不一致。"); return; }
        if (newPw == oldPw) { Fail("新密码不能与旧密码相同。"); return; }

        _ws.Users.SetPassword(u.Name, newPw);
        _ws.Log.Write("登录", $"修改密码（{u.Name}）", _ws.Operator);
        Close();
    }
}
