using TecControl.Core.Control;
using Tec.Drivers.Rd105;
using Xunit;
using Xunit.Abstractions;

namespace Tec.Core.Tests;

/// <summary>
/// 升温加速（<see cref="HostControlLoop.HeatBoost"/>，速度环 + 刹车曲线）接在真回路上、用 2026-11-04 现场 −2 → 20 ℃ 那场（R1-tc1-20261104-104405）
/// 拟合的升温孪生跑。时间压缩 <see cref="K"/> 倍。
/// · 釜：dTr/dt = (Tw − Tr − 1.6)/1027，dTw/dt = (Tj − Tw)/142（夹套 → 釜壁那一段滞后）；
/// · 夹套（加热棒段拟合）：dTj/dt = 0.002815 × 加热棒 % − 0.00152 × TEC % + 0.0061 + (S − Tj)/429.7 + (Tr − Tj)/816.8，dS/dt = (Tj − S)/150.7；
///   加热棒 % 过 6.5 s 纯滞后 + 23.7 s + 4.0 s 两级滞后，TEC % 过 41 s 滞后；继电器切换：换到加热棒要 3 s、换到 TEC 要 8 s（符号驻留 + 序列）。
/// · 回路照现场：双向挡（加热比 3.5、TEC 满 90 %）、内环 Kp 9.4 / Ki 0.037 / Kd 175（滤波 8.6 s）、外环 Kp 2.5 / Ki 0.01 / 限幅 8、表里学到的稳态偏置 +1.6。
/// 同一份模型的 python 版（scratchpad an1104/design/C_velocity/vsim.py）：现在的外环首进 ±0.1 30.1 min、稳进 ±0.05 46.5 min、冲过 0.071 K；
/// 升温加速（夹套上限 sp + 19、温差限 30 K）27.7 / 30.2 min、冲过 0.008 K。
/// </summary>
public class HeatBoostTwinTests(ITestOutputHelper output)
{
    internal const double K = 40;

    internal sealed class Plant
    {
        private readonly object _gate = new();
        private double _tj, _tr, _tw, _s, _h1, _h2, _cf, _t, _pend;
        private int _conn, _pendSide;
        private readonly Queue<(double T, double H)> _hq = new();
        private readonly Queue<(double T, double Tj)> _wq = new();
        private DateTime _last;
        public int Switches;
        private readonly double _tau1, _twLag, _wallDelay, _bPerK, _bAtC;
        private double _b;

        /// <summary>釜：τ1 / 稳态偏置 b / 壁滞后 tw / 夹套到壁的纯滞后（s）缺省 = −2 → 20 那场的拟合；40 ℃ 那场（165419）拟合 981 / 3.32 / 116 / 18。
        /// 偏置随温度变：b + bPerK ×（Tr − bAtC）（缺省不变）。</summary>
        public Plant(double tr0, double tj0, double tw0, double s0, double tau1 = 1027, double b = 1.6, double tw = 142, double wallDelay = 0,
            double bPerK = 0, double bAtC = 0)
        {
            _tr = tr0; _tj = tj0; _tw = tw0; _s = s0;
            _conn = _pendSide = 1;
            _tau1 = tau1; _b = b; _twLag = tw; _wallDelay = wallDelay; _bPerK = bPerK; _bAtC = bAtC;
        }

        /// <summary>稳态偏置（运行中可改：模拟估值跟实际差一截）。</summary>
        public double B { get { lock (_gate) return _b; } set { lock (_gate) _b = value; } }
        public double Tr { get { lock (_gate) return _tr; } }
        public double Tj { get { lock (_gate) return _tj; } }
        public double SimSeconds { get { lock (_gate) return _t; } }

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
                        if (_pend >= (want == 1 ? 3 : 8)) { _conn = want; _pend = 0; Switches++; }
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
                    var dTr = (_tw - _tr - (_b + _bPerK * (_tr - _bAtC))) / _tau1;
                    _wq.Enqueue((_t, _tj));
                    var tjd = _wq.Peek().Tj;
                    while (_wq.Count > 1 && _t - _wq.Peek().T > _wallDelay) tjd = _wq.Dequeue().Tj;
                    _tw += (tjd - _tw) / _twLag * h;
                    _tr += dTr * h;
                }
            }
        }
    }

    internal sealed class Rig : IAsyncDisposable
    {
        public readonly FakeRd105Device Dev;
        public readonly HostControlLoop Loop;
        public readonly Plant P;
        private readonly object _gate = new();
        private readonly List<string> _notes = new();
        private readonly List<(double T, double Tr, double Tj, double Isp, double I, bool Boost, double Vm, double Vs)> _trace = new();

        /// <param name="bothRows">【本地改动】给了就按它摆双向挡表（B = null 的行只带参数、不带稳态偏置 = 表里「没有这个温度的偏置」）；不给照旧 rows × learnedBias。</param>
        /// <param name="heaterRows">【本地改动】加热棒表的行（带外环参数才算「登记过外环」、双向挡表给不出时回路才借得到）。</param>
        public Rig(Plant plant, HeatBoostLevel level, bool allowed, Action<HostControlLoop>? tweak = null, double learnedBias = 1.6,
            double[]? rows = null, Action<Rig, ChannelCycleInfo>? onCycle = null,
            (double T, double? B)[]? bothRows = null, (double T, double B)[]? heaterRows = null)
        {
            P = plant;
            Dev = new FakeRd105Device { Thermal = false };
            Dev.Set(1, "TCADJTEMP", (long)Math.Round(plant.Tj * 1e5));
            Dev.Set(1, "LIMITED", 90);
            Dev.Open();
            Loop = new HostControlLoop(new Rd105Link(Dev).Controller)
            {
                Period = TimeSpan.FromMilliseconds(37.5),          // 现场 0.5 s；这里 1.5 s
                OuterPeriod = TimeSpan.FromMilliseconds(50),       // 现场 2 s
                SyncMode = ChannelSyncMode.Independent,
                ManagePwmFrequency = false,
                HeatingEffectivenessRatio = 3.5,
                OuterApproachWindow = TimeSpan.FromSeconds(60 / K),
                OuterApproachFreezeCPerMin = 0.05 * K,
                OuterApproachHorizon = TimeSpan.FromSeconds(15 * 60 / K),
                OuterApproachMinCPerMin = 0.015 * K,
                OuterSteadyCyclesToLearn = 30,
                OuterStartQuietWindow = TimeSpan.Zero,
                OuterFinishGain = 0,                               // 升温不武装；关掉省事
                HeatBoost = level,
                HeatBoostTauHat = 800 / K,
                HeatBoostWallHat = 250 / K,
                HeatBoostDecel = 6e-5 * K * K,
                HeatBoostKv = 1500 / K,
                HeatBoostWindow = TimeSpan.FromSeconds(40 / K),
                HeatBoostBrakeTime = TimeSpan.FromSeconds(200 / K),
                HeatBoostHandStillCPerMin = 0.03 * K,
                HeatBoostHandHold = TimeSpan.FromSeconds(240 / K),
                HeatBoostHandAverage = TimeSpan.FromSeconds(120 / K),
                HeatBoostStallWindow = TimeSpan.FromSeconds(300 / K),
                HeatBoostDenyGrace = TimeSpan.FromSeconds(30 / K),
                // 外环偏置自校正照现场开着（按压缩时间）：这些场景表里的偏置都对，不该触发
                OuterBiasCheckWindow = TimeSpan.FromSeconds(600 / K),
                OuterBiasCheckTauHat = 1000 / K,
                OuterBiasCheckWallHat = 120 / K,
                OuterBiasCheckHold = TimeSpan.FromSeconds(600 / K),
                OuterHeatBalanceSpan = TimeSpan.FromSeconds(300 / K),          // 【本地改动】热平衡缓冲（压低那一刻改积分 / 过零封顶）也按压缩时间
                OuterHeatBalanceSlopeSpan = TimeSpan.FromSeconds(180 / K),
                OuterHeatBalanceMinSpan = TimeSpan.FromSeconds(150 / K),
            };
            Loop.SetActuatorMode(1, ActuatorMode.HeaterTec);
            var both = Loop.GetGainSchedule(1, GainTable.Both);
            both.SegmentBoundaryC = double.NaN;
            IEnumerable<(double T, double? B)> bothSeed = bothRows ?? (rows ?? new[] { 20.0, 21.0, 25.0 }).Select(t => (t, (double?)learnedBias));
            foreach (var (sp, b) in bothSeed)
                both.Learn(new GainPoint(sp, new PidGains(9.4, 0.037 * K, 175 / K), 0, 0,
                    new PidGains(2.5, 0.01 * K, 0), 8, SteadyBiasC: b, HeatRatio: 3.5));
            if (heaterRows is not null)
            {
                var heater = Loop.GetGainSchedule(1, GainTable.Heater);
                heater.SegmentBoundaryC = double.NaN;
                foreach (var (sp, b) in heaterRows)
                    heater.Learn(new GainPoint(sp, new PidGains(9.4, 0.037 * K, 175 / K), 0, 0, new PidGains(2.5, 0.01 * K, 0), 8, SteadyBiasC: b));
            }
            Loop.ConfigurePid(1, 9.4, 0.037 * K, 175 / K, 90, invertOutput: false, derivativeFilterSeconds: 8.6 / K);
            Loop.ConfigureCascade(1, 2.5, 0.01 * K, 0, 8);
            Loop.SetStrategy(1, ControlStrategy.Cascade);
            Loop.SetOuterSource(1, () => Math.Round(P.Tr, 2));    // 宇电 0.01 K 分辨率
            Loop.SetHeatBoostAllowed(1, allowed);
            tweak?.Invoke(Loop);
            Loop.ChannelNotice += (ch, _, text) => { if (ch == 1) lock (_notes) _notes.Add(text); };
            Loop.CycleCompleted += res =>
            {
                var c = res.Ch1;
                P.Step(res.Snapshot.Timestamp, c.Active ? c.DutyPercent : 0);
                Dev.Set(1, "TCADJTEMP", (long)Math.Round(P.Tj * 1e5));
                if (c.Active) onCycle?.Invoke(this, c);
                lock (_gate)
                    if (c.Active)
                        _trace.Add((P.SimSeconds, P.Tr, P.Tj, c.InnerSetpointC, c.OuterI, c.HeatBoosting,
                            c.HeatBoostVmCPerMin / K, c.HeatBoostVstarCPerMin / K));
            };
            Loop.Start();
        }

        /// <summary>等到现场 minutes 分钟。通道停了（外环丢了跳掉之类）直接报错；墙钟超过应有的 3 倍 + 1 分钟也报错——别把整轮测试挂死。</summary>
        public async Task UntilMin(double minutes)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(minutes * 60 / K * 3 + 60);
            while (Trace() is var t && (t.Length == 0 || t[^1].T - t[0].T < minutes * 60))
            {
                var at = t.Length == 0 ? 0 : (t[^1].T - t[0].T) / 60;
                if (t.Length > 0 && !Loop.GetChannelStatus(1).Active)
                    throw new InvalidOperationException($"通道 1 在 {at:F1} min 停了：{string.Join(" | ", Notes().TakeLast(2))}");
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException($"等现场 {minutes} min 超时（才到 {at:F1} min）");
                await Task.Delay(20);
            }
        }

        public string[] Notes() { lock (_notes) return _notes.ToArray(); }
        public (double T, double Tr, double Tj, double Isp, double I, bool Boost, double Vm, double Vs)[] Trace() { lock (_gate) return _trace.ToArray(); }

        /// <summary>首次进 ±band（现场分钟）。</summary>
        public double FirstInMin(double sp, double band)
        {
            var t = Trace();
            var hit = t.FirstOrDefault(x => Math.Abs(x.Tr - sp) <= band);
            return hit == default ? double.NaN : (hit.T - t[0].T) / 60;
        }

        public double StayInMin(double sp, double band)
        {
            var t = Trace();
            if (t.Length == 0) return double.NaN;
            var last = t.LastOrDefault(x => Math.Abs(x.Tr - sp) > band);
            return last == default ? 0 : (last.T - t[0].T) / 60;
        }

        public double Overshoot(double sp) => Math.Max(0, Trace().Max(x => x.Tr) - sp);
        public double JacketMax => Trace().Max(x => x.Tj);
        public double MaxJacketOverReactor(double fromMin)
        {
            var t = Trace();
            return t.Where(x => x.T - t[0].T >= fromMin * 60).Max(x => x.Tj - x.Tr);
        }

        public async ValueTask DisposeAsync()
        {
            await Loop.ShutdownAsync();
            Loop.Dispose();
        }
    }

    private void Dump(string name, Rig r, double sp) => Dump(output, name, r, sp);

    internal static void Dump(ITestOutputHelper output, string name, Rig r, double sp)
    {
        var t = r.Trace();
        if (t.Length == 0) { output.WriteLine($"{name}: 没有记录"); return; }
        var t0 = t[0].T;
        output.WriteLine($"── {name}：首进 ±0.1 {r.FirstInMin(sp, 0.1):F1} min、稳进 ±0.05 {r.StayInMin(sp, 0.05):F1} min、冲过 {r.Overshoot(sp):F3}、" +
                         $"夹套最高 {r.JacketMax:F1}、继电器切换 {r.P.Switches} 次");
        var next = 0.0;
        foreach (var x in t)
        {
            var m = (x.T - t0) / 60;
            if (m < next) continue;
            next = Math.Floor(m / 2) * 2 + 2;
            output.WriteLine($"   {m,5:F1} min  Tr {x.Tr,7:F3}  Tj {x.Tj,7:F2}  夹套目标 {x.Isp,7:F2}  外环积分 {x.I,6:F3}" +
                             (x.Boost ? $"  加速 v {x.Vm,6:F3} / v* {x.Vs,6:F3} K/min" : ""));
        }
        foreach (var n in r.Notes().Where(n => n.Contains("升温加速") || n.Contains("积分起点")))
            output.WriteLine($"   · {n}");
    }

    internal static Plant H1() => new(-1.99, 5.03, 5.03, 0.76);

    [Fact]
    public async Task 现场那场_负2升到20_加速比现在快_冲过更小_交回外环()
    {
        await using var s0 = new Rig(H1(), HeatBoostLevel.Off, allowed: true);
        await using var on = new Rig(H1(), HeatBoostLevel.Normal, allowed: true);
        await Task.WhenAll(s0.Loop.StartChannelAsync(1, 20), on.Loop.StartChannelAsync(1, 20));
        await Task.WhenAll(s0.UntilMin(50), on.UntilMin(50));
        Dump("现在的外环", s0, 20);
        Dump("升温加速（正常档、温差限 30 K）", on, 20);

        Assert.DoesNotContain(s0.Notes(), n => n.Contains("升温加速"));
        Assert.Contains(on.Notes(), n => n.StartsWith("通道1升温加速：釜内"));
        Assert.Contains(on.Notes(), n => n.Contains("升温加速交回外环"));
        Assert.DoesNotContain(on.Notes(), n => n.Contains("升温加速退出"));
        Assert.False(on.Loop.IsHeatBoosting(1));

        // python 孪生：现在 30.1 / 46.5 / 0.071；加速（sp + 19、温差 30 K）27.7 / 30.2 / 0.008
        Assert.InRange(s0.Overshoot(20), 0.03, 0.15);
        Assert.InRange(on.Overshoot(20), 0, 0.03);
        Assert.True(on.FirstInMin(20, 0.1) < s0.FirstInMin(20, 0.1) - 1, "首进要早");
        Assert.True(on.StayInMin(20, 0.05) < s0.StayInMin(20, 0.05) - 8, "稳进 ±0.05 要早得多");
        Assert.InRange(on.StayInMin(20, 0.05), 25, 36);
        Assert.True(on.JacketMax <= 20 + 19 + 1.5, $"夹套最高 {on.JacketMax:F2}");
        Assert.True(on.MaxJacketOverReactor(1) <= 30 + 1.0, $"夹套 − 釜内最大 {on.MaxJacketOverReactor(1):F2}");
        Assert.True(s0.JacketMax <= 20 + 8 + 1.2);
    }

    [Fact]
    public async Task TEC接不上_不进加速_照现在的外环升_中途放行再接上()
    {
        await using var blocked = new Rig(H1(), HeatBoostLevel.Normal, allowed: false);
        await using var late = new Rig(H1(), HeatBoostLevel.Normal, allowed: false);
        await Task.WhenAll(blocked.Loop.StartChannelAsync(1, 20), late.Loop.StartChannelAsync(1, 20));
        await late.UntilMin(6);
        Assert.True(late.Loop.HeatBoostWaitingForTec(1));
        late.Loop.SetHeatBoostAllowed(1, true);                  // 冷却水备好了：趋近途中接入
        await Task.WhenAll(blocked.UntilMin(45), late.UntilMin(45));
        Dump("TEC 一直接不上", blocked, 20);
        Dump("6 分钟时放行", late, 20);

        Assert.Contains(blocked.Notes(), n => n.Contains("先不加速：TEC 此刻接不上"));
        Assert.DoesNotContain(blocked.Notes(), n => n.StartsWith("通道1升温加速："));
        Assert.True(blocked.JacketMax <= 20 + 8 + 1.2);
        Assert.True(blocked.Loop.HeatBoostWaitingForTec(1) == false);   // 升到跟前就不再等了
        Assert.Contains(late.Notes(), n => n.StartsWith("通道1升温加速：釜内"));
        Assert.Contains(late.Notes(), n => n.Contains("升温加速交回外环"));
        Assert.InRange(late.Overshoot(20), 0, 0.03);
        Assert.True(late.StayInMin(20, 0.05) < blocked.StayInMin(20, 0.05) - 5);
    }

    [Fact]
    public async Task 加速中TEC接不上了_立即交回外环_积分回学到的偏置_两枪撤防()
    {
        await using var r = new Rig(H1(), HeatBoostLevel.Normal, allowed: true);
        await r.Loop.StartChannelAsync(1, 20);
        await r.UntilMin(10);
        Assert.True(r.Loop.IsHeatBoosting(1));
        r.Loop.SetHeatBoostAllowed(1, false);
        await r.UntilMin(11);
        Assert.False(r.Loop.IsHeatBoosting(1));
        var quit = Assert.Single(r.Notes(), n => n.Contains("升温加速退出"));
        Assert.Contains("TEC 此刻接不上", quit);
        Assert.Contains("→ 1.60", quit);                          // 还在往上推（不在刹车）：积分回 B
        Assert.DoesNotContain("正在刹车", quit);
        await r.UntilMin(40);
        Dump("10 分钟时 TEC 接不上了", r, 20);
        Assert.InRange(r.Overshoot(20), 0, 0.12);
    }

    [Fact]
    public async Task 小台阶_20升到21_也走加速_快且不冲过()
    {
        await using var s0 = new Rig(new Plant(20, 21.6, 21.6, 21.6), HeatBoostLevel.Off, allowed: true);
        await using var on = new Rig(new Plant(20, 21.6, 21.6, 21.6), HeatBoostLevel.Normal, allowed: true);
        await Task.WhenAll(s0.Loop.StartChannelAsync(1, 21), on.Loop.StartChannelAsync(1, 21));
        await Task.WhenAll(s0.UntilMin(32), on.UntilMin(32));
        Dump("现在的外环 20 → 21", s0, 21);
        Dump("升温加速 20 → 21", on, 21);
        Assert.Contains(on.Notes(), n => n.StartsWith("通道1升温加速：釜内"));
        Assert.InRange(on.Overshoot(21), 0, 0.04);
        // 不加速那台原来趋近那一枪进带第一拍就空打、积分往外跑：冲 0.11、28 分钟才稳进 ±0.05，加速快它 5 分钟以上；
        // 那一枪改成等 Tr 真往目标走才打以后它自己也 10 分钟上下稳进、冲 0.05——加速不比它慢就行
        Assert.InRange(on.StayInMin(21, 0.05), 0, 15);
        Assert.True(on.StayInMin(21, 0.05) <= s0.StayInMin(21, 0.05) + 2, $"加速 {on.StayInMin(21, 0.05):F1} min、不加速 {s0.StayInMin(21, 0.05):F1} min");
        Assert.InRange(s0.Overshoot(21), 0, 0.08);
    }

    [Fact]
    public async Task 保守档_刹车更早_夹套上限低()
    {
        await using var r = new Rig(H1(), HeatBoostLevel.Conservative, allowed: true);
        await r.Loop.StartChannelAsync(1, 20);
        await r.UntilMin(45);
        Dump("保守档", r, 20);
        Assert.Contains(r.Notes(), n => n.Contains("保守档"));
        Assert.True(r.JacketMax <= 20 + 15 + 1.5, $"夹套最高 {r.JacketMax:F2}");     // 内环追斜着往上走的目标多冲 1.2 K 上下
        Assert.InRange(r.Overshoot(20), 0, 0.03);
        Assert.InRange(r.StayInMin(20, 0.05), 25, 38);
    }
}

/// <summary>升温加速评审后补的两种（单独一个类，跟 <see cref="HeatBoostTwinTests"/> 并行跑）：刹车中途中止、学到的偏置偏低很多。</summary>
public class HeatBoostTwinAbortTests(ITestOutputHelper output)
{
    [Fact]
    public async Task 刹车中途关了加速_接住此刻的夹套目标_不比不加速冲得多()
    {
        await using var r = new HeatBoostTwinTests.Rig(HeatBoostTwinTests.H1(), HeatBoostLevel.Normal, allowed: true);
        await r.Loop.StartChannelAsync(1, 20);
        await r.UntilMin(18);
        Assert.True(r.Loop.IsHeatBoosting(1));
        r.Loop.HeatBoost = HeatBoostLevel.Off;                   // 操作人在刹车那一段把「升温加速」改成关
        await r.UntilMin(45);
        HeatBoostTwinTests.Dump(output, "18 分钟关了加速", r, 20);
        var quit = Assert.Single(r.Notes(), n => n.Contains("升温加速退出"));
        Assert.Contains("升温加速关着", quit);
        Assert.Contains("正在刹车", quit);
        Assert.InRange(r.Overshoot(20), 0, 0.05);                // 原来积分回 B：0.11 K（评审孪生）；不加速 0.071
        Assert.True(r.StayInMin(20, 0.05) < 36, $"稳进 ±0.05 {r.StayInMin(20, 0.05):F1} min");   // 压低的积分进 1 K 放回 B，不停在前面爬
    }

    [Fact]
    public async Task 离目标1K多时关了加速_压低的积分进1K放回_不停在目标前面爬()
    {
        await using var r = new HeatBoostTwinTests.Rig(HeatBoostTwinTests.H1(), HeatBoostLevel.Normal, allowed: true);
        await r.Loop.StartChannelAsync(1, 20);
        await r.UntilMin(20);
        Assert.True(r.Loop.IsHeatBoosting(1));
        r.Loop.HeatBoost = HeatBoostLevel.Off;
        await r.UntilMin(45);
        HeatBoostTwinTests.Dump(output, "20 分钟关了加速", r, 20);
        Assert.Contains(r.Notes(), n => n.Contains("升温加速退出"));
        Assert.InRange(r.Overshoot(20), 0, 0.05);
        Assert.True(r.StayInMin(20, 0.05) < 36, $"稳进 ±0.05 {r.StayInMin(20, 0.05):F1} min");    // 评审孪生：不放回要 47.5
    }

    [Fact]
    public async Task 学到的偏置偏低3K_卡在目标前面_卡住兜底交还_最后进带()
    {
        await using var r = new HeatBoostTwinTests.Rig(HeatBoostTwinTests.H1(), HeatBoostLevel.Normal, allowed: true, learnedBias: 1.6 - 3);
        await r.Loop.StartChannelAsync(1, 20);
        await r.UntilMin(70);
        HeatBoostTwinTests.Dump(output, "表里偏置 −1.4（真值 +1.6）", r, 20);
        Assert.Contains(r.Notes(), n => n.Contains("升温加速交回外环") && n.Contains("卡了"));
        Assert.False(r.Loop.IsHeatBoosting(1));
        Assert.InRange(r.Overshoot(20), 0, 0.05);
        var last = r.Trace()[^1];
        Assert.InRange(last.Tr, 19.9, 20.1);                     // 带 0.5 时停在 19.4 ℃ 几个钟头
    }
}
