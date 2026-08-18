using Tec.Core.Catalog;
using Tec.Core.Recipes;
using Tec.Driver.Abi;

namespace Tec.Core.Chemistry;

/// <summary>一条「加料」步骤和配料表里那一行的对应关系。</summary>
public sealed class ChargeLinkEntry
{
    /// <summary>步骤在配方里的序号（0 起）。只用来在提示语里说「第 N 步」。</summary>
    public required int StepIndex { get; init; }
    public required Step Step { get; init; }
    /// <summary>步骤里填的料液名。</summary>
    public required string Liquid { get; init; }
    /// <summary>对上的那一行。null = 既没有引用也没有同名组分。</summary>
    public ChargeLine? Line { get; init; }

    /// <summary>步骤里存的配料行引用（行的 Id）。空 = 没建过引用，靠名字对。</summary>
    public string ChemId { get; init; } = "";

    /// <summary>体积是否跟随配料表。人手改过体积就是 false（已脱离计算）。</summary>
    public bool Linked { get; init; }

    /// <summary>是按引用对上的（不是按名字）。改了组分名也断不了。</summary>
    public bool MatchedById { get; init; }

    /// <summary>建过引用，但那一行已经不在配料表里（被删了）。</summary>
    public bool RefGone => ChemId.Length > 0 && Line is null;

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

/// <summary>「应用到加料步骤」干了什么：改了哪些体积、给哪几步新建了引用。</summary>
public sealed class ChargeApplyResult
{
    public required IReadOnlyList<(ChargeLinkEntry Entry, double Before, double After)> Volumes { get; init; }
    public required IReadOnlyList<ChargeLinkEntry> NewLinks { get; init; }
    public bool IsEmpty => Volumes.Count == 0 && NewLinks.Count == 0;
}

/// <summary>
/// 把配料表和配方里的「加料」步骤对起来。
///
/// **凭据先是引用，再是名字**（CH-4.1）：建立过引用的步骤持有配料行的 Id，
/// 改了组分名也断不了；没建过引用的老配方按料液名对——存量配方不能一夜全断。
/// 引用不是一层人看不见的 Id：界面上它就表现为「这一步连着配料表的哪一行」，
/// 名字仍然写在步骤里当显示与兜底。
///
/// **引用了就跟随**（CH-4.2）：linked 步骤的体积随配料表变（<see cref="Follow"/>，
/// 走快照与审计，撤销拉得回来）；人手改体积即脱离（<see cref="Detach"/>），
/// 脱离状态由校验条喊出来。步骤里始终存显式数值——下发执行的那个数
/// 必须明晃晃躺在步骤里，绝不做「运行时才去配料表取数」的隐式引用。
/// </summary>
public static class ChargeLink
{
    /// <summary>加料步骤里放料液名的那个参数键。</summary>
    public const string LiquidKey = "liq";

    /// <summary>加料体积那个参数键。</summary>
    public const string VolumeKey = "vol";

    /// <summary>配料行引用（行 Id）的参数键。空 / 缺 = 没建过引用。</summary>
    public const string ChemKey = "chemId";

    /// <summary>「体积跟随配料表」标志位的参数键。</summary>
    public const string LinkedKey = "linked";

    public static IReadOnlyList<ChargeLinkEntry> Match(Recipe recipe, ICommandCatalog catalog,
                                                       ChargeResult charge)
    {
        var list = new List<ChargeLinkEntry>();
        var byName = new Dictionary<string, ChargeLine>(StringComparer.OrdinalIgnoreCase);
        var byId = new Dictionary<string, ChargeLine>(StringComparer.Ordinal);
        foreach (var l in charge.Lines)
        {
            // 产物不投料，不参与加料步骤的匹配
            if (l.Item.Role == ChargeRole.Product) continue;
            byId.TryAdd(l.Item.Id, l);
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
            var chemId = s.Parameters.Str(ChemKey);
            ChargeLine? line = null;
            var byIdHit = false;
            if (chemId.Length > 0)
            {
                // 建过引用的只认引用。行被删了就是断了——退回按名对的话，
                // 可能悄悄对上一个恰好同名的新行，那比明说「断了」危险得多
                byIdHit = byId.TryGetValue(chemId, out line);
            }
            else if (byName.TryGetValue(Key(liq), out var named))
            {
                line = named;
            }

            list.Add(new ChargeLinkEntry
            {
                StepIndex = i,
                Step = s,
                Liquid = liq,
                Line = line,
                ChemId = chemId,
                Linked = s.Parameters.Flag(LinkedKey),
                MatchedById = byIdHit,
                StepVolume = s.Parameters.Has(VolumeKey) ? s.Parameters.Num(VolumeKey) : 0
            });
        }
        return list;
    }

    /// <summary>
    /// 「应用到加料步骤」：对上的步骤**建立引用**（写行 Id + 跟随标志，名字对齐到行名），
    /// 体积不一致的填成算出来的值。这是人按出来的动作；建立之后，跟随由
    /// <see cref="Follow"/> 自动做——「明确的动作」从每次同步收缩成建立引用这一次，
    /// 之后的跟随是引用语义本身。改了哪些体积、新连了哪几步，都要说得出来。
    /// </summary>
    public static ChargeApplyResult Apply(IReadOnlyList<ChargeLinkEntry> links)
    {
        var volumes = new List<(ChargeLinkEntry, double, double)>();
        var newLinks = new List<ChargeLinkEntry>();
        foreach (var e in links)
        {
            if (e.Line is not { } line) continue;

            if (e.ChemId != line.Item.Id || !e.Linked)
            {
                e.Step.Parameters[ChemKey] = line.Item.Id;
                e.Step.Parameters[LinkedKey] = true;
                // 名字对齐到配料行：引用建立后行名是权威，两边不再各叫各的
                if (line.Item.Name.Length > 0) e.Step.Parameters[LiquidKey] = line.Item.Name;
                newLinks.Add(e);
            }

            if (e.PlannedVolume is not { } want || want <= 0) continue;
            if (!e.Differs) continue;
            var before = e.StepVolume;
            // 泵的刻度到 0.01 mL 就够了；不圆一下会往配方里塞 24.60000000000000142
            e.Step.Parameters[VolumeKey] = Math.Round(want, 2);
            volumes.Add((e, before, Math.Round(want, 2)));
        }
        return new ChargeApplyResult { Volumes = volumes, NewLinks = newLinks };
    }

    /// <summary>这一步要不要跟随更新：引用着、跟随着、算得出体积、而且真的不一样。</summary>
    public static bool NeedsFollow(ChargeLinkEntry e)
        => e.Linked && e.MatchedById && e.PlannedVolume is > 0 && e.Differs;

    /// <summary>
    /// 跟随：把 linked 步骤的体积改成配料表算出来的值。只动建立过引用且在跟随的步骤——
    /// 按名字对上的、已脱离的、算不出体积的一概不碰。调用方要把这件事包进
    /// 快照与审计（改配方参数无论出自谁手都得留痕），改了哪几步由返回值说。
    /// </summary>
    public static IReadOnlyList<(ChargeLinkEntry Entry, double Before, double After)> Follow(
        IReadOnlyList<ChargeLinkEntry> links)
    {
        var done = new List<(ChargeLinkEntry, double, double)>();
        foreach (var e in links)
        {
            if (!NeedsFollow(e)) continue;
            var before = e.StepVolume;
            var after = Math.Round(e.PlannedVolume!.Value, 2);
            e.Step.Parameters[VolumeKey] = after;
            done.Add((e, before, after));
        }
        return done;
    }

    /// <summary>
    /// 人手改了加料体积：跟随标志清掉（已脱离计算）。引用还在——
    /// 校验条会提示手填值与算出值差多少，要恢复跟随再按一次「应用到加料步骤」。
    /// 返回 false = 本来就没在跟随，什么也没改。
    /// </summary>
    public static bool Detach(Step step)
    {
        if (!step.Parameters.Flag(LinkedKey)) return false;
        step.Parameters[LinkedKey] = false;
        return true;
    }

    /// <summary>名字比对前先规整一下：两头空白、全角空格都不该让两个「甲苯」对不上。</summary>
    private static string Key(string? name)
        => (name ?? "").Replace("　", " ").Trim();
}
