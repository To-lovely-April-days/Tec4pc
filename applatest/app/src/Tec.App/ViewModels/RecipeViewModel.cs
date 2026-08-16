using System.Collections.ObjectModel;
using Tec.App.Services;
using Tec.Core;
using Tec.Core.Benches;
using Tec.Core.Catalog;
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

    public string DropHint => IsCurrent ? "点击左侧步骤库添加到此通道" : "点选此列后可编辑";

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

    public RecipeViewModel(Workspace ws)
    {
        Workspace = ws;

        foreach (var m in ws.Catalog.Modules)
        {
            var group = new ModuleGroup(m);
            foreach (var c in ws.Catalog.InModule(m)) group.Commands.Add(new CommandItemViewModel(c));
            Groups.Add(group);
        }

        foreach (var r in ws.Library) Library.Add(r);
        _libPick = Library.FirstOrDefault();

        AddStep = new RelayCommand(p => { if (p is CommandItemViewModel c) AddCommand(c); });
        RemoveStep = new RelayCommand(p => { if (p is StepViewModel s) Delete(s); });
        CopyRecipe = new RelayCommand(DoCopy);
        ApplyLib = new RelayCommand(DoApplyLib);
        SaveToLib = new RelayCommand(DoSaveToLib);

        // 台面变了通道就变了，泳道的通道下拉要跟着刷新
        ws.BenchChanged += (_, _) => RefreshAll();
        // 打开别的实验会整份换掉配方库，右栏那个下拉得跟着换
        ws.Store.Changed += (_, _) => ReloadLibrary();

        RefreshAll();
    }

    public Workspace Workspace { get; }

    private void ReloadLibrary()
    {
        var keepId = _libPick?.Id;
        Library.Clear();
        foreach (var r in Workspace.Library) Library.Add(r);
        LibPick = Library.FirstOrDefault(r => r.Id == keepId) ?? Library.FirstOrDefault();
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
        Workspace.ChannelRecipes[channel] = new Recipe { Name = "新配方" };
        Workspace.LaneNames[channel] = "新配方";
        Workspace.Store.MarkDirty();
        RefreshAll();
    }

    public ObservableCollection<ModuleGroup> Groups { get; } = new();
    public ObservableCollection<LaneViewModel> Lanes { get; } = new();
    public ObservableCollection<Recipe> Library { get; } = new();
    public ObservableCollection<CopyTargetOption> CopyTargets { get; } = new();
    public ObservableCollection<ValidationIssue> Issues { get; } = new();

    public RelayCommand AddStep { get; }
    public RelayCommand RemoveStep { get; }
    public RelayCommand CopyRecipe { get; }
    public RelayCommand ApplyLib { get; }
    public RelayCommand SaveToLib { get; }

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

    public Recipe? LibPick
    {
        get => _libPick;
        set => Set(ref _libPick, value);
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
            RaiseAll(nameof(HasSelection), nameof(SelectedTitle), nameof(SelectedTip));
        }
    }

    public SchemaFormViewModel? Form
    {
        get => _form;
        private set => Set(ref _form, value);
    }

    public bool HasSelection => _selectedStep is not null;
    public string SelectedTitle => _selectedStep?.Name ?? "未选中步骤";
    public string? SelectedTip => _selectedStep?.Descriptor?.Tip;

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
        var d = c.Descriptor;
        var step = new Step
        {
            CommandId = d.Id,
            Parameters = new ParameterSet().FillDefaults(d.Parameters),
            Rows = d.Parameters.Table is null ? null : DefaultRows(d)
        };
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
        Current.Steps.Remove(s.Step);
        if (_selectedStep == s) SelectedStep = null;
        Workspace.Store.MarkDirty();
        RefreshAll();
    }

    /// <summary>一键复制（原型 copyRecipe）：深拷贝，各通道互不影响。</summary>
    private void DoCopy()
    {
        if (_copyTarget == _curCh || !Workspace.ChannelRecipes.ContainsKey(_copyTarget)) return;
        var copy = Current.Snapshot();
        copy.Name = Workspace.LaneNames.TryGetValue(_curCh, out var n) ? n : copy.Name;
        Workspace.ChannelRecipes[_copyTarget] = copy;
        Workspace.LaneNames[_copyTarget] = copy.Name;
        Workspace.Store.MarkDirty();
        RefreshAll();
    }

    /// <summary>应用配方库（原型 applyLibRecipe）：替换当前通道全部步骤。</summary>
    private void DoApplyLib()
    {
        if (_libPick is null) return;
        var copy = _libPick.Snapshot();
        Workspace.ChannelRecipes[_curCh] = copy;
        Workspace.LaneNames[_curCh] = copy.Name;
        SelectedStep = null;
        RefreshAll();
    }

    private void DoSaveToLib()
    {
        var copy = Current.Snapshot();
        copy.Name = Workspace.LaneNames.TryGetValue(_curCh, out var n) ? n : copy.Name;
        Workspace.Library.Add(copy);
        Library.Add(copy);
        Workspace.Store.SaveLibrary();     // 存进库就该落盘，不然关掉程序白存
        LibPick = copy;
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
            for (var i = 0; i < recipe.Steps.Count; i++)
            {
                Workspace.Catalog.TryGet(recipe.Steps[i].CommandId, out var d);
                lane.Steps.Add(new StepViewModel(i + 1, recipe.Steps[i], plan.Entries[i], d));
            }
        }

        CopyTargets.Clear();
        foreach (var ch in Workspace.ChannelRecipes.Keys.OrderBy(x => x))
            if (ch != _curCh) CopyTargets.Add(new CopyTargetOption(ch));
        if (CopyTargets.All(o => o.Channel != _copyTarget) && CopyTargets.Count > 0)
            _copyTarget = CopyTargets[0].Channel;
        Raise(nameof(CopyTarget));

        Issues.Clear();
        foreach (var i in RecipeValidator.Validate(Current, Workspace.Catalog, Workspace.ChannelOf(_curCh)))
            Issues.Add(i);

        RaiseAll(nameof(CurName), nameof(ChannelStates), nameof(HasLanes), nameof(NoLanes), nameof(CurLane));
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
        Form = new SchemaFormViewModel(d.Parameters, step.Parameters, rows,
                                       Workspace.ChannelOf(_curCh),
                                       () => { Workspace.Store.MarkDirty(); RefreshAll(); });
    }

    // ── 右栏通道状态（原型 chStateList）─────────────────────────────

    public IReadOnlyList<ChannelStateRow> ChannelStates
        => Workspace.ChannelRecipes.Keys.OrderBy(x => x).Select(ch => new ChannelStateRow(
            ch,
            Workspace.LaneNames.TryGetValue(ch, out var n) ? n : "新配方",
            Workspace.ChannelRecipes[ch].Steps.Count,
            Workspace.ChannelOf(ch)?.Enabled ?? false)).ToList();
}

public sealed record ChannelStateRow(int Channel, string Name, int StepCount, bool Enabled)
{
    public string ColorHex => Channel switch
    {
        1 => "#2f7ed8", 2 => "#2aa87a", 3 => "#c9772b", _ => "#8a63d2"
    };
    public string Label => $"CH{Channel} · {Name}";
    public string State => StepCount == 0 ? "空" : $"{StepCount} 步";
}

/// <summary>「复制到」下拉的一项。用具名类型而不是裸 int：选项一度会是空的，
/// ComboBox 这时会把 SelectedItem 置回 null，绑到 int 上就抛类型转换异常。</summary>
public sealed record CopyTargetOption(int Channel)
{
    public string Label => $"通道 {Channel}";
}
