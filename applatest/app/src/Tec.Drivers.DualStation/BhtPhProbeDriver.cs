using Tec.Driver.Abi;
using Tec.Drivers.Rd105;
using Tec.Drivers.Rd105.Modbus;

namespace Tec.Drivers.DualStation;

/// <summary>
/// 数字高温 pH 电极一体式变送器（BHT-DM100，Modbus-RTU）：电极 + 温补 + 变送器一体，直接出 pH。
/// 台面上「pH 电极」的第二种真机形态——宇电 J4 那种是 4~20 mA 进采集模块，这种自己说 Modbus，一个工位一支，
/// 串口写在自己的属性里（用户定的：每台设备的串口跟着设备走）。
///
/// 现场（2026-11-09 用户用串口工具实测）：工位 A 在 COM12、工位 B 在 COM13；**出厂地址是 2**（说明书缺省值一栏，
/// 例子里却写着 01——两种都试）、9600、8N1（寄存器 25 数据格式 = 1）。COM12 那支回帧：温度 15.8 ℃、pH 4.62、
/// 电位 136.6 mV、温度状态 0（每差 1 pH 约 59 mV，136.6 mV ≈ 7 以下 2.3 个单位，对得上）。
///
/// 寄存器（说明书「ModbusRTU 通讯协议」表，地址十进制）：0 温度 ×0.1 ℃、1 pH ×0.01、2 手动温度 ×0.1 ℃、3 电位 ×0.1 mV（有符号）、
/// 5 温度状态（0 正常 / 1 过高过低 / 2 未接传感器）、8 地址、9 波特率、10 参数恢复（写 1996）、11 复位（写 1524）、
/// 13 标定命令（1 零电位 / 2 斜率低点 / 3 斜率高点，读回标定结果码）、14 零电位 ×0.01 mV、15 斜率 %、20 标准液类型、25 数据格式。
/// </summary>
public sealed class BhtPhProbeDriver : IDeviceDriver, IStationPortDefaults
{
    public const string DriverId = "tec.probe.ph.bht";

    public const string FieldPort = "串口";
    public const string FieldAddr = "地址";
    public const string FieldBaud = "波特率";
    public const string FieldPeriod = "采样周期";

    /// <summary>缺省照现场：工位 A 的那支在 COM12（工位 B 的填 COM13）。</summary>
    public const string DefaultPort = "COM12";
    public const string DefaultPortB = "COM13";
    /// <summary>说明书缺省值一栏写的 2（例子里用的是 1，「测试连接」两个都试）。</summary>
    public const int DefaultAddr = 2;
    public const int DefaultBaud = 9600;
    public static readonly string[] BaudOptions = { "4800", "9600", "19200" };

    public const string TagPh = "pH";

    /// <summary>测试用：换掉串口池，假从站整链回归。</summary>
    public Func<ParameterSet, SharedSerial> SerialFactory { get; set; }
        = cn => SerialPortPool.Rent(cn.Str(FieldPort, DefaultPort), (int)cn.Num(FieldBaud, DefaultBaud));

    public DriverInfo Info { get; } = new(DriverId, "数字 pH 电极（BHT-DM100）", "BHT", "1.0.0")
    {
        ChannelsPerDevice = 0,          // 绑定到主机的通道上，不自带通道
        SimulatorIncluded = false,
        IconKey = "phel",               // 台面上跟宇电那支 pH 电极长一样、插同一个 pH 口——物理上就是插进釜里的一支电极
        HasSettings = true,             // 标定（零电位 / 斜率）、标准液类型、通讯参数都在设备参数面板里做
        Description = "高温 pH 电极 + 温补 + 变送器一体，RS485 Modbus-RTU 直接出 pH（0~14，±0.1）。一个工位一支，插到哪个工位那一路的 pH 就由它测得；" +
                      "零电位 / 斜率标定在「设备参数」里点按钮做。",
        Capabilities = new[] { nameof(IScalarSensor) }
    };

    public ParameterSchema ConnectionSchema { get; } = new(new[]
    {
        Field.Port(FieldPort, "串口", DefaultPort,
                   "下拉里是这台机器当前检测到的串口。现场：工位 A 的那支在 COM12、工位 B 的在 COM13"),
        Field.Num(FieldAddr, "地址", DefaultAddr, "", 1, 247, 1)
            with { Tip = "变送器寄存器 8，出厂 2（说明书例子里写的是 1）。「测试连接」配的地址不通会换另一个试并告诉你" },
        Field.Sel(FieldBaud, "波特率", BaudOptions, DefaultBaud.ToString())
            with { Tip = "出厂 9600，8N1（寄存器 25 数据格式 = 1）。只认 4800 / 9600 / 19200" },
        Field.Num(FieldPeriod, "采样周期", 1000, "ms", 200, 5000, 100)
    })
    {
        Tip = "9600 · 8N1 · 地址 2 是现场实测通的参数。帧格式固定 8N1——变送器那边改成别的（寄存器 25）这里就连不上，先在「设备参数」里改回 1。"
    };

    /// <summary>没有台面级配置：量程 0~14 是电极定的，标定那些是设备自己的寄存器，在参数面板里改。</summary>
    public ParameterSchema ConfigSchema { get; } = ParameterSchema.Empty;

    /// <summary>探头只采集；「pH 采集」指令由会话认领，不在这里声明新指令。</summary>
    public IReadOnlyList<CommandDescriptor> Commands { get; } = Array.Empty<CommandDescriptor>();

    // ── 串口跟工位走（IStationPortDefaults）：绑到 CH1 就是 COM12、CH2 就是 COM13 ──
    public string PortFieldKey => FieldPort;
    public string? DefaultPortFor(int channelNumber) => channelNumber switch { 1 => DefaultPort, 2 => DefaultPortB, _ => null };

    /// <summary>「COM12 @ 9600 8N1，地址 2」。</summary>
    public static string LinkName(ParameterSet cn)
        => $"{cn.Str(FieldPort, DefaultPort)} @ {(int)cn.Num(FieldBaud, DefaultBaud)} 8N1，地址 {cn.Int(FieldAddr, DefaultAddr)}";

    public async Task<ProbeResult> ProbeAsync(ParameterSet connection, CancellationToken ct)
    {
        var (ok, text) = await TryReadAsync(connection, ct).ConfigureAwait(false);
        if (ok) return new ProbeResult(true, text) { DetectedChannels = 1 };
        if (text.StartsWith("串口", StringComparison.Ordinal)) return new ProbeResult(false, text);   // 口都开不了，换地址也没用

        // 配置的地址没应答：说明书缺省 2、例子 1，另一个试一下再回话
        var addr = connection.Int(FieldAddr, DefaultAddr);
        var other = addr == 2 ? 1 : 2;
        var alt = connection.Clone();
        alt[FieldAddr] = (double)other;
        var (altOk, altText) = await TryReadAsync(alt, ct).ConfigureAwait(false);
        if (altOk)
            return new ProbeResult(false, $"{text}；换地址 {other} 有应答（{altText}）——把「{FieldAddr}」改成 {other} 再点「连接」");
        return new ProbeResult(false, $"{text}；换地址 {other} 也没应答——查：①是不是接 pH 变送器的那一路串口（现场 A = COM12、B = COM13）；" +
                                      "②变送器有没有上电（9~36 V）；③A/B 线有没有接反；④波特率是不是被改过（出厂 9600）");
    }

    private async Task<(bool Ok, string Text)> TryReadAsync(ParameterSet cn, CancellationToken ct)
    {
        SharedSerial? link = null;
        try
        {
            link = SerialFactory(cn);
            link.Open();
            var client = new BhtPhClient(new ModbusRtuClient(link.Transport, (byte)cn.Num(FieldAddr, DefaultAddr), busLock: link.BusLock)
                { TimeoutRetries = 0, CorruptRetries = 0 });      // 探测一试一报，链路层别叠重试（慢）
            var r = await client.ReadLiveAsync(ct).ConfigureAwait(false);
            var cal = await client.ReadCalibrationAsync(ct).ConfigureAwait(false);
            var comm = await client.ReadCommAsync(ct).ConfigureAwait(false);
            var note = r.TempStatus switch
            {
                2 => "——温度状态 2：未接传感器，电极可能没插好",
                1 => "——温度状态 1：温度过高或过低，温补不可信",
                _ => ""
            };
            return (true, $"pH 变送器 BHT-DM100 已响应（{LinkName(cn)}）：pH {r.Ph:0.00}、电极 {r.Mv:0.0} mV、温度 {r.TempC:0.0} ℃（{BhtPhClient.TempStatusText(r.TempStatus)}）{note} · " +
                          $"零电位 {cal.ZeroMv:0.00} mV、斜率 {cal.SlopePercent:0} %、标准液 {BhtPhClient.BufferText(cal.BufferSet)} · " +
                          $"变送器里地址 {comm.Address}、{comm.Baud}、{BhtPhClient.FrameText(comm.Frame)}");
        }
        catch (OperationCanceledException) { throw; }
        catch (TimeoutException ex) { return (false, $"{LinkName(cn)} 无应答（{ex.Message.TrimEnd('\n', '\r')}）"); }
        catch (ModbusException ex) { return (false, $"{LinkName(cn)} 通了但应答看不懂（多半是地址 / 波特率不对，或接到了别的设备）：{ex.Message}"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (false, SerialFault.Explain(ex, link?.PortName ?? cn.Str(FieldPort, DefaultPort)));
        }
        catch (Exception ex) { return (false, $"{LinkName(cn)}：{ex.Message}"); }
        finally { link?.Dispose(); }
    }

    public async Task<IDeviceSession> OpenAsync(ParameterSet connection, DriverContext ctx, CancellationToken ct)
    {
        var link = SerialFactory(connection);
        try
        {
            link.Open();
            var client = new BhtPhClient(new ModbusRtuClient(link.Transport, (byte)connection.Num(FieldAddr, DefaultAddr), busLock: link.BusLock));
            var session = new BhtPhProbeSession(link, client, ctx, connection,
                period: TimeSpan.FromMilliseconds(Math.Clamp(connection.Num(FieldPeriod, 1000), 200, 5000)));
            await session.InitAsync(ct).ConfigureAwait(false);
            return session;
        }
        catch (TimeoutException ex)
        {
            link.Dispose();
            throw new InvalidOperationException($"{LinkName(connection)} 无应答（{ex.Message.TrimEnd('\n', '\r')}）——查串口是不是接 pH 变送器的那一路、地址（出厂 2）、A/B 线、有没有上电", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            link.Dispose();
            throw new InvalidOperationException(SerialFault.Explain(ex, link.PortName ?? connection.Str(FieldPort, DefaultPort)), ex);
        }
        catch
        {
            link.Dispose();
            throw;
        }
    }
}

/// <summary>一拍实时量（寄存器 0~5）。</summary>
public sealed record BhtLive(double TempC, double Ph, double ManualTempC, double Mv, int TempStatus);
/// <summary>标定区（14 零电位、15 斜率、20 标准液）。</summary>
public sealed record BhtCalibration(double ZeroMv, double SlopePercent, int BufferSet);
/// <summary>通讯区（8 地址、9 波特率、25 数据格式）。</summary>
public sealed record BhtComm(int Address, int Baud, int Frame);

/// <summary>BHT-DM100 的访问层：寄存器 → 工程量。会话与参数面板都走它。</summary>
public sealed class BhtPhClient
{
    public const ushort RegTemp = 0, RegPh = 1, RegManualTemp = 2, RegMv = 3, RegTempStatus = 5;
    public const ushort RegAddress = 8, RegBaud = 9, RegRestore = 10, RegReboot = 11, RegCalibrate = 13;
    public const ushort RegZero = 14, RegSlope = 15, RegBuffer = 20, RegFrame = 25;
    public const ushort RestoreMagic = 1996, RebootMagic = 1524;

    private readonly ModbusRtuClient _mb;
    public BhtPhClient(ModbusRtuClient mb) => _mb = mb;
    public ModbusRtuClient Modbus => _mb;

    public async Task<BhtLive> ReadLiveAsync(CancellationToken ct)
    {
        var r = await _mb.ReadHoldingRegistersAsync(RegTemp, 6, ct).ConfigureAwait(false);
        return new BhtLive(r[0] * 0.1, r[1] * 0.01, r[2] * 0.1, (short)r[3] * 0.1, r[5]);
    }

    public async Task<BhtCalibration> ReadCalibrationAsync(CancellationToken ct)
    {
        var zs = await _mb.ReadHoldingRegistersAsync(RegZero, 2, ct).ConfigureAwait(false);
        var buf = await _mb.ReadHoldingRegistersAsync(RegBuffer, 1, ct).ConfigureAwait(false);
        return new BhtCalibration((short)zs[0] * 0.01, zs[1], buf[0]);
    }

    public async Task<BhtComm> ReadCommAsync(CancellationToken ct)
    {
        var ab = await _mb.ReadHoldingRegistersAsync(RegAddress, 2, ct).ConfigureAwait(false);
        var fr = await _mb.ReadHoldingRegistersAsync(RegFrame, 1, ct).ConfigureAwait(false);
        return new BhtComm(ab[0], ab[1], fr[0]);
    }

    public Task WriteAsync(ushort reg, ushort value, CancellationToken ct) => _mb.WriteRegisterAsync(reg, value, ct);

    /// <summary>读标定结果码（说明书「校准反馈错误代码列表」——按写在标定命令寄存器里读回）。</summary>
    public async Task<int> ReadCalibrationResultAsync(CancellationToken ct)
        => (await _mb.ReadHoldingRegistersAsync(RegCalibrate, 1, ct).ConfigureAwait(false))[0];

    public static string TempStatusText(int s) => s switch { 0 => "正常", 1 => "温度过高或过低", 2 => "未接温度传感器", _ => $"状态码 {s}" };
    public static string BufferText(int b) => b == 1 ? "4.00 / 6.86 / 9.18" : "4.00 / 7.00 / 10.00";
    public static string FrameText(int f) => f switch { 0 => "8N2", 1 => "8N1", 2 => "8E1", 3 => "8O1", _ => $"格式码 {f}" };

    /// <summary>标定结果码 → 人话（说明书表）。</summary>
    public static string CalibrationResultText(int code) => code switch
    {
        0x00 => "标定成功",
        0x80 => "标定失败：温度超出范围（允许标定的温度 5~50 ℃）",
        0x40 => "标定失败：mV 值超出范围",
        0x20 => "标定失败：斜率超出范围（小于 70 % 或大于 120 %）——电极老化或缓冲液不对",
        0x10 => "标定失败：零点毫伏值超出范围（小于 −70 mV 或大于 +70 mV）——电极脏了、缓冲液不对或没泡进去",
        _ => $"标定结果码 {code}（说明书表里没有）"
    };
}

/// <summary>
/// 一支数字 pH 电极的会话：轮询实时区、把 pH 发到所绑通道、以 IScalarSensor 端出去；
/// 参数面板（IDeviceSettings）里看实时量、改标准液类型、做零电位 / 斜率标定、改通讯参数。
/// 电极没有输出可停。
/// </summary>
public sealed class BhtPhProbeSession : IDeviceSession, IDeviceSettings
{
    public const string GroupLive = "live", GroupCal = "cal", GroupComm = "comm";
    public const string KPh = "ph", KTemp = "temp", KMv = "mv", KTempStatus = "tstat", KManualTemp = "mtemp",
                        KZero = "zero", KSlope = "slope", KBuffer = "buffer", KAddress = "addr", KBaud = "baud", KFrame = "frame";
    public const string ActCalZero = "calzero", ActCalLow = "callow", ActCalHigh = "calhigh", ActRestore = "restore", ActReboot = "reboot";
    public static readonly string[] BufferOptions = { "4.00 / 7.00 / 10.00", "4.00 / 6.86 / 9.18" };
    public static readonly string[] FrameOptions = { "8N2", "8N1", "8E1", "8O1" };

    private readonly SharedSerial _link;
    private readonly BhtPhClient _client;
    private readonly DriverContext _ctx;
    private readonly ParameterSet _connection;
    private readonly TimeSpan _period;
    private readonly Broadcast<Sample> _out = new();
    private readonly Sensor[] _sensors;
    private readonly LinkRecovery _recover;
    private readonly TagDescriptor _tag = new(BhtPhProbeDriver.TagPh, "pH", "", DataShape.Scalar) { Nominal = new ValueRange(0, 14) };
    private DeviceState _state = DeviceState.Connected;
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;
    private int _fails;
    private int _lastTempStatus;

    internal BhtPhProbeSession(SharedSerial link, BhtPhClient client, DriverContext ctx, ParameterSet connection, TimeSpan period)
    {
        _link = link;
        _client = client;
        _ctx = ctx;
        _connection = connection;
        _period = period;
        var chs = ctx.ChannelNumbers.Count > 0 ? ctx.ChannelNumbers : new[] { 0 };
        _sensors = chs.Select(c => new Sensor(c, _tag)).ToArray();
        _recover = new LinkRecovery($"{ctx.InstanceId} pH 变送器", link.Reopen, (l, t) => ctx.Log?.Invoke(l, t));

        Groups = new[]
        {
            new SettingsGroup(GroupLive, "实时", new ParameterSchema(new[]
            {
                Field.Num(KPh, "pH", 7, "", 0, 14, 0.01) with { ReadOnly = true },
                Field.Num(KMv, "电极电位", 0, "mV", -2000, 2000, 0.1) with { ReadOnly = true },
                Field.Num(KTemp, "电极温度", 25, "℃", 0, 130, 0.1) with { ReadOnly = true },
                Field.Text(KTempStatus, "温度状态", "正常") with { ReadOnly = true },
                Field.Num(KManualTemp, "手动温度", 25, "℃", 0, 127, 0.1) with { ReadOnly = true, Tip = "温度传感器没接时变送器按这个温度做温补（寄存器 2，只读）" },
                Field.Num(KZero, "零电位", 0, "mV", -59.16, 59.16, 0.01) with { ReadOnly = true },
                Field.Num(KSlope, "电极斜率", 100, "%", 0, 100, 1) with { ReadOnly = true, Tip = "新电极接近 100 %，低于 70 % 标定会被拒——该换电极了" }
            })) { Live = true, Tip = "变送器此刻的读数，两秒一刷。pH 就是发到通道上的那个值" },
            new SettingsGroup(GroupCal, "标定", new ParameterSchema(new[]
            {
                Field.Sel(KBuffer, "标准液", BufferOptions, BufferOptions[0])
                    with { Tip = "手上的缓冲液是哪一套（寄存器 20）。标定前先选对，再按下面的钮" },
                Field.Num(KZero, "零电位调整", 0, "mV", -59.16, 59.16, 0.01)
                    with { Tip = "寄存器 14，零电位标定成功时自动更新；一般不用手改" },
                Field.Num(KSlope, "电极斜率", 100, "%", 0, 100, 1) with { ReadOnly = true }
            }))
            {
                Tip = "两点标定：电极洗净擦干泡进 pH 7.00（或 6.86）缓冲液，读数稳了点「零电位标定」；再泡进 4.00 或 10.00（9.18）点对应的斜率标定。" +
                      "标定要在 5~50 ℃ 做；结果码直接回在回执里（成功 / 温度超范围 / 斜率超范围 / 零点超范围）"
            },
            new SettingsGroup(GroupComm, "通讯", new ParameterSchema(new[]
            {
                Field.Num(KAddress, "从机地址", BhtPhProbeDriver.DefaultAddr, "", 1, 247, 1)
                    with { Tip = "寄存器 8。改了以后变送器立刻按新地址应答——属性栏里的「地址」要跟着改再点「连接」" },
                Field.Sel(KBaud, "波特率", BhtPhProbeDriver.BaudOptions, "9600")
                    with { Tip = "寄存器 9，只认 4800 / 9600 / 19200。改了属性栏里的要跟着改" },
                Field.Sel(KFrame, "数据格式", FrameOptions, "8N1")
                    with { Tip = "寄存器 25。本程序只按 8N1 开口子——改成别的就连不上了，除非你知道自己在干什么" }
            })) { Tip = "变送器自己的通讯参数。写完它就按新参数说话，属性栏里的连接参数得跟着改" }
        };
        Actions = new[]
        {
            new SettingsAction(ActCalZero, "零电位标定（pH 7.00 / 6.86）") { Group = GroupCal, Confirm = true,
                Tip = "寄存器 13 写 1。电极要已经泡在 7.00（或 6.86）缓冲液里且读数稳定" },
            new SettingsAction(ActCalLow, "斜率标定（pH 4.00）") { Group = GroupCal, Confirm = true, Tip = "寄存器 13 写 2。电极泡在 4.00 缓冲液里" },
            new SettingsAction(ActCalHigh, "斜率标定（pH 10.00 / 9.18）") { Group = GroupCal, Confirm = true, Tip = "寄存器 13 写 3。电极泡在 10.00（或 9.18）缓冲液里" },
            new SettingsAction(ActRestore, "恢复出厂参数") { Group = GroupComm, Confirm = true,
                Tip = "寄存器 10 写 1996：地址回 2、波特率回 9600、标定清掉。之后属性栏的地址要改回 2" },
            new SettingsAction(ActReboot, "重启变送器") { Group = GroupComm, Confirm = true, Tip = "寄存器 11 写 1524" }
        };
    }

    internal int LinkReopens => _recover.Reopens;
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
        => well >= 0 && well < _sensors.Length ? new ICapability[] { _sensors[well] } : Array.Empty<ICapability>();

    /// <summary>认领「pH 采集」（Immediate：采集常驻，这条只是记进本次实验）——跟宇电那支同一个处理器。</summary>
    public ICommandHandler? Resolve(string commandId)
        => commandId == CommandSpecs.PhSample ? new YudianPhSampleHandler() : null;

    /// <summary>开机读一拍实时量和标定区，记进日志；温度传感器没接只警告不拒开——pH 照样出，温补按手动温度走。</summary>
    public async Task InitAsync(CancellationToken ct)
    {
        var r = await _client.ReadLiveAsync(ct).ConfigureAwait(false);
        var cal = await _client.ReadCalibrationAsync(ct).ConfigureAwait(false);
        _lastTempStatus = r.TempStatus;
        _ctx.Log?.Invoke("info",
            $"{InstanceId} pH 变送器 BHT-DM100（{BhtPhProbeDriver.LinkName(_connection)}）：pH {r.Ph:0.00}、电极 {r.Mv:0.0} mV、温度 {r.TempC:0.0} ℃（{BhtPhClient.TempStatusText(r.TempStatus)}）；" +
            $"零电位 {cal.ZeroMv:0.00} mV、斜率 {cal.SlopePercent:0} %、标准液 {BhtPhClient.BufferText(cal.BufferSet)}");
        if (r.TempStatus != 0)
            _ctx.Log?.Invoke("warn", $"{InstanceId} 温度状态 {r.TempStatus}（{BhtPhClient.TempStatusText(r.TempStatus)}）——" +
                                     (r.TempStatus == 2 ? "电极可能没插好或温度线断了，pH 按手动温度温补、按 Bad 发" : "温补不可信，pH 照常发"));
        if (cal.SlopePercent < 70 && cal.SlopePercent > 0)
            _ctx.Log?.Invoke("warn", $"{InstanceId} 电极斜率 {cal.SlopePercent:0} % 低于 70 %——电极老化，标定会被拒，考虑换电极");
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
            var r = await _client.ReadLiveAsync(ct).ConfigureAwait(false);
            if (_fails > 0) { _ctx.Log?.Invoke("info", $"{InstanceId} pH 变送器恢复（读失败 {_fails} 拍后）"); _fails = 0; }
            _recover.Ok();
            if (r.TempStatus != _lastTempStatus)
            {
                if (r.TempStatus == 2) _ctx.Log?.Invoke("warn", $"{InstanceId} 温度状态变为 2：未接温度传感器——电极可能脱开，pH 按 Bad 发");
                else if (r.TempStatus == 1) _ctx.Log?.Invoke("warn", $"{InstanceId} 温度状态变为 1：温度过高或过低（电极 {r.TempC:0.0} ℃），温补不可信");
                else _ctx.Log?.Invoke("info", $"{InstanceId} 温度状态恢复正常");
                _lastTempStatus = r.TempStatus;
            }
            var at = _ctx.Clock();
            var q = r.TempStatus == 2 ? Quality.Bad : Quality.Good;
            foreach (var s in _sensors)
            {
                var sample = new Sample(s.Channel, _tag.Tag, at.UtcTicks, at, Math.Round(r.Ph, 2), q);
                _out.Push(sample);
                s.Update(in sample);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            if (_fails++ == 0)
            {
                var why = ex is IOException or UnauthorizedAccessException ? SerialFault.Explain(ex, _link.PortName) : ex.Message;
                _ctx.Log?.Invoke("warn", $"{InstanceId} pH 变送器读失败：{why}（连续失败只报第一次）");
            }
            _recover.Failed(ex);
        }
    }

    /// <summary>电极没有输出可停：空表 = 「实现了，确实没什么可停的」，采集照旧。</summary>
    public ValueTask<IReadOnlyList<string>?> SafeStopAsync(int well, CancellationToken ct)
        => ValueTask.FromResult<IReadOnlyList<string>?>(Array.Empty<string>());

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _out.Complete();
        _link.Dispose();
        State = DeviceState.Disposed;
    }

    // ── IDeviceSettings ───────────────────────────────────────────

    public IReadOnlyList<SettingsGroup> Groups { get; }
    public IReadOnlyList<SettingsAction> Actions { get; }

    public async Task<ParameterSet> ReadAsync(string groupId, CancellationToken ct)
    {
        var p = new ParameterSet();
        switch (groupId)
        {
            case GroupLive:
            {
                var r = await _client.ReadLiveAsync(ct).ConfigureAwait(false);
                var cal = await _client.ReadCalibrationAsync(ct).ConfigureAwait(false);
                p[KPh] = Math.Round(r.Ph, 2); p[KMv] = Math.Round(r.Mv, 1); p[KTemp] = Math.Round(r.TempC, 1);
                p[KTempStatus] = BhtPhClient.TempStatusText(r.TempStatus); p[KManualTemp] = Math.Round(r.ManualTempC, 1);
                p[KZero] = Math.Round(cal.ZeroMv, 2); p[KSlope] = cal.SlopePercent;
                break;
            }
            case GroupCal:
            {
                var cal = await _client.ReadCalibrationAsync(ct).ConfigureAwait(false);
                p[KBuffer] = cal.BufferSet == 1 ? BufferOptions[1] : BufferOptions[0];
                p[KZero] = Math.Round(cal.ZeroMv, 2); p[KSlope] = cal.SlopePercent;
                break;
            }
            case GroupComm:
            {
                var c = await _client.ReadCommAsync(ct).ConfigureAwait(false);
                p[KAddress] = c.Address; p[KBaud] = c.Baud.ToString(); p[KFrame] = BhtPhClient.FrameText(c.Frame);
                break;
            }
            default: throw new ArgumentException($"没有这一组：{groupId}", nameof(groupId));
        }
        return p;
    }

    public async Task<IReadOnlyList<string>> WriteAsync(string groupId, ParameterSet values, CancellationToken ct)
    {
        var notes = new List<string>();
        var group = Groups.FirstOrDefault(g => g.Id == groupId) ?? throw new ArgumentException($"没有这一组：{groupId}", nameof(groupId));
        if (group.Live) { notes.Add("「实时」都是只读的，没有可写的项"); return notes; }
        foreach (var key in values.Keys.ToList())
        {
            var spec = group.Schema.Find(key);
            if (spec is null) { notes.Add($"{key}：这一组里没有这一项，没写"); continue; }
            if (spec.ReadOnly) { notes.Add($"{spec.Label}：只读，没写"); continue; }
            try
            {
                var note = await WriteOneAsync(key, spec, values, ct).ConfigureAwait(false);
                notes.Add(note);
                _ctx.Log?.Invoke("info", $"{InstanceId} pH 变送器参数：{note}");
            }
            catch (ModbusException ex)
            {
                notes.Add($"{spec.Label}：变送器没照写（{ex.Message}）");
                _ctx.Log?.Invoke("warn", $"{InstanceId} pH 变送器参数：{spec.Label} 没写成——{ex.Message}");
            }
        }
        return notes;
    }

    /// <summary>写一项、读回核对——不信应答只信读回。</summary>
    private async Task<string> WriteOneAsync(string key, FieldSpec spec, ParameterSet values, CancellationToken ct)
    {
        switch (key)
        {
            case KBuffer:
            {
                var want = (ushort)(values.Str(key) == BufferOptions[1] ? 1 : 0);
                await _client.WriteAsync(BhtPhClient.RegBuffer, want, ct).ConfigureAwait(false);
                var got = (await _client.ReadCalibrationAsync(ct).ConfigureAwait(false)).BufferSet;
                return got == want ? $"标准液改为 {BhtPhClient.BufferText(want)}" : $"标准液写了 {want} 读回却是 {got}——写没生效";
            }
            case KZero:
            {
                var mv = Math.Clamp(values.Num(key), -59.16, 59.16);
                var raw = unchecked((ushort)(short)Math.Round(mv * 100));
                await _client.WriteAsync(BhtPhClient.RegZero, raw, ct).ConfigureAwait(false);
                var got = (await _client.ReadCalibrationAsync(ct).ConfigureAwait(false)).ZeroMv;
                return Math.Abs(got - mv) < 0.006 ? $"零电位调整为 {mv:0.00} mV" : $"零电位写了 {mv:0.00} 读回却是 {got:0.00} mV";
            }
            case KAddress:
            {
                var want = (ushort)Math.Clamp(values.Int(key), 1, 247);
                await _client.WriteAsync(BhtPhClient.RegAddress, want, ct).ConfigureAwait(false);
                // 写完它就按新地址应答了，按旧地址读不回来——这一项不读回，提醒人去改属性栏
                return $"从机地址改为 {want}——变送器现在只按 {want} 应答，属性栏「{BhtPhProbeDriver.FieldAddr}」改成 {want} 再点「连接」";
            }
            case KBaud:
            {
                var want = (ushort)Math.Clamp(values.Int(key), 4800, 19200);
                if (want is not (4800 or 9600 or 19200)) return $"波特率只认 4800 / 9600 / 19200，{want} 没写";
                await _client.WriteAsync(BhtPhClient.RegBaud, want, ct).ConfigureAwait(false);
                return $"波特率改为 {want}——属性栏「{BhtPhProbeDriver.FieldBaud}」改成 {want} 再点「连接」";
            }
            case KFrame:
            {
                var idx = Array.IndexOf(FrameOptions, values.Str(key));
                if (idx < 0) return $"数据格式「{values.Str(key)}」不在表里，没写";
                await _client.WriteAsync(BhtPhClient.RegFrame, (ushort)idx, ct).ConfigureAwait(false);
                return idx == 1 ? "数据格式改为 8N1" : $"数据格式改为 {FrameOptions[idx]}——本程序只按 8N1 开口子，接下来会连不上；要改回去只能用串口工具按新格式写寄存器 25 = 1";
            }
            default: return $"{spec.Label}：不知道怎么写";
        }
    }

    public async Task<string> RunAsync(string actionId, CancellationToken ct)
    {
        switch (actionId)
        {
            case ActCalZero or ActCalLow or ActCalHigh:
            {
                var cmd = actionId == ActCalZero ? (ushort)1 : actionId == ActCalLow ? (ushort)2 : (ushort)3;
                var before = await _client.ReadLiveAsync(ct).ConfigureAwait(false);
                if (before.TempC is < 5 or > 50)
                    return $"没标：电极温度 {before.TempC:0.0} ℃，变送器只允许在 5~50 ℃ 标定";
                var calBefore = await _client.ReadCalibrationAsync(ct).ConfigureAwait(false);
                await _client.WriteAsync(BhtPhClient.RegCalibrate, cmd, ct).ConfigureAwait(false);
                // 结果码按说明书「校准反馈错误代码列表」从标定命令寄存器读回。它要多久把标定做完说明书没写：
                // 每半秒看一次、最多 3 s，寄存器里不再是刚写的命令值就是结果到了；一直是命令值就老实说没回，
                // 拿零电位 / 斜率的前后变化说话，不编一个「成功」
                int? code = null;
                for (var i = 0; i < 6; i++)
                {
                    await Task.Delay(500, ct).ConfigureAwait(false);
                    var v = await _client.ReadCalibrationResultAsync(ct).ConfigureAwait(false);
                    if (v != cmd) { code = v; break; }
                }
                var cal = await _client.ReadCalibrationAsync(ct).ConfigureAwait(false);
                var what = cmd == 1 ? "零电位标定" : cmd == 2 ? "斜率标定（低点）" : "斜率标定（高点）";
                var changed = Math.Abs(cal.ZeroMv - calBefore.ZeroMv) > 0.005 || Math.Abs(cal.SlopePercent - calBefore.SlopePercent) > 0.5;
                var verdict = code is { } c ? BhtPhClient.CalibrationResultText(c)
                            : changed ? "变送器没回结果码，但零电位 / 斜率变了——多半是做成了"
                            : "变送器没回结果码，零电位 / 斜率也没变——没有证据说明标成了，查电极是不是泡在缓冲液里、读数稳没稳";
                var msg = $"{what}：{verdict}（标定前 pH {before.Ph:0.00} / {before.Mv:0.0} mV；零电位 {calBefore.ZeroMv:0.00} → {cal.ZeroMv:0.00} mV、斜率 {calBefore.SlopePercent:0} → {cal.SlopePercent:0} %）";
                _ctx.Log?.Invoke(code == 0 || (code is null && changed) ? "warn" : "error", $"{InstanceId} {msg}");
                return msg;
            }
            case ActRestore:
                await _client.WriteAsync(BhtPhClient.RegRestore, BhtPhClient.RestoreMagic, ct).ConfigureAwait(false);
                _ctx.Log?.Invoke("warn", $"{InstanceId} pH 变送器已恢复出厂参数（寄存器 10 写 1996）");
                return "已恢复出厂参数：地址 2、9600、8N1，标定清掉——属性栏的地址改回 2 再点「连接」，电极要重新标定";
            case ActReboot:
                await _client.WriteAsync(BhtPhClient.RegReboot, BhtPhClient.RebootMagic, ct).ConfigureAwait(false);
                _ctx.Log?.Invoke("info", $"{InstanceId} pH 变送器已重启（寄存器 11 写 1524）");
                return "已重启，几秒后恢复应答";
            default: throw new ArgumentException($"没有这个动作：{actionId}", nameof(actionId));
        }
    }

    /// <summary>最新一拍的标量能力。没采到过就 TryReadLatest=false，不编数。</summary>
    private sealed class Sensor : IScalarSensor
    {
        private readonly Broadcast<Sample> _values = new();
        private Sample _latest;
        private bool _has;

        public Sensor(int channel, TagDescriptor tag) { Channel = channel; Tags = new[] { tag }; }

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
