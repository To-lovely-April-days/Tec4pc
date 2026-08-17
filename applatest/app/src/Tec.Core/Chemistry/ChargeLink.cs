using Tec.Core.Catalog;
using Tec.Core.Recipes;
using Tec.Driver.Abi;

namespace Tec.Core.Chemistry;

/// <summary>一条「加料」步骤和配料表里那一行的对应关系。</summary>
public sealed class ChargeLinkEntry
{
    /// <summary>步骤在配方里的序号（0 起）。</summary>
    public required int StepIndex { get; init; }
    public required Step Step { get; init; }
    /// <summary>步骤里填的料液名。</summary>
    public required string Liquid { get; init; }
    /// <summary>对上的那一行。null = 配料表里没有这个名字。</summary>
    public ChargeLine? Line { get; init; }

    /// <summary>步骤里现在填的加料体积 mL。</summary>
    public double StepVolume { get; init; }

    /// <summary>配料表算出来的体积 mL。null = 算不出来（缺密度之类）。</summary>
    public double? PlannedVolume => Line?.Volume;

    public bool Matched => Line is not null;

    /// <summary>两个体积差得超过 0.5 %（且绝对差超过 0.05 mL）就算对不上。</summary>
    public bool Differs
    {
        get
        {
            if (PlannedVolume is not { } want || want <= 0) return false;
            var gap = Math.Abs(StepVolume - want);
            return gap > 0.05 && gap / want > 0.005;
        }
    }
}

/// <summary>
/// 把配料表和配方里的「加料」步骤对起来。
///
/// **凭据是料液名**：加料步骤里那个「料液」填的字，跟配料表里组分名对得上就算一条。
/// 用名字而不是内部编号，是因为这两样东西本来就是同一个概念的两个说法——
/// 配料表说「甲苯 24.6 mL」，配方说「加料泵 1 加入甲苯」，中间不该再有一层人看不见的 Id。
///
/// 代价是改了名字就对不上了。所以对不上的**一律喊出来**（校验条上一条警告），
/// 而不是默默不联动——默默不联动的后果是人以为体积会跟着配料表变，其实没变。
/// </summary>
public static class ChargeLink
{
    /// <summary>加料步骤里放料液名的那个参数键。</summary>
    public const string LiquidKey = "liq";

    /// <summary>加料体积那个参数键。</summary>
    public const string VolumeKey = "vol";

    public static IReadOnlyList<ChargeLinkEntry> Match(Recipe recipe, ICommandCatalog catalog,
                                                       ChargeResult charge)
    {
        var list = new List<ChargeLinkEntry>();
        var byName = new Dictionary<string, ChargeLine>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in charge.Lines)
        {
            // 产物不投料，不参与加料步骤的匹配
            if (l.Item.Role == ChargeRole.Product) continue;
            var key = Key(l.Item.Name);
            if (key.Length > 0) byName.TryAdd(key, l);
        }

        for (var i = 0; i < recipe.Steps.Count; i++)
        {
            var s = recipe.Steps[i];
            if (!catalog.TryGet(s.CommandId, out var d)) continue;
            if (d.RequiredCapability != typeof(IDosing)) continue;
            if (!s.Parameters.Has(LiquidKey)) continue;

            var liq = s.Parameters.Str(LiquidKey);
            list.Add(new ChargeLinkEntry
            {
                StepIndex = i,
                Step = s,
                Liquid = liq,
                Line = byName.TryGetValue(Key(liq), out var line) ? line : null,
                StepVolume = s.Parameters.Has(VolumeKey) ? s.Parameters.Num(VolumeKey) : 0
            });
        }
        return list;
    }

    /// <summary>
    /// 把算出来的体积填进对上的那些加料步骤，返回改了哪几步、从多少改成多少。
    ///
    /// **是一个明确的动作，不是自动联动**：改配方参数这件事得由人按一下——
    /// 悄悄跟着配料表变，等于有人在操作人没看的时候改了工艺。
    /// 返回的清单要能落进审计与撤销栈，所以「改了哪几步」必须说得出来。
    /// </summary>
    public static IReadOnlyList<(ChargeLinkEntry Entry, double Before, double After)> Apply(
        IReadOnlyList<ChargeLinkEntry> links)
    {
        var done = new List<(ChargeLinkEntry, double, double)>();
        foreach (var e in links)
        {
            if (e.PlannedVolume is not { } want || want <= 0) continue;
            if (!e.Differs) continue;
            var before = e.StepVolume;
            // 泵的刻度到 0.01 mL 就够了；不圆一下会往配方里塞 24.60000000000000142
            e.Step.Parameters[VolumeKey] = Math.Round(want, 2);
            done.Add((e, before, Math.Round(want, 2)));
        }
        return done;
    }

    /// <summary>名字比对前先规整一下：两头空白、全角空格都不该让两个「甲苯」对不上。</summary>
    private static string Key(string? name)
        => (name ?? "").Replace("　", " ").Trim();
}
