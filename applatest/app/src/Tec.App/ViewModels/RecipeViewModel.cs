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

        foreach (var ch in ws.ChannelRecipes.Keys.OrderBy(x => x))
            Lanes.Add(new LaneViewModel(this, ch));

        AddStep = new RelayCommand(p => { if (p is CommandItemViewModel c) AddCommand(c); });
        RemoveStep = new RelayCommand(p => { if (p is StepViewModel s) Delete(s); });
        CopyRecipe = new RelayCommand(DoCopy);
        ApplyLib = new RelayCommand(DoApplyLib);
        SaveToLib = new RelayCommand(DoSaveToLib);

        RefreshAll();
    }

    public Workspace Workspace { get; }
    public ObservableCollection<ModuleGroup> Groups { get; } = new();
    public ObservableCollection<LaneViewModel> Lanes { get; } = new();
    public ObservableCollection<Recipe> Library { get; } = new();
    public ObservableCollection<int> CopyTargets { get; } = new();
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

    public int CopyTarget
    {
        get => _copyTarget;
        set => Set(ref _copyTarget, value);
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

    public Recipe Current => Workspace.ChannelRecipes.TryGetValue(_curCh, out var r)
        ? r
        : Workspace.ChannelRecipes[_curCh] = new Recipe { Name = "新配方" };

    // ── 中列操作 ─────────────────────────────────────────────────────

    private void AddCommand(CommandItemViewModel c)
    {
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
        RefreshAll();
        SelectedStep = Lanes.First(l => l.Channel == _curCh).Steps.FirstOrDefault(v => v.Step.StepId == step.StepId);
    }

    /// <summary>梯度控温 / 分段加料的默认分段，与原型 PSPEC.rows 一致。</summary>
    private static List<ParameterSet> DefaultRows(CommandDescriptor d) => d.Id switch
    {
        CommandSpecs.Gradient => new List<ParameterSet>
        {
            ParameterSet.Of(("t", 60d), ("r", 1d), ("h", 10d)),
            ParameterSet.Of(("t", 30d), ("r", 0.3d), ("h", 20d)),
            ParameterSet.Of(("t", 5d), ("r", 0.1d), ("h", 30d))
        },
        CommandSpecs.DoseSegments => new List<ParameterSet>
        {
            ParameterSet.Of(("v", 5d), ("r", 1d), ("w", 5d)),
            ParameterSet.Of(("v", 5d), ("r", 0.5d), ("w", 10d)),
            ParameterSet.Of(("v", 5d), ("r", 0.2d), ("w", 15d))
        },
        _ => new List<ParameterSet>()
    };

    private void Delete(StepViewModel s)
    {
        Current.Steps.Remove(s.Step);
        if (_selectedStep == s) SelectedStep = null;
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
        LibPick = copy;
    }

    // ── 刷新 ─────────────────────────────────────────────────────────

    public void RefreshAll()
    {
        foreach (var lane in Lanes)
        {
            lane.IsCurrent = lane.Channel == _curCh;
            lane.RefreshHint();
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
            if (ch != _curCh) CopyTargets.Add(ch);
        if (!CopyTargets.Contains(_copyTarget) && CopyTargets.Count > 0)
            CopyTarget = CopyTargets[0];

        Issues.Clear();
        foreach (var i in RecipeValidator.Validate(Current, Workspace.Catalog, Workspace.ChannelOf(_curCh)))
            Issues.Add(i);

        RaiseAll(nameof(CurName), nameof(ChannelStates));
    }

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
                                       Workspace.ChannelOf(_curCh), RefreshAll);
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
