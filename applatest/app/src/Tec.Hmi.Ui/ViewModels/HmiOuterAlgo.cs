namespace Tec.Hmi.Ui.ViewModels;

/// <summary>
/// 【本地改动】（2026-11-16，外环改「按实测实时算」第 1 步，主会话 D2 甲 / D4 / D11 / D15）温控卡上「外环 旧 | 新」两段钮出不出、
/// 它下面那一行状态字、点了以后面板说什么、放热报警红条 / Toast 说什么——全是纯函数（不碰 Avalonia、不碰能力接口），照 WaterAskPrompter 那样单测。
/// 用户 2026-11-16 点头：面板上加「外环：旧 / 新」，缺省旧；选「新」那一场由新算法（按实测实时算）控，随时能切回去；放热只报不停控。
/// 规矩在驱动那一层（开控那一刻锁定、整场不变，控中改了只存请求，停控后重新开控才用上；只在双向挡、控釜内时锁得成新）——
/// 这里只把话说对，不另做判断。给操作人的字用人话，不用 b̂ / τ̂ 这种符号（D15）。
/// 字眼避开验收仿真按字数计数的那几个（夹套压低 / 外环积分冻结在 / 外环偏置自校正 / 那一枪）。
/// </summary>
public static class HmiOuterAlgo
{
    /// <summary>
    /// 两段钮（连带下面那一行状态字）出不出：釜内控温（Tr 模式）+ 上位机 PID + 这一路支持选外环
    /// （<c>IPidTuningBench.OuterAlgorithmSupported</c>：只有双工位主机打开；单机 RD105、温控器 PID 方式没有）。
    /// 夹套 / 蒸回流模式没有外环，钮不摆——已选的照样存着，切回釜内控温又看得见。
    /// </summary>
    public static bool ShowPick(string mode, bool hostLoop, bool supported) => mode == "Tr" && hostLoop && supported;

    /// <summary>
    /// 状态行：钮出来才有。整句由驱动给（<c>IPidTuningBench.OuterAlgorithmNote</c>：在用新时带「维持温差 X K · 釜反应时间约 Y min」，
    /// 已选 / 在用不一样时说「停控后重新开控才用上」，选了新却锁成旧时说为什么）；驱动没给就按已选说一句。
    /// </summary>
    public static string? Line(bool show, bool live, string? note)
        => !show ? null
            : !string.IsNullOrWhiteSpace(note) ? note
            : live ? "外环：已选 新（按实测实时算）" : "外环：旧";

    /// <summary>
    /// 状态行标成琥珀色：在控（<paramref name="inUse"/> 不是 null）而已选跟在用不一样——控中改了选择（这一场照开控时定的走完），
    /// 或者选了新、这一场锁成了旧（不是双向挡 / 新算法还没起算……）。不在控时已选什么下一次开控就用什么，不提醒。
    /// </summary>
    public static bool LineWarn(bool show, bool live, bool? inUse) => show && inUse is { } u && u != live;

    /// <summary>点了「旧 / 新」、驱动收下并存了盘以后：面板日志一句（批次在跑时进运行记录）、Toast 一句。<paramref name="inUse"/> 是收下以后这一路此刻在用的（不在控 null）。</summary>
    public static (string Log, string Toast) Picked(bool live, bool? inUse)
    {
        var what = live ? "新（按实测实时算）" : "旧（原来的外环）";
        var log = $"外环选 {what}，已存盘——" + inUse switch
        {
            null => "下一次开温控（控釜内）起用" + (live ? "；只在双向挡、控釜内时用得上，用不上开控时按旧控并说明原因" : ""),
            { } u when u == live => "这一场本来就是它",
            _ => "这一路正在控温：这一场照开控时定的走完，停控后重新开控才用上",
        };
        var toast = inUse switch
        {
            null => live ? "外环已选 新：下一次开温控起由新算法控（双向挡、控釜内）" : "外环已选 旧：下一次开温控起按原来的外环控",
            { } u when u == live => $"外环已选 {(live ? "新" : "旧")}：这一场本来就是它",
            _ => $"外环已选 {(live ? "新" : "旧")}：这一场照{(inUse == true ? "新算法" : "原来的外环")}走完，停控后重新开控才用上",
        };
        return (log, toast);
    }

    /// <summary>放热报警上升沿的 Toast（只亮两拍，整句在温控卡 / 趋势页的红条和面板日志里）。
    /// 【本地改动】（第 1 步复核）报警 / 跑飞豁免都不看 TEC 有没有真在取热（会话给的「TEC 真接着」是继电器指令，不是电流 / 水流）：
    /// 断水、冷水机故障、TEC 开路时也成立——不说「保持满冷」，叫人查 TEC 电流和冷却水。</summary>
    public static string AlarmToast(int zoneIndex)
        => $"通道 {zoneIndex} 放热报警：控温不停；放热，或冷却没在出力——请人工查 TEC 电流和冷却水（温控卡上有红条）";

    /// <summary>放热报警撤了那一句面板日志（报警条件不再成立，或者停控了——驱动那一层在系统日志里分开说）。</summary>
    public const string AlarmClearedLog = "放热报警撤了（报警条件不再成立，或者已停控）";

    // ── 【本地改动】（2026-11-17，S1 复核查实）S1「读数没跟着夹套降」报警在面板上自己一路：原来跟放热报警共用一条 ITemperatureStatus.Alarm，
    //    面板一律当放热报警——Toast 叫人查 TEC 电流和冷却水、头卡写「放热报警」、撤了记「放热报警撤了」；放热那条亮着时它再起来没有沿（不 Toast、不进面板日志）。
    //    这条报警起来时夹套确实降下来了（新算法的釜壁估计跟着夹套实测走，降够了才报）——冷却在出力，该看的是釜内探头和釜里情况。
    //    红条照旧是驱动给的那一整串（两样都在用「；」接起来），不动。

    /// <summary>S1「读数没跟着夹套降」报警上升沿的 Toast（整句在红条和面板日志里）。不说冷却水 / TEC 电流：这条报警起来时冷却在出力。</summary>
    public static string ReadingAlarmToast(int zoneIndex)
        => $"通道 {zoneIndex} 釜内读数没跟着夹套降：控温不停——请人看一眼釜内探头（在不在本釜液里、插没插对工位、读数是不是卡住）和釜里情况" +
           "（也可能是慢慢变强的放热）（温控卡上有红条）";

    /// <summary>S1 报警撤了那一句面板日志（读数又跟着夹套动了，或者停控了——驱动那一层在系统日志里分开说）。</summary>
    public const string ReadingAlarmClearedLog = "「读数没跟着夹套降」报警撤了（读数又跟着夹套动了，或者已停控）";

    /// <summary>
    /// 把驱动给的两样拆开：<paramref name="alarm"/> = ITemperatureStatus.Alarm（红条那一整串），<paramref name="reading"/> = ITemperatureStatus.ReadingAlarm（S1 那一句）。
    /// 放热那一句 = 整串去掉 S1 那一句（驱动拼的是「放热；S1」）；整串就是 S1 那一句 → 没有放热。不认得的拼法（别的驱动）整串都算放热那一路，不丢报警。
    /// </summary>
    public static (string? Exotherm, string? Reading) SplitAlarm(string? alarm, string? reading)
    {
        if (alarm is null) return (null, null);
        if (reading is null) return (alarm, null);
        if (alarm == reading) return (null, reading);
        var tail = "；" + reading;
        return (alarm.EndsWith(tail, StringComparison.Ordinal) ? alarm[..^tail.Length] : alarm, reading);
    }

    /// <summary>头卡（通道页每一个标签顶上、总览卡片）缀的那一句：放热 → 「 · 放热报警（见温控卡）」（原话），S1 → 「 · 釜内读数报警（见温控卡）」，两样都在两样都说；都没有空串。</summary>
    public static string HeadAlarm(bool exotherm, bool reading)
        => exotherm && reading ? " · 放热报警、釜内读数报警（见温控卡）"
            : exotherm ? " · 放热报警（见温控卡）"
            : reading ? " · 釜内读数报警（见温控卡）"
            : "";

    /// <summary>串级丢了釜内 Tr、进保持那一下（新外环在控时）：保持期夹套目标是新算法按模型推的，不是「钳到釜内设定」（主会话 D10）。旧外环那两句照原样留在调用处。</summary>
    public static (string Log, string Toast) LiveHolding()
        => ("釜内 Tr 读数丢失，外环新算法先按模型推夹套目标，等它回来",
            "釜内 Tr 读数丢失，外环新算法先按模型推夹套目标，宽限内回来自动继续");
}

/// <summary>
/// 【本地改动】（2026-11-16，外环第 1 步，主会话 D11）放热报警在面板这一层的上升 / 下降沿（纯状态，不碰 Avalonia）：每秒喂一次
/// <c>ITemperatureStatus.Alarm</c>，上升沿面板要 Toast + 记一句日志、撤了记一句日志。沿在驱动那一层（Rd105Session.OnCycle）已经判过一次、
/// 写了系统日志和逐拍记录；这里只管面板自己的那一份（Toast、面板日志 → 批次在跑时进运行记录）。
/// 报警那句话驱动只在输出开着时给：切热源那一两秒输出关着时读到的是 null——连着 <see cref="ClearTicks"/> 拍读不到才算撤了
/// （同面板开关对账「连着 3 拍对不上才算真的对不上」那条规矩），免得那一两秒一来一回把 Toast 和日志重报一遍。
/// 红条本身不等：读到什么摆什么。
/// </summary>
public sealed class HmiAlarmEdge
{
    public enum Edge { None, Raised, Cleared }

    /// <summary>连着这么多拍（面板 1 s 一拍）读不到才算撤了。</summary>
    public const int ClearTicks = 3;

    private bool _on;
    private int _off;

    /// <summary>此刻在报（还没判成撤了）。</summary>
    public bool On => _on;

    /// <param name="alarm">驱动此刻给的那句报警（null = 没报 / 输出关着）。</param>
    public Edge Step(string? alarm)
    {
        if (alarm is not null)
        {
            _off = 0;
            if (_on) return Edge.None;
            _on = true;
            return Edge.Raised;
        }
        if (!_on) return Edge.None;
        if (++_off < ClearTicks) return Edge.None;
        _on = false;
        _off = 0;
        return Edge.Cleared;
    }
}
