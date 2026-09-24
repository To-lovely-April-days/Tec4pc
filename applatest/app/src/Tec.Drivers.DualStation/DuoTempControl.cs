using Tec.Driver.Abi;
using Tec.Drivers.Rd105;

namespace Tec.Drivers.DualStation;

/// <summary>
/// 一个工位的控温能力：套在 RD105 的 TC 回路外面，唯一多干的一件事是
/// **下发目标前定挡、把热源切到对的一侧**（需求 §2）。控温、限值、判到达全部
/// 委托给里面那路——不复制逻辑，只加切换这一层。
/// 蒸回流（IRefluxControl）转发给组合会话的跟随环：跟随长在采集循环里，
/// 这里只是开关和状态。
/// </summary>
public sealed class DuoTempControl : ITemperatureControl, IRefluxControl, IHeatSource, ITemperatureStatus
{
    // ── ITemperatureStatus：面板重开时对账用。Active 看的是意图（下发过目标且没停），
    //    不看设备上的 ENABLE——热源切换序列会把它临时关两秒。
    //    显式实现：类上那个 Active 是 IRefluxControl 的（跟随开没开），两回事
    public double? Setpoint => Inner.Setpoint;
    bool ITemperatureStatus.Active => _s.WantEnabled(_well);
    bool ITemperatureStatus.HostLoop => ((ITemperatureStatus)Inner).HostLoop;
    double? ITemperatureStatus.CascadeInnerSetpoint => ((ITemperatureStatus)Inner).CascadeInnerSetpoint;
    string? ITemperatureStatus.Holding => ((ITemperatureStatus)Inner).Holding;
    string? ITemperatureStatus.LastStop => ((ITemperatureStatus)Inner).LastStop;

    private readonly DuoSession _s;
    private readonly int _well;

    internal DuoTempControl(DuoSession s, int well)
    {
        _s = s;
        _well = well;
    }

    private Rd105TemperatureControl Inner => _s.InnerTemp(_well);

    public int Channel => Inner.Channel;
    public TempLimits Limits => Inner.Limits;

    // ── IHeatSource：界面问「这一路现在能往哪个方向出力」──────────────
    public bool TecHeating => _s.TecHeating;
    public bool ElectricAvailable => _s.ElectricReady;
    public bool OnElectric => _s.OnElectric(_well);
    /// <summary>升温挡 / 降温挡（0333）——界面印在热源牌子上；没在控 / 「TEC 加热」启用为 null。</summary>
    public string? Regime => _s.RegimeText(_well);

    public double CurrentReactor => Inner.CurrentReactor;
    public double CurrentJacket => Inner.CurrentJacket;
    public IObservable<Sample> Temperature => _s.Samples;

    public async Task SetTargetAsync(TempTarget target, CancellationToken ct)
    {
        _s.GuardTuning(_well);  // 自整定占着继电器与回路：先拒绝，一个继电器都别动
        var wasOn = _s.WantEnabled(_well);
        _s.NoteStart(_well);    // 这一路要控温了（热源切换按意图判，不按设备上的 ENABLE）
        _s.StopReflux(_well);   // 明确下发新目标 = 下一步接管，跟随环退位
        try
        {
            await _s.EnsureSourceAsync(_well, target.Value, target.Kind, ct).ConfigureAwait(false);
            await Inner.SetTargetAsync(target, ct).ConfigureAwait(false);
        }
        catch
        {
            // 下发被拒（釜内没读数、超出保护范围……）：原来没在控就别留一个「想控温」的旗——
            // 留着的话采集循环会按它合继电器、面板对账也把它当开着（幽灵开）
            if (!wasOn) _s.NoteStop(_well);
            throw;
        }
    }

    public async Task RampAsync(double target, double ratePerMin, TempChannelKind kind, CancellationToken ct)
    {
        _s.GuardTuning(_well);
        var wasOn = _s.WantEnabled(_well);
        _s.NoteStart(_well);
        _s.StopReflux(_well);
        try
        {
            await _s.EnsureSourceAsync(_well, target, kind, ct).ConfigureAwait(false);
            await Inner.RampAsync(target, ratePerMin, kind, ct).ConfigureAwait(false);
        }
        catch
        {
            if (!wasOn) _s.NoteStop(_well);
            throw;
        }
    }

    public Task<bool> WaitReachedAsync(double target, double tolerance, TimeSpan timeout, CancellationToken ct)
        => Inner.WaitReachedAsync(target, tolerance, timeout, ct);

    public Task StopAsync(CancellationToken ct)
    {
        // 停控温是安全路径（自然冷却/E 级程序/中止都走它）：跟随连压制旗一起清，
        // 保证下一拍没有谁再把目标写回去。NoteStop 是给热源切换看的——
        // 切换序列有两秒窗口，正在里头的话不许它切完再把输出打开
        _s.NoteStop(_well);
        _s.SuppressReflux(_well);
        return Inner.StopAsync(ct);
    }

    // ── 蒸回流（夹套跟随）。StopAsync 与 ITemperatureControl 同签名，
    //    显式实现分开：停跟随 ≠ 停控温——目标停在最后一次下发的值上
    Task IRefluxControl.StartAsync(double deltaT, double maxTj, CancellationToken ct)
    {
        _s.GuardTuning(_well);
        return _s.StartRefluxAsync(_well, deltaT, maxTj, ct);
    }

    Task IRefluxControl.StopAsync(CancellationToken ct)
    {
        _s.StopReflux(_well);
        return Task.CompletedTask;
    }

    public bool Active => _s.RefluxActive(_well);
}
