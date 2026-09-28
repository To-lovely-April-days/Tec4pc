using TecControl.Core.Control;
using TecControl.Core.Models;
using TecControl.Core.Protocol;
using Tec.Driver.Abi;

namespace Tec.Drivers.Rd105;

/// <summary>
/// 一台 RD105 温控器 = **两个工位的夹套回路**：TC1 = 工位 A，TC2 = 工位 B，
/// 每路探头测的都是夹套 Tj（docs/双工位反应主机驱动需求.md §3）。
/// 釜内 Tr / pH 不在这台设备上——宇电采集模块另采，由组合会话拼进来。
///
/// 两种控温方式（设备配置「控温方式」，见 Rd105TemperatureControl）：
/// · 温控器 PID：写 TG + SPEED + TCENABLE，会话自己开轮询收温度、告警、输出；
/// · 上位机 PID：TecControl.Core 那套 HostControlLoop 跑在这里——两路各自独立（Independent），
///   每拍读快照 → 算 PID → 写占空比（MODE=3）；快照 / 告警 / 电流 / 输出都从回路的周期回调拿，
///   不再另开轮询（串口上一条链两个人问只会互相挤）。串级外环吃组合会话喂进来的 Tr。
///   增益表按路落盘（%AppData%\TecDrivers\gains\），自整定登记 / 学到稳态偏置就存。
/// </summary>
public sealed class Rd105Session : IDeviceSession, IDeviceSettings
{
    private readonly Rd105Link _link;
    private readonly DriverContext _ctx;
    private readonly Rd105Settings _settings;
    private readonly Broadcast<Sample> _out = new();
    private readonly Rd105TemperatureControl[] _temps = new Rd105TemperatureControl[2];
    private readonly Rd105Tuning[] _tunings = new Rd105Tuning[2];
    /// <summary>上位机 PID 的整定台（面板「PID 整定」页）；温控器 PID 方式下没有。</summary>
    private readonly Rd105HostPid?[] _pids = new Rd105HostPid?[2];
    /// <summary>每次控温的逐拍记录（上位机方式）；温控器 PID 方式下没有拍子，不记。</summary>
    private readonly Rd105LoopRecorder?[] _recorders = new Rd105LoopRecorder?[2];
    private long _loadWarnedAt = long.MinValue / 2;
    private readonly HostControlLoop _loop;
    private readonly TimeSpan _period;
    private readonly bool _host;
    private readonly bool _invert;
    private readonly int _heaterSign;
    private readonly string _fpwmWant;
    private readonly string[] _gainPaths = new string[2];
    private DeviceState _state = DeviceState.Connected;
    private TecErrorCode _fault = TecErrorCode.None;
    private int _dutyBusy;
    private long _dutyAt;

    public Rd105Session(Rd105Link link, DriverContext ctx, ParameterSet connection, Rd105HostDefaults? defaults = null)
    {
        _link = link;
        _ctx = ctx;
        _period = TimeSpan.FromMilliseconds(
            Math.Clamp(connection.Num(Rd105TecDriver.FieldPeriod, 500), 200, 5000));
        defaults ??= Rd105HostDefaults.Standalone;
        _host = defaults.HostOf(ctx.Config);
        _invert = defaults.InvertOf(ctx.Config);
        _heaterSign = defaults.HeaterSignOf(ctx.Config);
        _fpwmWant = defaults.FpwmOf(ctx.Config);

        // 主机侧控制环：继电器法自整定、增益调度在它里面。温控器 PID 方式下只建不启（只有整定用得上）；
        // 上位机方式下 StartAsync 把它跑起来，控温、采集都走它
        _loop = new HostControlLoop(link.Controller)
        {
            Period = _period,
            SyncMode = ChannelSyncMode.Independent,      // 两路各自独立：TC1 = 工位 A、TC2 = 工位 B
            ManagePwmFrequency = false,                  // FPWM 是设备全局的，两路可能各在一侧，不由回路改
            MaxConsecutiveFailures = 20,                 // 连着 20 拍（约 10 s）读不到才停控：串口抖一下由 LinkRecovery 重开
            // 加热棒 / 只制冷的 TEC 整定时，回头那半周靠自然散热 / 回温，一个半周可能十几二十分钟：给 4 h
            AutoTuneTimeout = TimeSpan.FromHours(4),
            // 串级丢了釜内 Tr 先保持（外环冻结、夹套设定钳到釜内设定）再停控；连接参数可调，0 = 当拍停控
            OuterLossGrace = TimeSpan.FromSeconds(Math.Clamp(connection.Num(Rd105TecDriver.FieldTrGrace, 30), 0, 120))
        };

        for (var i = 0; i < 2; i++)
        {
            var ch = ctx.ChannelNumbers.Count > i ? ctx.ChannelNumbers[i] : i;
            _temps[i] = new Rd105TemperatureControl(ch, tc: i + 1, link, ctx.Config, _out, _host ? _loop : null);
            var tc = i + 1;
            var temp = _temps[i];   // 别在 lambda 里捕获 for 的循环变量：循环跑完它是 2，调用时 _temps[2] 越界
            _tunings[i] = new Rd105Tuning(ch, tc, _loop, (lvl, text) => ctx.Log?.Invoke(lvl, text),
                                          canCascade: _host ? () => !double.IsNaN(temp.CurrentReactor) : null);
            _loop.ConfigurePid(tc, Rd105HostControl.FallbackInner.Kp, Rd105HostControl.FallbackInner.Ki,
                               Rd105HostControl.FallbackInner.Kd, 90, _invert, Rd105HostControl.InnerDerivativeFilterSeconds);
            _loop.ConfigureCascade(tc, Rd105HostControl.FallbackOuter.Kp, Rd105HostControl.FallbackOuter.Ki,
                                   Rd105HostControl.FallbackOuter.Kd, Rd105HostControl.FallbackOuterMaxBiasC);
            _loop.SetHeaterSign(tc, _heaterSign);
            _gainPaths[i] = Rd105HostControl.GainsPath(ctx.InstanceId, tc);
            if (_host)
            {
                // 整定台：两张表（TEC / 加热棒）+ 手动参数 / 调度开关，开会话时从盘上读
                var pid = new Rd105HostPid(ch, tc, _loop, _temps[i], ctx.InstanceId, (lvl, text) => ctx.Log?.Invoke(lvl, text));
                _pids[i] = pid;
                _recorders[i] = new Rd105LoopRecorder(ctx.InstanceId, tc, (lvl, text) => ctx.Log?.Invoke(lvl, text));
                _temps[i].Recorder = _recorders[i];
                var said = pid.Load();
                ctx.Log?.Invoke("info", $"{ctx.InstanceId} TC{tc} 上位机 PID：{said}（{Rd105HostControl.GainsDir}）" +
                    $"；TEC 输出{(_invert ? "反向" : "不反向")}、加热棒占空比{(_heaterSign < 0 ? "负" : "正")}");
            }
            else
            {
                // 温控器 PID 方式下只有整定借用回路；表照样读，切到上位机方式时就有
                Rd105HostControl.Load(_loop.GetGainSchedule(tc), _gainPaths[i]);
            }
        }

        if (_host)
        {
            _loop.CycleCompleted += OnCycle;
            _loop.CycleFaulted += OnFaulted;
            _loop.ChannelTripped += OnChannelTripped;
            _loop.ChannelNotice += OnChannelNotice;
            _loop.SaturationWarning += msg => _ctx.Log?.Invoke("warn", $"{InstanceId} {msg}");
            _loop.GainScheduleChanged += SaveGains;
        }
        else
        {
            _link.Controller.SnapshotReceived += OnSnapshot;
            _link.Controller.ErrorCodeReceived += OnErrorCode;
            _link.Controller.PollFaulted += OnFaulted;
            _loop.GainScheduleChanged += SaveGains;        // 整定登记的表也要存，下次切到上位机方式就有
            // 温控器 PID 方式下自整定借用回路（它会把 MODE 写成 3）：整定完把 MODE 写回 0，
            // 不然之后写 TG 温控器也不动——它还在等通信占空比
            _loop.AutoTuneFinished += o => _ = RestoreDeviceModeAsync(o.Channel);
        }
        _recover = new LinkRecovery($"{ctx.InstanceId} RD105", link.Reopen, (l, t) => _ctx.Log?.Invoke(l, t));

        // 参数面板（IDeviceSettings）：温控器自己的寄存器——最大功率、两路电流、PID、自整定……
        _settings = new Rd105Settings(link, ctx.Config, ctx.Log, hostLoop: _host)
        {
            MaxDutyWritten = (tc, pct) =>
            {
                if (!_host) return;
                _loop.SetMaxDuty(tc, pct);
                if (_pids[tc - 1] is { } pid) pid.Limited = pct;
                _ctx.Log?.Invoke("info", $"{InstanceId} TC{tc} 最大输出 LIMITED 改为 {pct} %——回路的输出上限跟着改");
            }
        };
    }

    /// <summary>控温回路在上位机（true）还是温控器自己（false）。</summary>
    public bool HostControlled => _host;

    /// <summary>这一路的增益表文件（测试与日志用）。</summary>
    public string GainsPathOf(int well) => _gainPaths[well];

    /// <summary>这一路的整定台（上位机 PID 方式才有）。组合会话要往上面接热源配合。</summary>
    public Rd105HostPid? PidOf(int well) => well is 0 or 1 ? _pids[well] : null;

    /// <summary>逐拍记录里「extra」那一列由谁填（组合会话：热源在哪一侧）；按工位号问。</summary>
    public Func<int, string?>? LoopExtra { get; set; }

    /// <summary>这一路正在记的控温记录文件（测试 / 日志用）；没在记为 null。</summary>
    public string? LoopLogPathOf(int well) => well is 0 or 1 ? _recorders[well]?.Path : null;

    /// <summary>串口中途掉了之后的自愈（LinkRecovery 的规矩）；轮询连着报错时调。</summary>
    private readonly LinkRecovery _recover;
    private int _pollFails;

    /// <summary>重开过几次口子（测试用）。</summary>
    internal int LinkReopens => _recover.Reopens;

    public string InstanceId => _ctx.InstanceId;

    // ── IDeviceSettings：全部转给 Rd105Settings ──
    public IReadOnlyList<SettingsGroup> Groups => _settings.Groups;
    public IReadOnlyList<SettingsAction> Actions => _settings.Actions;
    public Task<ParameterSet> ReadAsync(string groupId, CancellationToken ct) => _settings.ReadAsync(groupId, ct);
    public Task<IReadOnlyList<string>> WriteAsync(string groupId, ParameterSet values, CancellationToken ct) => _settings.WriteAsync(groupId, values, ct);
    public Task<string> RunAsync(string actionId, CancellationToken ct) => _settings.RunAsync(actionId, ct);

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

    /// <summary>TC1/TC2 各带一个工位。</summary>
    public int WellCount => 2;

    /// <summary>本工位控温能力（组合会话要按工位喂 Tr、拿 Setpoint）。</summary>
    public Rd105TemperatureControl TempOf(int well) => _temps[well];

    public IReadOnlyList<TagDescriptor> Tags { get; } = new[]
    {
        // 这台设备只有夹套。Tr/dT/pH 是组合会话拼上宇电之后的事，
        // 在这里声明就是许了一个兑现不了的数
        new TagDescriptor("Tj", "夹套温度", "℃", DataShape.Scalar)
            { Nominal = new ValueRange(-40, 150) },
        new TagDescriptor("Tset", "设定温度", "℃", DataShape.Scalar)
            { Nominal = new ValueRange(-40, 150) },
        // 控温输出（PWMDUTY，±100 %，正加热负制冷）。放大时最要紧的问题是
        // 「夹套已经满功率还压不住放热」，没有这一路看不出来
        new TagDescriptor("duty", "控温输出", "%", DataShape.Scalar)
            { Nominal = new ValueRange(-100, 100) },
        // 上位机方式下每两秒从温控器读一次的 TEC 电流（回路周期回调里带着）；温控器方式不发
        new TagDescriptor("cur", "TEC 电流", "A", DataShape.Scalar)
            { Nominal = new ValueRange(0, 20) },
        // 串级时外环算出来的夹套设定值（釜内控温时夹套实际在追的数）；单环时 = Tset
        new TagDescriptor("Tjset", "夹套设定（串级）", "℃", DataShape.Scalar)
            { Nominal = new ValueRange(-40, 150) },
        // 设备告警字。安全层盯着它：非 0 即告警，> 0 就该动作。
        // 发成一路采样而不是另开一条通道，是因为安全层本来就是按采样求值的，
        // 顺带还能进记录、能画在时间轴上——告警什么时候出现的一目了然
        new TagDescriptor("fault", "设备告警字", "", DataShape.State)
            { Nominal = new ValueRange(0, 0) }
    };

    public IReadOnlyList<ICapability> CapabilitiesOf(int well)
        => well is 0 or 1
            ? (_pids[well] is { } pid
                ? new ICapability[] { _temps[well], _tunings[well], pid }
                : new ICapability[] { _temps[well], _tunings[well] })
            : Array.Empty<ICapability>();

    /// <summary>温度指令认领 ABI 的能力通用执行器（与仿真同一份语义）。</summary>
    public ICommandHandler? Resolve(string commandId) => CapabilityCommands.Resolve(commandId);

    /// <summary>把两路保护值都写进设备。OpenAsync 里调，早于任何控温动作。</summary>
    public async Task ApplyProtectionAsync(CancellationToken ct)
    {
        foreach (var t in _temps) await t.ApplyProtectionAsync(ct).ConfigureAwait(false);
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _link.Open();

        // 先读一次告警字再开轮询。轮询循环是「先取快照、后读告警」，
        // 不先读的话第一帧温度是在不知道有没有告警的情况下发出去的——
        // 传感器已经越限了却发成 Good，安全层就漏掉了第一拍。
        try { OnErrorCode(await _link.Controller.ReadErrorCodeAsync(ct).ConfigureAwait(false)); }
        catch (Exception ex) { _ctx.Log?.Invoke("warn", $"{InstanceId} 初次读告警字失败：{ex.Message}"); }

        // PWM 频率档（FPWM，设备全局、两路共用）按台面配置写；「不改」= 用温控器里存的
        var fpwmWant = _fpwmWant;
        var fpwmLevel = Array.IndexOf(Rd105TecDriver.FpwmOptions, fpwmWant) - 1;
        if (fpwmLevel >= 0)
        {
            try
            {
                await _link.Controller.SetPwmFrequencyAsync(fpwmLevel, ct).ConfigureAwait(false);
                _ctx.Log?.Invoke("info", $"{InstanceId} 温控器 PWM 输出频率 FPWM 已按台面配置写成 {fpwmWant}（两路共用）");
            }
            catch (Exception ex) { _ctx.Log?.Invoke("warn", $"{InstanceId} 写 FPWM = {fpwmWant} 失败：{ex.Message}——用温控器里存的"); }
        }

        if (_host)
        {
            // 回路的输出上限 = 温控器自己的 LIMITED（最大输出占空比）：读不到按 90
            for (var tc = 1; tc <= 2; tc++)
            {
                try
                {
                    var cfg = await _link.Controller.ReadChannelConfigAsync(tc, ct).ConfigureAwait(false);
                    var limited = Math.Clamp(cfg.MaxDutyPercent, 5, 100);
                    // 手动那组照整定台读回来的（上次存的 / 缺省），这里只换输出上限与方向
                    var m = _pids[tc - 1]?.Manual.Inner ?? new PidTuning(Rd105HostControl.FallbackInner.Kp,
                        Rd105HostControl.FallbackInner.Ki, Rd105HostControl.FallbackInner.Kd);
                    _loop.ConfigurePid(tc, m.Kp, m.Ki, m.Kd, limited, _invert, Rd105HostControl.InnerDerivativeFilterSeconds);
                    if (_pids[tc - 1] is { } pid) pid.Limited = limited;
                }
                catch (Exception ex)
                {
                    _ctx.Log?.Invoke("warn", $"{InstanceId} TC{tc} 读 LIMITED 失败：{ex.Message}——回路输出上限按 90 %");
                }
                // 启动电压（FDEADV / BDEADV）：不为 0 的话温控器会在写进去的占空比上垫一层——写 −4.41 % 回显 −6.43 % 就是它
                try
                {
                    var fwd = await _link.Client.QueryAsync(tc, "FDEADV", ct).ConfigureAwait(false) / 200.0;
                    var bwd = await _link.Client.QueryAsync(tc, "BDEADV", ct).ConfigureAwait(false) / 200.0;
                    if (fwd != 0 || bwd != 0)
                        _ctx.Log?.Invoke("warn", $"{InstanceId} TC{tc} 启动电压：正向 {fwd:0.###} % / 反向 {bwd:0.###} %——写占空比时温控器会在这上面垫一层" +
                            "（反向那一极是加热棒），回显会比写入多这么多、小输出段控不细；不需要就到参数窗把它设成 0");
                }
                catch (Exception ex) { _ctx.Log?.Invoke("warn", $"{InstanceId} TC{tc} 读启动电压失败：{ex.Message}"); }
            }
            // PWM 频率档是设备全局的，回路不动它（两路可能一路加热棒一路 TEC）；开机说一声，让人知道它在哪一档
            try
            {
                var fpwm = await _link.Controller.ReadPwmFrequencyAsync(ct).ConfigureAwait(false);
                var hz = fpwm switch { 0 => "0.5 Hz", 1 => "1 Hz", 2 => "10 Hz", 3 => "100 Hz", _ => $"档 {fpwm}" };
                _ctx.Log?.Invoke(fpwm >= 2 ? "warn" : "info", $"{InstanceId} 温控器 PWM 输出频率 FPWM = {hz}" + (fpwm >= 2
                    ? "——加热棒接的是过零型固态继电器的话，10 Hz 一个周期只有 5 个市电整周、功率 10 % 一档，到温后会来回擦；" +
                      "建议到参数窗改成 0.5 Hz 或 1 Hz（两路共用一个值）"
                    : ""));
            }
            catch (Exception ex) { _ctx.Log?.Invoke("warn", $"{InstanceId} 读 FPWM 失败：{ex.Message}"); }
            _loop.Start();
        }
        else
        {
            _link.Controller.StartPolling(_period);
        }
        if (State != DeviceState.Faulted) State = DeviceState.Ready;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_host)
        {
            // 停会话 = 回路停、两路输出归零关使能（回路一停没人写占空比，固件那边约一分钟也会自己撤）
            try { await _loop.ShutdownAsync().ConfigureAwait(false); } catch { }
        }
        else
        {
            _link.Controller.StopPolling();
        }
        State = DeviceState.Connected;
    }

    /// <summary>上位机回路每个周期的回调：快照、告警字、电流、两路输出都从这里进采样流。</summary>
    private void OnCycle(ControlCycleResult r)
    {
        OnSnapshot(r.Snapshot);
        if (r.ErrorCode is { } code) OnErrorCode(code);
        var at = DateTimeOffset.Now;
        if (r.Current1A is { } c1) Push(_temps[0].Channel, "cur", c1, at, Quality.Good);
        if (r.Current2A is { } c2) Push(_temps[1].Channel, "cur", c2, at, Quality.Good);
        // 一拍的串口耗时超过周期：dt 拉长、温控器可能答不过来（超时）——五分钟说一次
        if (r.CycleLoad > 1 && Environment.TickCount64 - _loadWarnedAt > 300_000)
        {
            _loadWarnedAt = Environment.TickCount64;
            _ctx.Log?.Invoke("warn", $"{InstanceId} 控制周期 {_period.TotalMilliseconds:0} ms 里串口用了 {r.CycleMs:0} ms（{r.CycleLoad:P0}）——" +
                $"周期跟不上，每拍 dt 拉长、温控器可能答不过来；把连接参数「控制周期」调到 {Math.Ceiling(r.CycleMs / 100) * 100 + 200:0} ms 左右");
        }
        var infos = new[] { r.Ch1, r.Ch2 };
        for (var i = 0; i < 2; i++)
        {
            var info = infos[i];
            _recorders[i]?.Write(info, _temps[i].CurrentReactor, ActuatorName(_loop.GetActuatorMode(i + 1)), r.CycleMs, LoopExtra?.Invoke(i));
            // 曲线上的「控温输出」是温控器回显（实际存下）的那个数；它按自己的规矩改了写入值时，画的是它真出的力
            _temps[i].LastDuty = info.AppliedDutyPercent ?? info.DutyPercent;
            Push(_temps[i].Channel, "duty", _temps[i].LastDuty, at, Quality.Good);
            _pids[i]?.OnCycle(info);
            if (info.Active)
            {
                var inner = info.Cascade ? info.InnerSetpointC : info.SetpointC;
                _temps[i].InnerSetpoint = inner;
                // 串级丢了 Tr 在保持：面板拿这句提示（每拍带秒数）；Tjset 照发——趋势里橙线从偏置那头落到釜内设定就是它
                _temps[i].Holding = info.OuterHoldSeconds is { } held
                    ? $"釜内 Tr 读数丢失 {held:0} s，夹套按 {inner:F1} ℃ 保持（{_loop.OuterLossGrace.TotalSeconds:0} s 内回来接着串级，回不来停控）"
                    : null;
                Push(_temps[i].Channel, "Tjset", inner, at, Quality.Good);
            }
            else _temps[i].Holding = null;
        }
    }

    private async Task RestoreDeviceModeAsync(int tc)
    {
        try
        {
            await _link.Controller.SetModeAsync(tc, 0, CancellationToken.None).ConfigureAwait(false);
            _ctx.Log?.Invoke("info", $"{InstanceId} TC{tc} 自整定结束，输出模式已写回 0（双向，温控器自己的 PID）");
        }
        catch (Exception ex)
        {
            _ctx.Log?.Invoke("error", $"{InstanceId} TC{tc} 自整定后写回 MODE=0 失败：{ex.Message}——温控器 PID 不会动，到参数窗把「输出模式」改回双向");
        }
    }

    private void OnChannelTripped(int tc, string reason)
    {
        _ctx.Log?.Invoke("error", $"{InstanceId} 上位机回路停控：{reason}");
        _temps[tc - 1].OnTripped(reason);
    }

    /// <summary>回路的提示（不是停控）：串级丢了釜内 Tr 进保持 / Tr 回来接着算 / 回显不符。</summary>
    private void OnChannelNotice(int tc, string level, string text)
    {
        _ctx.Log?.Invoke(level, $"{InstanceId} {text}");
        _recorders[tc - 1]?.Note(text);
    }

    private static string ActuatorName(ActuatorMode m) => m switch
    {
        ActuatorMode.HeatOnly => "加热棒",
        ActuatorMode.CoolOnly => "TEC只制冷",
        ActuatorMode.HeaterTec => "加热棒+TEC双向",
        _ => "TEC双向"
    };

    private void SaveGains(int tc)
    {
        // 上位机方式：两张表（TEC / 加热棒）都归整定台存
        if (_pids[tc - 1] is { } pid) { pid.SaveTables(); return; }
        try
        {
            Rd105HostControl.Save(_loop.GetGainSchedule(tc), _gainPaths[tc - 1]);
            _ctx.Log?.Invoke("info", $"{InstanceId} TC{tc} 增益表已保存：{Rd105HostControl.Describe(_loop.GetGainSchedule(tc))}（{_gainPaths[tc - 1]}）");
        }
        catch (Exception ex)
        {
            _ctx.Log?.Invoke("error", $"{InstanceId} TC{tc} 增益表保存失败：{ex.Message}（{_gainPaths[tc - 1]}）");
        }
    }

    /// <summary>
    /// 中止收尾：**关本工位的控温输出，轮询照旧。**
    ///
    /// 只关被点名的工位——A 出事不该把 B 的实验拖下水；两个工位一起停是
    /// 引擎层挨个调的事。轮询不停：釜里还是热的，停下来之后那段自然降温
    /// 曲线照样要记，不然记录在最需要它的那一刻断了。
    /// </summary>
    public async ValueTask<IReadOnlyList<string>?> SafeStopAsync(int well, CancellationToken ct)
    {
        if (well is not (0 or 1)) return Array.Empty<string>();
        await _temps[well].StopAsync(ct).ConfigureAwait(false);
        return new[]
        {
            $"已关闭工位 {(well == 0 ? "A" : "B")}（TC{well + 1}）控温输出" +
            $"（夹套停在 {_temps[well].CurrentJacket:F1} ℃，此后自然冷却；采集不停）"
        };
    }

    private void OnSnapshot(TecSnapshot s)
    {
        var at = DateTimeOffset.Now;
        if (_pollFails > 0)
        {
            _ctx.Log?.Invoke("info", $"{InstanceId} RD105 恢复（轮询失败 {_pollFails} 拍后）");
            _pollFails = 0;
            _recover.Ok();
            if (State == DeviceState.Faulted) State = DeviceState.Ready;
        }
        _temps[0].Observe(s.Temp1C);
        _temps[1].Observe(s.Temp2C);

        // 传感器越限时这一路的读数不可信，发成 Bad——安全层见 Bad 就触发。
        // 「读不到值当作正常」是最危险的失败模式（§7.5）
        var q1 = _fault.HasFlag(TecErrorCode.Ch1SensorOutOfRange) ? Quality.Bad : Quality.Good;
        var q2 = _fault.HasFlag(TecErrorCode.Ch2SensorOutOfRange) ? Quality.Bad : Quality.Good;

        // NaN = 该路没接传感器。宁可不发，也不要往曲线里塞一个假读数
        Push(_temps[0].Channel, "Tj", s.Temp1C, at, q1);
        Push(_temps[1].Channel, "Tj", s.Temp2C, at, q2);
        if (_temps[0].Setpoint is { } sp1) Push(_temps[0].Channel, "Tset", sp1, at, Quality.Good);
        if (_temps[1].Setpoint is { } sp2) Push(_temps[1].Channel, "Tset", sp2, at, Quality.Good);
        if (!_host) ReadDuty();      // 上位机方式下输出是自己写的，回路回调里直接发
    }

    /// <summary>
    /// 读两路控温输出占空比。
    ///
    /// 不在轮询快照里（快照只有两路温度和输出电压），得单独问 PWMDUTY，
    /// 所以放在轮询回调后面顺手发查询、**不等它**——等的话整个采集线程
    /// 会被串口往返卡住，温度那两路跟着一起晚。
    ///
    /// 上一轮还没回来就跳过这一轮：串口是独占的，堆着问只会越堆越多。
    /// 读不到就什么都不发——曲线上断一截，比塞一个「上次那个值」诚实得多（§9.4）。
    /// </summary>
    private void ReadDuty()
    {
        // 最快一秒一轮。轮询周期可以短到 200 ms，而输出占空比是个慢变量——
        // 跟着每一拍问只是白占串口：那条链上还排着温度和告警字的查询
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _dutyAt) < 1000) return;
        if (Interlocked.Exchange(ref _dutyBusy, 1) == 1) return;
        Interlocked.Exchange(ref _dutyAt, now);
        _ = Task.Run(async () =>
        {
            try
            {
                for (var i = 0; i < 2; i++)
                {
                    var duty = await _link.Controller.ReadDutyPercentAsync(i + 1).ConfigureAwait(false);
                    _temps[i].LastDuty = duty;
                    Push(_temps[i].Channel, "duty", duty, DateTimeOffset.Now, Quality.Good);
                }
            }
            catch (Exception ex)
            {
                _ctx.Log?.Invoke("warn", $"{InstanceId} 读控温输出失败：{ex.Message}");
            }
            finally { Interlocked.Exchange(ref _dutyBusy, 0); }
        });
    }

    /// <summary>
    /// 每个轮询周期一条告警字。硬告警（过温停输出、供电过高过低）把设备标成故障；
    /// 告警字本身两个工位各发一条，安全层按「非 0 即越限」各自处理——
    /// 供电这类全机告警对两个工位都成立，传感器越限的质量位在快照里按路分。
    /// </summary>
    private void OnErrorCode(TecErrorCode code)
    {
        var at = DateTimeOffset.Now;
        var was = _fault;
        _fault = code;
        var texts = code.Describe();
        foreach (var t in _temps)
        {
            t.Faults = texts;
            Push(t.Channel, "fault", (ushort)code, at, Quality.Good);
        }

        if (code != was)
        {
            foreach (var text in texts) _ctx.Log?.Invoke("warn", $"{InstanceId} 告警：{text}");
            if (was != TecErrorCode.None && code == TecErrorCode.None)
                _ctx.Log?.Invoke("info", $"{InstanceId} 告警已解除");
        }

        // 这几条是「已经在损坏或已经停输出」，不是「正在限流」这种可以接着跑的
        var hard = code & (TecErrorCode.OverTempShutdown | TecErrorCode.UnderVoltage
                           | TecErrorCode.OverVoltage);
        if (hard != TecErrorCode.None) State = DeviceState.Faulted;
        else if (State == DeviceState.Faulted && code == TecErrorCode.None) State = DeviceState.Ready;
    }

    private void Push(int channel, string tag, double value, DateTimeOffset at, Quality quality)
    {
        if (double.IsNaN(value)) return;
        _out.Push(new Sample(channel, tag, at.UtcTicks, at, value, quality));
    }

    /// <summary>
    /// 轮询出错不终止轮询（TecController 自己会接着转），但要把设备标成故障——
    /// 界面上得看得出这台机器现在的数据不可信。
    /// </summary>
    private void OnFaulted(Exception ex)
    {
        // 连着失败只报第一次（从前每拍一条，一分钟刷一百多行）；口子死了就重开（LinkRecovery）
        if (_pollFails++ == 0)
            _ctx.Log?.Invoke("error", $"{InstanceId} 轮询异常：{ex.Message}（连续失败只报第一次）");
        State = DeviceState.Faulted;
        _recover.Failed(ex);
    }

    public async ValueTask DisposeAsync()
    {
        _link.Controller.SnapshotReceived -= OnSnapshot;
        _link.Controller.ErrorCodeReceived -= OnErrorCode;
        _link.Controller.PollFaulted -= OnFaulted;
        _loop.CycleCompleted -= OnCycle;
        _loop.CycleFaulted -= OnFaulted;
        _loop.ChannelTripped -= OnChannelTripped;
        _loop.ChannelNotice -= OnChannelNotice;
        _loop.GainScheduleChanged -= SaveGains;
        foreach (var t in _tunings) t.Detach();
        foreach (var p in _pids) p?.Detach();
        foreach (var rec in _recorders) rec?.Dispose();
        try { await _loop.ShutdownAsync().ConfigureAwait(false); } catch { }
        _loop.Dispose();
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _out.Complete();
        _link.Dispose();
        State = DeviceState.Disposed;
    }
}
