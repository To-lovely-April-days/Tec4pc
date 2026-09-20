using System.Text.RegularExpressions;

namespace Tec.Driver.Abi;

/// <summary>这台机器上的一个串口：端口名 + 设备名（拿不到名字就是 null）。</summary>
public sealed record SerialPortInfo(string Name, string? Friendly);

/// <summary>
/// 把扫到的串口摆成下拉项。**怎么扫**是宿主的事（要碰系统 API），
/// 摆成什么样是这里的事——纯逻辑，不插硬件也回归得了。
/// </summary>
public static class SerialPortChoices
{
    /// <summary>
    /// <paramref name="current"/> 是这台设备**已经存着**的口：它不在当前列表里
    /// （拔了 / 换了机器 / 先在办公室配好台面再去现场）也必须留在下拉里，
    /// 否则一打开属性栏，存好的口就被空下拉吞掉了——标一句「当前不在线」说清楚。
    ///
    /// 顺序：先按名字里的字母部分（COM / /dev/ttyUSB / /dev/ttyS 各归各的），
    /// 再按**数字**——COM3 要在 COM10 前面，照整串字符串排会倒过来。
    /// 一个口都没扫到就是空表——不摆一份假的 COM1~COM6 让人选了也连不上。
    /// </summary>
    public static IReadOnlyList<ChoiceOption> Build(IEnumerable<SerialPortInfo> ports, string? current)
    {
        var list = ports
            .Where(p => !string.IsNullOrWhiteSpace(p.Name))
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(p => PrefixOf(p.Name), StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => NumberIn(p.Name))
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(p => new ChoiceOption(
                p.Name,
                string.IsNullOrWhiteSpace(p.Friendly) ? p.Name : $"{p.Name} · {p.Friendly}"))
            .ToList();

        if (!string.IsNullOrWhiteSpace(current)
            && !list.Any(o => string.Equals(o.Value, current, StringComparison.OrdinalIgnoreCase)))
            list.Insert(0, new ChoiceOption(current!, $"{current} · 当前不在线"));

        return list;
    }

    /// <summary>COM16 → 16；排序用。认不出数字的排到最后。</summary>
    public static int NumberIn(string name)
    {
        var m = Regex.Match(name, @"(\d+)\s*$");
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : int.MaxValue;
    }

    /// <summary>COM16 → 「COM」，/dev/ttyUSB1 → 「/dev/ttyUSB」。同一族的排在一起。</summary>
    private static string PrefixOf(string name)
        => Regex.Replace(name, @"\d+\s*$", "");
}
