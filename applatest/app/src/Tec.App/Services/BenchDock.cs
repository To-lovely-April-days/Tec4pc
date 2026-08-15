using Avalonia;
using Tec.Core.Benches;

using Point = Avalonia.Point;

namespace Tec.App.Services;

/// <summary>接口种类：上方管口 / 侧口 / 控制总线。</summary>
public enum PortKind { Top, Side, Bus }

/// <summary>管路类型，决定连线怎么画（原型 LT）。</summary>
public enum LinkKind { Probe, Feed, Sample, Signal }

/// <summary>设备身上的插头：自身图坐标里的一点 + 出线方向。</summary>
public readonly record struct Plug(double X, double Y, string Dir);

/// <summary>
/// 反应器上的一个具名接口（原型 anchors）。一个接口同时只接一台设备。
/// </summary>
public sealed record Anchor(string Id, PortKind Kind, double X, double Y, string Dir, string Label)
{
    /// <summary>属于哪个孔位（0 = A 孔 / 通道 1，1 = B 孔 / 通道 2）。总线不属于任何孔位。</summary>
    public int Slot { get; init; }
    /// <summary>侧口在左还是右。</summary>
    public string? Side { get; init; }
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
}

/// <summary>
/// 停靠几何：反应器开哪些口、什么设备能插、插上以后摆在哪儿、管路怎么走。
/// 数据照 tecstudioworkcell 原型的 anchors / plug 逐条搬过来（同一套设备图，
/// 坐标可以直接用）。
/// </summary>
public static class BenchDock
{
    public const double NodePad = 7;
    private const double ArtVw = 176, ArtVh = 140;

    /// <summary>
    /// 设备在画布上的显示宽度。反应器画得比原来大，是为了让釜体里的液体和
    /// 搅拌桨看得清——接口坐标按 ArtVw 等比换算，这里改宽度不会让管路错位。
    /// 三处（画布节点、拖拽幽灵、卡片缩略图）从前各写各的，现在只此一份。
    /// </summary>
    public static double DisplayWidth(string artKey) => artKey == "reactor2" ? 240 : 120;

    /// <summary>反应器的 11 个接口：每通道 3 个上方管口，左右各 2 个侧口，1 个总线口。</summary>
    public static readonly IReadOnlyList<Anchor> Anchors = new[]
    {
        new Anchor("T1a", PortKind.Top, 44.8, 9.6, "up", "通道1 · 管口 A") { Slot = 0 },
        new Anchor("T1b", PortKind.Top, 57.0, 9.2, "up", "通道1 · 管口 B") { Slot = 0 },
        new Anchor("T1c", PortKind.Top, 69.9, 4.6, "up", "通道1 · 主口") { Slot = 0 },
        new Anchor("T2a", PortKind.Top, 102.1, 4.6, "up", "通道2 · 主口") { Slot = 1 },
        new Anchor("T2b", PortKind.Top, 115.0, 9.2, "up", "通道2 · 管口 B") { Slot = 1 },
        new Anchor("T2c", PortKind.Top, 127.2, 9.6, "up", "通道2 · 管口 A") { Slot = 1 },
        new Anchor("L1", PortKind.Side, 4.9, 76, "left", "左侧口 · 通道1") { Slot = 0, Side = "L" },
        new Anchor("L2", PortKind.Side, 4.9, 99, "left", "左侧口 · 通道2") { Slot = 1, Side = "L" },
        new Anchor("R1", PortKind.Side, 168.1, 76, "right", "右侧口 · 通道1") { Slot = 0, Side = "R" },
        new Anchor("R2", PortKind.Side, 168.1, 99, "right", "右侧口 · 通道2") { Slot = 1, Side = "R" },
        new Anchor("BUS", PortKind.Bus, 86.5, 134.6, "down", "控制总线")
    };

    /// <summary>各设备的插头位置与出线方向（原型 plug / plugL）。</summary>
    private static readonly Dictionary<string, (Plug Plug, Plug? Left, PortKind Attach, LinkKind Link)> Defs =
        new(StringComparer.Ordinal)
        {
            ["ph"] = (new Plug(90, 128.2, "down"), null, PortKind.Top, LinkKind.Probe),
            ["turb"] = (new Plug(80, 125.8, "down"), null, PortKind.Top, LinkKind.Probe),
            ["raman"] = (new Plug(102, 131.8, "down"), null, PortKind.Top, LinkKind.Probe),
            ["ir"] = (new Plug(104, 131.8, "down"), null, PortKind.Top, LinkKind.Probe),
            ["psd"] = (new Plug(104, 130.8, "down"), null, PortKind.Top, LinkKind.Probe),
            ["pump"] = (new Plug(46, 124.6, "down"), null, PortKind.Side, LinkKind.Feed),
            ["sampler"] = (new Plug(30, 136, "down"), new Plug(138, 130, "right"), PortKind.Side, LinkKind.Sample),
            ["hplc"] = (new Plug(9, 136, "left"), new Plug(127, 136, "right"), PortKind.Side, LinkKind.Sample),
            ["host"] = (new Plug(70, 8, "up"), null, PortKind.Bus, LinkKind.Signal)
        };

    public static bool IsHost(string artKey) => artKey == "reactor2";
    public static bool Known(string artKey) => Defs.ContainsKey(artKey);
    public static PortKind AttachOf(string artKey)
        => Defs.TryGetValue(artKey, out var d) ? d.Attach : PortKind.Side;
    public static LinkKind LinkOf(string artKey)
        => Defs.TryGetValue(artKey, out var d) ? d.Link : LinkKind.Signal;

    /// <summary>取插头。停在左侧且该设备有左侧插头时用左侧那个（取样、液相两侧走线不同）。</summary>
    public static Plug PlugOf(string artKey, string? side)
    {
        if (!Defs.TryGetValue(artKey, out var d)) return new Plug(0, 0, "down");
        return side == "L" && d.Left is { } l ? l : d.Plug;
    }

    private static double ScaleOf(double width) => width / ArtVw;

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

    public static bool Accepts(string artKey, Anchor a) => AttachOf(artKey) == a.Kind;

    /// <summary>
    /// 管路从设备哪一侧出去。探头的电极永远朝下，所以固定向下；加料 / 取样 /
    /// 液相这类设备摆在哪儿都可能，按接口相对它的方位取主轴方向——不这样的话
    /// 泵放在反应器左上方，管路也会先往下绕一圈再拐回来。
    /// </summary>
    public static string ExitDir(string artKey, Point from, Point to)
    {
        if (AttachOf(artKey) == PortKind.Top) return "down";
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        return Math.Abs(dx) >= Math.Abs(dy) ? (dx > 0 ? "right" : "left") : (dy > 0 ? "down" : "up");
    }

    /// <summary>
    /// 选接口（原型 pickAnchor）：探头只比上方管口的横向距离；侧接设备先按落点
    /// 判左右，再在该侧取最近的；该侧占满就退到另一侧。已被占用的口不参与。
    /// </summary>
    /// <remarks>
    /// 不设距离门限：放在画布哪儿都连得上，管路自己拉过去（原型 pickAnchor 同样
    /// 不看距离）。接口全被占了才连不上。
    /// </remarks>
    public static Anchor? Pick(string artKey, Point hostPos, double hostWidth, Point drop,
                               ISet<string> taken, string? keep = null)
    {
        if (!Known(artKey)) return null;
        var attach = AttachOf(artKey);
        var side = drop.X < hostPos.X + NodePad + hostWidth / 2 ? "L" : "R";

        bool Free(Anchor a) => a.Id == keep || !taken.Contains(a.Id);

        var cands = Anchors.Where(a => Free(a) && a.Kind == attach &&
                                       (attach != PortKind.Side || a.Side == side)).ToList();
        if (cands.Count == 0 && attach == PortKind.Side)
            cands = Anchors.Where(a => a.Kind == PortKind.Side && Free(a)).ToList();
        if (cands.Count == 0) return null;

        Anchor best = cands[0];
        var bd = double.MaxValue;
        foreach (var a in cands)
        {
            var w = AnchorWorld(hostPos, hostWidth, a);
            var dist = attach == PortKind.Top
                ? Math.Abs(w.X - drop.X)
                : Math.Abs(w.Y - drop.Y) * 1.4 + Math.Abs(w.X - drop.X) * 0.2;
            if (dist < bd) { bd = dist; best = a; }
        }
        return best;
    }

    // ── 正交折线 + 圆角（原型 orth / roundPath）─────────────────────
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
