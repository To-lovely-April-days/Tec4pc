using ClosedXML.Excel;
using Tec.LimitTest.Records;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 「控温极限测试记录」工作簿：9 张表 + 结果汇总矩阵。
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
    public void 建簿_9张表加说明和汇总_每张表条件列汇总都齐()
    {
        var path = TempPath();
        using var book = new RecordBook(path);
        Assert.True(File.Exists(path));
        Assert.Equal(9, book.Layouts.Count);

        using var wb = new XLWorkbook(path);
        var names = wb.Worksheets.Select(w => w.Name).ToList();
        Assert.Equal(RecordBook.ReadmeSheet, names[0]);
        Assert.Equal(RecordBook.SummarySheet, names[^1]);
        Assert.Contains("最低温 20℃", names);
        Assert.Contains("最高温 7℃", names);
        Assert.Contains("恒温 15℃", names);
        Assert.Equal(11, names.Count);

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
            book.SetCondition(TestKind.MinTemp, 15, RecordBook.CTargetA, -40);
            book.SetCondition(TestKind.MinTemp, 15, RecordBook.CTargetB, -40);
            book.SetCondition(TestKind.MinTemp, 15, RecordBook.CWater, 15.3);
            book.SetCondition(TestKind.MinTemp, 15, RecordBook.CResult, "完成");
            // 20 行：A 从 23.5 每分钟降 2 ℃到 −16.5，第 12 行起 ≤ −39.5 才算到——这里到不了，B 到得了
            for (var i = 0; i <= 20; i++)
            {
                var a = 23.5 - 2 * i;
                var b = Math.Max(-40.2, 23.5 - 6 * i);
                book.WriteRow(TestKind.MinTemp, 15, i, new Dictionary<string, XLCellValue>
                {
                    [RecordBook.KATj] = a, [RecordBook.KAOut] = 90, [RecordBook.KACur] = 4.1 + 0.01 * i,
                    [RecordBook.KBTj] = b, [RecordBook.KBOut] = 90, [RecordBook.KBCur] = 4.0,
                    [RecordBook.KASrc] = "TEC", [RecordBook.KBSrc] = "TEC"
                });
            }
            Assert.False(book.WriteRow(TestKind.MinTemp, 15, 21, new Dictionary<string, XLCellValue>()));
            book.Save();
        }

        using var wb = new XLWorkbook(path);
        using var book2 = new RecordBook(TempPath(), new BookSpec { MinRows = 21, MaxRows = 21, HoldRows = 11 });
        var lay = book2.Layout(TestKind.MinTemp, 15);   // 同规格的簿坐标一样
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

        // 结果汇总矩阵指向对的表：15 ℃ 那一列 A 最低温 = −16.5
        var sum = wb.Worksheet(RecordBook.SummarySheet);
        var row = sum.Column(1).CellsUsed().First(c => c.GetText() == "最低温 ℃").Address.RowNumber;
        var col = sum.Row(4).CellsUsed().First(c => c.GetText() == "15 ℃ · A").Address.ColumnNumber;
        Assert.Equal(-16.5, sum.Cell(row, col).GetDouble());
        Assert.Equal(-40.2, sum.Cell(row, col + 1).GetDouble(), 6);
        var resRow = sum.Column(1).CellsUsed().First(c => c.GetText() == "结果").Address.RowNumber;
        Assert.Equal("完成", sum.Cell(resRow, col).GetText());
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
    }
}
