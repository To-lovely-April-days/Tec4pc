using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Tec.App.Controls;

/// <summary>
/// 控温输出条：±100 % 的面积图，零线在中间，正的往上（加热）负的往下（制冷）。
///
/// **它得和温度曲线共用一条时间轴**，贴在温度图下面看才有意义——
/// 放大时最要紧的那个问题是「夹套已经满功率了，釜温还压不住」，
/// 那是两条曲线**对着看**才看得出来的事，单独摆一张图等于没画。
///
/// 满出力（|输出| ≥ 满量程线）的那几段单独标红：那正是「余量用完了」的时刻。
/// </summary>
public sealed class DutyStripView : Control
{
    public static readonly StyledProperty<IReadOnlyList<(double T, double V)>?> PointsProperty =
        AvaloniaProperty.Register<DutyStripView, IReadOnlyList<(double T, double V)>?>(nameof(Points));

    public static readonly StyledProperty<double> AxisFromProperty =
        AvaloniaProperty.Register<DutyStripView, double>(nameof(AxisFrom));

    public static readonly StyledProperty<double> AxisToProperty =
        AvaloniaProperty.Register<DutyStripView, double>(nameof(AxisTo), 600);

    /// <summary>满量程（%）。真机上是温控器的 LIMITED，读不到就按 100。</summary>
    public static readonly StyledProperty<double> CeilingProperty =
        AvaloniaProperty.Register<DutyStripView, double>(nameof(Ceiling), 100);

    static DutyStripView() =>
        AffectsRender<DutyStripView>(PointsProperty, AxisFromProperty, AxisToProperty, CeilingProperty);

    public DutyStripView() => ClipToBounds = true;

    public IReadOnlyList<(double T, double V)>? Points
    {
        get => GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }
    public double AxisFrom { get => GetValue(AxisFromProperty); set => SetValue(AxisFromProperty, value); }
    public double AxisTo { get => GetValue(AxisToProperty); set => SetValue(AxisToProperty, value); }
    public double Ceiling { get => GetValue(CeilingProperty); set => SetValue(CeilingProperty, value); }

    // 左右留白与温度图一致（TrendView 的 L / R），两张图的时间轴才对得齐
    private const double L = 44, R = 40, T = 8, B = 14;

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 60 || h < 30) return;

        var span = Math.Max(AxisTo - AxisFrom, 1);
        double X(double sec) => L + (sec - AxisFrom) / span * (w - L - R);
        double Y(double pct) => T + (1 - (pct + 100) / 200.0) * (h - T - B);

        var grid = new Pen(new SolidColorBrush(Color.Parse("#ececec")), 1);
        var axisInk = new SolidColorBrush(Color.Parse("#8d8d8d"));

        // 零线粗一点：正负两个方向是加热与制冷，分界线要一眼看见
        ctx.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#c8c8c8")), 1.2),
                     new Point(L, Y(0)), new Point(w - R, Y(0)));

        var cap = Math.Clamp(Math.Abs(Ceiling) is var c && c > 1 ? c : 100, 1, 100);
        foreach (var v in new[] { cap, -cap })
        {
            // 满量程线画成红虚线的效果（短划），贴着它跑就是余量用完了
            var pen = new Pen(new SolidColorBrush(Color.Parse("#e0b4ad")), 1,
                              new DashStyle(new double[] { 4, 3 }, 0));
            ctx.DrawLine(pen, new Point(L, Y(v)), new Point(w - R, Y(v)));
        }
        foreach (var v in new[] { 50, -50 })
            ctx.DrawLine(grid, new Point(L, Y(v)), new Point(w - R, Y(v)));

        Label(ctx, "+100", new Point(L - 6, Y(100) - 5), axisInk);
        Label(ctx, "0", new Point(L - 6, Y(0) - 5), axisInk);
        Label(ctx, "-100", new Point(L - 6, Y(-100) - 5), axisInk);
        Label(ctx, "%", new Point(L - 6, T - 2), axisInk);

        if (Points is not { Count: > 1 } pts) return;

        // 面积：从零线往输出值填。加热暖色、制冷冷色，一眼分得出方向
        var warm = new SolidColorBrush(Color.Parse("#e02020"), 0.16);
        var cool = new SolidColorBrush(Color.Parse("#3f6fd8"), 0.16);
        for (var i = 1; i < pts.Count; i++)
        {
            var (t0, v0) = pts[i - 1];
            var (t1, v1) = pts[i];
            if (t1 < AxisFrom || t0 > AxisTo) continue;
            var mid = (v0 + v1) / 2;
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(new Point(X(t0), Y(0)), true);
                g.LineTo(new Point(X(t0), Y(v0)));
                g.LineTo(new Point(X(t1), Y(v1)));
                g.LineTo(new Point(X(t1), Y(0)));
                g.EndFigure(true);
            }
            ctx.DrawGeometry(mid >= 0 ? warm : cool, null, geo);
        }

        // 轮廓线；贴到满量程的那几段加粗描红
        var line = new Pen(new SolidColorBrush(Color.Parse("#b03a2e")), 1.2);
        var full = new Pen(new SolidColorBrush(Color.Parse("#c0392b")), 2.4);
        for (var i = 1; i < pts.Count; i++)
        {
            var (t0, v0) = pts[i - 1];
            var (t1, v1) = pts[i];
            if (t1 < AxisFrom || t0 > AxisTo) continue;
            var saturated = Math.Abs(v0) >= cap - 0.5 && Math.Abs(v1) >= cap - 0.5;
            ctx.DrawLine(saturated ? full : line, new Point(X(t0), Y(v0)), new Point(X(t1), Y(v1)));
        }
    }

    private static void Label(DrawingContext ctx, string text, Point at, IBrush ink)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                   new Typeface("Segoe UI, Microsoft YaHei UI, Microsoft YaHei"),
                                   9, ink);
        ctx.DrawText(ft, new Point(at.X - ft.Width, at.Y));
    }
}
