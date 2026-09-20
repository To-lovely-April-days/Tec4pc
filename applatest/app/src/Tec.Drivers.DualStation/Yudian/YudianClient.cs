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

    /// <summary>「Pt100（InP=21）」「线性电流（InP=51）」——报错和探测应答里指着说的那一路是什么口。</summary>
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
}

/// <summary>一路的一次读数。SensorFault = 报警状态里的 oral 位（断线/超量程），值不可信。</summary>
public readonly record struct YudianReading(
    double? Value, bool SensorFault, bool AlarmHigh, bool AlarmLow, string? Problem);

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
    private const ushort RegDpt = 2128;     // dPt 小数点位置（2128..2131 一把读上来）

    private readonly ModbusRtuClient _bus;
    private YudianIdentity? _id;

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
            var inp = groups[g - 1];
            chans[i] = Setup(kind, g, inp,
                             scl: (short)groups[4 + (g - 1)],
                             sch: (short)groups[8 + (g - 1)],
                             dpt);
        }

        _id = new YudianIdentity(glob[3], glob[2], dpt, chans);
        return _id;
    }

    /// <summary>
    /// InP 代码 → 人话。只写核对过的：热偶 0~7、Cu50 20、Pt100 21/22、线性电流 50/51；
    /// 别的照代码原样印出来（「InP=13」），不猜它是什么口。
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
        20 => "Cu50（InP=20）",
        21 => "Pt100（InP=21）",
        22 => "Pt100 两位小数（InP=22）",
        50 or 51 => $"线性电流（InP={inp}）",
        _ => $"InP={inp}"
    };

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
                    $"这一路是线性电流（InP={inp}）——温度探头读不了它；接 4~20mA 变送器（pH）的才用这种口");
            case YudianKind.Thermal when thermalDecimals is null:
                return new(true, group, inp, null, null, null,
                    $"输入规格 InP={inp} 不在已核对的标度表里，换算系数定不出来，不猜");
            case YudianKind.Thermal:
                return new(true, group, inp, Math.Pow(10, thermalDecimals.Value), null, null, null);

            case YudianKind.Linear when !isLinear:
                return new(true, group, inp, null, null, null,
                    $"这一路是{InpName(inp)}——是测温口，pH 变送器（4~20mA）读不了它");
            default:
                // 线性：寄存器值就是「面板显示值去掉小数点」，隐含小数位 = dPt。
                // ScL/ScH 同一套隐含小数位，换算完就是量程（pH 通常 0.00~14.00）
                var div = Math.Pow(10, dpt);
                return new(true, group, inp, div, scl / div, sch / div, null);
        }
    }

    /// <summary>
    /// 测温型 InP → 寄存器隐含小数位。按手册量程表推的：量程写着 .00 的是两位
    /// （13/17/18/19/22，×100 后都在 int16 之内），其余一位。**联调时拿真机核一遍**——
    /// 表里没有的规格返回 null，宁可拒绝换算也不上一个猜的系数。
    /// </summary>
    private static int? ThermalDecimalsOf(int inp) => inp switch
    {
        0 or 1 or 2 or 3 or 4 or 5 or 6 or 7 or 8 or 9 or 12 or 20 or 21 => 1,
        13 or 17 or 18 or 19 or 22 => 2,
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

            if (!ch.Enabled)
            {
                outs[i] = new YudianReading(null, false, false, false, null);
                continue;
            }
            if (ch.Problem is not null || ch.Divisor is not { } div)
            {
                outs[i] = new YudianReading(null, fault, ha, la, ch.Problem);
                continue;
            }
            // 断线时寄存器里是残值，照样给出去但把 fault 挂上——
            // 由上层发成 Quality.Bad，跟 RD105 传感器越限一个待遇
            outs[i] = new YudianReading((short)pv[i] / div, fault, ha, la, null);
        }
        return outs;
    }
}
