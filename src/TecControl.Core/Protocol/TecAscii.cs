using System.Text;

namespace TecControl.Core.Protocol;

/// <summary>
/// ASCII 指令名称常量（RD105 协议第 3 章）。
/// 通道控制指令使用 "TCn:" 前缀，通用参数指令不带前缀。
/// </summary>
public static class TecCmd
{
    // 通道控制指令
    public const string Target = "TG";
    public const string ActualTemp = "TCADJTEMP";
    public const string Resistor = "RESISTOR";
    public const string Enable = "ENABLE";
    public const string Mode = "MODE";
    public const string OutputPolarity = "PIDPOL";
    public const string PwmDuty = "PWMDUTY";
    public const string Speed = "SPEED";
    public const string MaxDuty = "LIMITED";
    public const string Kp = "KP";
    public const string Ki = "KI";
    public const string Kd = "KD";
    public const string AutoPid = "AUTOPID";
    public const string OverTempUp = "OVERTEMPUP";
    public const string OverTempLower = "OVERTEMPLOWER";
    public const string Current = "CURRENT";
    public const string SetCurrent = "SETCURRENT";
    public const string OnSensor = "ONSENSOR";
    public const string PowerMode = "POWERMODE";

    // 通用参数指令
    public const string PwmFrequency = "FPWM";
    public const string ControllerMode = "CONTMODE";
    public const string ErrorCode = "ERRORCODE";
    public const string Model = "TEC";
    public const string FirmwareVersion = "FPV";
    public const string InternalTemp = "SINTERIORTEMP";
    public const string DataDemand = "DATADEMAND";
}

public sealed class TecProtocolException(string message) : Exception(message);

/// <summary>ASCII 指令的构造与应答解析。</summary>
public static class TecAscii
{
    /// <summary>构造查询指令，如 "TC1:TG=?@\n"；channel 为 null 时为通用参数指令。</summary>
    public static string BuildQuery(int? channel, string name) =>
        $"{Prefix(channel)}{name}=?@\n";

    /// <summary>构造设置指令，如 "TC1:TG=2500000@\n"。</summary>
    public static string BuildSet(int? channel, string name, long value) =>
        $"{Prefix(channel)}{name}={value}@\n";

    /// <summary>应答中某个字段的完整键名，如 "TC1:TG"。</summary>
    public static string FieldKey(int? channel, string name) => $"{Prefix(channel)}{name}";

    private static string Prefix(int? channel)
    {
        if (channel is null) return string.Empty;
        if (channel is < 1 or > 2)
            throw new ArgumentOutOfRangeException(nameof(channel), "通道号只能是 1 或 2");
        return $"TC{channel}:";
    }

    /// <summary>
    /// 解析设备应答为 键→原始文本值 字典。
    /// 应答由若干 "名称=值@" 片段拼接而成，片段前可能带 "OK"，末尾带 \r\n；
    /// 协议文档示例中偶见全角冒号，一并归一化处理。
    /// 值不一定是数字：实测设备对 TEC（型号）等查询返回的是型号名字符串（如 "215L"）。
    /// </summary>
    public static Dictionary<string, string> ParseFieldsText(string reply)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var normalized = reply.Replace('：', ':').Replace("\r", "").Replace("\n", "");

        foreach (var rawToken in normalized.Split('@', StringSplitOptions.RemoveEmptyEntries))
        {
            var token = rawToken.Trim();
            while (token.StartsWith("OK", StringComparison.OrdinalIgnoreCase))
                token = token[2..];

            var eq = token.IndexOf('=');
            if (eq <= 0) continue;

            var key = token[..eq].Trim();
            var valueText = token[(eq + 1)..].Trim();
            if (valueText.Length > 0 && valueText != "?")
                result[key] = valueText;
        }

        return result;
    }

    /// <summary>解析设备应答为 键→整数值 字典（跳过非数字字段）。</summary>
    public static Dictionary<string, long> ParseFields(string reply)
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, text) in ParseFieldsText(reply))
        {
            if (long.TryParse(text, out var value))
                result[key] = value;
        }
        return result;
    }

    /// <summary>从应答中取出指定字段的原始文本值（用于型号等非数字字段），不存在则抛出协议异常。</summary>
    public static string GetTextField(string reply, int? channel, string name)
    {
        var fields = ParseFieldsText(reply);
        var key = FieldKey(channel, name);
        if (!fields.TryGetValue(key, out var value))
            throw new TecProtocolException($"应答中未找到字段 {key}，原始应答：{Printable(reply)}");
        return value;
    }

    /// <summary>从应答中取出指定字段的原始值，不存在则抛出协议异常。</summary>
    public static long GetField(string reply, int? channel, string name)
    {
        var fields = ParseFields(reply);
        var key = FieldKey(channel, name);
        if (!fields.TryGetValue(key, out var value))
            throw new TecProtocolException($"应答中未找到字段 {key}，原始应答：{Printable(reply)}");
        return value;
    }

    public static string Printable(string s) =>
        new StringBuilder(s).Replace("\r", "\\r").Replace("\n", "\\n").ToString();
}
