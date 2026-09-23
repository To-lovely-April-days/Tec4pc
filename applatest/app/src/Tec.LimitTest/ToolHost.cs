using Tec.Core.Persistence;
using Tec.Core.Records;
using Tec.Hmi.Runtime;
using Tec.LimitTest.Runs;

namespace Tec.LimitTest;

/// <summary>
/// 工具的宿主：一份数据目录、一份台面（bench.json，格式就是工作站的台面文件）、
/// 一个 HmiRuntime（真机驱动 + 管线 + 安全层）和套在它外面的 <see cref="RuntimeRig"/>。
///
/// 连接 = 新建一个 HmiRuntime 开机（Boot + StartAsync）；重连 = 把旧的整个扔掉再来一遍——
/// HmiRuntime 的台面是开机读一次的，不在运行中改，这里也不发明第二套。
/// </summary>
public sealed class ToolHost : IAsyncDisposable
{
    public const string Category = "极限测试";
    public const string Actor = "极限测试工具";

    public ToolHost(string? dataDir = null)
    {
        DataDir = dataDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData,
                                      Environment.SpecialFolderOption.Create), "TecLimitTest");
        Directory.CreateDirectory(DataDir);
        OutDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments,
                                      Environment.SpecialFolderOption.Create), "控温极限测试");
    }

    public string DataDir { get; }
    public string BenchPath => Path.Combine(DataDir, "bench.json");
    /// <summary>记录表与 csv 的缺省输出目录（我的文档\控温极限测试）。界面上可改。</summary>
    public string OutDir { get; set; }

    public HmiRuntime? Runtime { get; private set; }
    public RuntimeRig? Rig { get; private set; }
    /// <summary>连过没有（连过之后再「连接」就是重连）。</summary>
    public bool Started { get; private set; }

    /// <summary>
    /// 开机第一步：没有台面文件就先从设备 HMI 那份拷一份（现场那台机器上多半有），
    /// 再让 HmiRuntime 读台面、注册驱动（没有的话它自己写一份缺省的）。不开串口。
    /// </summary>
    public void Prepare()
    {
        if (!File.Exists(BenchPath))
        {
            var hmi = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TecHmi", "bench.json");
            if (File.Exists(hmi))
            {
                try { File.Copy(hmi, BenchPath); } catch { /* 拷不了就让 HmiRuntime 写缺省的 */ }
            }
        }
        // 先建好、开完机再发布出去：界面那只每秒的定时器会来读 Runtime.Log，
        // Boot 跑到一半（Log 还是 null）就被它撞上，定时器抛了异常就再也不走了——
        // 实时读数、日志从此不刷新，人看不出来（重连时 Prepare 是在线程池线程上跑的，撞得上）
        var rt = new HmiRuntime(DataDir);
        rt.Boot();
        Runtime = rt;
        Started = false;
        rt.Log.Write(Category, $"工具就绪 · 台面 {BenchPath} · 输出 {OutDir}", Actor);
    }

    /// <summary>把界面上改过的连接参数写回 bench.json。</summary>
    public void SaveBench()
    {
        if (Runtime is null) return;
        TecFiles.SaveBench(BenchPath, Runtime.Bench.ToDoc());
        Runtime.Log.Write(Category, "台面已保存", Actor);
    }

    /// <summary>换一份台面：工作站另存的 .tecbench / 设备 HMI 的 bench.json / 实验文件 .tec（取里面的台面）。</summary>
    public async Task ImportBenchAsync(string path)
    {
        var doc = path.EndsWith(TecFiles.ExperimentExt, StringComparison.OrdinalIgnoreCase)
            ? TecFiles.LoadExperiment(path).Bench
            : TecFiles.LoadBench(path);
        await DisconnectAsync().ConfigureAwait(false);
        TecFiles.SaveBench(BenchPath, doc);
        Prepare();
        Runtime!.Log.Write(Category, $"台面已从 {path} 导入：{doc.Devices.Count} 台设备", Actor);
    }

    /// <summary>开串口、开会话、建通道；连过一次再叫就是重连。</summary>
    public async Task ConnectAsync()
    {
        if (Runtime is null || Started)
        {
            await DisconnectAsync().ConfigureAwait(false);
            Prepare();
        }
        var rt = Runtime!;
        await rt.StartAsync().ConfigureAwait(false);
        Started = true;
        try
        {
            Rig = new RuntimeRig(rt, Log);
            rt.Log.Write(Category, $"机器就绪：{Rig.Describe}", Actor);
        }
        catch (InvalidOperationException ex)
        {
            Rig = null;
            rt.Log.Write(Category, ex.Message, Actor, LogLevel.Error);
        }
    }

    public async Task DisconnectAsync()
    {
        if (Rig is { } rig)
        {
            Rig = null;
            // 不在界面线程上干等它那条电流轮询收尾（串口一问一答最长两秒）
            try { await rig.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        if (Runtime is { } rt)
        {
            Runtime = null;
            try { await rt.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        Started = false;
    }

    /// <summary>Runner 的日志口：level = info / warn / error。</summary>
    public void Log(string level, string text)
        => Runtime?.Log?.Write(Category, text, Actor,
            level is "error" ? LogLevel.Error : level is "warn" ? LogLevel.Warn : LogLevel.Info);

    public IReadOnlyList<LogEntry> Tail(int max) => Runtime?.Log?.Tail(max) ?? Array.Empty<LogEntry>();

    public ValueTask DisposeAsync() => new(DisconnectAsync());
}
