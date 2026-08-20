using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Tec.App.Services;

namespace Tec.App.Views;

/// <summary>
/// 锁屏。离开工位时把界面挡住，回来输本人密码继续——**运行中的实验不受影响**。
///
/// 解锁时用的是 Users.Check，不是 TryLogin：锁屏输错不计错误次数、不锁账号。
/// 反过来的话，值夜班的人回来连按错三次，账号就锁了，还得半夜找管理员——
/// 而屏幕本来就已经锁着了，再锁一层账号并不多挡住什么。
/// </summary>
public partial class LockWindow : Window
{
    private readonly Workspace _ws;

    /// <summary>点了「换个人登录」——由调用方接着走注销那条路。</summary>
    public bool SwitchUser { get; private set; }

    public LockWindow() : this(null!) { }   // 设计器用

    public LockWindow(Workspace ws)
    {
        _ws = ws;
        InitializeComponent();

        if (ws?.CurrentUser is { } u)
            WhoLine.Text = $"{u.Name} · {u.RoleName} · 工作站 {Environment.MachineName}";

        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Opened += (_, _) => { CoverScreen(); PassBox.Focus(); };
    }

    /// <summary>
    /// 铺满这一块屏。**不用 WindowState=Maximized**：CanResize=False 的窗
    /// 窗口管理器不给最大化（实测 openbox 就不给，结果窗子只有默认那么大，
    /// 底下的界面还露着大半）。自己按屏幕范围摆，谁都拦不住。
    ///
    /// 用 Bounds 不用 WorkingArea：任务栏也要盖住，锁屏漏一条出来就没锁住。
    /// 多屏时只盖主屏——别的屏上主窗口是模态挡着的，点不动。
    /// </summary>
    private void CoverScreen()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null) return;
        var b = screen.Bounds;
        Position = b.Position;
        Width = b.Width / screen.Scaling;
        Height = b.Height / screen.Scaling;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // Esc 不放行：那样锁屏就成了摆设
        if (e.Key == Key.Enter) OnUnlock(this, e);
    }

    private void OnUnlock(object? sender, RoutedEventArgs e)
    {
        if (_ws?.CurrentUser is not { } u) { Close(); return; }
        if (!_ws.Users.Check(u.Name, PassBox.Text ?? ""))
        {
            ErrText.Text = "密码不对。";
            ErrText.IsVisible = true;
            PassBox.SelectAll();
            PassBox.Focus();
            return;
        }
        _ws.Log.Write("登录", $"解锁屏幕（{u.Name}）", _ws.Operator);
        Close();
    }

    private void OnSwitchUser(object? sender, RoutedEventArgs e)
    {
        SwitchUser = true;
        Close();
    }
}
