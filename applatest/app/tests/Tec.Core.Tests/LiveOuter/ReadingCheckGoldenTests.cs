using System.Text.Json;
using TecControl.Core.Control;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 【本地改动】（2026-11-17，第 1 步复核之后用户选的 S1「读数对不上就停控」）C# <see cref="ReadingCheck"/> 逐拍对 Python 定稿（照第 0 步 LiveOuter 对拍的做法）。
/// 夹具：<c>LiveOuter/golden/s1/*.csv.gz</c>（gzip 原件、省体积）+ <c>manifest.json</c>，由 Python 参考 <c>LiveOuter/reference/s1/reading_check.py</c>
/// 经 <c>make_fixtures.py</c> 生成（定稿目录 scratchpad step1/safety/spec/，2026-11-17；twin_ / bench_ 那 9 场要 scratchpad 里的试算台才重生得出来，
/// synth_edges 只要 reading_check.py；<c>verify_fixtures.py</c> 是这条测试的 Python 样子，读的是没压缩的 csv）。
/// 10 场共 28 496 拍：
/// · twin_frozen30 / 55 / 70：<see cref="LiveOuterClosedLoopTests"/> 那个 D1 闭环的 Python 逐行移植（60 ℃ 稳 10 min 后读数冻住）；
/// · twin_ramp_m04：同一闭环、吸热速率每小时涨 0.4 K/min，310 min（只有这一场长过 5500 拍，走得到「历史删前面一截」那一支）；
/// · bench_*：试算台一次加冷料 15 K、分 4 次加料、冻住 + 每 180 s 读不到一拍、探头提出液面、插错工位；
/// · synth_edges：人造边角（断档 &gt; gap、±inf、τ̂ 跳变、平在最低处、两拍隔 70 s 重摆、目标改变、读数在目标以上冻住）。
/// 判据全程只记不停（闭环不因判到而停控），判到以后的拍也在里面：核判到之后内部量照样一致。
/// 比法（定稿 manifest 写的）：verdict / which / valid / heat 逐拍相同；其余浮点 |C# − Py| ≤ 1e-9 + 1e-9·|Py|，NaN 对 NaN。
/// 参数：manifest 里的 Params（x0 0.02、r0 0.25、d0 8、τc 2850、T̂w 170、A 回看 950、窗 30、gap 30、重摆 60）= C# 出厂缺省 + gap 30。
/// </summary>
public class ReadingCheckGoldenTests
{
    /// <summary>10 场和拍数（钉住：夹具少了 / 换了测试要红）、第一次 stop / alarm 在哪一拍（manifest 里的，下标从 0 起；-1 = 没有）。</summary>
    public static readonly (string Name, int Beats, int FirstStop, string StopWhich, int FirstAlarm)[] Runs =
    [
        ("twin_frozen30", 2100, 326, "B", -1),
        ("twin_frozen55", 2100, 451, "A", -1),
        ("twin_frozen70", 2100, -1, "", 473),
        ("twin_ramp_m04", 9300, -1, "", -1),
        ("bench_charge60_15K", 2401, -1, "", -1),
        ("bench_portion40_4x3K", 2401, -1, "", -1),
        ("bench_nanfreeze60_m1", 2401, 901, "A", -1),
        ("bench_lift60_180", 2401, 760, "B", -1),
        ("bench_swap60_40", 2401, 634, "B", -1),
        ("synth_edges", 891, 237, "B", 672),
    ];

    public static IEnumerable<object[]> RunNames() => Runs.Select(r => new object[] { r.Name });

    private static string S1Dir => Path.Combine(LiveOuterFixtures.Dir, "golden", "s1");

    private static readonly string[] Floats = ["y30", "ra", "a_ts", "t_ref", "y_ref", "d", "t_pk", "t_seg", "rise", "r_pk", "u"];

    private static double Get(in ReadingCheckBeat b, string col) => col switch
    {
        "y30" => b.Y30, "ra" => b.Ra, "a_ts" => b.ATs, "t_ref" => b.TRef, "y_ref" => b.YRef, "d" => b.D, "t_pk" => b.TPk,
        "t_seg" => b.TSeg, "rise" => b.Rise, "r_pk" => b.RPk, "u" => b.U,
        _ => throw new ArgumentOutOfRangeException(nameof(col), col, null),
    };

    private static string Verdict(ReadingVerdict v) => v switch
    {
        ReadingVerdict.Stop => "stop",
        ReadingVerdict.Alarm => "alarm",
        _ => "ok",
    };

    [Fact]
    public void 夹具清单_十场都在_拍数和第一次判到的拍跟manifest一致_参数就是出厂缺省加gap30()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(S1Dir, "manifest.json")));
        var root = doc.RootElement;
        var p = root.GetProperty("params");
        var lp = new LiveOuterParams();
        Assert.Equal(ReadingCheck.X0, p.GetProperty("x0").GetDouble());
        Assert.Equal(ReadingCheck.R0, p.GetProperty("r0").GetDouble());
        Assert.Equal(ReadingCheck.D0, p.GetProperty("d0").GetDouble());
        Assert.Equal(ReadingCheck.WinS, p.GetProperty("win").GetDouble());
        Assert.Equal(lp.TauHi * lp.Tau0, p.GetProperty("tau_c").GetDouble());
        Assert.Equal(lp.Tw, p.GetProperty("tw").GetDouble());
        Assert.Equal(lp.Tau0, p.GetProperty("a_win").GetDouble());
        Assert.Equal(LiveOuterShadow.ReseatGapS, p.GetProperty("reseat").GetDouble());
        Assert.Equal(30.0, p.GetProperty("gap").GetDouble());
        Assert.Equal(2850.0, new ReadingCheck(null, 30).TauC);

        var runs = root.GetProperty("runs").EnumerateArray().ToArray();
        Assert.Equal(Runs.Length, runs.Length);
        for (var i = 0; i < Runs.Length; i++)
        {
            var (name, beats, firstStop, which, firstAlarm) = Runs[i];
            var m = runs[i];
            Assert.Equal(name, m.GetProperty("name").GetString());
            Assert.Equal(beats, m.GetProperty("beats").GetInt32());
            int Idx(string k) => m.GetProperty(k).ValueKind == JsonValueKind.Null ? -1 : m.GetProperty(k).GetInt32();
            Assert.Equal(firstStop, Idx("first_stop_beat"));
            Assert.Equal(firstAlarm, Idx("first_alarm_beat"));
            if (firstStop >= 0) Assert.Equal(which, m.GetProperty("first_stop_which").GetString());
            Assert.True(File.Exists(Path.Combine(S1Dir, name + ".csv.gz")), name + ".csv.gz 不在");
        }
        Assert.Equal(28496, Runs.Sum(r => r.Beats));
    }

    [Theory]
    [MemberData(nameof(RunNames))]
    public void 逐拍对Python定稿_verdict_which_valid_heat逐拍相同_浮点差在1e9以内(string name)
    {
        var spec = Runs.Single(r => r.Name == name);
        var t = LiveOuterFixtures.Load(Path.Combine(S1Dir, name + ".csv.gz"));
        Assert.Equal(spec.Beats, t.Rows.Length);
        int cT = t.Col("t"), cTr = t.Col("tr"), cSp = t.Col("sp"), cW = t.Col("wall"), cTau = t.Col("tau");
        int cV = t.Col("verdict"), cWh = t.Col("which"), cOk = t.Col("valid"), cHeat = t.Col("heat");
        var cF = Floats.Select(t.Col).ToArray();
        var chk = new ReadingCheck(null, 30);
        var bad = new List<string>();
        double worst = 0;
        int firstStop = -1, firstAlarm = -1;
        for (var k = 0; k < t.Rows.Length; k++)
        {
            var f = t.Rows[k];
            var n = LiveOuterFixtures.Num;
            var b = chk.Step(n(f[cT]), n(f[cTr]), n(f[cSp]), n(f[cW]), n(f[cTau]));
            if (firstStop < 0 && b.Verdict == ReadingVerdict.Stop) firstStop = k;
            if (firstAlarm < 0 && b.Verdict == ReadingVerdict.Alarm) firstAlarm = k;
            if (Verdict(b.Verdict) != f[cV] || b.Which != f[cWh] || (b.Valid ? "1" : "0") != f[cOk] || (b.Heat ? "1" : "0") != f[cHeat])
                bad.Add($"第 {k} 拍（t = {f[cT]}）：C# {Verdict(b.Verdict)}/{b.Which}/{b.Valid}/{b.Heat}，Python {f[cV]}/{f[cWh]}/{f[cOk]}/{f[cHeat]}");
            for (var j = 0; j < Floats.Length; j++)
            {
                var want = n(f[cF[j]]);
                var got = Get(b, Floats[j]);
                bool ok;
                if (double.IsNaN(want)) ok = double.IsNaN(got);
                else
                {
                    var diff = Math.Abs(got - want);
                    ok = diff <= 1e-9 + 1e-9 * Math.Abs(want);          // 写成「≤」：got 是 NaN 也算没对上
                    if (ok) worst = Math.Max(worst, diff);
                }
                if (!ok) bad.Add($"第 {k} 拍（t = {f[cT]}）列 {Floats[j]}：Python {want:R}，C# {got:R}");
            }
            if (bad.Count > 20) break;
        }
        Assert.True(bad.Count == 0, $"{name} 对不上 {bad.Count} 处（最多列 20 处）：\n" + string.Join("\n", bad.Take(20)));
        Assert.Equal(spec.FirstStop, firstStop);
        Assert.Equal(spec.FirstAlarm, firstAlarm);
        Assert.True(worst <= 1e-9, $"最大差 {worst:E2}");
    }

    [Fact]
    public void 读数不是有限数只记釜壁不判_参考留着_断档超过gap才重起_两拍隔60秒以上整个从头来()
    {
        // 手摆：读数冻在 50、目标 60、Ŵ 每拍 +0.1（夹套一直往上推）——A 一直累；中间插 NaN / ±inf，参考不重起
        var chk = new ReadingCheck(null, 30);
        double t = 0, w = 65;
        ReadingCheckBeat b = default;
        for (var k = 0; k < 10; k++) { t += 2; w += 0.1; b = chk.Step(t, 50, 60, w, 950); }
        var tRef = b.TRef;
        Assert.True(b.Valid && b.Ra > 0);
        foreach (var bad in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            t += 2; w += 0.1;
            var x = chk.Step(t, bad, 60, w, 950);
            Assert.False(x.Valid);
            Assert.Equal(ReadingVerdict.Ok, x.Verdict);
            Assert.Equal(tRef, x.TRef);                                 // 参考留着
        }
        t += 2; w += 0.1;
        b = chk.Step(t, 50, 60, w, 950);
        Assert.Equal(tRef, b.TRef);                                     // 断了 6 s（≤ 30）：接着算
        Assert.True(b.Ra > 0);

        // 断 40 s（> gap 30）：A 的参考从这一拍重起
        for (var k = 0; k < 20; k++) { t += 2; w += 0.1; chk.Step(t, double.NaN, 60, w, 950); }
        t += 2; w += 0.1;
        b = chk.Step(t, 50, 60, w, 950);
        Assert.Equal(t, b.TRef);
        Assert.Equal(0.0, b.Ra);
        Assert.True(double.IsNaN(b.ATs));

        // 两拍隔 70 s（> 60 s，影子断档重摆过）：整个从头来——Ŵ 跳了 5 K 不算「夹套推了」
        t += 2; w += 0.1;
        chk.Step(t, 50, 60, w, 950);
        t += 70; w += 5;
        b = chk.Step(t, 50, 60, w, 950);
        Assert.Equal(t, b.TRef);
        t += 2;
        b = chk.Step(t, 50, 60, w, 950);
        Assert.Equal(0.0, b.Ra, 12);                                    // 重摆后 Ŵ 没动：最慢的釜一点没该挪
    }
}
