using Tec.Core;
using Tec.Core.Benches;
using Tec.Core.Catalog;
using Tec.Core.Data;
using Tec.Core.Execution;
using Tec.Core.Recipes;
using Tec.Core.Safety;
using Tec.Driver.Abi;
using Tec.DriverHost;
using Tec.Drivers.Simulator;

namespace Tec.App.Services;

/// <summary>
/// 组合根。把驱动目录、台面、通道、执行引擎、数据管线接起来。
/// ViewModel 只认识它，不认识任何驱动（§11）。
/// </summary>
public sealed class Workspace
{
    private readonly Dictionary<string, IDeviceSession> _sessions = new(StringComparer.Ordinal);
    private readonly List<Channel> _channels = new();
    private Timer? _safetyTimer;

    public Workspace()
    {
        Clock = new VirtualClock();
        Catalog = new CommandCatalog();
        Drivers = new DriverCatalog();
        Pipeline = new DataPipeline();
        Arbiter = new ResourceArbiter();
        OperatorGate = new UiOperatorGate();
        Builtins = new BuiltinCommandProvider(OperatorGate, (ch, text) => MarkRequested?.Invoke(ch, text));
        Engine = new RunEngine(Catalog, Builtins, Arbiter, Pipeline, Clock.Func);
        Bench = new Bench { Name = "四通道平行合成台面" };
    }

    public VirtualClock Clock { get; }
    public CommandCatalog Catalog { get; }
    public DriverCatalog Drivers { get; }
    public DataPipeline Pipeline { get; }
    public ResourceArbiter Arbiter { get; }
    public RunEngine Engine { get; }
    public Bench Bench { get; }
    public UiOperatorGate OperatorGate { get; }
    public ICommandProvider Builtins { get; }
    public IReadOnlyList<Channel> Channels => _channels;
    public List<Recipe> Library { get; } = new();

    public event Action<int, string>? MarkRequested;
    public event EventHandler? BenchChanged;

    /// <summary>仿真加速倍数。真实硬件上恒为 1。</summary>
    public double TimeScale
    {
        get => Clock.Rate;
        set
        {
            Clock.Rate = value;
            Engine.TimeScale = value;
        }
    }

    public void Boot()
    {
        // 内置仿真驱动 + drivers/ 目录里的第三方包
        Drivers.RegisterBuiltin(new Rd105ReactorDriver());
        Drivers.RegisterBuiltin(new DosingPumpDriver());
        Drivers.RegisterBuiltin(new PhProbeDriver());
        Drivers.RegisterBuiltin(new TurbidityProbeDriver());
        Drivers.Discover(Path.Combine(AppContext.BaseDirectory, "drivers"));
        Drivers.LoadAll();

        foreach (var pkg in Drivers.Packages)
            if (pkg.Driver is { } d) Catalog.Register(d.Commands);

        // 加料泵是共享资源：同一时刻只让一个通道用（§5.1 / §7.4）
        Arbiter.Declare("P1", 1);
        Arbiter.Declare("P2", 1);
        Engine.ResourceOf = DemoBench.ResourceOf;

        TimeScale = 60;                       // 演示用；接真机时改回 1
        DemoBench.Fill(Bench);
        Library.AddRange(DemoBench.Recipes());
        RebuildChannelsAsync().GetAwaiter().GetResult();

        _safetyTimer = new Timer(_ => Engine.Safety.Evaluate(), null,
                                 TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// 台面变了就重建通道：拖一台双通道反应器进来 → 产生 2 个 Channel；
    /// 把 pH 探头绑到 CH2 → CH2 的能力集合多出 IScalarSensor（§5）。
    /// </summary>
    public async Task RebuildChannelsAsync()
    {
        foreach (var s in _sessions.Values)
        {
            try { await s.DisposeAsync(); } catch { }
        }
        _sessions.Clear();
        _channels.Clear();
        Engine.Safety.Clear();

        // 1. 先按宿主设备开通道
        var number = 0;
        var hostWells = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        foreach (var dev in Bench.Devices)
        {
            var driver = Drivers.Driver(dev.DriverId);
            if (driver is null || driver.Info.ChannelsPerDevice <= 0) continue;
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

            // 宿主设备按自己的孔位开通道；探头/泵按绑定关系挂到别人的通道上
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
                TimeScale = TimeScale,
                Clock = Clock.Func,
                Log = (level, text) => Console.WriteLine($"[{level}] {text}")
            };

            IDeviceSession session;
            try
            {
                session = await driver.OpenAsync(dev.Connection, ctx, CancellationToken.None);
                await session.StartAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[error] {dev.InstanceId} 打开失败：{ex.Message}");
                continue;
            }

            _sessions[dev.InstanceId] = session;
            Engine.Ingest(session);

            for (var w = 0; w < chs.Count && w < Math.Max(1, session.WellCount); w++)
            {
                var ch = _channels.FirstOrDefault(c => c.Number == chs[w]);
                ch?.Attach(session, w, hostWells.ContainsKey(dev.InstanceId));
            }
        }

        // 3. 执行器 + 安全限值。缺省从设备 Limits 推导，操作人只能收紧（§7.5）
        foreach (var ch in _channels)
        {
            Engine.Attach(ch);
            if (ch.Capabilities.Get<ITemperatureControl>() is { } t)
                Engine.Safety.Add(SafetyMonitor.FromTemperature(ch.Number, t.Limits));
        }

        BenchChanged?.Invoke(this, EventArgs.Empty);
    }

    public IDeviceSession? Session(string instanceId)
        => _sessions.TryGetValue(instanceId, out var s) ? s : null;

    public Channel? ChannelOf(int number) => _channels.FirstOrDefault(c => c.Number == number);

    public void Shutdown()
    {
        _safetyTimer?.Dispose();
        Engine.AbortAll(null, "程序退出");
        foreach (var s in _sessions.Values)
        {
            try { s.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        }
        Engine.Dispose();
    }
}

/// <summary>
/// 提示操作人。Core 只定义 IOperatorGate，弹框归界面——
/// 现在的实现是"等界面按下确认"，没界面时按超时处理。
/// </summary>
public sealed class UiOperatorGate : IOperatorGate
{
    private readonly Dictionary<int, TaskCompletionSource<bool>> _waiting = new();
    private readonly object _gate = new();

    public event EventHandler? Changed;

    public IReadOnlyList<int> Pending
    {
        get { lock (_gate) return _waiting.Keys.ToList(); }
    }

    public string Message { get; private set; } = "";

    public Task<bool> ConfirmAsync(int channel, string message, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) _waiting[channel] = tcs;
        Message = message;
        Changed?.Invoke(this, EventArgs.Empty);

        if (timeout > TimeSpan.Zero)
            _ = Task.Delay(timeout, ct).ContinueWith(_ => Confirm(channel, false), TaskScheduler.Default);

        return tcs.Task;
    }

    public void Confirm(int channel, bool ok = true)
    {
        TaskCompletionSource<bool>? tcs;
        lock (_gate)
        {
            if (!_waiting.Remove(channel, out tcs)) return;
        }
        tcs.TrySetResult(ok);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool IsWaiting(int channel)
    {
        lock (_gate) return _waiting.ContainsKey(channel);
    }
}
