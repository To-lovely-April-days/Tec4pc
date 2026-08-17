namespace Tec.Core.Chemistry;

/// <summary>
/// 这一组分在这一炉里干什么。
///
/// 角色不只是标签：**限制试剂**是整张表的基准（它定量，其余按当量算），
/// **溶剂**按倍量给（每克限制试剂几毫升），**目标产物**根本不投料——
/// 它在表里是为了算理论产量和收率。
/// </summary>
public enum ChargeRole
{
    /// <summary>限制试剂。整张表的基准，有且只能有一个。</summary>
    Limiting,
    Reagent,
    Solvent,
    Catalyst,
    /// <summary>淬灭剂 / 后处理用料。</summary>
    Quench,
    /// <summary>目标产物。**不投料**，摆在表里是为了理论产量与收率。</summary>
    Product,
    Other
}

/// <summary>这一行的量是怎么定下来的。</summary>
public enum ChargeBasis
{
    /// <summary>直接给量：称多少克、量多少毫升、要多少毫摩尔。</summary>
    Quantity,
    /// <summary>按当量：相对限制试剂的摩尔比。</summary>
    Equivalents,
    /// <summary>按倍量（V/W）：每 1 g 限制试剂用几 mL。溶剂的常规写法。</summary>
    Volumes
}

public enum ChargeUnit { Gram, Milliliter, Millimole }

public static class ChargeWords
{
    public static string Of(ChargeRole r) => r switch
    {
        ChargeRole.Limiting => "限制试剂",
        ChargeRole.Reagent => "试剂",
        ChargeRole.Solvent => "溶剂",
        ChargeRole.Catalyst => "催化剂",
        ChargeRole.Quench => "淬灭 / 后处理",
        ChargeRole.Product => "目标产物",
        _ => "其他"
    };

    public static string Of(ChargeBasis b) => b switch
    {
        ChargeBasis.Equivalents => "按当量",
        ChargeBasis.Volumes => "按倍量",
        _ => "直接给量"
    };

    public static string Of(ChargeUnit u) => u switch
    {
        ChargeUnit.Milliliter => "mL",
        ChargeUnit.Millimole => "mmol",
        _ => "g"
    };

    public static IReadOnlyList<ChargeRole> Roles { get; } = new[]
    {
        ChargeRole.Limiting, ChargeRole.Reagent, ChargeRole.Solvent,
        ChargeRole.Catalyst, ChargeRole.Quench, ChargeRole.Product, ChargeRole.Other
    };

    public static IReadOnlyList<ChargeBasis> Bases { get; } = new[]
    {
        ChargeBasis.Quantity, ChargeBasis.Equivalents, ChargeBasis.Volumes
    };

    public static IReadOnlyList<ChargeUnit> Units { get; } = new[]
    {
        ChargeUnit.Gram, ChargeUnit.Milliliter, ChargeUnit.Millimole
    };
}

/// <summary>
/// 配料表里的一行。
///
/// **连库不抄库**：库里那条化合物的物性（摩尔质量 / 密度 / 纯度）由 <see cref="Cas"/>
/// 现取，库里改了这里跟着变——那正是「库里的分子量和密度被加料任务实际消费」的意思。
/// 但这一瓶料自己的数（实测纯度、这一批的批号）写在行上，**行上写了的赢**：
/// 手里这瓶 65 % 的硝酸，不能因为库里那条写着 68 % 就按 68 % 算。
/// </summary>
public sealed class ChargeItem
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>连到化合物库的键。空 = 这一行不连库，物性全靠行上自己填。</summary>
    public string Cas { get; set; } = "";

    /// <summary>
    /// 组分名。**同时是与「加料」步骤对上的凭据**：步骤里那个「料液」填的就是它。
    /// 所以它是冻在这一炉里的，库里后来改了名不会把这一炉的记录一起改掉。
    /// </summary>
    public string Name { get; set; } = "";

    public ChargeRole Role { get; set; } = ChargeRole.Reagent;
    public ChargeBasis Basis { get; set; } = ChargeBasis.Equivalents;

    /// <summary>基准对应的那个数：当量 / 倍量 / 给定的量。空 = 还没填。</summary>
    public double? Amount { get; set; }

    /// <summary>直接给量时的单位。其余基准下不用。</summary>
    public ChargeUnit Unit { get; set; } = ChargeUnit.Gram;

    // ── 这一瓶自己的数。空 = 用库里那条的 ──────────────────────────

    public double? Mw { get; set; }
    public double? Density { get; set; }
    public double? Purity { get; set; }
    public string Batch { get; set; } = "";
    public string Supplier { get; set; } = "";

    /// <summary>
    /// 实际投料量 g（产物行是实际产量）。称完回填——**算出来的是应称量，
    /// 这一格才是真投进去的**，报告里两个数都要有，差多少一眼看得见。
    /// </summary>
    public double? ActualMass { get; set; }

    /// <summary>实际量取的体积 mL。液体按体积量的场合填这个。</summary>
    public double? ActualVolume { get; set; }

    public string Note { get; set; } = "";

    public ChargeItem Clone() => new()
    {
        Id = Id, Cas = Cas, Name = Name, Role = Role, Basis = Basis,
        Amount = Amount, Unit = Unit, Mw = Mw, Density = Density, Purity = Purity,
        Batch = Batch, Supplier = Supplier,
        ActualMass = ActualMass, ActualVolume = ActualVolume, Note = Note
    };
}

/// <summary>
/// 一个通道这一炉的配料表。
///
/// 跟着实验走（存进 .tec），不是全局的——同一台机器上四个通道跑四组不同的量，
/// 而化合物库里那些物性在哪一炉都一样。
/// </summary>
public sealed class ChargeTable
{
    public List<ChargeItem> Items { get; } = new();

    /// <summary>釜的容积 mL。填了就拿总体积跟它对一下。空 = 不检查。</summary>
    public double? VesselVolume { get; set; }

    public bool IsEmpty => Items.Count == 0;

    public ChargeTable Clone()
    {
        var t = new ChargeTable { VesselVolume = VesselVolume };
        foreach (var i in Items) t.Items.Add(i.Clone());
        return t;
    }
}
