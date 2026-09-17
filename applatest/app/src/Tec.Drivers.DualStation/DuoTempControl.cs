using Tec.Driver.Abi;
using Tec.Drivers.Rd105;

namespace Tec.Drivers.DualStation;

/// <summary>
/// 一个工位的控温能力：套在 RD105 的 TC 回路外面，唯一多干的一件事是
/// **下发目标前把热源切到对的一侧**（需求 §2）。控温、限值、判到达全部
/// 委托给里面那路——不复制逻辑，只加切换这一层。
/// 蒸回流（IRefluxControl）转发给组合会话的跟随环：跟随长在采集循环里，
/// 这里只是开关和状态。
/// </summary>
public sealed class DuoTempControl : ITemperatureControl, IRefluxControl, IHeatSource
{
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

    public double CurrentReactor => Inner.CurrentReactor;
    public double CurrentJacket => Inner.CurrentJacket;
    public IObservable<Sample> Temperature => _s.Samples;

    public async Task SetTargetAsync(TempTarget target, CancellationToken ct)
    {
        _s.NoteStart(_well);    // 这一路要控温了（热源切换按意图判，不按设备上的 ENABLE）
        _s.StopReflux(_well);   // 明确下发新目标 = 下一步接管，跟随环退位
        await _s.EnsureSourceAsync(_well, target.Value, ct).ConfigureAwait(false);
        await Inner.SetTargetAsync(target, ct).ConfigureAwait(false);
    }

    public async Task RampAsync(double target, double ratePerMin, TempChannelKind kind, CancellationToken ct)
    {
        _s.NoteStart(_well);
        _s.StopReflux(_well);
        await _s.EnsureSourceAsync(_well, target, ct).ConfigureAwait(false);
        await Inner.RampAsync(target, ratePerMin, kind, ct).ConfigureAwait(false);
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
        => _s.StartRefluxAsync(_well, deltaT, maxTj, ct);

    Task IRefluxControl.StopAsync(CancellationToken ct)
    {
        _s.StopReflux(_well);
        return Task.CompletedTask;
    }

    public bool Active => _s.RefluxActive(_well);
}
