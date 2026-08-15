namespace Tec.Driver.Abi;

/// <summary>驱动契约的版本。主版本不匹配的驱动不加载（核心架构 §3.5）。</summary>
public static class AbiVersion
{
    public const int Major = 1;
    public const int Minor = 0;
    public static string Text => $"{Major}.{Minor}";

    /// <summary>manifest.abi 形如 "1.0"；只比主版本。</summary>
    public static bool IsCompatible(string? declared)
    {
        if (string.IsNullOrWhiteSpace(declared)) return false;
        var head = declared.Split('.')[0];
        return int.TryParse(head, out var major) && major == Major;
    }
}
