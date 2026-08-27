using Tec.Core.Catalog;
using Tec.Driver.Abi;

namespace Tec.Core.Recipes;

/// <summary>
/// 一步执行期间的临时安全限值（iControl 每步的 Advanced 覆盖层）。
/// 步骤开始时挂进安全层、结束（成功、失败、跳过、中止都算）立刻撤下还原。
/// 三层限值并存同时求值——设备底线、配方「改限值」、这一层——所以它
/// **只能在前两层之内再收紧**：多一条限值只会多一双眼睛，撤下即还原。
/// 空着的字段就是不加那一条；全空 = 这一步没有覆盖。
/// </summary>
public sealed class StepGuard
{
    public double? TrMax { get; set; }
    public double? TrMin { get; set; }
    public double? TjMax { get; set; }
    public double? TjMin { get; set; }
    public double? PhMax { get; set; }
    public double? PhMin { get; set; }

    /// <summary>触发动作，SafetyActionWords 的五档措辞；缺省中止本通道。</summary>
    public string Action { get; set; } = "中止本通道";

    public bool IsEmpty => TrMax is null && TrMin is null && TjMax is null
                           && TjMin is null && PhMax is null && PhMin is null;

    public StepGuard Clone() => new()
    {
        TrMax = TrMax, TrMin = TrMin, TjMax = TjMax,
        TjMin = TjMin, PhMax = PhMax, PhMin = PhMin, Action = Action
    };

    /// <summary>盯了哪几路，报给校验器与记录用：("Tr", min, max)…只含非空的。</summary>
    public IEnumerable<(string Tag, double? Min, double? Max)> Bounds()
    {
        if (TrMin is not null || TrMax is not null) yield return ("Tr", TrMin, TrMax);
        if (TjMin is not null || TjMax is not null) yield return ("Tj", TjMin, TjMax);
        if (PhMin is not null || PhMax is not null) yield return ("pH", PhMin, PhMax);
    }
}

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

    /// <summary>这一步执行期间的临时安全限值。null 或 IsEmpty = 没有覆盖。</summary>
    public StepGuard? Guard { get; set; }

    /// <summary>
    /// 与上一步并行启动（iControl 的 Alignment = Parallel）。
    /// 一个「并行组」= 一条串行步 + 紧随其后的若干并行步：组内所有步骤
    /// 同时开跑，**全部结束**下一组才开始——这正是 iControl 的 Phase 同步语义。
    /// 第一步、循环标记、起始步骤不能并行（校验器把关）。缺省串行 = 老行为。
    /// </summary>
    public bool Parallel { get; set; }

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
        Phase = Phase,
        Guard = Guard?.Clone(),
        Parallel = Parallel
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
    /// 配方级变量。循环条件、条件等待里按名字读，「设定变量」步骤改；
    /// 启动时随配方一起冻进基线——运行中改的是执行器里那一份，基线不动。
    /// </summary>
    public List<RecipeVariable> Variables { get; } = new();

    /// <summary>
    /// 随配方结伴走的配料表（模板）。**配方和配料表是连体的**：加料步骤持有
    /// 配料行的 Id（chemId），只把步骤存进库、导出成文件，应用到别处时引用全断。
    /// 所以存库 / 导出时把当前通道的配料表捎上（模板化清洗见 ChargeTemplate），
    /// 应用 / 导入时一起落地——行 Id 原样保留，步骤引用天然成立，不用重映射。
    /// 通道上的工作配方不带它（通道的配料表在 Workspace.ChannelCharges 里）；
    /// 老文件没有这一项，读回来是 null。
    /// </summary>
    public Chemistry.ChargeTable? Charge { get; set; }

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
        foreach (var v in Variables) r.Variables.Add(v.Clone());
        r.Charge = Charge?.Clone();
        return r;
    }
}
