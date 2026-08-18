namespace Tec.Core.Chemistry;

/// <summary>
/// 配料表的模板化清洗（CH-3.7 的「另存为模板」）。
///
/// 模板存的是**工艺结构**：组分、角色、基准、当量、物性快照、相态、釜容——
/// 这些换一炉还是这一套。跟着某一炉走的数不进模板：
/// 实投 / 实取是那一炉称出来的，批号是那一瓶料的。带着它们存进库，
/// 下一炉应用出来就是一张「已经投过料」的表——实投列里坐着上一炉的数，
/// 操作人看不出它是老的，那正是要防的那种假数据。
/// </summary>
public static class ChargeTemplate
{
    /// <summary>克隆并清掉炉级数据（实投 / 实取 / 批号）。行 Id 原样保留——
    /// 配方步骤的 chemId 指着它，换了引用就断了。</summary>
    public static ChargeTable Strip(ChargeTable table)
    {
        var t = table.Clone();
        foreach (var i in t.Items)
        {
            i.ActualMass = null;
            i.ActualVolume = null;
            i.Batch = "";
        }
        return t;
    }
}
