using Tec.Core.Chemistry;
using Tec.Core.Compounds;
using Tec.Core.Records;
using Tec.Driver.Abi;
using Tec.Drivers.Simulator;
using Xunit;

namespace Tec.Core.Tests;

public class DoseReadbackTests
{
    private static readonly Compound Ethanol = new()
    { Cas = "64-17-5", Name = "乙醇", Mw = 46.07, Density = 0.789 };

    private static readonly Compound Benzoic = new()
    { Cas = "65-85-0", Name = "苯甲酸", Mw = 122.12, Density = 1.266 };

    private static readonly Compound[] Lib = { Ethanol, Benzoic };

    /// <summary>苯甲酸 12.212 g 打底 + 乙醇按倍量。</summary>
    private static ChargeTable Table()
    {
        var t = new ChargeTable();
        t.Items.Add(new ChargeItem
        {
            Cas = "65-85-0", Name = "苯甲酸", Role = ChargeRole.Limiting,
            Basis = ChargeBasis.Quantity, Amount = 12.212, Unit = ChargeUnit.Gram
        });
        t.Items.Add(new ChargeItem
        {
            Cas = "64-17-5", Name = "乙醇", Role = ChargeRole.Solvent,
            Basis = ChargeBasis.Volumes, Amount = 2
        });
        return t;
    }

    /// <summary>真跑一炉加料，泵会一路发 volume 标签。</summary>
    private static async Task<Harness> RunAsync(params (string Liq, double Vol)[] doses)
    {
        var h = new Harness(600);
        await h.ReactorChannelAsync(1, withPump: true);

        var steps = doses.Select(d => Harness.Mk(CommandSpecs.Dose,
            ("pump", "加料泵 1"), ("liq", d.Liq), ("vol", d.Vol), ("rate", 20d), ("sync", true))).ToArray();

        h.Engine.StartChannel(1, Harness.RecipeOf("加料", steps), "王工", charge: Table());
        await h.Engine.Runner(1)!.Completion;
        return h;
    }

    [Fact]
    public async Task 从采样里读回这一步实际加了多少()
    {
        await using var h = await RunAsync(("乙醇", 12));
        var read = DoseReadback.Of(h.Engine.Record.Channels[0], h.Pipeline, h.Catalog);

        var one = Assert.Single(read);
        Assert.Equal("乙醇", one.Liquid);
        Assert.Equal(12, one.Planned);
        Assert.NotNull(one.Actual);
        // 定量泵送够就停，实际量就是命令量
        Assert.Equal(12, one.Actual!.Value, 2);
        Assert.Null(one.Problem);
    }

    [Fact]
    public async Task 读回来的量填进配料表的实取和实投()
    {
        await using var h = await RunAsync(("乙醇", 12));
        var table = Table();
        var charge = Stoichiometry.Solve(table, Lib);
        var read = DoseReadback.Of(h.Engine.Record.Channels[0], h.Pipeline, h.Catalog);

        var done = DoseReadback.Apply(table, read, charge);

        var one = Assert.Single(done);
        Assert.Equal("乙醇", one.Name);
        var item = table.Items.Single(i => i.Name == "乙醇");
        Assert.Equal(one.Volume, item.ActualVolume);
        // 密度知道就顺带把克数也填上——称量记录要的是克
        Assert.Equal(Math.Round(one.Volume * 0.789, 4), item.ActualMass);
    }

    [Fact]
    public async Task 同一个组分分几批加就加起来算一次()
    {
        // 分批加料就是拿循环把一条加料圈起来写的，配料表里仍然只有一行
        await using var h = await RunAsync(("乙醇", 5), ("乙醇", 7));
        var table = Table();
        var read = DoseReadback.Of(h.Engine.Record.Channels[0], h.Pipeline, h.Catalog);

        Assert.Equal(2, read.Count);
        DoseReadback.Apply(table, read, Stoichiometry.Solve(table, Lib));

        var item = table.Items.Single(i => i.Name == "乙醇");
        Assert.Equal(12, item.ActualVolume!.Value, 2);      // 5 + 7，泵送够就停
    }

    [Fact]
    public async Task 配料表里没有的料液不会凭空多出一行()
    {
        await using var h = await RunAsync(("乙酸乙酯", 8));
        var table = Table();
        var before = table.Items.Count;

        var done = DoseReadback.Apply(table, DoseReadback.Of(h.Engine.Record.Channels[0], h.Pipeline, h.Catalog),
                                      Stoichiometry.Solve(table, Lib));

        Assert.Empty(done);
        Assert.Equal(before, table.Items.Count);
        Assert.All(table.Items, i => Assert.Null(i.ActualVolume));
    }

    [Fact]
    public async Task 没采到泵的累计量就说读不出来而不是填零()
    {
        // 归档太久、早期采样被环形缓冲顶掉，就是这个样子
        await using var h = await RunAsync(("乙醇", 12));
        var empty = new EmptySamples();

        var one = Assert.Single(DoseReadback.Of(h.Engine.Record.Channels[0], empty, h.Catalog));
        Assert.Null(one.Actual);
        Assert.Contains("没有采到", one.Problem!);

        var table = Table();
        Assert.Empty(DoseReadback.Apply(table, new[] { one }, Stoichiometry.Solve(table, Lib)));
        Assert.All(table.Items, i => Assert.Null(i.ActualVolume));
    }

    [Fact]
    public async Task 不是加料的步骤不进这张表()
    {
        var h = new Harness(600);
        await using var _ = h;
        await h.ReactorChannelAsync(1, withPump: true);
        h.Engine.StartChannel(1, Harness.RecipeOf("混着来",
            Harness.Mk(CommandSpecs.Control, ("target", 40d), ("rate", 10d)),
            Harness.Mk(CommandSpecs.Dose, ("pump", "加料泵 1"), ("liq", "乙醇"), ("vol", 6d),
                       ("rate", 20d), ("sync", true))), "王工");
        await h.Engine.Runner(1)!.Completion;

        var read = DoseReadback.Of(h.Engine.Record.Channels[0], h.Pipeline, h.Catalog);
        Assert.Single(read);
        Assert.Equal("乙醇", read[0].Liquid);
    }

    [Fact]
    public async Task 实际比计划差多少算得出来()
    {
        await using var h = await RunAsync(("乙醇", 12));
        var one = Assert.Single(DoseReadback.Of(h.Engine.Record.Channels[0], h.Pipeline, h.Catalog));

        Assert.NotNull(one.Deviation);
        Assert.InRange(one.Deviation!.Value, -5, 5);
    }

    [Fact]
    public void 累计量回退说明泵重开过要照实说()
    {
        // 累计量是每个驱动会话各自累加的。中途重开会话就归零，
        // 差值成了负数——拿绝对值糊过去会得到一个假的「实加量」
        var run = FakeRun(new[] { (0, 0.0), (10, 8.0), (20, 2.0) }, from: 5, to: 25);
        var one = Assert.Single(DoseReadback.Of(run.Run, run.Samples, run.Catalog));

        Assert.Null(one.Actual);
        Assert.Contains("回退", one.Problem!);
        Assert.Contains("会话中途重开", one.Problem!);
    }

    [Fact]
    public void 采样被顶掉过就说读不出来而不是当成从零开始()
    {
        // 归档久了早期采样会被环形缓冲顶掉。那时候序列开头已经是个大数，
        // 拿它当「从 0 开始」会算出一个大得离谱的实加量——而那个数看着很像真的
        var run = FakeRun(new[] { (30, 40.0), (60, 52.0) }, from: 10, to: 70);

        Assert.Null(Assert.Single(DoseReadback.Of(run.Run, run.Samples, run.Catalog,
                                                  samplesTruncated: true)).Actual);
        Assert.Contains("顶掉", Assert.Single(DoseReadback.Of(run.Run, run.Samples, run.Catalog,
                                                            samplesTruncated: true)).Problem!);

        // 没被顶掉的话，序列开头之前就是没泵过，按 0 算
        Assert.Equal(52, Assert.Single(DoseReadback.Of(run.Run, run.Samples, run.Catalog)).Actual);
    }

    [Fact]
    public async Task 定量泵送够就停不超量()
    {
        // 从前泵只管积分，靠上层轮询发现超了再喊停：命令 5 mL 实际送出 8 mL。
        // 超出去那部分是真的进了釜——记录、釜容校验、配料表的实取会一路错下去。
        // 时标越大错得越离谱，而演示和回归跑的正是大时标
        await using var h = await RunAsync(("乙醇", 5), ("乙醇", 7));
        var snap = h.Pipeline.Snapshot(1, DoseReadback.VolumeTag);

        Assert.Equal(12, snap[^1].Value, 2);
        Assert.All(h.Engine.Record.Channels[0].Steps,
                   s => Assert.DoesNotContain("实加 8", s.Note ?? "", StringComparison.Ordinal));
    }

    [Fact]
    public void 步骤没跑起来就没有实加量()
    {
        var run = FakeRun(new[] { (0, 0.0), (10, 8.0) }, from: null, to: null);
        var one = Assert.Single(DoseReadback.Of(run.Run, run.Samples, run.Catalog));
        Assert.Null(one.Actual);
        Assert.Contains("没有跑起来", one.Problem!);
    }

    // ── 手搭一炉：采样点与时刻自己指定，才测得了那几种怪情况 ──────────

    private static (ChannelRun Run, Tec.Core.Data.ISampleSource Samples, Tec.Core.Catalog.CommandCatalog Catalog)
        FakeRun((int Sec, double Value)[] points, int? from, int? to)
    {
        var t0 = new DateTimeOffset(2026, 8, 17, 9, 0, 0, TimeSpan.Zero);
        var catalog = new Tec.Core.Catalog.CommandCatalog();
        catalog.Register(new DosingPumpDriver().Commands);

        var recipe = new Tec.Core.Recipes.Recipe { Name = "手搭" };
        var step = new Tec.Core.Recipes.Step
        {
            CommandId = CommandSpecs.Dose,
            Parameters = ParameterSet.Of(("pump", "加料泵 1"), ("liq", "乙醇"), ("vol", 12d),
                                         ("rate", 20d), ("sync", true))
        };
        recipe.Steps.Add(step);

        var run = new ChannelRun
        {
            Channel = 1,
            StartedAt = t0,
            Baseline = new RunBaseline
            {
                Recipe = recipe,
                Schedule = Tec.Core.Scheduling.Schedule.FromEntries(
                    Array.Empty<Tec.Core.Scheduling.ScheduleEntry>(), Array.Empty<string>()),
                FrozenAt = t0
            }
        };
        run.Append(new StepRecord
        {
            Index = 0, StepId = step.StepId, CommandId = CommandSpecs.Dose, Title = "加料",
            Termination = TerminationKind.Quantity, PlanStart = TimeSpan.Zero,
            PlanDuration = TimeSpan.FromMinutes(1), ChannelStart = t0,
            ActualStart = from is { } f ? t0.AddSeconds(f) : null,
            ActualEnd = to is { } e ? t0.AddSeconds(e) : null,
            Status = StepStatus.Done
        });

        return (run, new FixedSamples(t0, points), catalog);
    }

    private sealed class FixedSamples : Tec.Core.Data.ISampleSource
    {
        private readonly Sample[] _pts;
        public FixedSamples(DateTimeOffset t0, (int Sec, double Value)[] pts)
            => _pts = pts.Select(p => new Sample(1, DoseReadback.VolumeTag, 0,
                                                 t0.AddSeconds(p.Sec), p.Value, Quality.Good)).ToArray();

        public IReadOnlyList<Tec.Core.Data.SeriesKey> Keys
            => new[] { new Tec.Core.Data.SeriesKey(1, DoseReadback.VolumeTag) };
        public Sample[] Snapshot(int channel, string tag)
            => tag == DoseReadback.VolumeTag ? _pts : Array.Empty<Sample>();
        public TagDescriptor? Tag(string tag) => null;
    }

    private sealed class EmptySamples : Tec.Core.Data.ISampleSource
    {
        public IReadOnlyList<Tec.Core.Data.SeriesKey> Keys => Array.Empty<Tec.Core.Data.SeriesKey>();
        public Sample[] Snapshot(int channel, string tag) => Array.Empty<Sample>();
        public TagDescriptor? Tag(string tag) => null;
    }
}
