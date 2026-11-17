using System.Reflection;
using TecControl.Core.Control;
using Tec.Driver.Abi;
using Tec.Drivers.DualStation;
using Tec.Drivers.Rd105;
using Tec.Drivers.Rd105.Modbus;
using Xunit;
using Xunit.Abstractions;

namespace Tec.Core.Tests;

/// <summary>
/// 【本地改动】（2026-11-16，外环改「按实测实时算」第 1 步）双工位主机的接线：面板选「新」（IPidTuningBench.SetOuterAlgorithm，存 pid.json、推回路请求）
/// → 下发釜内目标时回路锁成新（双向挡、上位机串级、影子起算了）→ 会话按回路锁定的判这一路的规则（DuoSession.LiveRules ← Rd105TemperatureControl.LiveOuterInControl）。
/// 上位机串级、真时钟、假机带热模型（照 DuoSignHoldCascadeTests / DuoHeatBoostWaterTests），每条新 / 旧各跑一遍对照：
/// · 「TEC 此刻真接着」每拍跟着继电器推给回路（两种模式都推，主会话 D7）；在控改目标不重锁，控中改选择这一场不变（D4）；
/// · 釜内冲过目标且在升：旧模式提前切 TEC、夹套目标压低；新模式不切、CascadePushC 一直 null（用户 2026-11-16 点头的范围）；
/// · 升温加速开着：旧模式备水进加速；新模式不进、不备水（主会话 C5）。
/// 语义细节（A1–A6 逐条、问水、最短制冷）在 <see cref="LiveOuterSessionRulesTests"/>（确定性台子）。
/// </summary>
public sealed class LiveOuterDuoWiringTests(ITestOutputHelper output)
{
    private static async Task WaitUntil(Func<bool> cond, int ms, Func<string> what)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond())
        {
            Assert.True(DateTime.UtcNow < deadline, "等太久：" + what());
            await Task.Delay(25);
        }
    }

    /// <summary>双向挡表：−40 ~ 230 都整过（同 BothTableSeed），20 ℃ 那行带外环参数和学到的稳态偏置 +1.6（没有就不加速；同 DuoHeatBoostWaterTests）。</summary>
    private static void SeedWithBias(string instanceId)
    {
        foreach (var tc in new[] { 1, 2 })
        {
            var sch = new PidGainSchedule { SegmentBoundaryC = double.NaN };
            for (var t = -40; t <= 230; t += 30)
                sch.Learn(t == 20
                    ? new GainPoint(t, Rd105HostControl.FallbackInner, 1, 100, new PidGains(2.5, 0.01, 0), 8, SteadyBiasC: 1.6)
                    : new GainPoint(t, Rd105HostControl.FallbackInner, UltimateGainKu: 1, UltimatePeriodTuSeconds: 100));
            Rd105HostControl.Save(sch, Rd105HostControl.BothGainsPath(instanceId, tc));
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        public readonly FakeRd105Device Rd = new() { Thermal = true, HeatSign = -1, GainCPerSec = 1.0, AmbientC = 18 };
        public readonly FakeModbusSlave Io = new();
        private readonly List<string> _logs = new();
        private readonly CancellationTokenSource _feedCts = new();
        private double _tr;
        public IDeviceSession S = null!;
        public DuoSession D = null!;
        public ITemperatureControl T = null!;

        /// <summary>喂给釜内 Tr 的值（每 100 ms 喂一次，新鲜窗 10 s）。</summary>
        public double Tr { get => Volatile.Read(ref _tr); set => Volatile.Write(ref _tr, value); }

        public string[] Logs() { lock (_logs) return _logs.ToArray(); }
        public IPidTuningBench Bench => S.CapabilitiesOf(0).OfType<IPidTuningBench>().Single();
        public Rd105TemperatureControl Inner => D.InnerTemp(0);
        public ITemperatureStatus Status => (ITemperatureStatus)T;
        public string Regime => ((IHeatSource)T).Regime ?? "";

        public HostControlLoop Loop
            => (HostControlLoop)typeof(Rd105TemperatureControl).GetField("_loop", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Inner)!;

        /// <summary>回路这一路此刻拿到的「TEC 此刻真接着」（ChannelState.TecEngaged，反射读，同 LiveOuterSessionTests 拿 ChannelState 的写法）。</summary>
        public bool TecEngaged(int ch)
        {
            var st = typeof(HostControlLoop).GetMethod("State", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Loop, new object[] { ch })!;
            return (bool)st.GetType().GetField("TecEngaged")!.GetValue(st)!;
        }

        /// <summary>增益表 / 逐拍记录目录的替换是 AsyncLocal：在这个同步方法里设好（留在测试方法的上下文里），再进 async 的那一半（同 DuoHeatBoostWaterTests）。</summary>
        public static Task<Rig> OpenAsync(string id, double tr, double tj, string boost = "关", bool seedBias = false)
        {
            Rd105HostControl.UseGainsDir(Path.Combine(Path.GetTempPath(), "tec-lo-wire-" + Guid.NewGuid().ToString("N")));   // 测试流程里一直有效，不收回
            if (seedBias) SeedWithBias(id);
            else BothTableSeed.Seed(id);
            return OpenCoreAsync(id, tr, tj, boost);
        }

        private static async Task<Rig> OpenCoreAsync(string id, double tr, double tj, string boost)
        {
            var r = new Rig { Tr = tr };
            foreach (var ch in new[] { 1, 2 })
            {
                r.Rd.Set(ch, "LIMITED", 90);
                r.Rd.Set(ch, "TCADJTEMP", (long)Math.Round(tj * 1e5));
            }
            var drv = new DualStationDriver
            {
                LinksFactory = _ => new DuoLinks { Rd105 = new Rd105Link(r.Rd), IoPort = r.Io, Io = new Io8rClient(new ModbusRtuClient(r.Io, 1, 200)) }
            };
            var cfg = ParameterSet.Of((DualStationDriver.Fields.Chiller, "已开"));
            cfg[DualStationDriver.Fields.WaterLead] = 0d;
            cfg[DualStationDriver.Fields.HeatBoost] = boost;
            var ctx = new DriverContext
            {
                InstanceId = id, ChannelNumbers = new[] { 1, 2 }, Config = cfg, Simulated = false, TimeScale = 1,
                Clock = () => DateTimeOffset.Now,
                Log = (l, text) => { lock (r._logs) r._logs.Add($"{DateTime.Now:HH:mm:ss.f} {l} {text}"); }
            };
            r.S = await drv.OpenAsync(ParameterSet.Of((Rd105TecDriver.FieldPeriod, 200d), (DualStationDriver.Fields.Tick, 200d)), ctx, CancellationToken.None);
            r.D = (DuoSession)r.S;
            await r.S.StartAsync(CancellationToken.None);
            r.T = r.S.CapabilitiesOf(0).OfType<ITemperatureControl>().Single();
            await WaitUntil(() => !double.IsNaN(r.T.CurrentJacket), 3000, () => "夹套读数");
            var ext = (IExternalReactorTemp)r.S;
            var token = r._feedCts.Token;
            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    ext.FeedReactor(1, r.Tr, Quality.Good);
                    try { await Task.Delay(100, token); } catch (OperationCanceledException) { break; }
                }
            });
            await Task.Delay(300);
            return r;
        }

        /// <summary>新外环（影子）起算了：开控锁成新的条件之一（主会话 D4）。旧模式也等，两遍跑法一样。</summary>
        public Task WaitShadowAsync()
            => WaitUntil(() => Inner.LiveOuterStatus is { ShadowStarted: true }, 10000, () => "新外环起算\n" + string.Join("\n", Logs().TakeLast(10)));

        public async ValueTask DisposeAsync()
        {
            _feedCts.Cancel();
            await S.DisposeAsync();
        }
    }

    private void Dump(Rig r)
    {
        foreach (var l in r.Logs()) output.WriteLine(l);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 接线_选新开控_会话按回路锁定的判_TEC真接着跟着继电器推给回路_控中改选择这一场不变(bool live)
    {
        await using var r = await Rig.OpenAsync("LOW1" + (live ? "N" : "O"), tr: 20, tj: 20);
        Assert.True(r.Bench.OuterAlgorithmSupported);
        r.Bench.SetOuterAlgorithm(live);
        Assert.Equal(live, r.Inner.OuterAlgorithmRequested);
        await r.WaitShadowAsync();

        // 目标比釜内低超过死区（2 K）：按方向起步合 TEC
        await r.T.SetTargetAsync(new TempTarget(15, TempChannelKind.Reactor), CancellationToken.None);
        Assert.Equal(live, r.Inner.LiveOuterInControl);
        Assert.Equal(live, r.Bench.OuterAlgorithmLiveInUse);
        Assert.True(r.Io.Coils[7] && !r.Io.Coils[0]);
        await WaitUntil(() => r.TecEngaged(1), 3000, () => "继电器在 TEC 侧，回路该拿到 TEC 真接着\n" + string.Join("\n", r.Logs().TakeLast(10)));
        Assert.False(r.TecEngaged(2));                                  // B 路没在控、继电器全断
        var note = r.Bench.OuterAlgorithmNote!;
        if (live) Assert.StartsWith("外环：新（按实测实时算） · 维持温差 ", note);
        else Assert.Equal("外环：旧", note);

        // 改目标到釜内之上：起步合加热棒——TEC 真接着撤了；在控改目标不重锁
        await r.T.SetTargetAsync(new TempTarget(25, TempChannelKind.Reactor), CancellationToken.None);
        Assert.True(r.Io.Coils[0] && !r.Io.Coils[7]);
        await WaitUntil(() => !r.TecEngaged(1), 3000, () => "回加热棒侧，TEC 真接着该撤\n" + string.Join("\n", r.Logs().TakeLast(10)));
        Assert.Equal(live, r.Inner.LiveOuterInControl);

        // 控中改选择：只存请求，这一场照开控时锁的走完，面板那一行说停控后才用上
        r.Bench.SetOuterAlgorithm(!live);
        await Task.Delay(600);
        Assert.Equal(live, r.Inner.LiveOuterInControl);
        Assert.Equal(live, r.Bench.OuterAlgorithmLiveInUse);
        Assert.Equal(!live, r.Bench.OuterAlgorithmLive);
        Assert.Contains("停控后重新开控", r.Bench.OuterAlgorithmNote);
        Assert.Contains(r.Logs(), l => l.Contains("这一路正在控温：这一场照开控时定的走完，停控后重新开控才用上"));

        var logs = r.Logs();
        Dump(r);
        if (live)
        {
            Assert.Single(logs, l => l.Contains("工位 A 外环归新算法（按实测实时算）"));   // 会话那句：每场一次
            Assert.Single(logs, l => l.Contains("通道1外环：新算法（按实测实时算）"));      // 回路开控那句
        }
        else
        {
            Assert.DoesNotContain(logs, l => l.Contains("外环归新算法"));
            Assert.DoesNotContain(logs, l => l.Contains("通道1外环：新算法"));
        }
        foreach (var w in new[] { "夹套压低", "外环积分冻结在", "外环偏置自校正", "那一枪" })
            Assert.DoesNotContain(logs, l => (l.Contains("外环归新算法") || l.Contains("外环算法")) && l.Contains(w));

        // 停控：锁定清掉
        await r.T.StopAsync(CancellationToken.None);
        Assert.False(r.Inner.LiveOuterInControl);
        Assert.Null(r.Bench.OuterAlgorithmLiveInUse);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 接线_釜内冲过目标且在升_旧模式提前切TEC压低夹套目标_新模式不切不压(bool live)
    {
        await using var r = await Rig.OpenAsync("LOW2" + (live ? "N" : "O"), tr: 20, tj: 20);
        // 提前判据的斜率窗缩到秒级（真时钟跑不起 30 s 窗；两种模式一样缩）
        r.D.EnterSlopeMinSpan = TimeSpan.FromSeconds(2);
        r.D.SlopeMinSpan = TimeSpan.FromSeconds(1);
        r.D.SlopeWindowSpan = TimeSpan.FromSeconds(3);
        r.Bench.SetOuterAlgorithm(live);
        await r.WaitShadowAsync();
        await r.T.SetTargetAsync(new TempTarget(20, TempChannelKind.Reactor), CancellationToken.None);
        Assert.Equal(live, r.Inner.LiveOuterInControl);
        Assert.True(r.Io.Coils[0]);

        // 釜内往上爬：每 100 ms +0.01 K，6 s 到 20.6（高过目标 + 0.1 且在升，2 s 窗斜率 0.1 K/s）
        var sawPush = false;
        for (var i = 1; i <= 60; i++)
        {
            r.Tr = 20 + 0.01 * i;
            await Task.Delay(100);
            sawPush |= r.Status.CascadePushC is not null;
        }
        if (!live)
        {
            await WaitUntil(() => r.Logs().Any(l => l.Contains("提前切 TEC，不等功率翻号")), 5000,
                () => "对照：旧模式该提前切 TEC\n" + string.Join("\n", r.Logs().TakeLast(10)));
            Assert.Contains(r.Logs(), l => l.Contains("夹套目标压到釜内以下 2.0 K"));
            Assert.True(sawPush || r.Status.CascadePushC is not null, "对照：旧模式该把夹套目标压低");
            Dump(r);
            return;
        }
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(100);
            sawPush |= r.Status.CascadePushC is not null;
        }
        var logs = r.Logs();
        Dump(r);
        Assert.False(sawPush, "新模式不该压低夹套目标");
        Assert.Null(r.Loop.GetOuterPushDown(1));
        Assert.DoesNotContain(logs, l => l.Contains("提前切 TEC，不等功率翻号"));   // 会话那句提示里提到「提前切 TEC」停用，按判据原话找
        Assert.DoesNotContain(logs, l => l.Contains("夹套目标压到"));
        Assert.DoesNotContain(logs, l => l.Contains("夹套压低"));
        Assert.DoesNotContain("提前切 TEC", r.Regime);
        Assert.True(r.Inner.LiveOuterInControl);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 接线_升温加速开着_旧模式备水进加速_新模式不进不备水(bool live)
    {
        await using var r = await Rig.OpenAsync("LOW3" + (live ? "N" : "O"), tr: 15, tj: 16, boost: "正常", seedBias: true);
        r.Bench.SetOuterAlgorithm(live);
        await r.WaitShadowAsync();
        await r.T.SetTargetAsync(new TempTarget(20, TempChannelKind.Reactor), CancellationToken.None);
        Assert.Equal(live, r.Inner.LiveOuterInControl);
        if (!live)
        {
            // 对照（同 DuoHeatBoostWaterTests「不用问_一开始就开冷却水」）：旧外环在等 TEC → 会话先开水 → 放行进加速
            await WaitUntil(() => r.Io.Coils[4], 5000, () => "对照：旧模式该先开冷却水\n" + string.Join("\n", r.Logs().TakeLast(10)));
            await WaitUntil(() => r.Logs().Any(l => l.Contains("升温加速：釜内")), 5000,
                () => "对照：旧模式该进加速\n" + string.Join("\n", r.Logs().TakeLast(10)));
            Dump(r);
            return;
        }
        var wanted = false;
        var waterForBoost = false;
        for (var i = 0; i < 50; i++)
        {
            await Task.Delay(100);
            wanted |= r.Inner.HeatBoosting || r.Inner.HeatBoostWaitingForTec;
            waterForBoost |= r.Io.Coils[4] && !r.Io.Coils[7];              // 继电器不在 TEC 侧却开着水 = 给升温加速备的
        }
        var logs = r.Logs();
        Dump(r);
        Assert.False(wanted, "新模式不该升温加速");
        Assert.False(waterForBoost, "新模式不该为升温加速备水");
        Assert.DoesNotContain(logs, l => l.Contains("升温加速"));
        Assert.Equal("双向挡（加热棒 ↔ TEC 跟功率符号走）", r.Regime);    // 没有「·升温加速」后缀
        Assert.True(r.Inner.LiveOuterInControl);
    }
}
