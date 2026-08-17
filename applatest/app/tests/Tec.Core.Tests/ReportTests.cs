using Tec.Core.Export;
using Tec.Core.Records;
using Tec.Driver.Abi;
using Tec.Drivers.Simulator;
using Xunit;

namespace Tec.Core.Tests;

public class ReportTests
{
    private static async Task<Harness> RunAsync()
    {
        var h = new Harness(600);
        await h.ReactorChannelAsync(1);
        await h.ReactorChannelAsync(2);
        // **标上工艺阶段**：这样步骤表是最宽的那一种（阶段 + 控温两列都在）。
        // 不标的话空列会被摘掉，测出来的是一张比实际窄的表——
        // 「时刻被掐成 13:32:…」那条就正好漏过去
        var a = Harness.Mk(CommandSpecs.Control, ("target", 60d), ("rate", 8d));
        a.Phase = "升温";
        var b = Harness.Mk(CommandSpecs.Hold, ("dur", 2d));
        b.Phase = "保温";
        var c = Harness.Mk(CommandSpecs.Control, ("target", 20d), ("rate", 4d), ("obj", "夹套 Tj"));
        c.Phase = "结晶";
        var recipe = Harness.RecipeOf("降温结晶", a, b, c);
        h.Engine.StartChannel(1, recipe, "王工");
        await Task.Delay(120);
        h.Engine.StartChannel(2, recipe, "王工");
        await h.Engine.Runner(1)!.Completion;
        await h.Engine.Runner(2)!.Completion;
        return h;
    }

    private static ExportMeta Meta() => new()
    {
        Experiment = "苯甲酸重结晶 · 四通道平行",
        Operator = "王工",
        ExportedAt = new DateTimeOffset(2026, 8, 17, 14, 30, 0, TimeSpan.FromHours(8)),
        Signer = "王工",
        BenchName = "四通道平行合成台面",
        Interval = "10 s",
        TimeBaseText = "绝对时间",
        AppVersion = "1.0.0.0",
        TargetDir = @"D:\TecStudio\exports"
    };

    /// <summary>一张 4:3 的假图，验证图片真的被排进去、也真的写进了 PDF。</summary>
    private static ImageBlock Chart(string title)
    {
        const int w = 320, h = 180;
        var px = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                var line = Math.Abs(y - (int)(h / 2 + 60 * Math.Sin(x * 0.05))) < 2;
                px[i] = line ? (byte)32 : (byte)250;        // B
                px[i + 1] = line ? (byte)32 : (byte)250;    // G
                px[i + 2] = line ? (byte)224 : (byte)250;   // R
                px[i + 3] = 255;
            }
        return new ImageBlock
        {
            Title = title, Bgra = px, PixelWidth = w, PixelHeight = h,
            // 真机上的图注就是这么长的一句。短句子测不出「图注没折行」——
            // 那条 bug 是在界面上看见字顶出纸外才发现的
            Note = "横轴为本通道启动后的时长（0:18:37），纵轴左 -50 ~ 190 ℃、右 pH 0 ~ 14；"
                   + "共 226 个采样点绘制。本通道为仿真运行，不是真实实验数据。"
        };
    }

    private static (ReportDoc Doc, List<ReportPage> Pages) Layout(Harness h, ReportTemplate template,
                                                                 out TextMetrics metrics)
    {
        var opt = new ReportOptions { Template = template };
        opt.Charts.Add(Chart("CH1 · 釜温 / 夹套温度"));
        opt.Files.Add(("data.csv", new string('a', 64)));
        opt.Files.Add(("steps.csv", new string('b', 64)));
        var doc = ReportBuilder.Build(h.Engine.Record, h.Pipeline, h.Catalog, Meta(), opt);
        metrics = new TextMetrics(FontFinder.Find()!);
        return (doc, ReportLayout.Paginate(doc, metrics));
    }

    [Fact]
    public async Task 完整报告排得出多页且每页都有页码()
    {
        if (FontFinder.Find() is null) return;
        await using var h = await RunAsync();
        var (_, pages) = Layout(h, ReportTemplate.Full, out _);

        Assert.True(pages.Count >= 2, "完整报告至少该有封面加正文两页");
        foreach (var p in pages)
            Assert.Contains(p.Items.OfType<TextItem>(), t => t.Text.Contains($"第 {p.Number} /", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 三种模板给出三种篇幅()
    {
        if (FontFinder.Find() is null) return;
        await using var h = await RunAsync();
        var full = Layout(h, ReportTemplate.Full, out _).Pages.Count;
        var brief = Layout(h, ReportTemplate.Summary, out _).Pages.Count;
        var glp = Layout(h, ReportTemplate.Glp, out _).Pages.Count;

        Assert.True(brief <= full, $"摘要 {brief} 页竟然不比完整报告 {full} 页短");
        Assert.True(glp >= full, $"GLP 报告 {glp} 页竟然不比完整报告 {full} 页长");
    }

    [Fact]
    public async Task 没有一行字超出版心()
    {
        if (FontFinder.Find() is null) return;
        await using var h = await RunAsync();
        var (_, pages) = Layout(h, ReportTemplate.Glp, out var m);
        var st = new PageStyle();

        foreach (var p in pages)
            foreach (var t in p.Items.OfType<TextItem>())
            {
                var right = t.X + m.Width(t.Text, t.Size);
                Assert.True(right <= st.Width - st.Right + 0.6,
                    $"第 {p.Number} 页「{t.Text}」右边到了 {right:F1}，版心右界是 {st.Width - st.Right}");
                Assert.True(t.Y >= 0 && t.Y <= st.Height, $"第 {p.Number} 页「{t.Text}」跑到纸外了：Y={t.Y:F1}");
            }
    }

    [Fact]
    public async Task 表格跨页时把表头重画一遍()
    {
        if (FontFinder.Find() is null) return;
        await using var h = await RunAsync();
        var (_, pages) = Layout(h, ReportTemplate.Glp, out _);

        // 「结束原因」是步骤表独有的列名；它出现在几页上，就说明表头跟着重画了几次
        var withHead = pages.Count(p => p.Items.OfType<TextItem>().Any(t => t.Text == "结束原因"));
        Assert.True(withHead >= 1);
    }

    [Fact]
    public async Task 写得出能被解析的PDF且含内嵌字体()
    {
        if (FontFinder.Find() is not { } font) return;
        await using var h = await RunAsync();
        var (doc, pages) = Layout(h, ReportTemplate.Glp, out _);

        using var ms = new MemoryStream();
        PdfWriter.Write(ms, pages, font, new PageStyle(), doc.Title, "王工");
        var bytes = ms.ToArray();
        Dump.Save("report.pdf", bytes);

        var head = System.Text.Encoding.ASCII.GetString(bytes, 0, 9);
        Assert.Equal("%PDF-1.7\n", head);

        var text = System.Text.Encoding.Latin1.GetString(bytes);
        Assert.Contains("/Type/Catalog", text, StringComparison.Ordinal);
        Assert.Contains("/Subtype/Type0", text, StringComparison.Ordinal);
        Assert.Contains("/Encoding/Identity-H", text, StringComparison.Ordinal);
        Assert.Contains("/FontFile2", text, StringComparison.Ordinal);
        Assert.Contains("/ToUnicode", text, StringComparison.Ordinal);
        Assert.Contains("/Subtype/Image", text, StringComparison.Ordinal);
        Assert.EndsWith("%%EOF\n", text, StringComparison.Ordinal);

        // xref 里每一条偏移都得真的落在一个 "N 0 obj" 上，否则阅读器打开就报错
        var xrefAt = text.LastIndexOf("startxref", StringComparison.Ordinal);
        Assert.True(xrefAt > 0);
    }

    [Fact]
    public async Task 仿真数据在报告里必须标出来()
    {
        if (FontFinder.Find() is null) return;
        await using var h = await RunAsync();
        var (doc, pages) = Layout(h, ReportTemplate.Full, out _);

        Assert.True(doc.Simulated);
        var all = pages.SelectMany(p => p.Items.OfType<TextItem>()).Select(t => t.Text).ToList();
        Assert.Contains(all, t => t.Contains("仿真", StringComparison.Ordinal));
        // 每一页的页脚都得带着这句，单页被撕下来也认得出
        foreach (var p in pages)
            Assert.Contains(p.Items.OfType<TextItem>(), t => t.Text.Contains("仿真运行数据", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 写得出能被解析的Word文件()
    {
        if (FontFinder.Find() is null) return;
        await using var h = await RunAsync();
        var (doc, _) = Layout(h, ReportTemplate.Full, out _);

        using var ms = new MemoryStream();
        DocxWriter.Write(ms, doc, "王工");
        var bytes = ms.ToArray();
        Dump.Save("report.docx", bytes);

        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(bytes),
                                                             System.IO.Compression.ZipArchiveMode.Read);
        foreach (var want in new[]
        {
            "[Content_Types].xml", "_rels/.rels", "word/document.xml",
            "word/_rels/document.xml.rels", "word/styles.xml", "word/footer1.xml"
        })
            Assert.True(zip.GetEntry(want) is not null, "包里少了 " + want);

        Assert.Contains(zip.Entries, e => e.FullName.StartsWith("word/media/", StringComparison.Ordinal));

        foreach (var e in zip.Entries)
        {
            if (!e.FullName.EndsWith(".xml", StringComparison.Ordinal)
                && !e.FullName.EndsWith(".rels", StringComparison.Ordinal)) continue;
            using var s = e.Open();
            var ex = Record.Exception(() => System.Xml.Linq.XDocument.Load(s));
            Assert.True(ex is null, $"{e.FullName} 不是合法 XML：{ex?.Message}");
        }
    }

    [Fact]
    public void 图片编码成真PNG()
    {
        var block = Chart("t");
        var png = Png.FromBgra(block.Bgra, block.PixelWidth, block.PixelHeight);
        Assert.True(png.Length > 100);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png.Take(8).ToArray());
        // 宽高写在 IHDR 里，大端
        Assert.Equal(block.PixelWidth, (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19]);
        Assert.Equal(block.PixelHeight, (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23]);
        Dump.Save("chart.png", png);
    }

    [Fact]
    public async Task 步骤表里的时刻列不许被掐掉()
    {
        if (FontFinder.Find() is not { } font) return;
        await using var h = await RunAsync();
        var (doc, _) = Layout(h, ReportTemplate.Full, out var m);

        // 概要页的通道一览里也有一列叫「步骤」（步数），按「结束原因」认步骤表
        var table = doc.Blocks.OfType<TableBlock>().First(t => t.Columns.Any(c => c.Name == "结束原因"));
        var widths = Widths(table);

        for (var i = 0; i < table.Columns.Count; i++)
        {
            if (!table.Columns[i].Right) continue;      // 右对齐的都是时刻与偏差
            foreach (var row in table.Rows)
            {
                var text = m.Sanitize(row[i]);
                if (text.Length == 0) continue;
                // 掐成「13:32:…」的表没法拿来对时间，而它存在的理由就是对时间
                Assert.True(m.Width(text, 9) <= widths[i] - 8,
                    $"「{table.Columns[i].Name}」列放不下「{text}」：需要 {m.Width(text, 9):F1}，只有 {widths[i] - 8:F1}");
            }
        }
    }

    private static double[] Widths(TableBlock t)
    {
        var st = new PageStyle();
        var total = t.Columns.Sum(c => c.Weight);
        return t.Columns.Select(c => st.ContentWidth * c.Weight / total).ToArray();
    }
}
