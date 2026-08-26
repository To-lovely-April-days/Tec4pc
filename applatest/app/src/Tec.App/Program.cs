using System.Runtime.InteropServices;
using Avalonia;

namespace Tec.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        SharpenTimer();
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            RelaxTimer();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // 合成方式按「新的排前面」写死一遍，不吃默认顺序的变化。
            // WinUIComposition 走 DirectComposition，出帧跟着显示器的垂直同步走；
            // 退到 RedirectionSurface 就成了自己拿定时器数着出帧——那条路的抖动
            // 正是下面 SharpenTimer 要救的（两处一起才稳）
            .With(new Win32PlatformOptions
            {
                CompositionMode = new[]
                {
                    Win32CompositionMode.WinUIComposition,
                    Win32CompositionMode.DirectComposition,
                    Win32CompositionMode.RedirectionSurface,
                }
            })
            .LogToTrace();

    // ── Windows 的时钟粒度 ────────────────────────────────────────────
    //
    // **这是「鼠标一动一卡、有时候顺有时候卡」的那个根。**
    //
    // Windows 默认的定时器粒度是 15.625 ms：进程里所有 Sleep / 定时器都按这个
    // 格子对齐。而画面要跟上 60 Hz 得每 16.67 ms 出一帧——两个数一错位，
    // 一次 16.67 ms 的等待实际落在 15.6 或者 31.2 上，于是帧距在
    // 「一格」和「两格」之间来回跳。**画得再快也没用**：现场量到渲染一帧只花
    // 1.33 ms，可帧距平均 18.6 ms、每秒还有 5 帧超过 32 ms（掉一整格）。
    // 手上感觉到的就是时不时顿一下，而且忽好忽坏——错位是慢慢漂的。
    //
    // 同一套自检在开发机（Linux，粒度 1 ms）上跑：帧距 15–17 ms，慢帧每秒 0–2。
    // 两台机器的差别就在这颗时钟上。
    //
    // 更早那个「界面线程迟到平均 5.5 ms」当时被我当成噪声放掉了，
    // 现在回头看它是同一件事的另一面证据：一支 16 ms 的表在 15.625 的格子上
    // 平均就是迟这么多。
    //
    // timeBeginPeriod(1) 把粒度要到 1 ms。Windows 10 2004 之后这个请求
    // **只影响本进程**，不再是全机器的开关，所以不会拖累别人。
    // 代价是空闲时功耗略高一点——这是一台插着电的实验室工作站，值。
    // 退出时对称地还回去。
    private static bool _sharpened;

    private static void SharpenTimer()
    {
        if (!OperatingSystem.IsWindows()) return;
        try { _sharpened = TimeBeginPeriod(1) == 0; }
        catch { _sharpened = false; }   // winmm 不在就算了，只是回到默认粒度
    }

    private static void RelaxTimer()
    {
        if (!_sharpened) return;
        try { TimeEndPeriod(1); }
        catch { /* 退出路上出岔就随它去 */ }
    }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint ms);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint ms);
}
