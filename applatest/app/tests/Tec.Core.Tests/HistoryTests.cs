using Tec.Core.Recipes;
using Tec.Driver.Abi;
using Xunit;

namespace Tec.Core.Tests;

public sealed class HistoryTests
{
    private static Recipe Of(params string[] commandIds)
    {
        var r = new Recipe { Name = "配方" };
        foreach (var id in commandIds)
            r.Steps.Add(new Step { CommandId = id, Parameters = new ParameterSet() });
        return r;
    }

    private static string Shape(Recipe r) => string.Join(",", r.Steps.Select(s => s.CommandId));

    [Fact]
    public void 撤销退回到改动之前那一份()
    {
        var h = new RecipeHistory();
        var r = Of("a", "b");

        h.Record(1, r);
        r.Steps.Add(new Step { CommandId = "c", Parameters = new ParameterSet() });

        var back = h.Undo(1, r);
        Assert.NotNull(back);
        Assert.Equal("a,b", Shape(back!));
    }

    [Fact]
    public void 重做把撤销掉的改动放回去()
    {
        var h = new RecipeHistory();
        var r = Of("a");

        h.Record(1, r);
        r.Steps.Add(new Step { CommandId = "b", Parameters = new ParameterSet() });

        var undone = h.Undo(1, r)!;          // 回到 a
        var again = h.Redo(1, undone)!;      // 再回到 a,b
        Assert.Equal("a,b", Shape(again));
    }

    [Fact]
    public void 通道之间互不影响()
    {
        var h = new RecipeHistory();
        var one = Of("a");
        var two = Of("x");

        h.Record(1, one);
        one.Steps.Add(new Step { CommandId = "b", Parameters = new ParameterSet() });

        // CH2 从来没记过，撤不动；CH1 的记录不该被 CH2 借走
        Assert.False(h.CanUndo(2));
        Assert.Null(h.Undo(2, two));
        Assert.True(h.CanUndo(1));
    }

    [Fact]
    public void 撤销之后再改会作废重做链()
    {
        var h = new RecipeHistory();
        var r = Of("a");

        h.Record(1, r);
        r.Steps.Add(new Step { CommandId = "b", Parameters = new ParameterSet() });
        var back = h.Undo(1, r)!;
        Assert.True(h.CanRedo(1));

        h.Record(1, back);                    // 走了新的分支
        Assert.False(h.CanRedo(1));
    }

    [Fact]
    public void 连着改同一个输入框只记一笔()
    {
        var h = new RecipeHistory();
        var r = Of("a");

        h.Record(1, r, "step1/target");
        h.Record(1, r, "step1/target");
        h.Record(1, r, "step1/target");

        h.Undo(1, r);
        Assert.False(h.CanUndo(1));           // 三次编辑只留下一笔
    }

    [Fact]
    public void 换一个输入框就另记一笔()
    {
        var h = new RecipeHistory();
        var r = Of("a");

        h.Record(1, r, "step1/target");
        h.Record(1, r, "step1/rate");

        h.Undo(1, r);
        Assert.True(h.CanUndo(1));
    }

    [Fact]
    public void 独立操作永远单独记不参与合并()
    {
        var h = new RecipeHistory();
        var r = Of("a");

        h.Record(1, r);
        h.Record(1, r);

        h.Undo(1, r);
        Assert.True(h.CanUndo(1));
    }

    [Fact]
    public void 超过深度上限丢最老的一笔()
    {
        var h = new RecipeHistory();
        var r = Of("a");

        for (var i = 0; i < RecipeHistory.Depth + 5; i++)
        {
            h.Record(1, r);
            r.Steps.Add(new Step { CommandId = "s" + i, Parameters = new ParameterSet() });
        }

        var count = 0;
        while (h.CanUndo(1)) { r = h.Undo(1, r)!; count++; }
        Assert.Equal(RecipeHistory.Depth, count);
    }

    [Fact]
    public void 存的是快照原件后续再改不会污染历史()
    {
        var h = new RecipeHistory();
        var r = Of("a");

        h.Record(1, r);
        r.Steps[0].Comment = "改了注释";
        r.Steps.Add(new Step { CommandId = "b", Parameters = new ParameterSet() });

        var back = h.Undo(1, r)!;
        Assert.Equal("a", Shape(back));
        Assert.Null(back.Steps[0].Comment);
    }

    [Fact]
    public void 通道被清掉之后历史整条丢掉()
    {
        var h = new RecipeHistory();
        var r = Of("a");
        h.Record(1, r);

        h.Forget(1);
        Assert.False(h.CanUndo(1));
    }
}
