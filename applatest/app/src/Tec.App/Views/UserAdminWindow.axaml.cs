using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Tec.App.Services;
using Tec.Core.Users;

namespace Tec.App.Views;

/// <summary>
/// 用户表里的一行（只读快照，动作都按登录名回到库里做——
/// 界面上这份数据随时可能是上一秒的）。
/// </summary>
public sealed class UserRow
{
    /// <summary>下拉里的三档，顺序就是 RoleIndex 的含义。</summary>
    public static readonly IReadOnlyList<string> Roles = new[] { "操作员", "主管", "管理员" };

    public UserRow(UserAccount u, bool isSelf, DateTimeOffset now)
    {
        Name = u.Name;
        Display = u.Display;
        RoleIndex = u.Role switch { UserRole.Admin => 2, UserRole.Supervisor => 1, _ => 0 };
        RoleName = u.RoleName;
        StateName = u.StateName;
        IsSelf = isSelf;
        LastLogin = Ago(u.LastLoginAt, now);

        var (fill, line) =
            u.Disabled ? ("#cfcfcf", "#a5a5a5") :
            u.Locked ? ("#e8c05a", "#b5851f") :
                       ("#3fbf5f", "#2f8f49");
        LedFill = Brush(fill);
        LedLine = Brush(line);

        // 密码到期：停用的账号不谈到期（谈了也没意义），刚建 / 刚重置的说「首次须改」
        var (expText, expColor) =
            u.Disabled ? ("—", "#c2c2c2") :
            u.MustChangePassword ? ("首次须改", "#b5851f") :
            u.ExpiresInDays(now) is not { } d ? ("—", "#c2c2c2") :
            d == 0 ? ("已过期", "#c0392b") :
            d <= 7 ? ($"{d} 天", "#b5851f") :
                     ($"{d} 天", "#2b2b2b");
        ExpiryText = expText;
        ExpiryBrush = Brush(expColor);

        ToggleLabel = u.Disabled ? "启用" : u.Locked ? "解锁" : "停用";
        // 停用自己 = 把自己关在门外，而且没有第二个人能把你放回来
        CanToggle = !(isSelf && !u.Disabled && !u.Locked);
        ToggleTip = CanToggle ? null : "不能停用自己";
        RoleTip = isSelf ? "不能改自己的角色" : "改角色即时生效，并写入审计追踪";
    }

    public string Name { get; }
    public string Display { get; }
    public int RoleIndex { get; }
    public string RoleName { get; }
    public string StateName { get; }
    public string LastLogin { get; }
    public string ExpiryText { get; }
    public IBrush ExpiryBrush { get; }
    public bool IsSelf { get; }
    public string ToggleLabel { get; }
    public bool CanToggle { get; }
    public string? ToggleTip { get; }
    public string RoleTip { get; }

    public IReadOnlyList<string> RoleNames => Roles;

    public IBrush LedFill { get; }
    public IBrush LedLine { get; }

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));

    /// <summary>「今天 08:41 / 昨天 17:40 / 08-14 13:20」——照原型那三档写。</summary>
    private static string Ago(DateTimeOffset? at, DateTimeOffset now)
    {
        if (at is not { } t) return "从未登录";
        var days = (now.Date - t.Date).Days;
        return days switch
        {
            0 => $"今天 {t:HH:mm}",
            1 => $"昨天 {t:HH:mm}",
            _ => t.ToString("MM-dd HH:mm")
        };
    }
}

/// <summary>
/// 用户管理（只有管理员打得开）：建账号、改角色、解锁、停用 / 启用、重置密码。
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

        SearchBox.TextChanged += (_, _) => Refresh();
        OnlyOffBox.IsCheckedChanged += (_, _) => Refresh();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);

        if (ws is not null) Refresh();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    private void OnTitlebarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    // ── 表 ───────────────────────────────────────────────────────────

    private void Refresh()
    {
        var now = _ws.Clock.Now;
        var self = _ws.CurrentUser?.Name ?? "";
        var q = (SearchBox.Text ?? "").Trim();
        var onlyOff = OnlyOffBox.IsChecked == true;

        var all = _ws.Users.All
            .OrderBy(u => u.Role)
            .ThenBy(u => u.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var shown = all
            .Where(u => !onlyOff || u.Disabled || u.Locked)
            .Where(u => q.Length == 0
                        || u.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || u.Display.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Select(u => new UserRow(u, string.Equals(u.Name, self, StringComparison.OrdinalIgnoreCase), now))
            .ToList();

        Rows.ItemsSource = shown;
        EmptyNote.IsVisible = shown.Count == 0;
        CountLine.Text = $"显示 {shown.Count} / {all.Count}";
        FootLine.Text = $"共 {all.Count} 个账号 · 停用的账号保留全部历史记录";
    }

    private UserRow? RowOf(object? sender) => (sender as Control)?.DataContext as UserRow;

    // ── 新增 ─────────────────────────────────────────────────────────

    private void OnToggleAdd(object? sender, RoutedEventArgs e)
    {
        AddPanel.IsVisible = !AddPanel.IsVisible;
        AddErr.IsVisible = false;
        if (AddPanel.IsVisible) AddName.Focus();
    }

    private void OnAdd(object? sender, RoutedEventArgs e)
    {
        AddErr.IsVisible = false;
        var name = (AddName.Text ?? "").Trim();
        var display = (AddDisplay.Text ?? "").Trim();
        var pass = AddPass.Text ?? "";
        var role = AddRole.SelectedIndex switch
        {
            2 => UserRole.Admin,
            1 => UserRole.Supervisor,
            _ => UserRole.Operator
        };

        string? bad =
            name.Length == 0 || display.Length == 0
                ? "登录名和署名都要填。署名会出现在审计追踪和批记录里。"
                : PasswordPolicy.FirstProblem(pass) is { } p ? $"初始密码：{p}。" : null;
        if (bad is null && !_ws.Users.Add(name, display, role, pass, AddForce.IsChecked == true))
            bad = $"登录名「{name}」已经有人用了。停用的账号也占着登录名，不能复用。";
        if (bad is not null)
        {
            AddErr.Text = bad;
            AddErr.IsVisible = true;
            return;
        }

        _ws.Log.Write("登录", $"新建账号 {name}（{display} · {UserRow.Roles[AddRole.SelectedIndex]}）"
                            + (AddForce.IsChecked == true ? "，下次登录必须改密码" : ""), _ws.Operator);
        // 整张表单归位，不只是清文字：角色留在上一个人那一档，
        // 下一次新增就会悄悄按上一次的角色建账号
        AddName.Text = ""; AddDisplay.Text = ""; AddPass.Text = "";
        AddRole.SelectedIndex = 0;
        AddForce.IsChecked = true;
        AddPanel.IsVisible = false;
        Refresh();
    }

    // ── 行动作 ───────────────────────────────────────────────────────

    /// <summary>
    /// 改角色。下拉刚建好时也会走这一下（SelectedIndex 从绑定填进去），
    /// 所以拿这一行原来的档位比一比，没变就不算一次改动。
    /// </summary>
    private void OnRoleChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox box || RowOf(sender) is not { } row) return;
        var i = box.SelectedIndex;
        if (i < 0 || i == row.RoleIndex) return;

        var role = i switch { 2 => UserRole.Admin, 1 => UserRole.Supervisor, _ => UserRole.Operator };
        _ws.Users.SetRole(row.Name, role);
        _ws.Log.Write("登录",
            $"改角色 {row.Name}：{UserRow.Roles[row.RoleIndex]} → {UserRow.Roles[i]}", _ws.Operator);
        Refresh();
    }

    private void OnToggle(object? sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        switch (row.ToggleLabel)
        {
            case "解锁":
                _ws.Users.Unlock(row.Name);
                _ws.Log.Write("登录", $"解锁账号 {row.Name}", _ws.Operator);
                break;
            case "启用":
                _ws.Users.SetDisabled(row.Name, false);
                _ws.Log.Write("登录", $"启用账号 {row.Name}", _ws.Operator);
                break;
            default:
                _ws.Users.SetDisabled(row.Name, true);
                _ws.Log.Write("登录", $"停用账号 {row.Name}（历史记录保留）", _ws.Operator);
                break;
        }
        Refresh();
    }

    private async void OnReset(object? sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        if (_ws.Users.Find(row.Name) is not { } u) return;

        var dlg = new ResetPasswordWindow(u.Name, u.Display, u.RoleName, u.Locked);
        await dlg.ShowDialog(this);
        if (!dlg.Confirmed) return;

        // 锁着的账号：解不解锁由勾说了算。不解锁就只是换了把打不开的钥匙——
        // 这也是一种合理的处置（先弄清楚为什么被锁），所以照勾办
        _ws.Users.SetPassword(u.Name, dlg.Password, dlg.MustChange, unlock: !u.Locked || dlg.AlsoUnlock);
        _ws.Log.Write("登录",
            $"重置账号 {u.Name} 的密码"
            + (dlg.MustChange ? "，下次登录必须改" : "")
            + (u.Locked ? dlg.AlsoUnlock ? "，并解锁" : "，账号仍锁定" : ""), _ws.Operator);
        Refresh();
    }
}
