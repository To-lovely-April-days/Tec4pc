using Tec.Core.Compounds;

namespace Tec.Core.Chemistry;

/// <summary>算完的一行。输入那一行照原样带着，好让界面和报告一起排。</summary>
public sealed class ChargeLine
{
    public required ChargeItem Item { get; init; }

    /// <summary>连上的那条化合物。null = 没连库，或者库里没有这个键。</summary>
    public Compound? Reference { get; init; }

    public bool IsLimiting { get; init; }

    /// <summary>物质的量 mmol。**是纯物质的量**，不是称出来那坨料的量。</summary>
    public double? Moles { get; set; }

    /// <summary>应称量 g。含纯度校正：料不纯，就得多称一些才够那么多物质的量。</summary>
    public double? Mass { get; set; }

    /// <summary>应量取的体积 mL。</summary>
    public double? Volume { get; set; }

    /// <summary>相对限制试剂的当量。按当量给的行就是填进去那个数，其余行是倒算出来的。</summary>
    public double? Equivalents { get; set; }

    /// <summary>算的时候用的那几个数，报告里要写出来——不然没法复核。</summary>
    public double? MwUsed { get; set; }
    public double? DensityUsed { get; set; }
    public double? PurityUsed { get; set; }

    /// <summary>理论产量 g。只有目标产物行有。</summary>
    public double? TheoreticalMass { get; set; }

    /// <summary>收率 %。理论产量和实际产量都有才算得出来。</summary>
    public double? Yield { get; set; }

    /// <summary>缺了什么，导致某个数算不出来。**照实列出来，不拿 0 或者默认值顶上**。</summary>
    public List<string> Missing { get; } = new();

    /// <summary>按什么假设算的（纯度没填按 100 % 计之类）。算出来了，但得说一声。</summary>
    public List<string> Assumptions { get; } = new();

    /// <summary>实投与应称量差了多少 %。两个都有才算。</summary>
    public double? MassDeviation => Mass is > 0 && Item.ActualMass is { } a
        ? (a - Mass.Value) / Mass.Value * 100 : null;

    // ── 实投折算（CH-3.5）：称完回填之后反算回摩尔 ──────────────────

    /// <summary>实投折算的物质的量 mmol（按行上纯度折算）。实投没填就没有。</summary>
    public double? ActualMoles { get; set; }

    /// <summary>
    /// 实际当量：实投摩尔数相对**限制试剂实投**摩尔数（限制试剂没回填时相对计划量）。
    /// 质量偏差答不了这个问题——限制试剂自己也称偏了的时候，相对量才是真相。
    /// </summary>
    public double? ActualEquivalents { get; set; }

    /// <summary>相对计划当量的过量百分比：+3 = 摩尔上比计划多投了 3 %。</summary>
    public double? ExcessPercent { get; set; }
}

public sealed class ChargeResult
{
    public required IReadOnlyList<ChargeLine> Lines { get; init; }
    public ChargeLine? Limiting { get; init; }

    /// <summary>整张表的毛病：没有限制试剂、有两个、基准填错。</summary>
    public List<string> Problems { get; } = new();

    /// <summary>投料合计（不含目标产物）。</summary>
    public double? TotalMass { get; set; }
    public double? TotalVolume { get; set; }

    /// <summary>总体积超过釜容。</summary>
    public bool OverVessel { get; set; }

    /// <summary>
    /// 没进合计体积的那几行（没有密度，换不出体积）。
    /// 合计体积少算了谁必须说得出来——不然拿它跟釜容一对，得出的「装得下」是假的。
    /// </summary>
    public List<string> VolumeExcluded { get; } = new();

    /// <summary>
    /// 每 1 g 目标产物需要多少 g 限制试剂（按理论收率 100 % 计）。
    ///
    /// 放大批量本来就只是改限制试剂那一格的数——整张表都是相对它算的，改完全表自动跟着走。
    /// 这个数只是省掉那一次除法，**它不是「放大设计」**：传热面积与体积之比、搅拌雷诺数、
    /// 加料时长、放热速率都不随批量线性缩放，那些得人来判断。
    /// 空 = 没有目标产物行，或者缺摩尔质量。
    /// </summary>
    public double? LimitingPerProductGram { get; set; }

    /// <summary>
    /// 摩尔浓度 mol/L：限制试剂的物质的量 ÷ 溶剂总体积（CH-C7）。
    /// 任何一个溶剂行算不出体积就不给数——按打了折的溶剂体积算浓度，
    /// 得出的数偏高还看着可信，那比没有更糟。
    /// </summary>
    public double? Concentration { get; set; }

    /// <summary>收率按什么基准算的（实投还是计划）。打印收率时必须连着这句话。</summary>
    public string? YieldBasis { get; set; }

    /// <summary>一个数都没算出来（表是空的，或者基准立不起来）。</summary>
    public bool Any => Lines.Any(l => l.Moles is not null || l.Mass is not null || l.Volume is not null);
}

/// <summary>
/// 化学计量。**基准是「限制试剂定量，其余按当量算」**——
/// 限制试剂那一行给一个实打实的量（称多少克 / 量多少毫升 / 要多少毫摩尔），
/// 它定下这一炉的物质的量，别的行按当量或倍量跟着它走。
///
/// 三件事必须做对，否则这张表就是个摆设：
///
/// 1. **纯度校正**。65 % 的硝酸要拿到 10 mmol 的 HNO₃，得称 10 mmol 对应的质量
///    再除以 0.65。不校正的话每一步都少投三分之一。
/// 2. **密度换体积**。泵下发的是体积，配料算出来的是质量，中间只有密度。
///    密度没填就**算不出体积**，照实说，不许拿 1 g/mL 顶。但要分清两种：
///    按毫升 / 按倍量给量的行没有密度是**真挡住**（连应称量都算不出来），
///    按克称的固体没有密度只是不进合计体积——后者不该标红，那是一条正常的投料行。
/// 3. **缺什么就说缺什么**。摩尔质量空着，这一行的 mmol 和质量就都是空的。
///    随便填个数进去，人照着称，那是能出事的（§不伪造数据）。
/// </summary>
public static class Stoichiometry
{
    public static ChargeResult Solve(ChargeTable table, IReadOnlyList<Compound>? library = null)
    {
        var lib = Index(library);
        var lines = table.Items.Select(i => Start(i, lib)).ToList();

        var limits = lines.Where(l => l.Item.Role == ChargeRole.Limiting).ToList();
        var limiting = limits.FirstOrDefault();
        var result = new ChargeResult { Lines = lines, Limiting = limiting };

        if (table.Items.Count == 0) return result;

        if (limits.Count == 0)
            result.Problems.Add("没有指定限制试剂。整张表的当量都是相对它算的，"
                                + "所以先把定量的那一行的角色改成「限制试剂」。");
        else if (limits.Count > 1)
            result.Problems.Add($"有 {limits.Count} 行都标着「限制试剂」（"
                                + string.Join("、", limits.Select(l => Show(l.Item.Name)))
                                + "）。基准只能有一个。");

        // ── 基准行先算 ──────────────────────────────────────────────
        if (limiting is not null)
        {
            if (limiting.Item.Basis != ChargeBasis.Quantity)
                result.Problems.Add($"限制试剂「{Show(limiting.Item.Name)}」的基准是"
                                    + $"「{ChargeWords.Of(limiting.Item.Basis)}」——它是基准，"
                                    + "得直接给一个量（克 / 毫升 / 毫摩尔），不能拿自己当参照。");
            else
                Quantity(limiting);

            limiting.Equivalents = limiting.Moles is not null ? 1 : null;
        }

        var nLim = limiting?.Moles;
        var mLim = limiting?.Mass;

        // ── 其余各行 ────────────────────────────────────────────────
        foreach (var line in lines)
        {
            if (line.IsLimiting) continue;

            switch (line.Item.Basis)
            {
                case ChargeBasis.Quantity:
                    Quantity(line);
                    break;

                case ChargeBasis.Equivalents:
                    ByEquivalents(line, nLim);
                    break;

                case ChargeBasis.Volumes:
                    ByVolumes(line, mLim);
                    break;
            }

            // 直接给量的行也倒算一个当量出来——「这一步我到底加了几倍量」
            // 是配料表要回答的头一个问题，不该让人自己拿计算器除
            if (line.Item.Basis != ChargeBasis.Equivalents && line.Moles is { } n && nLim is > 0)
                line.Equivalents = n / nLim.Value;
        }

        // ── 实投折算（CH-3.5）：称完回填之后，实际投了多少摩尔 ────────
        foreach (var line in lines)
        {
            if (line.Item.Role == ChargeRole.Product) continue;
            if (line.Item.ActualMass is not { } got || line.MwUsed is not > 0) continue;
            // 与应称量同一套纯度折算：称出来那坨料里，纯物质只有 p % 那么多
            line.ActualMoles = got * ((line.PurityUsed ?? 100) / 100) / line.MwUsed.Value * 1000;
        }

        // 实际当量的基准：限制试剂**实投**优先——它自己也称偏了的时候，
        // 「相对它我到底投了几倍」才是化学上要回答的问题；没回填就退回计划量
        var nLimActual = limiting?.ActualMoles;
        if ((nLimActual ?? nLim) is { } nBase && nBase > 0)
            foreach (var line in lines)
            {
                if (line.IsLimiting || line.Item.Role == ChargeRole.Product) continue;
                if (line.ActualMoles is not { } an) continue;
                line.ActualEquivalents = an / nBase;
                if (line.Equivalents is > 0)
                    line.ExcessPercent = (line.ActualEquivalents.Value / line.Equivalents.Value - 1) * 100;
            }

        // ── 目标产物：理论产量与收率 ────────────────────────────────
        foreach (var line in lines.Where(l => l.Item.Role == ChargeRole.Product))
        {
            // 产物不投料，前面按投料那套算出来的东西对它没有意义，一并清掉：
            // 留着「应称量」会混进合计，留着「缺密度（换不出体积）」更糟——
            // 那是在替一个还没生成的东西挑毛病，读报告的人会去找一个不存在的问题
            line.Mass = null;
            line.Volume = null;
            line.Missing.RemoveAll(x => x.Contains("密度", StringComparison.Ordinal));
            line.Assumptions.RemoveAll(x => x.Contains("密度", StringComparison.Ordinal));

            var eq = line.Item.Basis == ChargeBasis.Equivalents ? line.Item.Amount ?? 1 : 1;
            if (nLim is null) { Need(line, "限制试剂的物质的量"); continue; }
            if (line.MwUsed is not > 0) { Need(line, "摩尔质量"); continue; }

            line.Moles = eq * nLim.Value;
            line.Equivalents = eq;
            // 理论产量按**计划**投料——它是排配料时的目标，印在「应称量」列
            line.TheoreticalMass = line.Moles.Value / 1000 * line.MwUsed.Value;

            // 收率的分母按限制试剂**实投**折算的摩尔数——限制试剂少称了 5 %，
            // 理论上限就低 5 %，拿计划量当分母会把一炉好收率说成差的。
            // 实投没回填只好按计划算，但必须把这层假设说出口
            var denom = eq * (nLimActual ?? nLim.Value) / 1000 * line.MwUsed.Value;
            if (line.Item.ActualMass is { } got && denom > 0)
            {
                line.Yield = got / denom * 100;
                if (nLimActual is null)
                    line.Assumptions.Add("限制试剂未回填实投，收率按计划投料量计");
            }
        }

        if (limiting is not null && nLim is not null)
            result.YieldBasis = nLimActual is { } a
                ? $"收率按限制试剂实投折算 {Num(a)} mmol 计"
                : "收率按限制试剂计划投料计（实投未回填）";

        // 每 1 g 产物需要多少限制试剂。放大批量时省一次除法——仅此而已
        var prod = lines.FirstOrDefault(l => l.Item.Role == ChargeRole.Product
                                             && l.TheoreticalMass is > 0);
        if (prod is not null && limiting?.Mass is > 0)
            result.LimitingPerProductGram = limiting.Mass.Value / prod.TheoreticalMass!.Value;

        // ── 合计。产物不算进去，它不投料 ────────────────────────────
        var charged = lines.Where(l => l.Item.Role != ChargeRole.Product).ToList();
        if (charged.Any(l => l.Mass is not null)) result.TotalMass = charged.Sum(l => l.Mass ?? 0);
        if (charged.Any(l => l.Volume is not null))
        {
            result.TotalVolume = charged.Sum(l => l.Volume ?? 0);
            // 有量、却没换出体积的那几行：合计体积把它们漏掉了，说出来
            foreach (var l in charged.Where(l => l.Volume is null && l.Mass is > 0))
                result.VolumeExcluded.Add(l.Item.Name.Length > 0 ? l.Item.Name : "（未命名）");
        }

        if (result.VolumeExcluded.Count > 0)
            result.Problems.Add("合计体积不含 " + string.Join("、", result.VolumeExcluded)
                                + "（没有密度，换不出体积）——拿它跟釜容对照时要把这几项算进去。");

        if (table.VesselVolume is > 0 && result.TotalVolume is { } v && v > table.VesselVolume)
        {
            result.OverVessel = true;
            result.Problems.Add($"各组分体积加起来 {Num(v)} mL，超过釜容 "
                                + $"{Num(table.VesselVolume.Value)} mL。");
        }
        // CH-5.6：占到八成就提醒。搅拌旋涡、放热鼓泡、加料冲击都要余量
        else if (table.VesselVolume is > 0 && result.TotalVolume is { } v8
                 && v8 > table.VesselVolume * 0.8)
            result.Problems.Add($"各组分体积加起来 {Num(v8)} mL，已占釜容 "
                                + $"{Num(table.VesselVolume.Value)} mL 的 "
                                + $"{v8 / table.VesselVolume.Value * 100:F0} %——超过 80 % 的经验红线，"
                                + "留出搅拌与放热的余量。");

        // ── 限制试剂的建议（CH-3.2）：**建议不代填**——替人改角色等于替人定工艺 ──
        if (limits.Count == 0)
        {
            // 没有基准时按当量的行都算不出数，能比的只有直接给量的试剂行
            var candidate = lines
                .Where(l => l.Item.Role == ChargeRole.Reagent && l.Moles is > 0)
                .OrderBy(l => l.Moles!.Value)
                .FirstOrDefault();
            if (candidate is not null)
                result.Problems.Add($"按已给的量，「{Show(candidate.Item.Name)}」的摩尔数最小"
                                    + $"（{Num(candidate.Moles!.Value)} mmol）——通常它就是限制试剂，"
                                    + "把那一行的角色改成「限制试剂」即可。");
        }
        else
        {
            // 有基准、但某个试剂投得比基准还少（当量 < 1）：按定义限制试剂其实是它。
            // 催化剂 / 淬灭那些当量天生小，不在此列
            foreach (var line in lines.Where(l => l.Item.Role == ChargeRole.Reagent
                                                  && l.Equivalents is { } e && e < 0.999))
                result.Problems.Add($"「{Show(line.Item.Name)}」的当量 {Num(line.Equivalents!.Value)} 小于 1"
                                    + "——按摩尔数它比标着「限制试剂」的那行还少，真正的限制试剂其实是它。"
                                    + "欠量投料若是有意设计，可忽略这条。");
        }

        // ── 摩尔浓度（CH-C7）：限制试剂 ÷ 溶剂总体积 ────────────────
        var solvents = lines.Where(l => l.Item.Role == ChargeRole.Solvent).ToList();
        if (nLim is > 0 && solvents.Count > 0 && solvents.All(sv => sv.Volume is > 0))
            result.Concentration = nLim.Value / solvents.Sum(sv => sv.Volume!.Value);
            // mmol ÷ mL 正好就是 mol/L，不用再换

        return result;
    }

    // ── 三种基准 ────────────────────────────────────────────────────

    /// <summary>直接给量：把给的那个量换成 mmol / g / mL 三样里能换的。</summary>
    private static void Quantity(ChargeLine line)
    {
        var it = line.Item;
        if (it.Amount is not { } amount)
        {
            Need(line, $"给定量（{ChargeWords.Of(it.Unit)}）");
            return;
        }
        if (amount < 0) { line.Missing.Add("给的量是负数"); return; }

        var purity = Purity(line);

        switch (it.Unit)
        {
            case ChargeUnit.Gram:
                line.Mass = amount;
                line.Volume = Div(amount, line.DensityUsed, line);
                line.Moles = Mol(amount, purity, line);
                break;

            case ChargeUnit.Milliliter:
                line.Volume = amount;
                if (line.DensityUsed is > 0)
                {
                    line.Mass = amount * line.DensityUsed.Value;
                    line.Moles = Mol(line.Mass.Value, purity, line);
                }
                else Need(line, "密度（体积换不出质量）");
                break;

            case ChargeUnit.Millimole:
                line.Moles = amount;
                // 反着来：要这么多物质的量，料只有这个纯度，那就得称这么多
                if (line.MwUsed is > 0)
                {
                    line.Mass = amount / 1000 * line.MwUsed.Value / (purity / 100);
                    line.Volume = Div(line.Mass.Value, line.DensityUsed, line);
                }
                else Need(line, "摩尔质量");
                break;
        }
    }

    /// <summary>按当量：跟着限制试剂的物质的量走。</summary>
    private static void ByEquivalents(ChargeLine line, double? nLim)
    {
        if (line.Item.Amount is not { } eq) { Need(line, "当量"); return; }
        line.Equivalents = eq;

        if (nLim is null) { Need(line, "限制试剂的物质的量"); return; }
        if (line.MwUsed is not > 0) { Need(line, "摩尔质量"); return; }

        var purity = Purity(line);
        line.Moles = eq * nLim.Value;
        // **这里就是纯度校正**：要 n mmol 纯的，料只有 p %，得称 n·M/p
        line.Mass = line.Moles.Value / 1000 * line.MwUsed.Value / (purity / 100);
        line.Volume = Div(line.Mass.Value, line.DensityUsed, line);
    }

    /// <summary>按倍量（V/W）：每 1 g 限制试剂用几 mL。溶剂的常规写法。</summary>
    private static void ByVolumes(ChargeLine line, double? mLim)
    {
        if (line.Item.Amount is not { } k) { Need(line, "倍量"); return; }
        if (mLim is not > 0) { Need(line, "限制试剂的投料质量"); return; }

        line.Volume = k * mLim.Value;
        if (line.DensityUsed is > 0)
        {
            line.Mass = line.Volume.Value * line.DensityUsed.Value;
            line.Moles = Mol(line.Mass.Value, Purity(line), line);
        }
        else Need(line, "密度（体积换不出质量）");
    }

    // ── 小工具 ──────────────────────────────────────────────────────

    private static ChargeLine Start(ChargeItem item, Dictionary<string, Compound> lib)
    {
        var refc = item.Cas.Length > 0 && lib.TryGetValue(item.Cas, out var c) ? c : null;
        // 盖过章的行是**自足**的：物性在连库那一刻已经拷在行上，空的字段就是
        // 「快照时库里也没有」，不再回头看活库。不然日后有人往库里补一条密度，
        // 这行又开始跟着库漂——快照就白盖了。要吃到库里的新值，走「刷新库值」。
        var frozen = item.SnapshotAt is not null;
        var line = new ChargeLine
        {
            Item = item,
            Reference = refc,
            IsLimiting = item.Role == ChargeRole.Limiting,
            // 行上写了的赢：手里这瓶 65 % 的硝酸，不能因为库里那条写着 68 % 就按 68 % 算
            MwUsed = item.Mw ?? (frozen ? null : refc?.Mw),
            DensityUsed = item.Density ?? (frozen ? null : refc?.Density),
            PurityUsed = item.Purity ?? (frozen ? null : refc?.Purity)
        };
        if (item.Cas.Length > 0 && refc is null)
            line.Missing.Add($"化合物库里找不到 {item.Cas}");

        // 没盖章的连库行走到回退这条路（行上没有、从库里现取）的每一个数都要点名——
        // 现取的值跟着库漂，历史记录里出现这种行就说明它早于快照机制，复核的人必须知道
        if (!frozen)
        {
            if (item.Mw is null && refc?.Mw is not null)
                line.Assumptions.Add("摩尔质量取自化合物库当前值（此行未快照）");
            if (item.Density is null && refc?.Density is not null)
                line.Assumptions.Add("密度取自化合物库当前值（此行未快照）");
            if (item.Purity is null && refc?.Purity is not null)
                line.Assumptions.Add("纯度取自化合物库当前值（此行未快照）");
        }
        return line;
    }

    private static Dictionary<string, Compound> Index(IReadOnlyList<Compound>? lib)
    {
        var d = new Dictionary<string, Compound>(StringComparer.Ordinal);
        if (lib is null) return d;
        foreach (var c in lib) d[c.Cas] = c;
        return d;
    }

    /// <summary>纯度。没填就按 100 % 算，但**必须说一声**——不说就等于替这瓶料担保。</summary>
    private static double Purity(ChargeLine line)
    {
        if (line.PurityUsed is not { } p)
        {
            line.Assumptions.Add("纯度没填，按 100 % 计");
            return 100;
        }
        if (p <= 0)
        {
            line.Missing.Add($"纯度 {Num(p)} % 不是个能用的数");
            line.PurityUsed = null;
            line.Assumptions.Add("纯度按 100 % 计");
            return 100;
        }
        return p;
    }

    private static double? Mol(double grams, double purity, ChargeLine line)
    {
        if (line.MwUsed is not > 0) { Need(line, "摩尔质量"); return null; }
        return grams * (purity / 100) / line.MwUsed.Value * 1000;
    }

    /// <summary>
    /// 质量换体积。**换不出来不算缺东西**：按克称的固体本来就不需要密度，
    /// 这一行的 mmol 和应称量一个不少，只是它不进合计体积。
    ///
    /// 从前这里记成「缺密度」并标红，于是一条正常的固体投料行看着像出了错——
    /// 红字要留给真的挡住了计算的情况（按毫升给量、按倍量给量的行没有密度，
    /// 那是连应称量都算不出来）。
    /// </summary>
    private static double? Div(double grams, double? density, ChargeLine line)
    {
        if (density is > 0) return grams / density.Value;
        line.Assumptions.Add("没有密度，算不出体积，这一行不计入合计体积");
        return null;
    }

    private static void Need(ChargeLine line, string what)
    {
        var text = "缺" + what;
        if (!line.Missing.Contains(text, StringComparer.Ordinal)) line.Missing.Add(text);
    }

    private static string Show(string name) => name.Length > 0 ? name : "未命名";

    private static string Num(double v) => Fmt.Num(v);
}
