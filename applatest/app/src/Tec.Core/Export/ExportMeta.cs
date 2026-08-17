namespace Tec.Core.Export;

/// <summary>
/// 一次导出的「出处」。
///
/// 导出的文件离开程序之后，只剩这些字能解释它是什么：哪台机器上、谁、什么时候、
/// 按什么设置、从哪一份数据导的。Excel 的概要页、Word / PDF 的封面与页脚
/// 读的都是这一份，三种格式不各写一套——各写一套迟早对不上。
/// </summary>
public sealed class ExportMeta
{
    public required string Experiment { get; init; }
    public required string Operator { get; init; }
    public required DateTimeOffset ExportedAt { get; init; }
    public string Signer { get; init; } = "";
    public string BenchName { get; init; } = "";
    public string AppVersion { get; init; } = "";
    /// <summary>采样间隔的说法（「10 s」这种），照界面上选的写。</summary>
    public string Interval { get; init; } = "";
    /// <summary>时间基准的说法（「绝对时间」这种）。</summary>
    public string TimeBaseText { get; init; } = "";
    /// <summary>这一炉是从归档读回来的（不是这一开机跑的）。</summary>
    public bool Archived { get; init; }
    /// <summary>归档时采样已经被环形缓冲顶掉过一截。**必须写在文件里**，别让人事后才发现。</summary>
    public bool SamplesTruncated { get; init; }
    /// <summary>导出目录。写进概要页，事后能对上文件从哪儿来。</summary>
    public string TargetDir { get; init; } = "";
}
