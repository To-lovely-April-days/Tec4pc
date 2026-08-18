using Tec.Core.Chemistry;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 四通道对照矩阵的对齐（CH-3.6 的地基）。数据模型仍是一路一张表——
/// 矩阵只是按组分把它们拼起来的视图，对齐凭据先 CAS 后规整名。
/// </summary>
public class ChargeMatrixTests
{
    private static ChargeItem Row(string name, string cas = "", ChargeRole role = ChargeRole.Reagent,
                                  double? amount = null)
        => new() { Name = name, Cas = cas, Role = role, Amount = amount };

    private static ChargeTable T(params ChargeItem[] items)
    {
        var t = new ChargeTable();
        foreach (var i in items) t.Items.Add(i);
        return t;
    }

    [Fact]
    public void 同CAS对成一行_名字不同也认()
    {
        // CH2 把苯甲酸叫成了「苯甲酸（重结晶）」——CAS 相同就是同一个组分
        var rows = ChargeMatrix.Align(new[]
        {
            T(Row("苯甲酸", "65-85-0", ChargeRole.Limiting)),
            T(Row("苯甲酸（重结晶）", "65-85-0", ChargeRole.Limiting))
        });

        var r = Assert.Single(rows);
        Assert.NotNull(r.Cells[0]);
        Assert.NotNull(r.Cells[1]);
        Assert.Equal("苯甲酸", r.Name);                     // 显示名取第一个出现的
        Assert.False(r.HasGap);
    }

    [Fact]
    public void 没有CAS按规整过的名字对_全角空格不碍事()
    {
        var rows = ChargeMatrix.Align(new[]
        {
            T(Row("内部代号 A-7")),
            T(Row("　内部代号 A-7 "))
        });
        Assert.False(Assert.Single(rows).HasGap);
    }

    [Fact]
    public void 某通道缺这一行就是空格_缺本身是信息()
    {
        var rows = ChargeMatrix.Align(new[]
        {
            T(Row("苯甲酸", "65-85-0"), Row("催化剂 X")),
            T(Row("苯甲酸", "65-85-0"))
        });

        Assert.Equal(2, rows.Count);
        var cat = rows.Single(r => r.Name == "催化剂 X");
        Assert.True(cat.HasGap);
        Assert.Null(cat.Cells[1]);
    }

    [Fact]
    public void 行序按各通道出现的先后合并()
    {
        var rows = ChargeMatrix.Align(new[]
        {
            T(Row("A", "1-1-1"), Row("B", "2-2-2")),
            T(Row("B", "2-2-2"), Row("C", "3-3-3"))       // C 是 CH2 独有的，排最后
        });
        Assert.Equal(new[] { "A", "B", "C" }, rows.Select(r => r.Name));
    }

    [Fact]
    public void 同通道重复录了两行同名组分_各占一行不合并()
    {
        // 合并会把两行不同的量挤进一格，谁也读不懂
        var rows = ChargeMatrix.Align(new[]
        {
            T(Row("甲苯", "108-88-3", amount: 10), Row("甲苯", "108-88-3", amount: 5)),
            T(Row("甲苯", "108-88-3", amount: 8))
        });

        Assert.Equal(2, rows.Count);
        Assert.Equal(10, rows[0].Cells[0]!.Amount);
        Assert.Equal(8, rows[0].Cells[1]!.Amount);        // CH2 那行对齐到第一行
        Assert.Equal(5, rows[1].Cells[0]!.Amount);
        Assert.Null(rows[1].Cells[1]);                    // 第二行 CH2 没有
    }

    [Fact]
    public void 连名字都没有的空行不进矩阵()
    {
        var rows = ChargeMatrix.Align(new[] { T(Row(""), Row("有名字的")) });
        Assert.Single(rows);
    }

    [Fact]
    public void 格子就是那个通道自己的行_改格子等于改那张表()
    {
        var t1 = T(Row("甲苯", "108-88-3", amount: 10));
        var rows = ChargeMatrix.Align(new[] { t1 });
        rows[0].Cells[0]!.Amount = 99;
        Assert.Equal(99, t1.Items[0].Amount);             // 引用同一个对象，没有拷贝
    }
}
