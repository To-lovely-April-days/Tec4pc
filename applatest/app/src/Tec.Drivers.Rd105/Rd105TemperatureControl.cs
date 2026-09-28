using TecControl.Core.Control;
using Tec.Driver.Abi;

namespace Tec.Drivers.Rd105;

/// <summary>
/// 把 RD105 的**一路 TC** 翻成 ITemperatureControl。
///
/// 真机拓扑（docs/双工位反应主机驱动需求.md §1/§3）：一台 RD105 带两路，
/// TC1 = 工位 A、TC2 = 工位 B，**每路探头测的都是夹套 Tj**，PID 回路闭在夹套上。
/// 釜内 Tr 在这台设备上不存在——它从宇电 J7 那条串口来，由组合会话用
/// FeedReactor 喂进来；没人喂就是 NaN，绝不拿夹套读数冒充釜内。
///
/// 两种控温方式（设备配置「控温方式」）：
/// · 温控器 PID：写 TG（目标）与 SPEED（变温速率），它自己带斜坡，上位机只下发、收数、判到达。
///   这一路看不见釜内——「釜内」目标也只是把同一个数写给夹套回路（釜内永远差一个散热偏置）；
/// · 上位机 PID：温控器只采温度、出功率（MODE=3），回路在 HostControlLoop 里：夹套单环 / 釜内串级
///   （外环吃喂进来的 Tr，算夹套设定值）、按温度分段的增益表、稳态前馈、跑飞检测。
///   斜率用回路的分段曲线；热源切换那两秒把回路挂起（EnableAsync(false)）。
/// </summary>
public sealed class Rd105TemperatureControl : ITemperatureControl, ITemperatureStatus
{
    private readonly int _tc;
    private readonly Rd105Link _link;
    private readonly Broadcast<Sample> _out;
    private readonly double _overUp;
    private readonly double _overLow;
    private readonly double _maxCurrent;
    private readonly HostControlLoop? _loop;
    private double _tr = double.NaN;
    private double _tj = double.NaN;

    public Rd105TemperatureControl(int channel, int tc, Rd105Link link, ParameterSet config, Broadcast<Sample> outStream,
                                   HostControlLoop? hostLoop = null)
    {
        Channel = channel;
        _tc = tc;
        _link = link;
        _out = outStream;
        _loop = hostLoop;
        _overUp = config.Num(Rd105TecDriver.FieldOverUp, 180);
        _overLow = config.Num(Rd105TecDriver.FieldOverLow, -40);
        _maxCurrent = config.Num(Rd105TecDriver.FieldMaxCurrent, 5);
        // 串级外环的被控量 = 喂进来的釜内 Tr（没人喂就是 NaN，回路会停控并说明）
        _loop?.SetOuterSource(_tc, () => _tr);
    }

    public int Channel { get; }

    /// <summary>这一路挂在温控器的哪个物理路号上（TC1 = 工位 A，TC2 = 工位 B）。</summary>
    public int Tc => _tc;

    /// <summary>控温回路在上位机（true）还是温控器自己（false）。</summary>
    public bool HostControlled => _loop is not null;

    /// <summary>当前控温对象：夹套（单环）还是釜内（串级）。上位机方式下才有区别。</summary>
    public TempChannelKind Kind { get; private set; } = TempChannelKind.Jacket;

    /// <summary>串级时外环算出的夹套设定值；单环 / 温控器方式下就是 Setpoint。没在控就是 null。</summary>
    public double? InnerSetpoint { get; internal set; }

    /// <summary>回路正在「保持」的说明（串级丢了釜内 Tr、等它回来：夹套设定钳到釜内设定）；没在保持就是 null。会话每拍刷。</summary>
    public string? Holding { get; internal set; }

    /// <summary>最近一次被回路自己停下的原因；人手停的 / 重新下发之后为 null。面板拿它说清「为什么停了」。</summary>
    public string? LastStop { get; private set; }

    /// <summary>上位机回路把这一路停了（读数丢失 / 跑飞 / 连续通信失败）。组合会话据此把继电器落回去。</summary>
    public event Action<string>? Tripped;

    /// <summary>这一路的逐拍记录（上位机方式下会话装上）：下发目标开文件、停控收文件。</summary>
    internal Rd105LoopRecorder? Recorder { get; set; }

    /// <summary>组合会话的事件（定挡 / 交接 / 冷水机）挂到逐拍记录的下一行里；没在记就丢掉。</summary>
    public void Note(string text) => Recorder?.Note(text);

    /// <summary>回路此刻的执行器形态；温控器 PID 方式下没有（null）。</summary>
    public ActuatorMode? Actuator => _loop?.GetActuatorMode(_tc);

    /// <summary>
    /// 上位机 PID 这一拍算出的输出（PID 那一侧：反向、加热棒符号都还没动过，限幅 [下限, 上限]）。
    /// 只制冷形态下贴着上限 0 = 回路想加热而没有加热那一极；温控器 PID 方式下 null。
    /// </summary>
    public double? PidOutput => _loop is null ? null : _loop.GetChannelStatus(_tc).LastDutyPercent;

    /// <summary>最近一次写进 / 读回温控器的占空比（%，带符号）；还没有为 NaN。会话每拍刷。</summary>
    public double LastDuty { get; internal set; } = double.NaN;

    /// <summary>
    /// 限值取设备侧的超温保护值，不在界面里写死。
    /// 最大速率按 RD105 的 SPEED 量程与工艺上限取 5 ℃/min（ProfileSegment 也是这个上限）。
    /// </summary>
    public TempLimits Limits => new(_overLow, _overUp, 5);

    /// <summary>釜内温度。来自组合会话喂的宇电读数；单机使用没人喂就是 NaN。</summary>
    public double CurrentReactor => _tr;

    /// <summary>夹套温度：本 TC 路的探头读数。</summary>
    public double CurrentJacket => _tj;

    /// <summary>当前设定值。还没下发过就是 null——不假装有一个。</summary>
    public double? Setpoint { get; private set; }

    /// <summary>ITemperatureStatus：回路开着 = ENABLE 的影子（上位机方式下 = 回路激活且没被停）。</summary>
    public bool Active => Enabled;

    /// <summary>ITemperatureStatus：上位机 PID 方式就是 true。</summary>
    bool ITemperatureStatus.HostLoop => _loop is not null;

    /// <summary>ITemperatureStatus：只有上位机串级、正在控、外环已经算出过内环设定时才有数。</summary>
    double? ITemperatureStatus.CascadeInnerSetpoint
        => _loop is not null && Enabled && Kind == TempChannelKind.Reactor
           && _loop.GetStrategy(_tc) == ControlStrategy.Cascade ? InnerSetpoint : null;

    /// <summary>ITemperatureStatus：保持中的那句话只在回路开着时算数。</summary>
    string? ITemperatureStatus.Holding => Enabled ? Holding : null;

    string? ITemperatureStatus.LastStop => LastStop;

    /// <summary>
    /// 本路输出开着没有（ENABLE 的影子）。热源切换要看它：**停着的通道不该被自动切换动**——
    /// 输出都关了，扳继电器没有意义，还会把安全停机落回 TEC 侧的继电器又扳回电加热。
    /// </summary>
    public bool Enabled { get; private set; }

    /// <summary>设备当前的告警条目（已翻成人话）。没有告警就是空的。</summary>
    public IReadOnlyList<string> Faults { get; internal set; } = Array.Empty<string>();

    public IObservable<Sample> Temperature => _out;

    /// <summary>轮询到的本路夹套温度。</summary>
    public void Observe(double tj) => _tj = tj;

    /// <summary>组合会话把宇电 J7 采到的釜内 Tr 喂进来（断线/无效就喂 NaN，别喂残值）。</summary>
    public void FeedReactor(double tr) => _tr = tr;

    /// <summary>
    /// 把超温与限流写进温控器自己的保护寄存器。断了通信这两条照样生效——
    /// 固件没有通信看门狗，上位机的软限值在断线那一刻就不存在了。
    /// </summary>
    public async Task ApplyProtectionAsync(CancellationToken ct)
    {
        await _link.Controller.SetOverTempAsync(_tc, _overUp, _overLow, ct).ConfigureAwait(false);
        await _link.Controller.SetMaxCurrentAsync(_tc, _maxCurrent, ct).ConfigureAwait(false);
    }

    public async Task SetTargetAsync(TempTarget target, CancellationToken ct)
    {
        Guard(target.Value);
        if (_loop is null)
        {
            await _link.Controller.SetSpeedAsync(_tc, 0, ct).ConfigureAwait(false);   // 0 = 直接阶跃
            await _link.Controller.SetTargetAsync(_tc, target.Value, ct).ConfigureAwait(false);
            await _link.Controller.SetEnableAsync(_tc, true, ct).ConfigureAwait(false);
            Kind = target.Kind;
            Setpoint = target.Value;
            InnerSetpoint = target.Value;
            Enabled = true;
            LastStop = null;
            return;
        }

        await HostArmAsync(target.Kind, target.Value, ct).ConfigureAwait(false);
        _loop.StopProfile(_tc);
        _loop.SetSetpoint(_tc, target.Value);
        Setpoint = target.Value;
        InnerSetpoint = _loop.GetStrategy(_tc) == ControlStrategy.Cascade ? InnerSetpoint : target.Value;
        Enabled = true;
        LastStop = null;
        Recorder?.Start(target.Kind == TempChannelKind.Reactor ? "釜内" : "夹套", target.Value, "尽快");
    }

    public async Task RampAsync(double target, double ratePerMin, TempChannelKind kind, CancellationToken ct)
    {
        Guard(target);
        if (!double.IsFinite(ratePerMin))
            throw new ArgumentOutOfRangeException(nameof(ratePerMin), "变温速率不是有效数（釜内没有读数时「按时长」算不出速率）");
        if (_loop is null)
        {
            // 温控器的 SPEED 是 ℃/秒，配方里写的是 ℃/分
            var perSecond = Math.Abs(ratePerMin) / 60.0;
            await _link.Controller.SetSpeedAsync(_tc, perSecond, ct).ConfigureAwait(false);
            await _link.Controller.SetTargetAsync(_tc, target, ct).ConfigureAwait(false);
            await _link.Controller.SetEnableAsync(_tc, true, ct).ConfigureAwait(false);
            Kind = kind;
            Setpoint = target;
            InnerSetpoint = target;
            Enabled = true;
            LastStop = null;
            return;
        }

        // 斜坡从「此刻的设定值」起步：回路没开就从当前实测起步（夹套 / 釜内按控温对象）
        var pv = kind == TempChannelKind.Reactor ? _tr : _tj;
        var from = _loop.GetChannelStatus(_tc).Active ? _loop.GetChannelStatus(_tc).SetpointC
                 : double.IsNaN(pv) ? target : pv;
        await HostArmAsync(kind, from, ct).ConfigureAwait(false);
        _loop.SetSetpoint(_tc, from);
        var rate = Math.Clamp(Math.Abs(ratePerMin), ProfileSegment.MinRate, ProfileSegment.MaxRate);
        _loop.StartProfile(_tc, SegmentProfile.Linear(target, rate));
        Setpoint = target;
        Enabled = true;
        LastStop = null;
        Recorder?.Start(kind == TempChannelKind.Reactor ? "釜内" : "夹套", target, $"按速率 {rate:0.##} ℃/min，从 {from:0.##} ℃ 起");
    }

    /// <summary>
    /// 上位机方式：把回路对准控温对象并激活。釜内串级要有 Tr 才成立——没有就拒绝，
    /// 不悄悄降级成夹套（那就是「釜内控温」四个字兑现不了）。换对象要重启回路（外环从干净状态起步）。
    /// </summary>
    private async Task HostArmAsync(TempChannelKind kind, double setpoint, CancellationToken ct)
    {
        var loop = _loop!;
        if (loop.IsTuning(_tc))
            throw new InvalidOperationException(
                $"工位 {(_tc == 1 ? "A" : "B")}（TC{_tc}）正在自整定——先在面板「PID 整定」页取消，再下发控温目标");
        var strategy = kind == TempChannelKind.Reactor ? ControlStrategy.Cascade : ControlStrategy.Direct;
        if (strategy == ControlStrategy.Cascade && double.IsNaN(_tr))
            throw new InvalidOperationException(
                "釜内 Tr 没有读数（宇电探头没接 / 断线 / 探头会话没出数）——釜内控温做不了。先把探头接好，或改控夹套");
        var status = loop.GetChannelStatus(_tc);
        if (!status.Active || loop.GetStrategy(_tc) != strategy || loop.IsSuspended(_tc) && !Enabled)
        {
            loop.StopProfile(_tc);
            loop.SetStrategy(_tc, strategy);
            loop.SuspendOutput(_tc, false);
            InnerSetpoint = null;          // 换了对象：旧的内环设定不再成立，等外环下一拍算出新的
            await loop.StartChannelAsync(_tc, setpoint, ct: ct).ConfigureAwait(false);   // MODE=3、使能、PID 复位 + 前馈预置
        }
        Kind = kind;
    }

    /// <summary>
    /// 等到达。判据优先用釜内 Tr——工艺关心的是釜里到没到，不是夹套到没到；
    /// Tr 由组合会话从宇电喂进来。单独当温控器用、没人喂 Tr 时退回按夹套判——
    /// 那种用法它就只有夹套这一个温度，不是冒充。
    /// 超时返回 false，由调用方决定是报警还是接着走（§7.7）。
    /// </summary>
    public async Task<bool> WaitReachedAsync(double target, double tolerance, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            // 上位机串级（釜内）下 Tr 断了不退回按夹套判：保持期夹套设定就是釜内目标，几十秒就「到」——那是假到达
            var pv = double.IsNaN(_tr) && (Kind == TempChannelKind.Jacket || _loop is null) ? _tj : _tr;
            if (!double.IsNaN(pv) && Math.Abs(pv - target) <= Math.Abs(tolerance)) return true;
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
        return false;
    }

    /// <summary>停止控温：关本路输出。目标值留着，方便记录里看得出停的时候在追什么。</summary>
    public async Task StopAsync(CancellationToken ct)
    {
        if (_loop is null)
            await _link.Controller.SetEnableAsync(_tc, false, ct).ConfigureAwait(false);
        else
        {
            _loop.StopProfile(_tc);
            _loop.SuspendOutput(_tc, false);
            await _loop.StopChannelAsync(_tc, ct).ConfigureAwait(false);       // 占空比归零 + 关使能
        }
        InnerSetpoint = null;
        Enabled = false;
        Holding = null;
        LastStop = null;           // 人手停的，不是回路停的
        Recorder?.Stop("操作人停控");
    }

    /// <summary>
    /// 只开/关本路输出，TG 与 SPEED 保持原样。
    /// 热源切换序列用它：先关输出→切继电器→再开回来（带载切继电器 = 触点拉弧）。
    /// 上位机方式下关 = 把回路挂起（不算不写）并把占空比归零、关使能；开 = 反过来。
    /// </summary>
    public async Task EnableAsync(bool on, CancellationToken ct)
    {
        if (_loop is null)
        {
            await _link.Controller.SetEnableAsync(_tc, on, ct).ConfigureAwait(false);
        }
        else if (!on)
        {
            // 先挂起再等一个周期：回路可能正在这一拍的中途，等它写完这一拍再把输出归零，
            // 不然归零的那一笔会被它随后的一笔覆盖，继电器带着载切
            _loop.SuspendOutput(_tc, true);
            try { await Task.Delay(_loop.Period, ct).ConfigureAwait(false); } catch (OperationCanceledException) { throw; }
            await _link.Controller.SetDutyPercentAsync(_tc, 0, ct).ConfigureAwait(false);
            await _link.Controller.SetEnableAsync(_tc, false, ct).ConfigureAwait(false);
        }
        else
        {
            await _link.Controller.SetEnableAsync(_tc, true, ct).ConfigureAwait(false);
            _loop.SuspendOutput(_tc, false);
        }
        Enabled = on;
    }

    /// <summary>
    /// 继电器扳到哪一侧，回路的执行器形态就跟着换：加热棒 = 只加热（占空比幅度 ≥ 0，符号按接线），
    /// TEC = 双向。换了形态积分清零、按前馈预置——加热棒和 TEC 的增益差一个量级，背着旧积分换过去只会过冲。
    /// 温控器 PID 方式下没有这回事（固件自己按符号路由）。
    /// </summary>
    public void SetActuator(bool electric)
        => SetActuator(electric ? ActuatorMode.HeatOnly : ActuatorMode.Bidirectional);

    /// <summary>
    /// 四态：加热棒 = 只加热；TEC = 双向，或「TEC 加热」不启用时只制冷（正半轴那一极接的是加热棒 SSR、继电器断着）；
    /// 加热棒 + TEC 双向（0336 双向挡）= 一个 PID 正半轴加热棒、负半轴 TEC，参数按加热棒表——跟只加热是同一组参数，
    /// 互换只改下限、不复位（回路自己判：同一张表之内换形态运行中也许改）。
    /// </summary>
    public void SetActuator(ActuatorMode mode) => _loop?.SetActuatorMode(_tc, mode);

    /// <summary>回路把这一路停了（会话收到 ChannelTripped 时调）。</summary>
    internal void OnTripped(string reason)
    {
        Enabled = false;
        InnerSetpoint = null;
        Holding = null;
        LastStop = reason;
        Recorder?.Stop("回路停控：" + reason);
        Tripped?.Invoke(reason);
    }

    /// <summary>
    /// 指令由通用执行器按能力调用，这里不自己认领指令 Id——
    /// 认领了就等于把工艺语义分散到每个驱动里，31 条指令会各写各的。
    /// </summary>
    public ICommandHandler? Resolve(string commandId) => null;

    private void Guard(double target)
    {
        if (!double.IsFinite(target))
            throw new ArgumentOutOfRangeException(nameof(target), "目标温度不是有效数（读数是「—」时别拿它当目标）");
        if (target < _overLow || target > _overUp)
            throw new ArgumentOutOfRangeException(nameof(target),
                $"目标温度 {target:F1} ℃ 超出设备保护范围 {_overLow:F0}~{_overUp:F0} ℃");
    }
}
