using Tec.Core.Chemistry;
using Tec.Core.Persistence;
using Tec.Core.Records;
using Xunit;

namespace Tec.Core.Tests;

public class ChargePersistenceTests
{
    private static ChargeTable Sample()
    {
        var t = new ChargeTable { VesselVolume = 250 };
        t.Items.Add(new ChargeItem
        {
            Cas = "119-27-7", Name = "2,4-二硝基苯甲醚（内部代号 A-7）",
            Role = ChargeRole.Limiting, Basis = ChargeBasis.Quantity,
            Amount = 19.813, Unit = ChargeUnit.Gram,
            Batch = "20260817-3", Supplier = "所内合成", ActualMass = 19.82,
            Note = "见工艺卡 A-7，避光"
        });
        t.Items.Add(new ChargeItem
        {
            Cas = "7697-37-2", Name = "硝酸 65%", Role = ChargeRole.Reagent,
            Basis = ChargeBasis.Equivalents, Amount = 1.2, Purity = 68, Density = 1.41
        });
        t.Items.Add(new ChargeItem
        {
            Name = "甲苯", Role = ChargeRole.Solvent, Basis = ChargeBasis.Volumes,
            Amount = 10, Mw = 92.14, Density = 0.8669
        });
        return t;
    }

    [Fact]
    public void 配料表存进实验文件再读回来一个字段不少()
    {
        var src = Sample();
        var back = src.ToDoc().ToModel();

        Assert.Equal(250, back.VesselVolume);
        Assert.Equal(3, back.Items.Count);

        var a = back.Items[0];
        // 中文名 + 全角括号 + 逗号，整条链都得原样过
        Assert.Equal("2,4-二硝基苯甲醚（内部代号 A-7）", a.Name);
        Assert.Equal("119-27-7", a.Cas);
        Assert.Equal(ChargeRole.Limiting, a.Role);
        Assert.Equal(ChargeBasis.Quantity, a.Basis);
        Assert.Equal(19.813, a.Amount);
        Assert.Equal(ChargeUnit.Gram, a.Unit);
        Assert.Equal("20260817-3", a.Batch);
        Assert.Equal("所内合成", a.Supplier);
        Assert.Equal(19.82, a.ActualMass);
        Assert.Equal("见工艺卡 A-7，避光", a.Note);

        var b = back.Items[1];
        Assert.Equal(68, b.Purity);              // 行上写死的纯度，不是库里那个
        Assert.Equal(1.41, b.Density);
        Assert.Equal(1.2, b.Amount);

        var c = back.Items[2];
        Assert.Equal(ChargeBasis.Volumes, c.Basis);
        Assert.Equal(92.14, c.Mw);
        Assert.Equal("", c.Cas);                 // 不连库的行
    }

    [Fact]
    public void 没填的那几项读回来还是没填不是零()
    {
        var t = new ChargeTable();
        t.Items.Add(new ChargeItem { Name = "只有名字" });

        var back = t.ToDoc().ToModel().Items[0];
        Assert.Null(back.Amount);
        Assert.Null(back.Mw);
        Assert.Null(back.Density);
        Assert.Null(back.Purity);
        Assert.Null(back.ActualMass);
        Assert.Null(back.ActualVolume);
    }

    [Fact]
    public void 走一遍JSON也不掉东西()
    {
        // 实验文件是 JSON 存的。枚举、可空 double、中文全角标点这几样最容易在这一步栽
        var doc = new ExperimentDoc { Name = "配料自测" };
        doc.Lanes.Add(new LaneDoc { Channel = 1, Name = "CH1", Charge = Sample().ToDoc() });

        var back = TecJson.Read<ExperimentDoc>(TecJson.Write(doc));
        var charge = back.Lanes[0].Charge!.ToModel();

        Assert.Equal(3, charge.Items.Count);
        Assert.Equal("2,4-二硝基苯甲醚（内部代号 A-7）", charge.Items[0].Name);
        Assert.Equal(ChargeRole.Solvent, charge.Items[2].Role);
        Assert.Equal(ChargeBasis.Volumes, charge.Items[2].Basis);
        Assert.Equal(250, charge.VesselVolume);
    }

    [Fact]
    public void 老实验文件没有配料表这一项照样打得开()
    {
        // 这一版之前存的文件里根本没有 charge 字段
        const string json = """
        {"schema":1,"name":"老实验","lanes":[{"channel":1,"name":"CH1",
         "recipe":{"schema":1,"id":"aa","name":"老配方","steps":[]}}]}
        """;
        var doc = TecJson.Read<ExperimentDoc>(json);

        Assert.Equal("老实验", doc.Name);
        Assert.Null(doc.Lanes[0].Charge);        // null = 这一路还没配料，不是一张空表
    }

    [Fact]
    public void 老文件里的行没有Id也补得上()
    {
        // Id 是界面认行的凭据。空着的话点一行选中的是另一行
        var doc = new ChargeTableDoc();
        doc.Items.Add(new ChargeItemDoc { Name = "无 Id 的行" });
        doc.Items.Add(new ChargeItemDoc { Name = "另一行" });

        var back = doc.ToModel();
        Assert.All(back.Items, i => Assert.NotEqual("", i.Id));
        Assert.NotEqual(back.Items[0].Id, back.Items[1].Id);
    }

    [Fact]
    public void 冻进记录的是副本启动之后再改配料表不动这一炉()
    {
        var live = Sample();
        var frozen = new RunBaseline
        {
            Recipe = new Tec.Core.Recipes.Recipe(),
            Schedule = Tec.Core.Scheduling.Schedule.FromEntries(
                Array.Empty<Tec.Core.Scheduling.ScheduleEntry>(), Array.Empty<string>()),
            FrozenAt = DateTimeOffset.UnixEpoch,
            Charge = live.Clone()
        };

        live.Items[1].Amount = 3.0;              // 跑起来之后有人把当量改了
        live.Items.RemoveAt(2);

        Assert.Equal(1.2, frozen.Charge!.Items[1].Amount);
        Assert.Equal(3, frozen.Charge.Items.Count);
    }
}
