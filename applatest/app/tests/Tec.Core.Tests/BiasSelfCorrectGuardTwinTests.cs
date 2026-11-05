using System.Reflection;
using TecControl.Core.Control;
using Tec.Drivers.Rd105;
using Xunit;
using Xunit.Abstractions;
using static Tec.Core.Tests.BiasSelfCorrectTwinTests;

namespace Tec.Core.Tests;

/// <summary>
/// 外环偏置自校正改完以后（第三轮复查找出来的几条）：同一台 165419 釜（表里 40 ℃ 5.53、实际 3.32）从 39.6 ℃ 起，
/// 16 分半左右改一次（≈ 3.24），拿住到 26 分半。各台互不相干、一起跑。
/// </summary>
public class BiasSelfCorrectGuardTwinTests(ITestOutputHelper output)
{
    private const double K = HeatBoostTwinTests.K;
    private static object F(object st, string name) => st.GetType().GetField(name)!.GetValue(st)!;

    /// <summary>
    /// 管着的目标上、拿住期过了，解除压低重新武装的两枪不打——压低把釜内拽到目标下面那一截不能攒进积分：
    /// · 稳住以后硬压一次 90 s：攒进去的话积分 3.25 → 3.84 → 4.18，冲过 40.1 又压两次、再改一次；
    /// · 釜的偏置临时变小 6 分钟（3.32 → 2.6，少散热）：釜内往上走、压一次，回来以后同上不能一攒一压。
    /// </summary>
    [Fact]
    public async Task 管着的目标上压低一次_积分不往上攒_扰动以后不来回压()
    {
        // P1：改完 25 分钟（拿住早过了、稳在 40）硬压 90 s
        double? fix1 = null, push1 = null;
        var rel1 = false;
        var sw1 = new EarlySwitch();
        await using var p1 = Rig(5.53, true, sw1, NearTarget(), (r, c) =>
        {
            var t = r.P.SimSeconds;
            if (fix1 is null && c.OuterI < 4.5) fix1 = t;
            else if (fix1 is { } f && push1 is null && t >= f + 25 * 60)
            {
                if (r.Loop.SetOuterPushDown(1, 2.0)) push1 = t;
            }
            else if (push1 is { } p && !rel1 && t >= p + 90)
            {
                r.Loop.SetOuterPushDown(1, null);
                rel1 = true;
            }
        });

        // P3：改完 25 分钟起釜的偏置 3.32 → 2.6 六分钟再回来
        double? fix3 = null, d3 = null;
        var back3 = false;
        var sw3 = new EarlySwitch();
        await using var p3 = Rig(5.53, true, sw3, NearTarget(), (r, c) =>
        {
            var t = r.P.SimSeconds;
            if (fix3 is null && c.OuterI < 4.5) fix3 = t;
            else if (fix3 is { } f && d3 is null && t >= f + 25 * 60)
            {
                d3 = t;
                r.P.B = 2.6;
            }
            else if (d3 is { } d && !back3 && t >= d + 6 * 60)
            {
                r.P.B = 3.32;
                back3 = true;
            }
        });

        // P2：改完 7 分钟（还在拿住期里）硬压 90 s——松开时拿住期没过，拿住期满正赶上釜内被压在目标下面
        double? fix2 = null, push2 = null, rel2 = null;
        DateTime? hold2 = null, guard2 = null;
        var sw2 = new EarlySwitch();
        await using var p2 = Rig(5.53, true, sw2, NearTarget(), (r, c) =>
        {
            var t = r.P.SimSeconds;
            if (fix2 is null && c.OuterI < 4.5) fix2 = t;
            else if (fix2 is { } f && push2 is null && t >= f + 7 * 60)
            {
                if (r.Loop.SetOuterPushDown(1, 2.0)) push2 = t;
            }
            else if (push2 is { } p && rel2 is null && t >= p + 90)
            {
                r.Loop.SetOuterPushDown(1, null);
                rel2 = t;
                hold2 = (DateTime)F(BiasSelfCorrectRetargetTwinTests.StateOf(r.Loop), "BiasHoldUntil");
            }
            else if (rel2 is not null && guard2 is null)
            {
                // 回路线程收走解除压低那一拍立的保护：到期 = 拿住期满 + 一个拿住期（不是从松开那一刻起算）
                var st = BiasSelfCorrectRetargetTwinTests.StateOf(r.Loop);
                if ((int)F(st, "OuterRearmInPlaceFlag") == 0) guard2 = (DateTime)F(st, "ReleaseGuardUntil");
            }
        });

        await Task.WhenAll(p1.Loop.StartChannelAsync(1, 40), p2.Loop.StartChannelAsync(1, 40), p3.Loop.StartChannelAsync(1, 40));
        await Task.WhenAll(p1.UntilMin(80), p2.UntilMin(70), p3.UntilMin(85));
        Report(output, "改完 25 分钟硬压一次 90 s", p1, sw1);
        Report(output, "改完 7 分钟（拿住期里）硬压一次 90 s", p2, sw2);
        Report(output, "改完 25 分钟起偏置 2.6 六分钟", p3, sw3);

        // P2：松开在拿住期里，保护从拿住期满起算——不然拿住期满两枪不打、积分没人拦，攒到 3.65、冲 0.13、又压两回
        Assert.NotNull(rel2);
        Assert.True(rel2 < fix2!.Value + 600, $"松开 {(rel2 - fix2) / 60:F1} 分钟时拿住期已经过了（这条用例没查到拿住期里松开）");
        Assert.Single(Fixes(p2));
        // 这台 Tr 松开后 9–10 分钟就回到目标、保护先清掉了，从松开起算也拦得住——下面的曲线分不出两种算法，到期时刻直接对
        Assert.True(hold2 is { } h2 && h2 != default && guard2 == h2 + p2.Loop.OuterBiasCheckHold,
            $"拿住期里松开的保护到期 {guard2:HH:mm:ss.fff}，应为拿住期满 {hold2:HH:mm:ss.fff} + {p2.Loop.OuterBiasCheckHold.TotalSeconds:F1} s");
        var after2 = p2.Trace().Where(x => x.T >= rel2!.Value).ToArray();
        Assert.DoesNotContain(sw2.PushAtMin, m => m * 60 > rel2!.Value);
        Assert.True(after2.Max(x => x.I) <= 3.32 + 0.3, $"拿住期里压低、松开以后积分最高 {after2.Max(x => x.I):F2}");
        Assert.True(after2.Max(x => x.Tr) <= 40 + 0.1, $"拿住期里压低、松开以后冲到 {after2.Max(x => x.Tr):F3}");
        var tail2 = after2.Where(x => x.T >= rel2!.Value + 30 * 60).Select(x => x.Tr).ToArray();
        Assert.True(tail2.Min() >= 40 - 0.05 && tail2.Max() <= 40 + 0.05, $"拿住期里压低 30 分钟以后 Tr {tail2.Min():F3}…{tail2.Max():F3}");

        // P1：之后不再压低、积分不往上跑、不冲、不再改
        Assert.NotNull(push1);
        Assert.True(rel1, "硬压没松开");
        Assert.Single(Fixes(p1));
        var after1 = p1.Trace().Where(x => x.T >= push1!.Value).ToArray();
        Assert.DoesNotContain(sw1.PushAtMin, m => m * 60 > push1!.Value);
        Assert.True(after1.Max(x => x.I) <= 3.32 + 0.3, $"硬压以后积分最高 {after1.Max(x => x.I):F2}（压低拽下去那段攒进去了）");
        Assert.True(after1.Max(x => x.Tr) <= 40 + 0.1, $"硬压以后冲到 {after1.Max(x => x.Tr):F3}");
        var tail1 = after1.Where(x => x.T >= push1!.Value + 30 * 60).Select(x => x.Tr).ToArray();
        Assert.True(tail1.Min() >= 40 - 0.05 && tail1.Max() <= 40 + 0.05, $"硬压 30 分钟以后 Tr {tail1.Min():F3}…{tail1.Max():F3}");

        // P3：扰动那一下最多压一次；回来以后不再压、积分不往上跑、不再改
        Assert.NotNull(d3);
        Assert.True(back3, "扰动没撤");
        Assert.Single(Fixes(p3));
        var pushes3 = sw3.PushAtMin.Where(m => m * 60 > d3!.Value).ToArray();
        Assert.True(pushes3.Length <= 1, $"扰动以后压低 {pushes3.Length} 次：{string.Join("、", pushes3.Select(m => m.ToString("F1")))} min");
        var after3 = p3.Trace().Where(x => x.T >= d3!.Value + 6 * 60).ToArray();
        Assert.True(after3.Max(x => x.I) <= 3.32 + 0.4, $"扰动撤了以后积分最高 {after3.Max(x => x.I):F2}");
        var tail3 = after3.Where(x => x.T >= d3!.Value + 35 * 60).Select(x => x.Tr).ToArray();
        Assert.True(tail3.Min() >= 40 - 0.05 && tail3.Max() <= 40 + 0.05, $"扰动 35 分钟以后 Tr {tail3.Min():F3}…{tail3.Max():F3}");
    }

    /// <summary>
    /// 平移不许跑出「实测 ~ 现在表里」那一段：表里 40 那行错（5.53）、43 那行对（3.32）。40 改完 2 分钟去 43（管不着、学进表），
    /// 待 40 分钟回 42（离 40 两度、还管着）：整段平移是 42 插出来的 4.06 再减 2.29 = 1.77（真值 3.32，差算了两遍）——按它打两枪下冲 0.45；
    /// 夹在 [实测 3.24, 表 4.06 + 0.3] 里是 3.24（往上不比实测小；两边都留的话 2.94、掉 0.16）。
    /// </summary>
    [Fact]
    public async Task 平移不出实测和表之间_别处学对了不算两遍()
    {
        double? fix = null, away = null, back = null, steadyAtBack = null, overrideAtBack = null;
        var steady = typeof(HostControlLoop).GetMethod("SteadyBiasFor", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var sw = new EarlySwitch();
        await using var r42 = Rig(5.53, true, sw, NearTarget(), (r, c) =>
        {
            var t = r.P.SimSeconds;
            if (fix is null && c.OuterI < 4.5) fix = t;
            else if (fix is { } f && away is null && t >= f + 120)
            {
                away = t;
                sw.Release(r);
                sw.Paused = true;
                r.Loop.SetSetpoint(1, 43);
            }
            else if (away is { } aw && back is null && t >= aw + 40 * 60)
            {
                back = t;
                sw.Release(r);                 // 会话改目标那一下清斜率窗口（DuoSession.NoteStart）
                sw.Paused = false;
                var st = BiasSelfCorrectRetargetTwinTests.StateOf(r.Loop);
                steadyAtBack = (double?)steady.Invoke(r.Loop, new[] { st, (object)42.0 });
                overrideAtBack = (double?)st.GetType().GetField("BiasOverrideC")!.GetValue(st);
                r.Loop.SetSetpoint(1, 42);
            }
        }, tweak: l => l.GetGainSchedule(1, GainTable.Both).Learn(new GainPoint(43, new PidGains(9.4, 0.037 * K, 175 / K), 0, 0,
            new PidGains(2.5, 0.01 * K, 0), 8, SteadyBiasC: 3.32, HeatRatio: 3.5)));

        await r42.Loop.StartChannelAsync(1, 40);
        await r42.UntilMin(105);
        Report(output, "改完 40 → 43（40 min）→ 42", r42, sw, 42);

        Assert.NotNull(back);
        Assert.Single(Fixes(r42));
        var after = r42.Trace().Where(x => x.T >= back!.Value).ToArray();
        output.WriteLine($"   回 42 那一刻：42 用的偏置 {steadyAtBack:F3}、实测 {overrideAtBack:F3}");
        // 往上的目标不比实测小（两边都留余量的话 2.94）：两枪按它打
        Assert.True(steadyAtBack >= overrideAtBack - 1e-9, $"回 42 用的偏置 {steadyAtBack:F3} 比实测 {overrideAtBack:F3} 还小");
        // 掉多少主要看 43 → 42 往下走开头那一下积分往外跑了多少（降 1 K 的通病，表对也一样）、趋近那枪赶没赶上 0.25 K 的死区：41.87–41.91
        Assert.True(after.Min(x => x.Tr) >= 42 - 0.15, $"回 42 以后掉到 {after.Min(x => x.Tr):F3}（平移把差算了两遍）");
        Assert.True(after.Min(x => x.I) >= 3.32 - 0.6, $"回 42 以后积分最低 {after.Min(x => x.I):F2}");
        Assert.DoesNotContain(sw.PushAtMin, m => m * 60 >= back!.Value);
        // 掉下去那一下积分在下面攒了点，回来以后慢慢冲过 0.06 一次（不压低）、30 分钟起稳住
        Assert.True(after[^1].T - (back!.Value + 30 * 60) >= 10 * 60, $"回 42 后 30 分钟起只看了 {(after[^1].T - back!.Value) / 60 - 30:F1} 分钟");
        var tail = after.Where(x => x.T >= back!.Value + 30 * 60).Select(x => x.Tr).ToArray();
        Assert.True(tail.Min() >= 42 - 0.05 && tail.Max() <= 42 + 0.05, $"回 42 后 30 分钟起 Tr {tail.Min():F3}…{tail.Max():F3}");
    }
}

/// <summary>外环偏置自校正：按小门槛再改一次只认「这一串」冲过头被压低——早先那一次压低不能一直立着。</summary>
public class BiasSelfCorrectRefixAnchorTwinTests(ITestOutputHelper output)
{
    private static object F(object st, string name) => st.GetType().GetField(name)!.GetValue(st)!;

    /// <summary>
    /// A：改完 25 分钟硬压 90 s（之后没再改——积分对的），改完 50 分钟起釜的偏置 3.32 → 2.6 六分钟（压一次）。
    /// 起算点一直停在早先那次解除的话，扰动那次一松开、整个窗口就带着扰动和压低那一截（估 2.88），按小门槛错改、多沉 13 分钟（孪生：95.5 分钟才稳）；
    /// 隔了两个窗口以上的解除重新起算：不改，82 分钟左右稳住。
    /// B：起算点立上以后走曲线（不立改目标那个标志）：起算点清掉——到了别的温度，没冲过头也按小门槛改不行。
    /// </summary>
    [Fact]
    public async Task 早先压过一次_很久以后扰动压低_不按小门槛错改()
    {
        double? fixA = null, pushA = null, distA = null;
        bool relA = false, backA = false;
        DateTime anchorA = default;
        var swA = new EarlySwitch();
        await using var a = Rig(5.53, true, swA, NearTarget(), (r, c) =>
        {
            var t = r.P.SimSeconds;
            if (fixA is null && c.OuterI < 4.5) fixA = t;
            else if (fixA is { } f && pushA is null && t >= f + 25 * 60)
            {
                if (r.Loop.SetOuterPushDown(1, 2.0)) pushA = t;
            }
            else if (pushA is { } p && !relA && t >= p + 90)
            {
                r.Loop.SetOuterPushDown(1, null);
                relA = true;
            }
            else if (relA && anchorA == default)
            {
                var st = BiasSelfCorrectRetargetTwinTests.StateOf(r.Loop);
                if ((int)F(st, "OuterRearmInPlaceFlag") == 0) anchorA = (DateTime)F(st, "RefixFrom");
            }
            if (fixA is { } f2 && distA is null && t >= f2 + 50 * 60)
            {
                distA = t;
                r.P.B = 2.6;
            }
            else if (distA is { } d && !backA && t >= d + 6 * 60)
            {
                r.P.B = 3.32;
                backA = true;
            }
        });

        double? fixB = null, pushB = null, profB = null;
        var relB = false;
        DateTime anchorB = default;
        var probesB = new List<DateTime>();
        var swB = new EarlySwitch();
        await using var b = Rig(5.53, true, swB, NearTarget(), (r, c) =>
        {
            var t = r.P.SimSeconds;
            var st = BiasSelfCorrectRetargetTwinTests.StateOf(r.Loop);
            if (fixB is null && c.OuterI < 4.5) fixB = t;
            else if (fixB is { } f && pushB is null && t >= f + 25 * 60)
            {
                if (r.Loop.SetOuterPushDown(1, 2.0)) pushB = t;
            }
            else if (pushB is { } p && !relB && t >= p + 90)
            {
                r.Loop.SetOuterPushDown(1, null);
                relB = true;
            }
            else if (relB && anchorB == default)
            {
                if ((int)F(st, "OuterRearmInPlaceFlag") == 0) anchorB = (DateTime)F(st, "RefixFrom");
            }
            else if (anchorB != default && profB is null)
            {
                profB = t;
                swB.Paused = true;
                r.Loop.StartProfile(1, SegmentProfile.Linear(40.5, 0, holdMinutes: 2.0 / HeatBoostTwinTests.K));
            }
            else if (profB is not null && probesB.Count < 5)
                probesB.Add((DateTime)F(st, "RefixFrom"));
        });

        await Task.WhenAll(a.Loop.StartChannelAsync(1, 40), b.Loop.StartChannelAsync(1, 40));
        await Task.WhenAll(a.UntilMin(110), b.UntilMin(50));
        Report(output, "改完 25 分钟硬压一次、50 分钟起偏置 2.6 六分钟", a, swA);
        output.WriteLine($"   硬压解除以后起算点 {anchorA:HH:mm:ss}；±0.05 里待住从 {a.StayInMin(40, 0.05):F1} min");

        Assert.NotNull(pushA);
        Assert.True(relA && backA, "硬压没松开 / 扰动没撤");
        Assert.True(anchorA != default, "硬压解除以后起算点没立（这条用例没查到早先立着的那种）");
        Assert.Contains(swA.PushAtMin, m => m * 60 > distA!.Value);       // 扰动真压了一次
        var fixesA = Fixes(a);
        Assert.Single(fixesA);
        Assert.DoesNotContain(fixesA, n => n.Contains("冲过目标被压低过"));
        Assert.True(a.StayInMin(40, 0.05) <= 90, $"扰动以后 {a.StayInMin(40, 0.05):F1} min 才稳在 ±0.05（按小门槛错改、多沉）");

        Assert.True(anchorB != default, "B：硬压解除以后起算点没立");
        Assert.NotNull(profB);
        Assert.NotEmpty(probesB);
        Assert.All(probesB, x => Assert.Equal(default, x));
    }
}

/// <summary>外环偏置自校正：会话下新目标时先松压低、紧跟着改目标（同一拍到）——算改目标，不算解除压低。</summary>
public class BiasSelfCorrectRaceTwinTests(ITestOutputHelper output)
{
    private static object F(object st, string name) => st.GetType().GetField(name)!.GetValue(st)!;

    /// <summary>
    /// DuoSession 下新目标：EndEpisode → 松压低（立「解除压低」）→ 紧跟着 SetSetpoint（立「改目标」），回路线程同一拍收到两个。
    /// 解除压低那个标志只在没改目标那支里收的话，留到下一拍就被认成解除压低：改目标的两枪不打、积分保护也立上了（孪生：压低前积分被扰动攒到 4.1 的，
    /// 冲 0.24、又压五回）。改完 20 分钟硬压，30 s 后松开 + 改 41.5（管着）：之后十几拍都还是「改目标」那种。
    /// </summary>
    [Fact]
    public async Task 松压低和改目标同一拍到_算改目标()
    {
        double? fix = null, push = null, step = null;
        var consumed = false;
        var probes = new List<(bool Shots, bool Guard)>();
        var sw = new EarlySwitch();
        await using var r = Rig(5.53, true, sw, NearTarget(), (r, c) =>
        {
            var t = r.P.SimSeconds;
            if (fix is null && Fixes(r).Length > 0) { fix = t; return; }
            if (fix is not { } f) return;
            if (push is null && t >= f + 20 * 60) { if (r.Loop.SetOuterPushDown(1, 2.0)) push = t; return; }
            if (push is { } p && step is null && t >= p + 30)
            {
                step = t;
                sw.Release(r);
                sw.Paused = true;
                r.Loop.SetOuterPushDown(1, null);
                r.Loop.SetSetpoint(1, 41.5);
                return;
            }
            if (step is null || probes.Count >= 12) return;
            var st = BiasSelfCorrectRetargetTwinTests.StateOf(r.Loop);
            if (!consumed) { consumed = (int)F(st, "OuterTargetChangedFlag") == 0; if (!consumed) return; }
            probes.Add(((bool)F(st, "ShotsFromPushRelease"), (DateTime)F(st, "ReleaseGuardUntil") != default));
        });
        await r.Loop.StartChannelAsync(1, 40);
        await r.UntilMin(45);
        Report(output, "改完 20 分钟压低、30 s 后松开 + 改 41.5", r, sw, 41.5);
        Assert.NotNull(push);
        Assert.Equal(12, probes.Count);
        Assert.Single(Fixes(r));
        Assert.All(probes, x => Assert.False(x.Shots, "改目标被认成解除压低（两枪不打）"));
        Assert.All(probes, x => Assert.False(x.Guard, "改目标立了解除压低的积分保护"));
    }
}

/// <summary>外环偏置自校正：在几个温度之间来回（这一场的实测点）。</summary>
public class BiasSelfCorrectCycleTwinTests(ITestOutputHelper output)
{
    private const double K = HeatBoostTwinTests.K;

    private sealed class Leg
    {
        public double Sp, From, StartT;
        public double MaxTr = double.NegativeInfinity, MinTr = double.PositiveInfinity;
        public int Pushes;
        public double TailLo = double.PositiveInfinity, TailHi = double.NegativeInfinity;
        /// <summary>这一段开头（改目标前一拍）新目标按什么偏置（SteadyBiasFor）、这一场实测过哪些目标、表里 40 那行。</summary>
        public double SteadyAtStart = double.NaN, Row40AtStart = double.NaN;
        public double[] MeasuredSpsAtStart = [];
    }

    /// <summary>
    /// 表里 40 那行对（3.32）、43 那行错（5.53，比 40 那行先存），釜偏置处处 3.32。从 41.6 起控 42：表插出来 4.79，冲过、压低、在 42 改一次；
    /// 之后 40 → 42 → 40 → 42 每段 40 分钟。表副本只在改那一刻对，40 学完以后从 40 平移回 42 是 4.82、夹到 3.67——每回升到 42 都冲 0.15、压低两三次；
    /// 这一场在 42、40 都实测过：回到实测过的目标直接按实测。升到 42 那几段不冲过 0.12、最多压一次、最后 15 分钟 ±0.08；降到 40 那几段不掉过 0.12。
    /// 第二回降到 40 用的就是第一回在 40 实测到的（差不过 0.1）——曲线分不出来：40 只从上面来，积分到 1 K 那儿离实测、离平移的都不到放电门槛，
    /// 两枪都不打；不记实测点会按平移夹出来的 3.02 用（差 0.3），斜一点的釜两枪就会打偏。
    /// </summary>
    [Fact]
    public async Task 两个温度来回_回到实测过的目标按实测()
    {
        var seq = new[] { 40.0, 42.0, 40.0, 42.0 };
        const double holdMin = 40;
        double? fix = null;
        var legs = new List<Leg>();
        var lastPushes = 0;
        var sw = new EarlySwitch();
        var steady = typeof(HostControlLoop).GetMethod("SteadyBiasFor", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var plant = new HeatBoostTwinTests.Plant(41.6, 46.0, 46.0, 46.0, tau1: 981.4, b: 3.32, tw: 116.4, wallDelay: 18);
        await using var r = new HeatBoostTwinTests.Rig(plant, HeatBoostLevel.Off, allowed: false, tweak: l =>
        {
            var both = l.GetGainSchedule(1, GainTable.Both);
            both.Clear();
            var g = new PidGains(9.4, 0.037 * K, 175 / K);
            var o = new PidGains(2.5, 0.01 * K, 0);
            both.Learn(new GainPoint(43, g, 0, 0, o, 8, SteadyBiasC: 5.53, HeatRatio: 3.5));
            both.Learn(new GainPoint(40, g, 0, 0, o, 8, SteadyBiasC: 3.32, HeatRatio: 3.5));
        }, learnedBias: 3.32, rows: new[] { 40.0 }, onCycle: (r, c) =>
        {
            sw.OnCycle(r, c);
            var t = r.P.SimSeconds;
            if (fix is null && sw.FixAt.Count > 0) fix = sw.FixAt[0];
            if (legs.Count > 0)
            {
                var leg = legs[^1];
                leg.MaxTr = Math.Max(leg.MaxTr, r.P.Tr);
                leg.MinTr = Math.Min(leg.MinTr, r.P.Tr);
                leg.Pushes += sw.Pushes - lastPushes;
                if (t - leg.StartT >= (holdMin - 15) * 60)
                {
                    leg.TailLo = Math.Min(leg.TailLo, r.P.Tr);
                    leg.TailHi = Math.Max(leg.TailHi, r.P.Tr);
                }
            }
            lastPushes = sw.Pushes;
            if (fix is { } f && legs.Count < seq.Length && t >= f + (legs.Count + 1) * holdMin * 60)
            {
                sw.Release(r);
                var sp = seq[legs.Count];
                var st = BiasSelfCorrectRetargetTwinTests.StateOf(r.Loop);
                var measured = ((double Sp, double Bias)[])st.GetType().GetField("BiasMeasured")!.GetValue(st)!;
                legs.Add(new Leg
                {
                    Sp = sp, From = c.SetpointC, StartT = t,
                    SteadyAtStart = (double?)steady.Invoke(r.Loop, [st, sp]) ?? double.NaN,
                    Row40AtStart = r.Loop.BothSteadyBias(1, 40) ?? double.NaN,
                    MeasuredSpsAtStart = measured.Select(m => m.Sp).ToArray(),
                });
                r.Loop.SetSetpoint(1, sp);
            }
        });
        await r.Loop.StartChannelAsync(1, 42);
        while (fix is null && r.Trace() is var tr0 && (tr0.Length == 0 || tr0[^1].T - tr0[0].T < 60 * 60))
            await r.UntilMin((r.Trace() is var x && x.Length > 0 ? (x[^1].T - x[0].T) / 60 : 0) + 5);
        Assert.NotNull(fix);
        await r.UntilMin((fix!.Value - r.Trace()[0].T) / 60 + (seq.Length + 1) * holdMin + 1);
        Report(output, "42 改完 → 40 → 42 → 40 → 42", r, sw, 42);
        foreach (var leg in legs)
            output.WriteLine($"   {leg.From:F0} → {leg.Sp:F0}（{leg.StartT / 60:F1} min）：起点按 {leg.SteadyAtStart:F3}（实测过 {string.Join("、", leg.MeasuredSpsAtStart.Select(x => x.ToString("F0")))}，" +
                             $"表 40 行 {leg.Row40AtStart:F3}）；Tr {leg.MinTr:F3}…{leg.MaxTr:F3}，压低 {leg.Pushes} 次，最后 15 分钟 {leg.TailLo:F3}…{leg.TailHi:F3}");

        Assert.Single(Fixes(r));
        Assert.Equal(seq.Length, legs.Count);
        // 第二回降到 40：第一回在 40 真稳住学到的记成了实测点，按它（不是从 42 平移过来夹出的那个）
        var back40 = legs[2];
        Assert.Contains(40.0, back40.MeasuredSpsAtStart);
        Assert.True(Math.Abs(back40.SteadyAtStart - back40.Row40AtStart) <= 0.1,
            $"第二回降到 40 按 {back40.SteadyAtStart:F3}，这一场在 40 学到的 {back40.Row40AtStart:F3}");
        foreach (var leg in legs.Where(l => l.Sp > l.From))
        {
            Assert.True(leg.MaxTr - leg.Sp <= 0.12, $"{leg.StartT / 60:F1} min 升到 {leg.Sp}：冲 {leg.MaxTr - leg.Sp:F3}");
            Assert.True(leg.Pushes <= 1, $"{leg.StartT / 60:F1} min 升到 {leg.Sp}：压低 {leg.Pushes} 次");
            Assert.True(leg.TailLo >= leg.Sp - 0.08 && leg.TailHi <= leg.Sp + 0.08, $"{leg.StartT / 60:F1} min 升到 {leg.Sp}：最后 15 分钟 {leg.TailLo:F3}…{leg.TailHi:F3}");
        }
        foreach (var leg in legs.Where(l => l.Sp < l.From))
            Assert.True(leg.Sp - leg.MinTr <= 0.12, $"{leg.StartT / 60:F1} min 降到 {leg.Sp}：掉 {leg.Sp - leg.MinTr:F3}");
    }
}

/// <summary>外环偏置自校正的几处查表（直接调，不跑回路）。</summary>
public class BiasSelfCorrectTableTests(ITestOutputHelper output)
{
    private static readonly PidGains G = new(9.4, 0.037, 175);
    private static readonly PidGains O = new(2.5, 0.01, 0);

    private static (HostControlLoop Loop, object St, Func<double, double?> At) Open(ActuatorMode mode, Action<HostControlLoop> setup)
    {
        var dev = new FakeRd105Device { Thermal = false };
        dev.Open();
        var loop = new HostControlLoop(new Rd105Link(dev).Controller);
        loop.SetActuatorMode(1, mode);
        foreach (var tb in new[] { GainTable.Tec, GainTable.Heater, GainTable.Both }) loop.GetGainSchedule(1, tb).SegmentBoundaryC = double.NaN;
        setup(loop);
        var st = BiasSelfCorrectRetargetTwinTests.StateOf(loop);
        var steady = typeof(HostControlLoop).GetMethod("SteadyBiasFor", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (loop, st, sp => (double?)steady.Invoke(loop, new[] { st, (object)sp }));
    }

    private static void Override(object st, double sp, double value)
    {
        st.GetType().GetField("BiasOverrideC")!.SetValue(st, (double?)value);
        st.GetType().GetField("BiasOverrideSp")!.SetValue(st, sp);
    }

    /// <summary>
    /// 双向挡表 40 错（5.53）、43 对（3.32），40 改成 3.24：42 整段平移是 1.77，夹在 [实测, 表 4.06 + 0.3] 里 → 3.24
    /// （往上的目标偏置不会比实测还小；两边都留余量的话夹到 2.94、回 42 掉 0.16）；41 一样 3.24。
    /// 往下：37 那行 4.6、40 那行 5.53，40 改成 3.24：39 平移 2.93，夹在 [实测 − 0.15, 实测] → 3.09；37 那行比 40 还大（6.0，表错得反了）：夹到实测 3.24。
    /// 表全错一样（43 也是 5.53）：平移出来就是实测 3.24，不夹。
    /// </summary>
    [Fact]
    public void 平移夹在实测和表之间_只往物理上对的那边留()
    {
        var (loop, st, at) = Open(ActuatorMode.HeaterTec, l =>
        {
            l.GetGainSchedule(1, GainTable.Both).Learn(new GainPoint(40, G, 0, 0, O, 8, SteadyBiasC: 5.53));
            l.GetGainSchedule(1, GainTable.Both).Learn(new GainPoint(43, G, 0, 0, O, 8, SteadyBiasC: 3.32));
        });
        using var _ = loop;
        Override(st, 40, 3.24);
        output.WriteLine($"41：{at(41):F3}  42：{at(42):F3}  43：{at(43):F3}");
        Assert.Equal(3.24, at(42)!.Value, 6);
        Assert.Equal(3.24, at(41)!.Value, 6);
        Assert.Equal(3.32, at(43)!.Value, 6);     // 出了 ±2 K：照表

        var (loop2, st2, at2) = Open(ActuatorMode.HeaterTec, l =>
        {
            l.GetGainSchedule(1, GainTable.Both).Learn(new GainPoint(40, G, 0, 0, O, 8, SteadyBiasC: 5.53));
            l.GetGainSchedule(1, GainTable.Both).Learn(new GainPoint(43, G, 0, 0, O, 8, SteadyBiasC: 5.53));
        });
        using var __ = loop2;
        Override(st2, 40, 3.24);
        Assert.Equal(3.24, at2(41.5)!.Value, 6);

        foreach (var (row37, want39) in new[] { (4.6, 3.24 - 0.15), (6.0, 3.24) })
        {
            var (loop3, st3, at3) = Open(ActuatorMode.HeaterTec, l =>
            {
                l.GetGainSchedule(1, GainTable.Both).Learn(new GainPoint(37, G, 0, 0, O, 8, SteadyBiasC: row37));     // 合并带 2 K：隔 3 K
                l.GetGainSchedule(1, GainTable.Both).Learn(new GainPoint(40, G, 0, 0, O, 8, SteadyBiasC: 5.53));
            });
            using var ___ = loop3;
            Override(st3, 40, 3.24);
            output.WriteLine($"37 那行 {row37}：39 → {at3(39):F3}");
            Assert.Equal(want39, at3(39)!.Value, 6);
        }
    }

    /// <summary>
    /// 这一场实测过的点（<c>BiasMeasured</c>）：表 40 对（3.32）、43 错（5.53），42 改成 3.43、去 40 学到 3.35（实测挪到 40）——
    /// 回 42 按实测 3.43（只从 40 平移是 4.82：表副本 42 那头还错着）；41 夹在两个实测点中间，线性插 3.39；
    /// 42.5 离 40 远了 2 K、离 42 不远：还管着，从 42 平移（只认最新那个的话出了范围、照表 5.16）。
    /// </summary>
    [Fact]
    public void 回到实测过的目标按实测_中间插_一边从最近的平移()
    {
        var (loop, st, at) = Open(ActuatorMode.HeaterTec, l =>
        {
            l.GetGainSchedule(1, GainTable.Both).Learn(new GainPoint(40, G, 0, 0, O, 8, SteadyBiasC: 3.32));
            l.GetGainSchedule(1, GainTable.Both).Learn(new GainPoint(43, G, 0, 0, O, 8, SteadyBiasC: 5.53));
        });
        using var _ = loop;
        var both = loop.GetGainSchedule(1, GainTable.Both);
        double S(double x) => both.SteadyBiasAt(x)!.Value;
        var measured = st.GetType().GetField("BiasMeasured")!;
        Override(st, 40, 3.35);
        measured.SetValue(st, new (double, double)[] { (42, 3.43), (40, 3.35) });
        output.WriteLine($"41：{at(41):F3}  42：{at(42):F3}  42.5：{at(42.5):F3}");
        Assert.Equal(3.43, at(42)!.Value, 9);
        Assert.Equal(3.39, at(41)!.Value, 9);
        Assert.Equal(3.43 + (S(42.5) - S(42)), at(42.5)!.Value, 9);

        measured.SetValue(st, Array.Empty<(double, double)>());     // 只剩最新那个（40）：这条用例本身查得出来
        Assert.True(at(42) > 4.5, $"只从 40 平移时 42：{at(42):F3}");
        Assert.Equal(S(42.5), at(42.5)!.Value, 9);
    }

    /// <summary>
    /// 表副本只在这一场第一次改那一刻拍：40、43 两行都错成 5.53，40 改成 3.24、表里 40 那行学成 3.24，之后同一场在 40 又改一次（≈ 2.0）——
    /// 副本不换（还是 40、43 都 5.53），41.5 平移出来 2.0；第二次重拍的话 40 那行已经是 3.24，41.5 = 4.385 +（2.0 − 3.24）≈ 3.15。
    /// </summary>
    [Fact]
    public void 表副本只拍第一次_再改不重拍()
    {
        var (loop, st, at) = Open(ActuatorMode.HeaterTec, l =>
        {
            l.GetGainSchedule(1, GainTable.Both).Learn(new GainPoint(40, G, 0, 0, O, 8, SteadyBiasC: 5.53));
            l.GetGainSchedule(1, GainTable.Both).Learn(new GainPoint(43, G, 0, 0, O, 8, SteadyBiasC: 5.53));
        });
        using var _l = loop;
        loop.OuterBiasCheckAfterCross = false;
        loop.OuterBiasCheckWallHat = 0;
        var notices = new List<string>();
        loop.ChannelNotice += (_, _, m) => { if (m.Contains("外环偏置自校正")) notices.Add(m); };
        const double sp = 40;
        st.GetType().GetField("SetpointC")!.SetValue(st, sp);
        var check = typeof(HostControlLoop).GetMethod("BiasSelfCheck", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var carry = st.GetType().GetField("BiasCarryTables")!;
        var ovr = st.GetType().GetField("BiasOverrideC")!;
        var both = loop.GetGainSchedule(1, GainTable.Both);
        var t0 = new DateTime(2026, 11, 5, 10, 0, 0);
        var i = 0;
        for (; i <= 400 && notices.Count == 0; i++)
            check.Invoke(loop, new object?[] { 1, st, t0.AddSeconds(2 * i), sp, sp + 3.24, 0.0, null });
        Assert.Single(notices);
        var snap = carry.GetValue(st);
        Assert.NotNull(snap);
        Assert.Equal(3.24, (double)ovr.GetValue(st)!, 6);
        both.LearnSteadyBias(40, 3.24);                    // 稳住以后学进表
        for (var j = 0; j <= 800 && notices.Count == 1; j++, i++)
            check.Invoke(loop, new object?[] { 1, st, t0.AddSeconds(2 * i), sp, sp + 2.0, 0.0, null });
        Assert.Equal(2, notices.Count);                    // 这条用例本身真改了第二次
        var o2 = (double)ovr.GetValue(st)!;
        Assert.Equal(2.0, o2, 1);
        output.WriteLine($"第二次改成 {o2:F3}；41.5：{at(41.5):F3}（现在表里 {both.SteadyBiasAt(41.5):F3}）");
        Assert.Equal(o2, at(41.5)!.Value, 6);           // 重拍的话 ≈ 3.15：两枪按它打、冲过去
        Assert.Same(snap, carry.GetValue(st));
    }

    /// <summary>
    /// 平移按自校正那一刻的表副本：40、43 两行都错成 5.53，40 改成 3.24 以后在 40 稳住学到 3.08（表里 40 那行写成 3.08、实测跟着挪）——
    /// 拿现在的表算差归零，41.5 插成 4.31；按副本算是 3.08。
    /// </summary>
    [Fact]
    public void 平移按改之前的表_学过不归零()
    {
        var (loop, st, at) = Open(ActuatorMode.HeaterTec, l =>
        {
            l.GetGainSchedule(1, GainTable.Both).Learn(new GainPoint(40, G, 0, 0, O, 8, SteadyBiasC: 5.53));
            l.GetGainSchedule(1, GainTable.Both).Learn(new GainPoint(43, G, 0, 0, O, 8, SteadyBiasC: 5.53));
        });
        using var _ = loop;
        var both = loop.GetGainSchedule(1, GainTable.Both);
        var tablesType = typeof(HostControlLoop).GetNestedType("BiasTables", BindingFlags.NonPublic)!;
        var pre = Activator.CreateInstance(tablesType, loop.GetGainSchedule(1, GainTable.Tec).Clone(),
            loop.GetGainSchedule(1, GainTable.Heater).Clone(), both.Clone());
        st.GetType().GetField("BiasCarryTables")!.SetValue(st, pre);
        both.LearnSteadyBias(40, 3.08);
        Override(st, 40, 3.08);
        output.WriteLine($"41.5：{at(41.5):F3}（现在表里 {both.SteadyBiasAt(41.5):F3}）");
        Assert.Equal(3.08, at(41.5)!.Value, 6);
        st.GetType().GetField("BiasCarryTables")!.SetValue(st, null);
        Assert.True(at(41.5) > 4.0, $"没副本时 41.5：{at(41.5):F3}（这条用例本身没查到东西）");
    }

    private static string Notice(ActuatorMode mode, Action<HostControlLoop> setup, double sp)
    {
        var (loop, st, _) = Open(mode, setup);
        using var _l = loop;
        loop.OuterBiasCheckAfterCross = false;
        loop.OuterBiasCheckWallHat = 0;
        var notices = new List<string>();
        loop.ChannelNotice += (_, _, m) => notices.Add(m);
        st.GetType().GetField("SetpointC")!.SetValue(st, sp);
        var check = typeof(HostControlLoop).GetMethod("BiasSelfCheck", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var t0 = new DateTime(2026, 11, 5, 10, 0, 0);
        for (var i = 0; i <= 320 && notices.Count == 0; i++)
            check.Invoke(loop, new object?[] { 1, st, t0.AddSeconds(2 * i), sp, sp + 3.0, 0.0, null });
        return notices.FirstOrDefault() ?? "(没改)";
    }

    /// <summary>
    /// 自校正的提示说的是数真从哪张表来：加热棒挡、37 ℃ 按离得近选到 TEC 表（25 ℃ 1.0；加热棒表只有 50 ℃）——说「TEC 表」不说「加热棒表」；
    /// 双向挡表这个温度没有、借 TEC 表的——说借的、让在双向挡表加一行；双向挡表自己有的照旧说双向挡表。
    /// </summary>
    [Fact]
    public void 提示说数从哪张表来()
    {
        var heat = Notice(ActuatorMode.HeatOnly, l =>
        {
            l.GetGainSchedule(1, GainTable.Tec).Learn(new GainPoint(25, G, 0, 0, O, 8, SteadyBiasC: 1.0));
            l.GetGainSchedule(1, GainTable.Heater).Learn(new GainPoint(50, G, 0, 0, O, 8, SteadyBiasC: 5.5));
        }, 37);
        var borrowed = Notice(ActuatorMode.HeaterTec, l =>
            l.GetGainSchedule(1, GainTable.Tec).Learn(new GainPoint(37, G, 0, 0, O, 8, SteadyBiasC: 1.0)), 37);
        var own = Notice(ActuatorMode.HeaterTec, l =>
            l.GetGainSchedule(1, GainTable.Both).Learn(new GainPoint(37, G, 0, 0, O, 8, SteadyBiasC: 1.0)), 37);
        output.WriteLine(heat);
        output.WriteLine(borrowed);
        output.WriteLine(own);
        Assert.Contains("+1.00 K 偏了", heat);
        Assert.Contains("PID 页「TEC 表」改一下", heat);
        Assert.Contains("+1.00 K 偏了", borrowed);
        Assert.Contains("借「TEC 表」", borrowed);
        Assert.Contains("PID 页「双向挡表」这个温度加一行", borrowed);
        Assert.Contains("PID 页「双向挡表」改一下", own);
    }
}
