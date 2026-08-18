using Tec.Core.Compounds;
using Tec.Core.Persistence;
using Tec.Core.Recipes;
using Tec.Driver.Abi;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 全局库（配方 + 化合物）的落盘。盯三件事：
///   · 存进去读回来一个字段不少（配方是整份 JSON，参数值的类型最容易在这儿丢）
///   · 界面上删掉的，库里也真的没了——「对齐」不是「只增不减」
///   · 顺序保住：另存副本是插在原件后面的，读回来顺序变了就白插了
/// </summary>
public sealed class LibraryDbTests : IDisposable
{
    private readonly string _dir;

    public LibraryDbTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tecdb-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private LibraryDb Open(string name = "t.db") => new(Path.Combine(_dir, name));

    private static Recipe Sample(string name, params (string Cmd, double Target)[] steps)
    {
        var r = new Recipe { Name = name, Author = "管理员", Notes = "说明" };
        foreach (var s in steps)
            r.Steps.Add(new Step
            {
                CommandId = s.Cmd,
                Parameters = ParameterSet.Of(("target", s.Target), ("obj", "釜内 Tr"), ("hold", true)),
                Comment = "备注"
            });
        return r;
    }

    // ── 配方 ────────────────────────────────────────────────────────

    [Fact]
    public void 配方存进去读回来一个字段不少()
    {
        var r = Sample("降温结晶", ("tec.temp.control", 60));
        using (var db = Open()) db.SaveRecipes(new[] { r.ToDoc() });

        using var db2 = Open();
        var back = db2.LoadRecipes().Single().ToModel();

        Assert.Equal(r.Id, back.Id);
        Assert.Equal("降温结晶", back.Name);
        Assert.Equal("管理员", back.Author);
        Assert.Equal("说明", back.Notes);
        Assert.Equal(r.Steps[0].StepId, back.Steps[0].StepId);
        Assert.Equal("备注", back.Steps[0].Comment);
        // 参数值的 CLR 类型要还原，留成 JsonElement 的话 Num()/Str() 一个都认不出来
        Assert.Equal(60, back.Steps[0].Parameters.Num("target"));
        Assert.Equal("釜内 Tr", back.Steps[0].Parameters.Str("obj"));
        Assert.True(back.Steps[0].Parameters.Flag("hold"));
    }

    [Fact]
    public void 存第二遍是覆盖不是又插一条()
    {
        var r = Sample("配方一");
        using var db = Open();
        db.SaveRecipes(new[] { r.ToDoc() });

        r.Name = "改过的名字";
        db.SaveRecipes(new[] { r.ToDoc() });

        Assert.Equal(1, db.RecipeCount);
        Assert.Equal("改过的名字", db.LoadRecipes().Single().Name);
    }

    [Fact]
    public void 界面上删掉的库里也没了()
    {
        var a = Sample("甲");
        var b = Sample("乙");
        using var db = Open();
        db.SaveRecipes(new[] { a.ToDoc(), b.ToDoc() });

        db.SaveRecipes(new[] { a.ToDoc() });          // 界面上把「乙」删了

        Assert.Equal(1, db.RecipeCount);
        Assert.Equal("甲", db.LoadRecipes().Single().Name);
    }

    [Fact]
    public void 清空库就是一条都不剩()
    {
        using var db = Open();
        db.SaveRecipes(new[] { Sample("甲").ToDoc(), Sample("乙").ToDoc() });
        db.SaveRecipes(Array.Empty<RecipeDoc>());

        Assert.Equal(0, db.RecipeCount);
        Assert.Empty(db.LoadRecipes());
    }

    [Fact]
    public void 顺序按存进去的来()
    {
        // 另存副本是插在原件后面的：读回来顺序变了，副本就跑到列表末尾去了
        var list = new[] { Sample("丙"), Sample("甲"), Sample("乙") };
        using (var db = Open()) db.SaveRecipes(list.Select(r => r.ToDoc()).ToList());

        using var db2 = Open();
        Assert.Equal(new[] { "丙", "甲", "乙" }, db2.LoadRecipes().Select(d => d.Name));
    }

    // ── 化合物 ──────────────────────────────────────────────────────

    private static Compound Benzoic() => new()
    {
        Cas = "65-85-0",
        Name = "苯甲酸",
        Formula = "C7H6O2",
        Mw = 122.12,
        Mp = 122.4,
        Category = "有机酸",
        Solvent = "水 / 乙醇",
        Note = "常用结晶模型物",
        Solubility = new[] { 0.17, 0.006, 0.0006 },
        StructureKey = "BenzoicAcid"
    };

    [Fact]
    public void 化合物存进去读回来一个字段不少()
    {
        using (var db = Open()) db.SaveCompounds(new[] { Benzoic() });

        using var db2 = Open();
        var c = db2.LoadCompounds().Single();

        Assert.Equal("65-85-0", c.Cas);
        Assert.Equal("苯甲酸", c.Name);
        Assert.Equal("C7H6O2", c.Formula);
        Assert.Equal(122.12, c.Mw);
        Assert.Equal(122.4, c.Mp);
        Assert.Equal("有机酸", c.Category);
        Assert.Equal("水 / 乙醇", c.Solvent);
        Assert.Equal("常用结晶模型物", c.Note);
        Assert.Equal("BenzoicAcid", c.StructureKey);
        Assert.Equal(new[] { 0.17, 0.006, 0.0006 }, c.Solubility);
    }

    [Fact]
    public void 溶解度系数要按原值存不能丢精度()
    {
        var c = Benzoic();
        c.Solubility = new[] { 0.00008, 1.0 / 3.0, 179 };
        using var db = Open();
        db.SaveCompounds(new[] { c });

        Assert.Equal(c.Solubility, db.LoadCompounds().Single().Solubility);
    }

    [Fact]
    public void 离子化合物没有骨架式两个字段都留空()
    {
        var kcl = new Compound
        {
            Cas = "7447-40-7", Name = "氯化钾", Formula = "KCl",
            Mw = 74.55, Mp = 770, Category = "无机盐",
            Solubility = new[] { 28d, 0.32, 0.0 }, IonText = "K⁺ + Cl⁻"
        };
        using var db = Open();
        db.SaveCompounds(new[] { kcl });

        var back = db.LoadCompounds().Single();
        Assert.Null(back.StructureKey);
        Assert.Equal("K⁺ + Cl⁻", back.IonText);
    }

    [Fact]
    public void 改一条不影响别的也不改顺序()
    {
        var a = Benzoic();
        var b = new Compound { Cas = "56-40-6", Name = "甘氨酸", Category = "氨基酸" };
        using var db = Open();
        db.SaveCompounds(new[] { a, b });

        a.Note = "改过";
        db.SaveCompound(a);                          // 物性面板改一个字段走的是这条路

        var back = db.LoadCompounds();
        Assert.Equal(2, back.Count);
        Assert.Equal("苯甲酸", back[0].Name);          // 顺序不能因为改一条就翻过来
        Assert.Equal("改过", back[0].Note);
        Assert.Equal("甘氨酸", back[1].Name);
    }

    [Fact]
    public void 库里没有的那条按新增插在最后()
    {
        using var db = Open();
        db.SaveCompounds(new[] { Benzoic() });
        db.SaveCompound(new Compound { Cas = "50-00-0", Name = "甲醛" });

        Assert.Equal(new[] { "苯甲酸", "甲醛" }, db.LoadCompounds().Select(c => c.Name));
    }

    [Fact]
    public void 删一条只删那一条()
    {
        using var db = Open();
        db.SaveCompounds(new[] { Benzoic(), new Compound { Cas = "56-40-6", Name = "甘氨酸" } });

        db.DeleteCompound("65-85-0");

        Assert.Equal("甘氨酸", db.LoadCompounds().Single().Name);
    }

    // ── 库本身 ──────────────────────────────────────────────────────

    [Fact]
    public void 库文件不存在时自己建出来()
    {
        var path = Path.Combine(_dir, "sub", "deep", "new.db");
        using (var db = new LibraryDb(path)) db.SaveRecipes(new[] { Sample("甲").ToDoc() });

        Assert.True(File.Exists(path));
        using var db2 = new LibraryDb(path);
        Assert.Equal(1, db2.RecipeCount);
    }

    [Fact]
    public void 配方与化合物互不干扰()
    {
        using var db = Open();
        db.SaveRecipes(new[] { Sample("甲").ToDoc() });
        db.SaveCompounds(new[] { Benzoic() });

        db.SaveRecipes(Array.Empty<RecipeDoc>());    // 把配方库清空

        Assert.Equal(0, db.RecipeCount);
        Assert.Equal(1, db.CompoundCount);           // 化合物一条都不能少
    }

    [Fact]
    public void 元信息记得住()
    {
        using (var db = Open())
        {
            Assert.Null(db.Meta("compounds_seeded"));
            db.SetMeta("compounds_seeded", "1");
            db.SetMeta("compounds_seeded", "1");     // 写第二遍不该炸
        }
        using var db2 = Open();
        Assert.Equal("1", db2.Meta("compounds_seeded"));
        Assert.Equal(LibraryDb.CurrentSchema.ToString(), db2.Meta("schema"));
    }

    // ── 化合物库版本号（物性快照的凭据） ────────────────────────────

    [Fact]
    public void 库每写一次版本加一_整批对齐也只算一次()
    {
        using var db = Open();
        Assert.Equal(0, db.CompoundVersion);         // 从没写过就是 0

        db.SaveCompound(Benzoic());
        Assert.Equal(1, db.CompoundVersion);

        db.SaveCompounds(new[] { Benzoic() });       // 一批算一次，不按条数涨
        Assert.Equal(2, db.CompoundVersion);

        db.DeleteCompound("65-85-0");
        Assert.Equal(3, db.CompoundVersion);
    }

    [Fact]
    public void 没删着东西不涨版本()
    {
        using var db = Open();
        db.DeleteCompound("999-99-9");
        Assert.Equal(0, db.CompoundVersion);
    }

    [Fact]
    public void 版本号重开库还在()
    {
        using (var db = Open()) db.SaveCompound(Benzoic());
        using var db2 = Open();
        Assert.Equal(1, db2.CompoundVersion);
    }
}
