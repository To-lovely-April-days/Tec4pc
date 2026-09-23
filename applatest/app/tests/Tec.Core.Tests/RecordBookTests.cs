using ClosedXML.Excel;
using Tec.LimitTest.Records;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 「控温极限测试记录」工作簿：每个水温 最低温 + 恒温、最高温一张（不用冷却水，只测一次）+ 说明 + 结果汇总矩阵。
/// 锁的是结构（表名、条件块、列、汇总格都找得到）、写入落在对的格子、
/// 公式引用没写歪（用 ClosedXML 自己的算式引擎算一遍，MIN / INDEX-MATCH / 跨表引用都得出数）。
/// </summary>
public class RecordBookTests
{
    private static string TempPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tec-limit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "控温极限测试记录.xlsx");
    }

    [Fact]
    public void 建簿_三水温各两张加最高温一张_说明和汇总_每张表条件列汇总都齐()
    {
        var path = TempPath();
        using var book = new RecordBook(path);
        Assert.True(File.Exists(path));
        Assert.Equal(7, book.Layouts.Count);

        using var wb = new XLWorkbook(path);
        var names = wb.Worksheets.Select(w => w.Name).ToList();
        Assert.Equal(RecordBook.ReadmeSheet, names[0]);
        Assert.Equal(RecordBook.SummarySheet, names[^1]);
        Assert.Contains("最低温 20℃", names);
        Assert.Contains("最低温 7℃", names);
        Assert.Contains("恒温 12℃", names);
        Assert.Contains("最高温（无冷却水）", names);
        Assert.DoesNotContain("最高温 20℃", names);
        Assert.DoesNotContain(names, n => n.Contains("15"));
        Assert.Equal(9, names.Count);
        Assert.False(book.HasSheet(TestKind.MaxTemp, 20));
        Assert.True(book.HasSheet(TestKind.MaxTemp, BookSpec.NoWater));

        // 最高温那张：冷却水那格是固定文字，控温对象那格留给工具填
        Assert.Equal("无（升温不用冷却水）", book.GetCondition(TestKind.MaxTemp, BookSpec.NoWater, RecordBook.CWater).GetText());
        Assert.True(book.GetCondition(TestKind.MaxTemp, BookSpec.NoWater, RecordBook.CObject).IsBlank);
        Assert.True(book.GetCondition(TestKind.MinTemp, 20, RecordBook.CWater).IsBlank);

        // 老规矩（最高温也按水温各测一遍）还能建：3 × 3 + 2
        using var old = new RecordBook(TempPath(), new BookSpec { MaxOnce = false, Waters = new[] { 20d, 15, 7 } });
        Assert.Equal(9, old.Layouts.Count);
        Assert.True(old.HasSheet(TestKind.MaxTemp, 15));

        var lo = book.Layout(TestKind.MinTemp, 20);
        Assert.Contains(RecordBook.CTargetA, lo.Cond.Keys);
        Assert.Contains(RecordBook.CLimitedB, lo.Cond.Keys);
        Assert.Contains(RecordBook.KACur, lo.Col.Keys);
        Assert.Contains("AExt", lo.Sum.Keys);
        Assert.Equal(61, lo.LastRow - lo.FirstRow + 1);
        Assert.Equal(1, lo.Step);

        var hold = book.Layout(TestKind.Hold, 7);
        Assert.Contains(RecordBook.CReachA, hold.Cond.Keys);
        Assert.Contains(RecordBook.KRelay, hold.Col.Keys);
        Assert.Contains("relays", hold.Sum.Keys);
        Assert.Equal(2, hold.Step);
        // 时间列按间隔预填
        var ws = wb.Worksheet(hold.Name);
        Assert.Equal(0, ws.Cell(hold.FirstRow, 1).GetDouble());
        Assert.Equal(4, ws.Cell(hold.FirstRow + 2, 1).GetDouble());
        // 到达方式缺省「尽快」
        Assert.Equal("尽快", book.GetCondition(TestKind.MinTemp, 20, RecordBook.CMode).GetText());
    }

    [Fact]
    public void 写条件和行_落在对的格子_公式算得出数()
    {
        var path = TempPath();
        using (var book = new RecordBook(path, new BookSpec { MinRows = 21, MaxRows = 21, HoldRows = 11 }))
        {
            book.SetCondition(TestKind.MinTemp, 12, RecordBook.CTargetA, -40);
            book.SetCondition(TestKind.MinTemp, 12, RecordBook.CTargetB, -40);
            book.SetCondition(TestKind.MinTemp, 12, RecordBook.CWater, 12.3);
            book.SetCondition(TestKind.MinTemp, 12, RecordBook.CObject, "夹套 Tj");
            book.SetCondition(TestKind.MinTemp, 12, RecordBook.CResult, "完成");
            // 20 行：A 从 23.5 每分钟降 2 ℃到 −16.5，第 12 行起 ≤ −39.5 才算到——这里到不了，B 到得了
            for (var i = 0; i <= 20; i++)
            {
                var a = 23.5 - 2 * i;
                var b = Math.Max(-40.2, 23.5 - 6 * i);
                book.WriteRow(TestKind.MinTemp, 12, i, new Dictionary<string, XLCellValue>
                {
                    [RecordBook.KATj] = a, [RecordBook.KAOut] = 90, [RecordBook.KACur] = 4.1 + 0.01 * i,
                    [RecordBook.KBTj] = b, [RecordBook.KBOut] = 90, [RecordBook.KBCur] = 4.0,
                    [RecordBook.KASrc] = "TEC", [RecordBook.KBSrc] = "TEC"
                });
            }
            Assert.False(book.WriteRow(TestKind.MinTemp, 12, 21, new Dictionary<string, XLCellValue>()));
            // 最高温那张（无冷却水）也写两行，汇总矩阵得指到它
            book.SetCondition(TestKind.MaxTemp, BookSpec.NoWater, RecordBook.CTargetA, 150);
            book.SetCondition(TestKind.MaxTemp, BookSpec.NoWater, RecordBook.CTargetB, 150);
            book.WriteRow(TestKind.MaxTemp, BookSpec.NoWater, 0, new Dictionary<string, XLCellValue> { [RecordBook.KATj] = 25, [RecordBook.KBTj] = 25 });
            book.WriteRow(TestKind.MaxTemp, BookSpec.NoWater, 1, new Dictionary<string, XLCellValue> { [RecordBook.KATj] = 151.2, [RecordBook.KBTj] = 149 });
            Assert.Throws<KeyNotFoundException>(() => book.SetCondition(TestKind.MaxTemp, 20, RecordBook.CTargetA, 150));   // 没这张表
            book.Save();
        }

        using var wb = new XLWorkbook(path);
        using var book2 = new RecordBook(TempPath(), new BookSpec { MinRows = 21, MaxRows = 21, HoldRows = 11 });
        var lay = book2.Layout(TestKind.MinTemp, 12);   // 同规格的簿坐标一样
        var ws = wb.Worksheet(lay.Name);
        Assert.Equal(-40, ws.Cell(lay.Cond[RecordBook.CTargetA].Row, lay.Cond[RecordBook.CTargetA].Col).GetDouble());
        Assert.Equal(23.5, ws.Cell(lay.FirstRow, lay.Col[RecordBook.KATj]).GetDouble());
        Assert.Equal(-16.5, ws.Cell(lay.FirstRow + 20, lay.Col[RecordBook.KATj]).GetDouble());
        Assert.Equal("完成", ws.Cell(lay.Cond[RecordBook.CResult].Row, lay.Cond[RecordBook.CResult].Col).GetText());

        // 公式：ClosedXML 自己算一遍
        wb.RecalculateAllFormulas();
        double Sum(string key) => ws.Cell(lay.Sum[key].Row, lay.Sum[key].Col).GetDouble();
        Assert.Equal(-16.5, Sum("AExt"));
        Assert.Equal(-40.2, Sum("BExt"), 6);
        Assert.Equal(23.5, Sum("ADiff"), 6);          // −16.5 − (−40)
        Assert.Equal(2.0, Sum("ARate10"), 6);         // 前 10 min 每分钟 2 ℃
        Assert.Equal(4.3, Sum("ACurMax"), 6);
        Assert.Equal("未到", ws.Cell(lay.Sum["AReach"].Row, lay.Sum["AReach"].Col).GetText());
        Assert.Equal(11, Sum("BReach"));              // B 在第 11 分钟到 −40.2

        // 结果汇总矩阵指向对的表：12 ℃ 那一列 A 最低温 = −16.5
        var sum = wb.Worksheet(RecordBook.SummarySheet);
        var row = sum.Column(1).CellsUsed().First(c => c.GetText() == "最低温 ℃").Address.RowNumber;
        var col = sum.Row(4).CellsUsed().First(c => c.GetText() == "12 ℃ · A").Address.ColumnNumber;
        Assert.Equal(-16.5, sum.Cell(row, col).GetDouble());
        Assert.Equal(-40.2, sum.Cell(row, col + 1).GetDouble(), 6);
        var resRow = sum.Column(1).CellsUsed().First(c => c.GetText() == "结果").Address.RowNumber;
        Assert.Equal("完成", sum.Cell(resRow, col).GetText());
        var objRow = sum.Column(1).CellsUsed().First(c => c.GetText() == "控温对象").Address.RowNumber;
        Assert.Equal("夹套 Tj", sum.Cell(objRow, col).GetText());
        // 最高温那块：列头「无冷却水 · A/B」在项目标题那一行，占前两列；下面的最高温 / 过冲拉自那张表
        var hiTitle = sum.Column(1).CellsUsed().First(c => c.GetText() == "最高温").Address.RowNumber;
        Assert.Equal("无冷却水 · A", sum.Cell(hiTitle, 2).GetText());
        Assert.Equal("无冷却水 · B", sum.Cell(hiTitle, 3).GetText());
        Assert.Equal(151.2, sum.Cell(hiTitle + 1, 2).GetDouble(), 6);
        Assert.Equal(149, sum.Cell(hiTitle + 1, 3).GetDouble(), 6);
        var overRow = sum.Column(1).CellsUsed().First(c => c.GetText() == "过冲 ℃").Address.RowNumber;
        Assert.Equal(1.2, sum.Cell(overRow, 2).GetDouble(), 6);
        Assert.Equal(0, sum.Cell(overRow, 3).GetDouble(), 6);
    }

    [Fact]
    public void 恒温表_继电器与波动公式()
    {
        var path = TempPath();
        using (var book = new RecordBook(path, new BookSpec { MinRows = 5, MaxRows = 5, HoldRows = 6 }))
        {
            book.SetCondition(TestKind.Hold, 20, RecordBook.CTarget, 25);
            var tj = new[] { 25.1, 24.9, 25.2, 25.0, 24.8, 25.0 };
            for (var i = 0; i < 6; i++)
                book.WriteRow(TestKind.Hold, 20, i, new Dictionary<string, XLCellValue>
                {
                    [RecordBook.KATj] = tj[i], [RecordBook.KATr] = 25.05, [RecordBook.KAOut] = 12,
                    [RecordBook.KBTj] = 25.0, [RecordBook.KBTr] = Blank.Value, [RecordBook.KBOut] = 10,
                    [RecordBook.KRelay] = i == 3 ? 2 : 0
                });
            // 每秒数据算的稳定度：程序填值格（Excel 里没有每秒数据，没有公式）
            book.SetSummary(TestKind.Hold, 20, "stabWindow", 30);
            book.SetSummary(TestKind.Hold, 20, "ATjPp1s", 0.012);
            book.SetSummary(TestKind.Hold, 20, "ATjSd1s", 0.0031);
            book.SetSummary(TestKind.Hold, 20, "AErr1s", -0.004);
            book.SetSummary(TestKind.Hold, 20, "BTrPp1s", Blank.Value);      // B 没接探头：留空
            Assert.Throws<ArgumentException>(() => book.SetSummary(TestKind.Hold, 20, "没有这项", 1));
            Assert.Equal(0.012, book.GetSummary(TestKind.Hold, 20, "ATjPp1s").GetNumber(), 6);
            book.Save();
        }
        using var wb = new XLWorkbook(path);
        using var probe = new RecordBook(TempPath(), new BookSpec { MinRows = 5, MaxRows = 5, HoldRows = 6 });
        var lay = probe.Layout(TestKind.Hold, 20);
        var ws = wb.Worksheet(lay.Name);
        wb.RecalculateAllFormulas();
        double Sum(string key) => ws.Cell(lay.Sum[key].Row, lay.Sum[key].Col).GetDouble();
        Assert.Equal(0.4, Sum("ATjPp"), 6);
        Assert.Equal(0.2, Sum("ATjPm"), 6);
        Assert.Equal(0.05, Sum("ATrDiff"), 6);
        Assert.Equal(2, Sum("relays"));
        // B 的 Tr 一列全空：不编数，公式给空串
        Assert.Equal("", ws.Cell(lay.Sum["BTrPp"].Row, lay.Sum["BTrPp"].Col).GetText());
        // 程序填的值格原样在，没被公式盖掉；汇总矩阵把它拉过去
        Assert.False(ws.Cell(lay.Sum["ATjPp1s"].Row, lay.Sum["ATjPp1s"].Col).HasFormula);
        Assert.Equal(0.012, Sum("ATjPp1s"), 6);
        Assert.Equal(30, Sum("stabWindow"));
        Assert.Equal(-0.004, Sum("AErr1s"), 6);
        Assert.True(ws.Cell(lay.Sum["BTrPp1s"].Row, lay.Sum["BTrPp1s"].Col).Value.IsBlank);
        var sum = wb.Worksheet(RecordBook.SummarySheet);
        var ppRow = sum.Column(1).CellsUsed().First(c => c.GetText().StartsWith("Tj 峰峰值 ℃（1 s")).Address.RowNumber;
        var col = sum.Row(4).CellsUsed().First(c => c.GetText() == "20 ℃ · A").Address.ColumnNumber;
        Assert.Equal(0.012, sum.Cell(ppRow, col).GetDouble(), 6);
    }
}
