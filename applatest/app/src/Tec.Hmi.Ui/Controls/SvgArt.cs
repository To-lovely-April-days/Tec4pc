using System.Globalization;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Media;

namespace Tec.Hmi.Ui.Controls;

/// <summary>
/// 只认我们自己那两套图用到的 SVG 子集：
/// 设备线稿（rect/circle/ellipse/line/polygon/polyline/path/text + 渐变 + clipPath）
/// 与原型提取的界面图标（g 组继承、currentColor、stroke-dasharray、fill/stroke-opacity、
/// translate/scale/rotate 变换）。
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
    private readonly Dictionary<string, XElement> _clips = new(StringComparer.Ordinal);
    private readonly HashSet<string> _blurOnly = new(StringComparer.Ordinal);

    /// <summary>viewBox 的左上角。不一定是 (0,0)——设计稿导出的图常带负的 min-y。</summary>
    public double ViewX { get; }
    public double ViewY { get; }
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
        ViewX = box.Length == 4 ? Dbl(box[0], 0) : 0;
        ViewY = box.Length == 4 ? Dbl(box[1], 0) : 0;
        ViewWidth = box.Length == 4 ? Dbl(box[2], 100) : 100;
        ViewHeight = box.Length == 4 ? Dbl(box[3], 100) : 100;
        DeclaredWidth = Dbl(root.Attribute("width")?.Value, ViewWidth);
        DeclaredHeight = Dbl(root.Attribute("height")?.Value, ViewHeight);

        foreach (var g in root.Descendants())
        {
            var id = g.Attribute("id")?.Value;
            if (id is null) continue;
            if (g.Name.LocalName is "linearGradient" or "radialGradient") _gradients[id] = g;
            else if (g.Name.LocalName == "clipPath") _clips[id] = g;
            else if (g.Name.LocalName == "filter"
                     && g.Elements().Any() && g.Elements().All(e => e.Name.LocalName == "feGaussianBlur"))
                _blurOnly.Add(id);      // 纯高斯模糊 = 辉光 / 投影，见下
        }
    }

    public static SvgArt Parse(string xml) => new(XDocument.Parse(xml).Root!);

    public sealed record Paint(Color Tint1, Color Tint2, bool Run1, bool Run2)
    {
        /// <summary>
        /// data-spin 元素的旋转角（度）。泵的转子标着 data-spin="cx cy"，
        /// 泵在跑时外面每拍把角度加一点，转子就转起来（演示 .rotor 的 spin 动画）。
        /// 0 就不推变换，静态图零开销。
        /// </summary>
        public double Spin { get; init; }

        /// <summary>
        /// 两个工位的温控走向：1 升温 / −1 降温 / 0 没在变温。
        /// data-therm 的元素据此染色，data-show="n:heat|cool" 的元素据此画不画。
        /// </summary>
        public int Therm1 { get; init; }
        public int Therm2 { get; init; }

        /// <summary>
        /// 两个工位的桨叶相位 0..1（data-paddle 的元素按它左右摆，同 HMI 釜图）。
        /// null = 这一路没在搅拌，桨叶静止。
        /// </summary>
        public double? Paddle1 { get; init; }
        public double? Paddle2 { get; init; }

        internal int ThermOf(string slot) => slot == "2" ? Therm2 : Therm1;
        internal double? PaddleOf(string slot) => slot == "2" ? Paddle2 : Paddle1;

        /// <summary>currentColor 解析成什么。图标的着色入口。</summary>
        public Color Current { get; init; } = Color.Parse("#3d3d3d");

        /// <summary>
        /// currentColorPale / currentColorDeep 解析成什么：由 Current 按固定关系推出来。
        ///
        /// 指令方块（tile-*.svg）里的线条不是纯白一色，还有两支配角：
        /// 一支淡色画液面 / 次要填充，一支深色画描边 / 指针。
        /// 它们原本是照品牌红手调的三个死值（#E8B8BE / #7E0F1C / #A41626），
        /// 方块改成按模块着色之后，六个模块就要六套三色——手写十八个值不现实，
        /// 也保证不了六套之间的明度关系一致。
        ///
        /// 所以改成从 Current 推：淡＝提白 72%，深＝压黑 23%。
        /// 这两个系数就是从原来那套红里量出来的，换色不换手法。
        /// </summary>
        public Color CurrentPale => Lerp(Current, Colors.White, 0.72);

        public Color CurrentDeep => Lerp(Current, Colors.Black, 0.23);

        private static Color Lerp(Color a, Color b, double t) => Color.FromArgb(
            255,
            (byte)Math.Round(a.R + (b.R - a.R) * t),
            (byte)Math.Round(a.G + (b.G - a.G) * t),
            (byte)Math.Round(a.B + (b.B - a.B) * t));
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
        // 先按 viewBox 原点平移再缩放：min-x / min-y 不为 0 的图（设计稿导出的常见）
        // 直接画会整幅偏出去
        using (ctx.PushTransform(Matrix.CreateTranslation(-ViewX, -ViewY) * Matrix.CreateScale(scale, scale)))
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
        if (name is "defs" or "linearGradient" or "radialGradient" or "filter" or "title"
                 or "clipPath" or "mask" or "pattern" or "marker" or "symbol" or "style" or "desc") return;

        // 只做高斯模糊的滤镜，效果就是一团辉光或一片投影。我们画不了模糊，
        // 照原样画出来是一块硬边的色斑（那片绿辉光会变成一枚绿叶子），
        // 比不画难看得多——所以整枝跳过。别的滤镜照画，免得主体凭空消失
        if (el.Attribute("filter")?.Value is { } fl && fl.StartsWith("url(#", StringComparison.Ordinal)
            && _blurOnly.Contains(fl[5..].TrimEnd(')'))) return;

        // 运行辉光：通道没在跑就不画，而不是画成灰的
        var run = el.Attribute("data-run")?.Value;
        if (run == "1" && !paint.Run1) return;
        if (run == "2" && !paint.Run2) return;

        // data-show="1:heat" / "2:cool"：只有该工位真处于这个温控走向才画
        // （升温 / 降温标）。没在变温时两枚都不画，不留一个灰的占位
        if (el.Attribute("data-show")?.Value is { } show)
        {
            var f = show.Split(':');
            if (f.Length != 2) return;
            var want = f[1] switch { "heat" => 1, "cool" => -1, _ => 0 };
            if (paint.ThermOf(f[0]) != want) return;
        }

        var style = Merge(inherited, el);

        var opacity = Dbl(el.Attribute("opacity")?.Value, 1);
        IDisposable? pushedOpacity = null;
        if (opacity < 1) pushedOpacity = ctx.PushOpacity(opacity);
        IDisposable? pushedTransform = null;
        if (el.Attribute("transform")?.Value is { } tr && ParseTransform(tr) is { } m)
            pushedTransform = ctx.PushTransform(m);
        // data-spin="cx cy"：绕给定点转 Spin 度（泵的转子）。角度为 0 时不推
        IDisposable? pushedSpin = null;
        if (paint.Spin != 0 && el.Attribute("data-spin")?.Value is { } sp)
        {
            var c = sp.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (c.Length == 2)
            {
                double scx = Dbl(c[0], 0), scy = Dbl(c[1], 0);
                pushedSpin = ctx.PushTransform(
                    Matrix.CreateTranslation(-scx, -scy)
                    * Matrix.CreateRotation(paint.Spin * Math.PI / 180)
                    * Matrix.CreateTranslation(scx, scy));
            }
        }
        // data-paddle="工位 轴x"：桨叶按该工位的相位左右摆（scaleX，轴心不动），
        // 与 HMI 釜图那把桨同一套动作；这一路没在搅拌就不推变换，图是静的
        IDisposable? pushedPaddle = null;
        if (el.Attribute("data-paddle")?.Value is { } pad)
        {
            var f = pad.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (f.Length == 2 && paint.PaddleOf(f[0]) is { } phase)
            {
                var ax = Dbl(f[1], 0);
                var sx = 0.6 + 0.4 * Math.Cos(phase * Math.PI * 2);
                pushedPaddle = ctx.PushTransform(
                    Matrix.CreateTranslation(-ax, 0)
                    * Matrix.CreateScale(sx, 1)
                    * Matrix.CreateTranslation(ax, 0));
            }
        }
        // clip-path="url(#id)"：不裁的话液面那颗椭圆会整颗露在液面之上
        IDisposable? pushedClip = null;
        if (ClipOf(el) is { } clipGeo) pushedClip = ctx.PushGeometryClip(clipGeo);

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
                    if (Path(d) is not { } geo) break;
                    ctx.DrawGeometry(fill, pen, geo);
                    break;
                }
                case "polygon":
                case "polyline":
                {
                    var pts = Points(el.Attribute("points")?.Value);
                    if (pts.Count < 2) break;
                    ctx.DrawGeometry(fill, pen, new PolylineGeometry(pts, name == "polygon"));
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
            pushedClip?.Dispose();
            pushedPaddle?.Dispose();
            pushedSpin?.Dispose();
            pushedTransform?.Dispose();
            pushedOpacity?.Dispose();
        }
    }

    /// <summary>
    /// path 的 d 串 → Geometry，**认串缓存**。
    ///
    /// 从前每次 Render 都现解析一遍：Geometry.Parse 是把一条 "M 12,3 L…" 的
    /// 字符串重新走一遍语法、重新建一份几何。而 Render 是**重画就调**——
    /// 鼠标扫过谁、谁被作废一次，就重新解析一遍。一枚指令图标里十来条 path，
    /// 一栏三十一枚，鼠标划过去一趟就是几千次无谓的字符串解析，
    /// 外加同样多份短命的几何对象喂给 GC。
    ///
    /// 几何是不动的，认串缓存就够：同一条 d 串永远出同一份几何，
    /// 大小写、空格都一模一样才算同一条（Ordinal）。缓存的是形状，
    /// 不是颜色、不是缩放——那两样每次照旧现算，画出来一个像素不差。
    ///
    /// 解析不出来的（不该有，但 SVG 是外面来的）记一个 null 进去，
    /// 免得一条坏串每帧都去撞一次异常。
    ///
    /// 只在界面线程上碰（Render / Measure 都是），所以不用锁。
    /// </summary>
    private static readonly Dictionary<string, Geometry?> Paths = new(StringComparer.Ordinal);

    private static Geometry? Path(string d)
    {
        if (Paths.TryGetValue(d, out var hit)) return hit;
        Geometry? geo;
        try { geo = Geometry.Parse(d); }
        catch { geo = null; }
        Paths[d] = geo;
        return geo;
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

        // data-therm="1|2"：夹套跟着该工位的温控走向染色（升温暖、降温冷，
        // 与 HMI 釜图同一对颜色）；没在变温就用图里的原色
        if (el.Attribute("data-therm")?.Value is { } therm)
        {
            var hex = paint.ThermOf(therm) switch
            { 1 => "#EFDCCB", -1 => "#D8E7F2", _ => null };
            if (hex is not null) return new SolidColorBrush(Color.Parse(hex));
        }

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
        // 见 Paint.CurrentPale 那段说明：指令方块里的淡色 / 深色配角
        if (value == "currentColorPale") return new SolidColorBrush(paint.CurrentPale);
        if (value == "currentColorDeep") return new SolidColorBrush(paint.CurrentDeep);

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
            stops.Add(new GradientStop(color, Frac(s.Attribute("offset")?.Value, 0)));
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
            StartPoint = new RelativePoint(Frac(g.Attribute("x1")?.Value, 0), Frac(g.Attribute("y1")?.Value, 0),
                                           RelativeUnit.Relative),
            EndPoint = new RelativePoint(Frac(g.Attribute("x2")?.Value, 1), Frac(g.Attribute("y2")?.Value, 0),
                                         RelativeUnit.Relative)
        };
        foreach (var s in stops) brush.GradientStops.Add(s);
        return brush;
    }

    /// <summary>
    /// 渐变里的比例值：既可以写 0.38，也可以写 "37.796%"。设计稿导出的图几乎全是
    /// 百分号写法（这份图 50 条线性渐变里 44 条、172 个 stop 里 145 个），
    /// 按普通数字读会全部落回缺省值——整幅图的渐变方向和分段就都塌了。
    /// </summary>
    private static double Frac(string? text, double fallback)
    {
        if (text is null) return fallback;
        text = text.Trim();
        return text.EndsWith("%", StringComparison.Ordinal)
            ? Dbl(text[..^1], fallback * 100) / 100
            : Dbl(text, fallback);
    }

    /// <summary>points="x y x y …"（空格或逗号分隔都认）。</summary>
    private static List<Point> Points(string? text)
    {
        var list = new List<Point>();
        if (text is null) return list;
        var n = text.Split(new[] { ' ', ',', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i + 1 < n.Length; i += 2) list.Add(new Point(Dbl(n[i], 0), Dbl(n[i + 1], 0)));
        return list;
    }

    /// <summary>取形状本身的几何（给 clipPath 用，不管填色描边）。</summary>
    private static Geometry? ShapeGeometry(XElement el)
    {
        Geometry? geo = el.Name.LocalName switch
        {
            "path" => el.Attribute("d")?.Value is { } d && !string.IsNullOrWhiteSpace(d)
                ? Safe(d) : null,
            "rect" => new RectangleGeometry(new Rect(
                Dbl(el.Attribute("x")?.Value, 0), Dbl(el.Attribute("y")?.Value, 0),
                Dbl(el.Attribute("width")?.Value, 0), Dbl(el.Attribute("height")?.Value, 0))),
            "circle" => new EllipseGeometry(Circle(el)),
            "ellipse" => new EllipseGeometry(Oval(el)),
            "polygon" or "polyline" => Points(el.Attribute("points")?.Value) is { Count: > 1 } p
                ? new PolylineGeometry(p, true) : null,
            _ => null
        };
        if (geo is not null && el.Attribute("transform")?.Value is { } tr && ParseTransform(tr) is { } m)
            geo.Transform = new MatrixTransform(m);
        return geo;

        // **这一条不走缓存。**下面几行会往返回的几何上挂 Transform，
        // 而缓存里那一份是所有人共用的——挂上去就把别人的形状一起改了。
        // 这条路只服务 clip-path（每张图至多一两处，还不是每帧都走），
        // 不值得为它去做一份可变的副本
        static Geometry? Safe(string d)
        {
            try { return Geometry.Parse(d); }
            catch { return null; }
        }

        static Rect Circle(XElement e)
        {
            var r = Dbl(e.Attribute("r")?.Value, 0);
            return new Rect(Dbl(e.Attribute("cx")?.Value, 0) - r, Dbl(e.Attribute("cy")?.Value, 0) - r, r * 2, r * 2);
        }

        static Rect Oval(XElement e)
        {
            double rx = Dbl(e.Attribute("rx")?.Value, 0), ry = Dbl(e.Attribute("ry")?.Value, 0);
            return new Rect(Dbl(e.Attribute("cx")?.Value, 0) - rx, Dbl(e.Attribute("cy")?.Value, 0) - ry, rx * 2, ry * 2);
        }
    }

    /// <summary>clip-path="url(#id)" → 那个 clipPath 里所有形状的并集。</summary>
    private Geometry? ClipOf(XElement el)
    {
        var v = el.Attribute("clip-path")?.Value;
        if (v is null || !v.StartsWith("url(#", StringComparison.Ordinal)) return null;
        var id = v[5..].TrimEnd(')');
        if (!_clips.TryGetValue(id, out var def)) return null;

        var group = new GeometryGroup();
        foreach (var child in def.Elements())
            if (ShapeGeometry(child) is { } g) group.Children.Add(g);
        return group.Children.Count > 0 ? group : null;
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
            // rotate(a) 绕原点，rotate(a cx cy) 绕指定点。指令图标里
            // 搅拌桨、注射器、滴管都是斜着的，不转就全立正了
            else if (op == "rotate" && args.Length >= 1)
            {
                var r = Matrix.CreateRotation(args[0] * Math.PI / 180);
                if (args.Length >= 3)
                    r = Matrix.CreateTranslation(-args[1], -args[2]) * r
                        * Matrix.CreateTranslation(args[1], args[2]);
                m = r * m;
                any = true;
            }
        }
        return any ? m : null;
    }

    private static double Dbl(string? s, double fallback)
        => s is not null && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
