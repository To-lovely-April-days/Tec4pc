using System.Collections.ObjectModel;
using System.Globalization;
using Tec.App.Services;
using Tec.Core;
using Tec.Core.Catalog;
using Tec.Core.Recipes;
using Tec.Core.Scheduling;

namespace Tec.App.ViewModels;

// ── 开始视图 ─────────────────────────────────────────────────────────

/// <summary>最近实验卡片。字段与原型 RECENTS 一一对应（n/p/th/tag/tagc/when/size/pinned/on/note）。</summary>
public sealed class RecentCardViewModel : ViewModelBase
{
    private bool _on;
    private bool _pinned;

    public required string Name { get; init; }
    public required string Path { get; init; }
    /// <summary>缩略图键：bench4 / bench2 / curve / empty。</summary>
    public required string Thumb { get; init; }
    public required string Tag { get; init; }
    /// <summary>标签样式：live / draft / 空串。</summary>
    public required string TagClass { get; init; }
    public required string When { get; init; }
    public required string Size { get; init; }
    public required string Note { get; init; }

    public string ThumbKey => "thumb-" + Thumb;
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

    public StartViewModel(Workspace ws, MainViewModel shell)
    {
        _shell = shell;
        Workspace = ws;

        // 原型 RECENTS 五条，逐字段照搬
        Add("降温结晶_梯度筛选_0814", @"D:\TecStudio\Experiments\降温结晶_梯度筛选_0814.tec",
            "bench4", "正在运行", "live", "今天 09:12", "2.4 MB", true, true,
            "4 通道并行：CH1/CH3 梯度降温对比，CH2 pH 反馈加料，CH4 停用。");
        Add("硝化_控温加料_0812", @"D:\TecStudio\Experiments\硝化_控温加料_0812.tec",
            "curve", "已完成", "", "08/12 17:40", "1.1 MB", false, false,
            "恒速滴加 + Tr−Tj 放热监控，拉曼跟踪晶型转变。");
        Add("溶解度曲线_自动测定", @"D:\TecStudio\Experiments\溶解度曲线_自动测定.tec",
            "bench2", "双通道", "", "08/10 14:03", "860 KB", false, false,
            "单台反应器拆分使用：CH1 升温溶解，浊度判定溶清点，自动记录。");
        Add("介稳区_自动测定", @"D:\TecStudio\Experiments\介稳区_自动测定.tec",
            "curve", "模板", "", "08/06 08:55", "3.7 MB", true, false,
            "联用在线颗粒分析，无人值守自动测定结晶介稳区。");
        Add("新建实验", "（未保存）",
            "empty", "草稿", "draft", "08/05 11:20", "—", false, false,
            "台面还没有放置任何设备。");

        Pick = new RelayCommand(p =>
        {
            if (p is not RecentCardViewModel card) return;
            foreach (var c in Recent) c.On = false;
            card.On = true;
        });
        TogglePin = new RelayCommand(p =>
        {
            if (p is RecentCardViewModel card) card.Pinned = !card.Pinned;
        });

        OpenBench = new RelayCommand(() => shell.Tab = MainViewModel.TabBench);
        OpenExport = new RelayCommand(() => shell.Tab = MainViewModel.TabExport);
        OpenRecipe = new RelayCommand(() => shell.Tab = MainViewModel.TabRecipe);
    }

    private void Add(string n, string path, string th, string tag, string tagc,
                     string when, string size, bool pinned, bool on, string note)
        => Recent.Add(new RecentCardViewModel
        {
            Name = n, Path = path, Thumb = th, Tag = tag, TagClass = tagc,
            When = when, Size = size, Note = note, Pinned = pinned, On = on
        });

    public Workspace Workspace { get; }
    public ObservableCollection<RecentCardViewModel> Recent { get; } = new();

    public RelayCommand Pick { get; }
    public RelayCommand TogglePin { get; }
    public RelayCommand OpenBench { get; }
    public RelayCommand OpenExport { get; }
    public RelayCommand OpenRecipe { get; }

    public string Subtitle => "平行合成工作站 1.0 · 台面 PSW-4 · 2 台双通道反应器 · 4 通道";
}

// ── 配方库视图 ───────────────────────────────────────────────────────

public sealed class RecipeLibViewModel : ViewModelBase
{
    private readonly Workspace _ws;
    private readonly MainViewModel _shell;
    private Recipe? _selected;

    public RecipeLibViewModel(Workspace ws, MainViewModel shell)
    {
        _ws = ws;
        _shell = shell;
        foreach (var r in ws.Library) Items.Add(r);
        _selected = Items.FirstOrDefault();

        Duplicate = new RelayCommand(() =>
        {
            if (_selected is null) return;
            var copy = _selected.Snapshot();
            copy.Name = _selected.Name + " 副本";
            ws.Library.Add(copy);
            Items.Add(copy);
            Selected = copy;
        });

        Delete = new RelayCommand(() =>
        {
            if (_selected is null) return;
            ws.Library.Remove(_selected);
            Items.Remove(_selected);
            Selected = Items.FirstOrDefault();
        });

        ApplyToRecipe = new RelayCommand(() =>
        {
            if (_selected is null) return;
            var copy = _selected.Snapshot();
            ws.ChannelRecipes[shell.Recipe.CurCh] = copy;
            ws.LaneNames[shell.Recipe.CurCh] = copy.Name;
            shell.Recipe.RefreshAll();
            shell.Tab = MainViewModel.TabRecipe;
        });

        Refresh();
    }

    public ObservableCollection<Recipe> Items { get; } = new();
    public ObservableCollection<StepViewModel> Flow { get; } = new();

    public RelayCommand Duplicate { get; }
    public RelayCommand Delete { get; }
    public RelayCommand ApplyToRecipe { get; }

    public Recipe? Selected
    {
        get => _selected;
        set { if (Set(ref _selected, value)) Refresh(); }
    }

    public string Name => _selected?.Name ?? "—";
    public string Desc => _selected is null
        ? ""
        : $"{_selected.Steps.Count} 步 · 预计 {Fmt.Hms(Plan.Total)} · {_selected.Author ?? "—"}";

    public Schedule Plan { get; private set; } = Schedule.Empty;

    private void Refresh()
    {
        Flow.Clear();
        Plan = _selected is null ? Schedule.Empty : Schedule.Build(_selected, _ws.Catalog);
        if (_selected is not null)
            for (var i = 0; i < _selected.Steps.Count; i++)
            {
                _ws.Catalog.TryGet(_selected.Steps[i].CommandId, out var d);
                Flow.Add(new StepViewModel(i + 1, _selected.Steps[i], Plan.Entries[i], d));
            }
        RaiseAll(nameof(Name), nameof(Desc));
    }
}

// ── 化合物数据库 ─────────────────────────────────────────────────────

public sealed class CompoundViewModel
{
    public required string Name { get; init; }
    public required string Cas { get; init; }
    public required string Formula { get; init; }
    public required double Mw { get; init; }
    public required double Mp { get; init; }
    public required string Category { get; init; }
    /// <summary>溶解度三点：25 / 50 / 80 ℃ 附近的 g/g 溶剂。原型 sol 数组。</summary>
    public required double[] Solubility { get; init; }
    public required string Solvent { get; init; }
    public required string Note { get; init; }

    public string MwText => Mw.ToString("F2", CultureInfo.InvariantCulture);
    public string MpText => Mp.ToString("F1", CultureInfo.InvariantCulture) + " ℃";
    public string CategoryColor => Category switch
    {
        "有机酸" => "#ec5a24",
        "药物" => "#3f6fd8",
        "氨基酸" => "#2f8f49",
        "无机盐" => "#8a63d2",
        _ => "#dba32c"
    };
}

public sealed class CompoundsViewModel : ViewModelBase
{
    private string _search = "";
    private string _category = "全部";
    private CompoundViewModel? _selected;

    private static readonly CompoundViewModel[] Seed =
    {
        New("苯甲酸", "65-85-0", "C7H6O2", 122.12, 122.4, "有机酸", new[] { 0.17, 0.006, 0.0006 }, "水 / 乙醇", "常用结晶模型物"),
        New("水杨酸", "69-72-7", "C7H6O3", 138.12, 158.6, "有机酸", new[] { 0.12, 0.004, 0.0005 }, "水 / 乙醇", "温度敏感"),
        New("柠檬酸", "77-92-9", "C6H8O7", 192.12, 153.0, "有机酸", new[] { 54, 1.5, 0.012 }, "水", "高溶解度"),
        New("对乙酰氨基酚", "103-90-2", "C8H9NO2", 151.16, 169.0, "药物", new[] { 0.8, 0.03, 0.002 }, "水 / 乙醇", "药物结晶筛选常用"),
        New("布洛芬", "15687-27-1", "C13H18O2", 206.28, 76.0, "药物", new[] { 0.002, 0.0004, 0.00008 }, "乙醇 / 乙酸乙酯", "难溶于水"),
        New("甘氨酸", "56-40-6", "C2H5NO2", 75.07, 233.0, "氨基酸", new[] { 14.2, 0.44, 0.004 }, "水", "多晶型 α/β/γ"),
        New("L-谷氨酸", "56-86-0", "C5H9NO4", 147.13, 199.0, "氨基酸", new[] { 0.35, 0.02, 0.001 }, "水", "多晶型 α/β"),
        New("硫酸铵", "7783-20-2", "(NH4)2SO4", 132.14, 235.0, "无机盐", new[] { 70.6, 0.25, 0.0 }, "水", "盐析常用"),
        New("氯化钾", "7447-40-7", "KCl", 74.55, 770.0, "无机盐", new[] { 28, 0.32, 0.0 }, "水", "教学演示"),
        New("蔗糖", "57-50-1", "C12H22O11", 342.30, 186.0, "糖类", new[] { 179, 1.1, 0.02 }, "水", "高粘度体系")
    };

    private static CompoundViewModel New(string n, string cas, string fx, double mw, double mp,
                                         string cat, double[] sol, string solvent, string note)
        => new()
        {
            Name = n, Cas = cas, Formula = fx, Mw = mw, Mp = mp,
            Category = cat, Solubility = sol, Solvent = solvent, Note = note
        };

    public CompoundsViewModel()
    {
        foreach (var c in new[] { "全部", "有机酸", "药物", "氨基酸", "无机盐", "糖类" }) Categories.Add(c);
        Apply();
        _selected = Rows.FirstOrDefault();
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
        set { if (Set(ref _selected, value)) RaiseAll(nameof(HasSelection), nameof(SolubilityText)); }
    }

    public bool HasSelection => _selected is not null;

    public string SolubilityText => _selected is null
        ? ""
        : $"25 ℃ {_selected.Solubility[0]:G3} · 50 ℃ {_selected.Solubility[1]:G3} · 80 ℃ {_selected.Solubility[2]:G3}"
          + $"（g/g {_selected.Solvent}）";

    private void Apply()
    {
        Rows.Clear();
        foreach (var c in Seed)
        {
            if (_category != "全部" && c.Category != _category) continue;
            if (_search.Length > 0 &&
                !c.Name.Contains(_search, StringComparison.OrdinalIgnoreCase) &&
                !c.Cas.Contains(_search, StringComparison.OrdinalIgnoreCase) &&
                !c.Formula.Contains(_search, StringComparison.OrdinalIgnoreCase)) continue;
            Rows.Add(c);
        }
    }
}
