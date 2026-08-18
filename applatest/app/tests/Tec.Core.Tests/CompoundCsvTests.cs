using Tec.Core.Compounds;
using Tec.Core.Persistence;
using Xunit;

namespace Tec.Core.Tests;

public class CompoundCsvTests
{
    [Fact]
    public void 中文表头顺序随便排也认得出()
    {
        var csv = "备注,名称,密度 (g/mL),CAS 号,纯度%,摩尔质量\n"
                + "常用结晶模型物,苯甲酸,1.266,65-85-0,99.5,122.12\n";
        var r = CompoundCsv.Read(csv);

        Assert.Empty(r.Problems);
        var c = Assert.Single(r.Items);
        Assert.Equal("苯甲酸", c.Name);
        Assert.Equal("65-85-0", c.Cas);
        Assert.Equal(1.266, c.Density);
        Assert.Equal(99.5, c.Purity);
        Assert.Equal(122.12, c.Mw);
        Assert.Equal("常用结晶模型物", c.Note);
        // 没给的列留空，不是 0——0 和「不知道」是两回事
        Assert.Null(c.Bp);
        Assert.Null(c.Cp);
    }

    [Theory]
    // 真机上踩到过的那一种：「密度 g/mL」不带括号。先并空格再截斜杠的话只剩「密度g」，
    // 整列悄无声息地认不出来——导进去密度全空，界面上还写着「已导入 3 条」
    [InlineData("密度 g/mL")]
    [InlineData("密度(g/mL)")]
    [InlineData("密度（g/mL）")]
    [InlineData("密度g/cm3")]
    [InlineData("Density")]
    public void 带单位后缀的列名也认得出(string header)
    {
        var r = CompoundCsv.Read($"名称,{header}\n甲苯,0.867\n");
        Assert.Empty(r.IgnoredColumns);
        Assert.Equal(0.867, Assert.Single(r.Items).Density);
    }

    [Fact]
    public void 英文表头也认()
    {
        var r = CompoundCsv.Read("name,cas,MolarMass,density,purity\nToluene,108-88-3,92.14,0.867,99.9\n");
        var c = Assert.Single(r.Items);
        Assert.Equal("Toluene", c.Name);
        Assert.Equal(0.867, c.Density);
    }

    [Fact]
    public void 内部代号没有CAS也进得来且不会互相覆盖()
    {
        // 含能材料的中间体多半是内部代号，公开库里根本查不到——这是刚需不是加分项
        var r = CompoundCsv.Read("名称,分子式,摩尔质量\nA-7 中间体,C7H5N3O6,227.13\nB-12 中间体,C6H6N4O5,214.14\n");

        Assert.Equal(2, r.Items.Count);
        // 库按 CAS 认人。留空的话第二条会把第一条盖掉，于是「导了两条只剩一条」
        Assert.NotEqual(r.Items[0].Cas, r.Items[1].Cas);
        // 主键位上坐着的是内部键，不是 CAS——它不该露到界面「CAS 号」那一列去
        Assert.False(r.Items[0].HasCas);
        Assert.False(r.Items[1].HasCas);
    }

    [Fact]
    public void 同一份表导第二遍不会多出一份()
    {
        // 没有 CAS 的那几条，内部键必须**按名字算得出来**：随机生成的话，
        // 每导一遍库里就多一份同名的，翻到第三遍已经分不清该用哪条了
        const string csv = "名称,摩尔质量\nA-7 中间体,227.13\n";
        Assert.Equal(CompoundCsv.Read(csv).Items[0].Cas, CompoundCsv.Read(csv).Items[0].Cas);
    }

    [Fact]
    public void 没有CAS的条目导出去是空格子且导回来还认得同一条()
    {
        var one = CompoundCsv.Read("名称,密度\nA-7 中间体,1.34\n").Items[0];

        // 内部键写进 CAS 列的话，人拿 Excel 打开会以为那就是它的登记号
        var line = CompoundCsv.Write(new[] { one }).Split("\r\n")[1];
        Assert.DoesNotContain(Compound.KeyMark.ToString(), line, StringComparison.Ordinal);

        var back = CompoundCsv.Read(CompoundCsv.Write(new[] { one })).Items[0];
        Assert.Equal(one.Cas, back.Cas);        // 认回同一条，不是新增一条
        Assert.False(back.HasCas);
        Assert.Equal(1.34, back.Density);
    }

    [Fact]
    public void 带引号与逗号的备注不会被拆成两列()
    {
        var r = CompoundCsv.Read("名称,备注,摩尔质量\n甘氨酸,\"多晶型 α, β, γ\",75.07\n");
        var c = Assert.Single(r.Items);
        Assert.Equal("多晶型 α, β, γ", c.Note);
        Assert.Equal(75.07, c.Mw);
    }

    [Fact]
    public void 一格里换行也读得对()
    {
        var r = CompoundCsv.Read("名称,备注\n水杨酸,\"温度敏感\n避光保存\"\n");
        var c = Assert.Single(r.Items);
        Assert.Contains("避光保存", c.Note);
    }

    [Fact]
    public void 数字读不出来就留空并逐条报出行号()
    {
        var r = CompoundCsv.Read("名称,密度,纯度\n甲苯,大约零点九,105\n");
        var c = Assert.Single(r.Items);

        Assert.Null(c.Density);
        Assert.Null(c.Purity);          // 105 % 超出范围，不收
        Assert.Equal(2, r.Problems.Count);
        // 行号要能对上人打开文件看到的那一行
        Assert.All(r.Problems, p => Assert.Contains("第 2 行", p));
        Assert.Contains(r.Problems, p => p.Contains("密度"));
        Assert.Contains(r.Problems, p => p.Contains("纯度"));
    }

    [Fact]
    public void 认不出来的列照实列出来不悄悄丢掉()
    {
        var r = CompoundCsv.Read("名称,闪点,危险类别,摩尔质量\n甲苯,4,易燃液体,92.14\n");
        Assert.Single(r.Items);
        Assert.Contains("闪点", r.IgnoredColumns);
        Assert.Contains("危险类别", r.IgnoredColumns);
    }

    [Fact]
    public void 不是表头的文件明说而不是硬读()
    {
        var r = CompoundCsv.Read("这是一份说明文档\n第二行也不是表格\n");
        Assert.False(r.Ok);
        Assert.Contains(r.Problems, p => p.Contains("不像表头"));
    }

    [Fact]
    public void 空文件与空行()
    {
        Assert.False(CompoundCsv.Read("").Ok);
        var r = CompoundCsv.Read("名称,摩尔质量\n\n甲苯,92.14\n\n");
        Assert.Single(r.Items);
        Assert.Empty(r.Problems);
    }

    [Fact]
    public void 带BOM的Excel文件第一列认得出来()
    {
        // Excel 存出来的中文 CSV 一定带 BOM，不吃掉的话第一个列名就认不出来
        var r = CompoundCsv.Read("﻿名称,摩尔质量\n甲苯,92.14\n");
        Assert.Single(r.Items);
        Assert.Equal("甲苯", r.Items[0].Name);
    }

    [Fact]
    public void 导出再导入一个字段不少()
    {
        var src = new[]
        {
            new Compound
            {
                Name = "2,4-二硝基苯甲醚（内部代号 A-7）", Cas = "119-27-7", Formula = "C7H6N2O5",
                Mw = 198.13, Mp = 94.5, Density = 1.34, Bp = 320.5, Purity = 98.5, Cp = 1.24,
                Category = "中间体", Solvent = "乙醇", Batch = "20260817-3", Supplier = "所内合成",
                Note = "见工艺卡 A-7，避光",
                Solubility = new[] { 0.12, 0.004, 0.0005 }
            }
        };

        var back = CompoundCsv.Read(CompoundCsv.Write(src));
        Assert.Empty(back.Problems);
        var c = Assert.Single(back.Items);

        Assert.Equal(src[0].Name, c.Name);          // 中文名 + 全角括号 + 逗号，整条链都得原样过
        Assert.Equal(src[0].Cas, c.Cas);
        Assert.Equal(src[0].Mw, c.Mw);
        Assert.Equal(src[0].Density, c.Density);
        Assert.Equal(src[0].Bp, c.Bp);
        Assert.Equal(src[0].Purity, c.Purity);
        Assert.Equal(src[0].Cp, c.Cp);
        Assert.Equal(src[0].Batch, c.Batch);
        Assert.Equal(src[0].Supplier, c.Supplier);
        Assert.Equal(src[0].Note, c.Note);
        Assert.Equal(src[0].Solubility, c.Solubility);
    }

    [Fact]
    public void 没填熔点不会变成零度()
    {
        // 甲苯的熔点是 −95 ℃。没填就显示 0.0 ℃，等于替这条料断言了一个它没有的值——
        // 而这一列正是拿来判「室温下是固体还是液体」的
        var r = CompoundCsv.Read("名称,摩尔质量\n甲苯,92.14\n");
        var c = Assert.Single(r.Items);
        Assert.Null(c.Mp);

        // 水的熔点就是 0 ℃：0 是个正经值，不能拿它当「没填」
        var water = CompoundCsv.Read("名称,熔点\n水,0\n").Items[0];
        Assert.Equal(0, water.Mp);
    }

    [Fact]
    public void 没填的项导出去是空格子不是零()
    {
        var csv = CompoundCsv.Write(new[] { new Compound { Name = "未知物", Cas = "X-1" } });
        var line = csv.Split("\r\n")[1];
        // 导出去再导回来，「没填」不能变成「密度为零」
        var back = CompoundCsv.Read(csv).Items[0];
        Assert.Null(back.Density);
        Assert.Null(back.Purity);
        Assert.DoesNotContain(",0,", line);
    }

    [Fact]
    public void 名字里带逗号却没加引号要喊出来()
    {
        // 「2,4-二硝基苯甲醚」被切成两格，从那一列起整行错位——
        // 而错位之后每个值看着都还像模像样（密度栏里坐着一个熔点）。
        // 读进去不报，比读不进去危险得多。
        //
        // 只在**格数比表头多**时认得出来。后面几列正好都空着的话，格数对得上，
        // 这种错 CSV 本身就无从分辨——那时只能靠人看一眼导入结果
        var r = CompoundCsv.Read("名称,CAS,摩尔质量\n2,4-二硝基苯甲醚,119-27-7,198.13\n");
        Assert.Contains(r.Problems, p => p.Contains("错位"));
        Assert.Contains(r.Problems, p => p.Contains("第 2 行"));
    }

    [Fact]
    public void 发出去的那份模板我们自己读得懂()
    {
        // 模板给人拿 Excel 填，列名后面挂着单位（「密度 (g/mL)」）。
        // 我们自己的导入认不出来的话，那份模板就是废纸——而且是悄悄的废纸：
        // 导进去不报错，只是那几列全空
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docs", "化合物导入模板.csv")))
            dir = dir.Parent;
        Assert.NotNull(dir);

        var head = File.ReadAllText(Path.Combine(dir!.FullName, "docs", "化合物导入模板.csv"));
        var r = CompoundCsv.Read(head + "苯甲酸,65-85-0,C7H6O2,122.12,1.266,99.5,122.4,249,1.2,有机酸,水,L-1,国药,备,0.17;0.006\r\n");

        Assert.Empty(r.IgnoredColumns);         // 一列都不该认不出来
        Assert.Empty(r.Problems);
        var c = Assert.Single(r.Items);
        Assert.Equal(1.266, c.Density);
        Assert.Equal(99.5, c.Purity);
        Assert.Equal(1.2, c.Cp);
        Assert.Equal("国药", c.Supplier);
    }

    [Fact]
    public void 加了引号就正常读()
    {
        var r = CompoundCsv.Read("名称,CAS,摩尔质量\n\"2,4-二硝基苯甲醚\",119-27-7,198.13\n");
        var c = Assert.Single(r.Items);
        Assert.Empty(r.Problems);
        Assert.Equal("2,4-二硝基苯甲醚", c.Name);
        Assert.Equal(198.13, c.Mw);
    }

    [Fact]
    public void 自带的常用溶剂酸碱表一行不少地读进来()
    {
        // docs/常用溶剂酸碱.csv 是发给用户导入的成品数据（约 60 条手册值），
        // 跟模板一样必须有回归：一个问题都不能报——报了问题意味着某行某格被
        // 悄悄留空，用户导完只会看到「61 条导入成功」，不会去数哪格丢了
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docs", "常用溶剂酸碱.csv")))
            dir = dir.Parent;
        Assert.NotNull(dir);

        var r = CompoundCsv.Read(File.ReadAllText(
            Path.Combine(dir!.FullName, "docs", "常用溶剂酸碱.csv")));

        Assert.Empty(r.Problems);
        Assert.Empty(r.IgnoredColumns);
        Assert.Equal(61, r.Items.Count);

        // 每一条都有真 CAS、有相态；液体必有密度（配料表把质量换体积就靠它）
        Assert.All(r.Items, c => Assert.True(c.HasCas, c.Name));
        Assert.All(r.Items, c => Assert.True(c.Phase is "固" or "液", c.Name));
        Assert.All(r.Items.Where(c => c.Phase == "液"), c => Assert.NotNull(c.Density));

        // 不跟程序自带的 10 条种子撞 CAS：撞了的话导入会把种子那条覆盖掉
        var seed = new[] { "65-85-0", "69-72-7", "77-92-9", "103-90-2", "15687-27-1",
                           "56-40-6", "56-86-0", "7783-20-2", "7447-40-7", "57-50-1" };
        Assert.Empty(r.Items.Where(c => seed.Contains(c.Cas)));

        // 抽查几个手册值进来没走样
        var ac2o = r.Items.Single(c => c.Name == "乙酸酐");
        Assert.Equal(1.082, ac2o.Density);
        Assert.Equal(139.8, ac2o.Bp);
        var naoh = r.Items.Single(c => c.Name == "氢氧化钠");
        Assert.Equal("固", naoh.Phase);
        Assert.Equal(318, naoh.Mp);
        var hcl = r.Items.Single(c => c.Cas == "7647-01-0");
        Assert.Equal(37, hcl.Purity);                       // 溶液条目按市售浓品填纯度
        // 名字里带 ASCII 逗号的行靠引号活着——引号丢了整行错位，上面 Problems 会先喊
        Assert.Contains(r.Items, c => c.Name == "DBU（1,8-二氮杂二环[5.4.0]十一碳-7-烯）");
        Assert.Contains(r.Items, c => c.Name == "N,N-二甲基甲酰胺");
    }
}

public class CompoundFieldTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tec-cmp-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }

    private LibraryDb Open() => new(Path.Combine(_dir, "lib.db"));

    [Fact]
    public void 新字段存进去读回来一个不少()
    {
        var c = new Compound
        {
            Cas = "108-88-3", Name = "甲苯", Formula = "C7H8", Mw = 92.14, Mp = -95,
            Density = 0.8669, Bp = 110.6, Purity = 99.8, Cp = 1.707,
            Batch = "L-2026-08", Supplier = "国药", Category = "溶剂"
        };
        using (var db = Open()) db.SaveCompound(c);

        using var db2 = Open();
        var back = Assert.Single(db2.LoadCompounds());
        Assert.Equal(0.8669, back.Density);
        Assert.Equal(110.6, back.Bp);
        Assert.Equal(99.8, back.Purity);
        Assert.Equal(1.707, back.Cp);
        Assert.Equal("L-2026-08", back.Batch);
        Assert.Equal("国药", back.Supplier);
    }

    [Fact]
    public void 没填的那几项读回来还是没填不是零()
    {
        using (var db = Open()) db.SaveCompound(new Compound { Cas = "X-1", Name = "内部代号 X-1" });
        using var db2 = Open();
        var back = Assert.Single(db2.LoadCompounds());

        Assert.Null(back.Density);
        Assert.Null(back.Bp);
        Assert.Null(back.Purity);
        Assert.Null(back.Cp);
    }

    [Fact]
    public void 改了CAS要换钥匙不是多一条()
    {
        // 库按 cas 列认人。只 upsert 不删老的，改一次就多一条改之前的孤儿——
        // 两条名字还一模一样，事后翻库看不出哪条是新的
        var c = new Compound { Cas = Compound.KeyOf("A-7 中间体"), Name = "A-7 中间体", Mw = 227.13 };
        using (var db = Open()) db.SaveCompound(c);

        var old = c.Cas;
        c.Cas = "119-27-7";
        using (var db = Open()) { db.DeleteCompound(old); db.SaveCompound(c); }

        using var db2 = Open();
        var back = Assert.Single(db2.LoadCompounds());
        Assert.Equal("119-27-7", back.Cas);
        Assert.True(back.HasCas);
        Assert.Equal(227.13, back.Mw);
    }

    [Fact]
    public void 老库升上来照常打开且老数据还在()
    {
        var path = Path.Combine(_dir, "old.db");
        Directory.CreateDirectory(_dir);

        // 手搭一个「版本 1」的库：只有老那几列
        using (var cn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
        {
            cn.Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = @"
create table meta(k text primary key, v text not null);
insert into meta(k,v) values('schema','1');
create table compound(cas text primary key, name text not null, formula text, mw real, mp real,
                      category text, solvent text, note text, solubility text, structure text,
                      ion text, ord integer not null default 0);
insert into compound(cas,name,mw,ord) values('65-85-0','苯甲酸',122.12,0);";
            cmd.ExecuteNonQuery();
        }

        using var db = new LibraryDb(path);
        var back = Assert.Single(db.LoadCompounds());
        Assert.Equal("苯甲酸", back.Name);
        Assert.Equal(122.12, back.Mw);
        Assert.Null(back.Density);          // 新列在，值是「没填」

        back.Density = 1.266;
        db.SaveCompound(back);
        Assert.Equal(1.266, db.LoadCompounds()[0].Density);
    }
}