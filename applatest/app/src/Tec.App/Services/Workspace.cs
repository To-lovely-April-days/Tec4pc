using System.Collections.ObjectModel;
using Avalonia.Threading;
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
    /// <summary>
    /// 配方库。**可观察集合**：配方页与配方库页各有一份自己的视图行，
    /// 谁改了另一边都得跟着变——以前是裸 List，配方页存进去一条，
    /// 切到配方库页看不见，得重开程序（那正是"保存了列表里没有"的原因）。
    /// </summary>
    public ObservableCollection<Recipe> Library { get; } = new();

    /// <summary>
    /// 当前操作人。存配方、开批次、写记录都署这个名。
    /// 现在只有一个固定值——登录还没做；先集中到一处，将来接上登录只改这一行。
    /// </summary>
    public string Operator { get; set; } = "管理员";

    /// <summary>打开 / 新建 / 保存实验。Boot() 里建好。</summary>
    public ExperimentStore Store { get; private set; } = null!;

    /// <summary>
    /// 每通道一条配方（原型 recipes = {1:…,2:…,3:…,4:[]}）。
    /// 配方视图编辑的就是它；运行视图启动某通道时用它的这一条。
    /// </summary>
    public Dictionary<int, Recipe> ChannelRecipes { get; } = new();

    /// <summary>每条泳道的配方名称（原型 laneName）。配方是空的，名字也就只能是「新配方」。</summary>
    public Dictionary<int, string> LaneNames { get; } = new();

    /// <summary>
    /// 当前实验名。存盘 / 读取做出来之前一直是「未命名实验」——
    /// 界面上不写死某个实验名，免得看着像已经打开了什么东西。
    /// </summary>
    public string ExperimentName { get; set; } = "未命名实验";

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
        Drivers.RegisterBuiltin(new RamanProbeDriver());
        Drivers.RegisterBuiltin(new InfraredProbeDriver());
        // 真机：RD105 协议的 TEC 温控器。和仿真反应器并列摆在设备库里，
        // 台面上想用哪个用哪个——同一套配方两边都能跑
        Drivers.RegisterBuiltin(new Tec.Drivers.Rd105.Rd105TecDriver());
        Drivers.Discover(Path.Combine(AppContext.BaseDirectory, "drivers"));
        Drivers.LoadAll();

        foreach (var pkg in Drivers.Packages)
            if (pkg.Driver is { } d) Catalog.Register(d.Commands);

        // 加料泵是共享资源：同一时刻只让一个通道用（§5.1 / §7.4）
        Arbiter.Declare("P1", 1);
        Arbiter.Declare("P2", 1);
        Engine.ResourceOf = DemoBench.ResourceOf;

        TimeScale = 60;                       // 演示用；接真机时改回 1

        // 台面从空开始：设备由用户从设备库拖进来。要示例台面调 LoadSample()。
        // 配方也从空开始——预置几条「降温结晶」看着像已经配好了，其实一步没有。
        Store = new ExperimentStore(this);

        // 配方库读盘；读不到就是空的。**不预置示例配方**——
        // 界面上出现的每一条都得是操作人自己存进去的，凭空多出六条会让人
        // 以为工艺已经配好了（§不伪造数据）。
        // 早期版本自动灌过六条演示配方，已经落盘的那些在这里一次性清掉
        Store.LoadLibrary(Library);
        if (SeedPurge.Apply(Library) > 0) Store.SaveLibrary();
    }

    /// <summary>
    /// 开机建通道。单独拎出来是因为它得 await——在界面线程上阻塞等驱动收尾，
    /// 会把界面线程和驱动互相锁死。窗口建好之后调它，界面靠 BenchChanged 自己刷新。
    /// </summary>
    public async Task StartAsync()
    {
        await RebuildChannelsAsync();
        Store.ResetDirty();            // 开机建通道会触发 BenchChanged，别一上来就标脏

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
            try { await s.DisposeAsync().ConfigureAwait(false); } catch { }
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
                session = await driver.OpenAsync(dev.Connection, ctx, CancellationToken.None)
                                      .ConfigureAwait(false);
                await session.StartAsync(CancellationToken.None).ConfigureAwait(false);
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

        // 3. 每个通道配一条空配方与一条泳道名。拖一台反应器进来就多两条泳道，
        //    内容由用户自己往里加——不预置步骤。
        foreach (var ch in _channels)
        {
            if (!ChannelRecipes.ContainsKey(ch.Number))
                ChannelRecipes[ch.Number] = new Recipe { Name = "新配方" };
            if (!LaneNames.ContainsKey(ch.Number)) LaneNames[ch.Number] = "新配方";
        }

        // 4. 执行器 + 安全限值。缺省从设备 Limits 推导，操作人只能收紧（§7.5）
        foreach (var ch in _channels)
        {
            Engine.Attach(ch);
            if (ch.Capabilities.Get<ITemperatureControl>() is { } t)
                Engine.Safety.Add(SafetyMonitor.FromTemperature(ch.Number, t.Limits));

            // 设备自己报的告警字：非 0 就是有事。真机驱动会把它发成一路采样，
            // 安全层照旧按越限求值——不必为「设备告警」另开一条通路。
            // 传感器越限那种驱动还会把温度发成 Bad，安全层见 Bad 也会触发。
            if (_sessions.Values.Any(sess => sess.Tags.Any(tag => tag.Tag == "fault")) &&
                ch.Capabilities.Get<ITemperatureControl>() is not null)
                Engine.Safety.Add(new SafetyLimit(ch.Number, "fault", null, 0, null,
                                                  TimeSpan.FromSeconds(1), SafetyAction.AbortChannel)
                { Note = "设备告警字", FromDeviceLimits = true });
        }

        // 上面一律 ConfigureAwait(false)，所以这里已经不在界面线程上了。
        // BenchChanged 会驱动一堆 ObservableCollection，必须回到界面线程再发；
        // 而且要等它发完这个方法才算结束——调用方紧接着就要抹脏标记，
        // 事件晚一步到的话刚打开的实验立刻又被标成「改过」。
        await RaiseBenchChangedAsync();
    }

    private Task RaiseBenchChangedAsync()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            BenchChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
        return Dispatcher.UIThread.InvokeAsync(() => BenchChanged?.Invoke(this, EventArgs.Empty)).GetTask();
    }

    /// <summary>载入示例台面（2 台反应器 + 2 套加料 + 探头），演示与自测用。</summary>
    public Task LoadSampleAsync()
    {
        Bench.Devices.Clear();
        Bench.Bindings.Clear();
        DemoBench.Fill(Bench);
        return RebuildChannelsAsync();
    }

    public IDeviceSession? Session(string instanceId)
        => _sessions.TryGetValue(instanceId, out var s) ? s : null;

    public Channel? ChannelOf(int number) => _channels.FirstOrDefault(c => c.Number == number);

    public void Shutdown()
    {
        _safetyTimer?.Dispose();
        Store?.SaveLibrary();          // 配方库改了就留在盘上，下次开机还在
        Store?.SaveRecent();
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
