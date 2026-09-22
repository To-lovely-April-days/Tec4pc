using Tec.LimitTest.Records;

namespace Tec.LimitTest.Runs;

/// <summary>
/// 一轮极限测试的设置。界面上改，开始时拍一份快照给 Runner。
/// 缺省值照 docs/极限测试清单.md ⑯ ⑰ ⑳ 与那份手填模板：−40 / 150 / 25 ℃，60 / 60 / 120 min。
/// </summary>
public sealed class TestSettings
{
    /// <summary>冷却水温度分组（℃），用户定的：20 / 15 / 7。顺序就是跑的顺序。</summary>
    public double[] Waters { get; set; } = { 20, 15, 7 };

    public double MinTarget { get; set; } = -40;
    public double MaxTarget { get; set; } = 150;
    public double HoldTarget { get; set; } = 25;

    /// <summary>最低温 / 最高温各跑多久（min，1 min 一行）。</summary>
    public int MinMinutes { get; set; } = 60;
    public int MaxMinutes { get; set; } = 60;
    /// <summary>恒温记录多久（min，2 min 一行）——从两工位都到目标之后起算。</summary>
    public int HoldMinutes { get; set; } = 120;
    public int HoldStepMinutes { get; set; } = 2;

    /// <summary>判到达：夹套进到目标 ± 这个数（℃）。</summary>
    public double ReachTol { get; set; } = 0.5;

    /// <summary>提前结束：最近 SettleMinutes 分钟里两工位夹套的变化都不到 SettleBand ℃（最低温 / 最高温）。0 = 不提前结束。</summary>
    public double SettleBand { get; set; } = 0.2;
    public int SettleMinutes { get; set; } = 10;

    /// <summary>恒温：等多久没到目标就放弃（min）。</summary>
    public int HoldReachMaxMinutes { get; set; } = 90;

    /// <summary>两项测试之间回到这个温度再做下一项（℃，进到 ± ReturnBand 或等满 ReturnMaxMinutes）。</summary>
    public double ReturnTemp { get; set; } = 25;
    public double ReturnBand { get; set; } = 3;
    public int ReturnMaxMinutes { get; set; } = 30;

    /// <summary>两工位夹套都连着这么多秒没读数就中止（链路断了）。</summary>
    public int LinkLossSeconds { get; set; } = 30;

    public string Operator { get; set; } = "";
    /// <summary>环境温度 ℃（手填）。</summary>
    public double? Ambient { get; set; }
    public string Patch { get; set; } = "";

    public TestSettings Clone()
    {
        var c = (TestSettings)MemberwiseClone();
        c.Waters = (double[])Waters.Clone();
        return c;
    }

    public BookSpec ToBookSpec() => new()
    {
        Waters = (double[])Waters.Clone(),
        MinRows = Math.Max(2, MinMinutes + 1),
        MaxRows = Math.Max(2, MaxMinutes + 1),
        HoldRows = Math.Max(2, HoldMinutes / Math.Max(1, HoldStepMinutes) + 1),
        HoldStepMin = Math.Max(1, HoldStepMinutes)
    };

    public double TargetOf(TestKind kind) => kind switch
    {
        TestKind.MinTemp => MinTarget,
        TestKind.MaxTemp => MaxTarget,
        _ => HoldTarget
    };
}

public enum CellState
{
    /// <summary>没勾。</summary>
    Unselected,
    Pending,
    Running,
    /// <summary>跑满计划时长。</summary>
    Done,
    /// <summary>夹套稳住了，提前结束。</summary>
    EndedEarly,
    /// <summary>中止（原因在 Note）。</summary>
    Aborted,
    /// <summary>操作人跳过。</summary>
    Skipped
}

/// <summary>矩阵里的一格：某一项 × 某个水温。</summary>
public sealed class PlanCell
{
    public PlanCell(TestKind kind, double water)
    {
        Kind = kind;
        Water = water;
    }

    public TestKind Kind { get; }
    public double Water { get; }
    public bool Selected { get; set; } = true;
    public CellState State { get; set; } = CellState.Pending;
    public string Note { get; set; } = "";

    public string StateText => State switch
    {
        CellState.Unselected => "不测",
        CellState.Pending => "待测",
        CellState.Running => "进行中",
        CellState.Done => "完成",
        CellState.EndedEarly => "提前结束",
        CellState.Aborted => "中止",
        _ => "跳过"
    };

    public override string ToString() => $"{TestKinds.Name(Kind)} @ 冷却水 {Water:0.#} ℃";
}
