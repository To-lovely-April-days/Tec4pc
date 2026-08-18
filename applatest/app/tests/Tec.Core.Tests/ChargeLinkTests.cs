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

        Assert.Equal(2, done.Volumes.Count);
        Assert.Equal(30, done.Volumes[0].Before);
        Assert.Equal(122.12, done.Volumes[0].After);
        Assert.Equal(122.12, recipe.Steps[0].Parameters.Num("vol"));
        // 苯甲酸 12.212 g ÷ 1.266 g/mL = 9.6462… → 圆到 9.65，不往配方里塞一长串小数
        Assert.Equal(9.65, recipe.Steps[1].Parameters.Num("vol"));
    }

    [Fact]
    public void 已经一致的步骤不重复改也不进改动清单()
    {
        var recipe = RecipeWith(("甲苯", 122.12));
        var links = ChargeLink.Match(recipe, Catalog(), Stoichiometry.Solve(Table(), Lib));
        // 体积一致就不进体积改动清单；第一次按会建立引用，那是另一码事
        Assert.Empty(ChargeLink.Apply(links).Volumes);
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

        Assert.Empty(ChargeLink.Apply(links).Volumes);
        Assert.Equal(12, recipe.Steps[0].Parameters.Num("vol"));
    }

    // ── 校验器 ──────────────────────────────────────────────────────

    [Fact]
    public void 校验条上报出体积不一致()
    {
        var charge = Stoichiometry.Solve(Table(), Lib);
        var recipe = RecipeWith(("甲苯", 30));
        var issues = RecipeValidator.Validate(recipe, Catalog(), charge: charge);

        var one = Assert.Single(issues, i => i.Code == "charge-mismatch");
        Assert.Equal(IssueLevel.Warning, one.Level);
        Assert.Contains("30", one.Message);
        Assert.Contains("122.12", one.Message);
        // 校验条认步骤的稳定标识——插一步之后下标错位，Id 不会
        Assert.Equal(recipe.Steps[0].StepId, one.StepId);
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

    // ── 引用链（CH-4.1 / 4.2）────────────────────────────────────────

    [Fact]
    public void 应用会建立引用_名字对齐到行名()
    {
        var charge = Stoichiometry.Solve(Table(), Lib);
        var recipe = RecipeWith(("　甲苯 ", 30));           // 名字还带着全角空格
        var done = ChargeLink.Apply(ChargeLink.Match(recipe, Catalog(), charge));

        var linked = Assert.Single(done.NewLinks);
        var p = recipe.Steps[0].Parameters;
        Assert.Equal(charge.Lines[1].Item.Id, p.Str(ChargeLink.ChemKey));
        Assert.True(p.Flag(ChargeLink.LinkedKey));
        Assert.Equal("甲苯", p.Str(ChargeLink.LiquidKey));   // 引用建立后行名是权威
        Assert.Equal(linked.Step, recipe.Steps[0]);
    }

    [Fact]
    public void 再按一次不重复建引用()
    {
        var charge = Stoichiometry.Solve(Table(), Lib);
        var recipe = RecipeWith(("甲苯", 30));
        ChargeLink.Apply(ChargeLink.Match(recipe, Catalog(), charge));

        var again = ChargeLink.Apply(ChargeLink.Match(recipe, Catalog(), charge));
        Assert.Empty(again.NewLinks);
        Assert.Empty(again.Volumes);
    }

    [Fact]
    public void 建了引用之后改组分名也断不了()
    {
        var t = Table();
        var recipe = RecipeWith(("甲苯", 30));
        ChargeLink.Apply(ChargeLink.Match(recipe, Catalog(), Stoichiometry.Solve(t, Lib)));

        t.Items[1].Name = "甲苯（回收）";                    // 配料表里改了名
        var e = Assert.Single(ChargeLink.Match(recipe, Catalog(), Stoichiometry.Solve(t, Lib)));

        Assert.True(e.MatchedById);
        Assert.Equal("甲苯（回收）", e.Line!.Item.Name);
    }

    [Fact]
    public void 引用的行删了就是断了_不退回按名对()
    {
        // 退回按名对的话，可能悄悄对上一个恰好同名的新行——比明说「断了」危险得多
        var t = Table();
        var recipe = RecipeWith(("甲苯", 30));
        ChargeLink.Apply(ChargeLink.Match(recipe, Catalog(), Stoichiometry.Solve(t, Lib)));

        t.Items.RemoveAt(1);
        t.Items.Add(new ChargeItem
        { Cas = "108-88-3", Name = "甲苯", Role = ChargeRole.Solvent,
          Basis = ChargeBasis.Volumes, Amount = 5 });        // 同名的新行

        var e = Assert.Single(ChargeLink.Match(recipe, Catalog(), Stoichiometry.Solve(t, Lib)));
        Assert.True(e.RefGone);
        Assert.False(e.Matched);

        var issues = RecipeValidator.Validate(recipe, Catalog(),
                                              charge: Stoichiometry.Solve(t, Lib));
        Assert.Contains(issues, i => i.Code == "charge-refgone");
    }

    [Fact]
    public void 跟随只动引用着的步骤()
    {
        var t = Table();
        var recipe = RecipeWith(("甲苯", 30), ("苯甲酸", 1));
        var links = ChargeLink.Match(recipe, Catalog(), Stoichiometry.Solve(t, Lib));
        // 只给甲苯那步建引用；苯甲酸那步保持按名对
        ChargeLink.Apply(new[] { links[0] });

        t.Items[1].Amount = 5;                               // 倍量 10 → 5
        var after = ChargeLink.Match(recipe, Catalog(), Stoichiometry.Solve(t, Lib));
        var done = ChargeLink.Follow(after);

        var one = Assert.Single(done);
        Assert.Equal("甲苯", one.Entry.Liquid);
        Assert.Equal(61.06, one.After);                      // 12.212 g × 5 mL/g
        Assert.Equal(61.06, recipe.Steps[0].Parameters.Num("vol"));
        Assert.Equal(1, recipe.Steps[1].Parameters.Num("vol"));   // 没引用的不动
    }

    [Fact]
    public void 手改体积就脱离_跟随不再碰它()
    {
        var t = Table();
        var recipe = RecipeWith(("甲苯", 30));
        ChargeLink.Apply(ChargeLink.Match(recipe, Catalog(), Stoichiometry.Solve(t, Lib)));

        // 操作人手动把体积改成 100 —— 编辑器同时清跟随标志
        recipe.Steps[0].Parameters["vol"] = 100.0;
        Assert.True(ChargeLink.Detach(recipe.Steps[0]));

        var links = ChargeLink.Match(recipe, Catalog(), Stoichiometry.Solve(t, Lib));
        Assert.Empty(ChargeLink.Follow(links));
        Assert.Equal(100, recipe.Steps[0].Parameters.Num("vol"));

        // 校验条把脱离状态喊出来，而不是当成普通的「不一致」
        var issues = RecipeValidator.Validate(recipe, Catalog(),
                                              charge: Stoichiometry.Solve(t, Lib));
        Assert.Contains(issues, i => i.Code == "charge-detached");
        Assert.DoesNotContain(issues, i => i.Code == "charge-mismatch");
    }

    [Fact]
    public void 跟随着还没同步上的校验条说清会自动追平()
    {
        var t = Table();
        var recipe = RecipeWith(("甲苯", 30));
        ChargeLink.Apply(ChargeLink.Match(recipe, Catalog(), Stoichiometry.Solve(t, Lib)));

        t.Items[1].Amount = 5;                               // 配料表变了，还没人打开配料表页
        var issues = RecipeValidator.Validate(recipe, Catalog(),
                                              charge: Stoichiometry.Solve(t, Lib));
        var one = Assert.Single(issues, i => i.Code == "charge-follow");
        Assert.Contains("自动同步", one.Message);
    }

    [Fact]
    public void 脱离的步骤体积恰好一致就不唠叨()
    {
        var t = Table();
        var recipe = RecipeWith(("甲苯", 30));
        ChargeLink.Apply(ChargeLink.Match(recipe, Catalog(), Stoichiometry.Solve(t, Lib)));
        ChargeLink.Detach(recipe.Steps[0]);                  // 脱离但没改数

        var issues = RecipeValidator.Validate(recipe, Catalog(),
                                              charge: Stoichiometry.Solve(t, Lib));
        Assert.DoesNotContain(issues, i => i.Code is "charge-detached" or "charge-mismatch");
    }

    [Fact]
    public void 不传配料表的话校验器一句配料的话都不说()
    {
        // 校验器是所有页面都在跑的东西，多一条无关警告就多一次误导
        var issues = RecipeValidator.Validate(RecipeWith(("甲苯", 30)), Catalog());
        Assert.DoesNotContain(issues, i => i.Code!.StartsWith("charge", StringComparison.Ordinal));
    }
}
