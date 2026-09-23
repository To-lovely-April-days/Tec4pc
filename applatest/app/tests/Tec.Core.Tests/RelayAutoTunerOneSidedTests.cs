using TecControl.Core.Control;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 继电器法自整定碰上单方向执行器（只加热的加热棒 / 只制冷的 TEC）——纯算法，拿一个一阶对象加执行器滞后来喂。
/// 锁的是 0328 那几处本地改动：
/// · 过冲还在往外走的那一段不算「推不动」（原来停滞窗口起点留在换向之前，越过设定值两分钟就判失败——
///   现场加热棒 50 ℃ 冲到 53.6 ℃ 还没回落就停了）；
/// · 首次逼近慢（二三十分钟）但一直有进展，不再每 10 min 把偏置往外推（推大了过冲更大）；
/// · 输出 0 的那半周靠自然散热 / 回温，给得起时间，能整出结果；
/// · 真回不来（没有自然散热 / 平衡点在这一侧）时说的是「自然平衡点」那回事，不是叫人加幅值；
///   一直往外走的（只制冷 TEC 整比室温高的温度）一刻钟就判，不等满 1 h。
/// 对象参数是随手定的量级（升温每分钟零点几度、自然散热每分钟几十毫度），不是哪台机器的实测。
/// </summary>
public class RelayAutoTunerOneSidedTests
{
    /// <summary>
    /// 一阶热对象：dT/dt = g·q − k·(T − Ta)，执行器 q 以时间常数 lag 跟随输出 u（加热棒 / SSR 的热惯性）。
    /// 返回整定器最终状态、用了多少秒，以及第一次越过设定值（继电换向）那一拍的时刻与偏置。
    /// </summary>
    private static (RelayAutoTuner Tuner, double Seconds, double FirstSwitch, double BiasAtSwitch) Run(
        RelayAutoTuner tuner, double t0, double gain, double kLoss, double ambient, double lagSeconds, double maxSeconds)
    {
        var start = new DateTime(2026, 1, 1);
        var t = t0;
        var q = 0.0;
        double first = double.NaN, bias = double.NaN;
        for (var s = 0; s < maxSeconds; s++)
        {
            var u = tuner.Process(start.AddSeconds(s), t);
            if (tuner.State is AutoTuneState.Succeeded or AutoTuneState.Failed) return (tuner, s, first, bias);
            if (double.IsNaN(first) && t > tuner.SetpointC + tuner.HysteresisC) { first = s; bias = tuner.BiasPercent; }
            q += (u - q) / lagSeconds;
            t += gain * q - kLoss * (t - ambient);
        }
        return (tuner, maxSeconds, first, bias);
    }

    // 加热棒 50 ℃：继电 [0, 90] 里摆，幅值 20
    private static RelayAutoTuner Heater() => new(50, relayAmplitudePercent: 20, hysteresisC: 0.05, biasPercent: 0,
                                                  outputCeilingPercent: 90, outputFloorPercent: 0);

    [Fact]
    public void 加热棒_过冲期间不判推不动_关着的半周等自然散热_整出结果()
    {
        // 滞后 90 s：越过设定值后还要再冲两三分钟才回头——原来的停滞窗口在这儿就判失败了
        var (tuner, secs, _, _) = Run(Heater(), t0: 25, gain: 1.85e-4, kLoss: 3.6e-5, ambient: 25, lagSeconds: 90, maxSeconds: 6 * 3600);
        Assert.True(tuner.State == AutoTuneState.Succeeded, $"没整出来：{tuner.FailReason}（{secs:0} s）");
        var r = tuner.Result!;
        Assert.True(r.UltimateGainKu > 0 && r.UltimatePeriodTuSeconds > 0, $"Ku={r.UltimateGainKu} Tu={r.UltimatePeriodTuSeconds}");
        // 关着的那半周是几分钟的量级，整周期明显长过主动半周期上限的一半
        Assert.True(r.UltimatePeriodTuSeconds > 300, $"Tu={r.UltimatePeriodTuSeconds:0} s");
    }

    [Fact]
    public void 首次逼近慢但一直有进展_不推偏置()
    {
        // 从 25 ℃ 升到 50 ℃ 要半个多小时：原来每 10 min 把偏置往上推一格，越过设定值时偏置已经 40 %，
        // 关那半周输出还是 20 %，过冲到 51.6 ℃
        var (_, _, first, bias) = Run(Heater(), t0: 25, gain: 1.85e-4, kLoss: 3.6e-5, ambient: 25, lagSeconds: 90, maxSeconds: 6 * 3600);
        Assert.True(first > 1800, $"逼近用了 {first:0} s——对象太快，这条测不到");
        Assert.Equal(20, bias);          // 偏置还在起点（幅值 20、下限 0 → 可行域最低 20）
    }

    [Fact]
    public void 没有自然散热_关着的半周回不来_说离平衡点太近_不叫人加幅值()
    {
        var (tuner, secs, first, _) = Run(Heater(), t0: 25, gain: 1.85e-4, kLoss: 0, ambient: 25, lagSeconds: 90, maxSeconds: 6 * 3600);
        Assert.Equal(AutoTuneState.Failed, tuner.State);
        Assert.Contains("输出已经是 0", tuner.FailReason);
        Assert.Contains("自然平衡点", tuner.FailReason);
        Assert.Contains("提高继电幅值没有用", tuner.FailReason);
        // 过冲停住之后 5 min 没往回走就判，不用等满被动半周上限（1 h）
        Assert.True(secs - first < 30 * 60, $"越过设定值后 {secs - first:0} s 才判");
    }

    [Fact]
    public void 只制冷TEC整比室温高的温度_关着时只会往下走_一刻钟判_叫用加热棒()
    {
        // 「TEC 加热」不启用：TEC 只制冷 [−90, 0]。整 50 ℃：夹套从 60 降下来，弱制冷那半周（输出 0）只会接着往 25 ℃ 走，
        // 永远升不回 50——不能等满 1 h
        var tuner = new RelayAutoTuner(50, 20, 0.05, 0, outputCeilingPercent: 0, outputFloorPercent: -90);
        var (t, secs, _, _) = Run(tuner, t0: 60, gain: 1.85e-4, kLoss: 3.6e-5, ambient: 25, lagSeconds: 60, maxSeconds: 6 * 3600);
        Assert.Equal(AutoTuneState.Failed, t.State);
        Assert.Contains("不制冷，只能靠自然回温", t.FailReason);
        Assert.Contains("用加热棒整", t.FailReason);
        Assert.Contains("提高继电幅值没有用", t.FailReason);
        Assert.True(secs < 45 * 60, $"{secs:0} s 才判");
    }

    [Fact]
    public void 只制冷_推不下去时说把设定值改到极限之上()
    {
        // 只制冷 [−90, 0]，幅值 20；制冷能力太弱（0 以下平衡在 30 ℃ 以上），设定 10 ℃ 够不着
        var tuner = new RelayAutoTuner(10, 20, 0.05, 0, outputCeilingPercent: 0, outputFloorPercent: -90);
        var (t, _, _, _) = Run(tuner, t0: 25, gain: 1e-6, kLoss: 1e-3, ambient: 25, lagSeconds: 10, maxSeconds: 4 * 3600);
        Assert.Equal(AutoTuneState.Failed, t.State);
        Assert.Contains("下限", t.FailReason);
        Assert.Contains("极限温度之上", t.FailReason);
    }
}
