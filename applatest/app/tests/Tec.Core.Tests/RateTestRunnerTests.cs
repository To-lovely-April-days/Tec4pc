using ClosedXML.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using Tec.Driver.Abi;
using Tec.LimitTest.Rate;
using Tec.LimitTest.Runs;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 【本地改动】（2026-11-17，用户要的升温 / 降温速率测试）满功率升 / 降温速率测试：
/// 假机器（满功率时夹套每秒升 / 降固定的度数，停了以后余热按 10 s 时间常数衰减，所以归零后还会冲一点）+ 虚拟时钟。锁的是：
/// · 从当前温度直接开始，给夹套一个够不着的目标（升温 = 停止线 / 超温上限里小的，降温 = 超温下限），控夹套；
/// · 每一路从「接上」那一刻起算 0 s，满 60 s 归零，再记 60 s；一分钟升 / 降够 20 ℃ 合格，不够不合格；
/// · 升够合格线用时、最快 10 s、输出开满占比、停后还冲多少都按每秒记录算对；
/// · 降温前停下来等冷却水、水温进报告；等水时跳过 = 降温都不做；
/// · 升温到停止线这一路马上归零；读数停更 / 安全层 / 驱动拒绝整轮停、两路归零；
/// · 降温时夹套高过 TEC 接入上限那一路不做、原因进报告；
/// · 下令还没返回就中止：先撤回下令再归零，不让它在归零之后又开起来；
/// · Excel 报告：汇总一行一路、每项一张每秒数据、每张一张曲线，图表部件过 Open XML 校验。
/// </summary>
public class RateTestRunnerTests
{
    private sealed class Clock
    {
        public DateTimeOffset Now = new(2026, 11, 17, 14, 0, 0, TimeSpan.FromHours(8));
        /// <summary>每拍真等这么多毫秒（要在跑的中途插一脚的用例用；0 = 不等，几分钟跑成几毫秒）。</summary>
        public int RealMs;
        public async Task Delay(TimeSpan t, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Now += t;
            if (RealMs > 0) await Task.Delay(RealMs, ct);
        }
    }

    /// <summary>
    /// 假机器：开着时夹套按「满功率速率」走（升温 HeatRate、降温 CoolRate，K/s），停了以后那份速率按 10 s 时间常数衰减（余热）。
    /// 输出 = ±90（顶在最大输出占空比上），升温热源「电加热」、降温「TEC」。
    /// </summary>
    private sealed class FakeRig : IRig
    {
        private readonly Clock _clk;
        private DateTimeOffset _last;
        public readonly double[] Tj = { 25, 25 };
        public readonly double[] HeatRate = { 0.40, 0.30 };   // A 一分钟 +24，B +18
        public readonly double[] CoolRate = { 0.35, 0.10 };   // A 一分钟 −21，B −6
        private readonly double[] _v = new double[2];         // 此刻的变温速度 K/s（停了以后衰减）
        public readonly double?[] Target = new double?[2];
        public double? Limited = 90;
        public double? ThresholdValue = 90;
        public bool Alive = true;
        /// <summary>过了这一刻 Tj 的采样时刻和值都不再更新（温控器不回数）。</summary>
        public DateTimeOffset? FreezeAt;
        /// <summary>下发目标报错（驱动拒绝）。</summary>
        public string? Refuse;
        /// <summary>这一路的下发一直不返回（等冷却水 / 继电器卡住），直到被撤回。</summary>
        public int? HangWell;
        /// <summary>下发要用掉多少秒（切热源、等通水）。</summary>
        public double ConnectSeconds;
        public readonly List<(int Well, double Target, TempChannelKind Kind)> Targets = new();
        public readonly List<(int Well, DateTimeOffset At)> Stops = new();
        public int SafeStops;
        private readonly double[] _frozenTj = new double[2];
        private DateTimeOffset _frozenAt;

        public FakeRig(Clock clk) { _clk = clk; _last = clk.Now; }

        private void Step()
        {
            var dt = (_clk.Now - _last).TotalSeconds;
            if (dt <= 0) return;
            // 0.1 s 一小步积分，快到够用
            var steps = Math.Max(1, (int)Math.Ceiling(dt / 0.1));
            var h = dt / steps;
            for (var i = 0; i < steps; i++)
                for (var w = 0; w < 2; w++)
                {
                    if (Target[w] is { } t)
                        _v[w] = t > Tj[w] ? HeatRate[w] : -CoolRate[w];
                    else
                        _v[w] *= Math.Exp(-h / 10.0);
                    Tj[w] += _v[w] * h;
                }
            _last = _clk.Now;
        }

        public string Describe => "假机器 · CH1 / CH2";
        public int WellCount => 2;
        public event Action<string>? Tripped;
        public void Trip(string why) => Tripped?.Invoke(why);

        public WellReading Read(int well)
        {
            Step();
            if (FreezeAt is { } fz && _clk.Now >= fz)
            {
                if (_frozenAt == default) { _frozenAt = _clk.Now; _frozenTj[0] = Tj[0]; _frozenTj[1] = Tj[1]; }
                return new WellReading(Math.Round(_frozenTj[well], 2), null, 90, 3.0, "电加热", null) { TjAt = _frozenAt };
            }
            var on = Target[well] is not null;
            var heating = on && Target[well] > Tj[well];
            return new WellReading(Math.Round(Tj[well], 2), Math.Round(Tj[well] - 3, 2),
                                   on ? (heating ? -90 : 90) : 0,     // 现场那台：升温 −90、降温 +90
                                   on && !heating ? 4.5 : 0.1,
                                   on ? (heating ? "电加热" : "TEC") : "TEC", on && !heating)
            { TjAt = _clk.Now };
        }

        public bool IsAlive(out string why) { why = Alive ? "" : "串口没打开"; return Alive; }
        public TempLimits? Limits(int well) => new(-40, 200, 5);
        public double? Threshold => ThresholdValue;
        public double? Band => 2;

        public async Task SetTargetAsync(int well, double target, TempChannelKind kind, CancellationToken ct)
        {
            Step();
            if (Refuse is { } r) throw new InvalidOperationException(r);
            if (HangWell == well) await Task.Delay(Timeout.Infinite, ct);
            if (ConnectSeconds > 0) { _clk.Now += TimeSpan.FromSeconds(ConnectSeconds); Step(); }
            Target[well] = target;
            Targets.Add((well, target, kind));
        }

        public Task StopAsync(int well, CancellationToken ct)
        {
            Step();
            Target[well] = null;
            Stops.Add((well, _clk.Now));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> SafeStopAsync(CancellationToken ct)
        {
            Target[0] = Target[1] = null;
            SafeStops++;
            return Task.FromResult<IReadOnlyList<string>>(new[] { "R1 已关两路输出" });
        }

        public Task<double?> ReadLimitedAsync(int well, CancellationToken ct) => Task.FromResult(Limited);
        public Task<double?> ReadOverLimitAsync(int well, CancellationToken ct) => Task.FromResult<double?>(200);
    }

    private static string OutDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tec-rate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static (RateTestRunner Runner, FakeRig Rig, Clock Clk, List<string> Logs, string Dir) Make(RateSettings? s = null, Action<FakeRig>? setup = null)
    {
        var clk = new Clock();
        var rig = new FakeRig(clk);
        setup?.Invoke(rig);
        var logs = new List<string>();
        var dir = OutDir();
        var runner = new RateTestRunner(rig, s ?? new RateSettings { Operator = "测试员", Patch = "0340", Material = "水 50 mL" }, dir,
                                        () => clk.Now, clk.Delay, (l, t) => { lock (logs) logs.Add($"{l} {t}"); });
        return (runner, rig, clk, logs, dir);
    }

    private static async Task WaitFor(Func<bool> cond, Task run, int ms = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond())
        {
            if (run.IsCompleted) { await run; Assert.Fail("跑完了也没等到"); }
            Assert.True(DateTime.UtcNow < deadline, "等太久");
            await Task.Delay(5);
        }
    }

    /// <summary>后台跑，等到降温前的「等冷却水」就填水温（NaN = 不等水，直接跑完）。</summary>
    private static async Task RunFeeding(RateTestRunner r, double water = 18.5, CancellationToken ct = default)
    {
        var run = Task.Run(() => r.RunAsync(ct));
        if (!double.IsNaN(water) && r.Settings.Cool)
        {
            await WaitFor(() => r.State == RateRunnerState.WaitingWater || r.State == RateRunnerState.Finished, run);
            if (r.State == RateRunnerState.WaitingWater)
            {
                Assert.Contains("冷却水", r.Prompt);
                Assert.True(r.ConfirmWater(water));
            }
        }
        await run.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static RateMetrics M(RateTestRunner r, RateKind kind, int well, int round = 1)
    {
        var t = r.Tests.First(x => x.Kind == kind && x.Round == round);
        return RateMath.Compute(t.Traces[well]!, r.Settings, t.State is RateState.Aborted or RateState.Skipped ? t.Note : null);
    }

    [Fact]
    public async Task 一轮升温降温_两路一起_按一分钟20度判_数都算对()
    {
        var (r, rig, _, logs, _) = Make();
        await RunFeeding(r);

        Assert.Equal("全部做完", r.Status);
        Assert.All(r.Tests, t => Assert.Equal(RateState.Done, t.State));
        Assert.Equal(18.5, r.Water);
        // 够不着的目标：升温 = 停止线 190（比超温上限 200 小），降温 = 超温下限 −40；控夹套
        Assert.Contains(rig.Targets, x => x.Target == 190 && x.Kind == TempChannelKind.Jacket);
        Assert.Contains(rig.Targets, x => x.Target == -40 && x.Kind == TempChannelKind.Jacket);
        Assert.Equal(4, rig.Targets.Count);

        // 升温：A 0.4 K/s → 一分钟 +24 合格，50 s 升够 20；B 0.3 K/s → +18 不合格
        var ha = M(r, RateKind.Heat, 0);
        Assert.Equal("合格", ha.Verdict);
        Assert.Equal(25, ha.Start!.Value, 1);
        Assert.Equal(24, ha.Change!.Value, 0);
        Assert.Equal(50, ha.TimeToPass!.Value, 0);
        Assert.Equal(24, ha.Fastest10s!.Value, 0);      // 匀速：最快 10 s 折合也是 24 ℃/min
        Assert.Equal(100, ha.FullOutPercent!.Value, 0);
        Assert.Equal(90, ha.MeanOut!.Value, 0);
        Assert.Equal("电加热", ha.Source);
        Assert.Equal(60, ha.RanSeconds!.Value, 0);
        // 归零后余热：0.4 K/s × 10 s 时间常数 ≈ 还冲 4 ℃（一分钟内衰减掉 99.7 %）
        Assert.InRange(ha.Overrun!.Value, 3.5, 4.1);
        Assert.Equal(3, ha.MaxJacketReactorGap!.Value, 1);
        var hb = M(r, RateKind.Heat, 1);
        Assert.Equal("不合格", hb.Verdict);
        Assert.Equal(18, hb.Change!.Value, 0);
        Assert.Null(hb.TimeToPass);

        // 降温从升温结束后的温度直接开始：A 0.35 K/s → −21 合格；B 0.1 K/s → −6 不合格；「降了」是正数
        var ca = M(r, RateKind.Cool, 0);
        Assert.Equal("合格", ca.Verdict);
        Assert.InRange(ca.Start!.Value, 52, 54);         // 25 + 24 + 余热 4
        Assert.Equal(21, ca.Change!.Value, 0);
        Assert.Equal("TEC", ca.Source);
        Assert.Equal(4.5, ca.MeanCur!.Value, 1);
        var cb = M(r, RateKind.Cool, 1);
        Assert.Equal("不合格", cb.Verdict);
        Assert.Equal(6, cb.Change!.Value, 0);

        // 每一路满 60 s 归零（拍子在归零时刻上提前醒，差不到 0.1 s），最后两路都停
        var t0 = r.Tests[0].Traces[0]!.T0!.Value;
        Assert.Equal(60, (r.Tests[0].Traces[0]!.StopAt!.Value - t0).TotalSeconds, 1);
        Assert.Null(rig.Target[0]);
        Assert.Null(rig.Target[1]);
        Assert.Contains(logs, l => l.Contains("升温 第 1 次 做完") && l.Contains("合格"));
    }

    [Fact]
    public async Task 报告_汇总一行一路_每项一张每秒数据_曲线过校验()
    {
        var (r, _, _, _, dir) = Make();
        await RunFeeding(r);
        var path = r.ReportPath!;
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(Path.ChangeExtension(path, ".tmp.xlsx")));
        Assert.Single(Directory.GetFiles(Path.Combine(dir, "csv"), "*_heat1.csv"));
        Assert.Single(Directory.GetFiles(Path.Combine(dir, "csv"), "*_cool1.csv"));

        using (var wb = new XLWorkbook(path))
        {
            var sum = wb.Worksheet(RateReport.SummarySheet);
            var hdr = sum.CellsUsed(c => c.GetString() == "结论").Single();
            int Col(string name) => RateReport.SummaryColumns.ToList().IndexOf(name) + 1;
            var rows = Enumerable.Range(hdr.Address.RowNumber + 1, 4).Select(sum.Row).ToList();
            Assert.Equal("升温 第 1 次", rows[0].Cell(Col("测试")).GetString());
            Assert.Equal("A", rows[0].Cell(Col("工位")).GetString());
            Assert.Equal("合格", rows[0].Cell(Col("结论")).GetString());
            Assert.Equal("不合格", rows[1].Cell(Col("结论")).GetString());
            Assert.Equal("降温 第 1 次", rows[2].Cell(Col("测试")).GetString());
            Assert.Equal("合格", rows[2].Cell(Col("结论")).GetString());
            Assert.Equal(24, rows[0].Cell(Col("升 / 降了 ℃")).GetDouble(), 0);
            Assert.Equal(20, rows[0].Cell(Col("合格线 ℃")).GetDouble());
            Assert.Equal(XLColor.FromHtml("#C8E6C9"), rows[0].Cell(Col("结论")).Style.Fill.BackgroundColor);
            Assert.Equal(XLColor.FromHtml("#FFCDD2"), rows[1].Cell(Col("结论")).Style.Fill.BackgroundColor);
            // 条件块：手填的水温、物料、操作人，从温控器读的最大输出占空比
            Assert.Contains(sum.CellsUsed(), c => c.GetString() == "水 50 mL");
            Assert.Contains(sum.CellsUsed(), c => c.GetString() == "测试员");
            Assert.Contains(sum.CellsUsed(), c => c.Value.IsNumber && c.GetDouble() == 18.5);

            var heat = wb.Worksheet("升温 第1次");
            var first = RateReport.SecondHeaderRow + 1;
            Assert.Equal(0, heat.Cell(first, 1).GetDouble());
            Assert.Equal(120, heat.Cell(first + 120, 1).GetDouble());
            Assert.Equal("A 夹套 ℃", heat.Cell(RateReport.SecondHeaderRow, 3).GetString());
            Assert.Equal("满功率", heat.Cell(first + 30, 2).GetString());
            Assert.Equal("归零后", heat.Cell(first + 90, 2).GetString());
            Assert.Equal(25, heat.Cell(first, 3).GetDouble(), 1);
            Assert.Equal(24, heat.Cell(first + 60, 4).GetDouble(), 0);       // A 夹套变化 60 s
            Assert.Equal(-90, heat.Cell(first + 30, 6).GetDouble());          // 输出原样（现场升温是负的）
            Assert.True(wb.Worksheets.Contains("降温 第1次"));
        }

        // 图：每张每秒数据表一张，系列引用表里的格子；图表 / 绘图部件过 Open XML 校验（Excel 对这块最挑）
        using var doc = SpreadsheetDocument.Open(path, false);
        var wbPart = doc.WorkbookPart!;
        foreach (var name in new[] { "升温 第1次", "降温 第1次" })
        {
            var sheet = wbPart.Workbook.Sheets!.Elements<DocumentFormat.OpenXml.Spreadsheet.Sheet>().Single(s => s.Name == name);
            var ws = (WorksheetPart)wbPart.GetPartById(sheet.Id!);
            var chart = Assert.Single(ws.DrawingsPart!.ChartParts);
            var xml = chart.ChartSpace.OuterXml;
            Assert.Contains($"'{name}'!$C$5:$C$125", xml);
            Assert.Contains("A 夹套", xml);
            Assert.Contains("B 釜内", xml);
        }
        var errors = new OpenXmlValidator(FileFormatVersions.Office2016).Validate(doc)
            .Where(e => e.Part?.Uri.ToString() is { } u && (u.Contains("/charts/") || u.Contains("/drawings/"))
                        || e.Node is DocumentFormat.OpenXml.Spreadsheet.Drawing)
            .Select(e => $"{e.Part?.Uri} {e.Path?.XPath} {e.Description}").ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }

    [Fact]
    public async Task 升温到停止线_这一路马上归零_另一路照做满一分钟()
    {
        var (r, rig, _, logs, _) = Make(new RateSettings { Cool = false, StopLine = 40, Operator = "测试员" }, f => f.Tj[0] = 32);
        await RunFeeding(r, double.NaN);

        var t = r.Tests.Single();
        Assert.Equal(RateState.Done, t.State);
        Assert.Contains(rig.Targets, x => x.Target == 40);     // 够不着的目标也取停止线
        var a = t.Traces[0]!;
        // 32 → 40 ℃ 用 20 s：到线那一拍就归零
        Assert.Equal(20, (a.StopAt!.Value - a.T0!.Value).TotalSeconds, 0);
        Assert.Contains("停止线", a.EarlyStop);
        var ma = RateMath.Compute(a, r.Settings);
        Assert.Equal("判不了", ma.Verdict);
        Assert.Contains("满功率只开了 20 s", ma.Note);
        // B 从 25 起（0.3 K/s），50 s 到 40，同样归零
        var mb = M(r, RateKind.Heat, 1);
        Assert.Contains("停止线", t.Traces[1]!.EarlyStop);
        Assert.Equal("判不了", mb.Verdict);
        Assert.Contains(logs, l => l.Contains("马上归零"));
        Assert.All(rig.Target, x => Assert.Null(x));
    }

    [Fact]
    public async Task 停止线之前已经升够合格线_照样算合格()
    {
        var (r, _, _, _, _) = Make(new RateSettings { Cool = false, StopLine = 60, Operator = "测试员" }, f => f.Tj[0] = 35);
        await RunFeeding(r, double.NaN);
        var m = M(r, RateKind.Heat, 0);
        // 35 → 55 用 50 s 升够 20，60 ℃ 在 62.5 s（满功率 60 s 已经到时归零了）
        Assert.Equal("合格", m.Verdict);
        Assert.Equal(50, m.TimeToPass!.Value, 0);
    }

    [Fact]
    public async Task 夹套读数停更_整轮停_两路归零_报告照写()
    {
        var (r, rig, clk, logs, _) = Make(setup: f => { });
        rig.FreezeAt = clk.Now + TimeSpan.FromSeconds(20);
        await RunFeeding(r);

        var heat = r.Tests[0];
        Assert.Equal(RateState.Aborted, heat.State);
        Assert.Contains("没更新", heat.Note);
        Assert.StartsWith("已停止", r.Status);
        Assert.Equal(RateState.Skipped, r.Tests[1].State);     // 降温没跑到
        Assert.Null(rig.Target[0]);
        Assert.Null(rig.Target[1]);
        // 停更 5 s 以上才判（满功率下不等管线 15 s）
        var stopAt = rig.Stops.Min(s => s.At);
        Assert.InRange((stopAt - heat.Traces[0]!.T0!.Value).TotalSeconds, 25, 27);
        var m = M(r, RateKind.Heat, 0);
        Assert.Equal("判不了", m.Verdict);
        Assert.True(File.Exists(r.ReportPath));
    }

    [Fact]
    public async Task 降温_夹套高过TEC接入上限的那一路不做_原因进报告_另一路照做()
    {
        var (r, rig, _, logs, _) = Make(new RateSettings { Heat = false, Operator = "测试员" }, f => f.Tj[0] = 95);
        await RunFeeding(r);

        var t = r.Tests.Single();
        Assert.Equal(RateState.Done, t.State);
        Assert.Contains("TEC 接入上限", t.Traces[0]!.Refused);
        Assert.DoesNotContain(rig.Targets, x => x.Well == 0);
        Assert.Contains(rig.Targets, x => x.Well == 1 && x.Target == -40);
        var ma = M(r, RateKind.Cool, 0);
        Assert.Equal("判不了", ma.Verdict);
        Assert.Contains("没测", ma.Note);
        using var wb = new XLWorkbook(r.ReportPath!);
        Assert.Contains(wb.Worksheet(RateReport.SummarySheet).CellsUsed(), c => c.GetString().Contains("TEC 接入上限"));
    }

    [Fact]
    public async Task 等冷却水时跳过_降温都不做_升温照做()
    {
        var (r, rig, _, _, _) = Make();
        var run = Task.Run(() => r.RunAsync(CancellationToken.None));
        await WaitFor(() => r.State == RateRunnerState.WaitingWater, run);
        Assert.Equal(RateState.Done, r.Tests[0].State);       // 升温不用水，先做完了才问
        r.SkipCurrent();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(RateState.Skipped, r.Tests[1].State);
        Assert.Contains("降温都不做", r.Tests[1].Note);
        Assert.Equal("全部做完", r.Status);
        Assert.DoesNotContain(rig.Targets, x => x.Target == -40);
    }

    [Fact]
    public async Task 驱动拒绝下发_整轮停_原话进报告()
    {
        var (r, rig, _, _, _) = Make(setup: f => f.Refuse = "IO8R 没开：加热棒接不上");
        await RunFeeding(r);
        Assert.Equal(RateState.Aborted, r.Tests[0].State);
        Assert.Contains("IO8R 没开", r.Tests[0].Note);
        Assert.Contains("IO8R 没开", r.Status);
        Assert.Equal(RateState.Skipped, r.Tests[1].State);
        using var wb = new XLWorkbook(r.ReportPath!);
        Assert.Contains(wb.Worksheet(RateReport.SummarySheet).CellsUsed(), c => c.GetString().Contains("IO8R 没开"));
    }

    [Fact]
    public async Task 安全层触发_整轮停_两路归零()
    {
        var (r, rig, clk, _, _) = Make();
        clk.RealMs = 3;     // 一项要几百毫秒，测试线程插得进去
        var run = Task.Run(() => r.RunAsync(CancellationToken.None));
        await WaitFor(() => r.Tests[0].Traces[0]?.T0 is not null, run);
        rig.Trip("CH1 Tr 越限");
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(RateState.Aborted, r.Tests[0].State);
        Assert.Contains("安全层触发", r.Tests[0].Note);
        Assert.Null(rig.Target[0]);
        Assert.Null(rig.Target[1]);
    }

    [Fact]
    public async Task 一路下令一直不返回_到时中止_先撤回下令再归零_不会再开起来()
    {
        var (r, rig, _, _, _) = Make(new RateSettings { Cool = false, ConnectMaxSeconds = 30, Operator = "测试员" }, f => f.HangWell = 1);
        await RunFeeding(r, double.NaN);
        var t = r.Tests.Single();
        Assert.Equal(RateState.Aborted, t.State);
        Assert.Contains("还没接上", t.Note);
        Assert.Null(t.Traces[1]!.T0);
        Assert.DoesNotContain(rig.Targets, x => x.Well == 1);   // 撤回了：B 一次都没开起来
        Assert.Null(rig.Target[0]);
        Assert.Null(rig.Target[1]);
        Assert.Equal("判不了", M(r, RateKind.Heat, 1).Verdict);
    }

    [Fact]
    public async Task 接上要几秒_零秒从接上算_下令到接上用时进报告()
    {
        var (r, _, _, _, _) = Make(new RateSettings { Cool = false, WellB = false, Operator = "测试员" }, f => f.ConnectSeconds = 3);
        await RunFeeding(r, double.NaN);
        var m = M(r, RateKind.Heat, 0);
        Assert.Equal(3, m.ConnectSeconds!.Value, 1);
        Assert.Equal(24, m.Change!.Value, 0);                  // 接上之后的 60 s，不吃掉那 3 s
        Assert.Null(r.Tests[0].Traces[1]);                     // B 没勾
    }

    [Fact]
    public async Task 最大输出占空比读不到_开满占比留空_备注说清()
    {
        var (r, _, _, _, _) = Make(new RateSettings { Cool = false, Operator = "测试员" }, f => f.Limited = null);
        await RunFeeding(r, double.NaN);
        var m = M(r, RateKind.Heat, 0);
        Assert.Null(m.FullOutPercent);
        Assert.Contains("没读到最大输出占空比", m.Note);
        Assert.Equal("合格", m.Verdict);
    }

    [Fact]
    public async Task 输出没顶满_备注说这一项不算满功率()
    {
        var (r, _, _, _, _) = Make(new RateSettings { Cool = false, Operator = "测试员" }, f => f.Limited = 100);
        await RunFeeding(r, double.NaN);
        var m = M(r, RateKind.Heat, 0);
        Assert.Equal(0, m.FullOutPercent!.Value);
        Assert.Contains("不算满功率", m.Note);
    }

    [Fact]
    public async Task 测两次_每次升温再降温_都从当时温度开始()
    {
        var (r, _, _, _, _) = Make(new RateSettings { Rounds = 2, Operator = "测试员" });
        await RunFeeding(r);
        Assert.Equal(new[] { "升温 第 1 次", "降温 第 1 次", "升温 第 2 次", "降温 第 2 次" }, r.Tests.Select(t => t.Title));
        Assert.All(r.Tests, t => Assert.Equal(RateState.Done, t.State));
        Assert.True(M(r, RateKind.Heat, 0, 2).Start > M(r, RateKind.Heat, 0, 1).Start);
        using var wb = new XLWorkbook(r.ReportPath!);
        Assert.True(wb.Worksheets.Contains("降温 第2次"));
    }

    /// <summary>
    /// 架在仿真主机上（真机那条路上除了串口都走一遍：HmiRuntime → 管线 → RuntimeRig → 通道的控温能力），升温降温各一次、出报告。
    /// 仿真的「尽快」是按 2 ℃/min 走的（仿真模型自己的数，不是真机），所以两项都是「不合格」——这里锁的是路通、数有、图在。
    /// 设了环境变量 TEC_RATE_SAMPLE_OUT 就把这份报告拷过去（给人看的样例报告就是这么出的）。
    /// </summary>
    [Fact]
    public async Task 仿真主机_升温降温各一次_报告有数有图()
    {
        var benchDir = Path.Combine(Path.GetTempPath(), "tec-rate-sim-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(benchDir);
        Tec.Core.Persistence.TecFiles.SaveBench(Path.Combine(benchDir, "bench.json"), new Tec.Core.Persistence.BenchDoc
        {
            Name = "仿真",
            Devices = { new Tec.Core.Persistence.DeviceDoc { DriverId = Tec.Drivers.Simulator.Rd105ReactorDriver.DriverId, InstanceId = "R1" } }
        });
        await using var rt = new Tec.Hmi.Runtime.HmiRuntime(benchDir);
        rt.Drivers.RegisterBuiltin(new Tec.Drivers.Simulator.Rd105ReactorDriver());
        // 100 倍：1 虚拟秒 = 10 ms。整个测试集并行跑时采样会被挤晚，过期窗、停更判断都放宽（同 RuntimeRigTests）
        rt.Boot(timeScale: 100);
        rt.Pipeline.StaleAfter = TimeSpan.FromMinutes(1);
        await rt.StartAsync();
        using var rig = new RuntimeRig(rt);
        var boot = Task.Delay(TimeSpan.FromSeconds(10));
        while (rig.Read(0).TjAt is null || rig.Read(1).TjAt is null || rig.Read(0).Source is null)
        {
            Assert.False(boot.IsCompleted, "仿真主机 10 s 还没出数");
            await Task.Delay(10);
        }

        var outDir = OutDir();
        var s = new RateSettings { Operator = "仿真", Patch = "0340", Material = "（仿真，无料）", Ambient = 24, ReadingLossSeconds = 120 };
        var runner = new RateTestRunner(rig, s, outDir,
            now: rt.Clock.Func,
            delay: (t, ct) => Task.Delay(rt.Clock.RealDelay(t), ct),
            log: (l, t) => rt.Log.Write("测试", t, "测试", l == "error" ? Tec.Core.Records.LogLevel.Error : Tec.Core.Records.LogLevel.Info));
        var run = Task.Run(() => runner.RunAsync(CancellationToken.None));
        await WaitFor(() => runner.State == RateRunnerState.WaitingWater, run, 60000);
        runner.ConfirmWater(18.0);
        await run.WaitAsync(TimeSpan.FromSeconds(90));

        Assert.True(runner.Tests.All(t => t.State == RateState.Done), string.Join("；", runner.Tests.Select(t => $"{t.Title} {t.StateText} {t.Note}")));
        foreach (var t in runner.Tests)
            for (var w = 0; w < 2; w++)
            {
                var m = RateMath.Compute(t.Traces[w]!, s);
                Assert.NotNull(m.Start);
                Assert.True(m.Change > 0, $"{t.Title} {w} 没往要的方向走：{m.Change}");
                Assert.Equal("不合格", m.Verdict);      // 仿真 2 ℃/min
                Assert.NotNull(m.Source);
                Assert.Null(m.FullOutPercent);           // 仿真没有参数面板：最大输出占空比读不到，留空
                Assert.InRange(m.RanSeconds!.Value, 59.5, 61);
            }
        Assert.Contains(rt.Log.Tail(500), e => e.Text.Contains("接上满功率升温"));

        using var doc = SpreadsheetDocument.Open(runner.ReportPath!, false);
        var errors = new OpenXmlValidator(FileFormatVersions.Office2016).Validate(doc)
            .Where(e => e.Part?.Uri.ToString() is { } u && (u.Contains("/charts/") || u.Contains("/drawings/")))
            .Select(e => $"{e.Part?.Uri} {e.Description}").ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors));
        Assert.Equal(2, doc.WorkbookPart!.WorksheetParts.Count(p => p.DrawingsPart is not null));
        doc.Dispose();

        if (Environment.GetEnvironmentVariable("TEC_RATE_SAMPLE_OUT") is { Length: > 0 } dst)
            File.Copy(runner.ReportPath!, dst, overwrite: true);
    }

    [Fact]
    public void 插值_越线时刻_最快十秒_按采样时刻算()
    {
        var t0 = new DateTimeOffset(2026, 11, 17, 14, 0, 0, TimeSpan.Zero);
        var tr = new RateTrace(0, RateKind.Heat) { CommandAt = t0, T0 = t0, StopAt = t0.AddSeconds(60), Limited = 90 };
        // 采样 2 s 一个、Runner 每秒读一次（同一个采样读两次只算一次）：前 30 s 每秒 +0.2，后 30 s 每秒 +0.5
        for (var k = 0; k <= 120; k++)
        {
            var at = t0.AddSeconds(k);
            var sampleT = k - k % 2;
            var v = 25 + (sampleT <= 30 ? sampleT * 0.2 : 6 + (Math.Min(sampleT, 60) - 30) * 0.5);
            tr.Points.Add(new RatePoint(at, new WellReading(v, null, k <= 60 ? -90 : 0, null, "电加热", null) { TjAt = t0.AddSeconds(sampleT) }));
        }
        var s = new RateSettings();
        var series = RateMath.TjSeries(tr);
        Assert.Equal(61, series.Count);
        Assert.Equal(25.2, RateMath.At(series, t0.AddSeconds(1))!.Value, 6);      // 两个采样之间插值
        var m = RateMath.Compute(tr, s);
        Assert.Equal(21, m.Change!.Value, 6);                                      // 6 + 15
        Assert.Equal("合格", m.Verdict);
        Assert.Equal(58, m.TimeToPass!.Value, 6);                                  // 6 + (t − 30) × 0.5 = 20 → 58 s
        Assert.Equal(30, m.Fastest10s!.Value, 6);                                  // 0.5 K/s × 60
        Assert.Equal(100, m.FullOutPercent!.Value, 6);
        Assert.Equal(0, m.Overrun!.Value, 6);                                      // 停了以后没再升
        Assert.Null(RateMath.At(series, t0.AddSeconds(200)));                      // 不外推
    }
}
