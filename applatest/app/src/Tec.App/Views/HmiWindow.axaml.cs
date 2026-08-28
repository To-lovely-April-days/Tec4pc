using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Tec.App.Services;
using Tec.App.ViewModels;

namespace Tec.App.Views;

public partial class HmiWindow : Window
{
    /// <summary>
    /// 每台设备一扇：同一台反应器再点「手动控制面板」是把已开的那扇提到
    /// 前面，不是再开一扇——两扇窗对同一台设备各存一份「待下发」会打架。
    /// </summary>
    private static readonly Dictionary<string, HmiWindow> ByDevice = new();

    public HmiWindow()
    {
        InitializeComponent();
    }

    public static void Open(Workspace ws, string deviceId, string label, IReadOnlyList<int> channels)
    {
        if (ByDevice.TryGetValue(deviceId, out var had))
        {
            if (had.WindowState == WindowState.Minimized) had.WindowState = WindowState.Normal;
            had.Activate();
            return;
        }
        var win = new HmiWindow
        {
            Title = $"{label} · 手动控制面板",
            DataContext = new HmiViewModel(ws, label, channels),
        };
        ByDevice[deviceId] = win;
        win.Closed += (_, _) => ByDevice.Remove(deviceId);
        win.Show();   // 非模态：面板开着，主窗口照常操作
    }

    private void OnTitlebarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    private void OnMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
