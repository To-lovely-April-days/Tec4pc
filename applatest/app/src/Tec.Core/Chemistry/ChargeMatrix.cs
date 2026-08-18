namespace Tec.Core.Chemistry;

/// <summary>矩阵的一行：同一个组分在各通道里的那几行（缺的是 null）。</summary>
public sealed class MatrixRow
{
    /// <summary>对齐用的键：有 CAS 用 CAS，没有用规整过的名字。</summary>
    public required string Key { get; init; }
    /// <summary>显示名与角色取第一个出现它的通道那一行。</summary>
    public required string Name { get; init; }
    public required ChargeRole Role { get; init; }
    /// <summary>按传入通道的顺序，一格一个；null = 这个通道没有该组分。</summary>
    public required ChargeItem?[] Cells { get; init; }

    /// <summary>有通道缺这一行。缺本身就是要看见的信息。</summary>
    public bool HasGap => Cells.Any(c => c is null);
}

/// <summary>
/// 四通道对照矩阵的**对齐**：把各通道互相独立的配料表按组分拼成一张
/// 「组分 × 通道」的表。这是视图层的事——数据模型仍是一路一张表，
/// 各自冻结、各自快照互不牵连；「共享」发生在复制那一下，不在存储里。
///
/// 对齐凭据先 CAS 后名字（规整空白，忽略大小写）：CAS 是稳定键，
/// 名字是给没连库的内部代号兜底。同一通道里出现两行同键的，
/// 各占一行不合并——合并会把两行不同的量挤进一格，谁也读不懂。
/// </summary>
public static class ChargeMatrix
{
    public static IReadOnlyList<MatrixRow> Align(IReadOnlyList<ChargeTable> tables)
    {
        var rows = new List<MatrixRow>();
        // 键 → 已经排进去的行（同键可能有多行：某通道重复录了两行同一组分）
        var byKey = new Dictionary<string, List<MatrixRow>>(StringComparer.OrdinalIgnoreCase);

        for (var ch = 0; ch < tables.Count; ch++)
        {
            // 同键在本通道内出现第几次，就去对齐第几行
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in tables[ch].Items)
            {
                var key = KeyOf(item);
                if (key.Length == 0) continue;               // 连名字都没有的空行不进矩阵

                var nth = seen.TryGetValue(key, out var n) ? n : 0;
                seen[key] = nth + 1;

                if (!byKey.TryGetValue(key, out var list))
                    byKey[key] = list = new List<MatrixRow>();

                if (nth < list.Count)
                {
                    list[nth].Cells[ch] = item;
                    continue;
                }

                var row = new MatrixRow
                {
                    Key = key,
                    Name = item.Name.Trim().Length > 0 ? item.Name : key,
                    Role = item.Role,
                    Cells = new ChargeItem?[tables.Count]
                };
                row.Cells[ch] = item;
                list.Add(row);
                rows.Add(row);                               // 行序 = 各通道出现的先后
            }
        }
        return rows;
    }

    /// <summary>对齐键：CAS 稳定优先（含内部 # 键），没有就用规整过的名字。</summary>
    public static string KeyOf(ChargeItem item)
        => item.Cas.Length > 0 ? item.Cas : Norm(item.Name);

    private static string Norm(string s) => s.Replace("　", " ").Trim();
}
