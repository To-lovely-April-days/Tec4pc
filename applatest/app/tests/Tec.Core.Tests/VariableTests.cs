using Tec.Core.Catalog;
using Tec.Core.Persistence;
using Tec.Core.Records;
using Tec.Core.Recipes;
using Tec.Driver.Abi;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>条件表达式：解析与求值。</summary>
public class CondTests
{
    private static double? Table(string id) => id switch
    {
        "n" => 3,
        "t0" => 25.5,
        "浊度" => 80,
        "pH" => 6.2,
        _ => null
    };

    [Theory]
    [InlineData("n > 2", true)]
    [InlineData("n < 2", false)]
    [InlineData("n >= 3", true)]
    [InlineData("n <= 2.9", false)]
    [InlineData("n = 3", true)]
    [InlineData("n != 3", false)]
    [InlineData("n ≠ 4", true)]
    [InlineData("浊度 > 50", true)]
    [InlineData("浊度 > 50 且 pH < 7", true)]
    [InlineData("浊度 > 100 或 pH < 7", true)]
    [InlineData("浊度 > 100 && pH < 7", false)]
    [InlineData("(浊度 > 100 或 n = 3) 且 t0 < 30", true)]
    [InlineData("t0 >= -10", true)]
    [InlineData("2 < n", true)]
    public void 求值(string text, bool expect)
    {
        var expr = Cond.Parse(text, out var err);
        Assert.True(expr is not null, err);
        var got = Cond.Eval(expr!, Table, out var evalErr);
        Assert.Null(evalErr);
        Assert.Equal(expect, got);
    }

    [Theory]
    [InlineData("")]
    [InlineData("浊度 >")]
    [InlineData("浊度 50")]
    [InlineData("浊度 > 50 NTU")]     // 老默认值那种带单位的写法，就该报出来
    [InlineData("> 50")]
    [InlineData("浊度 > 50 pH < 7")]  // 缺连接词
    [InlineData("(浊度 > 50")]
    [InlineData("a & b")]
    public void 病句要报错而不是硬解(string text)
    {
        Assert.Null(Cond.Parse(text, out var err));
        Assert.False(string.IsNullOrEmpty(err));
    }

    [Fact]
    public void 读不到的名字让整个条件算不出来()
    {
        var expr = Cond.Parse("没有的量 > 1", out _);
        Assert.NotNull(expr);
        var got = Cond.Eval(expr!, Table, out var err);
        Assert.Null(got);
        Assert.Contains("没有的量", err);
    }

    [Fact]
    public void 名字收集给校验器用()
    {
        var expr = Cond.Parse("浊度 > 50 且 (n = 3 或 pH < 7)", out _)!;
        var ids = new HashSet<string>();
        Cond.CollectIdents(expr, ids);
        Assert.Equal(new[] { "n", "pH", "浊度" }, ids.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("n", true)]
    [InlineData("加料次数", true)]
    [InlineData("t_0", true)]
    [InlineData("3n", false)]
    [InlineData("a b", false)]
    [InlineData("a>b", false)]
    [InlineData("", false)]
    public void 变量名合法性(string name, bool ok) => Assert.Equal(ok, Cond.ValidName(name));
}

/// <summary>变量 + 条件循环 + 条件等待，在真的执行引擎上跑。</summary>
public class VariableRunTests
{
    private static Recipe WithVars(Recipe r, params (string Name, double Init)[] vars)
    {
        foreach (var (n, i) in vars) r.Variables.Add(new RecipeVariable { Name = n, Init = i });
        return r;
    }

    [Fact]
    public async Task 条件循环真的按条件转_变量计数三轮退出()
    {
        await using var h = new Harness(600);
        await h.ReactorChannelAsync(1);
        var recipe = WithVars(Harness.RecipeOf("计数循环",
            Harness.Mk(BuiltinCommands.LoopBegin, ("by", "按条件"), ("cond", "n >= 3"), ("max", 10d)),
            Harness.Mk(BuiltinCommands.SetVar, ("var", "n"), ("op", "加上"), ("src", "数值"), ("val", 1d)),
            Harness.Mk(BuiltinCommands.LoopEnd)), ("n", 0));

        var run = h.Engine.StartChannel(1, recipe, "测试员");
        await h.Engine.Runner(1)!.Completion;

        Assert.Equal(ChannelRunState.Completed, run.State);
        // 循环体（设定变量）跑了整 3 轮，多一轮少一轮都是错
        Assert.Equal(3, run.Steps.Count(s => s.CommandId == BuiltinCommands.SetVar));
        Assert.Equal(3, h.Engine.Runner(1)!.LiveVariables["n"]);
        // 条件满足留了痕
        Assert.Contains(run.Events, e => e.Kind == EventKind.Note && e.Text.Contains("循环条件满足"));
    }

    [Fact]
    public async Task 条件永不满足时最多轮次兜底()
    {
        await using var h = new Harness(600);
        await h.ReactorChannelAsync(1);
        var begin = Harness.Mk(BuiltinCommands.LoopBegin,
            ("by", "按条件"), ("cond", "n >= 3"), ("max", 4d));
        begin.PauseOnFault = false;          // 只验兜底，不验暂停
        var recipe = WithVars(Harness.RecipeOf("空转",
            begin,
            Harness.Mk(BuiltinCommands.Mark, ("tag", "转一圈")),
            Harness.Mk(BuiltinCommands.LoopEnd)), ("n", 0));

        var run = h.Engine.StartChannel(1, recipe, "测试员");
        await h.Engine.Runner(1)!.Completion;

        Assert.Equal(ChannelRunState.Completed, run.State);
        Assert.Equal(4, run.Steps.Count(s => s.CommandId == BuiltinCommands.Mark));
        Assert.Contains(run.Events, e => e.Kind == EventKind.Alarm && e.Text.Contains("最多轮次"));
    }

    [Fact]
    public async Task 条件等待_已满足就立刻过()
    {
        await using var h = new Harness(600);
        await h.ReactorChannelAsync(1);
        var recipe = WithVars(Harness.RecipeOf("即时",
            Harness.Mk(BuiltinCommands.WaitUntil, ("cond", "t = 5"), ("timeout", 1d))), ("t", 5));

        var run = h.Engine.StartChannel(1, recipe, "测试员");
        await h.Engine.Runner(1)!.Completion;

        var step = run.Steps.Single();
        Assert.Equal(EndReason.ConditionMet, step.Reason);
        Assert.Equal(StepStatus.Done, step.Status);
    }

    [Fact]
    public async Task 条件等待_超时按失败处理()
    {
        await using var h = new Harness(600);
        await h.ReactorChannelAsync(1);
        var wait = Harness.Mk(BuiltinCommands.WaitUntil,
            ("cond", "t > 10"), ("timeout", 0.5d), ("onTimeout", "按失败处理"));
        wait.PauseOnFault = false;
        var recipe = WithVars(Harness.RecipeOf("等不到", wait), ("t", 5));

        var run = h.Engine.StartChannel(1, recipe, "测试员");
        await h.Engine.Runner(1)!.Completion;

        var step = run.Steps.Single();
        Assert.Equal(StepStatus.Failed, step.Status);
    }

    [Fact]
    public async Task 条件等待_超时也可以选择继续()
    {
        await using var h = new Harness(600);
        await h.ReactorChannelAsync(1);
        var recipe = WithVars(Harness.RecipeOf("继续",
            Harness.Mk(BuiltinCommands.WaitUntil,
                ("cond", "t > 10"), ("timeout", 0.5d), ("onTimeout", "继续执行")),
            Harness.Mk(BuiltinCommands.Mark, ("tag", "到这了"))), ("t", 5));

        var run = h.Engine.StartChannel(1, recipe, "测试员");
        await h.Engine.Runner(1)!.Completion;

        Assert.Equal(ChannelRunState.Completed, run.State);
        Assert.Equal(EndReason.Timeout, run.Steps[0].Reason);
        Assert.Equal(StepStatus.Done, run.Steps[0].Status);
        Assert.Contains(run.Steps, s => s.CommandId == BuiltinCommands.Mark);
    }

    [Fact]
    public async Task 设定变量能记下当前读数()
    {
        await using var h = new Harness(600);
        await h.ReactorChannelAsync(1, withPh: true);
        await Task.Delay(300);           // 让仿真探头先出几个数
        var recipe = WithVars(Harness.RecipeOf("记 pH",
            Harness.Mk(BuiltinCommands.SetVar, ("var", "p0"), ("op", "设为"), ("src", "当前 pH"))),
            ("p0", -1));

        var run = h.Engine.StartChannel(1, recipe, "测试员");
        await h.Engine.Runner(1)!.Completion;

        Assert.Equal(ChannelRunState.Completed, run.State);
        var p0 = h.Engine.Runner(1)!.LiveVariables["p0"];
        Assert.InRange(p0, 0, 14);       // 真读到了 pH，不再是初值 -1
    }

    [Fact]
    public async Task 设定变量引用不存在的变量按失败处理()
    {
        await using var h = new Harness(600);
        await h.ReactorChannelAsync(1);
        var step = Harness.Mk(BuiltinCommands.SetVar, ("var", "不存在"), ("op", "设为"), ("val", 1d));
        step.PauseOnFault = false;
        var recipe = Harness.RecipeOf("坏引用", step);

        // 校验器会拦（var-ref 是 Error），这条测的就是拦
        var ex = Assert.Throws<Tec.Core.Execution.RecipeRejectedException>(
            () => h.Engine.StartChannel(1, recipe, "测试员"));
        Assert.Contains(ex.Errors, i => i.Code == "var-ref");
    }
}

/// <summary>校验器认不认得条件与变量。</summary>
public class VariableValidatorTests
{
    private static readonly CommandCatalog Catalog = new();

    private static IReadOnlyList<ValidationIssue> Check(Recipe r)
        => RecipeValidator.Validate(r, Catalog);

    [Fact]
    public void 病句条件是Error()
    {
        var r = Harness.RecipeOf("病句",
            Harness.Mk(BuiltinCommands.LoopBegin, ("by", "按条件"), ("cond", "浊度 > 50 NTU")),
            Harness.Mk(BuiltinCommands.LoopEnd));
        Assert.Contains(Check(r), i => i.Level == IssueLevel.Error && i.Code == "cond");
    }

    [Fact]
    public void 按次数的循环不管cond里写了什么()
    {
        var r = Harness.RecipeOf("按次数",
            Harness.Mk(BuiltinCommands.LoopBegin, ("by", "按次数"), ("n", 2d), ("cond", "随便写的")),
            Harness.Mk(BuiltinCommands.LoopEnd));
        Assert.DoesNotContain(Check(r), i => i.Code == "cond");
    }

    [Fact]
    public void 条件里的名字要么是变量要么是实时量()
    {
        var r = Harness.RecipeOf("未知名",
            Harness.Mk(BuiltinCommands.WaitUntil, ("cond", "咕咕 > 1"), ("timeout", 1d)));
        Assert.Contains(Check(r), i => i.Level == IssueLevel.Error && i.Message.Contains("咕咕"));

        r.Variables.Add(new RecipeVariable { Name = "咕咕" });
        Assert.DoesNotContain(Check(r), i => i.Code == "cond");
    }

    [Fact]
    public void 变量表的毛病都点名()
    {
        var r = Harness.RecipeOf("变量表");
        r.Variables.Add(new RecipeVariable { Name = "n" });
        r.Variables.Add(new RecipeVariable { Name = "n" });          // 重名
        r.Variables.Add(new RecipeVariable { Name = "pH" });         // 保留名
        r.Variables.Add(new RecipeVariable { Name = "3x" });         // 不合法
        r.Variables.Add(new RecipeVariable { Name = "" });           // 没起名
        var bad = Check(r).Where(i => i.Code == "var-name").ToList();
        Assert.Equal(4, bad.Count);
    }

    [Fact]
    public void 变量随配方存取一个来回不掉东西()
    {
        var r = Harness.RecipeOf("存取");
        r.Variables.Add(new RecipeVariable { Name = "n", Init = 3, Unit = "次", Note = "计数" });
        r.Variables.Add(new RecipeVariable { Name = "t0", Init = -5.5 });

        var back = TecJson.Read<RecipeDoc>(TecJson.Write(r.ToDoc())).ToModel();

        Assert.Equal(2, back.Variables.Count);
        Assert.Equal("n", back.Variables[0].Name);
        Assert.Equal(3, back.Variables[0].Init);
        Assert.Equal("次", back.Variables[0].Unit);
        Assert.Equal("计数", back.Variables[0].Note);
        Assert.Equal(-5.5, back.Variables[1].Init);
        // 快照也带着走
        Assert.Equal(2, back.Snapshot().Variables.Count);
    }
}
