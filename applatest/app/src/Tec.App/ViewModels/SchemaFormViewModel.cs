using Tec.Hmi.Ui.ViewModels;
using System.Collections.ObjectModel;
using System.Globalization;
using Tec.App.Controls;
using Tec.Core.Benches;
using Tec.Driver.Abi;

namespace Tec.App.ViewModels;

/// <summary>
/// 一个参数字段。**表单一律由 schema 渲染**——指令参数、通信配置、设备配置共用这一套。
/// 原型已经验证 31 条指令零手写表单是可行的（§11）。
/// </summary>
public sealed class FieldViewModel : ViewModelBase
{
    private readonly ParameterSet _target;
    private readonly Action? _changed;

    public FieldViewModel(FieldSpec spec, ParameterSet target, Channel? channel = null, Action? changed = null,
                          IReadOnlyList<ChoiceOption>? dynamicChoices = null)
    {
        Spec = spec;
        _target = target;
        _changed = changed;

        // 动态下拉（ChoicesFrom）：选项由编辑现场给，声明里那份只是兜底。
        // 现场给的项**存的值与显示的字可以不一样**——串口就是：存「COM16」，
        // 显示「COM16 · USB-Enhanced-SERIAL-A CH344」。这里两边各存一份对照表，
        // 下拉绑的是显示串，读写参数时换回值
        var opts = dynamicChoices
                   ?? (spec.Choices ?? Array.Empty<string>()).Select(c => new ChoiceOption(c)).ToList();
        foreach (var o in opts)
        {
            _valueOf[o.Text] = o.Value;
            _textOf[o.Value] = o.Text;
        }
        Choices = new ObservableCollection<string>(opts.Select(o => o.Text));

        // 显示串比值长（串口带设备名那种）时，属性栏那一百多宽的输入列装不下，
        // 收起来的框里只看得见前半截——挂个悬停提示把整串摆出来
        _labelled = opts.Any(o => !string.Equals(o.Text, o.Value, StringComparison.Ordinal));

        // 动态范围：LimitFrom 让参数上限跟着设备走（§4.2）
        var bound = channel is null ? null : ResolveLimit(channel, spec.LimitFrom);
        Min = spec.LimitFrom is not null && spec.LimitFrom.EndsWith(".Min", StringComparison.Ordinal) && bound is not null
            ? bound : spec.Min;
        Max = spec.LimitFrom is not null && spec.LimitFrom.EndsWith(".Max", StringComparison.Ordinal) && bound is not null
            ? bound : spec.Max;
        LimitNote = bound is not null ? $"设备限值 {bound:F1}" : null;

        if (!_target.Has(spec.Key) && spec.Default is not null) _target[spec.Key] = spec.Default;
    }

    public FieldSpec Spec { get; }
    public string Key => Spec.Key;
    public string Label => Spec.Label;
    public string? Unit => Spec.Unit;
    public string? Tip => Spec.Tip;
    public string? LimitNote { get; }
    public bool HasLimitNote => !string.IsNullOrEmpty(LimitNote);
    public double? Min { get; }
    public double? Max { get; }
    public ObservableCollection<string> Choices { get; }

    /// <summary>只读字段（设备参数面板里的型号、实时电流那些）：按值显示，不给编辑器。</summary>
    public bool IsReadOnly => Spec.ReadOnly;
    public bool IsNumber => Spec.Kind == FieldKind.Number && !Spec.ReadOnly;
    public bool IsDuration => Spec.Kind == FieldKind.Duration && !Spec.ReadOnly;
    public bool IsChoice => Spec.Kind == FieldKind.Choice && !Spec.ReadOnly;
    public bool IsToggle => Spec.Kind == FieldKind.Toggle && !Spec.ReadOnly;
    public bool IsText => Spec.Kind == FieldKind.Text && !Spec.ReadOnly;

    /// <summary>只读字段显示的那一串：数值按小数位（NaN 印「—」，不印 NaN）、下拉印选项、开关印开/关。</summary>
    public string ValueText
    {
        get
        {
            var raw = _target[Key];
            switch (Spec.Kind)
            {
                case FieldKind.Number:
                {
                    if (raw is null) return "—";
                    var v = _target.Num(Key, double.NaN);
                    return double.IsNaN(v) ? "—" : v.ToString("F" + Spec.Decimals, CultureInfo.InvariantCulture);
                }
                case FieldKind.Duration: return DurationText;
                case FieldKind.Toggle: return ToggleValue ? "开" : "关";
                default:
                {
                    var s = _target.Str(Key);
                    return s.Length == 0 ? "—" : s;
                }
            }
        }
    }

    /// <summary>外面直接改了 target（参数面板从设备读回一组值）之后，让界面重读一遍。</summary>
    public void Refresh() => RaiseAll(nameof(NumberText), nameof(DurationText), nameof(ChoiceValue),
                                      nameof(ToggleValue), nameof(TextValue), nameof(ValueText), nameof(IsVisible));

    public string NumberText
    {
        get => _target.Num(Key).ToString("F" + Spec.Decimals, CultureInfo.InvariantCulture);
        set
        {
            if (!double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)) return;
            if (Min is { } lo && v < lo) v = lo;
            if (Max is { } hi && v > hi) v = hi;
            _target[Key] = v;
            Raise();
            Raise(nameof(DurationText));
            _changed?.Invoke();
        }
    }

    /// <summary>时长用 时:分:秒 编辑，但存的仍然是秒——单位在内部只有一套（§10.6）。</summary>
    public string DurationText
    {
        get
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, _target.Num(Key)));
            return $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}";
        }
        set
        {
            var parts = value.Split(':');
            double secs = 0;
            try
            {
                secs = parts.Length switch
                {
                    3 => double.Parse(parts[0], CultureInfo.InvariantCulture) * 3600
                         + double.Parse(parts[1], CultureInfo.InvariantCulture) * 60
                         + double.Parse(parts[2], CultureInfo.InvariantCulture),
                    2 => double.Parse(parts[0], CultureInfo.InvariantCulture) * 60
                         + double.Parse(parts[1], CultureInfo.InvariantCulture),
                    _ => double.Parse(value, CultureInfo.InvariantCulture)
                };
            }
            catch { return; }
            _target[Key] = Math.Max(0, secs);
            Raise();
            Raise(nameof(NumberText));
            _changed?.Invoke();
        }
    }

    /// <summary>下拉项的「显示串 → 存进参数的值」对照，以及反过来那一份。</summary>
    private readonly Dictionary<string, string> _valueOf = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _textOf = new(StringComparer.Ordinal);
    private readonly bool _labelled;

    /// <summary>
    /// 悬停提示。只有显示串带了额外信息（串口的设备名）才给——
    /// 「串级」「按夹套」这种本来就看得全的，再挂一个原样重复的气泡是噪音。
    /// </summary>
    public string? ChoiceTip => _labelled ? ChoiceValue : null;

    /// <summary>
    /// 下拉绑的是**显示串**。存进参数的仍是值本身——
    /// .tecbench 里写的必须是「COM16」，不能是「COM16 · USB-Enhanced-SERIAL-A CH344」。
    /// </summary>
    public string ChoiceValue
    {
        get
        {
            var v = _target.Str(Key, Spec.Default as string ?? "");
            return _textOf.TryGetValue(v, out var t) ? t : v;
        }
        set
        {
            _target[Key] = _valueOf.TryGetValue(value ?? "", out var v) ? v : value;
            Raise();
            Raise(nameof(ChoiceTip));
            _changed?.Invoke();
        }
    }

    public bool ToggleValue
    {
        get => _target.Flag(Key, Spec.Default is bool b && b);
        set { _target[Key] = value; Raise(); _changed?.Invoke(); }
    }

    public string TextValue
    {
        get => _target.Str(Key);
        set { _target[Key] = value; Raise(); _changed?.Invoke(); }
    }

    /// <summary>条件显隐，如 结束方式=按时间。由外层在参数变化后重算。</summary>
    public bool IsVisible
    {
        get
        {
            if (Spec.VisibleWhen is null) return true;
            var bits = Spec.VisibleWhen.Split('=');
            if (bits.Length != 2) return true;
            var actual = _target[bits[0]];
            return string.Equals(Convert.ToString(actual, CultureInfo.InvariantCulture), bits[1],
                                 StringComparison.OrdinalIgnoreCase);
        }
    }

    public void RefreshVisibility() => Raise(nameof(IsVisible));

    private static double? ResolveLimit(Channel channel, string? path)
    {
        if (path is null) return null;
        var parts = path.Split('.');
        if (parts.Length < 3) return null;
        var which = parts[^1];
        return parts[0] switch
        {
            "TemperatureControl" => channel.Capabilities.Get<ITemperatureControl>() is { } t
                ? which switch
                {
                    "Max" => t.Limits.Max,
                    "Min" => t.Limits.Min,
                    "MaxRatePerMin" => t.Limits.MaxRatePerMin,
                    _ => null
                }
                : null,
            "Stirrer" => channel.Capabilities.Get<IStirrer>() is { } s
                ? which switch { "Max" => s.Limits.Max, "Min" => s.Limits.Min, _ => null }
                : null,
            "Dosing" => channel.Capabilities.Get<IDosing>() is { } d
                ? which switch { "Max" => d.Limits.Max, "Min" => d.Limits.Min, "MaxVolume" => d.Limits.MaxVolume, _ => null }
                : null,
            _ => null
        };
    }
}

/// <summary>一张按 schema 生成的表单，外加可选的多分段表。</summary>
public sealed class SchemaFormViewModel : ViewModelBase
{
    private readonly Action? _changed;

    public SchemaFormViewModel(ParameterSchema schema, ParameterSet target,
                               List<ParameterSet>? rows = null, Channel? channel = null,
                               Action? changed = null, double startTemp = 25,
                               Action<string>? fieldEdited = null,
                               Func<string, string?, IReadOnlyList<ChoiceOption>?>? choicesOf = null)
    {
        Schema = schema;
        Target = target;
        Rows = rows;
        Channel = channel;
        _changed = changed;
        StartTemp = startTemp;

        foreach (var f in schema.Fields)
        {
            // fieldEdited 让外层知道**改的是哪个键**（值已写进 target）——
            // 「手改加料体积就脱离配料表跟随」这种规则得认得出字段
            var key = f.Key;
            Fields.Add(new FieldViewModel(f, target, channel,
                fieldEdited is null ? OnFieldChanged
                                    : () => { fieldEdited(key); OnFieldChanged(); },
                // 现场取选项时**把这个字段当前存的值一起带过去**：串口那种「存好的口
                // 现在不在线」的情形，现场要靠它把这一项留在下拉里，不能让空下拉把它吞了
                f.ChoicesFrom is { } src ? choicesOf?.Invoke(src, target.Str(f.Key, f.Default as string ?? "")) : null));
        }

        if (schema.Table is not null && rows is not null)
            foreach (var r in rows) RowForms.Add(BuildRow(r));
    }

    public ParameterSchema Schema { get; }
    public ParameterSet Target { get; }
    public List<ParameterSet>? Rows { get; }
    public Channel? Channel { get; }

    public ObservableCollection<FieldViewModel> Fields { get; } = new();
    public ObservableCollection<ObservableCollection<FieldViewModel>> RowForms { get; } = new();

    /// <summary>
    /// 没给 rows 就不画那张分段表。表在、行的容器不在的话，界面上会摆出一张
    /// 空表加一个按下去没反应的「＋ 添加一段」——运行中改参数就是这种场合
    /// （分段表不支持热改，只放标量字段）。
    /// </summary>
    public bool HasTable => Schema.Table is not null && Rows is not null;
    public string TableLabel => Schema.Table?.Label ?? "";

    // ── 曲线预览 ─────────────────────────────────────────────────────

    /// <summary>这张表要不要画成温度曲线。由指令自己声明（TableSpec.Chart）。</summary>
    public bool HasProfile => Schema.Table?.Chart == "temp-profile";

    /// <summary>这一步开始时的温度：第一段「从当前温度走到目标」的长度全靠它。</summary>
    public double StartTemp { get; }

    /// <summary>
    /// 图下面那行小字：起点、几段、合计多久。
    /// 起点标在图上会跟纵轴刻度撞一起，挪下来更清楚。
    /// </summary>
    public string ProfileNote
    {
        get
        {
            var segs = Profile;
            if (segs.Count == 0) return "";
            var cur = StartTemp;
            var minutes = 0d;
            foreach (var s in segs)
            {
                minutes += Math.Abs(s.Target - cur) / Math.Max(s.Rate, 0.01) + Math.Max(0, s.Hold);
                cur = s.Target;
            }
            var span = minutes >= 60
                ? $"{(int)(minutes / 60)} h {minutes % 60:F0} min"
                : $"{minutes:F0} min";
            return $"起点 {StartTemp:F1} ℃ · {segs.Count} 段 · 合计约 {span}";
        }
    }

    /// <summary>
    /// 表里的行翻成曲线的分段。列按位置取（0 目标 / 1 速率 / 2 保持），
    /// 与 TableSpec.Chart = "temp-profile" 约定的列序一致。
    /// </summary>
    public IReadOnlyList<ProfileSeg> Profile
    {
        get
        {
            if (!HasProfile || Rows is null || Schema.Table is null) return Array.Empty<ProfileSeg>();
            var c = Schema.Table.Columns;
            if (c.Count < 3) return Array.Empty<ProfileSeg>();
            return Rows.Select(r => new ProfileSeg(r.Num(c[0].Key), r.Num(c[1].Key), r.Num(c[2].Key)))
                       .ToList();
        }
    }
    public string? Tip => Schema.Tip;
    public bool HasTip => !string.IsNullOrWhiteSpace(Schema.Tip);

    public void AddRow()
    {
        if (Schema.Table is null || Rows is null) return;
        var set = new ParameterSet();
        foreach (var c in Schema.Table.Columns) if (c.Default is not null) set[c.Key] = c.Default;
        Rows.Add(set);
        RowForms.Add(BuildRow(set));
        OnFieldChanged();
    }

    public void RemoveRow(int index)
    {
        if (Rows is null || index < 0 || index >= Rows.Count) return;
        Rows.RemoveAt(index);
        RowForms.RemoveAt(index);
        OnFieldChanged();
    }

    private ObservableCollection<FieldViewModel> BuildRow(ParameterSet set)
    {
        var list = new ObservableCollection<FieldViewModel>();
        foreach (var c in Schema.Table!.Columns)
            list.Add(new FieldViewModel(c, set, Channel, OnFieldChanged));
        return list;
    }

    private void OnFieldChanged()
    {
        foreach (var f in Fields) f.RefreshVisibility();
        RaiseAll(nameof(Profile), nameof(ProfileNote));   // 改一个格子，曲线与小字立刻跟着变
        _changed?.Invoke();
    }

    /// <summary>target 被外面整组改过（设备参数面板读回一组值）：每个字段重读一遍。</summary>
    public void RefreshValues()
    {
        foreach (var f in Fields) f.Refresh();
    }
}
