using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using A = DocumentFormat.OpenXml.Drawing;
using C = DocumentFormat.OpenXml.Drawing.Charts;
using Xdr = DocumentFormat.OpenXml.Drawing.Spreadsheet;

namespace Tec.LimitTest.Rate;

/// <summary>
/// 【本地改动】（2026-11-17，升降温速率测试）往已经存好的 .xlsx 里加 Excel 自己的折线图（散点带直线、不带点）。
/// ClosedXML 不会画图，所以 ClosedXML 存完以后用 Open XML SDK（ClosedXML 自带的那个依赖，不多引包）打开再加：
/// 每张图一个 ChartPart，挂在那张表的 DrawingsPart 上，系列引用表里的格子（Excel 打开按格子画），另带一份缓存值（不重算的看图软件也有图）。
/// 回归测试用 SDK 的校验器验过这几个部件合规（Excel 对图表的 XML 很挑，错一个元素顺序就弹「修复」）。
/// </summary>
public static class XlsxChart
{
    /// <summary>一条线：名字、Y 值在哪一列（1 起）、颜色（RRGGBB）、虚线不虚线，和每一行的值（缓存用，空格是 null）。</summary>
    public sealed record Series(string Name, int Col, string Color, bool Dashed)
    {
        public double?[] Values { get; init; } = Array.Empty<double?>();
    }

    /// <summary>
    /// 一张图：画在哪张表、标题、X 列（1 起）和数据行范围、几条线、坐标轴范围，摆在哪（左上角的列 / 行，0 起，占几列几行）。
    /// </summary>
    public sealed record Spec(
        string Sheet, string Title, string XTitle, string YTitle,
        int XCol, int FirstRow, int LastRow, IReadOnlyList<Series> Series,
        double XMax, double XMajor, double YMin, double YMax,
        int AnchorCol, int AnchorRow, int AnchorCols, int AnchorRows)
    {
        public double?[] X { get; init; } = Array.Empty<double?>();
    }

    public static void Add(string path, IEnumerable<Spec> specs)
    {
        using var doc = SpreadsheetDocument.Open(path, true);
        var wbPart = doc.WorkbookPart ?? throw new InvalidOperationException("工作簿没有 WorkbookPart");
        var n = 0;
        foreach (var spec in specs)
        {
            var sheet = wbPart.Workbook.Sheets?.Elements<Sheet>().FirstOrDefault(s => s.Name?.Value == spec.Sheet)
                        ?? throw new InvalidOperationException($"没有「{spec.Sheet}」这张表");
            var wsPart = (WorksheetPart)wbPart.GetPartById(sheet.Id!.Value!);
            var drawings = wsPart.DrawingsPart;
            if (drawings is null)
            {
                drawings = wsPart.AddNewPart<DrawingsPart>();
                var wsDr = new Xdr.WorksheetDrawing();
                wsDr.AddNamespaceDeclaration("xdr", "http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing");
                wsDr.AddNamespaceDeclaration("a", "http://schemas.openxmlformats.org/drawingml/2006/main");
                drawings.WorksheetDrawing = wsDr;
                InsertDrawingRef(wsPart.Worksheet, wsPart.GetIdOfPart(drawings));
            }
            var chartPart = drawings.AddNewPart<ChartPart>();
            chartPart.ChartSpace = BuildChart(spec);
            n++;
            drawings.WorksheetDrawing!.Append(Anchor(spec, drawings.GetIdOfPart(chartPart), (uint)(n + 1), $"图表 {n}"));
            drawings.WorksheetDrawing.Save();
            wsPart.Worksheet.Save();
        }
    }

    /// <summary>
    /// 表里挂 &lt;drawing r:id=…/&gt;。它在 worksheet 里有固定位置：排在 legacyDrawing、tableParts、extLst 这些前面，
    /// 前面那些（pageMargins、pageSetup、headerFooter…）后面；ClosedXML 写出来的表后面可能跟着 tableParts / extLst。
    /// </summary>
    private static void InsertDrawingRef(Worksheet ws, string relId)
    {
        var drawing = new Drawing { Id = relId };
        var after = ws.ChildElements.FirstOrDefault(e =>
            e is LegacyDrawing or LegacyDrawingHeaderFooter or DrawingHeaderFooter or Picture or OleObjects or Controls
              or WebPublishItems or TableParts or WorksheetExtensionList);
        if (after is not null) ws.InsertBefore(drawing, after);
        else ws.Append(drawing);
    }

    private static Xdr.TwoCellAnchor Anchor(Spec spec, string chartRelId, uint id, string name)
    {
        var chartRef = new C.ChartReference { Id = chartRelId };
        chartRef.AddNamespaceDeclaration("c", "http://schemas.openxmlformats.org/drawingml/2006/chart");
        chartRef.AddNamespaceDeclaration("r", "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
        var frame = new Xdr.GraphicFrame(
            new Xdr.NonVisualGraphicFrameProperties(
                new Xdr.NonVisualDrawingProperties { Id = id, Name = name },
                new Xdr.NonVisualGraphicFrameDrawingProperties()),
            new Xdr.Transform(new A.Offset { X = 0, Y = 0 }, new A.Extents { Cx = 0, Cy = 0 }),
            new A.Graphic(new A.GraphicData(chartRef) { Uri = "http://schemas.openxmlformats.org/drawingml/2006/chart" }))
        { Macro = "" };
        return new Xdr.TwoCellAnchor(
            new Xdr.FromMarker(new Xdr.ColumnId(I(spec.AnchorCol)), new Xdr.ColumnOffset("0"),
                               new Xdr.RowId(I(spec.AnchorRow)), new Xdr.RowOffset("0")),
            new Xdr.ToMarker(new Xdr.ColumnId(I(spec.AnchorCol + spec.AnchorCols)), new Xdr.ColumnOffset("0"),
                             new Xdr.RowId(I(spec.AnchorRow + spec.AnchorRows)), new Xdr.RowOffset("0")),
            frame,
            new Xdr.ClientData());
    }

    private const uint XAxisId = 50010, YAxisId = 50020;

    private static C.ChartSpace BuildChart(Spec spec)
    {
        var cs = new C.ChartSpace();
        cs.AddNamespaceDeclaration("c", "http://schemas.openxmlformats.org/drawingml/2006/chart");
        cs.AddNamespaceDeclaration("a", "http://schemas.openxmlformats.org/drawingml/2006/main");
        cs.AddNamespaceDeclaration("r", "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
        cs.Append(new C.RoundedCorners { Val = false });

        var scatter = new C.ScatterChart(
            new C.ScatterStyle { Val = C.ScatterStyleValues.LineMarker },
            new C.VaryColors { Val = false });
        var xRef = Ref(spec.Sheet, spec.XCol, spec.FirstRow, spec.LastRow);
        for (var i = 0; i < spec.Series.Count; i++)
        {
            var s = spec.Series[i];
            var line = new A.Outline(new A.SolidFill(new A.RgbColorModelHex { Val = s.Color })) { Width = s.Dashed ? 15875 : 22225 };
            if (s.Dashed) line.Append(new A.PresetDash { Val = A.PresetLineDashValues.Dash });
            scatter.Append(new C.ScatterChartSeries(
                new C.Index { Val = (uint)i },
                new C.Order { Val = (uint)i },
                new C.SeriesText(new C.NumericValue(s.Name)),
                new C.ChartShapeProperties(line),
                new C.Marker(new C.Symbol { Val = C.MarkerStyleValues.None }),
                new C.XValues(new C.NumberReference(new C.Formula(xRef), Cache(spec.X, "0"))),
                new C.YValues(new C.NumberReference(new C.Formula(Ref(spec.Sheet, s.Col, spec.FirstRow, spec.LastRow)), Cache(s.Values, "0.00"))),
                new C.Smooth { Val = false }));
        }
        scatter.Append(new C.AxisId { Val = XAxisId }, new C.AxisId { Val = YAxisId });

        var plot = new C.PlotArea(new C.Layout(), scatter,
            Axis(XAxisId, YAxisId, C.AxisPositionValues.Bottom, spec.XTitle, 0, spec.XMax, spec.XMajor, grid: false, "0"),
            Axis(YAxisId, XAxisId, C.AxisPositionValues.Left, spec.YTitle, spec.YMin, spec.YMax, null, grid: true, "0"));

        cs.Append(new C.Chart(
            Title(spec.Title, 1200),
            new C.AutoTitleDeleted { Val = false },
            plot,
            new C.Legend(new C.LegendPosition { Val = C.LegendPositionValues.Bottom }, new C.Overlay { Val = false }),
            new C.PlotVisibleOnly { Val = true },
            new C.DisplayBlanksAs { Val = C.DisplayBlanksAsValues.Gap }));
        return cs;
    }

    /// <summary>
    /// 值坐标轴。横轴放在纵轴最小值那一头（crosses = min，不然降温到 0 ℃ 以下时横轴会画在图中间），刻度标签贴着图边（low）。
    /// </summary>
    private static C.ValueAxis Axis(uint id, uint cross, C.AxisPositionValues pos, string title, double min, double max,
                                    double? major, bool grid, string fmt)
    {
        var ax = new C.ValueAxis(
            new C.AxisId { Val = id },
            new C.Scaling(new C.Orientation { Val = C.OrientationValues.MinMax },
                          new C.MaxAxisValue { Val = max }, new C.MinAxisValue { Val = min }),
            new C.Delete { Val = false },
            new C.AxisPosition { Val = pos });
        if (grid) ax.Append(new C.MajorGridlines(new C.ChartShapeProperties(
            new A.Outline(new A.SolidFill(new A.RgbColorModelHex { Val = "E0E0E0" })) { Width = 6350 })));
        ax.Append(
            Title(title, 1000),
            new C.NumberingFormat { FormatCode = fmt, SourceLinked = false },
            new C.MajorTickMark { Val = C.TickMarkValues.Outside },
            new C.MinorTickMark { Val = C.TickMarkValues.None },
            new C.TickLabelPosition { Val = C.TickLabelPositionValues.Low },
            new C.CrossingAxis { Val = cross },
            new C.Crosses { Val = C.CrossesValues.Minimum },
            new C.CrossBetween { Val = C.CrossBetweenValues.MidpointCategory });
        if (major is { } m) ax.Append(new C.MajorUnit { Val = m });
        return ax;
    }

    private static C.Title Title(string text, int size)
        => new(
            new C.ChartText(new C.RichText(
                new A.BodyProperties(),
                new A.ListStyle(),
                new A.Paragraph(
                    new A.ParagraphProperties(new A.DefaultRunProperties { FontSize = size, Bold = true }),
                    new A.Run(new A.RunProperties { Language = "zh-CN", FontSize = size, Bold = true }, new A.Text(text))))),
            new C.Overlay { Val = false });

    private static C.NumberingCache Cache(double?[] values, string fmt)
    {
        var cache = new C.NumberingCache(new C.FormatCode(fmt), new C.PointCount { Val = (uint)values.Length });
        for (var i = 0; i < values.Length; i++)
            if (values[i] is { } v && !double.IsNaN(v))
                cache.Append(new C.NumericPoint(new C.NumericValue(v.ToString("R", CultureInfo.InvariantCulture))) { Index = (uint)i });
        return cache;
    }

    /// <summary>'表名'!$A$5:$A$125（表名里的单引号要写两个）。</summary>
    private static string Ref(string sheet, int col, int first, int last)
    {
        var letter = ClosedXML.Excel.XLHelper.GetColumnLetterFromNumber(col);
        return $"'{sheet.Replace("'", "''")}'!${letter}${first}:${letter}${last}";
    }

    private static string I(int v) => v.ToString(CultureInfo.InvariantCulture);
}
