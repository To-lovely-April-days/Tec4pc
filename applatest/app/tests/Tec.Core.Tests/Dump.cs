namespace Tec.Core.Tests;

/// <summary>
/// 把测试生成的文件留一份出来，供人拿真正的 Excel / Word / PDF 打开看。
///
/// 单元测试断言得了「zip 里有没有那份 XML」，断言不了「Excel 打开会不会报修复」——
/// 后者只能人去开一次。设了 TEC_DUMP 就往那个目录里留一份，没设就什么都不做。
/// </summary>
public static class Dump
{
    public static string? Dir => Environment.GetEnvironmentVariable("TEC_DUMP");

    public static void Save(string name, byte[] bytes)
    {
        if (Dir is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name), bytes);
    }

    public static void Save(string name, string path)
    {
        if (Dir is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir);
        File.Copy(path, Path.Combine(dir, name), overwrite: true);
    }
}
