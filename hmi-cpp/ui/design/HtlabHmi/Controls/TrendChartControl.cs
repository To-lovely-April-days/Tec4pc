using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using HtlabHmi.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace HtlabHmi.Controls;

public class TrendChartControl : Control
{
    public static readonly StyledProperty<IReadOnlyList<TrendSample>?> PrimarySamplesProperty =
        AvaloniaProperty.Register<TrendChartControl, IReadOnlyList<TrendSample>?>(nameof(PrimarySamples));

    public static readonly StyledProperty<IReadOnlyList<TrendSample>?> SecondarySamplesProperty =
        AvaloniaProperty.Register<TrendChartControl, IReadOnlyList<TrendSample>?>(nameof(SecondarySamples));

    public static readonly StyledProperty<IReadOnlyList<TrendMarker>?> MarkersProperty =
        AvaloniaProperty.Register<TrendChartControl, IReadOnlyList<TrendMarker>?>(nameof(Markers));

    public static readonly StyledProperty<int> RevisionProperty =
        AvaloniaProperty.Register<TrendChartControl, int>(nameof(Revision));

    public static readonly StyledProperty<bool> DualChannelModeProperty =
        AvaloniaProperty.Register<TrendChartControl, bool>(nameof(DualChannelMode));

    public static readonly StyledProperty<bool> XAxisClockProperty =
        AvaloniaProperty.Register<TrendChartControl, bool>(nameof(XAxisClock), true);

    public static readonly StyledProperty<bool> YAxisAllProperty =
        AvaloniaProperty.Register<TrendChartControl, bool>(nameof(YAxisAll), true);

    public static readonly StyledProperty<bool> ShowTrProperty =
        AvaloniaProperty.Register<TrendChartControl, bool>(nameof(ShowTr), true);

    public static readonly StyledProperty<bool> ShowTjProperty =
        AvaloniaProperty.Register<TrendChartControl, bool>(nameof(ShowTj), true);

    public static readonly StyledProperty<bool> ShowTjSpProperty =
        AvaloniaProperty.Register<TrendChartControl, bool>(nameof(ShowTjSp), true);

    public static readonly StyledProperty<bool> ShowDeltaTProperty =
        AvaloniaProperty.Register<TrendChartControl, bool>(nameof(ShowDeltaT), false);

    public IReadOnlyList<TrendSample>? PrimarySamples { get => GetValue(PrimarySamplesProperty); set => SetValue(PrimarySamplesProperty, value); }
    public IReadOnlyList<TrendSample>? SecondarySamples { get => GetValue(SecondarySamplesProperty); set => SetValue(SecondarySamplesProperty, value); }
    public IReadOnlyList<TrendMarker>? Markers { get => GetValue(MarkersProperty); set => SetValue(MarkersProperty, value); }
    public int Revision { get => GetValue(RevisionProperty); set => SetValue(RevisionProperty, value); }
    public bool DualChannelMode { get => GetValue(DualChannelModeProperty); set => SetValue(DualChannelModeProperty, value); }
    public bool XAxisClock { get => GetValue(XAxisClockProperty); set => SetValue(XAxisClockProperty, value); }
    public bool YAxisAll { get => GetValue(YAxisAllProperty); set => SetValue(YAxisAllProperty, value); }
    public bool ShowTr { get => GetValue(ShowTrProperty); set => SetValue(ShowTrProperty, value); }
    public bool ShowTj { get => GetValue(ShowTjProperty); set => SetValue(ShowTjProperty, value); }
    public bool ShowTjSp { get => GetValue(ShowTjSpProperty); set => SetValue(ShowTjSpProperty, value); }
    public bool ShowDeltaT { get => GetValue(ShowDeltaTProperty); set => SetValue(ShowDeltaTProperty, value); }

    static TrendChartControl()
    {
        AffectsRender<TrendChartControl>(
            PrimarySamplesProperty, SecondarySamplesProperty, MarkersProperty, RevisionProperty,
            DualChannelModeProperty, XAxisClockProperty, YAxisAllProperty,
            ShowTrProperty, ShowTjProperty, ShowTjSpProperty, ShowDeltaTProperty);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 40 || h < 40) return;

        context.FillRectangle(Brushes.White, new Rect(0, 0, w, h));

        double padL = 54, padR = 20, padT = 14, padB = 30;
        double plotW = w - padL - padR;
        double plotH = h - padT - padB;
        if (plotW <= 10 || plotH <= 10) return;

        var primary = PrimarySamples;
        if (primary == null || primary.Count < 2) return;

        double minY = 0, maxY = 80;
        if (YAxisAll)
        {
            var vals = primary.SelectMany(s => new[] { s.Tr, s.Tj, s.TjSp }).ToList();
            if (DualChannelMode && SecondarySamples != null)
                vals.AddRange(SecondarySamples.SelectMany(s => new[] { s.Tr, s.Tj }));
            minY = Math.Floor((vals.Min() - 5) / 10.0) * 10.0;
            maxY = Math.Ceiling((vals.Max() + 5) / 10.0) * 10.0;
            if (maxY - minY < 20) { minY -= 10; maxY += 10; }
        }
        else
        {
            double center = primary[^1].Tr;
            minY = Math.Floor(center - 10);
            maxY = Math.Ceiling(center + 10);
        }

        var gridPen = new Pen(new SolidColorBrush(Color.Parse("#EAEFF5")), 1);
        var axisPen = new Pen(new SolidColorBrush(Color.Parse("#D5DEE8")), 1);
        var labelBrush = new SolidColorBrush(Color.Parse("#8792A0"));
        var tf = new Typeface("Microsoft YaHei UI");

        int yTicks = 5;
        for (int i = 0; i <= yTicks; i++)
        {
            double frac = (double)i / yTicks;
            double y = padT + plotH * (1.0 - frac);
            double val = minY + (maxY - minY) * frac;
            context.DrawLine(i == 0 ? axisPen : gridPen, new Point(padL, y), new Point(padL + plotW, y));
            var ft = new FormattedText($"{val:F0}℃", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, 12, labelBrush);
            context.DrawText(ft, new Point(padL - ft.Width - 8, y - ft.Height / 2));
        }

        int xTicks = 6;
        for (int i = 0; i <= xTicks; i++)
        {
            double frac = (double)i / xTicks;
            double x = padL + plotW * frac;
            context.DrawLine(i == 0 ? axisPen : gridPen, new Point(x, padT), new Point(x, padT + plotH));
            int idx = Math.Clamp((int)Math.Round(frac * (primary.Count - 1)), 0, primary.Count - 1);
            string lbl = XAxisClock ? primary[idx].Time.ToString("HH:mm") : $"{primary[idx].ElapsedMin:F0} min";
            var ft = new FormattedText(lbl, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, 12, labelBrush);
            context.DrawText(ft, new Point(x - ft.Width / 2, padT + plotH + 8));
        }

        double MapY(double v) => padT + plotH * (1.0 - Math.Clamp((v - minY) / Math.Max(0.001, maxY - minY), 0, 1));
        double MapX(int i, int count) => padL + plotW * ((double)i / Math.Max(1, count - 1));

        void DrawSeries(IReadOnlyList<TrendSample> list, Func<TrendSample, double> selector, string hex, double thickness, bool dashed)
        {
            if (list.Count < 2) return;
            var geom = new StreamGeometry();
            using (var ctx = geom.Open())
            {
                ctx.BeginFigure(new Point(MapX(0, list.Count), MapY(selector(list[0]))), false);
                for (int i = 1; i < list.Count; i++)
                    ctx.LineTo(new Point(MapX(i, list.Count), MapY(selector(list[i]))));
                ctx.EndFigure(false);
            }
            var brush = new SolidColorBrush(Color.Parse(hex));
            var pen = dashed
                ? new Pen(brush, thickness, new DashStyle(new[] { 5.0, 3.0 }, 0))
                : new Pen(brush, thickness);
            context.DrawGeometry(null, pen, geom);

            var endPt = new Point(MapX(list.Count - 1, list.Count), MapY(selector(list[^1])));
            context.DrawEllipse(brush, new Pen(Brushes.White, 1.5), endPt, 4, 4);
        }

        if (DualChannelMode)
        {
            // Exact Trends.dc.html palette: #E5645A (Tr1), #19B5A5 (Tj1), #F0A9A4 (Tr2), #9ADAD2 (Tj2)
            if (SecondarySamples != null && SecondarySamples.Count >= 2)
            {
                DrawSeries(SecondarySamples, s => s.Tj, "#9ADAD2", 2.2, false);
                DrawSeries(SecondarySamples, s => s.Tr, "#F0A9A4", 2.4, false);
            }
            DrawSeries(primary, s => s.Tj, "#19B5A5", 2.4, false);
            DrawSeries(primary, s => s.Tr, "#E5645A", 2.8, false);
        }
        else
        {
            // Exact ChTrend.dc.html palette: #E5645A (Tr), #19B5A5 (Tj), #F2A33A (Tj SP), #6366F1 (DeltaT)
            if (ShowTjSp) DrawSeries(primary, s => s.TjSp, "#F2A33A", 2.0, true);
            if (ShowTj) DrawSeries(primary, s => s.Tj, "#19B5A5", 2.4, false);
            if (ShowTr) DrawSeries(primary, s => s.Tr, "#E5645A", 2.8, false);
            if (ShowDeltaT) DrawSeries(primary, s => s.Tr - s.Tj + 30.0, "#7C5CFF", 1.8, false);

            if (Markers != null && Markers.Count > 0)
            {
                double minE = primary[0].ElapsedMin;
                double maxE = primary[^1].ElapsedMin;
                var mPen = new Pen(new SolidColorBrush(Color.Parse("#00559C")), 1.5, new DashStyle(new[] { 4.0, 3.0 }, 0));
                var mBrush = new SolidColorBrush(Color.Parse("#00559C"));
                foreach (var mk in Markers)
                {
                    if (mk.ElapsedMin < minE || mk.ElapsedMin > maxE) continue;
                    double mx = padL + plotW * ((mk.ElapsedMin - minE) / Math.Max(0.01, maxE - minE));
                    context.DrawLine(mPen, new Point(mx, padT), new Point(mx, padT + plotH));
                    var ft = new FormattedText(mk.Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, 11, mBrush);
                    context.DrawText(ft, new Point(mx + 4, padT + 2));
                }
            }
        }
    }
}
