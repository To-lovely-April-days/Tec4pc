using Tec.Core.Chemistry;
using Tec.Core.Compounds;
using Tec.Core.Recipes;
using Tec.Driver.Abi;
using Xunit;

namespace Tec.Core.Tests;

public class ChargeLinkTests
{
    private static readonly Compound Benzoic = new()
    { Cas = "65-85-0", Name = "苯甲酸", Mw = 122.12, Density = 1.266, Purity = 99.5 };

    private static readonly Compound Toluene = new()
    { Cas = "108-88-3", Name = "甲苯", Mw = 92.14, Density = 0.8669 };

    private static readonly Compound[] Lib = { Benzoic, Toluene };

    /// <summary>限制试剂 12.212 g 苯甲酸 + 10 倍量甲苯 = 甲苯 122.12 mL。</summary>
    private static ChargeTable Table()
    {
        var t = new ChargeTable();
        t.Items.Add(new ChargeItem
        {
            Cas = "65-85-0", Name = "苯甲酸", Role = ChargeRole.Limiting,
            Basis = ChargeBasis.Quantity, Amount = 12.212, Unit = ChargeUnit.Gram
        });
        t.Items.Add(new ChargeItem
        {
            Cas = "108-88-3", Name = "甲苯", Role = ChargeRole.Solvent,
            Basis = ChargeBasis.Volumes, Amount = 10
        });
        return t;
    }

    private static Recipe RecipeWith(params (string Liq, double Vol)[] doses)
    {
        var r = new Recipe { Name = "对表用" };
        foreach (var (liq, vol) in doses)
            r.Steps.Add(new Step
            {
                CommandId = CommandSpecs.Dose,
                Parameters = ParameterSet.Of(("pump", "加料泵 1"), ("liq", liq), ("vol", vol),
                                             ("rate", 5.0), ("sync", true))
            });
        return r;
    }

    private static Tec.Core.Catalog.CommandCatalog Catalog()
    {
        var c = new Tec.Core.Catalog.CommandCatalog();
        c.Register(new Tec.Drivers.Simulator.Rd105ReactorDriver().Commands);
        c.Register(new Tec.Drivers.Simulator.DosingPumpDriver().Commands);
        return c;
    }

    [Fact]
    public void 按料液名对上配料表那一行()
    {
        var charge = Stoichiometry.Solve(Table(), Lib);
        var links = ChargeLink.Match(RecipeWith(("甲苯", 30)), Catalog(), charge);

        var e = Assert.Single(links);
        Assert.True(e.Matched);
        Assert.Equal("甲苯", e.Line!.Item.Name);
        Assert.Equal(122.12, e.PlannedVolume!.Value, 4);
        Assert.Equal(30, e.StepVolume);
        Assert.True(e.Differs);
    }

    [Fact]
    public void 两头空白和全角空格不该让同一个名字对不上()
    {
        var charge = Stoichiometry.Solve(Table(), Lib);
        var links = ChargeLink.Match(RecipeWith(("　甲苯 ", 30)), Catalog(), charge);
        Assert.True(Assert.Single(links).Matched);
    }

    [Fact]
    public void 配料表里没有这个料液就说对不上()
    {
        var charge = Stoichiometry.Solve(Table(), Lib);
        var e = Assert.Single(ChargeLink.Match(RecipeWith(("乙酸乙酯", 30)), Catalog(), charge));

        Assert.False(e.Matched);
        Assert.Null(e.PlannedVolume);
        Assert.False(e.Differs);          // 对不上就谈不上"差多少"
    }

    [Fact]
    public void 目标产物不参与加料匹配()
    {
        // 产物是出来的东西，不会有哪一步去"加"它
        var t = Table();
        t.Items.Add(new ChargeItem
        {
            Name = "产物 P", Role = ChargeRole.Product, Basis = ChargeBasis.Equivalents,
            Amount = 1, Mw = 167.12
        });

        var charge = Stoichiometry.Solve(t, Lib);
        Assert.False(Assert.Single(ChargeLink.Match(RecipeWith(("产物 P", 5)), Catalog(), charge)).Matched);
    }

    [Fact]
    public void 应用之后体积就是配料表算出来的那个数()
    {
        var recipe = RecipeWith(("甲苯", 30), ("苯甲酸", 1));
        var charge = Stoichiometry.Solve(Table(), Lib);
        var links = ChargeLink.Match(recipe, Catalog(), charge);

        var done = ChargeLink.Apply(links);

        Assert.Equal(2, done.Count);
        Assert.Equal(30, done[0].Before);
        Assert.Equal(122.12, done[0].After);
        Assert.Equal(122.12, recipe.Steps[0].Parameters.Num("vol"));
        // 苯甲酸 12.212 g ÷ 1.266 g/mL = 9.6462… → 圆到 9.65，不往配方里塞一长串小数
        Assert.Equal(9.65, recipe.Steps[1].Parameters.Num("vol"));
    }

    [Fact]
    public void 已经一致的步骤不重复改也不进改动清单()
    {
        var recipe = RecipeWith(("甲苯", 122.12));
        var links = ChargeLink.Match(recipe, Catalog(), Stoichiometry.Solve(Table(), Lib));
        Assert.Empty(ChargeLink.Apply(links));
    }

    [Fact]
    public void 算不出体积的行不去覆盖配方里那个数()
    {
        // 缺密度 → 配料表给不出体积。这时候把配方里人填的那个数抹掉是纯粹的破坏
        var t = new ChargeTable();
        t.Items.Add(new ChargeItem
        {
            Name = "某固体", Role = ChargeRole.Limiting, Basis = ChargeBasis.Quantity,
            Amount = 10, Unit = ChargeUnit.Gram, Mw = 100
        });

        var recipe = RecipeWith(("某固体", 12));
        var links = ChargeLink.Match(recipe, Catalog(), Stoichiometry.Solve(t, Lib));

        Assert.Empty(ChargeLink.Apply(links));
        Assert.Equal(12, recipe.Steps[0].Parameters.Num("vol"));
    }

    // ── 校验器 ──────────────────────────────────────────────────────

    [Fact]
    public void 校验条上报出体积不一致()
    {
        var charge = Stoichiometry.Solve(Table(), Lib);
        var issues = RecipeValidator.Validate(RecipeWith(("甲苯", 30)), Catalog(), charge: charge);

        var one = Assert.Single(issues, i => i.Code == "charge-mismatch");
        Assert.Equal(IssueLevel.Warning, one.Level);
        Assert.Contains("30", one.Message);
        Assert.Contains("122.12", one.Message);
        Assert.Equal(0, one.StepIndex);
    }

    [Fact]
    public void 校验条上报出料液在配料表里查无此名()
    {
        var charge = Stoichiometry.Solve(Table(), Lib);
        var issues = RecipeValidator.Validate(RecipeWith(("乙酸乙酯", 30)), Catalog(), charge: charge);

        var one = Assert.Single(issues, i => i.Code == "charge-unlinked");
        Assert.Contains("乙酸乙酯", one.Message);
    }

    [Fact]
    public void 配料表是空的就不唠叨对不上()
    {
        // 只跑温控曲线的实验根本不需要配料表，不该被四条警告刷屏
        var empty = Stoichiometry.Solve(new ChargeTable(), Lib);
        var issues = RecipeValidator.Validate(RecipeWith(("硝酸 65%", 30)), Catalog(), charge: empty);
        Assert.DoesNotContain(issues, i => i.Code!.StartsWith("charge", StringComparison.Ordinal));
    }

    [Fact]
    public void 配料表自己的毛病也一并报到校验条上()
    {
        var t = Table();
        t.Items[0].Role = ChargeRole.Reagent;              // 没有限制试剂了
        var issues = RecipeValidator.Validate(RecipeWith(("甲苯", 30)), Catalog(),
                                              charge: Stoichiometry.Solve(t, Lib));

        Assert.Contains(issues, i => i.Code == "charge" && i.Message.Contains("没有指定限制试剂"));
    }

    [Fact]
    public void 算不出体积就说算不出并说缺什么()
    {
        var t = new ChargeTable();
        t.Items.Add(new ChargeItem
        {
            Name = "某固体", Role = ChargeRole.Limiting, Basis = ChargeBasis.Quantity,
            Amount = 10, Unit = ChargeUnit.Gram, Mw = 100
        });

        var issues = RecipeValidator.Validate(RecipeWith(("某固体", 12)), Catalog(),
                                              charge: Stoichiometry.Solve(t, Lib));
        var one = Assert.Single(issues, i => i.Code == "charge-novolume");
        // 原因可能记在「缺什么」里也可能记在「按什么假设算的」里，两边都得看，
        // 否则这条警告只剩一句「量还没填全」的空话
        Assert.Contains("密度", one.Message);
    }

    [Fact]
    public void 不传配料表的话校验器一句配料的话都不说()
    {
        // 校验器是所有页面都在跑的东西，多一条无关警告就多一次误导
        var issues = RecipeValidator.Validate(RecipeWith(("甲苯", 30)), Catalog());
        Assert.DoesNotContain(issues, i => i.Code!.StartsWith("charge", StringComparison.Ordinal));
    }
}
