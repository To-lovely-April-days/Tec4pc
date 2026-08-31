using Tec.Core.Benches;
using Tec.Core.Catalog;
using Tec.Core.Data;
using Tec.Core.Execution;
using Tec.Core.Recipes;
using Tec.Core.Records;
using Tec.Core.Safety;
using Tec.Driver.Abi;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// E 级紧急程序（EasyMax 手册 §6 的 Tsafe / Rsafe）。
/// 限值越限中止之后，机器不是断电了事：降到 Tsafe 守着、搅拌按 Rsafe 走。
/// 这里盯两件事——**真下发到设备**，而且**排在收安全态之后**
/// （反过来就被切输出抹掉了，那是这段代码存在的全部意义）。
/// </summary>
public class EmergencyPlanTests
{
    private sealed class RecTemp : ITemperatureControl
    {
        public readonly List<string> Log = new();
        public int Channel => 1;
        public TempLimits Limits { get; } = new(-40, 180, 16);
        public double CurrentReactor => 150;
        public double CurrentJacket => 150;
        public Task SetTargetAsync(TempTarget t, CancellationToken ct)
        { Log.Add($"set {t.Value}"); return Task.CompletedTask; }
        public Task RampAsync(double t, double r, TempChannelKind k, CancellationToken ct)
        { Log.Add($"ramp {t}"); return Task.CompletedTask; }
        public Task<bool> WaitReachedAsync(double t, double tol, TimeSpan to, CancellationToken ct)
            => Task.FromResult(true);
        public Task StopAsync(CancellationToken ct) { Log.Add("stop"); return Task.CompletedTask; }
        public IObservable<Sample> Temperature { get; } = new Broadcast<Sample>();
    }

    private sealed class RecStir : IStirrer
    {
        public readonly List<string> Log = new();
        public int Channel => 1;
        public SpeedLimits Limits { get; } = new(0, 1000);
        public double CurrentRpm => 300;
        public IObservable<Sample> Speed { get; } = new Broadcast<Sample>();
        public Task SetSpeedAsync(double rpm, CancellationToken ct)
        { Log.Add($"rpm {rpm}"); return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct) { Log.Add("rpm stop"); return Task.CompletedTask; }
    }

    /// <summary>会话的收安全态：先切输出——紧急程序必须排在它之后才算数。</summary>
    private sealed class StoppingSession : IDeviceSession
    {
        private readonly ICapability[] _caps;
        private readonly RecTemp _temp;
        public StoppingSession(RecTemp temp, params ICapability[] caps)
        { _temp = temp; _caps = caps; }
        public string InstanceId => "X1";
        public DeviceState State => DeviceState.Ready;
        public event EventHandler<DeviceState>? StateChanged { add { } remove { } }
        public IReadOnlyList<TagDescriptor> Tags => Array.Empty<TagDescriptor>();
        public IObservable<Sample> Samples { get; } = new Broadcast<Sample>();
        public int WellCount => 1;
        public IReadOnlyList<ICapability> CapabilitiesOf(int well) => _caps;
        public ICommandHandler? Resolve(string commandId) => null;
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public ValueTask<IReadOnlyList<string>?> SafeStopAsync(int well, CancellationToken ct)
        {
            _temp.Log.Add("safestop");
            return ValueTask.FromResult<IReadOnlyList<string>?>(new[] { "已切断加热输出" });
        }
    }

    private static (RunEngine Engine, RecTemp Temp, RecStir Stir) Rig()
    {
        var temp = new RecTemp();
        var stir = new RecStir();
        var provider = new BuiltinCommandProvider(new AutoOperatorGate());
        var engine = new RunEngine(new CommandCatalog(), provider, new ResourceArbiter(),
                                   new DataPipeline(), () => DateTimeOffset.Now);
        provider.Safety = engine.Safety;
        var ch = new Channel(1, "X1", 0);
        ch.Attach(new StoppingSession(temp, temp, stir), 0, true);
        engine.Attach(ch);
        engine.NewBatch("测试批次", "张三", "测试台面");
        return (engine, temp, stir);
    }

    [Fact]
    public async Task 安全层中止之后按Tsafe与Rsafe把机器摆好_且排在收安全态之后()
    {
        var (engine, temp, stir) = Rig();
        try
        {
            // 整条链路都走真的：配方里一步「改限值」把 Tsafe / Rsafe 交给安全层
            var setLimits = new Step
            {
                CommandId = BuiltinCommands.Interlock,
                Parameters = ParameterSet.Of(("eOn", true), ("tsafe", 25d),
                                             ("rsafeMode", "指定转速"), ("rsafe", 100d)),
                Rows = new List<ParameterSet>
                { ParameterSet.Of(("par", "Tr max"), ("val", 120d), ("act", "中止本通道")) }
            };
            engine.StartChannel(1, Harness.RecipeOf("越限就收",
                setLimits, Harness.Mk(BuiltinCommands.Wait, ("dur", 60d))), "张三");
            var runner = engine.Runner(1)!;
            for (var i = 0; i < 100 && engine.Safety.EmergencyPlanOf(1) is null; i++)
                await Task.Delay(20);
            Assert.NotNull(engine.Safety.EmergencyPlanOf(1));

            runner.Abort(null, "Tr 高于上限", emergency: true);
            await runner.Completion.WaitAsync(TimeSpan.FromSeconds(10));

            // 顺序：先切输出（safestop），再摆到安全温度
            Assert.Equal(new[] { "safestop", "set 25" }, temp.Log);
            Assert.Equal(new[] { "rpm 100" }, stir.Log);

            // 做了什么进记录——事后要答得出机器现在是什么状态
            var run = engine.Record.Of(1)!;
            Assert.Contains(run.Events, e => e.Kind == EventKind.SafeStop && e.Text.Contains("Tsafe"));
            Assert.Contains(run.Events, e => e.Kind == EventKind.SafeStop && e.Text.Contains("Rsafe"));
        }
        finally { engine.AbortAll(); engine.Dispose(); }
    }

    [Fact]
    public async Task 保持转速时不动搅拌_但照实记一句()
    {
        var (engine, temp, stir) = Rig();
        try
        {
            engine.StartChannel(1, Harness.RecipeOf("等着",
                Harness.Mk(BuiltinCommands.Wait, ("dur", 60d))), "张三");
            var runner = engine.Runner(1)!;
            // 计划由配方步骤在启动之后设——启动那一刻会清掉上一炉的
            engine.Safety.SetEmergencyPlan(1, new EmergencyPlan(SafeTemp: 30, SafeRpm: null));

            runner.Abort(null, "越限", emergency: true);
            await runner.Completion.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Empty(stir.Log);                    // Hold = 一个字都不发给搅拌
            Assert.Contains("set 30", temp.Log);
            Assert.Contains(engine.Record.Of(1)!.Events,
                e => e.Kind == EventKind.SafeStop && e.Text.Contains("保持当前转速"));
        }
        finally { engine.AbortAll(); engine.Dispose(); }
    }

    [Fact]
    public async Task 操作人自己按的中止不走紧急程序()
    {
        // E 级是安全层触发的那一档。操作人按「中止」就是收安全态，
        // 不该顺手把温控又打开到 Tsafe——那是替他做了没要求的事
        var (engine, temp, stir) = Rig();
        try
        {
            engine.StartChannel(1, Harness.RecipeOf("等着",
                Harness.Mk(BuiltinCommands.Wait, ("dur", 60d))), "张三");
            var runner = engine.Runner(1)!;
            engine.Safety.SetEmergencyPlan(1, new EmergencyPlan(SafeTemp: 25, SafeRpm: 100));

            runner.Abort("张三", "操作人中止");
            await runner.Completion.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(new[] { "safestop" }, temp.Log);
            Assert.Empty(stir.Log);
        }
        finally { engine.AbortAll(); engine.Dispose(); }
    }

    [Fact]
    public async Task 没设过紧急程序就是原来的收安全态()
    {
        var (engine, temp, stir) = Rig();
        try
        {
            engine.StartChannel(1, Harness.RecipeOf("等着",
                Harness.Mk(BuiltinCommands.Wait, ("dur", 60d))), "张三");
            var runner = engine.Runner(1)!;

            runner.Abort(null, "越限", emergency: true);
            await runner.Completion.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(new[] { "safestop" }, temp.Log);
            Assert.Empty(stir.Log);
        }
        finally { engine.AbortAll(); engine.Dispose(); }
    }
}
