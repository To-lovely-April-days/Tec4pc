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

        SimRun = new RelayCommand(() => { ws.TimeScale = ws.TimeScale <= 1 ? 60 : ws.TimeScale; Raise(nameof(SimNote)); });
        SimStop = new RelayCommand(() => { ws.Engine.AbortAll("操作员", "模拟模式停止"); Raise(nameof(SimNote)); });
        SimPause = new RelayCommand(() => { ws.Engine.PauseAll("操作员"); Raise(nameof(SimNote)); });
        SimStep = new RelayCommand(() => { foreach (var r in ws.Engine.Runners) r.SkipCurrent("操作员", "单步"); });
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
