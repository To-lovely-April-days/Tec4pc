using System.Globalization;
using System.Text;

namespace Tec.Core.Compounds;

/// <summary>一次导入的结果。**问题要逐条说得出**，不能只回一句「导入失败」。</summary>
public sealed class ImportResult
{
    public List<Compound> Items { get; } = new();
    /// <summary>第几行出了什么事。行号按文件里的物理行数，人打开文件能对上。</summary>
    public List<string> Problems { get; } = new();
    /// <summary>认出来的列。没认出来的列名照原样列在这儿，好让人知道哪一列被忽略了。</summary>
    public List<string> IgnoredColumns { get; } = new();
    public bool Ok => Items.Count > 0;
}

/// <summary>
/// 化合物库的 CSV 导入 / 导出。
///
/// **导入是刚需不是加分项**：这套装置真正跑的中间体多半是内部代号，
/// 公开库（PubChem / Wikidata）里根本查不到，而且客户也不会把内部结构发到线上服务去。
/// 所以库里那点自带数据只能覆盖常用溶剂和试剂，剩下的必须让人自己录进来。
///
/// 表头认中文也认英文，顺序随便排——手上那份表是从别的系统导出来的，
/// 不该要求人先把列排成我们要的顺序。认不出来的列照实列出来，不悄悄丢掉。
/// </summary>
public static class CompoundCsv
{
    /// <summary>列名 → 字段。一个字段可以有好几种叫法。</summary>
    private static readonly (string Key, string[] Names)[] Columns =
    {
        ("name", new[] { "名称", "化合物", "化合物名称", "name", "compound" }),
        ("cas", new[] { "cas", "cas号", "cas 号", "casno", "cas_no" }),
        ("formula", new[] { "分子式", "formula" }),
        ("mw", new[] { "摩尔质量", "分子量", "mw", "molarmass", "molecularweight" }),
        ("density", new[] { "密度", "density", "rho" }),
        ("purity", new[] { "纯度", "purity", "assay" }),
        ("mp", new[] { "熔点", "mp", "meltingpoint" }),
        ("bp", new[] { "沸点", "bp", "boilingpoint" }),
        ("cp", new[] { "比热", "比热容", "cp", "heatcapacity" }),
        ("category", new[] { "类别", "分组", "category", "group" }),
        ("solvent", new[] { "常用溶剂", "溶剂", "solvent" }),
        ("batch", new[] { "批号", "批次", "batch", "lot" }),
        ("supplier", new[] { "供应商", "厂家", "supplier", "vendor" }),
        ("note", new[] { "备注", "说明", "note", "remark" }),
        ("solubility", new[] { "溶解度系数", "溶解度拟合", "solubility" }),
        ("phase", new[] { "相态", "形态", "phase", "state" })
    };

    public static readonly string[] HeaderRow =
    {
        "名称", "CAS", "分子式", "摩尔质量", "密度", "纯度", "熔点", "沸点",
        "比热", "相态", "类别", "常用溶剂", "批号", "供应商", "备注", "溶解度系数"
    };

    // ── 导入 ────────────────────────────────────────────────────────

    public static ImportResult Read(string text)
    {
        var result = new ImportResult();
        var rows = Split(text);
        if (rows.Count == 0) { result.Problems.Add("文件是空的。"); return result; }

        var header = rows[0].Cells;
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < header.Count; i++)
        {
            var key = Match(header[i]);
            if (key is null)
            {
                if (header[i].Trim().Length > 0) result.IgnoredColumns.Add(header[i].Trim());
                continue;
            }
            map.TryAdd(key, i);
        }

        if (!map.ContainsKey("name") && !map.ContainsKey("cas"))
        {
            result.Problems.Add("第 1 行不像表头：既没有「名称」也没有「CAS」这一列。"
                                + "认得的列名：" + string.Join(" / ", HeaderRow));
            return result;
        }

        for (var r = 1; r < rows.Count; r++)
        {
            var (line, cells) = (rows[r].Line, rows[r].Cells);
            if (cells.All(c => c.Trim().Length == 0)) continue;      // 空行跳过，不当成错

            // 格数比表头多，几乎总是「名字里有逗号却没加引号」——
            // 「2,4-二硝基苯甲醚」会被切成两格，从那一列起**整行都错位**，
            // 而错位之后每个值看着都还像模像样（密度栏里坐着一个熔点）。
            // 这种错必须喊出来：读进去不报，比读不进去危险得多
            if (cells.Count > header.Count)
                result.Problems.Add($"第 {line} 行：有 {cells.Count} 格，表头只有 {header.Count} 列——"
                                    + "多半是某个名称里带了逗号却没有用引号括起来，这一行的列会整体错位。");

            string Cell(string key) => map.TryGetValue(key, out var i) && i < cells.Count
                ? cells[i].Trim() : "";

            var name = Cell("name");
            var cas = Cell("cas");
            if (name.Length == 0 && cas.Length == 0)
            {
                result.Problems.Add($"第 {line} 行：名称和 CAS 都是空的，跳过。");
                continue;
            }
            if (name.Length == 0) name = cas;

            var c = new Compound
            {
                Name = name,
                // 没有 CAS 的（内部代号）给一个按名字算出来的内部键。库是按 CAS 认人的，
                // 留空会让第二条无 CAS 的记录把第一条覆盖掉；
                // 直接拿名字顶上去则会漏到界面「CAS 号」那一列里，等于替它编了个 CAS
                Cas = cas.Length > 0 ? cas : Compound.KeyOf(name),
                Formula = Cell("formula"),
                Category = Cell("category"),
                Solvent = Cell("solvent"),
                Batch = Cell("batch"),
                Supplier = Cell("supplier"),
                Note = Cell("note")
            };

            void Num(string key, string label, Action<double> set, double min, double max)
            {
                var raw = Cell(key);
                if (raw.Length == 0) return;
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                {
                    result.Problems.Add($"第 {line} 行 {c.Name}：{label}「{raw}」不是数字，这一项留空。");
                    return;
                }
                if (v < min || v > max)
                {
                    result.Problems.Add($"第 {line} 行 {c.Name}：{label} {raw} 超出合理范围（{min} ~ {max}），这一项留空。");
                    return;
                }
                set(v);
            }

            Num("mw", "摩尔质量", v => c.Mw = v, 0.1, 100_000);
            Num("mp", "熔点", v => c.Mp = v, -273, 4000);
            Num("density", "密度", v => c.Density = v, 0.0001, 30);
            Num("bp", "沸点", v => c.Bp = v, -273, 6000);
            Num("purity", "纯度", v => c.Purity = v, 0, 100);
            Num("cp", "比热", v => c.Cp = v, 0.001, 100);

            // 相态只认「固」「液」两个字（英文 solid/liquid 也认）。别的写法照实报，
            // 不猜——「s」到底是 solid 还是 solution，猜错的代价是拦错一炉料
            var phase = Cell("phase");
            if (phase.Length > 0)
            {
                c.Phase = phase.Trim().ToLowerInvariant() switch
                {
                    "固" or "固体" or "solid" or "s" => "固",
                    "液" or "液体" or "liquid" or "l" => "液",
                    _ => ""
                };
                if (c.Phase.Length == 0)
                    result.Problems.Add($"第 {line} 行 {c.Name}：相态「{phase}」认不出（只认 固 / 液），这一项留空。");
            }

            var coef = Cell("solubility");
            if (coef.Length > 0)
            {
                var parts = coef.Split(new[] { ';', '|', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                var vals = new List<double>();
                foreach (var p in parts)
                    if (double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) vals.Add(v);
                if (vals.Count > 0) c.Solubility = vals.ToArray();
                else result.Problems.Add($"第 {line} 行 {c.Name}：溶解度系数「{coef}」读不出数字，忽略。");
            }

            result.Items.Add(c);
        }

        if (result.Items.Count == 0 && result.Problems.Count == 0)
            result.Problems.Add("表头认出来了，但一行数据都没有。");
        return result;
    }

    private static string? Match(string header)
    {
        var h = header.Trim().Trim('"').ToLowerInvariant();

        // 单位后缀去掉：「密度 g/mL」「熔点 ℃」「纯度%」「摩尔质量(g/mol)」这几种都很常见，
        // 手上那份表是从别的系统导出来的，不该要求人先把列名改成我们要的样子。
        //
        // **先按分隔符截，再去空格**。反过来的话「密度 g/mL」会先并成「密度g/ml」，
        // 截到斜杠只剩「密度g」——那一列就认不出来了，而且是悄无声息地认不出来
        // （实测踩到过：整份表导进来，密度全空）
        var cut = h.IndexOfAny(new[] { ' ', '　', '(', '（', '[', '/', '%', '℃', '·', ',', '，' });
        var bare = Squeeze(cut > 0 ? h[..cut] : h);
        var whole = Squeeze(h);

        // 中文列名后面直接贴着单位、中间连空格都没有的（「密度g/cm3」）：
        // 把结尾那串 ASCII 削掉再试一次。只对「中文开头」的列名这么干——
        // 英文列名（Density）整串都是 ASCII，削完就什么都不剩了
        var cjk = bare.Length > 0 && bare[0] > 0x7F
            ? new string(bare.TakeWhile(c => c > 0x7F).ToArray())
            : "";

        foreach (var (key, names) in Columns)
            foreach (var n in names)
                if (bare == n || whole == n || (cjk.Length > 0 && cjk == n)) return key;
        return null;
    }

    private static string Squeeze(string s)
        => s.Replace(" ", "").Replace("　", "").TrimEnd('_', '-', ':', '：');

    // ── 导出 ────────────────────────────────────────────────────────

    public static string Write(IEnumerable<Compound> items)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(",", HeaderRow)).Append("\r\n");
        foreach (var c in items)
            sb.Append(string.Join(",",
                // 没有 CAS 的那几条留空格子。内部键是我们自己的东西，
                // 写到「CAS」那一列里，人拿 Excel 打开会以为那就是它的 CAS 号
                Esc(c.Name), Esc(c.HasCas ? c.Cas : ""), Esc(c.Formula),
                N(c.Mw, "0.####"),
                N(c.Density, "0.####"), N(c.Purity, "0.##"),
                N(c.Mp, "0.##"), N(c.Bp, "0.##"), N(c.Cp, "0.####"),
                Esc(c.Phase),
                Esc(c.Category), Esc(c.Solvent), Esc(c.Batch), Esc(c.Supplier), Esc(c.Note),
                Esc(string.Join(";", c.Solubility.Select(v => v.ToString("R", CultureInfo.InvariantCulture)))))).Append("\r\n");
        return sb.ToString();
    }

    /// <summary>没填就是空格子。写成 0 的话，导出去再导回来「没填」就变成了「密度为零」。</summary>
    private static string N(double? v, string fmt)
        => v is null ? "" : v.Value.ToString(fmt, CultureInfo.InvariantCulture);

    private static string Esc(string? s)
    {
        s ??= "";
        if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    // ── 逐行拆 ──────────────────────────────────────────────────────

    /// <summary>
    /// 带引号的 CSV 拆分。**一格里可以有换行**（备注常常有），所以不能先按行切再按逗号切。
    /// 行号记的是文件里的物理行，人打开文件对得上。
    /// </summary>
    private static List<(int Line, List<string> Cells)> Split(string text)
    {
        var rows = new List<(int, List<string>)>();
        var cells = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        var line = 1;
        var rowLine = 1;
        var any = false;

        // UTF-8 BOM：Excel 存出来的中文 CSV 一定带它，不吃掉的话第一个列名认不出来
        var i = text.Length > 0 && text[0] == '﻿' ? 1 : 0;

        for (; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch != '"') { cell.Append(ch); if (ch == '\n') line++; continue; }
                if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; continue; }
                quoted = false;
                continue;
            }
            switch (ch)
            {
                case '"': quoted = true; any = true; break;
                case ',': cells.Add(cell.ToString()); cell.Clear(); any = true; break;
                case '\r': break;
                case '\n':
                    cells.Add(cell.ToString());
                    cell.Clear();
                    if (any || cells.Any(c => c.Length > 0)) rows.Add((rowLine, cells));
                    cells = new List<string>();
                    any = false;
                    line++;
                    rowLine = line;
                    break;
                default: cell.Append(ch); any = true; break;
            }
        }
        if (cell.Length > 0 || cells.Count > 0)
        {
            cells.Add(cell.ToString());
            if (any || cells.Any(c => c.Length > 0)) rows.Add((rowLine, cells));
        }
        return rows;
    }
}
