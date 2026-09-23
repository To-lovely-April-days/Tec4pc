using System.Globalization;
using TecControl.Core.Control;
using Tec.Driver.Abi;

namespace Tec.Drivers.Rd105;

/// <summary>
/// 设备配置里那三项（控温方式 / TEC 输出反向 / 加热棒占空比）没填时按谁的缺省：
/// 单独的 RD105 温控器驱动缺省温控器自己的 PID、不反向、正；双工位反应主机缺省上位机 PID，
/// 而且按现场实测的极性（降温 +90、升温 −90）缺省反向、负。
/// </summary>
public sealed record Rd105HostDefaults(string Control, string Invert, string HeaterSign)
{
    public static readonly Rd105HostDefaults Standalone =
        new(Rd105TecDriver.ControlDevice, Rd105TecDriver.InvertNo, Rd105TecDriver.HeaterPos);

    public static readonly Rd105HostDefaults DualStation =
        new(Rd105TecDriver.ControlHost, Rd105TecDriver.InvertYes, Rd105TecDriver.HeaterNeg);

    public bool HostOf(ParameterSet cfg) => cfg.Str(Rd105TecDriver.FieldControl, Control) == Rd105TecDriver.ControlHost;
    public bool InvertOf(ParameterSet cfg) => cfg.Str(Rd105TecDriver.FieldInvert, Invert) == Rd105TecDriver.InvertYes;
    public int HeaterSignOf(ParameterSet cfg) => cfg.Str(Rd105TecDriver.FieldHeaterSign, HeaterSign) == Rd105TecDriver.HeaterNeg ? -1 : 1;
}

/// <summary>
/// 上位机回路的落盘与缺省参数。增益表按「设备实例 + 路号」一张 CSV，格式与 TecControl.App 的
/// gain-schedule.csv 一字不差（同一份 GainScheduleStore）——在那边整定过的表拷过来就能用，
/// 反过来也一样。位置：%AppData%\TecDrivers\gains\&lt;实例&gt;-tc&lt;n&gt;.csv，工作站 / HMI / 极限测试工具共用，
/// 谁整定的谁都受益。
/// </summary>
public static class Rd105HostControl
{
    /// <summary>没整定过时的保守起点（P 为主、一点 I、不带 D）：跑得动、不会振，但慢——开机日志会提醒先自整定。</summary>
    public static readonly PidGains FallbackInner = new(8, 0.02, 0);

    /// <summary>串级外环起点：Kp 2.5 ℃/℃、Ki 0.008 1/s、无 D，偏置限幅 ±8 ℃（TecControl.App 出厂表 −10 ℃ 那一行的外环值）。</summary>
    public static readonly PidGains FallbackOuter = new(2.5, 0.0079, 0);
    public const double FallbackOuterMaxBiasC = 8;

    /// <summary>增益表目录，可用环境变量 TEC_GAINS_DIR 改。</summary>
    public static string GainsDir
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("TEC_GAINS_DIR");
            if (!string.IsNullOrWhiteSpace(env)) return env;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TecDrivers", "gains");
        }
    }

    public static string GainsPath(string instanceId, int tc)
    {
        var safe = string.Concat(instanceId.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(GainsDir, $"{safe}-tc{tc}.csv");
    }

    /// <summary>把这一路的增益表从盘上读进来。返回读到几个工作点（文件不存在 = 0）。</summary>
    public static int Load(PidGainSchedule schedule, string path)
    {
        try
        {
            GainScheduleStore.Load(schedule, path);
            return schedule.Count;
        }
        catch
        {
            return 0;
        }
    }

    public static void Save(PidGainSchedule schedule, string path) => GainScheduleStore.Save(schedule, path);

    public static string Describe(PidGainSchedule schedule)
        => schedule.Count == 0
            ? "增益表空"
            : "增益表 " + string.Join("、", schedule.Points.OrderBy(p => p.TemperatureC)
                .Select(p => p.TemperatureC.ToString("0.#", CultureInfo.InvariantCulture) + " ℃"));
}
