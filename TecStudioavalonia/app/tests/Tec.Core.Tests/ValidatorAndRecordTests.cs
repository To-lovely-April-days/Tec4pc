using Tec.Core.Catalog;
using Tec.Core.Records;
using Tec.Core.Recipes;
using Tec.Core.Scheduling;
using Tec.Driver.Abi;
using Tec.Drivers.Simulator;
using Xunit;

namespace Tec.Core.Tests;

public class ValidatorTests
{
    private static CommandCatalog Catalog()
    {
        var c = new CommandCatalog();
        c.Register(new Rd105ReactorDriver().Commands);
        c.Register(new DosingPumpDriver().Commands);
        c.Register(new PhProbeDriver().Commands);
        return c;
    }

    [Fact]
    public void 循环不配对要报错()
    {
        var recipe = Harness.RecipeOf("漏了结束",
            Harness.Mk("tec.flow.loopBegin", ("方式", "按次数"), ("次数", 2d)),
            Harness.Mk("tec.flow.wait", ("时长", 60d)));
        var issues = RecipeValidator.Validate(recipe, Catalog());
        Assert.Contains(issues, i => i.Code == "loop-unbalanced" && i.Level == IssueLevel.Error);
    }

    [Fact]
    public void 参数超出静态范围要报错()
    {
        var recipe = Harness.RecipeOf("超范围",
            Harness.Mk("tec.temp.rampTo", ("目标", 900d), ("速率", 2d), ("超时", 600d)));
        var issues = RecipeValidator.Validate(recipe, Catalog());
        Assert.Contains(issues, i => i.Code == "out-of-range" && i.Level == IssueLevel.Error);
    }

    [Fact]
    public void 到达目标类指令没设超时要提醒()
    {
        var recipe = Harness.RecipeOf("没超时",
            Harness.Mk("tec.temp.rampTo", ("目标", 60d), ("速率", 2d)));
        var issues = RecipeValidator.Validate(recipe, Catalog());
        Assert.Contains(issues, i => i.Code == "no-timeout" && i.Level == IssueLevel.Warning);
    }

    [Fact]
    public void 缺少驱动的配方仍然能校验而不是打不开()
    {
        var recipe = Harness.RecipeOf("含未知",
            Harness.Mk("vendor.raman.acquire"));
        var issues = RecipeValidator.Validate(recipe, Catalog());
        Assert.Contains(issues, i => i.Code == "missing-driver");
        Assert.Contains(issues, i => i.Code == "duration");     // 总时长照样给得出来
    }

    [Fact]
    public void 反馈加料同时要求加料与检测两项能力()
    {
        var catalog = Catalog();
        Assert.True(catalog.TryGet("tec.dose.feedback", out var d));
        Assert.Equal(typeof(IDosing), d.RequiredCapability);
        Assert.Contains(typeof(IScalarSensor), d.AlsoRequires);
    }

    [Fact]
    public void 重叠时段用同一台泵会被查出来()
    {
        var catalog = Catalog();
        var recipe = Harness.RecipeOf("加料",
            Harness.Mk("tec.dose.constant", ("体积", 10d), ("流量", 1d)));   // 10 min
        var t0 = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);

        var plans = new Dictionary<int, (Schedule Schedule, DateTimeOffset Start)>
        {
            [1] = (Schedule.Build(recipe, catalog), t0),
            [2] = (Schedule.Build(recipe, catalog), t0.AddMinutes(3))
        };

        var issues = RecipeValidator.DetectResourceConflicts(plans, catalog,
            id => id.StartsWith("tec.dose.", StringComparison.Ordinal) ? "P1" : null);
        Assert.Contains(issues, i => i.Code == "resource-conflict");
    }

    [Fact]
    public void 不重叠就不该报冲突()
    {
        var catalog = Catalog();
        var recipe = Harness.RecipeOf("加料",
            Harness.Mk("tec.dose.constant", ("体积", 2d), ("流量", 1d)));   // 2 min
        var t0 = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);

        var plans = new Dictionary<int, (Schedule Schedule, DateTimeOffset Start)>
        {
            [1] = (Schedule.Build(recipe, catalog), t0),
            [2] = (Schedule.Build(recipe, catalog), t0.AddMinutes(30))
        };

        var issues = RecipeValidator.DetectResourceConflicts(plans, catalog, _ => "P1");
        Assert.DoesNotContain(issues, i => i.Code == "resource-conflict");
    }
}

public class RecordStoreTests
{
    [Fact]
    public void 记录文件被改过就校验不过()
    {
        var path = Path.Combine(Path.GetTempPath(), "tec-glp-" + Guid.NewGuid().ToString("N")[..8] + ".log");
        try
        {
            using (var store = new RecordStore(path))
            {
                store.Write(new EventRecord
                {
                    At = DateTimeOffset.Now, Channel = 1, Kind = EventKind.ChannelStarted, Text = "启动"
                });
                store.Write(new EventRecord
                {
                    At = DateTimeOffset.Now, Channel = 1, Kind = EventKind.Note, Text = "取样"
                });
                store.Sign("管理员", "批准");
            }

            Assert.True(RecordStore.Verify(path).Ok);

            var lines = File.ReadAllLines(path);
            var target = Array.FindIndex(lines, l => l.Contains("取样", StringComparison.Ordinal));
            Assert.True(target >= 0);
            lines[target] = lines[target].Replace("取样", "改过");
            File.WriteAllLines(path, lines);

            var (ok, bad) = RecordStore.Verify(path);
            Assert.False(ok);
            Assert.Equal(target + 1, bad);   // 从被改的那一行起，之后全部对不上
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}

public class DeviationTests
{
    [Fact]
    public void 相对阈值要配绝对下限否则短步骤会被噪声淹掉()
    {
        // 5 秒的步骤抖 1 秒：比例上 20% 超标，但绝对值太小，不该报红
        var tiny = new StepRecord
        {
            Index = 0, StepId = "a", CommandId = "x", Title = "短步骤",
            Termination = TerminationKind.Timer,
            PlanStart = TimeSpan.Zero, PlanDuration = TimeSpan.FromSeconds(5),
            ChannelStart = DateTimeOffset.Now,
            ActualDuration = TimeSpan.FromSeconds(6)
        };
        Assert.False(tiny.DurationOutOfTolerance());

        var real = new StepRecord
        {
            Index = 0, StepId = "b", CommandId = "x", Title = "长步骤",
            Termination = TerminationKind.Timer,
            PlanStart = TimeSpan.Zero, PlanDuration = TimeSpan.FromMinutes(10),
            ChannelStart = DateTimeOffset.Now,
            ActualDuration = TimeSpan.FromMinutes(12)
        };
        Assert.True(real.DurationOutOfTolerance());
    }

    [Fact]
    public void 零时长步骤不参与偏差统计()
    {
        var instant = new StepRecord
        {
            Index = 0, StepId = "c", CommandId = "x", Title = "设定转速",
            Termination = TerminationKind.Immediate,
            PlanStart = TimeSpan.Zero, PlanDuration = TimeSpan.Zero,
            ChannelStart = DateTimeOffset.Now,
            ActualDuration = TimeSpan.FromSeconds(40)
        };
        Assert.False(instant.DurationOutOfTolerance());
    }

    [Fact]
    public void 开始偏差与时长偏差算的是两件事()
    {
        var t0 = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
        var step = new StepRecord
        {
            Index = 1, StepId = "d", CommandId = "x", Title = "被拖累的一步",
            Termination = TerminationKind.Timer,
            PlanStart = TimeSpan.FromMinutes(10),
            PlanDuration = TimeSpan.FromMinutes(5),
            ChannelStart = t0,
            ActualStart = t0.AddMinutes(18),          // 比计划晚 8 分钟开始
            ActualDuration = TimeSpan.FromMinutes(5)  // 但自己跑得分毫不差
        };

        Assert.Equal(TimeSpan.FromMinutes(8), step.StartDeviation);
        Assert.Equal(TimeSpan.Zero, step.DurationDeviation);
    }
}
