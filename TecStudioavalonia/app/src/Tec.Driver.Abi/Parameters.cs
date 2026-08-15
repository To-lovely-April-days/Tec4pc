using System.Collections;
using System.Globalization;

namespace Tec.Driver.Abi;

public enum FieldKind
{
    Number,
    Choice,
    Toggle,
    Text,
    /// <summary>秒为单位的时长，界面用 时:分:秒 编辑。</summary>
    Duration
}

/// <summary>
/// 一个参数字段的声明。核心架构 §4.2。
/// 界面按它生成编辑器——原型已验证 31 条指令零手写表单。
/// </summary>
public sealed record FieldSpec(string Key, string Label, FieldKind Kind)
{
    public object? Default { get; init; }
    public string? Unit { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    public double? Step { get; init; }
    public int Decimals { get; init; } = 1;
    public IReadOnlyList<string>? Choices { get; init; }

    /// <summary>
    /// 动态范围：取自能力的 Limits，如 "TemperatureControl.Limits.Max"。
    /// 有它时覆盖 Min/Max —— 换一台温度范围不同的反应器，编辑器自动改范围。
    /// </summary>
    public string? LimitFrom { get; init; }

    /// <summary>条件显隐，形如 "结束方式=按时间"。</summary>
    public string? VisibleWhen { get; init; }

    public string? Tip { get; init; }
}

/// <summary>多分段表：梯度控温、分段加料、曲线升温。</summary>
public sealed record TableSpec(string Label, IReadOnlyList<FieldSpec> Columns)
{
    public int MinRows { get; init; } = 1;
    public int MaxRows { get; init; } = 32;
}

public sealed record ParameterSchema(IReadOnlyList<FieldSpec> Fields)
{
    public TableSpec? Table { get; init; }
    public string? Tip { get; init; }

    public static readonly ParameterSchema Empty = new(Array.Empty<FieldSpec>());

    public FieldSpec? Find(string key)
    {
        for (var i = 0; i < Fields.Count; i++)
            if (Fields[i].Key == key) return Fields[i];
        return null;
    }
}

/// <summary>参数取值。可序列化、可克隆，不含任何界面对象。</summary>
public sealed class ParameterSet : IEnumerable<KeyValuePair<string, object?>>
{
    private readonly Dictionary<string, object?> _values;

    public ParameterSet() => _values = new Dictionary<string, object?>(StringComparer.Ordinal);

    public ParameterSet(IEnumerable<KeyValuePair<string, object?>> values)
        : this()
    {
        foreach (var kv in values) _values[kv.Key] = kv.Value;
    }

    public int Count => _values.Count;
    public IEnumerable<string> Keys => _values.Keys;
    public bool Has(string key) => _values.ContainsKey(key);

    public object? this[string key]
    {
        get => _values.TryGetValue(key, out var v) ? v : null;
        set => _values[key] = value;
    }

    public double Num(string key, double fallback = 0)
    {
        if (!_values.TryGetValue(key, out var v) || v is null) return fallback;
        return v switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            decimal m => (double)m,
            bool b => b ? 1 : 0,
            string s when double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var p) => p,
            _ => fallback
        };
    }

    public int Int(string key, int fallback = 0) => (int)Math.Round(Num(key, fallback));

    public string Str(string key, string fallback = "")
    {
        if (!_values.TryGetValue(key, out var v) || v is null) return fallback;
        return v as string ?? Convert.ToString(v, CultureInfo.InvariantCulture) ?? fallback;
    }

    public bool Flag(string key, bool fallback = false)
    {
        if (!_values.TryGetValue(key, out var v) || v is null) return fallback;
        return v switch
        {
            bool b => b,
            string s => s is "true" or "True" or "1" or "是" or "开",
            _ => Num(key, fallback ? 1 : 0) != 0
        };
    }

    public TimeSpan Duration(string key, TimeSpan fallback = default)
        => _values.ContainsKey(key) ? TimeSpan.FromSeconds(Num(key)) : fallback;

    public ParameterSet With(string key, object? value)
    {
        var c = Clone();
        c[key] = value;
        return c;
    }

    public ParameterSet Clone() => new(_values);

    /// <summary>按 schema 填齐缺省值，缺什么补什么，已有的不动。</summary>
    public ParameterSet FillDefaults(ParameterSchema schema)
    {
        foreach (var f in schema.Fields)
            if (!_values.ContainsKey(f.Key) && f.Default is not null)
                _values[f.Key] = f.Default;
        return this;
    }

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => _values.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public static ParameterSet Of(params (string Key, object? Value)[] pairs)
    {
        var p = new ParameterSet();
        foreach (var (k, v) in pairs) p[k] = v;
        return p;
    }
}
