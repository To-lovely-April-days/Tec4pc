using System.Reflection;
using TecControl.Core.Control;
using Tec.Drivers.Rd105;
using Xunit;
using Xunit.Abstractions;
using static Tec.Core.Tests.BiasSelfCorrectTwinTests;

namespace Tec.Core.Tests;

/// <summary>
/// 外环偏置自校正改完以后改目标（第二轮复查找出来的几条）：同一台 165419 釜（表里 40 ℃ 5.53、实际 3.32）从 39.6 ℃ 起，
/// 16 分半左右改一次（≈ 3.24），拿住到 26 分半。各台互不相干、一起跑。
/// </summary>
public class BiasSelfCorrectRetargetTwinTests(ITestOutputHelper output)
{
    private const double K = HeatBoostTwinTests.K;

    internal static object StateOf(HostControlLoop loop)
        => typeof(HostControlLoop).GetMethod("State", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(loop, new object[] { 1 })!;

    /// <summary>
    /// · 改到管着的附近（40 → 41.5，拿住期过了）：改目标是新的一段趋近，两枪照打——开头釜内还没动、积分照攒，
    ///   不放掉冲 0.18、压低两次、又改一次（只拦「解除压低」那种重新武装时的毛病）。这台表里 43 那行也错成 5.53：
    ///   平移按改之前的表算——拿现在的表算，40 稳住学完那行就是实测、差归零，41.5 插到 43 那行按 4.31 打，冲 0.23、压低两次。
    ///   还模拟 SetSetpoint 后一下立的重新武装落到下一拍（回路线程收走改目标以后再立一次）：不能被认成解除压低、两枪不打；
    /// · 先降到 39、再改回 40（原目标本身）：同上，两枪照打；
    /// · 离开去管不着的 37（表里 37 那行是对的）、待 45 分钟、再改 41：实测值不跟着学习挪到 37——
    ///   挪了的话回来按表里 5.53 打两枪，又冲 0.3、压低、再改一次；
    /// · 还没改过、过零 8 分钟（窗口攒了大半）时 40 改 40.05：窗口和锁存清掉，按 40.05 过零 + 一个窗口以后才改。
    /// </summary>
    [Fact]
    public async Task 改完改目标_附近和改回原目标两枪照打_去远处回来还按实测_没改过改目标窗口重攒()
    {
        // 1) 40 → 41.5（表里 43 那行也错；改目标以后补一下迟到的重新武装）
        double? fixA = null, stepA = null;
        var lateRearm = false;
        bool? lateShots = null;
        DateTime lateGuard = default;
        object? stA = null;
        var swA = new EarlySwitch();
        await using var a = Rig(5.53, true, swA, NearTarget(), (r, c) =>
        {
            var t = r.P.SimSeconds;
            if (fixA is null && c.OuterI < 4.5) fixA = t;
            else if (fixA is { } f && stepA is null && t >= f + 18.5 * 60)
            {
                stepA = t;
                r.Loop.SetSetpoint(1, 41.5);
            }
            else if (stepA is not null && !lateRearm)
            {
                stA ??= StateOf(r.Loop);
                if ((int)stA.GetType().GetField("OuterTargetChangedFlag")!.GetValue(stA)! == 0)
                {
                    stA.GetType().GetField("OuterRearmedFlag")!.SetValue(stA, 1);     // SetSetpoint 最后那下 Volatile.Write 晚到
                    lateRearm = true;
                }
            }
            else if (lateRearm && lateShots is null && (int)stA!.GetType().GetField("OuterRearmedFlag")!.GetValue(stA)! == 0)
            {
                // 回路线程收走了迟到的那下：分类没变（还是改目标那种），保护也没武装
                lateShots = (bool)stA.GetType().GetField("ShotsFromPushRelease")!.GetValue(stA)!;
                lateGuard = (DateTime)stA.GetType().GetField("ReleaseGuardUntil")!.GetValue(stA)!;
            }
        }, tweak: l => l.GetGainSchedule(1, GainTable.Both).Learn(new GainPoint(43, new PidGains(9.4, 0.037 * K, 175 / K), 0, 0,
            new PidGains(2.5, 0.01 * K, 0), 8, SteadyBiasC: 5.53, HeatRatio: 3.5)));

        // 2) 40 → 39 → 40（离开那段不按提前判据压）
        double? fixB = null, downB = null, backB = null;
        var swB = new EarlySwitch();
        await using var b = Rig(5.53, true, swB, NearTarget(), (r, c) =>
        {
            var t = r.P.SimSeconds;
            if (fixB is null && c.OuterI < 4.5) fixB = t;
            else if (fixB is { } f && downB is null && t >= f + 18.5 * 60)
            {
                downB = t;
                swB.Release(r);
                swB.Paused = true;
                r.Loop.SetSetpoint(1, 39);
            }
            else if (downB is { } d && backB is null && t >= d + 10 * 60)
            {
                backB = t;
                swB.Paused = false;
                r.Loop.SetSetpoint(1, 40);
            }
        });

        // 3) 40 → 37（待 45 分钟）→ 41
        double? fixC = null, awayC = null, backC = null;
        var swC = new EarlySwitch();
        await using var c3 = Rig(5.53, true, swC, NearTarget(), (r, c) =>
        {
            var t = r.P.SimSeconds;
            if (fixC is null && c.OuterI < 4.5) fixC = t;
            else if (fixC is { } f && awayC is null && t >= f + 120)
            {
                awayC = t;
                swC.Release(r);
                swC.Paused = true;
                r.Loop.SetSetpoint(1, 37);
            }
            else if (awayC is { } aw && backC is null && t >= aw + 45 * 60)
            {
                backC = t;
                swC.Paused = false;
                r.Loop.SetSetpoint(1, 41);
            }
        }, tweak: l => l.GetGainSchedule(1, GainTable.Both).Learn(new GainPoint(37, new PidGains(9.4, 0.037 * K, 175 / K), 0, 0,
            new PidGains(2.5, 0.01 * K, 0), 8, SteadyBiasC: 3.32, HeatRatio: 3.5)));

        // 4) 还没改、过零 8 分钟改 40.05
        double? crossD = null, changedD = null;
        var swD = new EarlySwitch();
        await using var d4 = Rig(5.53, true, swD, NearTarget(), (r, c) =>
        {
            var t = r.P.SimSeconds;
            if (crossD is null && Math.Round(r.P.Tr, 2) >= 40) crossD = t;
            else if (crossD is { } ca && changedD is null && t >= ca + 480)
            {
                changedD = t;
                r.Loop.SetSetpoint(1, 40.05);
            }
        });

        await Task.WhenAll(a.Loop.StartChannelAsync(1, 40), b.Loop.StartChannelAsync(1, 40), c3.Loop.StartChannelAsync(1, 40),
            d4.Loop.StartChannelAsync(1, 40));
        await Task.WhenAll(a.UntilMin(82), b.UntilMin(92), c3.UntilMin(106), d4.UntilMin(50));
        Report(output, "改完 40 → 41.5", a, swA, 41.5);
        Report(output, "改完 40 → 39 → 40", b, swB);
        Report(output, "改完 40 → 37（45 min）→ 41", c3, swC, 41);
        Report(output, "没改过、过零 8 分钟 40 → 40.05", d4, swD, 40.05);

        // 1) 41.5：不冲、不压低、不再改
        Assert.NotNull(stepA);
        Assert.True(lateRearm, "迟到的重新武装没补上");
        Assert.Single(Fixes(a));
        var afterA = a.Trace().Where(x => x.T >= stepA!.Value).ToArray();
        Assert.True(afterA.Max(x => x.Tr) <= 41.5 + 0.1, $"改 41.5 以后冲到 {afterA.Max(x => x.Tr):F3}");
        Assert.DoesNotContain(swA.PushAtMin, m => m * 60 >= stepA!.Value);
        var tailA = afterA.Where(x => x.T >= stepA!.Value + 30 * 60).Select(x => x.Tr).ToArray();
        Assert.True(tailA.Min() >= 41.45 && tailA.Max() <= 41.55, $"改 41.5 后 30 分钟起 Tr {tailA.Min():F3}…{tailA.Max():F3}");

        // 2) 改回 40：不冲、不压低、不再改
        Assert.NotNull(backB);
        Assert.Single(Fixes(b));
        var afterB = b.Trace().Where(x => x.T >= backB!.Value).ToArray();
        Assert.True(afterB.Max(x => x.Tr) <= 40 + 0.1, $"改回 40 以后冲到 {afterB.Max(x => x.Tr):F3}");
        Assert.DoesNotContain(swB.PushAtMin, m => m * 60 >= backB!.Value);

        // 3) 去 37 回 41：积分不被拽回 5.53、不冲、不再改
        Assert.NotNull(backC);
        Assert.Single(Fixes(c3));
        var afterC = c3.Trace().Where(x => x.T >= backC!.Value).ToArray();
        Assert.True(afterC.Max(x => x.I) <= 3.32 + 0.5,
            $"回 41 以后积分最高 {afterC.Max(x => x.I):F2}（两枪按表里那个 5.53 打了，或者压低以后积分又攒上去了）");
        Assert.True(afterC.Max(x => x.Tr) <= 41 + 0.2, $"回 41 以后冲到 {afterC.Max(x => x.Tr):F3}");

        // 4) 改 40.05：按新目标过零 + 一个窗口以后才改
        Assert.NotNull(changedD);
        Assert.NotEmpty(swD.FixAt);                   // 真改过：下面那条才查了东西
        var t0 = d4.Trace()[0].T;
        var changed = changedD!.Value - t0;
        var crossNew = Crossings(d4, at => at < changed ? 40 : 40.05).First(x => x > changed + 1);
        var first = swD.FixAt[0] - t0;
        Assert.True(first >= crossNew + 0.95 * 600, $"改目标 {changed / 60:F2} min 后 {first / 60:F2} min 就改了，新目标第一次过零 {crossNew / 60:F2} min");

        // 1) 迟到的重新武装不能被认成解除压低（被认成的话保护把趋近段积分冻住、正好不冲，上面几条看不出来）
        Assert.True(lateShots == false && lateGuard == default,
            $"迟到的重新武装被认成解除压低：ShotsFromPushRelease {lateShots}、ReleaseGuardUntil {lateGuard:O}");
    }
}

/// <summary>外环偏置自校正：估值差一截以后、学稳态偏置、附近目标平移（第二轮复查找出来的几条）。</summary>
public class BiasSelfCorrectLearnTwinTests(ITestOutputHelper output)
{
    private const double K = HeatBoostTwinTests.K;

    /// <summary>
    /// · 改完以后釜的偏置变了（3.32 → 2.92 / 2.7，估值高 0.3 / 0.5 K，都不到再改一次的 1.0 K）：拿住期过了，解除压低重新武装的两枪
    ///   不再把积分拽回那个估值、压低拽下去那段不往上攒；积分只在冲过目标那几分钟往下退一点，光靠它要一压一冲摆一个多小时
    ///   （孪生：估高 0.6 K 压低 6 回、90 分钟才稳）——冲过目标被压低过一回，解除以后整个窗口估出来的低了就按小门槛（0.2 K）再改一次：
    ///   改完最多压两回（拿住期里那一回 + 拿住期过了一回），拿住期过了 20 分钟起不冲过 40.1、45 分钟起稳在 ±0.05；
    /// · 表里偏高 0.5 K（3.82、真值 3.32，不到 1.0 K、原来一直改不了）：压低两回以后按小门槛改一次，改完不再压、稳住；
    /// · 改目标 / 走曲线时学稳态偏置的低通从头来：釜的偏置随温度变（30 ℃ 1.66、40 ℃ 3.32，表里两行都对），在 30 ℃ 稳住学过以后
    ///   直接改 40、或走一条曲线到 40——不清的话低通从 1.66 起、写进 40 那行的是 ≈ 3.0，表被写偏（曲线不走改目标那条，单独一路清）。
    /// </summary>
    [Fact]
    public async Task 估值高了不来回压低_改目标学偏置从头来()
    {
        var his = new List<(double B, HeatBoostTwinTests.Rig R, EarlySwitch Sw, Func<double?> Fix)>();
        foreach (var bNew in new[] { 2.92, 2.7 })
        {
            double? fixT = null;
            var sw = new EarlySwitch();
            var r0 = Rig(5.53, true, sw, NearTarget(), (r, c) =>
            {
                if (fixT is null && c.OuterI < 4.5)
                {
                    fixT = r.P.SimSeconds;
                    r.P.B = bNew;
                }
            });
            his.Add((bNew, r0, sw, () => fixT));
        }
        var swT = new EarlySwitch();
        await using var tblHi = Rig(3.82, true, swT, NearTarget());

        const double b40 = 3.32, perK = 0.166;
        var b30 = b40 - 10 * perK;
        double? changedAt = null;
        var minRow40 = double.PositiveInfinity;
        await using var learn = new HeatBoostTwinTests.Rig(
            new HeatBoostTwinTests.Plant(30, 30 + b30, 30 + b30, 30 + b30, tau1: 981.4, b: b40, tw: 116.4, wallDelay: 18, bPerK: perK, bAtC: 40),
            HeatBoostLevel.Off, allowed: false, tweak: l =>
            {
                l.OuterBiasCheckWindow = TimeSpan.Zero;
                l.GetGainSchedule(1, GainTable.Both).Learn(new GainPoint(30, new PidGains(9.4, 0.037 * K, 175 / K), 0, 0,
                    new PidGains(2.5, 0.01 * K, 0), 8, SteadyBiasC: b30, HeatRatio: 3.5));
            }, learnedBias: b40, rows: new[] { 40.0 }, onCycle: (r, c) =>
            {
                if (changedAt is null && r.P.SimSeconds >= 10 * 60)
                {
                    changedAt = r.P.SimSeconds;
                    r.Loop.SetSetpoint(1, 40);
                }
                else if (changedAt is not null && r.Loop.BothSteadyBias(1, 40) is { } v)
                    minRow40 = Math.Min(minRow40, v);
            });
        double? profAt = null;
        var minRow40P = double.PositiveInfinity;
        await using var learnProf = new HeatBoostTwinTests.Rig(
            new HeatBoostTwinTests.Plant(30, 30 + b30, 30 + b30, 30 + b30, tau1: 981.4, b: b40, tw: 116.4, wallDelay: 18, bPerK: perK, bAtC: 40),
            HeatBoostLevel.Off, allowed: false, tweak: l =>
            {
                l.OuterBiasCheckWindow = TimeSpan.Zero;
                l.GetGainSchedule(1, GainTable.Both).Learn(new GainPoint(30, new PidGains(9.4, 0.037 * K, 175 / K), 0, 0,
                    new PidGains(2.5, 0.01 * K, 0), 8, SteadyBiasC: b30, HeatRatio: 3.5));
            }, learnedBias: b40, rows: new[] { 40.0 }, onCycle: (r, c) =>
            {
                if (profAt is null && r.P.SimSeconds >= 10 * 60)
                {
                    profAt = r.P.SimSeconds;
                    r.Loop.StartProfile(1, SegmentProfile.Linear(40, 0, holdMinutes: 2.0 / K));     // 阶跃 + 保温 2 分钟（压缩时间下坡速超曲线上限）
                }
                else if (profAt is not null && r.Loop.BothSteadyBias(1, 40) is { } v)
                    minRow40P = Math.Min(minRow40P, v);
            });

        try
        {
            await Task.WhenAll(his.Select(h => h.R.Loop.StartChannelAsync(1, 40))
                .Append(tblHi.Loop.StartChannelAsync(1, 40)).Append(learn.Loop.StartChannelAsync(1, 30)).Append(learnProf.Loop.StartChannelAsync(1, 30)));
            await Task.WhenAll(his.Select(h => h.R.UntilMin(100)).Append(tblHi.UntilMin(100)).Append(learn.UntilMin(60)).Append(learnProf.UntilMin(68)));
            foreach (var h in his) Report(output, $"改完偏置变成 {h.B}（估值高）", h.R, h.Sw);
            Report(output, "表里 3.82（偏高 0.5）", tblHi, swT);
            AssertHigh(his);
        }
        finally
        {
            foreach (var h in his) await h.R.DisposeAsync();
        }
        HeatBoostTwinTests.Dump(output, "30 稳住学过、改 40", learn, 40);
        output.WriteLine($"   40 那行最低 {minRow40:F3}（对的 {b40:F2}）、最后 {learn.Loop.BothSteadyBias(1, 40):F3}；30 那行 {learn.Loop.BothSteadyBias(1, 30):F3}");
        HeatBoostTwinTests.Dump(output, "30 稳住学过、走曲线到 40", learnProf, 40);
        output.WriteLine($"   40 那行最低 {minRow40P:F3}、最后 {learnProf.Loop.BothSteadyBias(1, 40):F3}");

        // 表里偏高 0.5：按小门槛改一次（冲过目标被压低过那种），改完不再压、稳住
        var fixesT = Fixes(tblHi);
        Assert.Single(fixesT);
        Assert.Contains("冲过目标被压低过", fixesT[0]);
        Assert.Contains("+3.82 K 跟实测差", fixesT[0]);          // 差 0.5 K 也说表偏了（不说「对得上、不用改」）
        var fixTbl = swT.FixAt[0];
        Assert.True(swT.Pushes <= 2, $"表偏高 0.5：压低 {swT.Pushes} 次");
        Assert.DoesNotContain(swT.PushAtMin, m => m * 60 > fixTbl);
        var tT = tblHi.Trace();
        var afterFix = tT.Where(x => x.T >= fixTbl).ToArray();
        var backBelow = afterFix.First(x => x.Tr <= 40).T;              // 改的那一下正赶上第二回压低的峰在往下落：从回到目标下面起看
        var afterT = afterFix.Where(x => x.T >= backBelow).Select(x => x.Tr).ToArray();
        Assert.True(afterT.Max() <= 40 + 0.1, $"表偏高 0.5：改完回到目标下面以后又冲到 {afterT.Max():F3}");
        var lateT = tT.Where(x => x.T >= fixTbl + 35 * 60).Select(x => x.Tr).ToArray();
        Assert.True(lateT.Min() >= 40 - 0.05 && lateT.Max() <= 40 + 0.05, $"表偏高 0.5：改完 35 分钟起 Tr {lateT.Min():F3}…{lateT.Max():F3}");

        Assert.NotNull(changedAt);
        var lt0 = learn.Trace()[0].T;
        var tail = learn.Trace().Where(x => x.T - lt0 >= 50 * 60).Select(x => x.Tr).ToArray();
        Assert.True(tail.Min() >= 40 - 0.05 && tail.Max() <= 40 + 0.05, $"50 分钟起 Tr {tail.Min():F3}…{tail.Max():F3}（没稳住，学习没跑）");
        Assert.True(minRow40 >= b40 - 0.15, $"40 那行被写到 {minRow40:F3}（旧目标 30 ℃ 的偏置 {b30:F2} 掺进来了）");

        Assert.NotNull(profAt);
        var pt0 = learnProf.Trace()[0].T;
        var ptail = learnProf.Trace().Where(x => x.T - pt0 >= 58 * 60).Select(x => x.Tr).ToArray();
        Assert.True(ptail.Min() >= 40 - 0.05 && ptail.Max() <= 40 + 0.05, $"曲线那台 58 分钟起 Tr {ptail.Min():F3}…{ptail.Max():F3}（没稳住，学习没跑）");
        Assert.True(minRow40P >= b40 - 0.15, $"走曲线到 40：40 那行被写到 {minRow40P:F3}（30 ℃ 的偏置 {b30:F2} 掺进来了）");
    }

    private void AssertHigh(List<(double B, HeatBoostTwinTests.Rig R, EarlySwitch Sw, Func<double?> Fix)> his)
    {
        foreach (var (bNew, hi, sw, fix) in his)
        {
            Assert.True(fix() is not null, $"B {bNew}：没改");
            var fixT = fix()!.Value;
            var t = hi.Trace();
            var holdEnd = fixT + 600;
            var lateFix = sw.FixAt.Where(x => x > fixT + 1).ToArray();
            output.WriteLine($"   B {bNew}：拿住期到 {holdEnd / 60:F1} min；之后压低 {string.Join("、", sw.PushAtMin.Where(m => m * 60 > holdEnd).Select(m => m.ToString("F1")))}；" +
                             $"再改 {string.Join("、", lateFix.Select(x => (x / 60).ToString("F1")))}");
            var fixes = Fixes(hi);
            Assert.True(fixes.Length <= 2, $"B {bNew}：改了 {fixes.Length} 次");
            if (fixes.Length == 2) Assert.Contains("冲过目标被压低过", fixes[1]);       // 第二次是按小门槛那种
            var fixI = t.First(x => x.I < 4.5).I;
            var afterHold = t.Where(x => x.T >= holdEnd).ToArray();
            Assert.True(afterHold.Max(x => x.I) <= fixI + 0.05, $"B {bNew}：拿住期过了积分最高 {afterHold.Max(x => x.I):F3}、改的是 {fixI:F3}（压低拽下去那段攒进去了）");
            // 从改的那一刻数：拿住期里那一回（改完 9–10 分钟，正卡在拿住期满前后半分钟，从拿住期满数会忽 1 忽 2）加拿住期过了最多一回
            var postPushes = sw.PushAtMin.Where(m => m * 60 > fixT).ToArray();
            Assert.True(postPushes.Length <= 2, $"B {bNew}：改完压低 {postPushes.Length} 次（{string.Join("、", postPushes.Select(m => m.ToString("F1")))}，一压一冲来回摆）");
            Assert.DoesNotContain(sw.PushAtMin, m => m * 60 > holdEnd + 20 * 60);
            var mid = t.Where(x => x.T >= holdEnd + 20 * 60).Select(x => x.Tr).ToArray();
            Assert.True(mid.Max() <= 40 + 0.1, $"B {bNew}：拿住期过了 20 分钟起还冲到 {mid.Max():F3}");
            // 小改以后慢慢回升的那个谷正落在拿住期满 + 40 分钟前后（39.954…39.957）：从 +45 分钟起看
            var late = t.Where(x => x.T >= holdEnd + 45 * 60).Select(x => x.Tr).ToArray();
            Assert.True(late.Min() >= 40 - 0.05 && late.Max() <= 40 + 0.05, $"B {bNew}：拿住期过了 45 分钟起 Tr {late.Min():F3}…{late.Max():F3}");
        }
    }

    /// <summary>
    /// 附近目标按「实测 − 表」平移时两头从同一张表上取：加热棒挡、TEC 表只有 25 ℃ 一行（1.0）、加热棒表只有 50 ℃ 一行（5.5），
    /// 按离得近的选表在 37.5 换表。37 ℃ 自校正成 3.0 以后改 38.5：两头各查各的会平移成 5.5 +（3.0 − 1.0）= 7.5（表和表之间那个台阶加进来了）；
    /// 同一张表上取是 3.0。出了 ±2 K 照表。
    /// </summary>
    [Fact]
    public void 附近目标平移_两头同一张表()
    {
        var dev = new FakeRd105Device { Thermal = false };
        dev.Open();
        using var loop = new HostControlLoop(new Rd105Link(dev).Controller);
        loop.SetActuatorMode(1, ActuatorMode.HeatOnly);
        var g = new PidGains(9.4, 0.037, 175);
        var o = new PidGains(2.5, 0.01, 0);
        foreach (var tb in new[] { GainTable.Tec, GainTable.Heater }) loop.GetGainSchedule(1, tb).SegmentBoundaryC = double.NaN;
        loop.GetGainSchedule(1, GainTable.Tec).Learn(new GainPoint(25, g, 0, 0, o, 8, SteadyBiasC: 1.0));
        loop.GetGainSchedule(1, GainTable.Heater).Learn(new GainPoint(50, g, 0, 0, o, 8, SteadyBiasC: 5.5));
        var st = typeof(HostControlLoop).GetMethod("State", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(loop, new object[] { 1 })!;
        st.GetType().GetField("BiasOverrideC")!.SetValue(st, (double?)3.0);
        st.GetType().GetField("BiasOverrideSp")!.SetValue(st, 37.0);
        var steady = typeof(HostControlLoop).GetMethod("SteadyBiasFor", BindingFlags.NonPublic | BindingFlags.Instance)!;
        double? At(double sp) => (double?)steady.Invoke(loop, new[] { st, (object)sp });
        foreach (var sp in new[] { 37.0, 37.4, 37.6, 38.5, 39.0, 39.5 })
            output.WriteLine($"{sp:F1} ℃：{At(sp):F3}");
        foreach (var sp in new[] { 37.0, 37.4, 37.6, 38.5, 39.0 })
            Assert.Equal(3.0, At(sp)!.Value, 9);
        Assert.Equal(5.5, At(39.5)!.Value, 9);     // 出了 ±2 K：照表
    }
}
