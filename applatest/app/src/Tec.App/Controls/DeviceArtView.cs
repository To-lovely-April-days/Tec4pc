using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;

namespace Tec.App.Controls;

public static class DeviceArtCache
{
    private static readonly Dictionary<string, SvgArt?> Cache = new(StringComparer.Ordinal);

    /// <summary>设备线稿由原型导出，与 tecstudio.html 上的是同一份图。</summary>
    public static SvgArt? Get(string key)
    {
        if (Cache.TryGetValue(key, out var art)) return art;
        try
        {
            var uri = new Uri($"avares://Tec.App/Assets/devices/{key}.svg");
            using var stream = AssetLoader.Open(uri);
            using var reader = new StreamReader(stream);
            art = SvgArt.Parse(reader.ReadToEnd());
        }
        catch
        {
            art = null;      // 缺图不该让界面崩，画个占位框就行
        }
        Cache[key] = art;
        return art;
    }
}

/// <summary>台面画布与设备库共用的设备图元。</summary>
public sealed class DeviceArtView : Control
{
    public static readonly StyledProperty<string> ArtKeyProperty =
        AvaloniaProperty.Register<DeviceArtView, string>(nameof(ArtKey), "reactor2");

    public static readonly StyledProperty<double> ArtWidthProperty =
        AvaloniaProperty.Register<DeviceArtView, double>(nameof(ArtWidth), 140);

    public static readonly StyledProperty<Color> Tint1Property =
        AvaloniaProperty.Register<DeviceArtView, Color>(nameof(Tint1), Color.Parse("#c2c2c2"));

    public static readonly StyledProperty<Color> Tint2Property =
        AvaloniaProperty.Register<DeviceArtView, Color>(nameof(Tint2), Color.Parse("#c2c2c2"));

    public static readonly StyledProperty<bool> Run1Property =
        AvaloniaProperty.Register<DeviceArtView, bool>(nameof(Run1));

    public static readonly StyledProperty<bool> Run2Property =
        AvaloniaProperty.Register<DeviceArtView, bool>(nameof(Run2));

    static DeviceArtView()
    {
        AffectsRender<DeviceArtView>(ArtKeyProperty, Tint1Property, Tint2Property, Run1Property, Run2Property);
        AffectsMeasure<DeviceArtView>(ArtKeyProperty, ArtWidthProperty, FitProperty);
    }

    public string ArtKey
    {
        get => GetValue(ArtKeyProperty);
        set => SetValue(ArtKeyProperty, value);
    }

    public double ArtWidth
    {
        get => GetValue(ArtWidthProperty);
        set => SetValue(ArtWidthProperty, value);
    }

    public Color Tint1
    {
        get => GetValue(Tint1Property);
        set => SetValue(Tint1Property, value);
    }

    public Color Tint2
    {
        get => GetValue(Tint2Property);
        set => SetValue(Tint2Property, value);
    }

    public bool Run1
    {
        get => GetValue(Run1Property);
        set => SetValue(Run1Property, value);
    }

    public bool Run2
    {
        get => GetValue(Run2Property);
        set => SetValue(Run2Property, value);
    }

    /// <summary>
    /// 等比缩到给定格子里（设备库用）。设备图宽高比差得很远——探头细长、
    /// 反应器扁宽——按同一个宽度画出来就高矮不齐；固定格子、缩到格子内才齐整。
    /// </summary>
    public static readonly StyledProperty<bool> FitProperty =
        AvaloniaProperty.Register<DeviceArtView, bool>(nameof(Fit));

    public bool Fit
    {
        get => GetValue(FitProperty);
        set => SetValue(FitProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var art = DeviceArtCache.Get(ArtKey);
        if (art is null) return new Size(ArtWidth, ArtWidth * 0.7);
        if (Fit) return availableSize;                    // 由外面的格子定尺寸
        var scale = ArtWidth / art.ViewWidth;
        return new Size(ArtWidth, art.ViewHeight * scale);
    }

    /// <summary>缩放比与居中偏移：Fit 时取宽高两个方向的较小比例，整幅不裁。</summary>
    private (double Scale, double Dx, double Dy) Place(SvgArt art)
    {
        if (!Fit) return (ArtWidth / art.ViewWidth, 0, 0);
        var s = Math.Min(Bounds.Width / art.ViewWidth, Bounds.Height / art.ViewHeight);
        return (s, (Bounds.Width - art.ViewWidth * s) / 2, (Bounds.Height - art.ViewHeight * s) / 2);
    }

    public override void Render(DrawingContext context)
    {
        var art = DeviceArtCache.Get(ArtKey);
        if (art is null)
        {
            context.DrawRectangle(new SolidColorBrush(Color.Parse("#f2f4f6")),
                new Pen(new SolidColorBrush(Color.Parse("#c8ccd2")), 1),
                new Rect(0, 0, Bounds.Width, Bounds.Height), 4, 4);
            return;
        }
        var (scale, dx, dy) = Place(art);
        using var _ = context.PushTransform(Matrix.CreateTranslation(dx, dy));
        art.Render(context, scale, new SvgArt.Paint(Tint1, Tint2, Run1, Run2));
    }
}
