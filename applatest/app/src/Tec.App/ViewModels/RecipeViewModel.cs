using System.Collections.ObjectModel;
using System.Globalization;
using Tec.App.Services;
using Tec.Core;
using Tec.Core.Benches;
using Tec.Core.Catalog;
using Tec.Core.Chemistry;
using Tec.Core.Persistence;
using Tec.Core.Recipes;
using Tec.Core.Scheduling;
using Tec.Driver.Abi;
using Tec.Drivers.Simulator;

namespace Tec.App.ViewModels;

/// <summary>模块信息（原型 GRP）：标签、模块色。图标铺在模块色块上用白描边版。</summary>
public static class ModuleInfo
{
    public static string ColorOf(string module) => module switch
    {
        "通用" => "#9aa4ab",
        "温度模块" => "#ec5a24",
        "搅拌" => "#5b46bd",
        "加料" => "#c53a9d",
        "pH 控制" => "#39b54a",
        "在线分析" => "#dba32c",
        _ => "#9aa4ab"
    };
}

public sealed class CommandItemViewModel
{
    public CommandItemViewModel(CommandDescriptor d) => Descriptor = d;
    public CommandDescriptor Descriptor { get; }
    public string Name => Descriptor.DisplayName;
    public string Module => Descriptor.Module;
    public string IconKey => "cmd-" + (Descriptor.IconKey ?? "wait");
}

public sealed class ModuleGroup : ViewModelBase
{
    private bool _open = true;

    public ModuleGroup(string name)
    {
        Name = name;
        Color = ModuleInfo.ColorOf(name);
    }

    public string Name { get; }
    public string Color { get; }
    public ObservableCollection<CommandItemViewModel> Commands { get; } = new();

    public bool Open
    {
        get => _open;
        set => Set(ref _open, value);
    }
}

public static class TerminationText
{
    public static string Of(TerminationKind k) => k switch
    {
        TerminationKind.Setpoint => "到达目标",
        TerminationKind.Timer => "计时到",
        TerminationKind.Quantity => "加完设定量",
        TerminationKind.Condition => "条件满足",
        TerminationKind.Operator => "操作人",
        TerminationKind.Alarm => "报警",
        TerminationKind.Timeout => "超时",
        _ => "立即"
    };
}

/// <summary>
/// 步骤表里的一个参数小标签：「目标温度」「60.0 ℃」。
/// Lead 是它前面那个分隔点——第一个没有，后面的都有（原型 .c-p em）。
/// 摆在数据里而不是模板里：Avalonia 的选择器没有 :first-child。
/// </summary>
public sealed record ParamChip(string Label, string Value, string Lead = "");

/// <summary>一张步骤卡（原型 .step）：模块色图标块 + 整句描述 + 预计开始 / 耗时。</summary>
public sealed class StepViewModel : ViewModelBase
{
    public StepViewModel(int ordinal, Step step, ScheduleEntry entry, CommandDescriptor? d)
    {
        Ordinal = ordinal;
        Step = step;
        Entry = entry;
        Descriptor = d;
    }

    public int Ordinal { get; }
    public Step Step { get; }

    /// <summary>
    /// 这一步套在几层循环里。循环开始 / 结束行自己算外层——
    /// 卡片按它缩进，一眼看得出哪几步是循环体。
    /// </summary>
    public int Depth { get; init; }

    /// <summary>每一层画一条竖线。0 层就是空表，版面跟原来一样。</summary>
    public IReadOnlyList<int> Spines => Enumerable.Range(0, Depth).ToList();

    /// <summary>卡片宽度让出缩进，泳道整体还是 200（原型 .step 宽），各条泳道对得齐。</summary>
    public double CardWidth => 200 - Depth * 14;

    /// <summary>
    /// 卡片正文那一列的宽度：卡宽 − 2px 描边两条 − 左边（6px 色条 + 18px 竖排名）
    /// − 右边同样宽的 24px 空档。左右一样宽，图标和描述才真的落在卡片中线上。
    /// 算死而不是交给 Grid 的星号列去分：星号列量到的宽和最后排布的宽对不上，
    /// 描述会按更宽的那个折行，右半截被卡片裁掉（"加入 10" 断成 "加入 1"）。
    /// </summary>
    public double BodyWidth => CardWidth - 4 - 24 - 24;

    /// <summary>
    /// 描述那行文字的盒子宽度。写死在 TextBlock 上，再配上它自己 6px 的左右
    /// 内边距——两件事缺一不可：
    ///   · 宽度写死，排版和排布用的是同一个数；
    ///   · 内边距让**排版宽比盒子窄 12px**，一行排到头时最后那个字的墨迹
    ///     比排版算出来的行宽多出来的一点有地方去。只写宽度不留内边距，
    ///     那点墨迹会被 TextBlock 自己的边界切掉（"加入" 只剩一撇）。
    /// </summary>
    public double LabelWidth => BodyWidth - 8;

    public ScheduleEntry Entry { get; }
    public CommandDescriptor? Descriptor { get; }

    public string Name => Descriptor?.DisplayName ?? Entry.CommandId;
    public string Module => Descriptor?.Module ?? "—";
    public string ModuleColor => ModuleInfo.ColorOf(Module);
    public string IconKey => "cmd-" + (Descriptor?.IconKey ?? "wait");
    /// <summary>整句工艺语句（原型 stepDesc → DESC）。</summary>
    public string Desc => Entry.Title;
    /// <summary>卡片摘要（原型 PSPEC.sum）。</summary>
    public string Summary => Descriptor is null
        ? "缺少驱动"
        : Descriptor.SummaryOf(new CommandInput(Step.Parameters, Step.Rows));
    public string ModLine => $"{Module} · {Name}";

    /// <summary>配方库那张步骤表用的：序号、结束条件、工艺阶段、参数小标签。</summary>
    public int Seq => Ordinal;
    public string EndText => TerminationText.Of(Entry.Termination);
    public string PhaseText => Step.Phase ?? "";

    /// <summary>
    /// 参数小标签：「标签 值 单位」。标签和单位取自指令自己的 ParameterSchema，
    /// 不在这儿另写一份——加一个参数字段，表上自然就多一个标签。
    /// 分段表（梯度控温那种）不摊开，只报一句有几段。
    /// </summary>
    public IReadOnlyList<ParamChip> Chips => _chips ??= BuildChips();
    private IReadOnlyList<ParamChip>? _chips;

    /// <summary>这一步有没有参数可摊开。没有的话卡片第二行写一句「无参数」。</summary>
    public bool HasChips => Chips.Count > 0;

    private IReadOnlyList<ParamChip> BuildChips()
    {
        {
            var list = new List<ParamChip>();
            if (Descriptor is null) return list;
            foreach (var f in Descriptor.Parameters.Fields)
            {
                if (!Step.Parameters.Has(f.Key)) continue;
                var v = Step.Parameters[f.Key];
                if (v is null) continue;
                string text;
                switch (f.Kind)
                {
                    case FieldKind.Number:
                        var d = Step.Parameters.Num(f.Key);
                        text = d.ToString("F" + f.Decimals, CultureInfo.InvariantCulture);
                        if (!string.IsNullOrEmpty(f.Unit)) text += " " + f.Unit;
                        break;
                    case FieldKind.Toggle:
                        if (v is not true) continue;      // 关着的开关不占位置
                        text = "是";
                        break;
                    default:
                        text = v.ToString() ?? "";
                        break;
                }
                if (text.Length > 0)
                    list.Add(new ParamChip(f.Label, text, list.Count == 0 ? "" : "·"));
            }
            if (Step.Rows is { Count: > 0 } rows)
                list.Add(new ParamChip("分段", rows.Count + " 段", list.Count == 0 ? "" : "·"));
            return list;
        }
    }

    public string PlanStart => Fmt.Offset(Entry.Start);
    public string PlanDuration => Fmt.Hms(Entry.Extent);
    public bool HasDuration => Entry.Extent > TimeSpan.Zero;
    public bool IsMissing => !Entry.Known;

    private bool _selected;
    public bool IsSelected
    {
        get => _selected;
        set => Set(ref _selected, value);
    }

    /// <summary>拖拽落点指示：松手会插在这张卡**之前**。画在卡片上方那截连接线上。</summary>
    private bool _dropBefore;
    public bool DropBefore
    {
        get => _dropBefore;
        set => Set(ref _dropBefore, value);
    }

    /// <summary>正被拖走的那张卡自己变淡，好让人看出「它要挪去别处」。</summary>
    private bool _ghosted;
    public bool Ghosted
    {
        get => _ghosted;
        set => Set(ref _ghosted, value);
    }

    /// <summary>
    /// 这一步身上有没有 Error 级问题。卡片据此描红边、右上角亮一颗感叹号。
    /// **标在卡片上而不是只列在清单里**：清单要点开才看得见，
    /// 而出问题的是画布上这一张——错在哪儿得在原地就能看见。
    /// </summary>
    private string? _errorTip;
    public string? ErrorTip
    {
        get => _errorTip;
        set { if (Set(ref _errorTip, value)) Raise(nameof(HasError)); }
    }

    public bool HasError => _errorTip is not null;
}

/// <summary>一条泳道 = 一个通道的配方（原型 .lane）。</summary>
public sealed class LaneViewModel : ViewModelBase
{
    private readonly RecipeViewModel _owner;
    private bool _isCurrent;

    public LaneViewModel(RecipeViewModel owner, int channel)
    {
        _owner = owner;
        Channel = channel;
        Select = new RelayCommand(() => owner.CurCh = channel);
    }

    public int Channel { get; }
    public RelayCommand Select { get; }
    public ObservableCollection<StepViewModel> Steps { get; } = new();

    public string LaneName
    {
        get => _owner.Workspace.LaneNames.TryGetValue(Channel, out var n) ? n : "新配方";
        set
        {
            _owner.Workspace.LaneNames[Channel] = value;
            if (_owner.Workspace.ChannelRecipes.TryGetValue(Channel, out var r)) r.Name = value;
            _owner.Workspace.Store.MarkDirty();
            Raise();
        }
    }

    /// <summary>机A · CH1（原型 chLabel）。</summary>
    public string ChannelLabel
    {
        get
        {
            var ch = _owner.Workspace.ChannelOf(Channel);
            if (ch is null) return $"CH{Channel}";
            var hosts = _owner.Workspace.Bench.Devices
                .Where(d => d.DriverId == Rd105ReactorDriver.DriverId).ToList();
            var idx = hosts.FindIndex(d => d.InstanceId == ch.HostInstanceId);
            var machine = idx >= 0 && idx < 8 ? "机" + "ABCDEFGH"[idx] : ch.HostInstanceId;
            return $"{machine} · CH{Channel}";
        }
    }

    /// <summary>可改挂到哪个通道（原型 lh-sel + remapLane）。</summary>
    public ObservableCollection<string> ChannelOptions { get; } = new();

    public string ChannelPick
    {
        get => ChannelLabel;
        set
        {
            // 「机A · CH1」里取 CHn
            var i = value?.LastIndexOf("CH", StringComparison.Ordinal) ?? -1;
            if (i < 0 || !int.TryParse(value![(i + 2)..], out var target) || target == Channel) return;
            _owner.Remap(Channel, target);
        }
    }

    public void RefreshOptions()
    {
        Raise(nameof(LaneName));
        ChannelOptions.Clear();
        foreach (var c in _owner.Workspace.Channels.Where(c => c.Enabled).OrderBy(c => c.Number))
            ChannelOptions.Add(_owner.LabelOf(c.Number));
        RaiseAll(nameof(ChannelLabel), nameof(ChannelPick));
    }

    public bool IsCurrent
    {
        get => _isCurrent;
        set => Set(ref _isCurrent, value);
    }

    /// <summary>
    /// 这条泳道上的 Error 数。角标钉在泳道右上角——四条泳道并排时，
    /// 「哪一路不能跑」要在扫一眼的距离上就分得出来，不该等人挨个点开看。
    /// 提醒（Warning）不上角标：它不挡启动，摆上去只会让四条泳道全挂着红点。
    /// </summary>
    private int _errors;
    public int Errors
    {
        get => _errors;
        set { if (Set(ref _errors, value)) RaiseAll(nameof(HasErrors), nameof(ErrorBadge)); }
    }

    public bool HasErrors => _errors > 0;
    public string ErrorBadge => $"{_errors} 错误";

    /// <summary>落点在这条泳道的末尾（拖到虚线框上）。</summary>
    private bool _dropAtEnd;
    public bool DropAtEnd
    {
        get => _dropAtEnd;
        set { if (Set(ref _dropAtEnd, value)) Raise(nameof(DropHint)); }
    }

    /// <summary>松手会落在这条泳道里。整条描蓝，一眼看得出要放进哪个通道。</summary>
    private bool _isDropLane;
    public bool IsDropLane
    {
        get => _isDropLane;
        set => Set(ref _isDropLane, value);
    }

    public string DropHint => DropAtEnd
        ? "松手放到这里"
        : IsCurrent ? "从左侧步骤库拖进来，或点一下也行" : "点选此列后可编辑";

    public void RefreshHint() => RaiseAll(nameof(DropHint), nameof(IsCurrent));
}

/// <summary>
/// 配方视图（原型 view-recipe 的 1:1）：
/// 左 308px 工具箱（通用平铺 + 5 组折叠图标网格）｜中 配方工具条 + 一键复制条 + 多通道泳道｜
/// 右 342px 步骤属性 + 应用配方库 + 通道状态。
/// </summary>
public sealed class RecipeViewModel : ViewModelBase
{
    private StepViewModel? _selectedStep;
    private int _curCh = 1;
    private int _copyTarget = 2;
    private Recipe? _libPick;
    private SchemaFormViewModel? _form;
    private readonly RecipeHistory _history = new();

    public RecipeViewModel(Workspace ws)
    {
        Workspace = ws;

        foreach (var m in ws.Catalog.Modules)
        {
            var group = new ModuleGroup(m);
            foreach (var c in ws.Catalog.InModule(m)) group.Commands.Add(new CommandItemViewModel(c));
            Groups.Add(group);
        }

        foreach (var r in ws.Library) Library.Add(new LibOption(r));
        _libPick = Library.FirstOrDefault()?.Recipe;

        AddStep = new RelayCommand(p => { if (p is CommandItemViewModel c) AddCommand(c); });
        RemoveStep = new RelayCommand(p => { if (p is StepViewModel s) Delete(s); });
        CopyRecipe = new RelayCommand(DoCopy);
        ApplyLib = new RelayCommand(DoApplyLib);
        SaveToLib = new RelayCommand(DoSaveToLib);
        NewRecipe = new RelayCommand(DoNew);
        ManageLibrary = new RelayCommand(() => GoLibrary?.Invoke());
        ImportRecipe = new RelayCommand(() => _ = DoImportAsync());
        ExportRecipe = new RelayCommand(() => _ = DoExportAsync());
        PickIssue = new RelayCommand(p => { if (p is IssueRow r) SelectIssue(r); });
        TogglePanel = new RelayCommand(() => PanelOpen = !PanelOpen);
        ClosePanel = new RelayCommand(() => PanelOpen = false);
        ShowAll = new RelayCommand(() => OnlyErrors = false);
        ShowOnlyErrors = new RelayCommand(() => OnlyErrors = true);
        Undo = new RelayCommand(DoUndo);
        Redo = new RelayCommand(DoRedo);
        ToggleVars = new RelayCommand(() => VarsOpen = !VarsOpen);
        AddVar = new RelayCommand(DoAddVar);
        RemoveVar = new RelayCommand(p => { if (p is VarRowViewModel r) DoRemoveVar(r); });

        // 台面变了通道就变了，泳道的通道下拉要跟着刷新
        ws.BenchChanged += (_, _) => RefreshAll();
        // 打开别的实验会整份换掉配方库，右栏那个下拉得跟着换
        ws.Store.Changed += (_, _) => ReloadLibrary();
        // 配方库页那边增删了配方，这边的下拉也得当场跟上
        ws.Library.CollectionChanged += (_, _) => ReloadLibrary();

        RefreshAll();
    }

    public Workspace Workspace { get; }

    private void ReloadLibrary()
    {
        var keepId = _libPick?.Id;
        Library.Clear();
        foreach (var r in Workspace.Library) Library.Add(new LibOption(r));
        LibPick = Library.FirstOrDefault(o => o.Recipe.Id == keepId) ?? Library.FirstOrDefault();
    }

    /// <summary>机A · CH1（原型 chLabel），泳道下拉与标题共用。</summary>
    public string LabelOf(int channel)
    {
        var ch = Workspace.ChannelOf(channel);
        if (ch is null) return $"CH{channel}";
        var hosts = Workspace.Bench.Devices
            .Where(d => d.DriverId == Rd105ReactorDriver.DriverId).ToList();
        var idx = hosts.FindIndex(d => d.InstanceId == ch.HostInstanceId);
        var machine = idx >= 0 && idx < 8 ? "机" + "ABCDEFGH"[idx] : ch.HostInstanceId;
        return $"{machine} · CH{channel}";
    }

    /// <summary>把一条泳道改挂到另一个通道（原型 remapLane）：两边的配方与名称对调。</summary>
    public void Remap(int from, int to)
    {
        if (from == to) return;
        var recipes = Workspace.ChannelRecipes;
        var names = Workspace.LaneNames;
        recipes.TryGetValue(from, out var ra);
        recipes.TryGetValue(to, out var rb);
        names.TryGetValue(from, out var na);
        names.TryGetValue(to, out var nb);
        _history.Forget(from);
        _history.Forget(to);
        if (ra is not null) recipes[to] = ra; else recipes.Remove(to);
        if (rb is not null) recipes[from] = rb; else recipes.Remove(from);
        names[to] = na ?? "新配方";
        names[from] = nb ?? "新配方";
        Workspace.Store.MarkDirty();
        CurCh = to;
        RefreshAll();
    }

    /// <summary>删掉这条泳道（原型 removeChannelTab）：清空该通道的配方。</summary>
    public void RemoveLane(int channel)
    {
        _history.Forget(channel);          // 泳道都清了，那条道的历史对不上了
        Workspace.ChannelRecipes[channel] = new Recipe { Name = "新配方" };
        Workspace.LaneNames[channel] = "新配方";
        Workspace.Store.MarkDirty();
        RefreshAll();
    }

    public ObservableCollection<ModuleGroup> Groups { get; } = new();
    public ObservableCollection<LaneViewModel> Lanes { get; } = new();
    public ObservableCollection<LibOption> Library { get; } = new();
    public ObservableCollection<CopyTargetOption> CopyTargets { get; } = new();
    public ObservableCollection<ValidationIssue> Issues { get; } = new();

    /// <summary>
    /// 校验结果里要摆到界面上的那些。Info 级的「预计总时长」不算问题，
    /// 单独走 TotalNote——把它混进问题清单，操作人会以为配方有毛病。
    /// </summary>
    public ObservableCollection<IssueRow> Problems { get; } = new();

    public bool HasProblems => Problems.Count > 0;

    /// <summary>面板里当下显示的那几行（受「仅错误」开关过滤）。</summary>
    public ObservableCollection<IssueRow> PanelRows { get; } = new();

    public int ErrorCount => Problems.Count(p => p.IsError);
    public int WarnCount => Problems.Count - ErrorCount;
    public bool HasErrors => ErrorCount > 0;
    public bool HasWarns => WarnCount > 0;
    public string ErrorText => $"{ErrorCount} 错误";
    public string WarnText => $"{WarnCount} 提醒";

    /// <summary>汇总条右半段：「0:20:00 · 3 步」。跟问题分开，中间一条竖线隔着。</summary>
    public string ScaleText
    {
        get
        {
            if (NoLanes) return "";
            var plan = Schedule.Build(Current, Workspace.Catalog);
            var n = Current.Steps.Count(x => x.CommandId != BuiltinCommands.Finish);
            return $"{Fmt.Hms(plan.Total)} · {n} 步";
        }
    }

    private bool _panelOpen;
    /// <summary>校验面板展开着没有。默认收着——没问题的时候它不该占地方。</summary>
    public bool PanelOpen
    {
        get => _panelOpen;
        set { if (Set(ref _panelOpen, value)) Raise(nameof(PanelArrow)); }
    }

    public string PanelArrow => _panelOpen ? "▾" : "▴";

    private bool _onlyErrors;
    /// <summary>面板上的「仅错误」。提醒多起来的时候，先把真挡启动的挑出来看。</summary>
    public bool OnlyErrors
    {
        get => _onlyErrors;
        set { if (Set(ref _onlyErrors, value)) FillPanel(); }
    }

    public bool AllTab => !_onlyErrors;

    private void FillPanel()
    {
        PanelRows.Clear();
        foreach (var r in Problems)
            if (!_onlyErrors || r.IsError) PanelRows.Add(r);
        // 一条问题都没有了就自己收起来：改完最后一处还挂着一块空面板，
        // 人会以为它没刷新。这也让下面那句空态话只可能出现在「仅错误」筛下
        if (Problems.Count == 0) PanelOpen = false;
        RaiseAll(nameof(AllTab), nameof(PanelEmpty));
    }

    /// <summary>「仅错误」筛完一条不剩：说一句，别留一块空白让人以为没加载出来。</summary>
    public bool PanelEmpty => PanelRows.Count == 0;

    /// <summary>预计总时长那一句。没有问题时它就是这条配方唯一的"体检结论"。</summary>
    public string TotalNote { get; private set; } = "";

    public RelayCommand AddStep { get; }
    public RelayCommand RemoveStep { get; }
    public RelayCommand CopyRecipe { get; }
    public RelayCommand ApplyLib { get; }
    public RelayCommand SaveToLib { get; }
    public RelayCommand NewRecipe { get; }
    public RelayCommand ManageLibrary { get; }
    public RelayCommand ImportRecipe { get; }
    public RelayCommand ExportRecipe { get; }
    public RelayCommand PickIssue { get; }
    public RelayCommand TogglePanel { get; }
    public RelayCommand ClosePanel { get; }
    public RelayCommand ShowAll { get; }
    public RelayCommand ShowOnlyErrors { get; }

    /// <summary>导入 / 导出的结果说给操作人听。由外壳接到开始页的状态行上。</summary>
    public Action<string>? Say { get; set; }

    /// <summary>
    /// 同一句话也写在配方页的工具条右边——人在配方页操作，结果只写到开始页
    /// 那条状态行上，他是看不见的。
    /// </summary>
    private string _note = "";
    public string Note
    {
        get => _note;
        private set { if (Set(ref _note, value)) Raise(nameof(HasNote)); }
    }

    public bool HasNote => _note.Length > 0;

    private void Tell(string text)
    {
        Note = text;
        Say?.Invoke(text);
    }

    // 右栏三个可折叠小节。默认都展开——这是常用面板，一进来就该看得见
    public SectionViewModel ExecSection { get; } = new();
    public SectionViewModel LibSection { get; } = new();
    public SectionViewModel StatesSection { get; } = new();
    /// <summary>「管理配方库 →」往哪跳。由外壳注入，视图模型不认识标签页。</summary>
    public Action? GoLibrary { get; set; }
    public RelayCommand Undo { get; }
    public RelayCommand Redo { get; }

    public bool CanUndo => _history.CanUndo(_curCh);
    public bool CanRedo => _history.CanRedo(_curCh);

    public int CurCh
    {
        get => _curCh;
        set
        {
            if (!Set(ref _curCh, value)) return;
            SelectedStep = null;
            RefreshAll();
        }
    }

    public string CurName => Workspace.LaneNames.TryGetValue(_curCh, out var n) ? $"通道 {_curCh}（{n}）" : $"通道 {_curCh}";

    /// <summary>工具条那个「配方：」下拉。泳道是动态的，不能靠 SelectedIndex="0" 顶着。</summary>
    public LaneViewModel? CurLane
    {
        get => Lanes.FirstOrDefault(l => l.Channel == _curCh);
        set { if (value is not null) CurCh = value.Channel; }
    }

    /// <summary>
    /// 复制目标。可空——泳道是动态的，选项一度会是空的，
    /// ComboBox 这时会把 SelectedItem 置回 null，收不下 null 就直接抛类型转换异常。
    /// </summary>
    public CopyTargetOption? CopyTarget
    {
        get => CopyTargets.FirstOrDefault(o => o.Channel == _copyTarget);
        set { if (value is not null) Set(ref _copyTarget, value.Channel); else Raise(); }
    }

    /// <summary>
    /// 「应用配方库」选中的那一项。
    ///
    /// setter 必须在没变时**什么都不做**：ComboBox 是双向绑定，无条件 Raise 会让它
    /// 把值再写回来，一来一回就是死循环——上一版这里栈溢出过。
    /// </summary>
    public LibOption? LibPick
    {
        get => Library.FirstOrDefault(o => ReferenceEquals(o.Recipe, _libPick));
        set
        {
            if (value is null || ReferenceEquals(value.Recipe, _libPick)) return;
            _libPick = value.Recipe;
            Raise();
        }
    }

    public StepViewModel? SelectedStep
    {
        get => _selectedStep;
        set
        {
            if (_selectedStep is not null) _selectedStep.IsSelected = false;
            Set(ref _selectedStep, value);
            if (value is not null) value.IsSelected = true;
            RebuildForm();
            RaiseAll(nameof(HasSelection), nameof(NoSelection), nameof(StepName), nameof(StepIcon),
                     nameof(StepChannel), nameof(NoParams), nameof(PauseOnFault), nameof(StepSkipped), nameof(StepPhase));
        }
    }

    public SchemaFormViewModel? Form
    {
        get => _form;
        private set => Set(ref _form, value);
    }

    public bool HasSelection => _selectedStep is not null;
    public bool NoSelection => _selectedStep is null;

    // prop-head：指令图标 + 指令名 + 它属于哪个通道。
    // 从前是一个 12px 模块色块——同一个模块下的指令色块全一样，认不出是哪一条；
    // 换成这一步自己的图标，跟步骤库和泳道卡上看到的是同一张
    public string StepName => _selectedStep?.Name ?? "";
    public string StepIcon => _selectedStep?.IconKey ?? "cmd-wait";
    public string StepChannel => LabelOf(_curCh);

    /// <summary>这条指令一个参数都没有（比如「循环结束」）。空着不说话会让人以为界面坏了。</summary>
    public bool NoParams => _selectedStep?.Descriptor is { } d
                            && d.Parameters.Fields.Count == 0 && d.Parameters.Table is null;

    // ── 工艺阶段 ────────────────────────────────────────────────────

    /// <summary>
    /// 可选的工艺阶段。**没有「未标注」以外的空项**——选了就是选了，
    /// 想撤回就选回「未标注」。
    /// </summary>
    private static readonly string[] Phases =
    {
        NoPhase, "升温", "保温", "降温", "结晶", "溶解", "蒸馏", "加料", "反应", "淬灭", "后处理"
    };

    /// <summary>绑定用的是**实例**属性：反射绑定按 DataContext 的类型找实例成员，
    /// 静态属性它看不见——下拉框会是一个空框。</summary>
    public IReadOnlyList<string> PhaseOptions => Phases;

    public const string NoPhase = "未标注";

    /// <summary>
    /// 这一步属于哪个工艺阶段。
    ///
    /// **这是操作人自己标的，机器不知道。**设备只知道自己在按 Tr 还是按 Tj 控温；
    /// 「这一段在结晶」是人的判断。事后看 Tr−Tj 曲线，要读得懂靠的正是这一条，
    /// 所以它进记录、进报告，并在报告里注明是操作人标注的。
    /// </summary>
    public string StepPhase
    {
        get => _selectedStep?.Step.Phase is { Length: > 0 } p ? p : NoPhase;
        set
        {
            if (_selectedStep is not { } s) return;
            var v = value == NoPhase || string.IsNullOrWhiteSpace(value) ? null : value;
            if (s.Step.Phase == v) return;
            Record();
            s.Step.Phase = v;
            Workspace.Store.MarkDirty();
            Raise();
        }
    }

    // ── 执行选项：两个真的会影响运行的开关，不是摆设 ──────────────────

    /// <summary>失败时暂停并报警。执行引擎按它决定是停下等人还是接着往下走。</summary>
    public bool PauseOnFault
    {
        get => _selectedStep?.Step.PauseOnFault ?? true;
        set
        {
            if (_selectedStep is not { } s || s.Step.PauseOnFault == value) return;
            Record();
            s.Step.PauseOnFault = value;
            Workspace.Store.MarkDirty();
            Raise();
        }
    }

    /// <summary>
    /// 跳过该步骤。原型这一格写的是「条件满足则跳过」，但它没有地方填条件——
    /// 一个填不了条件的条件开关点下去什么也不会发生。这里落成实打实的「跳过」：
    /// 排期与执行都认 Step.Enabled，勾上以后这一步既不占时间也不会跑。
    /// </summary>
    public bool StepSkipped
    {
        get => _selectedStep is { } s && !s.Step.Enabled;
        set
        {
            if (_selectedStep is not { } s || s.Step.Enabled == !value) return;
            Record();
            s.Step.Enabled = !value;
            Workspace.Store.MarkDirty();
            RefreshAll();
            Raise();
        }
    }

    /// <summary>
    /// 当前泳道的配方。台面上一个通道都没有时给一条不入库的空配方顶着，
    /// 免得为了让界面不崩，反手在 ChannelRecipes 里造出一条根本不存在的通道。
    /// </summary>
    public Recipe Current => Workspace.ChannelRecipes.TryGetValue(_curCh, out var r) ? r : _scratch;

    private readonly Recipe _scratch = new() { Name = "新配方" };

    // ── 中列操作 ─────────────────────────────────────────────────────

    private void AddCommand(CommandItemViewModel c)
    {
        if (NoLanes) return;                      // 没有通道就没有地方放这一步
        Record();
        var step = NewStep(c.Descriptor);
        var recipe = Current;
        var at = _selectedStep is null ? recipe.Steps.Count : recipe.Steps.IndexOf(_selectedStep.Step) + 1;
        recipe.Steps.Insert(Math.Clamp(at, 0, recipe.Steps.Count), step);
        recipe.ModifiedAt = DateTimeOffset.Now;
        Workspace.Store.MarkDirty();
        RefreshAll();
        SelectedStep = Lanes.First(l => l.Channel == _curCh).Steps.FirstOrDefault(v => v.Step.StepId == step.StepId);
    }

    /// <summary>梯度控温的默认分段。</summary>
    private static List<ParameterSet> DefaultRows(CommandDescriptor d) => d.Id switch
    {
        CommandSpecs.Gradient => new List<ParameterSet>
        {
            ParameterSet.Of(("t", 60d), ("r", 1d), ("h", 10d)),
            ParameterSet.Of(("t", 30d), ("r", 0.3d), ("h", 20d)),
            ParameterSet.Of(("t", 5d), ("r", 0.1d), ("h", 30d))
        },
        _ => new List<ParameterSet>()
    };

    private void Delete(StepViewModel s)
    {
        Record();
        Current.Steps.Remove(s.Step);
        if (_selectedStep == s) SelectedStep = null;
        Workspace.Store.MarkDirty();
        RefreshAll();
    }

    // ── 拖拽：从步骤库拖进泳道 / 在泳道之间挪步骤 ─────────────────────
    //
    // 手写指针跟踪而不是用 DragDrop：落点指示要精确到「插在第几张卡之前」，
    // 系统拖放给不了这个粒度，而且台面那边已经是这套写法，两处保持一致。

    private CommandItemViewModel? _dragCmd;
    private StepViewModel? _dragStep;
    private int _dragFromCh;
    private bool _dragging;
    private int? _dropCh;
    private int _dropAt;

    public bool Dragging
    {
        get => _dragging;
        private set => Set(ref _dragging, value);
    }

    private double _dragX, _dragY;
    public double DragX { get => _dragX; private set => Set(ref _dragX, value); }
    public double DragY { get => _dragY; private set => Set(ref _dragY, value); }

    public string DragTitle { get; private set; } = "";
    public string DragIcon { get; private set; } = "cmd-wait";
    public string DragColor { get; private set; } = "#9aa4ab";

    /// <summary>从左边的步骤库拖出来。</summary>
    public void BeginDragCommand(CommandItemViewModel c)
    {
        if (NoLanes) return;                       // 没有通道就没有地方放
        _dragCmd = c;
        _dragStep = null;
        DragTitle = c.Name;
        DragIcon = c.IconKey;
        DragColor = ModuleInfo.ColorOf(c.Module);
        StartDrag();
    }

    /// <summary>拖泳道里已有的步骤：可以在本道内换位置，也可以整条挪到别的通道。</summary>
    public void BeginDragStep(StepViewModel s, int fromChannel)
    {
        _dragStep = s;
        _dragCmd = null;
        _dragFromCh = fromChannel;
        DragTitle = s.Name;
        DragIcon = s.IconKey;
        DragColor = s.ModuleColor;
        s.Ghosted = true;
        StartDrag();
    }

    private void StartDrag()
    {
        _dropCh = null;
        Dragging = true;
        RaiseAll(nameof(DragTitle), nameof(DragIcon), nameof(DragColor));
    }

    /// <summary>
    /// 指针挪到哪了。落点由视图算好传进来——只有视图知道每张卡片实际画在哪。
    /// channel 为 null 表示当前不在任何泳道上，这时不给落点指示：
    /// 拖到空处松手应该什么都不发生，而不是悄悄插到上一次的位置。
    /// </summary>
    public void DragTo(double x, double y, int? channel, int index)
    {
        DragX = x;
        DragY = y;
        if (channel == _dropCh && index == _dropAt) return;
        _dropCh = channel;
        _dropAt = index;
        ShowDropMark();
    }

    private void ShowDropMark()
    {
        foreach (var lane in Lanes)
        {
            var hit = _dropCh == lane.Channel;
            lane.IsDropLane = hit;
            lane.DropAtEnd = hit && _dropAt >= lane.Steps.Count;
            for (var i = 0; i < lane.Steps.Count; i++)
                lane.Steps[i].DropBefore = hit && i == _dropAt;
        }
    }

    private void ClearDropMark()
    {
        foreach (var lane in Lanes)
        {
            lane.DropAtEnd = false;
            lane.IsDropLane = false;
            foreach (var s in lane.Steps) { s.DropBefore = false; s.Ghosted = false; }
        }
    }

    public void CancelDrag()
    {
        ClearDropMark();
        _dragCmd = null;
        _dragStep = null;
        Dragging = false;
    }

    /// <summary>松手。落在有效位置才动配方，否则等于没拖过。</summary>
    public void EndDrag()
    {
        var cmd = _dragCmd;
        var step = _dragStep;
        var toCh = _dropCh;
        var at = _dropAt;
        CancelDrag();

        if (toCh is not { } ch || !Workspace.ChannelRecipes.TryGetValue(ch, out var target)) return;

        string keepId;
        if (cmd is not null)
        {
            Record(ch);
            var made = NewStep(cmd.Descriptor);
            target.Steps.Insert(Math.Clamp(at, 0, target.Steps.Count), made);
            keepId = made.StepId;
        }
        else if (step is not null)
        {
            if (!Workspace.ChannelRecipes.TryGetValue(_dragFromCh, out var source)) return;
            var from = source.Steps.IndexOf(step.Step);
            if (from < 0) return;

            // 同一条泳道里往后挪：先摘掉会让后面的下标整体前移一位
            if (ReferenceEquals(source, target) && at > from) at--;
            if (ReferenceEquals(source, target) && at == from) return;   // 原地没动

            Record(_dragFromCh);
            if (!ReferenceEquals(source, target)) Record(ch);   // 跨道要两边都能撤
            source.Steps.RemoveAt(from);
            target.Steps.Insert(Math.Clamp(at, 0, target.Steps.Count), step.Step);
            source.ModifiedAt = DateTimeOffset.Now;
            keepId = step.Step.StepId;
        }
        else return;

        target.ModifiedAt = DateTimeOffset.Now;
        Workspace.Store.MarkDirty();
        CurCh = ch;                     // 落到哪条道就切到哪条道，右栏跟着换
        RefreshAll();
        SelectedStep = Lanes.FirstOrDefault(l => l.Channel == ch)?
                            .Steps.FirstOrDefault(v => v.Step.StepId == keepId);
    }

    private static Step NewStep(CommandDescriptor d) => new()
    {
        CommandId = d.Id,
        Parameters = new ParameterSet().FillDefaults(d.Parameters),
        Rows = d.Parameters.Table is null ? null : DefaultRows(d)
    };

    /// <summary>一键复制（原型 copyRecipe）：深拷贝，各通道互不影响。</summary>
    private void DoCopy()
    {
        if (_copyTarget == _curCh || !Workspace.ChannelRecipes.ContainsKey(_copyTarget)) return;
        Record(_copyTarget);                       // 被覆盖的是目标通道，历史记在它头上
        var name = Workspace.LaneNames.TryGetValue(_curCh, out var n) ? n : Current.Name;
        var copy = Current.CopyAs(name, Current.Author);
        Workspace.ChannelRecipes[_copyTarget] = copy;
        Workspace.LaneNames[_copyTarget] = copy.Name;
        Workspace.Store.MarkDirty();
        RefreshAll();
        Tell($"已复制到 {LabelOf(_copyTarget)}（{copy.Steps.Count} 步）");
    }

    /// <summary>应用配方库（原型 applyLibRecipe）：替换当前通道全部步骤。</summary>
    private void DoApplyLib()
    {
        if (_libPick is null) return;
        // 台面上没有通道时不能应用：往 ChannelRecipes 里塞一条，界面会凭空多出
        // 一条根本不存在的泳道
        if (NoLanes) { Tell("台面上还没有通道，先去「台面」摆一台反应器。"); return; }

        Record();
        // CopyAs 而不是 Snapshot：泳道里这一份是独立的工作副本。沿用库里那条的 Id，
        // 应用到两个通道就成了两条同号配方，记录与导出都分不开
        var copy = _libPick.CopyAs(_libPick.Name, _libPick.Author);
        // 库里这条捎着配料表就一起落地——加料步骤的 chemId 指着那些行，
        // 只落步骤不落配料表，引用当场全断（charge-refgone 刷一排）
        var adopted = Workspace.AdoptCharge(_curCh, copy);
        Workspace.ChannelRecipes[_curCh] = copy;
        Workspace.LaneNames[_curCh] = copy.Name;
        SelectedStep = null;
        Workspace.Store.MarkDirty();
        RefreshAll();
        Tell($"已把「{copy.Name}」应用到 {LabelOf(_curCh)}（{copy.Steps.Count} 步）"
             + (adopted is null ? "" : "，" + adopted));
    }

    private void DoSaveToLib()
    {
        if (NoLanes) { Tell("台面上还没有通道，没有配方可存。"); return; }
        var name = Workspace.LaneNames.TryGetValue(_curCh, out var n) && n.Length > 0
            ? n : Current.Name;
        // CopyAs 而不是 Snapshot：库里这条是**新的一条**，得有自己的 Id 与时间戳。
        // 沿用原件的时间，库里「最近更新」会显示成它一直没动过；沿用 Id，
        // 存两次就有两条撞号的记录
        var copy = Current.CopyAs(name, Workspace.Operator);
        // 配料表捎上（模板化清洗：实投 / 实取 / 批号是跟这一炉走的，不进模板）。
        // 不捎的话，这条配方里加料步骤的 chemId 到了别的实验全是断引用
        var charge = Workspace.ChargeOf(_curCh);
        copy.Charge = charge.IsEmpty ? null : ChargeTemplate.Strip(charge);
        Workspace.Library.Add(copy);        // Library 是可观察集合，两个页面自己会跟上
        Workspace.Store.SaveLibrary();      // 存进库就该落盘，不然关掉程序白存
        LibPick = Library.FirstOrDefault(o => ReferenceEquals(o.Recipe, copy));
        Tell($"「{name}」已存入配方库（{copy.Steps.Count} 步"
             + (copy.Charge is null ? "）" : $"，附配料表 {copy.Charge.Items.Count} 组分）"));
    }

    // ── 工具条：新建 / 撤销 / 重做 ───────────────────────────────────

    /// <summary>
    /// 记一笔当前样子，供撤销回退。改动**之前**调。
    ///
    /// coalesceKey 把连续的同类编辑合成一笔：在温度框里连按几下上下箭头是一次编辑
    /// 意图，不该变成十次撤销。
    /// </summary>
    /// <summary>入栈一份**已经取好的**快照。改参数那条路用它——回调到手时原值已经被覆盖了。</summary>
    private void RecordSnapshot(int channel, Recipe before, string coalesceKey)
    {
        _history.Record(channel, before, coalesceKey);
        RaiseAll(nameof(CanUndo), nameof(CanRedo));
    }

    /// <summary>
    /// 别的页改这一路配方的入口：**先记一笔快照，改完再刷新**。
    ///
    /// 配料表页那颗「应用到加料步骤」改的是配方参数——撤销栈和校验面板都在这一页上，
    /// 所以「快照 → 改 → 刷新」整套由这一页包住。
    /// 只在改之前刷新是不够的：实测踩到过，配方页上那条校验还写着改之前的体积。
    /// </summary>
    public void EditExternally(int channel, string reason, Action edit)
    {
        Record(channel, reason);
        edit();
        RefreshAll();
    }

    private void Record(int? channel = null, string? coalesceKey = null)
    {
        var ch = channel ?? _curCh;
        if (!Workspace.ChannelRecipes.TryGetValue(ch, out var recipe)) return;
        _history.Record(ch, recipe, coalesceKey);
        RaiseAll(nameof(CanUndo), nameof(CanRedo));
    }

    /// <summary>新建：把当前通道清成一条空配方。名字保留——那是操作人起的。</summary>
    private void DoNew()
    {
        if (NoLanes) return;
        Record();
        var name = Workspace.LaneNames.TryGetValue(_curCh, out var n) ? n : "新配方";
        Workspace.ChannelRecipes[_curCh] = new Recipe { Name = name };
        SelectedStep = null;
        Workspace.Store.MarkDirty();
        RefreshAll();
    }

    private void DoUndo() => Step(_history.Undo(_curCh, Current));
    private void DoRedo() => Step(_history.Redo(_curCh, Current));

    private void Step(Recipe? to)
    {
        if (to is null || NoLanes) return;
        Workspace.ChannelRecipes[_curCh] = to;
        Workspace.LaneNames[_curCh] = to.Name;
        SelectedStep = null;
        Workspace.Store.MarkDirty();
        RefreshAll();
    }

    // ── 配方文件的导入 / 导出 ────────────────────────────────────────

    /// <summary>
    /// 导入一份 .tecrecipe 到当前通道。整份替换——「导入」不是「合并」，
    /// 合并出来的步骤顺序没人说得清。替换前记一笔，撤销能拉回来。
    /// </summary>
    private async Task DoImportAsync()
    {
        if (NoLanes) { Tell("台面上还没有通道，先去「台面」摆一台反应器。"); return; }
        if (await FileDialogs.OpenRecipe() is not { } path) return;
        try
        {
            var read = TecFiles.LoadRecipe(path).ToModel(out var migrated);
            // 同一份文件导进两个通道，得是两条各自独立的配方
            var recipe = read.CopyAs(read.Name, read.Author);
            Record();
            var adopted = Workspace.AdoptCharge(_curCh, recipe);
            Workspace.ChannelRecipes[_curCh] = recipe;
            Workspace.LaneNames[_curCh] = recipe.Name;
            SelectedStep = null;
            Workspace.Store.MarkDirty();
            RefreshAll();
            var note = migrated.Count > 0 ? $"，其中 {migrated.Count} 步用的是旧指令，已转换" : "";
            Tell($"已把「{recipe.Name}」导入 {LabelOf(_curCh)}（{recipe.Steps.Count} 步）{note}"
                 + (adopted is null ? "" : "，" + adopted));
        }
        catch (TecFileException ex) { Tell(ex.Message); }
        catch (Exception ex) { Tell("导入失败：" + ex.Message); }
    }

    private async Task DoExportAsync()
    {
        if (NoLanes) return;
        var name = Workspace.LaneNames.TryGetValue(_curCh, out var n) ? n : "配方";
        if (await FileDialogs.SaveRecipe(name) is not { } path) return;
        try
        {
            var copy = Current.Snapshot();
            copy.Name = name;
            var charge = Workspace.ChargeOf(_curCh);
            copy.Charge = charge.IsEmpty ? null : ChargeTemplate.Strip(charge);
            TecFiles.SaveRecipe(path, copy.ToDoc());
            Tell(copy.Charge is null ? $"配方已导出到 {path}"
                 : $"配方已导出到 {path}（附配料表 {copy.Charge.Items.Count} 组分）");
        }
        catch (Exception ex) { Tell("导出失败：" + ex.Message); }
    }

    /// <summary>点面板里的一条，跳到出问题的那一步。按 StepId 找，不按下标——
    /// 校验结果显示在屏上这段时间里人可能已经插了一步，下标就指歪了。
    /// 不是当前通道的先切过去：面板列的是四条泳道的事，点了哪条就该到哪条。</summary>
    private void SelectIssue(IssueRow row)
    {
        if (row.Issue.StepId is not { } id) return;
        CurCh = row.Channel;                            // 已经是当前通道的话，Set 会拦下，不白刷一遍
        var lane = Lanes.FirstOrDefault(l => l.Channel == row.Channel);
        var hit = lane?.Steps.FirstOrDefault(s => s.Step.StepId == id);
        if (hit is not null) SelectedStep = hit;
    }

    // ── 刷新 ─────────────────────────────────────────────────────────

    public void RefreshAll()
    {
        SyncLanes();
        SyncVars();
        foreach (var lane in Lanes)
        {
            lane.IsCurrent = lane.Channel == _curCh;
            lane.RefreshHint();
            lane.RefreshOptions();
            lane.Steps.Clear();
            if (!Workspace.ChannelRecipes.TryGetValue(lane.Channel, out var recipe)) continue;
            var plan = Schedule.Build(recipe, Workspace.Catalog);
            var depth = 0;
            for (var i = 0; i < recipe.Steps.Count; i++)
            {
                var step = recipe.Steps[i];
                Workspace.Catalog.TryGet(step.CommandId, out var d);

                // 循环结束行先退一层再画，它跟循环开始行才对得齐
                if (BuiltinCommands.IsLoopEnd(step.CommandId)) depth = Math.Max(0, depth - 1);
                lane.Steps.Add(new StepViewModel(i + 1, step, plan.Entries[i], d) { Depth = depth });
                if (BuiltinCommands.IsLoopBegin(step.CommandId)) depth++;
            }
        }

        CopyTargets.Clear();
        foreach (var ch in Workspace.ChannelRecipes.Keys.OrderBy(x => x))
            if (ch != _curCh) CopyTargets.Add(new CopyTargetOption(ch));
        if (CopyTargets.All(o => o.Channel != _copyTarget) && CopyTargets.Count > 0)
            _copyTarget = CopyTargets[0].Channel;
        Raise(nameof(CopyTarget));

        Issues.Clear();
        Problems.Clear();
        TotalNote = "";
        // 四条泳道一起校，不只校当前这条。四通道并排跑的时候，「哪一路不能跑」
        // 要在切过去之前就知道——按下运行才发现另一路挡着，前面排的队就白排了。
        // 每一行因此都记着自己是哪条通道的事（面板右侧那枚「机A · CH1」）。
        var rows = new List<IssueRow>();
        foreach (var lane in Lanes)
        {
            if (!Workspace.ChannelRecipes.TryGetValue(lane.Channel, out var recipe)) continue;
            var cur = lane.Channel == _curCh;
            // 配料表也一起校：加料步骤的料液名对不上配料表、或者体积跟算出来的不一致，
            // 都该在这里说，而不是等人切到配料表页才发现
            var charge = Workspace.ChannelCharges.TryGetValue(lane.Channel, out var t) && !t.IsEmpty
                ? Stoichiometry.Solve(t, Workspace.Compounds.ToList()) : null;
            var errs = 0;
            foreach (var i in RecipeValidator.Validate(recipe, Workspace.Catalog,
                                                       Workspace.ChannelOf(lane.Channel), charge: charge))
            {
                if (cur) Issues.Add(i);
                // 「预计总时长」不是问题，别混进问题清单吓人
                if (i.Code == "duration") { if (cur) TotalNote = i.Message; continue; }
                if (i.Level == IssueLevel.Info) continue;
                rows.Add(new IssueRow(i, lane.Channel, lane.ChannelLabel));
                if (i.Level != IssueLevel.Error) continue;
                errs++;
                // 红框钉到具体那一步上。按 StepId 找而不是下标：校验结果算出来到
                // 画到屏上这段时间里人可能已经插了一步，下标就指歪了
                if (i.StepId is not { } sid) continue;
                if (lane.Steps.FirstOrDefault(x => x.Step.StepId == sid) is not { } hit) continue;
                hit.ErrorTip = hit.ErrorTip is null ? i.Message : hit.ErrorTip + "\n" + i.Message;
            }
            lane.Errors = errs;
        }
        // 错误排在提醒前面：挡启动的那些先看。OrderBy 是稳定排序，
        // 同一级里仍按通道、按步骤的原顺序，不会每次刷新跳来跳去
        foreach (var r in rows.OrderBy(r => r.IsError ? 0 : 1)) Problems.Add(r);
        FillPanel();

        RaiseAll(nameof(CurName), nameof(ChannelStates), nameof(HasLanes), nameof(NoLanes), nameof(CurLane),
                 nameof(CanUndo), nameof(CanRedo), nameof(HasProblems),
                 nameof(ErrorCount), nameof(WarnCount), nameof(HasErrors), nameof(HasWarns),
                 nameof(ErrorText), nameof(WarnText), nameof(ScaleText),
                 nameof(TotalNote), nameof(VarsBadge), nameof(HasVarsBadge), nameof(NoVars),
                 // 切通道 / 换配方后连配料表那一段要跟着当前选中走——不 Raise 的话
                 // 上一个通道那条加料步骤的「已连乙醇」会一直挂在右栏（实测踩到）
                 nameof(IsDoseStep), nameof(ChargeRowNames), nameof(ChargeRowPick),
                 nameof(ChargeLinkNote), nameof(ChargeLinkColorHex));
    }

    // ── 变量栏（对照 GBG Process 视图左缘的 Variables 折叠栏） ────────

    public ObservableCollection<VarRowViewModel> VarRows { get; } = new();

    public RelayCommand ToggleVars { get; }
    public RelayCommand AddVar { get; }
    public RelayCommand RemoveVar { get; }

    private bool _varsOpen;
    public bool VarsOpen
    {
        get => _varsOpen;
        set { if (Set(ref _varsOpen, value)) Raise(nameof(VarsClosed)); }
    }
    public bool VarsClosed => !_varsOpen;

    /// <summary>收起时竖条上那个数字：有几个变量。0 个不摆，免得一排「0」。</summary>
    public string VarsBadge => Current.Variables.Count.ToString();
    public bool HasVarsBadge => Current.Variables.Count > 0;
    public bool NoVars => Current.Variables.Count == 0;

    private void DoAddVar()
    {
        if (NoLanes) return;
        Record();
        // 起个不重样的短名，v1 v2 v3……操作人多半会当场改掉
        var used = new HashSet<string>(Current.Variables.Select(v => v.Name), StringComparer.Ordinal);
        var i = 1;
        while (used.Contains($"v{i}")) i++;
        Current.Variables.Add(new RecipeVariable { Name = $"v{i}" });
        Current.ModifiedAt = DateTimeOffset.Now;
        Workspace.Store.MarkDirty();
        RefreshAll();
    }

    private void DoRemoveVar(VarRowViewModel row)
    {
        Record();
        Current.Variables.Remove(row.Model);
        Current.ModifiedAt = DateTimeOffset.Now;
        Workspace.Store.MarkDirty();
        RefreshAll();
    }

    /// <summary>
    /// 一行编辑（名字 / 初值 / 单位 / 说明）走这里：快照 → 改 → 落盘标记 → 重校验。
    /// coalesceKey 按行按字段合并——在初值框里连敲十个字符是一次编辑意图。
    /// </summary>
    internal void EditVar(RecipeVariable v, string field, Action apply)
    {
        Record(null, $"var/{v.Id}/{field}");
        apply();
        Current.ModifiedAt = DateTimeOffset.Now;
        Workspace.Store.MarkDirty();
        RefreshAll();
    }

    /// <summary>
    /// 行集合跟着当前配方走。**模型没换就不重建**：正在名字框里打字时
    /// 每敲一个键都会走 RefreshAll，这时把行拆了重摆，焦点就丢了。
    /// </summary>
    private void SyncVars()
    {
        var want = Current.Variables;
        var same = VarRows.Count == want.Count;
        if (same)
            for (var i = 0; i < want.Count; i++)
                if (!ReferenceEquals(VarRows[i].Model, want[i])) { same = false; break; }
        if (same) return;
        VarRows.Clear();
        foreach (var v in want) VarRows.Add(new VarRowViewModel(this, v));
    }

    /// <summary>
    /// 泳道跟着通道走：台面上拖进一台双通道反应器就多两条泳道，拖走就收回去。
    /// 台面空着的时候一条泳道都没有——这是对的，没有反应器就没有通道可配。
    /// </summary>
    private void SyncLanes()
    {
        var want = Workspace.ChannelRecipes.Keys.OrderBy(x => x).ToList();
        for (var i = Lanes.Count - 1; i >= 0; i--)
            if (!want.Contains(Lanes[i].Channel)) Lanes.RemoveAt(i);
        foreach (var ch in want)
        {
            if (Lanes.Any(l => l.Channel == ch)) continue;
            var at = Lanes.Count;
            for (var i = 0; i < Lanes.Count; i++)
                if (Lanes[i].Channel > ch) { at = i; break; }
            Lanes.Insert(at, new LaneViewModel(this, ch));
        }
        if (Lanes.Count > 0 && Lanes.All(l => l.Channel != _curCh)) _curCh = Lanes[0].Channel;
    }

    public bool HasLanes => Lanes.Count > 0;
    public bool NoLanes => Lanes.Count == 0;

    private void RebuildForm()
    {
        if (_selectedStep?.Descriptor is not { } d) { Form = null; return; }
        var step = _selectedStep.Step;
        var rows = step.Rows;
        if (d.Parameters.Table is not null && rows is null)
        {
            rows = new List<ParameterSet>();
            var idx = Current.Steps.IndexOf(step);
            var replaced = new Step
            {
                StepId = step.StepId, CommandId = step.CommandId, Parameters = step.Parameters,
                Rows = rows, Enabled = step.Enabled, Comment = step.Comment
            };
            if (idx >= 0) Current.Steps[idx] = replaced;
            step = replaced;
        }
        // 改参数的撤销粒度 = 一次「选中并编辑」。表单建起来时先留一份改之前的样子，
        // 第一次改动时才入栈；同一步接着改多少个字段都合并进这一笔——
        // 在温度框里连按几下上下箭头是一次编辑意图，不该变成十次撤销
        var before = Current.Snapshot();
        var key = $"{_curCh}/{step.StepId}";
        Form = new SchemaFormViewModel(d.Parameters, step.Parameters, rows,
                                       Workspace.ChannelOf(_curCh),
                                       () =>
                                       {
                                           RecordSnapshot(_curCh, before, key);
                                           Workspace.Store.MarkDirty();
                                           RefreshAll();
                                       },
                                       // 曲线从这一步开始时的温度起笔——排期已经把前面
                                       // 那些步骤走过一遍，那个温度就是这里的起点
                                       _selectedStep.Entry.StartTemp,
                                       // 手改加料体积 = 脱离配料表跟随（CH-4.2）。
                                       // 引用还在，校验面板会提示手填值与算出值差多少
                                       fieldEdited: k =>
                                       {
                                           if (k != ChargeLink.VolumeKey) return;
                                           if (d.RequiredCapability != typeof(IDosing)) return;
                                           if (ChargeLink.Detach(step))
                                               RaiseAll(nameof(ChargeLinkNote), nameof(ChargeLinkColorHex));
                                       },
                                       // 「设定变量」的变量下拉：选项 = 这条配方的变量表
                                       choicesOf: key => key == BuiltinCommands.ChoicesFromRecipeVars
                                           ? Current.Variables.Select(v => v.Name)
                                                 .Where(n => n.Trim().Length > 0).ToList()
                                           : null);
        RaiseAll(nameof(IsDoseStep), nameof(ChargeRowNames), nameof(ChargeRowPick),
                 nameof(ChargeLinkNote), nameof(ChargeLinkColorHex));
    }

    // ── 加料步骤连配料表（CH-4.1 引用链） ───────────────────────────

    /// <summary>选中的是一条加料步骤——右栏才摆「连配料表」这一段。</summary>
    public bool IsDoseStep => _selectedStep?.Descriptor is { } d
                              && d.RequiredCapability == typeof(IDosing)
                              && d.Parameters.Find(ChargeLink.LiquidKey) is not null;

    /// <summary>本通道配料表里可连的行（产物不投料，不在里面）。</summary>
    public IReadOnlyList<string> ChargeRowNames
        => Workspace.ChannelCharges.TryGetValue(_curCh, out var t)
            ? t.Items.Where(i => i.Role != ChargeRole.Product && i.Name.Trim().Length > 0)
                     .Select(i => i.Name).ToList()
            : Array.Empty<string>();

    /// <summary>
    /// 「连配料表」下拉。选一行 = 建立引用：写入行 Id + 跟随标志，名字对齐到行名，
    /// 算得出体积就把体积也填上。整套走快照，撤销拉得回来。
    /// </summary>
    public string? ChargeRowPick
    {
        get
        {
            if (_selectedStep?.Step is not { } step) return null;
            var chemId = step.Parameters.Str(ChargeLink.ChemKey);
            if (chemId.Length == 0) return null;
            return Workspace.ChannelCharges.TryGetValue(_curCh, out var t)
                ? t.Items.FirstOrDefault(i => i.Id == chemId)?.Name : null;
        }
        set
        {
            if (value is null || _selectedStep?.Step is not { } step || !IsDoseStep) return;
            if (!Workspace.ChannelCharges.TryGetValue(_curCh, out var table)) return;
            var row = table.Items.FirstOrDefault(i => i.Role != ChargeRole.Product && i.Name == value);
            if (row is null || step.Parameters.Str(ChargeLink.ChemKey) == row.Id) return;

            Record(_curCh, "charge-link");
            step.Parameters[ChargeLink.LiquidKey] = row.Name;
            step.Parameters[ChargeLink.ChemKey] = row.Id;
            step.Parameters[ChargeLink.LinkedKey] = true;
            var solved = Stoichiometry.Solve(table, Workspace.Compounds.ToList());
            if (solved.Lines.FirstOrDefault(l => l.Item.Id == row.Id)?.Volume is { } want && want > 0)
                step.Parameters[ChargeLink.VolumeKey] = Math.Round(want, 2);

            Workspace.Store.MarkDirty();
            RefreshAll();
            RebuildForm();          // 料液名与体积都可能变了，表单里的框要跟上
        }
    }

    /// <summary>连接状态一句话。绿 = 跟随中，琥珀 = 脱离 / 仅按名字，红 = 引用断了。</summary>
    public string ChargeLinkNote
    {
        get
        {
            if (!IsDoseStep || _selectedStep?.Step is not { } step) return "";
            var table = Workspace.ChannelCharges.TryGetValue(_curCh, out var t) ? t : null;
            if (table is null || table.IsEmpty)
                return "本通道还没有配料表。先去配料表页把组分建起来，这里才有可选的行。";

            var chemId = step.Parameters.Str(ChargeLink.ChemKey);
            if (chemId.Length > 0)
            {
                var row = table.Items.FirstOrDefault(i => i.Id == chemId);
                if (row is null) return "引用的配料行已不在（可能被删了）。重选一行即可重新连接。";
                return step.Parameters.Flag(ChargeLink.LinkedKey)
                    ? $"已连配料表「{row.Name}」：体积跟随计算，手改体积即脱离。"
                    : $"已连「{row.Name}」但已脱离计算：体积以手填为准。要恢复跟随，"
                      + "在配料表页按「应用到加料步骤」。";
            }

            var liq = Norm(step.Parameters.Str(ChargeLink.LiquidKey));
            var named = table.Items.FirstOrDefault(i =>
                i.Role != ChargeRole.Product && Norm(i.Name).Equals(liq, StringComparison.OrdinalIgnoreCase));
            return named is not null
                ? $"按名字「{named.Name}」对上配料表，但没建正式引用——改名就断。选一行即可连接。"
                : "未连配料表。从下拉选一行，加料体积就会引用配料表的计算。";
        }
    }

    public string ChargeLinkColorHex
    {
        get
        {
            if (!IsDoseStep || _selectedStep?.Step is not { } step) return "#8d8d8d";
            var table = Workspace.ChannelCharges.TryGetValue(_curCh, out var t) ? t : null;
            if (table is null || table.IsEmpty) return "#8d8d8d";
            var chemId = step.Parameters.Str(ChargeLink.ChemKey);
            if (chemId.Length == 0) return "#8d8d8d";
            if (table.Items.All(i => i.Id != chemId)) return "#c0392b";
            return step.Parameters.Flag(ChargeLink.LinkedKey) ? "#2f8f49" : "#a8710a";
        }
    }

    private static string Norm(string s) => s.Replace("　", " ").Trim();

    // ── 右栏通道状态（原型 chStateList）─────────────────────────────

    /// <summary>
    /// 右栏那张通道状态表。按台面上**全部**孔位列，不是只列有配方的那几个——
    /// 「CH4 没建标签」本身就是要看的信息，漏掉它等于告诉操作人这台机器只有三个通道。
    /// </summary>
    public IReadOnlyList<ChannelStateRow> ChannelStates
        => Workspace.Channels.OrderBy(c => c.Number).Select(c =>
        {
            var hasLane = Workspace.ChannelRecipes.ContainsKey(c.Number);
            var steps = hasLane ? Workspace.ChannelRecipes[c.Number].Steps.Count : 0;
            return new ChannelStateRow(
                c.Number,
                hasLane && Workspace.LaneNames.TryGetValue(c.Number, out var n) ? n : "—",
                LabelOf(c.Number),
                steps, c.Enabled, hasLane);
        }).ToList();
}

public sealed record ChannelStateRow(
    int Channel, string Name, string ChannelLabel, int StepCount, bool Enabled, bool HasLane)
{
    public string ColorHex => HasLane
        ? Channel switch { 1 => "#2f7ed8", 2 => "#2aa87a", 3 => "#c9772b", _ => "#8a63d2" }
        : "#c2c2c2";

    /// <summary>接在名称后面的灰字，比如「 · 机A · CH1」。</summary>
    public string ChannelSuffix => $" · {ChannelLabel}";

    /// <summary>亮灯 = 通道启用**且**建了泳道。两者缺一，这条通道这轮就不会跑。</summary>
    public bool Lit => Enabled && HasLane;

    public string State => !Enabled ? "已停用"
        : !HasLane ? "未建标签"
        : StepCount > 0 ? $"{StepCount} 步" : "空配方";
}

/// <summary>
/// 校验面板里的一行：一个圆点 + 哪儿不对 + 怎么办 + 是哪条通道的事。
///
/// 校验器一直在跑，结果以前直接扔了——「跑到一半才报错」正是它要避免的事，
/// 不显示等于白跑。
///
/// 「怎么办」那句来自 IssueHints（按 Code 查），跟问题本身分开写——
/// 上面那句一眼扫过，下面那句只有真要动手的人才读。
/// </summary>
public sealed record IssueRow(ValidationIssue Issue, int Channel, string ChannelLabel)
{
    public bool IsError => Issue.Level == IssueLevel.Error;
    public string Text => Issue.Message;
    public string Dot => IsError ? "#c62828" : "#dba32c";
    public string? Hint => IssueHints.Of(Issue.Code);
    public bool HasHint => Hint is not null;
    /// <summary>能定位到具体某一步的才可点。</summary>
    public bool CanGo => Issue.StepId is not null;
}

/// <summary>
/// 「应用配方库」下拉的一项。带上步数——同名配方改过几版之后，
/// 步数是下拉里唯一能一眼分辨的东西。
/// </summary>
public sealed record LibOption(Recipe Recipe)
{
    public string Label => $"{Recipe.Name}（{Recipe.Steps.Count} 步）";
}

/// <summary>「复制到」下拉的一项。用具名类型而不是裸 int：选项一度会是空的，
/// ComboBox 这时会把 SelectedItem 置回 null，绑到 int 上就抛类型转换异常。</summary>
public sealed record CopyTargetOption(int Channel)
{
    public string Label => $"通道 {Channel}";
}

/// <summary>
/// 变量栏里的一行。写回模型走 owner.EditVar——快照、落盘、重校验一处包办；
/// 属性自己只负责「值没变就什么都不做」，不然打字光标会被无谓的刷新打断。
/// </summary>
public sealed class VarRowViewModel : ViewModelBase
{
    private readonly RecipeViewModel _owner;

    public VarRowViewModel(RecipeViewModel owner, RecipeVariable model)
    {
        _owner = owner;
        Model = model;
    }

    public RecipeVariable Model { get; }

    public string Name
    {
        get => Model.Name;
        set
        {
            var v = value.Trim();
            if (Model.Name == v) return;
            _owner.EditVar(Model, "name", () => Model.Name = v);
            Raise();
        }
    }

    public string InitText
    {
        get => Model.Init.ToString("0.####", CultureInfo.InvariantCulture);
        set
        {
            if (!double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)) return;
            if (Math.Abs(Model.Init - v) < 1e-12) return;
            _owner.EditVar(Model, "init", () => Model.Init = v);
            Raise();
        }
    }

    public string Unit
    {
        get => Model.Unit;
        set
        {
            var v = value.Trim();
            if (Model.Unit == v) return;
            _owner.EditVar(Model, "unit", () => Model.Unit = v);
            Raise();
        }
    }

    public string NoteText
    {
        get => Model.Note ?? "";
        set
        {
            var v = value.Trim();
            if ((Model.Note ?? "") == v) return;
            _owner.EditVar(Model, "note", () => Model.Note = v.Length == 0 ? null : v);
            Raise();
        }
    }
}
