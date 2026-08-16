namespace Tec.Core.Compounds;

/// <summary>
/// 一条化合物的物性数据。
///
/// 这是**参考数据**，不是某一份实验的数据——苯甲酸的分子量在哪台机器上都一样，
/// 所以它跟配方库一样存在全局库里（`LibraryDb`），不写进 .tec 文件。
///
/// 骨架式的画法不进库：那是程序自带的图形资源，库里只记一个键
/// （<see cref="StructureKey"/>），界面拿键去取图。把一堆坐标存进数据库，
/// 换个版本改了画法就对不上了。
/// </summary>
public sealed class Compound
{
    /// <summary>CAS 号。库里按它认人——名字会有别名，CAS 号是唯一的。</summary>
    public string Cas { get; set; } = "";

    public string Name { get; set; } = "";
    public string Formula { get; set; } = "";

    /// <summary>分子量 g/mol。</summary>
    public double Mw { get; set; }

    /// <summary>熔点 ℃。</summary>
    public double Mp { get; set; }

    public string Category { get; set; } = "";
    public string Solvent { get; set; } = "";
    public string Note { get; set; } = "";

    /// <summary>溶解度对温度的二次拟合系数 a + b·T + c·T²（g/100 mL 水）。</summary>
    public double[] Solubility { get; set; } = Array.Empty<double>();

    /// <summary>内置骨架式的键。空 = 没有骨架式（离子化合物走 <see cref="IonText"/>）。</summary>
    public string? StructureKey { get; set; }

    /// <summary>离子对，如「K⁺ + Cl⁻」。离子化合物没有骨架式，排这个。</summary>
    public string? IonText { get; set; }

    public Compound Clone() => new()
    {
        Cas = Cas,
        Name = Name,
        Formula = Formula,
        Mw = Mw,
        Mp = Mp,
        Category = Category,
        Solvent = Solvent,
        Note = Note,
        Solubility = (double[])Solubility.Clone(),
        StructureKey = StructureKey,
        IonText = IonText
    };
}
