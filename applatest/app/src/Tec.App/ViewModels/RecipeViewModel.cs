using System.Collections.ObjectModel;
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
    /// <summary>通用组没有 grpbar，直接平铺（原型 misc 不带分组条）。</summary>
    public bool HasBar => Name != "通用";

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

    /// <summary>卡片宽度让出缩进，泳道整体还是 270，四条泳道对得齐。</summary>
    public double CardWidth => 270 - Depth * 14;

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
        Undo = new RelayCommand(DoUndo);
        Redo = new RelayCommand(DoRedo);

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

    /// <summary>「2 个错误 · 1 个提醒」。错误一个都不能剩，提醒可以自己判断。</summary>
    public string ProblemSummary
    {
        get
        {
            var err = Problems.Count(p => p.IsError);
            var warn = Problems.Count - err;
            if (err > 0 && warn > 0) return $"{err} 个错误 · {warn} 个提醒";
            return err > 0 ? $"{err} 个错误" : $"{warn} 个提醒";
        }
    }

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
            RaiseAll(nameof(HasSelection), nameof(NoSelection), nameof(StepName), nameof(StepColor),
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

    // prop-head：模块色块 + 指令名 + 它属于哪个通道
    public string StepName => _selectedStep?.Name ?? "";
    public string StepColor => _selectedStep?.ModuleColor ?? "#9aa4ab";
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
        Workspace.ChannelRecipes[_curCh] = copy;
        Workspace.LaneNames[_curCh] = copy.Name;
        SelectedStep = null;
        Workspace.Store.MarkDirty();
        RefreshAll();
        Tell($"已把「{copy.Name}」应用到 {LabelOf(_curCh)}（{copy.Steps.Count} 步）");
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
        Workspace.Library.Add(copy);        // Library 是可观察集合，两个页面自己会跟上
        Workspace.Store.SaveLibrary();      // 存进库就该落盘，不然关掉程序白存
        LibPick = Library.FirstOrDefault(o => ReferenceEquals(o.Recipe, copy));
        Tell($"「{name}」已存入配方库（{copy.Steps.Count} 步）");
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
    /// 配料表页那颗「应用到加料步骤」改的是配方参数——撤销栈和校验条都在这一页上，
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
            Workspace.ChannelRecipes[_curCh] = recipe;
            Workspace.LaneNames[_curCh] = recipe.Name;
            SelectedStep = null;
            Workspace.Store.MarkDirty();
            RefreshAll();
            var note = migrated.Count > 0 ? $"，其中 {migrated.Count} 步用的是旧指令，已转换" : "";
            Tell($"已把「{recipe.Name}」导入 {LabelOf(_curCh)}（{recipe.Steps.Count} 步）{note}");
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
            TecFiles.SaveRecipe(path, copy.ToDoc());
            Tell($"配方已导出到 {path}");
        }
        catch (Exception ex) { Tell("导出失败：" + ex.Message); }
    }

    /// <summary>点校验条里的一条，跳到出问题的那一步。按 StepId 找，不按下标——
    /// 校验结果显示在屏上这段时间里人可能已经插了一步，下标就指歪了。</summary>
    private void SelectIssue(IssueRow row)
    {
        if (row.Issue.StepId is not { } id) return;
        var lane = Lanes.FirstOrDefault(l => l.Channel == _curCh);
        var hit = lane?.Steps.FirstOrDefault(s => s.Step.StepId == id);
        if (hit is not null) SelectedStep = hit;
    }

    // ── 刷新 ─────────────────────────────────────────────────────────

    public void RefreshAll()
    {
        SyncLanes();
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
        // 配料表也一起校：加料步骤的料液名对不上配料表、或者体积跟算出来的不一致，
        // 都该在这条校验条上说，而不是等人切到配料表页才发现
        var charge = Workspace.ChannelCharges.TryGetValue(_curCh, out var t) && !t.IsEmpty
            ? Stoichiometry.Solve(t, Workspace.Compounds.ToList()) : null;
        foreach (var i in RecipeValidator.Validate(Current, Workspace.Catalog,
                                                   Workspace.ChannelOf(_curCh), charge: charge))
        {
            Issues.Add(i);
            // 「预计总时长」不是问题，别混进问题清单吓人
            if (i.Code == "duration") { TotalNote = i.Message; continue; }
            if (i.Level == IssueLevel.Info) continue;
            Problems.Add(new IssueRow(i));
        }

        RaiseAll(nameof(CurName), nameof(ChannelStates), nameof(HasLanes), nameof(NoLanes), nameof(CurLane),
                 nameof(CanUndo), nameof(CanRedo), nameof(HasProblems), nameof(ProblemSummary),
                 nameof(TotalNote));
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
                                       _selectedStep.Entry.StartTemp);
    }

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
/// 校验条里的一行。校验器一直在跑，结果以前直接扔了——
/// 「跑到一半才报错」正是它要避免的事，不显示等于白跑。
/// </summary>
public sealed record IssueRow(ValidationIssue Issue)
{
    public bool IsError => Issue.Level == IssueLevel.Error;
    public string Text => Issue.Message;
    public string Dot => IsError ? "#c62828" : "#c98a00";
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
