using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Tec.App.Services;
using Tec.Core.Users;

namespace Tec.App.Views;

/// <summary>规则表里的一行。文案来自 PasswordPolicy，界面只负责画勾。</summary>
public sealed class PasswordRuleRow
{
    public PasswordRuleRow(string text, bool ok) { Text = text; Ok = ok; }

    public string Text { get; }
    public bool Ok { get; }
    public IBrush Fg => Ok ? Ink : Pale;
    public IBrush BoxFill => Ok ? Green : Brushes.Transparent;
    public IBrush BoxLine => Ok ? Green : Grey;

    private static readonly IBrush Green = new SolidColorBrush(Color.Parse("#2f8f49"));
    private static readonly IBrush Grey = new SolidColorBrush(Color.Parse("#cfcfcf"));
    private static readonly IBrush Ink = new SolidColorBrush(Color.Parse("#4a4a4a"));
    private static readonly IBrush Pale = new SolidColorBrush(Color.Parse("#a2a2a2"));
}

/// <summary>
/// 改自己的密码。先对旧的，再收新的；规则边打字边勾，四条全绿才让点确定——
/// 「打完点确定才被驳回、还得从头再敲一遍」是这一版要甩掉的毛病。
///
/// forced = true 是「下次登录必须修改」那条路进来的：没有取消、没有 ✕，
/// 顶上多一条说明为什么现在非改不可。
/// </summary>
public partial class ChangePasswordWindow : Window
{
    private readonly Workspace _ws;
    private readonly bool _forced;

    /// <summary>改成了没有。forced 那条路上，调用方靠它决定要不要再问一遍。</summary>
    public bool Changed { get; private set; }

    public ChangePasswordWindow() : this(null!) { }   // 设计器用

    public ChangePasswordWindow(Workspace ws, bool forced = false)
    {
        _ws = ws;
        _forced = forced;
        InitializeComponent();

        if (ws?.CurrentUser is { } u)
            WhoLine.Text = $"账号 {u.Name} · 署名 {u.Display} · 角色 {u.RoleName}";

        if (forced)
        {
            TitleText.Text = "修改密码 — 首次登录";
            ForceStrip.IsVisible = true;
            CloseBtn.IsVisible = false;
            CancelBtn.IsVisible = false;
            Height += 72;   // 顶上多一条说明，窗子跟着长高（无边框窗不能靠 SizeToContent）
        }

        foreach (var box in new[] { OldBox, NewBox, New2Box })
        {
            box.TextChanged += (_, _) => Recheck();
            box.AddHandler(KeyDownEvent, OnBoxKeyDown, RoutingStrategies.Tunnel);
        }

        Opened += (_, _) => OldBox.Focus();
        Recheck();
    }

    // ── 规则表 ───────────────────────────────────────────────────────

    private void Recheck()
    {
        var old = OldBox.Text ?? "";
        var next = NewBox.Text ?? "";
        var again = New2Box.Text ?? "";

        var rows = PasswordPolicy.Rules
            .Select(r => new PasswordRuleRow(r.Text, r.Ok(next, old)))
            .ToList();
        // 「两次一致」只有界面知道（库里没有第二个输入框），所以在这儿补上
        rows.Add(new PasswordRuleRow("两次输入一致", again.Length > 0 && next == again));
        Rules.ItemsSource = rows;

        New2Box.Classes.Set("bad", again.Length > 0 && next != again);
        // 旧密码还没填时也不放行：不填旧的就改，等于谁坐下都能改这个账号的密码
        OkBtn.IsEnabled = old.Length > 0 && rows.All(r => r.Ok);
    }

    /// <summary>
    /// 大写锁定提示。Avalonia 没有「现在 CapsLock 亮不亮」这个问法，
    /// 只能从这一下敲出来的字反推：敲出大写字母、又没按住 Shift，那就是锁着的
    /// （反过来，小写 + 按住 Shift 同样说明锁着）。这是猜，但猜得有依据，
    /// 而且只用来提示一句，猜错不影响任何判断。
    /// </summary>
    private void OnBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { if (OkBtn.IsEnabled) OnOk(this, e); return; }
        if (e.Key == Key.Escape && !_forced) { Close(); return; }

        if (e.KeySymbol is not { Length: 1 } s || !char.IsLetter(s[0])) return;
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        CapsHint.IsVisible = char.IsUpper(s[0]) != shift;
    }

    // ── 拖动 / 收尾 ──────────────────────────────────────────────────

    private void OnTitlebarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    private void OnEye(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string name) return;
        if (this.FindControl<TextBox>(name) is not { } box) return;
        box.RevealPassword = !box.RevealPassword;
        b.Classes.Set("on", box.RevealPassword);
        box.Focus();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        if (_ws?.CurrentUser is not { } u) { Close(); return; }
        var oldPw = OldBox.Text ?? "";
        var newPw = NewBox.Text ?? "";

        // 旧密码对不对只有库知道，所以这一条不能进规则表（那是边打字边看的）
        if (!_ws.Users.Check(u.Name, oldPw))
        {
            ErrText.Text = "旧密码不对。";
            ErrText.IsVisible = true;
            OldBox.Focus();
            OldBox.SelectAll();
            return;
        }
        if (PasswordPolicy.FirstProblem(newPw, oldPw) is { } bad)
        {
            ErrText.Text = bad;
            ErrText.IsVisible = true;
            return;
        }

        _ws.Users.SetPassword(u.Name, newPw);
        _ws.Log.Write("登录", $"修改本人密码（{u.Name} · 工作站 {Environment.MachineName}）", _ws.Operator);
        Changed = true;
        Close();
    }

    /// <summary>forced 那条路上不许空手走人：不改完就一直在这儿。</summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_forced && !Changed) e.Cancel = true;
    }
}
