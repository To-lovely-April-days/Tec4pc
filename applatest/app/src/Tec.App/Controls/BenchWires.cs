using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Tec.App.ViewModels;

namespace Tec.App.Controls;

/// <summary>
/// 台面上的绑定连线（原型 renderWires）：把探头 / 加料泵连到它所绑定通道的孔位。
/// 线用通道色的虚线——一眼能看出"这根探头插在哪个釜里"。
/// </summary>
public sealed class BenchWires : Control
{
    /// <summary>反应器图形里两个孔位的中心（viewBox 坐标），与 ART.reactor2 一致。</summary>
    private const double WellX1 = 46, WellX2 = 126, WellY = 75.5, ArtVw = 176;
    private const double NodePad = 7;   // .bench-dev 的 padding

    public static readonly StyledProperty<IEnumerable?> NodesProperty =
        AvaloniaProperty.Register<BenchWires, IEnumerable?>(nameof(Nodes));

    static BenchWires() => AffectsRender<BenchWires>(NodesProperty);

    public IEnumerable? Nodes
    {
        get => GetValue(NodesProperty);
        set => SetValue(NodesProperty, value);
    }

    private static readonly Color[] Palette =
    {
        Color.Parse("#2f7ed8"), Color.Parse("#2aa87a"), Color.Parse("#c9772b"), Color.Parse("#8a63d2")
    };

    public override void Render(DrawingContext ctx)
    {
        if (Nodes is null) return;
        var nodes = Nodes.OfType<DeviceNodeViewModel>().ToList();
        if (nodes.Count == 0) return;

        // 通道号 → 孔位中心
        var wells = new Dictionary<int, Point>();
        foreach (var n in nodes)
        {
            if (n.ArtKey != "reactor2") continue;
            var s = n.Width / ArtVw;
            for (var i = 0; i < n.Channels.Count && i < 2; i++)
                wells[n.Channels[i]] = new Point(
                    n.X + NodePad + (i == 0 ? WellX1 : WellX2) * s,
                    n.Y + NodePad + WellY * s);
        }

        foreach (var n in nodes)
        {
            if (n.ArtKey == "reactor2") continue;
            var from = new Point(n.X + NodePad + n.Width / 2, n.Y + NodePad + 10);

            foreach (var ch in n.Channels)
            {
                if (!wells.TryGetValue(ch, out var to)) continue;
                var pen = new Pen(new SolidColorBrush(Palette[(ch - 1 + 4) % 4], 0.75), 1.4)
                {
                    DashStyle = new DashStyle(new double[] { 4, 3 }, 0)
                };

                // 折线走中间高度，避免压在设备图上
                var mid = (from.Y + to.Y) / 2;
                var geo = new StreamGeometry();
                using (var g = geo.Open())
                {
                    g.BeginFigure(from, false);
                    g.CubicBezierTo(new Point(from.X, mid), new Point(to.X, mid), to);
                    g.EndFigure(false);
                }
                ctx.DrawGeometry(null, pen, geo);
                ctx.DrawEllipse(new SolidColorBrush(Palette[(ch - 1 + 4) % 4]), null, to, 2.6, 2.6);
            }
        }
    }
}
