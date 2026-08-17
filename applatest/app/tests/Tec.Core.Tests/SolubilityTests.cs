using Tec.Core.Chemistry;
using Tec.Core.Compounds;
using Xunit;

namespace Tec.Core.Tests;

public class SolubilityTests
{
    /// <summary>苯甲酸在水里：0.17 + 0.006·T + 0.0006·T²（g/100 mL），与库里自带的一致。</summary>
    private static readonly double[] Benzoic = { 0.17, 0.006, 0.0006 };

    [Fact]
    public void 饱和温度解出来代回曲线就是那个浓度()
    {
        // 二分解出来的温度，代回去必须等于给的浓度——这是这套算法唯一要保证的事
        foreach (var c in new[] { 0.3, 1.0, 2.5, 4.9 })
        {
            var r = Solubility.Saturate(Benzoic, c);
            Assert.True(r.Ok, r.Problem);
            Assert.Equal(c, Solubility.At(Benzoic, r.Temperature!.Value), 6);
            Assert.InRange(r.Temperature.Value, Solubility.MinT, Solubility.MaxT);
        }
    }

    [Fact]
    public void 浓度低于零度的溶解度就是全溶没有饱和点()
    {
        // 0 ℃ 时溶解度 0.17。给 0.1 的话整个区间都是全溶——
        // 这时候硬给一个温度，等于说「降到某温度会析出」，那是假的
        var r = Solubility.Saturate(Benzoic, 0.1);
        Assert.False(r.Ok);
        Assert.Contains("全溶", r.Problem!);
        Assert.Contains("0.17", r.Problem!);
    }

    [Fact]
    public void 浓度高过九十度的溶解度就说溶不完()
    {
        var r = Solubility.Saturate(Benzoic, 20);
        Assert.False(r.Ok);
        Assert.Contains("溶不完", r.Problem!);
        Assert.Contains("拟合区间之外", r.Problem!);
    }

    [Fact]
    public void 没有曲线的化合物不硬算()
    {
        Assert.False(Solubility.Saturate(null, 1).Ok);
        Assert.False(Solubility.Saturate(new[] { 0.5 }, 1).Ok);
        Assert.Contains("没有溶解度曲线", Solubility.Saturate(Array.Empty<double>(), 1).Problem!);
    }

    [Fact]
    public void 不单调的曲线不给唯一解()
    {
        // 溶解度随温度先升后降的体系是有的（比如某些硫酸盐）。
        // 那时同一个浓度对上两个温度，随便挑一个就是猜
        var weird = new[] { 10.0, 2.0, -0.05 };       // 20 ℃ 之后开始往下走
        var r = Solubility.Saturate(weird, 20);
        Assert.False(r.Ok);
        Assert.Contains("不是单调升", r.Problem!);
    }

    [Fact]
    public void 浓度不是正数就不算()
    {
        Assert.False(Solubility.Saturate(Benzoic, 0).Ok);
        Assert.False(Solubility.Saturate(Benzoic, -1).Ok);
    }
}

public class ChargeSaturationTests
{
    private static readonly Compound Benzoic = new()
    {
        Cas = "65-85-0", Name = "苯甲酸", Mw = 122.12, Density = 1.266,
        Solubility = new[] { 0.17, 0.006, 0.0006 }
    };

    private static readonly Compound Water = new()
    { Cas = "7732-18-5", Name = "水", Mw = 18.015, Density = 0.997 };

    private static readonly Compound Toluene = new()
    { Cas = "108-88-3", Name = "甲苯", Mw = 92.14, Density = 0.8669 };

    private static readonly Compound[] Lib = { Benzoic, Water, Toluene };

    private static ChargeTable Table(string solventCas, string solventName, double volumes = 10)
    {
        var t = new ChargeTable();
        t.Items.Add(new ChargeItem
        {
            Cas = "65-85-0", Name = "苯甲酸", Role = ChargeRole.Limiting,
            Basis = ChargeBasis.Quantity, Amount = 5, Unit = ChargeUnit.Gram
        });
        t.Items.Add(new ChargeItem
        {
            Cas = solventCas, Name = solventName, Role = ChargeRole.Solvent,
            Basis = ChargeBasis.Volumes, Amount = volumes
        });
        return t;
    }

    private static SaturationCase One(ChargeTable t)
        => Assert.Single(ChargeSaturation.Of(Stoichiometry.Solve(t, Lib)));

    [Fact]
    public void 拿配料表里的质量和体积算出浓度与饱和温度()
    {
        // 5 g 苯甲酸 ÷ 50 mL 水 = 10 g/100 mL……那超出区间了，用 10 倍量 = 50 mL
        var one = One(Table("7732-18-5", "水"));

        Assert.Equal(5, one.SoluteMass);
        Assert.Equal(50, one.SolventVolume);
        Assert.Equal(10, one.Concentration!.Value, 6);      // 5 g / 50 mL → 10 g/100 mL
        // 10 g/100 mL 超过 90 ℃ 的溶解度（0.17+0.54+4.86 = 5.57），照实说溶不完
        Assert.False(one.Ok);
        Assert.Contains("溶不完", one.Result.Problem!);
    }

    [Fact]
    public void 稀一点就算得出饱和温度且说清是拿什么算的()
    {
        var t = Table("7732-18-5", "水", volumes: 40);      // 200 mL 水 → 2.5 g/100 mL
        var one = One(t);

        Assert.True(one.Ok, one.Result.Problem);
        Assert.Equal(2.5, one.Concentration!.Value, 6);
        Assert.Equal(2.5, Solubility.At(Benzoic.Solubility, one.Result.Temperature!.Value), 6);

        // 复核的人要能照着这句话把浓度重算一遍
        Assert.Contains("苯甲酸 5 g", one.Basis);
        Assert.Contains("水 200 mL", one.Basis);
        Assert.Contains("按应称量算", one.Basis);
    }

    [Fact]
    public void 溶剂不是水就拒绝算()
    {
        // 这是整件事最要紧的一条：库里那条曲线是水里的，拿去算甲苯体系
        // 会得到一个看着非常合理的错温度，而没人查得出来
        var one = One(Table("108-88-3", "甲苯", volumes: 40));

        Assert.False(one.Ok);
        Assert.Contains("水里的", one.Result.Problem!);
        Assert.Contains("甲苯", one.Result.Problem!);
        Assert.Null(one.Concentration);
    }

    [Theory]
    [InlineData("水")]
    [InlineData("纯化水")]
    [InlineData("去离子水")]
    [InlineData("蒸馏水")]
    [InlineData("Water")]
    [InlineData("H2O")]
    public void 这些叫法都认作水(string name)
        => Assert.True(ChargeSaturation.IsWater(new ChargeItem { Name = name }));

    [Theory]
    [InlineData("水杨酸")]
    [InlineData("水合肼")]
    [InlineData("重水")]
    [InlineData("水 / 乙醇")]
    public void 名字里带水字的不都是水(string name)
    {
        // 包含匹配会把「水杨酸」认成水，然后拿水的曲线去算——这条必须是精确匹配
        Assert.False(ChargeSaturation.IsWater(new ChargeItem { Name = name }));
    }

    [Fact]
    public void 按CAS也认得出水()
    {
        Assert.True(ChargeSaturation.IsWater(new ChargeItem { Cas = "7732-18-5", Name = "工艺用水" }));
    }

    [Fact]
    public void 没有溶剂那一行就说算不出浓度()
    {
        var t = new ChargeTable();
        t.Items.Add(new ChargeItem
        {
            Cas = "65-85-0", Name = "苯甲酸", Role = ChargeRole.Limiting,
            Basis = ChargeBasis.Quantity, Amount = 5, Unit = ChargeUnit.Gram
        });

        var one = One(t);
        Assert.False(one.Ok);
        Assert.Contains("没有标成「溶剂」", one.Result.Problem!);
    }

    [Fact]
    public void 溶剂算不出体积就说没有体积就没有浓度()
    {
        var t = Table("7732-18-5", "水", volumes: 40);
        t.Items[1].Cas = "";                 // 不连库
        t.Items[1].Density = null;           // 也没密度 → 倍量算得出体积……

        // 倍量是直接给体积的，所以这条得换个路子：把溶剂改成按当量给
        t.Items[1].Basis = ChargeBasis.Equivalents;
        t.Items[1].Amount = 5;

        var one = One(t);
        Assert.False(one.Ok);
        Assert.Contains("没有体积就没有浓度", one.Result.Problem!);
    }

    [Fact]
    public void 没有溶解度曲线的组分整块不出现()
    {
        // 一条都算不了就返回空表，界面上那一块整个不摆出来，不放一个「—」在那儿
        var t = new ChargeTable();
        t.Items.Add(new ChargeItem
        {
            Cas = "108-88-3", Name = "甲苯", Role = ChargeRole.Limiting,
            Basis = ChargeBasis.Quantity, Amount = 5, Unit = ChargeUnit.Gram
        });
        t.Items.Add(new ChargeItem
        {
            Cas = "7732-18-5", Name = "水", Role = ChargeRole.Solvent,
            Basis = ChargeBasis.Volumes, Amount = 10
        });

        Assert.Empty(ChargeSaturation.Of(Stoichiometry.Solve(t, Lib)));
    }

    [Fact]
    public void 溶剂那一行自己不参与当溶质()
    {
        // 水在库里也可以有曲线（没有），但溶剂行不该被当成要算饱和温度的溶质
        var t = Table("7732-18-5", "水", volumes: 40);
        var cases = ChargeSaturation.Of(Stoichiometry.Solve(t, Lib));
        Assert.All(cases, c => Assert.NotEqual(ChargeRole.Solvent, c.Solute.Item.Role));
    }

    // ── 每 1 g 产物需要多少限制试剂 ──────────────────────────────────

    [Fact]
    public void 每一克产物需要多少限制试剂算得出来()
    {
        var t = new ChargeTable();
        t.Items.Add(new ChargeItem
        {
            Cas = "65-85-0", Name = "苯甲酸", Role = ChargeRole.Limiting,
            Basis = ChargeBasis.Quantity, Amount = 12.212, Unit = ChargeUnit.Gram
        });
        var prod = new ChargeItem
        {
            Name = "产物", Role = ChargeRole.Product,
            Basis = ChargeBasis.Equivalents, Amount = 1, Mw = 167.12
        };
        t.Items.Add(prod);

        var r = Stoichiometry.Solve(t, Lib);
        // 12.212 g 限制试剂 → 理论产量 100 mmol × 167.12 = 16.712 g
        Assert.Equal(12.212 / 16.712, r.LimitingPerProductGram!.Value, 6);
    }

    [Fact]
    public void 没有产物行就没有这个比值不是零()
    {
        var r = Stoichiometry.Solve(Table("7732-18-5", "水"), Lib);
        Assert.Null(r.LimitingPerProductGram);
    }
}
