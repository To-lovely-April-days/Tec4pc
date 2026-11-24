using System.Text.Json.Nodes;
using Tec.Driver.Abi;

namespace Tec.Drivers.TouchPanel;

/// <summary>
/// 一台触摸屏主机的会话。干三件事：
///   1. 每半秒向触摸屏要一次整机状态（state），把读数转成采样推给上面（Tr / Tj / 设定 / 出力 / pH / 转速 / 已加料量）；
///   2. 两个工位各给出一组能力对象，能力对象的方法就是往触摸屏发对应的命令；
///   3. 认领配方里的温控、搅拌、加料步骤。
///
/// 控温、热源切换、冷却水、安全联锁都是触摸屏自己在做，这里一概不碰、也不重做。
/// 触摸屏不答应的命令（它正在跑自己的序列、关了远程控制、设备没连上）原话抛给上面，界面会显示出来。
///
/// 线断了：读数全部按 Bad 推（值给 NaN），会话进 Faulted；下一拍轮询通了自己恢复。
/// 断线期间触摸屏照常控温——工作站断开不影响它。
/// </summary>
internal sealed class PanelSession : IDeviceSession
{
    private readonly PanelClient _client;
    private readonly DriverContext _ctx;
    private readonly Broadcast<Sample> _out = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly PanelWell[] _wells;
    private Task? _pump;
    private DeviceState _state = DeviceState.Connected;
    private bool _linkDown;

    public PanelSession(PanelClient client, DriverContext ctx)
    {
        _client = client;
        _ctx = ctx;
        InstanceId = ctx.InstanceId;
        _wells = new PanelWell[2];
        for (var w = 0; w < 2; w++)
            _wells[w] = new PanelWell(this, w, w < ctx.ChannelNumbers.Count ? ctx.ChannelNumbers[w] : w + 1);
    }

    internal PanelClient Client => _client;
    internal DriverContext Context => _ctx;
    internal DateTimeOffset Now => _ctx.Clock();
    internal double Scale => _ctx.TimeScale <= 0 ? 1 : _ctx.TimeScale;
    /// <summary>触摸屏说「控温设备没连上」时的那句话；连着就是空的。</summary>
    internal string Offline { get; private set; } = "";
    internal bool LinkDown => _linkDown;
    internal PanelWell Well(int well) => _wells[well];

    public string InstanceId { get; }
    public IObservable<Sample> Samples => _out;
    public int WellCount => 2;

    public DeviceState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            StateChanged?.Invoke(this, value);
        }
    }

    public event EventHandler<DeviceState>? StateChanged;

    public IReadOnlyList<TagDescriptor> Tags { get; } = new[]
    {
        new TagDescriptor(TouchPanelDriver.TagTr, "釜内温度", "℃", DataShape.Scalar),
        new TagDescriptor(TouchPanelDriver.TagTj, "夹套温度", "℃", DataShape.Scalar),
        new TagDescriptor(TouchPanelDriver.TagDt, "Tr−Tj 温差", "℃", DataShape.Scalar) { DerivedFrom = "Tr,Tj" },
        new TagDescriptor(TouchPanelDriver.TagTset, "设定温度", "℃", DataShape.Scalar),
        new TagDescriptor(TouchPanelDriver.TagDuty, "控温输出", "%", DataShape.Scalar),
        new TagDescriptor(TouchPanelDriver.TagTjSet, "夹套设定（串级）", "℃", DataShape.Scalar),
        new TagDescriptor(TouchPanelDriver.TagPh, "pH", "", DataShape.Scalar),
        new TagDescriptor(TouchPanelDriver.TagRpm, "搅拌转速", "rpm", DataShape.Scalar) { Nominal = new ValueRange(0, 1000) },
        new TagDescriptor(TouchPanelDriver.TagVol, "累计加料", "mL", DataShape.Scalar)
    };

    public IReadOnlyList<ICapability> CapabilitiesOf(int well)
        => well is >= 0 and < 2 ? _wells[well].Capabilities : Array.Empty<ICapability>();

    public ICommandHandler? Resolve(string commandId) => commandId switch
    {
        CommandSpecs.Stir => new PanelStirHandler(),
        CommandSpecs.Dose => new PanelDoseHandler(this),
        _ => CapabilityCommands.Resolve(commandId)      // 温控那几条走契约里通用的执行器，它只按 ITemperatureControl 说话
    };

    /// <summary>开会话时先通一次：读限值、读一拍状态。连不上直接抛，界面上这台设备显示「未连接」和原因。</summary>
    public async Task InitAsync(CancellationToken ct)
    {
        for (var w = 0; w < 2; w++)
        {
            var lim = await _client.AskAsync(new JsonObject { ["cmd"] = "limits.get", ["ch"] = w + 1 }, ct).ConfigureAwait(false);
            _wells[w].ApplyLimits(lim["limits"] as JsonObject);
        }
        await PollOnceAsync(ct).ConfigureAwait(false);
    }

    public Task StartAsync(CancellationToken ct)
    {
        if (_pump is not null) return Task.CompletedTask;
        State = DeviceState.Ready;
        _pump = Task.Run(() => PumpAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _cts.Cancel();
        if (_pump is not null)
        {
            try { await _pump.ConfigureAwait(false); } catch { }
            _pump = null;
        }
        State = DeviceState.Connected;
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await PollOnceAsync(ct).ConfigureAwait(false);
            try { await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>要一拍状态并推采样。连不上不抛：把读数标成 Bad、会话进 Faulted，等下一拍。</summary>
    internal async Task PollOnceAsync(CancellationToken ct)
    {
        JsonObject state;
        try
        {
            state = await _client.AskAsync("state", ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) when (ex is IOException or PanelRefusedException)
        {
            if (!_linkDown) _ctx.Log?.Invoke("error", $"{InstanceId} 和触摸屏的连接断了：{ex.Message}（触摸屏那边照常控温，这里先收不到数据）");
            _linkDown = true;
            State = DeviceState.Faulted;
            foreach (var w in _wells) w.MarkLost(Emit);
            return;
        }
        if (_linkDown)
        {
            _linkDown = false;
            _ctx.Log?.Invoke("info", $"{InstanceId} 和触摸屏的连接恢复了");
            if (_pump is not null) State = DeviceState.Ready;
        }
        Offline = state["offline"]?.GetValue<string>() ?? "";
        var unacked = state["alarms_unacked"]?.GetValue<int>() ?? 0;
        if (unacked > 0)
        {
            // 触摸屏上有没确认的安全联锁：把最早那条的原因拿过来，挂在对应工位的控温状态上，工作站界面看得见
            try
            {
                var list = await _client.AskAsync("alarm.list", ct).ConfigureAwait(false);
                if (list["alarms"] is JsonArray arr)
                    foreach (var a in arr)
                        if (a is JsonObject o && o["acked"]?.GetValue<bool>() != true)
                        {
                            var ch = o["ch"]?.GetValue<int>() ?? 0;
                            if (ch is 1 or 2) _wells[ch - 1].PendingAlarm ??= o["title"]?.GetValue<string>();
                        }
            }
            catch (Exception ex) when (ex is IOException or PanelRefusedException) { /* 这一拍没问到报警原因，下一拍再问 */ }
        }
        if (state["ch"] is not JsonArray chs) return;
        for (var w = 0; w < 2 && w < chs.Count; w++)
        {
            if (unacked == 0) _wells[w].PendingAlarm = null;
            if (chs[w] is JsonObject c) _wells[w].Apply(c, Emit);
        }
    }

    private void Emit(int channel, string tag, double value, Quality quality)
    {
        var now = Now;
        _out.Push(new Sample(channel, tag, now.UtcTicks, now, value, quality));
    }

    /// <summary>
    /// 通道被中止（按了停止、联锁、出错）时把这一路收到安全态：停控温、停加料。搅拌不停——热液不搅会局部过热。
    /// 命令发不出去（线断了、触摸屏不答应）也要如实记下来，不能说成停好了。
    /// </summary>
    public async ValueTask<IReadOnlyList<string>?> SafeStopAsync(int well, CancellationToken ct)
    {
        if (well is < 0 or > 1) return Array.Empty<string>();
        var done = new List<string>();
        foreach (var (cmd, ok, bad) in new[] { ("temp.stop", "已让触摸屏停止控温", "停止控温的命令没送到触摸屏"), ("dose.stop", "已让触摸屏停止加料", "停止加料的命令没送到触摸屏") })
        {
            try
            {
                await _client.AskAsync(new JsonObject { ["cmd"] = cmd, ["ch"] = well + 1 }, ct).ConfigureAwait(false);
                done.Add(ok);
            }
            catch (Exception ex) when (ex is IOException or PanelRefusedException)
            {
                done.Add($"{bad}：{ex.Message}");
            }
        }
        done.Add("搅拌保持不动");
        return done;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _out.Complete();
        await _client.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
        State = DeviceState.Disposed;
    }
}

/// <summary>一个工位：最近一拍的读数，加上挂给上面的几样能力对象。</summary>
internal sealed class PanelWell
{
    private readonly PanelSession _s;
    private readonly int _well;
    private double _lastDosed;

    public PanelWell(PanelSession s, int well, int channel)
    {
        _s = s;
        _well = well;
        Channel = channel;
        Temp = new PanelTemp(this);
        Stirrer = new PanelStirrer(this);
        Pumps = new[] { new PanelPump(this, 1), new PanelPump(this, 2) };
        Ph = new PanelPh(this);
        Capabilities = new ICapability[] { Temp, Stirrer, Pumps[0], Ph };
    }

    public int Channel { get; }
    public PanelSession Session => _s;
    public PanelTemp Temp { get; }
    public PanelStirrer Stirrer { get; }
    public PanelPump[] Pumps { get; }
    public PanelPh Ph { get; }
    public IReadOnlyList<ICapability> Capabilities { get; }

    // 最近一拍的状态。读不到的是 NaN
    public double Tr = double.NaN, Tj = double.NaN, Sp = double.NaN, TjSp = double.NaN, Duty, PhValue = double.NaN, Rpm, RpmSet;
    public bool Running, SeqBusy, DoseActive;
    public string Mode = "off";
    public int DosePump;
    public double Dosed, TotalDosed;
    public string? PendingAlarm;
    public TempLimits Limits = new(-40, 180, 0);

    public void ApplyLimits(JsonObject? lim)
    {
        if (lim is null) return;
        Limits = new TempLimits(Num(lim, "tr_min", -40), Num(lim, "tr_max", 180), Num(lim, "rate_max_k_per_min", 0));
    }

    public void Apply(JsonObject c, Action<int, string, double, Quality> emit)
    {
        Running = c["running"]?.GetValue<bool>() == true;
        Mode = c["mode"]?.GetValue<string>() ?? "off";
        Tr = Num(c, "tr", double.NaN);
        Tj = Num(c, "tj", double.NaN);
        Sp = Running ? Num(c, "sp", double.NaN) : double.NaN;
        TjSp = Running ? Num(c, "tj_sp", double.NaN) : double.NaN;
        Duty = Num(c, "duty", 0);
        PhValue = Num(c, "ph", double.NaN);
        var seq = c["seq"]?["state"]?.GetValue<string>() ?? "idle";
        SeqBusy = seq is "running" or "waiting_confirm";
        if (c["stir"] is JsonObject st) { Rpm = Num(st, "rpm", 0); RpmSet = Num(st, "set", 0); }
        if (c["dose"] is JsonObject d)
        {
            DoseActive = d["active"]?.GetValue<bool>() == true;
            DosePump = d["pump"]?.GetValue<int>() ?? 0;
            // 触摸屏报的是「这一次加料已经加了多少」，每开一次新的从 0 起；这里攒成累计量
            var dosed = Num(d, "dosed_ml", 0);
            TotalDosed += dosed >= _lastDosed ? dosed - _lastDosed : dosed;
            _lastDosed = dosed;
            Dosed = dosed;
        }
        // 控温设备没连上时触摸屏里的温度是没读过的 0，不能当读数往上送
        var offline = _s.Offline.Length > 0;
        var q = offline ? Quality.Bad : Quality.Good;
        Emit(emit, TouchPanelDriver.TagTr, offline ? double.NaN : Tr, q);
        Emit(emit, TouchPanelDriver.TagTj, offline ? double.NaN : Tj, q);
        Emit(emit, TouchPanelDriver.TagDt, offline ? double.NaN : Tr - Tj, q);
        if (Running) { emit(Channel, TouchPanelDriver.TagTset, Sp, Quality.Good); emit(Channel, TouchPanelDriver.TagTjSet, TjSp, Quality.Good); }
        emit(Channel, TouchPanelDriver.TagDuty, Duty, Quality.Good);
        Emit(emit, TouchPanelDriver.TagPh, PhValue, Quality.Good);
        emit(Channel, TouchPanelDriver.TagRpm, Rpm, Quality.Good);
        emit(Channel, TouchPanelDriver.TagVol, TotalDosed, Quality.Good);
        Ph.Remember(PhValue, _s.Now);
    }

    /// <summary>线断了：主要读数按 Bad 推一拍，上面的安全层会把它当信号失效处理；内存里的读数也清掉，免得拿旧值判到达。</summary>
    public void MarkLost(Action<int, string, double, Quality> emit)
    {
        Tr = Tj = PhValue = double.NaN;
        foreach (var tag in new[] { TouchPanelDriver.TagTr, TouchPanelDriver.TagTj, TouchPanelDriver.TagPh, TouchPanelDriver.TagRpm })
            emit(Channel, tag, double.NaN, Quality.Bad);
    }

    private void Emit(Action<int, string, double, Quality> emit, string tag, double v, Quality q)
        => emit(Channel, tag, v, double.IsNaN(v) ? Quality.Bad : q);

    private static double Num(JsonObject o, string key, double fallback)
        => o[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : fallback;

    public Task<JsonObject> SendAsync(JsonObject command, CancellationToken ct)
    {
        command["ch"] = _well + 1;
        return _s.Client.AskAsync(command, ct);
    }
}

/// <summary>控温 + 控温状态 + 蒸回流。方法就是往触摸屏发命令；读数取最近一拍轮询到的。</summary>
internal sealed class PanelTemp : ITemperatureControl, ITemperatureStatus, IRefluxControl
{
    private readonly PanelWell _w;
    private readonly Broadcast<Sample> _temp = new();
    public PanelTemp(PanelWell w) => _w = w;

    public int Channel => _w.Channel;
    public TempLimits Limits => _w.Limits;
    public double CurrentReactor => _w.Tr;
    public double CurrentJacket => _w.Tj;
    public IObservable<Sample> Temperature => _temp;

    public async Task SetTargetAsync(TempTarget target, CancellationToken ct)
    {
        var mode = target.Kind == TempChannelKind.Jacket ? "jacket" : "reactor";
        // 已经在按同一种方式控着：只改目标，不重开——重开会把触摸屏那边已经估出来的量清掉，温度会晃一下
        if (_w.Running && _w.Mode == mode)
            await _w.SendAsync(new JsonObject { ["cmd"] = "temp.setpoint", ["target"] = target.Value }, ct).ConfigureAwait(false);
        else
            await _w.SendAsync(new JsonObject { ["cmd"] = "temp.start", ["mode"] = mode, ["target"] = target.Value, ["arrive"] = "fast", ["name"] = "工作站控温" }, ct).ConfigureAwait(false);
        Assume(mode, target.Value);
    }

    public async Task RampAsync(double target, double ratePerMin, TempChannelKind kind, CancellationToken ct)
    {
        var mode = kind == TempChannelKind.Jacket ? "jacket" : "reactor";
        await _w.SendAsync(new JsonObject
        {
            ["cmd"] = "temp.start", ["mode"] = mode, ["target"] = target, ["arrive"] = "rate",
            ["rate_k_per_min"] = Math.Abs(ratePerMin), ["name"] = "工作站控温"
        }, ct).ConfigureAwait(false);
        Assume(mode, target);
    }

    public Task<bool> WaitReachedAsync(double target, double tolerance, TimeSpan timeout, CancellationToken ct)
        => DriverTime.PollAsync(
            () => { var pv = _w.Mode == "jacket" ? _w.Tj : _w.Tr; return !double.IsNaN(pv) && Math.Abs(pv - target) <= tolerance; },
            timeout, _w.Session.Scale, () => _w.Session.Now, ct);

    public async Task StopAsync(CancellationToken ct)
    {
        await _w.SendAsync(new JsonObject { ["cmd"] = "temp.stop" }, ct).ConfigureAwait(false);
        _w.Running = false; _w.Mode = "off";
    }

    // 命令发出去到下一拍轮询回来之间有半秒：先按「已经照办」记着，免得紧跟着的判断拿到旧状态
    private void Assume(string mode, double sp) { _w.Running = true; _w.Mode = mode; _w.Sp = sp; }

    // ── 控温状态 ──
    public double? Setpoint => _w.Running && !double.IsNaN(_w.Sp) ? _w.Sp : null;
    bool ITemperatureStatus.Active => _w.Running;
    public double? CascadeInnerSetpoint => _w.Running && _w.Mode != "jacket" && !double.IsNaN(_w.TjSp) ? _w.TjSp : null;
    public string? Alarm => _w.PendingAlarm is { } a ? $"触摸屏安全联锁：{a}（到触摸屏上确认）"
                          : _w.Session.Offline.Length > 0 ? _w.Session.Offline
                          : _w.Session.LinkDown ? "和触摸屏的连接断了" : null;
    public string? Holding => _w.SeqBusy ? "触摸屏正在跑它自己的序列，这一路现在不听工作站的控温命令" : null;

    // ── 蒸回流 ──
    Task IRefluxControl.StartAsync(double deltaT, double maxTj, CancellationToken ct)
    {
        _ = maxTj;      // 夹套上限由触摸屏自己的安全限值把着，这里不另设
        return _w.SendAsync(new JsonObject { ["cmd"] = "temp.reflux", ["delta_k"] = deltaT, ["name"] = "工作站蒸回流" }, ct)
                 .ContinueWith(t => { t.GetAwaiter().GetResult(); _w.Running = true; _w.Mode = "reflux"; }, ct, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>停跟随不等于停控温：把夹套定在此刻的温度上，往哪走由下一步决定（和直连驱动的规矩一样）。</summary>
    async Task IRefluxControl.StopAsync(CancellationToken ct)
    {
        if (_w.Mode != "reflux") return;
        var hold = double.IsNaN(_w.Tj) ? 25 : _w.Tj;
        await _w.SendAsync(new JsonObject { ["cmd"] = "temp.start", ["mode"] = "jacket", ["target"] = hold, ["arrive"] = "fast", ["name"] = "工作站控温" }, ct).ConfigureAwait(false);
        Assume("jacket", hold);
    }

    bool IRefluxControl.Active => _w.Running && _w.Mode == "reflux";
}

internal sealed class PanelStirrer : IStirrer, IStirrerStatus
{
    private readonly PanelWell _w;
    private readonly Broadcast<Sample> _speed = new();
    public PanelStirrer(PanelWell w) => _w = w;

    public int Channel => _w.Channel;
    public SpeedLimits Limits { get; } = new(0, 1000);
    public double CurrentRpm => _w.Rpm;
    public double TargetRpm => _w.RpmSet;
    public IObservable<Sample> Speed => _speed;

    public async Task SetSpeedAsync(double rpm, CancellationToken ct)
    {
        await _w.SendAsync(new JsonObject { ["cmd"] = "stir.set", ["rpm"] = Math.Clamp(rpm, Limits.Min, Limits.Max) }, ct).ConfigureAwait(false);
        _w.RpmSet = rpm;
    }

    public Task StopAsync(CancellationToken ct) => SetSpeedAsync(0, ct);
}

/// <summary>一台加料泵（泵 1 / 泵 2）。挂在通道上的是泵 1；配方步骤里点名泵 2 时由指令处理器另取。</summary>
internal sealed class PanelPump : IDosing
{
    private readonly PanelWell _w;
    private readonly int _pump;
    private readonly Broadcast<Sample> _none = new();
    public PanelPump(PanelWell w, int pump) { _w = w; _pump = pump; }

    public int Channel => _w.Channel;
    public FlowLimits Limits { get; } = new(0.001, 50, 2000);
    public CalibrationRecord? Calibration => null;
    /// <summary>这个工位累计加了多少。触摸屏一个工位同一时刻只有一路加料在走，所以两台泵共用这一个数。</summary>
    public double TotalVolume => _w.TotalDosed;
    public bool Active => _w.DoseActive && _w.DosePump == _pump;
    public IObservable<Sample> Flow => _none;
    public IObservable<Sample> Total => _none;

    public Task DoseAsync(DoseRequest request, CancellationToken ct)
        => _w.SendAsync(new JsonObject
        {
            ["cmd"] = "dose.start",
            ["dose"] = new JsonObject
            {
                ["pump"] = _pump, ["mode"] = "rate", ["volume_ml"] = request.Volume,
                ["rate_ml_min"] = Math.Clamp(request.RatePerMin, Limits.Min, Limits.Max), ["liquid"] = request.Material ?? ""
            }
        }, ct);

    public Task SetRateAsync(double ratePerMin, CancellationToken ct)
        => Task.FromException(new InvalidOperationException("带触摸屏的机型不能在加料中途改流量：先停，再按新的流量重新加"));

    public Task StopAsync(CancellationToken ct) => _w.SendAsync(new JsonObject { ["cmd"] = "dose.stop" }, ct);
}

internal sealed class PanelPh : IScalarSensor
{
    private readonly PanelWell _w;
    private readonly Broadcast<Sample> _values = new();
    private Sample? _latest;
    public PanelPh(PanelWell w) => _w = w;

    public int Channel => _w.Channel;
    public IReadOnlyList<TagDescriptor> Tags { get; } = new[] { new TagDescriptor(TouchPanelDriver.TagPh, "pH", "", DataShape.Scalar) };
    public IObservable<Sample> Values => _values;

    public void Remember(double value, DateTimeOffset now)
    {
        var s = new Sample(Channel, TouchPanelDriver.TagPh, now.UtcTicks, now, value, double.IsNaN(value) ? Quality.Bad : Quality.Good);
        _latest = s;
        _values.Push(s);
    }

    public bool TryReadLatest(string tag, out Sample sample)
    {
        sample = _latest ?? default;
        return tag == TouchPanelDriver.TagPh && _latest is { Quality: Quality.Good };
    }
}

/// <summary>配方「搅拌」：照直连驱动的规矩，转速 0 是停机；设完等「到达用时」那么久再算这一步完。</summary>
internal sealed class PanelStirHandler : ICommandHandler
{
    public async Task<CommandOutcome> ExecuteAsync(CommandContext ctx, CommandInput p, CancellationToken ct)
    {
        var stir = ctx.Capabilities.Get<IStirrer>() ?? throw new InvalidOperationException("该通道没有搅拌能力");
        var began = ctx.Now();
        var rpm = p.Num("rpm");
        var ramp = CommandSpecs.StirImmediate(p) ? 0.5 : Math.Max(0.5, p.Num("ramp", 5));
        if (rpm <= 0) await stir.StopAsync(ct).ConfigureAwait(false);
        else await stir.SetSpeedAsync(rpm, ct).ConfigureAwait(false);
        await DriverTime.DelayAsync(TimeSpan.FromSeconds(ramp), ctx.TimeScale, ct).ConfigureAwait(false);
        return new CommandOutcome(EndReason.Reached, ctx.Now() - began)
        {
            Note = rpm <= 0 ? "搅拌已停机" : $"搅拌已设 {rpm:0} rpm，实测 {stir.CurrentRpm:0} rpm"
        };
    }
}

/// <summary>配方「加料」：步骤里的「加料泵 1 / 加料泵 2」对应触摸屏的泵 1 / 泵 2；发出去以后等触摸屏报的已加量到数。</summary>
internal sealed class PanelDoseHandler : ICommandHandler
{
    private readonly PanelSession _s;
    public PanelDoseHandler(PanelSession s) => _s = s;

    public async Task<CommandOutcome> ExecuteAsync(CommandContext ctx, CommandInput p, CancellationToken ct)
    {
        var onChannel = ctx.Capabilities.Get<IDosing>() as PanelPump ?? throw new InvalidOperationException("该通道没有加料能力");
        var which = p.Str("pump", "加料泵 1").Contains('2') ? 2 : 1;
        PanelPump pump = onChannel;
        for (var w = 0; w < _s.WellCount; w++)
            if (_s.Well(w).Channel == ctx.Channel) pump = _s.Well(w).Pumps[which - 1];
        var volume = Math.Max(0, p.Num("vol"));
        var rate = CommandSpecs.DoseAtOnce(p) ? pump.Limits.Max : Math.Clamp(p.Num("rate", 0.5), 0.001, pump.Limits.Max);
        var began = ctx.Now();
        var startTotal = pump.TotalVolume;

        await pump.DoseAsync(new DoseRequest(volume, rate) { Material = p.Str("liq") }, ct).ConfigureAwait(false);
        var planned = TimeSpan.FromSeconds(volume / rate * 60.0);
        var reached = await DriverTime.PollAsync(() => pump.TotalVolume - startTotal >= volume - 1e-6,
                                                 planned + TimeSpan.FromMinutes(5), ctx.TimeScale, ctx.Now, ct).ConfigureAwait(false);
        try { await pump.StopAsync(ct).ConfigureAwait(false); } catch (Exception ex) when (ex is IOException or InvalidOperationException) { /* 送够了触摸屏自己已停，这一下只是兜底 */ }

        var actual = pump.TotalVolume - startTotal;
        return new CommandOutcome(reached ? EndReason.QuantityDelivered : EndReason.Timeout, ctx.Now() - began)
        {
            Note = reached ? $"泵 {which} 实加 {actual:F2} mL" : $"泵 {which} 超时：计划 {volume:F2} mL，实加 {actual:F2} mL（到触摸屏上看泵有没有在转、有没有报警）"
        };
    }
}
