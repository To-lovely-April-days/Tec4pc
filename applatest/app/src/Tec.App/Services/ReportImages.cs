using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Tec.App.Controls;
using Tec.Core;
using Tec.Core.Data;
using Tec.Core.Export;
using Tec.Core.Records;

namespace Tec.App.Services;

/// <summary>
/// 报告里的趋势图。
///
/// 画的是**界面上那一个 TrendView 控件**，一笔一画完全一致——
/// 另写一份报告专用的画法，迟早和屏幕上看到的对不上（§13.1）。
/// 画完取原始像素交给 Core：Core 不认识 Avalonia，也不该为了一张图去解 PNG。
/// </summary>
public static class ReportImages
{
    private const double W = 900, H = 380;

    /// <summary>
    /// 给这一炉的每一路画一张。没采到点的那一路**不画**——
    /// 一张空坐标系贴进报告，比缺这一节更容易让人以为「这一路没温度」。
    /// </summary>
    public static List<ImageBlock> Charts(RunRecord rec, ISampleSource samples, int maxPoints = 900)
    {
        var list = new List<ImageBlock>();
        foreach (var ch in rec.Channels.OrderBy(c => c.Channel))
        {
            var model = Model(ch, samples, maxPoints, out var count);
            if (model is null) continue;

            var block = Render(model, ch, count);
            if (block is not null) list.Add(block);
        }
        return list;
    }

    private static TrendModel? Model(ChannelRun ch, ISampleSource samples, int maxPoints, out int count)
    {
        count = 0;
        var from = ch.StartedAt;
        var to = ch.FinishedAt ?? from;
        var span = (to - from).TotalSeconds;

        TrendSeries? Series(string tag)
        {
            var snap = samples.Snapshot(ch.Channel, tag);
            if (snap.Length < 2) return null;
            var pts = new List<(double, double)>(Math.Min(snap.Length, maxPoints) + 2);
            var step = Math.Max(1, snap.Length / maxPoints);
            for (var i = 0; i < snap.Length; i += step)
            {
                var s = snap[i];
                if (s.WallClock < from || (span > 0 && s.WallClock > to)) continue;
                pts.Add(((s.WallClock - from).TotalSeconds, s.Value));
            }
            return pts.Count < 2 ? null : new TrendSeries { Points = pts };
        }

        var tr = Series("Tr");
        var tj = Series("Tj");
        if (tr is null && tj is null) return null;
        count = (tr?.Points.Count ?? 0) + (tj?.Points.Count ?? 0);

        var ph = Series("pH");
        var last = Math.Max(tr?.Points[^1].T ?? 0, tj?.Points[^1].T ?? 0);
        return new TrendModel
        {
            AxisFrom = 0,
            AxisTo = Math.Max(60, span > 0 ? span : last),
            NowSec = last,
            HasPh = ph is not null,
            Label = sec => Fmt.Hms(TimeSpan.FromSeconds(sec)),
            Tr = tr,
            Tj = tj,
            Dt = Series("dT"),
            Ph = ph
        };
    }

    private static ImageBlock? Render(TrendModel model, ChannelRun ch, int points)
    {
        var root = Compose(model);
        var size = new Size(W, H);
        root.Measure(size);
        root.Arrange(new Rect(size));
        root.UpdateLayout();

        // 2× 渲染：报告打印出来是 A4 上 17 cm 宽，1× 的线在纸上是虚的
        const double scale = 2;
        var px = new PixelSize((int)Math.Round(W * scale), (int)Math.Round(H * scale));
        using var bmp = new RenderTargetBitmap(px, new Vector(96 * scale, 96 * scale));
        bmp.Render(root);

        var stride = px.Width * 4;
        var bytes = new byte[(long)stride * px.Height];
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            bmp.CopyPixels(new PixelRect(0, 0, px.Width, px.Height),
                           handle.AddrOfPinnedObject(), bytes.Length, stride);
        }
        catch { return null; }
        finally { handle.Free(); }

        var span = ch.FinishedAt is { } f ? f - ch.StartedAt : TimeSpan.Zero;
        return new ImageBlock
        {
            Title = $"CH{ch.Channel}　·　{ch.Baseline.Recipe.Name}",
            Bgra = bytes,
            PixelWidth = px.Width,
            PixelHeight = px.Height,
            Note = $"横轴为本通道启动后的时长（{Fmt.Hms(span)}），"
                   + $"纵轴左 −50 ~ 190 ℃、右 pH 0 ~ 14；共 {points.ToString("N0", CultureInfo.InvariantCulture)} 个采样点绘制。"
                   + (ch.Simulated ? "本通道为仿真运行，不是真实实验数据。" : "")
        };
    }

    /// <summary>图例 + 图。离屏的可视树拿不到 App.axaml 里的样式，字体字号一律显式写死。</summary>
    private static Control Compose(TrendModel model)
    {
        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Background = Brushes.White,
            Width = W,
            Height = H
        };

        var legend = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 16,
            Margin = new Thickness(14, 10, 14, 6)
        };
        void Item(string color, string text)
        {
            var one = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            one.Children.Add(new Rectangle
            {
                Width = 18, Height = 3, RadiusX = 1.5, RadiusY = 1.5,
                Fill = new SolidColorBrush(Color.Parse(color)),
                VerticalAlignment = VerticalAlignment.Center
            });
            one.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 12,
                FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI, Microsoft YaHei, system-ui, Arial"),
                Foreground = new SolidColorBrush(Color.Parse("#444444")),
                VerticalAlignment = VerticalAlignment.Center
            });
            legend.Children.Add(one);
        }
        if (model.Tr is not null) Item("#e02020", "Tr 釜内");
        if (model.Tj is not null) Item("#3f6fd8", "Tj 夹套");
        if (model.Dt is not null) Item("#b56cc9", "Tr−Tj");
        if (model.Ph is not null) Item("#2f8f49", "pH");
        grid.Children.Add(legend);

        var chart = new Border
        {
            Margin = new Thickness(14, 0, 14, 12),
            BorderBrush = new SolidColorBrush(Color.Parse("#dcdcdc")),
            BorderThickness = new Thickness(1),
            Background = Brushes.White,
            ClipToBounds = true,
            Child = new TrendView { Model = model }
        };
        Grid.SetRow(chart, 1);
        grid.Children.Add(chart);
        return grid;
    }
}
