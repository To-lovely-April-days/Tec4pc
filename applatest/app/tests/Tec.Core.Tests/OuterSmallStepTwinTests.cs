using TecControl.Core.Control;
using Xunit;
using Xunit.Abstractions;
using static Tec.Core.Tests.BiasSelfCorrectTwinTests;

namespace Tec.Core.Tests;

/// <summary>
/// 串级外环 1 K 小台阶（双向挡，165419 那台釜，表对：3.32；提前切换照会话判据）。原来改完误差就在 1 K 趋近带里，趋近那一枪第一拍就打——
/// 积分跟学到的差不到门槛、空打掉；Tr 还没动、积分照攒往外跑 0.5 K（夹套跟上以后就没东西拦），没枪收拾：
/// 43 → 42 掉 0.135、26 分钟才稳；42 → 43 冲 0.156，之后每 12 分钟压一回一直摆（压低松开两枪空打、拽下去那截又攒进积分）。
/// 现在那一枪等 Tr 真往目标走才打（到站稳住了还没打就作废），每回解除压低都拦着积分别往上攒。
/// </summary>
public class OuterSmallStepTwinTests(ITestOutputHelper output)
{
    /// <summary>【本地改动】偏置随釜温 0.087 K/K（11-09 那场拟的；40 ℃ 3.32）：小台阶的平移带着斜率，釜也得有斜率才对得上。</summary>
    internal static HeatBoostTwinTests.Plant Near(double tr0) => new(tr0, tr0 + 4.4, tr0 + 4.4, tr0 + 4.4, tau1: 981.4, b: 3.32, tw: 116.4, wallDelay: 18, bPerK: 0.087, bAtC: 40);

    internal sealed class Step
    {
        public double? At;
    }

    /// <summary>起控 <paramref name="from"/>（釜内从低 0.4 K 起），40 分钟稳住以后改 <paramref name="to"/>。</summary>
    internal static HeatBoostTwinTests.Rig StepRig(double from, double to, bool selfCorrect, EarlySwitch sw, Step st)
        => Rig(3.32, selfCorrect, sw, Near(from - 0.4), (r, _) =>
        {
            if (st.At is null && r.P.SimSeconds >= 40 * 60)
            {
                st.At = r.P.SimSeconds;
                sw.Release(r);                 // 会话下新目标时偏差滑窗清掉（DuoSession.NoteStart），不然 1 K 的跳变算成「往上冲」当场压低
                r.Loop.SetSetpoint(1, to);
            }
        });

    [Fact]
    public async Task 一度小台阶_积分不先跑偏_不冲不来回压()
    {
        Step sd = new(), su = new(), sus = new();
        EarlySwitch wd = new(), wu = new(), wus = new();
        await using var down = StepRig(43, 42, false, wd, sd);
        await using var up = StepRig(42, 43, false, wu, su);
        await using var upSc = StepRig(42, 43, true, wus, sus);
        await Task.WhenAll(down.Loop.StartChannelAsync(1, 43), up.Loop.StartChannelAsync(1, 42), upSc.Loop.StartChannelAsync(1, 42));
        await Task.WhenAll(down.UntilMin(95), up.UntilMin(95), upSc.UntilMin(95));
        Report(output, "43 稳住 → 42（自校正关）", down, wd, 42);
        Report(output, "42 稳住 → 43（自校正关）", up, wu, 43);
        Report(output, "42 稳住 → 43（自校正开）", upSc, wus, 43);

        // 降 1 K：不掉过 0.06、20 分钟起 ±0.05、不压低
        Assert.NotNull(sd.At);
        var afterD = down.Trace().Where(x => x.T >= sd.At!.Value).ToArray();
        var dip = 42 - afterD.Min(x => x.Tr);
        output.WriteLine($"   43 → 42：掉 {dip:F3}");
        Assert.True(dip <= 0.06, $"43 → 42 掉 {dip:F3}（积分在 Tr 动起来以前往外跑了）");
        Assert.DoesNotContain(wd.PushAtMin, m => m * 60 > sd.At!.Value);
        var tailD = afterD.Where(x => x.T >= sd.At!.Value + 20 * 60).Select(x => x.Tr).ToArray();
        Assert.True(tailD.Min() >= 42 - 0.05 && tailD.Max() <= 42 + 0.05, $"43 → 42 改完 20 分钟起 {tailD.Min():F3}…{tailD.Max():F3}");

        // 升 1 K（自校正关 / 开）：不冲过 0.08、一次都不压、25 分钟起 ±0.05；自校正开着也一次都不改（表是对的）
        foreach (var (name, r, sw, st) in new[] { ("自校正关", up, wu, su), ("自校正开", upSc, wus, sus) })
        {
            Assert.NotNull(st.At);
            var after = r.Trace().Where(x => x.T >= st.At!.Value).ToArray();
            var over = after.Max(x => x.Tr) - 43;
            output.WriteLine($"   42 → 43（{name}）：冲 {over:F3}、压低 {sw.PushAtMin.Count(m => m * 60 > st.At!.Value)} 次");
            Assert.True(over <= 0.08, $"42 → 43（{name}）冲 {over:F3}");
            Assert.DoesNotContain(sw.PushAtMin, m => m * 60 > st.At!.Value);
            var tail = after.Where(x => x.T >= st.At!.Value + 25 * 60).Select(x => x.Tr).ToArray();
            Assert.True(tail.Min() >= 43 - 0.05 && tail.Max() <= 43 + 0.05, $"42 → 43（{name}）改完 25 分钟起 {tail.Min():F3}…{tail.Max():F3}");
        }
        Assert.Empty(Fixes(upSc));
    }

    /// <summary>
    /// 到站起控（釜内本来就在目标、没「趋近」过）：趋近那一枪一直没打，学到稳态那会儿作废——不然留到以后，负荷真变了、积分正往新偏置走，
    /// 釜内掉下去又往回走那一下打出来，把积分拽回旧偏置（孪生：偏置 3.32 → 4.32，积分一拍掉 0.9，±0.05 晚 10 分钟进）。
    /// </summary>
    [Fact]
    public async Task 到站起控稳住以后_持续负载变化_趋近那一枪不留到以后打()
    {
        double? d = null;
        var sw = new EarlySwitch();
        await using var r = Rig(3.32, false, sw, new HeatBoostTwinTests.Plant(42, 45.32, 45.32, 45.32, tau1: 981.4, b: 3.32, tw: 116.4, wallDelay: 18),
            (r, _) => { if (d is null && r.P.SimSeconds >= 40 * 60) { d = r.P.SimSeconds; r.P.B = 4.32; } });
        await r.Loop.StartChannelAsync(1, 42);          // 釜内本来就在 42：趋近那一枪打不出来
        await r.UntilMin(80);
        Report(output, "42 到站起控、40 分钟偏置 3.32 → 4.32", r, sw, 42);

        Assert.NotNull(d);
        var after = r.Trace().Where(x => x.T >= d!.Value).ToArray();
        var drop = after.Zip(after.Skip(1), (a, b) => a.I - b.I).Max();
        output.WriteLine($"   扰动以后积分一拍最多掉 {drop:F3}");
        Assert.DoesNotContain(sw.PushAtMin, m => m * 60 > d!.Value);
        Assert.True(drop <= 0.3, $"扰动以后积分一拍掉 {drop:F3}（没打的趋近那一枪留到扰动回升时打了、拽回旧偏置）");
        var tail = after.Where(x => x.T >= d!.Value + 18 * 60).Select(x => x.Tr).ToArray();
        Assert.True(tail.Min() >= 42 - 0.05 && tail.Max() <= 42 + 0.05, $"扰动 18 分钟以后 {tail.Min():F3}…{tail.Max():F3}");
    }
}

/// <summary>
/// 没校正过的目标上（自校正关）压低松开：两枪照打，但松开那会儿釜内在目标上面、积分跟表差不到门槛，多半空打掉；压低把釜内拽到目标下面那一截
/// 原来照样攒进积分，回头冲过又压——42 稳住以后偏置少 1 K 八分钟，原来每 12 分钟压一回（100 分钟里 5 回）、还在 41.69–42.15 摆。现在松开以后釜内在目标下面时积分不往上攒。
/// </summary>
public class OuterReleaseGuardTwinTests(ITestOutputHelper output)
{
    [Fact]
    public async Task 没校正过的目标压低松开_积分不攒_不来回压()
    {
        double? d = null;
        var back = false;
        var sw = new EarlySwitch();
        await using var r = Rig(3.32, false, sw, OuterSmallStepTwinTests.Near(41.6), (r, _) =>
        {
            var t = r.P.SimSeconds;
            if (d is null && t >= 40 * 60)
            {
                d = t;
                r.P.B = 2.3;
            }
            else if (d is { } dd && !back && t >= dd + 8 * 60)
            {
                r.P.B = 3.32;
                back = true;
            }
        });
        await r.Loop.StartChannelAsync(1, 42);
        await r.UntilMin(100);
        Report(output, "42 稳住后偏置 2.3 八分钟（自校正关）", r, sw, 42);

        Assert.NotNull(d);
        Assert.True(back, "扰动没撤");
        var pushes = sw.PushAtMin.Where(m => m * 60 > d!.Value).ToArray();
        output.WriteLine($"   扰动以后压低 {string.Join("、", pushes.Select(m => m.ToString("F1")))} min");
        Assert.NotEmpty(pushes);                                                   // 扰动真压了
        Assert.True(pushes.Length <= 2, $"扰动以后压低 {pushes.Length} 次（一压一攒来回摆）");
        Assert.DoesNotContain(pushes, m => m * 60 > d!.Value + 25 * 60);
        var tail = r.Trace().Where(x => x.T >= d!.Value + 30 * 60).Select(x => x.Tr).ToArray();
        Assert.True(tail.Min() >= 42 - 0.05 && tail.Max() <= 42 + 0.05, $"扰动 30 分钟以后 {tail.Min():F3}…{tail.Max():F3}");
    }

    /// <summary>按现场分钟改釜的偏置（扰动）：(分钟, 偏置) 依次生效。</summary>
    private static Action<HeatBoostTwinTests.Rig, ChannelCycleInfo> Loads(params (double Min, double B)[] at)
    {
        var i = 0;
        return (r, _) =>
        {
            while (i < at.Length && r.P.SimSeconds >= at[i].Min * 60) r.P.B = at[i++].B;
        };
    }

    private static double BelowMin(HeatBoostTwinTests.Rig r, double sp, double fromMin)
    {
        var t = r.Trace().Where(x => x.T >= fromMin * 60).ToArray();
        var m = 0.0;
        for (var i = 1; i < t.Length; i++)
            if (t[i].Tr < sp - 0.1) m += (t[i].T - t[i - 1].T) / 60;
        return m;
    }

    /// <summary>压低松开以后的坑（坑底以后）往回冲多少。</summary>
    private static double Recovery(HeatBoostTwinTests.Rig r, double sp, double fromMin, double toMin)
    {
        var t = r.Trace().Where(x => x.T >= fromMin * 60 && x.T <= toMin * 60).ToArray();
        var bottom = t.MinBy(x => x.Tr)!.T;
        return t.Where(x => x.T >= bottom).Max(x => x.Tr) - sp;
    }

    /// <summary>
    /// 放热把釜内顶过目标、压低松开（表对、积分对）：松开以后那个坑全是压低拽的，坑再深也一直拦到釜内回目标——
    /// 曾试过「拦下的积分过了 0.7 K 就当真缺偏置放开」，坑一深在坑底就凑满放开了，往回走那一两分钟攒的就是下一回冲过头的电荷：
    /// 放热 2 K 四分钟回来冲 0.05 → 0.09；放热 3 K 冲到 +0.1、又压一回；壁慢的釜（壁滞后 36 s、壁时间常数 200 s）放热 2 K 八分钟压 4 回、一直摆。
    /// 只按时间拦：都只压放热那一回、回来不过 0.06（HEAD 管不着的目标没拦：压 4–6 回）。壁慢的那台不扰动也自己慢摆 ±0.08（外环按 165419 那台调的），
    /// 它只看压几回、回来不过 0.1。
    /// </summary>
    [Fact]
    public async Task 放热压低松开_坑深也一直拦到回目标_只压一回不冲()
    {
        var slow = new HeatBoostTwinTests.Plant(41.6, 46.0, 46.0, 46.0, tau1: 981.4, b: 3.32, tw: 200, wallDelay: 36);
        var cases = new (string Name, HeatBoostTwinTests.Plant Plant, (double, double)[] Loads, double EndMin, double OverMax, double Band)[]
        {
            ("放热 2 K 四分钟", OuterSmallStepTwinTests.Near(41.6), new[] { (40.0, 1.32), (44.0, 3.32) }, 44, 0.06, 0.05),
            ("放热 3 K 四分钟", OuterSmallStepTwinTests.Near(41.6), new[] { (40.0, 0.32), (44.0, 3.32) }, 44, 0.06, 0.05),
            ("壁慢的釜放热 2 K 八分钟", slow, new[] { (40.0, 1.32), (48.0, 3.32) }, 48, 0.10, 0.10),
        };
        var sws = cases.Select(_ => new EarlySwitch()).ToArray();
        var rigs = cases.Select((c, i) => Rig(3.32, false, sws[i], c.Plant, Loads(c.Loads))).ToArray();
        try
        {
            await Task.WhenAll(rigs.Select(r => r.Loop.StartChannelAsync(1, 42)));
            await Task.WhenAll(rigs.Select(r => r.UntilMin(100)));
            for (var i = 0; i < cases.Length; i++)
            {
                var (name, _, _, end, overMax, band) = cases[i];
                var r = rigs[i];
                Report(output, $"42 稳住后{name}（自校正关）", r, sws[i], 42);
                var pushes = sws[i].PushAtMin.Where(m => m >= 40).ToArray();
                var over = Recovery(r, 42, end, end + 30);
                output.WriteLine($"   {name}：压低 {pushes.Length} 次（{string.Join("、", pushes.Select(m => m.ToString("F1")))} min）、回来冲 {over:+0.000;-0.000}");
                Assert.Single(pushes);                                                        // 只有放热那一回
                Assert.True(over <= overMax, $"{name}：回来冲 {over:F3}（坑底放开积分、往回走那下攒进去了）");
                var tail = r.Trace().Where(x => x.T >= (end + 35) * 60).Select(x => x.Tr).ToArray();
                Assert.True(tail.Min() >= 42 - band && tail.Max() <= 42 + band, $"{name}：扰动撤了 35 分钟以后 {tail.Min():F3}…{tail.Max():F3}");
            }
        }
        finally
        {
            foreach (var r in rigs) await r.DisposeAsync();
        }
    }

    /// <summary>
    /// 代价：松开以后真缺偏置（不是压低拽的），保护照样拦到拿住期满，釜内这段只靠比例往上走——不冲，只是在目标下面待得久。
    /// · 42 稳住、放热 4 分钟压一次，松开 2 分钟后散热变大（3.32 → 5.3、不回来）：Tr < 41.9 共 29 分钟左右（HEAD 不拦 12 分钟，但 HEAD 单纯压低的坑一直来回压）；
    /// · 表 2.6（实际 3.32）、第一次学到以前放热压一次、两枪把积分打回 2.6：20 分钟左右（HEAD 9 分钟）。
    /// 这里只卡住别更差、别冲：只压放热那一回、回来不过 0.06。
    /// </summary>
    [Fact]
    public async Task 松开以后真缺偏置_拦满拿住期_不冲_在目标下面待的有数()
    {
        var swE = new EarlySwitch();
        var swC = new EarlySwitch();
        await using var e = Rig(3.32, false, swE, OuterSmallStepTwinTests.Near(41.6), Loads((40, 2.3), (44, 3.32), (46, 5.3)));
        await using var c = Rig(2.6, false, swC, OuterSmallStepTwinTests.Near(41.6), Loads((12, 2.3), (20, 3.32)));
        await Task.WhenAll(e.Loop.StartChannelAsync(1, 42), c.Loop.StartChannelAsync(1, 42));
        await Task.WhenAll(e.UntilMin(90), c.UntilMin(70));
        Report(output, "42 放热压一次、松开后散热变大（自校正关）", e, swE, 42);
        Report(output, "表 2.6、第一次学到以前放热压一次（自校正关）", c, swC, 42);

        foreach (var (name, r, sw, from, limit) in new[] { ("散热变大", e, swE, 40.0, 34.0), ("表偏低", c, swC, 12.0, 24.0) })
        {
            var pushes = sw.PushAtMin.Where(m => m >= from).ToArray();
            var below = BelowMin(r, 42, from);
            var peak = r.Trace().Where(x => x.T >= (from + 15) * 60).Max(x => x.Tr) - 42;
            output.WriteLine($"   {name}：压低 {pushes.Length} 次、Tr < 41.9 共 {below:F1} min、回来以后最高 {peak:+0.000;-0.000}");
            Assert.Single(pushes);                                                        // 只有放热那一次
            Assert.True(below <= limit, $"{name}：Tr < 41.9 共 {below:F1} min");
            Assert.True(peak <= 0.06, $"{name}：回来以后冲到 +{peak:F3}");
        }
    }
}
