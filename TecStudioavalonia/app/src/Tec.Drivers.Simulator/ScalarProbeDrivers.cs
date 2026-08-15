using System.Globalization;
using Tec.Driver.Abi;

namespace Tec.Drivers.Simulator;

/// <summary>
/// L1 标量接入（§9.3）——第三方在线分析仪的第一版形态。
/// 取到一个或多个数值，进趋势 / 判据 / 反馈 / 记录 / 导出，谱图交给对方软件。
/// pH 与浊度共用这一套：驱动之间的差别只有量程、标签与仿真曲线。
/// </summary>
public abstract class ScalarProbeDriver : IDeviceDriver
{
    public abstract DriverInfo Info { get; }
    public abstract TagDescriptor Tag { get; }
    public abstract IReadOnlyList<CommandDescriptor> Commands { get; }

    /// <summary>仿真曲线：t 为通道已运行秒数，返回该时刻的读数。</summary>
    internal abstract double Simulate(double seconds, double noise);

    public virtual ParameterSchema ConnectionSchema { get; } = new(new[]
    {
        new FieldSpec("接入方式", "接入方式", FieldKind.Choice)
            { Default = "Modbus TCP", Choices = new[] { "Modbus RTU", "Modbus TCP", "OPC UA", "厂商 SDK", "文件监视" },
              Tip = "第三方仪器的协议五花八门；真正的工作量在这里，不在画图（§9.4）" },
        new FieldSpec("地址", "地址 / 端点", FieldKind.Text) { Default = "192.168.1.50:502" },
        new FieldSpec("点位", "点位 / 寄存器", FieldKind.Text) { Default = "40001" },
        new FieldSpec("采样周期", "采样周期", FieldKind.Duration) { Default = 2d, Unit = "s", Min = 0.2, Max = 600 },
        new FieldSpec("时间戳来源", "时间戳来源", FieldKind.Choice)
            { Default = "本机接收时刻", Choices = new[] { "本机接收时刻", "仪器自带时间戳" },
              Tip = "对方的时间戳可能没有、可能没对时；统一换算到单调钟并记录换算依据（§9.4）" }
    });

    public virtual ParameterSchema ConfigSchema { get; } = new(new[]
    {
        new FieldSpec("量程下限", "量程下限", FieldKind.Number) { Default = 0d, Step = 0.1 },
        new FieldSpec("量程上限", "量程上限", FieldKind.Number) { Default = 14d, Step = 0.1 },
        new FieldSpec("失效判定", "多久没数算失效", FieldKind.Duration) { Default = 30d, Unit = "s", Min = 5, Max = 600 }
    });

    public async Task<ProbeResult> ProbeAsync(ParameterSet connection, CancellationToken ct)
    {
        await Task.Delay(60, ct).ConfigureAwait(false);
        return new ProbeResult(true, $"{connection.Str("地址")} 已响应")
        {
            Firmware = "SIM 1.0", Serial = Info.Id + "-SIM", DetectedChannels = 1
        };
    }

    public Task<IDeviceSession> OpenAsync(ParameterSet connection, DriverContext ctx, CancellationToken ct)
        => Task.FromResult<IDeviceSession>(new ScalarSession(this, ctx));
}

internal sealed class ScalarSession : SimSession
{
    private readonly ScalarProbeDriver _driver;
    private readonly ScalarImpl[] _ports;
    private double _seconds;

    public ScalarSession(ScalarProbeDriver driver, DriverContext ctx) : base(ctx)
    {
        _driver = driver;
        Tags = new[] { driver.Tag };
        var chs = ctx.ChannelNumbers.Count > 0 ? ctx.ChannelNumbers : new[] { 0 };
        _ports = chs.Select(c => new ScalarImpl(c, driver.Tag)).ToArray();
    }

    public override IReadOnlyList<TagDescriptor> Tags { get; }
    public override int WellCount => _ports.Length;

    public override IReadOnlyList<ICapability> CapabilitiesOf(int well)
        => well >= 0 && well < _ports.Length ? new ICapability[] { _ports[well] } : Array.Empty<ICapability>();

    protected override void Tick(double dt)
    {
        _seconds += dt;
        foreach (var p in _ports)
        {
            var v = _driver.Simulate(_seconds, Noise(0.01));
            p.Update(new Sample(p.Channel, _driver.Tag.Tag, Now.UtcTicks, Now, Math.Round(v, 3),
                                Context.Simulated ? Quality.Simulated : Quality.Good));
            Emit(p.Channel, _driver.Tag.Tag, Math.Round(v, 3));
        }
    }

    private static readonly HandlerTable Table = new HandlerTable()
        .Add(ProbeCommands.PhWait, () => new WaitScalarHandler("pH"))
        .Add(ProbeCommands.PhRecord, () => new RecordScalarHandler("pH"))
        .Add(ProbeCommands.TurbWait, () => new WaitScalarHandler("turb"));

    public override ICommandHandler? Resolve(string commandId) => Table.Resolve(commandId);
}

internal sealed class ScalarImpl : IScalarSensor
{
    private Sample _latest;
    private bool _has;

    public ScalarImpl(int channel, TagDescriptor tag)
    {
        Channel = channel;
        Tags = new[] { tag };
    }

    public int Channel { get; }
    public IReadOnlyList<TagDescriptor> Tags { get; }
    private readonly Broadcast<Sample> _values = new();
    public IObservable<Sample> Values => _values;

    public bool TryReadLatest(string tag, out Sample sample)
    {
        sample = _latest;
        return _has && (_latest.Tag == tag || tag.Length == 0);
    }

    public void Update(in Sample s)
    {
        _latest = s;
        _has = true;
        _values.Push(s);
    }
}

// ── 两台具体探头 ─────────────────────────────────────────────────────

public sealed class PhProbeDriver : ScalarProbeDriver
{
    public const string DriverId = "tec.probe.ph";

    public override DriverInfo Info { get; } = new(DriverId, "pH 在线检测", "Tec", "1.0.0")
    {
        ChannelsPerDevice = 0,
        IconKey = "ph",
        Description = "原位电极 + 变送器；提供 pH 判据与反馈加料的输入。",
        Capabilities = new[] { nameof(IScalarSensor) }
    };

    public override TagDescriptor Tag { get; } = new("pH", "pH", "", DataShape.Scalar)
    { Nominal = new ValueRange(0, 14), Period = TimeSpan.FromSeconds(2) };

    public override IReadOnlyList<CommandDescriptor> Commands => ProbeCommands.Ph;

    internal override double Simulate(double seconds, double noise)
        => 7.4 - 2.2 * (1 - Math.Exp(-seconds / 900.0)) + noise;
}

public sealed class TurbidityProbeDriver : ScalarProbeDriver
{
    public const string DriverId = "tec.probe.turbidity";

    public override DriverInfo Info { get; } = new(DriverId, "浊度探头", "第三方", "1.0.0")
    {
        ChannelsPerDevice = 0,
        IconKey = "turb",
        Description = "原位散射式浊度；结晶起始点判据（FR-5.15 用的就是这个数）。",
        Capabilities = new[] { nameof(IScalarSensor) }
    };

    public override TagDescriptor Tag { get; } = new("turb", "浊度", "NTU", DataShape.Scalar)
    { Nominal = new ValueRange(0, 500), Period = TimeSpan.FromSeconds(2) };

    public override IReadOnlyList<CommandDescriptor> Commands => ProbeCommands.Turbidity;

    /// <summary>成核之前几乎为零，成核后陡升——这才是"突升 = 成核点"能被判出来的原因。</summary>
    internal override double Simulate(double seconds, double noise)
    {
        var onset = 1500.0;
        var v = seconds < onset ? 1.5 : 1.5 + 260 * (1 - Math.Exp(-(seconds - onset) / 420.0));
        return Math.Max(0, v + noise * 20);
    }
}

// ── 指令 ─────────────────────────────────────────────────────────────

internal static class ProbeCommands
{
    public const string PhWait = "tec.ph.waitReach";
    public const string PhRecord = "tec.ph.record";
    public const string TurbWait = "tec.turb.waitThreshold";

    private static string N(double v, int d = 2) => v.ToString("F" + d, CultureInfo.InvariantCulture);

    /// <summary>
    /// 外部信号驱动动作时，**失效行为必须显式声明，默认不能是"继续"**（§9.4）。
    /// pH 探头掉线了还在按最后一个 pH 值加料，是安全事故，不是数据问题。
    /// </summary>
    internal static FieldSpec FailAction() => new("信号失效", "信号失效时", FieldKind.Choice)
    {
        Default = "停止并报警",
        Choices = new[] { "停止并报警", "保持当前输出并报警", "中止该通道" },
        Tip = "没有「继续」这个选项。"
    };

    private static FieldSpec Timeout(double seconds) => new("超时", "超时保护", FieldKind.Duration)
    { Default = seconds, Unit = "s", Min = 0, Max = 86400 };

    /// <summary>条件类步骤的排期估算只能靠人给一个预期值——它两个方向都可能偏，偏差最大。</summary>
    private static TimeSpan ConditionEstimate(CommandInput p, EstimationContext ctx)
        => TimeSpan.FromSeconds(Math.Max(0, p.Num("预计", 600)));

    private static FieldSpec Expected(double seconds) => new("预计", "预计耗时", FieldKind.Duration)
    { Default = seconds, Unit = "s", Min = 0, Max = 86400, Tip = "只用于排期；实际以条件命中为准" };

    public static IReadOnlyList<CommandDescriptor> Ph { get; } = new[]
    {
        new CommandDescriptor(PhWait, "等待 pH 达到", "pH", typeof(IScalarSensor),
            new ParameterSchema(new[]
            {
                new FieldSpec("目标", "目标 pH", FieldKind.Number)
                    { Default = 5.5d, Min = 0, Max = 14, Step = 0.1, Decimals = 2 },
                new FieldSpec("方向", "判据方向", FieldKind.Choice)
                    { Default = "≤ 目标", Choices = new[] { "≤ 目标", "≥ 目标" } },
                new FieldSpec("保持", "持续满足", FieldKind.Duration)
                    { Default = 30d, Unit = "s", Min = 0, Max = 3600, Tip = "去抖：避免一个噪点就判成到达" },
                Expected(900), Timeout(7200), FailAction()
            }),
            TerminationKind.Condition, ConditionEstimate,
            p => $"等待 pH {p.Str("方向", "≤ 目标").Replace("目标", N(p.Num("目标")))}")
        { IconKey = "ph", SupportsHotEdit = true },

        new CommandDescriptor(PhRecord, "记录 pH", "pH", typeof(IScalarSensor),
            new ParameterSchema(new[]
            {
                new FieldSpec("标注", "标注", FieldKind.Text) { Default = "取样点 pH" }
            }),
            TerminationKind.Immediate, (_, _) => TimeSpan.Zero,
            p => $"记录 pH：{p.Str("标注")}")
        { IconKey = "mark" }
    };

    public static IReadOnlyList<CommandDescriptor> Turbidity { get; } = new[]
    {
        new CommandDescriptor(TurbWait, "等待浊度阈值", "在线分析", typeof(IScalarSensor),
            new ParameterSchema(new[]
            {
                new FieldSpec("阈值", "浊度阈值", FieldKind.Number)
                    { Default = 25d, Unit = "NTU", Min = 0, Max = 1000, Step = 1, Decimals = 1 },
                new FieldSpec("方向", "判据方向", FieldKind.Choice)
                    { Default = "≥ 阈值", Choices = new[] { "≥ 阈值", "≤ 阈值" } },
                new FieldSpec("保持", "持续满足", FieldKind.Duration) { Default = 20d, Unit = "s", Min = 0, Max = 3600 },
                Expected(1800), Timeout(10800), FailAction()
            }),
            TerminationKind.Condition, ConditionEstimate,
            p => $"等待浊度 {p.Str("方向", "≥ 阈值").Replace("阈值", N(p.Num("阈值"), 1) + " NTU")}")
        { IconKey = "turb", SupportsHotEdit = true }
    };
}

internal sealed class WaitScalarHandler : ICommandHandler
{
    private readonly string _tag;
    public WaitScalarHandler(string tag) => _tag = tag;

    public async Task<CommandOutcome> ExecuteAsync(CommandContext ctx, CommandInput p, CancellationToken ct)
    {
        var sensor = ctx.Capabilities.Get<IScalarSensor>()
                     ?? throw new InvalidOperationException("该通道没有对应的在线检测能力");
        var target = p.Has("目标") ? p.Num("目标") : p.Num("阈值");
        var upward = p.Str("方向").StartsWith("≥", StringComparison.Ordinal);
        var hold = TimeSpan.FromSeconds(Math.Max(0, p.Num("保持")));
        var timeout = TimeSpan.FromSeconds(Math.Max(0, p.Num("超时", 7200)));
        var began = ctx.Now();
        DateTimeOffset? satisfiedSince = null;

        var ok = await SimTime.PollAsync(() =>
        {
            if (!sensor.TryReadLatest(_tag, out var s) || s.Quality == Quality.Bad)
            {
                satisfiedSince = null;
                return false;
            }
            var hit = upward ? s.Value >= target : s.Value <= target;
            if (!hit) { satisfiedSince = null; return false; }
            satisfiedSince ??= ctx.Now();
            return ctx.Now() - satisfiedSince.Value >= hold;
        }, timeout, ctx.TimeScale, ctx.Now, ct).ConfigureAwait(false);

        if (!ok) ctx.Note?.Invoke($"{_tag} 未在超时内满足判据，按「{p.Str("信号失效", "停止并报警")}」处理");
        return new CommandOutcome(ok ? EndReason.ConditionMet : EndReason.Timeout, ctx.Now() - began);
    }
}

internal sealed class RecordScalarHandler : ICommandHandler
{
    private readonly string _tag;
    public RecordScalarHandler(string tag) => _tag = tag;

    public Task<CommandOutcome> ExecuteAsync(CommandContext ctx, CommandInput p, CancellationToken ct)
    {
        var sensor = ctx.Capabilities.Get<IScalarSensor>();
        if (sensor is not null && sensor.TryReadLatest(_tag, out var s))
            ctx.Note?.Invoke($"{p.Str("标注")}：{_tag} = {s.Value:F2}（{s.Quality}）");
        else
            ctx.Note?.Invoke($"{p.Str("标注")}：{_tag} 无有效读数");
        return Task.FromResult(CommandOutcome.Instant());
    }
}
