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

    // 【本地改动】（2026-11-16，外环改「按实测实时算」第 1 步，主会话 D12）分方向的入口：只给「新外环在控」那一路用，旧外环照旧调上面的 Check（一字没动）。
    private int _satDir;   // 上一拍饱和在哪一侧：+1 加热顶满、−1 制冷顶满、0 不饱和。只有 CheckDirectional 读写

    /// <summary>
    /// 【本地改动】分方向的跑飞判据：<paramref name="saturatedDir"/> = +1 加热那一侧顶满、−1 制冷那一侧顶满、0 不饱和。
    /// 跟 <see cref="Check"/> 一样判，只多一条：饱和换了方向（上一拍加热满、这一拍制冷满，中间没有不饱和的一拍）当成重新开始饱和——
    /// 计时和「饱和起点的读数」都从这一拍起。旧的 Check 只在不饱和那一拍才清计时：输出一拍里从 +满 翻到 −满，计时和起点还是上一个方向的，
    /// 新外环往下的比例那一份不封顶、改目标时夹套目标可能一拍大跳，这个缺陷才可能被踩到（scout-loop §5.1：旧外环下 14 场真机记录里一次都没有）。
    /// 方向不变时跟 Check 逐拍同结果。Start / Reset 不用改：它们清了饱和起点，下一拍按「刚开始饱和」走。
    /// </summary>
    public bool CheckDirectional(DateTime time, double setpoint, double pv, int saturatedDir)
    {
        var dir = Math.Sign(saturatedDir);
        if (dir != 0 && _saturatedSince is not null && dir != _satDir) _saturatedSince = null;
        _satDir = dir;
        return Check(time, setpoint, pv, dir != 0);
    }
}
