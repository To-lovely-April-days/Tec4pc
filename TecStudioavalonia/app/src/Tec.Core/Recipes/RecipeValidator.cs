using Tec.Core.Benches;
using Tec.Core.Catalog;
using Tec.Core.Scheduling;
using Tec.Driver.Abi;

namespace Tec.Core.Recipes;

public enum IssueLevel { Info, Warning, Error }

public sealed record ValidationIssue(IssueLevel Level, string Code, string Message)
{
    public int? StepIndex { get; init; }
    public int? Channel { get; init; }
}

/// <summary>
/// 保存 / 装载前跑一遍（§10.4）。目的是**提前**告诉操作人哪里不对，
/// 而不是跑到一半报错。
/// </summary>
public static class RecipeValidator
{
    public static IReadOnlyList<ValidationIssue> Validate(
        Recipe recipe, ICommandCatalog catalog, Channel? channel = null, EstimationContext? seed = null)
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
                    $"第 {i + 1} 步引用了未安装的指令 {s.CommandId}") { StepIndex = i });
                continue;
            }

            if (BuiltinCommands.IsLoopBegin(s.CommandId)) { depth++; if (openAt < 0) openAt = i; }
            if (BuiltinCommands.IsLoopEnd(s.CommandId))
            {
                depth--;
                if (depth < 0)
                {
                    issues.Add(new ValidationIssue(IssueLevel.Error, "loop-unbalanced",
                        $"第 {i + 1} 步的循环结束没有对应的循环开始") { StepIndex = i });
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
                        $"第 {i + 1} 步 {f.Label} = {Fmt.Num(v)} 低于下限 {Fmt.Num(min)}") { StepIndex = i });
                if (f.Max is { } max && v > max)
                    issues.Add(new ValidationIssue(IssueLevel.Error, "out-of-range",
                        $"第 {i + 1} 步 {f.Label} = {Fmt.Num(v)} 高于上限 {Fmt.Num(max)}") { StepIndex = i });
            }

            // 超时保护：到不了的目标不能把通道永远挂住（§4.3）
            if (d.RequiresTimeout && !s.Parameters.Has("超时"))
                issues.Add(new ValidationIssue(IssueLevel.Warning, "no-timeout",
                    $"第 {i + 1} 步「{d.DisplayName}」按{Termination(d.Termination)}结束但没有设超时") { StepIndex = i });

            if (channel is not null) ValidateAgainstChannel(issues, i, s, d, channel);
        }

        if (depth > 0)
            issues.Add(new ValidationIssue(IssueLevel.Error, "loop-unbalanced",
                $"第 {openAt + 1} 步的循环开始没有对应的循环结束") { StepIndex = openAt });

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

        return issues;
    }

    private static void ValidateAgainstChannel(List<ValidationIssue> issues, int i, Step s,
                                               CommandDescriptor d, Channel channel)
    {
        if (d.RequiredCapability is { } need && !channel.Capabilities.Has(need))
        {
            issues.Add(new ValidationIssue(IssueLevel.Error, "capability",
                $"第 {i + 1} 步需要 {Friendly(need)}，CH{channel.Number} 没有") { StepIndex = i, Channel = channel.Number });
            return;
        }

        foreach (var extra in d.AlsoRequires)
            if (!channel.Capabilities.Has(extra))
                issues.Add(new ValidationIssue(IssueLevel.Error, "capability",
                    $"第 {i + 1} 步还需要 {Friendly(extra)}，CH{channel.Number} 没有")
                { StepIndex = i, Channel = channel.Number });

        // 动态范围：LimitFrom 让参数上限跟着设备走（§4.2）
        foreach (var f in d.Parameters.Fields)
        {
            if (f.LimitFrom is null || !s.Parameters.Has(f.Key)) continue;
            var bound = ResolveLimit(channel, f.LimitFrom);
            if (bound is null) continue;
            var v = s.Parameters.Num(f.Key);
            if (f.LimitFrom.EndsWith(".Max", StringComparison.Ordinal) && v > bound.Value)
                issues.Add(new ValidationIssue(IssueLevel.Error, "device-limit",
                    $"第 {i + 1} 步 {f.Label} = {Fmt.Num(v)} 超过设备上限 {Fmt.Num(bound.Value)}") { StepIndex = i });
            if (f.LimitFrom.EndsWith(".Min", StringComparison.Ordinal) && v < bound.Value)
                issues.Add(new ValidationIssue(IssueLevel.Error, "device-limit",
                    $"第 {i + 1} 步 {f.Label} = {Fmt.Num(v)} 低于设备下限 {Fmt.Num(bound.Value)}") { StepIndex = i });
        }

        // 标定：未标定或过期的设备，编排到配方里要拦下来（§10.3）
        if (d.RequiredCapability == typeof(IDosing) && channel.Capabilities.Get<IDosing>() is { } dosing)
        {
            if (dosing.Calibration is null)
                issues.Add(new ValidationIssue(IssueLevel.Error, "calibration",
                    $"第 {i + 1} 步用到加料，但该通道的泵未标定") { StepIndex = i, Channel = channel.Number });
            else if (dosing.Calibration.IsExpired(DateTimeOffset.Now))
                issues.Add(new ValidationIssue(IssueLevel.Warning, "calibration",
                    $"第 {i + 1} 步用到加料，泵的标定已于 {Fmt.Stamp(dosing.Calibration.ExpiresAt!.Value)} 过期")
                { StepIndex = i, Channel = channel.Number });
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
            total += Math.Max(0, s.Parameters.Num("体积"));
        }
        if (total > 0 && total > dosing.Limits.MaxVolume)
            issues.Add(new ValidationIssue(IssueLevel.Error, "volume",
                $"CH{channel.Number} 累计加料 {Fmt.Num(total)} mL 超过釜容 {Fmt.Num(dosing.Limits.MaxVolume)} mL")
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
