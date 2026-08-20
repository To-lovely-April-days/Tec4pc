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
    /// <summary>格式卡片上那个线描图标：选中的用主蓝，其余灰（原型 .fmt.on .fi）。</summary>
    public static readonly IValueConverter FmtInk =
        new FuncValueConverter<bool, Color>(on => Color.Parse(on ? "#1a7fc4" : "#8a8a8a"));

    /// <summary>
    /// 那条 6px 选中色条：选中上主色，没选中透明。
    /// 用一列常在的 Border 而不是左边框——写成边框的话选中时内容会横跳 6px。
    /// </summary>
    public static readonly IValueConverter SelBar =
        new FuncValueConverter<bool, IBrush>(on =>
            on ? new SolidColorBrush(Color.Parse("#1a7fc4")) : Brushes.Transparent);

    /// <summary>
    /// 步骤卡左边那条 3px 绿条（原型 .rx-card.rx-sel 的 inset 阴影）。
    /// 跟 SelBar 分开是因为配方库那张步骤卡的选中色是绿的不是蓝的——
    /// 那一片是「这条配方长什么样」的预览，跟温度剖面上的选中色带同一个绿，
    /// 点了色带哪一张卡亮起来，两边对得上。
    /// </summary>
    public static readonly IValueConverter GreenBar =
        new FuncValueConverter<bool, IBrush>(on =>
            on ? new SolidColorBrush(Color.Parse("#2f8f49")) : Brushes.Transparent);

    public static readonly IValueConverter SelWeight =
        new FuncValueConverter<bool, FontWeight>(on => on ? FontWeight.SemiBold : FontWeight.Normal);
}
