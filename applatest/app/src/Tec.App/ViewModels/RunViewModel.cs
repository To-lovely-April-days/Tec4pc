using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using Tec.App.Controls;
using Tec.App.Services;
using Tec.Core;
using Tec.Core.Records;
using Tec.Driver.Abi;

namespace Tec.App.ViewModels;

/// <summary>
/// 「机A · A 孔」这种孔位说法。台面视图的通道总表和运行视图的通道磁贴都要用同一句，
/// 两处各写一遍迟早对不上——同一个孔在一页叫「机A · A 孔」另一页叫别的，人会以为是两个孔。
/// </summary>
public static class WellLabel
{
    public static string Of(Workspace ws, int channel)
    {
        var ch = ws.ChannelOf(channel);
        if (ch is null) return "";
        var reactors = ws.Bench.Devices
            .Where(d => ws.Drivers.Driver(d.DriverId) is { Info.ChannelsPerDevice: > 0 }).ToList();
        var idx = reactors.FindIndex(d => d.InstanceId == ch.HostInstanceId);
        var machine = idx >= 0 && idx < 8 ? "机" + "ABCDEFGH"[idx] : ch.HostInstanceId;
        return $"{machine} · {(ch.Well == 0 ? "A" : "B")} 孔";
    }
}

/// <summary>通道磁贴（原型 .stat）：12px 色条 + 竖排 CHn + 数据行 + 当前步。</summary>
public sealed class StatTileViewModel : ViewModelBase
{
    private readonly Workspace _ws;
    public StatTileViewModel(Workspace ws, int channel) { _ws = ws; Channel = channel; }

    private bool _sel;

    public int Channel { get; }
    /// <summary>选中的那一路：顶栏那排单通道操作按钮对它下手。</summary>
    public bool IsSelected { get => _sel; set => Set(ref _sel, value); }
    /// <summary>竖排通道名：一行一个字符（CSS vertical-rl 的等价）。</summary>
    public string NameVertical => string.Join("\n", $"CH{Channel}".ToCharArray());
    public bool Off => !(_ws.ChannelOf(Channel)?.Enabled ?? false);
    public bool On => !Off;
    /// <summary>停用的通道只写「已停用」和它是哪个孔——数据行留着全是「—」，看着像坏了。</summary>
    public string HostLabel => WellLabel.Of(_ws, Channel);
    public string ColorHex => Off ? "#c2c2c2" : Channel switch
    { 1 => "#2f7ed8", 2 => "#2aa87a", 3 => "#c9772b", _ => "#8a63d2" };

    private ChannelRun? Run => _ws.Engine.Record.Of(Channel);
    private bool Started => Run is not null;

    private string Read(string tag, string fmt, string unit)
    {
        if (!Started || !_ws.Pipeline.TryLatest(Channel, tag, _ws.Clock.Now, out var s)) return "—";
        return s.Value.ToString(fmt) + unit;
    }

    public string TrText => Read("Tr", "F1", " ℃");
    public string TjText => Read("Tj", "F1", " ℃");
    public string PhText => HasPh ? Read("pH", "F2", "") : "—";
    public string RpmText => Started && _ws.Pipeline.TryLatest(Channel, "rpm", _ws.Clock.Now, out var s)
        ? $"{s.Value:F0} rpm" : "0 rpm";
    public bool HasPh => _ws.ChannelOf(Channel)?.Capabilities.Get<IScalarSensor>()
                              ?.Tags.Any(t => t.Tag == "pH") ?? false;

    public string StartLine
        => Off ? "已停用"
         : Run is { } r ? $"{r.StartedAt:HH:mm} 启动 · {Brief(r.Elapsed(_ws.Clock.Now))}"
         : "未启动";

    /// <summary>当前步取自执行记录，磁贴与记录表必然一致（原型 stepNowText）。</summary>
    public string StepNow
    {
        get
        {
            if (Off) return "";
            if (!_ws.ChannelRecipes.TryGetValue(Channel, out var recipe) || recipe.Steps.Count == 0)
                return "▶ 待机（未编排）";
            if (Run is not { } r) return "▶ 待机（未启动）";
            var cur = r.Current;
            if (cur is null)
                return r.State == ChannelRunState.Completed ? "▶ 已完成全部步骤" : "▶（步骤间）";
            var ran = _ws.Clock.Now - (cur.ActualStart ?? _ws.Clock.Now);
            return cur.PlanDuration > TimeSpan.Zero
                ? $"▶ {cur.Title}（{Brief(ran)} / {Brief(cur.PlanDuration)}）"
                : $"▶ {cur.Title}";
        }
    }

    public bool NotStartedDot => !Off && !Started;

    private static string Brief(TimeSpan t)
        => t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min"
         : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min"
         : $"{(int)t.TotalSeconds} s";

    public void Tick() => RaiseAll(nameof(TrText), nameof(TjText), nameof(PhText), nameof(RpmText),
                                   nameof(StartLine), nameof(StepNow), nameof(NotStartedDot),
                                   nameof(Off), nameof(On), nameof(HostLabel), nameof(ColorHex));
}

/// <summary>执行记录抽屉的一行（原型 drawRow 的 11 列）。</summary>
public sealed class DrawRowViewModel
{
    public required int Ch { get; init; }
    public string ChColorHex => Ch switch
    { 1 => "#2f7ed8", 2 => "#2aa87a", 3 => "#c9772b", _ => "#8a63d2" };
    public bool IsEvent { get; init; }
    public bool IsAlarm { get; init; }
    /// <summary>事件徽标（手动标记/参数修改/…）或步骤的「执行中」。</summary>
    public string Badge { get; init; } = "";
    public bool HasBadge => Badge.Length > 0;
    public string Seq { get; init; } = "";
    public required string Name { get; init; }
    public string PlanStart { get; init; } = "—";
    public string RealStart { get; init; } = "—";
    public string StartDev { get; init; } = "—";
    public string StartDevClass { get; init; } = "";
    public string PlanDur { get; init; } = "—";
    public string RealDur { get; init; } = "—";
    public bool Running { get; init; }
    public string DurDev { get; init; } = "—";
    public string DurDevClass { get; init; } = "";
    public string EndBy { get; init; } = "—";
    public string User { get; init; } = "";
    public bool GroupSep { get; init; }
}

public sealed class DrawChipViewModel : ViewModelBase
{
    private bool _on = true;
    public required int Ch { get; init; }
    public string ColorHex => Ch switch
    { 1 => "#2f7ed8", 2 => "#2aa87a", 3 => "#c9772b", _ => "#8a63d2" };
    public string Label => $"CH{Ch}";
    public bool On { get => _on; set => Set(ref _on, value); }
}

/// <summary>
/// 运行视图（原型 view-run 的 1:1）：runtop + 三块竖标签 pane
/// （台面总览 560 / 趋势曲线 flex / 步骤甘特 430）+ 底部执行记录抽屉。
/// 启动/暂停/停止走菜单栏的「模拟模式」四钮（原型的运行入口就在那里）。
/// </summary>
public sealed class RunViewModel : ViewModelBase
{
    private readonly Workspace _ws;
    private readonly DispatcherTimer _timer;
    private bool _alignWall = true;
    private bool _showTime = true;
    private bool _drawOpen;
    private bool _withEvents = true;
    private bool _sortByCh;
    private int _trendCh = 1;
    private string _trendWinLabel = "已采集";

    public RunViewModel(Workspace ws)
    {
        _ws = ws;

        ToggleDraw = new RelayCommand(() => DrawOpen = !DrawOpen);
        ToggleChip = new RelayCommand(p => { if (p is DrawChipViewModel c) { c.On = !c.On; RebuildRows(); } });
        BuildCommands();

        foreach (var w in new[] { "已采集", "全程（同甘特）", "最近 30 min", "最近 2 h" }) TrendWins.Add(w);

        // 通道是台面变出来的，而这个视图在开机建通道**之前**就构造好了。
        // 早先磁贴只在构造里灌一次，于是那一排永远是空的——台面上明明有两个通道，
        // 运行页却一块磁贴都没有。跟着台面走才对
        SyncChannels();
        ws.BenchChanged += (_, _) => { SyncChannels(); Tick(); };

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Tick();
    }

    /// <summary>
    /// 磁贴与趋势通道下拉对齐到台面现有的通道。**按通道号比，不按个数比**：
    /// 停用 CH2 同时启用 CH3，个数没变但那是两个通道，只看个数就永远不刷新。
    /// </summary>
    private void SyncChannels()
    {
        var now = _ws.Channels.Select(c => c.Number).OrderBy(x => x).ToList();
        if (Tiles.Select(t => t.Channel).SequenceEqual(now)) return;

        var keep = Selected?.Channel;
        Tiles.Clear();
        foreach (var ch in now) Tiles.Add(new StatTileViewModel(_ws, ch));
        Selected = Tiles.FirstOrDefault(t => t.Channel == keep) ?? Tiles.FirstOrDefault();
        RaiseAll(nameof(NoChannels), nameof(HasSelection));
    }

    // ── 单通道操作 ──────────────────────────────────────────────────
    // 四条通道各跑各的配方，只能整机启停是不够用的：CH2 出了问题要单独停，
    // 别的三条还在跑（这也是这台机器做成四通道的意义）。
    // 引擎本来就是按通道走的（ChannelRunner），这里只是把入口接出来。

    private StatTileViewModel? _selected;

    /// <summary>点中的那块磁贴。顶栏那排按钮对它下手。</summary>
    public StatTileViewModel? Selected
    {
        get => _selected;
        set
        {
            var old = _selected;
            if (!Set(ref _selected, value)) return;
            if (old is not null) old.IsSelected = false;
            if (value is not null) value.IsSelected = true;
            RaiseAll(nameof(HasSelection), nameof(SelectedLabel), nameof(CanStart), nameof(CanPause),
                     nameof(CanStop), nameof(PauseTip), nameof(ActHint));
        }
    }

    public bool HasSelection => _selected is not null;
    public string SelectedLabel => _selected is null ? "" : $"CH{_selected.Channel}";

    private ChannelRun? RunOf(StatTileViewModel? t)
        => t is null ? null : _ws.Engine.Record.Of(t.Channel);
    private Tec.Core.Execution.ChannelRunner? RunnerOf(StatTileViewModel? t)
        => t is null ? null : _ws.Engine.Runner(t.Channel);

    /// <summary>没编排步骤的通道启不了——启起来也是空跑一趟，记录里留一条什么都没干的批次。</summary>
    public bool CanStart
    {
        get
        {
            if (_selected is null || _selected.Off) return false;
            if (!_ws.ChannelRecipes.TryGetValue(_selected.Channel, out var r) || r.Steps.Count == 0) return false;
            // 跑完 / 出故障的可以再来一遍（记录里是新的一条子记录，不覆盖旧的）；
            // 正在跑和正在收尾的不能重启
            var st = RunnerOf(_selected)?.State;
            return st is null or ChannelRunState.Idle or ChannelRunState.Ready
                   or ChannelRunState.Completed or ChannelRunState.Faulted or ChannelRunState.Paused;
        }
    }

    public bool CanPause => RunnerOf(_selected)?.State is ChannelRunState.Running or ChannelRunState.Paused;
    public bool CanStop => RunnerOf(_selected)?.State is ChannelRunState.Running or ChannelRunState.Paused;
    public string PauseTip => RunnerOf(_selected)?.State == ChannelRunState.Paused ? "继续该通道" : "暂停该通道";

    /// <summary>这一排按钮为什么是灰的，得说出来——灰着不解释比没有按钮更难受。</summary>
    public string ActHint
    {
        get
        {
            if (_selected is null) return "点一块通道磁贴，这排按钮就对那一路生效。";
            if (_selected.Off) return $"CH{_selected.Channel} 已停用（在台面上启停）。";
            if (!_ws.ChannelRecipes.TryGetValue(_selected.Channel, out var r) || r.Steps.Count == 0)
                return $"CH{_selected.Channel} 还没编排步骤，去「配方」加几步。";
            return "";
        }
    }

    public RelayCommand StartOne { get; private set; } = null!;
    public RelayCommand PauseOne { get; private set; } = null!;
    public RelayCommand StopOne { get; private set; } = null!;
    public RelayCommand SkipOne { get; private set; } = null!;

    /// <summary>手动标记要写的那句话。空的时候「记一笔」是灰的——空标记进了记录等于噪声。</summary>
    public string MarkText
    {
        get => _markText;
        set { if (Set(ref _markText, value)) Raise(nameof(CanMark)); }
    }
    private string _markText = "";

    /// <summary>只有跑起来的通道才挂得上标记：没有运行记录就没有那条链。</summary>
    public bool CanMark => _markText.Trim().Length > 0 && RunOf(_selected) is not null;

    public RelayCommand Mark { get; private set; } = null!;

    /// <summary>顶栏的一句回话（标记记下了没有、通道停了没有）。</summary>
    public string Say { get; private set; } = "";
    public bool HasSay => Say.Length > 0;

    private void Tell(string text)
    {
        Say = text;
        RaiseAll(nameof(Say), nameof(HasSay));
        var stamp = ++_sayStamp;
        DispatcherTimer.RunOnce(() =>
        {
            if (stamp != _sayStamp) return;
            Say = "";
            RaiseAll(nameof(Say), nameof(HasSay));
        }, TimeSpan.FromSeconds(6));
    }
    private int _sayStamp;

    private void BuildCommands()
    {
        StartOne = new RelayCommand(() =>
        {
            if (_selected is not { } t || !CanStart) return;
            // 第一次启动要开批次，与菜单栏那四个钮同一套（记录里的批次名不能是空的）
            if (_ws.Engine.Record.Channels.Count == 0)
                _ws.Engine.NewBatch(_ws.ExperimentName, _ws.Operator, _ws.Bench.Name);

            var runner = RunnerOf(t);
            if (runner?.State == ChannelRunState.Paused) { runner.Resume(_ws.Operator); Tell($"CH{t.Channel} 已继续"); }
            else
            {
                if (!_ws.ChannelRecipes.TryGetValue(t.Channel, out var recipe)) return;
                try { _ws.Engine.StartChannel(t.Channel, recipe, _ws.Operator); Tell($"CH{t.Channel} 已启动（{recipe.Steps.Count} 步）"); }
                catch (Exception ex) { Tell($"CH{t.Channel} 启动失败：{ex.Message}"); }
            }
            Tick();
        });

        PauseOne = new RelayCommand(() =>
        {
            if (RunnerOf(_selected) is not { } r || _selected is null) return;
            if (r.State == ChannelRunState.Paused) { r.Resume(_ws.Operator); Tell($"CH{_selected.Channel} 已继续"); }
            else { r.Pause(_ws.Operator); Tell($"CH{_selected.Channel} 已暂停"); }
            Tick();
        });

        StopOne = new RelayCommand(() =>
        {
            if (RunnerOf(_selected) is not { } r || _selected is null) return;
            r.Abort(_ws.Operator, $"操作人停止 CH{_selected.Channel}");
            Tell($"CH{_selected.Channel} 已停止");
            Tick();
        });

        SkipOne = new RelayCommand(() =>
        {
            if (RunnerOf(_selected) is not { } r || _selected is null) return;
            r.SkipCurrent(_ws.Operator, "操作人跳过");
            Tell($"CH{_selected.Channel} 已跳过当前步");
            Tick();
        });

        Mark = new RelayCommand(() =>
        {
            if (_selected is not { } t || !CanMark) return;
            if (_ws.Engine.Mark(t.Channel, MarkText, _ws.Operator))
            {
                Tell($"CH{t.Channel} 已记一笔：{MarkText.Trim()}");
                MarkText = "";
                // 记完立刻能看见——抽屉是关着的就替他打开
                DrawOpen = true;
                Tick();
            }
            else Tell($"CH{t.Channel} 还没启动，标记没有可挂的记录链。");
        });
    }

    public Workspace Workspace => _ws;

    /// <summary>台面上一个通道都没有。三块 pane 各自给一句空状态，而不是画一堆空格子。</summary>
    public bool NoChannels => Tiles.Count == 0;

    /// <summary>
    /// 三块 pane 的折叠。原型里那个圆圈箭头只有 cursor:pointer，点了不动——
    /// 这一版让它真收起来：收起后只剩 28px 的竖标签，腾出的宽度给还开着的那块。
    /// </summary>
    public SectionViewModel BenchPane { get; } = new();
    public SectionViewModel TrendPane { get; } = new();
    public SectionViewModel GanttPane { get; } = new();
    public ObservableCollection<StatTileViewModel> Tiles { get; } = new();
    public ObservableCollection<DrawRowViewModel> Rows { get; } = new();
    public ObservableCollection<DrawChipViewModel> Chips { get; } = new();
    public ObservableCollection<string> TrendChannels { get; } = new();
    public ObservableCollection<string> TrendWins { get; } = new();

    public RelayCommand ToggleDraw { get; }
    public RelayCommand ToggleChip { get; }

    // ── runtop（原型 renderRunTop）──────────────────────────────────
    /// <summary>批次跑起来了就用批次名，否则用当前实验名（还没存过盘就是「未命名实验」）。</summary>
    public string ExperimentName => _ws.Engine.Record.Channels.Count > 0
        ? _ws.Engine.Record.Name
        : _ws.ExperimentName;
    public string RunTop
    {
        get
        {
            var started = _ws.Engine.Record.Channels.ToList();
            var enabled = _ws.Channels.Count(c => c.Enabled);
            if (NoChannels) return "台面上还没有通道 —— 去「台面」把反应器拖进来";
            if (started.Count == 0) return $"0 / {enabled} 通道运行中";
            var from = started.Min(r => r.StartedAt);
            var fin = started.Max(r => r.ProjectedFinish(_ws.Clock.Now));
            return $"{started.Count} / {enabled} 通道运行中 · 批次已运行 {Fmt.Hms(_ws.Clock.Now - from)}"
                 + $" · 预计 {fin:HH:mm} 全部完成";
        }
    }

    // ── 趋势 ────────────────────────────────────────────────────────
    public string TrendChLabel
    {
        get => $"通道 {_trendCh}";
        set
        {
            var n = value?.Replace("通道 ", "");
            if (int.TryParse(n, out var c) && c != _trendCh) { _trendCh = c; Tick(); }
        }
    }

    public string TrendWinLabel
    {
        get => _trendWinLabel;
        set { if (Set(ref _trendWinLabel, value)) Tick(); }
    }

    public TrendModel? Trend { get; private set; }
    public bool TrendHasPh => Tiles.FirstOrDefault(t => t.Channel == _trendCh)?.HasPh ?? false;

    /// <summary>
    /// 这一格没有可画的东西。空网格看着像图崩了，要写清楚是「还没启动」还是
    /// 「启动了但还没采到点」——这两件事该做的下一步不一样。
    /// </summary>
    public bool TrendEmpty
        => Trend is null || (Trend.Tr is null && Trend.Tj is null && Trend.Dt is null && Trend.Ph is null);

    public string TrendEmptyText
        => NoChannels ? "台面上还没有通道。"
         : _ws.Engine.Record.Of(_trendCh) is null
             ? $"通道 {_trendCh} 还没有启动。启动后这里实时画 Tr / Tj / Tr−Tj。"
             : "正在等第一批采样……";

    // ── 甘特 ────────────────────────────────────────────────────────
    public string AlignLabel
    {
        get => _alignWall ? "墙钟对齐" : "按通道对齐";
        set
        {
            var wall = value != "按通道对齐";
            if (wall == _alignWall) return;
            _alignWall = wall;
            Raise();
            Tick();
        }
    }
    public ObservableCollection<string> AlignOptions { get; } = new() { "墙钟对齐", "按通道对齐" };

    public bool ShowTime
    {
        get => _showTime;
        set { if (Set(ref _showTime, value)) Tick(); }
    }

    public GanttModel? Gantt { get; private set; }
    public string GTotal { get; private set; } = "";

    // ── 执行记录抽屉 ────────────────────────────────────────────────
    public bool DrawOpen
    {
        get => _drawOpen;
        set { if (Set(ref _drawOpen, value)) { RebuildRows(); Raise(nameof(NoRows)); } }
    }

    public bool WithEvents
    {
        get => _withEvents;
        set { if (Set(ref _withEvents, value)) RebuildRows(); }
    }

    public string SortLabel
    {
        get => _sortByCh ? "按通道分组" : "按实际时刻";
        set
        {
            var byCh = value == "按通道分组";
            if (byCh == _sortByCh) return;
            _sortByCh = byCh;
            Raise();
            RebuildRows();
        }
    }
    public ObservableCollection<string> SortOptions { get; } = new() { "按实际时刻", "按通道分组" };

    public string DrawLast { get; private set; } = "";
    public string DrawCnt { get; private set; } = "";
    public bool NoRows => _drawOpen && Rows.Count == 0;

    // ── 周期刷新 ────────────────────────────────────────────────────
    private void Tick()
    {
        foreach (var t in Tiles) t.Tick();

        // 同上：按内容比，不按个数比
        var enabled = _ws.Channels.Where(c => c.Enabled).Select(c => c.Number).ToList();
        var want = enabled.Select(c => $"通道 {c}").ToList();
        if (!TrendChannels.SequenceEqual(want))
        {
            TrendChannels.Clear();
            foreach (var c in want) TrendChannels.Add(c);
            Raise(nameof(TrendChLabel));
        }
        if (!enabled.Contains(_trendCh) && enabled.Count > 0) { _trendCh = enabled[0]; Raise(nameof(TrendChLabel)); }

        BuildTrend();
        BuildGantt();
        RebuildRows();
        // ExperimentName 早先不在这一串里：视图第一次绑定时还没打开实验，
        // 于是运行页顶上一直写着「未命名实验」，标题栏却已经是打开的那份的名字
        RaiseAll(nameof(ExperimentName),
                 nameof(RunTop), nameof(Trend), nameof(Gantt), nameof(GTotal),
                 nameof(TrendHasPh), nameof(TrendEmpty), nameof(TrendEmptyText),
                 nameof(NoChannels), nameof(DrawLast), nameof(DrawCnt), nameof(NoRows),
                 // 通道状态一直在变（跑完了、被中止了），这排按钮的可按性得跟着变
                 nameof(CanStart), nameof(CanPause), nameof(CanStop), nameof(CanMark),
                 nameof(PauseTip), nameof(ActHint), nameof(SelectedLabel), nameof(HasSelection));
    }

    public void Stop() => _timer.Stop();

    private DateTimeOffset BatchStart => _ws.Engine.Record.FirstStart ?? _ws.Clock.Now;

    /// <summary>axisOf：墙钟 = 批次最早启动为 0；按通道 = 通道自身为 0。</summary>
    private double AxisOf(int ch, double rel)
    {
        if (!_alignWall) return rel;
        var run = _ws.Engine.Record.Of(ch);
        return run is null ? rel : (run.StartedAt - BatchStart).TotalSeconds + rel;
    }

    private string AxisLabel(double sec)
        => _alignWall
            ? (BatchStart + TimeSpan.FromSeconds(sec)).ToString("HH:mm")
            : Fmt.Hms(TimeSpan.FromSeconds(sec));

    private (double A, double B) AxisRange()
    {
        var started = _ws.Engine.Record.Channels.ToList();
        if (!_alignWall)
        {
            var tot = _ws.ChannelRecipes
                .Where(kv => _ws.ChannelOf(kv.Key)?.Enabled == true)
                .Select(kv => Tec.Core.Scheduling.Schedule.Build(kv.Value, _ws.Catalog).Total.TotalSeconds)
                .DefaultIfEmpty(600).Max();
            return (0, Math.Max(600, tot));
        }
        if (started.Count == 0) return (0, 600);
        var now = (_ws.Clock.Now - BatchStart).TotalSeconds;
        var b = started.Max(r => (r.StartedAt - BatchStart).TotalSeconds + r.Baseline.Schedule.Total.TotalSeconds);
        return (0, Math.Max(now + 600, b));
    }

    private void BuildTrend()
    {
        var run = _ws.Engine.Record.Of(_trendCh);
        var elapsed = run is null ? 0 : (_ws.Clock.Now - run.StartedAt).TotalSeconds;
        var win = _trendWinLabel switch
        {
            "全程（同甘特）" => 0.0,
            "最近 30 min" => 1800.0,
            "最近 2 h" => 7200.0,
            _ => -1.0                                  // 已采集
        };
        (double A, double B) rg = win switch
        {
            0 => AxisRange(),
            > 0 => (Math.Max(AxisOf(_trendCh, elapsed - win), AxisOf(_trendCh, 0)), AxisOf(_trendCh, elapsed)),
            _ => (AxisOf(_trendCh, 0), Math.Max(AxisOf(_trendCh, elapsed), AxisOf(_trendCh, 0) + 60))
        };

        TrendSeries? Series(string tag)
        {
            if (run is null || _ws.Pipeline.Series(_trendCh, tag) is not { } s) return null;
            var snap = s.Snapshot();
            if (snap.Length < 2) return null;

            // 原型 trendData 只覆盖「通道启动 → 现在」。采集管线从开机就在记，
            // 启动前的样点（釜温常温平线）不属于这次运行，窗口外的点也不该进图。
            double AxisX(int i) => AxisOf(_trendCh, (snap[i].WallClock - run.StartedAt).TotalSeconds);
            int lo = 0, hi = snap.Length - 1;
            while (lo < hi && AxisX(lo + 1) <= rg.A) lo++;      // 各留一个窗口外的点接线
            while (hi > lo && AxisX(hi - 1) >= rg.B) hi--;
            if (hi - lo + 1 < 2) return null;

            var step = Math.Max(1, (hi - lo + 1) / 400);
            var pts = new List<(double, double)>((hi - lo) / step + 2);
            for (var i = lo; i <= hi; i += step) pts.Add((AxisX(i), snap[i].Value));
            if (pts.Count > 0 && pts[^1].Item1 < AxisX(hi)) pts.Add((AxisX(hi), snap[hi].Value));
            return new TrendSeries { Points = pts };
        }

        Trend = new TrendModel
        {
            AxisFrom = rg.A,
            AxisTo = rg.B,
            NowSec = AxisOf(_trendCh, elapsed),
            HasPh = TrendHasPh,
            Label = AxisLabel,
            Tr = Series("Tr"),
            Tj = Series("Tj"),
            Dt = Series("dT"),
            Ph = TrendHasPh ? Series("pH") : null
        };
    }

    private static string Signed(double sec)
        => (sec < 0 ? "−" : "+") + Fmt.Hms(TimeSpan.FromSeconds(Math.Abs(sec)));

    private static Color ChColor(int chn, bool enabled = true)
        => Color.Parse(enabled ? chn switch
        { 1 => "#2f7ed8", 2 => "#2aa87a", 3 => "#c9772b", _ => "#8a63d2" } : "#c2c2c2");

    private void BuildGantt()
    {
        var rg = AxisRange();
        var m = new GanttModel
        {
            AxisFrom = rg.A,
            AxisTo = rg.B,
            NowSec = _alignWall ? (_ws.Clock.Now - BatchStart).TotalSeconds : null,
            Label = AxisLabel
        };

        foreach (var chn in _ws.Channels.Select(c => c.Number).OrderBy(x => x))
        {
            var ch = _ws.ChannelOf(chn);
            var run = _ws.Engine.Record.Of(chn);
            var recipe = _ws.ChannelRecipes.TryGetValue(chn, out var r) ? r : null;
            var on = ch?.Enabled == true && run is not null;
            var steps = run?.Baseline.Schedule
                        ?? (recipe is null || recipe.Steps.Count == 0
                            ? null
                            : Tec.Core.Scheduling.Schedule.Build(recipe, _ws.Catalog));
            var tot = steps?.Total.TotalSeconds ?? 0;

            var drift = run?.Steps.LastOrDefault()?.StartDeviation?.TotalSeconds ?? 0;
            var note = ch?.Enabled != true ? "停用"
                : recipe is null || recipe.Steps.Count == 0 ? "未编排"
                : run is null ? "未启动"
                : _showTime ? Fmt.Hms(TimeSpan.FromSeconds(tot)) : "";

            var grp = new GanttRow
            {
                IsGroup = true,
                Name = $"CH{chn}",
                Swatch = ChColor(chn, ch?.Enabled == true),
                Note = note,
                Dev = Math.Abs(drift) >= 60 ? Signed(drift) : null,
                // 通道级偏差用绝对阈值：晚 5 / 15 min 才提醒（原型 chDevClass）
                DevClass = Math.Abs(drift) >= 900 ? "bad" : Math.Abs(drift) >= 300 ? "warn" : ""
            };
            if (on)
                grp.Bars.Add(new GanttRowBar
                { Kind = GanttBarKind.Sum, StartSec = AxisOf(chn, 0), DurSec = tot, Color = ChColor(chn) });
            m.Rows.Add(grp);
            if (!on || run is null || steps is null) continue;

            var stepRowStart = m.Rows.Count;
            var byIndex = new Dictionary<int, StepRecord>();
            foreach (var s in run.Steps) byIndex[s.Index] = s;   // 循环时留最后一轮

            for (var i = 0; i < steps.Entries.Count; i++)
            {
                var e = steps.Entries[i];
                var color = Color.Parse(ModuleColorOf(e.CommandId));
                var row = new GanttRow
                {
                    IsGroup = false,
                    Name = ShortName(e.CommandId),
                    Note = _showTime && e.Duration > TimeSpan.Zero ? Fmt.Hms(e.Duration) : null
                };

                byIndex.TryGetValue(i, out var rec);
                double Rel(StepRecord s) => _alignWall
                    ? ((s.ActualStart ?? run.StartedAt) - BatchStart).TotalSeconds
                    : ((s.ActualStart ?? run.StartedAt) - run.StartedAt).TotalSeconds;

                if (e.Span > TimeSpan.Zero)
                    row.Bars.Add(new GanttRowBar
                    { Kind = GanttBarKind.Loop, StartSec = AxisOf(chn, e.Start.TotalSeconds),
                      DurSec = e.Span.TotalSeconds, Color = color, Text = $"×{e.Repeats}" });
                else if (e.Duration < TimeSpan.FromSeconds(1))
                    row.Bars.Add(new GanttRowBar
                    { Kind = GanttBarKind.Mark,
                      StartSec = rec is not null ? Rel(rec) : AxisOf(chn, e.Start.TotalSeconds),
                      Color = color, Dim = rec is null });
                else
                {
                    row.Bars.Add(new GanttRowBar
                    { Kind = GanttBarKind.Plan, StartSec = AxisOf(chn, e.Start.TotalSeconds),
                      DurSec = e.Duration.TotalSeconds, Color = color });
                    if (rec?.ActualStart is not null)
                    {
                        var dur = rec.ActualDuration ?? (_ws.Clock.Now - rec.ActualStart.Value);
                        row.Bars.Add(new GanttRowBar
                        { Kind = GanttBarKind.Real, StartSec = Rel(rec),
                          DurSec = Math.Max(dur.TotalSeconds, 1), Color = color });
                    }
                }
                m.Rows.Add(row);
            }

            // 按通道对齐时红线改为每通道一段短标记（原型 gnowch）
            if (!_alignWall)
            {
                var elapsed = (_ws.Clock.Now - run.StartedAt).TotalSeconds;
                m.NowMarks.Add((elapsed, stepRowStart - 1, steps.Entries.Count + 1));
            }
        }

        Gantt = m;
        GTotal = _alignWall
            ? $"{AxisLabel(rg.A)} – {AxisLabel(rg.B)}"
            : $"最长通道 {Fmt.Hms(TimeSpan.FromSeconds(rg.B))}";
    }

    private string ModuleColorOf(string commandId)
    {
        _ws.Catalog.TryGet(commandId, out var d);
        return ModuleInfo.ColorOf(d?.Module ?? "通用");
    }

    private string ShortName(string commandId)
    {
        _ws.Catalog.TryGet(commandId, out var d);
        return d?.DisplayName ?? commandId;
    }

    // ── 抽屉行（原型 execRows + drawRow）───────────────────────────
    private static string DevClass(double? drift, double plan)
    {
        if (drift is not { } d) return "";
        var a = Math.Abs(d);
        var r = plan > 0 ? a / plan : 1;
        if (a < 60) return "";
        if (a >= 180 && r >= 0.20) return "bad";
        if (r >= 0.10) return "warn";
        return "";
    }

    private string EndByText(StepRecord s) => EndBy.Text(s, _ws.Catalog);

    private static string EvLabel(EventKind k) => k switch
    {
        EventKind.OperatorMark => "手动标记",
        EventKind.ParameterChanged => "参数修改",
        EventKind.Paused => "暂停",
        EventKind.Resumed => "继续",
        EventKind.Alarm or EventKind.SafetyAction => "报警",
        EventKind.ResourceWait => "等待资源",
        _ => "事件"
    };

    private void RebuildRows()
    {
        var started = _ws.Engine.Record.Channels.Select(c => c.Channel).Distinct().OrderBy(x => x).ToList();
        if (!Chips.Select(c => c.Ch).SequenceEqual(started))
        {
            Chips.Clear();
            foreach (var c in started) Chips.Add(new DrawChipViewModel { Ch = c });
        }

        var all = new List<(DateTimeOffset At, DrawRowViewModel Row)>();
        foreach (var run in _ws.Engine.Record.Channels)
        {
            foreach (var s in run.Steps)
                all.Add((s.ActualStart ?? run.StartedAt, new DrawRowViewModel
                {
                    Ch = run.Channel,
                    Seq = (s.Index + 1).ToString(),
                    Name = s.Title,
                    Badge = s.Status == StepStatus.Running ? "执行中" : "",
                    PlanStart = (run.StartedAt + s.PlanStart).ToString("HH:mm:ss"),
                    RealStart = s.ActualStart?.ToString("HH:mm:ss") ?? "—",
                    StartDev = s.StartDeviation is { } sd && Math.Abs(sd.TotalSeconds) >= 1
                        ? Signed(sd.TotalSeconds) : "—",
                    StartDevClass = DevClass(s.StartDeviation?.TotalSeconds, s.PlanDuration.TotalSeconds),
                    PlanDur = s.PlanDuration > TimeSpan.Zero ? Fmt.Hms(s.PlanDuration) : "—",
                    RealDur = Fmt.Hms(s.ActualDuration ?? (s.Status == StepStatus.Running
                        ? _ws.Clock.Now - (s.ActualStart ?? _ws.Clock.Now) : TimeSpan.Zero)),
                    Running = s.Status == StepStatus.Running,
                    DurDev = s.DurationDeviation is { } dd && Math.Abs(dd.TotalSeconds) >= 1
                        ? Signed(dd.TotalSeconds) : "—",
                    DurDevClass = DevClass(s.DurationDeviation?.TotalSeconds, s.PlanDuration.TotalSeconds),
                    EndBy = EndByText(s),
                    User = run.Operator ?? "管理员"
                }));
            foreach (var e in run.Events)
            {
                if (e.Kind is EventKind.ChannelStarted or EventKind.ChannelFinished or EventKind.Note) continue;
                all.Add((e.At, new DrawRowViewModel
                {
                    Ch = run.Channel,
                    IsEvent = true,
                    IsAlarm = e.Kind is EventKind.Alarm or EventKind.SafetyAction,
                    Badge = EvLabel(e.Kind),
                    Name = e.Text,
                    RealStart = e.At.ToString("HH:mm:ss"),
                    User = e.User ?? ""
                }));
            }
        }

        var stepsN = all.Count(x => !x.Row.IsEvent);
        var last = all.OrderBy(x => x.At).LastOrDefault();
        DrawLast = last.Row is null ? "" : $"最近：{last.At:HH:mm:ss} CH{last.Row.Ch} {Trunc(last.Row.Name)}";
        DrawCnt = $"{stepsN} 步 · {all.Count - stepsN} 事件";

        if (!_drawOpen) { Rows.Clear(); return; }

        var active = Chips.Where(c => c.On).Select(c => c.Ch).ToHashSet();
        var filtered = all.Where(x => (active.Count == 0 || active.Contains(x.Row.Ch))
                                   && (_withEvents || !x.Row.IsEvent));
        var ordered = (_sortByCh
            ? filtered.OrderBy(x => x.Row.Ch).ThenBy(x => x.At)
            : filtered.OrderBy(x => x.At)).Select(x => x.Row).ToList();

        for (var i = 1; i < ordered.Count; i++)
            if (_sortByCh && ordered[i].Ch != ordered[i - 1].Ch)
                ordered[i] = Clone(ordered[i], sep: true);

        Rows.Clear();
        foreach (var row in ordered) Rows.Add(row);
    }

    private static string Trunc(string s) => s.Length > 24 ? s[..24] + "…" : s;

    private static DrawRowViewModel Clone(DrawRowViewModel r, bool sep) => new()
    {
        Ch = r.Ch, IsEvent = r.IsEvent, IsAlarm = r.IsAlarm, Badge = r.Badge, Seq = r.Seq,
        Name = r.Name, PlanStart = r.PlanStart, RealStart = r.RealStart,
        StartDev = r.StartDev, StartDevClass = r.StartDevClass, PlanDur = r.PlanDur,
        RealDur = r.RealDur, Running = r.Running, DurDev = r.DurDev, DurDevClass = r.DurDevClass,
        EndBy = r.EndBy, User = r.User, GroupSep = sep
    };
}
