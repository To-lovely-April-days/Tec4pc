using Tec.Core;
using Tec.Core.Benches;
using Tec.Core.Data;
using Tec.Core.Execution;
using Tec.Core.Records;
using Tec.Driver.Abi;
using Tec.DriverHost;
using Tec.Hmi.Runtime;
using Tec.Hmi.Ui;
using Tec.Hmi.Ui.ViewModels;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 【本地改动】（2026-11-16，外环改「按实测实时算」第 1 步，主会话 D2 甲 / D10 / D11 / D15）HMI 温控卡的「外环 旧 | 新」两段钮、它下面那一行、
/// 放热报警红条 / Toast / 面板日志。没有 Avalonia 无头测试：
/// · 出不出、说什么拆在 <see cref="HmiOuterAlgo"/> / <see cref="HmiAlarmEdge"/>（纯函数 / 纯状态），照 WaterAskPrompterTests 直接测各种状态；
/// · 再拿真的 <see cref="HmiViewModel"/>（工作站 HMI 窗和设备端 HMI 是同一份）接一个假宿主（引擎 / 管线 / 日志借设备端精简工作台的，通道是测试摆的）
///   + 一个假能力（温控 + 回路状态 + 整定台），核接线：钮跟着模式 / 方式 / 支持出没、点了交给驱动、报警红条、上升沿只 Toast 一次、
///   切热源那一两秒读不到不重报、头卡带一句、Tr 丢失保持在新 / 旧外环下各说各的（旧外环原句一字不变）。
/// </summary>
public sealed class HmiOuterAlgoTests
{
    // ───────────────────────────── 纯函数 ─────────────────────────────

    [Theory]
    [InlineData("Tr", true, true, true)]
    [InlineData("Tj", true, true, false)]          // 夹套控温没有外环
    [InlineData("TrTj", true, true, false)]        // 蒸回流（夹套跟随）也没有
    [InlineData("Tr", false, true, false)]         // 温控器 PID 方式
    [InlineData("Tr", true, false, false)]         // 单机 RD105 / 老驱动：不支持
    public void 两段钮只在釜内控温加上位机PID加这一路支持时摆(string mode, bool hostLoop, bool supported, bool show)
        => Assert.Equal(show, HmiOuterAlgo.ShowPick(mode, hostLoop, supported));

    [Fact]
    public void 状态行_钮不摆就没有_驱动给的整句原样摆_驱动没给按已选说()
    {
        Assert.Null(HmiOuterAlgo.Line(false, true, "外环：新（按实测实时算） · 维持温差 5.9 K · 釜反应时间约 17 min"));
        Assert.Equal("外环：新（按实测实时算） · 维持温差 5.9 K · 釜反应时间约 17 min",
            HmiOuterAlgo.Line(true, true, "外环：新（按实测实时算） · 维持温差 5.9 K · 釜反应时间约 17 min"));
        Assert.Equal("外环：旧", HmiOuterAlgo.Line(true, false, null));
        Assert.Equal("外环：已选 新（按实测实时算）", HmiOuterAlgo.Line(true, true, " "));
    }

    [Fact]
    public void 状态行琥珀色_只在在控而已选跟在用不一样时()
    {
        Assert.False(HmiOuterAlgo.LineWarn(true, true, null));     // 没在控：已选什么下次开控就用什么
        Assert.False(HmiOuterAlgo.LineWarn(true, false, null));
        Assert.False(HmiOuterAlgo.LineWarn(true, true, true));
        Assert.False(HmiOuterAlgo.LineWarn(true, false, false));
        Assert.True(HmiOuterAlgo.LineWarn(true, true, false));     // 选了新：控中改的，或这一场锁成了旧
        Assert.True(HmiOuterAlgo.LineWarn(true, false, true));     // 控中选回旧：这一场照新算法走完
        Assert.False(HmiOuterAlgo.LineWarn(false, true, false));   // 钮不摆就不提醒
    }

    [Fact]
    public void 点了以后说清什么时候用上_没在控下次开温控_在控这一场照旧停控后才用上_一样的说本来就是()
    {
        var (log, toast) = HmiOuterAlgo.Picked(true, null);
        Assert.Contains("已存盘", log);
        Assert.Contains("下一次开温控", log);
        Assert.Contains("只在双向挡、控釜内时用得上", log);
        Assert.Contains("下一次开温控起由新算法控", toast);

        (log, toast) = HmiOuterAlgo.Picked(true, false);
        Assert.Contains("这一场照开控时定的走完，停控后重新开控才用上", log);
        Assert.Equal("外环已选 新：这一场照原来的外环走完，停控后重新开控才用上", toast);

        (log, toast) = HmiOuterAlgo.Picked(false, true);
        Assert.Contains("停控后重新开控才用上", log);
        Assert.Equal("外环已选 旧：这一场照新算法走完，停控后重新开控才用上", toast);

        (log, toast) = HmiOuterAlgo.Picked(false, false);
        Assert.Contains("这一场本来就是它", log);
        Assert.Contains("本来就是它", toast);

        (log, toast) = HmiOuterAlgo.Picked(false, null);
        Assert.DoesNotContain("双向挡", log);            // 选旧不用说新算法的适用范围
        Assert.Contains("原来的外环", toast);
    }

    [Fact]
    public void 面板上的字不带符号_也不含验收仿真计数的字眼()
    {
        var texts = new List<string>
        {
            HmiOuterAlgo.AlarmToast(1), HmiOuterAlgo.AlarmClearedLog,
            HmiOuterAlgo.LiveHolding().Log, HmiOuterAlgo.LiveHolding().Toast,
            HmiOuterAlgo.Line(true, true, null)!, HmiOuterAlgo.Line(true, false, null)!,
            // 【本地改动】（2026-11-17，S1 复核查实）读数报警自己的 Toast / 撤了那一句 / 头卡那几句
            HmiOuterAlgo.ReadingAlarmToast(1), HmiOuterAlgo.ReadingAlarmClearedLog,
            HmiOuterAlgo.HeadAlarm(true, false), HmiOuterAlgo.HeadAlarm(false, true), HmiOuterAlgo.HeadAlarm(true, true),
        };
        foreach (var live in new[] { true, false })
            foreach (var inUse in new bool?[] { null, true, false })
            {
                var (l, t) = HmiOuterAlgo.Picked(live, inUse);
                texts.Add(l);
                texts.Add(t);
            }
        foreach (var t in texts)
        {
            foreach (var w in new[] { "夹套压低", "外环积分冻结在", "外环偏置自校正", "那一枪", "b̂", "τ̂", "Ŵ", "T̂" })
                Assert.False(t.Contains(w), $"「{t}」里有「{w}」");
        }
    }

    [Fact]
    public void 放热报警Toast_说放热或冷却没在出力_请人工查TEC电流和冷却水_不说保持满冷()
    {
        // 【本地改动】（第 1 步复核）跑飞「放热」豁免 / 报警不看 TEC 有没有真在取热：断水、TEC 开路时也成立，不能说「保持满冷」
        var t = HmiOuterAlgo.AlarmToast(2);
        Assert.StartsWith("通道 2 放热报警：", t);
        Assert.Contains("控温不停", t);
        Assert.Contains("冷却没在出力", t);
        Assert.Contains("请人工查 TEC 电流和冷却水", t);
        Assert.DoesNotContain("保持满冷", t);
    }

    [Fact]
    public void 读数报警的话_Toast叫人看釜内探头不说冷却水和TEC电流_撤了那句点名是哪一条_头卡分得出哪一种_拆得开驱动拼的整串()
    {
        // 【本地改动】（2026-11-17，S1 复核查实）
        var t = HmiOuterAlgo.ReadingAlarmToast(2);
        Assert.StartsWith("通道 2 釜内读数没跟着夹套降：", t);
        Assert.Contains("控温不停", t);
        Assert.Contains("釜内探头", t);
        Assert.DoesNotContain("冷却水", t);
        Assert.DoesNotContain("TEC 电流", t);
        Assert.DoesNotContain("放热报警", t);
        Assert.Contains("「读数没跟着夹套降」报警撤了", HmiOuterAlgo.ReadingAlarmClearedLog);
        Assert.DoesNotContain("放热报警撤了", HmiOuterAlgo.ReadingAlarmClearedLog);
        Assert.Equal(" · 放热报警（见温控卡）", HmiOuterAlgo.HeadAlarm(true, false));   // 放热那一种：原话
        Assert.Equal(" · 釜内读数报警（见温控卡）", HmiOuterAlgo.HeadAlarm(false, true));
        Assert.Equal(" · 放热报警、釜内读数报警（见温控卡）", HmiOuterAlgo.HeadAlarm(true, true));
        Assert.Equal("", HmiOuterAlgo.HeadAlarm(false, false));

        static (string?, string?) P(string? exo, string? read) => (exo, read);
        Assert.Equal(P(null, null), HmiOuterAlgo.SplitAlarm(null, null));
        Assert.Equal(P(AlarmText, null), HmiOuterAlgo.SplitAlarm(AlarmText, null));                // 只有放热 / 老驱动不给 ReadingAlarm：整串都是放热那一路
        Assert.Equal(P(null, ReadText), HmiOuterAlgo.SplitAlarm(ReadText, ReadText));
        Assert.Equal(P(AlarmText, ReadText), HmiOuterAlgo.SplitAlarm(AlarmText + "；" + ReadText, ReadText));
        Assert.Equal(P("别的驱动拼法", ReadText), HmiOuterAlgo.SplitAlarm("别的驱动拼法", ReadText));    // 不认得的拼法：整串算放热那一路，不丢
        Assert.Equal(P(null, null), HmiOuterAlgo.SplitAlarm(null, ReadText));                     // 整串没有：红条也没有，不凭空报
    }

    [Fact]
    public void 报警沿_上来报一次_一直在不重报_读不到连着3拍才算撤了_撤了再上来再报()
    {
        var e = new HmiAlarmEdge();
        Assert.Equal(HmiAlarmEdge.Edge.None, e.Step(null));
        Assert.Equal(HmiAlarmEdge.Edge.Raised, e.Step("通道1放热报警：……"));
        Assert.True(e.On);
        Assert.Equal(HmiAlarmEdge.Edge.None, e.Step("通道1放热报警：……"));
        // 切热源那一两秒输出关着、驱动不给：不算撤，回来也不重报
        Assert.Equal(HmiAlarmEdge.Edge.None, e.Step(null));
        Assert.Equal(HmiAlarmEdge.Edge.None, e.Step(null));
        Assert.Equal(HmiAlarmEdge.Edge.None, e.Step("通道1放热报警：……"));
        // 真撤了：第 3 拍才算
        Assert.Equal(HmiAlarmEdge.Edge.None, e.Step(null));
        Assert.Equal(HmiAlarmEdge.Edge.None, e.Step(null));
        Assert.Equal(HmiAlarmEdge.Edge.Cleared, e.Step(null));
        Assert.False(e.On);
        Assert.Equal(HmiAlarmEdge.Edge.None, e.Step(null));
        Assert.Equal(HmiAlarmEdge.Edge.Raised, e.Step("通道1放热报警：……"));
    }

    // ───────────────────────────── 接到真的面板视图模型上 ─────────────────────────────

    /// <summary>一路的假能力：温控 + 回路状态 + 整定台（外环开关那几样可摆，其余用不到的抛）。</summary>
    private sealed class FakeLoop : ITemperatureControl, ITemperatureStatus, IPidTuningBench
    {
        public int Channel => 1;

        // ITemperatureControl
        public TempLimits Limits { get; } = new(-40, 180, 5);
        public double CurrentReactor { get; set; } = 50;
        public double CurrentJacket { get; set; } = 55.9;
        /// <summary>【本地改动】（第 1 步复核）不为 null 时下发一直不回来（面板「温控」开关一直转圈），测试放开。</summary>
        public TaskCompletionSource? Hold;
        public Task SetTargetAsync(TempTarget target, CancellationToken ct) => Hold?.Task ?? Task.CompletedTask;
        public Task RampAsync(double target, double ratePerMin, TempChannelKind kind, CancellationToken ct) => Hold?.Task ?? Task.CompletedTask;
        public Task<bool> WaitReachedAsync(double target, double tolerance, TimeSpan timeout, CancellationToken ct) => Task.FromResult(true);
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
        public IObservable<Sample> Temperature => throw new NotSupportedException();

        // ITemperatureStatus
        public double? Setpoint { get; set; }
        public bool Active { get; set; }
        public bool HostLoop { get; set; } = true;
        public string? Holding { get; set; }
        public string? Alarm { get; set; }
        /// <summary>【本地改动】（2026-11-17 复核后）S1「读数没跟着夹套降」那一句（驱动给 Alarm 时把它跟放热那一句用「；」接在一起，这里另给一份）。</summary>
        public string? ReadingAlarm { get; set; }

        /// <summary>照 Rd105TemperatureControl 拼 Alarm：放热那一句在前，两样都在用「；」接起来。</summary>
        public void SetAlarms(string? exo, string? reading)
        {
            ReadingAlarm = reading;
            Alarm = exo is null ? reading : reading is null ? exo : exo + "；" + reading;
        }

        // IPidTuningBench：外环「旧 / 新」
        public bool OuterAlgorithmSupported { get; set; } = true;
        public bool OuterAlgorithmLive { get; set; }
        public bool? OuterAlgorithmLiveInUse { get; set; }
        public string? OuterAlgorithmNote { get; set; }
        public readonly List<bool> Picks = new();
        public string? Refuse;
        public void SetOuterAlgorithm(bool live)
        {
            if (Refuse is { } why) throw new NotSupportedException(why);
            Picks.Add(live);
            OuterAlgorithmLive = live;
        }

        // IPidTuningBench：其余这里用不到
        public string TablePath(PidActuator actuator) => throw new NotSupportedException();
        public IReadOnlyList<PidGainRow> Rows(PidActuator actuator) => throw new NotSupportedException();
        public void ApplyRows(PidActuator actuator, IReadOnlyList<PidGainRow> rows) => throw new NotSupportedException();
        public void Reload() => throw new NotSupportedException();
        public bool Scheduling => true;
        public void SetScheduling(bool on) => throw new NotSupportedException();
        public PidManual Manual => throw new NotSupportedException();
        public void SetManual(PidManual manual) => throw new NotSupportedException();
        public PidActuator CurrentActuator => PidActuator.Both;
        public PidInUse InUse => throw new NotSupportedException();
        public PidInUse Preview(PidActuator actuator, double temperatureC) => throw new NotSupportedException();
        public PidLive? Live => null;
        public PidActuator SuggestActuator(double setpointC) => PidActuator.Both;
        public string? CheckTune(PidAutoTuneRequest request) => "测试";
        public bool Tuning => false;
        public string TuneNote => "";
        public Task StartAutoTuneAsync(PidAutoTuneRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task CancelAutoTuneAsync(CancellationToken ct) => Task.CompletedTask;
        public event Action<PidAutoTuneReport>? AutoTuneFinished { add { } remove { } }
        public event Action? TablesChanged { add { } remove { } }
    }

    /// <summary>假宿主：引擎 / 管线 / 日志 / 归档借设备端精简工作台（开机读台面、不连设备），通道换成测试摆的那一路。</summary>
    private sealed class Host : IHmiHost, IAsyncDisposable
    {
        private readonly HmiRuntime _rt;
        private readonly Channel _ch = new(1, "R1", 0);
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "tec-hmi-outer-" + Guid.NewGuid().ToString("N"));

        public Host(params ICapability[] caps)
        {
            _rt = new HmiRuntime(_dir);
            _rt.Boot();
            _ch.Capabilities.Add(caps);
        }

        public VirtualClock Clock => _rt.Clock;
        public RunEngine Engine => _rt.Engine;
        public DataPipeline Pipeline => _rt.Pipeline;
        public SystemLog Log => _rt.Log;
        public RunArchive Archive => _rt.Archive;
        public DriverCatalog Drivers => _rt.Drivers;
        public Bench Bench => _rt.Bench;
        public string Operator => "测试";
        public string DataDir => _rt.DataDir;
        public Channel? ChannelOf(int number) => number == 1 ? _ch : null;
        public IDeviceSession? Session(string instanceId) => null;
        public string? OpenFailure(string instanceId) => null;
        public bool BeginBatch() => false;
        public async ValueTask DisposeAsync()
        {
            await _rt.DisposeAsync();
            try { Directory.Delete(_dir, recursive: true); } catch { /* 临时目录删不掉不要紧 */ }
        }
    }

    private const string AlarmText = "通道1放热报警：TEC 制冷已顶满 60 s 以上，釜内还在升、比釜壁估计还热——控温不停；是加料 / 放热反应，还是冷却没在出力（TEC 功率线 / 冷却水 / 冷水机）？请人工查 TEC 电流和冷却水";

    [Fact]
    public async Task 面板接线_钮跟着模式方式支持出没_状态行用驱动的话_点了交给驱动说清什么时候用上_同一个不重发()
    {
        var f = new FakeLoop { OuterAlgorithmNote = "外环：旧" };
        await using var host = new Host(f);
        var vm = new HmiViewModel(host, "R1", new[] { 1 });
        var z = vm.Zones[0];

        Assert.True(z.ShowOuterPick);
        Assert.False(z.OuterLive);
        Assert.Equal("外环：旧", z.OuterLine);
        Assert.False(z.OuterLineWarn);
        Assert.Null(z.LoopAlarm);

        // 点「新」：交给驱动一次，面板日志 + Toast 说下一次开温控起用
        z.PickOuter(true);
        Assert.Equal(new[] { true }, f.Picks);
        Assert.True(z.OuterLive);
        Assert.Contains("外环 外环选 新（按实测实时算），已存盘——下一次开温控", z.LastLog);
        Assert.Equal(HmiOuterAlgo.Picked(true, null).Toast, vm.ToastText);
        z.PickOuter(true);                                      // 已经是它：不再交给驱动
        Assert.Equal(new[] { true }, f.Picks);

        // 在控、这一场是旧（控中改的）：状态行用驱动那句、标琥珀
        f.Active = true;
        f.Setpoint = 50;
        f.OuterAlgorithmLiveInUse = false;
        f.OuterAlgorithmNote = "外环：已选 新 / 这一场在用 旧，停控后重新开控才用上";
        vm.Refresh();
        Assert.Equal(f.OuterAlgorithmNote, z.OuterLine);
        Assert.True(z.OuterLineWarn);

        // 换到夹套控温：钮当下就收（不等下一拍）；换回来又摆
        f.Active = false;
        f.Setpoint = null;
        f.OuterAlgorithmLiveInUse = null;
        vm.Refresh();
        z.SwitchMode("Tj");
        Assert.False(z.ShowOuterPick);
        Assert.Null(z.OuterLine);
        z.SwitchMode("Tr");
        Assert.True(z.ShowOuterPick);

        // 温控器 PID 方式 / 驱动不支持：钮不摆；硬点也只 Toast 一句、不交给驱动
        f.HostLoop = false;
        vm.Refresh();
        Assert.False(z.ShowOuterPick);
        f.HostLoop = true;
        f.OuterAlgorithmSupported = false;
        vm.Refresh();
        Assert.False(z.ShowOuterPick);
        z.PickOuter(false);
        Assert.Equal(new[] { true }, f.Picks);
        Assert.Contains("不能选外环算法", vm.ToastText);

        // 驱动拒了（抛）：照原话说，选择不变
        f.OuterAlgorithmSupported = true;
        f.Refuse = "测试：驱动拒了";
        z.PickOuter(false);
        Assert.Equal("测试：驱动拒了", vm.ToastText);
        Assert.Contains("拒绝 测试：驱动拒了", z.LastLog);
        Assert.True(f.OuterAlgorithmLive);
    }

    [Fact]
    public async Task 放热报警_红条照驱动的话摆_上升沿Toast和面板日志各一次_头卡带一句_切热源读不到不重报_撤了记一句()
    {
        var f = new FakeLoop { Active = true, Setpoint = 50, OuterAlgorithmLive = true, OuterAlgorithmLiveInUse = true };
        await using var host = new Host(f);
        var vm = new HmiViewModel(host, "R1", new[] { 1 });
        var z = vm.Zones[0];
        Assert.Null(z.LoopAlarm);
        Assert.DoesNotContain("放热报警", z.HeadNote);

        f.Alarm = AlarmText;
        vm.Refresh();
        Assert.Equal(AlarmText, z.LoopAlarm);
        Assert.Equal(HmiOuterAlgo.AlarmToast(1), vm.ToastText);
        Assert.Contains("回路 " + AlarmText, z.LastLog);
        Assert.EndsWith(" · 放热报警（见温控卡）", z.HeadNote);

        // 一直在：不重报
        vm.Toast("哨兵");
        vm.Refresh();
        vm.Refresh();
        Assert.Equal("哨兵", vm.ToastText);
        // 切热源那一两秒输出关着读不到：红条收、回来不重报、不记「撤了」
        f.Alarm = null;
        vm.Refresh();
        Assert.Null(z.LoopAlarm);
        Assert.DoesNotContain("放热报警", z.HeadNote);
        vm.Refresh();
        f.Alarm = AlarmText;
        vm.Refresh();
        Assert.Equal(AlarmText, z.LoopAlarm);
        Assert.Equal("哨兵", vm.ToastText);
        Assert.DoesNotContain(HmiOuterAlgo.AlarmClearedLog, z.LastLog);

        // 真撤了：连着 3 拍读不到记一句
        f.Alarm = null;
        vm.Refresh();
        vm.Refresh();
        Assert.DoesNotContain(HmiOuterAlgo.AlarmClearedLog, z.LastLog);
        vm.Refresh();
        Assert.Contains(HmiOuterAlgo.AlarmClearedLog, z.LastLog);
        Assert.Equal("哨兵", vm.ToastText);                     // 撤了不 Toast

        // 再上来：再报
        f.Alarm = AlarmText;
        vm.Refresh();
        Assert.Equal(HmiOuterAlgo.AlarmToast(1), vm.ToastText);
    }

    // ───────────── 【本地改动】（2026-11-17 复核查实）S1「读数没跟着夹套降」报警在面板上不当成放热报警 ─────────────
    // 原来：ITemperatureStatus.Alarm 一条路过来，面板一律当放热报警——Toast 叫人查 TEC 电流和冷却水、头卡写「放热报警」、撤了记「放热报警撤了」；
    // 放热那一条已经亮着时 S1 再起来，Alarm 只是从一句变成「放热那句；S1 那句」，没有沿，不 Toast、不进面板日志。
    // 这条报警起来时夹套确实降下来了（冷却在出力），该看的是釜内探头和釜里情况

    private const string ReadText = "通道1釜内读数没跟着夹套降：读数 6 min 没动过 0.02 K（70.00 ℃），这段里夹套一直往下推，按新算法认得出最慢的釜也该降 0.26 K。" +
        "可能是正在慢慢变强的放热（夹套在跟着把热带走），也可能是釜内探头读数卡住、不在本釜液里——两样从温度上分不开，按「放热只报不停」不停控；请人看一眼釜内探头和釜里情况";

    [Fact]
    public async Task 读数报警单独起来_Toast叫人看釜内探头不说冷却水_头卡说读数报警_撤了记读数报警撤了不说放热报警撤了()
    {
        var f = new FakeLoop { Active = true, Setpoint = 60, OuterAlgorithmLive = true, OuterAlgorithmLiveInUse = true };
        await using var host = new Host(f);
        var vm = new HmiViewModel(host, "R1", new[] { 1 });
        var z = vm.Zones[0];
        f.SetAlarms(null, ReadText);
        vm.Refresh();
        Assert.Equal(ReadText, z.LoopAlarm);                            // 红条照驱动的话摆（不变）
        var toast = vm.ToastText;
        Assert.StartsWith("通道 1 釜内读数没跟着夹套降：", toast);
        Assert.Contains("控温不停", toast);
        Assert.Contains("釜内探头", toast);
        Assert.DoesNotContain("冷却水", toast);
        Assert.DoesNotContain("TEC 电流", toast);
        Assert.DoesNotContain("放热报警", toast);
        Assert.Contains("回路 " + ReadText, z.LastLog);
        Assert.EndsWith(" · 釜内读数报警（见温控卡）", z.HeadNote);
        Assert.DoesNotContain("放热报警", z.HeadNote);

        // 撤了（连着 3 拍读不到）：记「读数没跟着夹套降」那一条撤了，不说放热报警撤了；不 Toast
        vm.Toast("哨兵");
        f.SetAlarms(null, null);
        vm.Refresh();
        vm.Refresh();
        vm.Refresh();
        Assert.Contains("「读数没跟着夹套降」报警撤了", z.LastLog);
        Assert.DoesNotContain("放热报警撤了", z.LastLog);
        Assert.Equal("哨兵", vm.ToastText);
        Assert.DoesNotContain("报警", z.HeadNote);
    }

    [Fact]
    public async Task 放热报警亮着时读数报警再起来_读数报警照样Toast一次进面板日志一次_读数报警先撤只记它撤了_放热那条照旧()
    {
        var f = new FakeLoop { Active = true, Setpoint = 60, OuterAlgorithmLive = true, OuterAlgorithmLiveInUse = true };
        await using var host = new Host(f);
        var vm = new HmiViewModel(host, "R1", new[] { 1 });
        var z = vm.Zones[0];
        f.SetAlarms(AlarmText, null);
        vm.Refresh();
        Assert.Equal(HmiOuterAlgo.AlarmToast(1), vm.ToastText);         // 放热那一条：原样
        Assert.EndsWith(" · 放热报警（见温控卡）", z.HeadNote);

        vm.Toast("哨兵");
        f.SetAlarms(AlarmText, ReadText);
        vm.Refresh();
        Assert.Equal(AlarmText + "；" + ReadText, z.LoopAlarm);           // 红条两句接着摆（不变）
        Assert.StartsWith("通道 1 釜内读数没跟着夹套降：", vm.ToastText);    // 第二种照样有自己的沿
        Assert.Contains("回路 " + ReadText, z.LastLog);
        Assert.DoesNotContain("回路 " + AlarmText + "；", z.LastLog);     // 面板日志记的是它自己那一句，不是接起来的整串
        Assert.Contains("放热报警", z.HeadNote);
        Assert.Contains("釜内读数报警", z.HeadNote);
        vm.Toast("哨兵");
        vm.Refresh();
        vm.Refresh();
        Assert.Equal("哨兵", vm.ToastText);                              // 一直在：不重报

        // 读数报警先撤、放热那条还在：只记读数报警撤了
        f.SetAlarms(AlarmText, null);
        vm.Refresh();
        vm.Refresh();
        vm.Refresh();
        Assert.Contains("「读数没跟着夹套降」报警撤了", z.LastLog);
        Assert.DoesNotContain("放热报警撤了", z.LastLog);
        Assert.EndsWith(" · 放热报警（见温控卡）", z.HeadNote);
        Assert.Equal("哨兵", vm.ToastText);

        // 放热那条也撤：这才记放热报警撤了（原话）
        f.SetAlarms(null, null);
        vm.Refresh();
        vm.Refresh();
        vm.Refresh();
        Assert.Contains("放热报警撤了（报警条件不再成立，或者已停控）", z.LastLog);
    }

    [Fact]
    public async Task 旧外环两样都没有_面板什么都不多说()
    {
        var f = new FakeLoop { Active = true, Setpoint = 60, OuterAlgorithmLive = false, OuterAlgorithmLiveInUse = false };
        await using var host = new Host(f);
        var vm = new HmiViewModel(host, "R1", new[] { 1 });
        var z = vm.Zones[0];
        vm.Toast("哨兵");
        var log = z.LastLog;
        for (var k = 0; k < 5; k++) vm.Refresh();
        Assert.Null(z.LoopAlarm);
        Assert.Equal("哨兵", vm.ToastText);
        Assert.Equal(log, z.LastLog);
        Assert.DoesNotContain("报警", z.HeadNote);
    }

    [Fact]
    public async Task 温控在途转圈时点外环_不交给驱动_说等转完再选()
    {
        // 【本地改动】（第 1 步复核）开温控要先通水 / 切热源（几秒），锁定排在那之后的 StartChannelAsync：转圈期间改选会被这一次锁走，
        // 提示却说「下一次」。转圈期间不交给驱动
        var f = new FakeLoop { OuterAlgorithmNote = "外环：旧", Hold = new TaskCompletionSource() };
        await using var host = new Host(f);
        var vm = new HmiViewModel(host, "R1", new[] { 1 });
        var z = vm.Zones[0];
        Assert.True(z.ShowOuterPick);
        z.ToggleTemp();                                         // 打开温控：下发卡着不回来 = 一直转圈
        Assert.True(z.TempBusy);
        z.PickOuter(true);
        Assert.Empty(f.Picks);                                  // 没交给驱动
        Assert.False(z.OuterLive);
        Assert.Equal("温控正在下发（转圈中），转完再选外环", vm.ToastText);
        Assert.DoesNotContain("外环选", z.LastLog);
        z.PickOuter(false);                                     // 选旧也一样拦（跟已选的一样本来就不发，这里先拦）
        Assert.Empty(f.Picks);
        f.Hold.SetResult();                                     // 放开（面板收菊花靠 UI 线程，单测里不跑；这里只钉转圈期间）
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Tr丢失保持_新外环在控说按模型推_旧外环原句一字不变(bool live)
    {
        var f = new FakeLoop { Active = true, Setpoint = 50, OuterAlgorithmLive = live, OuterAlgorithmLiveInUse = live };
        await using var host = new Host(f);
        var vm = new HmiViewModel(host, "R1", new[] { 1 });
        var z = vm.Zones[0];
        f.Holding = "釜内 Tr 读数丢失 3 s，……";
        vm.Refresh();
        Assert.Equal(f.Holding, z.LoopNotice);
        if (live)
        {
            Assert.Contains("回路 " + HmiOuterAlgo.LiveHolding().Log, z.LastLog);
            Assert.Equal(HmiOuterAlgo.LiveHolding().Toast, vm.ToastText);
            Assert.DoesNotContain("钳到釜内设定", z.LastLog);
        }
        else
        {
            Assert.Contains("回路 釜内 Tr 读数丢失，夹套设定钳到釜内设定保持，等它回来", z.LastLog);
            Assert.Equal("釜内 Tr 读数丢失，夹套设定已钳到釜内设定，宽限内回来自动继续", vm.ToastText);
        }
        f.Holding = null;
        vm.Refresh();
        Assert.Equal("釜内 Tr 已恢复，串级继续", vm.ToastText);
    }
}
