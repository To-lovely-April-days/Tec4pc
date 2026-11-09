using Tec.Driver.Abi;
using Tec.Drivers.DualStation;
using Tec.Drivers.Rd105;
using Tec.Drivers.Rd105.Modbus;

namespace Tec.Drivers.Htlab;

/// <summary>
/// 进料系统的会话：轮询一帧读全，把两台泵、MFC、报警发到所绑通道；泵以 IDosing 端出去（配方加料泵那台挂到通道上，
/// 两台都能被配方「加料」步骤按「加料泵 1 / 2」点名）；设备参数面板管 MFC、两台泵的设定与开停、量程、压力报警、复位消音。
///
/// 定量加料的做法：写设定流量、开泵、记下起点累计量；每拍看累计量，送够了就停——「送够就停」是泵自己的事，
/// 不靠上层轮询发现超了再喊停（仿真泵那条注释里写的教训）。真机上累计量是进料系统 PLC 报的，不是我们积分的。
/// </summary>
public sealed class FeedSystemSession : IDeviceSession, IDeviceSettings, IReconnectAwareSession
{
    public const string GroupLive = "live", GroupControl = "ctl", GroupMfc = "mfc", GroupAlarm = "alarm";
    public const string KMfcSet = "mfcset", KMfcFlow = "mfcflow", KMfcTotal = "mfctotal", KMfcRun = "mfcrun",
                        KP1Set = "p1set", KP1Flow = "p1flow", KP1Total = "p1total", KP1P = "p1p", KP1Run = "p1run",
                        KP2Set = "p2set", KP2Flow = "p2flow", KP2Total = "p2total", KP2P = "p2p", KP2Run = "p2run",
                        KAlarm = "alarm",
                        KRangeHigh = "rangehigh", KRangeLow = "rangelow", KCorrection = "corr", KDpt = "dpt",
                        KP1High = "p1high", KP1Low = "p1low", KP2High = "p2high", KP2Low = "p2low";
    public const string ActReset = "reset", ActSilence = "silence", ActStopAll = "stopall";
    public const string RunOn = "运行", RunOff = "停止";
    public static readonly string[] RunOptions = { RunOff, RunOn };

    private readonly SharedSerial _link;
    private readonly FeedClient _client;
    private readonly DriverContext _ctx;
    private readonly ParameterSet _connection;
    private readonly TimeSpan _period;
    private readonly Broadcast<Sample> _out = new();
    private readonly LinkRecovery _recover;
    private readonly Pump[] _pumps;          // [0] = 泵 1、[1] = 泵 2（都按所绑的第一个通道号）
    private readonly int _recipePump;        // 1 / 2：挂到通道上当 IDosing 的那台
    private readonly bool _stopGasOnSafeStop;
    private readonly int _channel;
    private DeviceState _state = DeviceState.Connected;
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;
    private int _fails;
    private FeedSnapshot? _last;
    private string _lastAlarm = "";

    internal FeedSystemSession(SharedSerial link, FeedClient client, DriverContext ctx, ParameterSet connection, TimeSpan period)
    {
        _link = link;
        _client = client;
        _ctx = ctx;
        _connection = connection;
        _period = period;
        _channel = ctx.ChannelNumbers.Count > 0 ? ctx.ChannelNumbers[0] : 0;
        var cfg = ctx.Config;
        _recipePump = cfg.Str(FeedSystemDriver.FieldRecipePump, FeedSystemDriver.Pump1) == FeedSystemDriver.Pump2 ? 2 : 1;
        _stopGasOnSafeStop = cfg.Flag(FeedSystemDriver.FieldStopGasOnSafeStop, true);
        var maxRate = Math.Max(0.1, cfg.Num(FeedSystemDriver.FieldMaxRate, 50));
        var maxVol = Math.Max(1, cfg.Num(FeedSystemDriver.FieldMaxVolume, 2000));
        var opened = ctx.Clock();
        _pumps = new[]
        {
            new Pump(this, 1, _channel, maxRate, maxVol, cfg.Str(FeedSystemDriver.FieldLiquid1, "料液 A"), opened),
            new Pump(this, 2, _channel, maxRate, maxVol, cfg.Str(FeedSystemDriver.FieldLiquid2, "料液 B"), opened)
        };
        _recover = new LinkRecovery($"{ctx.InstanceId} 进料系统", link.Reopen, (l, t) => ctx.Log?.Invoke(l, t));
    }

    public string InstanceId => _ctx.InstanceId;
    /// <summary>台面重建重开（不是真断开）：收会话时泵 / 气路照旧跑，交给新会话接手。真断开（程序退出、设备删了）停泵停气。</summary>
    public bool Reconnecting { get; set; } = false;
    internal int LinkReopens => _recover.Reopens;
    internal FeedSnapshot? Last => _last;
    /// <summary>配方加料泵（1 / 2）。</summary>
    public int RecipePump => _recipePump;
    /// <summary>按号取泵（1 / 2），配方步骤点名用。</summary>
    public IDosing PumpOf(int n) => _pumps[n == 2 ? 1 : 0];

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
    public IObservable<Sample> Samples => _out;
    public int WellCount => 1;

    public IReadOnlyList<TagDescriptor> Tags { get; } = new[]
    {
        new TagDescriptor(FeedSystemDriver.TagFlow, "加料流量", "mL/min", DataShape.Scalar),
        new TagDescriptor(FeedSystemDriver.TagVol, "累计加料", "mL", DataShape.Scalar),
        new TagDescriptor(FeedSystemDriver.TagP1Flow, "泵 1 流量", "mL/min", DataShape.Scalar),
        new TagDescriptor(FeedSystemDriver.TagP1Vol, "泵 1 累计", "mL", DataShape.Scalar),
        new TagDescriptor(FeedSystemDriver.TagP1Pressure, "泵 1 压力", "MPa", DataShape.Scalar),
        new TagDescriptor(FeedSystemDriver.TagP2Flow, "泵 2 流量", "mL/min", DataShape.Scalar),
        new TagDescriptor(FeedSystemDriver.TagP2Vol, "泵 2 累计", "mL", DataShape.Scalar),
        new TagDescriptor(FeedSystemDriver.TagP2Pressure, "泵 2 压力", "MPa", DataShape.Scalar),
        new TagDescriptor(FeedSystemDriver.TagGasFlow, "MFC 流量", "L/min", DataShape.Scalar),
        new TagDescriptor(FeedSystemDriver.TagGasVol, "MFC 累计", "L", DataShape.Scalar),
        new TagDescriptor(FeedSystemDriver.TagFeedAlarm, "进料系统报警", "", DataShape.State)
    };

    /// <summary>挂到通道上的加料能力 = 配方加料泵那台。另一台靠配方步骤点名、或设备参数面板。</summary>
    public IReadOnlyList<ICapability> CapabilitiesOf(int well)
        => well == 0 ? new ICapability[] { PumpOf(_recipePump) } : Array.Empty<ICapability>();

    public ICommandHandler? Resolve(string commandId)
        => commandId == CommandSpecs.Dose ? new FeedDoseHandler(this) : null;

    public async Task InitAsync(CancellationToken ct)
    {
        var s = await _client.ReadAllAsync(ct).ConfigureAwait(false);
        Apply(s, _ctx.Clock(), publish: false);
        _ctx.Log?.Invoke("info", $"{InstanceId} 进料系统（{FeedSystemDriver.LinkName(_connection)}）：{FeedSystemDriver.Readout(s)}；" +
                                 $"配方加料泵 = 泵 {_recipePump}、泵流量上限 {_pumps[0].Limits.Max:0.#} mL/min、单次上限 {_pumps[0].Limits.MaxVolume:0} mL、" +
                                 $"安全停机{(_stopGasOnSafeStop ? "停泵停气" : "只停泵")}");
        if (s.AnyAlarm) _ctx.Log?.Invoke("warn", $"{InstanceId} 开机就带着报警：{s.AlarmText()}——到「设备参数」里点「报警复位」");
        if (s.Pump1On || s.Pump2On || s.MfcOn)
            _ctx.Log?.Invoke("warn", $"{InstanceId} 接手时{(s.Pump1On ? " 泵 1" : "")}{(s.Pump2On ? " 泵 2" : "")}{(s.MfcOn ? " MFC" : "")} 在跑——按物理现状接手，不替人停");
    }

    public Task StartAsync(CancellationToken ct)
    {
        _link.Open();
        _pollCts = new CancellationTokenSource();
        _pollTask = Task.Run(() => PollLoopAsync(_pollCts.Token), CancellationToken.None);
        State = _last is { AnyAlarm: true } ? DeviceState.Faulted : DeviceState.Ready;
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _pollCts?.Cancel();
        if (_pollTask is { } t) { try { await t.ConfigureAwait(false); } catch { } }
        _pollTask = null;
        State = DeviceState.Connected;
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_period);
        while (!ct.IsCancellationRequested)
        {
            try { if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) break; }
            catch (OperationCanceledException) { break; }
            try { await PollOnceAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>采一拍。单拎出来是为了回归测试能一拍一拍地推。</summary>
    internal async Task PollOnceAsync(CancellationToken ct)
    {
        try
        {
            var s = await _client.ReadAllAsync(ct).ConfigureAwait(false);
            if (_fails > 0) { _ctx.Log?.Invoke("info", $"{InstanceId} 进料系统恢复（读失败 {_fails} 拍后）"); _fails = 0; }
            _recover.Ok();
            Apply(s, _ctx.Clock(), publish: true);
            // 定量加料送够了就停——每拍核一次
            foreach (var p in _pumps) await p.CheckDeliveredAsync(s, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            if (_fails++ == 0)
            {
                var why = ex is IOException or UnauthorizedAccessException ? SerialFault.Explain(ex, _link.PortName) : ex.Message;
                _ctx.Log?.Invoke("warn", $"{InstanceId} 进料系统读失败：{why}（连续失败只报第一次）");
            }
            _recover.Failed(ex);
        }
    }

    private void Apply(FeedSnapshot s, DateTimeOffset at, bool publish)
    {
        _last = s;
        var alarm = s.AnyAlarm ? s.AlarmText() : "";
        if (alarm != _lastAlarm)
        {
            if (alarm.Length > 0) _ctx.Log?.Invoke("error", $"{InstanceId} 进料系统报警：{alarm}——处理完到「设备参数」点「报警复位」");
            else _ctx.Log?.Invoke("info", $"{InstanceId} 进料系统报警已清");
            _lastAlarm = alarm;
            if (_pollTask is not null) State = alarm.Length > 0 ? DeviceState.Faulted : DeviceState.Ready;
        }
        foreach (var p in _pumps) p.Update(s, at, publish);
        if (!publish) return;
        var ch = _channel;
        void Push(string tag, double v) => _out.Push(new Sample(ch, tag, at.UtcTicks, at, v, Quality.Good));
        var rp = PumpOf(_recipePump);
        Push(FeedSystemDriver.TagFlow, Math.Round(s.PumpFlow(_recipePump), 3));
        Push(FeedSystemDriver.TagVol, Math.Round(s.PumpTotal(_recipePump), 3));
        Push(FeedSystemDriver.TagP1Flow, Math.Round(s.Pump1Flow, 3)); Push(FeedSystemDriver.TagP1Vol, Math.Round(s.Pump1Total, 3)); Push(FeedSystemDriver.TagP1Pressure, Math.Round(s.Pump1Pressure, 3));
        Push(FeedSystemDriver.TagP2Flow, Math.Round(s.Pump2Flow, 3)); Push(FeedSystemDriver.TagP2Vol, Math.Round(s.Pump2Total, 3)); Push(FeedSystemDriver.TagP2Pressure, Math.Round(s.Pump2Pressure, 3));
        Push(FeedSystemDriver.TagGasFlow, Math.Round(s.MfcFlow, 3)); Push(FeedSystemDriver.TagGasVol, Math.Round(s.MfcTotal, 3));
        Push(FeedSystemDriver.TagFeedAlarm, s.AnyAlarm ? 1 : 0);
        _ = rp;
    }

    // ── 动作 ───────────────────────────────────────────────────────

    internal async Task PumpStartAsync(int pump, double mlPerMin, CancellationToken ct)
    {
        try
        {
            await _client.SetPumpSetpointAsync(pump, mlPerMin, ct).ConfigureAwait(false);
            await _client.SetPumpAsync(pump, true, ct).ConfigureAwait(false);
        }
        catch (ModbusException ex) { throw new InvalidOperationException($"{InstanceId} 泵 {pump} 没开起来：{ex.Message}", ex); }
        _ctx.Log?.Invoke("info", $"{InstanceId} 泵 {pump} 设 {mlPerMin:0.##} mL/min 并启动");
    }

    internal async Task PumpStopAsync(int pump, CancellationToken ct, string why = "")
    {
        try { await _client.SetPumpAsync(pump, false, ct).ConfigureAwait(false); }
        catch (ModbusException ex) { throw new InvalidOperationException($"{InstanceId} 泵 {pump} 没停下来：{ex.Message}", ex); }
        _ctx.Log?.Invoke("info", $"{InstanceId} 泵 {pump} 停{(why.Length > 0 ? $"（{why}）" : "")}");
    }

    internal async Task PumpSetRateAsync(int pump, double mlPerMin, CancellationToken ct)
    {
        try { await _client.SetPumpSetpointAsync(pump, mlPerMin, ct).ConfigureAwait(false); }
        catch (ModbusException ex) { throw new InvalidOperationException($"{InstanceId} 泵 {pump} 流量没写进去：{ex.Message}", ex); }
    }

    /// <summary>
    /// 安全停机（通道被中止）：两台泵一定停；气路按配置。照实记录停的那一刻泵送了多少。
    /// </summary>
    public async ValueTask<IReadOnlyList<string>?> SafeStopAsync(int well, CancellationToken ct)
    {
        var did = new List<string>();
        var s = _last;
        foreach (var p in _pumps)
        {
            var wasOn = s?.PumpOn(p.Number) ?? false;
            var running = wasOn || p.Dosing;
            try
            {
                if (running) await PumpStopAsync(p.Number, ct, "安全停机").ConfigureAwait(false);
                p.CancelDose();
                did.Add(running ? $"泵 {p.Number}（{p.Material}）已停，本趟累计加入 {p.TripVolume:F2} mL" : $"泵 {p.Number} 本来就停着，未动");
            }
            catch (Exception ex) { did.Add($"泵 {p.Number} 停机失败：{ex.Message}"); }
        }
        if (_stopGasOnSafeStop)
        {
            try
            {
                if (s?.MfcOn == true) { await _client.SetMfcAsync(false, ct).ConfigureAwait(false); did.Add("MFC 气路已停"); }
                else did.Add("MFC 气路本来就停着，未动");
            }
            catch (Exception ex) { did.Add($"MFC 停气失败：{ex.Message}"); }
        }
        else did.Add("MFC 气路保持（「安全停机也停气路」没勾）");
        return did;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        if (!Reconnecting)
        {
            // 真断开（程序退出 / 设备删了）：泵不能没人管地继续往釜里送，停泵停气；台面重建重开不走这里
            try
            {
                var s = _last;
                if (s?.Pump1On == true) await _client.SetPumpAsync(1, false, CancellationToken.None).ConfigureAwait(false);
                if (s?.Pump2On == true) await _client.SetPumpAsync(2, false, CancellationToken.None).ConfigureAwait(false);
                if (s?.MfcOn == true) await _client.SetMfcAsync(false, CancellationToken.None).ConfigureAwait(false);
                if (s is not null && (s.Pump1On || s.Pump2On || s.MfcOn))
                    _ctx.Log?.Invoke("warn", $"{InstanceId} 会话关闭（真断开），已停{(s.Pump1On ? " 泵 1" : "")}{(s.Pump2On ? " 泵 2" : "")}{(s.MfcOn ? " MFC" : "")}");
            }
            catch (Exception ex) { _ctx.Log?.Invoke("error", $"{InstanceId} 会话关闭时停泵停气失败：{ex.Message}——到进料系统面板上手动停"); }
        }
        else if (_last is { } l && (l.Pump1On || l.Pump2On || l.MfcOn))
            _ctx.Log?.Invoke("info", $"{InstanceId} 台面重建重开：泵 / 气路照旧跑，新会话接手");
        _out.Complete();
        _link.Dispose();
        State = DeviceState.Disposed;
    }

    // ── IDeviceSettings ───────────────────────────────────────────

    public IReadOnlyList<SettingsGroup> Groups { get; } = new[]
    {
        new SettingsGroup(GroupLive, "实时", new ParameterSchema(new[]
        {
            Field.Num(KMfcFlow, "MFC 瞬时流量", 0, "L/min", 0, 1000, 0.01) with { ReadOnly = true },
            Field.Num(KMfcTotal, "MFC 累计", 0, "L", 0, 1e9, 0.1) with { ReadOnly = true },
            Field.Text(KMfcRun, "MFC 状态", "停止") with { ReadOnly = true },
            Field.Num(KP1Flow, "泵 1 瞬时流量", 0, "mL/min", 0, 10000, 0.01) with { ReadOnly = true },
            Field.Num(KP1Total, "泵 1 累计", 0, "mL", 0, 1e9, 0.1) with { ReadOnly = true },
            Field.Num(KP1P, "泵 1 压力", 0, "MPa", -1, 100, 0.01) with { ReadOnly = true },
            Field.Text(KP1Run, "泵 1 状态", "停止") with { ReadOnly = true },
            Field.Num(KP2Flow, "泵 2 瞬时流量", 0, "mL/min", 0, 10000, 0.01) with { ReadOnly = true },
            Field.Num(KP2Total, "泵 2 累计", 0, "mL", 0, 1e9, 0.1) with { ReadOnly = true },
            Field.Num(KP2P, "泵 2 压力", 0, "MPa", -1, 100, 0.01) with { ReadOnly = true },
            Field.Text(KP2Run, "泵 2 状态", "停止") with { ReadOnly = true },
            Field.Text(KAlarm, "报警", "无") with { ReadOnly = true }
        })) { Live = true, Tip = "进料系统 PLC 报的读数，两秒一刷" },
        new SettingsGroup(GroupControl, "控制", new ParameterSchema(new[]
        {
            Field.Num(KMfcSet, "MFC 设定流量", 0, "L/min", 0, 1000, 0.01) with { Tip = "寄存器 0。写完读回核对" },
            Field.Sel(KMfcRun, "MFC 运行", RunOptions, RunOff) with { Tip = "寄存器 38：0 运行、1 停止（厂家定义跟泵反着，这里按字面选）" },
            Field.Num(KP1Set, "泵 1 设定流量", 0, "mL/min", 0, 10000, 0.01) with { Tip = "寄存器 6" },
            Field.Sel(KP1Run, "泵 1 运行", RunOptions, RunOff) with { Tip = "寄存器 39：1 运行、0 停止" },
            Field.Num(KP2Set, "泵 2 设定流量", 0, "mL/min", 0, 10000, 0.01) with { Tip = "寄存器 14" },
            Field.Sel(KP2Run, "泵 2 运行", RunOptions, RunOff) with { Tip = "寄存器 40：1 运行、0 停止" }
        })) { Tip = "手动开停与流量。配方跑着的时候别在这里动泵——配方那一步正按累计量等着送够" },
        new SettingsGroup(GroupMfc, "MFC 配置", new ParameterSchema(new[]
        {
            Field.Num(KRangeHigh, "量程上限", 10, "L/min", 0, 1000, 0.01) with { Tip = "寄存器 22" },
            Field.Num(KRangeLow, "量程下限", 0, "L/min", 0, 1000, 0.01) with { Tip = "寄存器 24" },
            Field.Num(KCorrection, "流量修正系数", 1, "", 0, 10, 0.001) with { Tip = "寄存器 26" },
            Field.Num(KDpt, "小数点位", 1, "", 0, 3, 1) with { Tip = "寄存器 28，厂家注明固定为 1" }
        })) { Tip = "MFC 的量程与修正，照流量计铭牌填" },
        new SettingsGroup(GroupAlarm, "压力报警", new ParameterSchema(new[]
        {
            Field.Num(KP1High, "泵 1 压力上限", 1, "MPa", 0, 100, 0.01) with { Tip = "寄存器 30，超过报警并停泵（进料系统自己的逻辑）" },
            Field.Num(KP1Low, "泵 1 压力下限", 0, "MPa", -1, 100, 0.01) with { Tip = "寄存器 32" },
            Field.Num(KP2High, "泵 2 压力上限", 1, "MPa", 0, 100, 0.01) with { Tip = "寄存器 34" },
            Field.Num(KP2Low, "泵 2 压力下限", 0, "MPa", -1, 100, 0.01) with { Tip = "寄存器 36" }
        })) { Tip = "压力报警是进料系统自己判的，这里只是写它的阈值" }
    };

    public IReadOnlyList<SettingsAction> Actions { get; } = new[]
    {
        new SettingsAction(ActReset, "报警复位") { Group = GroupAlarm, Tip = "寄存器 46 写 1：清全部报警位" },
        new SettingsAction(ActSilence, "报警消音") { Group = GroupAlarm, Tip = "寄存器 47 写 1" },
        new SettingsAction(ActStopAll, "全部停止（两台泵 + MFC）") { Group = GroupControl, Confirm = true, Tip = "泵 1、泵 2 写停止，MFC 写停止" }
    };

    public async Task<ParameterSet> ReadAsync(string groupId, CancellationToken ct)
    {
        var s = await _client.ReadAllAsync(ct).ConfigureAwait(false);
        Apply(s, _ctx.Clock(), publish: false);
        var p = new ParameterSet();
        switch (groupId)
        {
            case GroupLive:
                p[KMfcFlow] = R2(s.MfcFlow); p[KMfcTotal] = R1(s.MfcTotal); p[KMfcRun] = s.MfcOn ? RunOn : RunOff;
                p[KP1Flow] = R2(s.Pump1Flow); p[KP1Total] = R1(s.Pump1Total); p[KP1P] = R2(s.Pump1Pressure); p[KP1Run] = s.Pump1On ? RunOn : RunOff;
                p[KP2Flow] = R2(s.Pump2Flow); p[KP2Total] = R1(s.Pump2Total); p[KP2P] = R2(s.Pump2Pressure); p[KP2Run] = s.Pump2On ? RunOn : RunOff;
                p[KAlarm] = s.AnyAlarm ? s.AlarmText() : "无";
                break;
            case GroupControl:
                p[KMfcSet] = R2(s.MfcSetpoint); p[KMfcRun] = s.MfcOn ? RunOn : RunOff;
                p[KP1Set] = R2(s.Pump1Setpoint); p[KP1Run] = s.Pump1On ? RunOn : RunOff;
                p[KP2Set] = R2(s.Pump2Setpoint); p[KP2Run] = s.Pump2On ? RunOn : RunOff;
                break;
            case GroupMfc:
                p[KRangeHigh] = R2(s.MfcRangeHigh); p[KRangeLow] = R2(s.MfcRangeLow); p[KCorrection] = Math.Round(s.MfcCorrection, 3); p[KDpt] = Math.Round(s.MfcDpt, 0);
                break;
            case GroupAlarm:
                p[KP1High] = R2(s.Pump1PressureHigh); p[KP1Low] = R2(s.Pump1PressureLow); p[KP2High] = R2(s.Pump2PressureHigh); p[KP2Low] = R2(s.Pump2PressureLow);
                break;
            default: throw new ArgumentException($"没有这一组：{groupId}", nameof(groupId));
        }
        return p;
    }

    private static double R1(double v) => Math.Round(v, 1);
    private static double R2(double v) => Math.Round(v, 2);

    public async Task<IReadOnlyList<string>> WriteAsync(string groupId, ParameterSet values, CancellationToken ct)
    {
        var notes = new List<string>();
        var group = Groups.FirstOrDefault(g => g.Id == groupId) ?? throw new ArgumentException($"没有这一组：{groupId}", nameof(groupId));
        if (group.Live) { notes.Add("「实时」都是只读的，没有可写的项"); return notes; }
        foreach (var key in values.Keys.ToList())
        {
            var spec = group.Schema.Find(key);
            if (spec is null) { notes.Add($"{key}：这一组里没有这一项，没写"); continue; }
            if (spec.ReadOnly) { notes.Add($"{spec.Label}：只读，没写"); continue; }
            try
            {
                var note = await WriteOneAsync(key, spec, values, ct).ConfigureAwait(false);
                notes.Add(note);
                _ctx.Log?.Invoke("info", $"{InstanceId} 进料系统参数：{note}");
            }
            catch (ModbusException ex)
            {
                notes.Add($"{spec.Label}：进料系统没照写（{ex.Message}）");
                _ctx.Log?.Invoke("warn", $"{InstanceId} 进料系统参数：{spec.Label} 没写成——{ex.Message}");
            }
        }
        return notes;
    }

    private static readonly Dictionary<string, ushort> FloatRegs = new(StringComparer.Ordinal)
    {
        [KMfcSet] = FeedRegisters.MfcSetpoint, [KP1Set] = FeedRegisters.Pump1Setpoint, [KP2Set] = FeedRegisters.Pump2Setpoint,
        [KRangeHigh] = FeedRegisters.MfcRangeHigh, [KRangeLow] = FeedRegisters.MfcRangeLow, [KCorrection] = FeedRegisters.MfcCorrection, [KDpt] = FeedRegisters.MfcDpt,
        [KP1High] = FeedRegisters.Pump1PressureHigh, [KP1Low] = FeedRegisters.Pump1PressureLow,
        [KP2High] = FeedRegisters.Pump2PressureHigh, [KP2Low] = FeedRegisters.Pump2PressureLow
    };

    private async Task<string> WriteOneAsync(string key, FieldSpec spec, ParameterSet values, CancellationToken ct)
    {
        if (FloatRegs.TryGetValue(key, out var reg))
        {
            var want = values.Num(key);
            await _client.WriteFloatAsync(reg, want, ct).ConfigureAwait(false);
            var got = await _client.ReadFloatAsync(reg, ct).ConfigureAwait(false);
            return Math.Abs(got - want) <= Math.Max(0.005, Math.Abs(want) * 1e-5)
                ? $"{spec.Label} 写为 {want:0.###}{(spec.Unit is { Length: > 0 } u ? " " + u : "")}，读回一致"
                : $"{spec.Label} 写了 {want:0.###} 读回却是 {got:0.###}——写没生效";
        }
        switch (key)
        {
            case KMfcRun:
            {
                var on = values.Str(key) == RunOn;
                await _client.SetMfcAsync(on, ct).ConfigureAwait(false);
                var got = await _client.ReadInt16Async(FeedRegisters.MfcOnOff, ct).ConfigureAwait(false);
                return (got == 0) == on ? $"MFC {(on ? "已运行" : "已停止")}" : $"MFC 写了{(on ? "运行" : "停止")}读回却是 {got}——写没生效";
            }
            case KP1Run or KP2Run:
            {
                var pump = key == KP1Run ? 1 : 2;
                var on = values.Str(key) == RunOn;
                await _client.SetPumpAsync(pump, on, ct).ConfigureAwait(false);
                var got = await _client.ReadInt16Async(pump == 1 ? FeedRegisters.Pump1OnOff : FeedRegisters.Pump2OnOff, ct).ConfigureAwait(false);
                if (!on) PumpOfInternal(pump).CancelDose();
                return (got == 1) == on ? $"泵 {pump} {(on ? "已运行" : "已停止")}" : $"泵 {pump} 写了{(on ? "运行" : "停止")}读回却是 {got}——写没生效";
            }
            default: return $"{spec.Label}：不知道怎么写";
        }
    }

    private Pump PumpOfInternal(int n) => _pumps[n == 2 ? 1 : 0];

    public async Task<string> RunAsync(string actionId, CancellationToken ct)
    {
        switch (actionId)
        {
            case ActReset:
                await _client.ResetAlarmsAsync(ct).ConfigureAwait(false);
                await Task.Delay(300, ct).ConfigureAwait(false);
                var s = await _client.ReadAllAsync(ct).ConfigureAwait(false);
                Apply(s, _ctx.Clock(), publish: false);
                _ctx.Log?.Invoke("info", $"{InstanceId} 报警复位（寄存器 46 写 1），复位后{(s.AnyAlarm ? $"仍有报警：{s.AlarmText()}" : "无报警")}");
                return s.AnyAlarm ? $"已写复位，但报警还在：{s.AlarmText()}——原因没排除（压力还高 / 内部通信还断）" : "已复位，报警清了";
            case ActSilence:
                await _client.SilenceAlarmsAsync(ct).ConfigureAwait(false);
                return "已消音（寄存器 47 写 1）";
            case ActStopAll:
            {
                var did = new List<string>();
                foreach (var pump in new[] { 1, 2 })
                {
                    try { await PumpStopAsync(pump, ct, "面板全部停止").ConfigureAwait(false); PumpOfInternal(pump).CancelDose(); did.Add($"泵 {pump} 停"); }
                    catch (Exception ex) { did.Add($"泵 {pump} 没停：{ex.Message}"); }
                }
                try { await _client.SetMfcAsync(false, ct).ConfigureAwait(false); did.Add("MFC 停"); }
                catch (Exception ex) { did.Add($"MFC 没停：{ex.Message}"); }
                return string.Join("、", did);
            }
            default: throw new ArgumentException($"没有这个动作：{actionId}", nameof(actionId));
        }
    }

    /// <summary>一台计量泵的加料能力。定量：记起点累计量与要送的体积，每拍核累计量，送够就停。</summary>
    private sealed class Pump : IDosing
    {
        private readonly FeedSystemSession _s;
        private readonly Broadcast<Sample> _flow = new();
        private readonly Broadcast<Sample> _total = new();
        private double _startTotal;
        private double _remaining = double.PositiveInfinity;
        private bool _stopping;

        public Pump(FeedSystemSession s, int number, int channel, double maxRate, double maxVolume, string material, DateTimeOffset opened)
        {
            _s = s; Number = number; Channel = channel; Material = material;
            Limits = new FlowLimits(0, maxRate, maxVolume);
            // 计量泵自带流量闭环、累计量由进料系统 PLC 报——不需要本程序标定，照实写进标定记录（校验器据此放行）
            Calibration = new CalibrationRecord("计量泵自带", opened, "设备自带", "进料系统 PLC 报的瞬时流量与累计量，本程序不另标定", 1.0, null);
        }

        public int Number { get; }
        public int Channel { get; }
        public string Material { get; }
        public FlowLimits Limits { get; }
        public CalibrationRecord? Calibration { get; }
        public double TotalVolume { get; private set; }
        public IObservable<Sample> Flow => _flow;
        public IObservable<Sample> Total => _total;
        /// <summary>正在定量送（有目标体积）。</summary>
        public bool Dosing => !double.IsPositiveInfinity(_remaining);
        /// <summary>这一趟到现在送了多少。</summary>
        public double TripVolume => Math.Max(0, TotalVolume - _startTotal);

        public async Task DoseAsync(DoseRequest request, CancellationToken ct)
        {
            var rate = Math.Clamp(request.RatePerMin, Limits.Min, Limits.Max);
            _startTotal = _s._last?.PumpTotal(Number) ?? TotalVolume;
            _remaining = request.Volume > 0 ? request.Volume : double.PositiveInfinity;
            _stopping = false;
            await _s.PumpStartAsync(Number, rate, ct).ConfigureAwait(false);
            _s._ctx.Log?.Invoke("info", $"{_s.InstanceId} 泵 {Number} 定量加料 {request.Volume:0.##} mL @ {rate:0.##} mL/min（{request.Material ?? Material}），起点累计 {_startTotal:0.##} mL");
        }

        public async Task SetRateAsync(double ratePerMin, CancellationToken ct)
        {
            var rate = Math.Clamp(ratePerMin, Limits.Min, Limits.Max);
            if (rate <= 0) { await StopAsync(ct).ConfigureAwait(false); return; }
            // 没在定量时改流量 = 开着按这个流量送（pH 反馈加料那条路只给流量不给量）
            var on = _s._last?.PumpOn(Number) ?? false;
            if (on) await _s.PumpSetRateAsync(Number, rate, ct).ConfigureAwait(false);
            else { _remaining = double.PositiveInfinity; _startTotal = _s._last?.PumpTotal(Number) ?? TotalVolume; await _s.PumpStartAsync(Number, rate, ct).ConfigureAwait(false); }
        }

        public async Task StopAsync(CancellationToken ct)
        {
            _remaining = double.PositiveInfinity;
            await _s.PumpStopAsync(Number, ct).ConfigureAwait(false);
        }

        public void CancelDose() => _remaining = double.PositiveInfinity;

        public void Update(FeedSnapshot s, DateTimeOffset at, bool publish)
        {
            TotalVolume = s.PumpTotal(Number);
            if (!publish) return;
            _flow.Push(new Sample(Channel, FeedSystemDriver.TagFlow, at.UtcTicks, at, Math.Round(s.PumpFlow(Number), 3), Quality.Good));
            _total.Push(new Sample(Channel, FeedSystemDriver.TagVol, at.UtcTicks, at, Math.Round(TotalVolume, 3), Quality.Good));
        }

        /// <summary>每拍核：定量目标送够了就停泵（只停一次）。</summary>
        public async Task CheckDeliveredAsync(FeedSnapshot s, CancellationToken ct)
        {
            if (!Dosing || _stopping) return;
            if (s.PumpTotal(Number) - _startTotal < _remaining) return;
            _stopping = true;
            var delivered = s.PumpTotal(Number) - _startTotal;
            _remaining = double.PositiveInfinity;
            try { await _s.PumpStopAsync(Number, ct, $"送够 {delivered:0.##} mL").ConfigureAwait(false); }
            catch (Exception ex) { _s._ctx.Log?.Invoke("error", $"{_s.InstanceId} 泵 {Number} 送够了却停不下来：{ex.Message}——到面板点「全部停止」"); }
        }
    }
}

/// <summary>
/// 配方「加料」在进料系统上的认领：步骤里的「加料泵 1 / 加料泵 2」就是这台的泵 1 / 泵 2（不看通道上挂的是哪台）；
/// 「一次加入」按泵的流量上限送；等到进料系统报的累计量到了（泵自己每拍核、送够就停），再停一次兜底。
/// </summary>
internal sealed class FeedDoseHandler : ICommandHandler
{
    private readonly FeedSystemSession _s;
    public FeedDoseHandler(FeedSystemSession s) => _s = s;

    public async Task<CommandOutcome> ExecuteAsync(CommandContext ctx, CommandInput p, CancellationToken ct)
    {
        var which = p.Str("pump", "加料泵 1").Contains('2') ? 2 : 1;
        var pump = _s.PumpOf(which);
        var volume = Math.Max(0, p.Num("vol"));
        var rate = CommandSpecs.DoseAtOnce(p) ? pump.Limits.Max : Math.Clamp(p.Num("rate", 0.5), 0.001, pump.Limits.Max);
        var began = ctx.Now();
        var startTotal = pump.TotalVolume;

        await pump.DoseAsync(new DoseRequest(volume, rate) { Material = p.Str("liq") }, ct).ConfigureAwait(false);
        var planned = TimeSpan.FromSeconds(volume / rate * 60.0);
        var reached = await DriverTime.PollAsync(() => pump.TotalVolume - startTotal >= volume - 1e-6,
                                                 planned + TimeSpan.FromMinutes(5), ctx.TimeScale, ctx.Now, ct).ConfigureAwait(false);
        try { await pump.StopAsync(ct).ConfigureAwait(false); } catch { /* 泵自己送够已停，这一下只是兜底 */ }

        var actual = pump.TotalVolume - startTotal;
        return new CommandOutcome(reached ? EndReason.QuantityDelivered : EndReason.Timeout, ctx.Now() - began)
        {
            Note = reached ? $"泵 {which} 实加 {actual:F2} mL" : $"泵 {which} 超时：计划 {volume:F2} mL，实加 {actual:F2} mL（进料系统报的累计量没到，查泵有没有在转、压力报警）"
        };
    }
}
