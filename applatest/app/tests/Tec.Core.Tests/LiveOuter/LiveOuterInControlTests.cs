using System.Reflection;
using System.Text;
using Tec.Drivers.Rd105;
using TecControl.Core.Comm;
using TecControl.Core.Control;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 【本地改动】（2026-11-16，外环改「按实测实时算」第 1 步：新外环能接手控温，开关缺省「旧」）真回路 + 假 RD105 上锁这几件事：
/// · B1 旧模式一字不变：同一台回路两路、同样的输入，一路显式拨「旧」、每几拍翻一次「TEC 真接着」、控中又选「新」（这一场不变），另一路什么都不碰——
///   两路每拍的控制量（周期快照除影子读数外每一个属性，含 algo 标志）和写到设备上的占空比逐位相同；
/// · B2 这个比法比得出来：一路旧一路新，控制量分叉；
/// · B3 新模式每一拍夹套目标 = clamp(新外环这一拍的读数)，旧外环一拍没算（积分 0、节拍时刻没动），algo = 1，outer_* 为 NaN，开控提示是「新算法」、没有「积分起点」；
/// · B5 新模式压低被拒；B7 Tr 丢失保持期按模型推、过时限照停、恢复后不说「积分沿用」；B8 TEC 被扣着照算、喂给影子的事实对；
/// · D4 开控锁定：控中改选择这一场不变、停控再开控才用上（两个方向）；挂起中再开控沿用；影子停了 / 不是双向挡 / 控夹套 → 锁成旧 + 说为什么；停控清锁定；
/// · D5 在控中影子停了：夹套目标保持、OuterLossGrace 后停控、只说一次；
/// · B10 / B11 跑飞「放热」豁免：制冷顶满 + TEC 真接着 + 釜内比夹套热 → 不停、说一次、条件撤了再武装；TEC 没接着 / 釜内不比夹套热 / 旧模式 → 照停。
/// 【本地改动】（2026-11-17，复核后用户选的两道保护）
/// · S2「看电流再决定」：豁免还要 TEC 电流在出力（Rig 给两路 CURRENT 摆 3 A）；0.02 A → 先说「TEC 没在出力」再按跑飞原话停；只有限流位 → 算在出力；电流读不到 → W 后停；
/// · S1「读数对不上就停控」：A 路影子换成 TimeScale 25 的（判据按现场秒），冻在目标下 → S1 那句话停控、停控清判据、重新开控新的一份；旧模式同样冻住一拍不算、两路逐位相同；
///   冻在目标上 → 只报警一次、快照带红条、Tr 丢一下不重起、读数回来报解除。
/// 小釜、时间压着跑（回路 20 ms、外环 80 ms），影子按墙钟秒算——它算出来的数没有物理意义，这里只看「谁在给夹套目标、给的是不是它」。
/// 新外环要锁得成，两路都配成双向挡（加热棒 + TEC 双向、双向挡那张表），影子先跑几拍起算再开控。
/// </summary>
public class LiveOuterInControlTests
{
    private const BindingFlags Pub = BindingFlags.Public | BindingFlags.Instance;

    private static object StateOf(HostControlLoop loop, int ch)
        => typeof(HostControlLoop).GetMethod("State", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(loop, new object[] { ch })!;

    private static T Get<T>(HostControlLoop loop, int ch, string field)
    {
        var st = StateOf(loop, ch);
        return (T)st.GetType().GetField(field, Pub)!.GetValue(st)!;
    }

    private static void Set(HostControlLoop loop, int ch, string field, object? value)
    {
        var st = StateOf(loop, ch);
        st.GetType().GetField(field, Pub)!.SetValue(st, value);
    }

    /// <summary>照常算；Fail 置位后每一步都抛（核「在控中影子算挂了」）。</summary>
    private sealed class FailableShadow : LiveOuterShadow
    {
        public volatile bool Fail;

        protected override LiveOuterOutput StepCore(in LiveOuterInput input, bool first)
        {
            if (Fail) throw new InvalidOperationException("测试：在控中影子算挂了");
            return base.StepCore(input, first);
        }
    }

    /// <summary>照常算，只是把每一步喂给核心类的输入记下来。</summary>
    private sealed class RecordingShadow : LiveOuterShadow
    {
        private readonly List<LiveOuterInput> _steps = new();

        public LiveOuterInput[] Steps() { lock (_steps) return _steps.ToArray(); }

        protected override LiveOuterOutput StepCore(in LiveOuterInput input, bool first)
        {
            lock (_steps) _steps.Add(input);
            return base.StepCore(input, first);
        }
    }

    /// <summary>A 路改成测试手摆：釜内固定、夹套从开始那一刻起按固定速度往上走（核跑飞判据）。
    /// 【本地改动】（2026-11-17 复核后）<paramref name="floorC"/>：速度给负的（夹套往下走）时走到这儿就停住（「夹套推到底不动了」）；缺省 −∞ = 不停，原来几条照旧。</summary>
    private sealed class ManualPlant(double trC, double tjC, double ratePerS, double floorC = double.NegativeInfinity)
    {
        public readonly double TrC = trC, TjC = tjC, RatePerS = ratePerS, FloorC = floorC;
        /// <summary>夹套从这一刻（DateTime.Ticks）起往上走；0 = 还没开始。测试线程写、回路线程读。</summary>
        public long RampFromTicks;
    }

    /// <summary>
    /// 【本地改动】（第 1 步复核）串口外面套一层：<see cref="Armed"/> 置位后下一笔「TC1 写占空比」先不发，置 <see cref="Blocked"/>、
    /// 等测试放开（<see cref="Release"/>）。回路那一拍就卡在 RunControlStepAsync 的 WriteDutyAsync 里——停控竞态的那个窗口，拉宽到确定。
    /// </summary>
    private sealed class DutyGate(ISerialTransport inner) : ISerialTransport
    {
        public volatile bool Armed;
        public readonly ManualResetEventSlim Blocked = new(false);
        public readonly ManualResetEventSlim Release = new(false);
        public bool IsOpen => inner.IsOpen;
        public void Open() => inner.Open();
        public void Close() => inner.Close();
        public void DiscardInput() => inner.DiscardInput();
        public int Read(byte[] buffer, int offset, int count, int timeoutMs) => inner.Read(buffer, offset, count, timeoutMs);
        public void Dispose() => inner.Dispose();

        public void Write(byte[] buffer, int offset, int count)
        {
            if (Armed && Encoding.ASCII.GetString(buffer, offset, count).StartsWith("TC1:PWMDUTY=", StringComparison.Ordinal))
            {
                Armed = false;
                Blocked.Set();
                Release.Wait(TimeSpan.FromSeconds(10));
            }
            inner.Write(buffer, offset, count);
        }
    }

    private sealed record Cycle(ControlCycleResult Res, long Duty1, long Duty2, DateTime LastOuter1, double OuterLastI1);

    /// <summary>
    /// 真回路 + 假 RD105，两路各带一个一模一样的小釜（同 LiveOuterLoopHookTests 的那一个：夹套按占空比走 → 釜壁滞后 → 釜内），
    /// 都配成釜内串级 + 双向挡（加热棒 + TEC 双向、双向挡表）。釜模型只在回路每拍的回调里按快照时刻推。
    /// </summary>
    private sealed class Rig : IAsyncDisposable
    {
        private const double K = 20;
        public readonly FakeRd105Device Dev = new() { Thermal = false };
        public readonly HostControlLoop Loop;
        private readonly object _gate = new();
        private readonly List<Cycle> _cycles = new();
        private readonly List<(int Ch, string Level, string Text)> _notices = new();
        private readonly List<(int Ch, string Reason)> _trips = new();
        private readonly List<Exception> _faults = new();
        private readonly List<(string Kind, int Ch, string Text)> _events = new();   // 【本地改动】（S1 / S2）提示和停控按先后放一起（核「先说一句、再停控」）
        private readonly double[] _tj = [25, 25], _wall = [25, 25], _tr = [22, 22], _trRead = [22, 22];
        private DateTime _last;
        public readonly bool[] TrLost = new bool[2];
        /// <summary>【本地改动】（2026-11-17，S1）釜内读数钉在这个数上（读数冻住 / 插错 / 提出液面那种「有效但读错」）；NaN = 照真值。测试线程写、回路线程读。</summary>
        public readonly double[] TrForce = [double.NaN, double.NaN];
        public volatile ManualPlant? Manual1;
        public volatile Action<ControlCycleResult>? OnCycle;

        /// <param name="wrap">【本地改动】（第 1 步复核）串口外面再套一层（核停控竞态：卡住某一笔写）；null = 直接接假 RD105。</param>
        public Rig(int outerPeriodMs = 80, Func<ISerialTransport, ISerialTransport>? wrap = null)
        {
            for (var ch = 1; ch <= 2; ch++)
            {
                Dev.Set(ch, "TCADJTEMP", 25_00000);
                Dev.Set(ch, "LIMITED", 90);
                // 【本地改动】（2026-11-17，S2「看电流再决定」）TEC 输出电流摆 3 A（假 RD105 没摆过的寄存器答 0）：新模式跑飞「放热」豁免要看到 TEC 在出力，
                // 不摆的话下面靠豁免不停控的几条（B10 放热、B12a / B12b）都会翻成停控。旧模式不读它（只进周期快照）
                Dev.Set(ch, "CURRENT", 3000);
            }
            Dev.Open();
            Loop = new HostControlLoop(new Rd105Link(wrap?.Invoke(Dev) ?? Dev).Controller)
            {
                Period = TimeSpan.FromMilliseconds(20),
                OuterPeriod = TimeSpan.FromMilliseconds(outerPeriodMs),
                SyncMode = ChannelSyncMode.Independent,
                ManagePwmFrequency = false,
                OuterStartQuietWindow = TimeSpan.Zero,
            };
            for (var ch = 1; ch <= 2; ch++)
            {
                var i = ch - 1;
                Loop.ConfigurePid(ch, 8, 0.5, 0, 90, invertOutput: false);
                Loop.ConfigureCascade(ch, 2.5, 0.2, 0, 8);
                Loop.SetStrategy(ch, ControlStrategy.Cascade);
                Loop.SetActuatorMode(ch, ActuatorMode.HeaterTec, GainTable.Both);
                Loop.SetOuterSource(ch, () => Volatile.Read(ref TrLost[i]) ? double.NaN
                    : !double.IsNaN(Volatile.Read(ref TrForce[i])) ? Volatile.Read(ref TrForce[i])     // 【本地改动】（S1）钉住的读数
                    : Volatile.Read(ref _trRead[i]));
            }
            var st1 = StateOf(Loop, 1);
            var lastOuter = st1.GetType().GetField("LastOuterUpdate", Pub)!;
            var outerPid = (PidController)st1.GetType().GetField("OuterPid", Pub)!.GetValue(st1)!;
            Loop.ChannelNotice += (ch, level, text) => { lock (_gate) { _notices.Add((ch, level, text)); _events.Add(("notice", ch, text)); } };
            Loop.ChannelTripped += (ch, reason) => { lock (_gate) { _trips.Add((ch, reason)); _events.Add(("trip", ch, reason)); } };
            Loop.CycleFaulted += ex => { lock (_gate) _faults.Add(ex); };
            Loop.CycleCompleted += res =>
            {
                StepPlants(res);
                lock (_gate) _cycles.Add(new Cycle(res, Dev.Get(1, "PWMDUTY"), Dev.Get(2, "PWMDUTY"),
                    (DateTime)lastOuter.GetValue(st1)!, outerPid.LastI));
                OnCycle?.Invoke(res);
            };
        }

        private void StepPlants(ControlCycleResult res)
        {
            var at = res.Snapshot.Timestamp;
            var dt = _last == default ? 0 : Math.Clamp((at - _last).TotalSeconds, 0, 0.2) * K;
            _last = at;
            var infos = new[] { res.Ch1, res.Ch2 };
            for (var i = 0; i < 2; i++)
            {
                if (i == 0 && Manual1 is { } m)
                {
                    var from = Interlocked.Read(ref m.RampFromTicks);
                    var tj = Math.Max(m.FloorC, m.TjC + (from != 0 ? Math.Max(0, (at.Ticks - from) / 1e7) * m.RatePerS : 0));
                    Dev.Set(1, "TCADJTEMP", (long)Math.Round(tj * 1e5));
                    Volatile.Write(ref _trRead[0], m.TrC);
                    continue;
                }
                var duty = infos[i].Active ? infos[i].DutyPercent : 0;
                _tj[i] += (duty / 100.0 * 1.0 - (_tj[i] - 25) * 0.01) * dt;
                _wall[i] += (_tj[i] - _wall[i]) / 20 * dt;
                _tr[i] += (_wall[i] - _tr[i] - 1.5) / 60 * dt;
                Dev.Set(i + 1, "TCADJTEMP", (long)Math.Round(_tj[i] * 1e5));
                Volatile.Write(ref _trRead[i], Math.Round(_tr[i], 2));
            }
        }

        /// <summary>A 路改成手摆（回路转起来之前调）：夹套、釜内立刻按它给。</summary>
        public void UseManual1(ManualPlant m)
        {
            Manual1 = m;
            Dev.Set(1, "TCADJTEMP", (long)Math.Round(m.TjC * 1e5));
            Volatile.Write(ref _trRead[0], m.TrC);
        }

        public Cycle[] Cycles() { lock (_gate) return _cycles.ToArray(); }
        public (int Ch, string Level, string Text)[] Notices() { lock (_gate) return _notices.ToArray(); }
        public (int Ch, string Reason)[] Trips() { lock (_gate) return _trips.ToArray(); }
        public Exception[] Faults() { lock (_gate) return _faults.ToArray(); }
        public (string Kind, int Ch, string Text)[] Events() { lock (_gate) return _events.ToArray(); }

        /// <summary>【本地改动】（S2）这一路 TEC 输出电流（A）摆到假 RD105 的 CURRENT 上（mA）。</summary>
        public void SetCurrent(int ch, double amps) => Dev.Set(ch, "CURRENT", (long)Math.Round(amps * 1000));
        public int Count { get { lock (_gate) return _cycles.Count; } }

        public async Task UntilCycles(int n, int ms = 30000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(ms);
            while (Count < n)
            {
                Assert.True(DateTime.UtcNow < deadline, $"等太久：才 {Count} 拍（要 {n}）；周期故障 {Faults().Length}");
                await Task.Delay(10);
            }
        }

        public async Task Until(Func<bool> cond, string what, int ms = 30000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(ms);
            while (!cond())
            {
                Assert.True(DateTime.UtcNow < deadline, "等太久：" + what);
                await Task.Delay(10);
            }
        }

        /// <summary>回路转起来、两路影子都起算以后，在回路那条流上（两拍之间）同一个空当里给 chs 开控：开控时的条件对两路一模一样。返回开控后第一拍的下标。</summary>
        public async Task<int> StartTogether(double setpointC, params int[] chs)
        {
            Loop.Start();
            await UntilCycles(8);
            var started = -1;
            Exception? failed = null;
            var done = new TaskCompletionSource();
            OnCycle = res =>
            {
                if (started >= 0) return;
                started = Count;
                try { foreach (var ch in chs) Loop.StartChannelAsync(ch, setpointC).GetAwaiter().GetResult(); }
                catch (Exception ex) { failed = ex; }
                done.TrySetResult();
            };
            await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
            OnCycle = null;
            Assert.Null(failed);
            return started;
        }

        public async ValueTask DisposeAsync()
        {
            await Loop.ShutdownAsync();
            Loop.Dispose();
        }
    }

    /// <summary>新外环提示里不许出现的字（验收仿真 / 孪生按字数次数的那几个）。</summary>
    private static readonly string[] Counted = { "夹套压低", "外环积分冻结在", "外环偏置自校正", "那一枪" };

    private static void NoCountedWords(IEnumerable<string> texts)
    {
        foreach (var t in texts)
            foreach (var w in Counted)
                Assert.False(t.Contains(w), $"提示里有被计数的字「{w}」：{t}");
    }

    // ───────────────────────────── B1 / B2 ─────────────────────────────

    [Fact]
    public async Task B1_旧模式_两路同输入_一路拨旧翻TEC真接着控中又选新_另一路不碰_控制量和占空比逐拍逐位相同()
    {
        await using var r = new Rig();
        r.Loop.SetOuterAlgorithm(1, true);                             // 拨「新」又拨回「旧」：开控时锁的是此刻的「旧」
        r.Loop.SetOuterAlgorithm(1, false);
        var k = 0;
        var s0 = await r.StartTogether(24, 1, 2);
        r.OnCycle = res =>
        {
            k++;
            if (k % 5 == 0) r.Loop.SetTecEngaged(1, k % 10 == 0);     // 每 5 拍翻一次「TEC 真接着」
            r.Loop.SetOuterAlgorithm(1, false);
            _ = r.Loop.IsLiveOuterInControl(1);
            _ = r.Loop.GetLiveOuterStatus(1);
            _ = r.Loop.GetOuterAlgorithm(1);
            if (k == 120) r.Loop.SetOuterAlgorithm(1, true);           // 控中改选「新」：这一场不变
            if (k > 120) r.Loop.SetOuterAlgorithm(1, true);
        };
        await r.UntilCycles(s0 + 260);
        await r.Loop.ShutdownAsync();
        var all = r.Cycles();
        var c = all[(s0 + 1)..];
        Assert.Empty(r.Faults());
        Assert.Empty(r.Trips());

        // 不是摆设：两路一直在控、外环在算、控制量在动；A 路选了「新」以后这一场照旧是旧外环
        Assert.All(c, x => Assert.True(x.Res.Ch1.Active && x.Res.Ch2.Active && x.Res.Ch1.Cascade && !x.Res.Ch1.LiveOuterInControl));
        Assert.True(c.Select(x => x.Res.Ch1.DutyPercent).Distinct().Count() > 100, "占空比几乎没动，比不出东西");
        Assert.True(c.Select(x => x.Res.Ch1.InnerSetpointC).Distinct().Count() > 30, "夹套设定几乎没动");
        Assert.True(c.Select(x => x.LastOuter1).Distinct().Count() > 40, "旧外环没怎么算");
        Assert.True(r.Loop.GetOuterAlgorithm(1));
        Assert.False(r.Loop.IsLiveOuterInControl(1));
        // 两路开控都只说了旧外环那句「积分起点」，A 路没有多出任何一句
        var n = r.Notices();
        Assert.Equal(n.Count(x => x.Ch == 2), n.Count(x => x.Ch == 1));
        Assert.DoesNotContain(n, x => x.Text.Contains("新算法"));

        // 正题：从回路转起来的第一拍（没开控的那几拍也算）到最后，每一拍两路逐位相同
        for (var i = 0; i < all.Length; i++)
        {
            var x = all[i];
            Assert.True(x.Res.Ch1 with { LiveOuter = null } == x.Res.Ch2 with { LiveOuter = null },
                $"第 {i} 拍两路的控制量分叉了：\nA {x.Res.Ch1 with { LiveOuter = null }}\nB {x.Res.Ch2 with { LiveOuter = null }}");
            Assert.True(x.Duty1 == x.Duty2, $"第 {i} 拍写到设备上的占空比不一样：A {x.Duty1} B {x.Duty2}");
        }
    }

    [Fact]
    public async Task B2_比法灵敏_一路旧一路新_控制量分叉_algo各是各的()
    {
        await using var r = new Rig();
        r.Loop.SetOuterAlgorithm(2, true);
        var s0 = await r.StartTogether(24, 1, 2);
        await r.UntilCycles(s0 + 60);
        var c = r.Cycles()[(s0 + 1)..];
        Assert.All(c, x => Assert.True(x.Res.Ch1.Active && x.Res.Ch2.Active));
        Assert.All(c, x => Assert.False(x.Res.Ch1.LiveOuterInControl));
        Assert.All(c, x => Assert.True(x.Res.Ch2.LiveOuterInControl));
        Assert.Contains(c, x => x.Duty1 != x.Duty2);
        Assert.Contains(c, x => x.Res.Ch1.InnerSetpointC != x.Res.Ch2.InnerSetpointC);
        Assert.False(r.Loop.IsLiveOuterInControl(1));
        Assert.True(r.Loop.IsLiveOuterInControl(2));
    }

    // ───────────────────────────── B3 / B5 ─────────────────────────────

    [Fact]
    public async Task B3_新模式_每拍夹套目标就是新外环这一拍的读数_旧外环一拍不算_algo为1_outer列为NaN_压低被拒()
    {
        await using var r = new Rig();
        r.Loop.SetOuterAlgorithm(1, true);
        var s0 = await r.StartTogether(24, 1);
        Assert.True(r.Loop.IsLiveOuterInControl(1));
        await r.UntilCycles(s0 + 100);

        // B5：压低被拒（两种基准都拒），没有压低提示；解除也什么都不做
        Assert.False(r.Loop.SetOuterPushDown(1, 2.0));
        Assert.False(r.Loop.SetOuterPushDown(1, 2.0, fromEquilibrium: true));
        Assert.Null(r.Loop.GetOuterPushDown(1));
        Assert.False(r.Loop.SetOuterPushDown(1, null));
        await r.UntilCycles(s0 + 160);
        await r.Loop.ShutdownAsync();
        var c = r.Cycles()[(s0 + 1)..];
        Assert.Empty(r.Faults());
        Assert.Empty(r.Trips());

        var active = c.Where(x => x.Res.Ch1.Active).ToArray();
        Assert.True(active.Length >= 150, $"在控的只有 {active.Length} 拍");
        foreach (var x in active)
        {
            var i = x.Res.Ch1;
            Assert.True(i.LiveOuterInControl, "algo 不是 1");
            Assert.NotNull(i.LiveOuter);
            Assert.True(i.InnerSetpointC == Math.Clamp(i.LiveOuter!.JacketSetpointC, -50, 200),
                $"夹套目标 {i.InnerSetpointC:R} 不是新外环这一拍给的 {i.LiveOuter.JacketSetpointC:R}");
            Assert.True(double.IsNaN(i.OuterP) && double.IsNaN(i.OuterI) && double.IsNaN(i.OuterD) && double.IsNaN(i.OuterFinishC) && double.IsNaN(i.OuterPushC));
            Assert.Equal(default, x.LastOuter1);                       // 旧外环的节拍时刻从开控起一直是清零的：它一拍没算
            Assert.Equal(0.0, x.OuterLastI1);                          // 旧外环积分：开控清 0、没预置、没动
        }
        // 新外环真在算：读数换了很多拍，夹套目标跟着换
        Assert.True(active.Select(x => x.Res.Ch1.LiveOuter).Distinct(ReferenceEqualityComparer.Instance).Count() > 30);
        Assert.True(active.Select(x => x.Res.Ch1.InnerSetpointC).Distinct().Count() > 30);

        // 提示：开控那句是新算法的，没有旧外环的「积分起点」，没有压低，字眼不撞计数
        var n = r.Notices().Where(x => x.Ch == 1).Select(x => x.Text).ToArray();
        Assert.Single(n, t => t.Contains("外环：新算法（按实测实时算）"));
        Assert.DoesNotContain(n, t => t.Contains("积分起点"));
        Assert.DoesNotContain(n, t => t.Contains("压低"));
        NoCountedWords(n);
        var st = r.Loop.GetLiveOuterStatus(1);
        Assert.False(st.Active);                                       // ShutdownAsync 停了控
        Assert.True(st.Requested && !st.InControl && st.LockNote is null);
    }

    // ───────────────────────────── B7 Tr 丢失 ─────────────────────────────

    [Fact]
    public async Task B7_新模式_Tr丢失保持期按新外环模型推_恢复后接着按读数算不说积分沿用_过时限照停()
    {
        await using var r = new Rig();
        r.Loop.OuterLossGrace = TimeSpan.FromSeconds(1.5);
        r.Loop.SetOuterAlgorithm(1, true);
        var s0 = await r.StartTogether(24, 1);
        await r.UntilCycles(s0 + 40);
        var lostAt = r.Count;
        Volatile.Write(ref r.TrLost[0], true);                         // 丢 0.4 s（不到 1.5 s 的时限）
        await Task.Delay(400);
        Volatile.Write(ref r.TrLost[0], false);
        var backAt = r.Count;
        await r.UntilCycles(backAt + 40);
        Assert.Empty(r.Trips());

        var c = r.Cycles();
        var held = c[lostAt..].Where(x => x.Res.Ch1.OuterHoldSeconds is not null).ToArray();
        Assert.True(held.Length >= 5, $"保持期只有 {held.Length} 拍");
        foreach (var x in held)
        {
            var i = x.Res.Ch1;
            Assert.True(i.Active && i.LiveOuterInControl);
            Assert.NotNull(i.LiveOuter);                               // 没读数时影子只推模型，照样有读数
            Assert.True(i.InnerSetpointC == Math.Clamp(i.LiveOuter!.JacketSetpointC, -50, 200), "保持期夹套目标不是新外环推的那一个");
        }
        Assert.Contains(held, x => x.Res.Ch1.InnerSetpointC != 24);    // 不是钳到主设定（旧模式那种）
        // 恢复以后接着按新外环这一拍的读数
        Assert.All(c[(backAt + 2)..].Where(x => x.Res.Ch1.Active && x.Res.Ch1.OuterHoldSeconds is null),
            x => Assert.True(x.Res.Ch1.InnerSetpointC == Math.Clamp(x.Res.Ch1.LiveOuter!.JacketSetpointC, -50, 200)));
        var n = r.Notices().Where(x => x.Ch == 1).Select(x => x.Text).ToArray();
        Assert.Single(n, t => t.Contains("读数丢失") && t.Contains("只按模型推夹套目标"));
        var back = Assert.Single(n, t => t.Contains("釜内 Tr 已恢复"));
        Assert.Contains("外环新算法接着按读数算", back);
        Assert.DoesNotContain("积分沿用", back);
        Assert.DoesNotContain(n, t => t.Contains("外环冻结"));

        // 丢得比时限久：照停（原因里说的是按模型推，不是「按釜内设定保持」）
        Volatile.Write(ref r.TrLost[0], true);
        await r.Until(() => r.Trips().Length > 0, "Tr 丢太久停控", 10000);
        var trip = Assert.Single(r.Trips());
        Assert.Equal(1, trip.Ch);
        Assert.Contains("读数丢失超过", trip.Reason);
        Assert.Contains("按模型推着夹套目标", trip.Reason);
        Assert.False(r.Loop.IsLiveOuterInControl(1));
        NoCountedWords(r.Notices().Select(x => x.Text));
    }

    // ───────────────────────────── B8 TEC 被扣着 / 事实 ─────────────────────────────

    [Fact]
    public async Task B8_新模式_TEC被扣着照算_内环钳0_喂给影子的事实是扣着的_TEC真接着照喂()
    {
        await using var r = new Rig();
        var spy = new RecordingShadow();
        Set(r.Loop, 1, "Live", spy);
        r.Loop.SetOuterAlgorithm(1, true);
        r.Loop.SetTecEngaged(1, true);
        var s0 = await r.StartTogether(24, 1);
        Assert.True(r.Loop.IsLiveOuterInControl(1));
        await r.UntilCycles(s0 + 40);
        var engagedSteps = spy.Steps().Length;
        // 会话在等人答冷却水：双向挡表里换成只加热（同一张表，不换对象），TEC 也不再算「真接着」
        r.Loop.SetTecEngaged(1, false);
        r.Loop.SetActuatorMode(1, ActuatorMode.HeatOnly, GainTable.Both);
        var withheldAt = r.Count;
        r.Loop.SetSetpoint(1, 15);                                     // 要降温：只加热下内环贴着 0
        await r.UntilCycles(withheldAt + 60);
        await r.Loop.ShutdownAsync();
        var all = spy.Steps();
        var c = r.Cycles()[(withheldAt + 2)..].Where(x => x.Res.Ch1.Active).ToArray();

        Assert.True(r.Trips().Length == 0, string.Join("；", r.Trips().Select(t => t.Reason)));
        Assert.True(c.Length >= 40);
        Assert.All(c, x =>
        {
            Assert.True(x.Res.Ch1.LiveOuterInControl);
            Assert.True(x.Res.Ch1.InnerSetpointC == Math.Clamp(x.Res.Ch1.LiveOuter!.JacketSetpointC, -50, 200));
            Assert.True(x.Res.Ch1.DutyPercent >= 0, $"只加热形态下写了负的占空比 {x.Res.Ch1.DutyPercent}");
        });
        // 影子看到的事实：开控后 TEC 真接着；扣着以后 TecWithheld、贴下限（钳 0）、目标 15
        Assert.Contains(all[..engagedSteps], i => i.TecSide && i.OutputOn && !i.TecWithheld);
        var late = all[(engagedSteps + 4)..].Where(i => i.Sp == 15).ToArray();
        Assert.True(late.Length >= 5);
        Assert.All(late, i => Assert.True(i.TecWithheld && !i.TecSide && i.OutputOn));
        Assert.Contains(late, i => i.InnerSatLow);
    }

    // ───────────────────────────── D4 开控锁定 ─────────────────────────────

    [Fact]
    public async Task D4_控中改选择这一场不变_停控再开控才用上_两个方向()
    {
        await using var r = new Rig();
        // ① 旧开控，控中选「新」：这一场照旧是旧
        var s0 = await r.StartTogether(24, 1);
        await r.UntilCycles(s0 + 20);
        r.Loop.SetOuterAlgorithm(1, true);
        var sel1 = r.Count;
        await r.UntilCycles(sel1 + 40);
        Assert.All(r.Cycles()[(sel1 + 1)..], x => Assert.False(x.Res.Ch1.LiveOuterInControl));
        Assert.False(r.Loop.IsLiveOuterInControl(1));
        var st = r.Loop.GetLiveOuterStatus(1);
        Assert.True(st.Requested && !st.InControl && st.PendingUntilRestart);

        // ② 停控、再开控：用上「新」
        await r.Loop.StopChannelAsync(1);
        Assert.False(r.Loop.IsLiveOuterInControl(1));
        await r.Loop.StartChannelAsync(1, 24);
        var re1 = r.Count;
        Assert.True(r.Loop.IsLiveOuterInControl(1));
        await r.UntilCycles(re1 + 40);
        Assert.All(r.Cycles()[(re1 + 2)..], x => Assert.True(x.Res.Ch1.LiveOuterInControl
            && x.Res.Ch1.InnerSetpointC == Math.Clamp(x.Res.Ch1.LiveOuter!.JacketSetpointC, -50, 200)));

        // ③ 控中选回「旧」：这一场照旧是新
        r.Loop.SetOuterAlgorithm(1, false);
        var sel2 = r.Count;
        await r.UntilCycles(sel2 + 40);
        Assert.All(r.Cycles()[(sel2 + 1)..], x => Assert.True(x.Res.Ch1.LiveOuterInControl));
        Assert.True(r.Loop.GetLiveOuterStatus(1).PendingUntilRestart);

        // ④ 停控、再开控：回到旧（旧外环起步那句「积分起点」又出来了，algo 0、outer_i 有数）
        await r.Loop.StopChannelAsync(1);
        var startNotices = r.Notices().Count(x => x.Text.Contains("积分起点"));
        await r.Loop.StartChannelAsync(1, 24);
        var re2 = r.Count;
        await r.UntilCycles(re2 + 40);
        Assert.False(r.Loop.IsLiveOuterInControl(1));
        Assert.All(r.Cycles()[(re2 + 2)..], x => Assert.True(!x.Res.Ch1.LiveOuterInControl && double.IsFinite(x.Res.Ch1.OuterI)));
        Assert.Equal(startNotices + 1, r.Notices().Count(x => x.Text.Contains("积分起点")));
        Assert.Empty(r.Trips());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task D4_挂起中再开控_同一场沿用开控时锁的_新模式说一句_旧模式什么都不多说(bool live)
    {
        // 会话切热源序列：关输出、挂起回路（Active 还是 true），这时来一条下发 → Rd105TemperatureControl.HostArmAsync 第三个条件 → 再走一遍 StartChannelAsync
        await using var r = new Rig();
        r.Loop.SetOuterAlgorithm(1, live);
        var s0 = await r.StartTogether(24, 1);
        await r.UntilCycles(s0 + 20);
        Assert.Equal(live, r.Loop.IsLiveOuterInControl(1));
        r.Loop.SetOuterAlgorithm(1, !live);                            // 控中改了选择
        r.Loop.SuspendOutput(1, true);
        await r.UntilCycles(r.Count + 5);
        Assert.True(r.Loop.GetChannelStatus(1).Active);                // 钉住：这条路上调之前回路还在控
        var before = r.Notices().Length;
        r.Loop.SuspendOutput(1, false);
        await r.Loop.StartChannelAsync(1, 25);
        var re = r.Count;
        await r.UntilCycles(re + 30);
        Assert.Equal(live, r.Loop.IsLiveOuterInControl(1));            // 沿用，不按此刻的选择重锁
        Assert.All(r.Cycles()[(re + 2)..], x => Assert.Equal(live, x.Res.Ch1.LiveOuterInControl));
        var added = r.Notices()[before..].Where(x => x.Ch == 1).Select(x => x.Text).ToArray();
        if (live)
            Assert.Equal(new[] { "通道1切换中途重新开控：外环沿用这一场开控时定的新算法（改了选择要停控后重新开控才用上）" }, added);
        else
            Assert.True(added.Length == 1 && added[0].StartsWith("通道1串级外环积分起点"), string.Join("｜", added));   // 跟以前一样：旧外环重新起步那一句
    }

    public enum Downgrade { ShadowStopped, NotBoth, Jacket }

    [Theory]
    [InlineData(Downgrade.ShadowStopped)]
    [InlineData(Downgrade.NotBoth)]
    [InlineData(Downgrade.Jacket)]
    public async Task D4_选了新却不满足条件_开控锁成旧_说一句为什么(Downgrade why)
    {
        await using var r = new Rig();
        switch (why)
        {
            case Downgrade.ShadowStopped:
                var stopped = new LiveOuterShadow();
                stopped.Stop("测试：一上来就停着");
                Set(r.Loop, 1, "Live", stopped);
                break;
            case Downgrade.NotBoth:
                r.Loop.SetActuatorMode(1, ActuatorMode.Bidirectional);
                break;
            case Downgrade.Jacket:
                r.Loop.SetStrategy(1, ControlStrategy.Direct);
                break;
        }
        r.Loop.SetOuterAlgorithm(1, true);
        var s0 = await r.StartTogether(24, 1);
        await r.UntilCycles(s0 + 30);
        Assert.False(r.Loop.IsLiveOuterInControl(1));
        Assert.All(r.Cycles()[(s0 + 1)..], x => Assert.False(x.Res.Ch1.LiveOuterInControl));
        var st = r.Loop.GetLiveOuterStatus(1);
        Assert.True(st.Requested && !st.InControl && st.Active);
        Assert.NotNull(st.LockNote);
        var said = Assert.Single(r.Notices(), x => x.Ch == 1 && x.Text.StartsWith("通道1选了外环新算法，但"));
        Assert.Equal("warn", said.Level);
        Assert.Contains(why switch { Downgrade.ShadowStopped => "算出错停了", Downgrade.NotBoth => "双向挡", _ => "只管釜内控温" }, said.Text);
        Assert.Contains(st.LockNote!, said.Text);
        if (why != Downgrade.Jacket) Assert.Single(r.Notices(), x => x.Text.Contains("积分起点"));   // 旧外环照常起步
        NoCountedWords(new[] { said.Text });

        // 停控清锁定和原因
        await r.Loop.StopChannelAsync(1);
        Assert.Null(r.Loop.GetLiveOuterStatus(1).LockNote);
        Assert.False(Get<bool>(r.Loop, 1, "LiveInControl"));
    }

    [Fact]
    public async Task D4_停控清掉锁定_立刻不算新外环在控()
    {
        await using var r = new Rig();
        r.Loop.SetOuterAlgorithm(1, true);
        var s0 = await r.StartTogether(24, 1);
        await r.UntilCycles(s0 + 10);
        Assert.True(r.Loop.IsLiveOuterInControl(1));
        Assert.True(Get<bool>(r.Loop, 1, "LiveInControl"));
        await r.Loop.StopChannelAsync(1);
        Assert.False(r.Loop.IsLiveOuterInControl(1));
        Assert.False(Get<bool>(r.Loop, 1, "LiveInControl"));
        Assert.True(r.Loop.GetOuterAlgorithm(1));                     // 选择留着：下次开控还是新
        var stop = r.Count;
        await r.UntilCycles(stop + 10);
        Assert.All(r.Cycles()[(stop + 1)..], x => Assert.False(x.Res.Ch1.Active || x.Res.Ch1.LiveOuterInControl));
    }

    // ───────────────────────────── D5 在控中影子停了 ─────────────────────────────

    [Fact]
    public async Task D5_在控中新外环停了_夹套目标保持_过时限停控_只说一次()
    {
        await using var r = new Rig();
        r.Loop.OuterLossGrace = TimeSpan.FromSeconds(0.8);
        var shadow = new FailableShadow();
        Set(r.Loop, 1, "Live", shadow);
        r.Loop.SetOuterAlgorithm(1, true);
        var s0 = await r.StartTogether(24, 1);
        await r.UntilCycles(s0 + 30);
        shadow.Fail = true;
        await r.Until(() => r.Trips().Length > 0, "影子停了以后停控", 10000);
        await r.UntilCycles(r.Count + 5);
        var c = r.Cycles()[(s0 + 1)..];
        Assert.True(shadow.Stopped);
        var lastWith = Array.FindLastIndex(c, x => x.Res.Ch1.LiveOuter is not null);
        var hold = c[(lastWith + 1)..].TakeWhile(x => x.Res.Ch1.Active).ToArray();
        Assert.True(hold.Length >= 10, $"保持期只有 {hold.Length} 拍");
        var kept = c[lastWith].Res.Ch1.InnerSetpointC;
        Assert.All(hold, x =>
        {
            Assert.True(x.Res.Ch1.LiveOuterInControl);
            Assert.Equal(kept, x.Res.Ch1.InnerSetpointC);              // 保持上一拍的夹套目标
        });
        var dur = (hold[^1].Res.Snapshot.Timestamp - c[lastWith].Res.Snapshot.Timestamp).TotalSeconds;
        Assert.InRange(dur, 0.6, 3.0);                                 // 时限 0.8 s（墙钟抖一抖）

        var trip = Assert.Single(r.Trips());
        Assert.Contains("都没有读数", trip.Reason);
        Assert.Contains("算出错停了", trip.Reason);
        var n = r.Notices().Where(x => x.Ch == 1).Select(x => x.Text).ToArray();
        var said = Assert.Single(n, t => t.Contains("新外环算出错"));
        Assert.Contains("夹套目标先保持在", said);
        Assert.DoesNotContain(n, t => t.Contains("控温不受影响"));   // 第 0 步那句在新模式下是错的
        Assert.DoesNotContain(n, t => t.Contains("这一拍没有读数")); // 停了的那一句已经说过，不另说
        Assert.False(r.Loop.IsLiveOuterInControl(1));
        NoCountedWords(n);
        // 再开控：影子停了 → 锁成旧
        await r.Loop.StartChannelAsync(1, 24);
        Assert.False(r.Loop.IsLiveOuterInControl(1));
        Assert.Contains("算出错停了", r.Loop.GetLiveOuterStatus(1).LockNote);
    }

    // ───────────────────────────── B10 / B11 跑飞「放热」豁免 ─────────────────────────────

    public enum Runaway { Exotherm, TecNotEngaged, ReactorColder, OldLoop }

    [Theory]
    [InlineData(Runaway.Exotherm)]
    [InlineData(Runaway.TecNotEngaged)]
    [InlineData(Runaway.ReactorColder)]
    [InlineData(Runaway.OldLoop)]
    public async Task B10_B11_制冷顶满夹套还在被带着往上走_新模式TEC真接着且釜内比夹套热不停只说一次_否则照停(Runaway kind)
    {
        // 釜内固定、夹套从开控那一刻起 1 K/s 往上走（手摆）；目标 15 远在釜内下面 → 夹套目标远在夹套下面 → 内环贴制冷下限。
        // 跑飞检测换成压缩的（背离 0.5 K、饱和 1 s、宽限 0.3 s）：1 s 以后判「饱和期间温度持续背离」
        await using var r = new Rig();
        var plant = new ManualPlant(trC: kind == Runaway.ReactorColder ? 20 : 40, tjC: 25, ratePerS: 1.0);
        r.UseManual1(plant);
        Set(r.Loop, 1, "Runaway", new RunawayDetector(0.5, 1.0, 0.3));
        r.Loop.SetOuterAlgorithm(1, kind != Runaway.OldLoop);
        r.Loop.SetTecEngaged(1, kind != Runaway.TecNotEngaged);
        var s0 = await r.StartTogether(15, 1);
        Interlocked.Exchange(ref plant.RampFromTicks, DateTime.Now.Ticks);
        Assert.Equal(kind != Runaway.OldLoop, r.Loop.IsLiveOuterInControl(1));

        if (kind != Runaway.Exotherm)
        {
            await r.Until(() => r.Trips().Length > 0, "照停", 15000);
            var trip = Assert.Single(r.Trips());
            Assert.Contains("输出饱和期间温度持续背离设定值", trip.Reason);
            Assert.DoesNotContain(r.Notices(), x => x.Text.Contains("放热"));
            Assert.DoesNotContain(r.Notices(), x => x.Text.Contains("没在出力"));   // 【本地改动】（S2）不是「只差电流」的那种：不多说那一句；旧模式一句不走
            return;
        }

        // 豁免：过了检测窗也不停，说一次
        await Task.Delay(2500);
        Assert.Empty(r.Trips());
        Assert.Single(r.Notices(), x => x.Text.Contains(ExoSaid));
        var lowSat = r.Cycles()[(s0 + 1)..].Where(x => x.Res.Ch1.Active).ToArray();
        Assert.True(lowSat.All(x => x.Res.Ch1.LiveOuterInControl));
        Assert.True(r.Loop.GetChannelStatus(1).LastDutyPercent <= -90 + 1e-6, $"内环没贴制冷下限：{r.Loop.GetChannelStatus(1).LastDutyPercent}");
        // 【本地改动】（第 1 步复核）豁免着的拍周期快照上标出来（会话据此上红条）；豁免前没标
        Assert.True(lowSat[^1].Res.Ch1.LiveExoExempt);
        Assert.False(lowSat[0].Res.Ch1.LiveExoExempt);

        // 条件撤了（目标改到釜内上面：要加热，不再贴制冷下限）→ 重新武装；再回到 15 → 再满一个检测窗又说一次，照样不停
        r.Loop.SetSetpoint(1, 60);
        await Task.Delay(500);
        Assert.True(r.Loop.GetChannelStatus(1).LastDutyPercent > -90 + 1e-6);
        Assert.False(r.Cycles()[^1].Res.Ch1.LiveExoExempt);            // 撤了：快照上也撤
        r.Loop.SetSetpoint(1, 15);
        await r.Until(() => r.Notices().Count(x => x.Text.Contains(ExoSaid)) == 2, "撤了再成立再说一次", 10000);
        await Task.Delay(500);
        Assert.Empty(r.Trips());
        Assert.Equal(2, r.Notices().Count(x => x.Text.Contains(ExoSaid)));
        Assert.True(r.Loop.IsLiveOuterInControl(1));
        var said = r.Notices().First(x => x.Text.Contains(ExoSaid)).Text;
        Assert.Contains("不停控", said);
        // 【本地改动】（第 1 步复核）这一条也放过「冷却没在出力」（断水 / TEC 开路）：不说「保持满冷」
        // 【本地改动】（2026-11-17，S2）豁免还要 TEC 电流在出力（Rig 摆的 3 A）：话里带电流，断水电流看不出，照样叫人查冷却水
        Assert.StartsWith("通道1放热或冷却没在出力：", said);
        Assert.Contains("TEC 电流 3.00 A（在出力）", said);
        Assert.Contains("电流看不出断水，请人工查冷却水", said);
        Assert.DoesNotContain("保持满冷", said);
        NoCountedWords(r.Notices().Select(x => x.Text));
    }

    /// <summary>豁免那一句里固定的那一段（前面是通道号和「放热或冷却没在出力：」）。</summary>
    private const string ExoSaid = "TEC 制冷已顶满";

    /// <summary>B10 那一场：釜内 40 固定、夹套从 25 起 1 K/s 往上走、目标 15、压缩的跑飞检测、新外环、TEC 真接着。返回开控后第一拍下标。</summary>
    private static async Task<int> StartExotherm(Rig r, double trC = 40, bool tecEngaged = true)
    {
        var plant = new ManualPlant(trC: trC, tjC: 25, ratePerS: 1.0);
        r.UseManual1(plant);
        Set(r.Loop, 1, "Runaway", new RunawayDetector(0.5, 1.0, 0.3));
        r.Loop.SetOuterAlgorithm(1, true);
        r.Loop.SetTecEngaged(1, tecEngaged);
        var s0 = await r.StartTogether(15, 1);
        Interlocked.Exchange(ref plant.RampFromTicks, DateTime.Now.Ticks);
        Assert.True(r.Loop.IsLiveOuterInControl(1));
        return s0;
    }

    // ───────────────────────────── B12 放热豁免中 Tr 丢了（第 1 步复核）─────────────────────────────
    // 复核查实：豁免拿「这一拍」釜内读数比夹套，Tr 掉一拍（宇电质量不好喂 NaN / 10 s 没新数置 NaN）就 NaN > 夹套 = 假，豁免当拍失效、
    // 跑飞判据还成立 → 当拍停控、TEC 断电，抢在 E3 那 30 s 宽限之前——放热正猛的时候冷全没了。改成拿最近一拍有效的 Tr 比，宽限照 E3 走

    [Fact]
    public async Task B12a_放热豁免中Tr一直丢_宽限内不停控仍满冷_过了宽限按Tr丢失停_不按跑飞停()
    {
        await using var r = new Rig();
        r.Loop.OuterLossGrace = TimeSpan.FromSeconds(1.5);
        await StartExotherm(r);
        await r.Until(() => r.Notices().Any(x => x.Text.Contains(ExoSaid)), "豁免说过一次", 10000);
        var lostAt = r.Count;
        Volatile.Write(ref r.TrLost[0], true);
        await Task.Delay(800);
        Assert.True(r.Trips().Length == 0, "Tr 一丢当拍就停了：" + string.Join("；", r.Trips().Select(t => t.Reason)));
        Assert.True(r.Loop.GetChannelStatus(1).LastDutyPercent <= -90 + 1e-6, $"保持期没满冷：{r.Loop.GetChannelStatus(1).LastDutyPercent}");
        var held = r.Cycles()[lostAt..].Where(x => x.Res.Ch1.OuterHoldSeconds is not null).ToArray();
        Assert.True(held.Length >= 5, $"保持期只有 {held.Length} 拍");
        Assert.All(held, x => Assert.True(x.Res.Ch1.Active && x.Res.Ch1.LiveOuterInControl && x.Res.Ch1.LiveExoExempt));

        await r.Until(() => r.Trips().Length > 0, "过了宽限按 Tr 丢失停", 10000);
        var trip = Assert.Single(r.Trips());
        Assert.Contains("读数丢失超过", trip.Reason);
        Assert.DoesNotContain("输出饱和期间温度持续背离设定值", trip.Reason);
        Assert.Single(r.Notices(), x => x.Text.Contains(ExoSaid));
    }

    [Fact]
    public async Task B12b_放热豁免中Tr掉一小段_宽限内回来_不停控_豁免那句只说过一次()
    {
        await using var r = new Rig();
        r.Loop.OuterLossGrace = TimeSpan.FromSeconds(1.5);
        await StartExotherm(r);
        await r.Until(() => r.Notices().Any(x => x.Text.Contains(ExoSaid)), "豁免说过一次", 10000);
        Volatile.Write(ref r.TrLost[0], true);
        await Task.Delay(400);
        Assert.True(r.Trips().Length == 0, "Tr 一丢当拍就停了：" + string.Join("；", r.Trips().Select(t => t.Reason)));
        Volatile.Write(ref r.TrLost[0], false);
        await r.Until(() => r.Notices().Any(x => x.Text.Contains("釜内 Tr 已恢复")), "Tr 回来", 5000);
        await Task.Delay(600);
        Assert.True(r.Trips().Length == 0, string.Join("；", r.Trips().Select(t => t.Reason)));
        Assert.True(r.Loop.IsLiveOuterInControl(1));
        Assert.Single(r.Notices(), x => x.Text.Contains(ExoSaid));     // Tr 回来不重说
        Assert.True(r.Cycles()[^1].Res.Ch1.LiveExoExempt);
        Assert.True(r.Loop.GetChannelStatus(1).LastDutyPercent <= -90 + 1e-6);
    }

    [Theory]
    [InlineData(Runaway.TecNotEngaged)]
    [InlineData(Runaway.ReactorColder)]
    public async Task B12c_Tr丢失不给豁免_TEC没接着或釜内不比夹套热_保持期里照跑飞停(Runaway kind)
    {
        // 开控后马上丢 Tr（宽限 6 s，比检测窗 1.3 s 长得多：影子没读数时 2 s 才推一拍，开控后第一拍夹套目标可能还是开控前那一份、
        // 内环要等下一拍才顶满）：跑飞在保持期里判到，照原来的跑飞停——Tr 丢失不是放过真故障的理由
        await using var r = new Rig();
        r.Loop.OuterLossGrace = TimeSpan.FromSeconds(6);
        await StartExotherm(r, trC: kind == Runaway.ReactorColder ? 20 : 40, tecEngaged: kind != Runaway.TecNotEngaged);
        Volatile.Write(ref r.TrLost[0], true);
        await r.Until(() => r.Trips().Length > 0, "照停", 10000);
        var trip = Assert.Single(r.Trips());
        Assert.Contains("输出饱和期间温度持续背离设定值", trip.Reason);
        Assert.DoesNotContain("读数丢失超过", trip.Reason);
        Assert.DoesNotContain(r.Notices(), x => x.Text.Contains(ExoSaid));
        Assert.Contains(r.Cycles(), x => x.Res.Ch1.OuterHoldSeconds is not null);   // 确实是在保持期里判的
    }

    // ───────────────────────────── 停控竞态（第 1 步复核）─────────────────────────────
    // 复核查实：回路这一拍正卡在写占空比（RunControlStepAsync 里的 WriteDutyAsync），操作人停控（另一条线程）同步那半段先把 Active / LiveInControl 清了；
    // 回路写完回来那一拍的快照带着 Active = true、Cascade = true，可原来按「回来以后再读一遍 s.Active」标 algo，读到 false 不标——
    // 记录「停止」前多一行 kind = Tr、algo = 0、outer_p/i/d/fin 是旧外环残值。改成按这一拍实际走的那一支标

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 停控竞态_回路正写占空比时操作人停控_那一拍按它实际走的那一支标_新外环algo1且outer列空_旧外环照旧(bool live)
    {
        DutyGate? gate = null;
        await using var r = new Rig(wrap: t => gate = new DutyGate(t));
        r.Loop.SetOuterAlgorithm(1, live);
        var s0 = await r.StartTogether(24, 1);
        await r.UntilCycles(s0 + 20);
        Assert.Equal(live, r.Loop.IsLiveOuterInControl(1));

        gate!.Armed = true;
        Assert.True(gate.Blocked.Wait(5000), "回路没去写 TC1 的占空比");
        var raceAt = r.Count;                                          // 卡住的这一拍还没发 CycleCompleted：它是下一条
        var stop = Task.Run(() => r.Loop.StopChannelAsync(1));
        await r.Until(() => !r.Loop.GetChannelStatus(1).Active && !r.Loop.IsLiveOuterInControl(1), "停控同步那半段先清了状态", 5000);
        gate.Release.Set();
        await stop.WaitAsync(TimeSpan.FromSeconds(10));
        await r.UntilCycles(raceAt + 3);

        var raced = r.Cycles()[raceAt].Res.Ch1;
        Assert.True(raced.Active && raced.Cascade, $"这一拍不是竞态那一拍：{raced}");   // 回路这一拍照算完、写完了
        Assert.Equal(live, raced.LiveOuterInControl);
        if (live)
            Assert.True(double.IsNaN(raced.OuterP) && double.IsNaN(raced.OuterI) && double.IsNaN(raced.OuterD) && double.IsNaN(raced.OuterFinishC),
                $"新外环在控那一拍 outer_* 该是空的：P {raced.OuterP} I {raced.OuterI} D {raced.OuterD} fin {raced.OuterFinishC}");
        else
            Assert.True(double.IsFinite(raced.OuterP) && double.IsFinite(raced.OuterI), "旧外环那一拍照旧记旧外环的数");
        Assert.All(r.Cycles()[(raceAt + 1)..], x => Assert.False(x.Res.Ch1.Active || x.Res.Ch1.LiveOuterInControl));
        Assert.Empty(r.Faults());
    }

    // ───────────────────────────── S2「看电流再决定」（2026-11-17）─────────────────────────────
    // 用户 2026-11-16 选的：新模式「制冷顶满、TEC 真接着、釜内比夹套热」还要 TEC 电流在出力才豁免（只报警）；电流不对 / 看不到 = 不豁免，照旧程序停控，
    // 停之前多说一句 TEC 没在出力。电流就用回路每 4 拍那一次读（告警字 + 两路 CURRENT），不加串口读。门槛 / 时窗 / 样本规矩的白盒在 LiveOuterS2CurrentTests

    /// <summary>【本地改动】（S2）串口外面套一层：<see cref="Fail"/> 置位后「TC1:CURRENT=?」那一笔一写就抛（读失败；真机上是 2 s 重发都没应答，回路那一拍一样是抛）。</summary>
    private sealed class CurrentFailGate(ISerialTransport inner) : ISerialTransport
    {
        public volatile bool Fail;
        public bool IsOpen => inner.IsOpen;
        public void Open() => inner.Open();
        public void Close() => inner.Close();
        public void DiscardInput() => inner.DiscardInput();
        public int Read(byte[] buffer, int offset, int count, int timeoutMs) => inner.Read(buffer, offset, count, timeoutMs);
        public void Dispose() => inner.Dispose();

        public void Write(byte[] buffer, int offset, int count)
        {
            if (Fail && Encoding.ASCII.GetString(buffer, offset, count).StartsWith("TC1:CURRENT=?", StringComparison.Ordinal))
                throw new IOException("测试：读 TC1 电流失败");
            inner.Write(buffer, offset, count);
        }
    }

    [Fact]
    public async Task S2_电流0点02A_TEC没在出力_先说一句没在出力再按跑飞原话停控()
    {
        await using var r = new Rig();
        r.Loop.SetTecCurrentLimit(1, 5);                               // 门槛 √(0.001 × 5) = 0.07 A（TEC 一开始就接着，没有空载样本，按 1 mA）
        r.SetCurrent(1, 0.02);
        await StartExotherm(r);
        await r.Until(() => r.Trips().Length > 0, "照停", 15000);
        var trip = Assert.Single(r.Trips());
        Assert.Contains("输出饱和期间温度持续背离设定值", trip.Reason);   // 原来那句停控文字照原样
        var ev = r.Events().Where(e => e.Ch == 1).ToArray();
        var iNote = Array.FindIndex(ev, e => e.Kind == "notice" && e.Text.Contains("TEC 没在出力"));
        var iTrip = Array.FindIndex(ev, e => e.Kind == "trip");
        Assert.True(iNote >= 0 && iNote < iTrip, "没有先说一句「TEC 没在出力」再停控：" + string.Join("｜", ev.Select(e => e.Text)));
        var said = ev[iNote].Text;
        Assert.Equal("通道1制冷顶满、夹套还被釜内往上带，但 TEC 没在出力（近 30 s 电流最大 0.02 A，门槛 0.07 A）——不当放热，按跑飞停控：" +
                     "请查 TEC 功率线接触器、TEC 接线、温控器告警", said);
        Assert.DoesNotContain(r.Notices(), x => x.Text.Contains(ExoSaid));   // 没豁免过
        NoCountedWords(r.Notices().Select(x => x.Text));
    }

    [Fact]
    public async Task S2_电流读0但告警字报本路限流中_算在出力_豁免不停控_话里说限流中()
    {
        await using var r = new Rig();
        r.SetCurrent(1, 0);
        r.Dev.Set(null, "ERRORCODE", 1 << 6);                          // bit6 = 通道 1 输出电流到了 SETCURRENT（限流中）
        await StartExotherm(r);
        await r.Until(() => r.Notices().Any(x => x.Text.Contains(ExoSaid)), "豁免说过一次", 10000);
        await Task.Delay(2000);
        Assert.Empty(r.Trips());
        var said = Assert.Single(r.Notices(), x => x.Text.Contains(ExoSaid)).Text;
        Assert.Contains("TEC 电流 0.00 A（在出力，限流中）", said);
        Assert.True(r.Loop.IsLiveOuterInControl(1));
    }

    [Fact]
    public async Task S2_豁免成立后电流读不到_W秒以后按跑飞停控_说没读到电流()
    {
        CurrentFailGate? gate = null;
        await using var r = new Rig(wrap: t => gate = new CurrentFailGate(t));
        r.Loop.OuterLossGrace = TimeSpan.FromSeconds(2);               // W = max(2, 15 × 4 × 20 ms = 1.2) = 2 s
        await StartExotherm(r);
        await r.Until(() => r.Notices().Any(x => x.Text.Contains(ExoSaid)), "豁免说过一次", 10000);
        await Task.Delay(500);
        Assert.Empty(r.Trips());
        var failAt = DateTime.UtcNow;
        gate!.Fail = true;
        await r.Until(() => r.Trips().Length > 0, "电流读不到 W 秒以后停", 15000);
        var after = (DateTime.UtcNow - failAt).TotalSeconds;
        var trip = Assert.Single(r.Trips());
        Assert.Contains("输出饱和期间温度持续背离设定值", trip.Reason);
        Assert.True(after >= 1.8, $"电流读不到才 {after:F2} s 就停了（W = 2 s）");   // 最后一个好样本最多比 Fail 早一个电流采样间隔（4 拍 ≈ 80 ms）
        // 【本地改动】（2026-11-17 复核查实）读不到电流 ≠ TEC 没在出力：话里不许断定「TEC 没在出力」、不许把人先引到接触器 / 接线上，
        // 说清是读电流失败、确认不了，先查温控器通讯（有样本、电流低的那一种「没在出力」原话不变，见 S2_电流0点02A）
        var said = Assert.Single(r.Notices(), x => x.Text.StartsWith("通道1制冷顶满、夹套还被釜内往上带，近 2 s 没读到 TEC 电流")).Text;
        Assert.DoesNotContain("TEC 没在出力", said);
        Assert.Contains("确认不了 TEC 在出力", said);
        Assert.Contains("先查温控器通讯", said);
        Assert.EndsWith("再查 TEC 功率线接触器、TEC 接线", said);
        NoCountedWords(new[] { said });
        Assert.NotEmpty(r.Faults());                                   // 那几拍确实是读电流失败
    }

    // ───────────────────────────── S1「读数对不上就停控」（2026-11-17）─────────────────────────────
    // 判据（ReadingCheck）的时间常数是现场秒（τc 2850 s、τ̂₀ 950 s、窗 30 s）：这几条把 A 路影子换成 TimeScale = 25 的（外环 80 ms 一拍 = 现场 2 s 一拍，
    // 墙钟 1 s = 现场 25 s），判据跟影子同一个现场秒表（LiveOuterShadow.ClockS）。小釜是测试编的，算出来的数没有物理意义——这里只看接线：
    // 谁在什么时候喂、判到了走哪条停控、说什么、旧模式碰不碰、开控 / 停控清不清

    private const double S1Scale = 25;

    [Fact]
    public async Task S1_新模式釜内读数冻在目标下面远处_S1判到停控_说S1那句话_停控清掉判据_重新开控是新的一份()
    {
        await using var r = new Rig();
        Set(r.Loop, 1, "Live", new LiveOuterShadow { TimeScale = S1Scale });
        Assert.Null(Get<ReadingCheck?>(r.Loop, 1, "S1"));              // 没开控：没有判据
        r.Loop.SetOuterAlgorithm(1, true);
        var s0 = await r.StartTogether(24, 1);
        Assert.True(r.Loop.IsLiveOuterInControl(1));
        var chk = Get<ReadingCheck?>(r.Loop, 1, "S1");
        Assert.NotNull(chk);                                           // 锁成新：建了
        await r.UntilCycles(s0 + 60);
        Assert.NotNull(Get<ReadingCheckBeat?>(r.Loop, 1, "S1Beat"));  // 在算
        Assert.Contains(r.Cycles()[(s0 + 2)..], x => x.Res.Ch1.ReadingBeat is not null);   // 周期快照带出去（逐拍记录 s1_ 列）
        var freezeAt = DateTime.UtcNow;
        Volatile.Write(ref r.TrForce[0], 10.0);                        // 读数一步掉到 10 冻住（目标 24、冻住前 22 上下）
        await r.Until(() => r.Trips().Length > 0, "S1 停控", 30000);
        var took = (DateTime.UtcNow - freezeAt).TotalSeconds;
        var trip = Assert.Single(r.Trips());
        Assert.Equal(1, trip.Ch);
        Assert.StartsWith("通道1釜内读数跟夹套对不上，已停控：", trip.Reason);
        Assert.EndsWith("查好釜内探头后重新下发目标", trip.Reason);
        Assert.Contains("加热中釜内读数从", trip.Reason);               // B：加热时读数往下掉、而且不回
        Assert.Contains("掉到 10.00 ℃", trip.Reason);
        Assert.DoesNotContain("输出饱和期间", trip.Reason);
        NoCountedWords(new[] { trip.Reason });
        NoCountedWords(r.Notices().Select(x => x.Text));
        Assert.False(r.Loop.IsLiveOuterInControl(1));
        Assert.Null(Get<ReadingCheck?>(r.Loop, 1, "S1"));              // 停控清
        Assert.Null(Get<string?>(r.Loop, 1, "S1StopPending"));
        Assert.DoesNotContain(r.Notices(), x => x.Text.Contains("读数没跟着夹套降"));
        Console.WriteLine($"S1 接线：读数冻住后墙钟 {took:F1} s（现场约 {took * S1Scale:F0} s）停控");

        // 重新开控：新的一份判据（不是上一场那个、没带着上一场的状态）；读数对了不再停
        Volatile.Write(ref r.TrForce[0], double.NaN);
        await r.Loop.StartChannelAsync(1, 24);
        var chk2 = Get<ReadingCheck?>(r.Loop, 1, "S1");
        Assert.NotNull(chk2);
        Assert.NotSame(chk, chk2);
        Assert.Null(Get<string?>(r.Loop, 1, "S1StopPending"));
        var re = r.Count;
        await r.UntilCycles(re + 120);
        Assert.Single(r.Trips());
        Assert.True(r.Loop.IsLiveOuterInControl(1));
    }

    [Fact]
    public async Task S1_旧模式同样冻住读数_判据一拍不算一句不说_两路控制量逐位相同()
    {
        // 两路都是旧外环、同样的影子（TimeScale 25）、同样冻住的读数；新模式同样冻住几秒就停了（上一条）——旧模式一直控着，S1 一个字段都没碰
        await using var r = new Rig();
        Set(r.Loop, 1, "Live", new LiveOuterShadow { TimeScale = S1Scale });
        Set(r.Loop, 2, "Live", new LiveOuterShadow { TimeScale = S1Scale });
        r.Loop.SetOuterAlgorithm(1, true);                             // 拨「新」又拨回「旧」：开控时锁的是「旧」
        r.Loop.SetOuterAlgorithm(1, false);
        var s0 = await r.StartTogether(24, 1, 2);
        await r.UntilCycles(s0 + 60);
        Volatile.Write(ref r.TrForce[0], 10.0);
        Volatile.Write(ref r.TrForce[1], 10.0);
        await Task.Delay(8000);
        await r.Loop.ShutdownAsync();
        Assert.Empty(r.Trips());
        Assert.Empty(r.Faults());
        for (var ch = 1; ch <= 2; ch++)
        {
            Assert.Null(Get<ReadingCheck?>(r.Loop, ch, "S1"));
            Assert.Null(Get<ReadingCheckBeat?>(r.Loop, ch, "S1Beat"));
            Assert.Null(Get<LiveOuterReadout?>(r.Loop, ch, "S1Readout"));
        }
        Assert.DoesNotContain(r.Notices(), x => x.Text.Contains("釜内读数跟夹套对不上") || x.Text.Contains("读数没跟着夹套"));
        var all = r.Cycles();
        Assert.True(all.Length > s0 + 200, $"才 {all.Length} 拍");
        for (var i = 0; i < all.Length; i++)
        {
            var x = all[i];
            Assert.True(x.Res.Ch1 with { LiveOuter = null } == x.Res.Ch2 with { LiveOuter = null }, $"第 {i} 拍两路的控制量分叉了");
            Assert.True(x.Duty1 == x.Duty2, $"第 {i} 拍写到设备上的占空比不一样");
            Assert.Null(x.Res.Ch1.ReadingAlarm);
            Assert.Null(x.Res.Ch1.ReadingBeat);
        }
    }

    [Fact]
    public async Task S1_新模式读数冻在目标上面_只报警一次不停控_快照带红条_Tr丢一下不重起_读数回来报解除()
    {
        await using var r = new Rig();
        Set(r.Loop, 1, "Live", new LiveOuterShadow { TimeScale = S1Scale });
        r.Loop.SetOuterAlgorithm(1, true);
        var s0 = await r.StartTogether(24, 1);
        await r.UntilCycles(s0 + 60);
        Volatile.Write(ref r.TrForce[0], 34.0);                        // 目标 24、读数冻在 34：新外环一路往下推夹套
        const string AlarmHead = "通道1釜内读数没跟着夹套降：";
        await r.Until(() => r.Notices().Any(x => x.Text.StartsWith(AlarmHead)), "报警", 30000);
        var said = Assert.Single(r.Notices(), x => x.Text.StartsWith(AlarmHead));
        Assert.Equal("warn", said.Level);
        Assert.Contains("（34.00 ℃）", said.Text);
        Assert.Contains("按「放热只报不停」不停控；请人看一眼釜内探头和釜里情况", said.Text);
        await r.UntilCycles(r.Count + 5);
        Assert.Equal(said.Text, r.Cycles()[^1].Res.Ch1.ReadingAlarm);   // 周期快照带着那句话：会话上红条
        var tRef = Get<ReadingCheckBeat?>(r.Loop, 1, "S1Beat")!.Value.TRef;

        // Tr 丢一下（读数 NaN，影子只推模型）：判据只记 Ŵ、不重起参考，报警不撤不重报
        Volatile.Write(ref r.TrLost[0], true);
        await Task.Delay(250);
        Volatile.Write(ref r.TrLost[0], false);
        await r.UntilCycles(r.Count + 20);
        Assert.Equal(tRef, Get<ReadingCheckBeat?>(r.Loop, 1, "S1Beat")!.Value.TRef);
        Assert.Single(r.Notices(), x => x.Text.StartsWith(AlarmHead));
        Assert.DoesNotContain(r.Notices(), x => x.Text.Contains("报警解除"));
        Assert.Empty(r.Trips());

        // 读数回来（跟着真釜内动了）：撤、说一次；快照上也撤
        Volatile.Write(ref r.TrForce[0], double.NaN);
        await r.Until(() => r.Notices().Any(x => x.Text == "通道1釜内读数又跟着夹套动了，「读数没跟着夹套降」报警解除"), "报警解除", 10000);
        await r.UntilCycles(r.Count + 5);
        Assert.Null(r.Cycles()[^1].Res.Ch1.ReadingAlarm);
        Assert.Equal("info", r.Notices().Single(x => x.Text.Contains("报警解除")).Level);
        Assert.Empty(r.Trips());
        Assert.True(r.Loop.IsLiveOuterInControl(1));
        NoCountedWords(r.Notices().Select(x => x.Text));
    }

    // ───────────────────────────── 2026-11-17 复核查实的几件（S1 / S2）─────────────────────────────

    [Fact]
    public async Task S1_新模式读数冻在目标上面_夹套推到底不动以后_R_A回到门槛以内报警也不撤_快照一直带红条()
    {
        // 复核查实：A 只算最近 τ̂₀（950 s）里夹套推的量，夹套推到底不动以后窗滑过「往下推」那一段，R_A 回到 −0.25 以内，
        // 原来那一拍就撤报警、还说「读数又跟着夹套动了」——读数一拍没动。现在只在读数真动了 0.02 K 才撤。
        // A 路手摆：釜内读数钉在 34（目标 24）、夹套从 25 起 10 K/s 往下走到 5 停住；影子 TimeScale 100（外环 80 ms 一拍 = 现场 8 s，墙钟 1 s = 现场 100 s）
        await using var r = new Rig();
        Set(r.Loop, 1, "Live", new LiveOuterShadow { TimeScale = 100 });
        var plant = new ManualPlant(trC: 34, tjC: 25, ratePerS: -10, floorC: 5);
        r.UseManual1(plant);
        r.Loop.SetOuterAlgorithm(1, true);
        var s0 = await r.StartTogether(24, 1);
        Assert.True(r.Loop.IsLiveOuterInControl(1));
        await r.UntilCycles(s0 + 20);
        Interlocked.Exchange(ref plant.RampFromTicks, DateTime.Now.Ticks);
        const string AlarmHead = "通道1釜内读数没跟着夹套降：";
        await r.Until(() => r.Notices().Any(x => x.Text.StartsWith(AlarmHead)), "报警", 30000);
        var minRa = 0.0;
        await r.Until(() =>
        {
            if (Get<ReadingCheckBeat?>(r.Loop, 1, "S1Beat") is not { Valid: true } b) return false;
            minRa = Math.Min(minRa, b.Ra);
            return minRa <= -1 && b.Ra > -0.1;                           // 推够了、而且窗已经滑过去：R_A 回到 0 附近
        }, "R_A 回到门槛以内", 60000);
        await r.UntilCycles(r.Count + 40);
        var beat = Get<ReadingCheckBeat?>(r.Loop, 1, "S1Beat")!.Value;
        Console.WriteLine($"S1 报警锁住：R_A 最低 {minRa:F2}、此刻 {beat.Ra:F3}、30 s 平均 {beat.Y30:F2}");
        Assert.Equal(34.0, beat.Y30);                                     // 读数一拍没动
        Assert.DoesNotContain(r.Notices(), x => x.Text.Contains("报警解除") || x.Text.Contains("又跟着夹套动了"));
        Assert.Single(r.Notices(), x => x.Text.StartsWith(AlarmHead));
        var cycles = r.Cycles();
        var first = Array.FindIndex(cycles, x => x.Res.Ch1.ReadingAlarm is not null);
        Assert.True(first > s0, "快照上没出过红条");
        for (var i = first; i < cycles.Length; i++)
            Assert.True(cycles[i].Res.Ch1.ReadingAlarm is not null, $"第 {i} 拍快照上的红条没了（报警起在第 {first} 拍）");
        Assert.Empty(r.Trips());
        Assert.True(r.Loop.IsLiveOuterInControl(1));
    }

    [Fact]
    public async Task S1_B停控那句话_也说可能读数是对的在连续加冷料_带此刻夹套温度_说停控后夹套存的热还会让釜内往上走_先看是不是在加料()
    {
        // 复核查实：读数都对、计量泵连续打冷料（每分钟让釜掉 2–3 K、连打 8 min）约第 6 min 也会按 B 停，停的那一刻夹套被推到 97–109 ℃，
        // 停控后加热、制冷都断开，夹套存的热灌进釜里、釜内冲过目标（LiveOuterClosedLoopTests 钉着那几个数）。原来那句话只列探头的原因
        await using var r = new Rig();
        Set(r.Loop, 1, "Live", new LiveOuterShadow { TimeScale = S1Scale });
        r.Loop.SetOuterAlgorithm(1, true);
        var s0 = await r.StartTogether(24, 1);
        await r.UntilCycles(s0 + 60);
        Volatile.Write(ref r.TrForce[0], 10.0);
        await r.Until(() => r.Trips().Length > 0, "S1 停控", 30000);
        await r.UntilCycles(r.Count + 3);
        var trip = Assert.Single(r.Trips());
        var why = trip.Reason;
        Assert.StartsWith("通道1釜内读数跟夹套对不上，已停控：加热中釜内读数从", why);
        Assert.Contains("可能是釜内探头提出了液面、插到了别的工位、读数卡住", why);
        Assert.Contains("也可能读数是对的：正在连续加冷料 / 强吸热，掉了", why);
        var m = System.Text.RegularExpressions.Regex.Match(why, @"此刻夹套 (-?\d+\.\d) ℃（釜内读数 10\.00 ℃）");
        Assert.True(m.Success, "话里没有此刻夹套多少度：" + why);
        var tj = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var stopBeat = r.Cycles().First(x => x.Res.Ch1.Active == false && x.Res.Snapshot.Timestamp > r.Cycles()[s0].Res.Snapshot.Timestamp).Res.Ch1;
        Assert.InRange(tj, stopBeat.MeasuredC - 0.051, stopBeat.MeasuredC + 0.051);   // 就是停控那一拍的夹套读数
        Assert.Contains("停控后加热、制冷都断开，夹套里存的热还会让釜内往上走", why);
        Assert.EndsWith("先看是不是正在加料；不是的话查好釜内探头后重新下发目标", why);
        NoCountedWords(new[] { why });
    }

    /// <summary>
    /// 【本地改动】（2026-11-17 复核查实：S2 读点跟 PWM 锁相）串口外面套一层，模拟「CURRENT 是瞬时值、回路原来每 4 拍那一次读一直落在 PWM 关断段」：
    /// 紧跟在 ERRORCODE 后面的那一笔 TC1:CURRENT（原来那一次读）答 0；别的时候读 TC1:CURRENT（另一个相位）答 3 A。
    /// 真机上读点在 2 s 网格上（Period 0.5 s × 4），正好是 1 Hz PWM 周期的 2 倍、0.5 Hz 的 1 倍，相位每个样本只挪 1–2 ms，能连着几十上百个样本落在同一段。
    /// </summary>
    private sealed class PhaseLockGate(FakeRd105Device dev) : ISerialTransport
    {
        private bool _afterError;
        public int OffPhaseReads, OtherPhaseReads;
        public bool IsOpen => dev.IsOpen;
        public void Open() => dev.Open();
        public void Close() => dev.Close();
        public void DiscardInput() => dev.DiscardInput();
        public int Read(byte[] buffer, int offset, int count, int timeoutMs) => dev.Read(buffer, offset, count, timeoutMs);
        public void Dispose() => dev.Dispose();

        public void Write(byte[] buffer, int offset, int count)
        {
            var cmd = Encoding.ASCII.GetString(buffer, offset, count);
            if (cmd.StartsWith("ERRORCODE=?", StringComparison.Ordinal)) _afterError = true;
            else if (cmd.StartsWith("TC1:CURRENT=?", StringComparison.Ordinal))
            {
                if (_afterError) { dev.Set(1, "CURRENT", 0); OffPhaseReads++; }
                else { dev.Set(1, "CURRENT", 3000); OtherPhaseReads++; }
                _afterError = false;
            }
            else _afterError = false;
            dev.Write(buffer, offset, count);
        }
    }

    [Fact]
    public async Task S2_电流读点跟PWM锁相_原来那一次读一直落在关断段读0_另一个相位补读有电流_豁免成立而且不被打断()
    {
        PhaseLockGate? gate = null;
        await using var r = new Rig(wrap: t => gate = new PhaseLockGate((FakeRd105Device)t));
        r.Loop.OuterLossGrace = TimeSpan.FromSeconds(2);               // W = max(2, 15 × 4 × 20 ms = 1.2) = 2 s：补读得一直跟得上，不是靠一个老样本撑着
        await StartExotherm(r);
        await r.Until(() => r.Notices().Any(x => x.Text.Contains(ExoSaid)), "豁免说过一次", 10000);
        await Task.Delay(4000);
        Assert.True(r.Trips().Length == 0, "锁相读 0 把豁免打断、停控了：" + string.Join("；", r.Trips().Select(t => t.Reason)));
        Assert.DoesNotContain(r.Notices(), x => x.Text.Contains("没在出力（近") || x.Text.Contains("没读到 TEC 电流"));
        Assert.Single(r.Notices(), x => x.Text.Contains(ExoSaid));
        Assert.True(r.Cycles()[^1].Res.Ch1.LiveExoExempt);
        Assert.True(r.Loop.IsLiveOuterInControl(1));
        Console.WriteLine($"S2 锁相：原来那一次读（读 0）{gate!.OffPhaseReads} 笔、另一个相位补读 {gate.OtherPhaseReads} 笔");
        Assert.True(gate.OffPhaseReads > 20 && gate.OtherPhaseReads > 20, $"原来那一次读 {gate.OffPhaseReads} 笔、补读 {gate.OtherPhaseReads} 笔");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task S2_有一路按旧外环在控就不补读电流_两路都旧一笔不多读(bool ch1Live)
    {
        // 补读只在没有哪一路按旧外环控时才读：逐拍记录的 cycle_ms 两路共用，补一笔串口读旧模式那一路的 cycle_ms 就变了（旧模式一字不变）。
        // A 路照 B10 那一场（制冷顶满、TEC 真接着、釜内比夹套热），B 路旧外环一起开控：一笔补读都没有（代价：A 路的 S2 照旧只有锁相的那一个读点）
        PhaseLockGate? gate = null;
        await using var r = new Rig(wrap: t => gate = new PhaseLockGate((FakeRd105Device)t));
        var plant = new ManualPlant(trC: 40, tjC: 25, ratePerS: 1.0);
        r.UseManual1(plant);
        Set(r.Loop, 1, "Runaway", new RunawayDetector(0.5, 1.0, 0.3));
        r.Loop.SetOuterAlgorithm(1, ch1Live);
        r.Loop.SetTecEngaged(1, true);
        r.Loop.SetTecEngaged(2, true);
        await r.StartTogether(15, 1, 2);
        Interlocked.Exchange(ref plant.RampFromTicks, DateTime.Now.Ticks);
        Assert.Equal(ch1Live, r.Loop.IsLiveOuterInControl(1));
        Assert.False(r.Loop.IsLiveOuterInControl(2));
        await r.Until(() => r.Trips().Length > 0, "A 路照停（原来那一次读一直读 0）", 15000);
        await Task.Delay(500);
        Assert.Equal(0, gate!.OtherPhaseReads);
        Assert.True(gate.OffPhaseReads > 5);
        Assert.Equal(1, Assert.Single(r.Trips()).Ch);
    }

    [Fact]
    public async Task S2_时窗按单调钟算_合格样本和制冷顶满那一拍记的是单调时刻不是快照墙钟()
    {
        // 复核查实：S2 的 W 秒时窗原来拿快照时刻（DateTime.Now）相减，系统钟对时 / 手动改钟往前跳 28 s 以上当拍豁免失效、关掉满冷，往回跳会让旧样本过了 W 还作保。
        // 现在按 Environment.TickCount64 折成的一条单调时间线记（离墙钟差几十年，一看就分得出）；走的快慢跟真实秒一样
        await using var r = new Rig();
        await StartExotherm(r);
        await r.Until(() => r.Notices().Any(x => x.Text.Contains(ExoSaid)), "豁免说过一次", 10000);
        var a = Get<DateTime?>(r.Loop, 1, "TecDrawAt");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Task.Delay(1000);
        var b = Get<DateTime?>(r.Loop, 1, "TecDrawAt");
        var sat = Get<DateTime>(r.Loop, 1, "LiveSatLowAt");
        var el = sw.Elapsed.TotalSeconds;
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.True(Math.Abs((DateTime.Now - b!.Value).TotalDays) > 1, $"合格样本记的是墙钟：{b:O}（此刻 {DateTime.Now:O}）");
        Assert.InRange((b.Value - a!.Value).TotalSeconds, el - 0.35, el + 0.35);
        Assert.InRange((sat - b.Value).TotalSeconds, -0.35, 0.35);      // 「这一拍制冷顶满」跟样本同一条时间线（合格样本要两个相等）
        Assert.Empty(r.Trips());
    }
}
