namespace Tec.Core.Benches;

/// <summary>
/// 早期版本的台面上摆的是仿真设备（tec.reactor.rd105 那一族），属性栏里还有个
/// 「模拟」开关。程序里已经没有仿真：老台面读进来时按这张表把仿真设备换成
/// 真机孪生；没有真机形态的（加料泵、浊度 / 拉曼 / 红外探头）从台面上摘掉。
///
/// 换了什么、摘了什么都要说出来——悄悄改操作人存下来的东西不行（跟老指令
/// 翻译 RecipeMigration 同一条规矩）。
/// </summary>
public static class LegacyDevices
{
    /// <summary>仿真驱动号 → 真机驱动号。</summary>
    public static readonly IReadOnlyDictionary<string, string> Twins =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["tec.reactor.rd105"] = "tec.reactor.duo",
            ["tec.probe.tr"] = "tec.probe.tr.yudian",
            ["tec.probe.ph"] = "tec.probe.ph.yudian"
        };

    /// <summary>只有仿真形态、没有真机驱动的那几件。</summary>
    public static readonly IReadOnlySet<string> Retired =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "tec.dosing.pump", "tec.probe.turbidity", "tec.probe.raman", "tec.probe.ir"
        };

    /// <summary>
    /// 就地改台面，返回说明（一件事一条；什么都没动就是空表）。
    /// 仿真反应器上填的「端口」搬到真机的「RD105串口」里——那是操作人选好的口，
    /// 不该因为换了驱动就丢掉；波特率不搬（仿真那份默认 115200，RD105 出厂 38400，
    /// 搬过去反而连不上）。
    /// </summary>
    public static IReadOnlyList<string> Migrate(Bench bench)
    {
        var notes = new List<string>();

        foreach (var d in bench.Devices)
        {
            if (!Twins.TryGetValue(d.DriverId, out var real)) continue;
            var note = $"{d.Display} 是早期版本的仿真设备（{d.DriverId}），已改按真机驱动（{real}）打开";
            if (d.DriverId == "tec.reactor.rd105" && d.Connection.Has("端口"))
            {
                d.Connection["RD105串口"] = d.Connection.Str("端口");
                note += $"，串口沿用 {d.Connection.Str("端口")}";
            }
            notes.Add(note + "；其余连接参数请核对");
            d.DriverId = real;
        }

        var gone = bench.Devices.Where(d => Retired.Contains(d.DriverId)).ToList();
        foreach (var d in gone)
        {
            notes.Add($"{d.Display}（{d.DriverId}）是仿真设备，程序里已没有仿真，已从台面移除");
            bench.Devices.Remove(d);
            bench.Bindings.RemoveAll(b => b.DeviceId == d.InstanceId);
            foreach (var child in bench.Devices.Where(c => c.DockHostId == d.InstanceId))
            {
                child.DockHostId = null;
                child.DockAnchor = null;
                child.Dock = DockSide.None;
            }
        }

        return notes;
    }
}
