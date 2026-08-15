using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Tec.App.Services;

namespace Tec.App.Controls;

/// <summary>
/// 运行视图的台面总览（原型 renderRunDeck）：
/// 台面设备按 0.62 缩放整体画进来，孔位上色、运行辉光、设备编号标签、绑定连线。
/// 与台面画布共用同一份设备图（DeviceArtCache），只是缩放不同。
/// </summary>
public sealed class DeckView : Control
{
    public static readonly StyledProperty<Workspace?> WorkspaceProperty =
        AvaloniaProperty.Register<DeckView, Workspace?>(nameof(Workspace));

    private const double Scale = 0.62, Ox = 10, Oy = 8;
    private const double NodePad = 7;            // 原型 DEVPAD
    private const double WellX1 = 46, WellX2 = 126, WellY = 75.5, ReactorVw = 176;

    static DeckView() => AffectsRender<DeckView>(WorkspaceProperty);

    public Workspace? Workspace
    {
        get => GetValue(WorkspaceProperty);
        set => SetValue(WorkspaceProperty, value);
    }

    public void Refresh() => InvalidateVisual();

    private static readonly Color[] Palette =
    {
        Color.Parse("#2f7ed8"), Color.Parse("#2aa87a"), Color.Parse("#c9772b"), Color.Parse("#8a63d2")
    };
    private static readonly Color Gray = Color.Parse("#c2c2c2");
    private static readonly Color ProbeGray = Color.Parse("#9aa0a5");

    public override void Render(DrawingContext ctx)
    {
        var ws = Workspace;
        if (ws is null) return;

        var wells = new Dictionary<int, Point>();

        foreach (var dev in ws.Bench.Devices)
        {
            var driver = ws.Drivers.Driver(dev.DriverId);
            var key = driver?.Info.IconKey ?? "reactor2";
            if (DeviceArtCache.Get(key) is not { } art) continue;

            var isHost = driver is { Info.ChannelsPerDevice: > 0 };
            var chs = isHost
                ? ws.Channels.Where(c => c.HostInstanceId == dev.InstanceId).Select(c => c.Number).ToList()
                : ws.Bench.Bindings.Where(b => b.DeviceId == dev.InstanceId)
                                   .Select(b => b.ChannelNumber).Distinct().OrderBy(x => x).ToList();

            // 与台面画布同一套显示宽度 → viewBox 缩放。运行页的台面总览得跟画布对得上
            var wpx = Services.BenchDock.DisplayWidth(key);
            var k = Scale * wpx / art.ViewWidth;
            var x = dev.Position.X * Scale + Ox;
            var y = dev.Position.Y * Scale + Oy;

            Color c1 = Gray, c2 = Gray;
            bool r1 = false, r2 = false;
            if (isHost)
            {
                if (chs.Count > 0) { c1 = ColorOf(ws, chs[0]); r1 = IsRunning(ws, chs[0]); }
                if (chs.Count > 1) { c2 = ColorOf(ws, chs[1]); r2 = IsRunning(ws, chs[1]); }
                for (var i = 0; i < chs.Count && i < 2; i++)
                    wells[chs[i]] = new Point(
                        x + (i == 0 ? WellX1 : WellX2) / ReactorVw * wpx * Scale,
                        y + WellY / ReactorVw * wpx * Scale);
            }
            else
            {
                c1 = c2 = chs.Count > 0 ? ColorOf(ws, chs[0]) : ProbeGray;
            }

            using (ctx.PushTransform(Matrix.CreateTranslation(x, y)))
                art.Render(ctx, k, new SvgArt.Paint(c1, c2, r1, r2));

            // 设备编号标签（13px 灰字，居中在图形下方）
            var label = new FormattedText(dev.InstanceId, System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 13,
                new SolidColorBrush(Color.Parse("#8d8d8d")));
            ctx.DrawText(label, new Point(
                x + art.ViewWidth * k / 2 - label.Width / 2,
                y + art.ViewHeight * k + 2));
        }

        // 绑定连线：探头顶部 → 孔位中心，通道色虚线（与台面画布同款）
        foreach (var dev in ws.Bench.Devices)
        {
            var driver = ws.Drivers.Driver(dev.DriverId);
            if (driver is not { Info.ChannelsPerDevice: 0 }) continue;

            var x = dev.Position.X * Scale + Ox;
            var y = dev.Position.Y * Scale + Oy;
            var from = new Point(x + 120 * Scale / 2, y + NodePad * Scale);

            foreach (var chn in ws.Bench.Bindings.Where(b => b.DeviceId == dev.InstanceId)
                                                 .Select(b => b.ChannelNumber).Distinct())
            {
                if (!wells.TryGetValue(chn, out var to)) continue;
                var color = Palette[(chn - 1 + 4) % 4];
                var pen = new Pen(new SolidColorBrush(color, 0.75), 1.2)
                { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) };
                var mid = (from.Y + to.Y) / 2;
                var geo = new StreamGeometry();
                using (var g = geo.Open())
                {
                    g.BeginFigure(from, false);
                    g.CubicBezierTo(new Point(from.X, mid), new Point(to.X, mid), to);
                    g.EndFigure(false);
                }
                ctx.DrawGeometry(null, pen, geo);
                ctx.DrawEllipse(new SolidColorBrush(color), null, to, 2.4, 2.4);
            }
        }
    }

    private static Color ColorOf(Workspace ws, int ch)
        => ws.ChannelOf(ch)?.Enabled == true ? Palette[(ch - 1 + 4) % 4] : Gray;

    private static bool IsRunning(Workspace ws, int ch)
        => ws.Engine.Runner(ch)?.State == Tec.Core.Records.ChannelRunState.Running;
}
