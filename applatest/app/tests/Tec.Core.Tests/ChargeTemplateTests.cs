using Tec.Core.Chemistry;
using Tec.Core.Persistence;
using Tec.Core.Recipes;
using Tec.Driver.Abi;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 配方 + 配料表结伴存取（模板库，CH-3.7 / CH-7.3 的一半）。
/// 核心回归只有一条：**存进库再应用出来，加料步骤的引用不断**——
/// 0070 之后配方持有配料行 Id，只存步骤的话应用到别处 chemId 全悬空。
/// </summary>
public class ChargeTemplateTests
{
    private static ChargeTable Table()
    {
        var t = new ChargeTable { VesselVolume = 250 };
        t.Items.Add(new ChargeItem
        {
            Name = "苯甲酸", Cas = "65-85-0", Role = ChargeRole.Limiting,
            Basis = ChargeBasis.Quantity, Amount = 12.212, Unit = ChargeUnit.Gram,
            Mw = 122.12, Density = 1.266, Purity = 99.5, Phase = "固",
            Batch = "B-0815", ActualMass = 12.19, ActualVolume = 9.6,
            SnapshotAt = new DateTimeOffset(2026, 8, 17, 9, 0, 0, TimeSpan.FromHours(8)),
            LibraryVersion = 3
        });
        t.Items.Add(new ChargeItem
        {
            Name = "乙醇", Role = ChargeRole.Solvent, Basis = ChargeBasis.Volumes,
            Amount = 8, Mw = 46.07, Density = 0.789, Phase = "液"
        });
        return t;
    }

    private static Recipe RecipeLinked(ChargeTable charge)
    {
        var r = new Recipe { Name = "模板" };
        r.Steps.Add(new Step
        {
            CommandId = CommandSpecs.Dose,
            Parameters = ParameterSet.Of(("pump", "加料泵 1"), ("liq", "乙醇"), ("vol", 97.7),
                                         ("rate", 1.0), ("sync", true),
                                         (ChargeLink.ChemKey, charge.Items[1].Id),
                                         (ChargeLink.LinkedKey, true))
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

    // ── 模板化清洗 ──────────────────────────────────────────────────

    [Fact]
    public void 清洗掉炉级数据留下工艺结构()
    {
        var t = ChargeTemplate.Strip(Table());
        var a = t.Items[0];

        // 跟这一炉走的清掉：实投列里坐着上一炉的数，操作人看不出它是老的
        Assert.Null(a.ActualMass);
        Assert.Null(a.ActualVolume);
        Assert.Equal("", a.Batch);

        // 工艺结构原样留：物性快照、相态、当量、行 Id、釜容
        Assert.Equal(122.12, a.Mw);
        Assert.Equal("固", a.Phase);
        Assert.Equal(3, a.LibraryVersion);
        Assert.Equal(Table().Items[0].Cas, a.Cas);
        Assert.Equal(250, t.VesselVolume);
    }

    [Fact]
    public void 清洗不动原表()
    {
        var src = Table();
        ChargeTemplate.Strip(src);
        Assert.Equal(12.19, src.Items[0].ActualMass);
        Assert.Equal("B-0815", src.Items[0].Batch);
    }

    // ── 配方捎着配料表的往返 ────────────────────────────────────────

    [Fact]
    public void 配方文档带着配料表过序列化不丢_行Id保住()
    {
        var charge = ChargeTemplate.Strip(Table());
        var r = RecipeLinked(charge);
        r.Charge = charge;

        var json = TecJson.Write(r.ToDoc());
        var back = TecJson.Read<RecipeDoc>(json).ToModel();

        Assert.NotNull(back.Charge);
        Assert.Equal(2, back.Charge!.Items.Count);
        // 行 Id 原样保留——步骤的 chemId 指着它，换了引用就断
        Assert.Equal(charge.Items[1].Id, back.Charge.Items[1].Id);
        Assert.Equal("液", back.Charge.Items[1].Phase);
        Assert.Equal(250, back.Charge.VesselVolume);
    }

    [Fact]
    public void 老配方文档没有配料表读回来是空不炸()
    {
        var doc = new Recipe { Name = "老配方" }.ToDoc();
        Assert.Null(doc.Charge);
        Assert.Null(TecJson.Read<RecipeDoc>(TecJson.Write(doc)).ToModel().Charge);
    }

    [Fact]
    public void 配方库落盘读回配料表还在()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tec-tpl-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var charge = ChargeTemplate.Strip(Table());
            var r = RecipeLinked(charge);
            r.Charge = charge;

            using (var db = new LibraryDb(Path.Combine(dir, "t.db")))
                db.SaveRecipes(new[] { r.ToDoc() });
            using var db2 = new LibraryDb(Path.Combine(dir, "t.db"));
            var back = Assert.Single(db2.LoadRecipes()).ToModel();

            Assert.NotNull(back.Charge);
            Assert.Equal(charge.Items[0].Id, back.Charge!.Items[0].Id);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 复制配方连配料表一起克隆_两份互不牵连()
    {
        var charge = ChargeTemplate.Strip(Table());
        var r = RecipeLinked(charge);
        r.Charge = charge;

        var copy = r.CopyAs(r.Name, "工艺员");
        Assert.NotNull(copy.Charge);
        Assert.NotSame(r.Charge, copy.Charge);
        Assert.NotSame(r.Charge!.Items[0], copy.Charge!.Items[0]);

        copy.Charge.Items[0].Amount = 99;
        Assert.Equal(12.212, r.Charge.Items[0].Amount);      // 改副本不伤库里那条
    }

    // ── 核心回归：应用出来引用不断 ──────────────────────────────────

    [Fact]
    public void 核心回归_从模板应用出来加料步骤的引用全部命中()
    {
        // 存库：配方 + 清洗过的配料表
        var charge = ChargeTemplate.Strip(Table());
        var stored = RecipeLinked(charge);
        stored.Charge = charge;

        // 应用：CopyAs 克隆（模拟配方库 → 通道），配料表落地成通道的表
        var applied = stored.CopyAs(stored.Name, null);
        var landed = applied.Charge!;
        applied.Charge = null;                                // 落地后配方对象上清掉

        var links = ChargeLink.Match(applied, Catalog(), Stoichiometry.Solve(landed));
        var e = Assert.Single(links);
        Assert.True(e.MatchedById);                          // 不是靠名字兜底，是引用真的连着
        Assert.True(e.Linked);
        Assert.False(e.RefGone);
    }

    [Fact]
    public void 只存步骤不带配料表应用出来就是断的()
    {
        // 这条钉死「为什么必须结伴」：老做法（只存步骤）在新实验里 chemId 全悬空
        var charge = ChargeTemplate.Strip(Table());
        var stored = RecipeLinked(charge);                   // 不带 Charge

        var applied = stored.CopyAs(stored.Name, null);
        var links = ChargeLink.Match(applied, Catalog(), Stoichiometry.Solve(new ChargeTable()));
        Assert.True(Assert.Single(links).RefGone);
    }
}
