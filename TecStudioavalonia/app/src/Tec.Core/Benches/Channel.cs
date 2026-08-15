using Tec.Driver.Abi;

namespace Tec.Core.Benches;

public sealed record DeviceAttachment(IDeviceSession Session, int Well, bool IsHost);

/// <summary>
/// 通道的能力集合。上层只问"有没有这项能力"，不问"这是哪台设备"。
/// </summary>
public sealed class CapabilitySet : ICapabilityLookup
{
    private readonly List<ICapability> _items = new();

    public IReadOnlyList<ICapability> All => _items;

    public void Add(IEnumerable<ICapability> caps)
    {
        foreach (var c in caps) if (!_items.Contains(c)) _items.Add(c);
    }

    public void Clear() => _items.Clear();

    public T? Get<T>() where T : class, ICapability
    {
        for (var i = 0; i < _items.Count; i++)
            if (_items[i] is T t) return t;
        return null;
    }

    public bool Has<T>() where T : class, ICapability => Get<T>() is not null;

    public bool Has(Type capabilityType)
    {
        for (var i = 0; i < _items.Count; i++)
            if (capabilityType.IsInstanceOfType(_items[i])) return true;
        return false;
    }
}

/// <summary>
/// 执行的最小单位。它不是一个设备，而是一组能力的聚合（§5）。
/// 通道各自启动——用户用几个就是几个，不存在"整机一起开始"这个概念（§7.1）。
/// </summary>
public sealed class Channel
{
    public Channel(int number, string hostInstanceId, int well)
    {
        Number = number;
        HostInstanceId = hostInstanceId;
        Well = well;
    }

    public int Number { get; }
    public string HostInstanceId { get; }
    public int Well { get; }
    public bool Enabled { get; set; } = true;
    public string Name => $"CH{Number}";

    public CapabilitySet Capabilities { get; } = new();
    public List<DeviceAttachment> Attachments { get; } = new();

    public void Attach(IDeviceSession session, int well, bool isHost)
    {
        Attachments.RemoveAll(a => ReferenceEquals(a.Session, session) && a.Well == well);
        Attachments.Add(new DeviceAttachment(session, well, isHost));
        Capabilities.Add(session.CapabilitiesOf(well));
    }

    public void DetachAll()
    {
        Attachments.Clear();
        Capabilities.Clear();
    }

    /// <summary>
    /// 从挂在这个通道上的会话里找指令处理器。找不到 = 这个通道装不下这条指令，
    /// 应该在装载时就拦住，而不是跑到一半报错（§5 末）。
    /// </summary>
    public ICommandHandler? ResolveHandler(string commandId)
    {
        foreach (var a in Attachments)
        {
            var h = a.Session.Resolve(commandId);
            if (h is not null) return h;
        }
        return null;
    }

    /// <summary>配方要求的能力里，这个通道缺哪些。界面直接标出来。</summary>
    public IReadOnlyList<Type> MissingCapabilities(IEnumerable<Type> required)
        => required.Where(t => !Capabilities.Has(t)).ToList();
}
