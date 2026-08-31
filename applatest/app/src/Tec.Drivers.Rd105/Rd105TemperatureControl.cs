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
/// 控温本身交给温控器自己的 PID：写 TG（目标）与 SPEED（变温速率），
/// 它自己带斜坡。上位机只负责下发、收数、判到达。
/// </summary>
public sealed class Rd105TemperatureControl : ITemperatureControl
{
    private readonly int _tc;
    private readonly Rd105Link _link;
    private readonly Broadcast<Sample> _out;
    private readonly double _overUp;
    private readonly double _overLow;
    private readonly double _maxCurrent;
    private double _tr = double.NaN;
    private double _tj = double.NaN;

    public Rd105TemperatureControl(int channel, int tc, Rd105Link link, ParameterSet config, Broadcast<Sample> outStream)
    {
        Channel = channel;
        _tc = tc;
        _link = link;
        _out = outStream;
        _overUp = config.Num(Rd105TecDriver.FieldOverUp, 180);
        _overLow = config.Num(Rd105TecDriver.FieldOverLow, -40);
        _maxCurrent = config.Num(Rd105TecDriver.FieldMaxCurrent, 5);
    }

    public int Channel { get; }

    /// <summary>这一路挂在温控器的哪个物理路号上（TC1 = 工位 A，TC2 = 工位 B）。</summary>
    public int Tc => _tc;

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
        await _link.Controller.SetSpeedAsync(_tc, 0, ct).ConfigureAwait(false);   // 0 = 直接阶跃
        await _link.Controller.SetTargetAsync(_tc, target.Value, ct).ConfigureAwait(false);
        await _link.Controller.SetEnableAsync(_tc, true, ct).ConfigureAwait(false);
        Setpoint = target.Value;
    }

    public async Task RampAsync(double target, double ratePerMin, TempChannelKind kind, CancellationToken ct)
    {
        Guard(target);
        // 温控器的 SPEED 是 ℃/秒，配方里写的是 ℃/分
        var perSecond = Math.Abs(ratePerMin) / 60.0;
        await _link.Controller.SetSpeedAsync(_tc, perSecond, ct).ConfigureAwait(false);
        await _link.Controller.SetTargetAsync(_tc, target, ct).ConfigureAwait(false);
        await _link.Controller.SetEnableAsync(_tc, true, ct).ConfigureAwait(false);
        Setpoint = target;
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
            var pv = double.IsNaN(_tr) ? _tj : _tr;
            if (!double.IsNaN(pv) && Math.Abs(pv - target) <= Math.Abs(tolerance)) return true;
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
        return false;
    }

    /// <summary>停止控温：关本路输出。目标值留着，方便记录里看得出停的时候在追什么。</summary>
    public async Task StopAsync(CancellationToken ct)
    {
        await _link.Controller.SetEnableAsync(_tc, false, ct).ConfigureAwait(false);
    }

    /// <summary>只开/关本路输出，TG 与 SPEED 保持原样。
    /// 热源切换序列用它：先关输出→切继电器→再开回来（带载切继电器 = 触点拉弧）。</summary>
    public Task EnableAsync(bool on, CancellationToken ct)
        => _link.Controller.SetEnableAsync(_tc, on, ct);

    /// <summary>
    /// 指令由通用执行器按能力调用，这里不自己认领指令 Id——
    /// 认领了就等于把工艺语义分散到每个驱动里，31 条指令会各写各的。
    /// </summary>
    public ICommandHandler? Resolve(string commandId) => null;

    private void Guard(double target)
    {
        if (target < _overLow || target > _overUp)
            throw new ArgumentOutOfRangeException(nameof(target),
                $"目标温度 {target:F1} ℃ 超出设备保护范围 {_overLow:F0}~{_overUp:F0} ℃");
    }
}
