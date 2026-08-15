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
        new FieldSpec("端口", "串口", FieldKind.Choice)
            { Default = "COM4", Choices = new[] { "COM1", "COM2", "COM3", "COM4", "COM5", "COM6" } },
        new FieldSpec("波特率", "波特率", FieldKind.Choice)
            { Default = "9600", Choices = new[] { "9600", "19200", "38400" } },
        new FieldSpec("站号", "站号", FieldKind.Number) { Default = 2d, Min = 1, Max = 247, Step = 1, Decimals = 0 }
    });

    public ParameterSchema ConfigSchema { get; } = new(new[]
    {
        new FieldSpec("注射器", "注射器规格", FieldKind.Choice)
            { Default = "10 mL", Choices = new[] { "1 mL", "5 mL", "10 mL", "25 mL", "50 mL" } },
        new FieldSpec("管路内径", "管路内径", FieldKind.Number)
            { Default = 1.6d, Unit = "mm", Min = 0.2, Max = 6, Step = 0.1, Decimals = 1 },
        new FieldSpec("物料", "默认物料", FieldKind.Text) { Default = "去离子水" },
        new FieldSpec("标定系数", "标定 mL/rev", FieldKind.Number)
            { Default = 0.125d, Unit = "mL/rev", Min = 0.001, Max = 10, Step = 0.001, Decimals = 3,
              Tip = "标定记录属于设备实例并进 GLP；未标定的泵会被配方校验拦下来" }
    });

    public IReadOnlyList<CommandDescriptor> Commands => DosingCommands.All;

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

    private static readonly HandlerTable Table = new HandlerTable()
        .Add(DosingCommands.Constant, () => new DoseHandler())
        .Add(DosingCommands.Bolus, () => new DoseHandler())
        .Add(DosingCommands.Segmented, () => new SegmentedDoseHandler())
        .Add(DosingCommands.Feedback, () => new FeedbackDoseHandler())
        .Add(DosingCommands.Stop, () => new StopDoseHandler());

    public override ICommandHandler? Resolve(string commandId) => Table.Resolve(commandId);
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

internal static class DosingCommands
{
    public const string Constant = "tec.dose.constant";
    public const string Bolus = "tec.dose.bolus";
    public const string Segmented = "tec.dose.segmented";
    public const string Feedback = "tec.dose.feedback";
    public const string Stop = "tec.dose.stop";

    private const string Mod = "加料";

    private static string N(double v, int d = 1) => v.ToString("F" + d, CultureInfo.InvariantCulture);

    private static TimeSpan ConstantEstimate(CommandInput p, EstimationContext ctx)
    {
        var volume = Math.Max(0, p.Num("体积"));
        var rate = Math.Max(0.001, p.Num("流量", 1));
        ctx.Volume += volume;
        return TimeSpan.FromSeconds(volume / rate * 60.0);
    }

    private static TimeSpan SegmentedEstimate(CommandInput input, EstimationContext ctx)
    {
        var total = TimeSpan.Zero;
        foreach (var row in input.RowsOrEmpty)
        {
            var volume = Math.Max(0, row.Num("体积"));
            var rate = Math.Max(0.001, row.Num("流量", 1));
            total += TimeSpan.FromSeconds(volume / rate * 60.0);
            total += TimeSpan.FromSeconds(Math.Max(0, row.Num("间隔")));
            ctx.Volume += volume;
        }
        return total;
    }

    public static IReadOnlyList<CommandDescriptor> All { get; } = new[]
    {
        new CommandDescriptor(Constant, "恒速加料", Mod, typeof(IDosing),
            new ParameterSchema(new[]
            {
                new FieldSpec("体积", "总体积", FieldKind.Number)
                    { Default = 10d, Unit = "mL", Min = 0.01, Max = 500, Step = 0.1, Decimals = 2,
                      LimitFrom = "Dosing.Limits.MaxVolume" },
                new FieldSpec("流量", "流量", FieldKind.Number)
                    { Default = 2d, Unit = "mL/min", Min = 0.01, Max = 20, Step = 0.1, Decimals = 2,
                      LimitFrom = "Dosing.Limits.Max" },
                new FieldSpec("物料", "物料", FieldKind.Text) { Default = "" }
            }),
            TerminationKind.Quantity, ConstantEstimate,
            p => $"恒速加料 {N(p.Num("体积"), 2)} mL @ {N(p.Num("流量"), 2)} mL/min")
        { IconKey = "dose", SupportsHotEdit = true },

        new CommandDescriptor(Bolus, "定量加料", Mod, typeof(IDosing),
            new ParameterSchema(new[]
            {
                new FieldSpec("体积", "体积", FieldKind.Number)
                    { Default = 2d, Unit = "mL", Min = 0.01, Max = 500, Step = 0.1, Decimals = 2 },
                new FieldSpec("流量", "最大流量", FieldKind.Number)
                    { Default = 10d, Unit = "mL/min", Min = 0.01, Max = 20, Step = 0.1, Decimals = 2 }
            }),
            TerminationKind.Quantity, ConstantEstimate,
            p => $"定量加料 {N(p.Num("体积"), 2)} mL")
        { IconKey = "dose-bolus" },

        new CommandDescriptor(Segmented, "分段加料", Mod, typeof(IDosing),
            new ParameterSchema(Array.Empty<FieldSpec>())
            {
                Table = new TableSpec("加料分段", new[]
                {
                    new FieldSpec("体积", "mL", FieldKind.Number) { Default = 2d, Unit = "mL", Min = 0.01, Max = 500, Decimals = 2 },
                    new FieldSpec("流量", "mL/min", FieldKind.Number) { Default = 1d, Unit = "mL/min", Min = 0.01, Max = 20, Decimals = 2 },
                    new FieldSpec("间隔", "段后等待 s", FieldKind.Duration) { Default = 300d, Unit = "s", Min = 0 }
                }),
                Tip = "每段加完等待设定时间再进下一段；每段都会在执行记录里留一行。"
            },
            TerminationKind.Quantity, SegmentedEstimate,
            i => i.RowsOrEmpty.Count == 0
                ? "分段加料（未设分段）"
                : $"分段加料 {i.RowsOrEmpty.Count} 段，共 {N(i.RowsOrEmpty.Sum(r => r.Num("体积")), 2)} mL")
        { IconKey = "dose-seg" },

        // FR-5.14 自动反馈控制。它同时要 IDosing 与 IScalarSensor——
        // 判据用哪一路由参数选，所以换成第三方浊度计也不用改这条指令（§9.1）
        new CommandDescriptor(Feedback, "反馈加料", Mod, typeof(IDosing),
            new ParameterSchema(new[]
            {
                new FieldSpec("判据标签", "判据来源", FieldKind.Choice)
                    { Default = "pH", Choices = new[] { "pH", "turb" } },
                new FieldSpec("目标", "判据目标", FieldKind.Number)
                    { Default = 5.5d, Min = -1000, Max = 1000, Step = 0.1, Decimals = 2 },
                new FieldSpec("方向", "加料使其", FieldKind.Choice)
                    { Default = "下降至目标", Choices = new[] { "下降至目标", "上升至目标" } },
                new FieldSpec("流量", "加料流量", FieldKind.Number)
                    { Default = 0.5d, Unit = "mL/min", Min = 0.01, Max = 20, Step = 0.05, Decimals = 2 },
                new FieldSpec("最大体积", "最大加料量", FieldKind.Number)
                    { Default = 20d, Unit = "mL", Min = 0.1, Max = 500, Step = 0.5, Decimals = 2,
                      LimitFrom = "Dosing.Limits.MaxVolume" },
                new FieldSpec("预计", "预计耗时", FieldKind.Duration)
                    { Default = 1200d, Unit = "s", Min = 0, Max = 86400, Tip = "只用于排期" },
                new FieldSpec("超时", "超时保护", FieldKind.Duration) { Default = 5400d, Unit = "s", Min = 0, Max = 86400 },
                ProbeCommands.FailAction()
            }),
            TerminationKind.Condition,
            (p, ctx) => { ctx.Volume += p.Num("最大体积") * 0.5; return TimeSpan.FromSeconds(Math.Max(0, p.Num("预计", 1200))); },
            p => $"反馈加料至 {p.Str("判据标签")} {N(p.Num("目标"), 2)}，最多 {N(p.Num("最大体积"), 1)} mL")
        { IconKey = "dose-fb", SupportsHotEdit = true, AlsoRequires = new[] { typeof(IScalarSensor) } },

        new CommandDescriptor(Stop, "停止加料", Mod, typeof(IDosing),
            ParameterSchema.Empty, TerminationKind.Immediate,
            (_, _) => TimeSpan.Zero, _ => "停止加料")
        { IconKey = "dose-off" }
    };
}

internal sealed class DoseHandler : ICommandHandler
{
    public async Task<CommandOutcome> ExecuteAsync(CommandContext ctx, CommandInput p, CancellationToken ct)
    {
        var dosing = ctx.Capabilities.Get<IDosing>()
                     ?? throw new InvalidOperationException("该通道没有加料能力");
        if (dosing.Calibration is null)
            ctx.Note?.Invoke("泵未标定，加料量以命令值记录");

        var volume = Math.Max(0, p.Num("体积"));
        var rate = Math.Max(0.001, p.Num("流量", 1));
        var began = ctx.Now();
        var startTotal = dosing.TotalVolume;

        await dosing.DoseAsync(new DoseRequest(volume, rate) { Material = p.Str("物料") }, ct).ConfigureAwait(false);
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

internal sealed class SegmentedDoseHandler : ICommandHandler
{
    public async Task<CommandOutcome> ExecuteAsync(CommandContext ctx, CommandInput input, CancellationToken ct)
    {
        var dosing = ctx.Capabilities.Get<IDosing>()
                     ?? throw new InvalidOperationException("该通道没有加料能力");
        var began = ctx.Now();
        var grand = 0d;

        foreach (var row in input.RowsOrEmpty)
        {
            ct.ThrowIfCancellationRequested();
            var volume = Math.Max(0, row.Num("体积"));
            var rate = Math.Max(0.001, row.Num("流量", 1));
            var start = dosing.TotalVolume;
            await dosing.DoseAsync(new DoseRequest(volume, rate), ct).ConfigureAwait(false);
            var planned = TimeSpan.FromSeconds(volume / rate * 60.0);
            await SimTime.PollAsync(() => dosing.TotalVolume - start >= volume,
                                    planned + TimeSpan.FromMinutes(5), ctx.TimeScale, ctx.Now, ct).ConfigureAwait(false);
            await dosing.StopAsync(ct).ConfigureAwait(false);
            grand += dosing.TotalVolume - start;
            await SimTime.DelayAsync(TimeSpan.FromSeconds(Math.Max(0, row.Num("间隔"))), ctx.TimeScale, ct)
                         .ConfigureAwait(false);
        }

        return new CommandOutcome(EndReason.QuantityDelivered, ctx.Now() - began) { Note = $"实加 {grand:F2} mL" };
    }
}

internal sealed class StopDoseHandler : ICommandHandler
{
    public async Task<CommandOutcome> ExecuteAsync(CommandContext ctx, CommandInput p, CancellationToken ct)
    {
        var dosing = ctx.Capabilities.Get<IDosing>();
        if (dosing is not null) await dosing.StopAsync(ct).ConfigureAwait(false);
        return CommandOutcome.Instant();
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

        var tag = p.Str("判据标签", "pH");
        var target = p.Num("目标");
        var downward = p.Str("方向", "下降至目标").StartsWith("下降", StringComparison.Ordinal);
        var rate = Math.Max(0.01, p.Num("流量", 0.5));
        var cap = Math.Max(0, p.Num("最大体积", 20));
        var timeout = TimeSpan.FromSeconds(Math.Max(0, p.Num("超时", 5400)));
        var onFail = p.Str("信号失效", "停止并报警");

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

            var hit = downward ? s.Value <= target : s.Value >= target;
            if (hit) { reason = EndReason.ConditionMet; break; }

            if (dosing.TotalVolume - startTotal >= cap)
            {
                ctx.Note?.Invoke($"已达最大加料量 {cap:F2} mL，判据仍未满足");
                reason = EndReason.Timeout;
                break;
            }

            if (ctx.Now() >= deadline) { reason = EndReason.Timeout; break; }

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
