using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Tec.App.Services;
using Tec.App.ViewModels;

namespace Tec.App.Views;

public partial class LoginWindow : Window
{
    /// <summary>
    /// 关掉这扇窗要不要顺带退出程序，以及退出前找谁问「未保存的改动」。
    /// 由外壳（App）装进来：开机那一扇是「关了就退出，没什么要问的」，
    /// 注销之后那一扇背后还挂着一份可能没存的实验，得先问一句。
    /// </summary>
    public Func<Task<bool>>? ConfirmExit { get; set; }
    public Action? ExitApp { get; set; }

    private bool _leaving;

    public LoginWindow()
    {
        InitializeComponent();
        // 隧道阶段挂：焦点在密码框里按 F12 也要收得到
        AddHandler(KeyDownEvent, OnDiagKey, RoutingStrategies.Tunnel);
    }

    private PerfProbe? _probe;

    /// <summary>
    /// F12 开性能自检。**这扇窗上有这一件，是因为现场说登录界面也卡。**
    /// 登录窗里没有配方、没有步骤库、没有任何按节拍跳的东西——一张图加几个
    /// 输入框。它要是也卡，说明毛病跟我们画了什么无关，得往渲染节奏、
    /// 显卡驱动、机器本身去找。量表见 Services/PerfProbe，读数每秒写一行
    /// 到程序旁边的 perf.log，跑几分钟发回来就行（断续的毛病截图抓不住）。
    /// </summary>
    private void OnDiagKey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.F12) return;
        e.Handled = true;
        _probe ??= PerfProbe.Attach(this, DiagLine);
        var on = _probe.Toggle();
        PerfProbe.Overlays(this, on);
        DiagBar.IsVisible = on;
        if (on) _probe.Refresh();
    }

    private void OnTitlebarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    private void OnMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// 收起这扇窗，**不算退出**。进工作站时外壳调它——直接 Close() 会掉进
    /// 下面那条「关窗 = 退出程序」的路，一按「进入工作站」整个程序就没了。
    /// </summary>
    public void Dismiss()
    {
        _leaving = true;
        Close();
    }

    /// <summary>
    /// 关掉登录窗 = 退出程序。但注销回来的这一扇背后还挂着主窗口和可能没存的
    /// 实验，所以先问一句再走——问的是跟主窗口 ✕ 同一句话（ConfirmLeaveAsync）。
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_leaving || ConfirmExit is null) return;
        e.Cancel = true;
        _ = AskThenExit();
    }

    private async Task AskThenExit()
    {
        if (!await ConfirmExit!()) return;
        _leaving = true;
        ExitApp?.Invoke();
    }

    /// <summary>登录页在这扇窗里，语言、记住的账号都在它身上。</summary>
    public LoginViewModel? Vm => DataContext as LoginViewModel;
}
