using System.Text.RegularExpressions;
using TecControl.Core.Control;
using Tec.Drivers.Rd105;
using Xunit;
using Xunit.Abstractions;

namespace Tec.Core.Tests;

/// <summary>
/// 串级外环「到站不冲过」那几条（2026-11-02 现场：双向挡釜内 32 → 20 ℃，冲到 18.94、十二分钟才回来）：
/// · 趋近不积分：误差正在缩小（釜内往目标走）时外环积分不往外攒；
/// · 内环跟不上不积分：夹套还没追上外环要的、积分往外环正推的方向继续攒不行；
/// · 两条都只拦积分往参照值（信得过的偏置）外面走，往回退（退债）不拦；
/// · 起步积分起点：学到的稳态偏置 → 起步前稳着时的「夹套 − 釜内」→ 往来的方向刹车（离得近按距离封顶）→ 0；
///   积分还停在起步预置上时改目标按新方向重摆刹车，积过一下就不再重摆；
/// · 坡尾回基：斜坡停下来 / 坡中途直接改目标时积分回到起坡时的值，只往停在目标前面那一侧回。
/// 直接架在假 RD105 上、不开会话；釜用一阶 + 一小段滞后的模型在回路每拍的回调里推（夹套取那一拍的快照），
/// 时间按 60 倍压缩：现场拟合的釜 τ ≈ 675 s、滞后 ≈ 20 s、夹套跟随 ≈ 35 s、稳态偏置 +1.3 K → 这里 11.25 s / 0.33 s / 0.58 s。
/// 外环 Ki、趋近门槛（K/min）跟着乘 60，时间窗除 60。要按时间走的 Tr 都在回路回调里按快照时刻推（回路卡一下 Tr 也跟着停，不会假趋近）。
/// </summary>
public class OuterApproachTests(ITestOutputHelper output)
{
    private const double K = 60;                       // 时间压缩倍数

    /// <summary>釜：τ·dTr/dt = 滞后后的夹套 − Tr − 偏置（偏置 > 0 = 釜往外散热，夹套得比釜高这么多才稳得住）。</summary>
    private sealed class Reactor(double tr0, double tj0, double bias)
    {
        private readonly object _gate = new();
        private double _tr = tr0, _z = tj0, _min = tr0;
        private DateTime _last;
        private const double Tau = 675 / K, Lag = 20 / K;

        public double Tr { get { lock (_gate) return _tr; } }
        public double Min { get { lock (_gate) return _min; } }

        public void Step(DateTime at, double tj)
        {
            lock (_gate)
            {
                if (_last == default || double.IsNaN(tj)) { _last = at; return; }
                var dt = Math.Clamp((at - _last).TotalSeconds, 0, 1);
                _last = at;
                // 分小步推，回路一拍偶尔拖长也不失真
                var n = Math.Max(1, (int)Math.Ceiling(dt / 0.01));
                var h = dt / n;
                for (var i = 0; i < n; i++)
                {
                    _z += (tj - _z) / Lag * h;
                    _tr += (_z - _tr - bias) / Tau * h;
                }
                _min = Math.Min(_min, _tr);
            }
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        public readonly FakeRd105Device Dev;
        public readonly HostControlLoop Loop;
        public readonly Reactor? R;
        private readonly List<string> _notes = new();
        private readonly object _gate = new();
        private ChannelCycleInfo? _last;
        private double _tr;

        /// <summary>回路每拍回调里调（回路线程，参数是那一拍快照的时刻）：按回路的时间推 Tr。</summary>
        public volatile Action<DateTime>? OnCycle;

        /// <summary>外环被控量：有釜模型就是模型的 Tr，没有就是测试 / OnCycle 直接写的数。</summary>
        public double Tr
        {
            get => R?.Tr ?? Volatile.Read(ref _tr);
            set => Volatile.Write(ref _tr, value);
        }

        public ChannelCycleInfo? Last { get { lock (_gate) return _last; } }
        public double I => Last!.OuterI;
        public string[] Notes() { lock (_notes) return _notes.ToArray(); }

        /// <param name="thermal">夹套按占空比走（G 让内环 Kp 20 下跟随时间常数 ≈ 35 s ÷ K）；false = 夹套钉在 tj0 不动。</param>
        /// <param name="rules">false = 这次新加的几条全关（趋近不积分、内环跟不上不积分、刹车、起步估偏置），回到原来的样子。</param>
        /// <param name="quiet">起步估偏置开不开（要起步前稳够 2 s）；缺省关，免得测刹车 / 冻结的用例被调度拖长时意外走了估偏置。</param>
        public Rig(double tr0, double tj0, Reactor? reactor = null, bool thermal = true, bool rules = true, bool quiet = false)
        {
            Dev = new FakeRd105Device { Thermal = thermal, GainCPerSec = 100 / (20 * 35 / K), LeakPerSec = 0 };
            Dev.Set(1, "TCADJTEMP", (long)Math.Round(tj0 * 1e5));
            Dev.Set(1, "LIMITED", 90);
            Dev.Open();
            R = reactor;
            _tr = tr0;
            Loop = new HostControlLoop(new Rd105Link(Dev).Controller)
            {
                Period = TimeSpan.FromMilliseconds(50),
                OuterPeriod = TimeSpan.FromMilliseconds(200),
                SyncMode = ChannelSyncMode.Independent,
                ManagePwmFrequency = false,
                OuterApproachWindow = TimeSpan.FromSeconds(60 / K),
                OuterApproachFreezeCPerMin = rules ? 0.05 * K : 0,
                OuterApproachHorizon = TimeSpan.FromSeconds(15 * 60 / K),
                OuterApproachMinCPerMin = 0.015 * K,
                OuterInnerLagFreezeC = rules ? 0.5 : 0,
                OuterBrakeBiasC = rules ? 1.5 : 0,
                OuterStartQuietWindow = rules && quiet ? TimeSpan.FromSeconds(120 / K) : TimeSpan.Zero,
                OuterStartAverage = TimeSpan.FromSeconds(30 / K),
            };
            Loop.ConfigurePid(1, 20, 0.05, 0, 90, invertOutput: false);
            Loop.ConfigureCascade(1, 2.5, 0.013 * K, 0, 8);
            Loop.SetStrategy(1, ControlStrategy.Cascade);
            Loop.SetOuterSource(1, () => Tr);
            Loop.ChannelNotice += (ch, _, text) => { if (ch == 1) lock (_notes) _notes.Add(text); };
            Loop.CycleCompleted += res =>
            {
                R?.Step(res.Snapshot.Timestamp, res.Snapshot.Temp1C);
                OnCycle?.Invoke(res.Snapshot.Timestamp);
                lock (_gate) _last = res.Ch1;
            };
            Loop.Start();
        }

        public async ValueTask DisposeAsync()
        {
            await Loop.ShutdownAsync();
            Loop.Dispose();
        }
    }

    private static async Task WaitUntil(Func<bool> cond, int ms, Func<string> what)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond())
        {
            Assert.True(DateTime.UtcNow < deadline, "等太久：" + what());
            await Task.Delay(20);
        }
    }

    /// <summary>起步之后等外环算过（回调里 OuterI 有数）。</summary>
    private static Task Outer(Rig r) => WaitUntil(() => r.Last is { Active: true } l && !double.IsNaN(l.OuterI), 3000, () => "外环没算");

    private static double StartBias(Rig r)
    {
        var line = r.Notes().First(n => n.Contains("串级外环积分起点"));
        return double.Parse(Regex.Match(line, @"积分起点 ([+-]?[\d.]+) ℃").Groups[1].Value);
    }

    [Fact]
    public async Task 现场复现_釜内32降到20_没学到偏置_原来冲下去一度_现在不冲过()
    {
        // 三台一起跑（各自一个回路线程）：原来的样子 / 起步前不稳只能刹车 / 起步前稳着按「夹套 − 釜内」估
        const double b = 1.3;
        await using var old = new Rig(32, 32 + b, new Reactor(32, 32 + b, b), rules: false);
        await using var brake = new Rig(32, 32 + b, new Reactor(32, 32 + b, b));
        await using var quiet = new Rig(32, 32 + b, new Reactor(32, 32 + b, b), quiet: true);
        await Task.Delay(TimeSpan.FromSeconds(120 / K + 1.5));  // 起步前没控、夹套不动，釜稳在 32（33.3 − 32 − 1.3 = 0）；估偏置要稳够 2 s

        await Task.WhenAll(old.Loop.StartChannelAsync(1, 20), brake.Loop.StartChannelAsync(1, 20), quiet.Loop.StartChannelAsync(1, 20));
        Assert.Equal(0, StartBias(old));
        Assert.Equal(1.5, StartBias(brake), 2);
        Assert.Contains(brake.Notes(), n => n.Contains("刹车"));
        Assert.Equal(b, StartBias(quiet), 1);
        Assert.Contains(quiet.Notes(), n => n.Contains("按「夹套 − 釜内」估"));

        await Task.Delay(TimeSpan.FromSeconds(1500 / K));       // 现场 25 分钟

        string Say(Rig r) => $"最低 {r.R!.Min:F3}、此刻 {r.R.Tr:F3}、外环积分 {r.Last?.OuterI:F2}";
        output.WriteLine($"原来：{Say(old)}\n刹车：{Say(brake)}\n估偏置：{Say(quiet)}");
        Assert.True(20 - old.R!.Min > 0.5, "原来的样子该冲下去（场景没复现出来就验不了）：" + Say(old));
        Assert.True(20 - brake.R!.Min < 0.15, "刹车起步冲过了：" + Say(brake));
        Assert.True(20 - quiet.R!.Min < 0.15, "估偏置起步冲过了：" + Say(quiet));
        Assert.True(Math.Abs(brake.R.Tr - 20) < 0.3, "刹车起步没到站：" + Say(brake));
        Assert.True(Math.Abs(quiet.R.Tr - 20) < 0.3, "估偏置起步没到站：" + Say(quiet));
    }

    [Fact]
    public async Task 起步积分起点_学到的优先_刹车按距离封顶_离得太近不加()
    {
        await using var learned = new Rig(32, 25, thermal: false);
        var tec = learned.Loop.GetGainSchedule(1, ActuatorMode.Bidirectional);
        tec.SegmentBoundaryC = double.NaN;
        tec.Learn(new GainPoint(20, new PidGains(20, 0.05, 0), 0, 0, new PidGains(2.5, 0.013 * K, 0), 8, SteadyBiasC: 2.0));
        await learned.Loop.StartChannelAsync(1, 20);
        Assert.Equal(2.0, StartBias(learned), 2);
        Assert.Contains(learned.Notes(), n => n.Contains("学到的稳态偏置"));

        await using var near = new Rig(20.6, 25, thermal: false);
        await Task.Delay(200);
        await near.Loop.StartChannelAsync(1, 20);
        Assert.Equal(3.5 * 0.6 / 2, StartBias(near), 2);        // (1 + Kp) × 距离 ÷ 2 = 1.05 < 1.5

        await using var below = new Rig(17, 25, thermal: false);
        await Task.Delay(200);
        await below.Loop.StartChannelAsync(1, 20);
        Assert.Equal(-1.5, StartBias(below), 2);                // 升温趋近：往下刹

        await using var close = new Rig(20.3, 25, thermal: false);
        await Task.Delay(200);
        await close.Loop.StartChannelAsync(1, 20);
        Assert.Equal(0, StartBias(close));
        Assert.Contains(close.Notes(), n => n.Contains("不到 0.5 K"));
    }

    [Fact]
    public async Task 积分还停在起步预置上时改目标_刹车按新方向重摆_积过一下就不再重摆()
    {
        // 夹套钉住（跟不上那条把积分按在预置上）：釜内 32 → 目标 20 起步刹 +1.5，再改成 40（反过来要升温）→ 重摆成 −1.5
        await using var r = new Rig(32, 25, thermal: false);
        await Task.Delay(200);
        await r.Loop.StartChannelAsync(1, 20);
        Assert.Equal(1.5, StartBias(r), 2);
        await Outer(r);
        r.Loop.SetSetpoint(1, 40);
        await WaitUntil(() => Math.Abs(r.I + 1.5) < 0.05, 3000, () => $"刹车没重摆：积分 {r.I}");
        Assert.Contains(r.Notes(), n => n.Contains("外环刹车 +1.5 → -1.5 K"));

        // 夹套跟得上：釜内 29 → 目标 30 起步刹 −1.5，Tr 不动、积分往上积了一下——刹车被吸收了，再改目标 20 不重摆
        await using var moved = new Rig(29, 31, thermal: true);
        await Task.Delay(200);
        await moved.Loop.StartChannelAsync(1, 30);
        Assert.Equal(-1.5, StartBias(moved), 2);
        await WaitUntil(() => moved.Last is { Active: true } l && l.OuterI > -1.2, 5000, () => $"积分没积：{moved.Last?.OuterI}");
        moved.Loop.SetSetpoint(1, 20);                           // 离 9 K：带外，积分不动
        await Task.Delay(1000);
        var i = moved.I;
        await Task.Delay(500);
        Assert.Equal(i, moved.I, 2);
        Assert.DoesNotContain(moved.Notes(), n => n.Contains("外环刹车"));
    }

    [Fact]
    public async Task 内环跟不上不积分_往外不攒_往参照值退照退_退到参照值就停()
    {
        // 夹套钉在 25 不动（追不上），釜内 29、目标 30：外环要往上推、夹套落在下面 → 积分不往上攒
        await using var held = new Rig(29, 25, thermal: false);
        await Task.Delay(200);
        await held.Loop.StartChannelAsync(1, 30);
        await Outer(held);
        var i0 = held.I;
        await Task.Delay(2000);
        Assert.Equal(i0, held.I, 3);

        // 同样的局面把这条关了：积分照攒（对照，证明上面是这条拦的）
        await using var off = new Rig(29, 25, thermal: false);
        off.Loop.OuterInnerLagFreezeC = 0;
        await Task.Delay(200);
        await off.Loop.StartChannelAsync(1, 30);
        await Outer(off);
        var j0 = off.I;
        await Task.Delay(2000);
        Assert.True(off.I > j0 + 0.5, $"关了这条积分该攒：{j0:F2} → {off.I:F2}");

        // 退债：夹套钉在 40（比夹套目标高）、釜内 29 目标 30：积分往上攒到 +1 以上（参照 0）。再让釜内过头到 31（要往下）：
        // 夹套还在 40、落在要推的那边，但积分在参照值上方、往下是往回退——不拦，退到参照值 0 附近就停（再往下才是往外攒）
        await using var back = new Rig(29, 40, thermal: false);
        back.Loop.OuterBrakeBiasC = 0;
        await Task.Delay(200);
        await back.Loop.StartChannelAsync(1, 30);
        await WaitUntil(() => back.Last is { Active: true } l && l.OuterI > 1.2, 5000, () => $"积分没攒起来：{back.Last?.OuterI}");
        back.Tr = 31;
        await WaitUntil(() => back.I < 0.1, 5000, () => $"退债被拦了：积分 {back.I:F2}");
        await Task.Delay(1500);
        Assert.True(back.I > -0.25, $"退过参照值还在往外攒：积分 {back.I:F2}");
    }

    [Fact]
    public async Task 积分分离照旧_带外不积_关了才积()
    {
        // 新几条都不掺和：夹套钉在 50（比夹套目标高，不算跟不上）、Tr 不动（不算趋近）、不刹车；偏置上限 30 让 P 在 e = 5 时不顶限
        async Task<Rig> Make(double band)
        {
            var r = new Rig(25, 50, thermal: false);
            r.Loop.OuterBrakeBiasC = 0;
            r.Loop.OuterIntegralBandC = band;
            r.Loop.ConfigureCascade(1, 2.5, 0.013 * K, 0, 30);
            await r.Loop.StartChannelAsync(1, 30);
            await Outer(r);
            return r;
        }
        await using var on = await Make(3);
        var i0 = on.I;
        await Task.Delay(2000);
        Assert.Equal(i0, on.I, 3);

        await using var off = await Make(double.NaN);
        var j0 = off.I;
        await Task.Delay(2000);
        Assert.True(off.I > j0 + 0.5, $"关了积分分离该积：{j0:F2} → {off.I:F2}");
    }

    [Fact]
    public async Task 趋近不积分_釜内往目标走时积分不动_停住了才积()
    {
        // 只看趋近的速度门槛这一条：内环跟不上那条关了、不刹车、按到站时间算的那条关了（它单独在下面那条验）
        await using var r = new Rig(22.5, 22.5, thermal: true);
        r.Loop.OuterInnerLagFreezeC = 0;
        r.Loop.OuterBrakeBiasC = 0;
        r.Loop.OuterApproachHorizon = TimeSpan.Zero;
        await r.Loop.StartChannelAsync(1, 20);
        // Tr 按回路的时间每秒往下 0.4 K（24 K/min，门槛 3 K/min），到 20.6 停住
        DateTime? t0 = null;
        r.OnCycle = at => { t0 ??= at; r.Tr = Math.Max(20.6, 22.5 - 0.4 * (at - t0.Value).TotalSeconds); };
        await Task.Delay(1500);                                  // 窗（1 s）填满之后
        var i1 = r.I;
        await Task.Delay(1500);
        Assert.True(r.Tr > 20.65, "釜内走得太快，测不到趋近段");
        Assert.True(Math.Abs(r.I - i1) < 0.02, $"往目标走时积分动了：{i1:F3} → {r.I:F3}");

        await WaitUntil(() => r.Tr <= 20.6, 10000, () => $"Tr 没走到 20.6：{r.Tr}");   // 停在 20.6（e = −0.6）
        await Task.Delay(2500);                                  // 窗里不再缩小
        Assert.True(r.I < i1 - 0.3, $"停住了积分该往下攒：{i1:F2} → {r.I:F2}");
    }

    [Fact]
    public async Task 爬得慢但照这速度一会儿就到也算趋近_积分不往外攒_停住了照积()
    {
        // 2026-11-03 现场那段：釜内每分钟只靠近 0.02–0.05 K（低于 0.05 的速度门槛），但剩下的几分钟就走完——积分不该往外漂。
        // 这里 Tr 每分钟靠近 2.4 K（压缩后的门槛 3 K/min、最低 0.9 K/min），从 e = −0.30 起照这速度 7.5 s 到（到站时限 15 s），走到 20.15 停住。
        // Tr 在回路读外环被控量那一刻按墙钟算（不经回调慢一拍）：回路卡一下，量出来的斜率也不失真
        async Task<Rig> Crawl(bool horizon)
        {
            var r = new Rig(20.3, 20.3, thermal: true);
            r.Loop.OuterInnerLagFreezeC = 0;
            r.Loop.OuterBrakeBiasC = 0;
            if (!horizon) r.Loop.OuterApproachHorizon = TimeSpan.Zero;
            var t0 = DateTime.Now;
            r.Loop.SetOuterSource(1, () =>
            {
                var tr = Math.Max(20.15, 20.3 - 0.04 * (DateTime.Now - t0).TotalSeconds);
                r.Tr = tr;
                return tr;
            });
            await r.Loop.StartChannelAsync(1, 20);
            return r;
        }
        // 各看各的 Tr：20.24 → 20.18 这 1.5 s 里（窗早满了）积分动了多少
        static async Task<(double Before, double After)> CrawlSpan(Rig r)
        {
            await WaitUntil(() => r.Tr <= 20.24, 10000, () => $"Tr 没走到 20.24：{r.Tr}");
            var a = r.I;
            await WaitUntil(() => r.Tr <= 20.18, 10000, () => $"Tr 没走到 20.18：{r.Tr}");
            return (a, r.I);
        }
        await using var on = await Crawl(horizon: true);
        await using var off = await Crawl(horizon: false);
        var spans = await Task.WhenAll(CrawlSpan(on), CrawlSpan(off));
        var (i1, i2) = spans[0];
        var (j1, j2) = spans[1];
        Assert.True(Math.Abs(i2 - i1) < 0.05, $"爬行时积分动了：{i1:F3} → {i2:F3}");
        Assert.True(j2 < j1 - 0.12, $"只看速度门槛时这段积分该往下攒（对照）：{j1:F3} → {j2:F3}");

        await WaitUntil(() => on.Tr <= 20.15, 10000, () => $"Tr 没走到 20.15：{on.Tr}");
        await Task.Delay(1300);                                  // 窗里只剩停住以后的
        var i3 = on.I;
        await Task.Delay(1000);
        Assert.True(on.I < i3 - 0.06, $"停住了积分该往下攒：{i3:F2} → {on.I:F2}");
    }

    [Fact]
    public async Task 读数只跳一个字不算往目标走_积分照积()
    {
        // 离目标 0.1 K 时 Tr 跳一个字（20.11 → 20.10）：照「这速度多久到」它也算趋近（1 s 窗跳 0.01 = 0.6 K/min，0.1 K 十秒就到），
        // 只有最低速度（0.9 K/min、窗里至少走过 0.015 K）把它挡掉。对照把最低速度关了：那一阵积分就被冻住
        async Task<Rig> Make(bool floor)
        {
            var r = new Rig(20.11, 20.11, thermal: true);
            r.Loop.OuterInnerLagFreezeC = 0;
            r.Loop.OuterBrakeBiasC = 0;
            if (!floor) r.Loop.OuterApproachMinCPerMin = 0;
            await r.Loop.StartChannelAsync(1, 20);
            return r;
        }
        await using var on = await Make(floor: true);
        await using var off = await Make(floor: false);
        await Task.Delay(1500);                                  // 窗里只剩 20.11
        on.Tr = 20.10;
        off.Tr = 20.10;
        // 回路读到 20.10 以后再记起点：之前那几拍按 e = −0.11 停着照积，两边一样
        static async Task<double> Seen(Rig r)
        {
            await WaitUntil(() => r.Last is { } l && l.OuterMeasuredC <= 20.105, 3000, () => "回路没读到 20.10");
            return r.I;
        }
        var starts = await Task.WhenAll(Seen(on), Seen(off));
        await Task.Delay(500);                                   // 窗（1 s）里那个 20.11 还没出去（再晚两边都照积，比差值不怕晚）
        var onDrop = starts[0] - on.I;
        var offDrop = starts[1] - off.I;
        Assert.True(onDrop > 0.02, $"跳一个字被当成往目标走了，积分没积：{starts[0]:F3} → {on.I:F3}");
        Assert.True(offDrop < 0.03 && onDrop - offDrop > 0.02, $"对照（没有最低速度）该冻住：{starts[1]:F3} → {off.I:F3}（有最低速度那边积了 {onDrop:F3}）");
    }

    [Fact]
    public async Task 慢慢穿过目标那一阵不学稳态偏置_真稳住才学()
    {
        // 2026-11-03 现场：釜内每分钟 0.05 K 从 15.05 穿到 14.95，在 ±0.05 带里待满一分钟，就把还在漂的偏置记成了稳态偏置。
        // 这里学习要 10 拍（2 s）、带放宽到 ±0.1；Tr 每秒走 0.035 K 穿过 20（带里待 5.7 s，1 s 窗里走 0.026 以上 > 0.02）
        async Task<(Rig R, PidGainSchedule Table)> Make(bool still)
        {
            var r = new Rig(20.2, 21.5, thermal: true);
            r.Loop.OuterSteadyCyclesToLearn = 10;
            r.Loop.OuterLearnBandC = 0.1;
            if (!still) r.Loop.OuterLearnStillC = 0;
            var table = r.Loop.GetGainSchedule(1, ActuatorMode.Bidirectional);
            table.SegmentBoundaryC = double.NaN;
            table.Learn(new GainPoint(20, new PidGains(20, 0.05, 0), 0, 0, new PidGains(2.5, 0.013 * K, 0), 8));
            await r.Loop.StartChannelAsync(1, 20);
            DateTime? t0 = null;
            r.OnCycle = at => { t0 ??= at; r.Tr = Math.Max(19.8, 20.2 - 0.035 * (at - t0.Value).TotalSeconds); };
            return (r, table);
        }
        var (on, onTable) = await Make(still: true);
        var (off, offTable) = await Make(still: false);
        await using var _ = on;
        await using var __ = off;
        await WaitUntil(() => on.Tr <= 19.8 && off.Tr <= 19.8, 20000, () => $"Tr 没穿过去：{on.Tr} / {off.Tr}");
        Assert.Null(onTable.SteadyBiasAt(20));
        Assert.NotNull(offTable.SteadyBiasAt(20));               // 对照：不查动没动时，穿过去那一阵就记了

        on.OnCycle = null;
        on.Tr = 20.01;                                           // 真稳在带里
        await WaitUntil(() => onTable.SteadyBiasAt(20) is not null, 8000, () => "稳在带里也没学");
    }

    [Fact]
    public async Task 斜坡上照常积分_看的是误差不是Tr()
    {
        // 升温坡上 Tr 一直落后设定 0.5 K 跟着走：Tr 每分钟升 5 K（比门槛 3 K/min 快）、但误差不缩——不算趋近，积分照常跟坡
        await using var r = new Rig(19.5, 20, thermal: true);
        await r.Loop.StartChannelAsync(1, 20);
        r.OnCycle = _ => r.Tr = r.Loop.GetChannelStatus(1).SetpointC - 0.5;
        await Outer(r);
        r.Loop.StartProfile(1, SegmentProfile.Linear(25, 5));
        await Task.Delay(2000);
        var i1 = r.I;
        await Task.Delay(1500);
        Assert.True(r.I > i1 + 0.3, $"坡上积分不动了：{i1:F2} → {r.I:F2}");
        r.Loop.StopProfile(1);
    }

    [Fact]
    public async Task 坡尾回基_斜坡停下来积分回到起坡时的值()
    {
        // 釜内钉在 20，从 20 起坡到 21（5 K/min，12 s）：坡上误差一路涨，积分跟着攒；坡停的那一拍回到起坡时（≈ 0）
        await using var r = new Rig(20, 20, thermal: true);
        await Task.Delay(200);
        await r.Loop.StartChannelAsync(1, 20);
        await Outer(r);
        var i0 = r.I;
        r.Loop.StartProfile(1, SegmentProfile.Linear(21, 5));
        await WaitUntil(() => r.Notes().Any(n => Regex.IsMatch(n, @"斜坡走到 [\d.]+ ℃ 停了")), 20000,
            () => $"坡尾没回基：积分 {r.Last?.OuterI}\n" + string.Join("\n", r.Notes()));
        var line = r.Notes().First(n => Regex.IsMatch(n, @"斜坡走到 [\d.]+ ℃ 停了"));
        var m = Regex.Match(line, @"外环积分 ([+-]?[\d.]+) → ([+-]?[\d.]+) ℃");
        Assert.True(double.Parse(m.Groups[1].Value) > i0 + 1, $"坡上积分该攒起来：{i0:F2} → {m.Groups[1].Value}");
        Assert.Equal(i0, double.Parse(m.Groups[2].Value), 1);
        Assert.Contains("回到起坡时的值", line);
    }

    [Fact]
    public async Task 坡中途直接改目标_坡上攒的放掉_只往停在前面那一侧放()
    {
        // 釜内钉在 20、升温坡上积分往上攒；中途直接改目标 21（还在釜内上方、从下面趋近）：积分回到起坡时的 0（少加热 = 停在前面）
        await using var up = new Rig(20, 20, thermal: true);
        await Task.Delay(200);
        await up.Loop.StartChannelAsync(1, 20);
        await Outer(up);
        up.Loop.StartProfile(1, SegmentProfile.Linear(25, 5));
        await WaitUntil(() => up.I > 0.8, 15000, () => $"坡上积分没攒：{up.I:F2}");
        up.Loop.StopProfile(1);
        up.Loop.SetSetpoint(1, 21);
        await WaitUntil(() => up.Notes().Any(n => n.Contains("斜坡中途改目标 21.00 ℃")), 3000, () => string.Join("\n", up.Notes()));
        // 放到多少看通知里的「→」那个数：通知在那一拍中途发，这时读回调里的积分可能还是上一拍的
        var line = up.Notes().First(n => n.Contains("斜坡中途改目标 21.00 ℃"));
        var back = double.Parse(Regex.Match(line, @"外环积分 [+-]?[\d.]+ → ([+-]?[\d.]+) ℃").Groups[1].Value);
        Assert.True(Math.Abs(back) < 0.3, $"没放回起坡值：{back:F2}");

        // 同样攒起来之后改成 19（釜内在目标上方、从上面趋近）：回起坡值等于往下放、是往冲过去那边推——不放，积分留着
        await using var down = new Rig(20, 20, thermal: true);
        await Task.Delay(200);
        await down.Loop.StartChannelAsync(1, 20);
        await Outer(down);
        down.Loop.StartProfile(1, SegmentProfile.Linear(25, 5));
        await WaitUntil(() => down.I > 0.8, 15000, () => $"坡上积分没攒：{down.I:F2}");
        down.Loop.StopProfile(1);
        down.Loop.SetSetpoint(1, 19);
        var kept = down.I;
        await Task.Delay(800);
        Assert.DoesNotContain(down.Notes(), n => n.Contains("斜坡中途改目标"));
        // 真放了是一下掉 0.8 以上；改目标后头一拍没东西冻（e = −1、夹套还没落后）照常积 0.78 × dt，回路慢一点就过 0.2——门槛放到 0.5
        Assert.True(down.I > kept - 0.5, $"往冲过去那边放了：{kept:F2} → {down.I:F2}");
    }
}
