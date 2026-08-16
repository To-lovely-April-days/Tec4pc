namespace Tec.Core.Recipes;

/// <summary>
/// 配方的撤销 / 重做栈，**按通道分开**。
///
/// 分开是必须的：四条泳道摆在一屏上，在 CH2 上按撤销却把 CH1 的改动回滚了，
/// 比没有撤销更吓人——操作人看不到自己撤掉了什么。
///
/// 存的是整份配方快照（`Recipe.Snapshot()` 本来就是深拷贝）而不是「操作日志」。
/// 一条配方几十步、每步十来个参数，整份存下来也就几 KB；换来的是撤销永远不会
/// 因为某个操作忘了写反向动作而把配方改坏。这个取舍在实验室软件里只有一个答案。
/// </summary>
public sealed class RecipeHistory
{
    /// <summary>每条通道最多记这么多步。再多也没人往回翻，白占内存。</summary>
    public const int Depth = 30;

    private sealed class Lane
    {
        public readonly List<Recipe> Undo = new();
        public readonly List<Recipe> Redo = new();
        /// <summary>最近一次记录的合并键。连着改同一个输入框只留第一笔。</summary>
        public string? LastKey;
    }

    private readonly Dictionary<int, Lane> _lanes = new();

    private Lane Of(int channel)
    {
        if (!_lanes.TryGetValue(channel, out var lane)) _lanes[channel] = lane = new Lane();
        return lane;
    }

    /// <summary>
    /// 在改动**之前**调，把当前样子存下来。
    ///
    /// coalesceKey 用来合并连续的同类改动：在温度输入框里连按几下上下箭头是一次
    /// 编辑意图，不该变成十次撤销。传 null 表示这是一次独立操作，永远单独记一笔。
    /// </summary>
    public void Record(int channel, Recipe before, string? coalesceKey = null)
    {
        var lane = Of(channel);
        if (coalesceKey is not null && lane.LastKey == coalesceKey && lane.Undo.Count > 0) return;

        lane.Undo.Add(before.Snapshot());
        lane.LastKey = coalesceKey;
        if (lane.Undo.Count > Depth) lane.Undo.RemoveAt(0);
        lane.Redo.Clear();          // 走了新的分支，原来的重做链就作废了
    }

    public bool CanUndo(int channel) => Of(channel).Undo.Count > 0;
    public bool CanRedo(int channel) => Of(channel).Redo.Count > 0;

    /// <summary>撤销。返回该退回到的那份配方；没得撤就返回 null。</summary>
    public Recipe? Undo(int channel, Recipe current)
    {
        var lane = Of(channel);
        if (lane.Undo.Count == 0) return null;

        var back = lane.Undo[^1];
        lane.Undo.RemoveAt(lane.Undo.Count - 1);
        lane.Redo.Add(current.Snapshot());
        lane.LastKey = null;        // 撤销之后再改，必须重新记一笔
        return back;
    }

    /// <summary>重做。</summary>
    public Recipe? Redo(int channel, Recipe current)
    {
        var lane = Of(channel);
        if (lane.Redo.Count == 0) return null;

        var forward = lane.Redo[^1];
        lane.Redo.RemoveAt(lane.Redo.Count - 1);
        lane.Undo.Add(current.Snapshot());
        lane.LastKey = null;
        return forward;
    }

    /// <summary>通道被清掉 / 改挂之后，那条道的历史不再对得上，整条丢掉。</summary>
    public void Forget(int channel) => _lanes.Remove(channel);

    public void Clear() => _lanes.Clear();
}
