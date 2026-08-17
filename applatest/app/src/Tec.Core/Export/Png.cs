using System.IO.Compression;

namespace Tec.Core.Export;

/// <summary>
/// 最小 PNG 编码：8 位真彩、逐行 0 号滤波。
///
/// Word 的图必须是正经图片格式（BMP 能用但一张图几兆），而报告里的图是
/// 界面层画好的原始像素。PDF 那边直接把像素压进去就行，Word 这边得包成 PNG。
/// 只做「写」，不做「读」——本程序从不需要解别人的 PNG。
/// </summary>
public static class Png
{
    public static byte[] FromBgra(byte[] bgra, int width, int height)
    {
        // 每行前面加一个滤波类型字节（0 = 原样）
        var raw = new byte[(long)height * (width * 3 + 1)];
        var o = 0;
        for (var y = 0; y < height; y++)
        {
            raw[o++] = 0;
            var row = (long)y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var i = row + x * 4;
                if (i + 3 >= bgra.Length) { o += 3; continue; }
                var a = bgra[i + 3];
                if (a == 255)
                {
                    raw[o++] = bgra[i + 2];
                    raw[o++] = bgra[i + 1];
                    raw[o++] = bgra[i];
                }
                else
                {
                    // 压在白底上：PNG 这里不留 alpha 通道，透明处不压会变黑
                    raw[o++] = (byte)(bgra[i + 2] * a / 255 + (255 - a));
                    raw[o++] = (byte)(bgra[i + 1] * a / 255 + (255 - a));
                    raw[o++] = (byte)(bgra[i] * a / 255 + (255 - a));
                }
            }
        }

        using var ms = new MemoryStream();
        ms.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });

        var ihdr = new byte[13];
        Put32(ihdr, 0, (uint)width);
        Put32(ihdr, 4, (uint)height);
        ihdr[8] = 8;        // 位深
        ihdr[9] = 2;        // 真彩 RGB
        Chunk(ms, "IHDR", ihdr);

        using (var z = new MemoryStream())
        {
            using (var d = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true))
                d.Write(raw, 0, raw.Length);
            Chunk(ms, "IDAT", z.ToArray());
        }

        Chunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        Put32(len, 0, (uint)data.Length);
        s.Write(len);

        var body = new byte[4 + data.Length];
        for (var i = 0; i < 4; i++) body[i] = (byte)type[i];
        data.CopyTo(body, 4);
        s.Write(body);

        var crc = new byte[4];
        Put32(crc, 0, Crc(body));
        s.Write(crc);
    }

    private static void Put32(byte[] b, int o, uint v)
    {
        b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
    }

    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    private static uint Crc(byte[] data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
