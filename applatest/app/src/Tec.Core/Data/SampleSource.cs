using Tec.Driver.Abi;

namespace Tec.Core.Data;

/// <summary>
/// 一批采样从哪儿来。
///
/// 从前导出器直接吃 <see cref="DataPipeline"/>，于是「能导出的」和
/// 「此刻还在环形缓冲里的」是同一件事：程序一关，跑过的那几炉就再也导不出来了。
/// 归档回来的采样不在管线里，但它和管线里的点是同一种东西——
/// 导出器只需要「给我这一路的点」，不必知道点是从内存还是从盘上来的。
/// </summary>
public interface ISampleSource
{
    /// <summary>有哪些「通道 × 标签」。</summary>
    IReadOnlyList<SeriesKey> Keys { get; }

    /// <summary>某一路的全部点，按时间升序。没有这一路就是空数组。</summary>
    Sample[] Snapshot(int channel, string tag);

    /// <summary>标签的中文名与单位。认不出来就返回 null，调用方拿原始标签名顶上。</summary>
    TagDescriptor? Tag(string tag);
}

/// <summary>
/// 内存里的一份采样（归档读回来的就是它）。
/// 只读——读回来的历史数据不该有人再往里加点。
/// </summary>
public sealed class SampleSet : ISampleSource
{
    private readonly Dictionary<SeriesKey, Sample[]> _series = new();
    private readonly Dictionary<string, TagDescriptor> _tags = new(StringComparer.Ordinal);

    public IReadOnlyList<SeriesKey> Keys => _series.Keys.ToList();

    public Sample[] Snapshot(int channel, string tag)
        => _series.TryGetValue(new SeriesKey(channel, tag), out var s) ? s : Array.Empty<Sample>();

    public TagDescriptor? Tag(string tag) => _tags.TryGetValue(tag, out var t) ? t : null;

    public void Add(int channel, string tag, Sample[] points)
        => _series[new SeriesKey(channel, tag)] = points;

    public void Describe(TagDescriptor tag) => _tags[tag.Tag] = tag;

    /// <summary>点数合计。归档页要照实说「这份归档里还剩多少点」。</summary>
    public long Count => _series.Values.Sum(v => (long)v.Length);
}
