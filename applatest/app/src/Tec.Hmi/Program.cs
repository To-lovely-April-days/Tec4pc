using Avalonia;
using Avalonia.Media;

namespace Tec.Hmi;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
        => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // 设备多半不是 Windows：雅黑不存在。按顺序落到 Noto / 文泉驿——
            // 这两家是 Linux 设备上最常见的中文字形（部署 README 里有安装步骤，
            // apt install fonts-noto-cjk 一条命令）。谁在就用谁，都不在时
            // Avalonia 退回默认字体，中文会变方框——那是「设备没装字体」的实话，
            // 装上就好，不在程序里藏一个看不出来源的字形
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
