using Tec.Core.Benches;
using Tec.Core.Persistence;
using Tec.Core.Recipes;
using Tec.Driver.Abi;
using Xunit;

namespace Tec.Core.Tests;

public sealed class PersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "tecfiles-" + Guid.NewGuid().ToString("N")[..8]);

    public PersistenceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string name) => Path.Combine(_dir, name);

    private static Bench SampleBench()
    {
        var b = new Bench { Name = "四通道台面" };
        b.Devices.Add(new DeviceInstance
        {
            DriverId = "tec.reactor.rd105",
            InstanceId = "R1",
            Label = "反应器 A",
            Position = new Point(120.5, 88.25),
            Config = ParameterSet.Of(("volume", 100d), ("jacket", true), ("note", "左位"))
        });
        b.Devices.Add(new DeviceInstance
        {
            DriverId = "tec.probe.ph",
            InstanceId = "PH1",
            Position = new Point(60, 12),
            DockHostId = "R1",
            Dock = DockSide.Top,
            DockSlot = 1,
            DockAnchor = "T2b",
            DockSideTag = "R"
        });
        b.Bindings.Add(new Binding("PH1", 2));
        b.Bindings.Add(new Binding("P1", 1, BindingMode.Shared) { Port = 0 });
        var st = new Station { Name = "工位甲" };
        st.Channels.AddRange(new[] { 1, 2 });
        b.Stations.Add(st);
        return b;
    }

    private static Recipe SampleRecipe()
    {
        var r = new Recipe { Name = "降温结晶", Author = "工程师", Notes = "回归用" };
        r.Steps.Add(new Step
        {
            CommandId = "tec.stir.setSpeed",
            Parameters = ParameterSet.Of(("rpm", 400d), ("ramp", 5d))
        });
        r.Steps.Add(new Step
        {
            CommandId = "tec.temp.gradient",
            Parameters = ParameterSet.Of(("obj", "釜内 Tr"), ("loop", false)),
            Rows = new List<ParameterSet>
            {
                ParameterSet.Of(("t", 60d), ("r", 1d), ("h", 10d)),
                ParameterSet.Of(("t", 5d), ("r", 0.1d), ("h", 30d))
            },
            Comment = "三段降温"
        });
        return r;
    }

    // ── 台面 ─────────────────────────────────────────────────────────

    [Fact]
    public void 台面存出去再读回来每个字段都对得上()
    {
        var src = SampleBench();
        var path = P("a" + TecFiles.BenchExt);
        TecFiles.SaveBench(path, src.ToDoc());

        var back = new Bench();
        TecFiles.LoadBench(path).ApplyTo(back);

        Assert.Equal("四通道台面", back.Name);
        Assert.Equal(2, back.Devices.Count);

        var r1 = back.Device("R1")!;
        Assert.Equal("tec.reactor.rd105", r1.DriverId);
        Assert.Equal("反应器 A", r1.Label);
        Assert.Equal(120.5, r1.Position.X);
        Assert.Equal(88.25, r1.Position.Y);
        // 参数值必须还原成 double / bool / string，不能留成 JsonElement
        Assert.Equal(100d, r1.Config.Num("volume"));
        Assert.True(r1.Config.Flag("jacket"));
        Assert.Equal("左位", r1.Config.Str("note"));

        var ph = back.Device("PH1")!;
        Assert.Equal("R1", ph.DockHostId);
        Assert.Equal(DockSide.Top, ph.Dock);
        Assert.Equal(1, ph.DockSlot);
        Assert.Equal("T2b", ph.DockAnchor);
        Assert.Equal("R", ph.DockSideTag);

        Assert.Single(back.Stations);
        Assert.Equal(new[] { 1, 2 }, back.Stations[0].Channels);
    }

    [Fact]
    public void 读台面时丢掉指向不存在设备的绑定()
    {
        var src = SampleBench();          // P1 这台泵并不在 Devices 里
        var path = P("b" + TecFiles.BenchExt);
        TecFiles.SaveBench(path, src.ToDoc());

        var back = new Bench();
        TecFiles.LoadBench(path).ApplyTo(back);

        Assert.Single(back.Bindings);
        Assert.Equal("PH1", back.Bindings[0].DeviceId);
    }

    [Fact]
    public void 打开台面是替换不是合并()
    {
        var path = P("c" + TecFiles.BenchExt);
        TecFiles.SaveBench(path, SampleBench().ToDoc());

        var back = new Bench();
        back.Devices.Add(new DeviceInstance { DriverId = "x", InstanceId = "旧的" });
        TecFiles.LoadBench(path).ApplyTo(back);

        Assert.Null(back.Device("旧的"));
        Assert.Equal(2, back.Devices.Count);
    }

    // ── 配方 ─────────────────────────────────────────────────────────

    [Fact]
    public void 配方存出去再读回来含分段表()
    {
        var path = P("r" + TecFiles.RecipeExt);
        var src = SampleRecipe();
        TecFiles.SaveRecipe(path, src.ToDoc());

        var back = TecFiles.LoadRecipe(path).ToModel();

        Assert.Equal("降温结晶", back.Name);
        Assert.Equal("工程师", back.Author);
        Assert.Equal(2, back.Steps.Count);
        Assert.Equal(src.Steps[0].StepId, back.Steps[0].StepId);   // StepId 要保住，记录才对得上
        Assert.Equal(400d, back.Steps[0].Parameters.Num("rpm"));

        var g = back.Steps[1];
        Assert.Equal("三段降温", g.Comment);
        Assert.NotNull(g.Rows);
        Assert.Equal(2, g.Rows!.Count);
        Assert.Equal(0.1d, g.Rows[1].Num("r"));
        Assert.Equal("釜内 Tr", g.Parameters.Str("obj"));
        Assert.False(g.Parameters.Flag("loop"));
    }

    // ── 实验 ─────────────────────────────────────────────────────────

    [Fact]
    public void 实验文件带着台面配方与配方库一起走()
    {
        var doc = new ExperimentDoc
        {
            Name = "降温结晶_梯度筛选",
            Author = "管理员",
            Bench = SampleBench().ToDoc()
        };
        doc.Lanes.Add(new LaneDoc { Channel = 1, Name = "降温结晶", Recipe = SampleRecipe().ToDoc() });
        doc.Lanes.Add(new LaneDoc { Channel = 2, Name = "新配方", Recipe = new Recipe().ToDoc() });
        doc.Library.Add(SampleRecipe().ToDoc());

        var path = P("e" + TecFiles.ExperimentExt);
        TecFiles.Save(path, doc);
        var back = TecFiles.LoadExperiment(path);

        Assert.Equal("降温结晶_梯度筛选", back.Name);
        Assert.Equal(2, back.Bench.Devices.Count);
        Assert.Equal(2, back.Lanes.Count);
        Assert.Equal(1, back.Lanes[0].Channel);
        Assert.Equal("降温结晶", back.Lanes[0].Name);
        Assert.Equal(2, back.Lanes[0].Recipe.Steps.Count);
        Assert.Empty(back.Lanes[1].Recipe.Steps);
        Assert.Single(back.Library);
    }

    [Fact]
    public void 存盘会写上修改时间()
    {
        var doc = new ExperimentDoc { ModifiedAt = DateTimeOffset.Now.AddDays(-3) };
        var path = P("t" + TecFiles.ExperimentExt);
        TecFiles.Save(path, doc);

        var back = TecFiles.LoadExperiment(path);
        Assert.True(DateTimeOffset.Now - back.ModifiedAt < TimeSpan.FromMinutes(1));
    }

    // ── 出错时的表现 ─────────────────────────────────────────────────

    [Fact]
    public void 文件不存在报得清楚()
    {
        var ex = Assert.Throws<TecFileException>(() => TecFiles.LoadExperiment(P("没有这个.tec")));
        Assert.Contains("找不到文件", ex.Message);
    }

    [Fact]
    public void 内容坏掉不抛原始JSON异常而是给一句人话()
    {
        var path = P("bad" + TecFiles.ExperimentExt);
        File.WriteAllText(path, "{ \"name\": \"半份\", \"lanes\": [ ");

        var ex = Assert.Throws<TecFileException>(() => TecFiles.LoadExperiment(path));
        Assert.Contains("不完整", ex.Message);
    }

    [Fact]
    public void 比程序新的格式版本直接拒绝而不是硬读()
    {
        var path = P("future" + TecFiles.ExperimentExt);
        File.WriteAllText(path, "{ \"schema\": 99, \"name\": \"未来的实验\" }");

        var ex = Assert.Throws<TecFileException>(() => TecFiles.LoadExperiment(path));
        Assert.Contains("新版本", ex.Message);
    }

    [Fact]
    public void 台面格式版本超前同样拒绝()
    {
        var path = P("future" + TecFiles.BenchExt);
        File.WriteAllText(path, "{ \"schema\": 99, \"name\": \"未来的台面\" }");

        var doc = TecFiles.LoadBench(path);
        var ex = Assert.Throws<TecFileException>(() => doc.ApplyTo(new Bench()));
        Assert.Contains("新版本", ex.Message);
    }

    [Fact]
    public void 存盘失败不会毁掉原来那份()
    {
        var path = P("keep" + TecFiles.ExperimentExt);
        TecFiles.Save(path, new ExperimentDoc { Name = "第一版" });

        // 用一个存不进去的路径（目录名和已存在的文件同名）触发失败
        var blocked = Path.Combine(path, "sub" + TecFiles.ExperimentExt);
        Assert.Throws<TecFileException>(() => TecFiles.Save(blocked, new ExperimentDoc { Name = "第二版" }));

        Assert.Equal("第一版", TecFiles.LoadExperiment(path).Name);
    }

    [Fact]
    public void 同一份配方两次存出的字节完全一样()
    {
        // 参数键按字典序写。不这样的话每次存出来 diff 一片红，也没法算校验码
        var doc = SampleRecipe().ToDoc();
        Assert.Equal(TecJson.Write(doc), TecJson.Write(doc));

        var p1 = P("s1" + TecFiles.RecipeExt);
        var p2 = P("s2" + TecFiles.RecipeExt);
        TecFiles.SaveRecipe(p1, doc);
        TecFiles.SaveRecipe(p2, TecFiles.LoadRecipe(p1).ToModel().ToDoc());
        Assert.Equal(File.ReadAllText(p1), File.ReadAllText(p2));
    }
}
