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
    /// <summary>横轴 7 个刻度的文字（视图模型按「实验时间 / 时钟」算好）。</summary>
    public required IReadOnlyList<string> XLabels { get; init; }
    public IReadOnlyList<HmiChartMark> Marks { get; init; } = Array.Empty<HmiChartMark>();
    /// <summary>备注的红三角位置（秒）。</summary>
    public IReadOnlyList<double> Notes { get; init; } = Array.Empty<double>();
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
            var val = m.YHi - (m.YHi - m.YLo) * i / 6;
            var ft = Text(val.ToString("0").Replace('-', '−'), 10.5, mut);
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
    }

    private static FormattedText Text(string s, double size, IBrush brush)
        => new(s, System.Globalization.CultureInfo.InvariantCulture,
               FlowDirection.LeftToRight, Face, size, brush);
}
