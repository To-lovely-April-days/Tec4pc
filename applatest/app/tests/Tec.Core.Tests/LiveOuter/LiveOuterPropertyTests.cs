using System.Globalization;
using System.Text.RegularExpressions;
using TecControl.Core.Control;
using Xunit;
using Xunit.Abstractions;

namespace Tec.Core.Tests;

/// <summary>
/// LiveOuter（串级外环「按实测实时算」）的性质测试：不看 Python 的期望值，只看算法自己该守的事。
/// 对拍（LiveOuterGoldenTests）证明「C# 跟 Python 一样」；这里证明几条不该被以后的改动碰坏的性质：
/// 稳住时 b̂ = 夹套 − 釜内、丢读数只按模型推、单个坏点带不动夹套目标、τ̂ 出不了 0.4…3 倍、报警三个条件缺一不报、
/// 重摆留着 τ̂ 和把握、占位目标（不在控）的拍跟 τ 有关的量逐位不动、夹套目标夹在 −50…200、输入不合法不弄脏状态。
/// </summary>
public class LiveOuterPropertyTests(ITestOutputHelper output)
{
    private static LiveOuterInput In(double t, double sp, double tr, double tj, double dt = 2.0, bool tec = false, bool satLow = false,
        bool withheld = false, bool on = true, double? spFinal = null, double? spRate = null) => new()
        {
            T = t, Dt = dt, Sp = sp, Tr = tr, Tj = tj, TecSide = tec, InnerSatLow = satLow, TecWithheld = withheld, OutputOn = on,
            SpFinal = spFinal, SpRate = spRate,
        };

    /// <summary>标准起算：同一拍的输入先 Reset 再 Step(first)。</summary>
    private static LiveOuter Started(double tr, double tj, double sp, LiveOuterParams? p = null)
    {
        var lo = new LiveOuter(p);
        var i = In(0, sp, tr, tj);
        lo.Reset(i);
        lo.Step(i, first: true);
        return lo;
    }

    /// <summary>比例那一份 G(e)，照改动方案 B 节的公式另写一遍（不调被测类里的）。</summary>
    private static double G(LiveOuter lo, double e) =>
        e <= 0 ? (lo.K - 1) * e : lo.PUp * Math.Tanh((lo.K - 1) * e / lo.PUp);

    [Fact]
    public void 起算_釜壁估计等于夹套_T等于釜内_b等于夹套减釜内_第一拍不跑观测器()
    {
        var lo = new LiveOuter();
        Assert.False(lo.Started);
        Assert.Equal(950.0, lo.Tau);
        Assert.Equal(1.456346749, lo.K, 1e-9);            // 0.75·(950 + 170)² ÷ (4·950·170)
        Assert.Equal(11.089392007, lo.PUp, 1e-9);         // 0.017·950 ÷ K

        var i = In(0, sp: 40, tr: 40, tj: 46.3);
        lo.Reset(i);
        Assert.True(lo.Started);
        Assert.Equal(46.3, lo.Wall);
        Assert.Equal(40.0, lo.THat);
        Assert.Equal(46.3 - 40.0, lo.BHat);
        Assert.Equal(950.0, lo.TauEkf);

        var o = lo.Step(i, first: true);
        Assert.Equal(46.3, lo.Wall);                      // 第一拍观测器不跑：三个量原样
        Assert.Equal(40.0, lo.THat);
        Assert.Equal(46.3 - 40.0, lo.BHat);
        Assert.Equal(46.3, o.U, 1e-12);                   // 目标 = 釜内：夹套目标 = 目标 + b̂ = 此刻的夹套（不推不拉）
        Assert.False(o.AlarmFired);

        // 没 Reset 过就 Step：这一拍当起算，结果跟「先 Reset 再 Step(first)」逐位一样
        var lo2 = new LiveOuter();
        var o2 = lo2.Step(i);
        Assert.Equal(o, o2);
        Assert.Equal(lo.ExportState(), lo2.ExportState());
    }

    [Fact]
    public void 读数不动_稳住后b等于夹套减釜内()
    {
        // 起算时夹套 45、釜内 40（b̂ 起步 5）；之后夹套一直读 46.3、釜内一直读 40，目标不动。
        // 观测器的平衡点只有一个：Ŵ = 夹套、T̂ = 釜内、b̂ = Ŵ − T̂。
        var lo = Started(tr: 40, tj: 45, sp: 40);
        for (int k = 1; k <= 3000; k++) lo.Step(In(2.0 * k, sp: 40, tr: 40, tj: 46.3));
        Assert.Equal(46.3, lo.Wall, 1e-9);
        Assert.Equal(40.0, lo.THat, 1e-9);
        Assert.Equal(46.3 - 40.0, lo.BHat, 1e-6);
        Assert.Equal(950.0, lo.Tau);                      // 目标没动过：τ̂ 一拍没学
    }

    [Fact]
    public void 闭环_稳住后釜内等于目标_b等于夹套减釜内等于釜真正的维持温差()
    {
        // 一个最简单的釜：夹套一阶跟夹套目标（25 s）→ 釜壁滞后 170 s → 釜内 950 s，维持温差真值 3.0 K（跟出厂三个数对得上）。
        // 夹套目标 = 目标 + b̂ + G(目标 − T̂)：稳住时夹套 = 釜内 + 真维持温差、b̂ = 夹套 − T̂，代进去只剩 G(e) = −e，也就是 e = 0——
        // 没有积分，照样没有静差。
        const double bTrue = 3.0, sp = 50.0;
        double T = 30.0, tj = 33.0, w = 33.0;
        var lo = Started(tr: T, tj: tj, sp: T);
        double u = tj, peak = T;
        for (int k = 1; k <= 7200; k++)                   // 4 h
        {
            for (int n = 0; n < 8; n++)                   // 对象按 0.25 s 一步走 2 s
            {
                tj += 0.25 * (u - tj) / 25.0;
                w += 0.25 * (tj - w) / 170.0;
                T += 0.25 * (w - T - bTrue) / 950.0;
            }
            bool inControl = k > 5;                       // 头 10 s 没在控：目标喂当时的釜内读数
            var o = lo.Step(In(2.0 * k, sp: inControl ? sp : T, tr: T, tj: tj));
            u = inControl ? o.JacketSetpointC : tj;
            peak = Math.Max(peak, T);
        }
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"4 h 后：釜内 {T:F5}（目标 {sp}），夹套 {tj:F5}，b̂ {lo.BHat:F5}（真值 {bTrue}），τ̂ {lo.Tau:F1}，釜内最高 {peak:F4}"));
        Assert.Equal(sp, T, 1e-3);
        Assert.Equal(tj - T, lo.BHat, 1e-3);
        Assert.Equal(bTrue, lo.BHat, 1e-3);
        Assert.Equal(T, lo.THat, 1e-3);
    }

    [Fact]
    public void 丢读数_只按模型推_b和τ不动_公式照出()
    {
        var lo = Started(tr: 30, tj: 31.5, sp: 30);
        double t = 0;
        for (int k = 1; k <= 60; k++)                     // 升温途中：目标 45，釜内在升，夹套在 50
            lo.Step(In(t += 2, sp: 45, tr: 30 + 0.02 * k, tj: 50));
        Assert.NotEqual(950.0, lo.Tau);                   // 过渡里 τ̂ 已经在动（否则下面「没读数时 τ̂ 不动」没有分量）

        var twin = new LiveOuter();                       // 对照：同样的拍，读数给 +∞（也当没读数）
        twin.ImportState(lo.ExportState());
        for (int k = 0; k < 15; k++)
        {
            var s0 = lo.ExportState();
            const double dt = 2.0, tj = 50.0;
            var o = lo.Step(In(t += dt, sp: 45, tr: double.NaN, tj: tj));
            var s1 = lo.ExportState();
            // 釜壁估计照常跟夹套
            double wall = s0.Wall + (tj - s0.Wall) * (1.0 - Math.Exp(-dt / 170.0));
            Assert.Equal(wall, s1.Wall);
            // T̂ 只按模型走一步；b̂、τ̂ 一位不动
            Assert.Equal(s0.Xr + dt * ((wall - s0.Xr - s0.B) / s0.Tau), s1.Xr);
            Assert.Equal(s0.B, s1.B);
            Assert.Equal(s0.Tau, s1.Tau);
            Assert.Equal(s0.K, s1.K);
            // 第一路（回归）整个不跑：滤波量、累加量、中值缓冲都不动
            Assert.Equal((s0.F1, s0.F2, s0.G1, s0.G2), (s1.F1, s1.F2, s1.G1, s1.G2));
            Assert.Equal((s0.S0, s0.S1, s0.S2, s0.S3, s0.S4, s0.S5), (s1.S0, s1.S1, s1.S2, s1.S3, s1.S4, s1.S5));
            Assert.Equal((s0.MedN, s0.M0, s0.M1, s0.M2), (s1.MedN, s1.M0, s1.M1, s1.M2));
            // 第二路只推不修：T₂ 按模型走，b₂、θ 不动
            Assert.Equal(s0.ET + dt * s0.Th * (wall - s0.ET - s0.EB), s1.ET);
            Assert.Equal(s0.EB, s1.EB);
            Assert.Equal(s0.Th, s1.Th);
            // 「读数 − 模型」的平均往 0 回
            Assert.Equal(s0.EpsLp + (0.0 - s0.EpsLp) * (1.0 - Math.Exp(-dt / 20.0)), s1.EpsLp);
            // 公式照出
            Assert.Equal(45 + lo.BHat + G(lo, 45 - lo.THat), o.U, 1e-12);
            Assert.True(lo.IsFinite);

            twin.Step(In(t, sp: 45, tr: double.PositiveInfinity, tj: tj));
            Assert.Equal(s1, twin.ExportState());
        }

        // 读数回来接着修，不重摆：b̂ 从丢之前的值接着走
        double bBefore = lo.BHat;
        lo.Step(In(t += 2, sp: 45, tr: 31.5, tj: 50));
        Assert.InRange(lo.BHat, bBefore - 0.5, bBefore + 0.5);
    }

    [Fact]
    public void 单个坏点带不动夹套目标_连坏两个才进得来()
    {
        // 60 ℃ 保温稳着。A 干净；B 第 100 拍读数 +5 K（只坏一个点）；C 第 100、101 拍连坏两个。
        static List<double> Run(Func<int, double> glitch)
        {
            var lo = Started(tr: 60, tj: 65.8, sp: 60);
            var us = new List<double>();
            for (int k = 1; k <= 140; k++)
                us.Add(lo.Step(In(2.0 * k, sp: 60, tr: 60 + glitch(k), tj: 65.8)).U);
            return us;
        }

        var a = Run(_ => 0);
        var bUp = Run(k => k == 100 ? 5 : 0);
        var bDown = Run(k => k == 100 ? -5 : 0);
        var c = Run(k => k is 100 or 101 ? 5 : 0);
        // 3 点中值：只坏一个点的读数永远当不了中间那个 → 夹套目标逐位不变
        Assert.Equal(a, bUp);
        Assert.Equal(a, bDown);
        // 连坏两个：第二个进来，夹套目标动得起来（这条在说上面那条不是因为「怎么喂都不动」）
        double moved = Enumerable.Range(0, a.Count).Max(k => Math.Abs(c[k] - a[k]));
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"连坏两个 +5 K：夹套目标最多动了 {moved:F3} K"));
        Assert.True(moved > 0.3, $"连坏两个点夹套目标只动了 {moved} K");
        Assert.Equal(a.Take(100), c.Take(100));           // 第 101 拍（下标 100）之前一样
    }

    [Theory]
    [InlineData("s04_tau_hi")]          // 真 τ = 5200 s：往上顶
    [InlineData("s05_tau_lo_reseed")]   // 真 τ = 150 s：往下贴
    public void τ出不了出厂值的零点四到三倍_K不小于1(string scene)
    {
        // 输入用合成对拍那两场的（专门把 τ̂ 往两头推的）；这里只看 C# 自己的输出守不守范围，不看 Python 的期望值。
        var lo = new LiveOuter();
        const double lo0 = 0.4 * 950, hi0 = 3.0 * 950;
        double tauMin = double.MaxValue, tauMax = double.MinValue, tau2Min = double.MaxValue, tau2Max = double.MinValue, kMin = double.MaxValue;
        foreach (var beat in LiveOuterFixtures.Beats(LiveOuterFixtures.Load(LiveOuterFixtures.SynthPath(scene))))
        {
            LiveOuterFixtures.Feed(lo, beat);
            Assert.InRange(lo.Tau, lo0, hi0);
            Assert.InRange(lo.TauEkf, lo0 * (1 - 1e-12), hi0 * (1 + 1e-12));     // 1/θ 再取倒数，差 1 ulp
            Assert.True(lo.K >= 1.0);
            Assert.Equal(0.017 * lo.Tau / lo.K, lo.PUp);
            tauMin = Math.Min(tauMin, lo.Tau); tauMax = Math.Max(tauMax, lo.Tau);
            tau2Min = Math.Min(tau2Min, lo.TauEkf); tau2Max = Math.Max(tau2Max, lo.TauEkf);
            kMin = Math.Min(kMin, lo.K);
        }
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{scene}：τ̂ {tauMin:F1}…{tauMax:F1}，第二路 {tau2Min:F1}…{tau2Max:F1}，K 最小 {kMin:F4}"));
        if (scene == "s04_tau_hi")
        {
            Assert.Equal(hi0, tau2Max, 1e-9);             // 第二路实打实顶到了 3 倍
            Assert.True(tauMax > 0.95 * hi0);             // τ̂ 跟上去了（渐近，到不了正好 2850）
        }
        else
        {
            Assert.Equal(lo0, tau2Min, 1e-9);             // 第二路实打实贴到了 0.4 倍
            Assert.True(tauMin < 1.02 * lo0);
            Assert.Equal(1.0, kMin);                      // τ̂ 这么小时公式给的 K < 1：取 1
        }
    }

    /// <summary>报警场景：从 t = 0 起算，每 2 s 一拍。返回报出来的拍号、每拍条件成立与否、每拍的顶满计时。</summary>
    private static (List<int> Fired, List<bool> On, List<double> Full) AlarmRun(int beats, Func<int, double> tr, Func<int, double> tj,
        Func<int, (bool Tec, bool Sat, bool Withheld, bool On)> flags)
    {
        var lo = new LiveOuter();
        var fired = new List<int>();
        var on = new List<bool>();
        var full = new List<double>();
        for (int k = 0; k < beats; k++)
        {
            var f = flags(k);
            var i = In(2.0 * k, sp: 20, tr: tr(k), tj: tj(k), tec: f.Tec, satLow: f.Sat, withheld: f.Withheld, on: f.On);
            if (k == 0) lo.Reset(i);
            var o = lo.Step(i, first: k == 0);
            if (o.AlarmFired) fired.Add(k);
            on.Add(o.AlarmOn);
            full.Add(lo.FullSeconds);
        }
        return (fired, on, full);
    }

    private static readonly (bool, bool, bool, bool) Full = (true, true, false, true);   // TEC 侧、贴制冷下限、没在等水、输出开着

    [Fact]
    public void 报警_制冷顶满60秒_釜内还在升_釜壁比釜内冷_三条齐了报一次()
    {
        // 夹套压在 0 ℃（釜壁估计远比釜内冷），釜内从 20 ℃ 每拍升 0.02 K（放热），制冷从第 0 拍起一直顶满。
        static double Rising(int k) => 20 + 0.02 * k;
        var r = AlarmRun(80, Rising, _ => 0, _ => Full);
        // 顶满计时每拍加 2 s（第一拍也加）：第 29 拍正好 60 s，那一拍报；之后条件一直成立，不重报
        Assert.Equal(60.0, r.Full[29]);
        Assert.Equal([29], r.Fired);
        Assert.All(r.On.Take(29), Assert.False);
        Assert.All(r.On.Skip(29), Assert.True);

        // 缺一条都不报：
        Assert.Empty(AlarmRun(80, _ => 20, _ => 0, _ => Full).Fired);                    // 釜内没在升
        Assert.Empty(AlarmRun(80, Rising, _ => 40, _ => Full).Fired);                    // 釜壁估计不比釜内冷（夹套 40 ℃）
        Assert.Empty(AlarmRun(80, Rising, _ => 0, _ => (false, true, false, true)).Fired);   // 热源不在 TEC 侧
        Assert.Empty(AlarmRun(80, Rising, _ => 0, _ => (true, false, false, true)).Fired);   // 内环没贴制冷下限
        Assert.Empty(AlarmRun(80, Rising, _ => 0, _ => (true, true, true, true)).Fired);     // 在等水 / 等冷水机间隔
        Assert.Empty(AlarmRun(80, Rising, _ => 0, _ => (true, true, false, false)).Fired);   // 输出关着
    }

    [Fact]
    public void 报警_顶满断一拍就从头数_条件撤了再成立会再报()
    {
        static double Rising(int k) => 20 + 0.02 * k;
        // 第 20 拍输出关了一拍：顶满计时当拍清零，从第 21 拍重新数 → 第 50 拍才满 60 s
        var cut = AlarmRun(80, Rising, _ => 0, k => k == 20 ? (true, true, false, false) : Full);
        Assert.Equal(0.0, cut.Full[20]);
        Assert.Equal(58.0, cut.Full[49]);
        Assert.Equal([50], cut.Fired);

        // 第 29 拍报过；第 40 拍内环离开下限一拍（条件撤了 = 重新武装）；再顶满 60 s（第 70 拍）再报一次
        var again = AlarmRun(100, Rising, _ => 0, k => k == 40 ? (true, false, false, true) : Full);
        Assert.Equal([29, 70], again.Fired);
        Assert.False(again.On[40]);
        Assert.All(again.On.Skip(41).Take(29), Assert.False);
        Assert.True(again.On[70]);
    }

    [Fact]
    public void 重摆_釜壁估计和T和b按读数重摆_τ和它的把握留着()
    {
        // 先让 τ̂ 两路都认到别处去（合成场 s04 的前 300 拍：真 τ = 5200 s）
        var lo = new LiveOuter();
        foreach (var beat in LiveOuterFixtures.Beats(LiveOuterFixtures.Load(LiveOuterFixtures.SynthPath("s04_tau_hi"))).Take(300))
            LiveOuterFixtures.Feed(lo, beat);
        // 顶满计时、ε 的平均摆成非零，好看出重摆清没清
        lo.ImportState(lo.ExportState() with { FullS = 44.0, EpsLp = 0.7 });
        var before = lo.ExportState();
        Assert.True(before.Tau > 1.5 * 950, "τ̂ 没认上去，这条测试没有分量");
        Assert.True(before.TauE > 1.5 * 950);
        double prior = 0.3 / 950 * (0.3 / 950);
        Assert.True(before.P22 < 0.5 * prior, "对 τ 的把握没收紧，这条测试没有分量");

        var viaReset = new LiveOuter();
        viaReset.ImportState(before);

        // 断档之后拿到的第一拍：釜内 12.3、夹套 20.0（跟断之前毫无关系）
        var seat = In(5000, sp: 12.3, tr: 12.3, tj: 20.0);
        lo.Reseat(seat);
        var after = lo.ExportState();
        // 留着的：τ̂、K、第二路的 θ / τ₂、对 τ 的把握
        Assert.Equal(before.Tau, after.Tau);
        Assert.Equal(before.K, after.K);
        Assert.Equal(before.Th, after.Th);
        Assert.Equal(before.TauE, after.TauE);
        Assert.Equal(before.P22, after.P22);
        // 重摆的：Ŵ = 夹套、T̂ = 釜内、b̂ = 夹套 − 釜内，连同跟着它们的那些
        Assert.Equal(20.0, after.Wall);
        Assert.Equal(20.0, after.WallPrev);
        Assert.Equal(0.0, after.WallRate);
        Assert.Equal(12.3, after.Xr);
        Assert.Equal(20.0 - 12.3, after.B);
        Assert.Equal(12.3, after.Ref);
        Assert.Equal((1, 12.3), (after.MedN, after.M0));
        Assert.Equal((12.3, 20.0 - 12.3), (after.ET, after.EB));
        Assert.Equal((0.05, 0.0, 0.0, 4.0, 0.0), (after.P00, after.P01, after.P02, after.P11, after.P12));
        Assert.Equal((12.3, 12.3, 20.0, 20.0, 12.3), (after.F1, after.F2, after.G1, after.G2, after.Tr0));
        Assert.Equal((0.0, 0.0, 0.0, 0.0, 0.0, 0.0), (after.S0, after.S1, after.S2, after.S3, after.S4, after.S5));
        // 这里定的两样：顶满计时从头数、ε 的平均归零
        Assert.Equal(0.0, after.FullS);
        Assert.Equal(0.0, after.EpsLp);
        // 上一拍的终点不动（跟 Reset 一样）：紧跟的 Step 还要拿这一拍的终点跟它比
        Assert.True(after.HasSpfPrev);
        Assert.Equal(before.SpfPrev, after.SpfPrev);

        // 对照：Reset（照 Python 的 reset）留 τ̂，但第二路从 τ̂ 重新起、把握回先验——所以断档要用 Reseat 不用 Reset
        viaReset.Reset(seat);
        var r = viaReset.ExportState();
        Assert.Equal(before.Tau, r.Tau);
        Assert.Equal(before.Tau, r.TauE);
        Assert.Equal(1.0 / before.Tau, r.Th);
        Assert.Equal(0.3 / before.Tau * (0.3 / before.Tau), r.P22, 1e-20);
        Assert.Equal(44.0, r.FullS);                      // Reset 不清顶满计时（照 Python）

        // 重摆完紧跟的那一步。只在 Reseat 之后看把握不够——这一步会拿这一拍的终点跟断档前的比：
        // · 对照（不 Pin）：这一拍的目标 12.3 跟断档前的终点差着几十 K，照「终点变了 Δ」把握被放开——断档里真下发了新目标就该这样；
        var noPin = new LiveOuter();
        noPin.ImportState(after);
        noPin.Step(seat, first: true);
        double dl = seat.Sp - before.SpfPrev;
        Assert.True(Math.Abs(dl) > 20, "断档前的终点跟 12.3 差得不够远，这条对照没有分量");
        Assert.Equal(before.P22 + (prior - before.P22) * (dl * dl / (dl * dl + 5.0 * 5.0)), noPin.ExportState().P22, 1e-22);
        // · 这一拍的目标是占位的（不在控：目标 = 釜内读数 12.3，那个 Δ 只是釜内在断档里走了多远）：先 PinTarget 再 Step，把握原样
        lo.PinTarget(seat.Sp);
        lo.Step(seat, first: true);
        var seated = lo.ExportState();
        Assert.Equal(before.P22, seated.P22);
        Assert.Equal(before.Th, seated.Th);
        Assert.Equal(before.Tau, seated.Tau);
        Assert.Equal(0.0, seated.GSp);

        // 接着走：往后 50 拍，量都是有限数，τ̂ 没跳回出厂值
        for (int k = 1; k <= 50; k++) lo.Step(In(5000 + 2.0 * k, sp: 12.3, tr: 12.3, tj: 20.0));
        Assert.True(lo.IsFinite);
        Assert.True(lo.Tau > 1.5 * 950);
        Assert.Equal(20.0 - 12.3, lo.BHat, 1e-6);         // 读数没动：b̂ 就是重摆的那个数

        // 没起算过的对象：Reseat 就是 Reset
        var fresh1 = new LiveOuter();
        var fresh2 = new LiveOuter();
        fresh1.Reseat(seat);
        fresh2.Reset(seat);
        Assert.Equal(fresh2.ExportState(), fresh1.ExportState());
    }

    [Fact]
    public void Reset不清上一拍的终点_用过的对象再Reset_紧跟的第一拍照样拿终点跟它比()
    {
        // 照 Python 的 reset：不动 spf_prev。用过的对象（τ̂ 认到出厂值以上）再 Reset，第二路的把握回到「按此刻 τ̂ 的先验」(0.3 ÷ τ̂)²，
        // 比「按出厂值的先验」(0.3 ÷ 950)² 小；紧跟的第一拍终点跟 Reset 之前那一拍差 Δ → 把握再按 Δ² ÷ (Δ² + 25) 往后者放。
        // （合成场 s05 的第二次 reset 目标没变，钉不住这一条：Reset 要是把上一拍的终点清了，对拍照样全绿。）
        var lo = new LiveOuter();
        foreach (var beat in LiveOuterFixtures.Beats(LiveOuterFixtures.Load(LiveOuterFixtures.SynthPath("s04_tau_hi"))).Take(300))
            LiveOuterFixtures.Feed(lo, beat);
        var before = lo.ExportState();
        Assert.True(before.HasSpfPrev);
        Assert.True(before.Tau > 1.5 * 950, "τ̂ 没认上去，这条测试没有分量");
        double prior = 0.3 / 950 * (0.3 / 950);
        double v = 0.3 * (1.0 / before.Tau), p0 = v * v;

        var same = new LiveOuter();
        same.ImportState(before);

        var i = In(5000, sp: before.SpfPrev + 7.0, tr: 30, tj: 33);
        lo.Reset(i);
        var r = lo.ExportState();
        Assert.True(r.HasSpfPrev);
        Assert.Equal(before.SpfPrev, r.SpfPrev);
        Assert.Equal(p0, r.P22);
        lo.Step(i, first: true);
        double dl = i.Sp - before.SpfPrev;
        Assert.Equal(p0 + (prior - p0) * (dl * dl / (dl * dl + 5.0 * 5.0)), lo.ExportState().P22, 1e-22);
        Assert.True(lo.ExportState().P22 > 1.5 * p0, "把握应当真被放开了");
        Assert.Equal(i.Sp, lo.ExportState().SpfPrev);

        // 对照：终点没变（目标 = Reset 之前那一拍的终点）→ 不放
        var j = In(5000, sp: before.SpfPrev, tr: 30, tj: 33);
        same.Reset(j);
        same.Step(j, first: true);
        Assert.Equal(p0, same.ExportState().P22);
    }

    [Fact]
    public void 占位目标_先PinTarget再Step_跟τ有关的量逐位不动_b和T照算_不Pin的对照门控开着τ被带着走()
    {
        // 在控走一段过渡（合成场 s03 的前 150 拍：40 → 58 ℃ 升到一半，真 τ = 820 s，两路都在往小认、相关都攒着），然后「停控」：
        // 目标换成占位的（= 当拍的釜内读数），读数、夹套照 s03 接着走（釜内还在自己升 / 降）；中间加一拍掉 15 K（加冷料）、连坏两个点。
        var beats = LiveOuterFixtures.Beats(LiveOuterFixtures.Load(LiveOuterFixtures.SynthPath("s03_retarget"))).ToArray();
        var lo = new LiveOuter();
        foreach (var beat in beats.Take(150)) LiveOuterFixtures.Feed(lo, beat);
        var stop = lo.ExportState();
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"停控那一拍：门控 {stop.GSp:F3}，τ̂ {stop.Tau:F1}，第二路 {stop.TauE:F1}，P₂₂ {stop.P22:E3}，P₀₂ {stop.P02:E3}，P₁₂ {stop.P12:E3}"));
        Assert.True(stop.GSp > 0.9 && stop.Tau < 949 && stop.TauE < 900 && stop.P02 != 0.0 && stop.P12 != 0.0,
            $"停控前不在过渡里（门控 {stop.GSp}、τ̂ {stop.Tau}、第二路 {stop.TauE}、P02 {stop.P02}、P12 {stop.P12}），这条测试没有分量");
        var twin = new LiveOuter();                       // 对照：同样的输入，不 Pin（原来的喂法）
        twin.ImportState(stop);

        double offset = 0, gTwinSum = 0, bMoved = 0;
        int n = 0;
        foreach (var beat in beats.Skip(150))
        {
            if (n == 70) offset = -15;                    // 加冷料：读数一下掉 15 K 不回来
            double tr = beat.Input.Tr + offset + (n is 30 or 31 ? 5 : 0);
            var i = beat.Input with { Sp = tr, Tr = tr, SpFinal = null, SpRate = null };
            var s0 = lo.ExportState();
            lo.PinTarget(i.Sp);
            var o = lo.Step(i);
            var s = lo.ExportState();
            // 跟 τ 有关的：逐位不动（从停控后的第一拍起）
            Assert.Equal(stop.Tau, s.Tau);
            Assert.Equal(stop.K, s.K);
            Assert.Equal(stop.Th, s.Th);
            Assert.Equal(stop.TauE, s.TauE, 1e-9);        // τ₂ = 1 ÷ θ 每拍重算，θ 没动它就是同一个数
            Assert.Equal(stop.P22, s.P22);
            Assert.Equal(0.0, s.GSp);
            Assert.Equal((0.0, 0.0), (s.P02, s.P12));
            Assert.Equal((i.Sp, i.Sp), (s.Ref, s.SpfPrev));
            // 别的照算：釜壁估计照跟夹套；公式照出（目标 = 读数 → 夹套目标 = 读数 + b̂ + G(读数 − T̂)）
            Assert.Equal(s0.Wall + (i.Tj - s0.Wall) * (1.0 - Math.Exp(-i.Dt / 170.0)), s.Wall);
            Assert.Equal(tr, lo.ExportState().M2);        // 读数照进中值缓冲
            Assert.Equal(i.Sp + lo.BHat + G(lo, i.Sp - lo.THat), o.U, 1e-11);
            bMoved = Math.Max(bMoved, Math.Abs(s.B - s0.B));
            Assert.True(lo.IsFinite);

            twin.Step(i);
            gTwinSum += twin.GSp;
            n++;
        }
        Assert.True(bMoved > 0.05, $"b̂ 应当照常跟着读数修（每拍最多动了 {bMoved}）");
        // 对照：不 Pin 的那个，同样的读数——门控是开着的、τ 两路被带着走、把握被放开过
        var t = twin.ExportState();
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"不 Pin 的对照：门控平均 {gTwinSum / n:F3}；τ̂ {stop.Tau:F1} → {t.Tau:F1}，第二路 {stop.TauE:F1} → {t.TauE:F1}，P₂₂ {stop.P22:E3} → {t.P22:E3}"));
        Assert.True(gTwinSum / n > 0.5, $"对照的门控平均只有 {gTwinSum / n}");
        Assert.True(Math.Abs(t.Tau - stop.Tau) > 20 && Math.Abs(t.TauE - stop.TauE) > 20, $"对照的 τ̂ {t.Tau}、第二路 {t.TauE} 没怎么动");

        // 下发目标（不 Pin）：把握照「新目标 − 上一拍的釜内读数」放开、门控打开；这一拍观测器里 τ 还没学（门控是上一拍的 0、相关是 0）
        var last = lo.ExportState();
        var go = beats[^1].Input with { T = beats[^1].Input.T + 2, Sp = last.SpfPrev + 10, Tr = last.SpfPrev, SpFinal = null, SpRate = null };
        lo.Step(go);
        var g = lo.ExportState();
        double prior = 0.3 / 950 * (0.3 / 950), dl = go.Sp - last.SpfPrev;
        Assert.Equal(10.0, dl, 1e-9);
        Assert.Equal(last.P22 + (prior - last.P22) * (dl * dl / (dl * dl + 5.0 * 5.0)), g.P22, 1e-22);
        Assert.Equal(last.Tau, g.Tau);
        Assert.Equal(last.Th, g.Th);
        Assert.True(g.GSp > 0.99);

        // 没起算过：什么都不做（Step 会先 Reset）；占位目标不是有限数：抛，状态不动
        var fresh = new LiveOuter();
        fresh.PinTarget(40);
        Assert.Equal(new LiveOuter().ExportState(), fresh.ExportState());
        var s1 = lo.ExportState();
        Assert.ThrowsAny<ArgumentException>(() => lo.PinTarget(double.NaN));
        Assert.ThrowsAny<ArgumentException>(() => lo.PinTarget(double.PositiveInfinity));
        Assert.Equal(s1, lo.ExportState());
    }

    [Fact]
    public void 夹套目标夹在负50到200_没夹过的那个照公式出()
    {
        // 往上：目标 195，公式给 195 + b̂ + 接近 P上限 ≈ 208
        var lo = Started(tr: 20, tj: 22, sp: 20);
        var hi = lo.Step(In(2, sp: 195, tr: 20, tj: 22));
        Assert.True(hi.U > 200, $"U = {hi.U}");
        Assert.Equal(200.0, hi.JacketSetpointC);
        Assert.Equal(195 + lo.BHat + G(lo, 195 - lo.THat), hi.U, 1e-12);

        // 往下：釜内 100、目标 −40，降温方向不封顶 → 远低于 −50
        lo = Started(tr: 100, tj: 102, sp: 100);
        var low = lo.Step(In(2, sp: -40, tr: 100, tj: 102));
        Assert.True(low.U < -50, $"U = {low.U}");
        Assert.Equal(-50.0, low.JacketSetpointC);

        // 范围以内：两个数是同一个
        lo = Started(tr: 40, tj: 43, sp: 40);
        var mid = lo.Step(In(2, sp: 45, tr: 40, tj: 43));
        Assert.InRange(mid.U, -50, 200);
        Assert.Equal(mid.U, mid.JacketSetpointC);

        // 范围是参数（缺省 −50…200）
        lo = Started(tr: 40, tj: 43, sp: 40, new LiveOuterParams { JacketMaxC = 44.0 });
        Assert.Equal(44.0, lo.Step(In(2, sp: 45, tr: 40, tj: 43)).JacketSetpointC);
    }

    [Fact]
    public void 控制律_升温方向比例那一份封顶在P上限_降温方向不封顶()
    {
        var lo = Started(tr: 50, tj: 53, sp: 50);
        double t = 0;
        foreach (double e in new[] { 0.05, 0.5, 3, 10, 40, 150 })
        {
            // 升温方向：比例那一份 = P上限·tanh((K − 1)·e ÷ P上限)，永远小于 P上限
            var o = lo.Step(In(t += 2, sp: 50 + e, tr: 50, tj: 53));
            double eNow = 50 + e - lo.THat;
            double part = o.U - (50 + e) - lo.BHat;
            Assert.Equal(lo.PUp * Math.Tanh((lo.K - 1) * eNow / lo.PUp), part, 1e-11);
            Assert.InRange(part, 0, lo.PUp);
            if (e >= 150) Assert.True(part > 0.999 * lo.PUp);
        }
        foreach (double e in new[] { 0.05, 0.5, 3, 10, 40, 150 })
        {
            // 降温方向：斜率 K − 1，不封顶
            var o = lo.Step(In(t += 2, sp: 50 - e, tr: 50, tj: 53));
            double eNow = 50 - e - lo.THat;
            double part = o.U - (50 - e) - lo.BHat;
            Assert.Equal((lo.K - 1) * eNow, part, 1e-11);
            if (e >= 150) Assert.True(part < -5 * lo.PUp);
        }
    }

    [Fact]
    public void 目标从起算起一直不动_τ一拍不学()
    {
        // 认 τ 只在「目标变动引起的过渡」里认。目标从头到尾是 30（= 起算时的釜内读数）：
        // 不管夹套、釜内自己怎么晃，门控恒为 0，τ̂ 和第二路的 τ 一位不动。
        var lo = Started(tr: 30, tj: 31, sp: 30);
        for (int k = 1; k <= 2000; k++)
        {
            lo.Step(In(2.0 * k, sp: 30, tr: 30 + 5 * Math.Sin(k / 90.0), tj: 31 + 20 * Math.Sin(k / 40.0)));
            Assert.Equal(0.0, lo.GSp);
            Assert.Equal(950.0, lo.Tau);
            Assert.Equal(950.0, lo.TauEkf, 1e-9);
        }
        Assert.True(lo.IsFinite);
    }

    [Fact]
    public void 输入不合法_抛出来_状态一位不动_还能接着用()
    {
        var lo = Started(tr: 40, tj: 43, sp: 40);
        for (int k = 1; k <= 20; k++) lo.Step(In(2.0 * k, sp: 45, tr: 40 + 0.01 * k, tj: 50));
        var s0 = lo.ExportState();

        var bad = new[]
        {
            In(42, 45, 40.2, 50, dt: 0),                                  // Python 在这里是除零
            In(42, 45, 40.2, 50, dt: -2),
            In(42, 45, 40.2, 50, dt: double.NaN),
            In(42, 45, 40.2, 50, dt: double.PositiveInfinity),
            In(42, 45, 40.2, double.NaN),                                 // 夹套读数无效：Python 里 NaN 进了釜壁估计再也出不来
            In(42, 45, 40.2, double.NegativeInfinity),
            In(42, double.NaN, 40.2, 50),
            In(double.NaN, 45, 40.2, 50),
            In(42, 45, 40.2, 50, spFinal: double.NaN),
            In(42, 45, 40.2, 50, spRate: double.NaN),
        };
        foreach (var i in bad)
        {
            Assert.ThrowsAny<ArgumentException>(() => lo.Step(i));
            Assert.Equal(s0, lo.ExportState());
        }
        Assert.ThrowsAny<ArgumentException>(() => lo.Reseat(In(42, 45, double.NaN, 50)));
        Assert.ThrowsAny<ArgumentException>(() => lo.Reseat(In(42, 45, 40.2, double.NaN)));
        Assert.Equal(s0, lo.ExportState());

        var o = lo.Step(In(42, 45, 40.2, 50));
        Assert.True(double.IsFinite(o.U));
        Assert.True(lo.IsFinite);

        // 起算要有效的釜内、夹套读数；没起算成就还是「没起算」
        var fresh = new LiveOuter();
        Assert.ThrowsAny<ArgumentException>(() => fresh.Reset(In(0, 40, double.NaN, 43)));
        Assert.ThrowsAny<ArgumentException>(() => fresh.Reset(In(0, 40, 40, double.NaN)));
        Assert.ThrowsAny<ArgumentException>(() => fresh.Step(In(0, 40, double.NaN, 43)));
        Assert.False(fresh.Started);
    }

    [Fact]
    public void 内部量出了NaN或无穷_IsFinite说得出来()
    {
        // 挂接层靠它决定要不要把这一路影子停掉：正常走着是 true，任何一个内部量坏了就是 false。
        var lo = Started(tr: 40, tj: 43, sp: 40);
        for (int k = 1; k <= 20; k++) lo.Step(In(2.0 * k, sp: 45, tr: 40 + 0.01 * k, tj: 50));
        Assert.True(lo.IsFinite);
        var good = lo.ExportState();
        var broken = new[]
        {
            good with { Tau = double.NaN }, good with { K = double.PositiveInfinity }, good with { Wall = double.NaN }, good with { Xr = double.NaN },
            good with { B = double.NegativeInfinity }, good with { Ref = double.NaN }, good with { EpsLp = double.NaN }, good with { GSp = double.NaN },
            good with { ET = double.NaN }, good with { EB = double.NaN }, good with { Th = double.NaN }, good with { TauE = double.PositiveInfinity },
            good with { P00 = double.NaN }, good with { P01 = double.NaN }, good with { P02 = double.NaN },
            good with { P11 = double.NaN }, good with { P12 = double.NaN }, good with { P22 = double.NaN },
            good with { F1 = double.NaN }, good with { F2 = double.NaN }, good with { G1 = double.NaN }, good with { G2 = double.NaN },
            good with { S0 = double.NaN }, good with { S1 = double.NaN }, good with { S2 = double.NaN },
            good with { S3 = double.PositiveInfinity }, good with { S4 = double.NaN }, good with { S5 = double.NaN },
            good with { U = double.NaN }, good with { FullS = double.NaN }, good with { WallRate = double.NaN }, good with { ToEff = double.NaN },
        };
        foreach (var s in broken)
        {
            lo.ImportState(s);
            Assert.False(lo.IsFinite);
        }
        lo.ImportState(good);
        Assert.True(lo.IsFinite);
    }

    [Fact]
    public void 参数不合理_构造时就抛()
    {
        Assert.ThrowsAny<ArgumentException>(() => new LiveOuter(new LiveOuterParams { Tau0 = 0 }));
        Assert.ThrowsAny<ArgumentException>(() => new LiveOuter(new LiveOuterParams { Tw = -1 }));
        Assert.ThrowsAny<ArgumentException>(() => new LiveOuter(new LiveOuterParams { Rho = double.NaN }));
        Assert.ThrowsAny<ArgumentException>(() => new LiveOuter(new LiveOuterParams { Eps0 = 0 }));
        Assert.ThrowsAny<ArgumentException>(() => new LiveOuter(new LiveOuterParams { TauLo = 3, TauHi = 3 }));
        Assert.ThrowsAny<ArgumentException>(() => new LiveOuter(new LiveOuterParams { JacketMinC = 200, JacketMaxC = -50 }));
        _ = new LiveOuter(new LiveOuterParams { Rho = 0 });   // ρ = 0 是许的：升温方向不给比例
    }

    [Fact]
    public void 缺省参数跟参考实现的PARAMS一个不差()
    {
        // 参考实现（reference/outer_m.py）的 PARAMS 改了而这边没跟着改，这里红；没搬过来的那些开关要还是定稿的取法，否则这份移植就不是它了。
        var src = File.ReadAllText(Path.Combine(LiveOuterFixtures.Dir, "reference", "outer_m.py"));
        var block = Regex.Match(src, @"PARAMS = dict\((.*?)\r?\n\)\r?\n", RegexOptions.Singleline);
        Assert.True(block.Success, "没找到 PARAMS");
        var py = new Dictionary<string, string>();
        foreach (var line in block.Groups[1].Value.Split('\n'))
        {
            var code = line.Split('#')[0];
            foreach (Match m in Regex.Matches(code, @"(\w+)=('[^']*'|[^,\s]+)"))
                py[m.Groups[1].Value] = m.Groups[2].Value;
        }

        var p = new LiveOuterParams();
        var ported = new Dictionary<string, double>
        {
            ["tau"] = p.Tau0, ["tw"] = p.Tw, ["rho"] = p.Rho, ["k_margin"] = p.KMargin,
            ["to"] = p.To, ["eps0"] = p.Eps0, ["to_min"] = p.ToMin, ["eps_lp_s"] = p.EpsLpS,
            ["tau_lo"] = p.TauLo, ["tau_hi"] = p.TauHi, ["e0"] = p.E0,
            ["r"] = p.R, ["q_b"] = p.Qb, ["p0_b"] = p.P0b, ["p0_th_rel"] = p.P0ThRel, ["sig_delta"] = p.SigDelta, ["tcorr"] = p.TCorr,
            ["reopen_e1"] = p.ReopenE1,
            ["tf"] = p.Tf, ["tm"] = p.Tm, ["ta"] = p.Ta, ["reg_r"] = p.RegR, ["z0"] = p.Z0, ["sig_d"] = p.SigD, ["beta"] = p.Beta,
            ["alarm_full_s"] = p.AlarmFullS, ["alarm_rise_k"] = p.AlarmRiseK, ["ramp_ff"] = p.RampFf,
        };
        var notPorted = new Dictionary<string, string>
        {
            ["med3"] = "True", ["cap"] = "'tanh'", ["p_up"] = "None", ["lead"] = "0.0", ["k_hi_rel"] = "None", ["tau_mode"] = "'hyb'",
            ["gate_sp"] = "True", ["q_th_rel"] = "0.0", ["ta_down_mult"] = "1.0", ["alarm_wall"] = "True", ["alarm_src"] = "'xr'",
        };
        foreach (var (k, v) in ported)
            Assert.True(py.TryGetValue(k, out var s) && double.Parse(s, CultureInfo.InvariantCulture) == v, $"PARAMS['{k}'] = {py.GetValueOrDefault(k)}，C# 缺省是 {v}");
        foreach (var (k, v) in notPorted)
            Assert.True(py.TryGetValue(k, out var s) && s == v, $"PARAMS['{k}'] = {py.GetValueOrDefault(k)}，移植时按 {v} 走的");
        // 一个不多一个不少：PARAMS 里新添了数而这边不知道，也要红
        Assert.Equal(py.Keys.OrderBy(x => x, StringComparer.Ordinal), ported.Keys.Concat(notPorted.Keys).OrderBy(x => x, StringComparer.Ordinal));
        // Python 里写死、这边单列成参数的那几个
        Assert.Equal(60.0, p.AlarmWindowS);
        Assert.Equal((-50.0, 200.0), (p.JacketMinC, p.JacketMaxC));
    }
}
