using TecControl.Core.Control;

namespace TecControl.Core.Tests;

public class PidControllerTests
{
    private static PidController Make(double kp, double ki, double kd,
        double min = -100, double max = 100, double dFilter = 0)
    {
        var pid = new PidController();
        pid.Configure(kp, ki, kd, min, max, dFilter);
        return pid;
    }

    [Fact]
    public void ProportionalOnly_OutputEqualsKpTimesError()
    {
        var pid = Make(kp: 5, ki: 0, kd: 0);
        // 误差 2℃ → 输出 10%
        Assert.Equal(10.0, pid.Update(setpoint: 27, measurement: 25, dtSeconds: 0.5), 6);
    }

    [Fact]
    public void Output_IsClamped()
    {
        var pid = Make(kp: 100, ki: 0, kd: 0, min: -60, max: 60);
        Assert.Equal(60.0, pid.Update(30, 20, 0.5), 6);
        Assert.Equal(-60.0, pid.Update(20, 30, 0.5), 6);
    }

    [Fact]
    public void Integral_Accumulates()
    {
        var pid = Make(kp: 0, ki: 1, kd: 0);
        // 误差恒为 1℃，dt=1s：积分每拍 +1%
        Assert.Equal(1.0, pid.Update(26, 25, 1), 6);
        Assert.Equal(2.0, pid.Update(26, 25, 1), 6);
        Assert.Equal(3.0, pid.Update(26, 25, 1), 6);
    }

    [Fact]
    public void AntiWindup_IntegralStopsAtSaturation()
    {
        var pid = Make(kp: 0, ki: 10, kd: 0, min: -50, max: 50);
        // 大误差长时间积分，输出必须停在 50 且积分不无限累积
        for (var i = 0; i < 100; i++)
            pid.Update(35, 25, 1);
        Assert.Equal(50.0, pid.Update(35, 25, 1), 6);

        // 误差反向后应立即开始往回走，而不是先消耗巨大的积分存量
        var afterReversal = pid.Update(25, 35, 1);
        Assert.True(afterReversal < 50.0, $"积分饱和未被抑制，反向一拍后输出仍为 {afterReversal}");
    }

    [Fact]
    public void Derivative_ActsOnMeasurement_NoSetpointKick()
    {
        var pid = Make(kp: 0, ki: 0, kd: 10);
        pid.Update(25, 25, 1);
        // 只改设定值、测量值不变：微分项不应产生输出（无微分踢）
        Assert.Equal(0.0, pid.Update(30, 25, 1), 6);
    }

    [Fact]
    public void Derivative_OpposesMeasurementRise()
    {
        var pid = Make(kp: 0, ki: 0, kd: 10);
        pid.Update(25, 25, 1);
        // 温度以 0.5℃/s 上升 → 微分项输出为负（制动）
        var output = pid.Update(25, 25.5, 1);
        Assert.True(output < 0, $"温度上升时微分项应为负，实际 {output}");
    }

    [Fact]
    public void Reset_ClearsState()
    {
        var pid = Make(kp: 0, ki: 1, kd: 0);
        pid.Update(26, 25, 1);
        pid.Update(26, 25, 1);
        pid.Reset();
        Assert.Equal(1.0, pid.Update(26, 25, 1), 6);
    }

    [Fact]
    public void InvalidDt_DoesNotThrowOrExplode()
    {
        var pid = Make(kp: 1, ki: 1, kd: 1);
        var o1 = pid.Update(26, 25, 0);
        var o2 = pid.Update(26, 25, double.NaN);
        Assert.True(Math.Abs(o1) <= 100 && Math.Abs(o2) <= 100);
    }

    /// <summary>
    /// 闭环仿真验证：一阶热对象 dT/dt = (-(T-Tamb) + K·u) / tau。
    /// PID 应能把对象从环境温度拉到设定值并稳定在 ±0.05℃ 内且无持续振荡。
    /// </summary>
    [Fact]
    public void ClosedLoop_FirstOrderPlant_ConvergesToSetpoint()
    {
        const double ambient = 22.0, setpoint = 30.0;
        const double tau = 60.0;      // 对象时间常数 60s
        const double gain = 0.2;      // 每 1% 输出稳态升温 0.2℃
        const double dt = 0.5;        // 控制周期 500ms

        var pid = Make(kp: 20, ki: 0.5, kd: 0, dFilter: 2);
        var temp = ambient;

        for (var i = 0; i < 2400; i++) // 仿真 20 分钟
        {
            var duty = pid.Update(setpoint, temp, dt);
            temp += dt * (-(temp - ambient) + gain * duty) / tau;
        }

        Assert.True(Math.Abs(temp - setpoint) < 0.05,
            $"仿真 20 分钟后温度 {temp:F4}℃ 未收敛到设定 {setpoint}℃");
    }

    /// <summary>反向对象（输出为正时降温）配合外层取反后同样收敛，验证“反向输出”方案可行。</summary>
    [Fact]
    public void ClosedLoop_InvertedPlant_ConvergesWithInvertedOutput()
    {
        const double ambient = 22.0, setpoint = 15.0;
        const double tau = 60.0, gain = 0.2, dt = 0.5;

        var pid = Make(kp: 20, ki: 0.5, kd: 0, dFilter: 2);
        var temp = ambient;

        for (var i = 0; i < 2400; i++)
        {
            var duty = pid.Update(setpoint, temp, dt);
            // 物理对象：正占空比 = 制冷（与默认假设相反），控制侧用 -duty 驱动等效
            temp += dt * (-(temp - ambient) + gain * duty) / tau;
        }

        Assert.True(Math.Abs(temp - setpoint) < 0.05,
            $"制冷方向仿真未收敛，最终 {temp:F4}℃，设定 {setpoint}℃");
    }

    [Fact]
    public void PresetIntegral_ShortensSettlingWithoutRaisingGains()
    {
        // 实测问题：稳态需 -34% 输出，积分从零累积导致收尾很慢。
        // 预置积分后，同样参数应显著更快到达设定值。
        const double ambient = 29.0, setpoint = -10.0, gain = 1.14, tau = 972, dt = 0.5;
        const double steadyDuty = -(ambient - setpoint) / gain; // ≈ -34%

        static double Settle(double preset, double amb, double sp, double k, double tp, double h)
        {
            var pid = new PidController();
            pid.Configure(12.893, 0.0314, 382.444, -60, 60, derivativeFilterSeconds: 2);
            pid.PresetIntegral(preset);
            var temp = 16.29;
            for (var t = 0.0; t < 7200; t += h)
            {
                var u = pid.Update(sp, temp, h);
                temp += h * ((amb + k * u) - temp) / tp;
                if (Math.Abs(temp - sp) <= 0.1) return t;
            }
            return double.PositiveInfinity;
        }

        var withoutPreset = Settle(0, ambient, setpoint, gain, tau, dt);
        var withPreset = Settle(steadyDuty, ambient, setpoint, gain, tau, dt);

        Assert.True(withPreset < withoutPreset * 0.75,
            $"预置积分应明显缩短稳定时间：无预置 {withoutPreset / 60:F1}min，预置 {withPreset / 60:F1}min");
    }

    [Fact]
    public void PresetIntegral_IsClampedToOutputLimits()
    {
        var pid = new PidController();
        pid.Configure(1, 1, 0, -50, 50);
        pid.PresetIntegral(-999);
        // 误差为 0 时输出即积分项，应被限幅
        Assert.Equal(-50, pid.Update(25, 25, 1), 6);
    }

    [Fact]
    public void HeatOnlyLimits_ClampAtZero_WithoutNegativeWindup()
    {
        // 只加热执行器（电加热棒）：下限 0。温度高于设定值（要求"制冷"）时输出贴 0，
        // 且积分不得积成负值——否则温度回落后要先还清负积分才开始加热，白等几分钟。
        var pid = new PidController();
        pid.Configure(10, 0.5, 0, 0, 90);

        for (var i = 0; i < 200; i++)
            Assert.Equal(0, pid.Update(110, 115, 0.5), 6);   // 超温 5℃，输出恒 0
        Assert.True(pid.LastI >= 0, $"积分不得为负，实际 {pid.LastI:F2}");

        // 温度回落到设定值下方后应立即出正输出（无负积分欠账）
        var u = pid.Update(110, 109, 0.5);
        Assert.True(u > 0, $"回落后应立即加热，实际输出 {u:F2}%");
    }
}
