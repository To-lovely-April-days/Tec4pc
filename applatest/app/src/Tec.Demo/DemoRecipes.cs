using Tec.Core.Chemistry;
using Tec.Core.Recipes;
using Tec.Driver.Abi;

namespace Tec.Demo;

/// <summary>
/// 【本地改动】演示配方：两条通道各一条有业务意义的工作配方，外加一个像真实工艺的配方库。
///
/// WHY：配方页 / 配方库页 / 运行页的截图要有「真在跑一道工艺」的样子。步骤全部用程序现有的指令
/// （CommandSpecs 那 13 条 + 内置流程指令），参数键与指令声明一字不差——它们不是摆设，
/// 演示启动时真的被引擎校验、排期、执行，曲线是仿真热模型跑出来的。
/// 加料步骤带配料行引用（chemId），配料表页「对照」「跟随」都有东西可看。
/// </summary>
public static class DemoRecipes
{
    /// <summary>通道 1 加料步骤引用的配料行号 / 通道 2 反馈加料的料液（与 DemoCharges 里的行 Id 一致）。</summary>
    public const string EthanolRowId = "chg-c1-etoh", HclRowId = "chg-c2-hcl";

    private static readonly DateTimeOffset Oct = new(2026, 10, 21, 9, 30, 0, TimeSpan.FromHours(8));

    private static Step S(string cmd, string? phase = null, params (string, object?)[] p)
        => new() { CommandId = cmd, Parameters = ParameterSet.Of(p), Phase = phase };

    private static Step Rows(string cmd, string phase, (string, object?)[] p, params (double T, double R, double H)[] rows)
        => new()
        {
            CommandId = cmd, Parameters = ParameterSet.Of(p), Phase = phase,
            Rows = rows.Select(r => ParameterSet.Of(("t", r.T), ("r", r.R), ("h", r.H))).ToList()
        };

    /// <summary>通道 1：苯甲酸乙酯酯化合成——控温 → 搅拌 → 恒速滴加 → 蒸回流 → 梯度降温结晶 → 保温 → 停搅拌。</summary>
    public static Recipe Esterification()
    {
        var r = new Recipe
        {
            Id = "demo-ester", Name = "苯甲酸乙酯酯化合成", Author = "王工",
            Notes = "苯甲酸 + 无水乙醇（4 eq），浓硫酸催化；60 ℃ 滴加乙醇，回流 1 h，梯度降至 10 ℃ 结晶。",
            ModifiedAt = Oct.AddDays(20)
        };
        r.Steps.Add(S(CommandSpecs.Control, "升温", ("target", 60d), ("task", "尽快"), ("obj", "釜内 Tr"),
                       ("tol", 0.1d), ("wait", true), ("settle", 60d)));
        r.Steps.Add(S(CommandSpecs.Stir, "升温", ("rpm", 300d), ("task", "按时长"), ("ramp", 10d)));
        r.Steps.Add(S(CommandSpecs.Dose, "滴加", ("pump", "加料泵 1"), ("liq", "无水乙醇"), ("vol", 23.4d),
                       ("task", "按速率"), ("rate", 0.78d), ("sync", true),
                       (ChargeLink.ChemKey, EthanolRowId), (ChargeLink.LinkedKey, true)));
        r.Steps.Add(S(CommandSpecs.Reflux, "回流", ("dt", 5d), ("tjmax", 120d), ("dur", 60d)));
        r.Steps.Add(Rows(CommandSpecs.Gradient, "结晶", new (string, object?)[] { ("obj", "釜内 Tr"), ("loop", false) },
                         (40, 1.0, 5), (25, 0.5, 10), (10, 0.3, 0)));
        r.Steps.Add(S(CommandSpecs.Hold, "保温", ("dur", 30d), ("tol", 0.1d), ("obj", "釜内 Tr")));
        r.Steps.Add(S(CommandSpecs.Stir, "出料", ("rpm", 0d), ("task", "按时长"), ("ramp", 15d)));
        return r;
    }

    /// <summary>通道 2：对乙酰氨基酚母液 pH 反馈中和——控温 25 ℃ → 搅拌 → pH 采集 → 反馈加酸到 7.0 → 保持 → 停搅拌。</summary>
    public static Recipe PhNeutralization()
    {
        var r = new Recipe
        {
            Id = "demo-ph", Name = "pH 反馈中和（对乙酰氨基酚母液）", Author = "李工",
            Notes = "碱性母液用 1 mol/L 盐酸反馈中和到 pH 7.0 ± 0.2，25 ℃ 恒温；泵 2 调节、最大 50 mL。",
            ModifiedAt = Oct.AddDays(26)
        };
        r.Steps.Add(S(CommandSpecs.Control, "恒温", ("target", 25d), ("task", "尽快"), ("obj", "釜内 Tr"),
                       ("tol", 0.1d), ("wait", true), ("settle", 60d)));
        r.Steps.Add(S(CommandSpecs.Stir, "恒温", ("rpm", 400d), ("task", "按时长"), ("ramp", 8d)));
        r.Steps.Add(S(CommandSpecs.PhSample, "中和", ("interval", 2d), ("log", true)));
        r.Steps.Add(S(CommandSpecs.PhHold, "中和", ("target", 7.0d), ("band", 0.2d), ("pump", "加料泵 2"),
                       ("maxRate", 1.0d), ("maxVol", 50d), ("dur", 45d)));
        r.Steps.Add(S(CommandSpecs.Hold, "保持", ("dur", 30d), ("tol", 0.1d), ("obj", "釜内 Tr")));
        r.Steps.Add(S(CommandSpecs.Stir, "出料", ("rpm", 0d), ("task", "按时长"), ("ramp", 10d)));
        return r;
    }

    /// <summary>配方库：7 条命名像真实工艺的配方（前两条就是两路的工作配方）。</summary>
    public static List<Recipe> Library()
    {
        var list = new List<Recipe> { Esterification(), PhNeutralization() };

        var cryst = new Recipe
        {
            Id = "demo-cryst", Name = "降温结晶 · 60→5 ℃ 三段梯度", Author = "王工",
            Notes = "对乙酰氨基酚乙醇–水重结晶：60 ℃ 溶清后三段降温，低温养晶 1 h。", ModifiedAt = Oct.AddDays(3)
        };
        cryst.Steps.Add(S(CommandSpecs.Control, "溶清", ("target", 60d), ("task", "按速率"), ("rate", 2d), ("obj", "釜内 Tr"), ("tol", 0.1d), ("wait", true), ("settle", 60d)));
        cryst.Steps.Add(S(CommandSpecs.Stir, "溶清", ("rpm", 350d), ("task", "按时长"), ("ramp", 10d)));
        cryst.Steps.Add(S(CommandSpecs.Hold, "溶清", ("dur", 20d), ("tol", 0.1d), ("obj", "釜内 Tr")));
        cryst.Steps.Add(Rows(CommandSpecs.Gradient, "结晶", new (string, object?)[] { ("obj", "釜内 Tr"), ("loop", false) },
                             (45, 0.5, 15), (25, 0.3, 20), (5, 0.2, 0)));
        cryst.Steps.Add(S(CommandSpecs.Hold, "养晶", ("dur", 60d), ("tol", 0.1d), ("obj", "釜内 Tr")));
        cryst.Steps.Add(S(CommandSpecs.Stir, "养晶", ("rpm", 150d), ("task", "按时长"), ("ramp", 20d)));
        list.Add(cryst);

        var reflux = new Recipe
        {
            Id = "demo-reflux", Name = "常压回流 · ΔT 8 K · 2 h", Author = "王工",
            Notes = "沸点未知的体系用夹套跟随建立回流，不猜目标温度。", ModifiedAt = Oct.AddDays(8)
        };
        reflux.Steps.Add(S(CommandSpecs.Stir, "升温", ("rpm", 300d), ("task", "按时长"), ("ramp", 10d)));
        reflux.Steps.Add(S(CommandSpecs.Control, "升温", ("target", 70d), ("task", "按速率"), ("rate", 3d), ("obj", "釜内 Tr"), ("tol", 0.2d), ("wait", false), ("settle", 0d)));
        reflux.Steps.Add(S(CommandSpecs.Reflux, "回流", ("dt", 8d), ("tjmax", 150d), ("dur", 120d)));
        reflux.Steps.Add(S(CommandSpecs.PassiveCool, "降温", ("target", 40d), ("timeout", 90d)));
        list.Add(reflux);

        var staged = new Recipe
        {
            Id = "demo-staged", Name = "分段滴加 · 三批各 15 mL · 批间保温", Author = "李工",
            Notes = "放热体系分三批滴加，每批之后保温 15 min 让反应平息再加下一批。", ModifiedAt = Oct.AddDays(12)
        };
        staged.Steps.Add(S(CommandSpecs.Control, "升温", ("target", 45d), ("task", "按速率"), ("rate", 2d), ("obj", "釜内 Tr"), ("tol", 0.1d), ("wait", true), ("settle", 60d)));
        staged.Steps.Add(S(CommandSpecs.Stir, "升温", ("rpm", 450d), ("task", "按时长"), ("ramp", 10d)));
        for (var i = 1; i <= 3; i++)
        {
            staged.Steps.Add(S(CommandSpecs.Dose, $"第 {i} 批", ("pump", "加料泵 1"), ("liq", "乙酸酐"), ("vol", 15d), ("task", "按速率"), ("rate", 1.5d), ("sync", true)));
            staged.Steps.Add(S(CommandSpecs.Hold, $"第 {i} 批", ("dur", 15d), ("tol", 0.2d), ("obj", "釜内 Tr")));
        }
        staged.Steps.Add(S(CommandSpecs.PassiveCool, "降温", ("target", 25d), ("timeout", 120d)));
        list.Add(staged);

        var ladder = new Recipe
        {
            Id = "demo-ladder", Name = "阶梯升温 · 40 / 60 / 80 ℃ 各保温 30 min", Author = "张工",
            Notes = "热稳定性考察：三个台阶各保温 30 min，记录放热。", ModifiedAt = Oct.AddDays(15)
        };
        ladder.Steps.Add(S(CommandSpecs.Stir, "升温", ("rpm", 300d), ("task", "按时长"), ("ramp", 10d)));
        ladder.Steps.Add(Rows(CommandSpecs.Gradient, "升温", new (string, object?)[] { ("obj", "釜内 Tr"), ("loop", false) },
                              (40, 2.0, 30), (60, 2.0, 30), (80, 2.0, 30)));
        ladder.Steps.Add(S(CommandSpecs.PassiveCool, "降温", ("target", 30d), ("timeout", 150d)));
        list.Add(ladder);

        var cold = new Recipe
        {
            Id = "demo-cold", Name = "低温反应 · −10 ℃ 滴加 · 缓慢升至室温", Author = "张工",
            Notes = "格氏类低温滴加：−10 ℃ 滴加 20 mL，保温 30 min 后 0.5 ℃/min 升至 20 ℃。", ModifiedAt = Oct.AddDays(18)
        };
        cold.Steps.Add(S(CommandSpecs.Stir, "降温", ("rpm", 350d), ("task", "按时长"), ("ramp", 10d)));
        cold.Steps.Add(S(CommandSpecs.Control, "降温", ("target", -10d), ("task", "尽快"), ("obj", "釜内 Tr"), ("tol", 0.2d), ("wait", true), ("settle", 120d)));
        cold.Steps.Add(S(CommandSpecs.Dose, "滴加", ("pump", "加料泵 2"), ("liq", "溴乙烷 / THF"), ("vol", 20d), ("task", "按速率"), ("rate", 0.5d), ("sync", true)));
        cold.Steps.Add(S(CommandSpecs.Hold, "保温", ("dur", 30d), ("tol", 0.2d), ("obj", "釜内 Tr")));
        cold.Steps.Add(S(CommandSpecs.Control, "升温", ("target", 20d), ("task", "按速率"), ("rate", 0.5d), ("obj", "釜内 Tr"), ("tol", 0.2d), ("wait", true), ("settle", 60d)));
        list.Add(cold);

        var aspirin = new Recipe
        {
            Id = "demo-aspirin", Name = "阿司匹林乙酰化 · 70 ℃ 保温 · 水淬结晶", Author = "李工",
            Notes = "水杨酸 + 乙酸酐，浓硫酸催化，70 ℃ 保温 30 min，加水淬灭后降温结晶。", ModifiedAt = Oct.AddDays(24)
        };
        aspirin.Steps.Add(S(CommandSpecs.Stir, "升温", ("rpm", 300d), ("task", "按时长"), ("ramp", 10d)));
        aspirin.Steps.Add(S(CommandSpecs.Control, "升温", ("target", 70d), ("task", "按速率"), ("rate", 3d), ("obj", "釜内 Tr"), ("tol", 0.1d), ("wait", true), ("settle", 60d)));
        aspirin.Steps.Add(S(CommandSpecs.Hold, "反应", ("dur", 30d), ("tol", 0.1d), ("obj", "釜内 Tr")));
        aspirin.Steps.Add(S(CommandSpecs.Dose, "淬灭", ("pump", "加料泵 2"), ("liq", "去离子水"), ("vol", 40d), ("task", "按速率"), ("rate", 4d), ("sync", false)));
        aspirin.Steps.Add(Rows(CommandSpecs.Gradient, "结晶", new (string, object?)[] { ("obj", "釜内 Tr"), ("loop", false) },
                               (40, 1.0, 10), (10, 0.5, 30)));
        list.Add(aspirin);

        return list;
    }
}
