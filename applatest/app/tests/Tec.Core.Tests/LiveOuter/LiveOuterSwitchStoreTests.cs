using System.Reflection;
using System.Text.Json;
using Tec.Core.Tests.Step1Pins;
using Tec.Driver.Abi;
using Tec.Drivers.DualStation;
using Tec.Drivers.Rd105;
using Tec.Drivers.Rd105.Modbus;
using TecControl.Core.Control;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 【本地改动】（2026-11-16，外环改「按实测实时算」第 1 步，主会话 D2 甲 / D11）开关的存取和 ABI：
/// · 外环「旧 / 新」每个工位一份，存在本机这一路的 pid.json（跟增益调度开关同一个文件，Rd105HostControl.ParamsPath）：缺这一项 = 旧，
///   「旧」不写出——旧文件读进来再存出去逐字节不变，选了新再选回旧也逐字节一样；老程序读新文件照样读得进（不认识的项跳过）；
///   【本地改动】（第 1 步复核）开关只动盘上这一项（不把本场内存里的手动值带进去），只为开关建的文件选回旧就删掉（开会话那句日志仍是「（缺省）」）；
///   存盘失败抛出去、选择和回路请求都不动；影子停了时空闲那行不许诺「下一次起用」；
/// · IPidTuningBench 的缺省成员：只有双工位主机（DuoSession）开放（Supported），单机 RD105 不开放——改选抛 NotSupportedException、
///   存着的「新」不推给回路、存盘时原样写回去；开会话把存的推给回路（只是请求，Rd105TemperatureControl.OuterAlgorithmRequested 看得见），改了立刻存、立刻推；
/// · ITemperatureStatus.Alarm：缺省 null；Rd105TemperatureControl 只在回路开着时给（同 Holding）；DuoTempControl 显式转发（漏了面板永远是 null）。
/// 每条用自己的增益表目录（Rd105HostControl.UseGainsDir），不碰用户目录。
/// </summary>
public sealed class LiveOuterSwitchStoreTests
{
    private static string NewDir() => Path.Combine(Path.GetTempPath(), "tec-lo-sw-" + Guid.NewGuid().ToString("N"));

    /// <summary>22b9ebe 上 Rd105HostPid.ParamsDoc 的样子（四项、同样的顺序）：旧程序存出来、读进去的就是它。</summary>
    private sealed class OldParamsDoc
    {
        public bool Scheduling { get; set; } = true;
        public double[]? Inner { get; set; }
        public double[]? Outer { get; set; }
        public double OuterMaxBiasC { get; set; }
    }

    /// <summary>22b9ebe 上 SaveParams 的写法（缩进、缺省编码），用来算「旧程序会存出来的那几个字节」。</summary>
    private static string OldText(OldParamsDoc d) => JsonSerializer.Serialize(d, new JsonSerializerOptions { WriteIndented = true });

    private sealed class Duo : IAsyncDisposable
    {
        public readonly List<string> Logs = new();
        public IDeviceSession S = null!;

        public static async Task<Duo> OpenAsync(string control = Rd105TecDriver.ControlHost)
        {
            var d = new Duo();
            var rd = new FakeRd105Device { Thermal = true, HeatSign = -1, GainCPerSec = 1.0 };
            rd.Set(1, "LIMITED", 90);
            rd.Set(2, "LIMITED", 90);
            var io = new FakeModbusSlave();
            var drv = new DualStationDriver
            {
                LinksFactory = _ => new DuoLinks { Rd105 = new Rd105Link(rd), IoPort = io, Io = new Io8rClient(new ModbusRtuClient(io, 1, 200)) }
            };
            var cfg = ParameterSet.Of((DualStationDriver.Fields.Chiller, "已开"), (Rd105TecDriver.FieldControl, control));
            cfg[DualStationDriver.Fields.WaterLead] = 0d;
            var ctx = new DriverContext
            {
                InstanceId = "DUO1", ChannelNumbers = new[] { 1, 2 }, Config = cfg, Simulated = false, TimeScale = 1,
                Clock = () => DateTimeOffset.Now,
                Log = (l, t) => { lock (d.Logs) d.Logs.Add($"{l} {t}"); }
            };
            d.S = await drv.OpenAsync(ParameterSet.Of((Rd105TecDriver.FieldPeriod, 200d), (DualStationDriver.Fields.Tick, 200d)), ctx, CancellationToken.None);
            return d;
        }

        public IPidTuningBench Bench(int well) => S.CapabilitiesOf(well).OfType<IPidTuningBench>().Single();
        public Rd105TemperatureControl Inner(int well) => ((DuoSession)S).InnerTemp(well);
        public string[] Snapshot() { lock (Logs) return Logs.ToArray(); }

        public async ValueTask DisposeAsync() => await S.DisposeAsync();
    }

    private static async Task<(IDeviceSession S, List<string> Logs)> OpenStandaloneAsync()
    {
        var dev = new FakeRd105Device { Thermal = true, GainCPerSec = 1.0 };
        dev.Set(1, "LIMITED", 90);
        dev.Set(2, "LIMITED", 90);
        var logs = new List<string>();
        var drv = new Rd105TecDriver { LinkFactory = _ => new Rd105Link(dev) };
        var s = await drv.OpenAsync(ParameterSet.Of((Rd105TecDriver.FieldPeriod, 200d)), new DriverContext
        {
            InstanceId = "R1", ChannelNumbers = new[] { 1, 2 },
            Config = ParameterSet.Of((Rd105TecDriver.FieldControl, Rd105TecDriver.ControlHost)),
            Simulated = false, TimeScale = 1, Clock = () => DateTimeOffset.Now,
            Log = (l, t) => { lock (logs) logs.Add($"{l} {t}"); }
        }, CancellationToken.None);
        return (s, logs);
    }

    private static string? StoredAlgo(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.TryGetProperty("OuterAlgo", out var v) ? v.GetString() : null;
    }

    [Fact]
    public async Task 外环开关_缺省旧_双工位主机开放_开会话不写文件_单机RD105不开放改选被拒_温控器PID没有整定台()
    {
        using var _ = Rd105HostControl.UseGainsDir(NewDir());
        await using (var d = await Duo.OpenAsync())
        {
            foreach (var w in new[] { 0, 1 })
            {
                var b = d.Bench(w);
                Assert.True(b.OuterAlgorithmSupported);
                Assert.False(b.OuterAlgorithmLive);
                Assert.Null(b.OuterAlgorithmLiveInUse);                   // 没在控
                Assert.Equal("外环：旧", b.OuterAlgorithmNote);
                Assert.False(d.Inner(w).OuterAlgorithmRequested);         // 推给回路的也是旧
                Assert.False(d.Inner(w).LiveOuterInControl);
            }
            Assert.False(File.Exists(Rd105HostControl.ParamsPath("DUO1", 1)));   // 开会话只读不写
            Assert.False(File.Exists(Rd105HostControl.ParamsPath("DUO1", 2)));
            Assert.DoesNotContain(d.Snapshot(), l => l.Contains("外环算法"));    // 旧的什么都不多说，开机日志跟以前一样
        }

        var (s, logs) = await OpenStandaloneAsync();
        await using (s)
        {
            var b = s.CapabilitiesOf(0).OfType<IPidTuningBench>().Single();
            Assert.False(b.OuterAlgorithmSupported);
            Assert.False(b.OuterAlgorithmLive);
            Assert.Null(b.OuterAlgorithmLiveInUse);
            Assert.Null(b.OuterAlgorithmNote);
            var ex = Assert.Throws<NotSupportedException>(() => b.SetOuterAlgorithm(true));
            Assert.Contains("只有双工位反应主机", ex.Message);
            Assert.False(File.Exists(Rd105HostControl.ParamsPath("R1", 1)));   // 被拒：什么都没动
            Assert.False(((Rd105Session)s).TempOf(0).OuterAlgorithmRequested);
            lock (logs) Assert.DoesNotContain(logs, l => l.Contains("外环算法"));
        }

        // 温控器 PID 方式：没有上位机回路，也就没有整定台、没有这个开关
        await using (var d = await Duo.OpenAsync(Rd105TecDriver.ControlDevice))
        {
            Assert.Empty(d.S.CapabilitiesOf(0).OfType<IPidTuningBench>());
            Assert.False(d.Inner(0).OuterAlgorithmRequested);
        }
    }

    [Fact]
    public async Task 外环开关_选新立刻存盘推给回路_两工位各一份_重开读回推给回路_选回旧不留这一项()
    {
        using var _ = Rd105HostControl.UseGainsDir(NewDir());
        var pathA = Rd105HostControl.ParamsPath("DUO1", 1);
        var pathB = Rd105HostControl.ParamsPath("DUO1", 2);
        await using (var d = await Duo.OpenAsync())
        {
            d.Bench(0).SetOuterAlgorithm(true);
            Assert.Equal("新", StoredAlgo(pathA));
            Assert.False(File.Exists(pathB));                               // B 没动过，不落文件
            Assert.True(d.Bench(0).OuterAlgorithmLive);
            Assert.False(d.Bench(1).OuterAlgorithmLive);
            Assert.True(d.Inner(0).OuterAlgorithmRequested);                // 立刻推给回路（只是请求）
            Assert.False(d.Inner(1).OuterAlgorithmRequested);
            Assert.False(d.Inner(0).LiveOuterInControl);                    // 没在控：锁定是开控时的事
            Assert.Equal("外环：已选 新（按实测实时算），下一次开温控（双向挡、控釜内）起用", d.Bench(0).OuterAlgorithmNote);
            Assert.Single(d.Snapshot(), l => l.Contains("TC1 外环算法选了「新」（按实测实时算），已存盘——下一次开温控（控釜内）起用"));
        }

        // 重开：A 读回新、开会话就推给回路，说一句；B 还是旧、不说
        await using (var d = await Duo.OpenAsync())
        {
            Assert.True(d.Bench(0).OuterAlgorithmLive);
            Assert.True(d.Inner(0).OuterAlgorithmRequested);
            Assert.False(d.Bench(1).OuterAlgorithmLive);
            Assert.False(d.Inner(1).OuterAlgorithmRequested);
            Assert.Single(d.Snapshot(), l => l.Contains("TC1 外环算法：上次选的是「新」"));
            Assert.DoesNotContain(d.Snapshot(), l => l.Contains("TC2 外环算法"));

            d.Bench(0).SetOuterAlgorithm(false);
            // 【本地改动】（第 1 步复核）「旧」不写出；这一份是只为开关建的（A 路从没存过手动参数 / 调度开关）——选回旧就删掉，回到「从没设过」
            Assert.False(File.Exists(pathA));
            Assert.False(d.Inner(0).OuterAlgorithmRequested);
            Assert.False(d.Bench(0).OuterAlgorithmLive);
        }
    }

    [Fact]
    public async Task 外环开关_旧文件读进来再存出去逐字节不变_选新再选回旧也一样_老程序读新文件照样读得进()
    {
        using var _ = Rd105HostControl.UseGainsDir(NewDir());
        var path = Rd105HostControl.ParamsPath("DUO1", 1);
        var old = new OldParamsDoc { Scheduling = false, Inner = new[] { 9, 0.03, 0 }, Outer = new[] { 2, 0.006, 0 }, OuterMaxBiasC = 6 };
        var oldText = OldText(old);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, oldText);
        var oldBytes = File.ReadAllBytes(path);
        Assert.DoesNotContain("OuterAlgo", oldText);

        await using var d = await Duo.OpenAsync();
        var b = d.Bench(0);
        Assert.False(b.Scheduling);                                         // 旧文件照读
        Assert.Equal(9, b.Manual.Inner.Kp);
        Assert.False(b.OuterAlgorithmLive);                                 // 缺这一项 = 旧
        Assert.False(d.Inner(0).OuterAlgorithmRequested);
        Assert.Equal(oldBytes, File.ReadAllBytes(path));                   // 开会话不写

        b.SetScheduling(false);                                             // 触发一次存盘（旧程序也会存）
        Assert.Equal(oldBytes, File.ReadAllBytes(path));                   // 跟旧程序存出来的逐字节一样

        b.SetOuterAlgorithm(true);
        Assert.NotEqual(oldBytes, File.ReadAllBytes(path));
        Assert.Equal("新", StoredAlgo(path));
        var readByOld = JsonSerializer.Deserialize<OldParamsDoc>(File.ReadAllText(path))!;   // 老程序读新文件：不认识的项跳过
        Assert.False(readByOld.Scheduling);
        Assert.Equal(old.Inner, readByOld.Inner);
        Assert.Equal(old.Outer, readByOld.Outer);
        Assert.Equal(6, readByOld.OuterMaxBiasC);

        b.SetOuterAlgorithm(false);
        Assert.Equal(oldBytes, File.ReadAllBytes(path));                   // 选回旧：又跟旧文件逐字节一样
    }

    [Fact]
    public async Task 外环开关_单机RD105读到存着的新_不推给回路_改选被拒_别的项存盘时原样写回去()
    {
        using var _ = Rd105HostControl.UseGainsDir(NewDir());
        var path = Rd105HostControl.ParamsPath("R1", 1);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { Scheduling = true, Inner = new[] { 8, 0.02, 0 }, Outer = new[] { 2.5, 0.0079, 0 }, OuterMaxBiasC = 8, OuterAlgo = "新" }));

        var (s, _) = await OpenStandaloneAsync();
        await using (s)
        {
            var b = s.CapabilitiesOf(0).OfType<IPidTuningBench>().Single();
            Assert.False(b.OuterAlgorithmSupported);
            Assert.False(b.OuterAlgorithmLive);                             // 没开放：不当自己的选择
            Assert.False(((Rd105Session)s).TempOf(0).OuterAlgorithmRequested);   // 不推
            var before = File.ReadAllBytes(path);
            Assert.Throws<NotSupportedException>(() => b.SetOuterAlgorithm(false));
            Assert.Equal(before, File.ReadAllBytes(path));
            b.SetScheduling(false);                                         // 单机这边存盘：双工位那边选的「新」不能被它抹掉
            Assert.Equal("新", StoredAlgo(path));
            Assert.False(((Rd105Session)s).TempOf(0).OuterAlgorithmRequested);
        }
    }

    // ── 【本地改动】（第 1 步复核）开关只动盘上的 OuterAlgo 这一项 ──────────────────────────
    // 复核查实：SetOuterAlgorithm 原来每次都按内存全量重写 pid.json——① 从没有文件的工位点一次「新」再点「旧」，留下一份文件，
    // 下次开会话那句日志从「（缺省）」变成「（上次存的）」；② 本场内存里的手动值（如回路扩程只在本场生效的偏置上限，HostControlLoop:3787）被带进文件，
    // 下一场读回来就不一样了。改成只给盘上那份加上 / 去掉这一项，只为开关建的文件选回旧就删掉。

    private static HostControlLoop LoopOf(Rd105TemperatureControl t)
        => (HostControlLoop)typeof(Rd105TemperatureControl).GetField("_loop", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(t)!;

    private static void SetShadow(HostControlLoop loop, int ch, LiveOuterShadow shadow)
    {
        var st = typeof(HostControlLoop).GetMethod("State", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(loop, new object[] { ch })!;
        st.GetType().GetField("Live")!.SetValue(st, shadow);
    }

    [Fact]
    public async Task 外环开关_从没有pid文件_同一场选新再选回旧_文件不留_重开那句日志仍是缺省_跟没碰过的那一路逐字相同_手动参数是出厂值()
    {
        using var _ = Rd105HostControl.UseGainsDir(NewDir());
        var pathA = Rd105HostControl.ParamsPath("DUO1", 1);
        await using (var d = await Duo.OpenAsync())
        {
            d.Bench(0).SetOuterAlgorithm(true);
            Assert.Equal("新", StoredAlgo(pathA));                         // 选新：只为开关建了一份
            d.Bench(0).SetOuterAlgorithm(false);
            Assert.False(File.Exists(pathA));                               // 选回旧：回到「从没设过」
            Assert.False(d.Bench(0).OuterAlgorithmLive);
            Assert.False(d.Inner(0).OuterAlgorithmRequested);
            Assert.Equal("外环：旧", d.Bench(0).OuterAlgorithmNote);
        }

        await using (var d = await Duo.OpenAsync())
        {
            var logs = d.Snapshot();
            var a = Assert.Single(logs, l => l.Contains(" TC1 上位机 PID："));
            var b = Assert.Single(logs, l => l.Contains(" TC2 上位机 PID："));
            Assert.Contains("（缺省）", a);
            Assert.DoesNotContain("（上次存的）", a);
            Assert.Equal(b, a.Replace(" TC1 上位机 PID：", " TC2 上位机 PID："));   // 跟没碰过的 B 路逐字相同（TC 号换一下）
            var m = d.Bench(0).Manual;
            Assert.Equal((Rd105HostControl.FallbackInner.Kp, Rd105HostControl.FallbackInner.Ki, Rd105HostControl.FallbackInner.Kd),
                         (m.Inner.Kp, m.Inner.Ki, m.Inner.Kd));
            Assert.Equal((Rd105HostControl.FallbackOuter.Kp, Rd105HostControl.FallbackOuter.Ki, Rd105HostControl.FallbackOuter.Kd),
                         (m.Outer.Kp, m.Outer.Ki, m.Outer.Kd));
            Assert.Equal(Rd105HostControl.FallbackOuterMaxBiasC, m.OuterMaxBiasC);
            Assert.True(d.Bench(0).Scheduling);
            Assert.False(d.Bench(0).OuterAlgorithmLive);
            Assert.DoesNotContain(logs, l => l.Contains("外环算法"));
        }
    }

    [Fact]
    public async Task 外环开关_跨场_选新关会话重开再选旧_只为开关建的那份文件删掉()
    {
        using var _ = Rd105HostControl.UseGainsDir(NewDir());
        var pathA = Rd105HostControl.ParamsPath("DUO1", 1);
        await using (var d = await Duo.OpenAsync())
            d.Bench(0).SetOuterAlgorithm(true);
        Assert.True(File.Exists(pathA));
        using (var doc = JsonDocument.Parse(File.ReadAllText(pathA)))
        {
            // 只为开关建的：手动参数那几项是空的（不把内存里的出厂值写进去），读回来只做「调度开」= 缺省
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("Inner").ValueKind);
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("Outer").ValueKind);
        }

        await using (var d = await Duo.OpenAsync())
        {
            Assert.True(d.Bench(0).OuterAlgorithmLive);
            Assert.True(d.Inner(0).OuterAlgorithmRequested);
            Assert.True(d.Bench(0).Scheduling);
            d.Bench(0).SetOuterAlgorithm(false);
            Assert.False(File.Exists(pathA));
            Assert.False(d.Inner(0).OuterAlgorithmRequested);
            Assert.False(d.Bench(0).OuterAlgorithmLive);
        }
    }

    [Fact]
    public async Task 外环开关_已有旧文件_本场内存里的手动偏置上限跟文件不一样_选新再选旧_文件逐字节等于原文件()
    {
        using var _ = Rd105HostControl.UseGainsDir(NewDir());
        var path = Rd105HostControl.ParamsPath("DUO1", 1);
        var old = new OldParamsDoc { Scheduling = true, Inner = new[] { 9, 0.03, 0 }, Outer = new[] { 2, 0.006, 0 }, OuterMaxBiasC = 6 };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, OldText(old));
        var oldBytes = File.ReadAllBytes(path);

        await using var d = await Duo.OpenAsync();
        var b = d.Bench(0);
        Assert.Equal(6, b.Manual.OuterMaxBiasC);
        // 模拟回路扩程（HostControlLoop:3787：表里那个温度点没有外环行时写 ManualOuterMaxBiasC，注释说「至少本次运行生效」）——本场内存跟盘上不一样
        LoopOf(d.Inner(0)).ConfigureCascade(1, 2, 0.006, 0, 11);
        Assert.Equal(11, b.Manual.OuterMaxBiasC);

        b.SetOuterAlgorithm(true);
        Assert.Equal("新", StoredAlgo(path));
        var mid = JsonSerializer.Deserialize<OldParamsDoc>(File.ReadAllText(path))!;
        Assert.Equal(6, mid.OuterMaxBiasC);                                 // 本场放宽的 11 没被带进文件
        Assert.Equal(old.Inner, mid.Inner);
        Assert.Equal(old.Outer, mid.Outer);

        b.SetOuterAlgorithm(false);
        Assert.Equal(oldBytes, File.ReadAllBytes(path));                   // 选回旧：逐字节回到原文件
        Assert.Equal(11, b.Manual.OuterMaxBiasC);                           // 内存里的本场值不动（那是回路的事）
    }

    [Fact]
    public async Task 外环开关_从没有文件_选新_关调度_选旧_文件留着_调度关_没有外环这一项()
    {
        using var _ = Rd105HostControl.UseGainsDir(NewDir());
        var path = Rd105HostControl.ParamsPath("DUO1", 1);
        await using var d = await Duo.OpenAsync();
        var b = d.Bench(0);
        b.SetOuterAlgorithm(true);
        b.SetScheduling(false);                                             // 原来那条全量存盘：照常带上 OuterAlgo
        Assert.Equal("新", StoredAlgo(path));
        b.SetOuterAlgorithm(false);
        Assert.True(File.Exists(path));                                     // 不是只为开关建的了：留着
        Assert.Null(StoredAlgo(path));
        Assert.DoesNotContain("OuterAlgo", File.ReadAllText(path));
        var doc = JsonSerializer.Deserialize<OldParamsDoc>(File.ReadAllText(path))!;
        Assert.False(doc.Scheduling);
        Assert.NotNull(doc.Inner);
    }

    // ── 【本地改动】（第 1 步复核）改选外环存盘失败：抛出去，选择和回路请求都不动（面板说「拒绝」是真的）──────────

    [Fact]
    public async Task 外环开关_存盘失败_从旧选新_抛出去_选择和回路请求都不动_放开以后照常能存()
    {
        using var _ = Rd105HostControl.UseGainsDir(NewDir());
        var path = Rd105HostControl.ParamsPath("DUO1", 1);
        await using var d = await Duo.OpenAsync();
        var b = d.Bench(0);
        Directory.CreateDirectory(path);                                    // pid.json 的位置被一个同名目录占着：写不进去（macOS / Windows 都抛）
        Assert.ThrowsAny<Exception>(() => b.SetOuterAlgorithm(true));
        Assert.False(b.OuterAlgorithmLive);
        Assert.False(d.Inner(0).OuterAlgorithmRequested);
        Assert.Equal("外环：旧", b.OuterAlgorithmNote);
        Assert.DoesNotContain(d.Snapshot(), l => l.Contains("外环算法选了"));
        Assert.True(Directory.Exists(path));
        Assert.False(File.Exists(path));

        Directory.Delete(path);
        b.SetOuterAlgorithm(true);                                          // 回滚不挡下一次能成的存盘
        Assert.Equal("新", StoredAlgo(path));
        Assert.True(b.OuterAlgorithmLive);
        Assert.True(d.Inner(0).OuterAlgorithmRequested);
    }

    [Fact]
    public async Task 外环开关_存盘失败_从新选旧_抛出去_仍是新_重读也还是新()
    {
        using var _ = Rd105HostControl.UseGainsDir(NewDir());
        var path = Rd105HostControl.ParamsPath("DUO1", 1);
        await using var d = await Duo.OpenAsync();
        var b = d.Bench(0);
        b.SetScheduling(true);                                              // 先有一份完整的（不是只为开关建的，选旧时不会删）
        b.SetOuterAlgorithm(true);
        var before = File.ReadAllBytes(path);
        File.SetAttributes(path, FileAttributes.ReadOnly);                  // 文件只读：读得出、写不进
        try
        {
            Assert.ThrowsAny<Exception>(() => b.SetOuterAlgorithm(false));
            Assert.True(b.OuterAlgorithmLive);
            Assert.True(d.Inner(0).OuterAlgorithmRequested);
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.DoesNotContain(d.Snapshot(), l => l.Contains("外环算法选了「旧」"));
            b.Reload();                                                     // PID 整定页「重读」：盘上是新，内存也是新，对得上
            Assert.True(b.OuterAlgorithmLive);
            Assert.True(d.Inner(0).OuterAlgorithmRequested);
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    // ── 【本地改动】（第 1 步复核）空闲那行别许诺一定锁不成的「下一次开温控起用」 ──────────────────────────

    [Fact]
    public async Task 外环开关_这一路新外环之前算出错停了_空闲那行说下一次开控仍按旧_不许诺起用_另一路不受影响()
    {
        using var _ = Rd105HostControl.UseGainsDir(NewDir());
        await using var d = await Duo.OpenAsync();
        var stopped = new LiveOuterShadow();
        stopped.Stop("测试：停着");
        SetShadow(LoopOf(d.Inner(0)), 1, stopped);                           // 影子停了要断开设备重连才换新的（HostControlLoop.ChannelState.Live）
        d.Bench(0).SetOuterAlgorithm(true);
        var note = d.Bench(0).OuterAlgorithmNote!;
        Assert.Contains("算出错停了", note);
        Assert.Contains("测试：停着", note);
        Assert.Contains("仍按旧外环", note);
        Assert.DoesNotContain("起用", note);
        foreach (var w in new[] { "夹套压低", "外环积分冻结在", "外环偏置自校正", "那一枪" })
            Assert.DoesNotContain(w, note);

        Assert.Equal("外环：旧", d.Bench(1).OuterAlgorithmNote);
        d.Bench(1).SetOuterAlgorithm(true);                                 // B 路影子没停：照常许诺
        Assert.Equal("外环：已选 新（按实测实时算），下一次开温控（双向挡、控釜内）起用", d.Bench(1).OuterAlgorithmNote);

        d.Bench(0).SetOuterAlgorithm(false);
        Assert.Equal("外环：旧", d.Bench(0).OuterAlgorithmNote);            // 旧模式那句原样
    }

    // ── ITemperatureStatus.Alarm ─────────────────────────────────────

    private sealed class BareStatus : ITemperatureStatus
    {
        public int Channel => 1;
        public double? Setpoint => 20;
        public bool Active => true;
    }

    private static void SetExotherm(Rd105TemperatureControl t, string? text)
        => (typeof(Rd105TemperatureControl).GetProperty(nameof(Rd105TemperatureControl.Exotherm))!.GetSetMethod(nonPublic: true)
            ?? throw new InvalidOperationException("Rd105TemperatureControl.Exotherm 的 setter 找不到")).Invoke(t, new object?[] { text });

    [Fact]
    public async Task 报警_接口缺省null_内层只在输出开着时给_双工位温控能力显式转发_停控就不挂着()
    {
        Assert.Null(((ITemperatureStatus)new BareStatus()).Alarm);         // 老实现不用改：缺省 null

        await using var b = await SessionLegacyGoldenBench.OpenAsync();
        var outer = (ITemperatureStatus)b.T[0];                             // DuoTempControl（面板拿到的那个）
        var inner = b.D.InnerTemp(0);
        Assert.Null(outer.Alarm);
        await b.Target(0, 30);
        Assert.True(inner.Enabled);
        const string text = "通道1放热报警：测试摆的一句";
        SetExotherm(inner, text);                                           // 真机上由 Rd105Session.OnCycle 按上升沿写（LiveOuterSessionTests 钉着）
        Assert.Equal(text, ((ITemperatureStatus)inner).Alarm);
        Assert.Equal(text, outer.Alarm);                                    // 显式转发：漏了这一行就是接口缺省的 null
        Assert.Null(((ITemperatureStatus)b.T[1]).Alarm);                   // 另一路不串
        await b.Stop(0);
        Assert.False(inner.Enabled);
        Assert.Equal(text, inner.Exotherm);
        Assert.Null(((ITemperatureStatus)inner).Alarm);                    // 停了控就不挂着（同 Holding）
        Assert.Null(outer.Alarm);
    }

    private static void SetReadingAlarm(Rd105TemperatureControl t, string? text)
        => (typeof(Rd105TemperatureControl).GetProperty(nameof(Rd105TemperatureControl.ReadingAlarm))!.GetSetMethod(nonPublic: true)
            ?? throw new InvalidOperationException("Rd105TemperatureControl.ReadingAlarm 的 setter 找不到")).Invoke(t, new object?[] { text });

    [Fact]
    public async Task 读数报警单独一份_接口缺省null_内层只在输出开着时给_双工位温控能力显式转发_整串照旧放热在前()
    {
        // 【本地改动】（2026-11-17，S1 复核查实）面板要分得出是哪一种报警：S1「读数没跟着夹套降」那一句单独一份（ITemperatureStatus.ReadingAlarm），Alarm 照旧是整串
        Assert.Null(((ITemperatureStatus)new BareStatus()).ReadingAlarm);  // 老实现不用改：缺省 null

        await using var b = await SessionLegacyGoldenBench.OpenAsync();
        var outer = (ITemperatureStatus)b.T[0];
        var inner = b.D.InnerTemp(0);
        Assert.Null(outer.ReadingAlarm);
        await b.Target(0, 30);
        const string read = "通道1釜内读数没跟着夹套降：测试摆的一句";
        const string exo = "通道1放热报警：测试摆的一句";
        SetReadingAlarm(inner, read);
        Assert.Equal(read, ((ITemperatureStatus)inner).ReadingAlarm);
        Assert.Equal(read, outer.ReadingAlarm);                             // 显式转发：漏了这一行就是接口缺省的 null
        Assert.Equal(read, outer.Alarm);
        SetExotherm(inner, exo);
        Assert.Equal(exo + "；" + read, outer.Alarm);                       // 整串照旧：放热在前
        Assert.Equal(read, outer.ReadingAlarm);
        Assert.Null(((ITemperatureStatus)b.T[1]).ReadingAlarm);            // 另一路不串
        await b.Stop(0);
        Assert.Equal(read, inner.ReadingAlarm);
        Assert.Null(((ITemperatureStatus)inner).ReadingAlarm);             // 停了控就不挂着（同 Alarm）
        Assert.Null(outer.ReadingAlarm);
    }
}
