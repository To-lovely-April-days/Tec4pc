using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Interactivity;

namespace Tec.App.Services;

/// <summary>
/// 【本地改动】渲染设置（2026-11-23，用户在 Mac 上看界面比 Windows 细腻，想在 Windows 上也这样）。
///
/// WHY：Mac 与 Windows 的观感差在三处——像素密度（屏幕的事，程序管不了）、字体（Mac 没雅黑退到苹方）、
/// 文字抗锯齿（macOS 灰度 vs Windows ClearType）。后两处程序能抹平：
/// · 统一中文字体：把开源的思源黑体（Noto Sans SC）打进程序资源，两边都用它，截图在哪台机器上都长一样；
/// · 文字灰度抗锯齿：把窗口的 TextRenderingMode 从缺省的亚像素改成灰度，笔画柔和、不带彩边。
/// 两项**缺省都关**：现场机器照旧用雅黑 + ClearType，谁都不会在升级后突然换了脸。
/// 设置存 AppData\TecStudio\render.json——是这台机器的偏好，不跟实验走，演示模式也用同一份（演示数据目录是一次性的）。
/// 字体文件（Assets/Fonts/NotoSansSC-Regular.otf，SIL OFL 许可）没打进包时，字体那一项灰掉并照实说明，不假装生效。
/// </summary>
public sealed class RenderPrefs
{
    public bool UnifiedFont { get; set; }
    public bool GrayscaleText { get; set; }

    /// <summary>打进资源的字体：Assets/Fonts/ 目录 + 字族名（Avalonia 的字体 URI 写法）。</summary>
    public const string FontFamilyName = "Noto Sans SC";
    public const string FontFileUri = "avares://Tec.App/Assets/Fonts/NotoSansSC-Regular.otf";
    private const string FontFamilyUri = "avares://Tec.App/Assets/Fonts#Noto Sans SC";
    private const string FallbackFamilies = ", Microsoft YaHei UI, Microsoft YaHei, Segoe UI, system-ui, Arial";

    /// <summary>字体文件在不在程序资源里。不在就不能开「统一字体」。</summary>
    public static bool FontBundled
    {
        get
        {
            try { return AssetLoader.Exists(new Uri(FontFileUri)); }
            catch { return false; }
        }
    }

    public static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
        "TecStudio", "render.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true
    };

    public static RenderPrefs Load()
    {
        try
        {
            if (File.Exists(Path))
                return JsonSerializer.Deserialize<RenderPrefs>(File.ReadAllText(Path), Json) ?? new RenderPrefs();
        }
        catch (Exception ex) { Console.WriteLine($"[warn] 渲染设置读不出来，按缺省：{ex.Message}"); }
        return new RenderPrefs();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex) { Console.WriteLine($"[warn] 渲染设置存不上：{ex.Message}"); }
    }

    // ── 应用 ─────────────────────────────────────────────────────────

    private static bool _hooked;
    private static TextRenderingMode _mode = TextRenderingMode.Unspecified;

    /// <summary>
    /// 把设置落到程序上。开窗之前调一次（App 启动）；之后改开关再调，抗锯齿当场换、字体要重启
    /// （窗口里的 StaticResource UiFont 在装载时就定下了）。
    /// </summary>
    public void Apply(Application app)
    {
        // 字体：盖掉 Theme.axaml 里的 UiFont（Application.Resources 先于 Styles 的资源被查到）
        if (UnifiedFont && FontBundled)
            app.Resources["UiFont"] = new FontFamily(FontFamilyUri + FallbackFamilies);
        else
            app.Resources.Remove("UiFont");

        // 文字抗锯齿：RenderOptions 是挂在 Visual 上、往下继承的，在每扇窗的根上设一次就够。
        // Avalonia 11.2 没把那个附加属性公开给样式用，所以挂一个 Window 的 Loaded 类处理器——
        // 以后开的每扇窗（对话框、HMI 窗、报告预览）装载时都套上；已经开着的这里直接设
        _mode = GrayscaleText ? TextRenderingMode.Antialias : TextRenderingMode.Unspecified;
        if (!_hooked)
        {
            _hooked = true;
            Control.LoadedEvent.AddClassHandler<Window>((w, _) => RenderOptions.SetTextRenderingMode(w, _mode));
        }
        if (app.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            foreach (var w in desktop.Windows)
                RenderOptions.SetTextRenderingMode(w, _mode);
    }
}
