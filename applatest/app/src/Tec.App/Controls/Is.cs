using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Tec.App.Controls;

/// <summary>样式开关与选中着色的小转换器（原型 .warn/.bad 与 .rrow.is-sel）。</summary>
public static class Is
{
    public static readonly IValueConverter Warn = new FuncValueConverter<string?, bool>(s => s == "warn");
    public static readonly IValueConverter Bad = new FuncValueConverter<string?, bool>(s => s == "bad");

    public static readonly IValueConverter SelInk =
        new FuncValueConverter<bool, Color>(on => Color.Parse(on ? "#7e0f1c" : "#9a9a9a"));
    public static readonly IValueConverter SelFg =
        new FuncValueConverter<bool, IBrush>(on => new SolidColorBrush(Color.Parse(on ? "#7e0f1c" : "#2b2b2b")));
    /// <summary>格式卡片上那个线描图标：选中的用主蓝，其余灰（原型 .fmt.on .fi）。</summary>
    public static readonly IValueConverter FmtInk =
        new FuncValueConverter<bool, Color>(on => Color.Parse(on ? "#a41626" : "#8a8a8a"));

    /// <summary>
    /// 那条 6px 选中色条：选中上主色，没选中透明。
    /// 用一列常在的 Border 而不是左边框——写成边框的话选中时内容会横跳 6px。
    /// </summary>
    /// <summary>
    /// 「当前选中的这一条」左边那道竖条（配方库两处：库列表 6px、步骤卡 3px）。
    /// 一路换过来的：品牌红 #A41626 → 黄 #FFCF00 → 现在的黑 #17191c（用户定的，
    /// 与登录页那套黑同一支）。黑压在淡蓝 #ACC3DF 的选中底上对比最足，
    /// 也不跟这一页别处的红（报错）撞意思。
    /// </summary>
    public static readonly IValueConverter SelBar =
        new FuncValueConverter<bool, IBrush>(on =>
            on ? new SolidColorBrush(Color.Parse("#17191c")) : Brushes.Transparent);

    public static readonly IValueConverter SelWeight =
        new FuncValueConverter<bool, FontWeight>(on => on ? FontWeight.SemiBold : FontWeight.Normal);
}
