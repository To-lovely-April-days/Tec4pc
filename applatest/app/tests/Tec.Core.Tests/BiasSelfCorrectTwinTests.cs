using TecControl.Core.Control;
using Xunit;
using Xunit.Abstractions;

namespace Tec.Core.Tests;

/// <summary>
/// 外环偏置自校正（<see cref="HostControlLoop.OuterBiasCheckWindow"/>）：2026-11-04 现场 R1-tc1-20261104-165419（双向挡釜内 32.8 → 40 ℃）
/// 表里 40 ℃ 的稳态偏置 5.53 K、这台釜实际只要 3.3 K，釜内过 40.1 就提前切 TEC 压低夹套、松开两枪又打回 5.53——每 5 分多钟冲一次 40.2–40.3、一直不稳。
/// 孪生：那场拟合的釜（τ1 981 s、b 3.32 K、壁滞后 116 s、夹套到壁纯滞后 18 s）+ 升温孪生的加热棒 / TEC 夹套，接真回路；
/// 会话的提前切换在测试里照 DuoSession 的判据模拟（釜内 ≥ 目标 + 0.1 且 30 s 斜率在升 → 夹套压到釜内 − 2；至少 60 s，
/// 回路要加热满 3 s 或超前回落才松开）。时间压缩 <see cref="HeatBoostTwinTests.K"/> 倍。
/// </summary>
public class BiasSelfCorrectTwinTests(ITestOutputHelper output)
{
    private const double K = HeatBoostTwinTests.K;

    /// <summary>照 DuoSession 双向挡的提前判据 + 制冷段（压低基准釜内、Δ 2 K、最短 60 s、超前 180 s、驻留 3 s）。只在回路线程上调。
    /// 【本地改动】照会话 2026-11-06 那两条：② 段结束（超前回落 / 功率翻号）后 <see cref="ReentryDwell"/> 内不再按提前判据进（现场松开 5 s 就被 0.01 K 量化出来的 +0.04 K/min 假斜率再压一回），
    /// Enter 用的斜率要跨满 30 s 的窗；功率符号说加热但 Enter（高过目标且在升）还成立时不退段（DuoSession.cs:1781 那条，原来孪生没照搬）。</summary>
    internal sealed class EarlySwitch
    {
        private readonly List<(double T, double E)> _w = new();
        private double? _since, _heatSince, _endedAt;
        /// <summary>② 压低再进间隔（现场秒；DualStation「压低再进间隔」缺省 60 s）。0 = 像原来那样段一结束就能再进。</summary>
        public double ReentryDwell = 60;
        public int Pushes;
        public readonly List<double> PushAtMin = new();
        /// <summary>每次自校正的时刻（现场秒，跟 Trace 的 T 同一个钟）。</summary>
        public readonly List<double> FixAt = new();
        private int _fixes;

        /// <summary>这一段不按提前判据走（测试里离开目标那段）：压着的先松开、斜率窗口清掉。</summary>
        public void Release(HeatBoostTwinTests.Rig r)
        {
            if (_since is not null) r.Loop.SetOuterPushDown(1, null);
            _since = _heatSince = _endedAt = null;
            _w.Clear();
        }

        /// <summary>暂停提前判据（离开目标那段；先 <see cref="Release"/>）。自校正时刻照记。</summary>
        public bool Paused;

        public void OnCycle(HeatBoostTwinTests.Rig r, ChannelCycleInfo c)
        {
            var t = r.P.SimSeconds;
            var nf = r.Notes().Count(n => n.Contains("外环偏置自校正"));
            if (nf > _fixes)
            {
                _fixes = nf;
                FixAt.Add(t);
            }
            if (Paused) return;
            var e = Math.Round(r.P.Tr, 2) - c.SetpointC;
            _w.Add((t, e));
            // 窗留 30 s、再多留一个 30 s 以外的点：Enter 要的斜率得跨满 30 s（原来 RemoveAll 之后跨度永远差一拍不到 30）
            while (_w.Count > 1 && t - _w[1].T > 30) _w.RemoveAt(0);
            var span = _w[^1].T - _w[0].T;
            var slope = double.NaN;
            if (_w.Count >= 3 && span >= 10)
            {
                double tm = _w.Average(x => x.T), em = _w.Average(x => x.E), sxx = 0, sxy = 0;
                foreach (var (tt, ee) in _w) { sxx += (tt - tm) * (tt - tm); sxy += (tt - tm) * (ee - em); }
                if (sxx > 0) slope = sxy / sxx;
            }
            var enterNow = !double.IsNaN(slope) && e >= 0.1 && slope >= 0.0005;
            if (_since is null)
            {
                // ② 段刚结束的 ReentryDwell 里不进（继电器照功率符号走，加热棒能出力）；Enter 的斜率窗不满 30 s 不进
                if (enterNow && span >= 30 && (_endedAt is null || t - _endedAt.Value >= ReentryDwell) && r.Loop.SetOuterPushDown(1, 2.0))
                {
                    _since = t;
                    _heatSince = null;
                    Pushes++;
                    PushAtMin.Add(t / 60);
                }
                return;
            }
            if (t - _since.Value < 60) return;
            var lead = !double.IsNaN(slope) && e + 180 * slope <= -0.1 && slope <= -0.0005;
            if (c.DutyPercent > 1) _heatSince ??= t; else _heatSince = null;
            // 功率说加热、Enter 还成立：不退（DuoSession.cs:1781）——釜内还高过目标且在升，TEC 留着
            if (lead || (!enterNow && _heatSince is { } hs && t - hs >= 3))
            {
                r.Loop.SetOuterPushDown(1, null);
                _since = null;
                _endedAt = t;
            }
        }
    }

    internal static HeatBoostTwinTests.Plant Run165419() => new(32.81, 33.98, 33.98, 33.98, tau1: 981.4, b: 3.32, tw: 116.4, wallDelay: 18);

    /// <summary>【本地改动】2026-11-06 现场 R1-tc1-20261106-130619（双向挡釜内 65.8 → 70 ℃）拟合的釜：τ1 1126 s、b 6.41 K、壁滞后 12 s、夹套到壁纯滞后 30 s。
    /// 原装夹套模型下表 7.82 时复现第一次爬过 70 + 压低，表 6.41 时干净（不压、冲过 0.03）。</summary>
    internal static HeatBoostTwinTests.Plant Run130619() => new(65.78, 67.37, 67.37, 67.37, tau1: 1126, b: 6.41, tw: 12, wallDelay: 30);

    /// <summary>同一台釜，从 39.6 ℃、夹套 / 釜壁 44 ℃ 起（6 分钟左右就过 40）：查清窗口那几条路用，省掉前面 30 分钟升温。</summary>
    internal static HeatBoostTwinTests.Plant NearTarget() => new(39.6, 44.0, 44.0, 44.0, tau1: 981.4, b: 3.32, tw: 116.4, wallDelay: 18);

    /// <summary>自校正的参数（按压缩时间）孪生 Rig 里本来就配好了；这里只管开关。</summary>
    internal static HeatBoostTwinTests.Rig Rig(double tableBias, bool selfCorrect, EarlySwitch sw,
        HeatBoostTwinTests.Plant? plant = null, Action<HeatBoostTwinTests.Rig, ChannelCycleInfo>? extra = null,
        Action<HostControlLoop>? tweak = null)
        => new(plant ?? Run165419(), HeatBoostLevel.Off, allowed: false, tweak: l =>
            {
                if (!selfCorrect) l.OuterBiasCheckWindow = TimeSpan.Zero;
                tweak?.Invoke(l);
            }, learnedBias: tableBias, rows: new[] { 40.0 }, onCycle: (r, c) =>
            {
                sw.OnCycle(r, c);
                extra?.Invoke(r, c);
            });

    internal static string[] Fixes(HeatBoostTwinTests.Rig r) => r.Notes().Where(n => n.Contains("外环偏置自校正")).ToArray();

    /// <summary>外环看到的误差（釜内按宇电 0.01 K 取整）变号的时刻（现场秒，从开控算）。</summary>
    internal static List<double> Crossings(HeatBoostTwinTests.Rig r, Func<double, double> spAt)
    {
        var t = r.Trace();
        var res = new List<double>();
        var prev = double.NaN;
        foreach (var x in t)
        {
            var at = x.T - t[0].T;
            var e = spAt(at) - Math.Round(x.Tr, 2);
            if (!double.IsNaN(prev) && Math.Abs(prev) > 1e-12 && prev * e <= 0) res.Add(at);
            prev = e;
        }
        return res;
    }

    /// <summary>第一次改积分的时刻（现场秒，从开控算）：积分离开表里那个 5.53 掉到 4.5 以下。</summary>
    internal static double FixAt(HeatBoostTwinTests.Rig r)
    {
        var t = r.Trace();
        var hit = t.FirstOrDefault(x => x.I < 4.5);
        return hit == default ? double.NaN : hit.T - t[0].T;
    }

    private void Report(string name, HeatBoostTwinTests.Rig r, EarlySwitch sw) => Report(output, name, r, sw);

    internal static void Report(ITestOutputHelper output, string name, HeatBoostTwinTests.Rig r, EarlySwitch sw, double sp = 40)
    {
        HeatBoostTwinTests.Dump(output, name, r, sp);
        output.WriteLine($"   压低 {sw.Pushes} 次：{string.Join("、", sw.PushAtMin.Select(m => m.ToString("F1")))} min");
        foreach (var n in r.Notes().Where(n => n.Contains("自校正")))
            output.WriteLine($"   · {n}");
    }

    internal static (double Lo, double Hi) Band(HeatBoostTwinTests.Rig r, double fromMin)
    {
        var t = r.Trace();
        var tail = t.Where(x => x.T - t[0].T >= fromMin * 60).Select(x => x.Tr).ToArray();
        return (tail.Min(), tail.Max());
    }

    [Fact]
    public async Task 现场那场_表里偏置5点53偏了_不校正来回压低_自校正后稳住_表对时不动()
    {
        var swOff = new EarlySwitch();
        var swOn = new EarlySwitch();
        var swRight = new EarlySwitch();
        await using var off = Rig(5.53, selfCorrect: false, swOff);
        await using var on = Rig(5.53, selfCorrect: true, swOn);
        await using var right = Rig(3.32, selfCorrect: true, swRight);
        await Task.WhenAll(off.Loop.StartChannelAsync(1, 40), on.Loop.StartChannelAsync(1, 40), right.Loop.StartChannelAsync(1, 40));
        await Task.WhenAll(off.UntilMin(70), on.UntilMin(70), right.UntilMin(70));
        Report("不校正（表 5.53）", off, swOff);
        Report("自校正（表 5.53）", on, swOn);
        Report("表对（3.32）、自校正开着", right, swRight);

        // 不校正：现场那样一直摆
        Assert.DoesNotContain(off.Notes(), n => n.Contains("自校正"));
        Assert.True(swOff.Pushes >= 4, $"压低 {swOff.Pushes} 次");
        var (offLo, offHi) = Band(off, 50);
        Assert.True(offHi - offLo >= 0.2, $"不校正 50 分钟起 Tr {offLo:F3}…{offHi:F3}");

        // 自校正：过零以后攒满一个窗口改一次（按釜壁估计，估得准），改完积分拿住——之后不再压低、稳住
        var fixes = Fixes(on);
        Assert.Single(fixes);
        Assert.Contains("表里这个温度的稳态偏置 +5.53 K 偏了", fixes[0]);
        Assert.Contains("PID 页「双向挡表」改一下", fixes[0]);
        var fixAt = FixAt(on);
        var crossAt = Crossings(on, _ => 40)[0];
        Assert.True(fixAt >= crossAt + 0.95 * 600, $"自校正 {fixAt / 60:F2} min 早于第一次过零 {crossAt / 60:F2} min + 一个窗口（过零锁存没起作用）");
        Assert.InRange(fixAt / 60, 25, 40);
        Assert.InRange(on.Trace().First(x => x.I < 4.5).I, 3.32 - 0.3, 3.32 + 0.3);
        Assert.DoesNotContain(swOn.PushAtMin, m => m > fixAt / 60);
        var t0 = on.Trace()[0].T;
        var post = on.Trace().Where(x => x.T - t0 > fixAt).Select(x => x.Tr).ToArray();
        Assert.True(post.Min() >= 40 - 0.32 && post.Max() <= 40.27, $"改完以后 Tr {post.Min():F3}…{post.Max():F3}");   // 改完那一下往下掉 0.28 一次，不再冲上去
        var (onLo, onHi) = Band(on, 45);
        Assert.True(onLo >= 39.93 && onHi <= 40.07, $"自校正 45 分钟起 Tr {onLo:F3}…{onHi:F3}");
        Assert.InRange(on.Trace()[^1].I, 3.32 - 0.3, 3.32 + 0.3);
        Assert.InRange(on.Loop.BothSteadyBias(1, 40) ?? double.NaN, 3.32 - 0.3, 3.32 + 0.3);    // 稳住以后学进表的是对的

        // 表本来就对：一次都不触发
        Assert.DoesNotContain(right.Notes(), n => n.Contains("自校正"));
        Assert.InRange(right.Overshoot(40), 0, 0.12);
    }

    /// <summary>
    /// 窗口 / 过零锁存 / 拿住积分那几条路：同一台釜从 39.6 ℃ 起、表 5.53（6 分钟左右过 40，不管的话照样来回压低）。
    /// · 基准：过零 + 一个窗口以后改一次，改完拿住的 10 分钟里积分一动不动；
    /// · Tr 丢失 20 s 又回来：窗口和锁存都清掉——回来以后重新等过零、再攒满一个窗口才改；
    /// · 走曲线（保温段停在 40）：一直不记、不改；
    /// · 改完以后改目标 40.3（表里那头也是错的 5.53）：按实测平移，不冲、不压低，也不再改（改目标清窗口和锁存另有一条：<see cref="BiasSelfCorrectRetargetTwinTests"/>）；
    /// · 改完以后釜内跑出 ±0.5（外环读数临时低 0.7 K 一分钟）：拿住放开（不等拿住期满），不再拿回来。积分这会儿还不动——
    ///   改之前那次压低松开立的保护（管不着的目标上也立、10 分钟）还拦着往上攒，读数低那一下正是不该攒的（让它攒：回头多冲 0.1、多压一回）。
    /// </summary>
    [Fact]
    public async Task 清窗口和拿住积分_Tr丢失_走曲线_改目标_跑出带子()
    {
        const double sp = 40;
        double? lostAt = null, backAt = null, spChangedAt = null, kickAt = null, kickEnd = null;
        bool lostBack = false, kickBack = false;
        DateTime? holdInKick = null, holdAfterKick = null;
        static DateTime HoldUntil(HeatBoostTwinTests.Rig r)
        {
            var st = BiasSelfCorrectRetargetTwinTests.StateOf(r.Loop);
            return (DateTime)st.GetType().GetField("BiasHoldUntil")!.GetValue(st)!;
        }     // 读数接回去只接一次、到点后第一拍就接（别卡在一个几秒的窗口里：回路慢一拍跨过去就再也接不回来）
        var swBase = new EarlySwitch();
        var swLost = new EarlySwitch();
        var swProf = new EarlySwitch();
        var swSp = new EarlySwitch();
        var swKick = new EarlySwitch();
        await using var baseRig = Rig(5.53, true, swBase, NearTarget());
        await using var lost = Rig(5.53, true, swLost, NearTarget(), (r, c) =>
        {
            var t = r.P.SimSeconds;
            if (lostAt is null && Math.Round(r.P.Tr, 2) > sp && t > 60)
            {
                lostAt = t + 180;                      // 过零 3 分钟以后（窗口攒到一半）断 20 s
            }
            else if (lostAt is { } la && backAt is null && t >= la)
            {
                r.Loop.SetOuterSource(1, () => double.NaN);
                backAt = la + 20;
            }
            else if (backAt is { } ba && !lostBack && t >= ba)
            {
                r.Loop.SetOuterSource(1, () => Math.Round(r.P.Tr, 2));
                lostBack = true;
            }
        });
        await using var prof = Rig(5.53, true, swProf, NearTarget());
        await using var spRig = Rig(5.53, true, swSp, NearTarget(), (r, c) =>
        {
            if (spChangedAt is null && c.OuterI < 4.5)
            {
                spChangedAt = r.P.SimSeconds;
                r.Loop.SetSetpoint(1, sp + 0.3);
            }
        });
        await using var kick = Rig(5.53, true, swKick, NearTarget(), (r, c) =>
        {
            var t = r.P.SimSeconds;
            if (kickAt is null && c.OuterI < 4.5)
            {
                kickAt = t + 60;
            }
            else if (kickAt is { } ka && kickEnd is null && t >= ka)
            {
                r.Loop.SetOuterSource(1, () => Math.Round(r.P.Tr, 2) - 0.7);
                kickEnd = ka + 60;
            }
            else if (kickEnd is { } ke0 && holdInKick is null && t >= ke0 - 20)
            {
                holdInKick = HoldUntil(r);                // 跑出带子 40 s 了：拿住应该已经放开
            }
            else if (kickEnd is { } ke && !kickBack && t >= ke)
            {
                r.Loop.SetOuterSource(1, () => Math.Round(r.P.Tr, 2));
                kickBack = true;
            }
            else if (kickBack && holdAfterKick is null && t >= kickEnd!.Value + 180)
            {
                holdAfterKick = HoldUntil(r);             // 读数接回去 3 分钟（回到带子里了）：不再拿回来
            }
        });
        await Task.WhenAll(baseRig.Loop.StartChannelAsync(1, sp), lost.Loop.StartChannelAsync(1, sp), prof.Loop.StartChannelAsync(1, sp),
            spRig.Loop.StartChannelAsync(1, sp), kick.Loop.StartChannelAsync(1, sp));
        prof.Loop.StartProfile(1, TecControl.Core.Control.SegmentProfile.Linear(sp, 0.1 * K, holdMinutes: 45 / K));
        await Task.WhenAll(baseRig.UntilMin(30), lost.UntilMin(42), prof.UntilMin(30), spRig.UntilMin(42), kick.UntilMin(30));
        Report("基准（39.6 起、表 5.53）", baseRig, swBase);
        Report("Tr 丢失 20 s", lost, swLost);
        Report("走曲线（保温 40）", prof, swProf);
        Report("改完改目标 40.3", spRig, swSp);
        Report("改完跑出 ±0.5 一分钟", kick, swKick);

        // 基准：过零 + 一个窗口以后改一次，拿住期间积分不动
        Assert.Single(Fixes(baseRig));
        var fixB = FixAt(baseRig);
        var crossB = Crossings(baseRig, _ => sp)[0];
        Assert.True(fixB >= crossB + 0.95 * 600, $"基准 {fixB / 60:F2} min，过零 {crossB / 60:F2} min");
        var tb0 = baseRig.Trace()[0].T;
        var held = baseRig.Trace().Where(x => x.T - tb0 >= fixB && x.T - tb0 <= fixB + 540).Select(x => x.I).ToArray();
        Assert.True(held.Max() - held.Min() < 1e-6, $"拿住期间积分 {held.Min():F4}…{held.Max():F4}");

        // Tr 丢失：回来以后重新等过零、再攒满一个窗口
        Assert.NotNull(backAt);
        var tl0 = lost.Trace()[0].T;
        var back = backAt!.Value - tl0;
        var fixL = FixAt(lost);
        Assert.False(double.IsNaN(fixL), "Tr 丢失那台一直没改");
        var crossAfter = Crossings(lost, _ => sp).First(x => x > back);
        Assert.True(fixL >= crossAfter + 0.95 * 600,
            $"Tr 丢失：{fixL / 60:F2} min 改的，回来 {back / 60:F2} min、回来后第一次过零 {crossAfter / 60:F2} min");

        // 走曲线：不改
        Assert.Empty(Fixes(prof));

        // 改目标：表里 40.3 也是错的 5.53，按实测的差平移（不再冲、不再压低、不再改）
        Assert.NotNull(spChangedAt);
        var ts0 = spRig.Trace()[0].T;
        var changed = spChangedAt!.Value - ts0;
        var spFixes = Fixes(spRig);
        Assert.Contains("+5.53 K 偏了", spFixes[0]);
        var afterChange = spRig.Trace().Where(x => x.T - ts0 > changed).ToArray();
        Assert.True(afterChange.Max(x => x.Tr) <= sp + 0.3 + 0.1, $"改到 40.3 以后 Tr 最高 {afterChange.Max(x => x.Tr):F3}");
        Assert.True(afterChange.Max(x => x.I) <= 3.32 + 0.5, $"改到 40.3 以后积分最高 {afterChange.Max(x => x.I):F2}（表里那个 5.53 又回来了）");
        Assert.DoesNotContain(swSp.PushAtMin, m => m * 60 - ts0 > changed);
        Assert.Single(spFixes);

        // 跑出带子：拿住放开（拿住期还没满就放开了）、不再拿回来；只改那一次
        Assert.Single(Fixes(kick));
        Assert.NotNull(kickEnd);
        var fixK = FixAt(kick);
        Assert.True(kickEnd!.Value - kick.Trace()[0].T < fixK + 540, $"跑出带子 {kickEnd.Value / 60:F2} min 已经过了拿住期");
        Assert.Equal(default(DateTime), holdInKick);
        Assert.Equal(default(DateTime), holdAfterKick);
    }

    /// <summary>
    /// 改完以后马上走曲线 / 重新开控：
    /// · 走曲线（40 → 41、0.05 K/min）：拿住立刻放开，积分跟着坡动；
    /// · 停了重开：这一场的实测值、拿住、窗口全清——积分起点按表里那个（这台还是 5.53），积分也不再拿着。
    /// </summary>
    [Fact]
    public async Task 改完走曲线放开拿住_停了重开全清()
    {
        const double sp = 40;
        double? profAt = null;
        var swProf = new EarlySwitch();
        var swRe = new EarlySwitch();
        await using var prof = Rig(5.53, true, swProf, NearTarget(), (r, c) =>
        {
            if (profAt is null && c.OuterI < 4.5)
            {
                profAt = r.P.SimSeconds;
                r.Loop.StartProfile(1, TecControl.Core.Control.SegmentProfile.Linear(sp + 1, 0.05 * K));
            }
        });
        await using var re = Rig(5.53, true, swRe, NearTarget());
        await Task.WhenAll(prof.Loop.StartChannelAsync(1, sp), re.Loop.StartChannelAsync(1, sp));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(40 * 60 / K * 3 + 60);
        while (double.IsNaN(FixAt(re)))
        {
            Assert.True(DateTime.UtcNow < deadline, "重开那台一直没改");
            await Task.Delay(20);
        }
        var st = BiasSelfCorrectRetargetTwinTests.StateOf(re.Loop);
        var carryField = st.GetType().GetField("BiasCarryTables")!;
        var carryBefore = carryField.GetValue(st) is not null;
        await re.Loop.StopChannelAsync(1);
        var restartAt = re.P.SimSeconds;
        await re.Loop.StartChannelAsync(1, sp);
        var carryAfterRestart = carryField.GetValue(st) is not null;
        await Task.WhenAll(prof.UntilMin(26), re.UntilMin(28));
        Report("改完走曲线 40 → 41", prof, swProf);
        Report("改完停了重开", re, swRe);

        // 走曲线：拿住放开，积分在坡起头这几分钟里就动
        Assert.NotNull(profAt);
        var moving = prof.Trace().Where(x => x.T >= profAt!.Value + 30 && x.T <= profAt.Value + 240).Select(x => x.I).ToArray();
        Assert.True(moving.Max() - moving.Min() > 0.02, $"走曲线以后积分 {moving.Min():F4}…{moving.Max():F4}（拿住没放开）");

        // 重开：积分起点回到表里那个、积分不再拿着
        var start = re.Notes().Last(n => n.Contains("串级外环积分起点"));
        Assert.Contains("+5.53", start);
        Assert.True(carryBefore, "改完以后没拍表副本（这条用例本身没查到东西）");
        Assert.False(carryAfterRestart, "重开以后平移用的表副本没清（下一次改该按那时候的表重拍）");
        var after = re.Trace().Where(x => x.T >= restartAt + 30 && x.T <= restartAt + 180).Select(x => x.I).ToArray();
        Assert.True(after.Max() - after.Min() > 0.02, $"重开以后积分 {after.Min():F4}…{after.Max():F4}（拿住没清）");
    }
}
