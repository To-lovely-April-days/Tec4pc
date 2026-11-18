using System.Globalization;
using Tec.Driver.Abi;
using Tec.LimitTest.Runs;

namespace Tec.LimitTest.Rate;

public enum RateRunnerState
{
    Idle,
    /// <summary>降温前停下来，等操作人确认冷却水开好、填冷水机上的水温、点「继续」。</summary>
    WaitingWater,
    Running,
    Finished
}

/// <summary>
/// 【本地改动】（2026-11-17，用户要的「升温速率测试和降温速率测试」）满功率升 / 降温速率测试的序列引擎。
///
/// 一项测试（两个工位一起）：
/// ① 从当前温度直接开始（用户定的，不先控到起点）。开始前每一路先查：夹套有读数；升温没到停止线；降温夹套在 TEC 接入上限以下（TEC 只能在这以下接上）；
/// ② 给夹套下一个够不着的目标（升温 = 停止线和温控器超温上限里小的那个；降温 = 温控器超温下限），输出就一直顶在最大输出占空比上——满功率；
///    两路同时下令，各自从「接上」（下发返回：热源切好、冷却水通好、回路开了）那一刻起算 0 s；
/// ③ 满功率 N s（缺省 60）到了这一路就归零（停控温），再记 M s（缺省 60）看停了以后还冲多少；
/// ④ 每秒记一个点进 csv；一项做完重写一遍 Excel 报告（汇总 + 每项每秒数据 + 曲线）。
///
/// 停的规矩（安全）：
/// · 升温途中哪一路夹套到停止线（缺省 190 ℃，导热油硬上限 200）这一路马上归零，接着记归零之后；
/// · 夹套读数几秒没更新 / 读不到、安全层触发、夹套跑出设备范围：两路全停、整轮结束；
/// · 操作人「停止」整轮结束，「跳过当前」只停当前这一项，「急停」走设备的 SafeStop；
/// · 结束时不管怎么结束的，两路都停控温（输出归零）。
///
/// 降温要冷却水：第一项降温之前停下来等人确认水开好、填水温（同极限矩阵的等水提示）；升温不用水，不问。
/// 时钟与等待是注入的（now / delay），回归测试用虚拟时间跑。
///
/// 【本地改动】（2026-11-18）升到目标温度（用户要的 80 / 100 ℃ 升温测试）：跟升温一样满功率、从当前温度开始，
/// 不按时长归零，夹套到最高目标那一路就归零（满最长时间还没到也归零、记「没到」），再记 M s；过每个目标的用时在报告里按采样插值算。
/// 每一次里排在降温后面：它一路升到 100 ℃ 以上，排在降温前面的话降温那项夹套高过 TEC 接入上限、接不上。
/// </summary>
public sealed class RateTestRunner
{
    private readonly IRig _rig;
    private readonly RateSettings _s;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Action<string, string> _log;
    private readonly string _outDir;
    private readonly List<RateTest> _tests = new();
    private readonly double?[] _limited = new double?[2];

    private TaskCompletionSource<double>? _waterGate;
    private CancellationTokenSource? _cur;
    private string? _abortReason;
    private bool _fatal;
    private bool _emergency;
    private double? _water;
    private DateTimeOffset _startedAt;
    private int _saveFails;

    public RateTestRunner(IRig rig, RateSettings settings, string outDir,
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
        for (var r = 1; r <= Math.Max(1, _s.Rounds); r++)
        {
            if (_s.Heat) _tests.Add(new RateTest(RateKind.Heat, r));
            if (_s.Cool) _tests.Add(new RateTest(RateKind.Cool, r));
            if (_s.Reach) _tests.Add(new RateTest(RateKind.Reach, r, $"升到 {RateKinds.Targets(_s.ReachTargets)} ℃"));
        }
    }

    public IReadOnlyList<RateTest> Tests => _tests;
    public RateSettings Settings => _s;
    public RateRunnerState State { get; private set; } = RateRunnerState.Idle;
    public string Status { get; private set; } = "未开始";
    public string? Prompt { get; private set; }
    public RateTest? Current { get; private set; }
    /// <summary>当前这一项每一路的一句话（「A 满功率 35 s · +12.3 ℃」），界面直接显示。</summary>
    public string Phase { get; private set; } = "";
    public string? ReportPath { get; private set; }
    /// <summary>降温用的冷却水温度（手填）。</summary>
    public double? Water => _water;

    /// <summary>状态有变（界面刷新用）。可能在任何线程上来。</summary>
    public event Action? Changed;

    private void Touch() => Changed?.Invoke();
    private void Log(string level, string text) => _log(level, text);

    // ── 操作人的钮 ─────────────────────────────────────────────────

    /// <summary>冷却水开好了：填冷水机上看到的水温（写进报告）。</summary>
    public bool ConfirmWater(double actual) => _waterGate?.TrySetResult(actual) ?? false;

    /// <summary>跳过当前这一项（等水时 = 所有降温都不做）。</summary>
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
        _cur?.Cancel();
        _waterGate?.TrySetCanceled();
    }

    private void OnTripped(string why) => Cancel("安全层触发：" + why, fatal: true);

    private sealed class RateAbort : Exception
    {
        public RateAbort(string why, bool fatal) : base(why) => Fatal = fatal;
        public bool Fatal { get; }
    }

    // ── 整轮 ────────────────────────────────────────────────────────

    /// <summary>跑整轮。ct = 界面上的「停止」：当前这一项记中止、整轮结束。</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        if (State is RateRunnerState.Running or RateRunnerState.WaitingWater)
            throw new InvalidOperationException("已经在跑了");
        if (_tests.Count == 0) throw new InvalidOperationException("升温、降温、升到目标温度一个都没勾");
        if (_s.Reach && _s.ReachTargets.Count == 0) throw new InvalidOperationException("升到目标温度没填目标");
        if (_s.Reach && _s.ReachTargets.Max() >= _s.StopLine)
            throw new InvalidOperationException($"目标温度得低于升温停止线 {_s.StopLine:0.#} ℃");
        if (!_s.Wells().Any()) throw new InvalidOperationException("工位 A、B 一个都没勾");
        if (!_rig.IsAlive(out var why)) throw new InvalidOperationException("设备没连上：" + why);

        _fatal = false;
        _emergency = false;
        _abortReason = null;
        _startedAt = _now();
        Directory.CreateDirectory(_outDir);
        ReportPath = Path.Combine(_outDir, $"升降温速率测试_{_startedAt.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)}.xlsx");
        Log("info", $"升降温速率测试开始：{_rig.Describe}；工位 {string.Join("、", _s.Wells().Select(w => w == 0 ? "A" : "B"))}；" +
                    $"满功率 {_s.FullSeconds} s、归零后再记 {_s.AfterSeconds} s；合格线 升 {_s.HeatPassK:0.#} ℃ / 降 {_s.CoolPassK:0.#} ℃；" +
                    $"升温停止线 {_s.StopLine:0.#} ℃；" +
                    (_s.Reach ? $"升到目标温度 {RateKinds.Targets(_s.ReachTargets)} ℃（满功率最长 {_s.ReachMaxMinutes} min）；" : "") +
                    $"报告写到 {ReportPath}");
        _rig.Tripped += OnTripped;
        State = RateRunnerState.Running;
        Status = "开始";
        Touch();

        var waterAsked = false;
        try
        {
            // 在 try 里读：这时候按「停止」也要走下面的收尾（状态翻回、两路归零、报告落盘）
            foreach (var w in _s.Wells())
                _limited[w] = await SafeRead(() => _rig.ReadLimitedAsync(w, ct), $"最大输出占空比 {(w == 0 ? "A" : "B")}").ConfigureAwait(false);
            SafeSave();
            foreach (var test in _tests)
            {
                if (test.State != RateState.Pending) continue;
                if (test.Kind == RateKind.Cool && !waterAsked)
                {
                    waterAsked = true;
                    if (!await WaitWaterAsync(ct).ConfigureAwait(false)) continue;    // 跳过了：降温全都不做
                }
                await RunTestAsync(test, ct).ConfigureAwait(false);
                SafeSave();
                if (_fatal || ct.IsCancellationRequested) break;
            }
            Status = _fatal ? $"已停止（{_abortReason}）" : ct.IsCancellationRequested ? "已停止" : "全部做完";
        }
        catch (OperationCanceledException)
        {
            Status = _fatal ? $"已停止（{_abortReason}）" : "已停止";
        }
        finally
        {
            _rig.Tripped -= OnTripped;
            foreach (var t in _tests.Where(t => t.State == RateState.Pending))
            {
                t.State = RateState.Skipped;
                t.Note = string.IsNullOrEmpty(t.Note) ? "没跑到" : t.Note;
            }
            if (!_emergency) await StopWellsAsync().ConfigureAwait(false);
            SafeSave();
            State = RateRunnerState.Finished;
            Current = null;
            Prompt = null;
            Phase = "";
            Log("info", $"升降温速率测试结束：{Status}；报告 {ReportPath}");
            Touch();
        }
    }

    /// <summary>第一项降温之前等人：冷却水开好、填水温。返回 false = 跳过了（降温都不做）；停止 / 急停抛出去。</summary>
    private async Task<bool> WaitWaterAsync(CancellationToken ct)
    {
        _waterGate = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        // 提示先立好、状态最后翻：界面看见「等水」时提示一定已经在了
        Prompt = "降温要用 TEC，TEC 要冷却水：把冷水机开起来、冷却水开好，把冷水机上看到的水温填进来，点「继续」。" +
                 "不做降温就点「跳过当前」";
        Status = "等冷却水（降温）";
        State = RateRunnerState.WaitingWater;
        Touch();
        using var reg = ct.Register(() => _waterGate.TrySetCanceled());
        try
        {
            _water = await _waterGate.Task.ConfigureAwait(false);
            Log("info", $"降温用冷却水，水温（手填）{_water:0.0} ℃");
            return true;
        }
        catch (OperationCanceledException)
        {
            if (ct.IsCancellationRequested || _fatal) throw;
            _abortReason = null;
            foreach (var t in _tests.Where(t => t.Kind == RateKind.Cool && t.State == RateState.Pending))
            {
                t.State = RateState.Skipped;
                t.Note = "冷却水那一步跳过了，降温都不做";
            }
            Log("warn", "降温跳过（等冷却水时点了「跳过当前」）");
            return false;
        }
        finally
        {
            _waterGate = null;
            State = RateRunnerState.Running;     // 先翻状态再清提示
            Prompt = null;
            Touch();
        }
    }

    // ── 一项 ────────────────────────────────────────────────────────

    private async Task RunTestAsync(RateTest test, CancellationToken ct)
    {
        Current = test;
        test.State = RateState.Running;
        test.StartedAt = _now();
        test.Water = test.Kind == RateKind.Cool ? _water : null;
        _abortReason = null;
        Status = test.Kind == RateKind.Reach
            ? $"{test.Title}：满功率升温，从当前温度开始，夹套到 {_s.ReachTargets.Max():0.#} ℃ 归零"
            : $"{test.Title}：满功率{RateKinds.Name(test.Kind)}，从当前温度开始";
        Phase = "准备";
        Touch();
        _cur = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cur.Token;
        var wells = new List<int>();
        var go = new List<Task>();
        CsvLog? csv = null;
        try
        {
            // ① 每一路先查能不能做：拒了的那一路写清原因，另一路照做
            foreach (var w in _s.Wells())
            {
                var tr = new RateTrace(w, test.Kind) { Limited = _limited[w] };
                test.Traces[w] = tr;
                tr.Refused = Precheck(w, test.Kind, _rig.Read(w));
                if (tr.Refused is { } why) Log("warn", $"{test.Title} 工位 {tr.WellName} 不做：{why}");
                else wells.Add(w);
            }
            if (wells.Count == 0)
                throw new RateAbort("两路都做不了（原因在汇总表备注里）", fatal: false);

            // ② 够不着的目标：输出一直顶在最大输出占空比上
            var target = TargetFor(test.Kind, wells);
            test.Target = target;
            csv = new CsvLog(Path.Combine(_outDir, "csv",
                $"{_startedAt:yyyyMMdd_HHmmss}_{test.Kind switch { RateKind.Heat => "heat", RateKind.Cool => "cool", _ => "reach" }}{test.Round}.csv"));
            var cmdAt = _now();
            foreach (var w in wells)
            {
                var tr = test.Traces[w]!;
                tr.CommandAt = cmdAt;
                go.Add(ConnectAsync(tr, target, token));
            }
            Log("info", $"{test.Title}：工位 {string.Join("、", wells.Select(w => w == 0 ? "A" : "B"))} 下令满功率（夹套目标 {target:0.#} ℃，够不着，输出顶在最大输出占空比上）");

            // ③ 每秒记；每一路到时归零、再记 M s
            await LoopAsync(test, wells, go, csv, token).ConfigureAwait(false);
            test.State = RateState.Done;
            test.Note = "";
            Log("info", $"{test.Title} 做完：" + string.Join("；", wells.Select(w => Brief(test, w))));
        }
        catch (RateAbort ex)
        {
            test.State = RateState.Aborted;
            test.Note = ex.Message;
            _fatal |= ex.Fatal;
            if (ex.Fatal) _abortReason ??= ex.Message;
            Log("error", $"{test.Title} 中止：{ex.Message}");
        }
        catch (OperationCanceledException)
        {
            var why = _abortReason ?? "操作人停止";
            test.State = why == "操作人跳过" ? RateState.Skipped : RateState.Aborted;
            test.Note = why;
            Log("warn", $"{test.Title} 中止：{why}");
        }
        catch (Exception ex)
        {
            // 驱动拒绝下发、设备报错：原话进报告，整轮停——同样的原因下一项多半也过不去
            test.State = RateState.Aborted;
            test.Note = ex.Message;
            _fatal = true;
            _abortReason ??= ex.Message;
            Log("error", $"{test.Title} 出错：{ex.Message}");
        }
        finally
        {
            csv?.Dispose();
            // 还在「下令」里的那一路（等冷却水 / 等继电器）先撤回来、等它退出，再归零——
            // 不然它在归零之后才返回，回路就带着够不着的目标又开起来了
            if (go.Any(g => !g.IsCompleted))
            {
                _cur.Cancel();
                try { await Task.WhenAll(go).WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false); }
                catch { /* 下令被撤回 / 报错：下面照样归零 */ }
            }
            // 没按时归零的路（中止 / 跳过）：这一刻记成归零时刻，输出马上归零
            var now = _now();
            foreach (var w in wells)
                if (test.Traces[w] is { } tr && tr.StopAt is null && tr.T0 is not null) tr.StopAt = now;
            if (test.State != RateState.Done && !_emergency) await StopWellsAsync().ConfigureAwait(false);
            _cur.Dispose();
            _cur = null;
            Phase = "";
            Touch();
        }
    }

    /// <summary>开始前查这一路能不能做；能做是 null，不能做是一句原因。</summary>
    private string? Precheck(int w, RateKind kind, WellReading r)
    {
        if (r.Tj is not { } tj) return "夹套没有读数";
        if (RateKinds.IsHeat(kind) && tj >= _s.StopLine)
            return $"夹套 {tj:0.0} ℃ 已到升温停止线 {_s.StopLine:0.#} ℃";
        if (RateKinds.IsHeat(kind) && _rig.Limits(w) is { } lim && tj >= lim.Max)
            return $"夹套 {tj:0.0} ℃ 已到温控器超温上限 {lim.Max:0.#} ℃";
        if (kind == RateKind.Reach && _s.ReachTargets.Max() is var top && tj >= top)
            return $"夹套 {tj:0.0} ℃ 已经不低于最高目标 {top:0.#} ℃，没得升——等夹套降下来再测";
        if (kind == RateKind.Reach && _rig.Limits(w) is { } lm && lm.Max <= _s.ReachTargets.Max())
            return $"温控器超温上限 {lm.Max:0.#} ℃ 不高于目标 {_s.ReachTargets.Max():0.#} ℃，满功率也到不了";
        if (kind == RateKind.Cool && _rig.Threshold is { } th && tj >= th)
            return $"夹套 {tj:0.0} ℃ 高于 TEC 接入上限 {th:0.#} ℃，TEC 接不上——等夹套降到 {th:0.#} ℃ 以下再测降温";
        if (kind == RateKind.Cool && _rig.Limits(w) is { } lo && tj <= lo.Min)
            return $"夹套 {tj:0.0} ℃ 已到温控器超温下限 {lo.Min:0.#} ℃";
        return null;
    }

    /// <summary>够不着的夹套目标：升温 = 停止线与各路超温上限里最小的；降温 = 各路超温下限里最大的（缺省 −40）。</summary>
    private double TargetFor(RateKind kind, List<int> wells)
    {
        if (RateKinds.IsHeat(kind))
        {
            var t = _s.StopLine;
            foreach (var w in wells)
                if (_rig.Limits(w) is { } lim) t = Math.Min(t, lim.Max);
            return t;
        }
        double? lo = null;
        foreach (var w in wells)
            if (_rig.Limits(w) is { } lim) lo = lo is { } x ? Math.Max(x, lim.Min) : lim.Min;
        return lo ?? -40;
    }

    /// <summary>下令这一路：下发返回（热源切好、冷却水通好、回路开了）那一刻就是 0 s。</summary>
    private async Task ConnectAsync(RateTrace tr, double target, CancellationToken token)
    {
        await _rig.SetTargetAsync(tr.Well, target, TempChannelKind.Jacket, token).ConfigureAwait(false);
        tr.T0 = _now();
        Log("info", $"工位 {tr.WellName} 接上满功率{(RateKinds.IsHeat(tr.Kind) ? "升温" : "降温")}（下令后 {(tr.T0.Value - tr.CommandAt!.Value).TotalSeconds:0.0} s）");
    }

    private async Task LoopAsync(RateTest test, List<int> wells, List<Task> go, CsvLog csv, CancellationToken token)
    {
        var reach = test.Kind == RateKind.Reach;
        var top = reach ? _s.ReachTargets.Max() : 0;
        // 满功率最长开多久：一分钟那两项 = N s 到时归零；升到目标温度 = 最长那么多分钟（到最高目标就提前归零）
        var full = reach ? TimeSpan.FromMinutes(_s.ReachMaxMinutes) : TimeSpan.FromSeconds(_s.FullSeconds);
        var after = TimeSpan.FromSeconds(_s.AfterSeconds);
        var loss = TimeSpan.FromSeconds(Math.Max(1, _s.ReadingLossSeconds));
        var lastGood = new DateTimeOffset[2];
        var cmdAt = test.Traces[wells[0]]!.CommandAt!.Value;
        foreach (var w in wells) lastGood[w] = cmdAt;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            // 下令那一下报错（驱动拒绝 / 设备报错）：原话抛出去
            foreach (var g in go.Where(g => g.IsFaulted))
                throw g.Exception!.GetBaseException();
            var now = _now();
            var reads = new WellReading[2];
            for (var w = 0; w < 2; w++) reads[w] = wells.Contains(w) ? _rig.Read(w) : WellReading.Empty;
            foreach (var w in wells)
            {
                var tr = test.Traces[w]!;
                if (!tr.Finished) tr.Points.Add(new RatePoint(now, reads[w]));
            }
            csv.Write(now, now - cmdAt, reads[0], reads[1], PhaseLine(test, wells, now));

            // 安全：读数断了 → 全停；夹套跑出设备范围 → 全停；升温到停止线 → 这一路马上归零
            foreach (var w in wells)
            {
                var tr = test.Traces[w]!;
                if (tr.Finished) continue;
                var r = reads[w];
                // 读不到：从最后一次读到算；读得到但采样时刻旧了：从采样时刻算（不再叠一段）
                if (r.Tj is null)
                {
                    if (now - lastGood[w] > loss)
                        throw new RateAbort($"工位 {tr.WellName} 夹套连着 {_s.ReadingLossSeconds} s 没有读数（链路断了？）{AliveNote()}", fatal: true);
                }
                else if (r.TjAt is { } at && now - at > loss)
                    throw new RateAbort($"工位 {tr.WellName} 夹套读数 {(now - at).TotalSeconds:0} s 没更新（温控器没回数？）{AliveNote()}", fatal: true);
                else lastGood[w] = now;
                if (r.Tj is { } tj && _rig.Limits(w) is { } lim && (tj < lim.Min - 2 || tj > lim.Max + 2))
                    throw new RateAbort($"工位 {tr.WellName} 夹套 {tj:0.0} ℃ 跑出设备范围 {lim.Min:0.#}~{lim.Max:0.#} ℃", fatal: true);
                if (RateKinds.IsHeat(test.Kind) && tr.StopAt is null && r.Tj is { } hot && hot >= _s.StopLine)
                {
                    // 还没接上就到了（下令那几秒）：不能先归零再让下令返回把它开回去——整项中止，finally 撤回下令再归零
                    if (tr.T0 is null)
                        throw new RateAbort($"工位 {tr.WellName} 还没接上满功率夹套就到了升温停止线 {_s.StopLine:0.#} ℃（{hot:0.0} ℃）", fatal: false);
                    tr.EarlyStop = $"夹套到升温停止线 {_s.StopLine:0.#} ℃（{hot:0.0} ℃），马上归零";
                    await StopWellAsync(tr, now, tr.EarlyStop).ConfigureAwait(false);
                }
            }

            // 时间线：接上满 N s 归零（升到目标温度：夹套到最高目标归零，满最长时间还没到也归零）；归零后再记 M s 这一路就完
            foreach (var w in wells)
            {
                var tr = test.Traces[w]!;
                if (tr.Finished) continue;
                if (tr.T0 is { } t0 && tr.StopAt is null)
                {
                    if (reach && reads[w].Tj is { } v && v >= top)
                    {
                        Log("info", $"{test.Title} 工位 {tr.WellName} 夹套到 {top:0.#} ℃（{v:0.0} ℃），输出归零");
                        await StopWellAsync(tr, now, null).ConfigureAwait(false);
                    }
                    else if (now - t0 >= full)
                    {
                        if (reach) tr.EarlyStop = $"满功率 {_s.ReachMaxMinutes} min 还没到 {top:0.#} ℃，归零";
                        await StopWellAsync(tr, now, tr.EarlyStop).ConfigureAwait(false);
                    }
                }
                if (tr.StopAt is { } sa && now - sa >= after)
                {
                    tr.Finished = true;
                    Log("info", $"{test.Title} 工位 {tr.WellName} 记完（归零后 {_s.AfterSeconds} s）");
                }
                if (tr.T0 is null && now - cmdAt > TimeSpan.FromSeconds(_s.ConnectMaxSeconds))
                    throw new RateAbort($"工位 {tr.WellName} 下令 {_s.ConnectMaxSeconds} s 还没接上满功率（等冷却水 / 等继电器？看日志）", fatal: false);
            }
            Phase = PhaseLine(test, wells, now);
            Touch();
            if (wells.All(w => test.Traces[w]!.Finished)) return;

            // 下一拍：1 s 一拍，快到某一路的归零时刻就提前醒（归零时刻差不了零点几秒；升到目标温度是满最长时间那一刻）
            var next = TimeSpan.FromSeconds(1);
            foreach (var w in wells)
                if (test.Traces[w] is { T0: { } t0, StopAt: null } && t0 + full - now is var left && left > TimeSpan.Zero && left < next)
                    next = left;
            if (next < TimeSpan.FromMilliseconds(50)) next = TimeSpan.FromMilliseconds(50);
            await _delay(next, token).ConfigureAwait(false);
        }
    }

    /// <summary>这一路归零（停控温）。StopAt 记在发令之前那一刻——输出从这一刻起不再是满的。</summary>
    private async Task StopWellAsync(RateTrace tr, DateTimeOffset now, string? why)
    {
        tr.StopAt = now;
        try { await _rig.StopAsync(tr.Well, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { throw new RateAbort($"工位 {tr.WellName} 归零失败：{ex.Message}", fatal: true); }
        var ran = tr.T0 is { } t0 ? (now - t0).TotalSeconds : 0;
        Log(why is null ? "info" : "warn", $"工位 {tr.WellName} 满功率 {ran:0.0} s，输出归零{(why is null ? "" : "：" + why)}");
    }

    private string AliveNote() => _rig.IsAlive(out var w) ? "" : $"；设备：{w}";

    /// <summary>「A 满功率 35 s +12.3 ℃ · B 归零后 12 s」这种一句话。</summary>
    private string PhaseLine(RateTest test, List<int> wells, DateTimeOffset now)
    {
        var parts = new List<string>();
        foreach (var w in wells)
        {
            var tr = test.Traces[w]!;
            string s;
            if (tr.T0 is not { } t0) s = "等接上";
            else if (tr.StopAt is null && test.Kind == RateKind.Reach)
            {
                // 「A 满功率 2 分 15 秒，夹套 79.0 ℃，到 60 ℃ 用了 1 分 27 秒」
                var series = RateMath.TjSeries(tr);
                var bits = new List<string> { $"满功率 {RateKinds.Duration((now - t0).TotalSeconds)}" };
                if (series.Count > 0) bits.Add($"夹套 {series[^1].V:0.0} ℃");
                if (RateMath.At(series, t0) is { } v0)
                    foreach (var g in _s.ReachTargets.Where(g => g > v0))
                        if (RateMath.Cross(series, t0, v0, g, 1, now) is { } sec)
                            bits.Add($"到 {g:0.#} ℃ 用了 {RateKinds.Duration(sec)}");
                s = string.Join("，", bits);
            }
            else if (tr.StopAt is not { } sa)
            {
                var series = RateMath.TjSeries(tr);
                var d = series.Count > 0 && RateMath.At(series, t0) is { } v0 ? series[^1].V - v0 : (double?)null;
                s = $"满功率 {(now - t0).TotalSeconds:0} s" +
                    (d is { } x ? " " + x.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + " ℃" : "");
            }
            else if (!tr.Finished) s = $"归零后 {(now - sa).TotalSeconds:0} s";
            else s = "记完";
            parts.Add($"{tr.WellName} {s}");
        }
        return string.Join(" · ", parts);
    }

    private string Brief(RateTest test, int w)
    {
        var m = RateMath.Compute(test.Traces[w]!, _s);
        if (m.Reach is { } items)
            return $"{(w == 0 ? "A" : "B")} 起点 {F(m.Start)} ℃，" +
                   string.Join("、", items.Where(i => i.Measured).Select(i => i.Seconds is { } x
                       ? $"到 {i.Target:0.#} ℃ 用 {x:0.0} s（平均 {i.PerMinute:0.0} ℃/min）"
                       : $"{i.Target:0.#} ℃ 没到")) +
                   $"，{m.Verdict}";
        var name = test.Kind == RateKind.Heat ? "升" : "降";
        return m.Change is { } c
            ? $"{(w == 0 ? "A" : "B")} {F(m.Start)} → {F(m.End)} ℃，{_s.FullSeconds} s {name}了 {c:0.0} ℃，{m.Verdict}"
            : $"{(w == 0 ? "A" : "B")} {m.Verdict}（{m.Note}）";
    }

    private static string F(double? v) => v is { } x ? x.ToString("0.0", CultureInfo.InvariantCulture) : "—";

    private async Task StopWellsAsync()
    {
        foreach (var w in _s.Wells())
        {
            try { await _rig.StopAsync(w, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { Log("error", $"停工位 {(w == 0 ? "A" : "B")} 失败：{ex.Message}"); }
        }
    }

    /// <summary>每项结束重写一遍报告。失败（Excel 里开着）不中止测试：数据都在内存里，下一次保存补上，连续失败只报第一次。</summary>
    private void SafeSave()
    {
        if (ReportPath is not { } path) return;
        try
        {
            RateReport.Write(path, Header(), _tests, _s);
            if (_saveFails > 0)
            {
                Log("info", $"报告保存恢复（失败 {_saveFails} 次后）");
                _saveFails = 0;
            }
        }
        catch (Exception ex)
        {
            if (_saveFails++ == 0)
                Log("error", $"报告保存失败：{ex.Message}——是不是在 Excel 里开着？关掉后下一项做完会补上（连续失败只报第一次）");
        }
    }

    private RateHeader Header() => new()
    {
        Date = _startedAt,
        Device = _rig.Describe,
        Operator = _s.Operator,
        Ambient = _s.Ambient,
        Water = _water,
        Material = _s.Material,
        Patch = _s.Patch,
        LimitedA = _limited[0],
        LimitedB = _limited[1],
        Threshold = _rig.Threshold
    };

    private async Task<double?> SafeRead(Func<Task<double?>> read, string what)
    {
        try { return await read().ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log("warn", $"{what} 没读到：{ex.Message}——报告里留空");
            return null;
        }
    }
}
