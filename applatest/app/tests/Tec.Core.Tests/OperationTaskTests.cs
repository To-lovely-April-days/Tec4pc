using Tec.Core.Catalog;
using Tec.Core.Scheduling;
using Tec.Driver.Abi;
using Tec.Drivers.Simulator;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 到达方式（iControl 统一参数模型的 Task 段）。三条执行类指令各多了一档任务：
/// 控温「尽快 / 按时长 / 按速率」、搅拌「立即 / 按时长」、加料「一次加入 / 按速率」。
/// 这里盯三件事：**估算按设备真实能力走**（尽快 / 一次加入不许用编的速率）、
/// **老步骤没有 task 键行为逐字不变**（存过盘的配方零迁移）、摘要按方式说话。
/// </summary>
public class OperationTaskTests
{
    private static CommandCatalog Catalog()
    {
        var c = new CommandCatalog();
        c.Register(new Rd105ReactorDriver().Commands);
        c.Register(new DosingPumpDriver().Commands);
        return c;
    }

    // ── 估算 ────────────────────────────────────────────────────────

    [Fact]
    public void 控温尽快按设备最大变温能力估()
    {
        var r = Harness.RecipeOf("尽快",
            Harness.Mk(CommandSpecs.Control, ("target", 57d), ("task", "尽快")));
        var s = Schedule.Build(r, Catalog(),
            new EstimationContext { Temperature = 25, MaxTempRatePerMin = 16 });

        // (57 − 25) / 16 = 2 min——分母是播种进来的设备能力，不是参数里的 rate
        Assert.Equal(TimeSpan.FromMinutes(2), s.Total);
    }

    [Fact]
    public void 控温按时长直接用时长()
    {
        var r = Harness.RecipeOf("按时长",
            Harness.Mk(CommandSpecs.Control, ("target", 60d), ("task", "按时长"), ("dur", 12d)));
        var s = Schedule.Build(r, Catalog(), new EstimationContext { Temperature = 25 });

        Assert.Equal(TimeSpan.FromMinutes(12), s.Total);
    }

    [Fact]
    public void 老步骤没有task键_估算与从前逐字一致()
    {
        // 存过盘的老配方走到这儿：没有 task 键 → 按速率，(60−20)/2 = 20 min
        var r = Harness.RecipeOf("老配方",
            Harness.Mk(CommandSpecs.Control, ("target", 60d), ("rate", 2d)));
        var s = Schedule.Build(r, Catalog(), new EstimationContext { Temperature = 20 });

        Assert.Equal(TimeSpan.FromMinutes(20), s.Total);
    }

    [Fact]
    public void 一次加入按泵最大流量估()
    {
        var r = Harness.RecipeOf("一次加入",
            Harness.Mk(CommandSpecs.Dose, ("vol", 10d), ("task", "一次加入")));
        var s = Schedule.Build(r, Catalog(),
            new EstimationContext { MaxDoseRatePerMin = 20 });

        // 10 mL ÷ 20 mL/min = 30 s。泵送液要时间，「一次加入」不是零耗时
        Assert.Equal(TimeSpan.FromSeconds(30), s.Total);
    }

    [Fact]
    public void 搅拌立即不占排期()
    {
        var r = Harness.RecipeOf("立即",
            Harness.Mk(CommandSpecs.Stir, ("rpm", 400d), ("task", "立即")));
        var s = Schedule.Build(r, Catalog());

        Assert.Equal(TimeSpan.Zero, s.Total);
    }

    [Fact]
    public void 设备能力跟着估算上下文克隆走()
    {
        var seed = new EstimationContext { MaxTempRatePerMin = 8, MaxDoseRatePerMin = 5 };
        var c = seed.Clone();

        Assert.Equal(8, c.MaxTempRatePerMin);
        Assert.Equal(5, c.MaxDoseRatePerMin);
    }

    // ── 摘要与整句 ──────────────────────────────────────────────────

    [Fact]
    public void 摘要与整句按到达方式说话()
    {
        var c = Catalog();
        Assert.True(c.TryGet(CommandSpecs.Control, out var temp));

        var fast = new CommandInput(ParameterSet.Of(("target", 60d), ("obj", "釜内 Tr"), ("task", "尽快")));
        Assert.Equal("釜内 Tr → 60 ℃ · 尽快", temp.SummaryOf(fast));
        Assert.Equal("控温 釜内 Tr 至 60 ℃，尽快到达", temp.DescribeOf(fast));

        var timed = new CommandInput(ParameterSet.Of(
            ("target", 60d), ("obj", "釜内 Tr"), ("task", "按时长"), ("dur", 12d)));
        Assert.Equal("釜内 Tr → 60 ℃ · 12 min", temp.SummaryOf(timed));

        Assert.True(c.TryGet(CommandSpecs.Dose, out var dose));
        var once = new CommandInput(ParameterSet.Of(
            ("pump", "加料泵 1"), ("liq", "盐酸"), ("vol", 10d), ("task", "一次加入")));
        Assert.Equal("10 mL · 一次加入", dose.SummaryOf(once));
        Assert.Equal("加料泵 1 一次加入 10 mL「盐酸」（按泵最大流量）", dose.DescribeOf(once));

        Assert.True(c.TryGet(CommandSpecs.Stir, out var stir));
        var imm = new CommandInput(ParameterSet.Of(("rpm", 400d), ("task", "立即")));
        Assert.Equal("搅拌转速设为 400 rpm", stir.DescribeOf(imm));
    }

    [Fact]
    public void 老步骤的摘要一字不动()
    {
        // ValidatorAndRecordTests 也钉着缺省摘要；这里专盯「有 task 之后
        // 缺 task 的老参数仍走老话」——摘要变了，配方库和报告里的行就都变了
        var c = Catalog();
        Assert.True(c.TryGet(CommandSpecs.Dose, out var dose));
        var old = new CommandInput(ParameterSet.Of(
            ("pump", "加料泵 1"), ("liq", "反溶剂"), ("vol", 10d), ("rate", 0.5)));
        Assert.Equal("10 mL · 0.5 mL/min", dose.SummaryOf(old));

        Assert.True(c.TryGet(CommandSpecs.Stir, out var stir));
        var oldStir = new CommandInput(ParameterSet.Of(("rpm", 400d), ("ramp", 5d)));
        Assert.Equal("搅拌转速设为 400 rpm（5 s 内到达）", stir.DescribeOf(oldStir));
    }

    // ── 分组 ────────────────────────────────────────────────────────

    [Fact]
    public void pH两条各归各组_采集在采样_反馈加料在加料()
    {
        var c = new CommandCatalog();
        c.Register(new PhProbeDriver().Commands);

        Assert.Contains(c.InModule("采样"), d => d.Id == CommandSpecs.PhSample);
        Assert.Contains(c.InModule("加料"), d => d.Id == CommandSpecs.PhHold);
    }
}
