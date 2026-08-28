using Tec.Core.Records;
using Tec.Driver.Abi;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 断电/崩溃恢复（0255）。运行中每隔一段把活批次整份归档（快照式巡检），
/// 开机时 RepairInterrupted 把归档里还躺着 Running 的炉收尾：置中止、
/// 补结束时刻、追加「断电/崩溃中断」事件，并交回续跑要的信息
/// （跑到第几步、冻结的配方）。这里盯收尾的语义与幂等，不盯计时。
/// </summary>
public class RecoveryTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "tec-recover-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public async Task 中断的炉被收尾_并交回续跑信息_再修一遍没得修()
    {
        await using var h = new Harness(600);
        await h.ReactorChannelAsync(1);
        var recipe = Harness.RecipeOf("结晶-中断",
            Harness.Mk(CommandSpecs.Control, ("target", 45d), ("rate", 10d)),
            Harness.Mk(CommandSpecs.Hold, ("dur", 60d)));
        h.Engine.NewBatch("中断批次", "王工", "台面");
        h.Engine.StartChannel(1, recipe, "王工");

        // 等到第 2 步（保温 60 min）跑起来——第 1 步 2 min 仿真 ≈ 0.2 s 真时
        var run = h.Engine.Record.Of(1)!;
        for (var i = 0; i < 100 && run.Steps.Count(s => s.Status == StepStatus.Done) < 1; i++)
            await Task.Delay(50);
        Assert.Equal(ChannelRunState.Running, run.State);

        // 运行中的巡检快照落盘（Workspace 那只 30 s 定时器干的就是这件事）
        var archive = new RunArchive(_root, "1.0.0.0");
        archive.Save(h.Engine.Record, h.Pipeline, h.Clock.Now);

        // 「断电」：引擎连同容器整个没了，只剩盘上的快照。下一次开机修档
        var fixedUp = new RunArchive(_root).RepairInterrupted();

        var ir = Assert.Single(fixedUp);
        Assert.Equal("中断批次", ir.Name);
        var chInfo = Assert.Single(ir.Channels);
        Assert.Equal(1, chInfo.Channel);
        Assert.Equal(1, chInfo.DoneSteps);          // 控温跑完了，保温正跑着
        Assert.Equal(2, chInfo.TotalSteps);
        Assert.Single(chInfo.DoneStepIds);
        Assert.NotNull(chInfo.Recipe);              // 冻结基线读得回来，装回续跑靠它
        Assert.Equal(2, chInfo.Recipe!.Steps.Count);

        // 修完的档案：通道中止、有结束时刻、事件里写明是断电/崩溃收的尾
        var back = Assert.Single(new RunArchive(_root).Load());
        var chr = Assert.Single(back.Record.Channels);
        Assert.Equal(ChannelRunState.Aborted, chr.State);
        Assert.NotNull(chr.FinishedAt);
        Assert.Contains(chr.Events, e => e.Kind == EventKind.Aborted && e.Text.Contains("断电"));
        // 正在跑的那步照实标中止，不算跑完
        Assert.Contains(chr.Steps, s => s.Status == StepStatus.Aborted);
        Assert.Equal(1, chr.Steps.Count(s => s.Status == StepStatus.Done));

        // 幂等：收过尾的炉不再上报——不然每次开机都提醒同一炉
        Assert.Empty(new RunArchive(_root).RepairInterrupted());
    }

    [Fact]
    public async Task 善终的炉不动()
    {
        await using var h = new Harness(600);
        await h.ReactorChannelAsync(1);
        var recipe = Harness.RecipeOf("善终",
            Harness.Mk(CommandSpecs.Control, ("target", 45d), ("rate", 10d)),
            Harness.Mk(CommandSpecs.Hold, ("dur", 1d)));
        h.Engine.NewBatch("善终批次", "王工", "台面");
        h.Engine.StartChannel(1, recipe, "王工");
        await h.Engine.Runner(1)!.Completion;

        var archive = new RunArchive(_root, "1.0.0.0");
        archive.Save(h.Engine.Record, h.Pipeline, h.Clock.Now);

        Assert.Empty(new RunArchive(_root).RepairInterrupted());
        var back = Assert.Single(new RunArchive(_root).Load());
        Assert.Equal(ChannelRunState.Completed, Assert.Single(back.Record.Channels).State);
    }
}
