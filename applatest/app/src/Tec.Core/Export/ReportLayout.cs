using System.Text;

namespace Tec.Core.Export;

// 排好版之后的图元。PDF 写出和界面上的「预览报告」读的是同一批图元——
// 各画一套的话，预览上看着好好的，导出来的 PDF 换了行、串了页。

public abstract class PageItem
{
    public double X { get; init; }
    /// <summary>从页顶往下。PDF 那边再翻成从页底往上。</summary>
    public double Y { get; init; }
}

public sealed class TextItem : PageItem
{
    public required string Text { get; init; }
    public required double Size { get; init; }
    public bool Bold { get; init; }
    /// <summary>#rrggbb。</summary>
    public string Color { get; init; } = "#222222";
}

public sealed class RectItem : PageItem
{
    public required double W { get; init; }
    public required double H { get; init; }
    public string? Fill { get; init; }
    public string? Stroke { get; init; }
    public double StrokeWidth { get; init; } = 0.6;
}

public sealed class ImageItem : PageItem
{
    public required double W { get; init; }
    public required double H { get; init; }
    public required byte[] Bgra { get; init; }
    public required int PixelWidth { get; init; }
    public required int PixelHeight { get; init; }
}

public sealed class ReportPage
{
    public List<PageItem> Items { get; } = new();
    public int Number { get; set; }
}

/// <summary>纸张与页边距，单位是点（1/72 英寸）。缺省 A4 竖排。</summary>
public sealed class PageStyle
{
    public double Width { get; init; } = 595.28;
    public double Height { get; init; } = 841.89;
    public double Left { get; init; } = 56;
    public double Right { get; init; } = 56;
    public double Top { get; init; } = 52;
    public double Bottom { get; init; } = 52;

    public double ContentWidth => Width - Left - Right;
    /// <summary>正文区的下沿：页脚要占一条。</summary>
    public double BodyBottom => Height - Bottom - 22;
    /// <summary>正文区的上沿：页眉占一条（封面页除外）。</summary>
    public double BodyTop => Top + 24;
}

/// <summary>
/// 量字宽、断行。**用的是内嵌进 PDF 的那一份字体的度量**，
/// 所以量出来的宽度就是印出来的宽度——不是估的。
/// </summary>
public sealed class TextMetrics
{
    private readonly TrueTypeFont _font;
    private readonly Dictionary<int, double> _cache = new();

    public TextMetrics(TrueTypeFont font) => _font = font;

    public TrueTypeFont Font => _font;

    /// <summary>
    /// 字体里没有的字换个写法，换不掉就丢掉。
    ///
    /// 画不出来的字在 PDF 里是一个 .notdef——多数阅读器画成空白或方框。
    /// 与其让「25 ℃」变成「25 □」，不如换成「25 °C」；连替代写法都没有的
    /// 就删掉，绝不让一个方框留在交出去的报告上。
    /// </summary>
    public string Sanitize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        StringBuilder? sb = null;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c is '\r' or '\n' or '\t') { Ensure(ref sb, s, i).Append(' '); continue; }
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                var cp = char.ConvertToUtf32(c, s[i + 1]);
                if (_font.GidOf(cp) != 0) { sb?.Append(c).Append(s[i + 1]); i++; continue; }
                Ensure(ref sb, s, i);
                i++;                                   // 增补平面的字没有替代写法，整对丢掉
                continue;
            }
            if (_font.GidOf(c) != 0) { sb?.Append(c); continue; }

            var alt = Alt(c);
            var target = Ensure(ref sb, s, i);
            foreach (var a in alt) if (_font.GidOf(a) != 0) target.Append(a);
        }
        return sb?.ToString() ?? s;
    }

    private static StringBuilder Ensure(ref StringBuilder? sb, string s, int i)
        => sb ??= new StringBuilder(s.Length + 4).Append(s, 0, i);

    private static string Alt(char c) => c switch
    {
        '℃' => "°C",
        '−' or '—' or '–' or '－' => "-",
        '·' or '•' => "-",
        '～' => "~",
        '　' => " ",
        '“' or '”' or '「' or '」' => "\"",
        '‘' or '’' => "'",
        '±' => "+/-",
        '×' => "x",
        _ => ""
    };

    /// <summary>一串字在给定字号下有多宽（点）。</summary>
    public double Width(string s, double size)
    {
        double w = 0;
        for (var i = 0; i < s.Length; i++)
        {
            int cp = s[i];
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                cp = char.ConvertToUtf32(s[i], s[i + 1]);
                i++;
            }
            if (!_cache.TryGetValue(cp, out var per))
            {
                per = _font.Width1000(_font.GidOf(cp)) / 1000.0;
                _cache[cp] = per;
            }
            w += per;
        }
        return w * size;
    }

    /// <summary>
    /// 断行。中日韩逐字断，西文按空格断——
    /// 一律按空格断的话，一整段中文没有空格，会顶出页面右边；
    /// 一律逐字断的话，英文单词会被拦腰截断。
    /// </summary>
    public List<string> Wrap(string text, double size, double maxWidth)
    {
        var lines = new List<string>();
        if (text.Length == 0) { lines.Add(""); return lines; }
        if (maxWidth <= 0) { lines.Add(text); return lines; }

        var line = new StringBuilder();
        double w = 0;
        var lastBreak = -1;              // 行内最后一个可断处（空格之后）
        double widthAtBreak = 0;

        foreach (var ch in text)
        {
            var cw = Width(ch.ToString(), size);
            var cjk = IsCjk(ch);

            if (w + cw > maxWidth && line.Length > 0)
            {
                if (!cjk && lastBreak > 0 && lastBreak < line.Length)
                {
                    // 退回上一个空格断行，把后半截带到下一行
                    var carry = line.ToString(lastBreak, line.Length - lastBreak);
                    lines.Add(line.ToString(0, lastBreak).TrimEnd());
                    line.Clear().Append(carry);
                    w = widthAtBreak;
                }
                else
                {
                    lines.Add(line.ToString());
                    line.Clear();
                    w = 0;
                }
                lastBreak = -1;
            }

            line.Append(ch);
            w += cw;
            if (ch == ' ') { lastBreak = line.Length; widthAtBreak = 0; }
            else if (lastBreak > 0) widthAtBreak += cw;
        }
        if (line.Length > 0) lines.Add(line.ToString());
        if (lines.Count == 0) lines.Add("");
        return lines;
    }

    private static bool IsCjk(char c)
        => c >= 0x2E80 && c <= 0x9FFF || c >= 0xF900 && c <= 0xFAFF || c >= 0xFF00 && c <= 0xFFEF;

    /// <summary>掐到给定宽度以内，掐掉的用省略号收尾。表格里的长备注用它。</summary>
    public string Ellipsis(string s, double size, double maxWidth)
    {
        if (Width(s, size) <= maxWidth) return s;
        var dots = _font.GidOf('…') != 0 ? "…" : "...";
        var dotsW = Width(dots, size);
        var sb = new StringBuilder();
        double w = 0;
        foreach (var c in s)
        {
            var cw = Width(c.ToString(), size);
            if (w + cw + dotsW > maxWidth) break;
            sb.Append(c);
            w += cw;
        }
        return sb.ToString() + dots;
    }
}

/// <summary>
/// 把 <see cref="ReportDoc"/> 排成一页一页的图元。
///
/// 这是 PDF 与「预览报告」共同的唯一排版实现。表格跨页时表头**重画一遍**——
/// 翻到第二页只剩一堆数字、不知道哪一列是哪一列，那种报告没法读。
/// </summary>
public static class ReportLayout
{
    private const string Ink = "#242424";
    private const string Muted = "#7b7b7b";
    private const string Rule = "#d8d5e6";
    private const string HeadFill = "#ece9f5";
    private const string BadInk = "#b03a2e";
    private const string BadFill = "#fbeae7";

    public static List<ReportPage> Paginate(ReportDoc doc, TextMetrics m, PageStyle? style = null)
    {
        var st = style ?? new PageStyle();
        var pages = new List<ReportPage>();
        var page = NewPage(pages);
        var y = st.Top;

        // ── 封面 ───────────────────────────────────────────────────
        y += 40;
        foreach (var line in m.Wrap(m.Sanitize(doc.Title), 22, st.ContentWidth))
        {
            page.Items.Add(new TextItem { X = st.Left, Y = y, Text = line, Size = 22, Bold = true, Color = Ink });
            y += 30;
        }
        page.Items.Add(new TextItem
        {
            X = st.Left, Y = y, Size = 11, Color = Muted,
            Text = m.Ellipsis(m.Sanitize(doc.Subtitle), 11, st.ContentWidth)
        });
        y += 20;
        page.Items.Add(new RectItem { X = st.Left, Y = y, W = st.ContentWidth, H = 1.4, Fill = "#6f5fa8" });
        y += 26;

        if (doc.Simulated)
            y = Notice(page, st, m, y, "本报告含仿真运行数据，不是真实实验结果。", true);

        foreach (var (k, v) in doc.Cover)
        {
            var line = st.Left;
            page.Items.Add(new TextItem { X = line, Y = y, Text = m.Sanitize(k), Size = 10, Color = Muted });
            foreach (var (row, i) in m.Wrap(m.Sanitize(v), 11, st.ContentWidth - 120).Select((r, i) => (r, i)))
            {
                page.Items.Add(new TextItem { X = line + 120, Y = y + i * 15, Text = row, Size = 11, Color = Ink });
            }
            y += 15 * Math.Max(1, m.Wrap(m.Sanitize(v), 11, st.ContentWidth - 120).Count) + 6;
        }

        y += 10;
        page.Items.Add(new RectItem { X = st.Left, Y = y, W = st.ContentWidth, H = 0.6, Fill = Rule });
        y += 16;
        var foot = m.Sanitize("本报告由 TecStudio 从执行引擎记录自动生成。表中「计划」来自启动时冻结的基线，"
                              + "「实际」来自运行记录；两种偏差分列，未做任何平滑或补值。");
        foreach (var line in m.Wrap(foot, 9, st.ContentWidth))
        {
            page.Items.Add(new TextItem { X = st.Left, Y = y, Text = line, Size = 9, Color = Muted });
            y += 13;
        }

        // ── 正文 ───────────────────────────────────────────────────
        page = NewPage(pages);
        y = st.BodyTop;

        foreach (var block in doc.Blocks)
        {
            switch (block)
            {
                case PageBreakBlock:
                    if (page.Items.Count > 0) { page = NewPage(pages); y = st.BodyTop; }
                    break;

                case HeadingBlock h:
                {
                    var size = h.Level == 1 ? 14.5 : 12;
                    var need = (h.Level == 1 ? 34.0 : 26) + 14;
                    if (y + need > st.BodyBottom) { page = NewPage(pages); y = st.BodyTop; }
                    if (h.Level == 1) y += 6;
                    page.Items.Add(new TextItem { X = st.Left, Y = y, Text = m.Sanitize(h.Text), Size = size, Bold = true, Color = Ink });
                    y += size + 5;
                    if (h.Level == 1)
                    {
                        page.Items.Add(new RectItem { X = st.Left, Y = y, W = st.ContentWidth, H = 0.8, Fill = Rule });
                        y += 10;
                    }
                    else y += 3;
                    break;
                }

                case ParaBlock p:
                {
                    var size = p.Muted ? 9.5 : 10.5;
                    foreach (var line in m.Wrap(m.Sanitize(p.Text), size, st.ContentWidth))
                    {
                        if (y + size + 4 > st.BodyBottom) { page = NewPage(pages); y = st.BodyTop; }
                        page.Items.Add(new TextItem { X = st.Left, Y = y, Text = line, Size = size, Color = p.Muted ? Muted : Ink });
                        y += size + 4.5;
                    }
                    y += 5;
                    break;
                }

                case NoticeBlock n:
                {
                    if (y + 30 > st.BodyBottom) { page = NewPage(pages); y = st.BodyTop; }
                    y = Notice(page, st, m, y, n.Text, n.Bad);
                    break;
                }

                case KvBlock kv:
                {
                    if (kv.Title is { Length: > 0 })
                    {
                        if (y + 30 > st.BodyBottom) { page = NewPage(pages); y = st.BodyTop; }
                        page.Items.Add(new TextItem { X = st.Left, Y = y, Text = m.Sanitize(kv.Title), Size = 12, Bold = true, Color = Ink });
                        y += 18;
                    }
                    var cols = Math.Max(1, kv.Columns);
                    var cw = st.ContentWidth / cols;
                    var rows = (kv.Pairs.Count + cols - 1) / cols;
                    for (var r = 0; r < rows; r++)
                    {
                        if (y + 18 > st.BodyBottom) { page = NewPage(pages); y = st.BodyTop; }
                        for (var c = 0; c < cols; c++)
                        {
                            var i = r * cols + c;
                            if (i >= kv.Pairs.Count) break;
                            var x = st.Left + c * cw;
                            page.Items.Add(new TextItem { X = x, Y = y, Text = m.Sanitize(kv.Pairs[i].K), Size = 9.5, Color = Muted });
                            page.Items.Add(new TextItem
                            {
                                X = x + 76, Y = y, Size = 10.5, Color = Ink,
                                Text = m.Ellipsis(m.Sanitize(kv.Pairs[i].V), 10.5, cw - 84)
                            });
                        }
                        y += 17;
                    }
                    y += 8;
                    break;
                }

                case TableBlock t:
                    (page, y) = Table(pages, page, st, m, t, y);
                    break;

                case ImageBlock img:
                {
                    var w = st.ContentWidth * Math.Clamp(img.WidthRatio, 0.1, 1);
                    var h = img.PixelWidth > 0 ? w * img.PixelHeight / img.PixelWidth : 0;
                    var head = img.Title is { Length: > 0 } ? 18.0 : 0;
                    // 图注要折行。实测吃过亏：「横轴为本通道启动后的时长…共 226 个采样点绘制。
                    // 本通道为仿真运行…」一行摆出去直接顶到纸外面，而那句「仿真运行」
                    // 恰恰是整张图上最要紧的一句
                    var noteLines = img.Note is { Length: > 0 }
                        ? m.Wrap(m.Sanitize(img.Note), 9, st.ContentWidth)
                        : new List<string>();
                    var note = noteLines.Count * 13.0;
                    if (y + h + head + note + 8 > st.BodyBottom) { page = NewPage(pages); y = st.BodyTop; }
                    if (head > 0)
                    {
                        page.Items.Add(new TextItem
                        {
                            X = st.Left, Y = y, Size = 12, Bold = true, Color = Ink,
                            Text = m.Ellipsis(m.Sanitize(img.Title!), 12, st.ContentWidth)
                        });
                        y += head;
                    }
                    page.Items.Add(new ImageItem
                    {
                        X = st.Left, Y = y, W = w, H = h,
                        Bgra = img.Bgra, PixelWidth = img.PixelWidth, PixelHeight = img.PixelHeight
                    });
                    page.Items.Add(new RectItem { X = st.Left, Y = y, W = w, H = h, Stroke = Rule });
                    y += h + 5;
                    foreach (var line in noteLines)
                    {
                        page.Items.Add(new TextItem { X = st.Left, Y = y, Text = line, Size = 9, Color = Muted });
                        y += 13;
                    }
                    y += 8;
                    break;
                }
            }
        }

        // 页眉 / 页脚最后统一加：加的时候才知道一共几页
        for (var i = 0; i < pages.Count; i++) Chrome(pages[i], st, m, doc, i, pages.Count);
        return pages;
    }

    private static ReportPage NewPage(List<ReportPage> pages)
    {
        var p = new ReportPage { Number = pages.Count + 1 };
        pages.Add(p);
        return p;
    }

    private static double Notice(ReportPage page, PageStyle st, TextMetrics m, double y, string text, bool bad)
    {
        var lines = m.Wrap(m.Sanitize(text), 10, st.ContentWidth - 20);
        var h = lines.Count * 14 + 12;
        page.Items.Add(new RectItem
        {
            X = st.Left, Y = y, W = st.ContentWidth, H = h,
            Fill = bad ? BadFill : "#fdf6e8", Stroke = bad ? "#e2b6ad" : "#e9d9ae"
        });
        for (var i = 0; i < lines.Count; i++)
            page.Items.Add(new TextItem
            {
                X = st.Left + 10, Y = y + 8 + i * 14, Text = lines[i], Size = 10,
                Color = bad ? BadInk : "#7a5c14"
            });
        return y + h + 12;
    }

    private static (ReportPage, double) Table(List<ReportPage> pages, ReportPage page, PageStyle st,
                                              TextMetrics m, TableBlock t, double y)
    {
        const double fs = 9, lineH = 11.5, padY = 4.5;

        if (t.Title is { Length: > 0 })
        {
            if (y + 40 > st.BodyBottom) { page = NewPage(pages); y = st.BodyTop; }
            page.Items.Add(new TextItem { X = st.Left, Y = y, Text = m.Sanitize(t.Title), Size = 12, Bold = true, Color = Ink });
            y += 19;
        }

        var total = t.Columns.Sum(c => c.Weight);
        var widths = t.Columns.Select(c => st.ContentWidth * c.Weight / total).ToArray();
        var xs = new double[t.Columns.Count];
        var x = st.Left;
        for (var i = 0; i < widths.Length; i++) { xs[i] = x; x += widths[i]; }

        // 表头**允许折两行**。A4 竖排放十一列时，「计划开始」这种四字列名一行放不下，
        // 掐成「计划…」之后一整列就没人认得出是什么了
        var headLines = t.Columns
            .Select((c, i) => Cut(m, m.Sanitize(c.Name), fs, widths[i] - 8, 2))
            .ToArray();
        var headH = headLines.Max(l => l.Count) * lineH + padY * 2;

        void Header(ReportPage p, double top)
        {
            p.Items.Add(new RectItem { X = st.Left, Y = top, W = st.ContentWidth, H = headH, Fill = HeadFill });
            for (var i = 0; i < t.Columns.Count; i++)
                for (var k = 0; k < headLines[i].Count; k++)
                {
                    var text = headLines[i][k];
                    var tx = t.Columns[i].Right ? xs[i] + widths[i] - 4 - m.Width(text, fs) : xs[i] + 4;
                    p.Items.Add(new TextItem
                    { X = tx, Y = top + padY + k * lineH, Text = text, Size = fs, Bold = true, Color = Ink });
                }
        }

        var minRow = lineH + padY * 2;
        if (y + headH + minRow > st.BodyBottom) { page = NewPage(pages); y = st.BodyTop; }
        Header(page, y);
        y += headH;

        for (var r = 0; r < t.Rows.Count; r++)
        {
            var cells = t.Rows[r];
            var lines = new List<string>[t.Columns.Count];
            var maxLines = 1;
            for (var i = 0; i < t.Columns.Count; i++)
            {
                var text = i < cells.Length ? m.Sanitize(cells[i]) : "";
                // 右对齐的列装的都是数字和时刻，**一律不折行**：
                // 「09:00:00」折成两行是「09:00:0」和「0」，看着像两个数
                var cap = t.Columns[i].Right ? 1 : Math.Max(1, t.MaxLines);
                lines[i] = Cut(m, text, fs, widths[i] - 8, cap);
                if (lines[i].Count > maxLines) maxLines = lines[i].Count;
            }
            var rowH = maxLines * lineH + padY * 2;

            if (y + rowH > st.BodyBottom)
            {
                page = NewPage(pages);
                y = st.BodyTop;
                Header(page, y);          // 跨页把表头重画一遍
                y += headH;
            }

            var bad = t.BadRows.Contains(r);
            if (bad) page.Items.Add(new RectItem { X = st.Left, Y = y, W = st.ContentWidth, H = rowH, Fill = BadFill });
            else if (r % 2 == 1) page.Items.Add(new RectItem { X = st.Left, Y = y, W = st.ContentWidth, H = rowH, Fill = "#fafafa" });

            for (var i = 0; i < t.Columns.Count; i++)
                for (var k = 0; k < lines[i].Count; k++)
                {
                    var text = lines[i][k];
                    if (text.Length == 0) continue;
                    var tx = t.Columns[i].Right ? xs[i] + widths[i] - 4 - m.Width(text, fs) : xs[i] + 4;
                    page.Items.Add(new TextItem
                    { X = tx, Y = y + padY + k * lineH, Text = text, Size = fs, Color = bad ? BadInk : Ink });
                }

            page.Items.Add(new RectItem { X = st.Left, Y = y + rowH, W = st.ContentWidth, H = 0.4, Fill = "#e8e6f0" });
            y += rowH;
        }

        if (t.Rows.Count == 0)
        {
            page.Items.Add(new TextItem { X = st.Left + 4, Y = y + padY, Text = m.Sanitize("（无记录）"), Size = fs, Color = Muted });
            y += minRow;
        }

        y += 6;
        if (t.Note is { Length: > 0 })
        {
            foreach (var line in m.Wrap(m.Sanitize(t.Note), 9, st.ContentWidth))
            {
                if (y + 13 > st.BodyBottom) { page = NewPage(pages); y = st.BodyTop; }
                page.Items.Add(new TextItem { X = st.Left, Y = y, Text = line, Size = 9, Color = Muted });
                y += 13;
            }
        }
        return (page, y + 10);
    }

    /// <summary>折行，最多 <paramref name="maxLines"/> 行；放不下的最后一行掐掉加省略号。</summary>
    private static List<string> Cut(TextMetrics m, string text, double size, double width, int maxLines)
    {
        if (text.Length == 0) return new List<string> { "" };
        var all = m.Wrap(text, size, width);
        if (all.Count <= maxLines) return all;
        var kept = all.Take(maxLines).ToList();
        // 掐掉的部分并回最后一行再压省略号，否则「…」会盖在一个本来放得下的字上
        kept[^1] = m.Ellipsis(string.Concat(all.Skip(maxLines - 1)), size, width);
        return kept;
    }

    /// <summary>页眉页脚。封面（第 1 页）不加页眉，页码从封面开始数。</summary>
    private static void Chrome(ReportPage page, PageStyle st, TextMetrics m, ReportDoc doc, int index, int count)
    {
        if (index > 0)
        {
            page.Items.Insert(0, new TextItem
            {
                X = st.Left, Y = st.Top, Size = 8.5, Color = Muted,
                Text = m.Ellipsis(m.Sanitize(doc.Title), 8.5, st.ContentWidth - 120)
            });
            page.Items.Insert(1, new RectItem { X = st.Left, Y = st.Top + 13, W = st.ContentWidth, H = 0.5, Fill = Rule });
        }

        var footY = st.Height - st.Bottom - 4;
        page.Items.Add(new RectItem { X = st.Left, Y = footY - 10, W = st.ContentWidth, H = 0.5, Fill = Rule });

        var left = m.Sanitize(doc.FooterLeft + (doc.Simulated ? "　·　仿真运行数据" : ""));
        page.Items.Add(new TextItem { X = st.Left, Y = footY, Text = m.Ellipsis(left, 8.5, st.ContentWidth - 90), Size = 8.5, Color = doc.Simulated ? BadInk : Muted });

        var pn = m.Sanitize($"第 {index + 1} / {count} 页");
        page.Items.Add(new TextItem
        {
            X = st.Left + st.ContentWidth - m.Width(pn, 8.5), Y = footY,
            Text = pn, Size = 8.5, Color = Muted
        });
    }
}
