using TecControl.Core.Control;
using Xunit;
using Xunit.Abstractions;
using static Tec.Core.Tests.BiasSelfCorrectTwinTests;

namespace Tec.Core.Tests;

/// <summary>
/// 【本地改动】「表只做起点、之后一切以实测为准」（2026-11-09 现场 R1-tc1-20261109-095506，双向挡釜内 19.3 → 50 ℃）：
/// 双向挡表没有 50 ℃ 那行，40 ℃ 那行学到的 3.43 在 15 K 内平着沿用过来、还算「学到的」；这台釜 50 ℃ 实测要 5.1 K
/// （逐拍 CSV 拟合：τ1 1005 s、b 5.05 @50 ℃、随釜温 0.087 K/K、壁滞后 129 s、夹套到壁纯滞后 42 s，RMS 0.07 K）。
/// 现场：夹套目标钉在 58（目标 + 8）36 分钟；49.3 → 50.0 爬了 22 分钟（积分 3.43 → 5.01 慢慢补）；63 分钟过零那一枪把积分打回 3.43、
/// 夹套目标掉 1.6 K、切 TEC，釜内掉到 49.73，73 分钟操作人停控时还没回来。
/// 孪生：那场拟合的釜 + 升温孪生的加热棒 / TEC 夹套，接真回路；提前切换照 DuoSession 判据模拟。时间压缩 <see cref="HeatBoostTwinTests.K"/> 倍。
/// </summary>
public class OuterMeasuredBiasTwinTests(ITestOutputHelper output)
{
    /// <summary>11-09 那场拟合的釜：从 19.34 / 夹套 21.28 起；偏置随釜温变（50 ℃ 5.05、每 K 0.087）。</summary>
    internal static HeatBoostTwinTests.Plant Run095506() =>
        new(19.34, 21.28, 21.28, 21.28, tau1: 1005, b: 5.05, tw: 129, wallDelay: 42, bPerK: 0.087, bAtC: 50);

    internal static HeatBoostTwinTests.Rig Rig50(EarlySwitch sw, (double T, double? B)[] bothRows, HeatBoostLevel boost = HeatBoostLevel.Off,
        Action<HostControlLoop>? tweak = null)
        => new(Run095506(), boost, allowed: boost != HeatBoostLevel.Off, tweak: tweak, bothRows: bothRows,
            onCycle: (r, c) => sw.OnCycle(r, c));

    /// <summary>第一次过零以后积分一拍最多掉多少（过零那一枪打回表值就是这一下）。</summary>
    internal static double MaxDropAfterCross(HeatBoostTwinTests.Rig r, double sp)
    {
        var t = r.Trace();
        var crossed = false;
        var drop = 0.0;
        for (var i = 1; i < t.Length; i++)
        {
            if (!crossed && Math.Round(t[i].Tr, 2) >= sp) crossed = true;      // 回路看的是宇电 0.01 K 取整的读数
            if (crossed) drop = Math.Max(drop, t[i - 1].I - t[i].I);
        }
        return drop;
    }

    /// <summary>第一次过零以后釜内最低掉到哪。</summary>
    internal static double MinAfterCross(HeatBoostTwinTests.Rig r, double sp)
    {
        var t = r.Trace();
        var idx = Array.FindIndex(t, x => Math.Round(x.Tr, 2) >= sp);
        return idx < 0 ? double.NaN : t.Skip(idx).Min(x => x.Tr);
    }

    private void Report(string name, HeatBoostTwinTests.Rig r, EarlySwitch sw)
    {
        BiasSelfCorrectTwinTests.Report(output, name, r, sw, 50);
        output.WriteLine($"   过零后积分一拍最多掉 {MaxDropAfterCross(r, 50):F2}、过零后最低 {MinAfterCross(r, 50):F3}、最后积分 {r.Trace()[^1].I:F2}");
        foreach (var n in r.Notes().Where(n => n.Contains("那一枪") || n.Contains("热平衡") || n.Contains("积分起点")))
            output.WriteLine($"   · {n}");
    }

    [Fact]
    public async Task 现场那场_只有40那行_到站不打回表值_不掉回去_积分落到实测()
    {
        var sw = new EarlySwitch();
        await using var r = Rig50(sw, bothRows: [(40, 3.43)]);
        await r.Loop.StartChannelAsync(1, 50);
        await r.UntilMin(80);
        Report("只有 40 = 3.43（现场那样）", r, sw);

        Assert.True(MaxDropAfterCross(r, 50) <= 0.5, $"过零后积分一拍掉 {MaxDropAfterCross(r, 50):F2}（打回表值了）");
        Assert.True(MinAfterCross(r, 50) >= 50 - 0.12, $"过零后掉到 {MinAfterCross(r, 50):F3}");
        Assert.InRange(r.Overshoot(50), 0, 0.15);
        Assert.True(r.StayInMin(50, 0.05) <= 70, $"稳进 ±0.05 {r.StayInMin(50, 0.05):F1} min");
        Assert.InRange(r.Trace()[^1].I, 5.05 - 0.4, 5.05 + 0.4);
    }

    [Fact]
    public async Task 表对_50是5点05_照样干净()
    {
        var sw = new EarlySwitch();
        await using var r = Rig50(sw, bothRows: [(50, 5.05)]);
        await r.Loop.StartChannelAsync(1, 50);
        await r.UntilMin(65);
        Report("表对（50 = 5.05）", r, sw);

        Assert.Equal(0, sw.Pushes);
        Assert.InRange(r.Overshoot(50), 0, 0.1);
        Assert.True(r.StayInMin(50, 0.05) <= 63, $"稳进 ±0.05 {r.StayInMin(50, 0.05):F1} min");     // 改之前（表对、两枪打回表值）61.7 min、冲 0.084：不该更差
        Assert.True(MaxDropAfterCross(r, 50) <= 0.5);
    }

    [Fact]
    public async Task 表偏高_50给6点5_压一回就停_不来回()
    {
        var sw = new EarlySwitch();
        await using var r = Rig50(sw, bothRows: [(50, 6.5)]);
        await r.Loop.StartChannelAsync(1, 50);
        await r.UntilMin(80);
        Report("表偏高（50 = 6.5，真值 5.05）", r, sw);

        Assert.True(sw.Pushes <= 2, $"压低 {sw.Pushes} 次：{string.Join("、", sw.PushAtMin.Select(m => m.ToString("F1")))} min");
        Assert.True(r.StayInMin(50, 0.05) <= 75, $"稳进 ±0.05 {r.StayInMin(50, 0.05):F1} min");
        Assert.InRange(r.Trace()[^1].I, 5.05 - 0.4, 5.05 + 0.4);
    }

    [Fact]
    public async Task 升温加速保守档_只有40那行_交还按实测_不掉回去()
    {
        var sw = new EarlySwitch();
        await using var r = Rig50(sw, bothRows: [(40, 3.43)], boost: HeatBoostLevel.Conservative);
        await r.Loop.StartChannelAsync(1, 50);
        await r.UntilMin(70);
        Report("升温加速保守档、只有 40 = 3.43", r, sw);

        Assert.Contains(r.Notes(), n => n.Contains("升温加速交回外环"));
        Assert.True(MaxDropAfterCross(r, 50) <= 0.5, $"过零后积分一拍掉 {MaxDropAfterCross(r, 50):F2}");
        Assert.True(MinAfterCross(r, 50) >= 50 - 0.12, $"过零后掉到 {MinAfterCross(r, 50):F3}");
        Assert.InRange(r.Overshoot(50), 0, 0.15);
        Assert.True(r.StayInMin(50, 0.05) <= 60, $"稳进 ±0.05 {r.StayInMin(50, 0.05):F1} min");
        Assert.True(r.MaxJacketOverReactor(1) <= 30 + 1.0, $"夹套 − 釜内最大 {r.MaxJacketOverReactor(1):F2}");
    }
}
