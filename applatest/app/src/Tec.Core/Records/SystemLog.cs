using System.Globalization;
using System.Text;

namespace Tec.Core.Records;

public enum LogLevel { Info, Warn, Error }

public sealed record LogEntry(DateTimeOffset At, LogLevel Level, string Category, string User, string Text)
{
    public string LevelWord => Level switch { LogLevel.Warn => "警告", LogLevel.Error => "错误", _ => "信息" };
}

/// <summary>
/// 系统日志。**只追加**，一个月一个文件，制表分隔——用记事本就能打开，
/// 不需要本程序也读得懂（审计的人手里未必装着 TecStudio）。
///
/// 它和 <see cref="RecordStore"/> 分工不同：RecordStore 记的是**某一炉里发生了什么**，
/// 带链式摘要、进归档随实验走；这里记的是**这台机器上有人做了什么**——
/// 开关机、起停批次、导出了哪几条到哪儿。一次导出在两边都留痕：
/// 实验记录里说不出「谁把它拷走了」，那正是系统日志要回答的。
/// </summary>
public sealed class SystemLog
{
    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _now;

    public SystemLog(string dir, Func<DateTimeOffset>? now = null)
    {
        Dir = dir;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public string Dir { get; }

    /// <summary>当月的日志文件。跨月自动换一份，单个文件不会无限长。</summary>
    public string PathOf(DateTimeOffset at) => Path.Combine(Dir, $"system-{at:yyyyMM}.log");
    public string CurrentPath => PathOf(_now());

    /// <summary>写不进去也不能把调用方掀翻——日志是旁路，导出本身已经成了就不该因为它报失败。</summary>
    public bool Write(string category, string text, string user = "", LogLevel level = LogLevel.Info)
    {
        var at = _now();
        var line = string.Join('\t',
            at.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture),
            level switch { LogLevel.Warn => "WARN", LogLevel.Error => "ERROR", _ => "INFO" },
            Clean(category), Clean(user), Clean(text));
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(Dir);
                var path = PathOf(at);
                var isNew = !File.Exists(path);
                using var w = new StreamWriter(
                    new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                    new UTF8Encoding(true));
                if (isNew) w.WriteLine("# TecStudio 系统日志\t时刻\t级别\t类别\t操作人\t内容");
                w.WriteLine(line);
            }
            LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    /// <summary>上一次写失败的原因。界面要能照实说「日志没写进去」，而不是假装写了。</summary>
    public string? LastError { get; private set; }

    /// <summary>回读最近若干条，新的在前。给日志查看用。</summary>
    public IReadOnlyList<LogEntry> Tail(int max = 500)
    {
        var list = new List<LogEntry>();
        if (!Directory.Exists(Dir)) return list;
        var files = Directory.GetFiles(Dir, "system-*.log")
                             .OrderByDescending(x => x, StringComparer.Ordinal);
        foreach (var f in files)
        {
            string[] lines;
            try { lines = File.ReadAllLines(f, Encoding.UTF8); }
            catch { continue; }
            for (var i = lines.Length - 1; i >= 0 && list.Count < max; i--)
            {
                var line = lines[i];
                if (line.Length == 0 || line[0] == '#') continue;
                var p = line.Split('\t');
                if (p.Length < 5) continue;
                if (!DateTimeOffset.TryParse(p[0], CultureInfo.InvariantCulture,
                                             DateTimeStyles.None, out var at)) continue;
                var level = p[1] switch { "WARN" => LogLevel.Warn, "ERROR" => LogLevel.Error, _ => LogLevel.Info };
                list.Add(new LogEntry(at, level, p[2], p[3], p[4]));
            }
            if (list.Count >= max) break;
        }
        return list;
    }

    private static string Clean(string s)
        => s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
}
