using TecControl.Core.Control;
using Tec.Driver.Abi;

namespace Tec.Drivers.Rd105;

/// <summary>
/// PID 整定与控制策略，直接架在 TecControl.Core 的 HostControlLoop 上——
/// 继电器法自整定、串级（Tr 外环 / Tj 内环）、增益调度那一整套是那边现成的，
/// 这里只做翻译，一行控制算法都不重写。
///
/// 自整定**不进配方指令库**：它要激起温度振荡，是调试 / 维护动作，
/// 必须有人在场发起，只能从手动控制面板调用。
/// </summary>
internal sealed class Rd105Tuning : ITemperatureTuning
{
    /// <summary>温控器上的物理路号。控温回路挂在 TC1（釜内）上。</summary>
    private const int Tc = 1;

    private readonly HostControlLoop _loop;
    private readonly Action<string, string> _log;
    private TempChannelKind _kind = TempChannelKind.Jacket;
    private double _tuningSetpointC = double.NaN;

    public Rd105Tuning(int channel, HostControlLoop loop, Action<string, string> log)
    {
        Channel = channel;
        _loop = loop;
        _log = log;
        _loop.AutoTuneFinished += OnFinished;
    }

    public int Channel { get; }

    public TempChannelKind Strategy => _kind;

    public TuningState TuningState { get; private set; } = TuningState.Idle;

    public string TuningNote { get; private set; } = "";

    public event EventHandler<TuningOutcome>? TuningFinished;

    /// <summary>
    /// 釜内 Tr 走串级（外环盯釜内、内环驱动 TEC），夹套 Tj 走单环。
    /// 这也是配方里「釜内控温 Tr / 夹套控温 Tj」两条指令的落点。
    /// </summary>
    public Task SetStrategyAsync(TempChannelKind kind, CancellationToken ct)
    {
        _kind = kind;
        _loop.SetStrategy(Tc, kind == TempChannelKind.Reactor
            ? ControlStrategy.Cascade
            : ControlStrategy.Direct);
        return Task.CompletedTask;
    }

    public PidTuning GetGains(TempChannelKind kind)
    {
        // 串级时外环盯釜内、内环盯夹套；单环时只有一套
        var g = kind == TempChannelKind.Reactor && _loop.GetStrategy(Tc) == ControlStrategy.Cascade
            ? _loop.GetGainSchedule(Tc).OuterAt(_loop.GetChannelStatus(Tc).SetpointC)?.Gains
            : _loop.GetGainSchedule(Tc).GainsAt(_loop.GetChannelStatus(Tc).SetpointC);
        return g is null ? new PidTuning(0, 0, 0) : new PidTuning(g.Kp, g.Ki, g.Kd);
    }

    public Task SetGainsAsync(TempChannelKind kind, PidTuning gains, CancellationToken ct)
    {
        if (kind == TempChannelKind.Reactor)
            _loop.ConfigureCascade(Tc, gains.Kp, gains.Ki, gains.Kd, OuterMaxBiasC);
        else
            _loop.ConfigurePid(Tc, gains.Kp, gains.Ki, gains.Kd, MaxDutyPercent, invertOutput: false);
        return Task.CompletedTask;
    }

    /// <summary>外环偏置上限（℃）：外环最多把内环设定值推离主设定值这么多。</summary>
    private const double OuterMaxBiasC = 15;
    /// <summary>内环占空比上限。</summary>
    private const double MaxDutyPercent = 100;

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
            await _loop.StartAutoTuneAsync(Tc, setpointC, RelayAmplitudePercent, HysteresisC, ct)
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
        await _loop.StopChannelAsync(Tc, ct).ConfigureAwait(false);
        TuningState = TuningState.Cancelled;
        TuningNote = "已取消";
        TuningFinished?.Invoke(this, new TuningOutcome(false, null, "操作人取消"));
    }

    private void OnFinished(AutoTuneOutcome o)
    {
        if (o.Channel != Tc) return;

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
