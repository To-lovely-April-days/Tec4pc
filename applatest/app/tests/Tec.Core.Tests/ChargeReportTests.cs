using Tec.Core.Chemistry;
using Tec.Core.Compounds;
using Tec.Core.Export;
using Tec.Core.Records;
using Tec.Driver.Abi;
using Tec.Drivers.Simulator;
using Xunit;

namespace Tec.Core.Tests;

public class ChargeReportTests
{
    /// <summary>
    /// 名字里带全角括号、逗号和中文——iControl 的已知毛病正是这种名字会让报告里
    /// 某些项填不上。从库到配料表到 PDF / Word / Excel，整条链拿它跑一遍。
    /// </summary>
    private const string Hard = "2,4-二硝基苯甲醚（内部代号 A-7）";

    private static readonly Compound[] Lib =
    {
        new() { Cas = "119-27-7", Name = Hard, Mw = 198.13, Density = 1.34, Purity = 98.5 },
        new() { Cas = "7697-37-2", Name = "硝酸 65%", Mw = 63.01, Density = 1.39, Purity = 65 },
        new() { Cas = "108-88-3", Name = "甲苯", Mw = 92.14, Density = 0.8669 }
    };

    private static ChargeTable Table()
    {
        var t = new ChargeTable { VesselVolume = 250 };
        t.Items.Add(new ChargeItem
        {
            Cas = "119-27-7", Name = Hard, Role = ChargeRole.Limiting,
            Basis = ChargeBasis.Quantity, Amount = 19.813, Unit = ChargeUnit.Gram,
            Batch = "20260817-3", Supplier = "所内合成", ActualMass = 19.82
        });
        t.Items.Add(new ChargeItem
        {
            Cas = "7697-37-2", Name = "硝酸 65%", Role = ChargeRole.Reagent,
            Basis = ChargeBasis.Equivalents, Amount = 1.2, Batch = "N-0714"
        });
        t.Items.Add(new ChargeItem
        {
            Cas = "108-88-3", Name = "甲苯", Role = ChargeRole.Solvent,
            Basis = ChargeBasis.Volumes, Amount = 6
        });
        t.Items.Add(new ChargeItem
        {
            Name = "产物（粗品）", Role = ChargeRole.Product,
            Basis = ChargeBasis.Equivalents, Amount = 1, Mw = 243.13, ActualMass = 20.4
        });
        return t;
    }

    private static async Task<Harness> RunAsync(ChargeTable? charge)
    {
        var h = new Harness(600);
        await h.ReactorChannelAsync(1);
        var recipe = Harness.RecipeOf("硝化",
            Harness.Mk(CommandSpecs.Control, ("target", 40d), ("rate", 10d)),
            Harness.Mk(CommandSpecs.Hold, ("dur", 1d)));

        h.Engine.StartChannel(1, recipe, "王工", charge: charge);
        await h.Engine.Runner(1)!.Completion;
        return h;
    }

    private static ExportMeta Meta() => new()
    {
        Experiment = "硝化 · 配料自测",
        Operator = "王工",
        ExportedAt = new DateTimeOffset(2026, 8, 17, 14, 30, 0, TimeSpan.FromHours(8)),
        BenchName = "四通道平行合成台面"
    };

    private static ReportDoc Report(Harness h) =>
        ReportBuilder.Build(h.Engine.Record, h.Pipeline, h.Catalog, Meta(),
            new ReportOptions { Trend = false, Library = Lib });

    private static TableBlock ChargeTableOf(ReportDoc doc)
        => doc.Blocks.OfType<TableBlock>().First(t => t.Columns.Any(c => c.Name == "应称量 g"));

    // ── 报告 ────────────────────────────────────────────────────────

    [Fact]
    public async Task 报告里有配料表这一节且数字对得上()
    {
        await using var h = await RunAsync(Table());
        var doc = Report(h);

        Assert.Contains(doc.Blocks.OfType<HeadingBlock>(), b => b.Text.Contains("配料表与化学计量"));
        var table = ChargeTableOf(doc);
        Assert.Equal(4, table.Rows.Count);

        // 限制试剂：19.813 g × 98.5 % ÷ 198.13 = 98.5 mmol
        var lim = table.Rows[0];
        Assert.Equal(Hard, lim[0]);                       // 中文名 + 全角括号 + 逗号，原样
        Assert.Equal("限制试剂", lim[1]);
        Assert.Equal("98.5", lim[4]);                     // mmol
        Assert.Equal("19.82", lim[7]);                    // 实投
        Assert.Contains("20260817-3", lim[8]);
        Assert.Contains("所内合成", lim[8]);

        // 硝酸按 1.2 当量 + 65 % 纯度校正：118.2 mmol × 63.01 ÷ 0.65 = 11.458 g
        var acid = table.Rows[1];
        Assert.Equal("1.2", acid[3]);
        Assert.Equal((1.2 * 98.5 / 1000 * 63.01 / 0.65).ToString("0.##"), acid[5]);
        // 算的时候用的那几个数排在表脚：复核的人要能拿它们把整张表重算一遍
        Assert.Contains("硝酸 65% M 63.01 / 65 % / ρ 1.39", table.Note!);
    }

    [Fact]
    public async Task 表脚写清限制试剂合计与收率()
    {
        await using var h = await RunAsync(Table());
        var note = ChargeTableOf(Report(h)).Note!;

        Assert.Contains("限制试剂", note);
        Assert.Contains(Hard, note);
        Assert.Contains("98.5 mmol", note);
        Assert.Contains("釜容 250 mL", note);
        // 理论产量 98.5 mmol × 243.13 = 23.948 g，实际 20.4 g → 85.2 %
        Assert.Contains("理论产量", note);
        Assert.Contains("收率 85.2 %", note);
        // 体积相加只是个估计，得说清楚，不能摆成实测值
        Assert.Contains("实际体积会有出入", note);
    }

    [Fact]
    public async Task 报告里说清应称量是按纯度折算过的()
    {
        // 不写这一句的话，复核的人拿 mmol × M 一算对不上，会以为报告算错了
        await using var h = await RunAsync(Table());
        var text = string.Join("\n", Report(h).Blocks.OfType<ParaBlock>().Select(p => p.Text));

        Assert.Contains("按纯度折算", text);
        Assert.Contains("限制试剂定量", text);
        Assert.Contains("冻结的基线", text);
    }

    [Fact]
    public async Task 没有配料表的那一炉整节不出现()
    {
        // 只跑温控曲线的实验很常见。硬摆一张空表，读报告的人会以为这一炉什么都没投
        await using var h = await RunAsync(null);
        var doc = Report(h);

        Assert.DoesNotContain(doc.Blocks.OfType<HeadingBlock>(), b => b.Text.Contains("配料表"));
        Assert.DoesNotContain(doc.Blocks.OfType<TableBlock>(), t => t.Columns.Any(c => c.Name == "应称量 g"));
    }

    [Fact]
    public async Task 缺物性的行标红并在正文里点名说缺什么()
    {
        var t = Table();
        t.Items.Add(new ChargeItem
        {
            Name = "催化剂 C-3", Role = ChargeRole.Catalyst,
            Basis = ChargeBasis.Equivalents, Amount = 0.05      // 不连库、没填 M
        });

        await using var h = await RunAsync(t);
        var doc = Report(h);
        var table = ChargeTableOf(doc);

        Assert.Contains(4, table.BadRows);
        Assert.Equal("", table.Rows[4][5]);            // 应称量是空格子，不是 0
        var notices = doc.Blocks.OfType<NoticeBlock>().Select(n => n.Text).ToList();
        Assert.Contains(notices, n => n.Contains("催化剂 C-3") && n.Contains("摩尔质量"));
        Assert.Contains(notices, n => n.Contains("没有替它们填过任何数"));
    }

    [Fact]
    public async Task 章号按实际排进去的节数走不跳号()
    {
        // 从前章号是每处写死的：取消「步骤执行记录」，目录就从二直接跳到四
        await using var h = await RunAsync(Table());
        var doc = ReportBuilder.Build(h.Engine.Record, h.Pipeline, h.Catalog, Meta(),
            new ReportOptions { Trend = false, Steps = false, Library = Lib });

        var heads = doc.Blocks.OfType<HeadingBlock>().Where(b => b.Level == 1)
                       .Select(b => b.Text).Where(t => !t.StartsWith("附录", StringComparison.Ordinal))
                       .ToList();

        // 关掉了「步骤执行记录」，剩下的几节要连号排下来，中间不许缺一个数
        var want = new[] { "一", "二", "三", "四", "五", "六" }.Take(heads.Count).ToArray();
        Assert.Equal(want, heads.Select(t => t[..1]).ToArray());
        Assert.DoesNotContain(heads, t => t.Contains("步骤执行记录", StringComparison.Ordinal));
        Assert.Contains("配料表与化学计量", heads[1]);
    }

    [Fact]
    public async Task 配料表的数字列一个都不许被掐掉()
    {
        // PDF 上实测抓到过：应称量 11.4577 被排成「11.4…」。
        // 一张写着「11.4…」的配料表没法拿去称量，而它存在的理由就是拿去称量
        if (FontFinder.Find() is not { } font) return;
        await using var h = await RunAsync(Table());

        var m = new TextMetrics(font);
        var table = ChargeTableOf(Report(h));
        var st = new PageStyle();
        var total = table.Columns.Sum(c => c.Weight);

        // 数字列、以及「角色」「基准」这两列固定词表，都得一行放得下
        var oneLine = new[] { "角色", "基准" };
        for (var i = 0; i < table.Columns.Count; i++)
        {
            var col = table.Columns[i];
            if (!col.Right && !oneLine.Contains(col.Name)) continue;
            var w = st.ContentWidth * col.Weight / total;
            foreach (var row in table.Rows)
            {
                var text = m.Sanitize(row[i]);
                if (text.Length == 0) continue;
                Assert.True(m.Width(text, 9) <= w - 8,
                    $"「{col.Name}」列放不下「{text}」："
                    + $"需要 {m.Width(text, 9):F1}，只有 {w - 8:F1}");
            }
        }

        // 词表里最长的那两个也得放得下，哪怕这一炉的表里正好没出现
        var role = table.Columns.ToList().FindIndex(c => c.Name == "角色");
        var basis = table.Columns.ToList().FindIndex(c => c.Name == "基准");
        Assert.True(m.Width("限制试剂", 9) <= st.ContentWidth * table.Columns[role].Weight / total - 8,
            "「角色」列放不下「限制试剂」");
        Assert.True(m.Width("给定 mmol", 9) <= st.ContentWidth * table.Columns[basis].Weight / total - 8,
            "「基准」列放不下「给定 mmol」");
    }

    [Fact]
    public async Task 行首不许坐一个孤零零的右引号或句号()
    {
        // PDF 上实测见到过：「应称量 ↵ 」是理论产量。中文排版的「避头」，
        // 收尾的标点该跟着上一行走
        if (FontFinder.Find() is not { } font) return;
        await using var h = await RunAsync(Table());

        var pages = ReportLayout.Paginate(Report(h), new TextMetrics(font));
        foreach (var t in pages.SelectMany(p => p.Items.OfType<TextItem>()))
        {
            if (t.Text.Length == 0) continue;
            Assert.False("，。、；：？！）】》」』”’".Contains(t.Text[0]),
                $"这一行以「{t.Text[0]}」开头：{t.Text}");
        }
    }

    [Fact]
    public async Task 正文里不许漏出星号这种记号()
    {
        // 源码注释里用 **强调** 是给读代码的人看的。抄进报告正文，
        // 印出来就是一串莫名其妙的星号——PDF 上实测见到过
        await using var h = await RunAsync(Table());
        foreach (var p in Report(h).Blocks.OfType<ParaBlock>())
            Assert.DoesNotContain("**", p.Text, StringComparison.Ordinal);
    }

    // ── 三种文件 ────────────────────────────────────────────────────

    [Fact]
    public async Task 排到纸上那个中文名一个字不缺也没被掐掉()
    {
        if (FontFinder.Find() is not { } font) return;
        await using var h = await RunAsync(Table());

        var pages = ReportLayout.Paginate(Report(h), new TextMetrics(font));
        var items = pages.SelectMany(p => p.Items.OfType<TextItem>()).Select(t => t.Text).ToList();

        // 一格放不下会折成两行 = 两个图元，所以比对前先把空白去掉。
        // 折行不增删字符，掐字才会——掐了这里就对不上
        var flat = string.Concat(items).Replace(" ", "").Replace("　", "");
        string Bare(string x) => x.Replace(" ", "").Replace("　", "");

        Assert.Contains(Bare("配料表与化学计量"), flat, StringComparison.Ordinal);
        // 全角括号 + 逗号 + 中文，20 个字。掐掉的话名字后半截就没了
        Assert.Contains(Bare(Hard), flat, StringComparison.Ordinal);
        Assert.Contains(Bare("硝酸 65%"), flat, StringComparison.Ordinal);
        Assert.Contains("所内合成", flat, StringComparison.Ordinal);

        // 省略号 = 有东西被掐了。配料表上任何一格被掐都是不能接受的
        Assert.DoesNotContain(items, t => t.Contains('…'));
    }

    [Fact]
    public async Task 写得出真的PDF而且字都在里头()
    {
        if (FontFinder.Find() is not { } font) return;
        await using var h = await RunAsync(Table());

        var doc = Report(h);
        var pages = ReportLayout.Paginate(doc, new TextMetrics(font));
        using var ms = new MemoryStream();
        PdfWriter.Write(ms, pages, font, new PageStyle(), doc.Title, Meta().Operator);
        var bytes = ms.ToArray();
        Dump.Save("charge.pdf", bytes);

        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.True(bytes.Length > 5000, "PDF 太小，多半没写进去内容");
    }

    [Fact]
    public async Task Word里那个中文名一个字不缺()
    {
        if (FontFinder.Find() is null) return;
        await using var h = await RunAsync(Table());

        using var ms = new MemoryStream();
        DocxWriter.Write(ms, Report(h), "王工");
        var bytes = ms.ToArray();
        Dump.Save("charge.docx", bytes);

        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(bytes),
                                                             System.IO.Compression.ZipArchiveMode.Read);
        using var entry = zip.GetEntry("word/document.xml")!.Open();
        var xml = new StreamReader(entry).ReadToEnd();

        Assert.Contains("配料表与化学计量", xml, StringComparison.Ordinal);
        Assert.Contains(Hard, xml, StringComparison.Ordinal);
        Assert.Contains("所内合成", xml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Excel里配料表是一张平表且数字是数字()
    {
        await using var h = await RunAsync(Table());
        var opt = new ExportOptions { Library = Lib };
        var sheets = WorkbookExporter.Build(h.Engine.Record, h.Pipeline, opt, h.Catalog, Meta());

        var sheet = Assert.Single(sheets, s => s.Name == "配料表");
        var head = sheet.Rows[0].Select(c => c.Text).ToList();
        Assert.Equal("通道", head[0]);
        Assert.Contains("应称量 g", head);

        var lim = sheet.Rows[1];
        Assert.Equal("CH1", lim[0].Text);
        Assert.Equal(Hard, lim[1].Text);
        Assert.Equal("119-27-7", lim[2].Text);
        // **数字要以数字身份进表**：文本的话这一列排序会把 100 排在 99 前面
        Assert.Equal(98.5, lim[11].Num!.Value, 3);
        Assert.Null(lim[11].Text);
    }

    [Fact]
    public async Task 带配料表的工作簿写出来是一个能打开的xlsx()
    {
        // 配料表那一页新用了「四位小数」这个格式，而 numFmts / cellXfs 的 count
        // 写错一个数，整本工作簿在 Excel 里就是「文件已损坏」——这种错自己看 XML 看不出来
        await using var h = await RunAsync(Table());
        var sheets = WorkbookExporter.Build(h.Engine.Record, h.Pipeline,
            new ExportOptions { Library = Lib }, h.Catalog, Meta());

        using var ms = new MemoryStream();
        XlsxWriter.Write(ms, sheets, "配料自测", "王工");
        var bytes = ms.ToArray();
        Dump.Save("charge.xlsx", bytes);

        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(bytes),
                                                             System.IO.Compression.ZipArchiveMode.Read);
        using var styles = zip.GetEntry("xl/styles.xml")!.Open();
        var xml = System.Xml.Linq.XDocument.Load(styles);
        System.Xml.Linq.XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

        // count 属性必须跟实际条数对得上
        foreach (var (tag, child) in new[] { ("numFmts", "numFmt"), ("cellXfs", "xf") })
        {
            var node = xml.Descendants(ns + tag).Single();
            Assert.Equal(node.Elements(ns + child).Count().ToString(),
                         node.Attribute("count")!.Value);
        }

        // 每个格子引用的样式号都得在 cellXfs 里存在
        var styleCount = xml.Descendants(ns + "cellXfs").Single().Elements(ns + "xf").Count();
        Assert.True((int)XlStyle.Num4 < styleCount, "Num4 的样式号超出了 cellXfs 的范围");
    }

    [Fact]
    public async Task 没有配料表就不出那一页()
    {
        await using var h = await RunAsync(null);
        var sheets = WorkbookExporter.Build(h.Engine.Record, h.Pipeline,
            new ExportOptions { Library = Lib }, h.Catalog, Meta());
        Assert.DoesNotContain(sheets, s => s.Name == "配料表");
    }

    // ── 物性快照（CH-D1）：改库改不动历史报告 ───────────────────────

    /// <summary>盖过章的那张表——所有连库行的物性都拷在行上。</summary>
    private static ChargeTable Stamped()
    {
        var t = Table();
        var when = new DateTimeOffset(2026, 8, 17, 9, 0, 0, TimeSpan.FromHours(8));
        Assert.Equal(3, ChargeSnapshot.Migrate(t, Lib, 5, when).Count);
        return t;
    }

    [Fact]
    public async Task 核心回归_归档之后改库报告里的数纹丝不动()
    {
        await using var h = await RunAsync(Stamped());

        string VolumeOf(Compound[] lib)
        {
            var doc = ReportBuilder.Build(h.Engine.Record, h.Pipeline, h.Catalog, Meta(),
                new ReportOptions { Trend = false, Library = lib });
            // 硝酸那一行的「体积 mL」——密度被改的话最先漂的就是它
            var cols = ChargeTableOf(doc).Columns.ToList();
            var vol = cols.FindIndex(c => c.Name == "体积 mL");
            return ChargeTableOf(doc).Rows.First(r => r[0] == "硝酸 65%")[vol];
        }

        var before = VolumeOf(Lib);

        // 归档之后有人把库里硝酸的密度从 1.39 改成 1.50——历史报告不许跟着变
        var edited = Lib.Select(c => new Compound
        {
            Cas = c.Cas, Name = c.Name, Mw = c.Mw,
            Density = c.Cas == "7697-37-2" ? 1.50 : c.Density, Purity = c.Purity
        }).ToArray();

        Assert.Equal(before, VolumeOf(edited));
        Assert.NotEqual("", before);               // 空对空的「不变」不算数
    }

    [Fact]
    public async Task 盖过章的炉子报告不出早于快照那句话_表脚带上库版本()
    {
        await using var h = await RunAsync(Stamped());
        var doc = Report(h);

        Assert.DoesNotContain(doc.Blocks.OfType<NoticeBlock>(),
                              n => n.Text.Contains("早于物性快照机制"));
        Assert.Contains("化合物库第 5 版", ChargeTableOf(doc).Note);
    }

    [Fact]
    public async Task 没盖章的老炉子报告把话挑明()
    {
        await using var h = await RunAsync(Table());   // 未盖章：快照机制之前的归档
        var doc = Report(h);

        var notice = Assert.Single(doc.Blocks.OfType<NoticeBlock>(),
                                   n => n.Text.Contains("早于物性快照机制"));
        Assert.Contains("CH1", notice.Text);
        // 数照算——不能让老归档全变哑巴
        var cols = ChargeTableOf(doc).Columns.ToList();
        var mmol = cols.FindIndex(c => c.Name == "mmol");
        Assert.NotEqual("", ChargeTableOf(doc).Rows[0][mmol]);
    }

    [Fact]
    public async Task Excel里有物性快照一列_未盖章的行照实标()
    {
        await using var h = await RunAsync(Table());
        var sheets = WorkbookExporter.Build(h.Engine.Record, h.Pipeline,
            new ExportOptions { Library = Lib }, h.Catalog, Meta());
        var sheet = Assert.Single(sheets, s => s.Name == "配料表");

        var head = sheet.Rows[0].Select(c => c.Text).ToList();
        var snap = head.IndexOf("物性快照");
        Assert.True(snap >= 0);
        Assert.Contains("未快照", sheet.Rows[1][snap].Text);
        // 不连库的产物行没有「未快照」的帽子——它根本没有库可快照
        var product = sheet.Rows.First(r => r.Length > 1 && r[1].Text == "产物（粗品）");
        Assert.Equal("", product[snap].Text);
    }

    [Fact]
    public async Task 归档读回来配料表还在报告照样出得来()
    {
        await using var h = await RunAsync(Table());
        var root = Path.Combine(Path.GetTempPath(), "tec-charge-" + Guid.NewGuid().ToString("N"));
        try
        {
            var archive = new RunArchive(root);
            archive.Save(h.Engine.Record, h.Pipeline, h.Clock.Now);
            var back = archive.Load()[0];

            var charge = back.Record.Channels[0].Baseline.Charge;
            Assert.NotNull(charge);
            Assert.Equal(4, charge!.Items.Count);
            Assert.Equal(Hard, charge.Items[0].Name);
            Assert.Equal(19.82, charge.Items[0].ActualMass);

            var doc = ReportBuilder.Build(back.Record, back.Samples, h.Catalog, Meta(),
                new ReportOptions { Trend = false, Library = Lib });
            Assert.Equal(Hard, ChargeTableOf(doc).Rows[0][0]);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
