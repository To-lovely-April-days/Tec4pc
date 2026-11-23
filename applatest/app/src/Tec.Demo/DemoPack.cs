using Tec.Core.Compounds;
using Tec.Core.Persistence;

namespace Tec.Demo;

/// <summary>
/// 【本地改动】把演示工程拼成一份实验文件（.tec）：台面 + 两条泳道（配方 + 配料表）。
///
/// WHY：工作站打开实验的那条路（ExperimentStore.OpenAsync）已经把台面装回、通道重建、配料表盖章这些事做全了，
/// 演示模式顺着它走：先写出 .tec，再「打开」它——标题栏显示实验名、开始页「最近实验」里也有它，
/// 跟操作人自己存过一份实验是同一个状态，不另开一条装载路径。
/// </summary>
public static class DemoPack
{
    public const string ExperimentName = "演示实验 · 酯化合成与 pH 中和";

    /// <summary>当前演示实验 + 两份「以前做过」的实验，开始页最近列表有三张卡。</summary>
    public static readonly (string File, string Name, string Lane1, string Lane2)[] Experiments =
    {
        ("演示实验-酯化合成与pH中和", ExperimentName, "苯甲酸乙酯酯化合成", "pH 反馈中和（对乙酰氨基酚母液）"),
        ("对乙酰氨基酚重结晶-第3批", "对乙酰氨基酚重结晶 · 第 3 批", "降温结晶 · 60→5 ℃ 三段梯度", "阶梯升温 · 40 / 60 / 80 ℃ 各保温 30 min"),
        ("阿司匹林乙酰化-小试", "阿司匹林乙酰化 · 小试", "阿司匹林乙酰化 · 70 ℃ 保温 · 水淬结晶", "常压回流 · ΔT 8 K · 2 h"),
    };

    /// <summary>
    /// 写出全部演示实验文件，返回当前那份的路径。bench 由调用方给（工作站会先把插入件的画布位置算好）。
    /// </summary>
    public static string WriteExperiments(string experimentsDir, BenchDoc bench, IReadOnlyList<Compound> compounds)
    {
        Directory.CreateDirectory(experimentsDir);
        var lib = DemoRecipes.Library();
        string? current = null;
        for (var i = 0; i < Experiments.Length; i++)
        {
            var (file, name, lane1, lane2) = Experiments[i];
            var doc = new ExperimentDoc
            {
                Name = name, Author = DemoArchive.Operator,
                Notes = i == 0 ? "演示模式自动生成的实验：台面上的设备全部接在仿真驱动上。" : null,
                CreatedAt = new DateTimeOffset(2026, 11, 3 + i * 7, 9, 0, 0, TimeSpan.FromHours(8)),
                ModifiedAt = new DateTimeOffset(2026, 11, 5 + i * 7, 17, 30, 0, TimeSpan.FromHours(8)),
                Bench = bench
            };
            doc.Lanes.Add(new LaneDoc
            {
                Channel = 1, Name = lane1, Recipe = lib.First(r => r.Name == lane1).ToDoc(),
                Charge = i == 0 ? DemoCharges.Channel1(compounds).ToDoc() : null
            });
            doc.Lanes.Add(new LaneDoc
            {
                Channel = 2, Name = lane2, Recipe = lib.First(r => r.Name == lane2).ToDoc(),
                Charge = i == 0 ? DemoCharges.Channel2(compounds).ToDoc() : null
            });
            var path = Path.Combine(experimentsDir, file + TecFiles.ExperimentExt);
            TecFiles.Save(path, doc);
            // TecFiles.Save 会把 ModifiedAt 盖成现在；「以前做过」的两份改回它们的日期，最近列表才像有历史
            if (i > 0) File.SetLastWriteTime(path, doc.CreatedAt.DateTime.AddHours(8));
            current ??= path;
        }
        return current!;
    }
}
