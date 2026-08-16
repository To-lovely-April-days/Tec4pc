using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Tec.App.Services;

namespace Tec.App.Controls;

/// <summary>
/// 运行视图的台面总览（原型 renderRunDeck）：**照台面画布现在的样子画一份小的**。
///
/// 同一份设备图、同一套坐标、同一条管路走法（`BenchDock.Link` 两边共用），
/// 只是整体等比缩放塞进这一格。多出来的只有一样：孔位按通道上色，
/// 正在跑的那一路点亮辉光——这张图要回答的就是「哪个釜在跑」。
///
/// 早先这里是写死的 0.62 缩放 + 固定原点，设备摆远一点就跑到框外去了；
/// 管路也是自己另画的贝塞尔曲线，跟画布上那几根正交管子对不上。
/// 现在按包围盒自适应，画布上什么样这里就什么样。
/// </summary>
public sealed class DeckView : Control
{
    public static readonly StyledProperty<Workspace?> WorkspaceProperty =
        AvaloniaProperty.Register<DeckView, Workspace?>(nameof(Workspace));

    /// <summary>四周留一圈边，设备不贴着框。</summary>
    private const double Pad = 14;
    /// <summary>设备名那行字占的世界高度，算包围盒时留出来，免得被裁掉。</summary>
    private const double LabelRoom = 18;

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

    /// <summary>一台设备在画布上占的地方：art 外面裹一圈 NodePad，与设备卡片一致。</summary>
    private readonly record struct Node(
        string Id, string Title, string ArtKey, SvgArt? Art,
        Point Pos, double W, double H, bool IsHost, IReadOnlyList<int> Channels);

    private List<Node> Nodes(Workspace ws)
    {
        var list = new List<Node>();
        foreach (var dev in ws.Bench.Devices)
        {
            var driver = ws.Drivers.Driver(dev.DriverId);
            var key = driver?.Info.IconKey ?? "reactor2";
            var art = DeviceArtCache.Get(key);
            var w = BenchDock.DisplayWidth(key);
            var h = art is null ? w * 0.8 : w * art.ViewHeight / art.ViewWidth;
            var isHost = driver is { Info.ChannelsPerDevice: > 0 };
            var chs = isHost
                ? ws.Channels.Where(c => c.HostInstanceId == dev.InstanceId)
                             .Select(c => c.Number).OrderBy(x => x).ToList()
                : ws.Bench.Bindings.Where(b => b.DeviceId == dev.InstanceId)
                                   .Select(b => b.ChannelNumber).Distinct().OrderBy(x => x).ToList();
            list.Add(new Node(dev.InstanceId, dev.Display, key, art,
                              new Point(dev.Position.X, dev.Position.Y), w, h, isHost, chs));
        }
        return list;
    }

    public override void Render(DrawingContext ctx)
    {
        var ws = Workspace;
        if (ws is null || Bounds.Width < 8 || Bounds.Height < 8) return;

        var nodes = Nodes(ws);
        if (nodes.Count == 0) return;

        // 包围盒 → 等比缩放。**只缩不放**：台面上只有一台设备时放到满格反而失真，
        // 画布 100% 是什么大小这里就多大
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (var n in nodes)
        {
            x0 = Math.Min(x0, n.Pos.X);
            y0 = Math.Min(y0, n.Pos.Y);
            x1 = Math.Max(x1, n.Pos.X + BenchDock.NodePad * 2 + n.W);
            y1 = Math.Max(y1, n.Pos.Y + BenchDock.NodePad * 2 + n.H + LabelRoom);
        }
        var bw = Math.Max(x1 - x0, 1);
        var bh = Math.Max(y1 - y0, 1);
        var s = Math.Min(Math.Min((Bounds.Width - Pad * 2) / bw, (Bounds.Height - Pad * 2) / bh), 1);
        var ox = (Bounds.Width - bw * s) / 2 - x0 * s;
        var oy = (Bounds.Height - bh * s) / 2 - y0 * s;

        Point At(Point p) => new(ox + p.X * s, oy + p.Y * s);

        // 1. 管路压在设备下面，与画布同一个叠放顺序；画法也是画布那一份
        //    （BenchLinks.Draw），只是整体缩过、不画胶囊标签——
        //    这一格太小，几个胶囊摞在一起就糊了
        foreach (var link in BenchDock.LinksOf(ws))
        {
            var pts = BenchDock.Route(link.From, link.FromDir, link.To, link.ToDir,
                                      link.Kind == LinkKind.Probe ? 18 : 24)
                               .Select(At).ToList();
            BenchLinks.Draw(ctx, link, pts, s, labels: false);
        }

        // 2. 设备。宿主在下、探头压上面，同画布
        foreach (var n in nodes.OrderBy(n => n.IsHost ? 0 : 1))
        {
            var at = At(new Point(n.Pos.X + BenchDock.NodePad, n.Pos.Y + BenchDock.NodePad));

            Color c1 = Gray, c2 = Gray;
            bool r1 = false, r2 = false;
            if (n.IsHost)
            {
                if (n.Channels.Count > 0) { c1 = ColorOf(ws, n.Channels[0]); r1 = IsRunning(ws, n.Channels[0]); }
                if (n.Channels.Count > 1) { c2 = ColorOf(ws, n.Channels[1]); r2 = IsRunning(ws, n.Channels[1]); }
            }
            else
            {
                c1 = c2 = n.Channels.Count > 0 ? ColorOf(ws, n.Channels[0]) : ProbeGray;
            }

            if (n.Art is null)
            {
                ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#e9ecef")), null,
                    new Rect(at.X, at.Y, n.W * s, n.H * s), 2, 2);
            }
            else
            {
                using var _ = ctx.PushTransform(Matrix.CreateTranslation(at.X, at.Y));
                n.Art.Render(ctx, n.W * s / n.Art.ViewWidth, new SvgArt.Paint(c1, c2, r1, r2));
            }
        }

        // 3. 设备名。字号不跟着缩——缩到 6px 就没人看得见了，
        //    这张图是给人看「哪台在跑」的，名字得认得出
        foreach (var n in nodes)
        {
            var at = At(new Point(n.Pos.X + BenchDock.NodePad, n.Pos.Y + BenchDock.NodePad));
            var ft = new FormattedText(n.Title, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 11, new SolidColorBrush(Color.Parse("#8d8d8d")));
            ctx.DrawText(ft, new Point(at.X + n.W * s / 2 - ft.Width / 2, at.Y + n.H * s + 2));
        }
    }

    private static Color ColorOf(Workspace ws, int ch)
        => ws.ChannelOf(ch)?.Enabled == true ? Palette[(ch - 1 + 4) % 4] : Gray;

    private static bool IsRunning(Workspace ws, int ch)
        => ws.Engine.Runner(ch)?.State == Tec.Core.Records.ChannelRunState.Running;
}
