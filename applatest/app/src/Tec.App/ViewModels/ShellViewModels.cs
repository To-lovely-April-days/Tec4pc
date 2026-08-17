using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Tec.App.Controls;
using Tec.App.Services;
using Tec.Core;
using Tec.Core.Catalog;
using Tec.Core.Compounds;
using Tec.Core.Recipes;
using Tec.Core.Persistence;
using Tec.Core.Scheduling;
using Tec.Drivers.Simulator;

namespace Tec.App.ViewModels;

// ── 开始视图 ─────────────────────────────────────────────────────────

/// <summary>最近实验卡片。字段与原型 RECENTS 一一对应（n/p/th/tag/tagc/when/size/pinned/on/note）。</summary>
public sealed class RecentCardViewModel : ViewModelBase
{
    private bool _on;
    private bool _pinned;

    public required string Name { get; init; }
    public required string Path { get; init; }
    /// <summary>台面缩略图的画法。空 = 台面上没有设备。</summary>
    public required IReadOnlyList<ThumbPart> Parts { get; init; }
    public required string Tag { get; init; }
    /// <summary>标签样式：live / draft / 空串。</summary>
    public required string TagClass { get; init; }
    public required string When { get; init; }
    public required string Size { get; init; }
    public required string Note { get; init; }

    public bool IsLive => TagClass == "live";
    public bool IsDraft => TagClass == "draft";
    public bool IsPlain => TagClass.Length == 0;

    public bool Pinned
    {
        get => _pinned;
        set => Set(ref _pinned, value);
    }

    public bool On
    {
        get => _on;
        set => Set(ref _on, value);
    }
}

public sealed class StartViewModel : ViewModelBase
{
    private readonly MainViewModel _shell;
    private readonly ExperimentStore _store;
    private string _status = "";

    public StartViewModel(Workspace ws, MainViewModel shell)
    {
        _shell = shell;
        Workspace = ws;
        _store = ws.Store;

        Recent.CollectionChanged += (_, _) => Raise(nameof(IsEmpty));
        ws.BenchChanged += (_, _) => Raise(nameof(Subtitle));
        _store.Changed += (_, _) => Reload();

        Pick = new RelayCommand(p =>
        {
            if (p is not RecentCardViewModel card) return;
            foreach (var c in Recent) c.On = false;
            card.On = true;
        });

        TogglePin = new RelayCommand(p =>
        {
            if (p is not RecentCardViewModel card) return;
            card.Pinned = !card.Pinned;
            var e = _store.Recent.FirstOrDefault(r => r.Path == card.Path);
            if (e is not null) { e.Pinned = card.Pinned; _store.SaveRecent(); }
        });

        // 点最近实验卡片就打开它，并且直接跳到台面——打开一份实验就是要接着干活，
        // 停在开始页还得再点一下菜单。打不开时留在原地，好让错误提示看得见。
        OpenRecent = new RelayCommand(p => Async(async () =>
        {
            if (p is not RecentCardViewModel card) return;
            if (await GuardedAsync(() => _store.OpenAsync(card.Path), $"已打开 {card.Name}"))
            {
                Status += MigrationNote();
                shell.Tab = MainViewModel.TabBench;
            }
        }));

        NewExperiment = new RelayCommand(() => Async(async () =>
        {
            await GuardedAsync(_store.NewAsync, "已新建实验。去「台面」把设备拖进来。");
            shell.Tab = MainViewModel.TabBench;
        }));

        OpenExperiment = new RelayCommand(() => Async(async () =>
        {
            if (await FileDialogs.OpenExperiment() is not { } path) return;
            if (await GuardedAsync(() => _store.OpenAsync(path),
                                   $"已打开 {Path.GetFileNameWithoutExtension(path)}"))
            {
                Status += MigrationNote();
                shell.Tab = MainViewModel.TabBench;
            }
        }));

        SaveExperiment = new RelayCommand(() => Async(async () =>
        {
            // 还没存过盘的直接走另存为，不然「保存」按下去毫无反应
            if (_store.CurrentPath is null) { await SaveAsFlow(); return; }
            Guarded(_store.Save, $"已保存到 {_store.CurrentPath}");
        }));

        SaveAsExperiment = new RelayCommand(() => Async(SaveAsFlow));

        ImportBench = new RelayCommand(() => Async(async () =>
        {
            if (await FileDialogs.OpenBench() is not { } path) return;
            await GuardedAsync(() => _store.ImportBenchAsync(path), "台面已导入。配方按通道号对回去了。");
            shell.Tab = MainViewModel.TabBench;
        }));

        ExportBench = new RelayCommand(() => Async(async () =>
        {
            if (await FileDialogs.SaveBench(ws.ExperimentName + "_台面") is not { } path) return;
            Guarded(() => _store.ExportBench(path), $"台面已导出到 {path}");
        }));

        // 走关窗那条路，不直接 Shutdown——否则绕开了「未保存的改动」这一问
        Quit = new RelayCommand(() =>
            (Avalonia.Application.Current?.ApplicationLifetime
                as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
                ?.MainWindow?.Close());

        OpenBench = new RelayCommand(() => shell.Tab = MainViewModel.TabBench);
        OpenExport = new RelayCommand(() => shell.Tab = MainViewModel.TabExport);
        OpenRecipe = new RelayCommand(() => shell.Tab = MainViewModel.TabRecipe);

        Reload();
    }

    private async Task<bool> SaveAsFlow()
    {
        var suggested = Workspace.ExperimentName;
        if (await FileDialogs.SaveExperiment(suggested) is not { } path) return false;
        return Guarded(() => _store.SaveAs(path), $"已保存到 {path}");
    }

    /// <summary>
    /// 退出前保存。返回 false 表示没存成（挑位置时取消了，或者写盘失败）——
    /// 调用方看到 false 就该留在程序里，不能闷头关掉。
    /// </summary>
    public async Task<bool> SaveForExit()
    {
        if (_store.CurrentPath is null) return await SaveAsFlow();
        return Guarded(_store.Save, $"已保存到 {_store.CurrentPath}");
    }

    /// <summary>
    /// 文件操作一律兜住异常，把 TecFileException 那句人话原样显示。
    /// 抛到 Avalonia 的默认处理里就是整个窗口挂掉。
    /// </summary>
    /// <summary>
    /// 装载时翻译过老指令就在状态里说一句。翻译是就地改了操作人存下来的东西，
    /// 不出声等于偷偷改配方——那是这行不能干的事。
    /// </summary>
    private string MigrationNote()
    {
        var text = "";
        var n = _store.LastMigration;
        if (n.Count > 0)
        {
            var head = n.Count == 1 ? n[0] : $"{n[0]} 等 {n.Count} 处";
            text += $"。这份实验是旧指令库存的，已转换：{head}";
        }
        // 装载时另外做过的事（比如老文件里的配方库并进了全局库）。
        // 跟指令翻译分开说——把两件事串成一句，读起来像是配方库也被"转换"了
        foreach (var note in _store.LastNotes) text += "。" + note;
        return text;
    }

    private bool Guarded(Action act, string okText)
    {
        try { act(); Status = okText; return true; }
        catch (TecFileException ex) { Status = ex.Message; }
        catch (Exception ex) { Status = "出错了：" + ex.Message; }
        return false;
    }

    /// <summary>
    /// 打开 / 新建 / 导入台面要等驱动开完会话，全程 await——早先这里是阻塞等的，
    /// 台面上已经有设备时会把界面线程和驱动的收尾互相锁住，点「打开」就整个卡死。
    /// 返回 true 表示这一步真的成了；失败时不该继续往下跳视图。
    /// </summary>
    private async Task<bool> GuardedAsync(Func<Task> act, string okText)
    {
        try { await act(); Status = okText; return true; }
        catch (TecFileException ex) { Status = ex.Message; }
        catch (Exception ex) { Status = "出错了：" + ex.Message; }
        return false;
    }

    private static async void Async(Func<Task> work)
    {
        try { await work(); } catch (Exception ex) { Console.WriteLine("[error] " + ex.Message); }
    }

    /// <summary>把最近实验列表重画成卡片。</summary>
    public void Reload()
    {
        Recent.Clear();
        foreach (var e in _store.Recent)
            Recent.Add(new RecentCardViewModel
            {
                Name = e.Name,
                Path = e.Path,
                Parts = e.Thumb,
                Tag = string.Equals(e.Path, _store.CurrentPath, StringComparison.OrdinalIgnoreCase)
                    ? "已打开" : e.Steps == 0 ? "草稿" : "",
                TagClass = string.Equals(e.Path, _store.CurrentPath, StringComparison.OrdinalIgnoreCase)
                    ? "live" : e.Steps == 0 ? "draft" : "",
                When = Ago(e.OpenedAt),
                Size = Size(e.Path),
                Note = $"{e.Devices} 台设备 · {e.Channels} 个通道 · 共 {e.Steps} 步",
                Pinned = e.Pinned,
                On = string.Equals(e.Path, _store.CurrentPath, StringComparison.OrdinalIgnoreCase)
            });
        RaiseAll(nameof(IsEmpty), nameof(Subtitle), nameof(CurrentFile));
    }

    private static string Ago(DateTimeOffset at)
    {
        var d = DateTimeOffset.Now - at;
        if (d < TimeSpan.FromMinutes(1)) return "刚刚";
        if (at.Date == DateTimeOffset.Now.Date) return "今天 " + at.ToString("HH:mm");
        if (at.Date == DateTimeOffset.Now.Date.AddDays(-1)) return "昨天 " + at.ToString("HH:mm");
        return at.ToString("MM/dd HH:mm");
    }

    private static string Size(string path)
    {
        try
        {
            var n = new FileInfo(path).Length;
            return n >= 1048576 ? $"{n / 1048576.0:F1} MB" : n >= 1024 ? $"{n / 1024} KB" : $"{n} B";
        }
        catch { return "—"; }
    }

    public Workspace Workspace { get; }
    public ObservableCollection<RecentCardViewModel> Recent { get; } = new();

    /// <summary>一条最近实验都没有时，卡片区换成一句说明——空白一片容易让人以为是没加载出来。</summary>
    public bool IsEmpty => Recent.Count == 0;

    /// <summary>当前打开的文件路径，没存过盘就说没存过。</summary>
    public string CurrentFile => _store.CurrentPath ?? "（还没保存过）";

    /// <summary>最近一次文件操作的结果。成了失败了都写在这儿，不弹框。</summary>
    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    public RelayCommand Pick { get; }
    public RelayCommand TogglePin { get; }
    public RelayCommand OpenRecent { get; }
    public RelayCommand NewExperiment { get; }
    public RelayCommand OpenExperiment { get; }
    public RelayCommand SaveExperiment { get; }
    public RelayCommand SaveAsExperiment { get; }
    public RelayCommand ImportBench { get; }
    public RelayCommand ExportBench { get; }
    public RelayCommand Quit { get; }
    public RelayCommand OpenBench { get; }
    public RelayCommand OpenExport { get; }
    public RelayCommand OpenRecipe { get; }

    /// <summary>副标题照台面实况写。台面空着就说空着，不写死「2 台反应器 · 4 通道」。</summary>
    public string Subtitle
    {
        get
        {
            var hosts = Workspace.Bench.Devices.Count(d => d.DriverId == Rd105ReactorDriver.DriverId);
            var chs = Workspace.Channels.Count;
            return hosts == 0
                ? "平行合成工作站 1.0 · 台面还是空的，去「台面」把设备拖进来"
                : $"平行合成工作站 1.0 · {Workspace.Bench.Name} · {hosts} 台双通道反应器 · {chs} 通道";
        }
    }
}

// ── 配方库视图 ───────────────────────────────────────────────────────

public sealed class LibRowViewModel : ViewModelBase
{
    private bool _sel;
    public required Recipe Recipe { get; init; }
    public required IReadOnlyList<string> Mix { get; init; }
    public bool IsSelected
    {
        get => _sel;
        set => Set(ref _sel, value);
    }
    public string Name => Recipe.Name;
    /// <summary>属性面板改名后由列表行自己刷新（名字两处显示，必须同步）。</summary>
    public void NameChanged() => RaiseAll(nameof(Name), nameof(Meta));
    /// <summary>原型 rm2：「N 步 · 更新 MM/dd」。</summary>
    public string Meta => $"{Recipe.Steps.Count} 步 · 更新 {Recipe.ModifiedAt:MM/dd}";
}

/// <summary>
/// 配方库（原型 renderLibView 的 1:1）：左 312px 列表（圆图标 + 名称 + 元信息 + 模块色带），
/// 中间只读流程预览（与配方页同一种步骤卡），右侧配方属性 + 应用到通道。
/// </summary>
public sealed class RecipeLibViewModel : ViewModelBase
{
    /// <summary>右栏「应用到通道」小节的开合。</summary>
    public SectionViewModel ApplySection { get; } = new();

    private readonly Workspace _ws;
    private readonly MainViewModel _shell;
    private LibRowViewModel? _selected;
    private int _applyCh;

    public RecipeLibViewModel(Workspace ws, MainViewModel shell)
    {
        _ws = ws;
        _shell = shell;
        Reload();
        Selected = Rows.FirstOrDefault();

        Duplicate = new RelayCommand(() =>
        {
            if (_selected is null) return;
            var copy = _selected.Recipe.CopyAs(_selected.Recipe.Name + "_副本", ws.Operator);
            ws.Library.Insert(ws.Library.IndexOf(_selected.Recipe) + 1, copy);
            ws.Store.SaveLibrary();
            Selected = Rows.FirstOrDefault(r => ReferenceEquals(r.Recipe, copy));
        });

        Delete = new RelayCommand(() =>
        {
            if (_selected is null) return;
            var i = ws.Library.IndexOf(_selected.Recipe);
            ws.Library.Remove(_selected.Recipe);
            ws.Store.SaveLibrary();
            Selected = Rows.ElementAtOrDefault(Math.Min(i, Rows.Count - 1)) ?? Rows.FirstOrDefault();
        });

        // 与配方页的「存入配方库」是同一个动作：把当前通道的配方收进库
        NewFromChannel = new RelayCommand(() =>
        {
            var from = shell.Recipe.CurCh;
            if (!ws.ChannelRecipes.TryGetValue(from, out var live)) return;
            var name = ws.LaneNames.TryGetValue(from, out var n) && n.Length > 0 ? n : live.Name;
            var copy = live.CopyAs(name, ws.Operator);
            ws.Library.Add(copy);
            ws.Store.SaveLibrary();
            Selected = Rows.FirstOrDefault(r => ReferenceEquals(r.Recipe, copy));
        });

        ImportToLib = new RelayCommand(() => _ = ImportAsync());
        SaveLib = new RelayCommand(() => ws.Store.SaveLibrary());

        ApplyToChannel = new RelayCommand(() => Apply(false));
        ApplyToAll = new RelayCommand(() => Apply(true));

        // 打开别的实验会整份换掉配方库
        ws.Store.Changed += (_, _) => Resync();
        // 配方页存进来一条、或者这一页自己增删——列表当场跟上。
        // 以前是裸 List 没有通知，配方页存了之后切过来看不见，得重开程序
        ws.Library.CollectionChanged += (_, _) => Resync();
    }

    public ObservableCollection<LibRowViewModel> Rows { get; } = new();
    public ObservableCollection<StepViewModel> Flow { get; } = new();
    public ObservableCollection<string> ApplyTargets { get; } = new();

    public RelayCommand Duplicate { get; }
    public RelayCommand Delete { get; }
    public RelayCommand ApplyToChannel { get; }
    public RelayCommand ApplyToAll { get; }
    public RelayCommand NewFromChannel { get; }
    public RelayCommand ImportToLib { get; }
    public RelayCommand SaveLib { get; }

    public LibRowViewModel? Selected
    {
        get => _selected;
        set
        {
            // 允许置空：库可能一条都没有，硬留一个"选中项"就得凭空造一条
            var old = _selected;
            if (!Set(ref _selected, value)) return;
            if (old is not null) old.IsSelected = false;
            if (value is not null) value.IsSelected = true;
            Refresh();
        }
    }

    /// <summary>库里一条都没有。左列表、中预览、右属性都换成空状态。</summary>
    public bool IsEmpty => Rows.Count == 0;
    public bool HasAny => Rows.Count > 0;
    public bool HasSelection => _selected is not null;

    public Recipe? Current => _selected?.Recipe;

    public string Name
    {
        get => Current?.Name ?? "—";
        set
        {
            if (Current is null || value == Current.Name) return;
            Current.Name = value;
            _selected!.NameChanged();
            Raise();
        }
    }

    public string Desc
    {
        get => Current?.Notes ?? "";
        set { if (Current is not null) { Current.Notes = value; Raise(); } }
    }

    public string StepCountText => Current is null ? "" : Current.Steps.Count.ToString();
    public string UpdatedText => Current is null ? "" : Current.ModifiedAt.ToString("MM/dd");

    /// <summary>目标通道下拉：启用的通道，已在配方页有泳道的带上泳道名。</summary>
    public string ApplyTarget
    {
        get => LabelOf(_applyCh);
        set
        {
            var n = value?.Split(' ').FirstOrDefault()?.Replace("CH", "");
            if (int.TryParse(n, out var c)) { _applyCh = c; Raise(); }
        }
    }

    public bool HasTargets => ApplyTargets.Count > 0;

    private string LabelOf(int ch)
        => _ws.LaneNames.TryGetValue(ch, out var lane) && lane.Length > 0 ? $"CH{ch} · {lane}" : $"CH{ch}";

    /// <summary>库变了：重建列表并尽量守住原来选中的那条。</summary>
    private void Resync()
    {
        var keep = _selected?.Recipe.Id;
        Reload();
        Selected = Rows.FirstOrDefault(r => r.Recipe.Id == keep) ?? Rows.FirstOrDefault();
    }

    private void Reload()
    {
        Rows.Clear();
        foreach (var r in _ws.Library)
            Rows.Add(new LibRowViewModel { Recipe = r, Mix = MixOf(r) });
        if (Rows.Count == 0) Selected = null;
        RaiseAll(nameof(IsEmpty), nameof(HasAny));
    }

    /// <summary>把一份 .tecrecipe 收进库。与配方页那个导入是同一套读盘 + 老指令翻译。</summary>
    private async Task ImportAsync()
    {
        if (await FileDialogs.OpenRecipe() is not { } path) return;
        try
        {
            var read = Tec.Core.Persistence.TecFiles.LoadRecipe(path).ToModel();
            // 换个新 Id 再进库：同一个文件导两次，库里就该是两条各自独立的配方，
            // 撞了 Id 之后选中、删除都会指错人
            var recipe = read.CopyAs(read.Name, read.Author ?? _ws.Operator);
            _ws.Library.Add(recipe);
            _ws.Store.SaveLibrary();
            Selected = Rows.FirstOrDefault(r => ReferenceEquals(r.Recipe, recipe));
        }
        catch { /* 读不了就当没导入：这一页没有状态行，弹框反而打断操作 */ }
    }

    /// <summary>列表底部的模块色带：一步一段，结束实验不计（原型 rmix）。</summary>
    private IReadOnlyList<string> MixOf(Recipe r)
        => r.Steps.Where(s => s.CommandId != BuiltinCommands.Finish)
                  .Select(s => ModuleInfo.ColorOf(_ws.Catalog.TryGet(s.CommandId, out var d) ? d.Module : "通用"))
                  .ToList();

    private void Refresh()
    {
        Flow.Clear();
        var r = Current;
        if (r is not null)
        {
            var plan = Schedule.Build(r, _ws.Catalog);
            for (var i = 0; i < r.Steps.Count; i++)
            {
                if (r.Steps[i].CommandId == BuiltinCommands.Finish) continue;   // 原型预览不列结束实验
                _ws.Catalog.TryGet(r.Steps[i].CommandId, out var d);
                Flow.Add(new StepViewModel(i + 1, r.Steps[i], plan.Entries[i], d));
            }
        }

        var chs = _ws.Channels.Where(c => c.Enabled).Select(c => c.Number).OrderBy(x => x).ToList();
        ApplyTargets.Clear();
        foreach (var c in chs) ApplyTargets.Add(LabelOf(c));
        if (!chs.Contains(_applyCh)) _applyCh = chs.FirstOrDefault();

        RaiseAll(nameof(Name), nameof(Desc), nameof(StepCountText), nameof(UpdatedText),
                 nameof(ApplyTarget), nameof(HasTargets), nameof(Current),
                 nameof(IsEmpty), nameof(HasAny), nameof(HasSelection));
    }

    /// <summary>应用会替换目标通道的全部步骤，并跳到配方页（原型 libApplyTo）。</summary>
    private void Apply(bool all)
    {
        var r = Current;
        if (r is null) return;
        var targets = all
            ? _ws.Channels.Where(c => c.Enabled).Select(c => c.Number).ToList()
            : new List<int> { _applyCh };
        if (targets.Count == 0 || targets[0] == 0) return;

        foreach (var c in targets)
        {
            // 每个通道一份独立副本：同号配方在记录与导出里分不开
            _ws.ChannelRecipes[c] = r.CopyAs(r.Name, r.Author);
            _ws.LaneNames[c] = r.Name;
        }
        _ws.Store.MarkDirty();
        if (!all) _shell.Recipe.CurCh = targets[0];
        _shell.Recipe.RefreshAll();
        _shell.Tab = MainViewModel.TabRecipe;
    }
}

// ── 化合物数据库 ─────────────────────────────────────────────────────

/// <summary>
/// 表格一行 = 右栏物性详情的编辑面。数据本身在 <see cref="Compound"/> 上，
/// 这里只做通知与格式化——改一个字段就写一条进全局库，不必按「保存」。
/// </summary>
public sealed class CompoundViewModel : ViewModelBase
{
    private readonly Compound _m;
    private readonly Func<Compound, string, bool> _save;
    private bool _sel;

    /// <param name="save">存这一条。第二个参数是**改之前的主键**——改了 CAS 就得换钥匙。
    /// 返回 false = 新键跟别的条目撞了，这一改不算数。</param>
    public CompoundViewModel(Compound model, Func<Compound, string, bool> save)
    {
        _m = model;
        _save = save;
    }

    /// <summary>底下那条数据。存盘、提取到配方参数都拿它。</summary>
    public Compound Model => _m;

    /// <summary>选中态由 CompoundsViewModel 统一维护（表格行整行加深加粗）。</summary>
    public bool IsSelected { get => _sel; set => Set(ref _sel, value); }

    // 物性详情面板是这些字段的编辑面，改了表格同一行立刻跟着变，同时落进库
    public string Name
    {
        get => _m.Name;
        set => Edit(_m.Name, value, v => _m.Name = v);
    }

    /// <summary>主键。筛选、认回选中项拿它，**不往界面上摆**——见 <see cref="CasText"/>。</summary>
    public string Cas => _m.Cas;

    /// <summary>
    /// 表格里那一格。没有 CAS 的（内部代号、新建的空白条）是一条短横：
    /// 主键位上坐着的内部键不是 CAS，摆在「CAS 号」那一列底下等于替它编了一个。
    /// </summary>
    public string CasText => _m.HasCas ? _m.Cas : "—";

    /// <summary>
    /// 右栏那个编辑框。**改它等于换钥匙**：库按 cas 认人，老那一行得删掉，
    /// 否则改一次库里多一条改之前的孤儿（而且两条名字一样，看不出哪条是新的）。
    ///
    /// 清空 = 这一条没有 CAS，退回内部键，不是把主键抹成空串。
    /// </summary>
    public string CasEdit
    {
        get => _m.HasCas ? _m.Cas : "";
        set
        {
            var v = (value ?? "").Trim();
            if (string.Equals(v, CasEdit, StringComparison.Ordinal)) return;

            var old = _m.Cas;
            _m.Cas = v.Length == 0 ? Compound.KeyOf(_m.Name) : v;
            if (!_save(_m, old)) _m.Cas = old;      // 撞车了：退回去，框里也退回去
            RaiseAll(nameof(Cas), nameof(CasEdit), nameof(CasText));
        }
    }

    public string Formula
    {
        get => _m.Formula;
        set => Edit(_m.Formula, value, v => _m.Formula = v);
    }

    public string Category
    {
        get => _m.Category;
        set => Edit(_m.Category, value, v => _m.Category = v, new[] { nameof(CategoryColor) });
    }

    public string Solvent
    {
        get => _m.Solvent;
        set => Edit(_m.Solvent, value, v => _m.Solvent = v);
    }

    public string Note
    {
        get => _m.Note;
        set => Edit(_m.Note, value, v => _m.Note = v);
    }

    public double? Mw
    {
        get => _m.Mw;
        set => Edit(_m.Mw, value, v => _m.Mw = v, new[] { nameof(MwText), nameof(MwEdit) });
    }

    public double? Mp
    {
        get => _m.Mp;
        set => Edit(_m.Mp, value, v => _m.Mp = v, new[] { nameof(MpText), nameof(MpEdit) });
    }

    // ── 可空的那几项 ────────────────────────────────────────────────
    //
    // 界面上「没填」就是一个空框，不是 0。密度写成 0 的话，配料表把质量换成体积
    // 会得到「不用加」；写成无穷大会得到「加不完」。两种都是错的，所以让它空着，
    // 用的地方照实说「缺密度」。

    public double? Density
    {
        get => _m.Density;
        set => Edit(_m.Density, value, v => _m.Density = v, new[] { nameof(DensityText), nameof(DensityEdit) });
    }

    public double? Bp
    {
        get => _m.Bp;
        set => Edit(_m.Bp, value, v => _m.Bp = v, new[] { nameof(BpText), nameof(BpEdit) });
    }

    public double? Purity
    {
        get => _m.Purity;
        set => Edit(_m.Purity, value, v => _m.Purity = v, new[] { nameof(PurityText), nameof(PurityEdit) });
    }

    public double? Cp
    {
        get => _m.Cp;
        set => Edit(_m.Cp, value, v => _m.Cp = v, new[] { nameof(CpText), nameof(CpEdit) });
    }

    public string Batch
    {
        get => _m.Batch;
        set => Edit(_m.Batch, value ?? "", v => _m.Batch = v);
    }

    public string Supplier
    {
        get => _m.Supplier;
        set => Edit(_m.Supplier, value ?? "", v => _m.Supplier = v);
    }

    /// <summary>表格里的显示。没填就是一条短横，不是 0.00——0 和「不知道」是两回事。</summary>
    public string DensityText => Show(Density, "F3");
    public string BpText => Show(Bp, "F1");
    public string PurityText => Purity is null ? "—" : Purity.Value.ToString("F1", CultureInfo.InvariantCulture) + " %";
    public string CpText => Show(Cp, "F3");

    public string DensityEdit { get => Raw(Density); set => Density = Parse(value); }
    public string BpEdit { get => Raw(Bp); set => Bp = Parse(value); }
    public string PurityEdit { get => Raw(Purity); set => Purity = Parse(value); }
    public string CpEdit { get => Raw(Cp); set => Cp = Parse(value); }

    private static string Show(double? v, string fmt)
        => v is null ? "—" : v.Value.ToString(fmt, CultureInfo.InvariantCulture);

    /// <summary>
    /// 编辑框里「没填」是空串。放一条短横进去，人一按退格就变成了「—」这个名字。
    ///
    /// 填了的就把**存着的那个数**原样放进去，不用表格那份四舍五入的写法：
    /// 密度 1.1371 按 F3 显示成 1.137，人点进去再 Tab 出来，库里那位小数就没了。
    /// </summary>
    private static string Raw(double? v)
        => v?.ToString("0.##########", CultureInfo.InvariantCulture) ?? "";

    /// <summary>清空 = 把这一项设回「没填」。填了读不出来的字就当没改，不静默清零。</summary>
    private static double? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>溶解度对温度的二次拟合系数 a + b·T + c·T²（g/100 mL 水）。</summary>
    public double[] Solubility => _m.Solubility;

    /// <summary>骨架式的画法。库里只存一个键，画法是程序自带的图形资源。</summary>
    public Molecule? Structure => Structures.Get(_m.StructureKey);

    /// <summary>离子对。没有骨架式的（无机盐）排这个。</summary>
    public string? IonText => _m.IonText;

    /// <summary>摩尔质量 / 熔点按原型的小数位显示：表格与编辑框用同一份格式，免得一个 342.30 一个 342.3。</summary>
    public string MwText => Show(Mw, "F2");
    public string MpText => Show(Mp, "F1");

    public string MwEdit { get => Raw(Mw); set => Mw = Parse(value); }
    public string MpEdit { get => Raw(Mp); set => Mp = Parse(value); }

    /// <summary>原型 CATCOLOR。</summary>
    public string CategoryColor => Category switch
    {
        "有机酸" => "#ec5a24",
        "药物" => "#3f6fd8",
        "氨基酸" => "#2f8f49",
        "无机盐" => "#8a5a3b",
        _ => "#c0399f"
    };

    /// <summary>改一个字段：值真的变了才通知、才落盘。</summary>
    private void Edit<T>(T old, T value, Action<T> set, string[]? also = null,
                         [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(old, value)) return;
        set(value);
        Raise(name);
        if (also is not null) RaiseAll(also);
        _save(_m, _m.Cas);                          // 主键没动，照存
    }
}

public sealed class CompoundsViewModel : ViewModelBase
{
    /// <summary>右栏「溶解度 – 温度」小节的开合。</summary>
    public SectionViewModel SolubilitySection { get; } = new();

    private readonly Workspace _ws;
    private readonly List<CompoundViewModel> _all = new();
    private string _search = "";
    private string _category = "全部";
    private CompoundViewModel? _selected;

    public CompoundsViewModel(Workspace ws)
    {
        _ws = ws;
        foreach (var c in new[] { "全部", "有机酸", "药物", "氨基酸", "无机盐", "糖类" }) Categories.Add(c);

        AddNew = new RelayCommand(DoAddNew);
        Delete = new RelayCommand(DoDelete);
        Import = new RelayCommand(() => _ = DoImportAsync());
        Export = new RelayCommand(() => _ = DoExportAsync());
        GoCharge = new RelayCommand(() => GoChargePage?.Invoke());

        // 化合物库是全局的，从 tecstudio.db 读；改一个字段写一条回去
        Reload();
        ws.Compounds.CollectionChanged += (_, _) => Reload();
    }

    public RelayCommand AddNew { get; }
    public RelayCommand Delete { get; }
    public RelayCommand Import { get; }
    public RelayCommand Export { get; }

    /// <summary>「到配料表算饱和温度」。外壳接上跳页——浓度只有那一页才知道。</summary>
    public RelayCommand GoCharge { get; }

    /// <summary>跳到配料表页。由外壳接上。</summary>
    public Action? GoChargePage { get; set; }

    /// <summary>工具条下面那一行话。成了失败都写在这儿，不弹框打断正在跑的实验。</summary>
    public string Status { get; private set; } = "";
    public bool HasStatus => Status.Length > 0;
    public bool StatusBad { get; private set; }
    public string StatusColorHex => StatusBad ? "#c0392b" : "#2f8f49";

    private void Say(string text, bool bad = false)
    {
        Status = text;
        StatusBad = bad;
        RaiseAll(nameof(Status), nameof(HasStatus), nameof(StatusBad), nameof(StatusColorHex));
    }

    /// <summary>
    /// 存一条。<paramref name="oldCas"/> 是改之前的主键。
    ///
    /// **改了 CAS 等于换钥匙**：库按 cas 列认人，只 upsert 不删老的话，
    /// 库里会留下一条改之前的孤儿——两条名字还一模一样，看不出哪条是新的。
    /// </summary>
    private bool SaveRow(Compound c, string oldCas)
    {
        if (!string.Equals(oldCas, c.Cas, StringComparison.Ordinal))
        {
            // 新键已经归别人了。硬存进去会把那一条整个盖掉，所以这一改不算数
            if (_ws.Compounds.FirstOrDefault(x => !ReferenceEquals(x, c) && x.Cas == c.Cas) is { } dup)
            {
                Say($"CAS 号 {c.Cas} 已经是「{dup.Name}」的了，没有改", bad: true);
                return false;
            }
            _ws.Store.DeleteCompound(oldCas);
        }
        _ws.Store.SaveCompound(c);
        return true;
    }

    /// <summary>
    /// 新增一条。**空白的一条**，不预填任何物性——预填等于替人编数据。
    /// 主键先用一个内部键占住（库按 CAS 认人），人填上真的 CAS 就换过去。
    /// </summary>
    private void DoAddNew()
    {
        var c = new Compound { Cas = Compound.NewKey(), Name = "新化合物", Category = "" };
        _ws.Compounds.Add(c);
        _ws.Store.SaveCompound(c);
        Selected = Rows.FirstOrDefault(r => r.Cas == c.Cas);
        Say("已新增一条，请在右侧填写名称与 CAS 号");
    }

    private void DoDelete()
    {
        if (_selected is not { } row) { Say("先在表里选中一条", bad: true); return; }
        var name = row.Name;
        var cas = row.Cas;
        _ws.Compounds.Remove(row.Model);
        _ws.Store.DeleteCompound(cas);
        Say(row.Model.HasCas ? $"已删除 {name}（{cas}）" : $"已删除 {name}");
    }

    /// <summary>
    /// 从 CSV 导入。**按 CAS 合并**：已有的更新，没有的新增——
    /// 整库替换会把操作人自己录的那些内部代号一次性抹掉。
    /// </summary>
    private async Task DoImportAsync()
    {
        var path = await FileDialogs.OpenCsvAsync("导入化合物");
        if (path is null) return;
        try
        {
            var result = CompoundCsv.Read(await File.ReadAllTextAsync(path, Encoding.UTF8));
            if (!result.Ok)
            {
                Say("没有导入任何一条：" + string.Join(" ", result.Problems.Take(2)), bad: true);
                return;
            }

            int added = 0, updated = 0;
            foreach (var c in result.Items)
            {
                var old = _ws.Compounds.FirstOrDefault(x => x.Cas == c.Cas);
                if (old is null) { _ws.Compounds.Add(c); added++; }
                else
                {
                    // 就地改，不换对象：界面上那一行绑的是它，换掉的话选中项会跳走
                    Merge(old, c);
                    updated++;
                }
                _ws.Store.SaveCompound(old ?? c);
            }

            // 更新的那些是**就地改模型**，不经过行的属性设置器，谁都不会发通知——
            // 表格上那一行还显示着旧值。实测踩到过：导进去了，界面上密度还是「—」。
            // 整表重建一次最省事，选中项按 CAS 认回来
            Reload();

            var msg = $"已导入 {result.Items.Count} 条（新增 {added}、更新 {updated}）";
            if (result.IgnoredColumns.Count > 0)
                msg += "；忽略了认不出的列：" + string.Join("、", result.IgnoredColumns.Take(4));
            if (result.Problems.Count > 0)
                msg += $"；{result.Problems.Count} 处有问题：" + result.Problems[0];
            Say(msg, bad: result.Problems.Count > 0);
            _ws.Log?.Write("化合物", $"从 {path} 导入 {result.Items.Count} 条"
                                     + $"（新增 {added}、更新 {updated}、问题 {result.Problems.Count}）", _ws.Operator);
        }
        catch (Exception ex) { Say("导入失败：" + ex.Message, bad: true); }
    }

    /// <summary>覆盖式合并：CSV 里填了的就覆盖，没填的**留着库里原来的值**——
    /// 一份只有名称和密度的表不该把库里的分子量清空。</summary>
    private static void Merge(Compound into, Compound from)
    {
        if (from.Name.Length > 0) into.Name = from.Name;
        if (from.Formula.Length > 0) into.Formula = from.Formula;
        if (from.Category.Length > 0) into.Category = from.Category;
        if (from.Solvent.Length > 0) into.Solvent = from.Solvent;
        if (from.Batch.Length > 0) into.Batch = from.Batch;
        if (from.Supplier.Length > 0) into.Supplier = from.Supplier;
        if (from.Note.Length > 0) into.Note = from.Note;
        if (from.Mw is not null) into.Mw = from.Mw;
        if (from.Mp is not null) into.Mp = from.Mp;
        if (from.Density is not null) into.Density = from.Density;
        if (from.Bp is not null) into.Bp = from.Bp;
        if (from.Purity is not null) into.Purity = from.Purity;
        if (from.Cp is not null) into.Cp = from.Cp;
        if (from.Solubility.Length > 0) into.Solubility = from.Solubility;
    }

    private async Task DoExportAsync()
    {
        if (_ws.Compounds.Count == 0) { Say("库里一条都没有，没什么可导的", bad: true); return; }
        var path = await FileDialogs.SaveCsvAsync("导出化合物", "化合物库");
        if (path is null) return;
        try
        {
            // 带 BOM：不带的话 Excel 双击打开是乱码，而这份表就是给人拿 Excel 改的
            await File.WriteAllTextAsync(path, CompoundCsv.Write(_ws.Compounds), new UTF8Encoding(true));
            Say($"已导出 {_ws.Compounds.Count} 条到 {path}");
            _ws.Log?.Write("化合物", $"导出 {_ws.Compounds.Count} 条到 {path}", _ws.Operator);
        }
        catch (Exception ex) { Say("导出失败：" + ex.Message, bad: true); }
    }

    public ObservableCollection<CompoundViewModel> Rows { get; } = new();
    public ObservableCollection<string> Categories { get; } = new();

    public string Search
    {
        get => _search;
        set { if (Set(ref _search, value)) Apply(); }
    }

    public string Category
    {
        get => _category;
        set { if (Set(ref _category, value)) Apply(); }
    }

    public CompoundViewModel? Selected
    {
        get => _selected;
        set
        {
            // 列表用筛选结果重建，选中项不能跟着被清掉（原型 curCmp 独立于筛选）
            if (value is null) return;
            var old = _selected;
            if (!Set(ref _selected, value)) return;
            if (old is not null) old.IsSelected = false;
            value.IsSelected = true;
            RaiseAll(nameof(HasSelection), nameof(ExtractNote), nameof(Coefficients));
        }
    }

    public bool HasSelection => _selected is not null;

    /// <summary>曲线控件要的二次拟合系数。</summary>
    public double[]? Coefficients => _selected?.Solubility;

    public string ExtractNote => _selected is null ? "" : $"已将 {_selected.Name} 的物性数据提取到当前配方参数";

    /// <summary>没有匹配项时表格给一句话，而不是空白（原型同款）。</summary>
    public bool NoRows => Rows.Count == 0;

    /// <summary>库里一条都没有——跟「筛出来是空的」不是一回事，说法也不一样。</summary>
    public bool IsEmpty => _all.Count == 0;

    private void Reload()
    {
        var keep = _selected?.Cas;
        _all.Clear();
        foreach (var c in _ws.Compounds) _all.Add(new CompoundViewModel(c, SaveRow));
        Apply();
        _selected = null;
        Selected = Rows.FirstOrDefault(r => r.Cas == keep) ?? Rows.FirstOrDefault();
        Raise(nameof(IsEmpty));
    }

    private void Apply()
    {
        Rows.Clear();
        foreach (var c in _all)
        {
            if (_category != "全部" && c.Category != _category) continue;
            if (_search.Length > 0 &&
                !c.Name.Contains(_search, StringComparison.OrdinalIgnoreCase) &&
                // 按 CasText 找：内部键是我们自己的东西，人不会拿它来搜
                !c.CasText.Contains(_search, StringComparison.OrdinalIgnoreCase) &&
                !c.Formula.Contains(_search, StringComparison.OrdinalIgnoreCase)) continue;
            Rows.Add(c);
        }
        Raise(nameof(NoRows));
    }
}
