using Tec.Core;
using Tec.Core.Benches;
using Tec.Core.Catalog;
using Tec.Core.Data;
using Tec.Core.Execution;
using Tec.Core.Persistence;
using Tec.Core.Records;
using Tec.Core.Safety;
using Tec.Driver.Abi;
using Tec.DriverHost;
using Tec.Hmi.Ui;

namespace Tec.Hmi.Runtime;

/// <summary>
/// 设备端 HMI 的精简工作台（IHmiHost 的设备端实现，0277 §C.8）。
///
/// 跟工作站的 Workspace 是同一套地基（八件套：时钟/驱动目录/指令目录/
/// 管道/仲裁/执行引擎/安全监控/归档），但台面是**固定**的：开机从
/// bench.json 读一次、开一次会话，就是这台设备的样子——没有台面页、
/// 没有登录、没有配方库/化合物库/SQLite。
///
/// bench.json 的格式就是工作站的台面文件（BenchDoc）：在工作站上把
/// 双工位主机 + 两支宇电探头摆好、填好各自的串口，另存台面，
/// 拷到设备的数据目录里就能用——不发明第二种台面格式。
/// </summary>
public sealed class HmiRuntime : IHmiHost, IAsyncDisposable
{
    private readonly Dictionary<string, IDeviceSession> _sessions = new(StringComparer.Ordinal);
    private readonly List<IDisposable> _trFeedSubs = new();
    private readonly List<Channel> _channels = new();
    private readonly Dictionary<string, string> _archived = new(StringComparer.Ordinal);
    private Timer? _safetyTimer;
    private Timer? _journal;
    private int _journalBusy;

    public HmiRuntime(string? dataDir = null)
    {
        DataDir = dataDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TecHmi");
        Directory.CreateDirectory(DataDir);

        Clock = new VirtualClock();
        Catalog = new CommandCatalog();
        Drivers = new DriverCatalog();
        Pipeline = new DataPipeline();
        Arbiter = new ResourceArbiter();
        // 设备面板没有登录，也没有「提示步骤」会走到的确认弹窗——面板序列的
        // 六种步骤里没有提示。挂自动闸门是给引擎一个必需件，不是替人点确认
        var builtins = new BuiltinCommandProvider(new AutoOperatorGate());
        Builtins = builtins;
        Engine = new RunEngine(Catalog, Builtins, Arbiter, Pipeline, Clock.Func);
        builtins.Safety = Engine.Safety;
        Bench = new Bench { Name = "设备台面" };
    }

    // ── IHmiHost ─────────────────────────────────────────────────────

    public VirtualClock Clock { get; }
    public RunEngine Engine { get; }
    public DataPipeline Pipeline { get; }
    public SystemLog Log { get; private set; } = null!;
    public RunArchive Archive { get; private set; } = null!;
    public DriverCatalog Drivers { get; }
    public Bench Bench { get; }

    /// <summary>设备面板没有登录——署名照实写「设备面板」，不冒充任何人。</summary>
    public string Operator => "设备面板";

    public string DataDir { get; }

    public Channel? ChannelOf(int number) => _channels.FirstOrDefault(c => c.Number == number);

    public bool BeginBatch()
    {
        var fresh = Engine.EnsureBatch(ExperimentName, Operator, Bench.Name);
        if (fresh)
            Log?.Write("批次", $"{Engine.Record.RunId} 开始 · {ExperimentName} · 台面 {Bench.Name}", Operator);
        return fresh;
    }

    // ── 其余公开件 ───────────────────────────────────────────────────

    public CommandCatalog Catalog { get; }
    public ResourceArbiter Arbiter { get; }
    public ICommandProvider Builtins { get; }
    public IReadOnlyList<Channel> Channels => _channels;
    public string ExperimentName { get; set; } = "设备运行";

    /// <summary>台面文件：数据目录下的 bench.json（工作站的 BenchDoc 格式）。</summary>
    public string BenchPath => Path.Combine(DataDir, "bench.json");

    /// <summary>开机没有台面文件时写一份缺省的，给现场照着改。true = 这次是新写的。</summary>
    public bool SeededDefaultBench { get; private set; }

    /// <summary>上一次开机中断、这次开机被收尾的那几炉（同工作站的恢复语义）。</summary>
    public IReadOnlyList<InterruptedRun> Interrupted { get; private set; }
        = Array.Empty<InterruptedRun>();

    /// <summary>
    /// 开机第一步：注册驱动、读台面、建归档/日志。
    /// 时标恒为 1（真机）——仿真台面演示要加速的话由宿主可执行提供入口。
    /// </summary>
    public void Boot(double timeScale = 1)
    {
        // 真机三驱动：双工位主机 + 两支宇电探头（需求 docs/双工位反应主机驱动需求.md）
        Drivers.RegisterBuiltin(new Tec.Drivers.DualStation.DualStationDriver());
        Drivers.RegisterBuiltin(new Tec.Drivers.DualStation.YudianTrProbeDriver());
        Drivers.RegisterBuiltin(new Tec.Drivers.DualStation.YudianPhProbeDriver());
        // 仿真孪生也注册：没接硬件的机器（演示、验收、开发）用同一份 bench.json
        // 换个 DriverId 就能跑全套界面——界面上的数据照实带 Simulated 标
        Drivers.RegisterBuiltin(new Tec.Drivers.Simulator.Rd105ReactorDriver());
        Drivers.RegisterBuiltin(new Tec.Drivers.Simulator.TrProbeDriver());
        Drivers.RegisterBuiltin(new Tec.Drivers.Simulator.PhProbeDriver());
        // 第三方驱动包照工作站的规矩从 drivers/ 目录进
        Drivers.Discover(Path.Combine(AppContext.BaseDirectory, "drivers"));
        Drivers.LoadAll();
        foreach (var pkg in Drivers.Packages)
            if (pkg.Driver is { } d) Catalog.Register(d.Commands);

        Clock.Rate = timeScale <= 0 ? 1 : timeScale;
        Engine.TimeScale = Clock.Rate;

        var ver = typeof(HmiRuntime).Assembly.GetName().Version?.ToString() ?? "";
        Archive = new RunArchive(Path.Combine(DataDir, "Runs"), ver);
        Log = new SystemLog(Path.Combine(DataDir, "Logs"), Clock.Func);
        Engine.ReserveRunIds(Archive.KnownIds());
        Log.Write("程序", $"HMI 启动 v{ver} · 数据目录 {DataDir}", Operator);

        LoadBench();

        // 断电/崩溃恢复同工作站：没善终的炉按最后一次快照收尾、照实记档
        Interrupted = Archive.RepairInterrupted();
        foreach (var ir in Interrupted)
            Log.Write("恢复", $"{ir.RunId}「{ir.Name}」上次开机中断（"
                + string.Join("、", ir.Channels.Select(c => $"CH{c.Channel} 第 {c.DoneSteps + 1}/{c.TotalSteps} 步"))
                + "）——已按最后一次归档快照收尾", "系统", LogLevel.Warn);
    }

    private void LoadBench()
    {
        if (!File.Exists(BenchPath))
        {
            // 第一次开机：写一份缺省台面（真机三件，串口按驱动缺省），
            // 现场照着改串口就能用。写的是「还没配好」的实话，不是能直接跑的假象
            SeedDefaultBench();
            SeededDefaultBench = true;
            Log.Write("台面", $"没有找到台面文件，已写出缺省 {BenchPath}——请按现场串口修改", Operator, LogLevel.Warn);
        }
        try
        {
            TecFiles.LoadBench(BenchPath).ApplyTo(Bench);
            Log.Write("台面", $"台面「{Bench.Name}」已载入：{Bench.Devices.Count} 台设备、{Bench.Bindings.Count} 条绑定", Operator);
        }
        catch (Exception ex)
        {
            // 台面读不动就从空开始：面板开得起来、说得清为什么一台设备都没有，
            // 好过整个 HMI 起不来黑一块屏
            Log.Write("台面", $"台面文件读取失败：{ex.Message}——从空台面启动", Operator, LogLevel.Error);
        }
    }

    private void SeedDefaultBench()
    {
        var doc = new BenchDoc
        {
            Name = "双工位反应主机",
            Devices =
            {
                new DeviceDoc
                {
                    DriverId = Tec.Drivers.DualStation.DualStationDriver.DriverId,
                    InstanceId = "R1", Label = "双工位反应主机", Simulated = false,
                    // 连接参数留驱动缺省（COM 口现场改）；配置同理
                },
                new DeviceDoc
                {
                    DriverId = Tec.Drivers.DualStation.YudianTrProbeDriver.DriverId,
                    InstanceId = "TR1", Label = "宇电 Tr 探头（J7）", Simulated = false
                },
                new DeviceDoc
                {
                    DriverId = Tec.Drivers.DualStation.YudianPhProbeDriver.DriverId,
                    InstanceId = "PH1", Label = "宇电 pH 电极（J4）", Simulated = false
                },
            },
            Bindings =
            {
                new BindingDoc { DeviceId = "TR1", Channel = 1 },
                new BindingDoc { DeviceId = "TR1", Channel = 2, Port = 1 },
                new BindingDoc { DeviceId = "PH1", Channel = 1 },
                new BindingDoc { DeviceId = "PH1", Channel = 2, Port = 1 },
            }
        };
        TecFiles.SaveBench(BenchPath, doc);
    }

    /// <summary>
    /// 开机第二步：按台面开会话、建通道、牵 Tr 喂数、上安全限值、起定时器。
    /// 逻辑与工作站的 RebuildChannelsAsync 同一套语义，但只在开机跑一次——
    /// 设备的台面不在运行中改。
    /// </summary>
    public async Task StartAsync()
    {
        // 1. 宿主设备按孔位开通道
        var number = 0;
        var hostWells = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        foreach (var dev in Bench.Devices)
        {
            var driver = Drivers.Driver(dev.DriverId);
            if (driver is null)
            {
                Log.Write("台面", $"{dev.InstanceId} 的驱动 {dev.DriverId} 不存在——这台设备跳过", Operator, LogLevel.Error);
                continue;
            }
            if (driver.Info.ChannelsPerDevice <= 0) continue;
            var list = new List<int>();
            for (var w = 0; w < driver.Info.ChannelsPerDevice; w++)
            {
                number++;
                _channels.Add(new Channel(number, dev.InstanceId, w));
                list.Add(number);
            }
            hostWells[dev.InstanceId] = list;
        }

        // 2. 打开会话并挂能力
        foreach (var dev in Bench.Devices)
        {
            var driver = Drivers.Driver(dev.DriverId);
            if (driver is null) continue;
            var chs = hostWells.TryGetValue(dev.InstanceId, out var hosted)
                ? hosted
                : Bench.Bindings.Where(b => b.DeviceId == dev.InstanceId)
                                .Select(b => b.ChannelNumber).Distinct().OrderBy(x => x).ToList();
            if (chs.Count == 0) continue;

            var ctx = new DriverContext
            {
                InstanceId = dev.InstanceId,
                ChannelNumbers = chs,
                Config = dev.Config,
                Simulated = dev.Simulated,
                TimeScale = Clock.Rate,
                Clock = Clock.Func,
                Log = (level, text) => Log.Write("设备", $"{text}", Operator,
                    level is "error" ? LogLevel.Error : level is "warn" ? LogLevel.Warn : LogLevel.Info)
            };

            IDeviceSession session;
            try
            {
                session = await driver.OpenAsync(dev.Connection, ctx, CancellationToken.None).ConfigureAwait(false);
                await session.StartAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 打不开照实记着继续：面板上这一路显示未接，比整机起不来诚实
                Log.Write("设备", $"{dev.InstanceId} 打开失败：{ex.Message}", Operator, LogLevel.Error);
                continue;
            }

            _sessions[dev.InstanceId] = session;
            Engine.Ingest(session);
            for (var w = 0; w < chs.Count && w < Math.Max(1, session.WellCount); w++)
                ChannelOf(chs[w])?.Attach(session, w, hostWells.ContainsKey(dev.InstanceId));
        }

        // 2.5 跨会话喂釜温（同工作站 §2.5）：宇电探头发的 Tr 按通道号
        //     喂给实现 IExternalReactorTemp 的主机会话
        var trEaters = _sessions.Values.OfType<IExternalReactorTemp>().ToList();
        if (trEaters.Count > 0)
            foreach (var s in _sessions.Values)
            {
                if (s is IExternalReactorTemp) continue;
                _trFeedSubs.Add(s.Samples.Subscribe(new TrFeed(trEaters)));
            }

        // 3. 执行器 + 安全限值：缺省从设备 Limits 推导（§7.5），
        //    设备告警字非 0 走同一条安全通路
        foreach (var ch in _channels)
        {
            Engine.Attach(ch);
            if (ch.Capabilities.Get<ITemperatureControl>() is { } t)
                Engine.Safety.Add(SafetyMonitor.FromTemperature(ch.Number, t.Limits));
            if (_sessions.Values.Any(sess => sess.Tags.Any(tag => tag.Tag == "fault")) &&
                ch.Capabilities.Get<ITemperatureControl>() is not null)
                Engine.Safety.Add(new SafetyLimit(ch.Number, "fault", null, 0, null,
                                                  TimeSpan.FromSeconds(1), SafetyAction.AbortChannel)
                { Note = "设备告警字", FromDeviceLimits = true });
        }

        // 4. 1 s 安全求值 + 30 s 归档快照（断电恢复全指着这份快照活着，
        //    不加「跑着才写」的门——理由同工作站 Boot 里那段注释）
        _safetyTimer = new Timer(_ => Engine.Safety.Evaluate(), null,
                                 TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1));
        _journal = new Timer(_ =>
        {
            if (Interlocked.Exchange(ref _journalBusy, 1) == 1) return;
            try { SyncArchive(); }
            catch { /* 写归档失败 SyncArchive 自己记日志 */ }
            finally { Interlocked.Exchange(ref _journalBusy, 0); }
        }, null, 30_000, 30_000);

        Log.Write("程序", $"通道就绪：{_channels.Count} 条 · 会话 {_sessions.Count} 个", Operator);
    }

    /// <summary>把内存里跑过的批次写进归档（签名没变的不重写，同工作站）。</summary>
    public int SyncArchive()
    {
        if (Archive is null) return 0;
        var now = Clock.Now;
        var n = 0;
        foreach (var rec in Engine.Batches)
        {
            if (rec.Channels.Count == 0) continue;
            var sig = string.Join('|', rec.Channels.Select(c =>
                $"{c.Channel}:{c.State}:{c.Steps.Count}:{c.Events.Count}:{c.FinishedAt?.UtcTicks ?? 0}"));
            if (_archived.TryGetValue(rec.RunId, out var old) && old == sig) continue;
            try
            {
                Archive.Save(rec, Pipeline, now, truncated: false);
                _archived[rec.RunId] = sig;
                n++;
            }
            catch (Exception ex)
            {
                Log?.Write("归档", $"{rec.RunId} 写归档失败：{ex.Message}", Operator, LogLevel.Error);
            }
        }
        return n;
    }

    public async ValueTask DisposeAsync()
    {
        _safetyTimer?.Dispose();
        _journal?.Dispose();
        var n = SyncArchive();
        Log?.Write("程序", n > 0 ? $"退出，{n} 炉写入归档" : "退出", Operator);
        Engine.AbortAll(null, "程序退出");
        foreach (var d in _trFeedSubs) d.Dispose();
        foreach (var s in _sessions.Values)
        {
            try { await s.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        Engine.Dispose();
    }

    /// <summary>把别的会话发的 Tr 采样按通道转给吃外部釜温的宿主（同工作站的 TrFeed）。</summary>
    private sealed class TrFeed(IReadOnlyList<IExternalReactorTemp> eaters) : IObserver<Sample>
    {
        public void OnNext(Sample s)
        {
            if (s.Tag != "Tr") return;
            foreach (var e in eaters) e.FeedReactor(s.Channel, s.Value, s.Quality);
        }

        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
