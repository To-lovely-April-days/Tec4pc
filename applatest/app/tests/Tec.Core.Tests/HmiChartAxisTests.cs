using Tec.Hmi.Ui.Controls;
using Tec.Hmi.Ui.ViewModels;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// HMI 手动控制面板曲线的纵轴自动缩放（2026-11-03 用户要的：稳住以后要看得出 0.1 ℃ 的起伏），
/// 以及实测读数念两位、不印「−0.0」。数字取自当天四次现场记录（15 / 8 / 0 / −10 ℃）。
/// </summary>
public sealed class HmiChartAxisTests
{
    private const double Eps = 1e-9;

    private static void AssertNiceAndCovers(double dLo, double dHi, (double Lo, double Hi, string Format) r)
    {
        Assert.True(r.Lo <= dLo + Eps, $"下界 {r.Lo} 高过数据 {dLo}");
        Assert.True(r.Hi >= dHi - Eps, $"上界 {r.Hi} 低过数据 {dHi}");
        var step = (r.Hi - r.Lo) / HmiChartAxis.Cells;
        var e = Math.Pow(10, Math.Floor(Math.Log10(step) + Eps));
        var f = Math.Round(step / e, 6);
        Assert.Contains(f, new[] { 1.0, 2.0, 2.5, 5.0, 10.0 });
        var k = r.Lo / step;
        Assert.True(Math.Abs(k - Math.Round(k)) < 1e-6, $"下界 {r.Lo} 不在格距 {step} 的整数倍上");
    }

    [Fact]
    public void 稳住只看Tr_一格0点1度_整张0点6度()
    {
        // 15 ℃ 那次到站以后 Tr 在 14.97 … 15.05
        var r = HmiChartAxis.Fit(14.97, 15.05);
        Assert.Equal(14.7, r.Lo, 6);
        Assert.Equal(15.3, r.Hi, 6);
        Assert.Equal("0.0", r.Format);
        AssertNiceAndCovers(14.97, 15.05, r);
        // 0.1 ℃ 占整张图高的六分之一：屏上几十个像素
        Assert.True(0.1 / (r.Hi - r.Lo) > 0.15);
    }

    [Fact]
    public void 一条平线不放大成满屏_按最小跨度取整()
    {
        var r = HmiChartAxis.Fit(15.00, 15.00);
        Assert.Equal(0.6, r.Hi - r.Lo, 6);
        AssertNiceAndCovers(15, 15, r);
        // 数据大致在中间
        Assert.True(Math.Abs((r.Lo + r.Hi) / 2 - 15) <= 0.1 + Eps);
    }

    [Fact]
    public void 数据小动纵轴不跟着抖()
    {
        var a = HmiChartAxis.Fit(14.97, 15.05);
        var b = HmiChartAxis.Fit(14.95, 15.03);
        var c = HmiChartAxis.Fit(14.99, 15.07);
        Assert.Equal(a, b);
        Assert.Equal(a, c);
    }

    [Fact]
    public void 带上夹套也还能看出0点1()
    {
        // 15 ℃ 保温时 Tr 15、Tj 16.7：一格 0.5，整张 3 ℃
        var r = HmiChartAxis.Fit(14.97, 16.72);
        AssertNiceAndCovers(14.97, 16.72, r);
        Assert.Equal(3.0, r.Hi - r.Lo, 6);
        Assert.Equal("0.0", r.Format);
    }

    [Fact]
    public void 全程含降温段_一格5度整数刻度()
    {
        // 15 ℃ 那次全程：夹套最低 7、釜内起点 22
        var r = HmiChartAxis.Fit(7, 22);
        AssertNiceAndCovers(7, 22, r);
        Assert.Equal(0, r.Lo, 6);
        Assert.Equal(30, r.Hi, 6);
        Assert.Equal("0", r.Format);
    }

    [Fact]
    public void 跨零_刻度不印负零()
    {
        // 0 ℃ 那次到站以后 Tr 在 −0.08 … +0.06
        var r = HmiChartAxis.Fit(-0.08, 0.06);
        AssertNiceAndCovers(-0.08, 0.06, r);
        var ticks = Enumerable.Range(0, HmiChartAxis.Cells + 1)
            .Select(i => HmiChartAxis.Tick(r.Lo, r.Hi, i, r.Format)).ToList();
        Assert.Equal(new[] { "0.3", "0.2", "0.1", "0.0", "−0.1", "−0.2", "−0.3" }, ticks);
    }

    [Fact]
    public void 零下十度保温_刻度带小数和负号()
    {
        var r = HmiChartAxis.Fit(-10.06, -9.96);
        AssertNiceAndCovers(-10.06, -9.96, r);
        Assert.Equal("−9.7", HmiChartAxis.Tick(r.Lo, r.Hi, 0, r.Format));
        Assert.Equal("−10.3", HmiChartAxis.Tick(r.Lo, r.Hi, HmiChartAxis.Cells, r.Format));
    }

    [Fact]
    public void 各种数据范围_都盖得住_格距取整()
    {
        var rnd = new Random(20261103);
        for (var n = 0; n < 5000; n++)
        {
            var a = rnd.NextDouble() * 300 - 100;
            var span = Math.Pow(10, rnd.NextDouble() * 5 - 3);   // 0.001 … 100
            var r = HmiChartAxis.Fit(a, a + span);
            AssertNiceAndCovers(a, a + span, r);
            // 不至于空得离谱：数据（不到最小跨度的按最小跨度算）至少占图高的三分之一强
            Assert.True(Math.Max(span, HmiChartAxis.MinSpan) / (r.Hi - r.Lo) >= 0.35,
                $"[{a}, {a + span}] → [{r.Lo}, {r.Hi}]");
            // 两头再各挪 0.01：格距（缩放档）不变——档位只看跨度，不看数据落在格子的哪儿
            var r2 = HmiChartAxis.Fit(a + 0.01, a + span + 0.01);
            var s1 = Math.Max(span, HmiChartAxis.MinSpan) * 1.1 / (HmiChartAxis.Cells - 1);
            if (Math.Abs(Math.Log(s1 / ((r.Hi - r.Lo) / HmiChartAxis.Cells))) > 1e-3)   // 不是正卡在换档线上
                Assert.Equal(r.Hi - r.Lo, r2.Hi - r2.Lo, 9);
        }
    }

    [Fact]
    public void 数据落在格子哪儿不改缩放档()
    {
        // 评审翻出来的：旧算法按对齐不够就升档，0.01 的挪动让整张图 2 ~ 2.5 倍来回缩放
        var a = HmiChartAxis.Fit(46.23, 48.54);
        var b = HmiChartAxis.Fit(46.25, 48.55);
        Assert.Equal(a.Hi - a.Lo, b.Hi - b.Lo, 9);
        Assert.Equal(a.Format, b.Format);
        var c = HmiChartAxis.Fit(-12.27, -9.96);   // −10 ℃ 保温，Tr + Tj
        var d = HmiChartAxis.Fit(-12.26, -9.96);
        Assert.Equal(c, d);
    }

    [Fact]
    public void 宽范围不多空出一大截()
    {
        // 升温 20 → 120：旧算法给 −50 … 250（数据只占三分之一、刻度念 −50）
        var r = HmiChartAxis.Fit(20, 120);
        Assert.Equal((0.0, 150.0, "0"), r);
        var w = HmiChartAxis.Fit(68.26, 151.69);
        AssertNiceAndCovers(68.26, 151.69, w);
        Assert.True((151.69 - 68.26) / (w.Hi - w.Lo) > 0.6);
        // 2.5 档要带一位小数才写得全
        var q = HmiChartAxis.Fit(10, 20);
        Assert.Equal(2.5, (q.Hi - q.Lo) / HmiChartAxis.Cells, 9);
        Assert.Equal("0.0", q.Format);
        Assert.Equal("7.5", HmiChartAxis.Tick(q.Lo, q.Hi, HmiChartAxis.Cells, q.Format));
    }

    [Fact]
    public void 每秒带上一张的界_小动不跳_快到边或该换档才换()
    {
        var axis = HmiChartAxis.Fit(14.97, 15.05);
        // Tr 在 15.04 ↔ 15.06 抖（中点正卡在半格线上，不带上一张会一格一格来回跳）
        var flips = 0;
        for (var k = 0; k < 200; k++)
        {
            var hi = k % 2 == 0 ? 15.04 : 15.06;
            var next = HmiChartAxis.Fit(14.99, hi, axis);
            if (next != axis) flips++;
            axis = next;
        }
        Assert.Equal(0, flips);
        // 跨度在换档线附近来回（Tr + Tj，5 格装 2.27 / 2.28 正好跨 0.5 → 1 那一档）
        var t = HmiChartAxis.Fit(14.0, 16.27);
        var changes = 0;
        for (var k = 0; k < 200; k++)
        {
            var next = HmiChartAxis.Fit(14.0, k % 2 == 0 ? 16.28 : 16.27, t);
            if (next != t) changes++;
            t = next;
        }
        Assert.True(changes <= 1, $"换了 {changes} 回");
        // 快贴边了才挪
        var held = HmiChartAxis.Fit(14.97, 15.05);
        Assert.Equal(held, HmiChartAxis.Fit(14.97, 15.25, held));
        var moved = HmiChartAxis.Fit(14.97, 15.295, held);
        Assert.NotEqual(held, moved);
        AssertNiceAndCovers(14.97, 15.295, moved);
        // 数据收窄过一档（人把窗口缩到保温段那种）就换细的，不死守粗的
        Assert.Equal(HmiChartAxis.Fit(14.97, 15.05), HmiChartAxis.Fit(14.97, 15.05, (14.0, 17.0, "0.0")));
        // 到站那一段一格 0.2，稳住收窄到最小跨度以下要回到 0.1 一格（复查翻出来的：从前永远卡在 0.2）
        var settling = HmiChartAxis.Fit(14.97, 15.60);
        Assert.Equal(0.2, (settling.Hi - settling.Lo) / HmiChartAxis.Cells, 9);
        Assert.Equal(HmiChartAxis.Fit(14.97, 15.05), HmiChartAxis.Fit(14.97, 15.05, settling));
        Assert.Equal(HmiChartAxis.Fit(15.00, 15.01), HmiChartAxis.Fit(15.00, 15.01, HmiChartAxis.Fit(14.5, 15.05)));
        // 跨度在 0.1 / 0.2 那档线附近（0.44 ↔ 0.47）来回：不跳
        var edge = HmiChartAxis.Fit(14.8, 15.27);
        var edgeChanges = 0;
        for (var k = 0; k < 200; k++)
        {
            var next = HmiChartAxis.Fit(14.8, k % 2 == 0 ? 15.24 : 15.27, edge);
            if (next != edge) edgeChanges++;
            edge = next;
        }
        Assert.True(edgeChanges <= 1, $"换了 {edgeChanges} 回");
    }

    [Theory]
    [InlineData(-15.0, "−14.7", "−15.0", "−15.3")]
    [InlineData(15.0, "15.3", "15.0", "14.7")]
    [InlineData(15.05, "15.4", "15.1", "14.8")]     // 设定值不在 0.1 上：中线取最近的 0.1，刻度照样整
    [InlineData(-10.04, "−9.7", "−10.0", "−10.3")]
    [InlineData(0.0, "0.3", "0.0", "−0.3")]          // 跨零那根不印「−0.0」
    [InlineData(0.04, "0.3", "0.0", "−0.3")]
    public void 设定值附近_正负零点三_一格零点一(double sp, string top, string mid, string bottom)
    {
        var (lo, hi, fmt) = HmiChartAxis.Near(sp)!.Value;
        Assert.Equal(0.6, hi - lo, 9);
        Assert.Equal(top, HmiChartAxis.Tick(lo, hi, 0, fmt));
        Assert.Equal(mid, HmiChartAxis.Tick(lo, hi, 3, fmt));
        Assert.Equal(bottom, HmiChartAxis.Tick(lo, hi, 6, fmt));
        Assert.True(sp >= lo + 0.25 - Eps && sp <= hi - 0.25 + Eps, $"设定值 {sp} 不在中间那一格附近：{lo}..{hi}");
    }

    [Fact]
    public void 设定值附近_没有数退回按数据缩放()
    {
        Assert.Null(HmiChartAxis.Near(double.NaN));
        Assert.Null(HmiChartAxis.Near(double.PositiveInfinity));
        Assert.Null(HmiChartAxis.Near(1e300));
    }

    [Fact]
    public void 大到算不动的数不卡死()
    {
        foreach (var (lo, hi) in new[] { (1e16, 1e16), (3.4e38, 3.4e38), (0, double.MaxValue),
                                         (-1e308, 1e308), (double.MaxValue, double.MaxValue) })
        {
            var r = HmiChartAxis.Fit(lo, hi);
            Assert.True(double.IsFinite(r.Lo) && double.IsFinite(r.Hi) && r.Hi > r.Lo);
        }
    }

    [Fact]
    public void 没有数_回一个能画的界()
    {
        Assert.Equal((0.0, 6.0, "0"), HmiChartAxis.Fit(double.MaxValue, double.MinValue));
        Assert.Equal((0.0, 6.0, "0"), HmiChartAxis.Fit(double.NaN, 1));
    }

    [Fact]
    public void 实测念两位_四舍五入成零不带负号()
    {
        Assert.Equal("−10.05", HmiNum.Fixed(-10.05, 2));
        Assert.Equal("−10.06", HmiNum.Fixed(-10.06, 2));
        Assert.Equal("15.00", HmiNum.Fixed(15.0, 2));
        Assert.Equal("0.00", HmiNum.Fixed(-0.001, 2));
        Assert.Equal("−0.03", HmiNum.Fixed(-0.03, 2));
        Assert.Equal("0.0", HmiNum.Fixed(-0.03, 1));     // 从前印「−0.0」
        Assert.Equal("−0.1", HmiNum.Fixed(-0.08, 1));
        Assert.Equal("—", HmiNum.Fixed(null, 2));
        Assert.Equal("—", HmiNum.Fixed(double.NaN, 2));
    }

    [Fact]
    public void 温差带正负号_零不带()
    {
        Assert.Equal("−1.5", HmiNum.Signed(-1.53, 1));
        Assert.Equal("+1.5", HmiNum.Signed(1.5, 1));
        Assert.Equal("0.0", HmiNum.Signed(-0.02, 1));
        Assert.Equal("0.0", HmiNum.Signed(0, 1));
        Assert.Equal("—", HmiNum.Signed(double.NaN, 1));
    }
}
