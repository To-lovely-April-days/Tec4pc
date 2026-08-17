using Tec.Core.Export;
using Tec.Core.Records;
using Tec.Driver.Abi;
using Tec.Drivers.Simulator;
using Xunit;

namespace Tec.Core.Tests;

public class DutyAndPhaseTests
{
    private static async Task<Harness> RunAsync(string? phase)
    {
        var h = new Harness(600);
        await h.ReactorChannelAsync(1);
        var heat = Harness.Mk(CommandSpecs.Control, ("target", 70d), ("rate", 12d), ("obj", "夹套 Tj"));
        heat.Phase = phase;
        var hold = Harness.Mk(CommandSpecs.Hold, ("dur", 1d), ("obj", "釜内 Tr"));
        hold.Phase = phase is null ? null : "保温";
        var recipe = Harness.RecipeOf("出力用", heat, hold);

        h.Engine.StartChannel(1, recipe, "王工");
        await h.Engine.Runner(1)!.Completion;
        return h;
    }

    [Fact]
    public async Task 控温输出真的采到了且带正负号()
    {
        await using var h = await RunAsync(null);
        var duty = h.Pipeline.Snapshot(1, "duty");

        Assert.NotEmpty(duty);
        // 升温段执行器在加热方向使劲：至少有一段是正的
        Assert.Contains(duty, s => s.Value > 5);
        // 出力是占比，不该越界
        Assert.All(duty, s => Assert.InRange(s.Value, -100, 100));

        var tag = h.Pipeline.Tag("duty");
        Assert.NotNull(tag);
        Assert.Equal("%", tag!.Unit);
    }

    [Fact]
    public async Task 切了加热输出之后出力归零而不是留着上一个值()
    {
        var h = new Harness(600);
        await using var _ = h;
        await h.ReactorChannelAsync(1);
        h.Engine.StartChannel(1, Harness.RecipeOf("出力用",
            Harness.Mk(CommandSpecs.Control, ("target", 90d), ("rate", 12d))), "王工");

        // 等它真的开始使劲
        await Task.Delay(300);
        Assert.Contains(h.Pipeline.Snapshot(1, "duty"), x => Math.Abs(x.Value) > 5);

        // 中止 → 收安全态会切加热。**配方跑完并不会切**：温控器还守着最后那个设定值，
        // 那是真实行为，不是 bug（见 Rd105ReactorDriver.SafeStop 的说明）
        h.Engine.Runner(1)!.Abort("王工", "自测");
        await h.Engine.Runner(1)!.Completion;
        await Task.Delay(300);

        var duty = h.Pipeline.Snapshot(1, "duty");
        Assert.NotEmpty(duty);
        Assert.Equal(0, duty[^1].Value, 3);
    }

    [Fact]
    public async Task 控温对象跟着步骤冻进记录()
    {
        await using var h = await RunAsync(null);
        var steps = h.Engine.Record.Channels[0].Steps;

        Assert.Equal(2, steps.Count);
        Assert.Equal("夹套 Tj", steps[0].ControlMode);
        Assert.Equal("釜内 Tr", steps[1].ControlMode);
    }

    [Fact]
    public async Task 没标工艺阶段就是空的不编一个()
    {
        await using var h = await RunAsync(null);
        Assert.All(h.Engine.Record.Channels[0].Steps, s => Assert.Null(s.Phase));

        // 报告里那一列全空，就该被摘掉——A4 竖排的宽度是抢出来的
        var steps = StepTable(Report(h));
        Assert.DoesNotContain(steps.Columns, c => c.Name == "阶段");
        Assert.Contains(steps.Columns, c => c.Name == "控温");
    }

    [Fact]
    public async Task 标了工艺阶段就一路带到记录与报告()
    {
        await using var h = await RunAsync("升温");
        var steps = h.Engine.Record.Channels[0].Steps;
        Assert.Equal("升温", steps[0].Phase);
        Assert.Equal("保温", steps[1].Phase);

        var doc = Report(h);
        var table = StepTable(doc);
        var phaseCol = table.Columns.ToList().FindIndex(c => c.Name == "阶段");
        Assert.True(phaseCol >= 0, "标了阶段，报告里却没有这一列");
        Assert.Equal("升温", table.Rows[0][phaseCol]);

        // 报告里得写清楚这一列是人标的，不是设备回报的
        var notes = doc.Blocks.OfType<ParaBlock>().Select(p => p.Text);
        Assert.Contains(notes, t => t.Contains("操作人在配方里标注", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 执行记录CSV带上控温对象与工艺阶段()
    {
        await using var h = await RunAsync("结晶");
        var csv = RecordExporter.ExecutionCsv(h.Engine.Record, TimeBase.Wall, h.Catalog);

        Assert.Contains("工艺阶段", csv);
        Assert.Contains("控温对象", csv);
        Assert.Contains("夹套 Tj", csv);
        Assert.Contains("结晶", csv);
    }

    [Fact]
    public async Task 归档读回来控温对象与阶段一个不少()
    {
        await using var h = await RunAsync("蒸馏");
        var root = Path.Combine(Path.GetTempPath(), "tec-phase-" + Guid.NewGuid().ToString("N"));
        try
        {
            var archive = new RunArchive(root);
            archive.Save(h.Engine.Record, h.Pipeline, h.Clock.Now);
            var back = archive.Load()[0].Record.Channels[0].Steps;

            Assert.Equal("蒸馏", back[0].Phase);
            Assert.Equal("夹套 Tj", back[0].ControlMode);
            Assert.Equal("保温", back[1].Phase);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    /// <summary>步骤表。**不能按「步骤」这个列名找**——概要页的通道一览里也有一列叫「步骤」（步数）。</summary>
    private static TableBlock StepTable(ReportDoc doc)
        => doc.Blocks.OfType<TableBlock>().First(t => t.Columns.Any(c => c.Name == "结束原因"));

    private static ReportDoc Report(Harness h)
        => ReportBuilder.Build(h.Engine.Record, h.Pipeline, h.Catalog, new ExportMeta
        {
            Experiment = "出力自测",
            Operator = "王工",
            ExportedAt = new DateTimeOffset(2026, 8, 17, 12, 0, 0, TimeSpan.Zero)
        }, new ReportOptions { Trend = false });
}
