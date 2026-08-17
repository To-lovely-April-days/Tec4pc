using System.Globalization;
using System.Text;

namespace Tec.Core.Export;

/// <summary>
/// <see cref="ReportDoc"/> → .docx。
///
/// 和 PDF 读的是同一份内容，但**排版交给 Word 自己**：Word 是流式排版，
/// 我们只说「这是一级标题」「这是表格」，分页由它决定。硬按 PDF 那套
/// 绝对坐标去摆，人一改字号整份文档就散了——那样的 Word 文件不如给张图。
///
/// 表头行标了「跨页重复」，跨页时 Word 会自己把表头再画一遍，与 PDF 一致。
/// </summary>
public static class DocxWriter
{
    private const string WNs = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string RNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string WpNs = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    private const string ANs = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string PicNs = "http://schemas.openxmlformats.org/drawingml/2006/picture";

    /// <summary>A4 正文宽度（缇）：21cm − 左右各 2cm。</summary>
    private const int BodyTwips = 11906 - 1134 * 2;

    public static void Save(string path, ReportDoc doc, string author)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        Write(fs, doc, author);
    }

    public static void Write(Stream output, ReportDoc doc, string author)
    {
        using var pkg = new OoxmlPackage(output, leaveOpen: true);

        var images = new List<(string File, byte[] Png)>();
        var body = Body(doc, images);

        var types = new StringBuilder();
        types.Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">")
             .Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>")
             .Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>")
             .Append("<Default Extension=\"png\" ContentType=\"image/png\"/>")
             .Append("<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>")
             .Append("<Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/>")
             .Append("<Override PartName=\"/word/footer1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.footer+xml\"/>")
             .Append("<Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/>")
             .Append("<Override PartName=\"/docProps/app.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.extended-properties+xml\"/>")
             .Append("</Types>");
        pkg.Xml("[Content_Types].xml", types.ToString());

        pkg.Xml("_rels/.rels",
            $"<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
            + $"<Relationship Id=\"rId1\" Type=\"{RNs}/officeDocument\" Target=\"word/document.xml\"/>"
            + "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/>"
            + $"<Relationship Id=\"rId3\" Type=\"{RNs}/extended-properties\" Target=\"docProps/app.xml\"/>"
            + "</Relationships>");

        pkg.Xml("docProps/core.xml", XlsxWriter.CoreProps(doc.Title, author));
        pkg.Xml("docProps/app.xml",
            "<Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/extended-properties\">"
            + "<Application>TecStudio</Application></Properties>");

        var rels = new StringBuilder();
        rels.Append($"<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">")
            .Append($"<Relationship Id=\"rIdStyles\" Type=\"{RNs}/styles\" Target=\"styles.xml\"/>")
            .Append($"<Relationship Id=\"rIdFooter\" Type=\"{RNs}/footer\" Target=\"footer1.xml\"/>");
        for (var i = 0; i < images.Count; i++)
            rels.Append($"<Relationship Id=\"rIdImg{i}\" Type=\"{RNs}/image\" Target=\"media/{images[i].File}\"/>");
        rels.Append("</Relationships>");
        pkg.Xml("word/_rels/document.xml.rels", rels.ToString());

        pkg.Xml("word/styles.xml", Styles());
        pkg.Xml("word/footer1.xml", Footer(doc));
        pkg.Xml("word/document.xml",
            $"<w:document xmlns:w=\"{WNs}\" xmlns:r=\"{RNs}\" xmlns:wp=\"{WpNs}\" xmlns:a=\"{ANs}\" xmlns:pic=\"{PicNs}\">"
            + "<w:body>" + body + Section() + "</w:body></w:document>");

        foreach (var (file, png) in images) pkg.Binary("word/media/" + file, png);
    }

    // ── 正文 ────────────────────────────────────────────────────────

    private static string Body(ReportDoc doc, List<(string File, byte[] Png)> images)
    {
        var sb = new StringBuilder();

        sb.Append(Para(doc.Title, "TecTitle"));
        sb.Append(Para(doc.Subtitle, "TecSub"));
        if (doc.Simulated)
            sb.Append(Notice("本报告含仿真运行数据，不是真实实验结果。", true));

        sb.Append(Kv(doc.Cover, 2));
        sb.Append(Para("本报告由 TecStudio 从执行引擎记录自动生成。表中「计划」来自启动时冻结的基线，"
                       + "「实际」来自运行记录；两种偏差分列，未做任何平滑或补值。", "TecNote"));
        sb.Append(PageBreak());

        foreach (var block in doc.Blocks)
        {
            switch (block)
            {
                case HeadingBlock h:
                    sb.Append(Para(h.Text, h.Level == 1 ? "TecH1" : "TecH2"));
                    break;
                case ParaBlock p:
                    sb.Append(Para(p.Text, p.Muted ? "TecNote" : "TecBody"));
                    break;
                case NoticeBlock n:
                    sb.Append(Notice(n.Text, n.Bad));
                    break;
                case KvBlock kv:
                    if (kv.Title is { Length: > 0 }) sb.Append(Para(kv.Title, "TecH2"));
                    sb.Append(Kv(kv.Pairs, kv.Columns));
                    break;
                case TableBlock t:
                    if (t.Title is { Length: > 0 }) sb.Append(Para(t.Title, "TecH2"));
                    sb.Append(Table(t));
                    if (t.Note is { Length: > 0 }) sb.Append(Para(t.Note, "TecNote"));
                    break;
                case ImageBlock img:
                    if (img.Title is { Length: > 0 }) sb.Append(Para(img.Title, "TecH2"));
                    sb.Append(Image(img, images));
                    if (img.Note is { Length: > 0 }) sb.Append(Para(img.Note, "TecNote"));
                    break;
                case PageBreakBlock:
                    sb.Append(PageBreak());
                    break;
            }
        }
        return sb.ToString();
    }

    private static string Para(string text, string style)
        => $"<w:p><w:pPr><w:pStyle w:val=\"{style}\"/></w:pPr>{Run(text)}</w:p>";

    private static string Run(string text, bool bold = false, string? color = null)
    {
        var rpr = new StringBuilder("<w:rPr>");
        if (bold) rpr.Append("<w:b/>");
        if (color is { Length: > 0 }) rpr.Append($"<w:color w:val=\"{color}\"/>");
        rpr.Append("</w:rPr>");
        return $"<w:r>{rpr}<w:t xml:space=\"preserve\">{OoxmlPackage.X(text)}</w:t></w:r>";
    }

    private static string PageBreak()
        => "<w:p><w:r><w:br w:type=\"page\"/></w:r></w:p>";

    private static string Notice(string text, bool bad)
        => "<w:p><w:pPr><w:pStyle w:val=\"TecBody\"/>"
         + $"<w:shd w:val=\"clear\" w:fill=\"{(bad ? "FBEAE7" : "FDF6E8")}\"/>"
         + $"<w:pBdr><w:left w:val=\"single\" w:sz=\"18\" w:space=\"4\" w:color=\"{(bad ? "B03A2E" : "E9D9AE")}\"/></w:pBdr>"
         + $"<w:spacing w:before=\"120\" w:after=\"120\"/></w:pPr>{Run(text, false, bad ? "B03A2E" : "7A5C14")}</w:p>";

    /// <summary>键值表：无框细表，一行 N 对。</summary>
    private static string Kv(IReadOnlyList<(string K, string V)> pairs, int columns)
    {
        var cols = Math.Max(1, columns) * 2;
        var w = BodyTwips / cols;
        var sb = new StringBuilder();
        sb.Append("<w:tbl><w:tblPr><w:tblW w:w=\"5000\" w:type=\"pct\"/>")
          .Append("<w:tblBorders><w:insideH w:val=\"single\" w:sz=\"2\" w:space=\"0\" w:color=\"EDEDED\"/></w:tblBorders>")
          .Append("<w:tblCellMar><w:top w:w=\"40\" w:type=\"dxa\"/><w:bottom w:w=\"40\" w:type=\"dxa\"/></w:tblCellMar>")
          .Append("</w:tblPr><w:tblGrid>");
        for (var i = 0; i < cols; i++) sb.Append($"<w:gridCol w:w=\"{w}\"/>");
        sb.Append("</w:tblGrid>");

        var perRow = Math.Max(1, columns);
        for (var i = 0; i < pairs.Count; i += perRow)
        {
            sb.Append("<w:tr>");
            for (var c = 0; c < perRow; c++)
            {
                var j = i + c;
                var k = j < pairs.Count ? pairs[j].K : "";
                var v = j < pairs.Count ? pairs[j].V : "";
                sb.Append(Cell(k, w, false, "TecKvKey"));
                sb.Append(Cell(v, w, false, "TecKvVal"));
            }
            sb.Append("</w:tr>");
        }
        return sb.Append("</w:tbl>").Append(Spacer()).ToString();
    }

    private static string Cell(string text, int width, bool right, string style,
                               string? fill = null, bool bold = false, string? color = null)
    {
        var pr = new StringBuilder($"<w:tcPr><w:tcW w:w=\"{width}\" w:type=\"dxa\"/>");
        if (fill is { Length: > 0 }) pr.Append($"<w:shd w:val=\"clear\" w:fill=\"{fill}\"/>");
        pr.Append("<w:vAlign w:val=\"center\"/></w:tcPr>");

        var ppr = new StringBuilder($"<w:pPr><w:pStyle w:val=\"{style}\"/>");
        if (right) ppr.Append("<w:jc w:val=\"right\"/>");
        ppr.Append("</w:pPr>");

        return $"<w:tc>{pr}<w:p>{ppr}{Run(text, bold, color)}</w:p></w:tc>";
    }

    private static string Table(TableBlock t)
    {
        var total = t.Columns.Sum(c => c.Weight);
        var widths = t.Columns.Select(c => (int)Math.Round(BodyTwips * c.Weight / total)).ToArray();

        var sb = new StringBuilder();
        sb.Append("<w:tbl><w:tblPr><w:tblW w:w=\"5000\" w:type=\"pct\"/><w:tblLayout w:type=\"fixed\"/>")
          .Append("<w:tblBorders>")
          .Append("<w:top w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"D8D5E6\"/>")
          .Append("<w:bottom w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"D8D5E6\"/>")
          .Append("<w:insideH w:val=\"single\" w:sz=\"2\" w:space=\"0\" w:color=\"E8E6F0\"/>")
          .Append("</w:tblBorders>")
          .Append("<w:tblCellMar><w:left w:w=\"60\" w:type=\"dxa\"/><w:right w:w=\"60\" w:type=\"dxa\"/>")
          .Append("<w:top w:w=\"50\" w:type=\"dxa\"/><w:bottom w:w=\"50\" w:type=\"dxa\"/></w:tblCellMar>")
          .Append("</w:tblPr><w:tblGrid>");
        foreach (var w in widths) sb.Append($"<w:gridCol w:w=\"{w}\"/>");
        sb.Append("</w:tblGrid>");

        // 表头行标「跨页重复」，翻页之后 Word 会自己把它再画一遍
        sb.Append("<w:tr><w:trPr><w:tblHeader/><w:cantSplit/></w:trPr>");
        for (var i = 0; i < t.Columns.Count; i++)
            sb.Append(Cell(t.Columns[i].Name, widths[i], t.Columns[i].Right, "TecCell", "ECE9F5", bold: true));
        sb.Append("</w:tr>");

        for (var r = 0; r < t.Rows.Count; r++)
        {
            var bad = t.BadRows.Contains(r);
            sb.Append("<w:tr><w:trPr><w:cantSplit/></w:trPr>");
            var cells = t.Rows[r];
            for (var i = 0; i < t.Columns.Count; i++)
                sb.Append(Cell(i < cells.Length ? cells[i] : "", widths[i], t.Columns[i].Right, "TecCell",
                               bad ? "FBEAE7" : null, false, bad ? "B03A2E" : null));
            sb.Append("</w:tr>");
        }

        if (t.Rows.Count == 0)
        {
            sb.Append("<w:tr>");
            sb.Append(Cell("（无记录）", widths[0], false, "TecCell"));
            for (var i = 1; i < t.Columns.Count; i++) sb.Append(Cell("", widths[i], false, "TecCell"));
            sb.Append("</w:tr>");
        }

        return sb.Append("</w:tbl>").Append(Spacer()).ToString();
    }

    private static string Spacer()
        => "<w:p><w:pPr><w:spacing w:after=\"0\" w:line=\"120\" w:lineRule=\"exact\"/></w:pPr></w:p>";

    private static string Image(ImageBlock img, List<(string File, byte[] Png)> images)
    {
        var id = images.Count;
        images.Add(($"image{id}.png", Png.FromBgra(img.Bgra, img.PixelWidth, img.PixelHeight)));

        // 正文宽 17cm；EMU：1 英寸 = 914400，1 缇 = 635
        var wEmu = (long)(BodyTwips * 635 * Math.Clamp(img.WidthRatio, 0.1, 1));
        var hEmu = img.PixelWidth > 0 ? wEmu * img.PixelHeight / img.PixelWidth : wEmu;

        return "<w:p><w:pPr><w:pStyle w:val=\"TecBody\"/></w:pPr><w:r><w:drawing>"
             + $"<wp:inline distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\"><wp:extent cx=\"{wEmu}\" cy=\"{hEmu}\"/>"
             + $"<wp:docPr id=\"{id + 1}\" name=\"Picture {id + 1}\"/><a:graphic><a:graphicData uri=\"{PicNs}\">"
             + $"<pic:pic><pic:nvPicPr><pic:cNvPr id=\"{id + 1}\" name=\"image{id}.png\"/><pic:cNvPicPr/></pic:nvPicPr>"
             + $"<pic:blipFill><a:blip r:embed=\"rIdImg{id}\"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>"
             + $"<pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{wEmu}\" cy=\"{hEmu}\"/></a:xfrm>"
             + "<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></pic:spPr>"
             + "</pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>";
    }

    private static string Footer(ReportDoc doc)
    {
        var left = doc.FooterLeft + (doc.Simulated ? "　·　仿真运行数据" : "");
        return $"<w:ftr xmlns:w=\"{WNs}\" xmlns:r=\"{RNs}\"><w:p><w:pPr><w:pStyle w:val=\"TecNote\"/>"
             + $"<w:tabs><w:tab w:val=\"right\" w:pos=\"{BodyTwips}\"/></w:tabs>"
             + "<w:pBdr><w:top w:val=\"single\" w:sz=\"4\" w:space=\"4\" w:color=\"D8D5E6\"/></w:pBdr></w:pPr>"
             + Run(left, false, doc.Simulated ? "B03A2E" : null)
             + "<w:r><w:tab/><w:t>第 </w:t></w:r>"
             + "<w:fldSimple w:instr=\" PAGE \"><w:r><w:t>1</w:t></w:r></w:fldSimple>"
             + "<w:r><w:t> / </w:t></w:r>"
             + "<w:fldSimple w:instr=\" NUMPAGES \"><w:r><w:t>1</w:t></w:r></w:fldSimple>"
             + "<w:r><w:t> 页</w:t></w:r></w:p></w:ftr>";
    }

    private static string Section()
        => "<w:sectPr><w:footerReference w:type=\"default\" r:id=\"rIdFooter\"/>"
         + "<w:pgSz w:w=\"11906\" w:h=\"16838\"/>"
         + "<w:pgMar w:top=\"1134\" w:right=\"1134\" w:bottom=\"1134\" w:left=\"1134\" "
         + "w:header=\"720\" w:footer=\"600\" w:gutter=\"0\"/></w:sectPr>";

    /// <summary>
    /// 样式表。字体分西文 / 中文两套：只设 ascii 的话，Word 会拿默认的宋体排中文，
    /// 和界面上看到的完全不是一回事。
    /// </summary>
    private static string Styles()
    {
        string Font(int halfPt, bool bold = false, string? color = null)
            => "<w:rPr><w:rFonts w:ascii=\"Segoe UI\" w:hAnsi=\"Segoe UI\" w:eastAsia=\"微软雅黑\"/>"
             + (bold ? "<w:b/>" : "")
             + (color is null ? "" : $"<w:color w:val=\"{color}\"/>")
             + $"<w:sz w:val=\"{halfPt}\"/><w:szCs w:val=\"{halfPt}\"/></w:rPr>";

        string Style(string id, string name, string ppr, string rpr)
            => $"<w:style w:type=\"paragraph\" w:styleId=\"{id}\"><w:name w:val=\"{name}\"/>"
             + $"<w:pPr>{ppr}</w:pPr>{rpr}</w:style>";

        var sb = new StringBuilder();
        sb.Append($"<w:styles xmlns:w=\"{WNs}\">")
          .Append("<w:docDefaults><w:rPrDefault>")
          .Append(Font(21))
          .Append("</w:rPrDefault><w:pPrDefault><w:pPr><w:spacing w:after=\"80\" w:line=\"264\" w:lineRule=\"auto\"/></w:pPr></w:pPrDefault></w:docDefaults>")
          .Append("<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/></w:style>")
          .Append(Style("TecTitle", "Tec Title", "<w:spacing w:before=\"240\" w:after=\"60\"/>", Font(44, true)))
          .Append(Style("TecSub", "Tec Subtitle", "<w:spacing w:after=\"240\"/>", Font(20, false, "7B7B7B")))
          .Append(Style("TecH1", "Tec Heading 1",
                        "<w:spacing w:before=\"320\" w:after=\"140\"/><w:pBdr><w:bottom w:val=\"single\" w:sz=\"6\" w:space=\"4\" w:color=\"D8D5E6\"/></w:pBdr>"
                        + "<w:keepNext/>", Font(29, true)))
          .Append(Style("TecH2", "Tec Heading 2", "<w:spacing w:before=\"220\" w:after=\"100\"/><w:keepNext/>", Font(24, true)))
          .Append(Style("TecBody", "Tec Body", "", Font(21)))
          .Append(Style("TecNote", "Tec Note", "<w:spacing w:after=\"120\"/>", Font(18, false, "7B7B7B")))
          .Append(Style("TecCell", "Tec Cell", "<w:spacing w:after=\"0\" w:line=\"240\" w:lineRule=\"auto\"/>", Font(18)))
          .Append(Style("TecKvKey", "Tec Kv Key", "<w:spacing w:after=\"0\"/>", Font(18, false, "7B7B7B")))
          .Append(Style("TecKvVal", "Tec Kv Value", "<w:spacing w:after=\"0\"/>", Font(20)))
          .Append("</w:styles>");
        return sb.ToString();
    }
}
