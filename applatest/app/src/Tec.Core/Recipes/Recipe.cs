using Tec.Core.Catalog;
using Tec.Driver.Abi;

namespace Tec.Core.Recipes;

/// <summary>
/// 一条步骤。存的是 CommandId + ParameterSet，不是设备实例——
/// 这样同一条配方能在任何满足 RequiredCapability 的通道上跑（§4.1 / §6）。
/// </summary>
public sealed class Step
{
    public string StepId { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public required string CommandId { get; init; }
    public ParameterSet Parameters { get; init; } = new();
    /// <summary>多分段表：梯度控温、分段加料。</summary>
    public List<ParameterSet>? Rows { get; init; }
    public bool Enabled { get; set; } = true;
    /// <summary>
    /// 这一步失败了要不要停下来。默认停——后面的步骤多半建立在这一步做成了的前提上，
    /// 「升温失败了照样往下加料」是实打实的事故。只有明确不要紧的步骤（一条可有可无的
    /// 采集）才该关掉它。
    /// </summary>
    public bool PauseOnFault { get; set; } = true;
    public string? Comment { get; set; }

    /// <summary>
    /// 工艺阶段（升温 / 保温 / 结晶 / 蒸馏 / 淬灭…）。空 = 没标。
    ///
    /// **这是操作人自己标的，不是设备回报的。**事后看 Tr−Tj 曲线，
    /// 「这一段在结晶还是在蒸馏」机器根本不知道——它只知道自己在按 Tr 还是按 Tj 控温。
    /// 所以两件事分开记：控温对象从设备来（<see cref="Tec.Core.Records.StepRecord.ControlMode"/>），
    /// 工艺阶段由人来标，报告里也照实注明是谁说的。
    /// </summary>
    public string? Phase { get; set; }

    /// <summary>换一个新 Id 的副本（粘贴、复制到别的通道）。</summary>
    public Step Clone() => CopyWith(Guid.NewGuid().ToString("N")[..8]);

    /// <summary>
    /// 整份复制，只让调用方换 Id 和参数这两样。
    ///
    /// **就这一处**。从前快照、热改各手抄一遍字段清单，结果热改之后
    /// 「失败时暂停并报警」被悄悄抄丢、退回默认的 true——操作人明明关掉了它，
    /// 改一次参数又自己打开了。加字段时只改这里一处，就不会再抄漏。
    /// </summary>
    public Step CopyWith(string? stepId = null, ParameterSet? parameters = null) => new()
    {
        StepId = stepId ?? StepId,
        CommandId = CommandId,
        Parameters = parameters ?? Parameters.Clone(),
        Rows = Rows?.Select(r => r.Clone()).ToList(),
        Enabled = Enabled,
        PauseOnFault = PauseOnFault,
        Comment = Comment,
        Phase = Phase
    };
}

public sealed class Recipe
{
    public const int CurrentSchemaVersion = 1;

    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "未命名配方";
    public string? Author { get; set; }
    public string? Notes { get; set; }
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public DateTimeOffset ModifiedAt { get; set; } = DateTimeOffset.Now;
    public List<Step> Steps { get; } = new();

    /// <summary>
    /// 由 Steps 推导，不单独存。通道能不能跑这条配方，就是问它的能力集合是否覆盖这里。
    /// </summary>
    public IReadOnlySet<Type> RequiredCapabilities(ICommandCatalog catalog)
    {
        var set = new HashSet<Type>();
        foreach (var s in Steps)
        {
            if (!s.Enabled) continue;
            if (!catalog.TryGet(s.CommandId, out var d)) continue;
            if (d.RequiredCapability is { } t) set.Add(t);
            foreach (var extra in d.AlsoRequires) set.Add(extra);
        }
        return set;
    }

    /// <summary>
    /// 复制成一条**新**配方：换一个 Id、改名、记上是谁在什么时候存的。
    ///
    /// 存进配方库、另存副本走这一个。保住原 Id 的那种复制走 Snapshot()——
    /// 撤销栈与运行记录靠 Id 对回原件，那两处换了 Id 就对不上了。
    ///
    /// 时间戳必须重打：库里「最近更新」显示的是这一条什么时候存进来的，
    /// 沿用原件的时间会让人以为它一直没动过。
    /// </summary>
    public Recipe CopyAs(string name, string? author = null)
    {
        var r = Copy(Guid.NewGuid().ToString("N")[..8]);
        r.Name = name;
        r.Author = author ?? Author;
        r.ModifiedAt = DateTimeOffset.Now;
        return r;
    }

    public Recipe Snapshot() => Copy(Id);

    private Recipe Copy(string id)
    {
        var r = new Recipe
        {
            Id = id,
            Name = Name,
            Author = Author,
            Notes = Notes,
            SchemaVersion = SchemaVersion,
            ModifiedAt = ModifiedAt
        };
        // 快照保住 StepId，记录才对得上
        foreach (var s in Steps) r.Steps.Add(s.CopyWith());
        return r;
    }
}
