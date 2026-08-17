using Tec.Driver.Abi;

namespace Tec.Drivers.Simulator;

/// <summary>
/// 双通道反应器 RD-105。一台设备开出 2 个通道，每个孔位自带温度控制 + 搅拌 + 背景灯。
/// 指令是静态声明的——没连硬件也要能编辑配方（§3.3）。
/// 它认领温度模块 4 条与搅拌 1 条。
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
        Field.Sel("端口", "串口", new[] { "COM1", "COM2", "COM3", "COM4", "COM5", "COM6" }, "COM3"),
        Field.Sel("波特率", "波特率", new[] { "9600", "19200", "38400", "57600", "115200" }, "115200"),
        Field.Sel("校验", "校验位", new[] { "无", "奇", "偶" }, "无"),
        Field.Num("站号", "站号", 1, "", 1, 247, 1)
    })
    { Tip = "RD-105 走 RS-485 Modbus RTU。改完点「测试连接」，会回显固件版本与探测到的孔位数。" };

    public ParameterSchema ConfigSchema { get; } = new(new[]
    {
        Field.Sel("釜规格", "反应釜规格", new[] { "25 mL", "50 mL", "100 mL", "250 mL" }, "100 mL"),
        Field.Sel("釜材质", "材质", new[] { "玻璃", "哈氏合金", "316L" }, "玻璃"),
        Field.Sel("搅拌桨", "搅拌桨", new[] { "锚式", "桨式", "磁子" }, "锚式"),
        Field.Sel("温度探头", "温度探头", new[] { "Pt100 四线", "Pt1000", "热电偶 K" }, "Pt100 四线")
    })
    { Tip = "整机固定的三个配件在这里选型；它们没有独立驱动，不上台面。" };

    public IReadOnlyList<CommandDescriptor> Commands { get; } =
        CommandSpecs.Temperature.Concat(CommandSpecs.Stirring).ToList();

    public async Task<ProbeResult> ProbeAsync(ParameterSet connection, CancellationToken ct)
    {
        await Task.Delay(120, ct).ConfigureAwait(false);
        return new ProbeResult(true, $"{connection.Str("端口", "COM3")} 已响应")
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
        // 控温目标值。只在控温期间有数据——停控（自然冷却）时本来就没有设定值
        new TagDescriptor("Tset", "设定温度", "℃", DataShape.Scalar)
            { Nominal = new ValueRange(-40, 180), Period = TimeSpan.FromSeconds(1) },
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
        .Add(CommandSpecs.Control, () => new ControlHandler())
        .Add(CommandSpecs.Gradient, () => new GradientHandler())
        .Add(CommandSpecs.Hold, () => new HoldHandler())
        .Add(CommandSpecs.PassiveCool, () => new PassiveCoolHandler())
        .Add(CommandSpecs.Stir, () => new StirHandler());

    public override ICommandHandler? Resolve(string commandId) => Table.Resolve(commandId);

    /// <summary>
    /// 中止收尾：**切加热，保搅拌。**
    ///
    /// 加热必须切——中止只停了配方，温控器还守着最后那个设定值，
    /// 一路烧到目标温度再稳住，操作人按下停止时想的绝不是这个。
    ///
    /// 搅拌**故意留着**：釜里多半是热的反应液，不搅容易局部过热、结块、暴沸。
    /// 停搅拌比继续搅危险，所以这台设备的「安全态」就是「不加热、继续搅」。
    /// 要停搅拌的话在配方末尾自己写一条转速 0，那是工艺决定，不是停机动作。
    /// </summary>
    public override async ValueTask<IReadOnlyList<string>?> SafeStopAsync(int well, CancellationToken ct)
    {
        if (well < 0 || well >= _wells.Length) return Array.Empty<string>();
        var w = _wells[well];
        var did = new List<string>();

        await w.StopAsync(ct).ConfigureAwait(false);
        did.Add($"已切断加热输出（停在 {w.CurrentReactor:F1} ℃，此后自然冷却）");

        did.Add(w.Stirrer.CurrentRpm > 0
            ? $"搅拌保持 {w.Stirrer.CurrentRpm:F0} rpm —— 热液不搅有局部过热风险，停搅拌要在配方里写"
            : "搅拌本来就停着");
        return did;
    }
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

    public TempLimits Limits { get; } = new(-40, 180, 16);
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
        _rate = Math.Clamp(ratePerMin <= 0 ? 2 : ratePerMin, 0.05, Limits.MaxRatePerMin);
        _controlling = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _controlling = false;
        return Task.CompletedTask;
    }

    public Task<bool> WaitReachedAsync(double target, double tolerance, TimeSpan timeout, CancellationToken ct)
        => SimTime.PollAsync(() => Math.Abs(CurrentReactor - target) <= tolerance, timeout, _scale(), _now, ct);

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
            // 停控之后按自然冷却走：0.5 ℃/min，与排期估算里的 PASSIVE 一致
            var toward = 25 - CurrentReactor;
            var step = 0.5 * dt / 60.0;
            CurrentReactor += Math.Clamp(toward, -step, step);
            CurrentJacket += (CurrentReactor - CurrentJacket) * 0.2;
        }

        CurrentReactor += noise;
        CurrentJacket += noise * 1.4;

        _emit(Channel, "Tr", Math.Round(CurrentReactor, 2));
        _emit(Channel, "Tj", Math.Round(CurrentJacket, 2));
        _emit(Channel, "dT", Math.Round(CurrentReactor - CurrentJacket, 2));
        // 设定温度只在控温时才存在：停控（自然冷却）没有设定值，
        // 这一路断掉比拿釜温顶替诚实——导出的是要签进记录的数据。
        if (_controlling) _emit(Channel, "Tset", Math.Round(_target, 2));
        Stirrer.Tick(dt);
    }
}

internal sealed class StirrerImpl : IStirrer
{
    private readonly Action<int, string, double> _emit;
    private readonly Broadcast<Sample> _speed = new();
    private double _target;
    private double _rampSeconds = 5;

    public StirrerImpl(int channel, Action<int, string, double> emit)
    {
        Channel = channel;
        _emit = emit;
    }

    public int Channel { get; }
    public SpeedLimits Limits { get; } = new(0, 1000);
    public double CurrentRpm { get; private set; }
    public IObservable<Sample> Speed => _speed;

    public Task SetSpeedAsync(double rpm, CancellationToken ct)
    {
        _target = Math.Clamp(rpm, Limits.Min, Limits.Max);
        return Task.CompletedTask;
    }

    /// <summary>加/减速时间由指令给出（原型的 ramp 参数）。</summary>
    public void SetRampSeconds(double seconds) => _rampSeconds = Math.Max(0.5, seconds);

    public Task StopAsync(CancellationToken ct)
    {
        _target = 0;
        return Task.CompletedTask;
    }

    public void Tick(double dt)
    {
        var step = Limits.Max * dt / _rampSeconds;
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

// ── 指令处理器：只用 ABI 的能力接口，不认识具体设备类 ────────────────

internal static class TempHelp
{
    public static ITemperatureControl Temp(CommandContext ctx)
        => ctx.Capabilities.Get<ITemperatureControl>()
           ?? throw new InvalidOperationException("该通道没有温度控制能力");

    public static IStirrer Stir(CommandContext ctx)
        => ctx.Capabilities.Get<IStirrer>()
           ?? throw new InvalidOperationException("该通道没有搅拌能力");
}

/// <summary>
/// 控温。到达即结束；勾了"到达后等待稳定"再多等一个允差窗。
/// 升温还是降温不用问——RampAsync 给的是目标值，往哪边走由当前温度决定。
/// </summary>
internal sealed class ControlHandler : ICommandHandler
{
    public async Task<CommandOutcome> ExecuteAsync(CommandContext ctx, CommandInput p, CancellationToken ct)
    {
        var temp = TempHelp.Temp(ctx);
        var target = p.Num("target");
        var tol = p.Num("tol", 0.5);
        var kind = p.Str("obj", "釜内 Tr").Contains("Tj") ? TempChannelKind.Jacket : TempChannelKind.Reactor;
        var began = ctx.Now();

        await temp.RampAsync(target, p.Num("rate", 2), kind, ct).ConfigureAwait(false);
        // 到不了的目标不能把通道永远挂住：兜底超时按"温差 / 速率"的 3 倍给
        var budget = TimeSpan.FromMinutes(Math.Abs(target - temp.CurrentReactor)
                                          / Math.Max(p.Num("rate", 2), 0.05) * 3 + 10);
        var reached = await temp.WaitReachedAsync(target, tol, budget, ct).ConfigureAwait(false);

        if (reached && p.Flag("wait", true))
            await SimTime.DelayAsync(TimeSpan.FromMinutes(1), ctx.TimeScale, ct).ConfigureAwait(false);

        return new CommandOutcome(reached ? EndReason.Reached : EndReason.Timeout, ctx.Now() - began)
        {
            Note = reached ? null : $"未在 {budget.TotalMinutes:F0} min 内到达 {target:F1} ℃"
        };
    }
}

/// <summary>梯度控温：逐段走完，每段先按速率到目标再保持。</summary>
internal sealed class GradientHandler : ICommandHandler
{
    public async Task<CommandOutcome> ExecuteAsync(CommandContext ctx, CommandInput input, CancellationToken ct)
    {
        var temp = TempHelp.Temp(ctx);
        var began = ctx.Now();
        var rounds = input.Flag("loop") ? 2 : 1;   // 勾了"循环执行该曲线"就再走一遍

        for (var k = 0; k < rounds; k++)
            foreach (var row in input.RowsOrEmpty)
            {
                ct.ThrowIfCancellationRequested();
                var target = row.Num("t");
                await temp.RampAsync(target, Math.Max(row.Num("r"), 0.01), TempChannelKind.Reactor, ct)
                          .ConfigureAwait(false);
                await temp.WaitReachedAsync(target, 0.5, TimeSpan.FromHours(4), ct).ConfigureAwait(false);
                await SimTime.DelayAsync(TimeSpan.FromMinutes(row.Num("h")), ctx.TimeScale, ct).ConfigureAwait(false);
            }

        return new CommandOutcome(EndReason.TimerElapsed, ctx.Now() - began);
    }
}

/// <summary>恒温保持：保持当前温度，只计时。</summary>
internal sealed class HoldHandler : ICommandHandler
{
    public async Task<CommandOutcome> ExecuteAsync(CommandContext ctx, CommandInput p, CancellationToken ct)
    {
        var temp = TempHelp.Temp(ctx);
        var began = ctx.Now();
        await temp.SetTargetAsync(new TempTarget(temp.CurrentReactor), ct).ConfigureAwait(false);
        await SimTime.DelayAsync(TimeSpan.FromMinutes(p.Num("dur")), ctx.TimeScale, ct).ConfigureAwait(false);
        return new CommandOutcome(EndReason.TimerElapsed, ctx.Now() - began);
    }
}

/// <summary>自然冷却：停掉控温靠环境降，到点或超时结束。</summary>
internal sealed class PassiveCoolHandler : ICommandHandler
{
    public async Task<CommandOutcome> ExecuteAsync(CommandContext ctx, CommandInput p, CancellationToken ct)
    {
        var temp = TempHelp.Temp(ctx);
        var began = ctx.Now();
        var target = p.Num("target", 25);
        await temp.StopAsync(ct).ConfigureAwait(false);
        var ok = await SimTime.PollAsync(() => temp.CurrentReactor <= target + 0.5,
            TimeSpan.FromMinutes(p.Num("timeout", 120)), ctx.TimeScale, ctx.Now, ct).ConfigureAwait(false);
        return new CommandOutcome(ok ? EndReason.Reached : EndReason.Timeout, ctx.Now() - began)
        {
            Note = ok ? null : $"超时放弃，仍在 {temp.CurrentReactor:F1} ℃"
        };
    }
}

/// <summary>
/// 搅拌。转速 0 走 StopAsync 而不是 SetSpeedAsync(0)——
/// 真机上这两条是不同的寄存器操作：一个是设定值归零，一个是关输出。
/// </summary>
internal sealed class StirHandler : ICommandHandler
{
    public async Task<CommandOutcome> ExecuteAsync(CommandContext ctx, CommandInput p, CancellationToken ct)
    {
        var stir = TempHelp.Stir(ctx);
        var began = ctx.Now();
        var rpm = p.Num("rpm");
        var ramp = p.Num("ramp", 5);

        if (stir is StirrerImpl impl) impl.SetRampSeconds(ramp);
        if (rpm <= 0) await stir.StopAsync(ct).ConfigureAwait(false);
        else await stir.SetSpeedAsync(rpm, ct).ConfigureAwait(false);

        await SimTime.DelayAsync(TimeSpan.FromSeconds(ramp), ctx.TimeScale, ct).ConfigureAwait(false);
        return new CommandOutcome(EndReason.Reached, ctx.Now() - began);
    }
}
