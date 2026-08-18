using Tec.Core.Chemistry;
using Tec.Core.Compounds;
using Tec.Core.Persistence;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 物性快照（CH-D1）：连库那一刻把库值拷进行里、盖上版本与时刻，
/// 之后**库里怎么改都不影响这一行**。这一组测试的核心回归只有一条：
/// 改库改不动历史。其余都是围着它的边角。
/// </summary>
public class ChargeSnapshotTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 18, 9, 30, 0, TimeSpan.FromHours(8));

    private static Compound Benzoic() => new()
    {
        Cas = "65-85-0", Name = "苯甲酸", Mw = 122.12, Density = 1.266, Purity = 99.5,
        Batch = "B-0815", Supplier = "试剂公司"
    };

    private static ChargeItem Linked() => new()
    { Cas = "65-85-0", Name = "苯甲酸", Role = ChargeRole.Limiting,
      Basis = ChargeBasis.Quantity, Amount = 12.212, Unit = ChargeUnit.Gram };

    // ── 连库 ────────────────────────────────────────────────────────

    [Fact]
    public void 连库把空着的物性拷进行里并盖章()
    {
        var item = Linked();
        ChargeSnapshot.Link(item, Benzoic(), 7, T0);

        Assert.Equal(122.12, item.Mw);
        Assert.Equal(1.266, item.Density);
        Assert.Equal(99.5, item.Purity);
        Assert.Equal("B-0815", item.Batch);
        Assert.Equal("试剂公司", item.Supplier);
        Assert.Equal(T0, item.SnapshotAt);
        Assert.Equal(7, item.LibraryVersion);
    }

    [Fact]
    public void 行上写了的赢连库不盖掉()
    {
        // 手里这瓶实测 98.0 %，不能因为库里写着 99.5 % 就按 99.5 % 算
        var item = Linked();
        item.Purity = 98.0;
        item.Batch = "自家分装-3";
        ChargeSnapshot.Link(item, Benzoic(), 7, T0);

        Assert.Equal(98.0, item.Purity);
        Assert.Equal("自家分装-3", item.Batch);
        Assert.Equal(122.12, item.Mw);            // 空着的照拷
    }

    [Fact]
    public void 盖章之后不传库也算得出同样的数()
    {
        var t = new ChargeTable();
        t.Items.Add(Linked());
        ChargeSnapshot.Link(t.Items[0], Benzoic(), 7, T0);

        var withLib = Stoichiometry.Solve(t, new[] { Benzoic() });
        var without = Stoichiometry.Solve(t, null);

        Assert.Equal(withLib.Lines[0].Moles!.Value, without.Lines[0].Moles!.Value, 6);
        Assert.Equal(withLib.Lines[0].Volume!.Value, without.Lines[0].Volume!.Value, 6);
    }

    [Fact]
    public void 核心回归_改库改不动已盖章的行()
    {
        var t = new ChargeTable();
        t.Items.Add(Linked());
        ChargeSnapshot.Link(t.Items[0], Benzoic(), 7, T0);
        var before = Stoichiometry.Solve(t, null).Lines[0].Volume!.Value;

        // 有人把库里的密度改了——历史配料行的「应量取」必须纹丝不动
        var edited = Benzoic();
        edited.Density = 1.5;
        var after = Stoichiometry.Solve(t, new[] { edited }).Lines[0].Volume!.Value;

        Assert.Equal(before, after, 9);
    }

    [Fact]
    public void 盖章行不吃库里后来补的值()
    {
        // 快照时库里没密度（固体），章盖上了。之后有人往库里补了一条密度——
        // 这一行不许悄悄吃进去，不然快照等于没盖。要新值走「刷新库值」
        var noDensity = Benzoic();
        noDensity.Density = null;
        var t = new ChargeTable();
        t.Items.Add(Linked());
        ChargeSnapshot.Link(t.Items[0], noDensity, 7, T0);

        var line = Stoichiometry.Solve(t, new[] { Benzoic() }).Lines[0];

        Assert.Null(line.DensityUsed);
        Assert.DoesNotContain(line.Assumptions, a => a.Contains("化合物库当前值"));
    }

    [Fact]
    public void 未盖章的连库行现取库值并把话说明白()
    {
        var t = new ChargeTable();
        t.Items.Add(Linked());                    // 连库但没盖章：快照机制之前的行

        var line = Stoichiometry.Solve(t, new[] { Benzoic() }).Lines[0];

        Assert.Equal(122.12, line.MwUsed);        // 数照算——不能让老文件全变哑巴
        Assert.Contains(line.Assumptions, a => a.Contains("摩尔质量取自化合物库当前值"));
        Assert.Contains(line.Assumptions, a => a.Contains("密度取自化合物库当前值"));
        Assert.Contains(line.Assumptions, a => a.Contains("纯度取自化合物库当前值"));
    }

    [Fact]
    public void 不连库的行没有那三句话()
    {
        var t = new ChargeTable();
        t.Items.Add(new ChargeItem
        { Name = "某固体", Role = ChargeRole.Limiting, Basis = ChargeBasis.Quantity,
          Amount = 10, Unit = ChargeUnit.Gram, Mw = 100 });

        var line = Stoichiometry.Solve(t, new[] { Benzoic() }).Lines[0];
        Assert.DoesNotContain(line.Assumptions, a => a.Contains("化合物库当前值"));
    }

    // ── 刷新库值 ────────────────────────────────────────────────────

    [Fact]
    public void 刷新覆盖三项物性并逐条报告()
    {
        var t = new ChargeTable();
        t.Items.Add(Linked());
        ChargeSnapshot.Link(t.Items[0], Benzoic(), 7, T0);

        var edited = Benzoic();
        edited.Density = 1.27;
        var changes = ChargeSnapshot.Refresh(t, new[] { edited }, 8, T0.AddDays(1));

        Assert.Equal(1.27, t.Items[0].Density);
        Assert.Equal(8, t.Items[0].LibraryVersion);
        var one = Assert.Single(changes);
        Assert.Contains("密度", one);
        Assert.Contains("1.266", one);
        Assert.Contains("1.27", one);
    }

    [Fact]
    public void 没变化的刷新返回空清单但章照样更新()
    {
        var t = new ChargeTable();
        t.Items.Add(Linked());
        ChargeSnapshot.Link(t.Items[0], Benzoic(), 7, T0);

        var changes = ChargeSnapshot.Refresh(t, new[] { Benzoic() }, 9, T0.AddDays(2));

        Assert.Empty(changes);
        Assert.Equal(9, t.Items[0].LibraryVersion);
        Assert.Equal(T0.AddDays(2), t.Items[0].SnapshotAt);
    }

    [Fact]
    public void 刷新不碰批号供应商已有的值()
    {
        var t = new ChargeTable();
        var item = Linked();
        item.Batch = "自家分装-3";
        t.Items.Add(item);
        ChargeSnapshot.Link(item, Benzoic(), 7, T0);

        ChargeSnapshot.Refresh(t, new[] { Benzoic() }, 8, T0.AddDays(1));
        Assert.Equal("自家分装-3", item.Batch);   // 手里这一瓶的批号不被库里的顶掉
    }

    [Fact]
    public void 刷新不碰不连库的行()
    {
        var t = new ChargeTable();
        t.Items.Add(new ChargeItem { Name = "某固体", Mw = 100 });
        var changes = ChargeSnapshot.Refresh(t, new[] { Benzoic() }, 8, T0);

        Assert.Empty(changes);
        Assert.Null(t.Items[0].SnapshotAt);
        Assert.Equal(100, t.Items[0].Mw);
    }

    // ── 老文件补章 ──────────────────────────────────────────────────

    [Fact]
    public void 补章只处理连库且未盖章的行()
    {
        var t = new ChargeTable();
        t.Items.Add(Linked());                             // 该补
        var done = Linked();
        ChargeSnapshot.Link(done, Benzoic(), 3, T0);       // 已盖过
        t.Items.Add(done);
        t.Items.Add(new ChargeItem { Name = "不连库" });    // 不该动

        var stamped = ChargeSnapshot.Migrate(t, new[] { Benzoic() }, 9, T0.AddDays(1));

        Assert.Equal(new[] { "苯甲酸" }, stamped);
        Assert.Equal(9, t.Items[0].LibraryVersion);
        Assert.Equal(3, done.LibraryVersion);              // 老章不许被盖新章
        Assert.Null(t.Items[2].SnapshotAt);
    }

    [Fact]
    public void 库里找不到的行不补章()
    {
        // 没东西可拷，盖章就是谎报「已快照」
        var t = new ChargeTable();
        t.Items.Add(new ChargeItem { Cas = "999-99-9", Name = "查无此物" });
        Assert.Empty(ChargeSnapshot.Migrate(t, new[] { Benzoic() }, 9, T0));
        Assert.Null(t.Items[0].SnapshotAt);
    }

    // ── 自足判定与表脚描述 ──────────────────────────────────────────

    [Fact]
    public void 全部连库行盖了章才算自足()
    {
        var t = new ChargeTable();
        t.Items.Add(Linked());
        t.Items.Add(new ChargeItem { Name = "不连库" });    // 不连库不碍事

        Assert.False(ChargeSnapshot.SelfContained(t));
        ChargeSnapshot.Link(t.Items[0], Benzoic(), 7, T0);
        Assert.True(ChargeSnapshot.SelfContained(t));
    }

    [Fact]
    public void 表脚一句话_版本一致报一个数_不一致报范围()
    {
        var t = new ChargeTable();
        t.Items.Add(Linked());
        t.Items.Add(Linked());
        ChargeSnapshot.Link(t.Items[0], Benzoic(), 7, T0);
        ChargeSnapshot.Link(t.Items[1], Benzoic(), 7, T0.AddMinutes(1));

        Assert.Contains("第 7 版", ChargeSnapshot.Describe(t));

        t.Items[1].LibraryVersion = 9;             // 两行分别按不同版本连的库
        Assert.Contains("7–9", ChargeSnapshot.Describe(t));
    }

    [Fact]
    public void 没盖过章的表没有表脚那句话()
    {
        var t = new ChargeTable();
        t.Items.Add(Linked());
        Assert.Null(ChargeSnapshot.Describe(t));
    }

    // ── 落盘 ────────────────────────────────────────────────────────

    [Fact]
    public void 章跟着行过文档往返不丢()
    {
        var t = new ChargeTable();
        t.Items.Add(Linked());
        ChargeSnapshot.Link(t.Items[0], Benzoic(), 7, T0);

        var back = t.ToDoc().ToModel();
        Assert.Equal(T0, back.Items[0].SnapshotAt);
        Assert.Equal(7, back.Items[0].LibraryVersion);
        Assert.Equal(1.266, back.Items[0].Density);
    }

    [Fact]
    public void 章跟着克隆走()
    {
        var item = Linked();
        ChargeSnapshot.Link(item, Benzoic(), 7, T0);
        var copy = item.Clone();
        Assert.Equal(T0, copy.SnapshotAt);
        Assert.Equal(7, copy.LibraryVersion);
    }
}
