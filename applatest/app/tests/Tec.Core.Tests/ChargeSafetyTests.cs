using Tec.Core.Chemistry;
using Tec.Core.Recipes;
using Tec.Driver.Abi;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 校验与拦截那一批（CH-5.1 / 5.2 / 5.6 / 4.5）：
/// 往热釜里泵低沸点液体拦下（Error），工艺会把液体组分冻住的提醒（Warning），
/// 釜容占到八成提醒，固体走泵拦下。温度取排期的估算链。
/// </summary>
public class ChargeSafetyTests
{
    private static Tec.Core.Catalog.CommandCatalog Catalog()
    {
        var c = new Tec.Core.Catalog.CommandCatalog();
        c.Register(new Tec.Drivers.Simulator.Rd105ReactorDriver().Commands);
        c.Register(new Tec.Drivers.Simulator.DosingPumpDriver().Commands);
        return c;
    }

    private static Recipe HotThenDose(double target, string liq) => Harness.RecipeOf("热加料",
        Harness.Mk(CommandSpecs.Control, ("target", target), ("rate", 10d), ("obj", "釜内 Tr")),
        Harness.Mk(CommandSpecs.Dose, ("pump", "加料泵 1"), ("liq", liq),
                   ("vol", 10d), ("rate", 1d), ("sync", true)));

    /// <summary>一行乙醇（液，沸点 78.4，都在行上——启动闸不吃活库）。</summary>
    private static ChargeTable Ethanol(string phase = "液")
    {
        var t = new ChargeTable();
        t.Items.Add(new ChargeItem
        {
            Name = "乙醇", Role = ChargeRole.Limiting, Basis = ChargeBasis.Quantity,
            Amount = 10, Unit = ChargeUnit.Gram, Mw = 46.07, Density = 0.789,
            Bp = 78.4, Mp = -114.1, Phase = phase
        });
        return t;
    }

    // ── CH-5.1 沸点阻断 ─────────────────────────────────────────────

    [Fact]
    public void 往九十度的釜里泵乙醇拦下并提示沸点()
    {
        var issues = RecipeValidator.Validate(HotThenDose(90, "乙醇"), Catalog(),
                                              charge: Stoichiometry.Solve(Ethanol()));
        var one = Assert.Single(issues, i => i.Code == "charge-bp");
        Assert.Equal(IssueLevel.Error, one.Level);          // Error = 真的挡启动（0069 的闸）
        Assert.Contains("78.4", one.Message);
        Assert.Contains("90", one.Message);
        Assert.NotNull(one.StepId);
    }

    [Fact]
    public void 四十度加乙醇不拦()
    {
        var issues = RecipeValidator.Validate(HotThenDose(40, "乙醇"), Catalog(),
                                              charge: Stoichiometry.Solve(Ethanol()));
        Assert.DoesNotContain(issues, i => i.Code == "charge-bp");
    }

    [Fact]
    public void 没有沸点数据不拦也不猜()
    {
        var t = Ethanol();
        t.Items[0].Bp = null;
        var issues = RecipeValidator.Validate(HotThenDose(90, "乙醇"), Catalog(),
                                              charge: Stoichiometry.Solve(t));
        Assert.DoesNotContain(issues, i => i.Code == "charge-bp");
    }

    // ── CH-5.2 熔点警告（放行）────────────────────────────────────────

    /// <summary>冰乙酸：熔点 16.6 ℃ 的液体，降到 10 ℃ 就在釜里凝住。</summary>
    private static ChargeTable AceticAcid(string phase = "液")
    {
        var t = new ChargeTable();
        t.Items.Add(new ChargeItem
        {
            Name = "冰乙酸", Role = ChargeRole.Limiting, Basis = ChargeBasis.Quantity,
            Amount = 30, Unit = ChargeUnit.Milliliter, Mw = 60.05, Density = 1.049,
            Mp = 16.6, Phase = phase
        });
        return t;
    }

    private static Recipe CoolTo(double target) => Harness.RecipeOf("降温",
        Harness.Mk(CommandSpecs.Control, ("target", target), ("rate", 5d), ("obj", "釜内 Tr")));

    [Fact]
    public void 降到十度提醒冰乙酸会凝固但只是警告()
    {
        var issues = RecipeValidator.Validate(CoolTo(10), Catalog(),
                                              charge: Stoichiometry.Solve(AceticAcid()));
        var one = Assert.Single(issues, i => i.Code == "charge-mp");
        Assert.Equal(IssueLevel.Warning, one.Level);        // 结晶可能正是设计意图，不拦
        Assert.Contains("16.6", one.Message);
        Assert.Contains("冰乙酸", one.Message);
    }

    [Fact]
    public void 不降温就不提熔点()
    {
        var issues = RecipeValidator.Validate(CoolTo(25), Catalog(),
                                              charge: Stoichiometry.Solve(AceticAcid()));
        Assert.DoesNotContain(issues, i => i.Code == "charge-mp");
    }

    [Fact]
    public void 固体行不提熔点()
    {
        // 苯甲酸本来就是固体，「低于熔点」对它是废话
        var t = new ChargeTable();
        t.Items.Add(new ChargeItem
        {
            Name = "苯甲酸", Role = ChargeRole.Limiting, Basis = ChargeBasis.Quantity,
            Amount = 12, Unit = ChargeUnit.Gram, Mw = 122.12, Mp = 122.4, Phase = "固"
        });
        var issues = RecipeValidator.Validate(CoolTo(10), Catalog(), charge: Stoichiometry.Solve(t));
        Assert.DoesNotContain(issues, i => i.Code == "charge-mp");
    }

    [Fact]
    public void 相态没填按克称的也不提熔点()
    {
        // 按克称的多半是固体，猜它是液体去吓唬人不划算；按毫升量的才推断成液体
        var t = new ChargeTable();
        t.Items.Add(new ChargeItem
        {
            Name = "苯甲酸", Role = ChargeRole.Limiting, Basis = ChargeBasis.Quantity,
            Amount = 12, Unit = ChargeUnit.Gram, Mw = 122.12, Mp = 122.4
        });
        var issues = RecipeValidator.Validate(CoolTo(10), Catalog(), charge: Stoichiometry.Solve(t));
        Assert.DoesNotContain(issues, i => i.Code == "charge-mp");
    }

    // ── CH-4.5 固体不走泵 ───────────────────────────────────────────

    [Fact]
    public void 固体接到泵加料步骤拦下()
    {
        var issues = RecipeValidator.Validate(HotThenDose(40, "乙醇"), Catalog(),
                                              charge: Stoichiometry.Solve(Ethanol(phase: "固")));
        var one = Assert.Single(issues, i => i.Code == "charge-solid");
        Assert.Equal(IssueLevel.Error, one.Level);
        Assert.NotNull(one.StepId);
    }

    [Fact]
    public void 标了液或没标相态的不拦()
    {
        Assert.DoesNotContain(
            RecipeValidator.Validate(HotThenDose(40, "乙醇"), Catalog(),
                                     charge: Stoichiometry.Solve(Ethanol(phase: "液"))),
            i => i.Code == "charge-solid");
        Assert.DoesNotContain(
            RecipeValidator.Validate(HotThenDose(40, "乙醇"), Catalog(),
                                     charge: Stoichiometry.Solve(Ethanol(phase: ""))),
            i => i.Code == "charge-solid");
    }

    // ── CH-5.6 釜容 80 % ────────────────────────────────────────────

    [Fact]
    public void 配料合计占到八成半提醒但不算超容()
    {
        var t = Ethanol();
        t.Items[0].Amount = 67.07;                          // ÷0.789 ≈ 85 mL
        t.VesselVolume = 100;
        var r = Stoichiometry.Solve(t);
        Assert.False(r.OverVessel);
        Assert.Contains(r.Problems, p => p.Contains("80 %"));
    }

    [Fact]
    public void 超了釜容就报超容那条不再重复八成那条()
    {
        var t = Ethanol();
        t.Items[0].Amount = 86.79;                          // ≈110 mL
        t.VesselVolume = 100;
        var r = Stoichiometry.Solve(t);
        Assert.True(r.OverVessel);
        Assert.Contains(r.Problems, p => p.Contains("超过釜容"));
        Assert.DoesNotContain(r.Problems, p => p.Contains("80 %"));
    }

    [Fact]
    public async Task 累计加料占到泵量程八成校验条提醒()
    {
        await using var h = new Harness(600);
        var channel = await h.ReactorChannelAsync(1, withPump: true);   // Harness 的注射器 50 mL

        var warn = Harness.RecipeOf("九成", Harness.Mk(CommandSpecs.Dose,
            ("pump", "加料泵 1"), ("liq", "水"), ("vol", 45d), ("rate", 1d), ("sync", true)));
        var issues = RecipeValidator.Validate(warn, h.Catalog, channel);
        var one = Assert.Single(issues, i => i.Code == "volume-80");
        Assert.Equal(IssueLevel.Warning, one.Level);
        Assert.Contains("90 %", one.Message);

        // 真超了走原来的 Error，不再重复 80 % 那条
        var over = Harness.RecipeOf("超了", Harness.Mk(CommandSpecs.Dose,
            ("pump", "加料泵 1"), ("liq", "水"), ("vol", 55d), ("rate", 1d), ("sync", true)));
        var issues2 = RecipeValidator.Validate(over, h.Catalog, channel);
        Assert.Contains(issues2, i => i.Code == "volume" && i.Level == IssueLevel.Error);
        Assert.DoesNotContain(issues2, i => i.Code == "volume-80");
    }

    // ── 相态与沸熔点过快照与落盘 ────────────────────────────────────

    [Fact]
    public void 连库把相态沸点熔点一起带过来_行上写了的赢()
    {
        var c = new Tec.Core.Compounds.Compound
        { Cas = "64-17-5", Name = "乙醇", Bp = 78.4, Mp = -114.1, Phase = "液" };
        var item = new ChargeItem { Cas = "64-17-5", Name = "乙醇" };
        ChargeSnapshot.Link(item, c, 1, DateTimeOffset.Now);
        Assert.Equal(78.4, item.Bp);
        Assert.Equal(-114.1, item.Mp);
        Assert.Equal("液", item.Phase);

        var solid = new ChargeItem { Cas = "64-17-5", Name = "乙醇溶液", Phase = "液" };
        var solidLib = new Tec.Core.Compounds.Compound
        { Cas = "64-17-5", Name = "苯甲酸", Phase = "固" };
        ChargeSnapshot.Link(solid, solidLib, 1, DateTimeOffset.Now);
        Assert.Equal("液", solid.Phase);                    // 行上的投料形态不被库里的常态盖掉
    }

    [Fact]
    public void 相态沸熔点过文档往返不丢()
    {
        var t = Ethanol();
        var back = Tec.Core.Persistence.TecFiles.ToModel(Tec.Core.Persistence.TecFiles.ToDoc(t));
        Assert.Equal("液", back.Items[0].Phase);
        Assert.Equal(78.4, back.Items[0].Bp);
        Assert.Equal(-114.1, back.Items[0].Mp);
    }
}
