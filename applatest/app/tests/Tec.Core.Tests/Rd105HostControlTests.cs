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
        Assert.Contains(logs, l => l.Contains("上位机 PID") && l.Contains("增益表空"));

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
        await using var s = await drv.OpenAsync(Conn(), Ctx(HostCfg()), CancellationToken.None);
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

        // Tr 丢了：回路停这一路，Tripped 上来
        string? why = null;
        rd.TempOf(0).Tripped += r => why = r;
        rd.TempOf(0).FeedReactor(double.NaN);
        await WaitUntil(() => why is not null, 4000, "Tr 丢失停控");
        Assert.Contains("釜内 Tr 读数丢失", why);
        Assert.Equal(0, dev.Get(1, "PWMDUTY"));
        Assert.False(St(t).Active);
        Assert.Null(St(t).CascadeInnerSetpoint);          // 停了就没有「内环在追的数」
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
            Assert.Contains(logs, l => l.Contains("TC1 上位机 PID") && l.Contains("增益表 25 ℃"));
            Assert.Contains(logs, l => l.Contains("TC2 上位机 PID") && l.Contains("增益表空"));
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
        await WaitUntil(() => b.Logs.Any(l => l.Contains("控温已被回路停下")), 4000, "回路停控传到组合会话");
        await WaitUntil(() => !b.Io.Coils[0] && !b.Io.Coils[6], 4000, "继电器断开");
        Assert.Equal(0, b.Rd.Get(1, "PWMDUTY"));
        Assert.False(((ITemperatureStatus)t).Active);
    }
}
