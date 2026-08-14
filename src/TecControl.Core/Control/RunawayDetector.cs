namespace TecControl.Core.Control;

/// <summary>
/// 输出方向接反（跑飞）检测：闭环输出已在限幅值上饱和、而温度仍持续背离设定值，
/// 说明输出极性大概率接反（正反馈），应自动停控。
///
/// 时间尺度必须匹配热对象：实测本机 Tu≈187s，启动瞬间温度常带着惯性继续朝原方向走
/// 一两分钟，因此设启动宽限期，且检测窗口取分钟级，避免把正常启动瞬态误判为跑飞。
///
/// 另有"长期饱和但温度在收敛"的情形（设定值超出制冷能力）：那不是跑飞，
/// 由 SaturatedSeconds 暴露给上层做警告，不触发停机。
/// </summary>
public sealed class RunawayDetector(
    double deltaC = 0.5,
    double detectSeconds = 180,
    double graceSeconds = 120)
{
    private DateTime? _controlStart;
    private DateTime? _saturatedSince;
    private double _pvAtSaturation;

    /// <summary>当前连续饱和时长（秒）；未饱和为 0。</summary>
    public double SaturatedSeconds { get; private set; }

    /// <summary>闭环启动时调用，开始计算宽限期。</summary>
    public void Start(DateTime time)
    {
        _controlStart = time;
        _saturatedSince = null;
        SaturatedSeconds = 0;
    }

    public void Reset()
    {
        _controlStart = null;
        _saturatedSince = null;
        SaturatedSeconds = 0;
    }

    /// <summary>每个控制周期调用一次；返回 true 表示判定为方向跑飞，应停控。</summary>
    public bool Check(DateTime time, double setpoint, double pv, bool outputSaturated)
    {
        if (!outputSaturated)
        {
            _saturatedSince = null;
            SaturatedSeconds = 0;
            return false;
        }

        if (_saturatedSince is null)
        {
            _saturatedSince = time;
            _pvAtSaturation = pv;
            SaturatedSeconds = 0;
            return false;
        }

        SaturatedSeconds = (time - _saturatedSince.Value).TotalSeconds;

        // 启动宽限期内不判定：此时温度可能仍带惯性朝原方向运动
        if (_controlStart is { } start && (time - start).TotalSeconds < graceSeconds)
            return false;

        if (SaturatedSeconds < detectSeconds)
            return false;

        // 必须是持续背离：与饱和起点相比误差反而扩大了 deltaC 以上
        return Math.Abs(setpoint - pv) > Math.Abs(setpoint - _pvAtSaturation) + deltaC;
    }
}
