using Tec.Core.Benches;
using Tec.Core.Chemistry;
using Tec.Core.Recipes;
using Tec.Driver.Abi;

namespace Tec.Core.Persistence;

// 文件格式与内存模型分开写。模型天天改，文件格式是对外承诺——
// 混在一起的话，某天给 DeviceInstance 加个界面用的字段，
// 昨天存的实验就打不开了。

public sealed class DeviceDoc
{
    public string DriverId { get; set; } = "";
    public string InstanceId { get; set; } = "";
    public string? Label { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public bool Simulated { get; set; } = true;
    public ParameterSet Connection { get; set; } = new();
    public ParameterSet Config { get; set; } = new();

    // 停靠关系。探头插在哪台反应器的哪个管口上，是台面的一部分，必须存
    public string? DockHostId { get; set; }
    public DockSide Dock { get; set; } = DockSide.None;
    public int DockSlot { get; set; }
    public string? DockAnchor { get; set; }
    public string? DockSideTag { get; set; }
}

public sealed class BindingDoc
{
    public string DeviceId { get; set; } = "";
    public int Channel { get; set; }
    public BindingMode Mode { get; set; } = BindingMode.Exclusive;
    public int Port { get; set; }
}

public sealed class StationDoc
{
    public string Name { get; set; } = "";
    public List<int> Channels { get; set; } = new();
}

/// <summary>台面文件（.tecbench）。</summary>
public sealed class BenchDoc
{
    public int Schema { get; set; } = Bench.CurrentSchemaVersion;
    public string Name { get; set; } = "";
    public List<DeviceDoc> Devices { get; set; } = new();
    public List<BindingDoc> Bindings { get; set; } = new();
    public List<StationDoc> Stations { get; set; } = new();
}

public sealed class StepDoc
{
    public string StepId { get; set; } = "";
    public string CommandId { get; set; } = "";
    public ParameterSet Parameters { get; set; } = new();
    public List<ParameterSet>? Rows { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>老文件里没有这一项，缺省 true——与 Step 的默认一致。</summary>
    public bool PauseOnFault { get; set; } = true;
    public string? Comment { get; set; }
    /// <summary>工艺阶段。老文件里没有，读回来是 null，就是「没标」。</summary>
    public string? Phase { get; set; }
}

/// <summary>配方文件（.tecrecipe），也是实验文件与配方库里的一条。</summary>
public sealed class RecipeDoc
{
    public int Schema { get; set; } = Recipe.CurrentSchemaVersion;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Author { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset ModifiedAt { get; set; }
    public List<StepDoc> Steps { get; set; } = new();
}

/// <summary>配料表里的一行。</summary>
public sealed class ChargeItemDoc
{
    public string Id { get; set; } = "";
    /// <summary>连化合物库的键。空 = 不连库，物性靠下面几项自己填。</summary>
    public string Cas { get; set; } = "";
    public string Name { get; set; } = "";
    public ChargeRole Role { get; set; } = ChargeRole.Reagent;
    public ChargeBasis Basis { get; set; } = ChargeBasis.Equivalents;
    public double? Amount { get; set; }
    public ChargeUnit Unit { get; set; } = ChargeUnit.Gram;
    /// <summary>这一瓶自己的物性。空 = 用库里那条的。</summary>
    public double? Mw { get; set; }
    public double? Density { get; set; }
    public double? Purity { get; set; }
    public string Batch { get; set; } = "";
    public string Supplier { get; set; } = "";
    public double? ActualMass { get; set; }
    public double? ActualVolume { get; set; }
    public string Note { get; set; } = "";
}

/// <summary>
/// 一个通道的配料表。
///
/// **只存输入，不存算出来的数**：mmol / 应称量 / 体积都是拿库里的物性现算的，
/// 存下来的话库里改了密度，文件里那个体积还是老的——而人看不出它是老的。
/// </summary>
public sealed class ChargeTableDoc
{
    public List<ChargeItemDoc> Items { get; set; } = new();
    public double? VesselVolume { get; set; }
}

/// <summary>一条泳道：挂哪个通道、叫什么名、配方是什么、配料表是什么。</summary>
public sealed class LaneDoc
{
    public int Channel { get; set; }
    public string Name { get; set; } = "新配方";
    public RecipeDoc Recipe { get; set; } = new();
    /// <summary>配料表。老文件里没有这一项，读回来是 null，就是「这一路还没配料」。</summary>
    public ChargeTableDoc? Charge { get; set; }
}

/// <summary>
/// 实验文件（.tec）：台面 + 每通道配方 + 配方库。
/// 打开一个实验 = 台面照原样摆回来、配方照原样填回去，接着就能跑。
/// 运行记录不在里面——那是 GLP 记录，只追加，单独落盘。
/// </summary>
public sealed class ExperimentDoc
{
    public const int CurrentSchema = 1;

    public int Schema { get; set; } = CurrentSchema;
    public string Name { get; set; } = "未命名实验";
    public string? Author { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset ModifiedAt { get; set; } = DateTimeOffset.Now;
    public string AppVersion { get; set; } = "1.0";

    public BenchDoc Bench { get; set; } = new();
    public List<LaneDoc> Lanes { get; set; } = new();
    /// <summary>
    /// **老格式遗留**：早期版本把配方库也塞进实验文件里。
    /// 配方库现在是全局的（一台机器一份，存在 tecstudio.db），新存的文件这里恒为空；
    /// 字段留着是为了打得开老文件——打开时里头的配方并进全局库，不悄悄扔掉。
    /// </summary>
    public List<RecipeDoc> Library { get; set; } = new();
}
