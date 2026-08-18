using System.Globalization;

namespace Tec.Core.Compounds;

/// <summary>
/// 化合物库的编辑审计（CH-1.7/6.1 的库侧）：一次写入相对库里旧值**改了什么**，
/// 说成几句人话短语，进系统日志。日志是给复核的人看的，所以是
/// 「密度 1.266 → 1.27」这种句子，不是字段名加 JSON。
///
/// 谁改的、什么时候改的不归这里管——系统日志本身每行都带时刻和操作人。
/// 这里只回答「改了什么」，而且**跟库里落着的旧值比**，不跟界面上一闪而过的
/// 中间状态比：审计要对得上盘里的前后两个版本。
/// </summary>
public static class CompoundAudit
{
    /// <summary>
    /// 变化清单。<paramref name="before"/> 是库里的旧行（null = 库里没有，这次是新增）。
    /// 空清单 = 一个字都没变。数值照存的位数原样写，不做四舍五入——
    /// 审计里的数要跟库里的数一个模样，抹了小数就对不上了。
    /// </summary>
    public static List<string> Diff(Compound? before, Compound after)
    {
        if (before is null) return new List<string> { "新增" };

        var list = new List<string>();
        Text(list, "名称", before.Name, after.Name);
        Text(list, "分子式", before.Formula, after.Formula);
        Num(list, "摩尔质量", before.Mw, after.Mw);
        Num(list, "熔点", before.Mp, after.Mp);
        Num(list, "沸点", before.Bp, after.Bp);
        Num(list, "密度", before.Density, after.Density);
        Num(list, "纯度", before.Purity, after.Purity);
        Num(list, "比热容", before.Cp, after.Cp);
        Text(list, "相态", before.Phase, after.Phase);
        Text(list, "类别", before.Category, after.Category);
        Text(list, "溶剂", before.Solvent, after.Solvent);
        Text(list, "批号", before.Batch, after.Batch);
        Text(list, "供应商", before.Supplier, after.Supplier);
        Text(list, "备注", before.Note, after.Note);

        // 拟合系数是一串抽象的数，倒进日志里谁也读不出意思——只说这一栏动过了
        if (!before.Solubility.SequenceEqual(after.Solubility))
            list.Add(after.Solubility.Length == 0 ? "清掉溶解度拟合系数"
                   : before.Solubility.Length == 0 ? "补上溶解度拟合系数"
                   : "溶解度拟合系数已更新");

        // StructureKey / IonText 是程序自带的图形资源键，界面上改不了，不进审计
        return list;
    }

    /// <summary>日志里指认这一条的写法：有 CAS 带上，内部键不冒充 CAS 号。</summary>
    public static string Describe(Compound c)
    {
        var name = c.Name.Trim();
        if (name.Length == 0) name = c.Cas;
        return c.HasCas ? $"{name}（{c.Cas}）" : name;
    }

    private static void Num(List<string> list, string label, double? old, double? now)
    {
        if (Nullable.Equals(old, now)) return;
        list.Add(now is null ? $"清掉{label}（原 {N(old!.Value)}）"
               : old is null ? $"补上{label} {N(now.Value)}"
               : $"{label} {N(old.Value)} → {N(now.Value)}");
    }

    private static void Text(List<string> list, string label, string old, string now)
    {
        old = old.Trim(); now = now.Trim();
        if (string.Equals(old, now, StringComparison.Ordinal)) return;
        list.Add(now.Length == 0 ? $"清掉{label}（原「{Trunc(old)}」）"
               : old.Length == 0 ? $"补上{label}「{Trunc(now)}」"
               : $"{label}「{Trunc(old)}」→「{Trunc(now)}」");
    }

    /// <summary>照存的位数原样写（同编辑框），不用显示层那份四舍五入的格式。</summary>
    private static string N(double v) => v.ToString("0.##########", CultureInfo.InvariantCulture);

    /// <summary>备注可以写成小作文，日志里掐头留个认得出的开头就够了。</summary>
    private static string Trunc(string s) => s.Length <= 24 ? s : s[..23] + "…";
}
