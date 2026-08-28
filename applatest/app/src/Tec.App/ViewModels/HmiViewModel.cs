using System.Collections.ObjectModel;
using Tec.App.Controls;
using Tec.App.Services;
using Tec.Core;
using Tec.Core.Execution;
using Tec.Core.Records;
using Tec.Core.Recipes;
using Tec.Core.Scheduling;
using Tec.Driver.Abi;

namespace Tec.App.ViewModels;

/// <summary>
/// HMI 手动控制面板（HTLAB_HMI v54 原型的 1:1 还原）。这是「设备自带的
/// 10.1″ 触摸屏界面」弹成一扇 1280×720 的窗（0027 那条设计笔记定下的路）：
/// 显示的每个数都从能力接口 / 数据管线来，下发全部走既有能力接口，
/// 不新开任何通往设备的路；面板自己的状态（设定值、待下发、开关）只属于面板。
/// </summary>
public sealed class HmiViewModel : ViewModelBase
{
    private readonly Workspace _ws;
    private string _page = "ov";
    private string _ovTab = "app";
    private string _zTab = "ctl";
    private int _toastTtl;
    private string _toastText = "";

    public HmiViewModel(Workspace ws, string deviceLabel, IReadOnlyList<int> channels)
    {
        _ws = ws;
        DeviceLabel = deviceLabel;
        for (var i = 0; i < channels.Count; i++)
            Zones.Add(new HmiZoneViewModel(this, ws, i + 1, channels[i]));
        Refresh();
    }

    public string DeviceLabel { get; }
    public ObservableCollection<HmiZoneViewModel> Zones { get; } = new();
    internal Workspace Ws => _ws;

    // ── 页面 / 标签 ─────────────────────────────────────────────────

    public string Page
    {
        get => _page;
        set { if (Set(ref _page, value)) RaisePages(); }
    }

    public string OvTab { get => _ovTab; set { if (Set(ref _ovTab, value)) RaisePages(); } }
    public string ZTab { get => _zTab; set { if (Set(ref _zTab, value)) RaisePages(); } }

    public bool IsOv => _page == "ov";
    public bool IsZone => _page is "z0" or "z1";
    public bool IsFiles => _page == "files";
    public bool IsSys => _page == "sys";
    public bool OnZ0 => _page == "z0";
    public bool OnZ1 => _page == "z1";
    public HmiZoneViewModel? Cur => _page == "z0" ? Zones.ElementAtOrDefault(0)
                                  : _page == "z1" ? Zones.ElementAtOrDefault(1) : null;

    public bool OvApp => _ovTab == "app";
    public bool OvSeq => _ovTab == "seq";
    public bool OvGra => _ovTab == "gra";
    public bool ZCtl => _zTab == "ctl";
    public bool ZOther => _zTab != "ctl";

    public bool ShowOvApp => IsOv && OvApp;
    public bool ShowOvGra => IsOv && OvGra;
    public bool ShowOvSeq => IsOv && OvSeq;
    public bool ShowZCtl => IsZone && ZCtl;
    public bool ShowZGra => IsZone && _zTab == "gra";
    public bool ShowZSeq => IsZone && _zTab == "seq";
    /// <summary>通道页里还没接入的标签（头卡照常挂着，身子给一句实话）。</summary>
    public bool ZStub => IsZone && !(ZCtl || _zTab is "gra" or "seq");
    public bool ShowStub => IsFiles || IsSys;

    /// <summary>还没接入的页给一句实话，不摆一个点了没反应的空壳。</summary>
    public string StubText => "该页在下一补丁接入（本机数据照常记录）";

    private bool _artOpen = true;
    /// <summary>控制页左侧装置图抽屉（原型 artpane 404 ⇄ 28）。</summary>
    public bool ArtOpen
    {
        get => _artOpen;
        set { if (Set(ref _artOpen, value)) Raise(nameof(ArtChevron)); }
    }
    public string ArtChevron => _artOpen ? "❮" : "❯";

    private void RaisePages()
    {
        RaiseAll(nameof(Page), nameof(OvTab), nameof(ZTab), nameof(IsOv), nameof(IsZone),
                 nameof(IsFiles), nameof(IsSys), nameof(OnZ0), nameof(OnZ1), nameof(Cur),
                 nameof(OvApp), nameof(OvSeq), nameof(OvGra), nameof(ZCtl), nameof(ZOther),
                 nameof(ShowOvApp), nameof(ShowOvGra), nameof(ShowOvSeq), nameof(ShowZCtl),
                 nameof(ShowZGra), nameof(ShowZSeq), nameof(ZStub), nameof(ShowStub));
        // 切进曲线/序列页别等下一拍——空一秒的页面看着像坏了
        if (ShowZGra && Cur is { } z) z.RefreshGra();
        if (ShowZSeq && Cur is { } z2) z2.RefreshSeq();
        if (ShowOvGra) RefreshOvChart();
        if (ShowOvSeq) foreach (var zz in Zones) zz.RefreshTl();
    }

    // ── 侧栏状态 ────────────────────────────────────────────────────

    public bool AnyRun => Zones.Any(z => z.EngineRunning || z.TempOn);
    public string RailState => AnyRun ? "CONTROL" : "READY";

    // ── 顶栏右侧 ────────────────────────────────────────────────────

    public string OvRt => $"主机 A · {Zones.Count} 通道 · 两釜温差 {DeltaBetween()} ℃";
    public string ZRt => "采样周期 1 s · 全程记录";

    private string DeltaBetween()
    {
        if (Zones.Count < 2) return "—";
        var a = Zones[0].TrVal; var b = Zones[1].TrVal;
        return a is { } x && b is { } y ? Math.Abs(x - y).ToString("0.0") : "—";
    }

    // ── 曲线总览（原型 ov-gra：通道1 的 Tr/Tj + 通道2 的 Tr，一张图）────

    public HmiChartModel? OvChart { get; private set; }
    public bool OvChartEmpty => OvChart is null;

    internal void RefreshOvChart()
    {
        OvChart = BuildOvChart();
        RaiseAll(nameof(OvChart), nameof(OvChartEmpty));
    }

    private HmiChartModel? BuildOvChart()
    {
        var want = new List<(HmiZoneViewModel Z, string Tag, string Name, string Color)>();
        if (Zones.Count > 0)
        {
            want.Add((Zones[0], "Tr", "Tr 通道1", "#2F8189"));
            want.Add((Zones[0], "Tj", "Tj 通道1", "#4A4A4A"));
        }
        if (Zones.Count > 1) want.Add((Zones[1], "Tr", "Tr 通道2", "#C9C9C9"));

        DateTimeOffset? t0 = null, tEnd = null;
        var raw = new List<(Sample[] Pts, string Name, string Color)>();
        foreach (var (z, tag, name, color) in want)
        {
            var pts = _ws.Pipeline.Snapshot(z.Number, tag);
            if (pts.Length < 2) continue;
            raw.Add((pts, name, color));
            if (t0 is null || pts[0].WallClock < t0) t0 = pts[0].WallClock;
            if (tEnd is null || pts[^1].WallClock > tEnd) tEnd = pts[^1].WallClock;
        }
        if (t0 is null || tEnd is null) return null;
        var span = Math.Max(1, (tEnd.Value - t0.Value).TotalSeconds);

        double dLo = double.MaxValue, dHi = double.MinValue;
        var traces = new List<HmiChartTrace>();
        foreach (var (pts, name, color) in raw)
        {
            var pl = new List<Avalonia.Point>(pts.Length);
            foreach (var s in pts)
            {
                pl.Add(new Avalonia.Point((s.WallClock - t0.Value).TotalSeconds, s.Value));
                if (s.Value < dLo) dLo = s.Value;
                if (s.Value > dHi) dHi = s.Value;
            }
            traces.Add(new HmiChartTrace
            { Name = name, Color = Avalonia.Media.Color.Parse(color), Points = pl });
        }
        // 原型定死 −30…110；真数据出界时把界扩出去——图不许把点裁没了
        var lo = Math.Min(-30.0, dLo);
        var hi = Math.Max(110.0, dHi * 1.12 + 5);
        var labels = new List<string>(7);
        for (var i = 0; i <= 6; i++)
        {
            var sec = i * span / 6;
            labels.Add(span / 60 < 36 ? $"{sec / 60:0.0} min" : $"{Math.Round(sec / 60)} min");
        }
        return new HmiChartModel
        { Traces = traces, XStart = 0, XSpan = span, YLo = lo, YHi = hi, XLabels = labels };
    }

    // ── 点图弹窗（原型 gtap：在所选时刻加标记 / 备注）────────────────

    private double _gtSec;
    private DateTimeOffset? _pendingNoteAt;   // 备注走 Act 弹窗，记在点中的时刻上

    public bool GtOpen { get; private set; }
    public string GtSub { get; private set; } = "";

    public void ChartTapped(double sec)
    {
        if (Cur is not { } z || z.TimeAtSec(sec) is null) return;
        _gtSec = sec;
        GtSub = "曲线位置 t + " + Fmt.Hms(TimeSpan.FromSeconds(Math.Max(0, sec)));
        GtOpen = true;
        RaiseAll(nameof(GtOpen), nameof(GtSub));
    }

    public void GtCancel() { GtOpen = false; Raise(nameof(GtOpen)); }

    public void GtapMark()
    {
        GtOpen = false; Raise(nameof(GtOpen));
        if (Cur is not { } z || z.TimeAtSec(_gtSec) is not { } at) return;
        z.MarkAt(at, $"标记{z.MarkCount + 1}");
        Toast("时间标记已添加（新标记即当前 t=0）");
    }

    public void GtapNote()
    {
        GtOpen = false; Raise(nameof(GtOpen));
        if (Cur is not { } z || z.TimeAtSec(_gtSec) is not { } at) return;
        _pendingNoteAt = at;
        OpenAct("note");
    }

    // ── 曲线颜色弹窗（原型 tcolors）──────────────────────────────────

    public sealed record TColRow(string Key, string Name, IReadOnlyList<TColCell> Cells);
    public sealed record TColCell(string Key, string Hex, bool On);
    private static readonly string[] Palette =
        { "#2F8189", "#3A3A3A", "#4FB1B8", "#C97B2D", "#7B5EA7", "#2F6B38", "#C42B1C" };

    public bool TColOpen { get; private set; }
    public IReadOnlyList<TColRow> TColRows { get; private set; } = Array.Empty<TColRow>();

    public void OpenTCol()
    {
        if (Cur is not { } z) return;
        TColRows = z.TrendRows.Where(t => t.On).Select(t => new TColRow(t.Key, t.Name,
            Palette.Select(c => new TColCell(t.Key, c,
                string.Equals(z.ColorOf(t.Key), c, StringComparison.OrdinalIgnoreCase))).ToList()
        )).ToList();
        TColOpen = true;
        RaiseAll(nameof(TColOpen), nameof(TColRows));
    }

    public void TColPick(string key, string hex)
    {
        Cur?.SetTraceColor(key, hex);
        OpenTCol();   // 重开一遍刷新选中框
    }

    public void TColClose() { TColOpen = false; Raise(nameof(TColOpen)); }

    // ── 键盘弹窗（原型 kp）────────────────────────────────────────────

    private static readonly Dictionary<string, (string Name, string Unit)> Keys = new(StringComparer.Ordinal)
    {
        ["tr"] = ("目标 Tr", "℃"), ["tj"] = ("目标 Tj", "℃"), ["rate"] = ("变温速率", "℃/min"),
        ["dur"] = ("变温时长", "min"), ["rpm"] = ("搅拌转速", "rpm"),
        ["rEnd"] = ("斜坡终值", "rpm"), ["rDur"] = ("斜坡时长", "min"),
    };

    private HmiZoneViewModel? _kpZone;
    private string _kpKey = "";
    private string _kpBuf = "";
    private double _kpLo, _kpHi;
    private bool _kpOpen;

    public bool KpOpen { get => _kpOpen; private set => Set(ref _kpOpen, value); }
    public string KpTitle { get; private set; } = "";
    public string KpUnit { get; private set; } = "";
    public string KpRange { get; private set; } = "";
    public string KpBuf => _kpBuf.Length == 0 ? "0" : _kpBuf;

    public void OpenKeypad(HmiZoneViewModel z, string key)
    {
        if (!Keys.TryGetValue(key, out var meta)) return;
        _kpZone = z;
        _kpKey = key;
        _kpBuf = "";
        (_kpLo, _kpHi) = z.RangeOf(key);
        KpTitle = "输入" + meta.Name;
        KpUnit = meta.Unit;
        KpRange = $"范围 {Txt.Fx(_kpLo).Replace('-', '−')} … {Txt.Fx(_kpHi).Replace('-', '−')} {meta.Unit}";
        KpOpen = true;
        RaiseAll(nameof(KpTitle), nameof(KpUnit), nameof(KpRange), nameof(KpBuf));
    }

    public void KpPress(string d)
    {
        _kpBuf = d == "±"
            ? _kpBuf.StartsWith('−') ? _kpBuf[1..] : "−" + _kpBuf
            : (_kpBuf == "0" ? "" : _kpBuf) + d;
        Raise(nameof(KpBuf));
    }

    public void KpBack() { if (_kpBuf.Length > 0) _kpBuf = _kpBuf[..^1]; Raise(nameof(KpBuf)); }
    public void KpCancel() => KpOpen = false;

    public void KpOk()
    {
        if (_kpZone is not { } z) { KpOpen = false; return; }
        if (!double.TryParse(_kpBuf.Replace('−', '-'), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var v)) { KpOpen = false; return; }
        if (_kpHi > _kpLo)
        {
            if (v < _kpLo) { v = _kpLo; Toast($"低于下限，已修正为 {Txt.Fx(v)} {KpUnit}"); }
            else if (v > _kpHi) { v = _kpHi; Toast($"超出上限，已修正为 {Txt.Fx(v)} {KpUnit}"); }
        }
        z.SetPending(_kpKey, v);
        KpOpen = false;
    }

    // ── 手动操作弹窗（加料 / 取样 / 标记 / 备注 共用一张）────────────

    private bool _actOpen;
    private string _actKind = "";

    public bool ActOpen { get => _actOpen; private set => Set(ref _actOpen, value); }
    public string ActTitle { get; private set; } = "";
    public string ActLabel1 { get; private set; } = "";
    public string ActVal1 { get; set; } = "";
    public string ActVol { get; set; } = "10";
    public string ActRate { get; set; } = "0.5";
    public bool ActIsDose => _actKind == "dose";
    public string ActHint { get; private set; } = "";
    public string ActOk { get; private set; } = "记录";

    public void OpenAct(string kind)
    {
        if (Cur is null) return;
        _actKind = kind;
        (ActTitle, ActLabel1, ActVal1, ActHint, ActOk) = kind switch
        {
            "dose" => ("加料 Dose / Charge", "物质名称", "",
                       "按速率开泵送完自停；这一路没接泵时只能记录人工投料。", "开始加料"),
            "sample" => ("手动取样", "样品名称", $"Sample-{_ws.Clock.Now:HHmm}",
                         "全部样品带时间戳保存，随实验数据一并导出。", "记录取样"),
            "marker" => ("时间标记", "标记名称", "标记",
                         "标记会带时间戳进实验记录（如成核时刻）。", "添加标记"),
            _ => ("备注 Notes", "内容", "",
                  "备注进实验记录，通道跑着时也标到曲线上。", "添加"),
        };
        ActOpen = true;
        RaiseAll(nameof(ActTitle), nameof(ActLabel1), nameof(ActVal1), nameof(ActIsDose),
                 nameof(ActHint), nameof(ActOk));
    }

    public void ActCancel() => ActOpen = false;

    public void ActConfirm()
    {
        if (Cur is not { } z) { ActOpen = false; return; }
        var text = (ActVal1 ?? "").Trim();
        if (text.Length == 0) { Toast("先写点内容"); return; }
        if (_actKind == "dose")
        {
            var okV = double.TryParse(ActVol, out var vol);
            var okR = double.TryParse(ActRate, out var rate);
            if (okV && okR && vol > 0 && rate > 0) z.DoseRun(text, vol, rate);
            else z.Note("加料", $"{text}（人工投料，已记录）");
        }
        else if (_actKind == "marker")
        {
            // 标记落在真实时刻上，并成为当前 t=0（原型 mZero 的规矩）
            z.MarkAt(_ws.Clock.Now, text);
            Toast($"标记已添加：{text}（t=0）");
        }
        else if (_actKind == "note")
        {
            // 从图上点进来的备注记在点中的时刻，普通入口记在当下
            z.NoteAt(_pendingNoteAt ?? _ws.Clock.Now, text);
            _pendingNoteAt = null;
            Toast($"备注已记录：{text}");
        }
        else
        {
            z.Note("取样", text);
        }
        ActOpen = false;
    }

    // ── Toast（原型 hint）────────────────────────────────────────────

    public string ToastText { get => _toastText; private set => Set(ref _toastText, value); }
    public bool ToastOpen => _toastTtl > 0;

    public void Toast(string msg)
    {
        ToastText = msg;
        _toastTtl = 2;
        Raise(nameof(ToastOpen));
    }

    // ── 拍子 ────────────────────────────────────────────────────────

    /// <summary>1 秒一拍：读数、状态、toast 倒计时。视图可见才被调。</summary>
    public void Refresh()
    {
        foreach (var z in Zones)
        {
            // 曲线/序列模型只在有人看的那页重建——切片抽稀和排期估算不该在后台白跑
            z.WantGra = ShowZGra && ReferenceEquals(z, Cur);
            z.WantSeq = ShowZSeq && ReferenceEquals(z, Cur);
            z.WantTl = ShowOvSeq;
            z.Refresh();
        }
        if (ShowOvGra) RefreshOvChart();
        if (_toastTtl > 0 && --_toastTtl == 0) Raise(nameof(ToastOpen));
        RaiseAll(nameof(AnyRun), nameof(RailState), nameof(OvRt), nameof(Cur));
    }

    /// <summary>66 ms 动画拍：搅拌桨往复。返回 false = 没有桨在转，拍子可以停。</summary>
    public bool AnimTick(double dt)
    {
        var any = false;
        foreach (var z in Zones)
            if (z.RpmVal > 0)
            {
                z.PaddlePhase = (z.PaddlePhase + dt / 1.2) % 1.0;
                any = true;
            }
        return any;
    }
}

/// <summary>
/// HMI 的一个通道（原型的 zone）。实测值全部现读现显（能力接口 + 数据管线），
/// 设定值与待下发是面板自己的状态——下发之前设备不知道，也不该知道。
/// </summary>
public sealed class HmiZoneViewModel : ViewModelBase
{
    private readonly HmiViewModel _owner;
    private readonly Workspace _ws;

    public HmiZoneViewModel(HmiViewModel owner, Workspace ws, int index, int channelNumber)
    {
        _owner = owner;
        _ws = ws;
        Index = index;
        Number = channelNumber;
        // pH 电极在不在由台面插拔决定，开机时照实取
        PhOn = HasPh;
    }

    public int Index { get; }
    public int Number { get; }

    private Tec.Core.Benches.Channel? Ch => _ws.ChannelOf(Number);
    private ITemperatureControl? Temp => Ch?.Capabilities.Get<ITemperatureControl>();
    private IStirrer? Stir => Ch?.Capabilities.Get<IStirrer>();
    private IDosing? Dose => Ch?.Capabilities.Get<IDosing>();

    public bool HasPh => Ch?.Capabilities.All.OfType<IScalarSensor>()
        .Any(s => s.Tags.Any(t => t.Tag == "pH")) == true;

    // ── 面板状态：设定值 / 待下发 / 开关 / 模式 ───────────────────────

    public Dictionary<string, double> Sets { get; } = new(StringComparer.Ordinal)
    {
        ["tr"] = 20, ["tj"] = 20, ["rate"] = 0.5, ["dur"] = 10, ["rpm"] = 200,
        ["rEnd"] = 0, ["rDur"] = 0,
    };
    public Dictionary<string, double> Pending { get; } = new(StringComparer.Ordinal);

    public string Mode { get; private set; } = "Tr";
    public bool TempOn { get; private set; }
    public bool StirOn { get; private set; }
    public bool TrOn { get; private set; } = true;
    public bool PhOn { get; private set; }
    public string RampBy { get; private set; } = "rate";

    public double Pv(string k) => Pending.TryGetValue(k, out var v) ? v : Sets[k];
    public bool IsPending(string k) => Pending.ContainsKey(k);
    public int PendCount => Pending.Count;

    public (double Lo, double Hi) RangeOf(string k) => k switch
    {
        // 范围尽量取设备自己的 Limits，设备不在才落回原型缺省——范围也是真数据
        "tr" or "tj" => Temp is { } t ? (t.Limits.Min, t.Limits.Max) : (-40, 180),
        "rate" => Temp is { } t2 ? (0.05, t2.Limits.MaxRatePerMin) : (0.05, 16),
        "dur" => (0.1, 999),
        "rpm" or "rEnd" => Stir is { } s ? (s.Limits.Min, s.Limits.Max) : (0, 1000),
        "rDur" => (0, 999),
        _ => (0, 0)
    };

    public void SetPending(string k, double v)
    {
        Pending[k] = v;
        Log("修改", $"{k} → {Txt.Fx(v)}（待下发）");
        RaiseZone();
    }

    // ── 实测（每秒 Refresh 现读）────────────────────────────────────

    public double? TrVal { get; private set; }
    public double? TjVal { get; private set; }
    public double? TcVal { get; private set; }
    public double? PhVal { get; private set; }
    public double RpmVal { get; private set; }
    public double? TorqueVal { get; private set; }
    public double? FlowVal { get; private set; }
    public double? TotalVal { get; private set; }
    public double? TrRate { get; private set; }

    public double PaddlePhase { get; set; }

    public bool EngineRunning { get; private set; }
    public string HeadName { get; private set; } = "";
    public string HeadRt { get; private set; } = "待机";
    public string HeadRow2 { get; private set; } = "";
    public string HeadNote { get; private set; } = "";
    public double HeadPct { get; private set; }

    public void Refresh()
    {
        TrVal = Temp?.CurrentReactor;
        TjVal = Temp?.CurrentJacket;
        RpmVal = Stir?.CurrentRpm ?? 0;
        TotalVal = Dose?.TotalVolume;
        TcVal = Tag("Tc");
        TorqueVal = Tag("torque");
        FlowVal = Dose is null ? null : Tag("flow") ?? 0;
        PhVal = ReadPh();
        TrRate = MeasuredTrRate();

        var r = _ws.Engine.Runner(Number);
        EngineRunning = r?.State is Tec.Core.Records.ChannelRunState.Running
                                  or Tec.Core.Records.ChannelRunState.Paused;
        var run = r?.Run;
        HeadName = EngineRunning && run is not null ? run.Baseline.Recipe.Name : $"通道 {Index}";
        HeadRt = EngineRunning && run is not null
            ? Fmt.Hms(run.Elapsed(_ws.Clock.Now))
            : TempOn ? "控温中" : "待机";
        HeadRow2 = (EngineRunning ? "程序控制" : "手动控制") + " · " + ModeName;
        // 有 t=0 标记时把「标记 X +时长」缀在右上（原型 zHead 的 rt 段）
        if (MarkText.Length > 0) HeadRt = HeadRt + "　" + MarkText;
        if (WantGra) RefreshGra();
        if (WantSeq) RefreshSeq();
        if (WantTl) RefreshTl();
        if (EngineRunning && run is not null)
        {
            var steps = run.Steps;
            var done = steps.Count(s => s.Status is Tec.Core.Records.StepStatus.Done
                                                or Tec.Core.Records.StepStatus.Skipped);
            var total = Math.Max(1, run.Baseline.Recipe.Steps.Count(s => s.Enabled));
            HeadPct = Math.Min(100, done * 100.0 / total);
            HeadNote = $"步骤 {Math.Min(done + 1, total)}/{total}";
        }
        else
        {
            HeadPct = 0;
            HeadNote = TempOn ? "手动控制 · 温控中" : "手动待机";
        }
        RaiseZone();
    }

    private double? Tag(string tag)
        => _ws.Pipeline.TryLatest(Number, tag, _ws.Clock.Now, out var s)
           && s.Quality is Quality.Good or Quality.Simulated ? s.Value : null;

    private double? ReadPh()
    {
        var ch = Ch;
        if (ch is null) return null;
        foreach (var c in ch.Capabilities.All)
            if (c is IScalarSensor s && s.TryReadLatest("pH", out var smp)
                && smp.Quality is Quality.Good or Quality.Simulated)
                return smp.Value;
        return null;
    }

    /// <summary>实测变温速率：管线里 Tr 最近 ~30 s 两点的斜率，不是设定值的回显。</summary>
    private double? MeasuredTrRate()
    {
        var snap = _ws.Pipeline.Snapshot(Number, "Tr");
        if (snap.Length < 3) return null;
        var last = snap[^1];
        for (var i = snap.Length - 2; i >= 0; i--)
        {
            var dtMin = (last.WallClock - snap[i].WallClock).TotalMinutes;
            if (dtMin >= 0.5)
                return (last.Value - snap[i].Value) / dtMin;
        }
        return null;
    }

    // ── 显示文本 ────────────────────────────────────────────────────

    private static string F1(double? v) => v is { } x ? (x < 0 ? "−" : "") + Math.Abs(x).ToString("0.0") : "—";
    private static string Sg(double v) => (v >= 0 ? "+" : "−") + Math.Abs(v).ToString("0.0");

    public string ModeName => Mode switch
    { "Tj" => "夹套控温 Tj", "TrTj" => "蒸回流 Tj−Tr", _ => "釜内控温 Tr" };
    public bool ModeTr => Mode == "Tr";
    public bool ModeTj => Mode == "Tj";
    public bool ByDur => RampBy == "dur";

    public string TrText => F1(TrVal) + (TrVal is null ? "" : " ℃");
    // 釜上的「设定」牌念**已下发**的值：待下发的改动只亮在输入格的琥珀色里，
    // 别让图上的数抢在「下发设定值」之前变
    public string? TrSetText => Mode != "Tj" ? $"设定 {F1(Sets["tr"])} ℃" : null;
    public string PhText => PhVal is { } p ? p.ToString("0.00") : "—";
    public bool VesselRunning => TempOn || EngineRunning;

    public string TjBox => F1(TjVal);
    public string TjNote => Mode == "Tj" ? $"设定 {F1(Pv("tj"))}"
        : Temp is { } t ? $"上限 {Txt.Fx(t.Limits.Max)}" : "—";
    public bool TjHi => Mode == "Tj";
    public string DtBox => TrVal is { } a && TjVal is { } b ? Sg(a - b) : "—";
    // 热流方向照实说：Tj 比 Tr 热是夹套在**给**热（加热补偿），
    // Tr 比 Tj 热才是釜里在放热（夹套在收）。原型演示稿把 −6.2 K 标成
    // 「放热中」是填的展示词，这里按物理来，死区 ±0.5 K 算平衡
    public string DtNote => Temp is null ? "—" : TempOn || EngineRunning
        ? (TrVal - TjVal is { } d ? d < -0.5 ? "加热补偿" : d > 0.5 ? "放热中" : "趋于平衡" : "—")
        : "温控关闭";
    public string RpmBox => RpmVal.ToString("0");
    public bool RpmOff => RpmVal <= 0;
    public string RpmNote => TorqueVal is { } q ? $"扭矩 {q:0} mN·m" : "扭矩 —";
    public string DoseBox => Dose is null ? "—" : (FlowVal ?? 0).ToString("0.00");
    public bool DoseOff => Dose is null || (FlowVal ?? 0) <= 0;
    public string DoseNote => TotalVal is { } t ? $"累计 {t:0.0} mL" : "未接泵";
    public string TcBox => F1(TcVal);
    public string TcNote => TcVal is null ? "无信号" : "冷媒正常";
    public string RateBox => TrRate is { } r ? (r < 0 ? "−" : "+") + Math.Abs(r).ToString("0.00") : "—";
    public bool RateOff => !(TempOn || EngineRunning);
    public string RateNote => TempOn || EngineRunning ? "实测" : "停止";

    public string NowLine1 => $"Tj {F1(TjVal)} ℃　Tr−Tj {(TrVal is { } a && TjVal is { } b ? Sg(a - b) : "—")} K";
    public string NowLine2 => $"Tc {F1(TcVal)} ℃ · {(TcVal is null ? "无信号" : "冷媒正常")}";
    public string StirNow => $"实测 {RpmVal:0} rpm　扭矩 {(TorqueVal is { } q ? q.ToString("0") : "—")} mN·m";

    public string VbTr => F1(Pv(Mode == "Tj" ? "tj" : "tr"));
    public string VbRate => (Pv("rate") < 0 ? "−" : "") + Math.Abs(Pv("rate")).ToString("0.0");
    public string VbDur => Fmt.Hms(TimeSpan.FromMinutes(Math.Max(0, Pv("dur"))));
    public string VbRpm => Pv("rpm").ToString("0");
    public string VbREnd => Pv("rEnd") > 0 ? Pv("rEnd").ToString("0") : "—";
    public string VbRDur => Pv("rDur") > 0 ? Txt.Fx(Pv("rDur")) : "—";
    public bool ByRate => RampBy == "rate";
    public string TargetLabel => $"目标 {(Mode == "Tj" ? "Tj" : "Tr")}";
    public string RampValLabel => ByRate ? "速率" : "时长";

    /// <summary>底部安全条：直接念安全层此刻的册子，不抄原型里的展示数。</summary>
    public IReadOnlyList<string> LimChipList
    {
        get
        {
            var lims = _ws.Engine.Safety.Limits.Where(l => l.Channel == Number).ToList();
            if (lims.Count == 0) return new[] { "安全层未配置限值" };
            return lims.Select(l =>
            {
                // Tr/Tj/Tc 的限值来自温度联锁（FromTemperature），名目就是 ℃；
                // 负号照面板其他读数用 −（U+2212），别混 ASCII 连字符
                var u = l.Tag is "Tr" or "Tj" or "Tc" ? " ℃" : "";
                static string N(string s) => s.Replace('-', '−');
                return $"{l.Tag} {(l.Min is { } lo ? N(Txt.Fx(lo)) : "")}…{(l.Max is { } hi ? N(Txt.Fx(hi)) : "")}{u}"
                       + (l.MaxRatePerMin is { } r ? $" · ≤{N(Txt.Fx(r))}{u}/min" : "");
            }).ToList();
        }
    }

    /// <summary>侧栏那颗通道钮的状态：绿点（在动）与图标（液体亮青）。</summary>
    public bool RailRun => EngineRunning || TempOn;
    public string RailIcon => RailRun ? "hmi-reactor-run" : "hmi-reactor";
    /// <summary>把目标值那格映射到当前控温对象的键（键盘按它记待下发）。</summary>
    public string TargetKey => Mode == "Tj" ? "tj" : "tr";

    // ── 日志（面板本地，最近三条上屏）────────────────────────────────

    private readonly List<string> _log = new();
    public string LastLog => _log.Count == 0 ? "最近：暂无记录" : "最近：" + string.Join(" · ", _log.Take(3));

    private void Log(string kind, string text)
    {
        _log.Insert(0, $"{_ws.Clock.Now:HH:mm} {kind} {text}");
        if (_log.Count > 20) _log.RemoveAt(_log.Count - 1);
        // 跑着的通道顺手进 GLP 记录：面板操作也是操作
        _ws.Engine.Mark(Number, $"[手动面板] {kind}：{text}", _ws.Operator);
    }

    // ── 操作（全部走能力接口）────────────────────────────────────────

    /// <summary>程序控制中不许手动抢方向盘：引擎与面板同时下发是两个司机。</summary>
    private bool GuardEngine()
    {
        if (!EngineRunning) return false;
        _owner.Toast("程序控制中——请先结束序列再手动操作");
        return true;
    }

    public void SwitchMode(string m)
    {
        if (GuardEngine()) return;
        if (m == "TrTj") { _owner.Toast("蒸回流 Tj−Tr：本驱动暂不支持"); return; }
        if (Mode == m) return;
        Mode = m;
        Log("模式", "切换到 " + ModeName);
        if (TempOn) IssueTemp();
        RaiseZone();
    }

    public void SetRampBy(string v) { RampBy = v; RaiseZone(); }

    public void ToggleTemp()
    {
        if (GuardEngine()) return;
        if (Temp is not { } t) { _owner.Toast("该通道没有温度控制能力"); return; }
        TempOn = !TempOn;
        if (TempOn) IssueTemp();
        else _ = t.StopAsync(CancellationToken.None);
        Log("开关", "温控" + (TempOn ? " 开" : " 关"));
        RaiseZone();
    }

    private void IssueTemp()
    {
        if (Temp is not { } t) return;
        var kind = Mode == "Tj" ? TempChannelKind.Jacket : TempChannelKind.Reactor;
        var target = Pv(Mode == "Tj" ? "tj" : "tr");
        var cur = Mode == "Tj" ? t.CurrentJacket : t.CurrentReactor;
        var rate = RampBy == "rate"
            ? Math.Abs(Pv("rate"))
            : Math.Abs(target - cur) / Math.Max(0.1, Pv("dur"));
        rate = Math.Clamp(rate, 0.05, Math.Max(0.05, t.Limits.MaxRatePerMin));
        _ = t.RampAsync(target, rate, kind, CancellationToken.None);
    }

    public void ToggleStir()
    {
        if (GuardEngine()) return;
        if (Stir is not { } s) { _owner.Toast("该通道没有搅拌能力"); return; }
        StirOn = !StirOn;
        if (StirOn) IssueStir();
        else _ = s.StopAsync(CancellationToken.None);
        Log("开关", "搅拌" + (StirOn ? " 开" : " 关"));
        RaiseZone();
    }

    private void IssueStir()
    {
        if (Stir is not { } s) return;
        var rEnd = Pv("rEnd");
        var rDur = Pv("rDur");
        if (rEnd > 0 && rDur > 0 && s is Tec.Drivers.Simulator.StirrerImpl impl)
        {
            // 斜坡：终值 + 时长换算成驱动的加减速斜率（rampSeconds 按满量程标）
            var delta = Math.Abs(rEnd - s.CurrentRpm);
            if (delta > 1) impl.SetRampSeconds(rDur * 60 * s.Limits.Max / delta);
            _ = s.SetSpeedAsync(rEnd, CancellationToken.None);
        }
        else
        {
            if (s is Tec.Drivers.Simulator.StirrerImpl i2) i2.SetRampSeconds(5);
            _ = s.SetSpeedAsync(Pv("rpm"), CancellationToken.None);
        }
    }

    public void ToggleTrSensor()
    {
        TrOn = !TrOn;
        Log("开关", "Tr 传感器" + (TrOn ? " 开" : " 关"));
        RaiseZone();
    }

    public void TogglePhSensor()
    {
        if (!HasPh && !PhOn) { _owner.Toast("台面上没有给这一路插 pH 电极"); return; }
        PhOn = !PhOn;
        Log("开关", "pH 电极" + (PhOn ? " 开" : " 关"));
        RaiseZone();
    }

    public void Commit()
    {
        if (Pending.Count == 0) return;
        if (GuardEngine()) return;
        var n = Pending.Count;
        foreach (var kv in Pending) Sets[kv.Key] = kv.Value;
        Pending.Clear();
        if (TempOn) IssueTemp();
        if (StirOn) IssueStir();
        Log("下发", $"{n} 项设定值已写入控制器");
        _owner.Toast($"已下发 {n} 项设定值");
        RaiseZone();
    }

    public void Discard()
    {
        if (Pending.Count == 0) return;
        Pending.Clear();
        _owner.Toast("已放弃全部未下发的修改");
        RaiseZone();
    }

    public void StopSequence()
    {
        var r = _ws.Engine.Runner(Number);
        if (r is null || !EngineRunning) return;
        r.Abort(_ws.Operator, "手动面板结束序列");
        _owner.Toast("序列已结束（设备按安全停机收尾：切加热、保搅拌）");
        RaiseZone();
    }

    /// <summary>手动记录类操作（取样 / 标记 / 备注 / 人工投料），进面板日志与 GLP。</summary>
    public void Note(string kind, string text)
    {
        Log(kind, text);
        _owner.Toast($"{kind}已记录：{text}");
        RaiseZone();
    }

    /// <summary>按速率加料：真的开泵（DoseAsync 送完自停）。</summary>
    public void DoseRun(string material, double volume, double rate)
    {
        if (Dose is not { } d) { _owner.Toast("这一路没有接加料泵"); return; }
        var v = Math.Clamp(volume, 0.1, d.Limits.MaxVolume);
        var rt = Math.Clamp(rate, Math.Max(0.01, d.Limits.Min), d.Limits.Max);
        _ = d.DoseAsync(new DoseRequest(v, rt) { Material = material }, CancellationToken.None);
        Log("加料", $"{material} {Txt.Fx(v)} mL · {Txt.Fx(rt)} mL/min");
        _owner.Toast("加料任务已启动");
        RaiseZone();
    }

    // ── 趋势曲线页（0250）────────────────────────────────────────────
    //
    // 数据只有一个来源：数据管线的环形缓冲（每路 8192 点 ≈ 两个多小时，
    // 与台面/运行页看的是同一份）。窗口、平移、缩放都是在这份真数据上
    // 切片，不另攒任何点。

    public sealed record TrendRow(string Key, string Name, string Unit, string ColorHex,
                                  string ValText, bool On);

    private static readonly (string Key, string Name, string Unit, string Color)[] TrendDefs =
    {
        ("tr", "Tr 釜内温度", "℃", "#2F8189"),
        ("tj", "Tj 夹套温度", "℃", "#4A4A4A"),
        ("dt", "Tr−Tj 内外温差", "K", "#8E8E8E"),
        ("ph", "pH", "", "#6F6F6F"),
        ("rpm", "R 转速", "rpm", "#C9C9C9"),
    };

    private readonly HashSet<string> _trends = new(StringComparer.Ordinal) { "tr", "tj", "dt" };
    private readonly Dictionary<string, string> _traceColor = new(StringComparer.Ordinal);
    private double _winSec;          // 0 = 全程
    private double _offSec;          // 距最新点往回退的秒数
    private double _lastWin, _lastFull;
    private bool _clockAxis;
    private DateTimeOffset? _axis0;  // 本轮图的时间原点（最早样点）

    /// <summary>标记（红虚线）与备注（红三角）。时间戳全是真时刻。</summary>
    public sealed record TimeMark(DateTimeOffset At, string Name);
    private readonly List<TimeMark> _marks = new();
    private readonly List<DateTimeOffset> _noteTimes = new();
    private int _markZero = -1;

    /// <summary>页面在看曲线时才重建模型（拥有者每拍设置）。</summary>
    internal bool WantGra { get; set; }

    public HmiChartModel? GraModel { get; private set; }
    public bool GraEmpty => GraModel is null;

    public IReadOnlyList<TrendRow> TrendRows => TrendDefs.Select(d => new TrendRow(
        d.Key, d.Name, d.Unit, ColorOf(d.Key),
        d.Key switch
        {
            "tr" => F1(TrVal),
            "tj" => F1(TjVal),
            "dt" => TrVal is { } a && TjVal is { } b ? Sg(a - b) : "—",
            "ph" => PhText,
            _ => RpmVal.ToString("0"),
        },
        _trends.Contains(d.Key))).ToList();

    public string ColorOf(string key)
        => _traceColor.TryGetValue(key, out var c) ? c : TrendDefs.First(d => d.Key == key).Color;

    public IReadOnlyList<string> EnabledTrends => TrendDefs.Select(d => d.Key)
        .Where(_trends.Contains).ToList();

    public void ToggleTrend(string key)
    {
        if (!_trends.Remove(key)) _trends.Add(key);
        if (_trends.Count == 0) _trends.Add(key);   // 全关没意义，至少留一条
        RefreshGra();
    }

    public void SetTraceColor(string key, string hex) { _traceColor[key] = hex; RefreshGra(); }

    public string WindowLabel => _lastFull <= 0 ? "窗口 —"
        : $"窗口 {Math.Max(1, Math.Round(_lastWin / 60))} min";
    public string AxisModeText => _clockAxis ? "时钟" : "实验时间";

    public void GraZoom(double factor)
    {
        var full = _lastFull;
        if (full <= 0) return;
        var win = _winSec > 0 ? _winSec : full;
        _winSec = Math.Clamp(win * factor, 120, full);
        if (_winSec >= full - 1) _winSec = 0;
        RefreshGra();
    }

    public void GraPan(int dir)
    {
        var full = _lastFull;
        if (full <= 0) return;
        var win = _winSec > 0 ? _winSec : full;
        // ◀ = 往历史退（off 增大），▶ = 往最新走
        _offSec = Math.Clamp(_offSec - dir * win / 4, 0, Math.Max(0, full - win));
        RefreshGra();
    }

    public void GraReset() { _winSec = 0; _offSec = 0; RefreshGra(); }
    public void GraAxisToggle() { _clockAxis = !_clockAxis; RefreshGra(); }

    internal void RefreshGra()
    {
        GraModel = BuildChart();
        RaiseAll(nameof(GraModel), nameof(GraEmpty), nameof(TrendRows),
                 nameof(WindowLabel), nameof(AxisModeText));
    }

    /// <summary>图上点一下的位置换回真时刻（gtap 弹窗要用）。</summary>
    internal DateTimeOffset? TimeAtSec(double sec) => _axis0?.AddSeconds(sec);

    public void MarkAt(DateTimeOffset at, string name)
    {
        _marks.Add(new TimeMark(at, name));
        _markZero = _marks.Count - 1;      // 最新的标记就是当前 t=0（原型 mZero）
        Log("标记", $"{name}（t=0）");
        RefreshGra();
    }

    public void NoteAt(DateTimeOffset at, string text)
    {
        _noteTimes.Add(at);
        Log("备注", text);
        RefreshGra();
    }

    /// <summary>头部「标记 X +时长」那截（有标记才有）。</summary>
    public string MarkText => _markZero >= 0 && _markZero < _marks.Count
        ? $"标记 {_marks[_markZero].Name} +{Fmt.Hms(_ws.Clock.Now - _marks[_markZero].At)}"
        : "";

    public int MarkCount => _marks.Count;

    private Sample[] Snap(string tag) => _ws.Pipeline.Snapshot(Number, tag);

    /// <summary>dt 序列：Tr、Tj 各自的样点按时间就近配对（容差 1.5 s），配不上的丢掉。</summary>
    private (DateTimeOffset T, double V)[] PairedDt()
    {
        var tr = Snap("Tr"); var tj = Snap("Tj");
        var outp = new List<(DateTimeOffset, double)>(Math.Min(tr.Length, tj.Length));
        var j = 0;
        foreach (var a in tr)
        {
            while (j < tj.Length - 1 &&
                   Math.Abs((tj[j + 1].WallClock - a.WallClock).TotalSeconds)
                   <= Math.Abs((tj[j].WallClock - a.WallClock).TotalSeconds)) j++;
            if (j < tj.Length && Math.Abs((tj[j].WallClock - a.WallClock).TotalSeconds) <= 1.5)
                outp.Add((a.WallClock, a.Value - tj[j].Value));
        }
        return outp.ToArray();
    }

    private (DateTimeOffset T, double V)[] SeriesOf(string key) => key switch
    {
        "tr" => Snap("Tr").Select(s => (s.WallClock, s.Value)).ToArray(),
        "tj" => Snap("Tj").Select(s => (s.WallClock, s.Value)).ToArray(),
        "rpm" => Snap("rpm").Select(s => (s.WallClock, s.Value)).ToArray(),
        "ph" => Snap("pH").Select(s => (s.WallClock, s.Value)).ToArray(),
        _ => PairedDt(),
    };

    /// <summary>窗口切片 + 分桶抽稀（每桶留最小/最大两点，尖峰不丢）。</summary>
    private static List<Avalonia.Point> Decimate((DateTimeOffset T, double V)[] pts,
        DateTimeOffset t0, double xStart, double xEnd)
    {
        var inWin = new List<(double X, double V)>(pts.Length);
        foreach (var p in pts)
        {
            var x = (p.T - t0).TotalSeconds;
            if (x >= xStart - 1 && x <= xEnd + 1) inWin.Add((x, p.V));
        }
        const int Buckets = 570;
        var res = new List<Avalonia.Point>(Math.Min(inWin.Count, Buckets * 2));
        if (inWin.Count <= Buckets * 2)
        {
            foreach (var (x, v) in inWin) res.Add(new Avalonia.Point(x, v));
            return res;
        }
        var span = Math.Max(1e-9, xEnd - xStart);
        var bi = 0;
        (double, double) lo = (0, double.MaxValue), hi = (0, double.MinValue);
        void Flush((double, double) l, (double, double) h)
        {
            if (l.Item2 == double.MaxValue) return;
            if (l.Item1 <= h.Item1) { res.Add(new Avalonia.Point(l.Item1, l.Item2)); if (h.Item2 != l.Item2) res.Add(new Avalonia.Point(h.Item1, h.Item2)); }
            else { res.Add(new Avalonia.Point(h.Item1, h.Item2)); res.Add(new Avalonia.Point(l.Item1, l.Item2)); }
        }
        foreach (var (x, v) in inWin)
        {
            var b = Math.Min(Buckets - 1, (int)((x - xStart) / span * Buckets));
            if (b != bi) { Flush(lo, hi); bi = b; lo = (x, double.MaxValue); hi = (x, double.MinValue); }
            if (v < lo.Item2) lo = (x, v);
            if (v > hi.Item2) hi = (x, v);
        }
        Flush(lo, hi);
        return res;
    }

    private HmiChartModel? BuildChart()
    {
        var raw = new List<(string Key, (DateTimeOffset T, double V)[] Pts)>();
        DateTimeOffset? t0 = null, tEnd = null;
        foreach (var key in EnabledTrends)
        {
            var pts = SeriesOf(key);
            if (pts.Length < 2) continue;
            raw.Add((key, pts));
            if (t0 is null || pts[0].T < t0) t0 = pts[0].T;
            if (tEnd is null || pts[^1].T > tEnd) tEnd = pts[^1].T;
        }
        if (t0 is null || tEnd is null) { _axis0 = null; _lastFull = 0; return null; }
        _axis0 = t0;

        var full = Math.Max(1, (tEnd.Value - t0.Value).TotalSeconds);
        var win = _winSec > 0 ? Math.Min(_winSec, full) : full;
        _offSec = Math.Clamp(_offSec, 0, Math.Max(0, full - win));
        var xEnd = full - _offSec;
        var xStart = xEnd - win;
        _lastWin = win; _lastFull = full;

        double lo = double.MaxValue, hi = double.MinValue;
        var traces = new List<HmiChartTrace>();
        foreach (var (key, pts) in raw)
        {
            var dec = Decimate(pts, t0.Value, xStart, xEnd);
            if (dec.Count < 2) continue;
            foreach (var p in dec) { if (p.Y < lo) lo = p.Y; if (p.Y > hi) hi = p.Y; }
            var def = TrendDefs.First(d => d.Key == key);
            traces.Add(new HmiChartTrace
            {
                Name = def.Name,
                Color = Avalonia.Media.Color.Parse(ColorOf(key)),
                Points = dec,
            });
        }
        if (traces.Count == 0) return null;
        // 纵轴界照原型：下界不高于 −30，上界给 12% 余量再加 5
        lo = Math.Min(-30, lo);
        hi = hi * 1.12 + 5;
        if (hi <= lo + 1) hi = lo + 20;

        var labels = new List<string>(7);
        for (var i = 0; i <= 6; i++)
        {
            var sec = xStart + i * win / 6;
            labels.Add(_clockAxis
                ? t0.Value.AddSeconds(sec).ToString(win < 600 ? "HH:mm:ss" : "HH:mm")
                : win / 60 < 36 ? $"{sec / 60:0.0} min" : $"{Math.Round(sec / 60)} min");
        }

        return new HmiChartModel
        {
            Traces = traces,
            XStart = xStart,
            XSpan = win,
            YLo = lo,
            YHi = hi,
            XLabels = labels,
            Marks = _marks.Select((m, i) => new HmiChartMark(
                        (m.At - t0.Value).TotalSeconds, m.Name, i == _markZero)).ToList(),
            Notes = _noteTimes.Select(n => (n - t0.Value).TotalSeconds).ToList(),
        };
    }

    /// <summary>导出快照的 CSV 半边：当前窗口里的原始样点（未抽稀）。返回文件路径。</summary>
    public string? ExportWindowCsv(string dir, string baseName)
    {
        if (_axis0 is not { } t0 || _lastFull <= 0) return null;
        var xEnd = _lastFull - _offSec;
        var xStart = xEnd - _lastWin;
        var sb = new System.Text.StringBuilder("时间,标签,值\n");
        var any = false;
        foreach (var key in EnabledTrends)
            foreach (var (t, v) in SeriesOf(key))
            {
                var x = (t - t0).TotalSeconds;
                if (x < xStart || x > xEnd) continue;
                sb.Append(t.ToString("yyyy-MM-dd HH:mm:ss")).Append(',')
                  .Append(key).Append(',').Append(v.ToString("0.###")).Append('\n');
                any = true;
            }
        if (!any) return null;
        var path = System.IO.Path.Combine(dir, baseName + ".csv");
        System.IO.File.WriteAllText(path, sb.ToString(), System.Text.Encoding.UTF8);
        return path;
    }

    // ── 任务序列页 + 方案总览（0251）─────────────────────────────────
    //
    // 步骤与时长的唯一来源是排期（Schedule.Build）：跑着的读**启动那一刻冻结
    // 的基线**（GLP §7.2），没跑的按当前实测播种现算。面板端只做运行台：
    // 启动 / 继续 / 结束；编排步骤在工作站「配方」页，这里不另开一套编辑器。

    internal bool WantSeq { get; set; }
    internal bool WantTl { get; set; }

    public HmiSeqModel? SeqModel { get; private set; }
    public bool SeqEmpty => SeqModel is null;
    public string SeqSub { get; private set; } = "";
    public string PlanName =>
        _ws.LaneNames.TryGetValue(Number, out var n) && n.Length > 0 ? n : "新配方";

    public sealed record SeqStepBtn(string No, string Ty, string Pm,
                                    bool Done, bool Act, bool Err);
    public IReadOnlyList<SeqStepBtn> SeqSteps { get; private set; } = Array.Empty<SeqStepBtn>();

    public bool CanStartSeq { get; private set; }
    public string StartSeqText { get; private set; } = "启动序列";

    // 方案总览（ov-seq）
    public HmiTlModel? TlModel { get; private set; }
    public string TlHead => $"通道 {Index} · {PlanName}";
    public string TlSub { get; private set; } = "";
    public string TlTag { get; private set; } = "手动模式";
    public string TlTagKind { get; private set; } = "none";   // live / draft / none
    public bool TlLive => TlTagKind == "live";
    public bool TlDraft => TlTagKind == "draft";
    public bool TlEmpty => TlModel is null;

    private Recipe? PlanRecipe => _ws.ChannelRecipes.TryGetValue(Number, out var r) ? r : null;

    /// <summary>估算播种与引擎 SeedFor 同一套：从这台设备此刻的实测出发。</summary>
    private EstimationContext SeedNow()
    {
        var ctx = new EstimationContext();
        if (Temp is { } t)
        {
            ctx.Temperature = t.CurrentReactor;
            ctx.Jacket = t.CurrentJacket;
            ctx.MaxTempRatePerMin = Math.Max(0.05, t.Limits.MaxRatePerMin);
        }
        if (Stir is { } s) ctx.Rpm = s.CurrentRpm;
        if (Dose is { } d)
        {
            ctx.Volume = d.TotalVolume;
            ctx.MaxDoseRatePerMin = Math.Max(0.001, d.Limits.Max);
        }
        return ctx;
    }

    private (Schedule Sched, Recipe Rec, ChannelRun? Run)? Plan()
    {
        var runner = _ws.Engine.Runner(Number);
        if (runner?.Run is { } live &&
            runner.State is ChannelRunState.Running or ChannelRunState.Paused or ChannelRunState.Aborting)
            return (live.Baseline.Schedule, live.Baseline.Recipe, live);
        var rec = PlanRecipe;
        if (rec is null || rec.Steps.Count == 0) return null;
        // 刚跑完/中止的那炉：配方没被改过才把状态染回卡片上（按 Id 对得上才算）
        var last = _ws.Engine.Record.Of(Number);
        var match = last is not null && last.Baseline.Recipe.Id == rec.Id ? last : null;
        return (Schedule.Build(rec, _ws.Engine.Catalog, SeedNow()), rec, match);
    }

    private (string State, string Text, double Frac) StateOf(ScheduleEntry e, ChannelRun? run)
    {
        if (run is null) return ("idle", "", 0);
        var live = run.State is ChannelRunState.Running or ChannelRunState.Paused;
        StepRecord? sr = null;
        foreach (var s in run.Steps) if (s.StepId == e.StepId) sr = s;   // 循环取最后一轮
        if (sr is null) return live ? ("pend", "待执行", 0) : ("idle", "", 0);
        switch (sr.Status)
        {
            case StepStatus.Done:
            case StepStatus.Skipped:
                return ("done", "✓ 已完成", 0);
            case StepStatus.Running:
                var frac = sr.PlanDuration > TimeSpan.Zero && sr.ActualStart is { } a
                    ? Math.Clamp((_ws.Clock.Now - a) / sr.PlanDuration, 0, 1) : 0;
                return ("act", $"进行中 {frac * 100:0}%", frac);
            case StepStatus.Failed:
            case StepStatus.Aborted:
                return ("err", "⚠ 出错 · 已中止", 0);
            default:
                return live ? ("pend", "待执行", 0) : ("idle", "", 0);
        }
    }

    internal void RefreshSeq()
    {
        var plan = Plan();
        var runner = _ws.Engine.Runner(Number);
        CanStartSeq = runner is { CanResume: true }
                      || (plan is not null && (runner is null || runner.CanStart));
        StartSeqText = runner is { CanResume: true } ? "继续序列" : "启动序列";

        if (plan is null)
        {
            SeqModel = null;
            SeqSub = "未编排方案 · 在工作站「配方」页编排步骤";
            SeqSteps = Array.Empty<SeqStepBtn>();
            RaiseSeq();
            return;
        }
        var (sched, rec, run) = plan.Value;
        var cards = new List<HmiSeqCard>();
        var btns = new List<SeqStepBtn>();
        foreach (var e in sched.Entries)
        {
            var step = rec.Steps.FirstOrDefault(s => s.StepId == e.StepId);
            if (step is null || !step.Enabled) continue;
            var known = _ws.Engine.Catalog.TryGet(e.CommandId, out var d);
            var input = new CommandInput(step.Parameters, step.Rows);
            var ty = known ? d.DisplayName : "缺少驱动";
            var sum = known ? d.SummaryOf(input) : e.CommandId;
            var (st, text, frac) = StateOf(e, run);
            var rows = new List<HmiSeqRow>
            {
                new("摘要", sum),
                new("计划开始", Fmt.Hms(e.Start)),
                new("步时长", e.Extent > TimeSpan.Zero ? Fmt.Hms(e.Extent) : "—"),
            };
            if (e.Repeats > 1) rows.Add(new HmiSeqRow("循环", $"×{e.Repeats}"));
            cards.Add(new HmiSeqCard
            {
                No = cards.Count + 1, Type = ty, State = st, StateText = text,
                Rows = rows, T0 = e.StartTemp, T1 = e.EndTemp, Frac = frac,
            });
            btns.Add(new SeqStepBtn($"步骤 {cards.Count}", ty,
                sum.Length > 24 ? sum[..24] + "…" : sum,
                st == "done", st == "act", st == "err"));
        }
        SeqModel = cards.Count > 0 ? new HmiSeqModel { Cards = cards } : null;
        SeqSteps = btns;
        var miss = sched.MissingCommands.Count > 0
            ? $" · 缺少驱动 {string.Join("、", sched.MissingCommands)}" : "";
        SeqSub = $"{cards.Count} 步 · 预计总时长 {Fmt.Hms(sched.Total)}{miss}";
        RaiseSeq();
    }

    private void RaiseSeq() => RaiseAll(nameof(SeqModel), nameof(SeqEmpty), nameof(SeqSub),
        nameof(SeqSteps), nameof(CanStartSeq), nameof(StartSeqText), nameof(PlanName));

    // ── 方案总览时间轴（原型 segLay / tickStep 的逐条移植）───────────

    private const double TlTrack = 1084, TlSegMin = 26;

    private static (double W, bool Zip)[] SegLay(double[] ds)
    {
        var tot = ds.Sum(); if (tot <= 0) tot = 1;
        var nat = ds.Select(d => d / tot * TlTrack).ToArray();
        var zip = nat.Select(v => v < TlSegMin).ToArray();
        var fx = zip.Where(z => z).Sum(_ => TlSegMin);
        var rw = Math.Max(0, TlTrack - fx - (ds.Length - 1));
        var rn = nat.Where((v, i) => !zip[i]).Sum();
        return nat.Select((v, i) => (zip[i] ? TlSegMin : rn > 0 ? v / rn * rw : 0, zip[i])).ToArray();
    }

    private static double SegX((double W, bool Zip)[] lay, double[] ds, double t)
    {
        double a = 0, x = 0;
        for (var i = 0; i < ds.Length; i++)
        {
            if (t <= a + ds[i]) return x + lay[i].W * (ds[i] > 0 ? (t - a) / ds[i] : 0);
            a += ds[i]; x += lay[i].W + 1;
        }
        return x;
    }

    private static double TickStep(double tot)
    {
        foreach (var v in new double[] { 300, 600, 900, 1800, 3600, 7200, 10800 })
            if (tot / v <= 8) return v;
        return 21600;
    }

    private static string TickLabel(double t) => t <= 0 ? "0"
        : t < 3600 ? $"{(int)(t / 60)} min"
        : (t / 3600).ToString(t % 3600 != 0 ? "0.0" : "0") + " h";

    internal void RefreshTl()
    {
        var plan = Plan();
        if (plan is null)
        {
            TlModel = null;
            TlSub = "";
            TlTag = "手动模式"; TlTagKind = "none";
            RaiseAll(nameof(TlModel), nameof(TlEmpty), nameof(TlHead), nameof(TlSub),
                     nameof(TlTag), nameof(TlTagKind), nameof(TlLive), nameof(TlDraft));
            return;
        }
        var (sched, rec, run) = plan.Value;
        var rows = new List<(ScheduleEntry E, double Dur, string Rpm)>();
        var rpm = SeedNow().Rpm;
        foreach (var e in sched.Entries)
        {
            var step = rec.Steps.FirstOrDefault(s => s.StepId == e.StepId);
            if (step is null || !step.Enabled || e.Extent <= TimeSpan.Zero) continue;
            // 搅拌行：有 rpm 参数的步骤换挡，其余延续（机器就是这么保持的）
            if (step.Parameters.Has("rpm")) rpm = step.Parameters.Num("rpm", rpm);
            rows.Add((e, e.Extent.TotalSeconds, $"{rpm:0} rpm"));
        }
        if (rows.Count == 0) { TlModel = null; TlSub = ""; TlTag = "手动模式"; TlTagKind = "none";
            RaiseAll(nameof(TlModel), nameof(TlEmpty), nameof(TlHead), nameof(TlSub),
                     nameof(TlTag), nameof(TlTagKind), nameof(TlLive), nameof(TlDraft)); return; }

        var ds = rows.Select(r => r.Dur).ToArray();
        var lay = SegLay(ds);
        var tot = ds.Sum();
        var live = run is { State: ChannelRunState.Running or ChannelRunState.Paused };
        var doneIdx = -1;
        if (live)
            for (var i = 0; i < rows.Count; i++)
                if (StateOf(rows[i].E, run).State == "done") doneIdx = i;

        HmiTlSeg Seg(int i, string text) => new(lay[i].W, lay[i].Zip, live && i <= doneIdx,
            lay[i].Zip ? "" : text);
        var tempRow = rows.Select((r, i) =>
            Seg(i, $"{(r.E.EndTemp < 0 ? "−" : "")}{Math.Abs(r.E.EndTemp):0} ℃")).ToList();
        var stirRow = rows.Select((r, i) => Seg(i, r.Rpm)).ToList();

        var ticks = new List<(double, string)>();
        for (double t = 0, ss = TickStep(tot); t <= tot; t += ss)
            ticks.Add((SegX(lay, ds, t), TickLabel(t)));

        double? cursor = null;
        if (live && run?.StartedAt is { } t0)
            cursor = SegX(lay, ds, Math.Min((_ws.Clock.Now - t0).TotalSeconds, tot));

        var nz = lay.Count(l => l.Zip);
        TlModel = new HmiTlModel
        { TempRow = tempRow, StirRow = stirRow, Ticks = ticks, CursorX = cursor };
        TlSub = $"{rows.Count} 步 · 总时长 {Fmt.Hms(TimeSpan.FromSeconds(tot))}"
                + (nz > 0 ? $" · {nz} 个短步骤已压缩" : "");
        if (live)
        {
            var (curIdx, n) = (Math.Min(doneIdx + 2, rows.Count), rows.Count);
            TlTag = $"步骤 {curIdx}/{n}"; TlTagKind = "live";
        }
        else { TlTag = "未启动"; TlTagKind = "draft"; }
        RaiseAll(nameof(TlModel), nameof(TlEmpty), nameof(TlHead), nameof(TlSub),
                 nameof(TlTag), nameof(TlTagKind), nameof(TlLive), nameof(TlDraft));
    }

    // ── 序列动作（面板是运行台：启动 / 继续 / 结束；编排去工作站配方页）──

    public void StartSeq()
    {
        var runner = _ws.Engine.Runner(Number);
        if (runner is { CanResume: true })
        {
            runner.Resume(_ws.Operator);
            Log("序列", "已继续");
            _owner.Toast("序列已继续");
            return;
        }
        if (EngineRunning) return;
        var rec = PlanRecipe;
        if (rec is null || rec.Steps.Count(s => s.Enabled) == 0)
        { _owner.Toast("还没编排步骤——在工作站「配方」页编排"); return; }
        if (runner is null) { _owner.Toast($"CH{Number} 不在台面上"); return; }
        _ws.BeginBatch();
        try
        {
            _ws.Engine.StartChannel(Number, rec, _ws.Operator, charge: _ws.ChargeOf(Number));
            Log("序列", $"启动（{rec.Steps.Count(s => s.Enabled)} 步）");
            _owner.Toast($"序列已启动（{rec.Steps.Count(s => s.Enabled)} 步）");
        }
        catch (RecipeRejectedException ex)
        {
            // 校验器拦下的启动：拒绝也要留痕（谁按的、为什么没开）
            _owner.Toast(ex.Message);
            _ws.Log.Write("运行", ex.Message, _ws.Operator);
        }
        catch (Exception ex) { _owner.Toast($"启动失败:{ex.Message}"); }
        RefreshSeq();
        RaiseZone();
    }

    /// <summary>编排类按钮不装样子：面板不编辑配方，照实把路指给工作站。</summary>
    public void SeqEditHint()
        => _owner.Toast("步骤编排在工作站「配方」页——面板端只启动 / 继续 / 结束");

    internal void OpenKeypad(string key) => _owner.OpenKeypad(this, key);

    private void RaiseZone() => RaiseAll(
        nameof(TrVal), nameof(TjVal), nameof(TrText), nameof(TrSetText), nameof(PhText),
        nameof(VesselRunning), nameof(RpmVal), nameof(TjBox), nameof(TjNote), nameof(TjHi),
        nameof(DtBox), nameof(DtNote), nameof(RpmBox), nameof(RpmOff), nameof(RpmNote),
        nameof(DoseBox), nameof(DoseOff), nameof(DoseNote), nameof(TcBox), nameof(TcNote),
        nameof(RateBox), nameof(RateOff), nameof(RateNote), nameof(HeadName), nameof(HeadRt),
        nameof(HeadRow2), nameof(HeadNote), nameof(HeadPct), nameof(EngineRunning),
        nameof(Mode), nameof(ModeName), nameof(ModeTr), nameof(ModeTj), nameof(ByDur),
        nameof(TempOn), nameof(StirOn), nameof(TrOn), nameof(PhOn),
        nameof(RampBy), nameof(ByRate), nameof(TargetLabel), nameof(RampValLabel),
        nameof(VbTr), nameof(VbRate), nameof(VbDur), nameof(VbRpm), nameof(VbREnd), nameof(VbRDur),
        nameof(NowLine1), nameof(NowLine2), nameof(StirNow), nameof(PendCount), nameof(LimChipList),
        nameof(LastLog), nameof(HasCommit), nameof(CommitText), nameof(TrPend), nameof(RatePend),
        nameof(DurPend), nameof(RpmPend), nameof(REndPend), nameof(RDurPend),
        nameof(RailRun), nameof(RailIcon), nameof(TargetKey));

    public bool HasCommit => Pending.Count > 0;
    public string CommitText => $"下发设定值（{Pending.Count} 项）";
    public bool TrPend => IsPending(Mode == "Tj" ? "tj" : "tr");
    public bool RatePend => IsPending("rate");
    public bool DurPend => IsPending("dur");
    public bool RpmPend => IsPending("rpm");
    public bool REndPend => IsPending("rEnd");
    public bool RDurPend => IsPending("rDur");
}
