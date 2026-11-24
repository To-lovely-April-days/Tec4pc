using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Tec.App.Controls;

namespace Tec.App.Views;

/// <summary>
/// 「手动控制面板」的窗子。里面那块是触摸屏那套界面本身（同一份代码，不是重写的）。
///
/// 整个工作站同时只开一扇：那边的界面库在进程里只有一套，开第二扇也是同一个界面。
/// 所以点另一台机器的面板时，是把这扇窗转过去连那台，并且把标题改掉，而不是再开一扇。
/// </summary>
public partial class PanelWindow : Window
{
    private static PanelWindow? _open;

    public PanelWindow()
    {
        InitializeComponent();
        Closed += (_, _) => { if (ReferenceEquals(_open, this)) _open = null; };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// 打开（或转到）这台机器的面板。host / port 是它的控制核心在哪，channel 是进去先看哪个工位。
    /// 返回空串是开了；不是空串就是没开成的原因，调用方显示给操作人。
    /// </summary>
    public static string Open(string title, string host, int port, int channel)
    {
        var why = NativePanel.Attach(host, port, channel);

        var win = _open;
        if (win is null)
        {
            win = new PanelWindow();
            _open = win;
            win.Show();
        }
        else
        {
            if (win.WindowState == WindowState.Minimized) win.WindowState = WindowState.Normal;
            win.Activate();
        }

        win.Title = $"{title} · 手动控制面板";
        win.Find<TextBlock>("TitleText")!.Text = win.Title;
        win.Find<TextBlock>("HintText")!.Text = $"{host}:{port}";
        var problem = win.Find<TextBlock>("ProblemText")!;
        problem.Text = why;
        problem.IsVisible = why.Length > 0;
        return why;
    }
}
