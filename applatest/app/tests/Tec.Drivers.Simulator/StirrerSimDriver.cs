using Tec.Driver.Abi;

namespace Tec.Drivers.Simulator;

/// <summary>
/// 【本地改动】搅拌驱动器的仿真孪生（2026-11-23，演示模式）。
///
/// WHY：现场每个工位一台独立的搅拌电机驱动器（爱控 AQMD2408BLS-M），台面上是一只套在釜盖上的
/// 红色搅拌头，有自己的串口。早期仿真反应器把搅拌做在孔位里，台面上就少了这一台设备——
/// 演示截图要跟现场一致，所以补一台独立的仿真驱动器：IconKey 用真机那张图（stirdrv），
/// 串口字段名跟真机一字不差（「串口」，缺省 COM14 / COM11 跟工位走），能力只有 IStirrer，
/// 转速 / 扭矩 / 电流照 StirrerImpl 的斜坡模型发。配合反应器配置「自带搅拌 = 无」使用，
/// 同一路上只有一个搅拌源。
/// </summary>
public sealed class StirrerSimDriver : IDeviceDriver, IStationPortDefaults
{
    public const string DriverId = "sim.stir.aqmd";
    public const string FieldPort = "串口";

    public DriverInfo Info { get; } = new(DriverId, "搅拌驱动器 AQMD2408BLS-M", "爱控（仿真孪生）", "1.0.0")
    {
        ChannelsPerDevice = 0,
        SimulatorIncluded = true,
        IconKey = "stirdrv",
        Description = "无刷电机驱动器的仿真孪生：一个工位一台，套在釜盖上那一路就能设转速、看实测转速与电流。",
        Capabilities = new[] { nameof(IStirrer) }
    };

    public ParameterSchema ConnectionSchema { get; } = new(new[]
    {
        Field.Port(FieldPort, "串口", "COM14", "下拉里是这台机器当前检测到的串口；仿真不真开口子"),
        Field.Num("站号", "站号", 1, "", 1, 247, 1),
        Field.Sel("波特率", "波特率", new[] { "9600", "19200", "38400", "115200" }, "9600")
    })
    { Tip = "仿真孪生：连接参数只做记录，不占端口。" };

    public ParameterSchema ConfigSchema { get; } = new(new[]
    {
        Field.Num("转速上限", "转速上限", 1000, "rpm", 100, 3000, 10),
        Field.Num("电机极数", "电机极数", 4, "", 2, 24, 2),
        Field.Num("电机额定电流", "电机额定电流", 2.5, "A", 0.1, 20, 0.1)
    });

    public IReadOnlyList<CommandDescriptor> Commands { get; } = CommandSpecs.Stirring;

    public string PortFieldKey => FieldPort;
    public string? DefaultPortFor(int channelNumber) => channelNumber switch { 1 => "COM14", 2 => "COM11", _ => null };

    public async Task<ProbeResult> ProbeAsync(ParameterSet connection, CancellationToken ct)
    {
        await Task.Delay(90, ct).ConfigureAwait(false);
        return new ProbeResult(true, $"{connection.Str(FieldPort, "COM14")} 已响应（仿真）")
        { Firmware = "AQMD-SIM 1.0", Serial = DriverId + "-SIM", DetectedChannels = 1 };
    }

    public Task<IDeviceSession> OpenAsync(ParameterSet connection, DriverContext ctx, CancellationToken ct)
        => Task.FromResult<IDeviceSession>(new StirrerSimSession(ctx));
}

internal sealed class StirrerSimSession : SimSession
{
    private readonly StirrerImpl[] _ports;
    private readonly double _rated;

    public StirrerSimSession(DriverContext ctx) : base(ctx)
    {
        var chs = ctx.ChannelNumbers.Count > 0 ? ctx.ChannelNumbers : new[] { 0 };
        var max = Math.Clamp(ctx.Config.Num("转速上限", 1000), 100, 3000);
        _rated = Math.Max(0.1, ctx.Config.Num("电机额定电流", 2.5));
        _ports = chs.Select(c => new StirrerImpl(c, Emit, max)).ToArray();
    }

    public override int WellCount => _ports.Length;

    public override IReadOnlyList<TagDescriptor> Tags { get; } = new[]
    {
        new TagDescriptor("rpm", "搅拌转速", "rpm", DataShape.Scalar)
            { Nominal = new ValueRange(0, 1000), Period = TimeSpan.FromSeconds(1) },
        new TagDescriptor("torque", "搅拌扭矩", "mN·m", DataShape.Scalar)
            { Nominal = new ValueRange(0, 59), Period = TimeSpan.FromSeconds(1) },
        new TagDescriptor("stirI", "搅拌电流", "A", DataShape.Scalar)
            { Nominal = new ValueRange(0, 5), Period = TimeSpan.FromSeconds(1) }
    };

    public override IReadOnlyList<ICapability> CapabilitiesOf(int well)
        => well >= 0 && well < _ports.Length ? new ICapability[] { _ports[well] } : Array.Empty<ICapability>();

    protected override void Tick(double dt)
    {
        foreach (var p in _ports)
        {
            p.Tick(dt);
            // 电流从扭矩推：空载 0.18 A + 每 mN·m 0.028 A（450 rpm ≈ 0.7 A，额定 2.5 A 远未到）
            var amps = p.CurrentRpm > 1 ? 0.18 + p.Torque * 0.028 : 0;
            Emit(p.Channel, "stirI", Math.Round(Math.Min(_rated, amps + Noise(0.01)), 2));
        }
    }

    private static readonly HandlerTable Table = new HandlerTable()
        .Add(CommandSpecs.Stir, () => new StirHandler());

    public override ICommandHandler? Resolve(string commandId) => Table.Resolve(commandId);

    /// <summary>与反应器孔位同一条规矩：中止保搅拌（热液不搅有局部过热风险）。</summary>
    public override ValueTask<IReadOnlyList<string>?> SafeStopAsync(int well, CancellationToken ct)
    {
        if (well < 0 || well >= _ports.Length) return ValueTask.FromResult<IReadOnlyList<string>?>(Array.Empty<string>());
        var p = _ports[well];
        return ValueTask.FromResult<IReadOnlyList<string>?>(new[]
        {
            p.CurrentRpm > 0 ? $"搅拌保持 {p.CurrentRpm:F0} rpm —— 热液不搅有局部过热风险，停搅拌要在配方里写" : "搅拌本来就停着"
        });
    }
}
