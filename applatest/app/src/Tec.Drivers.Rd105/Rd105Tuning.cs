using TecControl.Core.Control;
using Tec.Driver.Abi;

namespace Tec.Drivers.Rd105;

/// <summary>
/// PID 整定与控制策略，直接架在 TecControl.Core 的 HostControlLoop 上——
/// 继电器法自整定、增益调度那一整套是那边现成的，这里只做翻译，
/// 一行控制算法都不重写。每个工位一个实例，整定各自的 TC 夹套回路。
///
/// 自整定**不进配方指令库**：它要激起温度振荡，是调试 / 维护动作，
/// 必须有人在场发起，只能从手动控制面板调用。
/// </summary>
internal sealed class Rd105Tuning : ITemperatureTuning
{
    /// <summary>温控器上的物理路号（TC1 = 工位 A，TC2 = 工位 B），整定的就是这一路的夹套回路。</summary>
    private readonly int _tc;

    private readonly HostControlLoop _loop;
    private readonly Action<string, string> _log;
    private readonly Func<bool>? _canCascade;
    private TempChannelKind _kind = TempChannelKind.Jacket;
    private double _tuningSetpointC = double.NaN;

    /// <param name="canCascade">上位机方式下「此刻有没有釜内 Tr 可吃」；null = 温控器方式，谈不上串级。</param>
    public Rd105Tuning(int channel, int tc, HostControlLoop loop, Action<string, string> log, Func<bool>? canCascade = null)
    {
        Channel = channel;
        _tc = tc;
        _loop = loop;
        _log = log;
        _canCascade = canCascade;
        _loop.AutoTuneFinished += OnFinished;
    }

    public int Channel { get; }

    public TempChannelKind Strategy => _kind;

    public TuningState TuningState { get; private set; } = TuningState.Idle;

    public string TuningNote { get; private set; } = "";

    public event EventHandler<TuningOutcome>? TuningFinished;

    /// <summary>
    /// 两路 TC 测的都是夹套，设备自己看不见釜内 Tr。「釜内串级」只在上位机方式、而且组合会话
    /// 已经把宇电的 Tr 喂进来时才成立；否则选 Reactor 就是许一个兑现不了的回路——直接拒绝，
    /// 不悄悄降级成夹套。自整定本身整的永远是内环（夹套），这里的策略只决定整定完回路按哪个对象控。
    /// </summary>
    public Task SetStrategyAsync(TempChannelKind kind, CancellationToken ct)
    {
        if (kind == TempChannelKind.Reactor)
        {
            if (_canCascade is null)
                throw new NotSupportedException(
                    "「控温方式」是温控器 PID：它的两路 TC 测的都是夹套，设备上没有釜内 Tr——" +
                    "釜内串级要在设备属性里把「控温方式」改成上位机 PID");
            if (!_canCascade())
                throw new InvalidOperationException("釜内 Tr 没有读数（宇电探头没接 / 断线）——釜内串级做不了，先接好探头");
        }
        _kind = kind;
        _loop.SetStrategy(_tc, kind == TempChannelKind.Reactor ? ControlStrategy.Cascade : ControlStrategy.Direct);
        return Task.CompletedTask;
    }

    public PidTuning GetGains(TempChannelKind kind)
    {
        var g = _loop.GetGainSchedule(_tc).GainsAt(_loop.GetChannelStatus(_tc).SetpointC);
        return g is null ? new PidTuning(0, 0, 0) : new PidTuning(g.Kp, g.Ki, g.Kd);
    }

    public Task SetGainsAsync(TempChannelKind kind, PidTuning gains, CancellationToken ct)
    {
        if (kind == TempChannelKind.Reactor)
        {
            // 外环增益：Kp ℃/℃、Ki 1/s、Kd s；偏置限幅沿用缺省
            _loop.ConfigureCascade(_tc, gains.Kp, gains.Ki, gains.Kd, Rd105HostControl.FallbackOuterMaxBiasC);
            return Task.CompletedTask;
        }
        // 手填的内环增益：只换三个系数，输出上限与方向照会话开机时配的（LIMITED、反向）走
        _loop.SetManualGains(_tc, gains.Kp, gains.Ki, gains.Kd);
        return Task.CompletedTask;
    }

    /// <summary>继电器法的默认激励：±20% 占空比、0.2 ℃ 回差。回差太小会被噪声触发。</summary>
    private const double RelayAmplitudePercent = 20;
    private const double HysteresisC = 0.2;

    public async Task StartTuningAsync(double setpointC, TempChannelKind kind, CancellationToken ct)
    {
        if (TuningState == TuningState.Running)
            throw new InvalidOperationException("这个通道正在自整定，先取消再重来。");

        await SetStrategyAsync(kind, ct).ConfigureAwait(false);
        TuningState = TuningState.Running;
        _tuningSetpointC = setpointC;
        TuningNote = $"正在 {setpointC:F1} ℃ 附近激起振荡";
        _log("info", $"CH{Channel} 开始自整定：{setpointC:F1} ℃，{KindText(kind)}");

        try
        {
            if (!_loop.IsRunning) _loop.Start();     // 温控器 PID 方式下回路平时不转，整定时转起来
            await _loop.StartAutoTuneAsync(_tc, setpointC, RelayAmplitudePercent, HysteresisC, ct)
                       .ConfigureAwait(false);
        }
        catch
        {
            TuningState = TuningState.Idle;
            TuningNote = "";
            throw;
        }
    }

    public async Task CancelTuningAsync(CancellationToken ct)
    {
        if (TuningState != TuningState.Running) return;
        await _loop.StopChannelAsync(_tc, ct).ConfigureAwait(false);
        TuningState = TuningState.Cancelled;
        TuningNote = "已取消";
        TuningFinished?.Invoke(this, new TuningOutcome(false, null, "操作人取消"));
    }

    private void OnFinished(AutoTuneOutcome o)
    {
        // 只认自己发起的那次：面板「PID 整定」页（Rd105HostPid）发起的整定由它自己收尾、记日志
        if (o.Channel != _tc || TuningState != TuningState.Running) return;

        // 取保守组（Tyreus–Luyben）：算法作者自己的注释就写着温控推荐用这一组——
        // ZN 那组响应快但会超调，控温超调意味着实际把料多加热了一段
        var g = o.Result?.Conservative;
        var gains = g is null ? null : new PidTuning(g.Kp, g.Ki, g.Kd);
        TuningState = o.Success ? TuningState.Succeeded : TuningState.Failed;
        TuningNote = o.Success ? $"整定完成：{gains}" : $"整定失败：{o.Reason}";
        _log(o.Success ? "info" : "warn", $"CH{Channel} {TuningNote}");

        TuningFinished?.Invoke(this, new TuningOutcome(o.Success, gains, o.Reason)
        {
            SetpointC = double.IsNaN(_tuningSetpointC) ? null : _tuningSetpointC,
            Kind = _kind
        });
    }

    private static string KindText(TempChannelKind kind)
        => kind == TempChannelKind.Reactor ? "釜内 Tr（串级）" : "夹套 Tj（单环）";

    public void Detach() => _loop.AutoTuneFinished -= OnFinished;
}
