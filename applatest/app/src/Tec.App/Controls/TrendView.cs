using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Tec.App.Controls;

public sealed class TrendSeries
{
    /// <summary>(相对通道秒, 值)。</summary>
    public required IReadOnlyList<(double T, double V)> Points { get; init; }
}

public sealed class TrendModel
{
    public double AxisFrom { get; init; }
    public double AxisTo { get; init; } = 600;
    public double NowSec { get; init; }
    public bool HasPh { get; init; }
    /// <summary>刻度秒 → 显示文本（墙钟或相对时长）。</summary>
    public required Func<double, string> Label { get; init; }
    public TrendSeries? Tr { get; init; }
    public TrendSeries? Tj { get; init; }
    public TrendSeries? Dt { get; init; }
    public TrendSeries? Ph { get; init; }
}

/// <summary>
/// 趋势曲线（原型 renderTrend 的 1:1）：
/// 温度轴 −50~190 ℃ 左侧，pH 轴 0~14 右侧（绿字，只有配了 pH 探头才画），
/// Tr 红 1.8 / Tj 蓝 1.6 / Tr−Tj 紫 1.3（×4+40 折算进温度轴）/ pH 绿虚线，
/// 红色当前时刻线 + 顶部圆点。
/// </summary>
public sealed class TrendView : Control
{
    public static readonly StyledProperty<TrendModel?> ModelProperty =
        AvaloniaProperty.Register<TrendView, TrendModel?>(nameof(Model));

    private const double L = 44, R = 40, T = 14, B = 26;

    static TrendView() => AffectsRender<TrendView>(ModelProperty);

    public TrendModel? Model
    {
        get => GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    public override void Render(DrawingContext ctx)
    {
        var m = Model;
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (m is null || w < 60 || h < 40) return;

        var span = Math.Max(m.AxisTo - m.AxisFrom, 1);
        double Xt(double sec) => L + (sec - m.AxisFrom) / span * (w - L - R);
        double Y(double v) => T + (1 - (v + 50) / 240.0) * (h - T - B);
        double Y2(double v) => T + (1 - v / 14.0) * (h - T - B);

        var gridPen = new Pen(new SolidColorBrush(Color.Parse("#ececec")), 1);
        var gridPen2 = new Pen(new SolidColorBrush(Color.Parse("#f2f2f2")), 1);
        var axis = new SolidColorBrush(Color.Parse("#8d8d8d"));
        var green = new SolidColorBrush(Color.Parse("#2f8f49"));

        // 横向温度格线 −40..180 每 40 ℃
        for (var tv = -40; tv <= 180; tv += 40)
        {
            ctx.DrawLine(gridPen, new Point(L, Y(tv)), new Point(w - R, Y(tv)));
            Text(ctx, tv.ToString(CultureInfo.InvariantCulture), new Point(L - 6, Y(tv) - 5), 10, axis, right: true);
        }
        if (m.HasPh)
            for (var ph = 0; ph <= 14; ph += 7)
                Text(ctx, ph.ToString(CultureInfo.InvariantCulture), new Point(w - R + 6, Y2(ph) - 5), 10, green);

        // 纵向时间格线 + 刻度文本
        foreach (var tick in Ticks(m.AxisFrom, m.AxisTo, 8))
        {
            var x = Xt(tick);
            ctx.DrawLine(gridPen2, new Point(x, T), new Point(x, h - B));
            Text(ctx, m.Label(tick), new Point(x, h - 18), 10, axis, center: true);
        }

        Text(ctx, "℃", new Point(L - 30, T - 3), 10, axis);
        if (m.HasPh) Text(ctx, "pH", new Point(w - R + 12, T - 3), 10, green);

        // 曲线：紫 dT（×4+40）→ 蓝 Tj → 红 Tr → 绿 pH 虚线（原型的绘制顺序）
        Curve(ctx, m.Dt, p => new Point(Xt(p.T), Y(p.V * 4 + 40)), "#b56cc9", 1.3, opacity: 0.8);
        Curve(ctx, m.Tj, p => new Point(Xt(p.T), Y(p.V)), "#3f6fd8", 1.6);
        Curve(ctx, m.Tr, p => new Point(Xt(p.T), Y(p.V)), "#e02020", 1.8);
        Curve(ctx, m.Ph, p => new Point(Xt(p.T), Y2(p.V)), "#2f8f49", 1.4, dash: true);

        // 当前时刻
        var nowX = Xt(m.NowSec);
        var nowPen = new Pen(new SolidColorBrush(Color.Parse("#e02020")), 1.4);
        ctx.DrawLine(nowPen, new Point(nowX, T), new Point(nowX, h - B));
        ctx.DrawEllipse(new SolidColorBrush(Color.Parse("#e02020")), null, new Point(nowX, T), 3, 3);
    }

    private static void Curve(DrawingContext ctx, TrendSeries? s, Func<(double T, double V), Point> map,
                              string color, double width, bool dash = false, double opacity = 1)
    {
        if (s is null || s.Points.Count < 2) return;
        var pen = new Pen(new SolidColorBrush(Color.Parse(color), opacity), width)
        { LineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        if (dash) pen.DashStyle = new DashStyle(new double[] { 5 / width, 3 / width }, 0);

        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(map(s.Points[0]), false);
            for (var i = 1; i < s.Points.Count; i++) g.LineTo(map(s.Points[i]));
            g.EndFigure(false);
        }
        ctx.DrawGeometry(null, pen, geo);
    }

    /// <summary>好看的刻度（原型 axisTicks 的简化：60/300/600/1800/3600 的倍数）。</summary>
    public static IEnumerable<double> Ticks(double a, double b, int n)
    {
        var span = Math.Max(b - a, 1);
        double[] steps = { 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 14400 };
        var step = steps.FirstOrDefault(s => span / s <= n);
        if (step == 0) step = Math.Ceiling(span / n / 3600) * 3600;
        var t = Math.Ceiling(a / step) * step;
        for (; t <= b; t += step) yield return t;
    }

    private static void Text(DrawingContext ctx, string s, Point at, double size, IBrush brush,
                             bool center = false, bool right = false)
    {
        var ft = new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, brush);
        var x = center ? at.X - ft.Width / 2 : right ? at.X - ft.Width : at.X;
        ctx.DrawText(ft, new Point(x, at.Y));
    }
}
