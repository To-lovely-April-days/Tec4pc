using Tec.Hmi.Ui.ViewModels;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Tec.App.Services;
using Tec.Core.Persistence;

namespace Tec.App.ViewModels;

/// <summary>
/// 视频演示页：两个分类——**搅拌混合放大**（桨型 / 转速 / 粘度对比，配套混合
/// 放大课程）与**软件教程**（仪器配置 → 实验设计 → 运行 → 数据分析 → 化合物库
/// → 报告 → 结果输出）。
///
/// 目录从 Resources\Videos\videos.json 读，视频文件散装在同一目录——跟登录页
/// 那张图同一套「现场可替换」规矩：换视频、加一课，改文件不用重新出包。
/// 铁规矩照旧：没录的课照实标「待录制」，目录里写了文件却没放进来的标
/// 「未放入」，能放的才叫「可播放」；页面上没有一个假进度条、假播放器。
/// </summary>
public sealed class VideoViewModel : ViewModelBase
{
    private readonly Workspace _ws;
    private readonly MainViewModel _shell;
    private readonly IVideoPlayback _playback = new ExternalVideoPlayback();
    private VideoCategoryVm? _cat;
    private VideoLessonVm? _sel;
    private string _statusLine = "";

    /// <summary>视频目录：exe 旁边 Resources\Videos（见 Resources\说明.txt）。</summary>
    public static string VideoDir => Path.Combine(LoginViewModel.ResourceDir, "Videos");
    public static string CatalogPath => Path.Combine(VideoDir, "videos.json");

    public VideoViewModel(Workspace ws, MainViewModel shell)
    {
        _ws = ws;
        _shell = shell;
        Play = new RelayCommand(DoPlay);
        Refresh = new RelayCommand(Reload);
        OpenFolder = new RelayCommand(DoOpenFolder);
        GoPage = new RelayCommand(DoGoPage);
        Reload();
    }

    public ObservableCollection<VideoCategoryVm> Categories { get; } = new();
    public ObservableCollection<VideoLessonVm> Lessons { get; } = new();

    public RelayCommand Play { get; }
    public RelayCommand Refresh { get; }
    public RelayCommand OpenFolder { get; }
    public RelayCommand GoPage { get; }

    // ── 目录状态 ─────────────────────────────────────────────────────

    public bool HasCatalog { get; private set; }
    /// <summary>目录文件读坏了的原因；没坏就是空。</summary>
    public string CatalogError { get; private set; } = "";
    public bool HasCatalogError => CatalogError.Length > 0;
    public string CatCount => Categories.Count == 0 ? "" : $"{Categories.Count} 个分类";
    public string DirText => VideoDir;

    public string EmptyTitle => HasCatalogError ? "视频目录读不出来" : "还没有视频目录";
    public string EmptyBody => HasCatalogError
        ? CatalogError + "\n改好文件后点左下角的刷新。"
        : $"把 videos.json 和视频文件放进程序目录的 Resources\\Videos 文件夹：\n{VideoDir}\n目录文件的写法见 Resources\\说明.txt；程序自带一份目录模板，只是视频还没录。";

    // ── 当前分类 / 当前课 ───────────────────────────────────────────

    public VideoCategoryVm? Cat => _cat;
    public bool HasCategory => _cat is not null;
    public string CatName => _cat?.Name ?? "";
    public string CatIntro => _cat?.Intro ?? "";
    public bool HasCatIntro => !string.IsNullOrWhiteSpace(_cat?.Intro);
    public string CatMeta => _cat is null ? "" : _cat.CountText;

    public VideoLessonVm? Sel => _sel;
    public bool HasLesson => _sel is not null;

    /// <summary>页面底部那句状态（放了什么、为什么没放出来）。</summary>
    public string StatusLine
    {
        get => _statusLine;
        private set => Set(ref _statusLine, value);
    }
    public bool HasStatus => _statusLine.Length > 0;

    // ── 读目录 ───────────────────────────────────────────────────────

    /// <summary>每次切到这一页、或点刷新都重读一遍：现场改了文件立刻能看见。</summary>
    public void Reload()
    {
        var keepCat = _cat?.Key;
        var keepLesson = _sel?.Id;
        Categories.Clear();
        Lessons.Clear();
        _cat = null;
        _sel = null;
        CatalogError = "";
        HasCatalog = false;

        if (File.Exists(CatalogPath))
        {
            try
            {
                var doc = VideoCatalogFile.Load(CatalogPath);
                foreach (var c in doc.Categories)
                    Categories.Add(new VideoCategoryVm(c, VideoDir));
                HasCatalog = true;
            }
            catch (TecFileException ex)
            {
                CatalogError = ex.Message;
            }
        }

        var cat = Categories.FirstOrDefault(c => c.Key == keepCat) ?? Categories.FirstOrDefault();
        if (cat is not null) PickCategory(cat, keepLesson);
        // 目录没了（文件被拿走 / 读坏）也要把页头和详情栏一起清掉——
        // 上面 _cat/_sel 已经置空，不通知的话界面还挂着上一次的分类和课，
        // 播放钮亮着指向一个目录里已经不存在的文件
        StatusLine = "";
        RaiseSelection();
        RaiseCatalog();
    }

    private void RaiseSelection() => RaiseAll(nameof(Cat), nameof(HasCategory), nameof(CatName),
        nameof(CatIntro), nameof(HasCatIntro), nameof(CatMeta), nameof(Sel), nameof(HasLesson), nameof(HasStatus));

    public void PickCategory(VideoCategoryVm cat) => PickCategory(cat, null);

    private void PickCategory(VideoCategoryVm cat, string? keepLesson)
    {
        foreach (var c in Categories) c.IsSelected = ReferenceEquals(c, cat);
        _cat = cat;
        Lessons.Clear();
        foreach (var l in cat.Lessons) Lessons.Add(l);
        var sel = cat.Lessons.FirstOrDefault(l => l.Id == keepLesson) ?? cat.Lessons.FirstOrDefault();
        PickLesson(sel);
        RaiseSelection();
    }

    public void PickLesson(VideoLessonVm? lesson)
    {
        foreach (var l in Lessons) l.IsSelected = lesson is not null && ReferenceEquals(l, lesson);
        _sel = lesson;
        StatusLine = "";
        RaiseAll(nameof(Sel), nameof(HasLesson), nameof(HasStatus));
    }

    private void RaiseCatalog() => RaiseAll(nameof(HasCatalog), nameof(CatalogError), nameof(HasCatalogError),
        nameof(CatCount), nameof(EmptyTitle), nameof(EmptyBody), nameof(StatusLine), nameof(HasStatus));

    // ── 操作 ─────────────────────────────────────────────────────────

    private void DoPlay()
    {
        if (_sel is not { } l) return;
        if (!l.CanPlay)
        {
            // 灰按钮点不到；这里是键盘/命令路径兜底——同样说清为什么
            StatusLine = l.PlayHint;
            Raise(nameof(HasStatus));
            return;
        }
        if (_playback.TryPlay(l.FilePath!, out var err))
        {
            StatusLine = $"已交给{_playback.Name}播放：{l.Title}";
            _ws.Log.Write("视频", $"播放「{l.Title}」（{_playback.Name}）", _ws.Operator);
        }
        else
        {
            StatusLine = err ?? "播放失败";
            _ws.Log.Write("视频", $"播放「{l.Title}」失败：{StatusLine}", _ws.Operator, Tec.Core.Records.LogLevel.Warn);
        }
        Raise(nameof(HasStatus));
    }

    private void DoOpenFolder()
    {
        try
        {
            // 目录不在就建出来再开：现场第一次放视频，开一个空文件夹比报错有用
            Directory.CreateDirectory(VideoDir);
            Process.Start(new ProcessStartInfo(VideoDir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusLine = $"打不开文件夹：{ex.Message}";
            Raise(nameof(HasStatus));
        }
    }

    private void DoGoPage()
    {
        if (_sel?.GoTab is { } tab) _shell.Tab = tab;
    }
}

/// <summary>分类（左栏一行）。</summary>
public sealed class VideoCategoryVm : ViewModelBase
{
    private bool _sel;

    public VideoCategoryVm(VideoCategoryDoc doc, string dir)
    {
        Key = doc.Key;
        Name = doc.Name;
        Sub = doc.Sub ?? "";
        Intro = doc.Intro ?? "";
        Lessons = doc.Lessons.Select(l => new VideoLessonVm(l, dir)).ToList();
    }

    public string Key { get; }
    public string Name { get; }
    public string Sub { get; }
    public string Intro { get; }
    public IReadOnlyList<VideoLessonVm> Lessons { get; }

    /// <summary>「7 课 · 0 可播放」——可播放的数照实数，不把待录的算进去。</summary>
    public string CountText
    {
        get
        {
            var ready = Lessons.Count(l => l.CanPlay);
            return $"{Lessons.Count} 课 · {ready} 可播放";
        }
    }

    public bool IsSelected
    {
        get => _sel;
        set => Set(ref _sel, value);
    }
}

/// <summary>一课（中间卡片 + 右栏详情共用）。</summary>
public sealed class VideoLessonVm : ViewModelBase
{
    private bool _sel;

    public VideoLessonVm(VideoLessonDoc doc, string dir)
    {
        Id = doc.Id;
        Title = doc.Title;
        Summary = doc.Summary ?? "";
        Tags = doc.Tags;
        Course = doc.Course ?? "";
        GoTab = doc.GoTab;
        Chapters = doc.Chapters.Select(c => new VideoChapterVm(c)).ToList();
        DurationText = doc.DurationSec is { } s ? Hms(s) : "时长待定";

        // 三态：没写文件名 = 待录制；写了没放 = 未放入；放了 = 可播放
        if (string.IsNullOrWhiteSpace(doc.File))
        {
            Status = LessonStatus.Todo;
            PlayHint = "这一课还没有录制。视频录好后把文件名填进 videos.json 的 file 字段。";
        }
        else
        {
            FilePath = Path.IsPathRooted(doc.File) ? doc.File : Path.Combine(dir, doc.File);
            if (File.Exists(FilePath))
            {
                Status = LessonStatus.Ready;
                PlayHint = "在系统播放器中打开。";
            }
            else
            {
                Status = LessonStatus.Missing;
                PlayHint = $"目录里写了这个文件，但文件夹里没有：\n{FilePath}";
            }
        }

        if (!string.IsNullOrWhiteSpace(doc.Poster))
        {
            var p = Path.IsPathRooted(doc.Poster) ? doc.Poster : Path.Combine(dir, doc.Poster);
            if (File.Exists(p))
            {
                try { Poster = new Avalonia.Media.Imaging.Bitmap(p); }
                catch { /* 图坏了当没有，卡片显示占位，别让整页开不出来 */ }
            }
        }
    }

    public string Id { get; }
    public string Title { get; }
    public string Summary { get; }
    public bool HasSummary => Summary.Length > 0;
    public IReadOnlyList<string> Tags { get; }
    public bool HasTags => Tags.Count > 0;
    public string TagLine => string.Join(" · ", Tags);
    public string DurationText { get; }
    /// <summary>卡片底下那行：「时长待定 · 桨型」。</summary>
    public string MetaLine => HasTags ? $"{DurationText} · {TagLine}" : DurationText;
    public IReadOnlyList<VideoChapterVm> Chapters { get; }
    public bool HasChapters => Chapters.Count > 0;
    public string Course { get; }
    public bool HasCourse => Course.Length > 0;
    public int? GoTab { get; }
    public bool HasGo => GoTab is not null;

    public string? FilePath { get; }
    public string FileText => FilePath ?? "（未指定文件）";
    public Avalonia.Media.Imaging.Bitmap? Poster { get; }
    public bool HasPoster => Poster is not null;

    public LessonStatus Status { get; }
    public bool IsReady => Status == LessonStatus.Ready;
    public bool IsMissing => Status == LessonStatus.Missing;
    public bool IsTodo => Status == LessonStatus.Todo;
    public bool CanPlay => IsReady;
    public string StatusText => Status switch
    {
        LessonStatus.Ready => "可播放",
        LessonStatus.Missing => "未放入",
        _ => "待录制"
    };
    public string PlayHint { get; }

    public bool IsSelected
    {
        get => _sel;
        set => Set(ref _sel, value);
    }

    private static string Hms(int s)
        => s >= 3600 ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}";
}

public enum LessonStatus { Todo, Missing, Ready }

public sealed class VideoChapterVm
{
    public VideoChapterVm(VideoChapterDoc c)
    {
        AtText = c.At is { } s ? $"{s / 60}:{s % 60:00}" : "—";
        Title = c.Title;
        Note = c.Note ?? "";
    }

    public string AtText { get; }
    public string Title { get; }
    public string Note { get; }
    public bool HasNote => Note.Length > 0;
}
