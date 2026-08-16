using TecControl.Core.Comm;
using TecControl.Core.Protocol;
using Tec.Driver.Abi;

namespace Tec.Drivers.Ttd7008;

/// <summary>
/// TEC 温控器 TTD-7008（RD105 协议）。真机驱动——不是仿真。
///
/// 协议、标度换算、PID、串级、自整定全在 TecControl.Core 里，那套跟着硬件一起演进；
/// 这个类只做翻译：把它的接口翻成 Tec.Driver.Abi 的能力契约，
/// 让上层照旧只问「能不能控温」，不问「是哪台机器」。
/// </summary>
public sealed class Ttd7008Driver : IDeviceDriver
{
    public const string DriverId = "tec.reactor.ttd7008";

    public const string FieldPort = "端口";
    public const string FieldBaud = "波特率";
    public const string FieldParity = "校验";
    public const string FieldPeriod = "控制周期";
    public const string FieldOverUp = "超温上限";
    public const string FieldOverLow = "超温下限";
    public const string FieldMaxCurrent = "最大电流";

    /// <summary>测试连接用的链路工厂。回归测试换成假串口，不必插硬件。</summary>
    public Func<ParameterSet, Ttd7008Link> LinkFactory { get; set; } = Ttd7008Link.Serial;

    public DriverInfo Info { get; } = new(DriverId, "TEC 温控器 TTD-7008", "Tec", "1.0.0")
    {
        ChannelsPerDevice = 2,
        SimulatorIncluded = false,
        IconKey = "reactor2",
        Description = "真机：RD105 ASCII 协议，双路 TEC 控温，支持串级（Tr）与单环（Tj）。",
        Capabilities = new[] { nameof(ITemperatureControl) }
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
    public IReadOnlyList<CommandDescriptor> Commands { get; } =
        CommandSpecs.Temperature.Concat(CommandSpecs.DeltaTCommands).ToList();

    public async Task<ProbeResult> ProbeAsync(ParameterSet connection, CancellationToken ct)
    {
        Ttd7008Link? link = null;
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
                DetectedChannels = 2
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

    public Task<IDeviceSession> OpenAsync(ParameterSet connection, DriverContext ctx, CancellationToken ct)
        => throw new NotSupportedException(
            "会话层还没接：通道拓扑（一台温控器算一个串级通道，还是两个独立通道）等确认后再写。");
}
