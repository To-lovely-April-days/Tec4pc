using System.Diagnostics;
using Tec.Core.Benches;
using Tec.Core.Catalog;
using Tec.Core.Data;
using Tec.Core.Execution;
using Tec.Core.Recipes;
using Tec.Core.Scheduling;
using Tec.Driver.Abi;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 起始装料与限值（iControl 的 [00] First Fill and Safety Limits）。
/// 盯四件事：**起始限值真进安全层**（FromRecipe 层，活到下一次启动前）、
/// 初始搅拌 / 初温真落到设备上、位置规矩（必须第一步、只能一条、
/// 老配方没有它只提醒不阻断）、估算按设备能力走。
/// </summary>
public class FirstFillTests
{
    // ── 台子：带搅拌 + 温控两项能力的最小通道 ───────────────────────

    private sealed class StubTemp : ITemperatureControl
    {
        public double? LastTarget;
        public double? LastRate;
        public int Channel => 1;
        public TempLimits Limits { get; } = new(-40, 180, 16);
        public double CurrentReactor => 25;
        public double CurrentJacket => 25;
        public Task SetTargetAsync(TempTarget t, CancellationToken ct) => Task.CompletedTask;
        public Task RampAsync(double t, double r, TempChannelKind k, CancellationToken ct)
        { LastTarget = t; LastRate = r; return Task.CompletedTask; }
        public Task<bool> WaitReachedAsync(double t, double tol, TimeSpan to, CancellationToken ct)
            => Task.FromResult(true);
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
        public IObservable<Sample> Temperature { get; } = new Broadcast<Sample>();
    }

    private sealed class StubStir : IStirrer
    {
        public double? LastRpm;
        public int Channel => 1;
        public SpeedLimits Limits { get; } = new(0, 1000);
        public double CurrentRpm => LastRpm ?? 0;
        public IObservable<Sample> Speed { get; } = new Broadcast<Sample>();
        public Task SetSpeedAsync(double rpm, CancellationToken ct)
        { LastRpm = rpm; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct) { LastRpm = 0; return Task.CompletedTask; }
    }

    private sealed class CapSession : IDeviceSession
    {
        private readonly ICapability[] _caps;
        public CapSession(params ICapability[] caps) => _caps = caps;
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
    }

    private static Step Start(params (string Key, object? Value)[] p)
        => Harness.Mk(BuiltinCommands.FirstFill, p);

    private static async Task Until(Func<bool> cond, int ms = 8000)
    {
        var sw = Stopwatch.StartNew();
        while (!cond())
        {
            Assert.True(sw.ElapsedMilliseconds < ms, "等待条件超时");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task 起始限值进安全层_初始搅拌与初温落到设备()
    {
        var temp = new StubTemp();
        var stir = new StubStir();
        var provider = new BuiltinCommandProvider(new AutoOperatorGate());
        var engine = new RunEngine(new CommandCatalog(), provider,
                                   new ResourceArbiter(), new DataPipeline(),
                                   () => DateTimeOffset.Now);
        provider.Safety = engine.Safety;
        try
        {
            var ch = new Channel(1, "X1", 0);
            ch.Attach(new CapSession(temp, stir), 0, true);
            engine.Attach(ch);
            engine.NewBatch("测试批次", "张三", "测试台面");

            var step = new Step
            {
                CommandId = BuiltinCommands.FirstFill,
                Parameters = ParameterSet.Of(("fill", true), ("stir", 300d),
                                             ("tempOn", true), ("temp", 50d)),
                Rows = new List<ParameterSet>
                {
                    ParameterSet.Of(("src", "釜内 Tr"), ("op", ">"), ("val", 100d), ("act", "中止本通道"))
                }
            };
            engine.StartChannel(1, Harness.RecipeOf("起始", step), "张三");
            var runner = engine.Runner(1)!;
            await runner.Completion.WaitAsync(TimeSpan.FromSeconds(10));

            // 起始限值走 FromRecipe 层：整趟实验的地板，不是这一步的临时覆盖
            var lim = Assert.Single(engine.Safety.Limits, l => l.FromRecipe);
            Assert.Equal("Tr", lim.Tag);
            Assert.Equal(100d, lim.Max);
            Assert.Equal("起始步骤设定", lim.Note);

            Assert.Equal(300d, stir.LastRpm);
            Assert.Equal(50d, temp.LastTarget);
            Assert.Equal(16d, temp.LastRate);    // 初温按设备最大变温能力去
        }
        finally
        {
            engine.AbortAll();
            engine.Dispose();
        }
    }

    // ── 位置规矩 ────────────────────────────────────────────────────

    [Fact]
    public void 有它就必须是第一步()
    {
        var r = Harness.RecipeOf("错位",
            Harness.Mk(BuiltinCommands.Wait, ("dur", 5d)),
            Start(("fill", true)));
        var issues = RecipeValidator.Validate(r, new CommandCatalog());
        Assert.Contains(issues, i => i.Code == "firstfill-order" && i.Level == IssueLevel.Error);
    }

    [Fact]
    public void 只能有一条()
    {
        var r = Harness.RecipeOf("重复", Start(("fill", true)), Start(("fill", true)));
        var issues = RecipeValidator.Validate(r, new CommandCatalog());
        Assert.Contains(issues, i => i.Code == "firstfill-dup" && i.Level == IssueLevel.Error);
    }

    [Fact]
    public void 老配方没有它只提醒不阻断_空配方连提醒都不给()
    {
        var old = Harness.RecipeOf("老配方", Harness.Mk(BuiltinCommands.Wait, ("dur", 5d)));
        Assert.Contains(RecipeValidator.Validate(old, new CommandCatalog()),
            i => i.Code == "firstfill-missing" && i.Level == IssueLevel.Warning);

        var blank = Harness.RecipeOf("空白");
        Assert.DoesNotContain(RecipeValidator.Validate(blank, new CommandCatalog()),
            i => i.Code == "firstfill-missing");
    }

    [Fact]
    public void 声明了初始搅拌或初温_通道就得真有那份能力()
    {
        var r = Harness.RecipeOf("要能力", Start(("fill", false), ("stir", 300d), ("tempOn", true)));
        var bare = new Channel(1, "X1", 0);              // 什么能力都没有
        var issues = RecipeValidator.Validate(r, new CommandCatalog(), bare);

        Assert.Equal(2, issues.Count(i => i.Code == "capability" && i.Level == IssueLevel.Error));
    }

    // ── 估算与摘要 ──────────────────────────────────────────────────

    [Fact]
    public void 初温按设备最大变温能力估_只投料不占排期()
    {
        var c = new CommandCatalog();

        var warm = Harness.RecipeOf("到初温", Start(("tempOn", true), ("temp", 57d), ("fill", false)));
        var s1 = Schedule.Build(warm, c, new EstimationContext { Temperature = 25, MaxTempRatePerMin = 16 });
        Assert.Equal(TimeSpan.FromMinutes(2), s1.Total);   // (57−25)/16

        var fillOnly = Harness.RecipeOf("只投料", Start(("fill", true)));
        Assert.Equal(TimeSpan.Zero, Schedule.Build(fillOnly, c).Total);
    }

    [Fact]
    public void 摘要按设了什么说话()
    {
        var c = new CommandCatalog();
        Assert.True(c.TryGet(BuiltinCommands.FirstFill, out var d));

        var full = new CommandInput(
            ParameterSet.Of(("fill", true), ("stir", 300d), ("tempOn", true), ("temp", 25d)),
            new[] { ParameterSet.Of(("src", "釜内 Tr"), ("op", ">"), ("val", 100d)) });
        Assert.Equal("投料 · 300 rpm · 初温 25 ℃ · 限值 1 条", d.SummaryOf(full));
        Assert.Equal("起始：按配料表投料，搅拌 300 rpm，初温 25 ℃，起始限值 1 条", d.DescribeOf(full));
    }
}
