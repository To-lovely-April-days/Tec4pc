using System.Globalization;
using Tec.Driver.Abi;

namespace Tec.Drivers.Simulator;

/// <summary>
/// 双通道反应器 RD-105。一台设备开出 2 个通道，每个孔位自带温度控制 + 搅拌 + 背景灯。
/// 指令是静态声明的——没连硬件也要能编辑配方（§3.3）。
/// </summary>
public sealed class Rd105ReactorDriver : IDeviceDriver
{
    public const string DriverId = "tec.reactor.rd105";

    public DriverInfo Info { get; } = new(DriverId, "双通道反应器 RD-105", "Tec", "1.0.0")
    {
        ChannelsPerDevice = 2,
        SimulatorIncluded = true,
        IconKey = "reactor2",
        Description = "整机自带；每孔提供 Tr/Tj 控温、磁力搅拌与 LED 背景灯。",
        Capabilities = new[] { nameof(ITemperatureControl), nameof(IStirrer), nameof(IIllumination) }
    };

    public ParameterSchema ConnectionSchema { get; } = new(new[]
    {
        new FieldSpec("端口", "串口", FieldKind.Choice)
            { Default = "COM3", Choices = new[] { "COM1", "COM2", "COM3", "COM4", "COM5", "COM6" } },
        new FieldSpec("波特率", "波特率", FieldKind.Choice)
            { Default = "115200", Choices = new[] { "9600", "19200", "38400", "57600", "115200" } },
        new FieldSpec("校验", "校验位", FieldKind.Choice)
            { Default = "无", Choices = new[] { "无", "奇", "偶" } },
        new FieldSpec("站号", "站号", FieldKind.Number) { Default = 1d, Min = 1, Max = 247, Step = 1, Decimals = 0 }
    })
    { Tip = "RD-105 走 RS-485 Modbus RTU。改完点「测试连接」，会回显固件版本与探测到的孔位数。" };

    public ParameterSchema ConfigSchema { get; } = new(new[]
    {
        new FieldSpec("釜规格", "反应釜规格", FieldKind.Choice)
            { Default = "100 mL", Choices = new[] { "25 mL", "50 mL", "100 mL", "250 mL" } },
        new FieldSpec("釜材质", "材质", FieldKind.Choice)
            { Default = "玻璃", Choices = new[] { "玻璃", "哈氏合金", "316L" } },
        new FieldSpec("搅拌桨", "搅拌桨", FieldKind.Choice)
            { Default = "锚式", Choices = new[] { "锚式", "桨式", "磁子" } },
        new FieldSpec("温度探头", "温度探头", FieldKind.Choice)
            { Default = "Pt100 四线", Choices = new[] { "Pt100 四线", "Pt1000", "热电偶 K" } }
    })
    { Tip = "整机固定的三个配件在这里选型；它们没有独立驱动，不上台面。" };

    public IReadOnlyList<CommandDescriptor> Commands => Rd105Commands.All;

    public async Task<ProbeResult> ProbeAsync(ParameterSet connection, CancellationToken ct)
    {
        await Task.Delay(120, ct).ConfigureAwait(false);
        var port = connection.Str("端口", "COM3");
        return new ProbeResult(true, $"{port} 已响应")
        {
            Firmware = "RD105-FW 1.3.0",
            Serial = "RD105-SIM-0001",
            DetectedChannels = 2
        };
    }

    public Task<IDeviceSession> OpenAsync(ParameterSet connection, DriverContext ctx, CancellationToken ct)
        => Task.FromResult<IDeviceSession>(new Rd105Session(ctx));
}

internal sealed class Rd105Session : SimSession
{
    private readonly ReactorWell[] _wells;

    public Rd105Session(DriverContext ctx) : base(ctx)
    {
        var chs = ctx.ChannelNumbers;
        _wells = new ReactorWell[2];
        for (var i = 0; i < 2; i++)
            _wells[i] = new ReactorWell(i < chs.Count ? chs[i] : 0, Emit, () => Scale, () => Now);
    }

    public override int WellCount => 2;

    public override IReadOnlyList<TagDescriptor> Tags { get; } = new[]
    {
        new TagDescriptor("Tr", "釜内温度", "℃", DataShape.Scalar)
            { Nominal = new ValueRange(-40, 180), Period = TimeSpan.FromSeconds(1) },
        new TagDescriptor("Tj", "夹套温度", "℃", DataShape.Scalar)
            { Nominal = new ValueRange(-40, 200), Period = TimeSpan.FromSeconds(1) },
        new TagDescriptor("dT", "Tr−Tj", "℃", DataShape.Scalar)
            { DerivedFrom = "Tr,Tj", Period = TimeSpan.FromSeconds(1) },
        new TagDescriptor("rpm", "搅拌转速", "rpm", DataShape.Scalar)
            { Nominal = new ValueRange(0, 1200), Period = TimeSpan.FromSeconds(1) }
    };

    public override IReadOnlyList<ICapability> CapabilitiesOf(int well)
        => well >= 0 && well < _wells.Length
            ? new ICapability[] { _wells[well], _wells[well].Stirrer, _wells[well].Light }
            : Array.Empty<ICapability>();

    protected override void Tick(double dt)
    {
        foreach (var w in _wells) w.Tick(dt, Noise(0.05));
    }

    private static readonly HandlerTable Table = new HandlerTable()
        .Add(Rd105Commands.RampTo, () => new RampHandler())
        .Add(Rd105Commands.CoolTo, () => new RampHandler())
        .Add(Rd105Commands.Hold, () => new HoldHandler())
        .Add(Rd105Commands.Gradient, () => new GradientHandler())
        .Add(Rd105Commands.StopTemp, () => new StopTempHandler())
        .Add(Rd105Commands.SetSpeed, () => new SetSpeedHandler())
        .Add(Rd105Commands.StopStir, () => new StopStirHandler())
        .Add(Rd105Commands.Light, () => new LightHandler());

    public override ICommandHandler? Resolve(string commandId) => Table.Resolve(commandId);
}

/// <summary>一个孔位。温度、搅拌、背景灯三项能力都由它提供。</summary>
internal sealed class ReactorWell : ITemperatureControl
{
    private readonly Action<int, string, double> _emit;
    private readonly Func<double> _scale;
    private readonly Func<DateTimeOffset> _now;
    private readonly Broadcast<Sample> _temp = new();

    private double _target = 25;
    private double _rate = 2;
    private bool _controlling;

    public ReactorWell(int channel, Action<int, string, double> emit, Func<double> scale, Func<DateTimeOffset> now)
    {
        Channel = channel;
        _emit = emit;
        _scale = scale;
        _now = now;
        Stirrer = new StirrerImpl(channel, emit);
        Light = new LightImpl(channel);
    }

    public int Channel { get; }
    public StirrerImpl Stirrer { get; }
    public LightImpl Light { get; }

    public TempLimits Limits { get; } = new(-40, 180, 10);
    public double CurrentReactor { get; private set; } = 25;
    public double CurrentJacket { get; private set; } = 25;
    public IObservable<Sample> Temperature => _temp;

    public Task SetTargetAsync(TempTarget target, CancellationToken ct)
    {
        _target = Math.Clamp(target.Value, Limits.Min, Limits.Max);
        _controlling = true;
        return Task.CompletedTask;
    }

    public Task RampAsync(double target, double ratePerMin, TempChannelKind kind, CancellationToken ct)
    {
        _target = Math.Clamp(target, Limits.Min, Limits.Max);
        _rate = Math.Clamp(ratePerMin <= 0 ? 2 : ratePerMin, 0.1, Limits.MaxRatePerMin);
        _controlling = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _controlling = false;
        return Task.CompletedTask;
    }

    public Task<bool> WaitReachedAsync(double target, double tolerance, TimeSpan timeout, CancellationToken ct)
        => SimTime.PollAsync(() => Math.Abs(CurrentReactor - target) <= tolerance,
                             timeout, _scale(), _now, ct);

    /// <summary>
    /// 一阶惯性 + 换热能力有限：越靠近目标越慢。
    /// 这样 Setpoint 类步骤天然"只会偏慢"，与偏差模型一致（§4.3）。
    /// </summary>
    public void Tick(double dt, double noise)
    {
        if (_controlling)
        {
            var err = _target - CurrentReactor;
            var maxStep = _rate * dt / 60.0;
            var move = Math.Clamp(err, -maxStep, maxStep);
            move *= 1 - Math.Exp(-Math.Abs(err) / 3.0) * 0.35;
            CurrentReactor += move;
            CurrentJacket = CurrentReactor + (_target - CurrentReactor) * 1.8;
        }
        else
        {
            CurrentReactor += (25 - CurrentReactor) * Math.Min(0.02, dt / 600.0);
            CurrentJacket += (CurrentReactor - CurrentJacket) * 0.2;
        }

        CurrentReactor += noise;
        CurrentJacket += noise * 1.4;

        _emit(Channel, "Tr", Math.Round(CurrentReactor, 2));
        _emit(Channel, "Tj", Math.Round(CurrentJacket, 2));
        _emit(Channel, "dT", Math.Round(CurrentReactor - CurrentJacket, 2));
        Stirrer.Tick(dt);
    }
}

internal sealed class StirrerImpl : IStirrer
{
    private readonly Action<int, string, double> _emit;
    private readonly Broadcast<Sample> _speed = new();
    private double _target;

    public StirrerImpl(int channel, Action<int, string, double> emit)
    {
        Channel = channel;
        _emit = emit;
    }

    public int Channel { get; }
    public SpeedLimits Limits { get; } = new(0, 1200);
    public double CurrentRpm { get; private set; }
    public IObservable<Sample> Speed => _speed;

    public Task SetSpeedAsync(double rpm, CancellationToken ct)
    {
        _target = Math.Clamp(rpm, Limits.Min, Limits.Max);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _target = 0;
        return Task.CompletedTask;
    }

    public void Tick(double dt)
    {
        var step = 400 * dt / 60.0 * 6;      // 变速很快，几秒到位
        CurrentRpm += Math.Clamp(_target - CurrentRpm, -step, step);
        _emit(Channel, "rpm", Math.Round(CurrentRpm, 0));
    }
}

internal sealed class LightImpl : IIllumination
{
    public LightImpl(int channel) => Channel = channel;
    public int Channel { get; }
    public bool On { get; private set; }
    public double Brightness { get; private set; } = 0.8;

    public Task SetAsync(bool on, double brightness, CancellationToken ct)
    {
        On = on;
        Brightness = Math.Clamp(brightness, 0, 1);
        return Task.CompletedTask;
    }
}

// ── 指令声明 ─────────────────────────────────────────────────────────

internal static class Rd105Commands
{
    public const string RampTo = "tec.temp.rampTo";
    public const string CoolTo = "tec.temp.coolTo";
    public const string Hold = "tec.temp.hold";
    public const string Gradient = "tec.temp.gradient";
    public const string StopTemp = "tec.temp.stop";
    public const string SetSpeed = "tec.stir.set";
    public const string StopStir = "tec.stir.stop";
    public const string Light = "tec.light.set";

    private const string ModT = "温度";
    private const string ModS = "搅拌";
    private const string ModL = "照明";

    private static FieldSpec Timeout(double seconds) =>
        new("超时", "超时保护", FieldKind.Duration)
        { Default = seconds, Unit = "s", Min = 0, Max = 86400, Tip = "到不了目标时的兜底，0 = 用驱动缺省" };

    private static string N(double v, int d = 1) => v.ToString("F" + d, CultureInfo.InvariantCulture);

    /// <summary>升温/降温耗时 = 温差 / 速率，并把 ctx.Temperature 推进到目标。</summary>
    private static TimeSpan RampEstimate(CommandInput p, EstimationContext ctx)
    {
        var target = p.Num("目标", 25);
        var rate = Math.Max(0.1, p.Num("速率", 2));
        var secs = Math.Abs(target - ctx.Temperature) / rate * 60.0;
        ctx.Temperature = target;
        return TimeSpan.FromSeconds(secs);
    }

    /// <summary>梯度控温：逐段推算，上下文串行推进，与执行顺序完全一致。</summary>
    private static TimeSpan GradientEstimate(CommandInput input, EstimationContext ctx)
    {
        var total = TimeSpan.Zero;
        foreach (var row in input.RowsOrEmpty)
        {
            var target = row.Num("目标", ctx.Temperature);
            var rate = Math.Max(0.05, row.Num("速率", 1));
            total += TimeSpan.FromSeconds(Math.Abs(target - ctx.Temperature) / rate * 60.0);
            total += TimeSpan.FromSeconds(Math.Max(0, row.Num("保持")));
            ctx.Temperature = target;
        }
        return total;
    }

    public static IReadOnlyList<CommandDescriptor> All { get; } = new[]
    {
        new CommandDescriptor(RampTo, "升温至", ModT, typeof(ITemperatureControl),
            new ParameterSchema(new[]
            {
                new FieldSpec("目标", "目标温度", FieldKind.Number)
                    { Default = 60d, Unit = "℃", Min = -40, Max = 180, Step = 0.5,
                      LimitFrom = "TemperatureControl.Limits.Max" },
                new FieldSpec("速率", "升温速率", FieldKind.Number)
                    { Default = 2d, Unit = "℃/min", Min = 0.1, Max = 10, Step = 0.1,
                      LimitFrom = "TemperatureControl.Limits.MaxRatePerMin" },
                new FieldSpec("控温对象", "控温对象", FieldKind.Choice)
                    { Default = "釜内 Tr", Choices = new[] { "釜内 Tr", "夹套 Tj" } },
                new FieldSpec("容差", "到温容差", FieldKind.Number)
                    { Default = 0.5d, Unit = "℃", Min = 0.1, Max = 5, Step = 0.1 },
                Timeout(3600)
            }),
            TerminationKind.Setpoint, RampEstimate,
            p => $"升温 {p.Str("控温对象", "釜内 Tr")} 至 {N(p.Num("目标"))} ℃，{N(p.Num("速率"))} ℃/min")
        { IconKey = "temp-up", SupportsHotEdit = true },

        new CommandDescriptor(CoolTo, "降温至", ModT, typeof(ITemperatureControl),
            new ParameterSchema(new[]
            {
                new FieldSpec("目标", "目标温度", FieldKind.Number)
                    { Default = 5d, Unit = "℃", Min = -40, Max = 180, Step = 0.5,
                      LimitFrom = "TemperatureControl.Limits.Min" },
                new FieldSpec("速率", "降温速率", FieldKind.Number)
                    { Default = 0.5d, Unit = "℃/min", Min = 0.05, Max = 10, Step = 0.05 },
                new FieldSpec("容差", "到温容差", FieldKind.Number)
                    { Default = 0.5d, Unit = "℃", Min = 0.1, Max = 5, Step = 0.1 },
                Timeout(7200)
            }),
            TerminationKind.Setpoint, RampEstimate,
            p => $"降温至 {N(p.Num("目标"))} ℃，{N(p.Num("速率"), 2)} ℃/min")
        { IconKey = "temp-down", SupportsHotEdit = true },

        new CommandDescriptor(Hold, "恒温", ModT, typeof(ITemperatureControl),
            new ParameterSchema(new[]
            {
                new FieldSpec("温度", "保持温度", FieldKind.Number)
                    { Default = 60d, Unit = "℃", Min = -40, Max = 180, Step = 0.5 },
                new FieldSpec("时长", "保持时长", FieldKind.Duration)
                    { Default = 1800d, Unit = "s", Min = 1, Max = 86400 }
            }),
            TerminationKind.Timer,
            (p, ctx) => { ctx.Temperature = p.Num("温度", ctx.Temperature); return TimeSpan.FromSeconds(Math.Max(0, p.Num("时长"))); },
            p => $"{N(p.Num("温度"))} ℃ 恒温 {Hms(p.Num("时长"))}")
        { IconKey = "temp-hold", SupportsHotEdit = true },

        new CommandDescriptor(Gradient, "梯度控温", ModT, typeof(ITemperatureControl),
            new ParameterSchema(Array.Empty<FieldSpec>())
            {
                Table = new TableSpec("温度分段", new[]
                {
                    new FieldSpec("目标", "目标 ℃", FieldKind.Number) { Default = 40d, Unit = "℃", Min = -40, Max = 180 },
                    new FieldSpec("速率", "℃/min", FieldKind.Number) { Default = 1d, Unit = "℃/min", Min = 0.05, Max = 10 },
                    new FieldSpec("保持", "保持 s", FieldKind.Duration) { Default = 600d, Unit = "s", Min = 0 }
                }),
                Tip = "逐段执行：先按速率走到目标，再保持。每段结束都会写一条记录。"
            },
            TerminationKind.Timer, GradientEstimate,
            i => i.RowsOrEmpty.Count == 0
                ? "梯度控温（未设分段）"
                : $"梯度控温 {i.RowsOrEmpty.Count} 段，末段 {N(i.RowsOrEmpty[^1].Num("目标"))} ℃")
        { IconKey = "temp-ramp" },

        new CommandDescriptor(StopTemp, "停止控温", ModT, typeof(ITemperatureControl),
            ParameterSchema.Empty, TerminationKind.Immediate,
            (_, _) => TimeSpan.Zero, _ => "停止控温")
        { IconKey = "temp-off" },

        new CommandDescriptor(SetSpeed, "设定转速", ModS, typeof(IStirrer),
            new ParameterSchema(new[]
            {
                new FieldSpec("转速", "转速", FieldKind.Number)
                    { Default = 300d, Unit = "rpm", Min = 0, Max = 1200, Step = 10, Decimals = 0,
                      LimitFrom = "Stirrer.Limits.Max" }
            }),
            TerminationKind.Immediate,
            (p, ctx) => { ctx.Rpm = p.Num("转速"); return TimeSpan.Zero; },
            p => $"搅拌 {p.Num("转速"):F0} rpm")
        { IconKey = "stir", SupportsHotEdit = true },

        new CommandDescriptor(StopStir, "停止搅拌", ModS, typeof(IStirrer),
            ParameterSchema.Empty, TerminationKind.Immediate,
            (_, ctx) => { ctx.Rpm = 0; return TimeSpan.Zero; }, _ => "停止搅拌")
        { IconKey = "stir-off" },

        new CommandDescriptor(Light, "背景灯", ModL, typeof(IIllumination),
            new ParameterSchema(new[]
            {
                new FieldSpec("开关", "开", FieldKind.Toggle) { Default = true },
                new FieldSpec("亮度", "亮度", FieldKind.Number)
                    { Default = 0.8d, Min = 0, Max = 1, Step = 0.05, Decimals = 2, VisibleWhen = "开关=true" }
            }),
            TerminationKind.Immediate,
            (_, _) => TimeSpan.Zero,
            p => p.Flag("开关", true) ? $"背景灯开 {p.Num("亮度", 0.8) * 100:F0}%" : "背景灯关")
        { IconKey = "light" }
    };

    private static string Hms(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}";
    }
}

// ── 指令处理器：只用 ABI 的能力接口，不认识具体设备类 ────────────────

internal sealed class RampHandler : ICommandHandler
{
    public async Task<CommandOutcome> ExecuteAsync(CommandContext ctx, CommandInput p, CancellationToken ct)
    {
        var temp = ctx.Capabilities.Get<ITemperatureControl>()
                   ?? throw new InvalidOperationException("该通道没有温度控制能力");
        var target = p.Num("目标", 25);
        var rate = p.Num("速率", 2);
        var tol = p.Num("容差", 0.5);
        var timeout = TimeSpan.FromSeconds(p.Num("超时", 3600));
        var kind = p.Str("控温对象", "釜内 Tr").Contains("Tj") ? TempChannelKind.Jacket : TempChannelKind.Reactor;

        var began = ctx.Now();
        await temp.RampAsync(target, rate, kind, ct).ConfigureAwait(false);
        var reached = await temp.WaitReachedAsync(target, tol, timeout, ct).ConfigureAwait(false);
        return new CommandOutcome(reached ? EndReason.Reached : EndReason.Timeout, ctx.Now() - began)
        {
            Note = reached ? null : $"未在 {timeout.TotalMinutes:F0} min 内到达 {target:F1} ℃"
        };
    }
}

internal sealed class HoldHandler : ICommandHandler
{
    public async Task<CommandOutcome> ExecuteAsync(CommandContext ctx, CommandInput p, CancellationToken ct)
    {
        var temp = ctx.Capabilities.Get<ITemperatureControl>()
                   ?? throw new InvalidOperationException("该通道没有温度控制能力");
        var began = ctx.Now();
        await temp.SetTargetAsync(new TempTarget(p.Num("温度", 25)), ct).ConfigureAwait(false);
        await SimTime.DelayAsync(TimeSpan.FromSeconds(Math.Max(0, p.Num("时长"))), ctx.TimeScale, ct)
                     .ConfigureAwait(false);
        return new CommandOutcome(EndReason.TimerElapsed, ctx.Now() - began);
    }
}

internal sealed class GradientHandler : ICommandHandler
{
    public async Task<CommandOutcome> ExecuteAsync(CommandContext ctx, CommandInput input, CancellationToken ct)
    {
        var temp = ctx.Capabilities.Get<ITemperatureControl>()
                   ?? throw new InvalidOperationException("该通道没有温度控制能力");
        var began = ctx.Now();
        foreach (var row in input.RowsOrEmpty)
        {
            ct.ThrowIfCancellationRequested();
            var target = row.Num("目标", 40);
            await temp.RampAsync(target, Math.Max(0.05, row.Num("速率", 1)), TempChannelKind.Reactor, ct)
                      .ConfigureAwait(false);
            await temp.WaitReachedAsync(target, 0.5, TimeSpan.FromHours(2), ct).ConfigureAwait(false);
            await SimTime.DelayAsync(TimeSpan.FromSeconds(Math.Max(0, row.Num("保持"))), ctx.TimeScale, ct)
                         .ConfigureAwait(false);
        }
        return new CommandOutcome(EndReason.TimerElapsed, ctx.Now() - began);
    }
}

internal sealed class StopTempHandler : ICommandHandler
{
    public async Task<CommandOutcome> ExecuteAsync(CommandContext ctx, CommandInput p, CancellationToken ct)
    {
        var temp = ctx.Capabilities.Get<ITemperatureControl>();
        if (temp is not null) await temp.StopAsync(ct).ConfigureAwait(false);
        return CommandOutcome.Instant();
    }
}

internal sealed class SetSpeedHandler : ICommandHandler
{
    public async Task<CommandOutcome> ExecuteAsync(CommandContext ctx, CommandInput p, CancellationToken ct)
    {
        var stir = ctx.Capabilities.Get<IStirrer>()
                   ?? throw new InvalidOperationException("该通道没有搅拌能力");
        await stir.SetSpeedAsync(p.Num("转速", 0), ct).ConfigureAwait(false);
        return CommandOutcome.Instant();
    }
}

internal sealed class StopStirHandler : ICommandHandler
{
    public async Task<CommandOutcome> ExecuteAsync(CommandContext ctx, CommandInput p, CancellationToken ct)
    {
        var stir = ctx.Capabilities.Get<IStirrer>();
        if (stir is not null) await stir.StopAsync(ct).ConfigureAwait(false);
        return CommandOutcome.Instant();
    }
}

internal sealed class LightHandler : ICommandHandler
{
    public async Task<CommandOutcome> ExecuteAsync(CommandContext ctx, CommandInput p, CancellationToken ct)
    {
        var light = ctx.Capabilities.Get<IIllumination>();
        if (light is not null)
            await light.SetAsync(p.Flag("开关", true), p.Num("亮度", 0.8), ct).ConfigureAwait(false);
        return CommandOutcome.Instant();
    }
}
