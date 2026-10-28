using Tec.Driver.Abi;

namespace Tec.Hmi.Ui.ViewModels;

/// <summary>
/// 「中途切 TEC 要不要开冷却水」询问的弹窗状态机（不碰 Avalonia，便于单测）。每秒喂一次驱动挂着的问题与
/// 确认弹窗的状态，吐出面板该做的一件事：开窗 / 刷副标题（倒计时）/ 关窗 / 什么都不做。
///
/// 规矩：同一个 Id 只弹一次；人点了取消（Dismiss）这个 Id 不再弹，驱动到时按打开办；别的窗开着先不弹、下一秒再看；
/// 本窗被别的窗盖掉了当没开过，等那张关了再弹；问题收回（null）时本窗还开着就关掉。
/// </summary>
public sealed class WaterAskPrompter
{
    public enum Action { None, Open, RefreshSub, Close }

    private int? _shownId;
    private int? _dismissedId;

    /// <param name="pending">驱动此刻挂着的问题（null = 没有 / 已收回）。</param>
    /// <param name="askOpen">确认弹窗此刻开着没有（不管是谁的）。</param>
    /// <param name="askIsWater">开着的那张是不是本询问的窗。</param>
    public Action Step(WaterAsk? pending, bool askOpen, bool askIsWater)
    {
        if (pending is null)
        {
            _dismissedId = null;
            if (_shownId is null) return Action.None;
            _shownId = null;
            return askOpen && askIsWater ? Action.Close : Action.None;
        }
        if (_shownId == pending.Id)
        {
            if (askOpen && askIsWater) return Action.RefreshSub;
            if (askOpen) return Action.None;            // 被别的窗盖掉了：等那张关了再弹
            _shownId = null;                            // 窗已经不在了（被盖掉后关了）：当没开过
        }
        if (_dismissedId == pending.Id) return Action.None;
        // 本窗还开着、问题却换了（旧的收回、另一路接着问）：用新问题换掉旧窗——别让旧窗留在屏上、倒计时冻住
        if (askOpen && !askIsWater) return Action.None; // 别的窗开着：下一秒再看
        _shownId = pending.Id;
        return Action.Open;
    }

    /// <summary>人点了取消：这个 Id 不再弹。</summary>
    public void Dismiss(int id)
    {
        _dismissedId = id;
        _shownId = null;
    }
}
