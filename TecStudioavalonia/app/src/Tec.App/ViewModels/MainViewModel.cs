using Tec.App.Services;

namespace Tec.App.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    private int _tab;

    public MainViewModel(Workspace ws)
    {
        Workspace = ws;
        Bench = new BenchViewModel(ws);
        Recipe = new RecipeViewModel(ws);
        Run = new RunViewModel(ws);
        Export = new ExportViewModel(ws);

        GoBench = new RelayCommand(() => Tab = 0);
        GoRecipe = new RelayCommand(() => Tab = 1);
        GoRun = new RelayCommand(() => Tab = 2);
        GoExport = new RelayCommand(() => { Export.Reload(); Tab = 3; });
    }

    public Workspace Workspace { get; }
    public BenchViewModel Bench { get; }
    public RecipeViewModel Recipe { get; }
    public RunViewModel Run { get; }
    public ExportViewModel Export { get; }

    public RelayCommand GoBench { get; }
    public RelayCommand GoRecipe { get; }
    public RelayCommand GoRun { get; }
    public RelayCommand GoExport { get; }

    public int Tab
    {
        get => _tab;
        set
        {
            if (!Set(ref _tab, value)) return;
            RaiseAll(nameof(IsBench), nameof(IsRecipe), nameof(IsRun), nameof(IsExport), nameof(Title));
        }
    }

    public bool IsBench => _tab == 0;
    public bool IsRecipe => _tab == 1;
    public bool IsRun => _tab == 2;
    public bool IsExport => _tab == 3;

    public string Title => _tab switch
    {
        0 => "台面",
        1 => "配方",
        2 => "运行",
        _ => "记录与导出"
    };

    public string Subtitle => "TecStudio · 四通道平行合成反应工作站";

    public string ClockText => $"仿真 {Workspace.TimeScale:F0}×";
}
