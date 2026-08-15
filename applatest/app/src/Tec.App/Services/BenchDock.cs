using Avalonia;
using Tec.Core.Benches;

using Point = Avalonia.Point;
using BPoint = Tec.Core.Benches.Point;

namespace Tec.App.Services;

/// <summary>台面上的一个可停靠位置。</summary>
public sealed record DockPort(string HostId, DockSide Side, int Slot, Point Anchor)
{
    /// <summary>这个口对应哪些通道：顶部口对一个孔位，侧面口服务宿主的全部通道。</summary>
    public IReadOnlyList<int> Channels { get; init; } = Array.Empty<int>();
    public string Label { get; init; } = "";
}

/// <summary>
/// 停靠几何：哪种设备能插哪儿、插上去以后摆在什么位置。
///
/// 反应器图 176×140（viewBox），两个孔位中心 x = 46 / 126，y = 75.5。
/// 探头从上方插进孔口，所以摆在孔位正上方、图底端往下压一截，
/// 让电极看起来伸进釜里；加料泵 / 取样 / 液相走侧面，摆在反应器左右。
/// </summary>
public static class BenchDock
{
    public const double NodePad = 7;                 // 设备节点的 padding，与视图一致
    private const double ArtVw = 176, ArtVh = 140;   // 反应器图的 viewBox
    private const double WellX1 = 46, WellX2 = 126, WellY = 75.5;

    /// <summary>吸附半径：拖到离口这么近就算插上了。</summary>
    public const double SnapRadius = 70;

    /// <summary>探头类：从上方插。其余按侧面走。</summary>
    public static bool IsProbe(string artKey)
        => artKey is "ph" or "turb" or "raman" or "ir" or "psd";

    public static DockSide DefaultSideFor(string artKey)
        => IsProbe(artKey) ? DockSide.Top : DockSide.Left;

    private static double ScaleOf(double width) => width / ArtVw;

    /// <summary>宿主上所有可停靠的口。</summary>
    public static IEnumerable<DockPort> PortsOf(string hostId, Point hostPos, double hostWidth,
                                                IReadOnlyList<int> hostChannels)
    {
        var s = ScaleOf(hostWidth);
        var h = ArtVh * s;
        var x0 = hostPos.X + NodePad;
        var y0 = hostPos.Y + NodePad;

        for (var i = 0; i < hostChannels.Count && i < 2; i++)
        {
            var wx = x0 + (i == 0 ? WellX1 : WellX2) * s;
            yield return new DockPort(hostId, DockSide.Top, i, new Point(wx, y0 + WellY * s))
            {
                Channels = new[] { hostChannels[i] },
                Label = $"{(i == 0 ? "A" : "B")} 孔 · CH{hostChannels[i]}"
            };
        }

        yield return new DockPort(hostId, DockSide.Left, 0, new Point(x0 - 6, y0 + h / 2))
        {
            Channels = hostChannels,
            Label = "左侧接口"
        };
        yield return new DockPort(hostId, DockSide.Right, 0, new Point(x0 + ArtVw * s + 6, y0 + h / 2))
        {
            Channels = hostChannels,
            Label = "右侧接口"
        };
    }

    /// <summary>这种设备能不能插这个口。探头只走顶部，加料 / 取样 / 液相只走侧面。</summary>
    public static bool Accepts(string artKey, DockSide side)
        => IsProbe(artKey) ? side == DockSide.Top : side is DockSide.Left or DockSide.Right;

    /// <summary>
    /// 插上以后设备摆在哪儿。返回的是节点左上角坐标（含 padding 的那个外框）。
    /// 顶部：孔位正上方，往下压 22px 让电极伸进釜口；侧面：贴着反应器外沿。
    /// </summary>
    public static Point Place(DockPort port, double devWidth, double devHeight, double hostWidth)
    {
        var w = devWidth + NodePad * 2;
        var h = devHeight + NodePad * 2;
        return port.Side switch
        {
            DockSide.Top => new Point(port.Anchor.X - w / 2, port.Anchor.Y - h + 22),
            DockSide.Left => new Point(port.Anchor.X - w, port.Anchor.Y - h / 2),
            _ => new Point(port.Anchor.X, port.Anchor.Y - h / 2)
        };
    }

    /// <summary>离拖动点最近的、能接受这种设备的口。够不着就返回 null。</summary>
    public static DockPort? Nearest(IEnumerable<DockPort> ports, string artKey, Point at,
                                    double radius = SnapRadius)
    {
        DockPort? best = null;
        var bestD = double.MaxValue;
        foreach (var p in ports)
        {
            if (!Accepts(artKey, p.Side)) continue;
            var d = Math.Sqrt(Math.Pow(p.Anchor.X - at.X, 2) + Math.Pow(p.Anchor.Y - at.Y, 2));
            if (d < bestD) { bestD = d; best = p; }
        }
        return bestD <= radius ? best : null;
    }
}
