using Tec.Driver.Abi;
using Tec.Drivers.Rd105;

namespace Tec.Drivers.DualStation;

/// <summary>
/// 一个工位的控温能力：套在 RD105 的 TC 回路外面，唯一多干的一件事是
/// **下发目标前把热源切到对的一侧**（需求 §2）。控温、限值、判到达全部
/// 委托给里面那路——不复制逻辑，只加切换这一层。
/// </summary>
public sealed class DuoTempControl : ITemperatureControl
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
    public double CurrentReactor => Inner.CurrentReactor;
    public double CurrentJacket => Inner.CurrentJacket;
    public IObservable<Sample> Temperature => _s.Samples;

    public async Task SetTargetAsync(TempTarget target, CancellationToken ct)
    {
        await _s.EnsureSourceAsync(_well, target.Value, ct).ConfigureAwait(false);
        await Inner.SetTargetAsync(target, ct).ConfigureAwait(false);
    }

    public async Task RampAsync(double target, double ratePerMin, TempChannelKind kind, CancellationToken ct)
    {
        await _s.EnsureSourceAsync(_well, target, ct).ConfigureAwait(false);
        await Inner.RampAsync(target, ratePerMin, kind, ct).ConfigureAwait(false);
    }

    public Task<bool> WaitReachedAsync(double target, double tolerance, TimeSpan timeout, CancellationToken ct)
        => Inner.WaitReachedAsync(target, tolerance, timeout, ct);

    public Task StopAsync(CancellationToken ct) => Inner.StopAsync(ct);
}
