using TecControl.Core.Control;

namespace TecControl.Core.Tests;

/// <summary>
/// 一阶惯性 + 纯滞后热对象仿真（FOPDT）：
///   dT/dt = ( -(T - Tamb) + K·u(t-L) ) / tau
/// 这是热对象最常用的近似模型，纯滞后 L 使继电反馈产生稳定极限环。
/// </summary>
file sealed class ThermalPlant
{
    private readonly double _ambient, _gain, _tau, _dt;
    private readonly Queue<double> _delayLine;

    public ThermalPlant(double ambient, double gain, double tau, double deadTimeSeconds, double dt)
    {
        (_ambient, _gain, _tau, _dt) = (ambient, gain, tau, dt);
        _delayLine = new Queue<double>(
            Enumerable.Repeat(0.0, Math.Max(1, (int)Math.Round(deadTimeSeconds / dt))));
        Temperature = ambient;
    }

    public double Temperature { get; private set; }

    public void Step(double duty)
    {
        _delayLine.Enqueue(duty);
        var delayed = _delayLine.Dequeue();
        Temperature += _dt * (-(Temperature - _ambient) + _gain * delayed) / _tau;
    }
}

public class RelayAutoTunerTests
{
    private const double Dt = 0.5; // 控制周期 500ms

    private static RelayAutoTuner RunTuning(
        double setpoint, double ambient = 22, double gain = 0.2, double tau = 60, double deadTime = 5,
        double relayAmp = 30, double hysteresis = 0.05, int maxSteps = 20000, double bias = 0)
    {
        var tuner = new RelayAutoTuner(setpoint, relayAmp, hysteresis, bias);
        var plant = new ThermalPlant(ambient, gain, tau, deadTime, Dt);
        var t0 = new DateTime(2026, 1, 1);

        for (var step = 0; step < maxSteps; step++)
        {
            var duty = tuner.Process(t0.AddSeconds(step * Dt), plant.Temperature);
            if (tuner.State is AutoTuneState.Succeeded or AutoTuneState.Failed) break;
            plant.Step(duty);
        }
        return tuner;
    }

    [Fact]
    public void DeepColdSetpoint_SucceedsWithCorrectBias_AndSelfCentersFromZero()
    {
        // 深冷场景：维持设定值本身需要 −60% 稳态输出（对象 ambient 22、gain 0.2）。
        // 偏置正确（−60 ± 20）→ 振荡对称，直接成功；
        // 偏置为零 → 制冷半周 −20% 远够不着，自寻中把中心一步步下挪后同样成功。
        const double setpoint = 10;

        var biased = RunTuning(setpoint, relayAmp: 20, bias: -60);
        Assert.Equal(AutoTuneState.Succeeded, biased.State);
        Assert.True(biased.Result!.UltimateGainKu > 1,
            $"Ku 应为正常量级，实际 {biased.Result.UltimateGainKu:F2}");
        Assert.True(biased.Result.UltimatePeriodTuSeconds is > 5 and < 300,
            $"Tu 量级异常：{biased.Result.UltimatePeriodTuSeconds:F1}s");

        var zeroBias = RunTuning(setpoint, relayAmp: 20, maxSteps: 80000);
        Assert.Equal(AutoTuneState.Succeeded, zeroBias.State);
        Assert.True(zeroBias.BiasPercent < -30,
            $"继电中心应已自寻中下移，实际 {zeroBias.BiasPercent:F0}%");
    }

    [Fact]
    public void StaleTooDeepBias_SelfCentersAndSucceeds()
    {
        // 现场实测（2026-07-29 −30℃）：历史存档偏置 −66%，当日实际稳态只需 −49%
        // （换了冷水机工况）。弱制冷半周 −51% 仍强于稳态需求，温度以 0.08℃/min
        // 缓慢下漂、永不回升穿越设定值，旧逻辑 Seeking 永久挂起（0 个周期）。
        // 新逻辑应通过自寻中把继电中心逐步上挪，最终正常起振并成功。
        // 对象 ambient 22、gain 0.245 → 设定 10℃ 的稳态输出 (10-22)/0.245 ≈ -49%。
        var tuner = RunTuning(setpoint: 10, gain: 0.245, relayAmp: 15, bias: -66,
            maxSteps: 40000);

        Assert.Equal(AutoTuneState.Succeeded, tuner.State);
        Assert.True(tuner.BiasPercent > -66,
            $"继电中心应已上挪，实际 {tuner.BiasPercent:F0}%");
        Assert.True(tuner.Result!.UltimateGainKu > 0.5,
            $"Ku 量级异常：{tuner.Result.UltimateGainKu:F2}");
    }

    [Fact]
    public void BiasedRelay_MatchesZeroBiasResult_WhenNoLoadNeeded()
    {
        // 偏置不进 Ku 公式：对无稳态负荷的对象（设定值=环境），带小偏置与零偏置
        // 测得的 Ku/Tu 应基本一致（对称性未被破坏时偏置只是平移工作点）。
        var a = RunTuning(22.5, relayAmp: 30);
        var b = RunTuning(22.5, relayAmp: 30, bias: 3);
        Assert.Equal(AutoTuneState.Succeeded, a.State);
        Assert.Equal(AutoTuneState.Succeeded, b.State);
        Assert.True(Math.Abs(a.Result!.UltimateGainKu - b.Result!.UltimateGainKu)
                    / a.Result.UltimateGainKu < 0.25,
            $"Ku 偏差过大：无偏置 {a.Result.UltimateGainKu:F2}，带偏置 {b.Result.UltimateGainKu:F2}");
    }

    [Fact]
    public void Relay_SwitchesWithHysteresis()
    {
        var tuner = new RelayAutoTuner(setpointC: 25, relayAmplitudePercent: 30, hysteresisC: 0.1);
        var t0 = new DateTime(2026, 1, 1);

        // 低于设定值 → 正输出（加热）
        Assert.Equal(30, tuner.Process(t0, 24.0), 6);
        // 未越过 设定+回差 → 保持加热
        Assert.Equal(30, tuner.Process(t0.AddSeconds(1), 25.05), 6);
        // 越过 设定+回差 → 切为制冷
        Assert.Equal(-30, tuner.Process(t0.AddSeconds(2), 25.2), 6);
        // 回落但未低于 设定-回差 → 保持制冷
        Assert.Equal(-30, tuner.Process(t0.AddSeconds(3), 24.95), 6);
        // 低于 设定-回差 → 切回加热
        Assert.Equal(30, tuner.Process(t0.AddSeconds(4), 24.8), 6);
    }

    [Fact]
    public void Tuning_OnFopdtPlant_SucceedsWithPlausibleResult()
    {
        var tuner = RunTuning(setpoint: 26);

        Assert.Equal(AutoTuneState.Succeeded, tuner.State);
        var r = tuner.Result!;

        // FOPDT 对象继电振荡周期量级约为 2~6 倍纯滞后（L=5s），给宽裕判界
        Assert.InRange(r.UltimatePeriodTuSeconds, 5, 120);
        Assert.True(r.UltimateGainKu > 0);
        Assert.True(r.OscillationAmplitudeC > 0.05, "振幅应大于回差");
        Assert.True(r.Conservative.Kp > 0 && r.Conservative.Ki > 0 && r.Conservative.Kd > 0);
        // 平稳型必须比快速型温和
        Assert.True(r.Conservative.Kp < r.Fast.Kp);
        Assert.True(r.Conservative.Ki < r.Fast.Ki);
    }

    [Fact]
    public void TunedConservativeGains_StabilizeClosedLoop()
    {
        const double ambient = 22, setpoint = 26, gain = 0.2, tau = 60, deadTime = 5;

        var tuner = RunTuning(setpoint, ambient, gain, tau, deadTime);
        Assert.Equal(AutoTuneState.Succeeded, tuner.State);
        var g = tuner.Result!.Conservative;

        // 用整定出的参数闭环仿真 30 分钟（从环境温度冷启动）
        var pid = new PidController();
        pid.Configure(g.Kp, g.Ki, g.Kd, -100, 100, derivativeFilterSeconds: 2);
        var plant = new ThermalPlant(ambient, gain, tau, deadTime, Dt);

        var tail = new List<double>();
        var steps = (int)(30 * 60 / Dt);
        for (var i = 0; i < steps; i++)
        {
            var duty = pid.Update(setpoint, plant.Temperature, Dt);
            plant.Step(duty);
            if (i > steps - (int)(5 * 60 / Dt)) tail.Add(plant.Temperature); // 最后 5 分钟
        }

        // 最后 5 分钟内应稳定在设定值 ±0.1℃ 内
        Assert.True(tail.All(t => Math.Abs(t - setpoint) < 0.1),
            $"整定参数闭环未稳定：最后 5 分钟温度范围 {tail.Min():F4}~{tail.Max():F4}℃，设定 {setpoint}℃");
    }

    [Fact]
    public void UnreachableSetpoint_FailsEarlyWithDiagnostic()
    {
        // 现场实测场景：设定 -10℃，但 30% 继电幅值下系统只能到 -6℃ 左右便停住
        // （实测曾在 Seeking 阶段空耗 31 分钟）——应提前失败并给出可执行提示
        var tuner = new RelayAutoTuner(setpointC: -10, relayAmplitudePercent: 30, hysteresisC: 0.05);
        var t0 = new DateTime(2026, 1, 1);
        var pv = 20.0;
        const double floor = -6.0;

        var elapsed = 0.0;
        while (tuner.State == AutoTuneState.Seeking && elapsed < 3600)
        {
            tuner.Process(t0.AddSeconds(elapsed), pv);
            // 指数趋近极限温度（时间常数约 8 分钟）
            pv += (floor - pv) * (Dt / 480.0);
            elapsed += Dt;
        }

        Assert.Equal(AutoTuneState.Failed, tuner.State);
        Assert.Contains("继电幅值", tuner.FailReason);
        Assert.Contains("设定值", tuner.FailReason);
        Assert.True(elapsed < 3600, "应远早于 60 分钟超时前失败");
    }

    [Fact]
    public void SlowButProgressingApproach_DoesNotFalselyStall()
    {
        // 缓慢但持续朝设定值前进（0.1℃/min）不应被误判为停滞
        var tuner = new RelayAutoTuner(setpointC: 10, relayAmplitudePercent: 30, hysteresisC: 0.05);
        var t0 = new DateTime(2026, 1, 1);
        var pv = 25.0;
        for (var s = 0.0; s < 1800 && pv > 12; s += Dt)
        {
            tuner.Process(t0.AddSeconds(s), pv);
            pv -= 0.1 * Dt / 60.0;
        }
        Assert.Equal(AutoTuneState.Seeking, tuner.State);
    }

    [Fact]
    public void Tuning_ReportsProgressCycles()
    {
        var tuner = RunTuning(setpoint: 26);
        Assert.True(tuner.CompletedCycles >= 3, $"应至少完成 3 个测量周期，实际 {tuner.CompletedCycles}");
    }

    // ---------- 只加热执行器（电加热棒，输出下限 0） ----------

    [Fact]
    public void HeatOnlyFloor_TunesWithoutNegativeOutput()
    {
        // 电加热棒场景：输出只能 0~90，"弱加热半周"靠自然散热降温。
        // 对象 ambient 22、gain 0.2 → 维持 30℃ 稳态需 40%，继电 40±20 全程为正。
        var tuner = new RelayAutoTuner(setpointC: 30, relayAmplitudePercent: 20,
            hysteresisC: 0.05, biasPercent: 40,
            outputCeilingPercent: 90, outputFloorPercent: 0);
        var plant = new ThermalPlant(ambient: 22, gain: 0.2, tau: 60, deadTimeSeconds: 5, dt: Dt);
        var t0 = new DateTime(2026, 1, 1);

        for (var step = 0; step < 20000; step++)
        {
            var duty = tuner.Process(t0.AddSeconds(step * Dt), plant.Temperature);
            Assert.True(duty >= 0, $"只加热整定第 {step} 步输出了负占空比 {duty:F1}%");
            if (tuner.State is AutoTuneState.Succeeded or AutoTuneState.Failed) break;
            plant.Step(duty);
        }

        Assert.Equal(AutoTuneState.Succeeded, tuner.State);
        Assert.True(tuner.Result!.UltimateGainKu > 0.5,
            $"Ku 量级异常：{tuner.Result.UltimateGainKu:F2}");
    }

    [Fact]
    public void HeatOnlyFloor_SelfCentersFromZeroBias()
    {
        // 初始偏置 0 会被下限抬到 幅值（保证 偏置−幅值 ≥ 0），再靠自寻中爬到稳态输出附近
        var tuner = new RelayAutoTuner(setpointC: 30, relayAmplitudePercent: 15,
            hysteresisC: 0.05, biasPercent: 0,
            outputCeilingPercent: 90, outputFloorPercent: 0);
        Assert.True(tuner.BiasPercent >= 15, $"偏置初值应被抬到幅值之上，实际 {tuner.BiasPercent:F0}%");

        var plant = new ThermalPlant(ambient: 22, gain: 0.2, tau: 60, deadTimeSeconds: 5, dt: Dt);
        var t0 = new DateTime(2026, 1, 1);
        for (var step = 0; step < 80000; step++)
        {
            var duty = tuner.Process(t0.AddSeconds(step * Dt), plant.Temperature);
            Assert.True(duty >= 0);
            if (tuner.State is AutoTuneState.Succeeded or AutoTuneState.Failed) break;
            plant.Step(duty);
        }
        Assert.Equal(AutoTuneState.Succeeded, tuner.State);
    }

    [Fact]
    public void HeatOnlyFloor_OversizedAmplitude_NeverLeavesOutputBounds()
    {
        // 幅值 60 超过上限 90 的一半：可行偏置域为空 → 偏置收缩到量程中点 45，
        // 摆动两端被边界截住——宁可整定精度打折，也不许输出越过 [0, 上限]
        var tuner = new RelayAutoTuner(setpointC: 30, relayAmplitudePercent: 60,
            hysteresisC: 0.05, biasPercent: 0,
            outputCeilingPercent: 90, outputFloorPercent: 0);
        var t0 = new DateTime(2026, 1, 1);

        Assert.InRange(tuner.Process(t0, 22.0), 0, 90);                // 强半周：45+60 截到 90
        Assert.InRange(tuner.Process(t0.AddSeconds(1), 30.2), 0, 90);  // 弱半周：45−60 截到 0
    }
}
