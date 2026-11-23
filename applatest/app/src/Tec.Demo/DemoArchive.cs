using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Tec.Core.Catalog;
using Tec.Core.Persistence;
using Tec.Core.Recipes;
using Tec.Core.Records;
using Tec.Core.Scheduling;
using Tec.Driver.Abi;

namespace Tec.Demo;

/// <summary>
/// 【本地改动】演示用的历史归档：四炉，写成 RunArchive 的目录格式（run.json + tags.json + samples.tsv.gz）。
///
/// WHY：数据导出页要有几炉可选、报告预览要能出图。采样曲线**不是编的**——取自 2026-11 现场控温测试的记录
/// （资料管理 / 控温测试 / R1-tc1-*.csv，按 10 s 重采样，见 Data/curves/），釜内 / 夹套 / 设定 / 出力四路都是实测，
/// 所以采样质量照实写 Good、通道不标仿真。这几炉的「配方」按测试里实际下发的设定（釜内 X ℃ 尽快 → 保持）还原成
/// 控温 + 恒温保持两步一段，排期用程序自己的 Schedule.Build 算——跟真跑一炉写出来的档案是同一个格式、同一套字段。
/// 记录编号沿用程序的 EXP-yyyyMMdd-nnn，开机时 ReserveRunIds 会避开它们。
/// </summary>
public static class DemoArchive
{
    private sealed class RunManifest
    {
        public string RunId { get; set; } = "";
        public string Name { get; set; } = "";
        public List<Segment> Segments { get; set; } = new();
        public int Points { get; set; }
    }

    private sealed class Segment
    {
        public double Sp { get; set; }
        public DateTimeOffset Start { get; set; }
        public DateTimeOffset Reached { get; set; }
        public DateTimeOffset End { get; set; }
        public double Tr0 { get; set; }
    }

    public const string Operator = "王工";

    /// <summary>写进 root（归档目录）。返回写了几炉。已经有同号目录的跳过。</summary>
    public static int Write(string root, ICommandCatalog catalog, string appVersion = "")
    {
        Directory.CreateDirectory(root);
        var runs = JsonSerializer.Deserialize<List<RunManifest>>(DemoCompounds.ReadText("Tec.Demo.Data.curves.runs.json"),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        var n = 0;
        foreach (var run in runs)
        {
            var dir = Path.Combine(root, run.RunId);
            if (File.Exists(Path.Combine(dir, "run.json"))) continue;
            Directory.CreateDirectory(dir);
            WriteOne(dir, run, catalog, appVersion);
            n++;
        }
        return n;
    }

    private static void WriteOne(string dir, RunManifest run, ICommandCatalog catalog, string appVersion)
    {
        const int channel = 1;
        var started = run.Segments[0].Start;
        var finished = run.Segments[^1].End;

        // 配方：每段「控温 釜内 X ℃ 尽快」+「恒温保持」。保持时长按实测的到温 → 段尾
        var recipe = new Recipe { Id = "demo-" + run.RunId.ToLowerInvariant(), Name = run.Name, Author = Operator, ModifiedAt = started.AddMinutes(-20) };
        var steps = new List<(Step Step, DateTimeOffset Start, DateTimeOffset End, EndReason Reason)>();
        foreach (var seg in run.Segments)
        {
            var ctl = new Step
            {
                CommandId = CommandSpecs.Control, Phase = seg.Sp > seg.Tr0 ? "升温" : "降温",
                Parameters = ParameterSet.Of(("target", seg.Sp), ("task", "尽快"), ("obj", "釜内 Tr"), ("tol", 0.3d), ("wait", true), ("settle", 60d))
            };
            var holdMin = Math.Max(1, Math.Round((seg.End - seg.Reached).TotalMinutes));
            var hold = new Step
            {
                CommandId = CommandSpecs.Hold, Phase = "保温",
                Parameters = ParameterSet.Of(("dur", holdMin), ("tol", 0.1d), ("obj", "釜内 Tr"))
            };
            recipe.Steps.Add(ctl);
            recipe.Steps.Add(hold);
            steps.Add((ctl, seg.Start, seg.Reached, EndReason.Reached));
            steps.Add((hold, seg.Reached, seg.End, EndReason.TimerElapsed));
        }

        var seed = new EstimationContext { Temperature = run.Segments[0].Tr0, Jacket = run.Segments[0].Tr0 };
        var sched = Schedule.Build(recipe, catalog, seed);

        var ch = new ChannelRunDoc
        {
            Channel = channel, StartedAt = started, FinishedAt = finished, State = ChannelRunState.Completed,
            Operator = Operator, Simulated = false,
            Baseline = new BaselineDoc
            {
                Recipe = recipe.ToDoc(), FrozenAt = started, ApprovedBy = Operator,
                Schedule = sched.Entries.Select(e => new SchedEntryDoc
                {
                    Index = e.Index, StepId = e.StepId, CommandId = e.CommandId, Start = e.Start, Duration = e.Duration,
                    Span = e.Span, Repeats = e.Repeats, Title = e.Title, Termination = e.Termination, Known = e.Known, StartTemp = e.StartTemp
                }).ToList()
            }
        };
        for (var i = 0; i < steps.Count; i++)
        {
            var (step, s0, s1, reason) = steps[i];
            var plan = sched.Entries.FirstOrDefault(e => e.StepId == step.StepId);
            ch.Steps.Add(new StepRecordDoc
            {
                Index = i, StepId = step.StepId, CommandId = step.CommandId,
                Title = plan?.Title ?? step.CommandId, Termination = plan?.Termination ?? TerminationKind.Timer,
                PlanStart = plan?.Start ?? TimeSpan.Zero, PlanDuration = plan?.Duration ?? TimeSpan.Zero,
                ActualStart = s0, ActualEnd = s1, ActualDuration = s1 - s0, Reason = reason, Status = StepStatus.Done,
                ChannelStart = started, ControlMode = "釜内 Tr（串级）", Phase = step.Phase
            });
        }
        ch.Events.Add(new EventRecordDoc { At = started, Channel = channel, Kind = EventKind.ChannelStarted, Text = $"通道启动：{run.Name}", User = Operator });
        foreach (var seg in run.Segments)
            ch.Events.Add(new EventRecordDoc { At = seg.Reached, Channel = channel, Kind = EventKind.OperatorMark,
                                               Text = $"釜内到达 {seg.Sp:0.#} ℃（±0.3 ℃），进入保温", User = Operator });
        ch.Events.Add(new EventRecordDoc { At = finished, Channel = channel, Kind = EventKind.ChannelFinished, Text = "配方执行完毕", User = Operator });

        var doc = new RunDoc
        {
            RunId = run.RunId, Name = run.Name, Operator = Operator, CreatedAt = started.AddMinutes(-3), ClosedAt = finished.AddMinutes(1),
            BenchName = DemoBench.Name, ArchivedAt = finished.AddMinutes(2), AppVersion = appVersion
        };
        doc.Channels.Add(ch);
        File.WriteAllText(Path.Combine(dir, "run.json"), TecJson.Write(doc), new UTF8Encoding(false));

        var tags = new List<TagDoc>
        {
            new() { Tag = "Tr", DisplayName = "釜内温度", Unit = "℃", Shape = DataShape.Scalar, Period = TimeSpan.FromSeconds(10) },
            new() { Tag = "Tj", DisplayName = "夹套温度", Unit = "℃", Shape = DataShape.Scalar, Period = TimeSpan.FromSeconds(10) },
            new() { Tag = "Tset", DisplayName = "设定温度", Unit = "℃", Shape = DataShape.Scalar, Period = TimeSpan.FromSeconds(10) },
            new() { Tag = "duty", DisplayName = "控温输出", Unit = "%", Shape = DataShape.Scalar, Period = TimeSpan.FromSeconds(10) },
            new() { Tag = "dT", DisplayName = "Tr−Tj", Unit = "℃", Shape = DataShape.Scalar, Period = TimeSpan.FromSeconds(10) }
        };
        File.WriteAllText(Path.Combine(dir, "tags.json"), TecJson.Write(tags), new UTF8Encoding(false));

        // 采样：与 RunArchive.Save 同一个版式（ch \t tag \t wall \t mono \t value \t quality）
        var csv = DemoCompounds.ReadText($"Tec.Demo.Data.curves.{run.RunId}.csv");
        var series = new Dictionary<string, List<(long Wall, double V)>> { ["Tr"] = new(), ["Tj"] = new(), ["Tset"] = new(), ["duty"] = new(), ["dT"] = new() };
        foreach (var line in csv.Split('\n').Skip(1))
        {
            var f = line.TrimEnd('\r').Split(',');
            if (f.Length < 5) continue;
            if (!DateTimeOffset.TryParse(f[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) continue;
            var tr = double.Parse(f[1], CultureInfo.InvariantCulture);
            var tj = double.Parse(f[2], CultureInfo.InvariantCulture);
            var sp = double.Parse(f[3], CultureInfo.InvariantCulture);
            var duty = double.Parse(f[4], CultureInfo.InvariantCulture);
            var w = t.UtcTicks;
            series["Tr"].Add((w, tr)); series["Tj"].Add((w, tj)); series["Tset"].Add((w, sp));
            series["duty"].Add((w, duty)); series["dT"].Add((w, Math.Round(tr - tj, 2)));
        }
        var tmp = Path.Combine(dir, "samples.tsv.gz.tmp");
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
        using (var wr = new StreamWriter(gz, new UTF8Encoding(false)))
        {
            wr.WriteLine("# TecStudio 采样归档 v1\tch\ttag\twall(ticks)\tmono(ticks)\tvalue\tquality");
            foreach (var (tag, pts) in series.OrderBy(k => k.Key, StringComparer.Ordinal))
                foreach (var (wall, v) in pts)
                {
                    wr.Write(channel); wr.Write('\t'); wr.Write(tag); wr.Write('\t');
                    wr.Write(wall.ToString(CultureInfo.InvariantCulture)); wr.Write('\t');
                    wr.Write(wall.ToString(CultureInfo.InvariantCulture)); wr.Write('\t');
                    wr.Write(v.ToString("R", CultureInfo.InvariantCulture)); wr.Write('\t');
                    wr.Write((int)Quality.Good); wr.Write('\n');
                }
        }
        File.Move(tmp, Path.Combine(dir, "samples.tsv.gz"), overwrite: true);
    }
}
