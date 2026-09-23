using System.Collections.ObjectModel;
using Tec.Hmi.Ui.Controls;

using Tec.Core;
using Tec.Core.Benches;
using Tec.Core.Data;
using Tec.Core.Execution;
using Tec.Core.Records;
using Tec.Core.Recipes;
using Tec.Core.Scheduling;
using Tec.Driver.Abi;

namespace Tec.Hmi.Ui.ViewModels;

/// <summary>
/// HMI 手动控制面板（HTLAB_HMI v54 原型的 1:1 还原）。这是「设备自带的
/// 10.1″ 触摸屏界面」弹成一扇 1280×720 的窗（0027 那条设计笔记定下的路）：
/// 显示的每个数都从能力接口 / 数据管线来，下发全部走既有能力接口，
/// 不新开任何通往设备的路；面板自己的状态（设定值、待下发、开关）只属于面板。
/// </summary>
public sealed class HmiViewModel : ViewModelBase
{
    private readonly IHmiHost _ws;
    private string _page = "ov";
    private string _ovTab = "app";
    private string _zTab = "ctl";
    private int _toastTtl;
    private string _toastText = "";

    public HmiViewModel(IHmiHost ws, string deviceLabel, IReadOnlyList<int> channels)
    {
        _ws = ws;
        DeviceLabel = deviceLabel;
        for (var i = 0; i < channels.Count; i++)
            Zones.Add(new HmiZoneViewModel(this, ws, i + 1, channels[i]));
        Refresh();
    }

    public string DeviceLabel { get; }
    public ObservableCollection<HmiZoneViewModel> Zones { get; } = new();
    internal IHmiHost Ws => _ws;

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
    // 页签的选中样式各绑各的——0249 那会儿后四页是占位钮，没绑 .on，
    // 于是「点了页换了、页签还蔫着」（用户指出）
    public bool ZGraTab => _zTab == "gra";
    public bool ZSeqTab => _zTab == "seq";
    public bool ZSafTab => _zTab == "saf";
    public bool ZExpTab => _zTab == "exp";
    public bool ZPidTab => _zTab == "pid";

    public bool ShowOvApp => IsOv && OvApp;
    public bool ShowOvGra => IsOv && OvGra;
    public bool ShowOvSeq => IsOv && OvSeq;
    public bool ShowZCtl => IsZone && ZCtl;
    public bool ShowZGra => IsZone && _zTab == "gra";
    public bool ShowZSeq => IsZone && _zTab == "seq";
    public bool ShowZSaf => IsZone && _zTab == "saf";
    public bool ShowZExp => IsZone && _zTab == "exp";
    public bool ShowZPid => IsZone && _zTab == "pid";
    public bool ShowFiles => IsFiles;
    public bool ShowSys => IsSys;
    /// <summary>兜底：认不出的标签给一句实话（正常路径全部页都已接入）。</summary>
    public bool ZStub => IsZone && !(ZCtl || _zTab is "gra" or "seq" or "saf" or "exp" or "pid");
    public bool ShowStub => false;

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
                 nameof(ZGraTab), nameof(ZSeqTab), nameof(ZSafTab), nameof(ZExpTab), nameof(ZPidTab),
                 nameof(ShowOvApp), nameof(ShowOvGra), nameof(ShowOvSeq), nameof(ShowZCtl),
                 nameof(ShowZGra), nameof(ShowZSeq), nameof(ShowZSaf), nameof(ShowZExp), nameof(ShowZPid),
                 nameof(ShowFiles), nameof(ShowSys), nameof(ZStub), nameof(ShowStub));
        if (ShowZPid && Cur is { } zp) zp.Pid.Refresh();
        // 切页别等下一拍——空一秒的页面看着像坏了
        if (ShowZGra && Cur is { } z) z.RefreshGra();
        if (ShowZSeq && Cur is { } z2) z2.RefreshSeq();
        if (ShowZSaf && Cur is { } z3) z3.RefreshSaf();
        if (ShowZExp) ExpRefresh(reloadArchive: true);   // 归档目录只在进页时读
        if (ShowFiles) FilesRefresh();
        if (ShowSys) SysRefresh();
        if (ShowOvGra) RefreshOvChart();
        if (ShowOvSeq) foreach (var zz in Zones) zz.RefreshTl();
    }

    // ── 侧栏状态 ────────────────────────────────────────────────────

    public bool AnyRun => Zones.Any(z => z.EngineRunning || z.TempOn);
    public string RailState => AnyRun ? "CONTROL" : "READY";

    // ── 顶栏右侧 ────────────────────────────────────────────────────

    public string OvRt => $"主机 A · {Zones.Count} 通道 · {Zones.FirstOrDefault()?.LinkShort ?? "未连接"}"
                          + $" · 两釜温差 {DeltaBetween()} ℃";
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

    // ── 序列步骤类型弹窗（原型 tygrid：2×3 六格）─────────────────────
    //
    // 原型的步骤库就这六个：Tr / Tj / TrTj / R / Wait / Dose。格子照摆全，
    // 干不了的照实说：TrTj 只在设备真有夹套跟随能力（IRefluxControl）时可用，
    // Dose 只在这一路真接了泵时可用——点灰格子弹提示，不装死。

    public sealed record TyRow(string Key, string Name, string Desc,
                               bool Enabled, bool Cur, string Why = "");

    public bool TyOpen { get; private set; }
    public string TyTitle { get; private set; } = "";
    public string TySub { get; private set; } = "";
    public IReadOnlyList<TyRow> TyRows { get; private set; } = Array.Empty<TyRow>();
    private HmiZoneViewModel? _tyZone;
    private int _tyIndex;

    internal void OpenTyPicker(HmiZoneViewModel z, int index)
    {
        _tyZone = z;
        _tyIndex = index;
        TyTitle = $"步骤 {index + 1}";
        TySub = "选择操作类型 · 参数在步骤卡片中直接点击修改";
        var cur = z.SeqTypeAt(index);   // 已有的步骤把当前类型描出来（原型 cur 态）
        var hasPump = z.HasPump;
        var hasStir = z.HasStir;
        var hasReflux = z.CanReflux;
        TyRows = new[]
        {
            new TyRow("Tr", "Tr", "釜内温度", true, cur == "Tr"),
            new TyRow("Tj", "Tj", "夹套温度", true, cur == "Tj"),
            new TyRow("TrTj", "TrTj", "蒸回流", hasReflux, cur == "TrTj",
                      "该设备没有夹套跟随能力（蒸回流）"),
            new TyRow("R", "R", "搅拌转速", hasStir, cur == "R",
                      "这台主机没有搅拌接口——搅拌协议未知"),
            new TyRow("Wait", "Wait", "等待", true, cur == "Wait"),
            new TyRow("Dose", "Dose", "加料", hasPump, cur == "Dose", "这一路没接加料泵"),
        };
        TyOpen = true;
        RaiseAll(nameof(TyOpen), nameof(TyTitle), nameof(TySub), nameof(TyRows));
    }

    public void TyPick(TyRow r)
    {
        if (!r.Enabled) { Toast(r.Why); return; }   // 灰格子：说明为什么，弹窗留着
        TyOpen = false;
        Raise(nameof(TyOpen));
        _tyZone?.SetSeqStep(_tyIndex, r.Key);
    }

    public void TyClose() { TyOpen = false; Raise(nameof(TyOpen)); }

    // ── 数据导出页（原型 zExp，0253）──────────────────────────────────
    //
    // 记录列表 = 本机这一开机的活批次 + 归档目录里的每一炉（跨次开机靠归档）。
    // 导出走既有 RecordExporter（执行记录 / 事件 / 采样宽表），采样间隔就是
    // 宽表的时间栅格——不另写一套导出器。

    public sealed record ExpRow(string Id, string Name, string ChTag, string State,
                                bool Live, string Dur, string Date, bool Sel);

    public IReadOnlyList<ExpRow> ExpRows { get; private set; } = Array.Empty<ExpRow>();
    public bool ExpEmpty => ExpRows.Count == 0;
    private string? _expSel;
    private IReadOnlyList<ArchivedRun>? _arch;
    private int _expInt = 1;
    public string ExpIntText => $"{_expInt} s";

    public void ExpCycleInt()
    {
        _expInt = _expInt switch { 1 => 10, 10 => 60, _ => 1 };
        Raise(nameof(ExpIntText));
    }

    public void ExpSelect(string id)
    {
        _expSel = id;
        ExpRefresh(reloadArchive: false);
    }

    internal void ExpRefresh(bool reloadArchive)
    {
        if (reloadArchive || _arch is null)
        {
            // 归档整目录读一遍不便宜（带采样），只在进页/导出后做，不跟秒拍
            try { _arch = _ws.Archive.Load(); }
            catch { _arch = Array.Empty<ArchivedRun>(); }
        }
        var rows = new List<ExpRow>();
        var rec = _ws.Engine.Record;
        if (rec.Channels.Count > 0)
        {
            var running = rec.Channels.Any(c =>
                c.State is ChannelRunState.Running or ChannelRunState.Paused);
            var dur = rec.Channels.Max(c => c.Elapsed(_ws.Clock.Now));
            rows.Add(new ExpRow("live", rec.Name,
                string.Join(" ", rec.StartedChannels.Select(n => "CH" + n)),
                running ? "运行中" : "本开机批次", running,
                Fmt.Hms(dur), "—", _expSel is null or "live"));
        }
        foreach (var a in _arch.OrderByDescending(x => x.ArchivedAt))
        {
            var aborted = a.Record.Channels.Any(c => c.State is ChannelRunState.Aborted
                                                              or ChannelRunState.Faulted);
            var dur = a.Record.Channels.Count > 0
                ? a.Record.Channels.Max(c => c.Elapsed(a.ArchivedAt)) : TimeSpan.Zero;
            rows.Add(new ExpRow(a.Dir, a.Record.Name,
                string.Join(" ", a.Record.StartedChannels.Select(n => "CH" + n)),
                aborted ? "已中止" : "已完成", false,
                Fmt.Hms(dur),
                a.ArchivedAt.ToString("yyyy-MM-dd"),
                _expSel == a.Dir)
                { });
        }
        ExpRows = rows;
        RaiseAll(nameof(ExpRows), nameof(ExpEmpty), nameof(ExpIntText));
    }

    /// <summary>导出所选：执行记录 + 事件 + 采样宽表（栅格 = 采样间隔）三份 CSV。</summary>
    public void ExpDo()
    {
        var sel = ExpRows.FirstOrDefault(r => r.Sel) ?? ExpRows.FirstOrDefault();
        if (sel is null) { Toast("还没有可导出的记录"); return; }
        (RunRecord Rec, ISampleSource Src)? pick = sel.Id == "live"
            ? (_ws.Engine.Record, _ws.Pipeline)
            : _arch?.FirstOrDefault(a => a.Dir == sel.Id) is { } a2 ? (a2.Record, a2.Samples) : null;
        if (pick is null) { Toast("这条记录已不在归档目录里"); return; }
        var (rec, src) = pick.Value;
        try
        {
            var dir = System.IO.Path.Combine(_ws.DataDir, "Exports",
                $"{San(rec.Name)}-{_ws.Clock.Now:yyyyMMdd-HHmmss}");
            System.IO.Directory.CreateDirectory(dir);
            var opt = new Tec.Core.Export.ExportOptions
            { Shape = Tec.Core.Export.TableShape.Wide, Grid = TimeSpan.FromSeconds(_expInt) };
            if (_ws.Engine.Catalog is Tec.Core.Catalog.CommandCatalog cat)
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "execution.csv"),
                    Tec.Core.Export.RecordExporter.ExecutionCsv(rec, Tec.Core.Export.TimeBase.Wall, cat),
                    System.Text.Encoding.UTF8);
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "events.csv"),
                Tec.Core.Export.RecordExporter.EventsCsv(rec, Tec.Core.Export.TimeBase.Wall),
                System.Text.Encoding.UTF8);
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "samples.csv"),
                Tec.Core.Export.RecordExporter.SamplesWideCsv(src, rec, opt),
                System.Text.Encoding.UTF8);
            _ws.Log.Write("导出", $"HMI 面板导出「{rec.Name}」→ {dir}（间隔 {_expInt} s）", _ws.Operator);
            Toast($"已导出 3 份 CSV：{dir}");
        }
        catch (Exception ex) { Toast("导出失败：" + ex.Message); }
    }

    private static string San(string s)
    {
        foreach (var c in System.IO.Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Length == 0 ? "未命名" : s;
    }

    // ── 配方库页（原型 pageFiles）────────────────────────────────────
    //
    // **面板独立的序列库**，与工作站的配方库是两码事（用户定的）：
    // 存的是面板 6 步序列，落设备本机 HmiPanel/library.json，
    // 不读也不写工作站的 Library / ChannelRecipes / LaneNames。

    /// <summary>库里一条：一份 6 步序列 + 名字 + 存入时刻。</summary>
    public sealed class HmiSeqLibEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
        public string Name { get; set; } = "";
        public DateTime SavedAt { get; set; }
        public List<HmiZoneViewModel.HmiSeqStep> Steps { get; set; } = new();
    }

    public sealed record FileRow(string Id, string Name, string ChTag, string State,
                                 bool Live, bool Loaded, string Detail, bool Sel);

    public IReadOnlyList<FileRow> FileRows { get; private set; } = Array.Empty<FileRow>();
    public bool FilesEmpty => FileRows.Count == 0;
    private string? _filePick;
    private List<HmiSeqLibEntry>? _lib;

    private string LibPath => System.IO.Path.Combine(
        _ws.DataDir, "HmiPanel", "library.json");

    private List<HmiSeqLibEntry> Lib
    {
        get
        {
            if (_lib is not null) return _lib;
            try
            {
                _lib = System.IO.File.Exists(LibPath)
                    ? Tec.Core.Persistence.TecJson.Read<List<HmiSeqLibEntry>>(
                        System.IO.File.ReadAllText(LibPath))
                    : new();
            }
            catch { _lib = new(); }   // 坏文件从空库开始，别把页面卡死
            return _lib;
        }
    }

    private void SaveLib()
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LibPath)!);
            System.IO.File.WriteAllText(LibPath, Tec.Core.Persistence.TecJson.Write(Lib));
        }
        catch { /* 存不上不拦操作，下次改动再试 */ }
    }

    private static bool SeqEq(IReadOnlyList<HmiZoneViewModel.HmiSeqStep> a,
                              IReadOnlyList<HmiZoneViewModel.HmiSeqStep> b)
        => a.Count == b.Count && a.Zip(b).All(p =>
            p.First.Type == p.Second.Type && p.First.Mode == p.Second.Mode
            && Math.Abs(p.First.Tgt - p.Second.Tgt) < 1e-9
            && Math.Abs(p.First.Val - p.Second.Val) < 1e-9
            && Math.Abs(p.First.Rpm - p.Second.Rpm) < 1e-9);

    internal void FilesRefresh()
    {
        var rows = new List<FileRow>();
        // 「装到哪条通道」按内容对：通道草稿和库里这条一步不差才算已加载
        // （装完改过一步就不再是库里那份，照实摘牌）。一条通道只认领一次。
        var drafts = Zones.Select(z => (Zone: z, Steps: z.SeqSnapshot())).ToList();
        var claimed = new HashSet<int>();
        foreach (var e in Lib)
        {
            var on = drafts.FirstOrDefault(d => !claimed.Contains(d.Zone.Number)
                                                && SeqEq(d.Steps, e.Steps)).Zone;
            if (on is not null) claimed.Add(on.Number);
            var live = on is { EngineRunning: true };
            rows.Add(new FileRow(e.Id, e.Name,
                on is null ? "—" : $"通道 {on.Index}",
                live ? "运行中" : on is not null ? "已加载" : "已保存",
                live, on is not null && !live,
                $"{e.Steps.Count} 步 · {e.SavedAt:MM-dd HH:mm} 存",
                _filePick == e.Id));
        }
        FileRows = rows;
        RaiseAll(nameof(FileRows), nameof(FilesEmpty));
    }

    public void FileSelect(string id) { _filePick = id; FilesRefresh(); }

    /// <summary>→ 通道 N：把库里选中的序列装进该通道的 6 步草稿（整份替换）。</summary>
    public void FileApply(int zoneIndex)
    {
        var pick = Lib.FirstOrDefault(e => e.Id == _filePick);
        if (pick is null) { Toast("先点选一条序列"); return; }
        var z = Zones.ElementAtOrDefault(zoneIndex - 1);
        if (z is null) return;
        if (!z.AdoptSeq(pick.Steps)) return;   // 序列锁定时里面已经照实提示
        _ws.Log.Write("面板", $"HMI 面板配方库把「{pick.Name}」装到 CH{z.Number}"
                              + $"（{pick.Steps.Count} 步）", _ws.Operator);
        Toast($"已把「{pick.Name}」装到通道 {z.Index}（{pick.Steps.Count} 步）");
        FilesRefresh();
    }

    /// <summary>
    /// 存通道 N 序列：把该通道当前草稿整份入库。
    /// HMI 只有数字键盘没有文本键盘，名字自动编（CHn 序列 k）。
    /// </summary>
    public void FileSave(int zoneIndex)
    {
        var z = Zones.ElementAtOrDefault(zoneIndex - 1);
        if (z is null) return;
        var steps = z.SeqSnapshot();
        if (steps.Count == 0)
        { Toast($"通道 {z.Index} 还没编排步骤——先在「任务序列」页编几步"); return; }
        if (Lib.Any(e => SeqEq(e.Steps, steps)))
        { Toast("一步不差的序列已经在库里"); return; }
        var k = 1; string name;
        do { name = $"CH{z.Number} 序列 {k++}"; } while (Lib.Any(e => e.Name == name));
        var entry = new HmiSeqLibEntry { Name = name, SavedAt = DateTime.Now, Steps = steps };
        Lib.Add(entry);
        SaveLib();
        _filePick = entry.Id;
        _ws.Log.Write("面板", $"HMI 面板把 CH{z.Number} 的序列存入面板配方库"
                              + $"「{name}」（{steps.Count} 步）", _ws.Operator);
        Toast($"已存入「{name}」（{steps.Count} 步）");
        FilesRefresh();
    }

    public void FileDelete()
    {
        var pick = Lib.FirstOrDefault(e => e.Id == _filePick);
        if (pick is null) { Toast("先点选一条序列"); return; }
        Lib.Remove(pick);
        SaveLib();
        _ws.Log.Write("面板", $"HMI 面板从面板配方库删除「{pick.Name}」", _ws.Operator);
        Toast($"已删除「{pick.Name}」");
        _filePick = null;
        FilesRefresh();
    }

    /// <summary>任务序列页「从配方库」：跳到面板自己的配方库页。</summary>
    public void GoLibrary() => Page = "files";

    // ── 系统页（原型 pageSys：只摆真有的东西）─────────────────────────

    public sealed record SysRow(string K, string V, string D);

    public IReadOnlyList<SysRow> SysRows { get; private set; } = Array.Empty<SysRow>();

    internal void SysRefresh()
    {
        var rows = new List<SysRow>();
        // 链路：台面上每台设备一行，念的是会话的真实状态（打不开带原因）。
        // 从前这里写死一句「本机模拟运行 · 未联网」——程序里已经没有仿真
        foreach (var d in _ws.Bench.Devices)
        {
            var (text, _) = DeviceLink.Describe(_ws.Session(d.InstanceId), _ws.OpenFailure(d.InstanceId));
            var info = _ws.Drivers.Driver(d.DriverId)?.Info;
            rows.Add(new SysRow($"链路 · {d.Display}", text,
                info is null ? $"驱动 {d.DriverId} 未加载" : $"{info.Name} · 串口在台面属性栏配置"));
        }
        if (_ws.Bench.Devices.Count == 0)
            rows.Add(new SysRow("链路", "台面上没有设备", "在工作站的「台面」页把设备拖进来"));
        rows.Add(new SysRow("时间", $"{_ws.Clock.Now:yyyy-MM-dd HH:mm}", "本机时钟"));
        rows.Add(new SysRow("语言与键盘", "简体中文 · QWERTY", "当前版本仅中文界面"));
        try
        {
            var di = new System.IO.DriveInfo(System.IO.Path.GetPathRoot(_ws.DataDir)!);
            rows.Add(new SysRow("本机存储",
                $"剩余 {di.AvailableFreeSpace / 1024.0 / 1024 / 1024:0.0} GB",
                $"数据目录 {_ws.DataDir}"));
        }
        catch { rows.Add(new SysRow("本机存储", "—", _ws.DataDir)); }
        var tc = Zones.FirstOrDefault()?.TcVal;
        rows.Add(new SysRow("冷却",
            tc is { } t ? $"Tc {(t < 0 ? "−" : "")}{Math.Abs(t):0.0} ℃ · 冷媒正常" : "Tc 无信号",
            "Tc 由安全层监视（限值见「反应釜与安全」页）"));
        rows.Add(new SysRow("操作人", _ws.Operator, "登录 / 切换在工作站进行"));
        var dev = Zones.FirstOrDefault()?.Number is { } n && _ws.ChannelOf(n) is { } ch
            ? _ws.Bench.Device(ch.HostInstanceId) : null;
        var drv = dev is null ? null : _ws.Drivers.Driver(dev.DriverId)?.Info;
        var appVer = typeof(HmiViewModel).Assembly.GetName().Version?.ToString(3) ?? "?";
        rows.Add(new SysRow("系统信息",
            $"TecStudio {appVer}" + (drv is null ? "" : $" · 驱动 {drv.Name} {drv.Version}"),
            drv is null ? "驱动未加载" : $"{drv.Vendor} · ABI {drv.Abi}"));
        SysRows = rows;
        Raise(nameof(SysRows));
    }

    // ── 紧急程序报警层（原型 alarmBox）────────────────────────────────
    //
    // 真报警：安全层报警本上这台设备通道里**没人确认过**的那条（最新优先）。
    // 全屏红罩不许点空白关掉——只有「复位（确认）」能收；确认走引擎正路，
    // GLP 记「谁在什么时候看见了它」。这张罩子只给真报警：从前那两个
    // 「模拟紧急程序」演练钮拿掉了——程序里不再有任何模拟。

    private Tec.Core.Safety.Alarm? _alarm;

    public bool AlarmOpen { get; private set; }
    public string AlarmTitle { get; private set; } = "";
    public string AlarmSub { get; private set; } = "";
    public string AlarmHead2 { get; private set; } = "";
    public string AlarmBody { get; private set; } = "";
    public string AlarmDid { get; private set; } = "";
    public bool AlarmMulti { get; private set; }

    private void RefreshAlarm()
    {
        var chans = Zones.Select(z => z.Number).ToHashSet();
        var live = _ws.Engine.Alarms.Live
            .Where(a => chans.Contains(a.Channel) && !a.Acknowledged)
            .OrderByDescending(a => a.LastAt)
            .ToList();
        var top = live.FirstOrDefault();
        if (top is null)
        {
            if (AlarmOpen) { AlarmOpen = false; RaiseAlarm(); }
            _alarm = null;
            return;
        }
        _alarm = top;
        AlarmTitle = $"安全联锁 · {top.ActionText}";
        AlarmSub = $"通道 {Zones.FirstOrDefault(z => z.Number == top.Channel)?.Index ?? top.Channel}"
                   + $" · {top.LastAt:HH:mm:ss}"
                   + (top.Episodes > 1 ? $" · 第 {top.Episodes} 回" : "");
        AlarmHead2 = top.Message;
        AlarmBody = top.Standing
            ? "触发条件此刻仍成立。等条件恢复后按「复位（确认）」翻篇；确认记入 GLP 记录。"
            : "条件已恢复。按「复位（确认）」把这条报警翻篇——谁看见、怎么处理都要留痕。";
        AlarmDid = top.Did.Count > 0 ? "安全层已执行：" + string.Join("；", top.Did)
                                     : "安全层只报警，未动设备——机器还在跑，需人工判断。";
        AlarmMulti = live.Count > 1;
        AlarmOpen = true;
        RaiseAlarm();
    }

    private void RaiseAlarm() => RaiseAll(nameof(AlarmOpen),
        nameof(AlarmTitle), nameof(AlarmSub), nameof(AlarmHead2), nameof(AlarmBody),
        nameof(AlarmDid), nameof(AlarmMulti));

    public void AlarmAck()
    {
        if (_alarm is { } a)
        {
            _ws.Engine.AckAlarm(a.Key, _ws.Operator, "面板复位");
            Toast(a.Standing ? "已确认（条件仍成立，继续监视）" : "已确认，报警翻篇");
        }
        RefreshAlarm();
    }

    public void AlarmAckAll()
    {
        var n = _ws.Engine.AckAllAlarms(_ws.Operator, "面板复位（全部）");
        Toast($"已确认 {n} 条报警");
        RefreshAlarm();
    }

    // ── 键盘弹窗（原型 kp）────────────────────────────────────────────

    private static readonly Dictionary<string, (string Name, string Unit)> Keys = new(StringComparer.Ordinal)
    {
        ["tr"] = ("目标 Tr", "℃"), ["tj"] = ("目标 Tj", "℃"), ["rate"] = ("变温速率", "℃/min"),
        ["dur"] = ("变温时长", "min"), ["rpm"] = ("搅拌转速", "rpm"),
        ["rEnd"] = ("斜坡终值", "rpm"), ["rDur"] = ("斜坡时长", "min"),
        ["dt"] = ("蒸回流 ΔT（Tj−Tr）", "K"),
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
        if (key is "rpm" or "rEnd" or "rDur" && !z.HasStir)
        {
            Toast("这台主机没有搅拌接口——搅拌协议未知，转速设定不可用");
            return;
        }
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
    public void KpCancel() { _kpApply = null; KpOpen = false; }

    /// <summary>安全页那类「不走待下发」的输入借同一张键盘：给回调即可。</summary>
    private Action<double>? _kpApply;

    public void OpenKeypadCustom(string title, string unit, double lo, double hi,
                                 Action<double> apply)
    {
        _kpZone = null;
        _kpKey = "";
        _kpBuf = "";
        _kpApply = apply;
        (_kpLo, _kpHi) = (lo, hi);
        KpTitle = "输入" + title;
        KpUnit = unit;
        KpRange = $"范围 {Txt.Fx(lo).Replace('-', '−')} … {Txt.Fx(hi).Replace('-', '−')} {unit}";
        KpOpen = true;
        RaiseAll(nameof(KpTitle), nameof(KpUnit), nameof(KpRange), nameof(KpBuf));
    }

    public void KpOk()
    {
        if (_kpZone is null && _kpApply is null) { KpOpen = false; return; }
        if (!double.TryParse(_kpBuf.Replace('−', '-'), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var v)) { KpOpen = false; return; }
        if (_kpHi > _kpLo)
        {
            if (v < _kpLo) { v = _kpLo; Toast($"低于下限，已修正为 {Txt.Fx(v)} {KpUnit}"); }
            else if (v > _kpHi) { v = _kpHi; Toast($"超出上限，已修正为 {Txt.Fx(v)} {KpUnit}"); }
        }
        if (_kpApply is { } apply) { _kpApply = null; apply(v); }
        else _kpZone!.SetPending(_kpKey, v);
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
            if (okV && okR && vol > 0 && rate > 0 && z.HasPump) z.DoseRun(text, vol, rate);
            else if (okV && vol > 0)
                // 没接泵（或没填速率）但量是有效的：人工投料把量记全——
                // 从前这条路撞上没泵只弹提示，连记录都不留（实测缺口）
                z.Note("加料", $"{text} {Txt.Fx(vol)} mL（人工投料，已记录）");
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

    // ── 确认弹窗（控着的时候动它：换控温对象这类操作）────────────────
    //
    // 面板上凡是「正在控的东西被动了」都不该顺手一点就生效（用户踩到：釜内控温跑着，
    // 点了「夹套」就按夹套那边的参数动了）。换控温对象弹这张：先说清会发生什么，
    // 三条路让人选——带着目标切 / 先停温控再切 / 不切。改到达方式只换斜率不换目标，
    // 不弹，切完 toast 一句说清做了什么

    private Action? _askPrimary, _askSecondary;

    public bool AskOpen { get; private set; }
    public string AskTitle { get; private set; } = "";
    public string AskSub { get; private set; } = "";
    public string AskBody { get; private set; } = "";
    public string AskPrimary { get; private set; } = "";
    public string AskSecondary { get; private set; } = "";
    public bool AskHasSecondary => _askSecondary is not null;

    public void OpenAsk(string title, string sub, string body, string primary, Action onPrimary,
                        string? secondary = null, Action? onSecondary = null)
    {
        AskTitle = title;
        AskSub = sub;
        AskBody = body;
        AskPrimary = primary;
        AskSecondary = secondary ?? "";
        _askPrimary = onPrimary;
        _askSecondary = onSecondary;
        AskOpen = true;
        RaiseAll(nameof(AskOpen), nameof(AskTitle), nameof(AskSub), nameof(AskBody),
                 nameof(AskPrimary), nameof(AskSecondary), nameof(AskHasSecondary));
    }

    public void AskCancel()
    {
        if (!AskOpen) return;
        AskOpen = false;
        _askPrimary = _askSecondary = null;
        Raise(nameof(AskOpen));
    }

    public void AskPrimaryDo() { var a = _askPrimary; AskCancel(); a?.Invoke(); }
    public void AskSecondaryDo() { var a = _askSecondary; AskCancel(); a?.Invoke(); }

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
            z.WantSaf = ShowZSaf && ReferenceEquals(z, Cur);
            z.WantTl = ShowOvSeq;
            z.Refresh();
        }
        // PID 整定页：只在有人看的那一路刷（稳定度要扫五分钟的样点）
        if (ShowZPid && Cur is { } zp) zp.Pid.Refresh();
        if (ShowOvGra) RefreshOvChart();
        if (ShowZExp) ExpRefresh(reloadArchive: false);
        if (ShowFiles) FilesRefresh();
        if (ShowSys) SysRefresh();
        RefreshAlarm();
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
    private readonly IHmiHost _ws;

    public HmiZoneViewModel(HmiViewModel owner, IHmiHost ws, int index, int channelNumber)
    {
        _owner = owner;
        _ws = ws;
        Index = index;
        Number = channelNumber;
        // pH 电极在不在由台面插拔决定，开机时照实取
        PhOn = HasPh;
        // 面板序列存在设备本机（跨开机还在），开窗就读回来
        LoadSeq();
    }

    public int Index { get; }
    public int Number { get; }

    private Tec.Core.Benches.Channel? Ch => _ws.ChannelOf(Number);
    private ITemperatureControl? Temp => Ch?.Capabilities.Get<ITemperatureControl>();
    private IStirrer? Stir => Ch?.Capabilities.Get<IStirrer>();
    private IDosing? Dose => Ch?.Capabilities.Get<IDosing>();
    private IRefluxControl? Reflux => Ch?.Capabilities.Get<IRefluxControl>();
    private IHeatSource? Heat => Ch?.Capabilities.Get<IHeatSource>();

    /// <summary>上位机 PID 的整定台（「PID 整定」页背后那一层）；温控器 PID 方式 / 没连上是 null。</summary>
    internal IPidTuningBench? PidBench => Ch?.Capabilities.Get<IPidTuningBench>();

    /// <summary>控温方式：true = 上位机 PID，false = 温控器 PID，null = 设备不报 / 没连上。</summary>
    internal bool? HostLoopKnown => Temp is ITemperatureStatus st ? st.HostLoop : null;

    private HmiPidViewModel? _pid;
    /// <summary>「PID 整定」页。</summary>
    public HmiPidViewModel Pid => _pid ??= new HmiPidViewModel(_owner, _ws, this);

    /// <summary>自整定开始时驱动已经把这一路的控温停了：面板的温控开关跟着关，不等三拍对账。</summary>
    internal void NoteTempStoppedForTuning()
    {
        if (!TempOn) return;
        TempOn = false;
        Log("整定", "开始自整定——这一路的控温已停，温控开关跟着关");
        RaiseZone();
    }

    public bool HasPh => Ch?.Capabilities.All.OfType<IScalarSensor>()
        .Any(s => s.Tags.Any(t => t.Tag == "pH")) == true;

    internal bool HasPump => Dose is not null;

    /// <summary>这一路有没有搅拌能力。真机双工位主机没有（搅拌通讯协议未知）——
    /// 面板凡涉及搅拌的显示与操作都按它收口，照加料徽章那套「—/未接」的规矩。</summary>
    internal bool HasStir => Stir is not null;

    /// <summary>这一路有没有蒸回流（夹套跟随）能力。TrTj 模式钮与序列格按它亮灯（§3.2）。</summary>
    public bool CanReflux => Reflux is not null;

    // ── 面板状态：设定值 / 待下发 / 开关 / 模式 ───────────────────────

    public Dictionary<string, double> Sets { get; } = new(StringComparer.Ordinal)
    {
        ["tr"] = 20, ["tj"] = 20, ["rate"] = 0.5, ["dur"] = 10, ["rpm"] = 200,
        ["rEnd"] = 0, ["rDur"] = 0, ["dt"] = 5,
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
        "dt" => (1, 30),          // 与指令声明的 ΔT 范围一致

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
    /// <summary>热源状态量（标签 heat）：0 = TEC，1 = 电加热（未核实），2 = 电加热（已核实）；
    /// −1 = 这台设备没有这一路（老驱动 / 没配 IO8R 的主机也照发 0，所以 −1 只在没数据时出现）。</summary>
    public int HeatState { get; private set; } = -1;

    public double PaddlePhase { get; set; }

    public bool EngineRunning { get; private set; }
    public string HeadName { get; private set; } = "";
    public string HeadRt { get; private set; } = "待机";
    public string HeadCtl { get; private set; } = "手动控制";
    public string HeadNote { get; private set; } = "";
    public double HeadPct { get; private set; }

    // ── 升温/降温徽章(原型 v60 thermState)──────────────────────────
    //
    // 温控关着又没在跑程序，就不显示——不猜。开着时按「设定温度比现在的
    // 釜温高还是低」定方向（Tset 这一签只在控温时才发），0.5 K 死区之内
    // 算恒温。
    //
    // **原型跟的是夹套走势**，在这台机器上会读反：夹套领先釜温，降温时
    // Tj 先冲到目标下方再回摆，降到一半标就翻成「升温」。**跟 duty 也不行**：
    // 控温输出稍有偏差就打满，到温后噪声让它在 ±100 % 之间跳。
    // 没有 Tset 这一签的驱动才退回 Tj 走势（12 拍差 ±0.25 K，原型那把尺子）。

    private readonly List<double> _tjTrail = new();

    /// <summary>0 = 无徽章；1 = 升温中；-1 = 降温中；2 = 恒温。</summary>
    public int Therm { get; private set; }
    public bool ThermOn => Therm != 0;
    public string ThermText => Therm switch
    { 1 => "升温中", -1 => "降温中", 2 => "恒温", _ => "" };

    // ── 链路（头卡左上那颗小签）──────────────────────────────────────
    //
    // 「已连接」只在主机会话真开着时才说；打不开把驱动报的原因原话带出来。
    // 面板上每个数都从会话现读，会话没开就全是「—」——那一排「—」得有一句
    // 解释，不然像是机器坏了。

    public string LinkText { get; private set; } = "未连接";
    /// <summary>签上那两三个字；整句（含原因）在 LinkText，悬停和系统页念它。</summary>
    public string LinkShort { get; private set; } = "未连接";
    public bool LinkOk { get; private set; }

    private void RefreshLink()
    {
        var host = Ch?.HostInstanceId;
        var (text, ok) = host is null
            ? ("未连接", false)
            : DeviceLink.Describe(_ws.Session(host), _ws.OpenFailure(host));
        LinkText = text;
        LinkOk = ok;
        LinkShort = ok ? "已连接"
            : text.StartsWith("未连接", StringComparison.Ordinal) ? "未连接"
            : text.StartsWith("已断开", StringComparison.Ordinal) ? "已断开"
            : text.StartsWith("连接中", StringComparison.Ordinal) ? "连接中"
            : "故障";
    }

    public void Refresh()
    {
        RefreshLink();
        TrVal = Temp?.CurrentReactor;
        TjVal = Temp?.CurrentJacket;
        RpmVal = Stir?.CurrentRpm ?? 0;
        TotalVal = Dose?.TotalVolume;
        TcVal = Tag("Tc");
        TorqueVal = Tag("torque");
        HeatState = Tag("heat") is { } hs ? (int)Math.Round(hs) : -1;
        FlowVal = Dose is null ? null : Tag("flow") ?? 0;
        PhVal = ReadPh();
        TrRate = MeasuredTrRate();

        if (TjVal is { } tjNow)
        {
            _tjTrail.Add(tjNow);
            if (_tjTrail.Count > 13) _tjTrail.RemoveAt(0);
        }
        else _tjTrail.Clear();   // 断读就重攒，别拿旧数据算斜率

        var r = _ws.Engine.Runner(Number);
        EngineRunning = r?.State is Tec.Core.Records.ChannelRunState.Running
                                  or Tec.Core.Records.ChannelRunState.Paused;
        SyncWithLoop();
        // 程序在跑 = 温控当然是开的，不用等面板那个开关也被按下
        Therm = !(TempOn || EngineRunning) ? 0
            : Tag("Tset") is { } sp && TrVal is { } tr
                ? (sp - tr) switch { > 0.5 => 1, < -0.5 => -1, _ => 2 }
            : _tjTrail.Count == 0 ? 0
            : (_tjTrail[^1] - _tjTrail[Math.Max(0, _tjTrail.Count - 12)]) switch
              { > 0.25 => 1, < -0.25 => -1, _ => 2 };
        var run = r?.Run;
        HeadName = EngineRunning && run is not null ? run.Baseline.Recipe.Name : $"通道 {Index}";
        // 右上：跑着显示运行时钟；有徽章时「待机/控温中」让位给徽章（原型
        // zhead 的 rt 段 thBadge||'待机'）；温控开着却读不到 Tj 才落回文字
        // 没连上就不写「待机」——待机是连着的机器闲着，跟没连上不是一回事
        HeadRt = EngineRunning && run is not null
            ? Fmt.Hms(run.Elapsed(_ws.Clock.Now))
            : ThermOn ? "" : !LinkOk ? LinkShort : TempOn ? "控温中" : "待机";
        HeadCtl = EngineRunning ? "程序控制" : "手动控制";
        // 有 t=0 标记时把「标记 X +时长」缀在右上（原型 zHead 的 rt 段）
        if (MarkText.Length > 0)
            HeadRt = (HeadRt.Length > 0 ? HeadRt + "　" : "") + MarkText;
        if (WantGra) RefreshGra();
        if (WantSeq) RefreshSeq();
        if (WantSaf) RefreshSaf();
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
            // 待机时把「温控开关关着」说在头上：设定值填了、下发了、机器没动，八成是这个开关
            HeadNote = !LinkOk ? LinkText : TempOn ? "手动控制 · 温控中" : "手动待机 · 温控开关关着（打开才下发目标）";
        }
        RaiseZone();
    }

    private double? Tag(string tag)
        => _ws.Pipeline.TryLatest(Number, tag, _ws.Clock.Now, out var s)
           && s.Quality is Quality.Good or Quality.Simulated ? s.Value : null;

    // ── 面板开关 ⇄ 回路真实状态对账 ─────────────────────────────────
    //
    // 面板关了再开、程序重启、配方 / 安全停机停了控温……面板自己记的那颗「温控」开关
    // 和目标框都可能跟回路对不上（用户踩到：控温中关掉面板再打开，面板全是缺省）。
    // 回路的真实状态从 ITemperatureStatus 读：下发过目标且没停 = 开着。
    // 面板自己的下发 / 停控是异步的，写到设备要几十毫秒到两秒（热源切换那段），
    // 这期间开关和回路短暂对不上是正常的——连着 3 拍对不上才算真的对不上；
    // 刚建好的面板第一拍就对（那时没有在途的操作）。程序控制期间开关归程序管，不对。

    private int _loopMismatch;
    private bool _loopSynced;

    private void SyncWithLoop()
    {
        if (Temp is not ITemperatureStatus st) { _loopMismatch = 0; return; }
        if (EngineRunning) { _loopMismatch = 0; _loopSynced = true; return; }
        var running = st.Active && st.Setpoint is { } sp;
        if (running == TempOn) { _loopMismatch = 0; _loopSynced = true; return; }
        if (++_loopMismatch < (_loopSynced ? 3 : 1)) return;
        _loopMismatch = 0;
        _loopSynced = true;
        if (running)
        {
            TempOn = true;
            var key = Mode == "Tj" ? "tj" : "tr";
            Sets[key] = st.Setpoint!.Value;
            Pending.Remove(key);
            Log("对账", $"回路在控温（目标 {Txt.Fx(st.Setpoint.Value)} ℃）——面板开关与目标按回路恢复");
        }
        else
        {
            TempOn = false;
            Log("对账", "回路已停（安全停机 / 程序 / 别处停的）——面板「温控」开关跟着关");
        }
    }

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

    // NaN 也是「没读到」：釜内探头没绑 / 没数时 CurrentReactor 就是 NaN，印「—」不印「NaN」
    private static string F1(double? v) => v is { } x && !double.IsNaN(x)
        ? (x < 0 ? "−" : "") + Math.Abs(x).ToString("0.0") : "—";
    private static string Sg(double v) => (v >= 0 ? "+" : "−") + Math.Abs(v).ToString("0.0");

    public string ModeName => Mode switch
    { "Tj" => "夹套控温 Tj", "TrTj" => "蒸回流 Tj−Tr", _ => "釜内控温 Tr" };

    // 热源（需求 §2.5 要求界面上说清「切到电加热了没、核实了没」）。
    // 有这一路就一直显示：TEC 侧是灰字，电加热侧是加热色粗字——
    // 操作人得知道现在这一路能不能制冷（电加热侧只能升不能降）。
    // 「未核实」照原话写出来，没接反馈回路就不假装核实过
    public bool HasHeat => HeatState >= 0;
    public bool HeatHot => HeatState > 0;
    /// <summary>「TEC 加热」没启用时 TEC 侧只有冷源——这一刻升不上去，牌子上就得写出来。</summary>
    private bool CoolOnly => Heat is { TecHeating: false };
    public string HeatText => HeatState switch
    {
        1 => "电加热（未核实）",
        2 => "电加热（已核实）",
        0 => CoolOnly ? "TEC（只制冷）" : "TEC",
        _ => ""
    };
    public bool ModeTr => Mode == "Tr";
    public bool ModeTj => Mode == "Tj";
    /// <summary>蒸回流（夹套跟随）模式。速率/时长两档在这个模式下没有意义——
    /// 跟随的变化率由 Tr 的爬升决定，写 TG 的限速是设备最大能力。</summary>
    public bool ModeFollow => Mode == "TrTj";
    public bool ByDur => RampBy == "dur" && !ModeFollow;

    public string TrText => F1(TrVal) + (TrVal is null ? "" : " ℃");
    // 釜上的「设定」牌念**回路真正在追的值**：回路开着就读温控器此刻的设定值（ITemperatureStatus），
    // 关着才念面板记的那个数。待下发的改动只亮在输入格的琥珀色里，别让图上的数抢在「下发设定值」
    // 之前变。从前念的是面板按模式各记一份的数：在 Tj 模式下发了 −10、切到 Tr 模式牌子写「设定 20」，
    // 温控器明明在追 −10（用户踩到）。蒸回流没有 Tr 目标（Tr 由沸点决定），牌子念的是跟随差
    public string? TrSetText => Mode == "Tj" ? null
        : ModeFollow ? $"ΔT {F1(Sets["dt"])} K" : $"设定 {F1(LoopSetpoint ?? Sets["tr"])} ℃";

    /// <summary>回路此刻真正在追的设定值；回路关着 / 设备不报就是 null。</summary>
    private double? LoopSetpoint
        => Temp is ITemperatureStatus { Active: true, Setpoint: { } sp } ? sp : null;

    /// <summary>控温回路在上位机（上位机 PID）——「尽快」等说明文字按它换说法。</summary>
    private bool HostLoop => Temp is ITemperatureStatus { HostLoop: true };

    /// <summary>
    /// 串级时外环算出来的夹套设定（内环此刻在追的数）。只有上位机串级、在控、Tr 模式才有；
    /// 温控器 PID 方式下没有这个东西——釜内目标就是直接写给夹套的，不编一个「内环」出来。
    /// </summary>
    private double? CascadeInner => Mode == "Tr" && Temp is ITemperatureStatus { Active: true } st
        ? st.CascadeInnerSetpoint : null;

    // 夹套气泡（用户要的：夹套温度实时在图上）。夹套是控温对象时多一行设定 / 跟随；
    // 串级时夹套是内环的被控量，多一行「内环 X ℃」——外环此刻要夹套到多少（用户要的）
    public string TjText => F1(TjVal) + (TjVal is null ? "" : " ℃");
    public string? TjSetText => Mode == "Tj" ? $"设定 {F1(LoopSetpoint ?? Sets["tj"])} ℃"
        : ModeFollow ? $"跟随 Tr+{F1(Sets["dt"])} K"
        : CascadeInner is { } inner ? $"内环 {F1(inner)} ℃" : null;
    public string PhText => PhVal is { } p ? p.ToString("0.00") : "—";
    public bool VesselRunning => TempOn || EngineRunning;

    public string TjBox => F1(TjVal);
    public string TjNote => Mode == "Tj" ? $"设定 {F1(Pv("tj"))}"
        : ModeFollow ? $"跟随 Tr+{F1(Sets["dt"])}"
        : CascadeInner is { } inner ? $"内环 {F1(inner)}"
        : Temp is { } t ? $"上限 {Txt.Fx(t.Limits.Max)}" : "—";
    public bool TjHi => Mode is "Tj" or "TrTj";

    /// <summary>「尽快」下面那行说明：温控器 PID 是温控器自己按最大能力走；上位机 PID 是回路按最大输出（LIMITED 限幅）走。</summary>
    public string FastNote => HostLoop
        ? "不限速\n上位机回路按最大输出（LIMITED）到达"
        : "不限速\n温控器按最大能力到达";
    public string DtBox => TrVal is { } a && TjVal is { } b ? Sg(a - b) : "—";
    // 热流方向照实说：Tj 比 Tr 热是夹套在**给**热（加热补偿），
    // Tr 比 Tj 热才是釜里在放热（夹套在收）。原型演示稿把 −6.2 K 标成
    // 「放热中」是填的展示词，这里按物理来，死区 ±0.5 K 算平衡
    public string DtNote => Temp is null ? "—" : TempOn || EngineRunning
        ? (TrVal - TjVal is { } d ? d < -0.5 ? "加热补偿" : d > 0.5 ? "放热中" : "趋于平衡" : "—")
        : "温控关闭";
    // 没有搅拌能力时显示「—/未接搅拌」而不是 0——0 rpm 读起来像实测值，
    // 分不清「没搅拌器」和「搅拌器停着」（与加料徽章同一套规矩）
    public string RpmBox => HasStir ? RpmVal.ToString("0") : "—";
    public bool RpmOff => !HasStir || RpmVal <= 0;
    public string RpmNote => !HasStir ? "未接搅拌"
        : TorqueVal is { } q ? $"扭矩 {q:0} mN·m" : "扭矩 —";
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
    public string StirNow => HasStir
        ? $"实测 {RpmVal:0} rpm　扭矩 {(TorqueVal is { } q ? q.ToString("0") : "—")} mN·m"
        : "本机无搅拌接口";

    /// <summary>搅拌卡副标题。从前写死仿真器的规格（磁耦合顶置 · 50–1000 rpm ·
    /// 扭矩上限 59 mN·m）——真机上是假话；现按能力现读量程，没有搅拌就说没有。</summary>
    public string StirSpec => Stir is { } s
        ? $"转速 {s.Limits.Min:0}–{s.Limits.Max:0} rpm"
        : "本机无搅拌接口 · 搅拌协议未知";

    public string VbTr => F1(Pv(TargetKey));
    public string VbRate => (Pv("rate") < 0 ? "−" : "") + Math.Abs(Pv("rate")).ToString("0.0");
    public string VbDur => Fmt.Hms(TimeSpan.FromMinutes(Math.Max(0, Pv("dur"))));
    public string VbRpm => Pv("rpm").ToString("0");
    public string VbREnd => Pv("rEnd") > 0 ? Pv("rEnd").ToString("0") : "—";
    public string VbRDur => Pv("rDur") > 0 ? Txt.Fx(Pv("rDur")) : "—";
    public bool ByRate => RampBy == "rate" && !ModeFollow;
    /// <summary>「尽快」：不限速，目标一步写给温控器（普通模式的升温）。速率 / 时长两格都不摆。</summary>
    public bool ByFast => RampBy == "fast" && !ModeFollow;
    public string TargetLabel => ModeFollow ? "ΔT（Tj−Tr）" : $"目标 {(Mode == "Tj" ? "Tj" : "Tr")}";
    public string TargetUnit => ModeFollow ? "K" : "℃";
    public string RampValLabel => ByRate ? "速率" : ByFast ? "" : "时长";

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
                var layer = l.StepScope is not null ? "（本步）" : l.FromRecipe ? "（配方）"
                          : l.FromOperator ? "（操作人）" : "";
                static string N(string s) => s.Replace('-', '−');
                return $"{l.Tag}{layer} {(l.Min is { } lo ? N(Txt.Fx(lo)) : "")}…{(l.Max is { } hi ? N(Txt.Fx(hi)) : "")}{u}"
                       + (l.MaxRatePerMin is { } r ? $" · ≤{N(Txt.Fx(r))}{u}/min" : "");
            }).ToList();
        }
    }

    /// <summary>侧栏那颗通道钮的状态：绿点（在动）与图标（液体亮青）。</summary>
    public bool RailRun => EngineRunning || TempOn;
    public string RailIcon => RailRun ? "hmi-reactor-run" : "hmi-reactor";
    /// <summary>把目标值那格映射到当前控温对象的键（键盘按它记待下发）。
    /// 蒸回流模式那一格是跟随差 ΔT——回流没有温度目标，Tr 停在哪由沸点决定。</summary>
    public string TargetKey => Mode switch { "Tj" => "tj", "TrTj" => "dt", _ => "tr" };

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
        if (m == "TrTj" && !CanReflux)
        {
            _owner.Toast("蒸回流 Tj−Tr：该设备没有夹套跟随能力");
            return;
        }
        if (Mode == m) return;
        if (!TempOn) { SwitchModeNow(m, stopFirst: false); return; }

        // 控着的时候换控温对象不是顺手一点的事（用户踩到：釜内控温跑着，点了「夹套」就按夹套
        // 那边的参数动了）。先说清会发生什么，三条路让人选：带着目标切 / 先停温控再切 / 不切
        var (body, primary) = AskTextFor(m);
        _owner.OpenAsk(
            title: $"正在{ModeName}" + (LoopSetpoint is { } sp ? $"，目标 {Txt.Fx(sp)} ℃" : ""),
            sub: $"要切到{NameOf(m)}——温控开着，先定怎么切",
            body: body,
            primary: primary, onPrimary: () => SwitchModeNow(m, stopFirst: false),
            secondary: "先停温控再切换", onSecondary: () => SwitchModeNow(m, stopFirst: true));
    }

    private static string NameOf(string m) => m switch
    { "Tj" => "夹套控温 Tj", "TrTj" => "蒸回流 Tj−Tr", _ => "釜内控温 Tr" };

    /// <summary>确认弹窗的正文与主钮文字：按「从哪个模式切到哪个模式」把会发生的事说清。</summary>
    private (string Body, string Primary) AskTextFor(string m)
    {
        if (m == "TrTj")
            return ($"切到蒸回流后夹套改为跟随釜内：目标 Tj = Tr + {Txt.Fx(Sets["dt"])} K，现在的目标不再有效；" +
                    "跟随要有釜内 Tr 的有效读数，没有会拒绝并把温控关掉。\n" +
                    "也可以先停温控再切，切完再打开。", "切到蒸回流");
        if (Mode == "TrTj")
        {
            var start = m == "Tj" ? LoopSetpoint : TrVal;
            var how = m == "Tj" ? "夹套当前的跟随目标" : "釜内当前实测，即恒温保持";
            return ($"跟随停止，{NameOf(m)}以{(start is { } s ? $" {Txt.Fx(s)} ℃" : "面板记的目标")}起步（{how}）。\n" +
                    "也可以先停温控再切，切完重新填目标、再打开。", "带着目标切换");
        }
        var cur = LoopSetpoint ?? Pv(TargetKey);
        return ($"温控继续，目标 {Txt.Fx(cur)} ℃ 带过去——变的是控温对象和判到达的依据，不是温度。\n" +
                "也可以先停温控再切，切完重新填目标、再打开。", "带着目标切换");
    }

    /// <summary>真正换模式。温控开着：目标带过去再重新下发；stopFirst：先把温控关了再换，不下发。</summary>
    private void SwitchModeNow(string m, bool stopFirst)
    {
        if (Mode == m) return;
        var from = Mode;
        if (stopFirst && TempOn)
        {
            TempOn = false;
            if (Temp is { } t) _ = t.StopAsync(CancellationToken.None);
            Log("开关", $"温控 关（换到{NameOf(m)}前先停）");
        }
        Mode = m;
        Log("模式", "切换到 " + ModeName);
        if (TempOn)
        {
            // 温控开着换模式：**目标温度带过去**，别让换个模式把温度改了。Tr / Tj 两个模式在面板上
            // 各记一份目标，从前换模式是按新模式那份重新下发——在 Tj 下发了 −10、切到 Tr 就把
            // 缺省的 20 发给了温控器（用户踩到）。换的是控温对象和判到达的依据，不是温度。
            // 从蒸回流出来：切夹套接着当前的跟随目标，切釜内按当前实测恒温；切进蒸回流目标它自己算
            double? carry = (from, m) switch
            {
                ("TrTj", "Tr") => TrVal ?? Pv("tr"),
                ("TrTj", "Tj") => LoopSetpoint ?? Pv("tj"),
                (_, "TrTj") => (double?)null,
                _ => LoopSetpoint ?? Pv(from == "Tj" ? "tj" : "tr")
            };
            if (carry is { } c)
            {
                var key = m == "Tj" ? "tj" : "tr";
                Sets[key] = c;
                Pending.Remove(key);
                Log("模式", $"目标 {Txt.Fx(c)} ℃ 带到 {ModeName}");
            }
            IssueTemp();
            _owner.Toast($"已切到{ModeName}，温控继续" + (carry is { } cc ? $"，目标 {Txt.Fx(cc)} ℃" : ""));
        }
        else if (stopFirst)
        {
            _owner.Toast($"温控已停，已切到{ModeName}——填好目标再打开「温控」");
        }
        RaiseZone();
    }

    public void SetRampBy(string v)
    {
        if (RampBy == v) return;
        RampBy = v;
        Log("模式", v switch { "fast" => "到达方式：尽快（不限速）", "dur" => "到达方式：按时长", _ => "到达方式：按速率" });
        // 温控开着就按新方式重新下发：切到「尽快」的那一刻斜率就该撤掉，不是等下次下发。
        // 只换斜率不换目标，不弹确认，但要说一句做了什么——控着的东西被动了不能没声
        if (TempOn)
        {
            IssueTemp();
            _owner.Toast(v switch
            {
                "fast" => "到达方式改为尽快：斜率已撤，目标不变，温控继续",
                "dur" => $"到达方式改为按时长 {Txt.Fx(Pv("dur"))} min：已按新斜率重新下发，目标不变",
                _ => $"到达方式改为按速率 {Txt.Fx(Math.Abs(Pv("rate")))} ℃/min：已重新下发，目标不变"
            });
        }
        RaiseZone();
    }

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
        // 发出去的就是要记下的：温控开关 / 换模式 / 换到达方式这几条路都从这里下发，用的是
        // 「待下发优先」的 Pv()。发了却还留在待下发里，格子亮着琥珀色、釜上的牌子写着旧数，
        // 温控器已经在追新数（用户踩到）——温度这几项一并落成设定值
        var moved = new List<string>();
        foreach (var k in new[] { "tr", "tj", "rate", "dur", "dt" })
            if (Pending.Remove(k, out var v)) { Sets[k] = v; moved.Add($"{k} = {Txt.Fx(v)}"); }
        if (moved.Count > 0)
        {
            Log("下发", $"随温控一并下发：{string.Join("，", moved)}");
            _owner.Toast($"待下发的 {string.Join("，", moved)} 已随温控一并下发");
        }
        if (Mode == "TrTj")
        {
            IssueReflux(t);
            return;
        }
        var kind = Mode == "Tj" ? TempChannelKind.Jacket : TempChannelKind.Reactor;
        var target = Pv(Mode == "Tj" ? "tj" : "tr");
        var cur = Mode == "Tj" ? t.CurrentJacket : t.CurrentReactor;
        // 「尽快」= 普通模式的升温（用户要的）：不按速率不按时长，目标一步写给温控器，
        // 它按自己的最大能力走（RD105：SPEED=0 不限斜率）——跟配方里「到达方式 = 尽快」同一个意思
        Task issue;
        if (RampBy == "fast")
        {
            issue = t.SetTargetAsync(new TempTarget(target, kind), CancellationToken.None);
        }
        else
        {
            var rate = RampBy == "rate"
                ? Math.Abs(Pv("rate"))
                : Math.Abs(target - cur) / Math.Max(0.1, Pv("dur"));
            rate = Math.Clamp(rate, 0.05, Math.Max(0.05, t.Limits.MaxRatePerMin));
            issue = t.RampAsync(target, rate, kind, CancellationToken.None);
        }
        // 下发可能被设备拒绝（「TEC 加热」没启用又没有电加热通路时的升温目标、
        // 超出保护范围的目标……）。**任务不能丢**：丢了的话开关看着是开的、
        // 实际一个字都没写进控制器，跟蒸回流那条路一样把它接住
        issue.ContinueWith(task =>
        {
            if (task.Exception?.GetBaseException() is not { } ex) return;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                TempOn = false;
                Log("拒绝", ex.Message);
                _owner.Toast(ex.Message);
                RaiseZone();
            });
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// 手动开蒸回流：ΔT 取面板设定，夹套上限取设备自己的保护上限（不另编一个数）。
    /// 开不了（真机上 Tr 探头没接/断线会拒绝）就把温控开关弹回去并说清原因——
    /// 不留一个看着开了实际没跟的开关。
    /// </summary>
    private void IssueReflux(ITemperatureControl t)
    {
        if (Reflux is not { } r) return;
        var dt = Math.Clamp(Pv("dt"), 0.5, 30);
        var cap = t.Limits.Max;
        r.StartAsync(dt, cap, CancellationToken.None).ContinueWith(task =>
        {
            if (task.Exception?.GetBaseException() is not { } ex) return;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                TempOn = false;
                Log("拒绝", ex.Message);
                _owner.Toast(ex.Message);
                RaiseZone();
            });
        }, TaskScheduler.Default);
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
        if (rEnd > 0 && rDur > 0 && s is IStirrerRamp impl)
        {
            // 斜坡：终值 + 时长换算成驱动的加减速斜率（rampSeconds 按满量程标）
            var delta = Math.Abs(rEnd - s.CurrentRpm);
            if (delta > 1) impl.SetRampSeconds(rDur * 60 * s.Limits.Max / delta);
            _ = s.SetSpeedAsync(rEnd, CancellationToken.None);
        }
        else
        {
            if (s is IStirrerRamp i2) i2.SetRampSeconds(5);
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
        if (!HasStir)
        {
            // 没有搅拌接口的机器不许出现「转速已写入控制器」这种假成功
            foreach (var k in new[] { "rpm", "rEnd", "rDur" }) Pending.Remove(k);
            if (Pending.Count == 0)
            {
                _owner.Toast("这台主机没有搅拌接口，转速设定没有去处");
                RaiseZone();
                return;
            }
        }
        var n = Pending.Count;
        // 「下发」只把设定值交给开着的那个回路：温控开关关着，温度目标就只是记在面板上，
        // 一个字都没写进温控器——从前这时候也报「已写入控制器」，现场对着 70 ℃ 等了半天没动静（用户踩到）
        var tempPending = Pending.Keys.Any(k => k is "tr" or "tj" or "rate" or "dur" or "dt");
        var stirPending = Pending.Keys.Any(k => k is "rpm" or "rEnd" or "rDur");
        foreach (var kv in Pending) Sets[kv.Key] = kv.Value;
        Pending.Clear();
        if (TempOn) IssueTemp();
        if (StirOn) IssueStir();

        var held = new List<string>();
        if (tempPending && !TempOn) held.Add("「温控」开关是关的，温度目标没有下发到温控器——把右上角「温控」打开才会写进去");
        if (stirPending && !StirOn && HasStir) held.Add("「搅拌」开关是关的，转速没有下发");
        if (held.Count == 0)
        {
            Log("下发", $"{n} 项设定值已写入控制器");
            _owner.Toast($"已下发 {n} 项设定值");
        }
        else
        {
            var msg = $"已记下 {n} 项设定值；{string.Join("；", held)}";
            Log("下发", msg);
            _owner.Toast(msg);
        }
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
        // 上位机 PID 下夹套回路在追的设定（串级 = 外环算出来的内环设定，单环 = 夹套目标）；温控器 PID 下没有这条
        ("tjset", "Tj 设定（内环）", "℃", "#E0873A"),
        ("dt", "Tr−Tj 内外温差", "K", "#8E8E8E"),
        ("ph", "pH", "", "#6F6F6F"),
        ("rpm", "R 转速", "rpm", "#C9C9C9"),
    };

    private readonly HashSet<string> _trends = new(StringComparer.Ordinal) { "tr", "tj", "tjset", "dt" };
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

    // 没有搅拌能力就不列「R 转速」这张卡——图例摆一个恒 0 的数，读起来像读数；
    // 「Tj 设定（内环）」只在上位机 PID 下有（温控器 PID 不发这一路），不然也不列
    private IEnumerable<(string Key, string Name, string Unit, string Color)> TrendDefsHere
        => TrendDefs.Where(d => (d.Key != "rpm" || HasStir) && (d.Key != "tjset" || HostLoop || Snap("Tjset").Length > 0));

    public IReadOnlyList<TrendRow> TrendRows => TrendDefsHere.Select(d => new TrendRow(
        d.Key, d.Name, d.Unit, ColorOf(d.Key),
        d.Key switch
        {
            "tr" => F1(TrVal),
            "tj" => F1(TjVal),
            "tjset" => F1(TempOn || EngineRunning ? Tag("Tjset") : null),
            "dt" => TrVal is { } a && TjVal is { } b ? Sg(a - b) : "—",
            "ph" => PhText,
            _ => RpmVal.ToString("0"),
        },
        _trends.Contains(d.Key))).ToList();

    public string ColorOf(string key)
        => _traceColor.TryGetValue(key, out var c) ? c : TrendDefs.First(d => d.Key == key).Color;

    public IReadOnlyList<string> EnabledTrends => TrendDefsHere.Select(d => d.Key)
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
        "tjset" => Snap("Tjset").Select(s => (s.WallClock, s.Value)).ToArray(),
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

    // ── 任务序列页（0256 重做：面板自己的简易序列，跟工作站配方两码事）──
    //
    // 工作站的配方是协调多台设备的长流程；面板序列照交互原型：**这台设备
    // 自己的 6 步小序列**（Tr / Tj / 保温 / 搅拌 / 加料），在屏上点出来、
    // 屏上启动，存在设备本机，不碰配方泳道。执行走同一台执行引擎——
    // 启动时把 6 步翻译成指令步骤（含换挡的搅拌插步），校验、安全层、
    // GLP 记录全是真的；显示的时长与温度轨迹来自翻译件的排期。

    /// <summary>面板序列的一步。字段照原型 defFields：类型 + 目标 + 到达方式 + 值 + 该步搅拌。</summary>
    public sealed class HmiSeqStep
    {
        public string Type { get; set; } = "Tr";   // Tr / Tj / TrTj / Wait / R / Dose
        public double Tgt { get; set; } = 40;      // ℃ / rpm / mL（TrTj 存 ΔT，Wait 不用）
        public string Mode { get; set; } = "rate"; // 温度: rate|time；R: now|time；Dose: rate|once
        public double Val { get; set; } = 0.5;     // 速率，或分钟（Wait 的保持、TrTj 的回流时长也在这）
        public double Rpm { get; set; } = 200;     // 该步搅拌转速
    }

    internal bool WantSeq { get; set; }
    internal bool WantTl { get; set; }

    private const int MaxSeqSteps = 6;
    private readonly List<HmiSeqStep> _seq = new();
    private List<List<string>>? _startedMap;     // 启动那一刻：面板步 → 翻译出的 StepId 们
    private string? _startedRecipeId;

    public HmiSeqModel? SeqModel { get; private set; }
    public bool SeqEmpty => _seq.Count == 0;
    public string SeqSub { get; private set; } = "";
    /// <summary>工作站正在此通道跑配方——面板序列锁定，只留「结束序列」。</summary>
    public bool SeqForeign { get; private set; }
    public bool SeqLocked { get; private set; }

    public sealed record SeqStepBtn(int Index, string No, string Ty, string Pm,
                                    bool Empty, bool Done, bool Act, bool Err);
    public IReadOnlyList<SeqStepBtn> SeqSteps { get; private set; } = Array.Empty<SeqStepBtn>();

    public bool CanStartSeq { get; private set; }
    public string StartSeqText { get; private set; } = "启动序列";

    // 方案总览（ov-seq）：跑着的显示机器真在跑的时序（不论谁启动的），
    // 没跑的显示面板自己的序列草稿
    public HmiTlModel? TlModel { get; private set; }
    public string TlHead { get; private set; } = "";
    public string TlSub { get; private set; } = "";
    public string TlTag { get; private set; } = "手动模式";
    public string TlTagKind { get; private set; } = "none";   // live / draft / none
    public bool TlLive => TlTagKind == "live";
    public bool TlDraft => TlTagKind == "draft";
    public bool TlEmpty => TlModel is null;

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

    // ── 面板序列：存取（设备本机，跨开机还在）───────────────────────

    private string SeqPath => System.IO.Path.Combine(
        _ws.DataDir, "HmiPanel", $"seq-CH{Number}.json");

    private void LoadSeq()
    {
        try
        {
            if (!System.IO.File.Exists(SeqPath)) return;
            var got = Tec.Core.Persistence.TecJson.Read<List<HmiSeqStep>>(
                System.IO.File.ReadAllText(SeqPath));
            _seq.Clear();
            _seq.AddRange(got.Take(MaxSeqSteps));
        }
        catch { /* 读不动就从空开始，别把面板卡死在坏文件上 */ }
    }

    private void SaveSeq()
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SeqPath)!);
            System.IO.File.WriteAllText(SeqPath, Tec.Core.Persistence.TecJson.Write(_seq));
        }
        catch { /* 存不上不拦操作，下次改动再试 */ }
    }

    // ── 面板序列 → 引擎指令的翻译 ───────────────────────────────────

    private static readonly Dictionary<string, string> SeqTypeNames = new(StringComparer.Ordinal)
    { ["Tr"] = "Tr", ["Tj"] = "Tj", ["TrTj"] = "Tr−Tj", ["Wait"] = "Wait", ["R"] = "R", ["Dose"] = "加料" };

    /// <summary>
    /// 翻译成执行引擎的步骤。每一步带着自己的搅拌转速（原型就是这么设计的）——
    /// 转速跟上一步不同就先插一条「搅拌 · 立即」换挡；R 步本身就是搅拌，不插。
    /// 返回配方 + 「面板步 → 翻译出的 StepId 们」的映射（状态染色靠它）。
    /// </summary>
    private (Recipe Rec, List<List<string>> Map) TranslateSeq()
    {
        var rec = new Recipe { Name = $"面板序列 CH{Number}" };
        var map = new List<List<string>>();
        var rpmPrev = Stir?.CurrentRpm ?? 0;
        foreach (var s in _seq)
        {
            var ids = new List<string>();
            // 没有搅拌接口的机器不插换挡步——插了整条序列会被校验器拦在门口，
            // 报的还是翻译件的步号，操作人对不上自己那 6 张卡
            if (HasStir && s.Type != "R" && Math.Abs(s.Rpm - rpmPrev) > 0.5)
            {
                var stir = new Step
                {
                    CommandId = Tec.Driver.Abi.CommandSpecs.Stir,
                    Parameters = ParameterSet.Of(("rpm", s.Rpm), ("task", "立即"))
                };
                rec.Steps.Add(stir);
                ids.Add(stir.StepId);
                rpmPrev = s.Rpm;
            }
            // 只带所选到达方式用得上的那个参数——校验器对写进去的每个数都查
            // 范围，「按时长 30 min」若同时写 rate=30 会按 30 ℃/min 被拦下
            var main = s.Type switch
            {
                "Tr" or "Tj" => new Step
                {
                    CommandId = Tec.Driver.Abi.CommandSpecs.Control,
                    Parameters = s.Mode == "time"
                        ? ParameterSet.Of(
                            ("obj", s.Type == "Tj" ? "夹套 Tj" : "釜内 Tr"),
                            ("target", s.Tgt), ("task", "按时长"), ("dur", s.Val))
                        : ParameterSet.Of(
                            ("obj", s.Type == "Tj" ? "夹套 Tj" : "釜内 Tr"),
                            ("target", s.Tgt), ("task", "按速率"), ("rate", s.Val))
                },
                // 蒸回流：ΔT 是卡上的 Tgt，夹套上限取设备自己的保护上限——
                // 不在面板另编一个数，设备超温寄存器反正也在兜底
                "TrTj" => new Step
                {
                    CommandId = Tec.Driver.Abi.CommandSpecs.Reflux,
                    Parameters = ParameterSet.Of(("dt", s.Tgt),
                        ("tjmax", Temp?.Limits.Max ?? 180), ("dur", s.Val))
                },
                "Wait" => new Step
                {
                    CommandId = Tec.Driver.Abi.CommandSpecs.Hold,
                    Parameters = ParameterSet.Of(("dur", s.Val))
                },
                "R" => new Step
                {
                    CommandId = Tec.Driver.Abi.CommandSpecs.Stir,
                    Parameters = s.Mode == "time"
                        ? ParameterSet.Of(("rpm", s.Tgt), ("task", "按时长"), ("ramp", s.Val * 60))
                        : ParameterSet.Of(("rpm", s.Tgt), ("task", "立即"))
                },
                _ => new Step
                {
                    CommandId = Tec.Driver.Abi.CommandSpecs.Dose,
                    Parameters = s.Mode == "once"
                        ? ParameterSet.Of(("vol", s.Tgt), ("task", "一次加入"),
                            ("liq", "面板加料"), ("sync", false))
                        : ParameterSet.Of(("vol", s.Tgt), ("task", "按速率"),
                            ("rate", s.Val), ("liq", "面板加料"), ("sync", false))
                },
            };
            if (s.Type == "R") rpmPrev = s.Tgt;
            rec.Steps.Add(main);
            ids.Add(main.StepId);
            map.Add(ids);
        }
        return (rec, map);
    }

    // ── 状态染色（一张面板卡可能对应多条引擎步骤）────────────────────

    private (string State, string Text, double Frac) PanelStateOf(
        ChannelRun? run, IReadOnlyList<string> ids)
    {
        if (run is null || ids.Count == 0) return ("idle", "", 0);
        var live = run.State is ChannelRunState.Running or ChannelRunState.Paused;
        var done = 0; StepRecord? act = null; var err = false; var seen = 0;
        foreach (var id in ids)
        {
            StepRecord? sr = null;
            foreach (var s in run.Steps) if (s.StepId == id) sr = s;   // 循环取最后一轮
            if (sr is null) continue;
            seen++;
            if (sr.Status is StepStatus.Done or StepStatus.Skipped) done++;
            else if (sr.Status == StepStatus.Running) act = sr;
            else if (sr.Status is StepStatus.Failed or StepStatus.Aborted) err = true;
        }
        if (err) return ("err", "⚠ 出错 · 已中止", 0);
        if (act is not null)
        {
            var inner = act.PlanDuration > TimeSpan.Zero && act.ActualStart is { } a
                ? Math.Clamp((_ws.Clock.Now - a) / act.PlanDuration, 0, 1) : 0;
            var frac = (done + inner) / ids.Count;
            return ("act", $"进行中 {frac * 100:0}%", frac);
        }
        if (seen > 0 && done == ids.Count) return ("done", "✓ 已完成", 0);
        return live ? ("pend", "待执行", 0) : ("idle", "", 0);
    }

    // ── 页面重建 ────────────────────────────────────────────────────

    private static string F0(double v) => Txt.Fx(v).Replace('-', '−');

    internal void RefreshSeq()
    {
        var runner = _ws.Engine.Runner(Number);
        var live = runner?.State is ChannelRunState.Running or ChannelRunState.Paused;
        var run = _ws.Engine.Record.Of(Number);
        var mine = run is not null && _startedRecipeId is not null
                   && run.Baseline.Recipe.Id == _startedRecipeId ? run : null;
        SeqForeign = live && mine is null;
        SeqLocked = live;
        CanStartSeq = (runner is { CanResume: true } && mine is not null)
                      || (!live && _seq.Count > 0 && runner is { CanStart: true });
        StartSeqText = runner is { CanResume: true } && mine is not null ? "继续序列" : "启动序列";

        Recipe rec; List<List<string>> map; Schedule sched;
        if (mine is not null && live && _startedMap is not null)
        {
            // 跑着的读启动那一刻冻结的基线（GLP §7.2），不重新翻译
            rec = mine.Baseline.Recipe;
            sched = mine.Baseline.Schedule;
            map = _startedMap;
        }
        else
        {
            (rec, map) = TranslateSeq();
            sched = Schedule.Build(rec, _ws.Engine.Catalog, SeedNow());
        }

        var byId = new Dictionary<string, ScheduleEntry>(StringComparer.Ordinal);
        foreach (var e in sched.Entries) byId[e.StepId] = e;

        var cards = new List<HmiSeqCard>();
        var btns = new List<SeqStepBtn>();
        for (var i = 0; i < _seq.Count; i++)
        {
            var s = _seq[i];
            var ids = i < map.Count ? map[i] : new List<string>();
            var (st, text, frac) = PanelStateOf(mine, ids);

            var dur = TimeSpan.Zero;
            double t0 = 25, t1 = 25;
            var first = true;
            foreach (var id in ids)
                if (byId.TryGetValue(id, out var e))
                {
                    dur += e.Extent;
                    if (first) { t0 = e.StartTemp; first = false; }
                    t1 = e.EndTemp;
                }

            var rows = new List<HmiSeqRow>();
            switch (s.Type)
            {
                case "Tr":
                case "Tj":
                    rows.Add(new HmiSeqRow($"目标 {s.Type}", $"{F0(s.Tgt)} ℃", "tgt"));
                    rows.Add(new HmiSeqRow("到达方式", s.Mode == "time" ? "按时长" : "按速率", "mode"));
                    rows.Add(s.Mode == "time"
                        ? new HmiSeqRow("用时", $"{F0(s.Val)} min", "val")
                        : new HmiSeqRow("速率", $"{F0(s.Val)} ℃/min", "val"));
                    rows.Add(new HmiSeqRow("步时长", Fmt.Hms(dur)));
                    rows.Add(HasStir ? new HmiSeqRow("搅拌", $"{s.Rpm:0} rpm", "rpm")
                                     : new HmiSeqRow("搅拌", "—（无搅拌）"));
                    break;
                case "TrTj":
                    rows.Add(new HmiSeqRow("ΔT（Tj−Tr）", $"{F0(s.Tgt)} K", "tgt"));
                    rows.Add(new HmiSeqRow("回流时长", $"{F0(s.Val)} min", "val"));
                    rows.Add(new HmiSeqRow("步时长", Fmt.Hms(dur)));
                    rows.Add(HasStir ? new HmiSeqRow("搅拌", $"{s.Rpm:0} rpm", "rpm")
                                     : new HmiSeqRow("搅拌", "—（无搅拌）"));
                    break;
                case "Wait":
                    rows.Add(new HmiSeqRow("保持温度", $"{F0(t0)} ℃"));
                    rows.Add(new HmiSeqRow("保持时长", $"{F0(s.Val)} min", "val"));
                    rows.Add(new HmiSeqRow("步时长", Fmt.Hms(dur)));
                    rows.Add(HasStir ? new HmiSeqRow("搅拌", $"{s.Rpm:0} rpm", "rpm")
                                     : new HmiSeqRow("搅拌", "—（无搅拌）"));
                    break;
                case "R":
                    rows.Add(new HmiSeqRow("目标转速", $"{s.Tgt:0} rpm", "tgt"));
                    rows.Add(new HmiSeqRow("到达方式", s.Mode == "time" ? "按时长" : "立即", "mode"));
                    if (s.Mode == "time") rows.Add(new HmiSeqRow("用时", $"{F0(s.Val)} min", "val"));
                    rows.Add(new HmiSeqRow("步时长", Fmt.Hms(dur)));
                    break;
                default:   // Dose
                    rows.Add(new HmiSeqRow("加料总量", $"{F0(s.Tgt)} mL", "tgt"));
                    rows.Add(new HmiSeqRow("加入方式", s.Mode == "once" ? "一次加入" : "按速率", "mode"));
                    if (s.Mode != "once") rows.Add(new HmiSeqRow("速率", $"{F0(s.Val)} mL/min", "val"));
                    rows.Add(new HmiSeqRow("步时长", Fmt.Hms(dur)));
                    rows.Add(HasStir ? new HmiSeqRow("搅拌", $"{s.Rpm:0} rpm", "rpm")
                                     : new HmiSeqRow("搅拌", "—（无搅拌）"));
                    break;
            }

            // 原型的叫法：卡片表头「加料」用中文，步骤条用代号 Dose
            var ty = SeqTypeNames.TryGetValue(s.Type, out var tn) ? tn : s.Type;
            cards.Add(new HmiSeqCard
            {
                No = i + 1, Type = ty, State = st, StateText = text,
                Rows = rows, T0 = t0, T1 = t1, Frac = frac,
            });
            btns.Add(new SeqStepBtn(i, $"步骤 {i + 1}", s.Type,
                rows.Count > 0 ? rows[0].V : "", false,
                st == "done", st == "act", st == "err"));
        }
        // 空位补到 6 格（原型 ＋ 点击添加）
        for (var i = _seq.Count; i < MaxSeqSteps; i++)
            btns.Add(new SeqStepBtn(i, $"步骤 {i + 1}", "＋", "点击添加", true, false, false, false));

        SeqModel = cards.Count > 0 ? new HmiSeqModel { Cards = cards, Editable = !SeqLocked } : null;
        SeqSteps = btns;
        SeqSub = SeqForeign
            ? "通道正被工作站程序控制——面板序列已锁定，结束后可编辑"
            : _seq.Count == 0
                ? "最多 6 步 · 可用操作 Tr / Tj / Tr−Tj / R / Wait / 加料 · 点下方空位添加"
                : $"{_seq.Count}/{MaxSeqSteps} 步 · 预计总时长 {Fmt.Hms(sched.Total)}";
        RaiseSeq();
    }

    private void RaiseSeq() => RaiseAll(nameof(SeqModel), nameof(SeqEmpty), nameof(SeqSub),
        nameof(SeqSteps), nameof(CanStartSeq), nameof(StartSeqText),
        nameof(SeqForeign), nameof(SeqLocked));

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
        // 跑着的显示机器真在跑的时序（不论从哪儿启动的——那是设备的现实）；
        // 没跑的显示面板序列草稿
        var runner = _ws.Engine.Runner(Number);
        var live = runner?.Run is { } lr &&
                   runner.State is ChannelRunState.Running or ChannelRunState.Paused;
        Schedule sched; Recipe rec; ChannelRun? run = null; string name;
        if (live)
        {
            run = runner!.Run!;
            sched = run.Baseline.Schedule;
            rec = run.Baseline.Recipe;
            name = rec.Name;
        }
        else if (_seq.Count > 0)
        {
            var (trec, _) = TranslateSeq();
            rec = trec;
            sched = Schedule.Build(rec, _ws.Engine.Catalog, SeedNow());
            name = "面板序列";
        }
        else
        {
            TlModel = null; TlSub = ""; TlHead = $"通道 {Index}";
            TlTag = "手动模式"; TlTagKind = "none";
            RaiseTl();
            return;
        }
        TlHead = $"通道 {Index} · {name}";

        var rows = new List<(ScheduleEntry E, double Dur, string Rpm)>();
        var rpm = SeedNow().Rpm;
        foreach (var e in sched.Entries)
        {
            var step = rec.Steps.FirstOrDefault(s => s.StepId == e.StepId);
            if (step is null || !step.Enabled || e.Extent <= TimeSpan.Zero) continue;
            if (step.Parameters.Has("rpm")) rpm = step.Parameters.Num("rpm", rpm);
            rows.Add((e, e.Extent.TotalSeconds, $"{rpm:0} rpm"));
        }
        if (rows.Count == 0)
        {
            TlModel = null; TlSub = ""; TlTag = "手动模式"; TlTagKind = "none";
            RaiseTl();
            return;
        }

        var ds = rows.Select(r => r.Dur).ToArray();
        var lay = SegLay(ds);
        var tot = ds.Sum();
        var doneIdx = -1;
        if (run is not null)
            for (var i = 0; i < rows.Count; i++)
            {
                StepRecord? sr = null;
                foreach (var s in run.Steps) if (s.StepId == rows[i].E.StepId) sr = s;
                if (sr?.Status is StepStatus.Done or StepStatus.Skipped) doneIdx = i;
            }

        HmiTlSeg Seg(int i, string text) => new(lay[i].W, lay[i].Zip, run is not null && i <= doneIdx,
            lay[i].Zip ? "" : text);
        var tempRow = rows.Select((r, i) =>
            Seg(i, $"{(r.E.EndTemp < 0 ? "−" : "")}{Math.Abs(r.E.EndTemp):0} ℃")).ToList();
        // 无搅拌的机器不画转速行——画一排 200 rpm 的段，看起来像真要执行
        var stirRow = HasStir ? rows.Select((r, i) => Seg(i, r.Rpm)).ToList() : new();

        var ticks = new List<(double, string)>();
        for (double t = 0, ss = TickStep(tot); t <= tot; t += ss)
            ticks.Add((SegX(lay, ds, t), TickLabel(t)));

        double? cursor = null;
        if (run is not null)
            cursor = SegX(lay, ds, Math.Min((_ws.Clock.Now - run.StartedAt).TotalSeconds, tot));

        var nz = lay.Count(l => l.Zip);
        TlModel = new HmiTlModel
        { TempRow = tempRow, StirRow = stirRow, Ticks = ticks, CursorX = cursor };
        TlSub = $"{rows.Count} 步 · 总时长 {Fmt.Hms(TimeSpan.FromSeconds(tot))}"
                + (nz > 0 ? $" · {nz} 个短步骤已压缩" : "");
        if (run is not null)
        {
            TlTag = $"步骤 {Math.Min(doneIdx + 2, rows.Count)}/{rows.Count}"; TlTagKind = "live";
        }
        else { TlTag = "未启动"; TlTagKind = "draft"; }
        RaiseTl();
    }

    private void RaiseTl() => RaiseAll(nameof(TlModel), nameof(TlEmpty), nameof(TlHead),
        nameof(TlSub), nameof(TlTag), nameof(TlTagKind), nameof(TlLive), nameof(TlDraft));

    // ── 面板序列：编辑 ──────────────────────────────────────────────

    private bool GuardSeqLocked()
    {
        if (!SeqLocked) return false;
        _owner.Toast(SeqForeign ? "通道正被工作站程序控制——面板序列已锁定"
                                : "已开始的序列不可编辑——结束后再改");
        return true;
    }

    /// <summary>步骤条 / 卡片表头点进来的：空位加一步，已有的换类型。</summary>
    public void SeqSlotTapped(int index)
    {
        if (GuardSeqLocked()) return;
        if (index >= MaxSeqSteps) return;
        _owner.OpenTyPicker(this, index);
    }

    /// <summary>类型弹窗描当前类型用：这一格现在是什么（空位是 null）。</summary>
    internal string? SeqTypeAt(int index) => index < _seq.Count ? _seq[index].Type : null;

    /// <summary>类型定了（类型弹窗回调）。已有的换类型保留该步转速，其余回缺省。</summary>
    internal void SetSeqStep(int index, string type)
    {
        if (GuardSeqLocked()) return;
        // 点了它本来的类型 = 没改主意，参数原样留着（重置了才叫吓人）
        if (index < _seq.Count && _seq[index].Type == type) return;
        var prevRpm = index > 0 && index - 1 < _seq.Count ? _seq[index - 1].Rpm
                    : _seq.Count > 0 ? _seq[^1].Rpm : Math.Max(200, Stir?.CurrentRpm ?? 200);
        var keep = index < _seq.Count ? _seq[index].Rpm : prevRpm;
        var s = type switch
        {
            "Tr" => new HmiSeqStep { Type = "Tr", Tgt = 40, Mode = "rate", Val = 0.5, Rpm = keep },
            "Tj" => new HmiSeqStep { Type = "Tj", Tgt = 40, Mode = "rate", Val = 0.5, Rpm = keep },
            // TrTj：Tgt 存 ΔT（缺省与指令声明一致 5 K），Val 存回流时长
            "TrTj" => new HmiSeqStep { Type = "TrTj", Tgt = 5, Mode = "rate", Val = 30, Rpm = keep },
            "Wait" => new HmiSeqStep { Type = "Wait", Val = 30, Rpm = keep },
            "R" => new HmiSeqStep { Type = "R", Tgt = 300, Mode = "now", Val = 1, Rpm = keep },
            _ => new HmiSeqStep { Type = "Dose", Tgt = 10, Mode = "rate", Val = 1.2, Rpm = keep },
        };
        if (index < _seq.Count) _seq[index] = s;
        else _seq.Add(s);
        Log("序列", $"第 {Math.Min(index, _seq.Count - 1) + 1} 步设为 {SeqTypeNames[s.Type]}");
        SaveSeq();
        RefreshSeq();
    }

    /// <summary>卡片上点了哪一格（EditKey）。数值走同一张数字键盘，到达方式点着换挡。</summary>
    public void SeqCardTapped(int index, string key)
    {
        if (index < 0 || index >= _seq.Count) return;
        if (key == "head") { SeqSlotTapped(index); return; }
        if (GuardSeqLocked()) return;
        var s = _seq[index];
        switch (key)
        {
            case "mode":
                s.Mode = s.Type switch
                {
                    "Tr" or "Tj" => s.Mode == "rate" ? "time" : "rate",
                    "R" => s.Mode == "now" ? "time" : "now",
                    "Dose" => s.Mode == "rate" ? "once" : "rate",
                    _ => s.Mode
                };
                // 换了到达方式，值也换单位——回到该方式的缺省，别让 0.5 ℃/min 变 0.5 min
                s.Val = s.Type switch
                {
                    "Tr" or "Tj" => s.Mode == "time" ? 30 : 0.5,
                    "R" => 1,
                    "Dose" => 1.2,
                    _ => s.Val
                };
                SaveSeq(); RefreshSeq();
                break;
            case "tgt":
                switch (s.Type)
                {
                    case "Tr" or "Tj":
                        var (lo, hi) = RangeOf("tr");
                        _owner.OpenKeypadCustom($"目标 {s.Type}", "℃", lo, hi,
                            v => { s.Tgt = v; SaveSeq(); RefreshSeq(); });
                        break;
                    case "TrTj":
                        _owner.OpenKeypadCustom("蒸回流 ΔT（Tj−Tr）", "K", 1, 30,
                            v => { s.Tgt = v; SaveSeq(); RefreshSeq(); });
                        break;
                    case "R":
                        var (rl, rh) = RangeOf("rpm");
                        _owner.OpenKeypadCustom("目标转速", "rpm", rl, rh,
                            v => { s.Tgt = v; SaveSeq(); RefreshSeq(); });
                        break;
                    case "Dose":
                        var maxV = Dose?.Limits.MaxVolume ?? 500;
                        _owner.OpenKeypadCustom("加料总量", "mL", 0.1, maxV,
                            v => { s.Tgt = v; SaveSeq(); RefreshSeq(); });
                        break;
                }
                break;
            case "val":
                switch (s.Type)
                {
                    case "Tr" or "Tj" when s.Mode == "rate":
                        var (_, rateHi) = RangeOf("rate");
                        _owner.OpenKeypadCustom("变温速率", "℃/min", 0.05, rateHi,
                            v => { s.Val = v; SaveSeq(); RefreshSeq(); });
                        break;
                    case "Tr" or "Tj":
                        _owner.OpenKeypadCustom("变温用时", "min", 0.1, 999,
                            v => { s.Val = v; SaveSeq(); RefreshSeq(); });
                        break;
                    case "TrTj":
                        _owner.OpenKeypadCustom("回流时长", "min", 1, 999,
                            v => { s.Val = v; SaveSeq(); RefreshSeq(); });
                        break;
                    case "Wait":
                        _owner.OpenKeypadCustom("保持时长", "min", 1, 999,
                            v => { s.Val = v; SaveSeq(); RefreshSeq(); });
                        break;
                    case "R":
                        _owner.OpenKeypadCustom("到达用时", "min", 0.1, 60,
                            v => { s.Val = v; SaveSeq(); RefreshSeq(); });
                        break;
                    case "Dose":
                        var rHi = Dose?.Limits.Max ?? 50;
                        _owner.OpenKeypadCustom("加料速率", "mL/min", 0.01, rHi,
                            v => { s.Val = v; SaveSeq(); RefreshSeq(); });
                        break;
                }
                break;
            case "rpm":
                if (!HasStir) { _owner.Toast("这台主机没有搅拌接口——搅拌协议未知"); break; }
                var (sl, sh) = RangeOf("rpm");
                _owner.OpenKeypadCustom("该步搅拌转速", "rpm", sl, sh,
                    v => { s.Rpm = v; SaveSeq(); RefreshSeq(); });
                break;
        }
    }

    /// <summary>清除全部步骤（新建序列走同一条：从空白开始）。</summary>
    public void ClearSeq()
    {
        if (GuardSeqLocked()) return;
        if (_seq.Count == 0) { _owner.Toast("序列本来就是空的"); return; }
        var n = _seq.Count;
        _seq.Clear();
        _startedRecipeId = null;
        _startedMap = null;
        SaveSeq();
        Log("序列", $"清除全部步骤（原 {n} 步）");
        _owner.Toast("已清除面板序列");
        RefreshSeq();
    }

    /// <summary>配方库存取用的草稿快照（深拷贝——库和草稿各自过日子）。</summary>
    internal List<HmiSeqStep> SeqSnapshot()
        => _seq.Select(s => new HmiSeqStep
        { Type = s.Type, Tgt = s.Tgt, Mode = s.Mode, Val = s.Val, Rpm = s.Rpm }).ToList();

    /// <summary>配方库「→ 通道 N」：整份替换 6 步草稿。序列锁定时拒绝并照实提示。</summary>
    internal bool AdoptSeq(IReadOnlyList<HmiSeqStep> steps)
    {
        if (GuardSeqLocked()) return false;
        _seq.Clear();
        _seq.AddRange(steps.Take(MaxSeqSteps).Select(s => new HmiSeqStep
        { Type = s.Type, Tgt = s.Tgt, Mode = s.Mode, Val = s.Val, Rpm = s.Rpm }));
        _startedRecipeId = null;
        _startedMap = null;
        SaveSeq();
        Log("序列", $"从配方库装入 {_seq.Count} 步");
        RefreshSeq();
        return true;
    }

    // ── 面板序列：启动 / 继续 ───────────────────────────────────────

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
        if (_seq.Count == 0) { _owner.Toast("先点下方空位编几步"); return; }
        if (runner is null) { _owner.Toast($"CH{Number} 不在台面上"); return; }
        if (!HasStir && _seq.FindIndex(s => s.Type == "R") is var k and >= 0)
        {
            // 在面板层用**卡片序号**说人话——校验器报的是翻译件步号，对不上这 6 张卡
            _owner.Toast($"序列第 {k + 1} 步是搅拌——这台主机没有搅拌接口，删掉它再启动");
            return;
        }
        if (!CanReflux && _seq.FindIndex(s => s.Type == "TrTj") is var k2 and >= 0)
        {
            // 老序列文件里可能存着别的设备编的 TrTj 步（选格子时已按能力拦，这里兜底）
            _owner.Toast($"序列第 {k2 + 1} 步是蒸回流——该设备没有夹套跟随能力，删掉它再启动");
            return;
        }
        var (rec, map) = TranslateSeq();
        _ws.BeginBatch();
        try
        {
            // 只启动翻译件，不碰工作站的配方泳道——面板序列跟配方是两码事
            _ws.Engine.StartChannel(Number, rec, _ws.Operator);
            _startedRecipeId = rec.Id;
            _startedMap = map;
            Log("序列", $"面板序列启动（{_seq.Count} 步）");
            _owner.Toast($"面板序列已启动（{_seq.Count} 步）");
        }
        catch (RecipeRejectedException ex)
        {
            // 校验器拦下的启动：拒绝也要留痕（谁按的、为什么没开）
            _owner.Toast(ex.Message);
            _ws.Log.Write("运行", ex.Message, _ws.Operator);
        }
        catch (Exception ex) { _owner.Toast($"启动失败：{ex.Message}"); }
        RefreshSeq();
        RaiseZone();
    }

    // ── 反应釜与安全页（0252）────────────────────────────────────────

    internal bool WantSaf { get; set; }

    public sealed record SafRow(string Name, string Val, string Desc, string EditKey)
    {
        public bool Editable => EditKey.Length > 0;
    }

    public IReadOnlyList<SafRow> SafRows { get; private set; } = Array.Empty<SafRow>();
    public string ReactorDesc { get; private set; } = "—";
    public string ReactorDetail { get; private set; } = "";

    private Tec.Core.Safety.SafetyLimit? BaseTr => _ws.Engine.Safety.Limits.FirstOrDefault(l =>
        l.FromDeviceLimits && l.Channel == Number && l.Tag == "Tr");
    private Tec.Core.Safety.SafetyLimit? OpTr => _ws.Engine.Safety.Limits.FirstOrDefault(l =>
        l.FromOperator && l.Channel == Number && l.Tag == "Tr");

    internal void RefreshSaf()
    {
        var dev = Ch is { } c ? _ws.Bench.Device(c.HostInstanceId) : null;
        ReactorDesc = dev is null ? "—"
            : $"{dev.Config.Str("釜规格", "100 mL")} {dev.Config.Str("釜材质", "玻璃")}釜";
        ReactorDetail = dev is null ? ""
            : $"{(HasStir ? $"搅拌桨 {dev.Config.Str("搅拌桨", "锚式")}" : "无搅拌接口")}"
              + $" · 探头 {dev.Config.Str("温度探头", "Pt100 四线")} · 规格在台面属性栏更换";

        static string N(double v) => Txt.Fx(v).Replace('-', '−');
        var rows = new List<SafRow>();
        var b = BaseTr;
        var op = OpTr;
        if (b is not null)
        {
            var act = Tec.Core.Safety.SafetyActionWords.Of(b.Action);
            double effMin = op?.Min ?? b.Min ?? 0,
                   effMax = op?.Max ?? b.Max ?? 0;
            double? effRate = op?.MaxRatePerMin ?? b.MaxRatePerMin;
            rows.Add(new SafRow("Tr min", $"{N(effMin)} ℃",
                $"低于此值 → {act} · 底线 {N(b.Min ?? 0)} ℃"
                + (op?.Min is { } && op.Min > b.Min ? " · 已收紧" : ""), "trmin"));
            rows.Add(new SafRow("Tr max", $"{N(effMax)} ℃",
                $"高于此值 → {act} · 底线 {N(b.Max ?? 0)} ℃"
                + (op?.Max is { } && op.Max < b.Max ? " · 已收紧" : ""), "trmax"));
            // 变化率缺省不检测（用户定的）：「尽快」模式下斜率有多快算多快。要盯就点这行填一个值，填 0 = 关
            rows.Add(new SafRow("变温速率上限", effRate is { } er ? $"{N(er)} ℃/min" : "关（不检测）",
                effRate is null
                    ? $"缺省不检测：「尽快」模式下斜率有多快算多快。要盯实测斜率就点这里填一个值（越限 → {act}），填 0 = 关"
                    : $"实测斜率越限 → {act} · "
                      + (b.MaxRatePerMin is { } br ? $"底线 {N(br)} ℃/min" : "底线不检测，这一条是操作人加的（填 0 = 关）")
                      + (op?.MaxRatePerMin is { } && (b.MaxRatePerMin is null || op.MaxRatePerMin < b.MaxRatePerMin) ? " · 已收紧" : ""),
                "trrate"));
        }
        foreach (var l in _ws.Engine.Safety.Limits.Where(l => l.Channel == Number))
        {
            if (ReferenceEquals(l, b) || ReferenceEquals(l, op)) continue;
            var layer = l.StepScope is not null ? "本步" : l.FromRecipe ? "配方"
                      : l.FromOperator ? "操作人" : "底线";
            var val = $"{(l.Min is { } lo ? N(lo) : "")}…{(l.Max is { } hi ? N(hi) : "")}"
                      + (l.MaxRatePerMin is { } r ? $" · ≤{N(r)}/min" : "");
            rows.Add(new SafRow($"{l.Tag}（{layer}）", val,
                $"{l.Note ?? ""} · 动作 {Tec.Core.Safety.SafetyActionWords.Of(l.Action)}".TrimStart('·', ' '),
                ""));
        }
        SafRows = rows;
        RaiseAll(nameof(SafRows), nameof(ReactorDesc), nameof(ReactorDetail));
    }

    /// <summary>点安全限值行改值：只有 Tr 那三行可改（操作人层，只能收紧）。</summary>
    public void EditSafRow(string key)
    {
        if (BaseTr is not { } b) return;
        if (EngineRunning) { _owner.Toast("序列运行中不可修改限值"); return; }
        var op = OpTr;
        double curMin = op?.Min ?? b.Min ?? 0,
               curMax = op?.Max ?? b.Max ?? 0;
        double? curRate = op?.MaxRatePerMin ?? b.MaxRatePerMin;
        switch (key)
        {
            case "trmin":
                _owner.OpenKeypadCustom("Tr min", "℃", b.Min ?? -99, curMax - 1,
                    v => ApplyOpLimit(v, curMax, curRate));
                break;
            case "trmax":
                _owner.OpenKeypadCustom("Tr max", "℃", curMin + 1, b.Max ?? 999,
                    v => ApplyOpLimit(curMin, v, curRate));
                break;
            case "trrate":
                // 缺省不检测；填 0 = 关。底线有斜率时只能往里收，没有时随便填
                _owner.OpenKeypadCustom("变温速率上限（0 = 关）", "℃/min", 0, b.MaxRatePerMin ?? 99,
                    v => ApplyOpLimit(curMin, curMax, v <= 0 ? null : v));
                break;
        }
    }

    private void ApplyOpLimit(double min, double max, double? rate)
    {
        var res = _ws.Engine.Safety.SetOperatorLimit(Number, "Tr", min, max, rate);
        static string N(double v) => Txt.Fx(v).Replace('-', '−');
        var msg = res is null ? "已回到设备底线"
            : $"Tr {N(res.Min ?? 0)}…{N(res.Max ?? 0)} ℃ · "
              + (res.MaxRatePerMin is { } rr ? $"≤{N(rr)} ℃/min" : "斜率不检测");
        Log("改限值", msg);
        _ws.Log.Write("安全", $"CH{Number} 面板改限值：{msg}", _ws.Operator);
        _owner.Toast("安全限值已更新：" + msg);
        RefreshSaf();
        RaiseZone();     // 控制页底部的限值条也跟着换
    }

    internal void OpenKeypad(string key) => _owner.OpenKeypad(this, key);

    private void RaiseZone() => RaiseAll(
        nameof(TrVal), nameof(TjVal), nameof(TrText), nameof(TrSetText), nameof(PhText),
        nameof(TjText), nameof(TjSetText),
        nameof(VesselRunning), nameof(RpmVal), nameof(TjBox), nameof(TjNote), nameof(TjHi),
        nameof(DtBox), nameof(DtNote), nameof(RpmBox), nameof(RpmOff), nameof(RpmNote),
        nameof(DoseBox), nameof(DoseOff), nameof(DoseNote), nameof(TcBox), nameof(TcNote),
        nameof(RateBox), nameof(RateOff), nameof(RateNote), nameof(HeadName), nameof(HeadRt),
        nameof(HeadCtl), nameof(HeadNote), nameof(HeadPct), nameof(EngineRunning),
        nameof(LinkText), nameof(LinkShort), nameof(LinkOk),
        nameof(Therm), nameof(ThermOn), nameof(ThermText),
        nameof(HeatState), nameof(HasHeat), nameof(HeatHot), nameof(HeatText),
        nameof(Mode), nameof(ModeName), nameof(ModeTr), nameof(ModeTj), nameof(ModeFollow),
        nameof(CanReflux), nameof(ByDur),
        nameof(TempOn), nameof(StirOn), nameof(TrOn), nameof(PhOn),
        nameof(RampBy), nameof(ByRate), nameof(ByFast), nameof(FastNote), nameof(TargetLabel), nameof(TargetUnit), nameof(RampValLabel),
        nameof(VbTr), nameof(VbRate), nameof(VbDur), nameof(VbRpm), nameof(VbREnd), nameof(VbRDur),
        nameof(NowLine1), nameof(NowLine2), nameof(StirNow), nameof(PendCount), nameof(LimChipList),
        nameof(LastLog), nameof(HasCommit), nameof(CommitText), nameof(TrPend), nameof(RatePend),
        nameof(DurPend), nameof(RpmPend), nameof(REndPend), nameof(RDurPend),
        nameof(RailRun), nameof(RailIcon), nameof(TargetKey));

    public bool HasCommit => Pending.Count > 0;
    public string CommitText => $"下发设定值（{Pending.Count} 项）";
    public bool TrPend => IsPending(TargetKey);
    public bool RatePend => IsPending("rate");
    public bool DurPend => IsPending("dur");
    public bool RpmPend => IsPending("rpm");
    public bool REndPend => IsPending("rEnd");
    public bool RDurPend => IsPending("rDur");
}
