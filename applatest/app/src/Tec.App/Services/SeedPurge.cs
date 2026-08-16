using Tec.Core.Recipes;

namespace Tec.App.Services;

/// <summary>
/// 一次性清理：把早期版本自动灌进配方库的六条演示配方删掉。
///
/// 那六条是原型 RECIPELIB 照搬来的示例，程序启动时自己写进去的——操作人一条也没编过。
/// 生成的代码已经删了，但它们已经落到 library.json 和存过盘的实验文件里，
/// 光"以后不再生成"是清不掉的。
///
/// **按指纹删，不按名字删。** 只有名称、作者、修改时间三样同时对上早期种子的原样
/// （作者「工程师」、时间是某月某日 09:00:00 整点）才删。操作人自己存的配方，
/// 时间是他按下按钮的那一刻，不可能正好是 09:00:00.000——这一条把误删挡住了。
/// 名字撞车也不要紧：只要他动过一次，修改时间就对不上。
/// </summary>
public static class SeedPurge
{
    private static readonly (string Name, int Month, int Day)[] Seeds =
    {
        ("降温结晶_梯度筛选", 8, 14),
        ("硝化_控温加料", 8, 12),
        ("溶解度曲线_自动", 8, 10),
        ("介稳区_自动测定", 8, 6),
        ("pH恒定_反应加料", 8, 2),
        ("分段反溶剂_结晶", 7, 28)
    };

    /// <summary>就地清理，返回删掉几条。</summary>
    public static int Apply(IList<Recipe> library)
    {
        var removed = 0;
        for (var i = library.Count - 1; i >= 0; i--)
        {
            if (!IsSeed(library[i])) continue;
            library.RemoveAt(i);
            removed++;
        }
        return removed;
    }

    private static bool IsSeed(Recipe r)
    {
        if (r.Author != "工程师") return false;
        var t = r.ModifiedAt;
        if (t.Hour != 9 || t.Minute != 0 || t.Second != 0 || t.Millisecond != 0) return false;
        foreach (var s in Seeds)
            if (r.Name == s.Name && t.Month == s.Month && t.Day == s.Day)
                return true;
        return false;
    }
}
