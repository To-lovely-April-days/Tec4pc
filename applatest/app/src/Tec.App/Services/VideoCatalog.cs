using Tec.Core.Persistence;

namespace Tec.App.Services;

// 视频演示页的目录文件（Resources\Videos\videos.json）。
// 文件格式与内存模型分开写，跟 .tec / .tecbench 一个规矩：格式是对外承诺，
// 现场会照着改这份 JSON，字段名定下就不动。键一律小驼峰（TecJson 的约定）。

public sealed class VideoCatalogDoc
{
    public int Schema { get; set; } = 1;
    public List<VideoCategoryDoc> Categories { get; set; } = new();
}

/// <summary>一个分类：搅拌混合放大 / 软件教程。</summary>
public sealed class VideoCategoryDoc
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>分类列表里那行小字。</summary>
    public string? Sub { get; set; }
    /// <summary>分类页头下面那段引言。</summary>
    public string? Intro { get; set; }
    public List<VideoLessonDoc> Lessons { get; set; } = new();
}

/// <summary>一课。</summary>
public sealed class VideoLessonDoc
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Summary { get; set; }
    /// <summary>视频文件：相对 Resources\Videos 的路径，或绝对路径。空 = 还没录。</summary>
    public string? File { get; set; }
    /// <summary>封面图（可选），路径规则同 File。</summary>
    public string? Poster { get; set; }
    /// <summary>时长（秒）。不知道就不填，界面写「时长待定」，不编一个。</summary>
    public int? DurationSec { get; set; }
    /// <summary>标签：桨型 / 转速 / 粘度 / 课程 / 台面 …</summary>
    public List<string> Tags { get; set; } = new();
    /// <summary>课程要点（章节大纲）。</summary>
    public List<VideoChapterDoc> Chapters { get; set; } = new();
    /// <summary>课程讲义正文（多行文本）。</summary>
    public string? Course { get; set; }
    /// <summary>教程课对应的软件页面（MainViewModel.Tab 的编号）。填了详情里多一个「去这一页」。</summary>
    public int? GoTab { get; set; }
}

public sealed class VideoChapterDoc
{
    /// <summary>在视频里的秒数。不知道就不填，界面显示「—」。</summary>
    public int? At { get; set; }
    public string Title { get; set; } = "";
    public string? Note { get; set; }
}

public static class VideoCatalogFile
{
    /// <summary>读目录文件。文件坏了抛 TecFileException，由页面显示原因——不吞。</summary>
    public static VideoCatalogDoc Load(string path)
    {
        try
        {
            return TecJson.Read<VideoCatalogDoc>(File.ReadAllText(path));
        }
        catch (TecFileException) { throw; }
        catch (Exception ex)
        {
            throw new TecFileException($"videos.json 读不出来：{ex.Message}", ex);
        }
    }
}
