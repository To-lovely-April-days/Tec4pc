using System.Globalization;
using ClosedXML.Excel;
using Tec.LimitTest.Runs;

namespace Tec.LimitTest.Rate;

/// <summary>报告表头那几样：测的是哪台、谁测的、什么条件。没有的留空。</summary>
public sealed record RateHeader
{
    public DateTimeOffset Date { get; init; }
    public string Device { get; init; } = "";
    public string Operator { get; init; } = "";
    public double? Ambient { get; init; }
    /// <summary>冷却水温度（手填，降温才有）。</summary>
    public double? Water { get; init; }
    public string Material { get; init; } = "";
    public string Patch { get; init; } = "";
    public double? LimitedA { get; init; }
    public double? LimitedB { get; init; }
    /// <summary>TEC 接入上限（台面配置）。</summary>
    public double? Threshold { get; init; }
}

/// <summary>
/// 【本地改动】（2026-11-17，升降温速率测试）速率测试的 Excel 报告。每做完一项整本重写一遍（数据在内存里，几分钟的量）：
/// · 「汇总」：测试条件 + 每项每个工位一行（起点、满功率 N s 后、升 / 降了多少、合格线、结论、升够用时、最快 10 s、输出开满占比、
///   平均输出 / 电流、热源、釜内起止、夹套与釜内最大温差、停后还冲多少、下令到接上用时、备注），合格绿、不合格红、判不了灰；
/// · 每项一张「每秒数据」：0 s = 这一路接上满功率那一刻，0 ~ N s 满功率、之后归零，两路各一组列；右边一张夹套（和釜内）温度曲线。
/// 数都是程序从每秒记录算好填进去的（<see cref="RateMath"/>），表里不放公式；算不出来的格子留空，不编。
/// 先写临时文件、加曲线，再原子替换——写到一半断电，上一版还在。
/// </summary>
public static class RateReport
{
    public const string SummarySheet = "汇总";

    private static readonly XLColor HdrFill = XLColor.FromHtml("#4A4A4A");
    private static readonly XLColor Grey = XLColor.FromHtml("#BFBFBF");
    private static readonly XLColor PassFill = XLColor.FromHtml("#C8E6C9");
    private static readonly XLColor FailFill = XLColor.FromHtml("#FFCDD2");
    private static readonly XLColor NaFill = XLColor.FromHtml("#EEEEEE");
    private static readonly XLColor LabelFill = XLColor.FromHtml("#E8EEF4");

    /// <summary>汇总表结果区的列（测试用例按名字找列，表的样子改了不用改一堆列号）。</summary>
    public static readonly string[] SummaryColumns =
    {
        "测试", "工位", "结论", "起点 ℃", "满功率到时 ℃", "升 / 降了 ℃", "合格线 ℃", "升 / 降够合格线用时 s",
        "最快 10 s 折合 ℃/min", "输出开满占比 %", "平均输出 %", "平均电流 A", "热源", "釜内 起点 ℃", "釜内 到时 ℃",
        "夹套−釜内 最大温差 ℃", "停后还冲 ℃", "停后极值 ℃", "极值在停后 s", "下令到接上 s", "满功率实际 s", "备注"
    };

    /// <summary>每秒数据表每一路的列（A、B 各一组）。</summary>
    public static readonly string[] SecondColumns = { "阶段", "夹套 ℃", "夹套变化 ℃", "釜内 ℃", "输出 %", "电流 A", "热源" };

    /// <summary>每秒数据表：表头在第几行（数据从下一行起，第一行是 0 s）。</summary>
    public const int SecondHeaderRow = 4;

    public static void Write(string path, RateHeader h, IReadOnlyList<RateTest> tests, RateSettings s)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = Path.ChangeExtension(path, ".tmp.xlsx");    // ClosedXML 按扩展名认类型，临时名也得是 .xlsx
        var charts = new List<XlsxChart.Spec>();
        using (var wb = new XLWorkbook())
        {
            BuildSummary(wb, h, tests, s);
            foreach (var t in tests.Where(t => t.Traces.Any(tr => tr?.T0 is not null)))
                if (BuildSeconds(wb, t, s) is { } spec) charts.Add(spec);
            wb.SaveAs(tmp);
        }
        if (charts.Count > 0) XlsxChart.Add(tmp, charts);
        File.Move(tmp, path, overwrite: true);
    }

    // ── 汇总 ────────────────────────────────────────────────────────

    private static void BuildSummary(XLWorkbook wb, RateHeader h, IReadOnlyList<RateTest> tests, RateSettings s)
    {
        var ws = wb.Worksheets.Add(SummarySheet);
        ws.Cell(1, 1).Value = "满功率升 / 降温速率测试";
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 14;
        ws.Cell(2, 1).Value =
            $"做法：从当前温度直接开始，给夹套一个够不着的目标，输出顶在温控器「最大输出占空比」上（= 满功率）；每个工位从它接上满功率那一刻起算，" +
            $"满功率 {s.FullSeconds} s 后输出归零，再记 {s.AfterSeconds} s 看停了以后还冲多少。夹套温度按温控器采样时刻插值，最快 10 s 是满功率这段里按 1 s 滑窗取的最大值。" +
            $"「升 / 降了」取正数 = 往要的方向走了。";
        ws.Range(2, 1, 2, 16).Merge();
        ws.Cell(2, 1).Style.Alignment.WrapText = true;
        ws.Row(2).Height = 46;

        // 测试条件：两列一对（名 | 值），手填的标出来
        var cond = new (string Label, XLCellValue Value, string? Fmt)[]
        {
            ("日期", h.Date.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), null),
            ("设备", h.Device, null),
            ("操作人（手填）", h.Operator, null),
            ("环境温度 ℃（手填）", V(h.Ambient), "0.0"),
            ("冷却水温度 ℃（手填，降温用）", V(h.Water), "0.0"),
            ("釜内物料（手填）", h.Material, null),
            ("补丁版本", h.Patch, null),
            ("升温停止线 ℃", s.StopLine, "0.#"),
            ("最大输出占空比 A %（从温控器读）", V(h.LimitedA), "0.#"),
            ("最大输出占空比 B %（从温控器读）", V(h.LimitedB), "0.#"),
            ("TEC 接入上限 ℃（台面配置）", V(h.Threshold), "0.#"),
            ($"合格线：{s.FullSeconds} s 至少升 ℃", s.HeatPassK, "0.#"),
            ($"合格线：{s.FullSeconds} s 至少降 ℃", s.CoolPassK, "0.#"),
            ("满功率 s / 归零后再记 s", $"{s.FullSeconds} / {s.AfterSeconds}", null)
        };
        // 条件块跟下面结果区共用列：名占 3 列、值占 2 列（合并），左右两对（第 1 列起、第 7 列起），列宽按结果区定
        var row = 4;
        for (var i = 0; i < cond.Length; i++)
        {
            var r = row + i / 2;
            var c = 1 + (i % 2) * 6;
            var label = ws.Range(r, c, r, c + 2).Merge();
            label.Value = cond[i].Label;
            label.Style.Fill.BackgroundColor = LabelFill;
            label.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            label.Style.Border.OutsideBorderColor = Grey;
            label.Style.Font.FontSize = 10;
            var value = ws.Range(r, c + 3, r, c + 4).Merge();
            value.Value = cond[i].Value;
            if (cond[i].Fmt is { } f) value.Style.NumberFormat.Format = f;
            value.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
            value.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            value.Style.Border.OutsideBorderColor = Grey;
            value.Style.Font.FontSize = 10;
        }
        row += (cond.Length + 1) / 2 + 1;

        // 结果区
        var hdr = row;
        for (var c = 0; c < SummaryColumns.Length; c++) Hdr(ws.Cell(hdr, c + 1), SummaryColumns[c]);
        ws.Row(hdr).Height = 32;
        row++;
        foreach (var t in tests)
        {
            var any = false;
            for (var w = 0; w < 2; w++)
            {
                if (t.Traces[w] is not { } tr) continue;
                any = true;
                var m = RateMath.Compute(tr, s, t.State is RateState.Aborted or RateState.Skipped ? t.Note : null);
                var vals = new XLCellValue[]
                {
                    t.Title, tr.WellName, m.Verdict, V(m.Start), V(m.End), V(m.Change), m.PassK, V(m.TimeToPass),
                    V(m.Fastest10s), V(m.FullOutPercent), V(m.MeanOut), V(m.MeanCur), m.Source ?? "", V(m.TrStart), V(m.TrEnd),
                    V(m.MaxJacketReactorGap), V(m.Overrun), V(m.AfterPeak), V(m.AfterPeakSeconds), V(m.ConnectSeconds), V(m.RanSeconds),
                    Join(m.Note, t.State is RateState.Aborted or RateState.Skipped ? $"这一项{t.StateText}：{t.Note}" : null)
                };
                var fmts = new[] { null, null, null, "0.0", "0.0", "0.0", "0.#", "0.0", "0.0", "0", "0.0", "0.00", null, "0.00", "0.00", "0.0", "0.0", "0.0", "0", "0.0", "0.0", null };
                for (var c = 0; c < vals.Length; c++)
                {
                    var cell = Box(ws.Cell(row, c + 1));
                    cell.Value = vals[c];
                    if (fmts[c] is { } f) cell.Style.NumberFormat.Format = f;
                    cell.Style.Alignment.Horizontal = c == vals.Length - 1 ? XLAlignmentHorizontalValues.Left : XLAlignmentHorizontalValues.Center;
                }
                var verdict = ws.Cell(row, 3);
                verdict.Style.Font.Bold = true;
                verdict.Style.Fill.BackgroundColor = m.Verdict == "合格" ? PassFill : m.Verdict == "不合格" ? FailFill : NaFill;
                row++;
            }
            if (!any)
            {
                Box(ws.Cell(row, 1)).Value = t.Title;
                Box(ws.Cell(row, 3)).Value = t.StateText;
                Box(ws.Cell(row, SummaryColumns.Length)).Value = t.Note;
                row++;
            }
        }
        ws.Column(1).Width = 14;
        ws.Column(2).Width = 6;
        ws.Column(3).Width = 9;
        for (var c = 4; c < SummaryColumns.Length; c++) ws.Column(c).Width = 11;
        ws.Column(13).Width = 8;
        ws.Column(SummaryColumns.Length).Width = 60;
        ws.SheetView.FreezeRows(hdr);
    }

    // ── 每秒数据 ────────────────────────────────────────────────────

    private static XlsxChart.Spec? BuildSeconds(XLWorkbook wb, RateTest t, RateSettings s)
    {
        var ws = wb.Worksheets.Add(t.SheetName);
        ws.Cell(1, 1).Value = $"{t.Title} · 每秒数据（夹套目标 {(t.Target is { } g ? g.ToString("0.#", CultureInfo.InvariantCulture) + " ℃，够不着，输出顶满" : "—")}）";
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 12;
        var notes = new List<string>();
        foreach (var tr in t.Traces)
            if (tr is { T0: { } t0, CommandAt: { } c })
                notes.Add($"工位 {tr.WellName} 下令后 {(t0 - c).TotalSeconds:0.0} s 接上（{t0.ToLocalTime():HH:mm:ss}）" +
                          (tr.EarlyStop is { } es ? $"，{es}" : ""));
        ws.Cell(2, 1).Value = $"每个工位的 0 s = 它接上满功率那一刻；0 ~ {s.FullSeconds} s 满功率，之后输出归零。" +
                              "夹套按温控器采样时刻插值到整秒，其余是离整秒最近的那次读数。" + string.Join("；", notes);
        ws.Range(2, 1, 2, 15).Merge();
        ws.Cell(2, 1).Style.Alignment.WrapText = true;
        ws.Row(2).Height = 32;

        Hdr(ws.Cell(SecondHeaderRow, 1), "秒");
        var col = 2;
        var wells = Enumerable.Range(0, 2).Where(w => t.Traces[w]?.T0 is not null).ToList();
        var tjCol = new Dictionary<int, int>();
        var trCol = new Dictionary<int, int>();
        foreach (var w in wells)
        {
            var name = w == 0 ? "A" : "B";
            for (var i = 0; i < SecondColumns.Length; i++)
            {
                Hdr(ws.Cell(SecondHeaderRow, col + i), $"{name} {SecondColumns[i]}");
                if (SecondColumns[i] == "夹套 ℃") tjCol[w] = col + i;
                if (SecondColumns[i] == "釜内 ℃") trCol[w] = col + i;
            }
            col += SecondColumns.Length;
        }
        ws.Row(SecondHeaderRow).Height = 30;

        var total = s.FullSeconds + s.AfterSeconds;
        var hasTr = new HashSet<int>();
        double lo = double.MaxValue, hi = double.MinValue;
        var xs = new double?[total + 1];
        var tjVals = wells.ToDictionary(w => w, _ => new double?[total + 1]);
        var trVals = wells.ToDictionary(w => w, _ => new double?[total + 1]);
        var tjSeries = wells.ToDictionary(w => w, w => RateMath.TjSeries(t.Traces[w]!));
        for (var k = 0; k <= total; k++)
        {
            var r = SecondHeaderRow + 1 + k;
            Box(ws.Cell(r, 1)).Value = k;
            xs[k] = k;
            col = 2;
            foreach (var w in wells)
            {
                var tr = t.Traces[w]!;
                var t0 = tr.T0!.Value;
                var at = t0 + TimeSpan.FromSeconds(k);
                var series = tjSeries[w];
                var tj = RateMath.At(series, at);
                var v0 = RateMath.At(series, t0);
                var near = RateMath.Near(tr, at);
                tjVals[w][k] = tj is { } q ? Math.Round(q, 4) : null;
                trVals[w][k] = near?.Tr;
                var stopped = tr.StopAt is { } sa && at > sa;
                var last = series.Count > 0 ? series[^1].T : t0;
                var phase = at > last + TimeSpan.FromSeconds(1.5) ? "" : stopped ? "归零后" : "满功率";
                var vals = new XLCellValue[]
                {
                    phase, V(tj), tj is { } a && v0 is { } b ? a - b : Blank.Value, V(near?.Tr), V(near?.Out), V(near?.Cur), near?.Source ?? ""
                };
                var fmts = new[] { null, "0.00", "0.00", "0.00", "0.0", "0.000", null };
                for (var i = 0; i < vals.Length; i++)
                {
                    var cell = Box(ws.Cell(r, col + i));
                    cell.Value = vals[i];
                    if (fmts[i] is { } f) cell.Style.NumberFormat.Format = f;
                }
                if (phase == "归零后") ws.Cell(r, col).Style.Font.FontColor = XLColor.Gray;
                if (tj is { } x) { lo = Math.Min(lo, x); hi = Math.Max(hi, x); }
                if (near?.Tr is { } y) { hasTr.Add(w); lo = Math.Min(lo, y); hi = Math.Max(hi, y); }
                col += SecondColumns.Length;
            }
        }
        ws.Column(1).Width = 6;
        for (var c = 2; c < col; c++) ws.Column(c).Width = 9.5;
        ws.SheetView.FreezeRows(SecondHeaderRow);

        if (lo > hi) return null;     // 一个数都没有：不画
        var first = SecondHeaderRow + 1;
        var lastRow = first + total;
        var series2 = new List<XlsxChart.Series>();
        foreach (var w in wells)
            series2.Add(new XlsxChart.Series($"{(w == 0 ? "A" : "B")} 夹套", tjCol[w], w == 0 ? "C62828" : "1565C0", Dashed: false)
                        { Values = tjVals[w] });
        foreach (var w in wells.Where(hasTr.Contains))
            series2.Add(new XlsxChart.Series($"{(w == 0 ? "A" : "B")} 釜内", trCol[w], w == 0 ? "EF9A9A" : "90CAF9", Dashed: true)
                        { Values = trVals[w] });
        var yMin = Math.Floor((lo - 2) / 5) * 5;
        var yMax = Math.Ceiling((hi + 2) / 5) * 5;
        return new XlsxChart.Spec(
            Sheet: t.SheetName,
            Title: $"{t.Title}：夹套温度（0 ~ {s.FullSeconds} s 满功率，之后归零）",
            XTitle: "s（从接上满功率起）",
            YTitle: "℃",
            XCol: 1, FirstRow: first, LastRow: lastRow,
            Series: series2,
            XMax: total, XMajor: 10, YMin: yMin, YMax: yMax,
            AnchorCol: col, AnchorRow: SecondHeaderRow - 1, AnchorCols: 12, AnchorRows: 24)
        { X = xs };
    }

    // ── 小工具 ──────────────────────────────────────────────────────

    private static XLCellValue V(double? d) => d is { } x && !double.IsNaN(x) ? Math.Round(x, 4) : Blank.Value;

    private static string Join(string a, string? b)
        => string.IsNullOrEmpty(b) ? a : string.IsNullOrEmpty(a) ? b : a + "；" + b;

    private static IXLCell Box(IXLCell c)
    {
        c.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        c.Style.Border.OutsideBorderColor = Grey;
        c.Style.Font.FontSize = 10;
        c.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        return c;
    }

    private static void Hdr(IXLCell c, string text)
    {
        Box(c).Value = text;
        c.Style.Font.Bold = true;
        c.Style.Font.FontColor = XLColor.White;
        c.Style.Fill.BackgroundColor = HdrFill;
        c.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        c.Style.Alignment.WrapText = true;
    }
}
