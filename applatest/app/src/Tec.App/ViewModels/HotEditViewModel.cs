using System.Collections.ObjectModel;
using Tec.App.Services;
using Tec.Core.Catalog;
using Tec.Core.Execution;
using Tec.Core.Recipes;
using Tec.Core.Records;
using Tec.Driver.Abi;

namespace Tec.App.ViewModels;

/// <summary>
/// 运行中可以挑的一步。
///
/// 「改了什么时候生效」必须写在每一行上，不能让人自己推：同一个改动落在
/// 当前步、后面某一步、循环体里已经跑过的一步上，结果完全不同——
/// 而最坏的一种是**什么也不会发生**（这一步已经跑完、配方里又没有循环），
/// 那种情况下人会以为改成了，一直等一个不会来的效果。
/// </summary>
public sealed class HotStepViewModel : ViewModelBase
{
    private bool _sel;

    public required int Index { get; init; }
    public required string StepId { get; init; }
    public required string CommandId { get; init; }
    public required string Title { get; init; }
    public required string Module { get; init; }
    /// <summary>正在跑的就是它。</summary>
    public required bool IsCurrent { get; init; }
    /// <summary>已经跑过（循环体里的可能还会再跑）。</summary>
    public required bool IsPast { get; init; }
    /// <summary>指令自己声明的：这条能不能在执行中途改（<c>SupportsHotEdit</c>）。</summary>
    public required bool HotEditable { get; init; }
    /// <summary>改了什么时候生效，一句话。</summary>
    public required string Effect { get; init; }
    /// <summary>改不了的原因；能改就是 null。</summary>
    public string? Blocked { get; init; }

    public bool CanEdit => Blocked is null;
    public string Seq => (Index + 1).ToString();
    public string Badge => IsCurrent ? "当前" : IsPast ? "已跑" : "";
    public bool HasBadge => Badge.Length > 0;
    public string BadgeFillHex => IsCurrent ? "#eaf4ec" : "#f0f0f0";
    public string BadgeInkHex => IsCurrent ? "#2f8f49" : "#8d8d8d";
    public string ColorHex => ModuleInfo.ColorOf(Module);

    public bool IsSelected { get => _sel; set => Set(ref _sel, value); }
}

/// <summary>
/// 运行中改参数（FR-5.5 / §7.6）。引擎那一套提案 → 校验 → 应用 → 审计
/// （<see cref="ChannelRunner.ProposeEdit"/>）早就有了，这里是它的入口。
///
/// 三条摆在明面上的规矩：
/// · **基线不动**。启动那一刻冻下来的配方与排期永远是那一份，
///   偏差照常按它算——改完之后甘特上的"计划"柱子不会跟着挪。
/// · **改动进记录**，带改前 / 改后与修改原因，谁改的署名。
/// · **改的是这一趟**，不是配方本身。要让下一趟也这样，去「配方」页改。
/// </summary>
public sealed class HotEditViewModel : ViewModelBase
{
    private readonly Workspace _ws;
    private readonly Action<string> _say;
    private readonly Action _changed;

    private bool _open;
    private int _channel;
    private HotStepViewModel? _selected;
    private ParameterSet _draft = new();
    private string _reason = "";

    public HotEditViewModel(Workspace ws, Action<string> say, Action changed)
    {
        _ws = ws;
        _say = say;
        _changed = changed;
        Apply = new RelayCommand(DoApply);
        Revert = new RelayCommand(DoRevert);
        Close = new RelayCommand(() => Open = false);
    }

    public ObservableCollection<HotStepViewModel> Steps { get; } = new();

    public RelayCommand Apply { get; }
    public RelayCommand Revert { get; }
    public RelayCommand Close { get; }

    public bool Open
    {
        get => _open;
        private set { if (Set(ref _open, value)) _changed(); }
    }

    public int Channel => _channel;
    public string Title => $"CH{_channel} 运行中改参数";

    private ChannelRunner? Runner => _ws.Engine.Runner(_channel);

    /// <summary>这一路现在能不能改。跑完了就不能——那趟记录已经收口了。</summary>
    public static bool CanOpenFor(Workspace ws, int channel)
        => ws.Engine.Runner(channel) is { CanEdit: true };

    public void OpenFor(int channel)
    {
        _channel = channel;
        _reason = "";
        _open = true;
        Rebuild(keepSelection: false);
        RaiseAll(nameof(Open), nameof(Title), nameof(Reason));
        _changed();
    }

    // ── 选中的那一步 ────────────────────────────────────────────────

    public HotStepViewModel? Selected
    {
        get => _selected;
        set
        {
            var old = _selected;
            if (ReferenceEquals(old, value)) return;
            if (old is not null) old.IsSelected = false;
            _selected = value;
            if (value is not null) value.IsSelected = true;
            BuildForm();
            Raise();
            RaiseAll(nameof(HasSelection), nameof(Form), nameof(EffectText), nameof(BlockedText),
                     nameof(IsBlocked), nameof(BaselineText), nameof(CurrentText), nameof(NextText),
                     nameof(CanApply), nameof(TableNote), nameof(HasTableNote));
        }
    }

    public bool HasSelection => _selected is not null;
    public SchemaFormViewModel? Form { get; private set; }

    /// <summary>改了什么时候生效。</summary>
    public string EffectText => _selected?.Effect ?? "";
    public string? BlockedText => _selected?.Blocked;
    public bool IsBlocked => _selected?.Blocked is not null;

    /// <summary>启动那一刻冻下来的那一份。偏差永远按它算。</summary>
    public string BaselineText => Describe(BaselineStep()?.Parameters, BaselineStep()?.Rows);
    /// <summary>现在实际在跑的那一份（此前改过就与基线不同）。</summary>
    public string CurrentText => Describe(LiveStep()?.Parameters, LiveStep()?.Rows);
    /// <summary>按当前编辑内容会变成什么。改一个格子这一行立刻跟着变。</summary>
    public string NextText => Describe(_draft, LiveStep()?.Rows);

    /// <summary>基线和现行不一样 = 这一趟已经被改过。</summary>
    public bool Amended => _selected is not null && BaselineText != CurrentText;

    /// <summary>分段表（梯度控温那种）不走热改，说清楚而不是画一张改不动的表。</summary>
    public string? TableNote
    {
        get
        {
            if (_selected is null) return null;
            if (!_ws.Catalog.TryGet(_selected.CommandId, out var d)) return null;
            return d.Parameters.Table is null
                ? null
                : $"「{d.Parameters.Table.Label}」不支持运行中修改 —— 分段表改一行会把后面几段的时间全推掉，"
                  + "本趟的排期就对不上了。要改曲线请停下这一路，在「配方」里改完重跑。";
        }
    }
    public bool HasTableNote => TableNote is not null;

    public string Reason
    {
        get => _reason;
        // 输入框清空时写回来的可能是 null，下一句 _reason.Trim() 就炸
        set { if (Set(ref _reason, value ?? "")) Raise(nameof(CanApply)); }
    }

    /// <summary>
    /// **修改原因是必填的。** 一条没有理由的运行中改参数，事后没有任何人
    /// 能解释它——审计要的正是那句话（§7.6）。
    /// </summary>
    public bool CanApply => _selected is { CanEdit: true }
                            && _reason.Trim().Length > 0
                            && Runner is { CanEdit: true };

    public string ApplyTip => _selected is null ? "先在上面挑一步"
        : _selected.Blocked is { } b ? b
        : Runner is not { CanEdit: true } ? $"CH{_channel} 已经不在运行了"
        : _reason.Trim().Length == 0 ? "填一句修改原因 —— 审计要答得出为什么改"
        : "应用：本趟立即生效，基线不动，改动进记录";

    // ── 干活 ────────────────────────────────────────────────────────

    private void DoApply()
    {
        if (_selected is not { } s || Runner is not { } r) return;
        if (!CanApply) { _say(ApplyTip); return; }

        var result = r.ProposeEdit(s.StepId, _draft.Clone(), _ws.Operator, _reason.Trim());
        _say(result.Applied ? $"CH{_channel} 第 {s.Seq} 步：{result.Message}" : $"改不了：{result.Message}");
        if (!result.Applied) return;

        _reason = "";
        Raise(nameof(Reason));
        Rebuild(keepSelection: true);
        _changed();
    }

    /// <summary>把编辑框拨回现在实际在跑的那一份。改坏了不用关面板重来。</summary>
    private void DoRevert()
    {
        BuildForm();
        RaiseAll(nameof(Form), nameof(NextText));
    }

    // ── 刷新 ────────────────────────────────────────────────────────

    /// <summary>周期刷新调它。通道停了就把面板收掉——改不了的表单不该留在眼前。</summary>
    public void Tick()
    {
        if (!_open) return;
        if (Runner is not { CanEdit: true })
        {
            Open = false;
            return;
        }
        Rebuild(keepSelection: true);
    }

    private void Rebuild(bool keepSelection)
    {
        if (Runner is not { } r) { Steps.Clear(); return; }

        var live = r.LiveSteps;
        var run = _ws.Engine.Record.Of(_channel);
        var curIndex = run?.Current?.Index ?? -1;
        // 配方里有循环的话，已经跑过的那几步下一轮还会再跑——「改了没用」这句
        // 在有循环时是错的，反过来在没有循环时不说清楚更糟
        var looped = live.Any(s => BuiltinCommands.IsLoopBegin(s.CommandId));

        var rows = new List<HotStepViewModel>();
        for (var i = 0; i < live.Count; i++)
        {
            var s = live[i];
            if (BuiltinCommands.IsLoopBegin(s.CommandId) || BuiltinCommands.IsLoopEnd(s.CommandId)) continue;
            if (!_ws.Catalog.TryGet(s.CommandId, out var d)) continue;

            var isCur = i == curIndex;
            var isPast = curIndex >= 0 && i < curIndex;
            string? blocked = null;
            string effect;

            if (!s.Enabled)
            {
                blocked = "这一步在配方里是停用的，本趟不会执行";
                effect = "不会执行";
            }
            else if (isCur && !d.SupportsHotEdit)
            {
                blocked = $"「{d.DisplayName}」不支持热改 —— 要改先暂停这一路，或者改后面的步骤";
                effect = "当前步，不支持热改";
            }
            else if (isCur)
            {
                effect = "立即生效（热改当前步）";
            }
            else if (isPast)
            {
                effect = looped ? "已跑过 —— 下一轮循环生效" : "已跑过 —— 本趟不会再执行，改了不生效";
                if (!looped) blocked = "这一步本趟已经跑完，配方里又没有循环，改了不会有任何效果";
            }
            else
            {
                effect = "轮到它时生效";
            }

            rows.Add(new HotStepViewModel
            {
                Index = i,
                StepId = s.StepId,
                CommandId = s.CommandId,
                Title = Describe(d, s.Parameters, s.Rows),
                Module = d.Module,
                IsCurrent = isCur,
                IsPast = isPast,
                HotEditable = d.SupportsHotEdit,
                Effect = effect,
                Blocked = blocked
            });
        }

        var prevId = keepSelection ? _selected?.StepId : null;

        // **一样就一个字都别动。** 这个方法每 700 ms 被调一次；无脑重建的话
        // 每次都换一批行对象，选中高亮跟着闪，表单也跟着重建——操作人正在
        // 输入的那个数会被当场抹掉（实测：把 300 改成 60，松手就变回 300）
        if (!Signature(rows).SequenceEqual(Signature(Steps)))
        {
            Steps.Clear();
            foreach (var row in rows) Steps.Add(row);

            var pick = prevId is null
                ? rows.FirstOrDefault(x => x.IsCurrent && x.CanEdit) ?? rows.FirstOrDefault(x => x.CanEdit)
                : rows.FirstOrDefault(x => x.StepId == prevId);

            if (pick is not null && pick.StepId == prevId)
            {
                // 还是那一步，只是行对象换了新的：接上选中态，**不重建表单**
                _selected = pick;
                pick.IsSelected = true;
                Raise(nameof(Selected));
            }
            else
            {
                _selected = null;                // 强制走一遍 setter：表单要跟着换
                Selected = pick;
            }
        }

        // 「什么时候生效」和「为什么改不了」会随执行推进而变：选中的那一步刚才还是
        // 当前步，几秒后就跑过去了。漏刷这两条的话，面板上还写着"立即生效"，
        // 而应用按钮已经按不动了——最难受的一种不一致
        RaiseAll(nameof(BaselineText), nameof(CurrentText), nameof(NextText),
                 nameof(Amended), nameof(EffectText), nameof(BlockedText), nameof(IsBlocked),
                 nameof(CanApply), nameof(ApplyTip), nameof(NoSteps));
    }

    /// <summary>这一批行跟上一批一不一样：内容变了才重排。</summary>
    private static IEnumerable<string> Signature(IEnumerable<HotStepViewModel> rows)
        => rows.Select(r => $"{r.StepId}|{r.Title}|{r.Effect}|{r.Badge}|{r.CanEdit}").ToList();

    public bool NoSteps => Steps.Count == 0;

    private void BuildForm()
    {
        if (_selected is null || LiveStep() is not { } step ||
            !_ws.Catalog.TryGet(step.CommandId, out var d))
        {
            Form = null;
            _draft = new ParameterSet();
            return;
        }

        // 编辑的是副本：取消不留痕，应用才交给 ProposeEdit 去校验。
        // 分段表不传（rows = null），它不走热改
        _draft = step.Parameters.Clone();
        Form = new SchemaFormViewModel(d.Parameters, _draft, null, _ws.ChannelOf(_channel),
                                       () => RaiseAll(nameof(NextText), nameof(CanApply)),
                                       // 运行中「设定变量」的变量下拉：选项 = 执行器手里那份变量表
                                       choicesOf: key => key == BuiltinCommands.ChoicesFromRecipeVars
                                           ? Runner?.LiveVariables.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList()
                                           : null);
    }

    private Step? LiveStep()
        => _selected is null ? null : Runner?.LiveSteps.FirstOrDefault(s => s.StepId == _selected.StepId);

    private Step? BaselineStep()
        => _selected is null
            ? null
            : _ws.Engine.Record.Of(_channel)?.Baseline.Recipe.Steps
                 .FirstOrDefault(s => s.StepId == _selected.StepId);

    private string Describe(ParameterSet? p, List<ParameterSet>? rows)
    {
        if (p is null || _selected is null) return "—";
        return _ws.Catalog.TryGet(_selected.CommandId, out var d) ? Describe(d, p, rows) : "—";
    }

    private static string Describe(CommandDescriptor d, ParameterSet p, List<ParameterSet>? rows)
    {
        try { return d.Describe(new CommandInput(p, rows)); }
        catch { return d.DisplayName; }
    }
}
