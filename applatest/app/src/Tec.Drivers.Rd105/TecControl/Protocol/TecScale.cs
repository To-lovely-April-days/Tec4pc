namespace TecControl.Core.Protocol;

/// <summary>
/// 光测未来 TEC 温控器（RD105 协议 v1.3.0）原始寄存器值与工程量之间的换算。
/// 协议中所有温度均以 1e-5 ℃ 为单位的整数传输，例如 2500000 = 25.00000℃。
/// </summary>
public static class TecScale
{
    /// <summary>通道未接传感器时 TCADJTEMP 返回的哨兵值。</summary>
    public const long SensorNotConnectedRaw = 999_999_999;

    public static double TempFromRaw(long raw) =>
        raw == SensorNotConnectedRaw ? double.NaN : raw / 1e5;

    public static long TempToRaw(double celsius) => (long)Math.Round(celsius * 1e5);

    /// <summary>RESISTOR：10000000000 = 10.000000kΩ，即原始值单位为 μΩ。</summary>
    public static double OhmsFromRaw(long raw) => raw / 1e6;

    /// <summary>OUTV（DATADEMAND=2）：1000000000 = 10V。</summary>
    public static double VoltsFromRaw(long raw) => raw / 1e8;

    /// <summary>PWMDUTY：±2000000 = ±100%，200000 = 10%。</summary>
    public static double DutyPercentFromRaw(long raw) => raw / 20_000.0;

    public static long DutyPercentToRaw(double percent) => (long)Math.Round(percent * 20_000.0);

    /// <summary>SPEED 温度变化斜率：1000 = 1℃/s，0 = 不限制。</summary>
    public static double SpeedFromRaw(long raw) => raw / 1000.0;

    public static long SpeedToRaw(double degPerSec) => (long)Math.Round(degPerSec * 1000.0);

    /// <summary>CURRENT 输出电流：3256 = 3.256A。</summary>
    public static double CurrentFromRaw(long raw) => raw / 1000.0;

    /// <summary>SETCURRENT 最大输出电流：10 = 1.0A。</summary>
    public static double MaxCurrentFromRaw(long raw) => raw / 10.0;

    public static long MaxCurrentToRaw(double amps) => (long)Math.Round(amps * 10.0);
}
