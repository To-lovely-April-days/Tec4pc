using System.Collections.ObjectModel;
using System.Globalization;
using Tec.App.Controls;
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

public sealed class LibRowViewModel : ViewModelBase
{
    private bool _sel;
    public required Recipe Recipe { get; init; }
    public required IReadOnlyList<string> Mix { get; init; }
    public bool IsSelected
    {
        get => _sel;
        set { if (Set(ref _sel, value)) Raise(nameof(InkHex)); }
    }
    public string Name => Recipe.Name;
    /// <summary>属性面板改名后由列表行自己刷新（名字两处显示，必须同步）。</summary>
    public void NameChanged() => RaiseAll(nameof(Name), nameof(Meta));
    /// <summary>文档图标的描边色：选中转深蓝（原型 renderLibView 内联 SVG 的 stroke）。</summary>
    public string InkHex => _sel ? "#0b3760" : "#9a9a9a";
    /// <summary>原型 rm2：「N 步 · 更新 MM/dd」。</summary>
    public string Meta => $"{Recipe.Steps.Count} 步 · 更新 {Recipe.ModifiedAt:MM/dd}";
}

/// <summary>
/// 配方库（原型 renderLibView 的 1:1）：左 312px 列表（圆图标 + 名称 + 元信息 + 模块色带），
/// 中间只读流程预览（与配方页同一种步骤卡），右侧配方属性 + 应用到通道。
/// </summary>
public sealed class RecipeLibViewModel : ViewModelBase
{
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
            var copy = _selected.Recipe.Snapshot();
            copy.Name = _selected.Recipe.Name + "_副本";
            copy.ModifiedAt = DateTimeOffset.Now;
            ws.Library.Insert(ws.Library.IndexOf(_selected.Recipe) + 1, copy);
            Reload();
            Selected = Rows.FirstOrDefault(r => ReferenceEquals(r.Recipe, copy));
        });

        Delete = new RelayCommand(() =>
        {
            if (_selected is null || ws.Library.Count <= 1) return;   // 至少保留一个配方
            var i = ws.Library.IndexOf(_selected.Recipe);
            ws.Library.Remove(_selected.Recipe);
            Reload();
            Selected = Rows.ElementAtOrDefault(Math.Min(i, Rows.Count - 1)) ?? Rows.FirstOrDefault();
        });

        ApplyToChannel = new RelayCommand(() => Apply(false));
        ApplyToAll = new RelayCommand(() => Apply(true));
    }

    public ObservableCollection<LibRowViewModel> Rows { get; } = new();
    public ObservableCollection<StepViewModel> Flow { get; } = new();
    public ObservableCollection<string> ApplyTargets { get; } = new();

    public RelayCommand Duplicate { get; }
    public RelayCommand Delete { get; }
    public RelayCommand ApplyToChannel { get; }
    public RelayCommand ApplyToAll { get; }

    public LibRowViewModel? Selected
    {
        get => _selected;
        set
        {
            if (value is null) return;
            var old = _selected;
            if (!Set(ref _selected, value)) return;
            if (old is not null) old.IsSelected = false;
            value.IsSelected = true;
            Refresh();
        }
    }

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

    private void Reload()
    {
        Rows.Clear();
        foreach (var r in _ws.Library)
            Rows.Add(new LibRowViewModel { Recipe = r, Mix = MixOf(r) });
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
                 nameof(ApplyTarget), nameof(HasTargets), nameof(Current));
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
            _ws.ChannelRecipes[c] = r.Snapshot();
            _ws.LaneNames[c] = r.Name;
        }
        if (!all) _shell.Recipe.CurCh = targets[0];
        _shell.Recipe.RefreshAll();
        _shell.Tab = MainViewModel.TabRecipe;
    }
}

// ── 化合物数据库 ─────────────────────────────────────────────────────

public sealed class CompoundViewModel : ViewModelBase
{
    private bool _sel;
    private string _cas = "", _formula = "", _category = "", _solvent = "", _note = "";
    private double _mw, _mp;

    /// <summary>选中态由 CompoundsViewModel 统一维护（表格行整行加深加粗）。</summary>
    public bool IsSelected { get => _sel; set => Set(ref _sel, value); }

    public required string Name { get; init; }

    // 物性详情面板是这些字段的编辑面，改了表格同一行立刻跟着变
    public required string Cas { get => _cas; set => Set(ref _cas, value); }
    public required string Formula { get => _formula; set => Set(ref _formula, value); }
    public required string Category
    {
        get => _category;
        set { if (Set(ref _category, value)) Raise(nameof(CategoryColor)); }
    }
    public required string Solvent { get => _solvent; set => Set(ref _solvent, value); }
    public required string Note { get => _note; set => Set(ref _note, value); }

    public required double Mw
    {
        get => _mw;
        set { if (Set(ref _mw, value)) RaiseAll(nameof(MwText), nameof(MwEdit)); }
    }

    public required double Mp
    {
        get => _mp;
        set { if (Set(ref _mp, value)) RaiseAll(nameof(MpText), nameof(MpEdit)); }
    }

    /// <summary>溶解度对温度的二次拟合系数 a + b·T + c·T²（g/100 mL 水）。原型 sol 数组。</summary>
    public required double[] Solubility { get; init; }

    /// <summary>骨架式的画法数据；没有的（离子化合物）用 IonText 显示离子对。</summary>
    public Molecule? Structure { get; init; }
    public string? IonText { get; init; }

    /// <summary>分子量 / 熔点按原型的小数位显示：表格与编辑框用同一份格式，免得一个 342.30 一个 342.3。</summary>
    public string MwText => Mw.ToString("F2", CultureInfo.InvariantCulture);
    public string MpText => Mp.ToString("F1", CultureInfo.InvariantCulture);

    public string MwEdit
    {
        get => MwText;
        set { if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) Mw = v; }
    }

    public string MpEdit
    {
        get => MpText;
        set { if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) Mp = v; }
    }

    /// <summary>原型 CATCOLOR。</summary>
    public string CategoryColor => Category switch
    {
        "有机酸" => "#ec5a24",
        "药物" => "#3f6fd8",
        "氨基酸" => "#2f8f49",
        "无机盐" => "#8a5a3b",
        _ => "#c0399f"
    };
}

public sealed class CompoundsViewModel : ViewModelBase
{
    private string _search = "";
    private string _category = "全部";
    private CompoundViewModel? _selected;

    private static readonly CompoundViewModel[] Seed =
    {
        New("苯甲酸", "65-85-0", "C7H6O2", 122.12, 122.4, "有机酸", new[] { 0.17, 0.006, 0.0006 }, "水 / 乙醇", "常用结晶模型物", Structures.BenzoicAcid),
        New("水杨酸", "69-72-7", "C7H6O3", 138.12, 158.6, "有机酸", new[] { 0.12, 0.004, 0.0005 }, "水 / 乙醇", "温度敏感", Structures.SalicylicAcid),
        New("柠檬酸", "77-92-9", "C6H8O7", 192.12, 153.0, "有机酸", new[] { 54, 1.5, 0.012 }, "水", "高溶解度", Structures.CitricAcid),
        New("对乙酰氨基酚", "103-90-2", "C8H9NO2", 151.16, 169.0, "药物", new[] { 0.8, 0.03, 0.002 }, "水 / 乙醇", "药物结晶筛选常用", Structures.Paracetamol),
        New("布洛芬", "15687-27-1", "C13H18O2", 206.28, 76.0, "药物", new[] { 0.002, 0.0004, 0.00008 }, "乙醇 / 乙酸乙酯", "难溶于水", Structures.Ibuprofen),
        New("甘氨酸", "56-40-6", "C2H5NO2", 75.07, 233.0, "氨基酸", new[] { 14.2, 0.44, 0.004 }, "水", "多晶型 α/β/γ", Structures.Glycine),
        New("L-谷氨酸", "56-86-0", "C5H9NO4", 147.13, 199.0, "氨基酸", new[] { 0.35, 0.02, 0.001 }, "水", "多晶型 α/β", Structures.GlutamicAcid),
        New("硫酸铵", "7783-20-2", "(NH4)2SO4", 132.14, 235.0, "无机盐", new[] { 70.6, 0.25, 0.0 }, "水", "盐析常用", null, "2 NH₄⁺ + SO₄²⁻"),
        New("氯化钾", "7447-40-7", "KCl", 74.55, 770.0, "无机盐", new[] { 28, 0.32, 0.0 }, "水", "教学演示", null, "K⁺ + Cl⁻"),
        New("蔗糖", "57-50-1", "C12H22O11", 342.30, 186.0, "糖类", new[] { 179, 1.1, 0.02 }, "水", "高粘度体系", Structures.Sucrose)
    };

    private static CompoundViewModel New(string n, string cas, string fx, double mw, double mp,
                                         string cat, double[] sol, string solvent, string note,
                                         Molecule? structure = null, string? ion = null)
        => new()
        {
            Name = n, Cas = cas, Formula = fx, Mw = mw, Mp = mp,
            Category = cat, Solubility = sol, Solvent = solvent, Note = note,
            Structure = structure, IonText = ion
        };

    public CompoundsViewModel()
    {
        foreach (var c in new[] { "全部", "有机酸", "药物", "氨基酸", "无机盐", "糖类" }) Categories.Add(c);
        Apply();
        Selected = Rows.FirstOrDefault();
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
        Raise(nameof(NoRows));
    }
}
