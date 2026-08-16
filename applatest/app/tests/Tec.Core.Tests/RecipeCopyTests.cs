using Tec.Core.Recipes;
using Tec.Driver.Abi;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 两种复制的分工：
///   Snapshot()  保住 Id —— 撤销栈与运行记录靠 Id 认回原件
///   CopyAs()    换新 Id —— 存进配方库、应用到通道、另存副本，那都是**新的一条**
///
/// 分错了会出两类事故：撤销拉不回来（Id 变了），或者两条配方同号，
/// 记录与导出把它们混成一条。
/// </summary>
public sealed class RecipeCopyTests
{
    private static Recipe Sample()
    {
        var r = new Recipe
        {
            Name = "降温结晶",
            Author = "工程师",
            Notes = "说明",
            ModifiedAt = new DateTimeOffset(new DateTime(2026, 1, 1, 9, 0, 0), TimeSpan.Zero)
        };
        r.Steps.Add(new Step
        {
            CommandId = "tec.temp.control",
            Parameters = ParameterSet.Of(("target", 60d)),
            Comment = "先升温"
        });
        return r;
    }

    [Fact]
    public void 快照保住配方号与时间戳()
    {
        var r = Sample();
        var s = r.Snapshot();

        Assert.Equal(r.Id, s.Id);
        Assert.Equal(r.ModifiedAt, s.ModifiedAt);
        Assert.Equal(r.Steps[0].StepId, s.Steps[0].StepId);   // 步骤号也得保住
    }

    [Fact]
    public void 存进库的副本换新号并重打时间戳()
    {
        var r = Sample();
        var c = r.CopyAs("我的配方", "管理员");

        Assert.NotEqual(r.Id, c.Id);
        Assert.Equal("我的配方", c.Name);
        Assert.Equal("管理员", c.Author);
        Assert.True(c.ModifiedAt > r.ModifiedAt);            // 库里「最近更新」要显示存进来的时刻
    }

    [Fact]
    public void 副本改参数不会动到原件()
    {
        var r = Sample();
        var c = r.CopyAs("副本");

        c.Steps[0].Parameters["target"] = 5d;
        c.Steps[0].Comment = "改了";
        c.Steps.Add(new Step { CommandId = "tec.stir.set", Parameters = new ParameterSet() });

        Assert.Equal(60, r.Steps[0].Parameters.Num("target"));
        Assert.Equal("先升温", r.Steps[0].Comment);
        Assert.Single(r.Steps);
    }

    [Fact]
    public void 同一条应用到两个通道得到两个不同的配方号()
    {
        var lib = Sample();
        var a = lib.CopyAs(lib.Name, lib.Author);
        var b = lib.CopyAs(lib.Name, lib.Author);

        Assert.NotEqual(a.Id, b.Id);
        Assert.NotEqual(a.Id, lib.Id);
        Assert.Equal(a.Name, b.Name);        // 名字一样是对的，同号才是错的
    }

    [Fact]
    public void 不传作者就沿用原件的()
    {
        var r = Sample();
        Assert.Equal("工程师", r.CopyAs("副本").Author);
    }

    [Fact]
    public void 副本带着分段表且互不影响()
    {
        var r = Sample();
        r.Steps.Add(new Step
        {
            CommandId = "tec.temp.gradient",
            Parameters = new ParameterSet(),
            Rows = new List<ParameterSet> { ParameterSet.Of(("t", 60d), ("r", 1d), ("h", 10d)) }
        });

        var c = r.CopyAs("副本");
        c.Steps[1].Rows![0]["t"] = 5d;

        Assert.Equal(60, r.Steps[1].Rows![0].Num("t"));
    }
}
