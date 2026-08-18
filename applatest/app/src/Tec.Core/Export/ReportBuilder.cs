using System.Globalization;
using Tec.Core.Catalog;
using Tec.Core.Chemistry;
using Tec.Core.Data;
using Tec.Core.Records;
using Tec.Driver.Abi;

namespace Tec.Core.Export;

/// <summary>报告里放哪几节。对应右栏「报告内容」那几个勾。</summary>
public sealed class ReportOptions
{
    public ReportTemplate Template { get; set; } = ReportTemplate.Full;
    public bool Trend { get; set; } = true;
    public bool Steps { get; set; } = true;
    public bool Alarms { get; set; } = true;
    public bool RecipeAndBench { get; set; } = true;
    public bool Chemicals { get; set; }
    /// <summary>配料表与化学计量那一节。</summary>
    public bool Charge { get; set; } = true;
    /// <summary>算配料要用的化合物库。界面层传进来——Core 不去碰数据库。</summary>
    public IReadOnlyList<Tec.Core.Compounds.Compound> Library { get; set; }
        = Array.Empty<Tec.Core.Compounds.Compound>();
    /// <summary>GLP 那三个勾。</summary>
    public bool Audit { get; set; } = true;
    public bool Checksum { get; set; } = true;
    public bool Signature { get; set; } = true;
    /// <summary>趋势图，界面层画好传进来（Core 不认识 Avalonia）。一路一张。</summary>
    public List<ImageBlock> Charts { get; } = new();
    /// <summary>本次导出一起写出的文件与它们的校验码。空 = 这份报告不附校验码。</summary>
    public List<(string Name, string Sha)> Files { get; } = new();
    /// <summary>化合物物性，界面层从化合物库里挑好传进来。</summary>
    public List<(string Name, string Cas, string Props)> Compounds { get; } = new();
}

/// <summary>
/// 章号。**按实际排进去的节数往下数**，不是每处写死一个。
///
/// 写死的后果是：把「步骤执行记录」那个勾取消掉，报告目录就从「二」直接跳到「四」——
/// 中间那一章去哪了，读报告的人无从得知，只能怀疑自己拿到的是残页。
/// </summary>
internal sealed class Chapters
{
    private static readonly string[] Cn = { "一", "二", "三", "四", "五", "六", "七", "八", "九", "十" };
    private int _n;

    public string Next(string title)
    {
        _n++;
        var no = _n <= Cn.Length ? Cn[_n - 1] : _n.ToString(CultureInfo.InvariantCulture);
        return no + "、" + title;
    }
}

/// <summary>
/// 一炉 + 一套设置 → 一份报告的内容。
///
/// Word 与 PDF 读同一份，所以措辞只写一遍。**内容全部由记录换算**：
/// 报告里出现的每个数都能在 steps.csv / data.csv 里找到出处，没有一句是模板凑的。
/// </summary>
public static class ReportBuilder
{
    public static ReportDoc Build(RunRecord rec, ISampleSource samples, CommandCatalog catalog,
                                  ExportMeta meta, ReportOptions opt)
    {
        var simulated = rec.Channels.Any(c => c.Simulated);
        var doc = new ReportDoc
        {
            Title = string.IsNullOrWhiteSpace(meta.Experiment) ? rec.Name : meta.Experiment,
            Subtitle = ReportTemplates.NameOf(opt.Template) + "　·　" + rec.RunId,
            Template = opt.Template,
            Simulated = simulated
        };
        doc.FooterLeft = $"{doc.Title}　·　{rec.RunId}";

        var t0 = rec.FirstStart ?? rec.CreatedAt;
        var end = rec.Channels.Count == 0 ? t0 : rec.Channels.Max(c => c.FinishedAt ?? meta.ExportedAt);

        doc.Cover.Add(("记录编号", rec.RunId));
        doc.Cover.Add(("批次名称", rec.Name));
        doc.Cover.Add(("台面", string.IsNullOrEmpty(rec.BenchName) ? meta.BenchName : rec.BenchName));
        doc.Cover.Add(("操作人", rec.Operator ?? meta.Operator));
        doc.Cover.Add(("开始时刻", Stamp(t0)));
        doc.Cover.Add(("结束时刻", rec.Channels.All(c => c.FinishedAt is not null) ? Stamp(end) : "仍在运行"));
        doc.Cover.Add(("总时长", Fmt.Hms(end - t0)));
        doc.Cover.Add(("通道", rec.StartedChannels.Count == 0
            ? "未启动任何通道"
            : string.Join(" / ", rec.StartedChannels.Select(c => "CH" + c))));
        doc.Cover.Add(("导出时刻", Stamp(meta.ExportedAt)));
        doc.Cover.Add(("导出人", meta.Operator));
        if (meta.Signer.Length > 0 && opt.Signature) doc.Cover.Add(("签名人", meta.Signer));
        if (meta.Archived) doc.Cover.Add(("数据来源", "归档记录"));

        var no = new Chapters();
        Summary(doc, rec, samples, meta, t0, end, no);

        // 配料表排在概要后面：这一炉「投了什么、投了多少」是读报告的人问的第一件事，
        // 而且它是配方参数的前提——先知道加的是什么，那些体积才读得懂
        if (opt.Charge) Charges(doc, rec, opt, no);

        if (opt.Trend && opt.Charts.Count > 0)
        {
            doc.Blocks.Add(new HeadingBlock { Level = 1, Text = no.Next("趋势曲线") });
            foreach (var chart in opt.Charts) doc.Blocks.Add(chart);
        }
        else if (opt.Trend)
        {
            doc.Blocks.Add(new HeadingBlock { Level = 1, Text = no.Next("趋势曲线") });
            doc.Blocks.Add(new ParaBlock
            {
                Muted = true,
                Text = "这一炉在导出时没有可画的采样数据——采样已不在缓冲区内，或本次未勾选任何数值数据项。"
            });
        }

        if (opt.Template == ReportTemplate.Summary)
        {
            Deviations(doc, rec, catalog, no);
            Sign(doc, rec, meta, opt);
            return doc;
        }

        if (opt.Steps) Steps(doc, rec, catalog, no);
        if (opt.Alarms) Events(doc, rec, opt.Template == ReportTemplate.Glp || opt.Audit, no);
        if (opt.RecipeAndBench) Recipes(doc, rec, catalog, no);
        if (opt.Chemicals && opt.Compounds.Count > 0) Compounds(doc, opt, no);
        if (opt.Template == ReportTemplate.Glp) Glp(doc, rec, meta, opt);
        Sign(doc, rec, meta, opt);
        return doc;
    }

    // ── 一、实验概要 ────────────────────────────────────────────────

    private static void Summary(ReportDoc doc, RunRecord rec, ISampleSource samples,
                                ExportMeta meta, DateTimeOffset t0, DateTimeOffset end, Chapters no)
    {
        doc.Blocks.Add(new PageBreakBlock());
        doc.Blocks.Add(new HeadingBlock { Level = 1, Text = no.Next("实验概要") });

        if (meta.SamplesTruncated)
            doc.Blocks.Add(new NoticeBlock
            {
                Bad = true,
                Text = "归档时早期采样已被环形缓冲顶掉，本报告的曲线与统计只覆盖仍保留的时段。"
            });

        var aborted = rec.Channels.Where(c => c.State is ChannelRunState.Aborted or ChannelRunState.Faulted).ToList();
        if (aborted.Count > 0)
            doc.Blocks.Add(new NoticeBlock
            {
                Bad = true,
                Text = string.Join(" / ", aborted.Select(c => "CH" + c.Channel)) + " 未正常结束，步骤记录不完整。"
            });

        var rows = new List<string[]>();
        var bad = new List<int>();
        foreach (var ch in rec.Channels.OrderBy(c => c.Channel))
        {
            long pts = 0;
            double? min = null, max = null;
            foreach (var s in samples.Snapshot(ch.Channel, "Tr"))
            {
                if (s.WallClock < ch.StartedAt || ch.FinishedAt is { } f && s.WallClock > f) continue;
                pts++;
                min = min is null ? s.Value : Math.Min(min.Value, s.Value);
                max = max is null ? s.Value : Math.Max(max.Value, s.Value);
            }
            var alarms = ch.Events.Count(e => e.Kind == EventKind.Alarm);
            if (ch.State is ChannelRunState.Aborted or ChannelRunState.Faulted || alarms > 0)
                bad.Add(rows.Count);
            rows.Add(new[]
            {
                "CH" + ch.Channel,
                ch.Baseline.Recipe.Name,
                Clock(ch.StartedAt),
                ch.FinishedAt is { } f2 ? Fmt.Hms(f2 - ch.StartedAt) : "运行中",
                RunStateWords.Of(ch.State),
                ch.Steps.Count.ToString(CultureInfo.InvariantCulture),
                min is null ? "—" : $"{min:F1} ~ {max:F1}",
                pts.ToString("N0", CultureInfo.InvariantCulture),
                alarms == 0 ? "—" : alarms.ToString(CultureInfo.InvariantCulture),
                ch.Simulated ? "仿真" : "实测"
            });
        }

        doc.Blocks.Add(new TableBlock
        {
            Title = "通道一览",
            Columns = new[]
            {
                new TableCol { Name = "通道", Weight = 0.62 },
                new TableCol { Name = "配方", Weight = 1.9 },
                new TableCol { Name = "启动", Weight = 0.95, Right = true },
                new TableCol { Name = "时长", Weight = 0.9, Right = true },
                new TableCol { Name = "状态", Weight = 0.8 },
                new TableCol { Name = "步骤", Weight = 0.55, Right = true },
                new TableCol { Name = "釜温范围 ℃", Weight = 1.15, Right = true },
                new TableCol { Name = "采样点", Weight = 0.85, Right = true },
                new TableCol { Name = "报警", Weight = 0.6, Right = true },
                new TableCol { Name = "来源", Weight = 0.62 }
            },
            Rows = rows,
            BadRows = bad,
            Note = "釜温范围取该通道时段内 Tr 的实测最小 / 最大值；采样点是导出时管线中仍保留的点数，不是按时长估算。"
        });

        doc.Blocks.Add(new KvBlock
        {
            Title = "导出设置",
            Pairs = new List<(string, string)>
            {
                ("采样间隔", meta.Interval),
                ("时间基准", meta.TimeBaseText),
                ("导出目录", meta.TargetDir),
                ("程序版本", meta.AppVersion.Length > 0 ? "TecStudio " + meta.AppVersion : "TecStudio")
            }
        });
    }

    // ── 超差步骤（摘要模板专用）────────────────────────────────────

    private static void Deviations(ReportDoc doc, RunRecord rec, CommandCatalog catalog, Chapters no)
    {
        doc.Blocks.Add(new HeadingBlock { Level = 1, Text = no.Next("超差与异常") });

        var rows = new List<string[]>();
        foreach (var ch in rec.Channels.OrderBy(c => c.Channel))
            foreach (var s in ch.Steps)
            {
                var outOf = s.DurationOutOfTolerance();
                if (!outOf && s.Status is not (StepStatus.Aborted or StepStatus.Failed or StepStatus.Skipped)) continue;
                rows.Add(new[]
                {
                    "CH" + ch.Channel,
                    (s.Index + 1).ToString(CultureInfo.InvariantCulture),
                    s.Title,
                    s.PlanDuration > TimeSpan.Zero ? Fmt.Hms(s.PlanDuration) : "—",
                    s.ActualDuration is { } d ? Fmt.Hms(d) : "—",
                    s.DurationDeviation is { } dd ? Fmt.Signed(dd) : "—",
                    StatusWords.Of(s.Status),
                    EndBy.Text(s, catalog)
                });
            }

        doc.Blocks.Add(new TableBlock
        {
            Columns = new[]
            {
                new TableCol { Name = "通道", Weight = 0.6 },
                new TableCol { Name = "#", Weight = 0.4, Right = true },
                new TableCol { Name = "步骤", Weight = 3 },
                new TableCol { Name = "计划时长", Weight = 0.95, Right = true },
                new TableCol { Name = "实际时长", Weight = 0.95, Right = true },
                new TableCol { Name = "偏差", Weight = 0.9, Right = true },
                new TableCol { Name = "状态", Weight = 0.7 },
                new TableCol { Name = "结束原因", Weight = 1.3 }
            },
            Rows = rows,
            MaxLines = 2,
            BadRows = Enumerable.Range(0, rows.Count).ToList(),
            Note = rows.Count == 0
                ? "没有超差或异常结束的步骤。判据：时长偏差同时超过计划的 10% 与 20 秒。"
                : "判据：时长偏差同时超过计划的 10% 与 20 秒；另含中止 / 失败 / 跳过的步骤。完整记录见随附的 steps.csv。"
        });
    }

    // ── 步骤执行记录 ────────────────────────────────────────────────

    private static void Steps(ReportDoc doc, RunRecord rec, CommandCatalog catalog, Chapters no)
    {
        doc.Blocks.Add(new HeadingBlock { Level = 1, Text = no.Next("步骤执行记录") });
        var phased = rec.Channels.Any(c => c.Steps.Any(s => !string.IsNullOrEmpty(s.Phase)));
        doc.Blocks.Add(new ParaBlock
        {
            Muted = true,
            Text = "「计划」来自启动那一刻冻结的基线，之后再改配方也不影响它；"
                 + "「实际」来自运行记录。开始偏差 = 被前面的步骤拖累了多少，时长偏差 = 这一步自己跑得对不对，两者分列。"
                 + "「控温」是这一步按哪一路控（Tr 釜内 / Tj 夹套），由设备决定——"
                 + "同一条 Tr−Tj 曲线，按 Tj 控时反映的是热效应，按 Tr 控时反映的是控制器在使劲，读法完全不同。"
                 + (phased ? "「阶段」是操作人在配方里标注的工艺阶段，不是设备回报的。" : "")
        });

        foreach (var ch in rec.Channels.OrderBy(c => c.Channel))
        {
            var rows = new List<string[]>();
            var bad = new List<int>();
            foreach (var s in ch.Steps)
            {
                if (s.Status is StepStatus.Aborted or StepStatus.Failed || s.DurationOutOfTolerance())
                    bad.Add(rows.Count);
                rows.Add(new[]
                {
                    (s.Index + 1).ToString(CultureInfo.InvariantCulture),
                    s.Iteration > 1 ? s.Iteration.ToString(CultureInfo.InvariantCulture) : "",
                    s.Phase ?? "",
                    Short(s.ControlMode),
                    s.Title,
                    Clock(ch.StartedAt + s.PlanStart),
                    s.ActualStart is { } a ? Clock(a) : "—",
                    s.StartDeviation is { } sd ? Fmt.Signed(sd) : "—",
                    s.PlanDuration > TimeSpan.Zero ? Fmt.Hms(s.PlanDuration) : "—",
                    s.ActualDuration is { } ad ? Fmt.Hms(ad) : "—",
                    s.DurationDeviation is { } dd ? Fmt.Signed(dd) : "—",
                    EndBy.Text(s, catalog),
                    StatusWords.Of(s.Status)
                });
            }

            doc.Blocks.Add(new TableBlock
            {
                Title = $"CH{ch.Channel}　·　{ch.Baseline.Recipe.Name}"
                        + (ch.Simulated ? "　（仿真运行）" : ""),
                Columns = Trim(ref rows, new[]
                {
                    // 多了「阶段」「控温」两列之后重新分过宽度：时刻列一旦被掐成
                    // 「13:32:…」，这张表就没法拿来对时间了——它存在的理由就是对时间
                    new TableCol { Name = "#", Weight = 0.32, Right = true },
                    new TableCol { Name = "轮", Weight = 0.28, Right = true },
                    new TableCol { Name = "阶段", Weight = 0.7 },
                    new TableCol { Name = "控温", Weight = 0.5 },
                    new TableCol { Name = "步骤", Weight = 1.45 },
                    new TableCol { Name = "计划开始", Weight = 1.15, Right = true },
                    new TableCol { Name = "实际开始", Weight = 1.15, Right = true },
                    new TableCol { Name = "开始偏差", Weight = 1, Right = true },
                    new TableCol { Name = "计划时长", Weight = 1, Right = true },
                    new TableCol { Name = "实际时长", Weight = 1, Right = true },
                    new TableCol { Name = "时长偏差", Weight = 1, Right = true },
                    new TableCol { Name = "结束原因", Weight = 1.1 },
                    new TableCol { Name = "状态", Weight = 0.6 }
                }),
                Rows = rows,
                BadRows = bad,
                // 步骤那一列写的是整句参数（「保持当前温度 2 min，允差 ±0 ℃」），
                // 放不下就往下折——那一列正是人要看的东西，掐掉等于没写。
                // 十三列挤在 A4 竖排上，宽度得先保证数字一个不掐（那是这张表存在的理由），
                // 剩下的给这一列，折三行也比掐掉强
                MaxLines = 3
            });
        }
    }

    // ── 事件与报警 ──────────────────────────────────────────────────

    private static void Events(ReportDoc doc, RunRecord rec, bool full, Chapters no)
    {
        doc.Blocks.Add(new HeadingBlock { Level = 1, Text = no.Next("事件与报警") });

        var all = rec.Channels
            .SelectMany(c => c.Events.Select(e => (Ch: c.Channel, E: e)))
            .OrderBy(x => x.E.At)
            .ToList();

        if (!full)
            all = all.Where(x => x.E.Kind is EventKind.Alarm or EventKind.SafetyAction or EventKind.AlarmAck
                                            or EventKind.AlarmCleared or EventKind.Aborted or EventKind.DeviceFault
                                            or EventKind.OperatorMark).ToList();

        var rows = new List<string[]>();
        var bad = new List<int>();
        foreach (var (ch, e) in all)
        {
            if (e.Kind is EventKind.Alarm or EventKind.SafetyAction or EventKind.DeviceFault or EventKind.Aborted)
                bad.Add(rows.Count);
            rows.Add(new[]
            {
                "CH" + ch, Clock(e.At), EventWords.Of(e.Kind), e.Text,
                e.Before ?? "", e.After ?? "", e.User ?? ""
            });
        }

        doc.Blocks.Add(new TableBlock
        {
            Columns = new[]
            {
                new TableCol { Name = "通道", Weight = 0.6 },
                new TableCol { Name = "时刻", Weight = 0.85, Right = true },
                new TableCol { Name = "类型", Weight = 1 },
                new TableCol { Name = "内容", Weight = 3.4 },
                new TableCol { Name = "改前", Weight = 0.95 },
                new TableCol { Name = "改后", Weight = 0.95 },
                new TableCol { Name = "操作人", Weight = 0.8 }
            },
            Rows = rows,
            BadRows = bad,
            MaxLines = 2,
            Note = full
                ? "事件只追加，不允许更新或删除；更正走「再追加一条更正事件」。"
                : "此处只列报警、安全动作、中止与操作人标记；完整事件流见随附的 run.glp。"
        });
    }

    // ── 配方与台面 ──────────────────────────────────────────────────

    private static void Recipes(ReportDoc doc, RunRecord rec, CommandCatalog catalog, Chapters no)
    {
        doc.Blocks.Add(new HeadingBlock { Level = 1, Text = no.Next("配方参数与台面") });
        doc.Blocks.Add(new ParaBlock
        {
            Muted = true,
            Text = "以下是启动那一刻冻结下来的配方，不是当前配方库里的版本——"
                 + "实验跑完之后再改配方，这一页也不会跟着变。"
        });

        foreach (var ch in rec.Channels.OrderBy(c => c.Channel))
        {
            var rows = new List<string[]>();
            var entries = ch.Baseline.Schedule.Entries;
            for (var i = 0; i < ch.Baseline.Recipe.Steps.Count; i++)
            {
                var step = ch.Baseline.Recipe.Steps[i];
                var entry = entries.FirstOrDefault(e => e.StepId == step.StepId);
                var name = catalog.TryGet(step.CommandId, out var d) ? d.DisplayName : step.CommandId;
                rows.Add(new[]
                {
                    (i + 1).ToString(CultureInfo.InvariantCulture),
                    name,
                    entry?.Title ?? step.CommandId,
                    entry is null ? "—" : Fmt.Hms(entry.Duration),
                    step.Enabled ? "" : "已停用"
                });
            }

            doc.Blocks.Add(new TableBlock
            {
                Title = $"CH{ch.Channel}　·　{ch.Baseline.Recipe.Name}",
                Columns = new[]
                {
                    new TableCol { Name = "#", Weight = 0.35, Right = true },
                    new TableCol { Name = "指令", Weight = 1.3 },
                    new TableCol { Name = "参数", Weight = 4 },
                    new TableCol { Name = "计划时长", Weight = 0.95, Right = true },
                    new TableCol { Name = "备注", Weight = 0.8 }
                },
                Rows = rows,
                MaxLines = 2,
                Note = $"冻结于 {Stamp(ch.Baseline.FrozenAt)}"
                       + (ch.Baseline.ApprovedBy is { Length: > 0 } who ? $"　·　审批 {who}" : "")
                       + (ch.Baseline.Schedule.MissingCommands.Count > 0
                          ? "　·　缺少驱动：" + string.Join("、", ch.Baseline.Schedule.MissingCommands)
                          : "")
            });
        }
    }

    // ── 配料表与化学计量 ────────────────────────────────────────────

    /// <summary>
    /// 每通道一张配料表。数据来自**启动那一刻冻结在基线里的那一份**，
    /// 不是配料表页上现在这一份——跑完之后改了当量，这一页不该跟着变。
    ///
    /// 一路都没有配料表就整节不出现：只跑温控曲线的实验不需要它，
    /// 硬摆一张空表进去，读报告的人会以为这一炉什么都没投。
    /// </summary>
    private static void Charges(ReportDoc doc, RunRecord rec, ReportOptions opt, Chapters no)
    {
        var withCharge = rec.Channels
            .Where(c => c.Baseline.Charge is { IsEmpty: false })
            .OrderBy(c => c.Channel)
            .ToList();
        if (withCharge.Count == 0) return;

        doc.Blocks.Add(new HeadingBlock { Level = 1, Text = no.Next("配料表与化学计量") });
        doc.Blocks.Add(new ParaBlock
        {
            Muted = true,
            Text = "「应称量」是按限制试剂定量、其余按当量算出来的、需要称取的量，"
                 + "已按纯度折算——料不纯就得多称一些才够那么多物质的量。"
                 + "「实投」是操作人称完回填的实际值，两者都在表里，差多少一眼看得见。"
                 + "本节数据取自启动那一刻冻结的基线；连库组分的物性是连库时拷贝进表内的快照，"
                 + "实验之后再改配料表或化合物库都不影响本节。"
        });

        foreach (var ch in withCharge)
        {
            // 已快照的基线自带全部物性，不给它活库——从机制上保证「今天改一条
            // 化合物的密度，去年那炉报告里的应量取」不会跟着变（CH-D1）。
            // 快照机制之前归档的炉没得选，只能按当前库现算，但必须把话挑明
            var frozen = ChargeSnapshot.SelfContained(ch.Baseline.Charge!);
            if (!frozen)
                doc.Blocks.Add(new NoticeBlock
                {
                    Bad = true,
                    Text = $"CH{ch.Channel} 本炉归档早于物性快照机制：连库组分的摩尔质量 / 密度 / 纯度"
                         + "取自导出这份报告时化合物库的当前值，不是启动时刻的快照——"
                         + "库在归档之后改过的话，表脚「计算用物性」里就是改过的数。"
                });

            var result = Stoichiometry.Solve(ch.Baseline.Charge!, frozen ? null : opt.Library);
            var rows = new List<string[]>();
            var bad = new List<int>();

            foreach (var l in result.Lines)
            {
                if (l.Missing.Count > 0) bad.Add(rows.Count);
                rows.Add(new[]
                {
                    l.Item.Name.Length > 0 ? l.Item.Name : "（未命名）",
                    ChargeWords.Of(l.Item.Role),
                    Basis(l.Item),
                    Q(l.Equivalents),
                    Q(l.Moles),
                    // 产物那一行放的是理论产量——它不投料，表脚里说明了这一点
                    Q(l.Item.Role == ChargeRole.Product ? l.TheoreticalMass : l.Mass),
                    Q(l.Volume),
                    Q(l.Item.ActualMass),
                    string.Join(" / ", new[] { l.Item.Batch, l.Item.Supplier }.Where(x => x.Length > 0))
                });
            }

            doc.Blocks.Add(new TableBlock
            {
                Title = $"CH{ch.Channel}　·　{ch.Baseline.Recipe.Name}",
                Columns = new[]
                {
                    // 列宽不是拍的：按内嵌字体实际量过，任何一格被掐都不接受
                    // （见「配料表的数字列一个都不许被掐掉」那条回归）。
                    // 计算用的物性没有做成一列——A4 竖排下九列已经到头，
                    // 硬塞第十列的结果是 M 和纯度双双被掐成「198.13 9…」。
                    // 它们改排在表脚：那一行是整页宽的，折得开
                    new TableCol { Name = "组分", Weight = 1.74 },
                    // 角色与基准是固定词表（「限制试剂」「给定 mmol」最长），
                    // 一行放得下才行——折成「限制试 / 剂」那样看着就是排版没做完
                    new TableCol { Name = "角色", Weight = 0.9 },
                    new TableCol { Name = "基准", Weight = 0.96 },
                    new TableCol { Name = "当量", Weight = 0.8, Right = true },
                    new TableCol { Name = "mmol", Weight = 0.85, Right = true },
                    new TableCol { Name = "应称量 g", Weight = 0.85, Right = true },
                    new TableCol { Name = "体积 mL", Weight = 0.85, Right = true },
                    new TableCol { Name = "实投 g", Weight = 0.8, Right = true },
                    new TableCol { Name = "批号 / 供应商", Weight = 1.35 }
                },
                Rows = rows,
                BadRows = bad,
                MaxLines = 2,
                Note = ChargeNote(ch.Baseline.Charge!, result)
            });

            foreach (var p in result.Problems)
                doc.Blocks.Add(new NoticeBlock { Bad = true, Text = $"CH{ch.Channel} 配料表：{p}" });

            // 缺物性的那几行单独点名。表里那一格已经标红了，但红行不说是缺什么
            var missing = result.Lines.Where(l => l.Missing.Count > 0).ToList();
            if (missing.Count > 0)
                doc.Blocks.Add(new NoticeBlock
                {
                    Bad = true,
                    Text = $"CH{ch.Channel} 有 {missing.Count} 个组分的量没能算全："
                           + string.Join("；", missing.Select(l =>
                               $"{(l.Item.Name.Length > 0 ? l.Item.Name : "（未命名）")}——"
                               + string.Join("、", l.Missing)))
                           + "。这几项在表里是空格，没有替它们填过任何数。"
                });
        }
    }

    /// <summary>表脚那一行：限制试剂是谁、合计多少、理论产量与收率。</summary>
    private static string ChargeNote(ChargeTable table, ChargeResult r)
    {
        var parts = new List<string>();

        if (r.Limiting is { } lim)
        {
            var text = $"限制试剂 {(lim.Item.Name.Length > 0 ? lim.Item.Name : "（未命名）")}";
            if (lim.Moles is { } n) text += $" {Q(n)} mmol";
            if (lim.Mass is { } m) text += $"（{Q(m)} g）";
            parts.Add(text);
        }
        else parts.Add("未指定限制试剂");

        if (r.TotalMass is { } tm) parts.Add($"投料合计 {tm.ToString("0.##", CultureInfo.InvariantCulture)} g");
        if (r.TotalVolume is { } tv)
        {
            var text = $"合计体积 {tv.ToString("0.##", CultureInfo.InvariantCulture)} mL";
            if (table.VesselVolume is { } cap)
                text += $" / 釜容 {cap.ToString("0.#", CultureInfo.InvariantCulture)} mL";
            // 混合后的体积不是各组分体积之和。拿它对釜容够用，但不能说成实测值
            text += "（各组分体积相加的估计值，混合后实际体积会有出入）";
            parts.Add(text);
        }

        foreach (var p in r.Lines.Where(l => l.Item.Role == ChargeRole.Product && l.TheoreticalMass is not null))
        {
            var text = $"目标产物「{(p.Item.Name.Length > 0 ? p.Item.Name : "（未命名）")}」那一行的"
                       + $"「应称量」是理论产量 {Q(p.TheoreticalMass)} g（按计划投料）";
            // 收率的分母按限制试剂实投折算——跟上面那个理论产量不是同一个基准，
            // 差在哪必须写出来，不然复核的人拿计算器一除对不上
            text += p.Yield is { } y
                ? $"，实际 {Q(p.Item.ActualMass)} g，收率 {y.ToString("0.#", CultureInfo.InvariantCulture)} %"
                  + (r.YieldBasis is { } basis ? $"（{basis}）" : "")
                : "，实际产量未回填";
            parts.Add(text);
        }

        // 摩尔浓度（CH-C7）。引擎不给（溶剂缺体积）就不写
        if (r.Concentration is { } conc)
            parts.Add($"摩尔浓度 {Q(conc)} mol/L（限制试剂 ÷ 溶剂总体积）");

        // 实投折算（CH-3.5）：回填过实投的行，实际投了多少摩尔、相对计划过量几何
        var acts = r.Lines.Where(l => l.ActualEquivalents is not null).ToList();
        if (acts.Count > 0)
            parts.Add("实投折算：" + string.Join("；", acts.Select(l =>
                $"{(l.Item.Name.Length > 0 ? l.Item.Name : "（未命名）")} {Q(l.ActualMoles)} mmol"
                + $"（实际当量 {Q(l.ActualEquivalents)}"
                + (l.ExcessPercent is { } ex
                    ? (ex >= 0 ? "，过量 +" : "，欠量 −")
                      + Math.Abs(ex).ToString("0.#", CultureInfo.InvariantCulture) + " %"
                    : "") + "）")));

        // 复核的人要能拿这几个数把整张表重算一遍，所以算的时候用了什么就写什么。
        // 它排在表脚而不是表里的一列：表脚是整页宽的，一列会被掐成「198.13 9…」
        var props = new List<string>();
        foreach (var l in r.Lines)
        {
            var one = new List<string>();
            if (l.MwUsed is { } m) one.Add("M " + m.ToString("0.##", CultureInfo.InvariantCulture));
            if (l.PurityUsed is { } p2) one.Add(p2.ToString("0.##", CultureInfo.InvariantCulture) + " %");
            if (l.DensityUsed is { } d) one.Add("ρ " + d.ToString("0.###", CultureInfo.InvariantCulture));
            if (one.Count == 0) continue;
            props.Add($"{(l.Item.Name.Length > 0 ? l.Item.Name : "（未命名）")} {string.Join(" / ", one)}");
        }
        if (props.Count > 0) parts.Add("计算用物性：" + string.Join("；", props));

        // 复核要能指认「按哪一版库、什么时刻的物性算的」（CH-6.3）。
        // 没盖过章的表没有这句话——没做过的事不写
        if (ChargeSnapshot.Describe(table) is { } snap) parts.Add(snap);

        return string.Join("　·　", parts);
    }

    private static string Basis(ChargeItem i) => i.Basis switch
    {
        ChargeBasis.Equivalents => "按当量",
        ChargeBasis.Volumes => "按倍量",
        _ => "给定 " + ChargeWords.Of(i.Unit)
    };

    /// <summary>没算出来就是一个空格子。填 0 进去等于说「不用加」。</summary>
    private static string N(double? v, string fmt)
        => v is null ? "" : v.Value.ToString(fmt, CultureInfo.InvariantCulture);

    /// <summary>
    /// 配料的数按**有效位**给小数，不一律四位。
    ///
    /// 一律四位的话 11.4577 有七位字符，A4 竖排下那一列放不下就被掐成「11.4…」——
    /// 一张写着「11.4…」的配料表没法拿去称量，而它存在的理由就是拿去称量。
    /// 一律两位又会把 0.0125 g 的催化剂圆成 0.01（差两成）。所以按量级来：
    /// 上千位保留一位，上十位两位，个位三位，小于 1 的四位——最长都是六个字符。
    /// 溶剂动辄上千 mmol，四位整数再加两位小数就是七个字符，正好卡在放不下那一档。
    /// </summary>
    private static string Q(double? v)
    {
        if (v is not { } x) return "";
        var a = Math.Abs(x);
        var fmt = a >= 10000 ? "0" : a >= 1000 ? "0.#" : a >= 10 ? "0.##" : a >= 1 ? "0.###" : "0.####";
        return x.ToString(fmt, CultureInfo.InvariantCulture);
    }

    private static void Compounds(ReportDoc doc, ReportOptions opt, Chapters no)
    {
        doc.Blocks.Add(new HeadingBlock { Level = 1, Text = no.Next("化合物物性") });
        doc.Blocks.Add(new TableBlock
        {
            Columns = new[]
            {
                new TableCol { Name = "名称", Weight = 1.6 },
                new TableCol { Name = "CAS", Weight = 1 },
                new TableCol { Name = "物性", Weight = 4 }
            },
            Rows = opt.Compounds.Select(c => new[] { c.Name, c.Cas, c.Props }).ToList()
        });
    }

    // ── GLP 附录 ────────────────────────────────────────────────────

    private static void Glp(ReportDoc doc, RunRecord rec, ExportMeta meta, ReportOptions opt)
    {
        doc.Blocks.Add(new PageBreakBlock());
        doc.Blocks.Add(new HeadingBlock { Level = 1, Text = "附录　GLP 合规" });

        doc.Blocks.Add(new ParaBlock
        {
            Text = "本次导出的记录文件采用只追加写入：每一行末尾带一段链式摘要，"
                 + "摘要由「上一行的摘要 + 本行内容」算出。任何一行被改动，从那一行起后面所有摘要都对不上。"
        });

        if (opt.Checksum && opt.Files.Count > 0)
            doc.Blocks.Add(new TableBlock
            {
                Title = "随附文件与完整性校验码（SHA-256）",
                Columns = new[]
                {
                    new TableCol { Name = "文件", Weight = 1.2 },
                    new TableCol { Name = "SHA-256", Weight = 4 }
                },
                Rows = opt.Files.Select(f => new[] { f.Name, f.Sha }).ToList(),
                Note = "核对方法：对文件重新计算 SHA-256，与此处比对。"
            });
        else if (opt.Checksum)
            doc.Blocks.Add(new ParaBlock
            {
                Muted = true,
                Text = "本次导出未生成随附数据文件，因此没有可列出的文件校验码。"
            });
        else
            doc.Blocks.Add(new ParaBlock
            {
                Muted = true,
                Text = "本次导出未勾选「生成完整性校验码」，此处不列校验码——"
                     + "而不是写一份没有摘要的校验表。"
            });

        var counts = new List<(string, string)>
        {
            ("通道数", rec.Channels.Count.ToString(CultureInfo.InvariantCulture)),
            ("步骤记录", rec.Channels.Sum(c => c.Steps.Count).ToString(CultureInfo.InvariantCulture)),
            ("事件记录", rec.Channels.Sum(c => c.Events.Count).ToString(CultureInfo.InvariantCulture)),
            ("参数修改", rec.Channels.Sum(c => c.Events.Count(e => e.Kind == EventKind.ParameterChanged))
                                     .ToString(CultureInfo.InvariantCulture)),
            ("报警", rec.Channels.Sum(c => c.Events.Count(e => e.Kind == EventKind.Alarm))
                                 .ToString(CultureInfo.InvariantCulture)),
            ("操作人标记", rec.Channels.Sum(c => c.Events.Count(e => e.Kind == EventKind.OperatorMark))
                                       .ToString(CultureInfo.InvariantCulture))
        };
        doc.Blocks.Add(new KvBlock { Title = "记录条目统计", Pairs = counts, Columns = 3 });

        if (!opt.Audit)
            doc.Blocks.Add(new NoticeBlock
            {
                Text = "本次导出未勾选「附带审计追踪」，随附文件中不含事件行。",
                Bad = false
            });
    }

    private static void Sign(ReportDoc doc, RunRecord rec, ExportMeta meta, ReportOptions opt)
    {
        if (!opt.Signature) return;
        doc.Blocks.Add(new HeadingBlock { Level = 2, Text = "签名" });
        doc.Blocks.Add(new KvBlock
        {
            Pairs = new List<(string, string)>
            {
                ("实验操作人", rec.Operator ?? meta.Operator),
                ("导出人", meta.Operator),
                ("签名人", meta.Signer.Length > 0 ? meta.Signer : "（未填写）"),
                ("签名时刻", Stamp(meta.ExportedAt))
            }
        });
        doc.Blocks.Add(new ParaBlock
        {
            Muted = true,
            Text = "本页签名由导出时填写的签名人记录，同时写入随附的 run.glp。"
                 + "程序尚未接入身份认证系统，签名人是一个由操作人自行填写的字段——"
                 + "在接入之前，它不构成 21 CFR Part 11 意义上的电子签名。"
        });
    }

    /// <summary>「釜内 Tr」→「Tr」。窄列里塞不下四个字，而 Tr / Tj 本身就说得清。</summary>
    private static string Short(string? mode) => mode switch
    {
        null or "" => "",
        var m when m.Contains("Tj", StringComparison.Ordinal) => "Tj",
        var m when m.Contains("Tr", StringComparison.Ordinal) => "Tr",
        var m => m
    };

    /// <summary>
    /// 把**每一行都空着**的列摘掉，行数据跟着删对应的格。
    ///
    /// A4 竖排一张十三列的表，宽度是抢出来的。没有循环的配方「轮」列全空、
    /// 没标阶段的「阶段」列全空、纯加料配方「控温」列全空——留着它们
    /// 等于让真正有内容的几列各挨一刀。空列不是信息，是版面开销。
    /// </summary>
    private static IReadOnlyList<TableCol> Trim(ref List<string[]> rows, IReadOnlyList<TableCol> cols)
    {
        var keep = new List<int>();
        for (var i = 0; i < cols.Count; i++)
            if (rows.Any(r => i < r.Length && r[i].Length > 0)) keep.Add(i);
        if (keep.Count == cols.Count || keep.Count == 0) return cols;

        rows = rows.Select(r => keep.Select(i => i < r.Length ? r[i] : "").ToArray()).ToList();
        return keep.Select(i => cols[i]).ToList();
    }

    private static string Stamp(DateTimeOffset at) => at.ToString("yyyy-MM-dd HH:mm:ss");
    private static string Clock(DateTimeOffset at) => at.ToString("HH:mm:ss");
}
