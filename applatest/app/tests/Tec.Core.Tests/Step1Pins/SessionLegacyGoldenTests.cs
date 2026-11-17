using Tec.Drivers.Rd105;
using Xunit;

namespace Tec.Core.Tests.Step1Pins;

/// <summary>
/// 【本地改动】E0 会话旧规则金样（2026-11-16，外环「按实测实时算」第 1 步动产品代码之前，在 22b9ebe 上录、跑绿）。
/// 第 1 步要在新模式下按路关掉 10-28 那几条会话规则（A1 提前切换、A2 压低、A3 最短制冷、A4 再进间隔、A5 回落 / 符号退段、
/// A6 否决 / 抢先），旧模式（开关是旧 / 从没设过）下必须一字不变。这里把旧模式下会话的一举一动逐拍录成夹具
/// （Step1Pins/golden/*.txt），以后每次跑出来逐行相同才算过。台子见 <see cref="SessionLegacyGoldenBench"/>。
/// 每个场景先在脚本里证明真的走到了那条规则（那句日志出现了、继电器在那一拍确实没动），再跟夹具逐字比——
/// 夹具对不上先查是不是旧路径被改了，不许重录了事（重录办法见 <see cref="SessionLegacyGolden"/>，正常不该用）。
/// 时间都是假钟秒；配置除通水延时 0 外全是缺省（提前切换偏差 0.1、最短制冷 60 s、再进间隔 60 s、退出超前 180 s、
/// 冷水机最短间隔 60 s、等答复 60 s、符号驻留 3 s、进段斜率跨 30 s / 其余 10 s、斜率门槛 0.0005 K/s）。
/// </summary>
public sealed class SessionLegacyGoldenTests
{
    private static string TempGainsDir() => Path.Combine(Path.GetTempPath(), "tec-step1pins-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// 开台子、跑脚本、跟夹具逐字比。脚本里的断言（「先证明走到了那条规则」）没过时，把跑到出错为止的逐拍落到临时目录、路径写进失败信息里。
    /// 增益表目录的替换是 AsyncLocal：调用方（测试方法本身）先设好再进来。
    /// </summary>
    private static async Task Golden(string name, Func<SessionLegacyGoldenBench, Task> script)
    {
        await using var b = await SessionLegacyGoldenBench.OpenAsync();
        try
        {
            await script(b);
        }
        catch (Exception ex)
        {
            var dump = Path.Combine(Path.GetTempPath(), $"step1pins-{name}.failed.txt");
            try { File.WriteAllText(dump, b.Trace, new System.Text.UTF8Encoding(false)); } catch { dump = "（没写成）"; }
            throw new Xunit.Sdk.XunitException($"{ex.Message}\n跑到出错为止的逐拍：{dump}", ex);
        }
        SessionLegacyGolden.Match(name, b.Trace);
    }

    /// <summary>升温到 30 ℃（加热棒合着、要加热 5 %），把夹套 5 s 一拍 +0.03 K 顶到 30.18：凑够 30 s 跨度那一拍（61 s）按提前判据进段。</summary>
    private static async Task RiseInto(SessionLegacyGoldenBench b, double? tjB = null)
    {
        await b.Duty(0, -5);
        await b.Tick(30.00, 31, tjB);
        foreach (var tj in new[] { 30.03, 30.06, 30.09, 30.12, 30.15 }) await b.Tick(tj, 5);
        Assert.Equal(0, b.Count("提前切 TEC，不等功率翻号"));
        await b.Tick(30.18, 5);
    }

    [Fact]
    public async Task 金样1_提前判据进段_温控器PID不压_最短制冷内要加热不扳_段龄满了还在升守着TEC_平了按功率符号退段()
    {
        using var _ = Rd105HostControl.UseGainsDir(TempGainsDir());
        await Golden("e0-1-提前进段-最短制冷-符号退段", async b =>
        {
            await b.Target(0, 30);
            await RiseInto(b);
            // A1：高过目标 0.1 且在升（30 s 窗）→ 提前切 TEC、立段、不等功率翻号
            Assert.Equal(1, b.Count("提前切 TEC，不等功率翻号"));
            // A2：温控器 PID 方式下压不了，说清楚「这一路不压」（段里每拍再试 PushJacketBelow，回路没有，恒 false）
            Assert.Equal(1, b.Count("这一路不压夹套目标"));
            Assert.True(b.Io.Coils[7] && !b.Io.Coils[0], "进段该切到 TEC\n" + b.Tail());

            await b.Duty(0, 20);                                // TEC 在制冷
            await b.Tick(30.20, 5);
            await b.Duty(0, -5);                                // 段龄 5 s 回路就要加热了
            await b.Tick(30.22, 5);
            // A3：最短制冷 60 s 内功率指向电加热也不扳、冷水机不停（这一段只报一次）
            Assert.Equal(1, b.Count("（< 最短 60 s）"));
            foreach (var tj in new[] { 30.24, 30.26, 30.28, 30.30, 30.32 }) await b.Tick(tj, 10);     // 到 121 s：段龄正好 60
            Assert.True(b.Io.Coils[7], "最短制冷时间内不该扳\n" + b.Tail());
            await b.Tick(30.34, 5);
            await b.Tick(30.36, 5);
            // A6c（段里那份否决）：段龄过了 60 s、功率指向电加热已满 3 s，被控量还高过目标且在升 → 守着 TEC
            Assert.True(b.Io.Coils[7] && !b.Io.Coils[0], "还高过目标且在升，功率说加热也该守着 TEC\n" + b.Tail());
            Assert.Equal(0, b.Count("制冷段结束"));

            // 平了（30 s 窗斜率掉到门槛以下）：A5b 功率符号退段 → 回加热棒、关水（先停冷水机）
            for (var i = 0; i < 9; i++) await b.Tick(30.36, 5);
            Assert.Equal(1, b.Count("制冷段结束（功率已连续 3 s 指向电加热"));
            Assert.Equal(1, b.Count("60 s 内不再按提前判据进"));
            Assert.True(b.Io.Coils[0] && !b.Io.Coils[7], "符号退段该回加热棒\n" + b.Tail());
            Assert.Equal(1, b.Count("两路都不制冷了"));

            b.SettleChiller();
            await b.Tick(30.36, 5);                             // 冷水机停稳了才关阀
            await b.Stop(0);
            await b.Tick(30.36, 1);
        });
    }

    [Fact]
    public async Task 金样2_提前判据进段_段龄满了超前回落退段_低过目标且在降回加热棒_再进间隔内不进_到点再进_段里停控()
    {
        using var _ = Rd105HostControl.UseGainsDir(TempGainsDir());
        await Golden("e0-2-超前回落-低了回加热棒-再进间隔", async b =>
        {
            await b.Target(0, 30);
            await RiseInto(b);
            Assert.Equal(1, b.Count("提前切 TEC，不等功率翻号"));

            await b.Duty(0, 20);                                // TEC 制冷 20 %，被控量 5 s 一拍 −0.03 K 往下走
            for (var k = 1; k <= 12; k++) await b.Tick(30.18 - 0.03 * k, 5);      // 到 121 s（29.82）：段龄满 60
            // A5a：被控量（加超前 180 s × 斜率）回落到目标 − 0.1 以下且在降 → 段结束；回路还要冷就留在 TEC
            Assert.Equal(1, b.Count("段结束；继电器交给回路"));
            Assert.Equal(1, b.Count("60 s 内不再按提前判据进"));
            Assert.True(b.Io.Coils[7], "回路还要冷：段结束了也留在 TEC\n" + b.Tail());

            await b.Duty(0, -5);                                // 回路不要冷了（要加热 5 %）
            await b.Tick(29.80, 2);
            // A6d：在 TEC 侧、低过目标 0.1 且在降、回路没在明确要冷 → 立刻回加热棒，不等功率翻号
            Assert.Equal(1, b.Count("且在降（相对目标"));
            Assert.Equal(1, b.Count("——回加热棒，不等功率翻号"));
            Assert.True(b.Io.Coils[0] && !b.Io.Coils[7], "低过目标且在降该回加热棒\n" + b.Tail());

            // 松开后釜壁回弹：被控量再冲过目标且在升——A4 再进间隔 60 s 内不按提前判据进（只报一次），到点才再进
            foreach (var dv in new[] { -0.15, -0.05, 0.05, 0.15, 0.25, 0.35, 0.45, 0.55, 0.65, 0.75, 0.85 }) await b.Tick(30 + dv, 5);
            Assert.Equal(1, b.Count("再进间隔 60 s 内不按提前判据进，继电器按功率符号走"));
            Assert.Equal(1, b.Count("提前切 TEC，不等功率翻号"));
            Assert.True(b.Io.Coils[0], "再进间隔内不该切 TEC\n" + b.Tail());
            await b.Tick(30.95, 5);
            Assert.Equal(2, b.Count("提前切 TEC，不等功率翻号"));
            Assert.True(b.Io.Coils[7], "间隔到点该再进\n" + b.Tail());

            await b.Duty(0, 20);
            await b.Tick(31.00, 5);
            await b.Stop(0);                                    // 段里停控：段结束（停控）
            Assert.Equal(1, b.Count("制冷段结束（停控"));
            await b.Tick(31.00, 1);
            b.SettleChiller();
            await b.Tick(31.00, 5);
        });
    }

    [Fact]
    public async Task 金样3_加热棒侧低过目标且在降_功率要TEC也不去_不降了才按符号接TEC_TEC侧高过且在升_功率要加热也不走()
    {
        using var _ = Rd105HostControl.UseGainsDir(TempGainsDir());
        await Golden("e0-3-低了不去TEC-高了不走", async b =>
        {
            await b.Target(0, 30);
            await b.Duty(0, -5);
            await b.Tick(30.00, 31);
            foreach (var tj in new[] { 29.97, 29.94, 29.91, 29.88 }) await b.Tick(tj, 5);     // 51 s：低过 0.1 且在降（10 s 窗）

            await b.Duty(0, 15);                                // 回路要制冷 15 %（功率符号指向 TEC）
            await b.Tick(29.85, 1);
            await b.Tick(29.82, 3.5);
            await b.Tick(29.79, 5);
            // A6a：在加热棒侧、功率符号连续 3 s 以上要 TEC，但被控量低过目标且在降 → 不去 TEC（不出声）
            Assert.True(b.Io.Coils[0] && !b.Io.Coils[7], "低过目标且在降：符号要 TEC 也该留在加热棒\n" + b.Tail());
            Assert.Equal(0, b.Count("指向TEC"));

            await b.Tick(29.95, 5);                             // 不再低过 0.1：照符号接 TEC（B1，冷启动冷水机、通水后合 TEC）
            Assert.Equal(1, b.Count("已连续 3 s 指向TEC——合 TEC 功率线、断加热棒"));
            Assert.True(b.Io.Coils[7] && !b.Io.Coils[0], "该按符号接 TEC\n" + b.Tail());

            foreach (var tj in new[] { 30.00, 30.06, 30.12, 30.18 }) await b.Tick(tj, 5);      // 在 TEC 侧冲过目标且在升（不在加热棒侧，不进段）
            await b.Duty(0, -5);                                // 回路要加热 5 %
            await b.Tick(30.21, 1);
            await b.Tick(30.24, 3.5);
            await b.Tick(30.27, 5);
            // A6b：在 TEC 侧、功率符号连续 3 s 以上要加热，但被控量高过目标且在升 → 不走（不出声）
            Assert.True(b.Io.Coils[7] && !b.Io.Coils[0], "高过目标且在升：符号要加热也该留在 TEC\n" + b.Tail());
            Assert.Equal(0, b.Count("指向电加热"));
            Assert.Equal(0, b.Count("提前切 TEC"));

            await b.Tick(30.05, 5);                             // 不再高过 0.1：照符号回加热棒、关水
            Assert.Equal(1, b.Count("已连续 3 s 指向电加热——合加热棒、断 TEC 功率线"));
            Assert.True(b.Io.Coils[0] && !b.Io.Coils[7], "该按符号回加热棒\n" + b.Tail());
            b.SettleChiller();
            await b.Tick(30.05, 5);
            await b.Stop(0);
            await b.Tick(30.05, 1);
        });
    }

    [Fact]
    public async Task 金样4_手动开_功率符号要TEC挂问水_钳0期间低过目标且在降撤段_问题留着_又要TEC等同一个_60s没人答按打开_经挂起接通立段_最短制冷()
    {
        using var _ = Rd105HostControl.UseGainsDir(TempGainsDir());
        await Golden("e0-4-问水挂起-撤段-60s按打开-经挂起接通", async b =>
        {
            await b.Target(0, 30, askWater: true);
            await b.Tick(28.00, 31);
            await b.Duty(0, 15);                                // 回路要把夹套往下拉（被控量还在目标下面）
            await b.Tick(28.00, 1);
            await b.Tick(28.00, 3.5);
            // 问水：手动开的、开那一下没问过、水没开 → 挂询问（60 s 没人答按打开），等答复期间加热棒留着、不接 TEC
            Assert.Equal(1, b.Count("问操作人开不开冷却水"));
            Assert.NotNull(b.Water.Pending);
            Assert.Contains("等人答开不开冷却水", ((Tec.Driver.Abi.IHeatSource)b.T[0]).Regime);
            Assert.True(b.Io.Coils[0] && !b.Io.Coils[7]);

            await b.Duty(0, 0);                                 // 挂起期间回路钳 0
            foreach (var tj in new[] { 27.90, 27.80 }) await b.Tick(tj, 5);
            // A7'（符号进的挂起段）：被控量低过目标且在降 → 撤段，问题留着
            Assert.Equal(1, b.Count("等水期间被控量已回落"));
            Assert.NotNull(b.Water.Pending);
            foreach (var tj in new[] { 27.90, 28.00, 28.10 }) await b.Tick(tj, 5);   // 不降了

            await b.Duty(0, 15);
            await b.Tick(28.10, 1);
            await b.Tick(28.10, 3.5);
            Assert.Equal(1, b.Count("询问还挂着（同一个，不重问）"));
            Assert.Equal(1, b.Count("问操作人开不开冷却水"));

            await b.Duty(0, 0);
            await b.Tick(28.12, 10);
            await b.Tick(28.14, 10);
            await b.Tick(28.16, 10.5);                          // 问出来满 60 s
            // 60 s 没人答按打开：先开阀、开冷水机、通水后接 TEC；符号进法经挂起接通也立段（最短 60 s）
            Assert.Equal(1, b.Count("60 s 没人答，按「打开冷却水」办"));
            Assert.Equal(1, b.Count("符号进法经挂起接通——立制冷段（最短 60 s）"));
            Assert.True(b.Io.Coils[7] && !b.Io.Coils[0] && b.Io.Coils[4], "到时该开水接 TEC\n" + b.Tail());

            await b.Duty(0, -5);                                // 刚接上回路就要加热
            await b.Tick(28.16, 5);
            Assert.Equal(1, b.Count("（< 最短 60 s）"));
            for (var i = 0; i < 6; i++) await b.Tick(28.16, 10);   // 到 160.5 s：段龄 65 s，这一拍才开始给符号计时（段龄不满时每拍清）
            Assert.Equal(0, b.Count("制冷段结束（功率已连续 3 s 指向电加热"));
            Assert.True(b.Io.Coils[7], "符号计时还没满 3 s 不该扳\n" + b.Tail());
            await b.Tick(28.16, 10);
            Assert.Equal(1, b.Count("制冷段结束（功率已连续 3 s 指向电加热"));
            Assert.True(b.Io.Coils[0] && !b.Io.Coils[7], "段龄满了、功率要加热满 3 s 该回加热棒\n" + b.Tail());
            b.SettleChiller();
            await b.Tick(28.16, 5);
            await b.Stop(0);
            await b.Tick(28.16, 1);
        });
    }

    [Fact]
    public async Task 金样5_手动开_提前判据进段先挂问水_等水期间回落撤段_问题留着_答打开落地_再进间隔内不进_到点再进自动开水()
    {
        using var _ = Rd105HostControl.UseGainsDir(TempGainsDir());
        await Golden("e0-5-提前进段挂问-回落撤段-答打开-再进间隔", async b =>
        {
            await b.Target(0, 30, askWater: true);
            await RiseInto(b);
            Assert.Equal(1, b.Count("提前切 TEC，不等功率翻号"));
            Assert.Equal(1, b.Count("问操作人开不开冷却水"));
            Assert.True(b.Io.Coils[0] && !b.Io.Coils[7], "挂起期间加热棒继电器留着\n" + b.Tail());

            foreach (var tj in new[] { 30.10, 30.00, 29.90, 29.80 }) await b.Tick(tj, 5);
            // A7'（提前判据进的挂起段）：等水期间被控量回落 → 撤段、留在加热棒、问题留着；也记再进间隔
            Assert.Equal(1, b.Count("等水期间被控量已回落"));
            Assert.Equal(1, b.Count("60 s 内不再按提前判据进"));
            Assert.NotNull(b.Water.Pending);

            b.Answer(true);
            await b.Tick(29.80, 2);                             // 没人在等它：答复在采集拍里落地，记下「问过了」
            Assert.Equal(1, b.Count("询问有了结果：操作人选了打开冷却水"));
            Assert.Null(b.Water.Pending);

            foreach (var tj in new[] { 29.90, 30.00, 30.10, 30.20, 30.30, 30.40, 30.50, 30.60, 30.70, 30.80 }) await b.Tick(tj, 5);
            Assert.Equal(1, b.Count("再进间隔 60 s 内不按提前判据进"));
            Assert.Equal(1, b.Count("提前切 TEC，不等功率翻号"));
            await b.Tick(30.90, 5);
            await b.Tick(31.00, 5);
            // 到点再进：这次控温已经问过（答的打开），不再问，直接冷启动冷水机、通水后接 TEC
            Assert.Equal(2, b.Count("提前切 TEC，不等功率翻号"));
            Assert.Equal(1, b.Count("问操作人开不开冷却水"));
            Assert.True(b.Io.Coils[7] && b.Io.Coils[4], "到点该再进、自动开水\n" + b.Tail());

            await b.Stop(0);
            await b.Tick(31.00, 1);
            b.SettleChiller();
            await b.Tick(31.00, 5);
        });
    }

    [Fact]
    public async Task 金样6_冷水机刚冷启动过_功率符号要TEC挂起等最短间隔_又要加热满3s撤段_再要再挂_到点经挂起接通()
    {
        using var _ = Rd105HostControl.UseGainsDir(TempGainsDir());
        await Golden("e0-6-冷水机最短间隔-挂起撤段-到点接通", async b =>
        {
            await b.PanelWater(true);                           // 面板开一次又关：冷水机冷启动过（0 s）
            await b.PanelWater(false);
            b.SettleChiller();
            await b.Tick(null, 1);
            Assert.False(b.Io.Coils[4]);

            await b.Target(0, 30);                              // 正常下发（不问水）
            await b.Tick(28.00, 4);
            await b.Duty(0, 15);
            await b.Tick(28.00, 1);
            await b.Tick(28.00, 3.5);
            // 冷水机最短间隔 60 s 没到：挂起等（加热棒留着、钳 0），到点再开
            Assert.Equal(1, b.Count("刚开过（最短间隔 60 s）——先不开"));
            Assert.Contains("等冷水机最短间隔", ((Tec.Driver.Abi.IHeatSource)b.T[0]).Regime);

            await b.Duty(0, -5);
            await b.Tick(28.00, 1);
            await b.Tick(28.00, 1);
            await b.Tick(28.00, 2.5);
            // A7'（符号进的挂起段）：回路又要加热连续 3 s → 撤段
            Assert.Equal(1, b.Count("等冷水机间隔期间回路又要加热了"));

            await b.Duty(0, 15);
            await b.Tick(28.00, 1);
            await b.Tick(28.00, 3.5);
            Assert.Contains("等冷水机最短间隔", ((Tec.Driver.Abi.IHeatSource)b.T[0]).Regime);
            await b.Duty(0, 0);
            for (var i = 0; i < 4; i++) await b.Tick(28.00, 10);
            Assert.False(b.Io.Coils[7], "间隔没到不该接 TEC\n" + b.Tail());
            await b.Tick(28.00, 2);                             // 离冷启动满 60 s
            Assert.Equal(1, b.Count("符号进法经挂起接通——立制冷段（最短 60 s）"));
            Assert.True(b.Io.Coils[7] && b.Io.Coils[4], "间隔到点该开水接 TEC\n" + b.Tail());

            await b.Stop(0);
            Assert.Equal(1, b.Count("制冷段结束（停控"));
            await b.Tick(28.00, 1);
            b.SettleChiller();
            await b.Tick(28.00, 5);
        });
    }

    [Fact]
    public async Task 金样7_两路_A提前判据进段开水_B按符号接TEC共用一阀_A退段水不关_B也退了才关水()
    {
        using var _ = Rd105HostControl.UseGainsDir(TempGainsDir());
        await Golden("e0-7-两路共用一阀", async b =>
        {
            await b.Target(0, 30);
            await b.Target(1, 30);
            await b.Duty(1, -5);
            await RiseInto(b, tjB: 28.00);
            Assert.Equal(1, b.Count("提前切 TEC，不等功率翻号"));
            Assert.True(b.Io.Coils[7] && b.Io.Coils[4] && b.Io.Coils[1], "A 进段开水、B 还在加热棒\n" + b.Tail());

            await b.Duty(1, 15);                                // B 要冷：水开着，不问、不等间隔，直接按符号接
            await b.Tick(30.18, 1);
            await b.Tick(30.18, 3.5);
            Assert.Equal(1, b.Count("工位 B 双向挡：功率 −15.0 %（正加热 / 负制冷）已连续 3 s 指向TEC"));
            Assert.True(b.Io.Coils[6] && !b.Io.Coils[1], "B 该接 TEC\n" + b.Tail());

            await b.Duty(0, -5);                                // A 回路要加热；段龄满 60 s、平了才退
            for (var i = 0; i < 12; i++) await b.Tick(30.18, 5);    // 到 125.5 s：段龄 64.5 s，这一拍才开始给符号计时
            Assert.True(b.Io.Coils[7], "A 段龄满 60 s 之后符号计时还没满 3 s，不该扳\n" + b.Tail());
            await b.Tick(30.18, 5);
            Assert.Equal(1, b.Count("工位 A 双向挡：制冷段结束（功率已连续 3 s 指向电加热"));
            Assert.True(b.Io.Coils[0] && !b.Io.Coils[7], "A 该回加热棒\n" + b.Tail());
            Assert.True(b.Io.Coils[4], "B 还在制冷：水不能关\n" + b.Tail());
            Assert.Equal(0, b.Count("两路都不制冷了"));

            await b.Duty(1, -5);
            await b.Tick(30.18, 1);
            await b.Tick(30.18, 3.5);
            Assert.Equal(1, b.Count("工位 B 双向挡：功率 +5.0 %（正加热 / 负制冷）已连续 3 s 指向电加热"));
            Assert.Equal(1, b.Count("两路都不制冷了"));
            b.SettleChiller();
            await b.Tick(30.18, 5);
            Assert.False(b.Io.Coils[4]);
            await b.Stop(0);
            await b.Stop(1);
            await b.Tick(30.18, 1);
        });
    }
}
