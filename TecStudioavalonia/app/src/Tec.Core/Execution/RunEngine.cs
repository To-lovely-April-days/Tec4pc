using Tec.Core.Benches;
using Tec.Core.Catalog;
using Tec.Core.Data;
using Tec.Core.Records;
using Tec.Core.Recipes;
using Tec.Core.Safety;
using Tec.Driver.Abi;

namespace Tec.Core.Execution;

/// <summary>
/// 批次编排。它**不**是"整机启动"按钮——通道各自启动，
/// 批次只是把同时段的通道子记录归拢在一起（§7.1）。
/// </summary>
public sealed class RunEngine
{
    private readonly Dictionary<int, ChannelRunner> _runners = new();
    private readonly List<IDisposable> _subs = new();

    public RunEngine(ICommandCatalog catalog, ICommandProvider builtins,
                     IResourceArbiter arbiter, DataPipeline pipeline,
                     Func<DateTimeOffset>? now = null)
    {
        Catalog = catalog;
        Builtins = builtins;
        Arbiter = arbiter;
        Pipeline = pipeline;
        Now = now ?? (() => DateTimeOffset.Now);
        Safety = new SafetyMonitor(pipeline, Now);
        Safety.Triggered += OnSafetyTriggered;
    }

    public ICommandCatalog Catalog { get; }
    public ICommandProvider Builtins { get; }
    public IResourceArbiter Arbiter { get; }
    public DataPipeline Pipeline { get; }
    public SafetyMonitor Safety { get; }
    public Func<DateTimeOffset> Now { get; }

    public RunRecord Record { get; private set; } = new() { CreatedAt = DateTimeOffset.Now };
    public IReadOnlyCollection<ChannelRunner> Runners => _runners.Values;

    public event EventHandler? Changed;

    /// <summary>共享资源的声明：某通道执行某条指令时要占用什么。台面知道，Core 不猜。</summary>
    public Func<int, string, ResourceNeed?> ResourceOf { get; set; } = (_, _) => null;

    public double TimeScale { get; set; } = 1;

    public void NewBatch(string name, string? op, string benchName)
    {
        Record = new RunRecord
        {
            CreatedAt = Now(),
            Name = name,
            Operator = op,
            BenchName = benchName
        };
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public ChannelRunner Attach(Channel channel)
    {
        if (_runners.TryGetValue(channel.Number, out var existing)) return existing;
        var r = new ChannelRunner(channel, Catalog, Builtins, Arbiter, Now, (ch, id) => ResourceOf(ch, id))
        {
            TimeScale = TimeScale
        };
        r.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        _runners[channel.Number] = r;
        return r;
    }

    public void Detach(int channel)
    {
        if (_runners.Remove(channel, out var r) && r.State is ChannelRunState.Running or ChannelRunState.Paused)
            r.Abort(null, "通道从台面移除");
    }

    public ChannelRunner? Runner(int channel)
        => _runners.TryGetValue(channel, out var r) ? r : null;

    /// <summary>启动一个通道。用户用几个就是几个。</summary>
    public ChannelRun StartChannel(int channel, Recipe recipe, string? user = null, EstimationContext? seed = null)
    {
        var r = _runners.TryGetValue(channel, out var found)
            ? found
            : throw new InvalidOperationException($"CH{channel} 不在台面上。");
        r.TimeScale = TimeScale;
        var run = r.Start(recipe, seed ?? SeedFor(r.Channel), user);
        Record.Append(run);
        Changed?.Invoke(this, EventArgs.Empty);
        return run;
    }

    /// <summary>把当前实测温度等作为估算起点——排期才不会从 25 ℃ 一路空想。</summary>
    private EstimationContext SeedFor(Channel ch)
    {
        var ctx = new EstimationContext();
        if (ch.Capabilities.Get<ITemperatureControl>() is { } t)
        {
            ctx.Temperature = t.CurrentReactor;
            ctx.Jacket = t.CurrentJacket;
        }
        if (ch.Capabilities.Get<IStirrer>() is { } s) ctx.Rpm = s.CurrentRpm;
        if (ch.Capabilities.Get<IDosing>() is { } d) ctx.Volume = d.TotalVolume;
        return ctx;
    }

    public void PauseAll(string? user = null)
    {
        foreach (var r in _runners.Values) r.Pause(user);
    }

    public void AbortAll(string? user = null, string reason = "全机中止")
    {
        foreach (var r in _runners.Values) r.Abort(user, reason);
    }

    /// <summary>把设备会话的采样接进管线。断线时驱动必须自己发 Bad/Stale（§9.4）。</summary>
    public void Ingest(IDeviceSession session)
    {
        foreach (var t in session.Tags) Pipeline.DescribeTag(t);
        _subs.Add(session.Samples.SubscribeTo(s => Pipeline.Push(s)));
    }

    private void OnSafetyTriggered(object? sender, SafetyEvent e)
    {
        var run = Record.Of(e.Channel);
        run?.Append(new EventRecord
        {
            At = e.At,
            Channel = e.Channel,
            Kind = EventKind.SafetyAction,
            Text = e.Message
        });

        switch (e.Limit.Action)
        {
            case SafetyAction.AbortChannel:
                Runner(e.Channel)?.Abort(null, e.Message);
                break;
            case SafetyAction.StopAll:
                AbortAll(null, e.Message);
                break;
            default:
                break;   // 报警 / 停加料 / 停升温由动作执行器处理
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        foreach (var s in _subs) s.Dispose();
        _subs.Clear();
    }
}
