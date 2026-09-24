using Tec.Driver.Abi;
using Tec.Drivers.DualStation;
using Tec.Drivers.Rd105;
using Tec.Drivers.Rd105.Modbus;
using TecControl.Core.Control;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 「控温方式 = 上位机 PID」那条路：温控器只采温度、出功率（MODE=3），回路在 HostControlLoop 里跑。
/// 假 RD105 带一个热模型（按写进去的占空比积分夹套温度），锁的是：
/// · 下发目标写的是 MODE=3 + 占空比，不是 TG；方向按「TEC 输出反向」；温度真往目标走；
/// · 釜内目标没有 Tr 就拒绝，有 Tr 才是串级（夹套设定值 ≠ 釜内目标）；
/// · 热源切换那两秒回路挂起不写占空比，换到加热棒后占空比符号按「加热棒占空比」；
/// · 增益表按路落盘、开机读回；回路停控（Tr 丢失）传到组合会话，继电器下一拍断开；
/// · 停会话把两路输出归零关使能。
/// </summary>
public class Rd105HostControlTests
{
    private static ParameterSet Conn() => ParameterSet.Of((Rd105TecDriver.FieldPeriod, 200d));

    private static ParameterSet HostCfg(params (string Key, object? Value)[] more)
        => ParameterSet.Of(new (string, object?)[] { (Rd105TecDriver.FieldControl, Rd105TecDriver.ControlHost) }
                           .Concat(more).ToArray());

    private static DriverContext Ctx(ParameterSet cfg, List<string>? logs = null, string id = "R1") => new()
    {
        InstanceId = id,
        ChannelNumbers = new[] { 1, 2 },
        Config = cfg,
        Simulated = false,
        TimeScale = 1,
        Clock = () => DateTimeOffset.Now,
        Log = (l, t) => { if (logs is not null) lock (logs) logs.Add($"{l} {t}"); }
    };

    private static (Rd105TecDriver Drv, FakeRd105Device Dev) Standalone()
    {
        // 热模型的增益放大到 100 % = 1 ℃/s：保守缺省增益（Kp 8）下几秒内就看得出往哪边走
        var dev = new FakeRd105Device { Thermal = true, GainCPerSec = 1.0 };
        dev.Set(1, "LIMITED", 90); dev.Set(2, "LIMITED", 90);
        return (new Rd105TecDriver { LinkFactory = _ => new Rd105Link(dev) }, dev);
    }

    private static Task WaitUntil(Func<bool> cond, int ms, string what) => WaitUntil(cond, ms, () => what);

    private static async Task WaitUntil(Func<bool> cond, int ms, Func<string> what)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond())
        {
            Assert.True(DateTime.UtcNow < deadline, "等太久：" + what());
            await Task.Delay(25);
        }
    }

    private static ITemperatureControl Temp(IDeviceSession s, int well)
        => s.CapabilitiesOf(well).OfType<ITemperatureControl>().Single();

    private static ITemperatureStatus St(ITemperatureControl t) => (ITemperatureStatus)t;

    /// <summary>
    /// 像宇电探头会话那样每半秒喂一次釜内 Tr（真时钟）：组合会话的 Tr 新鲜度窗是 10 s，喂一次就不管的话
    /// 十秒后回路会因为「Tr 读数丢失」停控——那是另一条用例要锁的事。Dispose 停喂。
    /// </summary>
    private sealed class TrFeeder : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        public double Value;
        public TrFeeder(IDeviceSession s, int channel, double value)
        {
            Value = value;
            var ext = (IExternalReactorTemp)s;
            _ = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    ext.FeedReactor(channel, Volatile.Read(ref Value), Quality.Good);
                    try { await Task.Delay(500, _cts.Token); } catch (OperationCanceledException) { break; }
                }
            });
        }
        public void Dispose() => _cts.Cancel();
    }

    private sealed class Collect(Action<Sample> on) : IObserver<Sample>
    {
        public void OnNext(Sample value) => on(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    [Fact]
    public async Task 夹套目标_写MODE3和占空比不写TG_温度往目标走_duty和Tjset采样()
    {
        var (drv, dev) = Standalone();
        var logs = new List<string>();
        await using var s = await drv.OpenAsync(Conn(), Ctx(HostCfg(), logs), CancellationToken.None);
        await s.StartAsync(CancellationToken.None);
        Assert.True(((Rd105Session)s).HostControlled);
        Assert.Contains(logs, l => l.Contains("上位机 PID") && l.Contains("表都空着"));

        var got = new List<Sample>();
        using var sub = s.Samples.Subscribe(new Collect(x => { lock (got) got.Add(x); }));

        var t = Temp(s, 0);
        await WaitUntil(() => !double.IsNaN(t.CurrentJacket), 3000, "夹套读数");
        await t.SetTargetAsync(new TempTarget(30, TempChannelKind.Jacket), CancellationToken.None);

        Assert.Equal(3, dev.Get(1, "MODE"));
        Assert.Equal(1, dev.Get(1, "ENABLE"));
        Assert.DoesNotContain(dev.Commands, c => c.StartsWith("TC1:TG=") && !c.Contains("=?"));   // 查询 TG=? 不算写
        // 单独的 RD105 驱动缺省不反向：要升温 → 正占空比
        await WaitUntil(() => dev.Get(1, "PWMDUTY") > 0, 2000, "正占空比");
        await WaitUntil(() => t.CurrentJacket > 26.5, 8000, "夹套往 30 走");
        Assert.True(St(t).Active);
        Assert.Equal(30, St(t).Setpoint);
        // 面板用的两个口子：上位机回路；单环夹套没有「串级内环设定」
        Assert.True(St(t).HostLoop);
        Assert.Null(St(t).CascadeInnerSetpoint);
        // B 路没动：独立两路
        Assert.NotEqual(3, dev.Get(2, "MODE"));
        Assert.Equal(0, dev.Get(2, "PWMDUTY"));

        lock (got)
        {
            Assert.Contains(got, x => x.Tag == "duty" && x.Channel == 1 && x.Value > 0);
            Assert.Contains(got, x => x.Tag == "Tjset" && x.Channel == 1 && x.Value == 30);
            Assert.Contains(got, x => x.Tag == "Tj" && x.Channel == 1);
        }

        await t.StopAsync(CancellationToken.None);
        Assert.Equal(0, dev.Get(1, "PWMDUTY"));
        Assert.Equal(0, dev.Get(1, "ENABLE"));
        Assert.False(St(t).Active);
    }

    [Fact]
    public async Task 反向配置_要升温写负占空比_假机器按现场极性照样升温()
    {
        var (drv, dev) = Standalone();
        dev.HeatSign = -1;                                 // 现场那台：负占空比 = 升温
        var cfg = HostCfg((Rd105TecDriver.FieldInvert, Rd105TecDriver.InvertYes));
        await using var s = await drv.OpenAsync(Conn(), Ctx(cfg), CancellationToken.None);
        await s.StartAsync(CancellationToken.None);
        var t = Temp(s, 0);
        await WaitUntil(() => !double.IsNaN(t.CurrentJacket), 3000, "夹套读数");
        await t.SetTargetAsync(new TempTarget(30, TempChannelKind.Jacket), CancellationToken.None);
        await WaitUntil(() => dev.Get(1, "PWMDUTY") < 0, 2000, "负占空比");
        await WaitUntil(() => t.CurrentJacket > 26.5, 8000,
            () => $"夹套往 30 走（夹套 {t.CurrentJacket:F2}，占空比 {dev.Get(1, "PWMDUTY")}，MODE {dev.Get(1, "MODE")}，ENABLE {dev.Get(1, "ENABLE")}；末尾指令 {string.Join(" ", dev.Commands.TakeLast(8))}）");
    }

    [Fact]
    public async Task 釜内目标_没有Tr拒绝_有Tr串级_夹套设定值不等于釜内目标()
    {
        var (drv, dev) = Standalone();
        var logs = new List<string>();
        await using var s = await drv.OpenAsync(Conn(), Ctx(HostCfg(), logs), CancellationToken.None);
        await s.StartAsync(CancellationToken.None);
        var rd = (Rd105Session)s;
        var t = Temp(s, 0);
        await WaitUntil(() => !double.IsNaN(t.CurrentJacket), 3000, "夹套读数");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => t.SetTargetAsync(new TempTarget(30, TempChannelKind.Reactor), CancellationToken.None));
        Assert.Contains("釜内 Tr 没有读数", ex.Message);
        Assert.NotEqual(3, dev.Get(1, "MODE"));           // 拒绝了就什么都没写

        rd.TempOf(0).FeedReactor(20.0);                    // 釜内 20，目标 30：外环要把夹套推得比 30 高
        await t.SetTargetAsync(new TempTarget(30, TempChannelKind.Reactor), CancellationToken.None);
        Assert.Equal(TempChannelKind.Reactor, rd.TempOf(0).Kind);
        await WaitUntil(() => rd.TempOf(0).InnerSetpoint is { } i && i > 30.5, 6000, "串级外环把夹套设定值抬到 30 以上");
        Assert.Equal(30, St(t).Setpoint);
        // 面板夹套气泡「内环 X ℃」念的就是这个：串级、在控时有数，而且不是釜内目标
        Assert.True(St(t).CascadeInnerSetpoint is { } cis && cis > 30.5);

        var got = new List<Sample>();
        using var sub = s.Samples.Subscribe(new Collect(x => { lock (got) got.Add(x); }));
        await WaitUntil(() => { lock (got) return got.Any(x => x.Tag == "Tjset" && x.Value > 30.5); }, 4000, "Tjset 采样");

        // Tr 丢了：不再当拍停控——外环冻结、夹套设定钳到釜内设定（30）保持，Tjset 照发、回路照开
        // （现场：探头样本断了 10 s 回路就停、加热棒关了，面板上只剩开关自己灭了）
        string? why = null;
        rd.TempOf(0).Tripped += r => why = r;
        var before = St(t).CascadeInnerSetpoint!.Value;
        rd.TempOf(0).FeedReactor(double.NaN);
        await WaitUntil(() => St(t).CascadeInnerSetpoint is { } h && Math.Abs(h - 30) < 1e-9, 3000, "夹套设定钳到釜内设定 30");
        await Task.Delay(1000);
        Assert.Null(why);
        Assert.True(St(t).Active);
        Assert.NotNull(St(t).Holding);
        Assert.Contains("夹套按 30.0 ℃ 保持", St(t).Holding);
        Assert.Contains(logs, l => l.Contains("外环冻结") && l.Contains("收到 30.0 ℃"));
        lock (got) Assert.Contains(got, x => x.Tag == "Tjset" && Math.Abs(x.Value - 30) < 1e-9);

        // Tr 回来了：无扰接续——外环接着算，内环设定回到丢之前那个数（同样的 Tr、同样的积分）
        rd.TempOf(0).FeedReactor(20.0);
        await WaitUntil(() => St(t).CascadeInnerSetpoint is { } h && Math.Abs(h - before) < 0.1, 6000,
            () => $"内环设定回到 {before:F2}（此刻 {St(t).CascadeInnerSetpoint}）");
        Assert.Null(why);
        Assert.Null(St(t).Holding);
        Assert.Null(St(t).LastStop);
        Assert.Contains(logs, l => l.Contains("釜内 Tr 已恢复（丢失") && l.Contains("积分沿用"));
    }

    [Fact]
    public async Task 釜内串级_Tr丢失宽限0_当拍停控_原因与状态()
    {
        // 「Tr 丢失宽限」= 0 是 0328 之前的行为：读不到当拍停控
        var (drv, dev) = Standalone();
        var logs = new List<string>();
        var conn = ParameterSet.Of((Rd105TecDriver.FieldPeriod, 200d), (Rd105TecDriver.FieldTrGrace, 0d));
        await using var s = await drv.OpenAsync(conn, Ctx(HostCfg(), logs), CancellationToken.None);
        await s.StartAsync(CancellationToken.None);
        var rd = (Rd105Session)s;
        var t = Temp(s, 0);
        await WaitUntil(() => !double.IsNaN(t.CurrentJacket), 3000, "夹套读数");
        rd.TempOf(0).FeedReactor(20.0);
        await t.SetTargetAsync(new TempTarget(30, TempChannelKind.Reactor), CancellationToken.None);
        await WaitUntil(() => rd.TempOf(0).InnerSetpoint is { } i && i > 30.5, 6000, "串级起来");

        string? why = null;
        rd.TempOf(0).Tripped += r => why = r;
        rd.TempOf(0).FeedReactor(double.NaN);
        await WaitUntil(() => why is not null, 4000, "Tr 丢失停控");
        Assert.Contains("釜内 Tr 读数丢失", why);
        Assert.Equal(0, dev.Get(1, "PWMDUTY"));
        Assert.False(St(t).Active);
        Assert.Null(St(t).CascadeInnerSetpoint);          // 停了就没有「内环在追的数」
        Assert.Equal(why, St(t).LastStop);                // 面板拿它说「为什么停了」
        Assert.Null(St(t).Holding);

        // 重新下发：LastStop 清掉（不是回路停的了）
        rd.TempOf(0).FeedReactor(20.0);
        await t.SetTargetAsync(new TempTarget(30, TempChannelKind.Reactor), CancellationToken.None);
        Assert.Null(St(t).LastStop);
        await t.StopAsync(CancellationToken.None);
        Assert.Null(St(t).LastStop);
    }

    [Fact]
    public async Task 釜内串级_曲线在Tr丢失保持期暂停_回来接着走()
    {
        var (drv, _) = Standalone();
        await using var s = await drv.OpenAsync(Conn(), Ctx(HostCfg()), CancellationToken.None);
        await s.StartAsync(CancellationToken.None);
        var rd = (Rd105Session)s;
        var t = Temp(s, 0);
        await WaitUntil(() => !double.IsNaN(t.CurrentJacket), 3000, "夹套读数");
        rd.TempOf(0).FeedReactor(20.0);
        // 从 20 起 5 ℃/min 爬到 40：曲线设定每秒走 0.083 ℃
        await t.RampAsync(40, 5, TempChannelKind.Reactor, CancellationToken.None);
        await WaitUntil(() => rd.TempOf(0).InnerSetpoint is { } i && i > 21, 8000, "曲线在走");

        rd.TempOf(0).FeedReactor(double.NaN);
        await WaitUntil(() => St(t).Holding is not null, 3000, "进保持");
        // 保持期内环设定 = 曲线此刻的设定值，而且曲线不走：两秒里纹丝不动
        var a = St(t).CascadeInnerSetpoint!.Value;
        await Task.Delay(2000);
        var b = St(t).CascadeInnerSetpoint!.Value;
        Assert.True(Math.Abs(a - b) < 0.01, $"保持期曲线还在走：{a:F3} → {b:F3}");
        Assert.True(a < 30, $"保持期夹套设定应是曲线此刻的釜内设定（{a:F2}），不是终点 40");

        rd.TempOf(0).FeedReactor(20.0);
        await WaitUntil(() => St(t).Holding is null, 3000, "出保持");
        await Task.Delay(2500);
        // 曲线从暂停处续：设定又往上走了，而且是接着 a 走的（没跳到「本该到的位置」）
        var c = St(t).CascadeInnerSetpoint!.Value;
        Assert.True(c > a + 0.05, $"曲线没接着走：{a:F3} → {c:F3}");
        Assert.True(c < a + 8 + 0.8, $"曲线跳了：{a:F3} → {c:F3}");   // 外环偏置最多 +8，曲线 2.5 s 最多走 0.21
    }

    [Fact]
    public async Task 温控器改写小占空比_回显不符不当通信失败_说一次_曲线画回显()
    {
        // 现场：夹套到温后加热棒只要 4 %，写 −4.41 % 温控器回显 −6.43 %——从前算坏帧、连着 20 拍就「连续通信失败」停控
        var (drv, dev) = Standalone();
        dev.HeatSign = -1;
        dev.DutyMinPercent = 6.43;
        var logs = new List<string>();
        var cfg = HostCfg((Rd105TecDriver.FieldInvert, Rd105TecDriver.InvertYes));
        await using var s = await drv.OpenAsync(Conn(), Ctx(cfg, logs), CancellationToken.None);
        await s.StartAsync(CancellationToken.None);
        var t = Temp(s, 0);
        await WaitUntil(() => !double.IsNaN(t.CurrentJacket), 3000, "夹套读数");
        var got = new List<Sample>();
        using var sub = s.Samples.Subscribe(new Collect(x => { lock (got) got.Add(x); }));
        string? why = null;
        ((Rd105Session)s).TempOf(0).Tripped += r => why = r;

        // 环境 25、目标 25.3：维持它只要零点几个百分点——全在温控器的「最小输出」之下，每一拍回显都不符
        await t.SetTargetAsync(new TempTarget(25.3, TempChannelKind.Jacket), CancellationToken.None);
        await Task.Delay(6000);                            // 200 ms 一拍：30 拍，比「连续 20 拍」多
        Assert.Null(why);
        Assert.True(St(t).Active);
        Assert.DoesNotContain(logs, l => l.Contains("轮询异常"));
        Assert.Single(logs, l => l.Contains("温控器回显") && l.Contains("不当通信失败"));   // 说一次，不每拍刷
        lock (got) Assert.Contains(got, x => x.Tag == "duty" && Math.Abs(Math.Abs(x.Value) - 6.43) < 0.01);   // 画的是回显

        dev.DutyMinPercent = 0;                            // 参数窗把启动电压改回 0 之后：对上了，再说一次
        await WaitUntil(() => logs.Any(l => l.Contains("回显又对上了")), 4000, "回显对上");
        await t.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task 上位机整定口子_两路各看自己的Tr_不越界()
    {
        // for 循环变量被 lambda 捕获：循环跑完 i == 2，调用时 _temps[2] 越界（IndexOutOfRangeException）
        var (drv, _) = Standalone();
        await using var s = await drv.OpenAsync(Conn(), Ctx(HostCfg()), CancellationToken.None);
        var rd = (Rd105Session)s;
        var tuneA = s.CapabilitiesOf(0).OfType<ITemperatureTuning>().Single();
        var tuneB = s.CapabilitiesOf(1).OfType<ITemperatureTuning>().Single();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => tuneA.SetStrategyAsync(TempChannelKind.Reactor, CancellationToken.None));
        Assert.Contains("釜内 Tr 没有读数", ex.Message);
        rd.TempOf(1).FeedReactor(25.0);
        await tuneB.SetStrategyAsync(TempChannelKind.Reactor, CancellationToken.None);   // B 有 Tr：成
        await Assert.ThrowsAsync<InvalidOperationException>(() => tuneA.SetStrategyAsync(TempChannelKind.Reactor, CancellationToken.None));   // A 还是没有
    }

    [Fact]
    public async Task NaN目标与NaN速率拒绝_Fx印破折号()
    {
        var (drv, _) = Standalone();
        await using var s = await drv.OpenAsync(Conn(), Ctx(HostCfg()), CancellationToken.None);
        await s.StartAsync(CancellationToken.None);
        var t = Temp(s, 0);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => t.SetTargetAsync(new TempTarget(double.NaN, TempChannelKind.Jacket), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => t.RampAsync(30, double.NaN, TempChannelKind.Jacket, CancellationToken.None));
        Assert.False(St(t).Active);
        Assert.Equal("—", Txt.Fx(double.NaN));
    }

    [Fact]
    public async Task 温控器PID方式_面板口子报不是上位机_没有内环设定()
    {
        var (drv, dev) = Standalone();
        await using var s = await drv.OpenAsync(Conn(), Ctx(ParameterSet.Of((Rd105TecDriver.FieldControl, Rd105TecDriver.ControlDevice))),
                                                CancellationToken.None);
        await s.StartAsync(CancellationToken.None);
        var rd = (Rd105Session)s;
        var t = Temp(s, 0);
        rd.TempOf(0).FeedReactor(20.0);
        await t.SetTargetAsync(new TempTarget(30, TempChannelKind.Reactor), CancellationToken.None);
        Assert.False(rd.HostControlled);
        Assert.True(St(t).Active);
        Assert.False(St(t).HostLoop);
        Assert.Null(St(t).CascadeInnerSetpoint);          // 温控器 PID：釜内目标直接写给夹套，没有内环这回事
        Assert.Equal(30_00000, dev.Get(1, "TG"));          // TG 按 1e-5 刻度
    }

    [Fact]
    public async Task 关输出是挂起_期间不写占空比_换到加热棒后按符号出幅度()
    {
        var (drv, dev) = Standalone();
        var cfg = HostCfg((Rd105TecDriver.FieldHeaterSign, Rd105TecDriver.HeaterNeg));
        await using var s = await drv.OpenAsync(Conn(), Ctx(cfg), CancellationToken.None);
        await s.StartAsync(CancellationToken.None);
        var rd = (Rd105Session)s;
        var inner = rd.TempOf(0);
        await WaitUntil(() => !double.IsNaN(inner.CurrentJacket), 3000, "夹套读数");
        await inner.SetTargetAsync(new TempTarget(40, TempChannelKind.Jacket), CancellationToken.None);
        await WaitUntil(() => dev.Get(1, "PWMDUTY") > 0, 2000, "TEC 侧正占空比");

        await inner.EnableAsync(false, CancellationToken.None);     // 关输出 = 挂起 + 归零 + 关使能
        Assert.Equal(0, dev.Get(1, "PWMDUTY"));
        Assert.Equal(0, dev.Get(1, "ENABLE"));
        var n = dev.Commands.Count;
        await Task.Delay(700);                                        // 三个多周期
        Assert.DoesNotContain(dev.Commands.Skip(n), c => c.StartsWith("TC1:PWMDUTY="));   // 挂起期间一笔都不写

        inner.SetActuator(electric: true);                            // 继电器扳到加热棒
        await inner.EnableAsync(true, CancellationToken.None);
        Assert.Equal(1, dev.Get(1, "ENABLE"));
        // 加热棒：幅度 ≥ 0、符号按配置（负）
        await WaitUntil(() => dev.Get(1, "PWMDUTY") < 0, 2000, "加热棒负占空比");
        await Task.Delay(500);
        var after = dev.Commands.Skip(n).ToList();
        Assert.All(after.Where(c => c.StartsWith("TC1:PWMDUTY=")),
            c => Assert.True(long.Parse(c.Split('=')[1].TrimEnd('@')) <= 0, c + " ｜ " + string.Join(" ", after)));

        // 回 TEC：同样先关输出（挂起）再换形态——没挂起的运行中不许换（形态必须跟继电器位置一致）
        Assert.Throws<InvalidOperationException>(() => inner.SetActuator(electric: false));
        await inner.EnableAsync(false, CancellationToken.None);
        inner.SetActuator(electric: false);
        await inner.EnableAsync(true, CancellationToken.None);
        await WaitUntil(() => dev.Get(1, "PWMDUTY") > 0, 3000, "回 TEC 正占空比");
    }

    [Fact]
    public async Task 增益表_按路落盘_开机读回()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tec-gains-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("TEC_GAINS_DIR", dir);
        try
        {
            var path = Rd105HostControl.GainsPath("R1", 1);
            Assert.StartsWith(dir, path);
            var sched = new PidGainSchedule();
            sched.Learn(new GainPoint(25, new PidGains(12.5, 0.03, 300), 28, 180, new PidGains(2.5, 0.008, 0), 8, -0.35));
            Rd105HostControl.Save(sched, path);
            Assert.True(File.Exists(path));
            Assert.Contains("温度(℃),Kp,Ki,Kd", File.ReadLines(path).First());

            var (drv, dev) = Standalone();
            var logs = new List<string>();
            await using var s = await drv.OpenAsync(Conn(), Ctx(HostCfg(), logs), CancellationToken.None);
            Assert.Contains(logs, l => l.Contains("TC1 上位机 PID") && l.Contains("TEC 表 25 ℃"));
            Assert.Contains(logs, l => l.Contains("TC2 上位机 PID") && l.Contains("表都空着"));
            Assert.Equal(path, ((Rd105Session)s).GainsPathOf(0));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEC_GAINS_DIR", null);
        }
    }

    [Fact]
    public async Task 停会话_两路归零关使能()
    {
        var (drv, dev) = Standalone();
        await using var s = await drv.OpenAsync(Conn(), Ctx(HostCfg()), CancellationToken.None);
        await s.StartAsync(CancellationToken.None);
        var t = Temp(s, 0);
        await WaitUntil(() => !double.IsNaN(t.CurrentJacket), 3000, "夹套读数");
        await t.SetTargetAsync(new TempTarget(35, TempChannelKind.Jacket), CancellationToken.None);
        await WaitUntil(() => dev.Get(1, "PWMDUTY") > 0, 2000, "占空比");
        await s.StopAsync(CancellationToken.None);
        Assert.Equal(0, dev.Get(1, "PWMDUTY"));
        Assert.Equal(0, dev.Get(1, "ENABLE"));
    }

    // ── 组合主机：切换序列 + 回路停控回落 ─────────────────────────────

    private sealed class Duo
    {
        public FakeRd105Device Rd = new() { Thermal = true, HeatSign = -1, GainCPerSec = 1.0 };   // 现场极性
        public FakeModbusSlave Io = new();
        public List<string> Logs = new();
        public DualStationDriver Drv;

        public Duo()
        {
            Rd.Set(1, "LIMITED", 90); Rd.Set(2, "LIMITED", 90);
            Drv = new DualStationDriver
            {
                LinksFactory = _ => new DuoLinks
                {
                    Rd105 = new Rd105Link(Rd),
                    IoPort = Io,
                    Io = new Io8rClient(new ModbusRtuClient(Io, 1, 200))
                }
            };
        }

        public DriverContext Ctx(ParameterSet? cfg = null) => new()
        {
            InstanceId = "DUO1",
            ChannelNumbers = new[] { 1, 2 },
            Config = cfg ?? new ParameterSet(),          // 主机缺省 = 上位机 PID、反向、加热棒负
            Simulated = false,
            TimeScale = 1,
            Clock = () => DateTimeOffset.Now,
            Log = (l, t) => { lock (Logs) Logs.Add($"{l} {t}"); }
        };

        public static ParameterSet Conn() => ParameterSet.Of((Rd105TecDriver.FieldPeriod, 200d), (DualStationDriver.Fields.Tick, 200d));
    }

    [Fact]
    public async Task 主机缺省上位机PID_升温切加热棒_占空比负_降温回TEC_占空比正()
    {
        var b = new Duo();
        await using var s = await b.Drv.OpenAsync(Duo.Conn(), b.Ctx(), CancellationToken.None);
        await s.StartAsync(CancellationToken.None);
        var t = Temp(s, 0);
        await WaitUntil(() => !double.IsNaN(t.CurrentJacket), 3000, "夹套读数");
        Assert.True(St(t).HostLoop);                       // 组合会话那一层把「上位机回路」转发上来（面板「尽快」的说明按它换）

        // 升温：加热棒继电器（DO0）合、TEC 功率线（DO6）断，占空比负（加热棒吃负）
        await t.SetTargetAsync(new TempTarget(40, TempChannelKind.Jacket), CancellationToken.None);
        Assert.True(b.Io.Coils[0]);
        Assert.False(b.Io.Coils[6]);
        Assert.Equal(3, b.Rd.Get(1, "MODE"));
        await WaitUntil(() => b.Rd.Get(1, "PWMDUTY") < 0, 3000, "加热棒负占空比");
        await WaitUntil(() => t.CurrentJacket > 27, 10000, "夹套往 40 走");
        Assert.Contains(b.Logs, l => l.Contains("已切至电加热"));

        // 降温：目标压到夹套之下 → 切 TEC（DO6 合、DO0 断），反向 → 要制冷写正
        await t.SetTargetAsync(new TempTarget(10, TempChannelKind.Jacket), CancellationToken.None);
        await WaitUntil(() => b.Io.Coils[6] && !b.Io.Coils[0], 5000, "切回 TEC");
        await WaitUntil(() => b.Rd.Get(1, "PWMDUTY") > 0, 4000, "TEC 制冷正占空比");
    }

    [Fact]
    public async Task 釜内串级升温_夹套越过釜内设定加死区_不切TEC()
    {
        // 串级升温：外环把夹套设定顶到 釜内设定 + 偏置上限（30 + 8 = 38），夹套必然越过 30 + 死区 2。
        // 换挡判据要比的是夹套此刻在追的数（内环设定），不是釜内设定——比错了就把加热棒切成只制冷的 TEC
        var b = new Duo();
        await using var s = await b.Drv.OpenAsync(Duo.Conn(), b.Ctx(), CancellationToken.None);
        await s.StartAsync(CancellationToken.None);
        var t = Temp(s, 0);
        await WaitUntil(() => !double.IsNaN(t.CurrentJacket), 3000, "夹套读数");
        using var feed = new TrFeeder(s, 1, 20.0);
        await Task.Delay(100);

        await t.SetTargetAsync(new TempTarget(30, TempChannelKind.Reactor), CancellationToken.None);
        Assert.True(b.Io.Coils[0]);
        await WaitUntil(() => St(t).CascadeInnerSetpoint is { } i && i > 35, 6000, "内环设定抬到 35 以上");
        await WaitUntil(() => t.CurrentJacket > 33.5, 20000, () => $"夹套升过 33.5（{t.CurrentJacket:F1}，占空比 {b.Rd.Get(1, "PWMDUTY")}，DO0 {b.Io.Coils[0]} DO6 {b.Io.Coils[6]}）\n" + string.Join("\n", b.Logs.TakeLast(12)));
        await Task.Delay(1500);                            // 采集循环 200 ms 一拍，够它「想」换挡好几回
        Assert.True(b.Io.Coils[0], "加热棒继电器不该断：夹套还在追内环设定");
        Assert.False(b.Io.Coils[6], "不该切到 TEC");
        Assert.DoesNotContain(b.Logs, l => l.Contains("已切至TEC"));
        Assert.True(St(t).Active);
    }

    [Fact]
    public async Task 釜内串级_Tr丢了回路停控_组合会话把继电器断开()
    {
        var b = new Duo();
        await using var s = await b.Drv.OpenAsync(Duo.Conn(), b.Ctx(), CancellationToken.None);
        await s.StartAsync(CancellationToken.None);
        var t = Temp(s, 0);
        await WaitUntil(() => !double.IsNaN(t.CurrentJacket), 3000, "夹套读数");
        ((IExternalReactorTemp)s).FeedReactor(1, 20.0, Quality.Good);

        await t.SetTargetAsync(new TempTarget(40, TempChannelKind.Reactor), CancellationToken.None);
        Assert.True(b.Io.Coils[0]);
        await WaitUntil(() => b.Rd.Get(1, "PWMDUTY") < 0, 3000, "加热");

        ((IExternalReactorTemp)s).FeedReactor(1, 0, Quality.Bad);       // 探头断线：喂进来的是 NaN
        // 缺省宽限 30 s：先保持——加热棒继电器不断、回路不停、不换挡；单个 Bad 样本不再当拍停控
        await WaitUntil(() => St(t).Holding is not null, 3000, "进保持");
        await Task.Delay(1500);
        Assert.DoesNotContain(b.Logs, l => l.Contains("控温已被回路停下"));
        Assert.True(b.Io.Coils[0]);
        Assert.True(St(t).Active);
        Assert.Contains("夹套按 40.0 ℃ 保持", St(t).Holding);
        Assert.Contains(b.Logs, l => l.Contains("外环冻结"));

        // Tr 回来：接着串级，组合会话记一句「恢复」
        ((IExternalReactorTemp)s).FeedReactor(1, 20.0, Quality.Good);
        await WaitUntil(() => St(t).Holding is null, 3000, "出保持");
        Assert.True(St(t).Active);
        Assert.True(b.Io.Coils[0]);
        Assert.Contains(b.Logs, l => l.Contains("釜内 Tr 恢复（中断"));
    }

    [Fact]
    public async Task 釜内串级_Tr丢了超过宽限_停控_继电器断开_原因带宽限()
    {
        var b = new Duo();
        var conn = ParameterSet.Of((Rd105TecDriver.FieldPeriod, 200d), (DualStationDriver.Fields.Tick, 200d),
                                   (Rd105TecDriver.FieldTrGrace, 2d));
        await using var s = await b.Drv.OpenAsync(conn, b.Ctx(), CancellationToken.None);
        await s.StartAsync(CancellationToken.None);
        var t = Temp(s, 0);
        await WaitUntil(() => !double.IsNaN(t.CurrentJacket), 3000, "夹套读数");
        ((IExternalReactorTemp)s).FeedReactor(1, 20.0, Quality.Good);
        await t.SetTargetAsync(new TempTarget(40, TempChannelKind.Reactor), CancellationToken.None);
        await WaitUntil(() => b.Rd.Get(1, "PWMDUTY") < 0, 3000, "加热");

        // 保持期夹套设定钳到 40、夹套还没到 40：加热棒继续出力，但继电器不动
        ((IExternalReactorTemp)s).FeedReactor(1, 0, Quality.Bad);
        await WaitUntil(() => St(t).Holding is not null, 3000, "进保持");
        Assert.True(b.Io.Coils[0]);
        Assert.DoesNotContain(b.Logs, l => l.Contains("热源切换：电加热 →"));

        // 宽限 2 s 过了还没回来：停控，原因说清保持过、超过多少秒；继电器全断
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await WaitUntil(() => b.Logs.Any(l => l.Contains("控温已被回路停下")), 6000, "回路停控传到组合会话");
        Assert.True(sw.Elapsed.TotalSeconds < 4.5, $"停控太晚：{sw.Elapsed.TotalSeconds:F1} s");
        await WaitUntil(() => !b.Io.Coils[0] && !b.Io.Coils[6], 4000, "继电器断开");
        Assert.Equal(0, b.Rd.Get(1, "PWMDUTY"));
        Assert.False(St(t).Active);
        Assert.Null(St(t).Holding);
        Assert.Contains("丢失超过 2 s", St(t).LastStop);
        Assert.Contains("已按釜内设定 40.0 ℃ 保持", St(t).LastStop);
        Assert.Contains(b.Logs, l => l.Contains("上位机回路停控") && l.Contains("丢失超过 2 s"));
    }

    [Fact]
    public async Task 釜内串级_保持期不换挡_夹套高过釜内设定加死区也不切TEC()
    {
        var b = new Duo();
        var conn = ParameterSet.Of((Rd105TecDriver.FieldPeriod, 200d), (DualStationDriver.Fields.Tick, 200d),
                                   (Rd105TecDriver.FieldTrGrace, 4d));
        await using var s = await b.Drv.OpenAsync(conn, b.Ctx(), CancellationToken.None);
        await s.StartAsync(CancellationToken.None);
        var t = Temp(s, 0);
        await WaitUntil(() => !double.IsNaN(t.CurrentJacket), 3000, "夹套读数");
        using var feed = new TrFeeder(s, 1, 20.0);
        await Task.Delay(100);
        await t.SetTargetAsync(new TempTarget(30, TempChannelKind.Reactor), CancellationToken.None);
        await WaitUntil(() => t.CurrentJacket > 33.5, 20000, () => $"夹套升过 33.5（{t.CurrentJacket:F1}）");
        Assert.True(b.Io.Coils[0]);

        // 保持期：夹套设定钳到 30，夹套 33+ 高过 30 + 死区——按单环那套该切 TEC，保持期不许动
        feed.Dispose();
        ((IExternalReactorTemp)s).FeedReactor(1, 0, Quality.Bad);
        await WaitUntil(() => St(t).Holding is not null, 3000, "进保持");
        await Task.Delay(1500);
        Assert.True(b.Io.Coils[0]);
        Assert.False(b.Io.Coils[6]);
        Assert.DoesNotContain(b.Logs, l => l.Contains("已切至TEC"));
        Assert.Equal(0, b.Rd.Get(1, "PWMDUTY"));           // 夹套高过保持值：加热棒不出力，但继电器留着

        // 宽限 4 s 到期：停控、全断
        await WaitUntil(() => !b.Io.Coils[0] && !b.Io.Coils[6], 8000, "宽限到期全断");
        Assert.False(St(t).Active);
    }

    [Fact]
    public async Task 停控后再下发被拒_不留幽灵开_继电器不合()
    {
        var b = new Duo();
        await using var s = await b.Drv.OpenAsync(Duo.Conn(), b.Ctx(), CancellationToken.None);
        await s.StartAsync(CancellationToken.None);
        var t = Temp(s, 0);
        await WaitUntil(() => !double.IsNaN(t.CurrentJacket), 3000, "夹套读数");

        // 没有 Tr 就下发釜内目标：内层拒绝。原来 _wantEnabled 已经立起来了——采集循环按它合继电器、面板对账当它开着
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => t.SetTargetAsync(new TempTarget(40, TempChannelKind.Reactor), CancellationToken.None));
        Assert.Contains("釜内 Tr 没有读数", ex.Message);
        Assert.False(St(t).Active);
        await Task.Delay(1000);                            // 采集循环 200 ms 一拍，几拍之后继电器该是全断的
        Assert.False(b.Io.Coils[0]);
        Assert.False(b.Io.Coils[6]);
    }

    [Fact]
    public async Task 釜内模式Tr丢失_判到达不退回按夹套()
    {
        var b = new Duo();
        await using var s = await b.Drv.OpenAsync(Duo.Conn(), b.Ctx(), CancellationToken.None);
        await s.StartAsync(CancellationToken.None);
        var t = Temp(s, 0);
        await WaitUntil(() => !double.IsNaN(t.CurrentJacket), 3000, "夹套读数");
        ((IExternalReactorTemp)s).FeedReactor(1, 20.0, Quality.Good);
        await t.SetTargetAsync(new TempTarget(25, TempChannelKind.Reactor), CancellationToken.None);
        ((IExternalReactorTemp)s).FeedReactor(1, 0, Quality.Bad);       // Tr 没了；夹套 25 就在目标上
        Assert.True(Math.Abs(t.CurrentJacket - 25) < 0.5);
        Assert.False(await t.WaitReachedAsync(25, 0.5, TimeSpan.FromMilliseconds(600), CancellationToken.None));
        await t.StopAsync(CancellationToken.None);
        // 夹套模式没有 Tr 才退回按夹套判（单独当温控器用那种）
        await t.SetTargetAsync(new TempTarget(25, TempChannelKind.Jacket), CancellationToken.None);
        Assert.True(await t.WaitReachedAsync(25, 0.5, TimeSpan.FromMilliseconds(600), CancellationToken.None));
    }
}
