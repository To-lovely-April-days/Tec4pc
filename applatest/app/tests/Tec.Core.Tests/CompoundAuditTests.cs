using Tec.Core.Compounds;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 化合物库编辑审计的变化清单（CH-1.7/6.1）。日志是给复核的人读的，
/// 所以盯两件事：句子读得懂（旧值新值都在），以及**没变的不说**——
/// 空清单是「不落日志」的凭据。
/// </summary>
public class CompoundAuditTests
{
    private static Compound C() => new()
    {
        Cas = "65-85-0",
        Name = "苯甲酸",
        Formula = "C7H6O2",
        Mw = 122.12,
        Density = 1.266,
        Purity = 99.5,
        Category = "有机酸"
    };

    [Fact]
    public void 数值改了_旧值新值都在句子里()
    {
        var after = C(); after.Density = 1.27;
        Assert.Equal(new[] { "密度 1.266 → 1.27" }, CompoundAudit.Diff(C(), after));
    }

    [Fact]
    public void 补上原来空着的_说补上不编旧值()
    {
        var after = C(); after.Bp = 249;
        Assert.Equal(new[] { "补上沸点 249" }, CompoundAudit.Diff(C(), after));
    }

    [Fact]
    public void 清掉一个数_原值留在括号里()
    {
        var after = C(); after.Purity = null;
        Assert.Equal(new[] { "清掉纯度（原 99.5）" }, CompoundAudit.Diff(C(), after));
    }

    [Fact]
    public void 数值照存的位数写_不吃显示层的四舍五入()
    {
        // 表格按 F3 显示成 1.137，日志里必须是库里那个 1.1371——抹了位数就对不上库
        var before = C(); before.Density = 1.1371;
        var after = C(); after.Density = 1.6448;
        Assert.Equal(new[] { "密度 1.1371 → 1.6448" }, CompoundAudit.Diff(before, after));
    }

    [Fact]
    public void 文字改了_两头都带引号()
    {
        var after = C(); after.Name = "苯甲酸钠";
        Assert.Equal(new[] { "名称「苯甲酸」→「苯甲酸钠」" }, CompoundAudit.Diff(C(), after));
    }

    [Fact]
    public void 长备注掐头_日志一行别成小作文()
    {
        var after = C(); after.Note = new string('长', 40);
        var line = Assert.Single(CompoundAudit.Diff(C(), after));
        Assert.Contains("…", line);
        Assert.True(line.Length < 40);
    }

    [Fact]
    public void 一个字都没变_清单是空的()
    {
        Assert.Empty(CompoundAudit.Diff(C(), C()));
    }

    [Fact]
    public void 库里没有旧行_一句新增()
    {
        Assert.Equal(new[] { "新增" }, CompoundAudit.Diff(null, C()));
    }

    [Fact]
    public void 同时改了几处_一处一句()
    {
        var after = C();
        after.Density = 1.27;
        after.Supplier = "国药";
        var list = CompoundAudit.Diff(C(), after);
        Assert.Equal(2, list.Count);
        Assert.Contains("密度 1.266 → 1.27", list);
        Assert.Contains("补上供应商「国药」", list);
    }

    [Fact]
    public void 溶解度系数只说动过_不往日志里倒数字()
    {
        var before = C(); before.Solubility = new[] { 0.17, 0.006, 0.0006 };
        var after = C(); after.Solubility = new[] { 0.18, 0.006, 0.0006 };
        Assert.Equal(new[] { "溶解度拟合系数已更新" }, CompoundAudit.Diff(before, after));

        var filled = C(); filled.Solubility = new[] { 0.17 };
        Assert.Equal(new[] { "补上溶解度拟合系数" }, CompoundAudit.Diff(C(), filled));
    }

    [Fact]
    public void 相态从未填到固_按补上说()
    {
        var after = C(); after.Phase = "固";
        Assert.Equal(new[] { "补上相态「固」" }, CompoundAudit.Diff(C(), after));
    }

    [Fact]
    public void 指认写法_有CAS带括号_内部键不冒充CAS()
    {
        Assert.Equal("苯甲酸（65-85-0）", CompoundAudit.Describe(C()));
        Assert.Equal("中间体 A-7",
            CompoundAudit.Describe(new Compound { Cas = Compound.KeyOf("中间体 A-7"), Name = "中间体 A-7" }));
    }
}
