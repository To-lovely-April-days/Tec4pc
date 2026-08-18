using System.Globalization;
using Tec.Core.Compounds;

namespace Tec.Core.Chemistry;

/// <summary>
/// 配料行的物性快照（CH-D1）。
///
/// 原则一句话：**实验记录不得引用活数据**。配料行连库那一刻把库里的
/// 摩尔质量 / 密度 / 纯度拷贝进行里，并盖上「哪一版库、什么时刻」；
/// 之后库里怎么改，这一行、这一炉的记录、这一份报告都纹丝不动。
/// 不这么做的话，今天修一下库里的密度，去年那炉报告里的「应量取 X mL」
/// 就跟着变了——投料依据被追溯性篡改，复核的人对不回去。
///
/// 拷贝**只填空格**：行上已经写了的数（手里这瓶料的实测纯度）比库里的
/// 参考值更接近事实，连库不把它盖掉。要整行改回库里的当前值，
/// 走 <see cref="Refresh"/>——那是人显式按出来的动作，改了什么逐条报。
/// </summary>
public static class ChargeSnapshot
{
    /// <summary>
    /// 连库：空的物性从库里拷进来，批号 / 供应商也是空才填，然后盖章。
    /// 就算一格都没拷（行上全填过了），章也要盖——章的意思是
    /// 「从这一刻起这一行自带全部物性，算它不再需要活库」。
    /// </summary>
    public static void Link(ChargeItem item, Compound c, int libraryVersion, DateTimeOffset now)
    {
        item.Mw ??= c.Mw;
        item.Density ??= c.Density;
        item.Purity ??= c.Purity;
        item.Bp ??= c.Bp;
        item.Mp ??= c.Mp;
        if (item.Batch.Length == 0) item.Batch = c.Batch;
        if (item.Supplier.Length == 0) item.Supplier = c.Supplier;
        // 相态也是空才带：库里写的是纯品常态，行上写的是这一炉的投料形态——
        // 固体配成溶液走泵时，行上那个「液」不能被库里的「固」盖掉
        if (item.Phase.Length == 0) item.Phase = c.Phase;
        item.SnapshotAt = now;
        item.LibraryVersion = libraryVersion;
    }

    /// <summary>
    /// 把连库行的物性改回库里的**当前**值。这是人显式按出来的同步动作，
    /// 三项物性直接覆盖（包括行上手改过的——所以改了什么必须逐条说出来，
    /// 让人看得见自己盖掉了什么）；批号 / 供应商仍然只填空格，
    /// 那两样写的是手里这一瓶，不该被库里的参考值顶掉。
    /// </summary>
    public static List<string> Refresh(ChargeTable table, IReadOnlyList<Compound> library,
                                       int libraryVersion, DateTimeOffset now)
    {
        var changes = new List<string>();
        var lib = Index(library);

        foreach (var item in table.Items)
        {
            if (item.Cas.Length == 0 || !lib.TryGetValue(item.Cas, out var c)) continue;

            var who = item.Name.Length > 0 ? item.Name : "（未命名）";
            Put(changes, who, "摩尔质量", item.Mw, c.Mw, v => item.Mw = v);
            Put(changes, who, "密度", item.Density, c.Density, v => item.Density = v);
            Put(changes, who, "纯度", item.Purity, c.Purity, v => item.Purity = v);
            if (item.Batch.Length == 0 && c.Batch.Length > 0)
            {
                item.Batch = c.Batch;
                changes.Add($"{who}：补上批号 {c.Batch}");
            }
            if (item.Supplier.Length == 0 && c.Supplier.Length > 0)
            {
                item.Supplier = c.Supplier;
                changes.Add($"{who}：补上供应商 {c.Supplier}");
            }
            item.SnapshotAt = now;
            item.LibraryVersion = libraryVersion;
        }
        return changes;
    }

    /// <summary>
    /// 老文件补章：只处理「连了库、还没盖过章」的行，语义同 <see cref="Link"/>。
    /// 快照机制之前存的文件，物性本来就是打开时从库里现取的——现在把现取的值
    /// 落在行上、盖今天的章，从此不再漂。返回补了章的行名，打开时要说一声。
    /// 库里找不到的行**不补**：没东西可拷，盖章就是谎报。
    /// </summary>
    public static List<string> Migrate(ChargeTable table, IReadOnlyList<Compound> library,
                                       int libraryVersion, DateTimeOffset now)
    {
        var stamped = new List<string>();
        var lib = Index(library);

        foreach (var item in table.Items)
        {
            if (item.Cas.Length == 0 || item.SnapshotAt is not null) continue;
            if (!lib.TryGetValue(item.Cas, out var c)) continue;
            Link(item, c, libraryVersion, now);
            stamped.Add(item.Name.Length > 0 ? item.Name : "（未命名）");
        }
        return stamped;
    }

    /// <summary>
    /// 全部连库行都盖了章——算这张表不再需要活库，报告 / 导出可以不传库，
    /// 从机制上杜绝「历史记录跟着今天的库变」。
    /// </summary>
    public static bool SelfContained(ChargeTable table)
        => !table.Items.Any(i => i.Cas.Length > 0 && i.SnapshotAt is null);

    /// <summary>
    /// 表脚用的一句话：这张表的物性按哪一版库、什么时刻快照的。
    /// 各行版本一致就报一个数；不一致（分几次连的库）就报个范围，不含糊成一个。
    /// 没有任何盖章行就没有这句话——没做过的事不写。
    /// </summary>
    public static string? Describe(ChargeTable table)
    {
        var stamps = table.Items
            .Where(i => i.SnapshotAt is not null && i.LibraryVersion is not null)
            .ToList();
        if (stamps.Count == 0) return null;

        var versions = stamps.Select(i => i.LibraryVersion!.Value).Distinct().OrderBy(v => v).ToList();
        var latest = stamps.Max(i => i.SnapshotAt!.Value);
        var when = latest.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        return versions.Count == 1
            ? $"物性快照：化合物库第 {versions[0]} 版 · {when}"
            : $"物性快照：化合物库第 {versions[0]}–{versions[^1]} 版（各行分次连库）· 最近 {when}";
    }

    private static void Put(List<string> changes, string who, string what,
                            double? old, double? now, Action<double?> set)
    {
        if (now is null || Same(old, now)) return;
        set(now);
        changes.Add(old is null
            ? $"{who}：补上{what} {Num(now.Value)}"
            : $"{who}：{what} {Num(old.Value)} → {Num(now.Value)}");
    }

    private static bool Same(double? a, double? b)
        => a is { } x && b is { } y && Math.Abs(x - y) < 1e-9;

    private static string Num(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    private static Dictionary<string, Compound> Index(IReadOnlyList<Compound> lib)
    {
        var d = new Dictionary<string, Compound>(StringComparer.Ordinal);
        foreach (var c in lib) d[c.Cas] = c;
        return d;
    }
}
