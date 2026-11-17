using System.Reflection;
using System.Text;
using Tec.Driver.Abi;
using Tec.Drivers.Rd105;
using TecControl.Core.Control;
using TecControl.Core.Models;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 【本地改动】（2026-11-16，外环改「按实测实时算」第 1 步）走真的 Rd105Session（上位机 PID 方式 + 假 RD105），锁 Rd105 这一层：
/// · 放热报警（主会话 D11）：上升 / 下降沿只在 Rd105Session.OnCycle 判——新外环在控时上升沿日志 warn + 逐拍记录 note 各一次、
///   Rd105TemperatureControl.Exotherm 有那句话；成立着不重报；撤了 info 一次、Exotherm 清掉；再成立再报。不停控。旧外环在控的那一路条件成立也不报；
/// · 逐拍记录 algo 列：新外环在控的行写 1、旧外环写 0；报警那一行 lo_alarm = 1、note 里有那句；
/// · Tr 丢失保持期（D10）：面板那句「保持」换成「按模型推」；
/// · 开控锁定走 Rd105TemperatureControl.HostArmAsync 第三个条件（切热源序列关了输出、挂着回路，这时来一条下发）：调之前回路还在控（钉住），
///   这一场沿用开控时锁的（新模式说一句；旧模式照旧重新起步、什么都不多说）；操作人停控清锁定，再下发按那时的选择。
/// 影子的报警条件（制冷顶满 60 s、釜内还在升……）在这里不造：把影子换成「核心类照算、AlarmOn 由测试摆」的那种。外环周期调到 200 ms 省时间。
/// </summary>
public class LiveOuterSessionTests
{
    /// <summary>核心类照算，只是这一拍的 AlarmOn 由测试摆（核会话那一侧的沿）。</summary>
    private sealed class AlarmShadow : LiveOuterShadow
    {
        public volatile bool Alarm;

        protected override LiveOuterOutput StepCore(in LiveOuterInput input, bool first)
            => base.StepCore(input, first) with { AlarmOn = Alarm };
    }

    private static HostControlLoop LoopOf(Rd105TemperatureControl t)
        => (HostControlLoop)typeof(Rd105TemperatureControl).GetField("_loop", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(t)!;

    private static void ReplaceShadow(HostControlLoop loop, int ch, LiveOuterShadow shadow)
    {
        var st = typeof(HostControlLoop).GetMethod("State", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(loop, new object[] { ch })!;
        st.GetType().GetField("Live")!.SetValue(st, shadow);
    }

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

    private static string[] Snapshot(List<string> logs) { lock (logs) return logs.ToArray(); }

    /// <summary>认引号的 CSV 拆行（同 LiveOuterRecorderTests）。</summary>
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

    private sealed class Bench : IAsyncDisposable
    {
        public readonly List<string> Logs = new();
        public readonly FakeRd105Device Dev = new() { Thermal = true, GainCPerSec = 1.0 };
        public IDeviceSession S = null!;
        public Rd105Session Rd = null!;
        public HostControlLoop Loop = null!;
        public readonly AlarmShadow[] Shadows = { new(), new() };
        private IDisposable? _dir;

        public async Task OpenAsync()
        {
            _dir = Rd105HostControl.UseGainsDir(Path.Combine(Path.GetTempPath(), "tec-lo-s1-" + Guid.NewGuid().ToString("N")));
            Dev.Set(1, "LIMITED", 90); Dev.Set(2, "LIMITED", 90);
            var drv = new Rd105TecDriver { LinkFactory = _ => new Rd105Link(Dev) };
            S = await drv.OpenAsync(Conn(), Ctx(Logs), CancellationToken.None);
            Rd = (Rd105Session)S;
            Loop = LoopOf(Rd.TempOf(0));
            Loop.OuterPeriod = TimeSpan.FromMilliseconds(200);
            for (var w = 0; w < 2; w++)
            {
                ReplaceShadow(Loop, w + 1, Shadows[w]);
                Rd.TempOf(w).SetActuator(ActuatorMode.HeaterTec, GainTable.Both);   // 双向挡：新外环才锁得成
                Rd.TempOf(w).FeedReactor(20.0);
            }
            await S.StartAsync(CancellationToken.None);
            await WaitUntil(() => Shadows[0].Started && Shadows[1].Started, 5000, "两路影子起算");
        }

        public async ValueTask DisposeAsync()
        {
            await S.DisposeAsync();
            _dir?.Dispose();
        }
    }

    [Fact]
    public async Task 放热报警_新模式上升沿报一次不停控_撤了再成立再报_旧模式不报_algo列和lo_alarm列对得上_保持期换说法()
    {
        await using var b = new Bench();
        await b.OpenAsync();
        var a = b.Rd.TempOf(0);
        var bb = b.Rd.TempOf(1);
        a.SetOuterAlgorithm(true);
        a.SetTecEngaged(true);
        await a.SetTargetAsync(new TempTarget(30, TempChannelKind.Reactor), CancellationToken.None);
        await bb.SetTargetAsync(new TempTarget(30, TempChannelKind.Reactor), CancellationToken.None);   // B 路没选新：旧外环
        Assert.True(a.LiveOuterInControl);
        Assert.False(bb.LiveOuterInControl);
        Assert.True(a.OuterAlgorithmRequested);
        Assert.False(bb.OuterAlgorithmRequested);
        var pathA = b.Rd.LoopLogPathOf(0)!;
        var pathB = b.Rd.LoopLogPathOf(1)!;
        b.Shadows[1].Alarm = true;                                     // B 路（旧外环）条件一直成立：只记 lo_alarm，不报
        await Task.Delay(800);
        Assert.Null(a.Exotherm);

        int Raised() => Snapshot(b.Logs).Count(l => l.StartsWith("warn") && l.Contains("通道1放热报警："));
        int Cleared() => Snapshot(b.Logs).Count(l => l.StartsWith("info") && l.Contains("通道1放热报警撤了"));

        // ① 成立：报一次，成立着不重报
        b.Shadows[0].Alarm = true;
        await WaitUntil(() => a.Exotherm is not null, 5000, "报警上升沿");
        await Task.Delay(1000);
        Assert.Equal(1, Raised());
        Assert.Contains("控温不停", a.Exotherm);
        // 【本地改动】（第 1 步复核）报警条件不看 TEC 有没有真在取热（断水 / TEC 开路也像）：不说「保持满冷」，叫人查 TEC 电流和冷却水
        Assert.Contains("冷却没在出力", a.Exotherm);
        Assert.Contains("请人工查 TEC 电流和冷却水", a.Exotherm);
        Assert.DoesNotContain("保持满冷", a.Exotherm);
        Assert.True(a.Enabled && a.LiveOuterInControl);
        // ② 撤了：说一句，Exotherm 清掉
        b.Shadows[0].Alarm = false;
        await WaitUntil(() => a.Exotherm is null, 5000, "报警下降沿");
        Assert.Equal(1, Cleared());
        // ③ 再成立：再报一次
        b.Shadows[0].Alarm = true;
        await WaitUntil(() => a.Exotherm is not null, 5000, "报警再成立");
        await Task.Delay(600);
        Assert.Equal(2, Raised());
        Assert.True(a.Enabled && a.LiveOuterInControl);

        // Tr 丢失保持期：面板那句换成「按模型推」（旧外环那句是「夹套按 X ℃ 保持」）
        a.FeedReactor(double.NaN);
        await WaitUntil(() => a.Holding is not null, 5000, "进保持");
        Assert.Contains("外环新算法按模型推夹套目标", a.Holding);
        bb.FeedReactor(double.NaN);
        await WaitUntil(() => bb.Holding is not null, 5000, "B 路进保持");
        Assert.Contains("夹套按", bb.Holding);
        Assert.DoesNotContain("按模型推", bb.Holding);
        a.FeedReactor(20.0);
        bb.FeedReactor(20.0);
        await WaitUntil(() => a.Holding is null && bb.Holding is null, 5000, "出保持");

        await a.StopAsync(CancellationToken.None);
        await bb.StopAsync(CancellationToken.None);
        await WaitUntil(() => Snapshot(b.Logs).Any(l => l.Contains("通道1放热报警随停控撤了")), 5000, "停控后报警清掉");
        Assert.Null(a.Exotherm);
        Assert.False(a.LiveOuterInControl);
        var logs = Snapshot(b.Logs);
        Assert.Equal(2, Raised());
        Assert.DoesNotContain(logs, l => l.Contains("通道2放热报警"));  // 旧外环那一路：不报
        Assert.Null(bb.Exotherm);
        Assert.DoesNotContain(logs, l => l.Contains("上位机回路停控"));  // 报警不停控
        foreach (var w in new[] { "夹套压低", "外环积分冻结在", "外环偏置自校正", "那一枪" })
            Assert.DoesNotContain(logs, l => l.Contains("放热") && l.Contains(w));

        // 逐拍记录：A 路逐拍行 algo = 1；报警成立的行 lo_alarm = 1，第一次成立那一行 note 里有那句；B 路 algo = 0、lo_alarm 有 1（只记）
        var ra = File.ReadAllLines(pathA).Skip(1).Select(SplitCsv).ToArray();
        Assert.All(ra, r => Assert.Equal(36, r.Length));                // 【本地改动】（2026-11-17，S1）行尾再加 s1_ 3 列：33 → 36
        // 在控的逐拍行（kind = Tr）；停控那一刻回路可能先于记录收尾多写一拍：不在控的（kind = Tj、algo = 0），或者停控时回路正卡在写占空比、
        // 那一拍照算完的（kind = Tr、algo = 1、outer 列空——【本地改动】第 1 步复核起按那一拍实际走的那一支标，下面的断言对它也成立）
        var beatsA = ra[..^1].Where(r => r[2] == "Tr").ToArray();
        Assert.True(beatsA.Length > 10, $"A 路在控的行只有 {beatsA.Length} 行");
        Assert.All(beatsA, r => Assert.Equal("1", r[25]));
        Assert.All(beatsA, r => Assert.True(r[12] == "" && r[13] == "" && r[14] == "" && r[15] == "", "新外环在控时 outer_p / i / d / fin 该是空的"));
        var firstAlarm = Array.FindIndex(beatsA, r => r[32] == "1");
        Assert.True(firstAlarm > 0);
        Assert.Contains("通道1放热报警：", beatsA[firstAlarm][24]);
        Assert.Contains(beatsA, r => r[24].Contains("通道1放热报警撤了"));
        Assert.Contains("外环：新算法（按实测实时算）", ra[0][24]);   // 开控那句跟在「开始」后面
        var rb = File.ReadAllLines(pathB).Skip(1).Select(SplitCsv).ToArray()[..^1];
        Assert.All(rb, r => Assert.Equal("0", r[25]));
        Assert.Contains(rb, r => r[32] == "1");
        Assert.DoesNotContain(rb, r => r[24].Contains("放热"));
    }

    [Fact]
    public async Task 跑飞放热豁免也上红条_说放热或冷却没在出力_请人工查TEC电流和冷却水_只报一次_撤了清掉_旧外环那一路不报()
    {
        // 【本地改动】（第 1 步复核）豁免成立时影子的报警条件不一定成立（降温中 TEC 开路：夹套被釜往上带，釜内却不升）——原来只进日志 / note，
        // 面板上没有红条。会话按周期快照上的 LiveExoExempt 一并上红条。回路不转，回调由测试按拍喂（Rd105Session.OnCycle）：确定
        var logs = new List<string>();
        using var dirScope = Rd105HostControl.UseGainsDir(Path.Combine(Path.GetTempPath(), "tec-lo-s1x-" + Guid.NewGuid().ToString("N")));
        var dev = new FakeRd105Device { Thermal = false };
        dev.Set(1, "LIMITED", 90); dev.Set(2, "LIMITED", 90);
        await using var s = await new Rd105TecDriver { LinkFactory = cfg => new Rd105Link(dev) }.OpenAsync(Conn(), Ctx(logs), CancellationToken.None);
        var rd = (Rd105Session)s;
        var onCycle = typeof(Rd105Session).GetMethod("OnCycle", BindingFlags.NonPublic | BindingFlags.Instance)!;
        void Beat(bool exempt1, bool live1 = true, bool exempt2 = false, bool active1 = true)
        {
            ChannelCycleInfo Info(bool active, bool live, bool exempt) => new(active, false, 0, 15, 30, active ? -90 : 0, 0, 0, 0)
            {
                Cascade = active, InnerSetpointC = 5, OuterMeasuredC = 40,
                LiveOuterInControl = active && live, LiveExoExempt = active && exempt,
            };
            var snap = new TecSnapshot(DateTime.Now, 30, 0, 0, 30, 0, 0, 24);
            onCycle.Invoke(rd, new object[] { new ControlCycleResult(snap, Info(active1, live1, exempt1), Info(true, false, exempt2), null) });
        }
        int Raised() => Snapshot(logs).Count(l => l.StartsWith("warn") && l.Contains("通道1放热报警："));
        var a = rd.TempOf(0);

        Beat(exempt1: false);
        Assert.Null(a.Exotherm);
        Beat(exempt1: true, exempt2: true);                            // B 路是旧外环（algo 0）：标了也不报
        var text = a.Exotherm;
        Assert.NotNull(text);
        Assert.StartsWith("通道1放热报警：", text);
        Assert.Contains("夹套还被釜内往上带", text);
        Assert.Contains("冷却没在出力", text);
        Assert.Contains("请人工查 TEC 电流和冷却水", text);
        Assert.Contains("控温不停", text);
        Assert.DoesNotContain("保持满冷", text);
        foreach (var w in new[] { "夹套压低", "外环积分冻结在", "外环偏置自校正", "那一枪" })
            Assert.DoesNotContain(w, text);
        Beat(exempt1: true);
        Beat(exempt1: true);
        Assert.Equal(1, Raised());                                     // 成立着不重报
        Assert.Null(rd.TempOf(1).Exotherm);
        Assert.DoesNotContain(Snapshot(logs), l => l.Contains("通道2放热报警"));

        Beat(exempt1: false);                                          // 豁免条件撤了：清掉、说一句
        Assert.Null(a.Exotherm);
        Assert.Single(Snapshot(logs), l => l.StartsWith("info") && l.Contains("通道1放热报警撤了"));
        Beat(exempt1: true);                                           // 再成立再报
        Assert.Equal(2, Raised());
        Beat(exempt1: false, active1: false);                          // 停控（跑飞 / 别的原因）：随停控撤
        Assert.Null(a.Exotherm);
        Assert.Single(Snapshot(logs), l => l.Contains("通道1放热报警随停控撤了"));
    }

    [Fact]
    public async Task S1读数没跟着夹套降报警_会话上红条_跟放热报警接在一起_撤了清掉_旧外环那一路不给_会话不另说()
    {
        // 【本地改动】（2026-11-17，S1「读数对不上就停控」）回路判到「读数没跟着夹套降」：上升 / 撤了那两句回路已经经 ChannelNotice 说过（日志 + note），
        // 周期快照带着那句话（ChannelCycleInfo.ReadingAlarm）——会话只管把它摆上红条（Rd105TemperatureControl.ReadingAlarm → ITemperatureStatus.Alarm），
        // 跟放热报警同一条，两样都在用「；」接起来。回路不转，回调由测试按拍喂（Rd105Session.OnCycle）：确定
        var logs = new List<string>();
        using var dirScope = Rd105HostControl.UseGainsDir(Path.Combine(Path.GetTempPath(), "tec-lo-s1r-" + Guid.NewGuid().ToString("N")));
        var dev = new FakeRd105Device { Thermal = false };
        dev.Set(1, "LIMITED", 90); dev.Set(2, "LIMITED", 90);
        await using var s = await new Rd105TecDriver { LinkFactory = cfg => new Rd105Link(dev) }.OpenAsync(Conn(), Ctx(logs), CancellationToken.None);
        var rd = (Rd105Session)s;
        var onCycle = typeof(Rd105Session).GetMethod("OnCycle", BindingFlags.NonPublic | BindingFlags.Instance)!;
        const string text = "通道1釜内读数没跟着夹套降：测试摆的一句";
        void Beat(string? read1, bool live1 = true, bool exempt1 = false, string? read2 = null)
        {
            ChannelCycleInfo Info(bool live, bool exempt, string? read) => new(true, false, 0, 15, 30, -90, 0, 0, 0)
            {
                Cascade = true, InnerSetpointC = 5, OuterMeasuredC = 40,
                LiveOuterInControl = live, LiveExoExempt = live && exempt, ReadingAlarm = read,
            };
            var snap = new TecSnapshot(DateTime.Now, 30, 0, 0, 30, 0, 0, 24);
            onCycle.Invoke(rd, new object[] { new ControlCycleResult(snap, Info(live1, exempt1, read1), Info(false, false, read2), null) });
        }
        var a = rd.TempOf(0);
        var b = rd.TempOf(1);
        foreach (var t in new[] { a, b })                              // 红条只在输出开着时给（同 Holding）：摆成开着
            typeof(Rd105TemperatureControl).GetProperty(nameof(Rd105TemperatureControl.Enabled))!.GetSetMethod(nonPublic: true)!.Invoke(t, new object[] { true });
        ITemperatureStatus Status(Rd105TemperatureControl t) => t;

        Beat(null);
        Assert.Null(a.ReadingAlarm);
        Assert.Null(Status(a).Alarm);
        Beat(text, read2: "通道2釜内读数没跟着夹套降：旧外环那一路不该有");
        Assert.Equal(text, a.ReadingAlarm);
        Assert.Equal(text, Status(a).Alarm);
        Assert.Null(b.ReadingAlarm);                                   // B 路是旧外环（algo 0）：带了也不给
        Assert.Null(Status(b).Alarm);
        Beat(text, exempt1: true);                                     // 跑飞「放热」豁免也成立：同一条红条，放热那句在前
        var both = Status(a).Alarm!;
        Assert.StartsWith("通道1放热报警：", both);
        Assert.EndsWith("；" + text, both);
        Beat(null);                                                    // 两样都撤了
        Assert.Null(a.ReadingAlarm);
        Assert.Null(Status(a).Alarm);
        Beat(text);
        Beat(text, live1: false);                                      // 停控 / 不在新外环：随着清掉
        Assert.Null(a.ReadingAlarm);
        Assert.DoesNotContain(Snapshot(logs), l => l.Contains(text));  // 会话不另说（回路那一路说过）
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 切热源中途关了输出又来下发_回路调之前还在控_这一场沿用开控时锁的_停控后按新选择(bool live)
    {
        await using var b = new Bench();
        await b.OpenAsync();
        var a = b.Rd.TempOf(0);
        a.SetOuterAlgorithm(live);
        await a.SetTargetAsync(new TempTarget(30, TempChannelKind.Reactor), CancellationToken.None);
        Assert.Equal(live, a.LiveOuterInControl);
        await Task.Delay(500);
        a.SetOuterAlgorithm(!live);                                    // 控中改了选择

        // 会话的切热源序列：关输出（回路挂起、Enabled = false），这时来一条下发 → HostArmAsync 第三个条件 → 再走 StartChannelAsync
        await a.EnableAsync(false, CancellationToken.None);
        Assert.True(b.Loop.IsSuspended(1) && !a.Enabled);
        Assert.True(b.Loop.GetChannelStatus(1).Active);                // 钉住：这条路上调 StartChannelAsync 之前回路还在控
        var before = Snapshot(b.Logs).Length;
        await a.SetTargetAsync(new TempTarget(31, TempChannelKind.Reactor), CancellationToken.None);
        Assert.False(b.Loop.IsSuspended(1));
        Assert.Equal(live, a.LiveOuterInControl);                      // 沿用，不按此刻的选择
        var added = Snapshot(b.Logs)[before..];
        if (live)
        {
            Assert.Single(added, l => l.Contains("通道1切换中途重新开控：外环沿用这一场开控时定的新算法"));
            Assert.DoesNotContain(added, l => l.Contains("积分起点"));
        }
        else
        {
            Assert.Single(added, l => l.Contains("通道1串级外环积分起点"));   // 旧模式跟以前一样：重新起步那一句，证明这条路真走了
            Assert.DoesNotContain(added, l => l.Contains("新算法"));
        }
        await Task.Delay(500);
        Assert.Equal(live, a.LiveOuterInControl);

        // 操作人停控：锁定清掉；再下发按此刻的选择（!live）
        await a.StopAsync(CancellationToken.None);
        Assert.False(a.LiveOuterInControl);
        Assert.False(b.Loop.GetLiveOuterStatus(1).Active);
        await a.SetTargetAsync(new TempTarget(30, TempChannelKind.Reactor), CancellationToken.None);
        Assert.Equal(!live, a.LiveOuterInControl);
        await a.StopAsync(CancellationToken.None);
    }
}
