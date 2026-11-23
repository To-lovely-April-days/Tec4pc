using Avalonia;
using Tec.App.Services;
using Tec.Hmi.Ui.ViewModels;

namespace Tec.App.ViewModels;

/// <summary>【本地改动】标题栏「设置」弹出里的渲染两项（见 RenderPrefs）。改一项就存盘、就套用。</summary>
public sealed class RenderViewModel : ViewModelBase
{
    private readonly RenderPrefs _prefs;

    public RenderViewModel(RenderPrefs prefs) => _prefs = prefs;

    public bool FontBundled => RenderPrefs.FontBundled;

    public bool UnifiedFont
    {
        get => _prefs.UnifiedFont;
        set
        {
            if (_prefs.UnifiedFont == value) return;
            _prefs.UnifiedFont = value;
            Commit();
            Raise(nameof(UnifiedFont));
            Raise(nameof(FontNote));
        }
    }

    public bool GrayscaleText
    {
        get => _prefs.GrayscaleText;
        set
        {
            if (_prefs.GrayscaleText == value) return;
            _prefs.GrayscaleText = value;
            Commit();
            Raise(nameof(GrayscaleText));
        }
    }

    /// <summary>字体那一项底下的小字：没打包就说没打包，打包了说要重启。</summary>
    public string FontNote => !FontBundled
        ? "字体文件未打进程序（Assets/Fonts/NotoSansSC-Regular.otf），这一项用不了"
        : UnifiedFont ? "已选思源黑体，重启程序后生效" : "现在用的是系统字体（Windows 雅黑 / Mac 苹方）";

    private void Commit()
    {
        _prefs.Save();
        if (Application.Current is { } app) _prefs.Apply(app);
    }
}
