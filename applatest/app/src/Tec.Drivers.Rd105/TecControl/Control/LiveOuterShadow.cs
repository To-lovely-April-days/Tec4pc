namespace TecControl.Core.Control;

// 【本地改动】（2026-11-14，外环改「按实测实时算」第 0 步）整个文件是本地新增的，源工程（src/TecControl.Core）里没有。
//
// 这是什么：新外环 LiveOuter 挂进回路的那一层「影子」——回路每拍把此刻的事实交给它，它决定这一拍算不算、怎么喂、
//   算完把结果交回去记进逐拍记录。第 0 步只算不控：这里的输出只流向 ChannelCycleInfo.LiveOuter（→ 逐拍记录末尾的 lo_ 列）
//   和一条「影子停了」的提示，不回写回路的任何状态。
//
// 为什么单独成类、不写在 HostControlLoop 里：
//   · 回路的时间是墙钟（TecController 给快照打的 DateTime.Now），没有可注入的钟；节拍 / 拆小步 / 断档重摆这些逻辑写在回路里就只能真等着测。
//     做成「时刻当参数传进来」的小块，回路只负责把快照时刻交给它，测试里时刻自己编。
//   · 它只认 LiveOuterFacts 里那几样只读的数，够不着回路的状态——「影子的输出没有流回控制量」从类型上就看得出来。
//
// 节拍（主会话 2026-11-14 定的，Python 参考实现的 step(obs) 自己不管节拍）：
//   (a) 离上一拍满一个外环周期（回路的 OuterPeriod，缺省 2 s）才算一拍；在控不在控都算。
//       釜内串级在控时尽量跟旧外环同一拍算：它的节拍还没到（LoopBeatPending）就再等一等——最多多等不到一个周期，之后两边一直同拍。
//   (b) 平常的拍——回路周期一抖，外环拍就落到 2.5–5 s——不拆，一拍一步、dt 用真隔了多久：这就是参考喂法（golden.py、方案里的真机重放、
//       试算台），对拍夹具里最长一拍 12.8 s 也是这么喂的。只有离上一拍超过 10 s（真断了一阵）才拆成每步 ≤ 2 s 的若干等长小步，
//       只有最后一小步带釜内读数，前面的小步按「无读数」只推模型（LiveOuter 的观测器是显式一步，拍太长会走歪：一步 300 s 时 b̂ 从 2.96 掉到 0.69）。
//       门槛为什么是 10 s 不是主会话原先说的 2.5 s（2026-11-14 复核后改的）：无读数的小步会让核心类的第一路回归停着（它有读数才跑），
//       带读数的那一小步又只按小步的长度 h 走——这一拍真实过去的 dt − h 就丢了：两级 100 s 低通在墙钟上变慢、釜内速度被高估、
//       τ回归偏小，τ̂ 一路贴到第二路给的下限上；观测器按读数修的那一份也只剩 h ÷ dt。真机 14 场记录里 13 场有 16–24 % 的拍超过 2.5 s，
//       按 2.5 s 拆、拿定稿在这 13 场的输入上喂，跟参考喂法比：τ̂ 逐拍最多偏 0.7–18.4 %（末值低 0.6–11.4 %），b̂ 最多差 0.13–2.36 K，
//       夹套目标最多差 0.14–1.31 K，而且偏的是危险的那一头（τ̂ 偏小 → b̂ 高估 → 冲过）。门槛 4 s 仍差到 0.47 K、6 s 仍有一场差 0.17 K，
//       10 s 起 13 场逐拍相同，只有 11-04 08:53 那场（4 拍超过 10 s，最长 12.8 s）还差 0.002 K。
//       10 s 一步对观测器是安全的：最快时间常数 To′ ≥ 20 s，显式一步的离散极点 1 − dt ÷ To′ ≥ 0.5。
//       小步里的夹套读数、目标、报警要的事实都用这一拍的（参考实现没定义断档里该喂什么，这是这里定的）：这样釜壁估计、过渡参考、顶满计时
//       这几个按指数 / 累加精确离散的量，拆不拆走到的是同一处（只差舍入）。
//   (c) 断档超过 60 s（设备断连、线程卡住、夹套读数丢了一阵、墙钟往前跳）：中间釜走到哪了模型不知道，
//       用下一拍「釜内、夹套都有效」的读数重摆 Ŵ / T̂ / b̂（LiveOuter.Reseat），τ̂ 和对它的把握留着。等重摆的那些拍记录里留空。
//       重摆那一拍：在控而且断档里目标真改了，把握照「终点变了 Δ」放开；目标没改、或者不在控（见下面「占位目标」），原样留着。
//   (d) 第一次拿到「釜内、夹套都有效」的读数那一拍起算（= Python 的 reset + 第一拍 step）。之后开控、停控、改目标都不清。
//   墙钟往回跳（对时）：这一拍不算，把「上一拍时刻」挪到现在——不然要等墙钟追回来才有下一拍；状态留着。
//
// 喂什么（同上）：
//   · 目标：釜内串级在控 → 回路此刻的釜内设定（走坡时是坡上的当前值）；不在控 / 在控的是夹套 → 当时的釜内读数（读数无效沿用上一个目标）。
//   · 占位目标（2026-11-14 复核后加的）：不在控时喂的那个「目标」不是谁下发的，它跟着读数变不算「目标变了」。每逢喂的是它，
//     算之前先 LiveOuter.PinTarget——跟 τ 有关的这一拍整个停着：门控为 0、对 τ 的把握不放开、第二路里 θ 跟读数的相关清掉，
//     τ̂（两路的）和把握逐位不动；b̂ / T̂ / Ŵ 照算。方案 B 节写的本来就是「不在控……g = 0，不学 τ；下发目标那一拍终点变了多少 = 新目标 − 釜内」，
//     光把目标喂成读数做不到：停控后釜自然凉、空闲时加冷料，门控是全开的，τ̂ 在不进记录的拍里被带偏；读数坏一个点、断档里釜内漂了几 K，
//     把握都会被放回先验（数见 PinTarget 的注释）。下发目标那一拍不 Pin：Δ = 新目标 − 上一拍的釜内读数，把握照常放开、门控照常打开。
//     这样同一次连接里停控再开控，下一份逐拍记录开头的 lo_tau / lo_tau2 就是上一份结尾的数。
//   · 第 0 步不给这一段的终点和坡速（终点 = 目标、坡速 = 0）：ITemperatureProfile 现在拿不出这两样，斜坡接口是第 2 步的事。
//     所以按速率走的配方段里影子看到的是一串小台阶、没有斜坡前馈，那几段的 lo_isp 不能当真。
//   · 釜内读数无效 → 传「无读数」（只按模型推）；夹套读数无效 → 这一拍不算，状态留着，按断档处理。
//
// 不许拖累控温：Tick 自己整段 try / catch，从不往外抛；抛了异常、或者内部量出现 NaN / 无穷，这一路影子就停掉（Stopped），
//   直到这个对象被换掉（回路重建）。回路那一头还有一层 try / catch 兜着。只在回路那一条顺序流上用，不加锁。

/// <summary>
/// 【本地改动】影子这一拍交给逐拍记录的读数（不可变）。两拍之间回路每拍拿到的是同一个对象——记录里重复上一拍的数。
/// </summary>
/// <param name="JacketSetpointC">新算法想给的夹套目标（℃），已夹到 −50…200。记录列 lo_isp。</param>
/// <param name="BHat">实时算的维持温差 b̂（K）：稳住时 = 夹套 − 釜内。lo_b。</param>
/// <param name="THat">滤过的釜内 T̂（℃）。lo_xr。</param>
/// <param name="Wall">釜壁估计 Ŵ（℃）。lo_wall。</param>
/// <param name="Tau">τ̂（s）：进控制律的那个。lo_tau。</param>
/// <param name="TauEkf">第二路认的 τ（s）。lo_tau2。</param>
/// <param name="AlarmOn">放热报警的条件这一拍成立。lo_alarm。第 0 步回路不知道继电器在哪一侧（按「不在 TEC 侧」喂），所以恒为 false。</param>
public sealed record LiveOuterReadout(
    double JacketSetpointC, double BHat, double THat, double Wall, double Tau, double TauEkf, bool AlarmOn);

/// <summary>
/// 【本地改动】回路每拍交给影子的事实：都是回路此刻自己知道的量，只读。
/// 影子在这一路的控制计算之前拿到它，所以「夹套目标 / 内环输出」这类是上一拍的——正好是 Python 试算台 Obs 的定义。
/// </summary>
public readonly record struct LiveOuterFacts
{
    /// <summary>夹套读数（℃，这一路内环的被控量）；无效 NaN。</summary>
    public double Tj { get; init; }

    /// <summary>釜内读数（℃，串级外环的被控量）；无效 NaN。</summary>
    public double Tr { get; init; }

    /// <summary>釜内串级在控（在闭环控温、被控对象是釜内）。false = 不在控，或者在控的是夹套。</summary>
    public bool ReactorInControl { get; init; }

    /// <summary>回路此刻的主设定（℃）。只有 <see cref="ReactorInControl"/> 时才是釜内目标（控夹套时它是夹套目标，影子不用）。</summary>
    public double SetpointC { get; init; }

    /// <summary>旧外环在走自己的节拍、而且这一拍还没到它（离它上一拍不到一个外环周期）：影子也先不算，等它那一拍一起算。</summary>
    public bool LoopBeatPending { get; init; }

    /// <summary>热源继电器在 TEC 侧。回路不知道继电器（会话扳的）——第 0 步恒给 false（宁可不报），第 1 步由会话把「TEC 此刻真接着」给进来。</summary>
    public bool TecSide { get; init; }

    /// <summary>内环贴在下限（上一拍 PID 那一侧的输出 ≤ 下限；只加热形态下是贴着 0）。</summary>
    public bool InnerSatLow { get; init; }

    /// <summary>TEC 被扣着：双向挡里在等人答开不开冷却水 / 等冷水机最短间隔，回路按只加热算。</summary>
    public bool TecWithheld { get; init; }

    /// <summary>输出开着（在控、没在切继电器的那一两秒里）。</summary>
    public bool OutputOn { get; init; }
}

/// <summary>
/// 【本地改动】新外环 <see cref="LiveOuter"/> 的影子运行（见文件头）。一路一个，跟回路同寿命。用法：回路每拍
/// <c>var r = shadow.Tick(快照时刻, 外环周期秒数, 事实);</c>——返回这一拍该记进记录的读数；
/// 还没起算 / 在等断档后重摆 / 已经停掉时返回 null（记录里那几列留空）。
/// 不是线程安全的：只在回路那一条顺序流上调。
/// </summary>
public class LiveOuterShadow
{
    /// <summary>一小步最长多少秒。2 s = Python 试算台的外环周期，试算成绩都是按它跑的。</summary>
    public const double MaxStepS = 2.0;

    /// <summary>离上一拍超过多少秒才拆小步。10 s：平常的节拍抖动（真机 14 场里 13 场有 16–24 % 的拍落在 2.5–5 s）不拆、dt 原样一步喂——
    /// 拆出来的无读数小步会让第一路回归丢时间、τ̂ 系统性偏小（见文件头 (b)）；10 s 一步对观测器还是安全的（最快 To′ = 20 s，离散极点 ≥ 0.5）。</summary>
    public const double SplitAboveS = 10.0;

    /// <summary>断档超过多少秒就不接着推、等有效读数重摆。60 s：再长的话中间釜走到哪了，只按模型推已经不可信。</summary>
    public const double ReseatGapS = 60.0;

    private bool _started;                 // 起算过（核心类 Reset 过）
    private DateTime _lastAt;              // 上一次真算了的那一拍的时刻（调用方给的时刻，自己不读钟）
    private double _clockS;                // 影子自己的秒表：各拍 dt 累加，喂给核心类当时刻（它只拿来给报警的 60 s 窗记时刻）
    private double _sp = double.NaN;       // 上一次喂的目标：不在控又没有釜内读数时沿用
    private LiveOuterReadout? _last;

    public LiveOuterShadow(LiveOuterParams? parameters = null)
    {
        Core = new LiveOuter(parameters);
    }

    /// <summary>核心类。给测试和排查看内部量用；控温路径上没有人读它。</summary>
    public LiveOuter Core { get; }

    /// <summary>起算过没有。</summary>
    public bool Started => _started;

    /// <summary>已经停掉（抛过异常 / 内部量出现过 NaN、无穷）。停了就一直停着，直到这个对象被换掉。</summary>
    public bool Stopped { get; private set; }

    /// <summary>为什么停的；没停为 null。</summary>
    public string? StopReason { get; private set; }

    /// <summary>最近一拍算出来的读数；还没起算 / 已经停掉为 null。</summary>
    public LiveOuterReadout? Last => _last;

    /// <summary>把这一路影子停掉（之后 <see cref="Tick"/> 一律返回 null、什么都不算）。回路那一头兜住异常时调。</summary>
    public void Stop(string reason)
    {
        if (Stopped) return;
        Stopped = true;
        StopReason = reason;
        _last = null;
    }

    /// <summary>
    /// 回路每拍调一次。<paramref name="now"/> = 这一拍快照的时刻；<paramref name="periodS"/> = 外环周期（s）。
    /// 从不往外抛：里面出了任何异常就把这一路影子停掉、返回 null。
    /// </summary>
    public LiveOuterReadout? Tick(DateTime now, double periodS, in LiveOuterFacts f)
    {
        if (Stopped) return null;
        try
        {
            return TickCore(now, periodS, f);
        }
        catch (Exception ex)
        {
            Stop($"{ex.GetType().Name}：{ex.Message}");
            return null;
        }
    }

    private LiveOuterReadout? TickCore(DateTime now, double periodS, in LiveOuterFacts f)
    {
        bool hasTj = double.IsFinite(f.Tj), hasTr = double.IsFinite(f.Tr);

        // (d) 起算：第一次「釜内、夹套都有效」的那一拍
        if (!_started)
        {
            if (!hasTj || !hasTr) return null;
            Seat(now, periodS, f, reseat: false);
            return _last;
        }

        double dt = (now - _lastAt).TotalSeconds;
        if (dt < 0.0)
        {
            // 墙钟往回跳了：真实过了多久不知道（多半很短）。这一拍不算，从现在重新计拍
            _lastAt = now;
            return _last;
        }

        // (c) 断档：不接着推，等一拍两个读数都有效的重摆；等的时候记录留空（旧读数已经不作数了）
        if (dt > ReseatGapS)
        {
            if (!hasTj || !hasTr) return null;
            Seat(now, periodS, f, reseat: true);
            return _last;
        }

        if (!hasTj) return _last;                              // 夹套读数无效：这一拍不算，状态留着（久了就走上面的断档）
        if (!(dt > 0.0) || dt < periodS) return _last;         // (a) 还没到拍
        if (f.LoopBeatPending) return _last;                   // (a) 跟旧外环对齐：它那一拍还没到，一起等

        double sp = Target(f, hasTr, out bool placeholder);
        if (!double.IsFinite(sp)) return _last;                // 起算时一定有过目标，到不了这里；到了就不算这一拍
        if (placeholder) Core.PinTarget(sp);                   // 占位目标：它变了不算「目标变了」，跟 τ 有关的这一拍停着，见文件头

        // (b) 平常的拍一步走完、dt 原样；真断了一阵（> SplitAboveS）才拆小步：等长，每步 ≤ MaxStepS，只有最后一小步带釜内读数
        int n = dt > SplitAboveS ? (int)Math.Ceiling(dt / MaxStepS) : 1;
        double h = dt / n;
        LiveOuterOutput o = default;
        for (int k = 1; k <= n; k++)
        {
            _clockS += h;
            o = StepCore(Input(f, sp, k == n && hasTr ? f.Tr : double.NaN, h), first: false);
        }
        _lastAt = now;
        Publish(o);
        return _last;
    }

    /// <summary>目标取什么：釜内串级在控 → 回路的釜内设定；否则 → 当时的釜内读数；读数也没有 → 上一个目标。
    /// <paramref name="placeholder"/> = 取到的不是回路的釜内设定（后两种）：这个「目标」只是占位的，调用处要先 <see cref="LiveOuter.PinTarget"/>。</summary>
    private double Target(in LiveOuterFacts f, bool hasTr, out bool placeholder)
    {
        placeholder = !(f.ReactorInControl && double.IsFinite(f.SetpointC));
        double sp = !placeholder ? f.SetpointC
            : hasTr ? f.Tr
            : _sp;
        _sp = sp;
        return sp;
    }

    private LiveOuterInput Input(in LiveOuterFacts f, double sp, double tr, double dt) => new()
    {
        T = _clockS,
        Dt = dt,
        Sp = sp,
        Tr = tr,
        Tj = f.Tj,
        TecSide = f.TecSide,
        InnerSatLow = f.InnerSatLow,
        TecWithheld = f.TecWithheld,
        OutputOn = f.OutputOn,
        // SpFinal / SpRate 不给（null）：第 0 步终点 = 目标、坡速 = 0，见文件头
    };

    /// <summary>起算（reseat = false）或断档后重摆（true）：同一拍的输入先摆好状态，再按「第一拍」走一步。调用前已确认两个读数都有效。</summary>
    private void Seat(DateTime now, double periodS, in LiveOuterFacts f, bool reseat)
    {
        // 第一拍也要给一个 dt（过渡参考、顶满计时那一拍按它走）：就用外环周期，Python 试算台给的也是这个（2 s）；周期配得离谱就按 2 s
        double dt0 = periodS > 0.0 && periodS <= MaxStepS ? periodS : MaxStepS;
        if (reseat) _clockS += (now - _lastAt).TotalSeconds;   // 秒表照常往前走（60 s 窗反正在重摆里清了）
        double sp = Target(f, hasTr: true, out bool placeholder);
        var input = Input(f, sp, f.Tr, dt0);
        SeatCore(input, reseat);
        // 占位目标（不在控）：断档前后釜内读数差出来的那一截不是谁改了目标，重摆这一拍不许拿它去放开对 τ 的把握。
        // 在控时不 Pin：断档里目标真改了，照「终点变了 Δ」放开；没改就是 Δ = 0。起算那一拍 Pin 不 Pin 都一样（过渡参考已经是釜内读数）。
        if (placeholder) Core.PinTarget(sp);
        var o = StepCore(input, first: true);
        _started = true;
        _lastAt = now;
        Publish(o);
    }

    /// <summary>这一拍算完：内部量有一个不是有限数就停掉这一路影子，否则换一份新的读数。</summary>
    private void Publish(in LiveOuterOutput o)
    {
        var c = Core;
        if (!c.IsFinite || !double.IsFinite(o.U) || !double.IsFinite(o.JacketSetpointC))
        {
            Stop("内部量出现 NaN / 无穷");
            return;
        }
        _last = new LiveOuterReadout(o.JacketSetpointC, c.BHat, c.THat, c.Wall, c.Tau, c.TauEkf, o.AlarmOn);
    }

    /// <summary>摆状态：起算走 <see cref="LiveOuter.Reset"/>，断档后重摆走 <see cref="LiveOuter.Reseat"/>。虚方法只为测试能看见 / 换掉这一步。</summary>
    protected virtual void SeatCore(in LiveOuterInput input, bool reseat)
    {
        if (reseat) Core.Reseat(input);
        else Core.Reset(input);
    }

    /// <summary>算一步。虚方法只为测试能看见每一步喂了什么、能让它抛异常（核「影子抛了、控温照常」）。</summary>
    protected virtual LiveOuterOutput StepCore(in LiveOuterInput input, bool first) => Core.Step(input, first);
}
