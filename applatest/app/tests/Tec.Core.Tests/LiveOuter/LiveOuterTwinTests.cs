using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Tec.Drivers.Rd105;
using TecControl.Core.Control;
using Xunit;
using Xunit.Abstractions;

namespace Tec.Core.Tests;

/// <summary>
/// 【本地改动】（2026-11-16，外环改「按实测实时算」第 1 步，D2）新外环接手控温的真回路孪生：<b>产品的 HostControlLoop</b>（选「新」、开控锁定、
/// 新外环那一支、内环 PID、双向挡映射、分方向跑飞保护全是产品代码）+ 假 RD105 + 一个釜，时间压缩 <see cref="K"/> 倍。
/// D1（LiveOuterClosedLoopTests）证明核心类 + 内环 PID 闭得上环；这一条证明的是<b>回路接对了</b>：每一拍内环追的就是新外环这一拍给的数、
/// 旧外环一拍没算、旧规则一句没出，在这样接起来的整套上到站不冲、保温不出带。
///
/// 对象（照实说，跟 D1 同一组方程）：
/// · 釜内 / 釜壁：11-09 那场逐拍拟合（OuterMeasuredBiasTwinTests.Run095506 同一组）——τ1 1005 s、b(Tr) = 5.05 + 0.087 ×（Tr − 50）、
///   壁滞后 129 s、夹套到壁纯滞后 42 s；
/// · 夹套 + 继电器：HeatBoostTwinTests.Plant 那一组方程原样（11-04 加热棒段拟合；加热棒 6.5 s 纯滞后 + 23.7 s + 4.0 s、TEC 41 s；
///   换到加热棒要 3 s、换到 TEC 要 8 s）。它的 Step 写死 K = 40——按这里稳住的时刻（58.7 / 71.9 min）+ 30 min 折算，那样一条要 132 / 153 s 墙钟，超过这一条的预算 120 s，
///   所以这里照抄一份、K 改成 60（CascadeFieldTwinTests / OuterApproachTests 也是 60）。没有环境散热、没有冷却水、加热比是常数，同 D1。
/// · 读数：釜内 0.01 K 量化的干净读数；夹套原值。
/// 回路照现场：加热棒 + TEC 双向（双向挡表，加热比 3.5、LIMITED 90）、内环 Kp 9.4 / Ki 0.037 / Kd 175（滤波 8.6 s，按 K 缩），
/// 回路 1.5 s 一拍、外环 2 s 一拍（现场秒）。新外环的影子用 TimeScale = K（墙钟秒 × K = 现场秒，主会话 D14），出厂缺省参数一个没动。
/// 跑飞检测换成按 K 缩过的（180 s / 120 s → ÷ K）：不缩等于没有。「TEC 真接着」每拍按对象的继电器给（会话 C7 做的事）。
/// 双向挡表只摆内环参数、不摆稳态偏置——新外环不查表，表里有没有偏置都一样。
///
/// 判（30 s 尾随平均、带内按 0.01 取整比，口径同 D1 / 试算台 metrics.py）：每一拍内环设定 == clamp(新外环这一拍的读数)、algo = 1、outer 列 NaN；
/// 旧外环的积分和节拍时刻从开控到结束一动没动；冲过 ≤ 0.10 K；稳住（首次连续 120 s 在 ±0.1）后 30 min 不出带；
/// 回路的提示里没有旧规则的字（夹套压低 / 那一枪 / 外环积分冻结 / 外环偏置自校正 / 升温加速 / 积分起点）、没停控、没周期故障、影子没停。
/// 时间类（首进 / 稳住时刻）、保温段继电器翻了几次、末了 b̂ / τ̂、夹套最高只报不判。
/// 作废：相邻两拍墙钟间隔超过现场 10 s（机器太挤、线程卡了）这一跑不算，当场停下重跑一次；两次都作废判红并说明是作废不是控制不过。
/// 能信什么：判「冲过 / 出带 / 摆」可信；判「过」不等于真机过（试算台到站偏乐观，bench/README「限度」）。
/// </summary>
public class LiveOuterTwinTests(ITestOutputHelper output)
{
    internal const double K = 60;

    /// <summary>超过这个现场秒数的墙钟空当判这一跑作废。</summary>
    private const double InvalidGapFieldS = 10;

    /// <summary>釜 + 夹套 + 继电器。方程同 HeatBoostTwinTests.Plant（那边写死 K = 40），这里 K 是参数；多给出继电器此刻在哪一侧。</summary>
    private sealed class Plant
    {
        private readonly object _gate = new();
        private double _tj, _tr, _tw, _s, _h1, _h2, _cf, _t, _pend;
        private int _conn, _pendSide;
        private readonly Queue<(double T, double H)> _hq = new();
        private readonly Queue<(double T, double Tj)> _wq = new();
        private DateTime _last;
        private int _switches;
        private const double Tau1 = 1005, WallLag = 129, WallDelay = 42, B50 = 5.05, BPerK = 0.087;

        public Plant(double tr0, double tj0, double tw0)
        {
            _tr = tr0; _tj = tj0; _tw = tw0; _s = tj0;
            _conn = _pendSide = 1;
        }

        public static double Bias(double tr) => B50 + BPerK * (tr - 50);
        public double Tr { get { lock (_gate) return _tr; } }
        public double Tj { get { lock (_gate) return _tj; } }
        public double SimSeconds { get { lock (_gate) return _t; } }
        public int Switches { get { lock (_gate) return _switches; } }
        /// <summary>继电器此刻接在哪一侧：+1 加热棒、−1 TEC。</summary>
        public int Conn { get { lock (_gate) return _conn; } }

        public void Step(DateTime at, double deviceDuty)
        {
            lock (_gate)
            {
                if (_last == default) { _last = at; return; }
                var dt = Math.Clamp((at - _last).TotalSeconds, 0, 0.5) * K;
                _last = at;
                var n = Math.Max(1, (int)Math.Ceiling(dt / 0.5));
                var h = dt / n;
                for (var i = 0; i < n; i++)
                {
                    var want = deviceDuty > 0 ? 1 : deviceDuty < 0 ? -1 : _conn;
                    if (want != _conn)
                    {
                        if (_pendSide != want) { _pendSide = want; _pend = 0; }
                        _pend += h;
                        if (_pend >= (want == 1 ? 3 : 8)) { _conn = want; _pend = 0; _switches++; }
                    }
                    else { _pend = 0; _pendSide = _conn; }
                    var heat = deviceDuty > 0 && _conn == 1 ? deviceDuty : 0;
                    var cool = deviceDuty < 0 && _conn == -1 ? -deviceDuty : 0;
                    _t += h;
                    _hq.Enqueue((_t, heat));
                    var h0 = _hq.Peek().H;
                    while (_hq.Count > 1 && _t - _hq.Peek().T > 6.5) h0 = _hq.Dequeue().H;
                    _h1 += (h0 - _h1) * h / 23.67;
                    _h2 += (_h1 - _h2) * h / 4.03;
                    _cf += (cool - _cf) * h / 41.0;
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
        }
    }

    /// <summary>开控后每一拍记下的东西。T = 对象的现场秒（从开控那一拍算起）；Tr 是回路看到的 0.01 取整读数。</summary>
    private sealed record Row(double T, double Tr, double Tj, double Isp, int Switches, LiveOuterReadout? Lo, bool InCtl,
        double OuterI, double OuterPidLastI, DateTime LastOuterUpdate);

    private sealed record Result(bool Valid, double MaxGapFieldS, int GapsOver2S, Row[] Rows, string[] Notices, string[] Trips, int Faults,
        bool ShadowStopped, double WallS);

    private static readonly BindingFlags Pub = BindingFlags.Public | BindingFlags.Instance;

    private static object StateOf(HostControlLoop loop, int ch)
        => typeof(HostControlLoop).GetMethod("State", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(loop, new object[] { ch })!;

    /// <summary>跑一场：从 (釜内, 夹套, 釜壁) 起、目标 sp。跑到「稳住 + 30 min」或封顶 capMin（现场分钟）；中途墙钟空当超过现场 10 s 当场停、判作废。</summary>
    private static async Task<Result> RunOnce(double tr0, double tj0, double tw0, double sp, double capMin)
    {
        var sw = Stopwatch.StartNew();
        var plant = new Plant(tr0, tj0, tw0);
        var dev = new FakeRd105Device { Thermal = false };
        dev.Set(1, "TCADJTEMP", (long)Math.Round(plant.Tj * 1e5));
        dev.Set(1, "LIMITED", 90);
        dev.Open();
        var loop = new HostControlLoop(new Rd105Link(dev).Controller)
        {
            Period = TimeSpan.FromMilliseconds(1500 / K),           // 现场 1.5 s（同 HeatBoostTwinTests）
            OuterPeriod = TimeSpan.FromMilliseconds(2000 / K),      // 现场 2 s：新外环的设计拍长
            SyncMode = ChannelSyncMode.Independent,
            ManagePwmFrequency = false,
            HeatingEffectivenessRatio = 3.5,
        };
        try
        {
            loop.SetActuatorMode(1, ActuatorMode.HeaterTec);          // 缺省查双向挡那张表（锁成新的条件之一）
            var both = loop.GetGainSchedule(1, GainTable.Both);
            both.SegmentBoundaryC = double.NaN;
            foreach (var t in new[] { 40.0, 50.0, 60.0, 70.0 })
                both.Learn(new GainPoint(t, new PidGains(9.4, 0.037 * K, 175 / K), 0, 0, new PidGains(2.5, 0.01 * K, 0), 8, HeatRatio: 3.5));
            loop.ConfigurePid(1, 9.4, 0.037 * K, 175 / K, 90, invertOutput: false, derivativeFilterSeconds: 8.6 / K);
            loop.ConfigureCascade(1, 2.5, 0.01 * K, 0, 8);
            loop.SetStrategy(1, ControlStrategy.Cascade);
            loop.SetOuterSource(1, () => Math.Round(plant.Tr, 2));
            loop.SetOuterAlgorithm(1, true);

            var st = StateOf(loop, 1);
            st.GetType().GetField("Live", Pub)!.SetValue(st, new LiveOuterShadow { TimeScale = K });
            st.GetType().GetField("Runaway", Pub)!.SetValue(st, new RunawayDetector(0.5, 180 / K, 120 / K));
            var outerPid = (PidController)st.GetType().GetField("OuterPid", Pub)!.GetValue(st)!;
            var lastOuter = st.GetType().GetField("LastOuterUpdate", Pub)!;

            var gate = new object();
            var rows = new List<Row>();
            var notices = new List<string>();
            var trips = new List<string>();
            var faults = 0;
            DateTime prevAt = default;
            double maxGap = 0, startSim = double.NaN;
            var gapsOver2 = 0;
            var started = false;
            loop.ChannelNotice += (ch, _, text) => { if (ch == 1) lock (gate) notices.Add(text); };
            loop.ChannelTripped += (ch, reason) => { lock (gate) trips.Add($"通道{ch}：{reason}"); };
            loop.CycleFaulted += _ => Interlocked.Increment(ref faults);
            loop.CycleCompleted += res =>
            {
                var c = res.Ch1;
                var at = res.Snapshot.Timestamp;
                plant.Step(at, c.Active ? c.DutyPercent : 0);
                dev.Set(1, "TCADJTEMP", (long)Math.Round(plant.Tj * 1e5));
                loop.SetTecEngaged(1, plant.Conn == -1);          // 会话 C7：继电器在 TEC 侧 = TEC 真接着
                lock (gate)
                {
                    if (prevAt != default && started)
                    {
                        var gap = (at - prevAt).TotalSeconds * K;
                        maxGap = Math.Max(maxGap, gap);
                        if (gap > 2.0 + 1e-9) gapsOver2++;
                    }
                    prevAt = at;
                    if (!c.Active) return;
                    if (double.IsNaN(startSim)) startSim = plant.SimSeconds;
                    rows.Add(new Row(plant.SimSeconds - startSim, Math.Round(plant.Tr, 2), plant.Tj, c.InnerSetpointC, plant.Switches,
                        c.LiveOuter, c.LiveOuterInControl, c.OuterI, outerPid.LastI, (DateTime)lastOuter.GetValue(st)!));
                }
            };
            loop.Start();

            // 影子先起算（连上设备第一拍两个读数都有就起算），再空转现场 1 min，然后开控
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!loop.GetLiveOuterStatus(1).ShadowStarted)
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("影子 10 s 没起算");
                await Task.Delay(5);
            }
            await Task.Delay(TimeSpan.FromSeconds(60 / K));
            await loop.StartChannelAsync(1, sp);
            lock (gate) started = true;
            Assert.True(loop.IsLiveOuterInControl(1), "开控没锁成新外环：" + loop.GetLiveOuterStatus(1).LockNote);

            var valid = true;
            var capS = capMin * 60;
            while (true)
            {
                await Task.Delay(500);
                Row[] snap;
                double gapNow;
                lock (gate) { snap = rows.ToArray(); gapNow = maxGap; }
                if (gapNow > InvalidGapFieldS) { valid = false; break; }
                if (!loop.GetChannelStatus(1).Active) break;
                if (snap.Length == 0) continue;
                if (snap[^1].T >= capS) break;
                var m = Measure(snap, sp);
                if (!double.IsNaN(m.Settle) && snap[^1].T - m.Settle >= 1800 + 30) break;
                if (sw.Elapsed > TimeSpan.FromSeconds(capS / K * 2 + 30))
                    throw new TimeoutException($"墙钟 {sw.Elapsed.TotalSeconds:F0} s 才跑到现场 {snap[^1].T / 60:F1} min");
            }
            await loop.ShutdownAsync();
            lock (gate)
                return new Result(valid, maxGap, gapsOver2, rows.ToArray(), notices.ToArray(), trips.ToArray(), faults,
                    loop.GetLiveOuterStatus(1).ShadowStopped, sw.Elapsed.TotalSeconds);
        }
        finally
        {
            await loop.ShutdownAsync();
            loop.Dispose();
        }
    }

    private sealed record Metrics(double OverAvg, double OverRaw, double FirstIn, double Settle, int Exc, double Worst, int HoldSwitches, double Observed,
        double IspP2pMedian);

    /// <summary>升温台阶的口径（同 D1 / 试算台 metrics.py）：30 s 尾随平均、带内按 0.01 取整比、|e| = 0.10 算带内。</summary>
    private static Metrics Measure(Row[] r, double sp)
    {
        var n = r.Length;
        var avg = new double[n];
        double sum = 0;
        var a = 0;
        for (var i = 0; i < n; i++)
        {
            sum += r[i].Tr;
            while (r[a].T <= r[i].T - 30) sum -= r[a++].Tr;
            avg[i] = sum / (i - a + 1);
        }
        // 升温台阶从下面上来：冲过 = 30 s 平均比目标高出最多的那一下。一直没够着目标（平均停在 59.99x）就是负数——没冲过
        var overAvg = double.NegativeInfinity;
        for (var i = 0; i < n; i++) overAvg = Math.Max(overAvg, avg[i] - sp);
        bool In(int i) => Math.Abs(Math.Round(avg[i] - sp, 2)) <= 0.10 + 1e-9;
        double firstIn = double.NaN, settle = double.NaN, runStart = double.NaN;
        for (var i = 0; i < n; i++)
        {
            if (!In(i)) { runStart = double.NaN; continue; }
            if (double.IsNaN(firstIn)) firstIn = r[i].T;
            if (double.IsNaN(runStart)) runStart = r[i].T;
            if (r[i].T - runStart >= 120) { settle = runStart; break; }
        }
        int exc = 0, sw0 = -1, sw1 = -1;
        double worst = 0;
        var prevIn = true;
        var holdIsp = new List<(double T, double Isp)>();
        for (var i = 0; i < n && !double.IsNaN(settle); i++)
        {
            if (r[i].T < settle) continue;
            if (r[i].T > settle + 1800) break;
            if (!In(i))
            {
                if (prevIn) exc++;
                worst = Math.Max(worst, Math.Abs(avg[i] - sp));
            }
            prevIn = In(i);
            if (sw0 < 0) sw0 = r[i].Switches;
            sw1 = r[i].Switches;
            holdIsp.Add((r[i].T, r[i].Isp));
        }
        // 保温段夹套目标每 60 s 窗的峰峰值，取中位（第 0 步给 40 ℃ 保温定的那把尺子：< 0.1 K）
        var p2p = new List<double>();
        for (var w = 0; holdIsp.Count > 0 && holdIsp[0].T + (w + 1) * 60 <= holdIsp[^1].T; w++)
        {
            var lo = holdIsp[0].T + w * 60;
            var win = holdIsp.Where(x => x.T >= lo && x.T < lo + 60).Select(x => x.Isp).ToArray();
            if (win.Length > 0) p2p.Add(win.Max() - win.Min());
        }
        p2p.Sort();
        var med = p2p.Count == 0 ? double.NaN : p2p[p2p.Count / 2];
        var observed = double.IsNaN(settle) ? double.NaN : Math.Min(1800, r[^1].T - settle);
        return new Metrics(overAvg, r.Max(x => x.Tr) - sp, firstIn, settle, exc, worst, sw0 < 0 ? 0 : sw1 - sw0, observed, med);
    }

    /// <summary>回路提示里不许出现的旧规则字眼（验收仿真 / 孪生按它们计数，新外环在控时一句都不该有）。</summary>
    private static readonly string[] OldRuleWords = { "夹套压低", "那一枪", "外环积分冻结", "外环偏置自校正", "升温加速", "积分起点" };

    [Theory]
    [InlineData("稳在42起到60", 42.0, 0)]
    [InlineData("11-16开控态到60", 43.74, 35.14)]
    public async Task D2_真回路新外环在控_每拍内环追的就是新外环的数_旧外环一拍没算_旧规则一句没出_到60不冲过0点1_稳住后30分钟不出带(
        string name, double tr0, double tj0)
    {
        const double sp = 60, capMin = 110;
        // 稳在 42：釜内 42、夹套 = 釜壁 = 42 + b(42)；11-16 那场开控那一拍（R1-tc1-20261116-133129.csv 第一行）：夹套比釜内冷、釜壁 = 夹套
        if (tj0 == 0) tj0 = tr0 + Plant.Bias(tr0);
        var r = await RunOnce(tr0, tj0, tj0, sp, capMin);
        if (!r.Valid)
        {
            output.WriteLine($"{name}：第一跑作废（墙钟空当最长 = 现场 {r.MaxGapFieldS:F1} s > {InvalidGapFieldS} s，跑了 {r.WallS:F0} s 墙钟），重跑一次");
            r = await RunOnce(tr0, tj0, tj0, sp, capMin);
        }
        Assert.True(r.Valid, $"{name}：连着两跑都作废（墙钟空当最长 = 现场 {r.MaxGapFieldS:F1} s > {InvalidGapFieldS} s）——机器太挤、回路线程卡了，这不是控制不过，换个空一点的时候重跑");

        var rows = r.Rows;
        var m = Measure(rows, sp);
        var last = rows[^1];
        var inv = CultureInfo.InvariantCulture;
        output.WriteLine(string.Create(inv,
            $"{name}：釜内 {tr0} / 夹套 {tj0:F2} → {sp}（K = {K}，墙钟 {r.WallS:F0} s，现场 {last.T / 60:F1} min，{rows.Length} 拍；" +
            $"墙钟空当最长 = 现场 {r.MaxGapFieldS:F2} s、超过现场 2 s 的 {r.GapsOver2S} 次）\n" +
            $"  冲过（30 s 平均比目标高出最多；负数 = 没越过目标）{m.OverAvg:+0.000;−0.000;0.000} K、原始读数最高比目标 {m.OverRaw:+0.00;−0.00;0.00} K；首进 ±0.1 {m.FirstIn / 60:F1} min、稳住 {m.Settle / 60:F1} min（只报）；" +
            $"稳住后 {m.Observed / 60:F1} min 出带 {m.Exc} 次（最远 {m.Worst:F3}）；保温段继电器翻 {m.HoldSwitches} 次、全程 {last.Switches} 次（只报）；" +
            $"保温段夹套目标 60 s 峰峰中位 {m.IspP2pMedian:F3} K（只报）；夹套目标最高 {rows.Max(x => x.Isp):F2}、夹套最高 {rows.Max(x => x.Tj):F2}（只报）\n" +
            $"  末了 b̂ {last.Lo?.BHat:F3}（夹套 − 釜内 {last.Tj - last.Tr:F3}、对象真值 b(60) {Plant.Bias(60):F3}）、τ̂ {last.Lo?.Tau:F0} s、τ₂ {last.Lo?.TauEkf:F0} s（只报）"));
        foreach (var n in r.Notices) output.WriteLine("  · " + n);

        // 回路没出事：没停控、没周期故障、影子一直在算
        Assert.Empty(r.Trips);
        Assert.Equal(0, r.Faults);
        Assert.False(r.ShadowStopped);

        // ① 新外环真在控：每一拍 algo = 1、有读数、内环设定 == clamp(它这一拍给的夹套目标)，逐位
        Assert.All(rows, x => Assert.True(x.InCtl, $"现场 {x.T:F0} s 那一拍 algo 不是 1"));
        foreach (var x in rows)
        {
            Assert.True(x.Lo is not null, $"现场 {x.T:F0} s 那一拍新外环没读数");
            Assert.True(x.Isp == Math.Clamp(x.Lo!.JacketSetpointC, -50, 200),
                $"现场 {x.T:F0} s 那一拍内环设定 {x.Isp} ≠ 新外环给的 {x.Lo.JacketSetpointC}");
        }
        Assert.True(rows.Select(x => x.Isp).Distinct().Count() > 500, "夹套目标几乎没动，不像在控");
        // ② 旧外环一拍没算：积分、节拍时刻从开控到结束一动没动（开控那一下也没预置），outer 列 NaN
        Assert.All(rows, x => Assert.True(x.OuterPidLastI == rows[0].OuterPidLastI && x.LastOuterUpdate == rows[0].LastOuterUpdate,
            $"现场 {x.T:F0} s 旧外环动了：积分 {x.OuterPidLastI}、节拍 {x.LastOuterUpdate:O}"));
        Assert.Equal(0, rows[0].OuterPidLastI);
        Assert.All(rows, x => Assert.True(double.IsNaN(x.OuterI)));
        // ③ 旧规则一句没出；开控说的是新算法
        foreach (var n in r.Notices)
            foreach (var w in OldRuleWords)
                Assert.False(n.Contains(w), $"新外环在控时回路说了旧规则的话「{w}」：{n}");
        Assert.Contains(r.Notices, n => n.Contains("外环：新算法"));
        // ④ 控得住：冲过 ≤ 0.10 K（用户的线），稳住后 30 min 不出 ±0.1
        Assert.False(double.IsNaN(m.Settle), $"现场 {last.T / 60:F0} min 里没稳住");
        Assert.True(m.Observed >= 1800 - 1e-6, $"稳住以后只看了 {m.Observed / 60:F1} min");
        Assert.True(m.OverAvg <= 0.10 + 1e-9, $"冲过 {m.OverAvg:F3} K（30 s 平均）超过 0.10");
        Assert.Equal(0, m.Exc);
    }
}
