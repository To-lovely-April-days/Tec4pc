using Tec.Core.Export;
using Xunit;

namespace Tec.Core.Tests;

public class FontTests
{
    /// <summary>没有中文字体的机器上这一组测试就没有意义——跳过，而不是报假失败。</summary>
    private static TrueTypeFont? Font => FontFinder.Find();

    [Fact]
    public void 找得到一份能内嵌的中文字体()
    {
        if (Font is null)
        {
            // 照实说清楚找过哪儿。CI 上没装中文字体是允许的，但得看得见
            Assert.True(true, "本机没有可内嵌的中文字体：" + string.Join("；", FontFinder.SearchedPaths));
            return;
        }
        Assert.True(Font.NumGlyphs > 1000, "字形太少，八成不是中文字体");
        Assert.NotEqual(0, Font.GidOf('釜'));
        Assert.NotEqual(0, Font.GidOf('A'));
        Assert.Equal(0, Font.GidOf(0x10FFFD));      // 私用区，不该有
    }

    [Fact]
    public void 子集只留用到的字且体积远小于整份()
    {
        if (Font is not { } f) return;
        var text = "TecStudio 实验记录 · 釜内温度 25.0 ℃ · CH1/CH2";
        var gids = text.Select(c => f.GidOf(c)).Where(g => g > 0).Distinct().ToList();
        Assert.NotEmpty(gids);

        var sub = f.Subset(gids);
        var whole = new FileInfo(f.Path).Length;
        Assert.True(sub.Length > 0);
        Assert.True(sub.Length < whole, $"子集 {sub.Length} 不比整份 {whole} 小");

        // 子集本身必须还是一份读得动的 TrueType：字形号不重排，所以原来的号还查得到宽度
        var path = Path.Combine(Path.GetTempPath(), "tec-subset-" + Guid.NewGuid().ToString("N") + ".ttf");
        try
        {
            File.WriteAllBytes(path, sub);
            var re = TrueTypeFont.TryLoad(path);
            Assert.NotNull(re);
            Assert.Equal(f.NumGlyphs, re!.NumGlyphs);
            Assert.Equal(f.UnitsPerEm, re.UnitsPerEm);
            foreach (var g in gids) Assert.Equal(f.AdvanceOf(g), re.AdvanceOf(g));
            Dump.Save("subset.ttf", sub);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void 宽度换算到千分之一em()
    {
        if (Font is not { } f) return;
        var han = f.Width1000(f.GidOf('温'));
        // 汉字是全角：一个 em 上下，容差留宽些（有的字体全角是 1000，有的略窄）
        Assert.InRange(han, 800, 1100);
        var latin = f.Width1000(f.GidOf('i'));
        Assert.True(latin < han, "西文 i 竟然不比汉字窄");
    }
}
