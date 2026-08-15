using System.Collections.ObjectModel;
using Tec.App.Services;
using Tec.Core;
using Tec.Core.Benches;
using Tec.Core.Catalog;
using Tec.Core.Recipes;
using Tec.Core.Scheduling;
using Tec.Driver.Abi;

namespace Tec.App.ViewModels;

public sealed class CommandItemViewModel
{
    public CommandItemViewModel(CommandDescriptor d) => Descriptor = d;
    public CommandDescriptor Descriptor { get; }
    public string Name => Descriptor.DisplayName;
    public string Module => Descriptor.Module;
    public string Termination => TerminationText.Of(Descriptor.Termination);
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

    public string Title => Entry.Title;
    public string Module => Descriptor?.Module ?? "—";
    public string Termination => TerminationText.Of(Entry.Termination);
    public string PlanStart => Fmt.Hms(Entry.Start);
    public string PlanDuration => Entry.Extent > TimeSpan.Zero ? Fmt.Hms(Entry.Extent) : "—";
    public bool IsMissing => !Entry.Known;
    public bool IsLoop => Entry.Repeats > 0;
    public string LoopNote => Entry.Repeats > 0 ? $"× {Entry.Repeats}" : "";
}

/// <summary>
/// 配方编辑。左边是指令库（按模块分组），中间是步骤表，右边是按 schema 生成的参数表单。
/// 配方绑能力不绑设备——通道选择只影响校验与动态限值，不写进配方（§6）。
/// </summary>
public sealed class RecipeViewModel : ViewModelBase
{
    private readonly Workspace _ws;
    private Recipe _recipe;
    private StepViewModel? _selectedStep;
    private CommandItemViewModel? _selectedCommand;
    private Channel? _targetChannel;
    private SchemaFormViewModel? _form;

    public RecipeViewModel(Workspace ws)
    {
        _ws = ws;
        _recipe = ws.Library.Count > 0 ? ws.Library[0] : new Recipe();

        foreach (var m in ws.Catalog.Modules)
        {
            var group = new ModuleGroup(m);
            foreach (var c in ws.Catalog.InModule(m)) group.Commands.Add(new CommandItemViewModel(c));
            Modules.Add(group);
        }

        foreach (var r in ws.Library) Library.Add(r);
        foreach (var ch in ws.Channels) TargetChannels.Add(ch);
        _targetChannel = ws.Channels.FirstOrDefault();

        AddCommand = new RelayCommand(() => AddSelectedCommand());
        RemoveStep = new RelayCommand(() => RemoveSelectedStep());
        MoveUp = new RelayCommand(() => Move(-1));
        MoveDown = new RelayCommand(() => Move(1));
        DuplicateStep = new RelayCommand(() => Duplicate());
        Validate = new RelayCommand(() => RunValidation());

        Refresh();
    }

    public ObservableCollection<ModuleGroup> Modules { get; } = new();
    public ObservableCollection<StepViewModel> Steps { get; } = new();
    public ObservableCollection<Recipe> Library { get; } = new();
    public ObservableCollection<Channel> TargetChannels { get; } = new();
    public ObservableCollection<ValidationIssue> Issues { get; } = new();

    public RelayCommand AddCommand { get; }
    public RelayCommand RemoveStep { get; }
    public RelayCommand MoveUp { get; }
    public RelayCommand MoveDown { get; }
    public RelayCommand DuplicateStep { get; }
    public RelayCommand Validate { get; }

    public Recipe Recipe
    {
        get => _recipe;
        set { if (Set(ref _recipe, value)) { SelectedStep = null; Refresh(); } }
    }

    public CommandItemViewModel? SelectedCommand
    {
        get => _selectedCommand;
        set => Set(ref _selectedCommand, value);
    }

    public Channel? TargetChannel
    {
        get => _targetChannel;
        set { if (Set(ref _targetChannel, value)) { RebuildForm(); RunValidation(); } }
    }

    public StepViewModel? SelectedStep
    {
        get => _selectedStep;
        set
        {
            if (!Set(ref _selectedStep, value)) return;
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
    public string SelectedTitle => _selectedStep?.Descriptor?.DisplayName ?? "未选中步骤";
    public string? SelectedTip => _selectedStep?.Descriptor?.Tip;

    // 属性刻意不叫 Schedule：那样 Schedule.Build 会被解析成实例成员而编译不过
    public Schedule Plan { get; private set; } = Schedule.Empty;
    public string TotalText => $"预计总时长 {Fmt.Hms(Plan.Total)} · {Steps.Count} 步";
    public string MissingText => Plan.MissingCommands.Count == 0
        ? ""
        : $"缺少驱动：{string.Join("、", Plan.MissingCommands)}";
    public bool HasMissing => Plan.MissingCommands.Count > 0;

    public void Refresh()
    {
        Plan = Schedule.Build(_recipe, _ws.Catalog, Seed());
        var keepId = _selectedStep?.Step.StepId;
        Steps.Clear();
        for (var i = 0; i < _recipe.Steps.Count; i++)
        {
            var s = _recipe.Steps[i];
            _ws.Catalog.TryGet(s.CommandId, out var d);
            Steps.Add(new StepViewModel(i + 1, s, Plan.Entries[i], d));
        }
        if (keepId is not null)
            _selectedStep = Steps.FirstOrDefault(v => v.Step.StepId == keepId);
        RaiseAll(nameof(TotalText), nameof(MissingText), nameof(HasMissing), nameof(SelectedStep));
        RunValidation();
    }

    private EstimationContext Seed()
    {
        var ctx = new EstimationContext();
        if (_targetChannel?.Capabilities.Get<ITemperatureControl>() is { } t)
            ctx.Temperature = t.CurrentReactor;
        return ctx;
    }

    private void RebuildForm()
    {
        if (_selectedStep?.Descriptor is not { } d) { Form = null; return; }
        var step = _selectedStep.Step;
        var rows = step.Rows;
        if (d.Parameters.Table is not null && rows is null)
        {
            rows = new List<ParameterSet>();
            var replaced = new Step
            {
                StepId = step.StepId,
                CommandId = step.CommandId,
                Parameters = step.Parameters,
                Rows = rows,
                Enabled = step.Enabled,
                Comment = step.Comment
            };
            var idx = _recipe.Steps.IndexOf(step);
            if (idx >= 0) _recipe.Steps[idx] = replaced;
            step = replaced;
        }
        Form = new SchemaFormViewModel(d.Parameters, step.Parameters, rows, _targetChannel, Refresh);
    }

    private void AddSelectedCommand()
    {
        if (_selectedCommand is null) return;
        var d = _selectedCommand.Descriptor;
        var p = new ParameterSet().FillDefaults(d.Parameters);
        var step = new Step
        {
            CommandId = d.Id,
            Parameters = p,
            Rows = d.Parameters.Table is null ? null : new List<ParameterSet>()
        };
        var at = _selectedStep is null ? _recipe.Steps.Count : _recipe.Steps.IndexOf(_selectedStep.Step) + 1;
        _recipe.Steps.Insert(Math.Clamp(at, 0, _recipe.Steps.Count), step);
        _recipe.ModifiedAt = DateTimeOffset.Now;
        Refresh();
        SelectedStep = Steps.FirstOrDefault(v => v.Step.StepId == step.StepId);
    }

    private void RemoveSelectedStep()
    {
        if (_selectedStep is null) return;
        _recipe.Steps.Remove(_selectedStep.Step);
        _selectedStep = null;
        Refresh();
        SelectedStep = null;
    }

    private void Duplicate()
    {
        if (_selectedStep is null) return;
        var idx = _recipe.Steps.IndexOf(_selectedStep.Step);
        if (idx < 0) return;
        _recipe.Steps.Insert(idx + 1, _selectedStep.Step.Clone());
        Refresh();
    }

    private void Move(int delta)
    {
        if (_selectedStep is null) return;
        var idx = _recipe.Steps.IndexOf(_selectedStep.Step);
        var to = idx + delta;
        if (idx < 0 || to < 0 || to >= _recipe.Steps.Count) return;
        var s = _recipe.Steps[idx];
        _recipe.Steps.RemoveAt(idx);
        _recipe.Steps.Insert(to, s);
        Refresh();
        SelectedStep = Steps.FirstOrDefault(v => v.Step.StepId == s.StepId);
    }

    private void RunValidation()
    {
        Issues.Clear();
        foreach (var i in RecipeValidator.Validate(_recipe, _ws.Catalog, _targetChannel, Seed()))
            Issues.Add(i);
        Raise(nameof(ErrorCount));
        Raise(nameof(IssueSummary));
    }

    public int ErrorCount => Issues.Count(i => i.Level == IssueLevel.Error);
    public string IssueSummary
    {
        get
        {
            var e = Issues.Count(i => i.Level == IssueLevel.Error);
            var w = Issues.Count(i => i.Level == IssueLevel.Warning);
            return e == 0 && w == 0 ? "校验通过" : $"{e} 项错误 · {w} 项提醒";
        }
    }
}

public sealed class ModuleGroup
{
    public ModuleGroup(string name) => Name = name;
    public string Name { get; }
    public ObservableCollection<CommandItemViewModel> Commands { get; } = new();
}
