using ClosedXML.Excel;
using Tec.Driver.Abi;
using Tec.LimitTest.Records;
using Tec.LimitTest.Runs;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 极限测试序列引擎：用一台假机器（一阶热模型，夹套往目标走、有个到不了的底）和虚拟时钟，
/// 把两个小时的矩阵跑成几百毫秒。锁的是：
/// · 换水温停下来等人、填的水温进条件块；
/// · 每个记录点写一行、结果那格写清「完成 / 提前结束 / 中止」；
/// · 停止 / 跳过 / 急停 / 安全层触发 / 链路断 各走各的路，中止原因进表；
/// · 最高温的「切到电加热时刻」、恒温的「到达用时」「继电器动作」从状态量数出来。
/// </summary>
public class LimitTestRunnerTests
{
    /// <summary>虚拟时钟：delay 就是拨表，不真等。</summary>
    private sealed class Clock
    {
        public DateTimeOffset Now = new(2026, 9, 22, 9, 0, 0, TimeSpan.FromHours(8));
        public Task Delay(TimeSpan t, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Now += t;
            return Task.CompletedTask;
        }
    }

    /// <summary>假机器：夹套按一阶惯性追目标（时间常数 2 min），最低只能到 Floor，最高到 Ceiling。</summary>
    private sealed class FakeRig : IRig
    {
        private readonly Clock _clk;
        private readonly double[] _tj = { 23.5, 23.4 };
        private readonly double?[] _target = new double?[2];
        private DateTimeOffset _last;
        public double Floor = -35, Ceiling = 160, TauMinutes = 2;
        public double? Limited = 90, Over = 180;
        public bool Alive = true;
        public bool NoReadings;
        public int Stops, SafeStops;
        public List<(int Well, double Target)> Targets = new();

        public FakeRig(Clock clk) { _clk = clk; _last = clk.Now; }

        private void Step()
        {
            var dt = (_clk.Now - _last).TotalMinutes;
            _last = _clk.Now;
            if (dt <= 0) return;
            for (var w = 0; w < 2; w++)
            {
                if (_target[w] is not { } t) continue;
                var goal = Math.Clamp(t, Floor, Ceiling);
                _tj[w] += (goal - _tj[w]) * (1 - Math.Exp(-dt / TauMinutes));
            }
        }

        public string Describe => "假机器 · CH1 / CH2";
        public int WellCount => 2;
        public event Action<string>? Tripped;
        public void Trip(string why) => Tripped?.Invoke(why);

        public WellReading Read(int well)
        {
            Step();
            if (NoReadings) return WellReading.Empty;
            var t = _target[well];
            var src = t is { } x && x > 90 ? "电加热" : "TEC";
            return new WellReading(Math.Round(_tj[well], 2), Math.Round(_tj[well] + 0.3, 2),
                                   t is null ? 0 : 90, 4.2, src, t is not null);
        }

        public bool IsAlive(out string why) { why = Alive ? "" : "串口没打开"; return Alive; }
        public TempLimits? Limits(int well) => new(-40, 180, 5);
        public double? Threshold => 90;
        public double? Band => 2;
        public Task SetTargetAsync(int well, double target, CancellationToken ct)
        { Step(); _target[well] = target; Targets.Add((well, target)); return Task.CompletedTask; }
        public Task StopAsync(int well, CancellationToken ct) { Step(); _target[well] = null; Stops++; return Task.CompletedTask; }
        public Task<IReadOnlyList<string>> SafeStopAsync(CancellationToken ct)
        { _target[0] = _target[1] = null; SafeStops++; return Task.FromResult<IReadOnlyList<string>>(new[] { "R1 已关两路输出" }); }
        public Task<double?> ReadLimitedAsync(int well, CancellationToken ct) => Task.FromResult(Limited);
        public Task<double?> ReadOverLimitAsync(int well, CancellationToken ct) => Task.FromResult(Over);
    }

    private static string OutDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tec-limit-run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static TestSettings Quick() => new()
    {
        MinMinutes = 20, MaxMinutes = 20, HoldMinutes = 10, HoldStepMinutes = 2,
        SettleMinutes = 5, SettleBand = 0.2, ReturnMaxMinutes = 10, HoldReachMaxMinutes = 30,
        Operator = "测试员", Ambient = 24.5, Patch = "0323"
    };

    private static (LimitTestRunner Runner, FakeRig Rig, Clock Clk, List<string> Logs, string Dir) Rig(TestSettings? s = null)
    {
        var clk = new Clock();
        var rig = new FakeRig(clk);
        var logs = new List<string>();
        var dir = OutDir();
        var runner = new LimitTestRunner(rig, s ?? Quick(), dir, () => clk.Now, clk.Delay, (l, t) => { lock (logs) logs.Add($"{l} {t}"); });
        return (runner, rig, clk, logs, dir);
    }

    /// <summary>后台跑整轮，等它停在「等水」上时替操作人填水温。</summary>
    private static async Task<Task> StartAndFeedWaterAsync(LimitTestRunner runner, CancellationToken ct, params double[] waters)
    {
        var run = Task.Run(() => runner.RunAsync(ct));
        foreach (var w in waters)
        {
            await WaitFor(() => runner.State == RunnerState.WaitingWater, run);
            Assert.Contains($"冷却水调到 {w:0.#} ℃", runner.Prompt);
            Assert.True(runner.ConfirmWater(w + 0.4));
            await WaitFor(() => runner.State != RunnerState.WaitingWater, run);
        }
        return run;
    }

    private static async Task WaitFor(Func<bool> cond, Task run)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!cond())
        {
            if (run.IsCompleted) { await run; Assert.True(cond(), "整轮已经结束，条件还没满足"); return; }
            Assert.True(DateTime.UtcNow < deadline, "等太久");
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task 只勾20度三项_等水填水温_三张表都写满_汇总有数()
    {
        var (runner, rig, clk, logs, dir) = Rig();
        foreach (var c in runner.Plan) c.Selected = c.Water == 20;
        var run = await StartAndFeedWaterAsync(runner, CancellationToken.None, 20);
        await run;

        Assert.Equal(RunnerState.Finished, runner.State);
        Assert.Equal("全部跑完", runner.Status);
        Assert.All(runner.Plan.Where(c => c.Water == 20), c => Assert.True(c.State is CellState.Done or CellState.EndedEarly, c.Note));
        Assert.All(runner.Plan.Where(c => c.Water != 20), c => Assert.Equal(CellState.Unselected, c.State));

        // 下发顺序：最低温两路 → 回温两路 → 最高温两路 → 回温两路 → 恒温两路
        Assert.Equal(new[] { -40d, -40, 25, 25, 150, 150, 25, 25, 25, 25 }, rig.Targets.Select(t => t.Target).ToArray());
        Assert.True(rig.Stops >= 6);

        var path = runner.BookPath!;
        Assert.True(File.Exists(path));
        using var wb = new XLWorkbook(path);
        using var probe = new RecordBook(Path.Combine(dir, "probe.xlsx"), Quick().ToBookSpec());

        // 最低温：假机器底在 −35，20 min 里到不了 −40，但夹套稳住了 → 提前结束；行按分钟写
        var lo = probe.Layout(TestKind.MinTemp, 20);
        var ws = wb.Worksheet(lo.Name);
        XLCellValue Cond(SheetLayout l, string k) => wb.Worksheet(l.Name).Cell(l.Cond[k].Row, l.Cond[k].Col).Value;
        Assert.Equal(20.4, Cond(lo, RecordBook.CWater).GetNumber(), 6);
        Assert.Equal("测试员", Cond(lo, RecordBook.COperator).GetText());
        Assert.Equal(24.5, Cond(lo, RecordBook.CAmbient).GetNumber());
        Assert.Equal(-40, Cond(lo, RecordBook.CTargetA).GetNumber());
        Assert.Equal(90, Cond(lo, RecordBook.CLimitedB).GetNumber());
        Assert.StartsWith("提前结束", Cond(lo, RecordBook.CResult).GetText());
        Assert.Equal(23.5, ws.Cell(lo.FirstRow, lo.Col[RecordBook.KATj]).GetDouble(), 6);
        Assert.Equal("TEC", ws.Cell(lo.FirstRow, lo.Col[RecordBook.KASrc]).GetText());
        Assert.Equal(4.2, ws.Cell(lo.FirstRow + 5, lo.Col[RecordBook.KBCur]).GetDouble(), 6);
        var written = Enumerable.Range(0, 21).Count(i => !ws.Cell(lo.FirstRow + i, lo.Col[RecordBook.KATj]).IsEmpty());
        Assert.InRange(written, 11, 20);          // 至少跑够 5 + 5 min 才判稳，20 min 前就稳了
        wb.RecalculateAllFormulas();
        var aMin = ws.Cell(lo.Sum["AExt"].Row, lo.Sum["AExt"].Col).GetDouble();
        Assert.InRange(aMin, -35.5, -30);
        Assert.Equal("未到", ws.Cell(lo.Sum["AReach"].Row, lo.Sum["AReach"].Col).GetText());

        // 最高温：目标 150 > 90，假机器一下发就报「电加热」→ 切换时刻 0.0
        var hi = probe.Layout(TestKind.MaxTemp, 20);
        Assert.Equal(0, Cond(hi, RecordBook.CElecA).GetNumber());
        Assert.Equal(90, Cond(hi, RecordBook.CThreshold).GetNumber());
        Assert.Equal(180, Cond(hi, RecordBook.COverA).GetNumber());
        var wsHi = wb.Worksheet(hi.Name);
        Assert.Equal("电加热", wsHi.Cell(hi.FirstRow + 1, hi.Col[RecordBook.KBSrc]).GetText());

        // 恒温：先等到达（回温后离 25 已经很近）→ 到达用时写进条件块 → 10 min 每 2 min 一行 = 6 行
        var hold = probe.Layout(TestKind.Hold, 20);
        var wsH = wb.Worksheet(hold.Name);
        Assert.True(Cond(hold, RecordBook.CReachA).IsNumber);
        Assert.Equal(2, Cond(hold, RecordBook.CBand).GetNumber());
        Assert.Equal("完成", Cond(hold, RecordBook.CResult).GetText());
        for (var i = 0; i < 6; i++)
        {
            Assert.False(wsH.Cell(hold.FirstRow + i, hold.Col[RecordBook.KATj]).IsEmpty(), $"恒温第 {i} 行没写");
            Assert.Equal(0, wsH.Cell(hold.FirstRow + i, hold.Col[RecordBook.KRelay]).GetDouble());
        }
        Assert.InRange(wsH.Cell(hold.FirstRow + 5, hold.Col[RecordBook.KATr]).GetDouble(), 24, 26);

        // 结果汇总矩阵拉得到
        var sum = wb.Worksheet(RecordBook.SummarySheet);
        var row = sum.Column(1).CellsUsed().First(c => c.GetText() == "最低温 ℃").Address.RowNumber;
        Assert.Equal(aMin, sum.Cell(row, 2).GetDouble(), 6);

        // csv：一次测试一个文件，每秒一行
        var csvs = Directory.GetFiles(Path.Combine(dir, "csv"));
        Assert.Equal(3, csvs.Length);
        Assert.True(File.ReadLines(csvs[0]).Count() > 60);
        Assert.Contains(logs, l => l.Contains("实际水温（手填）20.4"));
    }

    [Fact]
    public async Task 三个水温各勾一项_每组都停下来等水_跳过一组()
    {
        var (runner, rig, clk, logs, _) = Rig();
        foreach (var c in runner.Plan) c.Selected = c.Kind == TestKind.Hold;
        var run = Task.Run(() => runner.RunAsync(CancellationToken.None));

        await WaitFor(() => runner.State == RunnerState.WaitingWater, run);
        Assert.Contains("20 ℃", runner.Prompt);
        runner.ConfirmWater(20);
        await WaitFor(() => runner.State == RunnerState.WaitingWater && runner.Prompt!.Contains("15 ℃"), run);
        runner.SkipCurrent();                              // 15 ℃ 这一组不做
        await WaitFor(() => runner.State == RunnerState.WaitingWater && runner.Prompt!.Contains("7 ℃"), run);
        runner.ConfirmWater(7.2);
        await run;

        Assert.Equal(CellState.Done, runner.Cell(TestKind.Hold, 20).State);
        Assert.Equal(CellState.Skipped, runner.Cell(TestKind.Hold, 15).State);
        Assert.Equal(CellState.Done, runner.Cell(TestKind.Hold, 7).State);
        Assert.Equal("全部跑完", runner.Status);
        Assert.Contains(logs, l => l.Contains("15 ℃ 这一组跳过"));
        // 7 ℃ 的表里水温是 7.2；15 ℃ 的表没动过
        using var wb = new XLWorkbook(runner.BookPath!);
        using var probe = new RecordBook(Path.Combine(OutDir(), "p.xlsx"), Quick().ToBookSpec());
        var l7 = probe.Layout(TestKind.Hold, 7);
        Assert.Equal(7.2, wb.Worksheet(l7.Name).Cell(l7.Cond[RecordBook.CWater].Row, l7.Cond[RecordBook.CWater].Col).GetDouble(), 6);
        var l15 = probe.Layout(TestKind.Hold, 15);
        Assert.True(wb.Worksheet(l15.Name).Cell(l15.Cond[RecordBook.CResult].Row, l15.Cond[RecordBook.CResult].Col).Value.IsBlank);
        // 跳过的那一组没动机器：20 ℃ 恒温（两路）→ 回温（两路）→ 7 ℃ 恒温（两路）；15 ℃ 那组一次目标都没下发
        Assert.Equal(new[] { 25d, 25, 25, 25, 25, 25 }, rig.Targets.Select(t => t.Target).ToArray());
    }

    [Fact]
    public async Task 停止_当前那格记中止_后面的记没跑到_控温关掉()
    {
        var (runner, rig, clk, logs, _) = Rig();
        foreach (var c in runner.Plan) c.Selected = c.Water == 20;
        using var cts = new CancellationTokenSource();
        var run = await StartAndFeedWaterAsync(runner, cts.Token, 20);
        await WaitFor(() => runner.Current?.Kind == TestKind.MinTemp && runner.Elapsed > TimeSpan.FromMinutes(3), run);
        cts.Cancel();
        await run;

        Assert.Equal(CellState.Aborted, runner.Cell(TestKind.MinTemp, 20).State);
        Assert.Equal("操作人停止", runner.Cell(TestKind.MinTemp, 20).Note);
        Assert.Equal(CellState.Skipped, runner.Cell(TestKind.MaxTemp, 20).State);
        Assert.Equal("已停止", runner.Status);
        Assert.True(rig.Stops >= 2);
        using var wb = new XLWorkbook(runner.BookPath!);
        using var probe = new RecordBook(Path.Combine(OutDir(), "p.xlsx"), Quick().ToBookSpec());
        var lo = probe.Layout(TestKind.MinTemp, 20);
        Assert.Equal("中止：操作人停止", wb.Worksheet(lo.Name).Cell(lo.Cond[RecordBook.CResult].Row, lo.Cond[RecordBook.CResult].Col).GetText());
        // 停之前写的行都在
        Assert.False(wb.Worksheet(lo.Name).Cell(lo.FirstRow + 2, lo.Col[RecordBook.KATj]).IsEmpty());
    }

    [Fact]
    public async Task 跳过当前_接着做下一项()
    {
        var (runner, rig, clk, logs, _) = Rig();
        foreach (var c in runner.Plan) c.Selected = c.Water == 20 && c.Kind != TestKind.Hold;
        var run = await StartAndFeedWaterAsync(runner, CancellationToken.None, 20);
        await WaitFor(() => runner.Current?.Kind == TestKind.MinTemp && runner.Elapsed > TimeSpan.FromMinutes(2), run);
        runner.SkipCurrent();
        await run;

        Assert.Equal(CellState.Skipped, runner.Cell(TestKind.MinTemp, 20).State);
        Assert.True(runner.Cell(TestKind.MaxTemp, 20).State is CellState.Done or CellState.EndedEarly);
        Assert.Equal("全部跑完", runner.Status);
    }

    [Fact]
    public async Task 急停_设备SafeStop_整轮结束()
    {
        var (runner, rig, clk, logs, _) = Rig();
        foreach (var c in runner.Plan) c.Selected = c.Water == 20;
        var run = await StartAndFeedWaterAsync(runner, CancellationToken.None, 20);
        await WaitFor(() => runner.Current?.Kind == TestKind.MinTemp && runner.Elapsed > TimeSpan.FromMinutes(1), run);
        await runner.EmergencyStopAsync();
        await run;

        Assert.Equal(1, rig.SafeStops);
        Assert.Equal(CellState.Aborted, runner.Cell(TestKind.MinTemp, 20).State);
        Assert.Equal("急停", runner.Cell(TestKind.MinTemp, 20).Note);
        Assert.Equal(CellState.Skipped, runner.Cell(TestKind.MaxTemp, 20).State);
        Assert.Contains("急停", runner.Status);
        Assert.Contains(logs, l => l.Contains("急停：R1 已关两路输出"));
    }

    [Fact]
    public async Task 安全层触发_中止并停整轮_原因进表()
    {
        var (runner, rig, clk, logs, _) = Rig();
        foreach (var c in runner.Plan) c.Selected = c.Water == 20;
        var run = await StartAndFeedWaterAsync(runner, CancellationToken.None, 20);
        await WaitFor(() => runner.Current?.Kind == TestKind.MinTemp && runner.Elapsed > TimeSpan.FromMinutes(1), run);
        rig.Trip("CH1 Tr 低于下限 -41");
        await run;

        var cell = runner.Cell(TestKind.MinTemp, 20);
        Assert.Equal(CellState.Aborted, cell.State);
        Assert.Equal("安全层触发：CH1 Tr 低于下限 -41", cell.Note);
        Assert.Equal(CellState.Skipped, runner.Cell(TestKind.MaxTemp, 20).State);
        Assert.True(rig.Stops >= 2);
        using var wb = new XLWorkbook(runner.BookPath!);
        using var probe = new RecordBook(Path.Combine(OutDir(), "p.xlsx"), Quick().ToBookSpec());
        var lo = probe.Layout(TestKind.MinTemp, 20);
        Assert.StartsWith("中止：安全层触发", wb.Worksheet(lo.Name).Cell(lo.Cond[RecordBook.CResult].Row, lo.Cond[RecordBook.CResult].Col).GetText());
    }

    [Fact]
    public async Task 读数丢了30秒_中止说链路断了()
    {
        var (runner, rig, clk, logs, _) = Rig();
        foreach (var c in runner.Plan) c.Selected = c.Water == 20 && c.Kind == TestKind.MinTemp;
        var run = await StartAndFeedWaterAsync(runner, CancellationToken.None, 20);
        await WaitFor(() => runner.Elapsed > TimeSpan.FromMinutes(1), run);
        rig.NoReadings = true;
        await run;

        var cell = runner.Cell(TestKind.MinTemp, 20);
        Assert.Equal(CellState.Aborted, cell.State);
        Assert.Contains("30 s 没有读数", cell.Note);
    }

    [Fact]
    public async Task 恒温等不到目标_那格中止_下一格照跑()
    {
        var s = Quick();
        s.HoldTarget = -60;                 // 假机器底在 −35，永远到不了
        s.HoldReachMaxMinutes = 8;
        var (runner, rig, clk, logs, _) = Rig(s);
        foreach (var c in runner.Plan) c.Selected = c.Water == 20 && c.Kind != TestKind.MinTemp;
        // 顺序是 最高温 → 恒温：把最高温也去掉，只看恒温后面还有没有下一格——用 7 ℃ 的一格当「下一格」
        runner.Cell(TestKind.MaxTemp, 20).Selected = false;
        runner.Cell(TestKind.MaxTemp, 7).Selected = true;
        var run = await StartAndFeedWaterAsync(runner, CancellationToken.None, 20, 7);
        await run;

        var hold = runner.Cell(TestKind.Hold, 20);
        Assert.Equal(CellState.Aborted, hold.State);
        Assert.Contains("8 min 内没到达恒温目标", hold.Note);
        Assert.True(runner.Cell(TestKind.MaxTemp, 7).State is CellState.Done or CellState.EndedEarly);
    }

    [Fact]
    public void 一格都没勾_或设备没连_开始就拒绝()
    {
        var (runner, rig, _, _, _) = Rig();
        foreach (var c in runner.Plan) c.Selected = false;
        Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(CancellationToken.None)).Wait();
        var (runner2, rig2, _, _, _) = Rig();
        rig2.Alive = false;
        var ex = Assert.ThrowsAsync<InvalidOperationException>(() => runner2.RunAsync(CancellationToken.None)).Result;
        Assert.Contains("串口没打开", ex.Message);
    }
}
