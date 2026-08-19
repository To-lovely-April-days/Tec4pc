using System.Globalization;
using System.Text;

namespace Tec.Core.Recipes;

/// <summary>
/// 条件表达式。「循环开始 · 按条件」「条件等待」两条指令的判据都是它——
/// 从前 cond 只是一行没人解析的文字，写什么都「合法」，跑起来永远循环一轮完事。
///
/// 文法刻意小：比较（&gt; &lt; &gt;= &lt;= = ≠）之间用 且 / 或（也认 &amp;&amp; || and or）连接，
/// 可加括号。比较的两边是数字、配方变量名，或实时量 Tr / Tj / pH / 浊度 / rpm。
/// 不做四则运算——「浊度 &gt; 50 且 Tr &lt; 30」这种工艺判据用不上算式，
/// 文法越小，操作人写错的花样越少，报出来的错也越说得清。
/// </summary>
public static class Cond
{
    /// <summary>条件里可读的实时量。求值时由执行器接到设备上，校验时据此认名字。</summary>
    public static readonly IReadOnlyList<string> SensorKeys = new[] { "Tr", "Tj", "pH", "浊度", "rpm" };

    /// <summary>参数框下面那行提示，两条指令共用，改一处两边一起变。</summary>
    public const string Help = "如「浊度 > 50 且 Tr < 30」。可用配方变量与实时量 Tr / Tj / pH / 浊度 / rpm";

    // ── 语法树 ───────────────────────────────────────────────────────

    public abstract record Node;

    /// <summary>一次比较。操作数是数字或名字（变量 / 实时量）。</summary>
    public sealed record Cmp(Operand L, string Op, Operand R) : Node;

    /// <summary>且 / 或。</summary>
    public sealed record Logic(bool IsAnd, Node L, Node R) : Node;

    /// <summary>Ident 为 null 就是数字。</summary>
    public readonly record struct Operand(double Number, string? Ident);

    // ── 解析 ─────────────────────────────────────────────────────────

    /// <summary>解析失败返回 null，原因放在 error 里（给校验条直接显示）。</summary>
    public static Node? Parse(string? text, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(text)) { error = "条件是空的"; return null; }

        List<Tok> toks;
        try { toks = Lex(text); }
        catch (FormatException ex) { error = ex.Message; return null; }

        var pos = 0;
        try
        {
            var node = ParseOr(toks, ref pos);
            if (pos != toks.Count)
                throw new FormatException($"「{toks[pos].Text}」这里看不懂——比较之间要用 且 / 或 连接");
            return node;
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>表达式里出现的所有名字（变量或实时量），给校验器逐个认。</summary>
    public static void CollectIdents(Node node, ISet<string> into)
    {
        switch (node)
        {
            case Cmp c:
                if (c.L.Ident is { } a) into.Add(a);
                if (c.R.Ident is { } b) into.Add(b);
                break;
            case Logic l:
                CollectIdents(l.L, into);
                CollectIdents(l.R, into);
                break;
        }
    }

    /// <summary>
    /// 求值。lookup 解析名字（变量表 + 设备读数），读不到就返回 null——
    /// 这时整个条件算不出来，结果是 null 而不是随便猜一个 false：
    /// 探头坏了的时候「条件不满足」和「不知道满不满足」是两回事，后者要报警。
    /// </summary>
    public static bool? Eval(Node node, Func<string, double?> lookup, out string? error)
    {
        error = null;
        switch (node)
        {
            case Cmp c:
            {
                var l = Resolve(c.L, lookup, ref error);
                var r = Resolve(c.R, lookup, ref error);
                if (l is null || r is null) return null;
                return c.Op switch
                {
                    ">" => l > r,
                    "<" => l < r,
                    ">=" => l >= r,
                    "<=" => l <= r,
                    "=" => Math.Abs(l.Value - r.Value) < 1e-9,
                    "!=" => Math.Abs(l.Value - r.Value) >= 1e-9,
                    _ => null
                };
            }
            case Logic lg:
            {
                var l = Eval(lg.L, lookup, out error);
                if (error is not null) return null;
                var r = Eval(lg.R, lookup, out error);
                if (error is not null) return null;
                return lg.IsAnd ? l & r : l | r;   // 三值逻辑：null 会正确传染
            }
            default:
                return null;
        }
    }

    private static double? Resolve(Operand o, Func<string, double?> lookup, ref string? error)
    {
        if (o.Ident is null) return o.Number;
        var v = lookup(o.Ident);
        if (v is null) error ??= $"读不到「{o.Ident}」";
        return v;
    }

    /// <summary>
    /// 变量名的合法性：字母 / 数字 / 下划线 / 汉字，不能以数字开头，
    /// 不能撞实时量的名字（那几个名字在条件里永远指设备读数）。
    /// </summary>
    public static bool ValidName(string name)
    {
        if (name.Length == 0 || char.IsDigit(name[0])) return false;
        foreach (var ch in name)
            if (!IdentChar(ch)) return false;
        return true;
    }

    // ── 词法 ─────────────────────────────────────────────────────────

    private readonly record struct Tok(TokKind Kind, string Text, double Num);
    private enum TokKind { Num, Ident, CmpOp, And, Or, LParen, RParen }

    // 且 / 或 是保留字（单字连接词），不算标识符的一部分——变量名里也不许用
    private static bool IdentChar(char c)
        => c != '且' && c != '或' && (char.IsLetterOrDigit(c) || c == '_');

    private static List<Tok> Lex(string text)
    {
        var toks = new List<Tok>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c) || c == '　') { i++; continue; }

            switch (c)
            {
                case '(' or '（': toks.Add(new Tok(TokKind.LParen, "(", 0)); i++; continue;
                case ')' or '）': toks.Add(new Tok(TokKind.RParen, ")", 0)); i++; continue;
                case '且': toks.Add(new Tok(TokKind.And, "且", 0)); i++; continue;
                case '或': toks.Add(new Tok(TokKind.Or, "或", 0)); i++; continue;
                case '&':
                    if (i + 1 < text.Length && text[i + 1] == '&') { toks.Add(new Tok(TokKind.And, "&&", 0)); i += 2; continue; }
                    throw new FormatException("单个 & 不认识，「并且」写作 且 或 &&");
                case '|':
                    if (i + 1 < text.Length && text[i + 1] == '|') { toks.Add(new Tok(TokKind.Or, "||", 0)); i += 2; continue; }
                    throw new FormatException("单个 | 不认识，「或者」写作 或 或 ||");
                case '≥': toks.Add(new Tok(TokKind.CmpOp, ">=", 0)); i++; continue;
                case '≤': toks.Add(new Tok(TokKind.CmpOp, "<=", 0)); i++; continue;
                case '≠': toks.Add(new Tok(TokKind.CmpOp, "!=", 0)); i++; continue;
                case '>':
                    if (i + 1 < text.Length && text[i + 1] == '=') { toks.Add(new Tok(TokKind.CmpOp, ">=", 0)); i += 2; }
                    else { toks.Add(new Tok(TokKind.CmpOp, ">", 0)); i++; }
                    continue;
                case '<':
                    if (i + 1 < text.Length && text[i + 1] == '=') { toks.Add(new Tok(TokKind.CmpOp, "<=", 0)); i += 2; }
                    else { toks.Add(new Tok(TokKind.CmpOp, "<", 0)); i++; }
                    continue;
                case '=':
                    i += i + 1 < text.Length && text[i + 1] == '=' ? 2 : 1;
                    toks.Add(new Tok(TokKind.CmpOp, "=", 0));
                    continue;
                case '!':
                    if (i + 1 < text.Length && text[i + 1] == '=') { toks.Add(new Tok(TokKind.CmpOp, "!=", 0)); i += 2; continue; }
                    throw new FormatException("单个 ! 不认识，「不等于」写作 != 或 ≠");
            }

            // 数字（含负号与小数）。负号只在「前面不是数也不是名字」时归数字——
            // 「a-3」我们本来就不支持减法，报错让人写清楚
            if (char.IsDigit(c)
                || (c == '-' && i + 1 < text.Length && char.IsDigit(text[i + 1])
                    && (toks.Count == 0 || toks[^1].Kind is TokKind.CmpOp or TokKind.And or TokKind.Or or TokKind.LParen)))
            {
                var start = i;
                i++;
                while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.')) i++;
                var slice = text[start..i];
                if (!double.TryParse(slice, NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
                    throw new FormatException($"「{slice}」不是一个数");
                toks.Add(new Tok(TokKind.Num, slice, num));
                continue;
            }

            if (IdentChar(c) && !char.IsDigit(c))
            {
                var sb = new StringBuilder();
                while (i < text.Length && IdentChar(text[i])) { sb.Append(text[i]); i++; }
                var word = sb.ToString();
                // and / or 是英文写法的连接词，不当名字用
                if (word.Equals("and", StringComparison.OrdinalIgnoreCase)) toks.Add(new Tok(TokKind.And, word, 0));
                else if (word.Equals("or", StringComparison.OrdinalIgnoreCase)) toks.Add(new Tok(TokKind.Or, word, 0));
                else toks.Add(new Tok(TokKind.Ident, word, 0));
                continue;
            }

            throw new FormatException($"「{c}」不认识——条件里只有比较、且 / 或、括号、数字和名字");
        }
        if (toks.Count == 0) throw new FormatException("条件是空的");
        return toks;
    }

    // ── 递归下降 ─────────────────────────────────────────────────────

    private static Node ParseOr(List<Tok> toks, ref int pos)
    {
        var left = ParseAnd(toks, ref pos);
        while (pos < toks.Count && toks[pos].Kind == TokKind.Or)
        {
            pos++;
            left = new Logic(false, left, ParseAnd(toks, ref pos));
        }
        return left;
    }

    private static Node ParseAnd(List<Tok> toks, ref int pos)
    {
        var left = ParsePrim(toks, ref pos);
        while (pos < toks.Count && toks[pos].Kind == TokKind.And)
        {
            pos++;
            left = new Logic(true, left, ParsePrim(toks, ref pos));
        }
        return left;
    }

    private static Node ParsePrim(List<Tok> toks, ref int pos)
    {
        if (pos < toks.Count && toks[pos].Kind == TokKind.LParen)
        {
            pos++;
            var inner = ParseOr(toks, ref pos);
            if (pos >= toks.Count || toks[pos].Kind != TokKind.RParen)
                throw new FormatException("括号没配对");
            pos++;
            return inner;
        }

        var l = ParseOperand(toks, ref pos, "比较的左边");
        if (pos >= toks.Count || toks[pos].Kind != TokKind.CmpOp)
            throw new FormatException($"「{l.Ident ?? l.Number.ToString(CultureInfo.InvariantCulture)}」后面要跟比较符（> < >= <= = ≠）");
        var op = toks[pos].Text;
        pos++;
        var r = ParseOperand(toks, ref pos, "比较的右边");
        return new Cmp(l, op, r);
    }

    private static Operand ParseOperand(List<Tok> toks, ref int pos, string where)
    {
        if (pos >= toks.Count)
            throw new FormatException($"{where}缺一个数或名字");
        var t = toks[pos];
        pos++;
        return t.Kind switch
        {
            TokKind.Num => new Operand(t.Num, null),
            TokKind.Ident => new Operand(0, t.Text),
            _ => throw new FormatException($"{where}应当是数或名字，不是「{t.Text}」")
        };
    }
}
