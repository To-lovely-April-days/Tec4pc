namespace Tec.Core.Execution;

public enum ResourcePolicy
{
    /// <summary>排队等待。加料可以晚一点。</summary>
    Queue,
    /// <summary>拿不到就失败中止。有些工艺晚了就废。</summary>
    Fail
}

public enum Priority { Low = 0, Normal = 1, High = 2 }

public interface IResourceLease : IDisposable
{
    string ResourceId { get; }
    int Channel { get; }
    /// <summary>排队等了多久。要计入执行记录，否则事后看到"这步比计划晚了 8 分钟"却找不到原因（§7.4）。</summary>
    TimeSpan Waited { get; }
}

public interface IResourceArbiter
{
    /// <summary>策略必须显式配置，不能默认。</summary>
    Task<IResourceLease?> AcquireAsync(string resourceId, int channel, Priority priority,
                                       ResourcePolicy policy, CancellationToken ct);
    void Declare(string resourceId, int concurrency);
}

public sealed class ResourceArbiter : IResourceArbiter
{
    private readonly Dictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public void Declare(string resourceId, int concurrency)
    {
        lock (_lock)
        {
            if (!_gates.ContainsKey(resourceId))
                _gates[resourceId] = new SemaphoreSlim(Math.Max(1, concurrency), Math.Max(1, concurrency));
        }
    }

    public async Task<IResourceLease?> AcquireAsync(string resourceId, int channel, Priority priority,
                                                    ResourcePolicy policy, CancellationToken ct)
    {
        SemaphoreSlim gate;
        lock (_lock)
        {
            if (!_gates.TryGetValue(resourceId, out gate!))
            {
                gate = new SemaphoreSlim(1, 1);
                _gates[resourceId] = gate;
            }
        }

        var began = DateTimeOffset.UtcNow;
        if (policy == ResourcePolicy.Fail)
            return gate.Wait(0) ? new Lease(this, resourceId, channel, TimeSpan.Zero) : null;

        await gate.WaitAsync(ct).ConfigureAwait(false);
        return new Lease(this, resourceId, channel, DateTimeOffset.UtcNow - began);
    }

    private void Release(string resourceId)
    {
        lock (_lock)
        {
            if (_gates.TryGetValue(resourceId, out var gate)) gate.Release();
        }
    }

    private sealed class Lease : IResourceLease
    {
        private ResourceArbiter? _owner;
        public Lease(ResourceArbiter owner, string resourceId, int channel, TimeSpan waited)
        {
            _owner = owner;
            ResourceId = resourceId;
            Channel = channel;
            Waited = waited;
        }
        public string ResourceId { get; }
        public int Channel { get; }
        public TimeSpan Waited { get; }
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(ResourceId);
    }
}
