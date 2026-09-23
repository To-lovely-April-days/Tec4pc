using System.Globalization;

namespace TecControl.Core.Control;

/// <summary>增益表的磁盘持久化（CSV，便于人工查看与编辑）。</summary>
public static class GainScheduleStore
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TecStudio", "gain-schedule.csv");

    /// <summary>
    /// 随程序发布的出厂增益表（exe 同级 config\gain-schedule.csv）。
    /// 只在用户还没有自己的表时作为初值导入，之后一律以用户表为准。
    /// </summary>
    public static string SeedPath => Path.Combine(AppContext.BaseDirectory, "config", "gain-schedule.csv");

    /// <summary>
    /// 载入用户表；用户表不存在或没有任何数据行（例如点过"清空"）时退回出厂表。
    /// 返回实际读取的路径（都没有可读数据则为 null）。
    /// </summary>
    public static string? LoadOrSeed(PidGainSchedule schedule, string? userPath = null,
        string? seedPath = null)
    {
        var path = userPath ?? DefaultPath;
        var seed = seedPath ?? SeedPath;
        if (File.Exists(path))
        {
            Load(schedule, path);
            if (schedule.Count > 0) return path;
        }
        if (File.Exists(seed)) { Load(schedule, seed); return seed; }
        return null;
    }

    public static void Save(PidGainSchedule schedule, string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var w = new StreamWriter(path, false, System.Text.Encoding.UTF8);
        w.WriteLine("温度(℃),Kp,Ki,Kd,Ku,Tu(s),外环Kp,外环Ki,外环Kd,偏置上限(℃),稳态偏置(℃),加热比");
        foreach (var p in schedule.Points)
        {
            var o = p.OuterGains;
            w.WriteLine(string.Join(',',
                F(p.TemperatureC), F(p.Gains.Kp, "F4"), F(p.Gains.Ki, "F6"), F(p.Gains.Kd, "F4"),
                F(p.UltimateGainKu, "F4"), F(p.UltimatePeriodTuSeconds, "F1"),
                o is null ? "" : F(o.Kp, "F4"),
                o is null ? "" : F(o.Ki, "F6"),
                o is null ? "" : F(o.Kd, "F4"),
                p.OuterMaxBiasC > 0 ? F(p.OuterMaxBiasC, "F2") : "",
                p.SteadyBiasC is { } b ? F(b, "F3") : "",
                p.HeatRatio is { } h ? F(h, "F3") : ""));
        }
    }

    /// <summary>
    /// 读入增益表。列数少于新版的旧文件照常可读（缺的列按"未登记"处理）。
    /// </summary>
    public static void Load(PidGainSchedule schedule, string path)
    {
        if (!File.Exists(path)) return;
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var parts = line.Split(',');
            if (parts.Length < 4) continue;
            if (!TryNum(parts[0], out var t) || !TryNum(parts[1], out var kp) ||
                !TryNum(parts[2], out var ki) || !TryNum(parts[3], out var kd)) continue;

            var ku = Col(parts, 4) ?? 0;
            var tu = Col(parts, 5) ?? 0;

            // 外环三个增益必须齐备才算登记（缺一不可，否则插值会给出半套参数）
            PidGains? outer = Col(parts, 6) is { } okp && Col(parts, 7) is { } oki
                ? new PidGains(okp, oki, Col(parts, 8) ?? 0)
                : null;
            var maxBias = Col(parts, 9) ?? 0;
            if (outer is not null && maxBias <= 0) maxBias = 10;   // 旧文件没写限幅时给默认值

            // 加热比必须为正才有意义，非正值按"未登记"处理
            double? heatRatio = Col(parts, 11) is { } h && h > 0 ? h : null;

            schedule.Learn(new GainPoint(t, new PidGains(kp, ki, kd), ku, tu,
                outer, maxBias, Col(parts, 10), heatRatio));
        }
    }

    private static double? Col(string[] parts, int index) =>
        parts.Length > index && TryNum(parts[index], out var v) ? v : null;

    private static string F(double v, string fmt = "F3") => v.ToString(fmt, CultureInfo.InvariantCulture);

    private static bool TryNum(string s, out double v) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
}
