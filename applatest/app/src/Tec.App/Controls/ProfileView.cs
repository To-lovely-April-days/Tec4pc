using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Tec.App.Controls;

/// <summary>一段温度曲线：以 Rate 走到 Target，再保持 Hold 分钟。</summary>
public readonly record struct ProfileSeg(double Target, double Rate, double Hold);

/// <summary>
/// 温度分段的曲线预览。
///
/// 一张三列几十行的表读不出形状——降到 5 ℃ 到底用了半小时还是五小时，
/// 要拿目标温度和速率在脑子里做除法才知道。画出来就一眼看见：
/// 斜线是变温、平线是保持，横轴长度直接就是耗时。
///
/// **画法与运行页的趋势图是一套**：同样的边距比例、同样的格线色（横 #ececec /
/// 纵 #f2f2f2）、同样 10px 的 #8d8d8d 刻度字、左上角标单位。配方里画的是"打算怎么走"，
/// 运行时画的是"实际怎么走的"，两张图长得像，才对得上。
///
/// 曲线与表格是同一份数据，改哪边另一边立刻跟着变——它不是"示意图"，
/// 是这条配方真正会走的形状（与排期用的是同一套估算：时间 = |ΔT| / 速率）。
/// </summary>
public sealed class ProfileView : Control
{
    public static readonly StyledProperty<IReadOnlyList<ProfileSeg>?> SegmentsProperty =
        AvaloniaProperty.Register<ProfileView, IReadOnlyList<ProfileSeg>?>(nameof(Segments));

    /// <summary>起笔温度：这一步开始时釜里是多少度。第一段的长度全靠它。</summary>
    public static readonly StyledProperty<double> StartTempProperty =
        AvaloniaProperty.Register<ProfileView, double>(nameof(StartTemp), 25);

    /// <summary>设计尺寸。按控件宽度等比缩放，等价于 SVG 的 viewBox。</summary>
    private const double W = 306, H = 152;
    /// <summary>边距。与 TrendView 同一套比例：左让出温度刻度，下让出时间刻度。</summary>
    private const double L = 38, R = 12, T = 18, B = 24;

    private const string Line = "#ec5a24";      // 温度模块色，与步骤卡图标同一个橙
    private const string GridH = "#ececec";
    private const string GridV = "#f2f2f2";
    private const string AxisTx = "#8d8d8d";

    static ProfileView()
    {
        AffectsRender<ProfileView>(SegmentsProperty, StartTempProperty);
        AffectsMeasure<ProfileView>(SegmentsProperty);
    }

    public ProfileView() => ClipToBounds = true;

    public IReadOnlyList<ProfileSeg>? Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public double StartTemp
    {
        get => GetValue(StartTempProperty);
        set => SetValue(StartTempProperty, value);
    }

    protected override Size MeasureOverride(Size available)
    {
        var w = double.IsInfinity(available.Width) ? W : available.Width;
        return new Size(w, w * H / W);
    }

    /// <summary>把分段展开成折线的拐点：(分钟, 温度)。与排期同一套算法。</summary>
    private List<(double Min, double Temp)> Points()
    {
        var pts = new List<(double, double)> { (0, StartTemp) };
        var cur = StartTemp;
        var t = 0d;
        foreach (var s in Segments ?? Array.Empty<ProfileSeg>())
        {
            // 速率填 0 会让时间变成无穷；按排期里的同一个下限兜住
            t += Math.Abs(s.Target - cur) / Math.Max(s.Rate, 0.01);
            cur = s.Target;
            pts.Add((t, cur));
            if (s.Hold > 0)
            {
                t += s.Hold;
                pts.Add((t, cur));
            }
        }
        return pts;
    }

    public override void Render(DrawingContext ctx)
    {
        if (Bounds.Width < 40) return;

        var scale = Bounds.Width / W;
        using var _ = ctx.PushTransform(Matrix.CreateScale(scale, scale));

        var axis = new SolidColorBrush(Color.Parse(AxisTx));
        var pts = Points();

        // 只有一个点 = 还没有分段。画个空状态就收，不编一条假曲线出来
        if (pts.Count < 2)
        {
            Text(ctx, "还没有分段，下面加一行就画出来", new Point(W / 2, H / 2 - 6),
                 new SolidColorBrush(Color.Parse("#b6b6b6")), center: true);
            return;
        }

        // ── 坐标 ──────────────────────────────────────────────────────
        var tMax = Math.Max(pts[^1].Min, 1);
        var lo = pts.Min(p => p.Temp);
        var hi = pts.Max(p => p.Temp);
        if (hi - lo < 2) { lo -= 2; hi += 2; }      // 全程恒温也得有高度，不然线贴着边

        // 值域按数据留一成余量，**不**吸附到整刻度——吸附会把 5~60 ℃ 撑成 −20~80，
        // 曲线缩到中间一小条。整刻度只用来决定格线画在哪：
        // 轴上写的仍是 0 / 20 / 40 这种整数，但纵向空间不浪费
        var padY = (hi - lo) * 0.10;
        var yLo = lo - padY;
        var yHi = hi + padY;
        var yStep = NiceStep(yHi - yLo, 4, new double[] { 1, 2, 5, 10, 20, 25, 50, 100, 200 });

        var hours = tMax > 90;                      // 超过一个半小时就用小时做单位
        var tUnit = hours ? 60.0 : 1.0;
        var xStep = NiceStep(tMax / tUnit, 5,
            hours ? new double[] { 0.5, 1, 2, 3, 4, 6, 12, 24 }
                  : new double[] { 1, 2, 5, 10, 15, 30, 60 }) * tUnit;

        double X(double m) => L + m / tMax * (W - L - R);
        double Y(double c) => T + (1 - (c - yLo) / (yHi - yLo)) * (H - T - B);

        // ── 格线与刻度 ────────────────────────────────────────────────
        var gh = new Pen(new SolidColorBrush(Color.Parse(GridH)), 1);
        var gv = new Pen(new SolidColorBrush(Color.Parse(GridV)), 1);

        // 格线只画落在值域内的整刻度
        for (var c = Math.Ceiling(yLo / yStep) * yStep; c <= yHi + 1e-6; c += yStep)
        {
            ctx.DrawLine(gh, new Point(L, Y(c)), new Point(W - R, Y(c)));
            Text(ctx, c.ToString("0.#", CultureInfo.InvariantCulture),
                 new Point(L - 6, Y(c) - 5.5), axis, right: true);
        }

        for (var m = 0.0; m <= tMax + 1e-6; m += xStep)
        {
            var x = X(m);
            if (m > 0) ctx.DrawLine(gv, new Point(x, T), new Point(x, H - B));
            Text(ctx, (m / tUnit).ToString("0.#", CultureInfo.InvariantCulture),
                 new Point(x, H - 16), axis, center: true);
        }

        // 单位标在角上，与趋势图一样：轴上只留数字，读起来才干净
        Text(ctx, "℃", new Point(2, T - 12), axis);
        Text(ctx, hours ? "h" : "min", new Point(W - R, H - 16), axis);

        // ── 曲线 ──────────────────────────────────────────────────────
        var pen = new Pen(new SolidColorBrush(Color.Parse(Line)), 1.8)
        { LineJoin = PenLineJoin.Round, LineCap = PenLineCap.Round };

        using (ctx.PushClip(new Rect(L, T, W - L - R, H - T - B)))
        {
            // 曲线下方一层极淡的同色底：看得出"温度走过的区域"，又不抢线
            var area = new StreamGeometry();
            using (var g = area.Open())
            {
                g.BeginFigure(new Point(X(0), H - B), true);
                foreach (var p in pts) g.LineTo(new Point(X(p.Min), Y(p.Temp)));
                g.LineTo(new Point(X(pts[^1].Min), H - B));
                g.EndFigure(true);
            }
            ctx.DrawGeometry(new SolidColorBrush(Color.Parse(Line), 0.07), null, area);

            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(new Point(X(pts[0].Min), Y(pts[0].Temp)), false);
                for (var i = 1; i < pts.Count; i++) g.LineTo(new Point(X(pts[i].Min), Y(pts[i].Temp)));
                g.EndFigure(false);
            }
            ctx.DrawGeometry(null, pen, geo);

            // 每一段的落点一个实心小点，与表里的行一一对应
            var dot = new SolidColorBrush(Color.Parse(Line));
            for (var i = 1; i < pts.Count; i++)
                ctx.DrawEllipse(dot, null, new Point(X(pts[i].Min), Y(pts[i].Temp)), 2.4, 2.4);

            // 起点是灰的：它不是表里填的，是上一步结束时的温度
            ctx.DrawEllipse(new SolidColorBrush(Color.Parse("#9aa4ab")), null,
                            new Point(X(0), Y(pts[0].Temp)), 2.4, 2.4);
        }
    }

    /// <summary>从给定档位里挑一个刻度，使刻度数不超过 n。都不够就往上翻倍。</summary>
    private static double NiceStep(double span, int n, double[] steps)
    {
        foreach (var s in steps) if (span / s <= n) return s;
        var last = steps[^1];
        while (span / last > n) last *= 2;
        return last;
    }

    private static void Text(DrawingContext ctx, string s, Point at, IBrush brush,
                             bool center = false, bool right = false)
    {
        var ft = new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 10, brush);
        var x = center ? at.X - ft.Width / 2 : right ? at.X - ft.Width : at.X;
        ctx.DrawText(ft, new Point(x, at.Y));
    }
}
