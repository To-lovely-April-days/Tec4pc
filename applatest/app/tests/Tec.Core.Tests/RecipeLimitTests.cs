using Tec.Core.Benches;
using Tec.Core.Catalog;
using Tec.Core.Data;
using Tec.Core.Execution;
using Tec.Core.Recipes;
using Tec.Core.Safety;
using Tec.Driver.Abi;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 「改限值」（原「安全联锁」）。从前那条指令只写一行日志，什么都不联锁；
/// 后来它把限值真的登记进安全层；0264 起参数换成 EasyMax 手册 §6 的
/// 安全限值参数表（Tr max / Tr min / Tj max / Tj min / T diff max / Rmax
/// + 本机的 pH 上下限），外加 E 级紧急程序的 Tsafe / Rsafe。
/// 这里盯的是**登记的语义**：上下限并成一条、温差展开成对称的一对、
/// 换值不叠加、底线不受影响、下一炉清场、老配方翻得过来。
/// </summary>
public class RecipeLimitTests
{
    // ── 台子：只要 provider + 安全层，指令本身不碰设备 ─────────────────

    private static (BuiltinCommandProvider Provider, SafetyMonitor Safety, List<string> Notes) Rig()
    {
        var safety = new SafetyMonitor(new DataPipeline());
        var provider = new BuiltinCommandProvider(new AutoOperatorGate()) { Safety = safety };
        return (provider, safety, new List<string>());
    }

    /// <summary>跑一步「改限值」：限值表按 (参数, 值, 动作) 给，字段可选。</summary>
    private static Task Run(BuiltinCommandProvider provider, List<string> notes,
                            (string Par, double Val, string Act)[] rows,
                            params (string Key, object? Value)[] fields)
    {
        var h = provider.Resolve(BuiltinCommands.Interlock)!;
        var ctx = new CommandContext
        {
            Channel = 1,
            Capabilities = new CapabilitySet(),
            Now = () => new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero),
            Note = notes.Add
        };
        var input = new CommandInput(ParameterSet.Of(fields),
            rows.Select(r => ParameterSet.Of(("par", r.Par), ("val", r.Val), ("act", r.Act)))
                .ToList());
        return h.ExecuteAsync(ctx, input, CancellationToken.None);
    }

    private static (string, double, string)[] One(string par, double val, string act = "中止本通道")
        => new[] { (par, val, act) };

    // ── 登记的语义 ──────────────────────────────────────────────────

    [Fact]
    public async Task 改限值真的把限值交给安全层()
    {
        var (prov, safety, notes) = Rig();

        await Run(prov, notes, One("Tr max", 100, "停止加料"));

        var lim = Assert.Single(safety.Limits);
        Assert.Equal(1, lim.Channel);
        Assert.Equal("Tr", lim.Tag);
        Assert.Equal(100d, lim.Max);
        Assert.Null(lim.Min);
        Assert.Null(lim.MaxRatePerMin);
        Assert.Equal(SafetyAction.StopDosing, lim.Action);
        Assert.True(lim.FromRecipe);
        Assert.Contains(notes, n => n.Contains("安全层"));
    }

    [Fact]
    public async Task 下限参数设的是下限()
    {
        var (prov, safety, notes) = Rig();

        await Run(prov, notes, One("pH min", 3, "仅报警"));

        var lim = Assert.Single(safety.Limits);
        Assert.Equal("pH", lim.Tag);
        Assert.Equal(3d, lim.Min);
        Assert.Null(lim.Max);
        Assert.Equal(SafetyAction.Alarm, lim.Action);
    }

    [Fact]
    public async Task 同一路信号的上下限并成一条_动作取最严的那档()
    {
        // 安全层按「通道 + 信号」认一条配方限值。上下限各发一条会互相顶掉，
        // 从前 Tr 就只能留住后设的那一个——手册那张表却是上下限成对给的
        var (prov, safety, notes) = Rig();

        await Run(prov, notes, new[] { ("Tr max", 120d, "仅报警"), ("Tr min", -10d, "中止本通道") });

        var lim = Assert.Single(safety.Limits);
        Assert.Equal("Tr", lim.Tag);
        Assert.Equal(120d, lim.Max);
        Assert.Equal(-10d, lim.Min);
        Assert.Equal(SafetyAction.AbortChannel, lim.Action);
    }

    [Fact]
    public async Task 温差上限展开成对称的一对()
    {
        // 手册的 T diff max 说的是「允许的 Tj 与 Tr 温差」——两侧都算越限
        var (prov, safety, notes) = Rig();

        await Run(prov, notes, One("T diff max", 25));

        var lim = Assert.Single(safety.Limits);
        Assert.Equal("dT", lim.Tag);
        Assert.Equal(25d, lim.Max);
        Assert.Equal(-25d, lim.Min);
    }

    [Fact]
    public async Task 转速上限盯的是实测转速()
    {
        var (prov, safety, notes) = Rig();

        await Run(prov, notes, One("Rmax", 800, "停止加热"));

        var lim = Assert.Single(safety.Limits);
        Assert.Equal("rpm", lim.Tag);
        Assert.Equal(800d, lim.Max);
        Assert.Equal(SafetyAction.StopHeating, lim.Action);
    }

    [Fact]
    public async Task 同通道同信号再改一次是换值不是叠加()
    {
        var (prov, safety, notes) = Rig();

        await Run(prov, notes, One("Tr max", 100, "仅报警"));
        await Run(prov, notes, One("Tr max", 80, "中止本通道"));

        var lim = Assert.Single(safety.Limits);
        Assert.Equal(80d, lim.Max);
        Assert.Equal(SafetyAction.AbortChannel, lim.Action);
    }

    [Fact]
    public async Task 不同信号各是各的一条()
    {
        var (prov, safety, notes) = Rig();

        await Run(prov, notes, new[] { ("Tr max", 100d, "仅报警"), ("pH min", 3d, "仅报警") });

        Assert.Equal(2, safety.Limits.Count);
    }

    [Fact]
    public async Task 认不出的参数名照实回报_不当作没写过()
    {
        var (prov, safety, notes) = Rig();

        await Run(prov, notes, One("浊度 max", 50));

        Assert.Empty(safety.Limits);
        Assert.Contains(notes, n => n.Contains("没认出"));
    }

    // ── E 级紧急程序（手册 §6 的 Tsafe / Rsafe）───────────────────────

    [Fact]
    public async Task 紧急程序参数交给安全层_不勾就没有()
    {
        var (prov, safety, notes) = Rig();

        await Run(prov, notes, One("Tr max", 120));
        Assert.Null(safety.EmergencyPlanOf(1));

        await Run(prov, notes, One("Tr max", 120),
                  ("eOn", true), ("tsafe", 25d), ("rsafeMode", "指定转速"), ("rsafe", 150d));

        var plan = safety.EmergencyPlanOf(1);
        Assert.NotNull(plan);
        Assert.Equal(25d, plan!.SafeTemp);
        Assert.Equal(150d, plan.SafeRpm);
        Assert.Contains(notes, n => n.Contains("紧急程序"));
    }

    [Fact]
    public async Task 保持当前转速就不下发转速()
    {
        var (prov, safety, notes) = Rig();

        await Run(prov, notes, One("Tr max", 120),
                  ("eOn", true), ("tsafe", 30d), ("rsafeMode", "保持当前"));

        var plan = safety.EmergencyPlanOf(1);
        Assert.Equal(30d, plan!.SafeTemp);
        Assert.Null(plan.SafeRpm);          // Hold：不动搅拌
    }

    [Fact]
    public async Task 清配方限值时紧急程序参数一起撤()
    {
        // 它跟配方限值同源同寿。留下来的话，下一炉会按上一炉的 Tsafe 收尾
        var (prov, safety, notes) = Rig();
        await Run(prov, notes, One("Tr max", 120), ("eOn", true), ("tsafe", 25d));

        safety.RemoveRecipeLimits(1);

        Assert.Null(safety.EmergencyPlanOf(1));
    }

    // ── 底线与清场 ──────────────────────────────────────────────────

    [Fact]
    public async Task 底线限值不受配方限值影响_清场也只清配方那部分()
    {
        var (prov, safety, notes) = Rig();
        safety.Add(new SafetyLimit(1, "Tr", null, 181, null, TimeSpan.Zero, SafetyAction.AbortChannel)
        { FromDeviceLimits = true });

        await Run(prov, notes, One("Tr max", 100, "仅报警"));
        safety.SetRecipeLimit(new SafetyLimit(2, "Tr", null, 90, null, TimeSpan.Zero, SafetyAction.Alarm));
        Assert.Equal(3, safety.Limits.Count);

        Assert.Equal(1, safety.RemoveRecipeLimits(1));

        // 底线那条和别的通道的都还在
        Assert.Contains(safety.Limits, l => l.FromDeviceLimits && l.Max == 181);
        Assert.Contains(safety.Limits, l => l.FromRecipe && l.Channel == 2);
        Assert.DoesNotContain(safety.Limits, l => l.FromRecipe && l.Channel == 1);
    }

    [Fact]
    public async Task 没挂安全层就只记录_不装作生效了()
    {
        var provider = new BuiltinCommandProvider(new AutoOperatorGate());   // 没注入 Safety
        var notes = new List<string>();

        await Run(provider, notes, One("Tr max", 100, "仅报警"));

        Assert.Contains(notes, n => n.Contains("仅记录"));
    }

    [Fact]
    public void 启动通道先清掉上一炉的配方限值_底线留着()
    {
        var engine = new RunEngine(new CommandCatalog(), new BuiltinCommandProvider(new AutoOperatorGate()),
                                   new ResourceArbiter(), new DataPipeline(),
                                   () => new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero));
        try
        {
            engine.Attach(new Channel(1, "X1", 0));
            engine.Safety.Add(new SafetyLimit(1, "Tr", null, 181, null, TimeSpan.Zero, SafetyAction.AbortChannel)
            { FromDeviceLimits = true });
            engine.Safety.SetRecipeLimit(new SafetyLimit(1, "Tr", null, 80, null, TimeSpan.Zero, SafetyAction.Alarm));

            engine.NewBatch("测试批次", "张三", "测试台面");
            engine.StartChannel(1, Harness.RecipeOf("等着",
                Harness.Mk(BuiltinCommands.Wait, ("dur", 60d))), "张三");

            Assert.DoesNotContain(engine.Safety.Limits, l => l.FromRecipe);
            Assert.Contains(engine.Safety.Limits, l => l.FromDeviceLimits);
        }
        finally
        {
            engine.AbortAll();
            engine.Dispose();
        }
    }

    // ── 老配方翻译 ──────────────────────────────────────────────────

    [Fact]
    public void 迁移把旧的监测量与条件搬进限值表()
    {
        var r = Harness.RecipeOf("旧", Harness.Mk(BuiltinCommands.Interlock,
            ("src", "釜内 Tr"), ("op", ">"), ("val", 100d), ("act", "中止本通道")));

        var notes = RecipeMigration.Apply(r);

        var step = r.Steps[0];
        Assert.Equal(BuiltinCommands.Interlock, step.CommandId);   // Id 不动，文件还认得
        var row = Assert.Single(step.Rows!);
        Assert.Equal("Tr max", row.Str("par"));
        Assert.Equal(100d, row.Num("val"));
        Assert.Equal("中止本通道", row.Str("act"));
        Assert.False(step.Parameters.Has("src"));                  // 旧键清干净
        Assert.Single(notes);
    }

    [Fact]
    public void 迁移把小于号翻成下限()
    {
        var r = Harness.RecipeOf("旧", Harness.Mk(BuiltinCommands.Interlock,
            ("src", "pH"), ("op", "<"), ("val", 3d), ("act", "暂停实验")));

        RecipeMigration.Apply(r);

        var row = Assert.Single(r.Steps[0].Rows!);
        Assert.Equal("pH min", row.Str("par"));
        Assert.Equal("仅报警", row.Str("act"));      // 旧动作值同一趟翻过来
    }

    [Fact]
    public void 迁移不硬翻已经没有的信号()
    {
        // 浊度探头早从设备库撤了。硬翻成一条限值反而更糟：读不到值会按
        // 传感器失效触发。照实丢掉并说明
        var r = Harness.RecipeOf("旧", Harness.Mk(BuiltinCommands.Interlock,
            ("src", "浊度"), ("op", ">"), ("val", 50d), ("act", "仅报警")));

        var notes = RecipeMigration.Apply(r);

        Assert.Empty(r.Steps[0].Rows!);
        Assert.Contains(notes, n => n.Contains("未保留"));
    }

    [Fact]
    public void 新参数表不需要翻译_迁移不惊动()
    {
        var step = new Step
        {
            CommandId = BuiltinCommands.Interlock,
            Rows = new List<ParameterSet>
            { ParameterSet.Of(("par", "Tr max"), ("val", 100d), ("act", "中止本通道")) }
        };
        var r = Harness.RecipeOf("新", step);

        Assert.Empty(RecipeMigration.Apply(r));
    }
}
