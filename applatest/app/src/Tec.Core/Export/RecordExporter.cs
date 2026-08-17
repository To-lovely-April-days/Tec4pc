using System.Globalization;
using System.Text;
using Tec.Core.Catalog;
using Tec.Core.Data;
using Tec.Core.Records;
using Tec.Driver.Abi;

namespace Tec.Core.Export;

public enum TimeBase
{
    /// <summary>墙钟。跨通道对齐用它。</summary>
    Wall,
    /// <summary>各通道自己的 t0。通道各自启动，这才是"这一步跑了多久"的自然坐标。</summary>
    Channel
}

public enum TableShape
{
    /// <summary>长表：一行一个点。通道 × 标签任意多，不会有空列。</summary>
    Long,
    /// <summary>宽表：一行一个时刻，一列一个通道-标签。</summary>
    Wide
}

public sealed class ExportOptions
{
    public TimeBase TimeBase { get; set; } = TimeBase.Wall;
    public TableShape Shape { get; set; } = TableShape.Long;
    public List<int> Channels { get; } = new();
    public List<string> Tags { get; } = new();
    public bool IncludeExecution { get; set; } = true;
    public bool IncludeEvents { get; set; } = true;
    public bool IncludeSamples { get; set; } = true;
    /// <summary>宽表的时间栅格。</summary>
    public TimeSpan Grid { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// 导出。执行记录是这里最值钱的部分：iControl 那套回答的是
/// "这一步到底跑了多久、跟计划差多少"——多通道下每个通道都要有自己的答案。
/// </summary>
public static class RecordExporter
{
    private const string Sep = ",";

    /// <summary>
    /// 执行记录 CSV。结束原因与状态写中文：这是要交出去的记录文件，
    /// 不能一边界面显示「到达设定」、文件里却是 Reached。
    /// </summary>
    public static string ExecutionCsv(RunRecord record, TimeBase timeBase, CommandCatalog catalog)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 执行记录 " + record.Name + "  批次 " + record.RunId);
        sb.AppendLine("# 计划来自启动时冻结的基线；实际来自运行记录。两种偏差分列。");
        sb.AppendLine(string.Join(Sep,
            "通道", "轮次", "序号", "步骤", "工艺阶段", "控温对象", "指令", "结束方式",
            timeBase == TimeBase.Wall ? "计划开始(墙钟)" : "计划开始(通道)",
            timeBase == TimeBase.Wall ? "实际开始(墙钟)" : "实际开始(通道)",
            "开始偏差", "计划时长", "实际时长", "时长偏差", "结束原因", "状态", "备注", "数据来源"));

        foreach (var ch in record.Channels.OrderBy(c => c.Channel))
        {
            var source = ch.Simulated ? "仿真" : "实测";
            foreach (var s in ch.Steps)
            {
                var planStart = timeBase == TimeBase.Wall
                    ? Fmt.Clock(ch.StartedAt + s.PlanStart)
                    : Fmt.Hms(s.PlanStart);
                var actualStart = s.ActualStart is { } a
                    ? (timeBase == TimeBase.Wall ? Fmt.Clock(a) : Fmt.Hms(a - ch.StartedAt))
                    : "";
                sb.AppendLine(string.Join(Sep,
                    "CH" + ch.Channel,
                    s.Iteration.ToString(CultureInfo.InvariantCulture),
                    (s.Index + 1).ToString(CultureInfo.InvariantCulture),
                    Esc(s.Title),
                    Esc(s.Phase ?? ""),
                    Esc(s.ControlMode ?? ""),
                    Esc(s.CommandId),
                    TerminationWords.Of(s.Termination),
                    planStart,
                    actualStart,
                    s.StartDeviation is { } sd ? Fmt.Signed(sd) : "",
                    Fmt.Hms(s.PlanDuration),
                    s.ActualDuration is { } ad ? Fmt.Hms(ad) : "",
                    s.DurationDeviation is { } dd ? Fmt.Signed(dd) : "",
                    EndBy.Text(s, catalog),
                    StatusWords.Of(s.Status),
                    Esc(s.Note ?? ""),
                    source));
            }
        }
        return sb.ToString();
    }

    public static string EventsCsv(RunRecord record, TimeBase timeBase)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 事件记录（只追加；更正走追加一条更正事件）");
        sb.AppendLine(string.Join(Sep, "通道", "时刻", "相对通道", "类型", "内容", "改前", "改后", "操作人"));
        foreach (var ch in record.Channels.OrderBy(c => c.Channel))
            foreach (var e in ch.Events)
                sb.AppendLine(string.Join(Sep,
                    "CH" + ch.Channel,
                    Fmt.Stamp(e.At),
                    Fmt.Hms(e.At - ch.StartedAt),
                    EventWords.Of(e.Kind),
                    Esc(e.Text),
                    Esc(e.Before ?? ""),
                    Esc(e.After ?? ""),
                    Esc(e.User ?? "")));
        return sb.ToString();
    }

    /// <summary>
    /// 长表。原型上吃过亏：按批次时间迭代会让晚启动的通道在自己的零点前留一片空行，
    /// 正好把"通道各自启动"这件事抹掉。这里按每个通道自己的点逐条出（§13.4 同理）。
    /// </summary>
    public static string SamplesLongCsv(ISampleSource pipeline, RunRecord record, ExportOptions opt)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 采样长表");
        sb.AppendLine(string.Join(Sep, "通道", "时刻", "相对通道", "标签", "名称", "数值", "单位", "质量"));

        foreach (var ch in record.Channels.OrderBy(c => c.Channel))
        {
            if (opt.Channels.Count > 0 && !opt.Channels.Contains(ch.Channel)) continue;
            var rows = new List<Sample>();
            foreach (var key in pipeline.Keys)
            {
                if (key.Channel != ch.Channel) continue;
                if (opt.Tags.Count > 0 && !opt.Tags.Contains(key.Tag)) continue;
                rows.AddRange(pipeline.Snapshot(key.Channel, key.Tag).Where(s => s.WallClock >= ch.StartedAt
                                                        && (ch.FinishedAt is null || s.WallClock <= ch.FinishedAt)));
            }
            foreach (var s in rows.OrderBy(s => s.WallClock).ThenBy(s => s.Tag, StringComparer.Ordinal))
            {
                var td = pipeline.Tag(s.Tag);
                sb.AppendLine(string.Join(Sep,
                    "CH" + s.Channel,
                    Fmt.Stamp(s.WallClock),
                    Fmt.Hms(s.WallClock - ch.StartedAt),
                    Esc(s.Tag),
                    Esc(td?.DisplayName ?? s.Tag),
                    s.Value.ToString("G6", CultureInfo.InvariantCulture),
                    Esc(td?.Unit ?? ""),
                    s.Quality.ToString()));
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// 宽表。墙钟基准出一张跨通道对齐的表；通道基准出多张，一张一个通道——
    /// 因为通道各自启动，硬塞进一张表必然是一半空格。
    /// </summary>
    public static string SamplesWideCsv(ISampleSource pipeline, RunRecord record, ExportOptions opt)
    {
        var sb = new StringBuilder();
        var channels = record.Channels
            .Where(c => opt.Channels.Count == 0 || opt.Channels.Contains(c.Channel))
            .OrderBy(c => c.Channel).ToList();
        if (channels.Count == 0) return "# 没有可导出的通道\n";

        if (opt.TimeBase == TimeBase.Wall)
        {
            var from = channels.Min(c => c.StartedAt);
            var to = channels.Max(c => c.FinishedAt ?? c.StartedAt);
            var cols = ColumnsOf(pipeline, channels.Select(c => c.Channel).ToList(), opt);
            sb.AppendLine("# 采样宽表 · 墙钟对齐");
            sb.AppendLine(string.Join(Sep, new[] { "时刻" }.Concat(cols.Select(c => $"CH{c.Channel}·{Header(pipeline, c.Tag)}"))));
            WriteGrid(sb, pipeline, cols, from, to, opt.Grid, t => Fmt.Stamp(t));
            return sb.ToString();
        }

        foreach (var ch in channels)
        {
            var cols = ColumnsOf(pipeline, new List<int> { ch.Channel }, opt);
            if (cols.Count == 0) continue;
            sb.AppendLine($"# 采样宽表 · CH{ch.Channel} 自身时间基准（t0 = {Fmt.Stamp(ch.StartedAt)}）");
            sb.AppendLine(string.Join(Sep, new[] { "相对通道" }.Concat(cols.Select(c => Header(pipeline, c.Tag)))));
            var to = ch.FinishedAt ?? ch.StartedAt;
            WriteGrid(sb, pipeline, cols, ch.StartedAt, to, opt.Grid, t => Fmt.Hms(t - ch.StartedAt));
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static List<SeriesKey> ColumnsOf(ISampleSource pipeline, List<int> channels, ExportOptions opt)
        => pipeline.Keys
            .Where(k => channels.Contains(k.Channel))
            .Where(k => opt.Tags.Count == 0 || opt.Tags.Contains(k.Tag))
            .OrderBy(k => k.Channel).ThenBy(k => k.Tag, StringComparer.Ordinal)
            .ToList();

    private static string Header(ISampleSource pipeline, string tag)
    {
        var td = pipeline.Tag(tag);
        return td is null ? tag : (string.IsNullOrEmpty(td.Unit) ? td.DisplayName : $"{td.DisplayName}({td.Unit})");
    }

    private static void WriteGrid(StringBuilder sb, ISampleSource pipeline, List<SeriesKey> cols,
                                  DateTimeOffset from, DateTimeOffset to, TimeSpan grid,
                                  Func<DateTimeOffset, string> stamp)
    {
        if (grid <= TimeSpan.Zero) grid = TimeSpan.FromSeconds(5);
        var data = cols.Select(c => pipeline.Snapshot(c.Channel, c.Tag)).ToList();
        var cursor = new int[cols.Count];
        var last = new double?[cols.Count];
        var lastAt = new DateTimeOffset?[cols.Count];

        for (var t = from; t <= to; t += grid)
        {
            var cells = new string[cols.Count];
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
                cells[c] = lastAt[c] is { } at && at > t - grid && last[c] is { } v
                    ? v.ToString("G6", CultureInfo.InvariantCulture)
                    : "";
            }
            sb.AppendLine(string.Join(Sep, new[] { stamp(t) }.Concat(cells)));
        }
    }

    private static string Esc(string s)
    {
        if (s.Length == 0) return s;
        if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}
