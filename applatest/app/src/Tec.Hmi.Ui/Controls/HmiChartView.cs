using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Tec.Hmi.Ui.Controls;

/// <summary>一条曲线：横轴是「秒」（相对图的时间原点），纵轴原值。</summary>
public sealed class HmiChartTrace
{
    public required string Name { get; init; }
    public required Color Color { get; init; }
    /// <summary>按时间升序。X = 距时间原点的秒数，Y = 原值。</summary>
    public required IReadOnlyList<Point> Points { get; init; }
}

/// <summary>图上一根红虚线（时间标记）。Zero = 当前的 t=0 标记（实心显示 ⌖）。</summary>
public sealed record HmiChartMark(double XSec, string Name, bool Zero);

/// <summary>
/// 整张图一次性算好再交给控件画。窗口、缩放、轴文字都是视图模型的事——
/// 控件只认「给什么画什么」，不碰任何数据源。
/// </summary>
public sealed class HmiChartModel
{
    public required IReadOnlyList<HmiChartTrace> Traces { get; init; }
    public required double XStart { get; init; }
    public required double XSpan { get; init; }
    public required double YLo { get; init; }
    public required double YHi { get; init; }
    /// <summary>纵轴数字的格式。纵轴按数据自动缩放后格距可能是 0.05 / 0.1，得带小数才念得出来。</summary>
    public string YFormat { get; init; } = "0";
    /// <summary>横轴 7 个刻度的文字（视图模型按「实验时间 / 时钟」算好）。</summary>
    public required IReadOnlyList<string> XLabels { get; init; }
    public IReadOnlyList<HmiChartMark> Marks { get; init; } = Array.Empty<HmiChartMark>();
    /// <summary>备注的红三角位置（秒）。</summary>
    public IReadOnlyList<double> Notes { get; init; } = Array.Empty<double>();
    /// <summary>右上角一句提示（纵轴「设定值附近」时被控量跑出纵轴了就说一声）；null = 不画。</summary>
    public string? Hint { get; init; }
}

/// <summary>
/// HMI 的曲线图，1:1 复刻原型 chart()：左 48 右 16 上 30 下 30 的留白、
/// 6 格横网格线 + 右对齐的纵轴数字、底部 7 个时间刻度、左上角图例色条、
/// 时间标记红虚线与备注红三角。全部自绘（Avalonia 没有现成的多轴折线，
/// 台面/运行页那两张图各有各的模型，硬套不如照原型再画一张干净的）。
/// </summary>
public sealed class HmiChartView : Control
{
    public static readonly StyledProperty<HmiChartModel?> ModelProperty =
        AvaloniaProperty.Register<HmiChartView, HmiChartModel?>(nameof(Model));

    public HmiChartModel? Model
    {
        get => GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    /// <summary>点图（原型 data-gchart）：回调参数是点中位置的「秒」。视图代码挂。</summary>
    public Action<double>? Tapped { get; set; }

    static HmiChartView() => AffectsRender<HmiChartView>(ModelProperty);

    public HmiChartView()
    {
        PointerPressed += OnPressed;
    }

    // 原型 chart() 的常量
    private const double L = 48, R = 16, T = 30, B = 30;

    private static readonly Typeface Face =
        new("Segoe UI, Microsoft YaHei UI, Microsoft YaHei");

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Tapped is null || Model is not { } m || m.XSpan <= 0) return;
        var p = e.GetPosition(this);
        var w = Bounds.Width;
        if (p.X < L || p.X > w - R) return;
        Tapped(m.XStart + (p.X - L) / (w - L - R) * m.XSpan);
        e.Handled = true;
    }

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w < 120 || h < 90 || Model is not { } m) return;

        // 全幅垫一层透明：只画线条的控件不铺底就点不中（命中测试只认画过的区域），
        // 点图开 gtap 弹窗全指着这一层
        ctx.FillRectangle(Brushes.Transparent, new Rect(0, 0, w, h));

        var plotW = w - L - R;
        var plotH = h - T - B;
        double X(double sec) => L + (m.XSpan > 0 ? (sec - m.XStart) / m.XSpan : 0) * plotW;
        double Y(double v) => h - B - (m.YHi > m.YLo ? (v - m.YLo) / (m.YHi - m.YLo) : 0) * plotH;

        var grid = new Pen(new SolidColorBrush(Color.Parse("#EEEEEE")), 1);
        var axis = new Pen(new SolidColorBrush(Color.Parse("#DCDCDC")), 1);
        var mut = new SolidColorBrush(Color.Parse("#A3A3A3"));
        var leg = new SolidColorBrush(Color.Parse("#6F6F6F"));
        var red = new SolidColorBrush(Color.Parse("#C42B1C"));

        // 横网格 + 纵轴数字（右对齐）
        for (var i = 0; i <= 6; i++)
        {
            var yy = T + i * plotH / 6;
            ctx.DrawLine(grid, new Point(L, yy), new Point(w - R, yy));
            var ft = Text(HmiChartAxis.Tick(m.YLo, m.YHi, i, m.YFormat), 10.5, mut);
            ctx.DrawText(ft, new Point(L - 8 - ft.Width, yy - ft.Height / 2));
        }

        // 底部时间刻度
        for (var i = 0; i < m.XLabels.Count && i <= 6; i++)
        {
            var xx = L + i * plotW / 6;
            var ft = Text(m.XLabels[i], 10.5, mut);
            ctx.DrawText(ft, new Point(xx - ft.Width / 2, h - B + 15 - ft.Height / 2));
        }

        // 曲线（超出绘图区裁掉——平移/缩放时线会越界）
        using (ctx.PushClip(new Rect(L, T - 4, plotW, plotH + 8)))
        {
            foreach (var tr in m.Traces)
            {
                if (tr.Points.Count < 2) continue;
                var g = new StreamGeometry();
                using (var gc = g.Open())
                {
                    gc.BeginFigure(new Point(X(tr.Points[0].X), Y(tr.Points[0].Y)), false);
                    for (var i = 1; i < tr.Points.Count; i++)
                        gc.LineTo(new Point(X(tr.Points[i].X), Y(tr.Points[i].Y)));
                    gc.EndFigure(false);
                }
                ctx.DrawGeometry(null, new Pen(new SolidColorBrush(tr.Color), 2)
                { LineJoin = PenLineJoin.Round, LineCap = PenLineCap.Round }, g);
            }
        }

        // 时间标记：红虚线 + 名字（⌖ = 当前 t=0）
        foreach (var mk in m.Marks)
        {
            if (mk.XSec < m.XStart || mk.XSec > m.XStart + m.XSpan) continue;
            var x = X(mk.XSec);
            var pen = new Pen(red, 1.2) { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) };
            using (ctx.PushOpacity(mk.Zero ? 1 : 0.5))
                ctx.DrawLine(pen, new Point(x, T), new Point(x, h - B));
            var ft = Text((mk.Zero ? "⌖ " : "") + mk.Name, 10, red);
            ctx.DrawText(ft, new Point(x + 4, T + 13 - ft.Height / 2));
        }

        // 备注红三角
        foreach (var sec in m.Notes)
        {
            if (sec < m.XStart || sec > m.XStart + m.XSpan) continue;
            var x = X(sec);
            var g = new StreamGeometry();
            using (var gc = g.Open())
            {
                gc.BeginFigure(new Point(x, h - B - 7), true);
                gc.LineTo(new Point(x - 5, h - B + 2));
                gc.LineTo(new Point(x + 5, h - B + 2));
                gc.EndFigure(true);
            }
            ctx.DrawGeometry(red, null, g);
        }

        // 基线
        ctx.DrawLine(axis, new Point(L, h - B), new Point(w - R, h - B));

        // 图例（左上，间距照原型 132）
        var lx = L + 8;
        foreach (var tr in m.Traces)
        {
            ctx.DrawRectangle(new SolidColorBrush(tr.Color), null,
                new RoundedRect(new Rect(lx, 12, 15, 3), 1.5));
            var ft = Text(tr.Name, 11, leg);
            ctx.DrawText(ft, new Point(lx + 21, 14 - ft.Height / 2));
            lx += Math.Max(132, 21 + ft.Width + 24);
        }

        // 右上角提示（跟图例一行，右对齐）
        if (!string.IsNullOrEmpty(m.Hint))
        {
            var ft = Text(m.Hint, 11, leg);
            ctx.DrawText(ft, new Point(Math.Max(lx, w - R - ft.Width), 14 - ft.Height / 2));
        }
    }

    private static FormattedText Text(string s, double size, IBrush brush)
        => new(s, System.Globalization.CultureInfo.InvariantCulture,
               FlowDirection.LeftToRight, Face, size, brush);
}

/// <summary>
/// 纵轴按画面里的数据自动缩放（2026-11-03 用户要的：稳住以后要看得出 0.1 ℃ 的起伏）。
/// 从前照原型定死「下界不高于 −30、上界 ×1.12+5」，15 ℃ 时一张图从 −30 画到 29，
/// 0.1 ℃ 不到一个像素。现在：6 格，格距取 1 / 2 / 2.5 / 5 × 10ⁿ、**只看数据跨度**——数据（上下各留
/// 半成余量）塞进 5 格就够，第 6 格留给对齐，下界落在格距整数倍上、数据大致居中，刻度数字是整的。
/// 数据跨度不到 <see cref="MinSpan"/> 按它算（一般成了 0.1 一格、整张 0.6 ℃），免得一条 ±0.01 的平线放大成满屏噪声。
/// 每秒重画时把上一张的界带进来（<c>prev</c>）：数据还在界里、格距没比现在该用的粗过一档、跨度离换档不到一成半，
/// 就照旧不动——数据小动、窗口里进出一个点，纵轴不跟着跳。
/// </summary>
public static class HmiChartAxis
{
    /// <summary>数据跨度下限（℃）。</summary>
    public const double MinSpan = 0.4;

    /// <summary>格数，跟控件画的横网格一致。</summary>
    public const int Cells = 6;

    /// <summary>
    /// 给数据最小 / 最大值，回纵轴上下界和刻度数字格式。<paramref name="prev"/> 是上一拍这张图用的界
    /// （用户缩放 / 平移 / 开关曲线后传 null，重新取最合适的）。数不是有限值（一条线全是 NaN）、
    /// 上下颠倒、或大到算不出格子时回 0…6——图上反正没东西可画。
    /// </summary>
    public static (double Lo, double Hi, string Format) Fit(double dataLo, double dataHi,
        (double Lo, double Hi, string Format)? prev = null)
    {
        var none = (0.0, (double)Cells, "0");
        if (!double.IsFinite(dataLo) || !double.IsFinite(dataHi) || dataHi < dataLo) return none;
        var span = Math.Max(dataHi - dataLo, MinSpan);
        var mid = dataLo / 2 + dataHi / 2;
        if (!double.IsFinite(span) || !double.IsFinite(mid)) return none;
        var need = span * 1.1 / (Cells - 1);          // 5 格装下数据 + 上下各半成
        var step = Nice(need);

        if (prev is { } p && p.Hi > p.Lo)
        {
            var ps = (p.Hi - p.Lo) / Cells;
            // 上一张的格距还够用、也没粗过「跨度再大一成半时该用的那档」——不换档。
            // 「再大一成半」拿真跨度算再套下限：拿套过下限的算，平在 0.4 以下的线永远落在 0.2 那档的宽限里，
            // 收窄到底也回不到 0.1 一格（评审复查翻出来的：四场保温全程卡在 0.2）
            var coarsest = Nice(Math.Max((dataHi - dataLo) * 1.15, MinSpan) * 1.1 / (Cells - 1));
            if (ps >= need * (1 - 1e-9) && ps <= coarsest * (1 + 1e-9))
            {
                // 数据离上下边都还有一成格：界也不动
                if (dataLo >= p.Lo + ps * 0.1 && dataHi <= p.Hi - ps * 0.1) return p;
                step = ps;
            }
        }

        var lo = Math.Floor(mid / step - Cells / 2.0 + 0.5) * step;
        var hi = lo + Cells * step;
        if (!double.IsFinite(lo) || !(hi > lo)) return none;
        return (lo, hi, FormatOf(step));
    }

    /// <summary>「设定值附近」那一档的半宽（℃）：设定值 ±0.3、一格 0.1。</summary>
    public const double NearHalfSpan = 0.3;

    /// <summary>
    /// 纵轴「设定值附近」（2026-11-03 用户要的：全程那张图降温段一摆进来又是 −40…20，稳住以后的 0.1 看不见）：
    /// 以 <paramref name="center"/>（设定值）为中、上下各 <see cref="NearHalfSpan"/>、一格 0.1，不跟数据缩放——
    /// 降温段那一大截从上边进来，到了设定值附近每 0.1 一格。中心取到最近的 0.1，刻度都是整 0.1
    /// （设定值 15.05 时中线是 15.1）。不是有限数回 null（调用方退回按数据缩放）。
    /// </summary>
    public static (double Lo, double Hi, string Format)? Near(double center)
    {
        if (!double.IsFinite(center) || Math.Abs(center) > 1e6) return null;
        var mid = Math.Round(center * 10, MidpointRounding.AwayFromZero) / 10;
        return (mid - NearHalfSpan, mid + NearHalfSpan, "0.0");
    }

    /// <summary>从上往下第 i 根横线（0…6）的数字。</summary>
    public static string Tick(double lo, double hi, int i, string format)
    {
        var val = hi - (hi - lo) * i / Cells;
        // 跨零那一格的浮点零头（−1e-17）别印成「−0.0」
        if (Math.Abs(val) < (hi - lo) * 1e-9) val = 0;
        return val.ToString(format).Replace('-', '−');
    }

    /// <summary>不小于 x 的 1 / 2 / 2.5 / 5 × 10ⁿ（x 是正的有限数）。</summary>
    private static double Nice(double x)
    {
        var p = Math.Pow(10, Math.Floor(Math.Log10(x)));
        var f = x / p;
        return (f <= 1 + 1e-9 ? 1 : f <= 2 + 1e-9 ? 2 : f <= 2.5 + 1e-9 ? 2.5 : f <= 5 + 1e-9 ? 5 : 10) * p;
    }

    /// <summary>格距要几位小数才写得全：25 → "0"、2.5 / 0.5 → "0.0"、0.25 → "0.00"。</summary>
    private static string FormatOf(double step)
    {
        for (var d = 0; d < 6; d++)
        {
            var x = step * Math.Pow(10, d);
            if (Math.Abs(x - Math.Round(x)) < 1e-6 * Math.Max(1, x))
                return d == 0 ? "0" : "0." + new string('0', d);
        }
        return "0.000000";
    }
}
