using System.Globalization;
using System.Text.Json;
using TecControl.Core.Control;
using Xunit;
using Xunit.Abstractions;

namespace Tec.Core.Tests;

/// <summary>
/// LiveOuter 对 Python 定稿的白盒对拍：逐拍对拍（LiveOuterGoldenTests）走不到的那几支。
/// 定稿参数下任何喂得出来的输入都到不了这几支，所以把内部状态直接摆好再走一整拍，拍前拍后的全部内部量跟 Python 比：
/// 第二路相关系数夹 ±0.999、P₂₂ 贴下限、θ 正好在边上也清相关、回归权重和太小提前返回、ρ = 0 时升温方向不给比例、中值缓冲里两个不一样的数取大的。
/// 向量由 reference/gen_whitebox_steps.py 生成（每个都用插桩确认过走到了点名的那一支）。
/// </summary>
public class LiveOuterWhiteboxTests(ITestOutputHelper output)
{
    private static double D(JsonElement e, string name) =>
        double.Parse(e.GetProperty(name).GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static LiveOuterState State(JsonElement e) => new()
    {
        Started = e.GetProperty("Started").GetBoolean(),
        Tau = D(e, "Tau"), K = D(e, "K"), EpsLp = D(e, "EpsLp"), ToEff = D(e, "ToEff"), GSp = D(e, "GSp"), FullS = D(e, "FullS"),
        AlarmOn = e.GetProperty("AlarmOn").GetBoolean(),
        HasSpfPrev = e.GetProperty("HasSpfPrev").GetBoolean(), SpfPrev = D(e, "SpfPrev"), U = D(e, "U"),
        Wall = D(e, "Wall"), WallPrev = D(e, "WallPrev"), WallRate = D(e, "WallRate"), Xr = D(e, "Xr"), B = D(e, "B"), Ref = D(e, "Ref"),
        MedN = e.GetProperty("MedN").GetInt32(), M0 = D(e, "M0"), M1 = D(e, "M1"), M2 = D(e, "M2"),
        ET = D(e, "ET"), EB = D(e, "EB"), Th = D(e, "Th"), TauE = D(e, "TauE"),
        P00 = D(e, "P00"), P01 = D(e, "P01"), P02 = D(e, "P02"), P11 = D(e, "P11"), P12 = D(e, "P12"), P22 = D(e, "P22"),
        F1 = D(e, "F1"), F2 = D(e, "F2"), G1 = D(e, "G1"), G2 = D(e, "G2"), Tr0 = D(e, "Tr0"),
        S0 = D(e, "S0"), S1 = D(e, "S1"), S2 = D(e, "S2"), S3 = D(e, "S3"), S4 = D(e, "S4"), S5 = D(e, "S5"),
    };

    /// <summary>快照里的每个数（中值缓冲只列用到的槽：没用到的槽 Python 那边没有）。</summary>
    private static IEnumerable<(string Name, double V)> Numbers(LiveOuterState s)
    {
        yield return ("Tau", s.Tau); yield return ("K", s.K); yield return ("EpsLp", s.EpsLp); yield return ("ToEff", s.ToEff);
        yield return ("GSp", s.GSp); yield return ("FullS", s.FullS); yield return ("SpfPrev", s.SpfPrev); yield return ("U", s.U);
        yield return ("Wall", s.Wall); yield return ("WallPrev", s.WallPrev); yield return ("WallRate", s.WallRate);
        yield return ("Xr", s.Xr); yield return ("B", s.B); yield return ("Ref", s.Ref);
        if (s.MedN > 0) yield return ("M0", s.M0);
        if (s.MedN > 1) yield return ("M1", s.M1);
        if (s.MedN > 2) yield return ("M2", s.M2);
        yield return ("ET", s.ET); yield return ("EB", s.EB); yield return ("Th", s.Th); yield return ("TauE", s.TauE);
        yield return ("P00", s.P00); yield return ("P01", s.P01); yield return ("P02", s.P02);
        yield return ("P11", s.P11); yield return ("P12", s.P12); yield return ("P22", s.P22);
        yield return ("F1", s.F1); yield return ("F2", s.F2); yield return ("G1", s.G1); yield return ("G2", s.G2); yield return ("Tr0", s.Tr0);
        yield return ("S0", s.S0); yield return ("S1", s.S1); yield return ("S2", s.S2);
        yield return ("S3", s.S3); yield return ("S4", s.S4); yield return ("S5", s.S5);
    }

    /// <summary>
    /// 两个数算不算一样：相对差 ≤ 1e-12（向量里的数是 17 位有效数字的全精度，不是印 9 位小数的，所以按相对差比；
    /// 两边的差只可能来自平方的写法和数学库末位，是 1e-16 量级）。这里的量小到 1e-18（P₂₂ 的下限），绝对容差没有意义。
    /// </summary>
    private static bool Same(double want, double got) =>
        want == got || Math.Abs(want - got) <= 1e-12 * Math.Max(Math.Abs(want), Math.Abs(got));

    [Fact]
    public void 白盒对拍_逐拍对拍走不到的分支_拍前拍后全部内部量跟Python版对得上()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(LiveOuterFixtures.WhiteboxPath));
        var hit = new List<string>();
        double worstRel = 0;
        int n = 0;
        foreach (var v in doc.RootElement.EnumerateArray())
        {
            string name = v.GetProperty("name").GetString()!;
            var prm = new LiveOuterParams();
            if (v.GetProperty("params").TryGetProperty("Rho", out _)) prm = prm with { Rho = D(v.GetProperty("params"), "Rho") };
            var first = v.GetProperty("first");
            var inp = v.GetProperty("input");

            // 标准的第一拍（60 s 窗两边都是这一拍留下的两笔）→ 摆状态 → 走一整拍
            var lo = new LiveOuter(prm);
            var i0 = new LiveOuterInput { T = D(first, "t"), Dt = 2.0, Sp = D(first, "sp"), Tr = D(first, "tr"), Tj = D(first, "tj"), OutputOn = true };
            lo.Reset(i0);
            lo.Step(i0, first: true);
            var pre = State(v.GetProperty("pre"));
            lo.ImportState(pre);
            Assert.Equal(pre, lo.ExportState());          // 摆进去再读出来一位不差
            var i1 = new LiveOuterInput
            {
                T = D(inp, "t"), Dt = D(inp, "dt"), Sp = D(inp, "sp"),
                Tr = inp.GetProperty("tr").ValueKind == JsonValueKind.Null ? double.NaN : D(inp, "tr"),
                Tj = D(inp, "tj"),
                TecSide = inp.GetProperty("side").GetString() == "C",
                InnerSatLow = inp.GetProperty("inner_sat_low").GetBoolean(),
                TecWithheld = inp.GetProperty("tec_withheld").GetBoolean(),
                OutputOn = inp.GetProperty("output_on").GetBoolean(),
            };
            var o = lo.Step(i1);

            var want = State(v.GetProperty("post"));
            var got = lo.ExportState();
            Assert.True(want.MedN == got.MedN && want.AlarmOn == got.AlarmOn && want.HasSpfPrev == got.HasSpfPrev && got.Started, name + "：计数 / 开关量不一样");
            foreach (var ((field, w), (_, g)) in Numbers(want).Zip(Numbers(got)))
            {
                Assert.True(Same(w, g), string.Create(CultureInfo.InvariantCulture, $"{name}：{field} Python {w:R}，C# {g:R}"));
                if (w != g) worstRel = Math.Max(worstRel, Math.Abs(w - g) / Math.Max(Math.Abs(w), Math.Abs(g)));
            }
            Assert.True(Same(D(v, "u"), o.U), string.Create(CultureInfo.InvariantCulture, $"{name}：u Python {D(v, "u"):R}，C# {o.U:R}"));
            Assert.Equal(v.GetProperty("alarm").GetBoolean(), o.AlarmFired);
            hit.AddRange(v.GetProperty("branch").EnumerateArray().Select(b => b.GetString()!));
            n++;
        }
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{n} 个向量；跟 Python 的最大相对差 {worstRel:E2}"));

        // 向量文件被换掉 / 少了哪一支，这里红
        Assert.Equal(17, n);
        Assert.Equal(8, hit.Count(b => b.Contains("相关系数夹到")));
        Assert.Contains(hit, b => b.Contains("θ 顶下边"));
        Assert.Contains(hit, b => b.Contains("θ 顶上边"));
        Assert.DoesNotContain(hit, b => b.Contains("越过"));          // 那两个向量 θ 是正好在边上，不是越过去的
        Assert.Contains(hit, b => b.Contains("相关系数夹到") && b.Contains('正'));
        Assert.Contains(hit, b => b.Contains("相关系数夹到") && b.Contains('负'));
        Assert.Contains(hit, b => b.Contains("对角线贴下限"));
        Assert.Contains(hit, b => b.Contains("权重和太小"));
        Assert.Equal(2, hit.Count(b => b.Contains("P上限 ≤ 1e-9")));
        Assert.Equal(2, hit.Count(b => b.Contains("缓冲里 2 个数")));
    }
}
