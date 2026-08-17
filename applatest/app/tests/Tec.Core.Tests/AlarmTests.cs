using Tec.Core.Benches;
using Tec.Core.Catalog;
using Tec.Core.Data;
using Tec.Core.Execution;
using Tec.Core.Records;
using Tec.Core.Safety;
using Tec.Driver.Abi;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 报警的确认与处理。
///
/// 从前安全层只做两件事：写一条事件、AbortChannel / StopAll。剩下的
/// 「停加料」「停加热」落在 default 分支上**什么也没做**——限值上写着
/// 「超限停加料」，超限了泵却照打，比没有这条限值更糟：现场以为有人兜着。
/// 而且没有任何"确认"的概念，一条报警报完就沉进记录里，谁也不知道有没有人看过。
///
/// 这里盯三件事：**只报一次**（刷屏会把该看的那条埋掉）、
/// **动作真的落到设备上**、**确认由人来做且留痕**。
/// </summary>
public class AlarmTests
{
    // ── 台子 ────────────────────────────────────────────────────────
    // 不用仿真反应器：它自己一直在推 Tr，测不了"这一秒读到什么值"。
    // 挂一根自己控制的标签，钟也自己拨。

    private sealed class Bed : IAsyncDisposable
    {
        public DateTimeOffset Now = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
        public readonly DataPipeline Pipeline = new();
        public readonly StubTemp Temp = new();
        public readonly StubDose Dose = new();
        public readonly RunEngine Engine;
        public readonly Channel Channel;

        public Bed(bool withTemp = true, bool withDose = true)
        {
            var catalog = new CommandCatalog();
            Engine = new RunEngine(catalog, new BuiltinCommandProvider(new AutoOperatorGate()),
                                   new ResourceArbiter(), Pipeline, () => Now);
            Channel = new Channel(1, "X1", 0);
            Channel.Attach(new CapSession(withTemp ? Temp : null, withDose ? Dose : null), 0, true);
            Engine.Attach(Channel);
        }

        /// <summary>压力这一路：仿真设备不产这个标签，测里怎么推就怎么读。</summary>
        public void Push(double value, Quality q = Quality.Good)
            => Pipeline.Push(new Sample(1, "p", Now.Ticks, Now, value, q));

        public SafetyLimit Limit(SafetyAction action, double max = 5, int debounceSec = 0)
            => new(1, "p", null, max, null, TimeSpan.FromSeconds(debounceSec), action);

        public void Tick(int seconds = 1) => Now += TimeSpan.FromSeconds(seconds);

        public ValueTask DisposeAsync() { Engine.Dispose(); return ValueTask.CompletedTask; }
    }

    private sealed class StubTemp : ITemperatureControl
    {
        public int StopCalls { get; private set; }
        public int Channel => 1;
        public TempLimits Limits { get; } = new(-20, 180, 10);
        public double CurrentReactor => 25;
        public double CurrentJacket => 25;
        public Task SetTargetAsync(TempTarget t, CancellationToken ct) => Task.CompletedTask;
        public Task RampAsync(double t, double r, TempChannelKind k, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> WaitReachedAsync(double t, double tol, TimeSpan to, CancellationToken ct) => Task.FromResult(true);
        public Task StopAsync(CancellationToken ct) { StopCalls++; return Task.CompletedTask; }
        public IObservable<Sample> Temperature { get; } = new Broadcast<Sample>();
    }

    private sealed class StubDose : IDosing
    {
        public int StopCalls { get; private set; }
        public int Channel => 1;
        public FlowLimits Limits { get; } = new(0, 20, 50);
        public CalibrationRecord? Calibration => null;
        public double TotalVolume => 0;
        public Task DoseAsync(DoseRequest r, CancellationToken ct) => Task.CompletedTask;
        public Task SetRateAsync(double r, CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) { StopCalls++; return Task.CompletedTask; }
        public IObservable<Sample> Flow { get; } = new Broadcast<Sample>();
        public IObservable<Sample> Total { get; } = new Broadcast<Sample>();
    }

    private sealed class CapSession : IDeviceSession
    {
        private readonly ICapability[] _caps;
        public CapSession(ITemperatureControl? t, IDosing? d)
            => _caps = new ICapability?[] { t, d }.Where(c => c is not null).Cast<ICapability>().ToArray();

        public string InstanceId => "X1";
        public DeviceState State => DeviceState.Ready;
        public event EventHandler<DeviceState>? StateChanged { add { } remove { } }
        public IReadOnlyList<TagDescriptor> Tags => Array.Empty<TagDescriptor>();
        public IObservable<Sample> Samples { get; } = new Broadcast<Sample>();
        public int WellCount => 1;
        public IReadOnlyList<ICapability> CapabilitiesOf(int well) => _caps;
        public ICommandHandler? Resolve(string commandId) => null;
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // ── 只报一次 ────────────────────────────────────────────────────

    [Fact]
    public async Task 一直越着只报一条_不刷屏()
    {
        // 温度贴着上限抖，一秒一条能在十分钟里刷出六百行，
        // 真正要人管的那条反而被埋掉了
        await using var bed = new Bed();
        bed.Engine.Safety.Add(bed.Limit(SafetyAction.Alarm));

        for (var i = 0; i < 20; i++)
        {
            bed.Push(9);
            bed.Engine.Safety.Evaluate();
            bed.Tick();
        }

        Assert.Single(bed.Engine.Alarms.Live);
        Assert.Equal(1, bed.Engine.Alarms.Live[0].Episodes);
    }

    [Fact]
    public async Task 去抖没到不报()
    {
        await using var bed = new Bed();
        bed.Engine.Safety.Add(bed.Limit(SafetyAction.Alarm, debounceSec: 5));

        bed.Push(9);
        bed.Engine.Safety.Evaluate();
        bed.Tick(2);
        bed.Push(9);
        bed.Engine.Safety.Evaluate();
        Assert.Empty(bed.Engine.Alarms.Live);        // 才 2 秒，还在去抖里

        bed.Tick(6);
        bed.Push(9);
        bed.Engine.Safety.Evaluate();
        Assert.Single(bed.Engine.Alarms.Live);
    }

    [Fact]
    public async Task 读不到值也报警()
    {
        // 断线不报警是最危险的失败模式：读不到就当作正常，等于把眼睛蒙上（§7.5）
        await using var bed = new Bed();
        bed.Engine.Safety.Add(bed.Limit(SafetyAction.Alarm));

        bed.Engine.Safety.Evaluate();               // 一个点都没推过
        var a = Assert.Single(bed.Engine.Alarms.Live);
        Assert.Contains("无信号", a.Message);
    }

    [Fact]
    public async Task 消息不自带通道前缀()
    {
        // 报警清单、执行记录、报告都自带「通道」这一列。文本再带一个 CHn，
        // 界面上就成了「CH1 CH1 Tr 高于上限」
        await using var bed = new Bed();
        bed.Engine.Safety.Add(bed.Limit(SafetyAction.Alarm));

        bed.Push(9);
        bed.Engine.Safety.Evaluate();

        Assert.DoesNotContain("CH1", bed.Engine.Alarms.Live[0].Message);
    }

    [Fact]
    public async Task 越限那个数印得出和限值的差别()
    {
        // 一位小数印出来常常和限值一模一样（「高于上限 180.0（180.0）」），
        // 读的人只会以为程序算错了
        await using var bed = new Bed();
        bed.Engine.Safety.Add(bed.Limit(SafetyAction.Alarm, max: 180));

        bed.Push(180.03);
        bed.Engine.Safety.Evaluate();

        Assert.Contains("180.03", bed.Engine.Alarms.Live[0].Message);
    }

    [Fact]
    public void 缺省限值比设备工作范围松一档()
    {
        // 实测撞出来的：设备允许 16 ℃/min，配方照 16 ℃/min 跑，噪声让实测
        // 斜率在 16 上下抖——限值卡死在 16，一次完全正常的升温就被判成超速
        // 并中止整批。联锁防的是"跑飞了"，不是"贴着上限干活"
        var dev = new TempLimits(-40, 180, 16);
        var lim = SafetyMonitor.FromTemperature(1, dev);

        Assert.True(lim.Max > dev.Max);
        Assert.True(lim.Min < dev.Min);
        Assert.True(lim.MaxRatePerMin > dev.MaxRatePerMin);
        Assert.True(lim.FromDeviceLimits);
    }

    // ── 恢复与确认 ──────────────────────────────────────────────────

    [Fact]
    public async Task 恢复了也不会自己消失_要人确认过才翻篇()
    {
        // 一条自己好了的报警要是悄悄消失，操作人根本不会知道刚才出过事
        await using var bed = new Bed();
        bed.Engine.Safety.Add(bed.Limit(SafetyAction.Alarm));

        bed.Push(9);
        bed.Engine.Safety.Evaluate();
        bed.Tick(10);
        bed.Push(1);                                 // 回到范围内
        bed.Engine.Safety.Evaluate();

        var a = Assert.Single(bed.Engine.Alarms.Live);
        Assert.False(a.Standing);
        Assert.Equal("已恢复，待确认", a.StateText);

        Assert.True(bed.Engine.AckAlarm(a.Key, "张三"));
        Assert.Empty(bed.Engine.Alarms.Live);        // 恢复了又确认过 → 翻篇
    }

    [Fact]
    public async Task 确认不等于解除_条件还成立的仍然留在清单上()
    {
        await using var bed = new Bed();
        bed.Engine.Safety.Add(bed.Limit(SafetyAction.Alarm));
        bed.Push(9);
        bed.Engine.Safety.Evaluate();

        var key = bed.Engine.Alarms.Live[0].Key;
        Assert.True(bed.Engine.AckAlarm(key, "张三"));

        var a = Assert.Single(bed.Engine.Alarms.Live);
        Assert.True(a.Acknowledged);
        Assert.True(a.Standing);
        Assert.Equal("报警中（已确认）", a.StateText);
        Assert.Equal(0, bed.Engine.Alarms.UnackedCount);
        Assert.Equal(1, bed.Engine.Alarms.StandingCount);
    }

    [Fact]
    public async Task 没人确认就恢复了又回来_还是同一条_记第二回()
    {
        // 时好时坏的那种最烦人，但它是一件事，不该在清单上摊成两行；
        // 报了几回要留住——「响过三回」和「响过一回」不是一个严重程度
        await using var bed = new Bed();
        bed.Engine.Safety.Add(bed.Limit(SafetyAction.Alarm));

        bed.Push(9); bed.Engine.Safety.Evaluate();
        bed.Tick(5); bed.Push(1); bed.Engine.Safety.Evaluate();     // 恢复，但没人确认
        bed.Tick(5); bed.Push(9); bed.Engine.Safety.Evaluate();     // 又来了

        var a = Assert.Single(bed.Engine.Alarms.Live);
        Assert.Equal(2, a.Episodes);
        Assert.True(a.Standing);
        Assert.Equal(1, bed.Engine.Alarms.UnackedCount);
    }

    [Fact]
    public async Task 确认过的恢复之后翻篇_再报是新的一条()
    {
        // 那件事已经有人负责过了。同一条限值后来又越，是新的一件事，
        // 得重新有人看——沿用上一次的确认，会让一条正在响的报警
        // 看上去"已经有人管了"
        await using var bed = new Bed();
        bed.Engine.Safety.Add(bed.Limit(SafetyAction.Alarm));

        bed.Push(9); bed.Engine.Safety.Evaluate();
        bed.Engine.AckAlarm(bed.Engine.Alarms.Live[0].Key, "张三");
        bed.Tick(5); bed.Push(1); bed.Engine.Safety.Evaluate();     // 恢复 → 翻篇
        Assert.Empty(bed.Engine.Alarms.Live);

        bed.Tick(5); bed.Push(9); bed.Engine.Safety.Evaluate();     // 又来了

        var a = Assert.Single(bed.Engine.Alarms.Live);
        Assert.False(a.Acknowledged);
        Assert.Equal(1, a.Episodes);
        Assert.Equal(2, bed.Engine.Alarms.All.Count);               // 历史上两件事都留着
    }

    [Fact]
    public void 同一条再报一次_上一次的确认作废()
    {
        // 直接对报警本立的规矩。安全层那条路上「确认过 + 已恢复」会当场翻篇，
        // 走不到这一支；但限值被单独重设过（Safety.Clear 之后重新 Add）就会走到，
        // 而那时候沿用旧确认是危险的
        var book = new AlarmBook();
        var at = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
        var lim = new SafetyLimit(1, "p", null, 5, null, TimeSpan.Zero, SafetyAction.Alarm);
        var ev = new SafetyEvent(at, 1, lim, "CH1 p 高于上限 5（9）", 9);

        var a = book.Raise(ev, Array.Empty<string>(), at);
        book.Ack(a.Key, "张三", null, at);
        Assert.True(a.Acknowledged);

        book.Raise(ev, Array.Empty<string>(), at.AddMinutes(1));
        Assert.False(a.Acknowledged);
        Assert.Equal(2, a.Episodes);
    }

    // ── 动作真的落到设备上 ──────────────────────────────────────────

    [Fact]
    public async Task 停加热真的切了输出()
    {
        await using var bed = new Bed();
        bed.Engine.Safety.Add(bed.Limit(SafetyAction.StopHeating));

        bed.Push(9);
        bed.Engine.Safety.Evaluate();

        Assert.Equal(1, bed.Temp.StopCalls);
        Assert.Contains(bed.Engine.Alarms.Live[0].Did, s => s.Contains("切断加热输出"));
    }

    [Fact]
    public async Task 停加料真的停了泵()
    {
        await using var bed = new Bed();
        bed.Engine.Safety.Add(bed.Limit(SafetyAction.StopDosing));

        bed.Push(9);
        bed.Engine.Safety.Evaluate();

        Assert.Equal(1, bed.Dose.StopCalls);
        Assert.Contains(bed.Engine.Alarms.Live[0].Did, s => s.Contains("停加料泵"));
    }

    [Fact]
    public async Task 仅报警就真的什么都不动_但要说清楚没动()
    {
        // 「仅报警」超限时机器一动不动，现场必须知道这一点，
        // 否则会以为已经被兜住了
        await using var bed = new Bed();
        bed.Engine.Safety.Add(bed.Limit(SafetyAction.Alarm));

        bed.Push(9);
        bed.Engine.Safety.Evaluate();

        Assert.Equal(0, bed.Temp.StopCalls);
        Assert.Equal(0, bed.Dose.StopCalls);
        Assert.Empty(bed.Engine.Alarms.Live[0].Did);
    }

    [Fact]
    public async Task 这一路没有那种设备就照实说_不假装停过()
    {
        await using var bed = new Bed(withTemp: false);
        bed.Engine.Safety.Add(bed.Limit(SafetyAction.StopHeating));

        bed.Push(9);
        bed.Engine.Safety.Evaluate();

        Assert.Contains(bed.Engine.Alarms.Live[0].Did, s => s.Contains("没有可停的控温设备"));
    }

    // ── 留痕 ────────────────────────────────────────────────────────

    [Fact]
    public async Task 报警与动作逐条进执行记录()
    {
        await using var bed = new Bed();
        var run = Started(bed);
        bed.Engine.Safety.Add(bed.Limit(SafetyAction.StopHeating));

        bed.Push(9);
        bed.Engine.Safety.Evaluate();

        Assert.Contains(run.Events, e => e.Kind == EventKind.Alarm && e.Text.Contains("停加热"));
        Assert.Contains(run.Events, e => e.Kind == EventKind.SafetyAction && e.Text.Contains("切断加热输出"));
    }

    [Fact]
    public async Task 确认进记录_署名与处理说明都在()
    {
        // GLP 追的不只是「机器什么时候报的」，还有「谁在什么时候看见了它」
        await using var bed = new Bed();
        var run = Started(bed);
        bed.Engine.Safety.Add(bed.Limit(SafetyAction.Alarm));
        bed.Push(9);
        bed.Engine.Safety.Evaluate();

        bed.Engine.AckAlarm(bed.Engine.Alarms.Live[0].Key, "张三", "已现场查看，冷凝水阀没开");

        var ack = Assert.Single(run.Events, e => e.Kind == EventKind.AlarmAck);
        Assert.Equal("张三", ack.User);
        Assert.Contains("冷凝水阀没开", ack.Text);
    }

    [Fact]
    public async Task 恢复也记一条_答得出响了多久()
    {
        await using var bed = new Bed();
        var run = Started(bed);
        bed.Engine.Safety.Add(bed.Limit(SafetyAction.Alarm));

        bed.Push(9);
        bed.Engine.Safety.Evaluate();
        bed.Tick(125);
        bed.Push(1);
        bed.Engine.Safety.Evaluate();

        var cleared = Assert.Single(run.Events, e => e.Kind == EventKind.AlarmCleared);
        Assert.Contains("持续 0:02:05", cleared.Text);
    }

    [Fact]
    public async Task 限值重设把正在报的收尾_没确认的仍要人确认()
    {
        // 台面一动全部限值重来。留着不管的话，那条报警会一直等一个
        // 再也不会来的恢复
        await using var bed = new Bed();
        bed.Engine.Safety.Add(bed.Limit(SafetyAction.Alarm));
        bed.Push(9);
        bed.Engine.Safety.Evaluate();

        bed.Engine.ResetSafety("台面重建，限值已重设");

        var a = Assert.Single(bed.Engine.Alarms.Live);
        Assert.False(a.Standing);
        Assert.False(a.Acknowledged);
        Assert.Contains("限值已重设", a.Message);
    }

    [Fact]
    public async Task 全部确认一次到位()
    {
        await using var bed = new Bed();
        bed.Engine.Safety.Add(bed.Limit(SafetyAction.Alarm));
        bed.Engine.Safety.Add(new SafetyLimit(1, "p", 3, null, null, TimeSpan.Zero, SafetyAction.StopHeating));

        bed.Push(9);
        bed.Engine.Safety.Evaluate();
        Assert.Equal(1, bed.Engine.Alarms.UnackedCount);   // 只有上限那条越了

        bed.Push(1);                                       // 反过来越下限
        bed.Tick();
        bed.Engine.Safety.Evaluate();
        Assert.Equal(2, bed.Engine.Alarms.Live.Count);

        Assert.Equal(2, bed.Engine.AckAllAlarms("张三"));
        Assert.Equal(0, bed.Engine.Alarms.UnackedCount);
    }

    /// <summary>起一趟，好让报警有条记录链可挂。</summary>
    private static ChannelRun Started(Bed bed)
    {
        bed.Engine.NewBatch("测试批次", "张三", "测试台面");
        var recipe = Harness.RecipeOf("等着", Harness.Mk(BuiltinCommands.Wait, ("dur", 3600d)));
        return bed.Engine.StartChannel(1, recipe, "张三");
    }
}
