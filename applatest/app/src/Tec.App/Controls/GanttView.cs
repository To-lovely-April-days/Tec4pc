using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Tec.App.Controls;

public enum GanttBarKind { Plan, Real, Sum, Mark, Loop }

public sealed class GanttRowBar
{
    public required GanttBarKind Kind { get; init; }
    public required double StartSec { get; init; }
    public double DurSec { get; init; }
    public required Color Color { get; init; }
    public string? Text { get; init; }
    /// <summary>Mark 未执行时半透明（原型 opacity:.32）。</summary>
    public bool Dim { get; init; }
}

public sealed class GanttRow
{
    /// <summary>true = 通道汇总行（粗体 + 色块），false = 步骤子行（缩进细体）。</summary>
    public bool IsGroup { get; init; }
    public required string Name { get; init; }
    public Color? Swatch { get; init; }
    /// <summary>右侧灰字（时长 / 停用 / 未启动 / 未编排）。</summary>
    public string? Note { get; init; }
    /// <summary>组行的偏差角标 +5:00，warn/bad 上色。</summary>
    public string? Dev { get; init; }
    public string DevClass { get; init; } = "";
    public List<GanttRowBar> Bars { get; } = new();
}

public sealed class GanttModel
{
    public double AxisFrom { get; init; }
    public double AxisTo { get; init; } = 600;
    /// <summary>墙钟模式的红线位置（秒）；null = 不画整条红线。</summary>
    public double? NowSec { get; init; }
    public required Func<double, string> Label { get; init; }
    public List<GanttRow> Rows { get; } = new();
    /// <summary>按通道对齐时每通道一段短红线：(秒, 起始行, 行数)。</summary>
    public List<(double Sec, int Row, int Count)> NowMarks { get; } = new();
}

/// <summary>
/// 步骤甘特（原型 renderGantt 的 1:1）：
/// 左 164px 标签列（组行粗体 / 子行缩进 22px）+ 右侧条形区。
/// 刻度行 24px，数据行 26px；条形：gsum（top10 h6 α.45）、gplan（top4 h8 α.32）、
/// greal（top14 h8 实色）、gmk（6×13 标记点）、gloop（灰底 ×n）。红线 gnow 2px。
/// </summary>
public sealed class GanttView : Control
{
    public static readonly StyledProperty<GanttModel?> ModelProperty =
        AvaloniaProperty.Register<GanttView, GanttModel?>(nameof(Model));

    private const double LeftW = 164, ScaleH = 24, RowH = 26;

    static GanttView()
    {
        AffectsRender<GanttView>(ModelProperty);
        AffectsMeasure<GanttView>(ModelProperty);
    }

    public GanttModel? Model
    {
        get => GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
        => new(double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width,
               ScaleH + (Model?.Rows.Count ?? 0) * RowH + 4);

    public override void Render(DrawingContext ctx)
    {
        var m = Model;
        if (m is null) return;
        var w = Bounds.Width;
        var plotW = Math.Max(w - LeftW, 30);
        var span = Math.Max(m.AxisTo - m.AxisFrom, 1);
        double X(double sec) => LeftW + (sec - m.AxisFrom) / span * plotW;

        var ink = new SolidColorBrush(Color.Parse("#2b2b2b"));
        var sub = new SolidColorBrush(Color.Parse("#5a5a5a"));
        var gt = new SolidColorBrush(Color.Parse("#9a9a9a"));
        var rowLine = new Pen(new SolidColorBrush(Color.Parse("#ececec")), 1);
        var scaleLine = new Pen(new SolidColorBrush(Color.Parse("#d0d0d0")), 1);

        // 刻度行
        ctx.DrawLine(scaleLine, new Point(0, ScaleH - 0.5), new Point(w, ScaleH - 0.5));
        var ticks = TrendView.Ticks(m.AxisFrom, m.AxisTo, 5).ToList();
        foreach (var t in ticks)
        {
            var label = m.Label(t);
            var ft = Ft(label, 10, new SolidColorBrush(Color.Parse("#666666")));
            // 一律居中，再夹回框内。原来靠 p>0.97 判断右端，末刻度落在 0.95 那种
            // 就照样居中，右半截露到框外被裁成「17:1」
            var x = Math.Clamp(X(t) - ft.Width / 2, LeftW, Math.Max(LeftW, w - ft.Width));
            ctx.DrawText(ft, new Point(x, 5));
        }

        var y = ScaleH;
        for (var r = 0; r < m.Rows.Count; r++, y += RowH)
        {
            var row = m.Rows[r];
            ctx.DrawLine(rowLine, new Point(0, y + RowH - 0.5), new Point(w, y + RowH - 0.5));

            // 纵向格线（cell）
            foreach (var t in ticks)
                ctx.DrawLine(rowLine, new Point(X(t), y), new Point(X(t), y + RowH));

            // 左栏
            var tx = row.IsGroup ? 6.0 : 22.0;
            if (row.Swatch is { } sw)
            {
                ctx.DrawRectangle(new SolidColorBrush(sw), null, new Rect(tx, y + (RowH - 9) / 2, 9, 9), 2, 2);
                tx += 14;
            }
            var nameFt = Ft(row.Name, 11, row.IsGroup ? ink : sub, row.IsGroup ? FontWeight.SemiBold : FontWeight.Light);
            var maxName = LeftW - tx - 6 - (row.Dev is not null ? 34 : 0) - (row.Note is not null ? 34 : 0);
            ctx.DrawText(nameFt, new Point(tx, y + (RowH - nameFt.Height) / 2));
            var rx = LeftW - 6;
            if (row.Note is { } note)
            {
                var nf = Ft(note, 10, gt);
                rx -= nf.Width;
                ctx.DrawText(nf, new Point(rx, y + (RowH - nf.Height) / 2));
                rx -= 5;
            }
            if (row.Dev is { } dev)
            {
                var color = row.DevClass switch
                {
                    "bad" => "#c0392b", "warn" => "#a8710a", _ => "#5a5a5a"
                };
                var df = Ft(dev, 10, new SolidColorBrush(Color.Parse(color)), FontWeight.SemiBold);
                rx -= df.Width;
                ctx.DrawText(df, new Point(rx, y + (RowH - df.Height) / 2));
            }

            // 条形
            foreach (var bar in row.Bars)
            {
                var x1 = X(bar.StartSec);
                var bw = Math.Max(bar.DurSec / span * plotW, 2);
                switch (bar.Kind)
                {
                    case GanttBarKind.Sum:
                        ctx.DrawRectangle(new SolidColorBrush(bar.Color, 0.45), null,
                            new Rect(x1, y + 10, bw, 6), 3, 3);
                        break;
                    case GanttBarKind.Plan:
                        ctx.DrawRectangle(new SolidColorBrush(bar.Color, 0.32), null,
                            new Rect(x1, y + 4, bw, 8), 2, 2);
                        break;
                    case GanttBarKind.Real:
                        ctx.DrawRectangle(new SolidColorBrush(bar.Color), null,
                            new Rect(x1, y + 14, bw, 8), 2, 2);
                        break;
                    case GanttBarKind.Mark:
                        ctx.DrawRectangle(new SolidColorBrush(bar.Color, bar.Dim ? 0.32 : 1), null,
                            new Rect(x1 - 3, y + 6, 6, 13), 2, 2);
                        break;
                    case GanttBarKind.Loop:
                        ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#e2e2e2")), null,
                            new Rect(x1, y + 8, bw, 10), 2, 2);
                        if (bar.Text is { } lt)
                        {
                            var lf = Ft(lt, 9, new SolidColorBrush(Color.Parse("#6b6b6b")));
                            ctx.DrawText(lf, new Point(x1 + bw - lf.Width - 3, y + 8.5));
                        }
                        break;
                }
            }
        }

        // 红线
        var nowPen = new SolidColorBrush(Color.Parse("#e02020"));
        if (m.NowSec is { } now)
            ctx.DrawRectangle(nowPen, null, new Rect(X(now) - 1, ScaleH, 2, y - ScaleH));
        foreach (var (sec, rowIdx, count) in m.NowMarks)
            ctx.DrawRectangle(nowPen, null,
                new Rect(X(sec) - 1, ScaleH + rowIdx * RowH, 2, count * RowH));
    }

    private static FormattedText Ft(string s, double size, IBrush brush, FontWeight weight = FontWeight.Normal)
        => new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
               new Typeface("Segoe UI", FontStyle.Normal, weight), size, brush);
}
