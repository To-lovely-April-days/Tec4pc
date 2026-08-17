using System.Globalization;
using Tec.App.Services;

namespace Tec.App.ViewModels;

/// <summary>
/// 外壳。菜单栏前七项与原型 .menu-item 一一对应：
/// 开始 · 台面 · 配方 · 配方库 · 化合物数据库 · 运行 · 数据导出。
///
/// **「配料表」是原型里没有的第八项**，插在化合物数据库后面——
/// 它消费化合物库里的物性，产出加料步骤的体积，位置就在这两者之间。
/// </summary>
public sealed class MainViewModel : ViewModelBase
{
    public const int TabStart = 0, TabBench = 1, TabRecipe = 2, TabLib = 3,
                     TabCompounds = 4, TabCharge = 5, TabRun = 6, TabExport = 7;

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
        Charge = new ChargeViewModel(ws)
        {
            // 「应用到加料步骤」改的是配方参数，得能在配方页撤回去，改完那一页也要跟着刷新
            EditRecipe = (ch, why, edit) => Recipe.EditExternally(ch, why, edit)
        };
        Run = new RunViewModel(ws);
        Export = new ExportViewModel(ws);

        ws.Store.Changed += (_, _) => Raise(nameof(DocTitle));

        Go = new RelayCommand(p =>
        {
            if (p is null) return;
            if (int.TryParse(Convert.ToString(p, CultureInfo.InvariantCulture), out var i)) Tab = i;
        });

        // 菜单栏右边那排圆钮 = 原型 simbar，对**全部已启用通道**生效：
        // 运行（启动全部未运行的，用各自配方）/ 停止（急停）/ 暂停。
        // 只想动一路的，在运行页的通道磁贴上按那一路自己的四个钮。
        //
        // 原型第四颗「单步」去掉了：跳过当前步是看着这一路当前那一步临时决定的，
        // 四路情况各不相同，一次全跳掉几乎不可能是人想要的结果，
        // 而且跳掉的每一步都写进记录撤不回来。那一颗留在磁贴上就够了
        SimRun = new RelayCommand(() =>
        {
            // 该开新批次就开：全都停下来之后再按启动，那是下一炉（§7.1）。
            // 名字取当前实验名——记录里的批次名不能是空的，导出页整条记录都靠它认人
            ws.BeginBatch();

            foreach (var ch in ws.Channels.Where(c => c.Enabled))
            {
                var runner = ws.Engine.Runner(ch.Number);
                if (runner is { CanResume: true })
                {
                    runner.Resume(ws.Operator);
                    continue;
                }
                // 正在跑、正在收尾、正在准备的一律跳过——判据由执行器给，
                // 界面不另立一套（各写一套迟早「按钮亮着，按下去抛异常」）
                if (runner is not null && !runner.CanStart) continue;
                if (!ws.ChannelRecipes.TryGetValue(ch.Number, out var recipe) || recipe.Steps.Count == 0) continue;
                try { ws.Engine.StartChannel(ch.Number, recipe, ws.Operator, charge: ws.ChargeOf(ch.Number)); } catch { }
            }
            Tab = TabRun;
            RefreshSim();
        });
        SimStop = new RelayCommand(() => { ws.Engine.AbortAll(ws.Operator, "整机停止"); RefreshSim(); });
        SimPause = new RelayCommand(() => { ws.Engine.PauseAll(ws.Operator); RefreshSim(); });

        // 这三颗从前恒是亮的：台面空着按「运行」什么也不发生，全都停着按「停止」
        // 也一样——按下去没反应比按钮是灰的更难受。通道状态和配方步数一直在变，
        // 没有一个总的变更事件盖得住（配方页加一步不会惊动引擎），只能按节拍重算；
        // 搭运行页那趟 700ms 的车，不另起一个表
        Run.Ticked += (_, _) => RefreshSim();
    }

    /// <summary>
    /// 重算这三颗的可用性。
    ///
    /// **按完自己的那一下必须立刻重算，不能等下一跳。** 按「运行」之后通道在同一个
    /// 调用栈里就变成 Running，可急停那颗要等最多 700ms 才亮起来——两颗只隔 9px，
    /// 「按错了马上补一下停止」正好落在这个窗口里，而 Avalonia 的禁用按钮
    /// 连指针事件都不路由：戳下去毫无反应，也没有任何提示。
    /// </summary>
    private void RefreshSim() => RaiseAll(nameof(CanSimRun), nameof(CanSimStop),
                                          nameof(CanSimPause), nameof(SimRunTip));

    /// <summary>正在跑或暂停着的通道数。停止 / 暂停要看它。</summary>
    private int LiveCount => Workspace.Engine.Runners.Count(
        r => r.State is Tec.Core.Records.ChannelRunState.Running
                     or Tec.Core.Records.ChannelRunState.Paused);

    /// <summary>
    /// 有没有哪一路是这一按就会动起来的：已启用、编排过步骤，而且执行器说能开
    /// （或者暂停着，按下去是继续）。判据用 `ChannelRunner.CanStart` 那一份，
    /// 与磁贴上那颗、与 Start() 的拦截是同一个。
    /// </summary>
    public bool CanSimRun => Workspace.Channels.Any(c =>
    {
        if (!c.Enabled) return false;
        var runner = Workspace.Engine.Runner(c.Number);

        // **暂停中的那一支不看配方步数。** 它跑的是按下启动那一刻冻下来的快照，
        // 泳道后来被清空也不影响它继续跑完（配方页按一下「新建」就会清空）。
        // 反过来写（先卡步数）会把「暂停中 + 泳道已清空」锁死：运行灰（0 步）、
        // 暂停也灰（它不是 Running），整条通道从菜单栏救不回来——
        // 而命令本身是认的，它先判 CanResume 就 Resume 了，压根不看配方
        if (runner is { CanResume: true }) return true;

        if (!Workspace.ChannelRecipes.TryGetValue(c.Number, out var r) || r.Steps.Count == 0) return false;
        return runner is null || runner.CanStart;
    });

    public bool CanSimStop => LiveCount > 0;

    public bool CanSimPause => Workspace.Engine.Runners.Any(
        r => r.State is Tec.Core.Records.ChannelRunState.Running);

    /// <summary>
    /// 按不了的时候说清为什么，别让人对着一颗灰钮猜。
    ///
    /// 「有的在跑、有的没编排」是最容易说错的一种：只要有一路编排过就笼统地说
    /// 「已启用的通道都在跑了」，跟顶栏那句「2 / 4 通道运行中」当场打架，
    /// 还把唯一能解锁按钮的动作（给剩下那几路加步骤）盖住了。所以两类分别数。
    /// </summary>
    public string SimRunTip
    {
        get
        {
            if (Workspace.Channels.Count == 0) return "台面上还没有通道 —— 去「台面」把反应器拖进来";
            var enabled = Workspace.Channels.Where(c => c.Enabled).ToList();
            if (enabled.Count == 0) return "台面上的通道都停用了";
            if (Workspace.Engine.Runners.Any(r => r.CanResume))
                return "继续暂停中的通道，并启动其余已编排好的通道";
            if (CanSimRun) return "启动全部已启用通道（各跑各的配方）";

            var blank = enabled.Count(c => !Workspace.ChannelRecipes.TryGetValue(c.Number, out var r)
                                        || r.Steps.Count == 0);
            var busy = enabled.Count - blank;
            if (busy > 0 && blank > 0)
                return $"能跑的 {busy} 路都在跑（或正在收尾）；另外 {blank} 路还没编排步骤 —— 去「配方」加几步";
            if (busy > 0) return "已启用的通道都在跑（或正在收尾）";
            return "还没有哪一路编排了步骤 —— 去「配方」加几步";
        }
    }

    public Workspace Workspace { get; }
    public StartViewModel Start { get; }
    public BenchViewModel Bench { get; }
    public RecipeViewModel Recipe { get; }
    public RecipeLibViewModel Library { get; }
    public CompoundsViewModel Compounds { get; }
    public ChargeViewModel Charge { get; }
    public RunViewModel Run { get; }
    public ExportViewModel Export { get; }

    public RelayCommand Go { get; }
    public RelayCommand SimRun { get; }
    public RelayCommand SimStop { get; }
    public RelayCommand SimPause { get; }

    public int Tab
    {
        get => _tab;
        set
        {
            if (!Set(ref _tab, value)) return;
            if (value == TabExport) Export.Reload();
            // 配料表算的是「库里的物性 × 配方里的加料」，两边都可能在别的页上改过，
            // 切过来的时候重算一遍，别让人看着一张过时的表
            if (value == TabCharge) Charge.Reload();
            // 配方页那条校验条现在也看配料表。配料表在另一页上改，
            // 切回来不重算的话，条上写的还是改之前那一版
            if (value == TabRecipe) Recipe.RefreshAll();
            RaiseAll(nameof(IsStart), nameof(IsBench), nameof(IsRecipe), nameof(IsLib),
                     nameof(IsCompounds), nameof(IsCharge), nameof(IsRun), nameof(IsExport));
        }
    }

    public bool IsStart => _tab == TabStart;
    public bool IsBench => _tab == TabBench;
    public bool IsRecipe => _tab == TabRecipe;
    public bool IsLib => _tab == TabLib;
    public bool IsCompounds => _tab == TabCompounds;
    public bool IsCharge => _tab == TabCharge;
    public bool IsRun => _tab == TabRun;
    public bool IsExport => _tab == TabExport;

    /// <summary>标题栏上的实验名。改过还没存的带一个星号。</summary>
    public string DocTitle => Workspace.Store.Title;

    public string SimNote => Workspace.TimeScale > 1
        ? $"仿真 {Workspace.TimeScale:F0}×"
        : "实测";
}
