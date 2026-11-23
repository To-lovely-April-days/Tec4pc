using Tec.Core;
using Tec.Core.Benches;
using Tec.Core.Catalog;
using Tec.Core.Chemistry;
using Tec.Core.Persistence;
using Tec.Core.Recipes;
using Tec.Core.Records;
using Tec.Core.Scheduling;
using Tec.Demo;
using Tec.Driver.Abi;
using Tec.DriverHost;
using Tec.Hmi.Runtime;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 【本地改动】演示模式（--demo）的回归：缺省关、仿真驱动包能当插件装入（同一个加载上下文）、
/// 演示工程能装入且配方真能过校验、历史归档能读回。全程不动 DemoMode 的全局开关——
/// 测试并行跑，翻全局会把别的测试里的 HmiRuntime 带进演示模式。
/// </summary>
public class DemoModeTests
{
    private static string SimDll => Path.Combine(AppContext.BaseDirectory, "Tec.Drivers.Simulator.dll");

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "tec-demo-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>把仿真程序集 + manifest 摆成一个插件目录（跟 Tec.App 输出目录里的 demo/drivers 同构）。</summary>
    private static string StagePlugins()
    {
        var root = Path.Combine(TempDir(), "drivers");
        DemoDrivers.Stage(Path.Combine(root, "sim"), SimDll);
        return root;
    }

    private static CommandCatalog Catalog()
    {
        var c = new CommandCatalog();
        c.Register(CommandSpecs.Temperature);
        c.Register(CommandSpecs.Stirring);
        c.Register(CommandSpecs.Dosing);
        c.Register(CommandSpecs.Ph);
        return c;
    }

    [Fact]
    public void 启动参数_缺省关_只认demo()
    {
        Assert.False(DemoMode.Parse(Array.Empty<string>(), out var s0));
        Assert.Equal(10, s0);
        Assert.False(DemoMode.Parse(new[] { "--windowed", "demo" }, out _));
        Assert.True(DemoMode.Parse(new[] { "--windowed", "--demo" }, out var s1));
        Assert.Equal(10, s1);
        Assert.True(DemoMode.Parse(new[] { "--DEMO=5" }, out var s2));
        Assert.Equal(5, s2);
        Assert.True(DemoMode.Parse(new[] { "--demo=999" }, out var s3));
        Assert.Equal(60, s3);   // 钳在 1～60
    }

    [Fact]
    public void 仿真驱动包_当插件装入_六个驱动同一个加载上下文()
    {
        var drivers = new DriverCatalog();
        var n = DemoDrivers.Load(drivers, StagePlugins());
        Assert.Equal(DemoDrivers.All.Length, n);
        Assert.Equal("", DemoDrivers.Missing(drivers));

        var reactor = drivers.Driver(DemoDrivers.Reactor)!;
        var pump = drivers.Driver(DemoDrivers.Feed)!;
        var ph = drivers.Driver(DemoDrivers.PhProbe)!;
        Assert.Equal(2, reactor.Info.ChannelsPerDevice);
        // 兄弟类型跟主类型在同一个程序集实例里（仿真泵 ↔ 仿真 pH 的联动靠它）
        Assert.Same(reactor.GetType().Assembly, pump.GetType().Assembly);
        Assert.Same(reactor.GetType().Assembly, ph.GetType().Assembly);
        // 而且是隔离加载的那一份，不是测试工程直接引用的那一份
        Assert.NotSame(typeof(Tec.Drivers.Simulator.Rd105ReactorDriver).Assembly, reactor.GetType().Assembly);
        // 包清单里每个都 Usable，设备库才列得出来
        foreach (var id in DemoDrivers.All) Assert.True(drivers.Package(id)!.Usable, id);
    }

    [Fact]
    public void 没有插件目录_装入返回零_不抛()
    {
        var drivers = new DriverCatalog();
        Assert.Equal(0, DemoDrivers.Load(drivers, Path.Combine(TempDir(), "nowhere")));
        Assert.Null(drivers.Driver(DemoDrivers.Reactor));
    }

    [Fact]
    public async Task 不开演示_HMI照旧_不扫插件目录_台面是缺省真机台面()
    {
        var plugins = StagePlugins();
        var old = DemoDrivers.Dir;
        DemoDrivers.Dir = plugins;
        try
        {
            var dir = TempDir();
            await using var rt = new HmiRuntime(dir, demo: false);
            rt.Boot();
            Assert.False(rt.Demo);
            Assert.Null(rt.Drivers.Driver(DemoDrivers.Reactor));          // 插件目录在，但没开演示就不扫
            Assert.True(rt.SeededDefaultBench);
            Assert.All(rt.Bench.Devices, d => Assert.False(d.DriverId.StartsWith("sim.", StringComparison.Ordinal)));
            Assert.False(File.Exists(Path.Combine(dir, "HmiPanel", "seq-CH1.json")));
        }
        finally { DemoDrivers.Dir = old; }
    }

    [Fact]
    public async Task 演示台面_HMI装入_八台设备全连上_能力齐全_配方过校验()
    {
        var dir = TempDir();
        // 插件目录指到摆好的那份（可执行旁边的 demo/drivers 在测试里只有 manifest 没有 dll）
        var old = DemoDrivers.Dir;
        DemoDrivers.Dir = StagePlugins();
        await using var rt = new HmiRuntime(dir, demo: true);
        try
        {
            rt.Boot(timeScale: 10);
            await rt.StartAsync();
        }
        finally { DemoDrivers.Dir = old; }

        Assert.True(rt.Demo);
        Assert.Equal(DemoBench.Name, rt.Bench.Name);
        Assert.Equal(8, rt.Bench.Devices.Count);          // LegacyDevices 不认 sim.*，一台都没被摘
        Assert.Equal(2, rt.Channels.Count);
        foreach (var id in new[] { DemoBench.Host, DemoBench.Tr1, DemoBench.Tr2, DemoBench.Ph1, DemoBench.Ph2, DemoBench.Stir1, DemoBench.Stir2, DemoBench.Feed })
        {
            Assert.NotNull(rt.Session(id));
            Assert.Null(rt.OpenFailure(id));
        }

        foreach (var n in new[] { 1, 2 })
        {
            var ch = rt.ChannelOf(n)!;
            Assert.NotNull(ch.Capabilities.Get<ITemperatureControl>());
            Assert.NotNull(ch.Capabilities.Get<IRefluxControl>());
            Assert.NotNull(ch.Capabilities.Get<IStirrer>());          // 来自独立的搅拌驱动器，不是反应器自带的
            Assert.NotNull(ch.Capabilities.Get<IDosing>());           // 两液一气进料系统
            Assert.NotNull(ch.Capabilities.Get<IScalarSensor>());     // 数字 pH 电极
            var pid = ch.Capabilities.Get<IPidTuningBench>();
            Assert.NotNull(pid);
            foreach (var a in new[] { PidActuator.Tec, PidActuator.Heater, PidActuator.Both })
                Assert.NotEmpty(pid!.Rows(a));
        }
        // 反应器配置「自带搅拌 = 无」：通道上只有一个搅拌源
        Assert.Single(rt.ChannelOf(1)!.Capabilities.All.OfType<IStirrer>());

        // 两条工作配方在这台台面上过校验（启动时引擎拦的就是 Error 级）
        var lib = DemoCompounds.Load();
        var c1 = DemoCharges.Channel1(lib);
        var c2 = DemoCharges.Channel2(lib);
        var e1 = RecipeValidator.Validate(DemoRecipes.Esterification(), rt.Catalog, rt.ChannelOf(1), charge: Stoichiometry.Solve(c1))
                                .Where(x => x.Level == IssueLevel.Error).ToList();
        var e2 = RecipeValidator.Validate(DemoRecipes.PhNeutralization(), rt.Catalog, rt.ChannelOf(2), charge: Stoichiometry.Solve(c2))
                                .Where(x => x.Level == IssueLevel.Error).ToList();
        Assert.True(e1.Count == 0, string.Join("；", e1.Select(x => x.Message)));
        Assert.True(e2.Count == 0, string.Join("；", e2.Select(x => x.Message)));

        // 面板文件写好了
        Assert.True(File.Exists(Path.Combine(dir, "HmiPanel", "seq-CH1.json")));
        Assert.True(File.Exists(Path.Combine(dir, "HmiPanel", "library.json")));
    }

    [Fact]
    public void 配方库_七条_全部能排期_配料表能算()
    {
        var catalog = Catalog();
        var lib = DemoRecipes.Library();
        Assert.InRange(lib.Count, 5, 8);
        Assert.Equal(lib.Count, lib.Select(r => r.Id).Distinct().Count());
        foreach (var r in lib)
        {
            var s = Schedule.Build(r, catalog);
            Assert.True(s.MissingCommands.Count == 0, $"{r.Name}: 缺指令 {string.Join(",", s.MissingCommands)}");
            Assert.True(s.Total > TimeSpan.FromMinutes(10), r.Name);
        }

        var compounds = DemoCompounds.Load();
        Assert.InRange(compounds.Count, 67, 80);
        Assert.Contains(compounds, c => c.Name == "苯甲酸" && c.Mw is > 122 and < 123);
        var c1 = Stoichiometry.Solve(DemoCharges.Channel1(compounds));
        var c2 = Stoichiometry.Solve(DemoCharges.Channel2(compounds));
        Assert.NotNull(c1.Limiting);
        Assert.NotNull(c2.Limiting);
        Assert.Empty(c1.Problems);
        Assert.Empty(c2.Problems);
        // 通道 1 加料步骤引用的配料行真在表里
        Assert.Contains(c1.Lines, l => l.Item.Id == DemoRecipes.EthanolRowId);
        Assert.NotEmpty(ChargeMatrix.Align(new[] { DemoCharges.Channel1(compounds), DemoCharges.Channel2(compounds) }));
    }

    [Fact]
    public void 历史归档_四炉_写出能读回_采样是实测曲线()
    {
        var root = Path.Combine(TempDir(), "Runs");
        Assert.Equal(4, DemoArchive.Write(root, Catalog(), "test"));
        Assert.Equal(0, DemoArchive.Write(root, Catalog(), "test"));   // 已有的不重写

        var archive = new RunArchive(root, "test");
        var runs = archive.Load();
        Assert.Empty(archive.Failures);
        Assert.Equal(4, runs.Count);
        foreach (var r in runs)
        {
            var ch = Assert.Single(r.Record.Channels);
            Assert.Equal(ChannelRunState.Completed, ch.State);
            Assert.False(ch.Simulated);                                 // 实测曲线，不标仿真
            Assert.True(ch.Steps.Count >= 2 && ch.Steps.Count % 2 == 0);
            Assert.Equal(ch.Steps.Count, ch.Baseline.Schedule.Entries.Count);
            var tr = r.Samples.Snapshot(1, "Tr");
            Assert.True(tr.Length > 400, $"{r.Record.RunId} 只有 {tr.Length} 个点");
            Assert.All(tr, s => Assert.Equal(Quality.Good, s.Quality));
            Assert.True(r.Samples.Snapshot(1, "Tj").Length == tr.Length);
        }
        Assert.Contains(runs, r => r.Record.RunId == "EXP-20261120-001" && r.Record.Channels[0].Steps.Count == 4);
    }

    [Fact]
    public void 演示实验文件_三份_读回不丢设备_不触发老台面迁移()
    {
        var dir = TempDir();
        var path = DemoPack.WriteExperiments(dir, DemoBench.Build(), DemoCompounds.Load());
        Assert.Equal(3, Directory.GetFiles(dir, "*" + TecFiles.ExperimentExt).Length);

        var doc = TecFiles.LoadExperiment(path);
        var bench = new Bench();
        var notes = doc.Bench.ApplyTo(bench);
        Assert.Empty(notes);                                            // sim.* 不在 LegacyDevices 的表里，一台不动
        Assert.Equal(8, bench.Devices.Count);
        Assert.Equal(8, bench.Bindings.Count);
        Assert.Equal(2, doc.Lanes.Count);
        Assert.All(doc.Lanes, l => Assert.NotNull(l.Charge));
        Assert.Equal(DemoPack.ExperimentName, doc.Name);
        foreach (var l in doc.Lanes) Assert.NotEmpty(l.Recipe.ToModel().Steps);
    }
}
