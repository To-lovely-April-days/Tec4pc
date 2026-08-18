using System.Globalization;

namespace Tec.Core.Chemistry;

/// <summary>一次饱和温度换算的结果。算不出来必须说得出为什么。</summary>
public sealed class SaturationResult
{
    /// <summary>饱和温度 ℃。null = 算不出来，看 <see cref="Problem"/>。</summary>
    public double? Temperature { get; init; }

    /// <summary>算不出来的原因，人话。</summary>
    public string? Problem { get; init; }

    public bool Ok => Temperature is not null;

    internal static SaturationResult No(string why) => new() { Problem = why };
}

/// <summary>
/// 溶解度曲线：a + b·T + c·T²，单位 g 溶质 / 100 mL **水**。
///
/// **这条曲线写死了水。**库里存的那三个系数是水中溶解度的拟合，
/// 换成乙醇、甲苯就完全是另一条曲线——而拿水的曲线去算甲苯体系，
/// 会得到一个看着非常合理的温度。那种错没人查得出来，所以宁可拒绝算。
///
/// 拟合区间也写死在 0 ~ 90 ℃：外插出去的值不是数据，是二次函数的想象。
/// </summary>
public static class Solubility
{
    /// <summary>拟合区间。库里那几条曲线都是按这个范围拟的。</summary>
    public const double MinT = 0, MaxT = 90;

    /// <summary>某温度下的溶解度 g/100 mL 水。</summary>
    public static double At(IReadOnlyList<double> coef, double t)
    {
        var s = 0d;
        var p = 1d;
        foreach (var c in coef) { s += c * p; p *= t; }
        return s;
    }

    /// <summary>
    /// 给定浓度下的饱和温度：解 a + b·T + c·T² = C。
    ///
    /// 用二分不用求根公式：求根公式要在两个根里挑一个，挑错了就是一个
    /// 「在拟合区间内、看着也合理」的错温度。二分只会在**曲线单调**的区间里
    /// 找到唯一那个解，找不到就说找不到。
    /// </summary>
    public static SaturationResult Saturate(IReadOnlyList<double>? coef, double gramsPer100mL)
    {
        if (coef is not { Count: >= 2 })
            return SaturationResult.No("这条化合物没有溶解度曲线数据。");
        if (gramsPer100mL <= 0)
            return SaturationResult.No("浓度必须大于 0。");

        // 单调才有唯一解。导数 b + 2c·T 在区间两端同号即可（它自己是线性的）
        var d0 = Derivative(coef, MinT);
        var d1 = Derivative(coef, MaxT);
        if (d0 <= 0 || d1 <= 0)
            return SaturationResult.No($"这条曲线在 {N(MinT)} ~ {N(MaxT)} ℃ 内不是单调升的，"
                                       + "同一个浓度会对上不止一个温度，没法给出唯一的饱和温度。");

        var lo = At(coef, MinT);
        var hi = At(coef, MaxT);

        if (gramsPer100mL < lo)
            return SaturationResult.No($"浓度 {N(gramsPer100mL)} g/100 mL 低于 {N(MinT)} ℃ 的溶解度 "
                                       + $"{N(lo)}——这个浓度在整个拟合区间里都是全溶的，没有饱和点。");
        if (gramsPer100mL > hi)
            return SaturationResult.No($"浓度 {N(gramsPer100mL)} g/100 mL 高于 {N(MaxT)} ℃ 的溶解度 "
                                       + $"{N(hi)}——{N(MaxT)} ℃ 也溶不完，饱和温度在拟合区间之外。");

        // 二分。区间 90 ℃，40 次砍到 1e-10，够了
        double a = MinT, b = MaxT;
        for (var i = 0; i < 60; i++)
        {
            var m = (a + b) / 2;
            if (At(coef, m) < gramsPer100mL) a = m; else b = m;
        }
        return new SaturationResult { Temperature = (a + b) / 2 };
    }

    private static double Derivative(IReadOnlyList<double> coef, double t)
    {
        var s = 0d;
        for (var i = 1; i < coef.Count; i++) s += i * coef[i] * Math.Pow(t, i - 1);
        return s;
    }

    private static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}

/// <summary>拿这一炉的配料算出来的一次饱和温度换算。</summary>
public sealed class SaturationCase
{
    /// <summary>溶质是哪一行。</summary>
    public required ChargeLine Solute { get; init; }

    /// <summary>用的是哪一行当溶剂。</summary>
    public ChargeLine? Solvent { get; init; }

    /// <summary>溶质质量 g（按应称量算，不是实投）。</summary>
    public double? SoluteMass { get; init; }

    /// <summary>溶剂体积 mL。</summary>
    public double? SolventVolume { get; init; }

    /// <summary>浓度 g/100 mL。</summary>
    public double? Concentration { get; init; }

    public SaturationResult Result { get; init; } = SaturationResult.No("还没算");

    public bool Ok => Result.Ok;

    /// <summary>一句话说清这是拿什么算的，好让人复核。</summary>
    public string Basis
    {
        get
        {
            if (SoluteMass is not { } m || SolventVolume is not { } v || Concentration is not { } c)
                return "";
            return $"{Solute.Item.Name} {F(m)} g ÷ {Solvent?.Item.Name} {F(v)} mL = "
                   + $"{F(c)} g/100 mL（按应称量算，不是实投）";
        }
    }

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>
/// 把配料表接到溶解度曲线上。
///
/// 饱和温度是降温结晶整套参数的起点：溶清温度设在它上面几度，结晶终点设在它下面。
/// 从前这一步全靠人查表换算，而**换算要的两个数（溶质质量、溶剂体积）配料表里都有**。
/// </summary>
public static class ChargeSaturation
{
    /// <summary>水的 CAS。</summary>
    public const string WaterCas = "7732-18-5";

    /// <summary>
    /// 认得出来的水。**不做包含匹配**——「水杨酸」「水合肼」里都有个水字，
    /// 认错一个就是拿水的曲线去算别的体系。
    /// </summary>
    private static readonly string[] WaterNames =
    {
        "水", "纯水", "纯化水", "去离子水", "蒸馏水", "注射用水", "超纯水",
        "water", "purifiedwater", "distilledwater", "diwater", "h2o", "h₂o"
    };

    public static bool IsWater(ChargeItem item)
        => string.Equals(item.Cas, WaterCas, StringComparison.Ordinal)
           || WaterNames.Contains(Squeeze(item.Name), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 这一炉里能算饱和温度的组分。
    ///
    /// 只有**带溶解度曲线**的组分才算得了；溶剂必须是水，不是水就照实拒绝。
    /// 一条都算不出来时返回空表——界面上那一块就整个不出现，不摆一个「—」在那儿。
    /// </summary>
    public static IReadOnlyList<SaturationCase> Of(ChargeResult charge)
    {
        var list = new List<SaturationCase>();

        var solvents = charge.Lines.Where(l => l.Item.Role == ChargeRole.Solvent).ToList();
        var solutes = charge.Lines
            .Where(l => l.Item.Role != ChargeRole.Solvent)
            .Where(l => l.Reference?.Solubility is { Length: >= 2 })
            .ToList();
        if (solutes.Count == 0) return list;

        // 一行溶剂都还没有：**闭嘴**。表刚填了第一行就摆出一块来说「没有溶剂」，
        // 那不是问题，是表还没填完——而界面上一块红字会让人以为自己弄错了什么。
        //
        // 唯一的例外是「有一行看着就是水，只是角色没标成溶剂」：那是一个具体的、
        // 改一下就好的疏漏，说出来是帮忙。
        if (solvents.Count == 0)
        {
            var water = charge.Lines.FirstOrDefault(l => l.Item.Role != ChargeRole.Product
                                                         && IsWater(l.Item));
            if (water is null) return list;
            return new[]
            {
                new SaturationCase
                {
                    Solute = solutes[0],
                    Result = SaturationResult.No(
                        $"「{water.Item.Name}」这一行的角色是「{ChargeWords.Of(water.Item.Role)}」，"
                        + "改成「溶剂」就能按这一炉的浓度算饱和温度。")
                }
            };
        }

        foreach (var solute in solutes)
        {
            var one = Evaluate(solute, solvents);
            if (one is not null) list.Add(one);
        }
        return list;
    }

    private static SaturationCase? Evaluate(ChargeLine solute, IReadOnlyList<ChargeLine> solvents)
    {
        // 混合溶剂：水的曲线只对纯水成立。两种溶剂一掺，曲线就不是这一条了
        var notWater = solvents.Where(s => !IsWater(s.Item)).ToList();
        if (notWater.Count > 0)
            return new SaturationCase
            {
                Solute = solute,
                Solvent = solvents[0],
                Result = SaturationResult.No(
                    "库里那条溶解度曲线是水里的（g/100 mL 水），而这一炉的溶剂是"
                    + string.Join("、", notWater.Select(s => "「" + s.Item.Name + "」"))
                    + "，换算不了。拿水的曲线去算别的溶剂，会得到一个看着很合理的错温度。")
            };

        var volume = solvents.Sum(s => s.Volume ?? 0);
        if (volume <= 0)
            return new SaturationCase
            {
                Solute = solute,
                Solvent = solvents[0],
                Result = SaturationResult.No($"溶剂「{solvents[0].Item.Name}」还算不出体积"
                                             + (solvents[0].Missing.Count > 0
                                                ? "（" + string.Join("、", solvents[0].Missing) + "）"
                                                : "") + "，没有体积就没有浓度。")
            };

        if (solute.Mass is not { } mass || mass <= 0)
            return new SaturationCase
            {
                Solute = solute,
                Solvent = solvents[0],
                SolventVolume = volume,
                Result = SaturationResult.No($"「{solute.Item.Name}」还算不出应称量"
                                             + (solute.Missing.Count > 0
                                                ? "（" + string.Join("、", solute.Missing) + "）"
                                                : "") + "。")
            };

        var c = mass / volume * 100;
        return new SaturationCase
        {
            Solute = solute,
            Solvent = solvents[0],
            SoluteMass = mass,
            SolventVolume = volume,
            Concentration = c,
            Result = Solubility.Saturate(solute.Reference!.Solubility, c)
        };
    }

    private static string Squeeze(string s)
        => s.Replace(" ", "").Replace("　", "").Trim();
}
