using Tec.Driver.Abi;
using Tec.Drivers.Rd105;
using TecControl.Core.Protocol;

namespace Tec.Drivers.DualStation;

/// <summary>
/// 双工位反应主机（真机）：一台设备、两个通道。
/// 拓扑与全部行为规约见 docs/双工位反应主机驱动需求.md——
/// 主机自己带两条链路：RD105 两路夹套控温（TC1=A、TC2=B）与 IO8R 热源切换
/// （可没有，没有就上不了 90 ℃ 以上）。**釜内 Tr 与 pH 是独立的探头设备**
/// （宇电 J7/J4），串口在各自探头的属性里（用户定的：每台设备的串口跟着
/// 设备自己走），读数由探头会话发、主机经 IExternalReactorTemp 接住釜温。
/// </summary>
public sealed class DualStationDriver : IDeviceDriver
{
    public const string DriverId = "tec.reactor.duo";

    /// <summary>连接/配置表单的字段名，DuoLinks/DuoSession 按同一份取值。</summary>
    public static class Fields
    {
        public const string PortRd105 = "RD105串口";
        public const string BaudRd105 = "RD105波特率";
        public const string HasIo = "电加热切换";
        public const string PortIo = "IO8R串口";
        public const string AddrIo = "IO8R站号";
        public const string BaudIo = "IO8R波特率";
        public const string Tick = "模块轮询周期";

        public const string Threshold = "电加热切换阈值";
        public const string Hysteresis = "回切滞回";
        public const string Feedback = "切换反馈";
        public const string DoA = "切换DO·工位A";
        public const string DoB = "切换DO·工位B";
        public const string DiA = "反馈DI·工位A";
        public const string DiB = "反馈DI·工位B";
    }

    /// <summary>测试用的链路工厂：两条串口全换成假设备，整机逻辑不插硬件就能回归。</summary>
    public Func<ParameterSet, DuoLinks> LinksFactory { get; set; } = DuoLinks.Serial;

    public DriverInfo Info { get; } = new(DriverId, "双工位反应主机（真机）", "光测未来 + 艾莫迅", "1.0.0")
    {
        ChannelsPerDevice = 2,
        SimulatorIncluded = false,
        IconKey = "rd105",
        Description = "真机：RD105 双路夹套控温 + IO8R 热源切换（TEC ⇄ 电加热）。" +
                      "釜内 Tr 与 pH 由宇电探头设备各自采集，插到工位上即归该路。",
        Capabilities = new[] { nameof(ITemperatureControl), nameof(IRefluxControl), nameof(ITemperatureTuning) }
    };

    public ParameterSchema ConnectionSchema { get; } = new(new[]
    {
        Field.Text(Fields.PortRd105, "RD105 串口", "COM3", "TEC 温控器，8N1"),
        Field.Sel(Fields.BaudRd105, "RD105 波特率", new[] { "9600", "19200", "38400", "57600", "115200" }, "38400"),
        Field.Sel(Fields.HasIo, "电加热切换（IO8R）", new[] { "有", "无" }, "有"),
        Field.Text(Fields.PortIo, "IO8R 串口", "COM6", "艾莫迅 JY-MODBUS-IO8R"),
        Field.Num(Fields.AddrIo, "IO8R 站号", 1, "", 1, 247, 1),
        Field.Sel(Fields.BaudIo, "IO8R 波特率", new[] { "4800", "9600", "19200", "38400", "57600", "115200" }, "9600"),
        Field.Num(Rd105TecDriver.FieldPeriod, "控制周期", 500, "ms", 200, 5000, 100),
        Field.Num(Fields.Tick, "模块轮询周期", 1000, "ms", 100, 5000, 100)
    })
    {
        Tip = "主机只管自己的两条串口（RD105 与 IO8R）。釜内 Tr 与 pH 的串口在" +
              "各自探头设备的属性里——把「Tr 温度探头（真机）」「pH 玻璃电极（真机）」" +
              "插到工位上即可。IO8R 的站号/波特率看模块上的拨码（出厂 9600 / 1 号）；" +
              "部署前必须用厂家工具把 IO8R 的总线错误模式从「保持」改成「复位」（需求 §7）。"
    };

    public ParameterSchema ConfigSchema { get; } = new(new[]
    {
        Field.Num(Rd105TecDriver.FieldOverUp, "超温上限", 180, "℃", -50, 300, 1),
        Field.Num(Rd105TecDriver.FieldOverLow, "超温下限", -40, "℃", -80, 100, 1),
        Field.Num(Rd105TecDriver.FieldMaxCurrent, "最大电流", 5, "A", 0.5, 20, 0.1),
        // 上限 90 是死的（用户定的：只能比 90 小）——TEC 通路的工程上限
        Field.Num(Fields.Threshold, "电加热切换阈值", 90, "℃", 40, 90, 1),
        Field.Num(Fields.Hysteresis, "回切滞回", 5, "K", 2, 20, 1),
        Field.Sel(Fields.Feedback, "切换反馈", new[] { "无", "有" }, "无"),
        Field.Num(Fields.DoA, "切换 DO·工位 A", 0, "", 0, 7, 1),
        Field.Num(Fields.DoB, "切换 DO·工位 B", 1, "", 0, 7, 1),
        Field.Num(Fields.DiA, "反馈 DI·工位 A", 0, "", 0, 7, 1),
        Field.Num(Fields.DiB, "反馈 DI·工位 B", 1, "", 0, 7, 1)
    })
    {
        Tip = "超温上限就是电加热模式的最高温度——写进 RD105 自己的保护寄存器，断了通信照样生效，" +
              "任何目标温度都不得超过它。切换阈值只能往下调（≤ 90 ℃）。「切换反馈」接了电加热" +
              "接触器辅助触点才选「有」——没接选「有」会让每次切换都等 2 秒然后报失败。"
    };

    /// <summary>指令是静态声明的，没连硬件也要能编辑配方（§3.3）。这台真机没有搅拌，不认领搅拌指令。</summary>
    public IReadOnlyList<CommandDescriptor> Commands { get; } = CommandSpecs.Temperature;

    public async Task<ProbeResult> ProbeAsync(ParameterSet connection, CancellationToken ct)
    {
        DuoLinks? links = null;
        try
        {
            links = LinksFactory(connection);
            links.OpenAll();
            var lines = new List<string>();
            var okRd = false;
            var fw = "";

            try
            {
                var (model, firmware, _) = await links.Rd105.Controller.ReadDeviceInfoAsync(ct).ConfigureAwait(false);
                lines.Add($"RD105：{model} 已响应");
                fw = firmware;
                okRd = true;
            }
            catch (TecProtocolException ex) { lines.Add($"RD105：通了但应答看不懂——{ex.Message}"); }
            catch (Exception ex) { lines.Add($"RD105：{ex.Message}"); }

            if (links.Io is { } io)
            {
                try { await io.ReadRelaysAsync(ct).ConfigureAwait(false); lines.Add("IO8R：已响应"); }
                catch (Exception ex) { lines.Add($"IO8R：{ex.Message}（开机会照常，但电加热不可用）"); }
            }

            lines.Add("Tr / pH 探头各有自己的串口，在探头设备上分别测试");

            return new ProbeResult(okRd, string.Join("；", lines))
            {
                Firmware = fw,
                DetectedChannels = 2
            };
        }
        catch (Exception ex)
        {
            return new ProbeResult(false, $"打不开链路：{ex.Message}");
        }
        finally
        {
            links?.Dispose();
        }
    }

    public async Task<IDeviceSession> OpenAsync(ParameterSet connection, DriverContext ctx, CancellationToken ct)
    {
        var links = LinksFactory(connection);
        DuoSession session;
        try
        {
            links.OpenAll();
            session = new DuoSession(links, ctx, connection);
        }
        catch
        {
            links.Dispose();
            throw;
        }
        try
        {
            // 开机顺序：保护寄存器 → 继电器复位 TEC 侧
            await session.InitAsync(ct).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
