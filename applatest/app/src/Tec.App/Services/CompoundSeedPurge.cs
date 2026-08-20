using Tec.Core.Compounds;

namespace Tec.App.Services;

/// <summary>
/// 一次性清理：把早期版本开机自动灌进化合物库的十条参考物性删掉。
///
/// 那十条（苯甲酸 / 对乙酰氨基酚 / 布洛芬 / 蔗糖 …）是通用的结晶模型物，
/// 跟这台装置真正要跑的体系不沾边。留着有两个坏处：检索框里排在前面的永远是
/// 用不上的那些；更要紧的是「库里已经有数据」这件事本身会让人以为物性备齐了，
/// 而这一栏的数将来是要拿去算加料量、判析出、卡温度上限的。
///
/// 当初灌它们的理由是「物性是公开数据，不算伪造」——这话没错，但公开数据也分
/// 用得上用不上。空库不会骗人，一库用不上的数会。
///
/// **认的是「这条是程序写的」，不是「这条一个字没动过」。**
/// 一开始写成整条逐字段比对，实测在开发机上一条也没删着：盘里那十行是更早一版
/// 种子写的，带着 density=1.266、phase 空着，跟现在源码里这份对不上。
/// 种子改过几版，各版落盘的样子都不一样，逐字段比对等于要求「用户装的正好是
/// 我抄指纹的那一版」——这个前提不成立。
///
/// 改成认四样：CAS + 名称 + 备注 + 溶解度系数。后两样是程序自己编的内容
/// （「常用结晶模型物」「教学演示」、0.17,0.006,0.0006 这种三元组），
/// 人不会碰巧打出一模一样的。
///
/// 另外要求**批号和供应商都是空的**——这两栏一填，说明操作人把这条认领成了
/// 手头某一瓶真料，那就是他的数据了，留着。密度、相态那几栏不看：它们本来
/// 就是种子自己填的，不能拿来当「人动过」的证据。
///
/// 这份名单只作指纹用，永远不会再写进库里。
/// </summary>
public static class CompoundSeedPurge
{
    /// <summary>清过一次就记一笔，不必每次开机比一遍。</summary>
    public const string MetaKey = "compounds_seed_purged";

    /// <summary>
    /// 当年发出去的那十条。比对只用其中四样（CAS / 名称 / 备注 / 溶解度系数），
    /// 其余字段原样留着是为了看得出这几条是什么——别当成它们也参与比对。
    /// </summary>
    private static readonly Compound[] Shipped =
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

    /// <summary>库里哪几条该清。返回它们的 CAS 号；一条没动过的都没有就是空表。</summary>
    public static List<string> Pick(IEnumerable<Compound> rows)
    {
        var list = new List<string>();
        foreach (var r in rows)
            foreach (var s in Shipped)
                if (Same(r, s)) { list.Add(r.Cas); break; }
        return list;
    }

    /// <summary>库里这一行是不是当年程序自己写进去的那一条（见类说明里那四样 + 两样）。</summary>
    private static bool Same(Compound r, Compound s)
        => r.Cas == s.Cas
        && r.Name == s.Name
        && r.Note == s.Note
        && r.Solubility.SequenceEqual(s.Solubility)
        && r.Batch.Length == 0
        && r.Supplier.Length == 0;

    private static Compound C(string name, string cas, string formula, double mw, double mp,
                              string category, double[] sol, string solvent, string note,
                              string? structure = null, string? ion = null)
        => new()
        {
            Name = name, Cas = cas, Formula = formula, Mw = mw, Mp = mp,
            Category = category, Solubility = sol, Solvent = solvent, Note = note,
            StructureKey = structure, IonText = ion, Phase = "固"
        };
}
