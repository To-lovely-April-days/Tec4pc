using Tec.Driver.Abi;
using Tec.Drivers.DualStation.Modbus;
using Tec.Drivers.DualStation.Yudian;

namespace Tec.Drivers.DualStation;

/// <summary>
/// 台面上那支「Tr 温度探头」在真机上的形态：宇电 AI-8848GD91J7 的一路输入。
/// 串口写在**自己的**属性里（用户定的：每台设备的串口跟着设备自己走）——
/// 两支探头共一台模块时填同一个口名即可，串口池会把它们并到一条链路上。
/// </summary>
public sealed class YudianTrProbeDriver : YudianProbeDriverBase
{
    public const string DriverId = "tec.probe.tr.yudian";

    public YudianTrProbeDriver() : base(
        id: DriverId,
        name: "Tr 温度探头",
        icon: "trprobe",
        kind: YudianKind.Thermal,
        tag: new TagDescriptor("Tr", "釜内温度", "℃", DataShape.Scalar)
            { Nominal = new ValueRange(-40, 180) },
        defaultPort: "COM4",
        description: "Pt100 探头，接在宇电 AI-8848GD91J7 采集模块的一路输入上；" +
                     "插到哪个工位，那一路的釜内温度就由它测得。")
    { }
}

/// <summary>
/// 台面上那支「pH 玻璃电极」在真机上的形态：宇电 AI-8848GD91J4 的一路输入
/// （4~20mA 变送）。定标 ScL/ScH 从模块上读，不在这里再抄一遍。
/// </summary>
public sealed class YudianPhProbeDriver : YudianProbeDriverBase
{
    public const string DriverId = "tec.probe.ph.yudian";

    public YudianPhProbeDriver() : base(
        id: DriverId,
        name: "pH 玻璃电极",
        icon: "phel",
        kind: YudianKind.Linear,
        tag: new TagDescriptor("pH", "pH", "", DataShape.Scalar)
            { Nominal = new ValueRange(0, 14) },
        defaultPort: "COM5",
        description: "复合电极经 pH 变送器（4~20mA）接宇电 AI-8848GD91J4；" +
                     "量程定标（ScL/ScH）从模块上读出来，不在配置里另抄一份。")
    { }
}

/// <summary>
/// 两支真机探头的公共骨架：连接（串口/地址/波特率）+ 配置（宇电通道），
/// 会话只做采集——探头没有输出可停，SafeStop 永远是空表。
/// </summary>
public abstract class YudianProbeDriverBase : IDeviceDriver
{
    public const string FieldPort = "串口";
    public const string FieldAddr = "地址";
    public const string FieldBaud = "波特率";
    public const string FieldModuleCh = "宇电通道";
    public const string FieldPeriod = "采样周期";

    private readonly YudianKind _kind;
    private readonly TagDescriptor _tag;

    protected YudianProbeDriverBase(string id, string name, string icon, YudianKind kind,
                                    TagDescriptor tag, string defaultPort, string description)
    {
        _kind = kind;
        _tag = tag;
        Info = new DriverInfo(id, name, "宇电", "1.0.0")
        {
            ChannelsPerDevice = 0,          // 绑定到主机的通道上，不自带通道
            SimulatorIncluded = false,
            IconKey = icon,
            Description = description,
            Capabilities = new[] { nameof(IScalarSensor) }
        };
        ConnectionSchema = new ParameterSchema(new[]
        {
            Field.Port(FieldPort, "串口", defaultPort,
                       "下拉里是这台机器当前检测到的串口。两台宇电拼在同一段导轨上时 485 已并联——" +
                       "选同一个口即共线（两台模块地址须不同）"),
            Field.Num(FieldAddr, "模块地址", 1, "", 1, 80, 1),
            Field.Sel(FieldBaud, "波特率", new[] { "4800", "9600", "19200", "38400", "57600", "115200" }, "19200"),
            Field.Num(FieldPeriod, "采样周期", 1000, "ms", 200, 5000, 100)
        })
        {
            Tip = "串口在探头自己的属性里（每台设备各管各的口子）。宇电出厂地址 1、" +
                  "波特率 19.2K。同一台模块的两支探头填同一个口 + 同一个地址，" +
                  "「宇电通道」分别选各自接的那一路。"
        };
        ConfigSchema = new ParameterSchema(new[]
        {
            Field.Num(FieldModuleCh, "宇电通道", 1, "", 1, 4, 1)
        })
        {
            Tip = "这支探头接在模块的第几路输入上（IN1~IN4）。标度按模块自己的 " +
                  "InP / 定标寄存器换算，接错种类（温度口插 pH 表）开机就会被指出来。"
        };
    }

    public DriverInfo Info { get; }
    public ParameterSchema ConnectionSchema { get; }
    public ParameterSchema ConfigSchema { get; }

    /// <summary>探头只采集；pH 的采集指令由会话认领，不在这里声明新指令。</summary>
    public IReadOnlyList<CommandDescriptor> Commands { get; } = Array.Empty<CommandDescriptor>();

    /// <summary>测试用：换掉串口池，假从站/假总线整链回归。</summary>
    public Func<ParameterSet, SharedSerial> SerialFactory { get; set; } =
        cn => SerialPortPool.Rent(cn.Str(FieldPort, "COM4"), (int)cn.Num(FieldBaud, 19200));

    public async Task<ProbeResult> ProbeAsync(ParameterSet connection, CancellationToken ct)
    {
        SharedSerial? link = null;
        try
        {
            link = SerialFactory(connection);
            link.Open();
            var client = new YudianClient(new ModbusRtuClient(
                link.Transport, (byte)connection.Num(FieldAddr, 1), busLock: link.BusLock));
            var id = await client.InitAsync(_kind, ct).ConfigureAwait(false);
            var bad = id.Channels.Where(c => c.Enabled && c.Problem is not null).ToList();
            return bad.Count == 0
                ? new ProbeResult(true, $"宇电模块已响应（特征字 {id.FeatureWord:X4}）") { DetectedChannels = 1 }
                : new ProbeResult(false, bad[0].Problem!);
        }
        catch (Exception ex)
        {
            return new ProbeResult(false, ex.Message);
        }
        finally
        {
            link?.Dispose();
        }
    }

    public async Task<IDeviceSession> OpenAsync(ParameterSet connection, DriverContext ctx, CancellationToken ct)
    {
        var link = SerialFactory(connection);
        try
        {
            link.Open();
            var client = new YudianClient(new ModbusRtuClient(
                link.Transport, (byte)connection.Num(FieldAddr, 1), busLock: link.BusLock));
            var session = new YudianProbeSession(link, client, _kind, _tag, ctx,
                moduleChannel: Math.Clamp((int)ctx.Config.Num(FieldModuleCh, 1), 1, 4),
                period: TimeSpan.FromMilliseconds(Math.Clamp(connection.Num(FieldPeriod, 1000), 200, 5000)));
            await session.InitAsync(ct).ConfigureAwait(false);
            return session;
        }
        catch
        {
            link.Dispose();
            throw;
        }
    }
}

/// <summary>
/// 一支真机探头的会话：轮询宇电模块上自己那一路，把读数发到所绑的系统通道，
/// 并以 IScalarSensor 端出去（台面读数标签、pH 判据、配方校验吃它）。
/// 断线的那一拍挂 Bad 质量位；探头没有输出可停。
/// </summary>
public sealed class YudianProbeSession : IDeviceSession
{
    private readonly SharedSerial _link;
    private readonly YudianClient _client;
    private readonly YudianKind _kind;
    private readonly TagDescriptor _tag;
    private readonly DriverContext _ctx;
    private readonly int _moduleCh;
    private readonly TimeSpan _period;
    private readonly Broadcast<Sample> _out = new();
    private readonly ProbeSensor[] _sensors;
    private DeviceState _state = DeviceState.Connected;
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;
    private int _fails;

    internal YudianProbeSession(SharedSerial link, YudianClient client, YudianKind kind,
                                TagDescriptor tag, DriverContext ctx, int moduleChannel, TimeSpan period)
    {
        _link = link;
        _client = client;
        _kind = kind;
        _tag = tag;
        _ctx = ctx;
        _moduleCh = moduleChannel;
        _period = period;
        var chs = ctx.ChannelNumbers.Count > 0 ? ctx.ChannelNumbers : new[] { 0 };
        _sensors = chs.Select(c => new ProbeSensor(c, tag)).ToArray();
    }

    public string InstanceId => _ctx.InstanceId;

    public DeviceState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            StateChanged?.Invoke(this, value);
        }
    }

    public event EventHandler<DeviceState>? StateChanged;
    public IObservable<Sample> Samples => _out;
    public int WellCount => _sensors.Length;
    public IReadOnlyList<TagDescriptor> Tags => new[] { _tag };

    public IReadOnlyList<ICapability> CapabilitiesOf(int well)
        => well >= 0 && well < _sensors.Length
            ? new ICapability[] { _sensors[well] }
            : Array.Empty<ICapability>();

    /// <summary>pH 探头认领「pH 采集」（Immediate：采集常驻，这条只是记进本次实验）。</summary>
    public ICommandHandler? Resolve(string commandId)
        => _kind == YudianKind.Linear && commandId == CommandSpecs.PhSample
            ? new YudianPhSampleHandler()
            : null;

    /// <summary>开机自检：读模块身份、核对自己那一路的种类与标度，接反了直接开不了。</summary>
    public async Task InitAsync(CancellationToken ct)
    {
        var id = await _client.InitAsync(_kind, ct).ConfigureAwait(false);
        var setup = id.Channels[_moduleCh - 1];
        if (!setup.Enabled)
            throw new InvalidOperationException(
                $"宇电模块的 CH{_moduleCh} 是关闭的（In=0）——查「宇电通道」配置或模块参数");
        if (setup.Problem is { } p)
            throw new InvalidOperationException($"宇电模块 CH{_moduleCh}：{p}");
        if (id.WriteLocked)
            _ctx.Log?.Invoke("warn", $"{InstanceId} 宇电模块 Loc 锁着写入（只读不受影响，部署改参数时注意）");
    }

    public Task StartAsync(CancellationToken ct)
    {
        _link.Open();
        _pollCts = new CancellationTokenSource();
        _pollTask = Task.Run(() => PollLoopAsync(_pollCts.Token), CancellationToken.None);
        State = DeviceState.Ready;
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _pollCts?.Cancel();
        if (_pollTask is { } t) { try { await t.ConfigureAwait(false); } catch { } }
        _pollTask = null;
        State = DeviceState.Connected;
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_period);
        while (!ct.IsCancellationRequested)
        {
            try { if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) break; }
            catch (OperationCanceledException) { break; }
            try { await PollOnceAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>采一拍。单拎出来是为了回归测试能一拍一拍地推。</summary>
    internal async Task PollOnceAsync(CancellationToken ct)
    {
        try
        {
            var r = await _client.ReadAsync(ct).ConfigureAwait(false);
            if (_fails > 0) { _ctx.Log?.Invoke("info", $"{InstanceId} 宇电模块恢复"); _fails = 0; }
            var x = r[_moduleCh - 1];
            if (x.Value is not { } v) return;      // 配置问题在 Init 就报了；这里没值就不发
            var at = _ctx.Clock();
            var q = x.SensorFault ? Quality.Bad : Quality.Good;
            foreach (var s in _sensors)
            {
                var sample = new Sample(s.Channel, _tag.Tag, at.UtcTicks, at, v, q);
                _out.Push(sample);
                s.Update(in sample);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            if (_fails++ == 0)
                _ctx.Log?.Invoke("warn", $"{InstanceId} 宇电模块读失败：{ex.Message}（连续失败只报第一次）");
        }
    }

    /// <summary>探头没有输出可停：空表 = 「实现了，确实没什么可停的」，采集照旧。</summary>
    public ValueTask<IReadOnlyList<string>?> SafeStopAsync(int well, CancellationToken ct)
        => ValueTask.FromResult<IReadOnlyList<string>?>(Array.Empty<string>());

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _out.Complete();
        _link.Dispose();                            // 归还串口池；共口的另一台还在用就不真关
        State = DeviceState.Disposed;
    }

    /// <summary>最新一拍的标量能力。没采到过就 TryReadLatest=false，不编数。</summary>
    private sealed class ProbeSensor : IScalarSensor
    {
        private readonly Broadcast<Sample> _values = new();
        private Sample _latest;
        private bool _has;

        public ProbeSensor(int channel, TagDescriptor tag)
        {
            Channel = channel;
            Tags = new[] { tag };
        }

        public int Channel { get; }
        public IReadOnlyList<TagDescriptor> Tags { get; }
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
}

/// <summary>「pH 采集」在真机电极上的认领。语义与仿真那份一致：Immediate，不占时间。</summary>
internal sealed class YudianPhSampleHandler : ICommandHandler
{
    public Task<CommandOutcome> ExecuteAsync(CommandContext ctx, CommandInput p, CancellationToken ct)
    {
        var sensor = ctx.Capabilities.Get<IScalarSensor>();
        var now = sensor is not null && sensor.TryReadLatest("pH", out var s)
            ? $"当前 pH {s.Value:F2}（{s.Quality}）"
            : "当前无有效读数";
        ctx.Note?.Invoke($"开始采集 pH，每 {p.Num("interval", 1):0.##} s；{now}");
        return Task.FromResult(CommandOutcome.Instant());
    }
}
