using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Tec.App.Controls;

public sealed class GanttBar
{
    public required string Title { get; init; }
    /// <summary>秒，相对于 GanttModel.From。</summary>
    public required double PlanStart { get; init; }
    public required double PlanEnd { get; init; }
    public double? ActualStart { get; init; }
    public double? ActualEnd { get; init; }
    public bool Running { get; init; }
    public bool Bad { get; init; }
    public bool Loop { get; init; }
}

public sealed class GanttLane
{
    public required string Name { get; init; }
    public required Color Color { get; init; }
    public string Note { get; init; } = "";
    public List<GanttBar> Bars { get; } = new();
}

public sealed class GanttModel
{
    public bool WallClock { get; init; } = true;
    public double Span { get; init; } = 600;
    public double Now { get; init; }
    public DateTimeOffset Origin { get; init; }
    public List<GanttLane> Lanes { get; } = new();
}

/// <summary>
/// 甘特。计划条与实际条上下并排——**两个视图对不上就是错的**，
/// 所以这里的每一条都来自同一份 Schedule / StepRecord，不自己编时长（§13.1）。
/// </summary>
public sealed class GanttView : Control
{
    public static readonly StyledProperty<GanttModel?> ModelProperty =
        AvaloniaProperty.Register<GanttView, GanttModel?>(nameof(Model));

    private const double LaneHeight = 40;
    private const double HeaderHeight = 22;
    private const double LabelWidth = 76;

    static GanttView() => AffectsRender<GanttView>(ModelProperty);

    public GanttModel? Model
    {
        get => GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var lanes = Model?.Lanes.Count ?? 0;
        return new Size(double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width,
                        HeaderHeight + Math.Max(1, lanes) * LaneHeight + 6);
    }

    public override void Render(DrawingContext ctx)
    {
        var model = Model;
        var w = Bounds.Width;
        var plotLeft = LabelWidth;
        var plotWidth = Math.Max(20, w - plotLeft - 8);

        var line = new SolidColorBrush(Color.Parse("#e2e6ea"));
        var axisPen = new Pen(new SolidColorBrush(Color.Parse("#cfd5dc")), 1);
        var ink = new SolidColorBrush(Color.Parse("#1c2024"));
        var muted = new SolidColorBrush(Color.Parse("#6b7280"));

        if (model is null || model.Lanes.Count == 0)
        {
            Text(ctx, "尚无通道启动。通道各自启动，用户用几个就是几个。",
                 new Point(plotLeft, HeaderHeight), 12, muted);
            return;
        }

        var span = Math.Max(60, model.Span);
        double X(double seconds) => plotLeft + Math.Clamp(seconds / span, 0, 1) * plotWidth;

        // 时间刻度：墙钟对齐显示时刻，通道基准显示相对时长
        var ticks = 6;
        for (var i = 0; i <= ticks; i++)
        {
            var secs = span * i / ticks;
            var x = X(secs);
            ctx.DrawLine(axisPen, new Point(x, HeaderHeight - 4), new Point(x, Bounds.Height - 4));
            var label = model.WallClock
                ? model.Origin.AddSeconds(secs).ToString("HH:mm", CultureInfo.InvariantCulture)
                : Hm(secs);
            Text(ctx, label, new Point(x + 2, 2), 11, muted);
        }

        var y = HeaderHeight;
        foreach (var lane in model.Lanes)
        {
            ctx.DrawLine(new Pen(line, 1), new Point(0, y), new Point(w, y));
            Text(ctx, lane.Name, new Point(6, y + 6), 12, ink, bold: true);
            if (lane.Note.Length > 0) Text(ctx, lane.Note, new Point(6, y + 21), 10, muted);

            var planBrush = new SolidColorBrush(lane.Color, 0.22);
            var planPen = new Pen(new SolidColorBrush(lane.Color, 0.45), 1);
            var realBrush = new SolidColorBrush(lane.Color);

            foreach (var bar in lane.Bars)
            {
                var x1 = X(bar.PlanStart);
                var x2 = X(bar.PlanEnd);
                var pw = Math.Max(2, x2 - x1);
                ctx.DrawRectangle(planBrush, bar.Loop ? planPen : null,
                    new Rect(x1, y + 6, pw, 9), 2, 2);

                if (bar.ActualStart is not { } a) continue;
                var b = bar.ActualEnd ?? model.Now;
                var rx1 = X(a);
                var rw = Math.Max(2, X(b) - rx1);
                var brush = bar.Bad ? new SolidColorBrush(Color.Parse("#b3261e")) : realBrush;
                ctx.DrawRectangle(brush, null, new Rect(rx1, y + 19, rw, 9), 2, 2);
                if (bar.Running)
                    ctx.DrawRectangle(new SolidColorBrush(lane.Color, 0.35), null,
                        new Rect(rx1, y + 19, rw, 9), 2, 2);
            }

            y += LaneHeight;
        }

        // 当前时刻线
        var nowX = X(model.Now);
        ctx.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#b3261e")), 1.5),
                     new Point(nowX, HeaderHeight - 6), new Point(nowX, Bounds.Height - 2));
    }

    private static string Hm(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return $"{(int)t.TotalHours}:{t.Minutes:D2}";
    }

    private static void Text(DrawingContext ctx, string text, Point at, double size, IBrush brush, bool bold = false)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI", FontStyle.Normal, bold ? FontWeight.SemiBold : FontWeight.Normal),
            size, brush);
        ctx.DrawText(ft, at);
    }
}
