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
/// 「改限值」（原「安全联锁」）。从前那条指令只写一行日志，什么都不联锁——
/// 配方上写着「超 100 ℃ 停实验」，超了却什么也不会发生，比没有这一条更糟：
/// 编配方的人以为有人兜着。现在它把限值真的登记进安全层，由 SafetyMonitor
/// 独立于配方 1 Hz 求值；这里盯的是**登记的语义**：
/// 换值不叠加、底线不受影响、下一炉清场、老配方的动作值翻得过来。
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

    private static Task Run(BuiltinCommandProvider provider, List<string> notes,
                            params (string Key, object? Value)[] p)
    {
        var h = provider.Resolve(BuiltinCommands.Interlock)!;
        var ctx = new CommandContext
        {
            Channel = 1,
            Capabilities = new CapabilitySet(),
            Now = () => new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero),
            Note = notes.Add
        };
        return h.ExecuteAsync(ctx, ParameterSet.Of(p), CancellationToken.None);
    }

    // ── 登记的语义 ──────────────────────────────────────────────────

    [Fact]
    public async Task 改限值真的把限值交给安全层()
    {
        var (prov, safety, notes) = Rig();

        await Run(prov, notes, ("src", "釜内 Tr"), ("op", ">"), ("val", 100d), ("act", "停止加料"));

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
    public async Task 小于号设的是下限()
    {
        var (prov, safety, notes) = Rig();

        await Run(prov, notes, ("src", "pH"), ("op", "<"), ("val", 3d), ("act", "仅报警"));

        var lim = Assert.Single(safety.Limits);
        Assert.Equal("pH", lim.Tag);
        Assert.Equal(3d, lim.Min);
        Assert.Null(lim.Max);
        Assert.Equal(SafetyAction.Alarm, lim.Action);
    }

    [Fact]
    public async Task 同通道同监测量再改一次是换值不是叠加()
    {
        // 后面的步骤改的是同一条限值。叠成两条的话，旧的那条永远压着新的——
        // 收严了等于没收
        var (prov, safety, notes) = Rig();

        await Run(prov, notes, ("src", "釜内 Tr"), ("op", ">"), ("val", 100d), ("act", "仅报警"));
        await Run(prov, notes, ("src", "釜内 Tr"), ("op", ">"), ("val", 80d), ("act", "中止本通道"));

        var lim = Assert.Single(safety.Limits);
        Assert.Equal(80d, lim.Max);
        Assert.Equal(SafetyAction.AbortChannel, lim.Action);
    }

    [Fact]
    public async Task 不同监测量各是各的一条()
    {
        var (prov, safety, notes) = Rig();

        await Run(prov, notes, ("src", "釜内 Tr"), ("op", ">"), ("val", 100d), ("act", "仅报警"));
        await Run(prov, notes, ("src", "pH"), ("op", "<"), ("val", 3d), ("act", "仅报警"));

        Assert.Equal(2, safety.Limits.Count);
    }

    [Fact]
    public async Task 底线限值不受配方限值影响_清场也只清配方那部分()
    {
        var (prov, safety, notes) = Rig();
        safety.Add(new SafetyLimit(1, "Tr", null, 181, null, TimeSpan.Zero, SafetyAction.AbortChannel)
        { FromDeviceLimits = true });

        await Run(prov, notes, ("src", "釜内 Tr"), ("op", ">"), ("val", 100d), ("act", "仅报警"));
        safety.SetRecipeLimit(new SafetyLimit(2, "Tr", null, 90, null, TimeSpan.Zero, SafetyAction.Alarm));
        Assert.Equal(3, safety.Limits.Count);

        Assert.Equal(1, safety.RemoveRecipeLimits(1));

        // 底线那条和别的通道的都还在
        Assert.Contains(safety.Limits, l => l.FromDeviceLimits && l.Max == 181);
        Assert.Contains(safety.Limits, l => l.FromRecipe && l.Channel == 2);
        Assert.DoesNotContain(safety.Limits, l => l.FromRecipe && l.Channel == 1);
    }

    [Fact]
    public async Task 老配方的旧动作值兜底翻得过来()
    {
        // RecipeMigration 只在打开文件时翻一次；没走过迁移的老参数直接执行到这儿，
        // 也不能跑偏
        var (prov, safety, notes) = Rig();

        await Run(prov, notes, ("src", "釜内 Tr"), ("op", ">"), ("val", 100d), ("act", "停止实验"));
        await Run(prov, notes, ("src", "浊度"), ("op", ">"), ("val", 50d), ("act", "暂停实验"));

        Assert.Equal(2, safety.Limits.Count);
        Assert.Contains(safety.Limits, l => l.Tag == "Tr" && l.Action == SafetyAction.AbortChannel);
        Assert.Contains(safety.Limits, l => l.Tag == "turb" && l.Action == SafetyAction.Alarm);
    }

    [Fact]
    public async Task 没挂安全层就只记录_不装作生效了()
    {
        var provider = new BuiltinCommandProvider(new AutoOperatorGate());   // 没注入 Safety
        var notes = new List<string>();

        await Run(provider, notes, ("src", "釜内 Tr"), ("op", ">"), ("val", 100d), ("act", "仅报警"));

        Assert.Contains(notes, n => n.Contains("仅记录"));
    }

    // ── 下一炉清场 ──────────────────────────────────────────────────

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
    public void 迁移把停止实验翻成中止本通道()
    {
        var r = Harness.RecipeOf("旧", Harness.Mk(BuiltinCommands.Interlock,
            ("src", "釜内 Tr"), ("op", ">"), ("val", 100d), ("act", "停止实验")));

        var notes = RecipeMigration.Apply(r);

        Assert.Equal("中止本通道", r.Steps[0].Parameters.Str("act"));
        Assert.Equal(BuiltinCommands.Interlock, r.Steps[0].CommandId);   // Id 不动，文件还认得
        Assert.Single(notes);
    }

    [Fact]
    public void 迁移把暂停实验翻成仅报警()
    {
        var r = Harness.RecipeOf("旧", Harness.Mk(BuiltinCommands.Interlock,
            ("src", "pH"), ("op", "<"), ("val", 3d), ("act", "暂停实验")));

        RecipeMigration.Apply(r);

        Assert.Equal("仅报警", r.Steps[0].Parameters.Str("act"));
    }

    [Fact]
    public void 新动作值不需要翻译_迁移不惊动()
    {
        var r = Harness.RecipeOf("新", Harness.Mk(BuiltinCommands.Interlock,
            ("src", "釜内 Tr"), ("op", ">"), ("val", 100d), ("act", "中止本通道")));

        Assert.Empty(RecipeMigration.Apply(r));
    }
}
