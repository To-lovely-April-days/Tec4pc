using TecControl.Core.Control;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// LiveOuterShadow（新外环挂进回路的那一层，第 0 步只算不控）自己的规矩，不开回路、不等墙钟——时刻是测试自己编的：
/// · 起算：第一次「釜内、夹套都有效」的那一拍，等于参考喂法的 reset + 第一拍 step；
/// · 节拍：满一个外环周期才算；旧外环那一拍没到就等它；两拍之间交回去的是同一份读数；
/// · 拆小步：平常的慢拍（2.5–10 s）不拆、dt 原样一步喂，跟参考喂法在真机节拍上逐拍一致；离上一拍超过 10 s 才拆成每步 ≤ 2 s 的等长小步，
///   只有最后一小步带釜内读数；
/// · 断档：超过 60 s 用下一拍有效读数重摆 Ŵ / T̂ / b̂，τ̂ 和对它的把握留着（在控时断档里目标真改了才照常放开）；等重摆时不交读数；
/// · 喂量：在控用回路的设定、不在控用釜内读数、读数没了沿用上一个；釜内没读数照算、夹套没读数不算；
/// · 占位目标：不在控喂的目标跟着读数变不算「目标变了」——门控恒 0、τ 不学、把握不放开（坏点、釜内自己漂、断档重摆都一样），下发目标那一拍照常放开；
/// · 不拖累：核心类抛异常 / 内部量出 NaN 就停掉，Tick 自己从不往外抛。
/// 每一步真喂了什么，靠一个把 SeatCore / StepCore 记下来的子类看。
/// </summary>
public class LiveOuterShadowTests
{
    private static readonly DateTime T0 = new(2026, 11, 14, 9, 0, 0);

    /// <summary>T0 之后多少秒。按 tick 取整着加（AddSeconds 对小数部分是截断的，2.3 s 会变成 2.2999999 s）。</summary>
    private static DateTime At(double seconds) => T0.AddTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond));

    /// <summary>把每一次摆状态 / 每一步喂的输入记下来；可以指定哪一步抛异常。别的照旧走真的核心类。</summary>
    private sealed class Spy(LiveOuterParams? p = null) : LiveOuterShadow(p)
    {
        public readonly List<(LiveOuterInput In, bool Reseat)> Seats = new();
        public readonly List<(LiveOuterInput In, bool First)> Steps = new();
        public Func<int, Exception?>? Fail;      // 第几步（从 1 数）抛什么

        protected override void SeatCore(in LiveOuterInput input, bool reseat)
        {
            Seats.Add((input, reseat));
            base.SeatCore(input, reseat);
        }

        protected override LiveOuterOutput StepCore(in LiveOuterInput input, bool first)
        {
            Steps.Add((input, first));
            if (Fail?.Invoke(Steps.Count) is { } ex) throw ex;
            return base.StepCore(input, first);
        }
    }

    /// <summary>不在控、两个读数都有效的一拍。</summary>
    private static LiveOuterFacts Idle(double tr, double tj) => new() { Tr = tr, Tj = tj };

    /// <summary>釜内串级在控的一拍。</summary>
    private static LiveOuterFacts Ctl(double sp, double tr, double tj, bool pending = false) => new()
    {
        Tr = tr, Tj = tj, ReactorInControl = true, SetpointC = sp, LoopBeatPending = pending, OutputOn = true,
    };

    [Fact]
    public void 起算_等第一拍釜内夹套都有效_等于参考喂法的reset加第一拍()
    {
        var sh = new Spy();
        Assert.Null(sh.Tick(At(0), 2, Idle(double.NaN, 46.3)));        // 没有釜内读数：不起算
        Assert.Null(sh.Tick(At(0.5), 2, Idle(40, double.NaN)));        // 没有夹套读数：不起算
        Assert.False(sh.Started);
        Assert.Empty(sh.Steps);

        var r = sh.Tick(At(1.0), 2, Idle(40, 46.3));
        Assert.NotNull(r);
        Assert.True(sh.Started);
        var seat = Assert.Single(sh.Seats);
        Assert.False(seat.Reseat);
        var step = Assert.Single(sh.Steps);
        Assert.True(step.First);
        Assert.Equal(seat.In, step.In);                                // 同一拍的输入：先摆、再按第一拍走
        Assert.Equal(2.0, step.In.Dt);                                 // 第一拍的 dt = 外环周期（参考喂法给的也是 2）
        Assert.Equal(40.0, step.In.Sp);                                // 不在控：目标 = 当时的釜内读数
        Assert.Null(step.In.SpFinal);                                  // 第 0 步不给终点和坡速
        Assert.Null(step.In.SpRate);

        // 跟直接照 golden.py 的喂法（reset 再 step(first)，dt = 2）走出来的状态逐位一样
        var direct = new LiveOuter();
        direct.Reset(step.In);
        var o = direct.Step(step.In, first: true);
        Assert.Equal(direct.ExportState(), sh.Core.ExportState());
        Assert.Equal(new LiveOuterReadout(o.JacketSetpointC, 46.3 - 40.0, 40.0, 46.3, 950.0, 950.0, false), r);
    }

    [Fact]
    public void 节拍_不满一个外环周期不算_满了才算_两拍之间交回同一份读数()
    {
        var sh = new Spy();
        var first = sh.Tick(At(0), 2, Idle(40, 46));
        // 回路 0.5 s 一拍：0.5 / 1.0 / 1.5 不算，2.0 算
        for (var t = 0.5; t < 2.0; t += 0.5)
        {
            Assert.Same(first, sh.Tick(At(t), 2, Idle(40.1, 46.1)));
            Assert.Single(sh.Steps);
        }
        var second = sh.Tick(At(2.0), 2, Idle(40.1, 46.1));
        Assert.NotSame(first, second);
        Assert.Equal(2, sh.Steps.Count);
        Assert.False(sh.Steps[1].First);
        Assert.Equal(2.0, sh.Steps[1].In.Dt, 12);
        Assert.Equal(40.1, sh.Steps[1].In.Tr);
        Assert.Equal(46.1, sh.Steps[1].In.Tj);
        // 回路的拍不齐（2.3 s 才来）：dt 就是真隔了多久，不拆
        sh.Tick(At(4.3), 2, Idle(40.2, 46.2));
        Assert.Equal(3, sh.Steps.Count);
        Assert.Equal(2.3, sh.Steps[2].In.Dt, 12);
        // 影子自己的秒表 = 各拍 dt 累加
        Assert.Equal(4.3, sh.Steps[2].In.T, 12);

        // 外环周期配成 0 也不会拿 dt = 0 去调核心类（它会抛）：同一个时刻再来一次不算
        var z = new Spy();
        z.Tick(At(0), 0, Idle(40, 46));
        z.Tick(At(0), 0, Idle(40, 46));
        Assert.Single(z.Steps);
        Assert.False(z.Stopped);
        z.Tick(At(0.2), 0, Idle(40, 46));
        Assert.Equal(2, z.Steps.Count);
        Assert.Equal(0.2, z.Steps[1].In.Dt, 12);
    }

    [Fact]
    public void 每两秒一拍时_跟直接照参考喂法逐拍喂核心类走到同一个状态()
    {
        // 影子（回路 0.5 s 一拍地叫它）和一个直接照 golden.py 喂的核心类，同样的读数：每个外环拍之后全部内部量逐位相等。
        // 读数来自一个随手造的釜（釜壁滞后 170 s、τ 真值 1500 s、维持温差 3 K）：先稳在 40，第 20 拍目标改 50、夹套一步抬到 62，
        // 釜内慢慢跟；中间丢 3 拍釜内读数。τ 真值比出厂的 950 大，τ̂ 会被认上去——比的不是一堆没动过的初值。
        var sh = new Spy();
        var direct = new LiveOuter();
        double w = 43, tr = 40;
        for (var k = 0; k <= 400; k++)
        {
            double sp = k < 20 ? 40 : 50;
            double tj = k < 20 ? 43 : 62;
            w += (tj - w) * 2.0 / 170;
            tr += (w - tr - 3) * 2.0 / 1500;
            double read = k is >= 100 and < 103 ? double.NaN : Math.Round(tr, 2);
            var facts = Ctl(sp, read, tj);
            for (var sub = 0; sub < 4; sub++)                          // 这一个外环周期里回路的 4 拍：只有第一拍到点
                sh.Tick(At(2.0 * k + 0.5 * sub), 2, facts);
            var i = new LiveOuterInput { T = 2.0 * k, Dt = 2.0, Sp = sp, Tr = read, Tj = tj, OutputOn = true };
            if (k == 0) direct.Reset(i);
            direct.Step(i, first: k == 0);
            Assert.Equal(direct.ExportState(), sh.Core.ExportState());
        }
        Assert.Equal(401, sh.Steps.Count);
        Assert.True(sh.Core.Tau > 1000, $"τ̂ = {sh.Core.Tau}");         // 这段输入真让 τ̂ 动了
    }

    [Fact]
    public void 平常的慢拍不拆_离上一拍超过十秒才拆成等长小步_只有最后一小步带釜内读数()
    {
        var sh = new Spy();
        sh.Tick(At(0), 2, Ctl(45, 40, 46));

        // 2.6 s、7 s、正好 10 s：都是一步走完、带读数、dt = 真隔了多久（参考喂法；真机上 2 成的拍落在 2.5–5 s）
        double t = 0;
        var n = 1;
        foreach (var gap in new[] { 2.6, 7.0, 10.0 })
        {
            t += gap;
            sh.Tick(At(t), 2, Ctl(45, 40 + 0.1 * n, 46 + 0.1 * n));
            Assert.Equal(n + 1, sh.Steps.Count);
            Assert.Equal(gap, sh.Steps[n].In.Dt, 12);
            Assert.Equal(40 + 0.1 * n, sh.Steps[n].In.Tr);             // 带着这一拍的读数
            Assert.False(sh.Steps[n].First);
            Assert.Equal(t, sh.Steps[n].In.T, 9);
            n++;
        }
        Assert.Equal(4, sh.Steps.Count);

        sh.Tick(At(t += 12.1), 2, Ctl(45, 40.5, 46.5));                // 12.1 s：真断了一阵 → 7 步，各 12.1 ÷ 7 s
        Assert.Equal(11, sh.Steps.Count);
        Assert.All(sh.Steps.Skip(4), s => Assert.Equal(12.1 / 7, s.In.Dt, 12));
        Assert.All(sh.Steps.Skip(4).Take(6), s => Assert.True(double.IsNaN(s.In.Tr)));   // 前面的小步：无读数，只推模型
        Assert.Equal(40.5, sh.Steps[10].In.Tr);                        // 最后一小步才带读数
        Assert.All(sh.Steps.Skip(4), s => { Assert.Equal(46.5, s.In.Tj); Assert.Equal(45.0, s.In.Sp); Assert.False(s.First); });
        Assert.Equal(19.6 + 12.1 / 7, sh.Steps[4].In.T, 9);            // 秒表一小步一小步地走
        Assert.Equal(31.7, sh.Steps[10].In.T, 9);

        sh.Tick(At(t += 12.0), 2, Ctl(45, 40.6, 46.6));                // 12 s：6 步，各 2 s
        Assert.Equal(17, sh.Steps.Count);
        Assert.All(sh.Steps.Skip(11), s => Assert.Equal(2.0, s.In.Dt, 12));
        Assert.All(sh.Steps.Skip(11).Take(5), s => Assert.True(double.IsNaN(s.In.Tr)));
        Assert.Equal(40.6, sh.Steps[16].In.Tr);

        sh.Tick(At(t += 60.0), 2, Ctl(45, 40.7, 46.7));                // 正好 60 s：还是接着推（30 小步，每步 2 s），不重摆
        Assert.Equal(47, sh.Steps.Count);
        Assert.All(sh.Steps.Skip(17), s => Assert.Equal(2.0, s.In.Dt, 9));
        Assert.Single(sh.Seats);
        Assert.False(sh.Stopped);
    }

    [Fact]
    public void 拆小步_釜壁估计跟一步走完是同一个数_观测器不被一大步带歪()
    {
        // 夹套 46、釜内读数从 40 掉到 39 并停在 39（过了 3 点中值，读数离模型 −1 K），这时隔 50 s 才来下一拍。
        // 不拆（显式欧拉一步 50 s）：b̂ 一步被拽走 dt·τ̂/To′²·1 K ≈ +3 K；拆成 25 个 2 s 的小步：只有最后一小步吃到读数，b̂ 只动不到 0.1 K。
        static LiveOuterInput I(double t, double dt, double tr, double tj) => new() { T = t, Dt = dt, Sp = 40, Tr = tr, Tj = tj, OutputOn = true };
        var big = new LiveOuter();
        big.Reset(I(0, 2, 40, 46));
        big.Step(I(0, 2, 40, 46), first: true);
        big.Step(I(2, 2, 39, 46));
        big.Step(I(4, 2, 39, 46));

        var sh = new Spy();
        sh.Tick(At(0), 2, Ctl(40, 40, 46));
        sh.Tick(At(2), 2, Ctl(40, 39, 46));
        sh.Tick(At(4), 2, Ctl(40, 39, 46));
        Assert.Equal(big.ExportState(), sh.Core.ExportState());        // 断档之前两边一模一样
        var b0 = big.BHat;

        big.Step(I(54, 50, 39, 47));                                   // 不拆
        sh.Tick(At(54), 2, Ctl(40, 39, 47));                           // 影子：拆
        Assert.Equal(3 + 25, sh.Steps.Count);
        Assert.Equal(big.Wall, sh.Core.Wall, 12);                      // 釜壁估计是指数离散：拆不拆同一个数
        Assert.True(Math.Abs(big.BHat - b0) > 1.5, $"一大步 b̂ 动了 {big.BHat - b0}");
        Assert.True(Math.Abs(sh.Core.BHat - b0) < 0.3, $"拆小步 b̂ 动了 {sh.Core.BHat - b0}");
    }

    [Fact]
    public void 断档超过六十秒_用下一拍有效读数重摆_tau和把握留着_等重摆时不交读数()
    {
        var sh = new Spy();
        sh.Tick(At(0), 2, Ctl(45, 40, 46));
        for (var k = 1; k <= 5; k++) sh.Tick(At(2 * k), 2, Ctl(45, 40 + 0.02 * k, 46 + 0.1 * k));
        // 把 τ̂（两路的）和把握摆成认过一阵以后的样子：重摆不许把它们放回出厂值
        var st = sh.Core.ExportState();
        sh.Core.ImportState(st with { Tau = 780, K = 1.2345, Th = 1.0 / 820, TauE = 820, P22 = 3.3e-9, FullS = 44, EpsLp = 0.7 });
        var steps = sh.Steps.Count;

        // 断了 61 s 回来，这一拍釜内没读数：不能重摆（要两个读数都有效），也不接着推；记录留空
        Assert.Null(sh.Tick(At(71), 2, Ctl(45, double.NaN, 30)));
        Assert.Equal(steps, sh.Steps.Count);
        Assert.Single(sh.Seats);
        // 夹套没读数同理
        Assert.Null(sh.Tick(At(71.5), 2, Ctl(45, 33, double.NaN)));
        Assert.Equal(steps, sh.Steps.Count);

        // 下一拍两个都有：重摆
        var r = sh.Tick(At(72), 2, Ctl(45, 33, 30));
        Assert.NotNull(r);
        Assert.Equal(2, sh.Seats.Count);
        Assert.True(sh.Seats[1].Reseat);
        Assert.Equal(steps + 1, sh.Steps.Count);
        Assert.True(sh.Steps[^1].First);
        Assert.Equal(sh.Seats[1].In, sh.Steps[^1].In);
        Assert.Equal(2.0, sh.Steps[^1].In.Dt);
        Assert.Equal(45.0, sh.Steps[^1].In.Sp);
        Assert.Equal(10 + 62.0, sh.Steps[^1].In.T, 9);                 // 秒表把断档那 62 s 也算进去了，没倒着走

        var c = sh.Core;
        Assert.Equal(30.0, c.Wall);                                    // Ŵ / T̂ / b̂ 按这一拍的读数重摆
        Assert.Equal(33.0, c.THat);
        Assert.Equal(30.0 - 33.0, c.BHat);
        Assert.Equal(780.0, c.Tau);                                    // τ̂ 两路和把握原样
        Assert.Equal(820.0, c.TauEkf);
        var after = c.ExportState();
        Assert.Equal(3.3e-9, after.P22);
        Assert.Equal(1.2345, after.K);
        Assert.Equal(new LiveOuterReadout(r!.JacketSetpointC, -3.0, 33.0, 30.0, 780.0, 820.0, false), r);

        // 重摆以后照常 2 s 一拍
        sh.Tick(At(74), 2, Ctl(45, 33.01, 30.1));
        Assert.Equal(steps + 2, sh.Steps.Count);
        Assert.False(sh.Steps[^1].First);
        Assert.Equal(2.0, sh.Steps[^1].In.Dt, 12);
    }

    [Fact]
    public void 夹套读数无效_这一拍不算_状态留着_读数回来按隔了多久接着算()
    {
        var sh = new Spy();
        sh.Tick(At(0), 2, Ctl(45, 40, 46));
        var r1 = sh.Tick(At(2), 2, Ctl(45, 40.1, 46.1));
        var before = sh.Core.ExportState();

        // 夹套读数丢了 3 拍（6 s）：不算、状态一位不动、交回去的还是上一拍那份
        for (var t = 4.0; t <= 8.0; t += 2)
        {
            Assert.Same(r1, sh.Tick(At(t), 2, Ctl(45, 40.2, double.NaN)));
            Assert.Equal(before, sh.Core.ExportState());
        }
        Assert.Equal(2, sh.Steps.Count);

        // 回来了：离上一拍 8 s → 一步接着算，dt = 8（没到 10 s 不拆；没到 60 s 不重摆）
        sh.Tick(At(10), 2, Ctl(45, 40.3, 46.3));
        Assert.Equal(3, sh.Steps.Count);
        Assert.Equal(8.0, sh.Steps[2].In.Dt, 12);
        Assert.Equal(40.3, sh.Steps[2].In.Tr);
        Assert.Single(sh.Seats);

        // 再丢 14 s：超过 10 s 才拆（7 小步，每步 2 s）
        Assert.NotNull(sh.Tick(At(17), 2, Ctl(45, 40.4, double.NaN)));
        sh.Tick(At(24), 2, Ctl(45, 40.5, 46.5));
        Assert.Equal(10, sh.Steps.Count);
        Assert.All(sh.Steps.Skip(3), s => Assert.Equal(2.0, s.In.Dt, 12));
        Assert.Single(sh.Seats);
    }

    [Fact]
    public void 目标_在控用回路的设定_不在控用釜内读数_读数没了沿用上一个_釜内没读数照算()
    {
        var sh = new Spy();
        sh.Tick(At(0), 2, Idle(40, 46));                               // 不在控
        sh.Tick(At(2), 2, Idle(40.3, 46));                             // 不在控：目标跟着釜内读数走
        sh.Tick(At(4), 2, Idle(double.NaN, 46));                       // 不在控、釜内没读数：沿用上一个目标，按无读数推
        sh.Tick(At(6), 2, Ctl(55, 40.5, 46));                          // 下发了釜内目标
        sh.Tick(At(8), 2, Ctl(55, double.NaN, 46));                    // 在控、釜内丢了：目标还是回路的设定
        // 在控的是夹套（SetpointC 是夹套目标 70）：不算「釜内在控」，目标 = 釜内读数
        sh.Tick(At(10), 2, new LiveOuterFacts { Tr = 40.7, Tj = 46, ReactorInControl = false, SetpointC = 70, OutputOn = true });
        // 在控但设定不是有限数（不该有）：退回按釜内读数
        sh.Tick(At(12), 2, Ctl(double.NaN, 40.8, 46));

        Assert.Equal(new[] { 40, 40.3, 40.3, 55, 55, 40.7, 40.8 }, sh.Steps.Select(s => s.In.Sp).ToArray());
        Assert.True(double.IsNaN(sh.Steps[2].In.Tr));
        Assert.True(double.IsNaN(sh.Steps[4].In.Tr));
        Assert.Equal(7, sh.Steps.Count);                               // 釜内没读数的两拍也算了
        Assert.All(sh.Steps, s => { Assert.Null(s.In.SpFinal); Assert.Null(s.In.SpRate); });
        Assert.False(sh.Stopped);
        // 最后那一拍（在控但设定无效）喂的也是占位目标，照样先 PinTarget：门控 0，过渡参考、上一拍的终点都贴着它
        var last = sh.Core.ExportState();
        Assert.Equal(0.0, last.GSp);
        Assert.Equal((40.8, 40.8), (last.Ref, last.SpfPrev));
    }

    // ── 真机的节拍不齐：挂接层喂出来的要跟参考喂法（每拍一步、dt 原样）是同一个数 ──

    [Theory]
    [InlineData("20261109-095506", 408, 5.3)]   // 19.3 → 50 ℃：1869 拍里 408 拍离上一拍超过 2.5 s
    [InlineData("20261103-104547", 200, 4.9)]   // 19 → 15 ℃：905 拍里 200 拍
    [InlineData("20261106-130619", 452, 5.6)]   // 66 → 70 ℃ + 长保温：1877 拍里 452 拍
    public void 真机节拍下_挂接层喂出来的跟参考喂法逐拍一致(string name, int slowBeats, double longest)
    {
        // 对拍夹具的期望值是 Python 定稿「每拍一步、dt 用真隔了多久」喂出来的（golden.py），试算和真机重放的成绩也都是这么喂的。
        // 真机上回路周期一抖，外环拍就落到 2.5–5 s（这三场里占 2 成多）。挂接层要是把这种拍拆成「前面的小步不带读数」，
        // 第一路回归（有读数才跑）就只走了最后一小步那么长的时间，τ̂ 一路偏小——所以这里按夹具的时刻一拍一拍地叫 Tick，
        // 核它交给核心类的就是「一拍一步、dt 原样」，算出来的数跟夹具逐拍对得上（容差同 LiveOuterGoldenTests）。
        var table = LiveOuterFixtures.Load(LiveOuterFixtures.RealPath(name));
        (string Col, double Tol, Func<LiveOuter, double> Get)[] cols =
        [
            ("wall", 1e-9, c => c.Wall), ("xr", 1e-9, c => c.THat), ("b", 1e-9, c => c.BHat), ("tau", 1e-6, c => c.Tau),
            ("tau_ekf", 1e-6, c => c.TauEkf), ("k", 1e-9, c => c.K), ("g_sp", 1e-9, c => c.GSp), ("to_eff", 1e-6, c => c.ToEff),
        ];
        var idx = cols.Select(c => table.Col(c.Col)).ToArray();
        int cU = table.Col("u");
        var worst = new double[cols.Length + 1];
        string? firstBad = null;
        var sh = new Spy();
        int k = 0, slow = 0;
        double dtMax = 0;
        foreach (var beat in LiveOuterFixtures.Beats(table))
        {
            var i = beat.Input;
            var r = sh.Tick(At(i.T), 2, Ctl(i.Sp, i.Tr, i.Tj));
            Assert.NotNull(r);
            Assert.False(sh.Stopped);
            for (int j = 0; j < cols.Length; j++)
            {
                double want = LiveOuterFixtures.Num(beat.Cells[idx[j]]), got = cols[j].Get(sh.Core), diff = Math.Abs(got - want);
                if (diff > worst[j]) worst[j] = diff;
                if (!(diff <= cols[j].Tol)) firstBad ??= $"第 {k} 拍（t = {i.T} s，离上一拍 {i.Dt} s）列 {cols[j].Col}：参考 {want:R}，挂接层 {got:R}";
            }
            double wantU = Math.Clamp(LiveOuterFixtures.Num(beat.Cells[cU]), -50.0, 200.0), diffU = Math.Abs(r!.JacketSetpointC - wantU);
            if (diffU > worst[^1]) worst[^1] = diffU;
            if (!(diffU <= 1e-9)) firstBad ??= $"第 {k} 拍（t = {i.T} s，离上一拍 {i.Dt} s）夹套目标：参考 {wantU:R}，挂接层 {r.JacketSetpointC:R}";
            if (k > 0 && i.Dt > 2.5) slow++;
            if (k > 0) dtMax = Math.Max(dtMax, i.Dt);
            k++;
        }
        Assert.True(firstBad is null, $"{name}：{firstBad}；各列最大差 "
            + string.Join("  ", cols.Select((c, j) => $"{c.Col} {worst[j]:E2}")) + $"  夹套目标 {worst[^1]:E2}");
        // 这一场真有那么多慢拍（夹具被换成拍长齐的，这条测试就没有分量了）；每拍都是一步、dt 就是真隔了多久
        Assert.Equal(slowBeats, slow);
        Assert.Equal(longest, dtMax, 9);
        Assert.Equal(k, sh.Steps.Count);
        Assert.Single(sh.Seats);
        var beats = LiveOuterFixtures.Beats(table).ToArray();
        for (int n = 1; n < k; n++)
        {
            Assert.Equal(beats[n].Input.Dt, sh.Steps[n].In.Dt, 12);
            Assert.Equal(beats[n].Input.Tr, sh.Steps[n].In.Tr);
        }
    }

    // ── 不在控：喂的目标只是占位的（= 当时的釜内读数），它跟着读数变不算「目标变了」 ──

    /// <summary>把 τ̂（两路的）和对它的把握摆成「认过一阵以后」的样子：τ̂ 780（K 跟着它）、第二路 820、P₂₂ 3.3e-9（先验是 9.97e-8）。</summary>
    private static void SeatLearned(LiveOuter core) => core.ImportState(core.ExportState() with
    {
        Tau = 780, K = 0.75 * (780 + 170.0) * (780 + 170.0) / (4 * 780 * 170.0), Th = 1.0 / 820, TauE = 820, P22 = LearnedP22,
    });

    private const double LearnedP22 = 3.3e-9;
    private const double PriorP22 = 0.3 / 950.0 * (0.3 / 950.0);

    /// <summary>「终点一拍里变了 Δ」把对 τ 的把握放回先验多少（照改动方案 B 节 ⑤ 另写一遍）。</summary>
    private static double Reopened(double p22, double delta) => p22 + (PriorP22 - p22) * (delta * delta / (delta * delta + 5.0 * 5.0));

    [Theory]
    [InlineData(1.0)]
    [InlineData(5.0)]
    [InlineData(-20.0)]
    public void 不在控_釜内读数坏一个点或连坏两个_对tau的把握不放开_门控不开_tau不动(double bad)
    {
        // 不在控时目标 = 当时的釜内读数（原始值）。读数坏一个点：T̂ 那条路有 3 点中值挡着，但目标这条路没有——
        // 目标跳过去再跳回来，要是照「终点变了 Δ」算，对 τ 的把握连放两次（+5 K 一个坏点就放回先验的 3/4）。
        // 占位目标的变动不是谁下发了新目标：把握不放、门控不开、τ 两路不动。
        var sh = new Spy();
        sh.Tick(At(0), 2, Idle(60, 55));
        SeatLearned(sh.Core);
        double th = 1.0 / 820;
        for (var k = 1; k <= 200; k++)
        {
            double tr = 60 + (k == 50 || k is 120 or 121 ? bad : 0);   // 第 50 拍坏一个；第 120、121 拍连坏两个
            Assert.NotNull(sh.Tick(At(2.0 * k), 2, Idle(tr, 55)));
            var s = sh.Core.ExportState();
            Assert.True(s.P22 == LearnedP22, $"第 {k} 拍对 τ 的把握变成了 {s.P22:E3}（原来 {LearnedP22:E3}，先验 {PriorP22:E3}）");
            Assert.True(s.GSp == 0.0, $"第 {k} 拍门控 = {s.GSp}");
            Assert.Equal(780.0, s.Tau);
            Assert.Equal(th, s.Th);
            Assert.Equal(tr, sh.Steps[^1].In.Sp);                      // 喂的还是当拍的读数（主会话定的喂法没变）
            if (k < 121) Assert.Equal(60.0, s.Xr);                     // 单个坏点：3 点中值挡着，T̂ 一位不动
            // 连坏两个：第二个进了 T̂（核心类的 3 点中值只挡得住单个）——这条测试喂的坏点是真进得来的，跟 τ 有关的照样没动
            if (k == 121) Assert.True(Math.Abs(s.Xr - 60.0) > 1e-3, $"连坏两个点 T̂ 应当动：{s.Xr:R}");
        }
    }

    [Fact]
    public void 不在控_釜内自己走_门控为零_tau两路和把握逐位不动_下发目标那一拍照常放开()
    {
        // 方案 B 节：「不在控时目标取当时的釜内读数（所以 g = 0，不学 τ；下发目标那一拍终点变了多少 = 新目标 − 釜内）」。
        // 过渡参考是按 τ̂ ÷ K（缺省约 650 s）的一阶滞后跟目标的：目标跟着读数走，釜内自己以 v 漂时 d ≈ v × 650 s——
        // 停控后自然凉、空闲时加冷料，门控都是全开的，τ 两路在没人看见（不在控的拍不进记录）的时候被改。
        // 先在控走一段真过渡（τ 真值 1500，让两路都认动、相关都攒起来），过渡还没完就停控：釜内每分钟 −0.3 K 走 30 min，中间一拍掉 15 K。
        var sh = new Spy();
        double w = 43, tr = 40, tj = 43;
        var k = 0;
        for (; k <= 400; k++)
        {
            double sp = k < 20 ? 40 : 50;
            tj = k < 20 ? 43 : 62;
            w += (tj - w) * 2.0 / 170;
            tr += (w - tr - 3) * 2.0 / 1500;
            sh.Tick(At(2.0 * k), 2, Ctl(sp, Math.Round(tr, 2), tj));
        }
        var stop = sh.Core.ExportState();
        Assert.True(stop.Tau > 1000 && stop.GSp > 0.5 && stop.P02 != 0.0 && stop.P12 != 0.0,
            $"在控这段没让 τ̂ 动起来（τ̂ {stop.Tau}、门控 {stop.GSp}、P02 {stop.P02}、P12 {stop.P12}），这条测试没有分量");

        // 停控：从第一拍起，τ̂（两路的）和对它的把握逐位不动——同一次连接里下一份记录开头的 lo_tau / lo_tau2 就是这一份结尾的数
        tr = Math.Round(tr, 2);
        double bMoved = 0;
        for (var n = 1; n <= 900; n++, k++)
        {
            tr = Math.Round(tr - 0.01 - (n == 450 ? 15 : 0), 2);       // −0.3 K/min；第 450 拍加冷料
            tj -= 0.012;
            double b0 = sh.Core.BHat;
            var r = sh.Tick(At(2.0 * k), 2, Idle(tr, tj));
            var s = sh.Core.ExportState();
            Assert.True(s.GSp == 0.0, $"不在控第 {n} 拍门控 = {s.GSp}");
            Assert.True(s.Tau == stop.Tau, $"不在控第 {n} 拍 τ̂ 从 {stop.Tau:R} 变成了 {s.Tau:R}");
            Assert.True(s.Th == stop.Th, $"不在控第 {n} 拍第二路的 τ 从 {1 / stop.Th:R} 变成了 {1 / s.Th:R}");
            Assert.True(s.P22 == stop.P22, $"不在控第 {n} 拍对 τ 的把握从 {stop.P22:E6} 变成了 {s.P22:E6}");
            Assert.Equal(tr, s.Ref);                                   // 过渡参考、上一拍的终点都贴着占位目标
            Assert.Equal(tr, s.SpfPrev);
            Assert.Equal((stop.Tau, stop.TauE), (r!.Tau, r.TauEkf));   // 交给记录的 lo_tau / lo_tau2 也就不动
            bMoved = Math.Max(bMoved, Math.Abs(s.B - b0));
        }
        Assert.True(bMoved > 0.05, $"b̂ / T̂ 应当照常跟着读数修（b̂ 每拍最多动了 {bMoved}）");

        // 下发目标（釜内 + 10）：这一拍「终点变了」= 新目标 − 上一拍的釜内读数，把握照常放开，门控打开
        double target = tr + 10;
        sh.Tick(At(2.0 * k), 2, Ctl(target, tr, tj));
        var after = sh.Core.ExportState();
        Assert.True(after.GSp > 0.99, $"下发目标那一拍门控 = {after.GSp}");
        Assert.Equal(Reopened(stop.P22, target - tr), after.P22, 1e-20);
        Assert.True(after.P22 > 0.75 * PriorP22);
        Assert.Equal(stop.Tau, after.Tau);                             // 这一拍还没学（门控是上一拍的 0）；从下一拍起照常学
        for (var n = 1; n <= 60; n++) sh.Tick(At(2.0 * (k + n)), 2, Ctl(target, tr + 0.02 * n, tj + 0.2 * n));
        Assert.NotEqual(stop.Th, sh.Core.ExportState().Th);            // 再开控以后 τ 照常认得动
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(3.0)]
    [InlineData(5.0)]
    [InlineData(10.0)]
    public void 不在控时断档重摆_釜内在断档里漂了几K_对tau的把握原样留着_之后下发目标照常放开(double drift)
    {
        // 不在控喂的目标 = 釜内读数。断档 61 s 回来读数漂了 Δ：重摆那一拍要是拿「这一拍的占位目标 − 断档前的占位目标」当成终点变了 Δ，
        // 把握就按 Δ² ÷ (Δ² + 25) 放回先验（漂 10 K 放 8 成）——那是断档造出来的假象：同样漂 10 K，不断档一拍一拍地走过来基本不放。
        var sh = new Spy();
        sh.Tick(At(0), 2, Idle(40, 43));
        for (var k = 1; k <= 5; k++) sh.Tick(At(2 * k), 2, Idle(40 + 0.01 * k, 43));
        SeatLearned(sh.Core);

        double trSeat = 40.05 - drift, tjSeat = 43 - drift;
        Assert.NotNull(sh.Tick(At(10 + 61), 2, Idle(trSeat, tjSeat)));
        Assert.True(sh.Seats[^1].Reseat);
        Assert.True(sh.Steps[^1].First);
        Assert.Equal(trSeat, sh.Steps[^1].In.Sp);
        var s = sh.Core.ExportState();
        Assert.True(s.P22 == LearnedP22, $"重摆那一拍对 τ 的把握变成了 {s.P22:E3}（原来 {LearnedP22:E3}，先验 {PriorP22:E3}）");
        Assert.Equal(780.0, s.Tau);
        Assert.Equal(1.0 / 820, s.Th);
        Assert.Equal(820.0, s.TauE);
        Assert.Equal(0.0, s.GSp);
        Assert.Equal((trSeat, tjSeat, tjSeat - trSeat), (s.Xr, s.Wall, s.B));      // Ŵ / T̂ / b̂ 照常按读数重摆

        // 接着下发目标：按「新目标 − 重摆时的釜内」放开（下发目标那一拍放开还在）
        double target = trSeat + 4;
        sh.Tick(At(73), 2, Ctl(target, trSeat, tjSeat));
        Assert.Equal(Reopened(LearnedP22, target - trSeat), sh.Core.ExportState().P22, 1e-20);
    }

    [Fact]
    public void 在控时断档里目标真改了_重摆那一拍照常按改了多少放开()
    {
        // 上一条的另一面：釜内串级在控、断档里目标从 45 改成了 55（配方进了下一段、墙钟往前跳过去一截）——这是真的「终点变了」，
        // 重摆不许把它吞掉。目标没改时把握原样留着，见「断档超过六十秒…」那一条。
        var sh = new Spy();
        sh.Tick(At(0), 2, Ctl(45, 40, 46));
        for (var k = 1; k <= 5; k++) sh.Tick(At(2 * k), 2, Ctl(45, 40 + 0.02 * k, 46));
        SeatLearned(sh.Core);

        sh.Tick(At(10 + 61), 2, Ctl(55, 41, 47));
        Assert.True(sh.Seats[^1].Reseat);
        var s = sh.Core.ExportState();
        Assert.Equal(Reopened(LearnedP22, 55 - 45), s.P22, 1e-20);
        Assert.Equal(780.0, s.Tau);                                    // τ̂ 两路本身这一拍不动
        Assert.Equal(820.0, s.TauE);
    }

    [Fact]
    public void 墙钟往回跳_这一拍不算_从现在重新计拍_状态留着()
    {
        var sh = new Spy();
        sh.Tick(At(100), 2, Idle(40, 46));
        var r = sh.Tick(At(102), 2, Idle(40.1, 46.1));
        var before = sh.Core.ExportState();

        // 对时：墙钟倒回去一个小时
        Assert.Same(r, sh.Tick(At(102 - 3600), 2, Idle(40.2, 46.2)));
        Assert.Equal(2, sh.Steps.Count);
        Assert.Equal(before, sh.Core.ExportState());
        // 不用等墙钟追回来：从倒回去的那一刻起满 2 s 就是下一拍，dt = 2
        Assert.Same(r, sh.Tick(At(102 - 3600 + 1.5), 2, Idle(40.2, 46.2)));
        sh.Tick(At(102 - 3600 + 2), 2, Idle(40.2, 46.2));
        Assert.Equal(3, sh.Steps.Count);
        Assert.Equal(2.0, sh.Steps[2].In.Dt, 12);
        Assert.Equal(4.0, sh.Steps[2].In.T, 12);                       // 秒表不跟墙钟走：0（起算）+ 2 + 2
        Assert.False(sh.Stopped);
    }

    [Fact]
    public void 旧外环那一拍还没到_影子等它_到了同一拍算_它不走了影子自己走()
    {
        var sh = new Spy();
        sh.Tick(At(0), 2, Ctl(45, 40, 46));
        // 开控时两边的节拍错开了：影子 2.0 s 到点，旧外环要到 2.5 s 才到——影子等它
        sh.Tick(At(2.0), 2, Ctl(45, 40.1, 46.1, pending: true));
        Assert.Single(sh.Steps);
        sh.Tick(At(2.5), 2, Ctl(45, 40.1, 46.1, pending: false));
        Assert.Equal(2, sh.Steps.Count);
        Assert.Equal(2.5, sh.Steps[1].In.Dt, 12);
        // 之后两边同拍：旧外环 4.5 s 到，影子也正好满 2 s
        sh.Tick(At(4.0), 2, Ctl(45, 40.2, 46.2, pending: true));
        Assert.Equal(2, sh.Steps.Count);
        sh.Tick(At(4.5), 2, Ctl(45, 40.2, 46.2, pending: false));
        Assert.Equal(3, sh.Steps.Count);
        Assert.Equal(2.0, sh.Steps[2].In.Dt, 12);
        // 「等」不会让它早算：没满一个周期，旧外环到了影子也不算
        sh.Tick(At(5.0), 2, Ctl(45, 40.2, 46.2, pending: false));
        Assert.Equal(3, sh.Steps.Count);
    }

    [Fact]
    public void 核心类抛异常_这一路影子停掉_Tick不往外抛_之后不再调核心类()
    {
        // InvalidOperationException 是漏到回路里最糟的一种（会被当成串口死了、当拍重开串口）
        var sh = new Spy { Fail = n => n == 3 ? new InvalidOperationException("测试：核心类坏了") : null };
        Assert.NotNull(sh.Tick(At(0), 2, Idle(40, 46)));
        Assert.NotNull(sh.Tick(At(2), 2, Idle(40, 46)));
        Assert.False(sh.Stopped);
        Assert.Null(sh.StopReason);

        Assert.Null(sh.Tick(At(4), 2, Idle(40, 46)));                  // 第 3 步抛了：没漏出来
        Assert.True(sh.Stopped);
        Assert.Contains("InvalidOperationException", sh.StopReason);
        Assert.Contains("核心类坏了", sh.StopReason);
        Assert.Null(sh.Last);

        for (var t = 6.0; t < 200; t += 2) Assert.Null(sh.Tick(At(t), 2, Idle(40, 46)));
        Assert.Equal(3, sh.Steps.Count);                               // 停了就一直停着，再没调过核心类

        // 起算那一步就抛也一样
        var bad = new Spy { Fail = _ => new ArgumentException("测试") };
        Assert.Null(bad.Tick(At(0), 2, Idle(40, 46)));
        Assert.True(bad.Stopped);
        // 手动停：之后一律不算
        var manual = new Spy();
        manual.Tick(At(0), 2, Idle(40, 46));
        manual.Stop("测试停");
        manual.Stop("第二次不改原因");
        Assert.Equal("测试停", manual.StopReason);
        Assert.Null(manual.Tick(At(2), 2, Idle(40, 46)));
        Assert.Single(manual.Steps);
    }

    [Fact]
    public void 内部量出现NaN或无穷_这一路影子停掉()
    {
        foreach (var poison in new[] { double.NaN, double.PositiveInfinity })
        {
            var sh = new Spy();
            sh.Tick(At(0), 2, Idle(40, 46));
            Assert.NotNull(sh.Tick(At(2), 2, Idle(40, 46)));
            sh.Core.ImportState(sh.Core.ExportState() with { B = poison });
            Assert.Null(sh.Tick(At(4), 2, Idle(40, 46)));              // 这一拍照算了，算完一看不是有限数：停
            Assert.Equal(3, sh.Steps.Count);
            Assert.True(sh.Stopped);
            Assert.Contains("NaN", sh.StopReason);
            Assert.Null(sh.Tick(At(6), 2, Idle(40, 46)));
            Assert.Equal(3, sh.Steps.Count);
        }
    }

    [Fact]
    public void 报警要的四个事实原样交给核心类_四个都成立才报_回路现在给的不在TEC侧就不报()
    {
        // 釜内每 2 s 涨 0.05 K（放热），夹套一直比釜内冷 20 K。四个事实都成立时：顶满 60 s 以后报警条件成立。
        static (bool EverOn, Spy Sh) Run(bool tecSide, bool satLow = true, bool withheld = false, bool on = true)
        {
            var sh = new Spy();
            var ever = false;
            for (var k = 0; k <= 60; k++)
            {
                var r = sh.Tick(At(2.0 * k), 2, new LiveOuterFacts
                {
                    Tr = 30 + 0.05 * k, Tj = 10, ReactorInControl = true, SetpointC = 30,
                    TecSide = tecSide, InnerSatLow = satLow, TecWithheld = withheld, OutputOn = on,
                });
                ever |= r!.AlarmOn;
            }
            return (ever, sh);
        }

        var (full, shFull) = Run(tecSide: true);
        Assert.True(full);
        Assert.All(shFull.Steps, s => Assert.True(s.In is { TecSide: true, InnerSatLow: true, TecWithheld: false, OutputOn: true }));
        Assert.True(shFull.Last!.AlarmOn);                             // 读数里看得见（记录列 lo_alarm）

        Assert.False(Run(tecSide: false).EverOn);                      // 第 0 步回路喂的就是这个：不在 TEC 侧 → 恒不报
        Assert.False(Run(tecSide: true, satLow: false).EverOn);
        Assert.False(Run(tecSide: true, withheld: true).EverOn);
        Assert.False(Run(tecSide: true, on: false).EverOn);
    }
}
