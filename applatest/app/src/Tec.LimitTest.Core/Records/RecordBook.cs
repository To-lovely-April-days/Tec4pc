using System.Globalization;
using ClosedXML.Excel;

namespace Tec.LimitTest.Records;

/// <summary>三项极限测试。顺序就是同一个水温下自动串着跑的顺序。</summary>
public enum TestKind { MinTemp, MaxTemp, Hold }

public static class TestKinds
{
    public static readonly TestKind[] All = { TestKind.MinTemp, TestKind.MaxTemp, TestKind.Hold };

    public static string Name(TestKind k) => k switch
    {
        TestKind.MinTemp => "最低温",
        TestKind.MaxTemp => "最高温",
        _ => "恒温稳定性"
    };

    /// <summary>表名里用的短名（表名有 31 字上限，还要塞水温）。</summary>
    public static string Short(TestKind k) => k == TestKind.Hold ? "恒温" : Name(k);
}

/// <summary>工作簿的规格：几个水温、每张表多少行、恒温表的记录间隔。建簿时定死，之后只填。</summary>
public sealed record BookSpec
{
    /// <summary>「最高温不用冷却水、只测一次」那张表的水温键（NaN = 无冷却水）。</summary>
    public const double NoWater = double.NaN;

    /// <summary>冷却水温度分组（℃）：最低温、恒温各在每个水温下测一遍。推荐 20 / 12 / 7（不低于 7，用户定的）。</summary>
    public double[] Waters { get; init; } = { 20, 12, 7 };

    /// <summary>最高温只测一次、不用冷却水（升温靠加热棒，跟水温无关——用户定的）。false = 也按水温各测一遍。</summary>
    public bool MaxOnce { get; init; } = true;

    /// <summary>某一项要测的水温键列表：最高温只测一次时就是 [NaN]。</summary>
    public IEnumerable<double> WatersOf(TestKind k) => k == TestKind.MaxTemp && MaxOnce ? new[] { NoWater } : Waters;
    /// <summary>最低温表的行数（1 min 一行，含 t=0）。</summary>
    public int MinRows { get; init; } = 61;
    public int MaxRows { get; init; } = 61;
    /// <summary>恒温表的行数（HoldStepMin 一行，含 t=0）。</summary>
    public int HoldRows { get; init; } = 61;
    public int HoldStepMin { get; init; } = 2;

    public int RowsOf(TestKind k) => k switch
    {
        TestKind.MinTemp => MinRows,
        TestKind.MaxTemp => MaxRows,
        _ => HoldRows
    };

    public int StepOf(TestKind k) => k == TestKind.Hold ? HoldStepMin : 1;
}

/// <summary>一张测试表的坐标：条件格、数据列、汇总格各在哪。填表和结果汇总都按它找，不靠写死的 $B$6。</summary>
public sealed class SheetLayout
{
    public required string Name { get; init; }
    public required TestKind Kind { get; init; }
    public required double Water { get; init; }
    public int Step { get; init; }
    public int HeaderRow { get; internal set; }
    public int FirstRow { get; internal set; }
    public int LastRow { get; internal set; }
    /// <summary>条件块：键 → 值格。</summary>
    public Dictionary<string, (int Row, int Col)> Cond { get; } = new(StringComparer.Ordinal);
    /// <summary>数据列：键 → 列号。</summary>
    public Dictionary<string, int> Col { get; } = new(StringComparer.Ordinal);
    /// <summary>汇总块：键 → 值格。</summary>
    public Dictionary<string, (int Row, int Col)> Sum { get; } = new(StringComparer.Ordinal);

    public string CondAddr(string key) => Abs(Cond[key]);
    public string SumAddr(string key) => Abs(Sum[key]);
    public string ColLetter(string key) => XLHelper.GetColumnLetterFromNumber(Col[key]);

    private static string Abs((int Row, int Col) c) => $"${XLHelper.GetColumnLetterFromNumber(c.Col)}${c.Row}";
}

/// <summary>
/// 「控温极限测试记录」工作簿：3 项 × 3 水温 = 9 张表 + 结果汇总矩阵（用户定的结构）。
///
/// 规矩：
/// · 公式留在表里由 Excel 算，程序**只往黄格里填采到的数**——没采到的格子留空，不编；
/// · 条件块、数据列、汇总格的坐标都记在 <see cref="SheetLayout"/> 里，填表按键找格，
///   结果汇总的公式也按它引用，表的样子改了不用跟着改一堆 $B$6；
/// · 每次 <see cref="Save"/> 先写临时文件再原子替换——写到一半断电，上一版还在。
///
/// 从前那份手填模板（openpyxl 出的）的列与公式在这里照搬过来，再加了釜内温度、热源两列。
/// </summary>
public sealed class RecordBook : IDisposable
{
    public const string ReadmeSheet = "说明";
    public const string SummarySheet = "结果汇总";

    // 条件块的键（Runner 与测试按它填）
    public const string CDate = "date", COperator = "operator", CAmbient = "ambient", CWater = "water", CObject = "object",
                        CPatch = "patch", CResult = "result", CNote = "note",
                        CTargetA = "targetA", CTargetB = "targetB", CMode = "mode",
                        CLimitedA = "limitedA", CLimitedB = "limitedB",
                        CThreshold = "threshold", COverA = "overA", COverB = "overB",
                        CElecA = "elecA", CElecB = "elecB",
                        CTarget = "target", CBand = "band", CReachA = "reachA", CReachB = "reachB";

    // 数据列的键
    public const string KTime = "t", KNote = "note",
                        KATj = "aTj", KAOut = "aOut", KACur = "aCur", KATr = "aTr", KASrc = "aSrc",
                        KBTj = "bTj", KBOut = "bOut", KBCur = "bCur", KBTr = "bTr", KBSrc = "bSrc",
                        KRelay = "relay";

    private const string FontName = "Arial";
    private static readonly XLColor Yellow = XLColor.FromHtml("#FFFF00");
    private static readonly XLColor Blue = XLColor.FromHtml("#0000FF");
    private static readonly XLColor HdrFill = XLColor.FromHtml("#4A4A4A");
    private static readonly XLColor SumFill = XLColor.FromHtml("#E8EEF4");
    private static readonly XLColor Grey = XLColor.FromHtml("#BFBFBF");
    private static readonly XLColor NoteGrey = XLColor.FromHtml("#666666");

    private const string FmtC = "0.0";      // 夹套温度
    private const string FmtTr = "0.00";    // 釜内温度（宇电两位小数）
    private const string FmtP = "0.0";      // 输出百分数
    private const string FmtA = "0.000";    // 电流

    private readonly XLWorkbook _wb = new();
    private readonly Dictionary<string, SheetLayout> _layouts = new(StringComparer.Ordinal);

    public string Path { get; }
    public BookSpec Spec { get; }

    public IReadOnlyCollection<SheetLayout> Layouts => _layouts.Values;

    /// <summary>建一本新簿并写盘。path 已存在会被覆盖——调用方自己起带时间戳的名。</summary>
    public RecordBook(string path, BookSpec? spec = null)
    {
        Path = path;
        Spec = spec ?? new BookSpec();
        BuildReadme();
        foreach (var kind in TestKinds.All)
            foreach (var water in Spec.WatersOf(kind))
                BuildTestSheet(kind, water);
        BuildSummary();
        // 打开时全算一遍：程序不算公式，簿里没有缓存值
        _wb.CalculateMode = XLCalculateMode.Auto;
        _wb.FullCalculationOnLoad = true;
        Save();
    }

    public static string SheetName(TestKind kind, double water)
        => double.IsNaN(water)
            ? $"{TestKinds.Short(kind)}（无冷却水）"
            : $"{TestKinds.Short(kind)} {water.ToString("0.#", CultureInfo.InvariantCulture)}℃";

    /// <summary>水温键的人话：「20 ℃」/「无冷却水」。</summary>
    public static string WaterText(double water)
        => double.IsNaN(water) ? "无冷却水" : water.ToString("0.#", CultureInfo.InvariantCulture) + " ℃";

    public SheetLayout Layout(TestKind kind, double water) => _layouts[SheetName(kind, water)];

    public bool HasSheet(TestKind kind, double water) => _layouts.ContainsKey(SheetName(kind, water));

    /// <summary>填条件块的一格。键不认识就抛——填错地方比不填更糟。</summary>
    public void SetCondition(TestKind kind, double water, string key, XLCellValue value)
    {
        var lay = Layout(kind, water);
        if (!lay.Cond.TryGetValue(key, out var at))
            throw new ArgumentException($"「{lay.Name}」没有条件项 {key}", nameof(key));
        _wb.Worksheet(lay.Name).Cell(at.Row, at.Col).Value = value;
    }

    public XLCellValue GetCondition(TestKind kind, double water, string key)
    {
        var lay = Layout(kind, water);
        var at = lay.Cond[key];
        return _wb.Worksheet(lay.Name).Cell(at.Row, at.Col).Value;
    }

    /// <summary>
    /// 写第 index 行（0 起，时间列已按 index × 间隔预填）。values 按列键给，不认识的键跳过；
    /// 超出表的行数返回 false（表是按计划时长建的，跑过头的数只进 csv）。
    /// </summary>
    public bool WriteRow(TestKind kind, double water, int index, IReadOnlyDictionary<string, XLCellValue> values)
    {
        var lay = Layout(kind, water);
        if (index < 0 || index >= Spec.RowsOf(kind)) return false;
        var ws = _wb.Worksheet(lay.Name);
        var r = lay.FirstRow + index;
        foreach (var (k, v) in values)
            if (lay.Col.TryGetValue(k, out var c)) ws.Cell(r, c).Value = v;
        return true;
    }

    /// <summary>填汇总块里由程序算的那几格（1 s 数据算的稳定度那种，Excel 里没有原始数据算不出来）。</summary>
    public void SetSummary(TestKind kind, double water, string key, XLCellValue value)
    {
        var lay = Layout(kind, water);
        if (!lay.Sum.TryGetValue(key, out var at))
            throw new ArgumentException($"「{lay.Name}」没有汇总项 {key}", nameof(key));
        _wb.Worksheet(lay.Name).Cell(at.Row, at.Col).Value = value;
    }

    public XLCellValue GetSummary(TestKind kind, double water, string key)
    {
        var lay = Layout(kind, water);
        var at = lay.Sum[key];
        return _wb.Worksheet(lay.Name).Cell(at.Row, at.Col).Value;
    }

    public XLCellValue GetCell(TestKind kind, double water, int index, string colKey)
    {
        var lay = Layout(kind, water);
        return _wb.Worksheet(lay.Name).Cell(lay.FirstRow + index, lay.Col[colKey]).Value;
    }

    /// <summary>先写临时文件再原子替换。</summary>
    public void Save()
    {
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        // ClosedXML 按扩展名认文件类型，临时名也得是 .xlsx
        var tmp = System.IO.Path.ChangeExtension(Path, ".tmp.xlsx");
        _wb.SaveAs(tmp);
        File.Move(tmp, Path, overwrite: true);
    }

    public void Dispose() => _wb.Dispose();

    // ── 样式 ──────────────────────────────────────────────────────────

    private static IXLCell Box(IXLCell c)
    {
        c.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        c.Style.Border.OutsideBorderColor = Grey;
        c.Style.Font.FontName = FontName;
        c.Style.Font.FontSize = 10;
        return c;
    }

    private static IXLCell Center(IXLCell c)
    {
        c.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        c.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        c.Style.Alignment.WrapText = true;
        return c;
    }

    private static IXLCell Hdr(IXLWorksheet ws, int row, int col, string text, double? width = null)
    {
        var c = Center(Box(ws.Cell(row, col)));
        c.Value = text;
        c.Style.Font.Bold = true;
        c.Style.Font.FontColor = XLColor.White;
        c.Style.Fill.BackgroundColor = HdrFill;
        if (width is { } w) ws.Column(col).Width = w;
        return c;
    }

    /// <summary>黄底蓝字 = 数据格（程序填；没采到的留空给人补）。</summary>
    private static IXLCell Inp(IXLCell c, string? fmt = null, string? comment = null)
    {
        Center(Box(c));
        c.Style.Font.FontColor = Blue;
        c.Style.Fill.BackgroundColor = Yellow;
        if (fmt is not null) c.Style.NumberFormat.Format = fmt;
        if (comment is not null) c.CreateComment().AddText(comment);
        return c;
    }

    private static IXLCell Lbl(IXLCell c, string text, bool bold = false)
    {
        Box(c);
        c.Value = text;
        c.Style.Font.Bold = bold;
        c.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
        c.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        c.Style.Alignment.WrapText = true;
        return c;
    }

    /// <summary>黑字浅蓝底 = 公式。</summary>
    private static IXLCell Fml(IXLCell c, string formula, string? fmt = null, string? comment = null)
    {
        Center(Box(c));
        c.FormulaA1 = formula;
        c.Style.Fill.BackgroundColor = SumFill;
        if (fmt is not null) c.Style.NumberFormat.Format = fmt;
        if (comment is not null) c.CreateComment().AddText(comment);
        return c;
    }

    private static void Note(IXLWorksheet ws, int row, int col, string text)
    {
        var c = ws.Cell(row, col);
        c.Value = text;
        c.Style.Font.FontName = FontName;
        c.Style.Font.FontSize = 9;
        c.Style.Font.Italic = true;
        c.Style.Font.FontColor = NoteGrey;
    }

    private static void Title(IXLWorksheet ws, string text, string sub)
    {
        ws.Cell(1, 1).Value = text;
        ws.Cell(1, 1).Style.Font.FontName = FontName;
        ws.Cell(1, 1).Style.Font.FontSize = 14;
        ws.Cell(1, 1).Style.Font.Bold = true;
        Note(ws, 2, 1, sub);
    }

    // ── 说明 ──────────────────────────────────────────────────────────

    private void BuildReadme()
    {
        var ws = _wb.Worksheets.Add(ReadmeSheet);
        ws.Column(1).Width = 4;
        ws.Column(2).Width = 110;
        Title(ws, "控温极限测试记录", "双工位反应主机 · 由「控温极限测试工具」自动生成、自动填；汇总表自动算");
        var lines = new[]
        {
            "这份表怎么来的",
            "  工具按「最低温、恒温稳定性 × 冷却水 20 / 12 / 7 ℃」自动跑，每项每个水温一张表；最高温不用冷却水、只测一次，单独一张；最后一张「结果汇总」是矩阵。",
            "  控温对象（夹套 / 釜内）在工具里选，每张表的条件块里写着；釜内是上位机串级（外环吃宇电的 Tr），要接了探头才做得了。",
            "  黄底蓝字：数据格，工具每到一个记录点写一行（最低温 / 最高温每 1 min，恒温每 2 min）。没采到的格子留空，工具不编数——空着的可以事后手填。",
            "  黑字浅蓝底：公式，Excel 打开时算，别手改。恒温表汇总里「1 s」字样的几格是工具用每秒数据算好填进去的值（Excel 里没有每秒数据）。",
            "哪些是人填的",
            "  操作人、环境温度、补丁版本：开始前在工具里填一次，每张表都带上。",
            "  冷却水温度：工具在每一组水温开始前停下来问你，冷水机上看到多少填多少（手填，不是测的）。",
            "  备注：随便写。",
            "数据从哪来",
            "  夹套温度 / 输出 / 热源 / TEC 功率线：驱动每秒采样，取记录点那一刻的值。",
            "  釜内温度：宇电探头会话发的 Tr；没接探头这一列就是空的。",
            "  电流：温控器「实时状态」里的 CURRENT，每 5 s 读一次，取记录点前最近一次。",
            "  「切到电加热的时刻」「继电器动作次数」：从热源 / 功率线状态量的跳变数出来的，不靠人看灯。",
            "  每秒的全量数据在同目录的 csv 文件夹里，一次测试一个文件；表里只放记录点那一行。",
            "结果那一格",
            "  完成 / 提前结束（夹套 10 min 内变化不到 0.2 ℃）/ 中止（原因：操作人停止、急停、安全层触发、读数丢失……）。中止之前写的行都在。",
            "对应清单",
            "  docs/极限测试清单.md 第三块「控温极限」⑯ 最低温、⑰ 最高温、⑳ 长时间恒温。",
        };
        var r = 4;
        foreach (var t in lines)
        {
            var c = ws.Cell(r, 2);
            c.Value = t;
            c.Style.Font.FontName = FontName;
            c.Style.Font.FontSize = 10;
            c.Style.Font.Bold = !t.StartsWith("  ", StringComparison.Ordinal);
            c.Style.Alignment.WrapText = true;
            r++;
        }
        r++;
        ws.Cell(r, 2).Value = "图例";
        ws.Cell(r, 2).Style.Font.Bold = true;
        r++;
        Inp(ws.Cell(r, 2)).Value = "黄底蓝字 = 数据格（工具填，空着的可手填）";
        ws.Cell(r, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
        r++;
        var f = ws.Cell(r, 2);
        f.Value = "黑字浅蓝底 = 公式";
        f.Style.Fill.BackgroundColor = SumFill;
        f.Style.Font.FontName = FontName;
        f.Style.Font.FontSize = 10;
    }

    // ── 测试表 ────────────────────────────────────────────────────────

    private sealed record CondItem(string Key, string Label, XLCellValue Default, string? Fmt, string? Comment);
    private sealed record ColItem(string Key, string Header, double Width, string? Fmt);
    private sealed record HelperItem(string Key, string Header, Func<int, string> Formula);
    private sealed record SumItem(string? Key, string Label, Func<Func<string, string>, string>? Formula, string? Fmt, string? Comment);

    private static string SubOf(TestKind k) => k switch
    {
        TestKind.MinTemp => "清单 ⑯：两工位同时「尽快」降到目标，看能到多低、多快、电流多大、两路同时满载电源顶不顶得住",
        TestKind.MaxTemp => "清单 ⑰：两工位同时「尽快」升到目标（电加热段），看切电加热顺不顺、到温过冲多少",
        _ => "清单 ⑳：到达目标后恒温，看 Tj / Tr 的波动和继电器有没有抖"
    };

    private static List<CondItem> CondItems(TestKind kind, double water)
    {
        var list = new List<CondItem>
        {
            new(CDate, "日期", Blank.Value, "yyyy-mm-dd", "工具填：测试当天"),
            new(COperator, "操作人", Blank.Value, null, "工具里填的"),
            new(CAmbient, "环境温度 ℃", Blank.Value, FmtC, "工具里填的（手填）"),
            double.IsNaN(water)
                ? new(CWater, "冷却水", "无（升温不用冷却水）", null, "最高温只测一次、不用冷却水（用户定的）")
                : new(CWater, "冷却水温度 ℃（手填）", Blank.Value, FmtC,
                      $"这一组按 {water:0.#} ℃ 做；工具在开始前问你，冷水机上看到多少填多少——不是测的"),
            new(CObject, "控温对象", Blank.Value, null, "工具填：夹套（单环）/ 釜内（串级，外环吃宇电的 Tr）"),
        };
        switch (kind)
        {
            case TestKind.MinTemp:
                list.Add(new(CTargetA, "工位 A 目标 ℃", Blank.Value, FmtC, "工具填：下发的夹套目标"));
                list.Add(new(CTargetB, "工位 B 目标 ℃", Blank.Value, FmtC, null));
                list.Add(new(CMode, "到达方式", "尽快", null, "这张表按「尽快」做：目标一步写给温控器"));
                list.Add(new(CLimitedA, "A 最大功率 LIMITED %", Blank.Value, FmtP, "工具从温控器读的（参数窗「最大输出占空比」）"));
                list.Add(new(CLimitedB, "B 最大功率 LIMITED %", Blank.Value, FmtP, null));
                break;
            case TestKind.MaxTemp:
                list.Add(new(CThreshold, "电加热切换阈值 ℃", Blank.Value, FmtC, "工具从台面配置读的"));
                list.Add(new(CTargetA, "工位 A 目标 ℃", Blank.Value, FmtC, "工具填：下发的夹套目标"));
                list.Add(new(CTargetB, "工位 B 目标 ℃", Blank.Value, FmtC, null));
                list.Add(new(CMode, "到达方式", "尽快", null, null));
                list.Add(new(COverA, "A 超温上限 ℃", Blank.Value, FmtC, "工具从温控器读的（OVERTEMPUP），温控器自己那道最后防线"));
                list.Add(new(COverB, "B 超温上限 ℃", Blank.Value, FmtC, null));
                list.Add(new(CElecA, "A 切到电加热的时刻 min", Blank.Value, "0.0", "工具从热源状态量的跳变数出来的；一直没切显示空"));
                list.Add(new(CElecB, "B 切到电加热的时刻 min", Blank.Value, "0.0", null));
                break;
            default:
                list.Add(new(CTarget, "恒温目标 ℃", Blank.Value, FmtC, "工具填：两工位同一个目标"));
                list.Add(new(CBand, "热源死区 K", Blank.Value, "0.0", "工具从台面配置读的（「升降温死区」，0333 前叫「热源切换死区」）"));
                list.Add(new(CReachA, "A 到达用时 min", Blank.Value, "0.0", "下发到夹套首次进 ±0.5 ℃ 的用时；恒温记录从两路都到之后开始"));
                list.Add(new(CReachB, "B 到达用时 min", Blank.Value, "0.0", null));
                break;
        }
        list.Add(new(CPatch, "补丁版本", Blank.Value, null, "打到哪一号"));
        list.Add(new(CResult, "结果", Blank.Value, null, "工具填：完成 / 提前结束（原因）/ 中止（原因）"));
        list.Add(new(CNote, "备注", Blank.Value, null, null));
        return list;
    }

    private static List<ColItem> ColsOf(TestKind kind) => kind == TestKind.Hold
        ? new List<ColItem>
        {
            new(KTime, "时间 min", 10, "0"),
            new(KATj, "A Tj ℃", 11, FmtC), new(KATr, "A Tr ℃", 11, FmtTr), new(KAOut, "A 输出 %", 10, FmtP),
            new(KBTj, "B Tj ℃", 11, FmtC), new(KBTr, "B Tr ℃", 11, FmtTr), new(KBOut, "B 输出 %", 10, FmtP),
            new(KRelay, "继电器动作 次", 12, "0"),
            new(KNote, "备注", 22, null)
        }
        : new List<ColItem>
        {
            new(KTime, "时间 min", 10, "0"),
            new(KATj, "A 夹套 ℃", 11, FmtC), new(KAOut, "A 输出 %", 10, FmtP), new(KACur, "A 电流 A", 10, FmtA),
            new(KATr, "A 釜内 ℃", 11, FmtTr), new(KASrc, "A 热源", 9, null),
            new(KBTj, "B 夹套 ℃", 11, FmtC), new(KBOut, "B 输出 %", 10, FmtP), new(KBCur, "B 电流 A", 10, FmtA),
            new(KBTr, "B 釜内 ℃", 11, FmtTr), new(KBSrc, "B 热源", 9, null),
            new(KNote, "备注", 22, null)
        };

    private void BuildTestSheet(TestKind kind, double water)
    {
        var name = SheetName(kind, water);
        var ws = _wb.Worksheets.Add(name);
        var lay = new SheetLayout { Name = name, Kind = kind, Water = water, Step = Spec.StepOf(kind) };
        _layouts[name] = lay;
        Title(ws, name, SubOf(kind));

        // 条件块：两对一行（标签 | 值(并两格)）
        var r = 4;
        var k = 0;
        foreach (var item in CondItems(kind, water))
        {
            var col = k % 2 == 0 ? 1 : 4;
            Lbl(ws.Cell(r, col), item.Label);
            ws.Range(r, col + 1, r, col + 2).Merge();
            var v = Inp(ws.Cell(r, col + 1), item.Fmt, item.Comment);
            if (!item.Default.IsBlank) v.Value = item.Default;
            Box(ws.Cell(r, col + 2));
            lay.Cond[item.Key] = (r, col + 1);
            k++;
            if (k % 2 == 0) r++;
        }
        if (k % 2 == 1) r++;
        if (kind == TestKind.MinTemp)
        {
            var at = lay.Cond[CMode];
            ws.Cell(at.Row, at.Col).CreateDataValidation().List("\"尽快,按速率,按时长\"", true);
        }
        else if (kind == TestKind.MaxTemp)
        {
            var at = lay.Cond[CMode];
            ws.Cell(at.Row, at.Col).CreateDataValidation().List("\"尽快,按速率,按时长\"", true);
        }

        var hdrRow = r + 1;
        var n = Spec.RowsOf(kind);
        lay.HeaderRow = hdrRow;
        lay.FirstRow = hdrRow + 1;
        lay.LastRow = hdrRow + n;

        var cols = ColsOf(kind);
        for (var i = 0; i < cols.Count; i++)
        {
            Hdr(ws, hdrRow, i + 1, cols[i].Header, cols[i].Width);
            lay.Col[cols[i].Key] = i + 1;
        }
        var helpers = HelpersOf(kind, lay);
        var hc0 = cols.Count + 1;
        for (var j = 0; j < helpers.Count; j++)
        {
            Hdr(ws, hdrRow, hc0 + j, helpers[j].Header, 10);
            lay.Col[helpers[j].Key] = hc0 + j;
        }

        for (var i = 0; i < n; i++)
        {
            var row = lay.FirstRow + i;
            Inp(ws.Cell(row, 1), "0").Value = i * lay.Step;
            for (var c = 2; c <= cols.Count; c++) Inp(ws.Cell(row, c), cols[c - 1].Fmt);
            for (var j = 0; j < helpers.Count; j++)
                Fml(ws.Cell(row, hc0 + j), helpers[j].Formula(row), "0");
        }

        // 汇总块：标签列 sc、值列 sc+1。先派行号再写公式——有的汇总项引用别的汇总项
        var sc = hc0 + helpers.Count + 1;
        ws.Column(sc).Width = 36;
        ws.Column(sc + 1).Width = 14;
        Hdr(ws, hdrRow, sc, "汇总（自动算）");
        Hdr(ws, hdrRow, sc + 1, "值");
        var sums = SummaryOf(kind, lay);
        var rr = lay.FirstRow;
        foreach (var s in sums)
        {
            if (s.Key is not null) lay.Sum[s.Key] = (rr, sc + 1);
            rr++;
        }
        rr = lay.FirstRow;
        foreach (var s in sums)
        {
            if (s.Key is not null)
            {
                Lbl(ws.Cell(rr, sc), s.Label);
                if (s.Formula is not null)
                    Fml(ws.Cell(rr, sc + 1), s.Formula(lay.SumAddr), s.Fmt, s.Comment);
                else
                    Inp(ws.Cell(rr, sc + 1), s.Fmt, s.Comment);     // 程序算好填的值格
            }
            rr++;
        }

        ws.SheetView.FreezeRows(hdrRow);
        ws.SheetView.FreezeColumns(1);
    }

    /// <summary>「到达?」辅助列：每行 1 / 0，汇总里 MATCH 第一个 1 拿到达用时。</summary>
    private static List<HelperItem> HelpersOf(TestKind kind, SheetLayout lay)
    {
        if (kind == TestKind.Hold) return new List<HelperItem>();
        var a = lay.ColLetter(KATj);
        var b = lay.ColLetter(KBTj);
        var tA = lay.CondAddr(CTargetA);
        var tB = lay.CondAddr(CTargetB);
        var op = kind == TestKind.MinTemp ? "<=" : ">=";
        var tol = kind == TestKind.MinTemp ? "+0.5" : "-0.5";
        return new List<HelperItem>
        {
            new("aHit", "A 到达?", r => $"IF(AND({a}{r}<>\"\",{tA}<>\"\"),IF({a}{r}{op}{tA}{tol},1,0),\"\")"),
            new("bHit", "B 到达?", r => $"IF(AND({b}{r}<>\"\",{tB}<>\"\"),IF({b}{r}{op}{tB}{tol},1,0),\"\")")
        };
    }

    private static List<SumItem> SummaryOf(TestKind kind, SheetLayout lay)
    {
        var r1 = lay.FirstRow;
        var r2 = lay.LastRow;
        var r10 = r1 + 10;   // t = 10 min 那一行（1 min 一行时）
        string Rng(string colKey) => $"{lay.ColLetter(colKey)}{r1}:{lay.ColLetter(colKey)}{r2}";
        string MinOf(string colKey) => $"IF(COUNT({Rng(colKey)})=0,\"\",MIN({Rng(colKey)}))";
        string MaxOf(string colKey) => $"IF(COUNT({Rng(colKey)})=0,\"\",MAX({Rng(colKey)}))";
        string AvgOf(string colKey) => $"IF(COUNT({Rng(colKey)})=0,\"\",AVERAGE({Rng(colKey)}))";
        string PpOf(string colKey) => $"IF(COUNT({Rng(colKey)})=0,\"\",MAX({Rng(colKey)})-MIN({Rng(colKey)}))";

        var list = new List<SumItem>();
        if (kind == TestKind.Hold)
        {
            var target = lay.CondAddr(CTarget);
            foreach (var (w, tj, tr, outp) in new[] { ("A", KATj, KATr, KAOut), ("B", KBTj, KBTr, KBOut) })
            {
                list.Add(new($"{w}TjAvg", $"{w} Tj 平均 ℃", _ => AvgOf(tj), "0.00", null));
                list.Add(new($"{w}TjPp", $"{w} Tj 峰峰值 ℃", _ => PpOf(tj), "0.00", null));
                list.Add(new($"{w}TjPm", $"{w} Tj 波动 ± ℃", _ => $"IF(COUNT({Rng(tj)})=0,\"\",(MAX({Rng(tj)})-MIN({Rng(tj)}))/2)", "0.00", null));
                list.Add(new($"{w}TjSd", $"{w} Tj 标准差 ℃", _ => $"IF(COUNT({Rng(tj)})<2,\"\",STDEV({Rng(tj)}))", "0.000", null));
                list.Add(new($"{w}TrPp", $"{w} Tr 峰峰值 ℃", _ => PpOf(tr), "0.00", null));
                list.Add(new($"{w}TrDiff", $"{w} Tr 平均与目标差 ℃",
                    _ => $"IF(OR(COUNT({Rng(tr)})=0,{target}=\"\"),\"\",AVERAGE({Rng(tr)})-{target})", "0.00", null));
                list.Add(new($"{w}OutAvg", $"{w} 输出平均 %", _ => AvgOf(outp), FmtP, null));
                list.Add(new(null, "", null, null, null));
            }
            list.Add(new("relays", "继电器动作总次数", _ => $"IF(COUNT({Rng(KRelay)})=0,\"\",SUM({Rng(KRelay)}))", "0",
                "恒温期间正常应该是 0；有数就是在抖"));
            list.Add(new(null, "", null, null, null));
            // 下面这几格是工具用每秒数据算好填的值（Excel 里没有每秒数据）：最后 30 min 的稳定度
            list.Add(new("stabWindow", "稳定度统计窗口 min（1 s 数据，程序算）", null, "0", "取恒温记录最后这么多分钟的每秒数据算"));
            foreach (var w in new[] { "A", "B" })
            {
                list.Add(new($"{w}TjPp1s", $"{w} Tj 峰峰值 ℃（1 s，程序算）", null, "0.000", null));
                list.Add(new($"{w}TjSd1s", $"{w} Tj 标准差 ℃（1 s，程序算）", null, "0.0000", null));
                list.Add(new($"{w}TrPp1s", $"{w} Tr 峰峰值 ℃（1 s，程序算）", null, "0.000", "没接探头就是空"));
                list.Add(new($"{w}TrSd1s", $"{w} Tr 标准差 ℃（1 s，程序算）", null, "0.0000", null));
                list.Add(new($"{w}Err1s", $"{w} 被控量平均与目标差 ℃（1 s，程序算）", null, "0.000", "控夹套看 Tj、控釜内看 Tr"));
            }
            return list;
        }

        var isMin = kind == TestKind.MinTemp;
        foreach (var (w, tj, outp, cur, tr, hit, tgt) in new[]
                 {
                     ("A", KATj, KAOut, KACur, KATr, "aHit", CTargetA),
                     ("B", KBTj, KBOut, KBCur, KBTr, "bHit", CTargetB)
                 })
        {
            var t = lay.CondAddr(tgt);
            var tjL = lay.ColLetter(tj);
            var extKey = $"{w}Ext";
            list.Add(new(extKey, isMin ? $"{w} 最低温 ℃" : $"{w} 最高温 ℃", _ => isMin ? MinOf(tj) : MaxOf(tj), FmtC, null));
            list.Add(new($"{w}Reach", isMin ? $"{w} 到达用时 min（首次 ≤ 目标+0.5）" : $"{w} 到达用时 min（首次 ≥ 目标−0.5）",
                _ => $"IFERROR(INDEX($A${r1}:$A${r2},MATCH(1,{Rng(hit)},0)),\"未到\")", "0",
                "到达 = 夹套首次进到目标 ±0.5 ℃；一直没到显示「未到」"));
            list.Add(isMin
                ? new($"{w}Diff", $"{w} 最低温与目标差 ℃", S => $"IF(OR({S(extKey)}=\"\",{t}=\"\"),\"\",{S(extKey)}-{t})", FmtC, null)
                : new($"{w}Over", $"{w} 过冲 ℃（最高温−目标）", S => $"IF(OR({S(extKey)}=\"\",{t}=\"\"),\"\",MAX(0,{S(extKey)}-{t}))", FmtC,
                    "负的按 0 算：没到目标就没有过冲"));
            list.Add(new($"{w}Rate10", isMin ? $"{w} 前 10 min 平均降温速率 ℃/min" : $"{w} 前 10 min 平均升温速率 ℃/min",
                _ => isMin
                    ? $"IF(OR({tjL}{r1}=\"\",{tjL}{r10}=\"\",A{r10}=A{r1}),\"\",({tjL}{r1}-{tjL}{r10})/(A{r10}-A{r1}))"
                    : $"IF(OR({tjL}{r1}=\"\",{tjL}{r10}=\"\",A{r10}=A{r1}),\"\",({tjL}{r10}-{tjL}{r1})/(A{r10}-A{r1}))",
                "0.00", "按 t=0 那行和往下第 10 行两点算"));
            list.Add(new($"{w}OutAvg", $"{w} 输出平均 %", _ => AvgOf(outp), FmtP, null));
            list.Add(new($"{w}CurMax", $"{w} 电流最大 A", _ => MaxOf(cur), FmtA, null));
            list.Add(new($"{w}TrExt", isMin ? $"{w} 釜内最低 ℃" : $"{w} 釜内最高 ℃", _ => isMin ? MinOf(tr) : MaxOf(tr), FmtTr, null));
            list.Add(new(null, "", null, null, null));
        }
        return list;
    }

    // ── 结果汇总矩阵 ─────────────────────────────────────────────────

    private void BuildSummary()
    {
        var ws = _wb.Worksheets.Add(SummarySheet);
        Title(ws, SummarySheet, "不用填，全从各表拉过来（水温 × 工位的矩阵）；最后连这张一起发");
        ws.Column(1).Width = 36;
        var waters = Spec.Waters;
        Hdr(ws, 4, 1, "项目");
        for (var i = 0; i < waters.Length; i++)
        {
            var wtxt = waters[i].ToString("0.#", CultureInfo.InvariantCulture);
            Hdr(ws, 4, 2 + i * 2, $"{wtxt} ℃ · A", 12);
            Hdr(ws, 4, 3 + i * 2, $"{wtxt} ℃ · B", 12);
        }
        var noteCol = 2 + waters.Length * 2;
        Hdr(ws, 4, noteCol, "说明", 40);

        var r = 5;
        foreach (var kind in TestKinds.All)
        {
            var title = ws.Cell(r, 1);
            title.Value = TestKinds.Name(kind);
            title.Style.Font.FontName = FontName;
            title.Style.Font.Bold = true;
            title.Style.Font.FontSize = 11;
            var cols = Spec.WatersOf(kind).ToArray();
            if (cols.Length == 1 && double.IsNaN(cols[0]))
            {
                // 最高温只有一张表：列头换成「无冷却水 · A/B」，占前两列
                Hdr(ws, r, 2, "无冷却水 · A");
                Hdr(ws, r, 3, "无冷却水 · B");
            }
            r++;

            foreach (var (label, keyA, keyB, fmt, note) in MatrixRows(kind))
            {
                Lbl(ws.Cell(r, 1), label);
                for (var i = 0; i < cols.Length; i++)
                {
                    var lay = Layout(kind, cols[i]);
                    Fml(ws.Cell(r, 2 + i * 2), $"'{lay.Name}'!{lay.SumAddr(keyA)}", fmt);
                    Fml(ws.Cell(r, 3 + i * 2), $"'{lay.Name}'!{lay.SumAddr(keyB)}", fmt);
                }
                if (note is not null) Note(ws, r, noteCol, note);
                r++;
            }
            // 条件里那几项也拉过来：结果、实测水温、控温对象
            foreach (var (label, condKey, fmt) in new[]
                     {
                         ("冷却水温度（手填）", CWater, (string?)null),
                         ("控温对象", CObject, null),
                         ("结果", CResult, null)
                     })
            {
                Lbl(ws.Cell(r, 1), label);
                for (var i = 0; i < cols.Length; i++)
                {
                    var lay = Layout(kind, cols[i]);
                    // 文本格：直接引用空格会显示 0，套一层 IF
                    var f = $"IF('{lay.Name}'!{lay.CondAddr(condKey)}=\"\",\"\",'{lay.Name}'!{lay.CondAddr(condKey)})";
                    Fml(ws.Cell(r, 2 + i * 2), f, fmt);
                    Fml(ws.Cell(r, 3 + i * 2), f, fmt);
                }
                r++;
            }
            r++;
        }
        Note(ws, r, 1, "每一格都是公式，指向对应那张表右侧的「汇总（自动算）」；表里没数就是空。");
        ws.SheetView.FreezeRows(4);
        ws.SheetView.FreezeColumns(1);
    }

    private static IEnumerable<(string Label, string KeyA, string KeyB, string? Fmt, string? Note)> MatrixRows(TestKind kind)
    {
        switch (kind)
        {
            case TestKind.MinTemp:
                yield return ("最低温 ℃", "AExt", "BExt", FmtC, "夹套能到的最低温度");
                yield return ("到达用时 min", "AReach", "BReach", "0", "首次 ≤ 目标 + 0.5 ℃；「未到」= 没到过");
                yield return ("最低温与目标差 ℃", "ADiff", "BDiff", FmtC, "正数 = 没降到目标");
                yield return ("前 10 min 平均降温速率 ℃/min", "ARate10", "BRate10", "0.00", null);
                yield return ("输出平均 %", "AOutAvg", "BOutAvg", FmtP, null);
                yield return ("电流最大 A", "ACurMax", "BCurMax", FmtA, null);
                yield return ("釜内最低 ℃", "ATrExt", "BTrExt", FmtTr, "没接 Tr 探头就是空");
                break;
            case TestKind.MaxTemp:
                yield return ("最高温 ℃", "AExt", "BExt", FmtC, null);
                yield return ("到达用时 min", "AReach", "BReach", "0", "首次 ≥ 目标 − 0.5 ℃");
                yield return ("过冲 ℃", "AOver", "BOver", FmtC, "最高温 − 目标，负的按 0");
                yield return ("前 10 min 平均升温速率 ℃/min", "ARate10", "BRate10", "0.00", null);
                yield return ("输出平均 %", "AOutAvg", "BOutAvg", FmtP, null);
                yield return ("电流最大 A", "ACurMax", "BCurMax", FmtA, null);
                yield return ("釜内最高 ℃", "ATrExt", "BTrExt", FmtTr, null);
                break;
            default:
                yield return ("Tj 平均 ℃", "ATjAvg", "BTjAvg", "0.00", null);
                yield return ("Tj 峰峰值 ℃", "ATjPp", "BTjPp", "0.00", null);
                yield return ("Tj 波动 ± ℃", "ATjPm", "BTjPm", "0.00", null);
                yield return ("Tj 标准差 ℃", "ATjSd", "BTjSd", "0.000", null);
                yield return ("Tr 峰峰值 ℃", "ATrPp", "BTrPp", "0.00", null);
                yield return ("Tr 平均与目标差 ℃", "ATrDiff", "BTrDiff", "0.00", null);
                yield return ("输出平均 %", "AOutAvg", "BOutAvg", FmtP, null);
                yield return ("继电器动作总次数", "relays", "relays", "0", "两工位合计；正常应该是 0");
                yield return ("Tj 峰峰值 ℃（1 s 数据，最后 30 min）", "ATjPp1s", "BTjPp1s", "0.000", "工具用每秒数据算的，±0.01 看这几行");
                yield return ("Tj 标准差 ℃（1 s）", "ATjSd1s", "BTjSd1s", "0.0000", null);
                yield return ("Tr 峰峰值 ℃（1 s）", "ATrPp1s", "BTrPp1s", "0.000", null);
                yield return ("Tr 标准差 ℃（1 s）", "ATrSd1s", "BTrSd1s", "0.0000", null);
                yield return ("被控量平均与目标差 ℃（1 s）", "AErr1s", "BErr1s", "0.000", "控夹套看 Tj、控釜内看 Tr");
                break;
        }
    }
}
