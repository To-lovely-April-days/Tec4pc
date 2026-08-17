using System.IO.Compression;
using System.Text;

namespace Tec.Core.Export;

/// <summary>
/// Office 的三种文件（xlsx / docx / pptx）都是一个 zip 包，里面按约定摆着几份 XML。
/// 这里只做「往包里放一份 XML / 一份二进制」这一件事，不引第三方库——
/// 一个导出功能不值得把整套 OpenXML SDK 拖进来，也不值得为了它绑一个版本。
/// </summary>
public sealed class OoxmlPackage : IDisposable
{
    private readonly ZipArchive _zip;

    public OoxmlPackage(Stream output, bool leaveOpen = false)
        => _zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen);

    /// <summary>放一份 XML。<paramref name="path"/> 用正斜杠，如 xl/workbook.xml。</summary>
    public void Xml(string path, string xml)
        => Xml(path, w => w.Write(xml));

    /// <summary>
    /// 放一份 XML，内容边写边生成。采样表可以有几十万行——
    /// 先在内存里拼成一个字符串再写，光那个字符串就是几百兆。
    /// </summary>
    public void Xml(string path, Action<TextWriter> write)
    {
        var entry = _zip.CreateEntry(path, CompressionLevel.Optimal);
        using var s = entry.Open();
        using var w = new StreamWriter(s, new UTF8Encoding(false));
        w.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n");
        write(w);
    }

    public void Binary(string path, byte[] data)
    {
        var entry = _zip.CreateEntry(path, CompressionLevel.Optimal);
        using var s = entry.Open();
        s.Write(data, 0, data.Length);
    }

    public void Dispose() => _zip.Dispose();

    /// <summary>
    /// XML 文本转义。除了那五个字符，还得把 XML 1.0 根本不允许出现的控制字符扔掉——
    /// 步骤备注是操作人手打的，粘进一个  就能让整份文件打不开。
    /// </summary>
    public static string X(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length + 8);
        foreach (var c in s)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&apos;"); break;
                default:
                    if (c is '\t' or '\n' or '\r' || c >= ' ') sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
}
