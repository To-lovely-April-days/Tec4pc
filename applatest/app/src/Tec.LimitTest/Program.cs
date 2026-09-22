using Avalonia;
using Avalonia.Media;

namespace Tec.LimitTest;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
        => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // 字体回落与设备 HMI 同一份：Windows 上雅黑，Linux 上 Noto / 文泉驿，都没有就是方框——
            // 那是「没装字体」的实话，装上就好
            .With(new FontManagerOptions
            {
                FontFallbacks = new[]
                {
                    new FontFallback { FontFamily = new FontFamily("Microsoft YaHei UI") },
                    new FontFallback { FontFamily = new FontFamily("Microsoft YaHei") },
                    new FontFallback { FontFamily = new FontFamily("Noto Sans CJK SC") },
                    new FontFallback { FontFamily = new FontFamily("Noto Sans SC") },
                    new FontFallback { FontFamily = new FontFamily("WenQuanYi Zen Hei") },
                    new FontFallback { FontFamily = new FontFamily("WenQuanYi Micro Hei") },
                }
            })
            .LogToTrace();
}
