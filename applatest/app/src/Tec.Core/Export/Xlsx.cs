using System.Globalization;
using System.Text;

namespace Tec.Core.Export;

/// <summary>
/// 单元格的样子。样式是一小撮固定的，够用就行——
/// 一张交出去的数据表要的是「表头认得出、数字对得齐、时间是时间」，不是排版。
/// </summary>
public enum XlStyle
{
    Text = 0,
    /// <summary>表头：加粗、浅底、下边框。</summary>
    Head = 1,
    /// <summary>两位小数。测量值一律走它，免得 25.100000000000001 这种数糊在表里。</summary>
    Num2 = 2,
    /// <summary>整数。</summary>
    Int = 3,
    /// <summary>日期时间 yyyy-mm-dd hh:mm:ss。</summary>
    Stamp = 4,
    /// <summary>时长 [h]:mm:ss（超过 24 小时不回绕）。</summary>
    Span = 5,
    /// <summary>标题：加粗大字。</summary>
    Title = 6,
    /// <summary>说明文字：小号灰字。</summary>
    Note = 7,
    /// <summary>要人看见的那一行（仿真数据、已中止…）：红字。</summary>
    Bad = 8,
    /// <summary>六位小数。派生量和特征值有时很小，两位会全变成 0.00。</summary>
    Num6 = 9,
    /// <summary>四位小数。配料的质量走它：两位会把 0.0125 g 的催化剂圆成 0.01。</summary>
    Num4 = 10
}

/// <summary>一个格子。文本和数字**分开存**——数字进了表还是数字，能排序能算。</summary>
public readonly struct XlCell
{
    private XlCell(string? text, double? num, XlStyle style) { Text = text; Num = num; Style = style; }

    public string? Text { get; }
    public double? Num { get; }
    public XlStyle Style { get; }

    public static readonly XlCell Empty = new(null, null, XlStyle.Text);

    public static XlCell S(string? text, XlStyle style = XlStyle.Text) => new(text, null, style);
    public static XlCell N(double? v, XlStyle style = XlStyle.Num2)
        => v is null || double.IsNaN(v.Value) || double.IsInfinity(v.Value) ? Empty : new(null, v, style);
    public static XlCell I(long v) => new(null, v, XlStyle.Int);

    /// <summary>时刻。Excel 的日期是「1899-12-30 起的天数」，本地时间照写。</summary>
    public static XlCell T(DateTimeOffset? at)
        => at is null ? Empty
         : new(null, (at.Value.DateTime - new DateTime(1899, 12, 30)).TotalDays, XlStyle.Stamp);

    /// <summary>时长。同样是天数，配 [h]:mm:ss 格式；负数（偏差）照样能显示。</summary>
    public static XlCell D(TimeSpan? d)
        => d is null ? Empty : new(null, d.Value.TotalDays, XlStyle.Span);
}

public sealed class XlSheet
{
    public XlSheet(string name) => Name = name;

    public string Name { get; }
    /// <summary>列宽（字符数）。空 = 用默认宽。</summary>
    public List<double> Widths { get; } = new();
    /// <summary>冻结前几行。0 = 不冻。</summary>
    public int Freeze { get; set; }
    /// <summary>给哪一行加筛选器（1 起）。0 = 不加。</summary>
    public int FilterRow { get; set; }
    public List<XlCell[]> Rows { get; } = new();

    public void Add(params XlCell[] cells) => Rows.Add(cells);
    public void Head(params string[] names) => Rows.Add(names.Select(n => XlCell.S(n, XlStyle.Head)).ToArray());
    public void Blank() => Rows.Add(Array.Empty<XlCell>());
}

/// <summary>
/// .xlsx 写出。手写 OOXML：一个 zip + 几份 XML，没有第三方依赖。
///
/// 文本走共享字符串表（同一个「CH1」在采样表里出现几万次，存一份就够）；
/// 数字**以数字身份**写进去，不是「看着像数字的文本」——
/// 后者在 Excel 里排序会把 100 排在 99 前面，那种表交出去等于没交。
/// </summary>
public static class XlsxWriter
{
    public static void Save(string path, IReadOnlyList<XlSheet> sheets, string title, string author)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        Write(fs, sheets, title, author);
    }

    public static void Write(Stream output, IReadOnlyList<XlSheet> sheets, string title, string author)
    {
        if (sheets.Count == 0) throw new InvalidOperationException("工作簿里一页都没有。");

        var shared = new SharedStrings();
        using var pkg = new OoxmlPackage(output, leaveOpen: true);

        pkg.Xml("[Content_Types].xml", ContentTypes(sheets.Count));
        pkg.Xml("_rels/.rels",
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
            + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>"
            + "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/>"
            + "<Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties\" Target=\"docProps/app.xml\"/>"
            + "</Relationships>");
        pkg.Xml("docProps/core.xml", CoreProps(title, author));
        pkg.Xml("docProps/app.xml",
            "<Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/extended-properties\" "
            + "xmlns:vt=\"http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes\">"
            + "<Application>TecStudio</Application></Properties>");

        pkg.Xml("xl/workbook.xml", Workbook(sheets));
        pkg.Xml("xl/_rels/workbook.xml.rels", WorkbookRels(sheets.Count));
        pkg.Xml("xl/styles.xml", Styles());

        // 表先写：写的过程里才知道共享字符串表长什么样
        for (var i = 0; i < sheets.Count; i++)
        {
            var sheet = sheets[i];
            pkg.Xml($"xl/worksheets/sheet{i + 1}.xml", w => Worksheet(w, sheet, shared));
        }
        pkg.Xml("xl/sharedStrings.xml", w => shared.Write(w));
    }

    // ── 各部分 ──────────────────────────────────────────────────────

    private static string ContentTypes(int sheets)
    {
        var sb = new StringBuilder();
        sb.Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
        sb.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>");
        sb.Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>");
        sb.Append("<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>");
        for (var i = 1; i <= sheets; i++)
            sb.Append($"<Override PartName=\"/xl/worksheets/sheet{i}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
        sb.Append("<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
        sb.Append("<Override PartName=\"/xl/sharedStrings.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml\"/>");
        sb.Append("<Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/>");
        sb.Append("<Override PartName=\"/docProps/app.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.extended-properties+xml\"/>");
        sb.Append("</Types>");
        return sb.ToString();
    }

    internal static string CoreProps(string title, string author)
    {
        var now = DateTimeOffset.Now.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        return "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" "
             + "xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:dcterms=\"http://purl.org/dc/terms/\" "
             + "xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">"
             + $"<dc:title>{OoxmlPackage.X(title)}</dc:title>"
             + $"<dc:creator>{OoxmlPackage.X(author)}</dc:creator>"
             + $"<cp:lastModifiedBy>{OoxmlPackage.X(author)}</cp:lastModifiedBy>"
             + $"<dcterms:created xsi:type=\"dcterms:W3CDTF\">{now}</dcterms:created>"
             + $"<dcterms:modified xsi:type=\"dcterms:W3CDTF\">{now}</dcterms:modified>"
             + "</cp:coreProperties>";
    }

    private static string Workbook(IReadOnlyList<XlSheet> sheets)
    {
        var sb = new StringBuilder();
        sb.Append("<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" ")
          .Append("xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < sheets.Count; i++)
            sb.Append($"<sheet name=\"{OoxmlPackage.X(SheetName(sheets[i].Name, used))}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>");
        sb.Append("</sheets></workbook>");
        return sb.ToString();
    }

    /// <summary>
    /// 页名的规矩：不超过 31 字，不许出现 : \ / ? * [ ]，不许重名。
    /// 违一条整份文件在 Excel 里就打不开——而页名是从配方名来的，那是用户随手起的。
    /// </summary>
    internal static string SheetName(string raw, HashSet<string> used)
    {
        var sb = new StringBuilder();
        foreach (var c in raw) sb.Append(c is ':' or '\\' or '/' or '?' or '*' or '[' or ']' ? '-' : c);
        var name = sb.ToString().Trim();
        if (name.Length == 0) name = "工作表";
        if (name.Length > 31) name = name[..31];
        if (used.Add(name)) return name;
        for (var n = 2; ; n++)
        {
            var tag = $" ({n})";
            var cut = name.Length + tag.Length > 31 ? name[..(31 - tag.Length)] : name;
            var candidate = cut + tag;
            if (used.Add(candidate)) return candidate;
        }
    }

    private static string WorkbookRels(int sheets)
    {
        var sb = new StringBuilder();
        sb.Append("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
        for (var i = 1; i <= sheets; i++)
            sb.Append($"<Relationship Id=\"rId{i}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i}.xml\"/>");
        sb.Append($"<Relationship Id=\"rId{sheets + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
        sb.Append($"<Relationship Id=\"rId{sheets + 2}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings\" Target=\"sharedStrings.xml\"/>");
        sb.Append("</Relationships>");
        return sb.ToString();
    }

    /// <summary>
    /// 样式表。顺序**就是** XlStyle 的取值，别动：cellXfs 里第 n 个就是 s="n"。
    /// </summary>
    private static string Styles()
    {
        var sb = new StringBuilder();
        sb.Append("<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
        // 自定义数字格式从 164 起（0–163 是内置的）
        sb.Append("<numFmts count=\"5\">")
          .Append("<numFmt numFmtId=\"164\" formatCode=\"0.00\"/>")
          .Append("<numFmt numFmtId=\"165\" formatCode=\"yyyy\\-mm\\-dd\\ hh:mm:ss\"/>")
          .Append("<numFmt numFmtId=\"166\" formatCode=\"[h]:mm:ss\"/>")
          .Append("<numFmt numFmtId=\"167\" formatCode=\"0.000000\"/>")
          .Append("<numFmt numFmtId=\"168\" formatCode=\"0.0000\"/>")
          .Append("</numFmts>");

        // 0 正文 1 粗 2 大粗 3 灰小 4 红
        sb.Append("<fonts count=\"5\">")
          .Append("<font><sz val=\"10.5\"/><name val=\"等线\"/></font>")
          .Append("<font><b/><sz val=\"10.5\"/><name val=\"等线\"/></font>")
          .Append("<font><b/><sz val=\"14\"/><name val=\"等线\"/></font>")
          .Append("<font><sz val=\"9\"/><color rgb=\"FF8A8A8A\"/><name val=\"等线\"/></font>")
          .Append("<font><sz val=\"10.5\"/><color rgb=\"FFC0392B\"/><name val=\"等线\"/></font>")
          .Append("</fonts>");

        sb.Append("<fills count=\"3\">")
          .Append("<fill><patternFill patternType=\"none\"/></fill>")
          .Append("<fill><patternFill patternType=\"gray125\"/></fill>")
          .Append("<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFEDEAF6\"/><bgColor indexed=\"64\"/></patternFill></fill>")
          .Append("</fills>");

        sb.Append("<borders count=\"2\">")
          .Append("<border><left/><right/><top/><bottom/><diagonal/></border>")
          .Append("<border><left/><right/><top/><bottom style=\"thin\"><color rgb=\"FFBFB8D8\"/></bottom><diagonal/></border>")
          .Append("</borders>");

        sb.Append("<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>");

        sb.Append("<cellXfs count=\"11\">")
          // 0 Text
          .Append("<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyAlignment=\"1\"><alignment vertical=\"center\"/></xf>")
          // 1 Head
          .Append("<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment vertical=\"center\" wrapText=\"1\"/></xf>")
          // 2 Num2
          .Append("<xf numFmtId=\"164\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>")
          // 3 Int
          .Append("<xf numFmtId=\"1\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>")
          // 4 Stamp
          .Append("<xf numFmtId=\"165\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>")
          // 5 Span
          .Append("<xf numFmtId=\"166\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>")
          // 6 Title
          .Append("<xf numFmtId=\"0\" fontId=\"2\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>")
          // 7 Note
          .Append("<xf numFmtId=\"0\" fontId=\"3\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyAlignment=\"1\"><alignment vertical=\"center\" wrapText=\"1\"/></xf>")
          // 8 Bad
          .Append("<xf numFmtId=\"0\" fontId=\"4\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>")
          // 9 Num6
          .Append("<xf numFmtId=\"167\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>")
          // 10 Num4
          .Append("<xf numFmtId=\"168\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>")
          .Append("</cellXfs>");

        sb.Append("<cellStyles count=\"1\"><cellStyle name=\"常规\" xfId=\"0\" builtinId=\"0\"/></cellStyles>");
        sb.Append("</styleSheet>");
        return sb.ToString();
    }

    private static void Worksheet(TextWriter w, XlSheet sheet, SharedStrings shared)
    {
        var maxCol = 1;
        foreach (var r in sheet.Rows) if (r.Length > maxCol) maxCol = r.Length;
        var rows = Math.Max(1, sheet.Rows.Count);

        w.Write("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
        w.Write($"<dimension ref=\"A1:{Col(maxCol)}{rows}\"/>");
        w.Write("<sheetViews><sheetView workbookViewId=\"0\">");
        if (sheet.Freeze > 0)
            w.Write($"<pane ySplit=\"{sheet.Freeze}\" topLeftCell=\"A{sheet.Freeze + 1}\" activePane=\"bottomLeft\" state=\"frozen\"/>");
        w.Write("</sheetView></sheetViews>");
        w.Write("<sheetFormatPr defaultRowHeight=\"15\"/>");

        if (sheet.Widths.Count > 0)
        {
            w.Write("<cols>");
            for (var i = 0; i < sheet.Widths.Count; i++)
                w.Write($"<col min=\"{i + 1}\" max=\"{i + 1}\" width=\"{sheet.Widths[i].ToString("0.##", CultureInfo.InvariantCulture)}\" customWidth=\"1\"/>");
            w.Write("</cols>");
        }

        w.Write("<sheetData>");
        for (var r = 0; r < sheet.Rows.Count; r++)
        {
            var cells = sheet.Rows[r];
            if (cells.Length == 0) continue;                  // 空行不写，占位靠行号
            w.Write($"<row r=\"{r + 1}\">");
            for (var c = 0; c < cells.Length; c++)
            {
                var cell = cells[c];
                if (cell.Text is null && cell.Num is null) continue;
                var reference = Col(c + 1) + (r + 1).ToString(CultureInfo.InvariantCulture);
                var s = (int)cell.Style;
                if (cell.Num is { } n)
                    w.Write($"<c r=\"{reference}\" s=\"{s}\"><v>{n.ToString("R", CultureInfo.InvariantCulture)}</v></c>");
                else
                    w.Write($"<c r=\"{reference}\" s=\"{s}\" t=\"s\"><v>{shared.Index(cell.Text!)}</v></c>");
            }
            w.Write("</row>");
        }
        w.Write("</sheetData>");

        if (sheet.FilterRow > 0 && sheet.FilterRow <= sheet.Rows.Count)
            w.Write($"<autoFilter ref=\"A{sheet.FilterRow}:{Col(maxCol)}{rows}\"/>");
        w.Write("</worksheet>");
    }

    /// <summary>1 → A，27 → AA。</summary>
    internal static string Col(int n)
    {
        var sb = new StringBuilder(3);
        while (n > 0)
        {
            var rem = (n - 1) % 26;
            sb.Insert(0, (char)('A' + rem));
            n = (n - 1) / 26;
        }
        return sb.ToString();
    }

    private sealed class SharedStrings
    {
        private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
        private readonly List<string> _list = new();
        private int _total;

        public int Index(string s)
        {
            _total++;
            if (_index.TryGetValue(s, out var i)) return i;
            i = _list.Count;
            _index[s] = i;
            _list.Add(s);
            return i;
        }

        public void Write(TextWriter w)
        {
            w.Write($"<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" count=\"{_total}\" uniqueCount=\"{_list.Count}\">");
            foreach (var s in _list)
                // xml:space="preserve"：表头里有「Tr−Tj 温差 ℃」这种带空格的，
                // 不加的话前后空格被读表的一方吃掉，列名对不上
                w.Write($"<si><t xml:space=\"preserve\">{OoxmlPackage.X(s)}</t></si>");
            w.Write("</sst>");
        }
    }
}
