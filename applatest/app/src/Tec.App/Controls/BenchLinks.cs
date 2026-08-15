using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Tec.App.Services;

namespace Tec.App.Controls;

/// <summary>
/// 台面管路（原型 renderLinks 的 1:1）：两端直出一小段再正交拐弯，转角圆化。
/// 四种管路各有各的画法——
///   探头：深灰外壳 + 浅色芯 + 白高光，像一根插管，接口处压一个连接件；
///   加料：白描边 + 品红实线 + 箭头；
///   取样 / 分析：同上但走虚线，琥珀色；
///   信号：细灰点线。
/// 标签是白底小胶囊：通道色点 + CHn + 类型短名。
/// </summary>
public sealed class BenchLinks : Control
{
    public static readonly StyledProperty<IEnumerable?> LinksProperty =
        AvaloniaProperty.Register<BenchLinks, IEnumerable?>(nameof(Links));

    public static readonly StyledProperty<bool> ShowLabelsProperty =
        AvaloniaProperty.Register<BenchLinks, bool>(nameof(ShowLabels), true);

    static BenchLinks() => AffectsRender<BenchLinks>(LinksProperty, ShowLabelsProperty);

    /// <summary>
    /// 集合本身不变、只是内容增删时也要重画——AffectsRender 只认属性替换，
    /// 台面上加一台设备并不会换掉 Links 这个集合对象。
    /// </summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != LinksProperty) return;
        if (change.OldValue is INotifyCollectionChanged o) o.CollectionChanged -= OnLinksChanged;
        if (change.NewValue is INotifyCollectionChanged n) n.CollectionChanged += OnLinksChanged;
        InvalidateVisual();
    }

    private void OnLinksChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    public IEnumerable? Links
    {
        get => GetValue(LinksProperty);
        set => SetValue(LinksProperty, value);
    }

    public bool ShowLabels
    {
        get => GetValue(ShowLabelsProperty);
        set => SetValue(ShowLabelsProperty, value);
    }

    private static readonly Color[] ChColors =
    {
        Color.Parse("#2f7ed8"), Color.Parse("#2aa87a"), Color.Parse("#c9772b"), Color.Parse("#8a63d2")
    };

    private static Color ColorOf(LinkKind k) => k switch
    {
        LinkKind.Probe => Color.Parse("#5f6469"),
        LinkKind.Feed => Color.Parse("#c53a9d"),
        LinkKind.Sample => Color.Parse("#dba32c"),
        _ => Color.Parse("#9aa0a5")
    };

    private static string ShortOf(LinkKind k) => k switch
    {
        LinkKind.Probe => "管口",
        LinkKind.Feed => "加料",
        LinkKind.Sample => "取样",
        _ => "总线"
    };

    public override void Render(DrawingContext ctx)
    {
        if (Links is null) return;

        foreach (var link in Links.OfType<BenchLink>())
        {
            var pts = BenchDock.Route(link.From, link.FromDir, link.To, link.ToDir,
                                      link.Kind == LinkKind.Probe ? 18 : 24);
            var geo = Rounded(pts, link.Kind == LinkKind.Probe ? 9 : 12);
            var col = ColorOf(link.Kind);
            var chc = ChColors[Math.Clamp(link.Channel - 1, 0, 3)];

            switch (link.Kind)
            {
                case LinkKind.Probe:
                    Stroke(ctx, geo, Color.Parse("#5c5e60"), 5.8);
                    Stroke(ctx, geo, Color.Parse("#e6e9eb"), 3);
                    Stroke(ctx, geo, Colors.White, 1, 0.85);
                    // 接口处的连接件与通道色点
                    ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#b9bec2")), null,
                        new RoundedRect(new Rect(link.To.X - 7, link.To.Y - 3.4, 14, 8), 1.8));
                    ctx.DrawEllipse(new SolidColorBrush(chc), null, new Point(link.To.X, link.To.Y + 8), 2.6, 2.6);
                    break;

                case LinkKind.Signal:
                    Stroke(ctx, geo, col, 1.5, dash: new double[] { 2, 4.5 });
                    break;

                default:
                    Stroke(ctx, geo, Colors.White, 5.4, 0.92);
                    Stroke(ctx, geo, col, 2.6,
                           dash: link.Kind == LinkKind.Sample ? new double[] { 8, 4 } : null);
                    Arrow(ctx, pts, col);
                    // 设备那一端画一个出口圆点
                    ctx.DrawEllipse(Brushes.White, new Pen(new SolidColorBrush(col), 2), link.From, 3.8, 3.8);
                    break;
            }

            if (ShowLabels) Label(ctx, pts, link, chc);
        }
    }

    private static void Stroke(DrawingContext ctx, Geometry geo, Color c, double w,
                               double opacity = 1, double[]? dash = null)
    {
        var pen = new Pen(new SolidColorBrush(c, opacity), w)
        { LineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        if (dash is not null) pen.DashStyle = new DashStyle(dash, 0);
        ctx.DrawGeometry(null, pen, geo);
    }

    /// <summary>圆角折线：每个拐点用二次贝塞尔抹圆（原型 roundPath）。</summary>
    private static Geometry Rounded(IReadOnlyList<Point> pts, double r)
    {
        var geo = new StreamGeometry();
        using var g = geo.Open();
        if (pts.Count < 3)
        {
            g.BeginFigure(pts[0], false);
            for (var i = 1; i < pts.Count; i++) g.LineTo(pts[i]);
            g.EndFigure(false);
            return geo;
        }

        g.BeginFigure(pts[0], false);
        for (var i = 1; i < pts.Count - 1; i++)
        {
            Point p = pts[i - 1], c = pts[i], n = pts[i + 1];
            var d1 = Dist(c, p);
            var d2 = Dist(n, c);
            var rr = Math.Min(r, Math.Min(d1 / 2, d2 / 2));
            var a = new Point(c.X + (p.X - c.X) / d1 * rr, c.Y + (p.Y - c.Y) / d1 * rr);
            var b = new Point(c.X + (n.X - c.X) / d2 * rr, c.Y + (n.Y - c.Y) / d2 * rr);
            g.LineTo(a);
            g.QuadraticBezierTo(c, b);
        }
        g.LineTo(pts[^1]);
        g.EndFigure(false);
        return geo;
    }

    private static double Dist(Point a, Point b) => Math.Max(Math.Sqrt(
        (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y)), 0.001);

    /// <summary>末端箭头，指向接口。</summary>
    private static void Arrow(DrawingContext ctx, IReadOnlyList<Point> pts, Color col)
    {
        var b = pts[^1];
        var a = pts[^2];
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var len = Math.Max(Math.Sqrt(dx * dx + dy * dy), 0.001);
        double ux = dx / len, uy = dy / len;
        var tip = b;
        var back = new Point(b.X - ux * 8, b.Y - uy * 8);
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(tip, true);
            g.LineTo(new Point(back.X - uy * 4, back.Y + ux * 4));
            g.LineTo(new Point(back.X + uy * 4, back.Y - ux * 4));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(new SolidColorBrush(col), null, geo);
    }

    /// <summary>标签落在最长的一段上（原型 labelSpot）。</summary>
    private static void Label(DrawingContext ctx, IReadOnlyList<Point> pts, BenchLink link, Color chc)
    {
        var best = 0;
        var bestLen = -1.0;
        for (var i = 0; i < pts.Count - 1; i++)
        {
            var l = Dist(pts[i], pts[i + 1]);
            if (l > bestLen) { bestLen = l; best = i; }
        }
        if (bestLen < 14) return;      // 太短的段放不下胶囊

        var m = new Point((pts[best].X + pts[best + 1].X) / 2, (pts[best].Y + pts[best + 1].Y) / 2);
        var text = ShortOf(link.Kind);
        var showCh = link.Kind != LinkKind.Signal;
        var w = text.Length * 12 + (showCh ? 34 : 0) + 16;

        ctx.DrawRectangle(Brushes.White, new Pen(new SolidColorBrush(Color.Parse("#e2e5e7")), 1),
            new RoundedRect(new Rect(m.X - w / 2, m.Y - 9.5, w, 19), 3));
        var x = m.X - w / 2;
        if (showCh)
        {
            ctx.DrawEllipse(new SolidColorBrush(chc), null, new Point(x + 11, m.Y), 3.2, 3.2);
            Text(ctx, $"CH{link.Channel}", new Point(x + 18, m.Y - 6), 10, Color.Parse("#9aa0a5"));
        }
        Text(ctx, text, new Point(x + (showCh ? 42 : 9), m.Y - 6.5), 10.5, Color.Parse("#5f6469"));
    }

    private static void Text(DrawingContext ctx, string s, Point at, double size, Color c)
        => ctx.DrawText(new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, new SolidColorBrush(c)), at);
}
