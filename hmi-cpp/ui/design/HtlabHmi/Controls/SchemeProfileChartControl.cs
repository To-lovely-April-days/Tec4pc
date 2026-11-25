using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using HtlabHmi.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace HtlabHmi.Controls;

/// <summary>
/// Renders a 1:1 temperature-vs-time step profile chart for SchemeOverviewView & CryViewPart.
/// </summary>
public class SchemeProfileChartControl : Control
{
    public static readonly StyledProperty<IReadOnlyList<SequenceStep>?> StepsProperty =
        AvaloniaProperty.Register<SchemeProfileChartControl, IReadOnlyList<SequenceStep>?>(nameof(Steps));

    public static readonly StyledProperty<int> CurrentStepIndexProperty =
        AvaloniaProperty.Register<SchemeProfileChartControl, int>(nameof(CurrentStepIndex));

    public static readonly StyledProperty<bool> IsRunningProperty =
        AvaloniaProperty.Register<SchemeProfileChartControl, bool>(nameof(IsRunning));

    public static readonly StyledProperty<bool> CompactModeProperty =
        AvaloniaProperty.Register<SchemeProfileChartControl, bool>(nameof(CompactMode));

    public IReadOnlyList<SequenceStep>? Steps
    {
        get => GetValue(StepsProperty);
        set => SetValue(StepsProperty, value);
    }

    public int CurrentStepIndex
    {
        get => GetValue(CurrentStepIndexProperty);
        set => SetValue(CurrentStepIndexProperty, value);
    }

    public bool IsRunning
    {
        get => GetValue(IsRunningProperty);
        set => SetValue(IsRunningProperty, value);
    }

    public bool CompactMode
    {
        get => GetValue(CompactModeProperty);
        set => SetValue(CompactModeProperty, value);
    }

    static SchemeProfileChartControl()
    {
        AffectsRender<SchemeProfileChartControl>(StepsProperty, CurrentStepIndexProperty, IsRunningProperty, CompactModeProperty);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        double w = Bounds.Width, h = Bounds.Height;
        if (w < 60 || h < 40) return;

        double padL = CompactMode ? 36 : 48;
        double padR = CompactMode ? 12 : 20;
        double padT = CompactMode ? 14 : 24;
        double padB = CompactMode ? 22 : 30;
        double pw = w - padL - padR;
        double ph = h - padT - padB;

        var steps = Steps;
        if (steps == null || steps.Count == 0)
        {
            context.FillRectangle(new SolidColorBrush(Color.Parse("#FAFBFD")), new Rect(padL, padT, pw, ph));
            return;
        }

        // Build polyline points (timeMin, tempC, stepIdx, label)
        var pts = new List<(double t, double y, int idx, string lbl)>();
        double curT = 0;
        double curY = 25.0;
        pts.Add((curT, curY, -1, "起点"));
        for (int i = 0; i < steps.Count; i++)
        {
            var st = steps[i];
            double dur = Math.Max(10, st.DurationMin > 0 ? st.DurationMin : 25);
            double nextY = (st.Kind == StepKind.Tr || st.Kind == StepKind.Tj || st.Kind == StepKind.Wait)
                ? st.TargetTemp
                : curY;
            curT += dur;
            pts.Add((curT, nextY, i, st.ShortLabel));
            curY = nextY;
        }

        double totalT = Math.Max(1, curT);
        double minY = Math.Floor((pts.Min(p => p.y) - 10) / 10.0) * 10.0;
        double maxY = Math.Ceiling((pts.Max(p => p.y) + 10) / 10.0) * 10.0;
        if (maxY - minY < 30) { minY -= 15; maxY += 15; }

        double MapX(double t) => padL + pw * (t / totalT);
        double MapY(double y) => padT + ph * (1.0 - (y - minY) / (maxY - minY));

        var gridPen = new Pen(new SolidColorBrush(Color.Parse("#E8ECF1")), 1);
        var labelBrush = new SolidColorBrush(Color.Parse("#8792A0"));
        var tf = new Typeface("Microsoft YaHei UI");

        // Horizontal grid
        int yTicks = CompactMode ? 3 : 4;
        for (int i = 0; i <= yTicks; i++)
        {
            double frac = (double)i / yTicks;
            double yy = padT + ph * (1.0 - frac);
            double val = minY + (maxY - minY) * frac;
            context.DrawLine(gridPen, new Point(padL, yy), new Point(padL + pw, yy));
            var ft = new FormattedText($"{val:F0}℃", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, CompactMode ? 10 : 12, labelBrush);
            context.DrawText(ft, new Point(padL - ft.Width - 6, yy - ft.Height / 2));
        }

        // Step vertical dividers + labels
        var divPen = new Pen(new SolidColorBrush(Color.Parse("#DCE4ED")), 1, new DashStyle(new[] { 4.0, 3.0 }, 0));
        for (int i = 1; i < pts.Count; i++)
        {
            double x0 = MapX(pts[i - 1].t);
            double x1 = MapX(pts[i].t);
            int stepIdx = pts[i].idx;

            // Highlight active step band
            if (IsRunning && stepIdx == CurrentStepIndex)
            {
                context.FillRectangle(new SolidColorBrush(Color.Parse("#1400559C")), new Rect(x0, padT, x1 - x0, ph));
            }
            if (i < pts.Count - 1)
                context.DrawLine(divPen, new Point(x1, padT), new Point(x1, padT + ph));

            if (!CompactMode)
            {
                var lblColor = (IsRunning && stepIdx == CurrentStepIndex)
                    ? new SolidColorBrush(Color.Parse("#00559C"))
                    : new SolidColorBrush(Color.Parse("#4F5B67"));
                var ft = new FormattedText(pts[i].lbl, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, 12, lblColor);
                context.DrawText(ft, new Point((x0 + x1) / 2 - ft.Width / 2, padT - 18));

                var tFt = new FormattedText($"{pts[i].t:F0}m", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, 11, labelBrush);
                context.DrawText(tFt, new Point(x1 - tFt.Width / 2, padT + ph + 6));
            }
        }

        // Filled area under profile
        var areaGeom = new StreamGeometry();
        using (var ctx = areaGeom.Open())
        {
            ctx.BeginFigure(new Point(MapX(pts[0].t), padT + ph), true);
            foreach (var p in pts) ctx.LineTo(new Point(MapX(p.t), MapY(p.y)));
            ctx.LineTo(new Point(MapX(pts[^1].t), padT + ph));
            ctx.EndFigure(true);
        }
        context.DrawGeometry(new SolidColorBrush(Color.Parse("#1200559C")), null, areaGeom);

        // Draw planned curve & completed curve
        var futurePen = new Pen(new SolidColorBrush(Color.Parse("#9FB4CC")), 2.2, new DashStyle(new[] { 6.0, 4.0 }, 0));
        var donePen = new Pen(new SolidColorBrush(Color.Parse("#00559C")), 2.8);
        var activePen = new Pen(new SolidColorBrush(Color.Parse("#E5645A")), 2.8);

        for (int i = 1; i < pts.Count; i++)
        {
            var p0 = new Point(MapX(pts[i - 1].t), MapY(pts[i - 1].y));
            var p1 = new Point(MapX(pts[i].t), MapY(pts[i].y));
            int stepIdx = pts[i].idx;

            if (!IsRunning)
            {
                context.DrawLine(donePen, p0, p1);
            }
            else if (stepIdx < CurrentStepIndex)
            {
                context.DrawLine(donePen, p0, p1);
            }
            else if (stepIdx == CurrentStepIndex)
            {
                double frac = Math.Clamp((steps[stepIdx].ProgressPct) / 100.0, 0.05, 0.95);
                var pMid = new Point(p0.X + (p1.X - p0.X) * frac, p0.Y + (p1.Y - p0.Y) * frac);
                context.DrawLine(activePen, p0, pMid);
                context.DrawLine(futurePen, pMid, p1);
                context.DrawEllipse(new SolidColorBrush(Color.Parse("#E5645A")), new Pen(Brushes.White, 2), pMid, 5.5, 5.5);
            }
            else
            {
                context.DrawLine(futurePen, p0, p1);
            }
        }

        // Node dots
        for (int i = 0; i < pts.Count; i++)
        {
            var pt = new Point(MapX(pts[i].t), MapY(pts[i].y));
            bool isDone = !IsRunning || pts[i].idx < CurrentStepIndex;
            context.DrawEllipse(new SolidColorBrush(Color.Parse(isDone ? "#00559C" : "#9FB4CC")), new Pen(Brushes.White, 1.5), pt, 3.5, 3.5);
        }
    }
}
