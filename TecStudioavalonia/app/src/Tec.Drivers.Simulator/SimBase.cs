using Tec.Driver.Abi;

namespace Tec.Drivers.Simulator;

/// <summary>
/// 仿真会话的公共骨架。
/// 每个驱动必须提供模拟实现，而且要能跑出**合理的动态**——
/// 升温有惯性、加料有滞后，否则演示时曲线一眼假（§3.4）。
/// </summary>
public abstract class SimSession : IDeviceSession
{
    private readonly CancellationTokenSource _cts = new();
    private Task? _pump;
    private DeviceState _state = DeviceState.Connected;

    protected SimSession(DriverContext ctx)
    {
        Context = ctx;
        InstanceId = ctx.InstanceId;
    }

    protected DriverContext Context { get; }
    protected Broadcast<Sample> Out { get; } = new();
    protected Random Rng { get; } = new(20240517);

    /// <summary>仿真里所有时间都走宿主的钟；加速时它是虚拟钟。</summary>
    protected DateTimeOffset Now => Context.Clock();
    protected double Scale => Context.TimeScale <= 0 ? 1 : Context.TimeScale;

    public string InstanceId { get; }
    public IObservable<Sample> Samples => Out;
    public abstract IReadOnlyList<TagDescriptor> Tags { get; }
    public abstract int WellCount { get; }
    public abstract IReadOnlyList<ICapability> CapabilitiesOf(int well);
    public abstract ICommandHandler? Resolve(string commandId);

    public DeviceState State
    {
        get => _state;
        protected set
        {
            if (_state == value) return;
            _state = value;
            StateChanged?.Invoke(this, value);
        }
    }

    public event EventHandler<DeviceState>? StateChanged;

    /// <summary>仿真步长（仿真秒）。</summary>
    protected virtual TimeSpan Step => TimeSpan.FromSeconds(1);

    protected abstract void Tick(double dtSeconds);

    public Task StartAsync(CancellationToken ct)
    {
        if (_pump is not null) return Task.CompletedTask;
        State = DeviceState.Ready;
        _pump = Task.Run(() => PumpAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _cts.Cancel();
        if (_pump is not null)
        {
            try { await _pump.ConfigureAwait(false); } catch { }
            _pump = null;
        }
        State = DeviceState.Connected;
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        var real = TimeSpan.FromTicks((long)(Step.Ticks / Scale));
        if (real < TimeSpan.FromMilliseconds(20)) real = TimeSpan.FromMilliseconds(20);
        var perTickSim = TimeSpan.FromTicks((long)(real.Ticks * Scale));

        while (!ct.IsCancellationRequested)
        {
            try
            {
                Tick(perTickSim.TotalSeconds);
            }
            catch (Exception ex)
            {
                // 故障隔离：驱动出错只让该设备进 Faulted，绝不拖垮进程（§3.5）
                State = DeviceState.Faulted;
                Context.Log?.Invoke("error", $"{InstanceId} 仿真异常：{ex.Message}");
            }
            try { await Task.Delay(real, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    protected void Emit(int channel, string tag, double value)
        => Out.Push(new Sample(channel, tag, MonotonicTicks(), Now, value,
            Context.Simulated ? Quality.Simulated : Quality.Good));

    private long MonotonicTicks() => Now.UtcTicks;

    /// <summary>±amplitude 的确定性小噪声。演示要稳，不能每次跑出来的曲线都不一样。</summary>
    protected double Noise(double amplitude) => (Rng.NextDouble() * 2 - 1) * amplitude;

    protected Task DelaySimAsync(TimeSpan simulated, CancellationToken ct)
    {
        var real = TimeSpan.FromTicks((long)(simulated.Ticks / Scale));
        if (real < TimeSpan.Zero) real = TimeSpan.Zero;
        return Task.Delay(real, ct);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        Out.Complete();
        _cts.Dispose();
        State = DeviceState.Disposed;
        GC.SuppressFinalize(this);
    }
}

/// <summary>把一堆 (id → handler 工厂) 收成一个解析器，省掉每个驱动写一遍 switch。</summary>
public sealed class HandlerTable
{
    private readonly Dictionary<string, Func<ICommandHandler>> _map = new(StringComparer.Ordinal);

    public HandlerTable Add(string id, Func<ICommandHandler> factory)
    {
        _map[id] = factory;
        return this;
    }

    public ICommandHandler? Resolve(string id) => _map.TryGetValue(id, out var f) ? f() : null;
}

/// <summary>公共小工具。仿真的等待一律走它，保证加速倍数只在一个地方生效。</summary>
public static class SimTime
{
    public static Task DelayAsync(TimeSpan simulated, double scale, CancellationToken ct)
    {
        if (scale <= 0) scale = 1;
        var real = TimeSpan.FromTicks((long)(simulated.Ticks / scale));
        return real <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(real, ct);
    }

    /// <summary>轮询到条件成立或超时。返回是否成立。</summary>
    public static async Task<bool> PollAsync(Func<bool> predicate, TimeSpan timeout, double scale,
                                             Func<DateTimeOffset> now, CancellationToken ct)
    {
        var deadline = timeout > TimeSpan.Zero ? now() + timeout : DateTimeOffset.MaxValue;
        while (!ct.IsCancellationRequested)
        {
            if (predicate()) return true;
            if (now() >= deadline) return false;
            await DelayAsync(TimeSpan.FromSeconds(1), scale, ct).ConfigureAwait(false);
        }
        ct.ThrowIfCancellationRequested();
        return false;
    }
}
