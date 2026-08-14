namespace TecControl.Core.Models;

/// <summary>单个通道的完整配置参数（从设备读回 / 写入设备）。</summary>
public sealed record ChannelConfig
{
    public double TargetC { get; init; }
    public bool Enabled { get; init; }

    /// <summary>0 双向；1 制冷；2 加热；3 通信设定电压百分比。</summary>
    public int Mode { get; init; }

    /// <summary>PID 系数为设备内部单位的原始整数值（协议未定义物理单位）。</summary>
    public long Kp { get; init; }
    public long Ki { get; init; }
    public long Kd { get; init; }

    /// <summary>最大输出占空比（LIMITED），单位 %，范围 0~90。</summary>
    public int MaxDutyPercent { get; init; }

    /// <summary>温度变化斜率（SPEED），℃/s，0 = 不限制。</summary>
    public double SpeedCPerSec { get; init; }

    public double OverTempUpC { get; init; }
    public double OverTempLowerC { get; init; }

    /// <summary>最大输出电流（SETCURRENT），A。</summary>
    public double MaxCurrentA { get; init; }

    /// <summary>AUTOPID：0 常规；1 自整定进行中。</summary>
    public int AutoPid { get; init; }
}
