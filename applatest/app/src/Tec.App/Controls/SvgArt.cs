using System.Globalization;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Media;

namespace Tec.App.Controls;

/// <summary>
/// 只认我们自己那两套图用到的 SVG 子集：
/// 设备线稿（rect/circle/ellipse/line/path/text + 渐变）与原型提取的界面图标
/// （g 组继承、currentColor、stroke-dasharray、fill/stroke-opacity）。
/// 刻意不引第三方 SVG 库——图是我们自己的，形状可控。
///
/// 设备图的两个约定来自导出脚本：
///   data-tint="1|2"  该元素用通道色（停用通道转灰）
///   data-run ="1|2"  该元素是运行辉光，只在通道在跑时画
/// </summary>
public sealed class SvgArt
{
    private readonly XElement _root;
    private readonly Dictionary<string, XElement> _gradients = new(StringComparer.Ordinal);

    public double ViewWidth { get; }
    public double ViewHeight { get; }
    /// <summary>svg 标签自带的 width/height（图标用它定显示尺寸）。</summary>
    public double DeclaredWidth { get; }
    public double DeclaredHeight { get; }

    private SvgArt(XElement root)
    {
        _root = root;
        var box = (root.Attribute("viewBox")?.Value ?? "0 0 100 100")
            .Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
        ViewWidth = box.Length == 4 ? Dbl(box[2], 100) : 100;
        ViewHeight = box.Length == 4 ? Dbl(box[3], 100) : 100;
        DeclaredWidth = Dbl(root.Attribute("width")?.Value, ViewWidth);
        DeclaredHeight = Dbl(root.Attribute("height")?.Value, ViewHeight);

        foreach (var g in root.Descendants())
        {
            if (g.Name.LocalName is not ("linearGradient" or "radialGradient")) continue;
            var id = g.Attribute("id")?.Value;
            if (id is not null) _gradients[id] = g;
        }
    }

    public static SvgArt Parse(string xml) => new(XDocument.Parse(xml).Root!);

    public sealed record Paint(Color Tint1, Color Tint2, bool Run1, bool Run2)
    {
        /// <summary>currentColor 解析成什么。图标的着色入口。</summary>
        public Color Current { get; init; } = Color.Parse("#3d3d3d");
        /// <summary>非空时：所有实体描边强制此色（原型 iconWhite 的白描边版）。</summary>
        public Color? StrokeOverride { get; init; }
        /// <summary>
        /// 非空时：实心填充也强制此色。模拟模式那四个图标（play/pause/stop/step）
        /// 在原型里是铺在黑条上的，fill 写死 #fff——原样搬到白底圆钮上就整个看不见了。
        /// 这里只换颜色不动几何，图还是提取出来的那一份。
        /// </summary>
        public Color? FillOverride { get; init; }
    }

    /// <summary>可继承的表现属性（SVG 规范里 fill/stroke 一类就是继承的）。</summary>
    private sealed record Style(
        string? Fill, string? Stroke, double StrokeWidth,
        PenLineCap Cap, PenLineJoin Join, string? Dash, double FillOpacity, double StrokeOpacity)
    {
        public static readonly Style Root = new(null, null, 1, PenLineCap.Flat, PenLineJoin.Miter, null, 1, 1);
    }

    public void Render(DrawingContext ctx, double scale, Paint paint)
    {
        using (ctx.PushTransform(Matrix.CreateScale(scale, scale)))
        {
            var style = Merge(Style.Root, _root);   // 根 svg 上的 fill="none"、linecap 会继承下去
            foreach (var el in _root.Elements())
                Draw(ctx, el, paint, style);
        }
    }

    private static Style Merge(Style s, XElement el)
    {
        string? A(string n) => el.Attribute(n)?.Value;
        return new Style(
            A("fill") ?? s.Fill,
            A("stroke") ?? s.Stroke,
            A("stroke-width") is { } w ? Dbl(w, s.StrokeWidth) : s.StrokeWidth,
            A("stroke-linecap") switch { "round" => PenLineCap.Round, "square" => PenLineCap.Square, null => s.Cap, _ => PenLineCap.Flat },
            A("stroke-linejoin") switch { "round" => PenLineJoin.Round, "bevel" => PenLineJoin.Bevel, null => s.Join, _ => PenLineJoin.Miter },
            A("stroke-dasharray") ?? s.Dash,
            A("fill-opacity") is { } fo ? Dbl(fo, s.FillOpacity) : s.FillOpacity,
            A("stroke-opacity") is { } so ? Dbl(so, s.StrokeOpacity) : s.StrokeOpacity);
    }

    private void Draw(DrawingContext ctx, XElement el, Paint paint, Style inherited)
    {
        var name = el.Name.LocalName;
        if (name is "defs" or "linearGradient" or "radialGradient" or "filter" or "title") return;

        // 运行辉光：通道没在跑就不画，而不是画成灰的
        var run = el.Attribute("data-run")?.Value;
        if (run == "1" && !paint.Run1) return;
        if (run == "2" && !paint.Run2) return;

        var style = Merge(inherited, el);

        var opacity = Dbl(el.Attribute("opacity")?.Value, 1);
        IDisposable? pushedOpacity = null;
        if (opacity < 1) pushedOpacity = ctx.PushOpacity(opacity);
        IDisposable? pushedTransform = null;
        if (el.Attribute("transform")?.Value is { } tr && ParseTransform(tr) is { } m)
            pushedTransform = ctx.PushTransform(m);

        try
        {
            if (name == "g")
            {
                foreach (var child in el.Elements()) Draw(ctx, child, paint, style);
                return;
            }

            var fill = FillBrush(el, paint, style, run);
            var pen = StrokePen(el, paint, style);

            switch (name)
            {
                case "rect":
                {
                    var r = new Rect(Dbl(el.Attribute("x")?.Value, 0), Dbl(el.Attribute("y")?.Value, 0),
                                     Dbl(el.Attribute("width")?.Value, 0), Dbl(el.Attribute("height")?.Value, 0));
                    var rx = Dbl(el.Attribute("rx")?.Value, 0);
                    var ry = Dbl(el.Attribute("ry")?.Value, rx);
                    if (rx > 0 || ry > 0) ctx.DrawRectangle(fill, pen, r, rx, ry);
                    else ctx.DrawRectangle(fill, pen, r);
                    break;
                }
                case "circle":
                {
                    var c = new Point(Dbl(el.Attribute("cx")?.Value, 0), Dbl(el.Attribute("cy")?.Value, 0));
                    var rad = Dbl(el.Attribute("r")?.Value, 0);
                    ctx.DrawEllipse(fill, pen, c, rad, rad);
                    break;
                }
                case "ellipse":
                {
                    var c = new Point(Dbl(el.Attribute("cx")?.Value, 0), Dbl(el.Attribute("cy")?.Value, 0));
                    ctx.DrawEllipse(fill, pen, c,
                        Dbl(el.Attribute("rx")?.Value, 0), Dbl(el.Attribute("ry")?.Value, 0));
                    break;
                }
                case "line":
                {
                    if (pen is null) break;
                    ctx.DrawLine(pen,
                        new Point(Dbl(el.Attribute("x1")?.Value, 0), Dbl(el.Attribute("y1")?.Value, 0)),
                        new Point(Dbl(el.Attribute("x2")?.Value, 0), Dbl(el.Attribute("y2")?.Value, 0)));
                    break;
                }
                case "path":
                {
                    var d = el.Attribute("d")?.Value;
                    if (string.IsNullOrWhiteSpace(d)) break;
                    Geometry geo;
                    try { geo = Geometry.Parse(d); }
                    catch { break; }
                    ctx.DrawGeometry(fill, pen, geo);
                    break;
                }
                case "text":
                {
                    var text = el.Value;
                    if (string.IsNullOrEmpty(text)) break;
                    var size = Dbl(el.Attribute("font-size")?.Value, 8);
                    var family = el.Attribute("font-family")?.Value ?? "Segoe UI";
                    var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                                               new Typeface(family), size, fill ?? Brushes.Black);
                    var x = Dbl(el.Attribute("x")?.Value, 0);
                    var y = Dbl(el.Attribute("y")?.Value, 0);
                    if (el.Attribute("text-anchor")?.Value == "middle") x -= ft.Width / 2;
                    else if (el.Attribute("text-anchor")?.Value == "end") x -= ft.Width;
                    ctx.DrawText(ft, new Point(x, y - ft.Baseline));
                    break;
                }
            }
        }
        finally
        {
            pushedTransform?.Dispose();
            pushedOpacity?.Dispose();
        }
    }

    private IBrush? FillBrush(XElement el, Paint paint, Style style, string? run)
    {
        // 运行辉光：图里自己写了填色就用图里的（反应器写的是主题紫的径向渐变），
        // 没写才退回「通道色刷一层」。通道身份由釜底那条色带表示，辉光不必再兼职——
        // 从前这里无条件覆盖，改图里的渐变一点反应都没有。
        if (run is not null && el.Attribute("fill") is null)
            return new SolidColorBrush(run == "2" ? paint.Tint2 : paint.Tint1, 0.30);
        if (el.Attribute("data-tint")?.Value is { } tint && el.Attribute("fill") is not null)
            return new SolidColorBrush(tint == "2" ? paint.Tint2 : paint.Tint1);

        // 继承后的 fill。没写任何 fill（含祖先）的图形按 SVG 缺省黑；
        // 但只有描边的元素（写了 stroke 没写 fill）按不填充处理。
        var value = style.Fill;
        if (value is null)
            value = style.Stroke is null ? "#000000" : "none";
        if (value == "none") return null;

        if (paint.FillOverride is { } forcedFill)
            return new SolidColorBrush(forcedFill, style.FillOpacity);

        var brush = BrushOf(value, paint);
        return brush is SolidColorBrush sc && style.FillOpacity < 1
            ? new SolidColorBrush(sc.Color, style.FillOpacity)
            : brush;
    }

    private IPen? StrokePen(XElement el, Paint paint, Style style)
    {
        var stroke = style.Stroke;
        if (stroke is null || stroke == "none") return null;

        IBrush? brush;
        if (paint.StrokeOverride is { } forced)
            brush = new SolidColorBrush(forced);            // iconWhite：全部描边改白
        else if (el.Attribute("data-tint")?.Value is { } tint && el.Attribute("stroke") is not null)
            brush = new SolidColorBrush(tint == "2" ? paint.Tint2 : paint.Tint1);
        else
            brush = BrushOf(stroke, paint);
        if (brush is null) return null;
        if (brush is SolidColorBrush sc && style.StrokeOpacity < 1)
            brush = new SolidColorBrush(sc.Color, style.StrokeOpacity);

        var pen = new Pen(brush, style.StrokeWidth)
        {
            LineCap = style.Cap,
            LineJoin = style.Join
        };
        if (style.Dash is { } dash)
        {
            var parts = dash.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(v => Dbl(v, 0) / Math.Max(style.StrokeWidth, 0.01)).ToArray();
            if (parts.Length > 0) pen.DashStyle = new DashStyle(parts, 0);
        }
        return pen;
    }

    private IBrush? BrushOf(string value, Paint paint)
    {
        value = value.Trim();
        if (value.Length == 0 || value == "none") return null;
        if (value == "currentColor") return new SolidColorBrush(paint.Current);

        if (value.StartsWith("url(", StringComparison.Ordinal))
        {
            var start = value.IndexOf('#');
            var end = value.IndexOf(')');
            if (start < 0 || end <= start) return null;
            var id = value[(start + 1)..end];
            return _gradients.TryGetValue(id, out var g) ? GradientOf(g) : null;
        }

        try { return new SolidColorBrush(Color.Parse(value)); }
        catch { return null; }
    }

    private IBrush? GradientOf(XElement g)
    {
        var stops = new GradientStops();
        foreach (var s in g.Elements())
        {
            if (s.Name.LocalName != "stop") continue;
            var color = Color.Parse(s.Attribute("stop-color")?.Value ?? "#000000");
            var alpha = Dbl(s.Attribute("stop-opacity")?.Value, 1);
            if (alpha < 1) color = new Color((byte)Math.Round(alpha * 255), color.R, color.G, color.B);
            stops.Add(new GradientStop(color, Dbl(s.Attribute("offset")?.Value, 0)));
        }
        if (stops.Count == 0) return null;

        // 径向渐变：Avalonia 的 RadialGradientBrush 在这条渲染路径上画不出东西
        // （试过 objectBoundingBox 的相对半径，连纯色都不出），所以仍用首个 stop
        // 的颜色近似成纯色。需要辉光的地方就在图里叠几个透明度递减的实心椭圆，
        // 那个一定画得出来。
        if (g.Name.LocalName == "radialGradient")
            return new SolidColorBrush(stops[0].Color);

        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(Dbl(g.Attribute("x1")?.Value, 0), Dbl(g.Attribute("y1")?.Value, 0),
                                           RelativeUnit.Relative),
            EndPoint = new RelativePoint(Dbl(g.Attribute("x2")?.Value, 1), Dbl(g.Attribute("y2")?.Value, 0),
                                         RelativeUnit.Relative)
        };
        foreach (var s in stops) brush.GradientStops.Add(s);
        return brush;
    }

    private static Matrix? ParseTransform(string text)
    {
        var m = Matrix.Identity;
        var any = false;
        foreach (var part in text.Split(')'))
        {
            var open = part.IndexOf('(');
            if (open < 0) continue;
            var op = part[..open].Trim();
            var args = part[(open + 1)..]
                .Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(a => Dbl(a, 0)).ToArray();
            if (op == "translate" && args.Length >= 1)
            {
                m = Matrix.CreateTranslation(args[0], args.Length > 1 ? args[1] : 0) * m;
                any = true;
            }
            else if (op == "scale" && args.Length >= 1)
            {
                m = Matrix.CreateScale(args[0], args.Length > 1 ? args[1] : args[0]) * m;
                any = true;
            }
        }
        return any ? m : null;
    }

    private static double Dbl(string? s, double fallback)
        => s is not null && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
