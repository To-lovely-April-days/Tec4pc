using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Tec.App.Services;
using Tec.Core.Users;

namespace Tec.App.Views;

/// <summary>用户表里的一行（只读快照，动作按登录名回到库里做）。</summary>
public sealed record UserRow(
    string Name, string Display, string RoleName, string StateName,
    string LastLogin, bool Locked, bool IsSelf, string DisableLabel, IBrush StateColor);

/// <summary>
/// 用户管理（只有管理员打得开）：建账号、解锁、停用 / 启用、重置密码。
/// 每一件事写审计——「谁在什么时候对哪个账号做了什么」要答得出来。
/// </summary>
public partial class UserAdminWindow : Window
{
    private readonly Workspace _ws;

    public UserAdminWindow() : this(null!) { }   // 设计器用

    public UserAdminWindow(Workspace ws)
    {
        _ws = ws;
        InitializeComponent();
        if (ws is not null) Refresh();
    }

    private void Refresh()
    {
        var self = _ws.CurrentUser?.Name ?? "";
        Rows.ItemsSource = _ws.Users.All
            .OrderBy(u => u.Role).ThenBy(u => u.Name, StringComparer.OrdinalIgnoreCase)
            .Select(u => new UserRow(
                u.Name, u.Display, u.RoleName, u.StateName,
                u.LastLoginAt is { } t ? t.ToString("MM-dd HH:mm") : "从未登录",
                u.Locked,
                string.Equals(u.Name, self, StringComparison.OrdinalIgnoreCase),
                u.Disabled ? "启用" : "停用",
                u.Disabled ? Brushes.Gray : u.Locked ? new SolidColorBrush(Color.Parse("#c0392b"))
                                                     : new SolidColorBrush(Color.Parse("#2f8f49"))))
            .ToList();
    }

    private void Say(string text)
    {
        Note.Text = text;
        Note.IsVisible = true;
    }

    // ── 新增 ─────────────────────────────────────────────────────────

    private void OnAdd(object? sender, RoutedEventArgs e)
    {
        AddErr.IsVisible = false;
        var name = (AddName.Text ?? "").Trim();
        var display = (AddDisplay.Text ?? "").Trim();
        var pass = AddPass.Text ?? "";
        var role = AddRole.SelectedIndex == 1 ? UserRole.Admin : UserRole.Operator;

        string? bad =
            name.Length == 0 ? "登录名不能为空。"
            : pass.Length < 4 ? "初始密码至少 4 位。"
            : null;
        if (bad is null && !_ws.Users.Add(name, display, role, pass))
            bad = $"登录名「{name}」已经有人用了。";
        if (bad is not null)
        {
            AddErr.Text = bad;
            AddErr.IsVisible = true;
            return;
        }

        _ws.Log.Write("登录", $"新建账号 {name}（{(role == UserRole.Admin ? "管理员" : "操作员")}）", _ws.Operator);
        AddName.Text = ""; AddDisplay.Text = ""; AddPass.Text = "";
        Say($"已添加 {name}。");
        Refresh();
    }

    // ── 行动作 ───────────────────────────────────────────────────────

    private UserRow? RowOf(object? sender)
        => (sender as Control)?.DataContext as UserRow;

    private void OnUnlock(object? sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        _ws.Users.Unlock(row.Name);
        _ws.Log.Write("登录", $"解锁账号 {row.Name}", _ws.Operator);
        Say($"已解锁 {row.Name}。");
        Refresh();
    }

    private void OnToggleDisable(object? sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        var disable = row.DisableLabel == "停用";
        _ws.Users.SetDisabled(row.Name, disable);
        _ws.Log.Write("登录", $"{(disable ? "停用" : "启用")}账号 {row.Name}", _ws.Operator);
        Say($"已{(disable ? "停用" : "启用")} {row.Name}。");
        Refresh();
    }

    private async void OnReset(object? sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        var pass = await AskPasswordAsync(row.Name);
        if (pass is null) return;
        if (pass.Length < 4) { Say("没改：新密码至少 4 位。"); return; }
        _ws.Users.SetPassword(row.Name, pass);
        _ws.Log.Write("登录", $"重置账号 {row.Name} 的密码（连带解锁）", _ws.Operator);
        Say($"已重置 {row.Name} 的密码。");
        Refresh();
    }

    /// <summary>问一个新密码的小对话框。取消返回 null。</summary>
    private async Task<string?> AskPasswordAsync(string name)
    {
        var box = new TextBox { PasswordChar = '●', Watermark = "新密码（至少 4 位）", FontSize = 12 };
        var ok = new Button { Content = "确定", Classes = { "btn", "primary" } };
        var cancel = new Button { Content = "取消", Classes = { "btn" } };
        var win = new Window
        {
            Title = $"重置 {name} 的密码",
            Width = 320,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(16),
                Spacing = 10,
                Children =
                {
                    box,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { cancel, ok }
                    }
                }
            }
        };
        string? result = null;
        ok.Click += (_, _) => { result = box.Text ?? ""; win.Close(); };
        cancel.Click += (_, _) => win.Close();
        await win.ShowDialog(this);
        return result;
    }
}
