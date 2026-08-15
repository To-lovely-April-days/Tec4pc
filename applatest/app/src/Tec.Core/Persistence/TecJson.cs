using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tec.Driver.Abi;

namespace Tec.Core.Persistence;

/// <summary>
/// 存盘用的 JSON 约定。三种文件（台面 / 配方 / 实验）共用一套设置，
/// 换行缩进都开着——这些文件要能被人直接打开看、能进版本库做 diff。
/// </summary>
public static class TecJson
{
    public static readonly JsonSerializerOptions Options = Build();

    private static JsonSerializerOptions Build()
    {
        var o = new JsonSerializerOptions
        {
            WriteIndented = true,
            // 中文字段值不转义成 \uXXXX，文件要能直接读
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };
        o.Converters.Add(new JsonStringEnumConverter());
        o.Converters.Add(new ParameterSetConverter());
        return o;
    }

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)
        ?? throw new TecFileException("文件内容为空或不是有效的 JSON。");
}

/// <summary>读盘失败的统一异常。界面拿它的 Message 直接显示给操作人。</summary>
public sealed class TecFileException : Exception
{
    public TecFileException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// ParameterSet 的读写。值只有数字 / 布尔 / 文本三种，读回来必须还原成
/// double / bool / string 这三个 CLR 类型——留成 JsonElement 的话，
/// ParameterSet.Num() 那几个取值方法一个都认不出来，参数会全部退回默认值。
/// </summary>
public sealed class ParameterSetConverter : JsonConverter<ParameterSet>
{
    public override ParameterSet Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("参数应当是一个对象。");

        var set = new ParameterSet();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) return set;
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("参数对象里出现了预期外的内容。");

            var key = reader.GetString()!;
            reader.Read();
            set[key] = reader.TokenType switch
            {
                JsonTokenType.Number => reader.GetDouble(),
                JsonTokenType.True => true,
                JsonTokenType.False => false,
                JsonTokenType.String => reader.GetString(),
                JsonTokenType.Null => null,
                _ => throw new JsonException($"参数 {key} 的取值类型不支持。")
            };
        }
        throw new JsonException("参数对象没有正常结束。");
    }

    public override void Write(Utf8JsonWriter writer, ParameterSet value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        // 键按字典序写，同一份配方每次存出来的字节完全一样，能做 diff 也能算校验码
        foreach (var key in value.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            writer.WritePropertyName(key);
            switch (value[key])
            {
                case null: writer.WriteNullValue(); break;
                case bool b: writer.WriteBooleanValue(b); break;
                case string s: writer.WriteStringValue(s); break;
                case double d: writer.WriteNumberValue(d); break;
                case float f: writer.WriteNumberValue(f); break;
                case int i: writer.WriteNumberValue(i); break;
                case long l: writer.WriteNumberValue(l); break;
                case decimal m: writer.WriteNumberValue(m); break;
                case var other: writer.WriteStringValue(
                    Convert.ToString(other, CultureInfo.InvariantCulture)); break;
            }
        }
        writer.WriteEndObject();
    }
}
