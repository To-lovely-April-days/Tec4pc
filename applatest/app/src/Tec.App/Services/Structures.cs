using Tec.App.Controls;

namespace Tec.App.Services;

/// <summary>
/// 化合物的骨架式画法。坐标是键长 1 的单位格子，y 轴向下（跟控件坐标一致）。
/// 不写标签的顶点是碳，碳上的氢不画——骨架式的惯例。
/// 离子化合物没有骨架，走 IonText 排离子对。
/// </summary>
public static class Structures
{
    /// <summary>苯环六个顶点，Kekulé 式三条双键。</summary>
    private static (double X, double Y, string? L)[] Ring(double cx, double cy) => new (double, double, string?)[]
    {
        (cx + 1, cy, null), (cx + 0.5, cy + 0.866, null), (cx - 0.5, cy + 0.866, null),
        (cx - 1, cy, null), (cx - 0.5, cy - 0.866, null), (cx + 0.5, cy - 0.866, null)
    };

    private static readonly (int, int, int)[] RingBonds =
    { (0, 1, 2), (1, 2, 1), (2, 3, 2), (3, 4, 1), (4, 5, 2), (5, 0, 1) };

    private static Molecule Build((double, double, string?)[] extra, (int, int, int)[] extraBonds)
    {
        var atoms = new List<(double, double, string?)>(Ring(0, 0));
        atoms.AddRange(extra);
        var bonds = new List<(int, int, int)>(RingBonds);
        bonds.AddRange(extraBonds);
        return new Molecule { Atoms = atoms, Bonds = bonds };
    }

    /// <summary>苯甲酸：苯环 + 羧基。</summary>
    public static Molecule BenzoicAcid => Build(
        new (double, double, string?)[] { (1.87, -0.5, null), (2.74, 0, "O"), (1.87, -1.5, "OH") },
        new[] { (0, 6, 1), (6, 7, 2), (6, 8, 1) });

    /// <summary>水杨酸：苯甲酸的邻位再挂一个酚羟基。</summary>
    public static Molecule SalicylicAcid => Build(
        new (double, double, string?)[]
        { (1.87, -0.5, null), (2.74, 0, "O"), (1.87, -1.5, "OH"), (1.0, 1.732, "OH") },
        new[] { (0, 6, 1), (6, 7, 2), (6, 8, 1), (1, 9, 1) });

    /// <summary>对乙酰氨基酚：对位酚羟基 + 乙酰氨基。</summary>
    public static Molecule Paracetamol => Build(
        new (double, double, string?)[]
        { (-2, 0, "HO"), (2, 0, "NH"), (2.87, 0.5, null), (2.87, 1.5, "O"), (3.74, 0, null) },
        new[] { (3, 6, 1), (0, 7, 1), (7, 8, 1), (8, 9, 2), (8, 10, 1) });

    /// <summary>布洛芬：异丁基 + 丙酸侧链。</summary>
    public static Molecule Ibuprofen => Build(
        new (double, double, string?)[]
        {
            (-1.87, -0.5, null), (-2.74, 0, null), (-3.61, -0.5, null), (-2.74, 1, null),
            (1.87, -0.5, null), (1.87, -1.5, null), (2.74, 0, null), (3.61, -0.5, "O"), (2.74, 1, "OH")
        },
        new[]
        {
            (3, 6, 1), (6, 7, 1), (7, 8, 1), (7, 9, 1),
            (0, 10, 1), (10, 11, 1), (10, 12, 1), (12, 13, 2), (12, 14, 1)
        });

    /// <summary>柠檬酸：中心季碳带羟基，两条亚甲基各接一个羧基，中心再接一个。</summary>
    public static Molecule CitricAcid => new()
    {
        Atoms = new (double, double, string?)[]
        {
            (0, 0, null), (0, -1, "OH"),
            (-0.87, 0.5, null), (-1.74, 0, null), (-2.61, 0.5, "O"), (-1.74, -1, "HO"),
            (0.87, 0.5, null), (1.74, 0, null), (2.61, 0.5, "O"), (1.74, -1, "OH"),
            (0, 1, null), (-0.87, 1.5, "O"), (0.87, 1.5, "OH")
        },
        Bonds = new[]
        {
            (0, 1, 1), (0, 2, 1), (2, 3, 1), (3, 4, 2), (3, 5, 1),
            (0, 6, 1), (6, 7, 1), (7, 8, 2), (7, 9, 1),
            (0, 10, 1), (10, 11, 2), (10, 12, 1)
        }
    };

    /// <summary>甘氨酸：最简单的氨基酸。</summary>
    public static Molecule Glycine => new()
    {
        Atoms = new (double, double, string?)[]
        { (0, 0, "H₂N"), (0.87, 0.5, null), (1.74, 0, null), (2.61, 0.5, "O"), (1.74, -1, "OH") },
        Bonds = new[] { (0, 1, 1), (1, 2, 1), (2, 3, 2), (2, 4, 1) }
    };

    /// <summary>L-谷氨酸：α-氨基 + 两端羧基。</summary>
    public static Molecule GlutamicAcid => new()
    {
        Atoms = new (double, double, string?)[]
        {
            (-0.87, 0.5, "HO"), (0, 0, null), (0, -1, "O"),
            (0.87, 0.5, null), (0.87, 1.5, "NH₂"),
            (1.74, 0, null), (2.61, 0.5, null),
            (3.48, 0, null), (3.48, -1, "O"), (4.35, 0.5, "OH")
        },
        Bonds = new[]
        {
            (0, 1, 1), (1, 2, 2), (1, 3, 1), (3, 4, 1), (3, 5, 1),
            (5, 6, 1), (6, 7, 1), (7, 8, 2), (7, 9, 1)
        }
    };

    /// <summary>
    /// 蔗糖：α-D-吡喃葡萄糖 1→2 连 β-D-呋喃果糖，中间是苷键氧。
    /// 环上的羟基取向不画（骨架式简化），只保留连接关系。
    /// </summary>
    public static Molecule Sucrose => new()
    {
        Atoms = new (double, double, string?)[]
        {
            // 吡喃环：C1 C2 C3 C4 C5 + 环氧
            (1, 0, null), (0.5, 0.866, null), (-0.5, 0.866, null),
            (-1, 0, null), (-0.5, -0.866, null), (0.5, -0.866, "O"),
            (1.0, 1.732, "OH"), (-1.0, 1.732, "OH"), (-2, 0, "HO"), (-1.0, -1.732, "HOH₂C"),
            // 苷键氧
            (2, 0, "O"),
            // 呋喃环：C2 环氧 C5 C4 C3
            (3.0, 0.0, null), (3.95, -0.31, "O"), (4.54, 0.50, null),
            (3.95, 1.31, null), (3.0, 1.0, null),
            (3.0, -1.0, "CH₂OH"), (2.2, 1.59, "OH"), (4.3, 2.25, "OH"), (5.5, 0.6, "CH₂OH")
        },
        Bonds = new[]
        {
            (0, 1, 1), (1, 2, 1), (2, 3, 1), (3, 4, 1), (4, 5, 1), (5, 0, 1),
            (1, 6, 1), (2, 7, 1), (3, 8, 1), (4, 9, 1),
            (0, 10, 1), (10, 11, 1),
            (11, 12, 1), (12, 13, 1), (13, 14, 1), (14, 15, 1), (15, 11, 1),
            (11, 16, 1), (15, 17, 1), (14, 18, 1), (13, 19, 1)
        }
    };
}
