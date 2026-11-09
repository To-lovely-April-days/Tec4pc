using System.IO.Ports;
using Tec.Driver.Abi;
using Tec.Drivers.DualStation;
using Tec.Drivers.Rd105;
using Tec.Drivers.Rd105.Modbus;

namespace Tec.Drivers.Aqmd;

/// <summary>
/// 搅拌电机驱动器（成都爱控 AQMD2408BLS-M，Modbus-RTU over RS485）：一个工位一台，
/// 绑到哪个工位那一路就有了搅拌能力（IStirrer）。串口写在**自己的**属性里（用户定的：每台设备的串口跟着设备走）。
///
/// 现场（2026-11-09 用户用串口工具和本程序实测）：两台都答 9600 bps、**无校验 + 2 停止位**（8N2）。驱动器自己报的是
/// 偶校验 + 1 停止位（寄存器 0x0092 = 2）——8E1 和 8N2 都是 11 位一个字符，CH344 这边按 8N2 收发实测通，
/// 8N1（10 位）不通；照现场填，偶校验那档留在下拉里；站号都是 1；工位 A 在 COM11、工位 B 在 COM14
/// （两块 CH344 剩下的两个口，和口的字母正好相反：COM11 是第一块的 C、COM14 是第一块的 D——所以按工位记，别按字母记）。
/// 型号名读回「AQMD2408BLS-M2」v1.3，极数没配（0x0073 = 0）、额定电流还是出厂 7 A——这两项做成配置，连接时写进驱动器。
///
/// 驱动器一侧的语义：速度闭环目标是**换向频率**（Hz），不是 rpm；rpm = Hz ÷ 极数 × 20 ÷ 减速比（手册 p10）。
/// 极数不知道就换算不了，所以极数是硬要求：驱动器里有就用驱动器里的，没有就得在属性栏填。
/// </summary>
public sealed class AqmdStirrerDriver : IDeviceDriver, IStationPortDefaults
{
    public const string DriverId = "tec.stir.aqmd2408";

    // ── 连接参数 ───────────────────────────────────────────────────
    public const string FieldPort = "串口";
    public const string FieldAddr = "站号";
    public const string FieldBaud = "波特率";
    public const string FieldParity = "校验";
    public const string FieldStop = "停止位";
    public const string FieldPeriod = "轮询周期";

    public const string ParityEven = "偶校验";
    public const string ParityOdd = "奇校验";
    public const string ParityNone = "无校验";
    public static readonly string[] ParityOptions = { ParityNone, ParityEven, ParityOdd };
    public static readonly string[] StopOptions = { "2", "1" };
    public static readonly string[] BaudOptions = { "9600", "19200", "38400", "57600", "115200" };

    /// <summary>缺省照现场：工位 A 的那台在 COM11（工位 B 的填 COM14）。</summary>
    public const string DefaultPort = "COM11";
    public const string DefaultPortB = "COM14";
    public const int DefaultAddr = 1;
    public const int DefaultBaud = 9600;
    /// <summary>现场实测通的帧格式：无校验 + 2 停止位（见类注释）。</summary>
    public const string DefaultParity = ParityNone;
    public const string DefaultStop = "2";

    // ── 设备配置 ───────────────────────────────────────────────────
    /// <summary>电机极个数。0 = 照驱动器里存的（0x0073）；驱动器里也是 0 就开不了，报错里说怎么数。</summary>
    public const string FieldPoles = "电机极数";
    /// <summary>电机额定电流（A）。0 = 不改驱动器里的；>0 连接时写进 0x006A / 0x006B 并存储。</summary>
    public const string FieldRatedCurrent = "电机额定电流";
    public const string FieldMaxRpm = "转速上限";
    /// <summary>0 = 自动：按驱动器测速下限 10 Hz 折算（4 极电机 50 rpm、8 极 25 rpm）。</summary>
    public const string FieldMinRpm = "转速下限";
    public const string FieldDirection = "转向";
    public const string DirForward = "正转";
    public const string DirReverse = "反转";
    public static readonly string[] DirectionOptions = { DirForward, DirReverse };
    /// <summary>没人给斜坡时（面板「立即」、紧急转速）0 → 转速上限 用多少秒。</summary>
    public const string FieldRamp = "缺省加减速时间";
    /// <summary>通讯中断停止时间（0x0095）：主站多久不问驱动器就自己刹车。现场读回 0 = 断了也不停。</summary>
    public const string FieldWatchdog = "通讯中断停机";
    /// <summary>堵转停止时间（0x008E）：堵住多久驱动器自己停。现场读回 0 = 不保护，桨叶卡住就按最大负载电流顶着。</summary>
    public const string FieldStallStop = "堵转停机时间";
    public const string Keep = "不改";
    public const string Off = "关闭";
    public static readonly string[] WatchdogOptions = { Keep, Off, "5 s", "10 s", "30 s", "60 s" };
    public static readonly string[] StallStopOptions = { Keep, Off, "1 s", "3 s", "10 s" };

    public const string TagRpm = "rpm";
    public const string TagCurrent = "stirI";
    public const string TagFault = "stirFault";

    /// <summary>测试用：换掉串口池，假从站整链回归。兜底口名是驱动的缺省（刚拖上台面连接参数是空的）。</summary>
    public Func<ParameterSet, SharedSerial> SerialFactory { get; set; }
        = cn => SerialPortPool.Rent(cn.Str(FieldPort, DefaultPort), (int)cn.Num(FieldBaud, DefaultBaud),
                                    ParityOf(cn.Str(FieldParity, DefaultParity)), StopBitsOf(cn.Str(FieldStop, DefaultStop)));

    public DriverInfo Info { get; } = new(DriverId, "搅拌电机驱动器", "成都爱控", "1.0.0")
    {
        ChannelsPerDevice = 0,              // 绑到主机的工位上，不自带通道（跟宇电探头一样）
        SimulatorIncluded = false,
        HasSettings = true,                 // 设备参数面板：实时量、极数 / 额定电流 / 保护直接写驱动器、数圈测试、学习相序
        IconKey = "stirdrv",
        Description = "AQMD2408BLS-M 无刷电机驱动器，RS485 Modbus-RTU。一个工位一台，接到哪个工位那一路就能设转速、看实测转速与电流；" +
                      "配方里的「搅拌」步骤由它执行。",
        Capabilities = new[] { nameof(IStirrer) }
    };

    public ParameterSchema ConnectionSchema { get; } = new(new[]
    {
        Field.Port(FieldPort, "串口", DefaultPort,
                   "下拉里是这台机器当前检测到的串口。现场：工位 A 的驱动器在 COM11、工位 B 的在 COM14（两块 CH344 剩下的那两个口）。" +
                   "两台也可以并在一条 485 上共一个口，那样站号要不同"),
        Field.Num(FieldAddr, "站号", DefaultAddr, "", 1, 247, 1)
            with { Tip = "驱动器寄存器 0x009C，出厂 1（驱动器允许到 254，Modbus 主站这边只认 1~247）。现场两台各走一个口，都是 1" },
        Field.Sel(FieldBaud, "波特率", BaudOptions, DefaultBaud.ToString())
            with { Tip = "出厂 9600（现场实测就是）。驱动器那边改了波特率这里跟着改" },
        Field.Sel(FieldParity, "校验", ParityOptions, DefaultParity)
            with { Tip = "现场实测「无校验 + 2 停止位」通。驱动器自己报的是偶校验 + 1 停止位（0x0092 = 2），两种都是 11 位一个字符，" +
                         "CH344 这边按无校验 2 停止位收发没问题；8N1（10 位）不通。「测试连接」会把各档换着试" },
        Field.Sel(FieldStop, "停止位", StopOptions, DefaultStop)
            with { Tip = "现场实测 2 停止位才通；手册写的是 1。「测试连接」两档都会试并告诉你哪档通" },
        Field.Num(FieldPeriod, "轮询周期", 500, "ms", 200, 5000, 100)
            with { Tip = "读实测转速 / 电流 / 故障码的周期。驱动器的「通讯中断停机」要是开着，这个周期必须比它短得多" }
    })
    {
        Tip = "9600 · 8 位 · 无校验 · 2 停止位 · 站号 1 是现场两台实测通的参数。驱动器要先用按键切到 485 通讯方式" +
              "（黄灯灭、绿灯闪；黄灯常亮是数字/模拟方式，按住按键 1 s 松开切换）。「测试连接」真的开口子读型号、极数、学习状态。"
    };

    public ParameterSchema ConfigSchema { get; } = new(new[]
    {
        Field.Num(FieldPoles, "电机极数", 0, "", 0, 64, 1)
            with { Tip = "电机铭牌上的极数（不是极对数）。转速 = 换向频率 ÷ 极数 × 20，不填就换算不出来。0 = 照驱动器里存的（0x0073）；" +
                         "现场那两台驱动器里是 0。两个地方都是 0 也连得上，只是这一路没有搅拌能力——到「设备参数」面板点「数圈测试」" +
                         "让它按 20 Hz 转一分钟，数圈数：极数 = 400 ÷ 圈数（100 圈 = 4 极、50 圈 = 8 极），在面板「电机参数」里写进驱动器，再点「连接」。" +
                         "这里填了连接时写进驱动器并存储，读回核对一致才用" },
        Field.Num(FieldRatedCurrent, "电机额定电流", 0, "A", 0, 10, 0.1)
            with { Tip = "电机铭牌额定电流。手册要求配成与电机一致，否则调速不稳、烧保险丝；现场那两台还是出厂 7 A。" +
                         "0 = 不改驱动器里的；填了连接时把额定电流与最大负载电流都写成它并存储" },
        Field.Num(FieldMaxRpm, "转速上限", 1000, "rpm", 10, 20000, 10)
            with { Tip = "软件允许的最高转速，按桨叶和釜定（配方里的转速范围也从这里来）。驱动器自己的最大换向频率另外钳一道" },
        Field.Num(FieldMinRpm, "转速下限", 0, "rpm", 0, 5000, 10)
            with { Tip = "0 = 自动：按驱动器测速下限 10 Hz 折算（4 极 50 rpm、8 极 25 rpm）——低于它闭环没有反馈，转速不稳。要更高就填" },
        Field.Sel(FieldDirection, "转向", DirectionOptions, DirForward)
            with { Tip = "正转写正的换向频率、反转写负的。桨叶方向不对就切这个，不用换线" },
        Field.Num(FieldRamp, "缺省加减速时间", 5, "s", 0.5, 600, 0.5)
            with { Tip = "0 → 转速上限 用多少秒。配方「搅拌」步骤带「到达用时」时按步骤的来，这里只管没给的（面板「立即」、紧急转速）" },
        Field.Sel(FieldWatchdog, "通讯中断停机", WatchdogOptions, Keep)
            with { Tip = "驱动器寄存器 0x0095：上位机多久不问它就自己刹车。现场读回是 0（断了也不停，搅拌保持——跟温控器断线保持输出一个道理）。" +
                         "选了连接时写进去并存储；「不改」= 照驱动器里存的" },
        Field.Sel(FieldStallStop, "堵转停机时间", StallStopOptions, Keep)
            with { Tip = "驱动器寄存器 0x008E：堵转多久自己停。现场读回是 0（不保护，卡住就按最大负载电流一直顶着）。选了连接时写进去并存储" }
    })
    {
        Tip = "极数是硬要求——没有它软件连「设 300 rpm 该写多少 Hz」都算不出来。额定电流、通讯中断停机、堵转停机都写的是驱动器自己的寄存器，" +
              "写完读回核对、记日志；「不改」就一个字不写。"
    };

    /// <summary>驱动器认领配方里的「搅拌」指令（声明在 ABI 的 CommandSpecs.Stirring，跟仿真机同一条）。</summary>
    public IReadOnlyList<CommandDescriptor> Commands { get; } = CommandSpecs.Stirring;

    // ── 串口跟工位走（IStationPortDefaults）：绑到 CH1 就是 COM11、CH2 就是 COM14 ──
    public string PortFieldKey => FieldPort;
    public string? DefaultPortFor(int channelNumber) => channelNumber switch { 1 => DefaultPort, 2 => DefaultPortB, _ => null };

    public static Parity ParityOf(string s) => s switch
    {
        ParityOdd => Parity.Odd,
        ParityEven => Parity.Even,
        _ => Parity.None
    };

    public static StopBits StopBitsOf(string s) => s.Trim() == "1" ? StopBits.One : StopBits.Two;

    /// <summary>「COM11 @ 9600 8N2，站号 1」——报错和回显里指着说的那条链路。</summary>
    public static string LinkName(ParameterSet cn)
        => $"{cn.Str(FieldPort, DefaultPort)} @ {(int)cn.Num(FieldBaud, DefaultBaud)} " +
           $"{TecControl.Core.Comm.SerialPortTransport.FrameName(ParityOf(cn.Str(FieldParity, DefaultParity)), StopBitsOf(cn.Str(FieldStop, DefaultStop)))}" +
           $"，站号 {cn.Int(FieldAddr, DefaultAddr)}";

    /// <summary>「通讯中断停机」「堵转停机时间」下拉 → 要写的 ×0.1 s 值；「不改」= null。</summary>
    internal static ushort? TenthsOf(string choice)
    {
        var s = choice.Trim();
        if (s.Length == 0 || s == Keep) return null;
        if (s == Off) return 0;
        var digits = new string(s.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        return double.TryParse(digits, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sec)
            ? (ushort)Math.Clamp(Math.Round(sec * 10), 0, 255)
            : throw new InvalidOperationException($"「{choice}」不是下拉里的项");
    }

    public async Task<ProbeResult> ProbeAsync(ParameterSet connection, CancellationToken ct)
    {
        var (ok, text) = await TryIdentifyAsync(connection, ct).ConfigureAwait(false);
        if (ok) return new ProbeResult(true, text) { DetectedChannels = 1 };
        if (text.StartsWith("串口", StringComparison.Ordinal)) return new ProbeResult(false, text);   // 口都开不了，换帧格式也没用

        // 配置的帧格式没应答：换着试一遍，告诉人「改成什么就通了」。只试校验位 × 停止位，波特率是现场自己改的那就该现场知道
        var tried = new List<string>();
        foreach (var (parity, stop) in Alternatives(connection.Str(FieldParity, DefaultParity), connection.Str(FieldStop, DefaultStop)))
        {
            var alt = connection.Clone();
            alt[FieldParity] = parity;
            alt[FieldStop] = stop;
            var (altOk, altText) = await TryIdentifyAsync(alt, ct).ConfigureAwait(false);
            if (altOk)
                return new ProbeResult(false, $"{text}；换着试了一遍：{parity} / {stop} 停止位有应答（{altText}）——把「{FieldParity}」改成「{parity}」、「{FieldStop}」改成「{stop}」再点「连接」");
            tried.Add($"{parity}/{stop}停止位");
        }
        return new ProbeResult(false, $"{text}；换着试了 {string.Join("、", tried)} 也都没应答——查：①是不是接搅拌驱动器的那一路串口；" +
                                      "②驱动器是不是在 485 通讯方式（黄灯要灭，按住按键 1 s 松开切换）；③站号（出厂 1）；④A/B 线有没有接反");
    }

    private static IEnumerable<(string Parity, string Stop)> Alternatives(string parity, string stop)
    {
        foreach (var p in new[] { ParityNone, ParityEven, ParityOdd })
            foreach (var s in new[] { "2", "1" })
                if (p != parity || s != stop) yield return (p, s);
    }

    /// <summary>按一套连接参数开口子读身份 + 电机参数 + 状态，把现场要看的全摆出来；读不到就说为什么。</summary>
    private async Task<(bool Ok, string Text)> TryIdentifyAsync(ParameterSet cn, CancellationToken ct)
    {
        SharedSerial? link = null;
        try
        {
            link = SerialFactory(cn);
            link.Open();
            var client = new AqmdClient(new ModbusRtuClient(link.Transport, (byte)cn.Num(FieldAddr, DefaultAddr), busLock: link.BusLock)
                { TimeoutRetries = 0, CorruptRetries = 0 });      // 探测本身一试一报，链路层别叠重试（慢）
            var id = await client.ReadIdentityAsync(ct).ConfigureAwait(false);
            var motor = await client.ReadMotorAsync(ct).ConfigureAwait(false);
            var status = await client.ReadStatusAsync(ct).ConfigureAwait(false);
            var sys = await client.ReadSystemAsync(ct).ConfigureAwait(false);
            return (true, Readout(cn, id, motor, status, sys));
        }
        catch (OperationCanceledException) { throw; }
        catch (TimeoutException ex)
        {
            return (false, $"{LinkName(cn)} 无应答（{ex.Message.TrimEnd('\n', '\r')}）");
        }
        catch (ModbusException ex)
        {
            return (false, $"{LinkName(cn)} 通了但应答看不懂（多半是校验位 / 停止位不对，或接到了别的设备）：{ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (false, SerialFault.Explain(ex, link?.PortName ?? cn.Str(FieldPort, DefaultPort)));
        }
        catch (Exception ex)
        {
            return (false, $"{LinkName(cn)}：{ex.Message}");
        }
        finally
        {
            link?.Dispose();      // 串口让出来——换着试要重开同一个口；真正打开设备也要它
        }
    }

    /// <summary>
    /// 「AQMD2408BLS-M2 v1.3 已响应（COM11 @ 9600 8E2，站号 1）：已学习 · 极数 4 · 减速比 1.0 · 额定电流 7.00 A · 最大换向频率 2000 Hz ·
    /// 停着，电流 0.00 A，故障码 0 · 驱动器里 9600 偶校验 + 1 停止位、通讯中断停机 关、堵转停机 关」——
    /// 现场对配置就看这一眼；该补的（极数 0、未学习、额定电流还是出厂值）直接点出来。
    /// </summary>
    internal static string Readout(ParameterSet cn, AqmdIdentity id, AqmdMotorParams m, AqmdStatus st, AqmdSystemParams sys)
    {
        var notes = new List<string>();
        if (!m.Learned) notes.Add($"**未学习相序**（0x0075 = 0）——{AqmdRegisters.FaultAdvice(1)}");
        if (m.Poles <= 0) notes.Add($"**极数没配**（0x0073 = 0）——连接前在属性栏「{FieldPoles}」填电机铭牌极数，不然转速换算不出来");
        if (Math.Abs(m.RatedCurrentA - 7.0) < 0.001) notes.Add($"额定电流还是出厂 7.00 A——按电机铭牌在「{FieldRatedCurrent}」填上");
        if (st.Fault != 0) notes.Add($"故障码 {st.Fault}（{AqmdRegisters.FaultText(st.Fault)}）——{AqmdRegisters.FaultAdvice(st.Fault)}");
        var running = Math.Abs(st.FrequencyHz) > 0.05
            ? $"在转（{st.FrequencyHz:0.0} Hz{(m.Poles > 0 ? $" ≈ {Math.Abs(AqmdRegisters.HzToRpm(st.FrequencyHz, m.Poles, m.GearRatio)):0} rpm" : "")}）"
            : "停着";
        var main = $"{id.Display} 已响应（{LinkName(cn)}）：{(m.Learned ? "已学习" : "未学习")} · 极数 {m.Poles} · 减速比 {m.GearRatio:0.0} · " +
                   $"额定电流 {m.RatedCurrentA:0.00} A · 最大换向频率 {m.MaxFrequencyHz:0} Hz · {running}，电流 {st.CurrentA:0.00} A，故障码 {st.Fault} · " +
                   $"驱动器里 {sys.Baud} {sys.FrameName}、站号 {sys.SlaveAddress}、通讯中断停机 {(sys.WatchdogSeconds > 0 ? $"{sys.WatchdogSeconds:0.#} s" : "关")}、" +
                   $"堵转停机 {(sys.StallStopSeconds > 0 ? $"{sys.StallStopSeconds:0.#} s" : "关")}";
        return notes.Count == 0 ? main : $"{main}｜要补：{string.Join("；", notes)}";
    }

    public async Task<IDeviceSession> OpenAsync(ParameterSet connection, DriverContext ctx, CancellationToken ct)
    {
        // 配置说不通就别去占串口
        var cfgPoles = ctx.Config.Int(FieldPoles, 0);
        if (cfgPoles < 0 || cfgPoles > 64 || cfgPoles % 2 != 0)
            throw new InvalidOperationException($"「{FieldPoles}」填的是 {cfgPoles}——极数是偶数（2、4、6、8……），0 = 照驱动器里的");
        var watchdog = TenthsOf(ctx.Config.Str(FieldWatchdog, Keep));
        var stall = TenthsOf(ctx.Config.Str(FieldStallStop, Keep));

        var link = SerialFactory(connection);
        try
        {
            link.Open();
            var client = new AqmdClient(new ModbusRtuClient(link.Transport, (byte)connection.Num(FieldAddr, DefaultAddr), busLock: link.BusLock));
            var session = new AqmdStirrerSession(link, client, ctx, connection,
                period: TimeSpan.FromMilliseconds(Math.Clamp(connection.Num(FieldPeriod, 500), 200, 5000)))
            {
                WatchdogTenths = watchdog,
                StallStopTenths = stall
            };
            await session.InitAsync(ct).ConfigureAwait(false);
            return session;
        }
        catch (TimeoutException ex)
        {
            link.Dispose();
            throw new InvalidOperationException($"{LinkName(connection)} 无应答（{ex.Message.TrimEnd('\n', '\r')}）——查串口是不是接搅拌驱动器的那一路、" +
                                                "驱动器是不是在 485 通讯方式（黄灯灭）、站号、A/B 线", ex);
        }
        catch (ModbusException ex)
        {
            link.Dispose();
            throw new InvalidOperationException($"{LinkName(connection)}：{AqmdRegisters.ExplainWriteRejected(ex)}", ex);
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
