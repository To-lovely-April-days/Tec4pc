namespace TecControl.Core.Control;

// 【本地改动】（2026-11-14，外环改「按实测实时算」第 0 步）整个文件是本地新增的，源工程（src/TecControl.Core）里没有。
//
// 这是什么：釜内串级外环的新算法（釜内 Tr → 夹套目标），纯计算——不读时钟、不做 I/O、不碰回路状态，dt 由调用方给，一路一个实例。
//   夹套目标 = 目标 + b̂ + G(目标 − T̂)
//   b̂（维持温差）、T̂（滤过的釜内）由二阶观测器按读数实时算；τ̂（釜的时间常数）两路并着认；
//   没有按温度查的表、没有到站 / 过零那些附加规则——一条公式从头走到尾（用户 2026-11-12：不要打补丁式的规则，按实测实时算）。
//
// 为什么单独成类：第 0 步「只算不控」——回路每拍算一遍、只进逐拍记录，控温还是原来的程序。
//   做成不碰回路状态的纯计算类，才谈得上「挂上去不改变任何控制行为」，也才能离线逐拍对拍。
//
// 数从哪来：逐行照 Python 定稿移植（outer_m.py 的类 M，缺省参数 PARAMS 走到的那一条路；
//   拷贝在 tests/Tec.Core.Tests/LiveOuter/reference/，md5 46e5ed1f…）。试算成绩是那份 Python 跑出来的，
//   所以这里的运算次序、先后、边界（≤ 还是 <）一律照它，不许「顺手化简」：
//   tests/Tec.Core.Tests/LiveOuter/ 下的对拍测试拿 6 场真机记录 + 12 场合成场逐拍比（差 ≤ 1e-9 / 1e-6）。
//   Python 里别的开关（tau_mode 的其它取法、硬夹、lead、k_hi_rel、p_up 给定值……）定稿没走，没搬。
//
// 跟 Python 版有意不一样的只有这几处（都不影响「同样的合法输入出同样的数」）：
//   · 平方写 x * x：Python 的 x ** 2 走数学库 pow，偶尔差 1 ulp（实测最大 2.3e-13）；
//   · 取大取小用 PyMax / PyMin（Python 内置 max / min 的写法：第二个更大 / 更小才换），不用 Math.Max / Math.Min（遇 NaN 不一样）；
//   · 输入不合法（dt ≤ 0、夹套 / 目标不是有限数）时 Python 是抛除零或者让 NaN 进状态再也出不来；这里在动任何状态之前抛 ArgumentException；
//   · 釜内读数是 ±∞ 也当「没读数」（Python 只认 None / NaN）；
//   · 60 s 窗有硬上限（4096 笔），比 15 ms 一拍还密地调才碰得到；
//   · 多出 Reseat（断档后重摆，见该方法）、PinTarget（不在控时目标只是占位的，见该方法）、JacketSetpointC（夹过的夹套目标）、
//     ExportState / ImportState（测试和以后落盘用）。Reseat、PinTarget 都不改 Step 里的任何一行：
//     只是在 Step 之前把某几个状态摆一下，Python 那边等价的写法各在方法注释里，对拍也是照那个等价写法生成的。

/// <summary>
/// 【本地改动】<see cref="LiveOuter"/> 的参数：出厂三个数 + 设计常数。缺省值 = Python 定稿的 PARAMS。
/// 出厂三个数每台机器、每个工位各一组（第 0 步两个工位先共用这台机器的 950 / 170 / 0.017，按工位存是第 2 步的事）；
/// 设计常数所有机器一样，不进界面。每个数的来路见改动方案 B.1。
/// </summary>
public sealed record LiveOuterParams
{
    // ── 出厂三个数 ──

    /// <summary>① τ̂₀：釜的时间常数的起点（s）。这台机器 950：拿 11-09 那场 19 → 50 ℃ 的记录、用夹套读数把釜内积出来对 (τ, Tw) 最小二乘得 949 / 173。
    /// 只是起点——τ̂ 之后按实测认，范围 τ̂₀ × [<see cref="TauLo"/>, <see cref="TauHi"/>]。</summary>
    public double Tau0 { get; init; } = 950.0;

    /// <summary>② T̂w：夹套 → 釜壁的滞后（s，纯滞后并在里面）。这台机器 170（同一次拟合）。釜壁估计 Ŵ = 夹套读数过这个一阶滞后。</summary>
    public double Tw { get; init; } = 170.0;

    /// <summary>③ ρ：夹套在常用高温、零出力时自己每秒凉多少（K/s）。这台机器 0.017（74 ℃ 实测 −1.04 K/min）。
    /// 只用来定升温方向比例那一份的封顶 P上限 = ρ·τ̂ ÷ K；一个数顾不了全温区，宁小勿大。0 = 升温方向不给比例。</summary>
    public double Rho { get; init; } = 0.017;

    // ── 设计常数：增益 ──

    /// <summary>K 余量：K 取「刚好不冲过」那个值的几成。0.75（改动方案 A.3：0.9 快约 10 min，但出厂数失配时冲过的台次多十几倍）。</summary>
    public double KMargin { get; init; } = 0.75;

    // ── 设计常数：观测器（T̂、b̂）──

    /// <summary>观测器时间常数 To（s）。150：瞬时 b̂ 会被读数锯齿晃出 1.1 K，要慢到这个量级。</summary>
    public double To { get; init; } = 150.0;

    /// <summary>变速参考（K）：读数离模型这么远（20 s 平均）时观测器快一倍。2 K。</summary>
    public double Eps0 { get; init; } = 2.0;

    /// <summary>观测器最快到多少秒。20 s。</summary>
    public double ToMin { get; init; } = 20.0;

    /// <summary>变速看的是「读数 − 模型」这么多秒的平均（s），不是当拍值——一个坏点带不动它。20 s。</summary>
    public double EpsLpS { get; init; } = 20.0;

    // ── 设计常数：τ̂ 的范围和门控 ──

    /// <summary>τ̂ 最小是 τ̂₀ 的几倍。0.4（降温实测 0.66–0.75 倍；换物料试到 0.35）。</summary>
    public double TauLo { get; init; } = 0.4;

    /// <summary>τ̂ 最大是 τ̂₀ 的几倍。3（换物料试到 2 倍）。</summary>
    public double TauHi { get; init; } = 3.0;

    /// <summary>过渡参考 e0（K）：认 τ 只在「目标变动引起的过渡」里认，权重 d² ÷ (d² + e0²)，d = 目标 − 目标过一阶滞后。0.2 K。</summary>
    public double E0 { get; init; } = 0.2;

    // ── 设计常数：τ̂ 第二路（T₂、b₂、θ = 1/τ₂ 三个量的卡尔曼）──

    /// <summary>读数噪声方差 R（K²）。0.05。</summary>
    public double R { get; init; } = 5e-2;

    /// <summary>b₂ 每秒漂多少的方差 q_b（K²/s）。1e-4。</summary>
    public double Qb { get; init; } = 1e-4;

    /// <summary>起算时对 b₂ 的把握（方差，K²）。4。</summary>
    public double P0b { get; init; } = 4.0;

    /// <summary>对 τ 的先验：±30 %（θ 的标准差 = 0.3·θ）。</summary>
    public double P0ThRel { get; init; } = 0.3;

    /// <summary>σΔ（s）：釜壁估计动得快的那一拍少信模型（等效「釜壁滞后可能错这么多秒」）。120。</summary>
    public double SigDelta { get; init; } = 120.0;

    /// <summary>t_corr（s）：上面那份模型误差的相关时间。60。</summary>
    public double TCorr { get; init; } = 60.0;

    /// <summary>放开参考 e1（K）：终点一拍里变了 Δ，对 τ 的把握按 Δ² ÷ (Δ² + e1²) 放回先验（新的一段升降温 = 新的一次认 τ 的机会）。5 K。</summary>
    public double ReopenE1 { get; init; } = 5.0;

    // ── 设计常数：τ̂ 第一路（回归）──

    /// <summary>回归的预滤波（s）：Tr、Ŵ 各过两级这个常数的低通，釜内速度取两级之差，不做差分。100。</summary>
    public double Tf { get; init; } = 100.0;

    /// <summary>回归的记忆（s，指数遗忘）。600。</summary>
    public double Tm { get; init; } = 600.0;

    /// <summary>τ̂ 往回归值靠拢的时间常数（s）。120。往小改跟往大改一样快。</summary>
    public double Ta { get; init; } = 120.0;

    /// <summary>釜壁快动少信：权重 1 ÷ (1 + (<see cref="SigD"/>·ẇ ÷ 这个数)²)。0.3（合起来系数 333 s/K）。</summary>
    public double RegR { get; init; } = 0.3;

    /// <summary>釜内速度的噪声底（K/s）。3e-4：速度的方差比它小就不信回归。</summary>
    public double Z0 { get; init; } = 3e-4;

    /// <summary>见 <see cref="RegR"/>。100。</summary>
    public double SigD { get; init; } = 100.0;

    /// <summary>维持温差随温度涨的斜率（K/K）：回归里从「釜壁 − 釜内」扣掉这一份。0.1。</summary>
    public double Beta { get; init; } = 0.1;

    // ── 设计常数：放热报警（只报，不动夹套目标）──

    /// <summary>制冷连续顶满多少秒才算（s）。60（用户原话）。</summary>
    public double AlarmFullS { get; init; } = 60.0;

    /// <summary>「还在升」：T̂ 比 <see cref="AlarmWindowS"/> 前高出多少算在升（K）。0.02。</summary>
    public double AlarmRiseK { get; init; } = 0.02;

    /// <summary>「还在升」回看多久（s）。60——Python 里是写死的 60.0（不是 alarm_full_s），所以单列一个数。</summary>
    public double AlarmWindowS { get; init; } = 60.0;

    // ── 设计常数：斜坡 ──

    /// <summary>斜坡前馈的系数：走坡时夹套目标多给 这个数·(τ̂ + T̂w)·坡速。1。第 0 步回路还不给坡速（终点 = 目标、坡速 = 0），这一项恒为 0。</summary>
    public double RampFf { get; init; } = 1.0;

    // ── 夹套目标的范围（Python 里是试算台在外面夹的）──

    /// <summary>夹套目标下限（℃）。−50 = HostControlLoop.InnerSetpointMinC 的缺省值。只用于 <see cref="LiveOuterOutput.JacketSetpointC"/>。</summary>
    public double JacketMinC { get; init; } = -50.0;

    /// <summary>夹套目标上限（℃）。200 = 用户定的夹套上限（玻璃釜 + 导热油），也是 HostControlLoop.InnerSetpointMaxC 的缺省值。</summary>
    public double JacketMaxC { get; init; } = 200.0;

    /// <summary>参数不合理（会除零 / 出 NaN 的那些）就抛 <see cref="ArgumentOutOfRangeException"/>。构造 <see cref="LiveOuter"/> 时调一次。</summary>
    public void Validate()
    {
        Positive(Tau0, nameof(Tau0)); Positive(Tw, nameof(Tw)); Positive(KMargin, nameof(KMargin));
        Positive(To, nameof(To)); Positive(Eps0, nameof(Eps0)); Positive(ToMin, nameof(ToMin)); Positive(EpsLpS, nameof(EpsLpS));
        Positive(TauLo, nameof(TauLo)); Positive(TauHi, nameof(TauHi)); Positive(E0, nameof(E0));
        Positive(R, nameof(R)); Positive(P0b, nameof(P0b)); Positive(P0ThRel, nameof(P0ThRel)); Positive(TCorr, nameof(TCorr));
        Positive(ReopenE1, nameof(ReopenE1));
        Positive(Tf, nameof(Tf)); Positive(Tm, nameof(Tm)); Positive(Ta, nameof(Ta)); Positive(RegR, nameof(RegR));
        NotNegative(Rho, nameof(Rho)); NotNegative(Qb, nameof(Qb)); NotNegative(SigDelta, nameof(SigDelta)); NotNegative(Z0, nameof(Z0));
        NotNegative(SigD, nameof(SigD)); NotNegative(AlarmFullS, nameof(AlarmFullS)); NotNegative(AlarmWindowS, nameof(AlarmWindowS));
        Finite(Beta, nameof(Beta)); Finite(AlarmRiseK, nameof(AlarmRiseK)); Finite(RampFf, nameof(RampFf));
        Finite(JacketMinC, nameof(JacketMinC)); Finite(JacketMaxC, nameof(JacketMaxC));
        if (!(TauLo < TauHi)) throw new ArgumentOutOfRangeException(nameof(TauLo), TauLo, "τ̂ 的下限倍数要小于上限倍数");
        if (!(JacketMinC < JacketMaxC)) throw new ArgumentOutOfRangeException(nameof(JacketMinC), JacketMinC, "夹套目标下限要小于上限");
    }

    private static void Positive(double v, string name)
    {
        if (!(v > 0.0) || double.IsInfinity(v)) throw new ArgumentOutOfRangeException(name, v, "要是正的有限数");
    }

    private static void NotNegative(double v, string name)
    {
        if (!(v >= 0.0) || double.IsInfinity(v)) throw new ArgumentOutOfRangeException(name, v, "要是不小于 0 的有限数");
    }

    private static void Finite(double v, string name)
    {
        if (!double.IsFinite(v)) throw new ArgumentOutOfRangeException(name, v, "要是有限数");
    }
}

/// <summary>
/// 【本地改动】<see cref="LiveOuter"/> 一拍的输入（= Python 试算台 Obs 里 M 真用到的那几样）。
/// 注意 <c>default</c> 的 <see cref="Tr"/> 是 0 不是「没读数」——没读数要显式给 NaN。
/// </summary>
public readonly record struct LiveOuterInput
{
    /// <summary>这一拍的时刻（s），单调往前走就行（离哪个起点都行）。只给放热报警的 60 s 窗记时刻用，别的不看它。</summary>
    public double T { get; init; }

    /// <summary>离上一拍多少秒，必须 &gt; 0。第一拍（刚 Reset / Reseat）也要给：过渡参考和顶满计时那一拍就按它走（试算台给 2）。
    /// 类里不设上限——观测器是显式一步，拍太长会走歪（试算台、真机对拍最长 12.8 s）；断档怎么拆小步由调用方管。</summary>
    public double Dt { get; init; }

    /// <summary>此刻的釜内目标（℃）：走坡时是坡上的当前值；没在控时喂当时的釜内读数（这样「目标 − 釜内」= 0，不出比例），
    /// 并且在 <see cref="LiveOuter.Step"/> 之前先调 <see cref="LiveOuter.PinTarget"/>——这种目标只是占位的，它跟着读数变不算「目标变了」。</summary>
    public double Sp { get; init; }

    /// <summary>釜内读数（℃）；这一拍没有就给 NaN（±∞ 也当没有）。</summary>
    public double Tr { get; init; }

    /// <summary>夹套读数（℃），必须有效——无效的拍不要调 <see cref="LiveOuter.Step"/>。</summary>
    public double Tj { get; init; }

    /// <summary>热源继电器在 TEC 侧（Python 的 side == 'C'）。放热报警四个事实之一。</summary>
    public bool TecSide { get; init; }

    /// <summary>内环贴在制冷下限（TEC 顶满）。</summary>
    public bool InnerSatLow { get; init; }

    /// <summary>制冷段挂起：要 TEC 但在等人答开不开冷却水 / 等冷水机最短间隔。成立时不算顶满。</summary>
    public bool TecWithheld { get; init; }

    /// <summary>输出开着（扳继电器那一下是关的）。</summary>
    public bool OutputOn { get; init; }

    /// <summary>这一段的终点（℃）；null = 没给 → 用 <see cref="Sp"/>。第 0 步回路不给。</summary>
    public double? SpFinal { get; init; }

    /// <summary>此刻的坡速（K/s）；null 或 0 = 没在走坡。目标到了终点以后不要再给坡速（参考实现照字面会取带前馈的那一行）。第 0 步回路不给。</summary>
    public double? SpRate { get; init; }
}

/// <summary>【本地改动】<see cref="LiveOuter.Step"/> 一拍的输出。</summary>
/// <param name="U">公式算出来的夹套目标（℃），没夹过——对拍比的是它（Python 的 step 返回的就是没夹过的）。</param>
/// <param name="JacketSetpointC"><paramref name="U"/> 夹到 [JacketMinC, JacketMaxC]（缺省 −50…200 ℃）：记录里的 lo_isp、以后给内环的都是它。</param>
/// <param name="AlarmFired">放热报警这一拍报出来（条件从不成立变成立的那一拍；成立着不重报）。</param>
/// <param name="AlarmOn">放热报警的三个条件这一拍同时成立（记录里的 lo_alarm）。</param>
public readonly record struct LiveOuterOutput(double U, double JacketSetpointC, bool AlarmFired, bool AlarmOn);

/// <summary>
/// 【本地改动】<see cref="LiveOuter"/> 全部标量状态的快照（60 s 窗除外）。
/// 给测试（把内部状态摆到逐拍对拍走不到的分支上、核「重摆留着把握」）和以后把 τ̂ 落盘用；控温路径上不用它。
/// </summary>
public readonly record struct LiveOuterState
{
    public bool Started { get; init; }
    public double Tau { get; init; }
    public double K { get; init; }
    public double EpsLp { get; init; }
    public double ToEff { get; init; }
    public double GSp { get; init; }
    public double FullS { get; init; }
    public bool AlarmOn { get; init; }
    public bool HasSpfPrev { get; init; }
    public double SpfPrev { get; init; }
    public double U { get; init; }
    public double Wall { get; init; }
    public double WallPrev { get; init; }
    public double WallRate { get; init; }
    public double Xr { get; init; }
    public double B { get; init; }
    public double Ref { get; init; }
    public int MedN { get; init; }
    public double M0 { get; init; }
    public double M1 { get; init; }
    public double M2 { get; init; }
    public double ET { get; init; }
    public double EB { get; init; }
    public double Th { get; init; }
    public double TauE { get; init; }
    public double P00 { get; init; }
    public double P01 { get; init; }
    public double P02 { get; init; }
    public double P11 { get; init; }
    public double P12 { get; init; }
    public double P22 { get; init; }
    public double F1 { get; init; }
    public double F2 { get; init; }
    public double G1 { get; init; }
    public double G2 { get; init; }
    public double Tr0 { get; init; }
    public double S0 { get; init; }
    public double S1 { get; init; }
    public double S2 { get; init; }
    public double S3 { get; init; }
    public double S4 { get; init; }
    public double S5 { get; init; }
}

/// <summary>
/// 【本地改动】串级外环「按实测实时算」（见文件头）。用法：
/// <code>
/// var lo = new LiveOuter();                       // 一路一个；τ̂ 跟着对象走
/// lo.Reset(i); lo.Step(i, first: true);           // 第一次拿到「釜内、夹套都有效」的读数那一拍（同一拍的输入）
/// lo.Step(i);                                     // 之后每个外环拍（≥ 2 s）一次；釜内没读数给 NaN
/// lo.Reseat(i); lo.Step(i, first: true);          // 断档很久以后：重摆 Ŵ / T̂ / b̂，τ̂ 和它的把握留着
/// lo.PinTarget(i.Sp); lo.Step(i);                 // 不在控的拍（i.Sp 喂的是当时的釜内读数）：Step 之前先 PinTarget；
///                                                 // 起算 / 重摆那一拍不在控也一样：Reset 或 Reseat → PinTarget → Step(first)
/// </code>
/// 不是线程安全的：一个实例只在一条线程上用。
/// </summary>
public sealed class LiveOuter
{
    /// <summary>60 s 窗的硬上限（笔）：2 s 一拍是 31–32 笔，0.5 s 一拍 121 笔；到上限就丢最老的。只为「缓冲有上限」，正常节拍碰不到。</summary>
    private const int HistCap = 4096;

    private readonly LiveOuterParams _p;

    // ── 构造时就有、Reset 不动的 ──
    private double _tau;                 // τ̂（第一路；进控制律）
    private double _k;                   // K；只在 τ̂ 真变了的时候重算
    private double _epsLp;               // ε（读数 − 模型）的 20 s 平均
    private double _toEff;               // To′（只是记下来给人看）
    private double _gSp;                 // 门控：这一拍算出来，下一拍的两路认 τ 用
    private double _fullS;               // 顶满计时（s）
    private bool _alarmOn;
    private bool _hasSpfPrev;            // 上一拍的终点有没有（只有全新对象的第一拍没有）
    private double _spfPrev;
    private double _u;
    private bool _started;

    // ── Reset 才摆的 ──
    private double _wall, _wallPrev, _wallRate;       // 釜壁估计 Ŵ 和它每秒变多少
    private double _xr, _b;                           // T̂、b̂
    private double _ref;                              // 目标过一阶滞后（过渡参考）
    private double _m0, _m1, _m2;                     // 3 点中值的缓冲（_m0 最老）
    private int _medN;
    private readonly Queue<(double T, double Xr)> _hist = new(64);   // 60 s 窗：(时刻, T̂)
    private double _eT, _eB, _th, _tauE;              // 第二路：T₂、b₂、θ = 1/τ₂、τ₂
    private double _p00, _p01, _p02, _p11, _p12, _p22;  // 第二路的把握 P（对称 3×3，只存上三角）
    private double _f1, _f2, _g1, _g2, _tr0;          // 第一路：Tr、Ŵ 各两级低通；起算时的 Tr
    private double _s0, _s1, _s2, _s3, _s4, _s5;      // 第一路：带遗忘的加权累加量（Σw、Σwz、Σwy、Σwzz、Σwzy、Σwyy）

    public LiveOuter(LiveOuterParams? parameters = null)
    {
        _p = parameters ?? new LiveOuterParams();
        _p.Validate();
        _tau = _p.Tau0;
        _k = Gain();
        _toEff = _p.To;
        _tauE = _p.Tau0;
    }

    public LiveOuterParams Params => _p;

    /// <summary>起算过没有（Reset / Reseat 过）。</summary>
    public bool Started => _started;

    /// <summary>釜壁估计 Ŵ（℃）= 夹套读数过一阶滞后 T̂w。记录里的 lo_wall。</summary>
    public double Wall => _wall;

    /// <summary>滤过的釜内 T̂（℃）。记录里的 lo_xr。</summary>
    public double THat => _xr;

    /// <summary>实时算的维持温差 b̂（K）：稳住时 = 夹套 − 釜内。记录里的 lo_b。</summary>
    public double BHat => _b;

    /// <summary>τ̂（s）：进控制律的那个。记录里的 lo_tau。</summary>
    public double Tau => _tau;

    /// <summary>第二路认的 τ₂（s）：只管「τ̂ 往小改不许小过它」，不进控制律。记录里的 lo_tau2。</summary>
    public double TauEkf => _tauE;

    /// <summary>K = max(1, 余量·(τ̂ + T̂w)² ÷ (4·τ̂·T̂w))。</summary>
    public double K => _k;

    /// <summary>升温方向比例那一份的封顶 P上限 = ρ·τ̂ ÷ K（K）。</summary>
    public double PUp => _p.Rho * _tau / _k;

    /// <summary>「目标变动引起的过渡还剩多少」的权重（0…1）：认 τ 只在它大的时候认。</summary>
    public double GSp => _gSp;

    /// <summary>观测器这一拍实际用的时间常数 To′（s）。</summary>
    public double ToEff => _toEff;

    /// <summary>上一拍的夹套目标（没夹过）。</summary>
    public double U => _u;

    /// <summary>制冷已经连续顶满多少秒。</summary>
    public double FullSeconds => _fullS;

    /// <summary>放热报警的条件上一拍成立着。</summary>
    public bool AlarmOn => _alarmOn;

    /// <summary>内部量全是有限数（没有 NaN / 无穷）。挂接层每拍看一眼：不是就把这一路影子停掉。</summary>
    public bool IsFinite =>
        double.IsFinite(_tau) && double.IsFinite(_k) && double.IsFinite(_epsLp) && double.IsFinite(_toEff) && double.IsFinite(_gSp)
        && double.IsFinite(_fullS) && double.IsFinite(_u)
        && double.IsFinite(_wall) && double.IsFinite(_wallRate) && double.IsFinite(_xr) && double.IsFinite(_b) && double.IsFinite(_ref)
        && double.IsFinite(_eT) && double.IsFinite(_eB) && double.IsFinite(_th) && double.IsFinite(_tauE)
        && double.IsFinite(_p00) && double.IsFinite(_p01) && double.IsFinite(_p02)
        && double.IsFinite(_p11) && double.IsFinite(_p12) && double.IsFinite(_p22)
        && double.IsFinite(_f1) && double.IsFinite(_f2) && double.IsFinite(_g1) && double.IsFinite(_g2)
        && double.IsFinite(_s0) && double.IsFinite(_s1) && double.IsFinite(_s2)
        && double.IsFinite(_s3) && double.IsFinite(_s4) && double.IsFinite(_s5);

    // Python 内置 max(a, b) / min(a, b)：第二个「更大 / 更小」才换，否则留第一个（a 是 NaN 留 NaN，b 是 NaN 留 a）。
    // Math.Max / Math.Min 只要有一个 NaN 就给 NaN，不一样；正常数据上没区别，照 Python 写是为了逐拍对得上。
    private static double PyMax(double a, double b) => b > a ? b : a;

    private static double PyMin(double a, double b) => b < a ? b : a;

    private static bool HasReading(double tr) => double.IsFinite(tr);

    /// <summary>K = max(1, 余量·(τ̂ + T̂w)² ÷ (4·τ̂·T̂w))。τ̂ &lt; 约 510 s 时公式给出的数小于 1，取 1 = 「按釜自己的快慢走」。</summary>
    private double Gain()
    {
        double s = _tau + _p.Tw;
        double k = _p.KMargin * (s * s) / (4 * _tau * _p.Tw);
        return PyMax(1.0, k);
    }

    /// <summary>
    /// 第一次拿到「釜内、夹套都有效」的读数那一拍调（紧接着用同一拍的输入调 <c>Step(i, first: true)</c>）：
    /// Ŵ = 夹套、T̂ = 釜内、b̂ = 夹套 − 釜内；第二路从此刻的 τ̂ 重新起（把握回先验）；回归的累加量清零。
    /// 不动：τ̂、K、ε 的平均、门控、顶满计时、报警状态、上一拍的终点（照 Python 的 reset）。
    /// 只看 <see cref="LiveOuterInput.T"/> / <see cref="LiveOuterInput.Tr"/> / <see cref="LiveOuterInput.Tj"/>，三个都要是有限数。
    /// </summary>
    public void Reset(in LiveOuterInput i)
    {
        if (!double.IsFinite(i.Tr)) throw new ArgumentException("起算要一拍有效的釜内读数", nameof(i));
        if (!double.IsFinite(i.Tj)) throw new ArgumentException("起算要一拍有效的夹套读数", nameof(i));
        if (!double.IsFinite(i.T)) throw new ArgumentException("时刻要是有限数", nameof(i));
        var p = _p;
        _wall = i.Tj;
        _wallPrev = i.Tj;
        _wallRate = 0.0;
        _xr = i.Tr;
        _b = i.Tj - i.Tr;
        _ref = i.Tr;
        // 中值缓冲先放一个；紧跟着的 Step 会把同一个读数再推一次 → 第一拍缓冲里是两个一样的数。
        // 后果（照 Python）：起算后来的第一个新读数进不来（median(tr0, tr0, tr1) = tr0）。
        _m0 = i.Tr; _m1 = 0.0; _m2 = 0.0;
        _medN = 1;
        _hist.Clear();
        _hist.Enqueue((i.T, i.Tr));      // 同理：第一拍的 Step 还会再追加一笔 (t, T̂)
        _started = true;
        // 第二路
        _eT = i.Tr;
        _eB = i.Tj - i.Tr;
        _th = 1.0 / _tau;                // 是此刻的 τ̂，不是 τ̂₀
        _tauE = _tau;
        double v = p.P0ThRel * _th;
        _p00 = p.R; _p01 = 0.0; _p02 = 0.0;
        _p11 = p.P0b; _p12 = 0.0;
        _p22 = v * v;
        // 第一路
        _f1 = _f2 = i.Tr;
        _tr0 = i.Tr;
        _g1 = _g2 = i.Tj;
        _s0 = _s1 = _s2 = _s3 = _s4 = _s5 = 0.0;
    }

    /// <summary>
    /// 断档很久（设备断连、线程卡住）以后拿到第一拍有效读数时调（紧接着用同一拍的输入调 <c>Step(i, first: true)</c>）：
    /// Ŵ / T̂ / b̂ 按这一拍的读数重摆——断档里釜走到哪了模型不知道，接着旧状态推只会带歪；
    /// 但 τ̂（两路的）和对它的把握（P₂₂）原样留着：釜和物料没换，认到的时间常数还作数。
    /// 做法 = 试算台「两炉之间带着 τ̂」那个包装（checks.py 的 Carry）：Reset，再把 θ、τ₂、P₂₂ 放回去。
    /// 另外清两样（Python 没有这个入口，这两样是这里定的）：顶满计时归零（断档里顶没顶满不知道，宁可晚报）；
    /// ε 的平均归零（刚按读数摆好，读数离模型就是 0）。没起算过就等于 <see cref="Reset"/>。
    /// 上一拍的终点不动（跟 Reset 一样）：紧跟的 Step 照常拿这一拍的终点跟断档前的比——断档里真下发了新目标，把握照「终点变了 Δ」放开，
    /// 目标没变就原样留着。但这一拍喂的目标要是占位的（不在控，目标 = 釜内读数），断档前后读数差出来的那个 Δ 不是谁改了目标：
    /// 调用方要在 Reseat 之后、Step 之前调 <see cref="PinTarget"/>，否则把握会按「釜内在断档里漂了多少」被放回先验
    /// （实测：P₂₂ 3.3e-9、漂 10 K → 8.0e-8，先验 9.97e-8）。
    /// </summary>
    public void Reseat(in LiveOuterInput i)
    {
        if (!_started)
        {
            Reset(i);
            return;
        }
        double th = _th, tauE = _tauE, p22 = _p22;
        Reset(i);                        // 抛了就什么都没动（Reset 先查输入再动状态）
        _th = th;
        _tauE = tauE;
        _p22 = p22;
        _fullS = 0.0;
        _epsLp = 0.0;
    }

    /// <summary>
    /// 【本地改动】不在控（或在控的不是釜内）的拍，在 <see cref="Step"/> 之前调：这一拍喂的目标 <paramref name="sp"/> 只是占位的（= 当时的釜内读数），
    /// 不是谁下发的——跟 τ 有关的这一拍整个停着。做法是 Step 之前把四样状态摆一下，Step 自己一行不改：
    ///   ① 过渡参考 = 占位目标      → Step 里 d = 目标 − 过渡参考 = 0，门控 = 0；
    ///   ② 上一拍的终点 = 占位目标  → Step 里「终点变了 Δ」= 0，对 τ 的把握（P₂₂）不放开；
    ///   ③ 门控（上一拍留下的）= 0  → 这一拍观测器里两路都不学：第一路的 τ̂ 不动，第二路不往 θ 上攒新的相关；
    ///   ④ 第二路里 θ 跟 T₂、b₂ 的相关（P₀₂、P₁₂）清零 → 读数再怎么跳，θ 也不被带着走（定稿里 θ 顶边时清的也是这两个）。
    /// 结果：调过它的拍，τ̂、第二路的 θ / τ₂、P₂₂ 逐位不动；b̂ / T̂ / Ŵ（和第二路自己的 T₂、b₂）照算，公式照出。
    ///
    /// 为什么要有它：方案 B 节写的是「不在控时目标取当时的釜内读数（所以 g = 0，不学 τ；下发目标那一拍终点变了多少 = 新目标 − 釜内）」，
    /// 但光把目标喂成读数做不到括号里那句（2026-11-14 复核查出来的，下面的数是拿参考实现和这份 C# 各试出来的）：
    ///   · 过渡参考是按 τ̂ ÷ K（缺省约 650 s）的一阶滞后跟目标的，目标跟着读数走，釜内自己以 v 漂时 d ≈ v × 650 s：0.02 K/min 门控就半开；
    ///     停控后釜自然凉、空闲时加冷料，门控平均 0.95，τ̂ 被带偏一成到三成、第二路在下限 380 和上限 2850 之间乱跑，而这些拍还不进逐拍记录；
    ///   · 读数坏一个点，占位目标跳过去再跳回来，「终点变了」连触发两次：+5 K 一个坏点把握就放回先验的 3/4；
    ///   · 断档重摆那一拍，把握按「釜内在断档里漂了多少」放开（漂 10 K 放 8 成）。
    /// 只做 ①②（门控归零、把握不放）还不够：过渡走到一半停控，θ 跟 T₂、b₂ 的相关还大着，门控关了它照样顺着读数修 θ——
    /// 拿参考实现试（升温第 5 分钟停控）：之后第二路的 τ 自己漂 5–8 %，连坏两个点到 −12 %，加一份冷料（掉 15 K）−24 / −38 %。所以 ③④ 一起摆。
    /// 代价一：停控那一刻攒着的相关不要了，下次下发目标从 0 重新攒——试算台每一场、每一炉本来就是这么起步的
    /// （开控第一拍才起算；「两炉之间带着 τ̂」的包装 checks.py 的 Carry 也只带 θ、τ₂、P₂₂）。
    /// 代价二：开控前釜内自己在走的那一段，τ̂ 不再提前认。试算台「开控前估计器早就在跑」那几个案例原来是不带这一条跑的
    /// （attack-robust/xsim.py 的 preroll：obs.sp = 釜内读数），带上它各 20 台重跑：冲过一样（都 ≤ +0.06 K、20 / 20），升温两个时间一样，
    /// 降温那个（57–58 → 40 ℃）最慢的一台首进 ±0.1 K 从 61.8 变成 69.7 min；「开控时釜内正在动」三个案例偏差相差不到 0.01 K。
    /// 这不是加一条门槛规则：它只是把「这一拍有没有真目标」这个事实交给算法，公式还是那一条。
    ///
    /// 下发目标那一拍不调它：上一拍的终点还是上一拍的釜内读数，所以 Δ = 新目标 − 釜内、把握照放开；过渡参考从釜内读数起步，门控照常打开。
    /// 起算 / 断档重摆那一拍不在控时：Reset 或 <see cref="Reseat"/> 之后、Step 之前调。
    ///
    /// Python 定稿没有这个入口，等价的写法是 step 之前：<c>m.ref = obs.sp</c>；<c>if m.spf_prev is not None: m.spf_prev = obs.sp</c>；
    /// <c>m.g_sp = 0.0</c>；<c>m.P[0][2] = m.P[2][0] = m.P[1][2] = m.P[2][1] = 0.0</c>
    /// （对拍合成场 s12 就是这么生成的，见 tests/Tec.Core.Tests/LiveOuter/reference/）。
    /// 还没起算时什么都不做（Step 会先 Reset：过渡参考摆成釜内读数、门控本来是 0、相关本来是 0）。
    /// <paramref name="sp"/> 不是有限数抛 <see cref="ArgumentException"/>，抛之前不动状态。
    /// </summary>
    public void PinTarget(double sp)
    {
        if (!double.IsFinite(sp)) throw new ArgumentException("占位目标要是有限数（没在控时喂当时的釜内读数）", nameof(sp));
        if (!_started) return;
        _ref = sp;                       // ①
        if (_hasSpfPrev) _spfPrev = sp;  // ② 全新对象的第一拍还没有「上一拍的终点」：Step 那一拍本来就不比，留着「没有」
        _gSp = 0.0;                      // ③
        _p02 = 0.0;                      // ④
        _p12 = 0.0;
    }

    /// <summary>
    /// 算一拍，返回夹套目标和报警。<paramref name="first"/> = 这一拍刚 Reset / Reseat 过（同一拍的输入）：
    /// 观测器（釜壁估计、两路认 τ、T̂ / b̂）这一拍不跑，但过渡参考、控制律、顶满计时、报警照跑（照 Python）。
    /// 还没起算过就调：这一拍当起算（先 Reset 再按第一拍走，要有效的釜内读数）。
    /// 输入不合法（Dt ≤ 0、夹套 / 目标 / 时刻 / 终点 / 坡速不是有限数）抛 <see cref="ArgumentException"/>，抛之前不动任何状态。
    /// </summary>
    public LiveOuterOutput Step(in LiveOuterInput i, bool first = false)
    {
        // Python 在 dt = 0 时抛除零；夹套读数是 NaN 时没有任何防护（NaN 进了 Ŵ 再也出不来）。这里先挡住，状态一个不动。
        if (!(i.Dt > 0.0) || double.IsInfinity(i.Dt)) throw new ArgumentOutOfRangeException(nameof(i), i.Dt, "Dt 要大于 0");
        if (!double.IsFinite(i.Tj)) throw new ArgumentException("夹套读数无效的拍不要调 Step", nameof(i));
        if (!double.IsFinite(i.Sp)) throw new ArgumentException("目标要是有限数（没在控时喂当时的釜内读数）", nameof(i));
        if (!double.IsFinite(i.T)) throw new ArgumentException("时刻要是有限数", nameof(i));
        if (i.SpFinal is double f0 && !double.IsFinite(f0)) throw new ArgumentException("终点没有就给 null，不要给 NaN", nameof(i));
        if (i.SpRate is double r0 && !double.IsFinite(r0)) throw new ArgumentException("坡速没有就给 null，不要给 NaN", nameof(i));
        if (!_started)
        {
            Reset(i);                    // 釜内读数无效会在这里抛（还什么都没动）
            first = true;
        }
        var p = _p;

        // ⓪ 读数：3 点中值（第一拍也推——见 Reset 里的说明）
        bool has = Reading(i.Tr, out double tr);

        // ①–④ 观测器：第一拍整段不跑
        if (!first) Observe(i.Dt, i.Tj, has, tr);

        // ⑤ 目标
        double sp = i.Sp;
        double rate = i.SpRate is double r && r != 0.0 ? r : 0.0;       // 没给 / 0 → 0
        double spFinal = i.SpFinal ?? sp;                               // 没给 → 此刻目标
        // 终点（不是此刻目标）这一拍变了 Δ：对 τ 的把握按 Δ² ÷ (Δ² + e1²) 往先验放（P₂₂ 已经不小于先验就不动）
        if (_hasSpfPrev)
        {
            double dl = spFinal - _spfPrev;
            if (dl != 0.0)
            {
                double w = dl * dl / (dl * dl + p.ReopenE1 * p.ReopenE1);
                double v = p.P0ThRel / p.Tau0;
                double vMax = v * v;
                if (_p22 < vMax) _p22 += (vMax - _p22) * w;
            }
        }
        _spfPrev = spFinal;
        _hasSpfPrev = true;
        // 「目标变动引起的过渡还剩多少」：目标过一阶滞后（常数 = 名义趋近时间常数 τ̂ ÷ K，用这一拍改过的 τ̂、K）；第一拍也走
        _ref += (sp - _ref) * (1.0 - Math.Exp(-i.Dt / (_tau / _k)));
        double d = sp - _ref;
        _gSp = d * d / (d * d + p.E0 * p.E0);                           // 下一拍的两路认 τ 用

        // ⑥⑦ 控制律：夹套目标 = 目标 + b̂ + G(目标 − T̂)。这里不夹（−50…200 在出口另给一个夹过的）
        double u = sp + _b + Proportional(sp - _xr);
        if (rate != 0.0 || spFinal != sp)
        {
            // 走坡：多给前馈 (τ̂ + T̂w)·坡速，但不许越过「直接去终点」会给的那个数。
            // 取小还是取大看的是「终点 ≥ 此刻目标」，不是坡速的符号（照 Python 的字面）。
            u += p.RampFf * (_tau + p.Tw) * rate;
            double uf = spFinal + _b + Proportional(spFinal - _xr);
            u = spFinal >= sp ? PyMin(u, uf) : PyMax(u, uf);
        }
        _u = u;

        // ⑧ 放热报警（只报，不动 u；没读数的拍也算）
        bool fire = Alarm(i);
        return new LiveOuterOutput(u, Math.Clamp(u, p.JacketMinC, p.JacketMaxC), fire, _alarmOn);
    }

    /// <summary>⓪ 3 点中值：最近三个读数取中间那个，只坏一个点的读数进不来。没读数的拍不进缓冲、也不清缓冲。</summary>
    private bool Reading(double trIn, out double tr)
    {
        if (!HasReading(trIn))
        {
            tr = 0.0;
            return false;
        }
        if (_medN < 3)
        {
            if (_medN == 0) _m0 = trIn;
            else if (_medN == 1) _m1 = trIn;
            else _m2 = trIn;
            _medN++;
        }
        else
        {
            _m0 = _m1; _m1 = _m2; _m2 = trIn;
        }
        if (_medN == 1)
        {
            tr = _m0;
        }
        else if (_medN == 2)
        {
            tr = _m1 > _m0 ? _m1 : _m0;                                 // 两个数取大的那个（Python：sorted(...)[len // 2]）
        }
        else
        {
            double a = _m0, b = _m1, c = _m2, t;
            if (a > b) { t = a; a = b; b = t; }
            if (b > c) { t = b; b = c; c = t; }
            if (a > b) { t = a; a = b; b = t; }
            tr = b;
        }
        return true;
    }

    /// <summary>①–④：釜壁估计 → 第二路（没读数也推）→ 第一路（有读数才跑）→ 估计器。次序不能换：后面的用前面刚算的。</summary>
    private void Observe(double dt, double tj, bool has, double tr)
    {
        var p = _p;
        // ① 釜壁估计：夹套读数过一阶滞后（用实测夹套，所以内环饱和、被钳住都不用另外告诉外环——执行不了的不会攒）
        _wall += (tj - _wall) * (1.0 - Math.Exp(-dt / p.Tw));
        _wallRate = (_wall - _wallPrev) / dt;
        _wallPrev = _wall;
        // ② 第二路：用上一拍的门控、这一拍刚更新的 Ŵ 和它的变化率
        TauEkfStep(dt, has, tr);
        // ③ 第一路：下限用这一拍第二路刚算出来的 τ₂
        if (has) TauRegStep(dt, tr);
        // ④ 估计器：τ̂ 用 ③ 改过之后的；ε 用旧的 T̂；T̂、b̂ 都从旧值各走一步
        double tau = _tau;
        double eps = has ? tr - _xr : 0.0;
        _epsLp += (eps - _epsLp) * (1.0 - Math.Exp(-dt / p.EpsLpS));    // 没读数也更新（往 0 回）
        double q = _epsLp / p.Eps0;
        double to = PyMax(p.ToMin, p.To / (1.0 + q * q));               // 读数离模型远就快：To′ = max(20, 150 ÷ (1 + (ε̄/2)²))
        _toEff = to;
        double l1 = 2.0 / to - 1.0 / tau;
        double c = tau / (to * to);
        _xr += dt * ((_wall - _xr - _b) / tau + l1 * eps);              // 右边的 T̂、b̂ 是旧值（下一行还没执行）
        _b += dt * (-c * eps);
    }

    /// <summary>τ̂ 变了：观测器里「釜内在以多快的速度走」是事实，不跟着跳——把 b̂ 按同一个斜率换算过去。K 只在这里重算。</summary>
    private void SetTau(double tauNew)
    {
        double dtau = tauNew - _tau;
        if (dtau != 0.0)
        {
            double slope = (_wall - _xr - _b) / _tau;                   // 用旧的 τ̂
            _b -= dtau * slope;
            _tau = tauNew;
            _k = Gain();
        }
    }

    /// <summary>
    /// ② 第二路：T₂、它自己的 b₂、θ = 1 ÷ τ₂ 三个量，按「读数 − 模型」一起小修（扩展卡尔曼）。它只把 τ₂ 交出去，T₂、b₂ 不进控制律。
    /// 没读数的拍只推不修。P 是对称阵，只存上三角 6 个数。
    /// </summary>
    private void TauEkfStep(double dt, bool has, double tr)
    {
        var p = _p;
        double g = _gSp;                                                // 上一拍算的门控：过渡里才学 θ
        double th = _th;                                                // 这一拍修之前的 θ：下面推 T₂、算 F、算 qm 都用它
        double x = _wall - _eT - _eB;
        _eT += dt * th * x;
        double f00 = 1.0 - dt * th;
        double f01 = -dt * th;
        double f02 = dt * x * g;
        double r0 = f00 * _p00 + f01 * _p01 + f02 * _p02;
        double r1 = f00 * _p01 + f01 * _p11 + f02 * _p12;
        double r2 = f00 * _p02 + f01 * _p12 + f02 * _p22;
        double n00 = r0 * f00 + r1 * f01 + r2 * f02;
        double a = p.SigDelta * _wallRate * th * dt;
        double qm = a * a * (p.TCorr / dt);                             // 釜壁估计动得快，这一拍少信模型
        // （Python 这里还有一段「P₂₂ 低于先验时回涨」，定稿 q_th_rel = 0，是空操作，没搬）
        double q00 = n00 + 1e-8 * dt + qm;
        double q01 = r1, q02 = r2;
        double q11 = _p11 + p.Qb * dt;
        double q12 = _p12;
        double q22 = _p22;
        if (has)
        {
            double sden = q00 + p.R;
            double k0 = q00 / sden, k1 = q01 / sden, k2 = q02 / sden;
            double inn = tr - _eT;
            _eT += k0 * inn;
            _eB += k1 * inn;
            _th += k2 * inn;
            // P ← P − k·(P 第 0 行)，9 个元素都算，再对称化（非对角取两边的平均）——照 Python，只有修正之后才对称化
            double m00 = q00 - k0 * q00;
            double m01 = q01 - k0 * q01;
            double m02 = q02 - k0 * q02;
            double m10 = q01 - k1 * q00;
            double m11 = q11 - k1 * q01;
            double m12 = q12 - k1 * q02;
            double m20 = q02 - k2 * q00;
            double m21 = q12 - k2 * q01;
            double m22 = q22 - k2 * q02;
            q00 = m00; q11 = m11; q22 = m22;
            q01 = 0.5 * (m01 + m10);
            q02 = 0.5 * (m02 + m20);
            q12 = 0.5 * (m12 + m21);
        }
        // θ 夹在 1/(3·τ̂₀) … 1/(0.4·τ̂₀)；顶边（正好在边上也算）时把它跟另两个量的相关清零
        double lo = 1.0 / (p.TauHi * p.Tau0);
        double hi = 1.0 / (p.TauLo * p.Tau0);
        if (_th <= lo || _th >= hi)
        {
            _th = PyMin(hi, PyMax(lo, _th));
            q02 = 0.0;
            q12 = 0.0;
        }
        q00 = PyMax(q00, 1e-12);
        q11 = PyMax(q11, 1e-12);
        q22 = PyMax(q22, 1e-18);
        // 相关系数不许超过 ±0.999（数值上保住 P 正定）
        double lim = 0.999 * Math.Sqrt(q00 * q11);
        if (Math.Abs(q01) > lim) q01 = Math.CopySign(lim, q01);
        lim = 0.999 * Math.Sqrt(q00 * q22);
        if (Math.Abs(q02) > lim) q02 = Math.CopySign(lim, q02);
        lim = 0.999 * Math.Sqrt(q11 * q22);
        if (Math.Abs(q12) > lim) q12 = Math.CopySign(lim, q12);
        _p00 = q00; _p01 = q01; _p02 = q02; _p11 = q11; _p12 = q12; _p22 = q22;
        _tauE = 1.0 / _th;
    }

    /// <summary>
    /// ③ 第一路：回归。y = F[Ŵ − Tr] − β·(F[Tr] − 起点)，z = F[dTr/dt]（两级低通之差，不做差分），带遗忘的加权协方差，τ回归 = cov(z, y) ÷ var(z)。
    /// 往大改回归说了算；往小改要两路都同意（回归值不许小过第二路的 τ₂）。有读数才跑——没读数时滤波量、累加量都不动。
    /// </summary>
    private void TauRegStep(double dt, double tr)
    {
        var p = _p;
        double a = 1.0 - Math.Exp(-dt / p.Tf);
        _f1 += (tr - _f1) * a;
        _f2 += (_f1 - _f2) * a;                                         // 后一级用前一级刚更新的值
        _g1 += (_wall - _g1) * a;
        _g2 += (_g1 - _g2) * a;
        double z = (_f1 - _f2) / p.Tf;
        double y = _g2 - _f2 - p.Beta * (_f2 - _tr0);
        double wr = PyMax(Math.Abs(_g1 - _g2) / p.Tf, Math.Abs(_wallRate));
        double xx = p.SigD * wr / p.RegR;
        double w = 1.0 / (1.0 + xx * xx);                               // 釜壁快动的拍少算数
        double d = Math.Exp(-dt / p.Tm);
        _s0 = _s0 * d + w;
        _s1 = _s1 * d + w * z;
        _s2 = _s2 * d + w * y;
        _s3 = _s3 * d + w * z * z;
        _s4 = _s4 * d + w * z * y;
        _s5 = _s5 * d + w * y * y;
        if (_s0 <= 1e-9) return;
        double czz = _s3 - _s1 * _s1 / _s0;
        double czy = _s4 - _s1 * _s2 / _s0;
        double cyy = _s5 - _s2 * _s2 / _s0;
        double lam = p.Tm / dt * (p.Z0 * p.Z0);
        if (czz <= 0.0 || cyy <= 0.0) return;                           // 起算后头几拍 z 恒为 0，走这里
        double den = Math.Sqrt(czz * cyy);
        if (den == 0.0) return;                                         // 乘积下溢到 0：Python 会抛除零；实际到不了，这里当「这一拍不动」
        double rho = czy / den;
        double rp = PyMax(0.0, rho);
        double g = czz / (czz + lam) * (rp * rp) * _gSp;                // 信多少：速度够不够大 × 相关够不够 × 是不是在过渡里（门控是上一拍的）
        double lo = PyMax(p.TauLo * p.Tau0, PyMin(p.Tau0, _tauE));      // 往小改不许小过第二路；第二路 ≥ 出厂值时不许往出厂值以下改
        double tauLs = PyMin(p.TauHi * p.Tau0, PyMax(lo, czy / czz));
        double rate = dt / p.Ta * g;
        SetTau(_tau + rate * (tauLs - _tau));
    }

    /// <summary>比例那一份 G(e)：降温方向（e ≤ 0）斜率 K − 1、不封顶；升温方向平滑封顶在 P上限（tanh）——夹套比釜内热太多会过冲，凉太多不会。</summary>
    private double Proportional(double e)
    {
        double km = _k - 1.0;
        if (e <= 0.0) return km * e;
        double pu = PUp;
        if (pu <= 1e-9) return 0.0;                                     // ρ 配成 0 才到这里
        return pu * Math.Tanh(km * e / pu);
    }

    /// <summary>
    /// ⑧ 放热报警：制冷顶满（继电器在 TEC 侧、内环贴制冷下限、不在等水 / 等冷水机间隔、输出开着）连续 ≥ 60 s，
    /// 且 T̂ 比 60 s 前高 0.02 K 以上，且釜壁估计已经比釜内冷。三条同时成立的那一拍报一次；哪条不成立了就重新武装，没有冷却时间。
    /// </summary>
    private bool Alarm(in LiveOuterInput i)
    {
        var p = _p;
        if (_hist.Count >= HistCap) _hist.Dequeue();
        _hist.Enqueue((i.T, _xr));
        // 先追加再弹；严格小于：正好 60 s 前的那一笔留着。刚追加的那一笔永远弹不掉，队列不会空（Count > 1 只是双保险）
        while (_hist.Count > 1 && _hist.Peek().T < i.T - p.AlarmWindowS) _hist.Dequeue();
        bool full = i.TecSide && i.InnerSatLow && !i.TecWithheld && i.OutputOn;
        _fullS = full ? _fullS + i.Dt : 0.0;                            // 有一个不成立就当拍清零
        bool rising = _xr - _hist.Peek().Xr > p.AlarmRiseK;
        bool cond = _fullS >= p.AlarmFullS && rising && _wall < _xr;
        bool fire = cond && !_alarmOn;
        _alarmOn = cond;
        return fire;
    }

    /// <summary>全部标量状态的快照（60 s 窗不在里面）。</summary>
    public LiveOuterState ExportState() => new()
    {
        Started = _started, Tau = _tau, K = _k, EpsLp = _epsLp, ToEff = _toEff, GSp = _gSp, FullS = _fullS, AlarmOn = _alarmOn,
        HasSpfPrev = _hasSpfPrev, SpfPrev = _spfPrev, U = _u,
        Wall = _wall, WallPrev = _wallPrev, WallRate = _wallRate, Xr = _xr, B = _b, Ref = _ref,
        MedN = _medN, M0 = _m0, M1 = _m1, M2 = _m2,
        ET = _eT, EB = _eB, Th = _th, TauE = _tauE,
        P00 = _p00, P01 = _p01, P02 = _p02, P11 = _p11, P12 = _p12, P22 = _p22,
        F1 = _f1, F2 = _f2, G1 = _g1, G2 = _g2, Tr0 = _tr0,
        S0 = _s0, S1 = _s1, S2 = _s2, S3 = _s3, S4 = _s4, S5 = _s5,
    };

    /// <summary>
    /// 把标量状态整个换成快照里的（原样照搬，不做任何换算，K 也用快照里的）。60 s 窗不动；窗里一笔都没有时补一笔 (0, T̂) 免得报警那段没东西可比。
    /// 只给测试和以后的落盘恢复用。
    /// </summary>
    public void ImportState(in LiveOuterState s)
    {
        if (s.MedN is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(s), s.MedN, "中值缓冲里只能有 0–3 个数");
        _started = s.Started; _tau = s.Tau; _k = s.K; _epsLp = s.EpsLp; _toEff = s.ToEff; _gSp = s.GSp; _fullS = s.FullS; _alarmOn = s.AlarmOn;
        _hasSpfPrev = s.HasSpfPrev; _spfPrev = s.SpfPrev; _u = s.U;
        _wall = s.Wall; _wallPrev = s.WallPrev; _wallRate = s.WallRate; _xr = s.Xr; _b = s.B; _ref = s.Ref;
        _medN = s.MedN; _m0 = s.M0; _m1 = s.M1; _m2 = s.M2;
        _eT = s.ET; _eB = s.EB; _th = s.Th; _tauE = s.TauE;
        _p00 = s.P00; _p01 = s.P01; _p02 = s.P02; _p11 = s.P11; _p12 = s.P12; _p22 = s.P22;
        _f1 = s.F1; _f2 = s.F2; _g1 = s.G1; _g2 = s.G2; _tr0 = s.Tr0;
        _s0 = s.S0; _s1 = s.S1; _s2 = s.S2; _s3 = s.S3; _s4 = s.S4; _s5 = s.S5;
        if (_started && _hist.Count == 0) _hist.Enqueue((0.0, _xr));
    }
}
