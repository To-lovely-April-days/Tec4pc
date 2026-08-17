using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Tec.Core.Export;

/// <summary>
/// 排好版的页 → PDF。
///
/// 内嵌中文字体子集，写的是**真文字**：能选、能复制、能全文搜索。
/// 把每页渲染成图片塞进 PDF 也能出中文，但那样的报告搜不到「CH2」，
/// 也没法从里面把一个数字复制出来——交给审计的文件不该是一叠截图。
/// </summary>
public static class PdfWriter
{
    /// <summary>找不到可内嵌的中文字体时抛它。界面照实说，不退回英文字体糊弄。</summary>
    public sealed class NoFontException : Exception
    {
        public NoFontException(IReadOnlyList<string> searched)
            : base("找不到可内嵌的中文字体，无法生成 PDF。已找过：" + string.Join("；", searched)) { }
    }

    public static void Save(string path, IReadOnlyList<ReportPage> pages, TrueTypeFont font,
                            PageStyle style, string title, string author)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        Write(fs, pages, font, style, title, author);
    }

    public static void Write(Stream output, IReadOnlyList<ReportPage> pages, TrueTypeFont font,
                             PageStyle style, string title, string author)
    {
        var doc = new PdfDoc();

        // 用到哪些字形，字体子集就留哪些
        var gids = new SortedSet<int> { 0 };
        var toUnicode = new SortedDictionary<int, int>();
        foreach (var page in pages)
            foreach (var item in page.Items)
                if (item is TextItem t)
                    foreach (var (cp, gid) in Glyphs(font, t.Text))
                    {
                        gids.Add(gid);
                        toUnicode[gid] = cp;
                    }

        var tag = SubsetTag(gids);
        var baseFont = tag + "+" + font.PostScriptName;
        var subset = font.Subset(gids);

        var fontFile = doc.Stream(subset, $"/Length1 {subset.Length}");
        var descriptor = doc.Obj(
            "<</Type/FontDescriptor/FontName/" + baseFont
            + "/Flags 4/FontBBox[" + Scale(font, font.XMin) + " " + Scale(font, font.YMin) + " "
            + Scale(font, font.XMax) + " " + Scale(font, font.YMax) + "]"
            + "/ItalicAngle 0/Ascent " + Scale(font, font.Ascender)
            + "/Descent " + Scale(font, font.Descender)
            + "/CapHeight " + Scale(font, font.CapHeight)
            + "/StemV 80/FontFile2 " + fontFile + " 0 R>>");

        var w = new StringBuilder("[");
        foreach (var g in gids) w.Append(g).Append('[').Append(font.Width1000(g)).Append(']');
        w.Append(']');

        var cid = doc.Obj(
            "<</Type/Font/Subtype/CIDFontType2/BaseFont/" + baseFont
            + "/CIDSystemInfo<</Registry(Adobe)/Ordering(Identity)/Supplement 0>>"
            + "/FontDescriptor " + descriptor + " 0 R/DW 1000/W " + w
            + "/CIDToGIDMap/Identity>>");

        var uni = doc.Stream(Encoding.ASCII.GetBytes(ToUnicodeCMap(toUnicode)));
        var fontRef = doc.Obj(
            "<</Type/Font/Subtype/Type0/BaseFont/" + baseFont
            + "/Encoding/Identity-H/DescendantFonts[" + cid + " 0 R]/ToUnicode " + uni + " 0 R>>");

        var pagesRef = doc.Reserve();
        var kids = new List<int>();

        foreach (var page in pages)
        {
            var images = new List<(string Name, int Ref)>();
            var content = Content(page, style, font, images, doc);
            var contentRef = doc.Stream(content);

            var res = new StringBuilder("<</Font<</F1 " + fontRef + " 0 R>>");
            if (images.Count > 0)
            {
                res.Append("/XObject<<");
                foreach (var (name, r) in images) res.Append('/').Append(name).Append(' ').Append(r).Append(" 0 R");
                res.Append(">>");
            }
            res.Append("/ProcSet[/PDF/Text/ImageC]>>");

            kids.Add(doc.Obj(
                "<</Type/Page/Parent " + pagesRef + " 0 R"
                + "/MediaBox[0 0 " + F(style.Width) + " " + F(style.Height) + "]"
                + "/Resources " + res + "/Contents " + contentRef + " 0 R>>"));
        }

        doc.Fill(pagesRef, "<</Type/Pages/Count " + kids.Count + "/Kids["
                           + string.Join(" ", kids.Select(k => k + " 0 R")) + "]>>");

        var info = doc.Obj("<</Title" + Str(title) + "/Author" + Str(author)
                           + "/Producer(TecStudio)/Creator(TecStudio)"
                           + "/CreationDate(" + Date(DateTimeOffset.Now) + ")>>");
        var catalog = doc.Obj("<</Type/Catalog/Pages " + pagesRef + " 0 R>>");

        doc.WriteTo(output, catalog, info);
    }

    // ── 内容流 ──────────────────────────────────────────────────────

    private static byte[] Content(ReportPage page, PageStyle style, TrueTypeFont font,
                                  List<(string Name, int Ref)> images, PdfDoc doc)
    {
        var sb = new StringBuilder();
        var n = 0;

        foreach (var item in page.Items)
        {
            switch (item)
            {
                case RectItem r:
                {
                    // 版面 Y 从页顶往下，PDF 从页底往上
                    var y = style.Height - r.Y - r.H;
                    if (r.Fill is { } fill)
                        sb.Append(Rgb(fill, false)).Append(F(r.X)).Append(' ').Append(F(y)).Append(' ')
                          .Append(F(r.W)).Append(' ').Append(F(r.H)).Append(" re f\n");
                    if (r.Stroke is { } stroke)
                        sb.Append(Rgb(stroke, true)).Append(F(r.StrokeWidth)).Append(" w ")
                          .Append(F(r.X)).Append(' ').Append(F(y)).Append(' ')
                          .Append(F(r.W)).Append(' ').Append(F(r.H)).Append(" re S\n");
                    break;
                }

                case TextItem t when t.Text.Length > 0:
                {
                    var baseline = style.Height - (t.Y + Baseline(t.Size));
                    sb.Append("BT /F1 ").Append(F(t.Size)).Append(" Tf ").Append(Rgb(t.Color, false));
                    if (t.Bold)
                        // 没有单独内嵌一份粗体：同一份字体描边加粗。省下几百 KB，
                        // 也省掉「粗体那份字体缺了某个字」这一类只在个别字上冒出来的错
                        sb.Append(Rgb(t.Color, true)).Append("2 Tr ").Append(F(t.Size * 0.035)).Append(" w ");
                    sb.Append("1 0 0 1 ").Append(F(t.X)).Append(' ').Append(F(baseline)).Append(" Tm ")
                      .Append(Hex(font, t.Text)).Append(" Tj ET\n");
                    if (t.Bold) sb.Append("BT 0 Tr ET\n");
                    break;
                }

                case ImageItem img when img.PixelWidth > 0 && img.PixelHeight > 0:
                {
                    var name = "Im" + (++n);
                    var rgb = Rgb24(img.Bgra, img.PixelWidth, img.PixelHeight);
                    var r = doc.Stream(rgb,
                        "/Type/XObject/Subtype/Image/Width " + img.PixelWidth
                        + "/Height " + img.PixelHeight + "/ColorSpace/DeviceRGB/BitsPerComponent 8");
                    images.Add((name, r));
                    var y = style.Height - img.Y - img.H;
                    sb.Append("q ").Append(F(img.W)).Append(" 0 0 ").Append(F(img.H)).Append(' ')
                      .Append(F(img.X)).Append(' ').Append(F(y)).Append(" cm /").Append(name).Append(" Do Q\n");
                    break;
                }
            }
        }
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>字号 → 基线到文字块顶的距离。**版面与两种渲染器共用这一处**，
    /// 差一点点，预览和 PDF 的每一行就都错开一点。</summary>
    public static double Baseline(double size) => size * 0.80;

    private static IEnumerable<(int Cp, int Gid)> Glyphs(TrueTypeFont font, string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            int cp = s[i];
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                cp = char.ConvertToUtf32(s[i], s[i + 1]);
                i++;
            }
            var gid = font.GidOf(cp);
            if (gid != 0) yield return (cp, gid);
        }
    }

    private static string Hex(TrueTypeFont font, string s)
    {
        var sb = new StringBuilder(s.Length * 4 + 2).Append('<');
        foreach (var (_, gid) in Glyphs(font, s)) sb.Append(gid.ToString("X4", CultureInfo.InvariantCulture));
        return sb.Append('>').ToString();
    }

    private static string ToUnicodeCMap(SortedDictionary<int, int> map)
    {
        var sb = new StringBuilder();
        sb.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n")
          .Append("/CIDSystemInfo <</Registry (Adobe) /Ordering (UCS) /Supplement 0>> def\n")
          .Append("/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n")
          .Append("1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");

        var items = map.ToList();
        for (var i = 0; i < items.Count; i += 100)
        {
            var chunk = items.Skip(i).Take(100).ToList();
            sb.Append(chunk.Count).Append(" beginbfchar\n");
            foreach (var (gid, cp) in chunk)
            {
                sb.Append('<').Append(gid.ToString("X4", CultureInfo.InvariantCulture)).Append("> <");
                foreach (var u in char.ConvertFromUtf32(cp))
                    sb.Append(((int)u).ToString("X4", CultureInfo.InvariantCulture));
                sb.Append(">\n");
            }
            sb.Append("endbfchar\n");
        }
        sb.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        return sb.ToString();
    }

    /// <summary>
    /// 子集前缀。PDF 规矩：内嵌的是子集就得在字体名前加六个大写字母 + 「+」，
    /// 阅读器据此知道「这一份不是完整字体，别拿它去渲染别的文档」。
    /// </summary>
    private static string SubsetTag(IEnumerable<int> gids)
    {
        unchecked
        {
            var h = 2166136261u;
            foreach (var g in gids) { h ^= (uint)g; h *= 16777619; }
            var sb = new StringBuilder(6);
            for (var i = 0; i < 6; i++) { sb.Append((char)('A' + h % 26)); h /= 26; }
            return sb.ToString();
        }
    }

    private static byte[] Rgb24(byte[] bgra, int w, int h)
    {
        var need = (long)w * h * 4;
        var rgb = new byte[(long)w * h * 3];
        for (long i = 0, o = 0; i < need; i += 4, o += 3)
        {
            if (i + 3 >= bgra.Length) break;
            var a = bgra[i + 3];
            if (a == 255)
            {
                rgb[o] = bgra[i + 2]; rgb[o + 1] = bgra[i + 1]; rgb[o + 2] = bgra[i];
            }
            else
            {
                // 半透明的部分压在白纸上：PDF 里不带 alpha 通道，
                // 不压的话透明处会印成黑的
                rgb[o] = (byte)(bgra[i + 2] * a / 255 + (255 - a));
                rgb[o + 1] = (byte)(bgra[i + 1] * a / 255 + (255 - a));
                rgb[o + 2] = (byte)(bgra[i] * a / 255 + (255 - a));
            }
        }
        return rgb;
    }

    private static string Scale(TrueTypeFont f, int v)
        => ((int)Math.Round(v * 1000.0 / f.UnitsPerEm)).ToString(CultureInfo.InvariantCulture);

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Rgb(string hex, bool stroke)
    {
        var (r, g, b) = ParseHex(hex);
        return $"{F(r / 255.0)} {F(g / 255.0)} {F(b / 255.0)} {(stroke ? "RG" : "rg")} ";
    }

    internal static (int R, int G, int B) ParseHex(string hex)
    {
        if (hex.Length == 7 && hex[0] == '#'
            && int.TryParse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r)
            && int.TryParse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g)
            && int.TryParse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
            return (r, g, b);
        return (0, 0, 0);
    }

    /// <summary>PDF 文本串。中文要写成带 BOM 的 UTF-16BE 十六进制串，不然是乱码。</summary>
    private static string Str(string s)
    {
        var sb = new StringBuilder("<FEFF");
        foreach (var c in s) sb.Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
        return sb.Append('>').ToString();
    }

    private static string Date(DateTimeOffset at)
    {
        var off = at.Offset;
        var sign = off < TimeSpan.Zero ? "-" : "+";
        return at.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)
             + sign + Math.Abs(off.Hours).ToString("00", CultureInfo.InvariantCulture)
             + "'" + Math.Abs(off.Minutes).ToString("00", CultureInfo.InvariantCulture) + "'";
    }

    // ── 对象表 ──────────────────────────────────────────────────────

    private sealed class PdfDoc
    {
        private readonly List<byte[]?> _objects = new();

        public int Obj(string body) => Obj(Encoding.ASCII.GetBytes(body));

        public int Obj(byte[] body)
        {
            _objects.Add(body);
            return _objects.Count;
        }

        /// <summary>先占个号，内容后填——页对象要引用页树，页树又要引用页对象。</summary>
        public int Reserve()
        {
            _objects.Add(null);
            return _objects.Count;
        }

        public void Fill(int id, string body) => _objects[id - 1] = Encoding.ASCII.GetBytes(body);

        /// <summary>一条压缩流。内容流、字体、图片都走它。</summary>
        public int Stream(byte[] data, string extra = "")
        {
            var packed = Deflate(data);
            var head = Encoding.ASCII.GetBytes(
                $"<</Length {packed.Length}/Filter/FlateDecode{extra}>>\nstream\n");
            var tail = Encoding.ASCII.GetBytes("\nendstream");
            var all = new byte[head.Length + packed.Length + tail.Length];
            head.CopyTo(all, 0);
            packed.CopyTo(all, head.Length);
            tail.CopyTo(all, head.Length + packed.Length);
            return Obj(all);
        }

        private static byte[] Deflate(byte[] data)
        {
            using var ms = new MemoryStream();
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                z.Write(data, 0, data.Length);
            return ms.ToArray();
        }

        public void WriteTo(Stream output, int catalog, int info)
        {
            var offsets = new long[_objects.Count + 1];
            using var w = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);
            long pos = 0;

            void Put(byte[] b) { w.Write(b); pos += b.Length; }
            void PutAscii(string s) => Put(Encoding.ASCII.GetBytes(s));

            // 头两行：第二行是二进制注释，告诉传输工具「这是二进制文件，别转换换行」
            PutAscii("%PDF-1.7\n");
            Put(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' });

            for (var i = 0; i < _objects.Count; i++)
            {
                offsets[i + 1] = pos;
                PutAscii($"{i + 1} 0 obj\n");
                Put(_objects[i] ?? Encoding.ASCII.GetBytes("<<>>"));
                PutAscii("\nendobj\n");
            }

            var xref = pos;
            PutAscii($"xref\n0 {_objects.Count + 1}\n");
            PutAscii("0000000000 65535 f \n");
            for (var i = 1; i <= _objects.Count; i++)
                PutAscii(offsets[i].ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");

            PutAscii($"trailer\n<</Size {_objects.Count + 1}/Root {catalog} 0 R/Info {info} 0 R>>\n");
            PutAscii($"startxref\n{xref}\n%%EOF\n");
            w.Flush();
        }
    }
}
