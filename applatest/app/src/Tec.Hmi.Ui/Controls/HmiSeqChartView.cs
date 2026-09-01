using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Tec.Hmi.Ui.Controls;

/// <summary>一行属性。EditKey 非空 = 可点（tgt / mode / val / rpm），画成值框。</summary>
public sealed record HmiSeqRow(string K, string V, string EditKey = "");

/// <summary>任务序列预览里的一列步骤卡。State: idle / pend / act / done / err。</summary>
public sealed class HmiSeqCard
{
    public required int No { get; init; }
    public required string Type { get; init; }
    public required string State { get; init; }
    public required string StateText { get; init; }
    public required IReadOnlyList<HmiSeqRow> Rows { get; init; }
    /// <summary>这一步开始 / 结束的估算温度（排期 StartTemp / EndTemp——排期是唯一来源）。</summary>
    public required double T0 { get; init; }
    public required double T1 { get; init; }
    /// <summary>进行中步骤走到几成（0..1），画卡内红虚线。</summary>
    public double Frac { get; init; }
}

public sealed class HmiSeqModel
{
    public required IReadOnlyList<HmiSeqCard> Cards { get; init; }
    /// <summary>false = 序列已启动，值框不画、表头不带 ▾（原型 lock 态）。</summary>
    public bool Editable { get; init; } = true;
}

/// <summary>
/// 任务序列预览（原型 seqChart 的 1:1）：每步一列卡片——表头（步骤号 + 指令名 +
/// 状态角标）、中段温度轨迹带（跨卡折线 + 端点温度）、下段属性行。
/// 原型里那张是一整块 SVG，这里同样整块自绘。
/// </summary>
public sealed class HmiSeqChartView : Control
{
    public static readonly StyledProperty<HmiSeqModel?> ModelProperty =
        AvaloniaProperty.Register<HmiSeqChartView, HmiSeqModel?>(nameof(Model));

    public HmiSeqModel? Model
    {
        get => GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    static HmiSeqChartView() => AffectsRender<HmiSeqChartView>(ModelProperty);

    /// <summary>点了第几张卡的哪一格："head" 或该行的 EditKey。视图代码挂。</summary>
    public Action<int, string>? Tapped { get; set; }

    public HmiSeqChartView()
    {
        PointerPressed += (_, e) =>
        {
            if (Tapped is null || Model is not { Cards.Count: > 0 } m) return;
            var p = e.GetPosition(this);
            var n = m.Cards.Count;
            var cw = (Bounds.Width - 4 - G * (n - 1)) / n;
            for (var i = 0; i < n; i++)
            {
                var x0 = 2 + i * (cw + G);
                if (p.X < x0 || p.X > x0 + cw) continue;
                if (p.Y < 40) { Tapped(i, "head"); e.Handled = true; return; }
                var r = (int)Math.Round((p.Y - RowY0 + 12) / RowH);
                if (r >= 0 && r < m.Cards[i].Rows.Count && m.Cards[i].Rows[r].EditKey.Length > 0)
                { Tapped(i, m.Cards[i].Rows[r].EditKey); e.Handled = true; }
                return;
            }
        };
    }

    private static readonly Typeface Face = new("Segoe UI, Microsoft YaHei UI, Microsoft YaHei");
    private static readonly Typeface FaceBold =
        new("Segoe UI, Microsoft YaHei UI, Microsoft YaHei", weight: FontWeight.Bold);

    private const double G = 8, BT = 52, BB = 168, RowY0 = 198, RowH = 26;

    private static FormattedText T(string s, double size, IBrush b, bool bold = false)
        => new(s, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
               bold ? FaceBold : Face, size, b);

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w < 200 || h < 200 || Model is not { Cards.Count: > 0 } m) return;

        var ink = new SolidColorBrush(Color.Parse("#1A1A1A"));
        var mut = new SolidColorBrush(Color.Parse("#6F6F6F"));
        var key = new SolidColorBrush(Color.Parse("#8E8E8E"));
        var red = new SolidColorBrush(Color.Parse("#C42B1C"));
        var blueD = new SolidColorBrush(Color.Parse("#2F8189"));
        var green = new SolidColorBrush(Color.Parse("#2F6B38"));
        var s1 = new SolidColorBrush(Color.Parse("#2F8189"));

        var n = m.Cards.Count;
        var cw = (w - 4 - G * (n - 1)) / n;
        double CX(int i) => 2 + i * (cw + G);
        var ch2 = h - 4;

        // 温度纵标（原型：min−8 … max+8，跨度最少 24）
        double lo = double.MaxValue, hi = double.MinValue;
        foreach (var c in m.Cards)
        {
            lo = Math.Min(lo, Math.Min(c.T0, c.T1));
            hi = Math.Max(hi, Math.Max(c.T0, c.T1));
        }
        lo -= 8; hi += 8;
        if (hi - lo < 24) { var c0 = (hi + lo) / 2; lo = c0 - 12; hi = c0 + 12; }
        double Y(double v) => BB - (v - lo) / (hi - lo) * (BB - BT);

        for (var i = 0; i < n; i++)
        {
            var c = m.Cards[i];
            var x = CX(i);
            var (fill, bd, bw) = c.State switch
            {
                "act" => ("#E4F4F4", "#4FB1B8", 1.6),
                "err" => ("#FBEAE7", "#C42B1C", 1.6),
                "done" => ("#F1F1F1", "#DCDCDC", 1.0),
                _ => (i % 2 == 1 ? "#F6F6F6" : "#FBFBFB", "#E3E3E3", 1.0),
            };
            ctx.DrawRectangle(new SolidColorBrush(Color.Parse(fill)),
                new Pen(new SolidColorBrush(Color.Parse(bd)), bw),
                new RoundedRect(new Rect(x, 2, cw, ch2 - 2), 3));

            using var _ = ctx.PushClip(new Rect(x + 1, 2, cw - 2, ch2 - 2));

            ctx.DrawText(T($"步骤 {c.No}", 11, mut), new Point(x + 14, 26 - 13));
            var tb = c.State switch { "act" => (IBrush)blueD, "err" => red, _ => ink };
            var tyText = T(c.Type, 15, tb, bold: true);
            ctx.DrawText(tyText, new Point(x + 58, 27 - 17));
            // 可编辑时表头带 ▾（原型 lock 之外的卡）：点表头换类型
            if (m.Editable)
                ctx.DrawText(T("▾", 9.5, key), new Point(x + 62 + tyText.Width, 27 - 12));
            if (c.StateText.Length > 0)
            {
                var stB = c.State switch
                { "act" => (IBrush)blueD, "done" => green, "err" => red, _ => new SolidColorBrush(Color.Parse("#B0B0B0")) };
                var ft = T(c.StateText, 10.5, stB);
                ctx.DrawText(ft, new Point(x + cw - 14 - ft.Width, 26 - 12));
            }

            var sep = new Pen(new SolidColorBrush(Color.Parse(c.State == "err" ? "#F0CBC4" : "#E3E3E3")), 1);
            ctx.DrawLine(sep, new Point(x + 1, 40), new Point(x + cw - 1, 40));
            ctx.DrawLine(new Pen(new SolidColorBrush(Color.Parse(c.State == "err" ? "#F0CBC4" : "#ECECEC")), 1),
                         new Point(x + 1, 180), new Point(x + cw - 1, 180));

            for (var r = 0; r < c.Rows.Count; r++)
            {
                var yy = RowY0 + r * RowH;
                if (yy > ch2 - 8) break;
                ctx.DrawText(T(c.Rows[r].K, 10.5, key), new Point(x + 14, yy - 12));
                // 可点的值画成值框（原型 data-fedit 的 vbox），到达方式再加一枚下拉角
                if (m.Editable && c.Rows[r].EditKey.Length > 0)
                {
                    var bx = x + 62; var bwd = Math.Max(20, cw - 76);
                    ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#DCDCDC")),
                        new Pen(new SolidColorBrush(Color.Parse("#B4B4B4")), 1),
                        new RoundedRect(new Rect(bx, yy - 16, bwd, 22), 3));
                    if (c.Rows[r].EditKey == "mode")
                    {
                        var cxp = bx + bwd - 14;
                        var g2 = new StreamGeometry();
                        using (var gc = g2.Open())
                        {
                            gc.BeginFigure(new Point(cxp - 4.5, yy - 8), false);
                            gc.LineTo(new Point(cxp, yy - 3));
                            gc.LineTo(new Point(cxp + 4.5, yy - 8));
                            gc.EndFigure(false);
                        }
                        ctx.DrawGeometry(null, new Pen(mut, 1.6)
                        { LineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, g2);
                    }
                }
                ctx.DrawText(T(c.Rows[r].V, 12.5, ink), new Point(x + 71, yy - 14));
            }
        }

        // 跨卡温度折线 + 端点标注
        var geo = new StreamGeometry();
        using (var gc = geo.Open())
        {
            gc.BeginFigure(new Point(CX(0), Y(m.Cards[0].T0)), false);
            for (var i = 0; i < n; i++)
            {
                var c = m.Cards[i];
                gc.LineTo(new Point(CX(i), Y(c.T0)));
                gc.LineTo(new Point(CX(i) + cw, Y(c.T1)));
            }
            gc.EndFigure(false);
        }
        ctx.DrawGeometry(null, new Pen(s1, 2.4) { LineJoin = PenLineJoin.Round }, geo);

        void Node(double x, double v, bool leftLabel)
        {
            var y = Y(v);
            ctx.DrawEllipse(s1, null, new Point(x, y), 3, 3);
            var lbl = T(((v < 0 ? "−" : "") + Math.Abs(v).ToString("0.0")) + " ℃", 10.5,
                        new SolidColorBrush(Color.Parse("#3A3A3A")));
            var ly = y > 110 ? y - 9 - lbl.Height : y + 16 - lbl.Height / 2;
            ctx.DrawText(lbl, new Point(leftLabel ? x + 8 : x - 6 - lbl.Width, ly));
        }
        Node(CX(0), m.Cards[0].T0, leftLabel: true);
        for (var i = 0; i < n; i++) Node(CX(i) + cw, m.Cards[i].T1, leftLabel: false);

        // 进行中的红虚线游标
        for (var i = 0; i < n; i++)
        {
            var c = m.Cards[i];
            if (c.State != "act") continue;
            var x = CX(i) + Math.Clamp(c.Frac, 0, 1) * cw;
            ctx.DrawLine(new Pen(red, 1.6) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) },
                         new Point(x, BT - 6), new Point(x, BB + 6));
        }
    }
}
