using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Tec.App.Services;
using Tec.App.ViewModels;

namespace Tec.App.Views;

/// <summary>
/// 「温控器参数」窗。每台设备一扇（跟 HmiWindow 一个规矩）：再点是把已开的那扇提到前面。
/// 顶栏那个机器图标开的是台面上第一台反应主机的；属性栏「温控器参数」开的是选中那台的。
/// </summary>
public partial class DeviceSettingsWindow : Window
{
    private static readonly Dictionary<string, DeviceSettingsWindow> ByDevice = new();

    public DeviceSettingsWindow()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            if (!OperatingSystem.IsWindows()) return;
            try
            {
                if (TryGetPlatformHandle()?.Handle is not { } h || h == IntPtr.Zero) return;
                var round = DwmWindowCornerRound;
                DwmSetWindowAttribute(h, DwmWindowCornerPreference, ref round, sizeof(int));
            }
            catch { /* 老系统没有这个属性，方角就方角 */ }
        };
        Closed += (_, _) => (DataContext as DeviceSettingsViewModel)?.Dispose();
    }

    private const int DwmWindowCornerPreference = 33;
    private const int DwmWindowCornerRound = 2;

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Open(Workspace ws, string deviceId, string label)
    {
        if (ByDevice.TryGetValue(deviceId, out var had))
        {
            if (had.WindowState == WindowState.Minimized) had.WindowState = WindowState.Normal;
            had.Activate();
            return;
        }
        var win = new DeviceSettingsWindow
        {
            Title = $"{label} · 温控器参数",
            DataContext = new DeviceSettingsViewModel(ws, deviceId, label)
        };
        ByDevice[deviceId] = win;
        win.Closed += (_, _) => ByDevice.Remove(deviceId);
        win.Show();
    }

    /// <summary>
    /// 顶栏机器图标：开台面上第一台反应主机的参数窗。台面上没有主机也开——
    /// 窗里如实写「台面上没有反应主机」，不弹一个谁也看不见的错。
    /// </summary>
    public static void OpenFirstHost(Workspace ws)
    {
        var host = ws.HostDevices().FirstOrDefault();
        if (host is null)
        {
            Open(ws, "", "反应主机");
            return;
        }
        Open(ws, host.InstanceId, host.Display);
    }

    /// <summary>动作钮：要确认的（自整定）先问一句，问过才做。</summary>
    private async void OnAction(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SettingsActionViewModel a }) return;
        if (a.Confirm)
        {
            var choice = await ConfirmDialog.Ask(this, a.Label, $"确定要做「{a.Label}」？",
                a.Tip ?? "这个动作会直接改温控器的运行状态，要有人在场。", a.Label, null);
            if (choice != DialogChoice.Primary) return;
        }
        await a.RunAsync();
    }

    private void OnTitlebarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    private void OnMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
