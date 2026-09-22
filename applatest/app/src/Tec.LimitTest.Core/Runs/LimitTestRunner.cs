using System.Globalization;
using ClosedXML.Excel;
using Tec.LimitTest.Records;

namespace Tec.LimitTest.Runs;

public enum RunnerState
{
    Idle,
    /// <summary>停在一组水温开始前，等操作人把冷却水调好、填实际水温、点「继续」。</summary>
    WaitingWater,
    Running,
    /// <summary>两项之间回到室温。</summary>
    Returning,
    Finished
}

/// <summary>
/// 极限测试的序列引擎：按「最低温 → 最高温 → 恒温稳定性」在每个冷却水温度下串着跑，
/// 换水温停下来等人；每秒采样进 csv，按表的间隔往 <see cref="RecordBook"/> 里写一行并保存。
///
/// 规矩：
/// · 每个数都来自 <see cref="IRig"/> 的读数，没读到就留空；水温、环境温度、操作人是人填的，表里也这么标；
/// · 中止的原因写进那一格的「结果」：操作人停止 / 跳过 / 急停 / 安全层触发 / 读数丢失 / 夹套跑出设备范围；
/// · 急停走设备的 SafeStop（所有输出收安全态）；别的中止只关这两路的控温；
/// · 时钟与等待都是注入的（now / delay），回归测试能用虚拟时间把两小时跑成两秒。
/// </summary>
public sealed class LimitTestRunner
{
    private readonly IRig _rig;
    private readonly TestSettings _s;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Action<string, string> _log;
    private readonly string _outDir;
    private readonly List<PlanCell> _plan = new();

    private TaskCompletionSource<double>? _waterGate;
    private CancellationTokenSource? _cell;        // 当前测试 / 回温段的取消源
    private string? _abortReason;
    private bool _fatal;                           // 这次中止之后整轮都停（安全层 / 急停 / 链路）
    private bool _emergency;

    public LimitTestRunner(IRig rig, TestSettings settings, string outDir,
                           Func<DateTimeOffset>? now = null,
                           Func<TimeSpan, CancellationToken, Task>? delay = null,
                           Action<string, string>? log = null)
    {
        _rig = rig;
        _s = settings;
        _outDir = outDir;
        _now = now ?? (() => DateTimeOffset.Now);
        _delay = delay ?? ((t, ct) => Task.Delay(t, ct));
        _log = log ?? ((_, _) => { });
        foreach (var w in _s.Waters)
            foreach (var k in TestKinds.All)
                _plan.Add(new PlanCell(k, w));
    }

    /// <summary>矩阵：按水温分组、组内按项。界面上勾选 Selected。</summary>
    public IReadOnlyList<PlanCell> Plan => _plan;

    public PlanCell Cell(TestKind kind, double water) => _plan.First(c => c.Kind == kind && c.Water == water);

    public RunnerState State { get; private set; } = RunnerState.Idle;
    /// <summary>一句话的当前状态，界面直接显示。</summary>
    public string Status { get; private set; } = "未开始";
    /// <summary>等人的时候要人做什么。</summary>
    public string? Prompt { get; private set; }
    public PlanCell? Current { get; private set; }
    /// <summary>当前阶段（「降温中」「等到达」「恒温记录」「回温」）。</summary>
    public string Phase { get; private set; } = "";
    public TimeSpan Elapsed { get; private set; }
    public RecordBook? Book { get; private set; }
    public string? BookPath => Book?.Path;

    /// <summary>状态有变（界面刷新用）。可能在任何线程上来。</summary>
    public event Action? Changed;

    private void Touch() => Changed?.Invoke();

    private void Log(string level, string text) => _log(level, text);

    // ── 操作人的三个钮 ─────────────────────────────────────────────

    /// <summary>冷却水调好了：填实际水温（手填，写进这一组每张表的条件块）。</summary>
    public bool ConfirmWater(double actual) => _waterGate?.TrySetResult(actual) ?? false;

    /// <summary>跳过当前这一格（或当前回温段），接着跑下一格。</summary>
    public void SkipCurrent() => Cancel("操作人跳过", fatal: false);

    /// <summary>急停：设备所有输出收安全态，整轮结束。</summary>
    public async Task EmergencyStopAsync()
    {
        _emergency = true;
        Cancel("急停", fatal: true);
        IReadOnlyList<string> did;
        try { did = await _rig.SafeStopAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { Log("error", $"急停时设备报错：{ex.Message}"); return; }
        Log("warn", did.Count == 0 ? "急停：设备报没有可停的输出" : "急停：" + string.Join("；", did));
    }

    private void Cancel(string reason, bool fatal)
    {
        _abortReason ??= reason;
        _fatal |= fatal;
        _cell?.Cancel();
        _waterGate?.TrySetCanceled();
    }

    private void OnTripped(string why) => Cancel("安全层触发：" + why, fatal: true);

    // ── 整轮 ────────────────────────────────────────────────────────

    /// <summary>跑整个矩阵。ct = 界面上的「停止」：当前测试记为中止、整轮结束。</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        if (State is RunnerState.Running or RunnerState.WaitingWater or RunnerState.Returning)
            throw new InvalidOperationException("已经在跑了");
        _fatal = false;
        _emergency = false;
        _abortReason = null;
        foreach (var c in _plan)
        {
            c.State = c.Selected ? CellState.Pending : CellState.Unselected;
            c.Note = "";
        }
        if (_plan.All(c => !c.Selected)) throw new InvalidOperationException("矩阵里一格都没勾");
        if (!_rig.IsAlive(out var why)) throw new InvalidOperationException("设备没连上：" + why);

        var stamp = _now().ToString("yyyyMMdd_HHmm", CultureInfo.InvariantCulture);
        Directory.CreateDirectory(_outDir);
        Book = new RecordBook(Path.Combine(_outDir, $"控温极限测试记录_{stamp}.xlsx"), _s.ToBookSpec());
        Log("info", $"极限测试开始：{_rig.Describe}；记录写到 {Book.Path}");
        _rig.Tripped += OnTripped;
        State = RunnerState.Running;
        Status = "开始";
        Touch();

        var order = _plan.Where(c => c.Selected).ToList();
        try
        {
            foreach (var water in _s.Waters)
            {
                var group = order.Where(c => c.Water == water).ToList();
                if (group.Count == 0) continue;

                var actual = await WaitWaterAsync(water, ct).ConfigureAwait(false);

                foreach (var cell in group)
                {
                    var ran = await RunCellAsync(cell, actual, ct).ConfigureAwait(false);
                    if (_fatal || ct.IsCancellationRequested) break;
                    // 真跑过（机器动过）而且后面还有格才回温；整组跳过的没动机器，不用回
                    if (ran && !ReferenceEquals(cell, order[^1]))
                        await ReturnAsync(ct).ConfigureAwait(false);
                    if (_fatal || ct.IsCancellationRequested) break;
                }
                if (_fatal || ct.IsCancellationRequested) break;
            }
            Status = _fatal ? $"已停止（{_abortReason}）" : ct.IsCancellationRequested ? "已停止" : "全部跑完";
        }
        catch (OperationCanceledException)
        {
            Status = _fatal ? $"已停止（{_abortReason}）" : "已停止";
        }
        finally
        {
            _rig.Tripped -= OnTripped;
            // 没跑到的格子照实标出来
            foreach (var c in _plan)
                if (c.State == CellState.Pending) { c.State = CellState.Skipped; c.Note = "没跑到"; }
            if (!_emergency) await StopWellsAsync().ConfigureAwait(false);
            try { Book.Save(); } catch (Exception ex) { Log("error", $"记录表保存失败：{ex.Message}"); }
            State = RunnerState.Finished;
            Current = null;
            Prompt = null;
            Phase = "";
            Log("info", $"极限测试结束：{Status}");
            Touch();
        }
    }

    private async Task<double> WaitWaterAsync(double water, CancellationToken ct)
    {
        _waterGate = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        State = RunnerState.WaitingWater;
        Prompt = $"请把冷却水调到 {water:0.#} ℃，等水温稳住，把冷水机上看到的实际水温填进来，点「继续」";
        Status = $"等冷却水 {water:0.#} ℃";
        Touch();
        using var reg = ct.Register(() => _waterGate.TrySetCanceled());
        double actual;
        try { actual = await _waterGate.Task.ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            // 等水的时候按了跳过：这一组都跳过；按了停止 / 急停：整轮停
            if (ct.IsCancellationRequested || _fatal) throw;
            _abortReason = null;
            foreach (var c in _plan.Where(c => c.Water == water && c.State == CellState.Pending))
            { c.State = CellState.Skipped; c.Note = "这一组水温跳过"; }
            Log("warn", $"冷却水 {water:0.#} ℃ 这一组跳过");
            _waterGate = null;
            Prompt = null;
            return double.NaN;
        }
        _waterGate = null;
        Prompt = null;
        Log("info", $"冷却水 {water:0.#} ℃ 这一组开始，实际水温（手填）{actual:0.0} ℃");
        return actual;
    }

    // ── 一格 ────────────────────────────────────────────────────────

    private sealed class TestAbort : Exception
    {
        public TestAbort(string why, bool fatal) : base(why) => Fatal = fatal;
        public bool Fatal { get; }
    }

    /// <summary>跑一格。返回有没有真跑（整组跳过的格子状态已不是 Pending，直接返回 false）。</summary>
    private async Task<bool> RunCellAsync(PlanCell cell, double waterActual, CancellationToken ct)
    {
        if (cell.State != CellState.Pending) return false;
        var book = Book!;
        var kind = cell.Kind;
        var water = cell.Water;
        var lay = book.Layout(kind, water);
        var target = _s.TargetOf(kind);

        cell.State = CellState.Running;
        Current = cell;
        State = RunnerState.Running;
        Status = $"{cell}：{TestKinds.Name(kind)} 目标 {target:0.#} ℃";
        Phase = "准备";
        Elapsed = TimeSpan.Zero;
        _abortReason = null;
        _cell = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cell.Token;
        Touch();
        Log("info", $"开始 {cell}：目标 {target:0.#} ℃");

        // 条件块：知道的都写上，读不到的留空
        book.SetCondition(kind, water, RecordBook.CDate, _now().Date);
        book.SetCondition(kind, water, RecordBook.COperator, _s.Operator);
        book.SetCondition(kind, water, RecordBook.CAmbient, V(_s.Ambient));
        book.SetCondition(kind, water, RecordBook.CWater, double.IsNaN(waterActual) ? Blank.Value : waterActual);
        book.SetCondition(kind, water, RecordBook.CPatch, _s.Patch);
        switch (kind)
        {
            case TestKind.MinTemp:
                book.SetCondition(kind, water, RecordBook.CTargetA, target);
                book.SetCondition(kind, water, RecordBook.CTargetB, target);
                book.SetCondition(kind, water, RecordBook.CLimitedA, V(await SafeRead(() => _rig.ReadLimitedAsync(0, token), "LIMITED A")));
                book.SetCondition(kind, water, RecordBook.CLimitedB, V(await SafeRead(() => _rig.ReadLimitedAsync(1, token), "LIMITED B")));
                break;
            case TestKind.MaxTemp:
                book.SetCondition(kind, water, RecordBook.CTargetA, target);
                book.SetCondition(kind, water, RecordBook.CTargetB, target);
                book.SetCondition(kind, water, RecordBook.CThreshold, V(_rig.Threshold));
                book.SetCondition(kind, water, RecordBook.COverA, V(await SafeRead(() => _rig.ReadOverLimitAsync(0, token), "超温上限 A")));
                book.SetCondition(kind, water, RecordBook.COverB, V(await SafeRead(() => _rig.ReadOverLimitAsync(1, token), "超温上限 B")));
                break;
            default:
                book.SetCondition(kind, water, RecordBook.CTarget, target);
                book.SetCondition(kind, water, RecordBook.CBand, V(_rig.Band));
                break;
        }
        book.Save();

        var csvPath = Path.Combine(_outDir, "csv",
            $"{_now():yyyyMMdd_HHmm}_{TestKinds.Short(kind)}_{water.ToString("0.#", CultureInfo.InvariantCulture)}C.csv");
        using var csv = new CsvLog(csvPath);
        var result = "";
        try
        {
            await _rig.SetTargetAsync(0, target, token).ConfigureAwait(false);
            await _rig.SetTargetAsync(1, target, token).ConfigureAwait(false);
            Log("info", $"{cell}：两工位目标 {target:0.#} ℃ 已下发（尽快）");

            var t0 = _now();
            if (kind == TestKind.Hold)
                t0 = await WaitReachAsync(cell, target, csv, t0, token).ConfigureAwait(false);

            result = await RecordAsync(cell, lay, target, csv, t0, token).ConfigureAwait(false);
            cell.State = result.StartsWith("提前", StringComparison.Ordinal) ? CellState.EndedEarly : CellState.Done;
            cell.Note = result;
            Log("info", $"{cell}：{result}");
        }
        catch (TestAbort ex)
        {
            result = "中止：" + ex.Message;
            cell.State = CellState.Aborted;
            cell.Note = ex.Message;
            _fatal |= ex.Fatal;
            Log("error", $"{cell} 中止：{ex.Message}");
        }
        catch (OperationCanceledException)
        {
            var why = _abortReason ?? "操作人停止";
            result = "中止：" + why;
            cell.State = why == "操作人跳过" ? CellState.Skipped : CellState.Aborted;
            cell.Note = why;
            Log("warn", $"{cell} 中止：{why}");
        }
        catch (Exception ex)
        {
            result = "中止：" + ex.Message;
            cell.State = CellState.Aborted;
            cell.Note = ex.Message;
            _fatal = true;
            Log("error", $"{cell} 出错：{ex.Message}");
        }
        finally
        {
            book.SetCondition(kind, water, RecordBook.CResult, result);
            try { book.Save(); } catch (Exception ex) { Log("error", $"记录表保存失败：{ex.Message}"); }
            if (cell.State is CellState.Aborted or CellState.Skipped && !_emergency)
                await StopWellsAsync().ConfigureAwait(false);
            _cell.Dispose();
            _cell = null;
            Touch();
        }
        return true;
    }

    /// <summary>恒温的第一段：等两工位夹套都进目标 ±tol；到达用时写进条件块。返回恒温记录的起点。</summary>
    private async Task<DateTimeOffset> WaitReachAsync(PlanCell cell, double target, CsvLog csv, DateTimeOffset t0, CancellationToken token)
    {
        Phase = "等到达";
        Touch();
        var reached = new DateTimeOffset?[2];
        var lastGood = t0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var now = _now();
            var el = now - t0;
            Elapsed = el;
            var a = _rig.Read(0);
            var b = _rig.Read(1);
            csv.Write(now, el, a, b, "等到达");
            GuardLink(ref lastGood, now, a, b);
            GuardLimits(a, b);
            for (var w = 0; w < 2; w++)
            {
                var tj = w == 0 ? a.Tj : b.Tj;
                if (reached[w] is null && tj is { } v && Math.Abs(v - target) <= _s.ReachTol)
                {
                    reached[w] = now;
                    Book!.SetCondition(cell.Kind, cell.Water, w == 0 ? RecordBook.CReachA : RecordBook.CReachB,
                                       Math.Round(el.TotalMinutes, 1));
                    Log("info", $"{cell}：工位 {(w == 0 ? "A" : "B")} 到达 {target:0.#} ℃（{el.TotalMinutes:0.0} min）");
                }
            }
            if (reached[0] is not null && reached[1] is not null)
            {
                Book!.Save();
                return now;
            }
            if (el.TotalMinutes >= _s.HoldReachMaxMinutes)
                throw new TestAbort($"{_s.HoldReachMaxMinutes} min 内没到达恒温目标（A {(reached[0] is null ? "未到" : "已到")}、B {(reached[1] is null ? "未到" : "已到")}）", fatal: false);
            Touch();
            await _delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
        }
    }

    /// <summary>记录段：每秒采样进 csv，每到一个记录点写一行进表并保存；返回「完成」或「提前结束（…）」。</summary>
    private async Task<string> RecordAsync(PlanCell cell, SheetLayout lay, double target, CsvLog csv, DateTimeOffset t0, CancellationToken token)
    {
        var kind = cell.Kind;
        var water = cell.Water;
        var book = Book!;
        var rows = book.Spec.RowsOf(kind);
        var step = lay.Step;
        Phase = kind switch { TestKind.MinTemp => "降温中", TestKind.MaxTemp => "升温中", _ => "恒温记录" };
        Touch();

        var lastIdx = -1;
        var relayActions = 0;
        WellReading? prevA = null, prevB = null;
        var elecAt = new double?[2];
        var lastGood = t0;
        var history = new List<(TimeSpan T, double? A, double? B)>();

        while (true)
        {
            token.ThrowIfCancellationRequested();
            var now = _now();
            var el = now - t0;
            Elapsed = el;
            var a = _rig.Read(0);
            var b = _rig.Read(1);
            csv.Write(now, el, a, b, Phase);
            GuardLink(ref lastGood, now, a, b);
            GuardLimits(a, b);

            // 继电器动作：热源 / 功率线两个状态量跳一次算一次（两工位合计）
            relayActions += Transitions(prevA, a) + Transitions(prevB, b);
            prevA = a;
            prevB = b;
            // 最高温：第一次看到「电加热」就是切换时刻
            if (kind == TestKind.MaxTemp)
                for (var w = 0; w < 2; w++)
                {
                    var src = w == 0 ? a.Source : b.Source;
                    if (elecAt[w] is null && src == "电加热")
                    {
                        elecAt[w] = Math.Round(el.TotalMinutes, 1);
                        book.SetCondition(kind, water, w == 0 ? RecordBook.CElecA : RecordBook.CElecB, elecAt[w]!.Value);
                        Log("info", $"{cell}：工位 {(w == 0 ? "A" : "B")} 切到电加热（{el.TotalMinutes:0.0} min）");
                    }
                }

            var idx = (int)Math.Floor(el.TotalMinutes / step + 1e-6);
            if (idx > lastIdx)
            {
                lastIdx = idx;
                if (kind == TestKind.Hold)
                    book.WriteRow(kind, water, idx, new Dictionary<string, XLCellValue>
                    {
                        [RecordBook.KATj] = V(a.Tj), [RecordBook.KATr] = V(a.Tr), [RecordBook.KAOut] = V(a.Out),
                        [RecordBook.KBTj] = V(b.Tj), [RecordBook.KBTr] = V(b.Tr), [RecordBook.KBOut] = V(b.Out),
                        [RecordBook.KRelay] = relayActions
                    });
                else
                    book.WriteRow(kind, water, idx, new Dictionary<string, XLCellValue>
                    {
                        [RecordBook.KATj] = V(a.Tj), [RecordBook.KAOut] = V(a.Out), [RecordBook.KACur] = V(a.Cur),
                        [RecordBook.KATr] = V(a.Tr), [RecordBook.KASrc] = a.Source ?? "",
                        [RecordBook.KBTj] = V(b.Tj), [RecordBook.KBOut] = V(b.Out), [RecordBook.KBCur] = V(b.Cur),
                        [RecordBook.KBTr] = V(b.Tr), [RecordBook.KBSrc] = b.Source ?? ""
                    });
                relayActions = 0;
                try { book.Save(); } catch (Exception ex) { Log("error", $"记录表保存失败：{ex.Message}"); }
                Touch();
                if (idx >= rows - 1) return "完成";
            }

            if (kind != TestKind.Hold && _s.SettleBand > 0 && _s.SettleMinutes > 0)
            {
                history.Add((el, a.Tj, b.Tj));
                var window = TimeSpan.FromMinutes(_s.SettleMinutes);
                history.RemoveAll(h => el - h.T > window);
                // 至少跑够「窗口 + 5 min」再判：一开始夹套本来就稳在室温
                if (el >= window + TimeSpan.FromMinutes(5) && Settled(history, window, el))
                    return $"提前结束（夹套 {_s.SettleMinutes} min 内变化不到 {_s.SettleBand:0.0#} ℃）";
            }

            await _delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
        }
    }

    private bool Settled(List<(TimeSpan T, double? A, double? B)> h, TimeSpan window, TimeSpan el)
    {
        if (h.Count < 2 || el - h[0].T < window - TimeSpan.FromSeconds(2)) return false;
        double? Span(Func<(TimeSpan T, double? A, double? B), double?> pick)
        {
            double lo = double.MaxValue, hi = double.MinValue;
            var n = 0;
            foreach (var x in h)
                if (pick(x) is { } v) { lo = Math.Min(lo, v); hi = Math.Max(hi, v); n++; }
            return n == 0 ? null : hi - lo;
        }
        var sa = Span(x => x.A);
        var sb = Span(x => x.B);
        // 两工位都有数、都稳；一路没数就不算稳（不拿空当稳）
        return sa is { } va && sb is { } vb && va < _s.SettleBand && vb < _s.SettleBand;
    }

    private static int Transitions(WellReading? prev, WellReading now)
    {
        if (prev is null) return 0;
        var n = 0;
        if (prev.Source is not null && now.Source is not null && prev.Source != now.Source) n++;
        if (prev.TecPower is { } p && now.TecPower is { } q && p != q) n++;
        return n;
    }

    private void GuardLink(ref DateTimeOffset lastGood, DateTimeOffset now, WellReading a, WellReading b)
    {
        if (a.Tj is not null || b.Tj is not null) { lastGood = now; return; }
        if (now - lastGood > TimeSpan.FromSeconds(_s.LinkLossSeconds))
        {
            var why = _rig.IsAlive(out var w) ? "" : $"；设备：{w}";
            throw new TestAbort($"两工位夹套温度连着 {_s.LinkLossSeconds} s 没有读数（链路断了？）{why}", fatal: true);
        }
    }

    /// <summary>兜底：夹套跑出设备温度范围 2 ℃ 以外就中止。温控器自己的超温保护在它前面，这里只是程序这一侧的一道。</summary>
    private void GuardLimits(WellReading a, WellReading b)
    {
        for (var w = 0; w < 2; w++)
        {
            var tj = w == 0 ? a.Tj : b.Tj;
            if (tj is not { } v || _rig.Limits(w) is not { } lim) continue;
            if (v < lim.Min - 2 || v > lim.Max + 2)
                throw new TestAbort($"工位 {(w == 0 ? "A" : "B")} 夹套 {v:0.0} ℃ 跑出设备范围 {lim.Min:0.#}~{lim.Max:0.#} ℃", fatal: true);
        }
    }

    /// <summary>两项之间回到室温附近再做下一项——从 −40 直接去 150 不是测试想看的东西。</summary>
    private async Task ReturnAsync(CancellationToken ct)
    {
        State = RunnerState.Returning;
        Phase = "回温";
        Status = $"回到 {_s.ReturnTemp:0.#} ℃ 附近再做下一项";
        _abortReason = null;
        _cell = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cell.Token;
        Touch();
        try
        {
            await _rig.SetTargetAsync(0, _s.ReturnTemp, token).ConfigureAwait(false);
            await _rig.SetTargetAsync(1, _s.ReturnTemp, token).ConfigureAwait(false);
            var t0 = _now();
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var el = _now() - t0;
                Elapsed = el;
                var a = _rig.Read(0).Tj;
                var b = _rig.Read(1).Tj;
                var ok = a is { } va && b is { } vb
                         && Math.Abs(va - _s.ReturnTemp) <= _s.ReturnBand
                         && Math.Abs(vb - _s.ReturnTemp) <= _s.ReturnBand;
                if (ok) { Log("info", $"回温到 {_s.ReturnTemp:0.#} ℃ 附近（{el.TotalMinutes:0.0} min）"); break; }
                if (el.TotalMinutes >= _s.ReturnMaxMinutes)
                {
                    Log("warn", $"回温等了 {_s.ReturnMaxMinutes} min 还没进 ±{_s.ReturnBand:0.#} ℃（A {Fmt(a)}、B {Fmt(b)}），接着做下一项");
                    break;
                }
                Touch();
                await _delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            if (ct.IsCancellationRequested || _fatal) throw;
            Log("warn", "回温段跳过");
            _abortReason = null;
        }
        finally
        {
            _cell.Dispose();
            _cell = null;
            await StopWellsAsync().ConfigureAwait(false);
            Phase = "";
            Touch();
        }
    }

    private async Task StopWellsAsync()
    {
        for (var w = 0; w < 2; w++)
        {
            try { await _rig.StopAsync(w, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { Log("error", $"停工位 {(w == 0 ? "A" : "B")} 控温失败：{ex.Message}"); }
        }
    }

    private async Task<double?> SafeRead(Func<Task<double?>> read, string what)
    {
        try { return await read().ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log("warn", $"{what} 没读到：{ex.Message}——表里留空");
            return null;
        }
    }

    private static XLCellValue V(double? d) => d is { } x && !double.IsNaN(x) ? x : Blank.Value;

    private static string Fmt(double? d) => d is { } x ? x.ToString("0.0", CultureInfo.InvariantCulture) : "—";
}
