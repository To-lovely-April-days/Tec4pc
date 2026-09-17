using System.Threading;
using Tec.Driver.Abi;
using Tec.Drivers.Rd105;
using F = Tec.Drivers.DualStation.DualStationDriver.Fields;

namespace Tec.Drivers.DualStation;

/// <summary>
/// 双工位反应主机的组合会话：一台设备、两个通道（需求 §1）。
///
/// 里面套着一个 Rd105Session 干夹套控温和 Tj/Tset/duty/fault 的采集（原样转发），
/// 自己再开一个采集循环轮 IO8R（热源切换状态与回切）。**釜内 Tr 不归它采**——
/// Tr 是独立的宇电探头设备发的，工作台在会话都开起来之后把带 Tr 签的采样按
/// 通道喂进来（IExternalReactorTemp），主机拿它做判到达、E 级程序和 dT。
///
/// 热源切换（需求 §2）分两种模式，看设备配置「TEC 加热」：
///
///  **不启用（默认）**：TEC 只当冷源，**所有加热都走电加热棒**。继电器按
///  「这一刻该升温还是该降温」切：目标高出夹套一个死区就切电加热，低一个死区就回 TEC，
///  死区之内（已到达 / 恒温）保持现状不折腾继电器。阈值这时不参与切入判断。
///
///  **启用**：TEC 反向输出也能加热，于是只有目标 &gt; 阈值才需要电加热棒（原来那套）。
///
/// 两种模式共同的硬条款：
///  · 切回 TEC 必须实测夹套 ≤ 阈值 − 滞回——夹套还烫着就把 TEC 接回去，
///    等于让它贴着超出耐温的热源；
///  · 每次切换都走全套序列：关输出 → 切继电器 → （核反馈）→ 重开输出；
///  · 电加热用不了（没配 IO8R / 打不开）时，需要加热的目标直接拒绝，理由写明。
/// </summary>
public sealed class DuoSession : IDeviceSession, IExternalReactorTemp
{
    private readonly DuoLinks _links;
    private readonly DriverContext _ctx;
    private readonly Broadcast<Sample> _out = new();
    private readonly Rd105Session _rd;
    private readonly IDisposable _fwd;
    private readonly DuoTempControl[] _temps;
    private readonly SemaphoreSlim _switchLock = new(1, 1);

    // 这两项**每拍重读**（见 RefreshSwitches）：属性栏上的「TEC 加热」是个开关，
    // 人按下去就该生效，不该还要先把设备断开重连。其余配置照旧开会话时读一次
    private bool _tecHeat;                  // TEC 反向输出加热启不启用（默认不启用）
    private double _band;                   // 冷热判定死区（只在 TEC 加热不启用时用）
    private readonly double _threshold;     // 电加热切换阈值（≤ 90，用户定的死上限）
    private readonly double _hyst;          // 回切滞回
    private readonly bool _feedback;        // 有没有接切换反馈 DI
    private readonly int[] _doIdx = new int[2];
    private readonly int[] _diIdx = new int[2];
    private readonly TimeSpan _tick;

    private readonly bool[] _electric = new bool[2];
    /// <summary>上一次切换的时刻。自动切电加热前要等够最短间隔，免得继电器来回抖。</summary>
    private readonly DateTimeOffset[] _switchedAt = { DateTimeOffset.MinValue, DateTimeOffset.MinValue };
    /// <summary>电加热用不了那一段只报一次，别每拍刷屏。</summary>
    private readonly bool[] _noElectricSaid = new bool[2];
    /// <summary>「要降温但夹套还烫着接不回 TEC」那一段同样只报一次。</summary>
    private readonly bool[] _noCoolSaid = new bool[2];
    /// <summary>
    /// 停控代数：每停一次加一。切换序列里 ①关输出 → ②切继电器 → ③核反馈 要走上两秒，
    /// 这期间操作人或安全层完全可能按下「停控」——那时 Enabled 早被 ① 置成 false 了，
    /// 光看它分不出「是我自己关的」还是「别人要停」。记个代数，切换前后对不上就不重开。
    /// </summary>
    private readonly int[] _stopGen = new int[2];

    /// <summary>
    /// **这一路要不要控温的意图**，与设备上的 ENABLE 分开记。
    ///
    /// 切换序列第 ① 步自己会把 ENABLE 关掉，所以「设备上现在是关的」回答不了
    /// 「原本要不要控温」。更要命的是中途失败：② 写继电器超时抛出之后 ENABLE 停在 0，
    /// 下一拍再看 ENABLE 就成了「这一路没人要控温」，于是永远不再把输出接回来——
    /// 一次总线抖动就把通道静悄悄停了。意图只由下发目标 / 停控改写，不受切换过程影响。
    /// </summary>
    private readonly bool[] _wantEnabled = new bool[2];

    /// <summary>下发目标走过这里：这一路是要控温的。</summary>
    internal void NoteStart(int well)
    {
        if (well is 0 or 1) _wantEnabled[well] = true;
    }

    /// <summary>停控（含安全停机）走过这里：让正在进行的切换知道「别再把输出打开」。</summary>
    internal void NoteStop(int well)
    {
        if (well is not (0 or 1)) return;
        _wantEnabled[well] = false;
        System.Threading.Interlocked.Increment(ref _stopGen[well]);
    }
    /// <summary>反馈 DI 与继电器对不上的那一段只报一次，对上了再报一次「已对上」。</summary>
    private readonly bool[] _fbMismatch = new bool[2];

    /// <summary>
    /// 自动切到电加热的最短间隔。**只拦升温方向**——抢冷是安全动作，等不得；
    /// 明确下发目标（配方步 / 面板）也不受它约束，那是人或配方的决定，该立刻照办。
    /// </summary>
    private static readonly TimeSpan MinDwell = TimeSpan.FromSeconds(30);
    private bool _ioOk;
    private int _ioFails;

    // ── 蒸回流（夹套跟随）状态。__refluxGate 拴三样：跟随参数、安全压制旗、
    //    喂 Tr 的时间戳——采集循环与安全停/新目标可能在不同线程上碰它们
    private sealed class Follow
    {
        public double DeltaT;
        public double MaxTj;
    }

    private readonly object _refluxGate = new();
    private readonly Follow?[] _reflux = new Follow?[2];
    /// <summary>安全压制：SafeStop/停控清跟随时立起，跟随环写到一半撞见它要把输出关回去。</summary>
    private readonly bool[] _refluxKill = new bool[2];
    private readonly DateTimeOffset[] _trFedAt = { DateTimeOffset.MinValue, DateTimeOffset.MinValue };

    /// <summary>Tr 新鲜度窗：跟随环只吃这窗内喂过的釜温——探头会话死了残值还在，不能追着残值走。</summary>
    private static readonly TimeSpan TrFreshWindow = TimeSpan.FromSeconds(10);

    private DeviceState _state = DeviceState.Connected;
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;

    public DuoSession(DuoLinks links, DriverContext ctx, ParameterSet connection)
    {
        _links = links;
        _ctx = ctx;
        var cfg = ctx.Config;

        // 默认不启用：现场 TEC 的反向输出加热还没接/还没验，加热一律走电加热棒
        _tecHeat = cfg.Str(F.TecHeat, "不启用") == "启用";
        // 阈值上限 90 是死的（用户定的：只能比 90 小）——表单已经限了，这里再拴一道
        _threshold = Math.Min(90, cfg.Num(F.Threshold, 90));
        _hyst = Math.Clamp(cfg.Num(F.Hysteresis, 5), 2, 20);
        _band = Math.Clamp(cfg.Num(F.Band, 2), 0.5, 10);
        _feedback = cfg.Str(F.Feedback, "无") == "有";
        for (var w = 0; w < 2; w++)
        {
            _doIdx[w] = Math.Clamp((int)cfg.Num(w == 0 ? F.DoA : F.DoB, w), 0, 7);
            _diIdx[w] = Math.Clamp((int)cfg.Num(w == 0 ? F.DiA : F.DiB, w), 0, 7);
        }
        _tick = TimeSpan.FromMilliseconds(Math.Clamp(connection.Num(F.Tick, 1000), 100, 5000));

        // 内层 RD105 会话：夹套控温 + Tj/Tset/duty/fault 采集，原样转发到本会话的流上
        _rd = new Rd105Session(links.Rd105, new DriverContext
        {
            InstanceId = ctx.InstanceId,
            ChannelNumbers = ctx.ChannelNumbers,
            Config = cfg,
            Simulated = false,
            TimeScale = 1,
            Clock = ctx.Clock,
            Log = ctx.Log
        }, connection);
        _fwd = _rd.Samples.Subscribe(new Fwd(_out));
        _rd.StateChanged += (_, st) => { if (st != DeviceState.Disposed) State = st; };

        _temps = new[] { new DuoTempControl(this, 0), new DuoTempControl(this, 1) };
    }

    public string InstanceId => _ctx.InstanceId;

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

    public int WellCount => 2;

    public IReadOnlyList<TagDescriptor> Tags { get; } = new[]
    {
        // Tr 与 pH 不在这里声明——它们是宇电探头设备的签，各归各的会话，
        // 主机再发一份就是同一个量两个来源
        new TagDescriptor("Tj", "夹套温度", "℃", DataShape.Scalar)
            { Nominal = new ValueRange(-40, 180) },
        new TagDescriptor("dT", "Tr−Tj 温差", "℃", DataShape.Scalar)
            { Nominal = new ValueRange(-60, 60) },
        new TagDescriptor("Tset", "设定温度", "℃", DataShape.Scalar)
            { Nominal = new ValueRange(-40, 180) },
        new TagDescriptor("duty", "控温输出", "%", DataShape.Scalar)
            { Nominal = new ValueRange(-100, 100) },
        // 热源：0 = TEC，1 = 电加热（无反馈回路，未核实），2 = 电加热（反馈已核实）。
        // 发成一路状态量，记录里看得出什么时候换的挡、换过去有没有核实（需求 §2.5）
        new TagDescriptor("heat", "热源", "", DataShape.State)
            { Nominal = new ValueRange(0, 2) },
        new TagDescriptor("fault", "设备告警字", "", DataShape.State)
            { Nominal = new ValueRange(0, 0) }
    };

    public IReadOnlyList<ICapability> CapabilitiesOf(int well)
        => well is 0 or 1
            ? new ICapability[] { _temps[well] }
                .Concat(_rd.CapabilitiesOf(well).OfType<ITemperatureTuning>()).ToArray()
            : Array.Empty<ICapability>();

    /// <summary>温度指令认领 ABI 的能力通用执行器——与仿真同一份语义，
    /// 仿真调好的配方插上真机能跑。搅拌/加料这台没有，认不了不硬认。</summary>
    public ICommandHandler? Resolve(string commandId) => CapabilityCommands.Resolve(commandId);

    internal Rd105TemperatureControl InnerTemp(int well) => _rd.TempOf(well);

    /// <summary>
    /// 工作台喂进来的外部釜温（宇电 Tr 探头会话发的）。质量不好就按 NaN 处置——
    /// 断线残值不能进控制判据；判到达与 E 级程序吃的就是这口饭。
    /// </summary>
    public void FeedReactor(int channel, double value, Quality quality)
    {
        for (var w = 0; w < 2; w++)
        {
            var t = _rd.TempOf(w);
            if (t.Channel == channel)
            {
                t.FeedReactor(quality == Quality.Good ? value : double.NaN);
                lock (_refluxGate) _trFedAt[w] = _ctx.Clock();
            }
        }
    }

    // ── 开机 ─────────────────────────────────────────────────────────

    /// <summary>
    /// 开机自检：RD105 写两路保护寄存器；IO8R 把切换继电器复位到 TEC 侧
    /// （继电器落回的必须是安全侧），坏了照常开但电加热不可用（需求 §2.6）。
    /// </summary>
    public async Task InitAsync(CancellationToken ct)
    {
        await _rd.ApplyProtectionAsync(ct).ConfigureAwait(false);

        // 先把两路输出关掉。RD105 固件没有通信看门狗（注释见 Rd105TemperatureControl），
        // 上位机重启 / 台面重开的时候设备很可能还在按上一炉的目标输出着——
        // 那时去扳继电器就是带载切换（触点拉弧），而且 Enabled 影子一开始是 false，
        // 跟设备对不上，热源状态机会把一路正在加热的通道当成「停控」处理
        for (var w = 0; w < 2; w++)
        {
            try { await _rd.TempOf(w).EnableAsync(false, ct).ConfigureAwait(false); }
            catch (Exception ex)
            {
                _ctx.Log?.Invoke("warn", $"{InstanceId} 开机关闭工位 {AB(w)} 控温输出失败：{ex.Message}");
            }
        }

        RefreshSwitches();
        _ctx.Log?.Invoke("info", $"{InstanceId} 热源策略：「TEC 加热」{(_tecHeat ? "启用" : "不启用")}" +
            (_tecHeat
                ? $"——目标高于 {_threshold:F0} ℃ 才切电加热"
                : $"——TEC 只当冷源，升温一律走电加热棒（死区 {_band:F1} K）") +
            $"；回切线 {_threshold - _hyst:F0} ℃（阈值 {_threshold:F0} − 滞回 {_hyst:F0}）");

        _ioOk = false;
        if (_links.Io is { } io)
        {
            try
            {
                await io.AllOffAsync(ct).ConfigureAwait(false);
                _electric[0] = _electric[1] = false;
                _ioOk = true;
                _ctx.Log?.Invoke("info", $"{InstanceId} IO8R 已把热源切换继电器复位到 TEC 侧");
            }
            catch (Exception ex)
            {
                _ctx.Log?.Invoke("warn", $"{InstanceId} IO8R 打不开：{ex.Message}——照常开机，但电加热不可用，" +
                    (_tecHeat
                        ? $"目标超过 {_threshold:F0} ℃ 的下发会被拒绝"
                        : "而「TEC 加热」没启用——这台现在只降得下去、升不上来，需要升温的下发都会被拒绝"));
            }
        }
        else if (!_tecHeat)
        {
            // 没配 IO8R + TEC 加热不启用 = 这台机器一点温都升不了。
            // 这是开机就能算出来的事实，不该等到配方第一步升温时才炸出来
            _ctx.Log?.Invoke("warn", $"{InstanceId} 这台没配 IO8R 切换模块，而「TEC 加热」没启用" +
                "——本机当前只能制冷，任何升温的下发都会被拒绝。" +
                "要升温：接上 IO8R 切换模块，或在设备属性里把「TEC 加热」打开");
        }
    }

    public async Task StartAsync(CancellationToken ct)
    {
        await _rd.StartAsync(ct).ConfigureAwait(false);
        _pollCts = new CancellationTokenSource();
        _pollTask = Task.Run(() => PollLoopAsync(_pollCts.Token), CancellationToken.None);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _pollCts?.Cancel();
        if (_pollTask is { } t) { try { await t.ConfigureAwait(false); } catch { } }
        _pollTask = null;
        await _rd.StopAsync(ct).ConfigureAwait(false);
    }

    // ── 采集循环（dT + IO8R；RD105 的轮询在内层会话里自己转） ───────

    private async Task PollLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_tick);
        while (!ct.IsCancellationRequested)
        {
            try { if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) break; }
            catch (OperationCanceledException) { break; }
            try { await PollOnceAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>跑一拍。单拎出来是为了回归测试能一拍一拍地推，不靠真时钟。</summary>
    internal async Task PollOnceAsync(CancellationToken ct)
    {
        var at = _ctx.Clock();
        RefreshSwitches();

        // dT：外部喂进来的 Tr − 本机的 Tj。两头都有才发，缺哪头都不编
        for (var w = 0; w < 2; w++)
        {
            var t = _rd.TempOf(w);
            if (!double.IsNaN(t.CurrentReactor) && !double.IsNaN(t.CurrentJacket))
                Push(t.Channel, "dT", t.CurrentReactor - t.CurrentJacket, at, Quality.Good);
        }

        // 蒸回流跟随环：每拍把夹套目标追到 Tr+ΔT（钳上限、限速写、必要时切热源）
        for (var w = 0; w < 2; w++)
            await FollowOnceAsync(w, ct).ConfigureAwait(false);

        // IO8R：读回继电器实际位置跟命令核对，发热源状态，条件满足时回切 TEC
        if (_links.Io is { } io && _ioOk)
        {
            try
            {
                var dos = await io.ReadRelaysAsync(ct).ConfigureAwait(false);
                // 接了反馈就每拍连 DI 一起读：切换那一刻核实过不算完——接触器中途释放
                // （电加热其实断了）或粘连（继电器已断它还吸着）只有持续盯着才看得见
                var dis = _feedback ? await io.ReadInputsAsync(ct).ConfigureAwait(false) : null;
                if (_ioFails > 0) { _ctx.Log?.Invoke("info", $"{InstanceId} IO8R 恢复"); _ioFails = 0; }
                // 正在切换的那几十毫秒里读回来的是**切换前**的快照：拿它跟已经改过的
                // _electric 比，会冒出假的「有人手扳了模块」告警，还会把 heat 记反一拍。
                // 与 HeatStateOf 用同一道守卫：锁被占着就不比对
                var settled = _switchLock.CurrentCount > 0;
                for (var w = 0; w < 2; w++)
                {
                    var actual = dos[_doIdx[w]];
                    if (settled && actual != _electric[w])
                    {
                        // 模块上有按键，人手也能扳——以实际为准，不跟自己的想象过日子
                        _ctx.Log?.Invoke("warn", $"{InstanceId} 工位 {AB(w)} 切换继电器实际在" +
                            $"{(actual ? "电加热" : "TEC")}侧，与上位机命令不符——已按实际状态记录");
                        _electric[w] = actual;
                    }
                    else if (!settled)
                    {
                        actual = _electric[w];      // 切换进行中，以命令为准，别用陈旧快照落记录
                    }
                    Push(_rd.TempOf(w).Channel, "heat", HeatStateOf(w, actual, dis), at, Quality.Good);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                if (_ioFails++ == 0)
                    _ctx.Log?.Invoke("warn", $"{InstanceId} IO8R 读失败：{ex.Message}（连续失败只报第一次）");
            }
        }

        // 自动换挡**不放在上面那个 IO8R 守卫块里**：没配 / 没打开 IO8R 的机器
        // 恰恰最需要那句「要升温但电加热用不了」的提醒，放进去就永远打不出来。
        // 真要扳继电器的时候它自己会看 ElectricReady
        for (var w = 0; w < 2; w++)
            await AutoSourceAsync(w, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 采集循环里的自动换挡。两件事：**回 TEC**（原来就有：目标不再要电加热且夹套凉够了）
    /// 与 **切电加热**（TEC 加热不启用时新增：温度自己漂出死区、又需要升温了）。
    ///
    /// 只动「输出开着」的通道：停控 / 安全停机之后输出都关了，扳继电器没有意义，
    /// 还会把安全停机刚落回 TEC 侧的继电器又扳回电加热。
    /// </summary>
    private async Task AutoSourceAsync(int well, CancellationToken ct)
    {
        var innerT = _rd.TempOf(well);

        // 这一刻这一路到底想干什么。**看的是意图（_wantEnabled）而不是设备上的 ENABLE**：
        // 切换序列第 ① 步自己就会把 ENABLE 关掉，拿它当「有没有人要控温」会把
        // 「我刚关的」和「别人要停」混成一件事
        bool? want = null;
        if (_wantEnabled[well] && innerT.Setpoint is { } sp) want = SideFor(well, sp);

        // 「本路暂时没有制冷能力」那条闩：只在**确实卡着**的时候armed，别的时候一律重新上膛。
        // 放热反应「抢冷 → 缓过来 → 再抢冷」是常见序列，第二次不能是静默的
        var stuck = want is false && _electric[well]
                    && (double.IsNaN(innerT.CurrentJacket) || innerT.CurrentJacket > _threshold - _hyst);
        if (!stuck) _noCoolSaid[well] = false;

        bool side;
        // 抢到切换锁之后再问一遍「这一刻还该这么切吗」——这一路的判断是拿上一拍的
        // 状态做的，抢锁期间目标或开关状态都可能已经变了
        Func<bool> stillWanted;
        if (!_wantEnabled[well])
        {
            // 停控 / 安全停机之后：只允许落回 TEC 侧——那是继电器该待的安全位置，
            // 而且输出关着，回 TEC 不带载。绝不自动切到电加热：没人要它加热
            if (!_electric[well]) return;
            side = false;
            stillWanted = () => !_wantEnabled[well];
        }
        else
        {
            if (want is not { } w || w == _electric[well]) return;
            side = w;
            stillWanted = () => _wantEnabled[well] && _rd.TempOf(well).Setpoint is { } now
                                && SideFor(well, now) == side;
        }

        if (side)
        {
            if (!ElectricReady)
            {
                if (!_noElectricSaid[well])
                {
                    _noElectricSaid[well] = true;
                    _ctx.Log?.Invoke("warn", $"{InstanceId} 工位 {AB(well)} 需要升温但" +
                        NoElectricReason(well, innerT.Setpoint ?? double.NaN) + "（这一段只报一次）");
                }
                return;
            }
            _noElectricSaid[well] = false;
            // 升温方向留一道最短间隔：目标在死区边上抖的时候，别让继电器跟着抖。
            // 制冷方向不设这道闸——抢冷是安全动作
            if (_ctx.Clock() - _switchedAt[well] < MinDwell) return;
        }
        else if (!ElectricReady)
        {
            return;     // 没有继电器可扳；没有 IO8R 的机器本来就一直在 TEC 侧
        }
        // 「夹套凉到阈值 − 滞回 才准接回 TEC」只管**有人要控温**的情形：那条约束防的是
        // 让 TEC 带着载贴上超出耐温的热源。没人要控温的通道（停控 / 安全停机）输出是关的、
        // 不带载，落回 TEC 侧才是继电器该待的位置——SafeStopAsync 在同样的状态下就是
        // 直接断的，两条路得说同一句话
        else if (stuck)
        {
            // 这一段本路是没有制冷能力的：继电器在电加热侧，而 TEC 又接不回来。
            // 放热反应中途撞上这个，人得知道——按段只报一次
            if (!_noCoolSaid[well])
            {
                _noCoolSaid[well] = true;
                _ctx.Log?.Invoke("error", $"{InstanceId} 工位 {AB(well)} 要降温，但夹套 " +
                    $"{innerT.CurrentJacket:F1} ℃ 还高于回切线 {_threshold - _hyst:F0} ℃（阈值 − 滞回）——" +
                    "这一段只能等它自然凉，本路暂时没有制冷能力（这一段只报一次）");
            }
            return;
        }

        try
        {
            await SwitchAsync(well, side, ct, stillWanted).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // 别写「下拍再试」：反馈没核实上时 SwitchAsync 是把输出留在关闭状态退出的，
            // 下一拍这一路已经算「停控」，不会再自己切回来——这是要人去现场看的事
            _ctx.Log?.Invoke("error", $"{InstanceId} 工位 {AB(well)} " +
                $"自动切到{(side ? "电加热" : "TEC")}失败：{ex.Message}" +
                "——控温输出已关闭且不会自行恢复，请到现场检查");
        }
    }

    // ── 蒸回流（夹套跟随，需求 §2 + 0277 §B.5）────────────────────────
    //
    // 跟随环长在采集循环里：每拍读一次喂进来的 Tr，把夹套目标写成
    // min(Tr + ΔT, maxTj)，限速走 RampAsync（SPEED 限速写 TG，不是阶跃）。
    // 四道联锁：① Tr 无效/超时 → 停跟随（目标驻留，不追残值）；
    // ② 目标越过电加热阈值 → EnsureSourceAsync 全套切换序列；
    // ③ 安全动作（SafeStop / 停控温）→ 清跟随 + 压制旗，写到一半撞见就把
    //    输出关回去——下一拍绝不再把目标写回去；
    // ④ maxTj 之外还有设备自己的超温保护寄存器兜底（开机 ApplyProtection 写进去的）。

    /// <summary>开始跟随。第一拍不等采集循环，当下就把目标立起来。</summary>
    internal async Task StartRefluxAsync(int well, double deltaT, double maxTj, CancellationToken ct)
    {
        var t = _rd.TempOf(well);
        var dt = Math.Clamp(deltaT, 0.5, 30);
        var cap = Math.Min(maxTj, t.Limits.Max);    // 设备超温保护是死上限，指令说了不算

        // 起步就要有 Tr：跟随的目标就是 Tr+ΔT，没有 Tr 就没有目标——
        // 开一个空转的跟随比拒绝更糟
        if (!TrValid(well))
            throw new InvalidOperationException(
                $"工位 {AB(well)} 蒸回流开不了：釜内 Tr 没有有效读数" +
                "（宇电 Tr 探头没接、没连或断线）——夹套跟随的目标是 Tr+ΔT，没有 Tr 就没有目标");

        // 跟随的目标是 Tr+ΔT，永远在夹套之上——TEC 加热没启用时它整段都得靠电加热棒。
        // 电加热又用不了，那就是一开始就跟不动，与其跑一拍再报错不如现在说清楚
        if (!_tecHeat && !ElectricReady)
            throw new InvalidOperationException(
                $"工位 {AB(well)} 蒸回流开不了：「TEC 加热」没启用，夹套升温只能走电加热棒，" +
                (_links.Io is null ? "但这台没配 IO8R 切换模块" : "但 IO8R 没打开") +
                "——要么接上切换模块，要么在设备属性里打开「TEC 加热」");
        if (_tecHeat && cap > _threshold && !ElectricReady)
            _ctx.Log?.Invoke("warn", $"{InstanceId} 工位 {AB(well)} 蒸回流：夹套上限 {cap:F0} ℃ " +
                $"高于电加热切换阈值 {_threshold:F0} ℃，但电加热不可用——跟到阈值那一刻会停跟随");

        NoteStart(well);
        lock (_refluxGate)
        {
            _refluxKill[well] = false;
            _reflux[well] = new Follow { DeltaT = dt, MaxTj = cap };
        }
        _ctx.Log?.Invoke("info", $"{InstanceId} 工位 {AB(well)} 蒸回流开始：Tj 跟随 Tr+{dt:F1} K，上限 {cap:F0} ℃");
        await FollowOnceAsync(well, ct).ConfigureAwait(false);
    }

    /// <summary>停止跟随（步收尾/下一步接管）：目标停在最后一次下发的值上，输出不动。</summary>
    internal void StopReflux(int well)
    {
        bool was;
        lock (_refluxGate)
        {
            was = _reflux[well] is not null;
            _reflux[well] = null;
        }
        if (was) _ctx.Log?.Invoke("info", $"{InstanceId} 工位 {AB(well)} 蒸回流结束：目标停在最后一次下发的值上");
    }

    /// <summary>安全路径清跟随（SafeStop / 停控温）：连压制旗一起立——
    /// 跟随环这拍要是已经越过检查点，写完会自己把输出关回去。</summary>
    internal bool SuppressReflux(int well)
    {
        lock (_refluxGate)
        {
            var was = _reflux[well] is not null;
            _reflux[well] = null;
            _refluxKill[well] = true;
            return was;
        }
    }

    internal bool RefluxActive(int well)
    {
        lock (_refluxGate) return _reflux[well] is not null;
    }

    private bool TrValid(int well)
    {
        if (double.IsNaN(_rd.TempOf(well).CurrentReactor)) return false;
        lock (_refluxGate) return _ctx.Clock() - _trFedAt[well] <= TrFreshWindow;
    }

    /// <summary>跟随环跑一拍。目标没动（&lt; 0.1 ℃）就不写寄存器——不白磨总线。</summary>
    private async Task FollowOnceAsync(int well, CancellationToken ct)
    {
        Follow? r;
        lock (_refluxGate) r = _reflux[well];
        if (r is null) return;

        var t = _rd.TempOf(well);
        if (!TrValid(well))
        {
            // 联锁①：探头断线/超时。目标驻留在最后一次下发的值上——
            // 追着残值走比停跟随危险得多
            lock (_refluxGate) _reflux[well] = null;
            _ctx.Log?.Invoke("error", $"{InstanceId} 工位 {AB(well)} 蒸回流已停：" +
                "釜内 Tr 无效或超过新鲜度窗（探头断线？）——夹套目标停在最后一次下发的值上");
            return;
        }

        var tg = Math.Clamp(t.CurrentReactor + r.DeltaT, t.Limits.Min, r.MaxTj);
        if (t.Setpoint is { } sp && Math.Abs(tg - sp) < 0.1) return;

        // 跟随环是唯一**每拍**去调 EnsureSourceAsync 的路（明确下发是人或配方的一次决定，
        // 不受间隔约束；这里不是）。Tr 在沸点平台上本来就会抖，不守这道闸的话
        // 目标跟着抖、继电器也跟着抖——那 30 s 最短间隔就等于没有
        if (SideFor(well, tg) is true && !_electric[well]
            && _ctx.Clock() - _switchedAt[well] < MinDwell)
            return;

        try
        {
            // 联锁②：越过阈值先走全套热源切换序列（关输出→切继电器→核反馈→重开）
            await EnsureSourceAsync(well, tg, ct).ConfigureAwait(false);
            // 限速写：SPEED 按设备最大变温能力限，TG 让温控器自己斜坡过去——
            // 不是每拍阶跃，Tj 的变化率有帽子
            await t.RampAsync(tg, t.Limits.MaxRatePerMin, TempChannelKind.Jacket, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            lock (_refluxGate) _reflux[well] = null;
            _ctx.Log?.Invoke("error", $"{InstanceId} 工位 {AB(well)} 蒸回流已停：{ex.Message}");
            return;
        }

        // 联锁③的竞态补偿：写的当口有人安全停机（旗已清 + 压制立着），
        // 刚才那笔 RampAsync 把输出重新打开了——关回去
        bool kill;
        lock (_refluxGate) kill = _reflux[well] is null && _refluxKill[well];
        if (kill)
        {
            try { await t.StopAsync(ct).ConfigureAwait(false); } catch { }
            _ctx.Log?.Invoke("warn", $"{InstanceId} 工位 {AB(well)} 蒸回流写目标与安全停机撞车——输出已重新关闭");
        }
    }

    // ── 热源切换 ─────────────────────────────────────────────────────

    /// <summary>
    /// 重读属性栏上那两项「随手改、立刻生效」的配置。
    ///
    /// DriverContext.Config 拿到的就是台面设备身上那一份 ParameterSet 本体，属性栏改的也是它，
    /// 所以这里读得到最新值。**每拍读一次而不是每次用时读**：那是个普通字典，
    /// 界面线程正在写、采集线程同时读是要出事的，一拍抓一次再用本地副本，
    /// 撞上了就当这一拍没改到（下一拍自然读到）。
    /// </summary>
    private void RefreshSwitches()
    {
        try
        {
            var tec = _ctx.Config.Str(F.TecHeat, "不启用") == "启用";
            if (tec != _tecHeat)
            {
                _tecHeat = tec;
                _ctx.Log?.Invoke("info", $"{InstanceId} 「TEC 加热」改为{(tec ? "启用" : "不启用")}" +
                    (tec ? $"——目标高于 {_threshold:F0} ℃ 才切电加热"
                         : "——TEC 只当冷源，升温一律走电加热棒"));
            }
            _band = Math.Clamp(_ctx.Config.Num(F.Band, 2), 0.5, 10);
        }
        catch
        {
            // 撞上界面线程在写这一份配置。保持上一拍的值，下一拍再读
        }
    }

    /// <summary>电加热通路可不可用：配了切换模块而且开起来了。</summary>
    internal bool ElectricReady => _links.Io is not null && _ioOk;

    /// <summary>TEC 反向输出加热启没启用（界面按能力问它）。</summary>
    internal bool TecHeating => _tecHeat;

    internal bool OnElectric(int well) => _electric[well];

    /// <summary>
    /// 这个目标该落在哪一侧：true = 电加热，false = TEC，null = 保持现状（不折腾继电器）。
    ///
    /// TEC 加热启用时就是原来那条：超过阈值才要电加热，阈值以内 TEC 正反都能出力。
    /// 不启用时 TEC 只能制冷，于是按「该升温还是该降温」判——比的是 RD105 的被控量：
    /// PID 闭在夹套上，目标写进 TG，实测就是 Tj。死区之内当作到了，保持现状，
    /// 不让恒温时的上下擦动把继电器带得来回抖。
    /// </summary>
    private bool? SideFor(int well, double target)
    {
        if (_tecHeat) return target > _threshold;

        var t = _rd.TempOf(well);

        // 安全否决：**釜里已经不比目标凉了，就别再往上加热**。
        // 「恒温保持」下发的目标就是下发那一刻的 Tr（自指，严格相等），放热一起来
        // Tr 跑到目标上面、而夹套还在目标下面——只看夹套的话这时正好判成「要升温」，
        // 一头把电加热棒接上，等于往一个正在放热的釜里补热。
        // 明显高出一个死区 = 真要抢冷，回 TEC；刚好到/略高 = 保持现状，别去接加热棒。
        // Tr 必须是新鲜的（探头断线的残值不算数）
        if (TrValid(well) && t.CurrentReactor >= target)
            return t.CurrentReactor > target + _band ? false : null;

        // 正常判据：比的是 RD105 的被控量。PID 闭在夹套上，目标写进 TG，实测就是 Tj。
        // 夹套读不到（探头脱落 / 刚开机头一拍）就**不动继电器**——判不了别瞎扳，
        // 尤其别扳向加热侧；下一拍读到了采集循环自己会切
        var tj = t.CurrentJacket;
        if (double.IsNaN(tj)) return null;
        if (target > tj + _band) return true;   // 要升温 → 只能电加热棒
        if (target < tj - _band) return false;  // 要降温 → 只有 TEC 是冷源
        return null;
    }

    /// <summary>电加热用不了时，按当前模式说清楚为什么这个目标上不去。</summary>
    private string NoElectricReason(int well, double target)
    {
        var why = _links.Io is null ? "但这台没配 IO8R 切换模块" : "但 IO8R 没打开";
        if (_tecHeat)
            return $"目标 {target:F1} ℃ 高于电加热切换阈值 {_threshold:F0} ℃，{why}——电加热用不了，这个目标上不去";
        var tj = _rd.TempOf(well).CurrentJacket;
        return $"「TEC 加热」没启用，升温只能走电加热棒（目标 {target:F1} ℃ 高过夹套 {tj:F1} ℃），{why}" +
               "——这一路现在只降得下去、升不上来。要用 TEC 反向输出加热，请在设备属性里把「TEC 加热」打开";
    }

    /// <summary>
    /// 下发目标前确保热源在对的一侧。这里只管**切到电加热**：
    /// 回 TEC 不在这里做——夹套没凉到（阈值 − 滞回）之前把 TEC 接回去会烧它，
    /// 由采集循环在条件满足的那一拍执行。
    /// </summary>
    internal async Task EnsureSourceAsync(int well, double target, CancellationToken ct)
    {
        if (SideFor(well, target) is not true) return;
        if (!ElectricReady) throw new InvalidOperationException(NoElectricReason(well, target));
        if (!_electric[well]) await SwitchAsync(well, toElectric: true, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 切换动作全套序列（需求 §2.4）：关输出 → 切继电器 → 核反馈 → 重开输出。
    ///
    /// stillWanted：拿到锁之后再问一遍「这一刻还该往这边切吗」。采集循环那条路是拿着
    /// 上一拍的设定值做的判断，抢锁期间目标完全可能已经改了——不复核就会把刚下发的
    /// 指令的热源挪走。明确下发那条路不传它：那是人或配方当下的决定，不用再问。
    /// </summary>
    private async Task SwitchAsync(int well, bool toElectric, CancellationToken ct,
                                   Func<bool>? stillWanted = null)
    {
        var io = _links.Io!;
        await _switchLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_electric[well] == toElectric) return;      // 抢锁期间别人已经切完了
            if (stillWanted is not null && !stillWanted()) return;
            var gen = Volatile.Read(ref _stopGen[well]);
            var innerT = _rd.TempOf(well);
            // 切完要不要把输出重开，看的是**意图**不是设备上的 ENABLE：
            // 停控时 TG 还留着，光看 Setpoint 分不出「有没有人要控温」；而 ENABLE
            // 在切换序列里被我们自己关过，上一笔切换失败的话它会一直是 0
            var wasEnabled = _wantEnabled[well];
            _ctx.Log?.Invoke("info", $"{InstanceId} 工位 {AB(well)} 热源切换：" +
                $"{(toElectric ? "TEC → 电加热" : "电加热 → TEC")}（先关输出）");

            await innerT.StopAsync(ct).ConfigureAwait(false);                       // ① 带载切继电器 = 触点拉弧
            await io.SetRelayAsync(_doIdx[well], toElectric, ct).ConfigureAwait(false);   // ②
            _electric[well] = toElectric;

            if (_feedback)                                                          // ③ 接了反馈就必须核实
            {
                var deadline = Environment.TickCount64 + 2000;
                var ok = false;
                while (!ok && Environment.TickCount64 < deadline)
                {
                    var di = await io.ReadInputsAsync(ct).ConfigureAwait(false);
                    ok = di[_diIdx[well]] == toElectric;
                    if (!ok) await Task.Delay(100, ct).ConfigureAwait(false);
                }
                if (!ok)
                {
                    _ctx.Log?.Invoke("error", $"{InstanceId} 工位 {AB(well)} 切换命令已发，" +
                        $"但反馈 DI{_diIdx[well]} 2 秒内没对上——控温输出保持关闭");
                    throw new InvalidOperationException(
                        $"工位 {AB(well)} 热源切换未核实：继电器命令已发，但反馈 DI{_diIdx[well]} 没有跟上——" +
                        "接触器可能没动作。控温输出保持关闭，请到现场检查");
                }
            }

            _switchedAt[well] = _ctx.Clock();
            // ④ 原来开着、而且这两秒里没人喊停，才重开输出
            if (wasEnabled && innerT.Setpoint is not null && Volatile.Read(ref _stopGen[well]) == gen)
                await innerT.EnableAsync(true, ct).ConfigureAwait(false);
            else if (wasEnabled)
                _ctx.Log?.Invoke("info", $"{InstanceId} 工位 {AB(well)} 切换期间控温被停——输出保持关闭");
            _ctx.Log?.Invoke("info", $"{InstanceId} 工位 {AB(well)} 已切至{(toElectric ? "电加热" : "TEC")}" +
                (_feedback ? "（反馈已核实）" : "（无反馈回路，未核实）"));
        }
        finally
        {
            _switchLock.Release();
        }
    }

    // ── 安全停 ───────────────────────────────────────────────────────

    /// <summary>关本工位控温输出 + 把本工位切换继电器断开（落回 TEC 侧），采集不停。</summary>
    public async ValueTask<IReadOnlyList<string>?> SafeStopAsync(int well, CancellationToken ct)
    {
        if (well is not (0 or 1)) return Array.Empty<string>();
        var notes = new List<string>();

        // 先清跟随（联锁③）：不清的话下一拍跟随环又把目标写回去、把输出重新打开
        NoteStop(well);
        if (SuppressReflux(well))
            notes.Add($"工位 {AB(well)} 蒸回流跟随已停——安全停机后不会再有目标写回去");

        var inner = await _rd.SafeStopAsync(well, ct).ConfigureAwait(false);
        if (inner is not null) notes.AddRange(inner);

        if (_links.Io is { } io && _ioOk)
        {
            // 断继电器也要拿切换锁：不拿的话，跟随环那边正走到一半的切换序列
            // 会在安全停机之后把继电器重新合到电加热侧
            await _switchLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await io.SetRelayAsync(_doIdx[well], false, ct).ConfigureAwait(false);
                _electric[well] = false;
                _switchedAt[well] = _ctx.Clock();
                notes.Add($"工位 {AB(well)} 热源切换继电器已断开（落回 TEC 侧）");
            }
            catch (Exception ex)
            {
                notes.Add($"断开工位 {AB(well)} 热源切换继电器失败：{ex.Message}——" +
                          "兜底靠 IO8R 的总线错误复位（部署清单第 1 条，必须已改成「复位」）");
            }
            finally
            {
                _switchLock.Release();
            }
        }
        return notes;
    }

    /// <summary>
    /// 热源状态量（需求 §2.5）：0 = TEC，1 = 电加热（未核实），2 = 电加热（反馈已核实）。
    /// 没接反馈回路的只能报「已发命令，未核实」，不假装核实过；接了反馈的，
    /// 继电器与接触器辅助触点对不上就照实降级并报出来——两种不一致各有各的后果：
    /// 继电器吸着、触点没跟上 = 电加热其实没在加热；继电器断了、触点还吸着 = 接触器粘连，
    /// 电加热棒可能还带电。切换序列进行中（锁被占着）那 2 秒不算不一致，那正是在等它跟上。
    /// </summary>
    private double HeatStateOf(int w, bool relayClosed, bool[]? dis)
    {
        if (dis is null) return relayClosed ? 1 : 0;
        var contactor = dis[_diIdx[w]];
        if (contactor == relayClosed)
        {
            if (_fbMismatch[w])
            {
                _fbMismatch[w] = false;
                _ctx.Log?.Invoke("info", $"{InstanceId} 工位 {AB(w)} 反馈 DI{_diIdx[w]} 已与继电器对上");
            }
            return relayClosed ? 2 : 0;
        }
        if (_switchLock.CurrentCount == 0) return relayClosed ? 1 : 0;   // 正在切换，等它跟上
        if (!_fbMismatch[w])
        {
            _fbMismatch[w] = true;
            if (relayClosed)
                _ctx.Log?.Invoke("warn", $"{InstanceId} 工位 {AB(w)} 切换继电器在电加热侧，" +
                    $"但反馈 DI{_diIdx[w]} 已掉——接触器可能已释放，电加热实际没在加热");
            else
                _ctx.Log?.Invoke("error", $"{InstanceId} 工位 {AB(w)} 切换继电器已断开，" +
                    $"但反馈 DI{_diIdx[w]} 仍闭合——接触器可能粘连，电加热棒可能还带电，请到现场检查");
        }
        // 两种不一致都按「电加热·未核实」记：前者是命令说在电加热，后者是接触器说在电加热
        return 1;
    }

    private void Push(int channel, string tag, double value, DateTimeOffset at, Quality quality)
    {
        if (double.IsNaN(value)) return;
        _out.Push(new Sample(channel, tag, at.UtcTicks, at, value, quality));
    }

    private static string AB(int well) => well == 0 ? "A" : "B";

    public async ValueTask DisposeAsync()
    {
        _pollCts?.Cancel();
        if (_pollTask is { } t) { try { await t.ConfigureAwait(false); } catch { } }

        // 走人前把两路继电器落回 TEC 侧。断线后的兜底是 IO8R 的总线错误复位，
        // 但能自己收拾就不劳驾兜底
        if (_links.Io is { } io && _ioOk)
        {
            try { await io.AllOffAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
        }

        _fwd.Dispose();
        await _rd.DisposeAsync().ConfigureAwait(false);     // 里面会关掉 RD105 的链路
        _links.IoPort?.Dispose();
        _out.Complete();
        State = DeviceState.Disposed;
    }

    private sealed class Fwd(Broadcast<Sample> target) : IObserver<Sample>
    {
        public void OnNext(Sample value) => target.Push(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
