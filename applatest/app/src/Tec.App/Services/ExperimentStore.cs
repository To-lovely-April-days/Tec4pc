using System.Text.Json;
using Tec.Core.Persistence;
using Tec.Core.Recipes;

namespace Tec.App.Services;

/// <summary>
/// 缩略图里的一台设备：画什么图、摆在哪儿、接在谁身上。
/// 存这几个字段，卡片就能画出这份实验的台面真样子，不必为了画一张图去读整份文件。
/// </summary>
public sealed class ThumbPart
{
    public string Art { get; set; } = "reactor2";
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    /// <summary>停靠在哪台设备上（宿主 InstanceId）。空 = 自由摆放。</summary>
    public string? Host { get; set; }
    public string? Anchor { get; set; }
    public string? Side { get; set; }
    public string Id { get; set; } = "";
}

/// <summary>最近打开过的一份实验。图钉是操作人自己按的，跟着列表一起存。</summary>
public sealed class RecentEntry
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTimeOffset OpenedAt { get; set; }
    public bool Pinned { get; set; }
    /// <summary>存盘那一刻的规模，列表上直接显示，不必为了画卡片去读整份文件。</summary>
    public int Devices { get; set; }
    public int Channels { get; set; }
    public int Steps { get; set; }
    /// <summary>台面缩略图的画法。空 = 台面是空的，卡片显示空台面的样子。</summary>
    public List<ThumbPart> Thumb { get; set; } = new();
}

/// <summary>
/// 实验的打开 / 新建 / 保存。文件格式在 Tec.Core.Persistence，这里只管
/// 「当前打开的是哪一份、脏没脏、最近列表怎么维护」，以及把文档灌进 Workspace。
/// </summary>
public sealed class ExperimentStore
{
    private const int MaxRecent = 12;

    private readonly Workspace _ws;
    private readonly string _recentPath;

    public ExperimentStore(Workspace ws)
    {
        _ws = ws;
        _recentPath = Path.Combine(DataDir, "recent.json");
        LoadRecent();
        // 台面动过就是改过。打开 / 新建自己会把脏标记抹掉，抹在重建通道之后
        ws.BenchChanged += (_, _) => MarkDirty();
    }

    /// <summary>数据目录。默认放在用户目录下，不往程序目录里写——装在 Program Files 时那儿是只读的。</summary>
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData,
                                  Environment.SpecialFolderOption.Create),
        "TecStudio");

    /// <summary>实验文件的默认存放位置。</summary>
    public static string ExperimentsDir { get; } = Path.Combine(DataDir, "Experiments");

    /// <summary>当前打开的文件路径。还没存过盘就是 null。</summary>
    public string? CurrentPath { get; private set; }

    /// <summary>改过还没存。关窗和新建之前要拿它问一句。</summary>
    public bool Dirty { get; private set; }

    public List<RecentEntry> Recent { get; } = new();

    public event EventHandler? Changed;

    /// <summary>标题栏上那行字：实验名（没存过盘的加个星号）。</summary>
    public string Title => _ws.ExperimentName + (Dirty ? " *" : "");

    public void MarkDirty()
    {
        if (Dirty) return;
        Dirty = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>刚启动的空台面不算「改过」。Boot() 建完通道后调一次。</summary>
    public void ResetDirty()
    {
        Dirty = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ── 新建 / 打开 / 保存 ───────────────────────────────────────────

    /// <summary>新建：台面清空、配方清空、回到「未命名实验」。</summary>
    public async Task NewAsync()
    {
        _ws.Bench.Devices.Clear();
        _ws.Bench.Bindings.Clear();
        _ws.Bench.Stations.Clear();
        _ws.ChannelRecipes.Clear();
        _ws.LaneNames.Clear();
        _ws.ExperimentName = "未命名实验";
        CurrentPath = null;
        await _ws.RebuildChannelsAsync();
        Dirty = false;                 // 重建通道会触发 BenchChanged，脏标记要在它之后抹
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 上一次装载时翻译了哪些老指令。指令库精简过，老配方里的旧 Id 会被就地翻译——
    /// 翻了什么必须让操作人看见，悄悄改掉他存下来的东西不行。
    /// </summary>
    public IReadOnlyList<string> LastMigration { get; private set; } = Array.Empty<string>();

    public async Task OpenAsync(string path)
    {
        var doc = TecFiles.LoadExperiment(path);

        doc.Bench.ApplyTo(_ws.Bench);

        var migrated = new List<string>();

        _ws.ChannelRecipes.Clear();
        _ws.LaneNames.Clear();
        foreach (var lane in doc.Lanes)
        {
            _ws.ChannelRecipes[lane.Channel] = lane.Recipe.ToModel(out var notes);
            _ws.LaneNames[lane.Channel] = lane.Name;
            foreach (var n in notes) migrated.Add($"{lane.Name}·{n}");
        }

        _ws.Library.Clear();
        foreach (var r in doc.Library)
        {
            _ws.Library.Add(r.ToModel(out var notes));
            foreach (var n in notes) migrated.Add($"配方库·{n}");
        }
        // 老实验文件里可能还带着早期版本自动灌的六条演示配方，一并清掉。
        // 不在这里标脏：刚打开的实验不该显示成"有未保存的改动"；
        // 每次打开都会清一遍，它们再也不会出现在界面上
        SeedPurge.Apply(_ws.Library);

        LastMigration = migrated;

        _ws.ExperimentName = doc.Name;
        CurrentPath = path;

        // 台面变了通道就变了。RebuildChannels 会给新出现的通道补空配方，
        // 但绝不会覆盖刚读进来的那几条——它只在缺的时候补
        await _ws.RebuildChannelsAsync();
        Dirty = false;                 // 同上：重建之后再抹

        Remember(path, doc.Name);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>保存到当前路径。没有当前路径时调用方该走「另存为」。</summary>
    public void Save()
    {
        if (CurrentPath is null) throw new TecFileException("这份实验还没有存过，请用「另存为」。");
        SaveAs(CurrentPath);
    }

    public void SaveAs(string path)
    {
        if (!path.EndsWith(TecFiles.ExperimentExt, StringComparison.OrdinalIgnoreCase))
            path += TecFiles.ExperimentExt;

        var name = Path.GetFileNameWithoutExtension(path);
        var doc = Snapshot(name);
        TecFiles.Save(path, doc);

        _ws.ExperimentName = name;
        CurrentPath = path;
        Dirty = false;
        Remember(path, name);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>把 Workspace 现在的样子拍成一份实验文档。</summary>
    public ExperimentDoc Snapshot(string name)
    {
        var doc = new ExperimentDoc
        {
            Name = name,
            Author = "管理员",
            Bench = _ws.Bench.ToDoc()
        };
        foreach (var ch in _ws.ChannelRecipes.Keys.OrderBy(x => x))
            doc.Lanes.Add(new LaneDoc
            {
                Channel = ch,
                Name = _ws.LaneNames.TryGetValue(ch, out var n) ? n : "新配方",
                Recipe = _ws.ChannelRecipes[ch].ToDoc()
            });
        foreach (var r in _ws.Library) doc.Library.Add(r.ToDoc());
        return doc;
    }

    // ── 台面单独导入 / 导出 ─────────────────────────────────────────

    public void ExportBench(string path)
    {
        if (!path.EndsWith(TecFiles.BenchExt, StringComparison.OrdinalIgnoreCase))
            path += TecFiles.BenchExt;
        TecFiles.SaveBench(path, _ws.Bench.ToDoc());
    }

    /// <summary>
    /// 导入台面：只换设备，配方留着。通道号对不上的泳道会空转——
    /// 这是操作人自己要的「换一套硬件跑同一套配方」，不该替他删配方。
    /// </summary>
    public async Task ImportBenchAsync(string path)
    {
        TecFiles.LoadBench(path).ApplyTo(_ws.Bench);
        await _ws.RebuildChannelsAsync();
        MarkDirty();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ── 配方库持久化 ────────────────────────────────────────────────

    private string LibraryPath => Path.Combine(DataDir, "library.json");

    /// <summary>
    /// 配方库存在数据目录里，跟着这台机器走。实验文件里也带一份，
    /// 是为了把实验拷给别人时模板不丢。
    /// </summary>
    public void SaveLibrary()
    {
        try
        {
            var docs = _ws.Library.Select(r => r.ToDoc()).ToList();
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(LibraryPath, TecJson.Write(docs));
        }
        catch (Exception ex) { Console.WriteLine($"[warn] 配方库存盘失败：{ex.Message}"); }
    }

    /// <summary>返回 true 表示磁盘上确实有一份库，调用方就别再灌种子模板了。</summary>
    public bool LoadLibrary(IList<Recipe> into)
    {
        if (!File.Exists(LibraryPath)) return false;
        try
        {
            var docs = TecJson.Read<List<RecipeDoc>>(File.ReadAllText(LibraryPath));
            into.Clear();
            foreach (var d in docs) into.Add(d.ToModel());
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[warn] 配方库读盘失败，退回内置模板：{ex.Message}");
            return false;
        }
    }

    // ── 最近实验 ────────────────────────────────────────────────────

    private void Remember(string path, string name)
    {
        var full = Path.GetFullPath(path);
        var old = Recent.FirstOrDefault(r => string.Equals(r.Path, full, StringComparison.OrdinalIgnoreCase));
        if (old is not null) Recent.Remove(old);

        Recent.Insert(0, new RecentEntry
        {
            Path = full,
            Name = name,
            OpenedAt = DateTimeOffset.Now,
            Pinned = old?.Pinned ?? false,
            Devices = _ws.Bench.Devices.Count,
            Channels = _ws.Channels.Count,
            Thumb = ThumbOf(),
            Steps = _ws.ChannelRecipes.Values.Sum(r => r.Steps.Count)
        });

        // 超出上限时先淘汰没按图钉的
        while (Recent.Count > MaxRecent)
        {
            var drop = Recent.LastOrDefault(r => !r.Pinned) ?? Recent[^1];
            Recent.Remove(drop);
        }
        SaveRecent();
    }

    /// <summary>把当前台面压成缩略图的画法。宽度按设备类型取，和画布上一致。</summary>
    private List<ThumbPart> ThumbOf()
        => _ws.Bench.Devices.Select(d =>
        {
            var art = _ws.Drivers.Driver(d.DriverId)?.Info.IconKey ?? "reactor2";
            return new ThumbPart
            {
                Id = d.InstanceId,
                Art = art,
                X = d.Position.X,
                Y = d.Position.Y,
                W = BenchDock.DisplayWidth(art),
                Host = d.DockHostId,
                Anchor = d.DockAnchor,
                Side = d.DockSideTag
            };
        }).ToList();

    public void Forget(string path)
    {
        Recent.RemoveAll(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
        SaveRecent();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SaveRecent()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(_recentPath, TecJson.Write(Recent));
        }
        catch (Exception ex) { Console.WriteLine($"[warn] 最近实验列表存盘失败：{ex.Message}"); }
    }

    private void LoadRecent()
    {
        if (!File.Exists(_recentPath)) return;
        try
        {
            var list = JsonSerializer.Deserialize<List<RecentEntry>>(
                File.ReadAllText(_recentPath), TecJson.Options);
            if (list is null) return;
            // 文件被删被移走的不再显示——点了打不开的卡片比没有卡片更糟
            foreach (var r in list.Where(r => File.Exists(r.Path))) Recent.Add(r);
        }
        catch (Exception ex) { Console.WriteLine($"[warn] 最近实验列表读盘失败：{ex.Message}"); }
    }
}
