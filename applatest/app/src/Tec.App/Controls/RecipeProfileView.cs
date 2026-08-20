using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Tec.App.ViewModels;
using Tec.Core;
using Tec.Core.Scheduling;

namespace Tec.App.Controls;

/// <summary>
/// 配方库那条**温度剖面**（照 gbg-recipes 原型的 .rx-prof）。
///
/// 一张步骤表读不出一条配方长什么样：哪一段在升温、保温占了多大比重、
/// 整条要跑多久，全得拿参数在脑子里做除法。画成按时长比例的一条带子就一眼看见，
/// 而且色带可以点——点哪一段就选中哪一步。
///
/// **画的是计划不是实况**：每一段的起止时刻来自排期，两端温度来自估算器
/// 一路推进的釜温（见 <see cref="TempProfile"/>）。运行页那张趋势图画的才是
/// 真实采样，两张图不共用一个控件，免得看串。
///
/// 与 <see cref="ProfileView"/> 也不是一回事：那个画的是「梯度控温」这**一步**
/// 内部的分段曲线，这个画的是**整条配方**。
/// </summary>
public sealed class RecipeProfileView : Control
{
    public static readonly StyledProperty<TempProfile?> ProfileProperty =
        AvaloniaProperty.Register<RecipeProfileView, TempProfile?>(nameof(Profile));

    /// <summary>当前选中的步骤序号（Recipe.Steps 的下标）。−1 = 没选。</summary>
    public static readonly StyledProperty<int> SelectedIndexProperty =
        AvaloniaProperty.Register<RecipeProfileView, int>(nameof(SelectedIndex), -1);

    /// <summary>点了某一段色带。参数是步骤序号。</summary>
    public event EventHandler<int>? StepPicked;

    private const double Gutter = 34;    // y 刻度那一列
    private const double PlotH = 84;
    private const double AxisH = 15;
    private const double LaneH = 18;
    private const double BarH = 13;

    private int _hover = -1;

    static RecipeProfileView()
    {
        AffectsRender<RecipeProfileView>(ProfileProperty, SelectedIndexProperty);
        AffectsMeasure<RecipeProfileView>(ProfileProperty);
    }

    public RecipeProfileView()
    {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public TempProfile? Profile
    {
        get => GetValue(ProfileProperty);
        set => SetValue(ProfileProperty, value);
    }

    public int SelectedIndex
    {
        get => GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    private int Lanes => Profile?.States.Count ?? 0;

    protected override Size MeasureOverride(Size available)
    {
        var w = double.IsInfinity(available.Width) ? 600 : available.Width;
        var h = PlotH + AxisH + (Lanes > 0 ? 2 + Lanes * LaneH : 0);
        return new Size(w, h);
    }

    // ── 指针 ─────────────────────────────────────────────────────────

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var i = HitOf(e.GetPosition(this));
        if (i == _hover) return;
        _hover = i;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hover < 0) return;
        _hover = -1;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var i = HitOf(e.GetPosition(this));
        if (i >= 0) StepPicked?.Invoke(this, i);
    }

    /// <summary>指针落在哪一段上。不在图里（刻度栏、泳道）返回 −1。</summary>
    private int HitOf(Point p)
    {
        if (Profile is not { IsEmpty: false } prof) return -1;
        if (p.Y < 0 || p.Y > PlotH || p.X < Gutter) return -1;
        var w = Bounds.Width - Gutter;
        if (w <= 0) return -1;
        var total = prof.Total.TotalMinutes;
        if (total <= 0) return -1;
        var at = (p.X - Gutter) / w * total;
        foreach (var g in prof.Segments)
            if (at >= g.Start.TotalMinutes && at < g.End.TotalMinutes) return g.Index;
        return -1;
    }

    // ── 画 ───────────────────────────────────────────────────────────

    public override void Render(DrawingContext ctx)
    {
        if (Profile is not { IsEmpty: false } prof) return;
        var w = Bounds.Width - Gutter;
        if (w < 40) return;

        var total = prof.Total.TotalMinutes;
        if (total <= 0) return;

        // 值域：跨度太小就撑开一档，不然一条平线贴着上下边
        var lo = prof.MinC;
        var hi = prof.MaxC;
        if (hi - lo < 20) { var m = (hi + lo) / 2; lo = m - 10; hi = m + 10; }
        var pad = (hi - lo) * 0.18;
        lo -= pad;
        hi += pad;

        double X(double min) => Gutter + min / total * w;
        double Y(double c) => PlotH - (c - lo) / (hi - lo) * PlotH;

        var plot = new Rect(Gutter, 0, w, PlotH);
        ctx.FillRectangle(Brushes.White, plot);

        // ── 格线 + y 刻度 ─────────────────────────────────────────────
        var grid = new Pen(Brush("#f4f4f4"), 1);
        var tickTx = Brush("#a2a2a2");
        var step = TempProfile.TickStep(hi - lo);
        for (var c = Math.Ceiling(lo / step) * step; c <= hi + 1e-6; c += step)
        {
            var y = Y(c);
            ctx.DrawLine(grid, new Point(Gutter, y), new Point(Gutter + w, y));
            Text(ctx, c.ToString("0.#", CultureInfo.InvariantCulture), new Point(Gutter - 6, y - 6),
                 tickTx, 9.5, right: true);
        }

        // ── 色带：一步一段，点它选中那一步 ────────────────────────────
        // 选中 / 悬停都是蓝的（跟全局那一套一致），靠深浅与两条边线分开：
        // 悬停只上一层很淡的底，选中除了底色还画左右两条实线
        var sep = new Pen(Brush("#f0f0f0"), 1);
        var hovFill = Brush("#1a91d6", 0.08);
        var selFill = Brush("#1a7fc4", 0.16);
        var selEdge = new Pen(Brush("#1a7fc4"), 1);
        var num = Brush("#c2c2c2");
        var numOn = Brush("#1a7fc4");

        foreach (var g in prof.Segments)
        {
            var x0 = X(g.Start.TotalMinutes);
            var x1 = X(g.End.TotalMinutes);
            var bw = x1 - x0;
            if (bw <= 0) continue;
            var on = g.Index == SelectedIndex;

            if (on) ctx.FillRectangle(selFill, new Rect(x0, 0, bw, PlotH));
            else if (g.Index == _hover) ctx.FillRectangle(hovFill, new Rect(x0, 0, bw, PlotH));
            if (on)
            {
                ctx.DrawLine(selEdge, new Point(x0 + 0.5, 0), new Point(x0 + 0.5, PlotH));
                ctx.DrawLine(selEdge, new Point(x1 - 0.5, 0), new Point(x1 - 0.5, PlotH));
            }
            else if (x1 < Gutter + w - 0.5)
            {
                ctx.DrawLine(sep, new Point(x1, 0), new Point(x1, PlotH));
            }

            // 太窄的段不写号：挤在一起谁也看不清，选中的那个例外
            if (bw >= w * 0.03 || on)
                Text(ctx, (g.Index + 1).ToString(), new Point(x0 + bw / 2, 2),
                     on ? numOn : num, 9, center: true);
        }

        // ── 曲线 ─────────────────────────────────────────────────────
        var pen = new Pen(Brush("#f7941e"), 1.6)
        { LineJoin = PenLineJoin.Round, LineCap = PenLineCap.Round };
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            var first = true;
            foreach (var s in prof.Segments)
            {
                var p0 = new Point(X(s.Start.TotalMinutes), Y(s.FromC));
                if (first) { g.BeginFigure(p0, false); first = false; }
                else g.LineTo(p0);
                g.LineTo(new Point(X(s.End.TotalMinutes), Y(s.ToC)));
            }
            if (!first) g.EndFigure(false);
        }
        using (ctx.PushClip(plot)) ctx.DrawGeometry(null, pen, geo);

        ctx.DrawRectangle(null, new Pen(Brush("#dcdcdc"), 1), plot);

        // ── x 刻度 ───────────────────────────────────────────────────
        var iv = TempProfile.TimeStepMinutes(prof.Total);
        for (var m = 0.0; m <= total; m += iv)
        {
            // 太靠右就让位给末尾那个总时长，两个数叠在一起谁也读不出来
            if (m > 0 && (total - m) / total < 0.10) continue;
            var lab = m % 60 == 0 ? (m / 60).ToString("0.#") + " h" : m + " min";
            if (m == 0) lab = "0";
            Text(ctx, lab, new Point(X(m), PlotH + 2), tickTx, 9.5, center: m > 0);
        }
        Text(ctx, Fmt.Hms(prof.Total), new Point(Gutter + w, PlotH + 2), Brush("#8d8d8d"), 9.5,
             right: true);

        // ── 持续状态泳道 ─────────────────────────────────────────────
        var y0 = PlotH + AxisH + 2;
        for (var i = 0; i < prof.States.Count; i++)
        {
            var st = prof.States[i];
            var x0 = X(st.Start.TotalMinutes);
            var x1 = X(Math.Min(st.End.TotalMinutes, total));
            var bw = Math.Max(x1 - x0, 2);
            var top = y0 + i * LaneH + (LaneH - BarH) / 2;
            var rect = new Rect(x0, top, bw, BarH);
            ctx.DrawRectangle(Brush(ModuleInfo.ColorOf(st.Module)), null, new RoundedRect(rect, 2));
            using (ctx.PushClip(rect.Deflate(new Thickness(5, 0))))
                Text(ctx, $"{st.Name} · {st.Value}", new Point(x0 + 6, top + 1.5), Brushes.White, 9.5);
        }
    }

    private static IBrush Brush(string hex, double opacity = 1)
        => new SolidColorBrush(Color.Parse(hex), opacity);

    private static void Text(DrawingContext ctx, string s, Point at, IBrush brush, double size,
                             bool center = false, bool right = false)
    {
        var ft = new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI, Microsoft YaHei UI, Microsoft YaHei"), size, brush);
        var x = center ? at.X - ft.Width / 2 : right ? at.X - ft.Width : at.X;
        ctx.DrawText(ft, new Point(x, at.Y));
    }
}
