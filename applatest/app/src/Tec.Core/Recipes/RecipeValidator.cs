using Tec.Core.Benches;
using Tec.Core.Catalog;
using Tec.Core.Chemistry;
using Tec.Core.Scheduling;
using Tec.Driver.Abi;

namespace Tec.Core.Recipes;

public enum IssueLevel { Info, Warning, Error }

public sealed record ValidationIssue(IssueLevel Level, string Code, string Message)
{
    /// <summary>出问题的那一步的稳定标识。**不是数组下标**：插一步、删一步之后
    /// 下标整体错位，靠它指认的对象就全指歪了（CH-6.5 点名的正是这个）。
    /// 提示文案里的「第 N 步」只是给人看的说法，机器认的是这个。</summary>
    public string? StepId { get; init; }
    public int? Channel { get; init; }
}

/// <summary>
/// 保存 / 装载前跑一遍（§10.4）。目的是**提前**告诉操作人哪里不对，
/// 而不是跑到一半报错。
/// </summary>
public static class RecipeValidator
{
    public static IReadOnlyList<ValidationIssue> Validate(
        Recipe recipe, ICommandCatalog catalog, Channel? channel = null, EstimationContext? seed = null,
        ChargeResult? charge = null)
    {
        var issues = new List<ValidationIssue>();
        var depth = 0;
        var openAt = -1;

        for (var i = 0; i < recipe.Steps.Count; i++)
        {
            var s = recipe.Steps[i];

            if (!catalog.TryGet(s.CommandId, out var d))
            {
                issues.Add(new ValidationIssue(IssueLevel.Error, "missing-driver",
                    $"第 {i + 1} 步引用了未安装的指令 {s.CommandId}") { StepId = s.StepId });
                continue;
            }

            if (BuiltinCommands.IsLoopBegin(s.CommandId)) { depth++; if (openAt < 0) openAt = i; }
            if (BuiltinCommands.IsLoopEnd(s.CommandId))
            {
                depth--;
                if (depth < 0)
                {
                    issues.Add(new ValidationIssue(IssueLevel.Error, "loop-unbalanced",
                        $"第 {i + 1} 步的循环结束没有对应的循环开始") { StepId = s.StepId });
                    depth = 0;
                }
            }

            // 静态范围
            foreach (var f in d.Parameters.Fields)
            {
                if (f.Kind is not (FieldKind.Number or FieldKind.Duration)) continue;
                if (!s.Parameters.Has(f.Key)) continue;
                var v = s.Parameters.Num(f.Key);
                if (f.Min is { } min && v < min)
                    issues.Add(new ValidationIssue(IssueLevel.Error, "out-of-range",
                        $"第 {i + 1} 步 {f.Label} = {Fmt.Num(v)} 低于下限 {Fmt.Num(min)}") { StepId = s.StepId });
                if (f.Max is { } max && v > max)
                    issues.Add(new ValidationIssue(IssueLevel.Error, "out-of-range",
                        $"第 {i + 1} 步 {f.Label} = {Fmt.Num(v)} 高于上限 {Fmt.Num(max)}") { StepId = s.StepId });
            }

            // 超时保护：到不了的目标不能把通道永远挂住（§4.3）。
            // 只有声明了 timeout 字段的指令才查——原型的参数表里并非每条都有。
            if (d.RequiresTimeout && d.Parameters.Find("timeout") is not null && !s.Parameters.Has("timeout"))
                issues.Add(new ValidationIssue(IssueLevel.Warning, "no-timeout",
                    $"第 {i + 1} 步「{d.DisplayName}」按{Termination(d.Termination)}结束但没有设超时") { StepId = s.StepId });

            if (channel is not null) ValidateAgainstChannel(issues, i, s, d, channel);
        }

        if (depth > 0)
            issues.Add(new ValidationIssue(IssueLevel.Error, "loop-unbalanced",
                $"第 {openAt + 1} 步的循环开始没有对应的循环结束") { StepId = recipe.Steps[openAt].StepId });

        var schedule = Schedule.Build(recipe, catalog, seed);
        issues.Add(new ValidationIssue(IssueLevel.Info, "duration",
            $"预计总时长 {Fmt.Hms(schedule.Total)}，共 {recipe.Steps.Count} 步"));

        if (channel is not null)
        {
            var missing = channel.MissingCapabilities(recipe.RequiredCapabilities(catalog));
            foreach (var t in missing)
                issues.Add(new ValidationIssue(IssueLevel.Error, "capability",
                    $"CH{channel.Number} 缺少能力：{Friendly(t)}") { Channel = channel.Number });

            ValidateDoseVolume(issues, recipe, catalog, channel);
        }

        if (charge is not null) ValidateCharge(issues, recipe, catalog, charge, schedule);

        return issues;
    }

    /// <summary>
    /// 配料表和加料步骤对不对得上。
    ///
    /// 对得上但体积不一样、或者压根对不上——两种都得说。
    /// **默默不联动最坏**：人以为加料体积会跟着配料表走，其实配方里还是手打的那个数，
    /// 而这两个数一旦分家，报告上「计划加 30 mL」和配料表「应加 24.6 mL」会同时出现。
    /// </summary>
    private static void ValidateCharge(List<ValidationIssue> issues, Recipe recipe,
                                       ICommandCatalog catalog, ChargeResult charge,
                                       Scheduling.Schedule schedule)
    {
        foreach (var p in charge.Problems)
            issues.Add(new ValidationIssue(IssueLevel.Warning, "charge", "配料表：" + p));

        // 熔点（CH-5.2，警告放行）：工艺会把釜冷到某个液体组分的熔点以下——
        // 冰乙酸 16.6 ℃，冬天降到 10 ℃ 它就在釜里凝住。查**液体投料**的行：
        // 相态标了「液」的，或没标但按体积给量的（按毫升量的多半是液体）。
        // 固体行不查——固体本来就是固体，「低于熔点」对它是废话
        var low = LowestTemp(recipe, schedule);
        foreach (var l in charge.Lines)
        {
            if (l.Item.Role == ChargeRole.Product) continue;
            var liquid = l.Item.Phase == "液"
                         || (l.Item.Phase.Length == 0
                             && (l.Item.Basis == ChargeBasis.Volumes
                                 || (l.Item.Basis == ChargeBasis.Quantity
                                     && l.Item.Unit == ChargeUnit.Milliliter)));
            if (!liquid) continue;
            var mp = l.Item.Mp ?? l.Reference?.Mp;
            if (low is { } t && mp is { } m && t < m)
                issues.Add(new ValidationIssue(IssueLevel.Warning, "charge-mp",
                    $"配方最低温度约 {Fmt.Num(t)} ℃，低于「{Show(l.Item.Name)}」的熔点 {Fmt.Num(m)} ℃"
                    + "——低温段它可能在釜里凝固。结晶 / 冷冻析出若正是设计意图，可忽略这条。"));
        }

        var links = ChargeLink.Match(recipe, catalog, charge);
        if (links.Count == 0) return;

        foreach (var e in links)
        {
            if (e.Line is not { } line) continue;
            var stepId = recipe.Steps[e.StepIndex].StepId;

            // 固体不走泵（CH-4.5，Error 阻断）：泵打不了固体
            if (line.Item.Phase == "固")
                issues.Add(new ValidationIssue(IssueLevel.Error, "charge-solid",
                    $"第 {e.StepIndex + 1} 步想用泵加「{Show(e.Liquid)}」，而它的相态标着「固」"
                    + "——泵打不了固体。以溶液投料请把配料行相态改成「液」；"
                    + "固体直投改用「消息提示」步骤提醒人工投料。") { StepId = stepId });

            // 沸点（CH-5.1，Error 阻断）：往热釜里泵低沸点液体会闪蒸喷溅。
            // 温度取排期对这一步的估算（循环多轮取最热的那一轮）
            var bp = line.Item.Bp ?? line.Reference?.Bp;
            if (bp is { } b)
            {
                var at = schedule.Entries.Where(x => x.StepId == stepId)
                                         .Select(x => (double?)x.StartTemp).Max();
                if (at is { } temp && temp > b)
                    issues.Add(new ValidationIssue(IssueLevel.Error, "charge-bp",
                        $"第 {e.StepIndex + 1} 步在约 {Fmt.Num(temp)} ℃ 时泵入「{Show(e.Liquid)}」，"
                        + $"超过其沸点 {Fmt.Num(b)} ℃——低沸点液体进热釜会闪蒸喷溅。"
                        + "确要高温加料，请核对沸点数据或调整加料时机。") { StepId = stepId });
            }
        }

        foreach (var e in links)
        {
            if (e.RefGone)
            {
                // 建过引用、行被删了——不退回按名对：可能悄悄对上一个恰好同名的新行
                issues.Add(new ValidationIssue(IssueLevel.Warning, "charge-refgone",
                    $"第 {e.StepIndex + 1} 步引用的配料行已不在（「{Show(e.Liquid)}」可能被删了），"
                    + "这一步的体积不再跟随。在步骤属性里重选一行即可重新连接")
                { StepId = recipe.Steps[e.StepIndex].StepId });
                continue;
            }
            if (!e.Matched)
            {
                // 配料表是空的就别唠叨——只跑温控曲线的场合不需要配料表
                if (charge.Lines.Count == 0) continue;
                issues.Add(new ValidationIssue(IssueLevel.Warning, "charge-unlinked",
                    $"第 {e.StepIndex + 1} 步的料液「{Show(e.Liquid)}」在配料表里没有同名组分，"
                    + "这一步的体积不会跟着配料表走") { StepId = recipe.Steps[e.StepIndex].StepId });
                continue;
            }
            if (e.PlannedVolume is null)
            {
                // 原因可能记在「缺什么」里，也可能记在「按什么假设算的」里
                // （按克称的固体没有密度就是后者）——两边都要看，不然只剩一句空话
                var reasons = e.Line!.Missing.Concat(e.Line.Assumptions).ToList();
                var why = reasons.Count > 0 ? string.Join("、", reasons) : "配料表里的量还没填全";
                issues.Add(new ValidationIssue(IssueLevel.Warning, "charge-novolume",
                    $"第 {e.StepIndex + 1} 步的料液「{Show(e.Liquid)}」算不出应加体积（{why}）")
                { StepId = recipe.Steps[e.StepIndex].StepId });
                continue;
            }
            if (!e.Differs) continue;

            // 差多少是同一句话，差的**性质**分三种：跟随着还没同步上（转瞬即逝，
            // 打开配料表页就自动追平）、手改后脱离了计算（CH-4.2 点名要提示的状态）、
            // 从没建过引用（老配方按名对上的）
            var stepId = recipe.Steps[e.StepIndex].StepId;
            if (ChargeLink.NeedsFollow(e))
                issues.Add(new ValidationIssue(IssueLevel.Warning, "charge-follow",
                    $"第 {e.StepIndex + 1} 步引用配料表的体积还没跟上（现 {Fmt.Num(e.StepVolume, 2)} mL，"
                    + $"应 {Fmt.Num(e.PlannedVolume.Value, 2)} mL），打开配料表页即自动同步")
                { StepId = stepId });
            else if (e.MatchedById)
                issues.Add(new ValidationIssue(IssueLevel.Warning, "charge-detached",
                    $"第 {e.StepIndex + 1} 步已脱离计算：手填 {Fmt.Num(e.StepVolume, 2)} mL，"
                    + $"配料表算出 {Fmt.Num(e.PlannedVolume.Value, 2)} mL。"
                    + "要恢复跟随，在配料表页按「应用到加料步骤」")
                { StepId = stepId });
            else
                issues.Add(new ValidationIssue(IssueLevel.Warning, "charge-mismatch",
                    $"第 {e.StepIndex + 1} 步加「{Show(e.Liquid)}」{Fmt.Num(e.StepVolume, 2)} mL，"
                    + $"配料表算出来是 {Fmt.Num(e.PlannedVolume.Value, 2)} mL")
                { StepId = stepId });
        }
    }

    private static string Show(string s) => s.Length > 0 ? s : "（没填）";

    /// <summary>
    /// 配方全程的最低温度估算：各步开始温度（排期链）与控温类步骤的目标温度
    /// （含梯度控温分段表里的每一段）取最小。没有任何温度信息就是 null——不猜。
    /// </summary>
    private static double? LowestTemp(Recipe recipe, Scheduling.Schedule schedule)
    {
        double? low = null;
        void Take(double v) => low = low is { } x ? Math.Min(x, v) : v;

        foreach (var e in schedule.Entries) Take(e.StartTemp);
        foreach (var st in recipe.Steps)
        {
            if (!st.CommandId.StartsWith("tec.temp.", StringComparison.Ordinal)) continue;
            if (st.Parameters.Has("target")) Take(st.Parameters.Num("target"));
            if (st.Rows is { } rows)
                foreach (var row in rows)
                    if (row.Has("target")) Take(row.Num("target"));
        }
        return low;
    }

    private static void ValidateAgainstChannel(List<ValidationIssue> issues, int i, Step s,
                                               CommandDescriptor d, Channel channel)
    {
        if (d.RequiredCapability is { } need && !channel.Capabilities.Has(need))
        {
            issues.Add(new ValidationIssue(IssueLevel.Error, "capability",
                $"第 {i + 1} 步需要 {Friendly(need)}，CH{channel.Number} 没有") { StepId = s.StepId, Channel = channel.Number });
            return;
        }

        foreach (var extra in d.AlsoRequires)
            if (!channel.Capabilities.Has(extra))
                issues.Add(new ValidationIssue(IssueLevel.Error, "capability",
                    $"第 {i + 1} 步还需要 {Friendly(extra)}，CH{channel.Number} 没有")
                { StepId = s.StepId, Channel = channel.Number });

        // 动态范围：LimitFrom 让参数上限跟着设备走（§4.2）
        foreach (var f in d.Parameters.Fields)
        {
            if (f.LimitFrom is null || !s.Parameters.Has(f.Key)) continue;
            var bound = ResolveLimit(channel, f.LimitFrom);
            if (bound is null) continue;
            var v = s.Parameters.Num(f.Key);
            if (f.LimitFrom.EndsWith(".Max", StringComparison.Ordinal) && v > bound.Value)
                issues.Add(new ValidationIssue(IssueLevel.Error, "device-limit",
                    $"第 {i + 1} 步 {f.Label} = {Fmt.Num(v)} 超过设备上限 {Fmt.Num(bound.Value)}") { StepId = s.StepId });
            if (f.LimitFrom.EndsWith(".Min", StringComparison.Ordinal) && v < bound.Value)
                issues.Add(new ValidationIssue(IssueLevel.Error, "device-limit",
                    $"第 {i + 1} 步 {f.Label} = {Fmt.Num(v)} 低于设备下限 {Fmt.Num(bound.Value)}") { StepId = s.StepId });
        }

        // 标定：未标定或过期的设备，编排到配方里要拦下来（§10.3）
        if (d.RequiredCapability == typeof(IDosing) && channel.Capabilities.Get<IDosing>() is { } dosing)
        {
            if (dosing.Calibration is null)
                issues.Add(new ValidationIssue(IssueLevel.Error, "calibration",
                    $"第 {i + 1} 步用到加料，但该通道的泵未标定") { StepId = s.StepId, Channel = channel.Number });
            else if (dosing.Calibration.IsExpired(DateTimeOffset.Now))
                issues.Add(new ValidationIssue(IssueLevel.Warning, "calibration",
                    $"第 {i + 1} 步用到加料，泵的标定已于 {Fmt.Stamp(dosing.Calibration.ExpiresAt!.Value)} 过期")
                { StepId = s.StepId, Channel = channel.Number });
        }
    }

    private static void ValidateDoseVolume(List<ValidationIssue> issues, Recipe recipe,
                                           ICommandCatalog catalog, Channel channel)
    {
        var dosing = channel.Capabilities.Get<IDosing>();
        if (dosing is null) return;

        var total = 0d;
        foreach (var s in recipe.Steps)
        {
            if (!catalog.TryGet(s.CommandId, out var d)) continue;
            if (d.RequiredCapability != typeof(IDosing)) continue;
            total += Math.Max(0, s.Parameters.Num("vol"));
        }
        if (total > 0 && total > dosing.Limits.MaxVolume)
            issues.Add(new ValidationIssue(IssueLevel.Error, "volume",
                $"CH{channel.Number} 累计加料 {Fmt.Num(total)} mL 超过釜容 {Fmt.Num(dosing.Limits.MaxVolume)} mL")
            { Channel = channel.Number });
        // CH-5.6：超过 80 % 就该被看见——搅拌旋涡、放热鼓泡、加料冲击都要余量，
        // 灌到九成的釜出事时没有退路。超 100 % 上面那条 Error 拦，这里只提醒
        else if (total > 0 && total > dosing.Limits.MaxVolume * 0.8)
            issues.Add(new ValidationIssue(IssueLevel.Warning, "volume-80",
                $"CH{channel.Number} 累计加料 {Fmt.Num(total)} mL，已占釜容 "
                + $"{Fmt.Num(dosing.Limits.MaxVolume)} mL 的 {total / dosing.Limits.MaxVolume * 100:F0} %"
                + "——超过 80 % 的经验红线，留出搅拌与放热的余量。")
            { Channel = channel.Number });
    }

    /// <summary>
    /// 资源冲突：两个通道在重叠时段都要用同一台泵。
    /// 排期已经有了，做冲突检测几乎是白送（§10.4）。
    /// </summary>
    public static IReadOnlyList<ValidationIssue> DetectResourceConflicts(
        IReadOnlyDictionary<int, (Schedule Schedule, DateTimeOffset Start)> plans,
        ICommandCatalog catalog,
        Func<string, string?> resourceOf)
    {
        var issues = new List<ValidationIssue>();
        var windows = new List<(string Resource, int Channel, DateTimeOffset From, DateTimeOffset To, string Title)>();

        foreach (var (ch, plan) in plans)
            foreach (var e in plan.Schedule.Entries)
            {
                var res = resourceOf(e.CommandId);
                if (res is null || e.Duration <= TimeSpan.Zero) continue;
                windows.Add((res, ch, plan.Start + e.Start, plan.Start + e.Start + e.Duration, e.Title));
            }

        for (var i = 0; i < windows.Count; i++)
            for (var j = i + 1; j < windows.Count; j++)
            {
                var a = windows[i];
                var b = windows[j];
                if (a.Resource != b.Resource || a.Channel == b.Channel) continue;
                if (a.From >= b.To || b.From >= a.To) continue;
                issues.Add(new ValidationIssue(IssueLevel.Warning, "resource-conflict",
                    $"{a.Resource}：CH{a.Channel}「{a.Title}」与 CH{b.Channel}「{b.Title}」在 " +
                    $"{Fmt.Clock(Max(a.From, b.From))}–{Fmt.Clock(Min(a.To, b.To))} 重叠"));
            }

        return issues;
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private static double? ResolveLimit(Channel channel, string path)
    {
        var parts = path.Split('.');
        if (parts.Length < 3) return null;
        var which = parts[^1];
        return parts[0] switch
        {
            "TemperatureControl" => channel.Capabilities.Get<ITemperatureControl>() is { } t
                ? which switch { "Max" => t.Limits.Max, "Min" => t.Limits.Min, "MaxRatePerMin" => t.Limits.MaxRatePerMin, _ => null }
                : null,
            "Stirrer" => channel.Capabilities.Get<IStirrer>() is { } s
                ? which switch { "Max" => s.Limits.Max, "Min" => s.Limits.Min, _ => null }
                : null,
            "Dosing" => channel.Capabilities.Get<IDosing>() is { } dz
                ? which switch { "Max" => dz.Limits.Max, "Min" => dz.Limits.Min, "MaxVolume" => dz.Limits.MaxVolume, _ => null }
                : null,
            _ => null
        };
    }

    private static string Termination(TerminationKind k) => k switch
    {
        TerminationKind.Setpoint => "到达目标",
        TerminationKind.Condition => "条件满足",
        TerminationKind.Quantity => "加完设定量",
        TerminationKind.Timer => "计时",
        TerminationKind.Operator => "操作人确认",
        _ => "立即"
    };

    private static string Friendly(Type t) => t.Name switch
    {
        nameof(ITemperatureControl) => "温度控制",
        nameof(IStirrer) => "搅拌",
        nameof(IDosing) => "加料",
        nameof(IScalarSensor) => "标量检测（pH / 浊度 等）",
        nameof(ISpectrumSource) => "谱图源",
        nameof(IDistributionSource) => "分布源",
        nameof(IIllumination) => "背景灯",
        nameof(IImageSource) => "图像源",
        _ => t.Name
    };
}
