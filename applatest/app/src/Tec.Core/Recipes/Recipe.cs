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
    public string? Comment { get; set; }

    public Step Clone() => new()
    {
        StepId = Guid.NewGuid().ToString("N")[..8],
        CommandId = CommandId,
        Parameters = Parameters.Clone(),
        Rows = Rows?.Select(r => r.Clone()).ToList(),
        Enabled = Enabled,
        Comment = Comment
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

    public Recipe Snapshot()
    {
        var r = new Recipe
        {
            Id = Id,
            Name = Name,
            Author = Author,
            Notes = Notes,
            SchemaVersion = SchemaVersion,
            ModifiedAt = ModifiedAt
        };
        foreach (var s in Steps)
            r.Steps.Add(new Step
            {
                StepId = s.StepId,          // 快照要保住 StepId，记录才对得上
                CommandId = s.CommandId,
                Parameters = s.Parameters.Clone(),
                Rows = s.Rows?.Select(x => x.Clone()).ToList(),
                Enabled = s.Enabled,
                Comment = s.Comment
            });
        return r;
    }
}
