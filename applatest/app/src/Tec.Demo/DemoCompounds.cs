using System.Reflection;
using System.Text;
using Tec.Core.Compounds;

namespace Tec.Demo;

/// <summary>
/// 【本地改动】演示用化合物库：docs/常用溶剂酸碱.csv 那 61 条 + 10 条常见原料 / 产物（苯甲酸、对乙酰氨基酚、草酸、乙酸乙酯……）。
///
/// WHY：化合物页截图要有一库物性；配料表要靠库里的摩尔质量 / 密度 / 纯度算当量。两份 CSV 都走程序自己的
/// 导入器（CompoundCsv.Read）——跟操作人在化合物页按「导入 CSV」是同一条路，不另造一套解析。
/// </summary>
public static class DemoCompounds
{
    public static List<Compound> Load()
    {
        var list = new List<Compound>();
        foreach (var name in new[] { "Tec.Demo.Data.常用溶剂酸碱.csv", "Tec.Demo.Data.化合物-补充.csv" })
        {
            var r = CompoundCsv.Read(ReadText(name));
            foreach (var c in r.Items)
                if (!list.Any(x => x.Cas == c.Cas)) list.Add(c);
        }
        return list;
    }

    internal static string ReadText(string resource)
    {
        using var s = typeof(DemoCompounds).Assembly.GetManifestResourceStream(resource)
                      ?? throw new InvalidOperationException($"演示资源 {resource} 不在程序集里");
        using var r = new StreamReader(s, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return r.ReadToEnd();
    }

    /// <summary>按名字取（配料表盖物性快照用）。</summary>
    public static Compound? Find(IReadOnlyList<Compound> lib, string name)
        => lib.FirstOrDefault(c => c.Name == name) ?? lib.FirstOrDefault(c => c.Name.StartsWith(name, StringComparison.Ordinal));
}
