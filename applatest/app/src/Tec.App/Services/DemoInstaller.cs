using Tec.Core;
using Tec.Core.Compounds;
using Tec.Core.Records;
using Tec.Core.Safety;
using Tec.Demo;
using Point = Avalonia.Point;

namespace Tec.App.Services;

/// <summary>
/// 【本地改动】工作站的演示模式装载（2026-11-23，用户要在没接真机的电脑上截各页截图）。
///
/// WHY：Workspace.Boot / StartAsync 只在 <see cref="DemoMode.Enabled"/> 时各调这里一句，演示的事全收在这个类里，
/// 不带开关启动时这个类一行都不执行。装载走程序自己已有的路：仿真驱动当驱动包装（DemoDrivers）、
/// 配方进配方库（Store.SaveLibrary）、化合物进 SQLite 库、历史归档写成 RunArchive 目录、
/// 演示实验写成 .tec 再「打开」——所以界面上看到的每一样东西都跟操作人自己做出来的是同一个状态，
/// 只是来源是仿真，而且标题栏上那枚「演示数据」签一直在。
/// </summary>
public static class DemoInstaller
{
    /// <summary>通道 2 比通道 1 晚多少（真实秒）启动：运行页一路已跑了一段、一路刚起步。</summary>
    public static TimeSpan SecondChannelDelay { get; set; } = TimeSpan.FromSeconds(90);

    private static readonly List<Compound> _compounds = new();
    private static string? _experimentPath;
    private static bool _runsStarted;

    /// <summary>Boot 最开头：把演示数据目录重置成空的。ExperimentStore.DataDir 已经指向它。</summary>
    public static void PrepareDataDir()
    {
        var dir = DemoMode.ResetDir(DemoMode.StudioDataDir);
        if (!string.Equals(dir, ExperimentStore.DataDir, StringComparison.Ordinal))
            Console.WriteLine($"[demo] 演示数据目录被占用，这次用 {dir}");
    }

    /// <summary>Boot：仿真驱动包进驱动目录。</summary>
    public static void LoadDrivers(Workspace ws)
    {
        var n = DemoDrivers.Load(ws.Drivers);
        ws.Log?.Write("演示", n == DemoDrivers.All.Length
            ? $"演示模式：仿真驱动包已装入（{n} 个驱动，{DemoDrivers.Dir}）"
            : $"演示模式：仿真驱动包不全——缺 {DemoDrivers.Missing(ws.Drivers)}（目录 {DemoDrivers.Dir}）", "演示", LogLevel.Warn);
    }

    /// <summary>
    /// Boot（Store / Users / Archive 都建好之后）：配方库、化合物库、账号、历史归档。
    /// 返回化合物清单给后面写配料表用。
    /// </summary>
    public static void SeedLibraries(Workspace ws)
    {
        // 化合物：走程序自己的导入器与数据库
        _compounds.Clear();
        _compounds.AddRange(DemoCompounds.Load());
        ws.Store.Db?.SaveCompounds(_compounds);
        ws.Store.LoadCompounds(ws.Compounds);

        // 配方库
        ws.Library.Clear();
        foreach (var r in DemoRecipes.Library()) ws.Library.Add(r);
        ws.Store.SaveLibrary();

        // 账号：管理员 / 主管 / 操作员，登录页预填操作员
        DemoUsers.Seed(ws.Users);

        // 历史归档：四炉现场控温测试曲线
        var ver = typeof(Workspace).Assembly.GetName().Version?.ToString() ?? "";
        var n = DemoArchive.Write(ws.Archive.Root, ws.Catalog, ver);
        ws.Log.Write("演示", $"演示模式：配方库 {ws.Library.Count} 条、化合物 {ws.Compounds.Count} 条、历史归档 {n} 炉、账号 {ws.Users.All.Count} 个", "演示");
    }

    /// <summary>
    /// StartAsync：写出演示实验并打开它（台面 + 两路配方 + 两张配料表），然后连设备。
    /// 插入件（Tr / pH / 搅拌头）的画布位置按主机位置用 BenchDock 现算——它们的位置依赖设备图的 viewBox。
    /// </summary>
    public static async Task OpenExperimentAsync(Workspace ws)
    {
        var bench = DemoBench.Build();
        var host = bench.Devices.First(d => d.InstanceId == DemoBench.Host);
        var hostPos = new Point(host.X, host.Y);
        var hostW = BenchDock.DisplayWidth("rd105");
        foreach (var d in bench.Devices)
        {
            if (d.DockAnchor is null || d.DockHostId != host.InstanceId) continue;
            var art = ws.Drivers.Driver(d.DriverId)?.Info.IconKey ?? "";
            if (BenchDock.InsertArtFor(art, d.DockAnchor) is not { } ins) continue;
            if (BenchDock.AnchorById(d.DockAnchor) is not { } a) continue;
            var p = BenchDock.SnapPosition(ins, a, hostPos, hostW);
            d.X = p.X; d.Y = p.Y;
        }

        _experimentPath = DemoPack.WriteExperiments(ExperimentStore.ExperimentsDir, bench, _compounds);
        await ws.Store.OpenAsync(_experimentPath).ConfigureAwait(false);
        await ws.Store.ConnectChannelsAsync().ConfigureAwait(false);
        ws.Log.Write("演示", $"演示模式：已打开 {Path.GetFileName(_experimentPath)}，设备按仿真驱动连接", "演示");
    }

    /// <summary>
    /// 登录之后把两路跑起来（只跑一次）：批次署登录人的名，不署「未登录」。
    /// 通道 1 立刻启动、通道 2 晚 90 s——运行页一路已跑了一段、一路刚起步。
    /// 另加一条操作人收紧的夹套上限（仅报警）：回流段夹套会越过它、结晶段回来，
    /// 报警带上就有一条「已恢复，待确认」——那是安全层真判出来的，不是摆的。
    /// </summary>
    public static void StartRunsAfterSignIn(Workspace ws)
    {
        if (_runsStarted || ws.CurrentUser is null) return;
        _runsStarted = true;
        _ = Task.Run(async () =>
        {
            try
            {
                // 等通道会话都挂上（设备同时连，仿真几乎立刻就绪；多等一拍让采样进管线）
                for (var i = 0; i < 50 && ws.Channels.Count < 2; i++) await Task.Delay(200).ConfigureAwait(false);
                await Task.Delay(1500).ConfigureAwait(false);

                ws.Engine.Safety.Add(new SafetyLimit(1, "Tj", null, 80, null, TimeSpan.FromSeconds(5), SafetyAction.Alarm)
                { Note = "操作人收紧：夹套 ≤ 80 ℃（回流段会越过，仅报警）", FromOperator = true });

                Start(ws, 1);
                await Task.Delay(SecondChannelDelay).ConfigureAwait(false);
                Start(ws, 2);
            }
            catch (Exception ex)
            {
                ws.Log.Write("演示", $"演示运行启动失败：{ex.Message}", "演示", LogLevel.Error);
            }
        });
    }

    private static void Start(Workspace ws, int channel)
    {
        if (!ws.ChannelRecipes.TryGetValue(channel, out var recipe) || recipe.Steps.Count == 0) return;
        if (ws.Engine.Runner(channel) is not { CanStart: true }) return;
        ws.BeginBatch();
        var charge = ws.ChannelCharges.TryGetValue(channel, out var t) && !t.IsEmpty ? t : null;
        ws.Engine.StartChannel(channel, recipe, ws.Operator, charge: charge);
        ws.Log.Write("演示", $"演示模式：CH{channel}「{recipe.Name}」启动", ws.Operator);
    }
}
