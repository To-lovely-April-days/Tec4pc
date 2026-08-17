using System.Globalization;
using Tec.Core.Catalog;
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

        Summary(doc, rec, samples, meta, t0, end);

        if (opt.Trend && opt.Charts.Count > 0)
        {
            doc.Blocks.Add(new HeadingBlock { Level = 1, Text = "二、趋势曲线" });
            foreach (var chart in opt.Charts) doc.Blocks.Add(chart);
        }
        else if (opt.Trend)
        {
            doc.Blocks.Add(new HeadingBlock { Level = 1, Text = "二、趋势曲线" });
            doc.Blocks.Add(new ParaBlock
            {
                Muted = true,
                Text = "这一炉在导出时没有可画的采样数据——采样已不在缓冲区内，或本次未勾选任何数值数据项。"
            });
        }

        if (opt.Template == ReportTemplate.Summary)
        {
            Deviations(doc, rec, catalog);
            Sign(doc, rec, meta, opt);
            return doc;
        }

        if (opt.Steps) Steps(doc, rec, catalog);
        if (opt.Alarms) Events(doc, rec, opt.Template == ReportTemplate.Glp || opt.Audit);
        if (opt.RecipeAndBench) Recipes(doc, rec, catalog);
        if (opt.Chemicals && opt.Compounds.Count > 0) Compounds(doc, opt);
        if (opt.Template == ReportTemplate.Glp) Glp(doc, rec, meta, opt);
        Sign(doc, rec, meta, opt);
        return doc;
    }

    // ── 一、实验概要 ────────────────────────────────────────────────

    private static void Summary(ReportDoc doc, RunRecord rec, ISampleSource samples,
                                ExportMeta meta, DateTimeOffset t0, DateTimeOffset end)
    {
        doc.Blocks.Add(new PageBreakBlock());
        doc.Blocks.Add(new HeadingBlock { Level = 1, Text = "一、实验概要" });

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

    private static void Deviations(ReportDoc doc, RunRecord rec, CommandCatalog catalog)
    {
        doc.Blocks.Add(new HeadingBlock { Level = 1, Text = "三、超差与异常" });

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

    private static void Steps(ReportDoc doc, RunRecord rec, CommandCatalog catalog)
    {
        doc.Blocks.Add(new HeadingBlock { Level = 1, Text = "三、步骤执行记录" });
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

    private static void Events(ReportDoc doc, RunRecord rec, bool full)
    {
        doc.Blocks.Add(new HeadingBlock { Level = 1, Text = "四、事件与报警" });

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

    private static void Recipes(ReportDoc doc, RunRecord rec, CommandCatalog catalog)
    {
        doc.Blocks.Add(new HeadingBlock { Level = 1, Text = "五、配方参数与台面" });
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

    private static void Compounds(ReportDoc doc, ReportOptions opt)
    {
        doc.Blocks.Add(new HeadingBlock { Level = 1, Text = "六、化合物物性" });
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
