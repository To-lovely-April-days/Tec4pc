using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Tec.Driver.Abi;
using Tec.Drivers.TouchPanel;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 触摸屏主机（网络）驱动的回归。对面是一个假的触摸屏：本机开一个 TCP 口，按一行一条 JSON 的协议答话，
/// 状态照 hmi-cpp 控制核心 state 命令的字段填。不连真的控制核心（那个另有端到端的手动核对）。
/// </summary>
public sealed class TouchPanelDriverTests
{
    /// <summary>假触摸屏：记下收到的每条命令；state 按当前这份状态答；其余命令默认答 ok，可以指定某条拒绝。</summary>
    private sealed class FakePanel : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        public readonly List<JsonObject> Received = new();
        public readonly JsonObject[] Ch = { NewChannel(24.5, 25.0, 4.62), NewChannel(25.3, 25.1, 6.98) };
        public string Offline = "";
        public int Alarms;
        public string? RefuseCmd, RefuseText;

        public FakePanel()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = Task.Run(AcceptAsync);
        }

        public int Port { get; }

        private static JsonObject NewChannel(double tr, double tj, double ph) => new()
        {
            ["running"] = false, ["mode"] = "off", ["sp"] = 0.0, ["tr"] = tr, ["tj"] = tj, ["tj_sp"] = 0.0, ["duty"] = 0.0, ["ph"] = ph,
            ["stir"] = new JsonObject { ["rpm"] = 0.0, ["set"] = 0.0 },
            ["dose"] = new JsonObject { ["active"] = false, ["pump"] = 0, ["dosed_ml"] = 0.0 },
            ["seq"] = new JsonObject { ["state"] = "idle" }
        };

        private async Task AcceptAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var tcp = await _listener.AcceptTcpClientAsync(_cts.Token);
                    _ = Task.Run(() => ServeAsync(tcp));
                }
            }
            catch (OperationCanceledException) { }
            catch (SocketException) { }
        }

        private async Task ServeAsync(TcpClient tcp)
        {
            using var _ = tcp;
            var stream = tcp.GetStream();
            using var reader = new StreamReader(stream, new UTF8Encoding(false));
            try
            {
                while (await reader.ReadLineAsync(_cts.Token) is { } line)
                {
                    var q = (JsonObject)JsonNode.Parse(line)!;
                    JsonObject r;
                    lock (Received)
                    {
                        Received.Add(q);
                        r = Answer(q);
                    }
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(r.ToJsonString() + "\n"), _cts.Token);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException) { }
        }

        private JsonObject Answer(JsonObject q)
        {
            var cmd = q["cmd"]!.GetValue<string>();
            if (cmd == RefuseCmd) return new JsonObject { ["ok"] = false, ["error"] = RefuseText };
            var c = q["ch"] is { } n ? Ch[n.GetValue<int>() - 1] : null;
            switch (cmd)
            {
                case "state":
                    return new JsonObject { ["ok"] = true, ["offline"] = Offline, ["alarms_unacked"] = Alarms, ["ch"] = new JsonArray(Ch[0].DeepClone(), Ch[1].DeepClone()) };
                case "sys.info": return new JsonObject { ["ok"] = true, ["version"] = "0.1.0", ["sim"] = true };
                case "limits.get": return new JsonObject { ["ok"] = true, ["limits"] = new JsonObject { ["tr_min"] = -20.0, ["tr_max"] = 150.0, ["rate_max_k_per_min"] = 2.0 } };
                case "alarm.list": return new JsonObject { ["ok"] = true, ["alarms"] = new JsonArray(new JsonObject { ["id"] = 1, ["ch"] = 1, ["title"] = "Tr 超过上限 150.0 ℃", ["acked"] = false }) };
                case "temp.start": c!["running"] = true; c["mode"] = q["mode"]!.GetValue<string>(); c["sp"] = q["target"]!.GetValue<double>(); break;
                case "temp.setpoint": c!["sp"] = q["target"]!.GetValue<double>(); break;
                case "temp.reflux": c!["running"] = true; c["mode"] = "reflux"; break;
                case "temp.stop": c!["running"] = false; c["mode"] = "off"; break;
                case "stir.set": c!["stir"]!["set"] = q["rpm"]!.GetValue<double>(); c["stir"]!["rpm"] = q["rpm"]!.GetValue<double>(); break;
                case "dose.start": c!["dose"]!["active"] = true; c["dose"]!["pump"] = q["dose"]!["pump"]!.GetValue<int>(); c["dose"]!["dosed_ml"] = 0.0; break;
                case "dose.stop": c!["dose"]!["active"] = false; break;
            }
            return new JsonObject { ["ok"] = true };
        }

        public List<JsonObject> Commands(string cmd) { lock (Received) return Received.Where(q => q["cmd"]!.GetValue<string>() == cmd).ToList(); }
        public void Edit(Action<FakePanel> change) { lock (Received) change(this); }

        private bool _disposed;
        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;          // 有的用例中途就把它关了（模拟断线），收尾时还会再关一次
            _disposed = true;
            _cts.Cancel();
            _listener.Stop();
            try { await _loop; } catch { }
            _cts.Dispose();
        }
    }

    private static ParameterSet Conn(int port)
    {
        var cn = new ParameterSet().FillDefaults(new TouchPanelDriver().ConnectionSchema);
        cn[TouchPanelDriver.Fields.Host] = "127.0.0.1";
        cn[TouchPanelDriver.Fields.Port] = (double)port;
        return cn;
    }

    private static DriverContext Ctx(List<(string, string)>? logs = null) => new()
    {
        InstanceId = "panel-1",
        ChannelNumbers = new[] { 3, 4 },        // 故意不是 1、2：采样和能力要带系统通道号，不是工位号
        Config = new ParameterSet(),
        Clock = () => DateTimeOffset.Now,
        Log = logs is null ? (_, _) => { } : (l, t) => { lock (logs) logs.Add((l, t)); }
    };

    private static async Task<(PanelSession, List<Sample>)> OpenAsync(FakePanel panel, List<(string, string)>? logs = null)
    {
        var session = (PanelSession)await new TouchPanelDriver().OpenAsync(Conn(panel.Port), Ctx(logs), CancellationToken.None);
        var samples = new List<Sample>();
        session.Samples.Subscribe(new Collect(samples));
        return (session, samples);
    }

    private sealed class Collect : IObserver<Sample>
    {
        private readonly List<Sample> _to;
        public Collect(List<Sample> to) => _to = to;
        public void OnNext(Sample value) { lock (_to) _to.Add(value); }
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    [Fact]
    public void 驱动声明_两个工位_只要地址和端口()
    {
        var drv = new TouchPanelDriver();
        Assert.Equal(2, drv.Info.ChannelsPerDevice);
        // 设备库里同一台主机两个版本，名字要一眼分得出（用户 2026-11-24 定的叫法）
        Assert.Equal("双工位反应主机（带触摸屏）", drv.Info.Name);
        Assert.Equal("双工位反应主机（不带触摸屏）", new LocalCoreDriver().Info.Name);
        Assert.Equal("双工位反应主机（旧版直连）", new Tec.Drivers.DualStation.DualStationDriver().Info.Name);
        var cn = new ParameterSet().FillDefaults(drv.ConnectionSchema);
        Assert.Equal(5020, cn.Int(TouchPanelDriver.Fields.Port));
        Assert.Equal(2, cn.Count);                                           // 没有任何串口参数：串口在触摸屏上配
        Assert.Contains(drv.Commands, c => c.Id == CommandSpecs.Control);
        Assert.Contains(drv.Commands, c => c.Id == CommandSpecs.Stir);
        Assert.Contains(drv.Commands, c => c.Id == CommandSpecs.Dose);
    }

    [Fact]
    public async Task 探测_连得上说版本_连不上说原因()
    {
        await using var panel = new FakePanel();
        var ok = await new TouchPanelDriver().ProbeAsync(Conn(panel.Port), CancellationToken.None);
        Assert.True(ok.Success);
        Assert.Equal("0.1.0", ok.Firmware);
        Assert.Contains("仿真", ok.Message);

        var port = panel.Port;
        await panel.DisposeAsync();
        var bad = await new TouchPanelDriver().ProbeAsync(Conn(port), CancellationToken.None);
        Assert.False(bad.Success);
        Assert.Contains("触摸屏", bad.Message);
    }

    [Fact]
    public async Task 轮询_读数按系统通道号推上来_限值取触摸屏的()
    {
        await using var panel = new FakePanel();
        var (s, samples) = await OpenAsync(panel);
        await using var _ = s;
        await s.PollOnceAsync(CancellationToken.None);

        lock (samples)
        {
            Assert.Contains(samples, x => x is { Channel: 3, Tag: "Tr", Value: 24.5, Quality: Quality.Good });
            Assert.Contains(samples, x => x is { Channel: 4, Tag: "Tj", Value: 25.1, Quality: Quality.Good });
            Assert.Contains(samples, x => x is { Channel: 4, Tag: "pH", Value: 6.98 });
            Assert.DoesNotContain(samples, x => x.Channel is 1 or 2);
        }
        var temp = s.CapabilitiesOf(0).OfType<ITemperatureControl>().Single();
        Assert.Equal(3, temp.Channel);
        Assert.Equal(24.5, temp.CurrentReactor);
        Assert.Equal(new TempLimits(-20, 150, 2), temp.Limits);
        Assert.Single(s.CapabilitiesOf(1).OfType<IStirrer>());
        Assert.Single(s.CapabilitiesOf(1).OfType<IDosing>());
        Assert.Single(s.CapabilitiesOf(1).OfType<IScalarSensor>());
    }

    [Fact]
    public async Task 控温_没在控就开_在控就只改目标_按速率走另一条()
    {
        await using var panel = new FakePanel();
        var (s, _) = await OpenAsync(panel);
        await using var __ = s;
        var temp = s.CapabilitiesOf(1).OfType<ITemperatureControl>().Single();

        await temp.SetTargetAsync(new TempTarget(40), CancellationToken.None);
        var start = Assert.Single(panel.Commands("temp.start"));
        Assert.Equal(2, start["ch"]!.GetValue<int>());                       // 工位 2：命令里是工位号，不是系统通道号
        Assert.Equal("reactor", start["mode"]!.GetValue<string>());
        Assert.Equal(40, start["target"]!.GetValue<double>());

        await s.PollOnceAsync(CancellationToken.None);
        await temp.SetTargetAsync(new TempTarget(45), CancellationToken.None);
        Assert.Single(panel.Commands("temp.start"));                         // 没有重开
        Assert.Equal(45, Assert.Single(panel.Commands("temp.setpoint"))["target"]!.GetValue<double>());
        Assert.Equal(45, ((ITemperatureStatus)temp).Setpoint);

        await temp.SetTargetAsync(new TempTarget(10, TempChannelKind.Jacket), CancellationToken.None);      // 换了控温方式：要重开
        Assert.Equal("jacket", panel.Commands("temp.start")[1]["mode"]!.GetValue<string>());

        await temp.RampAsync(60, 1.5, TempChannelKind.Reactor, CancellationToken.None);
        var ramp = panel.Commands("temp.start")[2];
        Assert.Equal("rate", ramp["arrive"]!.GetValue<string>());
        Assert.Equal(1.5, ramp["rate_k_per_min"]!.GetValue<double>());

        await temp.StopAsync(CancellationToken.None);
        Assert.Single(panel.Commands("temp.stop"));
        Assert.False(((ITemperatureStatus)temp).Active);
    }

    [Fact]
    public async Task 触摸屏不答应_原话抛上来()
    {
        await using var panel = new FakePanel { RefuseCmd = "temp.start", RefuseText = "这个通道正在跑序列，先结束序列再手动操作" };
        var (s, _) = await OpenAsync(panel);
        await using var __ = s;
        var temp = s.CapabilitiesOf(0).OfType<ITemperatureControl>().Single();
        var ex = await Assert.ThrowsAsync<PanelRefusedException>(() => temp.SetTargetAsync(new TempTarget(40), CancellationToken.None));
        Assert.Equal("这个通道正在跑序列，先结束序列再手动操作", ex.Message);
        Assert.False(((ITemperatureStatus)temp).Active);                     // 没答应就不能记成「在控」
    }

    [Fact]
    public async Task 断线_读数标成坏的_通了自己恢复()
    {
        var panel = new FakePanel();
        var logs = new List<(string, string)>();
        var (s, samples) = await OpenAsync(panel, logs);
        await using var _ = s;
        await s.StartAsync(CancellationToken.None);
        var port = panel.Port;
        await panel.DisposeAsync();

        lock (samples) samples.Clear();
        for (var i = 0; i < 3 && s.State != DeviceState.Faulted; i++) await s.PollOnceAsync(CancellationToken.None);
        Assert.Equal(DeviceState.Faulted, s.State);
        lock (samples) Assert.Contains(samples, x => x is { Channel: 3, Tag: "Tr", Quality: Quality.Bad } && double.IsNaN(x.Value));
        var temp = s.CapabilitiesOf(0).OfType<ITemperatureControl>().Single();
        Assert.True(double.IsNaN(temp.CurrentReactor));                      // 不能拿断线前的旧值去判到达
        Assert.Contains("断了", ((ITemperatureStatus)temp).Alarm);
        lock (logs) Assert.Contains(logs, l => l.Item1 == "error" && l.Item2.Contains("连接断了"));
        await s.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task 安全停机_停控温停加料_搅拌不动_发不出去要如实说()
    {
        await using var panel = new FakePanel();
        var (s, _) = await OpenAsync(panel);
        await using var __ = s;
        var notes = await s.SafeStopAsync(0, CancellationToken.None);
        Assert.NotNull(notes);
        Assert.Contains(notes!, n => n.Contains("停止控温"));
        Assert.Contains(notes!, n => n.Contains("搅拌保持"));
        Assert.Single(panel.Commands("temp.stop"));
        Assert.Single(panel.Commands("dose.stop"));
        Assert.Empty(panel.Commands("stir.set"));

        panel.Edit(p => { p.RefuseCmd = "temp.stop"; p.RefuseText = "触摸屏上关了「允许远程控制」，PC 只能看"; });
        notes = await s.SafeStopAsync(1, CancellationToken.None);
        Assert.Contains(notes!, n => n.Contains("没送到") && n.Contains("允许远程控制"));
    }

    [Fact]
    public async Task 搅拌和加料_命令对得上_加料量跨两次攒起来()
    {
        await using var panel = new FakePanel();
        var (s, _) = await OpenAsync(panel);
        await using var __ = s;
        var stir = s.CapabilitiesOf(0).OfType<IStirrer>().Single();
        await stir.SetSpeedAsync(300, CancellationToken.None);
        Assert.Equal(300, panel.Commands("stir.set")[0]["rpm"]!.GetValue<double>());
        await stir.StopAsync(CancellationToken.None);
        Assert.Equal(0, panel.Commands("stir.set")[1]["rpm"]!.GetValue<double>());

        var pump = s.CapabilitiesOf(0).OfType<IDosing>().Single();
        await pump.DoseAsync(new DoseRequest(5, 1.2) { Material = "乙醇" }, CancellationToken.None);
        var dose = (JsonObject)panel.Commands("dose.start")[0]["dose"]!;
        Assert.Equal(1, dose["pump"]!.GetValue<int>());
        Assert.Equal(5, dose["volume_ml"]!.GetValue<double>());
        Assert.Equal("乙醇", dose["liquid"]!.GetValue<string>());

        panel.Edit(p => p.Ch[0]["dose"]!["dosed_ml"] = 5.0);
        await s.PollOnceAsync(CancellationToken.None);
        Assert.Equal(5, pump.TotalVolume, 6);
        await pump.DoseAsync(new DoseRequest(3, 1), CancellationToken.None);       // 第二次：触摸屏的已加量从 0 重新数
        await s.PollOnceAsync(CancellationToken.None);
        panel.Edit(p => p.Ch[0]["dose"]!["dosed_ml"] = 3.0);
        await s.PollOnceAsync(CancellationToken.None);
        Assert.Equal(8, pump.TotalVolume, 6);
    }

    [Fact]
    public async Task 触摸屏的联锁和设备没连上_挂在控温状态上给人看()
    {
        await using var panel = new FakePanel { Alarms = 1 };
        var (s, samples) = await OpenAsync(panel);
        await using var _ = s;
        var st = (ITemperatureStatus)s.CapabilitiesOf(0).OfType<ITemperatureControl>().Single();
        Assert.Contains("Tr 超过上限", st.Alarm);
        Assert.Null(((ITemperatureStatus)s.CapabilitiesOf(1).OfType<ITemperatureControl>().Single()).Alarm);      // 是工位 1 的报警，工位 2 不挂

        panel.Edit(p => { p.Alarms = 0; p.Offline = "温控器或继电器模块没连上，不能控温"; });
        lock (samples) samples.Clear();
        await s.PollOnceAsync(CancellationToken.None);
        Assert.Contains("没连上", st.Alarm);
        lock (samples) Assert.Contains(samples, x => x is { Channel: 3, Tag: "Tr", Quality: Quality.Bad });       // 那时的温度是没读过的 0，不能当好数
    }

    /// <summary>
    /// 对着真的控制核心走一遍（不是假触摸屏）。平时不跑：要先在本机起一个仿真的核心
    /// （hmi-cpp/build/tec_core --sim --port N），再设环境变量 TEC_PANEL_PORT=N 才会真的去连。
    /// 用来核对「协议两头对得上」——假触摸屏是照着协议写的，写错了两边会一起错。
    /// </summary>
    [Fact]
    public async Task 真的控制核心_端到端()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("TEC_PANEL_PORT"), out var port)) return;
        var drv = new TouchPanelDriver();
        var probe = await drv.ProbeAsync(Conn(port), CancellationToken.None);
        Assert.True(probe.Success, probe.Message);
        await using var s = (PanelSession)await drv.OpenAsync(Conn(port), Ctx(), CancellationToken.None);
        var samples = new List<Sample>();
        s.Samples.Subscribe(new Collect(samples));
        await s.StartAsync(CancellationToken.None);

        var temp = s.CapabilitiesOf(0).OfType<ITemperatureControl>().Single();
        var status = (ITemperatureStatus)temp;
        Assert.False(double.IsNaN(temp.CurrentReactor));
        Assert.True(temp.Limits.Max > temp.Limits.Min);

        await temp.SetTargetAsync(new TempTarget(30), CancellationToken.None);
        await Task.Delay(1500);
        Assert.True(status.Active);
        Assert.Equal(30, status.Setpoint);
        Assert.NotNull(status.CascadeInnerSetpoint);                         // 釜内控温：触摸屏在给夹套定目标
        await temp.SetTargetAsync(new TempTarget(31), CancellationToken.None);
        await Task.Delay(1200);
        Assert.Equal(31, status.Setpoint);

        var stir = s.CapabilitiesOf(0).OfType<IStirrer>().Single();
        await stir.SetSpeedAsync(250, CancellationToken.None);
        await Task.Delay(2500);
        Assert.Equal(250, ((IStirrerStatus)stir).TargetRpm);
        Assert.True(stir.CurrentRpm > 100, $"实测转速 {stir.CurrentRpm}");

        var pump = s.CapabilitiesOf(0).OfType<IDosing>().Single();
        await pump.DoseAsync(new DoseRequest(0.5, 20), CancellationToken.None);
        await Task.Delay(4000);
        Assert.True(pump.TotalVolume > 0.3, $"已加 {pump.TotalVolume} mL");

        var notes = await s.SafeStopAsync(0, CancellationToken.None);
        Assert.Contains(notes!, n => n.Contains("已让触摸屏停止控温"));
        await Task.Delay(1200);
        Assert.False(status.Active);
        await stir.StopAsync(CancellationToken.None);
        lock (samples)
        {
            Assert.Contains(samples, x => x is { Channel: 3, Tag: "Tset", Value: 31 });
            Assert.Contains(samples, x => x is { Channel: 3, Tag: "duty" } && x.Value != 0);
            Assert.Contains(samples, x => x is { Channel: 4, Tag: "Tr", Quality: Quality.Good });
        }
        await s.StopAsync(CancellationToken.None);
    }

    // ── 不带触摸屏的机型：PC 自己跑控制核心（LocalCoreDriver）──

    [Fact]
    public async Task 不带触摸屏_本机核心已经在跑_直接连上不再启动()
    {
        await using var panel = new FakePanel();
        var old = LocalPrograms.DirOverride;
        LocalPrograms.DirOverride = Path.Combine(Path.GetTempPath(), "tec-no-such-dir-" + Guid.NewGuid().ToString("N"));      // 目录里什么程序都没有：真去启动就会报找不到
        try
        {
            var drv = new LocalCoreDriver();
            var cn = new ParameterSet { [LocalCoreDriver.Fields.Port] = panel.Port };
            var probe = await drv.ProbeAsync(cn, CancellationToken.None);
            Assert.True(probe.Success, probe.Message);
            Assert.Contains("本机控制核心", probe.Message);
            Assert.DoesNotContain("触摸屏", probe.Message);                   // 这台没有触摸屏，话里不能出现这三个字
            Assert.Equal(1, new ParameterSet().FillDefaults(drv.ConnectionSchema).Count);      // 只有一个端口，没有串口参数
        }
        finally { LocalPrograms.DirOverride = old; }
    }

    [Fact]
    public async Task 不带触摸屏_核心没在跑也没装程序_照实说缺什么()
    {
        var old = LocalPrograms.DirOverride;
        LocalPrograms.DirOverride = Path.Combine(Path.GetTempPath(), "tec-no-such-dir-" + Guid.NewGuid().ToString("N"));
        try
        {
            // 找一个现在没人听的端口
            var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
            var drv = new LocalCoreDriver();
            var probe = await drv.ProbeAsync(new ParameterSet { [LocalCoreDriver.Fields.Port] = port }, CancellationToken.None);
            Assert.False(probe.Success);
            Assert.Contains("没找到控制核心程序", probe.Message);
            Assert.Contains("没找到界面程序", LocalPrograms.StartHmi("127.0.0.1", port));
        }
        finally { LocalPrograms.DirOverride = old; }
    }

    [Fact]
    public void 手动控制面板_两种新机型开新界面_旧版直连还是老面板()
    {
        var panel = LocalPrograms.HmiEndpointOf(TouchPanelDriver.DriverId, new ParameterSet { [TouchPanelDriver.Fields.Host] = " 192.168.1.50 ", [TouchPanelDriver.Fields.Port] = 5021 });
        Assert.Equal(("192.168.1.50", 5021), panel);
        Assert.Equal(("127.0.0.1", 5020), LocalPrograms.HmiEndpointOf(LocalCoreDriver.DriverId, new ParameterSet()));
        Assert.Null(LocalPrograms.HmiEndpointOf(Tec.Drivers.DualStation.DualStationDriver.DriverId, new ParameterSet()));
    }

    /// <summary>
    /// 真的把控制核心启动起来连一遍。平时不跑：设环境变量 TEC_HMI_DIR 指到放着 tec_core 和 gain-table.csv 的目录才跑。
    /// 启动的那个控制核心不会自己退（设计如此），跑完要手动把它停掉。
    /// </summary>
    [Fact]
    public async Task 不带触摸屏_真的启动本机控制核心()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TEC_HMI_DIR"))) return;
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
        var drv = new LocalCoreDriver();
        var cn = new ParameterSet { [LocalCoreDriver.Fields.Port] = port };
        var probe = await drv.ProbeAsync(cn, CancellationToken.None);
        Assert.True(probe.Success, probe.Message);
        Assert.True(File.Exists(Path.Combine(LocalPrograms.CoreDataDir, "gain-table.csv")));
        var again = await drv.ProbeAsync(cn, CancellationToken.None);         // 第二次：已经在跑，直接连
        Assert.True(again.Success, again.Message);
    }
}
