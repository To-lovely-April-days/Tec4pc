namespace TecControl.Core.Protocol;

/// <summary>ERRORCODE（0x0007）16 位状态字，按 RD105 协议 3.5.8 节定义。</summary>
[Flags]
public enum TecErrorCode : ushort
{
    None = 0,
    HighTempLimiting = 1 << 0,      // 高温报警：超过自身过温阈值，限制输出
    OverTempShutdown = 1 << 1,      // 过温报警：超过内部设定最大温度，停止输出
    UnderVoltage = 1 << 2,          // 电压过低（< 7V）
    OverVoltage = 1 << 3,           // 电压过高（> 30V）
    Ch1SensorOutOfRange = 1 << 5,   // 通道 1 传感器温度越过高/低阈值
    Ch1CurrentLimiting = 1 << 6,    // 通道 1 输出电流达到设定最大值（限流中）
    Ch2SensorOutOfRange = 1 << 9,   // 通道 2 传感器温度越过高/低阈值
    Ch2CurrentLimiting = 1 << 10,   // 通道 2 输出电流达到设定最大值（限流中）
}

public static class TecErrorCodeExtensions
{
    private static readonly (TecErrorCode Flag, string Text)[] Descriptions =
    [
        (TecErrorCode.HighTempLimiting, "温控器自身高温，正在限制输出"),
        (TecErrorCode.OverTempShutdown, "温控器过温，已停止输出"),
        (TecErrorCode.UnderVoltage, "供电电压过低（<7V）"),
        (TecErrorCode.OverVoltage, "供电电压过高（>30V）"),
        (TecErrorCode.Ch1SensorOutOfRange, "通道1 传感器温度越限"),
        (TecErrorCode.Ch1CurrentLimiting, "通道1 输出限流中"),
        (TecErrorCode.Ch2SensorOutOfRange, "通道2 传感器温度越限"),
        (TecErrorCode.Ch2CurrentLimiting, "通道2 输出限流中"),
    ];

    /// <summary>把状态字翻译成中文告警条目列表；无告警时返回空列表。</summary>
    public static IReadOnlyList<string> Describe(this TecErrorCode code) =>
        Descriptions.Where(d => code.HasFlag(d.Flag)).Select(d => d.Text).ToList();
}
