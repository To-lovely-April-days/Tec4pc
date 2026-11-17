using Tec.Core.Tests.Step1Pins;
using Tec.Driver.Abi;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 【本地改动】（2026-11-16，外环改「按实测实时算」第 1 步）会话（DuoSession）这一侧：新外环在控的那一路，会话的哪几条规则不再起作用、哪几条照旧。
/// 用户 2026-11-16 点头：选「新」时「提前切 TEC、压低 2 度、至少制冷 60 秒、低了回加热棒」这几条不再起作用；冷却水的问答和自动开照旧。
/// 主会话 D1 / D3 / D8：A1–A6（EarlyVerdict 那一串、ControlledBelow）按路关；最短制冷 60 s 不动（剩下的只有经挂起 / 等水后接上 TEC 的段）。
/// 台子用 Step1Pins 的会话金样台子（温控器 PID 假机 + 假钟 + 手动推拍，会话自带的采集循环已停，逐拍确定），
/// 「这一路算不算新外环在控」由测试口 <c>DuoSession.LiveRulesOverride</c> 直接给——温控器 PID 方式下没有上位机回路、也就没有新外环，
/// 这里只验会话规则的语义；配置 → 回路锁定 → 会话判定这条接线在 <see cref="LiveOuterDuoWiringTests"/>（上位机串级、真时钟）。
/// 每条都带旧模式对照（同一段脚本旧模式下确实走到了那条规则），证明场景够得着。
/// </summary>
public sealed class LiveOuterSessionRulesTests
{
    private static string Regime(SessionLegacyGoldenBench b, int w) => ((IHeatSource)b.T[w]).Regime ?? "";

    private static void SetLive(SessionLegacyGoldenBench b, bool live)
    {
        if (live) b.D.LiveRulesOverride = _ => true;
    }

    // ── A1（提前切换）+ A2（压低）──────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 新模式_被控量冲过目标且在升_不提前切TEC不压_回路真要冷才按功率符号接TEC(bool live)
    {
        await using var b = await SessionLegacyGoldenBench.OpenAsync();
        SetLive(b, live);
        await b.Target(0, 30);
        Assert.True(b.Io.Coils[0]);
        await b.Duty(0, -5);                                            // 加热棒还在出力：功率符号说的是加热
        await b.Tick(30.0, 31);
        foreach (var tj in new[] { 30.03, 30.06, 30.09, 30.12, 30.15, 30.18, 30.21, 30.24 }) await b.Tick(tj, 5);
        if (!live)
        {
            // 对照：旧模式 30.18 那一拍（凑够 30 s 跨度）按提前判据切 TEC；温控器 PID 下压低接不上，只说「这一路不压」
            Assert.Equal(1, b.Count("提前切 TEC，不等功率翻号"));
            Assert.Equal(1, b.Count("这一路不压夹套目标"));
            Assert.True(b.Io.Coils[7], "旧模式该提前切 TEC\n" + b.Tail());
            Assert.Contains("提前切 TEC", Regime(b, 0));
            Assert.Equal(0, b.Count("外环归新算法"));
            return;
        }
        Assert.Equal(0, b.Count("提前切 TEC，不等功率翻号"));         // 会话那句提示里提到「提前切 TEC」停用，按判据原话数
        Assert.Equal(0, b.Count("这一路不压夹套目标"));
        Assert.Equal(0, b.Count("夹套目标压到"));
        Assert.True(b.Io.Coils[0], "新模式不该按提前判据切 TEC\n" + b.Tail());
        Assert.False(b.Io.Coils[7]);
        Assert.Equal("双向挡（加热棒 ↔ TEC 跟功率符号走）", Regime(b, 0));
        Assert.Null(b.Water.Pending);
        Assert.Null(((ITemperatureStatus)b.T[0]).CascadePushC);
        Assert.Equal(1, b.Count("工位 A 外环归新算法（按实测实时算）"));      // 头一拍说一次，之后不重说

        // 回路真要冷（TEC 那一极 +15 % 连续 3 s）：照功率符号接 TEC——符号进法，不立提前判据的段
        await b.Duty(0, 15);
        await b.Tick(30.27, 1);
        Assert.False(b.Io.Coils[7]);
        await b.Tick(30.30, 3.5);
        Assert.True(b.Io.Coils[7], "回路要冷该按符号接 TEC\n" + b.Tail());
        Assert.False(b.Io.Coils[0]);
        Assert.Equal(1, b.Count("已连续 3 s 指向TEC——合 TEC 功率线"));
        Assert.Equal(0, b.Count("提前切 TEC，不等功率翻号"));
        Assert.Equal(1, b.Count("外环归新算法"));
    }

    // ── A6d（低了立刻回加热棒）/ A6b（高了不走）/ A6a（低了不去 TEC）──────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 新模式_在TEC侧低过目标且在降_不抢先回加热棒_回路要加热满3s才回(bool live)
    {
        await using var b = await SessionLegacyGoldenBench.OpenAsync();
        SetLive(b, live);
        await b.Target(0, 10);                                          // 夹套 25 → 目标 10：按方向起步合 TEC
        Assert.True(b.Io.Coils[7]);
        await b.Duty(0, 0);                                             // 回路没在明确要冷
        foreach (var (tj, dt) in new[] { (10.4, 31.0), (10.2, 5.0), (10.0, 5.0), (9.8, 5.0) }) await b.Tick(tj, dt);
        if (!live)
        {
            Assert.Equal(1, b.Count("回加热棒，不等功率翻号"));           // 对照：旧模式 15 s 跨度、低过 9.9 且在降 → 当拍回加热棒
            Assert.True(b.Io.Coils[0], "旧模式该抢先回加热棒\n" + b.Tail());
            return;
        }
        Assert.Equal(0, b.Count("不等功率翻号"));
        Assert.True(b.Io.Coils[7], "新模式不该按「低过目标且在降」抢先回加热棒\n" + b.Tail());
        Assert.False(b.Io.Coils[0]);

        await b.Duty(0, -5);                                            // 回路要加热 5 %
        await b.Tick(9.7, 1);
        Assert.True(b.Io.Coils[7]);
        await b.Tick(9.6, 3.5);
        Assert.True(b.Io.Coils[0], "回路要加热满 3 s 该回加热棒\n" + b.Tail());
        Assert.False(b.Io.Coils[7]);
        Assert.Equal(1, b.Count("已连续 3 s 指向电加热——合加热棒"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 新模式_在TEC侧回路要加热满3s_被控量高过目标且在升也照符号回加热棒(bool live)
    {
        await using var b = await SessionLegacyGoldenBench.OpenAsync();
        SetLive(b, live);
        await b.Target(0, 10);
        Assert.True(b.Io.Coils[7]);
        await b.Duty(0, 0);
        foreach (var (tj, dt) in new[] { (10.20, 31.0), (10.25, 5.0), (10.30, 5.0), (10.35, 5.0) }) await b.Tick(tj, dt);
        await b.Duty(0, -5);                                            // 回路要加热（夹套比目标高，内环往回收）
        await b.Tick(10.40, 1);
        await b.Tick(10.45, 3.5);                                       // 符号连续 3.5 s 指向加热棒；被控量 +0.45 且在升
        if (!live)
        {
            Assert.True(b.Io.Coils[7], "对照：旧模式高过目标且在升，符号说加热也不走\n" + b.Tail());
            Assert.Equal(0, b.Count("指向电加热"));
            return;
        }
        Assert.True(b.Io.Coils[0], "新模式该照功率符号回加热棒\n" + b.Tail());
        Assert.False(b.Io.Coils[7]);
        Assert.Equal(1, b.Count("已连续 3 s 指向电加热——合加热棒"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 新模式_在加热棒侧回路要冷满3s_被控量低过目标且在降也照符号接TEC(bool live)
    {
        await using var b = await SessionLegacyGoldenBench.OpenAsync();
        SetLive(b, live);
        await b.Target(0, 30);
        Assert.True(b.Io.Coils[0]);
        await b.Duty(0, 0);
        foreach (var (tj, dt) in new[] { (29.95, 31.0), (29.90, 5.0), (29.85, 5.0), (29.80, 5.0) }) await b.Tick(tj, dt);
        await b.Duty(0, 15);                                            // 回路要冷
        await b.Tick(29.75, 1);
        await b.Tick(29.70, 3.5);                                       // 符号连续 3.5 s 指向 TEC；被控量 −0.30 且在降
        if (!live)
        {
            Assert.True(b.Io.Coils[0], "对照：旧模式低过目标且在降，符号说要 TEC 也不去\n" + b.Tail());
            Assert.False(b.Io.Coils[7]);
            Assert.Equal(0, b.Count("指向TEC"));
            return;
        }
        Assert.True(b.Io.Coils[7], "新模式该照功率符号接 TEC\n" + b.Tail());
        Assert.False(b.Io.Coils[0]);
        Assert.True(b.Io.Coils[4]);                                     // 接 TEC 照旧先开水（阀 → 冷水机 → 合功率线）
        Assert.Equal(1, b.Count("已连续 3 s 指向TEC——合 TEC 功率线"));
    }

    // ── A7'（挂起撤段只看符号）：ChillerInterlockTests「功率符号进的挂起_回路钳0_被控量低过目标且在降_撤段_到时也不接TEC」新模式期望相反 ──

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 新模式_功率符号进的挂起_钳0时被控量低过目标且在降也不撤_到时按打开接TEC(bool live)
    {
        await using var b = await SessionLegacyGoldenBench.OpenAsync();
        SetLive(b, live);
        await b.Target(0, 30, askWater: true);                         // 手动开、开那一下没问过水
        await b.Duty(0, 15);
        await b.Tick(28.0, 31);
        await b.Tick(28.0, 1);
        await b.Tick(28.0, 3.5);                                        // 回路要冷连续 3.5 s：符号要 TEC → 挂询问
        var q = b.Water.Pending;
        Assert.NotNull(q);
        var askAt = b.Now;
        Assert.Contains("等人答开不开冷却水", Regime(b, 0));
        await b.Duty(0, 0);                                             // 挂起期间回路钳 0（想冷也出不来）
        foreach (var tj in new[] { 27.9, 27.8, 27.7, 27.6, 27.5, 27.4, 27.3, 27.2, 27.1, 27.0, 26.9 }) await b.Tick(tj, 5);
        await b.Tick(26.85, 4);                                         // 问出来 59 s：还没到时
        Assert.Equal(TimeSpan.FromSeconds(59), b.Now - askAt);
        Assert.Equal(q!.Id, b.Water.Pending?.Id);                       // 两种模式问题都留着（本次控温只问一次）
        Assert.True(b.Io.Coils[0]);
        Assert.False(b.Io.Coils[7]);
        if (!live)
            Assert.Equal(1, b.Count("等水期间被控量已回落"));            // 对照：旧模式夹套自己凉下去就撤段（问题留着）
        else
        {
            Assert.Equal(0, b.Count("被控量已回落"));
            Assert.Contains("等人答开不开冷却水", Regime(b, 0));         // 新模式挂起只看功率符号：钳 0 不表态，一直等
        }

        await b.Tick(26.8, 2);                                          // 问出来满 61 s：到时按打开
        Assert.Null(b.Water.Pending);
        if (!live)
        {
            Assert.Equal(1, b.Count("询问有了结果：60 s 没人答，按「打开冷却水」办"));   // 只记下，此刻不接
            Assert.True(b.Io.Coils[0]);
            Assert.False(b.Io.Coils[7]);
            Assert.False(b.Io.Coils[4]);
            return;
        }
        Assert.Equal(1, b.Count("中途切 TEC：60 s 没人答，按「打开冷却水」办"));
        Assert.Equal(1, b.Count("符号进法经挂起接通——立制冷段（最短 60 s）"));
        Assert.True(b.Io.Coils[7], "新模式到时该接 TEC\n" + b.Tail());
        Assert.False(b.Io.Coils[0]);
        Assert.True(b.Io.Coils[4]);
        Assert.Equal("双向挡·制冷段（功率符号切 TEC）", Regime(b, 0));
        Assert.Equal(1, b.Count("问操作人开不开冷却水"));
    }

    // ── 问水、最短制冷 60 s（D8）照旧：同一段脚本新 / 旧两种模式金样逐字一样（除了新模式头一拍那句提示）──

    [Fact]
    public async Task 新模式_问水挂起_答打开接TEC_最短制冷照旧60s_除那句提示外跟旧模式逐拍逐字一样()
    {
        var old = await AskAnswerHoldAsync(live: false);
        var neu = await AskAnswerHoldAsync(live: true);
        Assert.DoesNotContain("外环归新算法", old);
        Assert.Contains("外环归新算法", neu);
        var a = old.Split('\n');
        var n = WithoutLiveNote(neu).Split('\n');
        var i = 0;
        while (i < a.Length && i < n.Length && a[i] == n[i]) i++;
        Assert.True(i == a.Length && i == n.Length,
            $"新模式（去掉那句提示）跟旧模式第 {i + 1} 行起不一样：\n  旧：{(i < a.Length ? a[i] : "（到头）")}\n  新：{(i < n.Length ? n[i] : "（到头）")}");
    }

    /// <summary>手动开、符号要 TEC 挂问 → 5 s 后同一个问题还在 → 答打开接 TEC（立段，最短 60 s）→ 回路要加热：60 s 里不扳，满了再满 3 s 才回。返回金样全文。</summary>
    private static async Task<string> AskAnswerHoldAsync(bool live)
    {
        await using var b = await SessionLegacyGoldenBench.OpenAsync();
        SetLive(b, live);
        await b.Target(0, 30, askWater: true);
        await b.Duty(0, 15);
        await b.Tick(28.0, 31);
        await b.Tick(28.0, 1);
        await b.Tick(28.0, 3.5);
        var q = b.Water.Pending;
        Assert.NotNull(q);
        await b.Tick(28.0, 5);
        Assert.Equal(q!.Id, b.Water.Pending?.Id);                       // 不重问
        b.Answer(true);
        await b.Tick(28.0, 1);
        Assert.True(b.Io.Coils[7], "答打开该接 TEC\n" + b.Tail());
        Assert.Equal(1, b.Count("符号进法经挂起接通——立制冷段（最短 60 s）"));
        await b.Duty(0, -20);                                           // 回路要加热
        for (var k = 0; k < 11; k++) await b.Tick(28.0, 5);             // 段龄 55 s
        Assert.True(b.Io.Coils[7], "最短制冷 60 s 内不该扳回\n" + b.Tail());
        Assert.Equal(1, b.Count("（< 最短 60 s）"));
        await b.Tick(28.0, 5);                                          // 段龄 60：从这一拍起符号才开始计时
        await b.Tick(28.0, 1);
        Assert.True(b.Io.Coils[7]);
        await b.Tick(28.0, 3.5);
        Assert.True(b.Io.Coils[0], "满 60 s 后符号说加热满 3 s 该回\n" + b.Tail());
        Assert.Equal(1, b.Count("制冷段结束（功率已连续 3 s 指向电加热"));
        return b.Trace;
    }

    /// <summary>金样里去掉新模式那句提示（日志那一行整行去掉；逐拍记录 Note 跟别的 note 同一拍时是「；」连着的，只去掉它那一段）。</summary>
    private static string WithoutLiveNote(string trace)
    {
        var lines = trace.Split('\n');
        var texts = lines.Where(l => l.StartsWith("  日志·", StringComparison.Ordinal) && l.Contains("外环归新算法"))
            .Select(l => l[(l.IndexOf("DUO1 ", StringComparison.Ordinal) + "DUO1 ".Length)..]).Distinct().ToArray();
        Assert.NotEmpty(texts);
        var kept = new List<string>();
        foreach (var l in lines)
        {
            if (l.StartsWith("  日志·", StringComparison.Ordinal) && l.Contains("外环归新算法")) continue;
            var x = l;
            if (x.StartsWith("  Note·", StringComparison.Ordinal))
            {
                foreach (var t in texts) x = x.Replace("；" + t, "").Replace(t + "；", "").Replace(t, "");
                if (x.Length <= "  Note·A  ".Length) continue;
            }
            kept.Add(x);
        }
        return string.Join("\n", kept);
    }

    // ── C6：旧 → 新那一拍（下发那一刻夹套读不到、段被带进新模式这种漏网的）──────────────

    [Fact]
    public async Task 旧转新那一拍_提前判据接通的段收掉_继电器交给功率符号_不再扣满最短制冷_提示每场说一次()
    {
        await using var b = await SessionLegacyGoldenBench.OpenAsync();
        var live = new bool[1];
        b.D.LiveRulesOverride = w => w == 0 && live[0];
        await b.Target(0, 30);
        await b.Duty(0, -5);
        await b.Tick(30.0, 31);
        foreach (var tj in new[] { 30.03, 30.06, 30.09, 30.12, 30.15, 30.18 }) await b.Tick(tj, 5);
        Assert.Equal(1, b.Count("提前切 TEC，不等功率翻号"));           // 旧模式立的提前判据段（已接通）
        Assert.True(b.Io.Coils[7]);
        Assert.Contains("制冷段（提前切 TEC", Regime(b, 0));

        live[0] = true;
        await b.Tick(30.18, 1);
        Assert.Equal(1, b.Count("制冷段结束（外环换成新算法"));
        Assert.Equal(1, b.Count("工位 A 外环归新算法（按实测实时算）"));
        Assert.Equal("双向挡（加热棒 ↔ TEC 跟功率符号走）", Regime(b, 0));
        Assert.True(b.Io.Coils[7]);                                     // 收段不扳继电器，交给功率符号
        await b.Tick(30.18, 3.5);                                       // 段龄才 10 s 出头：旧模式要扣满 60 s；现在加热满 3 s 就回
        Assert.True(b.Io.Coils[0], "收段后该照功率符号回加热棒\n" + b.Tail());
        Assert.False(b.Io.Coils[7]);
        Assert.Equal(1, b.Count("已连续 3 s 指向电加热——合加热棒"));
        Assert.Equal(0, b.Count("（< 最短"));
        Assert.Equal(0, b.Count("不再按提前判据进"));                    // 收段不记再进间隔

        // 停控再开：下一场再说一次（NoteStop 复位）；同一场不重说
        await b.Stop(0);
        await b.Target(0, 30);
        await b.Tick(30.0, 1);
        await b.Tick(30.0, 1);
        Assert.Equal(2, b.Count("工位 A 外环归新算法（按实测实时算）"));
    }

    [Fact]
    public async Task 旧转新那一拍_提前判据挂起问水的段撤掉_问题留着_答了就记下不扳()
    {
        await using var b = await SessionLegacyGoldenBench.OpenAsync();
        var live = new bool[1];
        b.D.LiveRulesOverride = w => w == 0 && live[0];
        await b.Target(0, 30, askWater: true);
        await b.Duty(0, -5);
        await b.Tick(30.0, 31);
        foreach (var tj in new[] { 30.03, 30.06, 30.09, 30.12, 30.15, 30.18 }) await b.Tick(tj, 5);
        Assert.Equal(1, b.Count("提前切 TEC，不等功率翻号"));
        var q = b.Water.Pending;
        Assert.NotNull(q);
        Assert.Contains("等人答开不开冷却水", Regime(b, 0));
        Assert.True(b.Io.Coils[0]);

        live[0] = true;
        await b.Tick(30.18, 1);
        Assert.Equal(1, b.Count("制冷段取消（外环换成新算法"));
        Assert.Equal(1, b.Count("询问留着"));
        Assert.Equal(q!.Id, b.Water.Pending?.Id);                       // 本次控温只问一次：问题留着
        Assert.Equal("双向挡（加热棒 ↔ TEC 跟功率符号走）·开不开冷却水的询问挂着", Regime(b, 0));
        Assert.True(b.Io.Coils[0]);
        Assert.False(b.Io.Coils[7]);

        b.Answer(true);
        await b.Tick(30.18, 1);
        Assert.Null(b.Water.Pending);
        Assert.Equal(1, b.Count("询问有了结果：操作人选了打开冷却水——此刻不要制冷，先记下"));
        Assert.True(b.Io.Coils[0]);                                     // 此刻回路要加热：不扳
        Assert.False(b.Io.Coils[7]);
    }

    // ── 两路一新一旧：按路判，不串 ─────────────────────────────────────

    [Fact]
    public async Task 两路一新一旧同跑_A不提前切_B照旧提前切_B的继电器挡位判据跟两路都旧时逐拍一样()
    {
        var both = await TwoWellsAsync(liveA: false);
        var mixed = await TwoWellsAsync(liveA: true);
        Assert.Equal(1, both.AEarly);
        Assert.Equal(0, mixed.AEarly);                                   // A（新）：不提前切
        Assert.Equal(1, mixed.BEarly);                                   // B（旧）：照旧提前切
        Assert.True(mixed.BOnTec);
        Assert.False(mixed.AOnTec);
        Assert.Equal(both.BTrack, mixed.BTrack);                         // B 每拍的继电器 / 挡位一样
        Assert.Equal(both.BLines, mixed.BLines);                         // B 的定挡 / 判据原话一样
        Assert.Equal(0, both.LiveNotes);
        Assert.Equal(1, mixed.LiveNotes);
        Assert.Equal(0, mixed.BLiveNotes);
    }

    private sealed record TwoWells(int AEarly, int BEarly, bool AOnTec, bool BOnTec, List<string> BTrack, List<string> BLines, int LiveNotes, int BLiveNotes);

    private static async Task<TwoWells> TwoWellsAsync(bool liveA)
    {
        await using var b = await SessionLegacyGoldenBench.OpenAsync();
        if (liveA) b.D.LiveRulesOverride = w => w == 0;
        var track = new List<string>();
        void Mark() => track.Add($"DO1={b.Io.Coils[1]} DO6={b.Io.Coils[6]} 挡位={Regime(b, 1)}");
        await b.Target(0, 30);
        await b.Target(1, 30);
        Mark();
        await b.Duty(0, -5);
        await b.Duty(1, -5);
        await b.Tick(30.0, 31, 30.0);
        Mark();
        foreach (var v in new[] { 30.03, 30.06, 30.09, 30.12, 30.15, 30.18, 30.21, 30.24 })
        {
            await b.Tick(v, 5, v);
            Mark();
        }
        var bLines = b.Trace.Split('\n')
            .Where(l => l.StartsWith("  日志·", StringComparison.Ordinal) && l.Contains("工位 B 双向挡")).ToList();
        return new TwoWells(
            CountEarly(b, "A"),
            CountEarly(b, "B"),
            b.Io.Coils[7] && !b.Io.Coils[0], b.Io.Coils[6] && !b.Io.Coils[1],
            track, bLines, b.Count("外环归新算法"), b.Count("工位 B 外环归新算法"));
    }

    private static int CountEarly(SessionLegacyGoldenBench b, string well)
        => b.Trace.Split('\n').Count(l => l.StartsWith("  日志·", StringComparison.Ordinal)
                                           && l.Contains($"工位 {well} 双向挡") && l.Contains("提前切 TEC，不等功率翻号"));
}
