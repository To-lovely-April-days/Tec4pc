using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Tec.App.Controls;

/// <summary>样式开关与选中着色的小转换器（原型 .warn/.bad 与 .rrow.is-sel）。</summary>
public static class Is
{
    public static readonly IValueConverter Warn = new FuncValueConverter<string?, bool>(s => s == "warn");
    public static readonly IValueConverter Bad = new FuncValueConverter<string?, bool>(s => s == "bad");

    public static readonly IValueConverter SelInk =
        new FuncValueConverter<bool, Color>(on => Color.Parse(on ? "#0b3760" : "#9a9a9a"));
    public static readonly IValueConverter SelFg =
        new FuncValueConverter<bool, IBrush>(on => new SolidColorBrush(Color.Parse(on ? "#0b3760" : "#2b2b2b")));
    public static readonly IValueConverter SelWeight =
        new FuncValueConverter<bool, FontWeight>(on => on ? FontWeight.SemiBold : FontWeight.Normal);
}
