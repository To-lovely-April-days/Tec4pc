using Tec.Driver.Abi;

namespace Tec.Core.Benches;

/// <summary>
/// 【本地改动】开会话时谁能跟谁同时连。
///
/// 从前台面上的设备是一台一台顺序连的：主机的温控器不通时要换着试好几档、十几二十秒，排在后面的搅拌、pH 这段时间
/// 都还没连上，面板上就是「没有搅拌能力」（用户 2026-11-10：「各个模块不要互相干涉」）。
/// 现在按串口分组：**不同串口的设备同时连**，谁不通只耽误自己；共用同一个串口的（宇电一台模块上的两支探头、
/// 并在一条 485 上的两台设备）归一组、组内照旧一台一台来——同一个口同时开两次要打架。
/// 一台设备用到的串口 = 它连接参数里所有「串口下拉」字段（Field.Port）的值，没填按缺省。
/// </summary>
public static class ConnectGroups
{
    /// <summary>这台设备会去开哪些串口（大小写不敏感去重）。没有串口字段的设备返回空表。</summary>
    public static IReadOnlyList<string> PortsOf(IDeviceDriver driver, ParameterSet connection)
    {
        var ports = new List<string>();
        foreach (var f in driver.ConnectionSchema.Fields)
        {
            if (f.ChoicesFrom != WellKnownChoices.SerialPorts) continue;
            var v = connection.Str(f.Key, f.Default as string ?? "").Trim();
            if (v.Length > 0 && !ports.Contains(v, StringComparer.OrdinalIgnoreCase)) ports.Add(v);
        }
        return ports;
    }

    /// <summary>
    /// 把连接清单分成几组：组与组之间没有共用串口、可以同时连；组内保持清单里的先后。
    /// 组的先后按各组第一台在清单里的位置。没有串口的设备各自一组。
    /// </summary>
    public static List<List<T>> Group<T>(IReadOnlyList<T> items, Func<T, IReadOnlyList<string>> portsOf)
    {
        var parent = Enumerable.Range(0, items.Count).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        var owner = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < items.Count; i++)
            foreach (var port in portsOf(items[i]))
            {
                if (owner.TryGetValue(port, out var j))
                {
                    var (a, b) = (Find(i), Find(j));
                    if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b);   // 根取靠前的，组的先后就是第一台的先后
                }
                else owner[port] = i;
            }
        var groups = new SortedDictionary<int, List<T>>();
        for (var i = 0; i < items.Count; i++)
        {
            var root = Find(i);
            if (!groups.TryGetValue(root, out var g)) groups[root] = g = new List<T>();
            g.Add(items[i]);
        }
        return groups.Values.ToList();
    }
}
