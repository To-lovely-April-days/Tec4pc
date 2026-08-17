using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Tec.Core.Export;

namespace Tec.App.Controls;

/// <summary>
/// 一页报告，按 <see cref="ReportLayout"/> 排好的图元画出来。
///
/// **和 PDF 读的是同一批图元**：每一行字的坐标、每一格的宽度都是排版器算的，
/// 这里只负责把它们画到屏幕上。预览与导出各排一套的话，预览上看着好好的，
/// 导出来的 PDF 换了行、串了页——那种预览还不如没有。
/// </summary>
public sealed class ReportPageView : Control
{
    public static readonly StyledProperty<ReportPage?> PageProperty =
        AvaloniaProperty.Register<ReportPageView, ReportPage?>(nameof(Page));

    public static readonly StyledProperty<double> PageWidthProperty =
        AvaloniaProperty.Register<ReportPageView, double>(nameof(PageWidth), 595.28);

    public static readonly StyledProperty<double> PageHeightProperty =
        AvaloniaProperty.Register<ReportPageView, double>(nameof(PageHeight), 841.89);

    /// <summary>报告字体族名。取的就是内嵌进 PDF 的那一份，屏幕上和纸上是同一套字形。</summary>
    public static readonly StyledProperty<string> FontNameProperty =
        AvaloniaProperty.Register<ReportPageView, string>(nameof(FontName), "Microsoft YaHei");

    /// <summary>
    /// 缩放。**走布局不走 RenderTransform**：后者不改变控件占的位置，
    /// 放大之后纸张边框还是原来那么大，内容会溢出到纸外面去。
    /// </summary>
    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<ReportPageView, double>(nameof(Zoom), 1.0);

    static ReportPageView()
    {
        AffectsRender<ReportPageView>(PageProperty, FontNameProperty);
        AffectsMeasure<ReportPageView>(PageWidthProperty, PageHeightProperty, ZoomProperty);
    }

    public ReportPage? Page { get => GetValue(PageProperty); set => SetValue(PageProperty, value); }
    public double PageWidth { get => GetValue(PageWidthProperty); set => SetValue(PageWidthProperty, value); }
    public double PageHeight { get => GetValue(PageHeightProperty); set => SetValue(PageHeightProperty, value); }
    public string FontName { get => GetValue(FontNameProperty); set => SetValue(FontNameProperty, value); }
    public double Zoom { get => GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }

    private readonly Dictionary<string, IBrush> _brushes = new(StringComparer.Ordinal);
    private readonly Dictionary<(int, int), Bitmap> _images = new();

    protected override Size MeasureOverride(Size available)
    {
        var z = Math.Max(0.1, Zoom);
        return new Size(PageWidth * z, PageHeight * z);
    }

    public override void Render(DrawingContext ctx)
    {
        var z = Math.Max(0.1, Zoom);
        using var _ = ctx.PushTransform(Matrix.CreateScale(z, z));
        ctx.FillRectangle(Brushes.White, new Rect(0, 0, PageWidth, PageHeight));
        if (Page is not { } page) return;

        var family = new FontFamily(FontName + ", Microsoft YaHei UI, Segoe UI, system-ui, Arial");

        foreach (var item in page.Items)
        {
            switch (item)
            {
                case RectItem r:
                    if (r.Fill is { } fill)
                        ctx.FillRectangle(Brush(fill), new Rect(r.X, r.Y, r.W, r.H));
                    if (r.Stroke is { } stroke)
                        ctx.DrawRectangle(new Pen(Brush(stroke), r.StrokeWidth), new Rect(r.X, r.Y, r.W, r.H));
                    break;

                case TextItem t when t.Text.Length > 0:
                {
                    var ft = new FormattedText(t.Text, System.Globalization.CultureInfo.CurrentCulture,
                                               FlowDirection.LeftToRight,
                                               new Typeface(family, FontStyle.Normal,
                                                            t.Bold ? FontWeight.SemiBold : FontWeight.Normal),
                                               t.Size, Brush(t.Color));
                    // 版面给的是文字块顶，FormattedText 画的也是顶——但基线位置由字体决定，
                    // 两边都按 PdfWriter.Baseline 那一处折算，才不会整体错开半行
                    ctx.DrawText(ft, new Point(t.X, t.Y + PdfWriter.Baseline(t.Size) - ft.Baseline));
                    break;
                }

                case ImageItem img when img.PixelWidth > 0 && img.PixelHeight > 0:
                {
                    var bmp = Image(img);
                    if (bmp is not null)
                        ctx.DrawImage(bmp, new Rect(0, 0, bmp.Size.Width, bmp.Size.Height),
                                      new Rect(img.X, img.Y, img.W, img.H));
                    break;
                }
            }
        }
    }

    private IBrush Brush(string hex)
    {
        if (_brushes.TryGetValue(hex, out var b)) return b;
        b = new SolidColorBrush(Color.Parse(hex));
        _brushes[hex] = b;
        return b;
    }

    /// <summary>
    /// 图元里存的是原始 BGRA。缓存住：翻一次页重画一次，一张 1800×760 的图
    /// 每次都重新搬 5 MB 像素，滚动会卡。
    /// </summary>
    private Bitmap? Image(ImageItem img)
    {
        var key = (img.Bgra.GetHashCode(), img.PixelWidth);
        if (_images.TryGetValue(key, out var cached)) return cached;
        try
        {
            var handle = System.Runtime.InteropServices.GCHandle.Alloc(img.Bgra,
                System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                var bmp = new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul,
                                     handle.AddrOfPinnedObject(),
                                     new PixelSize(img.PixelWidth, img.PixelHeight),
                                     new Vector(96, 96), img.PixelWidth * 4);
                _images[key] = bmp;
                return bmp;
            }
            finally { handle.Free(); }
        }
        catch { return null; }
    }
}
