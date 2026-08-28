using System.Collections.ObjectModel;
using Tec.App.Controls;
using Tec.App.Services;
using Tec.Core;
using Tec.Core.Data;
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
    // 页签的选中样式各绑各的——0249 那会儿后四页是占位钮，没绑 .on，
    // 于是「点了页换了、页签还蔫着」（用户指出）
    public bool ZGraTab => _zTab == "gra";
    public bool ZSeqTab => _zTab == "seq";
    public bool ZSafTab => _zTab == "saf";
    public bool ZExpTab => _zTab == "exp";

    public bool ShowOvApp => IsOv && OvApp;
    public bool ShowOvGra => IsOv && OvGra;
    public bool ShowOvSeq => IsOv && OvSeq;
    public bool ShowZCtl => IsZone && ZCtl;
    public bool ShowZGra => IsZone && _zTab == "gra";
    public bool ShowZSeq => IsZone && _zTab == "seq";
    public bool ShowZSaf => IsZone && _zTab == "saf";
    public bool ShowZExp => IsZone && _zTab == "exp";
    public bool ShowFiles => IsFiles;
    public bool ShowSys => IsSys;
    /// <summary>兜底：认不出的标签给一句实话（正常路径全部页都已接入）。</summary>
    public bool ZStub => IsZone && !(ZCtl || _zTab is "gra" or "seq" or "saf" or "exp");
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
                 nameof(ZGraTab), nameof(ZSeqTab), nameof(ZSafTab), nameof(ZExpTab),
                 nameof(ShowOvApp), nameof(ShowOvGra), nameof(ShowOvSeq), nameof(ShowZCtl),
                 nameof(ShowZGra), nameof(ShowZSeq), nameof(ShowZSaf), nameof(ShowZExp),
                 nameof(ShowFiles), nameof(ShowSys), nameof(ZStub), nameof(ShowStub));
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

    // ── 序列步骤类型弹窗（原型 tygrid）───────────────────────────────

    public sealed record TyRow(string Key, string Name, string Desc, bool Enabled);

    public bool TyOpen { get; private set; }
    public string TySub { get; private set; } = "";
    public IReadOnlyList<TyRow> TyRows { get; private set; } = Array.Empty<TyRow>();
    private HmiZoneViewModel? _tyZone;
    private int _tyIndex;

    internal void OpenTyPicker(HmiZoneViewModel z, int index)
    {
        _tyZone = z;
        _tyIndex = index;
        TySub = $"步骤 {index + 1} · 选一种操作";
        var hasPump = z.HasPump;
        TyRows = new[]
        {
            new TyRow("Tr", "控温 Tr", "釜内温度到目标（按速率 / 按时长）", true),
            new TyRow("Tj", "控温 Tj", "夹套温度到目标（直接控恒温器）", true),
            new TyRow("Wait", "保温", "保持当前温度一段时长", true),
            new TyRow("R", "搅拌", "转速到目标（立即 / 按时长）", true),
            new TyRow("Dose", "加料", hasPump ? "按速率或一次加入设定体积" : "这一路没接加料泵", hasPump),
        };
        TyOpen = true;
        RaiseAll(nameof(TyOpen), nameof(TySub), nameof(TyRows));
    }

    public void TyPick(string key)
    {
        TyOpen = false;
        Raise(nameof(TyOpen));
        _tyZone?.SetSeqStep(_tyIndex, key);
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
            var dir = System.IO.Path.Combine(ExperimentStore.DataDir, "Exports",
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

    // ── 文件页（原型 pageFiles：实验方案 = 配方库）────────────────────

    public sealed record FileRow(string Id, string Name, string ChTag, string State,
                                 bool Live, bool Loaded, string Detail, bool Sel);

    public IReadOnlyList<FileRow> FileRows { get; private set; } = Array.Empty<FileRow>();
    public bool FilesEmpty => FileRows.Count == 0;
    private string? _filePick;

    internal void FilesRefresh()
    {
        var rows = new List<FileRow>();
        // 「已加载到哪条通道」按泳道名对（应用时泳道名就是配方名）。
        // 库里可能有几条同名——一条泳道只认领一次，不然满屏都标着已加载
        var claimed = new HashSet<int>();
        foreach (var r in _ws.Library)
        {
            var on = Zones.FirstOrDefault(z => !claimed.Contains(z.Number)
                && _ws.LaneNames.TryGetValue(z.Number, out var n) && n == r.Name);
            if (on is not null) claimed.Add(on.Number);
            var live = on is { EngineRunning: true };
            rows.Add(new FileRow(r.Id, r.Name,
                on is null ? "—" : $"通道 {on.Index}",
                live ? "运行中" : on is not null ? "已加载" : "已保存",
                live, on is not null && !live,
                $"{r.Steps.Count} 步" + (r.Charge is null ? "" : " · 含配料表"),
                _filePick == r.Id));
        }
        FileRows = rows;
        RaiseAll(nameof(FileRows), nameof(FilesEmpty));
    }

    public void FileSelect(string id) { _filePick = id; FilesRefresh(); }

    /// <summary>→ 通道 N：把库里选中的方案应用到该通道（照工作站 DoApplyLib 那套）。</summary>
    public void FileApply(int zoneIndex)
    {
        var pick = _ws.Library.FirstOrDefault(r => r.Id == _filePick);
        if (pick is null) { Toast("先点选一条方案"); return; }
        var z = Zones.ElementAtOrDefault(zoneIndex - 1);
        if (z is null) return;
        if (z.EngineRunning) { Toast($"通道 {z.Index} 正在运行——运行中的通道不可接收方案"); return; }
        var copy = pick.CopyAs(pick.Name, pick.Author);
        var adopted = _ws.AdoptCharge(z.Number, copy);
        _ws.ChannelRecipes[z.Number] = copy;
        _ws.LaneNames[z.Number] = copy.Name;
        _ws.Store.MarkDirty();
        _ws.Log.Write("配方", $"HMI 面板把「{copy.Name}」应用到 CH{z.Number}", _ws.Operator);
        Toast($"已把「{copy.Name}」应用到通道 {z.Index}（{copy.Steps.Count} 步）"
              + (adopted is null ? "" : "，" + adopted));
        FilesRefresh();
    }

    public void FileDelete()
    {
        var pick = _ws.Library.FirstOrDefault(r => r.Id == _filePick);
        if (pick is null) { Toast("先点选一条方案"); return; }
        _ws.Library.Remove(pick);
        _ws.Store.SaveLibrary();
        _ws.Log.Write("配方", $"HMI 面板从配方库删除「{pick.Name}」", _ws.Operator);
        Toast($"已从配方库删除「{pick.Name}」");
        _filePick = null;
        FilesRefresh();
    }

    // ── 系统页（原型 pageSys：只摆真有的东西）─────────────────────────

    public sealed record SysRow(string K, string V, string D);

    public IReadOnlyList<SysRow> SysRows { get; private set; } = Array.Empty<SysRow>();

    internal void SysRefresh()
    {
        var rows = new List<SysRow>();
        var scale = _ws.Engine.TimeScale;
        rows.Add(new SysRow("网络", "本机模拟运行 · 未联网",
            "接真机走 RS-485 Modbus RTU（串口在台面属性栏配置）"));
        rows.Add(new SysRow("时间",
            $"{_ws.Clock.Now:yyyy-MM-dd HH:mm}" + (scale > 1 ? $" · 仿真时钟 ×{scale:0}" : ""),
            scale > 1 ? "演示模式下时钟加速；接真机为 1:1" : "本机时钟"));
        rows.Add(new SysRow("语言与键盘", "简体中文 · QWERTY", "当前版本仅中文界面"));
        try
        {
            var di = new System.IO.DriveInfo(System.IO.Path.GetPathRoot(ExperimentStore.DataDir)!);
            rows.Add(new SysRow("本机存储",
                $"剩余 {di.AvailableFreeSpace / 1024.0 / 1024 / 1024:0.0} GB",
                $"数据目录 {ExperimentStore.DataDir}"));
        }
        catch { rows.Add(new SysRow("本机存储", "—", ExperimentStore.DataDir)); }
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
    // GLP 记「谁在什么时候看见了它」。演练（模拟紧急程序）用同一张罩子，
    // 明晃晃标着「演练」，不进报警本，只进系统日志。

    private Tec.Core.Safety.Alarm? _alarm;
    private bool _drill;

    public bool AlarmOpen { get; private set; }
    public bool AlarmDrill => _drill;
    public string AlarmTitle { get; private set; } = "";
    public string AlarmSub { get; private set; } = "";
    public string AlarmHead2 { get; private set; } = "";
    public string AlarmBody { get; private set; } = "";
    public string AlarmDid { get; private set; } = "";
    public bool AlarmMulti { get; private set; }

    private void RefreshAlarm()
    {
        if (_drill) return;                       // 演练罩子由人关
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

    private void RaiseAlarm() => RaiseAll(nameof(AlarmOpen), nameof(AlarmDrill),
        nameof(AlarmTitle), nameof(AlarmSub), nameof(AlarmHead2), nameof(AlarmBody),
        nameof(AlarmDid), nameof(AlarmMulti));

    public void AlarmAck()
    {
        if (_drill) { _drill = false; AlarmOpen = false; RaiseAlarm(); Toast("演练结束"); return; }
        if (_alarm is { } a)
        {
            _ws.Engine.AckAlarm(a.Key, _ws.Operator, "面板复位");
            Toast(a.Standing ? "已确认（条件仍成立，继续监视）" : "已确认，报警翻篇");
        }
        RefreshAlarm();
    }

    public void AlarmAckAll()
    {
        if (_drill) { AlarmAck(); return; }
        var n = _ws.Engine.AckAllAlarms(_ws.Operator, "面板复位（全部）");
        Toast($"已确认 {n} 条报警");
        RefreshAlarm();
    }

    /// <summary>模拟紧急程序（演练）：只演示罩子和处置路径，不进报警本。</summary>
    public void AlarmDrillOpen(string kind)
    {
        _drill = true;
        var a = kind == "A";
        AlarmTitle = $"紧急程序 {kind}（演练）";
        AlarmSub = $"通道 {Cur?.Index ?? 1} · {_ws.Clock.Now:HH:mm:ss} · 演练不进报警本";
        AlarmHead2 = a ? "Tc 越限示例 —— 冷媒温度超过安全限值" : "Tr 越限示例 —— 釜内温度超过安全限值";
        AlarmBody = a
            ? "A 类多为硬件侧故障（冷却失效、传感器故障）。检查冷却介质流量与供水温度，等 Tc 回落后复位；其余 A 类原因须断电并联系服务。"
            : "该类故障多为应用层问题，可复位：安全层会按限值声明的动作处理（切加热 / 停泵 / 中止通道），等读数回落后复位。";
        AlarmDid = "本机的真限值与动作见「反应釜与安全」页的安全限值表。";
        AlarmMulti = false;
        AlarmOpen = true;
        _ws.Log.Write("安全", $"报警演练 紧急程序 {kind}（HMI 面板）", _ws.Operator);
        RaiseAlarm();
    }

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
            z.WantSaf = ShowZSaf && ReferenceEquals(z, Cur);
            z.WantTl = ShowOvSeq;
            z.Refresh();
        }
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
    private readonly Workspace _ws;

    public HmiZoneViewModel(HmiViewModel owner, Workspace ws, int index, int channelNumber)
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

    public bool HasPh => Ch?.Capabilities.All.OfType<IScalarSensor>()
        .Any(s => s.Tags.Any(t => t.Tag == "pH")) == true;

    internal bool HasPump => Dose is not null;

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
        public string Type { get; set; } = "Tr";   // Tr / Tj / Wait / R / Dose
        public double Tgt { get; set; } = 40;      // ℃ / rpm / mL（Wait 不用）
        public string Mode { get; set; } = "rate"; // 温度: rate|time；R: now|time；Dose: rate|once
        public double Val { get; set; } = 0.5;     // 速率，或分钟（Wait 的保持时长也在这）
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
        ExperimentStore.DataDir, "HmiPanel", $"seq-CH{Number}.json");

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
    { ["Tr"] = "Tr", ["Tj"] = "Tj", ["Wait"] = "Wait", ["R"] = "R", ["Dose"] = "加料" };

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
            if (s.Type != "R" && Math.Abs(s.Rpm - rpmPrev) > 0.5)
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
                    rows.Add(new HmiSeqRow("搅拌", $"{s.Rpm:0} rpm", "rpm"));
                    break;
                case "Wait":
                    rows.Add(new HmiSeqRow("保持温度", $"{F0(t0)} ℃"));
                    rows.Add(new HmiSeqRow("保持时长", $"{F0(s.Val)} min", "val"));
                    rows.Add(new HmiSeqRow("步时长", Fmt.Hms(dur)));
                    rows.Add(new HmiSeqRow("搅拌", $"{s.Rpm:0} rpm", "rpm"));
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
                    rows.Add(new HmiSeqRow("搅拌", $"{s.Rpm:0} rpm", "rpm"));
                    break;
            }

            var ty = SeqTypeNames.TryGetValue(s.Type, out var tn) ? tn : s.Type;
            cards.Add(new HmiSeqCard
            {
                No = i + 1, Type = ty, State = st, StateText = text,
                Rows = rows, T0 = t0, T1 = t1, Frac = frac,
            });
            btns.Add(new SeqStepBtn(i, $"步骤 {i + 1}", ty,
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
                ? "最多 6 步 · 可用操作 Tr / Tj / 保温 / 搅拌 / 加料 · 点下方空位添加"
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
        var stirRow = rows.Select((r, i) => Seg(i, r.Rpm)).ToList();

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

    /// <summary>类型定了（类型弹窗回调）。已有的换类型保留该步转速，其余回缺省。</summary>
    internal void SetSeqStep(int index, string type)
    {
        if (GuardSeqLocked()) return;
        var prevRpm = index > 0 && index - 1 < _seq.Count ? _seq[index - 1].Rpm
                    : _seq.Count > 0 ? _seq[^1].Rpm : Math.Max(200, Stir?.CurrentRpm ?? 200);
        var keep = index < _seq.Count ? _seq[index].Rpm : prevRpm;
        var s = type switch
        {
            "Tr" => new HmiSeqStep { Type = "Tr", Tgt = 40, Mode = "rate", Val = 0.5, Rpm = keep },
            "Tj" => new HmiSeqStep { Type = "Tj", Tgt = 40, Mode = "rate", Val = 0.5, Rpm = keep },
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

    /// <summary>从文件：面板序列存在设备本机，长流程配方归工作站——照实说。</summary>
    public void SeqFromFileHint()
        => _owner.Toast("面板序列存在设备本机（改一步存一步）；长流程配方由工作站编排执行");

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
            : $"搅拌桨 {dev.Config.Str("搅拌桨", "锚式")} · 探头 {dev.Config.Str("温度探头", "Pt100 四线")}"
              + " · 规格在台面属性栏更换";

        static string N(double v) => Txt.Fx(v).Replace('-', '−');
        var rows = new List<SafRow>();
        var b = BaseTr;
        var op = OpTr;
        if (b is not null)
        {
            var act = Tec.Core.Safety.SafetyActionWords.Of(b.Action);
            double effMin = op?.Min ?? b.Min ?? 0,
                   effMax = op?.Max ?? b.Max ?? 0,
                   effRate = op?.MaxRatePerMin ?? b.MaxRatePerMin ?? 0;
            rows.Add(new SafRow("Tr min", $"{N(effMin)} ℃",
                $"低于此值 → {act} · 底线 {N(b.Min ?? 0)} ℃"
                + (op?.Min is { } && op.Min > b.Min ? " · 已收紧" : ""), "trmin"));
            rows.Add(new SafRow("Tr max", $"{N(effMax)} ℃",
                $"高于此值 → {act} · 底线 {N(b.Max ?? 0)} ℃"
                + (op?.Max is { } && op.Max < b.Max ? " · 已收紧" : ""), "trmax"));
            rows.Add(new SafRow("变温速率上限", $"{N(effRate)} ℃/min",
                $"实测斜率越限 → {act} · 底线 {N(b.MaxRatePerMin ?? 0)} ℃/min"
                + (op?.MaxRatePerMin is { } && op.MaxRatePerMin < b.MaxRatePerMin ? " · 已收紧" : ""),
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
               curMax = op?.Max ?? b.Max ?? 0,
               curRate = op?.MaxRatePerMin ?? b.MaxRatePerMin ?? 0;
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
                _owner.OpenKeypadCustom("变温速率上限", "℃/min", 0.05, b.MaxRatePerMin ?? 99,
                    v => ApplyOpLimit(curMin, curMax, v));
                break;
        }
    }

    private void ApplyOpLimit(double min, double max, double rate)
    {
        var res = _ws.Engine.Safety.SetOperatorLimit(Number, "Tr", min, max, rate);
        static string N(double v) => Txt.Fx(v).Replace('-', '−');
        var msg = res is null ? "已回到设备底线"
            : $"Tr {N(res.Min ?? 0)}…{N(res.Max ?? 0)} ℃ · ≤{N(res.MaxRatePerMin ?? 0)} ℃/min";
        Log("改限值", msg);
        _ws.Log.Write("安全", $"CH{Number} 面板改限值：{msg}", _ws.Operator);
        _owner.Toast("安全限值已更新：" + msg);
        RefreshSaf();
        RaiseZone();     // 控制页底部的限值条也跟着换
    }

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
