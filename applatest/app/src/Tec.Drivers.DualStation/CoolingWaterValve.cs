using Tec.Driver.Abi;

namespace Tec.Drivers.DualStation;

/// <summary>
/// 冷却水电磁阀（整机一只，接 IO8R 的一路 DO，现场是 DO4）。两个工位各拿一份实例——
/// ICapability 要带通道号——读写的都是组合会话里同一只阀的状态。
/// </summary>
public sealed class CoolingWaterValve : ICoolingWaterValve
{
    private readonly DuoSession _s;
    private readonly int _well;

    internal CoolingWaterValve(DuoSession s, int well)
    {
        _s = s;
        _well = well;
    }

    public int Channel => _s.InnerTemp(_well).Channel;
    public bool Available => _s.WaterAvailable;
    public bool IsOpen => _s.WaterOn;
    public string? Status => _s.WaterStatus;
    public bool Closing => _s.WaterClosing;
    public bool Opening => _s.WaterOpening;
    public string? Alert => _s.WaterAlert;
    public bool Ready => _s.WaterReady;
    /// <summary>中途切 TEC 的「开不开冷却水」询问：整机一个槽位（一只阀一台冷水机），两个工位读到同一份。</summary>
    public WaterAsk? Pending => _s.PendingAsk;
    public bool Answer(int id, bool open) => _s.AnswerAsk(id, open);

    /// <summary>面板手动开关：手动开的不会被自动联动关掉；有路在制冷时关不了（拒绝并说明）。
    /// 连了冷水机：开 = 先开阀再开冷水机；关 = 先停冷水机，停稳了阀才关（返回时可能还开着）。</summary>
    public Task SetAsync(bool open, CancellationToken ct) => _s.SetWaterManualAsync(open, ct);
}
