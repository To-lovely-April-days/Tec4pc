using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Tec.App.Services;
using Tec.Hmi.Ui.ViewModels;

namespace Tec.App.Views;

public partial class HmiWindow : Window
{
    /// <summary>
    /// 每台设备一扇：同一台反应器再点「手动控制面板」是把已开的那扇提到
    /// 前面，不是再开一扇——两扇窗对同一台设备各存一份「待下发」会打架。
    /// </summary>
    private static readonly Dictionary<string, HmiWindow> ByDevice = new();

    /// <summary>
    /// 面板的视图模型每台设备只建一份，**窗关了也留着**：设定值、待下发、开关、模式、日志都在，
    /// 再开还是那一份（用户踩到：控温中关掉面板再打开，面板全是缺省，像是控温没了）。
    /// 回路的真实状态另有对账（HmiZoneViewModel.SyncWithLoop）——程序重启那种连视图模型
    /// 都没了的情形靠它把开关和目标按回路恢复。通道号变了（设备重配）才重建。
    /// </summary>
    private static readonly Dictionary<string, HmiViewModel> VmByDevice = new();

    public HmiWindow()
    {
        InitializeComponent();
        // 四角与主窗口非最大化时同一副样子：Windows 11 的 DWM 切整扇窗的
        // 圆角（MainWindow.RoundCornersOnWindows11 同一套——无边框窗 DWM
        // 默认不切，得明说一句）。Windows 10 没这属性就方角，失败不管。
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
    }

    private const int DwmWindowCornerPreference = 33;   // DWMWA_WINDOW_CORNER_PREFERENCE
    private const int DwmWindowCornerRound = 2;         // DWMWCP_ROUND

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Open(Workspace ws, string deviceId, string label, IReadOnlyList<int> channels)
    {
        if (ByDevice.TryGetValue(deviceId, out var had))
        {
            if (had.WindowState == WindowState.Minimized) had.WindowState = WindowState.Normal;
            had.Activate();
            return;
        }
        if (!VmByDevice.TryGetValue(deviceId, out var vm)
            || !vm.Zones.Select(z => z.Number).SequenceEqual(channels))
        {
            vm = new HmiViewModel(ws, label, channels);
            VmByDevice[deviceId] = vm;
        }
        var win = new HmiWindow
        {
            Title = $"{label} · 手动控制面板",
            DataContext = vm,
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
