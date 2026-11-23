using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tec.DriverHost;

/// <summary>
/// drivers/&lt;id&gt;/manifest.json。
/// 里面的 capabilities 是**冗余的**——真正的能力以运行时接口实现为准；
/// 写一份是为了让设备库在不加载程序集的情况下就能列出与过滤设备（§3.1）。
/// </summary>
public sealed class DriverManifest
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("vendor")] public string Vendor { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "0.0.0";
    [JsonPropertyName("abi")] public string Abi { get; set; } = "1.0";
    [JsonPropertyName("entry")] public string Entry { get; set; } = "";
    [JsonPropertyName("assemblyQualifiedType")] public string TypeName { get; set; } = "";
    [JsonPropertyName("capabilities")] public List<string> Capabilities { get; set; } = new();
    [JsonPropertyName("channelsPerDevice")] public int ChannelsPerDevice { get; set; }
    [JsonPropertyName("simulatorIncluded")] public bool SimulatorIncluded { get; set; } = true;
    [JsonPropertyName("icon")] public string? Icon { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }

    /// <summary>
    /// 【本地改动】同一个程序集里的**其余**驱动类型（完整类名）。可选，缺省空。
    /// WHY：一个 manifest 原本只带一个驱动。演示模式要把仿真驱动包（反应器 / 探头 / 泵 / 搅拌 / 进料
    /// 五六个驱动在一个 dll 里）整包装进来，而且**必须在同一个 AssemblyLoadContext 里**——仿真泵往釜里加的酸
    /// 要让仿真 pH 电极看见，靠的是程序集内的静态状态，拆成一包一个上下文就断了。
    /// 这里列出的类型随主类型一起实例化，各自按 Info.Id 登记成独立的驱动包（目录、状态同主包）。
    /// </summary>
    [JsonPropertyName("siblingTypes")] public List<string> SiblingTypes { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static DriverManifest? Read(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return JsonSerializer.Deserialize<DriverManifest>(fs, Options);
        }
        catch
        {
            return null;
        }
    }

    public string Validate()
    {
        if (string.IsNullOrWhiteSpace(Id)) return "manifest.id 为空";
        if (string.IsNullOrWhiteSpace(Entry)) return "manifest.entry 为空";
        if (string.IsNullOrWhiteSpace(TypeName)) return "manifest.assemblyQualifiedType 为空";
        return "";
    }
}
