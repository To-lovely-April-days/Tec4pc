using System.Text;

namespace Tec.Core.Export;

/// <summary>
/// TrueType 字体：查字形、量宽度、做子集。
///
/// **为什么非要自己解字体**：PDF 里写中文只有一条路——把字体内嵌进去。
/// 不内嵌就得指望阅读器自己装了对应的中文字体包，一份交给审计的报告
/// 在别人机器上变成一片方框，那还不如不出这个格式。
/// 而整份中文字体是十几兆，一炉一份报告谁也受不了，所以得做子集。
///
/// 只认 glyf 轮廓的字体（TrueType）。CFF 轮廓的（很多 .otf、思源黑体那一类）
/// 子集化是另一套完全不同的活，这里认出来就换下一个候选，不硬来。
/// </summary>
public sealed class TrueTypeFont
{
    private readonly byte[] _data;
    private readonly Dictionary<string, (int Off, int Len)> _tables = new(StringComparer.Ordinal);
    private readonly Dictionary<int, int> _cmap = new();
    private int _numHMetrics;
    private int _hmtxOff;
    private int _locaOff, _locaLen, _glyfOff, _glyfLen;
    private bool _longLoca;

    private TrueTypeFont(byte[] data, string path)
    {
        _data = data;
        Path = path;
    }

    public string Path { get; }
    public string FamilyName { get; private set; } = "";
    public string PostScriptName { get; private set; } = "";
    public int UnitsPerEm { get; private set; } = 1000;
    public int NumGlyphs { get; private set; }
    public short Ascender { get; private set; }
    public short Descender { get; private set; }
    public short XMin { get; private set; }
    public short YMin { get; private set; }
    public short XMax { get; private set; }
    public short YMax { get; private set; }
    public short CapHeight { get; private set; }
    public bool Bold { get; private set; }

    /// <summary>
    /// 能不能拿它排这份报告：汉字**和**西文数字都得有。
    ///
    /// 只查汉字是不够的——这台机器上第一个候选 Droid Sans Fallback 就是个纯 CJK
    /// 兜底字体，一个拉丁字母、一个阿拉伯数字都没有。用它排出来的报告，
    /// 「25.4 ℃」「CH1」「2026-08-17」全是空白，而汉字看着好好的，
    /// 光看一眼标题还发现不了。
    /// </summary>
    public bool Usable
    {
        get
        {
            foreach (var c in "温度实验0123456789ABCabc.:-")
                if (GidOf(c) == 0) return false;
            return true;
        }
    }

    // ── 读 ──────────────────────────────────────────────────────────

    public static TrueTypeFont? TryLoad(string path, int faceIndex = 0)
    {
        try
        {
            var data = File.ReadAllBytes(path);
            var f = new TrueTypeFont(data, path);
            return f.Parse(faceIndex) ? f : null;
        }
        catch { return null; }
    }

    private bool Parse(int faceIndex)
    {
        var start = 0;
        if (U32(0) == 0x74746366)             // 'ttcf'：字体集，里面摆着好几份
        {
            var n = (int)U32(8);
            if (faceIndex >= n) faceIndex = 0;
            start = (int)U32(12 + faceIndex * 4);
        }

        var sfnt = U32(start);
        if (sfnt != 0x00010000 && sfnt != 0x74727565) return false;   // OTTO（CFF 轮廓）在这里就退出

        var numTables = U16(start + 4);
        for (var i = 0; i < numTables; i++)
        {
            var rec = start + 12 + i * 16;
            if (rec + 16 > _data.Length) return false;
            var tag = Encoding.ASCII.GetString(_data, rec, 4);
            _tables[tag] = ((int)U32(rec + 8), (int)U32(rec + 12));
        }

        if (!_tables.ContainsKey("glyf") || !_tables.ContainsKey("loca")) return false;
        if (!Table("head", out var head) || head.Len < 54) return false;
        if (!Table("hhea", out var hhea) || hhea.Len < 36) return false;
        if (!Table("maxp", out var maxp) || maxp.Len < 6) return false;
        if (!Table("hmtx", out var hmtx)) return false;

        UnitsPerEm = U16(head.Off + 18);
        if (UnitsPerEm == 0) UnitsPerEm = 1000;
        XMin = S16(head.Off + 36); YMin = S16(head.Off + 38);
        XMax = S16(head.Off + 40); YMax = S16(head.Off + 42);
        Bold = (U16(head.Off + 44) & 1) != 0;
        _longLoca = U16(head.Off + 50) == 1;

        Ascender = S16(hhea.Off + 4);
        Descender = S16(hhea.Off + 6);
        _numHMetrics = U16(hhea.Off + 34);
        _hmtxOff = hmtx.Off;

        NumGlyphs = U16(maxp.Off + 4);
        if (NumGlyphs == 0) return false;

        var loca = _tables["loca"];
        _locaOff = loca.Off; _locaLen = loca.Len;
        var glyf = _tables["glyf"];
        _glyfOff = glyf.Off; _glyfLen = glyf.Len;
        var need = (NumGlyphs + 1) * (_longLoca ? 4 : 2);
        if (_locaLen < need) return false;

        CapHeight = (short)(Ascender * 0.72);
        if (Table("OS/2", out var os2) && os2.Len >= 90 && U16(os2.Off) >= 2)
        {
            var cap = S16(os2.Off + 88);
            if (cap > 0) CapHeight = cap;
        }

        ReadNames();
        ReadCmap();
        // 这里**不要求**有 cmap：子集化之后那张表就不写了（PDF 用 Identity 映射，
        // 用不上它），而自测要能把子集再读回来验一遍。「有没有中文」由
        // CoversChinese 单独回答，选字体的人看那个
        return true;
    }

    private bool Table(string tag, out (int Off, int Len) t)
    {
        if (_tables.TryGetValue(tag, out t) && t.Off >= 0 && t.Off + t.Len <= _data.Length) return true;
        t = default;
        return false;
    }

    private void ReadNames()
    {
        if (!Table("name", out var n) || n.Len < 6) return;
        var count = U16(n.Off + 2);
        var strOff = n.Off + U16(n.Off + 4);
        for (var i = 0; i < count; i++)
        {
            var rec = n.Off + 6 + i * 12;
            if (rec + 12 > _data.Length) break;
            var platform = U16(rec);
            var encoding = U16(rec + 2);
            var nameId = U16(rec + 6);
            if (nameId != 1 && nameId != 6) continue;
            var len = U16(rec + 8);
            var off = strOff + U16(rec + 10);
            if (off + len > _data.Length) continue;
            var text = platform == 3 || (platform == 0)
                ? Encoding.BigEndianUnicode.GetString(_data, off, len)
                : Encoding.ASCII.GetString(_data, off, len);
            if (nameId == 1 && FamilyName.Length == 0) FamilyName = text;
            // PostScript 名必须是 ASCII：中文字体的 name(1) 常常是「微软雅黑」，
            // 直接拿去当 PDF 的 /BaseFont 会写出一个非 ASCII 名字
            if (nameId == 6 && PostScriptName.Length == 0 && encoding != 0) PostScriptName = text;
            if (nameId == 6 && PostScriptName.Length == 0) PostScriptName = text;
        }
        if (PostScriptName.Length == 0) PostScriptName = FamilyName;
        PostScriptName = Ascii(PostScriptName);
        if (PostScriptName.Length == 0) PostScriptName = "TecFont";
    }

    private static string Ascii(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            if (c > 32 && c < 127 && c is not ('(' or ')' or '<' or '>' or '[' or ']' or '{' or '}' or '/' or '%' or '#'))
                sb.Append(c);
        return sb.ToString();
    }

    private void ReadCmap()
    {
        if (!Table("cmap", out var c) || c.Len < 4) return;
        var n = U16(c.Off + 2);

        // **把认得的子表全部并起来**，不是挑一张最好的。
        // 吃过亏：Droid Sans Fallback 的 (3,10) 格式 12 里只有 CJK，西文在
        // (3,1) 格式 4 里；只认前者的话，报告上的数字和 CH1 全成了方框。
        // 分数高的后并，覆盖前面的。
        var subs = new List<(int Score, int Off, int Format)>();
        for (var i = 0; i < n; i++)
        {
            var rec = c.Off + 4 + i * 8;
            if (rec + 8 > _data.Length) break;
            var platform = U16(rec);
            var encoding = U16(rec + 2);
            var sub = c.Off + (int)U32(rec + 4);
            if (sub + 2 > _data.Length) continue;
            var format = U16(sub);
            if (format is not (4 or 12)) continue;
            var score = (platform, encoding, format) switch
            {
                (3, 10, 12) => 5,
                (0, _, 12) => 4,
                (3, 1, 4) => 3,
                (0, _, 4) => 2,
                _ => 1
            };
            subs.Add((score, sub, format));
        }

        foreach (var (_, off, format) in subs.OrderBy(s => s.Score))
        {
            if (format == 4) ReadCmap4(off);
            else ReadCmap12(off);
        }
    }

    private void ReadCmap4(int off)
    {
        var segX2 = U16(off + 6);
        var seg = segX2 / 2;
        var endOff = off + 14;
        var startOff = endOff + segX2 + 2;
        var deltaOff = startOff + segX2;
        var rangeOff = deltaOff + segX2;

        for (var i = 0; i < seg; i++)
        {
            int end = U16(endOff + i * 2), begin = U16(startOff + i * 2);
            int delta = S16(deltaOff + i * 2), ro = U16(rangeOff + i * 2);
            if (begin > end || end == 0xFFFF && begin == 0xFFFF) continue;
            for (var ch = begin; ch <= end && ch <= 0xFFFF; ch++)
            {
                int gid;
                if (ro == 0) gid = (ch + delta) & 0xFFFF;
                else
                {
                    var addr = rangeOff + i * 2 + ro + (ch - begin) * 2;
                    if (addr + 2 > _data.Length) continue;
                    gid = U16(addr);
                    if (gid != 0) gid = (gid + delta) & 0xFFFF;
                }
                if (gid != 0 && gid < NumGlyphs) _cmap[ch] = gid;
            }
        }
    }

    private void ReadCmap12(int off)
    {
        var groups = (int)U32(off + 12);
        for (var i = 0; i < groups; i++)
        {
            var g = off + 16 + i * 12;
            if (g + 12 > _data.Length) break;
            var begin = (int)U32(g);
            var end = (int)U32(g + 4);
            var gid = (int)U32(g + 8);
            // 一段几十万码位的组是有的（未映射的兜底段），别把内存吃光
            if (end - begin > 0x30000) end = begin + 0x30000;
            for (var ch = begin; ch <= end; ch++)
            {
                var id = gid + (ch - begin);
                if (id > 0 && id < NumGlyphs) _cmap[ch] = id;
            }
        }
    }

    // ── 查询 ────────────────────────────────────────────────────────

    /// <summary>码位 → 字形号。没有这个字就是 0（.notdef）。</summary>
    public int GidOf(int codepoint) => _cmap.TryGetValue(codepoint, out var g) ? g : 0;

    /// <summary>字形宽度（字体单位）。</summary>
    public int AdvanceOf(int gid)
    {
        if (_numHMetrics == 0) return UnitsPerEm / 2;
        var i = gid < _numHMetrics ? gid : _numHMetrics - 1;
        var off = _hmtxOff + i * 4;
        return off + 2 <= _data.Length ? U16(off) : UnitsPerEm / 2;
    }

    /// <summary>宽度换算到 PDF 的千分之一 em。</summary>
    public int Width1000(int gid) => (int)Math.Round(AdvanceOf(gid) * 1000.0 / UnitsPerEm);

    // ── 子集 ────────────────────────────────────────────────────────

    /// <summary>
    /// 只留用到的那些字形，其余留空。
    ///
    /// **字形号不重排**：留空的字形在 loca 里首尾偏移相同，占 0 字节。
    /// 重排能再省下 loca / hmtx 那百来 KB，但要同步改 PDF 里的 CID 映射、
    /// 复合字形里的分量号——为了一点体积换一整类难查的错，不划算。
    /// 一份中文字体十几兆，这么做之后是几百 KB，够了。
    /// </summary>
    public byte[] Subset(IEnumerable<int> gids)
    {
        var keep = new HashSet<int> { 0 };
        foreach (var g in gids) if (g > 0 && g < NumGlyphs) keep.Add(g);

        // 复合字形引用别的字形，被引用的那些也得留下，否则那个字画出来是半个
        var queue = new Queue<int>(keep);
        while (queue.Count > 0)
            foreach (var comp in Components(queue.Dequeue()))
                if (keep.Add(comp)) queue.Enqueue(comp);

        var glyf = new MemoryStream();
        var loca = new int[NumGlyphs + 1];
        for (var gid = 0; gid < NumGlyphs; gid++)
        {
            loca[gid] = (int)glyf.Length;
            if (!keep.Contains(gid)) continue;
            var (off, len) = GlyphRange(gid);
            if (len <= 0) continue;
            glyf.Write(_data, _glyfOff + off, len);
            while (glyf.Length % 4 != 0) glyf.WriteByte(0);
        }
        loca[NumGlyphs] = (int)glyf.Length;

        var locaBytes = new byte[(NumGlyphs + 1) * 4];      // 一律用长格式，省去两种分支
        for (var i = 0; i <= NumGlyphs; i++) Put32(locaBytes, i * 4, (uint)loca[i]);

        var head = Copy("head");
        Put16(head, 50, 1);                                  // indexToLocFormat = 长
        Put32(head, 8, 0);                                   // checkSumAdjustment 交给最后算

        var parts = new List<(string Tag, byte[] Data)>
        {
            ("glyf", glyf.ToArray()),
            ("head", head),
            ("hhea", Copy("hhea")),
            ("hmtx", Copy("hmtx")),
            ("loca", locaBytes),
            ("maxp", Copy("maxp"))
        };
        // 指令表照抄。没有它字体在低分辨率下发虚，有它也不影响正确性
        foreach (var tag in new[] { "cvt ", "fpgm", "prep" })
            if (Table(tag, out _)) parts.Add((tag, Copy(tag)));

        return Build(parts);
    }

    private IEnumerable<int> Components(int gid)
    {
        var (off, len) = GlyphRange(gid);
        if (len < 10) yield break;
        var p = _glyfOff + off;
        if (S16(p) >= 0) yield break;                        // 轮廓数 ≥ 0 = 简单字形
        p += 10;
        while (true)
        {
            if (p + 4 > _data.Length) yield break;
            var flags = U16(p);
            var index = U16(p + 2);
            yield return index;
            p += 4;
            p += (flags & 0x0001) != 0 ? 4 : 2;              // ARG_1_AND_2_ARE_WORDS
            if ((flags & 0x0008) != 0) p += 2;               // WE_HAVE_A_SCALE
            else if ((flags & 0x0040) != 0) p += 4;          // X_AND_Y_SCALE
            else if ((flags & 0x0080) != 0) p += 8;          // TWO_BY_TWO
            if ((flags & 0x0020) == 0) yield break;          // MORE_COMPONENTS
        }
    }

    private (int Off, int Len) GlyphRange(int gid)
    {
        int a, b;
        if (_longLoca)
        {
            a = (int)U32(_locaOff + gid * 4);
            b = (int)U32(_locaOff + gid * 4 + 4);
        }
        else
        {
            a = U16(_locaOff + gid * 2) * 2;
            b = U16(_locaOff + gid * 2 + 2) * 2;
        }
        if (b <= a || a < 0 || b > _glyfLen) return (0, 0);
        return (a, b - a);
    }

    private byte[] Copy(string tag)
    {
        var t = _tables[tag];
        var b = new byte[t.Len];
        Array.Copy(_data, t.Off, b, 0, t.Len);
        return b;
    }

    private static byte[] Build(List<(string Tag, byte[] Data)> parts)
    {
        parts.Sort((a, b) => string.CompareOrdinal(a.Tag, b.Tag));
        var n = parts.Count;
        var entrySelector = (int)Math.Floor(Math.Log2(n));
        var searchRange = (int)Math.Pow(2, entrySelector) * 16;

        var head = new byte[12 + n * 16];
        Put32(head, 0, 0x00010000);
        Put16(head, 4, (ushort)n);
        Put16(head, 6, (ushort)searchRange);
        Put16(head, 8, (ushort)entrySelector);
        Put16(head, 10, (ushort)(n * 16 - searchRange));

        var offset = head.Length;
        var body = new MemoryStream();
        for (var i = 0; i < n; i++)
        {
            var (tag, data) = parts[i];
            var rec = 12 + i * 16;
            Encoding.ASCII.GetBytes(tag).CopyTo(head, rec);
            Put32(head, rec + 4, Checksum(data));
            Put32(head, rec + 8, (uint)offset);
            Put32(head, rec + 12, (uint)data.Length);
            body.Write(data, 0, data.Length);
            var pad = (4 - data.Length % 4) % 4;
            for (var k = 0; k < pad; k++) body.WriteByte(0);
            offset += data.Length + pad;
        }

        var all = new byte[head.Length + body.Length];
        head.CopyTo(all, 0);
        body.ToArray().CopyTo(all, head.Length);

        // head 表里的 checkSumAdjustment：0xB1B0AFBA 减去整份文件的校验和
        var headIndex = parts.FindIndex(p => p.Tag == "head");
        if (headIndex >= 0)
        {
            var headOff = (int)U32At(head, 12 + headIndex * 16 + 8);
            Put32(all, headOff + 8, unchecked(0xB1B0AFBA - Checksum(all)));
        }
        return all;
    }

    private static uint Checksum(byte[] data)
    {
        uint sum = 0;
        for (var i = 0; i < data.Length; i += 4)
        {
            uint v = 0;
            for (var k = 0; k < 4; k++) v = (v << 8) | (i + k < data.Length ? data[i + k] : 0u);
            unchecked { sum += v; }
        }
        return sum;
    }

    // ── 大端小工具 ──────────────────────────────────────────────────

    private ushort U16(int o) => o + 2 <= _data.Length ? (ushort)((_data[o] << 8) | _data[o + 1]) : (ushort)0;
    private short S16(int o) => unchecked((short)U16(o));
    private uint U32(int o) => o + 4 <= _data.Length
        ? ((uint)_data[o] << 24) | ((uint)_data[o + 1] << 16) | ((uint)_data[o + 2] << 8) | _data[o + 3]
        : 0;
    private static uint U32At(byte[] b, int o)
        => ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];
    private static void Put16(byte[] b, int o, ushort v) { b[o] = (byte)(v >> 8); b[o + 1] = (byte)v; }
    private static void Put32(byte[] b, int o, uint v)
    {
        b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
    }
}
