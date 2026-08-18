using Tec.Core.Chemistry;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 计算补齐那一批：摩尔浓度（CH-C7）、限制试剂建议（CH-3.2）、
/// 实投折算成实际当量与过量（CH-3.5）、收率按限制试剂**实投**为分母。
/// 全部用行上自填物性（纯度 100 % 起步），数字能心算——测试算不出来的数没资格断言。
/// </summary>
public class ChargeActualsTests
{
    /// <summary>限 A 10 g / M100 → 100 mmol；B 1.2 eq / M50；溶剂 S 5 mL/g；产物 P M200。</summary>
    private static ChargeTable Table()
    {
        var t = new ChargeTable();
        t.Items.Add(new ChargeItem
        { Name = "A", Role = ChargeRole.Limiting, Basis = ChargeBasis.Quantity,
          Amount = 10, Unit = ChargeUnit.Gram, Mw = 100, Purity = 100 });
        t.Items.Add(new ChargeItem
        { Name = "B", Role = ChargeRole.Reagent, Basis = ChargeBasis.Equivalents,
          Amount = 1.2, Mw = 50, Purity = 100 });
        t.Items.Add(new ChargeItem
        { Name = "S", Role = ChargeRole.Solvent, Basis = ChargeBasis.Volumes,
          Amount = 5, Density = 0.8 });
        t.Items.Add(new ChargeItem
        { Name = "P", Role = ChargeRole.Product, Basis = ChargeBasis.Equivalents,
          Amount = 1, Mw = 200 });
        return t;
    }

    // ── 摩尔浓度（CH-C7）────────────────────────────────────────────

    [Fact]
    public void 摩尔浓度是限制试剂比溶剂总体积()
    {
        var r = Stoichiometry.Solve(Table());
        // 100 mmol ÷ 50 mL = 2 mol/L
        Assert.Equal(2.0, r.Concentration!.Value, 6);
    }

    [Fact]
    public void 溶剂算不出体积就没有浓度()
    {
        // 按克称的溶剂没密度 → 体积缺——按打了折的体积算浓度比没有更糟
        var t = Table();
        t.Items.Add(new ChargeItem
        { Name = "S2", Role = ChargeRole.Solvent, Basis = ChargeBasis.Quantity,
          Amount = 20, Unit = ChargeUnit.Gram, Mw = 80 });
        Assert.Null(Stoichiometry.Solve(t).Concentration);
    }

    [Fact]
    public void 没有溶剂行就没有浓度()
    {
        var t = Table();
        t.Items.RemoveAll(i => i.Role == ChargeRole.Solvent);
        Assert.Null(Stoichiometry.Solve(t).Concentration);
    }

    // ── 限制试剂建议（CH-3.2：建议不代填）───────────────────────────

    [Fact]
    public void 没有限制试剂时按摩尔数最小的试剂给建议()
    {
        var t = new ChargeTable();
        t.Items.Add(new ChargeItem
        { Name = "多的", Role = ChargeRole.Reagent, Basis = ChargeBasis.Quantity,
          Amount = 10, Unit = ChargeUnit.Gram, Mw = 100 });          // 100 mmol
        t.Items.Add(new ChargeItem
        { Name = "少的", Role = ChargeRole.Reagent, Basis = ChargeBasis.Quantity,
          Amount = 5, Unit = ChargeUnit.Gram, Mw = 100 });           // 50 mmol

        var r = Stoichiometry.Solve(t);
        var hint = Assert.Single(r.Problems, p => p.Contains("摩尔数最小"));
        Assert.Contains("少的", hint);
        // 建议归建议，角色一个都不许动——替人改角色等于替人定工艺
        Assert.All(t.Items, i => Assert.NotEqual(ChargeRole.Limiting, i.Role));
    }

    [Fact]
    public void 试剂当量小于一时点破真正的限制试剂是它()
    {
        var t = Table();
        t.Items[1].Amount = 0.9;                                     // B 欠量
        var r = Stoichiometry.Solve(t);
        var hint = Assert.Single(r.Problems, p => p.Contains("真正的限制试剂其实是它"));
        Assert.Contains("B", hint);
    }

    [Fact]
    public void 催化剂当量再小也不触发那条提示()
    {
        var t = Table();
        t.Items.Add(new ChargeItem
        { Name = "催化剂", Role = ChargeRole.Catalyst, Basis = ChargeBasis.Equivalents,
          Amount = 0.05, Mw = 300 });
        Assert.DoesNotContain(Stoichiometry.Solve(t).Problems,
                              p => p.Contains("真正的限制试剂其实是它"));
    }

    // ── 实投折算（CH-3.5）──────────────────────────────────────────

    [Fact]
    public void 实投按纯度折算成实际摩尔()
    {
        var t = Table();
        t.Items[1].Purity = 50;                                      // B 只有一半是纯品
        t.Items[1].ActualMass = 12;
        var b = Stoichiometry.Solve(t).Lines[1];
        Assert.Equal(120, b.ActualMoles!.Value, 6);                  // 12 g × 50 % ÷ 50 × 1000
    }

    [Fact]
    public void 实际当量以限制试剂实投为基准()
    {
        var t = Table();
        t.Items[0].ActualMass = 9.5;                                 // 限 A 实投 95 mmol
        t.Items[1].ActualMass = 6.3;                                 // B 实投 126 mmol
        var b = Stoichiometry.Solve(t).Lines[1];

        Assert.Equal(126.0 / 95, b.ActualEquivalents!.Value, 6);
        // 相对计划当量 1.2 的过量
        Assert.Equal((126.0 / 95 / 1.2 - 1) * 100, b.ExcessPercent!.Value, 6);
    }

    [Fact]
    public void 限制试剂没回填时实际当量按计划量算()
    {
        var t = Table();
        t.Items[1].ActualMass = 6.3;                                 // 只有 B 回填了
        var b = Stoichiometry.Solve(t).Lines[1];
        Assert.Equal(1.26, b.ActualEquivalents!.Value, 6);           // 126 ÷ 计划 100
        Assert.Equal(5.0, b.ExcessPercent!.Value, 6);                // 1.26 / 1.2 − 1
    }

    // ── 收率按实投（那个直击要害的口径）─────────────────────────────

    [Fact]
    public void 收率分母按限制试剂实投折算()
    {
        var t = Table();
        t.Items[0].ActualMass = 9.5;                                 // 实投只有 95 mmol
        t.Items[3].ActualMass = 19;                                  // 产物拿到 19 g
        var r = Stoichiometry.Solve(t);
        var p = r.Lines[3];

        // 分母 = 95 mmol × 200 = 19 g → 这一炉其实是满收率
        Assert.Equal(100.0, p.Yield!.Value, 6);
        // 「应称量」列的理论产量仍按计划投料——它是排配料时的目标
        Assert.Equal(20.0, p.TheoreticalMass!.Value, 6);
        Assert.Contains("实投", r.YieldBasis);
        Assert.DoesNotContain(p.Assumptions, a => a.Contains("收率按计划"));
    }

    [Fact]
    public void 限制试剂没实投时收率按计划算并把话说出口()
    {
        var t = Table();
        t.Items[3].ActualMass = 19;
        var r = Stoichiometry.Solve(t);
        var p = r.Lines[3];

        Assert.Equal(95.0, p.Yield!.Value, 6);                       // 19 / 20
        Assert.Contains(p.Assumptions, a => a.Contains("收率按计划投料量计"));
        Assert.Contains("计划投料", r.YieldBasis);
    }

    [Fact]
    public void 产物行不算实投折算()
    {
        // 产物的「实投」是实际产量，反算当量没有意义
        var t = Table();
        t.Items[3].ActualMass = 19;
        var p = Stoichiometry.Solve(t).Lines[3];
        Assert.Null(p.ActualMoles);
        Assert.Null(p.ActualEquivalents);
    }
}
