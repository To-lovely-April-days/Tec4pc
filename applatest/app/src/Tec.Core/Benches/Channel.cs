using Tec.Driver.Abi;

namespace Tec.Core.Benches;

public sealed record DeviceAttachment(IDeviceSession Session, int Well, bool IsHost)
{
    /// <summary>
    /// 【本地改动】这台设备在台面连接清单里排第几。会话是各连各的（不同串口同时连），谁先连上不一定——
    /// 挂载按这个序号排，通道上「第一个 IScalarSensor 是谁」才每次都一样；没给的排在后面、按挂上来的先后。
    /// </summary>
    public int Order { get; init; } = int.MaxValue;
    internal long Seq { get; init; }
}

/// <summary>
/// 通道的能力集合。上层只问"有没有这项能力"，不问"这是哪台设备"。
/// 【本地改动】写时复制：界面线程每秒在读、设备会话在后台线程挂上摘下——从前是一只共用的 List，
/// 一边遍历一边改（「Collection was modified」那一类）。现在改的时候换一只新数组，读的人拿到的永远是完整的一份。
/// </summary>
public sealed class CapabilitySet : ICapabilityLookup
{
    private volatile ICapability[] _items = Array.Empty<ICapability>();
    private readonly object _gate = new();

    public IReadOnlyList<ICapability> All => _items;

    public void Add(IEnumerable<ICapability> caps)
    {
        lock (_gate)
        {
            var list = _items.ToList();
            foreach (var c in caps) if (!list.Contains(c)) list.Add(c);
            _items = list.ToArray();
        }
    }

    /// <summary>整份换掉（去重、保序）。</summary>
    public void Replace(IEnumerable<ICapability> caps)
    {
        lock (_gate)
        {
            var list = new List<ICapability>();
            foreach (var c in caps) if (!list.Contains(c)) list.Add(c);
            _items = list.ToArray();
        }
    }

    public void Clear()
    {
        lock (_gate) _items = Array.Empty<ICapability>();
    }

    public T? Get<T>() where T : class, ICapability
    {
        var items = _items;
        for (var i = 0; i < items.Length; i++)
            if (items[i] is T t) return t;
        return null;
    }

    public bool Has<T>() where T : class, ICapability => Get<T>() is not null;

    public bool Has(Type capabilityType)
    {
        var items = _items;
        for (var i = 0; i < items.Length; i++)
            if (capabilityType.IsInstanceOfType(items[i])) return true;
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

    private volatile DeviceAttachment[] _attachments = Array.Empty<DeviceAttachment>();
    private readonly object _gate = new();
    private long _seq;

    /// <summary>挂在这一路上的设备，按 <see cref="DeviceAttachment.Order"/> 排。每次读到的是完整的一份快照（写时复制）。</summary>
    public IReadOnlyList<DeviceAttachment> Attachments => _attachments;

    /// <param name="order">这台设备在台面连接清单里的序号（见 DeviceAttachment.Order）；不给 = 排在后面、按挂上来的先后。</param>
    public void Attach(IDeviceSession session, int well, bool isHost, int? order = null)
    {
        lock (_gate)
        {
            var old = _attachments;
            // 不是从设备会话来的能力（测试直接塞的那种）照旧留着
            var fromSessions = new HashSet<ICapability>(old.SelectMany(a => a.Session.CapabilitiesOf(a.Well)), ReferenceEqualityComparer.Instance);
            var extras = Capabilities.All.Where(c => !fromSessions.Contains(c)).ToList();

            var list = old.Where(a => !(ReferenceEquals(a.Session, session) && a.Well == well)).ToList();
            list.Add(new DeviceAttachment(session, well, isHost) { Order = order ?? int.MaxValue, Seq = ++_seq });
            var sorted = list.OrderBy(a => a.Order).ThenBy(a => a.Seq).ToArray();
            _attachments = sorted;
            Capabilities.Replace(sorted.SelectMany(a => a.Session.CapabilitiesOf(a.Well)).Concat(extras));
        }
    }

    public void DetachAll()
    {
        lock (_gate)
        {
            _attachments = Array.Empty<DeviceAttachment>();
            Capabilities.Clear();
        }
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
