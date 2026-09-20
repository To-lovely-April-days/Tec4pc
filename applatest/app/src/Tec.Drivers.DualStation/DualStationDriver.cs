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
        /// <summary>ASCII（TTL 口）还是 Modbus-RTU（RS485 口），见 Rd105Protocol。</summary>
        public const string ProtoRd105 = "RD105协议";
        /// <summary>Modbus-RTU 的站号（协议 §3.5.3 ADDRESS，出厂 1）；ASCII 不用。</summary>
        public const string AddrRd105 = "RD105站号";
        public const string HasIo = "电加热切换";
        public const string PortIo = "IO8R串口";
        public const string AddrIo = "IO8R站号";
        public const string BaudIo = "IO8R波特率";
        public const string Tick = "模块轮询周期";

        /// <summary>TEC 反向输出加热启不启用。「不启用」（默认）= 所有加热都走电加热棒。</summary>
        public const string TecHeat = "TEC加热";
        public const string Threshold = "电加热切换阈值";
        public const string Hysteresis = "回切滞回";
        public const string Feedback = "切换反馈";
        public const string DoA = "切换DO·工位A";
        public const string DoB = "切换DO·工位B";
        public const string DiA = "反馈DI·工位A";
        public const string DiB = "反馈DI·工位B";
        /// <summary>冷热判定死区（K）。只在「TEC 加热」不启用时用到。</summary>
        public const string Band = "热源死区";
    }

    /// <summary>
    /// 连接参数的缺省值——按现场那台机器填的（用户定的）：RD105 在 CH344 的 C 口（COM7）、
    /// IO8R 在 B 口（COM10），两条都走 RS485 = Modbus-RTU、9600、站号 1。
    /// COM 号是这台 PC 上的枚举结果，换台电脑可能变，属性栏里改就是了；这里只是少填几步。
    /// </summary>
    public static class Defaults
    {
        public const string PortRd105 = "COM7";
        public const int BaudRd105 = 9600;
        public const string ProtoRd105 = Rd105Protocol.Modbus;
        public const int AddrRd105 = 1;
        public const string PortIo = "COM10";
        public const int BaudIo = 9600;
        public const int AddrIo = 1;
    }

    /// <summary>测试用的链路工厂：两条串口全换成假设备，整机逻辑不插硬件就能回归。</summary>
    public Func<ParameterSet, DuoLinks> LinksFactory { get; set; } = DuoLinks.Serial;

    public DriverInfo Info { get; } = new(DriverId, "双工位反应主机", "光测未来 + 艾莫迅", "1.0.0")
    {
        ChannelsPerDevice = 2,
        SimulatorIncluded = false,
        IconKey = "rd105",
        Description = "RD105 双路夹套控温 + IO8R 热源切换（TEC ⇄ 电加热）。" +
                      "釜内 Tr 与 pH 由宇电探头设备各自采集，插到工位上即归该路。",
        Capabilities = new[] { nameof(ITemperatureControl), nameof(IRefluxControl), nameof(ITemperatureTuning) }
    };

    public ParameterSchema ConnectionSchema { get; } = new(new[]
    {
        Field.Port(Fields.PortRd105, "RD105 串口", Defaults.PortRd105, "TEC 温控器，8N1。下拉里是当前检测到的串口（现场那台在 CH344 的 C 口）"),
        Field.Sel(Fields.BaudRd105, "RD105 波特率", new[] { "9600", "19200", "38400", "57600", "115200" }, Defaults.BaudRd105.ToString())
            with { Tip = "出厂值看接的是哪个口：TTL 口 38400，RS485 口 9600（协议 §1）" },
        Field.Sel(Fields.ProtoRd105, "RD105 协议", Rd105Protocol.Options, Defaults.ProtoRd105)
            with { Tip = "接 TTL 口选 ASCII，接 RS485 口选 Modbus-RTU（协议 §2）。选错了点「连接」会换着试一遍并告诉你该改成什么" },
        Field.Num(Fields.AddrRd105, "RD105 站号", Defaults.AddrRd105, "", 1, 247, 1)
            with { Tip = "只在 Modbus-RTU 下用，出厂 1（协议 §3.5.3）" },
        Field.Sel(Fields.HasIo, "电加热切换（IO8R）", new[] { "有", "无" }, "有"),
        Field.Port(Fields.PortIo, "IO8R 串口", Defaults.PortIo, "艾莫迅 JY-MODBUS-IO8R。下拉里是当前检测到的串口（现场那台在 CH344 的 B 口）"),
        Field.Num(Fields.AddrIo, "IO8R 站号", Defaults.AddrIo, "", 1, 247, 1),
        Field.Sel(Fields.BaudIo, "IO8R 波特率", new[] { "4800", "9600", "19200", "38400", "57600", "115200" }, Defaults.BaudIo.ToString()),
        Field.Num(Rd105TecDriver.FieldPeriod, "控制周期", 500, "ms", 200, 5000, 100),
        Field.Num(Fields.Tick, "模块轮询周期", 1000, "ms", 100, 5000, 100)
    })
    {
        Tip = "主机只管自己的两条串口（RD105 与 IO8R）。釜内 Tr 与 pH 的串口在" +
              "各自探头设备的属性里——把「Tr 温度探头」「pH 玻璃电极」" +
              "插到工位上即可。IO8R 的站号/波特率看模块上的拨码（出厂 9600 / 1 号）；" +
              "部署前必须用厂家工具把 IO8R 的总线错误模式从「保持」改成「复位」（需求 §7）。"
    };

    public ParameterSchema ConfigSchema { get; } = new(new[]
    {
        Field.Num(Rd105TecDriver.FieldOverUp, "超温上限", 180, "℃", -50, 300, 1),
        Field.Num(Rd105TecDriver.FieldOverLow, "超温下限", -40, "℃", -80, 100, 1),
        Field.Num(Rd105TecDriver.FieldMaxCurrent, "最大电流", 5, "A", 0.5, 20, 0.1),
        // TEC 的反向输出加热启不启用。默认「不启用」：TEC 只当冷源，所有加热都走电加热棒，
        // 继电器按「这一刻该升温还是该降温」切。启用后才回到「只有超过阈值才切电加热」那套
        Field.Sel(Fields.TecHeat, "TEC 加热", new[] { "不启用", "启用" }, "不启用"),
        // 上限 90 是死的（用户定的：只能比 90 小）——TEC 通路的工程上限
        Field.Num(Fields.Threshold, "电加热切换阈值", 90, "℃", 40, 90, 1),
        Field.Num(Fields.Hysteresis, "回切滞回", 5, "K", 2, 20, 1),
        // 冷热判定的死区：|目标 − 夹套| 在这个带子里就当作「到了」，保持当前热源不动。
        // 没有它，恒温时目标在实测上下擦来擦去，继电器会跟着抖
        Field.Num(Fields.Band, "热源切换死区", 2, "K", 0.5, 10, 0.5),
        Field.Sel(Fields.Feedback, "切换反馈", new[] { "无", "有" }, "无"),
        Field.Num(Fields.DoA, "切换 DO·工位 A", 0, "", 0, 7, 1),
        Field.Num(Fields.DoB, "切换 DO·工位 B", 1, "", 0, 7, 1),
        Field.Num(Fields.DiA, "反馈 DI·工位 A", 0, "", 0, 7, 1),
        Field.Num(Fields.DiB, "反馈 DI·工位 B", 1, "", 0, 7, 1)
    })
    {
        Tip = "超温上限就是电加热模式的最高温度——写进 RD105 自己的保护寄存器，断了通信照样生效，" +
              "任何目标温度都不得超过它。「TEC 加热」默认不启用：TEC 只当冷源，升温一律切电加热棒，" +
              "「切换阈值」这时不参与判断（只剩「夹套凉到阈值−滞回 才准接回 TEC」这条保护）；" +
              "启用后才是「目标超过阈值才切电加热」。切换阈值只能往下调（≤ 90 ℃）。「切换反馈」接了电加热" +
              "接触器辅助触点才选「有」——没接选「有」会让每次切换都等 2 秒然后报失败。"
    };

    /// <summary>指令是静态声明的，没连硬件也要能编辑配方（§3.3）。这台真机没有搅拌，不认领搅拌指令。</summary>
    public IReadOnlyList<CommandDescriptor> Commands { get; } = CommandSpecs.Temperature;

    public async Task<ProbeResult> ProbeAsync(ParameterSet connection, CancellationToken ct)
    {
        var lines = new List<string>();
        var okRd = false;
        var fw = "";
        string? noReply = null;

        DuoLinks? links = null;
        try
        {
            links = LinksFactory(connection);
            links.OpenAll();

            try
            {
                var (model, firmware, _) = await links.Rd105.Controller.ReadDeviceInfoAsync(ct).ConfigureAwait(false);
                lines.Add($"RD105：{model} 已响应（{links.RdName}）");
                fw = firmware;
                okRd = true;
            }
            catch (TecProtocolException ex) { lines.Add($"RD105：通了但应答看不懂（多半是波特率不对或接到了别的设备）——{ex.Message}"); }
            catch (TimeoutException ex) { noReply = links.NoReply(ex); }
            catch (Exception ex) { lines.Add($"RD105：{ex.Message}"); }

            if (links.IoOpenError is { } ioWhy)
                lines.Add($"{ioWhy}（开机会照常，但电加热不可用）");
            else if (links.Io is { } io)
            {
                try { await io.ReadRelaysAsync(ct).ConfigureAwait(false); lines.Add("IO8R：已响应"); }
                catch (Exception ex) { lines.Add($"IO8R：{ex.Message}（开机会照常，但电加热不可用）"); }
            }

            lines.Add("Tr / pH 探头各有自己的串口，在探头设备上分别测试");
        }
        catch (Exception ex)
        {
            return new ProbeResult(false, $"打不开链路：{ex.Message}");
        }
        finally
        {
            links?.Dispose();      // 串口让出来——下面换着试要重开同一个口
        }

        if (noReply is not null)
        {
            // 配置的协议 / 波特率没应答：换着试一遍，告诉人「改成什么就通了」
            var proto = connection.Str(Fields.ProtoRd105, Defaults.ProtoRd105);
            var baud = (int)connection.Num(Fields.BaudRd105, Defaults.BaudRd105);
            var station = connection.Int(Fields.AddrRd105, Defaults.AddrRd105);
            var scan = await Rd105ProbeScan.RunAsync((p, b) =>
            {
                var alt = connection.Clone();
                alt[Fields.ProtoRd105] = p;
                alt[Fields.BaudRd105] = b.ToString();
                var l = LinksFactory(alt);
                return (l.Rd105, l);
            }, proto, baud, station, "RD105 协议", ct).ConfigureAwait(false);
            lines.Insert(0, $"{noReply}；{scan}");
        }

        return new ProbeResult(okRd, string.Join("；", lines))
        {
            Firmware = fw,
            DetectedChannels = 2
        };
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
        catch (TimeoutException ex)
        {
            // 口子开了但 RD105 一声不吭：把口、波特率、该查什么一起说出来，
            // 别只留一句「指令无应答：TC1:OVERTEMPUP=…」让人对着猜
            await session.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException(links.NoReply(ex), ex);
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
