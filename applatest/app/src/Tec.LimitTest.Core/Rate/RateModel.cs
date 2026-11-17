using Tec.LimitTest.Runs;

namespace Tec.LimitTest.Rate;

// 【本地改动】（2026-11-17，用户要的「升温速率测试和降温速率测试」）
// 用户原话：「主要测满功率下看一分钟夹套的温度能不能上升 20 度，出一个 excel 报告」。
// 用户定的（2026-11-17）：放极限测试工具里；升温、降温都按「一分钟 ≥ 20 ℃」判；从当前温度直接开始（不先控到起点）；
// 一分钟到了输出归零、再记 1 分钟看停了以后还冲多少。
// 满功率 = 温控器「最大输出占空比」LIMITED（协议上限 90 %），程序不改它：给夹套下一个够不着的目标，让输出一直顶在上限，
// 不动驱动（外环第 1 步正在真机上测，驱动那几个文件不碰）。每秒实际输出了多少照记，没顶满报告里标出来。

/// <summary>升温（加热棒满功率）还是降温（TEC 满功率，要冷却水）。</summary>
public enum RateKind { Heat, Cool }

public static class RateKinds
{
    public static string Name(RateKind k) => k == RateKind.Heat ? "升温" : "降温";

    /// <summary>升温看「升了多少」取 +1，降温看「降了多少」取 −1：变化量 = 符号 ×（后 − 前）。</summary>
    public static double Sign(RateKind k) => k == RateKind.Heat ? 1 : -1;
}

/// <summary>一轮速率测试的设置。界面上改，开始时拍一份快照给 Runner。</summary>
public sealed class RateSettings
{
    public bool Heat { get; set; } = true;
    public bool Cool { get; set; } = true;
    /// <summary>测哪几个工位（两个一起测是缺省，用户定的）。</summary>
    public bool WellA { get; set; } = true;
    public bool WellB { get; set; } = true;
    /// <summary>测几次。每一次 = 升温一次再降温一次（勾了哪个做哪个），都从当时的温度直接开始。</summary>
    public int Rounds { get; set; } = 1;

    /// <summary>合格线（℃）：满功率这段至少升这么多。缺省 20（用户原话：一分钟夹套能不能上升 20 度）。</summary>
    public double HeatPassK { get; set; } = 20;
    /// <summary>合格线（℃）：满功率这段至少降这么多。缺省 20（用户 2026-11-17 选的：降温也按一分钟 20 ℃ 判）。</summary>
    public double CoolPassK { get; set; } = 20;

    /// <summary>满功率开多久（s），从这一路接上满功率那一刻起算。缺省 60（一分钟）。</summary>
    public int FullSeconds { get; set; } = 60;
    /// <summary>输出归零之后再记多久（s）：看停了以后夹套还会冲多少。缺省 60（用户 2026-11-17 选的）。</summary>
    public int AfterSeconds { get; set; } = 60;

    /// <summary>
    /// 升温停止线（℃）：升温途中哪一路夹套到这里就马上归零。缺省 190——导热油夹套硬上限 200 ℃，留 10 ℃ 给加热棒停了以后的余温上冲。
    /// 升温的「够不着的目标」也取它（再不超过温控器的超温上限）。
    /// </summary>
    public double StopLine { get; set; } = 190;

    /// <summary>夹套读数这么多秒没更新（或读不到）就整轮停：满功率加热下等不起管线 15 s 的过期判断。</summary>
    public int ReadingLossSeconds { get; set; } = 5;

    /// <summary>下令之后这么多秒还没接上（等冷却水 / 等继电器 / 驱动卡住）就算这一项中止。</summary>
    public int ConnectMaxSeconds { get; set; } = 180;

    public string Operator { get; set; } = "";
    public double? Ambient { get; set; }
    public string Patch { get; set; } = "";
    /// <summary>釜里装的什么（手填，写进报告）：玻璃釜满功率急升急降，空釜和有料差别很大。</summary>
    public string Material { get; set; } = "";

    public double PassOf(RateKind k) => k == RateKind.Heat ? HeatPassK : CoolPassK;

    public IEnumerable<int> Wells()
    {
        if (WellA) yield return 0;
        if (WellB) yield return 1;
    }

    public RateSettings Clone() => (RateSettings)MemberwiseClone();
}

public enum RateState { Pending, Running, Done, Aborted, Skipped }

/// <summary>一项测试（升温或降温的第几次），两个工位各一条记录。</summary>
public sealed class RateTest
{
    public RateTest(RateKind kind, int round)
    {
        Kind = kind;
        Round = round;
    }

    public RateKind Kind { get; }
    /// <summary>第几次（1 起）。</summary>
    public int Round { get; }
    public RateState State { get; set; } = RateState.Pending;
    /// <summary>中止 / 跳过的原因；完成是空。</summary>
    public string Note { get; set; } = "";
    /// <summary>给夹套下的那个够不着的目标（℃）。</summary>
    public double? Target { get; set; }
    /// <summary>冷却水温度（手填，降温才有）。</summary>
    public double? Water { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    /// <summary>两个工位的记录；没测的那一路是 null。</summary>
    public RateTrace?[] Traces { get; } = new RateTrace?[2];

    public string Title => $"{RateKinds.Name(Kind)} 第 {Round} 次";
    public string SheetName => $"{RateKinds.Name(Kind)} 第{Round}次";

    public string StateText => State switch
    {
        RateState.Pending => "待测",
        RateState.Running => "进行中",
        RateState.Done => "完成",
        RateState.Aborted => "中止",
        _ => "跳过"
    };
}

/// <summary>Runner 每秒读到的一个点：读的那一刻 + 读数（读数里的 Tj 自带采样时刻）。</summary>
public readonly record struct RatePoint(DateTimeOffset At, WellReading R);

/// <summary>一个工位在一项测试里的全部记录：下令、接上、归零的时刻，和每秒的点。时间相关的数都从这里算。</summary>
public sealed class RateTrace
{
    private readonly object _gate = new();
    private DateTimeOffset? _t0;

    public RateTrace(int well, RateKind kind)
    {
        Well = well;
        Kind = kind;
    }

    public int Well { get; }
    public RateKind Kind { get; }
    public string WellName => Well == 0 ? "A" : "B";

    /// <summary>下令的时刻。</summary>
    public DateTimeOffset? CommandAt { get; set; }

    /// <summary>接上满功率的时刻（下发目标那一下返回：热源切好、冷却水通好、回路开了）。0 s 从这里算。别的线程写，加锁。</summary>
    public DateTimeOffset? T0
    {
        get { lock (_gate) return _t0; }
        set { lock (_gate) _t0 = value; }
    }

    /// <summary>输出归零的时刻（满功率到时 / 到停止线 / 中止）。</summary>
    public DateTimeOffset? StopAt { get; set; }

    /// <summary>为什么提前归零（到停止线之类）；按时归零是 null。</summary>
    public string? EarlyStop { get; set; }

    /// <summary>这一路这一项开始时从温控器读的最大输出占空比（%）；读不到 null。</summary>
    public double? Limited { get; set; }

    /// <summary>这一路没测成的原因（开始前就拒了：夹套读不到、过了停止线、TEC 接不上……）。</summary>
    public string? Refused { get; set; }

    /// <summary>归零之后的记录也记满了。</summary>
    public bool Finished { get; set; }

    public List<RatePoint> Points { get; } = new();
}

/// <summary>一个工位一项测试算出来的数（报告汇总表一行）。都是从每秒记录里算的，算不出来就是 null，不编。</summary>
public sealed record RateMetrics
{
    /// <summary>「合格」「不合格」「判不了」（中止 / 没跑满又没升够）。</summary>
    public required string Verdict { get; init; }
    public double? Start { get; init; }
    public double? End { get; init; }
    /// <summary>满功率这段实际跑了多少秒（提前归零就不满 FullSeconds）。</summary>
    public double? RanSeconds { get; init; }
    /// <summary>满功率这段升了（升温）/ 降了（降温）多少 ℃，正数 = 往要的方向走了。</summary>
    public double? Change { get; init; }
    public double PassK { get; init; }
    /// <summary>升够 / 降够合格线用了多少秒；没够是 null。</summary>
    public double? TimeToPass { get; init; }
    /// <summary>最快的那 10 秒折合每分钟多少 ℃。</summary>
    public double? Fastest10s { get; init; }
    /// <summary>满功率这段里输出顶在最大输出占空比上的点占多少 %（最大输出占空比没读到就是 null）。</summary>
    public double? FullOutPercent { get; init; }
    /// <summary>满功率这段输出的平均值（取绝对值，%）。</summary>
    public double? MeanOut { get; init; }
    public double? MeanCur { get; init; }
    /// <summary>满功率这段用的热源（出现最多的那个）。</summary>
    public string? Source { get; init; }
    public double? TrStart { get; init; }
    public double? TrEnd { get; init; }
    /// <summary>满功率这段夹套跟釜内差得最多的时候差多少 ℃（玻璃釜怕的就是这个）。</summary>
    public double? MaxJacketReactorGap { get; init; }
    /// <summary>归零之后夹套还往同一个方向走了多少 ℃（升温：还升了多少；降温：还降了多少）。</summary>
    public double? Overrun { get; init; }
    /// <summary>归零之后的极值（升温：最高；降温：最低）。</summary>
    public double? AfterPeak { get; init; }
    /// <summary>极值在归零之后第几秒。</summary>
    public double? AfterPeakSeconds { get; init; }
    /// <summary>下令到接上满功率用了多少秒（切热源、等冷却水的时间）。</summary>
    public double? ConnectSeconds { get; init; }
    public string Note { get; init; } = "";
}

/// <summary>
/// 从一个工位的每秒记录算报告里的数。纯计算，不碰设备，测试直接喂点。
///
/// 规矩：
/// · 夹套温度按它自己的采样时刻（<see cref="WellReading.TjAt"/>）排、同一个采样只算一次，要某一时刻的值就在前后两个采样之间线性插值；
/// · 0 s = 接上满功率（<see cref="RateTrace.T0"/>），「满功率 N s 后」取 T0 + N 的插值——不取 Runner 那一拍读数（晚零点几秒）；
/// · 输出、电流、热源、釜内这几样没有自己的时刻，用 Runner 读的那一刻，取离得最近的点；
/// · 算不出来的格子是 null，报告里留空。
/// </summary>
public static class RateMath
{
    /// <summary>夹套温度序列（按采样时刻、去重）。</summary>
    public static List<(DateTimeOffset T, double V)> TjSeries(RateTrace tr)
    {
        var list = new List<(DateTimeOffset T, double V)>();
        foreach (var p in tr.Points)
        {
            if (p.R.Tj is not { } v || double.IsNaN(v)) continue;
            var t = p.R.TjAt ?? p.At;
            if (list.Count > 0 && t <= list[^1].T) continue;    // 同一个采样读了两次 / 时钟往回：只认第一次
            list.Add((t, v));
        }
        return list;
    }

    /// <summary>某一时刻的值：前后两个采样之间线性插值；在序列外面 0.5 s 以内取端点，再远是 null（不外推）。</summary>
    public static double? At(List<(DateTimeOffset T, double V)> s, DateTimeOffset t)
    {
        if (s.Count == 0) return null;
        if (t <= s[0].T) return (s[0].T - t).TotalSeconds <= 0.5 ? s[0].V : null;
        if (t >= s[^1].T) return (t - s[^1].T).TotalSeconds <= 0.5 ? s[^1].V : null;
        var hi = s.FindIndex(x => x.T >= t);
        var (t1, v1) = s[hi - 1];
        var (t2, v2) = s[hi];
        var f = (t - t1).TotalSeconds / (t2 - t1).TotalSeconds;
        return v1 + (v2 - v1) * f;
    }

    /// <summary>离 t 最近的那个 Runner 读数（1.5 s 以内）；没有是 null。</summary>
    public static WellReading? Near(RateTrace tr, DateTimeOffset t)
    {
        WellReading? best = null;
        var bestD = double.MaxValue;
        foreach (var p in tr.Points)
        {
            var d = Math.Abs((p.At - t).TotalSeconds);
            if (d < bestD) { bestD = d; best = p.R; }
        }
        return bestD <= 1.5 ? best : null;
    }

    /// <summary>输出算不算顶满：|输出| 到了最大输出占空比（差 0.5 % 以内，温控器回显有小数）。</summary>
    public static bool IsFull(double? output, double? limited)
        => output is { } o && limited is { } l && l > 0 && Math.Abs(o) >= l - 0.5;

    public static RateMetrics Compute(RateTrace tr, RateSettings s, string? abortNote = null)
    {
        var pass = s.PassOf(tr.Kind);
        if (tr.Refused is { } why)
            return new RateMetrics { Verdict = "判不了", PassK = pass, Note = "没测：" + why };
        var t0 = tr.T0;
        var connect = t0 is { } a && tr.CommandAt is { } c ? (a - c).TotalSeconds : (double?)null;
        if (t0 is null)
            return new RateMetrics
            {
                Verdict = "判不了", PassK = pass, ConnectSeconds = null,
                Note = abortNote is null ? "没接上满功率" : $"没接上满功率就中止：{abortNote}"
            };
        var sign = RateKinds.Sign(tr.Kind);
        var series = TjSeries(tr);
        var full = TimeSpan.FromSeconds(s.FullSeconds);
        var stop = tr.StopAt ?? (series.Count > 0 ? series[^1].T : t0.Value);
        var end = stop < t0.Value + full ? stop : t0.Value + full;
        var ran = Math.Max(0, (end - t0.Value).TotalSeconds);
        var ranFull = ran >= s.FullSeconds - 0.5;

        var start = At(series, t0.Value);
        var endV = At(series, end);
        double? change = start is { } sv && endV is { } ev ? sign * (ev - sv) : null;

        // 升够合格线的时刻：在满功率这段里第一次越过「起点 + 符号 × 合格线」，在两个采样之间插出越线那一刻
        double? toPass = null;
        if (start is { } st)
        {
            var line = st + sign * pass;
            (DateTimeOffset T, double V)? prev = (t0.Value, st);
            foreach (var p in series.Where(x => x.T > t0.Value && x.T <= end))
            {
                if (sign * (p.V - line) >= 0)
                {
                    var (pt, pv) = prev!.Value;
                    var f = Math.Abs(p.V - pv) < 1e-12 ? 1 : Math.Clamp((line - pv) / (p.V - pv), 0, 1);
                    toPass = (pt - t0.Value).TotalSeconds + f * (p.T - pt).TotalSeconds;
                    break;
                }
                prev = p;
            }
        }

        // 最快的那 10 s：在满功率这段里按 1 s 一格滑，取变化最大的那一窗，折合每分钟
        double? fastest = null;
        for (var k = 0; k + 10 <= ran + 1e-6; k++)
        {
            var v1 = At(series, t0.Value + TimeSpan.FromSeconds(k));
            var v2 = At(series, t0.Value + TimeSpan.FromSeconds(k + 10));
            if (v1 is null || v2 is null) continue;
            var r = sign * (v2.Value - v1.Value) * 6;
            if (fastest is null || r > fastest) fastest = r;
        }

        // 满功率这段（接上之后、归零之前）的输出、电流、热源、釜内
        var win = tr.Points.Where(p => p.At > t0.Value && p.At <= end).ToList();
        var outs = win.Where(p => p.R.Out is not null).ToList();
        double? fullPct = tr.Limited is { } lim && lim > 0 && outs.Count > 0
            ? 100.0 * outs.Count(p => IsFull(p.R.Out, lim)) / outs.Count
            : null;
        double? meanOut = outs.Count > 0 ? outs.Average(p => Math.Abs(p.R.Out!.Value)) : null;
        var curs = win.Where(p => p.R.Cur is not null).Select(p => p.R.Cur!.Value).ToList();
        var src = win.Where(p => p.R.Source is not null).GroupBy(p => p.R.Source!)
                     .OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
        var gaps = win.Where(p => p.R.Tj is not null && p.R.Tr is not null)
                      .Select(p => Math.Abs(p.R.Tj!.Value - p.R.Tr!.Value)).ToList();

        // 归零之后：夹套还往同一个方向走了多少、极值在第几秒
        double? overrun = null, peak = null, peakAt = null;
        if (tr.StopAt is { } sa)
        {
            var atStop = At(series, sa);
            var after = series.Where(x => x.T >= sa && x.T <= sa + TimeSpan.FromSeconds(s.AfterSeconds)).ToList();
            if (atStop is { } vs && after.Count > 0)
            {
                var best = after.OrderByDescending(x => sign * x.V).First();
                peak = best.V;
                peakAt = (best.T - sa).TotalSeconds;
                overrun = Math.Max(0, sign * (best.V - vs));
            }
        }

        string verdict;
        var notes = new List<string>();
        if (toPass is not null) verdict = "合格";
        else if (ranFull && change is not null) verdict = "不合格";
        else verdict = "判不了";
        if (!ranFull)
            notes.Add(tr.EarlyStop is { } es ? $"满功率只开了 {ran:0} s：{es}"
                      : abortNote is not null ? $"满功率只开了 {ran:0} s：中止（{abortNote}）"
                      : $"满功率只开了 {ran:0} s");
        if (start is null || endV is null) notes.Add("起点或终点夹套没有读数");
        if (tr.Limited is null) notes.Add("没读到最大输出占空比，判不了输出开没开满");
        else if (fullPct is { } fp && fp < 90) notes.Add($"输出只有 {fp:0} % 的时间开满（最大输出占空比 {tr.Limited:0.#} %）——这一项不算满功率");

        return new RateMetrics
        {
            Verdict = verdict,
            Start = start,
            End = endV,
            RanSeconds = Math.Round(ran, 1),
            Change = change,
            PassK = pass,
            TimeToPass = toPass,
            Fastest10s = fastest,
            FullOutPercent = fullPct,
            MeanOut = meanOut,
            MeanCur = curs.Count > 0 ? curs.Average() : null,
            Source = src,
            TrStart = Near(tr, t0.Value)?.Tr,
            TrEnd = Near(tr, end)?.Tr,
            MaxJacketReactorGap = gaps.Count > 0 ? gaps.Max() : null,
            Overrun = overrun,
            AfterPeak = peak,
            AfterPeakSeconds = peakAt,
            ConnectSeconds = connect,
            Note = string.Join("；", notes)
        };
    }
}
