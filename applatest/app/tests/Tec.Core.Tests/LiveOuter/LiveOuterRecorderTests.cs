using System.Text;
using Tec.Driver.Abi;
using Tec.Drivers.Rd105;
using TecControl.Core.Control;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 逐拍记录（Rd105LoopRecorder）在外环实时算第 0 步加的 8 列：
/// · 原 25 列的列名、列号、每一格的写法一个不变（拿真机 2026-11-10 记录里旧程序写的一行当样子逐格比）；
/// · 新列接在行尾：algo 恒 0；影子有读数就 7 个数，没有就 7 个空格子；
/// · extra / note 带逗号、引号时照样拆得对（认引号的拆法）；按逗号硬拆的从行尾倒着数 8 格也拿得到新列；
/// · 「停止」行：note 还在它自己那一列，后面补齐空列，每一行的列数都等于表头；
/// · 走真会话：釜内有读数时记录里新列有数，没喂釜内（影子起不了算）就留空。
/// </summary>
public class LiveOuterRecorderTests
{
    /// <summary>加列之前的表头（提交 9ea0934 的 Rd105LoopRecorder.Header，原样抄在这里钉住）。</summary>
    private const string OldHeader =
        "time,elapsed_s,kind,setpoint,inner_sp,tr,tj,duty_written,duty_applied,p,i,d,outer_p,outer_i,outer_d,outer_fin,boost,v_m,v_star,brake_f,actuator,hold_s,cycle_ms,extra,note";

    private const string NewColumns = "algo,lo_isp,lo_b,lo_xr,lo_wall,lo_tau,lo_tau2,lo_alarm";

    /// <summary>认引号的 CSV 拆行：格子整个用双引号包着时里面可以有逗号，两个引号 = 一个引号。</summary>
    private static string[] SplitCsv(string line)
    {
        var cells = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else sb.Append(ch);
            }
            else if (ch == '"' && sb.Length == 0) quoted = true;
            else if (ch == ',') { cells.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        cells.Add(sb.ToString());
        return cells.ToArray();
    }

    [Fact]
    public void 表头_原25列一字不差_行尾多8列()
    {
        Assert.Equal(OldHeader + "," + NewColumns, Rd105LoopRecorder.Header);
        var cols = Rd105LoopRecorder.Header.Split(',');
        Assert.Equal(33, cols.Length);
        Assert.Equal(OldHeader.Split(','), cols[..25]);
        Assert.Equal("extra", cols[23]);
        Assert.Equal("note", cols[24]);
    }

    [Fact]
    public void 逐拍行_原25列逐格不变_新8列接在行尾_note带逗号引号也拆得对_停止行补齐空列()
    {
        using var dir = Rd105HostControl.UseGainsDir(Path.Combine(Path.GetTempPath(), "tec-lo-rec-" + Guid.NewGuid().ToString("N")));
        var logs = new List<string>();
        var rec = new Rd105LoopRecorder("R1", 1, (l, t) => logs.Add($"{l} {t}"));
        rec.Start("釜内", 60, "尽快");
        var path = rec.Path!;

        // 真机 2026-11-10 10:47:26 那一拍（旧程序写的，第 3 格起）：
        //   Tr,60,74.667,41.84,42.319,-90,-15,630.065,0,0.457,45.375,4.284,-0,0,,,,,加热棒+TEC双向,,125,电加热·双向挡·冷水机开,
        // 用同样的数造一拍。OuterD 给 −0.0001：三位小数写出来就是那个「-0」
        var info = new ChannelCycleInfo(true, false, 0, SetpointC: 60, MeasuredC: 42.319, DutyPercent: -90, PTerm: 630.065, ITerm: 0, DTerm: 0.457)
        {
            Cascade = true, InnerSetpointC = 74.667, OuterMeasuredC = 41.84, AppliedDutyPercent = -15,
            OuterP = 45.375, OuterI = 4.284, OuterD = -0.0001, OuterFinishC = 0,
        };
        var old = "Tr,60,74.667,41.84,42.319,-90,-15,630.065,0,0.457,45.375,4.284,-0,0,,,,,加热棒+TEC双向,,125,电加热·双向挡·冷水机开".Split(',');
        Assert.Equal(22, old.Length);                                  // 第 2…23 列（kind … extra）

        // ① 影子没有读数（没起算 / 停了）
        rec.Write(info, 41.84, "加热棒+TEC双向", 125, "电加热·双向挡·冷水机开");
        // ② 有读数
        var read = new LiveOuterReadout(JacketSetpointC: 65.4321, BHat: 5.8129, THat: 41.8371, Wall: 47.6504, Tau: 987.6543, TauEkf: 912.3456, AlarmOn: false);
        rec.Write(info with { LiveOuter = read }, 41.84, "加热棒+TEC双向", 125, "电加热·双向挡·冷水机开");
        // ③ note、extra 里有逗号和引号；报警条件成立；夹套目标是负的
        rec.Note("他说\"先开水\",然后停了");
        rec.Write(info with { LiveOuter = read with { JacketSetpointC = -50, AlarmOn = true } }, 41.84, "加热棒+TEC双向", 125, "电加热,双向挡");
        // ④ 读数里混进了不是有限数的（不该有；有也只是那一格留空）
        rec.Write(info with { LiveOuter = read with { BHat = double.NaN, Tau = double.PositiveInfinity } }, 41.84, "加热棒+TEC双向", 125, null);
        rec.Stop("操作人停控");

        var lines = File.ReadAllLines(path);
        Assert.Equal(6, lines.Length);                                 // 表头 + 4 拍 + 停止
        Assert.Equal(Rd105LoopRecorder.Header, lines[0].TrimStart('﻿'));
        var rows = lines.Skip(1).Select(SplitCsv).ToArray();
        Assert.All(rows, r => Assert.Equal(33, r.Length));             // 每一行的列数都等于表头（认引号地拆）

        // ①：第 2…23 列跟旧程序写的那一行逐格相同；note 是「开始」那句；新列 = 0 + 7 个空
        Assert.Equal(old, rows[0][2..24]);
        Assert.Equal("开始：釜内 60 ℃（尽快）", rows[0][24]);
        Assert.Equal(new[] { "0", "", "", "", "", "", "", "" }, rows[0][25..]);
        // 这一行没有带引号的格子：整行除了时间两格，就是旧程序会写的那一行后面接上 ",0,,,,,,,"
        Assert.EndsWith("," + string.Join(",", old) + ",开始：釜内 60 ℃（尽快）,0,,,,,,,", lines[1]);

        // ②：前 25 列跟 ① 一样（note 空了）；新列是影子的 7 个数，三位小数
        Assert.Equal(old, rows[1][2..24]);
        Assert.Equal("", rows[1][24]);
        Assert.Equal(new[] { "0", "65.432", "5.813", "41.837", "47.65", "987.654", "912.346", "0" }, rows[1][25..]);
        Assert.EndsWith("," + string.Join(",", old) + ",,0,65.432,5.813,41.837,47.65,987.654,912.346,0", lines[2]);

        // ③：带逗号 / 引号的 extra、note 拆回原话；新列不受影响
        Assert.Equal(old[..21], rows[2][2..23]);
        Assert.Equal("电加热,双向挡", rows[2][23]);
        Assert.Equal("他说\"先开水\",然后停了", rows[2][24]);
        Assert.Equal(new[] { "0", "-50", "5.813", "41.837", "47.65", "987.654", "912.346", "1" }, rows[2][25..]);
        // 按逗号硬拆会多出格子（note 里有逗号），但新 8 列全是数字：从行尾倒着数照样拿得对
        var naive = lines[3].Split(',');
        Assert.True(naive.Length > 33);
        Assert.Equal(rows[2][25..], naive[^8..]);

        // ④：不是有限数的那一格留空，别的照写
        Assert.Equal(new[] { "0", "65.432", "", "41.837", "47.65", "", "912.346", "0" }, rows[3][25..]);
        Assert.Equal("", rows[3][23]);

        // 停止行：只有时间、秒数、note 有内容；note 还在第 24 列（下标），不是最后一列
        var stop = rows[4];
        Assert.NotEqual("", stop[0]);
        Assert.NotEqual("", stop[1]);
        Assert.Equal("停止：操作人停控", stop[24]);
        Assert.All(stop[2..24], cell => Assert.Equal("", cell));
        Assert.All(stop[25..], cell => Assert.Equal("", cell));
        Assert.EndsWith(",停止：操作人停控,,,,,,,,", lines[5]);
    }

    // ── 走真会话 ─────────────────────────────────────────────────────

    private static ParameterSet Conn() => ParameterSet.Of((Rd105TecDriver.FieldPeriod, 200d));

    private static DriverContext Ctx(List<string> logs) => new()
    {
        InstanceId = "R1",
        ChannelNumbers = new[] { 1, 2 },
        Config = ParameterSet.Of((Rd105TecDriver.FieldControl, Rd105TecDriver.ControlHost)),
        Simulated = false,
        TimeScale = 1,
        Clock = () => DateTimeOffset.Now,
        Log = (l, t) => { lock (logs) logs.Add($"{l} {t}"); }
    };

    private static async Task WaitUntil(Func<bool> cond, int ms, string what)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond())
        {
            Assert.True(DateTime.UtcNow < deadline, "等太久：" + what);
            await Task.Delay(25);
        }
    }

    [Fact]
    public async Task 真会话_釜内有读数时记录里新列有数_没喂釜内的那一路留空_原来的列照旧()
    {
        using var dir = Rd105HostControl.UseGainsDir(Path.Combine(Path.GetTempPath(), "tec-lo-rec-" + Guid.NewGuid().ToString("N")));
        var dev = new FakeRd105Device { Thermal = true, GainCPerSec = 1.0 };
        dev.Set(1, "LIMITED", 90); dev.Set(2, "LIMITED", 90);
        var drv = new Rd105TecDriver { LinkFactory = _ => new Rd105Link(dev) };
        var logs = new List<string>();
        await using var s = await drv.OpenAsync(Conn(), Ctx(logs), CancellationToken.None);
        await s.StartAsync(CancellationToken.None);
        var rd = (Rd105Session)s;
        var a = s.CapabilitiesOf(0).OfType<ITemperatureControl>().Single();
        var b = s.CapabilitiesOf(1).OfType<ITemperatureControl>().Single();
        await WaitUntil(() => !double.IsNaN(a.CurrentJacket) && !double.IsNaN(b.CurrentJacket), 3000, "夹套读数");

        // A 路：釜内 20 ℃（有读数），控釜内到 30；B 路：没人喂釜内，控夹套到 30
        rd.TempOf(0).FeedReactor(20.0);
        await Task.Delay(500);                                         // 还没下发目标：影子已经在算了（没开文件，不记）
        await a.SetTargetAsync(new TempTarget(30, TempChannelKind.Reactor), CancellationToken.None);
        await b.SetTargetAsync(new TempTarget(30, TempChannelKind.Jacket), CancellationToken.None);
        var pathA = rd.LoopLogPathOf(0)!;
        var pathB = rd.LoopLogPathOf(1)!;
        await Task.Delay(3000);                                        // 外环周期 2 s：至少跨过影子的一拍
        await a.StopAsync(CancellationToken.None);
        await b.StopAsync(CancellationToken.None);
        lock (logs) Assert.DoesNotContain(logs, l => l.Contains("新外环试算"));     // 影子没出事
        lock (logs) Assert.DoesNotContain(logs, l => l.Contains("轮询异常"));

        var la = File.ReadAllLines(pathA);
        Assert.Equal(Rd105LoopRecorder.Header, la[0].TrimStart('﻿'));
        var ra = la.Skip(1).Select(SplitCsv).ToArray();
        Assert.True(ra.Length > 8, $"A 路只有 {ra.Length} 行");
        Assert.All(ra, r => Assert.Equal(33, r.Length));
        var beatsA = ra[..^1];                                         // 最后一行是「停止」
        // 原来的列照旧
        Assert.All(beatsA, r => { Assert.Equal("Tr", r[2]); Assert.Equal("30", r[3]); Assert.Equal("20", r[5]); Assert.Equal("TEC双向", r[20]); });
        Assert.Contains("开始：釜内 30 ℃（尽快）", ra[0][24]);
        // 新列：algo 恒 0；影子从第一行起就有读数（下发目标之前就起算了）
        double N(string cell) => double.Parse(cell, System.Globalization.CultureInfo.InvariantCulture);
        Assert.All(beatsA, r =>
        {
            Assert.Equal("0", r[25]);
            Assert.InRange(N(r[26]), -50, 200);                        // lo_isp：夹在 −50…200
            Assert.InRange(N(r[27]) - (N(r[29]) - N(r[28])), -0.3, 0.3);   // lo_b ≈ lo_wall − lo_xr（刚起算不久）
            Assert.InRange(N(r[28]), 19.9, 20.1);                      // lo_xr ≈ 釜内 20
            Assert.InRange(N(r[29]), 20, 45);                          // lo_wall：跟着夹套从 25 往上走
            Assert.InRange(N(r[30]), 900, 1000);                       // lo_tau：刚起算，还是出厂值 950 附近
            Assert.InRange(N(r[31]), 380, 2850);                       // lo_tau2：0.4…3 倍
            Assert.Equal("0", r[32]);                                  // lo_alarm：第 0 步恒 0
        });
        // 3 s 里影子至少又算过一拍：新列不是从头到尾同一组数；两拍之间的行重复上一拍的数
        var groups = beatsA.Select(r => string.Join(",", r[26..])).Distinct().Count();
        Assert.InRange(groups, 2, beatsA.Length - 3);
        // 「停止」行
        Assert.Equal("停止：操作人停控", ra[^1][24]);
        Assert.All(ra[^1][25..], cell => Assert.Equal("", cell));

        // B 路：没有釜内读数 → 影子没起算 → 后 7 列空，algo 照写 0；原来的列照旧
        var rb = File.ReadAllLines(pathB).Skip(1).Select(SplitCsv).ToArray();
        Assert.True(rb.Length > 8, $"B 路只有 {rb.Length} 行");
        Assert.All(rb, r => Assert.Equal(33, r.Length));
        Assert.All(rb[..^1], r =>
        {
            Assert.Equal("Tj", r[2]);
            Assert.Equal("30", r[3]);
            Assert.Equal("", r[5]);
            Assert.Equal(new[] { "0", "", "", "", "", "", "", "" }, r[25..]);
        });
    }
}
