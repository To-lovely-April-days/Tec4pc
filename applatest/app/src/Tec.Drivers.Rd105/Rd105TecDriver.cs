using TecControl.Core.Comm;
using TecControl.Core.Protocol;
using Tec.Driver.Abi;

namespace Tec.Drivers.Rd105;

/// <summary>
/// TEC 温控器（RD105 通讯协议，光测未来）。真机驱动——不是仿真。
///
/// 名字按协议取，不按型号：TecControl.Core 说的是 RD105 那套 ASCII
/// （TC1:TG=?@），跟广州荣硕的 TTD-7008 不是一回事——那台走 Modbus TCP/RTU、
/// 8 路输入 8 路输出。**本项目只用 RD105**，TTD-7008 不在范围内；
/// 提它是因为手上有那份说明书，别看见就以为这个类该把它一起认了。
///
/// 协议、标度换算、PID、串级、自整定全在 TecControl.Core 里，那套跟着硬件一起演进；
/// 这个类只做翻译：把它的接口翻成 Tec.Driver.Abi 的能力契约，
/// 让上层照旧只问「能不能控温」，不问「是哪台机器」。
/// </summary>
public sealed class Rd105TecDriver : IDeviceDriver
{
    /// <summary>与仿真机的 tec.reactor.rd105 区分开：那是仿的整机，这是真的温控器。</summary>
    public const string DriverId = "tec.temp.rd105";

    public const string FieldPort = "端口";
    public const string FieldBaud = "波特率";
    /// <summary>ASCII（TTL 口）还是 Modbus-RTU（RS485 口），见 Rd105Protocol。</summary>
    public const string FieldProtocol = "协议";
    /// <summary>Modbus-RTU 的站号（协议 §3.5.3 ADDRESS，出厂 1）；ASCII 不用。</summary>
    public const string FieldAddress = "站号";
    public const string FieldParity = "校验";
    public const string FieldPeriod = "控制周期";
    public const string FieldOverUp = "超温上限";
    public const string FieldOverLow = "超温下限";
    public const string FieldMaxCurrent = "最大电流";

    /// <summary>连接参数缺省——按现场那台机器：RD105 在 CH344 的 C 口（COM7），RS485 = Modbus-RTU、站号 1，
    /// 波特率 38400（用户定的；协议 §1 写的 485 口出厂值是 9600，「连接」时两档都会试）。</summary>
    public const string DefaultPort = "COM7";
    public const int DefaultBaud = 38400;
    public const string DefaultProtocol = Rd105Protocol.Modbus;
    public const int DefaultAddress = 1;

    /// <summary>测试连接用的链路工厂。回归测试换成假串口，不必插硬件。</summary>
    public Func<ParameterSet, Rd105Link> LinkFactory { get; set; } = Rd105Link.Serial;

    public DriverInfo Info { get; } = new(DriverId, "TEC 温控器（RD105）", "光测未来", "1.0.0")
    {
        // 真机拓扑（docs/双工位反应主机驱动需求.md §3）：TC1 = 工位 A、TC2 = 工位 B，
        // 每路探头测的都是夹套 Tj。釜内 Tr / pH 由宇电模块另采，不在这台设备上
        ChannelsPerDevice = 2,
        SimulatorIncluded = false,
        IconKey = "reactor2",
        Description = "RD105 ASCII 协议。一台带两路夹套回路——TC1 = 工位 A，TC2 = 工位 B；" +
                      "釜内 Tr 由宇电采集模块另采。",
        Capabilities = new[] { nameof(ITemperatureControl), nameof(ITemperatureTuning) }
    };

    public ParameterSchema ConnectionSchema { get; } = new(new[]
    {
        Field.Port(FieldPort, "串口", DefaultPort,
                   "下拉里是这台机器当前检测到的串口（Windows 形如 COM7，Linux 形如 /dev/ttyUSB0）"),
        Field.Sel(FieldBaud, "波特率", new[] { "9600", "19200", "38400", "57600", "115200" }, DefaultBaud.ToString())
            with { Tip = "现场那台是 38400。协议 §1 写的出厂值：TTL 口 38400，RS485 口 9600——选错了点「连接」会两档都试" },
        Field.Sel(FieldProtocol, "协议", Rd105Protocol.Options, DefaultProtocol)
            with { Tip = "接 TTL 口选 ASCII，接 RS485 口选 Modbus-RTU（协议 §2）。选错了点「连接」会换着试一遍并告诉你该改成什么" },
        Field.Num(FieldAddress, "站号", DefaultAddress, "", 1, 247, 1)
            with { Tip = "只在 Modbus-RTU 下用，出厂 1（协议 §3.5.3）" },
        Field.Num(FieldPeriod, "控制周期", 500, "ms", 200, 5000, 100)
    })
    {
        Tip = "帧格式固定 8N1。点「测试连接」会真的开口子读型号与固件版本——" +
              "读得到才算通，读不到会把串口报的原话显示出来。"
    };

    public ParameterSchema ConfigSchema { get; } = new(new[]
    {
        Field.Num(FieldOverUp, "超温上限", 180, "℃", -50, 300, 1),
        Field.Num(FieldOverLow, "超温下限", -40, "℃", -80, 100, 1),
        Field.Num(FieldMaxCurrent, "最大电流", 5, "A", 0.5, 20, 0.1)
    })
    {
        Tip = "超温与限流写进温控器自己的保护寄存器，断了通信也照样生效——" +
              "这不是上位机的软限值，是设备的硬保护。安全监控的默认限值也从这里推。"
    };

    /// <summary>指令是静态声明的，没连硬件也要能编辑配方（§3.3）。</summary>
    public IReadOnlyList<CommandDescriptor> Commands { get; } = CommandSpecs.Temperature;

    public async Task<ProbeResult> ProbeAsync(ParameterSet connection, CancellationToken ct)
    {
        string? noReply = null;
        Rd105Link? link = null;
        try
        {
            link = LinkFactory(connection);
            link.Open();
            var (model, firmware, contMode) = await link.Controller.ReadDeviceInfoAsync(ct)
                                                        .ConfigureAwait(false);
            var mode = contMode is { } m ? $"，CONTMODE={m}" : "";
            return new ProbeResult(true, $"{LinkName(connection)} 已响应：{model}{mode}")
            {
                Firmware = firmware,
                Serial = model,
                DetectedChannels = 2
            };
        }
        catch (TecProtocolException ex)
        {
            // 口子开了但对面不按协议说话——多半是波特率不对或者接到了别的设备上
            return new ProbeResult(false, $"通了但应答看不懂（多半是波特率不对或接到了别的设备）：{ex.Message}");
        }
        catch (TimeoutException ex)
        {
            noReply = NoReply(connection, ex);
        }
        catch (Exception ex)
        {
            return new ProbeResult(false, $"打不开 {connection.Str(FieldPort, DefaultPort)}：{ex.Message}");
        }
        finally
        {
            link?.Dispose();      // 串口让出来——下面换着试要重开同一个口
        }

        // 配置的协议 / 波特率没应答：换着试一遍，告诉人「改成什么就通了」
        var scan = await Rd105ProbeScan.RunAsync((p, b) =>
        {
            var alt = connection.Clone();
            alt[FieldProtocol] = p;
            alt[FieldBaud] = b.ToString();
            var l = LinkFactory(alt);
            return (l, l);
        }, connection.Str(FieldProtocol, DefaultProtocol), (int)connection.Num(FieldBaud, DefaultBaud),
           connection.Int(FieldAddress, DefaultAddress), "协议", ct).ConfigureAwait(false);
        return new ProbeResult(false, $"{noReply}；{scan}");
    }

    /// <summary>「RD105 串口 COM7 @ 38400（ASCII）」——报错和回显里指着说的那条链路。</summary>
    internal static string LinkName(ParameterSet cn)
        => $"RD105 串口 {cn.Str(FieldPort, DefaultPort)} @ {(int)cn.Num(FieldBaud, DefaultBaud)}" +
           $"（{Rd105Protocol.Short(cn.Str(FieldProtocol, DefaultProtocol), cn.Int(FieldAddress, DefaultAddress))}）";

    /// <summary>
    /// 发了指令没回音时该怎么说：口、波特率和三个最常见的原因一起摆出来。
    /// 协议 §1：TTL 口出厂 38400，RS485 口出厂 9600。
    /// </summary>
    internal static string NoReply(ParameterSet cn, Exception ex)
        => $"{LinkName(cn)} 无应答" +
           $"（{ex.Message.TrimEnd('\n', '\r')}）——查：①是不是接 RD105 的那一路串口；" +
           "②波特率：现场那台是 38400（协议 §1 写的出厂值 TTL 口 38400、RS485 口出厂 9600，两档都会试）；" +
           "③接线（TTL 的 TX/RX 要交叉、485 的 A/B、共地）";

    public async Task<IDeviceSession> OpenAsync(ParameterSet connection, DriverContext ctx, CancellationToken ct)
    {
        var link = LinkFactory(connection);
        try
        {
            link.Open();
            var session = new Rd105Session(link, ctx, connection);
            // 开机第一件事是把超温与限流写进设备的保护寄存器，再谈控温
            await session.ApplyProtectionAsync(ct).ConfigureAwait(false);
            return session;
        }
        catch (TimeoutException ex)
        {
            link.Dispose();
            throw new InvalidOperationException(NoReply(connection, ex), ex);
        }
        catch
        {
            link.Dispose();
            throw;
        }
    }
}
