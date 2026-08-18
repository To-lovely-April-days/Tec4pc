using Tec.Core.Compounds;

namespace Tec.App.Services;

/// <summary>
/// 程序自带的化合物参考数据，第一次建库时灌进去，之后以库里的为准。
///
/// 这跟配方库不一样，不违背「不伪造数据」：CAS 号、分子量、熔点是公开的物性数据，
/// 在哪台机器上都是这个值，属于程序应该自带的参考资料；配方是**工艺**，
/// 预置几条会让人以为工艺已经配好了，那才是伪造。
///
/// 灌过一次就在 meta 里记一笔，操作人把某一条删了不会下次开机又冒出来。
/// </summary>
public static class CompoundSeed
{
    /// <summary>库里一条都没有、也从没灌过时才灌。返回灌了几条。</summary>
    public const string MetaKey = "compounds_seeded";

    public static IReadOnlyList<Compound> All { get; } = new[]
    {
        C("苯甲酸", "65-85-0", "C7H6O2", 122.12, 122.4, "有机酸",
          new[] { 0.17, 0.006, 0.0006 }, "水 / 乙醇", "常用结晶模型物", "BenzoicAcid"),
        C("水杨酸", "69-72-7", "C7H6O3", 138.12, 158.6, "有机酸",
          new[] { 0.12, 0.004, 0.0005 }, "水 / 乙醇", "温度敏感", "SalicylicAcid"),
        C("柠檬酸", "77-92-9", "C6H8O7", 192.12, 153.0, "有机酸",
          new[] { 54, 1.5, 0.012 }, "水", "高溶解度", "CitricAcid"),
        C("对乙酰氨基酚", "103-90-2", "C8H9NO2", 151.16, 169.0, "药物",
          new[] { 0.8, 0.03, 0.002 }, "水 / 乙醇", "药物结晶筛选常用", "Paracetamol"),
        C("布洛芬", "15687-27-1", "C13H18O2", 206.28, 76.0, "药物",
          new[] { 0.002, 0.0004, 0.00008 }, "乙醇 / 乙酸乙酯", "难溶于水", "Ibuprofen"),
        C("甘氨酸", "56-40-6", "C2H5NO2", 75.07, 233.0, "氨基酸",
          new[] { 14.2, 0.44, 0.004 }, "水", "多晶型 α/β/γ", "Glycine"),
        C("L-谷氨酸", "56-86-0", "C5H9NO4", 147.13, 199.0, "氨基酸",
          new[] { 0.35, 0.02, 0.001 }, "水", "多晶型 α/β", "GlutamicAcid"),
        C("硫酸铵", "7783-20-2", "(NH4)2SO4", 132.14, 235.0, "无机盐",
          new[] { 70.6, 0.25, 0.0 }, "水", "盐析常用", null, "2 NH₄⁺ + SO₄²⁻"),
        C("氯化钾", "7447-40-7", "KCl", 74.55, 770.0, "无机盐",
          new[] { 28, 0.32, 0.0 }, "水", "教学演示", null, "K⁺ + Cl⁻"),
        C("蔗糖", "57-50-1", "C12H22O11", 342.30, 186.0, "糖类",
          new[] { 179, 1.1, 0.02 }, "水", "高粘度体系", "Sucrose")
    };

    private static Compound C(string name, string cas, string formula, double mw, double mp,
                              string category, double[] sol, string solvent, string note,
                              string? structure = null, string? ion = null)
        => new()
        {
            Name = name, Cas = cas, Formula = formula, Mw = mw, Mp = mp,
            Category = category, Solubility = sol, Solvent = solvent, Note = note,
            StructureKey = structure, IonText = ion,
            // 自带这十条全是室温固体（熔点最低的布洛芬也有 76 ℃），照实标上——
            // 相态和熔点一样是公开物性，不算伪造
            Phase = "固"
        };
}
