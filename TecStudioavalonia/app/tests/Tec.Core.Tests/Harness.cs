using Tec.Core;
using Tec.Core.Benches;
using Tec.Core.Catalog;
using Tec.Core.Data;
using Tec.Core.Execution;
using Tec.Core.Recipes;
using Tec.Driver.Abi;
using Tec.Drivers.Simulator;

namespace Tec.Core.Tests;

/// <summary>测试用的最小台面：一台仿真反应器 + 一台泵，按需加探头。</summary>
public sealed class Harness : IAsyncDisposable
{
    private readonly List<IDeviceSession> _sessions = new();

    public Harness(double timeScale = 600)
    {
        Clock = new VirtualClock(new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero)) { Rate = timeScale };
        Catalog = new CommandCatalog();
        Pipeline = new DataPipeline();
        Arbiter = new ResourceArbiter();
        Builtins = new BuiltinCommandProvider(new AutoOperatorGate());
        Engine = new RunEngine(Catalog, Builtins, Arbiter, Pipeline, Clock.Func) { TimeScale = timeScale };
        TimeScale = timeScale;

        // 指令是静态声明的：没接硬件也要能编辑配方、也要能被校验器认出来（§3.3）
        Catalog.Register(new Rd105ReactorDriver().Commands);
        Catalog.Register(new DosingPumpDriver().Commands);
        Catalog.Register(new PhProbeDriver().Commands);
        Catalog.Register(new TurbidityProbeDriver().Commands);
    }

    public VirtualClock Clock { get; }
    public CommandCatalog Catalog { get; }
    public DataPipeline Pipeline { get; }
    public ResourceArbiter Arbiter { get; }
    public BuiltinCommandProvider Builtins { get; }
    public RunEngine Engine { get; }
    public double TimeScale { get; }

    public async Task<Channel> ReactorChannelAsync(int number, bool withPump = false, bool withPh = false)
    {
        var channel = new Channel(number, "R1", 0);

        var rs = await Open(new Rd105ReactorDriver(), "R1", number);
        channel.Attach(rs, 0, true);

        if (withPump)
            channel.Attach(await Open(new DosingPumpDriver(), "P1", number,
                ParameterSet.Of(("注射器", "50 mL"), ("标定系数", 0.125d), ("物料", "水"))), 0, false);

        if (withPh)
            channel.Attach(await Open(new PhProbeDriver(), "PH1", number), 0, false);

        Engine.Attach(channel);
        return channel;
    }

    private async Task<IDeviceSession> Open(IDeviceDriver driver, string id, int channel, ParameterSet? config = null)
    {
        var ctx = new DriverContext
        {
            InstanceId = id,
            ChannelNumbers = new[] { channel },
            Config = config ?? new ParameterSet(),
            Simulated = true,
            TimeScale = TimeScale,
            Clock = Clock.Func
        };
        var s = await driver.OpenAsync(new ParameterSet(), ctx, CancellationToken.None);
        await s.StartAsync(CancellationToken.None);
        _sessions.Add(s);
        Engine.Ingest(s);
        return s;
    }

    public static Step Mk(string commandId, params (string Key, object? Value)[] p)
        => new() { CommandId = commandId, Parameters = ParameterSet.Of(p) };

    public static Recipe RecipeOf(string name, params Step[] steps)
    {
        var r = new Recipe { Name = name };
        r.Steps.AddRange(steps);
        return r;
    }

    public async ValueTask DisposeAsync()
    {
        Engine.AbortAll();
        foreach (var s in _sessions)
        {
            try { await s.DisposeAsync(); } catch { }
        }
        Engine.Dispose();
    }
}
