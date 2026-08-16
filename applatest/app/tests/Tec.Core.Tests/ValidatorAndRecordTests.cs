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
            Harness.Mk(BuiltinCommands.LoopBegin, ("by", "按次数"), ("n", 2d)),
            Harness.Mk(BuiltinCommands.Wait, ("dur", 1d)));
        var issues = RecipeValidator.Validate(recipe, Catalog());
        Assert.Contains(issues, i => i.Code == "loop-unbalanced" && i.Level == IssueLevel.Error);
    }

    [Fact]
    public void 参数超出静态范围要报错()
    {
        var recipe = Harness.RecipeOf("超范围",
            Harness.Mk(CommandSpecs.Control, ("target", 900d), ("rate", 2d)));
        var issues = RecipeValidator.Validate(recipe, Catalog());
        Assert.Contains(issues, i => i.Code == "out-of-range" && i.Level == IssueLevel.Error);
    }

    [Fact]
    public void 声明了超时字段却没填要提醒()
    {
        // 自然冷却是原型里唯一自带"超时放弃"的到达类指令
        var recipe = Harness.RecipeOf("没超时",
            Harness.Mk(CommandSpecs.PassiveCool, ("target", 25d)));
        var issues = RecipeValidator.Validate(recipe, Catalog());
        Assert.Contains(issues, i => i.Code == "no-timeout" && i.Level == IssueLevel.Warning);
    }

    [Fact]
    public void 二十条指令一条不多一条不少()
    {
        var c = Catalog();
        c.Register(new TurbidityProbeDriver().Commands);
        c.Register(new RamanProbeDriver().Commands);
        c.Register(new InfraredProbeDriver().Commands);

        // 通用 8 · 温度 4 · 搅拌 1 · 加料 1 · pH 2 · 在线分析 4
        Assert.Equal(20, c.All.Count);
        Assert.Equal(8, c.InModule("通用").Count);
        Assert.Equal(4, c.InModule("温度模块").Count);
        Assert.Single(c.InModule("搅拌"));
        Assert.Single(c.InModule("加料"));
        Assert.Equal(2, c.InModule("pH 控制").Count);
        Assert.Equal(4, c.InModule("在线分析").Count);
    }

    [Fact]
    public void 精简掉的指令不许偷偷回来()
    {
        var c = Catalog();
        c.Register(new TurbidityProbeDriver().Commands);
        c.Register(new RamanProbeDriver().Commands);
        c.Register(new InfraredProbeDriver().Commands);
        var names = c.All.Select(d => d.DisplayName).ToHashSet(StringComparer.Ordinal);

        foreach (var n in new[]
        {
            "等待", "循环开始", "循环结束", "消息提示", "标记事件", "采样提醒", "安全联锁", "结束实验",
            "控温", "恒温保持", "梯度控温", "自然冷却",
            "搅拌",
            "加料",
            "pH 采集", "pH 反馈加料",
            "拉曼采集", "红外采集", "浊度采集", "溶解度点测定"
        })
            Assert.True(names.Contains(n), $"指令「{n}」应该在，实装里没有");

        // 合并 / 删掉的那些：同一个动作只留一条，回来了就是又拆开了
        foreach (var n in new[]
        {
            "升温至", "降温至", "夹套控温 Tj", "釜内控温 Tr", "结晶模式（蒸回流）",
            "设定转速", "转速梯度", "停止搅拌",
            "恒速加料", "定量加料", "分段加料",
            "pH 保持（反馈）", "pH 上下限报警", "Tr−Tj 记录"
        })
            Assert.False(names.Contains(n), $"指令「{n}」已经并掉了，不该再出现在指令库里");
    }

    [Fact]
    public void 摘要与整句描述按原型分两句写()
    {
        var c = Catalog();
        Assert.True(c.TryGet(CommandSpecs.Control, out var d));
        var p = new ParameterSet().FillDefaults(d.Parameters);

        // PSPEC.sum：卡片上那一行
        Assert.Equal("釜内 Tr → 60 ℃ · 2 ℃/min", d.SummaryOf(p));
        // DESC：整句工艺语句
        Assert.Equal("控温 釜内 Tr 至 60 ℃，2 ℃/min", d.DescribeOf(p));
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
    public void 反馈加料同时要求检测与加料两项能力()
    {
        var catalog = Catalog();
        Assert.True(catalog.TryGet(CommandSpecs.PhHold, out var d));
        // 判据是 pH，泵只是执行机构：主能力落在检测上，加料是附加要求
        Assert.Equal(typeof(IScalarSensor), d.RequiredCapability);
        Assert.Contains(typeof(IDosing), d.AlsoRequires);
        Assert.Equal(CommandSpecs.ModPh, d.Module);
    }

    [Fact]
    public void 重叠时段用同一台泵会被查出来()
    {
        var catalog = Catalog();
        var recipe = Harness.RecipeOf("加料",
            Harness.Mk(CommandSpecs.Dose, ("vol", 10d), ("rate", 1d)));   // 10 min
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
            Harness.Mk(CommandSpecs.Dose, ("vol", 2d), ("rate", 1d)));   // 2 min
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
