using Tec.Driver.Abi;

namespace Tec.LimitTest.Runs;

/// <summary>一个工位此刻的读数。没采到的是 null——Runner 往表里写空格，不编。</summary>
/// <param name="Tj">夹套温度 ℃（温控器自带的那路 TC）。</param>
/// <param name="Tr">釜内温度 ℃（宇电探头会话发的）；没接探头就是 null。</param>
/// <param name="Out">控温输出 %（PWMDUTY，±100）。</param>
/// <param name="Cur">TEC 电流 A（温控器实时状态里的 CURRENT，几秒读一次）。</param>
/// <param name="Source">热源：「TEC」/「电加热」；不知道是 null。</param>
/// <param name="TecPower">TEC 功率线通没通（IO8R 读回的继电器位置）；没配继电器的机器是 null。</param>
public sealed record WellReading(double? Tj, double? Tr, double? Out, double? Cur, string? Source, bool? TecPower)
{
    public static readonly WellReading Empty = new(null, null, null, null, null, null);
}

/// <summary>
/// Runner 眼里的「一台机器」：两个工位、读数、下发目标、停、急停，再加几项写进表里的设备参数。
/// 真机由 <see cref="RuntimeRig"/>（套在 HmiRuntime 外面）实现；回归测试用替身。
/// </summary>
public interface IRig
{
    /// <summary>「双工位反应主机（R1）· CH1 / CH2」这种一句话，写日志和表头用。</summary>
    string Describe { get; }

    /// <summary>工位数（这台机器是 2：0 = A，1 = B）。</summary>
    int WellCount { get; }

    /// <summary>此刻的读数。过期 / Bad 的按 null 给——Runner 不拿旧值冒充新值。</summary>
    WellReading Read(int well);

    /// <summary>链路活着没有；没活着 why 说原因（打开失败的原因 / 会话状态）。</summary>
    bool IsAlive(out string why);

    /// <summary>这一路的设备温度范围；Runner 拿它做「夹套跑出范围就中止」的兜底。</summary>
    TempLimits? Limits(int well);

    /// <summary>台面配置里的「电加热切换阈值」（℃），写进最高温表的条件块。</summary>
    double? Threshold { get; }

    /// <summary>台面配置里的「热源切换死区」（K），写进恒温表的条件块。</summary>
    double? Band { get; }

    /// <summary>下发夹套目标，「尽快」（目标一步写给温控器，斜率不限）。热源切换由驱动自己做。</summary>
    Task SetTargetAsync(int well, double target, CancellationToken ct);

    /// <summary>停这一路的控温（关输出）。</summary>
    Task StopAsync(int well, CancellationToken ct);

    /// <summary>急停：所有会话在两个工位上的输出收到安全态。返回每台设备报的「动了什么」。</summary>
    Task<IReadOnlyList<string>> SafeStopAsync(CancellationToken ct);

    /// <summary>从温控器读这一路的最大功率 LIMITED（%）。读不到 = null。</summary>
    Task<double?> ReadLimitedAsync(int well, CancellationToken ct);

    /// <summary>从温控器读这一路的超温上限（℃）。读不到 = null。</summary>
    Task<double?> ReadOverLimitAsync(int well, CancellationToken ct);

    /// <summary>安全层触发了（越限 / 设备告警 / 信号丢失）：Runner 中止当前测试。参数是原因。</summary>
    event Action<string>? Tripped;
}
