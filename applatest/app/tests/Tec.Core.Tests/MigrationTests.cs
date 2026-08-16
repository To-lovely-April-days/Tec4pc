using Tec.Core.Catalog;
using Tec.Core.Recipes;
using Tec.Driver.Abi;
using Tec.Drivers.Simulator;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 指令库从 23 条精简到 12 条之后，已经存过盘的配方还得打得开。
/// 这一组盯的是「翻译得对」和「翻译不出来的绝不瞎猜」两件事。
/// </summary>
public sealed class MigrationTests
{
    private static Recipe Old(string commandId, params (string, object?)[] kv)
    {
        var r = new Recipe { Name = "老配方" };
        r.Steps.Add(new Step { CommandId = commandId, Parameters = ParameterSet.Of(kv) });
        return r;
    }

    private static CommandCatalog Catalog()
    {
        var c = new CommandCatalog();
        c.Register(new Rd105ReactorDriver().Commands);
        c.Register(new DosingPumpDriver().Commands);
        c.Register(new PhProbeDriver().Commands);
        return c;
    }

    [Fact]
    public void 升温至与降温至都翻成控温且参数原样带过去()
    {
        foreach (var oldId in new[] { "tec.temp.rampUp", "tec.temp.rampDown" })
        {
            var r = Old(oldId, ("target", 60d), ("rate", 2d), ("obj", "釜内 Tr"), ("tol", 0.5d));
            var notes = RecipeMigration.Apply(r);

            Assert.Single(notes);
            Assert.Equal(CommandSpecs.Control, r.Steps[0].CommandId);
            Assert.Equal(60, r.Steps[0].Parameters.Num("target"));
            Assert.Equal(2, r.Steps[0].Parameters.Num("rate"));
            Assert.Equal("釜内 Tr", r.Steps[0].Parameters.Str("obj"));
        }
    }

    [Fact]
    public void 夹套控温翻成控温并把控温对象设成夹套()
    {
        var r = Old("tec.temp.jacket", ("target", 55d), ("rate", 2d), ("dur", 20d));
        var notes = RecipeMigration.Apply(r);

        Assert.Equal(CommandSpecs.Control, r.Steps[0].CommandId);
        Assert.Equal("夹套 Tj", r.Steps[0].Parameters.Str("obj"));
        // 老指令的「维持时长」在新指令里没有落点，必须删干净——留着会被当成有效参数
        Assert.False(r.Steps[0].Parameters.Has("dur"));
        // 而且要在说明里点出来，让人自己补一条恒温保持
        Assert.Contains("恒温保持", notes[0]);
    }

    [Fact]
    public void 转速梯度翻成搅拌时分钟要换成秒()
    {
        var r = Old("tec.stir.ramp", ("from", 400d), ("to", 800d), ("dur", 10d));
        RecipeMigration.Apply(r);

        Assert.Equal(CommandSpecs.Stir, r.Steps[0].CommandId);
        Assert.Equal(800, r.Steps[0].Parameters.Num("rpm"));
        Assert.Equal(600, r.Steps[0].Parameters.Num("ramp"));   // 10 min = 600 s
        Assert.False(r.Steps[0].Parameters.Has("from"));
        Assert.False(r.Steps[0].Parameters.Has("to"));
    }

    [Fact]
    public void 停止搅拌翻成转速零()
    {
        var r = Old("tec.stir.stop", ("ramp", 8d));
        RecipeMigration.Apply(r);

        Assert.Equal(CommandSpecs.Stir, r.Steps[0].CommandId);
        Assert.Equal(0, r.Steps[0].Parameters.Num("rpm"));
        Assert.Equal(8, r.Steps[0].Parameters.Num("ramp"));
    }

    [Fact]
    public void 定量加料的完成时间要换算成流量()
    {
        var r = Old("tec.dose.volume", ("vol", 10d), ("dur", 5d), ("liq", "溶剂"));
        RecipeMigration.Apply(r);

        Assert.Equal(CommandSpecs.Dose, r.Steps[0].CommandId);
        Assert.Equal(2, r.Steps[0].Parameters.Num("rate"));      // 10 mL / 5 min
        Assert.Equal(10, r.Steps[0].Parameters.Num("vol"));
        Assert.False(r.Steps[0].Parameters.Has("dur"));
    }

    [Fact]
    public void 翻译不出来的指令原样留着让校验器去报()
    {
        // 结晶模式（蒸回流）没有对应硬件，不给它编一条
        var r = Old("tec.temp.reflux", ("temp", 78d), ("dur", 45d));
        var notes = RecipeMigration.Apply(r);

        Assert.Empty(notes);
        Assert.Equal("tec.temp.reflux", r.Steps[0].CommandId);

        var issues = RecipeValidator.Validate(r, Catalog());
        Assert.Contains(issues, i => i.Code == "missing-driver");
    }

    [Fact]
    public void 翻译要保住步骤号否则老记录对不上()
    {
        var r = Old("tec.dose.rate", ("vol", 10d), ("rate", 1d));
        var id = r.Steps[0].StepId;
        r.Steps[0].Comment = "先加一半";

        RecipeMigration.Apply(r);

        Assert.Equal(id, r.Steps[0].StepId);
        Assert.Equal("先加一半", r.Steps[0].Comment);
    }

    [Fact]
    public void 翻译过的老配方能通过校验()
    {
        var r = new Recipe { Name = "老配方" };
        r.Steps.Add(new Step { CommandId = "tec.stir.setSpeed", Parameters = ParameterSet.Of(("rpm", 400d)) });
        r.Steps.Add(new Step
        {
            CommandId = "tec.temp.rampUp",
            Parameters = ParameterSet.Of(("target", 60d), ("rate", 2d), ("obj", "釜内 Tr"), ("tol", 0.5d))
        });
        r.Steps.Add(new Step
        {
            CommandId = "tec.dose.volume",
            Parameters = ParameterSet.Of(("vol", 10d), ("dur", 5d), ("pump", "加料泵 1"), ("liq", "溶剂"))
        });

        RecipeMigration.Apply(r);

        var issues = RecipeValidator.Validate(r, Catalog());
        // tec.stir.setSpeed 从来就不是有效 Id（是早年的手误），它照样该被报出来；
        // 翻译过的两条不许再报
        Assert.Single(issues, i => i.Code == "missing-driver");
        Assert.DoesNotContain(issues, i => i.Code == "out-of-range");
    }
}
