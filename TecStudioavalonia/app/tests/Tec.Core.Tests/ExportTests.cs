using Tec.Core.Export;
using Tec.Core.Records;
using Xunit;

namespace Tec.Core.Tests;

public class ExportTests
{
    private static async Task<Harness> RunTwoChannelsAsync()
    {
        var h = new Harness(600);
        await h.ReactorChannelAsync(1);
        await h.ReactorChannelAsync(2);
        var recipe = Harness.RecipeOf("导出用",
            Harness.Mk("tec.stir.set", ("转速", 300d)),
            Harness.Mk("tec.flow.wait", ("时长", 120d)));

        h.Engine.StartChannel(1, recipe, "测试员");
        await Task.Delay(150);                 // CH2 晚一点启动：通道各自启动
        h.Engine.StartChannel(2, recipe, "测试员");
        await h.Engine.Runner(1)!.Completion;
        await h.Engine.Runner(2)!.Completion;
        return h;
    }

    [Fact]
    public async Task 执行记录同时给出计划实际与两种偏差()
    {
        await using var h = await RunTwoChannelsAsync();
        var csv = RecordExporter.ExecutionCsv(h.Engine.Record, TimeBase.Channel);

        Assert.Contains("开始偏差", csv);
        Assert.Contains("时长偏差", csv);
        Assert.Contains("CH1", csv);
        Assert.Contains("CH2", csv);
        Assert.Contains("仿真", csv);          // 仿真数据必须标出来源
    }

    [Fact]
    public async Task 长表按每个通道自己的点出不产生空洞()
    {
        await using var h = await RunTwoChannelsAsync();
        var opt = new ExportOptions { TimeBase = TimeBase.Channel, Shape = TableShape.Long };
        var csv = RecordExporter.SamplesLongCsv(h.Pipeline, h.Engine.Record, opt);

        var lines = csv.Split('\n')
            .Where(l => l.StartsWith("CH", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(lines);

        // 长表的每一行都必须是真采到的点：不允许出现空数值单元格
        foreach (var line in lines)
        {
            var cells = line.Split(',');
            Assert.True(cells.Length >= 8, line);
            Assert.False(string.IsNullOrWhiteSpace(cells[5]), "长表出现空数值：" + line);
        }
    }

    [Fact]
    public async Task 宽表按通道基准时每个通道单独出一块()
    {
        await using var h = await RunTwoChannelsAsync();
        var opt = new ExportOptions { TimeBase = TimeBase.Channel, Shape = TableShape.Wide };
        var csv = RecordExporter.SamplesWideCsv(h.Pipeline, h.Engine.Record, opt);

        Assert.Contains("CH1 自身时间基准", csv);
        Assert.Contains("CH2 自身时间基准", csv);
    }

    [Fact]
    public async Task 宽表按墙钟时是一张跨通道对齐的表()
    {
        await using var h = await RunTwoChannelsAsync();
        var opt = new ExportOptions { TimeBase = TimeBase.Wall, Shape = TableShape.Wide };
        var csv = RecordExporter.SamplesWideCsv(h.Pipeline, h.Engine.Record, opt);

        Assert.Contains("墙钟对齐", csv);
        Assert.Contains("CH1·", csv);
        Assert.Contains("CH2·", csv);
    }

    [Fact]
    public async Task 事件记录能导出且带相对通道时间()
    {
        await using var h = await RunTwoChannelsAsync();
        var csv = RecordExporter.EventsCsv(h.Engine.Record, TimeBase.Channel);
        Assert.Contains("相对通道", csv);
        Assert.Contains("ChannelStarted", csv);
    }

    [Fact]
    public void 没有记录时不编数据()
    {
        var record = new RunRecord { CreatedAt = DateTimeOffset.Now };
        var csv = RecordExporter.ExecutionCsv(record, TimeBase.Wall);
        var rows = csv.Split('\n').Where(l => l.StartsWith("CH", StringComparison.Ordinal)).ToList();
        Assert.Empty(rows);
    }
}
