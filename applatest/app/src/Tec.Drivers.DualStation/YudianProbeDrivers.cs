using System.Globalization;
using Tec.Driver.Abi;
using Tec.Drivers.Rd105;
using Tec.Drivers.Rd105.Modbus;
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
        defaultPort: "COM8",         // 现场那台：宇电模块在 CH344 的 A 口
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
        defaultPort: "COM8",         // 现场那台模块 CH1/CH3 就是线性电流口——跟 Tr 探头共一台、共一个口
        description: "复合电极经 pH 变送器（4~20mA）接宇电模块的线性电流输入；" +
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

    /// <summary>
    /// 「宇电通道」的缺省项：探头绑在系统通道几，就读模块上**第几路同类输入**——
    /// 工位 A（通道 1）读第 1 路测温口、工位 B（通道 2）读第 2 路。哪几路是测温口从模块自己的
    /// InP 读出来：现场那台 CH1/CH3 配成线性电流（给 pH 变送器）、CH2/CH4 是 Pt100，
    /// 于是 Tr 探头 A → CH2、B → CH4，pH 电极 A → CH1、B → CH3，两支探头拖上台面不用各改一遍。
    /// </summary>
    public const string ChFollowWell = "跟工位走";

    /// <summary>下拉项。数字项存的就是 "1"~"4"，老台面里存的数值 1 读出来也是 "1"，照样对得上。</summary>
    public static readonly IReadOnlyList<string> ChOptions = new[] { ChFollowWell, "1", "2", "3", "4" };

    /// <summary>
    /// 「输入规格」：现场那台 J7 模块四路 InP 全是 51（4~20mA，说明书 §5.2 标注 J4 电流模块专用），
    /// Pt100 接上去读不了——参数出厂配错了。模块没有面板，改 InP 只能走总线（说明书 §7：06 功能码，
    /// 写入次数不限，只受 Loc 约束）。这一项让用户明确说「把我手选的那一路写成 Pt100」，缺省照模块现状不写。
    /// 写在开口子时做：写 06 → 重读 InP 核对 → 记日志；Loc 锁着写入就如实拒绝。
    /// </summary>
    public const string FieldInp = "输入规格";
    public const string InpKeep = "照模块的 InP（不写）";
    public const string InpWritePt100 = "写成 Pt100（InP=21）";
    public const string InpWritePt100x100 = "写成 Pt100 两位小数（InP=22）";
    /// <summary>探头要是热电偶不是热阻（两根线，IN 正 COM 负），规格该是 K——J7 是热偶/热阻通用口，两种都能接。</summary>
    public const string InpWriteK = "写成 K 型热电偶（InP=0）";
    public static readonly IReadOnlyList<string> InpOptionsThermal = new[] { InpKeep, InpWritePt100, InpWritePt100x100, InpWriteK };

    /// <summary>
    /// 一路报「断线/超量程」时该查什么——说明书 §2.4.5 J7 接法、那句「PT100 输入需要先接好线再重新上电」、
    /// §8「PV 窗口闪烁 = 输入有问题」。现场第三眼：InP 写成 21 之后四路全报断线，多半就是没重新上电；
    /// 第四眼：四路一个样（连没接探头的都一样）、原始值压在量程下限之下。
    /// </summary>
    public const string FaultHint =
        "①每改一次 InP 都要把模块断电重上电（说明书原话「PT100 输入需要先接好线再重新上电」）；" +
        "②看模块自己的面板：那一路 PV 闪就是输入有问题（§8），面板正常而这里不正常才是程序的事；" +
        "③接线（§2.4.5）：Pt100 三线制颜色相同（阻值小）的两根接 IN 和 COM、剩下一根接 RT；两线制在 IN–COM 之间跨短接片；" +
        "④用万用表量探头：同色两根之间 ≈0 Ω，任一根对单独那根室温下 Pt100 ≈110 Ω（Pt1000 ≈1100 Ω 这台 J7 不认、热电偶只有几欧、断线无穷大）；" +
        "⑤探头要是热电偶不是热阻，「输入规格」要写成 K 型（IN 正、COM 负）";

    private readonly YudianKind _kind;
    private readonly TagDescriptor _tag;

    protected YudianProbeDriverBase(string id, string name, string icon, YudianKind kind,
                                    TagDescriptor tag, string defaultPort, string description)
    {
        _kind = kind;
        _tag = tag;
        SerialFactory = cn => SerialPortPool.Rent(cn.Str(FieldPort, defaultPort), (int)cn.Num(FieldBaud, 19200));
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
        var cfg = new List<FieldSpec>
        {
            Field.Sel(FieldModuleCh, "宇电通道", ChOptions, ChFollowWell) with
            {
                Tip = "这支探头接在模块的第几路输入上（RT/IN/COM 1~4）。缺省「跟工位走」：插在工位 A" +
                      "（通道 1）读模块上第 1 路同类输入、工位 B（通道 2）读第 2 路——哪几路同类从模块自己的" +
                      " InP 读出来。接得不一样就手选实际接的那一路；选错了开机会被指出来，并说明能读的是哪几路"
            }
        };
        if (kind == YudianKind.Thermal)
            cfg.Add(Field.Sel(FieldInp, "输入规格", InpOptionsThermal, InpKeep) with
            {
                Tip = "模块上那一路的输入规格（InP）不是 Pt100 时，选「写成 Pt100」再点「连接」：程序把手选的" +
                      "那一路写成 Pt100（功能码 06），读回核对后才用。要先在「宇电通道」里手选那一路——" +
                      "「跟工位走」不知道该写哪一路。缺省照模块现状，什么都不写。Loc 锁着写入会如实拒绝"
            });
        ConfigSchema = new ParameterSchema(cfg)
        {
            Tip = "标度按模块自己的 InP / 定标寄存器换算，接错种类（温度口插 pH 表）开机就会被指出来。" +
                  "「测试连接」会把模块四路各是什么口、原始值多少都摆出来，对接线就看它。"
        };
    }

    /// <summary>「输入规格」选了写：返回要写的 InP 代码；照模块现状（缺省）返回 null。</summary>
    internal static ushort? ResolveInpWrite(ParameterSet config)
    {
        var raw = config.Str(FieldInp, InpKeep).Trim();
        if (raw.Length == 0 || raw == InpKeep) return null;
        var i = raw.IndexOf("InP=", StringComparison.OrdinalIgnoreCase);
        if (i >= 0)
        {
            var digits = new string(raw[(i + 4)..].TakeWhile(char.IsDigit).ToArray());
            if (ushort.TryParse(digits, out var code)) return code;
        }
        throw new InvalidOperationException($"「{FieldInp}」填的是「{raw}」，只认下拉里那几项");
    }

    /// <summary>
    /// 定这支探头读模块的第几路：手选 1~4 就是那一路；「跟工位走」返回 null，等开口子读到模块的
    /// InP 之后按「第几路同类输入」定（见 YudianProbeSession.InitAsync）。
    /// 模块只有 4 路——绑在第 5 路以后又没手选、或者绑了不止一个通道，如实拒绝，不绕回去猜一路。
    /// </summary>
    internal static int? ResolveModuleChannel(ParameterSet config, IReadOnlyList<int> wells)
    {
        var raw = config.Str(FieldModuleCh, ChFollowWell).Trim();
        if (raw.Length == 0 || raw == ChFollowWell)
        {
            if (wells.Count != 1)
                throw new InvalidOperationException(
                    $"「{FieldModuleCh}」是「{ChFollowWell}」，可这支探头绑了 {wells.Count} 个通道，定不出该读哪一路——请手选 1~4");
            var well = wells[0];
            if (well is >= 1 and <= 4) return null;
            throw new InvalidOperationException(
                $"「{FieldModuleCh}」是「{ChFollowWell}」，可这支探头绑在通道 {well} 上——模块只有 4 路，第 5 路以后请手选 1~4");
        }
        var digits = raw.StartsWith("CH", StringComparison.OrdinalIgnoreCase) ? raw[2..] : raw;
        if (double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            && n == Math.Round(n) && n is >= 1 and <= 4)
            return (int)n;
        throw new InvalidOperationException($"「{FieldModuleCh}」填的是「{raw}」，只认 1~4 或「{ChFollowWell}」");
    }

    public DriverInfo Info { get; }
    public ParameterSchema ConnectionSchema { get; }
    public ParameterSchema ConfigSchema { get; }

    /// <summary>探头只采集；pH 的采集指令由会话认领，不在这里声明新指令。</summary>
    public IReadOnlyList<CommandDescriptor> Commands { get; } = Array.Empty<CommandDescriptor>();

    /// <summary>
    /// 测试用：换掉串口池，假从站/假总线整链回归。
    /// 兜底口名是这台驱动的缺省（COM8）——不是写死的 COM4：刚拖上台面、属性栏还没打开过的探头，
    /// 连接参数是空的，从前就按 COM4 去开，属性栏里明明显示 COM8（Xvfb 截图踩到）。
    /// </summary>
    public Func<ParameterSet, SharedSerial> SerialFactory { get; set; }

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

            // 四路各是什么口、读数多少都摆出来：现场对「探头接在第几路」就靠这一眼，
            // 手摸一下探头看哪路的数在动，比翻接线照片靠谱。别的种类的口（pH 那两路）
            // 不算错——同一台模块两种口混着用是正常接法；一路能读的都没有才算不通
            var now = await client.ReadAsync(ct).ConfigureAwait(false);
            var readout = $"宇电模块已响应：{Readout(id, now)}";
            var kindName = YudianClient.KindName(_kind);
            var hint = _kind == YudianKind.Thermal
                ? $"。要把某一路改成 Pt100：「{FieldModuleCh}」手选那一路、「{FieldInp}」选「{InpWritePt100}」再点「连接」"
                : "";
            // 模块参数原样带上——原始寄存器比任何解读都可靠，拿它去对手册
            if (id.UsableChannels.Count == 0)
                return new ProbeResult(false, $"{readout}——四路里没有一路是{kindName}口，这支探头读不了它{hint}｜模块参数：{id.Dump}");

            // 能读的几路全报断线/超量程：链路是通的，但一个数都拿不到——把该查的说在这里，别让人对着「已连接」发呆
            var allFault = id.UsableChannels.All(n => now[n - 1].SensorFault);
            var fault = allFault
                ? $"；能读的几路全报断线/超量程——{FaultHint}" +
                  (id.AnyFaultLatched ? "；AAF 里「输入故障报警不自动复位」开着（bit0=1），断线标志锁住要手动清（说明书 §5.3）" : "")
                : "";
            return new ProbeResult(true, $"{readout}——{kindName}口是 {id.UsableList}{fault}｜模块参数：{id.Dump}") { DetectedChannels = 1 };
        }
        catch (Exception ex)
        {
            // 串口层的错（拔插过 USB 转串的「函数不正确」、被占着、口没了）翻成该怎么办；协议层的照原话
            return new ProbeResult(false, ex is IOException or UnauthorizedAccessException
                ? SerialFault.Explain(ex, link?.PortName ?? connection.Str(FieldPort, ""))
                : ex.Message);
        }
        finally
        {
            link?.Dispose();
        }
    }

    /// <summary>
    /// 「CH1 4~20mA（InP=51，J4） 原始值 -1999、CH2 Pt100（InP=21） 24.6 ℃、…」——探测应答里那一串：
    /// 每一路是什么口照模块的 InP 印；能读的带读数（小数位跟模块的标度走）；读不了的把 PV 寄存器原样印出来，
    /// 让人自己看它像不像温度（246 像 24.6 ℃，-1999 / 断线就不是）。
    /// </summary>
    internal string Readout(YudianIdentity id, YudianReading[] now)
    {
        var parts = new string[4];
        for (var i = 0; i < 4; i++)
        {
            var c = id.Channels[i];
            var x = now[i];
            var decimals = c.Divisor is { } d && d >= 1 ? (int)Math.Round(Math.Log10(d)) : 1;
            var fault = x.SensorFault ? "，断线/超量程" : "";
            var value = !c.Enabled ? ""
                : !c.Usable ? $" 原始值 {x.Raw}{fault}"
                : x.SensorFault ? $" 断线/超量程（{FaultDetail(c, x, decimals)}）"
                : x.Value is { } v ? " " + v.ToString($"F{decimals}", CultureInfo.InvariantCulture) + (_tag.Unit.Length > 0 ? $" {_tag.Unit}" : "")
                : " 无读数";
            parts[i] = $"CH{i + 1} {c.TypeName}{value}";
        }
        return string.Join("、", parts);
    }

    /// <summary>
    /// 「原始值 -20215 = -202.15 ℃，低于量程下限 -200.00」——断线/超量程那一路的原始值拿说明书 §1.3 的量程对一下：
    /// 压在下限之下 / 顶在上限之上是模块量到了不像样的东西；落在量程内的多半是标志锁着没清。
    /// </summary>
    internal string FaultDetail(YudianChannelSetup c, YudianReading x, int decimals)
    {
        if (x.Value is not { } v) return $"原始值 {x.Raw}";
        var unit = _tag.Unit.Length > 0 ? $" {_tag.Unit}" : "";
        var shown = $"原始值 {x.Raw} = {v.ToString($"F{decimals}", CultureInfo.InvariantCulture)}{unit}";
        if (YudianClient.RangeOf(c.Inp) is not var (lo, hi)) return shown;
        string F(double d) => d.ToString($"F{decimals}", CultureInfo.InvariantCulture);
        // 现场两眼：-202.15（下限之下）是 RT 与 IN 短在一起时量到 ≈0 Ω；311.11（上限之上）是模块量到了
        // 开路一样大的电阻——线断、没接探头，或探头根本不是 Pt100（Pt1000 阻值是它 10 倍）
        return v < lo ? $"{shown}，低于量程下限 {F(lo)}：模块量到的电阻接近 0——短路、RT 与 IN 接错位，或探头是热电偶"
             : v > hi ? $"{shown}，高于量程上限 {F(hi)}：模块量到的电阻像开路——线断、没接探头，或探头不是 Pt100（Pt1000 阻值是 10 倍，J7 不支持）"
             : $"{shown}，在量程内——标志可能锁着没清";
    }

    public async Task<IDeviceSession> OpenAsync(ParameterSet connection, DriverContext ctx, CancellationToken ct)
    {
        // 先把「读第几路」「要不要写 InP」定下来——配置说不通就别去占串口。跟工位走的这里是 null，开口子读到 InP 再定
        var moduleCh = ResolveModuleChannel(ctx.Config, ctx.ChannelNumbers);
        var inpWrite = _kind == YudianKind.Thermal ? ResolveInpWrite(ctx.Config) : null;
        if (inpWrite is not null && moduleCh is null)
            throw new InvalidOperationException(
                $"「{FieldInp}」选了写，可「{FieldModuleCh}」是「{ChFollowWell}」——不知道该写哪一路。先手选 1~4 那一路再连");
        var link = SerialFactory(connection);
        try
        {
            link.Open();
            var client = new YudianClient(new ModbusRtuClient(
                link.Transport, (byte)connection.Num(FieldAddr, 1), busLock: link.BusLock));
            var session = new YudianProbeSession(link, client, _kind, _tag, ctx,
                moduleChannel: moduleCh,
                period: TimeSpan.FromMilliseconds(Math.Clamp(connection.Num(FieldPeriod, 1000), 200, 5000)))
            {
                InpWrite = inpWrite
            };
            await session.InitAsync(ct).ConfigureAwait(false);
            return session;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            link.Dispose();
            throw new InvalidOperationException(SerialFault.Explain(ex, link.PortName ?? connection.Str(FieldPort, "")), ex);
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
    private readonly int? _explicitCh;      // 手选的那一路；null = 跟工位走，InitAsync 里按模块的 InP 定
    private int _moduleCh;                  // 最终读的那一路（1~4），InitAsync 之后才有
    private readonly TimeSpan _period;
    private readonly Broadcast<Sample> _out = new();
    private readonly ProbeSensor[] _sensors;
    private DeviceState _state = DeviceState.Connected;
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;
    private int _fails;
    private bool _faultHinted;

    internal YudianProbeSession(SharedSerial link, YudianClient client, YudianKind kind,
                                TagDescriptor tag, DriverContext ctx, int? moduleChannel, TimeSpan period)
    {
        _link = link;
        _client = client;
        _kind = kind;
        _tag = tag;
        _ctx = ctx;
        _explicitCh = moduleChannel;
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

    /// <summary>这支探头最终读的那一路（1~4）。InitAsync 之前是 0。</summary>
    public int ModuleChannel => _moduleCh;

    /// <summary>用户在「输入规格」里定的：开口子时把手选那一路的 InP 写成它。null = 照模块现状不写。</summary>
    public ushort? InpWrite { get; init; }

    /// <summary>
    /// 开机自检：读模块身份，定下自己读哪一路并核对种类与标度——接反了直接开不了，
    /// 报错里点名模块上能读的是哪几路，改成哪一路一眼就知道。
    /// 用户明确要写 InP 的，先写、读回核对，再按读回的身份走同一套核对。
    /// </summary>
    public async Task InitAsync(CancellationToken ct)
    {
        var id = await _client.InitAsync(_kind, ct).ConfigureAwait(false);
        var kindName = YudianClient.KindName(_kind);
        var field = YudianProbeDriverBase.FieldModuleCh;
        _ctx.Log?.Invoke("info", $"{InstanceId} 宇电模块参数：{id.Dump}");

        if (_explicitCh is { } n)
        {
            if (InpWrite is { } want) id = await WriteInpAsync(id, n, want, ct).ConfigureAwait(false);

            var setup = id.Channels[n - 1];
            if (!setup.Enabled)
                throw new InvalidOperationException(
                    $"宇电模块的 CH{n} 是关闭的（In=0）——模块上{kindName}口是 {id.UsableList}；查「{field}」配置或模块参数");
            if (setup.Problem is { } p)
            {
                var fix = _kind == YudianKind.Thermal && InpWrite is null
                    ? $"；要把 CH{n} 改成 Pt100，「{YudianProbeDriverBase.FieldInp}」选「{YudianProbeDriverBase.InpWritePt100}」再连"
                    : "";
                throw new InvalidOperationException(
                    $"宇电模块 CH{n}：{p}——模块上{kindName}口是 {id.UsableList}，把「{field}」改成那一路（或选「{YudianProbeDriverBase.ChFollowWell}」）{fix}");
            }
            _moduleCh = n;
        }
        else
        {
            // 跟工位走：绑在系统通道 k 就读模块上第 k 路同类输入。哪几路同类从模块的 InP 读出来——
            // CH1/CH3 线性、CH2/CH4 Pt100 的那台，Tr 探头 A → CH2、B → CH4
            var k = _ctx.ChannelNumbers[0];
            var usable = id.UsableChannels;
            if (usable.Count < k)
                throw new InvalidOperationException(
                    $"「{field}」是「{YudianProbeDriverBase.ChFollowWell}」：通道 {k} 要读模块上第 {k} 路{kindName}口，" +
                    $"可模块上{kindName}口只有 {usable.Count} 路（{id.UsableList}）——查模块的 InP，或手选一路");
            _moduleCh = usable[k - 1];
            _ctx.Log?.Invoke("info", $"{InstanceId} 「{field}」跟工位走：通道 {k} → CH{_moduleCh}（模块上第 {k} 路{kindName}口，全部{kindName}口 {id.UsableList}）");
        }

        if (id.WriteLocked)
            _ctx.Log?.Invoke("warn", $"{InstanceId} 宇电模块 Loc 锁着写入（只读不受影响，部署改参数时注意）");
    }

    /// <summary>
    /// 把 CH n 所在那一组的 InP 写成 want（用户在「输入规格」里定的），写完重读身份核对。
    /// 已经是了就不写；Loc 锁着如实拒绝；写了读回还不是，也如实报——不信应答只信读回。
    /// </summary>
    private async Task<YudianIdentity> WriteInpAsync(YudianIdentity id, int n, ushort want, CancellationToken ct)
    {
        var setup = id.Channels[n - 1];
        var fieldInp = YudianProbeDriverBase.FieldInp;
        if (!setup.Enabled)
            throw new InvalidOperationException($"宇电模块的 CH{n} 是关闭的（In=0），「{fieldInp}」写不了它——先把模块上这一路打开");
        if (setup.Group is < 1 or > 4)
            throw new InvalidOperationException($"宇电模块 CH{n} 的 In={id.RawIn[n - 1]}，组号不是 1~4，不知道该写哪一组的 InP");
        if (setup.Inp == want)
        {
            _ctx.Log?.Invoke("info", $"{InstanceId} 「{fieldInp}」要写 InP={want}，CH{n}（组 {setup.Group}）已经是 {YudianClient.InpName(want)}，不用写");
            return id;
        }
        if (id.WriteLocked)
            throw new InvalidOperationException(
                $"宇电模块 Loc={id.Loc} 锁着写入，「{fieldInp}」改不了 CH{n} 的 InP（现在是 {setup.TypeName}）——先解锁再连");

        await _client.WriteInpAsync(setup.Group, want, ct).ConfigureAwait(false);
        var after = await _client.InitAsync(_kind, ct).ConfigureAwait(false);
        var got = after.Channels[n - 1].Inp;
        if (got != want)
            throw new InvalidOperationException(
                $"「{fieldInp}」把 CH{n}（组 {setup.Group}）的 InP 写成 {want} 了，读回来却是 {got}——写没生效，查模块的 Loc / 手册里 InP 的地址");
        _ctx.Log?.Invoke("warn", $"{InstanceId} 「{fieldInp}」：CH{n}（组 {setup.Group}）的 InP 由 {setup.Inp} 写成 {want}（{YudianClient.InpName(want)}），读回核对一致。" +
                                 "说明书 §2.4.5：「PT100 输入需要先接好线再重新上电」——请把模块断电重上电一次，再看读数");
        return after;
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
            if (x.SensorFault && !_faultHinted)
            {
                // 每次进入断线只说一遍：说清原始值和该查什么；恢复了再断再说
                _faultHinted = true;
                _ctx.Log?.Invoke("warn", $"{InstanceId} 宇电模块 CH{_moduleCh} 断线/超量程（原始值 {x.Raw}），读数按 Bad 发——{YudianProbeDriverBase.FaultHint}");
            }
            else if (!x.SensorFault && _faultHinted)
            {
                _faultHinted = false;
                _ctx.Log?.Invoke("info", $"{InstanceId} 宇电模块 CH{_moduleCh} 传感器恢复");
            }
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
            {
                var why = ex is IOException or UnauthorizedAccessException ? SerialFault.Explain(ex, _link.PortName) : ex.Message;
                _ctx.Log?.Invoke("warn", $"{InstanceId} 宇电模块读失败：{why}（连续失败只报第一次）");
            }
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
