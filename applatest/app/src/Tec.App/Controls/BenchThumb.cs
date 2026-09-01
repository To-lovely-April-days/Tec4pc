using Tec.Hmi.Ui.Controls;
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

    /// <summary>
    /// 卡片上实际画哪张图、多宽：插在工位上的 Tr / pH 画插入形态（斜 9.6° 那支），
    /// 宽度按插入图重取——存的 W 是独立形态的。跟画布、运行页总览同一副样子，
    /// 这张缩略图的全部意义就是「这份实验的台面长什么样」。
    /// </summary>
    private static (string Art, double W) EffectiveArt(ThumbPart p)
        => Services.BenchDock.InsertArtFor(p.Art, p.Anchor) is { } ins
            ? (ins, Services.BenchDock.DisplayWidth(ins))
            : (p.Art, p.W);

    public override void Render(DrawingContext ctx)
    {
        var parts = Parts?.OfType<ThumbPart>().ToList() ?? new List<ThumbPart>();
        if (parts.Count == 0 || Bounds.Width < 4 || Bounds.Height < 4) { Empty(ctx); return; }

        // 包围盒 → 等比缩放塞进卡片，留一圈边
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (var p in parts)
        {
            var (art, w) = EffectiveArt(p);
            x0 = Math.Min(x0, p.X);
            y0 = Math.Min(y0, p.Y);
            x1 = Math.Max(x1, p.X + w + BenchDockPad);
            y1 = Math.Max(y1, p.Y + HeightOf(art, w) + BenchDockPad);
        }
        var bw = Math.Max(x1 - x0, 1);
        var bh = Math.Max(y1 - y0, 1);
        var s = Math.Min((Bounds.Width - Pad * 2) / bw, (Bounds.Height - Pad * 2) / bh);
        // 只缩不放：设备本来就小时放大会糊，居中摆着就行
        s = Math.Min(s, 1);
        var ox = (Bounds.Width - bw * s) / 2 - x0 * s;
        var oy = (Bounds.Height - bh * s) / 2 - y0 * s;

        Point At(double x, double y) => new(ox + x * s, oy + y * s);

        // 先画设备。宿主画在最下面，探头压在上面，和画布一个叠放顺序
        foreach (var p in parts.OrderBy(p => Services.BenchDock.IsHost(p.Art) ? 0 : 1))
        {
            var (key, w) = EffectiveArt(p);
            var art = DeviceArtCache.Get(key);
            var at = At(p.X + BenchDockPad / 2.0, p.Y + BenchDockPad / 2.0);
            if (art is null)
            {
                ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#e9ecef")), null,
                    new Rect(at.X, at.Y, w * s, HeightOf(key, w) * s), 2, 2);
                continue;
            }
            using var _ = ctx.PushTransform(Matrix.CreateTranslation(at.X, at.Y));
            art.Render(ctx, w * s / art.ViewWidth,
                       new SvgArt.Paint(Color.Parse("#c2c2c2"), Color.Parse("#c2c2c2"), false, false));
        }

        // 管路画在设备之上，**画法用画布那一份（BenchLinks.Draw）**：
        // 从前这里自己描一条细彩线，泵的管子在卡片上是品红一划、
        // 到了画布上却是深描边浅芯的那根管——同一份台面两副样子。
        // Tr / pH 没有管子（插入件画在工位上），跳过，跟画布一致。
        var byId = parts.Where(p => p.Id.Length > 0)
                        .GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var p in parts)
        {
            if (p.Host is null || p.Anchor is null) continue;
            if (!byId.TryGetValue(p.Host, out var host)) continue;
            var a = Services.BenchDock.AnchorById(p.Anchor);
            if (a is null || a.Accept is "tr" or "ph") continue;

            var link = Services.BenchDock.Link(p.Art, new Point(p.X, p.Y), p.W, p.Side,
                                               p.Id, host.Id, new Point(host.X, host.Y), host.W,
                                               Array.Empty<int>(), a);
            var pts = Services.BenchDock.Route(link.From, link.FromDir, link.To, link.ToDir,
                                               link.Kind == LinkKind.Probe ? 18
                                               : link.Kind == LinkKind.Feed ? Math.Max(24 * link.Scale, 10)
                                               : 24)
                              .Select(q => At(q.X, q.Y)).ToList();
            BenchLinks.Draw(ctx, link, pts, s, labels: false);
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
