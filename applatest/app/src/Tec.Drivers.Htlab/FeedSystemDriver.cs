using Tec.Driver.Abi;
using Tec.Drivers.DualStation;
using Tec.Drivers.Rd105;
using Tec.Drivers.Rd105.Modbus;

namespace Tec.Drivers.Htlab;

/// <summary>
/// 两液一气进料系统（霍桐仪器 HTLAB，Modbus-RTU）：一路 MFC 气体 + 两台计量泵，顶上两只料瓶，自带触摸屏与报警。
/// 协议摘自厂家给的 MaxChemical 插件源码（见 FeedRegisters）。
///
/// 在台面上是**接在一个工位加料口上的独立设备**（ChannelsPerDevice = 0，走「feed」那个口，管子画到釜盖），
/// 接到哪个工位，那一路的加料能力（IDosing）就是它的泵：配方「加料」步骤里的「加料泵 1 / 加料泵 2」就是这台的泵 1 / 泵 2；
/// HMI 加料卡、运行页、导出里那一个「加料泵」是配置项「配方加料泵」选的那台（缺省泵 1）。
/// MFC 气路配方语言里没有对应指令，在「设备参数」面板里设流量、开停。
///
/// 现场串口还没定（八个 CH344 口已经占满：RD105 COM7、IO8R COM3、宇电 COM8、冷水机 COM10、搅拌 COM11/14、pH COM12/13），
/// 缺省先写 COM15，接上第三块 USB 转串再在属性栏改。
/// </summary>
public sealed class FeedSystemDriver : IDeviceDriver
{
    public const string DriverId = "tec.feed.htlab2l1g";

    public const string FieldPort = "串口";
    public const string FieldAddr = "站号";
    public const string FieldBaud = "波特率";
    public const string FieldPeriod = "轮询周期";
    public const string DefaultPort = "COM15";
    public const int DefaultAddr = 1;
    public const int DefaultBaud = 9600;
    public static readonly string[] BaudOptions = { "4800", "9600", "19200", "38400", "57600", "115200" };

    /// <summary>配方 / HMI 里那一个「加料泵」用这台的哪台泵。</summary>
    public const string FieldRecipePump = "配方加料泵";
    public const string Pump1 = "泵 1";
    public const string Pump2 = "泵 2";
    public static readonly string[] PumpOptions = { Pump1, Pump2 };
    public const string FieldMaxRate = "泵最大流量";
    public const string FieldMaxVolume = "单次最大体积";
    public const string FieldLiquid1 = "泵 1 料液";
    public const string FieldLiquid2 = "泵 2 料液";
    /// <summary>安全停机（中止通道）时气路也停。缺省停：反应物气体继续往中止的釜里送比断保护气更危险。</summary>
    public const string FieldStopGasOnSafeStop = "安全停机也停气路";

    // 采样标签：配方加料泵镜像到 flow / vol（HMI 加料卡、导出列认这两个），其余各自一路
    public const string TagFlow = "flow", TagVol = "vol";
    public const string TagP1Flow = "p1flow", TagP1Vol = "p1vol", TagP1Pressure = "p1P";
    public const string TagP2Flow = "p2flow", TagP2Vol = "p2vol", TagP2Pressure = "p2P";
    public const string TagGasFlow = "gasFlow", TagGasVol = "gasVol", TagFeedAlarm = "feedAlarm";

    /// <summary>测试用：换掉串口池，假从站整链回归。</summary>
    public Func<ParameterSet, SharedSerial> SerialFactory { get; set; }
        = cn => SerialPortPool.Rent(cn.Str(FieldPort, DefaultPort), (int)cn.Num(FieldBaud, DefaultBaud));

    public DriverInfo Info { get; } = new(DriverId, "两液一气进料系统", "霍桐仪器 HTLAB", "1.0.0")
    {
        ChannelsPerDevice = 0,              // 接在工位的加料口上，不自带通道
        SimulatorIncluded = false,
        HasSettings = true,                 // MFC 气路、两台泵、报警都在设备参数面板里
        IconKey = "feedsys",
        Description = "一路 MFC 气体 + 两台计量泵，顶上两只料瓶，RS485 Modbus-RTU。管子接到哪个工位的加料口，那一路的加料就是它的泵：" +
                      "配方「加料」步骤选泵 1 / 泵 2；气路在「设备参数」里设流量、开停。",
        Capabilities = new[] { nameof(IDosing) }
    };

    public ParameterSchema ConnectionSchema { get; } = new(new[]
    {
        Field.Port(FieldPort, "串口", DefaultPort,
                   "下拉里是这台机器当前检测到的串口。现场八个 CH344 口已经占满，这台要接第三块 USB 转串，接上后在这里选"),
        Field.Num(FieldAddr, "站号", DefaultAddr, "", 1, 247, 1)
            with { Tip = "进料系统 PLC 的 Modbus 从站号，厂家软件里叫「Modbus站号」，缺省 1" },
        Field.Sel(FieldBaud, "波特率", BaudOptions, DefaultBaud.ToString())
            with { Tip = "厂家软件缺省 9600、无校验、8 位、1 停止位" },
        Field.Num(FieldPeriod, "轮询周期", 1000, "ms", 200, 5000, 100)
            with { Tip = "一帧读全 49 个寄存器（MFC、两台泵、报警）。厂家软件是 2 s 一次" }
    })
    {
        Tip = "9600 · 8N1 · 站号 1。「测试连接」真的开口子把 MFC 与两台泵的设定 / 瞬时 / 累计 / 压力、运行状态、报警位全读出来摆在下面。"
    };

    public ParameterSchema ConfigSchema { get; } = new(new[]
    {
        Field.Sel(FieldRecipePump, "配方加料泵", PumpOptions, Pump1)
            with { Tip = "HMI 加料卡、运行页「加料」、导出里的 flow / vol 列认的是这台泵。配方「加料」步骤里自己选「加料泵 1 / 2」，不受这项限制" },
        Field.Num(FieldMaxRate, "泵最大流量", 50, "mL/min", 0.1, 1000, 0.1)
            with { Tip = "计量泵的流量上限，按泵头型号填。配方「一次加入」按它送；步骤里填的流量超过它会被配方校验拦下" },
        Field.Num(FieldMaxVolume, "单次最大体积", 2000, "mL", 1, 100000, 1)
            with { Tip = "一步加料最多允许的体积，按料瓶容量填——配方校验用" },
        Field.Text(FieldLiquid1, "泵 1 料液", "料液 A"),
        Field.Text(FieldLiquid2, "泵 2 料液", "料液 B"),
        Field.Bool(FieldStopGasOnSafeStop, "安全停机也停气路", true)
            with { Tip = "通道被中止（停止 / 安全联锁 / 出错）时两台泵一定停；勾上气路（MFC）也一起停。通保护气的釜可以不勾" }
    })
    {
        Tip = "泵的流量上限、单次体积上限是配方校验的依据，按真实泵头和料瓶填；料液名只进记录与报告。"
    };

    /// <summary>认领配方里的「加料」（声明在 ABI 的 CommandSpecs.Dosing，跟仿真泵同一条）。</summary>
    public IReadOnlyList<CommandDescriptor> Commands { get; } = CommandSpecs.Dosing;

    public static string LinkName(ParameterSet cn)
        => $"{cn.Str(FieldPort, DefaultPort)} @ {(int)cn.Num(FieldBaud, DefaultBaud)} 8N1，站号 {cn.Int(FieldAddr, DefaultAddr)}";

    public async Task<ProbeResult> ProbeAsync(ParameterSet connection, CancellationToken ct)
    {
        SharedSerial? link = null;
        try
        {
            link = SerialFactory(connection);
            link.Open();
            var client = new FeedClient(new ModbusRtuClient(link.Transport, (byte)connection.Num(FieldAddr, DefaultAddr), busLock: link.BusLock)
                { TimeoutRetries = 0, CorruptRetries = 0 });
            var s = await client.ReadAllAsync(ct).ConfigureAwait(false);
            return new ProbeResult(true, $"进料系统已响应（{LinkName(connection)}）：{Readout(s)}") { DetectedChannels = 1 };
        }
        catch (OperationCanceledException) { throw; }
        catch (TimeoutException ex)
        {
            return new ProbeResult(false, $"{LinkName(connection)} 无应答（{ex.Message.TrimEnd('\n', '\r')}）——查：①串口是不是接进料系统的那一路；②站号（厂家软件里的「Modbus站号」，缺省 1）；" +
                                          "③波特率 9600 8N1；④进料系统上电、485 的 A/B 线");
        }
        catch (ModbusException ex)
        {
            return new ProbeResult(false, $"{LinkName(connection)} 通了但应答看不懂（多半是站号 / 波特率不对，或接到了别的设备）：{ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ProbeResult(false, SerialFault.Explain(ex, link?.PortName ?? connection.Str(FieldPort, DefaultPort)));
        }
        catch (Exception ex) { return new ProbeResult(false, $"{LinkName(connection)}：{ex.Message}"); }
        finally { link?.Dispose(); }
    }

    /// <summary>「MFC 设 0.00 瞬时 0.00 累计 0.0 L 停着 · 泵 1 设 2.30 瞬时 2.29 累计 125.4 mL 压力 0.31 MPa 运行 · 泵 2 … · 无报警 · MFC 量程 0~10 L/min」</summary>
    internal static string Readout(FeedSnapshot s)
    {
        string Pump(int n) =>
            $"泵 {n} 设 {s.PumpSetpoint(n):0.00} 瞬时 {s.PumpFlow(n):0.00} mL/min 累计 {s.PumpTotal(n):0.0} mL 压力 {s.PumpPressure(n):0.00} MPa {(s.PumpOn(n) ? "运行" : "停着")}";
        var alarm = s.AnyAlarm ? $"**报警：{s.AlarmText()}**" : "无报警";
        return $"MFC 设 {s.MfcSetpoint:0.00} 瞬时 {s.MfcFlow:0.00} L/min 累计 {s.MfcTotal:0.0} L {(s.MfcOn ? "运行" : "停着")} · {Pump(1)} · {Pump(2)} · {alarm} · " +
               $"MFC 量程 {s.MfcRangeLow:0.#}~{s.MfcRangeHigh:0.#} L/min · 泵压力报警 泵1 {s.Pump1PressureLow:0.##}~{s.Pump1PressureHigh:0.##} / 泵2 {s.Pump2PressureLow:0.##}~{s.Pump2PressureHigh:0.##} MPa";
    }

    public async Task<IDeviceSession> OpenAsync(ParameterSet connection, DriverContext ctx, CancellationToken ct)
    {
        var link = SerialFactory(connection);
        try
        {
            link.Open();
            var client = new FeedClient(new ModbusRtuClient(link.Transport, (byte)connection.Num(FieldAddr, DefaultAddr), busLock: link.BusLock));
            var session = new FeedSystemSession(link, client, ctx, connection,
                period: TimeSpan.FromMilliseconds(Math.Clamp(connection.Num(FieldPeriod, 1000), 200, 5000)));
            await session.InitAsync(ct).ConfigureAwait(false);
            return session;
        }
        catch (TimeoutException ex)
        {
            link.Dispose();
            throw new InvalidOperationException($"{LinkName(connection)} 无应答（{ex.Message.TrimEnd('\n', '\r')}）——查串口是不是接进料系统的那一路、站号（缺省 1）、波特率 9600、有没有上电", ex);
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
