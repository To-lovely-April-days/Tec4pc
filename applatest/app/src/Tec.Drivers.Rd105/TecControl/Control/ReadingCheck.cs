namespace TecControl.Core.Control;

// 【本地改动】（2026-11-17，外环「按实测实时算」第 1 步复核之后用户选的 S1「读数对不上就停控」）整个文件是本地新增的，源工程里没有。
//
// 这是什么：新外环在控时，判「釜内读数是不是跟本釜脱钩了」（读数卡住 / 探头提出液面 / 插错工位）。像超温、跑飞保护一样只管故障、
//   不参与控温：不回写任何控制量，只给回路一个 verdict（停控 / 只报警 / 没事）。只在新外环在控时由回路每个外环拍（影子出了一份新读数的那一拍）喂一次。
// 照 Python 定稿逐行写：scratchpad step1/safety/spec/reading_check.py（2026-11-17，定稿全文同目录 README.md；拷了一份在
//   tests/Tec.Core.Tests/LiveOuter/reference/s1/），对拍夹具 tests/Tec.Core.Tests/LiveOuter/golden/s1/（10 场 28 496 拍，|C# − Py| ≤ 1e-9 + 1e-9·|Py|）。
//   运算顺序（y30 每拍从老到新重加、R 用 Ec / Ew 的式子、历史二分找 k*、队列 / 段的同值规则）跟 Python 一样，别「优化」成滚动和——对拍会差到 1e-9 以外。
//
// 两条（详见定稿 §1）：
//   A「读数没跟着动」：读数 30 s 平均从参考起一个 x0（0.02 K，2 个字）都没动，而「最慢的釜」按最近一个 τ̂₀（出厂 950 s）里夹套推的量该挪 R_A：
//       R_A ≥ r0（0.25 K）→ 停控（夹套往上推、读数不升：读数在本釜以下卡住 / 读的不是本釜——再推会把釜烧过头）
//       R_A ≤ −r0 → 只报警（夹套往下推、读数不降：跟「慢慢变强的放热」从温度上分不开，按用户「放热只报不停」）
//   B「加热时读数往下掉、而且不回」：只看「读数 < 目标 且 Ŵ > 读数」的拍（热只能往釜里流）：读数从最近 τc 里的最高处掉了 ≥ D0（8 K），
//       而且最慢的釜（掉下去那一截当成一下子丢掉的热——加冷料——再按夹套推的量往回补）这会儿比读数高 u ≥ r0 → 停控。
//   「最慢的釜」= Ŵ 再过一段 T̂w（出厂 170 s）一阶、再过一段 τc = TauHi × τ̂₀ = 3 × 950 = 2850 s（新算法认得出的最慢的釜）一阶的阶跃响应。
//   不用学出来的 τ̂（τ̂ 学偏了也不碍判据）；不按温度分段，没有「升温中 / 保温中」之类的状态。
//
// 纯计算：不读钟（时刻是调用方给的影子现场秒）、不碰 I/O、不抛异常（输入是 NaN / 无穷就按「读不到」走）。不是线程安全的：只在回路那一条顺序流上用。

/// <summary>【本地改动】S1 一拍的判断。</summary>
public enum ReadingVerdict
{
    /// <summary>没事（含读数读不到、只记了 Ŵ 的拍）。</summary>
    Ok,

    /// <summary>停控：A（夹套往上推、读数不动）或 B（加热中读数掉了而且不回）。</summary>
    Stop,

    /// <summary>只报警：A 的制冷方向（夹套往下推、读数不降）。</summary>
    Alarm,
}

/// <summary>
/// 【本地改动】S1 一拍的结果，字段同 Python 定稿的 <c>Beat</c>（对拍逐个比）。
/// </summary>
/// <param name="Verdict">停控 &gt; 只报警 &gt; 没事。</param>
/// <param name="Which">"A" / "B" / "AB" / ""：哪一条成立（A 停或 A 报警都算 A）。</param>
/// <param name="Valid">这一拍读数有效、判据算了。</param>
/// <param name="Y30">读数 30 s 平均（℃）。</param>
/// <param name="Ra">A：最慢的釜从回看起点起该挪多少（K，带号；刚重起参考为 0）。</param>
/// <param name="ATs">A：回看起点（影子现场秒）= max(参考时刻, t − τ̂₀)；NaN = 这一拍刚重起参考。</param>
/// <param name="TRef">A：参考时刻（s）；NaN = 没有参考。</param>
/// <param name="YRef">A：参考时刻的 30 s 平均（℃）。</param>
/// <param name="Heat">B：这一拍是不是「加热拍」（30 s 平均 &lt; 目标 且 Ŵ &gt; 30 s 平均）。</param>
/// <param name="D">B：最近 τc 秒里加热拍的最高 − 此刻（K；不是加热拍为 0）。</param>
/// <param name="TPk">B：最高那一拍的时刻（s；NaN = 没有）。</param>
/// <param name="TSeg">B：这一段往下走的起点（s，不早于 t − τc；NaN = 没有）。</param>
/// <param name="Rise">B：这一段掉到底以后回升了多少（K）。</param>
/// <param name="RPk">B：最慢的釜从 <paramref name="TSeg"/> 起按夹套推的量该升多少（K）。</param>
/// <param name="U">B：最慢的釜比读数高多少（K；D ≥ D0 且 U ≥ r0 判停）。</param>
public readonly record struct ReadingCheckBeat(
    ReadingVerdict Verdict, string Which, bool Valid, double Y30, double Ra, double ATs, double TRef, double YRef,
    bool Heat, double D, double TPk, double TSeg, double Rise, double RPk, double U)
{
    /// <summary>A 的制冷方向成立（夹套往下推够了、读数不降：「读数没跟着夹套降」报警那一条）。A 成立而 R_A 为负只可能是这一向。</summary>
    public bool CoolingAlarm => Which.Contains('A') && Ra < 0;
}

/// <summary>
/// 【本地改动】S1「釜内读数跟夹套对不上」判据（见文件头）。一路一个；开控锁成新外环时新建（同一场挂起再开控沿用），
/// 新外环每出一份新读数调一次 <see cref="Step"/>。
/// </summary>
public sealed class ReadingCheck
{
    /// <summary>「读数动了」「读数回升了」的最小量：0.02 K = 2 个字（宇电 0.01 K 一个字）。设计者原数。</summary>
    public const double X0 = 0.02;

    /// <summary>最慢的釜该动的量：0.25 K（比 ±0.1 的带宽宽）。设计者原数；定稿 H0 8585 场里 R_A 最大 +0.127 / −0.203，挑错 H0 最大 +0.231。</summary>
    public const double R0 = 0.25;

    /// <summary>加热中读数往下掉多少才算「掉了」：8 K。唯一的工艺数（定稿 §6）。2026-11-17 用户交给我们定：定 8 K；以后接进料，把「泵正在打、流量多少」交给判据，不靠抬这个数。</summary>
    public const double D0 = 8.0;

    /// <summary>读数尾随平均的窗：30 s（外环 2 s 一拍、15 个数）。设计者原数。</summary>
    public const double WinS = 30.0;

    private readonly double _tauC;     // τc = TauHi × τ̂₀（2850 s）：新算法认得出的最慢的釜；B 回看、Ŵ 历史也留这么久
    private readonly double _tw;       // 多一段 T̂w（出厂 170 s）：Ŵ 已经比夹套慢 T̂w，再多一段 = 真釜壁慢到模型以为的两倍
    private readonly double _aWin;     // A 回看：τ̂₀（出厂 950 s）——只算最近一个出厂釜时间常数里夹套推的量
    private readonly double _gap;      // 两次有效读数隔了超过它：A 的参考、B 的队列从这一拍重起
    private readonly double _a, _b;    // a = τc ÷ (τc − T̂w)，b = T̂w ÷ (τc − T̂w)

    private readonly List<(double T, double W, double Ec, double Ew)> _hist = new();  // 按 t 递增；前面过期的定期删
    private int _h0;                                                                  // _hist 里还有效的第一条
    private readonly Queue<(double T, double Y)> _ybuf = new();                       // 最近 WinS 秒的有效读数
    private double? _lastOk;                                                          // 上一次有效读数的时刻
    private double? _tRef;                                                            // A 的参考
    private double _yRef = double.NaN;
    private readonly LinkedList<(double T, double Y)> _maxq = new();                  // B：加热拍的 (t, y30)，y 单调降（队头 = 窗里最高、同值取最晚）
    private double? _tLast;                                                           // 上一拍的时刻（判影子重摆）
    private Seg? _seg;                                                                // B：这一段往下走

    /// <summary>B 的一段：[起点, 最低, 最早到这么低的时刻, 掉 / 回, 回升最高, 它的时刻]（Python 的 6 元 list）。</summary>
    private sealed class Seg
    {
        public double TSeg, YMin, TMin, YHi, THi;
        public bool Rising;
    }

    /// <param name="parameters">这一路新外环核心类的出厂参数（<see cref="LiveOuter.Params"/>）：用它的 τ̂₀、TauHi、T̂w。null = 缺省。</param>
    /// <param name="gapS">两次有效读数最多隔多久（现场秒）还接着算：回路给 OuterLossGrace（缺省 30 s，读数断这么久 E3 本来就停控了）。</param>
    public ReadingCheck(LiveOuterParams? parameters, double gapS)
    {
        var p = parameters ?? new LiveOuterParams();
        _tauC = p.TauHi * p.Tau0;
        _tw = p.Tw;
        _aWin = p.Tau0;
        _gap = gapS;
        _a = _tauC / (_tauC - _tw);
        _b = _tw / (_tauC - _tw);
        Reset();
    }

    /// <summary>τc（s）：最慢的釜的时间常数。</summary>
    public double TauC => _tauC;

    /// <summary>两次有效读数最多隔多久还接着算（s）。</summary>
    public double GapS => _gap;

    /// <summary>从头来：历史、参考、队列、段全清。</summary>
    public void Reset()
    {
        _hist.Clear();
        _h0 = 0;
        _ybuf.Clear();
        _lastOk = null;
        _tRef = null;
        _yRef = double.NaN;
        _maxq.Clear();
        _tLast = null;
        _seg = null;
    }

    // ── 最慢的釜 ──

    private void PushWall(double t, double w)
    {
        var h = _hist;
        double ec, ew;
        if (h.Count > _h0)
        {
            var (tp, wp, ecp, ewp) = h[^1];
            var dw = w - wp;
            var dt = t - tp;
            ec = ecp * Math.Exp(-dt / _tauC) + dw;
            ew = ewp * Math.Exp(-dt / _tw) + dw;
        }
        else
        {
            ec = ew = 0.0;
        }
        h.Add((t, w, ec, ew));
        // 留到 t − τc 之前的最后一条（回看起点最早 t − τc，要它前面那一拍当 k*）
        var lim = t - _tauC;
        while (_h0 + 1 < h.Count && h[_h0 + 1].T <= lim)
            _h0++;
        if (_h0 > 4096)
        {
            h.RemoveRange(0, _h0);
            _h0 = 0;
        }
    }

    /// <summary>t_k ≤ ts 的最后一拍（没有就是留着的第一拍）。二分。</summary>
    private int KStar(double ts)
    {
        var h = _hist;
        int lo = _h0, hi = h.Count - 1;
        if (h[lo].T > ts) return lo;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (h[mid].T <= ts) lo = mid;
            else hi = mid - 1;
        }
        return lo;
    }

    /// <summary>R(ts)：最慢的釜从 ts 那一刻静止起、按 ts 以后夹套推的量，到此刻该挪多少（K，带号）。
    /// = Σ_{t_k &gt; ts} ΔŴ_k·h(t − t_k)，h(s) = 1 − a·e^{−s/τc} + b·e^{−s/T̂w}（按拍阶梯输入精确）。</summary>
    private double Response(double ts)
    {
        var (t, w, ec, ew) = _hist[^1];
        var (tk, wk, eck, ewk) = _hist[KStar(ts)];
        var s = t - tk;
        return (w - wk) - _a * (ec - Math.Exp(-s / _tauC) * eck) + _b * (ew - Math.Exp(-s / _tw) * ewk);
    }

    // ── 读数 30 s 平均 ──

    private double Y30(double t, double? y)
    {
        var q = _ybuf;
        if (y is { } v0) q.Enqueue((t, v0));
        while (q.Count > 0 && q.Peek().T <= t - WinS) q.Dequeue();
        if (q.Count == 0) return double.NaN;
        var s = 0.0;
        foreach (var (_, v) in q) s += v;           // 每拍从老到新重加（≤ 15 个数）：不带累加误差，跟 Python 同顺序逐位一样
        return s / q.Count;
    }

    /// <summary>
    /// 一拍。<paramref name="t"/> = 影子现场秒（单调增，<see cref="LiveOuterShadow.ClockS"/>）；<paramref name="tr"/> = 这一拍喂给影子的釜内原始读数（读不到 NaN）；
    /// <paramref name="sp"/> = 回路此刻的釜内目标；<paramref name="wall"/> = Ŵ（<see cref="LiveOuterReadout.Wall"/>）；
    /// <paramref name="tau"/> = τ̂（<see cref="LiveOuterReadout.Tau"/>）——定稿不用它，照传（Python 那个 a_win ≤ 0 的试验分支这里不写）。
    /// </summary>
    public ReadingCheckBeat Step(double t, double tr, double sp, double wall, double tau)
    {
        _ = tau;
        if (_tLast is { } tl && t - tl > LiveOuterShadow.ReseatGapS)
            Reset();                                        // 影子断档重摆过：Ŵ 是重新摆的，跟断档前接不上
        _tLast = t;
        PushWall(t, wall);
        var ok = double.IsFinite(tr);
        var y30 = Y30(t, ok ? tr : null);
        if (!ok || double.IsNaN(y30))
            // 读数读不到：只记了 Ŵ（夹套推的照算），A / B 都不动、不判（A 的参考留着）
            return new ReadingCheckBeat(ReadingVerdict.Ok, "", false, y30, 0.0, double.NaN, _tRef ?? double.NaN, _yRef,
                false, 0.0, double.NaN, double.NaN, 0.0, 0.0, 0.0);
        if (_lastOk is { } lastOk && t - lastOk > _gap)
        {
            // 读数断了超过 gap：断档里釜走到哪不知道，A 的参考、B 的队列都从这一拍重起
            _tRef = null;
            _maxq.Clear();
            _seg = null;
        }
        _lastOk = t;

        // A
        double ra, aTs;
        if (_tRef is null || Math.Abs(y30 - _yRef) >= X0)
        {
            _tRef = t;
            _yRef = y30;
            ra = 0.0;
            aTs = double.NaN;
        }
        else
        {
            aTs = PyMax(_tRef.Value, t - _aWin);
            ra = Response(aTs);
        }

        // B
        var heat = y30 < sp && wall > y30;
        var mq = _maxq;
        if (heat)
        {
            while (mq.Count > 0 && mq.Last!.Value.Y <= y30) mq.RemoveLast();
            mq.AddLast((t, y30));
            var g = _seg;
            if (g is null)
            {
                // 刚武装（窗里头一个加热拍）：这一拍就是这一段的起点
                _seg = new Seg { TSeg = t, YMin = y30, TMin = t, Rising = false, YHi = y30, THi = t };
            }
            else if (!g.Rising)
            {
                if (y30 < g.YMin) { g.YMin = y30; g.TMin = t; }                   // 更低：最低处挪到这一拍（同值不挪 = 留最早到这么低的那一刻）
                else if (y30 >= g.YMin + X0) { g.Rising = true; g.YHi = y30; g.THi = t; }   // 从最低处回升了 x0：读数在跟着回
            }
            else
            {
                if (y30 >= g.YHi) { g.YHi = y30; g.THi = t; }                     // 回升途中的最高（同值取最晚）
                else if (y30 <= g.YHi - X0)
                {
                    // 回升后又往下走 x0：新的一段，起点 = 回升到的最高那一拍
                    g.TSeg = g.THi;
                    g.YMin = y30;
                    g.TMin = t;
                    g.Rising = false;
                }
            }
        }
        var bw = _tauC;                                     // B 回看最慢的釜一个时间常数（τc）：比它慢的往下掉不算
        while (mq.Count > 0 && mq.First!.Value.T < t - bw) mq.RemoveFirst();
        if (mq.Count == 0) _seg = null;
        double d = 0.0, rise = 0.0, rPk = 0.0, u = 0.0, tPk = double.NaN, tSeg = double.NaN;
        if (heat && mq.Count > 0)
        {
            var (tp, yPk) = mq.First!.Value;
            tPk = tp;
            d = yPk - y30;
            var g = _seg!;
            tSeg = PyMax(g.TSeg, t - bw);
            double yMin = g.YMin, tMin = g.TMin;
            rise = y30 - yMin;
            rPk = Response(tSeg);
            // 最慢的釜：这一段起点 t_seg 起静止、夹套照实推（r_pk）；从窗里最高处掉下去的 y_pk − y_min 当成 t_min 那一刻一下子丢掉的热
            // （加冷料；放在最低点那一刻 = 最宽的算法），之后按 τc 往回补。读数比这个最慢的釜低多少 = u
            u = rPk - rise + (yPk - yMin) * (1.0 - Math.Exp(-(t - tMin) / _tauC));
        }

        var stopA = ra >= R0;
        var alarmA = ra <= -R0;
        var stopB = heat && d >= D0 && u >= R0;
        var which = (stopA || alarmA ? "A" : "") + (stopB ? "B" : "");
        var verdict = stopA || stopB ? ReadingVerdict.Stop : alarmA ? ReadingVerdict.Alarm : ReadingVerdict.Ok;
        return new ReadingCheckBeat(verdict, which, true, y30, ra, aTs, _tRef.Value, _yRef, heat, d, tPk, tSeg, rise, rPk, u);
    }

    /// <summary>Python 的 max(a, b)：b 严格大才取 b（两个都不是 NaN 时跟 Math.Max 一样；写成这样只为跟参考逐字对得上）。</summary>
    private static double PyMax(double a, double b) => b > a ? b : a;
}

/// <summary>【本地改动】（2026-11-17，S1 复核查实）「读数没跟着夹套降」报警这一拍的沿。</summary>
public enum ReadingAlarmEdge
{
    /// <summary>没变（含读数读不到的拍：保持上一状态）。</summary>
    None,

    /// <summary>报出来了：A 的制冷方向这一拍成立（<see cref="ReadingCheckBeat.CoolingAlarm"/>）。</summary>
    Raised,

    /// <summary>撤了：读数 30 s 平均离开报警那一刻的参考 ≥ <see cref="ReadingCheck.X0"/>（读数真动了）。</summary>
    Cleared,
}

/// <summary>
/// 【本地改动】（2026-11-17，S1 复核查实：制冷方向的报警在夹套推到底以后自己撤了）「读数没跟着夹套降」报警的锁存。判据（<see cref="ReadingCheck"/>）一字不动，
/// 只管「什么时候算撤了」：
/// · 起：A 的制冷方向这一拍成立，记下判据此刻的参考值 y_ref（读数 30 s 平均从它起一个 x0 都没动过）；
/// · 撤：只在读数真动了——这一拍有效、30 s 平均离开报警时的 y_ref ≥ x0（0.02 K，判据里「读数动了」的同一个数，不另加门槛）；
/// · 读数读不到的拍（Valid 为假）保持上一状态；判据重起参考（读数断档 &gt; gap / 影子重摆）也不撤——参考重起不等于读数动了。
/// 原来按「A 的制冷方向这一拍还成立不成立」撤：A 只算最近 τ̂₀（950 s）里夹套推的量，夹套推到底（回路下限 −50 / TEC 制冷极限）不动以后，
/// 窗滑过「往下推」那一段 R_A 就回到 −r0 以内（D1 闭环 = Python twin：冻在 70、目标 60，冻住后 71.8 min、R_A −0.248、Ŵ −50.13），报警撤了、
/// 还说「读数又跟着夹套动了」，之后 R_A 一直是 0 再也起不来，真釜内被一路冷到 −44.6 ℃（冻住后 95 min）。
/// 回路（HostControlLoop.ReadingCheckTick）每个喂了判据的拍调一次 <see cref="Step"/>；开控 / 停控换一个新的。纯状态，不读钟、不抛。
/// </summary>
public sealed class ReadingAlarmLatch
{
    /// <summary>报警此刻亮着。</summary>
    public bool On { get; private set; }

    /// <summary>报警起来那一拍判据的参考值（℃，30 s 平均）；没报为 NaN。</summary>
    public double YRef { get; private set; } = double.NaN;

    /// <summary>喂一拍判据的结果，返回这一拍的沿。</summary>
    public ReadingAlarmEdge Step(in ReadingCheckBeat b)
    {
        if (!b.Valid) return ReadingAlarmEdge.None;
        if (!On)
        {
            if (!b.CoolingAlarm) return ReadingAlarmEdge.None;
            On = true;
            YRef = b.YRef;
            return ReadingAlarmEdge.Raised;
        }
        if (!(Math.Abs(b.Y30 - YRef) >= ReadingCheck.X0)) return ReadingAlarmEdge.None;
        On = false;
        YRef = double.NaN;
        return ReadingAlarmEdge.Cleared;
    }
}
