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
    /// <summary>
    /// CAS 号，同时是库里的主键——名字会有别名，CAS 号是唯一的。
    ///
    /// 但**这台装置真正跑的中间体多半没有 CAS**（内部代号，公开库里查不到）。
    /// 那些条目主键位上坐着的是 <see cref="KeyOf"/> 造出来的内部键，不是 CAS，
    /// 界面上不能把它摆在「CAS 号」那一列底下——先问 <see cref="HasCas"/>。
    /// </summary>
    public string Cas { get; set; } = "";

    /// <summary>内部键的前缀。真的 CAS 号只有数字和连字符，# 不会撞上。</summary>
    public const char KeyMark = '#';

    /// <summary>这一条到底有没有 CAS 号。没有的话 <see cref="Cas"/> 里是内部键。</summary>
    public bool HasCas => Cas.Length > 0 && Cas[0] != KeyMark;

    /// <summary>
    /// 按名字造一个内部键。**要能重复算出同一个值**：同一份 CSV 导第二遍时，
    /// 没有 CAS 的那几条得认回库里原来那一条去更新，而不是每导一次多一条。
    /// </summary>
    public static string KeyOf(string name) => KeyMark + name.Trim();

    /// <summary>造一个谁都不会撞上的内部键。界面上新建空白条用。</summary>
    public static string NewKey() => KeyMark + Guid.NewGuid().ToString("N")[..8];

    public string Name { get; set; } = "";
    public string Formula { get; set; } = "";

    /// <summary>摩尔质量 g/mol。空 = 没填。</summary>
    public double? Mw { get; set; }

    /// <summary>
    /// 熔点 ℃。空 = 没填。
    ///
    /// **不能拿 0 当「没填」**：水的熔点就是 0 ℃。表格里显示成 0.0 等于替这条料
    /// 断言了一个它没有的值，而这一列正是拿来判「这东西室温下是固体还是液体」的。
    /// </summary>
    public double? Mp { get; set; }

    // ── 下面几项都用可空的 double：**「没填」和「填了 0」必须分得开**。
    //    密度没填就把质量换算成体积会得到无穷大；当成 0 会得到「不用加」。
    //    两种都是错的，而且错得不一样——所以宁可让它空着，用的地方照实说「缺密度」。

    /// <summary>密度 g/mL（室温）。**配料表把质量换成加料体积就靠它**，泵下发的是体积。</summary>
    public double? Density { get; set; }

    /// <summary>沸点 ℃。</summary>
    public double? Bp { get; set; }

    /// <summary>
    /// 纯度 %。当量计算要用：65 % 的硝酸和 100 % 的硝酸，同样称 10 g 差着三分之一的物质的量。
    /// 空 = 没填，算的时候按 100 % 计并在结果里注明，不悄悄假设。
    /// </summary>
    public double? Purity { get; set; }

    /// <summary>比热容 J/(g·K)。量热要用（FR-5.13）；现在只存着，还没有地方消费它。</summary>
    public double? Cp { get; set; }

    /// <summary>
    /// 相态：「固」「液」，空 = 没填。**指纯品常温常压下的形态**，是参考数据——
    /// 配料行上另有一个相态（这一炉以什么形态投料）：固体配成溶液就能走泵，
    /// 所以行上那个才是校验用的，这里只做连库时的默认值。
    /// 没填就不校验，**不从熔点瞎猜**：猜错一次拦一炉好工艺，代价不对等。
    /// </summary>
    public string Phase { get; set; } = "";

    /// <summary>批号。跟着这一瓶料走，GLP 要求记得出「用的是哪一批」。</summary>
    public string Batch { get; set; } = "";

    /// <summary>供应商。</summary>
    public string Supplier { get; set; } = "";

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
        Density = Density,
        Bp = Bp,
        Purity = Purity,
        Cp = Cp,
        Phase = Phase,
        Batch = Batch,
        Supplier = Supplier,
        Category = Category,
        Solvent = Solvent,
        Note = Note,
        Solubility = (double[])Solubility.Clone(),
        StructureKey = StructureKey,
        IonText = IonText
    };
}
