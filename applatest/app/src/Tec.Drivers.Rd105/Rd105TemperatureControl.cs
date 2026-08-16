using Tec.Driver.Abi;

namespace Tec.Drivers.Rd105;

/// <summary>
/// 把 RD105 温控器翻成 ITemperatureControl。
///
/// 控温本身交给温控器自己的 PID：写 TG（目标）与 SPEED（变温速率），
/// 它自己带斜坡。上位机只负责下发、收数、判到达——这也正是
/// TecStudio.sln 现在跑的那条路。
/// </summary>
internal sealed class Rd105TemperatureControl : ITemperatureControl
{
    private readonly Rd105Link _link;
    private readonly Broadcast<Sample> _out;
    private readonly double _overUp;
    private readonly double _overLow;
    private readonly double _maxCurrent;
    private double _tr = double.NaN;
    private double _tj = double.NaN;

    public Rd105TemperatureControl(int channel, Rd105Link link, ParameterSet config, Broadcast<Sample> outStream)
    {
        Channel = channel;
        _link = link;
        _out = outStream;
        _overUp = config.Num(Rd105TecDriver.FieldOverUp, 180);
        _overLow = config.Num(Rd105TecDriver.FieldOverLow, -40);
        _maxCurrent = config.Num(Rd105TecDriver.FieldMaxCurrent, 5);
    }

    public int Channel { get; }

    /// <summary>
    /// 限值取设备侧的超温保护值，不在界面里写死。
    /// 最大速率按 RD105 的 SPEED 量程与工艺上限取 5 ℃/min（ProfileSegment 也是这个上限）。
    /// </summary>
    public TempLimits Limits => new(_overLow, _overUp, 5);

    public double CurrentReactor => _tr;
    public double CurrentJacket => _tj;

    /// <summary>当前设定值。还没下发过就是 null——不假装有一个。</summary>
    public double? Setpoint { get; private set; }

    /// <summary>设备当前的告警条目（已翻成人话）。没有告警就是空的。</summary>
    public IReadOnlyList<string> Faults { get; internal set; } = Array.Empty<string>();

    public IObservable<Sample> Temperature => _out;

    /// <summary>轮询到的两路温度。TC1 = 釜内 Tr，TC2 = 夹套 Tj。</summary>
    public void Observe(double tr, double tj)
    {
        _tr = tr;
        _tj = tj;
    }

    /// <summary>
    /// 把超温与限流写进温控器自己的保护寄存器。断了通信这两条照样生效——
    /// 固件没有通信看门狗，上位机的软限值在断线那一刻就不存在了。
    /// </summary>
    public async Task ApplyProtectionAsync(CancellationToken ct)
    {
        await _link.Controller.SetOverTempAsync(Tc, _overUp, _overLow, ct).ConfigureAwait(false);
        await _link.Controller.SetMaxCurrentAsync(Tc, _maxCurrent, ct).ConfigureAwait(false);
    }

    /// <summary>温控器上的物理路号。釜内 Tr 是 TC1，控温回路也挂在它上面。</summary>
    private const int Tc = 1;

    public async Task SetTargetAsync(TempTarget target, CancellationToken ct)
    {
        Guard(target.Value);
        await _link.Controller.SetSpeedAsync(Tc, 0, ct).ConfigureAwait(false);   // 0 = 直接阶跃
        await _link.Controller.SetTargetAsync(Tc, target.Value, ct).ConfigureAwait(false);
        await _link.Controller.SetEnableAsync(Tc, true, ct).ConfigureAwait(false);
        Setpoint = target.Value;
    }

    public async Task RampAsync(double target, double ratePerMin, TempChannelKind kind, CancellationToken ct)
    {
        Guard(target);
        // 温控器的 SPEED 是 ℃/秒，配方里写的是 ℃/分
        var perSecond = Math.Abs(ratePerMin) / 60.0;
        await _link.Controller.SetSpeedAsync(Tc, perSecond, ct).ConfigureAwait(false);
        await _link.Controller.SetTargetAsync(Tc, target, ct).ConfigureAwait(false);
        await _link.Controller.SetEnableAsync(Tc, true, ct).ConfigureAwait(false);
        Setpoint = target;
    }

    /// <summary>
    /// 等到达。判据用釜内温度——工艺关心的是釜里到没到，不是夹套到没到。
    /// 超时返回 false，由调用方决定是报警还是接着走（§7.7）。
    /// </summary>
    public async Task<bool> WaitReachedAsync(double target, double tolerance, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (!double.IsNaN(_tr) && Math.Abs(_tr - target) <= Math.Abs(tolerance)) return true;
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
        return false;
    }

    /// <summary>停止控温：关输出。目标值留着，方便记录里看得出停的时候在追什么。</summary>
    public async Task StopAsync(CancellationToken ct)
    {
        await _link.Controller.SetEnableAsync(Tc, false, ct).ConfigureAwait(false);
    }

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
