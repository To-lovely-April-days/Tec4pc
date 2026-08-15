using System.Globalization;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Media;

namespace Tec.App.Controls;

/// <summary>
/// 只认我们自己那套设备线稿用到的 SVG 子集：
/// g / rect / circle / ellipse / line / path / text + linearGradient。
/// 刻意不引第三方 SVG 库——这些图是我们自己画的，形状可控，
/// 多一个渲染库反而多一处版本风险。
///
/// 两个约定来自导出脚本：
///   data-tint="1|2"  该元素用通道色（停用通道转灰）
///   data-run ="1|2"  该元素是运行辉光，只在通道在跑时画
/// </summary>
public sealed class SvgArt
{
    private readonly XElement _root;
    private readonly Dictionary<string, XElement> _gradients = new(StringComparer.Ordinal);

    public double ViewWidth { get; }
    public double ViewHeight { get; }

    private SvgArt(XElement root)
    {
        _root = root;
        var box = (root.Attribute("viewBox")?.Value ?? "0 0 100 100")
            .Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
        ViewWidth = box.Length == 4 ? Dbl(box[2], 100) : 100;
        ViewHeight = box.Length == 4 ? Dbl(box[3], 100) : 100;

        foreach (var g in root.Descendants())
        {
            if (g.Name.LocalName is not ("linearGradient" or "radialGradient")) continue;
            var id = g.Attribute("id")?.Value;
            if (id is not null) _gradients[id] = g;
        }
    }

    public static SvgArt Parse(string xml) => new(XDocument.Parse(xml).Root!);

    public sealed record Paint(Color Tint1, Color Tint2, bool Run1, bool Run2);

    public void Render(DrawingContext ctx, double scale, Paint paint)
    {
        using (ctx.PushTransform(Matrix.CreateScale(scale, scale)))
            foreach (var el in _root.Elements())
                Draw(ctx, el, paint);
    }

    private void Draw(DrawingContext ctx, XElement el, Paint paint)
    {
        var name = el.Name.LocalName;
        if (name is "defs" or "linearGradient" or "radialGradient" or "filter") return;

        // 运行辉光：通道没在跑就不画，而不是画成灰的
        var run = el.Attribute("data-run")?.Value;
        if (run == "1" && !paint.Run1) return;
        if (run == "2" && !paint.Run2) return;

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
                foreach (var child in el.Elements()) Draw(ctx, child, paint);
                return;
            }

            var fill = Brush(el, "fill", paint, run);
            var pen = Pen(el, paint);

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

    private IBrush? Brush(XElement el, string attr, Paint paint, string? run)
    {
        var value = el.Attribute(attr)?.Value;

        // 没写 fill 的元素：有描边的按"只描边不填充"处理，没描边的才用 SVG 缺省的黑。
        // 少了这一条，那些只画线的 path 会被涂成黑块。
        if (value is null)
            value = el.Attribute("stroke") is null ? "#000000" : "none";
        if (value == "none") return null;

        // 辉光用通道色的半透明实心近似径向渐变：
        // 省掉一个在 Avalonia 各小版本间改过签名的 API
        if (run is not null)
            return new SolidColorBrush(run == "2" ? paint.Tint2 : paint.Tint1, 0.30);

        if (el.Attribute("data-tint")?.Value is { } tint)
            return new SolidColorBrush(tint == "2" ? paint.Tint2 : paint.Tint1);

        return BrushOf(value);
    }

    private IPen? Pen(XElement el, Paint paint)
    {
        var stroke = el.Attribute("stroke")?.Value;
        if (stroke is null || stroke == "none") return null;

        IBrush? brush = el.Attribute("data-tint")?.Value is { } tint
            ? new SolidColorBrush(tint == "2" ? paint.Tint2 : paint.Tint1)
            : BrushOf(stroke);
        if (brush is null) return null;

        var pen = new Pen(brush, Dbl(el.Attribute("stroke-width")?.Value, 1));
        pen.LineCap = el.Attribute("stroke-linecap")?.Value switch
        {
            "round" => PenLineCap.Round,
            "square" => PenLineCap.Square,
            _ => PenLineCap.Flat
        };
        pen.LineJoin = el.Attribute("stroke-linejoin")?.Value switch
        {
            "round" => PenLineJoin.Round,
            "bevel" => PenLineJoin.Bevel,
            _ => PenLineJoin.Miter
        };
        return pen;
    }

    private IBrush? BrushOf(string value)
    {
        value = value.Trim();
        if (value.Length == 0 || value == "none") return null;

        if (value.StartsWith("url(", StringComparison.Ordinal))
        {
            var id = value.Trim('u', 'r', 'l', '(', ')', '#', ' ');
            var start = value.IndexOf('#');
            var end = value.IndexOf(')');
            if (start >= 0 && end > start) id = value[(start + 1)..end];
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

        // 径向渐变在 Avalonia 各小版本间的半径属性改过名字；这里用中心色近似，
        // 它只用在辉光与探头尖端，视觉差别可以忽略。
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
