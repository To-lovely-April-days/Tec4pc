using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Threading;
using Tec.LimitTest.Rate;

namespace Tec.LimitTest;

/// <summary>「升降温速率」那一页里的一行：一项测试（升温 / 降温第几次）的状态和两路的结果。</summary>
public sealed class RateRow : Bindable
{
    private string _title = "";
    private string _state = "";
    private string _detail = "";
    private IBrush _fill = Brushes.White;
    public string Title { get => _title; set => Set(ref _title, value); }
    public string StateText { get => _state; set => Set(ref _state, value); }
    public string Detail { get => _detail; set => Set(ref _detail, value); }
    public IBrush Fill { get => _fill; set => Set(ref _fill, value); }
}

/// <summary>
/// 【本地改动】（2026-11-17，用户要的升温 / 降温速率测试）主窗「升降温速率」那一页的状态。
/// 跟极限矩阵共用设备、操作人 / 环境温度 / 补丁版本 / 输出目录；两样不同时跑（串口和两路输出只有一套）。
/// 设置都是文本，开始时解析，解析不了当场说哪一项（同矩阵那页）。
/// </summary>
public sealed partial class MainViewModel
{
    private RateTestRunner? _rateRunner;
    private CancellationTokenSource? _rateStop;
    private Task? _rateRun;

    private bool _rateHeat = true;
    public bool RateHeat { get => _rateHeat; set => Set(ref _rateHeat, value); }
    private bool _rateCool = true;
    public bool RateCool { get => _rateCool; set => Set(ref _rateCool, value); }
    private bool _rateWellA = true;
    public bool RateWellA { get => _rateWellA; set => Set(ref _rateWellA, value); }
    private bool _rateWellB = true;
    public bool RateWellB { get => _rateWellB; set => Set(ref _rateWellB, value); }

    private string _rateHeatPass = "20";
    public string RateHeatPassText { get => _rateHeatPass; set => Set(ref _rateHeatPass, value); }
    private string _rateCoolPass = "20";
    public string RateCoolPassText { get => _rateCoolPass; set => Set(ref _rateCoolPass, value); }
    private string _rateFull = "60";
    public string RateFullText { get => _rateFull; set => Set(ref _rateFull, value); }
    private string _rateAfter = "60";
    public string RateAfterText { get => _rateAfter; set => Set(ref _rateAfter, value); }
    private string _rateStopLine = "190";
    public string RateStopLineText { get => _rateStopLine; set => Set(ref _rateStopLine, value); }
    private string _rateRounds = "1";
    public string RateRoundsText { get => _rateRounds; set => Set(ref _rateRounds, value); }
    private string _rateMaterial = "";
    public string RateMaterial { get => _rateMaterial; set => Set(ref _rateMaterial, value); }

    public bool IsRateRunning => _rateRun is { IsCompleted: false };
    public bool CanEditRate => !IsRateRunning;
    public bool CanStartRate => !_busy && !IsRunning && !IsRateRunning && _host.Rig is not null;
    public bool CanStopRate => IsRateRunning;
    public bool CanSkipRate => IsRateRunning && _rateRunner?.State is RateRunnerState.Running or RateRunnerState.WaitingWater;
    public bool IsRateWaitingWater => IsRateRunning && _rateRunner?.State == RateRunnerState.WaitingWater;

    private string _rateStatus = "未开始";
    public string RateStatusText { get => _rateStatus; set => Set(ref _rateStatus, value); }
    private string _ratePhase = "";
    public string RatePhaseText { get => _ratePhase; set => Set(ref _ratePhase, value); }
    private string _ratePrompt = "";
    public string RatePromptText { get => _ratePrompt; set => Set(ref _ratePrompt, value); }
    private string _rateWater = "";
    public string RateWaterText { get => _rateWater; set => Set(ref _rateWater, value); }
    private string _rateReport = "";
    public string RateReportText { get => _rateReport; set => Set(ref _rateReport, value); }

    public ObservableCollection<RateRow> RateRows { get; } = new();

    private RateSettings? ParseRate(out string? error)
    {
        error = null;
        double D(string text, string what, ref string? err)
        {
            if (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return v;
            err ??= $"「{what}」填的不是数：{text}";
            return 0;
        }
        var s = new RateSettings
        {
            Heat = RateHeat,
            Cool = RateCool,
            WellA = RateWellA,
            WellB = RateWellB,
            HeatPassK = D(RateHeatPassText, "合格线：升", ref error),
            CoolPassK = D(RateCoolPassText, "合格线：降", ref error),
            FullSeconds = (int)D(RateFullText, "满功率 s", ref error),
            AfterSeconds = (int)D(RateAfterText, "归零后再记 s", ref error),
            StopLine = D(RateStopLineText, "升温停止线", ref error),
            Rounds = (int)D(RateRoundsText, "测几次", ref error),
            Operator = OperatorName.Trim(),
            Patch = PatchText.Trim(),
            Material = RateMaterial.Trim()
        };
        if (!string.IsNullOrWhiteSpace(AmbientText)) s.Ambient = D(AmbientText, "环境温度", ref error);
        if (error is not null) return null;
        if (!s.Heat && !s.Cool) { error = "升温、降温一个都没勾"; return null; }
        if (!s.WellA && !s.WellB) { error = "工位 A、B 一个都没勾"; return null; }
        if (s.HeatPassK <= 0 || s.CoolPassK <= 0) { error = "合格线要大于 0"; return null; }
        if (s.FullSeconds < 10 || s.FullSeconds > 600) { error = "满功率时长填 10 ~ 600 s"; return null; }
        if (s.AfterSeconds < 0 || s.AfterSeconds > 600) { error = "归零后再记填 0 ~ 600 s"; return null; }
        if (s.Rounds < 1 || s.Rounds > 10) { error = "测几次填 1 ~ 10"; return null; }
        // 导热油夹套硬上限 200 ℃：停止线得在它下面（缺省 190，留 10 ℃ 给加热棒停了以后的余温上冲）
        if (s.StopLine >= 200) { error = "升温停止线得低于 200 ℃（导热油夹套的上限），缺省 190"; return null; }
        if (string.IsNullOrWhiteSpace(s.Operator)) { error = "操作人没填"; return null; }
        return s;
    }

    public void StartRate()
    {
        if (!CanStartRate) return;
        var s = ParseRate(out var error);
        if (s is null)
        {
            RateStatusText = "没开始：" + error;
            return;
        }
        var rig = _host.Rig!;
        var rt = _host.Runtime!;
        // 时钟用运行时那只（管线的采样时刻也是它打的）：「读数几秒没更新」拿两边同一只表比
        _rateRunner = new RateTestRunner(rig, s, OutDir.Trim(),
            now: rt.Clock.Func,
            delay: (t, ct) => Task.Delay(rt.Clock.RealDelay(t), ct),
            log: _host.Log);
        _rateRunner.Changed += () => Dispatcher.UIThread.Post(RefreshRate);
        _rateStop = new CancellationTokenSource();
        var token = _rateStop.Token;
        var runner = _rateRunner;
        RateRows.Clear();
        _rateRun = Task.Run(async () =>
        {
            try { await runner.RunAsync(token); }
            catch (Exception ex)
            {
                _host.Log("error", "升降温速率测试没跑起来：" + ex.Message);
                Dispatcher.UIThread.Post(() => RateStatusText = "没开始：" + ex.Message);
            }
        });
        _rateRun.ContinueWith(_ => Dispatcher.UIThread.Post(RefreshRate), TaskScheduler.Default);
        RefreshRate();
    }

    public void StopRate() => _rateStop?.Cancel();

    public void SkipRate() => _rateRunner?.SkipCurrent();

    public void ContinueRate()
    {
        if (_rateRunner is not { } r || !IsRateWaitingWater) return;
        if (!double.TryParse(RateWaterText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
        {
            RateStatusText = "冷却水温度填的不是数：" + RateWaterText;
            return;
        }
        r.ConfirmWater(v);
        RateWaterText = "";
    }

    /// <summary>把速率测试的状态搬到界面（界面线程上调）。</summary>
    private void RefreshRate()
    {
        if (_rateRunner is { } r)
        {
            RateStatusText = r.Status;
            RatePromptText = r.Prompt ?? "";
            RatePhaseText = r.Current is { } cur ? $"{cur.Title} · {r.Phase}" : "";
            RateReportText = r.ReportPath is { } p ? $"报告：{p}" : "";
            while (RateRows.Count < r.Tests.Count) RateRows.Add(new RateRow());
            for (var i = 0; i < r.Tests.Count; i++)
            {
                var t = r.Tests[i];
                var row = RateRows[i];
                row.Title = t.Title;
                row.StateText = t.StateText;
                row.Detail = Detail(t, r.Settings);
                row.Fill = t.State switch
                {
                    RateState.Running => new SolidColorBrush(Color.Parse("#E3F2FD")),
                    RateState.Done => new SolidColorBrush(Color.Parse("#E8F5E9")),
                    RateState.Aborted => new SolidColorBrush(Color.Parse("#FFEBEE")),
                    RateState.Skipped => new SolidColorBrush(Color.Parse("#F5F5F5")),
                    _ => Brushes.White
                };
            }
        }
        Raise(nameof(IsRateRunning));
        Raise(nameof(CanEditRate));
        Raise(nameof(CanStartRate));
        Raise(nameof(CanStopRate));
        Raise(nameof(CanSkipRate));
        Raise(nameof(IsRateWaitingWater));
        // 两页互锁：一页在跑，另一页的「开始」、连接、改矩阵都锁着
        Raise(nameof(CanStart));
        Raise(nameof(CanConnect));
        Raise(nameof(CanEditPlan));
    }

    /// <summary>一项测试两路的一句话结果（做完 / 中止才有数；进行中看上面那行阶段）。</summary>
    private static string Detail(RateTest t, RateSettings s)
    {
        if (t.State is RateState.Pending or RateState.Running) return t.State == RateState.Running ? "进行中…" : "";
        var parts = new List<string>();
        var verb = t.Kind == RateKind.Heat ? "升" : "降";
        for (var w = 0; w < 2; w++)
        {
            if (t.Traces[w] is not { } tr) continue;
            var m = RateMath.Compute(tr, s, t.State is RateState.Aborted or RateState.Skipped ? t.Note : null);
            parts.Add(m.Change is { } c && m.Start is { } a && m.End is { } b
                ? $"{tr.WellName}：{a:0.0} → {b:0.0} ℃，{m.RanSeconds:0} s {verb}了 {c:0.0} ℃ · {m.Verdict}"
                : $"{tr.WellName}：{m.Verdict}" + (string.IsNullOrEmpty(m.Note) ? "" : $"（{m.Note}）"));
        }
        if (t.State is RateState.Aborted or RateState.Skipped && !string.IsNullOrEmpty(t.Note))
            parts.Add($"{t.StateText}：{t.Note}");
        return string.Join("\n", parts);
    }
}
