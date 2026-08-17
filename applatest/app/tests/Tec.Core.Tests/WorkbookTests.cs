using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Tec.Core.Catalog;
using Tec.Core.Export;
using Tec.Core.Records;
using Tec.Driver.Abi;
using Tec.Drivers.Simulator;
using Xunit;

namespace Tec.Core.Tests;

public class WorkbookTests
{
    private static async Task<Harness> RunAsync()
    {
        var h = new Harness(600);
        await h.ReactorChannelAsync(1);
        await h.ReactorChannelAsync(2);
        var recipe = Harness.RecipeOf("工作簿用",
            Harness.Mk(CommandSpecs.Control, ("target", 60d), ("rate", 8d)),
            Harness.Mk(CommandSpecs.Hold, ("dur", 2d)));
        h.Engine.StartChannel(1, recipe, "测试员");
        await Task.Delay(120);
        h.Engine.StartChannel(2, recipe, "测试员");
        await h.Engine.Runner(1)!.Completion;
        await h.Engine.Runner(2)!.Completion;
        return h;
    }

    private static ExportMeta Meta() => new()
    {
        Experiment = "工作簿导出自测",
        Operator = "测试员",
        ExportedAt = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero),
        Interval = "10 s",
        TimeBaseText = "绝对时间",
        AppVersion = "1.0.0.0"
    };

    private static byte[] Build(Harness h, out IReadOnlyList<XlSheet> sheets)
    {
        var opt = new ExportOptions { Grid = TimeSpan.FromSeconds(10) };
        opt.Channels.AddRange(h.Engine.Record.StartedChannels);
        sheets = WorkbookExporter.Build(h.Engine.Record, h.Pipeline, opt, h.Catalog, Meta());
        using var ms = new MemoryStream();
        XlsxWriter.Write(ms, sheets, "实验记录", "测试员");
        return ms.ToArray();
    }

    [Fact]
    public async Task 每通道一页且概要与步骤事件都在()
    {
        await using var h = await RunAsync();
        var bytes = Build(h, out var sheets);
        Dump.Save("workbook.xlsx", bytes);

        var names = sheets.Select(s => s.Name).ToList();
        Assert.Equal("概要", names[0]);
        Assert.Contains("CH1", names);
        Assert.Contains("CH2", names);
        Assert.Contains("步骤记录", names);
        Assert.Contains("事件与报警", names);
    }

    [Fact]
    public async Task 包里该有的几份都在且是合法XML()
    {
        await using var h = await RunAsync();
        var bytes = Build(h, out var sheets);

        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        foreach (var want in new[]
        {
            "[Content_Types].xml", "_rels/.rels", "xl/workbook.xml", "xl/_rels/workbook.xml.rels",
            "xl/styles.xml", "xl/sharedStrings.xml", "xl/worksheets/sheet1.xml",
            "docProps/core.xml", "docProps/app.xml"
        })
            Assert.True(zip.GetEntry(want) is not null, "包里少了 " + want);

        // 每一份都得能被 XML 解析器读下来。少一个转义就是「文件已损坏」
        foreach (var e in zip.Entries)
        {
            using var s = e.Open();
            var ex = Record.Exception(() => XDocument.Load(s));
            Assert.True(ex is null, $"{e.FullName} 不是合法 XML：{ex?.Message}");
        }
    }

    [Fact]
    public async Task 数值以数字身份写进去而不是文本()
    {
        await using var h = await RunAsync();
        var bytes = Build(h, out _);
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);

        // sheet2 = CH1
        using var s = zip.GetEntry("xl/worksheets/sheet2.xml")!.Open();
        var doc = XDocument.Load(s);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var body = doc.Descendants(ns + "row").Skip(1).ToList();     // 跳过表头
        Assert.NotEmpty(body);

        var numeric = body.SelectMany(r => r.Elements(ns + "c"))
                          .Count(c => c.Attribute("t") is null && c.Element(ns + "v") is not null);
        Assert.True(numeric > 0, "CH1 页里一个数字单元格都没有");
        // 表头之后不该再出现共享字符串（那说明数值被当成文本写了）
        var textCells = body.SelectMany(r => r.Elements(ns + "c")).Count(c => (string?)c.Attribute("t") == "s");
        Assert.Equal(0, textCells);
    }

    [Fact]
    public void 页名过长重名与非法字符都挡得住()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.Equal("a-b-c", XlsxWriter.SheetName("a/b:c", used));
        var long31 = XlsxWriter.SheetName(new string('长', 40), used);
        Assert.Equal(31, long31.Length);
        Assert.Equal("步骤记录", XlsxWriter.SheetName("步骤记录", used));
        Assert.Equal("步骤记录 (2)", XlsxWriter.SheetName("步骤记录", used));
    }

    [Fact]
    public void 列号换算()
    {
        Assert.Equal("A", XlsxWriter.Col(1));
        Assert.Equal("Z", XlsxWriter.Col(26));
        Assert.Equal("AA", XlsxWriter.Col(27));
        Assert.Equal("AZ", XlsxWriter.Col(52));
        Assert.Equal("BA", XlsxWriter.Col(53));
    }

    [Fact]
    public void 控制字符不会把整份文件写坏()
    {
        var sheet = new XlSheet("测试");
        sheet.Add(XlCell.S("带响铃与 <&> 的备注"));
        using var ms = new MemoryStream();
        XlsxWriter.Write(ms, new[] { sheet }, "t", "t");

        using var zip = new ZipArchive(new MemoryStream(ms.ToArray()), ZipArchiveMode.Read);
        using var s = zip.GetEntry("xl/sharedStrings.xml")!.Open();
        var text = new StreamReader(s, Encoding.UTF8).ReadToEnd();
        // 得指明按序数比：默认那套是随文化的，控制字符在它眼里"可忽略"，
        // 于是任何字符串里都"找得到"一个响铃——这条断言会永远失败
        Assert.DoesNotContain("", text, StringComparison.Ordinal);
        Assert.Contains("&lt;&amp;&gt;", text);
    }
}
