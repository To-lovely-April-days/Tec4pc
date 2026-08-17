using System.Globalization;
using Tec.Driver.Abi;

namespace Tec.Drivers.Simulator;

/// <summary>
/// 注射式定量加料泵。**只有 2 套却有 4 个通道**——它天生是共享资源，
/// 台面用 BindingMode.Shared 绑，执行期由资源仲裁器排队（§5.1 / §7.4）。
/// </summary>
public sealed class DosingPumpDriver : IDeviceDriver
{
    public const string DriverId = "tec.dosing.pump";

    public DriverInfo Info { get; } = new(DriverId, "自动加料泵", "Tec", "1.0.0")
    {
        ChannelsPerDevice = 0,          // 不开通道，只能绑到别人的通道上
        IconKey = "pump",
        Description = "注射式定量加料；可共享给多个通道。",
        Capabilities = new[] { nameof(IDosing) }
    };

    public ParameterSchema ConnectionSchema { get; } = new(new[]
    {
        Field.Sel("端口", "串口", new[] { "COM1", "COM2", "COM3", "COM4", "COM5", "COM6" }, "COM4"),
        Field.Sel("波特率", "波特率", new[] { "9600", "19200", "38400" }, "9600"),
        Field.Num("站号", "站号", 2, "", 1, 247, 1)
    });

    public ParameterSchema ConfigSchema { get; } = new(new[]
    {
        Field.Sel("注射器", "注射器规格", new[] { "1 mL", "5 mL", "10 mL", "25 mL", "50 mL" }, "10 mL"),
        Field.Num("管路内径", "管路内径", 1.6, "mm", 0.2, 6, 0.1),
        Field.Text("物料", "默认物料", "去离子水"),
        Field.Num("标定系数", "标定 mL/rev", 0.125, "mL/rev", 0.001, 10, 0.001)
    })
    { Tip = "标定记录属于设备实例并进 GLP；未标定的泵会被配方校验拦下来。" };

    public IReadOnlyList<CommandDescriptor> Commands => CommandSpecs.Dosing;

    public async Task<ProbeResult> ProbeAsync(ParameterSet connection, CancellationToken ct)
    {
        await Task.Delay(80, ct).ConfigureAwait(false);
        return new ProbeResult(true, $"{connection.Str("端口", "COM4")} 已响应")
        {
            Firmware = "PUMP-FW 2.1", Serial = "PUMP-SIM-0001", DetectedChannels = 1
        };
    }

    public Task<IDeviceSession> OpenAsync(ParameterSet connection, DriverContext ctx, CancellationToken ct)
        => Task.FromResult<IDeviceSession>(new PumpSession(ctx));
}

internal sealed class PumpSession : SimSession
{
    private readonly DosingImpl[] _ports;

    public PumpSession(DriverContext ctx) : base(ctx)
    {
        var chs = ctx.ChannelNumbers.Count > 0 ? ctx.ChannelNumbers : new[] { 0 };
        var maxVolume = VolumeOf(ctx.Config.Str("注射器", "10 mL"));
        var cal = ctx.Config.Num("标定系数", 0) > 0
            ? new CalibrationRecord("pump", DateTimeOffset.Now.AddDays(-12), "工程师", "称重法",
                                    ctx.Config.Num("标定系数", 0.125), DateTimeOffset.Now.AddDays(78))
            : null;
        _ports = chs.Select(c => new DosingImpl(c, Emit, maxVolume, cal, ctx.Config.Str("物料", "去离子水"))).ToArray();
    }

    private static double VolumeOf(string spec)
        => double.TryParse(spec.Split(' ')[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 10;

    public override int WellCount => _ports.Length;

    public override IReadOnlyList<TagDescriptor> Tags { get; } = new[]
    {
        new TagDescriptor("flow", "加料流量", "mL/min", DataShape.Scalar)
            { Nominal = new ValueRange(0, 20), Period = TimeSpan.FromSeconds(1) },
        new TagDescriptor("volume", "累计加料", "mL", DataShape.Scalar)
            { Nominal = new ValueRange(0, 50), Period = TimeSpan.FromSeconds(1) }
    };

    public override IReadOnlyList<ICapability> CapabilitiesOf(int well)
        => well >= 0 && well < _ports.Length ? new ICapability[] { _ports[well] } : Array.Empty<ICapability>();

    protected override void Tick(double dt)
    {
        foreach (var p in _ports) p.Tick(dt, Noise(0.01));
    }

    // pH 反馈加料由 pH 探头那边认领（判据是 pH，泵只是执行机构），这里只管纯加料
    private static readonly HandlerTable Table = new HandlerTable()
        .Add(CommandSpecs.Dose, () => new DoseHandler());

    public override ICommandHandler? Resolve(string commandId) => Table.Resolve(commandId);

    /// <summary>
    /// 中止收尾：停泵，并把累计加了多少报出来。
    /// 加料是不可逆的——停的那一刻已经进去多少，记录上必须查得到，
    /// 不然这一釜料的配比就再也对不上账了。
    /// </summary>
    public override async ValueTask<IReadOnlyList<string>?> SafeStopAsync(int well, CancellationToken ct)
    {
        if (well < 0 || well >= _ports.Length) return Array.Empty<string>();
        var p = _ports[well];
        await p.StopAsync(ct).ConfigureAwait(false);
        return new[] { $"加料泵已停，本趟累计加入 {p.TotalVolume:F2} mL（{p.Material}）" };
    }
}

internal sealed class DosingImpl : IDosing
{
    private readonly Action<int, string, double> _emit;
    private readonly Broadcast<Sample> _flow = new();
    private readonly Broadcast<Sample> _total = new();
    private double _commandedRate;
    private double _actualRate;

    public DosingImpl(int channel, Action<int, string, double> emit, double maxVolume,
                      CalibrationRecord? calibration, string material)
    {
        Channel = channel;
        _emit = emit;
        Limits = new FlowLimits(0, 20, maxVolume);
        Calibration = calibration;
        Material = material;
    }

    public int Channel { get; }
    public FlowLimits Limits { get; }
    public CalibrationRecord? Calibration { get; }
    public string Material { get; }
    public double TotalVolume { get; private set; }
    public IObservable<Sample> Flow => _flow;
    public IObservable<Sample> Total => _total;

    public Task SetRateAsync(double ratePerMin, CancellationToken ct)
    {
        _commandedRate = Math.Clamp(ratePerMin, Limits.Min, Limits.Max);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _commandedRate = 0;
        return Task.CompletedTask;
    }

    /// <summary>加料本身由 handler 编排；这里只负责把命令流量变成真实流量与累计体积。</summary>
    public Task DoseAsync(DoseRequest request, CancellationToken ct)
        => SetRateAsync(request.RatePerMin, ct);

    public void Tick(double dt, double noise)
    {
        // 加料有滞后：泵起停有加速段，不然曲线一眼假
        _actualRate += (_commandedRate - _actualRate) * Math.Min(1, dt / 4.0);
        var delivered = _actualRate * dt / 60.0;
        if (delivered > 0) TotalVolume += delivered;
        _emit(Channel, "flow", Math.Round(Math.Max(0, _actualRate + noise), 3));
        _emit(Channel, "volume", Math.Round(TotalVolume, 3));
    }
}

internal sealed class DoseHandler : ICommandHandler
{
    public async Task<CommandOutcome> ExecuteAsync(CommandContext ctx, CommandInput p, CancellationToken ct)
    {
        var dosing = ctx.Capabilities.Get<IDosing>()
                     ?? throw new InvalidOperationException("该通道没有加料能力");
        if (dosing.Calibration is null)
            ctx.Note?.Invoke("泵未标定，加料量以命令值记录");

        var volume = Math.Max(0, p.Num("vol"));
        var rate = Math.Max(0.001, p.Num("rate"));
        var began = ctx.Now();
        var startTotal = dosing.TotalVolume;

        await dosing.DoseAsync(new DoseRequest(volume, rate) { Material = p.Str("liq") }, ct).ConfigureAwait(false);
        var planned = TimeSpan.FromSeconds(volume / rate * 60.0);
        await SimTime.PollAsync(() => dosing.TotalVolume - startTotal >= volume,
                                planned + TimeSpan.FromMinutes(5), ctx.TimeScale, ctx.Now, ct).ConfigureAwait(false);
        await dosing.StopAsync(ct).ConfigureAwait(false);

        var actual = dosing.TotalVolume - startTotal;
        return new CommandOutcome(EndReason.QuantityDelivered, ctx.Now() - began)
        {
            Note = $"实加 {actual:F2} mL"
        };
    }
}

/// <summary>
/// 反馈加料。慢回路（秒级）放在上位机是可以的，但**必须有安全层兜底**（§7.7）。
/// 外部信号失效时绝不允许"继续按最后一个值加"——那是安全事故。
/// </summary>
internal sealed class FeedbackDoseHandler : ICommandHandler
{
    public async Task<CommandOutcome> ExecuteAsync(CommandContext ctx, CommandInput p, CancellationToken ct)
    {
        var dosing = ctx.Capabilities.Get<IDosing>()
                     ?? throw new InvalidOperationException("该通道没有加料能力");
        var sensor = ctx.Capabilities.Get<IScalarSensor>()
                     ?? throw new InvalidOperationException("该通道没有反馈判据用的检测能力");

        const string tag = "pH";
        var target = p.Num("target", 7);
        var band = Math.Max(0.01, p.Num("band", 0.2));
        var rate = Math.Max(0.01, p.Num("maxRate", 1));
        var cap = Math.Max(0, p.Num("maxVol", 50));
        var timeout = TimeSpan.FromMinutes(Math.Max(1, p.Num("dur", 60)));
        // 外部信号失效时绝不允许"继续按最后一个值加"——那是安全事故（§9.4）
        const string onFail = "停止并报警";

        var began = ctx.Now();
        var startTotal = dosing.TotalVolume;
        var reason = EndReason.Timeout;
        var deadline = timeout > TimeSpan.Zero ? began + timeout : DateTimeOffset.MaxValue;

        while (!ct.IsCancellationRequested)
        {
            if (!sensor.TryReadLatest(tag, out var s) || s.Quality is Quality.Bad or Quality.Stale)
            {
                await dosing.StopAsync(ct).ConfigureAwait(false);
                ctx.Note?.Invoke($"{tag} 信号失效，按「{onFail}」处理");
                reason = onFail.StartsWith("中止", StringComparison.Ordinal) ? EndReason.Aborted : EndReason.Alarm;
                break;
            }

            // 死区内就停泵等着，出了死区再加——原型 pH 反馈加料的语义
            if (s.Value <= target + band)
            {
                await dosing.StopAsync(ct).ConfigureAwait(false);
                if (ctx.Now() >= deadline) { reason = EndReason.TimerElapsed; break; }
                await SimTime.DelayAsync(TimeSpan.FromSeconds(2), ctx.TimeScale, ct).ConfigureAwait(false);
                continue;
            }

            if (dosing.TotalVolume - startTotal >= cap)
            {
                ctx.Note?.Invoke($"已达最大加料量 {cap:F2} mL，判据仍未满足");
                reason = EndReason.Timeout;
                break;
            }

            if (ctx.Now() >= deadline) { reason = EndReason.TimerElapsed; break; }

            await dosing.SetRateAsync(rate, ct).ConfigureAwait(false);
            await SimTime.DelayAsync(TimeSpan.FromSeconds(2), ctx.TimeScale, ct).ConfigureAwait(false);
        }

        await dosing.StopAsync(ct).ConfigureAwait(false);
        return new CommandOutcome(reason, ctx.Now() - began)
        {
            Note = $"实加 {dosing.TotalVolume - startTotal:F2} mL"
        };
    }
}
