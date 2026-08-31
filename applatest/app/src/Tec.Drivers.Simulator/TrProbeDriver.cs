using Tec.Driver.Abi;

namespace Tec.Drivers.Simulator;

/// <summary>
/// Tr 温度探头（Pt100 · ⌀6）。插到工位上就是在说「这一路的釜内温度由这支探头量」。
///
/// **它自己不产数。** Tr 这一路一直是反应器会话在发——真机上正是 RD105 控制器
/// 读这支 Pt100 的线——探头再仿真一路 Tr，同一个量就有了两个来源，趋势、判据、
/// 记录不知道该信哪一路。所以它的会话是空的：没有标签、没有能力、Tick 什么都
/// 不干；台面上那张读数标签显示的是所插通道的 ITemperatureControl 当前釜温。
/// </summary>
public sealed class TrProbeDriver : IDeviceDriver
{
    public const string DriverId = "tec.probe.tr";

    public DriverInfo Info { get; } = new(DriverId, "Tr 温度探头", "Tec", "1.0.0")
    {
        ChannelsPerDevice = 0,
        SimulatorIncluded = true,
        IconKey = "trprobe",
        Description = "Pt100 · ⌀6；插入工位后该路釜温由它测得——仿真机走 RD105，" +
                      "真机接的是宇电 AI-8848GD91J7 采集模块。",
        Capabilities = new[] { nameof(IScalarSensor) }
    };

    public ParameterSchema ConnectionSchema { get; } = new(new[]
    {
        Field.Sel("接线", "接线方式", new[] { "四线", "三线", "两线" }, "四线")
    })
    { Tip = "探头的数值随主机一路上来，不单独占端口——仿真机由 RD105 读，" +
            "真机由宇电 AI-8848GD91J7（主机连接参数里的「温度模块串口」）读。" };

    public ParameterSchema ConfigSchema { get; } = new(new[]
    {
        Field.Num("偏置校正", "偏置校正", 0, "℃", -5, 5, 0.01)
    });

    public IReadOnlyList<CommandDescriptor> Commands { get; } = Array.Empty<CommandDescriptor>();

    public async Task<ProbeResult> ProbeAsync(ParameterSet connection, CancellationToken ct)
    {
        await Task.Delay(60, ct).ConfigureAwait(false);
        return new ProbeResult(true, $"{connection.Str("接线", "四线")}制 Pt100 在位")
        {
            Firmware = "—", Serial = DriverId + "-SIM", DetectedChannels = 1
        };
    }

    public Task<IDeviceSession> OpenAsync(ParameterSet connection, DriverContext ctx, CancellationToken ct)
        => Task.FromResult<IDeviceSession>(new TrProbeSession(ctx));
}

/// <summary>空会话：见驱动类上的说明——数在反应器那一路，这里一根线都不发。</summary>
internal sealed class TrProbeSession : SimSession
{
    public TrProbeSession(DriverContext ctx) : base(ctx) { }

    public override IReadOnlyList<TagDescriptor> Tags { get; } = Array.Empty<TagDescriptor>();
    public override int WellCount => Math.Max(1, Context.ChannelNumbers.Count);
    public override IReadOnlyList<ICapability> CapabilitiesOf(int well) => Array.Empty<ICapability>();
    public override ICommandHandler? Resolve(string commandId) => null;
    protected override void Tick(double dtSeconds) { }

    /// <summary>探头没有输出可关，返回空表 = 「实现了，确实没什么可停的」。</summary>
    public override ValueTask<IReadOnlyList<string>?> SafeStopAsync(int well, CancellationToken ct)
        => ValueTask.FromResult<IReadOnlyList<string>?>(Array.Empty<string>());
}
