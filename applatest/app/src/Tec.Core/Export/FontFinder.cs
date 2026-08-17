namespace Tec.Core.Export;

/// <summary>
/// 找一份能内嵌进 PDF 的中文字体。
///
/// 只找系统自带的：随程序打包一份中文字体是十几兆，而且几乎每一份的授权
/// 都不允许再分发。用系统自带的那份就没有这两个问题——Windows 上必有雅黑或黑体。
///
/// 一份都找不到时**不出 PDF**，照实说找不到字体。退回英文字体的话，
/// 整份报告会变成一片方框，那比没有更糟。
/// </summary>
public static class FontFinder
{
    private static readonly object Gate = new();
    private static TrueTypeFont? _cached;
    private static bool _looked;

    /// <summary>候选顺序：先挑界面上用的那几款，字形风格才和程序里看到的一致。</summary>
    private static IEnumerable<(string Path, int Face)> Candidates()
    {
        if (OperatingSystem.IsWindows())
        {
            var f = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
            yield return (Path.Combine(f, "msyh.ttc"), 0);      // 微软雅黑
            yield return (Path.Combine(f, "msyh.ttf"), 0);
            yield return (Path.Combine(f, "Deng.ttf"), 0);      // 等线
            yield return (Path.Combine(f, "simhei.ttf"), 0);    // 黑体
            yield return (Path.Combine(f, "simsun.ttc"), 0);    // 宋体
            yield return (Path.Combine(f, "simkai.ttf"), 0);    // 楷体
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return ("/Library/Fonts/Arial Unicode.ttf", 0);
            yield return ("/System/Library/Fonts/STHeiti Medium.ttc", 0);
            yield return ("/System/Library/Fonts/Hiragino Sans GB.ttc", 0);
        }
        else
        {
            yield return ("/usr/share/fonts/truetype/droid/DroidSansFallbackFull.ttf", 0);
            yield return ("/usr/share/fonts/truetype/wqy/wqy-zenhei.ttc", 0);
            yield return ("/usr/share/fonts/truetype/wqy/wqy-microhei.ttc", 0);
            yield return ("/usr/share/fonts/truetype/arphic/uming.ttc", 0);
            yield return ("/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc", 0);
        }
    }

    /// <summary>找不到时告诉调用方找过哪些路径——「PDF 导不出来」得说得出为什么。</summary>
    public static IReadOnlyList<string> SearchedPaths => Candidates().Select(c => c.Path).ToList();

    public static TrueTypeFont? Find()
    {
        lock (Gate)
        {
            if (_looked) return _cached;
            _looked = true;
            foreach (var (path, face) in Candidates())
            {
                if (!File.Exists(path)) continue;
                var f = TrueTypeFont.TryLoad(path, face);
                // 认出来是 CFF 轮廓（TryLoad 返回 null），或者中文 / 西文缺一样，就换下一个
                if (f is null || !f.Usable) continue;
                _cached = f;
                break;
            }
            return _cached;
        }
    }

    /// <summary>自测用：换一份字体或清掉缓存。</summary>
    internal static void Override(TrueTypeFont? font)
    {
        lock (Gate) { _cached = font; _looked = font is not null; }
    }
}
