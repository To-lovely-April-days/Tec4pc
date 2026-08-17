using Tec.Driver.Abi;

namespace Tec.Core.Data;

public sealed record SeriesKey(int Channel, string Tag);

/// <summary>定长环形缓冲。实时走它供界面订阅，落盘走批量追加（§8.1）。</summary>
public sealed class RingSeries
{
    private readonly Sample[] _buffer;
    private int _head;
    private int _count;
    private readonly object _gate = new();

    public RingSeries(int capacity = 8192) => _buffer = new Sample[capacity];

    public int Count { get { lock (_gate) return _count; } }
    public int Capacity => _buffer.Length;

    public void Add(in Sample s)
    {
        lock (_gate)
        {
            _buffer[_head] = s;
            _head = (_head + 1) % _buffer.Length;
            if (_count < _buffer.Length) _count++;
        }
    }

    public Sample[] Snapshot()
    {
        lock (_gate)
        {
            var result = new Sample[_count];
            var start = (_head - _count + _buffer.Length) % _buffer.Length;
            for (var i = 0; i < _count; i++)
                result[i] = _buffer[(start + i) % _buffer.Length];
            return result;
        }
    }

    public bool TryLatest(out Sample s)
    {
        lock (_gate)
        {
            if (_count == 0) { s = default; return false; }
            s = _buffer[(_head - 1 + _buffer.Length) % _buffer.Length];
            return true;
        }
    }
}

/// <summary>
/// 采样流 + 事件流两条流。断线时必须转 Bad 或 Stale，
/// 绝不能拿最后一个旧值一直冒充新值——这是最容易犯也最危险的错（§9.4）。
/// </summary>
public sealed class DataPipeline : ISampleSource
{
    private readonly Dictionary<SeriesKey, RingSeries> _series = new();
    private readonly Dictionary<string, TagDescriptor> _tags = new(StringComparer.Ordinal);
    private readonly Broadcast<Sample> _samples = new();
    private readonly object _gate = new();

    public IObservable<Sample> Samples => _samples;
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromSeconds(15);

    public void DescribeTag(TagDescriptor tag)
    {
        lock (_gate) _tags[tag.Tag] = tag;
    }

    public TagDescriptor? Tag(string tag)
    {
        lock (_gate) return _tags.TryGetValue(tag, out var t) ? t : null;
    }

    public IReadOnlyList<TagDescriptor> Tags
    {
        get { lock (_gate) return _tags.Values.ToList(); }
    }

    public void Push(in Sample s)
    {
        RingSeries series;
        var key = new SeriesKey(s.Channel, s.Tag);
        lock (_gate)
        {
            if (!_series.TryGetValue(key, out series!))
            {
                series = new RingSeries();
                _series[key] = series;
            }
        }
        series.Add(s);
        _samples.Push(s);
    }

    public RingSeries? Series(int channel, string tag)
    {
        lock (_gate) return _series.TryGetValue(new SeriesKey(channel, tag), out var s) ? s : null;
    }

    public IReadOnlyList<SeriesKey> Keys
    {
        get { lock (_gate) return _series.Keys.ToList(); }
    }

    /// <summary>某一路的全部点。没有这一路就是空数组——调用方不必再判一次 null。</summary>
    public Sample[] Snapshot(int channel, string tag)
        => Series(channel, tag)?.Snapshot() ?? Array.Empty<Sample>();

    /// <summary>取最新值，超过 StaleAfter 自动降级为 Stale——不允许旧值冒充新值。</summary>
    public bool TryLatest(int channel, string tag, DateTimeOffset now, out Sample sample)
    {
        sample = default;
        var s = Series(channel, tag);
        if (s is null || !s.TryLatest(out var latest)) return false;
        sample = now - latest.WallClock > StaleAfter && latest.Quality == Quality.Good
            ? latest with { Quality = Quality.Stale }
            : latest;
        return true;
    }

    public void Clear()
    {
        lock (_gate) _series.Clear();
    }
}
