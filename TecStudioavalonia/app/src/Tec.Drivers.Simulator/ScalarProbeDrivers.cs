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
        Field.Sel("接入方式", "接入方式",
            new[] { "Modbus RTU", "Modbus TCP", "OPC UA", "厂商 SDK", "文件监视" }, "Modbus TCP"),
        Field.Text("地址", "地址 / 端点", "192.168.1.50:502"),
        Field.Text("点位", "点位 / 寄存器", "40001"),
        Field.Num("采样周期", "采样周期", 2, "s", 0.2, 600, 0.1),
        Field.Sel("时间戳来源", "时间戳来源", new[] { "本机接收时刻", "仪器自带时间戳" }, "本机接收时刻")
    })
    { Tip = "第三方仪器的协议五花八门，真正的工作量在这里，不在画图。对方的时间戳可能没有、"
            + "可能没对时；统一换算到单调钟并记录换算依据（§9.4）。" };

    public virtual ParameterSchema ConfigSchema { get; } = new(new[]
    {
        Field.Num("量程下限", "量程下限", 0, "", null, null, 0.1),
        Field.Num("量程上限", "量程上限", 14, "", null, null, 0.1),
        Field.Num("失效判定", "多久没数算失效", 30, "s", 5, 600, 1)
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
        .Add(CommandSpecs.PhSample, () => new SampleHandler("pH"))
        .Add(CommandSpecs.PhHold, () => new PhHoldHandler())
        .Add(CommandSpecs.PhAlarm, () => new AlarmHandler("pH"))
        .Add(CommandSpecs.Turbidity, () => new SampleHandler("turb"))
        .Add(CommandSpecs.Solubility, () => new SolubilityHandler())
        .Add(CommandSpecs.Raman, () => new SampleHandler("raman"))
        .Add(CommandSpecs.Infrared, () => new SampleHandler("ir"));

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

    public override IReadOnlyList<CommandDescriptor> Commands => CommandSpecs.Ph;

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

    public override IReadOnlyList<CommandDescriptor> Commands => CommandSpecs.TurbidityCommands;

    /// <summary>成核之前几乎为零，成核后陡升——这才是"突升 = 成核点"能被判出来的原因。</summary>
    internal override double Simulate(double seconds, double noise)
    {
        var onset = 1500.0;
        var v = seconds < onset ? 1.5 : 1.5 + 260 * (1 - Math.Exp(-(seconds - onset) / 420.0));
        return Math.Max(0, v + noise * 20);
    }
}

// ── 指令处理器 ───────────────────────────────────────────────────────

internal static class ProbeHelp
{
    public static IScalarSensor Sensor(CommandContext ctx)
        => ctx.Capabilities.Get<IScalarSensor>()
           ?? throw new InvalidOperationException("该通道没有对应的在线检测能力");
}

/// <summary>
/// 采集类指令（pH 采集 / 浊度采集 / 拉曼采集 / 红外采集）。
/// 采集本身是常驻的——驱动一连上就在推数；这条指令做的是"从这里开始记进本次实验"，
/// 所以它是 Immediate，不占时间。原型的 SECS 里也没有它们。
/// </summary>
internal sealed class SampleHandler : ICommandHandler
{
    private readonly string _tag;
    public SampleHandler(string tag) => _tag = tag;

    public Task<CommandOutcome> ExecuteAsync(CommandContext ctx, CommandInput p, CancellationToken ct)
    {
        var sensor = ctx.Capabilities.Get<IScalarSensor>();
        var now = sensor is not null && sensor.TryReadLatest(_tag, out var s)
            ? $"当前 {s.Value:F2}（{s.Quality}）"
            : "当前无有效读数";
        ctx.Note?.Invoke($"开始采集 {_tag}，每 {Txt.Fx(p.Num("interval", 1))} s；{now}");
        return Task.FromResult(CommandOutcome.Instant());
    }
}

/// <summary>pH 保持（反馈）：靠加料把 pH 压在死区里，维持设定时长。</summary>
internal sealed class PhHoldHandler : ICommandHandler
{
    public async Task<CommandOutcome> ExecuteAsync(CommandContext ctx, CommandInput p, CancellationToken ct)
    {
        var sensor = ProbeHelp.Sensor(ctx);
        var dosing = ctx.Capabilities.Get<IDosing>();
        var began = ctx.Now();
        var target = p.Num("target", 7);
        var band = Math.Max(0.01, p.Num("band", 0.1));
        var deadline = began + TimeSpan.FromMinutes(Math.Max(1, p.Num("dur", 60)));

        while (!ct.IsCancellationRequested && ctx.Now() < deadline)
        {
            if (!sensor.TryReadLatest("pH", out var s) || s.Quality is Quality.Bad or Quality.Stale)
            {
                if (dosing is not null) await dosing.StopAsync(ct).ConfigureAwait(false);
                ctx.Note?.Invoke("pH 信号失效，已停止调节并报警");
                return new CommandOutcome(EndReason.Alarm, ctx.Now() - began);
            }

            if (dosing is not null)
            {
                if (s.Value > target + band) await dosing.SetRateAsync(0.5, ct).ConfigureAwait(false);
                else await dosing.StopAsync(ct).ConfigureAwait(false);
            }
            await SimTime.DelayAsync(TimeSpan.FromSeconds(5), ctx.TimeScale, ct).ConfigureAwait(false);
        }

        if (dosing is not null) await dosing.StopAsync(ct).ConfigureAwait(false);
        return new CommandOutcome(EndReason.TimerElapsed, ctx.Now() - began);
    }
}

/// <summary>pH 上下限报警：登记一条监视，不占时间。真正的安全层在 Core 的 SafetyMonitor。</summary>
internal sealed class AlarmHandler : ICommandHandler
{
    private readonly string _tag;
    public AlarmHandler(string tag) => _tag = tag;

    public Task<CommandOutcome> ExecuteAsync(CommandContext ctx, CommandInput p, CancellationToken ct)
    {
        ctx.Note?.Invoke($"{_tag} 报警带 {Txt.Fx(p.Num("lo"))} ~ {Txt.Fx(p.Num("hi"))}，超出时{p.Str("act")}");
        return Task.FromResult(CommandOutcome.Instant());
    }
}

/// <summary>
/// 溶解度点测定：等浊度降到溶清阈值以下并持续确认时长。
/// 条件类步骤两个方向都可能偏，偏差最大——这正是它被单列成 Condition 的原因。
/// </summary>
internal sealed class SolubilityHandler : ICommandHandler
{
    public async Task<CommandOutcome> ExecuteAsync(CommandContext ctx, CommandInput p, CancellationToken ct)
    {
        var sensor = ProbeHelp.Sensor(ctx);
        var began = ctx.Now();
        var thr = p.Num("thr", 5);
        var hold = TimeSpan.FromMinutes(Math.Max(0, p.Num("hold", 2)));
        DateTimeOffset? since = null;

        var ok = await SimTime.PollAsync(() =>
        {
            if (!sensor.TryReadLatest("turb", out var s) || s.Quality == Quality.Bad) { since = null; return false; }
            if (s.Value > thr) { since = null; return false; }
            since ??= ctx.Now();
            return ctx.Now() - since.Value >= hold;
        }, TimeSpan.FromHours(4), ctx.TimeScale, ctx.Now, ct).ConfigureAwait(false);

        if (ok) ctx.Note?.Invoke($"溶清点：浊度持续低于 {Txt.Fx(thr)} 达 {Txt.Fx(p.Num("hold", 2))} min");
        return new CommandOutcome(ok ? EndReason.ConditionMet : EndReason.Timeout, ctx.Now() - began);
    }
}

// ── 在线拉曼 / 在线红外 ──────────────────────────────────────────────
// 第一版按 L1 接：只回传一个特征峰强度，进趋势 / 判据 / 记录 / 导出。
// 完整谱图是 L2（瀑布图 + 游标切片，只看不建模），要等真拿到原始数据流再做（§9.3）。

public sealed class RamanProbeDriver : ScalarProbeDriver
{
    public const string DriverId = "tec.probe.raman";

    public override DriverInfo Info { get; } = new(DriverId, "在线拉曼", "第三方", "1.0.0")
    {
        ChannelsPerDevice = 0,
        IconKey = "raman",
        Description = "光纤探头；第一版只回传特征峰强度（L1），谱图留在厂商软件里。",
        Capabilities = new[] { nameof(IScalarSensor) }
    };

    public override TagDescriptor Tag { get; } = new("raman", "拉曼特征峰", "a.u.", DataShape.Scalar)
    { Nominal = new ValueRange(0, 100), Period = TimeSpan.FromSeconds(30) };

    public override IReadOnlyList<CommandDescriptor> Commands => CommandSpecs.RamanCommands;

    /// <summary>晶型转化：特征峰随时间单调上升并趋于平台。</summary>
    internal override double Simulate(double seconds, double noise)
        => 4 + 62 * (1 - Math.Exp(-seconds / 2400.0)) + noise * 3;
}

public sealed class InfraredProbeDriver : ScalarProbeDriver
{
    public const string DriverId = "tec.probe.ir";

    public override DriverInfo Info { get; } = new(DriverId, "在线红外", "第三方", "1.0.0")
    {
        ChannelsPerDevice = 0,
        IconKey = "ir",
        Description = "ATR 探头；同样按 L1 接入，回传特征峰面积。",
        Capabilities = new[] { nameof(IScalarSensor) }
    };

    public override TagDescriptor Tag { get; } = new("ir", "红外特征峰", "a.u.", DataShape.Scalar)
    { Nominal = new ValueRange(0, 100), Period = TimeSpan.FromSeconds(15) };

    public override IReadOnlyList<CommandDescriptor> Commands => CommandSpecs.InfraredCommands;

    /// <summary>反应物消耗：峰面积随反应进行下降。</summary>
    internal override double Simulate(double seconds, double noise)
        => 88 * Math.Exp(-seconds / 3000.0) + 6 + noise * 2;
}
