using System.Collections.ObjectModel;
using Tec.App.Services;
using Tec.Core;
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
    public bool ShowZCtl => IsZone && ZCtl;
    public bool ShowStub => (IsOv && !OvApp) || (IsZone && !ZCtl) || IsFiles || IsSys;

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
        => RaiseAll(nameof(Page), nameof(OvTab), nameof(ZTab), nameof(IsOv), nameof(IsZone),
                    nameof(IsFiles), nameof(IsSys), nameof(OnZ0), nameof(OnZ1), nameof(Cur),
                    nameof(OvApp), nameof(OvSeq), nameof(OvGra), nameof(ZCtl), nameof(ZOther),
                    nameof(ShowOvApp), nameof(ShowZCtl), nameof(ShowStub));

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
        else
        {
            var kind = _actKind switch { "sample" => "取样", "marker" => "标记", _ => "备注" };
            z.Note(kind, text);
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
        foreach (var z in Zones) z.Refresh();
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
