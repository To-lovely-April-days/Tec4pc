using Tec.Core.Catalog;
using Tec.Core.Scheduling;
using Tec.Driver.Abi;
using Tec.Drivers.Simulator;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 温度剖面。这条带子是配方库那一页的门面，最要紧的是**每一段都能对回排期**——
/// 画歪了没人看得出来，只会以为这条配方本来就长这样。
/// </summary>
public class TempProfileTests
{
    private static CommandCatalog Catalog()
    {
        var c = new CommandCatalog();
        c.Register(new Rd105ReactorDriver().Commands);
        c.Register(new DosingPumpDriver().Commands);
        return c;
    }

    private static TempProfile Of(Recipes.Recipe r, EstimationContext? seed = null)
    {
        var cat = Catalog();
        return TempProfile.Build(r, cat, Schedule.Build(r, cat, seed));
    }

    [Fact]
    public void 控温那一段两端就是起止温度_时长跟排期一致()
    {
        var r = Harness.RecipeOf("升温",
            Harness.Mk(CommandSpecs.Control, ("target", 60d), ("rate", 2d)));
        var p = Of(r, new EstimationContext { Temperature = 20 });

        var g = Assert.Single(p.Segments);
        Assert.Equal(20, g.FromC, 3);
        Assert.Equal(60, g.ToC, 3);
        Assert.Equal(TimeSpan.Zero, g.Start);
        Assert.Equal(TimeSpan.FromMinutes(20), g.End);      // (60−20)/2
        Assert.True(p.HasCurve);
    }

    [Fact]
    public void 保温那一段是平的_接着上一段的温度往下走()
    {
        var r = Harness.RecipeOf("升温后保温",
            Harness.Mk(CommandSpecs.Control, ("target", 60d), ("rate", 2d)),
            Harness.Mk(CommandSpecs.Hold, ("dur", 30d)));
        var p = Of(r, new EstimationContext { Temperature = 20 });

        Assert.Equal(2, p.Segments.Count);
        Assert.Equal(60, p.Segments[1].FromC, 3);
        Assert.Equal(60, p.Segments[1].ToC, 3);             // 保温不改温度
        Assert.Equal(p.Segments[0].End, p.Segments[1].Start);   // 首尾相接，不留缝
        Assert.Equal(TimeSpan.FromMinutes(50), p.Total);
    }

    [Fact]
    public void 自然冷却把温度带回目标()
    {
        var r = Harness.RecipeOf("冷却",
            Harness.Mk(CommandSpecs.PassiveCool, ("target", 25d)));
        var p = Of(r, new EstimationContext { Temperature = 80 });

        var g = Assert.Single(p.Segments);
        Assert.Equal(80, g.FromC, 3);
        Assert.Equal(25, g.ToC, 3);
    }

    [Fact]
    public void 全程没动过温度就不值得画()
    {
        var r = Harness.RecipeOf("只搅拌",
            Harness.Mk(CommandSpecs.Stir, ("rpm", 400d), ("ramp", 5d)),
            Harness.Mk(BuiltinCommands.Wait, ("dur", 10d)));
        var p = Of(r);

        Assert.False(p.HasCurve);
        Assert.Equal(p.MinC, p.MaxC, 3);
    }

    [Fact]
    public void 搅拌是持续状态_下一条搅拌把上一条收住()
    {
        var r = Harness.RecipeOf("两段搅拌",
            Harness.Mk(CommandSpecs.Stir, ("rpm", 400d), ("ramp", 5d)),
            Harness.Mk(BuiltinCommands.Wait, ("dur", 10d)),
            Harness.Mk(CommandSpecs.Stir, ("rpm", 0d), ("ramp", 5d)),
            Harness.Mk(BuiltinCommands.Wait, ("dur", 5d)));
        var p = Of(r);

        Assert.Equal(2, p.States.Count);
        Assert.Equal("搅拌", p.States[0].Name);
        Assert.Equal("400 rpm", p.States[0].Value);
        Assert.Equal(TimeSpan.Zero, p.States[0].Start);
        // 第二条搅拌开始的时刻 = 5 s（第一条的到达用时）+ 10 min
        Assert.Equal(TimeSpan.FromSeconds(5 + 600), p.States[0].End);

        Assert.Equal("停机", p.States[1].Value);            // 转速 0 就是停机，不写「0 rpm」
        Assert.Equal(p.Total, p.States[1].End);             // 没人再改，一直到配方走完
    }

    [Fact]
    public void 安全联锁挂上就一直生效()
    {
        var r = Harness.RecipeOf("联锁",
            Harness.Mk(BuiltinCommands.Wait, ("dur", 5d)),
            Harness.Mk(BuiltinCommands.Interlock, ("src", "釜内 Tr"), ("op", ">"), ("val", 100d),
                       ("act", "停止实验")),
            Harness.Mk(BuiltinCommands.Wait, ("dur", 5d)));
        var p = Of(r);

        var st = Assert.Single(p.States);
        Assert.Equal("安全联锁", st.Name);
        Assert.Contains("100", st.Value);
        Assert.Equal(TimeSpan.FromMinutes(5), st.Start);
        Assert.Equal(p.Total, st.End);
    }

    [Fact]
    public void 循环行不画成一段_但会标出来只画了第一轮()
    {
        var r = Harness.RecipeOf("循环",
            Harness.Mk(BuiltinCommands.LoopBegin, ("by", "按次数"), ("n", 3d)),
            Harness.Mk(BuiltinCommands.Wait, ("dur", 10d)),
            Harness.Mk(BuiltinCommands.LoopEnd));
        var p = Of(r);

        // 循环开始行的跨度是 30 min，跟 body 那一步重叠——它不该自成一段
        var g = Assert.Single(p.Segments);
        Assert.Equal(1, g.Index);
        Assert.Equal(TimeSpan.FromMinutes(10), g.End);
        Assert.True(p.HasLoop);
    }

    [Fact]
    public void 跳过的步骤不进剖面()
    {
        var r = Harness.RecipeOf("跳一步",
            Harness.Mk(CommandSpecs.Control, ("target", 60d), ("rate", 2d)),
            Harness.Mk(CommandSpecs.Hold, ("dur", 30d)));
        r.Steps[1].Enabled = false;
        var p = Of(r, new EstimationContext { Temperature = 20 });

        Assert.Single(p.Segments);
        Assert.Equal(0, p.Segments[0].Index);
    }

    [Fact]
    public void 空配方给一份空剖面_不是崩溃也不是假曲线()
    {
        var p = Of(Harness.RecipeOf("空"));
        Assert.True(p.IsEmpty);
        Assert.Empty(p.Segments);
        Assert.Empty(p.States);
    }

    [Theory]
    [InlineData(200, 50)]
    [InlineData(100, 20)]
    [InlineData(40, 10)]
    [InlineData(12, 5)]
    public void 温度刻度按跨度挑一档(double span, double want)
        => Assert.Equal(want, TempProfile.TickStep(span));

    [Theory]
    [InlineData(800, 180)]
    [InlineData(400, 120)]
    [InlineData(200, 60)]
    [InlineData(60, 30)]
    [InlineData(20, 10)]
    [InlineData(5, 5)]
    public void 时间刻度按总时长挑一档(double minutes, int want)
        => Assert.Equal(want, TempProfile.TimeStepMinutes(TimeSpan.FromMinutes(minutes)));
}
