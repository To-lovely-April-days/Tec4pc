using Tec.Drivers.Rd105.Modbus;

namespace Tec.Drivers.DualStation.Yudian;

/// <summary>这台宇电模块该是哪种输入：J7 测温（热偶/热阻）还是 J4 线性电流（pH 变送器）。</summary>
public enum YudianKind
{
    /// <summary>AI-8848GD91J7：热电偶 / 热电阻，InP 查表定标度。</summary>
    Thermal,
    /// <summary>AI-8848GD91J4：4~20mA（或 0~20mA），ScL/ScH 定标 + dPt 定小数位。</summary>
    Linear
}

/// <summary>一路输入在初始化时查明的身份。Problem 非空表示这一路的数不能信。</summary>
public sealed record YudianChannelSetup(
    bool Enabled,          // In 个位 = 0 就是这一路关着
    int Group,             // 用的哪组输入配置（In 个位，1~4）
    int Inp,               // 那组的输入规格
    double? Divisor,       // 寄存器值 ÷ 它 = 工程值；null = 定不出标度
    double? RangeLo,       // 线性输入的定标下限（已换算）；测温型是 null
    double? RangeHi,
    string? Problem)       // 接反了 / 规格不认识……人话原因
{
    /// <summary>这一路开着、规格跟探头对得上、标度定得出——探头能读它。</summary>
    public bool Usable => Enabled && Problem is null && Divisor is not null;

    /// <summary>「Pt100（InP=21）」「4~20mA（InP=51，J4）」——报错和探测应答里指着说的那一路是什么口。</summary>
    public string TypeName => Enabled ? YudianClient.InpName(Inp) : "关";
}

/// <summary>初始化自检读回来的全套身份。开每条串口都要过这一关（需求 §4）。</summary>
public sealed record YudianIdentity(
    ushort FeatureWord,    // 2131 型号特征字。手册没给对照表，只存原始值进日志
    ushort Loc,            // 2130 参数封锁
    ushort Dpt,            // 2128 小数点位置（只影响面板显示与线性定标的隐含小数位）
    IReadOnlyList<YudianChannelSetup> Channels)
{
    /// <summary>Loc.5/6/7 任一位挂着，写入就会「应答正常但不生效」——手册点名的最难查的坑。
    /// 我们虽然只读，但部署时该把它查出来说清楚。</summary>
    public bool WriteLocked => (Loc & 0b1110_0000) != 0;

    /// <summary>探头能读的那几路（1 起的 CH 号，按序）。现场那台 CH1/CH3 是线性电流、CH2/CH4 是 Pt100，
    /// 对 Tr 探头就是 [2, 4]——「跟工位走」按这个序取第几路，不是按 CH 号硬对。</summary>
    public IReadOnlyList<int> UsableChannels
        => Channels.Select((c, i) => (c, i)).Where(x => x.c.Usable).Select(x => x.i + 1).ToList();

    /// <summary>「CH2、CH4」——报错里点名用。</summary>
    public string UsableList => UsableChannels.Count == 0 ? "没有一路" : string.Join("、", UsableChannels.Select(n => $"CH{n}"));

    /// <summary>读回来的原始寄存器：In01~04、InP1~4、ScL1~4、ScH1~4。没有说明书核对时，把它们原样摆出来比任何解读都可靠。</summary>
    public IReadOnlyList<ushort> RawIn { get; init; } = Array.Empty<ushort>();
    public IReadOnlyList<ushort> RawInp { get; init; } = Array.Empty<ushort>();
    public IReadOnlyList<ushort> RawScl { get; init; } = Array.Empty<ushort>();
    public IReadOnlyList<ushort> RawSch { get; init; } = Array.Empty<ushort>();
    /// <summary>AAF1~4（0818H）：报警自动复位选择。bit0=1 表示输入故障报警不自动复位——断线标志会锁着不清（说明书 §5.3）。</summary>
    public IReadOnlyList<ushort> RawAaf { get; init; } = Array.Empty<ushort>();

    /// <summary>「In=1/2/3/4，InP=51/51/51/51，ScL=0/0/0/0，ScH=1400/…，AAF=0/0/0/0，dPt=2，Loc=0，型号字 8848」——模块参数一览，探测应答和开机日志里带着。</summary>
    public string Dump
    {
        get
        {
            static string J(IReadOnlyList<ushort> xs) => xs.Count == 0 ? "?" : string.Join("/", xs.Select(x => ((short)x).ToString()));
            return $"In={J(RawIn)}，InP={J(RawInp)}，ScL={J(RawScl)}，ScH={J(RawSch)}，AAF={J(RawAaf)}，dPt={Dpt}，Loc={Loc}，型号字 {FeatureWord}";
        }
    }

    /// <summary>有哪一组 AAF 把「输入故障不自动复位」打开了（bit0）——那样断线标志要手动清，读数会一直 Bad。</summary>
    public bool AnyFaultLatched => RawAaf.Any(a => (a & 0x01) != 0);
}

/// <summary>
/// 一路的一次读数。Raw 是 PV 寄存器的原样（有符号）——这一路的规格认不出来时值是 null，原始数照样给，
/// 让人自己看它像不像温度。
/// SensorFault = 值不可信：换算出来的数在这种输入的量程之外（断线 / 短路时寄存器里是 311.11、−202.15
/// 这类量程外的码），或者量程定不出来而报警字里的 oral 位挂着。
/// AlarmLatched = 报警字里 oral 位挂着、值却在量程内：标志锁着没清（AAF 不自动复位，说明书 §5.3）或
/// 干扰瞬间断线又回来了——值照常用，标志单独报出来。从前只看 oral 位，标志一锁，Tr 就永远 Bad（现场踩到）。
/// </summary>
public readonly record struct YudianReading(
    double? Value, short Raw, bool SensorFault, bool AlarmHigh, bool AlarmLow, string? Problem,
    bool AlarmLatched = false);

/// <summary>
/// 宇电 AI-8848G D91（4 路）访问层。J7 / J4 除输入规格外寄存器完全一致，
/// 所以一套客户端、两种 Kind——哪台是测温哪台是 pH 由组合会话决定。
///
/// 手册里三条反直觉的坑，全都在这层消化掉：
/// 1. 寄存器按参数分块不按通道分块（读 4 路 PV 是 1536 起连读 4 个）；
/// 2. dPt 只影响面板显示，上位机读到的永远是整数，小数位自己算——
///    测温型按 InP 查表（量程带 .00 的是两位小数），线性型按 dPt；
/// 3. 报警状态一个寄存器装两路：高字节 = 奇数通道，低字节 = 偶数通道。
/// </summary>
public sealed class YudianClient
{
    // 寄存器地址（十进制，见速查手册 §7）
    private const ushort RegIn = 384;       // In01~04：个位 0 = 该路关闭，1~4 = 用哪组输入配置
    private const ushort RegPv = 1536;      // PV1~4 测量值，只读，有符号
    private const ushort RegAlarm = 1664;   // 报警状态，一寄存器两通道
    private const ushort RegInp = 2048;     // InP1~4 输入规格
    private const ushort RegScl = 2052;     // ScL1~4 线性定标下限
    private const ushort RegAaf = 2072;     // AAF1~4 报警自动复位选择（0818H）
    private const ushort RegDpt = 2128;     // dPt 小数点位置（2128..2131 一把读上来）

    private readonly ModbusRtuClient _bus;
    private YudianIdentity? _id;
    private YudianKind _kind;

    public YudianClient(ModbusRtuClient bus) => _bus = bus;

    /// <summary>初始化自检后的身份；没 Init 过就是 null。</summary>
    public YudianIdentity? Identity => _id;

    /// <summary>
    /// 初始化自检：读特征字 / Loc / dPt / 各路 In 与 InP（线性的连 ScL/ScH），
    /// 核对每一路的输入规格跟期望的模块种类对不对得上。
    /// 「温度口插了 pH 表」这种接反在这里就报出来，不等到曲线画歪了才发现。
    /// </summary>
    public async Task<YudianIdentity> InitAsync(YudianKind kind, CancellationToken ct = default)
    {
        var inRegs = await _bus.ReadHoldingRegistersAsync(RegIn, 4, ct).ConfigureAwait(false);
        var groups = await _bus.ReadHoldingRegistersAsync(RegInp, 12, ct).ConfigureAwait(false); // InP×4 + ScL×4 + ScH×4
        var glob = await _bus.ReadHoldingRegistersAsync(RegDpt, 4, ct).ConfigureAwait(false);    // dPt、2129、Loc、特征字
        var aaf = await _bus.ReadHoldingRegistersAsync(RegAaf, 4, ct).ConfigureAwait(false);     // AAF1~4：断线标志锁不锁

        var dpt = glob[0];
        var chans = new YudianChannelSetup[4];
        for (var i = 0; i < 4; i++)
        {
            var g = inRegs[i] % 10;
            if (g == 0)
            {
                chans[i] = new YudianChannelSetup(false, 0, -1, null, null, null, null);
                continue;
            }
            if (g > 4)
            {
                // 个位 5~9 不是组号——别拿它去索引，那会把 ScL 当成 InP 读
                chans[i] = new YudianChannelSetup(true, g, -1, null, null, null,
                    $"In={inRegs[i]} 的个位 {g} 不是 1~4 的组号，规格定不出来，不猜");
                continue;
            }
            var inp = groups[g - 1];
            chans[i] = Setup(kind, g, inp,
                             scl: (short)groups[4 + (g - 1)],
                             sch: (short)groups[8 + (g - 1)],
                             dpt);
        }

        _kind = kind;
        _id = new YudianIdentity(glob[3], glob[2], dpt, chans)
        {
            RawIn = inRegs,
            RawInp = groups[..4],
            RawScl = groups[4..8],
            RawSch = groups[8..12],
            RawAaf = aaf
        };
        return _id;
    }

    /// <summary>
    /// 把第 group 组（1~4）的输入规格写成 inp（功能码 06）。只在用户明确选了「写成 Pt100」时调用——
    /// 模块没有面板，现场改参数只能走总线。写完由调用方重新 InitAsync 读回核对，不信应答信读回。
    /// </summary>
    public Task WriteInpAsync(int group, ushort inp, CancellationToken ct = default)
    {
        if (group is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(group), "组号只有 1~4");
        return _bus.WriteRegisterAsync((ushort)(RegInp + group - 1), inp, ct);
    }

    /// <summary>
    /// InP 代码 → 人话。照《AI-8 系列高精度多路 PID 调节器使用说明书》§5.2 的表抄的
    /// （docs/宇电AI-8寄存器速查.md）；表里没有的照代码原样印出来（「InP=99」），不猜它是什么口。
    /// 50/51 是电流输入，说明书标注 J4 模块专用——J7（热偶/热阻通用输入）上出现它就是参数配错了。
    /// </summary>
    public static string InpName(int inp) => inp switch
    {
        0 => "K 偶（InP=0）",
        1 => "S 偶（InP=1）",
        2 => "R 偶（InP=2）",
        3 => "T 偶（InP=3）",
        4 => "E 偶（InP=4）",
        5 => "J 偶（InP=5）",
        6 => "B 偶（InP=6）",
        7 => "N 偶（InP=7）",
        8 => "WRe3-WRe25（InP=8）",
        9 => "WRe5-WRe26（InP=9）",
        12 => "F2 辐射高温计（InP=12）",
        13 => "T 偶 0~300.00℃（InP=13）",
        17 => "K 偶 0~300.00℃（InP=17）",
        18 => "J 偶 0~300.00℃（InP=18）",
        19 => "Ni120（InP=19）",
        20 => "Cu50（InP=20）",
        21 => "Pt100（InP=21）",
        22 => "Pt100 两位小数（InP=22）",
        23 => "Pt1000（InP=23，J0/J2）",
        24 => "0~2000Ω（InP=24，J0/J2）",
        25 => "0~75mV（InP=25）",
        27 => "0~320Ω（InP=27）",
        28 => "0~20mV（InP=28）",
        29 => "0~50mV（InP=29）",
        33 => "1~5V（InP=33，J3）",
        34 => "0~5V（InP=34，J3）",
        35 => "-10~+10mV（InP=35）",
        36 => "-37.5~+37.5mV（InP=36）",
        38 => "10~50mV（InP=38）",
        39 => "15~75mV（InP=39）",
        42 => "0~10V（InP=42，J3）",
        43 => "2~10V（InP=43，J3）",
        50 => "0~20mA（InP=50，J4）",
        51 => "4~20mA（InP=51，J4）",
        _ => $"InP={inp}"
    };

    /// <summary>
    /// 测温规格的量程（℃，说明书 §1.3）。断线/超量程时拿原始值对着它说「低于下限」还是「高于上限」——
    /// 现场读到 -20215（InP=22 → -202.15 ℃）就是压在 -200.00 的下限之下，不是「没数」。
    /// </summary>
    public static (double Lo, double Hi)? RangeOf(int inp) => inp switch
    {
        0 => (-200, 1300), 1 => (-50, 1700), 2 => (-50, 1700), 3 => (-200, 350), 4 => (0, 800),
        5 => (0, 1000), 6 => (200, 1800), 7 => (0, 1300), 8 => (0, 2300), 9 => (0, 2300),
        12 => (450, 2000), 13 => (0, 300), 17 => (0, 300), 18 => (0, 300), 19 => (-50, 270),
        20 => (-50, 150), 21 => (-200, 800), 22 => (-200, 300), 23 => (-200, 300),
        _ => null
    };

    /// <summary>说明书 §5.2 表里有、但既不是热偶/热阻也不是电流的规格（mV / V / Ω）。</summary>
    private static bool IsOtherLinear(int inp) => inp is 24 or 25 or 27 or 28 or 29 or 33 or 34 or 35 or 36 or 38 or 39 or 42 or 43;

    /// <summary>该种探头叫什么口：报错里「模块上测温的是 CH2、CH4」那半句。</summary>
    public static string KindName(YudianKind kind) => kind == YudianKind.Thermal ? "测温" : "线性电流";

    private static YudianChannelSetup Setup(YudianKind kind, int group, int inp, short scl, short sch, int dpt)
    {
        var thermalDecimals = ThermalDecimalsOf(inp);
        var isLinear = inp is 50 or 51;

        switch (kind)
        {
            case YudianKind.Thermal when isLinear:
                return new(true, group, inp, null, null, null,
                    $"这一路的输入规格是 {InpName(inp)}——说明书 §5.2 标注 50/51 是 J4 电流模块专用，" +
                    "热阻探头读不了它；接的是 Pt100 就该把它写成 21");
            case YudianKind.Thermal when IsOtherLinear(inp):
                return new(true, group, inp, null, null, null,
                    $"这一路的输入规格是 {InpName(inp)}——不是热偶/热阻，温度探头读不了它");
            case YudianKind.Thermal when thermalDecimals is null:
                return new(true, group, inp, null, null, null,
                    $"输入规格 InP={inp} 不在说明书 §5.2 的表里，换算系数定不出来，不猜");
            case YudianKind.Thermal:
                return new(true, group, inp, Math.Pow(10, thermalDecimals.Value), null, null, null);

            case YudianKind.Linear when !isLinear:
                return new(true, group, inp, null, null, null,
                    $"这一路的输入规格是 {InpName(inp)}——不是电流口，pH 变送器（4~20mA）读不了它");
            default:
                // 线性：寄存器值就是「面板显示值去掉小数点」，隐含小数位 = dPt。
                // ScL/ScH 同一套隐含小数位，换算完就是量程（pH 通常 0.00~14.00）
                var div = Math.Pow(10, dpt);
                return new(true, group, inp, div, scl / div, sch / div, null);
        }
    }

    /// <summary>
    /// 测温型 InP → 寄存器隐含小数位。按说明书 §1.3「传感器测量范围」表：量程写着 .00 的是两位
    /// （13 T、17 K、18 J、19 Ni120、22 Pt100、23 Pt1000，×100 后都在 int16 之内），其余一位
    /// （Pt100 21 是 -200~+800℃，×10）。表里没有的规格返回 null，宁可拒绝换算也不上一个猜的系数。
    /// dPt 只管面板显示，不影响上位机读到的数（§5.4）。
    /// </summary>
    private static int? ThermalDecimalsOf(int inp) => inp switch
    {
        0 or 1 or 2 or 3 or 4 or 5 or 6 or 7 or 8 or 9 or 12 or 20 or 21 => 1,
        13 or 17 or 18 or 19 or 22 or 23 => 2,
        _ => null
    };

    /// <summary>
    /// 读一拍：4 路 PV + 报警状态。一共两问，1 秒一拍绰绰有余。
    /// PV 是有符号数——零下的温度是负的补码，别当无符号解。
    /// </summary>
    public async Task<YudianReading[]> ReadAsync(CancellationToken ct = default)
    {
        var id = _id ?? throw new InvalidOperationException("先 InitAsync 过自检拿到标度，再来读数");

        var pv = await _bus.ReadHoldingRegistersAsync(RegPv, 4, ct).ConfigureAwait(false);
        var alarm = await _bus.ReadHoldingRegistersAsync(RegAlarm, 2, ct).ConfigureAwait(false);

        var outs = new YudianReading[4];
        for (var i = 0; i < 4; i++)
        {
            var ch = id.Channels[i];
            // 高字节 = 奇数通道（CH1/CH3），低字节 = 偶数通道（CH2/CH4）
            var bits = i % 2 == 0 ? alarm[i / 2] >> 8 : alarm[i / 2] & 0xFF;
            var fault = (bits & 0x01) != 0;      // oral：断线或超量程
            var ha = (bits & 0x02) != 0;
            var la = (bits & 0x04) != 0;

            var raw = (short)pv[i];
            if (!ch.Enabled)
            {
                outs[i] = new YudianReading(null, raw, false, false, false, null);
                continue;
            }
            if (ch.Problem is not null || ch.Divisor is not { } div)
            {
                outs[i] = new YudianReading(null, raw, fault, ha, la, ch.Problem);
                continue;
            }
            // 值可不可信按**量程**判，不按报警字里的 oral 位判：断线 / 短路时寄存器里是量程外的码
            // （311.11、−202.15 那种），照样给出去但把 fault 挂上，由上层发成 Quality.Bad。
            // oral 位只说「出过事」——AAF 不自动复位时它锁住不清，干扰瞬间断线又回来也会留下它；
            // 值在量程内就照常用，把「标志锁着」单独报出来（现场踩到：标志一锁 Tr 永远 Bad）。
            // 量程定不出来的规格退回看 oral 位
            var value = raw / div;
            var range = _kind == YudianKind.Thermal ? RangeOf(ch.Inp)
                      : ch.RangeLo is { } rl && ch.RangeHi is { } rh ? (rl, rh) : null;
            var bad = range is var (lo, hi) ? value < lo || value > hi : fault;
            outs[i] = new YudianReading(value, raw, bad, ha, la, null, AlarmLatched: fault && !bad);
        }
        return outs;
    }
}
