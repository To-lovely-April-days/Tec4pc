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
        Recipe = new RecipeViewModel(ws) { GoLibrary = () => Tab = TabLib };
        // 配方页的导入 / 导出结果说到开始页那条状态行上，与打开 / 保存实验同一个位置
        Recipe.Say = text => Start.Status = text;
        Library = new RecipeLibViewModel(ws, this);
        Compounds = new CompoundsViewModel(ws);
        Run = new RunViewModel(ws);
        Export = new ExportViewModel(ws);

        ws.Store.Changed += (_, _) => Raise(nameof(DocTitle));

        Go = new RelayCommand(p =>
        {
            if (p is null) return;
            if (int.TryParse(Convert.ToString(p, CultureInfo.InvariantCulture), out var i)) Tab = i;
        });

        // 菜单栏右边那排圆钮 = 原型 simbar，对**全部已启用通道**生效：
        // 运行（启动全部未运行的，用各自配方）/ 停止 / 暂停 / 单步（跳过当前步）。
        // 只想动一路的，在运行页的通道磁贴上按那一路自己的四个钮
        SimRun = new RelayCommand(() =>
        {
            // 第一次启动时开一个批次，名字取当前实验名——记录里的批次名不能是空的，
            // 导出页整条记录都靠它认人
            if (ws.Engine.Record.Channels.Count == 0)
                ws.Engine.NewBatch(ws.ExperimentName, ws.Operator, ws.Bench.Name);

            foreach (var ch in ws.Channels.Where(c => c.Enabled))
            {
                var runner = ws.Engine.Runner(ch.Number);
                if (runner?.State is Tec.Core.Records.ChannelRunState.Paused)
                {
                    runner.Resume(ws.Operator);
                    continue;
                }
                if (runner?.State is Tec.Core.Records.ChannelRunState.Running) continue;
                if (!ws.ChannelRecipes.TryGetValue(ch.Number, out var recipe) || recipe.Steps.Count == 0) continue;
                try { ws.Engine.StartChannel(ch.Number, recipe, ws.Operator); } catch { }
            }
            Tab = TabRun;
        });
        SimStop = new RelayCommand(() => ws.Engine.AbortAll(ws.Operator, "整机停止"));
        SimPause = new RelayCommand(() => ws.Engine.PauseAll(ws.Operator));
        SimStep = new RelayCommand(() =>
        {
            foreach (var r in ws.Engine.Runners) r.SkipCurrent(ws.Operator, "单步");
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

    /// <summary>标题栏上的实验名。改过还没存的带一个星号。</summary>
    public string DocTitle => Workspace.Store.Title;

    public string SimNote => Workspace.TimeScale > 1
        ? $"仿真 {Workspace.TimeScale:F0}×"
        : "实测";
}
