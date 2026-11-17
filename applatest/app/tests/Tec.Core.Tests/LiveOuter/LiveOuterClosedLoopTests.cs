using System.Globalization;
using TecControl.Core.Control;
using Xunit;
using Xunit.Abstractions;

namespace Tec.Core.Tests;

/// <summary>
/// 【本地改动】（2026-11-16，外环改「按实测实时算」第 1 步）新外环「能控」的最小闭环证据（D1）：不走回路、不走墙钟，按现场秒步进，毫秒级跑完，确定。
/// 拼起来的是：新外环核心类 <see cref="LiveOuter"/>（出厂缺省，2 s 一拍） + 产品的 <see cref="PidController"/>（内环，0.5 s 一拍，11-04 现场双向挡那组
/// Kp 9.4 / Ki 0.037 / Kd 175、微分滤波 8.6 s） + 双向挡映射（正半轴给加热棒、负半轴乘加热比 3.5 给 TEC，下限 −90 ÷ 3.5，同 HostControlLoop） +
/// 会话的符号继电器（PID 输出 ±1 % 死区不表态、同号连续 3 s 才扳、扳的那 1 s 输出关、内环挂起） + 一个釜。
///
/// 釜有多简化（照实说）：
/// · 釜内 / 釜壁：11-09 那场逐拍拟合（OuterMeasuredBiasTwinTests 同一组）——dTr/dt = (Tw − Tr − b(Tr)) / 1005，b(Tr) = 5.05 + 0.087 ×（Tr − 50）；
///   dTw/dt = (Tj(t − 42 s) − Tw) / 129。
/// · 夹套：HeatBoostTwinTests.Plant 那一组方程（11-04 −2 → 20 ℃ 加热棒段拟合）原样搬成现场秒：加热棒过 6.5 s 纯滞后 + 23.7 s + 4.0 s 两级、TEC 过 41 s，
///   dTj/dt = 0.002815·加热棒% − 0.00152·TEC% + 0.0061 + (S − Tj)/429.7 + (Tr − Tj)/816.8，dS/dt = (Tj − S)/150.7。它是低温段拟的，搬到 42–60 ℃
///   是外推：没有对环境的散热（保温 60 ℃ 只要约 0.4 % 加热棒，真机约 5 %）、没有冷却水 / 水冷块、TEC 增益不随温度变、加热比是常数。
/// · 读数：釜内 0.01 K 量化、没有锯齿和噪声（「干净读数」）；夹套原值。冷却水一直有（TEC 一接上就能冷）、冷水机没有最短间隔、没有问水。
/// · 会话只有符号继电器（新模式下会话剩下的就是它，主会话 D1）；没有回路的墙钟、没有跑飞保护、没有 E3。
/// 所以它证明的是：C# 的新外环 + C# 的内环 PID 在一个跟真釜同量级的对象上闭得上环、到站不冲、保温稳、不来回扳继电器；
/// 证明不了回路接对了（那是 LiveOuterInControlTests 的事）、也不能当「真机能过」（试算台到站偏乐观，bench/README「限度」）。
/// 判：冲过（30 s 尾随平均）≤ 0.06 K；稳住（首次连续 120 s 在 ±0.1）以后 30 min 不出带；那 30 min 里继电器翻转 ≤ 1 次。时间类（首进 / 稳住时刻）只报不判。
/// 【本地改动】（2026-11-17，S1「读数对不上就停控」）这个闭环上还挂 S1 判据（<see cref="ReadingCheck"/>，每个外环拍喂一次，判到就关内环输出）：
/// 它跟定稿的 Python twin（det/twin.py）逐位相同（「D1闭环跟Python孪生逐拍对得上」），所以定稿那几个数（冻在 30 / 55 / 70、提出液面、插错工位、读数都对的几种）直接拿来当期望。
/// 口径同试算台 metrics.py：30 s 尾随平均、带内按 0.01 取整比、|e| = 0.10 算带内。
/// </summary>
public class LiveOuterClosedLoopTests(ITestOutputHelper output)
{
    /// <summary>釜 + 夹套（现场秒）。</summary>
    private sealed class Vessel
    {
        private const double Tau1 = 1005, WallLag = 129, WallDelay = 42, B50 = 5.05, BPerK = 0.087;
        private double _tj, _tr, _tw, _s, _h1, _h2, _cf, _t;
        private readonly Queue<(double T, double H)> _hq = new();
        private readonly Queue<(double T, double Tj)> _wq = new();

        public Vessel(double tr, double tj, double tw)
        {
            _tr = tr; _tj = tj; _tw = tw; _s = tj;
        }

        public double Tr => _tr;
        public double Tj => _tj;

        /// <summary>【本地改动】（2026-11-17，S1）真釜内直接加 dTr（放热 / 吸热按速率 × 步长、加冷料一下子掉一截）：同 Python twin 测斜坡时在 Vessel.step 之后改 tr。</summary>
        public void Disturb(double dTr) => _tr += dTr;
        public static double Bias(double tr) => B50 + BPerK * (tr - 50);

        /// <summary>走 h 秒：heaterPct 加热棒占空比（≥ 0）、tecPct TEC 制冷占空比（≥ 0）。</summary>
        public void Step(double h, double heaterPct, double tecPct)
        {
            _t += h;
            _hq.Enqueue((_t, heaterPct));
            var h0 = _hq.Peek().H;
            while (_hq.Count > 1 && _t - _hq.Peek().T > 6.5) h0 = _hq.Dequeue().H;
            _h1 += (h0 - _h1) * h / 23.67;
            _h2 += (_h1 - _h2) * h / 4.03;
            _cf += (tecPct - _cf) * h / 41.0;
            var dTj = 0.002815 * _h2 - 0.00152 * _cf + 0.0061 + (_s - _tj) / 429.7 + (_tr - _tj) / 816.8;
            _s += (_tj - _s) / 150.7 * h;
            _tj += dTj * h;
            var dTr = (_tw - _tr - Bias(_tr)) / Tau1;
            _wq.Enqueue((_t, _tj));
            var tjd = _wq.Peek().Tj;
            while (_wq.Count > 1 && _t - _wq.Peek().T > WallDelay) tjd = _wq.Dequeue().Tj;
            _tw += (tjd - _tw) / WallLag * h;
            _tr += dTr * h;
        }
    }

    private sealed record Run(double[] T, double[] Tr, int[] Side, double[] Isp, double[] Tj, LiveOuter Core, int Flips);

    /// <summary>
    /// 【本地改动】（2026-11-17，S1「读数对不上就停控」）挂在闭环上的 S1 判据：每个外环拍（新外环算完那一拍）喂一次——t = 拍时刻（现场秒）、
    /// tr = 外环这一拍看到的读数、sp、Ŵ = core.Wall、τ̂ = core.Tau，同 Python twin（det/twin.py + Guard）喂 Tap 的那几样。
    /// <see cref="StopOnTrip"/> = true 时第一次 stop 那一拍起把内环输出关掉（同 twin.py 的 stop_on_trip：加热棒、TEC 都 0，内环不再算，继电器不再扳）。
    /// 逐拍的输入和结果都记下来（核跟 Python 夹具对得上 / 报出来的数）。
    /// </summary>
    private sealed class S1Tap(bool stopOnTrip)
    {
        public readonly ReadingCheck Chk = new(null, 30);
        public readonly bool StopOnTrip = stopOnTrip;
        public readonly List<(double T, double Tr, double Wall, double Tau, ReadingCheckBeat B)> Beats = new();
        public double StopAt = double.NaN, FirstAlarmAt = double.NaN;
        public string StopWhich = "";
        public ReadingCheckBeat StopBeat;
        public double MaxRa, MinRa, MaxD;
        public int AlarmEdges;
        /// <summary>【本地改动】（2026-11-17 复核后）「读数没跟着夹套降」报警撤了几次、最后一次撤在哪一拍（现场秒）；跑完时报警还亮不亮。
        /// 报警的起 / 撤照回路的规矩算：用回路同一个锁存（<see cref="ReadingAlarmLatch"/>，HostControlLoop.ReadingCheckTick 每个喂了判据的拍调一次）。</summary>
        public int AlarmClears;
        public double LastClearAt = double.NaN;
        public bool AlarmOn => _latch.On;
        private readonly ReadingAlarmLatch _latch = new();

        public void Note(double t, double tr, double wall, double tau, ReadingCheckBeat b)
        {
            Beats.Add((t, tr, wall, tau, b));
            if (b.Valid)
            {
                MaxRa = Math.Max(MaxRa, b.Ra);
                MinRa = Math.Min(MinRa, b.Ra);
                MaxD = Math.Max(MaxD, b.D);
            }
            if (b.Verdict != ReadingVerdict.Stop)                     // 回路：判到停控那一拍直接去停，不走报警那一段
            {
                switch (_latch.Step(b))
                {
                    case ReadingAlarmEdge.Raised: AlarmEdges++; break;
                    case ReadingAlarmEdge.Cleared: AlarmClears++; LastClearAt = t; break;
                }
            }
            if (b.Verdict == ReadingVerdict.Alarm && double.IsNaN(FirstAlarmAt)) FirstAlarmAt = t;
            if (b.Verdict == ReadingVerdict.Stop && double.IsNaN(StopAt))
            {
                StopAt = t;
                StopWhich = b.Which;
                StopBeat = b;
            }
        }
    }

    /// <summary>从 (釜内, 夹套, 釜壁) 起、目标 sp，跑 minutes 分钟。</summary>
    /// <param name="readTr">【本地改动】（第 1 步复核）釜内读数怎么来：(t 秒, 真釜内 0.01 取整) → 喂给新外环的读数；null = 真值（D1 原样）。</param>
    /// <param name="runaway">【本地改动】给了就每个内环拍按回路的规矩喂分方向跑飞判据（夹套目标、夹套、加热顶满 +1 / 制冷顶满 −1 / 不饱和 0），判到的时刻记进 <paramref name="tripsAt"/>（只记，不停）。</param>
    /// <param name="s1">【本地改动】（2026-11-17，S1）给了就每个外环拍喂 S1 判据；它的 StopOnTrip 为 true 时判到就关内环输出（见 <see cref="S1Tap"/>）。</param>
    /// <param name="disturb">【本地改动】（2026-11-17，S1）每个内环拍对象走完以后给真釜内加的扰动：(t 秒, 步长 h) → 釜内这一步多变多少 K（放热正、吸热 / 加冷料负）；null = 没有。</param>
    private static Run Simulate(double tr0, double tj0, double tw0, double sp, double minutes,
        Func<double, double, double>? readTr = null, RunawayDetector? runaway = null, List<double>? tripsAt = null,
        S1Tap? s1 = null, Func<double, double, double>? disturb = null)
    {
        var at0 = new DateTime(2026, 11, 16, 9, 0, 0, DateTimeKind.Local);
        runaway?.Start(at0);
        const double inner = 0.5, outerEvery = 4, ratio = 3.5, max = 90;
        var floor = -max / ratio;
        var plant = new Vessel(tr0, tj0, tw0);
        var pid = new PidController();
        pid.Configure(9.4, 0.037, 175, floor, max, 8.6);
        var core = new LiveOuter();
        int side = +1, pendSign = 0, flips = 0;           // +1 加热棒、−1 TEC
        double pend = 0, switchUntil = -1, lastPid = 0, isp = tj0;
        var resumeReset = false;
        var stopped = false;                              // 【本地改动】（S1）判到停控以后内环不再出力
        var n = (int)(minutes * 60 / inner);
        var ts = new double[n];
        var trs = new double[n];
        var sides = new int[n];
        var isps = new double[n];
        var tjs = new double[n];
        for (var k = 0; k < n; k++)
        {
            var t = k * inner;
            var switching = t < switchUntil;
            var trRead = readTr?.Invoke(t, Math.Round(plant.Tr, 2)) ?? Math.Round(plant.Tr, 2);
            if (k % outerEvery == 0)
            {
                // 外环一拍：喂的是这一拍之前的事实（上一拍的内环输出、继电器此刻在哪），同回路喂影子
                var o = core.Step(new LiveOuterInput
                {
                    T = t, Dt = inner * outerEvery, Sp = sp, Tr = trRead, Tj = plant.Tj,
                    TecSide = side < 0 && !switching,
                    InnerSatLow = lastPid <= floor + 1e-6,
                    TecWithheld = false,
                    OutputOn = !switching && !stopped,
                });
                isp = Math.Clamp(o.JacketSetpointC, -50, 200);
                if (s1 is not null)
                {
                    // 【本地改动】（S1）同 Python twin：新外环这一拍算完，判据看这一拍的读数、目标、Ŵ、τ̂
                    var b = s1.Chk.Step(t, trRead, sp, core.Wall, core.Tau);
                    s1.Note(t, trRead, core.Wall, core.Tau, b);
                    if (s1.StopOnTrip && b.Verdict == ReadingVerdict.Stop) stopped = true;
                }
            }
            double heat = 0, tec = 0;
            if (!switching && !stopped)
            {
                if (resumeReset) { pid.ResetDerivative(); resumeReset = false; }
                var u = pid.Update(isp, plant.Tj, inner);
                lastPid = u;
                if (runaway is not null)
                {
                    // 回路（双向挡 HeaterTec）：贴上限 = 加热顶满、贴下限 = 制冷顶满；同 HostControlLoop.LiveRunaway 的 dir
                    var dir = u >= max - 1e-6 ? 1 : u <= floor + 1e-6 ? -1 : 0;
                    if (runaway.CheckDirectional(at0.AddSeconds(t), isp, plant.Tj, dir)) tripsAt?.Add(t);
                }
                // 双向挡映射：正半轴原样给加热棒、负半轴乘加热比给 TEC；继电器在哪一侧只有那一侧出得来
                if (side > 0) heat = Math.Max(0, u);
                else tec = u < 0 ? Math.Min(max, -u * ratio) : 0;
                // 符号继电器：±1 % 死区不表态（计时清）；同号连续 3 s 才扳，扳的那 1 s 输出关、内环挂起
                var sign = u > 1 ? 1 : u < -1 ? -1 : 0;
                if (sign == 0 || sign == side) { pendSign = 0; pend = 0; }
                else
                {
                    if (pendSign != sign) { pendSign = sign; pend = 0; }
                    pend += inner;
                    if (pend >= 3.0 - 1e-9)
                    {
                        side = sign;
                        flips++;
                        pendSign = 0;
                        pend = 0;
                        switchUntil = t + inner + 1.0;
                        resumeReset = true;
                        heat = tec = 0;
                    }
                }
            }
            plant.Step(inner, heat, tec);
            if (disturb is not null) plant.Disturb(disturb(t + inner, inner));
            ts[k] = t + inner;
            trs[k] = Math.Round(plant.Tr, 2);
            sides[k] = side;
            isps[k] = isp;
            tjs[k] = plant.Tj;
        }
        return new Run(ts, trs, sides, isps, tjs, core, flips);
    }

    private sealed record Metrics(double OverAvg, double OverRaw, double FirstIn, double Settle, int Exc, double Worst, int HoldFlips, double Observed);

    /// <summary>试算台 metrics.py 的口径（升温台阶）。</summary>
    private static Metrics Measure(Run r, double sp)
    {
        var n = r.T.Length;
        var avg = new double[n];
        double sum = 0;
        var a = 0;
        for (var i = 0; i < n; i++)
        {
            sum += r.Tr[i];
            while (r.T[a] <= r.T[i] - 30) sum -= r.Tr[a++];
            avg[i] = sum / (i - a + 1);
        }
        double crossAvg = double.NaN, overAvg = double.NaN, crossRaw = double.NaN, overRaw = double.NaN;
        for (var i = 0; i < n; i++)
        {
            var ea = avg[i] - sp;
            var er = r.Tr[i] - sp;
            if (double.IsNaN(crossAvg) && ea >= -1e-9) crossAvg = r.T[i];
            if (double.IsNaN(crossRaw) && er >= -1e-9) crossRaw = r.T[i];
            if (!double.IsNaN(crossAvg)) overAvg = double.IsNaN(overAvg) ? ea : Math.Max(overAvg, ea);
            if (!double.IsNaN(crossRaw)) overRaw = double.IsNaN(overRaw) ? er : Math.Max(overRaw, er);
        }
        bool In(int i) => Math.Abs(Math.Round(avg[i] - sp, 2)) <= 0.10 + 1e-9;
        double firstIn = double.NaN, settle = double.NaN, runStart = double.NaN;
        for (var i = 0; i < n; i++)
        {
            if (!In(i)) { runStart = double.NaN; continue; }
            if (double.IsNaN(firstIn)) firstIn = r.T[i];
            if (double.IsNaN(runStart)) runStart = r.T[i];
            if (r.T[i] - runStart >= 120) { settle = runStart; break; }
        }
        int exc = 0, flips = 0, lastSide = 0;
        double worst = 0;
        var prevIn = true;
        for (var i = 0; i < n && !double.IsNaN(settle); i++)
        {
            if (r.T[i] < settle) continue;
            if (r.T[i] > settle + 1800) break;
            if (!In(i))
            {
                if (prevIn) exc++;
                worst = Math.Max(worst, Math.Abs(avg[i] - sp));
            }
            prevIn = In(i);
            if (lastSide != 0 && r.Side[i] != lastSide) flips++;
            lastSide = r.Side[i];
        }
        var observed = double.IsNaN(settle) ? double.NaN : Math.Min(1800, r.T[^1] - settle);
        return new Metrics(overAvg, overRaw, firstIn, settle, exc, worst, flips, observed);
    }

    [Theory]
    [InlineData("稳在42起到60", 42.0, 0, 0)]
    [InlineData("11-16开控态到60", 43.74, 35.14, 35.14)]
    public void D1_新外环加产品内环PID加符号继电器_一阶釜带釜壁滞后_到60不冲过0点06_稳住后30分钟不出带_保温继电器不来回扳(
        string name, double tr0, double tj0, double tw0)
    {
        if (tj0 == 0)
        {
            // 稳在 42：釜内 42、釜壁 = 夹套 = 42 + b(42)
            tj0 = tw0 = tr0 + Vessel.Bias(tr0);
        }
        const double sp = 60;
        var s1 = new S1Tap(stopOnTrip: true);                       // 【本地改动】（2026-11-17，S1）判据挂着（判到就停的接法）：读数都对的正常场一次不许停、不许报
        var run = Simulate(tr0, tj0, tw0, sp, minutes: 160, s1: s1);
        var m = Measure(run, sp);
        var core = run.Core;
        var inv = CultureInfo.InvariantCulture;
        output.WriteLine(string.Create(inv,
            $"{name}：釜内 {tr0} / 夹套 {tj0:F2} → {sp}；冲过 30 s 平均 {m.OverAvg:F3} K、原始 {m.OverRaw:F2} K；" +
            $"首进 ±0.1 {m.FirstIn / 60:F1} min、稳住 {m.Settle / 60:F1} min（只报）；稳住后 {m.Observed / 60:F0} min 出带 {m.Exc} 次（最远 {m.Worst:F3}）、" +
            $"继电器翻 {m.HoldFlips} 次；全程翻 {run.Flips} 次；夹套目标最高 {run.Isp.Max():F2}、夹套最高 {run.Tj.Max():F2}；" +
            $"末了 b̂ {core.BHat:F3}（实测 夹套 − 釜内 {run.Tj[^1] - run.Tr[^1]:F3}，对象真值 b(60) {Vessel.Bias(60):F3}）、τ̂ {core.Tau:F0} s、τ₂ {core.TauEkf:F0} s"));

        Assert.False(double.IsNaN(m.Settle), "160 min 里没稳住");
        Assert.True(m.Observed >= 1800 - 1e-6, $"稳住以后只看了 {m.Observed / 60:F1} min");
        Assert.True(m.OverAvg <= 0.06 + 1e-9, $"冲过 {m.OverAvg:F3} K（30 s 平均）超过 0.06");
        Assert.Equal(0, m.Exc);
        Assert.True(m.HoldFlips <= 1, $"保温 30 min 里继电器翻了 {m.HoldFlips} 次");
        Assert.True(core.IsFinite);
        output.WriteLine(string.Create(inv, $"S1：R_A 最大 {s1.MaxRa:+0.000;-0.000} / 最小 {s1.MinRa:+0.000;-0.000}（门槛 ±0.25）、D 最大 {s1.MaxD:F2}（门槛 8）"));
        Assert.True(double.IsNaN(s1.StopAt), $"S1 在正常场停了：t = {s1.StopAt} s，{s1.StopWhich}");
        Assert.True(double.IsNaN(s1.FirstAlarmAt), $"S1 在正常场报警了：t = {s1.FirstAlarmAt} s");
    }

    /// <summary>
    /// 【本地改动】（第 1 步复核时是「刻画现状」；2026-11-17 加了 S1「读数对不上就停控」以后翻成这条）釜内探头「有效但读错」：质量好、数却跟本釜脱钩
    /// （提出液面 / 插错工位 / 读数冻结）。新外环 u = 目标 + b̂ + G(目标 − T̂)，b̂ 纯积分、不设界：观测器收敛到 T̂ = 读数、b̂ = Ŵ − 读数，
    /// 读数低于目标夹套就一路追到 200、高于目标一路压到 −50（复核实跑：不保护 60 min 后冻在 30 → 真釜内 177.81、55 → 109.66、70 → −26.60），
    /// 分方向跑飞判据看的是「夹套越来越远离夹套目标」，夹套追着目标走，整段不判。
    /// 现在挂了 S1（判到就把内环输出关掉，同 Python twin 的 stop_on_trip；回路里是 TripChannelAsync 停控）——期望值是定稿 Python twin 逐行移植的数（定稿 README §3.3）：
    /// · 冻在 30（目标下 30 K）：t = 652 s（冻住后 0.87 min）B 判到；那一刻真釜内 59.99、停后最高 63.64（断言 ≤ 64）；
    /// · 冻在 55（目标下 5 K）：t = 902 s（5.03 min）A 判到；那一刻 61.66、停后最高 71.47（断言 ≤ 72）；
    /// · 冻在 70（目标上 10 K，制冷方向）：**整段不停**（按「放热只报不停」：越来越猛的放热被控住时从温度上一模一样），t = 946 s（5.77 min）第一次报警，
    ///   60 min 后真釜内照旧 −26.60——只报警、不保护，要人去看。
    /// 判到时刻允许 ±1 拍（2 s）。
    /// </summary>
    [Theory]
    [InlineData(30.0, 652.0, "B", 59.99, 64.0)]
    [InlineData(55.0, 902.0, "A", 61.66, 72.0)]
    [InlineData(70.0, double.NaN, "", double.NaN, double.NaN)]
    public void 新外环在控时釜内读数冻住_加热方向S1停控_制冷方向只报警_跑飞判据整段不判(
        double frozen, double stopAtS, string which, double trAtStop, double maxAfter)
    {
        const double sp = 60, freezeAt = 600;
        var tj0 = sp + Vessel.Bias(sp);
        var det = new RunawayDetector();
        var trips = new List<double>();
        var s1 = new S1Tap(stopOnTrip: true);
        var run = Simulate(sp, tj0, tj0, sp, minutes: 10 + 60, (t, real) => t < freezeAt ? real : frozen, det, trips, s1);
        // 对照：同一场不挂保护（判据只记不停）
        var free = Simulate(sp, tj0, tj0, sp, minutes: 10 + 60, (t, real) => t < freezeAt ? real : frozen, s1: new S1Tap(stopOnTrip: false));
        var inv = CultureInfo.InvariantCulture;
        var i = double.IsNaN(s1.StopAt) ? -1 : (int)(s1.StopAt / 0.5);
        output.WriteLine(string.Create(inv,
            $"读数冻在 {frozen}：S1 {(i < 0 ? "不停" : $"t = {s1.StopAt} s（冻住后 {(s1.StopAt - freezeAt) / 60:F2} min）判到 {s1.StopWhich}，那一刻真釜内 {run.Tr[i]:F2}、夹套 {run.Tj[i]:F2}，停后真釜内最高 {run.Tr[i..].Max():F2}")}；" +
            $"第一次报警 {(double.IsNaN(s1.FirstAlarmAt) ? "无" : $"t = {s1.FirstAlarmAt} s（冻住后 {(s1.FirstAlarmAt - freezeAt) / 60:F2} min）")}；" +
            $"R_A 最大 {s1.MaxRa:F3} / 最小 {s1.MinRa:F3}、D 最大 {s1.MaxD:F2}；60 min 后真釜内 {run.Tr[^1]:F2}（不保护 {free.Tr[^1]:F2}）；跑飞判到 {trips.Count} 次"));

        Assert.Empty(trips);                                            // 分方向跑飞判据整段不判（S1 才是拦这种故障的）
        if (!double.IsNaN(stopAtS))
        {
            Assert.InRange(s1.StopAt, stopAtS - 2, stopAtS + 2);
            Assert.Equal(which, s1.StopWhich);
            Assert.InRange(run.Tr[i], trAtStop - 0.02, trAtStop + 0.02);
            Assert.True(run.Tr[i..].Max() <= maxAfter, $"停后真釜内最高 {run.Tr[i..].Max():F2} 超过 {maxAfter}");
            // 停后这个釜没有对环境的散热（类头「釜有多简化」），夹套里存的热放完就停在那儿（60 min 后 62.85 / 70.53）；不保护的话 177.81 / 109.66 还在涨
            Assert.True(free.Tr[^1] > sp + 40, $"对照（不保护）60 min 后真釜内才 {free.Tr[^1]:F2}");
            Assert.True(run.Tr[^1] < free.Tr[^1] - 30, $"挂了 S1 60 min 后真釜内 {run.Tr[^1]:F2}，不保护 {free.Tr[^1]:F2}");
        }
        else
        {
            Assert.True(double.IsNaN(s1.StopAt), $"制冷方向不该停：t = {s1.StopAt} s 判到 {s1.StopWhich}");
            Assert.InRange(s1.FirstAlarmAt, 946 - 2, 946 + 2);
            Assert.Equal(1, s1.AlarmEdges);                             // 报出来以后一直报着（读数一直冻着）
            Assert.InRange(run.Tr[^1], -26.65, -26.55);                 // 只报警、不保护：照旧被冷下去
        }
    }

    /// <summary>
    /// 【本地改动】（2026-11-17，S1 复核查实）冻在 70（目标上 10 K、制冷方向只报警不停控——这条报警是这类故障唯一的保护）跑到冻住后 95 min：
    /// 新外环把夹套一路压到回路下限 −50 以后夹套不动了，A 只算最近 τ̂₀ = 950 s 里夹套推的量，窗滑过「往下推」那一段 R_A 就回到 −0.25 以内
    /// （Python twin：冻住后 71.8 min、R_A −0.248、Ŵ −50.13）——原来那一拍就当「条件没了」把报警撤了、还说「读数又跟着夹套动了」，读数其实一拍没动。
    /// 现在：报警锁住，只在读数真动了 0.02 K（30 s 平均离开报警时那个参考）才撤。这里断言：整段只起一次、一次没撤、跑完还亮着；
    /// 而且「原来撤报警的那个条件」确实出现过（夹套在 −50 停了 30 min 以上、最后 R_A 已回到门槛以内）——不是没跑到那儿。
    /// </summary>
    [Fact]
    public void 新外环在控时釜内读数冻在目标上面_夹套推到底以后报警一直报着_冻住后95分钟都不撤()
    {
        const double sp = 60, freezeAt = 600;
        var tj0 = sp + Vessel.Bias(sp);
        var s1 = new S1Tap(stopOnTrip: true);
        var run = Simulate(sp, tj0, tj0, sp, minutes: 10 + 95, (t, real) => t < freezeAt ? real : 70.0, s1: s1);
        var bottom = Array.FindIndex(run.Isp, x => x <= -50 + 1e-9);
        var last = s1.Beats[^1].B;
        // 「原来会撤的那一拍」：照撤之前的判法（这一拍有效、A 的制冷方向不成立）第一次出现在哪
        var wouldClear = s1.Beats.FindIndex(x => x.T > s1.FirstAlarmAt && x.B.Valid && !x.B.CoolingAlarm);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"冻在 70：第一次报警 t = {s1.FirstAlarmAt} s（冻住后 {(s1.FirstAlarmAt - freezeAt) / 60:F2} min）；夹套目标到 −50 在冻住后 {(run.T[bottom] - freezeAt) / 60:F1} min；" +
            $"A 的制冷方向不再成立（原来撤报警）在冻住后 {(wouldClear < 0 ? double.NaN : (s1.Beats[wouldClear].T - freezeAt) / 60):F1} min（R_A {(wouldClear < 0 ? double.NaN : s1.Beats[wouldClear].B.Ra):F3}、Ŵ {(wouldClear < 0 ? double.NaN : s1.Beats[wouldClear].Wall):F2}）；" +
            $"报警起 {s1.AlarmEdges} 次、撤 {s1.AlarmClears} 次{(double.IsNaN(s1.LastClearAt) ? "" : $"（最后一次冻住后 {(s1.LastClearAt - freezeAt) / 60:F1} min）")}、跑完 {(s1.AlarmOn ? "还亮着" : "灭了")}；" +
            $"最后一拍 R_A {last.Ra:F3}、30 s 平均 {last.Y30:F2}；95 min 后真釜内 {run.Tr[^1]:F2}"));

        Assert.True(double.IsNaN(s1.StopAt), $"制冷方向不该停：t = {s1.StopAt} s 判到 {s1.StopWhich}");
        Assert.InRange(s1.FirstAlarmAt, 946 - 2, 946 + 2);
        Assert.True(bottom > 0 && run.T[bottom] <= run.T[^1] - 1800, "夹套目标没在 −50 停够 30 min：这一场没跑到「夹套推到底不动」");
        Assert.True(wouldClear > 0, "A 的制冷方向一直成立：没跑到原来撤报警的那一拍，这条测不到东西");
        Assert.True(last.Valid && last.Ra > -ReadingCheck.R0, $"最后一拍 R_A {last.Ra:F3} 还在门槛外");
        Assert.True(last.Y30 == 70.0, "读数该一直冻在 70");
        Assert.Equal(1, s1.AlarmEdges);
        Assert.True(s1.AlarmClears == 0, $"读数一拍没动，报警却撤了 {s1.AlarmClears} 次（最后一次冻住后 {(s1.LastClearAt - freezeAt) / 60:F1} min）");
        Assert.True(s1.AlarmOn, "跑完时报警灭了");
    }

    /// <summary>
    /// 【本地改动】（2026-11-17，S1 复核查实）报警锁存（<see cref="ReadingAlarmLatch"/>，回路和上面那个闭环用的同一个）的规矩：
    /// A 的制冷方向成立 → 起；之后 A 不成立了（R_A 回到门槛以内）不撤；读数读不到的拍不动；判据参考重起而读数没离开报警时的参考 0.02 K（断档 / 重摆）不撤；
    /// 读数 30 s 平均离开报警时的参考 ≥ 0.02 K 才撤；撤了以后再成立再起（参考换成新的）。
    /// </summary>
    [Fact]
    public void 读数报警锁存_起了以后R_A回到门槛以内不撤_读不到不动_参考重起不撤_读数动了0点02才撤_撤了能再起()
    {
        static ReadingCheckBeat B(bool valid, double y30, double ra, double yRef, bool a)
            => new(ReadingVerdict.Ok, a ? "A" : "", valid, y30, ra, double.NaN, 0, yRef, false, 0, double.NaN, double.NaN, 0, 0, 0);
        var l = new ReadingAlarmLatch();
        Assert.Equal(ReadingAlarmEdge.None, l.Step(B(false, double.NaN, 0, double.NaN, false)));
        Assert.Equal(ReadingAlarmEdge.None, l.Step(B(true, 70, -0.2, 70, false)));
        Assert.False(l.On);
        Assert.Equal(ReadingAlarmEdge.Raised, l.Step(B(true, 70, -0.3, 70, true)));
        Assert.True(l.On);
        Assert.Equal(70.0, l.YRef);
        Assert.Equal(ReadingAlarmEdge.None, l.Step(B(true, 70, -0.31, 70, true)));      // 一直在：不重起
        Assert.Equal(ReadingAlarmEdge.None, l.Step(B(true, 70, -0.248, 70, false)));    // 夹套推到底、R_A 回到门槛以内：原来撤，现在不撤
        Assert.Equal(ReadingAlarmEdge.None, l.Step(B(true, 70, 0.0, 70, false)));
        Assert.Equal(ReadingAlarmEdge.None, l.Step(B(false, double.NaN, 0, 70, false))); // 读不到：不动
        Assert.Equal(ReadingAlarmEdge.None, l.Step(B(true, 70.01, 0.0, 70.01, false))); // 判据参考重起（断档 / 重摆）、读数离报警时的参考不到 0.02：不撤
        Assert.Equal(ReadingAlarmEdge.None, l.Step(B(true, 69.99, 0.0, 70.01, false)));
        Assert.True(l.On);
        Assert.Equal(ReadingAlarmEdge.Cleared, l.Step(B(true, 69.97, 0.0, 69.97, false))); // 读数动了 0.03 K：撤
        Assert.False(l.On);
        Assert.True(double.IsNaN(l.YRef));
        Assert.Equal(ReadingAlarmEdge.None, l.Step(B(true, 69.97, -0.1, 69.97, false)));
        Assert.Equal(ReadingAlarmEdge.Raised, l.Step(B(true, 69.97, -0.26, 69.97, true))); // 再成立：再起，参考是新的
        Assert.Equal(69.97, l.YRef);
    }

    /// <summary>
    /// 【本地改动】（2026-11-17，S1 复核查实：「读数都对时误停都在凉的那一边」不对）读数都对、计量泵连续打冷料（真釜内按 q K/min 往下掉、连打 8 min）：
    /// 判据不改（B 判的就是「加热中读数往下掉、而且不回」，从温度上分不开），这里把代价钉住、给说法和提示用——
    /// 停的那一刻夹套为了顶住加料已经被推得很热，停控后加热、制冷都断开，夹套存的热灌进釜里，釜内冲过目标，比不停还高。
    /// 期望值是 Python twin（det/twin.py，同这个闭环逐位）复核实跑的数（scratchpad ftrev/dose2.py、ftrev_verify/dose_cmp.py）：
    /// 60 ℃ −2：打料后 6.4 min 按 B 停，那一刻夹套 96.96、真釜内 51.08，停后 20 min 最高 68.48；60 ℃ −3：5.8 min、108.54、47.20、70.33；
    /// 40 ℃ −2：6.4 min、75.34、31.07、49.01；40 ℃ −3：5.8 min、87.01、27.21、50.87。停后到跑完最高比不停同一段高 +5.46 / +5.49 / +6.82 / +6.80 K。
    /// 判到时刻 ±1 拍（2 s），温度 ±0.05。
    /// </summary>
    [Theory]
    [InlineData(60.0, 2.0, 6.4, 96.96, 51.08, 68.48)]
    [InlineData(60.0, 3.0, 5.8, 108.54, 47.20, 70.33)]
    [InlineData(40.0, 2.0, 6.4, 75.34, 31.07, 49.01)]
    [InlineData(40.0, 3.0, 5.8, 87.01, 27.21, 50.87)]
    public void 读数都对时连续打冷料8分钟_S1按B误停_停时夹套很烫_停后釜内冲过目标而且比不停还高(
        double sp, double rate, double stopMin, double tjAtStop, double trAtStop, double max20)
    {
        const double doseAt = 600, doseS = 8 * 60;
        var tj0 = sp + Vessel.Bias(sp);
        double Dose(double t, double h) => t >= doseAt && t < doseAt + doseS ? -rate / 60.0 * h : 0;
        var s1 = new S1Tap(stopOnTrip: true);
        var run = Simulate(sp, tj0, tj0, sp, minutes: 70, s1: s1, disturb: Dose);
        var free = Simulate(sp, tj0, tj0, sp, minutes: 70, s1: new S1Tap(stopOnTrip: false), disturb: Dose);
        Assert.False(double.IsNaN(s1.StopAt), "连续打冷料没误停：这一场的前提变了");
        var i = (int)(s1.StopAt / 0.5);
        var j = Math.Min(run.Tr.Length, i + (int)(20 * 60 / 0.5));
        var peak20 = run.Tr[i..j].Max();
        var peakStop = run.Tr[i..].Max();
        var peakFree = free.Tr[i..].Max();
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"目标 {sp}、打冷料 {rate} K/min × 8 min：打料后 {(s1.StopAt - doseAt) / 60:F2} min 判到 {s1.StopWhich}（D {s1.StopBeat.D:F2} K、u {s1.StopBeat.U:F2} K），" +
            $"那一刻夹套 {run.Tj[i]:F2}、真釜内 {run.Tr[i]:F2}；停后 20 min 真釜内最高 {peak20:F2}（比目标 {peak20 - sp:+0.00;-0.00} K）；" +
            $"停后到跑完最高 {peakStop - sp:+0.00;-0.00} K，不停同一段 {peakFree - sp:+0.00;-0.00} K，差 {peakStop - peakFree:+0.00;-0.00} K"));
        Assert.Equal("B", s1.StopWhich);
        Assert.InRange(s1.StopAt - doseAt, stopMin * 60 - 6 - 2, stopMin * 60 + 6 + 2);   // 6.4 / 5.8 是打料后分钟数取一位：±3 s 取整 + ±1 拍
        Assert.InRange(run.Tj[i], tjAtStop - 0.05, tjAtStop + 0.05);
        Assert.InRange(run.Tr[i], trAtStop - 0.05, trAtStop + 0.05);
        Assert.InRange(peak20, max20 - 0.05, max20 + 0.05);
        Assert.True(peak20 > sp + 8, $"停后 20 min 真釜内最高 {peak20:F2}，没冲过目标 8 K");
        Assert.True(peakStop > peakFree + 5, $"停了 {peakStop:F2}、不停 {peakFree:F2}：停了没比不停高 5 K 以上");
    }

    /// <summary>
    /// 【本地改动】（2026-11-17，S1）C# 这个闭环跟 Python twin（det/twin.py，定稿的夹具就是它跑出来的）逐拍对得上：不挂保护（判据只记不停）跑同一场，
    /// 每个外环拍喂给判据的 (t, 读数, Ŵ, τ̂) 跟夹具 twin_* 那一场的输入列比、判据这一拍的 verdict / which 跟夹具比。
    /// 这样上面那条的期望值（Python twin 的数）才能直接拿来当 C# 的期望。
    /// </summary>
    [Theory]
    [InlineData("twin_frozen30", 30.0)]
    [InlineData("twin_frozen55", 55.0)]
    [InlineData("twin_frozen70", 70.0)]
    [InlineData("twin_ramp_m04", double.NaN)]
    public void D1闭环跟Python孪生逐拍对得上_喂给判据的读数和Ŵτ̂和判断都一样(string name, double frozen)
    {
        const double sp = 60, freezeAt = 600;
        var tj0 = sp + Vessel.Bias(sp);
        var tap = new S1Tap(stopOnTrip: false);
        var ramp = double.IsNaN(frozen);
        Simulate(sp, tj0, tj0, sp, minutes: ramp ? 310 : 70,
            ramp ? null : (t, real) => t < freezeAt ? real : frozen, s1: tap,
            disturb: ramp ? (t, h) => t >= 600 ? -0.4 * (t - 600) / 3600.0 / 60.0 * h : 0 : null);
        var tab = LiveOuterFixtures.Load(Path.Combine(LiveOuterFixtures.Dir, "golden", "s1", name + ".csv.gz"));
        Assert.Equal(tab.Rows.Length, tap.Beats.Count);
        int cT = tab.Col("t"), cTr = tab.Col("tr"), cW = tab.Col("wall"), cTau = tab.Col("tau"), cV = tab.Col("verdict"), cWh = tab.Col("which");
        double dW = 0, dTau = 0;
        var mismatch = 0;
        for (var k = 0; k < tab.Rows.Length; k++)
        {
            var f = tab.Rows[k];
            var (t, tr, wall, tau, b) = tap.Beats[k];
            Assert.Equal(LiveOuterFixtures.Num(f[cT]), t);
            Assert.True(LiveOuterFixtures.Num(f[cTr]) == tr, $"第 {k} 拍读数不一样：Python {f[cTr]}，C# {tr:R}");
            dW = Math.Max(dW, Math.Abs(LiveOuterFixtures.Num(f[cW]) - wall));
            dTau = Math.Max(dTau, Math.Abs(LiveOuterFixtures.Num(f[cTau]) - tau));
            var v = b.Verdict switch { ReadingVerdict.Stop => "stop", ReadingVerdict.Alarm => "alarm", _ => "ok" };
            if (v != f[cV] || b.Which != f[cWh]) mismatch++;
        }
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{name}：{tab.Rows.Length} 拍，Ŵ 最大差 {dW:E2}、τ̂ 最大差 {dTau:E2}、判断不一样 {mismatch} 拍"));
        Assert.True(dW <= 1e-6, $"Ŵ 最大差 {dW:E2}");
        Assert.True(dTau <= 1e-4, $"τ̂ 最大差 {dTau:E2}");
        Assert.Equal(0, mismatch);
    }

    /// <summary>
    /// 【本地改动】（2026-11-17，S1）探头被提出液面：读数从那一刻起按一阶滞后 τp 往室温 25 ℃ 走（同定稿试算的 Lift 故障），60 ℃ 保温 10 min 后出事。
    /// 判到就停（B：加热中读数往下掉、而且不回）；定稿试算台 60 / 40 ℃ 保温、τp 60 / 180 / 600 s：5.0–5.6 min（40 ℃ τp 600：7.9 min），
    /// 停后再冲高 12–23 K（定稿 §7「判得慢的」第 1 条：慢下来是为了加冷料时不误停）。这里断言：判到、是 B、提出后 ≤ 8 min、停后真釜内最高不超过目标 + 23.5 K。
    /// </summary>
    [Theory]
    [InlineData(60.0)]
    [InlineData(180.0)]
    [InlineData(600.0)]
    public void 新外环在控时探头提出液面_读数往室温走_S1按B停控_用时和停后冲高在定稿的数以内(double tauP)
    {
        const double sp = 60, liftAt = 600, amb = 25;
        var tj0 = sp + Vessel.Bias(sp);
        double y = double.NaN, tl = 0;
        double Lift(double t, double real)
        {
            if (t < liftAt) return real;
            if (double.IsNaN(y)) { y = real; tl = t; }
            if (t > tl) { y += (amb - y) * (1 - Math.Exp(-(t - tl) / tauP)); tl = t; }
            return Math.Round(y, 2);
        }
        var s1 = new S1Tap(stopOnTrip: true);
        var run = Simulate(sp, tj0, tj0, sp, minutes: 10 + 40, Lift, s1: s1);
        Assert.False(double.IsNaN(s1.StopAt), "提出液面 40 min 都没判到");
        var i = (int)(s1.StopAt / 0.5);
        var peak = run.Tr[i..].Max();
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"τp {tauP} s：提出后 {(s1.StopAt - liftAt) / 60:F2} min 判到 {s1.StopWhich}（D {s1.StopBeat.D:F2} K、u {s1.StopBeat.U:F2} K），那一刻读数 {s1.StopBeat.Y30:F2}、" +
            $"真釜内 {run.Tr[i]:F2}、夹套 {run.Tj[i]:F2}；停后真釜内最高 {peak:F2}（比目标 +{peak - sp:F2} K）"));
        Assert.Contains("B", s1.StopWhich);
        Assert.InRange((s1.StopAt - liftAt) / 60, 0, 8);
        Assert.True(peak - sp <= 23.5, $"停后冲到 {peak:F2}");
    }

    /// <summary>
    /// 【本地改动】（2026-11-17，S1）插错工位：60 ℃ 保温 10 min 后读数换成另一个工位上一台稳在 40 ℃ 的釜（同一个闭环把那台釜也跑一遍，读数是它的）。
    /// 定稿试算台「插错到一台稳着的冷釜」0.8–1.5 min、B 判到，停后比读数对的同一刻停多热 4.3–6.7 K。这里断言：B 判到、换了以后 ≤ 2 min、停后真釜内最高 ≤ 目标 + 8 K。
    /// </summary>
    [Fact]
    public void 新外环在控时探头插错到另一个稳在40度的工位_S1按B停控()
    {
        const double sp = 60, swapAt = 600;
        var other = Simulate(40, 40 + Vessel.Bias(40), 40 + Vessel.Bias(40), 40, minutes: 50);
        double OtherAt(double t)
        {
            var k = (int)Math.Floor(t / 0.5) - 1;                     // other.T[k] = (k + 1) × 0.5：t 那一刻那台釜的读数
            return k < 0 ? other.Tr[0] : other.Tr[Math.Min(k, other.Tr.Length - 1)];
        }
        var tj0 = sp + Vessel.Bias(sp);
        var s1 = new S1Tap(stopOnTrip: true);
        var run = Simulate(sp, tj0, tj0, sp, minutes: 10 + 40, (t, real) => t < swapAt ? real : OtherAt(t), s1: s1);
        Assert.False(double.IsNaN(s1.StopAt), "插错工位 40 min 都没判到");
        var i = (int)(s1.StopAt / 0.5);
        var peak = run.Tr[i..].Max();
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"插错到 40 ℃ 那一台：换了以后 {(s1.StopAt - swapAt) / 60:F2} min 判到 {s1.StopWhich}；那一刻真釜内 {run.Tr[i]:F2}、夹套 {run.Tj[i]:F2}；停后真釜内最高 {peak:F2}"));
        Assert.Contains("B", s1.StopWhich);
        Assert.InRange((s1.StopAt - swapAt) / 60, 0, 2);
        Assert.True(peak - sp <= 8, $"停后冲到 {peak:F2}");
    }

    public enum Normal { Hold60, ExoStep, ExoRamp, EndoRamp, Charge10K, Charge15K, HotJacketStart }

    /// <summary>
    /// 【本地改动】（2026-11-17，S1）读数都对的场次 S1 一次不停、不报（判据挂着、判到就停的那种接法）：
    /// · 60 ℃ 保温 70 min；
    /// · 放热：10 min 后来一段 0.5 K/min、20 min 的放热（阶跃）；放热速率每小时涨 0.4 K/min 的慢斜坡（310 min，定稿 twin 慢斜坡 R_A 最小 −0.192，门槛 −0.25）；
    /// · 吸热速率每小时涨 0.4 K/min（310 min，夹具 twin_ramp_m04 那一场，R_A 最大 +0.193）；
    /// · 加冷料：保温 20 min 后真釜内一下子掉 10 / 15 K（定稿试算台 5–15 K 0 停，只有「料一下子变 4 倍」那种 2/20 会停——这里只掉温度、热容不变）；
    /// · 开控时夹套烫：釜内 45、夹套 85、釜壁 60 起到 60。
    /// 前两条 D1 正常场（稳在 42 / 11-16 开控态 → 60）在 D1 那一条里一起挂着判据。
    /// </summary>
    [Theory]
    [InlineData(Normal.Hold60)]
    [InlineData(Normal.ExoStep)]
    [InlineData(Normal.ExoRamp)]
    [InlineData(Normal.EndoRamp)]
    [InlineData(Normal.Charge10K)]
    [InlineData(Normal.Charge15K)]
    [InlineData(Normal.HotJacketStart)]
    public void 读数都对_保温放热吸热加冷料开控时夹套烫_S1不停不报(Normal kind)
    {
        const double sp = 60;
        var tjs = sp + Vessel.Bias(sp);
        var s1 = new S1Tap(stopOnTrip: true);
        var charged = false;
        Func<double, double, double>? disturb = kind switch
        {
            Normal.ExoStep => (t, h) => t > 600 && t <= 600 + 1200 ? 0.5 / 60 * h : 0,
            Normal.ExoRamp => (t, h) => t >= 600 ? 0.4 * (t - 600) / 3600.0 / 60.0 * h : 0,
            Normal.EndoRamp => (t, h) => t >= 600 ? -0.4 * (t - 600) / 3600.0 / 60.0 * h : 0,
            Normal.Charge10K or Normal.Charge15K => (t, _) =>
            {
                if (charged || t < 1200) return 0;
                charged = true;
                return kind == Normal.Charge10K ? -10 : -15;
            },
            _ => null,
        };
        var minutes = kind is Normal.ExoRamp or Normal.EndoRamp ? 310 : 70;
        var run = kind == Normal.HotJacketStart
            ? Simulate(45, 85, 60, sp, minutes: 120, s1: s1)
            : Simulate(sp, tjs, tjs, sp, minutes, s1: s1, disturb: disturb);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{kind}：S1 停 {(double.IsNaN(s1.StopAt) ? "无" : $"t = {s1.StopAt} s {s1.StopWhich}")}、报警 {(double.IsNaN(s1.FirstAlarmAt) ? "无" : $"t = {s1.FirstAlarmAt} s")}；" +
            $"R_A 最大 {s1.MaxRa:+0.000;-0.000} / 最小 {s1.MinRa:+0.000;-0.000}（门槛 ±0.25）、D 最大 {s1.MaxD:F2}（门槛 8）；" +
            $"真釜内最高 {run.Tr.Max():F2} / 最低 {run.Tr.Min():F2}、末了 {run.Tr[^1]:F2}"));
        Assert.True(double.IsNaN(s1.StopAt), $"读数都对却停了：t = {s1.StopAt} s，{s1.StopWhich}");
        Assert.True(double.IsNaN(s1.FirstAlarmAt), $"读数都对却报警了：t = {s1.FirstAlarmAt} s");
    }

    [Fact]
    public void A3_影子TimeScale_按K倍压缩的墙钟喂跟按现场秒喂逐位一样_含拆小步和断档重摆()
    {
        // K = 4（2 的幂：墙钟秒 × 4 在浮点里是精确的；时刻都取 1/8 s 的整数倍，TimeSpan.TotalSeconds 也精确）
        const double K = 4;
        var real = new LiveOuterShadow();
        var warped = new LiveOuterShadow { TimeScale = K };
        var t0 = new DateTime(2026, 11, 16, 9, 0, 0, DateTimeKind.Local);
        // 现场秒的节拍：0.5 s 一拍的回路（没到外环拍的多数拍）、3 s / 12 s（拆小步）/ 80 s（断档重摆）的空当、中间丢几拍釜内读数、开控 / 改目标
        var gaps = new List<double>();
        for (var i = 0; i < 400; i++) gaps.Add(0.5);
        gaps.AddRange(new[] { 3.0, 2.5, 12.0, 0.5, 0.5, 80.0, 0.5, 2.0, 2.0, 30.0 });
        for (var i = 0; i < 400; i++) gaps.Add(0.5);
        double ts = 0;
        var k = 0;
        var distinct = new HashSet<LiveOuterReadout>();
        foreach (var g in gaps)
        {
            ts += g;
            k++;
            var inControl = k > 150;
            var facts = new LiveOuterFacts
            {
                Tj = 35 + 0.01 * k,
                Tr = k % 37 == 0 ? double.NaN : Math.Round(30 + 0.004 * k, 2),
                ReactorInControl = inControl,
                SetpointC = k > 600 ? 45 : 40,
                TecSide = k % 50 < 10,
                InnerSatLow = k % 50 < 20,
                OutputOn = inControl,
            };
            var a = real.Tick(t0.AddTicks((long)Math.Round(ts * 1e7)), 2.0, facts);
            var b = warped.Tick(t0.AddTicks((long)Math.Round(ts / K * 1e7)), 2.0 / K, facts);
            Assert.True(a == b, $"第 {k} 拍（现场 {ts} s）不一样：\n现场 {a}\n压缩 {b}");
            if (a is not null) distinct.Add(a);
        }
        Assert.False(real.Stopped || warped.Stopped);
        Assert.Equal(real.Core.ExportState(), warped.Core.ExportState());
        Assert.True(distinct.Count > 150, $"影子只算了 {distinct.Count} 拍");   // 不是摆设：一直在算
    }
}
