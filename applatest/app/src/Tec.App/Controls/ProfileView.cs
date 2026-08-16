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

    private const double W = 300, H = 132, L = 34, R = 8, T = 10, B = 20;

    static ProfileView()
    {
        AffectsRender<ProfileView>(SegmentsProperty, StartTempProperty);
        AffectsMeasure<ProfileView>(SegmentsProperty);
    }

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
        if (Bounds.Width < 20) return;
        var pts = Points();

        var scale = Bounds.Width / W;
        using var _ = ctx.PushTransform(Matrix.CreateScale(scale, scale));

        var bg = new SolidColorBrush(Color.Parse("#ffffff"));
        ctx.DrawRectangle(bg, null, new Rect(0, 0, W, H), 3, 3);

        var grid = new Pen(new SolidColorBrush(Color.Parse("#eef0f2")), 1);
        var axisText = new SolidColorBrush(Color.Parse("#8d8d8d"));

        // 只有一个点 = 还没有分段，画个空状态就收——不编一条假曲线出来
        if (pts.Count < 2)
        {
            Text(ctx, "还没有分段", new Point(W / 2, H / 2 - 6),
                 new SolidColorBrush(Color.Parse("#b0b0b0")), center: true);
            return;
        }

        var tMax = Math.Max(pts[^1].Min, 1);
        var lo = pts.Min(p => p.Temp);
        var hi = pts.Max(p => p.Temp);
        if (hi - lo < 1) { lo -= 1; hi += 1; }     // 全程恒温也得有个高度，不然线贴着边
        var pad = (hi - lo) * 0.08;
        lo -= pad; hi += pad;

        double X(double m) => L + m / tMax * (W - L - R);
        double Y(double c) => T + (1 - (c - lo) / (hi - lo)) * (H - T - B);

        // 横向三条参考线 + 左侧温度刻度
        foreach (var f in new[] { 0.0, 0.5, 1.0 })
        {
            var c = lo + (hi - lo) * f;
            ctx.DrawLine(grid, new Point(L, Y(c)), new Point(W - R, Y(c)));
            Text(ctx, c.ToString("F0", CultureInfo.InvariantCulture) + "℃",
                 new Point(L - 4, Y(c) - 5), axisText, right: true);
        }

        // 横轴：起点与总时长。中间刻度没有意义——分段边界本来就不等距
        Text(ctx, "0", new Point(L, H - 13), axisText, center: true);
        Text(ctx, Minutes(tMax), new Point(W - R, H - 13), axisText, right: true);

        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(X(pts[0].Min), Y(pts[0].Temp)), false);
            for (var i = 1; i < pts.Count; i++) g.LineTo(new Point(X(pts[i].Min), Y(pts[i].Temp)));
            g.EndFigure(false);
        }
        ctx.DrawGeometry(null, new Pen(new SolidColorBrush(Color.Parse("#ec5a24")), 1.8)
        { LineJoin = PenLineJoin.Round, LineCap = PenLineCap.Round }, geo);

        // 每一段的落点打一个圈，与表里的行一一对应
        var dot = new SolidColorBrush(Color.Parse("#ec5a24"));
        var white = new SolidColorBrush(Color.Parse("#ffffff"));
        for (var i = 1; i < pts.Count; i++)
            ctx.DrawEllipse(white, new Pen(dot, 1.6), new Point(X(pts[i].Min), Y(pts[i].Temp)), 2.6, 2.6);

        // 起点用灰点区分：它不是表里填的，是上一步结束时的温度。
        // 具体数值写在图下面那行小字里——标在图上会跟纵轴刻度撞一起
        ctx.DrawEllipse(new SolidColorBrush(Color.Parse("#9aa4ab")), null,
                        new Point(X(0), Y(pts[0].Temp)), 2.6, 2.6);
    }

    private static string Minutes(double m)
        => m >= 60
            ? $"{(m / 60).ToString("F1", CultureInfo.InvariantCulture)} h"
            : $"{m.ToString("F0", CultureInfo.InvariantCulture)} min";

    private static void Text(DrawingContext ctx, string s, Point at, IBrush brush,
                             bool center = false, bool right = false)
    {
        var ft = new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 9, brush);
        var x = center ? at.X - ft.Width / 2 : right ? at.X - ft.Width : at.X;
        ctx.DrawText(ft, new Point(x, at.Y));
    }
}
