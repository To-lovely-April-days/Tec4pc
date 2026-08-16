using System.Text;
using System.Text.Json;
using Tec.Core.Benches;
using Tec.Core.Recipes;

namespace Tec.Core.Persistence;

/// <summary>
/// 三种文件的读写。写盘一律先写临时文件再原子替换——
/// 存到一半断电，原来那份还在，不会两份都没了。
/// </summary>
public static class TecFiles
{
    public const string ExperimentExt = ".tec";
    public const string BenchExt = ".tecbench";
    public const string RecipeExt = ".tecrecipe";

    // ── 模型 → 文档 ──────────────────────────────────────────────────

    public static BenchDoc ToDoc(this Bench bench)
    {
        var doc = new BenchDoc { Schema = Bench.CurrentSchemaVersion, Name = bench.Name };
        foreach (var d in bench.Devices)
            doc.Devices.Add(new DeviceDoc
            {
                DriverId = d.DriverId,
                InstanceId = d.InstanceId,
                Label = d.Label,
                X = d.Position.X,
                Y = d.Position.Y,
                Simulated = d.Simulated,
                Connection = d.Connection.Clone(),
                Config = d.Config.Clone(),
                DockHostId = d.DockHostId,
                Dock = d.Dock,
                DockSlot = d.DockSlot,
                DockAnchor = d.DockAnchor,
                DockSideTag = d.DockSideTag
            });
        foreach (var b in bench.Bindings)
            doc.Bindings.Add(new BindingDoc
            { DeviceId = b.DeviceId, Channel = b.ChannelNumber, Mode = b.Mode, Port = b.Port });
        foreach (var s in bench.Stations)
            doc.Stations.Add(new StationDoc { Name = s.Name, Channels = s.Channels.ToList() });
        return doc;
    }

    public static RecipeDoc ToDoc(this Recipe r)
    {
        var doc = new RecipeDoc
        {
            Schema = Recipe.CurrentSchemaVersion,
            Id = r.Id,
            Name = r.Name,
            Author = r.Author,
            Notes = r.Notes,
            ModifiedAt = r.ModifiedAt
        };
        foreach (var s in r.Steps)
            doc.Steps.Add(new StepDoc
            {
                StepId = s.StepId,
                CommandId = s.CommandId,
                Parameters = s.Parameters.Clone(),
                Rows = s.Rows?.Select(x => x.Clone()).ToList(),
                Enabled = s.Enabled,
                PauseOnFault = s.PauseOnFault,
                Comment = s.Comment
            });
        return doc;
    }

    // ── 文档 → 模型 ──────────────────────────────────────────────────

    /// <summary>把台面文档灌回一个 Bench。原有内容先清空——是「打开」不是「合并」。</summary>
    public static void ApplyTo(this BenchDoc doc, Bench bench)
    {
        Guard(doc.Schema, Bench.CurrentSchemaVersion, "台面");

        bench.Name = doc.Name.Length > 0 ? doc.Name : bench.Name;
        bench.Devices.Clear();
        bench.Bindings.Clear();
        bench.Stations.Clear();

        foreach (var d in doc.Devices)
        {
            if (d.DriverId.Length == 0 || d.InstanceId.Length == 0)
                throw new TecFileException("台面里有一台设备缺少驱动号或实例号。");
            bench.Devices.Add(new DeviceInstance
            {
                DriverId = d.DriverId,
                InstanceId = d.InstanceId,
                Label = d.Label,
                Position = new Point(d.X, d.Y),
                Simulated = d.Simulated,
                Connection = d.Connection.Clone(),
                Config = d.Config.Clone(),
                DockHostId = d.DockHostId,
                Dock = d.Dock,
                DockSlot = d.DockSlot,
                DockAnchor = d.DockAnchor,
                DockSideTag = d.DockSideTag
            });
        }

        // 指向不存在设备的绑定直接丢掉：宁可少一条绑定，也不能让台面带着悬空引用跑起来
        var ids = bench.Devices.Select(x => x.InstanceId).ToHashSet(StringComparer.Ordinal);
        foreach (var b in doc.Bindings)
        {
            if (!ids.Contains(b.DeviceId)) continue;
            bench.Bindings.Add(new Binding(b.DeviceId, b.Channel, b.Mode) { Port = b.Port });
        }

        foreach (var s in doc.Stations)
        {
            var station = new Station { Name = s.Name };
            station.Channels.AddRange(s.Channels);
            bench.Stations.Add(station);
        }
    }

    public static Recipe ToModel(this RecipeDoc doc) => doc.ToModel(out _);

    /// <summary>
    /// 带迁移说明的装载。老指令 Id 会被翻译成现在这套，翻译了什么由 migrated 带出来——
    /// 悄悄改掉操作人存下来的东西不行，改了得说。
    /// </summary>
    public static Recipe ToModel(this RecipeDoc doc, out IReadOnlyList<string> migrated)
    {
        Guard(doc.Schema, Recipe.CurrentSchemaVersion, "配方");

        var r = new Recipe
        {
            Id = doc.Id.Length > 0 ? doc.Id : Guid.NewGuid().ToString("N")[..8],
            Name = doc.Name.Length > 0 ? doc.Name : "未命名配方",
            Author = doc.Author,
            Notes = doc.Notes,
            ModifiedAt = doc.ModifiedAt == default ? DateTimeOffset.Now : doc.ModifiedAt
        };
        foreach (var s in doc.Steps)
        {
            if (s.CommandId.Length == 0)
                throw new TecFileException($"配方「{r.Name}」里有一步没有指令号。");
            r.Steps.Add(new Step
            {
                StepId = s.StepId.Length > 0 ? s.StepId : Guid.NewGuid().ToString("N")[..8],
                CommandId = s.CommandId,
                Parameters = s.Parameters.Clone(),
                Rows = s.Rows?.Select(x => x.Clone()).ToList(),
                Enabled = s.Enabled,
                PauseOnFault = s.PauseOnFault,
                Comment = s.Comment
            });
        }
        migrated = RecipeMigration.Apply(r);
        return r;
    }

    /// <summary>
    /// 版本闸门。文件比程序新就直接拒绝——硬着头皮读下去，
    /// 读出来的台面看着像那么回事，实际少了新版本才有的东西，比打不开更坏。
    /// </summary>
    private static void Guard(int schema, int current, string what)
    {
        if (schema > current)
            throw new TecFileException(
                $"这份{what}文件是新版本程序存的（格式 v{schema}，本程序支持到 v{current}），请先升级 TecStudio。");
    }

    // ── 落盘 / 读盘 ──────────────────────────────────────────────────

    public static void Save(string path, ExperimentDoc doc)
    {
        doc.ModifiedAt = DateTimeOffset.Now;
        WriteAtomic(path, TecJson.Write(doc));
    }

    public static void SaveBench(string path, BenchDoc doc) => WriteAtomic(path, TecJson.Write(doc));

    public static void SaveRecipe(string path, RecipeDoc doc) => WriteAtomic(path, TecJson.Write(doc));

    public static ExperimentDoc LoadExperiment(string path)
    {
        var doc = Load<ExperimentDoc>(path, "实验");
        if (doc.Schema > ExperimentDoc.CurrentSchema)
            throw new TecFileException(
                $"这份实验文件是新版本程序存的（格式 v{doc.Schema}，本程序支持到 v{ExperimentDoc.CurrentSchema}），请先升级 TecStudio。");
        return doc;
    }

    public static BenchDoc LoadBench(string path) => Load<BenchDoc>(path, "台面");

    public static RecipeDoc LoadRecipe(string path) => Load<RecipeDoc>(path, "配方");

    private static T Load<T>(string path, string what)
    {
        if (!File.Exists(path)) throw new TecFileException($"找不到文件：{path}");
        string text;
        try { text = File.ReadAllText(path, Encoding.UTF8); }
        catch (Exception ex) { throw new TecFileException($"读不了这个文件：{ex.Message}", ex); }

        try { return TecJson.Read<T>(text); }
        catch (JsonException ex)
        {
            throw new TecFileException(
                $"这份{what}文件的内容不完整或格式不对（第 {ex.LineNumber + 1} 行）。", ex);
        }
    }

    /// <summary>
    /// 先写同目录下的临时文件，再整体替换。写一半断电时，磁盘上要么是旧的完整文件，
    /// 要么是新的完整文件，不会出现半份。
    /// </summary>
    private static void WriteAtomic(string path, string text)
    {
        var tmp = path + ".tmp";
        try
        {
            // 建目录也算写盘的一步，一起兜住——界面只认 TecFileException.Message，
            // 漏一个原始 IOException 出去，操作人看到的就是一句英文堆栈
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
        catch (Exception ex)
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw new TecFileException($"存不进去：{ex.Message}", ex);
        }
    }
}
