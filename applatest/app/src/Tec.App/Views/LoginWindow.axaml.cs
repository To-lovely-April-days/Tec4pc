using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Tec.App.Views;

public partial class LoginWindow : Window
{
    public LoginWindow() => InitializeComponent();

    private void OnTitlebarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    private void OnMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    /// <summary>还没登录，关掉登录窗就是退出程序（走生命周期的最后一扇窗规则）。</summary>
    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
