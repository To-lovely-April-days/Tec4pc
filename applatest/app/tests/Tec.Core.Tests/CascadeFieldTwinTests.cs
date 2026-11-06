using TecControl.Core.Control;
using Tec.Drivers.Rd105;
using Xunit;
using Xunit.Abstractions;

namespace Tec.Core.Tests;

/// <summary>
/// 2026-11-03 现场 −15 ℃ 那场（双向挡釜内 8 → −15：满幅降温 20 分钟，然后 −14.3 → −15.0 爬了 25 分钟，47 分钟才进 ±0.1，
/// 到站后掉到 −15.06 慢慢回）拿逐拍 CSV 拟合出来的釜和夹套，接在真回路上跑。时间压缩 <see cref="K"/> 倍。
/// · 夹套（TEC 侧拟合）：dTj/dt = 0.00106 × 加热棒 % − 0.00144 × TEC % + (16.2 − Tj)/360 + (Tr − Tj)/609，占空比过 30 s 一阶滞后；
/// · 釜（双模型）：dTr/dt = (Tj(t − 50 s) − Tr − 0.98)/637 + (W − Tr)/1835，dW/dt = (Tr − W)/433——W 是壁 / 盖那份慢热容，
///   降温后还落在 Tr 后面 2–3 K、往釜里漏热，维持目标要的夹套比长期稳态再冷一截、二三十分钟才退；
/// · 回路照现场：双向挡（加热比 3.5、TEC 满 90 % = PID −25.7）、内环 Kp 10 / Ki 0.0175 / Kd 500（滤波 10 s）、外环 Kp 2.5 / Ki 0.01 / 限幅 8、
///   起步外环积分 = 表里 −15 ℃ 学到的 +1.219。
/// 同一份模型的 python 版逐分钟对得上现场（Tr 差 0.05 K 以内）。
/// 大降温收尾（<see cref="HostControlLoop.OuterFinishGain"/>）在这上面对比开 / 关：时间相关的参数一律按 <see cref="K"/> 缩。
/// </summary>
public class CascadeFieldTwinTests(ITestOutputHelper output)
{
    private const double K = 60;

    private sealed class Plant
    {
        private readonly object _gate = new();
        private double _tj, _tr, _w, _hf, _cf, _t, _min = double.MaxValue, _max = double.MinValue;
        private readonly Queue<(double T, double Tj)> _lag = new();
        private DateTime _last;

        public Plant(double tr0, double tj0, double w0) { _tr = tr0; _tj = tj0; _w = w0; }

        public double Tr { get { lock (_gate) return _tr; } }
        public double Tj { get { lock (_gate) return _tj; } }
        /// <summary>现场时间（s，按 K 放大）。</summary>
        public double SimSeconds { get { lock (_gate) return _t; } }
        /// <summary>从 <see cref="WatchFrom"/> 起 Tr 的最低 / 最高。</summary>
        public double Min { get { lock (_gate) return _min; } }
        public double Max { get { lock (_gate) return _max; } }
        public double WatchFrom = double.MaxValue;

        public void Step(DateTime at, double deviceDuty)
        {
            lock (_gate)
            {
                if (_last == default) { _last = at; return; }
                var dt = Math.Clamp((at - _last).TotalSeconds, 0, 0.5) * K;   // 回路卡一下也不一口气跳太远
                _last = at;
                var heat = Math.Max(deviceDuty, 0);
                var cool = Math.Max(-deviceDuty, 0);
                var n = Math.Max(1, (int)Math.Ceiling(dt / 0.5));
                var h = dt / n;
                for (var i = 0; i < n; i++)
                {
                    _hf += (heat - _hf) * h / 30;
                    _cf += (cool - _cf) * h / 30;
                    _tj += (0.00106 * _hf - 0.00144 * _cf + (16.2 - _tj) / 360 + (_tr - _tj) / 608.7) * h;
                    _t += h;
                    _lag.Enqueue((_t, _tj));
                    var tjd = _lag.Peek().Tj;
                    while (_lag.Count > 1 && _t - _lag.Peek().T > 50) tjd = _lag.Dequeue().Tj;
                    var dtr = (tjd - _tr - 0.98) / 637.2 + (_w - _tr) / 1834.5;
                    _w += (_tr - _w) / 432.5 * h;
                    _tr += dtr * h;
                }
                if (_t >= WatchFrom) { _min = Math.Min(_min, _tr); _max = Math.Max(_max, _tr); }
            }
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        public readonly FakeRd105Device Dev;
        public readonly HostControlLoop Loop;
        public readonly Plant P;
        private readonly object _gate = new();
        private readonly List<string> _notes = new();
        private readonly List<(double T, double Tr, double Tj, double Isp, double I, double F)> _trace = new();
        private ChannelCycleInfo? _last;

        public Rig(Plant plant, double learnedBias, Action<HostControlLoop>? tweak = null, double outerKp = 2.5)
        {
            P = plant;
            Dev = new FakeRd105Device { Thermal = false };
            Dev.Set(1, "TCADJTEMP", (long)Math.Round(plant.Tj * 1e5));
            Dev.Set(1, "LIMITED", 90);
            Dev.Open();
            Loop = new HostControlLoop(new Rd105Link(Dev).Controller)
            {
                Period = TimeSpan.FromMilliseconds(50),
                OuterPeriod = TimeSpan.FromMilliseconds(100),
                SyncMode = ChannelSyncMode.Independent,
                ManagePwmFrequency = false,
                HeatingEffectivenessRatio = 3.5,
                OuterApproachWindow = TimeSpan.FromSeconds(60 / K),
                OuterApproachFreezeCPerMin = 0.05 * K,
                OuterApproachHorizon = TimeSpan.FromSeconds(15 * 60 / K),
                OuterApproachMinCPerMin = 0.015 * K,
                OuterSteadyCyclesToLearn = 10,                          // 现场 30 拍 × 2 s；这里外环 6 s 一拍
                OuterStartQuietWindow = TimeSpan.Zero,
                OuterFinishSaturation = TimeSpan.FromSeconds(300 / K),
                OuterFinishCrawlHorizon = TimeSpan.FromSeconds(3 * 60 / K),
                OuterFinishRamp = TimeSpan.FromSeconds(120 / K),
                OuterFinishSettleTime = TimeSpan.FromSeconds(300 / K),
                OuterFinishMaxHold = TimeSpan.FromSeconds(20 * 60 / K),
                OuterFinishRelease = TimeSpan.FromSeconds(20 * 60 / K),
                OuterFinishAwayCPerMin = 0.05 * K,
                OuterFinishAwayRelease = TimeSpan.FromSeconds(600 / K),
                OuterFinishShotTau = TimeSpan.FromSeconds(400 / K),
                OuterFinishShotWindow = TimeSpan.FromSeconds(120 / K),
                OuterFinishShotMinSpan = TimeSpan.FromSeconds(60 / K),
                OuterFinishCarry = TimeSpan.FromSeconds(20 * 60 / K),
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
            both.Learn(new GainPoint(-15, new PidGains(10, 0.0175 * K, 500 / K), 0, 0,
                new PidGains(outerKp, 0.01 * K, 0), 8, SteadyBiasC: learnedBias, HeatRatio: 3.5));
            Loop.ConfigurePid(1, 10, 0.0175 * K, 500 / K, 90, invertOutput: false, derivativeFilterSeconds: 10 / K);
            Loop.ConfigureCascade(1, outerKp, 0.01 * K, 0, 8);
            Loop.SetStrategy(1, ControlStrategy.Cascade);
            Loop.SetOuterSource(1, () => P.Tr);
            tweak?.Invoke(Loop);
            Loop.ChannelNotice += (ch, _, text) => { if (ch == 1) lock (_notes) _notes.Add(text); };
            Loop.CycleCompleted += res =>
            {
                var c = res.Ch1;
                P.Step(res.Snapshot.Timestamp, c.Active ? c.DutyPercent : 0);
                Dev.Set(1, "TCADJTEMP", (long)Math.Round(P.Tj * 1e5));
                lock (_gate)
                {
                    _last = c;
                    if (c.Active) _trace.Add((P.SimSeconds, P.Tr, P.Tj, c.InnerSetpointC, c.OuterI, c.OuterFinishC));
                }
            };
            Loop.Start();
        }

        public ChannelCycleInfo? Last { get { lock (_gate) return _last; } }

        /// <summary>等到现场时间（从起步算）到了这么多分钟。</summary>
        public async Task UntilMin(double minutes)
        {
            while (Trace() is var t && (t.Length == 0 || t[^1].T - t[0].T < minutes * 60)) await Task.Delay(20);
        }
        public string[] Notes() { lock (_notes) return _notes.ToArray(); }
        public (double T, double Tr, double Tj, double Isp, double I, double F)[] Trace() { lock (_gate) return _trace.ToArray(); }

        /// <summary>从起步算（现场分钟）Tr 最后一次在 ±band 外的时刻——之后一直在带里。</summary>
        public double StayInMin(double sp, double band)
        {
            var t = Trace();
            if (t.Length == 0) return double.NaN;
            var t0 = t[0].T;
            var last = t.LastOrDefault(x => Math.Abs(x.Tr - sp) > band);
            return last == default ? 0 : (last.T - t0) / 60;
        }

        /// <summary>冲过目标多少（K）：从上面降下来的看 Tr 最低比目标低多少。</summary>
        public double Overshoot(double sp)
        {
            var t = Trace();
            if (t.Length == 0) return double.NaN;
            return t[0].Tr > sp ? Math.Max(0, sp - t.Min(x => x.Tr)) : Math.Max(0, t.Max(x => x.Tr) - sp);
        }

        public async ValueTask DisposeAsync()
        {
            await Loop.ShutdownAsync();
            Loop.Dispose();
        }
    }

    private void Dump(string name, Rig r, double sp)
    {
        var t = r.Trace();
        if (t.Length == 0) { output.WriteLine($"{name}: 没有记录"); return; }
        var t0 = t[0].T;
        output.WriteLine($"── {name}：稳进 ±0.1 {r.StayInMin(sp, 0.1):F1} min、±0.05 {r.StayInMin(sp, 0.05):F1} min、冲过 {r.Overshoot(sp):F3}" +
                         (r.P.Min <= r.P.Max ? $"、{r.P.WatchFrom / 60:0} 分钟起 Tr {r.P.Min:F3} … {r.P.Max:F3}" : ""));
        var next = 0.0;
        foreach (var x in t)
        {
            var m = (x.T - t0) / 60;
            if (m < next) continue;
            next = Math.Floor(m / 2) * 2 + 2;
            output.WriteLine($"   {m,5:F1} min  Tr {x.Tr,7:F3}  Tj {x.Tj,7:F2}  夹套目标 {x.Isp,7:F2}  外环积分 {x.I,6:F3}  收尾多给 {x.F,6:F3}");
        }
        foreach (var n in r.Notes().Where(n => n.Contains("收尾") || n.Contains("过零") || n.Contains("在爬")))
            output.WriteLine($"   · {n}");
    }

    private static readonly Action<HostControlLoop> Off = l => l.OuterFinishGain = 0;

    [Fact]
    public async Task 现场复现_釜内8降到负15_收尾加比例_快且不冲过()
    {
        // 同一场开 / 关一起跑：现场那场（表里 −15 ℃ 学到 +1.219）和表里存着漂移时学到的 +0.80 那种
        Plant Field() => new(7.92, 12.1, 5.55) { WatchFrom = 50 * 60 };     // 开着的 42–47 分钟进 ±0.05：50 分钟起看 Tr 摆多少
        await using var before = new Rig(Field(), 1.219, Off);
        await using var after = new Rig(Field(), 1.219);
        await using var before08 = new Rig(Field(), 0.80, Off);
        await using var after08 = new Rig(Field(), 0.80);
        Rig[] all = [before, after, before08, after08];
        await Task.Delay(300);
        foreach (var r in all) await r.Loop.StartChannelAsync(1, -15);
        double? Learned(Rig r) => r.Loop.GetGainSchedule(1, GainTable.Both).SteadyBiasAt(-15);
        await Task.Delay(TimeSpan.FromSeconds(60 * 60 / K));                 // 现场 60 分钟：开着的已稳住、多给的还没退完
        var learned60 = (Before: Learned(before), After: Learned(after));
        await Task.Delay(TimeSpan.FromSeconds(15 * 60 / K));                 // 现场 75 分钟
        Dump("关（现在）", before, -15);
        Dump("开", after, -15);
        Dump("关，表里 +0.80", before08, -15);
        Dump("开，表里 +0.80", after08, -15);
        output.WriteLine($"表里 −15 ℃ 稳态偏置：60 分钟 关 {learned60.Before:F3} / 开 {learned60.After:F3}；75 分钟 关 {Learned(before):F3} / 开 {Learned(after):F3}");

        Assert.InRange(before.StayInMin(-15, 0.1), 38, 60);                  // 复现出来了：现场 47 分钟
        Assert.Contains(after.Notes(), n => n.Contains("大降温收尾已武装"));
        Assert.Contains(after.Notes(), n => n.Contains("在爬"));
        Assert.DoesNotContain(before.Notes(), n => n.Contains("大降温收尾"));
        Assert.True(after.StayInMin(-15, 0.1) <= before.StayInMin(-15, 0.1) - 3,
            $"进 ±0.1：关 {before.StayInMin(-15, 0.1):F1}、开 {after.StayInMin(-15, 0.1):F1} min");
        Assert.True(after.StayInMin(-15, 0.05) <= before.StayInMin(-15, 0.05) - 1.5,     // 过零那一枪不封顶的话进 ±0.05 比关着还晚
            $"进 ±0.05：关 {before.StayInMin(-15, 0.05):F1}、开 {after.StayInMin(-15, 0.05):F1} min");
        Assert.True(after.Overshoot(-15) <= before.Overshoot(-15) + 0.01, $"冲过：关 {before.Overshoot(-15):F3}、开 {after.Overshoot(-15):F3}");
        Assert.InRange(after.P.Min, -15.05, -14.95);                           // 到站后不往下偏、也不弹回去（不封顶的话 55 分钟前后弹到 −14.93）
        Assert.InRange(after.P.Max, -15.05, -14.95);
        // 收尾期不学：关着的爬行段里就把漏热那份记进了表（≈ 0.77）；开着的 60 分钟时表里还是原值，多给的退完以后才学、学到 ≈ 0.95（真值 0.98）
        Assert.True(learned60.Before < 1.1, $"关着的 60 分钟时表里 {learned60.Before}");
        Assert.Equal(1.219, learned60.After!.Value, 3);
        Assert.InRange(Learned(after)!.Value, 0.88, 1.05);

        Assert.True(after08.StayInMin(-15, 0.1) <= before08.StayInMin(-15, 0.1) - 2,
            $"表里 +0.80 进 ±0.1：关 {before08.StayInMin(-15, 0.1):F1}、开 {after08.StayInMin(-15, 0.1):F1} min");
        Assert.True(after08.StayInMin(-15, 0.05) <= before08.StayInMin(-15, 0.05) - 2,
            $"表里 +0.80 进 ±0.05：关 {before08.StayInMin(-15, 0.05):F1}、开 {after08.StayInMin(-15, 0.05):F1} min");
        Assert.True(after08.Overshoot(-15) <= before08.Overshoot(-15) + 0.01, $"表里 +0.80 冲过：关 {before08.Overshoot(-15):F3}、开 {after08.Overshoot(-15):F3}");
        Assert.InRange(after08.P.Min, -15.05, -14.95);
        Assert.InRange(after08.P.Max, -15.05, -14.95);
    }

    [Fact]
    public async Task 目标高于负10不武装()
    {
        // 8 → −5：满制冷也顶够了、走过也够远，但目标在中温段——孪生里换一台釜 / 夹套慢一点时这一下会来回摆，不放开
        Plant P() => new(7.92, 12.1, 5.55);
        await using var on = new Rig(P(), 1.219);
        await Task.Delay(300);
        await on.Loop.StartChannelAsync(1, -5);
        await Task.Delay(TimeSpan.FromSeconds(30 * 60 / K));
        Dump("开，目标 −5", on, -5);
        Assert.DoesNotContain(on.Notes(), n => n.Contains("大降温收尾"));
        Assert.All(on.Trace(), x => Assert.Equal(0, x.F));
    }

    [Fact]
    public async Task 收尾中改目标_离得近带着慢慢退_离得远一下撤_再改远连带着的一起撤_外环Kp大不多给()
    {
        Plant P() => new(7.92, 12.1, 5.55);
        await using var near = new Rig(P(), 1.219);
        await using var far = new Rig(P(), 1.219);
        await using var nearThenFar = new Rig(P(), 1.219);
        await using var bigKp = new Rig(P(), 1.219, outerKp: 6);
        await Task.Delay(300);
        foreach (var r in new[] { near, far, nearThenFar, bigKp }) await r.Loop.StartChannelAsync(1, -15);

        // 现场 29 分钟：在爬、多给着（开着的这时多给 −0.3 ~ −0.7 K）。近的改到釜内附近，远的改到 2 K 开外
        await Task.WhenAll(near.UntilMin(29), far.UntilMin(29), nearThenFar.UntilMin(29));
        var nearAt = near.P.SimSeconds;
        near.Loop.SetSetpoint(1, Math.Round(near.P.Tr, 1) - 0.2);
        var farAt = far.P.SimSeconds;
        far.Loop.SetSetpoint(1, -12);
        nearThenFar.Loop.SetSetpoint(1, Math.Round(nearThenFar.P.Tr, 1) - 0.2);
        await nearThenFar.UntilMin(32);                                       // 带着退了 3 分钟，再改到 2 K 开外：还在退的那份也撤
        var thenFarAt = nearThenFar.P.SimSeconds;
        nearThenFar.Loop.SetSetpoint(1, -12);
        await Task.WhenAll(near.UntilMin(52), far.UntilMin(35), nearThenFar.UntilMin(36), bigKp.UntilMin(45));
        Dump("近处改目标", near, near.Loop.GetChannelStatus(1).SetpointC);

        var nt = near.Trace();
        var before = nt.Last(x => x.T < nearAt).F;
        Assert.True(before < -0.1, $"改目标前多给 {before:F3}，没在爬");
        Assert.Contains(near.Notes(), n => n.Contains("不一下撤掉"));
        var after = nt.First(x => x.T > nearAt + 30).F;                       // 改完半分钟：还是那么多，一点点退
        Assert.InRange(after / before, 0.9, 1.0);
        var mid = nt.First(x => x.T > nearAt + 10 * 60).F;                  // 10 分钟：退了一半上下
        Assert.InRange(mid / before, 0.4, 0.6);
        Assert.All(nt.Where(x => x.T > nearAt + 21 * 60), x => Assert.Equal(0, x.F));

        var ft = far.Trace();
        Assert.True(ft.Last(x => x.T < farAt).F < -0.1);
        Assert.DoesNotContain(far.Notes(), n => n.Contains("不一下撤掉"));
        Assert.All(ft.Where(x => x.T > farAt + 30), x => Assert.Equal(0, x.F));     // 新的一段趋近：一下撤掉

        var tt = nearThenFar.Trace();
        Assert.True(tt.Last(x => x.T < thenFarAt).F < -0.1, "近处改目标之后应该还带着在退");
        Assert.All(tt.Where(x => x.T > thenFarAt + 30), x => Assert.Equal(0, x.F));

        // 外环 Kp 6：加上多给的就超过 5 了，一点不多给（照样武装、照样封顶过零那一枪、照样收尾期不学）
        Assert.Contains(bigKp.Notes(), n => n.Contains("大降温收尾已武装"));
        Assert.All(bigKp.Trace(), x => Assert.Equal(0, x.F));
    }
}
