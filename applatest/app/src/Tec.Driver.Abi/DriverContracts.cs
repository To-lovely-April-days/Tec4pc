namespace Tec.Driver.Abi;

/// <summary>Discovered → Configured → Probing → Connected → Ready → Faulted → Disposed（§3.4）。</summary>
public enum DeviceState
{
    Discovered,
    Configured,
    Probing,
    Connected,
    Ready,
    Faulted,
    Disposed
}

public sealed record DriverInfo(string Id, string Name, string Vendor, string Version)
{
    public string Abi { get; init; } = AbiVersion.Text;
    /// <summary>一台设备开出几个通道（双通道反应器 = 2，探头 = 0，表示只能绑定到别人的通道上）。</summary>
    public int ChannelsPerDevice { get; init; }
    public bool SimulatorIncluded { get; init; } = true;
    public string? IconKey { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string> Capabilities { get; init; } = Array.Empty<string>();
}

public sealed record ProbeResult(bool Success, string Message)
{
    public string? Firmware { get; init; }
    public string? Serial { get; init; }
    public int? DetectedChannels { get; init; }
}

/// <summary>宿主交给驱动的东西。驱动不认识主程序，只认识这个。</summary>
public sealed class DriverContext
{
    public required string InstanceId { get; init; }
    /// <summary>该设备的每个孔位对应的系统通道号。探头类设备长度为 0。</summary>
    public required IReadOnlyList<int> ChannelNumbers { get; init; }
    /// <summary>设备配置（釜规格、料液、量程…），按驱动自己的 ConfigSchema。</summary>
    public ParameterSet Config { get; init; } = new();
    public bool Simulated { get; init; }
    /// <summary>仿真加速倍数。真实硬件恒为 1。</summary>
    public double TimeScale { get; init; } = 1;
    /// <summary>
    /// 采样打时间戳用的钟。仿真加速时它是虚拟钟——否则一条 10 分钟的升温会被记成 10 秒，
    /// 记录里的"实际时长"和"计划时长"就没法比了。
    /// </summary>
    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.Now;
    public Action<string, string>? Log { get; init; }
}

public interface IDeviceDriver
{
    DriverInfo Info { get; }

    /// <summary>要哪些连接参数，主程序据此渲染表单。串口/OPC UA/厂商 SDK 各写各的（§3.3）。</summary>
    ParameterSchema ConnectionSchema { get; }

    /// <summary>设备实例级配置：釜规格、量程、料液（FR-2.6）。</summary>
    ParameterSchema ConfigSchema { get; }

    /// <summary>指令是静态声明——没连硬件也要能编辑配方。</summary>
    IReadOnlyList<CommandDescriptor> Commands { get; }

    Task<ProbeResult> ProbeAsync(ParameterSet connection, CancellationToken ct);

    Task<IDeviceSession> OpenAsync(ParameterSet connection, DriverContext ctx, CancellationToken ct);
}

public interface IDeviceSession : IAsyncDisposable
{
    string InstanceId { get; }
    DeviceState State { get; }
    event EventHandler<DeviceState>? StateChanged;

    IReadOnlyList<TagDescriptor> Tags { get; }
    IObservable<Sample> Samples { get; }

    /// <summary>孔位数；探头为 1（它自己就是一路）。</summary>
    int WellCount { get; }

    /// <summary>该孔位提供的能力实例。已经带好了通道号，上层拿到即可用。</summary>
    IReadOnlyList<ICapability> CapabilitiesOf(int well);

    ICommandHandler? Resolve(string commandId);

    Task StartAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);

    /// <summary>
    /// 把这台设备在**这一路**上的输出收到安全态。通道被中止时调它——
    /// 操作人按了停止、安全联锁触发、执行出错，都算。
    ///
    /// 中止本身只是取消配方的执行循环，机器不会因此停下来：温控器还守着最后
    /// 那个设定值，泵还在打。**该关什么、能不能关，只有驱动自己知道**——
    /// 一台反应器该切加热但多半要保住搅拌（热液不搅局部过热），一支 pH 探头
    /// 压根没什么可停的。所以这件事归驱动，Core 不替它假定（§核心架构 §1）。
    ///
    /// 返回值进执行记录，GLP 要答得出「停的那一刻机器被动了什么」：
    ///   · null   = 这个驱动没实现停机动作。界面照实说出来，不假装停干净了
    ///   · 空表   = 实现了，但这台设备在这一路上确实没什么可停的
    ///   · 有内容 = 逐条记下来（「已切断加热输出」「加料泵已停，本步已加 12.4 mL」）
    ///
    /// 只在**非正常结束**时调用。正常跑完不调：收尾状态是配方作者定的
    /// （降温结晶跑完就该保持在 5 ℃ 过夜，替他关掉才是帮倒忙）。
    /// </summary>
    ValueTask<IReadOnlyList<string>?> SafeStopAsync(int well, CancellationToken ct)
        => ValueTask.FromResult<IReadOnlyList<string>?>(null);
}
