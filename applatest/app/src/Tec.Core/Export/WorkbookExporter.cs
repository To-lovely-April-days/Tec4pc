using Tec.Core.Catalog;
using Tec.Core.Chemistry;
using Tec.Core.Data;
using Tec.Core.Records;
using Tec.Driver.Abi;

namespace Tec.Core.Export;

/// <summary>
/// 一炉 → 一本工作簿。
///
/// 页的分法就是格式卡上写的那句「每通道一页」：概要 · CH1 · CH2 … · 步骤记录 · 事件与报警。
/// 通道各自启动、各自结束，硬塞进一张表必然是一半空格（§13.4）——
/// 一路一页，每一页的第一行就是这一路自己的 t0。
/// </summary>
public static class WorkbookExporter
{
    public static IReadOnlyList<XlSheet> Build(RunRecord rec, ISampleSource samples, ExportOptions opt,
                                               CommandCatalog catalog, ExportMeta meta)
    {
        var sheets = new List<XlSheet> { Summary(rec, samples, opt, meta) };

        foreach (var ch in rec.Channels.OrderBy(c => c.Channel))
        {
            if (opt.Channels.Count > 0 && !opt.Channels.Contains(ch.Channel)) continue;
            if (opt.IncludeSamples) sheets.Add(ChannelSheet(ch, samples, opt));
        }

        if (opt.IncludeExecution) sheets.Add(Steps(rec, opt, catalog));
        if (opt.IncludeEvents) sheets.Add(Events(rec));
        if (Charges(rec, opt) is { } charge) sheets.Add(charge);
        return sheets;
    }

    // ── 配料表 ──────────────────────────────────────────────────────

    /// <summary>
    /// 四路的配料表摞成一张平表（多一列「通道」）。
    ///
    /// 报告里是一路一张——那是给人读的；这里是给人拿去做透视和对比的，
    /// 平表才筛得动。**数字以数字身份写进去**，不然一列 mmol 在 Excel 里排序会乱。
    ///
    /// 一路都没有配料表就不出这一页：空页比没有更让人以为「导漏了」。
    /// </summary>
    private static XlSheet? Charges(RunRecord rec, ExportOptions opt)
    {
        var channels = rec.Channels
            .Where(c => c.Baseline.Charge is { IsEmpty: false })
            .Where(c => opt.Channels.Count == 0 || opt.Channels.Contains(c.Channel))
            .OrderBy(c => c.Channel)
            .ToList();
        if (channels.Count == 0) return null;

        var s = new XlSheet("配料表") { Freeze = 1, FilterRow = 1 };
        s.Widths.AddRange(new[] { 8.0, 26, 14, 12, 12, 10, 10, 10, 10, 12, 12, 12, 12, 12,
                                  16, 16, 12, 12, 12, 22, 30 });
        s.Head("通道", "组分", "CAS", "角色", "基准", "给定量", "单位", "当量",
               "M g/mol", "密度 g/mL", "纯度 %", "mmol", "应称量 g", "应量取 mL",
               "实投 g", "实取 mL", "实投 mmol", "实际当量", "过量 %",
               "物性快照", "缺什么 / 按什么假设算的");

        foreach (var ch in channels)
        {
            // 已快照的基线不给活库：历史工作簿里的数不许跟着今天的库变（CH-D1）
            var frozen = ChargeSnapshot.SelfContained(ch.Baseline.Charge!);
            var r = Stoichiometry.Solve(ch.Baseline.Charge!, frozen ? null : opt.Library);
            foreach (var l in r.Lines)
            {
                var isProduct = l.Item.Role == ChargeRole.Product;
                s.Add(
                    XlCell.S("CH" + ch.Channel),
                    XlCell.S(l.Item.Name),
                    XlCell.S(l.Item.Cas.Length > 0 && !l.Item.Cas.StartsWith('#') ? l.Item.Cas : ""),
                    XlCell.S(ChargeWords.Of(l.Item.Role)),
                    XlCell.S(ChargeWords.Of(l.Item.Basis)),
                    XlCell.N(l.Item.Basis == ChargeBasis.Quantity ? l.Item.Amount : null, XlStyle.Num4),
                    XlCell.S(l.Item.Basis == ChargeBasis.Quantity ? ChargeWords.Of(l.Item.Unit) : ""),
                    XlCell.N(l.Equivalents, XlStyle.Num4),
                    XlCell.N(l.MwUsed, XlStyle.Num2),
                    XlCell.N(l.DensityUsed, XlStyle.Num4),
                    XlCell.N(l.PurityUsed, XlStyle.Num2),
                    XlCell.N(l.Moles, XlStyle.Num4),
                    // 产物那一行「应称量」放的是理论产量——它不投料
                    XlCell.N(isProduct ? l.TheoreticalMass : l.Mass, XlStyle.Num4),
                    XlCell.N(l.Volume, XlStyle.Num4),
                    XlCell.N(l.Item.ActualMass, XlStyle.Num4),
                    XlCell.N(l.Item.ActualVolume, XlStyle.Num4),
                    XlCell.N(l.ActualMoles, XlStyle.Num4),
                    XlCell.N(l.ActualEquivalents, XlStyle.Num4),
                    XlCell.N(l.ExcessPercent, XlStyle.Num2),
                    XlCell.S(l.Item.SnapshotAt is { } t
                        ? $"库第 {l.Item.LibraryVersion} 版 · {t:yyyy-MM-dd HH:mm}"
                        : l.Item.Cas.Length > 0 ? "未快照（早于快照机制）" : ""),
                    XlCell.S(string.Join("；", l.Missing.Concat(l.Assumptions))));
            }

            s.Blank();
            s.Add(XlCell.S("CH" + ch.Channel + " 合计", XlStyle.Head), XlCell.S(""), XlCell.S(""),
                  XlCell.S(""), XlCell.S(""), XlCell.S(""), XlCell.S(""), XlCell.S(""),
                  XlCell.S(""), XlCell.S(""), XlCell.S(""), XlCell.S(""),
                  XlCell.N(r.TotalMass, XlStyle.Num4), XlCell.N(r.TotalVolume, XlStyle.Num4));

            foreach (var p in r.Problems) s.Add(XlCell.S("CH" + ch.Channel + " 提示"), XlCell.S(p));
            foreach (var p in r.Lines.Where(x => x.Item.Role == ChargeRole.Product && x.Yield is not null))
                s.Add(XlCell.S("CH" + ch.Channel + " 收率", XlStyle.Head), XlCell.S(p.Item.Name),
                      XlCell.N(p.Yield, XlStyle.Num2), XlCell.S("%"));
            s.Blank();
        }

        s.Add(XlCell.S("「应称量」已按纯度折算——料不纯就得多称一些才够那么多物质的量；"
                       + "「实投」是操作人称完回填的实际值；「实投 mmol / 实际当量 / 过量 %」由实投"
                       + "按同一套纯度折算反推，实际当量以限制试剂实投为基准（没回填则按计划）。"
                       + "空格子 = 没算出来或没填，不是 0。"
                       + "配料表取自各通道启动那一刻冻结的基线；连库组分的物性是连库时刻的快照"
                       + "（见「物性快照」列），之后改化合物库不影响本表。"
                       + "标着「未快照」的行早于快照机制，物性取自导出时库的当前值，"
                       + "具体哪个数取自当前库见最后一列。", XlStyle.Note));
        return s;
    }

    // ── 概要 ────────────────────────────────────────────────────────

    private static XlSheet Summary(RunRecord rec, ISampleSource samples, ExportOptions opt, ExportMeta meta)
    {
        var s = new XlSheet("概要") { Freeze = 0 };
        s.Widths.AddRange(new[] { 18.0, 30, 14, 14, 14, 14, 12, 12 });

        s.Add(XlCell.S("实验记录", XlStyle.Title));
        s.Add(XlCell.S("本工作簿由 TecStudio 从执行引擎记录导出；数字均为实测或仿真采样，未做任何平滑或补值。", XlStyle.Note));
        s.Blank();

        void KV(string k, string? v, XlStyle style = XlStyle.Text)
        {
            s.Add(XlCell.S(k, XlStyle.Head), XlCell.S(v ?? "", style));
        }

        KV("实验名称", meta.Experiment);
        KV("记录编号", rec.RunId);
        KV("批次名称", rec.Name);
        KV("台面", string.IsNullOrEmpty(rec.BenchName) ? meta.BenchName : rec.BenchName);
        KV("操作人", rec.Operator ?? meta.Operator);
        s.Add(XlCell.S("批次开始", XlStyle.Head), XlCell.T(rec.FirstStart ?? rec.CreatedAt));
        s.Add(XlCell.S("导出时刻", XlStyle.Head), XlCell.T(meta.ExportedAt));
        KV("采样间隔", meta.Interval);
        KV("时间基准", meta.TimeBaseText);
        if (meta.Signer.Length > 0) KV("签名人", meta.Signer);
        if (meta.AppVersion.Length > 0) KV("程序版本", "TecStudio " + meta.AppVersion);
        if (meta.Archived) KV("数据来源", "从归档读回（非本次开机运行）");

        var sim = rec.Channels.Where(c => c.Simulated).Select(c => "CH" + c.Channel).ToList();
        if (sim.Count > 0)
            KV("仿真运行", string.Join(" / ", sim) + " 为仿真数据，不是真实实验结果", XlStyle.Bad);
        if (meta.SamplesTruncated)
            KV("采样完整性", "归档时早期采样已被环形缓冲顶掉，本表只含仍保留的时段", XlStyle.Bad);

        s.Blank();
        s.Add(XlCell.S("通道", XlStyle.Head), XlCell.S("配方", XlStyle.Head),
              XlCell.S("启动", XlStyle.Head), XlCell.S("结束", XlStyle.Head),
              XlCell.S("时长", XlStyle.Head), XlCell.S("状态", XlStyle.Head),
              XlCell.S("步骤数", XlStyle.Head), XlCell.S("采样点", XlStyle.Head));

        foreach (var ch in rec.Channels.OrderBy(c => c.Channel))
        {
            long pts = 0;
            foreach (var k in samples.Keys)
                if (k.Channel == ch.Channel)
                    pts += CountIn(samples.Snapshot(k.Channel, k.Tag), ch.StartedAt, ch.FinishedAt);

            s.Add(XlCell.S("CH" + ch.Channel),
                  XlCell.S(ch.Baseline.Recipe.Name),
                  XlCell.T(ch.StartedAt),
                  XlCell.T(ch.FinishedAt),
                  XlCell.D(ch.FinishedAt is { } f ? f - ch.StartedAt : null),
                  XlCell.S(RunStateWords.Of(ch.State), ch.State is ChannelRunState.Aborted or ChannelRunState.Faulted
                                                       ? XlStyle.Bad : XlStyle.Text),
                  XlCell.I(ch.Steps.Count),
                  XlCell.I(pts));
        }

        s.Blank();
        s.Add(XlCell.S("导出的数据项", XlStyle.Head));
        var tags = opt.Tags.Count > 0
            ? opt.Tags
            : samples.Keys.Select(k => k.Tag).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
        foreach (var t in tags)
        {
            var td = samples.Tag(t);
            s.Add(XlCell.S(t), XlCell.S(td?.DisplayName ?? t), XlCell.S(td?.Unit ?? ""));
        }
        if (tags.Count == 0) s.Add(XlCell.S("（本次未导出数值数据项）", XlStyle.Note));

        return s;
    }

    private static long CountIn(Sample[] arr, DateTimeOffset from, DateTimeOffset? to)
    {
        long n = 0;
        foreach (var s in arr)
            if (s.WallClock >= from && (to is null || s.WallClock <= to)) n++;
        return n;
    }

    // ── 每通道一页 ──────────────────────────────────────────────────

    private static XlSheet ChannelSheet(ChannelRun ch, ISampleSource samples, ExportOptions opt)
    {
        var cols = samples.Keys
            .Where(k => k.Channel == ch.Channel)
            .Where(k => opt.Tags.Count == 0 || opt.Tags.Contains(k.Tag))
            .OrderBy(k => k.Tag, StringComparer.Ordinal)
            .ToList();

        var s = new XlSheet($"CH{ch.Channel}") { Freeze = 1, FilterRow = 1 };
        s.Widths.Add(19);       // 绝对时间
        s.Widths.Add(11);       // 相对通道
        foreach (var _ in cols) s.Widths.Add(13);

        var head = new List<XlCell>
        {
            XlCell.S("时刻（绝对）", XlStyle.Head),
            XlCell.S("相对本通道启动", XlStyle.Head)
        };
        foreach (var c in cols)
        {
            var td = samples.Tag(c.Tag);
            var name = td?.DisplayName ?? c.Tag;
            head.Add(XlCell.S(string.IsNullOrEmpty(td?.Unit) ? name : $"{name} ({td!.Unit})", XlStyle.Head));
        }
        s.Rows.Add(head.ToArray());

        if (cols.Count == 0)
        {
            s.Add(XlCell.S("这一路没有采到任何数值数据。", XlStyle.Note));
            return s;
        }

        var grid = opt.Grid <= TimeSpan.Zero ? TimeSpan.FromSeconds(5) : opt.Grid;
        var data = cols.Select(c => samples.Snapshot(c.Channel, c.Tag)).ToList();
        var cursor = new int[cols.Count];
        var last = new double?[cols.Count];
        var lastAt = new DateTimeOffset?[cols.Count];

        var from = ch.StartedAt;
        var to = ch.FinishedAt ?? data.Where(d => d.Length > 0).Select(d => d[^1].WallClock)
                                     .DefaultIfEmpty(ch.StartedAt).Max();

        var wrote = 0;
        for (var t = from; t <= to; t += grid)
        {
            var row = new List<XlCell> { XlCell.T(t), XlCell.D(t - ch.StartedAt) };
            var any = false;
            for (var c = 0; c < cols.Count; c++)
            {
                var arr = data[c];
                while (cursor[c] < arr.Length && arr[cursor[c]].WallClock <= t)
                {
                    last[c] = arr[cursor[c]].Value;
                    lastAt[c] = arr[cursor[c]].WallClock;
                    cursor[c]++;
                }
                // 采样率不匹配时不补值：拉曼 30 s 一条，不能假装每秒都有（§9.4）
                if (lastAt[c] is { } at && at > t - grid && last[c] is { } v)
                {
                    row.Add(XlCell.N(v, Math.Abs(v) < 0.01 && v != 0 ? XlStyle.Num6 : XlStyle.Num2));
                    any = true;
                }
                else row.Add(XlCell.Empty);
            }
            // 整行一个值都没有就不写这一行。栅格比实际采样率密时（拉曼 30 s 一条、
            // 栅格 5 s）会排出一大片只有时间列的空行——那不是数据，是噪声，
            // 而且让人以为「这一段采样丢了」。时间列上的跳变本身就说明了空档
            if (!any) continue;
            s.Rows.Add(row.ToArray());
            wrote++;
        }

        if (wrote == 0) s.Add(XlCell.S("这一路在导出时段内没有留下采样点。", XlStyle.Note));
        return s;
    }

    // ── 步骤记录 ────────────────────────────────────────────────────

    private static XlSheet Steps(RunRecord rec, ExportOptions opt, CommandCatalog catalog)
    {
        var s = new XlSheet("步骤记录") { Freeze = 1, FilterRow = 1 };
        s.Widths.AddRange(new[] { 7.0, 6, 6, 30, 10, 10, 16, 12, 12, 12, 11, 11, 11, 11, 14, 9, 20, 8 });
        s.Head("通道", "轮次", "序号", "步骤", "工艺阶段", "控温对象", "指令", "结束方式",
               "计划开始", "实际开始", "开始偏差", "计划时长", "实际时长", "时长偏差",
               "结束原因", "状态", "备注", "数据来源");

        foreach (var ch in rec.Channels.OrderBy(c => c.Channel))
        {
            if (opt.Channels.Count > 0 && !opt.Channels.Contains(ch.Channel)) continue;
            foreach (var st in ch.Steps)
                s.Add(XlCell.S("CH" + ch.Channel),
                      XlCell.I(st.Iteration),
                      XlCell.I(st.Index + 1),
                      XlCell.S(st.Title),
                      XlCell.S(st.Phase ?? ""),
                      XlCell.S(st.ControlMode ?? ""),
                      XlCell.S(st.CommandId),
                      XlCell.S(TerminationWords.Of(st.Termination)),
                      XlCell.T(ch.StartedAt + st.PlanStart),
                      XlCell.T(st.ActualStart),
                      XlCell.D(st.StartDeviation),
                      XlCell.D(st.PlanDuration),
                      XlCell.D(st.ActualDuration),
                      XlCell.D(st.DurationDeviation),
                      XlCell.S(EndBy.Text(st, catalog)),
                      XlCell.S(StatusWords.Of(st.Status),
                               st.Status is StepStatus.Aborted or StepStatus.Failed ? XlStyle.Bad : XlStyle.Text),
                      XlCell.S(st.Note ?? ""),
                      XlCell.S(ch.Simulated ? "仿真" : "实测", ch.Simulated ? XlStyle.Bad : XlStyle.Text));
        }
        if (s.Rows.Count == 1) s.Add(XlCell.S("这一炉没有步骤记录。", XlStyle.Note));
        return s;
    }

    // ── 事件与报警 ──────────────────────────────────────────────────

    private static XlSheet Events(RunRecord rec)
    {
        var s = new XlSheet("事件与报警") { Freeze = 1, FilterRow = 1 };
        s.Widths.AddRange(new[] { 7.0, 19, 12, 14, 46, 16, 16, 10 });
        s.Head("通道", "时刻", "相对通道", "类型", "内容", "改前", "改后", "操作人");

        foreach (var ch in rec.Channels.OrderBy(c => c.Channel))
            foreach (var e in ch.Events)
                s.Add(XlCell.S("CH" + ch.Channel),
                      XlCell.T(e.At),
                      XlCell.D(e.At - ch.StartedAt),
                      XlCell.S(EventWords.Of(e.Kind)),
                      XlCell.S(e.Text, e.Kind is EventKind.Alarm or EventKind.SafetyAction or EventKind.DeviceFault
                                       ? XlStyle.Bad : XlStyle.Text),
                      XlCell.S(e.Before ?? ""),
                      XlCell.S(e.After ?? ""),
                      XlCell.S(e.User ?? ""));

        if (s.Rows.Count == 1) s.Add(XlCell.S("这一炉没有事件记录。", XlStyle.Note));
        return s;
    }
}

/// <summary>结束方式的中文说法。CSV / Excel / 报告共用一处，免得三处各写一版。</summary>
public static class TerminationWords
{
    public static string Of(TerminationKind k) => k switch
    {
        TerminationKind.Setpoint => "到达目标",
        TerminationKind.Timer => "计时到",
        TerminationKind.Quantity => "加完设定量",
        TerminationKind.Condition => "条件满足",
        TerminationKind.Operator => "操作人",
        TerminationKind.Alarm => "报警",
        TerminationKind.Timeout => "超时",
        _ => "立即"
    };
}

public static class StatusWords
{
    public static string Of(StepStatus s) => s switch
    {
        StepStatus.Pending => "待执行",
        StepStatus.Running => "执行中",
        StepStatus.Done => "完成",
        StepStatus.Skipped => "跳过",
        StepStatus.Aborted => "中止",
        _ => "失败"
    };
}

/// <summary>
/// 事件类型的中文说法。**记录文件里不许出现枚举名**——
/// 一份交给审计的记录写着 AlarmAck，读的人得先会英文再会本程序。
/// </summary>
public static class EventWords
{
    public static string Of(EventKind k) => k switch
    {
        EventKind.ChannelStarted => "通道启动",
        EventKind.ChannelFinished => "通道结束",
        EventKind.Paused => "暂停",
        EventKind.Resumed => "继续",
        EventKind.ParameterChanged => "参数修改",
        EventKind.StepSkipped => "跳过步骤",
        EventKind.SafeStop => "收至安全态",
        EventKind.Aborted => "中止",
        EventKind.Alarm => "报警",
        EventKind.SafetyAction => "安全动作",
        EventKind.AlarmAck => "报警确认",
        EventKind.AlarmCleared => "报警恢复",
        EventKind.OperatorMark => "操作人标记",
        EventKind.Sampling => "取样",
        EventKind.ResourceWait => "等待资源",
        EventKind.DeviceFault => "设备故障",
        _ => "备注"
    };
}
