using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Tec.App.Views;

public static class Converters
{
    private static IBrush B(string hex) => new SolidColorBrush(Color.Parse(hex));

    /// <summary>通道状态小灯（原型 .chstate .led / .led.on）。</summary>
    public static readonly IValueConverter LedBrush =
        new FuncValueConverter<bool, IBrush>(on => B(on ? "#3fbf5f" : "#c8c8c8"));

    // ── 开始视图卡片标签（原型 .rtag / .rtag.live / .rtag.draft）──

    public static readonly IValueConverter TagBg =
        new FuncValueConverter<string?, IBrush>(c => c switch
        {
            "live" => B("#2f8f49"),
            "draft" => B("#e6ffffff"),
            _ => B("#f0ffffff")
        });

    public static readonly IValueConverter TagBorder =
        new FuncValueConverter<string?, IBrush>(c => c switch
        {
            "live" => B("#2f8f49"),
            _ => B("#dcdcdc")
        });

    public static readonly IValueConverter TagFg =
        new FuncValueConverter<string?, IBrush>(c => c switch
        {
            "live" => Brushes.White,
            "draft" => B("#b0b0b0"),
            _ => B("#7a7a7a")
        });

    /// <summary>图钉：钉住转绿（原型 .rpin.pinned）。</summary>
    public static readonly IValueConverter PinTint =
        new FuncValueConverter<bool, Color>(p => Color.Parse(p ? "#2f8f49" : "#8d8d8d"));

    /// <summary>选中的卡片标题转深蓝加粗（原型 .rcard.on .rname）。</summary>
    public static readonly IValueConverter NameFg =
        new FuncValueConverter<bool, IBrush>(on => B(on ? "#0b3760" : "#2b2b2b"));

    public static readonly IValueConverter NameWeight =
        new FuncValueConverter<bool, FontWeight>(on => on ? FontWeight.SemiBold : FontWeight.Normal);

    /// <summary>非空字符串才显示。空提示行不该占着一行的高度。</summary>
    public static readonly IValueConverter NotBlank =
        new FuncValueConverter<string?, bool>(s => !string.IsNullOrWhiteSpace(s));
}
