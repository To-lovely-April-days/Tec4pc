using System.Diagnostics;
using Tec.Core.Benches;
using Tec.Core.Catalog;
using Tec.Core.Data;
using Tec.Core.Execution;
using Tec.Core.Persistence;
using Tec.Core.Recipes;
using Tec.Core.Safety;
using Tec.Driver.Abi;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 本步安全覆盖（iControl 每步的 Advanced 层）：步骤执行期间把临时限值
/// 挂进安全层、结束（成功、失败、中止都算）自动撤下还原。
/// 这里盯四件事：**挂上了真会触发**、**撤下真的还原**（不许把限值留过
/// 这一步的生命期）、三层同键分层不串、文件读写不丢。
/// </summary>
public class StepGuardTests
{
    private static async Task Until(Func<bool> cond, int ms = 8000)
    {
        var sw = Stopwatch.StartNew();
        while (!cond())
        {
            Assert.True(sw.ElapsedMilliseconds < ms, "等待条件超时");
            await Task.Delay(20);
        }
    }

    private sealed class Rig : IDisposable
    {
        public DateTimeOffset Now = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
        public readonly DataPipeline Pipeline = new();
        public readonly RunEngine Engine;

        public Rig()
        {
            Engine = new RunEngine(new CommandCatalog(), new BuiltinCommandProvider(new AutoOperatorGate()),
                                   new ResourceArbiter(), Pipeline, () => Now);
            Engine.Attach(new Channel(1, "X1", 0));
            Engine.NewBatch("测试批次", "张三", "测试台面");
        }

        public void PushTr(double v)
            => Pipeline.Push(new Sample(1, "Tr", Now.Ticks, Now, v, Quality.Good));

        public void Dispose()
        {
            Engine.AbortAll();
            Engine.Dispose();
        }
    }

    [Fact]
    public async Task 执行期间挂上_结束撤下()
    {
        using var rig = new Rig();
        var step = Harness.Mk(BuiltinCommands.Wait, ("dur", 60d));
        step.Guard = new StepGuard { TrMax = 80, Action = "停止加热" };

        rig.Engine.StartChannel(1, Harness.RecipeOf("守着", step), "张三");
        await Until(() => rig.Engine.Safety.Limits.Any(l => l.StepScope == step.StepId));

        var lim = rig.Engine.Safety.Limits.Single(l => l.StepScope == step.StepId);
        Assert.Equal("Tr", lim.Tag);
        Assert.Equal(80d, lim.Max);
        Assert.Null(lim.Min);
        Assert.Equal(SafetyAction.StopHeating, lim.Action);
        Assert.Contains("执行期间", lim.Note);

        var runner = rig.Engine.Runner(1)!;
        runner.Abort();
        await runner.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        // 怎么结束都不许把限值留过这一步的生命期
        Assert.DoesNotContain(rig.Engine.Safety.Limits, l => l.StepScope is not null);
    }

    [Fact]
    public async Task 挂上的限值真的会触发()
    {
        using var rig = new Rig();
        var step = Harness.Mk(BuiltinCommands.Wait, ("dur", 60d));
        step.Guard = new StepGuard { TrMax = 80, Action = "仅报警" };

        rig.Engine.StartChannel(1, Harness.RecipeOf("守着", step), "张三");
        await Until(() => rig.Engine.Safety.Limits.Any(l => l.StepScope == step.StepId));

        rig.PushTr(85);
        rig.Engine.Safety.Evaluate();          // 进入去抖
        rig.Now += TimeSpan.FromSeconds(4);    // 越过 3 秒去抖
        rig.PushTr(85);
        rig.Engine.Safety.Evaluate();

        var alarm = Assert.Single(rig.Engine.Alarms.Live);
        Assert.Contains("高于上限 80", alarm.Message);
    }

    [Fact]
    public void 三层同键分层不串()
    {
        // 底线、配方、本步可以在同一通道同一监测量上同动作并存。
        // KeyOf 不分层的话三条共用一份去抖与「在报」状态——后越限的那条永远发不出来
        var baseline = new SafetyLimit(1, "Tr", null, 181, null, TimeSpan.Zero, SafetyAction.AbortChannel)
        { FromDeviceLimits = true };
        var recipe = baseline with { Max = 80, FromDeviceLimits = false, FromRecipe = true };
        var step = baseline with { Max = 60, FromDeviceLimits = false, StepScope = "abc123" };

        Assert.NotEqual(SafetyMonitor.KeyOf(baseline), SafetyMonitor.KeyOf(recipe));
        Assert.NotEqual(SafetyMonitor.KeyOf(baseline), SafetyMonitor.KeyOf(step));
        Assert.NotEqual(SafetyMonitor.KeyOf(recipe), SafetyMonitor.KeyOf(step));
    }

    [Fact]
    public void 同一步重挂是换值不叠加_撤某步不动别的步()
    {
        var m = new SafetyMonitor(new DataPipeline());
        m.SetStepLimits(1, "s1", new[] { new SafetyLimit(1, "Tr", null, 80, null, TimeSpan.Zero, SafetyAction.Alarm) });
        m.SetStepLimits(1, "s1", new[] { new SafetyLimit(1, "Tr", null, 70, null, TimeSpan.Zero, SafetyAction.Alarm) });
        m.SetStepLimits(1, "s2", new[] { new SafetyLimit(1, "pH", 3, null, null, TimeSpan.Zero, SafetyAction.Alarm) });

        Assert.Equal(2, m.Limits.Count);
        Assert.Equal(70d, m.Limits.Single(l => l.StepScope == "s1").Max);

        Assert.Equal(1, m.RemoveStepLimits(1, "s1"));
        Assert.Equal("s2", Assert.Single(m.Limits).StepScope);

        Assert.Equal(1, m.RemoveStepLimits(1));      // 兜底清场：撤全部步骤层
        Assert.Empty(m.Limits);
    }

    [Fact]
    public void 覆盖段随文件走_全空的不落盘()
    {
        var withGuard = Harness.Mk(CommandSpecs.Control, ("target", 60d));
        withGuard.Guard = new StepGuard { TrMax = 80, PhMin = 3, Action = "停止加料" };
        var empty = Harness.Mk(BuiltinCommands.Wait, ("dur", 5d));
        empty.Guard = new StepGuard();               // 全空 = 没有覆盖

        var r = Harness.RecipeOf("带覆盖", withGuard, empty);
        var back = r.ToDoc().ToModel();

        var g = back.Steps[0].Guard;
        Assert.NotNull(g);
        Assert.Equal(80d, g!.TrMax);
        Assert.Equal(3d, g.PhMin);
        Assert.Equal("停止加料", g.Action);
        Assert.Null(back.Steps[1].Guard);
    }

    [Fact]
    public void 复制步骤带上覆盖_且互不牵连()
    {
        var step = Harness.Mk(BuiltinCommands.Wait, ("dur", 5d));
        step.Guard = new StepGuard { TjMax = 120 };

        var copy = step.Clone();
        Assert.Equal(120d, copy.Guard!.TjMax);

        copy.Guard.TjMax = 90;                       // 改副本不动原件
        Assert.Equal(120d, step.Guard.TjMax);
    }

    [Fact]
    public void 上下限颠倒挡启动_盯不到的信号提醒()
    {
        var bad = Harness.Mk(BuiltinCommands.Wait, ("dur", 5d));
        bad.Guard = new StepGuard { TrMin = 90, TrMax = 60 };
        var blind = Harness.Mk(BuiltinCommands.Wait, ("dur", 5d));
        blind.Guard = new StepGuard { PhMax = 9 };

        var issues = RecipeValidator.Validate(Harness.RecipeOf("查", bad, blind),
            new CommandCatalog(), new Channel(1, "X1", 0));   // 通道上什么能力都没有

        Assert.Contains(issues, i => i.Code == "guard-range" && i.Level == IssueLevel.Error
                                     && i.StepId == bad.StepId);
        Assert.Contains(issues, i => i.Code == "guard-no-signal" && i.Level == IssueLevel.Warning
                                     && i.StepId == blind.StepId);
    }
}
