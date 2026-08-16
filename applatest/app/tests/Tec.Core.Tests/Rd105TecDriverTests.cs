using TecControl.Core.Protocol;
using Tec.Driver.Abi;
using Tec.Drivers.Rd105;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 真机驱动的回归。全程走假串口——不插硬件也要能验证协议这一层，
/// 否则每改一行都得抱着机器跑一趟。
/// </summary>
public sealed class Rd105TecDriverTests
{
    private static (Rd105TecDriver Driver, FakeRd105Device Device) Rig()
    {
        var device = new FakeRd105Device();
        var driver = new Rd105TecDriver { LinkFactory = _ => new Rd105Link(device) };
        return (driver, device);
    }

    private static ParameterSet Conn() =>
        ParameterSet.Of((Rd105TecDriver.FieldPort, "COM9"), (Rd105TecDriver.FieldBaud, 38400d));

    [Fact]
    public async Task 测试连接读得到型号与固件()
    {
        var (driver, device) = Rig();
        device.Model = "215L";

        var probe = await driver.ProbeAsync(Conn(), CancellationToken.None);

        Assert.True(probe.Success);
        Assert.Contains("215L", probe.Message);
        Assert.Equal("v1.3.0", probe.Firmware);      // FPV=130 → v1.3.0
        Assert.Equal(1, probe.DetectedChannels);   // 一台温控器 = 一个反应通道
    }

    [Fact]
    public async Task 对面不应答时测试连接失败而不是抛异常()
    {
        var (driver, device) = Rig();
        device.Mute = true;

        var probe = await driver.ProbeAsync(Conn(), CancellationToken.None);

        // 界面上「测试连接」按下去必须给一句话，不能把异常抛到 UI 线程上
        Assert.False(probe.Success);
        Assert.False(string.IsNullOrWhiteSpace(probe.Message));
    }

    [Fact]
    public async Task 串口打不开时把原话带出来()
    {
        var driver = new Rd105TecDriver
        {
            LinkFactory = _ => throw new IOException("COM9 已被占用")
        };

        var probe = await driver.ProbeAsync(Conn(), CancellationToken.None);

        Assert.False(probe.Success);
        Assert.Contains("COM9 已被占用", probe.Message);
    }

    [Fact]
    public async Task 测试连接之后把串口关掉不占着口子()
    {
        var (driver, device) = Rig();

        await driver.ProbeAsync(Conn(), CancellationToken.None);

        // 探测完不释放的话，紧接着真正打开设备就会「端口被占用」
        Assert.False(device.IsOpen);
    }

    [Fact]
    public void 真机驱动与仿真机认同一套温度指令()
    {
        var (driver, _) = Rig();
        var real = driver.Commands.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);

        // 拿仿真调好的配方插上真机必须能跑——两边的指令 Id 必须重合，
        // 各写各的「升温至」就等于两套工艺语言
        foreach (var d in CommandSpecs.Temperature)
            Assert.Contains(d.Id, real);
    }

    [Fact]
    public void 连接参数与配置参数都有缺省值()
    {
        var (driver, _) = Rig();

        var cn = new ParameterSet().FillDefaults(driver.ConnectionSchema);
        Assert.Equal("COM3", cn.Str(Rd105TecDriver.FieldPort));
        Assert.Equal(500, cn.Num(Rd105TecDriver.FieldPeriod));

        // 超温与限流是设备侧的硬保护，缺省值必须填齐，不能让操作人从空白开始猜
        var cfg = new ParameterSet().FillDefaults(driver.ConfigSchema);
        Assert.Equal(180, cfg.Num(Rd105TecDriver.FieldOverUp));
        Assert.Equal(-40, cfg.Num(Rd105TecDriver.FieldOverLow));
        Assert.True(cfg.Num(Rd105TecDriver.FieldMaxCurrent) > 0);
    }

    [Fact]
    public async Task 假设备能完整走一遍读写()
    {
        // 这条是给假设备本身把关的：它要是答得不对，上面几条测试就没有意义
        var device = new FakeRd105Device();
        using var link = new Rd105Link(device);
        link.Open();

        await link.Controller.SetTargetAsync(1, 55.5);
        await link.Controller.SetEnableAsync(1, true);
        var cfgTarget = await link.Controller.ReadChannelConfigAsync(1);
        Assert.Equal(55.5, cfgTarget.TargetC, 3);
        Assert.True(cfgTarget.Enabled);

        device.Set(1, "TCADJTEMP", 40_00000);
        device.Set(2, "TCADJTEMP", 12_50000);
        var snap = await link.Controller.ReadSnapshotAsync();
        Assert.Equal(40.0, snap.Temp1C, 3);
        Assert.Equal(12.5, snap.Temp2C, 3);
    }

    // ── 会话层 ──────────────────────────────────────────────────────

    private static DriverContext Ctx(int channel = 1, ParameterSet? config = null) => new()
    {
        InstanceId = "T1",
        ChannelNumbers = new[] { channel },
        Config = config ?? new ParameterSet(),
        Simulated = false,
        TimeScale = 1,
        Clock = () => DateTimeOffset.Now,
        Log = (_, _) => { }
    };

    [Fact]
    public async Task 一台温控器只开一个反应通道()
    {
        var (driver, _) = Rig();
        await using var session = await driver.OpenAsync(Conn(), Ctx(), CancellationToken.None);

        // TC1 与 TC2 是同一个反应通道的两个温度（釜内与夹套），不是两个反应通道
        Assert.Equal(1, driver.Info.ChannelsPerDevice);
        Assert.Equal(1, session.WellCount);
        Assert.Single(session.CapabilitiesOf(0));
        Assert.Empty(session.CapabilitiesOf(1));
    }

    [Fact]
    public async Task 打开设备时把超温与限流写进保护寄存器()
    {
        var (driver, device) = Rig();
        var cfg = ParameterSet.Of((Rd105TecDriver.FieldOverUp, 120d),
                                  (Rd105TecDriver.FieldOverLow, -20d),
                                  (Rd105TecDriver.FieldMaxCurrent, 8d));

        await using var _ = await driver.OpenAsync(Conn(), Ctx(config: cfg), CancellationToken.None);

        // 固件没有通信看门狗：断线那一刻上位机的软限值就不存在了，
        // 所以这两条必须落到设备自己的寄存器里
        Assert.Equal(120_00000, device.Get(1, "OVERTEMPUP"));
        Assert.Equal(-20_00000, device.Get(1, "OVERTEMPLOWER"));
        Assert.True(device.Get(1, "SETCURRENT") > 0);
    }

    [Fact]
    public async Task 限值取自设备侧的超温保护值()
    {
        var (driver, _) = Rig();
        var cfg = ParameterSet.Of((Rd105TecDriver.FieldOverUp, 120d), (Rd105TecDriver.FieldOverLow, -20d));
        await using var session = await driver.OpenAsync(Conn(), Ctx(config: cfg), CancellationToken.None);

        var temp = (ITemperatureControl)session.CapabilitiesOf(0)[0];
        Assert.Equal(-20, temp.Limits.Min);
        Assert.Equal(120, temp.Limits.Max);
        Assert.Equal(1, temp.Channel);
    }

    [Fact]
    public async Task 设定目标温度写到TG并开输出()
    {
        var (driver, device) = Rig();
        await using var session = await driver.OpenAsync(Conn(), Ctx(), CancellationToken.None);
        var temp = (ITemperatureControl)session.CapabilitiesOf(0)[0];

        await temp.SetTargetAsync(new TempTarget(60), CancellationToken.None);

        Assert.Equal(60_00000, device.Get(1, "TG"));
        Assert.Equal(1, device.Get(1, "ENABLE"));
        Assert.Equal(0, device.Get(1, "SPEED"));      // 直接阶跃，不带斜坡
    }

    [Fact]
    public async Task 变温速率按每分换算成每秒写进SPEED()
    {
        var (driver, device) = Rig();
        await using var session = await driver.OpenAsync(Conn(), Ctx(), CancellationToken.None);
        var temp = (ITemperatureControl)session.CapabilitiesOf(0)[0];

        // 配方里写 ℃/min，温控器的 SPEED 是 ℃/s——换算错了升温会快 60 倍
        await temp.RampAsync(5, 0.6, TempChannelKind.Reactor, CancellationToken.None);

        Assert.Equal(5_00000, device.Get(1, "TG"));
        Assert.Equal(TecControl.Core.Protocol.TecScale.SpeedToRaw(0.01), device.Get(1, "SPEED"));
    }

    [Fact]
    public async Task 目标温度超出设备保护范围直接拒绝()
    {
        var (driver, device) = Rig();
        var cfg = ParameterSet.Of((Rd105TecDriver.FieldOverUp, 120d), (Rd105TecDriver.FieldOverLow, -20d));
        await using var session = await driver.OpenAsync(Conn(), Ctx(config: cfg), CancellationToken.None);
        var temp = (ITemperatureControl)session.CapabilitiesOf(0)[0];

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => temp.SetTargetAsync(new TempTarget(150), CancellationToken.None));

        // 拒绝了就不该留下任何下发痕迹
        Assert.NotEqual(150_00000, device.Get(1, "TG"));
    }

    [Fact]
    public async Task 停止控温关掉输出()
    {
        var (driver, device) = Rig();
        await using var session = await driver.OpenAsync(Conn(), Ctx(), CancellationToken.None);
        var temp = (ITemperatureControl)session.CapabilitiesOf(0)[0];

        await temp.SetTargetAsync(new TempTarget(60), CancellationToken.None);
        await temp.StopAsync(CancellationToken.None);

        Assert.Equal(0, device.Get(1, "ENABLE"));
    }

    [Fact]
    public async Task 轮询把TC1TC2发成Tr与Tj并算出温差()
    {
        var (driver, device) = Rig();
        device.Set(1, "TCADJTEMP", 60_00000);      // TC1 = 釜内
        device.Set(2, "TCADJTEMP", 65_00000);      // TC2 = 夹套

        var cn = ParameterSet.Of((Rd105TecDriver.FieldPort, "COM9"),
                                 (Rd105TecDriver.FieldPeriod, 200d));
        await using var session = await driver.OpenAsync(cn, Ctx(), CancellationToken.None);

        var got = new Dictionary<string, double>(StringComparer.Ordinal);
        using var sub = session.Samples.Subscribe(new Collect(s => got[s.Tag] = s.Value));
        await session.StartAsync(CancellationToken.None);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && !got.ContainsKey("dT")) await Task.Delay(50);
        await session.StopAsync(CancellationToken.None);

        Assert.Equal(60, got["Tr"], 2);
        Assert.Equal(65, got["Tj"], 2);
        Assert.Equal(-5, got["dT"], 2);            // Tr − Tj，放热时为正
    }

    // ── 告警字 ──────────────────────────────────────────────────────

    /// <summary>
    /// 跑一段轮询，把采样收下来；断言在**还在跑的时候**做——
    /// StopAsync 会把状态收回 Connected，停完再断言就什么都看不出来了。
    /// </summary>
    private static async Task<List<Sample>> PollWhile(
        IDeviceSession session, Func<List<Sample>, bool> until, Action<List<Sample>>? assert = null)
    {
        var got = new List<Sample>();
        using var sub = session.Samples.Subscribe(new Collect(got.Add));
        await session.StartAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && !until(got)) await Task.Delay(30);
        assert?.Invoke(got);
        await session.StopAsync(CancellationToken.None);
        return got;
    }

    [Fact]
    public async Task 告警字发成一路采样安全层才盯得住()
    {
        var (driver, device) = Rig();
        device.Set(null, "ERRORCODE", (long)TecErrorCode.OverVoltage);

        var cn = ParameterSet.Of((Rd105TecDriver.FieldPort, "COM9"), (Rd105TecDriver.FieldPeriod, 200d));
        await using var session = await driver.OpenAsync(cn, Ctx(), CancellationToken.None);

        // 告警字要在 Tags 里声明，安全层才知道有这么一路可以求值
        Assert.Contains(session.Tags, t => t.Tag == "fault");

        var got = await PollWhile(session, g => g.Count(s => s.Tag == "fault") >= 2);
        var fault = got.Where(s => s.Tag == "fault").ToList();
        Assert.NotEmpty(fault);
        Assert.Equal((double)(ushort)TecErrorCode.OverVoltage, fault[^1].Value);
    }

    [Fact]
    public async Task 传感器越限时那一路温度发成Bad()
    {
        var (driver, device) = Rig();
        // 通道1 传感器越限：Tr 这一路的读数不能再当好数用
        device.Set(null, "ERRORCODE", (long)TecErrorCode.Ch1SensorOutOfRange);

        var cn = ParameterSet.Of((Rd105TecDriver.FieldPort, "COM9"), (Rd105TecDriver.FieldPeriod, 200d));
        await using var session = await driver.OpenAsync(cn, Ctx(), CancellationToken.None);

        var got = await PollWhile(session, g => g.Count(s => s.Tag == "Tr") >= 3);
        var tr = got.Where(s => s.Tag == "Tr").ToList();
        var tj = got.Where(s => s.Tag == "Tj").ToList();

        Assert.NotEmpty(tr);
        // 「读不到值当作正常」是最危险的失败模式：越限那一路必须是 Bad，
        // 安全层见 Bad 就触发；没越限的那一路照旧 Good，不要一起拖下水
        Assert.All(tr, s => Assert.Equal(Quality.Bad, s.Quality));
        Assert.All(tj, s => Assert.Equal(Quality.Good, s.Quality));
    }

    [Fact]
    public async Task 过温停输出把设备标成故障()
    {
        var (driver, device) = Rig();
        device.Set(null, "ERRORCODE", (long)TecErrorCode.OverTempShutdown);

        var cn = ParameterSet.Of((Rd105TecDriver.FieldPort, "COM9"), (Rd105TecDriver.FieldPeriod, 200d));
        await using var session = await driver.OpenAsync(cn, Ctx(), CancellationToken.None);

        await PollWhile(session, _ => session.State == DeviceState.Faulted,
                        _ => Assert.Equal(DeviceState.Faulted, session.State));
    }

    [Fact]
    public async Task 限流中不算故障还能接着跑()
    {
        var (driver, device) = Rig();
        // 「正在限流」是工况不是损坏——标成 Faulted 会把还能跑的实验掐掉
        device.Set(null, "ERRORCODE", (long)TecErrorCode.Ch1CurrentLimiting);

        var cn = ParameterSet.Of((Rd105TecDriver.FieldPort, "COM9"), (Rd105TecDriver.FieldPeriod, 200d));
        await using var session = await driver.OpenAsync(cn, Ctx(), CancellationToken.None);

        await PollWhile(session, g => g.Count(s => s.Tag == "fault") >= 2,
                        _ => Assert.NotEqual(DeviceState.Faulted, session.State));
    }

    [Fact]
    public async Task 告警解除后设备恢复正常()
    {
        var (driver, device) = Rig();
        device.Set(null, "ERRORCODE", (long)TecErrorCode.UnderVoltage);

        var cn = ParameterSet.Of((Rd105TecDriver.FieldPort, "COM9"), (Rd105TecDriver.FieldPeriod, 200d));
        await using var session = await driver.OpenAsync(cn, Ctx(), CancellationToken.None);

        using var sub = session.Samples.Subscribe(new Collect(_ => { }));
        await session.StartAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && session.State != DeviceState.Faulted) await Task.Delay(50);
        Assert.Equal(DeviceState.Faulted, session.State);

        // 电压恢复了就该自己回到可用，不能一直挂着故障等人手动清
        device.Set(null, "ERRORCODE", 0);
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && session.State == DeviceState.Faulted) await Task.Delay(50);

        Assert.Equal(DeviceState.Ready, session.State);   // 停之前断言：StopAsync 会收回 Connected
        await session.StopAsync(CancellationToken.None);
    }

    private sealed class Collect(Action<Sample> onNext) : IObserver<Sample>
    {
        public void OnNext(Sample value) => onNext(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
