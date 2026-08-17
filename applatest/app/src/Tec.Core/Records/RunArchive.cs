using System.Globalization;
using System.IO.Compression;
using System.Text;
using Tec.Core.Data;
using Tec.Core.Persistence;
using Tec.Driver.Abi;

namespace Tec.Core.Records;

/// <summary>标签的中文名与单位。采样文件里只有数字，没有它读回来全是 Tr / Tj 这种代号。</summary>
public sealed class TagDoc
{
    public string Tag { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Unit { get; set; }
    public DataShape Shape { get; set; } = DataShape.Scalar;
    public TimeSpan? Period { get; set; }
}

/// <summary>归档目录里的一炉：记录 + 采样 + 它躺在哪儿。</summary>
public sealed class ArchivedRun
{
    public required RunRecord Record { get; init; }
    public required SampleSet Samples { get; init; }
    public required string Dir { get; init; }
    public required DateTimeOffset ArchivedAt { get; init; }
    /// <summary>
    /// 采样是不是完整的一炉。
    ///
    /// 采样在定长环形缓冲里，归档写的是**写那一刻还留着的点**。一炉跑得够久、
    /// 或者中间又跑了几炉，前半段早被顶掉了。这里照实记下来，导出页才说得出
    /// 「这份归档只剩后半段」，而不是让人导出来才发现少了一截。
    /// </summary>
    public bool SamplesTruncated { get; init; }
}

/// <summary>
/// 批次归档。**跨次开机的记录靠它**——从前批次只在内存里，关掉程序
/// 跑过的那几炉就再也导不出来了，导出页只能摆这一开机的几行。
///
/// 一炉一个目录：run.json（执行记录，只追加的那份事实）+ tags.json（标签说明）
/// + samples.tsv.gz（采样，制表分隔、gzip）。采样单独放是因为它比记录大两个数量级，
/// 而列表页只需要记录——读列表不该把几十兆采样一起拖进内存。
/// </summary>
public sealed class RunArchive
{
    private const string RunFile = "run.json";
    private const string TagFile = "tags.json";
    private const string SampleFile = "samples.tsv.gz";
    private const string TruncFlag = "# truncated";

    public RunArchive(string root, string appVersion = "")
    {
        Root = root;
        AppVersion = appVersion;
    }

    public string Root { get; }
    public string AppVersion { get; }

    /// <summary>
    /// 写一炉。已经写过的**整份重写**——一炉跑到一半归档过，跑完还要把后半段补上。
    /// 先写临时文件再改名：写到一半断电的话，留下的是上一份完整归档，不是半份。
    /// </summary>
    public string Save(RunRecord rec, ISampleSource samples, DateTimeOffset now, bool truncated = false)
    {
        var dir = Path.Combine(Root, Safe(rec.RunId));
        Directory.CreateDirectory(dir);

        WriteAtomic(Path.Combine(dir, RunFile), TecJson.Write(rec.ToDoc(now, AppVersion)));

        var chs = rec.StartedChannels.ToHashSet();
        var keys = samples.Keys.Where(k => chs.Contains(k.Channel)).ToList();

        var tags = keys.Select(k => k.Tag).Distinct(StringComparer.Ordinal)
            .Select(t => samples.Tag(t))
            .Where(t => t is not null)
            .Select(t => new TagDoc
            {
                Tag = t!.Tag, DisplayName = t.DisplayName, Unit = t.Unit,
                Shape = t.Shape, Period = t.Period
            })
            .ToList();
        WriteAtomic(Path.Combine(dir, TagFile), TecJson.Write(tags));

        // 只写这一炉时间窗内的点。管线是全局的，不筛的话会把别的炉的点一起写进来
        var from = rec.FirstStart ?? rec.CreatedAt;
        var to = rec.Channels.Count == 0 ? now : rec.Channels.Max(c => c.FinishedAt ?? now);

        var tmp = Path.Combine(dir, SampleFile + ".tmp");
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
        using (var w = new StreamWriter(gz, new UTF8Encoding(false)))
        {
            w.WriteLine("# TecStudio 采样归档 v1\tch\ttag\twall(ticks)\tmono(ticks)\tvalue\tquality");
            if (truncated) w.WriteLine(TruncFlag);
            foreach (var k in keys.OrderBy(k => k.Channel).ThenBy(k => k.Tag, StringComparer.Ordinal))
                foreach (var s in samples.Snapshot(k.Channel, k.Tag))
                {
                    if (s.WallClock < from || s.WallClock > to) continue;
                    w.Write(k.Channel); w.Write('\t');
                    w.Write(k.Tag); w.Write('\t');
                    w.Write(s.WallClock.UtcTicks.ToString(CultureInfo.InvariantCulture)); w.Write('\t');
                    w.Write(s.MonotonicTicks.ToString(CultureInfo.InvariantCulture)); w.Write('\t');
                    w.Write(s.Value.ToString("R", CultureInfo.InvariantCulture)); w.Write('\t');
                    w.Write((int)s.Quality);
                    w.Write('\n');
                }
        }
        Replace(tmp, Path.Combine(dir, SampleFile));
        return dir;
    }

    /// <summary>目录里有哪些炉（只读 run.json，不碰采样）。读不动的那一份跳过，不让它拖垮整张表。</summary>
    public IReadOnlyList<ArchivedRun> Load()
    {
        var list = new List<ArchivedRun>();
        if (!Directory.Exists(Root)) return list;

        foreach (var dir in Directory.EnumerateDirectories(Root).OrderBy(x => x, StringComparer.Ordinal))
        {
            var path = Path.Combine(dir, RunFile);
            if (!File.Exists(path)) continue;
            try
            {
                var doc = TecJson.Read<RunDoc>(File.ReadAllText(path, Encoding.UTF8));
                if (doc.Schema > RunDoc.CurrentSchema) continue;   // 新版程序写的，读不了就不假装读懂
                list.Add(new ArchivedRun
                {
                    Record = doc.ToModel(),
                    Samples = ReadSamples(dir, out var trunc),
                    Dir = dir,
                    ArchivedAt = doc.ArchivedAt,
                    SamplesTruncated = trunc
                });
            }
            catch (Exception ex)
            {
                Failures.Add($"{Path.GetFileName(dir)}：{ex.Message}");
            }
        }
        return list;
    }

    /// <summary>读不回来的归档目录。界面照实说「有 N 份归档读不回来」，不悄悄吞掉。</summary>
    public List<string> Failures { get; } = new();

    /// <summary>已经归档过的记录编号。开新批次时拿它避开撞号（编号是导出文件的目录名）。</summary>
    public IReadOnlyList<string> KnownIds()
    {
        if (!Directory.Exists(Root)) return Array.Empty<string>();
        return Directory.EnumerateDirectories(Root)
                        .Where(d => File.Exists(Path.Combine(d, RunFile)))
                        .Select(d => Path.GetFileName(d)!)
                        .ToList();
    }

    private static SampleSet ReadSamples(string dir, out bool truncated)
    {
        truncated = false;
        var set = new SampleSet();

        var tagPath = Path.Combine(dir, TagFile);
        if (File.Exists(tagPath))
        {
            try
            {
                foreach (var t in TecJson.Read<List<TagDoc>>(File.ReadAllText(tagPath, Encoding.UTF8)))
                    set.Describe(new TagDescriptor(t.Tag, t.DisplayName, t.Unit, t.Shape) { Period = t.Period });
            }
            catch { /* 标签说明丢了不该让整炉读不回来，退回用标签名本身 */ }
        }

        var path = Path.Combine(dir, SampleFile);
        if (!File.Exists(path)) return set;

        var buckets = new Dictionary<SeriesKey, List<Sample>>();
        using (var fs = File.OpenRead(path))
        using (var gz = new GZipStream(fs, CompressionMode.Decompress))
        using (var r = new StreamReader(gz, Encoding.UTF8))
        {
            string? line;
            while ((line = r.ReadLine()) is not null)
            {
                if (line.Length == 0) continue;
                if (line[0] == '#') { if (line.StartsWith(TruncFlag, StringComparison.Ordinal)) truncated = true; continue; }
                var f = line.Split('\t');
                if (f.Length < 6) continue;
                if (!int.TryParse(f[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ch)) continue;
                if (!long.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var wall)) continue;
                if (!long.TryParse(f[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mono)) continue;
                if (!double.TryParse(f[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) continue;
                _ = int.TryParse(f[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var q);

                var key = new SeriesKey(ch, f[1]);
                if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = new List<Sample>();
                list.Add(new Sample(ch, f[1], mono,
                                    new DateTimeOffset(wall, TimeSpan.Zero).ToLocalTime(),
                                    v, (Quality)q));
            }
        }
        foreach (var (k, v) in buckets) set.Add(k.Channel, k.Tag, v.ToArray());
        return set;
    }

    /// <summary>记录编号进文件名，先挡一道非法字符——编号是程序自己生成的，但归档目录归用户所有。</summary>
    private static string Safe(string id)
    {
        var bad = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(id.Length);
        foreach (var c in id) sb.Append(Array.IndexOf(bad, c) >= 0 ? '_' : c);
        var s = sb.ToString().Trim();
        return s.Length == 0 ? "run" : s;
    }

    private static void WriteAtomic(string path, string text)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        Replace(tmp, path);
    }

    private static void Replace(string tmp, string path)
    {
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
    }
}
