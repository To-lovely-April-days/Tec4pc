using System.Reflection;
using Tec.Drivers.Rd105;
using TecControl.Core.Control;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 【本地改动】（2026-11-17，第 1 步复核之后用户选的 S2「看电流再决定」）新模式跑飞「放热」豁免要的「TEC 在出力」——样本、门槛、时窗的白盒（回路不转，
/// 直接调回路里那几个私有方法：确定、不靠墙钟）。真回路 + 假 RD105 的接线在 LiveOuterInControlTests（S2_ 那几条、B10 / B12）。
/// 定稿（scratchpad step1/safety/spec/README.md §4）：
/// · 合格样本：这一拍走了控制步（在控、没挂起、不在整定）+ 新外环在控 + TEC 真接着 + 这一拍制冷顶满 + 电流 ≥ I_通（或告警字报本路限流中）；
/// · I_通 = √(max(I_空, 0.001) × I_限)：I_空 = 最新一个空载样本往前 W 秒内空载样本的中位数（没有按 1 mA），I_限 = SETCURRENT（没推过按 0.5 A）；
/// · W = max(OuterLossGrace, 15 × 4 × Period)，缺省 30 s；TecDrawing = 有合格样本且离它 ≤ W；
/// · TEC 不接着当拍清掉合格样本：接回来以后旧样本不作保。
/// </summary>
public class LiveOuterS2CurrentTests
{
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private sealed class Bench
    {
        public readonly HostControlLoop Loop;
        public readonly object S;
        public readonly DateTime T0 = new(2026, 11, 17, 9, 0, 0, DateTimeKind.Local);

        public Bench(bool live = true, bool engaged = true)
        {
            var dev = new FakeRd105Device();
            dev.Open();                                                // 停控那一条要真写一下占空比 / 使能
            Loop = new HostControlLoop(new Rd105Link(dev).Controller);
            S = typeof(HostControlLoop).GetMethod("State", Any)!.Invoke(Loop, new object[] { 1 })!;
            Set("Active", true);
            Set("LiveInControl", live);
            Set("Strategy", ControlStrategy.Cascade);
            Set("TecEngaged", engaged);
        }

        public void Set(string field, object? v) => S.GetType().GetField(field, Any)!.SetValue(S, v);
        public T Get<T>(string field) => (T)S.GetType().GetField(field, Any)!.GetValue(S)!;
        private object? Call(string name, params object[] args) => typeof(HostControlLoop).GetMethod(name, Any)!.Invoke(Loop, args);

        public DateTime At(double s) => T0.AddSeconds(s);

        /// <summary>回路每 4 拍那一次读完电流：样本按路记下来。<paramref name="satLow"/> = 这一拍控制步里制冷那一侧顶满（回路在 RunControlStepAsync 里记 LiveSatLowAt）。</summary>
        public void Sample(double s, double amps, bool satLow = true, bool limiting = false)
        {
            if (satLow) Set("LiveSatLowAt", At(s));
            Call("NoteTecEngaged", S);                                 // 回路每拍都看一眼「TEC 真接着」
            Call("NoteTecCurrent", S, At(s), amps, limiting);
        }

        public void Engaged(bool on)
        {
            Set("TecEngaged", on);
            Call("NoteTecEngaged", S);
        }

        public bool Drawing(double s) => (bool)Call("TecDrawing", S, At(s))!;
        public double Threshold => (double)Call("TecDrawThreshold", S)!;
        public double W => (double)Call("TecDrawWindowSeconds")!;

        /// <summary>【本地改动】（2026-11-17 复核后）回路那一拍的跑飞判定（新外环在控）：<paramref name="wall"/> = 快照墙钟（只给跑飞判据），<paramref name="mono"/> = 回路的单调时刻（S2 的 W 秒按它）。
        /// 制冷那一侧顶满（贴下限）、釜内 <paramref name="tr"/> 比夹套 <paramref name="pv"/> 热。返回要不要停控。</summary>
        public bool Runaway(DateTime wall, DateTime mono, double innerSetpoint, double pv, double tr)
            => (bool)Call("LiveRunaway", 1, S, wall, mono, innerSetpoint, pv, tr, false, true, true)!;
    }

    [Fact]
    public void S2_墙钟往前跳60秒_豁免照样成立不停控_往回跳30秒以后TEC掉了_按单调钟W秒照停()
    {
        // 【本地改动】（2026-11-17 复核查实）原来 S2 拿快照墙钟（DateTime.Now）相减：对时 / 改钟往前跳 28 s 以上，豁免当拍失效、放热中把满冷关掉；
        // 往回跳 X 秒，旧样本要 X + W 秒才过期。现在样本、「这一拍制冷顶满」、判豁免都按回路的单调时刻，墙钟只给跑飞判据（原样）。
        // 这里按回路那一拍的先后喂：记「这一拍制冷顶满」→ 跑飞判定 → 每 4 拍读完电流记样本；夹套每拍往上 0.1 K（背离设定），跑飞判据压成 0.5 K / 1 s / 0.3 s
        var b = new Bench();
        b.Set("Runaway", new RunawayDetector(0.5, 1.0, 0.3));
        b.Loop.SetTecCurrentLimit(1, 5.0);                             // 门槛 √(0.001 × 5) = 0.07 A
        var mono0 = new DateTime(2000, 1, 3, 0, 0, 0);                  // 单调时间线（原点随便，跟墙钟差得远）
        double wall = 0, mono = 0, pv = 25;
        var k = 0;
        bool Beat(double dWall, double amps)
        {
            wall += dWall;
            mono += 0.5;
            pv += 0.1;
            var m = mono0.AddSeconds(mono);
            b.Set("LiveSatLowAt", m);
            var trip = b.Runaway(b.At(wall), m, 0, pv, 80);
            if (k++ % 4 == 0)
            {
                b.Set("LiveSatLowAt", m);
                typeof(HostControlLoop).GetMethod("NoteTecEngaged", Any)!.Invoke(null, new[] { b.S });
                typeof(HostControlLoop).GetMethod("NoteTecCurrent", Any)!.Invoke(b.Loop, new object[] { b.S, m, amps, false });
            }
            return trip;
        }

        for (var i = 0; i < 40; i++) Assert.False(Beat(0.5, 3.0), $"第 {i} 拍：电流 3 A 在出力，不该停");
        Assert.True(b.Get<bool>("LiveExoSaid"));                       // 跑飞判据确实判到了、是豁免放过的
        // 墙钟往前跳 60 s（一拍里）：单调时刻只过了 0.5 s，样本才 ≤ 2 s 老——照样豁免
        Assert.False(Beat(60.5, 3.0), "墙钟往前跳 60 s 当拍把豁免翻成停控了");
        for (var i = 0; i < 20; i++) Assert.False(Beat(0.5, 3.0), $"往前跳以后第 {i} 拍停了");
        // 墙钟往回跳 30 s，同时 TEC 掉了（读 0 A，不合格）：按单调钟最后一个合格样本过了 W（30 s）就停，不等 30 + 30 s
        var lastGood = mono - 0.5 * ((k - 1) % 4);                     // 最后一个合格样本的单调时刻（上一次记样本那一拍）
        var tripAt = double.NaN;
        var first = true;
        for (var i = 0; i < 200 && double.IsNaN(tripAt); i++)
        {
            if (Beat(first ? -29.5 : 0.5, 0.0)) tripAt = mono;
            first = false;
        }
        Assert.False(double.IsNaN(tripAt), "TEC 掉了 100 s 都没停");
        Assert.InRange(tripAt - lastGood, 30.0, 31.0);
    }

    [Fact]
    public void 时窗W_缺省30秒_宽限填0仍是15个电流采样_宽限放长跟着放长()
    {
        var b = new Bench();
        Assert.Equal(30.0, b.W, 9);                                    // max(OuterLossGrace 30, 15 × 4 × 0.5 = 30)
        b.Loop.OuterLossGrace = TimeSpan.Zero;
        Assert.Equal(30.0, b.W, 9);                                    // 宽限填 0：照样 15 个采样，豁免不会永远不成立
        b.Loop.Period = TimeSpan.FromMilliseconds(20);
        Assert.Equal(1.2, b.W, 9);                                     // 15 × 4 × 20 ms
        b.Loop.OuterLossGrace = TimeSpan.FromSeconds(45);
        Assert.Equal(45.0, b.W, 9);

        // 宽限 0、周期 0.5 s：样本 29 s 前的还算、31 s 前的不算（不是「W = 0 一拍就过期」）
        var c = new Bench();
        c.Loop.OuterLossGrace = TimeSpan.Zero;
        c.Sample(0, 3.0);
        Assert.True(c.Drawing(29));
        Assert.False(c.Drawing(31));
    }

    [Fact]
    public void 门槛_空载中位数乘限流开方_限流1A空载25mA是0点16A_5A是0点35A_没空载样本按1mA_没推过限流按0点5A()
    {
        var b = new Bench(engaged: false);
        Assert.Equal(Math.Sqrt(0.001 * 0.5), b.Threshold, 12);         // 什么都没有：1 mA × 0.5 A
        b.Loop.SetTecCurrentLimit(1, 1.0);
        for (var k = 0; k < 5; k++) b.Sample(k * 2, 0.025, satLow: false);
        Assert.Equal(Math.Sqrt(0.025 * 1.0), b.Threshold, 12);
        Assert.Equal("0.16", b.Threshold.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
        b.Loop.SetTecCurrentLimit(1, 5.0);
        Assert.Equal("0.35", b.Threshold.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
        b.Loop.SetTecCurrentLimit(1, double.NaN);                      // 不是有限正数不收
        b.Loop.SetTecCurrentLimit(1, 0);
        Assert.Equal("0.35", b.Threshold.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));

        // 中位数：一个离谱的空载读数不带偏；空载读 0 按 1 mA
        b.Sample(10, 5.0, satLow: false);
        Assert.Equal(Math.Sqrt(0.025 * 5.0), b.Threshold, 12);
        var z = new Bench(engaged: false);
        z.Loop.SetTecCurrentLimit(1, 5.0);
        z.Sample(0, 0.0, satLow: false);
        Assert.Equal(Math.Sqrt(0.001 * 5.0), z.Threshold, 12);

        // 只留最新一个空载样本往前 W 秒内的：40 s 前那批 0.025 过期，按最近的 0.2
        var w = new Bench(engaged: false);
        w.Loop.SetTecCurrentLimit(1, 1.0);
        w.Sample(0, 0.025, satLow: false);
        w.Sample(2, 0.025, satLow: false);
        w.Sample(40, 0.2, satLow: false);
        Assert.Equal(Math.Sqrt(0.2 * 1.0), w.Threshold, 12);
    }

    [Fact]
    public void 合格样本_TEC接着且这一拍制冷顶满且电流过门槛或限流中_才算在出力()
    {
        var b = new Bench();
        b.Loop.SetTecCurrentLimit(1, 5.0);                             // 门槛 √(0.001 × 5) = 0.071 A
        b.Sample(0, 3.0, satLow: false);                               // 这一拍没顶满：不算
        Assert.False(b.Drawing(0));
        b.Sample(2, 0.05);                                             // 顶满、但电流低于门槛：不算
        Assert.False(b.Drawing(2));
        b.Sample(4, 0.0, limiting: true);                              // 电流读 0、告警字报本路限流中：算
        Assert.True(b.Drawing(4));
        Assert.Equal(0.0, b.Get<double>("TecDrawAmps"));
        Assert.True(b.Get<bool>("TecDrawLimiting"));
        b.Sample(6, 3.0);                                              // 正常：算，记电流
        Assert.Equal(3.0, b.Get<double>("TecDrawAmps"));
        Assert.False(b.Get<bool>("TecDrawLimiting"));
        Assert.True(b.Drawing(6 + 30));                                // W = 30 s 以内还算
        Assert.False(b.Drawing(6 + 30.5));                             // 过了 W：TEC 中途掉了，W 之后不再豁免

        // 不在控 / 挂起 / 旧外环在控（新外环没锁定）时采的不算
        var o = new Bench(live: false);
        o.Sample(0, 3.0);
        Assert.False(o.Drawing(0));
        var p = new Bench();
        p.Set("Suspended", true);
        p.Sample(0, 3.0);
        Assert.False(p.Drawing(0));
        var q = new Bench();
        q.Set("Active", false);
        q.Sample(0, 3.0);
        Assert.False(q.Drawing(0));
    }

    [Fact]
    public void 加热棒侧读到的电流不算在出力_只进空载样本()
    {
        var b = new Bench(engaged: false);                             // 继电器在加热棒侧（TEC 不接着）
        for (var k = 0; k < 10; k++) b.Sample(k * 2, 3.0);             // 读到 3 A、这一拍也「顶满」
        Assert.False(b.Drawing(20));
        Assert.Null(b.Get<DateTime?>("TecDrawAt"));
        Assert.Equal(10, b.Get<Queue<(DateTime At, double Amps)>>("TecIdleAmps").Count);
        Assert.Empty(b.Get<Queue<(DateTime At, double Amps)>>("TecSideAmps"));
        // 接回 TEC、电流 0：没有作保的样本
        b.Engaged(true);
        b.Sample(22, 0.0);
        Assert.False(b.Drawing(22));
    }

    [Fact]
    public void 继电器离开TEC又接回_上一次接通时的样本不替这一次作保()
    {
        var b = new Bench();
        b.Sample(0, 3.0);
        Assert.True(b.Drawing(1));
        b.Engaged(false);                                              // 离开 TEC（W 还没过）
        Assert.False(b.Drawing(2));
        Assert.Empty(b.Get<Queue<(DateTime At, double Amps)>>("TecSideAmps"));
        b.Engaged(true);                                               // 接回来：上一次的 3 A 不作保
        Assert.False(b.Drawing(3));
        b.Sample(4, 0.0);
        Assert.False(b.Drawing(5));
        b.Sample(6, 3.0);                                              // 这一次自己有了合格样本才算
        Assert.True(b.Drawing(7));
    }

    [Fact]
    public void 开控锁定和停控都清掉合格样本()
    {
        var b = new Bench();
        b.Sample(0, 3.0);
        Assert.True(b.Drawing(1));
        b.Loop.StopChannelAsync(1).GetAwaiter().GetResult();
        Assert.Null(b.Get<DateTime?>("TecDrawAt"));
        b.Set("Active", true);
        b.Set("LiveInControl", true);
        b.Sample(2, 3.0);
        Assert.True(b.Drawing(3));
        var lockLive = typeof(HostControlLoop).GetMethod("LockLiveOuter", Any)!;
        lockLive.Invoke(b.Loop, new[] { b.S });
        Assert.Null(b.Get<DateTime?>("TecDrawAt"));
    }
}
