using System.Globalization;
using TecControl.Core.Control;
using Xunit;
using Xunit.Abstractions;

namespace Tec.Core.Tests;

/// <summary>
/// LiveOuter（串级外环「按实测实时算」）C# 版对 Python 定稿（LiveOuter/reference/outer_m.py 的类 M）逐拍对拍。
/// 试算成绩是 Python 版跑出来的；这里核的是「同样的读数进去，C# 算出来的是不是同一个数」，不核算法本身好不好。
///
/// 夹具：真机 6 场 9607 拍（现场逐拍记录上开环重放）+ 合成 12 场 3249 拍（覆盖真机场走不到的分支）。
/// 合成场 s12 另核 LiveOuter.PinTarget（不在控时目标是占位的）：Python 定稿没有这个入口，那一场是照它的等价写法（step 之前摆四样状态）生成的。
/// 比的量：Ŵ、T̂、b̂、τ̂、第二路的 τ、K、P上限、g_sp、To′、夹套目标（没夹过的）；合成场另比报警两列。
///
/// 容差（绝对差）：csv 里印 9 位小数的 7 列 ≤ 1e-9，印 6 位小数的 3 列（τ̂、第二路 τ、To′）≤ 1e-6；报警两列必须相等。
/// 为什么是这两个数：csv 的打印取整本身就有半个末位（5e-10 / 5e-7）；C# 的平方写 x * x 而 Python 的 x ** 2 走 pow，
/// 差在 1e-13 量级；所以容差 = 打印取整的 2 倍，留给计算误差的余量是实测的几百倍，而抄错一处通常差 1e-6 以上。
/// 对不上先当移植抄错查（拿 reference/gen_synth_golden.py --debug-dir 出的宽表往前找第一处分叉），不许放宽容差。
/// </summary>
public class LiveOuterGoldenTests(ITestOutputHelper output)
{
    private const double Tol9 = 1e-9;
    private const double Tol6 = 1e-6;

    /// <summary>比哪些列、各自的容差、C# 这边从哪取。u 取 Step 返回的没夹过的那个。</summary>
    private static readonly (string Col, double Tol, Func<LiveOuter, LiveOuterOutput, double> Get)[] Columns =
    [
        ("wall", Tol9, (lo, _) => lo.Wall),
        ("xr", Tol9, (lo, _) => lo.THat),
        ("b", Tol9, (lo, _) => lo.BHat),
        ("tau", Tol6, (lo, _) => lo.Tau),
        ("tau_ekf", Tol6, (lo, _) => lo.TauEkf),
        ("k", Tol9, (lo, _) => lo.K),
        ("p_up", Tol9, (lo, _) => lo.PUp),
        ("g_sp", Tol9, (lo, _) => lo.GSp),
        ("to_eff", Tol6, (lo, _) => lo.ToEff),
        ("u", Tol9, (_, o) => o.U),
    ];

    private sealed record Result(string Name, int Beats, double[] MaxDiff, int Alarms, int NoReading, int Pinned);

    /// <summary>整场重放，逐拍逐列比；第一处超容差就带着「哪一场、第几拍、哪一列、期望 / 实际」失败。</summary>
    private static Result Replay(string name, string path)
    {
        var table = LiveOuterFixtures.Load(path);
        var cols = Columns.Select(c => table.Col(c.Col)).ToArray();
        bool synth = table.Has("alarm");
        int cAlarm = synth ? table.Col("alarm") : -1, cAlarmOn = synth ? table.Col("alarm_on") : -1;
        var lo = new LiveOuter();
        var worst = new double[Columns.Length];
        int k = 0, alarms = 0, noReading = 0, pinned = 0;
        foreach (var beat in LiveOuterFixtures.Beats(table))
        {
            var o = LiveOuterFixtures.Feed(lo, beat);
            for (int j = 0; j < Columns.Length; j++)
            {
                double want = LiveOuterFixtures.Num(beat.Cells[cols[j]]);
                double got = Columns[j].Get(lo, o);
                double diff = Math.Abs(got - want);
                if (!(diff <= Columns[j].Tol))          // 写成「不 ≤」：NaN 也算没对上
                    Assert.Fail(string.Create(CultureInfo.InvariantCulture,
                        $"{name} 第 {k} 拍（t = {beat.Input.T} s）列 {Columns[j].Col}：Python {want:R}，C# {got:R}，差 {diff:E3}（容差 {Columns[j].Tol:E0}）"));
                if (diff > worst[j]) worst[j] = diff;
            }
            if (synth)
            {
                Assert.True((beat.Cells[cAlarm] == "1") == o.AlarmFired, $"{name} 第 {k} 拍：这一拍报不报警跟 Python 不一样（Python {beat.Cells[cAlarm]}）");
                Assert.True((beat.Cells[cAlarmOn] == "1") == o.AlarmOn, $"{name} 第 {k} 拍：报警条件成立与否跟 Python 不一样（Python {beat.Cells[cAlarmOn]}）");
                Assert.Equal(o.AlarmOn, lo.AlarmOn);
                if (o.AlarmFired) alarms++;
            }
            if (double.IsNaN(beat.Input.Tr)) noReading++;
            if (beat.Pin) pinned++;
            Assert.True(lo.IsFinite, $"{name} 第 {k} 拍：内部量出了 NaN / 无穷");
            k++;
        }
        return new Result(name, k, worst, alarms, noReading, pinned);
    }

    private static string Diffs(double[] worst) =>
        string.Join("  ", Columns.Select((c, j) => c.Col + " " + worst[j].ToString("E2", CultureInfo.InvariantCulture)));

    private void Print(Result r) =>
        output.WriteLine($"{r.Name,-20} {r.Beats,5} 拍  最大差：" + Diffs(r.MaxDiff)
            + (r.Alarms > 0 || r.NoReading > 0 ? $"  报警 {r.Alarms} 次  无读数 {r.NoReading} 拍" : ""));

    [Theory]
    [InlineData("20261110-104725")]   // 42 → 60 ℃ 升温带锯齿
    [InlineData("20261109-095506")]   // 19.3 → 50 ℃
    [InlineData("20261106-130619")]   // 66 → 70 ℃ 小台阶 + 长保温
    [InlineData("20261104-085350")]   // 21.6 → −10 ℃ 大降温
    [InlineData("20261104-104405")]   // −2 → 20 ℃，带开水询问
    [InlineData("20261103-104547")]   // 19 → 15 ℃ 小降温
    public void 真机对拍_同样的读数进去逐拍跟Python版对得上(string name)
    {
        var want = LiveOuterFixtures.Real.Single(x => x.Name == name);
        var r = Replay(name, LiveOuterFixtures.RealPath(name));
        Print(r);
        Assert.Equal(want.Beats, r.Beats);
    }

    [Theory]
    [InlineData("s01_step_plain")]      // 没在控 → 大台阶（贴着 P上限）→ 目标改到读数下面
    [InlineData("s02_ramp")]            // 升坡、降坡（终点、坡速）和几种边角给法
    [InlineData("s03_retarget")]        // 目标中途改：大改小改，把握放开
    [InlineData("s04_tau_hi")]          // τ̂ 往上顶（第二路顶到 3 倍、回归值夹在上限）
    [InlineData("s05_tau_lo_reseed")]   // τ̂ 往下贴（第二路到 0.4 倍、K 贴 1）；第二次 Reset；连续 120 s 没读数
    [InlineData("s06_glitch")]          // 单个坏点、连坏两个点
    [InlineData("s07_loss")]            // 无读数若干拍（含起算后第一拍就没读数）
    [InlineData("s08_far")]             // 读数离模型远：观测器变速到 20 s；夹套目标冲出 −50…200
    [InlineData("s09_alarm_combo")]     // 报警条件各种组合：真报的、差一个条件不报的、重新武装
    [InlineData("s10_exo_dt")]          // 放热 + 拍长不齐；报警报在没读数的拍上
    [InlineData("s11_dt_mix")]          // 拍长 0.5 / 1 / 2 / 3 s 混着
    [InlineData("s12_idle_pin")]        // 占位目标（PinTarget）：没在控 → 在控 → 过渡一半停控（坏点、没读数、加冷料）→ 再下发目标
    public void 合成对拍_真机走不到的分支逐拍跟Python版对得上(string name)
    {
        var want = LiveOuterFixtures.Synth.Single(x => x.Name == name);
        var r = Replay(name, LiveOuterFixtures.SynthPath(name));
        Print(r);
        Assert.Equal(want.Beats, r.Beats);
    }

    [Fact]
    public void 对拍夹具一场不多一场不少_总拍数对得上()
    {
        // 上面两个 Theory 是按名字点的：仓库里多放了一场没人点名、或者少了一场，都要在这里红，不能悄悄少比。
        static string[] Names(string sub, string ext) =>
            Directory.GetFiles(Path.Combine(LiveOuterFixtures.Dir, "golden", sub))
                .Select(Path.GetFileName).Where(n => n!.EndsWith(ext, StringComparison.Ordinal))
                .Select(n => n![..^ext.Length]).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.Equal(LiveOuterFixtures.Real.Select(x => x.Name).OrderBy(n => n, StringComparer.Ordinal), Names("real", ".csv.gz"));
        Assert.Equal(LiveOuterFixtures.Synth.Select(x => x.Name).OrderBy(n => n, StringComparer.Ordinal), Names("synth", ".csv"));

        // 汇总一遍（也方便从测试输出里直接抄「对了多少拍、最大差多少」）
        var all = LiveOuterFixtures.Real.Select(x => Replay(x.Name, LiveOuterFixtures.RealPath(x.Name)))
            .Concat(LiveOuterFixtures.Synth.Select(x => Replay(x.Name, LiveOuterFixtures.SynthPath(x.Name)))).ToList();
        foreach (var r in all) Print(r);
        int real = all.Take(LiveOuterFixtures.Real.Length).Sum(r => r.Beats);
        int synth = all.Skip(LiveOuterFixtures.Real.Length).Sum(r => r.Beats);
        var worst = Columns.Select((_, j) => all.Max(r => r.MaxDiff[j])).ToArray();
        output.WriteLine($"合计：真机 {LiveOuterFixtures.Real.Length} 场 {real} 拍 + 合成 {LiveOuterFixtures.Synth.Length} 场 {synth} 拍 = {real + synth} 拍；各列最大差："
            + Diffs(worst));
        Assert.Equal(9607, real);
        Assert.Equal(3249, synth);
        // 合成场要真走到了它该走的地方（夹具被换成不痛不痒的输入时这里红）
        Assert.Equal(8, all.Single(r => r.Name == "s09_alarm_combo").Alarms);
        Assert.Equal(1, all.Single(r => r.Name == "s10_exo_dt").Alarms);
        Assert.Equal(40, all.Single(r => r.Name == "s05_tau_lo_reseed").NoReading);
        Assert.Equal(25, all.Single(r => r.Name == "s07_loss").NoReading);
        Assert.Equal(10, all.Single(r => r.Name == "s10_exo_dt").NoReading);
        Assert.Equal(118, all.Single(r => r.Name == "s12_idle_pin").Pinned);       // 起算前后 8 拍 + 停控那 110 拍
        Assert.Equal(2, all.Single(r => r.Name == "s12_idle_pin").NoReading);
        Assert.Equal(0, all.Where(r => r.Name != "s12_idle_pin").Sum(r => r.Pinned));
    }
}
