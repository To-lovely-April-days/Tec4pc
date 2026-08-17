using System.Globalization;
using Tec.Core.Catalog;
using Tec.Core.Data;
using Tec.Core.Records;
using Tec.Driver.Abi;

namespace Tec.Core.Chemistry;

/// <summary>一条加料步骤实际交付了多少。</summary>
public sealed class DosedStep
{
    public required int Index { get; init; }
    /// <summary>步骤里填的料液名。跟配料表按它对上。</summary>
    public required string Liquid { get; init; }
    /// <summary>配方里计划加的体积 mL。</summary>
    public required double Planned { get; init; }
    /// <summary>泵实际送出去的体积 mL。null = 读不出来，看 <see cref="Problem"/>。</summary>
    public double? Actual { get; init; }
    /// <summary>读不出来的原因，人话。</summary>
    public string? Problem { get; init; }

    /// <summary>实际比计划差多少 %。</summary>
    public double? Deviation => Planned > 0 && Actual is { } a ? (a - Planned) / Planned * 100 : null;
}

/// <summary>
/// 从运行记录里读回「这一步实际泵了多少」。
///
/// 配料表里的「实投」从前只能人手填。而泵的累计加料量（`volume` 这个标签）
/// 一直在采、也进了归档——**计划与实际在物料这一侧本来就能闭环**，缺的只是接上。
///
/// 读的是采样而不是步骤记录里那句「实加 12.40 mL」：那是一句给人看的话，
/// 措辞一改这里就悄悄失灵；采样是数，而且任何会发这个标签的驱动都适用，
/// 包括以后接的真机泵。
/// </summary>
public static class DoseReadback
{
    /// <summary>泵的累计加料量标签。</summary>
    public const string VolumeTag = "volume";

    /// <summary>
    /// 这一路这一炉的每一条加料步骤实际加了多少。
    ///
    /// 累计量是**每个驱动会话各自累加**的：中途重开会话会归零，那一步的差值就成了负数。
    /// 这种情况照实说读不出来，不拿绝对值糊过去——负的差值背后是「泵重启过」，
    /// 那正是操作人该知道的事。
    /// </summary>
    /// <param name="samplesTruncated">
    /// 这一炉的早期采样被环形缓冲顶掉过（归档时会记这一笔）。
    /// **这个标志决定了「序列开头之前算 0 还是算不知道」**——泵在开始加料之前
    /// 根本不发这个标签，所以「第一条加料步骤之前没有采样点」是正常的，那时累计量就是 0；
    /// 但采样被顶掉过的时候看起来一模一样，那时候当成 0 会算出一个大得离谱的实加量。
    /// 两者从数据本身分不出来，只能由知道的人告诉它。
    /// </param>
    public static IReadOnlyList<DosedStep> Of(ChannelRun run, ISampleSource samples,
                                              ICommandCatalog catalog, bool samplesTruncated = false)
    {
        var list = new List<DosedStep>();
        var snap = samples.Snapshot(run.Channel, VolumeTag);

        var dosing = run.Steps
            .Where(s => catalog.TryGet(s.CommandId, out var d) && d.RequiredCapability == typeof(IDosing))
            .OrderBy(s => s.Index)
            .ToList();

        for (var i = 0; i < dosing.Count; i++)
        {
            var s = dosing[i];
            var step = run.Baseline.Recipe.Steps.FirstOrDefault(x => x.StepId == s.StepId);
            var liq = step?.Parameters.Str(ChargeLink.LiquidKey) ?? "";
            var planned = step?.Parameters.Num(ChargeLink.VolumeKey) ?? 0;

            // 读到**下一条加料步骤开始之前**为止。累计量只在加料的时候动，
            // 两条加料之间它是平的，所以把窗口开到那儿不会多算别人的量；
            // 而泵的最后一次上报常常落在这一步结束之后那一瞬——只读到 ActualEnd
            // 会把尾巴丢掉（实测：计划 12 mL 只读回 8 mL）
            var boundary = dosing.Skip(i + 1).Select(x => x.ActualStart).FirstOrDefault(x => x is not null)
                           ?? run.FinishedAt
                           ?? (snap.Length > 0 ? snap[^1].WallClock : (DateTimeOffset?)null);

            list.Add(Measure(s, liq, planned, snap, boundary, samplesTruncated));
        }
        return list;
    }

    private static DosedStep Measure(StepRecord s, string liquid, double planned,
                                     IReadOnlyList<Sample> snap, DateTimeOffset? boundary,
                                     bool truncated)
    {
        DosedStep No(string why) => new()
        { Index = s.Index, Liquid = liquid, Planned = planned, Problem = why };

        if (s.ActualStart is not { } from) return No("这一步没有跑起来");
        var to = s.ActualEnd ?? from;
        if (boundary is { } bound && bound > to) to = bound;
        if (snap.Count == 0) return No("这一炉没有采到泵的累计加料量");

        // 起点。**泵在开始加料之前根本不发这个标签**——第一条加料步骤开跑时
        // 序列里一个点都还没有，那时候累计量就是 0（它本来就是从 0 累起来的）。
        // 采样被顶掉过的时候看起来一模一样，但那时候当成 0 会算出一个大得离谱的量，
        // 所以那种情况照实说读不出来（见 samplesTruncated 那个参数的说明）
        var a = Before(snap, from);
        if (a is null)
        {
            if (truncated)
                return No($"这一步开始时的累计量没采到（现存最早的一点是 {F(snap[0].Value)} mL），"
                          + "归档时早期采样被环形缓冲顶掉了，这一步读不出实际量");
            a = 0;
        }

        var b = Before(snap, to);
        if (b is null) return No("这一步结束时的累计量没采到");

        // **整段扫一遍，不只看两端**：累计量是每个驱动会话各自累加的，
        // 中途重开会话会归零。只比两端的话，「0 → 8 → 重开 → 2」看起来就像老实加了 2 mL
        double? prev = null;
        foreach (var x in snap)
        {
            if (x.WallClock < from || x.WallClock > to) continue;
            if (prev is { } p && x.Value < p - 1e-9)
                return No($"累计加料量在这一步里回退了（{F(p)} → {F(x.Value)} mL），"
                          + "多半是加料泵的会话中途重开过，这一步读不出实际量");
            prev = x.Value;
        }

        var delta = b.Value - a.Value;
        if (delta < 0)
            return No($"累计加料量在这一步里回退了（{F(a.Value)} → {F(b.Value)} mL），"
                      + "多半是加料泵的会话中途重开过，这一步读不出实际量");

        return new DosedStep { Index = s.Index, Liquid = liquid, Planned = planned, Actual = delta };
    }

    /// <summary>那一刻之前最后一个采样点。一个都没有就是 null。</summary>
    private static double? Before(IReadOnlyList<Sample> snap, DateTimeOffset at)
    {
        double? last = null;
        foreach (var x in snap)
        {
            if (x.WallClock > at) break;
            last = x.Value;
        }
        return last;
    }

    /// <summary>
    /// 把读回来的量填进配料表的「实取」一栏，返回填了哪几行。
    ///
    /// **按料液名对应**，跟「应用到加料步骤」同一套凭据。同一个组分被分几步加的，
    /// 几步加起来算一次（分批加料就是这么写的）。
    /// 密度知道就顺带把实投质量也填上——称量记录要的是克。
    /// </summary>
    public static IReadOnlyList<(string Name, double Volume, double? Mass)> Apply(
        ChargeTable table, IReadOnlyList<DosedStep> steps, ChargeResult charge)
    {
        var done = new List<(string, double, double?)>();

        // 同名的几步先并起来：一个组分分三批加，实取是三批之和
        var byName = steps
            .Where(s => s.Actual is > 0)
            .GroupBy(s => Key(s.Liquid), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(s => s.Actual!.Value), StringComparer.OrdinalIgnoreCase);

        foreach (var line in charge.Lines)
        {
            if (line.Item.Role == ChargeRole.Product) continue;
            if (!byName.TryGetValue(Key(line.Item.Name), out var volume)) continue;

            var item = table.Items.FirstOrDefault(i => i.Id == line.Item.Id);
            if (item is null) continue;

            item.ActualVolume = Math.Round(volume, 3);
            double? mass = line.DensityUsed is > 0 ? Math.Round(volume * line.DensityUsed.Value, 4) : null;
            if (mass is not null) item.ActualMass = mass;
            done.Add((item.Name, item.ActualVolume.Value, mass));
        }
        return done;
    }

    private static string Key(string? name) => (name ?? "").Replace("　", " ").Trim();

    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
