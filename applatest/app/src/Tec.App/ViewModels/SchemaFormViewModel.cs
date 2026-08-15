using System.Collections.ObjectModel;
using System.Globalization;
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

    public FieldViewModel(FieldSpec spec, ParameterSet target, Channel? channel = null, Action? changed = null)
    {
        Spec = spec;
        _target = target;
        _changed = changed;
        Choices = new ObservableCollection<string>(spec.Choices ?? Array.Empty<string>());

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

    public bool IsNumber => Spec.Kind == FieldKind.Number;
    public bool IsDuration => Spec.Kind == FieldKind.Duration;
    public bool IsChoice => Spec.Kind == FieldKind.Choice;
    public bool IsToggle => Spec.Kind == FieldKind.Toggle;
    public bool IsText => Spec.Kind == FieldKind.Text;

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

    public string ChoiceValue
    {
        get => _target.Str(Key, Spec.Default as string ?? "");
        set { _target[Key] = value; Raise(); _changed?.Invoke(); }
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
                               List<ParameterSet>? rows = null, Channel? channel = null, Action? changed = null)
    {
        Schema = schema;
        Target = target;
        Rows = rows;
        Channel = channel;
        _changed = changed;

        foreach (var f in schema.Fields)
            Fields.Add(new FieldViewModel(f, target, channel, OnFieldChanged));

        if (schema.Table is not null && rows is not null)
            foreach (var r in rows) RowForms.Add(BuildRow(r));
    }

    public ParameterSchema Schema { get; }
    public ParameterSet Target { get; }
    public List<ParameterSet>? Rows { get; }
    public Channel? Channel { get; }

    public ObservableCollection<FieldViewModel> Fields { get; } = new();
    public ObservableCollection<ObservableCollection<FieldViewModel>> RowForms { get; } = new();

    public bool HasTable => Schema.Table is not null;
    public string TableLabel => Schema.Table?.Label ?? "";
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
        _changed?.Invoke();
    }
}
