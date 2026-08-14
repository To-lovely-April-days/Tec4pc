using TecControl.Core.Control;

namespace TecControl.Core.Tests;

/// <summary>
/// 双容对象仿真：TEC/加热器作用于腔体壁（快，实测 τ≈16min、纯滞后 48s），
/// 内部温度经壁面热耦合跟随（实测 τ≈8.6min），另有环境漏热。
/// 用于验证串级（外环控内部温度、内环控壁温）确实优于单环+固定偏置。
/// </summary>
file sealed class TwoCapacityPlant
{
    private const double GainWall = 1.14;      // ℃/%（壁面稳态增益）
    private const double TauWall = 972;        // s
    private const double TauCore = 516;        // s（内部对壁面的跟随时间常数）

    private readonly double _ambient, _dt, _leak;
    private readonly Queue<double> _delay;

    /// <param name="leakRatio">内部的环境漏热占比。加大它等效于跑更低的温度（温差更大）。</param>
    public TwoCapacityPlant(double ambient = 29.0, double dt = 0.5, double leakRatio = 0.12)
    {
        (_ambient, _dt, _leak) = (ambient, dt, leakRatio);
        _delay = new Queue<double>(Enumerable.Repeat(0.0, Math.Max(1, (int)(48 / dt))));
        Wall = Core = ambient;
    }

    public double Wall { get; private set; }
    public double Core { get; private set; }

    /// <summary>内部附加热源（模拟放热反应），单位 ℃/min。</summary>
    public double CoreHeatCPerMin { get; set; }

    /// <summary>维持内部温度所需的壁温（稳态解），即"稳态偏置"的物理来源。</summary>
    public double SteadyWallFor(double core) => core - _leak * (_ambient - core);

    /// <summary>维持该壁温所需的稳态输出（%）。</summary>
    public double SteadyDutyFor(double wall) => (wall - _ambient) / GainWall;

    /// <summary>把对象直接置于某内部温度的稳态（模拟"已经在温度上再启动"），返回所需稳态输出。</summary>
    public double SettleTo(double core)
    {
        Core = core;
        Wall = SteadyWallFor(core);
        var u = SteadyDutyFor(Wall);
        _delay.Clear();
        for (var i = 0; i < Math.Max(1, (int)(48 / _dt)); i++) _delay.Enqueue(u);
        return u;
    }

    public void Step(double dutyPercent)
    {
        _delay.Enqueue(dutyPercent);
        var u = _delay.Dequeue();
        Wall += _dt * ((_ambient + GainWall * u) - Wall) / TauWall;
        var pull = (Wall - Core) / TauCore + _leak * (_ambient - Core) / TauCore;
        Core += _dt * pull + CoreHeatCPerMin * _dt / 60.0;
    }
}

public class CascadeControlTests
{
    private const double Dt = 0.5;

    /// <summary>单环控壁温：内部温度存在稳态偏差，且随内部热源变化而漂移。</summary>
    private static (double CoreErr, double WallErr) RunDirect(double setpoint, double coreHeat, int minutes)
    {
        var plant = new TwoCapacityPlant();
        var pid = new PidController();
        pid.Configure(12.893, 0.0314, 382.444, -60, 60, 2);
        plant.CoreHeatCPerMin = coreHeat;

        for (var t = 0.0; t < minutes * 60; t += Dt)
            plant.Step(pid.Update(setpoint, plant.Wall, Dt));

        return (plant.Core - setpoint, plant.Wall - setpoint);
    }

    /// <summary>串级：外环控内部温度、输出壁温设定值偏置；内环控壁温。</summary>
    private static (double CoreErr, double WallSp) RunCascade(double setpoint, double coreHeat, int minutes)
    {
        var plant = new TwoCapacityPlant();
        var inner = new PidController();
        inner.Configure(12.893, 0.0314, 382.444, -60, 60, 2);
        var outer = new PidController();
        outer.Configure(kp: 1.5, ki: 0.0025, kd: 0, outputMin: -15, outputMax: 15, derivativeFilterSeconds: 10);
        plant.CoreHeatCPerMin = coreHeat;

        var wallSp = setpoint;
        var lastOuter = 0.0;
        for (var t = 0.0; t < minutes * 60; t += Dt)
        {
            if (t - lastOuter >= 2.0)   // 外环 2s 更新一次（内外环 4:1）
            {
                var innerSat = Math.Abs(inner.LastP + inner.LastI + inner.LastD) >= 60 - 1e-6;
                wallSp = setpoint + outer.Update(setpoint, plant.Core, t - lastOuter, freezeIntegral: innerSat);
                lastOuter = t;
            }
            plant.Step(inner.Update(wallSp, plant.Wall, Dt));
        }
        return (plant.Core - setpoint, wallSp);
    }

    [Fact]
    public void Direct_LeavesSteadyStateOffsetOnCore()
    {
        // 单环把壁温控准，但内部因漏热存在稳态偏差
        var (coreErr, wallErr) = RunDirect(-10, coreHeat: 0, minutes: 180);
        Assert.True(Math.Abs(wallErr) < 0.05, $"壁温应控准，实际偏差 {wallErr:F3}℃");
        Assert.True(Math.Abs(coreErr) > 0.15, $"内部应存在可观测偏差，实际 {coreErr:F3}℃");
    }

    [Fact]
    public void Cascade_EliminatesCoreOffset()
    {
        var (coreErr, _) = RunCascade(-10, coreHeat: 0, minutes: 180);
        Assert.True(Math.Abs(coreErr) < 0.05,
            $"串级应消除内部稳态偏差，实际 {coreErr:F3}℃");
    }

    [Fact]
    public void Cascade_RejectsInternalHeatLoad()
    {
        // 内部持续放热 0.05℃/min（模拟放热反应）：单环无法察觉，串级应主动补偿
        const double heat = 0.05;
        var (directErr, _) = RunDirect(-10, heat, 180);
        var (cascadeErr, wallSp) = RunCascade(-10, heat, 180);

        Assert.True(Math.Abs(cascadeErr) < Math.Abs(directErr) / 3,
            $"串级抑制放热应显著优于单环：单环 {directErr:F3}℃，串级 {cascadeErr:F3}℃");
        Assert.True(wallSp < -10, $"串级应自动压低壁温设定值以抵消放热，实际 {wallSp:F2}℃");
    }

    [Fact]
    public void InnerSetpointClampProtectsPlant()
    {
        // 外环输出偏置受限幅保护，不会把壁温设定值推到极端
        var outer = new PidController();
        outer.Configure(5, 0.05, 0, -15, 15, 10);
        for (var i = 0; i < 2000; i++) outer.Update(-40, 20, 0.5);   // 巨大持续误差
        var bias = outer.Update(-40, 20, 0.5);
        Assert.True(bias >= -15 && bias <= 15, $"外环输出应被限幅，实际 {bias:F2}");
    }

    /// <summary>串级运行诊断结果（判据一律按 ±0.05℃ 计）。</summary>
    private readonly record struct CascadeDiag(
        double SettleMin, double Undershoot, double FinalErr, double FinalBias, double FinalLimit);

    /// <summary>
    /// 串级仿真诊断：进入 ±0.05℃ 并不再离开的时刻、最大下冲、末态偏差与末态偏置。
    /// <paramref name="guard"/> 非空时启用偏置限幅自动扩程。
    /// </summary>
    private static CascadeDiag RunCascadeDiag(double setpoint, double outerKp, double outerKi,
        double maxBias, int minutes, double outerPreset = 0,
        OuterBiasRangeGuard? guard = null, bool startAtSetpoint = false, double leak = 0.12,
        double? crossingDischargeTo = null, double approachBand = 0, double trackingBoost = 0,
        double clampBand = 0, double clampMargin = 0.3)
    {
        var plant = new TwoCapacityPlant(leakRatio: leak);
        var inner = new PidController();
        inner.Configure(12.893, 0.0314, 382.444, -60, 60, 2);

        // 已在温度上重启：对象置于稳态，内环积分同样预置到稳态输出，
        // 这样两组对比只差"外环积分有没有预置"这一项。
        if (startAtSetpoint) inner.PresetIntegral(plant.SettleTo(setpoint));

        var outer = new PidController();
        outer.Configure(outerKp, outerKi, 0, -maxBias, maxBias, 30);
        outer.PresetIntegral(outerPreset);

        var wallSp = setpoint + outerPreset;
        var lastOuter = 0.0;
        var settle = double.NaN;
        var undershoot = 0.0;
        var crossingArmed = true;
        var approachArmed = true;
        var lastErr = double.NaN;

        for (var t = 0.0; t < minutes * 60; t += Dt)
        {
            if (t - lastOuter >= 2.0)
            {
                var dt = t - lastOuter;
                var innerSat = Math.Abs(inner.LastP + inner.LastI + inner.LastD) >= 60 - 1e-6;
                var bias = outer.Update(setpoint, plant.Core, dt, freezeIntegral: innerSat);

                // 腔体跟踪缺口前馈（与 HostControlLoop 相同的实现）
                var boost = trackingBoost <= 0
                    ? 0
                    : Math.Clamp(trackingBoost * (setpoint + bias - plant.Wall), -3, 3);

                // 落地偏置钳位（与 HostControlLoop 相同的实现；参考值同放电参考）
                var effBias = bias;
                if (clampBand > 0 && crossingArmed && crossingDischargeTo is { } clampRef
                    && Math.Abs(setpoint - plant.Core) <= clampBand)
                {
                    effBias = setpoint - plant.Core < 0
                        ? Math.Max(bias, clampRef - clampMargin)
                        : Math.Min(bias, clampRef + clampMargin);
                }

                wallSp = setpoint + effBias + boost;
                lastOuter = t;

                // 两段式积分放电（与 HostControlLoop 相同的判据与动作）
                var outerErr = setpoint - plant.Core;
                if (crossingDischargeTo is { } steadyI)
                {
                    if (approachArmed && approachBand > 0 && Math.Abs(outerErr) <= approachBand)
                    {
                        approachArmed = false;
                        if (Math.Abs(outer.LastI - steadyI) > 0.25)
                            outer.PresetIntegral(steadyI);
                    }
                    if (crossingArmed && !double.IsNaN(lastErr)
                        && Math.Abs(lastErr) > 1e-12 && lastErr * outerErr <= 0)
                    {
                        crossingArmed = false;
                        if (Math.Abs(outer.LastI - steadyI) > 0.25)
                            outer.PresetIntegral(steadyI);
                    }
                }
                lastErr = outerErr;

                if (guard?.Update(dt, bias, outer.OutputMax, innerSat, outerErr) is { } widened)
                    outer.SetOutputLimits(-widened, widened);
            }
            plant.Step(inner.Update(wallSp, plant.Wall, Dt));

            var err = plant.Core - setpoint;
            undershoot = Math.Min(undershoot, err);
            // 一旦超出 ±0.05℃ 就重新计时，因此最终留下的是"进入后不再离开"的时刻
            if (Math.Abs(err) > 0.05) settle = double.NaN;
            else if (double.IsNaN(settle)) settle = t / 60.0;
        }
        return new CascadeDiag(settle, undershoot, plant.Core - setpoint,
            wallSp - setpoint, outer.OutputMax);
    }

    [Fact]
    public void TunedOuterGains_BeatOriginalOnBothSettlingAndUndershoot()
    {
        // 现场实测问题：外环 1.5/0.0025/±15 下冲后回收极慢（实机 52 分钟才回到 ±0.1℃）。
        // 扫参得到的 2.5/0.008/±8 应在到温时间与下冲两个指标上同时占优。
        var original = RunCascadeDiag(-10, 1.5, 0.0025, 15, minutes: 240);
        var tuned = RunCascadeDiag(-10, 2.5, 0.008, 8, minutes: 240);

        Assert.True(tuned.SettleMin < original.SettleMin * 0.75,
            $"新参数应显著更早稳定：新 {tuned.SettleMin:F1}min，旧 {original.SettleMin:F1}min");
        Assert.True(Math.Abs(tuned.Undershoot) < Math.Abs(original.Undershoot),
            $"新参数应减小下冲：新 {tuned.Undershoot:F3}℃，旧 {original.Undershoot:F3}℃");
        Assert.True(Math.Abs(tuned.FinalErr) < 0.05, $"末态应落在 ±0.05℃ 内，实际 {tuned.FinalErr:F3}℃");
    }

    [Fact]
    public void LargerBiasLimit_TradesUndershootForNothing()
    {
        // 下冲主要由偏置限幅决定，而非 Ki：同样的增益下放大限幅只会换来更大的下冲
        var tight = RunCascadeDiag(-10, 1.5, 0.008, 6, minutes: 240);
        var loose = RunCascadeDiag(-10, 1.5, 0.008, 15, minutes: 240);

        Assert.True(Math.Abs(loose.Undershoot) > Math.Abs(tight.Undershoot) * 3,
            $"放大限幅应显著加大下冲：±6℃ 下冲 {tight.Undershoot:F3}℃，±15℃ 下冲 {loose.Undershoot:F3}℃");
    }

    [Fact]
    public void BiasLimitTooTight_LeavesPermanentOffset_UntilGuardWidensIt()
    {
        // 漏热加倍（相当于跑更低的温度）：维持设定值需要约 9.4℃ 偏置，
        // 固定 ±8℃ 限幅够不着 → 内部温度永远差一截；自动扩程后应恢复正常。
        const double plantLeak = 0.24;
        var pinned = RunCascadeDiag(-10, 2.5, 0.008, 8, 240, leak: plantLeak);
        Assert.True(double.IsNaN(pinned.SettleMin) && Math.Abs(pinned.FinalErr) > 0.3,
            $"限幅不够时应留下明显稳态偏差，实际 {pinned.FinalErr:F3}℃");

        var guarded = RunCascadeDiag(-10, 2.5, 0.008, 8, 240,
            guard: new OuterBiasRangeGuard { PinnedSecondsToWiden = 300, StepC = 5, CeilingC = 25 },
            leak: plantLeak);
        Assert.True(guarded.FinalLimit > 8, $"限幅应被自动放宽，实际仍为 ±{guarded.FinalLimit:F0}℃");
        Assert.True(Math.Abs(guarded.FinalErr) < 0.05,
            $"扩程后应收敛到 ±0.05℃ 内，实际 {guarded.FinalErr:F3}℃");
    }

    [Fact]
    public void Guard_DoesNotWidenDuringNormalCooldown()
    {
        // 现场回归（2026-07-28 −10℃ 实测）：从环境温度长距离降温时，偏置顶在限幅上
        // 5 分钟以上是正常现象——限幅在当降温限速器用，误差一直在缩小。
        // 旧判据据此误放宽 ±8→±13，腔体被多压 1.6℃，直接造成 0.6℃ 下冲。
        // 新判据（误差停止缩小才计时）在整个正常降温过程中绝不能放宽。
        var run = RunCascadeDiag(-10, 2.5, 0.008, 8, 240,
            guard: new OuterBiasRangeGuard { PinnedSecondsToWiden = 300, StepC = 5, CeilingC = 25 });
        Assert.True(run.FinalLimit == 8,
            $"正常降温全程不应放宽限幅，实际被放宽到 ±{run.FinalLimit:F0}℃");
        Assert.False(double.IsNaN(run.SettleMin), "限幅 ±8℃ 本就够用，应正常收敛");
        Assert.True(Math.Abs(run.FinalErr) < 0.05, $"末态应在 ±0.05℃ 内，实际 {run.FinalErr:F3}℃");
    }

    [Fact]
    public void Guard_ProgressVeto_IsSetpointIndependent()
    {
        // 判据只看误差变化量，不掺绝对温度：同一套默认参数在高温点和低温点行为必须一致。
        // 两个大温差工作点（+80℃ 与 −10℃，起点均为环境 29℃）各跑一遍：
        // 限幅够用 → 全程不放宽且收敛。
        foreach (var sp in new[] { 80.0, -10.0 })
        {
            var run = RunCascadeDiag(sp, 2.5, 0.008, 8, 240,
                guard: new OuterBiasRangeGuard());
            Assert.True(run.FinalLimit == 8,
                $"设定 {sp}℃：正常趋近过程不应放宽限幅，实际 ±{run.FinalLimit:F0}℃");
            Assert.True(Math.Abs(run.FinalErr) < 0.05,
                $"设定 {sp}℃：末态应在 ±0.05℃ 内，实际 {run.FinalErr:F3}℃");
        }
    }

    [Fact]
    public void Guard_WidensAfterDisturbanceOnlyIfErrorStaysStuck()
    {
        // 扰动把误差推大再恢复的过程算"仍在推进"（基准随扰动抬高），不触发放宽；
        // 恢复后若误差重新停滞在够不着的位置，计时照常走满并放宽。
        var g = new OuterBiasRangeGuard { PinnedSecondsToWiden = 300, StepC = 5, CeilingC = 25 };

        // 顶限幅 + 误差从 5℃ 正常缩小到 0.5℃：每有进展就重开窗口，不放宽
        var e = 5.0;
        for (var t = 0.0; t < 480; t += 2)
        {
            Assert.Null(g.Update(2, -8, 8, false, e));
            e = Math.Max(0.5, e - 0.02);   // 450s 后停在 0.5℃
        }

        // 扰动骤然把误差推到 3℃ 再以同样速率恢复：恢复即进展，同样不放宽
        e = 3.0;
        for (var t = 0.0; t < 260; t += 2)
        {
            Assert.Null(g.Update(2, -8, 8, false, e));
            e = Math.Max(0.5, e - 0.02);
        }

        // 之后误差停滞在 0.5℃ 不动：300s 走满，放宽一步
        double? widened = null;
        for (var t = 0.0; t <= 302 && widened is null; t += 2)
            widened = g.Update(2, -8, 8, false, 0.5);
        Assert.Equal(13, widened!.Value, 6);
    }

    [Fact]
    public void CrossingDischarge_CutsUndershootFromIntegralWindup()
    {
        // 现场实测（−10℃，2026-07-28）：过零瞬间外环积分 −3.0，稳态只需 −0.35，
        // 多攒的 2.65 只能靠温度越过设定值反向积分放掉——下冲面积 334 ℃·s 与
        // ΔI/Ki=340 完全吻合，证明串级下冲的主因就是积分放电。
        // 过零时把积分预置到该温度已学到的稳态偏置，下冲应大幅缩小，稳定不应变慢。
        // 用加大的 Ki 放大趋近段积分累积，使机理在双容模型上清晰可测。
        const double ki = 0.02;
        var steadyBias = new TwoCapacityPlant().SteadyWallFor(-10) - (-10);

        var plain = RunCascadeDiag(-10, 2.5, ki, 8, minutes: 240);
        var discharged = RunCascadeDiag(-10, 2.5, ki, 8, minutes: 240,
            crossingDischargeTo: steadyBias);

        // 本模型壁面 τ=16min 远慢于实机腔体，过零后的热惯性下坠在仿真里占大头、
        // 放电只能消掉积分那一份；实机（腔体分钟级跟随）下冲几乎全是积分份额，
        // 收益对应更大。这里验证机理方向与幅度，不苛求比例。
        Assert.True(Math.Abs(discharged.Undershoot) < Math.Abs(plain.Undershoot) * 0.75
                    && Math.Abs(plain.Undershoot) - Math.Abs(discharged.Undershoot) > 0.1,
            $"过零放电应明显减小下冲：无 {plain.Undershoot:F3}℃，有 {discharged.Undershoot:F3}℃");
        // 放大 Ki 的场景里放电后回升略欠阻尼，允许稳定时间有边际差异（≤15%）
        Assert.True(discharged.SettleMin <= plain.SettleMin * 1.15,
            $"过零放电不应明显拖慢稳定：无 {plain.SettleMin:F1}min，有 {discharged.SettleMin:F1}min");
        Assert.True(Math.Abs(discharged.FinalErr) < 0.05,
            $"末态应在 ±0.05℃ 内，实际 {discharged.FinalErr:F3}℃");
    }

    [Fact]
    public void TwoStageDischarge_NotWorseThanCrossingOnly_AndConverges()
    {
        // 第一枪（趋近带 1℃ 内放电）让腔体提前归位；实机腔体分钟级跟随时收益显著，
        // 本模型壁面 τ=16min 极慢、收益有限，但方向必须不劣化且不破坏收敛。
        const double ki = 0.02;
        var steadyBias = new TwoCapacityPlant().SteadyWallFor(-10) - (-10);

        var one = RunCascadeDiag(-10, 2.5, ki, 8, 240, crossingDischargeTo: steadyBias);
        var two = RunCascadeDiag(-10, 2.5, ki, 8, 240,
            crossingDischargeTo: steadyBias, approachBand: 1.0);

        Assert.True(Math.Abs(two.Undershoot) <= Math.Abs(one.Undershoot) + 0.02,
            $"两段放电不应加大下冲：单段 {one.Undershoot:F3}℃，两段 {two.Undershoot:F3}℃");
        Assert.False(double.IsNaN(two.SettleMin), "两段放电必须仍收敛");
        Assert.True(Math.Abs(two.FinalErr) < 0.05,
            $"末态应在 ±0.05℃ 内，实际 {two.FinalErr:F3}℃");
    }

    [Fact]
    public void TrackingBoost_CutsRemainingUndershoot()
    {
        // 【注意】缺口前馈已被实机否决、默认关闭（k=1.5 在真实腔体 ~50s 纯滞后下
        // 触发 ~300s 周期自激，见 HostControlLoop.OuterTrackingBoost 注释）。
        // 本测试保留为台架机理演示：在无纯滞后主导的理想化对象上该前馈确实减小
        // 下冲——它验证的是台架实现的数学正确性，不代表实机可用性。
        const double ki = 0.02;
        var steadyBias = new TwoCapacityPlant().SteadyWallFor(-10) - (-10);

        var plain = RunCascadeDiag(-10, 2.5, ki, 8, 240,
            crossingDischargeTo: steadyBias, approachBand: 1.0);
        var boosted = RunCascadeDiag(-10, 2.5, ki, 8, 240,
            crossingDischargeTo: steadyBias, approachBand: 1.0, trackingBoost: 1.5);

        Assert.True(Math.Abs(boosted.Undershoot) < Math.Abs(plain.Undershoot) - 0.01,
            $"缺口前馈应减小下冲：无 {plain.Undershoot:F3}℃，有 {boosted.Undershoot:F3}℃");
        Assert.False(double.IsNaN(boosted.SettleMin), "缺口前馈不应破坏收敛");
        Assert.True(Math.Abs(boosted.FinalErr) < 0.05,
            $"末态应在 ±0.05℃ 内，实际 {boosted.FinalErr:F3}℃");
    }

    [Fact]
    public void BiasClamp_CutsUndershoot_OnDeadTimePlant()
    {
        // "第三枪"落地偏置钳位：交接带内、首次过零前，不许比例项把腔体压得
        // 比稳态位深太多。本对象含 48s 纯滞后（前两个连续补偿方案的克星），
        // 钳位是事件武装的静态界、无回路，纯滞后无法使其失稳——只验证
        // 下冲收益方向与无损害。
        const double ki = 0.02;
        var steadyBias = new TwoCapacityPlant().SteadyWallFor(-10) - (-10);

        var plain = RunCascadeDiag(-10, 2.5, ki, 8, 240,
            crossingDischargeTo: steadyBias, approachBand: 1.0);
        var clamped = RunCascadeDiag(-10, 2.5, ki, 8, 240,
            crossingDischargeTo: steadyBias, approachBand: 1.0,
            clampBand: 0.8, clampMargin: 0.3);

        Assert.True(Math.Abs(clamped.Undershoot) < Math.Abs(plain.Undershoot) - 0.01,
            $"钳位应减小下冲：无 {plain.Undershoot:F3}℃，有 {clamped.Undershoot:F3}℃");
        Assert.False(double.IsNaN(clamped.SettleMin), "钳位不应破坏收敛");
        Assert.True(clamped.SettleMin <= plain.SettleMin * 1.3,
            $"钳位不应大幅拖慢稳定：无 {plain.SettleMin:F1}min，有 {clamped.SettleMin:F1}min");
        Assert.True(Math.Abs(clamped.FinalErr) < 0.05,
            $"末态应在 ±0.05℃ 内，实际 {clamped.FinalErr:F3}℃");
    }

    [Fact]
    public void TwoStageDischarge_WithStaleBias_NoStallNoOscillation()
    {
        // 关键安全性：稳态偏置过时（偏 3℃）时，两段放电最多让落点偏一次；
        // 积分从不冻结，必须由正常积分接管收敛到 ±0.05℃，不允许卡在带内某处。
        const double ki = 0.02;
        var trueBias = new TwoCapacityPlant(leakRatio: 0.24).SteadyWallFor(-10) - (-10);

        var run = RunCascadeDiag(-10, 2.5, ki, 15, 240, leak: 0.24,
            crossingDischargeTo: trueBias + 3, approachBand: 1.0);

        Assert.False(double.IsNaN(run.SettleMin), "过时的放电值不应阻止收敛");
        Assert.True(Math.Abs(run.FinalErr) < 0.05,
            $"末态应回到 ±0.05℃ 内，实际 {run.FinalErr:F3}℃");
    }

    [Fact]
    public void CrossingDischarge_WithStaleBias_StillConvergesAndFiresOnce()
    {
        // 学到的稳态偏置过时（环境变了、漏热变了）时，过零放电把积分预置到一个
        // 不再准确的值——只触发一次，之后正常积分接管，必须仍能收敛，
        // 不允许出现放电↔再积分的往复振荡。
        const double ki = 0.02;
        var trueBias = new TwoCapacityPlant(leakRatio: 0.24).SteadyWallFor(-10) - (-10);
        var staleBias = trueBias + 3;   // 偏了 3℃ 的旧值

        var run = RunCascadeDiag(-10, 2.5, ki, 15, minutes: 240,
            leak: 0.24, crossingDischargeTo: staleBias);

        Assert.False(double.IsNaN(run.SettleMin), "过时的放电值不应阻止收敛");
        Assert.True(Math.Abs(run.FinalErr) < 0.05,
            $"末态应回到 ±0.05℃ 内，实际 {run.FinalErr:F3}℃");
    }

    [Fact]
    public void OuterIntegralPreset_HelpsWhenRestartingAtTemperature()
    {
        // 已经在温度上停机再启动（现场很常见）：外环积分从零起步会先漂出去再收回来，
        // 预置到已学到的稳态偏置则几乎无扰动。降温全程反而看不出差别——那段外环偏置
        // 一直顶在限幅上，预置值被限幅盖住了。
        // 该对象维持 −10℃ 所需的壁面偏置（−0.12×(29−(−10)) = −4.68℃）
        var steadyBias = new TwoCapacityPlant().SteadyWallFor(-10) - (-10);
        var cold = RunCascadeDiag(-10, 2.5, 0.008, 8, 120, startAtSetpoint: true);
        var preset = RunCascadeDiag(-10, 2.5, 0.008, 8, 120,
            outerPreset: steadyBias, startAtSetpoint: true);

        Assert.True(Math.Abs(preset.Undershoot) < Math.Abs(cold.Undershoot) / 2,
            $"预置外环积分应大幅减小重启扰动：预置 {preset.Undershoot:F3}℃，不预置 {cold.Undershoot:F3}℃");
    }

    [Fact]
    public void FreezeIntegral_StopsOuterWindupWhenInnerSaturated()
    {
        var pid = new PidController();
        pid.Configure(1, 1, 0, -50, 50);
        for (var i = 0; i < 10; i++) pid.Update(10, 0, 1);        // 正常累积
        var before = pid.LastI;
        for (var i = 0; i < 10; i++) pid.Update(10, 0, 1, freezeIntegral: true);
        Assert.Equal(before, pid.LastI, 6);
    }
}
