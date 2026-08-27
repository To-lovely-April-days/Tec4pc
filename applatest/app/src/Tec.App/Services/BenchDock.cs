using Avalonia;
using Tec.Core.Benches;

using Point = Avalonia.Point;

namespace Tec.App.Services;

/// <summary>接口种类：上方管口 / 侧口 / 控制总线。</summary>
public enum PortKind { Top, Side, Bus }

/// <summary>管路类型，决定连线怎么画。</summary>
public enum LinkKind { Probe, Feed, Sample, Signal }

/// <summary>设备身上的插头：自身图坐标里的一点 + 出线方向。</summary>
public readonly record struct Plug(double X, double Y, string Dir);

/// <summary>
/// 反应器上的一个具名接口。一个接口同时只接一台设备。
/// </summary>
public sealed record Anchor(string Id, PortKind Kind, double X, double Y, string Dir, string Label)
{
    /// <summary>属于哪个工位（0 = 工位 1 / 通道 1，1 = 工位 2 / 通道 2）。</summary>
    public int Slot { get; init; }
    /// <summary>侧口在左还是右。</summary>
    public string? Side { get; init; }
    /// <summary>这个口收什么设备：tr / ph / feed。同为顶口，Tr 探头不能插进 pH 口。</summary>
    public string Accept { get; init; } = "";
}

/// <summary>台面上一条已接好的管路。</summary>
public sealed record BenchLink(string DeviceId, string HostId, string AnchorId, LinkKind Kind)
{
    public required Point From { get; init; }
    public required string FromDir { get; init; }
    public required Point To { get; init; }
    public required string ToDir { get; init; }
    public int Channel { get; init; }
    public string Label { get; init; } = "";
    /// <summary>主机图单位 → 画布像素的比例。加料管的粗细、加料口的大小按它缩，
    /// 跟主机永远同一个比例——演示图里管宽 5 是主机坐标系里的 5。</summary>
    public double Scale { get; init; } = 1;

    /// <summary>泵在跑：管内画流动虚线（演示 .flow）。</summary>
    public bool Flow { get; init; }
    /// <summary>流动速度（主机图单位/秒）。演示一圈 32 单位走 (1.6 − 0.24×速率) 秒。</summary>
    public double FlowSpeed { get; init; }
}

/// <summary>
/// 停靠几何：主机开哪些口、什么设备能插、插上以后摆在哪儿、管路怎么走。
/// 坐标全部来自 HT-RS2 交互演示（machine / probeTr / probePh / dosePort / tube），
/// 设备图与它是同一份几何，数值可以直接抄。
/// </summary>
public static class BenchDock
{
    public const double NodePad = 7;

    /// <summary>主机（rd105.svg）的 viewBox 尺寸。接口坐标都写在这个坐标系里。</summary>
    public const double MachineVw = 560, MachineVh = 548;
    /// <summary>两个工位的釜心，与演示的 CX=[180,380] 同值。工位间距 200。</summary>
    private const double Cx0 = 180, Cx1 = 380;

    /// <summary>
    /// 设备在画布上的显示宽度。三处（画布节点、拖拽幽灵、卡片缩略图）只此一份。
    /// 主机 300：插入形态（*-in）与读数标签都按 300/560 这一个比例缩，
    /// 探头 18 / 泵 110 也是照同一比例给的——四件东西摆在一起大小关系
    /// 跟 parts_current 那张总图一致。
    /// </summary>
    public static double DisplayWidth(string artKey) => artKey switch
    {
        "rd105" => 300,
        "feedpump" => 110,
        "trprobe" or "phel" => 18,
        // 插入形态的宽 = 它的 viewBox 宽（50）按主机比例缩，落到主机上不差一个像素
        "trprobe-in" or "phel-in" => 300.0 / MachineVw * 50,
        _ => 60
    };

    /// <summary>
    /// 主机上的六个口：每个工位一个 Tr 插口、一个 pH 插口、一个加料口。
    /// Tr / pH 的插点取演示插入件的旋转中心（cx∓18, 162）；
    /// 加料口取 dosePort 那块小方块的中心——工位 1 开在釜盖左伸出端、
    /// 工位 2 开在右伸出端，跟演示 dosePort(CX[0],-1) / (CX[1],+1) 一致。
    /// </summary>
    public static readonly IReadOnlyList<Anchor> Anchors = new[]
    {
        new Anchor("S1TR", PortKind.Top, Cx0 - 18, 163, "up", "工位1 · Tr") { Slot = 0, Accept = "tr" },
        new Anchor("S1PH", PortKind.Top, Cx0 + 18, 163, "up", "工位1 · pH") { Slot = 0, Accept = "ph" },
        new Anchor("S1FD", PortKind.Side, Cx0 - 57, 171, "left", "工位1 · 加料口") { Slot = 0, Side = "L", Accept = "feed" },
        new Anchor("S2TR", PortKind.Top, Cx1 - 18, 163, "up", "工位2 · Tr") { Slot = 1, Accept = "tr" },
        new Anchor("S2PH", PortKind.Top, Cx1 + 18, 163, "up", "工位2 · pH") { Slot = 1, Accept = "ph" },
        new Anchor("S2FD", PortKind.Side, Cx1 + 57, 171, "right", "工位2 · 加料口") { Slot = 1, Side = "R", Accept = "feed" }
    };

    public static Anchor? AnchorById(string? id)
        => id is null ? null : Anchors.FirstOrDefault(a => a.Id == id);

    /// <summary>
    /// 各设备的插头（相对各自 viewBox 原点）与它要找的口。
    /// 探头的插头是尖端；泵的插头是滚轮出口（演示 g-tube 就从 (190,210) 出发），
    /// 出线方向恒为向上——演示里管子先竖着升到釜盖高度再拐过去。
    /// </summary>
    private static readonly Dictionary<string, (Plug Plug, string Accept, LinkKind Link)> Defs =
        new(StringComparer.Ordinal)
        {
            ["trprobe"] = (new Plug(16, 293, "down"), "tr", LinkKind.Probe),
            ["trprobe-in"] = (new Plug(36, 138, "down"), "tr", LinkKind.Probe),
            ["phel"] = (new Plug(17, 291, "down"), "ph", LinkKind.Probe),
            ["phel-in"] = (new Plug(14, 138, "down"), "ph", LinkKind.Probe),
            ["feedpump"] = (new Plug(176, 164, "up"), "feed", LinkKind.Feed)
        };

    public static bool IsHost(string artKey) => artKey == "rd105";
    public static bool Known(string artKey) => Defs.ContainsKey(artKey);
    public static string? AcceptOf(string artKey)
        => Defs.TryGetValue(artKey, out var d) ? d.Accept : null;
    public static LinkKind LinkOf(string artKey)
        => Defs.TryGetValue(artKey, out var d) ? d.Link : LinkKind.Signal;

    public static Plug PlugOf(string artKey, string? side)
        => Defs.TryGetValue(artKey, out var d) ? d.Plug : new Plug(0, 0, "down");

    private static double ScaleOf(double hostWidth) => hostWidth / MachineVw;

    /// <summary>接口在画布上的位置。</summary>
    public static Point AnchorWorld(Point hostPos, double hostWidth, Anchor a)
    {
        var s = ScaleOf(hostWidth);
        return new Point(hostPos.X + NodePad + a.X * s, hostPos.Y + NodePad + a.Y * s);
    }

    public static Point PlugWorld(Point devPos, double devWidth, string artKey, string? side)
    {
        var art = Controls.DeviceArtCache.Get(artKey);
        var s = art is null ? 1 : devWidth / art.ViewWidth;
        var p = PlugOf(artKey, side);
        return new Point(devPos.X + NodePad + p.X * s, devPos.Y + NodePad + p.Y * s);
    }

    public static bool Accepts(string artKey, Anchor a) => AcceptOf(artKey) == a.Accept;

    /// <summary>
    /// 探头插上工位后的**独立插入图**（trprobe-in / phel-in）。
    /// 这两张图沿用主机的坐标系，所以设备一插上就把节点吸到
    /// <see cref="SnapPosition"/> 算出的位置，插入件正好落在演示画的那个地方。
    /// 泵不换图也不吸附——它留在放手的地方，靠管子连过去。
    /// </summary>
    public static string? InsertArtFor(string artKey, string? anchorId)
    {
        if (AnchorById(anchorId) is null) return null;
        return artKey switch
        {
            "trprobe" => "trprobe-in",
            "phel" => "phel-in",
            _ => null
        };
    }

    /// <summary>
    /// 插入图的节点位置：插入图的 viewBox 写的就是主机坐标（工位 1），
    /// 所以位置 = 主机位置 + viewBox 原点 × 主机比例；工位 2 整体右移 200。
    /// 节点内边距（NodePad）两边相同，正好抵消。
    /// </summary>
    public static Point SnapPosition(string insertArtKey, Anchor a, Point hostPos, double hostWidth)
    {
        var art = Controls.DeviceArtCache.Get(insertArtKey);
        var s = ScaleOf(hostWidth);
        var dx = a.Slot == 1 ? Cx1 - Cx0 : 0;
        var vx = art?.ViewX ?? 0;
        var vy = art?.ViewY ?? 0;
        return new Point(hostPos.X + (vx + dx) * s, hostPos.Y + vy * s);
    }

    /// <summary>
    /// 泵控制小窗肘形引线的落点：泵机身顶沿中部。演示 PTGT 取的是泵图 (150,154)，
    /// 换算到 feedpump.svg 的 viewBox（原点 14,46）就是 (136,108)，再乘泵自己的比例。
    /// </summary>
    public static Point PumpPanelTarget(Point pumpPos, double pumpWidth)
    {
        var art = Controls.DeviceArtCache.Get("feedpump");
        var s = art is null ? 1 : pumpWidth / art.ViewWidth;
        return new Point(pumpPos.X + NodePad + 136 * s, pumpPos.Y + NodePad + 108 * s);
    }

    /// <summary>算出一条管路的几何。台面画布与运行页的台面总览共用这一个。</summary>
    public static BenchLink Link(string artKey, Point pos, double width, string? side,
                                 string deviceId, string hostId, Point hostPos, double hostWidth,
                                 IReadOnlyList<int> hostChannels, Anchor a)
    {
        var to = AnchorWorld(hostPos, hostWidth, a);
        var from = PlugWorld(pos, width, artKey, side);
        return new BenchLink(deviceId, hostId, a.Id, LinkOf(artKey))
        {
            From = from,
            FromDir = ExitDir(artKey, from, to),
            To = to,
            ToDir = a.Dir,
            Channel = hostChannels.ElementAtOrDefault(a.Slot),
            Label = a.Label,
            Scale = ScaleOf(hostWidth)
        };
    }

    /// <summary>
    /// 台面上现有的全部管路。运行页那张总览直接照这份画。
    /// Tr / pH 是插进工位的，没有管子——插入件本身就画在工位上，这里跳过。
    /// </summary>
    public static List<BenchLink> LinksOf(Workspace ws)
    {
        var links = new List<BenchLink>();
        foreach (var dev in ws.Bench.Devices)
        {
            if (dev.DockAnchor is null || dev.DockHostId is null) continue;
            var host = ws.Bench.Devices.FirstOrDefault(d => d.InstanceId == dev.DockHostId);
            if (host is null) continue;
            var a = AnchorById(dev.DockAnchor);
            if (a is null || a.Accept is "tr" or "ph") continue;

            var artKey = ws.Drivers.Driver(dev.DriverId)?.Info.IconKey ?? "rd105";
            var hostKey = ws.Drivers.Driver(host.DriverId)?.Info.IconKey ?? "rd105";
            var hostChs = ws.Channels.Where(c => c.HostInstanceId == host.InstanceId)
                                     .Select(c => c.Number).OrderBy(x => x).ToList();

            links.Add(Link(artKey, new Point(dev.Position.X, dev.Position.Y), DisplayWidth(artKey),
                           dev.DockSideTag, dev.InstanceId, host.InstanceId,
                           new Point(host.Position.X, host.Position.Y), DisplayWidth(hostKey),
                           hostChs, a));
        }
        return links;
    }

    /// <summary>
    /// 管路从设备哪一侧出去。探头尖端朝下、泵的出口管先竖直向上
    /// （演示的 g-tube 就是先升到釜盖高度再拐），方向都写在插头定义里。
    /// </summary>
    public static string ExitDir(string artKey, Point from, Point to) => PlugOf(artKey, null).Dir;

    /// <summary>
    /// 探头插口的吸附半径（画布像素）。Tr / pH 是**插进**工位的：得把它拖到
    /// 机器跟前才算要插，拖到空地上就是拔下来放着——没有这个门限的话，
    /// 插上的探头永远拔不下来（放到哪儿都被吸回去）。
    /// 150 约等于半台主机宽，跟着人的手感给的，不是量出来的。
    /// 加料口**不设门限**：泵靠管子连，摆多远管子拉多长（演示里泵就在画面边上）。
    /// </summary>
    private const double ProbeSnapRange = 150;

    /// <summary>
    /// 选接口：在**收这类设备**且空着的口里取离插头最近的那个——
    /// 「拖过去，吸到最近的工位」就是这一条在管。
    /// 同类接口全被占了连不上；探头离得太远（见 ProbeSnapRange）也不吸。
    /// </summary>
    public static Anchor? Pick(string artKey, Point hostPos, double hostWidth, Point drop,
                               ISet<string> taken, string? keep = null)
    {
        var accept = AcceptOf(artKey);
        if (accept is null) return null;

        Anchor? best = null;
        var bd = double.MaxValue;
        foreach (var a in Anchors)
        {
            if (a.Accept != accept) continue;
            if (a.Id != keep && taken.Contains(a.Id)) continue;
            var w = AnchorWorld(hostPos, hostWidth, a);
            var d = (w.X - drop.X) * (w.X - drop.X) + (w.Y - drop.Y) * (w.Y - drop.Y);
            if (d < bd) { bd = d; best = a; }
        }
        if (best is not null && accept is "tr" or "ph" && bd > ProbeSnapRange * ProbeSnapRange)
            return null;
        return best;
    }

    // ── 正交折线 + 圆角 ─────────────────────────────────────────────
    private static bool IsV(string dir) => dir is "up" or "down";

    private static Point StepOut(Point p, string dir, double n) => dir switch
    {
        "left" => new Point(p.X - n, p.Y),
        "right" => new Point(p.X + n, p.Y),
        "up" => new Point(p.X, p.Y - n),
        _ => new Point(p.X, p.Y + n)
    };

    /// <summary>两端各先直出一小段，再用一条中线拐过去——管路不会斜着穿设备。</summary>
    public static List<Point> Route(Point a, string ad, Point b, string bd, double stub = 24)
    {
        var A = StepOut(a, ad, stub);
        var B = StepOut(b, bd, stub);
        var pts = new List<Point> { a, A };

        if (IsV(ad) && IsV(bd))
        {
            if (Math.Abs(A.X - B.X) > 0.5)
            {
                var my = (A.Y + B.Y) / 2;
                pts.Add(new Point(A.X, my));
                pts.Add(new Point(B.X, my));
            }
        }
        else if (!IsV(ad) && !IsV(bd))
        {
            if (Math.Abs(A.Y - B.Y) > 0.5)
            {
                var mx = (A.X + B.X) / 2;
                pts.Add(new Point(mx, A.Y));
                pts.Add(new Point(mx, B.Y));
            }
        }
        else if (IsV(ad)) pts.Add(new Point(A.X, B.Y));
        else pts.Add(new Point(B.X, A.Y));

        pts.Add(B);
        pts.Add(b);

        var outp = new List<Point>();
        foreach (var p in pts)
        {
            if (outp.Count == 0 ||
                Math.Abs(outp[^1].X - p.X) > 0.4 || Math.Abs(outp[^1].Y - p.Y) > 0.4)
                outp.Add(p);
        }
        return outp;
    }
}
