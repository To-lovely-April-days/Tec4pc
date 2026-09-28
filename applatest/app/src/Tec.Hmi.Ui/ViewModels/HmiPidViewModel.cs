using System.Collections.ObjectModel;
using System.Globalization;
using Tec.Driver.Abi;

namespace Tec.Hmi.Ui.ViewModels;

/// <summary>增益表里的一行（界面上的编辑态：全是文本，应用时才解析）。</summary>
public sealed class HmiPidRow : ViewModelBase
{
    private readonly HmiPidViewModel _owner;
    private string _t = "", _kp = "", _ki = "", _kd = "", _okp = "", _oki = "", _okd = "", _bias = "", _heat = "", _steady = "";
    private bool _inUse;

    internal HmiPidRow(HmiPidViewModel owner, PidGainRow? src, bool heatEditable)
    {
        _owner = owner;
        HeatEditable = heatEditable;
        Source = src;
        if (src is null) return;
        _t = F(src.TemperatureC, "0.##");
        _kp = F(src.Kp, "0.####");
        _ki = F(src.Ki, "0.#######");
        _kd = F(src.Kd, "0.###");
        _okp = F(src.OuterKp, "0.###");
        _oki = F(src.OuterKi, "0.#######");
        _okd = F(src.OuterKd, "0.###");
        _bias = F(src.OuterMaxBiasC, "0.##");
        _heat = F(src.HeatRatio, "0.###");
        _steady = F(src.SteadyBiasC, "0.##");
    }

    /// <summary>从表里读出来的原行（带着自整定的 Ku/Tu 与学到的稳态偏置）；新增的行是 null。</summary>
    internal PidGainRow? Source { get; }

    public bool HeatEditable { get; }

    private static string F(double? v, string fmt) => v is { } x ? x.ToString(fmt, CultureInfo.InvariantCulture) : "";

    private string Edit(ref string field, string value, string name)
    {
        if (Set(ref field, value, name)) _owner.MarkDirty();
        return field;
    }

    public string T { get => _t; set => Edit(ref _t, value, nameof(T)); }
    public string Kp { get => _kp; set => Edit(ref _kp, value, nameof(Kp)); }
    public string Ki { get => _ki; set => Edit(ref _ki, value, nameof(Ki)); }
    public string Kd { get => _kd; set => Edit(ref _kd, value, nameof(Kd)); }
    public string OKp { get => _okp; set => Edit(ref _okp, value, nameof(OKp)); }
    public string OKi { get => _oki; set => Edit(ref _oki, value, nameof(OKi)); }
    public string OKd { get => _okd; set => Edit(ref _okd, value, nameof(OKd)); }
    public string Bias { get => _bias; set => Edit(ref _bias, value, nameof(Bias)); }
    public string Heat { get => _heat; set => Edit(ref _heat, value, nameof(Heat)); }
    /// <summary>
    /// 稳态偏置（夹套设定 − 釜内目标，℃）：串级下发时预置外环积分、两枪放电的参照。回路到温后自己学（±0.05 ℃ 里待满 60 s）；
    /// 0335 起也能手填——釜内稳住时看夹套比釜内高多少（现场 50 ℃ 是 5.5）。空 = 没有，回路只能从 0 攒。
    /// </summary>
    public string Steady { get => _steady; set => Edit(ref _steady, value, nameof(Steady)); }

    public string Tu => Source is { TuSeconds: > 0 } s ? s.TuSeconds.ToString("0", CultureInfo.InvariantCulture) : "—";
    public string Origin => Source is null ? "新增" : Source.FromAutoTune ? "自整定" : "手工";

    /// <summary>此刻回路插值用到的行（设定值两侧那两行 / 只有一行时就是它）。</summary>
    public bool InUse { get => _inUse; set => Set(ref _inUse, value); }

    public void Remove() => _owner.RemoveRow(this);

    /// <summary>解析成一行；不对就说哪一格。</summary>
    internal PidGainRow Parse(int line)
    {
        double Req(string s, string what)
        {
            if (double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v)) return v;
            throw new ArgumentException($"第 {line} 行「{what}」填的不是数：{(s.Length == 0 ? "（空）" : s)}");
        }
        double? Opt(string s, string what) => string.IsNullOrWhiteSpace(s) ? null : Req(s, what);
        var row = new PidGainRow(Req(T, "温度"), Req(Kp, "Kp"), Req(Ki, "Ki"), Req(Kd, "Kd"))
        {
            OuterKp = Opt(OKp, "外环 Kp"),
            OuterKi = Opt(OKi, "外环 Ki"),
            OuterKd = Opt(OKd, "外环 Kd"),
            OuterMaxBiasC = Opt(Bias, "偏置上限"),
            HeatRatio = HeatEditable ? Opt(Heat, "加热比") : null,
            SteadyBiasC = Opt(Steady, "稳态偏置")
        };
        if (row.SteadyBiasC is { } sb && Math.Abs(sb) > 60)
            throw new ArgumentException($"第 {line} 行「稳态偏置」{sb} 不像话（夹套比釜内高 / 低不会超过 60 ℃）");
        // 自整定测得的 Ku/Tu 原样带回去——温度没改的话它们还成立（稳态偏置按格子里填的走，空就是没有）
        if (Source is { } src && Math.Abs(src.TemperatureC - row.TemperatureC) < 1e-9)
            row = row with { Ku = src.Ku, TuSeconds = src.TuSeconds };
        return row;
    }
}

/// <summary>自整定建议点位（面板上一排小片）：点了把温度填进去；这一温度的表里已经有行就打勾。</summary>
public sealed record HmiPidPoint(double T, string Text, bool Done, PidActuator Actuator);

/// <summary>
/// 面板「PID 整定」页（每个通道一个）。背后是驱动的 IPidTuningBench：两张增益表（TEC / 加热棒）、
/// 手动参数、此刻在用的参数、每拍的 P/I/D、继电器法自整定。所有登录用户都能改（用户定的），
/// 每一笔改动记系统日志、署名操作人。
///
/// 驱动事件（自整定结束、回路改了表）可能在控制环的线程上来：这里只记旗，
/// 由面板 1 s 的拍子（Refresh，界面线程）落到界面上。
/// </summary>
public sealed class HmiPidViewModel : ViewModelBase
{
    private readonly HmiViewModel _owner;
    private readonly IHmiHost _ws;
    private readonly HmiZoneViewModel _zone;
    private IPidTuningBench? _hooked;
    private volatile bool _tablesChanged;
    private PidAutoTuneReport? _pendingReport;
    private readonly object _gate = new();

    internal HmiPidViewModel(HmiViewModel owner, IHmiHost ws, HmiZoneViewModel zone)
    {
        _owner = owner;
        _ws = ws;
        _zone = zone;
    }

    private IPidTuningBench? Bench => _zone.PidBench;

    private string Who => $"CH{_zone.Number}（工位 {(_zone.Index == 1 ? "A" : "B")}）";

    // ── 有没有这一页 ────────────────────────────────────────────────

    public bool HasBench => Bench is not null;
    public bool NoBench => Bench is null;

    public string NoBenchText => _zone.LinkOk
        ? _zone.HostLoopKnown == false
            ? "这台的「控温方式」是温控器 PID：PID 在温控器里，参数与自整定在温控器参数窗（顶栏机器图标）。" +
              "要用这一页，在台面属性栏把主机的「控温方式」改成「上位机 PID（串级）」，保存后重新连接。"
            : "这一路没有上位机 PID 的整定能力（驱动不提供）。"
        : "设备没连上，没有回路可调——先在台面上连接设备。";

    public string Heading => $"{Who} · 上位机 PID 整定";

    // ── 拍子 ───────────────────────────────────────────────────────

    /// <summary>页面在看时每秒调一次（界面线程）。</summary>
    internal void Refresh()
    {
        var b = Bench;
        if (!ReferenceEquals(b, _hooked))
        {
            if (_hooked is { } old) { old.TablesChanged -= OnTablesChanged; old.AutoTuneFinished -= OnTuneFinished; }
            _hooked = b;
            if (b is not null)
            {
                b.TablesChanged += OnTablesChanged;
                b.AutoTuneFinished += OnTuneFinished;
                // 进页先看此刻执行器那张表（正在用加热棒 / 正拿加热棒整定，就别先摆一张空的 TEC 表）
                _table = b.CurrentActuator;
                RaiseAll(nameof(TableTec), nameof(TableHeater), nameof(HeatColumn), nameof(HeatColumnTip));
                LoadRows();
                LoadManual();
                if (string.IsNullOrWhiteSpace(_tuneT)) TuneT = "25";
            }
            RaiseAll(nameof(HasBench), nameof(NoBench), nameof(NoBenchText));
        }
        RaiseAll(nameof(NoBenchText));
        if (b is null) return;

        if (_tablesChanged)
        {
            _tablesChanged = false;
            if (!TableDirty) LoadRows();
            else TableNote = "回路刚改了表（自整定登记 / 学到稳态偏置）；你这边有没应用的改动——点「放弃修改」就能看到最新的";
        }
        PidAutoTuneReport? rep;
        lock (_gate) { rep = _pendingReport; _pendingReport = null; }
        if (rep is not null) ShowReport(rep);

        MarkInUseRows(b);
        RefreshLive(b);
        RefreshTune(b);
    }

    private void OnTablesChanged() => _tablesChanged = true;

    private void OnTuneFinished(PidAutoTuneReport r)
    {
        lock (_gate) _pendingReport = r;
        _tablesChanged = true;
    }

    // ── 增益表 ─────────────────────────────────────────────────────

    private PidActuator _table = PidActuator.Tec;

    public bool TableTec => _table == PidActuator.Tec;
    public bool TableHeater => _table == PidActuator.Heater;
    /// <summary>「加热比」两张表都有（0336 起加热棒表那列给双向挡用：TEC 那半轴乘它）；列头提示按表说。</summary>
    public bool HeatColumn => true;
    public string HeatColumnTip => _table == PidActuator.Tec
        ? "加热 / 制冷有效度之比（TEC 双向、「TEC 加热」启用时用）：正半轴除以它。空 = 1"
        : "加热棒 / TEC 制冷有效度之比，给双向挡（冷水机已开）用：PID 按加热棒整，负半轴给 TEC 时乘它——加热棒比 TEC 强 2 倍就填 2。空 = 1（只加热不用）";

    public ObservableCollection<HmiPidRow> Rows { get; } = new();

    private bool _dirty;
    public bool TableDirty { get => _dirty; private set { if (Set(ref _dirty, value)) Raise(nameof(TableClean)); } }
    public bool TableClean => !_dirty;

    private string _tableNote = "";
    public string TableNote { get => _tableNote; private set => Set(ref _tableNote, value); }

    public string TablePath => Bench is { } b ? b.TablePath(_table) : "";

    public string TableSummary
    {
        get
        {
            if (Bench is not { } b) return "";
            var rows = b.Rows(_table);
            var name = _table == PidActuator.Heater ? "加热棒表" : "TEC 表";
            return rows.Count == 0
                ? $"{name}空着——这一路用{(_table == PidActuator.Heater ? "加热棒" : " TEC ")}时回路用手动参数。在常用温度点自整定一次就会自动建行"
                : $"{name}：{rows.Count} 个温度点，{rows[0].TemperatureC:0.#} ~ {rows[^1].TemperatureC:0.#} ℃ 之间线性插值、两头之外取最近那行";
        }
    }

    public string OtherWell => _zone.Index == 1 ? "B" : "A";

    internal void MarkDirty()
    {
        TableDirty = true;
        TableNote = "有改动还没应用——点「应用表格」写进回路并存盘";
    }

    private void LoadRows()
    {
        Rows.Clear();
        if (Bench is { } b)
            foreach (var r in b.Rows(_table))
                Rows.Add(new HmiPidRow(this, r, heatEditable: true));
        TableDirty = false;
        TableNote = "";
        RaiseAll(nameof(TablePath), nameof(TableSummary), nameof(TableEmpty));
        RefreshPoints();
    }

    public bool TableEmpty => Rows.Count == 0;

    public void SelectTable(string which)
    {
        var a = which == "heater" ? PidActuator.Heater : PidActuator.Tec;
        if (a == _table) return;
        void go()
        {
            _table = a;
            LoadRows();
            RaiseAll(nameof(TableTec), nameof(TableHeater), nameof(HeatColumn), nameof(HeatColumnTip));
        }
        if (TableDirty)
            _owner.OpenAsk("换表", "当前这张表有改动还没应用", "换到另一张表会丢掉这些改动。", "丢掉改动并换表", go);
        else go();
    }

    public void AddRow()
    {
        if (Bench is not { } b) return;
        // 新行的起点：比现有最高温度高 20 ℃（空表从 25 ℃ 起），参数按那个温度此刻会用的那组填
        var t = Rows.Count == 0 ? 25
            : Rows.Select(r => double.TryParse(r.T, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN)
                  .Where(double.IsFinite).DefaultIfEmpty(5).Max() + 20;
        var p = b.Preview(_table, t);
        var row = new HmiPidRow(this, null, heatEditable: true)
        {
            T = t.ToString("0.#", CultureInfo.InvariantCulture),
            Kp = p.Inner.Kp.ToString("0.####", CultureInfo.InvariantCulture),
            Ki = p.Inner.Ki.ToString("0.#######", CultureInfo.InvariantCulture),
            Kd = p.Inner.Kd.ToString("0.###", CultureInfo.InvariantCulture),
            OKp = p.Outer.Kp.ToString("0.###", CultureInfo.InvariantCulture),
            OKi = p.Outer.Ki.ToString("0.#######", CultureInfo.InvariantCulture),
            OKd = p.Outer.Kd.ToString("0.###", CultureInfo.InvariantCulture),
            Bias = p.OuterMaxBiasC.ToString("0.#", CultureInfo.InvariantCulture)
        };
        Rows.Add(row);
        MarkDirty();
        Raise(nameof(TableEmpty));
    }

    internal void RemoveRow(HmiPidRow row)
    {
        if (!Rows.Remove(row)) return;
        MarkDirty();
        Raise(nameof(TableEmpty));
    }

    public void ApplyTable()
    {
        if (Bench is not { } b) return;
        List<PidGainRow> rows;
        try { rows = Rows.Select((r, i) => r.Parse(i + 1)).ToList(); }
        catch (ArgumentException ex) { Fail(ex.Message); return; }
        try { b.ApplyRows(_table, rows); }
        catch (Exception ex) { Fail(ex.Message); return; }
        var name = _table == PidActuator.Heater ? "加热棒" : "TEC";
        Log($"{name}增益表应用（{b.Rows(_table).Count} 个温度点）：" +
            string.Join("；", b.Rows(_table).Select(r => $"{r.TemperatureC:0.#} ℃ Kp {r.Kp:0.###} Ki {r.Ki:0.#####} Kd {r.Kd:0.##}")));
        _owner.Toast($"{name}增益表已写进回路并存盘");
        Ok();
        LoadRows();
    }

    public void RevertTable()
    {
        LoadRows();
        _owner.Toast("改动已放弃，表是回路里此刻的样子");
    }

    public void ReloadDisk()
    {
        if (Bench is not { } b) return;
        void go()
        {
            try { b.Reload(); }
            catch (Exception ex) { Fail(ex.Message); return; }
            Log("两张增益表与手动参数从盘上重读");
            LoadRows();
            LoadManual();
            _owner.Toast("已从盘上重读两张表和手动参数");
        }
        if (TableDirty || ManualDirty)
            _owner.OpenAsk("从盘上重读", "有改动还没应用", "重读会丢掉这一页上还没应用的改动（表和手动参数）。", "重读", go);
        else go();
    }

    public void ClearTable()
    {
        if (Bench is not { } b) return;
        var name = _table == PidActuator.Heater ? "加热棒" : "TEC";
        _owner.OpenAsk($"清空{name}增益表", $"{Who} · {b.Rows(_table).Count} 个温度点",
            $"清空后这一路用{name}时回路改用手动参数，直到重新自整定或手工填表。文件里的内容一并清掉。",
            "清空", () =>
            {
                try { b.ApplyRows(_table, Array.Empty<PidGainRow>()); }
                catch (Exception ex) { Fail(ex.Message); return; }
                Log($"{name}增益表清空");
                LoadRows();
            });
    }

    public void CopyToOther()
    {
        if (Bench is not { } b) return;
        var other = _owner.Zones.FirstOrDefault(z => !ReferenceEquals(z, _zone))?.PidBench;
        var name = _table == PidActuator.Heater ? "加热棒" : "TEC";
        if (other is null) { Fail($"工位 {OtherWell} 没有上位机 PID 整定台（没连上 / 温控器 PID 方式）"); return; }
        if (TableDirty) { Fail("这张表有改动还没应用——先应用或放弃，再复制（复制的是回路里此刻的表）"); return; }
        var rows = b.Rows(_table);
        _owner.OpenAsk($"复制{name}增益表到工位 {OtherWell}", $"{rows.Count} 个温度点",
            $"工位 {OtherWell} 的{name}表会被整张替换成这一张（两台硬件一样时省一遍自整定）。" +
            "学到的稳态偏置随表带过去，那一路自己跑起来会重新学。",
            "替换", () =>
            {
                try { other.ApplyRows(_table, rows); }
                catch (Exception ex) { Fail(ex.Message); return; }
                Log($"{name}增益表复制到工位 {OtherWell}（{rows.Count} 个温度点）");
                _owner.Toast($"已复制到工位 {OtherWell}");
            });
    }

    /// <summary>设定值两侧那两行（插值用到的）高亮；表是当前执行器那张才亮。</summary>
    private void MarkInUseRows(IPidTuningBench b)
    {
        var use = b.InUse;
        var live = b.Live;
        var on = b.Scheduling && use.InnerFromTable && use.Actuator == _table && live is { Active: true } or { Tuning: true };
        var temps = Rows.Select(r => double.TryParse(r.T, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN).ToArray();
        var finite = temps.Where(double.IsFinite).OrderBy(x => x).ToArray();
        double lo = double.NaN, hi = double.NaN;
        if (on && finite.Length > 0)
        {
            var t = use.AtC;
            if (t <= finite[0]) lo = hi = finite[0];
            else if (t >= finite[^1]) lo = hi = finite[^1];
            else
                for (var i = 1; i < finite.Length; i++)
                    if (t <= finite[i]) { lo = finite[i - 1]; hi = finite[i]; break; }
        }
        for (var i = 0; i < Rows.Count; i++)
            Rows[i].InUse = on && (temps[i] == lo || temps[i] == hi);
    }

    // ── 在用参数 / 调度开关 / 查看某温度 ─────────────────────────────

    public string UseHead { get; private set; } = "";
    public string UseInner { get; private set; } = "";
    public string UseOuter { get; private set; } = "";
    public string UseExtra { get; private set; } = "";
    public bool Scheduling => Bench?.Scheduling == true;

    public void ToggleScheduling()
    {
        if (Bench is not { } b) return;
        var on = !b.Scheduling;
        try { b.SetScheduling(on); }
        catch (Exception ex) { Fail(ex.Message); return; }
        Log($"增益调度{(on ? "打开" : "关闭")}");
        _owner.Toast(on ? "增益调度开：按设定值在表里插值" : "增益调度关：固定用手动参数");
        Ok();
        Raise(nameof(Scheduling));
    }

    private string _previewT = "";
    public string PreviewT { get => _previewT; set { if (Set(ref _previewT, value)) UpdatePreview(); } }
    public string PreviewText { get; private set; } = "";

    private void UpdatePreview()
    {
        PreviewText = "";
        if (Bench is { } b && double.TryParse(_previewT.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var t) && double.IsFinite(t))
        {
            var p = b.Preview(_table, t);
            PreviewText = $"{(_table == PidActuator.Heater ? "加热棒" : "TEC")} {t:0.#} ℃ → 内环 {Fmt(p.Inner)}（{(p.InnerFromTable ? "表" : "手动")}）；" +
                          $"外环 {Fmt(p.Outer)} ±{p.OuterMaxBiasC:0.#} ℃（{(p.OuterFromTable ? "表" : "手动")}）";
        }
        Raise(nameof(PreviewText));
    }

    // ── 实时与稳定度 ───────────────────────────────────────────────

    public string LiveHead { get; private set; } = "";
    public string LivePid { get; private set; } = "";
    public string StabLine1 { get; private set; } = "";
    public string StabLine2 { get; private set; } = "";
    public bool StabGood { get; private set; }

    /// <summary>稳定度统计窗（min）与「已稳定」判带（℃）：照 TecControl.App 的 5 min / ±0.05。</summary>
    private const double StabWindowMin = 5, StableBand = 0.05, FineBand = 0.01;

    private void RefreshLive(IPidTuningBench b)
    {
        var use = b.InUse;
        var live = b.Live;
        // 双向挡（0336）：执行器算加热棒（参数按加热棒表），负半轴也出力（TEC）
        var act = use.Actuator == PidActuator.Heater
            ? (use.Bidirectional ? "加热棒 + TEC（双向挡，参数按加热棒表）" : "加热棒（只加热）")
            : "TEC";
        UseHead = live is { Tuning: true } ? $"执行器 {act} · 正在自整定"
            : live is { Active: true } l
                ? $"执行器 {act} · 设定 {F2(l.SetpointC)} ℃" + (l.Cascade ? $" · 内环设定 {F2(l.InnerSetpointC)} ℃（串级）" : "（单环）")
                : $"执行器 {act} · 没在控温（下面是按上次设定 {F2(use.AtC)} ℃ 预估的）";
        UseInner = $"内环 {Fmt(use.Inner)}　{(use.InnerFromTable ? "← 增益表插值" : "← 手动参数")}";
        UseOuter = $"外环 {Fmt(use.Outer)}　偏置 ±{use.OuterMaxBiasC:0.#} ℃　{(use.OuterFromTable ? "← 增益表插值" : "← 手动参数")}";
        UseExtra = (use.SteadyBiasC is { } sb ? $"稳态偏置 {sb:+0.00;−0.00} ℃（串级启动时预置）　" : "") +
                   (use.Actuator == PidActuator.Tec ? $"加热比 {use.HeatRatio:0.##}"
                    : use.Bidirectional ? $"加热比 {use.HeatRatio:0.##}（TEC 那半轴乘它）"
                    : $"加热比 {use.HeatRatio:0.##}（只加热不归一；双向挡里给 TEC 那半轴用）");

        if (live is null)
        {
            LiveHead = "回路还没转起来（设备没连上或刚连上）";
            LivePid = StabLine1 = StabLine2 = "";
            StabGood = false;
        }
        else
        {
            LiveHead = live.Tuning
                ? $"自整定输出 {live.DutyPercent:+0.0;−0.0;0.0} %　夹套 {F2(live.MeasuredC)} ℃"
                : live.Active
                    ? $"输出 {live.DutyPercent:+0.0;−0.0;0.0} %　{(live.Cascade ? $"釜内 {F2(live.OuterMeasuredC)} ℃ · 夹套 {F2(live.MeasuredC)} ℃" : $"夹套 {F2(live.MeasuredC)} ℃")}"
                    : "没在控温";
            LivePid = live.Active ? $"P {live.P:0.00}　I {live.I:0.00}　D {live.D:0.00}（内环三项，%）" : "";
            ComputeStability(live);
        }
        RaiseAll(nameof(UseHead), nameof(UseInner), nameof(UseOuter), nameof(UseExtra), nameof(Scheduling),
                 nameof(LiveHead), nameof(LivePid), nameof(StabLine1), nameof(StabLine2), nameof(StabGood));
    }

    /// <summary>
    /// 被控量最近 5 min 的每个样点（串级看釜内 Tr，单环看夹套 Tj）对主设定：最大偏差、峰峰、σ、
    /// 进 ±0.01 的比例、连着在 ±0.05 以内多久了。数据就是管线里的那份，不另攒。
    /// </summary>
    private void ComputeStability(PidLive live)
    {
        StabGood = false;
        if (!live.Active) { StabLine1 = StabLine2 = ""; return; }
        var tag = live.Cascade ? "Tr" : "Tj";
        var sp = live.SetpointC;
        var now = _ws.Clock.Now;
        var pts = _ws.Pipeline.Snapshot(_zone.Number, tag)
            .Where(s => now - s.WallClock <= TimeSpan.FromMinutes(StabWindowMin) && double.IsFinite(s.Value)).ToArray();
        if (pts.Length < 5) { StabLine1 = $"稳定度：{(live.Cascade ? "釜内 Tr" : "夹套 Tj")} 数据还不够"; StabLine2 = ""; return; }
        var dev = pts.Select(p => p.Value - sp).ToArray();
        var maxAbs = dev.Max(Math.Abs);
        var pp = pts.Max(p => p.Value) - pts.Min(p => p.Value);
        var mean = dev.Average();
        var sd = Math.Sqrt(dev.Sum(d => (d - mean) * (d - mean)) / Math.Max(1, dev.Length - 1));
        var fine = dev.Count(d => Math.Abs(d) <= FineBand) * 100.0 / dev.Length;
        var span = (pts[^1].WallClock - pts[0].WallClock).TotalMinutes;
        // 连着在 ±0.05 以内多久（从最新往回数）
        var stableSince = pts[^1].WallClock;
        for (var i = pts.Length - 1; i >= 0 && Math.Abs(dev[i]) <= StableBand; i--) stableSince = pts[i].WallClock;
        var stableMin = Math.Abs(dev[^1]) <= StableBand ? (pts[^1].WallClock - stableSince).TotalMinutes : 0;
        StabLine1 = $"{(live.Cascade ? "釜内 Tr" : "夹套 Tj")} 最近 {span:0.#} min：距设定最大 {maxAbs:0.000} ℃ · 峰峰 {pp:0.000} ℃ · σ {sd:0.0000} ℃ · 平均偏 {mean:+0.000;−0.000;0.000} ℃";
        StabLine2 = $"±{FineBand} ℃ 以内 {fine:0} % 的样点 · " +
                    (stableMin > 0 ? $"已连着 {stableMin:0.#} min 在 ±{StableBand} ℃ 以内" : $"此刻不在 ±{StableBand} ℃ 以内");
        StabGood = maxAbs <= FineBand && span >= StabWindowMin - 0.5;
    }

    // ── 手动参数 ───────────────────────────────────────────────────

    private string _mkp = "", _mki = "", _mkd = "", _mokp = "", _moki = "", _mokd = "", _mbias = "";
    private bool _mdirty;

    private void ME(ref string f, string v, string n) { if (Set(ref f, v, n)) { _mdirty = true; Raise(nameof(ManualDirty)); } }

    public string MKp { get => _mkp; set => ME(ref _mkp, value, nameof(MKp)); }
    public string MKi { get => _mki; set => ME(ref _mki, value, nameof(MKi)); }
    public string MKd { get => _mkd; set => ME(ref _mkd, value, nameof(MKd)); }
    public string MOKp { get => _mokp; set => ME(ref _mokp, value, nameof(MOKp)); }
    public string MOKi { get => _moki; set => ME(ref _moki, value, nameof(MOKi)); }
    public string MOKd { get => _mokd; set => ME(ref _mokd, value, nameof(MOKd)); }
    public string MBias { get => _mbias; set => ME(ref _mbias, value, nameof(MBias)); }
    public bool ManualDirty => _mdirty;

    private void LoadManual()
    {
        if (Bench is not { } b) return;
        var m = b.Manual;
        _mkp = m.Inner.Kp.ToString("0.####", CultureInfo.InvariantCulture);
        _mki = m.Inner.Ki.ToString("0.#######", CultureInfo.InvariantCulture);
        _mkd = m.Inner.Kd.ToString("0.###", CultureInfo.InvariantCulture);
        _mokp = m.Outer.Kp.ToString("0.###", CultureInfo.InvariantCulture);
        _moki = m.Outer.Ki.ToString("0.#######", CultureInfo.InvariantCulture);
        _mokd = m.Outer.Kd.ToString("0.###", CultureInfo.InvariantCulture);
        _mbias = m.OuterMaxBiasC.ToString("0.##", CultureInfo.InvariantCulture);
        _mdirty = false;
        RaiseAll(nameof(MKp), nameof(MKi), nameof(MKd), nameof(MOKp), nameof(MOKi), nameof(MOKd), nameof(MBias), nameof(ManualDirty));
    }

    public void ApplyManual()
    {
        if (Bench is not { } b) return;
        double N(string s, string what)
        {
            if (double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v)) return v;
            throw new ArgumentException($"手动参数「{what}」填的不是数：{s}");
        }
        PidManual m;
        try
        {
            m = new PidManual(new PidTuning(N(MKp, "内环 Kp"), N(MKi, "内环 Ki"), N(MKd, "内环 Kd")),
                              new PidTuning(N(MOKp, "外环 Kp"), N(MOKi, "外环 Ki"), N(MOKd, "外环 Kd")),
                              N(MBias, "偏置上限"));
            b.SetManual(m);
        }
        catch (Exception ex) { Fail(ex.Message); return; }
        Log($"手动 PID 参数改为 内环 {Fmt(m.Inner)}；外环 {Fmt(m.Outer)} ±{m.OuterMaxBiasC:0.#} ℃");
        _owner.Toast("手动参数已写进回路并存盘");
        Ok();
        LoadManual();
    }

    public void RevertManual() => LoadManual();

    // ── 自整定 ─────────────────────────────────────────────────────

    private string _tuneT = "", _tuneAmp = "20", _tuneHys = "0.05";
    private PidActuator _tuneAct = PidActuator.Tec;
    private bool _tuneActPicked;

    public string TuneT { get => _tuneT; set { if (Set(ref _tuneT, value)) { _tuneActPicked = false; RefreshTune(Bench); } } }
    public string TuneAmp { get => _tuneAmp; set { if (Set(ref _tuneAmp, value)) RefreshTune(Bench); } }
    public string TuneHys { get => _tuneHys; set { if (Set(ref _tuneHys, value)) RefreshTune(Bench); } }
    public bool TuneTec => _tuneAct == PidActuator.Tec;
    public bool TuneHeater => _tuneAct == PidActuator.Heater;

    public bool Tuning => Bench?.Tuning == true;
    public bool NotTuning => !Tuning;
    public string TuneNote { get; private set; } = "";
    public string TuneCheck { get; private set; } = "";
    public string TuneSuggest { get; private set; } = "";
    public bool CanStartTune { get; private set; }

    public IReadOnlyList<HmiPidPoint> Points { get; private set; } = Array.Empty<HmiPidPoint>();

    /// <summary>
    /// 建议点位（用户定的）：−20 / 0 / 25 / 50 / 80 / 110 / 140 ℃。用哪个执行器整按设备的热源策略给（0328）：
    /// 「TEC 加热」启用时阈值以下是 TEC、以上是加热棒；不启用时 TEC 只制冷，比夹套此刻高的点（放着不管时夹套
    /// 停在冷却水 / 室温附近，所以一般是 25 ℃ 以上）用加热棒——只制冷的 TEC 整不了比自然平衡温度高的点。
    /// </summary>
    private static readonly double[] Suggested = { -20, 0, 25, 50, 80, 110, 140 };

    private void RefreshPoints()
    {
        if (Bench is not { } b) { Points = Array.Empty<HmiPidPoint>(); Raise(nameof(Points)); return; }
        var tec = b.Rows(PidActuator.Tec);
        var heat = b.Rows(PidActuator.Heater);
        Points = Suggested.Select(t =>
        {
            var a = b.SuggestActuator(t);
            var rows = a == PidActuator.Heater ? heat : tec;
            var done = rows.Any(r => Math.Abs(r.TemperatureC - t) <= 2 && r.FromAutoTune);
            return new HmiPidPoint(t, $"{(done ? "✓ " : "")}{t:0} ℃ {(a == PidActuator.Heater ? "加热棒" : "TEC")}", done, a);
        }).ToList();
        Raise(nameof(Points));
    }

    public void PickPoint(HmiPidPoint p)
    {
        TuneT = p.T.ToString("0.#", CultureInfo.InvariantCulture);
        _tuneAct = p.Actuator;
        _tuneActPicked = true;
        RefreshTune(Bench);
    }

    public void PickActuator(string which)
    {
        _tuneAct = which == "heater" ? PidActuator.Heater : PidActuator.Tec;
        _tuneActPicked = true;
        RefreshTune(Bench);
    }

    private PidAutoTuneRequest? Request(out string? error)
    {
        error = null;
        bool P(string s, out double v) => double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && double.IsFinite(v);
        if (!P(_tuneT, out var t)) { error = "整定温度填的不是数"; return null; }
        if (!P(_tuneAmp, out var amp)) { error = "继电幅值填的不是数"; return null; }
        if (!P(_tuneHys, out var hys)) { error = "回差填的不是数"; return null; }
        return new PidAutoTuneRequest(t, _tuneAct, amp, hys);
    }

    private void RefreshTune(IPidTuningBench? b)
    {
        if (b is null) return;
        // 没手选过执行器就按设备的热源策略给建议
        if (!_tuneActPicked && double.TryParse(_tuneT.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var t0))
            _tuneAct = b.SuggestActuator(t0);
        var req = Request(out var err);
        var check = err ?? (req is null ? null : b.CheckTune(req));
        TuneCheck = check ?? "";
        CanStartTune = !b.Tuning && check is null;
        TuneSuggest = req is null ? "" : _tuneAct == PidActuator.Heater
            ? "加热棒只加热：继电在 0 ~ 2×幅值 之间摆（幅值最多 LIMITED 的一半，一般 15 ~ 25 %；离室温近、维持它用不了多少功率的" +
              "温度取小些，5 ~ 10 %），关着的那半周靠自然散热降温——整定温度比不加热时夹套会停的温度高得越多越快，太近了整不出振荡"
            : b.SuggestActuator(req.SetpointC) == PidActuator.Heater
                ? "这个温度平时是加热棒在出力（「TEC 加热」没启用时 TEC 只制冷）；用 TEC 整也行，但表登记在 TEC 那张"
                : "TEC：继电在设定值两边推；「TEC 加热」没启用时只在制冷一侧摆（强制冷 / 弱制冷，幅值最多 LIMITED 的一半），" +
                  "弱的那半周靠自然回温——只能整比不制冷时夹套会停的温度（冷却水 / 室温附近）低不少的温度";
        TuneNote = b.Tuning ? b.TuneNote : "";
        RaiseAll(nameof(TuneTec), nameof(TuneHeater), nameof(Tuning), nameof(NotTuning), nameof(TuneNote),
                 nameof(TuneCheck), nameof(TuneSuggest), nameof(CanStartTune));
    }

    public void StartTune()
    {
        if (Bench is not { } b) return;
        var req = Request(out var err);
        if (req is null) { Fail(err!); return; }
        if (b.CheckTune(req) is { } why) { Fail(why); return; }
        var act = req.Actuator == PidActuator.Heater ? "加热棒" : "TEC";
        var running = _zone.TempOn || _zone.EngineRunning;
        if (_zone.EngineRunning) { Fail("这一路在跑程序（配方 / 面板序列）——先结束程序再整定"); return; }
        _owner.OpenAsk("开始自整定", $"{Who} · {act} · {req.SetpointC:0.#} ℃",
            $"继电器法：输出在设定值两边来回推（幅值 {req.RelayAmplitudePercent:0} %、回差 {req.HysteresisC:0.###} ℃），" +
            "温度会在设定值附近振荡几个周期。要有人在场。" +
            (req.Actuator == PidActuator.Heater
                ? "\n加热棒只加热：关着的那半周靠自然散热降温，一个周期可能十几二十分钟，整完可能要一两个小时（最长 4 h 自动放弃）。"
                : "\nTEC 双向一般 10 ~ 60 min；只制冷时回温那半周靠自然回温，会慢一些（最长 4 h 自动放弃）。") +
            (running ? "\n这一路正在控温——开始整定会先把控温停掉。" : "") +
            $"\n整完输出关掉、继电器断开；成功时平稳型参数自动登记进{act}那张表。",
            "开始整定", () => _ = StartTuneAsync(b, req));
    }

    private async Task StartTuneAsync(IPidTuningBench b, PidAutoTuneRequest req)
    {
        CanStartTune = false;
        Raise(nameof(CanStartTune));
        try
        {
            await b.StartAutoTuneAsync(req, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => Fail("自整定没开起来：" + ex.Message));
            return;
        }
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _zone.NoteTempStoppedForTuning();
            Log($"开始自整定：{(req.Actuator == PidActuator.Heater ? "加热棒" : "TEC")} {req.SetpointC:0.#} ℃，" +
                $"幅值 {req.RelayAmplitudePercent:0} %、回差 {req.HysteresisC:0.###} ℃");
            ResultText = "";
            ResultTitle = "";
            RaiseAll(nameof(ResultText), nameof(ResultTitle), nameof(HasResult));
            RefreshTune(b);
        });
    }

    public void CancelTune()
    {
        if (Bench is not { } b || !b.Tuning) return;
        _ = Task.Run(async () =>
        {
            try { await b.CancelAutoTuneAsync(CancellationToken.None); }
            catch (Exception ex) { Avalonia.Threading.Dispatcher.UIThread.Post(() => Fail("取消没成：" + ex.Message)); }
        });
    }

    public string ResultTitle { get; private set; } = "";
    public string ResultText { get; private set; } = "";
    public bool HasResult => ResultTitle.Length > 0;
    public bool ResultOk { get; private set; }

    private void ShowReport(PidAutoTuneReport r)
    {
        var act = r.Actuator == PidActuator.Heater ? "加热棒" : "TEC";
        ResultOk = r.Success;
        if (r.Success)
        {
            ResultTitle = $"自整定完成 · {act} {r.SetpointC:0.#} ℃";
            ResultText = $"Ku {r.Ku:0.###}　Tu {r.TuSeconds:0.#} s　振幅 {r.OscillationC:0.###} ℃　继电幅值 {r.RelayAmplitudePercent:0} %\n" +
                         $"平稳型（已登记进{act}表）：{(r.Conservative is { } c ? Fmt(c) : "—")}\n" +
                         $"快速型（可能超调，只供参考）：{(r.Fast is { } f ? Fmt(f) : "—")}";
            Log($"自整定完成：{act} {r.SetpointC:0.#} ℃，Ku {r.Ku:0.###}、Tu {r.TuSeconds:0.#} s，平稳型 {(r.Conservative is { } c2 ? Fmt(c2) : "—")} 已登记");
            _owner.Toast($"自整定完成，{act} {r.SetpointC:0.#} ℃ 已登记进表");
        }
        else
        {
            ResultTitle = $"自整定没成 · {act} {r.SetpointC:0.#} ℃";
            ResultText = r.Reason ?? "";
            Log($"自整定没成：{act} {r.SetpointC:0.#} ℃，{r.Reason}");
        }
        RaiseAll(nameof(ResultTitle), nameof(ResultText), nameof(HasResult), nameof(ResultOk));
        RefreshPoints();
    }

    // ── 小工具 ─────────────────────────────────────────────────────

    /// <summary>最近一次没做成的事（页底一行，下一次做成了就清掉）——toast 两秒就没了，长句子看不完。</summary>
    private string _error = "";
    public string ErrorText { get => _error; private set { if (Set(ref _error, value)) Raise(nameof(HasError)); } }
    public bool HasError => _error.Length > 0;

    private void Fail(string msg)
    {
        _owner.Toast(msg);
        ErrorText = msg;
    }

    private void Ok() => ErrorText = "";

    private void Log(string text) => _ws.Log.Write("PID", $"{Who} {text}", _ws.Operator);

    private static string F2(double v) => double.IsFinite(v) ? v.ToString("0.00", CultureInfo.InvariantCulture) : "—";

    internal static string Fmt(PidTuning g)
        => $"Kp {g.Kp.ToString("0.###", CultureInfo.InvariantCulture)} · Ki {g.Ki.ToString("0.######", CultureInfo.InvariantCulture)} · Kd {g.Kd.ToString("0.##", CultureInfo.InvariantCulture)}";
}
