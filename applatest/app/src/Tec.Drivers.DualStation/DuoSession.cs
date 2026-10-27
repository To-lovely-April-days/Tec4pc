using System.Threading;
using Tec.Driver.Abi;
using Tec.Drivers.Rd105;
using TecControl.Core.Control;
using F = Tec.Drivers.DualStation.DualStationDriver.Fields;

namespace Tec.Drivers.DualStation;

/// <summary>
/// 双工位反应主机的组合会话：一台设备、两个通道（需求 §1）。
///
/// 里面套着一个 Rd105Session 干夹套控温和 Tj/Tset/duty/fault 的采集（原样转发），
/// 自己再开一个采集循环轮 IO8R（热源切换状态与回切）。**釜内 Tr 不归它采**——
/// Tr 是独立的宇电探头设备发的，工作台在会话都开起来之后把带 Tr 签的采样按
/// 通道喂进来（IExternalReactorTemp），主机拿它做判到达、E 级程序和 dT。
///
/// 热源切换（需求 §2）分两种模式，看设备配置「TEC 加热」：
///
///  **不启用（默认，这台机器）**：升温系统 = 加热棒（RD105 负占空比 → 固态继电器，按功率调），降温系统 =
///  TEC（正占空比，按功率调）；一路只有一个输出，正负号选系统。再看「冷水机」：
///
///  · **「冷水机」已开（缺省）= 双向挡**（0336，用户定的：反应会放热也会吸热，控得住就得加热、制冷随时都能出力）：
///    **一个 PID 两边出力**——正给加热棒、负给 TEC 制冷，同一组参数（加热棒那张表），过零积分不清、什么都不重置。
///    **继电器跟着 PID 输出的符号走**：同号连续 3 s（SignDwell）才扳（关输出 → 断一只 → 合一只 → 开输出），保温段要的
///    功率一直是正的（夹套往冷却水漏热）所以不抖，只在落地、放热起来 / 结束这种真要换方向的时候扳一次。
///    下发目标那一刻按方向先合一只（低于当前超过死区合 TEC，其余合加热棒），之后就看符号。
///    **冷却水跟着 TEC 走（0522，用户定的）**：往 TEC 侧扳才先通水（阀 → 冷水机 → 通水延时 → 合功率线）；
///    回加热棒侧（升温 / 保温）就停冷水机、停稳关阀——升温时冷水机与阀都关着。
///    夹套 &gt; 「TEC 接入上限」（90）：TEC 断开、只加热棒出力、功率下限钳回 0（不复位），凉到 上限 − 滞回（缺省 85）再放开；
///    定挡那一刻夹套已经高过那条线也先不接。
///  · **「冷水机」已关 = 升温挡 / 降温挡**（0333）：**下发目标时按方向定挡**——目标比当前温度低超过死区 = 降温挡，
///    其余 = 升温挡。升温挡全程只用加热棒（过冲靠加热棒提前收功率、过了头关加热棒自然凉，绝不接 TEC）；
///    降温挡没水不接 TEC，两只都断着自然凉，自然凉到了目标交给加热棒维持（一次交接不回头）。中途温度漂动不换挡。
///
///  **启用**：TEC 反向输出也能加热，于是只有目标 &gt; 阈值才需要电加热棒（原来那套，不分挡）。
///
/// 各模式共同的硬条款：
///  · TEC 不能接到烫夹套上（TEC 模块自身耐温）：接入看「TEC 接入上限」与「接入滞回」；
///  · 每次切换都走全套序列：关输出 → 切继电器 → （核反馈）→ 重开输出；
///  · 电加热用不了（没配 IO8R / 打不开）时，需要加热的目标直接拒绝，理由写明。
/// </summary>
public sealed class DuoSession : IDeviceSession, IExternalReactorTemp, IDeviceSettings, IReconnectAwareSession
{
    // ── IDeviceSettings：温控器参数面板归里面那台 RD105 ──
    public IReadOnlyList<SettingsGroup> Groups => _rd.Groups;
    public IReadOnlyList<SettingsAction> Actions => _rd.Actions;
    public Task<ParameterSet> ReadAsync(string groupId, CancellationToken ct) => _rd.ReadAsync(groupId, ct);
    public Task<IReadOnlyList<string>> WriteAsync(string groupId, ParameterSet values, CancellationToken ct) => _rd.WriteAsync(groupId, values, ct);
    public Task<string> RunAsync(string actionId, CancellationToken ct) => _rd.RunAsync(actionId, ct);

    private readonly DuoLinks _links;
    private readonly DriverContext _ctx;
    private readonly Broadcast<Sample> _out = new();
    private readonly Rd105Session _rd;
    private readonly IDisposable _fwd;
    private readonly DuoTempControl[] _temps;
    private readonly SemaphoreSlim _switchLock = new(1, 1);

    // 这三项**每拍重读**（见 RefreshSwitches）：属性栏上的「TEC 加热」「冷水机」是开关，
    // 人按下去就该生效，不该还要先把设备断开重连。其余配置照旧开会话时读一次
    private bool _tecHeat;                  // TEC 反向输出加热启不启用（默认不启用）
    private bool _chiller;                  // 冷水机开没开（人标的；缺省已开——用户定的：降温时冷水机一定开着）——已开 = 双向挡，已关 = 升温挡 / 降温挡
    private double _band;                   // 升降温死区（只在 TEC 加热不启用时用：下发目标时定挡 / 双向挡起步合哪只）
    private readonly int _heaterSign;       // 加热棒吃哪个符号的占空比（温控器 PID 方式下判「TEC 没在制冷」/ 功率符号用）
    private readonly double _threshold;     // TEC 接入上限（≤ 90，用户定的死上限；「TEC 加热」启用时兼作电加热切换阈值）
    private readonly double _hyst;          // 接入滞回
    private readonly bool _feedback;        // 有没有接切换反馈 DI
    private readonly int[] _doIdx = new int[2];
    private readonly int[] _diIdx = new int[2];
    /// <summary>
    /// TEC 功率线经不经 IO8R 的继电器（配置「TEC 功率继电器」= 有，且配了 IO8R）。
    /// 现场电气定的：DO7 = 工位 A、DO6 = 工位 B（2026-10-27 用户对调），闭合 TEC 才有电——从前没人合它，
    /// 现场「降温没反应」就是这个。用户定的：**不常合**——不控温就断着，打开温控后
    /// 按「这一刻要升温还是要降温」只合该合的那一只（降温 / TEC 加热合它，升温合加热棒）。
    /// </summary>
    private readonly bool _tecRelay;
    private readonly int[] _tecDoIdx = new int[2];
    /// <summary>TEC 功率继电器的影子：闭合 = TEC 有电。与 _electric 一样，每拍读回核对。</summary>
    private readonly bool[] _tecOn = new bool[2];
    /// <summary>冷却水电磁阀接哪一路 DO（整机一只，现场 DO4）。</summary>
    private readonly int _waterDo;
    /// <summary>冷却水阀的影子：true = 开着。与 _electric 一样，每拍读回核对。</summary>
    private volatile bool _waterOn;
    /// <summary>开关冷却水阀时占着：采集循环看见它被占就不拿读回快照跟影子比（同 _switchLock 那道守卫）。</summary>
    private readonly SemaphoreSlim _waterLock = new(1, 1);
    /// <summary>每个工位一份能力实例（ICapability 要带通道号），读写的是同一只阀。</summary>
    private readonly CoolingWaterValve[] _water;
    /// <summary>阀刚打开时等多久再接 TEC（让水先流起来，用户定的）。阀已经开着就不等。</summary>
    private readonly TimeSpan _waterLead;
    /// <summary>
    /// 阀是人在面板上手动打开的：自动联动不去关它（两路都不制冷了也留着），人自己关或安全停机才关。
    /// 自动打开的不立这个旗。
    /// </summary>
    private volatile bool _waterManual;
    /// <summary>
    /// 这一路的切换序列正往要水的那一侧走（开阀 → 通水延时 → 合 TEC 功率线 / 进双向形态）：继电器还没合，
    /// 光看继电器判不出这一路要水，对账会把刚开的阀当成没人认领关掉，延时过了再开一遍（开 → 关 → 开，现场踩到）。
    /// 序列走完（合上了 / 不切了 / 失败了）就放手，之后按继电器判。计数：同一路可能有两条序列叠着等锁。
    /// </summary>
    private readonly int[] _waterClaim = new int[2];
    /// <summary>正在等通水延时的序列有几条（面板开关这段时间转菊花）。</summary>
    private int _waterLeading;
    /// <summary>
    /// 这一路这次控温人选了「不开冷却水，直接制冷」（HMI 降温弹窗，用户定的）：接 TEC 不先开阀 / 冷水机、不等通水，
    /// 对账也不按这一路留水、补水。下一次下发按那次的选择重定；停控清掉。
    /// </summary>
    private readonly bool[] _waterDeclined = new bool[2];
    /// <summary>HMI 下发前说的「下一次不开水」，下发时（TakeWaterChoice）取用一次。</summary>
    private readonly bool[] _skipWaterNext = new bool[2];
    /// <summary>「人选了不开冷却水，直接接 TEC」这次控温报过没有（双向挡来回扳不刷屏）。</summary>
    private readonly bool[] _declinedSaid = new bool[2];
    /// <summary>这一次开水是什么时候开始的：联动冷水机时到「正常运行」为止算开水在途（最多 ChillerStartWarn）。转起来 / 关水时清掉。</summary>
    private DateTimeOffset? _waterOpenAt;
    private readonly TimeSpan _tick;

    // ── 冷水机联动（用户定的标准开关顺序）────────────────────────────
    // 开水：先开阀 → 再开冷水机；关水：先停冷水机 → 等它真停了（运行状态 = 空闲）→ 再关阀。
    // 冷水机还在转（含延时停机那段）就关阀会憋泵；读不到它的状态 = 确认不了停没停，阀宁可开着。
    // 「关水」是个要等的过程：发完停机就挂起（_waterClosing），之后每拍读状态，停稳了才关阀
    /// <summary>冷水机最近一次读到的运行状态（_chillerStateKnown = false 时作废）。</summary>
    private volatile int _chillerState = -1;
    /// <summary>最近一次读冷水机读到了没有。读不到 = 确认不了它停没停。</summary>
    private volatile bool _chillerStateKnown;
    /// <summary>关水挂起：冷水机停机已发（或在发），等它停稳再关阀。阀这时还开着。</summary>
    private volatile bool _waterClosing;
    private DateTimeOffset _waterClosingSince;
    /// <summary>停机指令发出去了没有、什么时候发的（没发出去 / 冷水机没理就隔 StopResend 再发）。</summary>
    private bool _stopSent;
    private DateTimeOffset _stopSentAt;
    private bool _stopFailSaid;
    private static readonly TimeSpan StopResend = TimeSpan.FromSeconds(10);
    /// <summary>冷水机读不到连续几拍（只报第一次）。</summary>
    private int _chillerFails;
    /// <summary>挂起的关水等这么久冷水机还没停稳就报一次（不关阀，接着等）。</summary>
    internal TimeSpan ChillerStopWarn { get; set; } = TimeSpan.FromMinutes(5);
    private bool _closingSlowSaid;
    /// <summary>冷水机开机指令发了之后多久还没转起来就报一次。</summary>
    internal TimeSpan ChillerStartWarn { get; set; } = TimeSpan.FromSeconds(60);
    private DateTimeOffset? _chillerStartAt;
    private bool _chillerStartSlowSaid;
    /// <summary>断开设备时等冷水机停稳最多等多久，等不到阀就留着开（转着关阀会憋泵）。程序退出也走这里，别等太久。</summary>
    internal TimeSpan ChillerStopWaitOnDispose { get; set; } = TimeSpan.FromSeconds(15);
    /// <summary>开机指令该发还没发成（发失败了）：下一拍补发。关水时清掉。</summary>
    private bool _chillerStartPending;
    private bool _startFailSaid;
    /// <summary>冷水机读不到之后隔多久再试：一次读不到要等满超时，每拍都等会拖慢整个采集循环。</summary>
    private static readonly TimeSpan ChillerRetryAfter = TimeSpan.FromSeconds(10);
    private DateTimeOffset _chillerNextTry = DateTimeOffset.MinValue;
    /// <summary>日志里最后报过的冷水机状态（状态变了记一句）。</summary>
    private int? _chillerLogged;
    /// <summary>「TEC 在制冷而冷水机停了」那一段报过没有（冷水机再转起来清掉）。</summary>
    private bool _chillerDownSaid;

    private readonly bool[] _electric = new bool[2];
    /// <summary>电加热用不了那一段只报一次，别每拍刷屏。</summary>
    private readonly bool[] _noElectricSaid = new bool[2];
    /// <summary>「降温挡但 TEC 接不上（冷水机关 / 夹套还烫）」那一段同样只报一次；记的是上次报的原因，原因变了再报。</summary>
    private readonly string?[] _noCoolSaid = new string?[2];

    /// <summary>
    /// 这一路此刻在哪个挡（「TEC 加热」不启用时才有意义）。下发目标时定（DecideRegime）：「冷水机」已开 = 双向挡；
    /// 已关按方向 = 升温挡 / 降温挡。停控清掉；降温挡交给加热棒之后变升温挡（HandOver）。待定 = 定挡那一刻夹套读不到，采集循环读到了再定。
    /// </summary>
    private enum Regime { Undecided, Heating, Cooling, Both }

    private readonly Regime[] _regime = new Regime[2];

    /// <summary>
    /// 双向挡里 PID 输出的符号要连续多久指向另一只继电器才扳过去（0336，用户定的 3 s）。过零那一拍不扳：
    /// 一次切换要关输出一秒钟，符号在零附近来回的那几拍不值得。测试可以调短。
    /// </summary>
    internal TimeSpan SignDwell { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>双向挡符号判据的死区（% 功率）：|输出| 不超过它不算有方向，保持现状、计时清零。</summary>
    internal const double SignDeadbandPercent = 1.0;

    /// <summary>双向挡：符号从什么时候起连续指向哪一侧（跟此刻继电器位置不同的那一侧）。</summary>
    private readonly Side?[] _signSide = new Side?[2];
    private readonly DateTimeOffset?[] _signSince = new DateTimeOffset?[2];

    /// <summary>
    /// 双向挡的「夹套烫」锁存：夹套 &gt; TEC 接入上限（90）立起——TEC 断开、只加热棒出力、功率下限钳 0；
    /// 凉到 上限 − 滞回（88）落下。进双向挡那一刻夹套已经高过 88 也先立着（跟 0333 一样不把 TEC 接到烫夹套上）。
    /// </summary>
    private readonly bool[] _tecHot = new bool[2];
    /// <summary>
    /// 停控代数：每停一次加一。切换序列里 ①关输出 → ②切继电器 → ③核反馈 要走上两秒，
    /// 这期间操作人或安全层完全可能按下「停控」——那时 Enabled 早被 ① 置成 false 了，
    /// 光看它分不出「是我自己关的」还是「别人要停」。记个代数，切换前后对不上就不重开。
    /// </summary>
    private readonly int[] _stopGen = new int[2];

    /// <summary>
    /// **这一路要不要控温的意图**，与设备上的 ENABLE 分开记。
    ///
    /// 切换序列第 ① 步自己会把 ENABLE 关掉，所以「设备上现在是关的」回答不了
    /// 「原本要不要控温」。更要命的是中途失败：② 写继电器超时抛出之后 ENABLE 停在 0，
    /// 下一拍再看 ENABLE 就成了「这一路没人要控温」，于是永远不再把输出接回来——
    /// 一次总线抖动就把通道静悄悄停了。意图只由下发目标 / 停控改写，不受切换过程影响。
    /// </summary>
    private readonly bool[] _wantEnabled = new bool[2];

    /// <summary>下发目标走过这里：这一路是要控温的。</summary>
    internal void NoteStart(int well)
    {
        if (well is 0 or 1) _wantEnabled[well] = true;
    }

    /// <summary>这一路此刻是不是在控温（意图）。面板重开时按它对账，不按设备上会被切换序列
    /// 临时关掉的 ENABLE。</summary>
    internal bool WantEnabled(int well) => well is 0 or 1 && _wantEnabled[well];

    /// <summary>
    /// 这一路正在自整定（面板「PID 整定」页发起）。整定期间继电器归整定占着：采集循环不自动换挡、
    /// 也不因为「没在控温」把它断开；下发控温目标拒绝。整定结束（任何原因）落旗，下一拍全断。
    /// </summary>
    private readonly bool[] _tuning = new bool[2];

    internal bool Tuning(int well) => well is 0 or 1 && Volatile.Read(ref _tuning[well]);

    /// <summary>整定中拒绝下发控温目标 / 开蒸回流（DuoTempControl 调）。</summary>
    internal void GuardTuning(int well)
    {
        if (Tuning(well))
            throw new InvalidOperationException(
                $"工位 {AB(well)} 正在自整定——先在面板「PID 整定」页取消，再下发控温目标");
    }

    /// <summary>停控（含安全停机）走过这里：让正在进行的切换知道「别再把输出打开」。挡位一并清掉——下次下发重新定。</summary>
    internal void NoteStop(int well)
    {
        if (well is not (0 or 1)) return;
        _wantEnabled[well] = false;
        _regime[well] = Regime.Undecided;
        _signSide[well] = null;
        _signSince[well] = null;
        _tecHot[well] = false;
        _waterDeclined[well] = _skipWaterNext[well] = _declinedSaid[well] = false;
        System.Threading.Interlocked.Increment(ref _stopGen[well]);
    }
    /// <summary>反馈 DI 与继电器对不上的那一段只报一次，对上了再报一次「已对上」。</summary>
    private readonly bool[] _fbMismatch = new bool[2];

    private bool _ioOk;
    private int _ioFails;

    // ── 蒸回流（夹套跟随）状态。__refluxGate 拴三样：跟随参数、安全压制旗、
    //    喂 Tr 的时间戳——采集循环与安全停/新目标可能在不同线程上碰它们
    private sealed class Follow
    {
        public double DeltaT;
        public double MaxTj;
    }

    private readonly object _refluxGate = new();
    private readonly Follow?[] _reflux = new Follow?[2];
    /// <summary>安全压制：SafeStop/停控清跟随时立起，跟随环写到一半撞见它要把输出关回去。</summary>
    private readonly bool[] _refluxKill = new bool[2];
    private readonly DateTimeOffset[] _trFedAt = { DateTimeOffset.MinValue, DateTimeOffset.MinValue };
    /// <summary>Tr 从什么时候起没了（超过新鲜度窗 / 喂进来的是 Bad）；回来时记一条「恢复（中断 N s）」。</summary>
    private readonly DateTimeOffset?[] _trLostAt = new DateTimeOffset?[2];

    /// <summary>Tr 新鲜度窗：跟随环只吃这窗内喂过的釜温——探头会话死了残值还在，不能追着残值走。</summary>
    private static readonly TimeSpan TrFreshWindow = TimeSpan.FromSeconds(10);

    private DeviceState _state = DeviceState.Connected;
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;

    public DuoSession(DuoLinks links, DriverContext ctx, ParameterSet connection)
    {
        _links = links;
        _ctx = ctx;
        var cfg = ctx.Config;

        // 默认不启用：现场 TEC 的反向那一极接的是加热棒 SSR，TEC 只能制冷；加热一律走电加热棒
        _tecHeat = cfg.Str(F.TecHeat, "不启用") == "启用";
        _chiller = cfg.Str(F.Chiller, "已开") == "已开";
        _heaterSign = Rd105HostDefaults.DualStation.HeaterSignOf(cfg);
        // TEC 接入上限 90 是死的（用户定的：只能比 90 小，TEC 模块自身耐温）——表单已经限了，这里再拴一道
        _threshold = Math.Min(90, cfg.Num(F.Threshold, 90));
        _hyst = Math.Clamp(cfg.Num(F.Hysteresis, 5), 2, 20);
        _band = Math.Clamp(cfg.Num(F.Band, 2), 0.5, 10);
        _feedback = cfg.Str(F.Feedback, "无") == "有";
        for (var w = 0; w < 2; w++)
        {
            _doIdx[w] = Math.Clamp((int)cfg.Num(w == 0 ? F.DoA : F.DoB, w), 0, 7);
            _diIdx[w] = Math.Clamp((int)cfg.Num(w == 0 ? F.DiA : F.DiB, w), 0, 7);
            _tecDoIdx[w] = Math.Clamp((int)cfg.Num(w == 0 ? F.TecDoA : F.TecDoB, 7 - w), 0, 7);
        }
        // TEC 功率线经 IO8R 继电器（现场电气定的：DO7 = A、DO6 = B）。没配 IO8R 的机器
        // 无所谓「经不经」——那台没有继电器可扳，只能当它是硬线接的
        _tecRelay = links.Io is not null && cfg.Str(F.TecRelay, "有") == "有";
        // 制冷点位 2026-10-27 对调成 A = DO7、B = DO6。台面里存的若还是对调前那一对（A = 6、B = 7，
        // 只存了一项的按旧缺省补齐另一项再比——工作站属性栏一打开就把当时的缺省写进去了），
        // 当旧缺省、按新的用（用户定的：不用现场去改文件）。别的填法照填的走
        if ((cfg.Has(F.TecDoA) || cfg.Has(F.TecDoB))
            && (cfg.Has(F.TecDoA) ? _tecDoIdx[0] : 6) == 6
            && (cfg.Has(F.TecDoB) ? _tecDoIdx[1] : 7) == 7)
        {
            _tecDoIdx[0] = 7;
            _tecDoIdx[1] = 6;
            if (_tecRelay)
                _ctx.Log?.Invoke("info", $"{InstanceId} 台面里「TEC 功率 DO·工位 A / B」存的是对调前的 6 / 7，" +
                    "按现在的点位用：通道 1（A）制冷 = DO7、通道 2（B）制冷 = DO6");
        }
        if (_tecRelay)
            for (var w = 0; w < 2; w++)
                if (_tecDoIdx[w] == _doIdx[w])
                    throw new InvalidOperationException(
                        $"设备配置冲突：工位 {AB(w)} 的「切换 DO」和「TEC 功率 DO」都填了 DO{_doIdx[w]}——" +
                        "一只继电器不能既接加热棒又接 TEC 功率线，改掉一个再连");
        _waterDo = Math.Clamp((int)cfg.Num(F.WaterDo, 4), 0, 7);
        if (links.Io is not null)
            for (var w = 0; w < 2; w++)
                if (_waterDo == _doIdx[w] || (_tecRelay && _waterDo == _tecDoIdx[w]))
                    throw new InvalidOperationException(
                        $"设备配置冲突：「冷却水阀 DO」填了 DO{_waterDo}，跟工位 {AB(w)} 的" +
                        $"{(_waterDo == _doIdx[w] ? "「切换 DO」" : "「TEC 功率 DO」")}重号——一只继电器只能接一样东西，改掉一个再连");
        // 冷水机要单独一路串口：跟 RD105 / IO8R 共一个口，开口时后开的那条就被占着打不开（报出来是「被占用」，看不出是自己占的）
        if (links.ChillerPortName is { } chp)
            foreach (var (name, label) in new[] { (links.RdPortName, "RD105 串口"), (links.IoPortName, "IO8R 串口") })
                if (string.Equals(chp, name, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"设备配置冲突：「冷水机串口」跟「{label}」都填了 {chp}——冷水机要单独一路串口，改掉一个再连" +
                        "（不联动冷水机就把「冷水机联动」选成「无」）");
        _water = new[] { new CoolingWaterValve(this, 0), new CoolingWaterValve(this, 1) };
        _waterLead = TimeSpan.FromSeconds(Math.Clamp(cfg.Num(F.WaterLead, 5), 0, 60));
        _tick = TimeSpan.FromMilliseconds(Math.Clamp(connection.Num(F.Tick, 1000), 100, 5000));

        // 内层 RD105 会话：夹套控温 + Tj/Tset/duty/fault 采集，原样转发到本会话的流上
        _rd = new Rd105Session(links.Rd105, new DriverContext
        {
            InstanceId = ctx.InstanceId,
            ChannelNumbers = ctx.ChannelNumbers,
            Config = cfg,
            Simulated = false,
            TimeScale = 1,
            Clock = ctx.Clock,
            Log = ctx.Log
        }, connection, Rd105HostDefaults.DualStation);
        _fwd = _rd.Samples.Subscribe(new Fwd(_out));
        // 逐拍记录里带上热源在哪一侧、哪个挡、冷水机标的开还是关（事后看「那一炉为什么没制冷」就靠它）
        _rd.LoopExtra = w => _tecHeat ? SideName(SideNow(w))
            : $"{SideName(SideNow(w))}·{RegimeName(_regime[w])}·冷水机{(_chiller ? "开" : "关")}";
        _rd.StateChanged += (_, st) => { if (st != DeviceState.Disposed) State = st; };
        // 上位机回路把某一路停了（Tr 丢失 / 跑飞 / 连续通信失败）：这一路就算不控了，
        // 下一拍采集循环把它的两只继电器都断开（不控温不合，用户定的）
        for (var w = 0; w < 2; w++)
        {
            var well = w;
            _rd.TempOf(w).Tripped += why =>
            {
                NoteStop(well);
                SuppressReflux(well);
                _ctx.Log?.Invoke("error", $"{InstanceId} 工位 {AB(well)} 控温已被回路停下（{why}）——继电器下一拍断开");
            };
        }

        _temps = new[] { new DuoTempControl(this, 0), new DuoTempControl(this, 1) };

        // 上位机 PID 的整定台：自整定要按执行器把继电器扳好、整定期间不自动换挡、结束了断开
        for (var w = 0; w < 2; w++)
        {
            var well = w;
            if (_rd.PidOf(w) is { } pid)
                pid.Hooks = new Rd105PidHooks
                {
                    Suggest = sp => SuggestTuneActuator(well, sp),
                    Check = r => CheckTune(well, r),
                    StopControl = ct => _temps[well].StopAsync(ct),
                    Prepare = (r, ct) => PrepareTuneAsync(well, r, ct),
                    Ended = () => EndTune(well)
                };
        }
    }

    // ── 自整定的热源配合（面板「PID 整定」页）──────────────────────────
    //
    // 继电器法要在设定值两边来回推，推的只能是一个执行器——整定期间绝不能换热源。
    // TEC 侧：TEC 功率线合、加热棒断；「TEC 加热」不启用时只许制冷（强制冷 / 弱制冷两个半周）。
    // 加热棒侧：加热棒合、功率线断；只加热（强加热 / 弱加热，弱的那半周靠自然散热）。
    // 结果登记进对应执行器那张表——同一温度，TEC 和加热棒的过程增益差一个量级。

    private PidActuator SuggestTuneActuator(int well, double setpointC)
    {
        if (setpointC > _threshold) return PidActuator.Heater;          // TEC 接入上限以上 TEC 不接
        if (_tecHeat) return PidActuator.Tec;                            // TEC 正反都能出力
        // TEC 只能制冷，而且没有冷却水不能开：冷水机没标开就只有加热棒；
        // 开着的话，整定点比此刻夹套低超过死区（要制冷才维持得住）才用 TEC——跟下发目标起步合哪只同一条规矩。
        // 双向挡（0336）跑的是加热棒那张表的参数，保温、升温整加热棒就是在整双向挡
        if (!_chiller) return PidActuator.Heater;
        var tj = _rd.TempOf(well).CurrentJacket;
        return !double.IsNaN(tj) && setpointC < tj - _band ? PidActuator.Tec : PidActuator.Heater;
    }

    private string? CheckTune(int well, PidAutoTuneRequest r)
    {
        if (r.Actuator == PidActuator.Heater)
            return ElectricReady ? null
                : $"电加热用不了（{(_links.Io is null ? "这台没配 IO8R 切换模块" : "IO8R 没打开")}）——加热棒那张表整不了";

        if (_tecRelay && !ElectricReady) return NoTecReason(well);
        if (r.SetpointC > _threshold)
            return $"整定温度 {r.SetpointC:0.#} ℃ 高于 TEC 接入上限 {_threshold:0} ℃——这个温度 TEC 不接，选加热棒整";
        if (!_tecHeat && !_chiller)
            return "设备属性「冷水机」标为已关——TEC 没有冷却水不能开。先开冷水机、把「冷水机」改成已开，再整 TEC 那张表；或者选加热棒整";
        var tj = _rd.TempOf(well).CurrentJacket;
        if (!double.IsNaN(tj) && tj > _threshold - _hyst)
            return $"夹套 {tj:0.0} ℃ 还高于 TEC 接入线 {_threshold - _hyst:0} ℃（上限 − 滞回）——TEC 不能接到这么烫的夹套上，等它凉下来再用 TEC 整";
        // 「TEC 加热」不启用：TEC 只制冷，跟加热棒一样是单方向，幅值同样不能超过 LIMITED 的一半
        if (!_tecHeat && _rd.PidOf(well)?.OneSidedAmplitudeCheck(r, "TEC（「TEC 加热」没启用，只制冷）") is { } amp)
            return amp;
        return null;
    }

    private async Task<bool> PrepareTuneAsync(int well, PidAutoTuneRequest r, CancellationToken ct)
    {
        var side = r.Actuator == PidActuator.Heater ? Side.Electric : Side.Tec;
        Volatile.Write(ref _tuning[well], true);        // 先立旗：采集循环这一拍起不再自动换挡 / 断开
        try
        {
            // 继电器与执行器形态都到位才算在这一侧（SwitchAsync 自己判，已到位就什么都不动）
            await SwitchAsync(well, side, ct).ConfigureAwait(false);
        }
        catch
        {
            Volatile.Write(ref _tuning[well], false);
            throw;
        }
        var coolOnly = side == Side.Tec && !_tecHeat;
        _ctx.Log?.Invoke("info", $"{InstanceId} 工位 {AB(well)} 自整定：热源在{SideName(side)}侧" +
            (coolOnly ? "（「TEC 加热」没启用：只制冷继电，TEC 不反向出力）" : "") + "，整定期间不自动换挡");
        return coolOnly;
    }

    private void EndTune(int well)
    {
        if (!Volatile.Read(ref _tuning[well])) return;
        Volatile.Write(ref _tuning[well], false);
        _ctx.Log?.Invoke("info", $"{InstanceId} 工位 {AB(well)} 自整定结束——没在控温，继电器下一拍全断");
    }

    public string InstanceId => _ctx.InstanceId;

    /// <summary>台面重建重开时宿主先把这个打上再 Dispose：「走人前收拾」（停冷水机、
    /// 关冷却水阀）跳过，物理状态交给新会话（它的 DriverContext.Reconnecting 也是 true）。</summary>
    public bool Reconnecting { get; set; }

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

    public int WellCount => 2;

    public IReadOnlyList<TagDescriptor> Tags { get; } = new[]
    {
        // Tr 与 pH 不在这里声明——它们是宇电探头设备的签，各归各的会话，
        // 主机再发一份就是同一个量两个来源
        new TagDescriptor("Tj", "夹套温度", "℃", DataShape.Scalar)
            { Nominal = new ValueRange(-40, 180) },
        new TagDescriptor("dT", "Tr−Tj 温差", "℃", DataShape.Scalar)
            { Nominal = new ValueRange(-60, 60) },
        new TagDescriptor("Tset", "设定温度", "℃", DataShape.Scalar)
            { Nominal = new ValueRange(-40, 180) },
        new TagDescriptor("duty", "控温输出", "%", DataShape.Scalar)
            { Nominal = new ValueRange(-100, 100) },
        // 热源：0 = TEC，1 = 电加热（无反馈回路，未核实），2 = 电加热（反馈已核实）。
        // 发成一路状态量，记录里看得出什么时候换的挡、换过去有没有核实（需求 §2.5）
        new TagDescriptor("heat", "热源", "", DataShape.State)
            { Nominal = new ValueRange(0, 2) },
        // TEC 功率线：0 = 断（TEC 没电，制冷不动），1 = 通。读回的继电器实际位置，
        // 只在功率线经 IO8R 继电器的机器上发——事后「那一炉为什么降不下去」看它
        new TagDescriptor("tecpwr", "TEC 功率线", "", DataShape.State)
            { Nominal = new ValueRange(0, 1) },
        // 上位机 PID 下里面那台 RD105 会话发的：TEC 电流、串级外环算出的夹套设定值（原样转发）
        new TagDescriptor("cur", "TEC 电流", "A", DataShape.Scalar)
            { Nominal = new ValueRange(0, 20) },
        new TagDescriptor("Tjset", "夹套设定（串级）", "℃", DataShape.Scalar)
            { Nominal = new ValueRange(-40, 180) },
        new TagDescriptor("fault", "设备告警字", "", DataShape.State)
            { Nominal = new ValueRange(0, 0) }
    };

    public IReadOnlyList<ICapability> CapabilitiesOf(int well)
        => well is 0 or 1
            ? new ICapability[] { _temps[well] }
                // 冷却水阀长在 IO8R 上：没配 IO8R 的机器没有这只阀，不给能力（面板就不显示开关）
                .Concat(_links.Io is not null ? new ICapability[] { _water[well] } : Array.Empty<ICapability>())
                .Concat(_rd.CapabilitiesOf(well).OfType<ITemperatureTuning>())
                .Concat(_rd.CapabilitiesOf(well).OfType<IPidTuningBench>()).ToArray()
            : Array.Empty<ICapability>();

    /// <summary>温度指令认领 ABI 的能力通用执行器——与仿真同一份语义，
    /// 仿真调好的配方插上真机能跑。搅拌/加料这台没有，认不了不硬认。</summary>
    public ICommandHandler? Resolve(string commandId) => CapabilityCommands.Resolve(commandId);

    internal Rd105TemperatureControl InnerTemp(int well) => _rd.TempOf(well);

    /// <summary>这一工位正在记的控温记录文件（测试用）。</summary>
    internal string? InnerLoopLogPath(int well) => _rd.LoopLogPathOf(well);

    /// <summary>
    /// 工作台喂进来的外部釜温（宇电 Tr 探头会话发的）。质量不好就按 NaN 处置——
    /// 断线残值不能进控制判据；判到达与 E 级程序吃的就是这口饭。
    /// </summary>
    public void FeedReactor(int channel, double value, Quality quality)
    {
        for (var w = 0; w < 2; w++)
        {
            var t = _rd.TempOf(w);
            if (t.Channel != channel) continue;
            var good = quality == Quality.Good;
            var wasNaN = double.IsNaN(t.CurrentReactor);
            t.FeedReactor(good ? value : double.NaN);
            var now = _ctx.Clock();
            DateTimeOffset? lost = null;
            lock (_refluxGate)
            {
                _trFedAt[w] = now;
                if (!good) _trLostAt[w] ??= now;
                else if (wasNaN && _trLostAt[w] is { } since) { lost = since; _trLostAt[w] = null; }
            }
            // 断过再回来记一句（探头会话那边只说自己恢复了，这边说的是「这一路的釜内 Tr 又能用了」）
            if (lost is { } l)
                _ctx.Log?.Invoke("info", $"{InstanceId} 工位 {AB(w)} 釜内 Tr 恢复（中断 {(now - l).TotalSeconds:0} s，Tr {value:F2} ℃）——判到达、dT、串级外环都接着用它");
        }
    }

    // ── 开机 ─────────────────────────────────────────────────────────

    /// <summary>
    /// 开机自检：RD105 写两路保护寄存器；IO8R 把切换继电器复位到 TEC 侧
    /// （继电器落回的必须是安全侧），坏了照常开但电加热不可用（需求 §2.6）。
    /// </summary>
    public async Task InitAsync(CancellationToken ct)
    {
        await _rd.ApplyProtectionAsync(ct).ConfigureAwait(false);

        // 先把两路输出关掉。RD105 固件没有通信看门狗（注释见 Rd105TemperatureControl），
        // 上位机重启 / 台面重开的时候设备很可能还在按上一炉的目标输出着——
        // 那时去扳继电器就是带载切换（触点拉弧），而且 Enabled 影子一开始是 false，
        // 跟设备对不上，热源状态机会把一路正在加热的通道当成「停控」处理
        for (var w = 0; w < 2; w++)
        {
            try { await _rd.TempOf(w).EnableAsync(false, ct).ConfigureAwait(false); }
            catch (Exception ex)
            {
                _ctx.Log?.Invoke("warn", $"{InstanceId} 开机关闭工位 {AB(w)} 控温输出失败：{ex.Message}");
            }
        }

        RefreshSwitches();
        _ctx.Log?.Invoke("info", $"{InstanceId} 热源策略：「TEC 加热」{(_tecHeat ? "启用" : "不启用")}" +
            (_tecHeat
                ? $"——目标高于 {_threshold:F0} ℃ 才切电加热；回切线 {_threshold - _hyst:F0} ℃（阈值 − 滞回）"
                : $"——升温系统 = 加热棒、降温系统 = TEC 制冷；「冷水机」{(_chiller
                    ? $"已开 = 双向挡：一个 PID 两边出力（正加热棒、负 TEC），继电器跟着功率符号走（同号连续 {SignDwell.TotalSeconds:0} s 才扳）；冷却水跟着 TEC 走：升温时冷水机与阀都关，往 TEC 侧扳才先通水"
                    : $"已关 = 升温挡或降温挡（下发目标时按方向定挡，死区 {_band:F1} K）：升温挡只用加热棒，降温挡没水不接 TEC 只自然凉；要 TEC 制冷把它改成已开")}" +
                  $"；TEC 接入上限 {_threshold:F0} ℃（夹套高过它 TEC 断开、只加热棒），凉到 {_threshold - _hyst:F0} ℃（上限 − 滞回 {_hyst:F0}）再接"));

        _ioOk = false;
        if (_links.Io is { } io)
        {
            try
            {
                // 串口都没开成就别去发 Modbus 了——原因是开口时记下的那句，照它说
                if (_links.IoOpenError is { } why) throw new InvalidOperationException(why);
                // 联动冷水机：先看它转没转。还在转（上一炉留着 / 人在冷水机上开的）就别把阀跟着八路一起断——
                // 转着关阀会憋泵；读不到确认不了，同样留着阀（只在阀本来就开着时）
                var keepWater = await ChillerKeepsWaterAsync(io, ct).ConfigureAwait(false);
                // 一帧全断：加热棒继电器与 TEC 功率线都断着（用户定的：不控温就不合）。
                // 打开温控后按升温 / 降温只合该合的那一只（EnsureSourceAsync / 采集循环）
                if (keepWater)
                {
                    var relays = new bool[Io8rClient.Points];
                    relays[_waterDo] = true;
                    await io.WriteRelaysAsync(relays, ct).ConfigureAwait(false);
                }
                else await io.AllOffAsync(ct).ConfigureAwait(false);
                _electric[0] = _electric[1] = false;
                _tecOn[0] = _tecOn[1] = false;
                _waterOn = keepWater;   // 冷却水阀（DO{_waterDo}）跟着断了：开机关阀，要接 TEC 时自动开（冷水机在转就留着）
                _waterManual = keepWater;
                _ioOk = true;
                _ctx.Log?.Invoke("info", $"{InstanceId} IO8R 已复位：" +
                    (keepWater ? $"除冷却水阀（DO{_waterDo}，冷水机在转 / 确认不了停没停）外全断" : "八路全断") +
                    (_tecRelay
                        ? $"——加热棒继电器与 TEC 功率线（DO{_tecDoIdx[0]} = A、DO{_tecDoIdx[1]} = B）都断着，" +
                          (_tecHeat ? "打开温控后要降温合 TEC 功率线、要升温合加热棒"
                                    : "打开温控后按方向先合一只（要制冷合 TEC 功率线、其余合加热棒），双向挡里再跟着功率符号扳")
                        : "——热源切换继电器在 TEC 侧"));
            }
            catch (Exception ex)
            {
                _ctx.Log?.Invoke("warn", $"{InstanceId} IO8R 打不开：{ex.Message}——照常开机，但" +
                    (_tecRelay
                        ? $"TEC 功率线经它的 DO{_tecDoIdx[0]}/DO{_tecDoIdx[1]} 接通，接不通 TEC 就没电；电加热不可用" +
                          "——这台现在既不能制冷也不能加热，所有控温目标都会被拒绝，先把 IO8R 弄通"
                        : "电加热不可用，" + (_tecHeat
                            ? $"目标超过 {_threshold:F0} ℃ 的下发会被拒绝"
                            : "而「TEC 加热」没启用——这台现在只降得下去、升不上来，需要升温的下发都会被拒绝")));
            }
        }
        else if (!_tecHeat)
        {
            // 没配 IO8R + TEC 加热不启用 = 这台机器一点温都升不了。
            // 这是开机就能算出来的事实，不该等到配方第一步升温时才炸出来
            _ctx.Log?.Invoke("warn", $"{InstanceId} 这台没配 IO8R 切换模块，而「TEC 加热」没启用" +
                "——本机当前只能制冷，任何升温的下发都会被拒绝。" +
                "要升温：接上 IO8R 切换模块，或在设备属性里把「TEC 加热」打开");
        }
    }

    public async Task StartAsync(CancellationToken ct)
    {
        await _rd.StartAsync(ct).ConfigureAwait(false);
        _pollCts = new CancellationTokenSource();
        _pollTask = Task.Run(() => PollLoopAsync(_pollCts.Token), CancellationToken.None);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _pollCts?.Cancel();
        if (_pollTask is { } t) { try { await t.ConfigureAwait(false); } catch { } }
        _pollTask = null;
        await _rd.StopAsync(ct).ConfigureAwait(false);
    }

    // ── 采集循环（dT + IO8R；RD105 的轮询在内层会话里自己转） ───────

    private async Task PollLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_tick);
        while (!ct.IsCancellationRequested)
        {
            try { if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) break; }
            catch (OperationCanceledException) { break; }
            try { await PollOnceAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>跑一拍。单拎出来是为了回归测试能一拍一拍地推，不靠真时钟。</summary>
    internal async Task PollOnceAsync(CancellationToken ct)
    {
        var at = _ctx.Clock();
        RefreshSwitches();

        // dT：外部喂进来的 Tr − 本机的 Tj。两头都有才发，缺哪头都不编。
        // Tr 超过新鲜度窗没有新数（探头会话死了 / 口子掉了）就把里面那路的 Tr 置成无效——
        // 判到达、面板读数、dT 全都别再吃残值（现场：探头口子掉了之后面板上 Tr 一直停在旧数）
        for (var w = 0; w < 2; w++)
        {
            var t = _rd.TempOf(w);
            if (!TrValid(w) && !double.IsNaN(t.CurrentReactor))
            {
                t.FeedReactor(double.NaN);
                lock (_refluxGate) _trLostAt[w] ??= at;
                _ctx.Log?.Invoke("warn", $"{InstanceId} 工位 {AB(w)} 釜内 Tr 超过 {TrFreshWindow.TotalSeconds:0} s 没有新数" +
                    "（探头会话没出数？）——按无效处置：面板上显示「—」、dT 停发；釜内串级先把夹套设定钳到釜内设定保持，" +
                    "宽限内没回来会停控；有新数自动恢复");
            }
            if (!double.IsNaN(t.CurrentReactor) && !double.IsNaN(t.CurrentJacket))
                Push(t.Channel, "dT", t.CurrentReactor - t.CurrentJacket, at, Quality.Good);
        }

        // 蒸回流跟随环：每拍把夹套目标追到 Tr+ΔT（钳上限、限速写、必要时切热源）
        for (var w = 0; w < 2; w++)
            await FollowOnceAsync(w, ct).ConfigureAwait(false);

        // IO8R：读回继电器实际位置跟命令核对，发热源状态，条件满足时回切 TEC
        if (_links.Io is { } io && _ioOk)
        {
            try
            {
                var dos = await io.ReadRelaysAsync(ct).ConfigureAwait(false);
                // 接了反馈就每拍连 DI 一起读：切换那一刻核实过不算完——接触器中途释放
                // （电加热其实断了）或粘连（继电器已断它还吸着）只有持续盯着才看得见
                var dis = _feedback ? await io.ReadInputsAsync(ct).ConfigureAwait(false) : null;
                if (_ioFails > 0) { _ctx.Log?.Invoke("info", $"{InstanceId} IO8R 恢复"); _ioFails = 0; }
                // 正在切换的那几十毫秒里读回来的是**切换前**的快照：拿它跟已经改过的
                // _electric 比，会冒出假的「有人手扳了模块」告警，还会把 heat 记反一拍。
                // 与 HeatStateOf 用同一道守卫：锁被占着就不比对
                var settled = _switchLock.CurrentCount > 0;
                for (var w = 0; w < 2; w++)
                {
                    var actual = dos[_doIdx[w]];
                    if (settled && actual != _electric[w])
                    {
                        // 模块上有按键，人手也能扳——以实际为准，不跟自己的想象过日子
                        _ctx.Log?.Invoke("warn", $"{InstanceId} 工位 {AB(w)} 切换继电器实际在" +
                            $"{(actual ? "电加热" : "TEC")}侧，与上位机命令不符——已按实际状态记录");
                        _electric[w] = actual;
                    }
                    else if (!settled)
                    {
                        actual = _electric[w];      // 切换进行中，以命令为准，别用陈旧快照落记录
                    }
                    Push(_rd.TempOf(w).Channel, "heat", HeatStateOf(w, actual, dis), at, Quality.Good);

                    if (!_tecRelay) continue;
                    // TEC 功率继电器同样读回核对：正降温时被人按断了 TEC 就没电，这一路制冷不动；
                    // 没控温时被人按合了，就违背了「不控温不合」——都报出来、按实际记录；
                    // 这一拍下面的自动换挡会把它扳回该在的位置（要降温的接回、停控的断开）
                    var tec = dos[_tecDoIdx[w]];
                    if (settled && tec != _tecOn[w])
                    {
                        _ctx.Log?.Invoke("warn", $"{InstanceId} 工位 {AB(w)} TEC 功率继电器 DO{_tecDoIdx[w]} 实际" +
                            $"{(tec ? "闭合" : "断开")}，与上位机命令不符——已按实际状态记录" +
                            (tec ? (_wantEnabled[w] ? "" : "（这一路没在控温，会断开它）") : "（TEC 没电，这一路现在制冷不动）"));
                        _tecOn[w] = tec;
                    }
                    else if (!settled)
                    {
                        tec = _tecOn[w];
                    }
                    Push(_rd.TempOf(w).Channel, "tecpwr", tec ? 1 : 0, at, Quality.Good);
                }
                // 冷却水阀同样读回核对：模块面板上按键也能扳它。正在开关的那一下读回来的是旧快照，不比
                var water = dos[_waterDo];
                if (_waterLock.CurrentCount > 0 && water != _waterOn)
                {
                    _ctx.Log?.Invoke("warn", $"{InstanceId} 冷却水阀 DO{_waterDo} 实际{(water ? "开着" : "关着")}，" +
                        "与上位机命令不符——已按实际状态记录" + (water ? "" : "（有路在制冷的话下面会补开）"));
                    _waterOn = water;
                    _waterManual = water;      // 人在模块上开的按手动算：自动联动不去关它
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                if (_ioFails++ == 0)
                    _ctx.Log?.Invoke("warn", $"{InstanceId} IO8R 读失败：{ex.Message}（连续失败只报第一次）");
            }
        }

        // 自动换挡**不放在上面那个 IO8R 守卫块里**：没配 / 没打开 IO8R 的机器
        // 恰恰最需要那句「要升温但电加热用不了」的提醒，放进去就永远打不出来。
        // 真要扳继电器的时候它自己会看 ElectricReady
        for (var w = 0; w < 2; w++)
            await AutoSourceAsync(w, ct).ConfigureAwait(false);

        // 冷却水阀对账：有路在制冷就得开着，都不制冷了（自动开的）就关
        await SyncWaterAsync(ct).ConfigureAwait(false);
        // 冷水机对账：关水挂着就看它停稳没有（停稳才关阀）；它在转而阀关着就先开阀
        try { await TickChillerAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _ctx.Log?.Invoke("warn", $"{InstanceId} 冷水机联动对账失败：{ex.Message}"); }
    }

    /// <summary>
    /// 采集循环里的自动换挡。「TEC 加热」不启用时：双向挡（冷水机已开）继电器跟着 PID 输出的符号走（同号连续 SignDwell 才扳）、
    /// 夹套过 / 回 TEC 接入线时只加热 ⇄ 加热棒 + TEC 换形态；升温挡 / 降温挡（冷水机已关）只有四件事会动继电器：
    /// 定挡那一刻夹套读不到的补定挡、冷水机开关翻了、夹套过了接入线、降温挡交给加热棒（CheckHandover），温度自己漂动**不换挡**。
    ///
    /// 只动「要控温」的通道：停控 / 安全停机之后输出都关了，扳继电器没有意义，
    /// 还会把安全停机刚落回的继电器又扳回电加热。
    /// </summary>
    private async Task AutoSourceAsync(int well, CancellationToken ct)
    {
        // 自整定期间继电器归整定占着：不换挡，也不因为「没在控温」断开
        if (Tuning(well)) return;
        var innerT = _rd.TempOf(well);

        Side side;
        Blocked? blocked = null;
        // 抢到切换锁之后再问一遍「这一刻还该这么切吗」——这一路的判断是拿上一拍的
        // 状态做的，抢锁期间目标或开关状态都可能已经变了
        Func<bool> stillWanted;
        // 这一刻这一路到底想干什么。**看的是意图（_wantEnabled）而不是设备上的 ENABLE**：
        // 切换序列第 ① 步自己就会把 ENABLE 关掉，拿它当「有没有人要控温」会把
        // 「我刚关的」和「别人要停」混成一件事
        if (!_wantEnabled[well])
        {
            // 停控 / 安全停机之后：全断——用户定的，不控温就一只都不合（加热棒、TEC 功率线都断）。
            // 输出关着不带载，不必等夹套凉。绝不自动切到电加热：没人要它加热。形态不管（没在控）
            _noCoolSaid[well] = null;
            if (OnSide(well, Side.Off)) return;
            side = Side.Off;
            // 抢锁期间有人发起了自整定（它刚把继电器扳好）：别在它后面把继电器断开
            stillWanted = () => !_wantEnabled[well] && !Tuning(well);
        }
        else
        {
            if (innerT.Setpoint is not { } sp) return;
            // 串级丢了釜内 Tr、回路在保持（夹套设定钳到釜内设定）：升温挡 / 降温挡不换挡，等 Tr 回来或回路停控。
            // 双向挡不设这个例外——一个 PID 的规矩就是继电器跟符号，保持期夹套设定钳到釜内设定、回路要制冷就接 TEC，
            // 不然负半轴写到断着的 TEC 引脚上白攒积分
            var holding = ((ITemperatureStatus)innerT).Holding is not null;
            if (holding && (_tecHeat || _regime[well] != Regime.Both)) return;
            if (!_tecHeat)
            {
                // 定挡那一刻夹套读不到（刚开机头一拍 / 探头脱落）：读到了再定
                if (_regime[well] == Regime.Undecided) DecideRegime(well, sp, innerT.Kind, "采集循环");
                if (holding) return;
                CheckHandover(well, sp);
            }
            if (WantedSide(well, sp, out blocked) is not { } w)
            {
                SayBlocked(well, blocked);          // 待定 / 「TEC 加热」启用下夹套还烫：一只都不动
                return;
            }
            side = w;
            // 「已经在那一侧」要两只继电器**和执行器形态**一起看：加热棒那只对了、TEC 功率线却断着
            // （被人手扳过 / 刚打开温控）也算没到位
            if (Settled(well, side)) { SayBlocked(well, blocked); return; }
            stillWanted = () => _wantEnabled[well] && _rd.TempOf(well).Setpoint is { } now
                                && WantedSide(well, now, out _) == w;
            // 双向挡按符号扳：把「为什么扳」记进日志和逐拍记录（切换序列自己的两句只说从哪到哪）
            if (_regime[well] == Regime.Both && !_tecHot[well] && !OnSide(well, side) && SideNow(well) != Side.Off)
            {
                var text = $"工位 {AB(well)} 双向挡：功率 {Demand(well):+0.0;−0.0} %（正加热 / 负制冷）已连续 {SignDwell.TotalSeconds:0} s 指向{SideName(side)}——" +
                           (side == Side.Tec ? "合 TEC 功率线、断加热棒" : "合加热棒、断 TEC 功率线") + "（PID 不复位，积分照旧）";
                _ctx.Log?.Invoke("info", $"{InstanceId} {text}");
                innerT.Note(text);
            }
        }

        if (side == Side.Electric)
        {
            if (!ElectricReady)
            {
                if (!_noElectricSaid[well])
                {
                    _noElectricSaid[well] = true;
                    _ctx.Log?.Invoke("warn", $"{InstanceId} 工位 {AB(well)} 需要升温但" +
                        NoElectricReason(well, innerT.Setpoint ?? double.NaN) + "（这一段只报一次）");
                }
                return;
            }
            _noElectricSaid[well] = false;
        }
        else if (_tecRelay && !ElectricReady)
        {
            return;     // 没有继电器可扳
        }
        // 降温挡但 TEC 接不上（冷水机关 / 夹套还烫）：说一次为什么，继电器照样落到「全断」自然凉——
        // 放热反应「抢冷 → 缓过来 → 再抢冷」是常见序列，原因变了 / 翻篇了再报
        SayBlocked(well, blocked);

        try
        {
            await SwitchAsync(well, side, ct, stillWanted).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // 别写「下拍再试」：反馈没核实上时 SwitchAsync 是把输出留在关闭状态退出的，
            // 下一拍这一路已经算「停控」，不会再自己切回来——这是要人去现场看的事
            // 开冷却水那一步就失败了：还没关输出、继电器也没动，下一拍照常重试——别说成「输出已关」
            _ctx.Log?.Invoke("error", $"{InstanceId} 工位 {AB(well)} " +
                $"自动切到{SideName(side)}失败：{ex.Message}" +
                (innerT.Enabled ? "——输出没动，下一拍再试" : "——控温输出已关闭且不会自行恢复，请到现场检查"));
        }
    }

    // ── 蒸回流（夹套跟随，需求 §2 + 0277 §B.5）────────────────────────
    //
    // 跟随环长在采集循环里：每拍读一次喂进来的 Tr，把夹套目标写成
    // min(Tr + ΔT, maxTj)，限速走 RampAsync（SPEED 限速写 TG，不是阶跃）。
    // 四道联锁：① Tr 无效/超时 → 停跟随（目标驻留，不追残值）；
    // ② 目标越过电加热阈值 → EnsureSourceAsync 全套切换序列；
    // ③ 安全动作（SafeStop / 停控温）→ 清跟随 + 压制旗，写到一半撞见就把
    //    输出关回去——下一拍绝不再把目标写回去；
    // ④ maxTj 之外还有设备自己的超温保护寄存器兜底（开机 ApplyProtection 写进去的）。

    /// <summary>开始跟随。第一拍不等采集循环，当下就把目标立起来。</summary>
    internal async Task StartRefluxAsync(int well, double deltaT, double maxTj, CancellationToken ct)
    {
        var t = _rd.TempOf(well);
        var dt = Math.Clamp(deltaT, 0.5, 30);
        var cap = Math.Min(maxTj, t.Limits.Max);    // 设备超温保护是死上限，指令说了不算

        // 起步就要有 Tr：跟随的目标就是 Tr+ΔT，没有 Tr 就没有目标——
        // 开一个空转的跟随比拒绝更糟
        if (!TrValid(well))
            throw new InvalidOperationException(
                $"工位 {AB(well)} 蒸回流开不了：釜内 Tr 没有有效读数" +
                "（宇电 Tr 探头没接、没连或断线）——夹套跟随的目标是 Tr+ΔT，没有 Tr 就没有目标");

        // 跟随的目标是 Tr+ΔT，永远在夹套之上——TEC 加热没启用时它整段都得靠电加热棒。
        // 电加热又用不了，那就是一开始就跟不动，与其跑一拍再报错不如现在说清楚
        if (!_tecHeat && !ElectricReady)
            throw new InvalidOperationException(
                $"工位 {AB(well)} 蒸回流开不了：「TEC 加热」没启用，夹套升温只能走电加热棒，" +
                (_links.Io is null ? "但这台没配 IO8R 切换模块" : "但 IO8R 没打开") +
                "——要么接上切换模块，要么在设备属性里打开「TEC 加热」");
        if (_tecHeat && cap > _threshold && !ElectricReady)
            _ctx.Log?.Invoke("warn", $"{InstanceId} 工位 {AB(well)} 蒸回流：夹套上限 {cap:F0} ℃ " +
                $"高于电加热切换阈值（TEC 接入上限）{_threshold:F0} ℃，但电加热不可用——跟到阈值那一刻会停跟随");

        NoteStart(well);
        lock (_refluxGate)
        {
            _refluxKill[well] = false;
            _reflux[well] = new Follow { DeltaT = dt, MaxTj = cap };
        }
        _ctx.Log?.Invoke("info", $"{InstanceId} 工位 {AB(well)} 蒸回流开始：Tj 跟随 Tr+{dt:F1} K，上限 {cap:F0} ℃");
        await FollowOnceAsync(well, ct).ConfigureAwait(false);
    }

    /// <summary>停止跟随（步收尾/下一步接管）：目标停在最后一次下发的值上，输出不动。</summary>
    internal void StopReflux(int well)
    {
        bool was;
        lock (_refluxGate)
        {
            was = _reflux[well] is not null;
            _reflux[well] = null;
        }
        if (was) _ctx.Log?.Invoke("info", $"{InstanceId} 工位 {AB(well)} 蒸回流结束：目标停在最后一次下发的值上");
    }

    /// <summary>安全路径清跟随（SafeStop / 停控温）：连压制旗一起立——
    /// 跟随环这拍要是已经越过检查点，写完会自己把输出关回去。</summary>
    internal bool SuppressReflux(int well)
    {
        lock (_refluxGate)
        {
            var was = _reflux[well] is not null;
            _reflux[well] = null;
            _refluxKill[well] = true;
            return was;
        }
    }

    internal bool RefluxActive(int well)
    {
        lock (_refluxGate) return _reflux[well] is not null;
    }

    private bool TrValid(int well)
    {
        if (double.IsNaN(_rd.TempOf(well).CurrentReactor)) return false;
        lock (_refluxGate) return _ctx.Clock() - _trFedAt[well] <= TrFreshWindow;
    }

    /// <summary>跟随环跑一拍。目标没动（&lt; 0.1 ℃）就不写寄存器——不白磨总线。</summary>
    private async Task FollowOnceAsync(int well, CancellationToken ct)
    {
        Follow? r;
        lock (_refluxGate) r = _reflux[well];
        if (r is null) return;

        var t = _rd.TempOf(well);
        if (!TrValid(well))
        {
            // 联锁①：探头断线/超时。目标驻留在最后一次下发的值上——
            // 追着残值走比停跟随危险得多
            lock (_refluxGate) _reflux[well] = null;
            _ctx.Log?.Invoke("error", $"{InstanceId} 工位 {AB(well)} 蒸回流已停：" +
                "釜内 Tr 无效或超过新鲜度窗（探头断线？）——夹套目标停在最后一次下发的值上");
            return;
        }

        var tg = Math.Clamp(t.CurrentReactor + r.DeltaT, t.Limits.Min, r.MaxTj);
        if (t.Setpoint is { } sp && Math.Abs(tg - sp) < 0.1) return;

        try
        {
            // 联锁②：越过阈值先走全套热源切换序列（关输出→切继电器→核反馈→重开）。
            // 「TEC 加热」不启用、冷水机已关时蒸回流是**升温挡钉死的**：夹套跟着 Tr+ΔT 走，Tr 一抖跟随目标就落到夹套之下，
            // 按方向定挡会判成降温、把加热棒断开——沸点平台上 Tr 本来就抖，继电器不能跟着它一拍一扳。
            // 冷水机已开是双向挡：继电器只看 PID 输出的符号（连续 3 s），跟随目标怎么抖都不按方向扳
            await EnsureSourceAsync(well, tg, TempChannelKind.Jacket, ct, heatingOnly: true).ConfigureAwait(false);
            // 限速写：SPEED 按设备最大变温能力限，TG 让温控器自己斜坡过去——
            // 不是每拍阶跃，Tj 的变化率有帽子
            await t.RampAsync(tg, t.Limits.MaxRatePerMin, TempChannelKind.Jacket, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            lock (_refluxGate) _reflux[well] = null;
            _ctx.Log?.Invoke("error", $"{InstanceId} 工位 {AB(well)} 蒸回流已停：{ex.Message}");
            return;
        }

        // 联锁③的竞态补偿：写的当口有人安全停机（旗已清 + 压制立着），
        // 刚才那笔 RampAsync 把输出重新打开了——关回去
        bool kill;
        lock (_refluxGate) kill = _reflux[well] is null && _refluxKill[well];
        if (kill)
        {
            try { await t.StopAsync(ct).ConfigureAwait(false); } catch { }
            _ctx.Log?.Invoke("warn", $"{InstanceId} 工位 {AB(well)} 蒸回流写目标与安全停机撞车——输出已重新关闭");
        }
    }

    // ── 热源切换 ─────────────────────────────────────────────────────

    /// <summary>
    /// 重读属性栏上那两项「随手改、立刻生效」的配置。
    ///
    /// DriverContext.Config 拿到的就是台面设备身上那一份 ParameterSet 本体，属性栏改的也是它，
    /// 所以这里读得到最新值。**每拍读一次而不是每次用时读**：那是个普通字典，
    /// 界面线程正在写、采集线程同时读是要出事的，一拍抓一次再用本地副本，
    /// 撞上了就当这一拍没改到（下一拍自然读到）。
    /// </summary>
    private void RefreshSwitches()
    {
        try
        {
            var tec = _ctx.Config.Str(F.TecHeat, "不启用") == "启用";
            if (tec != _tecHeat)
            {
                _tecHeat = tec;
                _ctx.Log?.Invoke("info", $"{InstanceId} 「TEC 加热」改为{(tec ? "启用" : "不启用")}" +
                    (tec ? $"——目标高于 {_threshold:F0} ℃ 才切电加热"
                         : "——冷水机已开双向挡（加热棒 ↔ TEC 跟功率符号走）、已关升温挡 / 降温挡，下发目标时定挡"));
                // 正在 TEC 侧的工位：回路的执行器形态跟着翻（双向 ⇄ 只制冷只改限幅，运行中也能改）
                for (var w = 0; w < 2; w++)
                {
                    if (_rd.TempOf(w).Actuator is { } m && m is ActuatorMode.Bidirectional or ActuatorMode.CoolOnly && SideNow(w) != Side.Electric)
                        try { _rd.TempOf(w).SetActuator(TecMode); } catch (Exception ex) { _ctx.Log?.Invoke("warn", $"{InstanceId} 工位 {AB(w)} 执行器形态没跟上：{ex.Message}"); }
                    Redecide(w);                       // 换了规矩：在控的路下一拍按此刻温度重新定挡
                }
            }
            var chiller = _ctx.Config.Str(F.Chiller, "已开") == "已开";
            if (chiller != _chiller)
            {
                _chiller = chiller;
                var text = chiller
                    ? "「冷水机」改为已开：在控的路这一拍起按双向挡走（一个 PID 两边出力，继电器跟功率符号）"
                    : "「冷水机」改为已关：没有冷却水不能开 TEC——在控的路这一拍按方向重新定挡（升温挡 / 降温挡），正在 TEC 侧的断开功率线、只能自然凉";
                _ctx.Log?.Invoke("info", $"{InstanceId} {text}");
                for (var w = 0; w < 2; w++)
                {
                    if (_wantEnabled[w]) _rd.TempOf(w).Note(text);
                    Redecide(w);
                }
            }
            _band = Math.Clamp(_ctx.Config.Num(F.Band, 2), 0.5, 10);
        }
        catch
        {
            // 撞上界面线程在写这一份配置。保持上一拍的值，下一拍再读
        }
    }

    /// <summary>规矩变了（「TEC 加热」/「冷水机」翻了）：在控的路下一拍按此刻温度重新定挡，符号计时、烫锁存一并清掉。</summary>
    private void Redecide(int well)
    {
        _regime[well] = Regime.Undecided;
        _signSide[well] = null;
        _signSince[well] = null;
        _tecHot[well] = false;
    }

    /// <summary>电加热通路可不可用：配了切换模块而且开起来了。</summary>
    internal bool ElectricReady => _links.Io is not null && _ioOk;

    /// <summary>TEC 侧回路的执行器形态：「TEC 加热」启用 = 双向；不启用 = 只制冷（正半轴那一极接的是加热棒 SSR、继电器断着）。</summary>
    private ActuatorMode TecMode => _tecHeat ? ActuatorMode.Bidirectional : ActuatorMode.CoolOnly;

    /// <summary>
    /// 继电器在哪个位置，回路就该是哪个形态。
    /// · 双向挡（冷水机已开、没在整定）：不管哪只合着都是「加热棒 + TEC 双向」——一个 PID、加热棒那张表的参数，继电器只是跟着符号走；
    ///   夹套烫（锁存立着）时按只加热（功率下限钳 0、TEC 断），同一张表之内换形态不复位。
    /// · 其余：TEC 侧 = 只制冷 / 双向（「TEC 加热」启用）；加热棒侧与全断（降温挡但 TEC 接不上）按只加热算：出力 0 自然凉、
    ///   积分不往制冷那边攒，交给加热棒时形态不用再换（一次交接就是合一只继电器）。
    /// </summary>
    private ActuatorMode ModeOf(int well, Side side)
    {
        if (!_tecHeat && _regime[well] == Regime.Both && !Tuning(well) && side != Side.Off)
            return _tecHot[well] ? ActuatorMode.HeatOnly : ActuatorMode.HeaterTec;
        return side == Side.Tec ? TecMode : ActuatorMode.HeatOnly;
    }

    /// <summary>TEC 反向输出加热启没启用（界面按能力问它）。</summary>
    internal bool TecHeating => _tecHeat;

    internal bool OnElectric(int well) => _electric[well];

    /// <summary>这一路此刻在哪个挡（界面印在热源牌子上）；没在控 / 不分挡（「TEC 加热」启用）为 null。</summary>
    internal string? RegimeText(int well)
    {
        if (well is not (0 or 1) || _tecHeat || !_wantEnabled[well]) return null;
        return _regime[well] switch
        {
            Regime.Heating => "升温挡（只用加热棒）",
            Regime.Cooling => !_chiller ? "降温挡·冷水机关（自然凉）"
                : TecDriving(well) ? "降温挡（TEC 制冷）" : "降温挡（等夹套凉到接入线）",
            Regime.Both => _tecHot[well]
                ? $"双向挡·夹套 > TEC 接入上限（TEC 断开、只加热棒，凉到 {_threshold - _hyst:0} ℃ 再放开）"
                : "双向挡（加热棒 ↔ TEC 跟功率符号走）",
            _ => "待定挡（夹套读不到）"
        };
    }

    private static string RegimeName(Regime r) => r switch
    {
        Regime.Heating => "升温挡",
        Regime.Cooling => "降温挡",
        Regime.Both => "双向挡",
        _ => "待定"
    };

    /// <summary>
    /// 这一路继电器的三个位置：全断（不控温：加热棒、TEC 功率线都断着）、TEC（功率线合、
    /// 加热棒断：降温或 TEC 加热）、电加热（加热棒合、功率线断：升温）。
    /// 功率线不经继电器的机器只有加热棒那一只，全断与 TEC 是同一个位置。
    /// </summary>
    private enum Side { Off, Tec, Electric }

    /// <summary>这一路的继电器是不是已经完全在某个位置（两只一起看）。</summary>
    private bool OnSide(int well, Side s) => s switch
    {
        Side.Electric => _electric[well] && !(_tecRelay && _tecOn[well]),
        Side.Tec => !_electric[well] && (!_tecRelay || _tecOn[well]),
        _ => !_electric[well] && !(_tecRelay && _tecOn[well])
    };

    /// <summary>继电器在位、回路的执行器形态也对（温控器 PID 方式没有形态，只看继电器）。</summary>
    private bool Settled(int well, Side s) => OnSide(well, s) && ActuatorOk(well, s);

    private bool ActuatorOk(int well, Side s) => _rd.TempOf(well).Actuator is not { } m || m == ModeOf(well, s);

    /// <summary>
    /// TEC 此刻是不是在出力的那个执行器：功率线经继电器的机器看继电器；硬线直连的机器软件断不了它，
    /// 看回路形态（温控器 PID 方式没有形态，没在加热棒侧就算 TEC 在驱动）。
    /// </summary>
    private bool TecDriving(int well)
        => !_electric[well] && (_tecRelay ? _tecOn[well] : _rd.TempOf(well).Actuator is not ActuatorMode.HeatOnly);

    private string SideName(Side s) => s switch
    {
        Side.Electric => "电加热",
        Side.Tec => "TEC",
        _ => _tecRelay ? "全断" : "TEC"
    };

    private static string ActuatorName(ActuatorMode m) => m switch
    {
        ActuatorMode.HeatOnly => "只加热",
        ActuatorMode.CoolOnly => "只制冷",
        ActuatorMode.HeaterTec => "加热棒 + TEC 双向",
        _ => "双向"
    };

    /// <summary>此刻这一路继电器在哪个位置（按影子说）。</summary>
    private Side SideNow(int well)
        => _electric[well] ? Side.Electric : _tecRelay && _tecOn[well] ? Side.Tec : Side.Off;

    /// <summary>TEC 功率线接不通时该怎么说：这一路既不能制冷也不能加热。</summary>
    private string NoTecReason(int well)
        => $"IO8R {(_links.IoOpenError is not null ? "没打开" : "没响应")}，而工位 {AB(well)} 的 TEC 功率线经它的 " +
           $"DO{_tecDoIdx[well]} 接通——接不通 TEC 就没电，这一路现在既不能制冷也不能加热，先把 IO8R 弄通";

    // ── 定挡（「TEC 加热」不启用，0333 / 0336）────────────────────────
    //
    // 一路 RD105 只有一个输出，正负号选系统：负 → 固态继电器 → 加热棒（升温系统），正 → H 桥 → TEC（降温系统），
    // 两边都按功率大小连续调。问题从来不在 PID，在「两个系统什么时候接在线上」。
    // · 冷水机已开（缺省）= 双向挡（0336，用户定的：反应会放热也会吸热，控得住就得两边随时能出力）：一个 PID 两边出力，
    //   继电器跟着输出符号走（同号连续 3 s 才扳）。
    // · 冷水机已关 = 升温挡 / 降温挡（0333）：下发目标时按方向定挡，中途温度漂动不换挡；升温挡不接 TEC、降温挡没水不接 TEC。

    /// <summary>
    /// 下发目标那一刻定挡。「冷水机」已开 = 双向挡（起步合哪只在 WantedSide 里按方向定）；已关：目标比当前温度低超过死区 =
    /// 降温挡，其余 = 升温挡（差不到一个死区的「降温」靠关加热棒自然凉，不值得接 TEC；加热棒本来就是维持温度的那套）。
    /// 比的是这个目标的被控量：釜内（串级）看新鲜的 Tr，夹套 / Tr 不新鲜看 Tj。**夹套读不到就不定**（判不了方向别瞎扳继电器，
    /// 尤其别扳向加热侧），采集循环读到了再定。
    /// </summary>
    private void DecideRegime(int well, double target, TempChannelKind kind, string how, Regime? force = null)
    {
        var t = _rd.TempOf(well);
        var tj = t.CurrentJacket;
        if (double.IsNaN(tj)) { _regime[well] = Regime.Undecided; return; }
        var byTr = kind == TempChannelKind.Reactor && TrValid(well);
        var cur = byTr ? t.CurrentReactor : tj;
        var r = _chiller ? Regime.Both : force ?? (target < cur - _band ? Regime.Cooling : Regime.Heating);
        var was = _regime[well];
        _regime[well] = r;
        if (r == was) return;
        _signSide[well] = null;
        _signSince[well] = null;
        var what = byTr ? "釜内" : "夹套";
        string text;
        switch (r)
        {
            case Regime.Both:
                // 进双向挡那一刻夹套已经高过接入线（上限 − 滞回）：先不接 TEC——跟 0333 一样不把 TEC 接到烫夹套上
                _tecHot[well] = tj > _threshold - _hyst;
                text = $"工位 {AB(well)} 双向挡：目标 {target:F1} ℃，{what} {cur:F1} ℃（「冷水机」已开）——一个 PID 两边出力，正给加热棒、负给 TEC 制冷，" +
                       $"继电器跟着功率符号走（同号连续 {SignDwell.TotalSeconds:0} s 才扳）；夹套 > TEC 接入上限 {_threshold:F0} ℃ 时 TEC 断开、只加热棒出力，" +
                       $"凉到 {_threshold - _hyst:F0} ℃ 再放开" +
                       (_tecHot[well] ? $"。此刻夹套 {tj:F1} ℃ 已高过 {_threshold - _hyst:F0} ℃：先只加热棒" : "");
                break;
            case Regime.Heating:
                text = $"工位 {AB(well)} 升温挡：目标 {target:F1} ℃，{what} {cur:F1} ℃——只用加热棒（过冲靠加热棒提前收功率，过了头关加热棒自然凉），全程不接 TEC";
                break;
            default:
                text = $"工位 {AB(well)} 降温挡：目标 {target:F1} ℃ 比{what} {cur:F1} ℃ 低超过 {_band:F1} K，但「冷水机」没标开：TEC 没水不接，" +
                       "加热棒与功率线都断着自然凉；自然凉到了目标就交给加热棒维持";
                break;
        }
        _ctx.Log?.Invoke("info", $"{InstanceId} {text}（{how}）");
        t.Note(text);
    }

    /// <summary>
    /// 双向挡的「夹套烫」锁存按此刻夹套更新：高过 TEC 接入上限立起（TEC 断开、只加热棒、功率下限钳 0），
    /// 凉到 上限 − 滞回 落下（放开 TEC）。读不到夹套不动锁存、按不许接算。返回此刻许不许接 TEC。
    /// </summary>
    private bool TecPermitted(int well, double tj, out Blocked? blocked)
    {
        blocked = null;
        if (double.IsNaN(tj)) return false;
        if (_tecHot[well])
        {
            if (tj <= _threshold - _hyst)
            {
                _tecHot[well] = false;
                var text = $"工位 {AB(well)} 双向挡：夹套凉到 {tj:F1} ℃（≤ 接入线 {_threshold - _hyst:F0} ℃）——放开 TEC，功率下限回到 −上限，要制冷时按符号合 TEC 功率线";
                _ctx.Log?.Invoke("info", $"{InstanceId} {text}");
                _rd.TempOf(well).Note(text);
            }
        }
        else if (tj > _threshold)
        {
            _tecHot[well] = true;
        }
        if (_tecHot[well])
            blocked = new Blocked("hot-both", "warn",
                $"工位 {AB(well)} 双向挡：夹套 {tj:F1} ℃ 高过 TEC 接入上限 {_threshold:F0} ℃（TEC 模块自身耐温）——TEC 功率线断开、只加热棒出力，" +
                $"功率下限钳到 0（PID 不复位）；凉到 {_threshold - _hyst:F0} ℃（上限 − 滞回）再放开。这一段只能靠加热棒收功率自然凉（这一段只报一次）");
        return !_tecHot[well];
    }

    /// <summary>
    /// 双向挡此刻回路要的功率（PID 那一侧，正 = 加热、负 = 制冷）：上位机 PID 直接看它算出的量；温控器 PID 看写进 / 读回的占空比，
    /// 按「加热棒占空比」的符号翻成正 = 加热。读不到为 NaN。
    /// </summary>
    private double Demand(int well)
    {
        var t = _rd.TempOf(well);
        if (t.PidOutput is { } pid) return pid;
        var d = t.LastDuty;
        return double.IsNaN(d) ? double.NaN : d * _heaterSign;
    }

    /// <summary>
    /// 双向挡：功率符号说该在哪一侧。|功率| ≤ 死区不表态（保持现状、计时清零）；跟此刻继电器位置一样直接返回；
    /// 指向另一侧要连续 SignDwell 才返回那一侧，没够返回 null（保持现状）。
    /// </summary>
    private Side? SignSide(int well)
    {
        var d = Demand(well);
        Side? s = double.IsNaN(d) || Math.Abs(d) <= SignDeadbandPercent ? null : d > 0 ? Side.Electric : Side.Tec;
        var now = SideNow(well);
        if (s is null || s == now)
        {
            _signSide[well] = null;
            _signSince[well] = null;
            return s;
        }
        var at = _ctx.Clock();
        if (_signSide[well] != s)
        {
            _signSide[well] = s;
            _signSince[well] = at;
        }
        return at - _signSince[well]!.Value >= SignDwell ? s : null;
    }

    /// <summary>这一路此刻的被控量：釜内串级看新鲜的 Tr，其余看 Tj（读不到 NaN）。</summary>
    private double Controlled(int well)
    {
        var t = _rd.TempOf(well);
        return t.Kind == TempChannelKind.Reactor && TrValid(well) ? t.CurrentReactor : t.CurrentJacket;
    }

    /// <summary>
    /// 降温挡（冷水机已关）交给加热棒（一次交接，不回头）：TEC 没接着（没水）、自然凉到了目标——该加热棒接手维持了。
    /// 0333 那条「TEC 在出力、收到 0 温度仍不高于目标够 60 s 才交接」随 0336 撤掉：冷水机已开是双向挡（继电器跟符号走，
    /// 没有交接这回事），降温挡里 TEC 根本接不上。
    /// </summary>
    private void CheckHandover(int well, double sp)
    {
        if (_regime[well] != Regime.Cooling || _electric[well] || TecDriving(well)) return;
        var t = _rd.TempOf(well);
        var cur = Controlled(well);
        if (double.IsNaN(cur) || cur > sp) return;
        var what = t.Kind == TempChannelKind.Reactor && TrValid(well) ? "釜内" : "夹套";
        _regime[well] = Regime.Heating;
        var text = $"工位 {AB(well)} 降温挡 → 升温挡：TEC 没接着，{what}自然凉到了目标（{cur:F2} ℃ ≤ {sp:F2} ℃）——加热棒接手维持，这一步不再接 TEC（新目标再定挡）";
        _ctx.Log?.Invoke("info", $"{InstanceId} {text}");
        t.Note(text);
    }

    /// <summary>降温挡里 TEC 为什么接不上（按段只报一次；Key 一样就不重复）。</summary>
    private sealed record Blocked(string Key, string Level, string Text);

    /// <summary>
    /// 这个目标该落在哪一侧：null = 一只都不动（待定 / 「TEC 加热」启用下夹套还烫接不回 TEC）。
    /// blocked 说的是「想接 TEC 而接不上」的原因（Off、null 或双向挡夹套烫时才有）。
    ///
    /// 「TEC 加热」启用：原来那条——超过阈值才要电加热，阈值以内 TEC 正反都能出力；回 TEC 要夹套凉到接入线以下。
    /// 不启用：双向挡 = 夹套烫（锁存）就加热棒那只（只加热形态）；否则继电器跟功率符号走——fresh（刚下发目标）按方向起步：
    /// 目标比被控量低超过死区合 TEC、其余合加热棒；之后符号连续 SignDwell 指向另一侧才扳，没表态保持现状。
    /// 升温挡 = 加热棒；降温挡 = 冷水机已关不接 TEC，全断自然凉。
    /// </summary>
    private Side? WantedSide(int well, double target, out Blocked? blocked, bool fresh = false)
    {
        blocked = null;
        var t = _rd.TempOf(well);
        var tj = t.CurrentJacket;
        var hot = double.IsNaN(tj) || tj > _threshold - _hyst;
        if (_tecHeat)
        {
            if (target > _threshold) return Side.Electric;
            if (_electric[well] && hot) { blocked = HotJacket(well, tj); return null; }
            return Side.Tec;
        }
        switch (_regime[well])
        {
            case Regime.Both:
            {
                if (!TecPermitted(well, tj, out blocked)) return Side.Electric;
                if (!fresh)
                {
                    if (SignSide(well) is { } bySign) return bySign;
                    var now = SideNow(well);
                    if (now != Side.Off) return now;             // 符号没表态 / 还在计时：保持现状
                }
                else
                {
                    _signSide[well] = null;                      // 新目标：方向重新算，之前的计时作废
                    _signSince[well] = null;
                }
                // 起步（刚下发目标 / 一只都没合着）：按方向——低于被控量超过死区要制冷，其余加热棒（保温要的功率是正的）
                var cur = Controlled(well);
                return !double.IsNaN(cur) && target < cur - _band ? Side.Tec : Side.Electric;
            }
            case Regime.Heating:
                return Side.Electric;
            case Regime.Cooling:
                if (!_chiller)
                {
                    blocked = new Blocked("chiller", "warn",
                        $"工位 {AB(well)} 降温挡（目标 {target:F1} ℃），但设备属性「冷水机」标为已关：TEC 没有冷却水不能开——" +
                        "加热棒与 TEC 功率线都断着，只能自然凉，到了目标加热棒接手维持。要 TEC 制冷：先开冷水机，再把「冷水机」改成已开（当拍生效，之后按双向挡走）（这一段只报一次）");
                    return Side.Off;
                }
                if (hot) { blocked = HotJacket(well, tj); return Side.Off; }
                return Side.Tec;
            default:
                return null;
        }
    }

    private Blocked HotJacket(int well, double tj) => new("hot", "error",
        $"工位 {AB(well)} 要降温，但夹套 {tj:F1} ℃ 还高于 TEC 接入线 {_threshold - _hyst:F0} ℃（上限 − 滞回）——" +
        "TEC 不能接到这么烫的夹套上，这一段只能等它自然凉，本路暂时没有制冷能力（这一段只报一次）");

    /// <summary>「想接 TEC 而接不上」按段只报一次：原因变了再报，接上了 / 不再要 TEC 了翻篇。</summary>
    private void SayBlocked(int well, Blocked? b)
    {
        if (b is null) { _noCoolSaid[well] = null; return; }
        if (_noCoolSaid[well] == b.Key) return;
        _noCoolSaid[well] = b.Key;
        _ctx.Log?.Invoke(b.Level, $"{InstanceId} {b.Text}");
        _rd.TempOf(well).Note(b.Text);
    }

    /// <summary>电加热用不了时，按当前模式说清楚为什么这个目标上不去。</summary>
    private string NoElectricReason(int well, double target)
    {
        var why = _links.Io is null ? "但这台没配 IO8R 切换模块" : "但 IO8R 没打开";
        if (_tecHeat)
            return $"目标 {target:F1} ℃ 高于电加热切换阈值（TEC 接入上限）{_threshold:F0} ℃，{why}——电加热用不了，这个目标上不去";
        var tj = _rd.TempOf(well).CurrentJacket;
        return $"「TEC 加热」没启用，升温只能走电加热棒（目标 {target:F1} ℃ 高过夹套 {tj:F1} ℃），{why}" +
               "——这一路现在只降得下去、升不上来。要用 TEC 反向输出加热，请在设备属性里把「TEC 加热」打开";
    }

    /// <summary>
    /// 这个目标下发下去会不会接 TEC 制冷：比被控量（釜内串级看新鲜的 Tr，其余看夹套）低超过死区，而且这台机器降温走 TEC——
    /// 「TEC 加热」启用时阈值以内都走 TEC；不启用时「冷水机」标已开（双向挡）才接 TEC，标已关的降温挡只自然凉。
    /// 判不出方向（读不到温度）是 false。HMI 手动开温控时拿它决定要不要先问开不开冷却水。
    /// </summary>
    internal bool WouldCool(int well, double target, TempChannelKind kind)
    {
        var t = _rd.TempOf(well);
        var cur = kind == TempChannelKind.Reactor && TrValid(well) ? t.CurrentReactor : t.CurrentJacket;
        if (double.IsNaN(cur) || !(target < cur - _band)) return false;
        return _tecHeat ? target <= _threshold : _chiller;
    }

    /// <summary>HMI 降温弹窗里人选了「不开，直接制冷」：下一次下发这一路接 TEC 不开冷却水。</summary>
    internal void SkipWaterNext(int well) => _skipWaterNext[well] = true;

    /// <summary>下发目标时取用「下一次不开水」：这次控温按它定，用过复位（配方 / 正常下发照常先通水）。</summary>
    internal void TakeWaterChoice(int well)
    {
        _waterDeclined[well] = _skipWaterNext[well];
        _skipWaterNext[well] = false;
        _declinedSaid[well] = false;
    }

    /// <summary>
    /// 下发目标（打开温控）前定挡、把继电器合到对的一只上：双向挡按方向起步（低于被控量超过死区合 TEC 功率线，其余合加热棒），
    /// 之后继电器跟功率符号走；升温挡 → 加热棒；降温挡（冷水机已关）→ 全断自然凉。经继电器的机器：不控温时两只都断着（用户定的）——
    /// 不合上目标写进温控器也是空写，现场「降温没反应」就是它。夹套读不到（判不出方向）一只都不合，采集循环读到了再定。
    /// 蒸回流（heatingOnly）每拍都来：双向挡里不按方向重算（跟随目标抖不能扳继电器，只看符号），冷水机已关钉死升温挡。
    /// 「TEC 加热」启用下电加热 → TEC 的回切要等夹套凉到接入线以下，由采集循环在条件满足的那一拍执行。
    /// </summary>
    internal async Task EnsureSourceAsync(int well, double target, TempChannelKind kind, CancellationToken ct, bool heatingOnly = false)
    {
        if (!_tecHeat) DecideRegime(well, target, kind, heatingOnly ? "蒸回流" : "下发目标", heatingOnly ? Regime.Heating : null);
        var side = WantedSide(well, target, out var blocked, fresh: !heatingOnly);
        if (side is null)
        {
            SayBlocked(well, blocked);
            return;
        }
        if (side == Side.Electric)
        {
            if (!ElectricReady) throw new InvalidOperationException(NoElectricReason(well, target));
            await SwitchAsync(well, Side.Electric, ct).ConfigureAwait(false);
            return;
        }

        // TEC 侧 / 全断（降温挡但 TEC 接不上）。功率线经继电器而 IO8R 不通：什么都接不上，拒绝并写明
        if (_tecRelay && !ElectricReady) throw new InvalidOperationException(NoTecReason(well));
        SayBlocked(well, blocked);
        await SwitchAsync(well, side.Value, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 往要水的那一侧切（接 TEC / 硬线直连进双向形态）：从开阀到序列走完，这一路认领着冷却水（_waterClaim）——
    /// 通水延时里继电器还没合，不认领的话对账会把阀和冷水机关掉，延时过了锁里再开一遍。
    /// 不切了 / 失败了就放手，阀由对账收回去。
    /// </summary>
    private async Task SwitchAsync(int well, Side to, CancellationToken ct, Func<bool>? stillWanted = null)
    {
        if (!(to == Side.Tec || !_tecRelay && ModeOf(well, to) == ActuatorMode.HeaterTec))
        {
            await SwitchSequenceAsync(well, to, ct, stillWanted).ConfigureAwait(false);
            return;
        }
        Interlocked.Increment(ref _waterClaim[well]);
        try { await SwitchSequenceAsync(well, to, ct, stillWanted).ConfigureAwait(false); }
        finally { Interlocked.Decrement(ref _waterClaim[well]); }
    }

    /// <summary>
    /// 切换动作全套序列（需求 §2.4）：关输出 → 切继电器 → 核反馈 → 重开输出。
    ///
    /// stillWanted：拿到锁之后再问一遍「这一刻还该往这边切吗」。采集循环那条路是拿着
    /// 上一拍的设定值做的判断，抢锁期间目标完全可能已经改了——不复核就会把刚下发的
    /// 指令的热源挪走。明确下发那条路不传它：那是人或配方当下的决定，不用再问。
    /// </summary>
    private async Task SwitchSequenceAsync(int well, Side to, CancellationToken ct, Func<bool>? stillWanted)
    {
        // 继电器写只在配了 IO8R 的机器上发生（_electric / _tecRelay 没有 IO8R 一律 false）；
        // 没配的机器走到这里只是对齐形态（关输出 → 换形态 → 开输出）
        var io = _links.Io;
        // ⓪ 制冷先通水（用户定的）：真要接 TEC 才先开冷却水阀、等通水延时再动继电器——
        //    继电器机 = 往 TEC 侧扳；硬线直连 = 进双向形态（它断不了 TEC 的电，形态就是通路）。
        //    继电器机往加热棒侧扳不通水（0522：升温时冷水机与阀都关，降温才开）。
        //    在拿切换锁**之前**等：等水那几秒不能把安全停机挡在锁外面；原来的热源这几秒照常出力。
        //    等完了要是已经不该切了（被停 / 目标改了），阀由采集循环的对账收回去
        if (to == Side.Tec || !_tecRelay && ModeOf(well, to) == ActuatorMode.HeaterTec)
            await EnsureWaterAsync(well, ct).ConfigureAwait(false);
        await _switchLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var relaysOk = OnSide(well, to);
            if (relaysOk && ActuatorOk(well, to)) return;   // 抢锁期间别人已经切完了 / 本来就在位
            if (stillWanted is not null && !stillWanted()) return;
            var gen = Volatile.Read(ref _stopGen[well]);
            var innerT = _rd.TempOf(well);
            var mode = ModeOf(well, to);
            if (relaysOk && innerT.Actuator is { } cur && HostControlLoop.HeaterTable(cur) == HostControlLoop.HeaterTable(mode))
            {
                // 换成「加热棒 + TEC 双向」且真要接 TEC（往 TEC 侧扳 / 硬线直连进双向形态）才通水；
                // 继电器机留在加热棒侧不通水（0522：升温时冷水机与阀都关）
                if (to == Side.Tec || !_tecRelay && mode == ActuatorMode.HeaterTec)
                    await EnsureWaterAsync(well, ct).ConfigureAwait(false);
                // 继电器在位、只是同一张表之内换形态（只加热 ⇄ 加热棒 + TEC：夹套过 / 回 TEC 接入线；TEC 双向 ⇄ 只制冷）：
                // 只改输出限幅、PID 不复位，回路运行中也许改——不必为它关一秒输出
                innerT.SetActuator(mode);
                _ctx.Log?.Invoke("info", $"{InstanceId} 工位 {AB(well)} 执行器形态对齐：{SideName(to)}侧按{ActuatorName(mode)}算（同一组参数，只改限幅，输出不关）");
                return;
            }
            var toElectric = to == Side.Electric;
            // 锁外开过阀了；等锁期间阀被关了（另一路断 TEC 时的收尾）就再开——这时等水照样要等。
            // 同上面 ⓪：继电器机往加热棒侧不通水
            if (to == Side.Tec || !_tecRelay && mode == ActuatorMode.HeaterTec)
                await EnsureWaterAsync(well, ct).ConfigureAwait(false);
            // 切完要不要把输出重开，看的是**意图**不是设备上的 ENABLE：
            // 停控时 TG 还留着，光看 Setpoint 分不出「有没有人要控温」；而 ENABLE
            // 在切换序列里被我们自己关过，上一笔切换失败的话它会一直是 0
            var wasEnabled = _wantEnabled[well];
            _ctx.Log?.Invoke("info", $"{InstanceId} 工位 {AB(well)} " + (relaysOk
                ? $"执行器形态对齐：{SideName(to)}侧按{ActuatorName(mode)}算（先关输出）"
                : $"热源切换：{SideName(SideNow(well))} → {SideName(to)}（先关输出）"));

            await innerT.EnableAsync(false, ct).ConfigureAwait(false);              // ① 带载切继电器 = 触点拉弧（只关输出，回路挂起，目标留着）
            // ② 先断后通：加热棒继电器与 TEC 功率继电器不许同时闭合（TEC 和加热棒一起带电）。
            //    该断的先断、该合的再合；每只写完就记影子——中途总线掉了，下一拍只补没写成的那一只
            if (to != Side.Electric && _electric[well])
            {
                await io!.SetRelayAsync(_doIdx[well], false, ct).ConfigureAwait(false);
                _electric[well] = false;
            }
            if (to != Side.Tec && _tecRelay && _tecOn[well])
            {
                await io!.SetRelayAsync(_tecDoIdx[well], false, ct).ConfigureAwait(false);
                _tecOn[well] = false;
            }
            // 加热：先断 TEC（上面），确认另一路没在制冷后关冷却水，再接加热棒（下面）——用户定的顺序
            if (to != Side.Tec) await ReleaseWaterAsync(ct).ConfigureAwait(false);
            if (to == Side.Electric && !_electric[well])
            {
                await io!.SetRelayAsync(_doIdx[well], true, ct).ConfigureAwait(false);
                _electric[well] = true;
            }
            if (to == Side.Tec && _tecRelay && !_tecOn[well])
            {
                await io!.SetRelayAsync(_tecDoIdx[well], true, ct).ConfigureAwait(false);
                _tecOn[well] = true;
            }

            if (_feedback && !relaysOk)                                             // ③ 接了反馈就必须核实
            {
                var deadline = Environment.TickCount64 + 2000;
                var ok = false;
                while (!ok && Environment.TickCount64 < deadline)
                {
                    var di = await io!.ReadInputsAsync(ct).ConfigureAwait(false);
                    ok = di[_diIdx[well]] == toElectric;
                    if (!ok) await Task.Delay(100, ct).ConfigureAwait(false);
                }
                if (!ok)
                {
                    _ctx.Log?.Invoke("error", $"{InstanceId} 工位 {AB(well)} 切换命令已发，" +
                        $"但反馈 DI{_diIdx[well]} 2 秒内没对上——控温输出保持关闭");
                    throw new InvalidOperationException(
                        $"工位 {AB(well)} 热源切换未核实：继电器命令已发，但反馈 DI{_diIdx[well]} 没有跟上——" +
                        "接触器可能没动作。控温输出保持关闭，请到现场检查");
                }
            }

            // ③.5 继电器扳到哪一侧，回路的执行器形态跟着换（上位机 PID：加热棒 / 全断 = 只加热、TEC = 只制冷或双向；
            //     双向挡里两侧都是「加热棒 + TEC 双向」——同一个形态、同一个 PID，扳继电器什么都不复位；温控器 PID 下是空操作）。
            //     要在重开输出之前换——带着 TEC 的积分去驱动加热棒只会过冲
            innerT.SetActuator(mode);
            // ④ 原来开着、而且这两秒里没人喊停，才重开输出
            if (wasEnabled && innerT.Setpoint is not null && Volatile.Read(ref _stopGen[well]) == gen)
                await innerT.EnableAsync(true, ct).ConfigureAwait(false);
            else if (wasEnabled)
                _ctx.Log?.Invoke("info", $"{InstanceId} 工位 {AB(well)} 切换期间控温被停——输出保持关闭");
            if (relaysOk)
                _ctx.Log?.Invoke("info", $"{InstanceId} 工位 {AB(well)} 执行器形态已对齐（{ActuatorName(mode)}），输出{(wasEnabled ? "重开" : "关着")}");
            else
                _ctx.Log?.Invoke("info", $"{InstanceId} 工位 {AB(well)} 已切至{SideName(to)}" +
                    (_tecRelay
                        ? to switch
                        {
                            Side.Electric => "（TEC 功率线断开、加热棒继电器闭合）",
                            Side.Tec => "（加热棒继电器断开、TEC 功率线闭合）",
                            _ => "（加热棒继电器与 TEC 功率线都断开）"
                        }
                        : "") +
                    (_feedback ? "（反馈已核实）" : "（无反馈回路，未核实）"));
        }
        finally
        {
            _switchLock.Release();
        }
    }

    // ── 安全停 ───────────────────────────────────────────────────────

    /// <summary>关本工位控温输出 + 把本工位切换继电器断开（落回 TEC 侧），采集不停。</summary>
    public async ValueTask<IReadOnlyList<string>?> SafeStopAsync(int well, CancellationToken ct)
    {
        if (well is not (0 or 1)) return Array.Empty<string>();
        var notes = new List<string>();

        // 先清跟随（联锁③）：不清的话下一拍跟随环又把目标写回去、把输出重新打开。
        // 正在自整定也一并落旗：下面停输出会把整定器清掉，继电器照常断开
        Volatile.Write(ref _tuning[well], false);
        NoteStop(well);
        if (SuppressReflux(well))
            notes.Add($"工位 {AB(well)} 蒸回流跟随已停——安全停机后不会再有目标写回去");

        var inner = await _rd.SafeStopAsync(well, ct).ConfigureAwait(false);
        if (inner is not null) notes.AddRange(inner);

        if (_links.Io is { } io && _ioOk)
        {
            // 断继电器也要拿切换锁：不拿的话，跟随环那边正走到一半的切换序列
            // 会在安全停机之后把继电器重新合到电加热侧
            await _switchLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await io.SetRelayAsync(_doIdx[well], false, ct).ConfigureAwait(false);
                _electric[well] = false;
                if (_tecRelay && _tecOn[well])
                {
                    // 停控即全断（用户定的）。E 级程序接着要降温的话，
                    // 下发目标那一刻 EnsureSourceAsync 会把功率线重新合上
                    await io.SetRelayAsync(_tecDoIdx[well], false, ct).ConfigureAwait(false);
                    _tecOn[well] = false;
                }
                notes.Add($"工位 {AB(well)} 热源切换继电器已断开（落回 TEC 侧）" +
                          (_tecRelay ? "；TEC 功率线也已断开（停控即全断，再打开温控时按升温 / 降温接通）" : ""));
            }
            catch (Exception ex)
            {
                notes.Add($"断开工位 {AB(well)} 热源切换继电器失败：{ex.Message}——" +
                          "兜底靠 IO8R 的总线错误复位（部署清单第 1 条，必须已改成「复位」）");
            }
            finally
            {
                _switchLock.Release();
            }

            // 冷却水阀跟着关（用户定的）——但阀是整机一只：另一路 TEC 还接着就不关，
            // 断了水 TEC 会干烧。安全停机连手动开的也关。联动冷水机：先停冷水机，停稳了才关阀（采集循环接着看）
            if (_waterOn && !_waterClosing)
            {
                var other = 1 - well;
                if (NeedsWater(other))
                    notes.Add($"冷却水阀没关：工位 {AB(other)} 还在制冷，断水 TEC 会干烧——那一路停了会自动关");
                else
                {
                    try
                    {
                        await SetWaterAsync(false, ct).ConfigureAwait(false);
                        _waterManual = false;
                        notes.Add(_waterOn
                            ? $"冷水机停机已发：冷却水阀（DO{_waterDo}）等冷水机停稳再关（冷水机还在转就关阀会憋泵）"
                            : $"冷却水阀（DO{_waterDo}）已关");
                    }
                    catch (Exception ex)
                    {
                        notes.Add($"关冷却水阀（DO{_waterDo}）失败：{ex.Message}");
                    }
                }
            }
        }
        return notes;
    }

    // ── 冷却水电磁阀（整机一只，IO8R 的 DO{_waterDo}）──────────────────

    internal bool WaterAvailable => _links.Io is not null && _ioOk;
    internal bool WaterOn => _waterOn;

    /// <summary>冷水机最近读到的运行状态；没配 / 读不到是 null。</summary>
    internal int? ChillerState => _links.Chiller is not null && _chillerStateKnown ? _chillerState : (int?)null;

    /// <summary>关水挂着：冷水机停机已发，阀等它停稳再关（这时 WaterOn 还是 true）。</summary>
    internal bool WaterClosing => _waterClosing;

    /// <summary>
    /// 开水在途（面板开关转菊花）：在等通水延时；或联动冷水机、开机后还没读到「正常运行」——
    /// 冷水机报故障 / 等满 ChillerStartWarn 就不算了（那时告警已经说了它为什么没转起来，开关别一直转）。
    /// </summary>
    internal bool WaterOpening
    {
        get
        {
            if (!_waterOn || _waterClosing) return false;
            if (Volatile.Read(ref _waterLeading) > 0) return true;
            if (_links.Chiller is null || _waterOpenAt is not { } t0) return false;
            if (_chillerStateKnown && _chillerState is 2 or ChillerClient.Fault) return false;
            return _ctx.Clock() - t0 < ChillerStartWarn;
        }
    }

    /// <summary>冷却水已经备好：阀开着、没在关，联动冷水机的话读到它在转（准备启动 / 正常运行）。</summary>
    internal bool WaterReady => _waterOn && !_waterClosing
        && (_links.Chiller is null || _chillerStateKnown && ChillerClient.Running(_chillerState));

    /// <summary>冷水机出毛病时面板状态签上那个短词：读不到（联动不起来）/ 报故障；不联动、一切正常是 null。</summary>
    internal string? WaterAlert => _links.Chiller is null ? null
        : !_chillerStateKnown ? "冷水机读不到"
        : _chillerState == ChillerClient.Fault ? "冷水机故障"
        : null;

    /// <summary>面板开关旁边那句：联动冷水机时说冷水机在干什么、关水在等什么；不联动 null。</summary>
    internal string? WaterStatus
    {
        get
        {
            if (_links.Chiller is null) return null;
            var st = ChillerClient.StateName(ChillerState);
            if (_waterClosing) return $"冷水机{st}·停稳后关阀";
            if (WaterOpening) return $"开启中·冷水机{st}";
            return _waterOn ? $"已开·冷水机{st}" : $"已关·冷水机{st}";
        }
    }

    /// <summary>
    /// 开 / 关冷却水。IO8R 不通就拒绝（说清为什么），不留一个看着开了实际没动的开关。
    /// 联动冷水机（用户定的标准开关顺序）：开 = 先开阀、再开冷水机；关 = 先停冷水机，**停稳了才关阀**——
    /// 这里只发停机、把关水挂起，阀由 AdvanceWaterCloseAsync 每拍看冷水机状态再关（冷水机已经停着就当场关）。
    /// 所以 open = false 返回时阀可能还开着：WaterOn 照实说，WaterStatus 说在等什么。
    /// </summary>
    internal async Task SetWaterAsync(bool open, CancellationToken ct)
    {
        if (_links.Io is null)
            throw new InvalidOperationException("这台没配 IO8R 模块，冷却水阀没有去处");
        if (!_ioOk)
            throw new InvalidOperationException($"IO8R 没打开，冷却水阀（DO{_waterDo}）开关不了——先把 IO8R 弄通");
        await _waterLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (open) await OpenWaterLockedAsync(ct).ConfigureAwait(false);
            else await BeginCloseWaterLockedAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _waterLock.Release();
        }
    }

    /// <summary>开水（拿着 _waterLock）：① 阀 → ② 冷水机。关水挂着的话撤销它——阀本来就开着，冷水机重新开起来。</summary>
    private async Task OpenWaterLockedAsync(CancellationToken ct)
    {
        var cancelled = _waterClosing;
        _waterClosing = false;
        await RelayWaterAsync(true, ct).ConfigureAwait(false);
        if (cancelled)
            _ctx.Log?.Invoke("info", $"{InstanceId} 关水撤销：又要冷却水了，冷却水阀（DO{_waterDo}）留着开、冷水机重新开机");
        if (_links.Chiller is null) return;
        // 阀开了才开冷水机（用户定的顺序）。手里的状态可能是上一拍的（人刚在冷水机上停了它）：
        // 照发不误，在转的冷水机收到开机什么都不变
        _waterOpenAt = _ctx.Clock();
        _chillerStartPending = true;
        await TryStartChillerLockedAsync(ct).ConfigureAwait(false);
    }

    /// <summary>发开机（拿着 _waterLock）。发不成不回滚阀——阀开着不危险——下一拍补发（TickChillerAsync）。</summary>
    private async Task TryStartChillerLockedAsync(CancellationToken ct)
    {
        if (_links.Chiller is not { } ch || !_chillerStartPending) return;
        try
        {
            await ch.StartAsync(ct).ConfigureAwait(false);
            _chillerStartPending = false;
            _startFailSaid = false;
            _chillerStartAt = _ctx.Clock();
            _chillerStartSlowSaid = false;
            _ctx.Log?.Invoke("info", $"{InstanceId} 冷水机开机指令已发（冷却水阀 DO{_waterDo} 已先打开）");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            if (!_startFailSaid)
            {
                _startFailSaid = true;
                _ctx.Log?.Invoke("warn", $"{InstanceId} 冷水机开机指令没发出去：{ex.Message}——冷却水阀已开着，接着补发（连续失败只报第一次）");
            }
        }
    }

    /// <summary>
    /// 关水（拿着 _waterLock）：不联动冷水机就直接关阀；联动的先停冷水机、挂起关水，停稳了 AdvanceWaterCloseAsync 再关阀。
    /// </summary>
    private async Task BeginCloseWaterLockedAsync(CancellationToken ct)
    {
        if (_links.Chiller is null)
        {
            await RelayWaterAsync(false, ct).ConfigureAwait(false);
            return;
        }
        if (!_waterOn && !_waterClosing) return;
        if (!_waterClosing)
        {
            _waterClosing = true;
            _waterClosingSince = _ctx.Clock();
            _stopSent = false;
            _stopFailSaid = false;
            _closingSlowSaid = false;
            _chillerStartAt = null;
            _chillerStartPending = false;
            _waterOpenAt = null;
            _ctx.Log?.Invoke("info", $"{InstanceId} 关冷却水：先停冷水机，等它停稳再关阀（DO{_waterDo}）");
        }
        await AdvanceCloseLockedAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 关水挂着时往前推一步（拿着 _waterLock）：停机没发成 / 冷水机没理就隔 StopResend 再发；读冷水机状态，
    /// **空闲**才关阀（延时停机、准备启动、正常运行、故障、读不到都不关——故障时泵可能还在走，确认不了就别关）。
    /// </summary>
    private async Task AdvanceCloseLockedAsync(CancellationToken ct)
    {
        if (!_waterClosing || _links.Chiller is not { } ch) return;
        var now = _ctx.Clock();
        // 头一次照发不误（手里的状态可能是上一拍的：刚发过开机、它还没转起来也读成空闲）；
        // 之后冷水机没理（还在转 / 故障）隔 StopResend 重发，延时停机是它正在按自己的规矩停，不催
        if (!_stopSent
            || now - _stopSentAt >= StopResend && !(_chillerStateKnown && _chillerState is 0 or 3))
        {
            try
            {
                await ch.StopAsync(ct).ConfigureAwait(false);
                if (!_stopSent) _ctx.Log?.Invoke("info", $"{InstanceId} 冷水机停机指令已发");
                _stopSent = true;
                _stopSentAt = now;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _stopSentAt = now;
                if (!_stopFailSaid)
                {
                    _stopFailSaid = true;
                    _ctx.Log?.Invoke("warn", $"{InstanceId} 冷水机停机指令没发出去：{ex.Message}——冷却水阀先不关（冷水机可能还在转），接着重发");
                }
            }
        }
        await ReadChillerAsync(ch, ct).ConfigureAwait(false);
        if (_chillerStateKnown && _chillerState == ChillerClient.Idle)
        {
            _waterClosing = false;
            await RelayWaterAsync(false, ct).ConfigureAwait(false);
            _ctx.Log?.Invoke("info", $"{InstanceId} 冷水机已停稳（空闲）：冷却水阀（DO{_waterDo}）已关" +
                $"（等了 {(_ctx.Clock() - _waterClosingSince).TotalSeconds:0} s）");
            return;
        }
        if (!_closingSlowSaid && now - _waterClosingSince >= ChillerStopWarn)
        {
            _closingSlowSaid = true;
            _ctx.Log?.Invoke("warn", $"{InstanceId} 关水已等了 {ChillerStopWarn.TotalMinutes:0} 分钟，冷水机还是「{ChillerClient.StateName(ChillerState)}」——" +
                "冷却水阀留着开（冷水机没停稳就关阀会憋泵）。查一下冷水机：起停方式是不是「本地+远程」、有没有故障");
        }
    }

    /// <summary>
    /// 读一次冷水机运行状态，记下来；状态变了记一句（故障按 warn）。读不到记成「不知道」（只报第一次），
    /// 隔 ChillerRetryAfter 再试。
    /// </summary>
    private async Task ReadChillerAsync(ChillerClient ch, CancellationToken ct)
    {
        try
        {
            var st = await ch.ReadStateAsync(ct).ConfigureAwait(false);
            _chillerState = st;
            _chillerStateKnown = true;
            _chillerNextTry = DateTimeOffset.MinValue;
            if (st == 2) _waterOpenAt = null;           // 正常运行：开水走完了，面板收菊花
            if (_chillerFails > 0) { _ctx.Log?.Invoke("info", $"{InstanceId} 冷水机恢复（读失败 {_chillerFails} 次后）"); _chillerFails = 0; }
            if (_chillerLogged != st)
            {
                _ctx.Log?.Invoke(st == ChillerClient.Fault ? "warn" : "info",
                    $"{InstanceId} 冷水机：{(_chillerLogged is { } was ? ChillerClient.StateName(was) + " → " : "")}{ChillerClient.StateName(st)}" +
                    (st == ChillerClient.Fault ? "——冷水机报故障，去冷水机面板上看故障信息；故障期间冷却水阀不动" : ""));
                _chillerLogged = st;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _chillerStateKnown = false;
            _chillerNextTry = _ctx.Clock() + ChillerRetryAfter;
            if (_chillerFails++ == 0)
                _ctx.Log?.Invoke("warn", $"{InstanceId} 冷水机读不到：{ex.Message}（连续失败只报第一次，隔 {ChillerRetryAfter.TotalSeconds:0} s 再试）" +
                    (_waterOn ? "——确认不了它停没停，冷却水阀先不关" : ""));
        }
    }

    /// <summary>
    /// 冷水机每拍对账（联动冷水机才有，采集循环调）：读运行状态；关水挂着就往前推（停稳了才关阀）；
    /// 冷水机在转而冷却水阀关着（人在冷水机面板上开了机 / 有人在模块上把阀关了）就先把阀打开——转着关阀会憋泵；
    /// 开机指令没发出去的补发；发了 ChillerStartWarn 还没转起来报一次。读不到冷水机时隔 ChillerRetryAfter 再试。
    /// </summary>
    private async Task TickChillerAsync(CancellationToken ct)
    {
        if (_links.Chiller is not { } ch || _links.Io is null || !_ioOk) return;
        if (_ctx.Clock() < _chillerNextTry) return;
        await _waterLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_waterClosing) await AdvanceCloseLockedAsync(ct).ConfigureAwait(false);
            else await ReadChillerAsync(ch, ct).ConfigureAwait(false);
            if (!_chillerStateKnown) return;
            var st = _chillerState;
            if (st is 1 or 2 or 3 && !_waterOn)
            {
                // 人在冷水机面板上开了机 / 有人在 IO8R 模块上把阀关了：冷水机转着阀关着会憋泵，先开阀
                await RelayWaterAsync(true, ct).ConfigureAwait(false);
                if (!_waterClosing) _waterManual = true;
                _ctx.Log?.Invoke("warn", $"{InstanceId} 冷水机在转（{ChillerClient.StateName(st)}）而冷却水阀（DO{_waterDo}）关着——" +
                    "已先把阀打开（冷水机转着阀关着会憋泵）" + (_waterClosing ? "，停稳了再关"
                        : "。按手动开的算、不自动关；要关在面板上关：先停冷水机，停稳了再关阀"));
                return;
            }
            if (_waterClosing || !_waterOn) return;
            if (_chillerStartPending)
            {
                await TryStartChillerLockedAsync(ct).ConfigureAwait(false);
                return;
            }
            if (ChillerClient.Running(st)) { _chillerStartAt = null; _chillerDownSaid = false; return; }
            if (_chillerStartAt is { } t0)
            {
                if (!_chillerStartSlowSaid && _ctx.Clock() - t0 >= ChillerStartWarn)
                {
                    _chillerStartSlowSaid = true;
                    _ctx.Log?.Invoke("warn", $"{InstanceId} 冷水机开机指令发了 {ChillerStartWarn.TotalSeconds:0} s，冷水机还是「{ChillerClient.StateName(st)}」——" +
                        "冷却水阀开着、冷水机没转。查一下冷水机：「起停方式」是不是「本地+远程」、有没有故障");
                }
                return;
            }
            // 转起来过、现在停了 / 故障了，而有路在制冷：说一次（不替人重新开机——可能是人在冷水机上停的，有他的道理）
            if (!_chillerDownSaid && (NeedsWater(0) || NeedsWater(1)))
            {
                _chillerDownSaid = true;
                _ctx.Log?.Invoke("warn", $"{InstanceId} TEC 在制冷，冷水机却是「{ChillerClient.StateName(st)}」——没有冷却水 TEC 会干烧。" +
                    "去冷水机面板上看是不是被停了 / 报了故障；要接着制冷就把冷水机开起来，不制冷就停掉那一路温控");
            }
        }
        finally
        {
            _waterLock.Release();
        }
    }

    /// <summary>
    /// 开机复位 IO8R 之前问冷水机：还在转（含延时停机）就留着阀——上一炉留着没停 / 人在冷水机上开的，
    /// 八路一起断会把阀在它转着的时候关掉；读不到冷水机就看阀本来开没开，开着就留着（确认不了它停没停）。
    /// 留下来的按手动开的算：自动联动不去关它，要关在面板上关（先停冷水机，停稳了再关阀）。
    /// </summary>
    private async Task<bool> ChillerKeepsWaterAsync(Io8rClient io, CancellationToken ct)
    {
        if (_links.Chiller is not { } ch) return false;
        // 台面重建重开（不是冷开机）：阀的开合按物理现状接手——开着的留着
        // （多半是人手动开的 / 重建前就在开），全断那一下不该把它拍掉
        if (_ctx.Reconnecting)
        {
            var open = await WaterWasOpenAsync(io, ct).ConfigureAwait(false);
            if (open) _ctx.Log?.Invoke("info", $"{InstanceId} 台面重建：冷却水阀（DO{_waterDo}）开着，保持");
            return open;
        }
        if (_links.ChillerOpenError is { } why)
        {
            _ctx.Log?.Invoke("warn", $"{InstanceId} {why}——照常开机，但冷却水阀跟冷水机联动不起来：确认不了冷水机停没停，阀只开不关");
            _chillerStateKnown = false;
            _chillerFails = 1;
            return await WaterWasOpenAsync(io, ct).ConfigureAwait(false);
        }
        await ReadChillerAsync(ch, ct).ConfigureAwait(false);
        if (_chillerStateKnown)
        {
            try
            {
                if (await ch.ReadStartStopModeAsync(ct).ConfigureAwait(false) == 0)
                    _ctx.Log?.Invoke("warn", $"{InstanceId} 冷水机「起停方式」是「本地」：远程开关机它不理——" +
                        "冷水机面板上把「起停方式」改成「本地+远程」，冷却水阀才能跟它联动（开水先开阀再开冷水机，关水先停冷水机、停稳再关阀）");
            }
            catch (OperationCanceledException) { throw; }
            catch { /* 读不出起停方式不影响联动，真不理开关机时会另报 */ }
            if (_chillerState is 1 or 2 or 3)
            {
                _ctx.Log?.Invoke("warn", $"{InstanceId} 开机时冷水机在转（{ChillerClient.StateName(_chillerState)}）：冷却水阀（DO{_waterDo}）留着开，别的继电器照常全断。" +
                    "按手动开的算、不自动关；要关在面板上关冷却水：先停冷水机，停稳了再关阀");
                return true;
            }
            return false;
        }
        if (await WaterWasOpenAsync(io, ct).ConfigureAwait(false))
        {
            _ctx.Log?.Invoke("warn", $"{InstanceId} 开机时冷水机读不到、冷却水阀（DO{_waterDo}）开着：确认不了冷水机停没停，阀留着开");
            return true;
        }
        return false;
    }

    /// <summary>
    /// 断开设备时停冷水机：发停机（拿 _waterLock，跟采集循环错开），每 0.5 s 读一次状态，空闲就返回 true（阀可以关）；
    /// 等满 ChillerStopWaitOnDispose 还没停稳 / 读不到就返回 false（阀留着开），记一句为什么。
    /// </summary>
    private async Task<bool> StopChillerForDisposeAsync()
    {
        var ch = _links.Chiller!;
        var deadline = _ctx.Clock() + ChillerStopWaitOnDispose;
        var wall = Environment.TickCount64 + (long)ChillerStopWaitOnDispose.TotalMilliseconds;
        await _waterLock.WaitAsync().ConfigureAwait(false);
        try
        {
            try { await ch.StopAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { _ctx.Log?.Invoke("warn", $"{InstanceId} 断开设备：冷水机停机指令没发出去（{ex.Message}），接着读它的状态"); }
            while (true)
            {
                try
                {
                    _chillerState = await ch.ReadStateAsync(CancellationToken.None).ConfigureAwait(false);
                    _chillerStateKnown = true;
                }
                catch { _chillerStateKnown = false; }
                if (_chillerStateKnown && _chillerState == ChillerClient.Idle)
                {
                    _waterClosing = false;
                    _ctx.Log?.Invoke("info", $"{InstanceId} 断开设备：冷水机已停稳，冷却水阀（DO{_waterDo}）跟着关");
                    return true;
                }
                // 测试的钟是拨的：真时钟和测试钟哪个先到都算到了
                if (_ctx.Clock() >= deadline || Environment.TickCount64 >= wall) break;
                await Task.Delay(500).ConfigureAwait(false);
            }
            _ctx.Log?.Invoke("warn", $"{InstanceId} 断开设备：等了 {ChillerStopWaitOnDispose.TotalSeconds:0} s 冷水机还是「{ChillerClient.StateName(ChillerState)}」——" +
                $"冷却水阀（DO{_waterDo}）留着开（冷水机转着关阀会憋泵），别的继电器全断。冷水机停了以后到 IO8R 模块上关 DO{_waterDo}，或重新连接设备再关");
            return false;
        }
        finally
        {
            _waterLock.Release();
        }
    }

    private async Task<bool> WaterWasOpenAsync(Io8rClient io, CancellationToken ct)
    {
        try { return (await io.ReadRelaysAsync(ct).ConfigureAwait(false))[_waterDo]; }
        catch (OperationCanceledException) { throw; }
        catch { return false; }        // IO8R 都读不了：下面全断那一帧多半也发不出去，照常走
    }

    /// <summary>只扳阀那只继电器、记影子（拿着 _waterLock）。</summary>
    private async Task RelayWaterAsync(bool open, CancellationToken ct)
    {
        await _links.Io!.SetRelayAsync(_waterDo, open, ct).ConfigureAwait(false);
        var was = _waterOn;
        _waterOn = open;
        if (was != open)
            _ctx.Log?.Invoke("info", $"{InstanceId} 冷却水阀（DO{_waterDo}）已{(open ? "打开" : "关闭")}");
    }

    /// <summary>
    /// 面板手动开关走这里：手动开的立旗（自动联动不关它），手动关的落旗。
    /// 联动冷水机时关 = 先停冷水机、停稳了再关阀，返回时阀可能还开着（面板上那句状态说在等什么）。
    /// </summary>
    internal async Task SetWaterManualAsync(bool open, CancellationToken ct)
    {
        // 任一路制冷时 DO4 保持打开（用户定的）：手动关了下一拍也会被补开，不如当场说清楚
        if (!open && (NeedsWater(0) || NeedsWater(1)))
        {
            var w = NeedsWater(0) ? 0 : 1;
            throw new InvalidOperationException(
                $"工位 {AB(w)} 的 TEC {(TecDriving(w) ? "在制冷" : "正要接上（在等通水）")}{(_regime[w] == Regime.Both ? "（双向挡）" : "")}——" +
                "断水 TEC 会干烧，冷却水关不了。先停那一路的温控");
        }
        // 旗子在序列起跑时就立（不是跑完才立）：序列里冷水机开机要等它答话，链路慢要数秒——
        // 这期间采集拍会看到「阀开了、还没人认领」，从前的下场是阀被自动联动关掉（现场踩到）
        var was = _waterManual;
        _waterManual = open;
        try { await SetWaterAsync(open, ct).ConfigureAwait(false); }
        catch { _waterManual = was; throw; }
    }

    /// <summary>
    /// 这一路此刻要不要冷却水：TEC 带电（功率线经继电器的机器看继电器；硬线直连断不了电，
    /// 回路在 TEC 侧形态就要水）。**升温不要水**（继电器机在加热棒侧）：冷水机停、阀关——
    /// 用户定的（0522）：升温时冷水机与阀都关，要降温了（往 TEC 侧接）才开阀、开冷水机，
    /// 「先通水再接 TEC」在切换序列里（EnsureWaterAsync）——序列从开阀到合上继电器这一段也算要水（_waterClaim）。
    /// 人在 HMI 降温弹窗里选了「不开，直接制冷」的那一路不算要水（_waterDeclined）：对账不替它补开、不为它留着。
    /// </summary>
    private bool NeedsWater(int well)
        => !_waterDeclined[well]
           && (Volatile.Read(ref _waterClaim[well]) > 0
               || TecDriving(well) && (_tecRelay ? _tecOn[well] : _wantEnabled[well]));

    /// <summary>
    /// 制冷先通水（用户定的）：阀没开就开，等通水延时再回去接 TEC。阀已经开着不等。
    /// IO8R 不通时 TEC 那只继电器也扳不了，不在这里拦——下面写继电器会照常报错。
    /// </summary>
    private async Task EnsureWaterAsync(int well, CancellationToken ct)
    {
        if (_links.Io is null || !_ioOk) return;
        if (_waterDeclined[well])
        {
            // 人在 HMI 降温弹窗里选了「不开，直接制冷」（用户定的）：不开阀、不开冷水机、不等通水，直接接 TEC
            if (!_declinedSaid[well] && !(_waterOn && !_waterClosing))
            {
                _declinedSaid[well] = true;
                var text = $"工位 {AB(well)} 要接 TEC：人选了不开冷却水，直接接（冷却水阀 DO{_waterDo} 与冷水机不动）——没有冷却水 TEC 会干烧";
                _ctx.Log?.Invoke("warn", $"{InstanceId} {text}");
                _rd.TempOf(well).Note(text);
            }
            return;
        }
        // 阀开着、也没在关水：不等。关水挂着（冷水机停机已发、阀还开着）就撤销它，冷水机重新开起来——
        // 那时冷水机多半已经在停，水不一定在流，照样等通水延时
        if (_waterOn && !_waterClosing) return;
        await SetWaterAsync(true, ct).ConfigureAwait(false);
        _ctx.Log?.Invoke("info", $"{InstanceId} 工位 {AB(well)} 要接 TEC：冷却水阀（DO{_waterDo}）先打开" +
            (_links.Chiller is null ? "" : "、再开冷水机") + $"，等 {_waterLead.TotalSeconds:0} s 让水流起来再接");
        _rd.TempOf(well).Note($"冷却水阀 DO{_waterDo} 打开（接 TEC 前通水 {_waterLead.TotalSeconds:0} s）");
        if (_waterLead <= TimeSpan.Zero) return;
        Interlocked.Increment(ref _waterLeading);
        try { await Task.Delay(_waterLead, ct).ConfigureAwait(false); }
        finally { Interlocked.Decrement(ref _waterLeading); }
    }

    /// <summary>
    /// TEC 断开之后：两路都不要水了才关（另一路 TEC 还在制冷 / 还在双向挡，断水会干烧）。
    /// 人手动开的不关。关阀失败只记一笔——阀多开着不危险，不该为它把切换序列掐断。
    /// </summary>
    private async Task ReleaseWaterAsync(CancellationToken ct)
    {
        if (!_waterOn || _waterClosing || _waterManual || NeedsWater(0) || NeedsWater(1)) return;
        // 人选了「不开，直接制冷」的那一路正接着 TEC：「不开」是不碰水，不是关水——开着的阀别替它关了
        if (_waterDeclined[0] && TecDriving(0) || _waterDeclined[1] && TecDriving(1)) return;
        try
        {
            await SetWaterAsync(false, ct).ConfigureAwait(false);
            _ctx.Log?.Invoke("info", $"{InstanceId} 两路都不制冷了：" + (_waterOn
                ? "冷水机停机已发，停稳了再关冷却水阀"
                : $"冷却水阀（DO{_waterDo}）已关"));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _ctx.Log?.Invoke("warn", $"{InstanceId} 关冷却水阀（DO{_waterDo}）失败：{ex.Message}——阀还开着，下一拍再试");
        }
    }

    /// <summary>
    /// 采集循环每拍对一次账：有路要水阀却关着（被人在模块上扳了 / 开阀失败）就补开——
    /// 这时 TEC 已经接着，不再等通水延时；谁都不要水了、阀是自动开的就关。
    /// 正在切换（_switchLock 被占着）或正在开关水（_waterLock 被占着）不动：
    /// 序列自己会开关阀——序列没跑完就插进来，会把「阀开了、标志还没立」当成没人认领
    /// （手动开水被自动关掉，现场踩到）。
    /// </summary>
    private async Task SyncWaterAsync(CancellationToken ct)
    {
        if (_links.Io is null || !_ioOk || _switchLock.CurrentCount == 0 || _waterLock.CurrentCount == 0) return;
        var need = NeedsWater(0) || NeedsWater(1);
        try
        {
            if (need && (!_waterOn || _waterClosing))
            {
                var closing = _waterClosing;
                await SetWaterAsync(true, ct).ConfigureAwait(false);
                if (!closing)
                    _ctx.Log?.Invoke("warn", $"{InstanceId} TEC 在制冷而冷却水阀（DO{_waterDo}）关着——已补开");
            }
            else if (!need && _waterOn && !_waterManual)
            {
                await ReleaseWaterAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _ctx.Log?.Invoke("warn", $"{InstanceId} 冷却水阀（DO{_waterDo}）对账失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 热源状态量（需求 §2.5）：0 = TEC，1 = 电加热（未核实），2 = 电加热（反馈已核实）。
    /// 没接反馈回路的只能报「已发命令，未核实」，不假装核实过；接了反馈的，
    /// 继电器与接触器辅助触点对不上就照实降级并报出来——两种不一致各有各的后果：
    /// 继电器吸着、触点没跟上 = 电加热其实没在加热；继电器断了、触点还吸着 = 接触器粘连，
    /// 电加热棒可能还带电。切换序列进行中（锁被占着）那 2 秒不算不一致，那正是在等它跟上。
    /// </summary>
    private double HeatStateOf(int w, bool relayClosed, bool[]? dis)
    {
        if (dis is null) return relayClosed ? 1 : 0;
        var contactor = dis[_diIdx[w]];
        if (contactor == relayClosed)
        {
            if (_fbMismatch[w])
            {
                _fbMismatch[w] = false;
                _ctx.Log?.Invoke("info", $"{InstanceId} 工位 {AB(w)} 反馈 DI{_diIdx[w]} 已与继电器对上");
            }
            return relayClosed ? 2 : 0;
        }
        if (_switchLock.CurrentCount == 0) return relayClosed ? 1 : 0;   // 正在切换，等它跟上
        if (!_fbMismatch[w])
        {
            _fbMismatch[w] = true;
            if (relayClosed)
                _ctx.Log?.Invoke("warn", $"{InstanceId} 工位 {AB(w)} 切换继电器在电加热侧，" +
                    $"但反馈 DI{_diIdx[w]} 已掉——接触器可能已释放，电加热实际没在加热");
            else
                _ctx.Log?.Invoke("error", $"{InstanceId} 工位 {AB(w)} 切换继电器已断开，" +
                    $"但反馈 DI{_diIdx[w]} 仍闭合——接触器可能粘连，电加热棒可能还带电，请到现场检查");
        }
        // 两种不一致都按「电加热·未核实」记：前者是命令说在电加热，后者是接触器说在电加热
        return 1;
    }

    private void Push(int channel, string tag, double value, DateTimeOffset at, Quality quality)
    {
        if (double.IsNaN(value)) return;
        _out.Push(new Sample(channel, tag, at.UtcTicks, at, value, quality));
    }

    private static string AB(int well) => well == 0 ? "A" : "B";

    public async ValueTask DisposeAsync()
    {
        _pollCts?.Cancel();
        if (_pollTask is { } t) { try { await t.ConfigureAwait(false); } catch { } }

        // 走人前把两路继电器落回 TEC 侧。断线后的兜底是 IO8R 的总线错误复位，
        // 但能自己收拾就不劳驾兜底
        if (_links.Io is { } io && _ioOk)
        {
            // 联动冷水机：阀最后关——先停冷水机、等它停稳（最多 ChillerStopWaitOnDispose）；
            // 等不到就别的全断、阀留着开（冷水机转着关阀会憋泵），说清楚
            bool keepWater;
            if (Reconnecting)
            {
                // 台面重建重开（不是真断开）：冷水机别停、阀别动——物理状态原样交给
                // 新会话（它开机复位时按物理现状接手）。重建一次就把人开着的阀拍掉、
                // 把在转的冷水机停了，现场踩到
                keepWater = _waterOn || _waterClosing;
                if (keepWater)
                    _ctx.Log?.Invoke("info", $"{InstanceId} 台面重建：冷水机与冷却水阀保持原样（新会话接手）");
            }
            else if (_links.Chiller is not null && (_waterOn || _waterClosing))
                keepWater = !await StopChillerForDisposeAsync().ConfigureAwait(false);
            else keepWater = false;
            try
            {
                if (keepWater)
                {
                    var relays = new bool[Io8rClient.Points];
                    relays[_waterDo] = true;
                    await io.WriteRelaysAsync(relays, CancellationToken.None).ConfigureAwait(false);
                }
                else await io.AllOffAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch { }
        }

        _fwd.Dispose();
        await _rd.DisposeAsync().ConfigureAwait(false);     // 里面会关掉 RD105 的链路
        _links.IoPort?.Dispose();
        _out.Complete();
        State = DeviceState.Disposed;
    }

    private sealed class Fwd(Broadcast<Sample> target) : IObserver<Sample>
    {
        public void OnNext(Sample value) => target.Push(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
