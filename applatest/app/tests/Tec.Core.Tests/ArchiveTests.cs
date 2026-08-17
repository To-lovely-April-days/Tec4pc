using Tec.Core.Data;
using Tec.Core.Export;
using Tec.Core.Records;
using Tec.Driver.Abi;
using Tec.Drivers.Simulator;
using Xunit;

namespace Tec.Core.Tests;

public class ArchiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tec-archive-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }

    private static async Task<Harness> RunAsync()
    {
        var h = new Harness(600);
        await h.ReactorChannelAsync(1);
        await h.ReactorChannelAsync(2);
        var recipe = Harness.RecipeOf("归档用",
            Harness.Mk(CommandSpecs.Control, ("target", 45d), ("rate", 10d)),
            Harness.Mk(CommandSpecs.Hold, ("dur", 1d)));
        h.Engine.NewBatch("归档批次", "王工", "四通道台面");
        h.Engine.StartChannel(1, recipe, "王工");
        await Task.Delay(100);
        h.Engine.StartChannel(2, recipe, "王工");
        await h.Engine.Runner(1)!.Completion;
        await h.Engine.Runner(2)!.Completion;
        return h;
    }

    [Fact]
    public async Task 写下去再读回来记录一条不少()
    {
        await using var h = await RunAsync();
        var rec = h.Engine.Record;
        var archive = new RunArchive(_root, "1.0.0.0");
        archive.Save(rec, h.Pipeline, h.Clock.Now);

        var back = new RunArchive(_root).Load();
        Assert.Single(back);
        var got = back[0].Record;

        Assert.Equal(rec.RunId, got.RunId);
        Assert.Equal(rec.Name, got.Name);
        Assert.Equal(rec.Operator, got.Operator);
        Assert.Equal(rec.BenchName, got.BenchName);
        Assert.Equal(rec.Channels.Count, got.Channels.Count);

        for (var i = 0; i < rec.Channels.Count; i++)
        {
            var a = rec.Channels[i];
            var b = got.Channels[i];
            Assert.Equal(a.Channel, b.Channel);
            Assert.Equal(a.State, b.State);
            Assert.Equal(a.Simulated, b.Simulated);
            Assert.Equal(a.Steps.Count, b.Steps.Count);
            Assert.Equal(a.Events.Count, b.Events.Count);
            Assert.Equal(a.Baseline.Recipe.Name, b.Baseline.Recipe.Name);
            // 冻结的排期原样读回：读回时**不能**拿今天的目录重算，那等于改了基线
            Assert.Equal(a.Baseline.Schedule.Entries.Count, b.Baseline.Schedule.Entries.Count);
            Assert.Equal(a.Baseline.Schedule.Total, b.Baseline.Schedule.Total);

            for (var k = 0; k < a.Steps.Count; k++)
            {
                Assert.Equal(a.Steps[k].Title, b.Steps[k].Title);
                Assert.Equal(a.Steps[k].PlanStart, b.Steps[k].PlanStart);
                Assert.Equal(a.Steps[k].PlanDuration, b.Steps[k].PlanDuration);
                Assert.Equal(a.Steps[k].ActualDuration, b.Steps[k].ActualDuration);
                Assert.Equal(a.Steps[k].Status, b.Steps[k].Status);
                // 偏差是算出来的，两边算出来必须一样——不然报告上的数就变了
                Assert.Equal(a.Steps[k].StartDeviation, b.Steps[k].StartDeviation);
            }
        }
    }

    [Fact]
    public async Task 采样读回来能照原样导出()
    {
        await using var h = await RunAsync();
        var archive = new RunArchive(_root);
        archive.Save(h.Engine.Record, h.Pipeline, h.Clock.Now);

        var back = archive.Load()[0];
        // 归档只写这一炉时间窗内的点：管线是全局的，跑完之后仿真器还在推，
        // 那些点不属于这一炉
        var rec = h.Engine.Record;
        var from = rec.FirstStart!.Value;
        var to = rec.Channels.Max(c => c.FinishedAt!.Value);
        var live = h.Pipeline.Snapshot(1, "Tr")
                             .Where(s => s.WallClock >= from && s.WallClock <= to).ToList();
        var read = back.Samples.Snapshot(1, "Tr");

        Assert.NotEmpty(read);
        Assert.Equal(live.Count, read.Length);
        for (var i = 0; i < read.Length; i++)
        {
            Assert.Equal(live[i].Value, read[i].Value, 9);
            Assert.Equal(live[i].WallClock.UtcTicks, read[i].WallClock.UtcTicks);
            Assert.Equal(live[i].Quality, read[i].Quality);
        }

        // 标签的中文名与单位也要跟着回来，不然导出表头全是 Tr / Tj 这种代号
        var tag = back.Samples.Tag("Tr");
        Assert.NotNull(tag);
        Assert.Equal("℃", tag!.Unit);

        // 归档回来的采样走同一套导出器
        var opt = new ExportOptions();
        opt.Channels.AddRange(back.Record.StartedChannels);
        var csv = RecordExporter.SamplesLongCsv(back.Samples, back.Record, opt);
        Assert.Contains("CH1", csv);
        Assert.Contains("釜内温度", csv);
    }

    [Fact]
    public async Task 同一炉重写不会写出第二份()
    {
        await using var h = await RunAsync();
        var archive = new RunArchive(_root);
        archive.Save(h.Engine.Record, h.Pipeline, h.Clock.Now);
        archive.Save(h.Engine.Record, h.Pipeline, h.Clock.Now);

        Assert.Single(Directory.GetDirectories(_root));
        Assert.Single(new RunArchive(_root).Load());
    }

    [Fact]
    public async Task 记录编号不与归档撞号()
    {
        await using var h = await RunAsync();
        var archive = new RunArchive(_root);
        var at = h.Clock.Now;
        h.Engine.NewBatch("第一炉", "王工", "台面");
        var first = h.Engine.Record.RunId;
        archive.Save(h.Engine.Record, h.Pipeline, at);

        // 模拟「关掉程序再开」：新引擎只知道归档里有哪些编号
        var fresh = new Harness(600);
        await using var _ = fresh;
        fresh.Engine.ReserveRunIds(archive.KnownIds());
        fresh.Engine.NewBatch("下一炉", "王工", "台面");

        Assert.NotEqual(first, fresh.Engine.Record.RunId);
        Assert.StartsWith("EXP-", fresh.Engine.Record.RunId);
    }

    [Fact]
    public void 读不动的归档跳过而不是整张表读不出来()
    {
        Directory.CreateDirectory(Path.Combine(_root, "EXP-20260101-001"));
        File.WriteAllText(Path.Combine(_root, "EXP-20260101-001", "run.json"), "{ 这不是 json");
        Directory.CreateDirectory(Path.Combine(_root, "EXP-20260101-002"));
        File.WriteAllText(Path.Combine(_root, "EXP-20260101-002", "run.json"),
            Tec.Core.Persistence.TecJson.Write(new Tec.Core.Persistence.RunDoc
            { RunId = "EXP-20260101-002", Name = "好的那一份", CreatedAt = DateTimeOffset.Now }));

        var archive = new RunArchive(_root);
        var list = archive.Load();

        Assert.Single(list);
        Assert.Equal("EXP-20260101-002", list[0].Record.RunId);
        // 坏的那一份不能被悄悄吞掉：界面要能照实说「有一份读不回来」
        Assert.Single(archive.Failures);
        Assert.Contains("EXP-20260101-001", archive.Failures[0]);
    }

    [Fact]
    public void 归档目录不存在时读出来是空的不抛异常()
    {
        var list = new RunArchive(Path.Combine(_root, "还没有这个目录")).Load();
        Assert.Empty(list);
    }
}

public class SystemLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tec-log-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void 写进去读得回来且新的在前()
    {
        var t = new DateTimeOffset(2026, 8, 17, 9, 0, 0, TimeSpan.FromHours(8));
        var log = new SystemLog(_dir, () => t);
        Assert.True(log.Write("程序", "启动 v1.0", "王工"));
        t = t.AddMinutes(5);
        Assert.True(log.Write("导出", "导出 2 条到 D:\\exports", "王工"));
        t = t.AddMinutes(1);
        Assert.True(log.Write("归档", "写归档失败：磁盘满", "王工", LogLevel.Error));

        var back = log.Tail();
        Assert.Equal(3, back.Count);
        Assert.Equal("归档", back[0].Category);
        Assert.Equal(LogLevel.Error, back[0].Level);
        Assert.Equal("错误", back[0].LevelWord);
        Assert.Equal("导出", back[1].Category);
        Assert.Equal("王工", back[1].User);
        Assert.Contains("D:\\exports", back[1].Text);
    }

    [Fact]
    public void 制表符与换行不会把一行拆成两条()
    {
        var log = new SystemLog(_dir);
        log.Write("导出", "第一行\n第二行\t带制表符");
        var back = log.Tail();
        Assert.Single(back);
        Assert.Equal("第一行 第二行 带制表符", back[0].Text);
    }

    [Fact]
    public void 跨月换一份文件()
    {
        var t = new DateTimeOffset(2026, 8, 31, 23, 59, 0, TimeSpan.Zero);
        var log = new SystemLog(_dir, () => t);
        log.Write("程序", "八月最后一条");
        t = t.AddMinutes(2);
        log.Write("程序", "九月第一条");

        Assert.Equal(2, Directory.GetFiles(_dir, "system-*.log").Length);
        var back = log.Tail();
        Assert.Equal(2, back.Count);
        Assert.Equal("九月第一条", back[0].Text);
    }

    [Fact]
    public void 写不进去时照实返回失败不掀翻调用方()
    {
        // 拿一个「目录位置上其实是个文件」的路径：建目录必然失败
        var blocker = Path.Combine(Path.GetTempPath(), "tec-log-blocker-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "x");
        try
        {
            var log = new SystemLog(Path.Combine(blocker, "logs"));
            Assert.False(log.Write("导出", "这条写不进去"));
            Assert.False(string.IsNullOrEmpty(log.LastError));
        }
        finally { File.Delete(blocker); }
    }
}
