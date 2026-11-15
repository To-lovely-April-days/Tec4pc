using System.Reflection;
using Tec.Drivers.Rd105;
using TecControl.Core.Control;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 新外环的影子挂进 HostControlLoop 以后（第 0 步：只算不控），在真回路 + 假 RD105 上锁这几件事：
/// · 回路每拍都把影子算一遍——不在控也算、「起步估偏置」关着（RecordHistory 提前返回）也算，读数挂在周期快照上；
/// · 釜内串级在控时影子跟旧外环同一拍算；
/// · 回路喂给影子的量：目标（在控 = 釜内设定、不在控 = 釜内读数）、输出开没开、TEC 扣没扣着、内环贴没贴下限，热源侧恒按「不在 TEC 侧」；
/// · 影子抛异常：只停这一路影子、只说一次，不算周期故障（那会被当成串口死了去重开串口、连 20 拍就停控），控温照常，另一路影子照算；
/// · <b>控制行为没变</b>：同一台回路的两路、同样的输入、同样的配置，一路影子照算、另一路的影子换成会抛异常的 / 干脆没有 / 一上来就停——
///   两路每一拍算出来的控制量（占空比、夹套设定、内外环 P/I/D……周期快照里除影子读数外的每一个属性）和写到设备上的占空比逐位相同。
///
/// 为什么拿「同一台回路的两路」比，而不是开影子 / 关影子各跑一遍：回路的时间是墙钟（快照时刻 = DateTime.Now），
/// 两遍跑的每拍 dt 不可能一样，占空比序列对不上不说明任何问题。两路共用同一份快照（同一个时刻、同一个 dt），
/// 两个釜模型又只按快照时刻和各自那一路的占空比往前推——只要影子不碰控制，两路就该逐位一样；碰了（哪怕一拍）就分叉。
/// 时间压着跑（回路周期 20 ms、外环周期 80 ms；釜内 22 ℃ 起、目标 24 ℃——离得不远，内外环都不顶限幅，每一拍的控制量都在动）：影子里的 950 s / 170 s 是现场秒，这里它算出来的数没有物理意义，只看它算没算、碰没碰别人。
/// </summary>
public class LiveOuterLoopHookTests
{
    /// <summary>从第 n 步（从 1 数）起每一步都抛。InvalidOperationException：漏进回路的话最糟的一种（当成串口死了）。</summary>
    private sealed class ThrowingShadow(int failFrom) : LiveOuterShadow
    {
        public int Steps;

        protected override LiveOuterOutput StepCore(in LiveOuterInput input, bool first)
        {
            if (++Steps >= failFrom) throw new InvalidOperationException("测试：影子算挂了");
            return base.StepCore(input, first);
        }
    }

    /// <summary>照常算，只是把每一步喂给核心类的输入记下来（回路线程写、测试线程读：加锁）。</summary>
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

    private static object StateOf(HostControlLoop loop, int ch)
        => typeof(HostControlLoop).GetMethod("State", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(loop, new object[] { ch })!;

    /// <summary>把某一路的影子换掉（null = 这一路不算）。回路转起来之前换。</summary>
    private static void ReplaceShadow(HostControlLoop loop, int ch, LiveOuterShadow? shadow)
    {
        var st = StateOf(loop, ch);
        st.GetType().GetField("Live")!.SetValue(st, shadow);
    }

    private static LiveOuterShadow? ShadowOf(HostControlLoop loop, int ch)
    {
        var st = StateOf(loop, ch);
        return (LiveOuterShadow?)st.GetType().GetField("Live")!.GetValue(st);
    }

    /// <summary>
    /// 真回路 + 假 RD105，两路各带一个一模一样的小釜（夹套按占空比走 → 釜壁滞后 → 釜内），都配成釜内串级。
    /// 釜模型只在回路每拍的回调里（回路那条流上）按快照时刻推：同一拍里两路各读各的釜内，读到的数在这一拍里不会变。
    /// </summary>
    private sealed class Rig : IAsyncDisposable
    {
        private const double K = 20;                 // 釜模型的时间压缩
        public readonly FakeRd105Device Dev = new() { Thermal = false };
        public readonly HostControlLoop Loop;
        private readonly object _gate = new();
        private readonly List<(ControlCycleResult Res, long Duty1, long Duty2, bool LoopBeat1)> _cycles = new();
        private readonly List<(int Ch, string Level, string Text)> _notices = new();
        private readonly List<Exception> _faults = new();
        private readonly double[] _tj = [25, 25], _wall = [25, 25], _tr = [22, 22], _trRead = [22, 22];
        private DateTime _last;

        /// <summary>影子那句提示的处理函数要不要抛（核「处理函数坏了也漏不进回路」）。</summary>
        public volatile bool NoticeHandlerThrows;

        /// <summary>回路每拍回调里调（回路那条流上，这一拍已经记下来了）：要卡着某一拍做事的测试用。</summary>
        public volatile Action<ControlCycleResult>? OnCycle;

        public Rig(TimeSpan? quietWindow = null, int outerPeriodMs = 80)
        {
            for (var ch = 1; ch <= 2; ch++)
            {
                Dev.Set(ch, "TCADJTEMP", 25_00000);
                Dev.Set(ch, "LIMITED", 90);
            }
            Dev.Open();
            Loop = new HostControlLoop(new Rd105Link(Dev).Controller)
            {
                Period = TimeSpan.FromMilliseconds(20),
                OuterPeriod = TimeSpan.FromMilliseconds(outerPeriodMs),
                SyncMode = ChannelSyncMode.Independent,
                ManagePwmFrequency = false,
                // 缺省关着：RecordHistory 第一句就返回——影子不能写在它里面，关着也得照算
                OuterStartQuietWindow = quietWindow ?? TimeSpan.Zero,
            };
            for (var ch = 1; ch <= 2; ch++)
            {
                var i = ch - 1;
                Loop.ConfigurePid(ch, 8, 0.5, 0, 90, invertOutput: false);
                Loop.ConfigureCascade(ch, 2.5, 0.2, 0, 8);
                Loop.SetStrategy(ch, ControlStrategy.Cascade);
                Loop.SetOuterSource(ch, () => Volatile.Read(ref _trRead[i]));
            }
            var stateOf1 = StateOf(Loop, 1);
            var lastOuter = stateOf1.GetType().GetField("LastOuterUpdate")!;
            Loop.ChannelNotice += (ch, level, text) =>
            {
                lock (_gate) _notices.Add((ch, level, text));
                // 只对影子那句抛：回路自己原有的提示（开控时那句「积分起点」）本来就没人兜，不是这里要核的
                if (NoticeHandlerThrows && text.Contains("新外环试算")) throw new InvalidOperationException("测试：提示的处理函数坏了");
            };
            Loop.CycleFaulted += ex => { lock (_gate) _faults.Add(ex); };
            Loop.CycleCompleted += res =>
            {
                StepPlants(res);
                // 旧外环这一拍算没算：算了的那一拍它把 LastOuterUpdate 记成这一拍快照的时刻
                var loopBeat = (DateTime)lastOuter.GetValue(stateOf1)! == res.Snapshot.Timestamp;
                lock (_gate) _cycles.Add((res, Dev.Get(1, "PWMDUTY"), Dev.Get(2, "PWMDUTY"), loopBeat));
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
                var duty = infos[i].Active ? infos[i].DutyPercent : 0;
                _tj[i] += (duty / 100.0 * 1.0 - (_tj[i] - 25) * 0.01) * dt;
                _wall[i] += (_tj[i] - _wall[i]) / 20 * dt;
                _tr[i] += (_wall[i] - _tr[i] - 1.5) / 60 * dt;
                Dev.Set(i + 1, "TCADJTEMP", (long)Math.Round(_tj[i] * 1e5));
                Volatile.Write(ref _trRead[i], Math.Round(_tr[i], 2));      // 宇电 0.01 K 分辨率
            }
        }

        public (ControlCycleResult Res, long Duty1, long Duty2, bool LoopBeat1)[] Cycles() { lock (_gate) return _cycles.ToArray(); }
        public (int Ch, string Level, string Text)[] Notices() { lock (_gate) return _notices.ToArray(); }
        public Exception[] Faults() { lock (_gate) return _faults.ToArray(); }
        public int Count { get { lock (_gate) return _cycles.Count; } }

        public async Task UntilCycles(int n, int ms = 30000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(ms);
            while (Count < n)
            {
                Assert.True(DateTime.UtcNow < deadline, $"等太久：才 {Count} 拍（要 {n}）；周期故障 {Faults().Length}");
                await Task.Delay(20);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Loop.ShutdownAsync();
            Loop.Dispose();
        }
    }

    [Fact]
    public async Task 回路每拍都把影子算一遍_不在控也算_起步估偏置关着也算_两拍之间是同一份读数()
    {
        await using var r = new Rig();
        // B 路的釜内读数来源坏了（一调就抛）：这一路影子起不了算，但不许算周期故障
        r.Loop.SetOuterSource(2, () => throw new InvalidOperationException("测试：读釜内的口子坏了"));
        r.Loop.Start();
        await r.UntilCycles(60);
        var c = r.Cycles();
        Assert.Empty(r.Faults());
        Assert.Empty(r.Notices());
        Assert.All(c, x => Assert.False(x.Res.Ch1.Active));            // 一直没在控

        // A 路：第一拍（釜内 22、夹套 25 都有效）就起算，之后每拍都有读数
        Assert.All(c, x => Assert.NotNull(x.Res.Ch1.LiveOuter));
        var first = c[0].Res.Ch1.LiveOuter!;
        Assert.Equal(25.0, first.Wall);                                // Ŵ = 夹套
        Assert.Equal(22.0, first.THat);                                // T̂ = 釜内
        Assert.Equal(3.0, first.BHat);                                 // b̂ = 夹套 − 釜内
        Assert.Equal(950.0, first.Tau);
        Assert.Equal(25.0, first.JacketSetpointC, 9);                  // 不在控：目标 = 釜内读数 → 夹套目标 = 釜内 + b̂ = 此刻的夹套
        Assert.False(first.AlarmOn);

        // 影子满一个外环周期（80 ms）才算一拍：两拍之间回路每拍交出来的是同一份读数
        var beats = new List<DateTime> { c[0].Res.Snapshot.Timestamp };
        for (var i = 1; i < c.Length; i++)
            if (!ReferenceEquals(c[i].Res.Ch1.LiveOuter, c[i - 1].Res.Ch1.LiveOuter)) beats.Add(c[i].Res.Snapshot.Timestamp);
        Assert.True(beats.Count >= 5, $"60 拍里影子只算了 {beats.Count} 拍");
        Assert.True(beats.Count < c.Length / 2, $"影子算得比外环周期还密：{c.Length} 拍里 {beats.Count} 拍");
        for (var i = 1; i < beats.Count; i++)
            Assert.True((beats[i] - beats[i - 1]).TotalSeconds >= 0.08, $"两拍只隔 {(beats[i] - beats[i - 1]).TotalMilliseconds:F1} ms");

        // B 路：釜内读不到 → 没起算，读数一直是空的
        Assert.All(c, x => Assert.Null(x.Res.Ch2.LiveOuter));
        Assert.False(ShadowOf(r.Loop, 2)!.Started);
        Assert.False(ShadowOf(r.Loop, 2)!.Stopped);
    }

    [Fact]
    public async Task 釜内串级在控时_影子跟旧外环同一拍算()
    {
        // 外环周期放到 400 ms（十来个回路周期）：开控那几条串口指令的工夫远不到一个外环周期
        await using var r = new Rig(outerPeriodMs: 400);
        // 卡着拍开控，让两边的节拍一定错开：影子刚算过一拍的那一拍（回调里、回路那条流上）开控——
        // 下一拍旧外环就算它的第一拍，这时离影子上一拍还不到一个外环周期，影子到不了点。
        // 影子要是不等旧外环、只按自己的节拍走，它每一拍都比旧外环早到一两个回路周期，两边一直差着拍；等它，旧外环的第二拍起就同拍。
        LiveOuterReadout? prev = null;
        int n = 0, startedAt = -1;
        Exception? startFailed = null;
        r.OnCycle = res =>
        {
            var beat = !ReferenceEquals(res.Ch1.LiveOuter, prev);
            prev = res.Ch1.LiveOuter;
            if (n++ >= 12 && beat && startedAt < 0)
            {
                startedAt = n - 1;
                try { r.Loop.StartChannelAsync(1, 24).GetAwaiter().GetResult(); }
                catch (Exception ex) { startFailed = ex; }
            }
        };
        r.Loop.Start();
        await r.UntilCycles(150);
        var c = r.Cycles();
        Assert.Empty(r.Faults());
        Assert.Null(startFailed);
        var s0 = Volatile.Read(ref startedAt);
        Assert.InRange(s0, 12, 60);

        static bool ShadowBeat((ControlCycleResult Res, long Duty1, long Duty2, bool LoopBeat1)[] c, int i)
            => !ReferenceEquals(c[i].Res.Ch1.LiveOuter, c[i - 1].Res.Ch1.LiveOuter);

        // 前提摆对了：开控前那一拍影子刚算过；开控后第一拍旧外环算了、影子没到点（错开了）
        Assert.True(ShadowBeat(c, s0) && !c[s0].Res.Ch1.Active);
        Assert.True(c[s0 + 1].Res.Ch1.Active && c[s0 + 1].LoopBeat1, "开控后第一拍旧外环没算");
        Assert.False(ShadowBeat(c, s0 + 1));

        // 之后：旧外环算了的拍和影子算了的拍是同一批拍（旧外环第二拍起；影子为了等它，那一拍离上一拍比一个外环周期长）
        var after = Enumerable.Range(s0 + 2, c.Length - s0 - 2).ToArray();
        var loopBeats = after.Where(i => c[i].LoopBeat1).ToArray();
        var shadowBeats = after.Where(i => ShadowBeat(c, i)).ToArray();
        Assert.True(loopBeats.Length >= 5, $"旧外环只算了 {loopBeats.Length} 拍");
        Assert.Equal(loopBeats, shadowBeats);
        // 影子等的那一下：它这一拍离上一拍比一个外环周期长，之后每拍都跟旧外环一样长
        Assert.True((c[loopBeats[0]].Res.Snapshot.Timestamp - c[s0].Res.Snapshot.Timestamp).TotalSeconds > 0.4);
    }

    [Fact]
    public async Task 回路喂给影子的量_不在控是釜内读数_在控是釜内设定_输出开没开_TEC扣没扣着_贴没贴下限_热源侧恒按不在TEC侧()
    {
        await using var r = new Rig();
        var spy = new RecordingShadow();
        ReplaceShadow(r.Loop, 1, spy);
        r.Loop.Start();
        await r.UntilCycles(15);                                       // ① 不在控
        var idle = spy.Steps();

        // ② 双向挡表、形态只加热（会话在等人答冷却水 / 等冷水机间隔时就是这么摆的）= TEC 被扣着；釜内 22 → 目标 24，要加热
        r.Loop.SetActuatorMode(1, ActuatorMode.HeatOnly, GainTable.Both);
        await r.Loop.StartChannelAsync(1, 24);
        await r.UntilCycles(r.Count + 40);
        // ③ 目标改到 15：要降温，只加热形态下内环贴着下限 0
        r.Loop.SetSetpoint(1, 15);
        await r.UntilCycles(r.Count + 50);
        var withheld = spy.Steps().Length;
        // ④ TEC 放开（同一张表里换形态）
        r.Loop.SetActuatorMode(1, ActuatorMode.HeaterTec, GainTable.Both);
        await r.UntilCycles(r.Count + 30);
        var released = spy.Steps().Length;
        // ⑤ 切继电器那一两秒：输出挂起（还在控）
        r.Loop.SuspendOutput(1, true);
        await r.UntilCycles(r.Count + 20);
        await r.Loop.ShutdownAsync();
        var all = spy.Steps();
        Assert.Empty(r.Faults());
        Assert.False(spy.Stopped);

        // 「热源继电器在 TEC 侧」回路不知道：第 0 步恒按不成立喂（宁可不报）；终点和坡速不给
        Assert.All(all, i => { Assert.False(i.TecSide); Assert.Null(i.SpFinal); Assert.Null(i.SpRate); Assert.True(double.IsFinite(i.Tj)); });

        // ① 不在控：目标 = 当时的釜内读数；输出没开；不算贴下限、不算扣着
        Assert.True(idle.Length >= 3);
        Assert.All(idle, i => Assert.True(i.Sp == i.Tr && i is { OutputOn: false, InnerSatLow: false, TecWithheld: false }, i.ToString()));

        // ② 在控（目标 24）：目标 = 回路的釜内设定（不是釜内读数）；输出开着；TEC 扣着；加热中不贴下限
        var heat = all.Where(i => i.Sp == 24).ToArray();
        Assert.True(heat.Length >= 5, $"目标 24 的只有 {heat.Length} 拍");
        Assert.All(heat, i => Assert.True(i is { OutputOn: true, TecWithheld: true } && i.Tr != 24, i.ToString()));
        Assert.Contains(heat, i => !i.InnerSatLow);

        // ③④⑤ 在控（目标 15）
        var cool = all.Select((i, k) => (I: i, K: k)).Where(x => x.I.Sp == 15).ToArray();
        Assert.True(cool.Length >= 10, $"目标 15 的只有 {cool.Length} 拍");
        Assert.Contains(cool, x => x.K < withheld && x.I is { InnerSatLow: true, TecWithheld: true, OutputOn: true });   // ③ 扣着、贴 0
        Assert.All(cool.Where(x => x.K < withheld), x => Assert.True(x.I.TecWithheld));
        Assert.All(cool.Where(x => x.K > withheld + 1), x => Assert.False(x.I.TecWithheld));                              // ④ 放开了
        Assert.Contains(cool, x => x.K < released && x.I.OutputOn);
        Assert.False(cool[^1].I.OutputOn);                                                                                 // ⑤ 挂起：输出关着，目标还是回路的设定
        Assert.Equal(all.Length, withheld + cool.Count(x => x.K >= withheld));                                             // 目标 15 一直喂到最后
    }

    [Fact]
    public async Task 影子抛异常_只停这一路影子只说一次_不算周期故障_控温照常_另一路影子照算()
    {
        await using var r = new Rig();
        var bad = new ThrowingShadow(failFrom: 4);
        ReplaceShadow(r.Loop, 1, bad);
        r.NoticeHandlerThrows = true;                                  // 提示的处理函数也坏：照样漏不进回路
        await r.Loop.StartChannelAsync(1, 24);
        r.Loop.Start();
        await r.UntilCycles(160);
        var c = r.Cycles();

        Assert.Empty(r.Faults());                                      // 一拍周期故障都没有（有的话会话会去重开串口）
        Assert.True(bad.Stopped);
        Assert.Equal(4, bad.Steps);                                    // 抛过一次就再没调过核心类
        Assert.Contains("InvalidOperationException", bad.StopReason);
        var said = Assert.Single(r.Notices(), n => n.Text.Contains("新外环试算"));
        Assert.Equal(1, said.Ch);
        Assert.Equal("warn", said.Level);
        Assert.Contains("影子算挂了", said.Text);
        Assert.Contains("控温不受影响", said.Text);
        // 字眼别撞上孪生测试 / 验收仿真按字数次数的那几个
        Assert.DoesNotContain("夹套压低", said.Text);
        Assert.DoesNotContain("那一枪", said.Text);
        Assert.DoesNotContain("外环积分冻结", said.Text);

        // 这一路：影子停之前有读数（3 拍），停了以后没有；控温从头到尾在跑
        var lastWith = Array.FindLastIndex(c, x => x.Res.Ch1.LiveOuter is not null);
        Assert.True(lastWith >= 0 && lastWith < c.Length - 100, $"影子读数一直到第 {lastWith} 拍（共 {c.Length} 拍）");
        Assert.All(c.Skip(lastWith + 1), x => Assert.Null(x.Res.Ch1.LiveOuter));
        Assert.All(c, x => Assert.True(x.Res.Ch1.Active));
        var after = c.Skip(lastWith + 1).ToArray();
        Assert.True(after.Select(x => x.Duty1).Distinct().Count() > 20, "影子停了以后占空比不动了");
        Assert.True(after.Count(x => x.LoopBeat1) > 20, "影子停了以后旧外环不算了");
        // 釜内（22 ℃ 起、目标 24 ℃）在影子停了以后接着往目标走
        var (tr0, tr1) = (after[0].Res.Ch1.OuterMeasuredC, after[^1].Res.Ch1.OuterMeasuredC);
        Assert.True(tr1 > tr0 + 0.3 && Math.Abs(24 - tr1) < Math.Abs(24 - tr0),
            $"影子停了以后釜内没接着往目标走：{tr0:F2} → {tr1:F2}（夹套 {after[0].Res.Ch1.MeasuredC:F2} → {after[^1].Res.Ch1.MeasuredC:F2}）");

        // 另一路（没在控）的影子不受牵连：一直有读数、一直在换
        Assert.All(c, x => Assert.NotNull(x.Res.Ch2.LiveOuter));
        Assert.True(after.Select(x => x.Res.Ch2.LiveOuter).Distinct(ReferenceEqualityComparer.Instance).Count() > 20);
        Assert.False(ShadowOf(r.Loop, 2)!.Stopped);
    }

    public enum Other
    {
        /// <summary>算了几拍以后抛异常。</summary>
        Throws,
        /// <summary>第一步就抛异常。</summary>
        ThrowsAtOnce,
        /// <summary>这一路干脆没有影子。</summary>
        None,
        /// <summary>一上来就是停着的。</summary>
        Stopped,
    }

    [Theory]
    [InlineData(Other.Throws)]
    [InlineData(Other.ThrowsAtOnce)]
    [InlineData(Other.None)]
    [InlineData(Other.Stopped)]
    public async Task 两路同样的输入_一路影子照算另一路影子换掉_两路的控制量和写出去的占空比逐拍逐位相同(Other other)
    {
        await using var r = new Rig();
        LiveOuterShadow? swapped = other switch
        {
            Other.Throws => new ThrowingShadow(failFrom: 6),
            Other.ThrowsAtOnce => new ThrowingShadow(failFrom: 1),
            Other.None => null,
            _ => Stopped(),
        };
        ReplaceShadow(r.Loop, 2, swapped);
        // 两路都在回路转起来之前开控：第一拍起就都在控，起步条件一模一样
        await r.Loop.StartChannelAsync(1, 24);
        await r.Loop.StartChannelAsync(2, 24);
        r.Loop.Start();
        await r.UntilCycles(250);
        await r.Loop.ShutdownAsync();
        var c = r.Cycles();
        Assert.Empty(r.Faults());

        // 这一场不是摆设：两路都一直在控、外环在算、占空比和夹套设定都在动；A 路影子真在算、B 路影子确实是换掉的那个样子
        Assert.All(c, x => Assert.True(x.Res.Ch1.Active && x.Res.Ch2.Active && x.Res.Ch1.Cascade && x.Res.Ch2.Cascade));
        Assert.True(c.Select(x => x.Res.Ch1.DutyPercent).Distinct().Count() > 100, "占空比几乎没动，比不出东西");
        Assert.True(c.Select(x => x.Res.Ch1.InnerSetpointC).Distinct().Count() > 30, "夹套设定几乎没动，比不出东西");
        Assert.True(c.Count(x => x.LoopBeat1) > 40, "旧外环没怎么算");
        Assert.All(c, x => Assert.NotNull(x.Res.Ch1.LiveOuter));
        Assert.True(c.Select(x => x.Res.Ch1.LiveOuter).Distinct(ReferenceEqualityComparer.Instance).Count() > 40, "A 路影子没怎么算");
        var a = ShadowOf(r.Loop, 1)!;
        Assert.True(a.Started && !a.Stopped);
        switch (other)
        {
            case Other.Throws:
                Assert.True(swapped!.Stopped);
                Assert.Contains(c, x => x.Res.Ch2.LiveOuter is not null);          // 抛之前算过几拍
                Assert.All(c.Skip(c.Length / 2), x => Assert.Null(x.Res.Ch2.LiveOuter));
                Assert.Single(r.Notices(), n => n.Ch == 2 && n.Text.Contains("新外环试算"));
                break;
            case Other.ThrowsAtOnce:
            case Other.Stopped:
                Assert.True(swapped!.Stopped);
                Assert.All(c, x => Assert.Null(x.Res.Ch2.LiveOuter));
                Assert.Single(r.Notices(), n => n.Ch == 2 && n.Text.Contains("新外环试算"));
                break;
            default:
                Assert.All(c, x => Assert.Null(x.Res.Ch2.LiveOuter));
                Assert.DoesNotContain(r.Notices(), n => n.Text.Contains("新外环试算"));
                break;
        }
        Assert.DoesNotContain(r.Notices(), n => n.Ch == 1 && n.Text.Contains("新外环试算"));

        // 正题：每一拍，两路的周期快照除了影子读数之外逐个属性相同（record 的相等：double 按位比，NaN 等于 NaN），写到设备上的占空比也相同
        for (var i = 0; i < c.Length; i++)
        {
            var x = c[i];
            Assert.True(x.Res.Ch1 with { LiveOuter = null } == x.Res.Ch2 with { LiveOuter = null },
                $"第 {i} 拍两路的控制量分叉了：\nA {x.Res.Ch1 with { LiveOuter = null }}\nB {x.Res.Ch2 with { LiveOuter = null }}");
            Assert.True(x.Duty1 == x.Duty2, $"第 {i} 拍写到设备上的占空比不一样：A {x.Duty1} B {x.Duty2}");
        }

        static LiveOuterShadow Stopped()
        {
            var s = new LiveOuterShadow();
            s.Stop("测试：一上来就停着");
            return s;
        }
    }

    [Fact]
    public async Task 这个比法比得出分叉_两路目标差一点点_控制量就不一样()
    {
        // 上一条的反面：证明「两路逐拍比」不是怎么都相等。B 路目标只多 0.01 ℃，占空比就对不上了。
        await using var r = new Rig();
        await r.Loop.StartChannelAsync(1, 24);
        await r.Loop.StartChannelAsync(2, 24.01);
        r.Loop.Start();
        await r.UntilCycles(40);
        var c = r.Cycles();
        Assert.Contains(c, x => x.Duty1 != x.Duty2);
        Assert.Contains(c, x => x.Res.Ch1 with { LiveOuter = null } != x.Res.Ch2 with { LiveOuter = null });
    }
}
