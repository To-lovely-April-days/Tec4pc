namespace Tec.App.Controls;

/// <summary>
/// 骨架式的画法数据。坐标用键长 1 的单位格子，渲染时统一缩放居中。
/// 顶点不写标签的就是碳（骨架式的惯例：碳与碳上的氢都不画）。
/// </summary>
public sealed class Molecule
{
    /// <summary>顶点：位置 + 可选原子标签（O、N、OH、NH₂…）。</summary>
    public required IReadOnlyList<(double X, double Y, string? Label)> Atoms { get; init; }

    /// <summary>键：两个顶点下标 + 键级（1 单键，2 双键）。</summary>
    public required IReadOnlyList<(int A, int B, int Order)> Bonds { get; init; }

    /// <summary>苯环画内圈还是三条双键——这里按三条双键画，用键级 2 表示。</summary>
    public static Molecule Of((double, double, string?)[] atoms, (int, int, int)[] bonds)
        => new() { Atoms = atoms, Bonds = bonds };
}
