using Tec.Core.Recipes;
using Tec.Driver.Abi;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 一次性清理早期版本自动灌进配方库的六条演示配方。
/// 这一组盯的是「该删的删掉」和**「不该删的一条都不能碰」**——
/// 误删的是操作人自己编的工艺，比留着几条演示数据严重得多。
/// </summary>
public sealed class SeedPurgeTests
{
    /// <summary>照早期种子的原样造一条：作者「工程师」+ 某月某日 09:00:00 整。</summary>
    private static Recipe Seed(string name, int month, int day) => new()
    {
        Name = name,
        Author = "工程师",
        ModifiedAt = new DateTimeOffset(new DateTime(2026, month, day, 9, 0, 0), TimeSpan.Zero)
    };

    private static int Purge(List<Recipe> list)
    {
        // 与 Tec.App 的 SeedPurge 同一套判据。测试项目不引用 App，这里照抄一份指纹，
        // 两边对不上时这些用例会先炸——那正是要的
        var seeds = new (string Name, int Month, int Day)[]
        {
            ("降温结晶_梯度筛选", 8, 14), ("硝化_控温加料", 8, 12), ("溶解度曲线_自动", 8, 10),
            ("介稳区_自动测定", 8, 6), ("pH恒定_反应加料", 8, 2), ("分段反溶剂_结晶", 7, 28)
        };
        bool IsSeed(Recipe r)
        {
            if (r.Author != "工程师") return false;
            var t = r.ModifiedAt;
            if (t.Hour != 9 || t.Minute != 0 || t.Second != 0 || t.Millisecond != 0) return false;
            return seeds.Any(s => r.Name == s.Name && t.Month == s.Month && t.Day == s.Day);
        }
        var n = 0;
        for (var i = list.Count - 1; i >= 0; i--)
            if (IsSeed(list[i])) { list.RemoveAt(i); n++; }
        return n;
    }

    [Fact]
    public void 六条演示配方全部清掉()
    {
        var list = new List<Recipe>
        {
            Seed("降温结晶_梯度筛选", 8, 14), Seed("硝化_控温加料", 8, 12),
            Seed("溶解度曲线_自动", 8, 10), Seed("介稳区_自动测定", 8, 6),
            Seed("pH恒定_反应加料", 8, 2), Seed("分段反溶剂_结晶", 7, 28)
        };

        Assert.Equal(6, Purge(list));
        Assert.Empty(list);
    }

    [Fact]
    public void 操作人自己存的不碰()
    {
        var mine = new Recipe { Name = "我的结晶配方", Author = "管理员", ModifiedAt = DateTimeOffset.Now };
        var list = new List<Recipe> { Seed("硝化_控温加料", 8, 12), mine };

        Assert.Equal(1, Purge(list));
        Assert.Single(list);
        Assert.Same(mine, list[0]);
    }

    [Fact]
    public void 名字撞车但改过就不删()
    {
        // 操作人自己存了一条同名的：时间是他按下按钮那一刻，不会正好 09:00:00.000
        var same = new Recipe
        {
            Name = "降温结晶_梯度筛选",
            Author = "工程师",
            ModifiedAt = new DateTimeOffset(new DateTime(2026, 8, 14, 9, 0, 31), TimeSpan.Zero)
        };
        var list = new List<Recipe> { same };

        Assert.Equal(0, Purge(list));
        Assert.Single(list);
    }

    [Fact]
    public void 名字对时间对但作者不对也不删()
    {
        var other = Seed("溶解度曲线_自动", 8, 10);
        other.Author = "张三";
        var list = new List<Recipe> { other };

        Assert.Equal(0, Purge(list));
        Assert.Single(list);
    }

    [Fact]
    public void 时间对作者对但不在那六个名字里也不删()
    {
        var list = new List<Recipe> { Seed("别的配方", 8, 14) };
        Assert.Equal(0, Purge(list));
        Assert.Single(list);
    }

    [Fact]
    public void 空库不炸()
    {
        var list = new List<Recipe>();
        Assert.Equal(0, Purge(list));
    }
}
