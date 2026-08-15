using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Tec.Core.Records;

/// <summary>
/// GLP 记录落盘：**只追加**，不允许更新或删除；修正走"追加一条更正事件"（§8.3）。
/// 一行一条，行内是制表分隔的字段，末尾带链式摘要——
/// 任何一行被改过，之后所有行的摘要都对不上。
/// </summary>
public sealed class RecordStore : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly object _gate = new();
    private string _chain;

    public RecordStore(string path, string? seed = null)
    {
        Path = path;
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var isNew = !File.Exists(path);
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read),
                                   new UTF8Encoding(false)) { AutoFlush = true };
        _chain = seed ?? "TecStudio";
        if (isNew) WriteLine("HEAD", "schema=1", "created=" + Fmt.Stamp(DateTimeOffset.Now));
    }

    public string Path { get; }
    public string Chain { get { lock (_gate) return _chain; } }

    public void Write(ChannelRun run)
    {
        WriteLine("RUN",
            "ch=" + run.Channel,
            "started=" + Fmt.Stamp(run.StartedAt),
            "recipe=" + run.Baseline.Recipe.Name,
            "steps=" + run.Baseline.Recipe.Steps.Count,
            "planned=" + Fmt.Hms(run.Baseline.Schedule.Total),
            "simulated=" + (run.Simulated ? "1" : "0"),
            "operator=" + (run.Operator ?? ""));
    }

    public void Write(int channel, StepRecord s)
        => WriteLine("STEP",
            "ch=" + channel,
            "idx=" + s.Index,
            "iter=" + s.Iteration,
            "cmd=" + s.CommandId,
            "title=" + s.Title,
            "planStart=" + Fmt.Hms(s.PlanStart),
            "planDur=" + Fmt.Hms(s.PlanDuration),
            "actStart=" + (s.ActualStart is { } a ? Fmt.Stamp(a) : ""),
            "actDur=" + (s.ActualDuration is { } d ? Fmt.Hms(d) : ""),
            "devStart=" + (s.StartDeviation is { } sd ? Fmt.Signed(sd) : ""),
            "devDur=" + (s.DurationDeviation is { } dd ? Fmt.Signed(dd) : ""),
            "end=" + (s.Reason?.ToString() ?? ""),
            "status=" + s.Status);

    public void Write(EventRecord e)
        => WriteLine("EVENT",
            "ch=" + e.Channel,
            "at=" + Fmt.Stamp(e.At),
            "kind=" + e.Kind,
            "text=" + e.Text,
            "before=" + (e.Before ?? ""),
            "after=" + (e.After ?? ""),
            "user=" + (e.User ?? ""));

    /// <summary>电子签名。依赖真实身份体系，否则只是个可以随便填的输入框（§8.3）。</summary>
    public void Sign(string user, string reason)
        => WriteLine("SIGN", "user=" + user, "reason=" + reason, "at=" + Fmt.Stamp(DateTimeOffset.Now));

    private void WriteLine(string kind, params string[] fields)
    {
        lock (_gate)
        {
            var body = kind + "\t" + string.Join("\t", fields.Select(Clean));
            _chain = Hash(_chain + "\n" + body);
            _writer.WriteLine(body + "\t#" + _chain[..16]);
        }
    }

    private static string Clean(string s)
        => s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    private static string Hash(string s)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))
                  .ToLower(CultureInfo.InvariantCulture);

    /// <summary>回读并校验链。任何一行被改过，从那行起全部对不上。</summary>
    public static (bool Ok, int BadLine) Verify(string path, string? seed = null)
    {
        var chain = seed ?? "TecStudio";
        var lines = File.ReadAllLines(path, Encoding.UTF8);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var cut = line.LastIndexOf("\t#", StringComparison.Ordinal);
            if (cut < 0) return (false, i + 1);
            var body = line[..cut];
            var stamp = line[(cut + 2)..];
            chain = Hash(chain + "\n" + body);
            if (!chain.StartsWith(stamp, StringComparison.Ordinal)) return (false, i + 1);
        }
        return (true, 0);
    }

    public void Dispose() => _writer.Dispose();
}
