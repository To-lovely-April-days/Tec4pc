using Tec.Core.Catalog;
using Tec.Core.Scheduling;
using Tec.Driver.Abi;
using Tec.Drivers.Simulator;
using Xunit;

namespace Tec.Core.Tests;

public class ScheduleTests
{
    private static CommandCatalog Catalog()
    {
        var c = new CommandCatalog();
        c.Register(new Rd105ReactorDriver().Commands);
        c.Register(new DosingPumpDriver().Commands);
        return c;
    }

    [Fact]
    public void 升温耗时由温差与速率推算()
    {
        var recipe = Harness.RecipeOf("升温",
            Harness.Mk(CommandSpecs.Control, ("target", 60d), ("rate", 2d)));
        var s = Schedule.Build(recipe, Catalog(), new EstimationContext { Temperature = 20 });

        // (60 − 20) / 2 = 20 min
        Assert.Equal(TimeSpan.FromMinutes(20), s.Entries[0].Duration);
        Assert.Equal(TimeSpan.FromMinutes(20), s.Total);
    }

    [Fact]
    public void 估算上下文串行推进而不是各算各的()
    {
        var recipe = Harness.RecipeOf("两段升温",
            Harness.Mk(CommandSpecs.Control, ("target", 40d), ("rate", 2d)),
            Harness.Mk(CommandSpecs.Control, ("target", 60d), ("rate", 2d)));
        var s = Schedule.Build(recipe, Catalog(), new EstimationContext { Temperature = 20 });

        Assert.Equal(TimeSpan.FromMinutes(10), s.Entries[0].Duration);  // 20 → 40
        Assert.Equal(TimeSpan.FromMinutes(10), s.Entries[1].Duration);  // 40 → 60，不是 20 → 60
        Assert.Equal(TimeSpan.FromMinutes(10), s.Entries[1].Start);
    }

    [Fact]
    public void 循环开始行的跨度覆盖全部轮次()
    {
        var recipe = Harness.RecipeOf("循环",
            Harness.Mk(BuiltinCommands.LoopBegin, ("by", "按次数"), ("n", 3d)),
            Harness.Mk(BuiltinCommands.Wait, ("dur", 10d)),
            Harness.Mk(BuiltinCommands.LoopEnd),
            Harness.Mk(BuiltinCommands.Wait, ("dur", 1d)));
        var s = Schedule.Build(recipe, Catalog());

        Assert.Equal(3, s.Entries[0].Repeats);
        Assert.Equal(TimeSpan.FromMinutes(30), s.Entries[0].Span);       // 3 × 10 min
        Assert.Equal(TimeSpan.FromMinutes(30), s.Entries[3].Start);      // 循环之后的步骤从 30 min 起
        Assert.Equal(TimeSpan.FromMinutes(31), s.Total);
    }

    [Fact]
    public void 总时长取跨度而不是单轮时长()
    {
        var recipe = Harness.RecipeOf("只有循环",
            Harness.Mk(BuiltinCommands.LoopBegin, ("by", "按次数"), ("n", 4d)),
            Harness.Mk(BuiltinCommands.Wait, ("dur", 5d)),
            Harness.Mk(BuiltinCommands.LoopEnd));
        var s = Schedule.Build(recipe, Catalog());
        Assert.Equal(TimeSpan.FromMinutes(20), s.Total);
    }

    [Fact]
    public void 缺少驱动的步骤照样排得进去且被点名()
    {
        var recipe = Harness.RecipeOf("含未知指令",
            Harness.Mk(BuiltinCommands.Wait, ("dur", 1d)),
            Harness.Mk("vendor.unknown.cmd"));
        var s = Schedule.Build(recipe, Catalog());

        Assert.Equal(2, s.Entries.Count);
        Assert.Contains("vendor.unknown.cmd", s.MissingCommands);
        Assert.False(s.Entries[1].Known);
        Assert.Equal(TimeSpan.FromMinutes(1), s.Total);
    }

    [Fact]
    public void 停用的步骤不占时间()
    {
        var recipe = Harness.RecipeOf("停用",
            Harness.Mk(BuiltinCommands.Wait, ("dur", 10d)),
            Harness.Mk(BuiltinCommands.Wait, ("dur", 10d)));
        recipe.Steps[0].Enabled = false;
        var s = Schedule.Build(recipe, Catalog());

        Assert.Equal(TimeSpan.Zero, s.Entries[0].Duration);
        Assert.Equal(TimeSpan.FromMinutes(10), s.Total);
    }

    [Fact]
    public void 梯度控温的时长来自分段表()
    {
        var step = Harness.Mk(CommandSpecs.Gradient);
        var rows = new List<ParameterSet>
        {
            // 起点 25 ℃：升到 45 ℃ 用 10 min，再保持 5 min
            ParameterSet.Of(("t", 45d), ("r", 2d), ("h", 5d)),
            // 从 45 ℃ 降到 25 ℃ 用 20 min，不保持
            ParameterSet.Of(("t", 25d), ("r", 1d), ("h", 0d))
        };
        var recipe = Harness.RecipeOf("梯度", new Recipes.Step
        {
            CommandId = step.CommandId,
            Parameters = step.Parameters,
            Rows = rows
        });

        var s = Schedule.Build(recipe, Catalog());
        Assert.Equal(TimeSpan.FromMinutes(35), s.Total);
    }

    [Fact]
    public void 同样的输入必须得到同样的排期()
    {
        var recipe = Harness.RecipeOf("确定性",
            Harness.Mk(CommandSpecs.Control, ("target", 55d), ("rate", 1.5d)),
            Harness.Mk(CommandSpecs.Hold, ("dur", 15d)));
        var a = Schedule.Build(recipe, Catalog());
        var b = Schedule.Build(recipe, Catalog());
        Assert.Equal(a.Total, b.Total);
        for (var i = 0; i < a.Entries.Count; i++)
        {
            Assert.Equal(a.Entries[i].Start, b.Entries[i].Start);
            Assert.Equal(a.Entries[i].Duration, b.Entries[i].Duration);
            Assert.Equal(a.Entries[i].Title, b.Entries[i].Title);
        }
    }
}
