using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Tec.App.Controls;

/// <summary>
/// 溶解度 – 温度曲线（原型 renderCmpProps 里那段 SVG 的 1:1）：
/// 0~90 ℃ 取 46 个点，绿线 1.8，25 ℃ 处一个红点加读数。
/// 视口固定 290×170，按控件宽度等比缩放（等价于 SVG viewBox + width:100%）。
/// </summary>
public sealed class SolubilityView : Control
{
    public static readonly StyledProperty<double[]?> CoefficientsProperty =
        AvaloniaProperty.Register<SolubilityView, double[]?>(nameof(Coefficients));

    private const double W = 290, H = 170, L = 40, R = 10, T = 12, B = 24;

    static SolubilityView()
    {
        AffectsRender<SolubilityView>(CoefficientsProperty);
        AffectsMeasure<SolubilityView>(CoefficientsProperty);
    }

    /// <summary>二次拟合系数 a + b·T + c·T²（原型 sol 数组）。</summary>
    public double[]? Coefficients
    {
        get => GetValue(CoefficientsProperty);
        set => SetValue(CoefficientsProperty, value);
    }

    protected override Size MeasureOverride(Size available)
    {
        var w = double.IsInfinity(available.Width) ? W : available.Width;
        return new Size(w, w * H / W);
    }

    private double SolAt(double t)
    {
        var c = Coefficients;
        if (c is null || c.Length < 3) return 0;
        return Math.Max(0, c[0] + c[1] * t + c[2] * t * t);
    }

    public override void Render(DrawingContext ctx)
    {
        if (Coefficients is null || Bounds.Width < 20) return;

        var scale = Bounds.Width / W;
        using var _ = ctx.PushTransform(Matrix.CreateScale(scale, scale));

        var smax = Math.Max(SolAt(90) * 1.15, 0.001);
        double X(double t) => L + t / 90 * (W - L - R);
        double Y(double s) => T + (1 - s / smax) * (H - T - B);

        var grid = new Pen(new SolidColorBrush(Color.Parse("#f0f0f0")), 1);
        var axis = new SolidColorBrush(Color.Parse("#8d8d8d"));

        foreach (var t in new[] { 0, 30, 60, 90 })
        {
            ctx.DrawLine(grid, new Point(X(t), T), new Point(X(t), H - B));
            Text(ctx, $"{t}℃", new Point(X(t), H - 15), axis, center: true);
        }
        foreach (var f in new[] { 0, 0.5, 1.0 })
        {
            ctx.DrawLine(grid, new Point(L, Y(smax * f)), new Point(W - R, Y(smax * f)));
            Text(ctx, (smax * f).ToString(smax < 1 ? "F2" : "F0", CultureInfo.InvariantCulture),
                 new Point(L - 4, Y(smax * f) - 4.5), axis, right: true);
        }

        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(X(0), Y(SolAt(0))), false);
            for (var i = 1; i < 46; i++) g.LineTo(new Point(X(i * 2), Y(SolAt(i * 2))));
            g.EndFigure(false);
        }
        ctx.DrawGeometry(null, new Pen(new SolidColorBrush(Color.Parse("#2f8f49")), 1.8)
        { LineJoin = PenLineJoin.Round }, geo);

        // 25 ℃ 是室温基准，结晶筛选先看这一点
        var red = new SolidColorBrush(Color.Parse("#e02020"));
        var at25 = SolAt(25);
        ctx.DrawEllipse(red, null, new Point(X(25), Y(at25)), 3, 3);
        Text(ctx, $"25℃：{at25.ToString("F2", CultureInfo.InvariantCulture)}",
             new Point(X(25) + 6, Y(at25) - 12), red);
    }

    private static void Text(DrawingContext ctx, string s, Point at, IBrush brush,
                             bool center = false, bool right = false)
    {
        var ft = new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 9, brush);
        var x = center ? at.X - ft.Width / 2 : right ? at.X - ft.Width : at.X;
        ctx.DrawText(ft, new Point(x, at.Y));
    }
}
