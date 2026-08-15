using Tec.Driver.Abi;

namespace Tec.Core.Catalog;

public interface ICommandCatalog
{
    IReadOnlyList<CommandDescriptor> All { get; }
    bool TryGet(string commandId, out CommandDescriptor descriptor);
    IReadOnlyList<string> Modules { get; }
    IReadOnlyList<CommandDescriptor> InModule(string module);
}

/// <summary>
/// 全部可编排指令的汇总：流程指令（Core 自带）+ 已装驱动声明的指令。
/// 配方引用了一条当前没装驱动的指令时，这里查不到——
/// 但配方仍然必须能打开、能显示、能提示"缺少驱动 X"（§6 第 3 条）。
/// </summary>
public sealed class CommandCatalog : ICommandCatalog
{
    private readonly Dictionary<string, CommandDescriptor> _byId = new(StringComparer.Ordinal);
    private readonly List<CommandDescriptor> _ordered = new();

    public CommandCatalog(bool includeBuiltins = true)
    {
        if (includeBuiltins) Register(BuiltinCommands.All);
    }

    public IReadOnlyList<CommandDescriptor> All => _ordered;

    public void Register(IEnumerable<CommandDescriptor> commands)
    {
        foreach (var c in commands)
        {
            if (_byId.ContainsKey(c.Id)) continue;   // 先到先得：同 Id 的重复声明不覆盖
            _byId[c.Id] = c;
            _ordered.Add(c);
        }
    }

    public void Unregister(IEnumerable<string> commandIds)
    {
        foreach (var id in commandIds)
        {
            if (!_byId.Remove(id)) continue;
            _ordered.RemoveAll(c => c.Id == id);
        }
    }

    public bool TryGet(string commandId, out CommandDescriptor descriptor)
        => _byId.TryGetValue(commandId, out descriptor!);

    public IReadOnlyList<string> Modules
        => _ordered.Select(c => c.Module).Distinct(StringComparer.Ordinal).ToList();

    public IReadOnlyList<CommandDescriptor> InModule(string module)
        => _ordered.Where(c => c.Module == module).ToList();
}
