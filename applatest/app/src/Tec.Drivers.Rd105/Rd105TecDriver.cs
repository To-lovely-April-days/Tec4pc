using TecControl.Core.Comm;
using TecControl.Core.Protocol;
using Tec.Driver.Abi;

namespace Tec.Drivers.Rd105;

/// <summary>
/// TEC 温控器（RD105 通讯协议，光测未来）。真机驱动——不是仿真。
///
/// 名字按协议取，不按型号：TecControl.Core 说的是 RD105 那套 ASCII
/// （TC1:TG=?@），跟广州荣硕的 TTD-7008 不是一回事——那台走 Modbus TCP/RTU、
/// 8 路输入 8 路输出。两者以后要各写各的驱动。
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
    public const string FieldParity = "校验";
    public const string FieldPeriod = "控制周期";
    public const string FieldOverUp = "超温上限";
    public const string FieldOverLow = "超温下限";
    public const string FieldMaxCurrent = "最大电流";

    /// <summary>测试连接用的链路工厂。回归测试换成假串口，不必插硬件。</summary>
    public Func<ParameterSet, Rd105Link> LinkFactory { get; set; } = Rd105Link.Serial;

    public DriverInfo Info { get; } = new(DriverId, "TEC 温控器（RD105）", "光测未来", "1.0.0")
    {
        ChannelsPerDevice = 1,
        SimulatorIncluded = false,
        IconKey = "reactor2",
        Description = "真机：RD105 ASCII 协议。一台温控器带一个反应通道——TC1 测釜内 Tr，TC2 测夹套 Tj。",
        Capabilities = new[] { nameof(ITemperatureControl), nameof(ITemperatureTuning) }
    };

    public ParameterSchema ConnectionSchema { get; } = new(new[]
    {
        Field.Text(FieldPort, "串口", "COM3", "Windows 上形如 COM3；Linux 上形如 /dev/ttyUSB0"),
        Field.Sel(FieldBaud, "波特率", new[] { "9600", "19200", "38400", "57600", "115200" }, "38400"),
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
        Rd105Link? link = null;
        try
        {
            link = LinkFactory(connection);
            link.Open();
            var (model, firmware, contMode) = await link.Controller.ReadDeviceInfoAsync(ct)
                                                        .ConfigureAwait(false);
            var mode = contMode is { } m ? $"，CONTMODE={m}" : "";
            return new ProbeResult(true, $"{connection.Str(FieldPort, "COM3")} 已响应：{model}{mode}")
            {
                Firmware = firmware,
                Serial = model,
                DetectedChannels = 1
            };
        }
        catch (TecProtocolException ex)
        {
            // 口子开了但对面不按协议说话——多半是波特率不对或者接到了别的设备上
            return new ProbeResult(false, $"通了但应答看不懂：{ex.Message}");
        }
        catch (Exception ex)
        {
            return new ProbeResult(false, $"打不开 {connection.Str(FieldPort, "COM3")}：{ex.Message}");
        }
        finally
        {
            link?.Dispose();
        }
    }

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
        catch
        {
            link.Dispose();
            throw;
        }
    }
}
