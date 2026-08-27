using System.Diagnostics;
using Tec.Core.Benches;
using Tec.Core.Catalog;
using Tec.Core.Data;
using Tec.Core.Execution;
using Tec.Core.Persistence;
using Tec.Core.Records;
using Tec.Core.Recipes;
using Tec.Core.Scheduling;
using Tec.Driver.Abi;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 并行步骤（iControl 的 Alignment = Series | Parallel）。
/// 一个并行组 = 一条串行步 + 紧随其后的并行步：组内同一时刻开跑，
/// **全部结束**下一组才开始——正是 iControl 的 Phase 同步语义。
/// 这里盯四件事：排期同起点、执行真并发且组后同步、失败暂停在组里
/// 照样生效、位置规矩（第一步 / 循环标记 / 起始步骤不能并）。
/// </summary>
public class ParallelStepTests
{
    private static Step Wait(double min, bool parallel = false)
    {
        var s = Harness.Mk(BuiltinCommands.Wait, ("dur", min));
        s.Parallel = parallel;
        return s;
    }

    private static async Task Until(Func<bool> cond, int ms = 8000)
    {
        var sw = Stopwatch.StartNew();
        while (!cond())
        {
            Assert.True(sw.ElapsedMilliseconds < ms, "等待条件超时");
            await Task.Delay(20);
        }
    }

    // ── 排期 ────────────────────────────────────────────────────────

    [Fact]
    public void 排期_并行成员同一起点_组时长取最长()
    {
        var r = Harness.RecipeOf("并行", Wait(10), Wait(4, parallel: true), Wait(5));
        var s = Schedule.Build(r, new CommandCatalog());

        Assert.Equal(TimeSpan.Zero, s.Entries[0].Start);
        Assert.Equal(TimeSpan.Zero, s.Entries[1].Start);          // 与上一步同一起点
        Assert.Equal(TimeSpan.FromMinutes(10), s.Entries[2].Start); // 组时长 = max(10, 4)
        Assert.Equal(TimeSpan.FromMinutes(15), s.Total);
    }

    [Fact]
    public void 排期_停用的步骤不隔断并行组()
    {
        var off = Wait(99);
        off.Enabled = false;
        var r = Harness.RecipeOf("夹着停用", Wait(10), off, Wait(4, parallel: true));
        var s = Schedule.Build(r, new CommandCatalog());

        Assert.Equal(TimeSpan.Zero, s.Entries[2].Start);          // 仍并在第一步上
        Assert.Equal(TimeSpan.Zero, s.Entries[1].Duration);
        Assert.Equal(TimeSpan.FromMinutes(10), s.Total);
    }

    // ── 执行 ────────────────────────────────────────────────────────

    [Fact]
    public async Task 执行_组内真并发_全到齐才走下一步()
    {
        var engine = new RunEngine(new CommandCatalog(), new BuiltinCommandProvider(new AutoOperatorGate()),
                                   new ResourceArbiter(), new DataPipeline(), () => DateTimeOffset.Now)
        { TimeScale = 100 };
        try
        {
            engine.Attach(new Channel(1, "X1", 0));
            engine.NewBatch("测试批次", "张三", "测试台面");

            var a = Wait(1);                        // 100× 时标下约 0.6 s
            var b = Wait(1, parallel: true);
            var mark = Harness.Mk(BuiltinCommands.Mark, ("tag", "组后"));
            var run = engine.StartChannel(1, Harness.RecipeOf("并发", a, b, mark), "张三");
            await engine.Runner(1)!.Completion.WaitAsync(TimeSpan.FromSeconds(15));

            var ra = run.Steps.Single(x => x.StepId == a.StepId);
            var rb = run.Steps.Single(x => x.StepId == b.StepId);
            var rc = run.Steps.Single(x => x.StepId == mark.StepId);

            // 真并发：两步的执行区间互相重叠（串行的话 b 要等 a 收完尾）
            Assert.True(rb.ActualStart < ra.ActualEnd && ra.ActualStart < rb.ActualEnd,
                "并行成员的区间应当重叠");
            // 组同步：组后那一步等两个成员都结束才开始
            Assert.True(rc.ActualStart >= ra.ActualEnd && rc.ActualStart >= rb.ActualEnd,
                "组后的步骤必须等全组收尾");
            Assert.All(new[] { ra, rb, rc }, x => Assert.Equal(StepStatus.Done, x.Status));
        }
        finally
        {
            engine.AbortAll();
            engine.Dispose();
        }
    }

    [Fact]
    public async Task 执行_并行成员失败_失败暂停照样生效()
    {
        var engine = new RunEngine(new CommandCatalog(), new BuiltinCommandProvider(new AutoOperatorGate()),
                                   new ResourceArbiter(), new DataPipeline(), () => DateTimeOffset.Now)
        { TimeScale = 100 };
        try
        {
            engine.Attach(new Channel(1, "X1", 0));
            engine.NewBatch("测试批次", "张三", "测试台面");

            // 校验器会把静态就能看出的问题拦在启动前，所以拿一个**运行期**才失败的成员：
            // 条件等不到、超时按失败处理
            var bad = Harness.Mk(BuiltinCommands.Wait,
                ("by", "按条件"), ("cond", "x > 5"), ("timeout", 0.1d), ("onTimeout", "按失败处理"));
            bad.Parallel = true;
            var recipe = Harness.RecipeOf("组内失败",
                Wait(1), bad, Harness.Mk(BuiltinCommands.Mark, ("tag", "别跑到这")));
            recipe.Variables.Add(new RecipeVariable { Name = "x", Init = 0 });
            engine.StartChannel(1, recipe, "张三");

            var runner = engine.Runner(1)!;
            await Until(() => runner.State == ChannelRunState.Paused, 15000);
        }
        finally
        {
            engine.AbortAll();
            engine.Dispose();
        }
    }

    // ── 持久化与复制 ────────────────────────────────────────────────

    [Fact]
    public void 并行标记随文件走_复制也带上()
    {
        var r = Harness.RecipeOf("存取", Wait(10), Wait(4, parallel: true));
        var back = r.ToDoc().ToModel();
        Assert.False(back.Steps[0].Parallel);
        Assert.True(back.Steps[1].Parallel);

        Assert.True(r.Steps[1].Clone().Parallel);
    }

    // ── 位置规矩 ────────────────────────────────────────────────────

    [Fact]
    public void 第一步与循环标记不能并行_循环体第一步是组头()
    {
        var first = Wait(5, parallel: true);
        var r1 = Harness.RecipeOf("第一步就并", first);
        Assert.Contains(RecipeValidator.Validate(r1, new CommandCatalog()),
            i => i.Code == "parallel-order" && i.StepId == first.StepId);

        var lb = Harness.Mk(BuiltinCommands.LoopBegin, ("by", "按次数"), ("n", 2d));
        lb.Parallel = true;
        var r2 = Harness.RecipeOf("标记并行", Wait(5), lb, Harness.Mk(BuiltinCommands.LoopEnd));
        Assert.Contains(RecipeValidator.Validate(r2, new CommandCatalog()),
            i => i.Code == "parallel-order" && i.StepId == lb.StepId);

        // 循环体的第一步不能「与上一步并行」——它的上一步是循环开始
        var inLoop = Wait(5, parallel: true);
        var r3 = Harness.RecipeOf("并在标记上",
            Wait(5), Harness.Mk(BuiltinCommands.LoopBegin, ("by", "按次数"), ("n", 2d)),
            inLoop, Harness.Mk(BuiltinCommands.LoopEnd));
        Assert.Contains(RecipeValidator.Validate(r3, new CommandCatalog()),
            i => i.Code == "parallel-order" && i.StepId == inLoop.StepId);
    }

    [Fact]
    public void 起始步骤独走_谁都不能跟它并()
    {
        var tail = Wait(5, parallel: true);
        var r = Harness.RecipeOf("并在起始上",
            Harness.Mk(BuiltinCommands.FirstFill, ("fill", true)), tail);
        Assert.Contains(RecipeValidator.Validate(r, new CommandCatalog()),
            i => i.Code == "parallel-order" && i.StepId == tail.StepId);
    }

    [Fact]
    public void 正常的第二步并行不报任何位置问题()
    {
        var r = Harness.RecipeOf("合规", Wait(10), Wait(4, parallel: true));
        Assert.DoesNotContain(RecipeValidator.Validate(r, new CommandCatalog()),
            i => i.Code == "parallel-order");
    }
}
