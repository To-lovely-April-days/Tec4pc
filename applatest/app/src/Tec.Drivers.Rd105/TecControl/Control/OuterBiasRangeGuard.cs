namespace TecControl.Core.Control;

/// <summary>
/// 串级外环偏置限幅的自动扩程保护。
///
/// 解决的问题：外环把腔体设定值偏离主设定值的幅度受限幅保护，但"维持某温度需要多大偏置"
/// 随温度大幅变化——腔体到环境的漏热正比于温差。限幅一旦小于该温度实际需要的偏置，
/// 内环明明还有余力，核心温度却永远差一截到不了，
/// 表现为"温度停在离设定值零点几度的地方不动了"，且外环积分早已顶在限幅上无法再修正。
///
/// 判据（三条同时满足才放宽）：
///  1. 偏置顶在限幅上；
///  2. 内环输出并未饱和——说明瓶颈不是制冷能力。内环一旦饱和就重新计时：那是能力不足，
///     再放宽限幅只会把腔体推得更极端而无济于事；
///  3. 外环误差已停止缩小——顶限幅期间误差每缩小 <see cref="ProgressEpsilonC"/> 就重新计时。
///     长距离降/升温时偏置顶在限幅上是正常现象（限幅在当降温限速器用），
///     此时误差持续缩小，绝不能放宽：−10℃ 实测一次误放宽直接造成 0.6℃ 下冲。
///     真正卡死时误差停滞不动，计时自然走满。判据只看误差变化量，与设定值绝对温度无关，
///     任何工作点行为一致。
/// </summary>
public sealed class OuterBiasRangeGuard
{
    /// <summary>限幅可自动放宽到的上限（℃）。</summary>
    public double CeilingC { get; init; } = 25;

    /// <summary>每次放宽的步长（℃）。</summary>
    public double StepC { get; init; } = 5;

    /// <summary>偏置持续顶在限幅上多少秒后才放宽（避免暂态误判）。</summary>
    public double PinnedSecondsToWiden { get; init; } = 300;

    /// <summary>
    /// 顶限幅计时窗口内，外环误差缩小超过该值（℃）即视为"仍在正常推进"，重新计时。
    /// 卡死的系统误差在整个窗口内几乎不动（典型 &lt;0.05℃），正常降温则远快于此，
    /// 该阈值把两者干净分开。
    /// </summary>
    public double ProgressEpsilonC { get; init; } = 0.3;

    /// <summary>当前已连续顶在限幅上（且误差无进展）的秒数。</summary>
    public double PinnedSeconds { get; private set; }

    /// <summary>本计时窗口内观测到的最大误差幅值（进展的比较基准）；NaN 表示窗口未开始。</summary>
    private double _windowMaxAbsError = double.NaN;

    public void Reset()
    {
        PinnedSeconds = 0;
        _windowMaxAbsError = double.NaN;
    }

    /// <summary>
    /// 喂入一次外环更新的结果。需要放宽时返回新的限幅值，否则返回 null。
    /// </summary>
    /// <param name="outerErrorC">外环误差（设定值 − 被控量，℃）。只用其幅值的变化趋势。</param>
    public double? Update(double dtSeconds, double biasC, double limitC, bool innerSaturated,
        double outerErrorC)
    {
        if (innerSaturated || limitC <= 0 || Math.Abs(biasC) < limitC - 1e-6)
        {
            Reset();
            return null;
        }

        var absErr = Math.Abs(outerErrorC);
        if (double.IsNaN(_windowMaxAbsError)) _windowMaxAbsError = absErr;
        // 误差被扰动推大时抬高基准，这样扰动后的恢复同样算作"仍在推进"
        _windowMaxAbsError = Math.Max(_windowMaxAbsError, absErr);

        if (_windowMaxAbsError - absErr >= ProgressEpsilonC)
        {
            // 仍在向设定值推进：限幅正在履行降温限速的本职，重开计时窗口
            PinnedSeconds = 0;
            _windowMaxAbsError = absErr;
            return null;
        }

        PinnedSeconds += Math.Max(0, dtSeconds);
        if (PinnedSeconds < PinnedSecondsToWiden || limitC >= CeilingC) return null;

        Reset();
        return Math.Min(CeilingC, limitC + StepC);
    }
}
