using Tec.Core.Chemistry;
using Tec.Core.Compounds;
using Xunit;

namespace Tec.Core.Tests;

public class StoichiometryTests
{
    private static readonly Compound Benzoic = new()
    { Cas = "65-85-0", Name = "苯甲酸", Mw = 122.12, Density = 1.266, Purity = 99.5 };

    private static readonly Compound Toluene = new()
    { Cas = "108-88-3", Name = "甲苯", Mw = 92.14, Density = 0.8669 };

    /// <summary>65 % 的硝酸：纯度校正那条路专门拿它走。</summary>
    private static readonly Compound Nitric = new()
    { Cas = "7697-37-2", Name = "硝酸 65%", Mw = 63.01, Density = 1.39, Purity = 65 };

    private static readonly Compound[] Lib = { Benzoic, Toluene, Nitric };

    private static ChargeItem It(string cas, string name, ChargeRole role, ChargeBasis basis,
                                 double? amount, ChargeUnit unit = ChargeUnit.Gram) => new()
    { Cas = cas, Name = name, Role = role, Basis = basis, Amount = amount, Unit = unit };

    // ── 基准 ────────────────────────────────────────────────────────

    [Fact]
    public void 限制试剂按克给量算出物质的量且扣掉纯度()
    {
        var t = new ChargeTable();
        t.Items.Add(It("65-85-0", "苯甲酸", ChargeRole.Limiting, ChargeBasis.Quantity, 12.212));

        var r = Stoichiometry.Solve(t, Lib);
        var l = Assert.Single(r.Lines);

        Assert.Empty(r.Problems);
        // 12.212 g × 99.5 % ÷ 122.12 g/mol = 99.5 mmol。纯度不扣的话是 100 mmol——
        // 差的这 0.5 % 会一路乘到每一个当量上去
        Assert.Equal(99.5, l.Moles!.Value, 3);
        Assert.Equal(12.212, l.Mass!.Value, 6);
        Assert.Equal(12.212 / 1.266, l.Volume!.Value, 6);
        Assert.Equal(1, l.Equivalents);
        Assert.Empty(l.Missing);
    }

    [Fact]
    public void 限制试剂按毫摩尔给量则反算应称量()
    {
        var t = new ChargeTable();
        t.Items.Add(It("7697-37-2", "硝酸 65%", ChargeRole.Limiting, ChargeBasis.Quantity, 100,
                       ChargeUnit.Millimole));

        var l = Assert.Single(Stoichiometry.Solve(t, Lib).Lines);

        Assert.Equal(100, l.Moles);
        // 要 100 mmol 的 HNO₃，料只有 65 %：100 mmol × 63.01 ÷ 0.65 = 9.6938 g
        Assert.Equal(0.1 * 63.01 / 0.65, l.Mass!.Value, 6);
        Assert.Equal(l.Mass.Value / 1.39, l.Volume!.Value, 6);
    }

    [Fact]
    public void 限制试剂按毫升给量走密度换成质量()
    {
        var t = new ChargeTable();
        t.Items.Add(It("108-88-3", "甲苯", ChargeRole.Limiting, ChargeBasis.Quantity, 10,
                       ChargeUnit.Milliliter));

        var l = Assert.Single(Stoichiometry.Solve(t, Lib).Lines);

        Assert.Equal(10, l.Volume);
        Assert.Equal(8.669, l.Mass!.Value, 6);
        // 甲苯没填纯度：按 100 % 算，但得说一声
        Assert.Equal(8.669 / 92.14 * 1000, l.Moles!.Value, 4);
        Assert.Contains(l.Assumptions, a => a.Contains("100 %"));
    }

    // ── 当量 ────────────────────────────────────────────────────────

    [Fact]
    public void 按当量的行跟着限制试剂走并且做纯度校正()
    {
        var t = new ChargeTable();
        t.Items.Add(It("65-85-0", "苯甲酸", ChargeRole.Limiting, ChargeBasis.Quantity, 12.212));
        t.Items.Add(It("7697-37-2", "硝酸 65%", ChargeRole.Reagent, ChargeBasis.Equivalents, 1.2));

        var r = Stoichiometry.Solve(t, Lib);
        var acid = r.Lines[1];

        var nLim = r.Limiting!.Moles!.Value;                 // 99.5 mmol
        Assert.Equal(1.2 * nLim, acid.Moles!.Value, 4);

        // **这一条是整个引擎存在的理由**：要 119.4 mmol 的 HNO₃，手里的料只有 65 %，
        // 就得称 119.4 mmol × 63.01 ÷ 0.65 = 11.575 g。不校正会少投三分之一
        var want = 1.2 * nLim / 1000 * 63.01 / 0.65;
        Assert.Equal(want, acid.Mass!.Value, 5);
        Assert.Equal(want / 1.39, acid.Volume!.Value, 5);
        Assert.Equal(65, acid.PurityUsed);
    }

    [Fact]
    public void 行上填的纯度盖过库里的()
    {
        // 手里这瓶实测 68 %，库里那条写着 65 %。**行上的赢**——
        // 库里那个数是个通用参考值，这一瓶是这一瓶
        var t = new ChargeTable();
        t.Items.Add(It("65-85-0", "苯甲酸", ChargeRole.Limiting, ChargeBasis.Quantity, 12.212));
        var acid = It("7697-37-2", "硝酸", ChargeRole.Reagent, ChargeBasis.Equivalents, 1);
        acid.Purity = 68;
        t.Items.Add(acid);

        var line = Stoichiometry.Solve(t, Lib).Lines[1];
        Assert.Equal(68, line.PurityUsed);
        Assert.Equal(99.5 / 1000 * 63.01 / 0.68, line.Mass!.Value, 5);
    }

    [Fact]
    public void 不连库的行靠行上自己填的物性也算得出来()
    {
        var t = new ChargeTable();
        t.Items.Add(It("65-85-0", "苯甲酸", ChargeRole.Limiting, ChargeBasis.Quantity, 12.212));
        var own = It("", "A-7 中间体", ChargeRole.Reagent, ChargeBasis.Equivalents, 1);
        own.Mw = 227.13;
        own.Density = 1.34;
        t.Items.Add(own);

        var line = Stoichiometry.Solve(t, Lib).Lines[1];
        Assert.Null(line.Reference);
        Assert.Equal(99.5 / 1000 * 227.13, line.Mass!.Value, 5);
        Assert.Empty(line.Missing);
    }

    [Fact]
    public void 连了库但库里没这个键要说出来()
    {
        var t = new ChargeTable();
        t.Items.Add(It("999-99-9", "查无此物", ChargeRole.Limiting, ChargeBasis.Quantity, 10));

        var line = Assert.Single(Stoichiometry.Solve(t, Lib).Lines);
        Assert.Contains(line.Missing, m => m.Contains("999-99-9"));
        Assert.Null(line.Moles);            // 没有摩尔质量，不硬算
        Assert.Equal(10, line.Mass);        // 称多少克是人给的，这个还在
    }

    // ── 倍量 ────────────────────────────────────────────────────────

    [Fact]
    public void 溶剂按倍量给每克限制试剂几毫升()
    {
        var t = new ChargeTable();
        t.Items.Add(It("65-85-0", "苯甲酸", ChargeRole.Limiting, ChargeBasis.Quantity, 12.212));
        t.Items.Add(It("108-88-3", "甲苯", ChargeRole.Solvent, ChargeBasis.Volumes, 10));

        var sol = Stoichiometry.Solve(t, Lib).Lines[1];

        Assert.Equal(122.12, sol.Volume!.Value, 4);          // 10 mL/g × 12.212 g
        Assert.Equal(122.12 * 0.8669, sol.Mass!.Value, 4);
        // 溶剂也倒算一个当量出来——溶剂过量几十倍是常态，这个数一看就知道对不对
        Assert.True(sol.Equivalents > 10);
    }

    // ── 缺物性 ──────────────────────────────────────────────────────

    [Fact]
    public void 缺摩尔质量就不算物质的量而不是当成一()
    {
        var t = new ChargeTable();
        var lim = It("", "内部代号 X", ChargeRole.Limiting, ChargeBasis.Quantity, 10);
        t.Items.Add(lim);
        t.Items.Add(It("65-85-0", "苯甲酸", ChargeRole.Reagent, ChargeBasis.Equivalents, 1));

        var r = Stoichiometry.Solve(t, Lib);

        Assert.Null(r.Lines[0].Moles);
        Assert.Contains(r.Lines[0].Missing, m => m.Contains("摩尔质量"));
        // 基准立不起来，跟着它算的那一行也算不出来——但当量还是显示出来的
        Assert.Null(r.Lines[1].Moles);
        Assert.Equal(1, r.Lines[1].Equivalents);
        Assert.Contains(r.Lines[1].Missing, m => m.Contains("限制试剂"));
    }

    [Fact]
    public void 缺密度就不给体积而不是拿一顶上()
    {
        // 泵下发的是体积。密度当成 1 g/mL，人照着这个数去泵，加进去的量就是错的
        var t = new ChargeTable();
        var lim = It("", "某固体", ChargeRole.Limiting, ChargeBasis.Quantity, 10);
        lim.Mw = 100;
        t.Items.Add(lim);

        var line = Assert.Single(Stoichiometry.Solve(t, Lib).Lines);
        Assert.Equal(10, line.Mass);
        Assert.Null(line.Volume);
        Assert.Contains(line.Missing, m => m.Contains("密度"));
    }

    [Fact]
    public void 纯度是零这种数不拿来做除法()
    {
        var t = new ChargeTable();
        var lim = It("", "怪数据", ChargeRole.Limiting, ChargeBasis.Quantity, 10);
        lim.Mw = 100;
        lim.Density = 1;
        lim.Purity = 0;
        t.Items.Add(lim);

        var line = Assert.Single(Stoichiometry.Solve(t, Lib).Lines);
        Assert.Contains(line.Missing, m => m.Contains("纯度"));
        Assert.Equal(10 / 100.0 * 1000, line.Moles!.Value, 6);   // 按 100 % 兜住，不是 0 也不是无穷
    }

    // ── 表一级的毛病 ────────────────────────────────────────────────

    [Fact]
    public void 没有限制试剂就说没有()
    {
        var t = new ChargeTable();
        t.Items.Add(It("65-85-0", "苯甲酸", ChargeRole.Reagent, ChargeBasis.Equivalents, 1));

        var r = Stoichiometry.Solve(t, Lib);
        Assert.Contains(r.Problems, p => p.Contains("没有指定限制试剂"));
        Assert.Null(r.Limiting);
    }

    [Fact]
    public void 两个限制试剂也要说()
    {
        var t = new ChargeTable();
        t.Items.Add(It("65-85-0", "苯甲酸", ChargeRole.Limiting, ChargeBasis.Quantity, 10));
        t.Items.Add(It("108-88-3", "甲苯", ChargeRole.Limiting, ChargeBasis.Quantity, 10));

        var r = Stoichiometry.Solve(t, Lib);
        Assert.Contains(r.Problems, p => p.Contains("只能有一个"));
        Assert.Contains(r.Problems, p => p.Contains("苯甲酸") && p.Contains("甲苯"));
    }

    [Fact]
    public void 限制试剂拿当量当基准是循环定义要拦住()
    {
        var t = new ChargeTable();
        t.Items.Add(It("65-85-0", "苯甲酸", ChargeRole.Limiting, ChargeBasis.Equivalents, 1));

        var r = Stoichiometry.Solve(t, Lib);
        Assert.Contains(r.Problems, p => p.Contains("不能拿自己当参照"));
        Assert.Null(r.Lines[0].Moles);
    }

    [Fact]
    public void 空表不报错也不编数()
    {
        var r = Stoichiometry.Solve(new ChargeTable(), Lib);
        Assert.Empty(r.Lines);
        Assert.Empty(r.Problems);
        Assert.False(r.Any);
        Assert.Null(r.TotalMass);
    }

    // ── 合计与釜容 ──────────────────────────────────────────────────

    [Fact]
    public void 合计只算投料的不算目标产物()
    {
        var t = new ChargeTable();
        t.Items.Add(It("65-85-0", "苯甲酸", ChargeRole.Limiting, ChargeBasis.Quantity, 12.212));
        t.Items.Add(It("108-88-3", "甲苯", ChargeRole.Solvent, ChargeBasis.Volumes, 10));
        var prod = It("", "产物 P", ChargeRole.Product, ChargeBasis.Equivalents, 1);
        prod.Mw = 180;
        t.Items.Add(prod);

        var r = Stoichiometry.Solve(t, Lib);

        Assert.Equal(12.212 + 122.12 * 0.8669, r.TotalMass!.Value, 4);
        Assert.Equal(12.212 / 1.266 + 122.12, r.TotalVolume!.Value, 4);
        // 产物那一行不占体积也不占质量——它还没生成
        Assert.Null(r.Lines[2].Mass);
        Assert.Null(r.Lines[2].Volume);
    }

    [Fact]
    public void 总体积超过釜容要拦一下()
    {
        var t = new ChargeTable { VesselVolume = 100 };
        t.Items.Add(It("65-85-0", "苯甲酸", ChargeRole.Limiting, ChargeBasis.Quantity, 12.212));
        t.Items.Add(It("108-88-3", "甲苯", ChargeRole.Solvent, ChargeBasis.Volumes, 10));

        var r = Stoichiometry.Solve(t, Lib);
        Assert.True(r.OverVessel);
        Assert.Contains(r.Problems, p => p.Contains("超过釜容"));
    }

    // ── 理论产量与收率 ──────────────────────────────────────────────

    [Fact]
    public void 理论产量按限制试剂算收率按实际产量算()
    {
        var t = new ChargeTable();
        t.Items.Add(It("65-85-0", "苯甲酸", ChargeRole.Limiting, ChargeBasis.Quantity, 12.212));
        var prod = It("", "对硝基苯甲酸", ChargeRole.Product, ChargeBasis.Equivalents, 1);
        prod.Mw = 167.12;
        prod.ActualMass = 14.2;
        t.Items.Add(prod);

        var line = Stoichiometry.Solve(t, Lib).Lines[1];

        var theory = 99.5 / 1000 * 167.12;              // 16.628 g
        Assert.Equal(theory, line.TheoreticalMass!.Value, 4);
        Assert.Equal(14.2 / theory * 100, line.Yield!.Value, 3);
    }

    [Fact]
    public void 没填实际产量就没有收率而不是零()
    {
        var t = new ChargeTable();
        t.Items.Add(It("65-85-0", "苯甲酸", ChargeRole.Limiting, ChargeBasis.Quantity, 12.212));
        var prod = It("", "产物", ChargeRole.Product, ChargeBasis.Equivalents, 1);
        prod.Mw = 167.12;
        t.Items.Add(prod);

        var line = Stoichiometry.Solve(t, Lib).Lines[1];
        Assert.NotNull(line.TheoreticalMass);
        Assert.Null(line.Yield);
    }

    // ── 实投偏差 ────────────────────────────────────────────────────

    [Fact]
    public void 实投跟应称量差多少算得出来()
    {
        var t = new ChargeTable();
        var lim = It("65-85-0", "苯甲酸", ChargeRole.Limiting, ChargeBasis.Quantity, 12.212);
        t.Items.Add(lim);
        var acid = It("7697-37-2", "硝酸 65%", ChargeRole.Reagent, ChargeBasis.Equivalents, 1);
        t.Items.Add(acid);

        var line = Stoichiometry.Solve(t, Lib).Lines[1];
        Assert.Null(line.MassDeviation);            // 还没称，不是 0 %

        acid.ActualMass = line.Mass!.Value * 1.03;
        line = Stoichiometry.Solve(t, Lib).Lines[1];
        Assert.Equal(3, line.MassDeviation!.Value, 6);
    }

    [Fact]
    public void 中文名带全角括号和逗号一路原样带着()
    {
        // iControl 的已知毛病：化学品名里有「特殊字符」（明确点了中文和日文）
        // 会让报告里某些项填不上。这条从库一路验到算完
        const string name = "2,4-二硝基苯甲醚（内部代号 A-7）";
        var lib = new[] { new Compound { Cas = "119-27-7", Name = name, Mw = 198.13, Density = 1.34 } };

        var t = new ChargeTable();
        t.Items.Add(It("119-27-7", name, ChargeRole.Limiting, ChargeBasis.Quantity, 19.813));

        var r = Stoichiometry.Solve(t, lib);
        var l = Assert.Single(r.Lines);
        Assert.Equal(name, l.Item.Name);
        Assert.Equal(name, l.Reference!.Name);
        Assert.Equal(100, l.Moles!.Value, 3);
    }
}
