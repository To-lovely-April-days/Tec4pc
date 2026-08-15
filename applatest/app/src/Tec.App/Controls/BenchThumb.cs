using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Tec.App.Services;

namespace Tec.App.Controls;

/// <summary>
/// 台面缩略图：把一份实验的设备按真实坐标缩放画出来，管路一并画上。
/// 最近实验卡片用的就是它——四张固定示意图看不出哪份实验是哪份，
/// 摆位不同的两份台面必须一眼能分出来。
/// </summary>
public sealed class BenchThumb : Control
{
    public static readonly StyledProperty<IEnumerable?> PartsProperty =
        AvaloniaProperty.Register<BenchThumb, IEnumerable?>(nameof(Parts));

    static BenchThumb() => AffectsRender<BenchThumb>(PartsProperty);

    public IEnumerable? Parts
    {
        get => GetValue(PartsProperty);
        set => SetValue(PartsProperty, value);
    }

    private const double Pad = 10;

    private static double HeightOf(string art, double w)
    {
        var a = DeviceArtCache.Get(art);
        return a is null ? w * 0.8 : w * a.ViewHeight / a.ViewWidth;
    }

    public override void Render(DrawingContext ctx)
    {
        var parts = Parts?.OfType<ThumbPart>().ToList() ?? new List<ThumbPart>();
        if (parts.Count == 0 || Bounds.Width < 4 || Bounds.Height < 4) { Empty(ctx); return; }

        // 包围盒 → 等比缩放塞进卡片，留一圈边
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (var p in parts)
        {
            x0 = Math.Min(x0, p.X);
            y0 = Math.Min(y0, p.Y);
            x1 = Math.Max(x1, p.X + p.W + BenchDockPad);
            y1 = Math.Max(y1, p.Y + HeightOf(p.Art, p.W) + BenchDockPad);
        }
        var bw = Math.Max(x1 - x0, 1);
        var bh = Math.Max(y1 - y0, 1);
        var s = Math.Min((Bounds.Width - Pad * 2) / bw, (Bounds.Height - Pad * 2) / bh);
        // 只缩不放：设备本来就小时放大会糊，居中摆着就行
        s = Math.Min(s, 1);
        var ox = (Bounds.Width - bw * s) / 2 - x0 * s;
        var oy = (Bounds.Height - bh * s) / 2 - y0 * s;

        Point At(double x, double y) => new(ox + x * s, oy + y * s);

        // 先画管路，压在设备下面
        var byId = parts.Where(p => p.Id.Length > 0)
                        .GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var p in parts)
        {
            if (p.Host is null || p.Anchor is null) continue;
            if (!byId.TryGetValue(p.Host, out var host)) continue;
            var a = Services.BenchDock.Anchors.FirstOrDefault(x => x.Id == p.Anchor);
            if (a is null) continue;

            var to = Services.BenchDock.AnchorWorld(new Point(host.X, host.Y), host.W, a);
            var from = Services.BenchDock.PlugWorld(new Point(p.X, p.Y), p.W, p.Art, p.Side);
            var kind = Services.BenchDock.LinkOf(p.Art);
            var pts = Services.BenchDock.Route(from, Services.BenchDock.ExitDir(p.Art, from, to),
                                               to, a.Dir, 18)
                              .Select(q => At(q.X, q.Y)).ToList();

            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(pts[0], false);
                for (var i = 1; i < pts.Count; i++) g.LineTo(pts[i]);
                g.EndFigure(false);
            }
            var col = kind switch
            {
                LinkKind.Probe => Color.Parse("#8d9298"),
                LinkKind.Feed => Color.Parse("#c53a9d"),
                LinkKind.Sample => Color.Parse("#dba32c"),
                _ => Color.Parse("#b6bbc0")
            };
            ctx.DrawGeometry(null, new Pen(new SolidColorBrush(col, 0.85), Math.Max(1, 1.6 * s))
            { LineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, geo);
        }

        // 再画设备。宿主画在最下面，探头压在上面，和画布一个叠放顺序
        foreach (var p in parts.OrderBy(p => Services.BenchDock.IsHost(p.Art) ? 0 : 1))
        {
            var art = DeviceArtCache.Get(p.Art);
            var at = At(p.X + BenchDockPad / 2.0, p.Y + BenchDockPad / 2.0);
            if (art is null)
            {
                ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#e9ecef")), null,
                    new Rect(at.X, at.Y, p.W * s, HeightOf(p.Art, p.W) * s), 2, 2);
                continue;
            }
            using var _ = ctx.PushTransform(Matrix.CreateTranslation(at.X, at.Y));
            art.Render(ctx, p.W * s / art.ViewWidth,
                       new SvgArt.Paint(Color.Parse("#c2c2c2"), Color.Parse("#c2c2c2"), false, false));
        }
    }

    private const double BenchDockPad = Services.BenchDock.NodePad * 2;

    /// <summary>台面是空的：画一个虚线框，别留一片白让人以为是没加载出来。</summary>
    private void Empty(DrawingContext ctx)
    {
        var w = Math.Min(Bounds.Width - Pad * 4, 120);
        var h = Math.Min(Bounds.Height - Pad * 4, 64);
        if (w <= 0 || h <= 0) return;
        var r = new Rect((Bounds.Width - w) / 2, (Bounds.Height - h) / 2, w, h);
        var pen = new Pen(new SolidColorBrush(Color.Parse("#d5d9dd")), 1.4)
        { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) };
        ctx.DrawRectangle(null, pen, new RoundedRect(r, 4));
    }
}
