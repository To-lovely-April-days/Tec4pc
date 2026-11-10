using TecControl.Core.Control;
using Tec.Drivers.Rd105;
using Xunit;
using Xunit.Abstractions;
using static Tec.Core.Tests.BiasSelfCorrectTwinTests;

namespace Tec.Core.Tests;

/// <summary>
/// 表里没有这个温度的稳态偏置（2026-11-06 现场 R1-tc1-20261106-130619，双向挡釜内 65.8 → 70 ℃）：双向挡表没有 70 ℃ 那行，借了加热棒表的 7.82 K，
/// 这台釜 70 ℃ 真值 6.4–6.6（拟合 τ1 1126 s、b 6.41、壁滞后 ~40 s）。釜内 28.8 min 过 70 还在升、爬到 70.11 提前切 TEC 压低；松开 5 s 又压（量化假斜率）、
/// 第二段把夹套掉到 63.8、釜内掉 1.56 K，之后一压一冲，52.4 min 自校正改到 6.89（τ̂ 800 偏小、估高），71 min 操作人停了。
/// 孪生：那场拟合的釜（<see cref="BiasSelfCorrectTwinTests.Run130619"/>）+ 升温孪生的加热棒 / TEC 夹套，接真回路；提前切换照 DuoSession 的判据模拟（含 ② 压低再进间隔）。
/// 时间压缩 <see cref="HeatBoostTwinTests.K"/> 倍。
/// </summary>
internal static class NewTempRig
{
    /// <summary>70 ℃ 那台釜 + 提前切换；双向挡表 / 加热棒表按给的行摆（B = null 的行只带参数、不带偏置）。</summary>
    public static HeatBoostTwinTests.Rig Rig70(EarlySwitch sw, (double T, double? B)[] bothRows, (double T, double B)[]? heaterRows = null,
        Action<HostControlLoop>? tweak = null)
        => new(Run130619(), HeatBoostLevel.Off, allowed: false, tweak: tweak, bothRows: bothRows, heaterRows: heaterRows,
            onCycle: (r, c) => sw.OnCycle(r, c));

    public static string StartNote(HeatBoostTwinTests.Rig r) => r.Notes().First(n => n.Contains("串级外环积分起点"));

    public static void Report(ITestOutputHelper output, string name, HeatBoostTwinTests.Rig r, EarlySwitch sw)
    {
        BiasSelfCorrectTwinTests.Report(output, name, r, sw, 70);
        output.WriteLine($"   · {StartNote(r)}");
    }
}

/// <summary>
/// 借表（现场那种：双向挡表 70 ℃ 没有偏置、加热棒表 70 = 7.82）。
/// · 全开：过零那一枪按实测封顶（③，夹套顶在 78 上趋近所以稳着）——不再按 7.82 打回去，最多压一次、不再冲，40–57 分钟稳进 ±0.05（松开后的小门槛再改触发不触发），积分落到真值附近；
/// · 过零封顶关着：第一次压低照样发生，压低那一刻按热平衡改积分（①），② 的再进间隔挡住 5 s 后那次再压，60 分钟内稳进 ±0.05。
/// </summary>
public class OuterBorrowedBiasTwinTests(ITestOutputHelper output)
{
    [Fact]
    public async Task 借加热棒表7点82_过零时夹套稳着按实测封顶_最多压一次_不再冲_积分落到真值()
    {
        var sw = new EarlySwitch();
        await using var r = NewTempRig.Rig70(sw, bothRows: [(70, null)], heaterRows: [(70, 7.82)]);
        await r.Loop.StartChannelAsync(1, 70);
        await r.UntilMin(60);
        NewTempRig.Report(output, "借表 7.82（全开）", r, sw);

        var start = NewTempRig.StartNote(r);
        Assert.Contains("+7.82", start);
        Assert.Contains("借「加热棒表」", start);
        var cap = Assert.Single(r.Notes(), n => n.Contains("那一枪"));     // 【本地改动】2026-11-09 起两枪落点按实测（热平衡），表值只做起点：夹套钉在 78 上趋近、69 ℃ 那一枪就按热平衡落
        Assert.Contains("借「加热棒表」", cap);
        Assert.Contains("+7.82 K", cap);
        Assert.Contains("只做起点、不打回去", cap);
        Assert.True(sw.Pushes <= 1, $"压低 {sw.Pushes} 次：{string.Join("、", sw.PushAtMin.Select(m => m.ToString("F1")))} min");
        Assert.InRange(r.Overshoot(70), 0, 0.2);
        // 封顶那一枪落到 6.46（真值 6.41）；壁里存的热还是把釜内推到 70.10 压一回（夹套在 78 上顶了 25 分钟）。松开以后：
        // 松开后的小门槛再改（原来就有的那条）没触发时 40 分钟稳进 ±0.05；触发时（窗口里釜内还在从坑底往回爬，b̂ 6.12 比真值低 0.3——
        // τ̂ 1000 在升着的窗口上落在低的那边，正是 ④ 选的方向）釜内低 0.07 K 待十几分钟、56–57 分钟稳进。两种都不冲、不再压
        Assert.True(r.StayInMin(70, 0.05) <= 60, $"稳进 ±0.05 {r.StayInMin(70, 0.05):F1} min");
        Assert.InRange(r.Trace()[^1].I, 6.41 - 0.3, 6.41 + 0.3);
    }

    [Fact]
    public async Task 过零封顶关着_第一次压低照样发生_压低那一刻按热平衡改积分_不再压第二次()
    {
        var sw = new EarlySwitch();
        await using var r = NewTempRig.Rig70(sw, bothRows: [(70, null)], heaterRows: [(70, 7.82)], tweak: l => l.OuterCrossCapJacketSteadyC = 0);
        await r.Loop.StartChannelAsync(1, 70);
        await r.UntilMin(75);
        NewTempRig.Report(output, "借表 7.82（过零封顶关）", r, sw);

        Assert.DoesNotContain(r.Notes(), n => n.Contains("那一枪"));          // 热平衡落点关着、这一场又没实测过：两枪只剩「回起点」、起点就是 7.82，空打
        Assert.Equal(1, sw.Pushes);
        var refix = Assert.Single(r.Notes(), n => n.Contains("压低那一刻按热平衡"));
        Assert.Contains("借「加热棒表」", refix);
        // 改在压低那一拍（EarlySwitch 在同一拍记下压低时刻和自校正时刻）；松开以后窗口攒满自校正按小门槛再修一次是原来就有的那条，不管它
        Assert.NotEmpty(sw.FixAt);
        Assert.InRange(sw.FixAt[0] / 60, sw.PushAtMin[0] - 0.1, sw.PushAtMin[0] + 0.2);
        var t0 = r.Trace()[0].T;
        var afterFix = r.Trace().First(x => x.T - t0 >= sw.FixAt[0]).I;
        Assert.InRange(afterFix, 7.82 - 1.5 - 0.05, 7.82 - 0.2);      // 一次最多压 1.5 K、至少压过门槛
        Assert.True(r.StayInMin(70, 0.05) <= 60, $"稳进 ±0.05 {r.StayInMin(70, 0.05):F1} min");
        Assert.InRange(r.Trace()[^1].I, 6.41 - 0.3, 6.41 + 0.3);
    }

    /// <summary>复查 2026-11-06：自校正关着（OuterBiasCheckWindow = 0）时 BiasSelfCheck 的门原来每拍把热平衡缓冲清掉，过零封顶就哑了——
    /// 借表 7.82 又是现场那种 7 分钟一压（26.8、34.5、41.9、49.1、56.4 min，冲过 0.23）。封顶只看自己的开关：关着自校正照封、一次压低、积分落到真值附近。</summary>
    [Fact]
    public async Task 自校正关着_过零封顶照样按实测封_不靠自校正()
    {
        var sw = new EarlySwitch();
        await using var r = NewTempRig.Rig70(sw, bothRows: [(70, null)], heaterRows: [(70, 7.82)], tweak: l => l.OuterBiasCheckWindow = TimeSpan.Zero);
        await r.Loop.StartChannelAsync(1, 70);
        await r.UntilMin(50);
        NewTempRig.Report(output, "借表 7.82（自校正关、过零封顶开）", r, sw);

        Assert.DoesNotContain(r.Notes(), n => n.Contains("外环偏置自校正"));     // 自校正关着（压低那一刻改积分跟自校正一起关）
        var cap = Assert.Single(r.Notes(), n => n.Contains("那一枪"));              // 【本地改动】落点按热平衡，不靠自校正开着
        Assert.Contains("借「加热棒表」", cap);
        Assert.True(sw.Pushes <= 1, $"压低 {sw.Pushes} 次：{string.Join("、", sw.PushAtMin.Select(m => m.ToString("F1")))} min");
        Assert.InRange(r.Overshoot(70), 0, 0.2);
        Assert.True(r.StayInMin(70, 0.05) <= 50, $"稳进 ±0.05 {r.StayInMin(70, 0.05):F1} min");    // 孪生 41 min（封顶落 6.46、一次压低、不再改）
        Assert.InRange(r.Trace()[^1].I, 6.41 - 0.3, 6.41 + 0.3);
    }
}

/// <summary>
/// 外推（⑤）：双向挡表只有 40 = 3.4（那天早上学到的那行），70 ℃ 按 0.10 K/K 外推 = 6.4，正是真值——不压、不冲；两行 30 = 2.4、40 = 3.4 拟合出来的斜率也是 0.10。
/// </summary>
public class OuterExtrapolatedBiasTwinTests(ITestOutputHelper output)
{
    [Fact]
    public async Task 双向挡表只有40那行_70按表外推到6点4_不压不冲_两行拟合也一样()
    {
        var sw1 = new EarlySwitch();
        var sw2 = new EarlySwitch();
        await using var one = NewTempRig.Rig70(sw1, bothRows: [(40, 3.4)]);
        await using var two = NewTempRig.Rig70(sw2, bothRows: [(30, 2.4), (40, 3.4)]);
        await Task.WhenAll(one.Loop.StartChannelAsync(1, 70), two.Loop.StartChannelAsync(1, 70));
        await Task.WhenAll(one.UntilMin(55), two.UntilMin(55));
        NewTempRig.Report(output, "外推（40 = 3.4 一行）", one, sw1);
        NewTempRig.Report(output, "外推（30 = 2.4、40 = 3.4 两行）", two, sw2);

        foreach (var (name, r, sw) in new[] { ("一行", one, sw1), ("两行", two, sw2) })
        {
            var start = NewTempRig.StartNote(r);
            Assert.Contains("+6.40", start);
            Assert.Contains("按表外推", start);
            Assert.Equal(0, sw.Pushes);
            Assert.InRange(r.Overshoot(70), 0, 0.08);
            Assert.DoesNotContain(r.Notes(), n => n.Contains("自校正"));
            Assert.True(r.StayInMin(70, 0.05) <= 45, $"{name}：稳进 ±0.05 {r.StayInMin(70, 0.05):F1} min");
        }
        Assert.Contains("斜率 0.10 K/K", NewTempRig.StartNote(one));
        Assert.Contains("按 2 行拟合", NewTempRig.StartNote(two));
    }
}

/// <summary>⑤ 的纯单元：<see cref="PidGainSchedule.SteadyBiasSource"/> 的外推与来路，回路的借表。</summary>
public class SteadyBiasExtrapolationTests
{
    private static readonly PidGains Inner = new(8, 0.02, 0);
    private static readonly PidGains Outer = new(2.5, 0.0079, 0);
    private static GainPoint Row(double t, double? b) => new(t, Inner, 0, 0, Outer, 8, SteadyBiasC: b);

    [Fact]
    public void 两行拟合斜率外推_两行之间插值和够近沿用算学到的()
    {
        var sch = new PidGainSchedule { SegmentBoundaryC = double.NaN };
        sch.Learn(Row(30, 2.4));
        sch.Learn(Row(40, 3.4));
        var far = sch.SteadyBiasSource(70);
        Assert.Equal(6.4, far.Bias!.Value, 6);
        Assert.Equal(BiasOrigin.Extrapolated, far.Origin);
        Assert.Equal(0.10, far.SlopePerK, 6);
        Assert.Equal(2, far.Rows);
        Assert.Equal(40, far.AnchorC, 6);                           // 【本地改动】从最近那行起推（两头接得上）
        Assert.Equal(6.4, sch.SteadyBiasAt(70)!.Value, 6);          // 老接口也外推
        Assert.Equal(0.4, sch.SteadyBiasSource(10).Bias!.Value, 6); // 往下也推
        var mid = sch.SteadyBiasSource(35);
        Assert.Equal(2.9, mid.Bias!.Value, 6);
        Assert.Equal(BiasOrigin.Learned, mid.Origin);               // 两行之间插出来的算学到的
        var near = sch.SteadyBiasSource(41.5);
        Assert.Equal(3.55, near.Bias!.Value, 6);                    // 两头之外 2 K 内值也按斜率走（两头连续），只是还算学到的
        Assert.Equal(BiasOrigin.Learned, near.Origin);
        var edge = sch.SteadyBiasSource(50);
        Assert.Equal(4.4, edge.Bias!.Value, 6);                     // 【本地改动】2026-11-09：再远就按斜率推（现场 50 ℃ 平着沿用 40 那行的 3.43、真值 5.1）
        Assert.Equal(BiasOrigin.Extrapolated, edge.Origin);
        Assert.Equal(BiasOrigin.Extrapolated, sch.SteadyBiasSource(56).Origin);
    }

    [Fact]
    public void 拟合斜率夹在范围里()
    {
        var steep = new PidGainSchedule { SegmentBoundaryC = double.NaN };
        steep.Learn(Row(30, 1.0));
        steep.Learn(Row(40, 5.0));                                  // 0.40 K/K → 0.30
        var s = steep.SteadyBiasSource(70);
        Assert.Equal(0.30, s.SlopePerK, 6);
        Assert.Equal(5.0 + 0.30 * 30, s.Bias!.Value, 6);          // 从最近那行（40 = 5.0）起推
        var flat = new PidGainSchedule { SegmentBoundaryC = double.NaN };
        flat.Learn(Row(30, 3.0));
        flat.Learn(Row(40, 3.1));                                   // 0.01 K/K → 0.02
        var f = flat.SteadyBiasSource(70);
        Assert.Equal(0.02, f.SlopePerK, 6);
        Assert.Equal(3.1 + 0.02 * 30, f.Bias!.Value, 6);
    }

    [Fact]
    public void 一行按缺省斜率_可改()
    {
        var sch = new PidGainSchedule { SegmentBoundaryC = double.NaN };
        sch.Learn(Row(40, 3.4));
        var r = sch.SteadyBiasSource(70);
        Assert.Equal(6.4, r.Bias!.Value, 6);
        Assert.Equal(BiasOrigin.Extrapolated, r.Origin);
        Assert.Equal(1, r.Rows);
        Assert.Equal(40, r.AnchorC);
        sch.SteadyBiasSlopePerK = 0.2;
        Assert.Equal(9.4, sch.SteadyBiasAt(70)!.Value, 6);
        Assert.Equal(0.2, sch.Clone().SteadyBiasSlopePerK);        // 副本带着规矩
    }

    [Fact]
    public void 外推距离封顶_不跨段_没带偏置的行不算()
    {
        var sch = new PidGainSchedule { SegmentBoundaryC = double.NaN };
        sch.Learn(Row(40, 3.4));
        sch.Learn(Row(70, null));                                   // 整过、没学到偏置：不算
        Assert.Null(sch.SteadyBiasAt(130));                         // 90 K > 80
        Assert.NotNull(sch.SteadyBiasAt(120));                      // 80 K 刚好
        sch.SteadyBiasExtrapolateReachC = 0;
        Assert.Null(sch.SteadyBiasAt(70));                          // 关了：原来的规矩（15 K 外 null）
        Assert.Equal(3.4, sch.SteadyBiasAt(50));                    // 关了：15 K 内照旧平着沿用
        var seg = new PidGainSchedule { SegmentBoundaryC = 100 };
        seg.Learn(Row(50, 3.0));
        Assert.Null(seg.SteadyBiasAt(110));                         // 另一段（另一套执行器）不推
        Assert.Equal(3.0 + 0.1 * 40, seg.SteadyBiasAt(90)!.Value, 6);
    }

    [Fact]
    public void 回路_双向挡表给不出借加热棒表记借的_有行先外推_有这个温度就是学到的()
    {
        var dev = new FakeRd105Device();
        dev.Open();
        var loop = new HostControlLoop(new Rd105Link(dev).Controller);
        loop.SetActuatorMode(1, ActuatorMode.HeaterTec);
        var heater = loop.GetGainSchedule(1, GainTable.Heater);
        heater.SegmentBoundaryC = double.NaN;
        heater.Learn(Row(70, 7.82));
        var both = loop.GetGainSchedule(1, GainTable.Both);
        both.SegmentBoundaryC = double.NaN;
        both.Learn(Row(70, null));                                  // 双向挡 70 整过、没学到偏置
        var borrowed = loop.TableSteadyBias(1, 70);
        Assert.Equal(7.82, borrowed.Bias);
        Assert.Equal(BiasOrigin.Borrowed, borrowed.Origin);
        Assert.Equal(GainTable.Heater, borrowed.From);
        Assert.Equal(7.82, loop.BothSteadyBias(1, 70));

        both.Learn(Row(40, 3.4));                                   // 自己有行：先外推，不借
        var extra = loop.TableSteadyBias(1, 70);
        Assert.Equal(6.4, extra.Bias!.Value, 6);
        Assert.Equal(BiasOrigin.Extrapolated, extra.Origin);
        Assert.Equal(GainTable.Both, extra.From);
        Assert.Equal(6.4, loop.BothSteadyBias(1, 70)!.Value, 6);

        both.LearnSteadyBias(70, 6.5);                              // 学到了：按学到的
        var learned = loop.TableSteadyBias(1, 70);
        Assert.Equal(6.5, learned.Bias);
        Assert.Equal(BiasOrigin.Learned, learned.Origin);
        Assert.Equal(GainTable.Both, learned.From);

        var empty = new HostControlLoop(new Rd105Link(dev).Controller);
        empty.SetActuatorMode(1, ActuatorMode.HeaterTec);
        Assert.Null(empty.TableSteadyBias(1, 70).Bias);             // 三张都空：null
    }
}
