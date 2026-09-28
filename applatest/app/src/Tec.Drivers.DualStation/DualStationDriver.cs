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
        /// <summary>
        /// 键沿用「电加热切换阈值」（台面上存过的配置不作废），0336 起标签叫「TEC 接入上限」：夹套高过它 TEC 断开、只加热棒；
        /// 「TEC 加热」启用时兼作「目标高于它才切电加热」的阈值。
        /// </summary>
        public const string Threshold = "电加热切换阈值";
        /// <summary>键沿用「回切滞回」，标签「接入滞回」：夹套凉到 上限 − 滞回 才（再）接 TEC。</summary>
        public const string Hysteresis = "回切滞回";
        public const string Feedback = "切换反馈";
        public const string DoA = "切换DO·工位A";
        public const string DoB = "切换DO·工位B";
        public const string DiA = "反馈DI·工位A";
        public const string DiB = "反馈DI·工位B";
        /// <summary>升降温死区（K）。只在「TEC 加热」不启用时用到：目标比当前温度低不到这个数不算降温（下发时先合加热棒；冷水机已关时定成升温挡）。</summary>
        public const string Band = "热源死区";
        /// <summary>
        /// 冷水机开没开（手动开关，软件不能自动）：「已开」（默认，0334 用户定的——降温的时候冷水机一定开着；0336 起已开 = 双向挡：
        /// 一个 PID 两边出力，继电器跟功率符号走）/「已关」（升温挡 / 降温挡：降温目标不接 TEC、光靠自然凉）。TEC 没有冷却水不能开。
        /// 属性栏上改了当拍生效：在控的路按新规矩重新定挡。
        /// </summary>
        public const string Chiller = "冷水机";
        /// <summary>
        /// TEC 功率线经不经 IO8R 的继电器。现场电气定的（2026-09-21）：经——DO6 = 工位 A、
        /// DO7 = 工位 B，闭合 TEC 才有电。从前这两只没人合，现场「降温没反应」就是它。
        /// 用户定的：**不常合**——不控温就断着，打开温控后按升温 / 降温只合该合的那一只。
        /// </summary>
        public const string TecRelay = "TEC功率继电器";
        public const string TecDoA = "TEC功率DO·工位A";
        public const string TecDoB = "TEC功率DO·工位B";
    }

    /// <summary>
    /// 连接参数的缺省值——按现场那台机器填的（用户定的）：RD105 在 CH344 的 C 口（COM7）、
    /// IO8R 在 B 口（COM10），两条都走 RS485 = Modbus-RTU、站号 1；RD105 波特率 38400（用户定的，
    /// 不是协议 §1 写的 485 口出厂 9600），IO8R 9600。
    /// COM 号是这台 PC 上的枚举结果，换台电脑可能变，属性栏里改就是了；这里只是少填几步。
    /// </summary>
    public static class Defaults
    {
        public const string PortRd105 = "COM7";
        public const int BaudRd105 = 38400;
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
        Description = "RD105 双路夹套控温 + IO8R 热源切换（电加热棒升温、TEC 制冷：冷水机已开是双向挡，一个 PID 两边出力、继电器跟功率符号走）。" +
                      "釜内 Tr 与 pH 由宇电探头设备各自采集，插到工位上即归该路。",
        Capabilities = new[] { nameof(ITemperatureControl), nameof(IRefluxControl), nameof(ITemperatureTuning) }
    };

    public ParameterSchema ConnectionSchema { get; } = new(new[]
    {
        Field.Port(Fields.PortRd105, "RD105 串口", Defaults.PortRd105, "TEC 温控器，8N1。下拉里是当前检测到的串口（现场那台在 CH344 的 C 口）"),
        Field.Sel(Fields.BaudRd105, "RD105 波特率", new[] { "9600", "19200", "38400", "57600", "115200" }, Defaults.BaudRd105.ToString())
            with { Tip = "现场那台是 38400。协议 §1 写的出厂值：TTL 口 38400，RS485 口 9600——选错了点「连接」会两档都试" },
        Field.Sel(Fields.ProtoRd105, "RD105 协议", Rd105Protocol.Options, Defaults.ProtoRd105)
            with { Tip = "接 TTL 口选 ASCII，接 RS485 口选 Modbus-RTU（协议 §2）。选错了点「连接」会换着试一遍并告诉你该改成什么" },
        Field.Num(Fields.AddrRd105, "RD105 站号", Defaults.AddrRd105, "", 1, 247, 1)
            with { Tip = "只在 Modbus-RTU 下用，出厂 1（协议 §3.5.3）" },
        Field.Sel(Fields.HasIo, "电加热切换（IO8R）", new[] { "有", "无" }, "有"),
        Field.Port(Fields.PortIo, "IO8R 串口", Defaults.PortIo, "艾莫迅 JY-MODBUS-IO8R。下拉里是当前检测到的串口（现场那台在 CH344 的 B 口）"),
        Field.Num(Fields.AddrIo, "IO8R 站号", Defaults.AddrIo, "", 1, 247, 1),
        Field.Sel(Fields.BaudIo, "IO8R 波特率", new[] { "4800", "9600", "19200", "38400", "57600", "115200" }, Defaults.BaudIo.ToString()),
        Field.Num(Rd105TecDriver.FieldPeriod, "控制周期", 500, "ms", 200, 5000, 100),
        Field.Num(Fields.Tick, "模块轮询周期", 1000, "ms", 100, 5000, 100),
        Field.Num(Rd105TecDriver.FieldTrGrace, "Tr 丢失宽限", 30, "s", 0, 120, 5)
            with { Tip = "釜内（串级）控温时宇电釜内 Tr 断了（10 s 没新数）：夹套设定先钳到釜内设定保持这么久，Tr 回来就接着串级，" +
                         "回不来才停控、继电器全断。0 = 断了当拍停控（0328 之前的行为）" }
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
        // 控温回路闭在哪里（用户定的：搬到上位机——釜内串级、按温度分段的增益表、前馈都在那边；
        // 温控器 PID 只有夹套单环、出厂参数，「釜内」目标也只是把同一个数写给夹套）
        Field.Sel(Rd105TecDriver.FieldControl, "控温方式", Rd105TecDriver.ControlOptions, Rd105TecDriver.ControlHost)
            with { Tip = Rd105TecDriver.HostControlTip },
        // 极性按现场实测（2026-09-23）：降温时输出读回 +90、升温 −90 → TEC 侧「反向」、加热棒吃负占空比。
        // 接反了上位机回路的跑飞检测两三分钟内会停控并说明，改这两项再开
        Field.Sel(Rd105TecDriver.FieldInvert, "TEC 输出反向", Rd105TecDriver.InvertOptions, Rd105TecDriver.InvertYes)
            with { Tip = "上位机 PID 下用：回路算出「要加热」写正占空比还是写负。这台实测降温 +90、升温 −90，所以缺省反向；跑飞检测报「方向接反」就改它" },
        Field.Sel(Rd105TecDriver.FieldHeaterSign, "加热棒占空比", Rd105TecDriver.HeaterSignOptions, Rd105TecDriver.HeaterNeg)
            with { Tip = "上位机 PID 下用：加热棒那只 SSR 接在哪个引脚（固件按占空比符号路由）。这台实测电加热侧读回 −90，所以缺省负" },
        // 加热棒是交流 + 固态继电器：出厂 10 Hz 功率只有 10 % 一档，保温段来回擦。缺省 1 Hz（2 % 一档）；
        // 加热棒功率明显大于保温所需的机器选 0.5 Hz
        Field.Sel(Rd105TecDriver.FieldFpwm, "PWM 输出频率", Rd105TecDriver.FpwmOptions, "1 Hz") with { Tip = Rd105TecDriver.FpwmTip },
        // TEC 的反向输出加热启不启用。默认「不启用」（这台机器 TEC 的反向那一极接的是加热棒 SSR，TEC 只能制冷）：
        // 升温系统 = 加热棒（负占空比、按功率调）、降温系统 = TEC（正占空比、按功率调）；冷水机已开双向挡（0336）、已关按方向定挡（0333）。
        // 启用后才回到「只有超过阈值才切电加热」那套
        Field.Sel(Fields.TecHeat, "TEC 加热", new[] { "不启用", "启用" }, "不启用"),
        // 冷水机是手动开关、软件不能自动。缺省「已开」（0334，用户定的：降温的时候冷水机一定开着；0336 起冷水机直开）= 双向挡：
        // 一个 PID 两边出力、继电器跟功率符号走。「已关」= 升温挡 / 降温挡：降温目标不接 TEC、光靠自然凉
        Field.Sel(Fields.Chiller, "冷水机", new[] { "已开", "已关" }, "已开")
            with { Tip = "缺省「已开」= 双向挡：一个 PID 两边出力，正给加热棒、负给 TEC 制冷（参数用加热棒那张表，TEC 那半轴按该行「加热比」放大），" +
                         "IO8R 继电器跟着功率符号走（同号连续 3 s 才扳一次）——放热、吸热随时接得上。前提是你真把冷水机开了：TEC 没有冷却水会干烧，软件看不见水。" +
                         "改成「已关」= 升温挡 / 降温挡：升温、保温只用加热棒，降温目标不接 TEC、只关加热棒自然凉，到了目标加热棒接手。" +
                         "属性栏上改了当拍生效，在控的路按新规矩重新定挡，不用断开重连" },
        // 上限 90 是死的（用户定的：只能比 90 小）——TEC 模块自身耐温的工程上限，等厂家给模块热端最高温度再定。
        // 键沿用「电加热切换阈值」/「回切滞回」（存过的配置不作废），0336 起标签按它真正管的事叫
        Field.Num(Fields.Threshold, "TEC 接入上限", 90, "℃", 40, 90, 1)
            with { Tip = "夹套温度高过它 TEC 就不接（TEC 模块自身耐温）：双向挡里 TEC 功率线断开、只加热棒出力、功率下限钳到 0，" +
                         "凉到 上限 − 接入滞回 再放开；降温挡里同样凉到那条线才合 TEC。只能往下调（≤ 90 ℃）。「TEC 加热」启用时兼作「目标高于它才切电加热」的阈值" },
        Field.Num(Fields.Hysteresis, "接入滞回", 5, "K", 2, 20, 1)
            with { Tip = "夹套高过「TEC 接入上限」断开 TEC 之后，要凉到 上限 − 滞回 才再接（2 ~ 20 K）；刚下发目标时夹套高过这条线也先不接" },
        // 升降温死区：下发目标时目标比当前温度低不到这个数就不算降温（加热棒关着自然凉那几分之一度，不值得接 TEC）；
        // 双向挡里只管起步合哪只，冷水机已关时定挡；「TEC 加热」启用时不用
        Field.Num(Fields.Band, "升降温死区", 2, "K", 0.5, 10, 0.5)
            with { Tip = "下发目标时按它定起步：目标比当前温度（釜内串级看 Tr，其余看夹套）低超过这个数先合 TEC 功率线，其余先合加热棒。" +
                         "双向挡（冷水机已开）之后继电器跟功率符号走；冷水机已关时它定的就是挡位（降温挡 / 升温挡），中途温度漂动不换挡" },
        Field.Sel(Fields.Feedback, "切换反馈", new[] { "无", "有" }, "无"),
        Field.Num(Fields.DoA, "切换 DO·工位 A", 0, "", 0, 7, 1),
        Field.Num(Fields.DoB, "切换 DO·工位 B", 1, "", 0, 7, 1),
        Field.Num(Fields.DiA, "反馈 DI·工位 A", 0, "", 0, 7, 1),
        Field.Num(Fields.DiB, "反馈 DI·工位 B", 1, "", 0, 7, 1),
        // TEC 的功率线也经 IO8R 的继电器（现场电气定的：DO6 = 工位 A、DO7 = 工位 B）。
        // 不闭合 TEC 就没电——制冷、TEC 加热都不动。从前这两只没人管，现场「降温没反应」就是它。
        // 用户定的：不常合，不控温就断着；打开温控后要降温才合它、要升温合加热棒那只
        Field.Sel(Fields.TecRelay, "TEC 功率继电器", new[] { "有", "无" }, "有")
            with { Tip = "TEC 的功率线经 IO8R 继电器接通（闭合 = TEC 有电）。默认断着；要制冷才合它、要加热合加热棒那只（双向挡跟功率符号走），两只从不同时合；停控 / 安全停机断开。功率线是硬线直连、不经继电器的机器选「无」" },
        Field.Num(Fields.TecDoA, "TEC 功率 DO·工位 A", 6, "", 0, 7, 1),
        Field.Num(Fields.TecDoB, "TEC 功率 DO·工位 B", 7, "", 0, 7, 1)
    })
    {
        Tip = "超温上限就是电加热模式的最高温度——写进 RD105 自己的保护寄存器，断了通信照样生效，" +
              "任何目标温度都不得超过它。「TEC 加热」默认不启用：升温系统 = 加热棒、降温系统 = TEC 制冷。「冷水机」已开（缺省）= 双向挡：" +
              "一个 PID 两边出力（正加热棒、负 TEC），继电器跟功率符号走（同号连续 3 s 才扳），放热 / 吸热随时接得上；" +
              "「冷水机」已关 = 升温挡 / 降温挡：下发目标时按方向定挡，升温挡只用加热棒，降温挡不接 TEC 只自然凉。" +
              "「TEC 接入上限」是 TEC 模块自身耐温：夹套高过它 TEC 断开、只加热棒，凉到 上限 − 接入滞回 再接；只能往下调（≤ 90 ℃）。" +
              "「TEC 加热」启用后才是「目标超过上限才切电加热」。「切换反馈」接了电加热" +
              "接触器辅助触点才选「有」——没接选「有」会让每次切换都等 2 秒然后报失败。" +
              "TEC 功率线经 IO8R 的 DO6（A）/ DO7（B）（现场电气定的）：不控温就断着，停控 / 安全停机断开；" +
              "IO8R 打不开时这台既不能制冷也不能加热，控温目标会被拒绝。"
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

            // IO8R 不通的后果不只是没有电加热：TEC 功率线也经它的继电器（DO6/DO7，现场电气定的），
            // 接不通就连制冷也没有——探测这一步不知道设备配置，两种后果都说
            const string ioDown = "（开机会照常，但电加热不可用；TEC 功率线若也经 IO8R 继电器（现场那台：DO6/DO7），连制冷也没有）";
            if (links.IoOpenError is { } ioWhy)
                lines.Add($"{ioWhy}{ioDown}");
            else if (links.Io is { } io)
            {
                try { await io.ReadRelaysAsync(ct).ConfigureAwait(false); lines.Add("IO8R：已响应"); }
                catch (Exception ex) { lines.Add($"IO8R：{ex.Message}{ioDown}"); }
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
