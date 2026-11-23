using Tec.Driver.Abi;

namespace Tec.Drivers.Simulator;

/// <summary>
/// 【本地改动】仿真反应器的「上位机 PID 整定台」（2026-11-23，演示模式）。
///
/// WHY：HMI 的「PID 整定」页按 <see cref="IPidTuningBench"/> 能力画，没有这个能力那一页只有一句
/// 「这一路没有上位机 PID 的整定能力」。演示要截这一页，仿真机就得有一台整定台：三张增益表
/// （TEC / 加热棒 / 双向挡）自带一组像现场整出来的行、手动参数、此刻在用的参数、每拍的 P/I/D，
/// 还有一次「假」自整定——起振几个周期之后出一组平稳型参数登记进表。
///
/// 它是**仿真机自己的整定表**：行里的 Ku / Tu 是按双态热模型算出来的量级，不冒充任何真机的实测；
/// 真机那张表在 Tec.Drivers.Rd105 的 Rd105HostPid 里，两边互不相通。表只活在内存里（仿真机没有数据目录），
/// TablePath 照实写「仿真 · 未落盘」。
/// </summary>
internal sealed class SimPidBench : IPidTuningBench
{
    private readonly ReactorWell _well;
    private readonly Func<DateTimeOffset> _now;
    private readonly Dictionary<PidActuator, List<PidGainRow>> _tables = new();
    private bool _scheduling = true;
    private PidManual _manual = new(new PidTuning(6.0, 0.012, 40), new PidTuning(1.6, 0.0009, 0), 12);

    // 回路「每拍」的量：由 ReactorWell.Tick 推进（仿真的 PID 项是从热模型的误差积出来的，不是另编一路）
    private double _i;
    private double _lastErr;
    private bool _hadErr;
    private PidLive? _live;

    // 假自整定：起振若干个周期（仿真秒）后出结果
    private CancellationTokenSource? _tune;
    private PidAutoTuneRequest? _tuneReq;
    private double _tuneElapsed;
    private const double TunePeriodSeconds = 48;   // 一个振荡周期（仿真秒），四个周期出结果
    private const int TuneCyclesNeeded = 4;

    public SimPidBench(ReactorWell well, Func<DateTimeOffset> now)
    {
        _well = well;
        _now = now;
        // 三张表的缺省行：按夹套温度分点（双向挡表照 BothTablePoints 的建议点取了四个），
        // 数值量级与现场 2026-11 整出来的那几组相近（Kp 几～十几、Ki 千分位、Kd 几十）
        _tables[PidActuator.Tec] = new List<PidGainRow>
        {
            Row(-10, 4.2, 0.0061, 28, 1.3, 0.0007, 9, 1.8, ku: 7.1, tu: 86),
            Row(10, 5.6, 0.0084, 33, 1.5, 0.0008, 10, 1.6, ku: 9.4, tu: 74),
            Row(25, 6.8, 0.0110, 36, 1.7, 0.0009, 12, 1.4, ku: 11.3, tu: 68, steady: 0.6),
        };
        _tables[PidActuator.Heater] = new List<PidGainRow>
        {
            Row(50, 9.5, 0.0146, 52, 1.9, 0.0011, 14, null, ku: 15.8, tu: 112, steady: 5.5),
            Row(80, 11.2, 0.0172, 58, 2.1, 0.0012, 16, null, ku: 18.7, tu: 104, steady: 8.9),
            Row(110, 12.6, 0.0190, 61, 2.3, 0.0013, 18, null, ku: 21.0, tu: 98),
        };
        _tables[PidActuator.Both] = new List<PidGainRow>
        {
            Row(10, 5.1, 0.0078, 31, 1.4, 0.0008, 10, 1.7, ku: 8.5, tu: 78),
            Row(25, 6.4, 0.0102, 35, 1.6, 0.0009, 12, 1.5, ku: 10.7, tu: 70, steady: 0.8),
            Row(50, 8.9, 0.0139, 49, 1.9, 0.0011, 14, 1.3, ku: 14.8, tu: 118, steady: 5.5),
            Row(80, 10.8, 0.0165, 56, 2.1, 0.0012, 16, 1.2, ku: 18.0, tu: 108, steady: 8.7),
        };
    }

    private static PidGainRow Row(double t, double kp, double ki, double kd, double okp, double oki, double okd,
                                  double? heat, double ku, double tu, double? steady = null)
        => new(t, kp, ki, kd)
        {
            OuterKp = okp, OuterKi = oki, OuterKd = okd, OuterMaxBiasC = 12, HeatRatio = heat,
            Ku = ku, TuSeconds = tu, SteadyBiasC = steady
        };

    public int Channel => _well.Channel;

    public string TablePath(PidActuator actuator) => $"仿真机 · {Name(actuator)}增益表（只在内存里，未落盘）";

    private static string Name(PidActuator a) => a switch
    {
        PidActuator.Tec => "TEC ",
        PidActuator.Heater => "加热棒",
        _ => "双向挡"
    };

    public IReadOnlyList<PidGainRow> Rows(PidActuator actuator)
    {
        lock (_tables) return _tables[actuator].OrderBy(r => r.TemperatureC).ToList();
    }

    public void ApplyRows(PidActuator actuator, IReadOnlyList<PidGainRow> rows)
    {
        var clean = new List<PidGainRow>();
        foreach (var r in rows.OrderBy(r => r.TemperatureC))
        {
            if (r.Kp < 0 || r.Ki < 0 || r.Kd < 0) throw new ArgumentException($"{r.TemperatureC:0.#} ℃ 那一行有负的增益");
            if ((r.OuterKp is null) != (r.OuterKi is null))
                throw new ArgumentException($"{r.TemperatureC:0.#} ℃ 那一行外环参数只填了一半");
            // 温度相差 2 ℃ 以内算同一个工作点，后面的覆盖前面的（与真机同一条规矩）
            var near = clean.FindIndex(x => Math.Abs(x.TemperatureC - r.TemperatureC) <= 2);
            if (near >= 0) clean[near] = r; else clean.Add(r);
        }
        lock (_tables) _tables[actuator] = clean;
        TablesChanged?.Invoke();
    }

    public void Reload() => TablesChanged?.Invoke();

    public bool Scheduling => _scheduling;
    public void SetScheduling(bool on) { _scheduling = on; TablesChanged?.Invoke(); }

    public PidManual Manual => _manual;

    public void SetManual(PidManual manual)
    {
        if (manual.Inner.Kp < 0 || manual.Inner.Ki < 0 || manual.Inner.Kd < 0) throw new ArgumentException("内环参数不能是负数");
        if (manual.OuterMaxBiasC <= 0 || manual.OuterMaxBiasC > 60) throw new ArgumentException("偏置上限要在 0～60 ℃ 之间");
        _manual = manual;
        TablesChanged?.Invoke();
    }

    /// <summary>执行器形态与热源切换一致：电加热侧 = 加热棒；TEC 侧且「TEC 加热」启用 = 双向挡，否则 TEC。</summary>
    public PidActuator CurrentActuator
        => _well.Electric ? PidActuator.Heater : _well.TecHeat ? PidActuator.Both : PidActuator.Tec;

    public PidInUse InUse => Preview(CurrentActuator, _well.Setpoint ?? _well.CurrentJacket) with
    {
        Bidirectional = CurrentActuator == PidActuator.Both
    };

    public PidInUse Preview(PidActuator actuator, double temperatureC)
    {
        var rows = Rows(actuator);
        if (!_scheduling || rows.Count == 0)
            return new PidInUse(temperatureC, actuator, _manual.Inner, false, _manual.Outer, _manual.OuterMaxBiasC, false, null, 1.4);

        // 两个整过的点之间按温度线性插值；端点之外取最近一行
        PidGainRow lo = rows[0], hi = rows[^1];
        foreach (var r in rows) { if (r.TemperatureC <= temperatureC) lo = r; }
        foreach (var r in rows.AsEnumerable().Reverse()) { if (r.TemperatureC >= temperatureC) hi = r; }
        var f = hi.TemperatureC > lo.TemperatureC
            ? Math.Clamp((temperatureC - lo.TemperatureC) / (hi.TemperatureC - lo.TemperatureC), 0, 1) : 0;
        double L(double a, double b) => a + (b - a) * f;
        double? Ln(double? a, double? b) => a is null || b is null ? a ?? b : a + (b - a) * f;

        var inner = new PidTuning(L(lo.Kp, hi.Kp), L(lo.Ki, hi.Ki), L(lo.Kd, hi.Kd));
        var outerOk = lo.OuterKp is not null && hi.OuterKp is not null;
        var outer = outerOk
            ? new PidTuning(L(lo.OuterKp!.Value, hi.OuterKp!.Value), L(lo.OuterKi ?? 0, hi.OuterKi ?? 0), L(lo.OuterKd ?? 0, hi.OuterKd ?? 0))
            : _manual.Outer;
        return new PidInUse(temperatureC, actuator, inner, true, outer,
                            Ln(lo.OuterMaxBiasC, hi.OuterMaxBiasC) ?? _manual.OuterMaxBiasC, outerOk,
                            Ln(lo.SteadyBiasC, hi.SteadyBiasC), Ln(lo.HeatRatio, hi.HeatRatio) ?? 1.4);
    }

    public PidLive? Live => _live;

    public PidActuator SuggestActuator(double setpointC)
        => setpointC > _well.Threshold ? PidActuator.Heater : _well.TecHeat ? PidActuator.Both : PidActuator.Tec;

    public string? CheckTune(PidAutoTuneRequest request)
    {
        if (request.SetpointC < _well.Limits.Min || request.SetpointC > _well.Limits.Max)
            return $"整定温度 {request.SetpointC:0.#} ℃ 超出设备范围 {_well.Limits.Min:0}～{_well.Limits.Max:0} ℃";
        if (request.Actuator == PidActuator.Heater && !_well.HasElectric) return "这台没配电加热切换模块，加热棒整不了";
        if (_tune is not null) return "已经在整定了";
        return null;
    }

    public bool Tuning => _tune is not null;
    public string TuneNote { get; private set; } = "";

    public Task StartAutoTuneAsync(PidAutoTuneRequest request, CancellationToken ct)
    {
        if (CheckTune(request) is { } why) throw new InvalidOperationException(why);
        _tune = new CancellationTokenSource();
        _tuneReq = request;
        _tuneElapsed = 0;
        TuneNote = $"在 {request.SetpointC:0.0} ℃ 附近起振，已完成 0 个周期（一般 {TuneCyclesNeeded} 个）";
        // 先把釜带到整定温度附近——振荡是围着它来的
        return _well.SetTargetAsync(new TempTarget(request.SetpointC, TempChannelKind.Jacket), ct);
    }

    public Task CancelAutoTuneAsync(CancellationToken ct)
    {
        if (_tune is null || _tuneReq is null) return Task.CompletedTask;
        var req = _tuneReq;
        _tune = null; _tuneReq = null;
        TuneNote = "";
        AutoTuneFinished?.Invoke(new PidAutoTuneReport(false, "已取消", req.SetpointC, req.Actuator));
        return _well.StopAsync(ct);
    }

    public event Action<PidAutoTuneReport>? AutoTuneFinished;
    public event Action? TablesChanged;

    /// <summary>每拍由 ReactorWell 调：按此刻的误差推 P/I/D，推进假自整定。</summary>
    internal void Tick(double dt, bool active, double setpoint, double measured, double duty, double innerSp)
    {
        var inUse = InUse;
        var err = setpoint - measured;
        if (active)
        {
            _i = Math.Clamp(_i + inUse.Inner.Ki * err * dt, -100, 100);
            var d = _hadErr && dt > 0 ? -inUse.Inner.Kd * (err - _lastErr) / dt : 0;
            _lastErr = err; _hadErr = true;
            _live = new PidLive(true, Tuning, TuneCycles(), setpoint, measured, duty,
                                Math.Round(inUse.Inner.Kp * err, 3), Math.Round(_i, 3), Math.Round(d, 3),
                                Cascade: true, innerSp, measured, CurrentActuator, _now());
        }
        else
        {
            _i = 0; _hadErr = false;
            _live = new PidLive(false, Tuning, 0, setpoint, measured, 0, 0, 0, 0, false, innerSp, measured, CurrentActuator, _now());
        }

        if (_tune is null || _tuneReq is null) return;
        _tuneElapsed += dt;
        var cycles = TuneCycles();
        TuneNote = $"在 {_tuneReq.SetpointC:0.0} ℃ 附近振荡，已完成 {cycles} 个周期（一般 {TuneCyclesNeeded} 个）";
        if (cycles < TuneCyclesNeeded) return;

        // 出结果：Ku 按继电幅值 / 振幅算（4d/πa），Tu 取振荡周期；平稳型 Tyreus–Luyben，快速型 Ziegler–Nichols
        var req = _tuneReq;
        var amp = 0.35;                                   // 仿真里的振荡幅度（℃）
        var ku = 4 * req.RelayAmplitudePercent / (Math.PI * amp);
        var tu = TunePeriodSeconds;
        var cons = new PidTuning(Math.Round(ku / 2.2, 3), Math.Round(ku / 2.2 / (2.2 * tu), 5), Math.Round(ku / 2.2 * tu / 6.3, 2));
        var fast = new PidTuning(Math.Round(0.6 * ku, 3), Math.Round(1.2 * ku / tu, 5), Math.Round(0.075 * ku * tu, 2));
        var row = new PidGainRow(req.SetpointC, cons.Kp, cons.Ki, cons.Kd)
        {
            OuterKp = Math.Round(cons.Kp / 4, 3), OuterKi = Math.Round(cons.Ki / 12, 6), OuterKd = Math.Round(cons.Kd / 4, 2),
            OuterMaxBiasC = 12, Ku = Math.Round(ku, 2), TuSeconds = tu,
            HeatRatio = req.Actuator == PidActuator.Heater ? null : 1.4
        };
        var rows = Rows(req.Actuator).ToList();
        var near = rows.FindIndex(x => Math.Abs(x.TemperatureC - row.TemperatureC) <= 2);
        if (near >= 0) rows[near] = row; else rows.Add(row);
        lock (_tables) _tables[req.Actuator] = rows.OrderBy(r => r.TemperatureC).ToList();

        _tune = null; _tuneReq = null;
        TuneNote = "";
        _well.StopAsync(CancellationToken.None);
        TablesChanged?.Invoke();
        AutoTuneFinished?.Invoke(new PidAutoTuneReport(true, null, req.SetpointC, req.Actuator)
        {
            Ku = row.Ku, TuSeconds = tu, OscillationC = amp, RelayAmplitudePercent = req.RelayAmplitudePercent,
            Conservative = cons, Fast = fast, Registered = true
        });
    }

    private int TuneCycles() => _tune is null ? 0 : (int)(_tuneElapsed / TunePeriodSeconds);
}
