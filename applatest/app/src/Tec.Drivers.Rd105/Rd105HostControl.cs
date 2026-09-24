using System.Globalization;
using TecControl.Core.Control;
using Tec.Driver.Abi;

namespace Tec.Drivers.Rd105;

/// <summary>
/// 设备配置里那三项（控温方式 / TEC 输出反向 / 加热棒占空比）没填时按谁的缺省：
/// 单独的 RD105 温控器驱动缺省温控器自己的 PID、不反向、正；双工位反应主机缺省上位机 PID，
/// 而且按现场实测的极性（降温 +90、升温 −90）缺省反向、负。
/// </summary>
public sealed record Rd105HostDefaults(string Control, string Invert, string HeaterSign, string Fpwm = Rd105TecDriver.FpwmKeep)
{
    public static readonly Rd105HostDefaults Standalone =
        new(Rd105TecDriver.ControlDevice, Rd105TecDriver.InvertNo, Rd105TecDriver.HeaterPos);

    /// <summary>双工位主机：加热棒是交流 + 固态继电器，PWM 频率缺省 1 Hz（功率 2 % 一档）。</summary>
    public static readonly Rd105HostDefaults DualStation =
        new(Rd105TecDriver.ControlHost, Rd105TecDriver.InvertYes, Rd105TecDriver.HeaterNeg, "1 Hz");

    public string FpwmOf(ParameterSet cfg) => cfg.Str(Rd105TecDriver.FieldFpwm, Fpwm);

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

    private static readonly AsyncLocal<string?> DirOverride = new();

    /// <summary>
    /// 测试用：这一段异步流程里（开会话之前调）增益表放到 dir。不碰进程级的环境变量——
    /// 并行跑的测试各用各的目录，互不串。返回的东西 Dispose 了就恢复。
    /// </summary>
    public static IDisposable UseGainsDir(string dir)
    {
        var old = DirOverride.Value;
        DirOverride.Value = dir;
        return new Restore(() => DirOverride.Value = old);
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    /// <summary>增益表目录，可用环境变量 TEC_GAINS_DIR 改。</summary>
    public static string GainsDir
    {
        get
        {
            if (DirOverride.Value is { } o) return o;
            var env = Environment.GetEnvironmentVariable("TEC_GAINS_DIR");
            if (!string.IsNullOrWhiteSpace(env)) return env;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TecDrivers", "gains");
        }
    }

    /// <summary>TEC 那张表（0325 起就是这个文件）。</summary>
    public static string GainsPath(string instanceId, int tc) => Path.Combine(GainsDir, $"{Safe(instanceId)}-tc{tc}.csv");

    /// <summary>加热棒那张表（0327 起按执行器分两张）。</summary>
    public static string HeaterGainsPath(string instanceId, int tc) => Path.Combine(GainsDir, $"{Safe(instanceId)}-tc{tc}-heater.csv");

    /// <summary>手动 PID 参数与这一路的调度开关。</summary>
    public static string ParamsPath(string instanceId, int tc) => Path.Combine(GainsDir, $"{Safe(instanceId)}-tc{tc}-pid.json");

    /// <summary>每次控温的逐拍记录放哪（Rd105LoopRecorder）：环境变量 TEC_LOOPLOG_DIR 可改；测试的 UseGainsDir 顺带把它也隔开。</summary>
    public static string LoopLogDir
    {
        get
        {
            if (DirOverride.Value is { } o) return Path.Combine(o, "loops");
            var env = Environment.GetEnvironmentVariable("TEC_LOOPLOG_DIR");
            if (!string.IsNullOrWhiteSpace(env)) return env;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TecDrivers", "loops");
        }
    }

    internal static string SafeName(string instanceId) => Safe(instanceId);

    private static string Safe(string instanceId)
        => string.Concat(instanceId.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

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
