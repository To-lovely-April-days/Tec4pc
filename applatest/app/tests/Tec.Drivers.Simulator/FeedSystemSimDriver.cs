using Tec.Driver.Abi;

namespace Tec.Drivers.Simulator;

/// <summary>
/// 【本地改动】两液一气进料系统的仿真孪生（2026-11-23，演示模式）。
///
/// WHY：现场接在工位加料口上的是霍桐的「两液一气」进料系统（一路 MFC 气体 + 两台计量泵），
/// 配方「加料」步骤里选的「加料泵 1 / 加料泵 2」就是它的泵 1 / 泵 2。早期仿真只有一台单泵，
/// 演示截图要跟现场一致：这里按真机的标签（flow / vol 镜像配方泵，p1flow / p1vol / p2flow / p2vol、
/// gasFlow）和图（feedsys）做一台孪生。两台泵各是一个 <see cref="DosingImpl"/>（送够就停的那套模型），
/// 加料 / pH 反馈加料的处理器按步骤里选的泵点名。
/// </summary>
public sealed class FeedSystemSimDriver : IDeviceDriver
{
    public const string DriverId = "sim.feed.htlab";
    public const string FieldPort = "串口";
    public const string Pump1 = "泵 1", Pump2 = "泵 2";

    public DriverInfo Info { get; } = new(DriverId, "两液一气进料系统", "霍桐仪器 HTLAB（仿真孪生）", "1.0.0")
    {
        ChannelsPerDevice = 0,
        SimulatorIncluded = true,
        IconKey = "feedsys",
        Description = "一路 MFC 气体 + 两台计量泵的仿真孪生。管子接到哪个工位的加料口，那一路的加料就是它的泵；配方「加料」步骤选泵 1 / 泵 2。",
        Capabilities = new[] { nameof(IDosing) }
    };

    public ParameterSchema ConnectionSchema { get; } = new(new[]
    {
        Field.Port(FieldPort, "串口", "COM15", "下拉里是这台机器当前检测到的串口；仿真不真开口子"),
        Field.Num("站号", "站号", 1, "", 1, 247, 1),
        Field.Sel("波特率", "波特率", new[] { "9600", "19200", "38400" }, "9600")
    })
    { Tip = "仿真孪生：连接参数只做记录，不占端口。" };

    public ParameterSchema ConfigSchema { get; } = new(new[]
    {
        Field.Sel("配方加料泵", "配方加料泵", new[] { Pump1, Pump2 }, Pump1)
            with { Tip = "HMI 加料卡、运行页「加料」、导出里的 flow / vol 列认的是这台泵" },
        Field.Num("泵最大流量", "泵最大流量", 50, "mL/min", 0.1, 1000, 0.1),
        Field.Num("单次最大体积", "单次最大体积", 500, "mL", 1, 100000, 1),
        Field.Text("泵 1 料液", "泵 1 料液", "料液 A"),
        Field.Text("泵 2 料液", "泵 2 料液", "料液 B"),
        Field.Num("气体流量", "MFC 气体流量", 0.2, "L/min", 0, 10, 0.05)
    });

    public IReadOnlyList<CommandDescriptor> Commands { get; } = CommandSpecs.Dosing;

    public async Task<ProbeResult> ProbeAsync(ParameterSet connection, CancellationToken ct)
    {
        await Task.Delay(100, ct).ConfigureAwait(false);
        return new ProbeResult(true, $"进料系统已响应（仿真，{connection.Str(FieldPort, "COM15")}）：MFC 0.00 L/min · 泵 1 停 · 泵 2 停")
        { Firmware = "HTLAB-SIM 1.0", Serial = DriverId + "-SIM", DetectedChannels = 1 };
    }

    public Task<IDeviceSession> OpenAsync(ParameterSet connection, DriverContext ctx, CancellationToken ct)
        => Task.FromResult<IDeviceSession>(new FeedSystemSimSession(ctx));
}

/// <summary>仿真里「一个通道上挂着两台泵」的点名口：加料 / pH 反馈加料处理器按步骤参数选泵。</summary>
internal interface ISimPumpSelect
{
    IDosing Pump(string pumpParam);
}

internal static class SimPumps
{
    /// <summary>步骤参数「pump」（加料泵 1 / 加料泵 2）点哪台就用哪台；不是双泵设备就原样返回。</summary>
    public static IDosing Pick(IDosing dosing, CommandInput p)
        => dosing is ISimPumpSelect sel ? sel.Pump(p.Str("pump", "加料泵 1")) : dosing;
}

internal sealed class FeedSystemSimSession : SimSession
{
    private readonly TwinPump[] _ports;
    private readonly double _gas;
    private double _gasTotal;

    public FeedSystemSimSession(DriverContext ctx) : base(ctx)
    {
        var chs = ctx.ChannelNumbers.Count > 0 ? ctx.ChannelNumbers : new[] { 0 };
        var cfg = ctx.Config;
        var recipePump = cfg.Str("配方加料泵", FeedSystemSimDriver.Pump1).Contains('2') ? 2 : 1;
        var maxRate = Math.Clamp(cfg.Num("泵最大流量", 50), 0.1, 1000);
        var maxVol = Math.Clamp(cfg.Num("单次最大体积", 500), 1, 100000);
        _gas = Math.Max(0, cfg.Num("气体流量", 0.2));
        _ports = chs.Select(c => new TwinPump(c, Emit, recipePump, maxRate, maxVol,
                                              cfg.Str("泵 1 料液", "料液 A"), cfg.Str("泵 2 料液", "料液 B"))).ToArray();
    }

    public override int WellCount => _ports.Length;

    public override IReadOnlyList<TagDescriptor> Tags { get; } = new[]
    {
        new TagDescriptor("flow", "加料流量", "mL/min", DataShape.Scalar) { Nominal = new ValueRange(0, 50), Period = TimeSpan.FromSeconds(1) },
        new TagDescriptor("vol", "累计加料", "mL", DataShape.Scalar) { Nominal = new ValueRange(0, 500), Period = TimeSpan.FromSeconds(1) },
        new TagDescriptor("p1flow", "泵 1 流量", "mL/min", DataShape.Scalar) { Period = TimeSpan.FromSeconds(1) },
        new TagDescriptor("p1vol", "泵 1 累计", "mL", DataShape.Scalar) { Period = TimeSpan.FromSeconds(1) },
        new TagDescriptor("p2flow", "泵 2 流量", "mL/min", DataShape.Scalar) { Period = TimeSpan.FromSeconds(1) },
        new TagDescriptor("p2vol", "泵 2 累计", "mL", DataShape.Scalar) { Period = TimeSpan.FromSeconds(1) },
        new TagDescriptor("gasFlow", "MFC 流量", "L/min", DataShape.Scalar) { Period = TimeSpan.FromSeconds(1) },
        new TagDescriptor("gasVol", "MFC 累计", "L", DataShape.Scalar) { Period = TimeSpan.FromSeconds(1) }
    };

    public override IReadOnlyList<ICapability> CapabilitiesOf(int well)
        => well >= 0 && well < _ports.Length ? new ICapability[] { _ports[well] } : Array.Empty<ICapability>();

    protected override void Tick(double dt)
    {
        _gasTotal += _gas * dt / 60.0;
        foreach (var p in _ports)
        {
            p.Tick(dt, Noise(0.01));
            Emit(p.Channel, "gasFlow", Math.Round(_gas + Noise(0.004), 3));
            Emit(p.Channel, "gasVol", Math.Round(_gasTotal, 3));
        }
    }

    private static readonly HandlerTable Table = new HandlerTable()
        .Add(CommandSpecs.Dose, () => new DoseHandler());

    public override ICommandHandler? Resolve(string commandId) => Table.Resolve(commandId);

    public override async ValueTask<IReadOnlyList<string>?> SafeStopAsync(int well, CancellationToken ct)
    {
        if (well < 0 || well >= _ports.Length) return Array.Empty<string>();
        var p = _ports[well];
        await p.StopAllAsync(ct).ConfigureAwait(false);
        return new[] { $"进料系统两台泵已停（泵 1 累计 {p.P1.TotalVolume:F2} mL · 泵 2 累计 {p.P2.TotalVolume:F2} mL）；MFC 气路照配置继续" };
    }
}

/// <summary>
/// 一个工位上的两台泵。对外（IDosing）是「配方加料泵」那一台——HMI 加料卡、累计加料校验、flow / vol 列认它；
/// 处理器按步骤里的「加料泵 1 / 2」通过 <see cref="ISimPumpSelect"/> 点名。
/// </summary>
internal sealed class TwinPump : IDosing, ISimPumpSelect
{
    private readonly int _recipe;

    public TwinPump(int channel, Action<int, string, double> emit, int recipePump, double maxRate, double maxVol,
                    string liquid1, string liquid2)
    {
        Channel = channel;
        _recipe = recipePump;
        var cal = new CalibrationRecord("pump", DateTimeOffset.Now.AddDays(-9), "工程师", "称重法", 1.0, DateTimeOffset.Now.AddDays(81));
        P1 = new DosingImpl(channel, emit, maxVol, cal, liquid1, "p1flow", "p1vol", maxRate);
        P2 = new DosingImpl(channel, emit, maxVol, cal, liquid2, "p2flow", "p2vol", maxRate);
        Mirror = new DosingImpl(channel, emit, maxVol, cal, liquid1, "flow", "vol", maxRate);
    }

    public int Channel { get; }
    public DosingImpl P1 { get; }
    public DosingImpl P2 { get; }
    /// <summary>配方泵的镜像标签（flow / vol）：不是第三台泵，只是把配方泵的两路再发一份真机那两个名字。</summary>
    private DosingImpl Mirror { get; }
    private DosingImpl Recipe => _recipe == 2 ? P2 : P1;

    public IDosing Pump(string pumpParam) => pumpParam.Contains('2') ? P2 : P1;

    public FlowLimits Limits => Recipe.Limits;
    public CalibrationRecord? Calibration => Recipe.Calibration;
    public double TotalVolume => Recipe.TotalVolume;
    public IObservable<Sample> Flow => Recipe.Flow;
    public IObservable<Sample> Total => Recipe.Total;
    public Task DoseAsync(DoseRequest request, CancellationToken ct) => Recipe.DoseAsync(request, ct);
    public Task SetRateAsync(double ratePerMin, CancellationToken ct) => Recipe.SetRateAsync(ratePerMin, ct);
    public Task StopAsync(CancellationToken ct) => Recipe.StopAsync(ct);

    public async Task StopAllAsync(CancellationToken ct)
    {
        await P1.StopAsync(ct).ConfigureAwait(false);
        await P2.StopAsync(ct).ConfigureAwait(false);
    }

    public void Tick(double dt, double noise)
    {
        P1.Tick(dt, noise);
        P2.Tick(dt, noise);
        Mirror.MirrorOf(Recipe);
    }
}
