using Tec.Core.Chemistry;
using Tec.Core.Compounds;

namespace Tec.Demo;

/// <summary>
/// 【本地改动】演示配料表：两路各一张填好的表（限制试剂、当量、倍量、纯度），物性从演示化合物库盖快照。
///
/// WHY：配料表页与对照矩阵要有数；通道 1 的加料步骤引用「无水乙醇」那一行（chemId），
/// 「应用到加料步骤」「跟随」才有对得上的东西。两张表都有乙醇 / 水，对照矩阵才有同行可比。
/// </summary>
public static class DemoCharges
{
    private static readonly DateTimeOffset Stamp = new(2026, 11, 20, 14, 5, 0, TimeSpan.FromHours(8));

    public static ChargeTable Channel1(IReadOnlyList<Compound> lib)
    {
        var t = new ChargeTable { VesselVolume = 250 };
        t.Items.Add(Row(lib, "chg-c1-ba", "苯甲酸", ChargeRole.Limiting, ChargeBasis.Quantity, 12.21, ChargeUnit.Gram, "固", "B20261018-02", "国药"));
        t.Items.Add(Row(lib, DemoRecipes.EthanolRowId, "乙醇（无水）", ChargeRole.Reagent, ChargeBasis.Equivalents, 4.0, ChargeUnit.Milliliter, "液", "E20261101-07", "泰坦"));
        t.Items.Add(Row(lib, "chg-c1-tol", "甲苯", ChargeRole.Solvent, ChargeBasis.Volumes, 4.0, ChargeUnit.Milliliter, "液", "T20260912-11", "泰坦"));
        t.Items.Add(Row(lib, "chg-c1-h2so4", "硫酸（浓）", ChargeRole.Catalyst, ChargeBasis.Equivalents, 0.05, ChargeUnit.Milliliter, "液", "S20260830-03", "国药"));
        t.Items.Add(Row(lib, "chg-c1-h2o", "水", ChargeRole.Quench, ChargeBasis.Volumes, 2.0, ChargeUnit.Milliliter, "液", "", "去离子"));
        t.Items.Add(Row(lib, "chg-c1-prod", "苯甲酸乙酯", ChargeRole.Product, ChargeBasis.Equivalents, 1.0, ChargeUnit.Gram, "液", "", ""));
        return t;
    }

    public static ChargeTable Channel2(IReadOnlyList<Compound> lib)
    {
        var t = new ChargeTable { VesselVolume = 250 };
        t.Items.Add(Row(lib, "chg-c2-apap", "对乙酰氨基酚", ChargeRole.Limiting, ChargeBasis.Quantity, 15.1, ChargeUnit.Gram, "固", "P20261105-01", "山东新华"));
        t.Items.Add(Row(lib, "chg-c2-h2o", "水", ChargeRole.Solvent, ChargeBasis.Volumes, 5.0, ChargeUnit.Milliliter, "液", "", "去离子"));
        t.Items.Add(Row(lib, "chg-c2-etoh", "乙醇（无水）", ChargeRole.Solvent, ChargeBasis.Volumes, 2.0, ChargeUnit.Milliliter, "液", "E20261101-07", "泰坦"));
        t.Items.Add(Row(lib, "chg-c2-naoh", "氢氧化钠", ChargeRole.Reagent, ChargeBasis.Equivalents, 1.05, ChargeUnit.Gram, "固", "N20261010-04", "国药"));
        t.Items.Add(Row(lib, DemoRecipes.HclRowId, "盐酸", ChargeRole.Quench, ChargeBasis.Quantity, 50, ChargeUnit.Milliliter, "液", "H20261102-09", "国药"));
        return t;
    }

    private static ChargeItem Row(IReadOnlyList<Compound> lib, string id, string name, ChargeRole role, ChargeBasis basis,
                                  double amount, ChargeUnit unit, string phase, string batch, string supplier)
    {
        var c = DemoCompounds.Find(lib, name);
        return new ChargeItem
        {
            Id = id, Name = name, Cas = c?.Cas ?? "", Role = role, Basis = basis, Amount = amount, Unit = unit,
            Mw = c?.Mw, Density = c?.Density, Purity = c?.Purity, Bp = c?.Bp, Mp = c?.Mp,
            Phase = phase, Batch = batch, Supplier = supplier,
            SnapshotAt = c is null ? null : Stamp, LibraryVersion = c is null ? null : 1
        };
    }
}
