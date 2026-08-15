using System.Globalization;
using Tec.App.Services;

namespace Tec.App.ViewModels;

/// <summary>
/// 外壳。菜单栏七项与原型 .menu-item 一一对应：
/// 开始 · 台面 · 配方 · 配方库 · 化合物数据库 · 运行 · 数据导出。
/// </summary>
public sealed class MainViewModel : ViewModelBase
{
    public const int TabStart = 0, TabBench = 1, TabRecipe = 2, TabLib = 3,
                     TabCompounds = 4, TabRun = 5, TabExport = 6;

    private int _tab = TabStart;

    public MainViewModel(Workspace ws)
    {
        Workspace = ws;
        Start = new StartViewModel(ws, this);
        Bench = new BenchViewModel(ws);
        Recipe = new RecipeViewModel(ws);
        Library = new RecipeLibViewModel(ws, this);
        Compounds = new CompoundsViewModel();
        Run = new RunViewModel(ws);
        Export = new ExportViewModel(ws);

        Go = new RelayCommand(p =>
        {
            if (p is null) return;
            if (int.TryParse(Convert.ToString(p, CultureInfo.InvariantCulture), out var i)) Tab = i;
        });

        // 模拟模式四钮 = 原型 simbar：运行（启动全部已启用未运行的通道，用各自配方）/
        // 停止 / 暂停⇄继续 / 单步（跳过当前步）
        SimRun = new RelayCommand(() =>
        {
            // 第一次启动时开一个批次，名字取当前实验名——记录里的批次名不能是空的，
            // 导出页整条记录都靠它认人
            if (ws.Engine.Record.Channels.Count == 0)
                ws.Engine.NewBatch(ws.ExperimentName, "管理员", ws.Bench.Name);

            foreach (var ch in ws.Channels.Where(c => c.Enabled))
            {
                var runner = ws.Engine.Runner(ch.Number);
                if (runner?.State is Tec.Core.Records.ChannelRunState.Paused)
                {
                    runner.Resume("管理员");
                    continue;
                }
                if (runner?.State is Tec.Core.Records.ChannelRunState.Running) continue;
                if (!ws.ChannelRecipes.TryGetValue(ch.Number, out var recipe) || recipe.Steps.Count == 0) continue;
                try { ws.Engine.StartChannel(ch.Number, recipe, "管理员"); } catch { }
            }
            Tab = TabRun;
        });
        SimStop = new RelayCommand(() => ws.Engine.AbortAll("管理员", "模拟模式停止"));
        SimPause = new RelayCommand(() => ws.Engine.PauseAll("管理员"));
        SimStep = new RelayCommand(() =>
        {
            foreach (var r in ws.Engine.Runners) r.SkipCurrent("管理员", "单步");
        });
    }

    public Workspace Workspace { get; }
    public StartViewModel Start { get; }
    public BenchViewModel Bench { get; }
    public RecipeViewModel Recipe { get; }
    public RecipeLibViewModel Library { get; }
    public CompoundsViewModel Compounds { get; }
    public RunViewModel Run { get; }
    public ExportViewModel Export { get; }

    public RelayCommand Go { get; }
    public RelayCommand SimRun { get; }
    public RelayCommand SimStop { get; }
    public RelayCommand SimPause { get; }
    public RelayCommand SimStep { get; }

    public int Tab
    {
        get => _tab;
        set
        {
            if (!Set(ref _tab, value)) return;
            if (value == TabExport) Export.Reload();
            RaiseAll(nameof(IsStart), nameof(IsBench), nameof(IsRecipe), nameof(IsLib),
                     nameof(IsCompounds), nameof(IsRun), nameof(IsExport));
        }
    }

    public bool IsStart => _tab == TabStart;
    public bool IsBench => _tab == TabBench;
    public bool IsRecipe => _tab == TabRecipe;
    public bool IsLib => _tab == TabLib;
    public bool IsCompounds => _tab == TabCompounds;
    public bool IsRun => _tab == TabRun;
    public bool IsExport => _tab == TabExport;

    public string SimNote => Workspace.TimeScale > 1
        ? $"仿真 {Workspace.TimeScale:F0}×"
        : "实测";
}
