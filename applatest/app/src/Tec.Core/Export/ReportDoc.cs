namespace Tec.Core.Export;

/// <summary>报告模板。三种，选哪种决定报告里有哪几节、每节多细。</summary>
public enum ReportTemplate
{
    /// <summary>完整实验报告：封面 + 概要 + 趋势曲线 + 步骤执行记录 + 事件与报警 + 配方与台面 + 签名页。</summary>
    Full,
    /// <summary>实验摘要：一到两页，概要 + 曲线 + 关键偏差。交班、贴实验记录本用。</summary>
    Summary,
    /// <summary>GLP 归档报告：完整报告 + 审计追踪全文 + 完整性校验码 + 签名块。</summary>
    Glp
}

public static class ReportTemplates
{
    public static string NameOf(ReportTemplate t) => t switch
    {
        ReportTemplate.Summary => "实验摘要",
        ReportTemplate.Glp => "GLP 归档报告",
        _ => "完整实验报告"
    };

    public static string DescOf(ReportTemplate t) => t switch
    {
        ReportTemplate.Summary => "一到两页：概要、趋势曲线、超差的步骤。交班和贴实验记录本用",
        ReportTemplate.Glp => "完整报告之外再附审计追踪全文、链式校验码与签名块",
        _ => "封面、概要、趋势曲线、步骤执行记录、事件与报警、配方与台面"
    };

    public static IReadOnlyList<ReportTemplate> All =>
        new[] { ReportTemplate.Full, ReportTemplate.Summary, ReportTemplate.Glp };
}

public abstract class ReportBlock { }

public sealed class HeadingBlock : ReportBlock
{
    public required int Level { get; init; }          // 1 = 章，2 = 节
    public required string Text { get; init; }
}

public sealed class ParaBlock : ReportBlock
{
    public required string Text { get; init; }
    public bool Muted { get; init; }
}

/// <summary>要人看见的一句话：仿真数据、已中止、采样不全。带底色，不是普通段落。</summary>
public sealed class NoticeBlock : ReportBlock
{
    public required string Text { get; init; }
    public bool Bad { get; init; }
}

public sealed class KvBlock : ReportBlock
{
    public string? Title { get; init; }
    public required IReadOnlyList<(string K, string V)> Pairs { get; init; }
    public int Columns { get; init; } = 2;
}

public sealed class TableCol
{
    public required string Name { get; init; }
    /// <summary>相对宽度权重。列宽按权重瓜分正文宽度。</summary>
    public double Weight { get; init; } = 1;
    /// <summary>数字列右对齐。一列「到达目标温度」右对齐排下来像数字栏里混进了字。</summary>
    public bool Right { get; init; }
}

public sealed class TableBlock : ReportBlock
{
    public string? Title { get; init; }
    public required IReadOnlyList<TableCol> Columns { get; init; }
    public required IReadOnlyList<string[]> Rows { get; init; }
    /// <summary>要标红的行号（0 起）：中止的步骤、报警。</summary>
    public IReadOnlyCollection<int> BadRows { get; init; } = Array.Empty<int>();
    public string? Note { get; init; }
    /// <summary>
    /// 一格最多折几行。默认 1（放不下就掐掉加省略号）。
    ///
    /// 步骤表这种列多的，A4 竖排下「保持当前温度 2 min，允差 ±0 ℃」一行放不下，
    /// 全掐掉的话报告上只剩「保持当前温…」——那一列正是人要看的东西。
    /// </summary>
    public int MaxLines { get; init; } = 1;
}

/// <summary>
/// 一张图。**存的是原始像素不是 PNG**：图由界面层用 Avalonia 画出来，
/// 再编码成 PNG、这边又解一遍，白白多两道工序，还得为此写一个 PNG 解码器。
/// </summary>
public sealed class ImageBlock : ReportBlock
{
    public string? Title { get; init; }
    /// <summary>BGRA8888，行优先，行长 = <see cref="PixelWidth"/> × 4。</summary>
    public required byte[] Bgra { get; init; }
    public required int PixelWidth { get; init; }
    public required int PixelHeight { get; init; }
    /// <summary>图在纸上占多宽（正文宽度的比例，0–1）。高按像素比例算。</summary>
    public double WidthRatio { get; init; } = 1;
    public string? Note { get; init; }
}

public sealed class PageBreakBlock : ReportBlock { }

/// <summary>
/// 一份报告的**内容**（不含排版）。
///
/// Word 和 PDF 读的是同一份：Word 是流式的，交给 Word 自己分页；
/// PDF 要自己分页，所以再过一道 <see cref="ReportLayout"/>。
/// 内容只写一遍，两种格式里的字句就不会有第三种说法。
/// </summary>
public sealed class ReportDoc
{
    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public required ReportTemplate Template { get; init; }
    /// <summary>封面上那张信息表。</summary>
    public List<(string K, string V)> Cover { get; } = new();
    public List<ReportBlock> Blocks { get; } = new();
    /// <summary>每页页脚左边那行字：实验名 · 记录编号。</summary>
    public string FooterLeft { get; set; } = "";
    /// <summary>整炉里有仿真通道。封面和每页页脚都要标出来（§8.1）。</summary>
    public bool Simulated { get; set; }
}
