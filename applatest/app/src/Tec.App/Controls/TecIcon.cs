using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;

namespace Tec.App.Controls;

/// <summary>
/// 原型提取的界面图标。Key 对应 Assets/icons/{Key}.svg：
///   cmd-*  31 条指令图标（深灰主体 + 模块色动作部，来自 ICONS）
///   ui-*   外壳图标（snav / tbtn / 标题栏 / 模拟模式 / 帽子 / 抽屉箭头…）
///
/// Tint 解析 currentColor（原型里 snav 图标随选中态换色靠的就是它）；
/// White=true 输出纯白描边版（原型 iconWhite，铺在模块色块上用）。
/// 显示尺寸默认取 SVG 自带的 width/height，与原型逐像素一致；Size 可覆盖。
/// </summary>
public sealed class TecIcon : Control
{
    private static readonly Dictionary<string, SvgArt?> Cache = new(StringComparer.Ordinal);

    public static readonly StyledProperty<string?> KeyProperty =
        AvaloniaProperty.Register<TecIcon, string?>(nameof(Key));

    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<TecIcon, double>(nameof(Size), double.NaN);

    public static readonly StyledProperty<Color> TintProperty =
        AvaloniaProperty.Register<TecIcon, Color>(nameof(Tint), Color.Parse("#3d3d3d"));

    public static readonly StyledProperty<bool> WhiteProperty =
        AvaloniaProperty.Register<TecIcon, bool>(nameof(White));

    public static readonly StyledProperty<bool> RecolorProperty =
        AvaloniaProperty.Register<TecIcon, bool>(nameof(Recolor));

    static TecIcon()
    {
        AffectsRender<TecIcon>(KeyProperty, TintProperty, WhiteProperty, RecolorProperty);
        AffectsMeasure<TecIcon>(KeyProperty, SizeProperty);
    }

    public string? Key
    {
        get => GetValue(KeyProperty);
        set => SetValue(KeyProperty, value);
    }

    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public Color Tint
    {
        get => GetValue(TintProperty);
        set => SetValue(TintProperty, value);
    }

    /// <summary>
    /// 连写死的颜色一起换成 Tint。原型里铺在黑条上的图标 fill 是写死的 #fff，
    /// 搬到白底圆钮上原样画就是一片白——开这个开关按 Tint 重上色，几何不动。
    /// </summary>
    public bool Recolor
    {
        get => GetValue(RecolorProperty);
        set => SetValue(RecolorProperty, value);
    }

    public bool White
    {
        get => GetValue(WhiteProperty);
        set => SetValue(WhiteProperty, value);
    }

    private static SvgArt? Load(string key)
    {
        if (Cache.TryGetValue(key, out var art)) return art;
        try
        {
            using var stream = AssetLoader.Open(new Uri($"avares://Tec.App/Assets/icons/{key}.svg"));
            using var reader = new StreamReader(stream);
            art = SvgArt.Parse(reader.ReadToEnd());
        }
        catch
        {
            art = null;    // 缺图不崩，量测归零即可
        }
        Cache[key] = art;
        return art;
    }

    private (SvgArt Art, double W, double H)? Resolve()
    {
        if (Key is null || Load(Key) is not { } art) return null;
        var w = double.IsNaN(Size) ? art.DeclaredWidth : Size;
        var h = double.IsNaN(Size) ? art.DeclaredHeight : Size * art.ViewHeight / art.ViewWidth;
        return (art, w, h);
    }

    protected override Size MeasureOverride(Size availableSize)
        => Resolve() is { } r ? new Size(r.W, r.H) : default;

    public override void Render(DrawingContext ctx)
    {
        if (Resolve() is not { } r) return;
        var paint = new SvgArt.Paint(Colors.Gray, Colors.Gray, false, false)
        {
            Current = White ? Colors.White : Tint,
            StrokeOverride = White ? Colors.White : Recolor ? Tint : null,
            FillOverride = Recolor && !White ? Tint : null
        };

        // 图标画在自身范围的正中。被 Border / Panel 拉伸时（比如 CARET 的 20px 圆圈，
        // 原型是 place-items:center），控件比图大，按左上角画就会偏出圆心。
        var dx = Math.Max(0, (Bounds.Width - r.W) / 2);
        var dy = Math.Max(0, (Bounds.Height - r.H) / 2);
        using var _ = ctx.PushTransform(Matrix.CreateTranslation(dx, dy));
        r.Art.Render(ctx, r.W / r.Art.ViewWidth, paint);
    }
}
