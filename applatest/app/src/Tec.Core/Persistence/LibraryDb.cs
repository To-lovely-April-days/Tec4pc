using System.Globalization;
using Microsoft.Data.Sqlite;
using Tec.Core.Compounds;

namespace Tec.Core.Persistence;

/// <summary>
/// 全局库：配方库 + 化合物库，一个 SQLite 文件。
///
/// **这两样跟机器走，不跟实验走。** 配方是工艺模板，化合物是物性参考数据，
/// 换一份实验它们照样是那一套；从前配方库跟着 .tec 文件走了一份副本，
/// 结果同一条配方在三份实验里各存一遍，改了一处另外两处还是老的。
/// 现在实验文件里不再带它们（老文件里带的，打开时并进来，见 ExperimentStore）。
///
/// 为什么是 SQLite 而不是接着用 JSON：
///   · 存到一半断电，JSON 整个文件废掉；SQLite 有事务，要么全成要么全不成
///   · 库会越来越大（配方几百条不稀奇），JSON 每改一条都要整份重写
///   · 两个窗口同时开着时，JSON 是后写的覆盖先写的，SQLite 有锁
///
/// 表里既存整份 JSON（doc 列）又拆出几个列，是故意的：doc 列保证读回来
/// 一个字段不丢（配方的结构还会长），拆出来的列让列表页不必把每条都解一遍 JSON。
/// </summary>
public sealed class LibraryDb : IDisposable
{
    /// <summary>库结构版本。改表结构时 +1，并在 <see cref="Migrate"/> 里写升级步骤。</summary>
    public const int CurrentSchema = 2;

    private readonly SqliteConnection _cn;
    private readonly object _gate = new();

    /// <summary>库文件路径。内存库是 ":memory:"。</summary>
    public string Path { get; }

    public LibraryDb(string path)
    {
        Path = path;
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _cn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // 连接池会把连接留着不放，测试里删库文件会被占住；这里全程只用一条连接，
            // 用不上池子
            Pooling = false
        }.ToString());
        _cn.Open();
        Exec("pragma journal_mode=wal; pragma foreign_keys=on;");
        Migrate();
    }

    // ── 建表与升级 ──────────────────────────────────────────────────

    private void Migrate()
    {
        Exec(@"
create table if not exists meta(k text primary key, v text not null);

create table if not exists recipe(
  id          text primary key,
  name        text not null,
  author      text,
  modified_at text,
  steps       integer not null default 0,
  ord         integer not null default 0,
  doc         text not null);

create table if not exists compound(
  cas        text primary key,
  name       text not null,
  formula    text,
  mw         real,
  mp         real,
  category   text,
  solvent    text,
  note       text,
  solubility text,
  structure  text,
  ion        text,
  ord        integer not null default 0);");

        // 加列的升级**不看版本号，每次开库都跑一遍**：AddColumns 自己会先问表里有什么，
        // 已经有的就不动。这样「新建的库」和「老库升上来」走的是同一条路——
        // 按版本号分支的写法吃过亏：新库的版本号一上来就是最新的，于是跳过了升级，
        // 而建表语句还停在老的列清单上，新列一个都没有，一写就是「no column named …」
        AddColumns("compound",
            ("density", "real"), ("bp", "real"), ("purity", "real"), ("cp", "real"),
            ("batch", "text"), ("supplier", "text"), ("phase", "text"));

        SetMeta("schema", CurrentSchema.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// 缺哪列加哪列。SQLite 没有「add column if not exists」，所以先问表里现在有什么——
    /// 硬加会在已经升过级的库上抛「duplicate column」，把程序挡在开机那一步。
    /// </summary>
    private void AddColumns(string table, params (string Name, string Type)[] columns)
    {
        var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = _cn.CreateCommand())
        {
            cmd.CommandText = $"pragma table_info({table})";
            using var r = cmd.ExecuteReader();
            while (r.Read()) have.Add(r.GetString(1));
        }
        foreach (var (name, type) in columns)
            if (!have.Contains(name)) Exec($"alter table {table} add column {name} {type}");
    }

    // ── 元信息 ──────────────────────────────────────────────────────

    /// <summary>读一条元信息。没有就是 null。</summary>
    public string? Meta(string key)
    {
        lock (_gate)
        {
            using var cmd = _cn.CreateCommand();
            cmd.CommandText = "select v from meta where k=$k";
            cmd.Parameters.AddWithValue("$k", key);
            return cmd.ExecuteScalar() as string;
        }
    }

    public void SetMeta(string key, string value)
    {
        lock (_gate)
        {
            using var cmd = _cn.CreateCommand();
            cmd.CommandText = "insert into meta(k,v) values($k,$v) on conflict(k) do update set v=$v";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", value);
            cmd.ExecuteNonQuery();
        }
    }

    // ── 配方库 ──────────────────────────────────────────────────────

    public int RecipeCount => Count("recipe");

    /// <summary>按存进去的顺序读回整个配方库。读坏一条只丢那一条，其余照常返回。</summary>
    public List<RecipeDoc> LoadRecipes()
    {
        var list = new List<RecipeDoc>();
        lock (_gate)
        {
            using var cmd = _cn.CreateCommand();
            cmd.CommandText = "select id, doc from recipe order by ord, rowid";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                try { list.Add(TecJson.Read<RecipeDoc>(r.GetString(1))); }
                catch (Exception ex)
                {
                    // 一条读不出来不能把整个库废掉——剩下的配方还是好的
                    Console.WriteLine($"[warn] 配方库里 {r.GetString(0)} 这条读不出来，跳过：{ex.Message}");
                }
            }
        }
        return list;
    }

    /// <summary>
    /// 整库对齐：有的更新、没的插入、库里多出来的删掉，一个事务里做完。
    /// 界面上的配方库就是这一份清单，删掉的那条必须真的从库里消失。
    /// </summary>
    public void SaveRecipes(IReadOnlyList<RecipeDoc> docs)
    {
        lock (_gate)
        {
            using var tx = _cn.BeginTransaction();

            using (var up = _cn.CreateCommand())
            {
                up.Transaction = tx;
                up.CommandText = @"
insert into recipe(id,name,author,modified_at,steps,ord,doc)
values($id,$name,$author,$mod,$steps,$ord,$doc)
on conflict(id) do update set
  name=$name, author=$author, modified_at=$mod, steps=$steps, ord=$ord, doc=$doc";
                var pId = up.Parameters.Add("$id", SqliteType.Text);
                var pName = up.Parameters.Add("$name", SqliteType.Text);
                var pAuthor = up.Parameters.Add("$author", SqliteType.Text);
                var pMod = up.Parameters.Add("$mod", SqliteType.Text);
                var pSteps = up.Parameters.Add("$steps", SqliteType.Integer);
                var pOrd = up.Parameters.Add("$ord", SqliteType.Integer);
                var pDoc = up.Parameters.Add("$doc", SqliteType.Text);

                for (var i = 0; i < docs.Count; i++)
                {
                    var d = docs[i];
                    pId.Value = d.Id;
                    pName.Value = d.Name;
                    pAuthor.Value = (object?)d.Author ?? DBNull.Value;
                    pMod.Value = d.ModifiedAt.ToString("O", CultureInfo.InvariantCulture);
                    pSteps.Value = d.Steps.Count;
                    pOrd.Value = i;
                    pDoc.Value = TecJson.Write(d);
                    up.ExecuteNonQuery();
                }
            }

            DeleteMissing(tx, "recipe", "id", docs.Select(d => d.Id));
            tx.Commit();
        }
    }

    // ── 化合物库 ────────────────────────────────────────────────────

    public int CompoundCount => Count("compound");

    /// <summary>
    /// 化合物库版本号：每一次写入（改一条、删一条、整批对齐各算一次）+1。
    /// 配料行做物性快照时把它盖在行上（CH-D1），复核的人能指认「按哪一版库算的」。
    /// 从没写过就是 0。
    /// </summary>
    public int CompoundVersion
        => int.TryParse(Meta("compound_version"), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var v) ? v : 0;

    /// <summary>版本 +1。必须跟那次写入同一个事务，写成了版本才算数。</summary>
    private void BumpCompoundVersion(SqliteTransaction? tx)
    {
        using var cmd = _cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"insert into meta(k,v) values('compound_version','1')
                            on conflict(k) do update set v=cast(cast(v as integer)+1 as text)";
        cmd.ExecuteNonQuery();
    }

    public List<Compound> LoadCompounds()
    {
        var list = new List<Compound>();
        lock (_gate)
        {
            using var cmd = _cn.CreateCommand();
            cmd.CommandText = @"select cas,name,formula,mw,mp,category,solvent,note,
                                       solubility,structure,ion,
                                       density,bp,purity,cp,batch,supplier,phase
                                from compound order by ord, rowid";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new Compound
                {
                    Cas = r.GetString(0),
                    Name = r.GetString(1),
                    Formula = Str(r, 2) ?? "",
                    Mw = Num(r, 3),
                    Mp = Num(r, 4),
                    Category = Str(r, 5) ?? "",
                    Solvent = Str(r, 6) ?? "",
                    Note = Str(r, 7) ?? "",
                    Solubility = ParseCoefficients(Str(r, 8)),
                    StructureKey = Str(r, 9),
                    IonText = Str(r, 10),
                    Density = Num(r, 11),
                    Bp = Num(r, 12),
                    Purity = Num(r, 13),
                    Cp = Num(r, 14),
                    Batch = Str(r, 15) ?? "",
                    Supplier = Str(r, 16) ?? "",
                    Phase = Str(r, 17) ?? ""
                });
        }
        return list;
    }

    /// <summary>整库对齐，同 <see cref="SaveRecipes"/>。</summary>
    public void SaveCompounds(IReadOnlyList<Compound> list)
    {
        lock (_gate)
        {
            using var tx = _cn.BeginTransaction();
            for (var i = 0; i < list.Count; i++) Upsert(tx, list[i], i);
            DeleteMissing(tx, "compound", "cas", list.Select(c => c.Cas));
            BumpCompoundVersion(tx);
            tx.Commit();
        }
    }

    /// <summary>
    /// 只写一条。物性详情面板是逐个字段改的，改一个字就把整张表重写一遍没必要。
    /// 已有的按 CAS 号更新，没有的插在最后。
    /// </summary>
    public void SaveCompound(Compound c)
    {
        lock (_gate)
        {
            using var cmd = _cn.CreateCommand();
            cmd.CommandText = "select ord from compound where cas=$cas";
            cmd.Parameters.AddWithValue("$cas", c.Cas);
            var ord = cmd.ExecuteScalar() is long l ? (int)l : NextOrd();
            using var tx = _cn.BeginTransaction();
            Upsert(tx, c, ord);
            BumpCompoundVersion(tx);
            tx.Commit();
        }
    }

    public void DeleteCompound(string cas)
    {
        lock (_gate)
        {
            using var tx = _cn.BeginTransaction();
            using var cmd = _cn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "delete from compound where cas=$cas";
            cmd.Parameters.AddWithValue("$cas", cas);
            // 没删着东西就不涨版本：版本号数的是「库真的变了几次」
            if (cmd.ExecuteNonQuery() > 0) BumpCompoundVersion(tx);
            tx.Commit();
        }
    }

    private int NextOrd()
    {
        using var cmd = _cn.CreateCommand();
        cmd.CommandText = "select coalesce(max(ord),-1)+1 from compound";
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private void Upsert(SqliteTransaction? tx, Compound c, int ord)
    {
        using var cmd = _cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
insert into compound(cas,name,formula,mw,mp,category,solvent,note,solubility,structure,ion,ord,
                     density,bp,purity,cp,batch,supplier,phase)
values($cas,$name,$fx,$mw,$mp,$cat,$sol,$note,$coef,$st,$ion,$ord,
       $den,$bp,$pur,$cp,$batch,$sup,$phase)
on conflict(cas) do update set
  name=$name, formula=$fx, mw=$mw, mp=$mp, category=$cat, solvent=$sol,
  note=$note, solubility=$coef, structure=$st, ion=$ion, ord=$ord,
  density=$den, bp=$bp, purity=$pur, cp=$cp, batch=$batch, supplier=$sup, phase=$phase";
        cmd.Parameters.AddWithValue("$cas", c.Cas);
        cmd.Parameters.AddWithValue("$name", c.Name);
        cmd.Parameters.AddWithValue("$fx", c.Formula);
        cmd.Parameters.AddWithValue("$mw", (object?)c.Mw ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$mp", (object?)c.Mp ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cat", c.Category);
        cmd.Parameters.AddWithValue("$sol", c.Solvent);
        cmd.Parameters.AddWithValue("$note", c.Note);
        cmd.Parameters.AddWithValue("$coef", string.Join(',',
            c.Solubility.Select(v => v.ToString("R", CultureInfo.InvariantCulture))));
        cmd.Parameters.AddWithValue("$st", (object?)c.StructureKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ion", (object?)c.IonText ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ord", ord);
        cmd.Parameters.AddWithValue("$den", (object?)c.Density ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$bp", (object?)c.Bp ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pur", (object?)c.Purity ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cp", (object?)c.Cp ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$batch", c.Batch);
        cmd.Parameters.AddWithValue("$sup", c.Supplier);
        cmd.Parameters.AddWithValue("$phase", c.Phase);
        cmd.ExecuteNonQuery();
    }

    /// <summary>可空的数。null 就是「没填」，不是 0——见 Compound 里那段说明。</summary>
    private static double? Num(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);

    private static double[] ParseCoefficients(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<double>();
        var parts = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var list = new List<double>(parts.Length);
        foreach (var p in parts)
            if (double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) list.Add(v);
        return list.ToArray();
    }

    // ── 小工具 ──────────────────────────────────────────────────────

    private static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    /// <summary>把清单里没有的那些行删掉。键都很短，直接拼进 in (...)，不必建临时表。</summary>
    private void DeleteMissing(SqliteTransaction tx, string table, string keyColumn, IEnumerable<string> keep)
    {
        var keys = keep.ToList();
        using var cmd = _cn.CreateCommand();
        cmd.Transaction = tx;
        if (keys.Count == 0)
        {
            cmd.CommandText = $"delete from {table}";
        }
        else
        {
            var names = new List<string>(keys.Count);
            for (var i = 0; i < keys.Count; i++)
            {
                names.Add($"$k{i}");
                cmd.Parameters.AddWithValue($"$k{i}", keys[i]);
            }
            cmd.CommandText = $"delete from {table} where {keyColumn} not in ({string.Join(',', names)})";
        }
        cmd.ExecuteNonQuery();
    }

    private int Count(string table)
    {
        lock (_gate)
        {
            using var cmd = _cn.CreateCommand();
            cmd.CommandText = $"select count(*) from {table}";
            return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }

    private void Exec(string sql)
    {
        lock (_gate)
        {
            using var cmd = _cn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        try { _cn.Close(); } catch { }
        _cn.Dispose();
    }
}
