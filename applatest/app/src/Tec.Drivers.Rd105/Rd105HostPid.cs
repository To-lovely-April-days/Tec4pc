using System.Globalization;
using System.Text.Json;
using TecControl.Core.Control;
using Tec.Driver.Abi;

namespace Tec.Drivers.Rd105;

/// <summary>
/// 组合会话（双工位主机）接进来的热源配合。单独的 RD105 没有继电器，这几项都不填。
/// </summary>
public sealed class Rd105PidHooks
{
    /// <summary>这个温度建议用哪个执行器整。</summary>
    public Func<double, PidActuator>? Suggest { get; init; }

    /// <summary>设备这一侧能不能整（没有电加热、夹套太烫接不回 TEC……）：null = 能。</summary>
    public Func<PidAutoTuneRequest, string?>? Check { get; init; }

    /// <summary>停这一路的控温（组合会话要连意图、跟随一起清）。</summary>
    public Func<CancellationToken, Task>? StopControl { get; init; }

    /// <summary>按执行器把继电器扳好、换执行器形态；返回 TEC 侧是不是只许制冷。</summary>
    public Func<PidAutoTuneRequest, CancellationToken, Task<bool>>? Prepare { get; init; }

    /// <summary>整定结束（任何原因）：继电器放回去。</summary>
    public Action? Ended { get; init; }
}

/// <summary>
/// 上位机 PID 的整定台（IPidTuningBench），每个工位一个，架在 HostControlLoop 上——
/// 增益表、手动参数、继电器法自整定全是那边现成的，这里只做翻译、校验、落盘和收尾。
///
/// 两张表按执行器分：TEC（&lt;实例&gt;-tc&lt;n&gt;.csv，跟 0325 起的那张是同一个文件）与加热棒
/// （&lt;实例&gt;-tc&lt;n&gt;-heater.csv），格式都是 TecControl.App 的 gain-schedule.csv。
/// 表内不再按 100 ℃ 分段（SegmentBoundaryC = NaN）：这台机器上加热棒不只在高温段用，
/// 段是按执行器分的，不是按温度。手动参数与这一路的调度开关存在 &lt;实例&gt;-tc&lt;n&gt;-pid.json。
/// </summary>
public sealed class Rd105HostPid : IPidTuningBench
{
    private readonly int _tc;
    private readonly HostControlLoop _loop;
    private readonly Rd105TemperatureControl _temp;
    private readonly string _instanceId;
    private readonly Action<string, string> _log;
    private readonly string _tecPath;
    private readonly string _heaterPath;
    private readonly string _paramsPath;

    private volatile PidLive? _live;
    private volatile bool _tuning;
    /// <summary>正在发起（停控温、扳继电器那一两秒）：回路里还没有整定器，别把它当「被停下」。</summary>
    private volatile bool _starting;
    private PidAutoTuneRequest? _tuneReq;
    private int _tuneCycles;

    internal Rd105HostPid(int channel, int tc, HostControlLoop loop, Rd105TemperatureControl temp,
                          string instanceId, Action<string, string> log)
    {
        Channel = channel;
        _tc = tc;
        _loop = loop;
        _temp = temp;
        _instanceId = instanceId;
        _log = log;
        _tecPath = Rd105HostControl.GainsPath(instanceId, tc);
        _heaterPath = Rd105HostControl.HeaterGainsPath(instanceId, tc);
        _paramsPath = Rd105HostControl.ParamsPath(instanceId, tc);
        foreach (var a in new[] { PidActuator.Tec, PidActuator.Heater })
            Schedule(a).SegmentBoundaryC = double.NaN;      // 段按执行器分，表内全温域插值
        _loop.AutoTuneFinished += OnAutoTuneFinished;
    }

    public int Channel { get; }

    /// <summary>组合会话接进来的热源配合；单独的 RD105 不填。</summary>
    public Rd105PidHooks Hooks { get; set; } = new();

    private string Who => $"{_instanceId} TC{_tc}";

    private static ActuatorMode Mode(PidActuator a) => a == PidActuator.Heater ? ActuatorMode.HeatOnly : ActuatorMode.Bidirectional;

    private static PidActuator Of(ActuatorMode m) => m == ActuatorMode.HeatOnly ? PidActuator.Heater : PidActuator.Tec;

    public static string ActuatorName(PidActuator a) => a == PidActuator.Heater ? "加热棒" : "TEC";

    private PidGainSchedule Schedule(PidActuator a) => _loop.GetGainSchedule(_tc, Mode(a));

    // ── 落盘 ─────────────────────────────────────────────────────────

    public string TablePath(PidActuator actuator) => actuator == PidActuator.Heater ? _heaterPath : _tecPath;

    /// <summary>开会话时读一次：两张表 + 手动参数 / 调度开关。返回给日志的一句话。</summary>
    internal string Load()
    {
        var nTec = Rd105HostControl.Load(Schedule(PidActuator.Tec), _tecPath);
        var nHeat = Rd105HostControl.Load(Schedule(PidActuator.Heater), _heaterPath);
        var p = LoadParams();
        return $"TEC 表 {Describe(PidActuator.Tec)}、加热棒表 {Describe(PidActuator.Heater)}" +
               (nTec + nHeat == 0 ? "——表都空着，先用手动参数，请在常用温度点自整定" : "") +
               $"；手动内环 {Fmt(Manual.Inner)}{(p ? "（上次存的）" : "（缺省）")}；增益调度{(Scheduling ? "开" : "关")}";
    }

    private string Describe(PidActuator a)
    {
        var rows = Rows(a);
        return rows.Count == 0
            ? "空"
            : string.Join("、", rows.Select(r => r.TemperatureC.ToString("0.#", CultureInfo.InvariantCulture) + " ℃"));
    }

    /// <summary>回路登记 / 学到东西之后存两张表（会话收到 GainScheduleChanged 时调）。</summary>
    internal void SaveTables(bool quiet = false)
    {
        foreach (var a in new[] { PidActuator.Tec, PidActuator.Heater })
        {
            var path = TablePath(a);
            var sched = Schedule(a);
            // 从没建过、现在也空着的那张不落空文件
            if (sched.Count == 0 && !File.Exists(path)) continue;
            try { Rd105HostControl.Save(sched, path); }
            catch (Exception ex) { _log("error", $"{Who} {ActuatorName(a)}增益表保存失败：{ex.Message}（{path}）"); }
        }
        if (!quiet) _log("info", $"{Who} 增益表已保存：TEC {Describe(PidActuator.Tec)}；加热棒 {Describe(PidActuator.Heater)}");
        TablesChanged?.Invoke();
    }

    private sealed class ParamsDoc
    {
        public bool Scheduling { get; set; } = true;
        public double[]? Inner { get; set; }
        public double[]? Outer { get; set; }
        public double OuterMaxBiasC { get; set; }
    }

    private bool LoadParams()
    {
        try
        {
            if (!File.Exists(_paramsPath)) return false;
            var doc = JsonSerializer.Deserialize<ParamsDoc>(File.ReadAllText(_paramsPath));
            if (doc is null) return false;
            _loop.SetGainScheduling(_tc, doc.Scheduling);
            if (doc.Inner is { Length: 3 } i && i.All(ok))
                _loop.SetManualGains(_tc, i[0], i[1], i[2]);
            if (doc.Outer is { Length: 3 } o && o.All(ok) && doc.OuterMaxBiasC > 0)
                _loop.ConfigureCascade(_tc, o[0], o[1], o[2], doc.OuterMaxBiasC);
            return true;
        }
        catch (Exception ex)
        {
            _log("warn", $"{Who} 手动 PID 参数读不出来（{_paramsPath}）：{ex.Message}——用缺省");
            return false;
        }

        static bool ok(double v) => double.IsFinite(v) && v >= 0;
    }

    private void SaveParams()
    {
        var m = Manual;
        var doc = new ParamsDoc
        {
            Scheduling = Scheduling,
            Inner = new[] { m.Inner.Kp, m.Inner.Ki, m.Inner.Kd },
            Outer = new[] { m.Outer.Kp, m.Outer.Ki, m.Outer.Kd },
            OuterMaxBiasC = m.OuterMaxBiasC
        };
        Directory.CreateDirectory(Path.GetDirectoryName(_paramsPath)!);
        File.WriteAllText(_paramsPath, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
    }

    // ── 表 ───────────────────────────────────────────────────────────

    public IReadOnlyList<PidGainRow> Rows(PidActuator actuator)
        => Schedule(actuator).Points.OrderBy(p => p.TemperatureC).Select(ToRow).ToArray();

    private static PidGainRow ToRow(GainPoint p) => new(p.TemperatureC, p.Gains.Kp, p.Gains.Ki, p.Gains.Kd)
    {
        OuterKp = p.OuterGains?.Kp,
        OuterKi = p.OuterGains?.Ki,
        OuterKd = p.OuterGains?.Kd,
        OuterMaxBiasC = p.OuterGains is null || p.OuterMaxBiasC <= 0 ? null : p.OuterMaxBiasC,
        HeatRatio = p.HeatRatio,
        Ku = p.UltimateGainKu,
        TuSeconds = p.UltimatePeriodTuSeconds,
        SteadyBiasC = p.SteadyBiasC
    };

    public void ApplyRows(PidActuator actuator, IReadOnlyList<PidGainRow> rows)
    {
        // 先全部校验、全部换算，一行不对整张不动
        var points = new List<GainPoint>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
            points.Add(ToPoint(rows[i], i + 1, actuator));

        var sched = Schedule(actuator);
        sched.Clear();
        foreach (var p in points) sched.Learn(p);          // 2 ℃ 以内合并：后面的覆盖前面的
        SaveTables(quiet: true);
        _log("info", $"{Who} {ActuatorName(actuator)}增益表已应用（{Schedule(actuator).Count} 个温度点：{Describe(actuator)}）");
    }

    private static GainPoint ToPoint(PidGainRow r, int line, PidActuator actuator)
    {
        string At(string what) => $"第 {line} 行{what}";
        if (!double.IsFinite(r.TemperatureC) || r.TemperatureC < -80 || r.TemperatureC > 300)
            throw new ArgumentException(At($"温度 {r.TemperatureC} 不在 −80 ~ 300 ℃"));
        foreach (var (v, n) in new[] { (r.Kp, "Kp"), (r.Ki, "Ki"), (r.Kd, "Kd") })
            if (!double.IsFinite(v) || v < 0) throw new ArgumentException(At($"内环 {n} = {v} 不对（要 ≥ 0 的数）"));
        if (r.Kp <= 0 && r.Ki <= 0) throw new ArgumentException(At("内环 Kp、Ki 都是 0——这一行等于不控"));

        PidGains? outer = null;
        var bias = 0.0;
        var anyOuter = r.OuterKp is not null || r.OuterKi is not null || r.OuterKd is not null || r.OuterMaxBiasC is not null;
        if (anyOuter)
        {
            if (r.OuterKp is not { } okp || r.OuterKi is not { } oki || r.OuterMaxBiasC is not { } ob)
                throw new ArgumentException(At("外环只填了一半：外环 Kp、Ki、偏置上限要一起填（Kd 可空 = 0）；不用串级就三格都空着"));
            var okd = r.OuterKd ?? 0;
            foreach (var (v, n) in new[] { (okp, "外环 Kp"), (oki, "外环 Ki"), (okd, "外环 Kd") })
                if (!double.IsFinite(v) || v < 0) throw new ArgumentException(At($"{n} = {v} 不对（要 ≥ 0 的数）"));
            if (!double.IsFinite(ob) || ob <= 0 || ob > 60) throw new ArgumentException(At($"偏置上限 {ob} 不在 0 ~ 60 ℃"));
            outer = new PidGains(okp, oki, okd);
            bias = ob;
        }
        double? heat = null;
        if (r.HeatRatio is { } hr && actuator == PidActuator.Tec)
        {
            if (!double.IsFinite(hr) || hr <= 0.05 || hr > 20) throw new ArgumentException(At($"加热比 {hr} 不在 0.05 ~ 20"));
            heat = hr;
        }
        return new GainPoint(r.TemperatureC, new PidGains(r.Kp, r.Ki, r.Kd), Math.Max(0, r.Ku), Math.Max(0, r.TuSeconds),
                             outer, bias, r.SteadyBiasC, heat);
    }

    public void Reload()
    {
        foreach (var a in new[] { PidActuator.Tec, PidActuator.Heater })
        {
            var sched = Schedule(a);
            sched.Clear();
            Rd105HostControl.Load(sched, TablePath(a));
        }
        LoadParams();
        _log("info", $"{Who} 增益表与手动参数已从盘上重读：TEC {Describe(PidActuator.Tec)}；加热棒 {Describe(PidActuator.Heater)}");
        TablesChanged?.Invoke();
    }

    // ── 开关与手动参数 ───────────────────────────────────────────────

    public bool Scheduling => _loop.IsGainScheduling(_tc);

    public void SetScheduling(bool on)
    {
        _loop.SetGainScheduling(_tc, on);
        SaveParams();
        _log("info", $"{Who} 增益调度{(on ? "打开：按设定值在表里插值" : "关闭：固定用手动参数")}");
    }

    public PidManual Manual
    {
        get
        {
            var inner = _loop.GetManualGains(_tc) ?? Rd105HostControl.FallbackInner;
            var (outer, bias) = _loop.GetManualOuter(_tc);
            var o = outer ?? Rd105HostControl.FallbackOuter;
            return new PidManual(new PidTuning(inner.Kp, inner.Ki, inner.Kd), new PidTuning(o.Kp, o.Ki, o.Kd),
                                 outer is null ? Rd105HostControl.FallbackOuterMaxBiasC : bias);
        }
    }

    public void SetManual(PidManual manual)
    {
        foreach (var (v, n) in new[]
                 {
                     (manual.Inner.Kp, "内环 Kp"), (manual.Inner.Ki, "内环 Ki"), (manual.Inner.Kd, "内环 Kd"),
                     (manual.Outer.Kp, "外环 Kp"), (manual.Outer.Ki, "外环 Ki"), (manual.Outer.Kd, "外环 Kd")
                 })
            if (!double.IsFinite(v) || v < 0) throw new ArgumentException($"{n} = {v} 不对（要 ≥ 0 的数）");
        if (manual.Inner.Kp <= 0 && manual.Inner.Ki <= 0) throw new ArgumentException("内环 Kp、Ki 都是 0——等于不控");
        if (!double.IsFinite(manual.OuterMaxBiasC) || manual.OuterMaxBiasC <= 0 || manual.OuterMaxBiasC > 60)
            throw new ArgumentException($"偏置上限 {manual.OuterMaxBiasC} 不在 0 ~ 60 ℃");
        _loop.SetManualGains(_tc, manual.Inner.Kp, manual.Inner.Ki, manual.Inner.Kd);
        _loop.ConfigureCascade(_tc, manual.Outer.Kp, manual.Outer.Ki, manual.Outer.Kd, manual.OuterMaxBiasC);
        SaveParams();
        _log("info", $"{Who} 手动 PID 参数已改：内环 {Fmt(manual.Inner)}；外环 {Fmt(manual.Outer)}、偏置上限 ±{manual.OuterMaxBiasC:0.#} ℃");
    }

    // ── 在用参数与实时 ─────────────────────────────────────────────

    public PidActuator CurrentActuator => Of(_loop.GetActuatorMode(_tc));

    public PidLive? Live => _live;

    public PidInUse InUse
    {
        get
        {
            var live = _live;
            var sp = _loop.GetChannelStatus(_tc).SetpointC;
            var inner = live is { Cascade: true } l && double.IsFinite(l.InnerSetpointC) ? l.InnerSetpointC : sp;
            return Compose(CurrentActuator, inner, sp);
        }
    }

    public PidInUse Preview(PidActuator actuator, double temperatureC) => Compose(actuator, temperatureC, temperatureC);

    /// <summary>照回路的规矩算：内环按内环设定查、外环 / 稳态偏置按主设定查；表里没有或调度关了就是手动那组。</summary>
    private PidInUse Compose(PidActuator a, double innerAt, double outerAt)
    {
        var sched = Schedule(a);
        var on = Scheduling;
        var m = Manual;
        var g = on ? sched.GainsAt(innerAt) : null;
        var o = on ? sched.OuterAt(outerAt) : null;
        var heat = a == PidActuator.Heater ? 1.0 : (on ? sched.HeatRatioAt(innerAt) : null) ?? _loop.HeatingEffectivenessRatio;
        return new PidInUse(
            innerAt, a,
            g is null ? m.Inner : new PidTuning(g.Kp, g.Ki, g.Kd), g is not null,
            o is null ? m.Outer : new PidTuning(o.Gains.Kp, o.Gains.Ki, o.Gains.Kd),
            o?.MaxBiasC ?? m.OuterMaxBiasC, o is not null,
            sched.SteadyBiasAt(outerAt), heat);
    }

    /// <summary>会话在回路每拍回调里喂进来。</summary>
    internal void OnCycle(ChannelCycleInfo info)
    {
        _live = new PidLive(info.Active, info.Tuning, info.TuneCycles, info.SetpointC, info.MeasuredC, info.DutyPercent,
                            info.PTerm, info.ITerm, info.DTerm, info.Cascade, info.InnerSetpointC, info.OuterMeasuredC,
                            CurrentActuator, DateTimeOffset.Now);
        if (!_tuning) return;
        _tuneCycles = info.TuneCycles;
        // 整定被别处停下了（停控温 / 安全停机 / 读数丢失停控）：正常结束会先发 AutoTuneFinished（那时 _tuning 已落），
        // 走到这里说明回路里已经没有整定器了，却没人来报结束
        if (!info.Tuning && !_loop.IsTuning(_tc))
            Finish(new PidAutoTuneReport(false, "整定被停下（停了控温 / 安全停机 / 回路停控）",
                                         _tuneReq?.SetpointC ?? double.NaN, _tuneReq?.Actuator ?? PidActuator.Tec)
                   { RelayAmplitudePercent = _tuneReq?.RelayAmplitudePercent });
    }

    // ── 自整定 ─────────────────────────────────────────────────────

    public bool Tuning => _tuning;

    public string TuneNote
    {
        get
        {
            if (!_tuning || _tuneReq is not { } r) return "";
            var head = $"{ActuatorName(r.Actuator)}，{r.SetpointC:0.0} ℃，幅值 {r.RelayAmplitudePercent:0} %、回差 {r.HysteresisC:0.###} ℃";
            return _tuneCycles <= 0
                ? $"自整定中（{head}）：把温度推到设定值附近，等它来回穿越…"
                : $"自整定中（{head}）：已完成 {_tuneCycles} 个振荡周期（一般要 4 个）";
        }
    }

    public PidActuator SuggestActuator(double setpointC) => Hooks.Suggest?.Invoke(setpointC) ?? PidActuator.Tec;

    public string? CheckTune(PidAutoTuneRequest r)
    {
        if (_tuning || _starting) return "这一路已经在自整定了，先取消再重来";
        if (!double.IsFinite(r.SetpointC)) return "整定温度不是数";
        var lim = _temp.Limits;
        if (r.SetpointC < lim.Min || r.SetpointC > lim.Max)
            return $"整定温度 {r.SetpointC:0.#} ℃ 超出设备保护范围 {lim.Min:0.#} ~ {lim.Max:0.#} ℃";
        if (!double.IsFinite(r.RelayAmplitudePercent) || r.RelayAmplitudePercent < 5 || r.RelayAmplitudePercent > 100)
            return $"继电幅值 {r.RelayAmplitudePercent} % 不在 5 ~ 100 %";
        if (!double.IsFinite(r.HysteresisC) || r.HysteresisC < 0.01 || r.HysteresisC > 5)
            return $"回差 {r.HysteresisC} ℃ 不在 0.01 ~ 5 ℃";
        if (double.IsNaN(_temp.CurrentJacket)) return "夹套温度没有读数——自整定靠它判穿越，读不到整不了";
        if (r.Actuator == PidActuator.Heater && Hooks.Prepare is null)
            return "这台温控器单独使用、没有加热棒切换——只能用 TEC 整";
        return Hooks.Check?.Invoke(r);
    }

    public async Task StartAutoTuneAsync(PidAutoTuneRequest r, CancellationToken ct)
    {
        if (CheckTune(r) is { } why) throw new InvalidOperationException(why);
        _starting = true;
        var coolOnly = false;
        var prepared = false;
        try
        {
            // ① 这一路在控温就先停（面板已经问过人）
            if (_temp.Enabled || _loop.GetChannelStatus(_tc).Active)
            {
                if (Hooks.StopControl is { } stop) await stop(ct).ConfigureAwait(false);
                else await _temp.StopAsync(ct).ConfigureAwait(false);
                _log("info", $"{Who} 自整定前先停了这一路的控温");
            }

            // ② 热源：组合会话按执行器扳继电器、换执行器形态（它自己先立旗，采集循环就不去换挡 / 断开）；
            //    单独的 RD105 就是 TEC 双向
            _tuneReq = r;
            _tuneCycles = 0;
            prepared = true;
            if (Hooks.Prepare is { } prep) coolOnly = await prep(r, ct).ConfigureAwait(false);
            else _loop.SetActuatorMode(_tc, ActuatorMode.Bidirectional);

            // ③ 回路：单环（整的是夹套内环）、解除挂起（切换序列关输出时挂起了）、进继电器振荡
            _loop.StopProfile(_tc);
            _loop.SetStrategy(_tc, ControlStrategy.Direct);
            _loop.SuspendOutput(_tc, false);
            if (!_loop.IsRunning) _loop.Start();
            await _loop.StartAutoTuneAsync(_tc, r.SetpointC, r.RelayAmplitudePercent, r.HysteresisC, coolOnly, ct)
                       .ConfigureAwait(false);
            _tuning = true;       // 回路里有整定器了，从这一拍起「没有整定器」才算被停下
        }
        catch
        {
            _tuneReq = null;
            if (prepared) { try { Hooks.Ended?.Invoke(); } catch { } }
            throw;
        }
        finally
        {
            _starting = false;
        }
        _log("warn", $"{Who} 自整定开始：{ActuatorName(r.Actuator)}{(coolOnly ? "（只制冷）" : "")}，{r.SetpointC:0.0} ℃，" +
                     $"继电幅值 {r.RelayAmplitudePercent:0} %、回差 {r.HysteresisC:0.###} ℃——会在设定值附近激起振荡，要有人在场");
    }

    public async Task CancelAutoTuneAsync(CancellationToken ct)
    {
        if (!_tuning) return;
        var r = _tuneReq;
        await _loop.StopChannelAsync(_tc, ct).ConfigureAwait(false);
        Finish(new PidAutoTuneReport(false, "操作人取消", r?.SetpointC ?? double.NaN, r?.Actuator ?? PidActuator.Tec)
               { RelayAmplitudePercent = r?.RelayAmplitudePercent });
    }

    private void OnAutoTuneFinished(AutoTuneOutcome o)
    {
        if (o.Channel != _tc || !_tuning) return;
        var r = _tuneReq;
        var res = o.Result;
        Finish(new PidAutoTuneReport(o.Success, o.Success ? null : o.Reason ?? "自整定失败",
                                     r?.SetpointC ?? double.NaN, r?.Actuator ?? CurrentActuator)
        {
            Ku = res?.UltimateGainKu,
            TuSeconds = res?.UltimatePeriodTuSeconds,
            OscillationC = res?.OscillationAmplitudeC,
            RelayAmplitudePercent = res?.RelayAmplitudePercent ?? r?.RelayAmplitudePercent,
            Conservative = res?.Conservative is { } c ? new PidTuning(c.Kp, c.Ki, c.Kd) : null,
            Fast = res?.Fast is { } f ? new PidTuning(f.Kp, f.Ki, f.Kd) : null,
            Registered = o.Success && res is not null
        });
    }

    private readonly object _finishGate = new();

    private void Finish(PidAutoTuneReport report)
    {
        lock (_finishGate)
        {
            if (!_tuning) return;
            _tuning = false;
            _tuneReq = null;
        }
        try { Hooks.Ended?.Invoke(); } catch { }
        _log(report.Success ? "info" : "warn", $"{Who} " + (report.Success
            ? $"自整定完成：{ActuatorName(report.Actuator)} {report.SetpointC:0.0} ℃，Ku {report.Ku:0.###}、Tu {report.TuSeconds:0.#} s；" +
              $"平稳型 {Fmt(report.Conservative!)} 已登记进{ActuatorName(report.Actuator)}增益表（快速型 {Fmt(report.Fast!)} 只供参考）"
            : $"自整定没成：{report.Reason}"));
        AutoTuneFinished?.Invoke(report);
    }

    public event Action<PidAutoTuneReport>? AutoTuneFinished;

    public event Action? TablesChanged;

    internal void Detach() => _loop.AutoTuneFinished -= OnAutoTuneFinished;

    internal static string Fmt(PidTuning g)
        => $"Kp {g.Kp.ToString("0.###", CultureInfo.InvariantCulture)} / Ki {g.Ki.ToString("0.#####", CultureInfo.InvariantCulture)} / Kd {g.Kd.ToString("0.##", CultureInfo.InvariantCulture)}";

    private static string Fmt(PidGains g) => Fmt(new PidTuning(g.Kp, g.Ki, g.Kd));
}
