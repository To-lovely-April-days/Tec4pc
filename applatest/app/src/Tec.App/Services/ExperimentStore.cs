using System.Text.Json;
using Tec.Core.Chemistry;
using Tec.Core.Compounds;
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
        OpenDb();
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

    /// <summary>
    /// 上一次装载时另外做了什么（跟指令翻译不是一回事，所以不能混在一起说）。
    /// 现在只有一件：老文件里带的配方库并进了全局库。
    /// </summary>
    public IReadOnlyList<string> LastNotes { get; private set; } = Array.Empty<string>();

    public async Task OpenAsync(string path)
    {
        var doc = TecFiles.LoadExperiment(path);

        doc.Bench.ApplyTo(_ws.Bench);

        var migrated = new List<string>();
        var anyStamped = false;

        _ws.ChannelRecipes.Clear();
        _ws.LaneNames.Clear();
        _ws.ChannelCharges.Clear();
        foreach (var lane in doc.Lanes)
        {
            _ws.ChannelRecipes[lane.Channel] = lane.Recipe.ToModel(out var notes);
            _ws.LaneNames[lane.Channel] = lane.Name;
            // 老文件里没有配料表这一项，读回来是 null，就是「这一路还没配料」
            if (lane.Charge is { } charge)
            {
                var t = charge.ToModel();
                _ws.ChannelCharges[lane.Channel] = t;
                // 快照机制之前存的行没盖章，物性一直是打开时从库里现取的——
                // 现在把现取的值落到行上、盖今天的章，从此不再跟着库漂。
                // 这是行为变化，必须说一声，不能悄悄地把人的文件改了
                var stamped = ChargeSnapshot.Migrate(t, _ws.Compounds.ToList(),
                                                     CompoundVersion, DateTimeOffset.Now);
                if (stamped.Count > 0)
                {
                    migrated.Add($"{lane.Name}·配料表 {stamped.Count} 行（{string.Join("、", stamped)}）"
                                 + "的物性已按当前化合物库做了快照，之后改库不再影响这几行");
                    anyStamped = true;
                }
            }
            foreach (var n in notes) migrated.Add($"{lane.Name}·{n}");
        }

        // 配方库不再跟着实验文件走（它是全局的，见「全局库」那一节）。
        // 老文件里带的那一份**不能直接扔**——那可能是操作人在别的机器上编的工艺。
        // 库里没有的并进来，并了几条说一声；库里已经有的（按配方号认）不动
        var loadNotes = new List<string>();
        MergeLegacyLibrary(doc, migrated, loadNotes);

        LastMigration = migrated;
        LastNotes = loadNotes;

        _ws.ExperimentName = doc.Name;
        CurrentPath = path;

        // 台面变了通道就变了。RebuildChannels 会给新出现的通道补空配方，
        // 但绝不会覆盖刚读进来的那几条——它只在缺的时候补
        await _ws.RebuildChannelsAsync();
        Dirty = false;                 // 同上：重建之后再抹

        // 补了章的文件是**真的改了**：不标脏的话章只活在内存里，下次打开
        // 又重盖一个新时刻——「快照」的时刻自己在漂，那就不叫快照了
        if (anyStamped) MarkDirty();

        Remember(path, doc.Name);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 老实验文件里带着的那份配方库，并进全局库。
    /// 按配方号认人：库里已经有的不动（本机那份才是新的），没有的补进去。
    /// 早期版本自动灌的六条演示配方顺手清掉，别让它们借着老文件回来。
    /// </summary>
    private void MergeLegacyLibrary(ExperimentDoc doc, List<string> migrated, List<string> notes)
    {
        if (doc.Library.Count == 0) return;

        var have = _ws.Library.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var added = new List<Recipe>();
        foreach (var d in doc.Library)
        {
            if (!have.Add(d.Id)) continue;
            var r = d.ToModel(out var ns);
            added.Add(r);
            foreach (var n in ns) migrated.Add($"配方库·{n}");
        }
        SeedPurge.Apply(added);
        if (added.Count == 0) return;

        foreach (var r in added) _ws.Library.Add(r);
        SaveLibrary();
        notes.Add($"这份实验是老格式，里头带的 {added.Count} 条配方已并入配方库——配方库现在是全局的，不再跟着实验文件走");
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

    /// <summary>
    /// 把 Workspace 现在的样子拍成一份实验文档。
    /// **配方库与化合物库不在里头**：它们是全局的，存在 tecstudio.db。
    /// 从前每份实验都带一份配方库副本，同一条配方在几份实验里各存一遍，
    /// 改了一处别处还是老的——分不清哪份才算数。
    /// </summary>
    public ExperimentDoc Snapshot(string name)
    {
        var doc = new ExperimentDoc
        {
            Name = name,
            Author = _ws.Operator,
            Bench = _ws.Bench.ToDoc()
        };
        foreach (var ch in _ws.ChannelRecipes.Keys.OrderBy(x => x))
            doc.Lanes.Add(new LaneDoc
            {
                Channel = ch,
                Name = _ws.LaneNames.TryGetValue(ch, out var n) ? n : "新配方",
                Recipe = _ws.ChannelRecipes[ch].ToDoc(),
                // 空表不写进文件：一份没配过料的实验不该带四张空表
                Charge = _ws.ChannelCharges.TryGetValue(ch, out var t) && !t.IsEmpty ? t.ToDoc() : null
            });
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

    // ── 全局库（配方 + 化合物）────────────────────────────────────────

    /// <summary>
    /// 全局库文件。配方库和化合物库都在这里头——它们跟**这台机器**走，
    /// 不跟某一份实验走。开不起来就是 null，程序照常能用，只是这一开机存不住库。
    /// </summary>
    public LibraryDb? Db { get; private set; }

    /// <summary>全局库存在哪儿。界面上要能告诉操作人"东西存到哪去了"。</summary>
    public static string LibraryDbPath { get; } = Path.Combine(DataDir, "tecstudio.db");

    private static string LegacyLibraryPath => Path.Combine(DataDir, "library.json");

    private void OpenDb()
    {
        try
        {
            Db = new LibraryDb(LibraryDbPath);
            MigrateLegacyLibrary();
        }
        catch (Exception ex)
        {
            // 盘满、目录只读、文件被另一个进程独占——都不该让程序开不起来。
            // 库这一开机存不住，但台面和实验文件照旧能存
            Console.WriteLine($"[warn] 全局库打不开，本次运行配方库与化合物库不会落盘：{ex.Message}");
            Db = null;
        }
    }

    /// <summary>
    /// 老版本的配方库是 library.json。搬进数据库之后把它改名留着——
    /// 不删，万一搬砸了操作人还能自己找回来；改了名下次开机就不会再搬一遍。
    /// </summary>
    private void MigrateLegacyLibrary()
    {
        if (Db is null || !File.Exists(LegacyLibraryPath)) return;
        try
        {
            var docs = TecJson.Read<List<RecipeDoc>>(File.ReadAllText(LegacyLibraryPath));
            var have = Db.LoadRecipes();
            var ids = have.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
            var added = docs.Where(d => ids.Add(d.Id)).ToList();
            if (added.Count > 0)
            {
                have.AddRange(added);
                Db.SaveRecipes(have);
            }
            File.Move(LegacyLibraryPath, LegacyLibraryPath + ".bak", overwrite: true);
            Console.WriteLine($"[info] 老配方库已搬进 {LibraryDbPath}（{added.Count} 条）");
        }
        catch (Exception ex) { Console.WriteLine($"[warn] 老配方库搬迁失败，原文件留着：{ex.Message}"); }
    }

    /// <summary>把内存里的配方库整个对齐到数据库：改的改、删的删。</summary>
    public void SaveLibrary()
    {
        if (Db is null) return;
        try { Db.SaveRecipes(_ws.Library.Select(r => r.ToDoc()).ToList()); }
        catch (Exception ex) { Console.WriteLine($"[warn] 配方库存盘失败：{ex.Message}"); }
    }

    /// <summary>返回 true 表示库里确实有东西。</summary>
    public bool LoadLibrary(IList<Recipe> into)
    {
        if (Db is null) return false;
        try
        {
            var docs = Db.LoadRecipes();
            into.Clear();
            foreach (var d in docs) into.Add(d.ToModel());
            return into.Count > 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[warn] 配方库读盘失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 化合物库读盘。第一次开机库是空的，把程序自带的参考数据灌一次；
    /// 灌过就记一笔，操作人删掉的那条不会下次开机又回来。
    /// </summary>
    public void LoadCompounds(IList<Compound> into)
    {
        into.Clear();
        if (Db is null)
        {
            // 库开不起来时至少让界面有东西可看，只是改了存不住
            foreach (var c in CompoundSeed.All) into.Add(c.Clone());
            return;
        }
        try
        {
            if (Db.CompoundCount == 0 && Db.Meta(CompoundSeed.MetaKey) is null)
            {
                Db.SaveCompounds(CompoundSeed.All);
                Db.SetMeta(CompoundSeed.MetaKey, "1");
            }
            foreach (var c in Db.LoadCompounds()) into.Add(c);
        }
        catch (Exception ex) { Console.WriteLine($"[warn] 化合物库读盘失败：{ex.Message}"); }
    }

    /// <summary>化合物库版本号（每写一次 +1）。库开不起来时恒为 0——那时候本来也存不住。</summary>
    public int CompoundVersion => Db?.CompoundVersion ?? 0;

    /// <summary>
    /// 改一条化合物就写一条，不必把整张表重写。
    /// 返回相对库里旧值的变化清单（进系统日志用）；库开不起来就是空清单——
    /// 没落盘的改动不该在日志里说成「库改了」。
    /// </summary>
    public List<string> SaveCompound(Compound c)
    {
        if (Db is null) return new List<string>();
        try { return Db.SaveCompound(c); }
        catch (Exception ex)
        {
            Console.WriteLine($"[warn] 化合物存盘失败：{ex.Message}");
            return new List<string>();
        }
    }

    /// <summary>删一条。返回被删条目的名字；没删着（库里没有 / 库开不起来）就是 null，不假装删了。</summary>
    public string? DeleteCompound(string cas)
    {
        if (Db is null) return null;
        try { return Db.DeleteCompound(cas); }
        catch (Exception ex) { Console.WriteLine($"[warn] 化合物删除失败：{ex.Message}"); return null; }
    }

    public void CloseDb()
    {
        Db?.Dispose();
        Db = null;
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
