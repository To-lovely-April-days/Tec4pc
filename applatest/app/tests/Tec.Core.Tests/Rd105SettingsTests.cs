using Tec.Driver.Abi;
using Tec.Drivers.DualStation;
using Tec.Drivers.Rd105;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 温控器参数面板（IDeviceSettings / Rd105Settings）：用户要的是「点顶栏那个图标就能看到、
/// 设置 TEC 的最大功率、两路实时电流、温控器温度、两路 PID、自整定」。
/// 盯：四组的声明、读回来的换算、只写改过的项、只读项拒写、超温 / 最大电流同步回台面配置、
/// 自整定动作走温控器自己的 AUTOPID、会话（单机 / 组合主机）都端出这个接口。
/// </summary>
public sealed class Rd105SettingsTests
{
    private static (FakeRd105Device Dev, Rd105Link Link, Rd105Settings S, ParameterSet Cfg, List<string> Logs) Rig()
    {
        var dev = new FakeRd105Device();
        dev.Open();
        // 一台典型出厂态：协议表里的缺省值
        foreach (var tc in new[] { 1, 2 })
        {
            dev.Set(tc, "LIMITED", 30); dev.Set(tc, "SETCURRENT", 50); dev.Set(tc, "SPEED", 0);
            dev.Set(tc, "OVERTEMPUP", 150_00000); dev.Set(tc, "OVERTEMPLOWER", -30_00000);
            dev.Set(tc, "KP", 3000); dev.Set(tc, "KI", 150); dev.Set(tc, "KD", 0);
            dev.Set(tc, "POWERMODE", 0); dev.Set(tc, "STARTUPDELAY", 3); dev.Set(tc, "FDEADV", 200); dev.Set(tc, "BDEADV", 0);
            dev.Set(tc, "ONSENSOR", 1); dev.Set(tc, "PIDPOL", 0); dev.Set(tc, "AUTOPID", 0);
            dev.Set(tc, "PWMDUTY", 200000); dev.Set(tc, "CURRENT", 3256);
        }
        dev.Set(2, "MODE", 2);
        dev.Set(null, "ADDRESS", 1); dev.Set(null, "BOUNDTABLEONE", 3); dev.Set(null, "BOUNDTABLETWO", 1);
        dev.Set(null, "OVERTVPT", 70); dev.Set(null, "OVERTTEMP", 1); dev.Set(null, "CONTMODE", 0); dev.Set(null, "FPWM", 2);
        var link = new Rd105Link(dev);
        var cfg = ParameterSet.Of((Rd105TecDriver.FieldOverUp, 150d), (Rd105TecDriver.FieldOverLow, -30d), (Rd105TecDriver.FieldMaxCurrent, 5d));
        var logs = new List<string>();
        var s = new Rd105Settings(link, cfg, (_, t) => logs.Add(t));
        return (dev, link, s, cfg, logs);
    }

    [Fact]
    public void 四组_实时状态只读且是live_通道组和温控器组可写_动作挂在通道组下()
    {
        var (_, _, s, _, _) = Rig();
        Assert.Equal(new[] { "st", "tc1", "tc2", "sys" }, s.Groups.Select(g => g.Id));
        var st = s.Groups[0];
        Assert.True(st.Live);
        Assert.All(st.Schema.Fields, f => Assert.True(f.ReadOnly));
        Assert.Contains(st.Schema.Fields, f => f.Key == "tc1.current" && f.Unit == "A");
        Assert.Contains(st.Schema.Fields, f => f.Key == "sys.temp");

        var tc1 = s.Groups[1];
        Assert.False(tc1.Live);
        Assert.Contains(tc1.Schema.Fields, f => f.Key == Rd105Settings.KLimited && f.Max == 90 && !f.ReadOnly);
        Assert.Contains(tc1.Schema.Fields, f => f.Key == Rd105Settings.KKp);

        var sys = s.Groups[3];
        Assert.True(sys.Schema.Find(Rd105Settings.KModel)!.ReadOnly);
        Assert.True(sys.Schema.Find(Rd105Settings.KAddress)!.ReadOnly);       // 地址、波特率只看不改：改了链路当场断
        Assert.False(sys.Schema.Find(Rd105Settings.KOverTvpt)!.ReadOnly);

        Assert.Equal(new[] { "tune1", "tunestop1", "tune2", "tunestop2" }, s.Actions.Select(a => a.Id));
        Assert.All(s.Actions.Where(a => a.Id.StartsWith("tune") && !a.Id.Contains("stop")), a => Assert.True(a.Confirm));
        Assert.Equal("tc2", s.Actions.First(a => a.Id == "tune2").Group);
    }

    [Fact]
    public async Task 读实时状态_温度目标输出电流使能自整定告警都是设备此刻的值()
    {
        var (dev, _, s, _, _) = Rig();
        dev.Set(1, "TCADJTEMP", 24_50000); dev.Set(2, "TCADJTEMP", 30_00000);
        dev.Set(1, "ENABLE", 1); dev.Set(2, "AUTOPID", 1);
        dev.Set(null, "ERRORCODE", 1 << 6);                                   // 通道 1 限流中

        var p = await s.ReadAsync(Rd105Settings.GroupStatus, CancellationToken.None);

        Assert.Equal(24.5, p.Num("tc1.temp"), 3);
        Assert.Equal(30.0, p.Num("tc2.temp"), 3);
        Assert.Equal(25.0, p.Num("tc1.target"), 3);                           // 假设备出厂 TG 25
        Assert.Equal(10.0, p.Num("tc1.duty"), 3);                             // PWMDUTY 200000 = 10 %
        Assert.Equal(3.256, p.Num("tc2.current"), 3);                         // CURRENT 3256 = 3.256 A
        Assert.Equal("开", p.Str("tc1.enable"));
        Assert.Equal("关", p.Str("tc2.enable"));
        Assert.StartsWith("进行中", p.Str("tc2.autopid"));
        Assert.Equal(24.0, p.Num(Rd105Settings.KSysTemp), 3);
        Assert.Contains("通道1 输出限流中", p.Str(Rd105Settings.KFault));
        Assert.Contains("ERRORCODE=64", p.Str(Rd105Settings.KFault));
    }

    [Fact]
    public async Task 读通道组_原始值换成工程量与下拉项()
    {
        var (_, _, s, _, _) = Rig();
        var p = await s.ReadAsync(Rd105Settings.GroupTc2, CancellationToken.None);

        Assert.Equal("只加热", p.Str(Rd105Settings.KMode));                    // MODE=2
        Assert.Equal("正向", p.Str(Rd105Settings.KPol));
        Assert.Equal(30, p.Num(Rd105Settings.KLimited));
        Assert.Equal(5.0, p.Num(Rd105Settings.KSetCurrent), 3);               // SETCURRENT 50 = 5.0 A
        Assert.Equal(150.0, p.Num(Rd105Settings.KOverUp), 3);
        Assert.Equal(-30.0, p.Num(Rd105Settings.KOverLow), 3);
        Assert.Equal(3000, p.Num(Rd105Settings.KKp));
        Assert.Equal(1.0, p.Num(Rd105Settings.KFdeadV), 3);                   // FDEADV 200 = 1 %
        Assert.Equal("传感器断线 / 短路时关输出", p.Str(Rd105Settings.KOnSensor));
        Assert.Equal("跟随断电前状态", p.Str(Rd105Settings.KPowerMode));

        var sys = await s.ReadAsync(Rd105Settings.GroupSys, CancellationToken.None);
        Assert.Equal("215L", sys.Str(Rd105Settings.KModel));
        Assert.Equal("v1.3.0", sys.Str(Rd105Settings.KFirmware));
        Assert.Equal("38400（表 3）", sys.Str(Rd105Settings.KBaudTtl));
        Assert.Equal("9600（表 1）", sys.Str(Rd105Settings.KBaud485));
        Assert.Equal("10 Hz", sys.Str(Rd105Settings.KFpwm));
        Assert.Equal("各通道独立", sys.Str(Rd105Settings.KContMode));
    }

    [Fact]
    public async Task 写通道组_只写给的键_换算回原始值_保护项同步回台面配置()
    {
        var (dev, _, s, cfg, logs) = Rig();
        var v = ParameterSet.Of(
            (Rd105Settings.KLimited, 60d),
            (Rd105Settings.KMode, "只制冷"),
            (Rd105Settings.KSetCurrent, 8.0),
            (Rd105Settings.KOverUp, 120d),
            (Rd105Settings.KKp, 4200d),
            (Rd105Settings.KFdeadV, 0.5));

        var notes = await s.WriteAsync(Rd105Settings.GroupTc1, v, CancellationToken.None);

        Assert.Equal(60, dev.Get(1, "LIMITED"));
        Assert.Equal(1, dev.Get(1, "MODE"));
        Assert.Equal(80, dev.Get(1, "SETCURRENT"));
        Assert.Equal(120_00000, dev.Get(1, "OVERTEMPUP"));
        Assert.Equal(4200, dev.Get(1, "KP"));
        Assert.Equal(100, dev.Get(1, "FDEADV"));
        // 没给的键一个都没动
        Assert.Equal(150, dev.Get(1, "KI"));
        Assert.Equal(-30_00000, dev.Get(1, "OVERTEMPLOWER"));
        Assert.Equal(30, dev.Get(2, "LIMITED"));
        Assert.DoesNotContain(dev.Commands, c => c.StartsWith("TC2:"));

        // 台面配置跟着走：下次「连接」ApplyProtection 写的就是这两个新值
        Assert.Equal(120d, cfg.Num(Rd105TecDriver.FieldOverUp));
        Assert.Equal(8.0, cfg.Num(Rd105TecDriver.FieldMaxCurrent));
        Assert.Equal(-30d, cfg.Num(Rd105TecDriver.FieldOverLow));

        Assert.Contains(notes, n => n.Contains("最大输出占空比 = 60 %"));
        Assert.Contains(notes, n => n.Contains("已同步到台面配置"));
        Assert.Contains(logs, l => l.Contains("KP = 4200"));
    }

    [Fact]
    public async Task 写_只读项拒写_实时状态组拒写_下拉外的值拒写()
    {
        var (dev, _, s, _, _) = Rig();
        var n1 = await s.WriteAsync(Rd105Settings.GroupSys,
            ParameterSet.Of((Rd105Settings.KAddress, 5d), (Rd105Settings.KOverTvpt, 80d)), CancellationToken.None);
        Assert.Contains(n1, n => n.Contains("485 地址：只读，没写"));
        Assert.Equal(1, dev.Get(null, "ADDRESS"));
        Assert.Equal(80, dev.Get(null, "OVERTVPT"));

        var n2 = await s.WriteAsync(Rd105Settings.GroupStatus, ParameterSet.Of(("tc1.temp", 1d)), CancellationToken.None);
        Assert.Single(n2);
        Assert.Contains("只读", n2[0]);

        var n3 = await s.WriteAsync(Rd105Settings.GroupTc1, ParameterSet.Of((Rd105Settings.KMode, "随便")), CancellationToken.None);
        Assert.Contains(n3, n => n.Contains("不在选项里"));
        Assert.Equal(0, dev.Get(1, "MODE"));
    }

    [Fact]
    public async Task 自整定_走温控器自己的AUTOPID_输出关着先拒绝_停止写回0()
    {
        var (dev, _, s, logs, _) = Rig();
        var refused = await s.RunAsync(Rd105Settings.ActTune1, CancellationToken.None);
        Assert.Contains("输出是关的", refused);
        Assert.Equal(0, dev.Get(1, "AUTOPID"));

        dev.Set(1, "ENABLE", 1);
        var started = await s.RunAsync(Rd105Settings.ActTune1, CancellationToken.None);
        Assert.Contains("已启动（AUTOPID=1）", started);
        Assert.Equal(1, dev.Get(1, "AUTOPID"));
        Assert.Equal(0, dev.Get(2, "AUTOPID"));

        var stopped = await s.RunAsync(Rd105Settings.ActTuneStop1, CancellationToken.None);
        Assert.Contains("AUTOPID=0", stopped);
        Assert.Equal(0, dev.Get(1, "AUTOPID"));
    }

    [Fact]
    public async Task 单机会话与组合主机会话都端出参数面板()
    {
        var dev = new FakeRd105Device();
        var drv = new Rd105TecDriver { LinkFactory = _ => new Rd105Link(dev) };
        var ctx = new DriverContext
        {
            InstanceId = "R1", ChannelNumbers = new[] { 1, 2 }, Config = new ParameterSet(),
            Simulated = false, TimeScale = 1, Clock = () => DateTimeOffset.Now, Log = (_, _) => { }
        };
        await using var single = await drv.OpenAsync(new ParameterSet(), ctx, CancellationToken.None);
        var st = Assert.IsAssignableFrom<IDeviceSettings>(single);
        Assert.Equal(4, st.Groups.Count);
        var p = await st.ReadAsync(Rd105Settings.GroupSys, CancellationToken.None);
        Assert.Equal("215L", p.Str(Rd105Settings.KModel));

        var duo = new DualStationDriver
        {
            LinksFactory = _ => new DuoLinks { Rd105 = new Rd105Link(new FakeRd105Device()) }
        };
        await using var host = await duo.OpenAsync(ParameterSet.Of((DualStationDriver.Fields.HasIo, "无")), ctx, CancellationToken.None);
        var hs = Assert.IsAssignableFrom<IDeviceSettings>(host);
        Assert.Equal("工位 A · TC1", hs.Groups[1].Title);
    }
}
